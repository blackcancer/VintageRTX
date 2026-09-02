using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace VintageRTX.Rendering;

/// <summary>
/// Captures Vintage Story's official opaque terrain/entity render systems and replays those exact
/// geometry passes into the owned mirror framebuffer. Reusing the engine renderers supplies static
/// scenery outside the primary screen as well as animated and modded entity geometry.
/// </summary>
internal static class EntityMirrorGeometryReplayPatch
{
    /// <summary>Harmony ownership key used for surgical teardown.</summary>
    private const string HarmonyId = "vintagertx.entity-mirror-geometry";

    /// <summary>Official 1.22 client system that enumerates every active entity renderer.</summary>
    private const string RenderSystemTypeName = "Vintagestory.Client.NoObf.SystemRenderEntities";

    /// <summary>Official 1.22 client system that owns opaque terrain chunk submission.</summary>
    private const string TerrainRenderSystemTypeName = "Vintagestory.Client.NoObf.SystemRenderTerrain";

    private static readonly double[] SavedCameraMatrix = new double[16];
    private static readonly float[] SavedCameraMatrixFloat = new float[16];
    private static readonly float[] MirroredCameraMatrixFloat = new float[16];
    private static readonly int[] SavedViewport = new int[4];

    private static Harmony? harmony;
    private static ICoreClientAPI? api;
    private static MethodInfo? opaqueEntityPass;
    private static MethodInfo? terrainBeforePass;
    private static MethodInfo? opaqueTerrainPass;
    private static object? currentRenderSystem;
    private static object? currentTerrainRenderSystem;
    private static float currentDeltaTime;
    private static float currentTerrainDeltaTime;
    private static bool replayInProgress;
    private static bool failureLogged;

    /// <summary>Installs postfixes that remember the official terrain/entity render systems.</summary>
    /// <param name="clientApi">Client renderer whose camera arrays are temporarily reflected.</param>
    /// <returns>Whether both exact 1.22 opaque world passes were found and patched.</returns>
    internal static bool Install(ICoreClientAPI clientApi)
    {
        ArgumentNullException.ThrowIfNull(clientApi);
        Uninstall();
        Type? renderSystemType = AccessTools.TypeByName(RenderSystemTypeName);
        Type? terrainRenderSystemType = AccessTools.TypeByName(TerrainRenderSystemTypeName);
        opaqueEntityPass = renderSystemType is null
            ? null
            : AccessTools.Method(renderSystemType, "OnRenderOpaque3D", [typeof(float)]);
        terrainBeforePass = terrainRenderSystemType is null
            ? null
            : AccessTools.Method(terrainRenderSystemType, "OnRenderBefore", [typeof(float)]);
        opaqueTerrainPass = terrainRenderSystemType is null
            ? null
            : AccessTools.Method(terrainRenderSystemType, "OnRenderOpaque", [typeof(float)]);
        if (opaqueEntityPass is null || terrainBeforePass is null || opaqueTerrainPass is null)
        {
            clientApi.Logger.Warning(
                "[VintageRTX] Official opaque world replay is unavailable for this game build; "
                + "the conservative G-buffer mirror remains active.");
            return false;
        }

        api = clientApi;
        harmony = new Harmony(HarmonyId);
        try
        {
            harmony.Patch(
                opaqueEntityPass,
                postfix: new HarmonyMethod(
                    typeof(EntityMirrorGeometryReplayPatch),
                    nameof(AfterOpaqueEntityPass)));
            harmony.Patch(
                opaqueTerrainPass,
                postfix: new HarmonyMethod(
                    typeof(EntityMirrorGeometryReplayPatch),
                    nameof(AfterOpaqueTerrainPass)));
        }
        catch (Exception exception)
        {
            clientApi.Logger.Error(
                "[VintageRTX] Official opaque world replay hook was rejected; "
                + "the conservative G-buffer entity mirror remains active: {0}",
                exception);
            Uninstall();
            return false;
        }

        clientApi.Logger.Notification(
            "[VintageRTX] Official opaque terrain/entity geometry replay installed for mirrored views.");
        return true;
    }

