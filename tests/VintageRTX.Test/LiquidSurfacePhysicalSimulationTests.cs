using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Verifies deterministic SI free-surface propagation, damping, forcing, and impacts.</summary>
[TestClass]
public sealed class LiquidSurfacePhysicalSimulationTests
{
    /// <summary>Checks phase/group speeds and decay against the shared analytic physical model.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void PhaseSpeedAndDampingComeFromSiMaterialProperties()
    {
        LiquidSurfaceDynamics dynamics = CreateDynamics(wavelengthMetres: 0.10f);
        LiquidSurfacePhysicalProperties water = new(998.2f, 0.001002f, 0.07275f);
        LiquidSurfacePhysicalProperties honey = new(1_496.0f, 7.85f, 0.050f);

        float waterSpeed = LiquidSurfaceSimulation.ComputePhaseSpeedMetresPerSecond(
            in dynamics,
            in water,
            cellSize: 0.025f);
        float waterGroupSpeed = LiquidSurfaceSimulation.ComputeGroupVelocityMetresPerSecond(
            in dynamics,
            in water,
            cellSize: 0.025f,
            depthMetres: 0.03f);
        float waterDamping = LiquidSurfaceSimulation.ComputeDampingPerSecond(
            in dynamics,
            in water,
            cellSize: 0.025f);
        float honeyDamping = LiquidSurfaceSimulation.ComputeDampingPerSecond(
            in dynamics,
            in honey,
            cellSize: 0.025f);

        Assert.AreEqual(
            LiquidPhysicalModel.DeepWaterPhaseSpeedMetresPerSecond(0.10, 998.2, 0.07275),
            waterSpeed,
            1e-6);
        Assert.AreEqual(
            LiquidPhysicalModel.GravityCapillaryGroupVelocityMetresPerSecond(
                0.10,
                0.03,
                998.2,
                0.07275),
            waterGroupSpeed,
            1e-6);
        Assert.AreEqual(
            LiquidPhysicalModel.ViscousAmplitudeDampingPerSecond(0.10, 998.2, 0.001002),
            waterDamping,
            1e-6);
        Assert.IsTrue(honeyDamping > waterDamping * 5_000.0f);
    }

