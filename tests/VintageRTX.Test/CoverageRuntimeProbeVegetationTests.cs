using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Covers the copied-save vegetation staging state machine without launching Vintage Story.
/// The fixture preserves the real five-block layout and authoritative client replication order.
/// </summary>
[TestClass]
public sealed class CoverageRuntimeProbeVegetationTests
{
    /// <summary>Process-wide capture gate restored after every isolated vegetation test.</summary>
    private const string ReadyEnvironmentVariable = "VINTAGERTX_VEGETATION_MAP_READY";

    /// <summary>
    /// Proves that every side of the expanding-square search can select a patch and that a world
    /// with no vertically reachable receiver terminates with cleared output coordinates.
    /// </summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void ExpandingSquareSearchCoversEveryEdgeAndExhaustion()
    {
        (int X, int Z)[] targets =
        [
            (16, 8),
            (16, 24),
            (8, 16),
            (24, 16)
        ];

        foreach ((int targetX, int targetZ) in targets)
        {
            RuntimeCoverageProbeHarness harness = CreateSearchHarness(targetX, targetZ);

            Assert.IsTrue(RuntimeScenarioProbe.TryFindVegetationStagingPatch(
                harness.BlockAccessor,
                new BlockPos(0),
                16,
                64,
                16,
                out int centerX,
                out int surfaceY,
                out int centerZ));
            Assert.AreEqual(targetX, centerX);
            Assert.AreEqual(64, surfaceY);
            Assert.AreEqual(targetZ, centerZ);
        }

        RuntimeCoverageProbeHarness unreachable = new()
        {
            RainHeightAt = static (_, _) => 1_000
        };
        Assert.IsFalse(RuntimeScenarioProbe.TryFindVegetationStagingPatch(
            unreachable.BlockAccessor,
            new BlockPos(0),
            0,
            64,
            0,
            out int missingX,
            out int missingY,
            out int missingZ));
        Assert.AreEqual(0, missingX);
        Assert.AreEqual(0, missingY);
        Assert.AreEqual(0, missingZ);
    }

    /// <summary>
    /// Scans one complete signed chunk and verifies the deliberately named fence type is counted
    /// exactly once; this protects the expensive real-map audit from off-by-one chunk bounds.
    /// </summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void FenceAuditScansExactSignedChunkBounds()
    {
        RuntimeCoverageProbeHarness harness = new();
        Block fence = new BlockFenceStackAware
        {
            BlockId = 91,
            BlockMaterial = EnumBlockMaterial.Wood
        };
        harness.FallbackBlockAt = (x, y, z, _) =>
            x == -32 && y == 64 && z == -32 ? fence : harness.Air;

        int count = RuntimeScenarioProbe.CountFenceStackAwareBlocksInChunk(
            harness.BlockAccessor,
            new BlockPos(0),
            -1,
            65,
            -1,
            out int chunkX,
            out int chunkY,
            out int chunkZ);

        Assert.AreEqual(1, count);
        Assert.AreEqual(-1, chunkX);
        Assert.AreEqual(2, chunkY);
        Assert.AreEqual(-1, chunkZ);
    }

    /// <summary>
    /// Drives placement arming, delayed command dispatch, replication timeout, geometry rejection,
    /// successful alpha-cut verification, and the terminal no-op through deterministic block data.
    /// </summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void VegetationProbeCoversReplicationFailureAndSuccessfulGateRelease()
    {
        string? previousReady = Environment.GetEnvironmentVariable(ReadyEnvironmentVariable);
        RuntimeScenarioProbe? successfulProbe = null;
        RuntimeScenarioProbe? rejectedProbe = null;
        try
        {
            RuntimeCoverageProbeHarness successful = CreateVegetationHarness();
            successfulProbe = successful.CreateProbe("vegetation-shadow-map");
            Assert.IsTrue((bool)Invoke(successfulProbe, "TryApplyVegetationShadowMapCamera")!);
            Assert.AreEqual("staging", Environment.GetEnvironmentVariable(ReadyEnvironmentVariable));
            Assert.IsTrue(successful.ChatMessages.Any(static message =>
                message.StartsWith("/tp =", StringComparison.Ordinal)));

            SetField(successfulProbe, "vegetationPlacementTicks", 3);
            Invoke(successfulProbe, "UpdateVegetationShadowMapProbe");
            Assert.IsFalse(successful.ChatMessages.Any(static message =>
                message.StartsWith("/vintagertxtestvegetation place", StringComparison.Ordinal)));
            Invoke(successfulProbe, "UpdateVegetationShadowMapProbe");
            Assert.IsTrue(successful.ChatMessages.Any(static message =>
                message.StartsWith("/vintagertxtestvegetation place", StringComparison.Ordinal)));

            Invoke(successfulProbe, "UpdateVegetationShadowMapProbe");
            SetField(successfulProbe, "vegetationPlacementTicks", 749);
            Invoke(successfulProbe, "UpdateVegetationShadowMapProbe");
            Assert.IsTrue(successful.Logs.Any(static entry => entry.Message.Contains(
                "placement verification timed out",
                StringComparison.Ordinal)));

            (int centerX, int plantY, int centerZ) = ReadVegetationCenter(successfulProbe);
            successful.SetBlock(centerX - 3, plantY, centerZ - 1, successful.Solid);
            SetField(successfulProbe, "vegetationPlacementTicks", 749);
            Invoke(successfulProbe, "UpdateVegetationShadowMapProbe");
            Assert.IsTrue(successful.Logs.Any(static entry => entry.Message.Contains(
                "observed=game:solid",
                StringComparison.Ordinal)));

            InstallPlantWitnesses(successful, successfulProbe, validGeometry: true);
            Invoke(successfulProbe, "UpdateVegetationShadowMapProbe");
            Assert.AreEqual("1", Environment.GetEnvironmentVariable(ReadyEnvironmentVariable));
            Assert.IsTrue(successful.Logs.Any(static entry => entry.Message.Contains(
                "Vegetation map geometry verified: PASS",
                StringComparison.Ordinal)));
            Invoke(successfulProbe, "UpdateVegetationShadowMapProbe");

            RuntimeCoverageProbeHarness rejected = CreateVegetationHarness();
            rejectedProbe = rejected.CreateProbe("vegetation-shadow-map");
            SeedReplicationState(rejectedProbe, centerX: 16, plantY: 65, centerZ: 16);
            InstallPlantWitnesses(rejected, rejectedProbe, validGeometry: false);
            Invoke(rejectedProbe, "UpdateVegetationShadowMapProbe");
            Assert.IsTrue(rejected.Logs.Any(static entry => entry.Message.Contains(
                "Vegetation map geometry verified: FAIL",
                StringComparison.Ordinal)));
            Assert.AreEqual("0", Environment.GetEnvironmentVariable(ReadyEnvironmentVariable));
        }
        finally
        {
            successfulProbe?.Dispose();
            rejectedProbe?.Dispose();
            Environment.SetEnvironmentVariable(ReadyEnvironmentVariable, previousReady);
        }
    }

