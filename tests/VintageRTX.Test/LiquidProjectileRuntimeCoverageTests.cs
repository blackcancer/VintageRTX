using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Exercises the exact <see cref="IProjectile"/> liquid-contact bridge with detached deterministic
/// entities, including real arrow identity, reconstructed collision position, nesting, and cleanup.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiquidProjectileRuntimeCoverageTests
{
    /// <summary>
    /// Verifies a real arrow-shaped <see cref="IProjectile"/> is converted to SI once and that the
    /// previous position is reconstructed from the same fast-physics substep used by the engine.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ArrowProjectileSampleReconstructsCollisionPositionAndPhysicalImpulse()
    {
        TestProjectile arrow = CreateProjectile(
            entityId: 8101,
            code: "arrow-copper",
            worldSide: EnumAppSide.Server);
        arrow.Pos.SetPos(12.5, 4.75, -3.25);
        arrow.Pos.Motion.Set(0.20, -0.15, 0.05);
        Vec3d incident = new(0.25, -0.30, 0.10);

        LiquidProjectileCollisionSample sample =
            LiquidSurfaceWorldInputs.SampleProjectileLiquidCollision(arrow, incident);

        double scale = LiquidSurfaceWorldInputs.LiquidCollisionSubstepDisplacementScale(
            arrow.Pos.Motion);
        Assert.AreEqual(8101L, sample.EntityId);
        Assert.AreEqual(LiquidEntitySurfaceClass.Projectile, sample.SurfaceClass);
        Assert.AreEqual(12.5 - 0.20 * scale, sample.PreviousWorldX, 1.0e-12);
        Assert.AreEqual(4.75 + 0.15 * scale, sample.PreviousWorldY, 1.0e-12);
        Assert.AreEqual(-3.25 - 0.05 * scale, sample.PreviousWorldZ, 1.0e-12);
        Assert.AreEqual(0.25f, sample.IncidentMotionX);
        Assert.AreEqual(-0.30f, sample.IncidentMotionY);
        Assert.AreEqual(0.10f, sample.IncidentMotionZ);
        Assert.AreEqual(LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms, sample.MassKilograms);
        Assert.IsTrue(sample.IsServerAuthoritative);

        Assert.ThrowsException<ArgumentNullException>(
            () => LiquidSurfaceWorldInputs.SampleProjectileLiquidCollision(null!, incident));
        Assert.ThrowsException<ArgumentNullException>(
            () => LiquidSurfaceWorldInputs.SampleProjectileLiquidCollision(arrow, null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidSurfaceWorldInputs.SampleProjectileLiquidCollision(arrow, incident, 0.0f));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidSurfaceWorldInputs.SampleProjectileLiquidCollision(arrow, incident, float.NaN));
    }

    /// <summary>
    /// Covers unresolved projectile-stack world lookup, stone promotion, and invalid generic entity
    /// mass fallback without depending on a running game registry.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ProjectileIdentityUsesWorldLookupAndInvalidEntityMassFallsBack()
    {
        Item arrowItem = new() { Code = new AssetLocation("game", "arrow-flint") };
        Block stoneBlock = new() { Code = new AssetLocation("game", "stone-granite") };
        IWorldAccessor world = CreateWorld(
            EnumAppSide.Client,
            item: arrowItem,
            block: stoneBlock);

        TestProjectile unresolvedArrow = new()
        {
            EntityId = 8201,
            World = world,
            ProjectileStack = new ItemStack
            {
                Class = EnumItemClass.Item,
                Id = 41,
                StackSize = 1
            }
        };
        SetEntityProperties(unresolvedArrow, "generic-projectile", 1.0f);
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            LiquidSurfaceWorldInputs.SampleEntity(unresolvedArrow).MassKilograms);

        RetainedStoneProjectile unresolvedStone = new()
        {
            EntityId = 8202,
            World = world,
            ProjectileStack = new ItemStack
            {
                Class = EnumItemClass.Block,
                Id = 42,
                StackSize = 1
            }
        };
        SetEntityProperties(unresolvedStone, "generic-projectile", 1.0f);
        Assert.AreEqual(
            LiquidEntitySurfaceClass.ThrownStone,
            LiquidSurfaceWorldInputs.ClassifyEntity(unresolvedStone));

        EntityPlayer invalidMass = new();
        SetEntityProperties(invalidMass, "player", float.NaN);
        Assert.AreEqual(25.0f, LiquidSurfaceWorldInputs.SampleEntity(invalidMass).MassKilograms);
    }

    /// <summary>
    /// Drives Harmony callback bodies directly so one outer arrow entry emits exactly once, nested
    /// base callbacks do not duplicate it, and exceptional callbacks restore thread-local depth.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void PatchCallbacksEmitOneArrowEntryAndRestoreNestedDepth()
    {
        List<LiquidProjectileCollisionSample> emitted = [];
        ProjectileLiquidCollisionPatch.Uninstall();
        SetPatchField("collisionSink", (Action<LiquidProjectileCollisionSample>)emitted.Add);
        TestProjectile arrow = CreateProjectile(8301, "arrow-bronze", EnumAppSide.Client);
        arrow.Pos.SetPos(1.5, 0.80, 2.5);
        arrow.Pos.Motion.Set(0.20, -0.20, 0.05);

        try
        {
            LiquidProjectileCollisionSample outer = InvokeBefore(arrow);
            arrow.Pos.Motion.Set(double.MaxValue, double.NaN, double.NegativeInfinity);
            InvokeAfter(arrow, outer);

            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual(float.MaxValue, emitted[0].OutgoingMotionX);
            Assert.AreEqual(0.0f, emitted[0].OutgoingMotionY);
            Assert.AreEqual(0.0f, emitted[0].OutgoingMotionZ);
            Assert.AreEqual(LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms, emitted[0].MassKilograms);
            InvokeAfter(arrow, outer);
            Assert.AreEqual(1, emitted.Count, "A completed outer callback must not emit twice.");

            arrow.Pos.Motion.Set(0.10, -0.10, 0.0);
            LiquidProjectileCollisionSample nestedOuter = InvokeBefore(arrow);
            LiquidProjectileCollisionSample nestedInner = InvokeBefore(arrow);
            Assert.AreEqual(default, nestedInner);
            InvokeAfter(arrow, nestedInner);
            Assert.AreEqual(1, emitted.Count);
            InvokeAfter(arrow, nestedOuter);
            Assert.AreEqual(2, emitted.Count, "Only the outermost nested callback may emit.");

            LiquidProjectileCollisionSample failed = InvokeBefore(arrow);
            InvalidOperationException expected = new("fixture physics failure");
            Exception? returned = InvokeFinalizer(arrow, expected);
            Assert.AreSame(expected, returned);
            InvokeAfter(arrow, failed);
            Assert.AreEqual(2, emitted.Count, "A failed engine callback must not become an impulse.");

            EntityPlayer nonProjectile = new();
            Assert.AreEqual(default, InvokeBefore(nonProjectile));
            TestProjectile noWorld = CreateProjectile(8302, "arrow-flint", EnumAppSide.Client);
            noWorld.World = null!;
            Assert.AreEqual(default, InvokeBefore(noWorld));
            Assert.IsNull(InvokeFinalizer(nonProjectile, null));
        }
        finally
        {
            ProjectileLiquidCollisionPatch.Uninstall();
        }
    }

    /// <summary>
    /// Installs the real Harmony bridge, invokes the public engine callback on an arrow projectile,
    /// and proves the installed prefix/postfix pair publishes one detached collision.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void InstalledHarmonyBridgeObservesPublicArrowLiquidCallback()
    {
        List<string> logs = [];
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Notification) or nameof(ILogger.Warning))
            {
                logs.Add(arguments?[0]?.ToString() ?? string.Empty);
            }
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_Logger"
                ? logger
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        List<LiquidProjectileCollisionSample> emitted = [];
        TestProjectile arrow = CreateProjectile(8351, "arrow-flint", EnumAppSide.Client);
        arrow.Pos.SetPos(2.0, 0.75, 3.0);
        arrow.Pos.Motion.Set(0.1, -0.2, 0.0);

        ProjectileLiquidCollisionPatch.Uninstall();
        try
        {
            Assert.ThrowsException<ArgumentNullException>(
                () => ProjectileLiquidCollisionPatch.Install(null!, emitted.Add));
            Assert.ThrowsException<ArgumentNullException>(
                () => ProjectileLiquidCollisionPatch.Install(api, null!));
            Assert.IsTrue(ProjectileLiquidCollisionPatch.Install(api, emitted.Add));

            arrow.OnCollideWithLiquid();

            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual(8351L, emitted[0].EntityId);
            Assert.AreEqual(LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms, emitted[0].MassKilograms);
            Assert.IsTrue(logs.Any(log => log.Contains("bridge installed", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(log => log.Contains("could not be patched", StringComparison.Ordinal)));
        }
        finally
        {
            ProjectileLiquidCollisionPatch.Uninstall();
        }
    }

    /// <summary>
    /// Verifies retained thrown-stone motion, cache fallback, and all assembly type-enumeration
    /// recovery paths used while discovering modded projectile overrides.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void RetainedIncidentMotionAndLoadableTypeRecoveryAreDeterministic()
    {
        ProjectileLiquidCollisionPatch.Uninstall();
        RetainedStoneProjectile stone = new();
        stone.Pos.Motion.Set(0.1, -0.2, 0.3);
        stone.SetRetainedMotion(new Vec3d(0.4, -0.5, 0.6));
        Vec3d retained = ProjectileLiquidCollisionPatch.ResolveIncidentMotion(stone);
        Assert.AreEqual(0.4, retained.X, 0.0);
        Assert.AreEqual(-0.5, retained.Y, 0.0);
        Assert.AreEqual(0.6, retained.Z, 0.0);
        Assert.AreSame(retained, ProjectileLiquidCollisionPatch.ResolveIncidentMotion(stone));

        TestProjectile fallback = new();
        fallback.Pos.Motion.Set(-0.7, 0.8, -0.9);
        Assert.AreSame(fallback.Pos.Motion, ProjectileLiquidCollisionPatch.ResolveIncidentMotion(fallback));

        Type[] normal = InvokeGetLoadableTypes(typeof(LiquidProjectileRuntimeCoverageTests).Assembly);
        CollectionAssert.Contains(normal, typeof(LiquidProjectileRuntimeCoverageTests));
        Type[] partial = InvokeGetLoadableTypes(new FaultingAssembly(FaultKind.Partial));
        CollectionAssert.AreEqual(new[] { typeof(string) }, partial);
        Assert.AreEqual(0, InvokeGetLoadableTypes(new FaultingAssembly(FaultKind.Total)).Length);
        ProjectileLiquidCollisionPatch.Uninstall();
    }

    /// <summary>
    /// Covers thrown-stone prefix selection, both early postfix ownership guards, dynamic callback
    /// diagnostics, and deterministic zero-target installation containment.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void PatchResidualGuardsRetainStoneMotionAndContainUnavailableInstallation()
    {
        List<string> logs = [];
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Notification) or nameof(ILogger.Warning))
            {
                logs.Add(arguments?[0]?.ToString() ?? string.Empty);
            }
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_Logger"
                ? logger
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        Item stoneItem = new() { Code = new AssetLocation("game", "thrownboulder-granite") };
        RetainedStoneProjectile stone = new()
        {
            EntityId = 8401,
            World = CreateWorld(EnumAppSide.Client, stoneItem, new Block()),
            ProjectileStack = new ItemStack(stoneItem, 1),
        };
        SetEntityProperties(stone, "thrownboulder-granite", 1.0f);
        stone.Pos.SetPos(0.25, 0.80, 0.25);
        stone.Pos.Motion.Set(0.10, -0.20, 0.0);
        stone.SetRetainedMotion(new Vec3d(0.40, -0.50, 0.10));

        ProjectileLiquidCollisionPatch.Uninstall();
        SetPatchField("collisionSink", (Action<LiquidProjectileCollisionSample>)(_ => { }));
        try
        {
            LiquidProjectileCollisionSample state = InvokeBefore(stone);
            Assert.AreEqual(LiquidEntitySurfaceClass.ThrownStone, state.SurfaceClass);
            Assert.AreEqual(0.40f, state.IncidentMotionX);
            InvokeAfter(stone, state);

            InvokeAfter(new EntityPlayer(), default);
            TestProjectile worldless = new();
            InvokeAfter(worldless, default);
        }
        finally
        {
            ProjectileLiquidCollisionPatch.Uninstall();
        }

        Assert.IsFalse(ProjectileLiquidCollisionPatch.FinishInstallation(api, patched: 0));
        Assert.IsTrue(logs.Any(log => log.Contains("unavailable", StringComparison.Ordinal)));
        System.Reflection.Emit.DynamicMethod dynamicCallback = new(
            "FixtureDynamicLiquidCallback",
            typeof(void),
            Type.EmptyTypes);
        Assert.AreEqual(
            "<unknown>",
            ProjectileLiquidCollisionPatch.DescribeDeclaringType(dynamicCallback));
        Assert.AreEqual(
            typeof(Entity).FullName,
            ProjectileLiquidCollisionPatch.DescribeDeclaringType(
                typeof(Entity).GetMethod(nameof(Entity.OnCollideWithLiquid))!));
        Assert.ThrowsException<ArgumentNullException>(() =>
            ProjectileLiquidCollisionPatch.DescribeDeclaringType(null!));
    }

    /// <summary>Creates a test projectile with a resolved collectible identity and detached world.</summary>
    /// <param name="entityId">Stable entity identity.</param>
    /// <param name="code">Projectile collectible path.</param>
    /// <param name="worldSide">Client or integrated-server provenance.</param>
    /// <returns>Configured public projectile contract.</returns>
    private static TestProjectile CreateProjectile(long entityId, string code, EnumAppSide worldSide)
    {
        Item item = new() { Code = new AssetLocation("game", code) };
        TestProjectile projectile = new()
        {
            EntityId = entityId,
            World = CreateWorld(worldSide, item, new Block()),
            ProjectileStack = new ItemStack(item, 1)
        };
        SetEntityProperties(projectile, "generic-projectile", 1.0f);
        return projectile;
    }

    /// <summary>Creates the minimal world surface required by projectile identity and provenance.</summary>
    /// <param name="side">World application side.</param>
    /// <param name="item">Item returned for unresolved item stacks.</param>
    /// <param name="block">Block returned for unresolved block stacks.</param>
    /// <returns>Deterministic world accessor.</returns>
    private static IWorldAccessor CreateWorld(EnumAppSide side, Item item, Block block)
    {
        return RuntimeCoverageDispatchProxy.Create<IWorldAccessor>((method, _) => method.Name switch
        {
            "get_Side" => side,
            "GetItem" => item,
            "GetBlock" => block,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Sets entity properties through the engine's non-public test boundary.</summary>
    /// <param name="entity">Entity receiving properties.</param>
    /// <param name="path">Asset path.</param>
    /// <param name="weight">Documented entity mass.</param>
    private static void SetEntityProperties(Entity entity, string path, float weight)
    {
        typeof(Entity).GetProperty(nameof(Entity.Properties))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(entity, [new EntityProperties
            {
                Code = new AssetLocation("game", path),
                Weight = weight
            }]);
    }

    /// <summary>Invokes the private Harmony prefix and returns its detached out state.</summary>
    /// <param name="entity">Callback entity.</param>
    /// <returns>Prefix state.</returns>
    private static LiquidProjectileCollisionSample InvokeBefore(Entity entity)
    {
        object?[] arguments = [entity, null];
        GetPatchMethod("BeforeLiquidCollision").Invoke(null, arguments);
        return (LiquidProjectileCollisionSample)arguments[1]!;
    }

    /// <summary>Invokes the private Harmony postfix.</summary>
    /// <param name="entity">Callback entity.</param>
    /// <param name="state">Matching prefix state.</param>
    private static void InvokeAfter(Entity entity, LiquidProjectileCollisionSample state)
    {
        GetPatchMethod("AfterLiquidCollision").Invoke(null, [entity, state]);
    }

    /// <summary>Invokes the private Harmony exception finalizer.</summary>
    /// <param name="entity">Callback entity.</param>
    /// <param name="exception">Engine exception, if any.</param>
    /// <returns>Unchanged exception.</returns>
    private static Exception? InvokeFinalizer(Entity entity, Exception? exception)
    {
        return (Exception?)GetPatchMethod("FinalizeLiquidCollision").Invoke(
            null,
            [entity, exception]);
    }

    /// <summary>Invokes the private resilient assembly type enumerator.</summary>
    /// <param name="assembly">Assembly fixture.</param>
    /// <returns>Materialized loadable types.</returns>
    private static Type[] InvokeGetLoadableTypes(Assembly assembly)
    {
        IEnumerable<Type> types = (IEnumerable<Type>)GetPatchMethod("GetLoadableTypes")
            .Invoke(null, [assembly])!;
        return types.ToArray();
    }

    /// <summary>Finds one private static patch callback by stable name.</summary>
    /// <param name="name">Method name.</param>
    /// <returns>Reflected callback.</returns>
    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(ProjectileLiquidCollisionPatch).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ProjectileLiquidCollisionPatch).FullName, name);
    }

    /// <summary>Sets one private static patch field for isolated callback execution.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">Fixture value.</param>
    private static void SetPatchField(string name, object? value)
    {
        typeof(ProjectileLiquidCollisionPatch).GetField(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, value);
    }

    /// <summary>
    /// Supplies a loaded entity override with no managed method body so the discovery loop proves
    /// that one incompatible mod callback cannot prevent the supported base callback from loading.
    /// </summary>
    private sealed class UnpatchableProjectile : Entity
    {
        /// <inheritdoc />
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
        public override extern void OnCollideWithLiquid();
    }

    /// <summary>Minimal public projectile implementation used by detached callback tests.</summary>
    private class TestProjectile : Entity, IProjectile
    {
        /// <inheritdoc />
        public bool Collectible { get; set; }
        /// <inheritdoc />
        public float Damage { get; set; }
        /// <inheritdoc />
        public bool DamageStackOnImpact { get; set; }
        /// <inheritdoc />
        public int DamageTier { get; set; }
        /// <inheritdoc />
        public EnumDamageType DamageType { get; set; }
        /// <inheritdoc />
        public float DropOnImpactChance { get; set; }
        /// <inheritdoc />
        public bool EntityHit { get; }
        /// <inheritdoc />
        public Entity? FiredBy { get; set; }
        /// <inheritdoc />
        public bool IgnoreInvFrames { get; set; }
        /// <inheritdoc />
        public ItemStack? ProjectileStack { get; set; }
        /// <inheritdoc />
        public bool Stuck { get; set; }
        /// <inheritdoc />
        public ItemStack? WeaponStack { get; set; }
        /// <inheritdoc />
        public float Weight { get; set; }

        /// <inheritdoc />
        public void PreInitialize()
        {
        }

        /// <inheritdoc />
        public void SetFromConfig(IProjectileJsonConfig config)
        {
        }
    }

    /// <summary>Thrown-stone fixture exposing the retained vector used by the stock override.</summary>
    private sealed class RetainedStoneProjectile : TestProjectile
    {
        private Vec3d motionBeforeCollide = new();

        /// <summary>Sets the private retained vector discovered by Harmony reflection.</summary>
        /// <param name="motion">Incident vector.</param>
        internal void SetRetainedMotion(Vec3d motion)
        {
            motionBeforeCollide = motion;
        }
    }

    /// <summary>Failure mode emitted by the synthetic reflection assembly.</summary>
    private enum FaultKind
    {
        /// <summary>Returns one surviving type through <see cref="ReflectionTypeLoadException"/>.</summary>
        Partial,
        /// <summary>Throws an unrelated failure that must fail closed.</summary>
        Total
    }

    /// <summary>Assembly fixture forcing both resilient type-enumeration catch paths.</summary>
    private sealed class FaultingAssembly : Assembly
    {
        private readonly FaultKind kind;

        /// <summary>Creates one deterministic assembly failure.</summary>
        /// <param name="kind">Failure class.</param>
        internal FaultingAssembly(FaultKind kind)
        {
            this.kind = kind;
        }

        /// <inheritdoc />
        public override Type[] GetTypes()
        {
            if (kind == FaultKind.Partial)
            {
                throw new ReflectionTypeLoadException(
                    [typeof(string), null],
                    [new TypeLoadException("fixture missing optional type")]);
            }

            throw new InvalidOperationException("fixture assembly failure");
        }
    }
}
