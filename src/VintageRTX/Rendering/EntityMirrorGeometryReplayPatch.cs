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
    private static readonly double[] SavedPerspectiveProjection = new double[16];
    private static readonly float[] SavedCurrentProjection = new float[16];
    private static readonly double[] SavedProjectionStackTop = new double[16];
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
        return Install(
            clientApi,
            AccessTools.TypeByName(RenderSystemTypeName),
            AccessTools.TypeByName(TerrainRenderSystemTypeName));
    }

    /// <summary>
    /// Installs the replay against already-resolved engine system types. Production supplies the
    /// official 1.22 types, while deterministic tests can prove Harmony success and rollback without
    /// loading the complete game client assembly.
    /// </summary>
    /// <param name="clientApi">Client renderer whose camera arrays are temporarily reflected.</param>
    /// <param name="renderSystemType">Resolved entity render-system type, or null when unavailable.</param>
    /// <param name="terrainRenderSystemType">Resolved terrain render-system type, or null when unavailable.</param>
    /// <returns>Whether all required opaque callbacks were found and patched.</returns>
    internal static bool Install(
        ICoreClientAPI clientApi,
        Type? renderSystemType,
        Type? terrainRenderSystemType)
    {
        ArgumentNullException.ThrowIfNull(clientApi);
        Uninstall();
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
    /// <param name="obliqueProjectionMatrix">Projection whose near plane clips the liquid interface.</param>
    /// <returns>True only when the official geometry pass completed successfully.</returns>
    internal static bool TryReplay(
        double[] mirroredCameraMatrix,
        double[] obliqueProjectionMatrix) =>
        TryReplayCore(
            mirroredCameraMatrix,
            obliqueProjectionMatrix,
            includeTerrain: true);

    /// <summary>Replays only official opaque entities into an isolated evidence framebuffer.</summary>
    /// <param name="mirroredCameraMatrix">Column-major reflected floating-origin camera matrix.</param>
    /// <param name="obliqueProjectionMatrix">Projection whose near plane clips the liquid interface.</param>
    /// <returns>True only when the official entity pass completed successfully.</returns>
    internal static bool TryReplayEntitiesOnly(
        double[] mirroredCameraMatrix,
        double[] obliqueProjectionMatrix) =>
        TryReplayCore(
            mirroredCameraMatrix,
            obliqueProjectionMatrix,
            includeTerrain: false);

    /// <summary>Executes a reflected official replay with optional terrain submission.</summary>
    /// <param name="mirroredCameraMatrix">Column-major reflected floating-origin camera matrix.</param>
    /// <param name="obliqueProjectionMatrix">Projection whose near plane clips the liquid interface.</param>
    /// <param name="includeTerrain">Whether to cull and draw opaque terrain before entities.</param>
    /// <returns>True when every requested official pass completed.</returns>
    private static bool TryReplayCore(
        double[] mirroredCameraMatrix,
        double[] obliqueProjectionMatrix,
        bool includeTerrain)
    {
        ICoreClientAPI? clientApi = api;
        MethodInfo? pass = opaqueEntityPass;
        MethodInfo? beforeTerrain = terrainBeforePass;
        MethodInfo? terrainPass = opaqueTerrainPass;
        object? renderSystem = currentRenderSystem;
        object? terrainRenderSystem = currentTerrainRenderSystem;
        if (clientApi is null
            || replayInProgress
            || pass is null
            || renderSystem is null
            || (includeTerrain
                && (beforeTerrain is null
                    || terrainPass is null
                    || terrainRenderSystem is null))
            || mirroredCameraMatrix.Length < 16
            || obliqueProjectionMatrix.Length < 16)
        {
            return false;
        }

        double[] cameraMatrix = clientApi.Render.CameraMatrixOrigin;
        float[] cameraMatrixFloat = clientApi.Render.CameraMatrixOriginf;
        double[] perspectiveProjection = clientApi.Render.PerspectiveProjectionMat;
        float[] currentProjection = clientApi.Render.CurrentProjectionMatrix;
        var projectionStack = clientApi.Render.PMatrix;
        if (cameraMatrix.Length < 16
            || cameraMatrixFloat.Length < 16
            || perspectiveProjection.Length < 16
            || currentProjection.Length < 16
            || projectionStack.Count < 1
            || projectionStack.Top.Length < 16
            || !IsFiniteMatrix(obliqueProjectionMatrix))
        {
            return false;
        }

        Array.Copy(cameraMatrix, SavedCameraMatrix, 16);
        Array.Copy(cameraMatrixFloat, SavedCameraMatrixFloat, 16);
        Array.Copy(perspectiveProjection, SavedPerspectiveProjection, 16);
        Array.Copy(currentProjection, SavedCurrentProjection, 16);
        Array.Copy(projectionStack.Top, SavedProjectionStackTop, 16);
        int savedProjectionStackCount = projectionStack.Count;
        bool mirrorReplayStarted = false;
        bool modelViewPushed = false;
        GL.GetInteger(GetPName.FrontFace, out int savedFrontFace);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int mirrorFramebuffer);
        GL.GetInteger(GetPName.Viewport, SavedViewport);
        FrontFaceDirection reflectedFrontFace = ReflectedFrontFace(
            (FrontFaceDirection)savedFrontFace);
        try
        {
            projectionStack.Push(obliqueProjectionMatrix);
            ApplyMirrorMatrices(
                clientApi,
                mirroredCameraMatrix,
                obliqueProjectionMatrix,
                savedProjectionStackCount + 1);
            replayInProgress = true;
            FirstPersonReflectionCapturePatch.BeginEntityMirrorReplay();
            mirrorReplayStarted = true;
            GL.FrontFace(reflectedFrontFace);
            clientApi.Render.GlMatrixModeModelView();
            clientApi.Render.GlPushMatrix();
            modelViewPushed = true;
            clientApi.Render.GlLoadMatrix(cameraMatrix);
            // OnRenderBefore rebuilds the visible chunk set from the reflected
            // camera. Without it the replay would still contain only terrain
            // already visible in the primary image and could not fill the
            // off-screen side of a lake reflection.
            if (includeTerrain)
            {
                beforeTerrain!.Invoke(terrainRenderSystem, [currentTerrainDeltaTime]);
                ApplyMirrorMatrices(
                    clientApi,
                    mirroredCameraMatrix,
                    obliqueProjectionMatrix,
                    savedProjectionStackCount + 1);
                RestoreMirrorTarget(mirrorFramebuffer, reflectedFrontFace);
                terrainPass!.Invoke(terrainRenderSystem, [currentTerrainDeltaTime]);
                ApplyMirrorMatrices(
                    clientApi,
                    mirroredCameraMatrix,
                    obliqueProjectionMatrix,
                    savedProjectionStackCount + 1);
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
            if (modelViewPushed)
            {
                clientApi.Render.GlPopMatrix();
            }
            RestoreMirrorTarget(mirrorFramebuffer, reflectedFrontFace);
            GL.FrontFace((FrontFaceDirection)savedFrontFace);
            if (mirrorReplayStarted)
            {
                FirstPersonReflectionCapturePatch.EndEntityMirrorReplay();
            }
            replayInProgress = false;
            RestoreMutableCameraCarriers(
                cameraMatrix,
                SavedCameraMatrix,
                cameraMatrixFloat,
                SavedCameraMatrixFloat,
                perspectiveProjection,
                SavedPerspectiveProjection,
                currentProjection,
                SavedCurrentProjection,
                projectionStack,
                savedProjectionStackCount,
                SavedProjectionStackTop);
        }
    }

    /// <summary>
    /// Restores every mutable engine camera carrier after an official mirrored replay. Keeping this
    /// operation reusable lets the outer display pass enforce the same invariant even if an engine
    /// callback changes a carrier after the inner replay has returned.
    /// </summary>
    /// <param name="cameraMatrix">Engine-owned double-precision view matrix.</param>
    /// <param name="savedCameraMatrix">Exact pre-replay view snapshot.</param>
    /// <param name="cameraMatrixFloat">Engine-owned single-precision view matrix.</param>
    /// <param name="savedCameraMatrixFloat">Exact pre-replay float-view snapshot.</param>
    /// <param name="perspectiveProjection">Engine-owned double-precision projection.</param>
    /// <param name="savedPerspectiveProjection">Exact pre-replay projection snapshot.</param>
    /// <param name="currentProjection">Engine-owned single-precision current projection.</param>
    /// <param name="savedCurrentProjection">Exact pre-replay float-projection snapshot.</param>
    /// <param name="projectionStack">Engine-owned projection stack.</param>
    /// <param name="savedProjectionStackCount">Exact pre-replay stack depth.</param>
    /// <param name="savedProjectionStackTop">Exact pre-replay top matrix.</param>
    internal static void RestoreMutableCameraCarriers(
        double[] cameraMatrix,
        double[] savedCameraMatrix,
        float[] cameraMatrixFloat,
        float[] savedCameraMatrixFloat,
        double[] perspectiveProjection,
        double[] savedPerspectiveProjection,
        float[] currentProjection,
        float[] savedCurrentProjection,
        Vintagestory.API.Datastructures.StackMatrix4 projectionStack,
        int savedProjectionStackCount,
        double[] savedProjectionStackTop)
    {
        ArgumentNullException.ThrowIfNull(cameraMatrix);
        ArgumentNullException.ThrowIfNull(savedCameraMatrix);
        ArgumentNullException.ThrowIfNull(cameraMatrixFloat);
        ArgumentNullException.ThrowIfNull(savedCameraMatrixFloat);
        ArgumentNullException.ThrowIfNull(perspectiveProjection);
        ArgumentNullException.ThrowIfNull(savedPerspectiveProjection);
        ArgumentNullException.ThrowIfNull(currentProjection);
        ArgumentNullException.ThrowIfNull(savedCurrentProjection);
        if (cameraMatrix.Length < 16
            || savedCameraMatrix.Length < 16
            || cameraMatrixFloat.Length < 16
            || savedCameraMatrixFloat.Length < 16
            || perspectiveProjection.Length < 16
            || savedPerspectiveProjection.Length < 16
            || currentProjection.Length < 16
            || savedCurrentProjection.Length < 16)
        {
            throw new ArgumentException("Mutable camera carriers must contain sixteen coefficients.");
        }

        Array.Copy(savedCameraMatrix, cameraMatrix, 16);
        Array.Copy(savedCameraMatrixFloat, cameraMatrixFloat, 16);
        RestoreProjectionStack(
            projectionStack,
            savedProjectionStackCount,
            projectionPushed: true,
            savedProjectionStackTop);
        Array.Copy(savedPerspectiveProjection, perspectiveProjection, 16);
        Array.Copy(savedCurrentProjection, currentProjection, 16);
    }

    /// <summary>Applies reflected view and oblique projection arrays immediately before an official pass.</summary>
    /// <param name="clientApi">Active render API whose mutable matrix carriers are scoped by the replay.</param>
    /// <param name="mirroredCameraMatrix">Reflected floating-origin view.</param>
    /// <param name="obliqueProjectionMatrix">Projection clipped to the liquid interface.</param>
    /// <param name="expectedProjectionStackCount">Exact stack depth owned by this replay.</param>
    private static void ApplyMirrorMatrices(
        ICoreClientAPI clientApi,
        double[] mirroredCameraMatrix,
        double[] obliqueProjectionMatrix,
        int expectedProjectionStackCount)
    {
        double[] cameraMatrix = clientApi.Render.CameraMatrixOrigin;
        float[] cameraMatrixFloat = clientApi.Render.CameraMatrixOriginf;
        double[] perspectiveProjection = clientApi.Render.PerspectiveProjectionMat;
        float[] currentProjection = clientApi.Render.CurrentProjectionMatrix;
        var projectionStack = clientApi.Render.PMatrix;
        if (projectionStack.Count != expectedProjectionStackCount
            || projectionStack.Top.Length < 16)
        {
            throw new InvalidOperationException(
                "The official mirrored pass changed the projection stack depth.");
        }

        for (int index = 0; index < 16; index++)
        {
            double viewValue = mirroredCameraMatrix[index];
            double projectionValue = obliqueProjectionMatrix[index];
            if (!double.IsFinite(viewValue) || !double.IsFinite(projectionValue))
            {
                throw new InvalidOperationException(
                    "The reflected view or liquid-clipped projection is non-finite.");
            }

            cameraMatrix[index] = viewValue;
            MirroredCameraMatrixFloat[index] = (float)viewValue;
            cameraMatrixFloat[index] = MirroredCameraMatrixFloat[index];
            perspectiveProjection[index] = projectionValue;
            currentProjection[index] = (float)projectionValue;
            projectionStack.Top[index] = projectionValue;
        }
    }

    /// <summary>Restores the exact projection-stack depth and top matrix captured before replay.</summary>
    /// <param name="projectionStack">Engine-owned projection stack.</param>
    /// <param name="savedCount">Depth observed before the mirror matrix was pushed.</param>
    /// <param name="projectionPushed">Whether this replay successfully pushed its matrix.</param>
    /// <param name="savedTop">Exact matrix that occupied the original top slot.</param>
    internal static void RestoreProjectionStack(
        Vintagestory.API.Datastructures.StackMatrix4 projectionStack,
        int savedCount,
        bool projectionPushed,
        double[] savedTop)
    {
        ArgumentNullException.ThrowIfNull(projectionStack);
        ArgumentNullException.ThrowIfNull(savedTop);
        if (savedCount < 0 || savedTop.Length < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(savedCount));
        }

        if (projectionPushed)
        {
            while (projectionStack.Count > savedCount)
            {
                projectionStack.Pop();
            }
        }

        while (projectionStack.Count < savedCount)
        {
            projectionStack.Push(savedTop);
        }

        // StackMatrix4 always exposes a sixteen-coefficient Top when non-empty.
        if (projectionStack.Count > 0)
        {
            Array.Copy(savedTop, projectionStack.Top, 16);
        }
    }

    /// <summary>Checks the first sixteen coefficients of a replay matrix.</summary>
    /// <param name="matrix">Candidate column-major matrix.</param>
    /// <returns>True only when every coefficient is finite.</returns>
    private static bool IsFiniteMatrix(double[] matrix)
    {
        for (int index = 0; index < 16; index++)
        {
            if (!double.IsFinite(matrix[index]))
            {
                return false;
            }
        }

        return true;
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