    /// <summary>
    /// Covers the camera's vertical-sun guard, absent-calendar fallback, and the failure returned
    /// when every loaded rain-map height is outside the scenario teleport envelope.
    /// </summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void VegetationCameraCoversSunFallbacksAndMissingPatch()
    {
        string? previousReady = Environment.GetEnvironmentVariable(ReadyEnvironmentVariable);
        RuntimeScenarioProbe? verticalProbe = null;
        RuntimeScenarioProbe? calendarlessProbe = null;
        RuntimeScenarioProbe? missingProbe = null;
        try
        {
            RuntimeCoverageProbeHarness vertical = CreateVegetationHarness();
            vertical.SunDirection = new Vec3f(0, 1, 0);
            verticalProbe = vertical.CreateProbe("vegetation-shadow-map");
            Assert.IsTrue((bool)Invoke(verticalProbe, "TryApplyVegetationShadowMapCamera")!);

            RuntimeCoverageProbeHarness calendarless = CreateVegetationHarness();
            calendarless.CalendarAvailable = false;
            calendarlessProbe = calendarless.CreateProbe("vegetation-shadow-map");
            Assert.IsTrue((bool)Invoke(calendarlessProbe, "TryApplyVegetationShadowMapCamera")!);

            RuntimeCoverageProbeHarness missing = new()
            {
                RainHeightAt = static (_, _) => 1_000
            };
            missingProbe = missing.CreateProbe("vegetation-shadow-map");
            Assert.IsFalse((bool)Invoke(missingProbe, "TryApplyVegetationShadowMapCamera")!);
        }
        finally
        {
            verticalProbe?.Dispose();
            calendarlessProbe?.Dispose();
            missingProbe?.Dispose();
            Environment.SetEnvironmentVariable(ReadyEnvironmentVariable, previousReady);
        }
    }

