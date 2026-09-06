using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using VintageRTX.RuntimeTestSupport;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>Locks the copied-map vegetation scenario without starting Vintage Story.</summary>
[TestClass]
public sealed class RuntimeVegetationMapScenarioTests
{
    /// <summary>The Test Explorer row is a fixed foggy-village Ultra capture, not a benchmark.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void CatalogExposesCaptureOnlyFoggyVillageVegetationScenario()
    {
        ScenarioDefinition scenario = ScenarioCatalog.Get("vegetation-shadow-map");

        Assert.AreEqual("foggy village world", scenario.World);
        Assert.AreEqual("vegetation-shadow-map", scenario.RuntimeProbe);
        Assert.AreEqual("vegetation-shadow-map", scenario.CaptureProfile);
        Assert.AreEqual(10.0, scenario.WorldHour);
        Assert.AreEqual(ShadowValidation.SunProjected, scenario.ShadowValidation);
        Assert.AreEqual(VintageRTX.Configuration.VintageRtxRenderProfile.Ultra, scenario.RenderProfile);
        Assert.IsFalse(scenario.RunBenchmark);
        CollectionAssert.Contains(
            scenario.RequiredLogTokens,
            "Vegetation map geometry verified: PASS | count=5, alpha-cutout=5, crossed-planes=3, json-shapes=2");
        CollectionAssert.Contains(scenario.RequiredLogTokens, "-final-vintagertx.png");
        CollectionAssert.Contains(scenario.RequiredLogTokens, "-voxel-shadow-vintagertx.png");
        CollectionAssert.Contains(scenario.RequiredLogTokens, "-native-sun-shadow-vintagertx.png");
    }