    /// <summary>Remembers the official system instance and frame duration after the primary pass.</summary>
    /// <param name="__instance">Current internal entity-render system.</param>
    /// <param name="deltaTime">Current engine render-frame duration.</param>
    private static void AfterOpaqueEntityPass(object __instance, float deltaTime)
    {
        if (replayInProgress)
        {
            return;
        }

        currentRenderSystem = __instance;
        currentDeltaTime = deltaTime;
    }

    /// <summary>Remembers the official terrain system after its primary opaque submission.</summary>
    /// <param name="__instance">Current internal terrain-render system.</param>
    /// <param name="deltaTime">Current engine render-frame duration.</param>
    private static void AfterOpaqueTerrainPass(object __instance, float deltaTime)
    {
        if (replayInProgress)
        {
            return;
        }

        currentTerrainRenderSystem = __instance;
        currentTerrainDeltaTime = deltaTime;
    }

    /// <summary>
    /// Re-culls and replays opaque terrain, then every opaque entity, with the supplied reflected
    /// view matrix. The caller owns the bound framebuffer, viewport, and cleared depth target.
    /// </summary>
    /// <param name="mirroredCameraMatrix">Column-major reflected floating-origin camera matrix.</param>
    /// <returns>True only when the official geometry pass completed successfully.</returns>
    internal static bool TryReplay(double[] mirroredCameraMatrix) =>
        TryReplayCore(mirroredCameraMatrix, includeTerrain: true);

    /// <summary>Replays only official opaque entities into an isolated evidence framebuffer.</summary>
    /// <param name="mirroredCameraMatrix">Column-major reflected floating-origin camera matrix.</param>
    /// <returns>True only when the official entity pass completed successfully.</returns>
    internal static bool TryReplayEntitiesOnly(double[] mirroredCameraMatrix) =>
        TryReplayCore(mirroredCameraMatrix, includeTerrain: false);

