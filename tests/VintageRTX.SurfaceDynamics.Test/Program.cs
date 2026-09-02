using System.Reflection;
using VintageRTX.Rendering;

namespace VintageRTX.SurfaceDynamics.Test;

/// <summary>
/// Hosts the command-line test runner and maps any failed contract to a non-zero process exit code.
/// </summary>
internal static class Program
{
    private static readonly (string Name, Action Body)[] Tests =
    [
            ("fixed-step bounded stability", TestBoundedStability),
            ("radial wave propagation", TestPropagation),
            ("asset-authored honey and lava damping", TestViscousDamping),
            ("liquid profile confinement", TestProfileConfinement),
            ("directional wind normals", TestDirectionalWind),
            ("generic object entry", TestGenericObjectEntry),
            ("dropped item stone-reference entry", TestDroppedItemEntry),
            ("exposed deterministic rainfall", TestRainfall),
            ("game bobber wake", TestBobber),
            ("near-surface fish wake", TestFish),
            ("thrown-stone ricochet", TestRicochet),
            ("asset-authored bubble burst", TestBubbleBurst),
            ("resolution-independent bubble density", TestBubbleDensityAndLocality),
            ("bounded event budget", TestEventBudget),
            ("compact GPU output", TestGpuOutput),
            ("constructor and surface edge contracts", TestEdgeContracts),
            ("entity tracker collision and wake fallbacks", TestTrackerAndWakeFallbacks),
            ("zero-allocation steady-state loop", TestSteadyStateAllocations)
    ];

    /// <summary>
    /// Gets the stable scenario sequence exposed to MSTest; ordering remains deterministic for reproducible diagnostics.
    /// </summary>
    internal static IEnumerable<string> TestNames => Tests.Select(static test => test.Name);

    /// <summary>
    /// Executes test as an isolated test step and propagates failures to the owning suite.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    internal static void RunTest(string name)
    {
        foreach ((string testName, Action body) in Tests)
        {
            if (string.Equals(testName, name, StringComparison.Ordinal))
            {
                body();
                return;
            }
        }

        throw new KeyNotFoundException($"Unknown liquid-surface test '{name}'.");
    }