    /// <summary>Checks that kinetic-energy displacement scales with speed and square-root mass.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void QueueImpactUsesMassAndVelocityKineticEnergy()
    {
        LiquidSurfaceSimulation unitMass = CreateSimulation();
        LiquidSurfaceSimulation quadrupleMass = CreateSimulation();

        Assert.IsTrue(unitMass.QueueImpact(2.25, 2.25, 1.0f, 1.0f));
        Assert.IsTrue(quadrupleMass.QueueImpact(2.25, 2.25, 4.0f, 1.0f));
        Assert.AreEqual(1, unitMass.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default));
        Assert.AreEqual(1, quadrupleMass.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default));

        float firstHeight = Math.Abs(unitMass.GetCellSample(4, 4).Height);
        float secondHeight = Math.Abs(quadrupleMass.GetCellSample(4, 4).Height);
        Assert.IsTrue(firstHeight > 0.0f);
        Assert.AreEqual(firstHeight * 2.0f, secondHeight, firstHeight * 0.002f);
    }

    /// <summary>
    /// Checks dropped items share a material-agnostic stone mass, retain a minimum visible entry,
    /// and still scale their disturbance from the observed velocity.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void DroppedItemEntryHasAMinimumButVelocityStillControlsEnergy()
    {
        LiquidSurfaceSimulation minimum = CreateSimulation();
        LiquidSurfaceSimulation fast = CreateSimulation();
        LiquidEntitySurfaceSample above = new(
            81,
            LiquidEntitySurfaceClass.DroppedItem,
            2.25,
            1.0,
            2.25,
            0.0f,
            0.0f,
            0.0f,
            FeetInLiquid: false,
            Swimming: false,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            MotionSamplePeriodSeconds: 1.0f);
        LiquidEntitySurfaceSample slowEntry = above with
        {
            WorldY = 0.0,
            FeetInLiquid = true
        };
        LiquidEntitySurfaceSample fastAbove = above with { MotionY = -3.0f };
        LiquidEntitySurfaceSample fastEntry = slowEntry with { MotionY = -0.05f };

        minimum.ObserveEntities([above]);
        minimum.ObserveEntities([slowEntry]);
        fast.ObserveEntities([fastAbove]);
        fast.ObserveEntities([fastEntry]);
        Assert.AreEqual(1, minimum.PendingImpulseCount);
        Assert.AreEqual(1, fast.PendingImpulseCount);
        Assert.AreEqual(1, minimum.TotalDroppedItemImpactCount);
        Assert.AreEqual(1, fast.TotalDroppedItemImpactCount);
        minimum.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        fast.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        minimum.ObserveEntities([above]);
        minimum.ObserveEntities([slowEntry]);
        Assert.AreEqual(
            1,
            minimum.TotalDroppedItemImpactCount,
            "Buoyancy toggles must not turn one dropped item into repeated entry impacts.");
        Assert.AreEqual(0, minimum.PendingImpulseCount);

        float minimumEnergy = minimum.ComputeTotalEnergy();
        float fastEnergy = fast.ComputeTotalEnergy();
        Assert.IsTrue(minimumEnergy > 0.0f);
        Assert.IsTrue(fastEnergy > minimumEnergy * 3.5f);
        Assert.IsTrue(minimum.LastAppliedImpactPeakDisplacement > 0.0f);
        Assert.AreEqual(
            minimum.LastAppliedImpactPeakDisplacement,
            minimum.LastDroppedItemImpactPeakDisplacement,
            1.0e-6f);
        LiquidSurfaceImpactDiagnostic diagnostic = minimum.LastDroppedItemImpactDiagnostic;
        Assert.AreEqual(1, diagnostic.Sequence);
        Assert.AreEqual(2.25, diagnostic.WorldX, 1.0e-6);
        Assert.AreEqual(2.25, diagnostic.WorldZ, 1.0e-6);
        Assert.AreEqual(4, diagnostic.CellX);
        Assert.AreEqual(4, diagnostic.CellZ);
        Assert.AreEqual((4.5f / minimum.Width), diagnostic.TextureU, 1.0e-6f);
        Assert.AreEqual((4.5f / minimum.Depth), diagnostic.TextureV, 1.0e-6f);
        Assert.IsTrue(float.IsFinite(diagnostic.UploadedHeight));
        Assert.IsTrue(float.IsFinite(diagnostic.UploadedNormalX));
        Assert.IsTrue(float.IsFinite(diagnostic.UploadedNormalZ));
        Assert.IsTrue(diagnostic.SubgridPeakDisplacement > 0.0f);
        Assert.IsTrue(diagnostic.SubgridPeakDisplacement > Math.Abs(diagnostic.UploadedHeight));
        Assert.IsTrue(diagnostic.EnergyJoules > 0.0f);
        Assert.AreEqual(0.0f, diagnostic.DirectionXMetresPerSecond, 1.0e-6f);
        Assert.AreEqual(
            -LiquidSurfaceWorldInputs.MinimumDroppedItemImpactSpeedMetresPerSecond,
            diagnostic.ImpactVelocityYMetresPerSecond,
            1.0e-6f);
        Assert.AreEqual(0.0f, diagnostic.DirectionZMetresPerSecond, 1.0e-6f);
        double diagnosticKineticEnergy = LiquidPhysicalModel.KineticEnergyJoules(
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            Math.Abs(diagnostic.ImpactVelocityYMetresPerSecond));
        Assert.AreEqual(diagnosticKineticEnergy, diagnostic.EnergyJoules, diagnosticKineticEnergy * 0.001);
        Assert.AreEqual(998.2f, diagnostic.DensityKilogramsPerCubicMetre, 0.01f);
        Assert.AreEqual(0.001002f, diagnostic.DynamicViscosityPascalSeconds, 0.000001f);
        Assert.AreEqual(0.07275f, diagnostic.SurfaceTensionNewtonsPerMetre, 0.00001f);
        Assert.IsTrue(diagnostic.DominantWavelengthMetres is >= 0.12f and <= 0.55f);
        LiquidSurfaceSubgridImpactDiagnostic droppedPacket =
            minimum.LastSubgridImpactDiagnostic;
        Assert.AreEqual(1, droppedPacket.Sequence);
        Assert.AreEqual(diagnostic.Sequence, droppedPacket.SourceSequence);
        Assert.AreEqual(81L, droppedPacket.EntityId);
        Assert.AreEqual(LiquidEntitySurfaceClass.DroppedItem, droppedPacket.SurfaceClass);
        Assert.AreEqual(diagnostic.WorldX, droppedPacket.WorldX, 1.0e-6);
        Assert.AreEqual(diagnostic.WorldZ, droppedPacket.WorldZ, 1.0e-6);
        Assert.AreEqual(diagnostic.SubgridPeakDisplacement, droppedPacket.PeakDisplacement, 1.0e-6f);
        Assert.AreEqual(
            droppedPacket.SurfaceCoupledEnergyJoules,
            droppedPacket.ResolvedWaveEnergyJoules + droppedPacket.SubgridWaveEnergyJoules,
            1.0e-6f);

        LiquidSurfaceSimulation firstObservedInsideSurface = CreateSimulation();
        firstObservedInsideSurface.ObserveEntities([slowEntry]);
        Assert.AreEqual(
            0,
            firstObservedInsideSurface.PendingImpulseCount,
            "A stationary item already floating when the solver starts must not invent an impact.");
        Assert.AreEqual(0, firstObservedInsideSurface.TotalDroppedItemImpactCount);

        LiquidSurfaceSimulation fastFirstObservation = CreateSimulation();
        fastFirstObservation.ObserveEntities([fastEntry with { MotionY = -1.0f }]);
        Assert.AreEqual(1, fastFirstObservation.PendingImpulseCount);
        Assert.AreEqual(1, fastFirstObservation.TotalDroppedItemImpactCount);
        fastFirstObservation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        firstObservedInsideSurface.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Assert.IsTrue(fastFirstObservation.ComputeTotalEnergy() > 0.0f);

        LiquidSurfaceSimulation deeplySubmerged = CreateSimulation();
        deeplySubmerged.ObserveEntities([slowEntry with { WorldY = -2.0 }]);
        Assert.AreEqual(0, deeplySubmerged.PendingImpulseCount);
        Assert.AreEqual(0, deeplySubmerged.TotalDroppedItemImpactCount);
    }

    /// <summary>
    /// Verifies liquid contact cannot fire before the rendered segment reaches the plane, then uses
    /// the exact oblique crossing and last airborne velocity instead of the damped endpoint.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void DroppedItemUsesSegmentIntersectionAndLastAirMotion()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        LiquidEntitySurfaceSample airborne = new(
            901,
            LiquidEntitySurfaceClass.DroppedItem,
            1.25,
            1.0,
            1.25,
            0.05f,
            -0.275f,
            0.0f,
            FeetInLiquid: false,
            Swimming: false,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            MotionSamplePeriodSeconds: 1.0f / 30.0f);
        LiquidEntitySurfaceSample contactReportedAbovePlane = airborne with
        {
            WorldX = 1.75,
            WorldY = 0.20,
            FeetInLiquid = true,
            MotionY = -0.01f
        };
        LiquidEntitySurfaceSample slowedWhileAirborne = airborne with
        {
            WorldX = 1.50,
            WorldY = 0.60,
            MotionX = 0.01f,
            MotionY = -0.02f
        };
        LiquidEntitySurfaceSample crossed = contactReportedAbovePlane with
        {
            WorldX = 2.75,
            WorldY = -0.20
        };

        simulation.ObserveEntities([airborne]);
        simulation.ObserveEntities([slowedWhileAirborne]);
        simulation.ObserveEntities([contactReportedAbovePlane]);
        Assert.AreEqual(0, simulation.PendingImpulseCount);
        simulation.ObserveEntities([crossed]);
        Assert.AreEqual(1, simulation.PendingImpulseCount);

        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        LiquidSurfaceImpactDiagnostic diagnostic = simulation.LastDroppedItemImpactDiagnostic;
        Assert.AreEqual(2.25, diagnostic.WorldX, 1.0e-6);
        Assert.AreEqual(1.25, diagnostic.WorldZ, 1.0e-6);
        Assert.AreEqual(4, diagnostic.CellX);
        Assert.AreEqual(2, diagnostic.CellZ);
        Assert.AreEqual(1.5f, diagnostic.DirectionXMetresPerSecond, 1.0e-6f);
        Assert.AreEqual(-8.25f, diagnostic.ImpactVelocityYMetresPerSecond, 1.0e-6f);
        Assert.AreEqual(0.0f, diagnostic.DirectionZMetresPerSecond, 1.0e-6f);
        double lastAirSpeed = Math.Sqrt(
            diagnostic.DirectionXMetresPerSecond * diagnostic.DirectionXMetresPerSecond
            + diagnostic.ImpactVelocityYMetresPerSecond * diagnostic.ImpactVelocityYMetresPerSecond
            + diagnostic.DirectionZMetresPerSecond * diagnostic.DirectionZMetresPerSecond);
        double expectedIncidentEnergy = LiquidPhysicalModel.KineticEnergyJoules(
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            lastAirSpeed);
        Assert.AreEqual(expectedIncidentEnergy, diagnostic.EnergyJoules, expectedIncidentEnergy * 0.001);
        Assert.AreEqual(1, simulation.TotalDroppedItemImpactCount);
    }

    /// <summary>
    /// Verifies a real thrown-item style callback reconstructs the surface crossing, transfers the
    /// measured kinetic-energy loss, and cannot be duplicated by the following periodic sample.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ExactStoneRicochetUsesSurfaceIntersectionAndMeasuredEnergyLoss()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        LiquidProjectileCollisionSample collision = new(
            EntityId: 7001,
            SurfaceClass: LiquidEntitySurfaceClass.ThrownStone,
            WorldX: 2.60,
            WorldY: -0.10,
            WorldZ: 2.25,
            PreviousWorldX: 2.20,
            PreviousWorldY: 0.10,
            PreviousWorldZ: 2.25,
            IncidentMotionX: 0.20f,
            IncidentMotionY: -0.10f,
            IncidentMotionZ: 0.0f,
            OutgoingMotionX: 0.20f,
            OutgoingMotionY: 0.05f,
            OutgoingMotionZ: 0.0f,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            MotionSamplePeriodSeconds: 1.0f / 60.0f);

        simulation.ObserveProjectileLiquidCollisions([collision]);

        Assert.AreEqual(1, simulation.PendingImpulseCount);
        Assert.AreEqual(1, simulation.TotalProjectileImpactCount);
        LiquidProjectileImpactDiagnostic diagnostic = simulation.LastProjectileImpactDiagnostic;
        Assert.AreEqual(LiquidSurfaceImpulseKind.StoneRicochet, diagnostic.ImpulseKind);
        Assert.AreEqual(2.40, diagnostic.WorldX, 1.0e-6);
        Assert.AreEqual(2.25, diagnostic.WorldZ, 1.0e-6);
        Assert.AreEqual(4, diagnostic.CellX);
        Assert.AreEqual(4, diagnostic.CellZ);
        Assert.AreEqual(12.0f, diagnostic.IncidentVelocityXMetresPerSecond, 1.0e-5f);
        Assert.AreEqual(-6.0f, diagnostic.IncidentVelocityYMetresPerSecond, 1.0e-5f);
        Assert.AreEqual(3.0f, diagnostic.OutgoingVelocityYMetresPerSecond, 1.0e-5f);
        Assert.AreEqual(4.725f, diagnostic.EnergyJoules, 1.0e-4f);

        simulation.ObserveEntities([
            new LiquidEntitySurfaceSample(
                collision.EntityId,
                LiquidEntitySurfaceClass.ThrownStone,
                collision.WorldX,
                collision.WorldY,
                collision.WorldZ,
                collision.OutgoingMotionX,
                collision.OutgoingMotionY,
                collision.OutgoingMotionZ,
                FeetInLiquid: true,
                Swimming: false,
                collision.MassKilograms,
                collision.MotionSamplePeriodSeconds)
        ]);
        Assert.AreEqual(1, simulation.PendingImpulseCount);
        Assert.AreEqual(1, simulation.TotalProjectileImpactCount);
    }

    /// <summary>Checks rebounding stones repeat while a non-rebounding arrow enters only once.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void StoneCallbacksRepeatButArrowCallbackIsOneShot()
    {
        LiquidSurfaceSimulation stone = CreateSimulation();
        LiquidProjectileCollisionSample stoneCollision = new(
            7101,
            LiquidEntitySurfaceClass.ThrownStone,
            1.60,
            -0.10,
            1.25,
            1.20,
            0.10,
            1.25,
            0.20f,
            -0.10f,
            0.0f,
            0.20f,
            0.05f,
            0.0f,
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            1.0f / 60.0f);
        stone.ObserveProjectileLiquidCollisions([stoneCollision]);
        stone.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 6.0f, default);
        stone.ObserveProjectileLiquidCollisions([
            stoneCollision with { WorldX = 2.60, WorldZ = 1.75 }
        ]);
        Assert.AreEqual(2, stone.TotalProjectileImpactCount);

        LiquidSurfaceSimulation arrow = CreateSimulation();
        LiquidProjectileCollisionSample arrowCollision = stoneCollision with
        {
            EntityId = 7201,
            SurfaceClass = LiquidEntitySurfaceClass.Projectile,
            MassKilograms = LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            OutgoingMotionY = -0.05f
        };
        arrow.ObserveProjectileLiquidCollisions([arrowCollision]);
        Assert.AreEqual(1, arrow.PendingImpulseCount);
        float expectedArrowEnergy = 0.5f
            * LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms
            * (12.0f * 12.0f + 6.0f * 6.0f);
        Assert.AreEqual(
            expectedArrowEnergy,
            arrow.LastProjectileImpactDiagnostic.EnergyJoules,
            1.0e-5f);
        arrow.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 6.0f, default);
        float[] arrowSurface = new float[arrow.Width * arrow.Depth * 4];
        arrow.WriteGpuTexture(arrowSurface);
        Assert.IsTrue(
            Enumerable.Range(0, arrow.Width * arrow.Depth)
                .Any(index => Math.Abs(arrowSurface[index * 4]) > 1.0e-6f),
            "A real projectile entry must alter the geometric surface field, not only its diagnostic counter.");
        arrow.ObserveProjectileLiquidCollisions([
            arrowCollision with { WorldX = 2.60, WorldZ = 1.75 }
        ]);
        Assert.AreEqual(1, arrow.TotalProjectileImpactCount);
        Assert.AreEqual(
            LiquidEntitySurfaceClass.Projectile,
            arrow.LastProjectileImpactDiagnostic.SurfaceClass);
    }

    /// <summary>
    /// Checks repeat-contact eligibility follows measured motion rather than the vanilla stone class,
    /// allowing modded projectiles to produce distinct physical skips without reopening resting entries.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void GenericProjectileWithMeasuredReboundCanProduceSuccessiveSurfaceContacts()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        LiquidProjectileCollisionSample firstRicochet = new(
            EntityId: 7251,
            SurfaceClass: LiquidEntitySurfaceClass.Projectile,
            WorldX: 1.60,
            WorldY: -0.10,
            WorldZ: 1.25,
            PreviousWorldX: 1.20,
            PreviousWorldY: 0.10,
            PreviousWorldZ: 1.25,
            IncidentMotionX: 0.20f,
            IncidentMotionY: -0.10f,
            IncidentMotionZ: 0.0f,
            OutgoingMotionX: 0.20f,
            OutgoingMotionY: 0.05f,
            OutgoingMotionZ: 0.0f,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceProjectileMassKilograms,
            MotionSamplePeriodSeconds: 1.0f / 60.0f,
            IsServerAuthoritative: true);

        simulation.ObserveProjectileLiquidCollisions([firstRicochet]);
        LiquidProjectileImpactDiagnostic first = simulation.LastProjectileImpactDiagnostic;
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 6.0f, default);
        simulation.ObserveProjectileLiquidCollisions([
            firstRicochet with
            {
                WorldX = 2.60,
                WorldZ = 1.75,
                PreviousWorldX = 2.20,
                PreviousWorldZ = 1.75
            }
        ]);

        Assert.AreEqual(2, simulation.TotalProjectileImpactCount);
        LiquidProjectileImpactDiagnostic second = simulation.LastProjectileImpactDiagnostic;
        Assert.AreEqual(LiquidEntitySurfaceClass.Projectile, second.SurfaceClass);
        Assert.AreEqual(LiquidSurfaceImpulseKind.GenericEntry, second.ImpulseKind);
        Assert.AreEqual(first.EnergyJoules, second.EnergyJoules, 1.0e-6f);
        Assert.AreEqual(4.725f, second.EnergyJoules, 1.0e-4f);
        Assert.IsTrue(second.WorldX > first.WorldX);
    }

    /// <summary>
    /// Ensures an integrated server's exact motion wins even when the inflated client interpolation
    /// sample reached the detached callback batch first.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void IntegratedServerProjectileMotionPrecedesRemoteClientReconstruction()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        LiquidProjectileCollisionSample authoritative = new(
            EntityId: 7301,
            SurfaceClass: LiquidEntitySurfaceClass.Projectile,
            WorldX: 2.60,
            WorldY: -0.10,
            WorldZ: 2.25,
            PreviousWorldX: 2.30,
            PreviousWorldY: 0.20,
            PreviousWorldZ: 2.05,
            IncidentMotionX: -0.30f,
            IncidentMotionY: -0.34f,
            IncidentMotionZ: -0.20f,
            OutgoingMotionX: -0.30f,
            OutgoingMotionY: -0.34f,
            OutgoingMotionZ: -0.20f,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            MotionSamplePeriodSeconds: 1.0f / 60.0f,
            IsServerAuthoritative: true);
        LiquidProjectileCollisionSample remote = authoritative with
        {
            IncidentMotionX = authoritative.IncidentMotionX * 1.559f,
            IncidentMotionY = authoritative.IncidentMotionY * 1.559f,
            IncidentMotionZ = authoritative.IncidentMotionZ * 1.559f,
            OutgoingMotionX = authoritative.OutgoingMotionX * 1.559f,
            OutgoingMotionY = authoritative.OutgoingMotionY * 1.559f,
            OutgoingMotionZ = authoritative.OutgoingMotionZ * 1.559f,
            IsServerAuthoritative = false
        };

        LiquidSurfaceRuntime.ObservePrioritizedProjectileCollisions(
            simulation,
            [remote, authoritative]);

        Assert.AreEqual(1, simulation.TotalProjectileImpactCount);
        LiquidProjectileImpactDiagnostic diagnostic = simulation.LastProjectileImpactDiagnostic;
        Assert.IsTrue(diagnostic.IsServerAuthoritative);
        float expectedEnergy = 0.5f
            * LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms
            * (18.0f * 18.0f + 20.4f * 20.4f + 12.0f * 12.0f);
        Assert.AreEqual(expectedEnergy, diagnostic.EnergyJoules, 1.0e-4f);
    }

    /// <summary>
    /// Verifies authoritative provenance survives later callback drains while distinct server-side
    /// stone contacts remain eligible as physical ricochets.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ServerProjectileProvenancePersistsWithoutSuppressingLaterServerRicochets()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        LiquidProjectileCollisionSample firstServerRicochet = new(
            EntityId: 7401,
            SurfaceClass: LiquidEntitySurfaceClass.ThrownStone,
            WorldX: 1.60,
            WorldY: -0.10,
            WorldZ: 1.25,
            PreviousWorldX: 1.20,
            PreviousWorldY: 0.10,
            PreviousWorldZ: 1.25,
            IncidentMotionX: 0.20f,
            IncidentMotionY: -0.10f,
            IncidentMotionZ: 0.0f,
            OutgoingMotionX: 0.20f,
            OutgoingMotionY: 0.05f,
            OutgoingMotionZ: 0.0f,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            MotionSamplePeriodSeconds: 1.0f / 60.0f,
            IsServerAuthoritative: true);

        simulation.ObserveProjectileLiquidCollisions([firstServerRicochet]);
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 8.0f, default);
        LiquidSurfaceSubgridImpactDiagnostic firstPacket =
            simulation.LastSubgridImpactDiagnostic;

        LiquidProjectileCollisionSample delayedClientReconstruction = firstServerRicochet with
        {
            IncidentMotionX = firstServerRicochet.IncidentMotionX * 1.559f,
            IncidentMotionY = firstServerRicochet.IncidentMotionY * 1.559f,
            OutgoingMotionX = firstServerRicochet.OutgoingMotionX * 1.559f,
            OutgoingMotionY = firstServerRicochet.OutgoingMotionY * 1.559f,
            IsServerAuthoritative = false
        };
        simulation.ObserveProjectileLiquidCollisions([delayedClientReconstruction]);

        Assert.AreEqual(1, simulation.TotalProjectileImpactCount);
        Assert.AreEqual(0, simulation.PendingImpulseCount);

        LiquidProjectileCollisionSample secondServerRicochet = firstServerRicochet with
        {
            WorldX = 2.60,
            WorldZ = 1.75,
            PreviousWorldX = 2.20,
            PreviousWorldZ = 1.75
        };
        simulation.ObserveProjectileLiquidCollisions([secondServerRicochet]);

        Assert.AreEqual(2, simulation.TotalProjectileImpactCount);
        Assert.AreEqual(1, simulation.PendingImpulseCount);
        Assert.IsTrue(simulation.LastProjectileImpactDiagnostic.IsServerAuthoritative);
        LiquidProjectileImpactDiagnostic secondDiagnostic =
            simulation.LastProjectileImpactDiagnostic;
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Span<LiquidSurfaceSubgridImpactDiagnostic> packets =
            stackalloc LiquidSurfaceSubgridImpactDiagnostic[4];
        int packetCount = simulation.WriteSubgridImpactsAfter(0, packets);
        Assert.AreEqual(2, packetCount);
        Assert.AreEqual(1, packets[0].Sequence);
        Assert.AreEqual(2, packets[1].Sequence);
        Assert.AreEqual(firstPacket.WorldX, packets[0].WorldX, 1.0e-6);
        Assert.AreEqual(firstPacket.WorldZ, packets[0].WorldZ, 1.0e-6);
        Assert.AreEqual(secondDiagnostic.WorldX, packets[1].WorldX, 1.0e-6);
        Assert.AreEqual(secondDiagnostic.WorldZ, packets[1].WorldZ, 1.0e-6);
    }

    /// <summary>
    /// Checks exact projectile support follows object/cavity physics at fine resolution, falls back
    /// to one resolvable cell at runtime resolution, and retains only the coupled impact joules.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ProjectileImpactSupportIsLocalizedPhysicalAndEnergyNormalized()
    {
        LiquidSurfaceDynamics dynamics = CreateDynamics(wavelengthMetres: 4.0f);
        LiquidSurfacePhysicalProperties water = new(
            998.2f,
            0.001002f,
            0.07275f,
            0.0f,
            0.020f);
        const float arrowEnergyJoules = 26.907f;
        const float stoneEnergyJoules = 4.725f;

        float legacySupport = LiquidSurfaceSimulation.ResolveImpulseSupportRadius(
            in dynamics,
            cellSize: 0.5f);
        float arrowSupport = LiquidSurfaceSimulation.ResolveProjectileImpulseSupportRadius(
            LiquidEntitySurfaceClass.Projectile,
            LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            arrowEnergyJoules,
            in dynamics,
            in water,
            cellSize: 0.5f);
        float stoneSupport = LiquidSurfaceSimulation.ResolveProjectileImpulseSupportRadius(
            LiquidEntitySurfaceClass.ThrownStone,
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            stoneEnergyJoules,
            in dynamics,
            in water,
            cellSize: 0.5f);

        Assert.AreEqual(2.0f, legacySupport, 1.0e-6f);
        Assert.AreEqual(0.5f, arrowSupport, 1.0e-6f);
        Assert.AreEqual(0.5f, stoneSupport, 1.0e-6f);
        Assert.IsTrue(arrowSupport < legacySupport);
        Assert.IsTrue(stoneSupport < legacySupport);

        float fineSupport = LiquidSurfaceSimulation.ResolveProjectileImpulseSupportRadius(
            LiquidEntitySurfaceClass.Projectile,
            LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            arrowEnergyJoules,
            in dynamics,
            in water,
            cellSize: 0.025f);
        Assert.AreEqual(0.025f, fineSupport, 1.0e-6f);

        LiquidProjectileWaveEnergyPartition arrowPartition =
            LiquidSurfaceSimulation.ResolveProjectileWaveEnergyPartition(
                LiquidEntitySurfaceClass.Projectile,
                LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
                arrowEnergyJoules,
                in water,
                cellSize: 0.5f);
        double arrowWaveNumber = Math.PI / (2.0 * 0.5);
        double arrowFrontalArea = Math.PI * 0.004 * 0.004;
        double expectedArrowSurfaceEnergy = arrowEnergyJoules * (1.0 - Math.Exp(
            -water.DensityKilogramsPerCubicMetre * 0.40 * arrowFrontalArea
            / (LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms * arrowWaveNumber)));
        double expectedArrowCavityRadius = 0.004 * 1.15;
        double expectedArrowResolvedFraction = 1.0 - Math.Exp(
            -Math.Pow(arrowWaveNumber * expectedArrowCavityRadius, 2.0));
        Assert.AreEqual(expectedArrowSurfaceEnergy, arrowPartition.SurfaceCoupledEnergyJoules, 1.0e-6);
        Assert.AreEqual(expectedArrowCavityRadius, arrowPartition.CavityRadiusMetres, 1.0e-7);
        Assert.AreEqual(expectedArrowResolvedFraction, arrowPartition.ResolvedFraction, 1.0e-7);
        Assert.AreEqual(
            arrowPartition.SurfaceCoupledEnergyJoules,
            arrowPartition.ResolvedWaveEnergyJoules + arrowPartition.SubgridWaveEnergyJoules,
            1.0e-6f);
        Assert.IsTrue(arrowPartition.NearInterfaceEnergyJoules is > 0.040f and < 0.043f);
        Assert.IsTrue(arrowPartition.CavityEnergyJoules is > 0.012f and < 0.018f);
        Assert.IsTrue(arrowPartition.CapillaryPacketEnergyJoules is > 0.0004f and < 0.0005f);
        Assert.IsTrue(arrowPartition.SplashEnergyJoules is > 0.025f and < 0.028f);
        Assert.AreEqual(
            arrowPartition.NearInterfaceEnergyJoules,
            arrowPartition.CavityEnergyJoules
                + arrowPartition.CapillaryPacketEnergyJoules
                + arrowPartition.SplashEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(
            arrowPartition.SubgridWaveEnergyJoules,
            arrowPartition.CavityEnergyJoules
                + arrowPartition.CapillaryPacketEnergyJoules
                + arrowPartition.SplashEnergyJoules
                + arrowPartition.WakeEnergyJoules,
            1.0e-6f);
        Assert.IsTrue(arrowPartition.WakeEnergyJoules > 2.6f);
        Assert.IsTrue(arrowPartition.ResolvedWaveEnergyJoules < 0.003f);
        Assert.AreEqual(
            0.0184f,
            LiquidSurfaceSimulation.ResolveProjectileSubgridWavelengthMetres(
                arrowPartition.CavityRadiusMetres,
                0.5f),
            1.0e-6f);
        Assert.AreEqual(
            0.012f,
            LiquidSurfaceSimulation.ResolveProjectileSubgridWavelengthMetres(float.NaN, 0.5f),
            0.0f);
        Assert.AreEqual(
            0.012f,
            LiquidSurfaceSimulation.ResolveProjectileSubgridWavelengthMetres(0.1f, 0.0f),
            0.0f);

        LiquidProjectileWaveEnergyPartition stonePartition =
            LiquidSurfaceSimulation.ResolveProjectileWaveEnergyPartition(
                LiquidEntitySurfaceClass.ThrownStone,
                LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
                1.9996f,
                in water,
                cellSize: 0.5f);
        Assert.AreEqual(1.9996f, stonePartition.SurfaceCoupledEnergyJoules, 1.0e-6f);
        Assert.IsTrue(stonePartition.CavityRadiusMetres > 0.09f);
        Assert.IsTrue(stonePartition.ResolvedWaveEnergyJoules > 0.25f);
        Assert.IsTrue(stonePartition.ResolvedWaveEnergyJoules < 0.35f);
        Assert.AreEqual(
            stonePartition.SurfaceCoupledEnergyJoules,
            stonePartition.ResolvedWaveEnergyJoules + stonePartition.SubgridWaveEnergyJoules,
            1.0e-6f);

        LiquidSurfaceSimulation simulation = CreateSimulation(dynamics, water);
        LiquidProjectileCollisionSample collision = new(
            EntityId: 7501,
            SurfaceClass: LiquidEntitySurfaceClass.Projectile,
            WorldX: 2.60,
            WorldY: -0.10,
            WorldZ: 2.25,
            PreviousWorldX: 2.30,
            PreviousWorldY: 0.20,
            PreviousWorldZ: 2.05,
            IncidentMotionX: -0.30f,
            IncidentMotionY: -0.34f,
            IncidentMotionZ: -0.20f,
            OutgoingMotionX: -0.30f,
            OutgoingMotionY: -0.34f,
            OutgoingMotionZ: -0.20f,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            MotionSamplePeriodSeconds: 1.0f / 60.0f,
            IsServerAuthoritative: true);
        simulation.ObserveProjectileLiquidCollisions([collision]);
        MethodInfo applyPendingImpulses = typeof(LiquidSurfaceSimulation).GetMethod(
            "ApplyPendingImpulses",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("Missing ApplyPendingImpulses solver method.");
        applyPendingImpulses.Invoke(simulation, null);

        LiquidProjectileImpactDiagnostic diagnostic = simulation.LastProjectileImpactDiagnostic;
        double coupledEnergyJoules = diagnostic.ResolvedWaveEnergyJoules;
        float resolvedEnergyJoules = simulation.ComputeTotalEnergy();
        Assert.IsTrue(
            resolvedEnergyJoules <= coupledEnergyJoules * 1.0001,
            $"Localized kernel created {resolvedEnergyJoules:F6} J from {coupledEnergyJoules:F6} coupled J.");
        Assert.AreEqual(coupledEnergyJoules, resolvedEnergyJoules, coupledEnergyJoules * 0.001);
        Assert.AreEqual(
            diagnostic.SurfaceCoupledEnergyJoules,
            diagnostic.ResolvedWaveEnergyJoules + diagnostic.SubgridWaveEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(0.5f, diagnostic.SupportRadiusWorldBlocks, 1.0e-6f);

        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        LiquidSurfaceSubgridImpactDiagnostic arrowPacket =
            simulation.LastSubgridImpactDiagnostic;
        Assert.AreEqual(1, arrowPacket.Sequence);
        Assert.AreEqual(diagnostic.Sequence, arrowPacket.SourceSequence);
        Assert.AreEqual(diagnostic.EntityId, arrowPacket.EntityId);
        Assert.AreEqual(LiquidEntitySurfaceClass.Projectile, arrowPacket.SurfaceClass);
        Assert.AreEqual(diagnostic.WorldX, arrowPacket.WorldX, 1.0e-6);
        Assert.AreEqual(diagnostic.WorldZ, arrowPacket.WorldZ, 1.0e-6);
        Assert.IsTrue(arrowPacket.PeakDisplacement > 0.0f);
        Assert.AreEqual(
            diagnostic.SurfaceCoupledEnergyJoules,
            arrowPacket.SurfaceCoupledEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(
            diagnostic.ResolvedWaveEnergyJoules,
            arrowPacket.ResolvedWaveEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(
            diagnostic.SubgridWaveEnergyJoules,
            arrowPacket.SubgridWaveEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(
            diagnostic.CapillaryPacketEnergyJoules,
            arrowPacket.RenderedPacketEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(
            diagnostic.CavityEnergyJoules + diagnostic.SplashEnergyJoules,
            arrowPacket.LocalSplashEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(
            diagnostic.WakeEnergyJoules,
            arrowPacket.WakeEnergyJoules,
            1.0e-6f);
        Assert.AreEqual(
            arrowPacket.SubgridWaveEnergyJoules,
            arrowPacket.RenderedPacketEnergyJoules
                + arrowPacket.LocalSplashEnergyJoules
                + arrowPacket.WakeEnergyJoules,
            1.0e-6f);
        double packetWaveNumber = 2.0 * Math.PI / arrowPacket.DominantWavelengthMetres;
        Assert.IsTrue(
            packetWaveNumber * arrowPacket.PeakDisplacement <= 0.35001,
            "Arrow capillary packet exceeded the linear-wave steepness limit.");
        Assert.IsTrue(arrowPacket.PeakDisplacement is > 0.0008f and < 0.0011f);
        Assert.AreEqual(0.10f, arrowPacket.SplashRadiusWorldBlocks, 1.0e-6f);
        Assert.AreEqual(0.045f, arrowPacket.SplashReleaseSeconds, 1.0e-6f);
        Assert.IsTrue(arrowPacket.SplashPeakDisplacement is > 0.03f and < 0.04f);
        Assert.IsTrue(arrowPacket.DominantWavelengthMetres < 4.0f * simulation.CellSize);
    }

    /// <summary>Checks renderer packet identity cannot collide across source-local counters.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SubgridRendererUsesGlobalSequenceAndSourceIdentity()
    {
        LiquidSurfaceSubgridImpactDiagnostic active = default;
        LiquidSurfaceSubgridImpactDiagnostic arrow = active with
        {
            Sequence = 1,
            SourceSequence = 1,
            EntityId = 8001,
            SurfaceClass = LiquidEntitySurfaceClass.Projectile,
            SubgridWaveEnergyJoules = 1.0f
        };
        Assert.IsTrue(FilmicDisplayRenderer.IsNewSubgridImpact(in active, in arrow));
        Assert.IsFalse(FilmicDisplayRenderer.IsNewSubgridImpact(in arrow, in arrow));

        LiquidSurfaceSubgridImpactDiagnostic droppedItem = arrow with
        {
            EntityId = 8002,
            SurfaceClass = LiquidEntitySurfaceClass.DroppedItem
        };
        Assert.IsTrue(
            FilmicDisplayRenderer.IsNewSubgridImpact(in arrow, in droppedItem),
            "Equal source-local sequence numbers must not collide across entity/source identity.");

        LiquidSurfaceSubgridImpactDiagnostic nextStone = droppedItem with
        {
            Sequence = 2,
            EntityId = 8003,
            SurfaceClass = LiquidEntitySurfaceClass.ThrownStone
        };
        Assert.IsTrue(FilmicDisplayRenderer.IsNewSubgridImpact(in droppedItem, in nextStone));

        Span<LiquidSurfaceSubgridImpactDiagnostic> slots =
            stackalloc LiquidSurfaceSubgridImpactDiagnostic[
                FilmicDisplayRenderer.MaximumActiveSubgridImpactCount];
        Span<float> ages = stackalloc float[FilmicDisplayRenderer.MaximumActiveSubgridImpactCount];
        Assert.AreEqual(0, FilmicDisplayRenderer.SelectSubgridImpactSlot(slots, ages, 0));
        for (int index = 0; index < slots.Length; index++)
        {
            slots[index] = arrow with { Sequence = index + 1 };
        }
        ages[0] = 1.0f;
        ages[1] = 5.0f;
        ages[2] = 2.0f;
        ages[3] = 4.0f;
        Assert.AreEqual(
            1,
            FilmicDisplayRenderer.SelectSubgridImpactSlot(slots, ages, 2),
            "A fifth packet must replace the oldest slot, not the immediately preceding ricochet.");
    }

    /// <summary>Verifies one solver orders dropped-item and projectile packets in one global stream.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SubgridPacketSequenceIsSharedAcrossImpactClasses()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        LiquidEntitySurfaceSample above = new(
            EntityId: 8101,
            SurfaceClass: LiquidEntitySurfaceClass.DroppedItem,
            WorldX: 2.25,
            WorldY: 1.0,
            WorldZ: 2.25,
            MotionX: 0.0f,
            MotionY: 0.0f,
            MotionZ: 0.0f,
            FeetInLiquid: false,
            Swimming: false,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            MotionSamplePeriodSeconds: 1.0f);
        simulation.ObserveEntities([above]);
        simulation.ObserveEntities([
            above with { WorldY = 0.0, FeetInLiquid = true }
        ]);
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        LiquidSurfaceSubgridImpactDiagnostic droppedPacket =
            simulation.LastSubgridImpactDiagnostic;
        Assert.AreEqual(1, droppedPacket.Sequence);
        Assert.AreEqual(LiquidEntitySurfaceClass.DroppedItem, droppedPacket.SurfaceClass);
        Assert.AreEqual(1, simulation.LastDroppedItemImpactDiagnostic.Sequence);

        LiquidProjectileCollisionSample arrow = new(
            EntityId: 8102,
            SurfaceClass: LiquidEntitySurfaceClass.Projectile,
            WorldX: 3.10,
            WorldY: -0.10,
            WorldZ: 2.75,
            PreviousWorldX: 3.40,
            PreviousWorldY: 0.20,
            PreviousWorldZ: 2.95,
            IncidentMotionX: -0.30f,
            IncidentMotionY: -0.34f,
            IncidentMotionZ: -0.20f,
            OutgoingMotionX: -0.30f,
            OutgoingMotionY: -0.34f,
            OutgoingMotionZ: -0.20f,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            MotionSamplePeriodSeconds: 1.0f / 60.0f,
            IsServerAuthoritative: true);
        simulation.ObserveProjectileLiquidCollisions([arrow]);
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        LiquidSurfaceSubgridImpactDiagnostic arrowPacket =
            simulation.LastSubgridImpactDiagnostic;
        Assert.AreEqual(2, arrowPacket.Sequence);
        Assert.AreEqual(1, arrowPacket.SourceSequence);
        Assert.AreEqual(arrow.EntityId, arrowPacket.EntityId);
        Assert.AreEqual(LiquidEntitySurfaceClass.Projectile, arrowPacket.SurfaceClass);
        Assert.AreEqual(
            1,
            simulation.LastDroppedItemImpactDiagnostic.Sequence,
            "Publishing a projectile packet must not overwrite dropped-item compatibility evidence.");
    }

    /// <summary>Checks semantic projectile masses never inherit the API knockback coefficient.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ProjectileMassesUseDocumentedSemanticPriors()
    {
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
            LiquidSurfaceWorldInputs.EstimateProjectilePseudoMassKilograms(
                new Vintagestory.API.Common.AssetLocation("game", "arrow-flint")));
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceSpearMassKilograms,
            LiquidSurfaceWorldInputs.EstimateProjectilePseudoMassKilograms(
                new Vintagestory.API.Common.AssetLocation("game", "javelin-copper")));
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceProjectileMassKilograms,
            LiquidSurfaceWorldInputs.EstimateProjectilePseudoMassKilograms(
                new Vintagestory.API.Common.AssetLocation("othermod", "magicbolt")));
        Assert.IsTrue(LiquidSurfaceWorldInputs.IsSkippableStoneProjectile(
            new Vintagestory.API.Common.AssetLocation("game", "stone-granite")));
        Assert.IsFalse(LiquidSurfaceWorldInputs.IsSkippableStoneProjectile(
            new Vintagestory.API.Common.AssetLocation("game", "arrow-flint")));
        Assert.AreEqual(
            0.2,
            LiquidSurfaceWorldInputs.LiquidCollisionSubstepDisplacementScale(
                new Vintagestory.API.MathTools.Vec3d(0.20, -0.10, 0.0)),
            1.0e-6);
        Assert.AreEqual(
            2.0,
            LiquidSurfaceWorldInputs.LiquidCollisionSubstepDisplacementScale(
                new Vintagestory.API.MathTools.Vec3d(0.05, 0.0, 0.0)),
            1.0e-6);
    }

    /// <summary>Checks that the discrete impact kernel retains the requested coupled joules.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ImpactKernelPreservesCoupledEnergyAcrossAResolvedArea()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        const float massKilograms = 4.0f;
        const float speedMetresPerSecond = 1.0f;
        Assert.IsTrue(simulation.QueueImpact(
            2.25,
            2.25,
            massKilograms,
            speedMetresPerSecond));

        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        float resolvedJoules = simulation.ComputeTotalEnergy();
        double incidentJoules = LiquidPhysicalModel.KineticEnergyJoules(
            massKilograms,
            speedMetresPerSecond);
        double expectedCoupledJoules = incidentJoules * 0.04;

        Assert.AreEqual(expectedCoupledJoules, resolvedJoules, expectedCoupledJoules * 0.12);
    }

    /// <summary>Checks fixed-step results are independent of render-frame partitioning.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void FixedStepIntegrationIsFramePartitionIndependent()
    {
        LiquidSurfaceSimulation combined = CreateSimulation();
        LiquidSurfaceSimulation partitioned = CreateSimulation();
        combined.QueueImpact(2.25, 2.25, 2.0f, 1.5f);
        partitioned.QueueImpact(2.25, 2.25, 2.0f, 1.5f);

        combined.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 2.0f, default);
        partitioned.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        partitioned.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        for (int z = 0; z < combined.Depth; z++)
        {
            for (int x = 0; x < combined.Width; x++)
            {
                Assert.AreEqual(
                    combined.GetCellSample(x, z),
                    partitioned.GetCellSample(x, z));
            }
        }
    }

    /// <summary>
    /// Guards the zero-copy fixed-step promotion: completed current and scratch grids exchange
    /// ownership on every step while the public state remains valid across consecutive steps.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void FixedStepPromotesCompletedStateBySwappingReusableBuffers()
    {
        LiquidSurfaceSimulation simulation = CreateSimulation();
        float[] initialHeights = GetBuffer(simulation, "heights");
        float[] initialVelocities = GetBuffer(simulation, "velocities");
        float[] initialNextHeights = GetBuffer(simulation, "nextHeights");
        float[] initialNextVelocities = GetBuffer(simulation, "nextVelocities");
        Assert.IsTrue(simulation.QueueImpact(2.25, 2.25, 2.0f, 1.5f));

        Assert.AreEqual(
            1,
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default));
        Assert.AreSame(initialNextHeights, GetBuffer(simulation, "heights"));
        Assert.AreSame(initialNextVelocities, GetBuffer(simulation, "velocities"));
        Assert.AreSame(initialHeights, GetBuffer(simulation, "nextHeights"));
        Assert.AreSame(initialVelocities, GetBuffer(simulation, "nextVelocities"));
        Assert.IsTrue(simulation.ComputeTotalEnergy() > 0.0f);

        Assert.AreEqual(
            1,
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default));
        Assert.AreSame(initialHeights, GetBuffer(simulation, "heights"));
        Assert.AreSame(initialVelocities, GetBuffer(simulation, "velocities"));
        Assert.AreSame(initialNextHeights, GetBuffer(simulation, "nextHeights"));
        Assert.AreSame(initialNextVelocities, GetBuffer(simulation, "nextVelocities"));
        Assert.IsTrue(simulation.ComputeTotalEnergy() > 0.0f);
    }

    /// <summary>
    /// Checks that reauthoring cells invalidates strength/direction-dependent wind terms instead
    /// of retaining forcing cached for the previous material profile.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ReconfiguredSurfaceInvalidatesCachedWindForcing()
    {
        LiquidSurfaceDynamics windyDynamics = CreateDynamics();
        LiquidSurfaceDynamics stillDynamics = windyDynamics with { WindCoupling = 0.0f };
        LiquidSurfacePhysicalProperties physics = new(
            998.2f,
            0.001002f,
            0.07275f,
            0.0f,
            0.020f);
        LiquidSurfaceSimulation windInput = CreateSimulation(windyDynamics, physics);
        LiquidSurfaceSimulation stillInput = CreateSimulation(windyDynamics, physics);
        LiquidSurfaceForcing wind = new(5.0f, 2.0f, 0.0f);

        windInput.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in wind);
        stillInput.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in wind);
        Reconfigure(windInput, stillDynamics, physics);
        Reconfigure(stillInput, stillDynamics, physics);

        windInput.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in wind);
        stillInput.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        for (int z = 0; z < windInput.Depth; z++)
        {
            for (int x = 0; x < windInput.Width; x++)
            {
                Assert.AreEqual(
                    stillInput.GetCellSample(x, z),
                    windInput.GetCellSample(x, z));
            }
        }
    }

    /// <summary>Checks resolved energy fraction and viscosity keep water mobile while honey/lava damp.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void WaterRemainsMoreMobileThanHoneyAndLavaUnderWind()
    {
        LiquidSurfaceDynamics dynamics = CreateDynamics(wavelengthMetres: 2.0f);
        LiquidSurfaceSimulation water = CreateSimulation(
            dynamics,
            new LiquidSurfacePhysicalProperties(998.2f, 0.001002f, 0.07275f, 0.0f, 0.020f));
        LiquidSurfaceSimulation honey = CreateSimulation(
            dynamics,
            new LiquidSurfacePhysicalProperties(1_496.0f, 7.85f, 0.050f, 0.0f, 0.003f));
        LiquidSurfaceSimulation lava = CreateSimulation(
            dynamics,
            new LiquidSurfacePhysicalProperties(2_700.0f, 100.0f, 0.350f, 0.0f, 0.005f));
        LiquidSurfaceForcing wind = new(5.0f, 0.0f, 0.0f);

        water.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in wind);
        honey.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in wind);
        lava.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in wind);

        float waterVelocity = Math.Abs(water.GetCellSample(4, 4).Velocity);
        float honeyVelocity = Math.Abs(honey.GetCellSample(4, 4).Velocity);
        float lavaVelocity = Math.Abs(lava.GetCellSample(4, 4).Velocity);
        Assert.IsTrue(waterVelocity > 0.0f);
        Assert.IsTrue(honeyVelocity < waterVelocity);
        Assert.IsTrue(lavaVelocity < waterVelocity);
    }

    /// <summary>Checks rainfall depth flux becomes finite drop kinetic energy on exposed cells only.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void RainfallRateUsesDropVolumeAndTerminalKineticEnergy()
    {
        LiquidSurfaceSimulation exposed = CreateSimulation(rainExposed: true);
        LiquidSurfaceSimulation sheltered = CreateSimulation(rainExposed: false);
        double dropRadiusMetres = 0.001;
        double dropVolumeCubicMetres = 4.0 / 3.0 * Math.PI
            * dropRadiusMetres * dropRadiusMetres * dropRadiusMetres;
        double exposedArea = exposed.Width * exposed.Depth * exposed.CellSize * exposed.CellSize;
        float oneDropPerStepRate = (float)(dropVolumeCubicMetres
            / (exposedArea * LiquidSurfaceSimulation.FixedStepSeconds));
        LiquidSurfaceForcing rain = new(0.0f, 0.0f, oneDropPerStepRate);

        for (int step = 0; step < 4; step++)
        {
            exposed.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in rain);
            sheltered.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in rain);
        }

        Assert.IsTrue(exposed.ComputeTotalEnergy() > 0.0f);
        Assert.AreEqual(0.0f, sheltered.ComputeTotalEnergy(), 0.0f);
    }

    /// <summary>Checks invalid density, viscosity, tension, and extra damping are rejected.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void InvalidPhysicalSurfacePropertiesAreRejected()
    {
        LiquidSurfaceDynamics dynamics = CreateDynamics();
        LiquidSurfaceSimulation simulation = new(0, 0, 1, 1);
        LiquidSurfacePhysicalProperties invalidDensity = new(0.0f, 0.001f, 0.072f);
        LiquidSurfacePhysicalProperties invalidViscosity = new(1_000.0f, -0.001f, 0.072f);
        LiquidSurfacePhysicalProperties invalidTension = new(1_000.0f, 0.001f, -0.072f);
        LiquidSurfacePhysicalProperties invalidDamping = new(1_000.0f, 0.001f, 0.072f, -1.0f);
        LiquidSurfacePhysicalProperties invalidResolvedFraction = new(
            1_000.0f,
            0.001f,
            0.072f,
            0.0f,
            1.1f);
        LiquidSurfacePhysicalProperties validPhysics = new(1_000.0f, 0.001f, 0.072f);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Set(in invalidDensity));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Set(in invalidViscosity));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Set(in invalidTension));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Set(in invalidDamping));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Set(in invalidResolvedFraction));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            simulation.SetSurfaceCell(
                0,
                0,
                0.0f,
                1,
                in dynamics,
                in validPhysics,
                true,
                depthMetres: 0.0f));
        return;

        void Set(in LiquidSurfacePhysicalProperties physics)
        {
            simulation.SetSurfaceCell(0, 0, 0.0f, 1, in dynamics, in physics, true);
        }
    }

    /// <summary>Creates a physically bounded dynamics profile for deterministic tests.</summary>
    /// <param name="wavelengthMetres">Dominant resolved wavelength in metres.</param>
    /// <returns>Dynamics with four-percent impact coupling and no bubbles.</returns>
    private static LiquidSurfaceDynamics CreateDynamics(float wavelengthMetres = 2.0f)
    {
        return new LiquidSurfaceDynamics(
            WindCoupling: 0.16f,
            WaveAmplitude: 0.20f,
            WaveLength: wavelengthMetres,
            WaveSpeed: 99.0f,
            Damping: 0.0f,
            ImpactResponse: 0.04f,
            SurfaceTension: 0.072f,
            BubbleRate: 0.0f,
            BubbleRadiusMinimum: 0.0f,
            BubbleRadiusMaximum: 0.0f,
            BubbleRiseDuration: 0.0f,
            BubbleBurstStrength: 0.0f,
            BubbleEmissionBoost: 0.0f);
    }

    /// <summary>Creates a 9x9 connected test surface centered at world position 2.25/2.25.</summary>
    /// <param name="rainExposed">Whether every cell receives rainfall.</param>
    /// <returns>Water-like initialized simulation.</returns>
    private static LiquidSurfaceSimulation CreateSimulation(bool rainExposed = false)
    {
        LiquidSurfaceDynamics dynamics = CreateDynamics();
        LiquidSurfacePhysicalProperties physics = new(
            998.2f,
            0.001002f,
            0.07275f,
            0.0f,
            0.020f);
        return CreateSimulation(dynamics, physics, rainExposed);
    }

    /// <summary>Creates a connected test surface from explicit dynamics and SI properties.</summary>
    /// <param name="dynamics">Wave scale and coupling controls.</param>
    /// <param name="physics">Density, viscosity, and surface tension in SI.</param>
    /// <param name="rainExposed">Whether every cell receives rainfall.</param>
    /// <returns>Initialized deterministic simulation.</returns>
    private static LiquidSurfaceSimulation CreateSimulation(
        LiquidSurfaceDynamics dynamics,
        LiquidSurfacePhysicalProperties physics,
        bool rainExposed = false)
    {
        LiquidSurfaceSimulation simulation = new(
            originWorldX: 0,
            originWorldZ: 0,
            width: 9,
            depth: 9,
            cellSize: 0.5f,
            deterministicSeed: 0x12345678u);
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                simulation.SetSurfaceCell(
                    x,
                    z,
                    0.0f,
                    1,
                    in dynamics,
                    in physics,
                    rainExposed);
            }
        }

        return simulation;
    }

    /// <summary>Reconfigures every cell while preserving the solver's dynamic state.</summary>
    /// <param name="simulation">Simulation to update.</param>
    /// <param name="dynamics">Replacement authored dynamics.</param>
    /// <param name="physics">Replacement physical material properties.</param>
    private static void Reconfigure(
        LiquidSurfaceSimulation simulation,
        LiquidSurfaceDynamics dynamics,
        LiquidSurfacePhysicalProperties physics)
    {
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                simulation.SetSurfaceCell(
                    x,
                    z,
                    0.0f,
                    1,
                    in dynamics,
                    in physics,
                    rainExposed: false);
            }
        }
    }

    /// <summary>Reads one private solver buffer for the zero-copy ownership regression test.</summary>
    /// <param name="simulation">Simulation owning the requested buffer.</param>
    /// <param name="fieldName">Private buffer field name.</param>
    /// <returns>The buffer currently assigned to the field.</returns>
    private static float[] GetBuffer(LiquidSurfaceSimulation simulation, string fieldName)
    {
        FieldInfo field = typeof(LiquidSurfaceSimulation).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Missing solver buffer '{fieldName}'.");
        return (float[])(field.GetValue(simulation)
            ?? throw new AssertFailedException($"Null solver buffer '{fieldName}'."));
    }
}