    /// <summary>Executes a reflected official replay with optional terrain submission.</summary>
    /// <param name="mirroredCameraMatrix">Column-major reflected floating-origin camera matrix.</param>
    /// <param name="includeTerrain">Whether to cull and draw opaque terrain before entities.</param>
    /// <returns>True when every requested official pass completed.</returns>
    private static bool TryReplayCore(double[] mirroredCameraMatrix, bool includeTerrain)
    {
        ICoreClientAPI? clientApi = api;
        MethodInfo? pass = opaqueEntityPass;
        MethodInfo? beforeTerrain = terrainBeforePass;
        MethodInfo? terrainPass = opaqueTerrainPass;
        object? renderSystem = currentRenderSystem;
        object? terrainRenderSystem = currentTerrainRenderSystem;
        if (clientApi is null
            || pass is null
            || renderSystem is null
            || (includeTerrain
                && (beforeTerrain is null
                    || terrainPass is null
                    || terrainRenderSystem is null))
            || mirroredCameraMatrix.Length < 16)
        {
            return false;
        }

        double[] cameraMatrix = clientApi.Render.CameraMatrixOrigin;
        float[] cameraMatrixFloat = clientApi.Render.CameraMatrixOriginf;
        if (cameraMatrix.Length < 16 || cameraMatrixFloat.Length < 16)
        {
            return false;
        }

        Array.Copy(cameraMatrix, SavedCameraMatrix, 16);
        Array.Copy(cameraMatrixFloat, SavedCameraMatrixFloat, 16);
        for (int index = 0; index < 16; index++)
        {
            cameraMatrix[index] = mirroredCameraMatrix[index];
            MirroredCameraMatrixFloat[index] = (float)mirroredCameraMatrix[index];
            cameraMatrixFloat[index] = MirroredCameraMatrixFloat[index];
        }

        replayInProgress = true;
        FirstPersonReflectionCapturePatch.BeginEntityMirrorReplay();
        GL.GetInteger(GetPName.FrontFace, out int savedFrontFace);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int mirrorFramebuffer);
        GL.GetInteger(GetPName.Viewport, SavedViewport);
        FrontFaceDirection reflectedFrontFace = ReflectedFrontFace(
            (FrontFaceDirection)savedFrontFace);
        GL.FrontFace(reflectedFrontFace);
        clientApi.Render.GlMatrixModeModelView();
        clientApi.Render.GlPushMatrix();
        clientApi.Render.GlLoadMatrix(cameraMatrix);
        try
        {
            // OnRenderBefore rebuilds the visible chunk set from the reflected
            // camera. Without it the replay would still contain only terrain
            // already visible in the primary image and could not fill the
            // off-screen side of a lake reflection.
            if (includeTerrain)
            {
                beforeTerrain!.Invoke(terrainRenderSystem, [currentTerrainDeltaTime]);
                RestoreMirrorTarget(mirrorFramebuffer, reflectedFrontFace);
                terrainPass!.Invoke(terrainRenderSystem, [currentTerrainDeltaTime]);
                RestoreMirrorTarget(mirrorFramebuffer, reflectedFrontFace);
            }
            pass.Invoke(renderSystem, [currentDeltaTime]);
            RestoreMirrorTarget(mirrorFramebuffer, reflectedFrontFace);
            failureLogged = false;
            return true;
        }
        catch (Exception exception)
        {
            if (!failureLogged)
            {
                failureLogged = true;
                Exception failure = exception is TargetInvocationException { InnerException: not null }
                    ? exception.InnerException
                    : exception;
                clientApi.Logger.Error(
                    "[VintageRTX] Official mirrored {0} geometry replay failed; "
                    + "the G-buffer fallback remains active: {1}",
                    includeTerrain ? "terrain/entity" : "entity-only",
                    failure);
            }

            return false;
        }
        finally
        {
            clientApi.Render.GlPopMatrix();
            RestoreMirrorTarget(mirrorFramebuffer, reflectedFrontFace);
            GL.FrontFace((FrontFaceDirection)savedFrontFace);
            FirstPersonReflectionCapturePatch.EndEntityMirrorReplay();
            replayInProgress = false;
            Array.Copy(SavedCameraMatrix, cameraMatrix, 16);
            Array.Copy(SavedCameraMatrixFloat, cameraMatrixFloat, 16);
        }
    }

    /// <summary>
    /// Restores the mirror draw target after an engine subsystem temporarily selected its normal
    /// primary framebuffer, while preserving the reflected winding convention.
    /// </summary>
    /// <param name="framebuffer">Mirror draw framebuffer captured on entry.</param>
    /// <param name="frontFace">Reflected front-face convention.</param>
    private static void RestoreMirrorTarget(int framebuffer, FrontFaceDirection frontFace)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(SavedViewport[0], SavedViewport[1], SavedViewport[2], SavedViewport[3]);
        GL.FrontFace(frontFace);
    }

    /// <summary>Reverses the winding convention for a view transform with negative determinant.</summary>
    /// <param name="frontFace">Current OpenGL front-face convention.</param>
    /// <returns>The opposite convention required by the reflected camera.</returns>
    internal static FrontFaceDirection ReflectedFrontFace(FrontFaceDirection frontFace) =>
        frontFace == FrontFaceDirection.Cw
            ? FrontFaceDirection.Ccw
            : FrontFaceDirection.Cw;

    /// <summary>Removes only this replay hook and clears active-world references.</summary>
    internal static void Uninstall()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        api = null;
        opaqueEntityPass = null;
        terrainBeforePass = null;
        opaqueTerrainPass = null;
        currentRenderSystem = null;
        currentTerrainRenderSystem = null;
        currentDeltaTime = 0.0f;
        currentTerrainDeltaTime = 0.0f;
        replayInProgress = false;
        failureLogged = false;
    }
}
