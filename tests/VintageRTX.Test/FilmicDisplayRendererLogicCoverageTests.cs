using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Drives the non-OpenGL renderer coordinator against deterministic Vintage
/// Story API doubles. RenderLab owns the complementary real-context GL paths.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FilmicDisplayRendererLogicCoverageTests
{
    /// <summary>Ensures a provisional CPU scene survives until it is safe to publish atomically.</summary>
    [TestMethod]
    public void DeferredVoxelSnapshotIsRetainedUntilSceneSettles()
    {
        VoxelSceneSnapshot expected = default;
        VoxelSceneSnapshot pending = expected;
        bool available = true;

        Assert.IsFalse(FilmicDisplayRenderer.TrySelectSettledVoxelSnapshot(
            ref pending,
            ref available,
            generationStable: false,
            out _));
        Assert.IsTrue(available);

        Assert.IsTrue(FilmicDisplayRenderer.TrySelectSettledVoxelSnapshot(
            ref pending,
            ref available,
            generationStable: true,
            out VoxelSceneSnapshot selected));
        Assert.AreEqual(expected, selected);
        Assert.IsFalse(available);

        Assert.IsFalse(FilmicDisplayRenderer.TrySelectSettledVoxelSnapshot(
            ref pending,
            ref available,
            generationStable: true,
            out _));
    }

    /// <summary>
    /// Verifies the coordinator Properties Early Exits Fault Reset And Dispose Are Stable regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CoordinatorPropertiesEarlyExitsFaultResetAndDisposeAreStable()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;

        Assert.AreEqual(0.95, renderer.RenderOrder);
        Assert.AreEqual(0, renderer.RenderRange);
        CollectionAssert.Contains(
            harness.RegisteredRendererStages,
            EnumRenderStage.AfterPostProcessing);
        CollectionAssert.Contains(harness.RegisteredRendererStages, EnumRenderStage.ShadowFar);
        CollectionAssert.Contains(harness.RegisteredRendererStages, EnumRenderStage.ShadowNear);
        renderer.OnRenderFrame(0.016f, EnumRenderStage.ShadowFar);
        Assert.IsTrue(GetField<bool>(renderer, "nativeShadowMatrixFarReady"));
        CollectionAssert.AreEqual(
            harness.Uniforms.ToShadowMapSpaceMatrixFar,
            GetField<float[]>(renderer, "nativeShadowMatrixFar"));
        Vec3d capturedFarReference = GetField<Vec3d>(renderer, "nativeShadowReferenceFar");
        Assert.AreEqual(harness.Uniforms.playerReferencePos.X, capturedFarReference.X, 0.0);
        Assert.AreEqual(harness.Uniforms.playerReferencePos.Y, capturedFarReference.Y, 0.0);
        Assert.AreEqual(harness.Uniforms.playerReferencePos.Z, capturedFarReference.Z, 0.0);
        Assert.AreEqual(96.0f, GetField<float>(renderer, "nativeShadowRangeFar"));
        renderer.OnRenderFrame(0.016f, EnumRenderStage.ShadowNear);
        Assert.IsTrue(GetField<bool>(renderer, "nativeShadowMatrixNearReady"));
        Assert.AreEqual(32.0f, GetField<float>(renderer, "nativeShadowRangeNear"));
        harness.Uniforms.ToShadowMapSpaceMatrixNear[3] = float.NaN;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.ShadowNear);
        Assert.IsFalse(GetField<bool>(renderer, "nativeShadowMatrixNearReady"));
        harness.Uniforms.ToShadowMapSpaceMatrixNear = IdentityMatrixFloat();
        harness.Uniforms.ShadowRangeNear = 0.0f;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.ShadowNear);
        Assert.IsFalse(GetField<bool>(renderer, "nativeShadowMatrixNearReady"));
        harness.Uniforms.ShadowRangeNear = 32.0f;
        harness.Uniforms.playerReferencePos = new Vec3d(double.NaN, 80, 20);
        renderer.OnRenderFrame(0.016f, EnumRenderStage.ShadowFar);
        Assert.IsFalse(GetField<bool>(renderer, "nativeShadowMatrixFarReady"));
        harness.Uniforms.playerReferencePos = new Vec3d(10, 80, 20);
        ReflectionSourceCaptureRenderer reflectionSource =
            (ReflectionSourceCaptureRenderer)renderer.ReflectionSourceRenderer;
        Assert.AreEqual(0.79, reflectionSource.RenderOrder);
        Assert.AreEqual(0, reflectionSource.RenderRange);
        Assert.AreEqual(0, reflectionSource.TextureId);
        Assert.IsFalse(reflectionSource.IsReady(1920, 1080));
        reflectionSource.Enabled = false;
        reflectionSource.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
        reflectionSource.Enabled = true;
        reflectionSource.OnRenderFrame(0.016f, EnumRenderStage.Before);
        Assert.IsFalse(renderer.Initialized);
        Assert.AreEqual("waiting for the render thread", renderer.Status);
        Assert.IsTrue(Path.IsPathFullyQualified(renderer.CaptureDirectory));
        Assert.IsTrue(renderer.QueueCapture());
        Assert.IsFalse(renderer.QueueCapture());
        FrameCaptureService captureService = GetField<FrameCaptureService>(renderer, "captureService");
        Assert.IsTrue(captureService.TryGetCapture(1, out _));

        harness.Config.Enabled = false;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
        harness.Config.Enabled = true;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.Before);
        harness.FrameWidth = 0;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
        reflectionSource.OnRenderFrame(0.016f, EnumRenderStage.Opaque);

        SetField(renderer, "faulted", true);
        SetField(renderer, "failureReason", "synthetic failure");
        StringAssert.Contains(renderer.Status, "faulted: synthetic failure");
        renderer.ResetFault();
        Assert.AreEqual("waiting for the render thread", renderer.Status);

        harness.FrameWidth = 1920;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
        StringAssert.StartsWith(renderer.Status, "faulted:");
        renderer.ResetFault();
        Assert.IsFalse(renderer.ReloadShader());
        StringAssert.Contains(renderer.Status, "GLSL 3.30");
        renderer.ResetFault();

        Invoke(renderer, "ResolveGBuffer", default(GameGBuffer));
        Invoke(renderer, "ResolveGBuffer", default(GameGBuffer));
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Contains("Screen-space lighting disabled", StringComparison.Ordinal)));
        int primaryIndex = (int)EnumFrameBuffer.Primary;
        while (harness.FrameBuffers.Count <= primaryIndex)
        {
            harness.FrameBuffers.Add(new FrameBufferRef());
        }
        harness.FrameBuffers[primaryIndex] = new FrameBufferRef
        {
            ColorTextureIds = [99, 11, 22, 33]
        };
        SetField(renderer, "gBufferAvailabilityLogged", false);
        Invoke(renderer, "ResolveGBuffer", default(GameGBuffer));
        Assert.IsTrue(GetField<bool>(renderer, "gBufferAvailable"));

        SetField(renderer, "initialized", true);
        SetField(renderer, "textureWidth", 1280);
        SetField(renderer, "textureHeight", 720);
        StringAssert.StartsWith(renderer.Status, "ready (1280x720)");
        renderer.Dispose();
        Assert.IsFalse(renderer.Initialized);
        CollectionAssert.Contains(
            harness.UnregisteredRendererStages,
            EnumRenderStage.AfterPostProcessing);
        CollectionAssert.Contains(harness.UnregisteredRendererStages, EnumRenderStage.ShadowFar);
        CollectionAssert.Contains(harness.UnregisteredRendererStages, EnumRenderStage.ShadowNear);
    }

    /// <summary>Validates safe borrowing of complete native near/far solar depth attachments.</summary>
    [TestMethod]
    public void NativeSunShadowDepthMapsRejectIncompleteFramebuffers()
    {
        Assert.AreEqual(
            default,
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(null));

        List<FrameBufferRef> frameBuffers = [];
        while (frameBuffers.Count <= (int)EnumFrameBuffer.ShadowmapNear)
        {
            frameBuffers.Add(new FrameBufferRef());
        }

        frameBuffers[(int)EnumFrameBuffer.ShadowmapFar] = new FrameBufferRef
        {
            Width = 2048,
            Height = 2048,
            DepthTextureId = 71
        };
        frameBuffers[(int)EnumFrameBuffer.ShadowmapNear] = new FrameBufferRef
        {
            Width = 4096,
            Height = 4096,
            DepthTextureId = 72,
            Disposed = true
        };
        Assert.AreEqual(
            new NativeSunShadowDepthMaps(71, 0),
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(frameBuffers));

        frameBuffers[(int)EnumFrameBuffer.ShadowmapNear].Disposed = false;
        Assert.AreEqual(
            new NativeSunShadowDepthMaps(71, 72),
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(frameBuffers));
        frameBuffers[(int)EnumFrameBuffer.ShadowmapFar].Width = 0;
        frameBuffers[(int)EnumFrameBuffer.ShadowmapNear].DepthTextureId = 0;
        Assert.AreEqual(
            default,
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(frameBuffers));
    }

    /// <summary>
    /// Verifies the benchmark Environment Settings Clamp And Fallback regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void BenchmarkEnvironmentSettingsClampAndFallback()
    {
        string integerName = $"VINTAGERTX_TEST_INT_{Guid.NewGuid():N}";
        string longName = $"VINTAGERTX_TEST_LONG_{Guid.NewGuid():N}";
        try
        {
            Assert.AreEqual(7, ReadIntegerSetting(integerName, 7, 3, 12));
            Environment.SetEnvironmentVariable(integerName, "bad");
            Assert.AreEqual(7, ReadIntegerSetting(integerName, 7, 3, 12));
            Environment.SetEnvironmentVariable(integerName, "-50");
            Assert.AreEqual(3, ReadIntegerSetting(integerName, 7, 3, 12));
            Environment.SetEnvironmentVariable(integerName, "500");
            Assert.AreEqual(12, ReadIntegerSetting(integerName, 7, 3, 12));

            Assert.AreEqual(70L, ReadLongSetting(longName, 70L, 30L, 120L));
            Environment.SetEnvironmentVariable(longName, "bad");
            Assert.AreEqual(70L, ReadLongSetting(longName, 70L, 30L, 120L));
            Environment.SetEnvironmentVariable(longName, "-50");
            Assert.AreEqual(30L, ReadLongSetting(longName, 70L, 30L, 120L));
            Environment.SetEnvironmentVariable(longName, "500");
            Assert.AreEqual(120L, ReadLongSetting(longName, 70L, 30L, 120L));
        }
        finally
        {
            Environment.SetEnvironmentVariable(integerName, null);
            Environment.SetEnvironmentVariable(longName, null);
        }
    }

    /// <summary>
    /// Verifies the weather Wetness Handles Physical Samples And Invalid Inputs regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WeatherWetnessHandlesPhysicalSamplesAndInvalidInputs()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;

        Assert.AreEqual(0.0f, FilmicDisplayRenderer.ComputeRainWetnessTarget(float.NaN, float.PositiveInfinity));
        Assert.AreEqual(0.0f, FilmicDisplayRenderer.ComputeRainWetnessTarget(-1.0f, 1.0f));
        Assert.AreEqual(1.0f, FilmicDisplayRenderer.ComputeRainWetnessTarget(2.0f, 2.0f));
        Assert.IsTrue(FilmicDisplayRenderer.ComputeRainWetnessTarget(0.5f, 1.0f) > 0.4f);

        Assert.AreEqual(0.4f, FilmicDisplayRenderer.AdvanceRainWetness(0.4f, 0.8f, float.NaN));
        Assert.IsTrue(FilmicDisplayRenderer.AdvanceRainWetness(-1.0f, 2.0f, 9.0f) > 0.0f);
        Assert.IsTrue(FilmicDisplayRenderer.AdvanceRainWetness(1.0f, 0.0f, 0.25f) < 1.0f);
        Assert.IsFalse(FilmicDisplayRenderer.ShouldSnapDeterministicClearWeather(null, "1", "1", 0.0f));
        Assert.IsFalse(FilmicDisplayRenderer.ShouldSnapDeterministicClearWeather("run", "0", "1", 0.0f));
        Assert.IsFalse(FilmicDisplayRenderer.ShouldSnapDeterministicClearWeather("run", "1", "0", 0.0f));
        Assert.IsFalse(FilmicDisplayRenderer.ShouldSnapDeterministicClearWeather("run", "1", "1", 0.1f));
        Assert.IsTrue(FilmicDisplayRenderer.ShouldSnapDeterministicClearWeather("run", "1", "1", 0.0f));

        harness.Climate = new ClimateCondition { Rainfall = 0.8f, RainCloudOverlay = 0.9f };
        Invoke(renderer, "UpdateWeatherWetness", 0.1f);
        Assert.IsTrue(GetField<float>(renderer, "rainWetnessTarget") > 0.7f);
        Assert.IsTrue(GetField<float>(renderer, "smoothedRainWetness") > 0.0f);

        harness.Climate = new ClimateCondition
        {
            Rainfall = float.NaN,
            RainCloudOverlay = float.PositiveInfinity
        };
        SetField(renderer, "weatherSampleAccumulator", 0.25f);
        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "coverage");
            Invoke(renderer, "UpdateWeatherWetness", float.NaN);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previous);
        }
        Assert.AreEqual(0.0f, GetField<float>(renderer, "rainWetnessTarget"));
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Contains("Weather wetness", StringComparison.Ordinal)));

        SetField(renderer, "smoothedRainWetness", 0.8f);
        SetField(renderer, "rainWetnessTarget", 0.0f);
        SetField(renderer, "weatherSampleAccumulator", 0.0f);
        using (EnvironmentScope clearReference = new(new Dictionary<string, string?>
        {
            ["VINTAGERTX_TEST_RUN_ID"] = "coverage-clear",
            ["VINTAGERTX_TEST_CLEAR_WEATHER"] = "1",
            ["VINTAGERTX_TEST_ENVIRONMENT_READY"] = "1"
        }))
        {
            Invoke(renderer, "UpdateWeatherWetness", 0.0f);
        }
        Assert.AreEqual(0.0f, GetField<float>(renderer, "smoothedRainWetness"));

        int weatherLogCount = harness.Logs.Count(static entry =>
            entry.Contains("Weather wetness", StringComparison.Ordinal));
        SetField(renderer, "benchmarkPhase", BenchmarkPhase.Effect);
        SetField(renderer, "renderedFrameCount", 0L);
        using (EnvironmentScope scope = new(new Dictionary<string, string?>
        {
            ["VINTAGERTX_TEST_RUN_ID"] = "coverage"
        }))
        {
            Invoke(renderer, "UpdateWeatherWetness", 0.0f);
        }
        Assert.AreEqual(
            weatherLogCount,
            harness.Logs.Count(static entry => entry.Contains("Weather wetness", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the camera Stability Tracks Short Matrices Motion And Diagnostic Windows regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CameraStabilityTracksShortMatricesMotionAndDiagnosticWindows()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;

        harness.CameraMatrix = [1.0, 2.0];
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateBenchmarkCameraStability"));
        harness.CameraMatrix = IdentityMatrix();
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateBenchmarkCameraStability"));
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateBenchmarkCameraStability"));

        harness.Entity.Pos.X += 1.0;
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateBenchmarkCameraStability"));
        SetField(renderer, "benchmarkMatrixDiagnosticFrames", 299);
        harness.CameraMatrix[4] += 0.01;
        _ = Invoke<bool>(renderer, "UpdateBenchmarkCameraStability");
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Contains("Benchmark camera diagnostic", StringComparison.Ordinal)));

        int diagnosticLogCount = harness.Logs.Count(static entry =>
            entry.Contains("Benchmark camera diagnostic", StringComparison.Ordinal));
        SetField(renderer, "benchmarkPhase", BenchmarkPhase.Effect);
        SetField(renderer, "benchmarkMatrixDiagnosticFrames", 299);
        _ = Invoke<bool>(renderer, "UpdateBenchmarkCameraStability");
        Assert.AreEqual(
            diagnosticLogCount,
            harness.Logs.Count(static entry => entry.Contains("Benchmark camera diagnostic", StringComparison.Ordinal)));

        harness.CameraMatrix = [1.0];
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        harness.CameraMatrix = IdentityMatrix();
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        SetField(renderer, "temporalHistoryValid", true);
        SetField(renderer, "temporalMotionResetLogCooldownFrames", 0);
        harness.CameraMatrix[0] += 0.01;
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        Assert.AreEqual(3600, GetField<int>(renderer, "temporalMotionResetLogCooldownFrames"));
        _ = Invoke<bool>(renderer, "UpdateTemporalCameraStability");
        Assert.AreEqual(3599, GetField<int>(renderer, "temporalMotionResetLogCooldownFrames"));
    }

    /// <summary>
    /// Verifies the automatic Benchmark Visits Every Phase And Completes Aba Report regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AutomaticBenchmarkVisitsEveryPhaseAndCompletesAbaReport()
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_BENCHMARK"] = "1",
            ["VINTAGERTX_BENCHMARK_WARMUP_FRAMES"] = "30",
            ["VINTAGERTX_BENCHMARK_SAMPLE_FRAMES"] = "180",
            ["VINTAGERTX_BENCHMARK_WORLD_WARMUP_MS"] = "5000"
        };
        using EnvironmentScope scope = new(settings);
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;

        Assert.IsFalse(Invoke<bool>(renderer, "UpdateAutomaticBenchmark"));
        SetField(harness.Scene, "generation", 1);
        harness.InWorldMilliseconds = 5_000;
        harness.CameraMatrix = IdentityMatrix();
        SetField(renderer, "benchmarkCameraInitialized", true);
        Array.Copy(harness.CameraMatrix, GetField<double[]>(renderer, "benchmarkCameraMatrix"), 16);
        SetField(renderer, "benchmarkCameraX", harness.Entity.Pos.X);
        SetField(renderer, "benchmarkCameraY", harness.Entity.Pos.Y);
        SetField(renderer, "benchmarkCameraZ", harness.Entity.Pos.Z);
        SetField(renderer, "benchmarkStableFrames", 30);
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateAutomaticBenchmark"));

        VisitBenchmarkPhase(renderer, BenchmarkPhase.BaselineFirst, 179, expectedSkip: true);
        VisitBenchmarkPhase(renderer, BenchmarkPhase.EffectWarmup, 29, expectedSkip: false);
        VisitBenchmarkPhase(renderer, BenchmarkPhase.Effect, 179, expectedSkip: false);
        VisitBenchmarkPhase(renderer, BenchmarkPhase.BaselineSecondWarmup, 29, expectedSkip: true);

        SetField(renderer, "benchmarkBaselineFirst", new PerformanceSnapshot(10, 120, 90, 1.0, 2.0));
        SetField(renderer, "benchmarkEffect", new PerformanceSnapshot(10, 100, 75, 1.5, 3.0));
        VisitBenchmarkPhase(renderer, BenchmarkPhase.BaselineSecond, 179, expectedSkip: true);
        Assert.AreEqual(BenchmarkPhase.Complete, GetField<BenchmarkPhase>(renderer, "benchmarkPhase"));
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateAutomaticBenchmark"));
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Contains(
            "profile={3}, effective-tier={4}",
            StringComparison.Ordinal)));
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Contains("Stabilized A/B/A result", StringComparison.Ordinal)));

        Invoke(renderer, "BeginBenchmarkPhase", BenchmarkPhase.Effect, "manual phase");
        Assert.AreEqual("manual phase", GetField<string>(renderer, "benchmarkStatus"));
    }

    /// <summary>
    /// Verifies the adaptive Quality Covers Fixed Capture Cooldown Downgrade And Upgrade regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AdaptiveQualityCoversFixedCaptureCooldownDowngradeAndUpgrade()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        VintageRtxConfig config = harness.Config;

        config.AdaptiveQualityEnabled = false;
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual("high (fixed)", GetField<string>(renderer, "adaptiveQualityStatus"));

        config.AdaptiveQualityEnabled = true;
        Invoke(renderer, "UpdateAdaptiveQuality", config, true);
        StringAssert.Contains(GetField<string>(renderer, "adaptiveQualityStatus"), "capture excluded");
        SetField(renderer, "adaptiveCaptureCooldownFrames", 0);
        SetField(renderer, "adaptiveTransitionCooldownFrames", 2);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        StringAssert.Contains(GetField<string>(renderer, "adaptiveQualityStatus"), "transition settling");

        SetField(renderer, "adaptiveTransitionCooldownFrames", 0);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual("high", GetField<string>(renderer, "adaptiveQualityStatus"));

        RenderPerformanceMonitor monitor = GetField<RenderPerformanceMonitor>(renderer, "performanceMonitor");
        SetField(monitor, "smoothedGpuMilliseconds", 4.0);
        SetField(renderer, "adaptiveOverBudgetFrames", 119);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(2, GetField<int>(renderer, "adaptiveQualityLevel"));

        SetField(renderer, "adaptiveTransitionCooldownFrames", 0);
        SetField(monitor, "smoothedGpuMilliseconds", 0.4);
        SetField(renderer, "adaptiveUnderBudgetFrames", 7199);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(1, GetField<int>(renderer, "adaptiveQualityLevel"));

        SetField(renderer, "adaptiveTransitionCooldownFrames", 0);
        SetField(monitor, "smoothedGpuMilliseconds", config.GpuBudgetMilliseconds * 0.8);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(0, GetField<int>(renderer, "adaptiveOverBudgetFrames"));

        Assert.AreEqual("high", InvokeStatic<string>("QualityLevelName", [typeof(int)], 0));
        Assert.AreEqual("balanced", InvokeStatic<string>("QualityLevelName", [typeof(int)], 1));
        Assert.AreEqual("performance", InvokeStatic<string>("QualityLevelName", [typeof(int)], 2));
        Assert.AreEqual(
            2,
            FilmicDisplayRenderer.AdaptiveDowngradeTarget(0, 12.0, 8.5),
            "A severe initial overload must settle in one transition.");
        Assert.AreEqual(
            1,
            FilmicDisplayRenderer.AdaptiveDowngradeTarget(0, 9.0, 8.5),
            "Mild pressure must retain one-tier hysteresis.");
        Assert.AreEqual(2, FilmicDisplayRenderer.AdaptiveDowngradeTarget(1, 20.0, 8.5));
        Assert.AreEqual(1, FilmicDisplayRenderer.AdaptiveDowngradeTarget(-4, 4.0, 8.5));
        Assert.AreEqual(2, FilmicDisplayRenderer.AdaptiveDowngradeTarget(7, 4.0, 8.5));
        Assert.AreEqual(1, FilmicDisplayRenderer.AdaptiveDowngradeTarget(0, 4.0, 0.0));

        Type[] denseSignature = [typeof(int), typeof(int), typeof(VoxelLight[])];
        Assert.IsFalse(InvokeStatic<bool>(
            "IsDenseMultiLightCluster", denseSignature, 1, 2, new VoxelLight[2]));
        Assert.IsTrue(InvokeStatic<bool>(
            "IsDenseMultiLightCluster", denseSignature, 2, 2, null));
        Assert.IsFalse(InvokeStatic<bool>(
            "IsDenseMultiLightCluster", denseSignature, 2, 1, null));
        Assert.IsFalse(InvokeStatic<bool>(
            "IsDenseMultiLightCluster", denseSignature, 2, 1, new VoxelLight[1]));
        Assert.IsTrue(InvokeStatic<bool>(
            "IsDenseMultiLightCluster", denseSignature, 2, 1, new VoxelLight[2]));
    }

    /// <summary>
    /// Verifies the light Selection Energy And Packing Cover Boundary Contracts regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LightSelectionEnergyAndPackingCoverBoundaryContracts()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            FilmicDisplayRenderer.SelectStableLightCandidates([1], [1.0f, 2.0f], [], new int[1]));
        Assert.ThrowsException<ArgumentException>(() =>
            FilmicDisplayRenderer.SelectStableLightCandidates(new int[65], new float[65], [], new int[1]));
        Assert.ThrowsException<ArgumentException>(() =>
            FilmicDisplayRenderer.SelectStableLightCandidates([1], [1.0f], [], new int[9]));

        int[] selected = new int[3];
        int count = FilmicDisplayRenderer.SelectStableLightCandidates(
            [8, 4, 6],
            [1.0f, 1.0f, 0.5f],
            [8],
            selected);
        Assert.AreEqual(3, count);
        Assert.AreEqual(0, selected[0]);
        Assert.AreEqual(1, selected[1]);

        Assert.ThrowsException<ArgumentException>(() =>
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy([1.0f], []));
        FilmicDisplayRenderer.IndependentLightEnergy empty =
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy([-1.0f], [2.0f]);
        Assert.AreEqual(0.0f, empty.Potential);
        Assert.AreEqual(1.0f, empty.Visibility);
        FilmicDisplayRenderer.IndependentLightEnergy energy =
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy([2.0f, 3.0f], [-1.0f, 2.0f]);
        Assert.AreEqual(5.0f, energy.Potential);
        Assert.AreEqual(3.0f, energy.Direct);
        Assert.AreEqual(2.0f, energy.Blocked);

        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        VoxelLight detailed = new(1, 2, 3, 0.2f, 0.4f, 0.6f, 2.0f, "lamp", new byte[VoxelScene.LightCasterVoxelCount]);
        VoxelLight simple = detailed with { X = 20, CasterMask = [] };
        Invoke(renderer, "WriteStaticLight", 0, 3, detailed, 12.0f, 4.0f);
        Invoke(renderer, "WriteStaticLight", 1, 4, simple, 8.0f, 2.0f);
        Assert.AreEqual(3.0f, GetField<float[]>(renderer, "voxelLightCasterLayers")[0]);
        Assert.AreEqual(-1.0f, GetField<float[]>(renderer, "voxelLightCasterLayers")[1]);
        Assert.AreEqual(-1, Invoke<int>(renderer, "FindDuplicateDynamicLight", detailed, 2));
        Assert.AreEqual(1, Invoke<int>(renderer, "FindDuplicateDynamicLight", simple, 2));
        Assert.AreEqual(-1, Invoke<int>(renderer, "FindDuplicateDynamicLight", simple with { X = 100 }, 2));
        Assert.AreEqual(1, Invoke<int>(renderer, "FindWeakestSelectedLight", 2));
        Assert.AreEqual(-1, Invoke<int>(renderer, "FindWeakestSelectedLight", 0));
        Assert.IsTrue(InvokeStatic<float>(
            "StaticLightSelectionScore",
            [typeof(VoxelLight), typeof(Vec3d), typeof(float)],
            detailed,
            new Vec3d(0, 0, 0),
            12.0f) > 0.0f);

        double[] inverse = GetField<double[]>(renderer, "inverseViewMatrixDouble");
        Array.Copy(IdentityMatrix(), inverse, 16);
        object?[] transformArguments = [1.0f, 2.0f, 3.0f, 10.0, 20.0, 30.0, 0.0f, 0.0f, 0.0f];
        GetMethod("TransformViewToWorld", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(renderer, transformArguments);
        Assert.AreEqual(11.0f, (float)transformArguments[6]!);
        Assert.AreEqual(22.0f, (float)transformArguments[7]!);
        Assert.AreEqual(33.0f, (float)transformArguments[8]!);
    }

    /// <summary>
    /// Verifies the matrices Dynamic Lights Sun And Disabled Voxel Binding Are Deterministic regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void MatricesDynamicLightsSunAndDisabledVoxelBindingAreDeterministic()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        IShaderProgram shader = RuntimeCoverageDispatchProxy.Create<IShaderProgram>((method, _) => method.Name switch
        {
            "get_Disposed" => false,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        SetField(renderer, "shader", shader);

        harness.ProjectionMatrix = [1.0, 2.0];
        harness.CameraMatrix = IdentityMatrix();
        TargetInvocationException projectionFailure = Assert.ThrowsException<TargetInvocationException>(
            () => Invoke(renderer, "CopyCameraMatrices"));
        Assert.IsInstanceOfType<InvalidOperationException>(projectionFailure.InnerException);
        harness.ProjectionMatrix = IdentityMatrix();
        harness.CameraMatrix = [1.0, 2.0];
        TargetInvocationException cameraFailure = Assert.ThrowsException<TargetInvocationException>(
            () => Invoke(renderer, "CopyCameraMatrices"));
        Assert.IsInstanceOfType<InvalidOperationException>(cameraFailure.InnerException);
        harness.CameraMatrix = IdentityMatrix();
        Invoke(renderer, "CopyCameraMatrices");
        Assert.AreEqual(1.0f, GetField<float[]>(renderer, "projectionMatrix")[0]);

        harness.Uniforms.PointLightsCount = 4;
        harness.Uniforms.PointLights3 =
        [
            1.0f, 2.0f, 3.0f,
            2.0f, 0.0f, 0.0f,
            3.0f, 0.0f, 0.0f,
            4.0f, 0.0f, 0.0f
        ];
        harness.Uniforms.PointLightColors3 =
        [
            3.0f, 4.0f, 0.0f,
            0.0f, 0.0f, 0.0f,
            float.NaN, 1.0f, 1.0f,
            1.0f, 1.0f, 1.0f
        ];
        string? previousRun = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "coverage");
            int first = Invoke<int>(renderer, "CopyDynamicPointLights", harness.Config, 10.0, 20.0, 30.0, 2);
            int second = Invoke<int>(renderer, "CopyDynamicPointLights", harness.Config, 10.0, 20.0, 30.0, 2);
            Assert.AreEqual(2, first);
            Assert.AreEqual(2, second);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previousRun);
        }
        Assert.AreEqual(2, GetField<int>(renderer, "availableDynamicLightCount"),
                "The zero-energy and non-finite entries are not available emitter candidates.");

        SetField(renderer, "voxelTextureReady", false);
        SetField(renderer, "gBufferAvailable", false);
        Invoke(renderer, "BindVoxelUniforms", harness.Config, 4);
        Assert.AreEqual(0, GetField<int>(renderer, "currentDynamicLightCount"));

        harness.SunDirection = new Vec3f(0, 0, 0);
        Invoke(renderer, "BindSunUniforms", harness.Config, false, new Vec3d(10.5, 81.6, 20.5));
        harness.SunDirection = new Vec3f(2, 3, 4);
        Invoke(renderer, "BindSunUniforms", harness.Config, true, new Vec3d(10.5, 81.6, 20.5));
        harness.CalendarAvailable = false;
        harness.Uniforms.SunPosition3D = new Vec3f(0, 1, 0);
        Invoke(renderer, "BindSunUniforms", harness.Config, true, new Vec3d(10.5, 81.6, 20.5));

        Invoke(renderer, "UpdateVoxelTexture");
        SetField(renderer, "voxelTextureReady", true);
        Invoke(renderer, "UpdateVoxelBlockTextures");
    }

    /// <summary>
    /// Verifies the coordinator Short Circuits Fault Height And Benchmark Baseline Without Touching Gl regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CoordinatorShortCircuitsFaultHeightAndBenchmarkBaselineWithoutTouchingGl()
    {
        using (RendererHarness harness = new())
        {
            FilmicDisplayRenderer renderer = harness.Renderer;
            SetField(renderer, "initialized", true);
            SetField(renderer, "textureWidth", 640);
            SetField(renderer, "textureHeight", 360);
            SetField(renderer, "temporalHistoryValid", true);
            StringAssert.Contains(renderer.Status, "temporal=accumulated");

            SetField(renderer, "faulted", true);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsTrue(GetField<bool>(renderer, "faulted"));
            renderer.ResetFault();

            harness.FrameWidth = 640;
            harness.FrameHeight = 0;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsFalse(GetField<bool>(renderer, "faulted"));
        }

        using EnvironmentScope scope = new(new Dictionary<string, string?>
        {
            ["VINTAGERTX_AUTO_BENCHMARK"] = "1"
        });
        using RendererHarness benchmark = new();
        FilmicDisplayRenderer benchmarkRenderer = benchmark.Renderer;
        SetField(benchmarkRenderer, "benchmarkPhase", BenchmarkPhase.BaselineFirst);
        SetField(benchmarkRenderer, "benchmarkCameraInitialized", true);
        Array.Copy(benchmark.CameraMatrix, GetField<double[]>(benchmarkRenderer, "benchmarkCameraMatrix"), 16);
        SetField(benchmarkRenderer, "benchmarkCameraX", benchmark.Entity.Pos.X);
        SetField(benchmarkRenderer, "benchmarkCameraY", benchmark.Entity.Pos.Y);
        SetField(benchmarkRenderer, "benchmarkCameraZ", benchmark.Entity.Pos.Z);
        benchmarkRenderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
        Assert.AreEqual(1, GetField<int>(benchmarkRenderer, "benchmarkPhaseFrame"));
        Assert.IsFalse(GetField<bool>(benchmarkRenderer, "faulted"));
    }

    /// <summary>
    /// Verifies the weather And Temporal Short Circuit Matrices Cover Missing Entity And Cooldown Motion regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WeatherAndTemporalShortCircuitMatricesCoverMissingEntityAndCooldownMotion()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;

        harness.PlayerAvailable = false;
        SetField(renderer, "weatherSampleAccumulator", 0.25f);
        Invoke(renderer, "UpdateWeatherWetness", 0.0f);
        Assert.AreEqual(0.25f, GetField<float>(renderer, "weatherSampleAccumulator"));
        harness.PlayerAvailable = true;

        harness.PlayerEntityAvailable = false;
        SetField(renderer, "weatherSampleAccumulator", 0.25f);
        Invoke(renderer, "UpdateWeatherWetness", 0.0f);
        Assert.AreEqual(0.25f, GetField<float>(renderer, "weatherSampleAccumulator"));
        harness.PlayerEntityAvailable = true;

        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "coverage-nonperiodic");
            SetField(renderer, "renderedFrameCount", 1L);
            Invoke(renderer, "UpdateWeatherWetness", 0.0f);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previous);
        }

        harness.CameraMatrix = IdentityMatrix();
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        SetField(renderer, "temporalHistoryValid", true);
        SetField(renderer, "temporalMotionResetLogCooldownFrames", 10);
        harness.Entity.CameraPos.X += 1.0;
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        harness.Entity.Pos.X += 1.0;
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));
        Assert.AreEqual(8, GetField<int>(renderer, "temporalMotionResetLogCooldownFrames"));
        Assert.IsFalse(GetField<bool>(renderer, "temporalHistoryValid"));

        SetField(renderer, "automatedCameraLock", true);
        harness.Entity.Pos.X += 1.0;
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateTemporalCameraStability"));

        harness.MousePitch = 1.0f;
        SetField(renderer, "benchmarkCameraInitialized", true);
        Array.Copy(harness.CameraMatrix, GetField<double[]>(renderer, "benchmarkCameraMatrix"), 16);
        SetField(renderer, "benchmarkCameraX", harness.Entity.Pos.X);
        SetField(renderer, "benchmarkCameraY", harness.Entity.Pos.Y);
        SetField(renderer, "benchmarkCameraZ", harness.Entity.Pos.Z);
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateBenchmarkCameraStability"));
        Assert.AreEqual(1.0, GetField<double>(renderer, "benchmarkPitchDiagnosticMaximumDelta"), 0.0001);
    }

    /// <summary>Verifies the full-rate mirror carrier uses only the Performance resolution reduction.</summary>
    [TestMethod]
    public void EntityMirrorResolutionPreservesHigherQualityAndBoundsPerformance()
    {
        Assert.AreEqual(2, FilmicDisplayRenderer.MirrorResolutionDivisor(0));
        Assert.AreEqual(2, FilmicDisplayRenderer.MirrorResolutionDivisor(1));
        Assert.AreEqual(4, FilmicDisplayRenderer.MirrorResolutionDivisor(2));
    }

    /// <summary>Verifies shadow refreshes alternate opposite the stable Performance mirror carrier.</summary>
    [TestMethod]
    public void ShadowVisibilityRefreshCadencePreservesMotionCaptureAndHistoryOwnership()
    {
        Assert.IsTrue(FilmicDisplayRenderer.ShouldRefreshShadowVisibility(0, true, false, true, 2L));
        Assert.IsTrue(FilmicDisplayRenderer.ShouldRefreshShadowVisibility(1, true, false, true, 2L));
        Assert.IsTrue(FilmicDisplayRenderer.ShouldRefreshShadowVisibility(2, false, false, true, 2L));
        Assert.IsTrue(FilmicDisplayRenderer.ShouldRefreshShadowVisibility(2, true, true, true, 2L));
        Assert.IsTrue(FilmicDisplayRenderer.ShouldRefreshShadowVisibility(2, true, false, false, 2L));
        Assert.IsTrue(FilmicDisplayRenderer.ShouldRefreshShadowVisibility(2, true, false, true, 1L));
        Assert.IsFalse(FilmicDisplayRenderer.ShouldRefreshShadowVisibility(2, true, false, true, 2L));
    }

    /// <summary>
    /// Verifies the benchmark Waiting And All Phase Thresholds Exercise Both Sides regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void BenchmarkWaitingAndAllPhaseThresholdsExerciseBothSides()
    {
        using EnvironmentScope scope = new(new Dictionary<string, string?>
        {
            ["VINTAGERTX_AUTO_BENCHMARK"] = "1",
            ["VINTAGERTX_BENCHMARK_WARMUP_FRAMES"] = "30",
            ["VINTAGERTX_BENCHMARK_SAMPLE_FRAMES"] = "180",
            ["VINTAGERTX_BENCHMARK_WORLD_WARMUP_MS"] = "5000"
        });

        using (RendererHarness sceneNotReady = new())
        {
            PrimeBenchmarkCamera(sceneNotReady);
            sceneNotReady.InWorldMilliseconds = 5_000;
            SetField(sceneNotReady.Renderer, "benchmarkStableFrames", 30);
            Assert.IsFalse(Invoke<bool>(sceneNotReady.Renderer, "UpdateAutomaticBenchmark"));
        }

        using (EnvironmentScope captureProfile = new(new Dictionary<string, string?>
        {
            ["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = "render-lab",
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "180"
        }))
        using (RendererHarness captureNotReady = new())
        {
            PrimeBenchmarkCamera(captureNotReady);
            captureNotReady.InWorldMilliseconds = 5_000;
            SetField(captureNotReady.Scene, "generation", 1);
            SetField(captureNotReady.Renderer, "benchmarkStableFrames", 30);
            Assert.IsFalse(Invoke<bool>(captureNotReady.Renderer, "UpdateAutomaticBenchmark"));
        }

        using RendererHarness thresholds = new();
        PrimeBenchmarkCamera(thresholds);
        thresholds.InWorldMilliseconds = 5_000;
        SetField(thresholds.Scene, "generation", 1);
        SetField(thresholds.Renderer, "benchmarkStableFrames", 0);
        Assert.IsFalse(Invoke<bool>(thresholds.Renderer, "UpdateAutomaticBenchmark"));

        foreach ((BenchmarkPhase Phase, bool Skip) in new[]
        {
            (BenchmarkPhase.BaselineFirst, true),
            (BenchmarkPhase.EffectWarmup, false),
            (BenchmarkPhase.Effect, false),
            (BenchmarkPhase.BaselineSecondWarmup, true),
            (BenchmarkPhase.BaselineSecond, true)
        })
        {
            SetField(thresholds.Renderer, "benchmarkPhase", Phase);
            SetField(thresholds.Renderer, "benchmarkPhaseFrame", 0);
            Assert.AreEqual(Skip, Invoke<bool>(thresholds.Renderer, "UpdateAutomaticBenchmark"), Phase.ToString());
            Assert.AreEqual(1, GetField<int>(thresholds.Renderer, "benchmarkPhaseFrame"));
        }
    }

    /// <summary>
    /// Verifies the adaptive Quality Boundary Tiers Cannot Move Past Performance Or High regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AdaptiveQualityBoundaryTiersCannotMovePastPerformanceOrHigh()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        VintageRtxConfig config = harness.Config;
        config.AdaptiveQualityEnabled = true;
        RenderPerformanceMonitor monitor = GetField<RenderPerformanceMonitor>(renderer, "performanceMonitor");
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);

        SetField(renderer, "adaptiveQualityLevel", 2);
        SetField(renderer, "adaptiveOverBudgetFrames", 119);
        SetField(renderer, "adaptiveUnderBudgetFrames", 0);
        SetField(monitor, "smoothedGpuMilliseconds", config.GpuBudgetMilliseconds * 2.0);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(2, GetField<int>(renderer, "adaptiveQualityLevel"));

        SetField(renderer, "adaptiveQualityLevel", 0);
        SetField(renderer, "adaptiveOverBudgetFrames", 0);
        SetField(renderer, "adaptiveUnderBudgetFrames", 7199);
        SetField(monitor, "smoothedGpuMilliseconds", config.GpuBudgetMilliseconds * 0.25);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(0, GetField<int>(renderer, "adaptiveQualityLevel"));

        SetField(renderer, "adaptiveOverBudgetFrames", 0);
        SetField(renderer, "adaptiveUnderBudgetFrames", 0);
        SetField(monitor, "smoothedGpuMilliseconds", config.GpuBudgetMilliseconds * 2.0);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(1, GetField<int>(renderer, "adaptiveOverBudgetFrames"));
    }

    /// <summary>
    /// Verifies real stone and arrow callbacks queue one prompt surface-field/final pair each,
    /// retain work while the event slot is busy, and ignore later bounces of the same stone.
    /// </summary>
    [TestMethod]
    public void ProjectileImpactsQueueFinalAndSurfaceFieldEvidenceOncePerKind()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        FrameCaptureService captureService = GetField<FrameCaptureService>(renderer, "captureService");
        LiquidSurfaceRuntime runtime = GetField<LiquidSurfaceRuntime>(renderer, "liquidSurfaceRuntime");
        LiquidSurfaceSimulation simulation = CreateProjectileCaptureSimulation();
        SetField(runtime, "simulation", simulation);
        LiquidProjectileCollisionSample stone = new(
            EntityId: 8_001,
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
        simulation.ObserveProjectileLiquidCollisions([stone]);

        Invoke(renderer, "QueueProjectileImpactEvidence", 0.10f);
        Assert.IsFalse(captureService.TryGetCapture(1, out _), "Non-test gameplay must not queue evidence.");

        using EnvironmentScope scope = new(new Dictionary<string, string?>
        {
            ["VINTAGERTX_TEST_SCENARIO"] = "water-reflection"
        });
        Assert.IsTrue(captureService.QueueCapture(
            new FrameCaptureRequest("busy", VintageRtxDebugView.Final)));
        for (int frame = 0; frame < 2; frame++)
        {
            Invoke(renderer, "QueueProjectileImpactEvidence", 0.10f);
        }
        Assert.IsTrue(captureService.TryGetCapture(2, out FrameCaptureRequest busy));
        Assert.AreEqual("busy", busy.Label);
        Invoke(renderer, "QueueProjectileImpactEvidence", 0.10f);
        Assert.IsTrue(captureService.TryGetCapture(3, out FrameCaptureRequest stoneField));
        Assert.AreEqual(
            new FrameCaptureRequest(
                "projectile-stone-surface-field",
                VintageRtxDebugView.LiquidSurfaceField),
            stoneField);
        Invoke(renderer, "QueueProjectileImpactEvidence", 0.10f);
        Assert.IsTrue(captureService.TryGetCapture(4, out FrameCaptureRequest stoneFinal));
        Assert.AreEqual(
            new FrameCaptureRequest("projectile-stone-final", VintageRtxDebugView.Final),
            stoneFinal);

        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds * 6.0f, default);
        simulation.ObserveProjectileLiquidCollisions([
            stone with
            {
                WorldX = 3.60,
                WorldZ = 2.75,
                PreviousWorldX = 3.20,
                PreviousWorldZ = 2.75
            }
        ]);
        Invoke(renderer, "QueueProjectileImpactEvidence", 0.50f);
        Assert.IsFalse(
            captureService.TryGetCapture(5, out _),
            "A later bounce of the same real stone must not replace the comparable first witness.");

        LiquidProjectileCollisionSample arrow = stone with
        {
            EntityId = 8_002,
            SurfaceClass = LiquidEntitySurfaceClass.Projectile,
            WorldX = 4.10,
            WorldZ = 3.25,
            PreviousWorldX = 3.90,
            PreviousWorldZ = 3.25,
            OutgoingMotionY = -0.05f,
            MassKilograms = LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms
        };
        simulation.ObserveProjectileLiquidCollisions([arrow]);
        for (int frame = 0; frame < 2; frame++)
        {
            Invoke(renderer, "QueueProjectileImpactEvidence", 0.10f);
        }
        Assert.IsTrue(captureService.TryGetCapture(6, out FrameCaptureRequest arrowField));
        Assert.AreEqual(
            new FrameCaptureRequest(
                "projectile-arrow-surface-field",
                VintageRtxDebugView.LiquidSurfaceField),
            arrowField);
        Invoke(renderer, "QueueProjectileImpactEvidence", 0.10f);
        Assert.IsTrue(captureService.TryGetCapture(7, out FrameCaptureRequest arrowFinal));
        Assert.AreEqual(
            new FrameCaptureRequest("projectile-arrow-final", VintageRtxDebugView.Final),
            arrowFinal);
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Contains(
            "Projectile-synchronized capture {0}",
            StringComparison.Ordinal)));
    }

    /// <summary>Verifies hardware profiles immediately reset and bound the adaptive tier.</summary>
    [TestMethod]
    public void HardwareProfilesApplyImmediateAdaptiveFloors()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        VintageRtxConfig config = harness.Config;
        RenderPerformanceMonitor monitor = GetField<RenderPerformanceMonitor>(renderer, "performanceMonitor");

        config.ApplyRenderProfile(VintageRtxRenderProfile.Performance);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(2, GetField<int>(renderer, "adaptiveQualityLevel"));
        SetField(renderer, "adaptiveUnderBudgetFrames", 7199);
        SetField(monitor, "smoothedGpuMilliseconds", 0.1);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(2, GetField<int>(renderer, "adaptiveQualityLevel"));

        config.ApplyRenderProfile(VintageRtxRenderProfile.Balanced);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(1, GetField<int>(renderer, "adaptiveQualityLevel"));
        SetField(renderer, "adaptiveOverBudgetFrames", 119);
        SetField(monitor, "smoothedGpuMilliseconds", config.GpuBudgetMilliseconds * 2.0);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(2, GetField<int>(renderer, "adaptiveQualityLevel"));
        SetField(renderer, "adaptiveTransitionCooldownFrames", 0);
        SetField(renderer, "adaptiveUnderBudgetFrames", 7199);
        SetField(monitor, "smoothedGpuMilliseconds", 0.1);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(1, GetField<int>(renderer, "adaptiveQualityLevel"));

        config.ApplyRenderProfile(VintageRtxRenderProfile.Quality);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(0, GetField<int>(renderer, "adaptiveQualityLevel"));

        config.ApplyRenderProfile(VintageRtxRenderProfile.Ultra);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(0, GetField<int>(renderer, "adaptiveQualityLevel"));
        Assert.AreEqual("high (fixed)", GetField<string>(renderer, "adaptiveQualityStatus"));
        Assert.AreEqual(4, harness.Logs.Count(static entry => entry.Contains(
            "Rendering profile {0} active: initial-tier={1}",
            StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the dynamic Light Null Buffers Primary Reorder And Stable Selection Are Deterministic regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void DynamicLightNullBuffersPrimaryReorderAndStableSelectionAreDeterministic()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        Array.Copy(IdentityMatrix(), GetField<double[]>(renderer, "inverseViewMatrixDouble"), 16);

        harness.Uniforms.PointLightsCount = 4;
        harness.Uniforms.PointLights3 = null!;
        harness.Uniforms.PointLightColors3 = null!;
        Assert.AreEqual(0, Invoke<int>(renderer, "CopyDynamicPointLights", harness.Config, 0.0, 0.0, 0.0, 2));

        harness.Uniforms.PointLightsCount = 2;
        harness.Uniforms.PointLights3 = [1, 0, 0, 2, 0, 0];
        harness.Uniforms.PointLightColors3 = [0, 0, 0, 3, 0, 0];
        Assert.AreEqual(1, Invoke<int>(renderer, "CopyDynamicPointLights", harness.Config, 0.0, 0.0, 0.0, 2));
        Assert.AreEqual(1, GetField<int>(renderer, "lastPrimaryDynamicLightSourceIndex"));

        harness.Uniforms.PointLightColors3 = [3, 0, 0, 0, 0, 0];
        Assert.AreEqual(1, Invoke<int>(renderer, "CopyDynamicPointLights", harness.Config, 0.0, 0.0, 0.0, 2));
        Assert.AreEqual(0, GetField<int>(renderer, "lastPrimaryDynamicLightSourceIndex"));
        Assert.AreEqual(1, Invoke<int>(renderer, "CopyDynamicPointLights", harness.Config, 0.0, 0.0, 0.0, 2));

        float[] scores = GetField<float[]>(renderer, "voxelLightSelectionScores");
        scores[0] = 1.0f;
        scores[1] = 2.0f;
        Assert.AreEqual(0, Invoke<int>(renderer, "FindWeakestSelectedLight", 2));

        int[] selected = new int[3];
        Assert.AreEqual(0, FilmicDisplayRenderer.SelectStableLightCandidates(
            [7], [float.NaN], [], selected.AsSpan(0, 1)));
        Assert.AreEqual(3, FilmicDisplayRenderer.SelectStableLightCandidates(
            [9, 4, 7], [1.0f, 1.0f, 1.0f], [4, 99, 4], selected));
        CollectionAssert.AreEqual(new[] { 1, 2, 0 }, selected);
    }

    /// <summary>
    /// Verifies the sun Fallbacks And Voxel Enable Short Circuits Cover Every Cpu Combination regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void SunFallbacksAndVoxelEnableShortCircuitsCoverEveryCpuCombination()
    {
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        IShaderProgram shader = RuntimeCoverageDispatchProxy.Create<IShaderProgram>((method, _) =>
            method.Name == "get_Disposed"
                ? false
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        SetField(renderer, "shader", shader);

        harness.Config.SunShadowsEnabled = false;
        harness.SunDirection = new Vec3f(1, 2, 3);
        Invoke(renderer, "BindSunUniforms", harness.Config, true, new Vec3d(10.5, 81.6, 20.5));

        harness.SunDirection = null!;
        harness.SunColor = null!;
        harness.Uniforms.SunPosition3D = new Vec3f(0, 0, 0);
        Invoke(renderer, "BindSunUniforms", harness.Config, true, new Vec3d(10.5, 81.6, 20.5));

        harness.Config.VoxelLightingEnabled = false;
        SetField(renderer, "voxelTextureReady", true);
        SetField(renderer, "gBufferAvailable", true);
        Invoke(renderer, "BindVoxelUniforms", harness.Config, 0);
        Assert.AreEqual(0, GetField<int>(renderer, "currentDynamicLightCount"));

        harness.Config.VoxelLightingEnabled = true;
        SetField(renderer, "gBufferAvailable", false);
        Invoke(renderer, "BindVoxelUniforms", harness.Config, 0);
        Assert.AreEqual(0, GetField<int>(renderer, "currentDynamicLightCount"));
    }

    /// <summary>
    /// Verifies the benchmark Movement Restart Locked Camera And Unknown Phase Are Covered regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void BenchmarkMovementRestartLockedCameraAndUnknownPhaseAreCovered()
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_BENCHMARK"] = "1",
            ["VINTAGERTX_TEST_CAMERA_LOCKED"] = "1"
        };
        using EnvironmentScope scope = new(settings);
        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        harness.CameraMatrix = IdentityMatrix();
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateBenchmarkCameraStability"));
        harness.Entity.Pos.X += 100;
        Assert.IsTrue(Invoke<bool>(renderer, "UpdateBenchmarkCameraStability"));

        SetField(renderer, "benchmarkPhase", BenchmarkPhase.Effect);
        SetField(renderer, "benchmarkCameraInitialized", false);
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateAutomaticBenchmark"));
        Assert.AreEqual(BenchmarkPhase.Waiting, GetField<BenchmarkPhase>(renderer, "benchmarkPhase"));

        SetField(renderer, "benchmarkPhase", (BenchmarkPhase)999);
        SetField(renderer, "benchmarkCameraInitialized", true);
        Array.Copy(harness.CameraMatrix, GetField<double[]>(renderer, "benchmarkCameraMatrix"), 16);
        SetField(renderer, "benchmarkCameraX", harness.Entity.Pos.X);
        SetField(renderer, "benchmarkCameraY", harness.Entity.Pos.Y);
        SetField(renderer, "benchmarkCameraZ", harness.Entity.Pos.Z);
        Assert.IsFalse(Invoke<bool>(renderer, "UpdateAutomaticBenchmark"));
    }

    /// <summary>Verifies packet arrays are packed contiguously for direct OpenGL vec4 upload.</summary>
    [TestMethod]
    public void SubgridImpactArraysUseDirectLocationsAndContiguousPacking()
    {
        LiquidSurfaceSubgridImpactDiagnostic packet =
            default(LiquidSurfaceSubgridImpactDiagnostic) with
        {
            PeakDisplacement = 0.001f,
            SplashPeakDisplacement = 0.032f,
            SplashRadiusWorldBlocks = 0.10f,
            SplashReleaseSeconds = 0.045f,
            SurfaceCoupledEnergyJoules = 2.70f,
            ResolvedWaveEnergyJoules = 0.0005f,
            SubgridWaveEnergyJoules = 2.6995f,
            RenderedPacketEnergyJoules = 0.00044f,
            LocalSplashEnergyJoules = 0.040f,
            DirectionXMetresPerSecond = 4.0f,
            DirectionZMetresPerSecond = -3.0f,
            DensityKilogramsPerCubicMetre = 998.2f,
            DynamicViscosityPascalSeconds = 0.001002f,
            SurfaceTensionNewtonsPerMetre = 0.07275f,
            AdditionalDampingPerSecond = 0.08f,
            DominantWavelengthMetres = 0.0184f
        };
        float[] origins = new float[FilmicDisplayRenderer.MaximumActiveSubgridImpactCount * 4];
        float[] materials = new float[origins.Length];
        float[] motions = new float[origins.Length];
        float[] energies = new float[origins.Length];
        float[] splashes = new float[origins.Length];

        FilmicDisplayRenderer.PackSubgridImpactUniform(
            in packet,
            12.5f,
            -7.25f,
            -1.0f,
            2,
            origins,
            materials,
            motions,
            energies,
            splashes);

        const int offset = 8;
        Assert.IsTrue(origins[..offset].All(static value => value == 0.0f));
        CollectionAssert.AreEqual(
            new[] { 12.5f, -7.25f, 0.0f, 0.001f },
            origins[offset..(offset + 4)]);
        CollectionAssert.AreEqual(
            new[] { 998.2f, 0.001002f, 0.07275f, 0.08f },
            materials[offset..(offset + 4)]);
        CollectionAssert.AreEqual(
            new[] { 4.0f, -3.0f, 0.0184f, 1.0f },
            motions[offset..(offset + 4)]);
        CollectionAssert.AreEqual(
            new[] { 2.70f, 0.0005f, 2.6995f, 0.00044f },
            energies[offset..(offset + 4)]);
        CollectionAssert.AreEqual(
            new[] { 0.032f, 0.10f, 0.045f, 0.040f },
            splashes[offset..(offset + 4)]);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            FilmicDisplayRenderer.PackSubgridImpactUniform(
                in packet,
                0.0f,
                0.0f,
                0.0f,
                FilmicDisplayRenderer.MaximumActiveSubgridImpactCount,
                origins,
                materials,
                motions,
                energies,
                splashes));

        string rendererSource = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "Rendering",
            "FilmicDisplayRenderer.cs"));
        StringAssert.Contains(rendererSource, "\"liquidImpactOriginAgeAmplitude[0]\"");
        StringAssert.Contains(
            rendererSource,
            "GL.Uniform4(liquidImpactOriginLocation, boundCount, liquidImpactOrigins)");
        Assert.IsFalse(rendererSource.Contains("ImpactOriginUniformNames", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies the shader Preflight And Cpu Validation Helpers Cover Every Outcome regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ShaderPreflightAndCpuValidationHelpersCoverEveryOutcome()
    {
        Assert.AreEqual("driver", FilmicDisplayRenderer.NormalizeGlLabel("driver", "fallback"));
        Assert.AreEqual("fallback", FilmicDisplayRenderer.NormalizeGlLabel(null, "fallback"));

        FilmicDisplayRenderer.ValidateVoxelUniformLocations();
        FilmicDisplayRenderer.ValidateVoxelUniformLocations(0, 1, 2);
        Assert.ThrowsException<InvalidOperationException>(() =>
            FilmicDisplayRenderer.ValidateVoxelUniformLocations(-1, 0, 0));
        Assert.ThrowsException<InvalidOperationException>(() =>
            FilmicDisplayRenderer.ValidateVoxelUniformLocations(0, 0, -1));
        FilmicDisplayRenderer.ValidateImpactUniformLocations();
        FilmicDisplayRenderer.ValidateImpactUniformLocations(0, 1, 2, 3, 4);
        Assert.ThrowsException<InvalidOperationException>(() =>
            FilmicDisplayRenderer.ValidateImpactUniformLocations(0, 1, -1, 3, 4));

        using RendererHarness harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        harness.ShaderVersionSupported = true;

        foreach ((int PassId, bool Compiled, bool LoadError, bool Disposed) in new[]
        {
            (-1, true, false, false),
            (1, false, false, false),
            (1, true, true, false),
            (1, true, false, true)
        })
        {
            harness.ShaderPassId = PassId;
            harness.ShaderCompileResult = Compiled;
            harness.ShaderLoadError = LoadError;
            harness.ShaderDisposed = Disposed;
            TargetInvocationException failure = Assert.ThrowsException<TargetInvocationException>(() =>
                Invoke(renderer, "CreateShader"));
            InvalidOperationException shaderFailure = failure.InnerException as InvalidOperationException
                ?? throw new AssertFailedException("CreateShader did not expose its expected validation failure.");
            StringAssert.Contains(shaderFailure.Message, DisplayShaderSource.VertexAssetCode);
        }

        float[] scores = GetField<float[]>(renderer, "voxelLightSelectionScores");
        scores[0] = 1.0f;
        scores[1] = 2.0f;

        int lightCount = 2;
        Assert.AreEqual(1, SelectStaticLightTarget(renderer, 1, 2, 100.0f, ref lightCount));
        Assert.AreEqual(2, lightCount);

        lightCount = 0;
        Assert.AreEqual(0, SelectStaticLightTarget(renderer, -1, 2, 1.0f, ref lightCount));
        Assert.AreEqual(1, lightCount);

        lightCount = 2;
        Assert.AreEqual(0, SelectStaticLightTarget(renderer, -1, 2, 2.0f, ref lightCount));
        Assert.AreEqual(2, lightCount);
        Assert.AreEqual(-1, SelectStaticLightTarget(renderer, -1, 2, 1.0f, ref lightCount));
        lightCount = 0;
        Assert.AreEqual(-1, SelectStaticLightTarget(renderer, -1, 0, 1.0f, ref lightCount));
    }

    /// <summary>
    /// Executes the visit Benchmark Phase step used by the deterministic filmic Display Renderer Logic Coverage Tests fixture.
    /// </summary>
    /// <param name="renderer">The renderer input used to configure this deterministic test path.</param>
    /// <param name="phase">The phase input used to configure this deterministic test path.</param>
    /// <param name="frame">The frame input used to configure this deterministic test path.</param>
    /// <param name="expectedSkip">Expected value enforced by the regression contract.</param>
    private static void VisitBenchmarkPhase(
        FilmicDisplayRenderer renderer,
        BenchmarkPhase phase,
        int frame,
        bool expectedSkip)
    {
        SetField(renderer, "benchmarkPhase", phase);
        SetField(renderer, "benchmarkPhaseFrame", frame);
        SetField(renderer, "benchmarkCameraInitialized", true);
        SetField(renderer, "benchmarkStableFrames", 30);
        Assert.AreEqual(expectedSkip, Invoke<bool>(renderer, "UpdateAutomaticBenchmark"), phase.ToString());
    }

    /// <summary>
    /// Executes the select Static Light Target step used by the deterministic filmic Display Renderer Logic Coverage Tests fixture.
    /// </summary>
    /// <param name="renderer">The renderer input used to configure this deterministic test path.</param>
    /// <param name="duplicateIndex">Coordinate component in the space defined by the tested API.</param>
    /// <param name="maximumVoxelLights">The maximum Voxel Lights input used to configure this deterministic test path.</param>
    /// <param name="staticScore">The static Score input used to configure this deterministic test path.</param>
    /// <param name="lightCount">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <returns>The select Static Light Target result consumed by the caller&apos;s assertion.</returns>
    private static int SelectStaticLightTarget(
        FilmicDisplayRenderer renderer,
        int duplicateIndex,
        int maximumVoxelLights,
        float staticScore,
        ref int lightCount)
    {
        object?[] arguments = [duplicateIndex, maximumVoxelLights, staticScore, lightCount];
        int selected = Invoke<int>(renderer, "SelectStaticLightTarget", arguments);
        lightCount = (int)arguments[3]!;
        return selected;
    }

    /// <summary>
    /// Executes the prime Benchmark Camera step used by the deterministic filmic Display Renderer Logic Coverage Tests fixture.
    /// </summary>
    /// <param name="harness">The harness input used to configure this deterministic test path.</param>
    private static void PrimeBenchmarkCamera(RendererHarness harness)
    {
        FilmicDisplayRenderer renderer = harness.Renderer;
        harness.CameraMatrix = IdentityMatrix();
        SetField(renderer, "benchmarkCameraInitialized", true);
        Array.Copy(harness.CameraMatrix, GetField<double[]>(renderer, "benchmarkCameraMatrix"), 16);
        SetField(renderer, "benchmarkCameraX", harness.Entity.Pos.X);
        SetField(renderer, "benchmarkCameraY", harness.Entity.Pos.Y);
        SetField(renderer, "benchmarkCameraZ", harness.Entity.Pos.Z);
    }

    /// <summary>Creates a connected water-like solver used by projectile capture scheduling tests.</summary>
    /// <returns>A deterministic 9x9 free surface centered around the authored collision samples.</returns>
    private static LiquidSurfaceSimulation CreateProjectileCaptureSimulation()
    {
        LiquidSurfaceSimulation simulation = new(
            originWorldX: 0,
            originWorldZ: 0,
            width: 9,
            depth: 9,
            cellSize: 0.5f,
            deterministicSeed: 0x76543210u);
        LiquidSurfaceDynamics dynamics = new(
            WindCoupling: 0.16f,
            WaveAmplitude: 0.20f,
            WaveLength: 2.0f,
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
        LiquidSurfacePhysicalProperties physics = new(
            DensityKilogramsPerCubicMetre: 998.2f,
            DynamicViscosityPascalSeconds: 0.001002f,
            SurfaceTensionNewtonsPerMetre: 0.07275f,
            AdditionalDampingPerSecond: 0.0f,
            ResolvedWaveEnergyFraction: 0.020f);
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

        return simulation;
    }

    /// <summary>
    /// Reads integer Setting from isolated test input and rejects malformed state at the fixture boundary.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="fallback">The fallback input used to configure this deterministic test path.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <returns>The read Integer Setting result consumed by the caller&apos;s assertion.</returns>
    private static int ReadIntegerSetting(string name, int fallback, int minimum, int maximum)
    {
        return InvokeStatic<int>(
            "ReadBenchmarkSetting",
            [typeof(string), typeof(int), typeof(int), typeof(int)],
            name,
            fallback,
            minimum,
            maximum);
    }

    /// <summary>
    /// Reads long Setting from isolated test input and rejects malformed state at the fixture boundary.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="fallback">The fallback input used to configure this deterministic test path.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <returns>The read Long Setting result consumed by the caller&apos;s assertion.</returns>
    private static long ReadLongSetting(string name, long fallback, long minimum, long maximum)
    {
        return InvokeStatic<long>(
            "ReadBenchmarkSetting",
            [typeof(string), typeof(long), typeof(long), typeof(long)],
            name,
            fallback,
            minimum,
            maximum);
    }

    /// <summary>
    /// Invokes static through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="parameterTypes">The parameter Types input used to configure this deterministic test path.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The invoke Static result consumed by the caller&apos;s assertion.</returns>
    private static T InvokeStatic<T>(string name, Type[] parameterTypes, params object?[] arguments)
    {
        MethodInfo method = typeof(FilmicDisplayRenderer).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null) ?? throw new MissingMethodException(name);
        return (T)method.Invoke(null, arguments)!;
    }

    /// <summary>
    /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    private static void Invoke(object instance, string name, params object?[] arguments)
    {
        _ = FindMethod(instance, name, arguments).Invoke(instance, arguments);
    }

    /// <summary>
    /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The invoke result consumed by the caller&apos;s assertion.</returns>
    private static T Invoke<T>(object instance, string name, params object?[] arguments)
    {
        return (T)FindMethod(instance, name, arguments).Invoke(instance, arguments)!;
    }

    /// <summary>
    /// Executes the find Method step used by the deterministic filmic Display Renderer Logic Coverage Tests fixture.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The find Method result consumed by the caller&apos;s assertion.</returns>
    private static MethodInfo FindMethod(object instance, string name, object?[] arguments)
    {
        MethodInfo[] candidates = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .ToArray();
        Assert.AreEqual(1, candidates.Length, name);
        return candidates[0];
    }

    /// <summary>
    /// Returns method from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="flags">The flags input used to configure this deterministic test path.</param>
    /// <returns>The get Method result consumed by the caller&apos;s assertion.</returns>
    private static MethodInfo GetMethod(string name, BindingFlags flags)
    {
        return typeof(FilmicDisplayRenderer).GetMethod(name, flags)
            ?? throw new MissingMethodException(name);
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

    /// <summary>
    /// Sets field on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    private static void SetField<T>(object instance, string fieldName, T value)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, value);
    }

    /// <summary>
    /// Executes the identity Matrix step used by the deterministic filmic Display Renderer Logic Coverage Tests fixture.
    /// </summary>
    /// <returns>The identity Matrix result consumed by the caller&apos;s assertion.</returns>
    private static double[] IdentityMatrix()
    {
        return
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];
    }

    /// <summary>Creates the single-precision identity used by native shadow-stage doubles.</summary>
    /// <returns>A complete column-major identity matrix.</returns>
    private static float[] IdentityMatrixFloat()
    {
        return
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];
    }

    /// <summary>
    /// Supports environment Scope within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new environment Scope fixture with the dependencies required for isolated execution.
        /// </summary>
        /// <param name="values">The values input used to configure this deterministic test path.</param>
        public EnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach ((string name, string? value) in values)
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        /// <summary>
        /// Releases resources owned by environment Scope; cleanup remains safe after partial fixture initialization.
        /// </summary>
        public void Dispose()
        {
            foreach ((string name, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>
    /// Coordinates the renderer harness while keeping filesystem, game, and graphics state isolated.
    /// </summary>
    private sealed class RendererHarness : IDisposable
    {
        private readonly string captureRoot = Path.Combine(
            Path.GetTempPath(),
            $"vintagertx-filmic-coverage-{Guid.NewGuid():N}");

        /// <summary>
        /// Initializes a new renderer Harness fixture with the dependencies required for isolated execution.
        /// </summary>
        public RendererHarness()
        {
            Directory.CreateDirectory(captureRoot);
            Entity = new EntityPlayer
            {
                CameraPos = new Vec3d(10.5, 81.6, 20.5)
            };
            Entity.Pos.SetPos(10, 80, 20);
            Climate = new ClimateCondition();
            CameraMatrix = IdentityMatrix();

            ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
            {
                if (method.Name is nameof(ILogger.Notification) or nameof(ILogger.Warning) or nameof(ILogger.Error))
                {
                    Logs.Add(arguments?[0]?.ToString() ?? string.Empty);
                }
                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            });
            IBlockAccessor blockAccessor = RuntimeCoverageDispatchProxy.Create<IBlockAccessor>((method, _) =>
                method.Name == "GetClimateAt"
                    ? Climate
                    : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            IClientPlayer player = RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) =>
                method.Name == "get_Entity"
                    ? PlayerEntityAvailable ? Entity : null
                    : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            IClientGameCalendar calendar = RuntimeCoverageDispatchProxy.Create<IClientGameCalendar>((method, arguments) => method.Name switch
            {
                "get_SunPositionNormalized" => SunDirection,
                "GetSunPosition" => SunDirection,
                "get_SunColor" => SunColor,
                "GetDayLightStrength" => DayLightStrength,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
            IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>((method, _) => method.Name switch
            {
                "get_Collectibles" => new List<CollectibleObject>(),
                "get_Player" => PlayerAvailable ? player : null,
                "get_BlockAccessor" => blockAccessor,
                "get_Calendar" => CalendarAvailable ? calendar : null,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
            IClientEventAPI eventApi = RuntimeCoverageDispatchProxy.Create<IClientEventAPI>(
                (method, arguments) =>
                {
                    if (method.Name == "RegisterRenderer")
                    {
                        RegisteredRendererStages.Add((EnumRenderStage)arguments![1]!);
                    }
                    else if (method.Name == "UnregisterRenderer")
                    {
                        UnregisteredRendererStages.Add((EnumRenderStage)arguments![1]!);
                    }
                    return method.Name == "RegisterGameTickListener"
                        ? 1L
                        : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
                });
            IInputAPI input = RuntimeCoverageDispatchProxy.Create<IInputAPI>((method, _) => method.Name switch
            {
                "get_MouseYaw" => MouseYaw,
                "get_MousePitch" => MousePitch,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
            Uniforms = new DefaultShaderUniforms
            {
                ToShadowMapSpaceMatrixFar = IdentityMatrixFloat(),
                ToShadowMapSpaceMatrixNear = IdentityMatrixFloat(),
                ShadowRangeFar = 96.0f,
                ShadowRangeNear = 32.0f,
                playerReferencePos = new Vec3d(10, 80, 20)
            };
            IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) => method.Name switch
            {
                "get_FrameWidth" => FrameWidth,
                "get_FrameHeight" => FrameHeight,
                 "get_CameraMatrixOrigin" => CameraMatrix,
                 "get_PerspectiveProjectionMat" => ProjectionMatrix,
                "get_ShaderUniforms" => Uniforms,
                "get_FrameBuffers" => FrameBuffers,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
            IShader shaderStage = RuntimeCoverageDispatchProxy.Create<IShader>((method, _) =>
                RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            IShaderProgram shaderProgram = RuntimeCoverageDispatchProxy.Create<IShaderProgram>((method, _) => method.Name switch
            {
                "get_VertexShader" => shaderStage,
                "get_FragmentShader" => shaderStage,
                "Compile" => ShaderCompileResult,
                "get_LoadError" => ShaderLoadError,
                "get_Disposed" => ShaderDisposed,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
            IShaderAPI shaderApi = RuntimeCoverageDispatchProxy.Create<IShaderAPI>((method, _) => method.Name switch
            {
                "IsGLSLVersionSupported" => ShaderVersionSupported,
                "NewShaderProgram" => shaderProgram,
                "NewShader" => shaderStage,
                "RegisterMemoryShaderProgram" => ShaderPassId,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
            IAsset shaderAsset = RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) => method.Name switch
            {
                "ToText" => "#version 330 core\nvoid main() {}\n",
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
            IAssetManager assetManager = RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, _) =>
                method.Name == "TryGet"
                    ? shaderAsset
                    : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_Event" => eventApi,
                "get_Logger" => logger,
                "get_World" => world,
                "get_Render" => render,
                "get_Input" => input,
                "get_Shader" => shaderApi,
                "get_Assets" => assetManager,
                "get_InWorldEllapsedMilliseconds" => InWorldMilliseconds,
                "GetOrCreateDataPath" => captureRoot,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });

            Scene = new VoxelScene(api);
            Renderer = new FilmicDisplayRenderer(api, () => Config, Scene);
        }

        /// <summary>
        /// Gets the renderer value exposed to the deterministic fixture.
        /// </summary>
        public FilmicDisplayRenderer Renderer { get; }
        /// <summary>
        /// Gets the scene value exposed to the deterministic fixture.
        /// </summary>
        public VoxelScene Scene { get; }
        /// <summary>
        /// Gets the config value exposed to the deterministic fixture.
        /// </summary>
        public VintageRtxConfig Config { get; } = new();
        /// <summary>
        /// Gets the entity value exposed to the deterministic fixture.
        /// </summary>
        public EntityPlayer Entity { get; }
        /// <summary>
        /// Gets or sets the climate value exposed to the deterministic fixture.
        /// </summary>
        public ClimateCondition Climate { get; set; }
        /// <summary>
        /// Gets the logs value exposed to the deterministic fixture.
        /// </summary>
        public List<string> Logs { get; } = [];
        /// <summary>Gets renderer stages registered by the coordinator.</summary>
        public List<EnumRenderStage> RegisteredRendererStages { get; } = [];
        /// <summary>Gets renderer stages unregistered by the coordinator.</summary>
        public List<EnumRenderStage> UnregisteredRendererStages { get; } = [];
        /// <summary>
        /// Gets the frame Buffers value exposed to the deterministic fixture.
        /// </summary>
        public List<FrameBufferRef> FrameBuffers { get; } = [];
        /// <summary>
        /// Gets the uniforms value exposed to the deterministic fixture.
        /// </summary>
        public DefaultShaderUniforms Uniforms { get; }
        /// <summary>
        /// Gets or sets the camera Matrix value exposed to the deterministic fixture.
        /// </summary>
        public double[] CameraMatrix { get; set; }
        /// <summary>
        /// Gets or sets the projection Matrix value exposed to the deterministic fixture.
        /// </summary>
        public double[] ProjectionMatrix { get; set; } = IdentityMatrix();
        /// <summary>
        /// Gets or sets the frame Width value exposed to the deterministic fixture.
        /// </summary>
        public int FrameWidth { get; set; } = 1920;
        /// <summary>
        /// Gets or sets the frame Height value exposed to the deterministic fixture.
        /// </summary>
        public int FrameHeight { get; set; } = 1080;
        /// <summary>
        /// Gets or sets the in World Milliseconds value exposed to the deterministic fixture.
        /// </summary>
        public long InWorldMilliseconds { get; set; }
        /// <summary>
        /// Gets or sets the mouse Yaw value exposed to the deterministic fixture.
        /// </summary>
        public float MouseYaw { get; set; }
        /// <summary>
        /// Gets or sets the mouse Pitch value exposed to the deterministic fixture.
        /// </summary>
        public float MousePitch { get; set; }
        /// <summary>
        /// Gets or sets the calendar Available value exposed to the deterministic fixture.
        /// </summary>
        public bool CalendarAvailable { get; set; } = true;
        /// <summary>
        /// Gets or sets the player Available value exposed to the deterministic fixture.
        /// </summary>
        public bool PlayerAvailable { get; set; } = true;
        /// <summary>
        /// Gets or sets the player Entity Available value exposed to the deterministic fixture.
        /// </summary>
        public bool PlayerEntityAvailable { get; set; } = true;
        /// <summary>
        /// Gets or sets the shader Version Supported value exposed to the deterministic fixture.
        /// </summary>
        public bool ShaderVersionSupported { get; set; }
        /// <summary>
        /// Gets or sets the shader Pass Id value exposed to the deterministic fixture.
        /// </summary>
        public int ShaderPassId { get; set; }
        /// <summary>
        /// Gets or sets the shader Compile Result value exposed to the deterministic fixture.
        /// </summary>
        public bool ShaderCompileResult { get; set; } = true;
        /// <summary>
        /// Gets or sets the shader Load Error value exposed to the deterministic fixture.
        /// </summary>
        public bool ShaderLoadError { get; set; }
        /// <summary>
        /// Gets or sets the shader Disposed value exposed to the deterministic fixture.
        /// </summary>
        public bool ShaderDisposed { get; set; }
        /// <summary>
        /// Gets or sets the sun Direction value exposed to the deterministic fixture.
        /// </summary>
        public Vec3f SunDirection { get; set; } = new(0.4f, 0.7f, 0.5f);
        /// <summary>
        /// Gets or sets the sun Color value exposed to the deterministic fixture.
        /// </summary>
        public Vec3f SunColor { get; set; } = new(1.0f, 0.9f, 0.8f);
        /// <summary>
        /// Gets or sets the day Light Strength value exposed to the deterministic fixture.
        /// </summary>
        public float DayLightStrength { get; set; } = 0.75f;

        /// <summary>
        /// Releases resources owned by renderer Harness; cleanup remains safe after partial fixture initialization.
        /// </summary>
        public void Dispose()
        {
            Renderer.Dispose();
            Scene.Dispose();
        }
    }
}
