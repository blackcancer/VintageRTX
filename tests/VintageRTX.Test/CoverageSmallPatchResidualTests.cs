using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using System.Runtime.CompilerServices;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>Covers residual branch contracts in the two small Harmony guards and mod bootstrap.</summary>
[TestClass]
[DoNotParallelize]
public sealed class CoverageSmallPatchResidualTests
{
    /// <summary>Binding flags used for private static production seams.</summary>
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    /// <summary>Distinct animation manager returned by the scoped Harmony getter override.</summary>
    private static IAnimationManager? forcedAnimationManager;

    /// <summary>Resets process-wide patch state before each deterministic assertion.</summary>
    [TestInitialize]
    public void ResetPatches()
    {
        FirstPersonReflectionCapturePatch.Uninstall();
        FenceStackAwareTessellationGuardPatch.Uninstall();
    }

    /// <summary>Removes every process-wide patch after each assertion.</summary>
    [TestCleanup]
    public void CleanupPatches()
    {
        FirstPersonReflectionCapturePatch.Uninstall();
        FenceStackAwareTessellationGuardPatch.Uninstall();
    }

    /// <summary>Covers null and incompatible field operands in the local-body selectors.</summary>
    [TestMethod]
    public void FirstPersonFieldSelectorsRejectNullAndNonMatchingFields()
    {
        FirstPersonHolder holder = new();
        FieldInfo flag = Field<FirstPersonHolder>(nameof(FirstPersonHolder.Flag));
        FieldInfo number = Field<FirstPersonHolder>(nameof(FirstPersonHolder.Number));

        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            holder,
            null,
            out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            holder,
            flag,
            out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySelectThirdPersonAnimator(
            holder,
            null,
            out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySelectThirdPersonAnimator(
            holder,
            number,
            out _));
    }