    /// <summary>The isolated server command owns five distinct real stock plant blocks.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void ServerPlacementContractIsBoundedDistinctAndScenarioScoped()
    {
        Assert.IsTrue(RuntimeVegetationServerModSystem.IsVegetationShadowScenario(
            " VEGETATION-SHADOW-MAP "));
        Assert.IsFalse(RuntimeVegetationServerModSystem.IsVegetationShadowScenario(null));
        Assert.IsFalse(RuntimeVegetationServerModSystem.IsVegetationShadowScenario("render-lab"));
        Assert.IsTrue(RuntimeVegetationServerModSystem.ArePlacementCoordinatesBounded(0, 64, 0));
        Assert.IsFalse(RuntimeVegetationServerModSystem.ArePlacementCoordinatesBounded(
            int.MinValue,
            64,
            0));

        VegetationPlacement[] placements = RuntimeVegetationServerModSystem.GetPlacements().ToArray();
        Assert.AreEqual(5, placements.Length);
        Assert.AreEqual(5, placements.Select(static placement => placement.Code).Distinct().Count());
        Assert.AreEqual(3, placements.Count(static placement =>
            placement.Code.StartsWith("game:tallgrass-", StringComparison.Ordinal)));
        Assert.IsTrue(placements.Any(static placement => placement.Code == "game:fern-eaglefern"));
        Assert.IsTrue(placements.Any(static placement => placement.Code == "game:flower-redtopgrass-free"));

        Block solid = new()
        {
            BlockId = 1,
            BlockMaterial = EnumBlockMaterial.Soil,
            CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)]
        };
        Block air = new() { BlockId = 0, BlockMaterial = EnumBlockMaterial.Air };
        Block roof = new()
        {
            BlockId = 2,
            BlockMaterial = EnumBlockMaterial.Wood,
            CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)]
        };
        Block replaceablePlant = new()
        {
            BlockId = 3,
            BlockMaterial = EnumBlockMaterial.Plant,
            CollisionBoxes = null
        };
        Assert.IsTrue(RuntimeVegetationServerModSystem.IsSolidGround(solid));
        Assert.IsFalse(RuntimeVegetationServerModSystem.IsSolidGround(air));
        Assert.IsFalse(RuntimeVegetationServerModSystem.IsSolidGround(roof));
        Assert.IsTrue(RuntimeVegetationServerModSystem.IsReplaceableAir(air));
        Assert.IsTrue(RuntimeVegetationServerModSystem.IsReplaceableAir(replaceablePlant));
        Assert.IsFalse(RuntimeVegetationServerModSystem.IsReplaceableAir(solid));
        Assert.IsTrue(RuntimeScenarioProbe.IsVegetationReceiverGround(solid));
        Assert.IsFalse(RuntimeScenarioProbe.IsVegetationReceiverGround(roof));
        Assert.IsTrue(RuntimeScenarioProbe.IsReplaceableVegetationCell(replaceablePlant));
        Assert.IsFalse(RuntimeScenarioProbe.IsReplaceableVegetationCell(roof));
    }

    /// <summary>Candidate terrain may slope gently but remains replaceable and owned by one chunk.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void StagingPatchRejectsContentAndChunkBoundaryCrossing()
    {
        RuntimeCoverageProbeHarness harness = new();
        Block replaceablePlant = new()
        {
            BlockId = 3,
            BlockMaterial = EnumBlockMaterial.Plant,
            CollisionBoxes = null
        };
        harness.RainHeightAt = static (_, _) => 64;
        harness.FallbackBlockAt = (_, y, _, _) => y == 64 ? harness.Solid : harness.Air;
        BlockPos sample = new(0);

        Assert.IsTrue(RuntimeScenarioProbe.HasVegetationStagingPatch(
            harness.BlockAccessor,
            sample,
            16,
            64,
            16));
        harness.FallbackBlockAt = (_, y, _, _) => y == 64
            ? harness.Solid
            : y == 65
                ? replaceablePlant
                : harness.Air;
        Assert.IsTrue(RuntimeScenarioProbe.HasVegetationStagingPatch(
            harness.BlockAccessor,
            sample,
            16,
            64,
            16));
        Assert.IsTrue(RuntimeScenarioProbe.AreVegetationWitnessesInSingleChunk(16, 65, 16));
        Assert.IsFalse(RuntimeScenarioProbe.AreVegetationWitnessesInSingleChunk(31, 65, 16));
        Assert.IsTrue(RuntimeScenarioProbe.AreVegetationWitnessesInSingleChunk(-16, 65, -16));
        Assert.IsFalse(RuntimeScenarioProbe.AreVegetationWitnessesInSingleChunk(-1, 65, -16));
        Assert.IsTrue(RuntimeScenarioProbe.IsVegetationSurfaceWithinTeleportRange(113, 70));
        Assert.IsTrue(RuntimeScenarioProbe.IsVegetationSurfaceWithinTeleportRange(113, 17));
        Assert.IsFalse(RuntimeScenarioProbe.IsVegetationSurfaceWithinTeleportRange(113, 16));
        Assert.IsFalse(RuntimeScenarioProbe.IsVegetationSurfaceWithinTeleportRange(
            int.MaxValue,
            int.MinValue));

        harness.RainHeightAt = static (x, _) => x >= 16 ? 65 : 64;
        harness.FallbackBlockAt = (x, y, _, _) => y == (x >= 16 ? 65 : 64)
            ? harness.Solid
            : harness.Air;
        Assert.IsTrue(RuntimeScenarioProbe.HasVegetationStagingPatch(
            harness.BlockAccessor,
            sample,
            16,
            65,
            16));
        harness.RainHeightAt = static (x, _) => x >= 16 ? 67 : 64;
        harness.FallbackBlockAt = (x, y, _, _) => y == (x >= 16 ? 67 : 64)
            ? harness.Solid
            : harness.Air;
        Assert.IsFalse(RuntimeScenarioProbe.HasVegetationStagingPatch(
            harness.BlockAccessor,
            sample,
            16,
            67,
            16));

        harness.RainHeightAt = static (_, _) => 64;
        harness.FallbackBlockAt = (x, y, z, _) =>
            x == 15 && y == 65 && z == 17 ? harness.Solid
            : y == 64 ? harness.Solid
            : harness.Air;
        Assert.IsFalse(RuntimeScenarioProbe.HasVegetationStagingPatch(
            harness.BlockAccessor,
            sample,
            16,
            64,
            16));
    }

    /// <summary>Automatic screenshots wait for readiness and a newer settled voxel generation.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void CaptureProfileWaitsForPlacementCameraAndPostPlacementVoxelGeneration()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "vintagertx-vegetation-gate-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "180",
            ["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = "vegetation-shadow-map",
            ["VINTAGERTX_VEGETATION_MAP_READY"] = "0"
        };
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "GetOrCreateDataPath"
                ? temporary
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

        try
        {
            FrameCaptureService captures = new(
                api,
                key => environment.GetValueOrDefault(key),
                static () => DateTime.UnixEpoch);
            captures.ObserveVoxelSceneState(7, settled: true);
            Assert.IsFalse(captures.TryGetCapture(5_000, out _));

            environment["VINTAGERTX_VEGETATION_MAP_READY"] = "staging";
            Assert.IsFalse(captures.TryGetCapture(5_001, out _), "Staging must latch the pre-placement generation without capturing it.");
            environment["VINTAGERTX_VEGETATION_MAP_READY"] = "1";
            Assert.IsFalse(captures.TryGetCapture(5_002, out _));
            captures.ObserveVoxelSceneState(8, settled: false);
            Assert.IsFalse(captures.TryGetCapture(5_003, out _));
            captures.ObserveVoxelSceneState(8, settled: true);

            Assert.IsTrue(captures.TryGetCapture(5_004, out FrameCaptureRequest request));
            Assert.AreEqual("final", request.Label);

            (long Frame, string Label, VintageRtxDebugView View)[] remaining =
            [
                (5_024, "normal", VintageRtxDebugView.Normal),
                (5_044, "position", VintageRtxDebugView.Position),
                (5_064, "lighting", VintageRtxDebugView.Lighting),
                (5_074, "reflection", VintageRtxDebugView.Reflection),
                (5_084, "voxel-reflection", VintageRtxDebugView.VoxelReflection),
                (5_094, "voxel-albedo", VintageRtxDebugView.VoxelAlbedo),
                (5_104, "voxel-bounce", VintageRtxDebugView.VoxelBounce),
                (5_114, "voxel-visibility", VintageRtxDebugView.VoxelVisibility),
                (5_124, "transport-components", VintageRtxDebugView.TransportComponents),
                (5_134, "voxel-shadow", VintageRtxDebugView.VoxelShadow),
                (5_139, "native-sun-shadow", VintageRtxDebugView.NativeSunShadow),
                (5_144, "material", VintageRtxDebugView.Material),
                (5_154, "water", VintageRtxDebugView.Water),
                (5_164, "wetness", VintageRtxDebugView.Wetness),
                (5_174, "entity-mirror", VintageRtxDebugView.EntityMirror)
            ];
            foreach ((long frame, string label, VintageRtxDebugView view) in remaining)
            {
                Assert.IsTrue(captures.TryGetCapture(frame, out request), label);
                Assert.AreEqual(label, request.Label);
                Assert.AreEqual(view, request.DebugViewOverride);
            }
            Assert.IsFalse(captures.TryGetCapture(50_000, out _));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    /// <summary>An irregular plant shadow passes while a solid square fallback fails.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void RealMapVegetationMorphologyRejectsSolidBlockFallback()
    {
        using SKBitmap cutout = CreateShadowBitmap();
        for (int step = 0; step < 52; step++)
        {
            PaintShadow(cutout, 245 + step, 138 + step / 2);
            if (step is >= 5 and <= 44 && step % 3 != 0)
            {
                PaintShadow(cutout, 251 + step, 136 + step / 2);
            }
            if (step is >= 15 and <= 48 && step % 4 != 1)
            {
                PaintShadow(cutout, 240 + step, 142 + step / 2);
            }
        }

        RuntimeImageValidator.VegetationShadowAssessment cutoutAssessment =
            RuntimeImageValidator.MeasureRealMapVegetationSunShadow(cutout);
        Assert.IsTrue(cutoutAssessment.MeetsGate);
        Assert.IsTrue(cutoutAssessment.RectangularFill < 0.76);

        using SKBitmap solid = CreateShadowBitmap();
        for (int y = 145; y < 190; y++)
        {
            for (int x = 260; x < 305; x++)
            {
                PaintShadow(solid, x, y);
            }
        }

        RuntimeImageValidator.VegetationShadowAssessment solidAssessment =
            RuntimeImageValidator.MeasureRealMapVegetationSunShadow(solid);
        Assert.IsFalse(solidAssessment.MeetsGate);
        Assert.AreEqual(1.0, solidAssessment.RectangularFill, 0.0001);
    }

    /// <summary>The provenance carrier keeps voxel, native, and cascade channels independent.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void NativeSunShadowProvenanceRequiresIndependentSupportedPlantOcclusion()
    {
        using SKBitmap provenance = CreateProvenanceBitmap(copyOcclusionChannels: false);

        RuntimeImageValidator.NativeSunShadowAssessment assessment =
            RuntimeImageValidator.MeasureNativeSunShadowProvenance(provenance);

        Assert.IsTrue(assessment.MeetsGate);
        Assert.IsTrue(assessment.VoxelOcclusionRatio > 0.0);
        Assert.IsTrue(assessment.NativeOcclusionRatio > 0.0);
        Assert.IsTrue(assessment.CascadeSupportRatio > 0.50);
        Assert.AreEqual(1.0, assessment.SupportedNativeRatio, 0.0001);
        Assert.IsTrue(assessment.DifferentSourceRatio > 0.0);
        Assert.IsTrue(assessment.NativeMorphology.MeetsGate);

        using SKBitmap copied = CreateProvenanceBitmap(copyOcclusionChannels: true);
        RuntimeImageValidator.NativeSunShadowAssessment copiedAssessment =
            RuntimeImageValidator.MeasureNativeSunShadowProvenance(copied);
        Assert.IsFalse(copiedAssessment.MeetsGate);
        Assert.AreEqual(0.0, copiedAssessment.DifferentSourceRatio, 0.0001);
    }

    /// <summary>Both required PNG paths decode and pass the complete copied-map image validator.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void RealMapValidatorConsumesVoxelAndNativeSunShadowArtifacts()
    {
        string temporary = Path.Combine(
            Path.GetTempPath(),
            "vintagertx-native-shadow-artifact-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        string voxelPath = Path.Combine(temporary, "fixture-voxel-shadow-vintagertx.png");
        string nativePath = Path.Combine(temporary, "fixture-native-sun-shadow-vintagertx.png");
        try
        {
            using SKBitmap voxel = CreateShadowBitmap();
            PaintCutoutShadow(voxel, includeVoxelChannel: false);
            SavePng(voxel, voxelPath);
            using SKBitmap native = CreateProvenanceBitmap(copyOcclusionChannels: false);
            SavePng(native, nativePath);
            string log = "[VintageRTX] Native solar shadow detail active:\n"
                + "[VintageRTX.Test] Vegetation map camera applied:\n"
                + "[VintageRTX.Test] Vegetation map geometry verified: PASS | count=5, alpha-cutout=5, crossed-planes=3, json-shapes=2\n"
                + $"Comparison capture saved: fixture-voxel-shadow-before.png and {voxelPath}\n"
                + $"Comparison capture saved: fixture-native-sun-shadow-before.png and {nativePath}\n";

            IReadOnlyList<string> failures =
                RuntimeImageValidator.ValidateRealMapVegetationSunShadow(log);

            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    /// <summary>The runtime log gate rejects a fence tessellation abort after placement.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void RuntimeLogRejectsFenceStackAwareFailureAfterPlacement()
    {
        ScenarioDefinition scenario = ScenarioCatalog.Get("vegetation-shadow-map");
        string log = "[VintageRTX.Test] Vegetation map placement requested: count=5\n"
            + "2026-09-02 [Error] Exception: Index was outside the bounds of the array.\n"
            + "   at Vintagestory.GameContent.BlockFenceStackAware.OnJsonTesselation(Object source)\n"
            + "   at Vintagestory.Client.NoObf.ChunkTesselator.BuildBlockPolygons(Object chunk)\n";

        IReadOnlyList<string> failures = RuntimeLogValidator.Validate(log, scenario);

        Assert.IsTrue(failures.Any(failure => failure.Contains(
            "BlockFenceStackAware aborted chunk tessellation",
            StringComparison.Ordinal)));
    }

    /// <summary>Creates a black diagnostic at a representative runtime resolution.</summary>
    /// <returns>Mutable synthetic shadow carrier.</returns>
    private static SKBitmap CreateShadowBitmap()
    {
        SKBitmap bitmap = new(640, 360, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Black);
        return bitmap;
    }

    /// <summary>Creates a source-separated provenance fixture with broad cascade support.</summary>
    /// <param name="copyOcclusionChannels">Whether red deliberately duplicates green.</param>
    /// <returns>Mutable synthetic native sun-shadow carrier.</returns>
    private static SKBitmap CreateProvenanceBitmap(bool copyOcclusionChannels)
    {
        SKBitmap bitmap = CreateShadowBitmap();
        for (int y = 100; y < 346; y++)
        {
            for (int x = 52; x < 589; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(0, 0, 128, 255));
            }
        }

        PaintCutoutShadow(bitmap, includeVoxelChannel: copyOcclusionChannels);
        if (!copyOcclusionChannels)
        {
            for (int step = 0; step < 46; step++)
            {
                bitmap.SetPixel(
                    330 + step,
                    205 + step / 3,
                    new SKColor(190, 0, 128, 255));
            }
        }
        return bitmap;
    }

    /// <summary>Paints the irregular native plant shadow used by both image fixtures.</summary>
    /// <param name="bitmap">Target carrier.</param>
    /// <param name="includeVoxelChannel">Whether red copies green at every native sample.</param>
    private static void PaintCutoutShadow(SKBitmap bitmap, bool includeVoxelChannel)
    {
        byte red = includeVoxelChannel ? (byte)220 : (byte)0;
        for (int step = 0; step < 52; step++)
        {
            PaintProvenance(bitmap, 245 + step, 138 + step / 2, red);
            if (step is >= 5 and <= 44 && step % 3 != 0)
            {
                PaintProvenance(bitmap, 251 + step, 136 + step / 2, red);
            }
            if (step is >= 15 and <= 48 && step % 4 != 1)
            {
                PaintProvenance(bitmap, 240 + step, 142 + step / 2, red);
            }
        }
    }

    /// <summary>Writes one native-occlusion sample while retaining cascade support.</summary>
    /// <param name="bitmap">Target provenance carrier.</param>
    /// <param name="x">Pixel X.</param>
    /// <param name="y">Pixel Y.</param>
    /// <param name="voxel">Red voxel-occlusion byte.</param>
    private static void PaintProvenance(SKBitmap bitmap, int x, int y, byte voxel)
    {
        bitmap.SetPixel(x, y, new SKColor(voxel, 220, 128, 255));
    }

    /// <summary>Encodes a lossless test artifact.</summary>
    /// <param name="bitmap">Source pixels.</param>
    /// <param name="path">Destination PNG path.</param>
    private static void SavePng(SKBitmap bitmap, string path)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>Writes one projected-sun-shadow sample with native-cascade support.</summary>
    /// <param name="bitmap">Synthetic shadow carrier.</param>
    /// <param name="x">Pixel X.</param>
    /// <param name="y">Pixel Y.</param>
    private static void PaintShadow(SKBitmap bitmap, int x, int y)
    {
        bitmap.SetPixel(x, y, new SKColor(0, 220, 128, 255));
    }
}
