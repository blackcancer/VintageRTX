using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Closes fail-closed and compact-history liquid branches that are intentionally rare in the live
/// renderer but remain part of the deterministic physics contract.
/// </summary>
[TestClass]
public sealed class LiquidSurfaceResidualCoverageTests
{
    /// <summary>Covers world-input sanitizers, rainfall height, mass, and taxonomy boundaries.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void WorldInputBoundariesRemainFiniteAndFailClosed()
    {
        IBlockAccessor accessor = RuntimeCoverageDispatchProxy.Create<IBlockAccessor>((method, _) =>
            method.Name switch
            {
                "GetWindSpeedAt" => new Vec3d(double.NaN, 0.0, double.PositiveInfinity),
                "GetClimateAt" => null,
                "GetRainMapHeightAt" => 12,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        LiquidSurfaceWorldInputs inputs = new();
        Assert.AreEqual(default, inputs.SampleEnvironment(accessor, 2.0, 12.0, 3.0));
        Assert.IsTrue(inputs.IsRainExposed(accessor, 2, 12.0f, 3));
        Assert.IsFalse(inputs.IsRainExposed(accessor, 2, 11.99f, 3));

        EntityPlayer entity = new();
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidSurfaceWorldInputs.SampleEntity(entity, -1.0f));
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            LiquidSurfaceWorldInputs.EstimateDroppedItemPseudoMassKilograms(int.MinValue));
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms * 8.0f,
            LiquidSurfaceWorldInputs.EstimateDroppedItemPseudoMassKilograms(int.MaxValue),
            1.0e-6f);
        Assert.AreEqual(
            LiquidEntitySurfaceClass.Bobber,
            LiquidSurfaceEntityClassifier.Classify("GAME", "BOBBER", isCreature: false));
        Assert.AreEqual(
            LiquidEntitySurfaceClass.ThrownStone,
            LiquidSurfaceEntityClassifier.Classify("mod", "thrownboulder-granite", isCreature: false));
        Assert.AreEqual(
            LiquidEntitySurfaceClass.Fish,
            LiquidSurfaceEntityClassifier.Classify("mod", "fish-cod", isCreature: true));
        Assert.AreEqual(
            LiquidEntitySurfaceClass.Generic,
            LiquidSurfaceEntityClassifier.Classify("mod", "fish-decoration", isCreature: false));
    }

    /// <summary>Covers truncated column metadata, disconnected depth, reserved profiles, and rain bounds.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SnapshotSurfaceHelpersHandleTruncationAndDisconnectedColumns()
    {
        VoxelSceneSnapshot truncated = CreateSnapshot(
            fluidSurface: [1, 1, 7, 0],
            metadata: []);
        Assert.IsTrue(LiquidSurfaceRuntime.TryResolveColumnSurface(
            in truncated,
            0,
            0,
            out float height,
            out byte profile));
        Assert.AreEqual(0.875f, height, 1.0e-6f);
        Assert.AreEqual(1, profile);

        byte[] disconnectedMetadata =
        [
            2,
            (byte)VoxelLiquidFlags.FluidLayer,
            7,
            0
        ];
        VoxelSceneSnapshot disconnected = CreateSnapshot(
            fluidSurface: [1, 1, 7, 0],
            metadata: disconnectedMetadata);
        Assert.IsTrue(LiquidSurfaceRuntime.TryResolveColumnSurface(
            in disconnected,
            0,
            0,
            out _,
            out _,
            out float fallbackDepth));
        Assert.AreEqual(0.875f, fallbackDepth, 1.0e-6f);

        Assert.IsFalse(LiquidSurfaceRuntime.TryDecodeProfile(
            null!,
            LiquidOpticalRegistry.UnknownLiquidProfileId,
            out _,
            out _));
        Assert.IsFalse(LiquidSurfaceRuntime.TryDecodeProfile(new float[1], 1, out _, out _));
        Assert.IsFalse(LiquidSurfaceRuntime.IsRainExposed(in truncated, -1, 0, 1.0f));
        VoxelSceneSnapshot shortRain = truncated with { RainSurface = [] };
        Assert.IsFalse(LiquidSurfaceRuntime.IsRainExposed(in shortRain, 0, 0, 1.0f));

        EntityPlayer dead = new() { Alive = false };
        Assert.IsFalse(LiquidSurfaceRuntime.IsRelevantSurfaceEntity(dead));
        EntityPlayer swimmer = new() { Alive = true, Swimming = true };
        Assert.IsTrue(LiquidSurfaceRuntime.IsRelevantSurfaceEntity(swimmer));
    }

    /// <summary>
    /// Covers empty history, newest-packet truncation, invalid physical inputs, and diagnostic
    /// accessors using two distinct authoritative arrow entries.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SimulationResidualBranchesPreserveBoundedNewestHistory()
    {
        LiquidSurfaceSimulation inactive = new(0, 0, 1, 1, 0.5f);
        Span<LiquidSurfaceSubgridImpactDiagnostic> onePacket =
            stackalloc LiquidSurfaceSubgridImpactDiagnostic[1];
        Assert.AreEqual(0, inactive.WriteSubgridImpactsAfter(0, onePacket));
        Assert.IsFalse(inactive.TryGetNearestSurfaceWorldY(0.25, 0.25, out float missingY));
        Assert.AreEqual(0.0f, missingY);
        Assert.IsFalse(inactive.QueueImpact(0.25, 0.25, float.NaN, 1.0f));
        Assert.IsFalse(inactive.QueueImpact(0.25, 0.25, 1.0f, -1.0f));
        Assert.AreEqual(0.0f, LiquidSurfaceSimulation.ComputeSubgridSplashPeakDisplacement(
            0.0f,
            998.0f,
            0.072f,
            0.5f));

        LiquidSurfaceDynamics dynamics = CreateDynamics();
        LiquidSurfacePhysicalProperties physics = CreatePhysics();
        Assert.AreEqual(
            LiquidSurfaceSimulation.ResolveImpulseSupportRadius(in dynamics, 0.5f),
            LiquidSurfaceSimulation.ResolveProjectileImpulseSupportRadius(
                LiquidEntitySurfaceClass.Generic,
                1.0f,
                1.0f,
                in dynamics,
                in physics,
                0.5f));
        Assert.AreEqual(default, LiquidSurfaceSimulation.ResolveProjectileWaveEnergyPartition(
            LiquidEntitySurfaceClass.Generic,
            1.0f,
            1.0f,
            in physics,
            0.5f));
        Assert.AreEqual(0.0, InvokeSphericalCavityRadius(0.0), 0.0);

        LiquidSurfaceSimulation active = new(0, 0, 2, 1, 0.5f);
        for (int x = 0; x < 2; x++)
        {
            active.SetSurfaceCell(
                x,
                0,
                0.875f,
                1,
                in dynamics,
                in physics,
                rainExposed: true,
                depthMetres: 1.0f);
        }
        LiquidProjectileCollisionSample first = CreateArrowCollision(9301, 0.25);
        LiquidProjectileCollisionSample second = CreateArrowCollision(9302, 0.75);
        active.ObserveProjectileLiquidCollisions([first, second]);
        Assert.AreEqual(2, active.TotalProjectileImpactCount);
        active.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 2.0f, default);
        Assert.AreEqual(1, active.WriteSubgridImpactsAfter(0, onePacket));
        Assert.AreEqual(2, onePacket[0].Sequence, "Newest packet must survive caller truncation.");

        LiquidProjectileImpactDiagnostic projectile = active.LastProjectileImpactDiagnostic;
        _ = projectile.MassKilograms;
        _ = projectile.IncidentVelocityZMetresPerSecond;
        _ = projectile.OutgoingVelocityXMetresPerSecond;
        _ = projectile.OutgoingVelocityZMetresPerSecond;
        _ = projectile.NearInterfaceEnergyJoules;
        LiquidSurfaceImpactDiagnostic impact = default;
        _ = impact.PeakDisplacement;
        _ = impact.AdditionalDampingPerSecond;
        LiquidSurfaceSubgridImpactDiagnostic packet = onePacket[0];
        _ = packet.ImpulseKind;
        _ = packet.CellX;
        _ = packet.CellZ;
        _ = packet.TextureU;
        _ = packet.TextureV;
        _ = packet.UploadedHeight;
        _ = packet.UploadedNormalX;
        _ = packet.UploadedNormalZ;
        _ = packet.ImpactVelocityYMetresPerSecond;
        _ = packet.DirectionXMetresPerSecond;
        _ = packet.DirectionZMetresPerSecond;
        _ = packet.DensityKilogramsPerCubicMetre;
        _ = packet.DynamicViscosityPascalSeconds;
        _ = packet.SurfaceTensionNewtonsPerMetre;
        _ = packet.AdditionalDampingPerSecond;
        Assert.AreEqual(0, active.WriteSubgridImpactsAfter(packet.Sequence, onePacket));
    }

    /// <summary>
    /// Exercises constructor and grid guards, inactive-cell stepping, queue overflow, projectile
    /// rejection, bounded tracker replacement, and maximum fixed-step catch-up.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SimulationGuardsQueuesAndTrackerReplacementRemainBounded()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 0, 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 1, float.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 1, eventBudget: 0));

        LiquidSurfaceSimulation simulation = new(
            0,
            0,
            2,
            1,
            0.5f,
            eventBudget: 1,
            bubbleBudget: 1,
            trackedEntityBudget: 1);
        LiquidSurfaceDynamics dynamics = CreateDynamics();
        LiquidSurfacePhysicalProperties physics = CreatePhysics();
        simulation.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: true,
            depthMetres: 1.0f);
        simulation.SetSurfaceCell(
            1,
            0,
            0.875f,
            LiquidOpticalRegistry.NoLiquidProfileId,
            in dynamics,
            in physics,
            rainExposed: false);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => simulation.SetSurfaceCell(
            0,
            0,
            float.NaN,
            1,
            in dynamics,
            in physics,
            rainExposed: false));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => simulation.GetCellSample(-1, 0));

        Assert.IsFalse(simulation.TryGetSurfaceWorldY(-1.0, 0.25, out _));
        Assert.IsFalse(simulation.TryGetSurfaceWorldY(0.75, 0.25, out _));
        Assert.IsTrue(simulation.TryGetSurfaceWorldY(0.25, 0.25, out float activeY));
        Assert.AreEqual(0.875f, activeY, 1.0e-6f);
        Assert.IsFalse(simulation.QueueImpulse(-1.0, 0.25, 1.0f));
        Assert.IsFalse(simulation.QueueImpulse(0.75, 0.25, 1.0f));
        Assert.IsFalse(simulation.QueueImpulse(0.25, 0.25, float.NaN));
        Assert.IsTrue(simulation.QueueImpulse(0.25, 0.25, 1.0f));
        Assert.IsFalse(simulation.QueueImpulse(0.25, 0.25, 1.0f));
        Assert.IsTrue(simulation.DroppedEventCount > 0);
        Assert.ThrowsException<ArgumentException>(() => simulation.WriteGpuTexture([]));

        LiquidProjectileCollisionSample valid = CreateArrowCollision(9401, 0.25);
        simulation.ObserveProjectileLiquidCollisions([
            valid with { MassKilograms = float.NaN },
            valid with { WorldX = -1.0 },
            valid with { WorldX = 0.75 },
            valid with { IncidentMotionY = 0.25f },
            valid with { EntityId = 9402, PreviousWorldY = valid.WorldY },
            valid with { EntityId = 9403, PreviousWorldY = 1.50, WorldY = 1.40 },
            valid with { EntityId = 9404, PreviousWorldX = -2.0 },
            valid with { EntityId = 9405, PreviousWorldX = 1.2 }
        ]);
        Assert.AreEqual(0, simulation.TotalProjectileImpactCount);
        int droppedBeforeRain = simulation.DroppedEventCount;
        InvokeApplyRain(
            simulation,
            new LiquidSurfaceForcing(
                0.0f,
                0.0f,
                LiquidSurfaceWorldInputs.MaximumRainfallRateMetresPerSecond));
        Assert.IsTrue(simulation.DroppedEventCount > droppedBeforeRain);

        LiquidEntitySurfaceSample firstTracker = new(
            9501,
            LiquidEntitySurfaceClass.Generic,
            0.25,
            1.0,
            0.25,
            0.0f,
            0.0f,
            0.0f,
            false,
            false,
            1.0f,
            LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds);
        simulation.ObserveEntities([firstTracker]);
        simulation.ObserveEntities([firstTracker with { EntityId = 9502 }]);

        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 0.5f, default);
        simulation.ClearSurfaceCell(0, 0);
        Assert.IsTrue(
            simulation.Advance(1.0f, new LiquidSurfaceForcing(5.0f, 0.0f, 0.0f)) > 0);
        Assert.AreEqual(0, simulation.ActiveBubbleCount);
        Assert.AreEqual(1, simulation.BubbleBudget);
        Assert.AreEqual(0, simulation.TotalBubbleSpawnCount);
    }

    /// <summary>
    /// Drives deterministic bubble spawning through rise, burst, and decay while validating the
    /// compact GPU primitive's spatial height and emission kernels.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void BubbleLifecycleWritesFiniteGpuPrimitiveAndCompletesBurst()
    {
        LiquidSurfaceDynamics bubbles = new(
            WindCoupling: 0.4f,
            WaveAmplitude: 0.03f,
            WaveLength: 2.0f,
            WaveSpeed: 1.5f,
            Damping: 0.07f,
            ImpactResponse: 1.0f,
            SurfaceTension: 0.072f,
            BubbleRate: 1.0e9f,
            BubbleRadiusMinimum: 0.04f,
            BubbleRadiusMaximum: 0.08f,
            BubbleRiseDuration: LiquidSurfaceSimulation.FixedStepSeconds * 3.0f,
            BubbleBurstStrength: 0.5f,
            BubbleEmissionBoost: 2.0f);
        LiquidSurfacePhysicalProperties physics = CreatePhysics();
        LiquidSurfaceSimulation simulation = new(
            0,
            0,
            2,
            1,
            0.5f,
            eventBudget: 8,
            bubbleBudget: 2,
            trackedEntityBudget: 1,
            deterministicSeed: 1);
        simulation.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in bubbles,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);

        simulation.Advance(
            LiquidSurfaceSimulation.FixedStepSeconds,
            new LiquidSurfaceForcing(2.0f, 1.0f, 0.0f));
        Assert.IsTrue(simulation.ActiveBubbleCount > 0);
        Assert.IsTrue(simulation.TotalBubbleSpawnCount > 0);
        Span<LiquidSurfaceBubbleSample> samples = stackalloc LiquidSurfaceBubbleSample[2];
        int written = simulation.WriteActiveBubbles(samples);
        Assert.IsTrue(written > 0);
        LiquidSurfaceBubbleSample sample = samples[0];
        Assert.IsTrue(float.IsFinite(sample.LocalX));
        Assert.IsTrue(float.IsFinite(sample.LocalZ));
        Assert.IsTrue(sample.Radius > 0.0f);
        Assert.IsTrue(sample.Growth >= 0.0f);
        Assert.IsTrue(sample.Emission >= 0.0f);
        Assert.IsTrue(sample.EvaluateHeight(sample.LocalX, sample.LocalZ) >= 0.0f);
        Assert.IsTrue(sample.EvaluateEmission(sample.LocalX, sample.LocalZ) >= 0.0f);
        Assert.AreEqual(0.0f, sample.EvaluateHeight(sample.LocalX + sample.Radius * 2.0f, sample.LocalZ));
        Assert.AreEqual(0.0f, sample.EvaluateEmission(sample.LocalX, sample.LocalZ + sample.Radius * 2.0f));
        Assert.AreEqual(0.0f, default(LiquidSurfaceBubbleSample).EvaluateHeight(0.0f, 0.0f));

        for (int step = 0; step < 5; step++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
            simulation.WriteActiveBubbles(samples);
        }
        Assert.IsTrue(simulation.TotalBubbleSpawnCount >= 1);
        Assert.AreEqual(0, InvokeFindProfileCell(simulation, profileId: 1, ordinal: 1));
        Assert.AreEqual(0, InvokeFindProfileCell(simulation, profileId: 99, ordinal: 0));

        LiquidSurfaceDynamics rareBubbles = bubbles with { BubbleRate = 1.0e-6f };
        LiquidSurfaceSimulation rare = new(0, 0, 1, 1, 0.5f, deterministicSeed: 7);
        rare.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in rareBubbles,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);
        rare.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Assert.AreEqual(0, rare.TotalBubbleSpawnCount);
    }

    /// <summary>
    /// Verifies entity observations reject missing surfaces and invalid mass while a horizontal
    /// thrown-stone crossing queues one ricochet and suppresses an immediate repeated contact.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ThrownStoneEntityCrossingQueuesOneRecentRicochet()
    {
        LiquidSurfaceSimulation simulation = new(0, 0, 2, 1, 0.5f, trackedEntityBudget: 16);
        LiquidSurfaceDynamics dynamics = CreateDynamics();
        LiquidSurfacePhysicalProperties physics = CreatePhysics();
        simulation.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);

        LiquidEntitySurfaceSample outside = CreateEntitySample(9601, 2.0, 1.0f);
        simulation.ObserveEntities([outside]);
        simulation.ObserveEntities([outside]);
        LiquidEntitySurfaceSample inactive = CreateEntitySample(9602, 0.75, 1.0f);
        simulation.ObserveEntities([inactive]);
        simulation.ObserveEntities([inactive]);
        LiquidEntitySurfaceSample invalidMass = CreateEntitySample(9603, 0.25, 1.0f);
        simulation.ObserveEntities([invalidMass]);
        simulation.ObserveEntities([invalidMass with { MassKilograms = float.NaN }]);
        simulation.ObserveEntities([
            CreateEntitySample(9604, 2.0, 1.0f) with
            {
                SurfaceClass = LiquidEntitySurfaceClass.DroppedItem
            },
            CreateEntitySample(9605, 0.75, 1.0f) with
            {
                SurfaceClass = LiquidEntitySurfaceClass.DroppedItem
            }
        ]);

        LiquidEntitySurfaceSample above = CreateEntitySample(9701, 0.25, 0.35f) with
        {
            SurfaceClass = LiquidEntitySurfaceClass.ThrownStone,
            WorldY = 1.10,
            MotionX = 0.20f,
            MotionY = -0.10f,
            FeetInLiquid = false
        };
        LiquidEntitySurfaceSample below = above with
        {
            WorldY = 0.80,
            FeetInLiquid = true
        };
        simulation.ObserveEntities([above]);
        simulation.ObserveEntities([below]);
        Assert.AreEqual(1, simulation.TotalImpulseCount);
        simulation.ObserveEntities([above]);
        simulation.ObserveEntities([below]);
        Assert.AreEqual(1, simulation.TotalImpulseCount, "Recent stone collision must not duplicate energy.");
    }

    /// <summary>
    /// Rejects crossings whose interpolated contact leaves the clipmap or lands on an inactive
    /// neighbour, resolves a two-height cell transition, and prevents a later ricochet from
    /// reusing an entity that already deposited its entry energy.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void EntityCrossingRemapHonorsClipmapActivityHeightAndPriorImpact()
    {
        LiquidSurfaceDynamics dynamics = CreateDynamics();
        LiquidSurfacePhysicalProperties physics = CreatePhysics();

        LiquidSurfaceSimulation rejected = new(0, 0, 2, 1, 0.5f, trackedEntityBudget: 8);
        rejected.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);
        LiquidEntitySurfaceSample current = CreateEntitySample(9710, 0.25, 0.35f) with
        {
            SurfaceClass = LiquidEntitySurfaceClass.ThrownStone,
            WorldY = 0.0,
            MotionX = 0.20f,
            MotionY = -0.10f,
            FeetInLiquid = false,
        };
        rejected.ObserveEntities([current with
        {
            WorldX = 2.0,
            WorldY = 1.0,
            FeetInLiquid = false,
        }]);
        rejected.ObserveEntities([current]);
        Assert.AreEqual(0, rejected.TotalImpulseCount);

        rejected.ObserveEntities([current with
        {
            EntityId = 9711,
            WorldX = 0.75,
            WorldY = 1.0,
            FeetInLiquid = false,
        }]);
        rejected.ObserveEntities([current with { EntityId = 9711 }]);
        Assert.AreEqual(0, rejected.TotalImpulseCount);

        LiquidSurfaceSimulation stepped = new(0, 0, 2, 1, 0.5f, trackedEntityBudget: 8);
        stepped.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);
        stepped.SetSurfaceCell(
            1,
            0,
            0.20f,
            1,
            in dynamics,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);
        LiquidEntitySurfaceSample high = current with
        {
            EntityId = 9712,
            WorldX = 0.75,
            WorldY = 1.20,
            FeetInLiquid = false,
        };
        stepped.ObserveEntities([high]);
        stepped.ObserveEntities([current with { EntityId = 9712 }]);
        Assert.AreEqual(1, stepped.TotalImpulseCount);

        LiquidSurfaceSimulation priorImpact = new(0, 0, 1, 1, 0.5f, trackedEntityBudget: 4);
        priorImpact.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);
        LiquidEntitySurfaceSample vertical = current with
        {
            EntityId = 9713,
            WorldY = 1.10,
            MotionX = 0.0f,
            MotionY = -0.20f,
            FeetInLiquid = false,
        };
        priorImpact.ObserveEntities([vertical]);
        priorImpact.ObserveEntities([vertical with { WorldY = 0.80, FeetInLiquid = true }]);
        Assert.AreEqual(1, priorImpact.TotalImpulseCount);
        priorImpact.ObserveEntities([vertical with { MotionX = 0.30f }]);
        priorImpact.ObserveEntities([vertical with
        {
            WorldY = 0.80,
            MotionX = 0.30f,
            MotionY = -0.05f,
            FeetInLiquid = true,
        }]);
        Assert.AreEqual(1, priorImpact.TotalImpulseCount);
    }

    /// <summary>
    /// Covers periodic bobber, fish, and swimmer wake dispatch after the fixed six-step interval,
    /// plus the generic near-surface entity's explicit no-wake branch.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void NearSurfaceEntitiesQueueOnlyAuthoredWakeKinds()
    {
        LiquidSurfaceSimulation simulation = new(0, 0, 1, 1, 0.5f, eventBudget: 16);
        LiquidSurfaceDynamics dynamics = CreateDynamics();
        LiquidSurfacePhysicalProperties physics = CreatePhysics();
        simulation.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: false,
            depthMetres: 1.0f);
        LiquidEntitySurfaceSample bobber = CreateWakeSample(
            9801,
            LiquidEntitySurfaceClass.Bobber,
            swimming: false);
        LiquidEntitySurfaceSample fish = CreateWakeSample(
            9802,
            LiquidEntitySurfaceClass.Fish,
            swimming: false);
        LiquidEntitySurfaceSample swimmer = CreateWakeSample(
            9803,
            LiquidEntitySurfaceClass.Generic,
            swimming: true);
        LiquidEntitySurfaceSample generic = CreateWakeSample(
            9804,
            LiquidEntitySurfaceClass.Generic,
            swimming: false);
        simulation.ObserveEntities([bobber, fish, swimmer, generic]);
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 6.0f, default);
        simulation.ObserveEntities([
            bobber with { WorldX = 0.26 },
            fish with { WorldX = 0.27 },
            swimmer with { WorldX = 0.28 },
            generic with { WorldX = 0.29 }
        ]);

        Assert.AreEqual(3, simulation.PendingImpulseCount);
    }

    /// <summary>Covers successful cleanup when initial texture allocation fails.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void UploaderAllocationFailureDeletesCandidateWithoutMaskingError()
    {
        int deletes = 0;
        ILiquidSurfaceTextureApi textureApi = RuntimeCoverageDispatchProxy.Create<ILiquidSurfaceTextureApi>(
            (method, _) =>
            {
                if (method.Name == nameof(ILiquidSurfaceTextureApi.CreateTexture))
                {
                    return 77;
                }
                if (method.Name == nameof(ILiquidSurfaceTextureApi.AllocateRgba32Float))
                {
                    throw new InvalidOperationException("fixture allocation failure");
                }
                if (method.Name == nameof(ILiquidSurfaceTextureApi.DeleteTexture))
                {
                    deletes++;
                }
                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            });
        using LiquidSurfaceGpuUploader uploader = new(textureApi);
        LiquidSurfaceSimulation simulation = new(0, 0, 1, 1, 0.5f);

        Assert.ThrowsException<InvalidOperationException>(() => uploader.Upload(simulation));
        Assert.AreEqual(1, deletes);
        Assert.AreEqual(default, uploader.Current);
    }

    /// <summary>Creates a one-column snapshot rooted at zero.</summary>
    /// <param name="fluidSurface">Four-channel column ABI.</param>
    /// <param name="metadata">Four-channel voxel ABI.</param>
    /// <returns>Immutable snapshot.</returns>
    private static VoxelSceneSnapshot CreateSnapshot(byte[] fluidSurface, byte[] metadata)
    {
        return new VoxelSceneSnapshot(
            Voxels: [],
            Occupancy: [],
            Width: 1,
            Height: 1,
            Depth: 1,
            OriginX: 0,
            OriginY: 0,
            OriginZ: 0,
            Lights: [],
            Irradiance: [],
            IrradianceDirection: [],
            FluidSurface: fluidSurface,
            LiquidMetadata: metadata,
            LiquidOpticalProfileLookup: [],
            LiquidOpticalProfileCount: 0,
            SunOccupancy: [],
            RainSurface: [0.0f],
            SunOccupancyWidth: 0,
            SunOccupancyHeight: 0,
            SunOccupancyDepth: 0,
            SunOccupancyScale: 1,
            SunTraceDistance: 1,
            SunOriginX: 0,
            SunOriginY: 0,
            SunOriginZ: 0,
            RainSurfaceWidth: 1,
            RainSurfaceDepth: 1,
            RainSurfaceOriginX: 0,
            RainSurfaceOriginZ: 0,
            FluidVoxelCount: 1,
            VisibleLiquidContainerCount: 0,
            Generation: 1,
            FluidSurfaceWidth: 1,
            FluidSurfaceDepth: 1,
            FluidSurfaceOriginX: 0,
            FluidSurfaceOriginZ: 0);
    }

    /// <summary>Creates stable wave coefficients for physical helper tests.</summary>
    /// <returns>Finite coefficients.</returns>
    private static LiquidSurfaceDynamics CreateDynamics()
    {
        return new LiquidSurfaceDynamics(
            WindCoupling: 0.4f,
            WaveAmplitude: 0.03f,
            WaveLength: 2.0f,
            WaveSpeed: 1.5f,
            Damping: 0.07f,
            ImpactResponse: 1.0f,
            SurfaceTension: 0.072f,
            BubbleRate: 0.0f,
            BubbleRadiusMinimum: 0.0f,
            BubbleRadiusMaximum: 0.0f,
            BubbleRiseDuration: 0.0f,
            BubbleBurstStrength: 0.0f,
            BubbleEmissionBoost: 0.0f);
    }

    /// <summary>Creates validated water-like SI properties.</summary>
    /// <returns>Finite properties.</returns>
    private static LiquidSurfacePhysicalProperties CreatePhysics()
    {
        return new LiquidSurfacePhysicalProperties(
            DensityKilogramsPerCubicMetre: 998.0f,
            DynamicViscosityPascalSeconds: 0.001f,
            SurfaceTensionNewtonsPerMetre: 0.072f,
            AdditionalDampingPerSecond: 0.07f,
            ResolvedWaveEnergyFraction: 0.5f);
    }

    /// <summary>Creates one exact downward arrow collision centered in a chosen cell.</summary>
    /// <param name="entityId">Projectile identity.</param>
    /// <param name="worldX">Contact X.</param>
    /// <returns>Authoritative collision sample.</returns>
    private static LiquidProjectileCollisionSample CreateArrowCollision(long entityId, double worldX)
    {
        return new LiquidProjectileCollisionSample(
            EntityId: entityId,
            SurfaceClass: LiquidEntitySurfaceClass.Projectile,
            WorldX: worldX,
            WorldY: 0.80,
            WorldZ: 0.25,
            PreviousWorldX: worldX,
            PreviousWorldY: 1.05,
            PreviousWorldZ: 0.25,
            IncidentMotionX: 0.10f,
            IncidentMotionY: -0.25f,
            IncidentMotionZ: 0.05f,
            OutgoingMotionX: 0.08f,
            OutgoingMotionY: -0.04f,
            OutgoingMotionZ: 0.02f,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            MotionSamplePeriodSeconds: LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds,
            IsServerAuthoritative: true);
    }

    /// <summary>Creates one finite entity observation at a selected horizontal coordinate.</summary>
    /// <param name="entityId">Entity identity.</param>
    /// <param name="worldX">World X.</param>
    /// <param name="massKilograms">Finite mass.</param>
    /// <returns>Generic detached sample.</returns>
    private static LiquidEntitySurfaceSample CreateEntitySample(
        long entityId,
        double worldX,
        float massKilograms)
    {
        return new LiquidEntitySurfaceSample(
            entityId,
            LiquidEntitySurfaceClass.Generic,
            worldX,
            1.0,
            0.25,
            0.0f,
            0.0f,
            0.0f,
            false,
            false,
            massKilograms,
            LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds);
    }

    /// <summary>Creates a near-surface horizontally moving entity observation.</summary>
    /// <param name="entityId">Entity identity.</param>
    /// <param name="surfaceClass">Wake taxonomy.</param>
    /// <param name="swimming">Whether generic motion represents a swimmer.</param>
    /// <returns>Finite observation.</returns>
    private static LiquidEntitySurfaceSample CreateWakeSample(
        long entityId,
        LiquidEntitySurfaceClass surfaceClass,
        bool swimming)
    {
        return new LiquidEntitySurfaceSample(
            entityId,
            surfaceClass,
            0.25,
            0.90,
            0.25,
            0.10f,
            0.0f,
            0.05f,
            true,
            swimming,
            1.0f,
            LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds);
    }

    /// <summary>Invokes the private zero-energy spherical cavity guard.</summary>
    /// <param name="energy">Surface energy.</param>
    /// <returns>Cavity radius.</returns>
    private static double InvokeSphericalCavityRadius(double energy)
    {
        MethodInfo method = typeof(LiquidSurfaceSimulation).GetMethod(
            "ResolveSphericalCavityRadiusMetres",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (double)method.Invoke(null, [energy, 998.0, 0.072])!;
    }

    /// <summary>Invokes the private defensive profile-cell fallback.</summary>
    /// <param name="simulation">Configured solver.</param>
    /// <param name="profileId">Absent profile.</param>
    /// <param name="ordinal">Requested ordinal.</param>
    /// <returns>Defensive flat-cell fallback.</returns>
    private static int InvokeFindProfileCell(
        LiquidSurfaceSimulation simulation,
        int profileId,
        int ordinal)
    {
        MethodInfo method = typeof(LiquidSurfaceSimulation).GetMethod(
            "FindProfileCell",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (int)method.Invoke(simulation, [profileId, ordinal])!;
    }

    /// <summary>Invokes rain application directly while a bounded event queue is deliberately full.</summary>
    /// <param name="simulation">Configured solver.</param>
    /// <param name="forcing">Positive rainfall forcing.</param>
    private static void InvokeApplyRain(
        LiquidSurfaceSimulation simulation,
        LiquidSurfaceForcing forcing)
    {
        MethodInfo method = typeof(LiquidSurfaceSimulation).GetMethod(
            "ApplyRain",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(simulation, [forcing]);
    }
}
