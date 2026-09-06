using System.Reflection;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Test;

/// <summary>Covers version-sensitive first-person patch state without launching the game renderer.</summary>
[TestClass]
[DoNotParallelize]
public sealed class FirstPersonReflectionCapturePatchCoverageTests
{
    /// <summary>Starts every test from an uninstalled, allocation-free patch state.</summary>
    [TestInitialize]
    public void ResetPatch()
    {
        FirstPersonReflectionCapturePatch.Uninstall();
    }

    /// <summary>Removes Harmony/static state after every assertion, including failed assertions.</summary>
    [TestCleanup]
    public void CleanupPatch()
    {
        FirstPersonReflectionCapturePatch.Uninstall();
    }

    /// <summary>Exercises unavailable-build installation and verifies every discovered handle is cleared.</summary>
    [TestMethod]
    public void InstallFailsOpenWhenOfficialRendererTypeIsUnavailable()
    {
        List<string> logs = [];
        ICoreClientAPI api = Api(new EntityPlayer { EntityId = 42 }, logs);
        ReflectionSourceCaptureRenderer capture = new(api);
        Harmony typeGate = new("vintagertx.test.first-person-type-gate");
        MethodInfo typeByName = AccessTools.Method(
            typeof(AccessTools),
            nameof(AccessTools.TypeByName),
            [typeof(string)]);
        typeGate.Patch(
            typeByName,
            prefix: new HarmonyMethod(
                typeof(FirstPersonReflectionCapturePatchCoverageTests),
                nameof(HideOfficialRendererType)));
        try
        {
            bool installed = FirstPersonReflectionCapturePatch.Install(api, capture);

            Assert.IsFalse(installed);
            Assert.IsFalse(FirstPersonReflectionCapturePatch.HasDeferredDraw);
            Assert.IsTrue(logs.Any(message => message.Contains("hook unavailable", StringComparison.Ordinal)));
        }
        finally
        {
            typeGate.UnpatchAll(typeGate.Id);
        }
    }