    /// <summary>Covers optional identity, transform, batched, and deferral operands independently.</summary>
    [TestMethod]
    public void FirstPersonPrivateGuardsCoverEveryAbsentIdentityOperand()
    {
        FirstPersonHolder holder = new();
        EntityPlayer local = new() { EntityId = 42 };
        EntityPlayer remote = new() { EntityId = 84 };

        Assert.IsTrue((bool)InvokeFirstPerson("BeforePlayerOpaque", holder, 0.1f, false)!);
        SetFirstPerson("entityField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.Entity)));
        holder.Entity = local;
        Assert.IsTrue((bool)InvokeFirstPerson("BeforePlayerOpaque", holder, 0.1f, false)!);

        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(null, EnumCameraMode.FirstPerson, [])));
        Assert.IsTrue((bool)InvokeFirstPerson("BeforePlayerOpaque", holder, 0.1f, false)!);
        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(local, EnumCameraMode.FirstPerson, [])));
        holder.Entity = remote;
        Assert.IsTrue((bool)InvokeFirstPerson("BeforePlayerOpaque", holder, 0.1f, false)!);

        SetFirstPerson("entityMirrorReplayInProgress", true);
        object?[] transformArguments = [holder, true];
        InvokeFirstPerson("BeforeLoadPlayerModelMatrix", transformArguments);
        SetFirstPerson("mirroredLocalPlayerRenderer", new object());
        InvokeFirstPerson("BeforeLoadPlayerModelMatrix", transformArguments);

        InvokeFirstPerson("AfterLoadPlayerModelMatrix", holder);
        SetFirstPerson("mirroredLocalPlayerRenderer", holder);
        InvokeFirstPerson("AfterLoadPlayerModelMatrix", holder);
        SetFirstPerson("modelMatrixField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.ShortMatrix)));
        InvokeFirstPerson("AfterLoadPlayerModelMatrix", holder);
        SetFirstPerson("modelMatrixField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.ModelMatrix)));
        InvokeFirstPerson("AfterLoadPlayerModelMatrix", holder);

        SetFirstPerson("entityMirrorReplayInProgress", false);
        InvokeFirstPerson("BeforePlayerOpaqueBatched", holder, 0.1f, false);
        InvokeFirstPerson("AfterPlayerOpaqueBatched", holder, false);
        SetFirstPerson("entityMirrorReplayInProgress", true);
        InvokeFirstPerson("BeforePlayerOpaqueBatched", holder, 0.1f, true);
        InvokeFirstPerson("AfterPlayerOpaqueBatched", holder, true);

        SetFirstPerson("capture", null);
        InvokeFirstPerson("BeforePlayerOpaqueBatched", holder, 0.1f, false);
        InvokeFirstPerson("AfterPlayerOpaqueBatched", holder, false);
        Assert.IsFalse((bool)InvokeFirstPerson("IsLocalPlayerRenderer", holder)!);

        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(null, EnumCameraMode.FirstPerson, [])));
        Assert.IsFalse((bool)InvokeFirstPerson("IsLocalPlayerRenderer", holder)!);
        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(local, EnumCameraMode.FirstPerson, [])));
        holder.Entity = null;
        Assert.IsFalse((bool)InvokeFirstPerson("IsLocalPlayerRenderer", holder)!);
        holder.Entity = remote;
        Assert.IsFalse((bool)InvokeFirstPerson("IsLocalPlayerRenderer", holder)!);
        holder.Entity = local;
        Assert.IsTrue((bool)InvokeFirstPerson("IsLocalPlayerRenderer", holder)!);

        SetFirstPerson("capture", null);
        SetFirstPerson("entityField", null);
        Assert.IsFalse((bool)InvokeFirstPerson("ShouldDefer", holder, false)!);
        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(null, EnumCameraMode.FirstPerson, [])));
        SetFirstPerson("entityField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.Entity)));
        Assert.IsFalse((bool)InvokeFirstPerson("ShouldDefer", holder, false)!);
        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(local, EnumCameraMode.ThirdPerson, [])));
        Assert.IsFalse((bool)InvokeFirstPerson("ShouldDefer", holder, false)!);
        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(local, EnumCameraMode.FirstPerson, [])));
        Assert.IsTrue((bool)InvokeFirstPerson("ShouldDefer", holder, false)!);
    }

    /// <summary>Covers the non-wrapped reflection failure and logger-free mirror failure branches.</summary>
    [TestMethod]
    public void FirstPersonReplayAndMirrorFailureHandleDirectReflectionErrors()
    {
        FirstPersonHolder holder = new();
        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(new EntityPlayer(), EnumCameraMode.FirstPerson, [])));
        SetFirstPerson("deferredRenderer", holder);
        SetFirstPerson("opaqueDrawMethod", typeof(FirstPersonHolder).GetMethod(nameof(FirstPersonHolder.NoArguments)));
        InvokeFirstPerson("ReplayDeferredDraw");
        Assert.IsTrue((bool)GetFirstPerson("deferralDisabled")!);

        SetFirstPerson("capture", null);
        SetFirstPerson("mirrorBodyFailureLogged", false);
        Assert.IsFalse((bool)InvokeFirstPerson("ReportMirrorBodyFailure")!);

        SetFirstPerson("renderHeldItemField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.Flag)));
        SetFirstPerson("opaqueMeshField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.Mesh)));
        SetFirstPerson("playerShadowPassField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.Flag)));
        SetFirstPerson("handProjectionField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.HandProjection)));
        SetFirstPerson("normalProjectionField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.NormalProjection)));
        SetFirstPerson("renderModeField", Field<FirstPersonHolder>(nameof(FirstPersonHolder.Mode)));
        SetFirstPerson("entityField", null);
        SetFirstPerson("mirrorBodyFailureLogged", false);
        Assert.IsFalse((bool)InvokeFirstPerson("TryUseLocalPlayerWorldBody", holder)!);
    }

    /// <summary>Covers the safety rollback when an engine field no longer selects its TP animator.</summary>
    [TestMethod]
    public void FirstPersonMirrorBodyRollsBackWhenAnimatorSelectionDiverges()
    {
        DivergentEntityPlayer local = new() { EntityId = 42 };
        if (!TryInstallDistinctThirdPersonAnimator(local))
        {
            Assert.Fail("The Vintage Story EntityPlayer fixture exposes no mutable TP animator carrier.");
        }

        global::Vintagestory.GameContent.EntityPlayerShapeRenderer renderer = new(local);
        List<string> logs = [];
        SetFirstPerson("capture", new ReflectionSourceCaptureRenderer(Api(local, EnumCameraMode.FirstPerson, logs)));
        SetFirstPerson("entityField", typeof(global::Vintagestory.GameContent.EntityPlayerShapeRenderer)
            .GetField("entity", BindingFlags.Instance | BindingFlags.NonPublic));
        SetFirstPerson("renderModeField", typeof(global::Vintagestory.GameContent.EntityPlayerShapeRenderer)
            .GetField("renderMode", BindingFlags.Instance | BindingFlags.NonPublic));
        SetFirstPerson("renderHeldItemField", typeof(global::Vintagestory.GameContent.EntityPlayerShapeRenderer)
            .GetField("DoRenderHeldItem", BindingFlags.Instance | BindingFlags.NonPublic));
        SetFirstPerson("opaqueMeshField", typeof(global::Vintagestory.GameContent.EntityPlayerShapeRenderer)
            .GetField("meshRefOpaque", BindingFlags.Instance | BindingFlags.NonPublic));
        SetFirstPerson("handProjectionField", typeof(global::Vintagestory.GameContent.EntityPlayerShapeRenderer)
            .GetField("pMatrixHandFov", BindingFlags.Instance | BindingFlags.NonPublic));
        SetFirstPerson("normalProjectionField", typeof(global::Vintagestory.GameContent.EntityPlayerShapeRenderer)
            .GetField("pMatrixNormalFov", BindingFlags.Instance | BindingFlags.NonPublic));
        SetFirstPerson("playerShadowPassField", Field<DivergentEntityPlayer>(
            nameof(DivergentEntityPlayer.UnrelatedSelector)));

        forcedAnimationManager = RuntimeHelpers.GetUninitializedObject(
            typeof(EntityPlayer).GetProperty(nameof(EntityPlayer.TpAnimManager))!.PropertyType)
            as IAnimationManager;
        Assert.IsNotNull(forcedAnimationManager);
        Harmony animatorOverride = new("vintagertx.test.divergent-animator");
        animatorOverride.Patch(
            typeof(EntityPlayer).GetProperty(nameof(EntityPlayer.AnimManager))!.GetMethod!,
            prefix: new HarmonyMethod(
                typeof(CoverageSmallPatchResidualTests),
                nameof(ReturnForcedAnimationManager)));
        try
        {
            Assert.IsFalse((bool)InvokeFirstPerson("TryUseLocalPlayerWorldBody", renderer)!);
            Assert.IsNull(GetFirstPerson("mirroredLocalPlayerRenderer"));
            Assert.IsTrue(logs.Any(static entry => entry.Contains("body mode is unavailable", StringComparison.Ordinal)));
        }
        finally
        {
            animatorOverride.UnpatchAll(animatorOverride.Id);
            forcedAnimationManager = null;
        }
    }

    /// <summary>Covers exact-field rejection, Harmony installation failure, and public resolver entry.</summary>
    [TestMethod]
    public void FenceInstallContainsHarmonyFailureAndLateFieldMismatch()
    {
        List<string> logs = [];
        ICoreClientAPI api = FenceApi(logs);
        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.Install(api));

        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.Install(
            api,
            name => name.EndsWith("BlockFenceStackAware", StringComparison.Ordinal)
                ? typeof(FenceWrongCodeFixture)
                : typeof(FenceNeighborFixture)));

        Harmony blocker = new("vintagertx.test.reject-harmony-patch");
        MethodInfo patchMethod = typeof(Harmony).GetMethod(
            nameof(Harmony.Patch),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(MethodBase),
                typeof(HarmonyMethod),
                typeof(HarmonyMethod),
                typeof(HarmonyMethod),
                typeof(HarmonyMethod)
            ],
            modifiers: null) ?? throw new MissingMethodException(typeof(Harmony).FullName, nameof(Harmony.Patch));
        blocker.Patch(
            patchMethod,
            prefix: new HarmonyMethod(
                typeof(CoverageSmallPatchResidualTests),
                nameof(RejectHarmonyPatch)));
        try
        {
            Assert.IsFalse(FenceStackAwareTessellationGuardPatch.Install(api, ResolveFenceFixture));
            Assert.IsTrue(logs.Any(static entry => entry.Contains(
                "could not be installed",
                StringComparison.Ordinal)));
        }
        finally
        {
            blocker.UnpatchAll(blocker.Id);
        }
    }

    /// <summary>Covers cache-field absence, null prefixes, cache hits, and baked optional carriers.</summary>
    [TestMethod]
    public void FenceCacheAndBakedVariantGuardsCoverEveryOptionalCarrier()
    {
        FenceContractFixture fixture = new();
        BlockPos pos = new(3, 4, 5);
        object?[] cacheArguments = [fixture, pos, false];
        Assert.IsFalse((bool)InvokeFence("TryResolveCachedMesh", cacheArguments)!);

        SetFence("continuousFenceMeshesField", Field<FenceContractFixture>("continousFenceMeches"));
        Assert.IsFalse((bool)InvokeFence("TryResolveCachedMesh", cacheArguments)!);
        SetFence("continuousFenceCodeField", Field<FenceContractFixture>("cntCode"));
        Assert.IsTrue((bool)InvokeFence("TryResolveCachedMesh", cacheArguments)!);
        Assert.AreEqual(false, cacheArguments[2]);
        fixture.AddCached(pos);
        cacheArguments[2] = false;
        Assert.IsTrue((bool)InvokeFence("TryResolveCachedMesh", cacheArguments)!);
        Assert.AreEqual(true, cacheArguments[2]);

        FenceNullCodeFixture nullCode = new();
        SetFence("continuousFenceMeshesField", Field<FenceNullCodeFixture>("continousFenceMeches"));
        SetFence("continuousFenceCodeField", Field<FenceNullCodeFixture>("cntCode"));
        object?[] nullCodeArguments = [nullCode, pos, false];
        Assert.IsTrue((bool)InvokeFence("TryResolveCachedMesh", nullCodeArguments)!);

        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.HasValidBakedVariantAccess(
            pos,
            new CompositeTexture(),
            out _,
            out _));
        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.HasValidBakedVariantAccess(
            pos,
            new CompositeTexture { Baked = new BakedCompositeTexture() },
            out _,
            out _));
    }

    /// <summary>Covers bounded warning formatting and every atomic counter result class.</summary>
    [TestMethod]
    public void FenceWarningCounterPublishesDetailedSaturatedAndSuppressedStates()
    {
        List<string> logs = [];
        ILogger logger = Logger(logs);

        SetFence("boundedReportCount", 0);
        SetFence("logger", null);
        InvokeFence("ReportSuppressedAccess", FenceTessellationUnsafeArray.MoveIndexTable, 1, 2, null, -1, -1);

        SetFence("boundedReportCount", 0);
        SetFence("logger", logger);
        InvokeFence("ReportSuppressedAccess", FenceTessellationUnsafeArray.MoveIndexTable, 1, 2, null, -1, -1);
        InvokeFence("ReportSuppressedAccess", FenceTessellationUnsafeArray.MoveIndexTable, 1, 2, new[] { 0 }, -1, -1);
        InvokeFence("ReportSuppressedAccess", FenceTessellationUnsafeArray.ExtendedChunkNeighbor, 1, 2, new[] { 0, 0, 0, 7 }, -1, -1);
        InvokeFence("ReportSuppressedAccess", FenceTessellationUnsafeArray.BakedTextureVariant, 1, 2, new[] { 0, 0, 0, 7 }, 4, 2);
        InvokeFence("ReportSuppressedAccess", FenceTessellationUnsafeArray.BakedTextureVariant, 1, 2, new[] { 0, 0, 0, 7 }, 4, 2);
        SetFence("boundedReportCount", 8);
        InvokeFence("ReportSuppressedAccess", FenceTessellationUnsafeArray.BakedTextureVariant, 1, 2, new[] { 0, 0, 0, 7 }, 4, 2);
        Assert.IsTrue(logs.Any(static entry => entry.Contains("Further unsafe", StringComparison.Ordinal)));

        SetFence("boundedReportCount", 0);
        Assert.AreEqual(1, (int)InvokeFence("IncrementSaturatedReportCount")!);
        SetFence("boundedReportCount", 9);
        Assert.AreEqual(0, (int)InvokeFence("IncrementSaturatedReportCount")!);
        SetFence("boundedReportCount", int.MaxValue);
        Assert.AreEqual(0, (int)InvokeFence("IncrementSaturatedReportCount")!);
    }

    /// <summary>Covers the bootstrap readiness lambda and a player whose synchronized data is absent.</summary>
    [TestMethod]
    public void ModSystemStartupReadinessHandlesGeneratedDelegateAndNullWorldData()
    {
        VintageRtxModSystem system = new();
        MethodInfo readiness = typeof(VintageRtxModSystem)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(static method => method.Name.Contains("<StartClientSide>", StringComparison.Ordinal)
                && method.ReturnType == typeof(bool)
                && method.GetParameters().Length == 0)
            .Single(method => CanInvokeBoolean(system, method));
        Assert.IsTrue((bool)readiness.Invoke(system, null)!);
        SetInstance(system, "startupWorldStateReady", false);
        Assert.IsFalse((bool)readiness.Invoke(system, null)!);

        EntityPlayer entity = new();
        IClientPlayer player = RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) => method.Name switch
        {
            "get_Entity" => entity,
            "get_WorldData" => null,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>((method, _) =>
            method.Name == "get_Player" ? player : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_World" ? world : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        SetInstance(system, "api", api);
        SetInstance(system, "startupGameModeCommandSent", true);
        SetInstance(system, "startupGameMode", 2);
        InvokeInstance(system, "ApplyStartupWorldState", 0.1f);
        Assert.IsFalse((bool)GetInstance(system, "startupWorldStateReady")!);
    }

    /// <summary>Returns whether one generated boolean method can execute without initialized renderers.</summary>
    /// <param name="instance">Mod-system instance.</param>
    /// <param name="method">Generated candidate.</param>
    /// <returns>True only for the startup-ready field accessor.</returns>
    private static bool CanInvokeBoolean(VintageRtxModSystem instance, MethodInfo method)
    {
        try
        {
            return method.Invoke(instance, null) is bool;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    /// <summary>Creates a reflection capture API with configurable local identity.</summary>
    /// <param name="local">Optional local entity.</param>
    /// <param name="cameraMode">Reported camera mode.</param>
    /// <param name="logs">Diagnostic destination.</param>
    /// <returns>Client API proxy.</returns>
    private static ICoreClientAPI Api(EntityPlayer? local, EnumCameraMode cameraMode, List<string> logs)
    {
        ILogger logger = Logger(logs);
        IClientPlayer? player = local is null
            ? null
            : RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) => method.Name switch
            {
                "get_Entity" => local,
                "get_CameraMode" => cameraMode,
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

    /// <summary>Creates a logger that retains warning, error, and notification payloads.</summary>
    /// <param name="logs">Diagnostic destination.</param>
    /// <returns>Logger proxy.</returns>
    private static ILogger Logger(List<string> logs) =>
        RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Warning) or nameof(ILogger.Error) or nameof(ILogger.Notification))
            {
                logs.Add(string.Join("|", arguments ?? []));
            }
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

    /// <summary>Creates the minimal API used by fence installation diagnostics.</summary>
    /// <param name="logs">Diagnostic destination.</param>
    /// <returns>Client API proxy.</returns>
    private static ICoreClientAPI FenceApi(List<string> logs)
    {
        ILogger logger = Logger(logs);
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_Logger" ? logger : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
    }

    /// <summary>Rejects a Harmony patch operation after the test blocker has been installed.</summary>
    /// <exception cref="InvalidOperationException">Always thrown to exercise installation containment.</exception>
    private static void RejectHarmonyPatch() =>
        throw new InvalidOperationException("intentional Harmony installation rejection");

    /// <summary>Returns the scoped animation-manager mismatch without executing the engine getter.</summary>
    /// <param name="__result">Replacement active animation manager.</param>
    /// <returns>False so Harmony skips the original getter.</returns>
    private static bool ReturnForcedAnimationManager(ref IAnimationManager? __result)
    {
        __result = forcedAnimationManager;
        return false;
    }

    /// <summary>Maps production survival type names to exact test fixtures.</summary>
    /// <param name="name">Runtime-qualified type name.</param>
    /// <returns>Matching fixture type.</returns>
    private static Type? ResolveFenceFixture(string name) => name switch
    {
        "Vintagestory.GameContent.BlockFenceStackAware" => typeof(FenceContractFixture),
        "Vintagestory.GameContent.BlockFence" => typeof(FenceNeighborFixture),
        _ => null
    };

    /// <summary>Installs a distinct TP animator instance into a player through its runtime carrier.</summary>
    /// <param name="player">Player receiving the carrier.</param>
    /// <returns>Whether a mutable property or field accepted the instance.</returns>
    private static bool TryInstallDistinctThirdPersonAnimator(EntityPlayer player)
    {
        PropertyInfo? property = typeof(EntityPlayer).GetProperty(
            nameof(EntityPlayer.TpAnimManager),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is null || property.PropertyType.IsAbstract)
        {
            return false;
        }
        object animator = RuntimeHelpers.GetUninitializedObject(property.PropertyType);
        if (property.SetMethod is not null)
        {
            property.SetValue(player, animator);
            return !ReferenceEquals(player.AnimManager, player.TpAnimManager);
        }

        FieldInfo? thirdPersonField = null;
        FieldInfo? firstPersonField = null;
        for (Type? type = typeof(EntityPlayer); type is not null; type = type.BaseType)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            thirdPersonField ??= fields.FirstOrDefault(candidate =>
                candidate.FieldType == property.PropertyType
                && string.Equals(candidate.Name, "animManager", StringComparison.Ordinal));
            firstPersonField ??= fields.FirstOrDefault(candidate =>
                candidate.FieldType == property.PropertyType
                && string.Equals(candidate.Name, "selfFpAnimManager", StringComparison.Ordinal));
        }
        if (thirdPersonField is not null && firstPersonField is not null)
        {
            thirdPersonField.SetValue(player, animator);
            firstPersonField.SetValue(
                player,
                RuntimeHelpers.GetUninitializedObject(property.PropertyType));
            return true;
        }
        return false;
    }

    /// <summary>Gets a field declared on a deterministic fixture.</summary>
    /// <typeparam name="T">Fixture type.</typeparam>
    /// <param name="name">Exact field name.</param>
    /// <returns>Reflected field.</returns>
    private static FieldInfo Field<T>(string name) => typeof(T).GetField(
        name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(T).FullName, name);

    /// <summary>Invokes one private FirstPerson patch method.</summary>
    /// <param name="name">Exact method name.</param>
    /// <param name="arguments">Method arguments.</param>
    /// <returns>Boxed result.</returns>
    private static object? InvokeFirstPerson(string name, params object?[] arguments) =>
        typeof(FirstPersonReflectionCapturePatch).GetMethod(name, PrivateStatic)!
            .Invoke(null, arguments);

    /// <summary>Assigns one private FirstPerson patch field.</summary>
    /// <param name="name">Exact field name.</param>
    /// <param name="value">Assigned value.</param>
    private static void SetFirstPerson(string name, object? value) =>
        typeof(FirstPersonReflectionCapturePatch).GetField(name, PrivateStatic)!.SetValue(null, value);

    /// <summary>Reads one private FirstPerson patch field.</summary>
    /// <param name="name">Exact field name.</param>
    /// <returns>Current value.</returns>
    private static object? GetFirstPerson(string name) =>
        typeof(FirstPersonReflectionCapturePatch).GetField(name, PrivateStatic)!.GetValue(null);

    /// <summary>Invokes one private fence patch method.</summary>
    /// <param name="name">Exact method name.</param>
    /// <param name="arguments">Method arguments.</param>
    /// <returns>Boxed result.</returns>
    private static object? InvokeFence(string name, params object?[] arguments) =>
        typeof(FenceStackAwareTessellationGuardPatch).GetMethod(name, PrivateStatic)!
            .Invoke(null, arguments);

    /// <summary>Assigns one private fence patch field.</summary>
    /// <param name="name">Exact field name.</param>
    /// <param name="value">Assigned value.</param>
    private static void SetFence(string name, object? value) =>
        typeof(FenceStackAwareTessellationGuardPatch).GetField(name, PrivateStatic)!.SetValue(null, value);

    /// <summary>Invokes one private instance method.</summary>
    /// <param name="instance">Method target.</param>
    /// <param name="name">Exact method name.</param>
    /// <param name="arguments">Method arguments.</param>
    private static void InvokeInstance(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments);

    /// <summary>Assigns one private instance field.</summary>
    /// <param name="instance">Field owner.</param>
    /// <param name="name">Exact field name.</param>
    /// <param name="value">Assigned value.</param>
    private static void SetInstance(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    /// <summary>Reads one private instance field.</summary>
    /// <param name="instance">Field owner.</param>
    /// <param name="name">Exact field name.</param>
    /// <returns>Current value.</returns>
    private static object? GetInstance(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);

    /// <summary>Render modes used by the local reflection fixture.</summary>
    private enum LocalRenderMode
    {
        /// <summary>Camera-space mode.</summary>
        FirstPerson,
        /// <summary>World-body mode.</summary>
        ThirdPerson
    }

    /// <summary>Reflection target containing every field used by focused guard calls.</summary>
    private sealed class FirstPersonHolder
    {
        /// <summary>Mutable render mode.</summary>
        public LocalRenderMode Mode = LocalRenderMode.FirstPerson;
        /// <summary>Boolean selection field.</summary>
        public bool Flag = true;
        /// <summary>Non-Boolean incompatible field.</summary>
        public int Number = 1;
        /// <summary>Rendered entity identity.</summary>
        public Entity? Entity;
        /// <summary>Opaque mesh carrier.</summary>
        public object? Mesh = new();
        /// <summary>Hand projection carrier.</summary>
        public object? HandProjection = new();
        /// <summary>Normal projection carrier.</summary>
        public object? NormalProjection = new();
        /// <summary>Intentionally undersized transform.</summary>
        public float[] ShortMatrix = new float[4];
        /// <summary>Complete telemetry transform.</summary>
        public float[] ModelMatrix = new float[16];

        /// <summary>Parameterless method deliberately incompatible with deferred replay arguments.</summary>
        public void NoArguments()
        {
        }
    }

    /// <summary>Player with a Boolean unrelated to the official animation-manager selector.</summary>
    private sealed class DivergentEntityPlayer : EntityPlayer
    {
        /// <summary>Selector used to prove a stale engine field cannot satisfy the animator contract.</summary>
        public bool UnrelatedSelector = true;
    }

    /// <summary>Exact runtime fence-stack contract used for Harmony installation and cache reads.</summary>
    private sealed class FenceContractFixture : Block
    {
        /// <summary>Authored top-mesh cache using the engine&apos;s misspelled runtime field name.</summary>
        private readonly Dictionary<string, MeshData> continousFenceMeches = [];
        /// <summary>Stable cache prefix.</summary>
        private readonly string cntCode = "fixture-";

        /// <summary>Adds the exact authored cache key for one position.</summary>
        /// <param name="pos">World position used by the engine hash.</param>
        public void AddCached(BlockPos pos)
        {
            int variant = GameMath.MurmurHash3Mod(pos.X, pos.Y, pos.Z, 8) + 1;
            continousFenceMeches[cntCode + variant.ToString()] = new MeshData();
        }

        /// <summary>Exact by-reference method signature expected by the production resolver.</summary>
        private new void OnJsonTesselation(
            ref MeshData mesh,
            ref int[] textureIds,
            BlockPos pos,
            Block[] blocks,
            int index)
        {
            _ = mesh;
            _ = textureIds;
            _ = pos;
            _ = blocks;
            _ = index;
        }
    }

    /// <summary>Exact target method whose late code field has the wrong type.</summary>
    private sealed class FenceWrongCodeFixture : Block
    {
        /// <summary>Correct mesh cache proving evaluation reaches the later code-field operand.</summary>
        private readonly Dictionary<string, MeshData> continousFenceMeches = [];
        /// <summary>Deliberately incompatible cache prefix.</summary>
        public int cntCode = 1;

        /// <summary>Reads the incompatible field so the fixture remains warning-clean.</summary>
        /// <returns>Current numeric prefix.</returns>
        public int ReadCode() => cntCode;

        /// <summary>Exact by-reference method signature expected by the production resolver.</summary>
        private new void OnJsonTesselation(
            ref MeshData mesh,
            ref int[] textureIds,
            BlockPos pos,
            Block[] blocks,
            int index)
        {
            _ = mesh;
            _ = textureIds;
            _ = pos;
            _ = blocks;
            _ = index;
        }
    }

    /// <summary>Cache fixture whose optional code prefix is null.</summary>
    private sealed class FenceNullCodeFixture
    {
        /// <summary>Empty cache used with the null prefix.</summary>
        private readonly Dictionary<string, MeshData> continousFenceMeches = [];
        /// <summary>Deliberately absent optional cache prefix.</summary>
        public string? cntCode = null;

        /// <summary>Reads the absent field so the fixture remains warning-clean.</summary>
        /// <returns>Current nullable prefix.</returns>
        public string? ReadCode() => cntCode;
    }

    /// <summary>Fence-neighbor identity accepted by the stack-aware type check.</summary>
    private sealed class FenceNeighborFixture : Block
    {
    }
}
