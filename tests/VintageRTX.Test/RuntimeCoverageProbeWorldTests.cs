using System.Globalization;
using VintageRTX.Configuration;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Exercises the real world-selection and scene-construction paths against a
/// deterministic block accessor, including the explicit failure diagnostics.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuntimeCoverageProbeWorldTests
{
    /// <summary>
    /// Verifies the render Lab Builds Verified Authored Scene And Rejects Unavailable Chunks regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void RenderLabBuildsVerifiedAuthoredSceneAndRejectsUnavailableChunks()
    {
        RuntimeCoverageProbeHarness success = new()
        {
            // Exercise the production IBlockAccessor fallback once; the two
            // negative cases below use the deterministic direct lookup.
            UseFastRenderLabChunkLookup = false,
            UseFastRenderLabBlockLookup = false
        };
        success.RegisterBlock(
            "game:tallgrass-tall-free",
            13,
            EnumBlockMaterial.Plant,
            collision: true);
        success.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid && y == 79 ? success.Solid : success.Air;
        RuntimeScenarioProbe probe = success.CreateProbe("render-lab");

        Assert.IsTrue((bool)Invoke(probe, "TryBuildRenderLab")!);
        Assert.AreEqual(1, success.BulkCommitCount);
        Assert.IsTrue(success.ChatMessages.Any(static text => text.StartsWith("/tp =", StringComparison.Ordinal)));
        Assert.IsTrue(GetField<bool>(probe, "renderLabLightPositionSet"));
        Assert.AreEqual(
            success.Air.Id,
            success.GetPlacedBlock(6, 105, 10, BlockLayersAccess.Solid).Id,
            "The +X solar aperture must remain open behind the tallgrass witness.");
        Assert.IsTrue(success.Logs.Any(static entry =>
            entry.Message.Contains("Render lab reflection targets verified: count=3", StringComparison.Ordinal)
            && entry.Message.Contains("emissive=none", StringComparison.Ordinal)));
        Assert.IsTrue(success.Logs.Any(static entry =>
            entry.Message.Contains("Render lab solar aperture verified: side=+X", StringComparison.Ordinal)));
        Assert.IsTrue(success.Logs.Any(static entry =>
            entry.Message.Contains("Render lab camera applied: close=true", StringComparison.Ordinal)
            && entry.Message.Contains("water and authored casters in frame=true", StringComparison.Ordinal)));
        StringAssert.Contains(success.ChatMessages.Single(static text =>
            text.StartsWith("/tp =", StringComparison.Ordinal)), "=3.25");
        probe.Dispose();

        RuntimeCoverageProbeHarness unloaded = new();
        unloaded.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid && y == 79 ? unloaded.Solid : unloaded.Air;
        unloaded.ChunkAvailableAt = position => position.X > -9;
        RuntimeScenarioProbe firstChunkMissing = unloaded.CreateProbe("render-lab");
        Assert.IsFalse((bool)Invoke(firstChunkMissing, "TryBuildRenderLab")!);
        firstChunkMissing.Dispose();

        unloaded.ChunkAvailableAt = position => position.X < 9;
        RuntimeScenarioProbe secondChunkMissing = unloaded.CreateProbe("render-lab");
        Assert.IsFalse((bool)Invoke(secondChunkMissing, "TryBuildRenderLab")!);
        secondChunkMissing.Dispose();
    }

    /// <summary>
    /// Verifies the render Lab Requires Every Asset And Detects Placement Mismatch regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void RenderLabRequiresEveryAssetAndDetectsPlacementMismatch()
    {
        RuntimeCoverageProbeHarness assets = new();
        assets.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid && y == 79 ? assets.Solid : assets.Air;
        Assert.AreEqual(1, ((Block)InvokeStatic(
            typeof(RuntimeScenarioProbe),
            "RequireRenderLabBlock",
            assets.BlockAccessor,
            "game:solid")!).Id);

        // Keep the two real IBulkBlockAccessor branches covered once without
        // routing the complete 7,000-write laboratory through DispatchProxy.
        RuntimeCoverageProbeHarness engineAdapter = new()
        {
            UseFastRenderLabWriter = false
        };
        RuntimeScenarioProbe engineProbe = engineAdapter.CreateProbe("exterior-roof");
        BlockPos adapterPosition = new(2, 3, 4);
        Invoke(engineProbe, "StageRenderLabBlock", engineAdapter.BulkAccessor, 1, adapterPosition, null);
        Invoke(
            engineProbe,
            "StageRenderLabBlock",
            engineAdapter.BulkAccessor,
            2,
            adapterPosition,
            (int?)BlockLayersAccess.Fluid);
        Invoke(engineProbe, "CommitRenderLabBlocks", engineAdapter.BulkAccessor);
        Assert.AreEqual(1, engineAdapter.BulkCommitCount);
        engineProbe.Dispose();

        assets.RemoveRegisteredBlock("game:anvil-iron");
        RuntimeScenarioProbe missing = assets.CreateProbe("render-lab");
        InvalidOperationException missingError = Assert.ThrowsException<InvalidOperationException>(
            () => Invoke(missing, "TryBuildRenderLab"));
        StringAssert.Contains(missingError.Message, "game:anvil-iron");
        missing.Dispose();

        RuntimeCoverageProbeHarness mismatch = new();
        mismatch.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid && y == 79 ? mismatch.Solid : mismatch.Air;
        mismatch.SuppressBlockWrites = true;
        RuntimeScenarioProbe broken = mismatch.CreateProbe("render-lab");
        InvalidOperationException placementError = Assert.ThrowsException<InvalidOperationException>(
            () => Invoke(broken, "TryBuildRenderLab"));
        StringAssert.Contains(placementError.Message, "placement verification");
        broken.Dispose();
    }

    /// <summary>
    /// Verifies the exterior Roof Camera Covers Low Sun No Candidate And Open Ground Success regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ExteriorRoofCameraCoversLowSunNoCandidateAndOpenGroundSuccess()
    {
        RuntimeCoverageProbeHarness lowSun = CreateExteriorHarness();
        lowSun.SunDirection = new(0.2f, 0.0f, 0.4f);
        RuntimeScenarioProbe rejectedSun = lowSun.CreateProbe("exterior-roof");
        Assert.IsFalse((bool)Invoke(rejectedSun, "TryApplyExteriorRoofCamera")!);
        rejectedSun.Dispose();

        RuntimeCoverageProbeHarness unavailable = new();
        unavailable.FallbackBlockAt = (_, _, _, _) => unavailable.Air;
        RuntimeScenarioProbe rejectedTerrain = unavailable.CreateProbe("exterior-roof");
        Assert.IsFalse((bool)Invoke(rejectedTerrain, "TryApplyExteriorRoofCamera")!);
        rejectedTerrain.Dispose();

        RuntimeCoverageProbeHarness success = CreateExteriorHarness();
        RuntimeScenarioProbe applied = success.CreateProbe("exterior-roof");
        Assert.IsTrue((bool)Invoke(applied, "TryApplyExteriorRoofCamera")!);
        Assert.IsTrue(success.ChatMessages.Any(static text => text.StartsWith("/tp =", StringComparison.Ordinal)));
        Assert.IsTrue(GetField<bool>(applied, "exteriorPositionLocked"));
        applied.Dispose();

        RuntimeCoverageProbeHarness verticalSun = CreateExteriorHarness();
        verticalSun.SunDirection = new Vintagestory.API.MathTools.Vec3f(0.0f, 1.0f, 0.0f);
        RuntimeScenarioProbe verticalProbe = verticalSun.CreateProbe("exterior-roof");
        Assert.IsTrue((bool)Invoke(verticalProbe, "TryApplyExteriorRoofCamera")!);
        verticalProbe.Dispose();

        RuntimeCoverageProbeHarness noClientCalendar = CreateExteriorHarness();
        noClientCalendar.CalendarAvailable = false;
        RuntimeScenarioProbe fallbackSun = noClientCalendar.CreateProbe("exterior-roof");
        Assert.IsTrue((bool)Invoke(fallbackSun, "TryApplyExteriorRoofCamera")!);
        fallbackSun.Dispose();
    }

    /// <summary>
    /// Verifies the rain Wetness Camera Requires Shelter And Exposed Receiving Ground regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void RainWetnessCameraRequiresShelterAndExposedReceivingGround()
    {
        RuntimeCoverageProbeHarness noShelter = CreateExteriorHarness();
        noShelter.FallbackBlockAt = (_, _, _, _) => noShelter.Air;
        RuntimeScenarioProbe missingShelter = noShelter.CreateProbe("rain-wetness");
        Assert.IsFalse((bool)Invoke(missingShelter, "TryApplyExteriorRoofCamera")!);
        missingShelter.Dispose();

        RuntimeCoverageProbeHarness success = CreateExteriorHarness();
        RuntimeScenarioProbe applied = success.CreateProbe("rain-wetness");
        Assert.IsTrue((bool)Invoke(applied, "TryApplyExteriorRoofCamera")!);
        applied.Dispose();
    }

    /// <summary>
    /// Verifies the cave Camera Finds Dark Covered Volume And Rejects Invalid Candidates regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CaveCameraFindsDarkCoveredVolumeAndRejectsInvalidCandidates()
    {
        RuntimeCoverageProbeHarness empty = new();
        empty.FallbackBlockAt = (_, _, _, _) => empty.Air;
        RuntimeScenarioProbe rejected = empty.CreateProbe("cave-interior");
        Assert.IsFalse((bool)Invoke(rejected, "TryApplyCaveInteriorCamera")!);
        rejected.Dispose();

        RuntimeCoverageProbeHarness cave = CreateCaveHarness();
        RuntimeScenarioProbe applied = cave.CreateProbe("cave-interior");
        Assert.IsTrue((bool)Invoke(applied, "TryApplyCaveInteriorCamera")!);
        Assert.IsTrue(GetField<bool>(applied, "caveLightPositionSet"));
        Assert.IsTrue(cave.ChatMessages.Any(static text => text.StartsWith("/tp =", StringComparison.Ordinal)));
        applied.Dispose();

        RuntimeCoverageProbeHarness bright = CreateCaveHarness();
        bright.LightAt = static (_, kind) => kind == EnumLightLevelType.OnlySunLight ? 3 : 9;
        RuntimeScenarioProbe brightProbe = bright.CreateProbe("cave-interior");
        Assert.IsFalse((bool)Invoke(brightProbe, "TryApplyCaveInteriorCamera")!);
        brightProbe.Dispose();
    }

    /// <summary>
    /// Verifies the water Camera Finds Long Visible Run And Rejects Missing Water Or Bank regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WaterCameraFindsLongVisibleRunAndRejectsMissingWaterOrBank()
    {
        RuntimeCoverageProbeHarness empty = new();
        RuntimeScenarioProbe noWater = empty.CreateProbe("water-reflection");
        Assert.IsFalse((bool)Invoke(noWater, "TryApplyWaterReflectionCamera")!);
        noWater.Dispose();

        RuntimeCoverageProbeHarness water = CreateWaterHarness(hasBank: true);
        RuntimeScenarioProbe applied = water.CreateProbe("water-reflection");
        Assert.IsTrue((bool)Invoke(applied, "TryApplyWaterReflectionCamera")!);
        Assert.IsTrue(water.ChatMessages.Any(static text => text.StartsWith("/tp =", StringComparison.Ordinal)));
        Vec3d[] landingTargets = GetField<Vec3d[]>(applied, "waterImpactLandingPositions");
        Assert.AreEqual(3, landingTargets.Length);
        Assert.IsTrue(Math.Sqrt(
            Math.Pow(landingTargets[0].X - landingTargets[1].X, 2)
            + Math.Pow(landingTargets[0].Z - landingTargets[1].Z, 2)) >= 1.20);
        Assert.IsTrue(Math.Sqrt(
            Math.Pow(landingTargets[1].X - landingTargets[2].X, 2)
            + Math.Pow(landingTargets[1].Z - landingTargets[2].Z, 2)) >= 1.20);
        double[] expectedDropHeights = [0.50, 3.00, 5.00];
        double expectedSurfaceY = landingTargets[0].Y;
        Assert.AreEqual(expectedSurfaceY, landingTargets[1].Y, 0.001);
        Assert.AreEqual(expectedSurfaceY, landingTargets[2].Y, 0.001);
        Vec3d[] projectileTargets = GetField<Vec3d[]>(applied, "waterProjectileLandingPositions");
        Assert.AreEqual(2, projectileTargets.Length);
        Assert.AreEqual(expectedSurfaceY, projectileTargets[0].Y, 0.001);
        Assert.AreEqual(expectedSurfaceY, projectileTargets[1].Y, 0.001);
        Assert.IsTrue(Math.Sqrt(
            Math.Pow(projectileTargets[0].X - landingTargets[1].X, 2)
            + Math.Pow(projectileTargets[0].Z - landingTargets[1].Z, 2)) >= 2.90);
        Assert.AreEqual(landingTargets[1].X, projectileTargets[1].X, 0.001);
        Assert.AreEqual(landingTargets[1].Z, projectileTargets[1].Z, 0.001);
        Assert.IsTrue(Math.Sqrt(
            Math.Pow(projectileTargets[1].X - projectileTargets[0].X, 2)
            + Math.Pow(projectileTargets[1].Z - projectileTargets[0].Z, 2)) >= 2.90);
        string witnessCommand = water.ChatMessages.Single(static text =>
            text.StartsWith("/vintagertxtest witness ", StringComparison.Ordinal));
        string[] witnessArguments = witnessCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(8, witnessArguments.Length);
        double opaqueX = double.Parse(witnessArguments[2], CultureInfo.InvariantCulture);
        double opaqueZ = double.Parse(witnessArguments[4], CultureInfo.InvariantCulture);
        double humanoidX = double.Parse(witnessArguments[5], CultureInfo.InvariantCulture);
        double humanoidZ = double.Parse(witnessArguments[7], CultureInfo.InvariantCulture);
        double runX = landingTargets[2].X - landingTargets[0].X;
        double runZ = landingTargets[2].Z - landingTargets[0].Z;
        double runLength = Math.Sqrt(runX * runX + runZ * runZ);
        runX /= runLength;
        runZ /= runLength;
        double opaqueLateral = -runZ * (opaqueX - landingTargets[0].X)
            + runX * (opaqueZ - landingTargets[0].Z);
        double humanoidLateral = -runZ * (humanoidX - landingTargets[2].X)
            + runX * (humanoidZ - landingTargets[2].Z);
        double opaqueLongitudinal = runX * (opaqueX - landingTargets[0].X)
            + runZ * (opaqueZ - landingTargets[0].Z);
        double humanoidLongitudinal = runX * (humanoidX - landingTargets[2].X)
            + runZ * (humanoidZ - landingTargets[2].Z);
        Assert.AreEqual(0.40, opaqueLateral, 0.02);
        Assert.AreEqual(-0.40, humanoidLateral, 0.02);
        Assert.AreEqual(-0.90, opaqueLongitudinal, 0.02);
        Assert.AreEqual(0.90, humanoidLongitudinal, 0.02);
        Assert.IsTrue(opaqueLateral * humanoidLateral < 0.0);
        Assert.AreEqual(expectedSurfaceY + 1.20, double.Parse(witnessArguments[3], CultureInfo.InvariantCulture), 0.011);
        Assert.AreEqual(expectedSurfaceY + 0.04, double.Parse(witnessArguments[6], CultureInfo.InvariantCulture), 0.011);
        Assert.IsTrue(water.Logs.Any(static entry => entry.Message.Contains(
            "First-person held arm/torch remains an independent exclusion challenge",
            StringComparison.Ordinal)));
        foreach (int triggerTick in new[] { 2_499, 2_749, 2_899 })
        {
            SetField(applied, "waterImpactTicks", triggerTick);
            Invoke(applied, "UpdateWaterImpactProbe");
        }

        string[] impactCommands = water.ChatMessages
            .Where(static text => text.StartsWith("/vintagertxtest impact ", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(3, impactCommands.Length);
        Assert.IsFalse(impactCommands.Any(static command =>
            command.Contains("energy", StringComparison.OrdinalIgnoreCase)));
        int[] expectedStackSizes = [1, 9, 64];
        double[] expectedVelocityX = [0.24, -0.52, 0.88];
        double[] expectedVelocityY = [-0.05, -1.50, -4.00];
        double[] expectedVelocityZ = [-0.08, 0.28, -0.46];
        for (int index = 0; index < impactCommands.Length; index++)
        {
            string[] arguments = impactCommands[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(10, arguments.Length);
            double impactX = double.Parse(arguments[2], CultureInfo.InvariantCulture);
            double impactY = double.Parse(arguments[3], CultureInfo.InvariantCulture);
            double impactZ = double.Parse(arguments[4], CultureInfo.InvariantCulture);
            double velocityX = double.Parse(arguments[5], CultureInfo.InvariantCulture);
            double velocityY = double.Parse(arguments[6], CultureInfo.InvariantCulture);
            double velocityZ = double.Parse(arguments[7], CultureInfo.InvariantCulture);
            double flightSeconds = (
                velocityY
                + Math.Sqrt(
                    velocityY * velocityY
                    + 2.0 * 9.80665 * expectedDropHeights[index]))
                / 9.80665;
            Assert.AreEqual(landingTargets[index].X - velocityX * flightSeconds, impactX, 0.011);
            Assert.AreEqual(landingTargets[index].Y + expectedDropHeights[index], impactY, 0.011);
            Assert.AreEqual(landingTargets[index].Z - velocityZ * flightSeconds, impactZ, 0.011);
            Assert.AreEqual(expectedVelocityX[index], velocityX, 0.001);
            Assert.AreEqual(expectedVelocityY[index], velocityY, 0.001);
            Assert.AreEqual(expectedVelocityZ[index], velocityZ, 0.001);
            Assert.AreEqual(expectedStackSizes[index], int.Parse(arguments[8], CultureInfo.InvariantCulture));
            Assert.AreEqual(expectedDropHeights[index], double.Parse(arguments[9], CultureInfo.InvariantCulture), 0.001);
        }
        string expectedStrongImpactLog = string.Format(
            CultureInfo.InvariantCulture,
            "target=({0:0.00},{1:0.00}), spawn=({2:0.00},{3:0.00},{4:0.00}), drop-height=5.00 m, stack=64, expected pseudo-mass=2.800 kg",
            landingTargets[2].X,
            landingTargets[2].Z,
            double.Parse(impactCommands[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)[2], CultureInfo.InvariantCulture),
            double.Parse(impactCommands[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)[3], CultureInfo.InvariantCulture),
            double.Parse(impactCommands[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)[4], CultureInfo.InvariantCulture));
        Assert.IsTrue(water.Logs.Any(entry => entry.Message.Contains(
            expectedStrongImpactLog,
            StringComparison.Ordinal)));

        SetField(applied, "waterProjectileTicks", 1_799);
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        SetField(applied, "waterProjectileTicks", 1_899);
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        Invoke(applied, "UpdateWaterProjectileProbe");
        string[] projectileCommands = water.ChatMessages
            .Where(static text => text.StartsWith("/vintagertxtest projectile ", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(2, projectileCommands.Length);
        CollectionAssert.AreEqual(
            new[]
            {
                "projectile-stone-baseline-final",
                "projectile-stone-baseline-earlier-surface-field",
                "projectile-stone-baseline-prior-surface-field",
                "projectile-stone-baseline-surface-field",
                "projectile-arrow-baseline-final",
                "projectile-arrow-baseline-earlier-surface-field",
                "projectile-arrow-baseline-prior-surface-field",
                "projectile-arrow-baseline-surface-field"
            },
            water.DiagnosticCaptures.Select(static capture => capture.Label).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                VintageRtxDebugView.Final,
                VintageRtxDebugView.LiquidSurfaceField,
                VintageRtxDebugView.LiquidSurfaceField,
                VintageRtxDebugView.LiquidSurfaceField,
                VintageRtxDebugView.Final,
                VintageRtxDebugView.LiquidSurfaceField,
                VintageRtxDebugView.LiquidSurfaceField,
                VintageRtxDebugView.LiquidSurfaceField
            },
            water.DiagnosticCaptures.Select(static capture => capture.View).ToArray());
        string[] expectedKinds = ["stone", "arrow"];
        double[] projectileDropHeights = [0.65, 1.40];
        double[] projectileVelocityX = [5.50, -18.00];
        double[] projectileVelocityY = [-0.35, -20.00];
        double[] projectileVelocityZ = [0.45, -12.00];
        for (int index = 0; index < projectileCommands.Length; index++)
        {
            string[] arguments = projectileCommands[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(9, arguments.Length);
            Assert.AreEqual(expectedKinds[index], arguments[2]);
            double spawnX = double.Parse(arguments[3], CultureInfo.InvariantCulture);
            double spawnY = double.Parse(arguments[4], CultureInfo.InvariantCulture);
            double spawnZ = double.Parse(arguments[5], CultureInfo.InvariantCulture);
            double velocityX = double.Parse(arguments[6], CultureInfo.InvariantCulture);
            double velocityY = double.Parse(arguments[7], CultureInfo.InvariantCulture);
            double velocityZ = double.Parse(arguments[8], CultureInfo.InvariantCulture);
            double flightSeconds = (
                velocityY
                + Math.Sqrt(
                    velocityY * velocityY
                    + 2.0 * 9.80665 * projectileDropHeights[index]))
                / 9.80665;
            Assert.AreEqual(projectileTargets[index].X - velocityX * flightSeconds, spawnX, 0.011);
            Assert.AreEqual(projectileTargets[index].Y + projectileDropHeights[index], spawnY, 0.011);
            Assert.AreEqual(projectileTargets[index].Z - velocityZ * flightSeconds, spawnZ, 0.011);
            Assert.AreEqual(projectileVelocityX[index], velocityX, 0.001);
            Assert.AreEqual(projectileVelocityY[index], velocityY, 0.001);
            Assert.AreEqual(projectileVelocityZ[index], velocityZ, 0.001);
        }
        Assert.IsTrue(water.Logs.Any(static entry => entry.Message.Contains(
            "sequence=1/2, kind=stone, entity-type=game:thrownitem, payload=game:stone-granite",
            StringComparison.Ordinal)));
        Assert.IsTrue(water.Logs.Any(static entry => entry.Message.Contains(
            "sequence=2/2, kind=arrow, entity-type=game:arrow-flint, payload=game:arrow-flint",
            StringComparison.Ordinal)));
        applied.Dispose();

        RuntimeCoverageProbeHarness anonymousWater = CreateWaterHarness(hasBank: true);
        anonymousWater.Water.Code = null!;
        RuntimeScenarioProbe anonymousProbe = anonymousWater.CreateProbe("water-reflection");
        Assert.IsTrue((bool)Invoke(anonymousProbe, "TryApplyWaterReflectionCamera")!);
        Assert.IsTrue(anonymousWater.Logs.Any(static entry => entry.Message.Contains("water=unknown", StringComparison.Ordinal)));
        anonymousProbe.Dispose();

        RuntimeCoverageProbeHarness noBank = CreateWaterHarness(hasBank: false);
        RuntimeScenarioProbe rejected = noBank.CreateProbe("water-reflection");
        Assert.IsFalse((bool)Invoke(rejected, "TryApplyWaterReflectionCamera")!);
        rejected.Dispose();
    }

    /// <summary>
    /// Verifies the water View Rejects Each Far Surface Failure Independently regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WaterViewRejectsEachFarSurfaceFailureIndependently()
    {
        BlockPos sample = new(0);

        RuntimeCoverageProbeHarness noFarWater = CreateWaterViewHarness(
            static (_, _, _, _) => false,
            static (_, _) => 10);
        Assert.IsFalse(RuntimeScenarioProbe.TryFindWaterView(
            noFarWater.BlockAccessor, sample, 0, 10, 0,
            out _, out _, out _, out _, out _, out _, out _));

        RuntimeCoverageProbeHarness wrongHeight = CreateWaterViewHarness(
            static (x, y, _, layer) => layer == BlockLayersAccess.Fluid && x < -10 && y == 14,
            static (x, _) => x < -10 ? 14 : 10);
        Assert.IsFalse(RuntimeScenarioProbe.TryFindWaterView(
            wrongHeight.BlockAccessor, sample, 0, 10, 0,
            out _, out _, out _, out _, out _, out _, out _));

        RuntimeCoverageProbeHarness sparsePatch = CreateWaterViewHarness(
            static (x, y, z, layer) => layer == BlockLayersAccess.Fluid && x < -10 && z == 0 && y == 10,
            static (_, _) => 10);
        Assert.IsFalse(RuntimeScenarioProbe.TryFindWaterView(
            sparsePatch.BlockAccessor, sample, 0, 10, 0,
            out _, out _, out _, out _, out _, out _, out _));

        RuntimeCoverageProbeHarness brokenRun = CreateWaterViewHarness(
            static (x, y, _, layer) => layer == BlockLayersAccess.Fluid && x <= -10 && y == 10,
            static (_, _) => 10);
        Assert.IsFalse(RuntimeScenarioProbe.TryFindWaterView(
            brokenRun.BlockAccessor, sample, 0, 10, 0,
            out _, out _, out _, out _, out _, out _, out _));
    }

    /// <summary>
    /// Creates exterior Harness with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Exterior Harness result consumed by the caller&apos;s assertion.</returns>
    private static RuntimeCoverageProbeHarness CreateExteriorHarness()
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.Solid.BlockMaterial = EnumBlockMaterial.Wood;
        harness.Solid.Code = new AssetLocation("game:slantedroofing-oak");
        harness.RainHeightAt = static (x, z) => Math.Abs(x) <= 1 && Math.Abs(z) <= 1 ? 82 : 78;
        harness.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid
                && (y == harness.RainHeightAt(x, z)
                    || (Math.Abs(x) <= 1 && Math.Abs(z) <= 1 && y == 80))
                ? harness.Solid
                : harness.Air;
        return harness;
    }

    /// <summary>
    /// Creates cave Harness with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Cave Harness result consumed by the caller&apos;s assertion.</returns>
    private static RuntimeCoverageProbeHarness CreateCaveHarness()
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.RainHeightAt = static (_, _) => 64;
        harness.FallbackBlockAt = (x, y, z, layer) =>
        {
            if (layer == BlockLayersAccess.Fluid)
            {
                return harness.Air;
            }

            bool inside = Math.Abs(x) <= 20 && Math.Abs(z) <= 20;
            if (!inside)
            {
                return harness.Air;
            }

            return y == 35 || y == 43 || (x >= 8 && y is >= 36 and <= 42)
                ? harness.Solid
                : harness.Air;
        };
        return harness;
    }

    /// <summary>
    /// Creates water Harness with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="hasBank">The has Bank input used to configure this deterministic test path.</param>
    /// <returns>The create Water Harness result consumed by the caller&apos;s assertion.</returns>
    private static RuntimeCoverageProbeHarness CreateWaterHarness(bool hasBank)
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.RainHeightAt = static (_, _) => 64;
        harness.FallbackBlockAt = (x, y, z, layer) =>
        {
            if (y != 64)
            {
                return harness.Air;
            }

            if (layer == BlockLayersAccess.Fluid && x >= 8)
            {
                return harness.Water;
            }

            if (layer != BlockLayersAccess.Fluid && hasBank && x < 8)
            {
                return harness.Solid;
            }

            return harness.Air;
        };
        return harness;
    }

    /// <summary>
    /// Creates water View Harness with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="isWater">The is Water input used to configure this deterministic test path.</param>
    /// <param name="rainHeight">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <returns>The create Water View Harness result consumed by the caller&apos;s assertion.</returns>
    private static RuntimeCoverageProbeHarness CreateWaterViewHarness(
        System.Func<int, int, int, int, bool> isWater,
        System.Func<int, int, int> rainHeight)
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.RainHeightAt = rainHeight;
        harness.FallbackBlockAt = (x, y, z, layer) =>
        {
            if (x == 2 && y == 10 && z == 0 && layer != BlockLayersAccess.Fluid)
            {
                return harness.Solid;
            }

            return isWater(x, y, z, layer) ? harness.Water : harness.Air;
        };
        return harness;
    }

    /// <summary>
    /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="methodName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The invoke result consumed by the caller&apos;s assertion.</returns>
    private static object? Invoke(object instance, string methodName, params object?[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    /// <summary>
    /// Invokes static through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="type">The type input used to configure this deterministic test path.</param>
    /// <param name="methodName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The invoke Static result consumed by the caller&apos;s assertion.</returns>
    private static object? InvokeStatic(Type type, string methodName, params object?[] arguments)
    {
        MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, methodName);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    /// <summary>
    /// Returns field from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The get Field result consumed by the caller&apos;s assertion.</returns>
    private static T GetField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>Sets one private fixture field for a deterministic state transition.</summary>
    /// <param name="instance">Fixture instance that owns the private field.</param>
    /// <param name="fieldName">Exact private field name.</param>
    /// <param name="value">Value assigned before invoking the tested transition.</param>
    private static void SetField(object instance, string fieldName, object value)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, value);
    }
}
