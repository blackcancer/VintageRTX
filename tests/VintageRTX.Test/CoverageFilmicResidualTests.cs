using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Reflection;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Closes deterministic coordinator paths that are not exercised by the live OpenGL RenderLab.
/// The fixture reuses the established Vintage Story API harness and never owns driver resources.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CoverageFilmicResidualTests
{
    /// <summary>Verifies named-capture state, native-map accessors, and adaptive profile-floor repair.</summary>
    [TestMethod]
    public void CapturePropertiesNativeMapsAndAdaptiveFloorAreObservable()
    {
        using HarnessLease harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;

        Assert.IsTrue(renderer.IsDiagnosticCaptureIdle);
        Assert.IsTrue(renderer.QueueDiagnosticCapture("residual", VintageRtxDebugView.NativeSunShadow));
        Assert.IsFalse(renderer.IsDiagnosticCaptureIdle);
        Assert.IsFalse(renderer.QueueDiagnosticCapture("occupied", VintageRtxDebugView.Final));

        FrameCaptureService capture = GetField<FrameCaptureService>(renderer, "captureService");
        Assert.IsTrue(capture.TryGetCapture(1, out FrameCaptureRequest request));
        Assert.AreEqual("residual", request.Label);
        Assert.AreEqual(VintageRtxDebugView.NativeSunShadow, request.DebugViewOverride);
        Assert.IsTrue(renderer.IsDiagnosticCaptureIdle);

        NativeSunShadowDepthMaps maps = new(71, 72);
        Assert.AreEqual(71, maps.FarTextureId);
        Assert.AreEqual(72, maps.NearTextureId);

        VintageRtxConfig config = harness.Config;
        config.RenderProfile = VintageRtxRenderProfile.Performance;
        SetField(renderer, "activeRenderProfile", VintageRtxRenderProfile.Performance);
        SetField(renderer, "adaptiveQualityLevel", 1);
        Invoke(renderer, "UpdateAdaptiveQuality", config, false);
        Assert.AreEqual(2, GetField<int>(renderer, "adaptiveQualityLevel"));
    }

    /// <summary>Consumes, ages, replaces, and resets globally sequenced sub-grid packets.</summary>
    [TestMethod]
    public void SubgridWaveConsumesAgesAndResetsRuntimeHistory()
    {
        using HarnessLease harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        LiquidSurfaceRuntime runtime = GetField<LiquidSurfaceRuntime>(renderer, "liquidSurfaceRuntime");
        LiquidSurfaceSimulation simulation = CreateSurfaceSimulation();
        SetField(runtime, "simulation", simulation);

        LiquidSurfaceSubgridImpactDiagnostic[] history =
            GetField<LiquidSurfaceSubgridImpactDiagnostic[]>(simulation, "subgridImpactHistory");
        history[0] = CreatePacket(sequence: 1, entityId: 101);
        SetField(simulation, "subgridImpactHistoryCount", 1);
        SetField(simulation, "nextSubgridImpactHistoryIndex", 1);
        SetAutoProperty(simulation, "TotalSubgridImpactCount", 1);

        Invoke(renderer, "UpdateSubgridImpactWave", float.NaN);
        LiquidSurfaceSubgridImpactDiagnostic[] active =
            GetField<LiquidSurfaceSubgridImpactDiagnostic[]>(renderer, "activeSubgridImpacts");
        float[] ages = GetField<float[]>(renderer, "activeSubgridImpactAges");
        Assert.AreEqual(1, active[0].Sequence);
        Assert.AreEqual(0.0f, ages[0]);

        Invoke(renderer, "UpdateSubgridImpactWave", 0.25f);
        Assert.AreEqual(0.10f, ages[0], 0.0001f, "Frame aging must use the physical 100 ms clamp.");

        history[1] = CreatePacket(sequence: 2, entityId: 202);
        SetField(simulation, "subgridImpactHistoryCount", 2);
        SetField(simulation, "nextSubgridImpactHistoryIndex", 2);
        SetAutoProperty(simulation, "TotalSubgridImpactCount", 2);
        Invoke(renderer, "UpdateSubgridImpactWave", 0.05f);
        Assert.IsTrue(active.Any(static packet => packet.Sequence == 2));

        SetField(simulation, "subgridImpactHistoryCount", 0);
        SetAutoProperty(simulation, "TotalSubgridImpactCount", 0);
        Invoke(renderer, "UpdateSubgridImpactWave", 0.05f);
        Assert.IsTrue(active.All(static packet => packet.Sequence == 0));
        Assert.IsTrue(ages.All(static age => age == 0.0f));
        Assert.AreEqual(0, GetField<int>(renderer, "lastConsumedSubgridImpactSequence"));
        Assert.AreEqual(0, GetField<int>(renderer, "nextSubgridImpactSlot"));
    }

    /// <summary>Exercises all three dropped-item evidence views, delay clamping, busy retry, and reset.</summary>
    [TestMethod]
    public void DroppedItemEvidenceSchedulesThreeStableViewsAndSurvivesBusySlot()
    {
        using HarnessLease harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        LiquidSurfaceRuntime runtime = GetField<LiquidSurfaceRuntime>(renderer, "liquidSurfaceRuntime");
        LiquidSurfaceSimulation simulation = CreateSurfaceSimulation();
        SetField(runtime, "simulation", simulation);
        FrameCaptureService capture = GetField<FrameCaptureService>(renderer, "captureService");

        SetField(renderer, "lastCapturedDroppedItemImpactCount", 3);
        SetAutoProperty(simulation, "TotalDroppedItemImpactCount", 2);
        Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.1f);
        Assert.AreEqual(0, GetField<int>(renderer, "pendingImpactEvidencePhase"));

        SetAutoProperty(simulation, "TotalDroppedItemImpactCount", 3);
        Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.1f);
        Assert.AreEqual(2, GetField<int>(renderer, "lastCapturedDroppedItemImpactCount"));

        SetAutoProperty(simulation, "TotalDroppedItemImpactCount", 2);
        using EnvironmentVariableLease scenario = new("VINTAGERTX_TEST_SCENARIO", "water-reflection");
        Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.1f);
        Assert.AreEqual(0, GetField<int>(renderer, "pendingImpactEvidencePhase"));

        SetAutoProperty(simulation, "TotalDroppedItemImpactCount", 3);
        Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.1f);
        Assert.AreEqual(1, GetField<int>(renderer, "pendingImpactEvidencePhase"));
        Invoke(renderer, "QueueDroppedItemImpactEvidence", float.NaN);
        Assert.IsTrue(GetField<float>(renderer, "pendingImpactEvidenceDelaySeconds") > 0.0f);

        Assert.IsTrue(capture.QueueCapture(new FrameCaptureRequest("busy", VintageRtxDebugView.Final)));
        for (int frame = 0; frame < 7; frame++)
        {
            Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.10f);
        }
        Assert.AreEqual(1, GetField<int>(renderer, "pendingImpactEvidencePhase"));
        Assert.IsTrue(capture.TryGetCapture(10, out FrameCaptureRequest busy));
        Assert.AreEqual("busy", busy.Label);

        Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.0f);
        Assert.IsTrue(capture.TryGetCapture(11, out FrameCaptureRequest final));
        Assert.AreEqual("impact-3-final", final.Label);
        Assert.AreEqual(VintageRtxDebugView.Final, final.DebugViewOverride);

        Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.10f);
        Assert.IsTrue(capture.TryGetCapture(12, out FrameCaptureRequest field));
        Assert.AreEqual("impact-3-surface-field", field.Label);
        Assert.AreEqual(VintageRtxDebugView.LiquidSurfaceField, field.DebugViewOverride);

        Invoke(renderer, "QueueDroppedItemImpactEvidence", 0.10f);
        Assert.IsTrue(capture.TryGetCapture(13, out FrameCaptureRequest source));
        Assert.AreEqual("impact-3-reflection-source", source.Label);
        Assert.AreEqual(VintageRtxDebugView.ReflectionSource, source.DebugViewOverride);
        Assert.AreEqual(0, GetField<int>(renderer, "pendingImpactEvidencePhase"));
        Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
            "Impact-synchronized capture {0}",
            StringComparison.Ordinal)));
    }

    /// <summary>Projects the exact contact and all five local projective frames, including rejected controls.</summary>
    [TestMethod]
    public void ProjectileScreenAnchorLogsContactControlsAndUnavailableSurface()
    {
        using HarnessLease harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        LiquidSurfaceRuntime runtime = GetField<LiquidSurfaceRuntime>(renderer, "liquidSurfaceRuntime");
        LiquidSurfaceSimulation simulation = CreateSurfaceSimulation();
        SetField(runtime, "simulation", simulation);
        harness.Entity.Pos.SetPos(0, 0, 0);
        harness.Entity.CameraPos.Set(0, 0, 0);
        harness.ProjectionMatrix =
        [
            0.5, 0, 0, 0,
            0, 0.5, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];

        LiquidProjectileImpactDiagnostic stone = CreateProjectile(
            LiquidEntitySurfaceClass.ThrownStone,
            LiquidSurfaceImpulseKind.StoneRicochet,
            supportRadius: 0.01f);
        Invoke(renderer, "LogProjectileImpactScreenAnchor", stone);
        Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
            "Projectile impact screen anchor: sequence={0}",
            StringComparison.Ordinal)));
        Assert.AreEqual(5, harness.Logs.Count(static log => log.Contains(
            "Projectile impact surface frame: sequence={0}",
            StringComparison.Ordinal)));

        LiquidProjectileImpactDiagnostic arrow = CreateProjectile(
            LiquidEntitySurfaceClass.Projectile,
            LiquidSurfaceImpulseKind.GenericEntry,
            supportRadius: 0.10f);
        Invoke(renderer, "LogProjectileImpactScreenAnchor", arrow);
        Assert.IsTrue(harness.Logs.Any(static log => log.Contains("projectile={1}", StringComparison.Ordinal)));
        Invoke(renderer, "LogProjectileImpactScreenAnchor", arrow with { SupportRadiusWorldBlocks = 3.0f });

        SetField<LiquidSurfaceSimulation?>(runtime, "simulation", null);
        Invoke(renderer, "LogProjectileImpactScreenAnchor", arrow);
        Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
            "Projectile impact screen anchor unavailable",
            StringComparison.Ordinal)));

        SetField(renderer, "lastObservedProjectileImpactSequence", 7);
        Invoke(renderer, "QueueProjectileImpactEvidence", 0.10f);
        Assert.AreEqual(0, GetField<int>(renderer, "lastObservedProjectileImpactSequence"));

        Type evidenceKind = typeof(FilmicDisplayRenderer).GetNestedType(
            "ProjectileEvidenceKind",
            BindingFlags.NonPublic) ?? throw new TypeLoadException("ProjectileEvidenceKind");
        object stoneKind = Enum.Parse(evidenceKind, "Stone");
        Invoke(renderer, "BeginProjectileImpactEvidence", stoneKind, 8, 0.25f, 0.12f);
        using EnvironmentVariableLease scenario = new("VINTAGERTX_TEST_SCENARIO", "water-reflection");
        Invoke(renderer, "QueueProjectileImpactEvidence", float.NaN);
        Assert.AreEqual(0.12f, GetField<float>(renderer, "pendingProjectileEvidenceDelaySeconds"));
    }

    /// <summary>Exercises clean G-buffer borrowing and the mirror's validated pre-GL matrix rejection.</summary>
    [TestMethod]
    public void CleanGBufferAndEntityMirrorReachValidatedNonGlBoundaries()
    {
        using HarnessLease harness = new();
        FilmicDisplayRenderer renderer = harness.Renderer;
        ReflectionSourceCaptureRenderer source =
            GetField<ReflectionSourceCaptureRenderer>(renderer, "reflectionSourceCapture");
        EntityMirrorSourceCaptureRenderer entitySource =
            GetField<EntityMirrorSourceCaptureRenderer>(renderer, "entityMirrorSourceCapture");
        try
        {
            SetCaptureReady(source, harness.FrameWidth, harness.FrameHeight);
            SetField(source, "glowTextureId", 102);
            SetField(source, "normalTextureId", 103);
            SetField(source, "positionTextureId", 104);
            SetField(source, "depthTextureId", 105);
            SetField(entitySource, "ready", true);
            SetField(entitySource, "positionTextureId", 106);
            SetField(entitySource, "width", harness.FrameWidth);
            SetField(entitySource, "height", harness.FrameHeight);

            PopulatePrimary(harness.FrameBuffers, harness.FrameWidth, harness.FrameHeight);
            object?[] resolveArguments = [default(GameGBuffer)];
            InvokeWithArguments(renderer, "ResolveGBuffer", resolveArguments);
            Assert.IsTrue(GetField<bool>(renderer, "gBufferAvailable"));
            StringAssert.Contains(GetField<string>(renderer, "gBufferStatus"), "clean late-opaque");

            LiquidSurfaceRuntime runtime = GetField<LiquidSurfaceRuntime>(renderer, "liquidSurfaceRuntime");
            harness.Entity.Pos.SetPos(0.25, 0, 0.25);
            ICoreClientAPI api = GetField<ICoreClientAPI>(renderer, "api");
            StackMatrix4 projectionStack = new(4);
            projectionStack.Push(IdentityMatrix());
            Assert.IsFalse(Invoke<bool>(
                renderer,
                "RenderEntityMirror",
                harness.Config,
                harness.FrameWidth,
                harness.FrameHeight,
                false));

            SetField(runtime, "simulation", CreateSurfaceSimulation());
            SetField(
                api.Render,
                "handler",
                (System.Func<MethodInfo, object?[]?, object?>)((method, _) => method.Name switch
                {
                    "get_FrameWidth" => harness.FrameWidth,
                    "get_FrameHeight" => harness.FrameHeight,
                    "get_CameraMatrixOrigin" => new double[15],
                    "get_CameraMatrixOriginf" => IdentityMatrixFloat(),
                    "get_PerspectiveProjectionMat" => IdentityMatrix(),
                    "get_CurrentProjectionMatrix" => IdentityMatrixFloat(),
                    "get_PMatrix" => projectionStack,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                }));
            Assert.IsFalse(Invoke<bool>(
                renderer,
                "RenderEntityMirror",
                harness.Config,
                harness.FrameWidth,
                harness.FrameHeight,
                false));

            SetField(
                api.Render,
                "handler",
                (System.Func<MethodInfo, object?[]?, object?>)((method, _) => method.Name switch
                {
                    "get_FrameWidth" => harness.FrameWidth,
                    "get_FrameHeight" => harness.FrameHeight,
                    "get_CameraMatrixOrigin" => IdentityMatrix(),
                    "get_CameraMatrixOriginf" => IdentityMatrixFloat(),
                    "get_PerspectiveProjectionMat" => IdentityMatrix(),
                    "get_CurrentProjectionMatrix" => IdentityMatrixFloat(),
                    "get_PMatrix" => projectionStack,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                }));

            // This fixture has no usable mirror depth projection. The public
            // rendering boundary now rejects it before touching OpenGL instead
            // of leaking a reflection-wrapped native-context exception.
            Assert.IsFalse(Invoke<bool>(renderer, "RenderEntityMirror",
                harness.Config, harness.FrameWidth, harness.FrameHeight, false));
            Assert.IsFalse(GetField<bool>(renderer, "faulted"));
            Assert.AreEqual(1, projectionStack.Count, "The mirror guard must restore the projection stack.");
        }
        finally
        {
            ResetCaptureHandles(source, entitySource);
        }
    }

    /// <summary>Runs the post-final failure containment and benchmark CPU-stage report branches.</summary>
    [TestMethod]
    public void CaptureReadbackFailureAndCpuBenchmarkDiagnosticsRemainContained()
    {
        RunWithContext("VintageRTX.Filmic.ReadbackFailure.Coverage", () =>
        {
            using HarnessLease harness = new();
            FilmicDisplayRenderer renderer = harness.Renderer;

            // The checked byte-count multiplication fails before allocation or GL.ReadPixels.
            harness.FrameWidth = int.MinValue;
            harness.FrameHeight = 2;
            SetField(renderer, "activeCaptureStepPending", true);
            Invoke(renderer, "CompletePostFinalCapture");
            Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));
            Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
                "Post-final capture restarted",
                StringComparison.Ordinal)));

            SetField(renderer, "benchmarkCpuDiagnosticFrames", 2);
            long[] totals = GetField<long[]>(renderer, "benchmarkCpuStageTicks");
            long[] maximums = GetField<long[]>(renderer, "benchmarkCpuStageMaximumTicks");
            for (int index = 0; index < totals.Length; index++)
            {
                totals[index] = index + 1;
                maximums[index] = index + 2;
            }
            SetField(renderer, "benchmarkCpuMaximumFrameTicks", 12L);
            SetField(renderer, "benchmarkBaselineFirst", new PerformanceSnapshot(2, 100, 90, 1, 0));
            SetField(renderer, "benchmarkEffect", new PerformanceSnapshot(2, 80, 70, 2, 3));
            Invoke(renderer, "CompleteAutomaticBenchmark", new PerformanceSnapshot(2, 90, 80, 3, 0));
            Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
                "Effect CPU-wall stages",
                StringComparison.Ordinal)));
        });
    }

    /// <summary>Exercises projection short-circuit operands that require independent invalid values.</summary>
    [TestMethod]
    public void ProjectionRejectsEveryNonFiniteClipComponentAndVerticalOverflow()
    {
        double[] identity = IdentityMatrix();
        double[] nonFiniteX = (double[])identity.Clone();
        nonFiniteX[0] = double.NaN;
        double[] nonFiniteY = (double[])identity.Clone();
        nonFiniteY[5] = double.PositiveInfinity;
        double[] nonFiniteW = (double[])identity.Clone();
        nonFiniteW[15] = double.NaN;

        Assert.IsFalse(Project(nonFiniteX, identity, 0.25, 0.25));
        Assert.IsFalse(Project(identity, nonFiniteY, 0.25, 0.25));
        Assert.IsFalse(Project(identity, nonFiniteW, 0.25, 0.25));
        Assert.IsFalse(Project(identity, identity, 0.0, 1.09));
    }

    /// <summary>
    /// Covers named framebuffer encoding, raw diagnostic readback, successful post-final submission,
    /// GL-state capture/restoration, and unavailable entity-mirror containment on a real driver.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void RawDiagnosticAndColorEncodingUseRealHiddenContext()
    {
        RunWithContext("VintageRTX.Filmic.Diagnostic.Coverage", () =>
        {
            using HarnessLease harness = new();
            FilmicDisplayRenderer renderer = harness.Renderer;
            harness.FrameWidth = 4;
            harness.FrameHeight = 4;

            int texture = CreateColorTexture(4, 4, PixelInternalFormat.Rgba8);
            int framebuffer = CreateColorFramebuffer(texture);
            try
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
                GL.ClearColor(0.25f, 0.5f, 0.75f, 1.0f);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                MethodInfo queryEncoding = typeof(FilmicDisplayRenderer).GetMethod(
                    "QueryFramebufferColorEncoding",
                    BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("QueryFramebufferColorEncoding");
                int encoding = (int)queryEncoding.Invoke(
                    null,
                    [framebuffer, FramebufferAttachment.ColorAttachment0])!;
                Assert.AreEqual(DisplayColorPipelineContract.LinearColorEncoding, encoding);

                LumaRenderBridge bridge = GetField<LumaRenderBridge>(renderer, "lumaBridge");
                SetField(bridge, "colorTextureId", texture);
                SetField(bridge, "framebufferId", framebuffer);
                SetField(bridge, "width", 4);
                SetField(bridge, "height", 4);

                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                object state = CaptureGlState();
                Invoke(
                    renderer,
                    "CaptureRawPreFinalDiagnostic",
                    state,
                    4,
                    4,
                    VintageRtxDebugView.ReflectionSource,
                    false);

                FrameCaptureStep effect = BeginEffectCapture(
                    renderer,
                    "gpu-reflection-source",
                    VintageRtxDebugView.ReflectionSource,
                    4,
                    4);
                SetField(renderer, "activeCaptureStep", effect);
                SetField(renderer, "activeCaptureStepPending", true);
                state = CaptureGlState();
                Invoke(
                    renderer,
                    "CaptureRawPreFinalDiagnostic",
                    state,
                    4,
                    4,
                    VintageRtxDebugView.ReflectionSource,
                    false);
                Invoke(renderer, "CompletePostFinalCapture");
                Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));

                FrameCaptureStep unavailable = BeginEffectCapture(
                    renderer,
                    "gpu-entity-unavailable",
                    VintageRtxDebugView.EntityMirror,
                    4,
                    4);
                SetField(renderer, "activeCaptureStep", unavailable);
                SetField(renderer, "activeCaptureStepPending", true);
                state = CaptureGlState();
                Invoke(
                    renderer,
                    "CaptureRawPreFinalDiagnostic",
                    state,
                    4,
                    4,
                    VintageRtxDebugView.EntityMirror,
                    false);
                Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));
                Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
                    "Raw pre-final diagnostic capture restarted",
                    StringComparison.Ordinal)));

                // The bridge now owns both names and releases them with the renderer inside the context.
                texture = 0;
                framebuffer = 0;
            }
            finally
            {
                if (framebuffer > 0)
                {
                    GL.DeleteFramebuffer(framebuffer);
                }
                if (texture > 0)
                {
                    GL.DeleteTexture(texture);
                }
                DrainGlErrors();
            }
        });
    }

    /// <summary>
    /// Executes one complete pre-final frame against real GL storage and exact-size engine carriers,
    /// covering display setup, colour-contract logging, shadow attachment, and final state restoration.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void PreFinalDisplayPassUsesRealHiddenContextAndRestoresState()
    {
        RunWithContext("VintageRTX.Filmic.Display.Coverage", () =>
        {
            using HarnessLease harness = new();
            FilmicDisplayRenderer renderer = harness.Renderer;
            harness.FrameWidth = 8;
            harness.FrameHeight = 8;
            harness.Config.ScreenSpaceReflectionsEnabled = false;
            harness.Config.TemporalAccumulationEnabled = true;

            using EngineFramebufferFixture engine = new(8, 8);
            engine.CopyTo(harness.FrameBuffers);
            InstallRenderCarrierHandler(renderer, harness, engine.FrameBuffers);

            IShaderProgram noOpShader = CreateNoOpShaderProgram();
            SetField(renderer, "shader", noOpShader);
            LumaRenderBridge bridge = GetField<LumaRenderBridge>(renderer, "lumaBridge");
            SetField(bridge, "shader", noOpShader);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, engine.Luma.FboId);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.ClearColor(0.2f, 0.3f, 0.4f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            DrainGlErrors();

            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsTrue(renderer.Initialized);
            Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);
            Assert.IsTrue(GetField<bool>(renderer, "preFinalColorContractLogged"));

            harness.Config.DebugView = VintageRtxDebugView.NativeSunShadow;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);

            harness.Config.DebugView = VintageRtxDebugView.Final;
            foreach (VintageRtxRenderProfile profile in new[]
            {
                VintageRtxRenderProfile.Extreme,
                VintageRtxRenderProfile.Cinematic
            })
            {
                harness.Config.RenderProfile = profile;
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);
                Assert.AreEqual(0, GetField<int>(renderer, "adaptiveQualityLevel"));
            }

            SetField(renderer, "activeCaptureStepPending", true);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));

            Assert.IsTrue(renderer.QueueDiagnosticCapture(
                "gpu-pre-final-transaction",
                VintageRtxDebugView.ReflectionSource));
            // This fixture has no published voxel scene. A transport capture must remain
            // queued, while a deliberately voxel-free raster capture still completes.
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));
            Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);
            bool originalVoxelLighting = harness.Config.VoxelLightingEnabled;
            harness.Config.VoxelLightingEnabled = false;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsTrue(GetField<bool>(renderer, "activeCaptureStepPending"));
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterBlit);
            Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsTrue(GetField<bool>(renderer, "activeCaptureStepPending"));
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterBlit);
            Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));

            harness.Config.VoxelLightingEnabled = originalVoxelLighting;

            SetField(renderer, "benchmarkPhase", BenchmarkPhase.Effect);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.AreEqual(1, GetField<int>(renderer, "benchmarkCpuDiagnosticFrames"));

            int nativeShadowMaximum = Math.Max(
                (int)EnumFrameBuffer.ShadowmapFar,
                (int)EnumFrameBuffer.ShadowmapNear);
            while (engine.FrameBuffers.Count <= nativeShadowMaximum)
            {
                engine.FrameBuffers.Add(new FrameBufferRef());
            }
            FrameBufferRef borrowedNativeShadow = new()
            {
                FboId = engine.Primary.FboId,
                Width = 8,
                Height = 8,
                DepthTextureId = engine.Primary.DepthTextureId
            };
            engine.FrameBuffers[(int)EnumFrameBuffer.ShadowmapFar] = borrowedNativeShadow;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.ShadowFar);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsTrue(GetField<bool>(renderer, "nativeSunShadowContractLogged"));
            SetField(renderer, "nativeSunShadowContractLogged", false);
            engine.FrameBuffers[(int)EnumFrameBuffer.ShadowmapNear] = borrowedNativeShadow;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.ShadowNear);

            ReflectionSourceCaptureRenderer cleanSource =
                GetField<ReflectionSourceCaptureRenderer>(renderer, "reflectionSourceCapture");
            EntityMirrorSourceCaptureRenderer entitySource =
                GetField<EntityMirrorSourceCaptureRenderer>(renderer, "entityMirrorSourceCapture");
            SetField(cleanSource, "ready", true);
            SetField(cleanSource, "colorTextureId", engine.Primary.ColorTextureIds[0]);
            SetField(cleanSource, "glowTextureId", engine.Primary.ColorTextureIds[1]);
            SetField(cleanSource, "normalTextureId", engine.Primary.ColorTextureIds[2]);
            SetField(cleanSource, "positionTextureId", engine.Primary.ColorTextureIds[3]);
            SetField(cleanSource, "depthTextureId", engine.Primary.DepthTextureId);
            SetField(cleanSource, "width", 8);
            SetField(cleanSource, "height", 8);
            SetField(entitySource, "ready", true);
            SetField(entitySource, "positionTextureId", engine.Primary.ColorTextureIds[3]);
            SetField(entitySource, "width", 8);
            SetField(entitySource, "height", 8);
            LiquidSurfaceRuntime liquidRuntime =
                GetField<LiquidSurfaceRuntime>(renderer, "liquidSurfaceRuntime");
            SetField(liquidRuntime, "simulation", CreateSurfaceSimulation());
            EntityMirrorProjection mirrorProjection =
                GetField<EntityMirrorProjection>(renderer, "entityMirrorProjection");
            SetField(mirrorProjection, "shader", noOpShader);
            harness.Config.ScreenSpaceReflectionsEnabled = true;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);
            Assert.IsTrue(GetField<bool>(renderer, "nativeSunShadowContractLogged"));
            Assert.IsTrue(mirrorProjection.IsAllocated);

            FrameCaptureStep entityMirrorStep = BeginEffectCapture(
                renderer,
                "gpu-entity-mirror-ready",
                VintageRtxDebugView.EntityMirror,
                8,
                8);
            SetField(renderer, "activeCaptureStep", entityMirrorStep);
            SetField(renderer, "activeCaptureStepPending", true);
            Invoke(
                renderer,
                "CaptureRawPreFinalDiagnostic",
                CaptureGlState(),
                8,
                8,
                VintageRtxDebugView.EntityMirror,
                true);
            Invoke(renderer, "CompletePostFinalCapture");
            Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));
            ResetCaptureHandles(cleanSource, entitySource);
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, DrainGlErrors());
        });
    }

    /// <summary>
    /// Exercises driver-observed multi-target state, incomplete shadow attachment containment,
    /// raw diagnostic rejection, and a logger failure during the colour-contract probe.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void DriverFailuresAndMultiTargetStateRemainContained()
    {
        RunWithContext("VintageRTX.Filmic.DriverFailures.Coverage", () =>
        {
            using HarnessLease harness = new();
            FilmicDisplayRenderer renderer = harness.Renderer;
            harness.FrameWidth = 4;
            harness.FrameHeight = 4;
            using EngineFramebufferFixture engine = new(4, 4);
            engine.CopyTo(harness.FrameBuffers);
            InstallRenderCarrierHandler(renderer, harness, engine.FrameBuffers);

            DrawBuffersEnum[] fourTargets =
            [
                DrawBuffersEnum.ColorAttachment0,
                DrawBuffersEnum.ColorAttachment1,
                DrawBuffersEnum.ColorAttachment2,
                DrawBuffersEnum.ColorAttachment3
            ];
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, engine.Primary.FboId);
            GL.DrawBuffers(fourTargets.Length, fourTargets);
            object multiTargetState = CaptureGlState();
            DrawBuffersEnum[] capturedTargets = (DrawBuffersEnum[])(multiTargetState.GetType()
                .GetProperty("DrawBuffers", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(multiTargetState)
                ?? throw new MissingMemberException(multiTargetState.GetType().FullName, "DrawBuffers"));
            Assert.AreEqual(4, capturedTargets.Length);
            multiTargetState.GetType().GetMethod("Restore", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(multiTargetState, null);

            int incompleteFramebuffer = GL.GenFramebuffer();
            int[] incompleteTextures = new int[3];
            try
            {
                for (int index = 0; index < incompleteTextures.Length; index++)
                {
                    incompleteTextures[index] = GL.GenTexture();
                    GL.BindTexture(TextureTarget.Texture2D, incompleteTextures[index]);
                }
                MethodInfo attachShadow = typeof(FilmicDisplayRenderer).GetMethod(
                    "AttachShadowFramebuffer",
                    BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("AttachShadowFramebuffer");
                TargetInvocationException attachmentFailure = Assert.ThrowsException<TargetInvocationException>(
                    () => attachShadow.Invoke(
                        null,
                        [
                            incompleteFramebuffer,
                            incompleteTextures[0],
                            incompleteTextures[1],
                            incompleteTextures[2],
                            "expected incomplete shadow carrier"
                        ]));
                Assert.IsInstanceOfType<InvalidOperationException>(attachmentFailure.InnerException);
                Assert.AreEqual(
                    "expected incomplete shadow carrier",
                    attachmentFailure.InnerException!.Message);
            }
            finally
            {
                GL.DeleteFramebuffer(incompleteFramebuffer);
                foreach (int texture in incompleteTextures)
                {
                    if (texture > 0)
                    {
                        GL.DeleteTexture(texture);
                    }
                }
            }

            LumaRenderBridge bridge = GetField<LumaRenderBridge>(renderer, "lumaBridge");
            SetField(bridge, "colorTextureId", engine.Luma.ColorTextureIds[0]);
            SetField(bridge, "framebufferId", engine.Luma.FboId);
            SetField(bridge, "width", 4);
            SetField(bridge, "height", 4);
            FrameCaptureStep submitted = BeginEffectCapture(
                renderer,
                "gpu-rejected-reflection-source",
                VintageRtxDebugView.ReflectionSource,
                4,
                4);
            SetField(renderer, "activeCaptureStep", submitted);
            SetField(renderer, "activeCaptureStepPending", true);
            object readbackState = CaptureGlState();
            Invoke(
                renderer,
                "CaptureRawPreFinalDiagnostic",
                readbackState,
                4,
                4,
                VintageRtxDebugView.ReflectionSource,
                false);
            SetField(renderer, "activeCaptureStep", default(FrameCaptureStep));
            Invoke(
                renderer,
                "CaptureRawPreFinalDiagnostic",
                readbackState,
                4,
                4,
                VintageRtxDebugView.ReflectionSource,
                false);
            Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));
            Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
                "Raw pre-final diagnostic capture was rejected",
                StringComparison.Ordinal)));

            ICoreClientAPI api = GetField<ICoreClientAPI>(renderer, "api");
            ILogger logger = api.Logger;
            bool notificationFailed = false;
            bool warningObserved = false;
            SetField(
                logger,
                "handler",
                (System.Func<MethodInfo, object?[]?, object?>)((method, _) =>
                {
                    if (method.Name == nameof(ILogger.Notification))
                    {
                        notificationFailed = true;
                        throw new InvalidOperationException("expected logger failure");
                    }
                    if (method.Name == nameof(ILogger.Warning))
                    {
                        warningObserved = true;
                    }
                    return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
                }));
            object?[] probeArguments = [EnumRenderStage.AfterPostProcessing, false];
            InvokeWithArguments(renderer, "TryLogColorPipelineContract", probeArguments);
            Assert.IsTrue(notificationFailed);
            Assert.IsTrue(warningObserved);
            Assert.AreEqual(false, probeArguments[1]);
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, DrainGlErrors());

            // The bridge borrows the engine-owned carrier in this test.
            SetField(bridge, "colorTextureId", 0);
            SetField(bridge, "framebufferId", 0);
        });
    }

    /// <summary>
    /// Binds real driver uniforms for the four-slot liquid-impact window while exercising every
    /// rejection operand and both peak-displacement representations.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void SubgridImpactUniformsFilterAndUploadAllPhysicalPacketForms()
    {
        RunWithContext("VintageRTX.Filmic.SubgridUniforms.Coverage", () =>
        {
            using HarnessLease harness = new();
            FilmicDisplayRenderer renderer = harness.Renderer;
            SetField(renderer, "shader", CreateNoOpShaderProgram());
            SetField(
                renderer,
                "dynamicLiquidSurfaceBinding",
                new LiquidSurfaceGpuBinding(1, 4, 7, 8, 8, 0.5f, 1));

            LiquidSurfaceSubgridImpactDiagnostic[] impacts =
                GetField<LiquidSurfaceSubgridImpactDiagnostic[]>(renderer, "activeSubgridImpacts");
            float[] ages = GetField<float[]>(renderer, "activeSubgridImpactAges");
            impacts[0] = default;
            impacts[1] = CreatePacket(2, 102);
            ages[1] = 8.01f;
            impacts[2] = CreatePacket(3, 103) with
            {
                PeakDisplacement = 0.0f,
                SplashPeakDisplacement = 0.0f
            };
            impacts[3] = CreatePacket(4, 104);
            ages[3] = 0.25f;

            Invoke(renderer, "BindSubgridImpactUniforms", false);
            Invoke(renderer, "BindSubgridImpactUniforms", true);
            float[] origins = GetField<float[]>(renderer, "liquidImpactOrigins");
            Assert.AreEqual(1.0f, origins[0], 0.0001f);
            Assert.AreEqual(-5.5f, origins[1], 0.0001f);
            Assert.AreEqual(0.25f, origins[2], 0.0001f);

            impacts[3] = impacts[3] with
            {
                PeakDisplacement = 0.0f,
                SplashPeakDisplacement = 0.02f,
                SplashRadiusWorldBlocks = 0.3f,
                SplashReleaseSeconds = 0.4f
            };
            Invoke(renderer, "BindSubgridImpactUniforms", true);
            Assert.AreEqual(0.0f, origins[3], 0.0001f);
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, DrainGlErrors());
        });
    }

    /// <summary>
    /// Proves that missing and aliased Luma carriers fault only their renderer instance and that
    /// an exception after shader activation still stops the Vintage Story wrapper.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void InvalidLumaAndActiveShaderFailuresRestoreTransactionalState()
    {
        RunWithContext("VintageRTX.Filmic.TransportFailures.Coverage", () =>
        {
            using (HarnessLease harness = new())
            using (EngineFramebufferFixture engine = new(8, 8))
            {
                FilmicDisplayRenderer renderer = harness.Renderer;
                harness.FrameWidth = 8;
                harness.FrameHeight = 8;
                engine.CopyTo(harness.FrameBuffers);
                InstallRenderCarrierHandler(renderer, harness, engine.FrameBuffers);
                IShaderProgram shader = CreateNoOpShaderProgram();
                SetField(renderer, "shader", shader);
                SetField(GetField<LumaRenderBridge>(renderer, "lumaBridge"), "shader", shader);

                // This tests Luma failure containment, without a populated voxel world.
                harness.Config.VoxelLightingEnabled = false;
                Assert.IsTrue(renderer.QueueDiagnosticCapture(
                    "gpu-missing-luma",
                    VintageRtxDebugView.Final));
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsTrue(GetField<bool>(renderer, "activeCaptureStepPending"));
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterBlit);
                engine.FrameBuffers[(int)EnumFrameBuffer.Luma] = new FrameBufferRef();
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsTrue(GetField<bool>(renderer, "faulted"));
                harness.Config.Enabled = false;
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsTrue(GetField<bool>(renderer, "faulted"));
                Assert.IsFalse(GetField<bool>(renderer, "activeCaptureStepPending"));
                Assert.IsTrue(harness.Logs.Any(static log => log.Contains(
                    "Display pass disabled after an OpenGL failure",
                    StringComparison.Ordinal)));
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsTrue(GetField<bool>(renderer, "faulted"));
            }

            using (HarnessLease harness = new())
            using (EngineFramebufferFixture engine = new(8, 8))
            {
                FilmicDisplayRenderer renderer = harness.Renderer;
                harness.FrameWidth = 8;
                harness.FrameHeight = 8;
                engine.CopyTo(harness.FrameBuffers);
                InstallRenderCarrierHandler(renderer, harness, engine.FrameBuffers);
                IShaderProgram shader = CreateNoOpShaderProgram();
                SetField(renderer, "shader", shader);
                LumaRenderBridge bridge = GetField<LumaRenderBridge>(renderer, "lumaBridge");
                SetField(bridge, "shader", shader);
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);

                engine.Luma.ColorTextureIds[0] = bridge.ColorTextureId;
                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsTrue(GetField<bool>(renderer, "faulted"));
                Assert.IsTrue(GetField<string>(renderer, "failureReason").Contains(
                    "aliases an owned VintageRTX write target",
                    StringComparison.Ordinal));
            }

            using (HarnessLease harness = new())
            using (EngineFramebufferFixture engine = new(8, 8))
            {
                FilmicDisplayRenderer renderer = harness.Renderer;
                harness.FrameWidth = 8;
                harness.FrameHeight = 8;
                engine.CopyTo(harness.FrameBuffers);
                InstallRenderCarrierHandler(renderer, harness, engine.FrameBuffers);
                bool stopObserved = false;
                IShaderProgram throwingShader = RuntimeCoverageDispatchProxy.Create<IShaderProgram>(
                    (method, arguments) =>
                    {
                        if (method.Name == "get_Disposed")
                        {
                            return false;
                        }
                        if (method.Name == "get_LoadError")
                        {
                            return false;
                        }
                        if (method.Name == "Compile")
                        {
                            return true;
                        }
                        if (method.Name == "Stop")
                        {
                            stopObserved = true;
                        }
                        if (method.Name == "BindTexture2D"
                            && string.Equals(
                                arguments?[0]?.ToString(),
                                "nativeShadowMapFar",
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("expected active shader failure");
                        }
                        return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
                    });
                SetField(renderer, "shader", throwingShader);
                SetField(GetField<LumaRenderBridge>(renderer, "lumaBridge"), "shader", throwingShader);

                renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
                Assert.IsTrue(GetField<bool>(renderer, "faulted"));
                Assert.IsTrue(stopObserved);
            }

            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, DrainGlErrors());
        });
    }

    /// <summary>
    /// Consumes a real queued fluid-column edit and verifies the exact RGBA texel written into the
    /// renderer-owned 128 by 128 surface carrier.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void IncrementalFluidSurfaceUpdateUploadsOneExactDriverTexel()
    {
        RunWithContext("VintageRTX.Filmic.FluidSubUpload.Coverage", () =>
        {
            using HarnessLease harness = new();
            FilmicDisplayRenderer renderer = harness.Renderer;
            int fluidTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, fluidTexture);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                VoxelScene.FluidSurfaceWidth,
                VoxelScene.FluidSurfaceDepth,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);
            SetField(renderer, "voxelFluidSurfaceTexture", fluidTexture);
            SetField(renderer, "voxelTextureReady", true);

            VoxelScene scene = GetField<VoxelScene>(renderer, "voxelScene");
            List<VoxelFluidSurfaceUpdate> pending =
                GetField<List<VoxelFluidSurfaceUpdate>>(scene, "pendingFluidSurfaceUpdates");
            pending.Add(new VoxelFluidSurfaceUpdate(2, 3, [11, 22, 33, 44]));
            Invoke(renderer, "UpdateVoxelBlockTextures");

            byte[] pixels = new byte[
                VoxelScene.FluidSurfaceWidth
                * VoxelScene.FluidSurfaceDepth
                * VoxelScene.FluidSurfaceChannels];
            GL.BindTexture(TextureTarget.Texture2D, fluidTexture);
            GL.GetTexImage(
                TextureTarget.Texture2D,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pixels);
            int offset = (3 * VoxelScene.FluidSurfaceWidth + 2)
                * VoxelScene.FluidSurfaceChannels;
            CollectionAssert.AreEqual(
                new byte[] { 11, 22, 33, 44 },
                pixels.AsSpan(offset, VoxelScene.FluidSurfaceChannels).ToArray());
            Assert.AreEqual(0, pending.Count);
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, DrainGlErrors());
        });
    }

    /// <summary>
    /// Executes the display pass with an unavailable G-buffer, a live dynamic-surface texture, and
    /// an empty enabled voxel-light snapshot to cover their independent fallbacks.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void DisplayFallbacksAcceptMissingGBufferAndLiveDynamicSurface()
    {
        RunWithContext("VintageRTX.Filmic.DisplayFallbacks.Coverage", () =>
        {
            using HarnessLease harness = new();
            using EngineFramebufferFixture engine = new(8, 8);
            FilmicDisplayRenderer renderer = harness.Renderer;
            harness.FrameWidth = 8;
            harness.FrameHeight = 8;
            engine.FrameBuffers[(int)EnumFrameBuffer.Primary] = new FrameBufferRef();
            engine.CopyTo(harness.FrameBuffers);
            InstallRenderCarrierHandler(renderer, harness, engine.FrameBuffers);
            IShaderProgram shader = CreateNoOpShaderProgram();
            SetField(renderer, "shader", shader);
            SetField(GetField<LumaRenderBridge>(renderer, "lumaBridge"), "shader", shader);
            harness.Config.SunShadowsEnabled = true;
            harness.Config.SunLightStrength = 0.0f;

            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);
            Assert.IsFalse(GetField<bool>(renderer, "gBufferAvailable"));

            SetField(
                renderer,
                "dynamicLiquidSurfaceBinding",
                new LiquidSurfaceGpuBinding(
                    engine.Luma.ColorTextureIds[0],
                    0,
                    0,
                    1,
                    1,
                    1.0f,
                    1));
            Invoke(
                renderer,
                "RenderDisplayPass",
                harness.Config,
                8,
                8,
                default(GameGBuffer),
                VintageRtxDebugView.Final,
                false);

            SetField(renderer, "voxelTextureReady", true);
            SetField(renderer, "voxelSnapshot", default(VoxelSceneSnapshot));
            SetField(renderer, "gBufferAvailable", true);
            GameGBuffer availableGBuffer = new(
                engine.Primary.ColorTextureIds[1],
                engine.Primary.ColorTextureIds[2],
                engine.Primary.ColorTextureIds[3]);
            engine.FrameBuffers[(int)EnumFrameBuffer.Primary] = engine.Primary;
            harness.Config.SunShadowsEnabled = false;
            Invoke(
                renderer,
                "RenderDisplayPass",
                harness.Config,
                8,
                8,
                availableGBuffer,
                VintageRtxDebugView.Final,
                false);
            harness.Config.SunShadowsEnabled = true;
            harness.Config.SunLightStrength = 1.0f;
            harness.Config.TemporalAccumulationEnabled = true;
            Invoke(
                renderer,
                "RenderDisplayPass",
                harness.Config,
                8,
                8,
                availableGBuffer,
                VintageRtxDebugView.Final,
                false);
            Invoke(
                renderer,
                "RenderDisplayPass",
                harness.Config,
                8,
                8,
                availableGBuffer,
                VintageRtxDebugView.Final,
                false);
            harness.Config.TemporalAccumulationEnabled = false;
            Invoke(
                renderer,
                "RenderDisplayPass",
                harness.Config,
                8,
                8,
                availableGBuffer,
                VintageRtxDebugView.Final,
                false);
            Assert.AreEqual(0, GetField<int>(renderer, "currentVoxelLightCount"));
            Assert.IsTrue(GetField<bool>(renderer, "shadowHistoryValid"));
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterPostProcessing);
            Assert.IsFalse(GetField<bool>(renderer, "faulted"), renderer.Status);
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, DrainGlErrors());
        });
    }

    /// <summary>Runs one assertion body inside an isolated invisible OpenGL 4.3 context.</summary>
    /// <param name="title">Native diagnostic title.</param>
    /// <param name="assertions">Assertions requiring a current loaded context.</param>
    private static void RunWithContext(string title, Action assertions)
    {
        Type glfwProvider = typeof(NativeWindow).Assembly.GetType(
            "OpenTK.Windowing.Desktop.GLFWProvider",
            throwOnError: true)!;
        PropertyInfo threadGuard = glfwProvider.GetProperty(
            "CheckForMainThread",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMemberException(glfwProvider.FullName, "CheckForMainThread");
        bool previousGuard = (bool)(threadGuard.GetValue(null) ?? true);
        try
        {
            threadGuard.SetValue(null, false);
            NativeWindowSettings settings = new()
            {
                API = ContextAPI.OpenGL,
                APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                ClientSize = new Vector2i(16, 16),
                StartVisible = false,
                StartFocused = false,
                AutoLoadBindings = false,
                NumberOfSamples = 0,
                Title = title
            };
            using NativeWindow window = new(settings);
            window.MakeCurrent();
            GL.LoadBindings(new GLFWBindingsContext());
            assertions();
        }
        finally
        {
            threadGuard.SetValue(null, previousGuard);
        }
    }

    /// <summary>Creates one exact-size RGBA texture on the current context.</summary>
    /// <param name="width">Positive texture width.</param>
    /// <param name="height">Positive texture height.</param>
    /// <param name="format">Driver storage format.</param>
    /// <returns>Owned texture identifier.</returns>
    private static int CreateColorTexture(int width, int height, PixelInternalFormat format)
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            format,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            IntPtr.Zero);
        return texture;
    }

    /// <summary>Attaches one colour texture to a complete framebuffer.</summary>
    /// <param name="texture">Allocated texture identifier.</param>
    /// <returns>Owned framebuffer identifier.</returns>
    private static int CreateColorFramebuffer(int texture)
    {
        int framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            texture,
            0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        Assert.AreEqual(
            FramebufferErrorCode.FramebufferComplete,
            GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        return framebuffer;
    }

    /// <summary>Captures the renderer's private GL guard from the current real context.</summary>
    /// <returns>Boxed immutable guard passed to private diagnostic readback.</returns>
    private static object CaptureGlState()
    {
        Type stateType = typeof(FilmicDisplayRenderer).GetNestedType(
            "GlState",
            BindingFlags.NonPublic) ?? throw new TypeLoadException("GlState");
        MethodInfo capture = stateType.GetMethod(
            "Capture",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("GlState.Capture");
        return capture.Invoke(null, null)!;
    }

    /// <summary>Starts and stores a baseline so the requested effect step is ready for raw readback.</summary>
    /// <param name="renderer">Renderer owning the capture service.</param>
    /// <param name="label">Stable capture label.</param>
    /// <param name="view">Raw diagnostic view.</param>
    /// <param name="width">Capture width.</param>
    /// <param name="height">Capture height.</param>
    /// <returns>Active effect step.</returns>
    private static FrameCaptureStep BeginEffectCapture(
        FilmicDisplayRenderer renderer,
        string label,
        VintageRtxDebugView view,
        int width,
        int height)
    {
        FrameCaptureService service = GetField<FrameCaptureService>(renderer, "captureService");
        Assert.IsTrue(renderer.QueueDiagnosticCapture(label, view));
        Assert.IsTrue(service.TryBeginCaptureFrame(1, out FrameCaptureStep baseline));
        Assert.AreEqual(FrameCapturePhase.Baseline, baseline.Phase);
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(baseline, new byte[width * height * 4], width, height));
        Assert.IsTrue(service.TryBeginCaptureFrame(2, out FrameCaptureStep effect));
        Assert.AreEqual(FrameCapturePhase.Effect, effect.Phase);
        return effect;
    }

    /// <summary>Creates a Vintage Story shader wrapper whose calls are safe no-ops around raw GL tests.</summary>
    /// <returns>Stable non-disposed shader program proxy.</returns>
    private static IShaderProgram CreateNoOpShaderProgram() =>
        RuntimeCoverageDispatchProxy.Create<IShaderProgram>((method, _) => method.Name switch
        {
            "get_Disposed" => false,
            "get_LoadError" => false,
            "Compile" => true,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>Replaces only the established render proxy handler with complete live carrier data.</summary>
    /// <param name="renderer">Renderer owning the API proxy.</param>
    /// <param name="harness">Established frame and entity fixture.</param>
    /// <param name="frameBuffers">Real engine-like framebuffer registry.</param>
    private static void InstallRenderCarrierHandler(
        FilmicDisplayRenderer renderer,
        HarnessLease harness,
        List<FrameBufferRef> frameBuffers)
    {
        ICoreClientAPI api = GetField<ICoreClientAPI>(renderer, "api");
        double[] identity = IdentityMatrix();
        float[] identityFloat = IdentityMatrixFloat();
        StackMatrix4 stack = new(4);
        stack.Push(identity);
        DefaultShaderUniforms uniforms = new()
        {
            ToShadowMapSpaceMatrixFar = identityFloat,
            ToShadowMapSpaceMatrixNear = identityFloat,
            ShadowRangeFar = 96.0f,
            ShadowRangeNear = 32.0f,
            playerReferencePos = new Vec3d(0, 0, 0)
        };
        SetField(
            api.Render,
            "handler",
            (System.Func<MethodInfo, object?[]?, object?>)((method, _) => method.Name switch
            {
                "get_FrameWidth" => harness.FrameWidth,
                "get_FrameHeight" => harness.FrameHeight,
                "get_FrameBuffers" => frameBuffers,
                "get_CameraMatrixOrigin" => identity,
                "get_CameraMatrixOriginf" => identityFloat,
                "get_PerspectiveProjectionMat" => identity,
                "get_CurrentProjectionMatrix" => identityFloat,
                "get_PMatrix" => stack,
                "get_ShaderUniforms" => uniforms,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            }));
    }

    /// <summary>Drains benign driver errors left by intentionally no-op engine shader wrappers.</summary>
    /// <returns>The final no-error sentinel.</returns>
    private static OpenTK.Graphics.OpenGL4.ErrorCode DrainGlErrors()
    {
        OpenTK.Graphics.OpenGL4.ErrorCode error;
        do
        {
            error = GL.GetError();
        }
        while (error != OpenTK.Graphics.OpenGL4.ErrorCode.NoError);
        return error;
    }

    /// <summary>Creates a projectile diagnostic centered in the deterministic water surface.</summary>
    /// <param name="surfaceClass">Projectile classification used by capture labels.</param>
    /// <param name="impulseKind">Physical entry or ricochet kind.</param>
    /// <param name="supportRadius">World-space validation support radius.</param>
    /// <returns>A finite exact-contact diagnostic.</returns>
    private static LiquidProjectileImpactDiagnostic CreateProjectile(
        LiquidEntitySurfaceClass surfaceClass,
        LiquidSurfaceImpulseKind impulseKind,
        float supportRadius) => new(
            Sequence: 1,
            EntityId: 9001,
            SurfaceClass: surfaceClass,
            ImpulseKind: impulseKind,
            WorldX: 0.02,
            WorldZ: 0.02,
            CellX: 0,
            CellZ: 0,
            SupportRadiusWorldBlocks: supportRadius,
            MassKilograms: 0.1f,
            EnergyJoules: 0.2f,
            IncidentVelocityXMetresPerSecond: 1.0f,
            IncidentVelocityYMetresPerSecond: -1.0f,
            IncidentVelocityZMetresPerSecond: 0.0f,
            OutgoingVelocityXMetresPerSecond: 0.5f,
            OutgoingVelocityYMetresPerSecond: 0.1f,
            OutgoingVelocityZMetresPerSecond: 0.0f);

    /// <summary>Creates one renderer packet with enough physical data to remain shader-visible.</summary>
    /// <param name="sequence">Global packet sequence.</param>
    /// <param name="entityId">Stable source entity identifier.</param>
    /// <returns>A visible water-like packet.</returns>
    private static LiquidSurfaceSubgridImpactDiagnostic CreatePacket(int sequence, long entityId) =>
        default(LiquidSurfaceSubgridImpactDiagnostic) with
        {
            Sequence = sequence,
            SourceSequence = sequence,
            EntityId = entityId,
            SurfaceClass = LiquidEntitySurfaceClass.DroppedItem,
            ImpulseKind = LiquidSurfaceImpulseKind.DroppedItemEntry,
            WorldX = 1.0 + sequence,
            WorldZ = 1.5,
            PeakDisplacement = 0.01f,
            DensityKilogramsPerCubicMetre = 998.2f,
            DynamicViscosityPascalSeconds = 0.001f,
            SurfaceTensionNewtonsPerMetre = 0.072f,
            DominantWavelengthMetres = 0.02f
        };

    /// <summary>Reuses the established connected surface fixture without duplicating physics setup.</summary>
    /// <returns>A deterministic connected 9 by 9 surface.</returns>
    private static LiquidSurfaceSimulation CreateSurfaceSimulation()
    {
        MethodInfo factory = typeof(FilmicDisplayRendererLogicCoverageTests).GetMethod(
            "CreateProjectileCaptureSimulation",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("CreateProjectileCaptureSimulation");
        return (LiquidSurfaceSimulation)factory.Invoke(null, null)!;
    }

    /// <summary>Marks the clean source as an exact-size complete borrowed snapshot.</summary>
    /// <param name="source">Clean late-opaque source fixture.</param>
    /// <param name="width">Expected viewport width.</param>
    /// <param name="height">Expected viewport height.</param>
    private static void SetCaptureReady(ReflectionSourceCaptureRenderer source, int width, int height)
    {
        SetField(source, "ready", true);
        SetField(source, "colorTextureId", 101);
        SetField(source, "width", width);
        SetField(source, "height", height);
    }

    /// <summary>Clears synthetic texture identifiers before the established harness disposes.</summary>
    /// <param name="source">Clean reflection source.</param>
    /// <param name="entitySource">Early entity-position source.</param>
    private static void ResetCaptureHandles(
        ReflectionSourceCaptureRenderer source,
        EntityMirrorSourceCaptureRenderer entitySource)
    {
        foreach (string field in new[]
        {
            "colorTextureId", "glowTextureId", "normalTextureId", "positionTextureId", "depthTextureId"
        })
        {
            SetField(source, field, 0);
        }
        SetField(entitySource, "positionTextureId", 0);
    }

    /// <summary>Populates the public Primary slot with a complete four-attachment fixture.</summary>
    /// <param name="frameBuffers">Mutable public registry.</param>
    /// <param name="width">Framebuffer width.</param>
    /// <param name="height">Framebuffer height.</param>
    private static void PopulatePrimary(List<FrameBufferRef> frameBuffers, int width, int height)
    {
        while (frameBuffers.Count <= (int)EnumFrameBuffer.Primary)
        {
            frameBuffers.Add(new FrameBufferRef());
        }
        frameBuffers[(int)EnumFrameBuffer.Primary] = new FrameBufferRef
        {
            FboId = 44,
            Width = width,
            Height = height,
            ColorTextureIds = [201, 202, 203, 204],
            DepthTextureId = 205
        };
    }

    /// <summary>Projects one point through the supplied matrices for concise negative assertions.</summary>
    /// <param name="view">Candidate view matrix.</param>
    /// <param name="projection">Candidate projection matrix.</param>
    /// <param name="x">Relative point X.</param>
    /// <param name="y">Relative point Y.</param>
    /// <returns>Whether the production projection accepted the point.</returns>
    private static bool Project(double[] view, double[] projection, double x, double y) =>
        FilmicDisplayRenderer.TryProjectWorldPoint(
            x, y, 0, 0, 0, 0, view, projection, 320, 180, out _, out _);

    /// <summary>Gets a private field from a deterministic production or fixture object.</summary>
    /// <typeparam name="T">Expected field type.</typeparam>
    /// <param name="instance">Object owning the field.</param>
    /// <param name="name">Exact field name.</param>
    /// <returns>The current field value.</returns>
    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = FindField(instance.GetType(), name);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>Sets a private field on a deterministic production or fixture object.</summary>
    /// <typeparam name="T">Assigned field type.</typeparam>
    /// <param name="instance">Object owning the field.</param>
    /// <param name="name">Exact field name.</param>
    /// <param name="value">Value to assign.</param>
    private static void SetField<T>(object instance, string name, T value)
    {
        FieldInfo field = FindField(instance.GetType(), name);
        field.SetValue(instance, value);
    }

    /// <summary>Finds a private field declared on a concrete proxy type or one of its base types.</summary>
    /// <param name="type">Most-derived runtime type.</param>
    /// <param name="name">Exact field name.</param>
    /// <returns>The matching reflected field.</returns>
    private static FieldInfo FindField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field is not null)
            {
                return field;
            }
        }
        throw new MissingFieldException(type.FullName, name);
    }

    /// <summary>Sets a compiler-generated private setter backing field without changing production visibility.</summary>
    /// <typeparam name="T">Assigned property type.</typeparam>
    /// <param name="instance">Object owning the auto-property.</param>
    /// <param name="propertyName">Exact property name.</param>
    /// <param name="value">Value to assign.</param>
    private static void SetAutoProperty<T>(object instance, string propertyName, T value) =>
        SetField(instance, $"<{propertyName}>k__BackingField", value);

    /// <summary>Invokes one non-public instance method and returns its result.</summary>
    /// <typeparam name="T">Expected return type.</typeparam>
    /// <param name="instance">Method target.</param>
    /// <param name="name">Exact method name.</param>
    /// <param name="arguments">Invocation arguments.</param>
    /// <returns>The production method result.</returns>
    private static T Invoke<T>(object instance, string name, params object?[] arguments) =>
        (T)FindMethod(instance, name, arguments.Length).Invoke(instance, arguments)!;

    /// <summary>Invokes one non-public instance method that has no return value.</summary>
    /// <param name="instance">Method target.</param>
    /// <param name="name">Exact method name.</param>
    /// <param name="arguments">Invocation arguments.</param>
    private static void Invoke(object instance, string name, params object?[] arguments) =>
        _ = FindMethod(instance, name, arguments.Length).Invoke(instance, arguments);

    /// <summary>Invokes a method with a mutable argument array so ref/out results remain observable.</summary>
    /// <param name="instance">Method target.</param>
    /// <param name="name">Exact method name.</param>
    /// <param name="arguments">Mutable invocation argument array.</param>
    private static void InvokeWithArguments(object instance, string name, object?[] arguments) =>
        _ = FindMethod(instance, name, arguments.Length).Invoke(instance, arguments);

    /// <summary>Resolves the unique private method with the requested arity.</summary>
    /// <param name="instance">Method target.</param>
    /// <param name="name">Exact method name.</param>
    /// <param name="argumentCount">Required parameter count.</param>
    /// <returns>The unique matching method.</returns>
    private static MethodInfo FindMethod(object instance, string name, int argumentCount)
    {
        MethodInfo[] candidates = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == name && method.GetParameters().Length == argumentCount)
            .ToArray();
        Assert.AreEqual(1, candidates.Length, name);
        return candidates[0];
    }

    /// <summary>Creates a column-major identity matrix.</summary>
    /// <returns>A new 4 by 4 identity matrix.</returns>
    private static double[] IdentityMatrix() =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    ];

    /// <summary>Creates a single-precision column-major identity matrix.</summary>
    /// <returns>A new 4 by 4 identity matrix.</returns>
    private static float[] IdentityMatrixFloat() =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    ];

    /// <summary>Owns exact-size Primary, Luma, and liquid-depth carriers on the current GL context.</summary>
    private sealed class EngineFramebufferFixture : IDisposable
    {
        private readonly List<int> textures = [];
        private readonly List<int> framebuffers = [];

        /// <summary>Allocates a complete engine-like public framebuffer registry.</summary>
        /// <param name="width">Exact viewport width.</param>
        /// <param name="height">Exact viewport height.</param>
        public EngineFramebufferFixture(int width, int height)
        {
            int primaryFramebuffer = GL.GenFramebuffer();
            framebuffers.Add(primaryFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, primaryFramebuffer);
            int[] primaryColors = new int[4];
            DrawBuffersEnum[] drawBuffers = new DrawBuffersEnum[4];
            for (int index = 0; index < primaryColors.Length; index++)
            {
                primaryColors[index] = CreateColorTexture(width, height, PixelInternalFormat.Rgba16f);
                textures.Add(primaryColors[index]);
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0 + index,
                    TextureTarget.Texture2D,
                    primaryColors[index],
                    0);
                drawBuffers[index] = DrawBuffersEnum.ColorAttachment0 + index;
            }
            int primaryDepth = CreateDepthTexture(width, height);
            textures.Add(primaryDepth);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D,
                primaryDepth,
                0);
            GL.DrawBuffers(drawBuffers.Length, drawBuffers);
            Assert.AreEqual(
                FramebufferErrorCode.FramebufferComplete,
                GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            Primary = new FrameBufferRef
            {
                FboId = primaryFramebuffer,
                Width = width,
                Height = height,
                ColorTextureIds = primaryColors,
                DepthTextureId = primaryDepth
            };

            int lumaTexture = CreateColorTexture(width, height, PixelInternalFormat.Rgba16f);
            int lumaFramebuffer = CreateColorFramebuffer(lumaTexture);
            textures.Add(lumaTexture);
            framebuffers.Add(lumaFramebuffer);
            Luma = new FrameBufferRef
            {
                FboId = lumaFramebuffer,
                Width = width,
                Height = height,
                ColorTextureIds = [lumaTexture]
            };

            int liquidDepthTexture = CreateDepthTexture(width, height);
            int liquidFramebuffer = GL.GenFramebuffer();
            textures.Add(liquidDepthTexture);
            framebuffers.Add(liquidFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, liquidFramebuffer);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D,
                liquidDepthTexture,
                0);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            Assert.AreEqual(
                FramebufferErrorCode.FramebufferComplete,
                GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            LiquidDepth = new FrameBufferRef
            {
                FboId = liquidFramebuffer,
                Width = width,
                Height = height,
                DepthTextureId = liquidDepthTexture
            };

            int maximumIndex = Math.Max(
                (int)EnumFrameBuffer.Primary,
                Math.Max((int)EnumFrameBuffer.Luma, (int)EnumFrameBuffer.LiquidDepth));
            FrameBuffers = Enumerable.Range(0, maximumIndex + 1)
                .Select(static _ => new FrameBufferRef())
                .ToList();
            FrameBuffers[(int)EnumFrameBuffer.Primary] = Primary;
            FrameBuffers[(int)EnumFrameBuffer.Luma] = Luma;
            FrameBuffers[(int)EnumFrameBuffer.LiquidDepth] = LiquidDepth;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        /// <summary>Gets the complete Primary framebuffer.</summary>
        public FrameBufferRef Primary { get; }

        /// <summary>Gets the public pre-final Luma framebuffer.</summary>
        public FrameBufferRef Luma { get; }

        /// <summary>Gets the public liquid-depth framebuffer.</summary>
        public FrameBufferRef LiquidDepth { get; }

        /// <summary>Gets the enum-indexed public registry.</summary>
        public List<FrameBufferRef> FrameBuffers { get; }

        /// <summary>Copies the registry into the established mutable harness collection.</summary>
        /// <param name="destination">Harness registry to replace.</param>
        public void CopyTo(List<FrameBufferRef> destination)
        {
            destination.Clear();
            destination.AddRange(FrameBuffers);
        }

        /// <summary>Deletes every driver object owned by the engine-like fixture.</summary>
        public void Dispose()
        {
            foreach (int framebuffer in framebuffers)
            {
                GL.DeleteFramebuffer(framebuffer);
            }
            foreach (int texture in textures)
            {
                GL.DeleteTexture(texture);
            }
        }

        /// <summary>Creates one exact-size depth texture.</summary>
        /// <param name="width">Positive width.</param>
        /// <param name="height">Positive height.</param>
        /// <returns>Owned depth texture identifier.</returns>
        private static int CreateDepthTexture(int width, int height)
        {
            int texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.DepthComponent24,
                width,
                height,
                0,
                PixelFormat.DepthComponent,
                PixelType.Float,
                IntPtr.Zero);
            return texture;
        }
    }

    /// <summary>Temporarily assigns one process environment variable and restores it exactly.</summary>
    private sealed class EnvironmentVariableLease : IDisposable
    {
        private readonly string name;
        private readonly string? previous;

        /// <summary>Assigns the scoped environment value.</summary>
        /// <param name="name">Exact variable name.</param>
        /// <param name="value">Temporary value.</param>
        public EnvironmentVariableLease(string name, string? value)
        {
            this.name = name;
            previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>Restores the value observed before construction.</summary>
        public void Dispose() => Environment.SetEnvironmentVariable(name, previous);
    }

    /// <summary>Reflectively leases the established private renderer harness without duplicating API doubles.</summary>
    private sealed class HarnessLease : IDisposable
    {
        private readonly object harness;

        /// <summary>Constructs the established deterministic harness.</summary>
        public HarnessLease()
        {
            Type harnessType = typeof(FilmicDisplayRendererLogicCoverageTests).GetNestedType(
                "RendererHarness",
                BindingFlags.NonPublic)
                ?? throw new TypeLoadException("RendererHarness");
            harness = Activator.CreateInstance(harnessType, nonPublic: true)
                ?? throw new InvalidOperationException("RendererHarness construction returned null.");
        }

        /// <summary>Gets the production renderer under test.</summary>
        public FilmicDisplayRenderer Renderer => GetProperty<FilmicDisplayRenderer>("Renderer");

        /// <summary>Gets the mutable render configuration.</summary>
        public VintageRtxConfig Config => GetProperty<VintageRtxConfig>("Config");

        /// <summary>Gets the mutable player entity.</summary>
        public EntityPlayer Entity => GetProperty<EntityPlayer>("Entity");

        /// <summary>Gets captured logger format strings.</summary>
        public List<string> Logs => GetProperty<List<string>>("Logs");

        /// <summary>Gets the mutable public framebuffer registry.</summary>
        public List<FrameBufferRef> FrameBuffers => GetProperty<List<FrameBufferRef>>("FrameBuffers");

        /// <summary>Gets the configured frame width.</summary>
        public int FrameWidth
        {
            get => GetProperty<int>("FrameWidth");
            set => SetProperty("FrameWidth", value);
        }

        /// <summary>Gets the configured frame height.</summary>
        public int FrameHeight
        {
            get => GetProperty<int>("FrameHeight");
            set => SetProperty("FrameHeight", value);
        }

        /// <summary>Gets or sets the projection matrix returned by the render API double.</summary>
        public double[] ProjectionMatrix
        {
            get => GetProperty<double[]>("ProjectionMatrix");
            set => SetProperty("ProjectionMatrix", value);
        }

        /// <summary>Disposes the established harness and all zero-handle renderer resources.</summary>
        public void Dispose() => ((IDisposable)harness).Dispose();

        /// <summary>Reads one public property from the private nested harness type.</summary>
        /// <typeparam name="T">Expected property type.</typeparam>
        /// <param name="name">Exact property name.</param>
        /// <returns>The current property value.</returns>
        private T GetProperty<T>(string name)
        {
            PropertyInfo property = harness.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(harness.GetType().FullName, name);
            return (T)property.GetValue(harness)!;
        }

        /// <summary>Writes one public property on the private nested harness type.</summary>
        /// <typeparam name="T">Assigned property type.</typeparam>
        /// <param name="name">Exact property name.</param>
        /// <param name="value">Value to assign.</param>
        private void SetProperty<T>(string name, T value)
        {
            PropertyInfo property = harness.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(harness.GetType().FullName, name);
            property.SetValue(harness, value);
        }
    }
}