    /// <summary>Installs all three Harmony hooks and drives the synthetic official mirror-body path.</summary>
    [TestMethod]
    public void InstallAndMirrorReplayUseOfficialBodyContractEndToEnd()
    {
        List<string> logs = [];
        EntityPlayer local = new() { EntityId = 42 };
        ICoreClientAPI api = Api(local, logs);
        ReflectionSourceCaptureRenderer capture = new(api);
        global::Vintagestory.GameContent.EntityPlayerShapeRenderer renderer = new(local);
        object originalMesh = renderer.meshRefOpaque!;
        object originalHandProjection = renderer.pMatrixHandFov!;
        object originalNormalProjection = renderer.pMatrixNormalFov!;

        Assert.IsTrue(FirstPersonReflectionCapturePatch.Install(api, capture));
        FirstPersonReflectionCapturePatch.BeginEntityMirrorReplay();
        renderer.DoRender3DOpaque(0.1f, false);
        global::Vintagestory.GameContent.EntityPlayerShapeRenderer alternateRenderer = new(local);
        alternateRenderer.DoRender3DOpaque(0.1f, false);
        renderer.DoRender3DOpaqueBatched(0.1f, false);
        FirstPersonReflectionCapturePatch.EndEntityMirrorReplay();

        Assert.AreEqual(1, renderer.OpaqueDrawCount);
        Assert.AreEqual(0, alternateRenderer.OpaqueDrawCount);
        Assert.AreEqual(1, renderer.BatchedDrawCount);
        Assert.AreEqual(
            global::Vintagestory.GameContent.EntityPlayerShapeRenderer.TestRenderMode.FirstPerson,
            renderer.renderMode);
        Assert.IsTrue(renderer.DoRenderHeldItem);
        Assert.AreSame(originalMesh, renderer.meshRefOpaque);
        Assert.AreSame(originalHandProjection, renderer.pMatrixHandFov);
        Assert.AreSame(originalNormalProjection, renderer.pMatrixNormalFov);
        Assert.IsTrue(logs.Any(message => message.Contains("deferral installed", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(message => message.Contains("Local mirror body path", StringComparison.Ordinal)));
    }

    /// <summary>Contains malformed installed fields at both body-selection and transform boundaries.</summary>
    [TestMethod]
    public void InstalledPatchContainsMalformedBodyAndTransformFields()
    {
        EntityPlayer local = new() { EntityId = 42 };
        ICoreClientAPI api = Api(local, []);
        ReflectionSourceCaptureRenderer capture = new(api);
        global::Vintagestory.GameContent.EntityPlayerShapeRenderer renderer = new(local);

        Assert.IsTrue(FirstPersonReflectionCapturePatch.Install(api, capture));
        FirstPersonReflectionCapturePatch.BeginEntityMirrorReplay();
        SetStatic("renderHeldItemField", GetStatic("renderModeField"));
        InvokePrivate("BeforePlayerOpaqueBatched", renderer, 0.1f, false);
        Assert.AreEqual(0, renderer.BatchedDrawCount);

        SetStatic("mirroredLocalPlayerRenderer", renderer);
        SetStatic("modelMatrixField", typeof(global::Vintagestory.GameContent.EntityPlayerShapeRenderer)
            .GetField("meshRefOpaque", BindingFlags.Instance | BindingFlags.NonPublic));
        InvokePrivate("AfterLoadPlayerModelMatrix", renderer);
        FirstPersonReflectionCapturePatch.EndEntityMirrorReplay();
    }

    /// <summary>Exercises mirror interval telemetry, transform rewriting, and all early batched guards.</summary>
    [TestMethod]
    public void MirrorIntervalTracksTransformAndResetsTelemetry()
    {
        PatchHolder holder = new();
        SetStatic("modelMatrixField", Field(nameof(PatchHolder.ModelMatrix)));
        SetStatic("mirroredLocalPlayerRenderer", holder);

        FirstPersonReflectionCapturePatch.BeginEntityMirrorReplay();
        SetStatic("mirroredLocalPlayerRenderer", holder);
        object?[] prefixArguments = [holder, true];
        InvokePrivate("BeforeLoadPlayerModelMatrix", prefixArguments);
        Assert.AreEqual(false, prefixArguments[1]);

        InvokePrivate("AfterLoadPlayerModelMatrix", holder);
        float[] translation = (float[])GetStatic("MirrorModelTranslation")!;
        CollectionAssert.AreEqual(new[] { 12.0f, 13.0f, 14.0f }, translation);

        InvokePrivate("BeforePlayerOpaqueBatched", holder, 0.1f, true);
        InvokePrivate("BeforePlayerOpaqueBatched", holder, 0.1f, false);
        InvokePrivate("AfterPlayerOpaqueBatched", holder, true);
        InvokePrivate("AfterPlayerOpaqueBatched", holder, false);
        FirstPersonReflectionCapturePatch.EndEntityMirrorReplay();
        FirstPersonReflectionCapturePatch.EndEntityMirrorReplay();

        Assert.AreEqual(0, (int)GetStatic("mirrorOpaqueCalls")!);
        Assert.AreEqual(0, (int)GetStatic("mirrorBatchedCalls")!);
    }

    /// <summary>Exercises successful and incompatible field mutation, including reflection exception guards.</summary>
    [TestMethod]
    public void FieldMutationGuardsContainIncompatibleReflectionFailures()
    {
        PatchHolder holder = new();
        FieldInfo mode = Field(nameof(PatchHolder.Mode));
        FieldInfo boolField = Field(nameof(PatchHolder.Flag));
        FieldInfo constantMode = Field(nameof(PatchHolder.ConstantMode));
        FieldInfo constantFlag = Field(nameof(PatchHolder.ConstantFlag));

        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            new object(), mode, out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            holder, Field(nameof(PatchHolder.NoThirdPersonMode)), out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            holder, constantMode, out _));

        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryRestoreRenderMode(
            new object(), mode, TestRenderMode.FirstPerson));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryRestoreRenderMode(
            holder, constantMode, TestRenderMode.FirstPerson));

        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            new object(), boolField, true));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            holder, constantFlag, true));

        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySelectThirdPersonAnimator(
            new object(), boolField, out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySelectThirdPersonAnimator(
            holder, constantFlag, out _));
    }

    /// <summary>Queues one eligible direct draw, preserves a duplicate, and replays the official method.</summary>
    [TestMethod]
    public void EligibleOpaqueDrawQueuesOnceAndReplaysSuccessfully()
    {
        List<string> logs = [];
        EntityPlayer local = new() { EntityId = 42 };
        ICoreClientAPI api = Api(local, logs);
        ReflectionSourceCaptureRenderer capture = new(api);
        PatchHolder holder = new() { Entity = local };
        SetStatic("capture", capture);
        SetStatic("entityField", Field(nameof(PatchHolder.Entity)));
        SetStatic("opaqueDrawMethod", typeof(PatchHolder).GetMethod(nameof(PatchHolder.Draw)));

        Assert.IsFalse((bool)InvokePrivate("BeforePlayerOpaque", holder, 0.25f, false)!);
        Assert.IsTrue(FirstPersonReflectionCapturePatch.HasDeferredDraw);
        Assert.IsTrue((bool)InvokePrivate("BeforePlayerOpaque", holder, 0.5f, false)!);

        FirstPersonReflectionCapturePatch.ReplayDeferredDraw();
        Assert.AreEqual(1, holder.DrawCount);
        Assert.AreEqual(0.25f, holder.LastDeltaTime);
        Assert.IsFalse(holder.LastShadowPass);
        Assert.IsFalse(FirstPersonReflectionCapturePatch.HasDeferredDraw);
        Assert.AreEqual(0, logs.Count(message => message.Contains("replay failed", StringComparison.Ordinal)));
    }

    /// <summary>Disables deferral after a failed replay and suppresses duplicate failure diagnostics.</summary>
    [TestMethod]
    public void FailedReplayLogsOnceAndDisablesSubsequentDeferral()
    {
        List<string> logs = [];
        EntityPlayer local = new() { EntityId = 42 };
        ICoreClientAPI api = Api(local, logs);
        ReflectionSourceCaptureRenderer capture = new(api);
        PatchHolder holder = new() { Entity = local, ThrowOnDraw = true };
        SetStatic("capture", capture);
        SetStatic("entityField", Field(nameof(PatchHolder.Entity)));
        SetStatic("opaqueDrawMethod", typeof(PatchHolder).GetMethod(nameof(PatchHolder.Draw)));
        SetStatic("deferredRenderer", holder);
        SetStatic("deferredDeltaTime", 0.5f);

        FirstPersonReflectionCapturePatch.ReplayDeferredDraw();
        Assert.IsTrue((bool)GetStatic("deferralDisabled")!);
        Assert.AreEqual(1, logs.Count(message => message.Contains("replay failed", StringComparison.Ordinal)));

        SetStatic("deferredRenderer", holder);
        SetStatic("deferredDeltaTime", 0.75f);
        FirstPersonReflectionCapturePatch.ReplayDeferredDraw();
        Assert.AreEqual(1, logs.Count(message => message.Contains("replay failed", StringComparison.Ordinal)));
        Assert.IsTrue((bool)InvokePrivate("BeforePlayerOpaque", holder, 0.1f, false)!);
    }

    /// <summary>Covers empty replay cleanup and mirror-body failure suppression without engine fields.</summary>
    [TestMethod]
    public void EmptyReplayAndUnavailableMirrorBodyFailOpen()
    {
        FirstPersonReflectionCapturePatch.ReplayDeferredDraw();
        Assert.IsFalse(FirstPersonReflectionCapturePatch.HasDeferredDraw);

        EntityPlayer local = new() { EntityId = 42 };
        List<string> logs = [];
        ReflectionSourceCaptureRenderer capture = new(Api(local, logs));
        PatchHolder holder = new() { Entity = local };
        SetStatic("capture", capture);
        SetStatic("entityField", Field(nameof(PatchHolder.Entity)));
        FirstPersonReflectionCapturePatch.BeginEntityMirrorReplay();

        Assert.IsFalse((bool)InvokePrivate("BeforePlayerOpaque", holder, 0.1f, false)!);
        Assert.IsFalse((bool)InvokePrivate("BeforePlayerOpaque", holder, 0.1f, false)!);
        Assert.AreEqual(1, logs.Count(message => message.Contains("body mode is unavailable", StringComparison.Ordinal)));
        FirstPersonReflectionCapturePatch.EndEntityMirrorReplay();
    }

    /// <summary>Restores every saved field and reports one deliberately incompatible restore.</summary>
    [TestMethod]
    public void RestoreLocalPlayerModeRestoresAllFieldsAndReportsFailure()
    {
        List<string> logs = [];
        ReflectionSourceCaptureRenderer capture = new(Api(new EntityPlayer(), logs));
        PatchHolder holder = new()
        {
            Mode = TestRenderMode.ThirdPerson,
            Flag = false,
            Mesh = new object(),
            HandProjection = new object(),
            NormalProjection = new object()
        };
        object previousMesh = new();
        object previousHand = new();
        object previousNormal = new();
        SetStatic("capture", capture);
        SetStatic("renderModeField", Field(nameof(PatchHolder.Mode)));
        SetStatic("renderHeldItemField", Field(nameof(PatchHolder.Flag)));
        SetStatic("opaqueMeshField", Field(nameof(PatchHolder.Mesh)));
        SetStatic("playerShadowPassField", Field(nameof(PatchHolder.ShadowFlag)));
        SetStatic("handProjectionField", Field(nameof(PatchHolder.HandProjection)));
        SetStatic("normalProjectionField", Field(nameof(PatchHolder.NormalProjection)));
        SetStatic("mirroredLocalPlayerRenderer", holder);
        SetStatic("mirroredLocalPlayerEntity", holder);
        SetStatic("savedLocalPlayerRenderMode", TestRenderMode.FirstPerson);
        SetStatic("savedRenderHeldItem", true);
        SetStatic("savedOpaqueMesh", previousMesh);
        SetStatic("savedPlayerShadowPass", false);
        SetStatic("savedHandProjection", previousHand);
        SetStatic("savedNormalProjection", previousNormal);

        InvokePrivate("RestoreLocalPlayerRenderMode");
        Assert.AreEqual(TestRenderMode.FirstPerson, holder.Mode);
        Assert.IsTrue(holder.Flag);
        Assert.AreSame(previousMesh, holder.Mesh);
        Assert.IsFalse(holder.ShadowFlag);
        Assert.AreSame(previousHand, holder.HandProjection);
        Assert.AreSame(previousNormal, holder.NormalProjection);

        SetStatic("mirroredLocalPlayerRenderer", holder);
        InvokePrivate("RestoreLocalPlayerRenderMode");
        Assert.AreEqual(1, logs.Count(message => message.Contains("Failed to restore", StringComparison.Ordinal)));
    }

    /// <summary>Creates a minimal client identity graph and records patch diagnostics.</summary>
    /// <param name="entity">Local player entity.</param>
    /// <param name="logs">Destination diagnostics.</param>
    /// <returns>A deterministic client API.</returns>
    private static ICoreClientAPI Api(EntityPlayer entity, List<string> logs)
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Warning) or nameof(ILogger.Error) or nameof(ILogger.Notification))
            {
                logs.Add(string.Join("|", arguments ?? []));
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        IClientPlayer player = RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) => method.Name switch
        {
            "get_Entity" => entity,
            "get_CameraMode" => EnumCameraMode.FirstPerson,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>((method, _) =>
            method.Name == "get_Player" ? player : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Logger" => logger,
            "get_World" => world,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Gets one public fixture field.</summary>
    /// <param name="name">Field name.</param>
    /// <returns>The reflected field.</returns>
    private static FieldInfo Field(string name) => typeof(PatchHolder).GetField(name)!;

    /// <summary>Invokes one private patch method.</summary>
    /// <param name="name">Method name.</param>
    /// <param name="arguments">Boxed arguments.</param>
    /// <returns>The boxed result.</returns>
    private static object? InvokePrivate(string name, params object?[] arguments) =>
        typeof(FirstPersonReflectionCapturePatch)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, arguments);

    /// <summary>Assigns one private static patch field.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">Assigned value.</param>
    private static void SetStatic(string name, object? value) =>
        typeof(FirstPersonReflectionCapturePatch)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, value);

    /// <summary>Reads one private static patch field.</summary>
    /// <param name="name">Field name.</param>
    /// <returns>The current boxed value.</returns>
    private static object? GetStatic(string name) =>
        typeof(FirstPersonReflectionCapturePatch)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null);

    /// <summary>Hides only the official renderer identity during the unavailable-build test.</summary>
    /// <param name="name">Type name requested by Harmony.</param>
    /// <param name="__result">Replacement lookup result.</param>
    /// <returns>False for the official renderer so the original lookup is skipped.</returns>
    private static bool HideOfficialRendererType(string name, ref Type? __result)
    {
        if (!string.Equals(
                name,
                "Vintagestory.GameContent.EntityPlayerShapeRenderer",
                StringComparison.Ordinal))
        {
            return true;
        }

        __result = null;
        return false;
    }

    /// <summary>Render modes used by the reflection field contract.</summary>
    private enum TestRenderMode
    {
        /// <summary>Camera-space first-person mode.</summary>
        FirstPerson,
        /// <summary>World-space full-body mode.</summary>
        ThirdPerson
    }

    /// <summary>Enum intentionally missing the required named world-body mode.</summary>
    private enum IncompleteRenderMode
    {
        /// <summary>The only unsupported mode.</summary>
        FirstPerson
    }

    /// <summary>Small reflection target modeling official renderer fields and draw behavior.</summary>
    private sealed class PatchHolder
    {
        /// <summary>Literal enum used to force a contained field-access failure.</summary>
        public const TestRenderMode ConstantMode = TestRenderMode.FirstPerson;
        /// <summary>Literal Boolean used to force a contained field-access failure.</summary>
        public const bool ConstantFlag = false;
        /// <summary>Mutable render mode.</summary>
        public TestRenderMode Mode = TestRenderMode.FirstPerson;
        /// <summary>Enum missing the required third-person name.</summary>
        public IncompleteRenderMode NoThirdPersonMode = IncompleteRenderMode.FirstPerson;
        /// <summary>Mutable held-item flag.</summary>
        public bool Flag = true;
        /// <summary>Mutable animator selector.</summary>
        public bool ShadowFlag = true;
        /// <summary>Mutable opaque mesh slot.</summary>
        public object? Mesh;
        /// <summary>Mutable hand projection.</summary>
        public object? HandProjection;
        /// <summary>Mutable normal projection.</summary>
        public object? NormalProjection;
        /// <summary>Entity identity read by the patch.</summary>
        public Entity? Entity;
        /// <summary>World model transform captured by telemetry.</summary>
        public float[] ModelMatrix =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            12, 13, 14, 1
        ];
        /// <summary>Number of replayed draws.</summary>
        public int DrawCount { get; private set; }
        /// <summary>Last replay delta time.</summary>
        public float LastDeltaTime { get; private set; }
        /// <summary>Last replay shadow flag.</summary>
        public bool LastShadowPass { get; private set; }
        /// <summary>Whether the replay should throw.</summary>
        public bool ThrowOnDraw { get; set; }

        /// <summary>Models the official opaque draw invoked by replay.</summary>
        /// <param name="deltaTime">Deferred frame duration.</param>
        /// <param name="isShadowPass">Deferred shadow flag.</param>
        public void Draw(float deltaTime, bool isShadowPass)
        {
            DrawCount++;
            LastDeltaTime = deltaTime;
            LastShadowPass = isShadowPass;
            if (ThrowOnDraw)
            {
                throw new InvalidOperationException("synthetic replay failure");
            }
        }
    }
}