    /// <summary>
    /// Runs every registered check and returns zero only when all test contracts pass.
    /// </summary>
    /// <returns>Zero when every registered contract passes; otherwise a non-zero process exit code.</returns>
    public static int Main()
    {

        int failures = 0;
        foreach ((string name, Action body) in Tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"Liquid surface dynamics: {Tests.Length - failures}/{Tests.Length} passed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Verifies the bounded Stability regression contract against deterministic fixture data.
    /// </summary>
    private static void TestBoundedStability()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(
            waveAmplitude: 0.12f,
            waveLength: 2.0f,
            waveSpeed: 32.0f,
            damping: 0.04f,
            impactResponse: 1.0f,
            surfaceTension: 0.35f);
        LiquidSurfaceSimulation simulation = CreateFilled(24, 24, 1, in coefficients);
        Assert(simulation.QueueImpulse(12.5, 12.5, 100.0f), "central impulse was rejected");
        // Forcing is SI: 50 mm/h is a deliberately severe but physically expressible rain rate.
        LiquidSurfaceForcing forcing = new(3.0f, 1.0f, 0.050f / 3_600.0f);
        for (int frame = 0; frame < 1_200; frame++)
        {
            simulation.Advance(1.0f / 60.0f, in forcing);
        }

        // The solver's small-wave limiter is spatial: four authored amplitudes or ten percent
        // of the dominant wavelength, whichever can contain the resolved disturbance.
        float maximum = Math.Max(
            0.001f,
            Math.Max(coefficients.WaveAmplitude * 4.0f, coefficients.WaveLength * 0.10f));
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                LiquidSurfaceCellSample sample = simulation.GetCellSample(x, z);
                Assert(float.IsFinite(sample.Height), $"non-finite height at {x},{z}");
                Assert(float.IsFinite(sample.Velocity), $"non-finite velocity at {x},{z}");
                Assert(Math.Abs(sample.Height) <= maximum + 1e-5f, $"height bound exceeded at {x},{z}");
            }
        }
    }

    /// <summary>
    /// Verifies the propagation regression contract against deterministic fixture data.
    /// </summary>
    private static void TestPropagation()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(
            waveAmplitude: 0.15f,
            waveLength: 3.0f,
            waveSpeed: 4.0f,
            damping: 0.03f,
            impactResponse: 1.0f,
            surfaceTension: 0.1f);
        LiquidSurfaceSimulation simulation = CreateFilled(15, 15, 1, in coefficients);
        simulation.QueueImpulse(7.5, 7.5, 4.0f);
        AdvanceSeconds(simulation, 0.35f);

        float neighborEnergy = Math.Abs(simulation.GetCellSample(8, 7).Height)
            + Math.Abs(simulation.GetCellSample(8, 7).Velocity);
        float remoteEnergy = Math.Abs(simulation.GetCellSample(0, 0).Height)
            + Math.Abs(simulation.GetCellSample(0, 0).Velocity);
        Assert(neighborEnergy > 1e-4f, "impulse did not propagate to a connected neighbour");
        Assert(remoteEnergy < neighborEnergy, "wave arrived remotely without local propagation gradient");
    }

    /// <summary>
    /// Verifies the viscous Damping regression contract against deterministic fixture data.
    /// </summary>
    private static void TestViscousDamping()
    {
        LiquidSurfaceDynamics waterLike = Dynamics(damping: 0.03f);
        LiquidSurfaceDynamics honeyLike = Dynamics(damping: 0.95f);
        LiquidSurfaceDynamics lavaLike = Dynamics(damping: 0.72f);
        float waterEnergy = SimulateDecayingImpulse(in waterLike);
        float honeyEnergy = SimulateDecayingImpulse(in honeyLike);
        float lavaEnergy = SimulateDecayingImpulse(in lavaLike);
        Assert(honeyEnergy < waterEnergy * 0.35f,
            $"honey profile did not damp enough ({honeyEnergy} vs {waterEnergy})");
        Assert(lavaEnergy < waterEnergy * 0.55f,
            $"lava profile did not damp enough ({lavaEnergy} vs {waterEnergy})");
    }

    /// <summary>
    /// Verifies the profile Confinement regression contract against deterministic fixture data.
    /// </summary>
    private static void TestProfileConfinement()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(damping: 0.05f);
        LiquidSurfaceSimulation simulation = new(0, 0, 12, 5);
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                if (x == 5)
                {
                    continue;
                }

                byte profile = x < 5 ? (byte)1 : (byte)2;
                simulation.SetSurfaceCell(x, z, 10.0f, profile, in coefficients, rainExposed: true);
            }
        }

        simulation.QueueImpulse(3.5, 2.5, 5.0f);
        AdvanceSeconds(simulation, 1.0f);
        Assert(simulation.GetCellSample(5, 2).Height == 0.0f, "inactive bank received wave state");
        Assert(simulation.GetCellSample(8, 2).Height == 0.0f, "wave leaked into another liquid profile");
        Assert(simulation.GetCellSample(8, 2).Velocity == 0.0f, "velocity leaked into another liquid profile");
    }

    /// <summary>
    /// Verifies the directional Wind regression contract against deterministic fixture data.
    /// </summary>
    private static void TestDirectionalWind()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(
            windCoupling: 1.2f,
            waveAmplitude: 0.08f,
            waveLength: 6.0f,
            waveSpeed: 2.5f,
            damping: 0.08f);
        LiquidSurfaceSimulation simulation = CreateFilled(20, 20, 1, in coefficients);
        LiquidSurfaceForcing forcing = new(3.0f, 0.0f, 0.0f);
        AdvanceSeconds(simulation, 1.25f, in forcing);

        float xNormalEnergy = 0.0f;
        float zNormalEnergy = 0.0f;
        for (int z = 2; z < simulation.Depth - 2; z++)
        {
            for (int x = 2; x < simulation.Width - 2; x++)
            {
                LiquidSurfaceCellSample sample = simulation.GetCellSample(x, z);
                xNormalEnergy += Math.Abs(sample.NormalX);
                zNormalEnergy += Math.Abs(sample.NormalZ);
            }
        }

        Assert(xNormalEnergy > 0.01f, "wind did not form micro-wave normals");
        Assert(xNormalEnergy > zNormalEnergy * 2.5f,
            $"dominant wind direction was lost ({xNormalEnergy} vs {zNormalEnergy})");
        Assert(zNormalEnergy > xNormalEnergy * 0.05f,
            $"wind spectrum collapsed into perfectly parallel crests ({xNormalEnergy} vs {zNormalEnergy})");
    }

    /// <summary>
    /// Verifies the generic Object Entry regression contract against deterministic fixture data.
    /// </summary>
    private static void TestGenericObjectEntry()
    {
        LiquidSurfaceDynamics coefficients = Dynamics();
        LiquidSurfaceSimulation simulation = CreateFilled(8, 8, 1, in coefficients);
        LiquidEntitySurfaceSample above = new(
            41,
            LiquidEntitySurfaceClass.Generic,
            4.5,
            11.0,
            4.5,
            0.2f,
            -1.5f,
            0.1f,
            FeetInLiquid: false,
            Swimming: false);
        LiquidEntitySurfaceSample entered = above with { WorldY = 10.0, FeetInLiquid = true };
        simulation.ObserveEntities([above]);
        simulation.ObserveEntities([entered]);
        Assert(simulation.PendingImpulseCount == 1, "generic liquid-entry transition was not detected");
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Assert(simulation.ComputeTotalEnergy() > 0.0f, "generic entry did not disturb the surface");
    }

    /// <summary>
    /// Verifies that every dropped stack receives the common stone-reference minimum while faster
    /// observed motion still injects greater kinetic energy into the shared surface field.
    /// </summary>
    private static void TestDroppedItemEntry()
    {
        LiquidSurfaceDynamics coefficients = Dynamics();
        LiquidSurfaceSimulation minimum = CreateFilled(8, 8, 1, in coefficients);
        LiquidSurfaceSimulation fast = CreateFilled(8, 8, 1, in coefficients);
        LiquidEntitySurfaceSample above = new(
            42,
            LiquidEntitySurfaceClass.DroppedItem,
            4.5,
            11.0,
            4.5,
            0.0f,
            0.0f,
            0.0f,
            FeetInLiquid: false,
            Swimming: false,
            MassKilograms: LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            MotionSamplePeriodSeconds: 1.0f);
        LiquidEntitySurfaceSample minimumEntry = above with
        {
            WorldY = 10.0,
            FeetInLiquid = true
        };
        LiquidEntitySurfaceSample fastAbove = above with { MotionY = -3.0f };
        // Vintage Story may damp EntityItem motion on the first liquid sample.
        // The physically relevant incident velocity is the last airborne one.
        LiquidEntitySurfaceSample fastEntry = minimumEntry with { MotionY = 0.0f };
        minimum.ObserveEntities([above]);
        minimum.ObserveEntities([minimumEntry]);
        fast.ObserveEntities([fastAbove]);
        fast.ObserveEntities([fastEntry]);
        minimum.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        fast.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        float minimumEnergy = minimum.ComputeTotalEnergy();
        float fastEnergy = fast.ComputeTotalEnergy();
        Assert(minimumEnergy > 0.0f, "slow dropped item did not receive its minimum ripple");
        Assert(fastEnergy > minimumEnergy * 3.5f, "dropped-item velocity did not control ripple energy");
    }

    /// <summary>
    /// Verifies the rainfall regression contract against deterministic fixture data.
    /// </summary>
    private static void TestRainfall()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(impactResponse: 1.0f);
        LiquidSurfaceSimulation first = CreateRainPartition(in coefficients, 0x12345678u);
        LiquidSurfaceSimulation second = CreateRainPartition(in coefficients, 0x12345678u);
        LiquidSurfaceForcing rain = new(0.0f, 0.0f, 0.050f / 3_600.0f);
        AdvanceSeconds(first, 1.0f, in rain);
        AdvanceSeconds(second, 1.0f, in rain);

        float exposedEnergy = 0.0f;
        float shelteredEnergy = 0.0f;
        for (int z = 0; z < first.Depth; z++)
        {
            for (int x = 0; x < first.Width; x++)
            {
                LiquidSurfaceCellSample a = first.GetCellSample(x, z);
                LiquidSurfaceCellSample b = second.GetCellSample(x, z);
                Assert(a == b, $"rain simulation is not deterministic at {x},{z}");
                float energy = a.Height * a.Height + a.Velocity * a.Velocity;
                if (x < 4)
                {
                    exposedEnergy += energy;
                }
                else if (x > 4)
                {
                    shelteredEnergy += energy;
                }
            }
        }

        Assert(exposedEnergy > 0.0f, "rain did not disturb exposed cells");
        Assert(shelteredEnergy == 0.0f, "rain disturbed sheltered cells");
    }

    /// <summary>
    /// Verifies the bobber regression contract against deterministic fixture data.
    /// </summary>
    private static void TestBobber()
    {
        Assert(
            LiquidSurfaceEntityClassifier.Classify("game", "bobber", isCreature: false)
                == LiquidEntitySurfaceClass.Bobber,
            "game:bobber classification failed");
        AssertWake(LiquidEntitySurfaceClass.Bobber, LiquidSurfaceImpulseKind.Bobber);
    }

    /// <summary>
    /// Verifies the fish regression contract against deterministic fixture data.
    /// </summary>
    private static void TestFish()
    {
        Assert(
            LiquidSurfaceEntityClassifier.Classify("game", "fish-perch", isCreature: true)
                == LiquidEntitySurfaceClass.Fish,
            "fish classification failed");
        AssertWake(LiquidEntitySurfaceClass.Fish, LiquidSurfaceImpulseKind.NearSurfaceFish);
    }

    /// <summary>
    /// Verifies the ricochet regression contract against deterministic fixture data.
    /// </summary>
    private static void TestRicochet()
    {
        Assert(
            LiquidSurfaceEntityClassifier.Classify("game", "thrownstone-granite", isCreature: false)
                == LiquidEntitySurfaceClass.ThrownStone,
            "thrownstone classification failed");
        Assert(
            LiquidSurfaceWorldInputs.IsSkippableStoneProjectile(
                new Vintagestory.API.Common.AssetLocation("game", "stone-granite")),
            "real game:thrownitem projectile stack was not recognized as a skippable stone");
        LiquidSurfaceDynamics coefficients = Dynamics();
        LiquidSurfaceSimulation simulation = CreateFilled(
            8,
            8,
            1,
            in coefficients,
            cellSize: LiquidSurfaceSimulation.RecommendedCellSize);
        LiquidProjectileCollisionSample collision = new(
            77,
            LiquidEntitySurfaceClass.ThrownStone,
            3.6,
            9.9,
            3.2,
            3.2,
            10.1,
            3.2,
            0.2f,
            -0.1f,
            0.0f,
            0.2f,
            0.05f,
            0.0f,
            0.35f,
            1.0f / 60.0f,
            IsServerAuthoritative: true);
        simulation.ObserveProjectileLiquidCollisions([collision]);
        Assert(simulation.PendingImpulseCount == 1, "exact thrown-item liquid callback was not detected");
        Assert(simulation.TotalProjectileImpactCount == 1, "ricochet diagnostic was not counted");
        LiquidProjectileImpactDiagnostic diagnostic = simulation.LastProjectileImpactDiagnostic;
        Assert(diagnostic.SupportRadiusWorldBlocks == 0.5f, "projectile entry retained wavelength-wide support");
        Assert(
            diagnostic.ResolvedWaveEnergyJoules <= diagnostic.SurfaceCoupledEnergyJoules,
            "resolved ricochet wave exceeded its measured surface-energy ceiling");
        Assert(
            Math.Abs(
                diagnostic.SurfaceCoupledEnergyJoules
                - diagnostic.ResolvedWaveEnergyJoules
                - diagnostic.SubgridWaveEnergyJoules) < 1.0e-5f,
            "ricochet surface-energy ledger is not conservative");
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Assert(simulation.ComputeTotalEnergy() > 0.0f, "ricochet did not produce a directional ripple");
        LiquidSurfaceSubgridImpactDiagnostic packet = simulation.LastSubgridImpactDiagnostic;
        Assert(packet.Sequence == 1, "first projectile packet did not use the global subgrid sequence");
        Assert(packet.EntityId == collision.EntityId, "subgrid packet lost exact projectile identity");
        Assert(
            packet.SurfaceClass == LiquidEntitySurfaceClass.ThrownStone,
            "subgrid packet lost thrown-stone class semantics");
        Assert(packet.PeakDisplacement > 0.0f, "stone subgrid energy did not produce packet geometry");
        Assert(
            Math.Abs(
                packet.SurfaceCoupledEnergyJoules
                - packet.ResolvedWaveEnergyJoules
                - packet.SubgridWaveEnergyJoules) < 1.0e-5f,
            "rendered stone packet broke the conservative energy ledger");
        Assert(
            Math.Abs(
                packet.SubgridWaveEnergyJoules
                - packet.RenderedPacketEnergyJoules
                - packet.LocalSplashEnergyJoules
                - packet.WakeEnergyJoules) < 1.0e-5f,
            "stone packet rendered more energy than its sub-grid ledger owns");
        Assert(
            packet.DominantWavelengthMetres < 4.0f * simulation.CellSize,
            "stone packet escaped the conservative four-cell subgrid band");
        AdvanceSeconds(simulation, LiquidSurfaceSimulation.FixedStepSeconds * 8.0f);
        simulation.ObserveProjectileLiquidCollisions([
            collision with
            {
                IncidentMotionY = collision.IncidentMotionY * 1.559f,
                OutgoingMotionY = collision.OutgoingMotionY * 1.559f,
                IsServerAuthoritative = false
            }
        ]);
        Assert(
            simulation.TotalProjectileImpactCount == 1,
            "a delayed client reconstruction duplicated an authoritative server ricochet");

        LiquidProjectileCollisionSample secondServerRicochet = collision with
        {
            WorldX = 1.2,
            WorldZ = 1.0,
            PreviousWorldX = 0.8,
            PreviousWorldZ = 1.0
        };
        simulation.ObserveProjectileLiquidCollisions([secondServerRicochet]);
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Span<LiquidSurfaceSubgridImpactDiagnostic> recentPackets =
            stackalloc LiquidSurfaceSubgridImpactDiagnostic[4];
        int recentPacketCount = simulation.WriteSubgridImpactsAfter(0, recentPackets);
        Assert(recentPacketCount == 2, "rapid authoritative ricochets did not remain simultaneous");
        Assert(
            recentPackets[0].Sequence == 1 && recentPackets[1].Sequence == 2,
            "ricochet packet history lost global ordering");
        Assert(
            Math.Abs(recentPackets[0].WorldX - recentPackets[1].WorldX) > 1.0,
            "distinct ricochet contacts collapsed onto one packet origin");
    }

    /// <summary>
    /// Verifies the bubble Burst regression contract against deterministic fixture data.
    /// </summary>
    private static void TestBubbleBurst()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(
            waveAmplitude: 0.08f,
            damping: 0.45f,
            bubbleRate: 64.0f,
            bubbleRadiusMinimum: 0.12f,
            bubbleRadiusMaximum: 0.12f,
            bubbleRiseDuration: 0.04f,
            bubbleBurstStrength: 3.0f,
            bubbleEmissionBoost: 4.0f);
        LiquidSurfaceSimulation simulation = CreateFilled(2, 2, 3, in coefficients);
        for (int step = 0; step < 60 && simulation.ActiveBubbleCount == 0; step++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        }

        Assert(simulation.ActiveBubbleCount > 0, "authored bubble rate did not spawn a bounded bubble");
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        float swelling = 0.0f;
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                swelling = Math.Max(swelling, simulation.GetCellSample(x, z).Height);
            }
        }

        Assert(swelling > 0.0f, "bubble did not swell at the surface before bursting");
        bool sawSubcellEmissionBurst = false;
        Span<LiquidSurfaceBubbleSample> bubblePrimitives = stackalloc LiquidSurfaceBubbleSample[128];
        for (int step = 0; step < 120 && !sawSubcellEmissionBurst; step++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
            int primitiveCount = simulation.WriteActiveBubbles(bubblePrimitives);
            for (int primitive = 0; primitive < primitiveCount; primitive++)
            {
                LiquidSurfaceBubbleSample bubble = bubblePrimitives[primitive];
                if (bubble.Emission <= 0.0f)
                {
                    continue;
                }

                Assert(
                    bubble.EvaluateEmission(bubble.LocalX, bubble.LocalZ) > 0.0f,
                    "bubble burst emission is missing at its local center");
                Assert(
                    bubble.EvaluateEmission(bubble.LocalX + bubble.Radius * 1.01f, bubble.LocalZ) == 0.0f,
                    "bubble burst emission leaked outside its authored radius");
                sawSubcellEmissionBurst = true;
                break;
            }
        }

        Assert(sawSubcellEmissionBurst, "bubble burst did not expose a brief sub-cell emission primitive");
        AdvanceSeconds(simulation, 0.5f);
        float emission = 0.0f;
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                emission = Math.Max(emission, simulation.GetCellSample(x, z).TransientEmission);
            }
        }

        Assert(simulation.ComputeTotalEnergy() > 0.0f, "bubble burst did not disturb the surface");
        Assert(emission > 0.0f, "bubble emission boost did not reach the GPU output state");
    }

    /// <summary>
    /// Verifies the event Budget regression contract against deterministic fixture data.
    /// </summary>
    private static void TestEventBudget()
    {
        LiquidSurfaceDynamics coefficients = Dynamics();
        LiquidSurfaceSimulation simulation = new(0, 0, 2, 2, eventBudget: 2);
        simulation.SetSurfaceCell(0, 0, 10.0f, 1, in coefficients, rainExposed: true);
        Assert(simulation.QueueImpulse(0.5, 0.5, 1.0f), "first event rejected");
        Assert(simulation.QueueImpulse(0.5, 0.5, 1.0f), "second event rejected");
        Assert(!simulation.QueueImpulse(0.5, 0.5, 1.0f), "event budget was not enforced");
        Assert(simulation.DroppedEventCount == 1, "dropped event diagnostic is incorrect");
    }

    /// <summary>
    /// Verifies the bubble Density And Locality regression contract against deterministic fixture data.
    /// </summary>
    private static void TestBubbleDensityAndLocality()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(
            damping: 0.65f,
            bubbleRate: 0.03f,
            bubbleRadiusMinimum: 0.05f,
            bubbleRadiusMaximum: 0.22f,
            bubbleRiseDuration: 0.15f,
            bubbleBurstStrength: 1.0f,
            bubbleEmissionBoost: 1.5f);
        LiquidSurfaceSimulation blockResolution = CreateFilled(
            10,
            10,
            3,
            in coefficients,
            seed: 0xBADDCAFEu,
            cellSize: 1.0f,
            bubbleBudget: 64);
        LiquidSurfaceSimulation halfBlockResolution = CreateFilled(
            20,
            20,
            3,
            in coefficients,
            seed: 0xBADDCAFEu,
            cellSize: LiquidSurfaceSimulation.RecommendedCellSize,
            bubbleBudget: 64);
        AdvanceSeconds(blockResolution, 30.0f);
        AdvanceSeconds(halfBlockResolution, 30.0f);
        Assert(
            blockResolution.TotalBubbleSpawnCount == halfBlockResolution.TotalBubbleSpawnCount,
            "bubble density changed with surface texture resolution");
        float spawnRate = blockResolution.TotalBubbleSpawnCount / 30.0f;
        Assert(spawnRate is >= 2.0f and <= 4.0f,
            $"0.03/s/block² did not produce a subtle ~3/s on 100 blocks² ({spawnRate}/s)");

        LiquidSurfaceDynamics localCoefficients = coefficients with
        {
            BubbleRate = 1.0f,
            BubbleRiseDuration = 1.0f
        };
        LiquidSurfaceSimulation local = CreateFilled(
            8,
            8,
            3,
            in localCoefficients,
            seed: 0x1234ABCDu,
            cellSize: LiquidSurfaceSimulation.RecommendedCellSize,
            bubbleBudget: 8);
        for (int step = 0; step < 240 && local.ActiveBubbleCount == 0; step++)
        {
            local.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        }

        Assert(local.ActiveBubbleCount > 0, "locality fixture did not spawn a bubble");
        local.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Span<LiquidSurfaceBubbleSample> primitives = stackalloc LiquidSurfaceBubbleSample[8];
        int primitiveCount = local.WriteActiveBubbles(primitives);
        Assert(primitiveCount > 0, "active sub-cell bubble buffer is empty");
        LiquidSurfaceBubbleSample bubble = primitives[0];
        Assert(bubble.Radius is >= 0.05f and <= 0.22f, "asset-authored radius was not preserved");
        float centerHeight = bubble.EvaluateHeight(bubble.LocalX, bubble.LocalZ);
        float halfRadiusHeight = bubble.EvaluateHeight(
            bubble.LocalX + bubble.Radius * 0.5f,
            bubble.LocalZ);
        float outsideHeight = bubble.EvaluateHeight(
            bubble.LocalX + bubble.Radius * 1.01f,
            bubble.LocalZ);
        float orthogonalHeight = bubble.EvaluateHeight(
            bubble.LocalX,
            bubble.LocalZ + bubble.Radius * 0.5f);
        Assert(centerHeight > halfRadiusHeight && halfRadiusHeight > 0.0f,
            "sub-cell bubble is not a smooth radial dome");
        Assert(Math.Abs(halfRadiusHeight - orthogonalHeight) < 1e-6f,
            "sub-cell bubble shape is not circular");
        Assert(outsideHeight == 0.0f, "sub-cell bubble leaked beyond its authored radius");

        LiquidSurfaceDynamics saturated = localCoefficients with
        {
            BubbleRate = 64.0f,
            BubbleRiseDuration = 10.0f
        };
        LiquidSurfaceSimulation bounded = new(
            0,
            0,
            10,
            10,
            eventBudget: 16,
            bubbleBudget: 6,
            deterministicSeed: 0xDEADBEEFu);
        for (int z = 0; z < bounded.Depth; z++)
        {
            for (int x = 0; x < bounded.Width; x++)
            {
                byte profileId = checked((byte)(1 + x / 2));
                bounded.SetSurfaceCell(x, z, 10.0f, profileId, in saturated, rainExposed: true);
            }
        }

        bounded.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Assert(bounded.TotalBubbleSpawnCount <= LiquidSurfaceSimulation.MaximumBubbleSpawnsPerStep,
            "global per-step bubble spawn budget was exceeded");
        AdvanceSeconds(bounded, 1.0f);
        Assert(bounded.ActiveBubbleCount <= bounded.BubbleBudget,
            "global active bubble pool was exceeded");
    }

    /// <summary>
    /// Verifies the gpu Output regression contract against deterministic fixture data.
    /// </summary>
    private static void TestGpuOutput()
    {
        LiquidSurfaceDynamics coefficients = Dynamics();
        LiquidSurfaceSimulation simulation = CreateFilled(4, 3, 1, in coefficients);
        simulation.QueueImpulse(1.5, 1.5, 2.0f);
        AdvanceSeconds(simulation, 0.2f);
        float[] output = new float[4 * 3 * LiquidSurfaceSimulation.GpuChannels];
        simulation.WriteGpuTexture(output);
        Assert(output.Length == 48, "GPU output is not one RGBA texel per surface cell");
        Assert(output.Any(static value => Math.Abs(value) > 0.0f), "GPU output remained empty after impact");
        Assert(output.All(float.IsFinite), "GPU output contains a non-finite value");
    }

    /// <summary>
    /// Verifies the edge Contracts regression contract against deterministic fixture data.
    /// </summary>
    private static void TestEdgeContracts()
    {
        LiquidSurfaceDynamics coefficients = Dynamics();
        AssertThrows<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 0, 1),
            "zero width was accepted");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 0),
            "zero depth was accepted");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 1, cellSize: float.NaN),
            "non-finite cell size was accepted");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 1, cellSize: 0.0f),
            "zero cell size was accepted");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 1, eventBudget: 0),
            "zero event budget was accepted");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 1, bubbleBudget: 0),
            "zero bubble budget was accepted");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new LiquidSurfaceSimulation(0, 0, 1, 1, trackedEntityBudget: 0),
            "zero entity budget was accepted");

        LiquidSurfaceSimulation simulation = new(
            10,
            20,
            2,
            2,
            eventBudget: 2,
            bubbleBudget: 1,
            deterministicSeed: 0);
        simulation.SetSurfaceCell(0, 0, 12.0f, 1, in coefficients, rainExposed: true);
        Assert(simulation.TryGetSurfaceWorldY(10.25, 20.25, out float surfaceY)
            && surfaceY == 12.0f, "active surface lookup failed");
        Assert(!simulation.TryGetSurfaceWorldY(9.99, 20.25, out surfaceY) && surfaceY == 0.0f,
            "negative X outside the grid was accepted");
        Assert(!simulation.TryGetSurfaceWorldY(10.25, 22.01, out surfaceY) && surfaceY == 0.0f,
            "Z outside the grid was accepted");
        Assert(!simulation.TryGetSurfaceWorldY(11.25, 20.25, out surfaceY) && surfaceY == 0.0f,
            "inactive cell reported a surface");

        Assert(!simulation.QueueImpulse(10.25, 20.25, float.NaN),
            "non-finite impulse energy was accepted");
        Assert(!simulation.QueueImpulse(10.25, 20.25, 0.0f),
            "zero impulse energy was accepted");
        Assert(!simulation.QueueImpulse(99.0, 20.25, 1.0f),
            "out-of-grid impulse was accepted");
        Assert(!simulation.QueueImpulse(11.25, 20.25, 1.0f),
            "inactive-cell impulse was accepted");

        AssertThrows<ArgumentOutOfRangeException>(
            () => simulation.SetSurfaceCell(1, 0, float.NaN, 1, in coefficients, rainExposed: true),
            "non-finite surface height was accepted");
        simulation.SetSurfaceCell(
            1,
            0,
            12.0f,
            LiquidOpticalRegistry.NoLiquidProfileId,
            in coefficients,
            rainExposed: true);
        simulation.SetSurfaceCell(1, 1, 12.0f, 2, in coefficients, rainExposed: true);
        simulation.SetSurfaceCell(
            1,
            1,
            12.0f,
            LiquidOpticalRegistry.UnknownLiquidProfileId,
            in coefficients,
            rainExposed: true);
        simulation.ClearSurfaceCell(0, 0);
        Assert(simulation.GetCellSample(0, 0) == default, "clear retained transient channels");
        AssertThrows<ArgumentOutOfRangeException>(
            () => simulation.GetCellSample(-1, 0),
            "negative cell X was accepted");
        AssertThrows<ArgumentOutOfRangeException>(
            () => simulation.GetCellSample(0, 2),
            "cell Z beyond depth was accepted");
        AssertThrows<ArgumentException>(
            () => simulation.WriteGpuTexture(new float[3]),
            "undersized GPU destination was accepted");

        Assert(simulation.Advance(float.NaN, default) == 0, "NaN elapsed time advanced the solver");
        Assert(simulation.Advance(-1.0f, default) == 0, "negative elapsed time advanced the solver");
        FieldInfo accumulator = typeof(LiquidSurfaceSimulation).GetField(
            "accumulator",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("accumulator");
        accumulator.SetValue(simulation, LiquidSurfaceSimulation.FixedStepSeconds * 2.0f);
        Assert(
            simulation.Advance(10.0f, default) == 12,
            "large elapsed time did not saturate the fixed-step budget");
        Assert((float)accumulator.GetValue(simulation)! < LiquidSurfaceSimulation.FixedStepSeconds,
            "excess fixed-step debt was not bounded after saturation");

        LiquidSurfaceDynamics invalidDynamics = coefficients with { WaveSpeed = float.NaN };
        simulation.SetSurfaceCell(0, 0, 12.0f, 1, in invalidDynamics, rainExposed: true);
        LiquidSurfaceForcing invalidForcing = new(float.NaN, float.PositiveInfinity, float.NaN);
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in invalidForcing);
        LiquidSurfaceCellSample sanitized = simulation.GetCellSample(0, 0);
        Assert(float.IsFinite(sanitized.Height) && float.IsFinite(sanitized.Velocity),
            "non-finite authored dynamics escaped the solver safety boundary");

        MethodInfo findProfileCell = typeof(LiquidSurfaceSimulation).GetMethod(
            "FindProfileCell",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("FindProfileCell");
        Assert((int)findProfileCell.Invoke(simulation, [42, 0])! == 0,
            "defensive missing-profile fallback changed");

        LiquidSurfaceSimulation bubblePool = new(0, 0, 1, 1, bubbleBudget: 1);
        bubblePool.SetSurfaceCell(0, 0, 10.0f, 1, in coefficients, rainExposed: true);
        MethodInfo spawnBubble = typeof(LiquidSurfaceSimulation).GetMethod(
            "SpawnBubble",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("SpawnBubble");
        spawnBubble.Invoke(bubblePool, [0, coefficients]);
        spawnBubble.Invoke(bubblePool, [0, coefficients]);
        Assert(bubblePool.ActiveBubbleCount == 1 && bubblePool.TotalBubbleSpawnCount == 1,
            "full bubble pool admitted an extra primitive");
    }

    /// <summary>
    /// Verifies the tracker And Wake Fallbacks regression contract against deterministic fixture data.
    /// </summary>
    private static void TestTrackerAndWakeFallbacks()
    {
        LiquidSurfaceDynamics coefficients = Dynamics();
        LiquidSurfaceSimulation simulation = new(
            0,
            0,
            2,
            2,
            eventBudget: 1,
            trackedEntityBudget: 8);
        simulation.SetSurfaceCell(0, 0, 10.0f, 1, in coefficients, rainExposed: true);

        LiquidEntitySurfaceSample outside = EntitySample(101, 8.0, 10.0, 8.0);
        simulation.ObserveEntities([outside]);
        simulation.ObserveEntities([outside with { WorldX = 8.1 }]);
        LiquidEntitySurfaceSample inactive = EntitySample(102, 1.2, 10.0, 0.2);
        simulation.ObserveEntities([inactive]);
        simulation.ObserveEntities([inactive with { WorldX = 1.3 }]);

        LiquidEntitySurfaceSample generic = EntitySample(103, 0.2, 10.0, 0.2);
        simulation.ObserveEntities([generic]);
        simulation.ObserveEntities([generic with { WorldX = 0.21 }]);
        for (int step = 0; step < 8; step++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        }
        LiquidEntitySurfaceSample farFromSurface = EntitySample(106, 0.4, 11.0, 0.4);
        simulation.ObserveEntities([farFromSurface]);
        simulation.ObserveEntities([farFromSurface with { WorldX = 0.5, MotionX = 0.4f }]);
        simulation.ObserveEntities([generic with { WorldX = 0.3, MotionX = 0.4f }]);
        Assert(simulation.PendingImpulseCount == 0,
            "generic near-surface motion unexpectedly selected a wake kind");

        LiquidEntitySurfaceSample crossing = EntitySample(104, 0.3, 10.5, 0.3);
        simulation.ObserveEntities([crossing]);
        simulation.ObserveEntities([crossing with { WorldY = 10.0, MotionY = -0.2f }]);
        Assert(simulation.PendingImpulseCount == 1,
            "surface crossing without FeetInLiquid did not queue a generic entry");
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        LiquidEntitySurfaceSample bobberCrossing = EntitySample(
            107,
            0.6,
            10.5,
            0.6,
            LiquidEntitySurfaceClass.Bobber);
        simulation.ObserveEntities([bobberCrossing]);
        simulation.ObserveEntities([bobberCrossing with { WorldY = 10.0, MotionY = -0.1f }]);
        Assert(simulation.PendingImpulseCount == 1,
            "bobber surface crossing did not select the bobber entry impulse");
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        LiquidEntitySurfaceSample swimmer = EntitySample(
            105,
            0.4,
            10.0,
            0.4,
            LiquidEntitySurfaceClass.Generic,
            swimming: true,
            motionX: 0.5f);
        simulation.ObserveEntities([swimmer]);
        for (int step = 0; step < 8; step++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        }
        Assert(simulation.QueueImpulse(0.5, 0.5, 1.0f), "wake overflow fixture did not fill its queue");
        simulation.ObserveEntities([swimmer with { WorldX = 0.5 }]);
        Assert(simulation.DroppedEventCount > 0,
            "failed swimming wake did not exercise bounded queue overflow");

        LiquidSurfaceSimulation trackers = new(0, 0, 1, 1, trackedEntityBudget: 8);
        trackers.SetSurfaceCell(0, 0, 10.0f, 1, in coefficients, rainExposed: false);
        for (int index = 0; index < 8; index++)
        {
            trackers.ObserveEntities([EntitySample(index * 8L, 0.2, 10.0, 0.2)]);
        }
        trackers.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        trackers.ObserveEntities([EntitySample(0, 0.2, 10.0, 0.2)]);
        trackers.ObserveEntities([EntitySample(64, 0.2, 10.0, 0.2)]);
        Assert(trackers.PendingImpulseCount == 0,
            "tracker replacement fabricated a surface transition");
    }

    /// <summary>
    /// Executes the entity Sample step used by the deterministic program fixture.
    /// </summary>
    /// <param name="id">The id input used to configure this deterministic test path.</param>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="surfaceClass">The surface Class input used to configure this deterministic test path.</param>
    /// <param name="swimming">The swimming input used to configure this deterministic test path.</param>
    /// <param name="motionX">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The entity Sample result consumed by the caller&apos;s assertion.</returns>
    private static LiquidEntitySurfaceSample EntitySample(
        long id,
        double x,
        double y,
        double z,
        LiquidEntitySurfaceClass surfaceClass = LiquidEntitySurfaceClass.Generic,
        bool swimming = false,
        float motionX = 0.0f)
    {
        return new LiquidEntitySurfaceSample(
            id,
            surfaceClass,
            x,
            y,
            z,
            motionX,
            0.0f,
            0.0f,
            FeetInLiquid: false,
            Swimming: swimming);
    }

    /// <summary>
    /// Verifies the steady State Allocations regression contract against deterministic fixture data.
    /// </summary>
    private static void TestSteadyStateAllocations()
    {
        LiquidSurfaceDynamics coefficients = Dynamics(
            windCoupling: 0.5f,
            bubbleRate: 0.03f,
            bubbleRadiusMinimum: 0.05f,
            bubbleRadiusMaximum: 0.22f,
            bubbleRiseDuration: 0.25f,
            bubbleBurstStrength: 0.7f,
            bubbleEmissionBoost: 1.5f);
        LiquidSurfaceSimulation simulation = CreateFilled(
            12,
            12,
            1,
            in coefficients,
            bubbleBudget: 16);
        float[] output = new float[12 * 12 * LiquidSurfaceSimulation.GpuChannels];
        LiquidSurfaceBubbleSample[] bubbleOutput = new LiquidSurfaceBubbleSample[16];
        LiquidSurfaceForcing forcing = new(1.0f, 0.5f, 0.0f);
        for (int warmup = 0; warmup < 32; warmup++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in forcing);
            simulation.WriteGpuTexture(output);
            simulation.WriteActiveBubbles(bubbleOutput);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 128; iteration++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in forcing);
            simulation.WriteGpuTexture(output);
            simulation.WriteActiveBubbles(bubbleOutput);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert(after == before, $"steady-state loop allocated {after - before} bytes");
    }

    /// <summary>
    /// Asserts wake and throws when the regression contract is violated.
    /// </summary>
    /// <param name="surfaceClass">The surface Class input used to configure this deterministic test path.</param>
    /// <param name="expectedKind">Expected value enforced by the regression contract.</param>
    private static void AssertWake(
        LiquidEntitySurfaceClass surfaceClass,
        LiquidSurfaceImpulseKind expectedKind)
    {
        LiquidSurfaceDynamics coefficients = Dynamics();
        LiquidSurfaceSimulation simulation = CreateFilled(8, 8, 1, in coefficients);
        LiquidEntitySurfaceSample initial = new(
            59,
            surfaceClass,
            3.2,
            10.0,
            3.2,
            0.4f,
            0.0f,
            0.2f,
            FeetInLiquid: surfaceClass != LiquidEntitySurfaceClass.Bobber,
            Swimming: false);
        simulation.ObserveEntities([initial]);
        AdvanceSeconds(simulation, LiquidSurfaceSimulation.FixedStepSeconds * 8.0f);
        LiquidEntitySurfaceSample moved = initial with { WorldX = 3.3, WorldZ = 3.25 };
        simulation.ObserveEntities([moved]);
        Assert(simulation.PendingImpulseCount == 1, $"{expectedKind} wake was not detected");
    }

    /// <summary>
    /// Executes the simulate Decaying Impulse step used by the deterministic program fixture.
    /// </summary>
    /// <param name="coefficients">The coefficients input used to configure this deterministic test path.</param>
    /// <returns>The simulate Decaying Impulse result consumed by the caller&apos;s assertion.</returns>
    private static float SimulateDecayingImpulse(in LiquidSurfaceDynamics coefficients)
    {
        LiquidSurfaceSimulation simulation = CreateFilled(15, 15, 1, in coefficients);
        simulation.QueueImpulse(7.5, 7.5, 4.0f);
        AdvanceSeconds(simulation, 2.0f);
        return simulation.ComputeTotalEnergy();
    }

    /// <summary>
    /// Creates filled with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="width">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="depth">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="profileId">The profile Id input used to configure this deterministic test path.</param>
    /// <param name="coefficients">The coefficients input used to configure this deterministic test path.</param>
    /// <param name="seed">The seed input used to configure this deterministic test path.</param>
    /// <param name="cellSize">The cell Size input used to configure this deterministic test path.</param>
    /// <param name="bubbleBudget">The bubble Budget input used to configure this deterministic test path.</param>
    /// <returns>The create Filled result consumed by the caller&apos;s assertion.</returns>
    private static LiquidSurfaceSimulation CreateFilled(
        int width,
        int depth,
        byte profileId,
        in LiquidSurfaceDynamics coefficients,
        uint seed = 0x9E3779B9u,
        float cellSize = 1.0f,
        int bubbleBudget = 128)
    {
        LiquidSurfaceSimulation simulation = new(
            0,
            0,
            width,
            depth,
            cellSize: cellSize,
            bubbleBudget: bubbleBudget,
            deterministicSeed: seed);
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                simulation.SetSurfaceCell(
                    x,
                    z,
                    10.0f,
                    profileId,
                    in coefficients,
                    rainExposed: true);
            }
        }

        return simulation;
    }

    /// <summary>
    /// Creates rain Partition with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="coefficients">The coefficients input used to configure this deterministic test path.</param>
    /// <param name="seed">The seed input used to configure this deterministic test path.</param>
    /// <returns>The create Rain Partition result consumed by the caller&apos;s assertion.</returns>
    private static LiquidSurfaceSimulation CreateRainPartition(
        in LiquidSurfaceDynamics coefficients,
        uint seed)
    {
        LiquidSurfaceSimulation simulation = new(0, 0, 9, 8, deterministicSeed: seed);
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                if (x == 4)
                {
                    continue;
                }

                bool exposed = x < 4;
                simulation.SetSurfaceCell(
                    x,
                    z,
                    10.0f,
                    exposed ? (byte)1 : (byte)2,
                    in coefficients,
                    rainExposed: exposed);
            }
        }

        return simulation;
    }

    /// <summary>
    /// Executes the advance Seconds step used by the deterministic program fixture.
    /// </summary>
    /// <param name="simulation">The simulation input used to configure this deterministic test path.</param>
    /// <param name="seconds">Duration supplied to the simulation, in seconds unless the tested API states otherwise.</param>
    private static void AdvanceSeconds(
        LiquidSurfaceSimulation simulation,
        float seconds)
    {
        LiquidSurfaceForcing forcing = default;
        AdvanceSeconds(simulation, seconds, in forcing);
    }

    /// <summary>
    /// Executes the advance Seconds step used by the deterministic program fixture.
    /// </summary>
    /// <param name="simulation">The simulation input used to configure this deterministic test path.</param>
    /// <param name="seconds">Duration supplied to the simulation, in seconds unless the tested API states otherwise.</param>
    /// <param name="forcing">The forcing input used to configure this deterministic test path.</param>
    private static void AdvanceSeconds(
        LiquidSurfaceSimulation simulation,
        float seconds,
        in LiquidSurfaceForcing forcing)
    {
        int steps = (int)MathF.Ceiling(seconds / LiquidSurfaceSimulation.FixedStepSeconds);
        for (int step = 0; step < steps; step++)
        {
            simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in forcing);
        }
    }

    /// <summary>
    /// Executes the dynamics step used by the deterministic program fixture.
    /// </summary>
    /// <param name="windCoupling">The wind Coupling input used to configure this deterministic test path.</param>
    /// <param name="waveAmplitude">The wave Amplitude input used to configure this deterministic test path.</param>
    /// <param name="waveLength">The wave Length input used to configure this deterministic test path.</param>
    /// <param name="waveSpeed">The wave Speed input used to configure this deterministic test path.</param>
    /// <param name="damping">The damping input used to configure this deterministic test path.</param>
    /// <param name="impactResponse">The impact Response input used to configure this deterministic test path.</param>
    /// <param name="surfaceTension">The surface Tension input used to configure this deterministic test path.</param>
    /// <param name="bubbleRate">The bubble Rate input used to configure this deterministic test path.</param>
    /// <param name="bubbleRadiusMinimum">The bubble Radius Minimum input used to configure this deterministic test path.</param>
    /// <param name="bubbleRadiusMaximum">The bubble Radius Maximum input used to configure this deterministic test path.</param>
    /// <param name="bubbleRiseDuration">Duration supplied to the simulation, in seconds unless the tested API states otherwise.</param>
    /// <param name="bubbleBurstStrength">The bubble Burst Strength input used to configure this deterministic test path.</param>
    /// <param name="bubbleEmissionBoost">The bubble Emission Boost input used to configure this deterministic test path.</param>
    /// <returns>The dynamics result consumed by the caller&apos;s assertion.</returns>
    private static LiquidSurfaceDynamics Dynamics(
        float windCoupling = 0.0f,
        float waveAmplitude = 0.10f,
        float waveLength = 3.0f,
        float waveSpeed = 3.0f,
        float damping = 0.08f,
        float impactResponse = 1.2f,
        float surfaceTension = 0.12f,
        float bubbleRate = 0.0f,
        float bubbleRadiusMinimum = 0.0f,
        float bubbleRadiusMaximum = 0.0f,
        float bubbleRiseDuration = 0.0f,
        float bubbleBurstStrength = 0.0f,
        float bubbleEmissionBoost = 0.0f)
    {
        return new LiquidSurfaceDynamics(
            windCoupling,
            waveAmplitude,
            waveLength,
            waveSpeed,
            damping,
            impactResponse,
            surfaceTension,
            bubbleRate,
            bubbleRadiusMinimum,
            bubbleRadiusMaximum,
            bubbleRiseDuration,
            bubbleBurstStrength,
            bubbleEmissionBoost);
    }

    /// <summary>
    /// Asserts requested fixture operation and throws when the regression contract is violated.
    /// </summary>
    /// <param name="condition">The condition input used to configure this deterministic test path.</param>
    /// <param name="message">The message input used to configure this deterministic test path.</param>
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Asserts throws and throws when the regression contract is violated.
    /// </summary>
    /// <param name="action">Injected operation used to isolate the test from external state.</param>
    /// <param name="message">The message input used to configure this deterministic test path.</param>
    /// <typeparam name="TException">Type participating in the generic test contract.</typeparam>
    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
