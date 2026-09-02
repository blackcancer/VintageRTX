using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Rendering;

/// <summary>
/// Defers only the local first-person opaque draw until the reflection source has copied Vintage
/// Story's completed world G-buffer. During the separate geometric mirror replay, the local renderer
/// is temporarily switched to its official third-person body mesh so the body can be reflected
/// without injecting the camera-space arm and held-item overlay into reflected incident radiance.
/// </summary>
internal static class FirstPersonReflectionCapturePatch
{
    /// <summary>Harmony ownership key used for surgical teardown.</summary>
    private const string HarmonyId = "vintagertx.first-person-reflection-source";
    /// <summary>Official renderer that owns the local first-person opaque draw in Vintage Story 1.22.</summary>
    private const string RendererTypeName = "Vintagestory.GameContent.EntityPlayerShapeRenderer";
    /// <summary>Installed patch owner, or null outside an active client world.</summary>
    private static Harmony? harmony;
    /// <summary>Current render-thread snapshot target.</summary>
    private static ReflectionSourceCaptureRenderer? capture;
    /// <summary>Exact official opaque draw patched and replayed by the render-thread boundary.</summary>
    private static MethodInfo? opaqueDrawMethod;
    /// <summary>Official batched body draw that submits the selected animated mesh.</summary>
    private static MethodInfo? opaqueBatchedDrawMethod;
    /// <summary>
    /// Official player transform patched during a mirror replay so the third-person mesh is
    /// positioned from its world entity instead of remaining in first-person camera space.
    /// </summary>
    private static MethodInfo? playerModelMatrixMethod;
    /// <summary>Inherited official entity field used without a compile-time game-content dependency.</summary>
    private static FieldInfo? entityField;
    /// <summary>Official 1.22 player render-mode field used to select the world-body mesh.</summary>
    private static FieldInfo? renderModeField;
    /// <summary>Inherited switch controlling separate held-item geometry in the official renderer.</summary>
    private static FieldInfo? renderHeldItemField;
    /// <summary>Inherited mesh slot changed by the official batched body renderer.</summary>
    private static FieldInfo? opaqueMeshField;
    /// <summary>Official complete player mesh used outside the camera-space first-person path.</summary>
    private static FieldInfo? thirdPersonMeshField;
    /// <summary>Animated model transform consumed by the batched entity shader.</summary>
    private static FieldInfo? modelMatrixField;
    /// <summary>Official visibility guard that suppresses every body draw in spectator mode.</summary>
    private static FieldInfo? spectatorField;
    /// <summary>
    /// Official player flag whose getter-side contract selects the third-person animation manager.
    /// Vintage Story uses the same switch while drawing the local body into its shadow maps.
    /// </summary>
    private static FieldInfo? playerShadowPassField;
    /// <summary>Inherited cached projection used only by the first-person hand path.</summary>
    private static FieldInfo? handProjectionField;
    /// <summary>Inherited cached projection restored before the deferred direct draw.</summary>
    private static FieldInfo? normalProjectionField;
    /// <summary>Renderer whose skipped first-person draw awaits the late-opaque replay boundary.</summary>
    private static object? deferredRenderer;
    /// <summary>Frame duration supplied to the skipped official draw.</summary>
    private static float deferredDeltaTime;
    /// <summary>Prevents the Harmony prefix from deferring its own reflected replay.</summary>
    private static bool replayInProgress;
    /// <summary>Suppresses repeated logs if a game update makes the official replay fail.</summary>
    private static bool replayFailureLogged;
    /// <summary>Fails open for the rest of the world after an incompatible official replay.</summary>
    private static bool deferralDisabled;
    /// <summary>Marks the entity-only mirror interval in which the local renderer needs a body mesh.</summary>
    private static bool entityMirrorReplayInProgress;
    /// <summary>Local player renderer temporarily promoted to the official third-person mode.</summary>
    private static object? mirroredLocalPlayerRenderer;
    /// <summary>Local player entity whose animation-manager selector is temporarily overridden.</summary>
    private static object? mirroredLocalPlayerEntity;
    /// <summary>Exact render-mode value restored after the official mirror pass.</summary>
    private static object? savedLocalPlayerRenderMode;
    /// <summary>Exact held-item switch restored after the official mirror pass.</summary>
    private static object? savedRenderHeldItem;
    /// <summary>Exact opaque mesh restored after the official mirror pass.</summary>
    private static object? savedOpaqueMesh;
    /// <summary>Exact animation-manager selector restored after the official mirror pass.</summary>
    private static object? savedPlayerShadowPass;
    /// <summary>Exact hand projection restored after the official mirror pass.</summary>
    private static object? savedHandProjection;
    /// <summary>Exact normal projection restored after the official mirror pass.</summary>
    private static object? savedNormalProjection;
    /// <summary>Suppresses repeated compatibility warnings if the render-mode switch fails.</summary>
    private static bool mirrorBodyFailureLogged;
    /// <summary>Prevents per-frame mirror diagnostics after the first completed reflected pass.</summary>
    private static bool mirrorTelemetryLogged;
    /// <summary>Number of local non-batched entries observed in the current reflected pass.</summary>
    private static int mirrorOpaqueCalls;
    /// <summary>Number of local world-transform evaluations observed in the current reflected pass.</summary>
    private static int mirrorTransformCalls;
    /// <summary>Number of local batched entries observed in the current reflected pass.</summary>
    private static int mirrorBatchedCalls;
    /// <summary>Number of local batched calls that returned normally in the current reflected pass.</summary>
    private static int mirrorBatchedCompletions;
    /// <summary>Whether the engine originally requested its camera-space self transform.</summary>
    private static bool mirrorTransformReceivedSelf;
    /// <summary>Whether the batched safety path had to prepare a missing local world transform.</summary>
    private static bool mirrorBatchedPreparedTransform;
    /// <summary>Whether the complete official player mesh existed at batched submission time.</summary>
    private static bool mirrorThirdPersonMeshAvailable;
    /// <summary>Whether the inherited opaque slot held a mesh after batched submission.</summary>
    private static bool mirrorOpaqueMeshAvailable;
    /// <summary>Whether the full-body mesh is paired with the official third-person animator.</summary>
    private static bool mirrorThirdPersonAnimatorSelected;
    /// <summary>Whether the official renderer considered the player a spectator.</summary>
    private static bool mirrorSpectator;
    /// <summary>Last reflected local-body model translation, for one bounded runtime diagnostic.</summary>
    private static readonly float[] MirrorModelTranslation = new float[3];