    /// <summary>
    /// Exercises the unarmed replication no-op and every independently rejected center/witness
    /// predicate so the staging contract cannot hide an uncovered destructive placement path.
    /// </summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void VegetationPatchCoversEveryRejectionPredicate()
    {
        RuntimeCoverageProbeHarness inactive = new();
        using RuntimeScenarioProbe inactiveProbe = inactive.CreateProbe("vegetation-shadow-map");
        Invoke(inactiveProbe, "UpdateVegetationShadowMapProbe");

        Block wood = new()
        {
            BlockId = 80,
            BlockMaterial = EnumBlockMaterial.Wood,
            CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)]
        };
        Block collidablePlant = new()
        {
            BlockId = 81,
            BlockMaterial = EnumBlockMaterial.Plant,
            CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)]
        };
        Block gravel = new() { BlockId = 82, BlockMaterial = EnumBlockMaterial.Gravel };
        Block sand = new() { BlockId = 83, BlockMaterial = EnumBlockMaterial.Sand };
        Block plant = new()
        {
            BlockId = 84,
            BlockMaterial = EnumBlockMaterial.Plant,
            CollisionBoxes = null!
        };
        Assert.IsFalse(RuntimeScenarioProbe.IsVegetationReceiverGround(null));
        Assert.IsTrue(RuntimeScenarioProbe.IsVegetationReceiverGround(gravel));
        Assert.IsTrue(RuntimeScenarioProbe.IsVegetationReceiverGround(sand));
        Assert.IsTrue(RuntimeScenarioProbe.IsVegetationReceiverGround(inactive.Solid));
        Assert.IsFalse(RuntimeScenarioProbe.IsReplaceableVegetationCell(collidablePlant));
        Assert.IsTrue(RuntimeScenarioProbe.IsReplaceableVegetationCell(null));
        Assert.IsTrue(RuntimeScenarioProbe.IsReplaceableVegetationCell(inactive.Air));
        Assert.IsTrue(RuntimeScenarioProbe.IsReplaceableVegetationCell(plant));

        AssertPatchRejected(static (_, _, _, _) => null!);
        AssertPatchRejected((x, y, z, _) => x == 16 && y == 64 && z == 16 ? wood : inactive.Air);
        AssertPatchRejected((x, y, z, _) =>
            x == 16 && y == 64 && z == 16 ? inactive.Solid
            : x == 16 && y == 65 && z == 16 ? inactive.Solid
            : inactive.Air);

        AssertWitnessRejected(inactive, static (x, z) => x == 13 && z == 15 ? 67 : 64);
        AssertWitnessRejected(inactive, static (_, _) => 64, centerX: 2);
        AssertWitnessRejected(inactive, static (_, _) => 64, witnessGround: inactive.Air);
        AssertWitnessRejected(inactive, static (_, _) => 64, witnessGround: wood);
        AssertWitnessRejected(inactive, static (_, _) => 64, firstAbove: inactive.Solid);
        AssertWitnessRejected(inactive, static (_, _) => 64, secondAbove: inactive.Solid);
    }

    /// <summary>
    /// Runs the verified-tick integration for vegetation failure/success and for the three legacy
    /// world-camera scenarios whose successful branch must return to the common tick tail.
    /// </summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void VerifiedTickCoversEveryWorldCameraContinuation()
    {
        string? previousReady = Environment.GetEnvironmentVariable(ReadyEnvironmentVariable);
        try
        {
            RuntimeCoverageProbeHarness vegetation = CreateVegetationHarness();
            using RuntimeScenarioProbe vegetationProbe = vegetation.CreateProbe("vegetation-shadow-map");
            RunVerifiedTick(vegetationProbe);
            Assert.IsTrue(vegetation.ChatMessages.Any(static message =>
                message.StartsWith("/tp =", StringComparison.Ordinal)));

            RuntimeCoverageProbeHarness missingVegetation = new()
            {
                RainHeightAt = static (_, _) => 1_000
            };
            using RuntimeScenarioProbe missingVegetationProbe =
                missingVegetation.CreateProbe("vegetation-shadow-map");
            RunVerifiedTick(missingVegetationProbe);
            Assert.IsTrue(missingVegetation.Logs.Any(static entry => entry.Message.Contains(
                "Vegetation map camera failed",
                StringComparison.Ordinal)));

            using RuntimeScenarioProbe exteriorProbe =
                CreateExistingWorldHarness("CreateExteriorHarness").CreateProbe("exterior-roof");
            RunVerifiedTick(exteriorProbe);
            using RuntimeScenarioProbe caveProbe =
                CreateExistingWorldHarness("CreateCaveHarness").CreateProbe("cave-interior");
            RunVerifiedTick(caveProbe);
            using RuntimeScenarioProbe waterProbe =
                CreateExistingWorldHarness("CreateWaterHarness", true).CreateProbe("water-reflection");
            RunVerifiedTick(waterProbe);

            RuntimeCoverageProbeHarness defaultGate = new();
            RuntimeScenarioProbe? defaultGateProbe = RuntimeScenarioProbe.TryStart(
                defaultGate.Api,
                static () => true,
                static () => true,
                static key => key == "VINTAGERTX_TEST_SCENARIO" ? "environment-only" : null,
                isStartupWorldStateReady: null);
            Assert.IsNotNull(defaultGateProbe);
            defaultGateProbe.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReadyEnvironmentVariable, previousReady);
        }
    }

    /// <summary>
    /// Covers the remaining water-probe guard returns and invalid physical inputs without advancing
    /// a complete runtime scenario or allocating a graphics resource.
    /// </summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void WaterProbeCoversBaselineAndTargetFailureGuards()
    {
        RuntimeCoverageProbeHarness noCaptureHarness = new();
        using RuntimeScenarioProbe noCaptureProbe = RuntimeScenarioProbe.TryStart(
            noCaptureHarness.Api,
            static () => true,
            static () => true,
            static key => key == "VINTAGERTX_TEST_SCENARIO" ? "water-reflection" : null,
            isStartupWorldStateReady: null,
            queueDiagnosticCapture: null)
            ?? throw new AssertFailedException("Water probe was not created.");
        Assert.IsTrue((bool)Invoke(noCaptureProbe, "TryCompleteProjectileBaseline", "stone")!);
        RunVerifiedTick(noCaptureProbe);

        RuntimeCoverageProbeHarness busy = new() { DiagnosticCaptureIdle = false };
        using RuntimeScenarioProbe busyProbe = busy.CreateProbe("water-reflection");
        Assert.IsFalse((bool)Invoke(busyProbe, "TryCompleteProjectileBaseline", "stone")!);

        RuntimeCoverageProbeHarness rejectedCapture = new() { DiagnosticCaptureResult = false };
        using RuntimeScenarioProbe rejectedCaptureProbe = rejectedCapture.CreateProbe("water-reflection");
        Assert.IsFalse((bool)Invoke(
            rejectedCaptureProbe,
            "TryCompleteProjectileBaseline",
            "stone")!);

        SetField(rejectedCaptureProbe, "waterImpactTargetSet", true);
        Invoke(rejectedCaptureProbe, "UpdateWaterImpactProbe");
        Invoke(rejectedCaptureProbe, "UpdateWaterProjectileProbe");

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            InvokeStatic("WaterImpactDropHeightMetres", 3));

        RuntimeCoverageProbeHarness dry = new();
        Assert.IsFalse((bool)Invoke(
            rejectedCaptureProbe,
            "TryConfigureWaterImpactTargets",
            dry.BlockAccessor,
            new BlockPos(0),
            0.0,
            0.0,
            1.0,
            0.0,
            10.0)!);
        Assert.IsFalse((bool)InvokeStatic(
            "TryResolveWaterWitnessAnchor",
            dry.BlockAccessor,
            new BlockPos(0),
            new Vec3d(10, 64, 0),
            0.4,
            0.0,
            null!)!);

        RuntimeCoverageProbeHarness missingProjectile = new();
        missingProjectile.RainHeightAt = static (_, _) => 64;
        missingProjectile.FallbackBlockAt = (x, y, _, layer) =>
            layer == BlockLayersAccess.Fluid && y == 64 && x is 8 or 10 or 11
                ? missingProjectile.Water
                : missingProjectile.Air;
        Assert.IsFalse((bool)Invoke(
            rejectedCaptureProbe,
            "TryConfigureWaterImpactTargets",
            missingProjectile.BlockAccessor,
            new BlockPos(0),
            0.0,
            0.0,
            1.0,
            0.0,
            10.0)!);

        RuntimeCoverageProbeHarness nullFenceChunk = new()
        {
            FallbackBlockAt = static (_, _, _, _) => null!
        };
        Assert.AreEqual(0, RuntimeScenarioProbe.CountFenceStackAwareBlocksInChunk(
            nullFenceChunk.BlockAccessor,
            new BlockPos(0),
            0,
            0,
            0,
            out _,
            out _,
            out _));
    }

    /// <summary>Covers slow diagnostic capture states and null-like vegetation replication results.</summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void AsyncCaptureAndVegetationNullResultsCoverDefensiveBranches()
    {
        RuntimeCoverageProbeHarness capture = new() { DiagnosticCaptureIdle = false };
        using RuntimeScenarioProbe captureProbe = capture.CreateProbe("water-reflection");
        SetField(captureProbe, "waterImpactCommandIndex", 3);
        Invoke(captureProbe, "UpdateLocalBodyMirrorCapture");
        SetField(captureProbe, "waterLocalBodyCaptureDelayTicks", 249);
        Invoke(captureProbe, "UpdateLocalBodyMirrorCapture");
        SetEnumField(captureProbe, "waterLocalBodyCaptureState", "Capturing");
        Invoke(captureProbe, "UpdateLocalBodyMirrorCapture");

        RuntimeCoverageProbeHarness completedCapture = new();
        using RuntimeScenarioProbe completedCaptureProbe =
            completedCapture.CreateProbe("water-reflection");
        SetField(completedCaptureProbe, "waterImpactCommandIndex", 3);
        SetField(completedCaptureProbe, "waterLocalBodyCaptureDelayTicks", 249);
        Invoke(completedCaptureProbe, "UpdateLocalBodyMirrorCapture");
        Assert.AreEqual(1, completedCapture.DiagnosticCaptures.Count);
        Invoke(completedCaptureProbe, "UpdateLocalBodyMirrorCapture");
        Assert.IsTrue(completedCapture.Logs.Any(static entry => entry.Message.Contains(
            "Local-body entity-mirror capture completed",
            StringComparison.Ordinal)));

        RuntimeCoverageProbeHarness nullCandidate = CreateVegetationHarness();
        using RuntimeScenarioProbe nullCandidateProbe = nullCandidate.CreateProbe("vegetation-shadow-map");
        SeedReplicationState(nullCandidateProbe, 16, 65, 16);
        nullCandidate.ReturnNullBlockAt = static (x, y, z, layer) =>
            x == 13 && y == 65 && z == 15 && layer == BlockLayersAccess.MostSolid;
        SetField(nullCandidateProbe, "vegetationPlacementTicks", 749);
        Invoke(nullCandidateProbe, "UpdateVegetationShadowMapProbe");
        Assert.IsTrue(nullCandidate.Logs.Any(static entry => entry.Message.Contains(
            "observed=missing",
            StringComparison.Ordinal)));

        RuntimeCoverageProbeHarness nullCode = CreateVegetationHarness();
        using RuntimeScenarioProbe nullCodeProbe = nullCode.CreateProbe("vegetation-shadow-map");
        SeedReplicationState(nullCodeProbe, 16, 65, 16);
        Block malformed = new()
        {
            BlockId = 92,
            Code = null!,
            BlockMaterial = EnumBlockMaterial.Plant,
            CollisionBoxes = null!
        };
        nullCode.SetBlock(13, 65, 15, malformed);
        SetField(nullCodeProbe, "vegetationPlacementTicks", 749);
        Invoke(nullCodeProbe, "UpdateVegetationShadowMapProbe");
        Assert.IsTrue(nullCode.Logs.Any(static entry => entry.Message.Contains(
            "observed=missing",
            StringComparison.Ordinal)));

        RuntimeCoverageProbeHarness nullChunk = new()
        {
            ReturnNullBlockAt = static (_, _, _, _) => true
        };
        Assert.AreEqual(0, RuntimeScenarioProbe.CountFenceStackAwareBlocksInChunk(
            nullChunk.BlockAccessor,
            new BlockPos(0),
            0,
            0,
            0,
            out _,
            out _,
            out _));
    }

    /// <summary>Covers RenderLab fallback terrain, calendar, and corrupted aperture verification.</summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void RenderLabCoversFallbackReadersAndApertureMismatch()
    {
        RuntimeCoverageProbeHarness fallback = new()
        {
            UseFastRenderLabRainHeightLookup = false,
            CalendarAvailable = false
        };
        fallback.FallbackBlockAt = (_, _, _, _) => fallback.Air;
        using RuntimeScenarioProbe fallbackProbe = fallback.CreateProbe("render-lab");
        Assert.IsTrue((bool)Invoke(fallbackProbe, "TryBuildRenderLab")!);
        Assert.IsTrue(fallback.Logs.Any(static entry => entry.Message.Contains(
            "Render lab solar aperture verified",
            StringComparison.Ordinal)));

        RuntimeCoverageProbeHarness corrupted = new()
        {
            SuppressBlockWrites = true
        };
        corrupted.FallbackBlockAt = (_, _, _, _) => corrupted.Solid;
        using RuntimeScenarioProbe corruptedProbe = corrupted.CreateProbe("render-lab");
        Assert.ThrowsException<InvalidOperationException>(() =>
            Invoke(corruptedProbe, "TryBuildRenderLab"));
    }

    /// <summary>Covers every compound rejection in the exterior receiver and wetness-pair scans.</summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void ExteriorCameraCoversReceiverAndShelterRejections()
    {
        using RuntimeScenarioProbe feetBlocked =
            CreateExteriorRejectionHarness(ExteriorRejection.Feet).CreateProbe("exterior-roof");
        Assert.IsFalse((bool)Invoke(feetBlocked, "TryApplyExteriorRoofCamera")!);

        using RuntimeScenarioProbe headBlocked =
            CreateExteriorRejectionHarness(ExteriorRejection.Head).CreateProbe("exterior-roof");
        Assert.IsFalse((bool)Invoke(headBlocked, "TryApplyExteriorRoofCamera")!);

        using RuntimeScenarioProbe patchBlocked =
            CreateExteriorRejectionHarness(ExteriorRejection.Patch).CreateProbe("exterior-roof");
        Assert.IsFalse((bool)Invoke(patchBlocked, "TryApplyExteriorRoofCamera")!);

        using RuntimeScenarioProbe sightBlocked =
            CreateExteriorRejectionHarness(ExteriorRejection.LineOfSight).CreateProbe("exterior-roof");
        Assert.IsFalse((bool)Invoke(sightBlocked, "TryApplyExteriorRoofCamera")!);

        RuntimeCoverageProbeHarness wetness = CreateExistingWorldHarness("CreateExteriorHarness");
        wetness.FallbackBlockAt = (x, y, z, _) =>
            y == wetness.RainHeightAt(x, z) ? wetness.Solid : wetness.Air;
        using RuntimeScenarioProbe wetnessProbe = wetness.CreateProbe("rain-wetness");
        Assert.IsFalse((bool)Invoke(wetnessProbe, "TryApplyExteriorRoofCamera")!);
        Assert.IsTrue(wetness.Logs.Any(static entry => entry.Message.Contains(
            "Rain wetness exposure pair failed",
            StringComparison.Ordinal)));

        RuntimeCoverageProbeHarness validWetness =
            CreateExistingWorldHarness("CreateExteriorHarness");
        using RuntimeScenarioProbe validWetnessProbe = validWetness.CreateProbe("rain-wetness");
        Assert.IsTrue((bool)Invoke(validWetnessProbe, "TryApplyExteriorRoofCamera")!);
        Assert.IsTrue(validWetness.Logs.Any(static entry => entry.Message.Contains(
            "Rain wetness exposure pair verified",
            StringComparison.Ordinal)));

        AssertRainHeightRaceIsRejected();
    }

    /// <summary>Covers a valid dark cave candidate whose neighborhood is deliberately too narrow.</summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void CaveCameraRejectsPhysicallyNarrowCandidate()
    {
        RuntimeCoverageProbeHarness narrow = new();
        narrow.RainHeightAt = static (_, _) => 64;
        narrow.FallbackBlockAt = (x, y, z, _) =>
            x == 0 && z == 0 && y is 59 or 60 or 61 ? narrow.Air : narrow.Solid;
        using RuntimeScenarioProbe probe = narrow.CreateProbe("cave-interior");

        Assert.IsFalse((bool)Invoke(probe, "TryApplyCaveInteriorCamera")!);
    }

    /// <summary>Covers impact and both reflection-witness failures after a fully verified water run.</summary>
    [TestMethod]
    [TestCategory("Coverage")]
    public void WaterCameraCoversPostViewTargetAndWitnessFailures()
    {
        WaterFailureGeometry geometry = MeasureWaterFailureGeometry();

        PhasedWaterFixture impact = CreatePhasedWaterHarness(
            geometry,
            geometry.ImpactX,
            geometry.ImpactZ);
        using RuntimeScenarioProbe impactProbe = impact.Harness.CreateProbe("water-reflection");
        Assert.IsFalse(
            (bool)Invoke(impactProbe, "TryApplyWaterReflectionCamera")!,
            $"impact terminalHits={impact.TerminalHits()}, denial={impact.DenialTriggered()}, "
            + $"geometry={geometry}");

        PhasedWaterFixture opaque = CreatePhasedWaterHarness(
            geometry,
            geometry.OpaqueX,
            geometry.OpaqueZ);
        using RuntimeScenarioProbe opaqueProbe = opaque.Harness.CreateProbe("water-reflection");
        Assert.IsFalse(
            (bool)Invoke(opaqueProbe, "TryApplyWaterReflectionCamera")!,
            $"opaque terminalHits={opaque.TerminalHits()}, denial={opaque.DenialTriggered()}, "
            + $"geometry={geometry}");

        PhasedWaterFixture humanoid = CreatePhasedWaterHarness(
            geometry,
            geometry.HumanoidX,
            geometry.HumanoidZ);
        using RuntimeScenarioProbe humanoidProbe = humanoid.Harness.CreateProbe("water-reflection");
        Assert.IsFalse(
            (bool)Invoke(humanoidProbe, "TryApplyWaterReflectionCamera")!,
            $"humanoid terminalHits={humanoid.TerminalHits()}, denial={humanoid.DenialTriggered()}, "
            + $"geometry={geometry}");
    }

    /// <summary>Creates a terrain accessor where only one authored center and its witnesses are valid.</summary>
    /// <param name="targetX">Expected center X on the expanding ring.</param>
    /// <param name="targetZ">Expected center Z on the expanding ring.</param>
    /// <returns>Deterministic staging-search fixture.</returns>
    private static RuntimeCoverageProbeHarness CreateSearchHarness(int targetX, int targetZ)
    {
        HashSet<(int X, int Z)> validColumns =
        [
            (targetX, targetZ),
            (targetX - 3, targetZ - 1),
            (targetX - 1, targetZ + 1),
            (targetX + 1, targetZ - 1),
            (targetX + 3, targetZ + 1),
            (targetX, targetZ + 2)
        ];
        RuntimeCoverageProbeHarness harness = new();
        harness.RainHeightAt = (x, z) => validColumns.Contains((x, z)) ? 64 : 1_000;
        harness.FallbackBlockAt = (x, y, z, _) =>
            validColumns.Contains((x, z)) && y == 64 ? harness.Solid : harness.Air;
        return harness;
    }

    /// <summary>Creates a natural receiver plane accepted by the real vegetation-search contract.</summary>
    /// <returns>Deterministic copied-map terrain fixture.</returns>
    private static RuntimeCoverageProbeHarness CreateVegetationHarness()
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.RainHeightAt = static (_, _) => 64;
        harness.FallbackBlockAt = (_, y, _, _) => y == 64 ? harness.Solid : harness.Air;
        return harness;
    }

    /// <summary>Builds one exterior world that reaches exactly one compound receiver rejection.</summary>
    /// <param name="rejection">Predicate intentionally made false.</param>
    /// <returns>Deterministic exterior camera fixture.</returns>
    private static RuntimeCoverageProbeHarness CreateExteriorRejectionHarness(ExteriorRejection rejection)
    {
        const int targetX = 14;
        const int targetZ = 0;
        RuntimeCoverageProbeHarness harness = new();
        harness.RainHeightAt = (x, z) =>
        {
            if (rejection is ExteriorRejection.Feet or ExteriorRejection.Head)
            {
                return 78;
            }

            if (x == 0 && z == 0)
            {
                return 78;
            }

            if (Math.Abs(x - targetX) <= 1 && Math.Abs(z - targetZ) <= 1)
            {
                return rejection == ExteriorRejection.Patch && x == targetX + 1 ? 60 : 78;
            }

            if (rejection == ExteriorRejection.LineOfSight && x is >= 6 and <= 13 && z == 0)
            {
                return 200;
            }

            return 1_000;
        };
        harness.FallbackBlockAt = (x, y, z, _) =>
        {
            int surface = harness.RainHeightAt(x, z);
            if (rejection == ExteriorRejection.Feet && y == surface + 1)
            {
                return harness.Solid;
            }

            if (rejection == ExteriorRejection.Head && y == surface + 2)
            {
                return harness.Solid;
            }

            return y == surface ? harness.Solid : harness.Air;
        };
        return harness;
    }

    /// <summary>
    /// Simulates the engine rain map changing after receiver selection but before final wetness
    /// validation, covering the asynchronous exposed-height mismatch without weakening geometry.
    /// </summary>
    private static void AssertRainHeightRaceIsRejected()
    {
        RuntimeCoverageProbeHarness locator = CreateExistingWorldHarness("CreateExteriorHarness");
        using RuntimeScenarioProbe locatorProbe = locator.CreateProbe("rain-wetness");
        Assert.IsTrue((bool)Invoke(locatorProbe, "TryApplyExteriorRoofCamera")!);
        int bestX = (int)Math.Floor(GetField<double>(locatorProbe, "exteriorPositionX"));
        int bestZ = (int)Math.Floor(GetField<double>(locatorProbe, "exteriorPositionZ"));

        RuntimeCoverageProbeHarness counter = CreateExistingWorldHarness("CreateExteriorHarness");
        System.Func<int, int, int> stableHeight = counter.RainHeightAt;
        int stableReads = 0;
        counter.RainHeightAt = (x, z) =>
        {
            if (x == bestX && z == bestZ)
            {
                stableReads++;
            }

            return stableHeight(x, z);
        };
        using RuntimeScenarioProbe counterProbe = counter.CreateProbe("rain-wetness");
        Assert.IsTrue((bool)Invoke(counterProbe, "TryApplyExteriorRoofCamera")!);
        Assert.IsTrue(stableReads > 1);

        RuntimeCoverageProbeHarness changing = CreateExistingWorldHarness("CreateExteriorHarness");
        System.Func<int, int, int> originalHeight = changing.RainHeightAt;
        int reads = 0;
        changing.RainHeightAt = (x, z) =>
        {
            int value = originalHeight(x, z);
            if (x == bestX && z == bestZ && ++reads == stableReads)
            {
                return value + 4;
            }

            return value;
        };
        using RuntimeScenarioProbe changingProbe = changing.CreateProbe("rain-wetness");
        Assert.IsFalse((bool)Invoke(changingProbe, "TryApplyExteriorRoofCamera")!);
        Assert.IsTrue(changing.Logs.Any(static entry => entry.Message.Contains(
            "Rain wetness exposure pair failed",
            StringComparison.Ordinal)));
    }

    /// <summary>Measures the deterministic water view and derives its later exact target columns.</summary>
    /// <returns>Coordinates used to inject post-view failures.</returns>
    private static WaterFailureGeometry MeasureWaterFailureGeometry()
    {
        RuntimeCoverageProbeHarness baseline = CreateExistingWorldHarness("CreateWaterHarness", true);
        Assert.IsTrue(RuntimeScenarioProbe.TryFindWaterView(
            baseline.BlockAccessor,
            new BlockPos(0),
            10,
            64,
            0,
            out int bankX,
            out _,
            out int bankZ,
            out double targetX,
            out _,
            out double targetZ,
            out double lookDistance));
        double destinationX = bankX + 0.5;
        double destinationZ = bankZ + 0.5;
        double dx = targetX - destinationX;
        double dz = targetZ - destinationZ;
        double length = Math.Sqrt(dx * dx + dz * dz);
        double directionX = dx / length;
        double directionZ = dz / length;
        double centralDistance = Math.Clamp(lookDistance * 0.36, 6.0, 9.0);
        double impactWorldX = destinationX + directionX * (centralDistance - 1.25);
        double impactWorldZ = destinationZ + directionZ * (centralDistance - 1.25);
        double farWorldX = destinationX + directionX * (centralDistance + 1.25);
        double farWorldZ = destinationZ + directionZ * (centralDistance + 1.25);
        double perpendicularX = -directionZ;
        double perpendicularZ = directionX;
        return new WaterFailureGeometry(
            (int)Math.Floor(targetX - 0.5),
            (int)Math.Floor(targetZ - 0.5),
            (int)Math.Floor(impactWorldX),
            (int)Math.Floor(impactWorldZ),
            (int)Math.Floor(impactWorldX + perpendicularX * 0.40 - directionX * 0.90),
            (int)Math.Floor(impactWorldZ + perpendicularZ * 0.40 - directionZ * 0.90),
            (int)Math.Floor(farWorldX - perpendicularX * 0.40 + directionX * 0.90),
            (int)Math.Floor(farWorldZ - perpendicularZ * 0.40 + directionZ * 0.90));
    }

    /// <summary>Returns normal water until the verified run's terminal sample, then removes one target.</summary>
    /// <param name="geometry">Measured deterministic geometry.</param>
    /// <param name="deniedX">Post-view missing-liquid X.</param>
    /// <param name="deniedZ">Post-view missing-liquid Z.</param>
    /// <returns>Stateful but deterministic water fixture and diagnostic state readers.</returns>
    private static PhasedWaterFixture CreatePhasedWaterHarness(
        WaterFailureGeometry geometry,
        int deniedX,
        int deniedZ)
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.RainHeightAt = static (_, _) => 64;
        int terminalHits = 0;
        bool postView = false;
        bool denialTriggered = false;
        harness.FallbackBlockAt = (x, y, z, layer) =>
        {
            if (layer == BlockLayersAccess.Fluid && y == 64 && x >= 8)
            {
                if (denialTriggered)
                {
                    return harness.Air;
                }

                if (x == geometry.TerminalX && z == geometry.TerminalZ)
                {
                    terminalHits++;
                    postView |= terminalHits >= 3;
                }

                if (postView && x == deniedX && z == deniedZ)
                {
                    denialTriggered = true;
                    return harness.Air;
                }

                return harness.Water;
            }

            return layer != BlockLayersAccess.Fluid && y == 64 && x < 8
                ? harness.Solid
                : harness.Air;
        };
        return new PhasedWaterFixture(
            harness,
            () => terminalHits,
            () => denialTriggered);
    }

    /// <summary>Asserts that an invalid center supplier is rejected without mutating the world.</summary>
    /// <param name="fallback">Block resolver used by the deterministic accessor.</param>
    private static void AssertPatchRejected(System.Func<int, int, int, int, Block> fallback)
    {
        RuntimeCoverageProbeHarness harness = new()
        {
            RainHeightAt = static (_, _) => 64,
            FallbackBlockAt = fallback
        };
        Assert.IsFalse(RuntimeScenarioProbe.HasVegetationStagingPatch(
            harness.BlockAccessor,
            new BlockPos(0),
            16,
            64,
            16));
    }

    /// <summary>Asserts one independently invalid witness predicate against an otherwise valid plane.</summary>
    /// <param name="source">Fixture supplying canonical air and stone blocks.</param>
    /// <param name="rainHeight">Per-column surface elevation.</param>
    /// <param name="centerX">Center X, optionally chosen to cross a chunk boundary.</param>
    /// <param name="witnessGround">Optional first-witness ground override.</param>
    /// <param name="firstAbove">Optional first replaceable-cell override.</param>
    /// <param name="secondAbove">Optional second replaceable-cell override.</param>
    private static void AssertWitnessRejected(
        RuntimeCoverageProbeHarness source,
        System.Func<int, int, int> rainHeight,
        int centerX = 16,
        Block? witnessGround = null,
        Block? firstAbove = null,
        Block? secondAbove = null)
    {
        RuntimeCoverageProbeHarness harness = new()
        {
            RainHeightAt = rainHeight
        };
        harness.FallbackBlockAt = (x, y, z, _) =>
        {
            int surface = rainHeight(x, z);
            bool firstWitness = x == centerX - 3 && z == 15;
            if (firstWitness && y == surface)
            {
                return witnessGround ?? source.Solid;
            }

            if (firstWitness && y == surface + 1 && firstAbove is not null)
            {
                return firstAbove;
            }

            if (firstWitness && y == surface + 2 && secondAbove is not null)
            {
                return secondAbove;
            }

            return y == surface ? source.Solid : source.Air;
        };
        Assert.IsFalse(RuntimeScenarioProbe.HasVegetationStagingPatch(
            harness.BlockAccessor,
            new BlockPos(0),
            centerX,
            64,
            16));
    }

    /// <summary>Marks environment convergence and advances one real probe tick.</summary>
    /// <param name="probe">Probe whose world-camera branch is exercised.</param>
    private static void RunVerifiedTick(RuntimeScenarioProbe probe)
    {
        SetField(probe, "environmentVerified", true);
        Invoke(probe, "OnTick", 0.02f);
    }

    /// <summary>Reuses the canonical deterministic world builders already owned by scenario tests.</summary>
    /// <param name="methodName">Exact private factory name.</param>
    /// <param name="arguments">Optional factory arguments.</param>
    /// <returns>Initialized in-memory world fixture.</returns>
    private static RuntimeCoverageProbeHarness CreateExistingWorldHarness(
        string methodName,
        params object?[] arguments)
    {
        MethodInfo method = typeof(RuntimeCoverageProbeWorldTests).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RuntimeCoverageProbeWorldTests).FullName, methodName);
        return (RuntimeCoverageProbeHarness)method.Invoke(null, arguments)!;
    }

    /// <summary>Reads the selected staging center and common plant elevation from private state.</summary>
    /// <param name="probe">Probe after successful camera staging.</param>
    /// <returns>Selected center and plant layer.</returns>
    private static (int CenterX, int PlantY, int CenterZ) ReadVegetationCenter(RuntimeScenarioProbe probe) =>
        (GetField<int>(probe, "vegetationCenterX"),
            GetField<int>(probe, "vegetationPlantY"),
            GetField<int>(probe, "vegetationCenterZ"));

    /// <summary>Seeds the authoritative-replication phase without repeating the expensive chunk audit.</summary>
    /// <param name="probe">Vegetation scenario probe.</param>
    /// <param name="centerX">Fixture center X.</param>
    /// <param name="plantY">Fixture plant elevation.</param>
    /// <param name="centerZ">Fixture center Z.</param>
    private static void SeedReplicationState(
        RuntimeScenarioProbe probe,
        int centerX,
        int plantY,
        int centerZ)
    {
        SetField(probe, "vegetationCenterX", centerX);
        SetField(probe, "vegetationPlantY", plantY);
        SetField(probe, "vegetationCenterZ", centerZ);
        SetField(probe, "vegetationPlacementArmed", true);
        SetField(probe, "vegetationPlacementRequested", true);
        SetField(probe, "vegetationPlacementTicks", 24);
        int[] elevations = GetField<int[]>(probe, "vegetationWitnessPlantYs");
        Array.Fill(elevations, plantY);
    }

    /// <summary>Installs the five exact stock block codes with either accepted or rejected geometry.</summary>
    /// <param name="harness">Block accessor owner.</param>
    /// <param name="probe">Probe carrying the selected witness elevations.</param>
    /// <param name="validGeometry">Whether all alpha-cut and draw-type counts satisfy the gate.</param>
    private static void InstallPlantWitnesses(
        RuntimeCoverageProbeHarness harness,
        RuntimeScenarioProbe probe,
        bool validGeometry)
    {
        (int centerX, int plantY, int centerZ) = ReadVegetationCenter(probe);
        (int X, int Z, string Code, EnumDrawType DrawType)[] witnesses =
        [
            (-3, -1, "game:tallgrass-verytall-free", EnumDrawType.Cross),
            (-1, 1, "game:tallgrass-tall-free", EnumDrawType.Cross),
            (1, -1, "game:tallgrass-medium-free", EnumDrawType.Cross),
            (3, 1, "game:flower-redtopgrass-free", EnumDrawType.JSON),
            (0, 2, "game:fern-eaglefern", EnumDrawType.JSON)
        ];
        int[] elevations = GetField<int[]>(probe, "vegetationWitnessPlantYs");
        for (int index = 0; index < witnesses.Length; index++)
        {
            (int x, int z, string code, EnumDrawType drawType) = witnesses[index];
            Block block = harness.RegisterBlock(
                code,
                100 + index,
                EnumBlockMaterial.Plant,
                collision: false);
            block.RenderPass = validGeometry || index > 0
                ? EnumChunkRenderPass.OpaqueNoCull
                : EnumChunkRenderPass.Opaque;
            block.DrawType = validGeometry ? drawType : EnumDrawType.Cube;
            harness.SetBlock(centerX + x, elevations[index], centerZ + z, block);
        }
    }

    /// <summary>Invokes one non-public instance method and preserves its original exception.</summary>
    /// <param name="instance">Target object.</param>
    /// <param name="methodName">Exact method name.</param>
    /// <param name="arguments">Arguments passed to the method.</param>
    /// <returns>Reflected return value.</returns>
    private static object? Invoke(object instance, string methodName, params object?[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
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

    /// <summary>Invokes one non-public static probe helper and preserves its original exception.</summary>
    /// <param name="methodName">Exact static method name.</param>
    /// <param name="arguments">Arguments passed to the helper.</param>
    /// <returns>Reflected return value.</returns>
    private static object? InvokeStatic(string methodName, params object?[] arguments)
    {
        MethodInfo method = typeof(RuntimeScenarioProbe).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RuntimeScenarioProbe).FullName, methodName);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    /// <summary>Reads one private field used as an asynchronous scenario checkpoint.</summary>
    /// <typeparam name="T">Expected field type.</typeparam>
    /// <param name="instance">Field owner.</param>
    /// <param name="fieldName">Exact private field name.</param>
    /// <returns>Current field value.</returns>
    private static T GetField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>Sets one private field to advance a deterministic asynchronous checkpoint.</summary>
    /// <param name="instance">Field owner.</param>
    /// <param name="fieldName">Exact private field name.</param>
    /// <param name="value">Checkpoint value.</param>
    private static void SetField(object instance, string fieldName, object value)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, value);
    }

    /// <summary>Sets one private enum field from its stable member name.</summary>
    /// <param name="instance">Field owner.</param>
    /// <param name="fieldName">Exact private enum field name.</param>
    /// <param name="memberName">Exact enum member name.</param>
    private static void SetEnumField(object instance, string fieldName, string memberName)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, Enum.Parse(field.FieldType, memberName));
    }

    /// <summary>Compound exterior-camera predicate selected for one negative fixture.</summary>
    private enum ExteriorRejection
    {
        /// <summary>Camera feet cell is collidable.</summary>
        Feet,

        /// <summary>Camera head cell is collidable.</summary>
        Head,

        /// <summary>The surrounding receiver is not level.</summary>
        Patch,

        /// <summary>Terrain blocks the roof sight line.</summary>
        LineOfSight
    }

    /// <summary>Deterministic water columns derived from one verified shoreline view.</summary>
    /// <param name="TerminalX">Last X sampled by the verified continuous run.</param>
    /// <param name="TerminalZ">Last Z sampled by the verified continuous run.</param>
    /// <param name="ImpactX">First impact target X.</param>
    /// <param name="ImpactZ">First impact target Z.</param>
    /// <param name="OpaqueX">Opaque reflection witness X.</param>
    /// <param name="OpaqueZ">Opaque reflection witness Z.</param>
    /// <param name="HumanoidX">Humanoid reflection witness X.</param>
    /// <param name="HumanoidZ">Humanoid reflection witness Z.</param>
    private readonly record struct WaterFailureGeometry(
        int TerminalX,
        int TerminalZ,
        int ImpactX,
        int ImpactZ,
        int OpaqueX,
        int OpaqueZ,
        int HumanoidX,
        int HumanoidZ);

    /// <summary>Stateful water fixture with observable phase counters for assertion diagnostics.</summary>
    /// <param name="Harness">In-memory world accessor.</param>
    /// <param name="TerminalHits">Returns the terminal continuous-run sample count.</param>
    /// <param name="DenialTriggered">Returns whether the selected post-view column was removed.</param>
    private readonly record struct PhasedWaterFixture(
        RuntimeCoverageProbeHarness Harness,
        System.Func<int> TerminalHits,
        System.Func<bool> DenialTriggered);

    /// <summary>
    /// Test-only runtime type whose exact name exercises the defensive fence audit without loading
    /// the gameplay assembly that owns Vintage Story's concrete implementation.
    /// </summary>
    private sealed class BlockFenceStackAware : Block;
}