    /// <summary>Gets whether the opaque-stage boundary currently owes one official replay.</summary>
    internal static bool HasDeferredDraw => deferredRenderer is not null;

    /// <summary>Installs the exact 1.22-compatible player-render deferral prefix.</summary>
    /// <param name="api">Client world used to identify the local first-person entity.</param>
    /// <param name="targetCapture">Texture owner that defines the late-opaque replay boundary.</param>
    /// <returns>Whether the official renderer type and opaque draw method were found and patched.</returns>
    internal static bool Install(
        ICoreClientAPI api,
        ReflectionSourceCaptureRenderer targetCapture)
    {
        Uninstall();
        Type? rendererType = AccessTools.TypeByName(RendererTypeName);
        opaqueDrawMethod = rendererType is null
            ? null
            : AccessTools.Method(
                rendererType,
                "DoRender3DOpaque",
                [typeof(float), typeof(bool)]);
        opaqueBatchedDrawMethod = rendererType is null
            ? null
            : AccessTools.Method(
                rendererType,
                "DoRender3DOpaqueBatched",
                [typeof(float), typeof(bool)]);
        playerModelMatrixMethod = rendererType is null
            ? null
            : AccessTools.Method(
                rendererType,
                "loadModelMatrixForPlayer",
                [typeof(Entity), typeof(bool), typeof(float), typeof(bool)]);
        entityField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "entity");
        renderModeField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "renderMode");
        renderHeldItemField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "DoRenderHeldItem");
        opaqueMeshField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "meshRefOpaque");
        thirdPersonMeshField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "thirdPersonMeshRef");
        modelMatrixField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "ModelMat");
        spectatorField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "isSpectator");
        playerShadowPassField = AccessTools.Field(typeof(EntityPlayer), "selfNowShadowPass");
        handProjectionField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "pMatrixHandFov");
        normalProjectionField = rendererType is null
            ? null
            : AccessTools.Field(rendererType, "pMatrixNormalFov");
        if (opaqueDrawMethod is null
            || opaqueBatchedDrawMethod is null
            || playerModelMatrixMethod is null
            || entityField is null
            || renderModeField is null
            || renderHeldItemField is null
            || opaqueMeshField is null
            || thirdPersonMeshField is null
            || modelMatrixField is null
            || spectatorField is null
            || playerShadowPassField is null
            || handProjectionField is null
            || normalProjectionField is null)
        {
            api.Logger.Warning(
                "[VintageRTX] First-person reflection-source hook unavailable for this game build.");
            opaqueDrawMethod = null;
            opaqueBatchedDrawMethod = null;
            playerModelMatrixMethod = null;
            entityField = null;
            renderModeField = null;
            renderHeldItemField = null;
            opaqueMeshField = null;
            thirdPersonMeshField = null;
            modelMatrixField = null;
            spectatorField = null;
            playerShadowPassField = null;
            handProjectionField = null;
            normalProjectionField = null;
            return false;
        }

        capture = targetCapture;
        harmony = new Harmony(HarmonyId);
        harmony.Patch(
            opaqueDrawMethod,
            prefix: new HarmonyMethod(
                typeof(FirstPersonReflectionCapturePatch),
                nameof(BeforePlayerOpaque)));
        harmony.Patch(
            opaqueBatchedDrawMethod,
            prefix: new HarmonyMethod(
                typeof(FirstPersonReflectionCapturePatch),
                nameof(BeforePlayerOpaqueBatched)),
            postfix: new HarmonyMethod(
                typeof(FirstPersonReflectionCapturePatch),
                nameof(AfterPlayerOpaqueBatched)));
        harmony.Patch(
            playerModelMatrixMethod,
            prefix: new HarmonyMethod(
                typeof(FirstPersonReflectionCapturePatch),
                nameof(BeforeLoadPlayerModelMatrix)),
            postfix: new HarmonyMethod(
                typeof(FirstPersonReflectionCapturePatch),
                nameof(AfterLoadPlayerModelMatrix)));
        api.Logger.Notification(
            "[VintageRTX] First-person opaque draw deferral installed for Vintage Story 1.22.");
        return true;
    }

    /// <summary>Removes only this renderer hook and releases static references to the active world.</summary>
    internal static void Uninstall()
    {
        RestoreLocalPlayerRenderMode();
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        capture = null;
        opaqueDrawMethod = null;
        opaqueBatchedDrawMethod = null;
        playerModelMatrixMethod = null;
        entityField = null;
        renderModeField = null;
        renderHeldItemField = null;
        opaqueMeshField = null;
        thirdPersonMeshField = null;
        modelMatrixField = null;
        spectatorField = null;
        playerShadowPassField = null;
        handProjectionField = null;
        normalProjectionField = null;
        deferredRenderer = null;
        deferredDeltaTime = 0.0f;
        replayInProgress = false;
        replayFailureLogged = false;
        deferralDisabled = false;
        entityMirrorReplayInProgress = false;
        mirrorBodyFailureLogged = false;
        mirrorTelemetryLogged = false;
        ResetMirrorTelemetry();
    }

    /// <summary>
    /// Skips exactly one eligible local first-person draw. Any unexpected second candidate renders
    /// normally instead of being hidden, preserving a safe visual fallback.
    /// </summary>
    /// <param name="__instance">Official player renderer selected by Harmony.</param>
    /// <param name="dt">Frame duration required by the eventual official replay.</param>
    /// <param name="isShadowPass">Whether the call targets a shadow framebuffer.</param>
    /// <returns><see langword="false"/> only when this invocation was queued for replay.</returns>
    private static bool BeforePlayerOpaque(
        object __instance,
        float dt,
        bool isShadowPass)
    {
        Entity? renderedEntity = entityField?.GetValue(__instance) as Entity;
        long? localEntityId = capture?.Api.World.Player?.Entity?.EntityId;
        if (ShouldRenderLocalPlayerAsWorldBodyDuringMirror(
                entityMirrorReplayInProgress,
                renderedEntity?.EntityId,
                localEntityId))
        {
            mirrorOpaqueCalls++;
            return TryUseLocalPlayerWorldBody(__instance);
        }

        if (!ShouldDefer(__instance, isShadowPass) || deferredRenderer is not null)
        {
            return true;
        }

        deferredRenderer = __instance;
        deferredDeltaTime = dt;
        return false;
    }

    /// <summary>Marks the official entity pass as an entity-only reflected replay.</summary>
    internal static void BeginEntityMirrorReplay()
    {
        RestoreLocalPlayerRenderMode();
        ResetMirrorTelemetry();
        entityMirrorReplayInProgress = true;
    }

    /// <summary>Restores the local renderer after its reflected third-person body draw.</summary>
    internal static void EndEntityMirrorReplay()
    {
        ReportMirrorTelemetry();
        RestoreLocalPlayerRenderMode();
        entityMirrorReplayInProgress = false;
    }

    /// <summary>Keeps local world-body selection independently testable without a live renderer.</summary>
    /// <param name="mirrorReplay">Whether the official pass is currently targeting the mirror.</param>
    /// <param name="renderedEntityId">Entity owned by the candidate player renderer.</param>
    /// <param name="localEntityId">Current local player entity.</param>
    /// <returns>True only for the local player during the reflected geometry pass.</returns>
    internal static bool ShouldRenderLocalPlayerAsWorldBodyDuringMirror(
        bool mirrorReplay,
        long? renderedEntityId,
        long? localEntityId) =>
        mirrorReplay
        && renderedEntityId.HasValue
        && localEntityId.HasValue
        && renderedEntityId.Value == localEntityId.Value;

    /// <summary>Switches an official renderer to its named third-person enum value.</summary>
    /// <param name="renderer">Renderer instance that owns the supplied mode field.</param>
    /// <param name="modeField">Enum field controlling the player mesh selection.</param>
    /// <param name="previousMode">Receives the exact value that must be restored.</param>
    /// <returns>True when the field was an enum containing a <c>ThirdPerson</c> value.</returns>
    internal static bool TryOverrideToThirdPerson(
        object renderer,
        FieldInfo? modeField,
        out object? previousMode)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        previousMode = null;
        if (modeField is null || !modeField.FieldType.IsEnum)
        {
            return false;
        }

        try
        {
            object thirdPerson = Enum.Parse(modeField.FieldType, "ThirdPerson", ignoreCase: false);
            previousMode = modeField.GetValue(renderer);
            modeField.SetValue(renderer, thirdPerson);
            return true;
        }
        catch (ArgumentException)
        {
            previousMode = null;
            return false;
        }
        catch (FieldAccessException)
        {
            previousMode = null;
            return false;
        }
    }

    /// <summary>Restores a render-mode field previously changed for a mirror replay.</summary>
    /// <param name="renderer">Renderer instance owning the mode field.</param>
    /// <param name="modeField">Field changed by <see cref="TryOverrideToThirdPerson"/>.</param>
    /// <param name="previousMode">Exact enum value captured before the change.</param>
    /// <returns>True when a compatible non-null field value was restored.</returns>
    internal static bool TryRestoreRenderMode(
        object? renderer,
        FieldInfo? modeField,
        object? previousMode)
    {
        if (renderer is null
            || modeField is null
            || previousMode is null
            || !modeField.FieldType.IsInstanceOfType(previousMode))
        {
            return false;
        }

        try
        {
            modeField.SetValue(renderer, previousMode);
            return true;
        }
        catch (FieldAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Writes a compatible reflected field value, including null reference values.</summary>
    /// <param name="renderer">Renderer instance owning the field.</param>
    /// <param name="field">Field that must receive the value.</param>
    /// <param name="value">Previously captured field value.</param>
    /// <returns>True when the value was compatible and assigned.</returns>
    internal static bool TrySetCompatibleFieldValue(
        object? renderer,
        FieldInfo? field,
        object? value)
    {
        if (renderer is null
            || field is null
            || (value is null && field.FieldType.IsValueType)
            || (value is not null && !field.FieldType.IsInstanceOfType(value)))
        {
            return false;
        }

        try
        {
            field.SetValue(renderer, value);
            return true;
        }
        catch (FieldAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Selects the official third-person animation manager for one reflected local-body draw.
    /// In Vintage Story 1.22, <see cref="EntityPlayer.AnimManager"/> returns the detached first-person
    /// hand animator while the camera is first-person unless <c>selfNowShadowPass</c> is true. The
    /// engine already uses that flag to pair the full body with <see cref="EntityPlayer.TpAnimManager"/>
    /// for shadow geometry, which is the same mesh/animation contract required by a mirror replay.
    /// </summary>
    /// <param name="playerEntity">Official local player entity owning the selector field.</param>
    /// <param name="selectorField">Boolean 1.22 animation-manager selector.</param>
    /// <param name="previousValue">Exact value restored after the mirror pass.</param>
    /// <returns>True when the selector was captured and enabled.</returns>
    internal static bool TrySelectThirdPersonAnimator(
        object playerEntity,
        FieldInfo? selectorField,
        out object? previousValue)
    {
        ArgumentNullException.ThrowIfNull(playerEntity);
        previousValue = null;
        if (selectorField?.FieldType != typeof(bool))
        {
            return false;
        }

        try
        {
            previousValue = selectorField.GetValue(playerEntity);
            selectorField.SetValue(playerEntity, true);
            return true;
        }
        catch (FieldAccessException)
        {
            previousValue = null;
            return false;
        }
        catch (ArgumentException)
        {
            previousValue = null;
            return false;
        }
    }

    /// <summary>Promotes the local renderer for the remainder of the official entity pass.</summary>
    /// <param name="renderer">Local official player renderer currently entering its opaque draw.</param>
    /// <returns>True when the original draw may continue using the world-body mesh.</returns>
    private static bool TryUseLocalPlayerWorldBody(object renderer)
    {
        if (mirroredLocalPlayerRenderer is not null)
        {
            if (!ReferenceEquals(mirroredLocalPlayerRenderer, renderer)
                || mirroredLocalPlayerEntity is not EntityPlayer activePlayerEntity
                || !TrySetCompatibleFieldValue(activePlayerEntity, playerShadowPassField, true))
            {
                return false;
            }

            // DoRender3DOpaque writes selfNowShadowPass from its false argument after our
            // non-batched prefix. Reassert the selector here when the engine enters the later
            // batched mesh submission, immediately before EntityShapeRenderer reads AnimManager.
            mirrorThirdPersonAnimatorSelected = ReferenceEquals(
                activePlayerEntity.AnimManager,
                activePlayerEntity.TpAnimManager);
            return mirrorThirdPersonAnimatorSelected;
        }

        if (renderHeldItemField is null
            || opaqueMeshField is null
            || playerShadowPassField is null
            || handProjectionField is null
            || normalProjectionField is null)
        {
            return ReportMirrorBodyFailure();
        }

        object? previousHeldItem = renderHeldItemField.GetValue(renderer);
        object? previousOpaqueMesh = opaqueMeshField.GetValue(renderer);
        object? previousHandProjection = handProjectionField.GetValue(renderer);
        object? previousNormalProjection = normalProjectionField.GetValue(renderer);
        EntityPlayer? playerEntity = entityField?.GetValue(renderer) as EntityPlayer;
        if (TryOverrideToThirdPerson(renderer, renderModeField, out object? previousMode)
            && TrySetCompatibleFieldValue(renderer, renderHeldItemField, false)
            && playerEntity is not null
            && TrySelectThirdPersonAnimator(
                playerEntity,
                playerShadowPassField,
                out object? previousPlayerShadowPass))
        {
            mirroredLocalPlayerRenderer = renderer;
            mirroredLocalPlayerEntity = playerEntity;
            savedLocalPlayerRenderMode = previousMode;
            savedRenderHeldItem = previousHeldItem;
            savedOpaqueMesh = previousOpaqueMesh;
            savedPlayerShadowPass = previousPlayerShadowPass;
            savedHandProjection = previousHandProjection;
            savedNormalProjection = previousNormalProjection;
            mirrorThirdPersonAnimatorSelected = ReferenceEquals(
                playerEntity.AnimManager,
                playerEntity.TpAnimManager);
            if (!mirrorThirdPersonAnimatorSelected)
            {
                RestoreLocalPlayerRenderMode();
                return ReportMirrorBodyFailure();
            }
            mirrorBodyFailureLogged = false;
            return true;
        }

        TryRestoreRenderMode(renderer, renderModeField, previousMode);
        TrySetCompatibleFieldValue(renderer, renderHeldItemField, previousHeldItem);
        return ReportMirrorBodyFailure();
    }

    /// <summary>Logs one compatibility failure and excludes the unsafe first-person overlay.</summary>
    /// <returns>Always false so Harmony suppresses the incompatible local draw.</returns>
    private static bool ReportMirrorBodyFailure()
    {
        if (!mirrorBodyFailureLogged)
        {
            mirrorBodyFailureLogged = true;
            capture?.Api.Logger.Warning(
                "[VintageRTX] Local reflected body mode is unavailable; "
                + "the first-person overlay is conservatively excluded from the mirror.");
        }

        return false;
    }

    /// <summary>
    /// Makes the official player transform follow its world-position branch during the mirror pass.
    /// Vintage Story 1.22 otherwise treats the local entity as camera-space even after selecting its
    /// third-person mesh, which leaves that mesh at the reflected camera origin and outside the
    /// useful mirror image. Patching the transform argument itself avoids relying on whether the
    /// runtime inlined the small <c>IsSelf</c> property getter before Harmony was installed.
    /// </summary>
    /// <param name="__instance">Official player renderer whose transform is being prepared.</param>
    /// <param name="isSelf">Official camera-space selection passed to the player transform.</param>
    private static void BeforeLoadPlayerModelMatrix(object __instance, ref bool isSelf)
    {
        if (entityMirrorReplayInProgress
            && ReferenceEquals(mirroredLocalPlayerRenderer, __instance))
        {
            mirrorTransformCalls++;
            mirrorTransformReceivedSelf |= isSelf;
        }

        isSelf = IsSelfForCurrentRender(entityMirrorReplayInProgress, isSelf);
    }

    /// <summary>Captures the resulting local world transform for one bounded runtime diagnostic.</summary>
    /// <param name="__instance">Official player renderer whose model transform was updated.</param>
    private static void AfterLoadPlayerModelMatrix(object __instance)
    {
        if (!entityMirrorReplayInProgress
            || !ReferenceEquals(mirroredLocalPlayerRenderer, __instance)
            || modelMatrixField?.GetValue(__instance) is not float[] { Length: >= 15 } modelMatrix)
        {
            return;
        }

        MirrorModelTranslation[0] = modelMatrix[12];
        MirrorModelTranslation[1] = modelMatrix[13];
        MirrorModelTranslation[2] = modelMatrix[14];
    }

    /// <summary>
    /// Reasserts local third-person state at the actual mesh-submission boundary. If an engine build
    /// reaches the batched loop without its preceding non-batched player call, the official transform
    /// is prepared directly before drawing instead of silently retaining first-person state.
    /// </summary>
    /// <param name="__instance">Official player renderer entering batched submission.</param>
    /// <param name="dt">Current render-frame duration used by the player transform.</param>
    /// <param name="isShadowPass">Whether this invocation targets the engine shadow pass.</param>
    private static void BeforePlayerOpaqueBatched(
        object __instance,
        float dt,
        bool isShadowPass)
    {
        if (!entityMirrorReplayInProgress
            || isShadowPass
            || !IsLocalPlayerRenderer(__instance))
        {
            return;
        }

        mirrorBatchedCalls++;
        if (!TryUseLocalPlayerWorldBody(__instance))
        {
            return;
        }

        if (mirrorTransformCalls == 0
            && entityField?.GetValue(__instance) is Entity localEntity
            && playerModelMatrixMethod is not null)
        {
            playerModelMatrixMethod.Invoke(__instance, [localEntity, false, dt, false]);
            mirrorBatchedPreparedTransform = true;
        }

        object? thirdPersonMesh = thirdPersonMeshField?.GetValue(__instance);
        mirrorThirdPersonMeshAvailable = thirdPersonMesh is not null;
        if (thirdPersonMesh is not null)
        {
            TrySetCompatibleFieldValue(__instance, opaqueMeshField, thirdPersonMesh);
        }

        mirrorSpectator = spectatorField?.GetValue(__instance) is true;
    }

    /// <summary>Records that the official local batched draw returned without throwing.</summary>
    /// <param name="__instance">Official player renderer leaving batched submission.</param>
    /// <param name="isShadowPass">Whether this invocation targeted the engine shadow pass.</param>
    private static void AfterPlayerOpaqueBatched(object __instance, bool isShadowPass)
    {
        if (!entityMirrorReplayInProgress
            || isShadowPass
            || !IsLocalPlayerRenderer(__instance))
        {
            return;
        }

        mirrorBatchedCompletions++;
        mirrorOpaqueMeshAvailable = opaqueMeshField?.GetValue(__instance) is not null;
    }

    /// <summary>Determines whether a renderer owns the active local-player entity.</summary>
    /// <param name="renderer">Official player renderer candidate.</param>
    /// <returns>True only for the active local entity.</returns>
    private static bool IsLocalPlayerRenderer(object renderer)
    {
        Entity? renderedEntity = entityField?.GetValue(renderer) as Entity;
        long? localEntityId = capture?.Api.World.Player?.Entity?.EntityId;
        return renderedEntity is not null
            && localEntityId.HasValue
            && renderedEntity.EntityId == localEntityId.Value;
    }

    /// <summary>Clears allocation-free counters before one official mirrored entity pass.</summary>
    private static void ResetMirrorTelemetry()
    {
        mirrorOpaqueCalls = 0;
        mirrorTransformCalls = 0;
        mirrorBatchedCalls = 0;
        mirrorBatchedCompletions = 0;
        mirrorTransformReceivedSelf = false;
        mirrorBatchedPreparedTransform = false;
        mirrorThirdPersonMeshAvailable = false;
        mirrorOpaqueMeshAvailable = false;
        mirrorThirdPersonAnimatorSelected = false;
        mirrorSpectator = false;
        Array.Clear(MirrorModelTranslation);
    }

    /// <summary>Writes exactly one local-body path diagnostic per installed client world.</summary>
    private static void ReportMirrorTelemetry()
    {
        if (mirrorTelemetryLogged)
        {
            return;
        }

        mirrorTelemetryLogged = true;
        capture?.Api.Logger.Notification(
            "[VintageRTX] Local mirror body path: opaque={0}, transform={1}, batched={2}/{3}, "
            + "received-self={4}, batched-transform={5}, third-mesh={6}, opaque-mesh={7}, "
            + "third-animator={8}, spectator={9}, translation=({10:0.000},{11:0.000},{12:0.000}).",
            mirrorOpaqueCalls,
            mirrorTransformCalls,
            mirrorBatchedCompletions,
            mirrorBatchedCalls,
            mirrorTransformReceivedSelf,
            mirrorBatchedPreparedTransform,
            mirrorThirdPersonMeshAvailable,
            mirrorOpaqueMeshAvailable,
            mirrorThirdPersonAnimatorSelected,
            mirrorSpectator,
            MirrorModelTranslation[0],
            MirrorModelTranslation[1],
            MirrorModelTranslation[2]);
    }

    /// <summary>Resolves whether the official transform may use its camera-space self branch.</summary>
    /// <param name="mirrorReplay">Whether rendering currently targets the reflected entity view.</param>
    /// <param name="officialIsSelf">Whether Vintage Story identified the renderer as local.</param>
    /// <returns>
    /// False for the local player only during mirror replay; otherwise the official result unchanged.
    /// </returns>
    internal static bool IsSelfForCurrentRender(bool mirrorReplay, bool officialIsSelf) =>
        officialIsSelf && !mirrorReplay;

    /// <summary>Restores and clears any local render-mode override left by a mirror pass.</summary>
    private static void RestoreLocalPlayerRenderMode()
    {
        object? renderer = mirroredLocalPlayerRenderer;
        object? previousMode = savedLocalPlayerRenderMode;
        object? previousHeldItem = savedRenderHeldItem;
        object? previousOpaqueMesh = savedOpaqueMesh;
        object? playerEntity = mirroredLocalPlayerEntity;
        object? previousPlayerShadowPass = savedPlayerShadowPass;
        object? previousHandProjection = savedHandProjection;
        object? previousNormalProjection = savedNormalProjection;
        mirroredLocalPlayerRenderer = null;
        mirroredLocalPlayerEntity = null;
        savedLocalPlayerRenderMode = null;
        savedRenderHeldItem = null;
        savedOpaqueMesh = null;
        savedPlayerShadowPass = null;
        savedHandProjection = null;
        savedNormalProjection = null;
        if (renderer is null)
        {
            return;
        }

        bool restored = TryRestoreRenderMode(renderer, renderModeField, previousMode);
        restored &= TrySetCompatibleFieldValue(renderer, renderHeldItemField, previousHeldItem);
        restored &= TrySetCompatibleFieldValue(renderer, opaqueMeshField, previousOpaqueMesh);
        restored &= TrySetCompatibleFieldValue(
            playerEntity,
            playerShadowPassField,
            previousPlayerShadowPass);
        restored &= TrySetCompatibleFieldValue(renderer, handProjectionField, previousHandProjection);
        restored &= TrySetCompatibleFieldValue(renderer, normalProjectionField, previousNormalProjection);
        if (!restored)
        {
            capture?.Api.Logger.Error(
                "[VintageRTX] Failed to restore the local player render mode after mirror replay.");
        }
    }

    /// <summary>
    /// Replays the skipped official draw after the owned opaque attachments have been copied.
    /// Reflection invokes the same virtual target while the re-entry guard lets its Harmony prefix
    /// proceed normally.
    /// </summary>
    internal static void ReplayDeferredDraw()
    {
        object? renderer = deferredRenderer;
        MethodInfo? target = opaqueDrawMethod;
        ReflectionSourceCaptureRenderer? activeCapture = capture;
        if (renderer is null || target is null || activeCapture is null)
        {
            deferredRenderer = null;
            deferredDeltaTime = 0.0f;
            return;
        }

        float deltaTime = deferredDeltaTime;
        deferredRenderer = null;
        deferredDeltaTime = 0.0f;
        replayInProgress = true;
        try
        {
            target.Invoke(renderer, [deltaTime, false]);
            replayFailureLogged = false;
        }
        catch (Exception exception)
        {
            if (!replayFailureLogged)
            {
                replayFailureLogged = true;
                Exception failure = exception is TargetInvocationException { InnerException: not null }
                    ? exception.InnerException
                    : exception;
                activeCapture.Api.Logger.Error(
                    "[VintageRTX] Deferred first-person opaque replay failed; deferral is disabled and subsequent draws run normally: {0}",
                    failure);
            }

            deferralDisabled = true;
        }
        finally
        {
            replayInProgress = false;
        }
    }

    /// <summary>Rejects remote players, third-person cameras, shadow passes, and disabled captures.</summary>
    /// <param name="renderer">Candidate official renderer instance.</param>
    /// <param name="isShadowPass">Whether the candidate draw is a shadow call.</param>
    /// <returns>True only for the local player in a first-person camera draw.</returns>
    private static bool ShouldDefer(object renderer, bool isShadowPass)
    {
        ReflectionSourceCaptureRenderer? activeCapture = capture;
        Entity? renderedEntity = entityField?.GetValue(renderer) as Entity;
        IClientPlayer? player = activeCapture?.Api.World.Player;
        return IsEligibleForDeferral(
            isShadowPass,
            activeCapture?.Enabled == true && !deferralDisabled,
            replayInProgress,
            player?.CameraMode == EnumCameraMode.FirstPerson,
            renderedEntity?.EntityId,
            player?.Entity?.EntityId);
    }

    /// <summary>Keeps the version-sensitive Harmony predicate independently testable.</summary>
    /// <param name="isShadowPass">Whether the call targets a shadow map.</param>
    /// <param name="captureEnabled">Whether a reflection snapshot will run later this frame.</param>
    /// <param name="isReplay">Whether the official method is currently being replayed.</param>
    /// <param name="isFirstPerson">Whether the local camera is first-person.</param>
    /// <param name="renderedEntityId">Entity rendered by the patched instance.</param>
    /// <param name="localEntityId">Current local-player entity.</param>
    /// <returns>True only when skipping the original draw is safe and required.</returns>
    internal static bool IsEligibleForDeferral(
        bool isShadowPass,
        bool captureEnabled,
        bool isReplay,
        bool isFirstPerson,
        long? renderedEntityId,
        long? localEntityId) =>
        !isShadowPass
        && captureEnabled
        && !isReplay
        && isFirstPerson
        && renderedEntityId.HasValue
        && localEntityId.HasValue
        && renderedEntityId.Value == localEntityId.Value;
}
