using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>
/// Exercises deterministic renderer services without requiring an OpenGL
/// context. GPU interop itself remains covered by the RenderLab test project.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RendererServicesCoverageTests
{
    /// <summary>
    /// Gets or sets the MSTest context used to locate per-run artifacts without process-global state.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies the frame Capture Manual Queue And Default Schedule Are Deterministic regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void FrameCaptureManualQueueAndDefaultScheduleAreDeterministic()
    {
        DateTime now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        FrameCaptureService service = CreateCaptureService(
            _ => null,
            () => now,
            out _);

        Assert.AreEqual("idle", service.Status);
        Assert.IsTrue(Path.IsPathFullyQualified(service.CaptureDirectory));
        Assert.IsTrue(service.IsEventCaptureIdle);
        Assert.IsTrue(service.ReadyForBenchmark);
        Assert.IsFalse(service.TryGetCapture(10_000, out _));
        Assert.IsTrue(service.QueueCapture());
        Assert.IsFalse(service.QueueCapture());
        Assert.AreEqual("queued", service.Status);
        Assert.IsTrue(service.TryGetCapture(10_001, out FrameCaptureRequest request));
        Assert.AreEqual(new FrameCaptureRequest("manual", null), request);
        Assert.IsFalse(service.TryGetCapture(10_002, out _));
        Assert.IsFalse(service.QueueCapture(new FrameCaptureRequest("", VintageRtxDebugView.Water)));
        Assert.IsTrue(service.QueueCapture(
            new FrameCaptureRequest("impact-1-final", VintageRtxDebugView.Final)));
        Assert.IsTrue(service.TryGetCapture(10_003, out FrameCaptureRequest impactRequest));
        Assert.AreEqual(
            new FrameCaptureRequest("impact-1-final", VintageRtxDebugView.Final),
            impactRequest);
        Assert.ThrowsException<OverflowException>(
            () => FrameCaptureService.ReadCurrentFrame(int.MaxValue, int.MaxValue));
    }

    /// <summary>
    /// Verifies a final comparison request remains latched across distinct official baseline and
    /// effect frames and that benchmarking cannot start while either readback is outstanding.
    /// </summary>
    [TestMethod]
    public void FrameCaptureTransactionLatchesBaselineThenEffectAcrossFrames()
    {
        DateTime now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        FrameCaptureService service = CreateCaptureService(
            _ => null,
            () => now,
            out _);
        FrameCaptureRequest request = new("post-final", VintageRtxDebugView.Final);

        Assert.IsTrue(service.ReadyForBenchmark);
        Assert.IsTrue(service.QueueCapture(request));
        Assert.IsFalse(service.IsEventCaptureIdle);
        Assert.IsFalse(service.ReadyForBenchmark);
        Assert.IsTrue(service.TryBeginCaptureFrame(100, out FrameCaptureStep baseline));
        Assert.IsFalse(service.TryGetCapture(100, out _));
        Assert.AreEqual(new FrameCaptureStep(request, FrameCapturePhase.Baseline), baseline);
        Assert.IsFalse(service.TryBeginCaptureFrame(100, out _));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.Rejected,
            service.SubmitPostFinalFrame(
                new FrameCaptureStep(request, FrameCapturePhase.Effect),
                [1, 2, 3, 255],
                1,
                1));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(baseline, [1, 2, 3, 255], 1, 1));
        Assert.IsFalse(service.ReadyForBenchmark);
        Assert.IsFalse(service.TryBeginCaptureFrame(100, out _), "effect must use a later engine frame");

        Assert.IsTrue(service.TryBeginCaptureFrame(101, out FrameCaptureStep effect));
        Assert.AreEqual(new FrameCaptureStep(request, FrameCapturePhase.Effect), effect);
        Assert.AreEqual(
            FrameCaptureAdvanceResult.PairSaved,
            service.SubmitPostFinalFrame(effect, [4, 5, 6, 255], 1, 1));
        Assert.IsTrue(service.ReadyForBenchmark);
        Assert.IsTrue(service.Status.StartsWith("saved: ", StringComparison.Ordinal));

        string[] captures = Directory.GetFiles(service.CaptureDirectory, "*-post-final-*.png");
        Assert.AreEqual(2, captures.Length);
        Assert.IsTrue(captures.Any(static path => path.EndsWith("-before.png", StringComparison.Ordinal)));
        Assert.IsTrue(captures.Any(static path => path.EndsWith("-vintagertx.png", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the reflection-source diagnostic cannot complete without a correlated pre-final
    /// readback and writes that raw evidence alongside the official before/after pair.
    /// </summary>
    [TestMethod]
    public void FrameCaptureReflectionSourceRequiresAndSavesRawPreFinalEvidence()
    {
        DateTime now = new(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        FrameCaptureService service = CreateCaptureService(
            _ => null,
            () => now,
            out _);
        FrameCaptureRequest request = new(
            "reflection-source",
            VintageRtxDebugView.ReflectionSource);

        Assert.IsTrue(service.QueueCapture(request));
        Assert.IsTrue(service.TryBeginCaptureFrame(200, out FrameCaptureStep baseline));
        Assert.IsFalse(service.SubmitPreFinalDiagnostic(
            baseline,
            [8, 16, 24, 255],
            1,
            1));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(baseline, [1, 2, 3, 255], 1, 1));
        Assert.IsTrue(service.TryBeginCaptureFrame(201, out FrameCaptureStep effectWithoutRaw));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.RestartQueued,
            service.SubmitPostFinalFrame(effectWithoutRaw, [4, 5, 6, 255], 1, 1));
        StringAssert.Contains(service.Status, "raw pre-final diagnostic is unavailable");

        Assert.IsTrue(service.TryBeginCaptureFrame(202, out FrameCaptureStep restartedBaseline));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(restartedBaseline, [1, 2, 3, 255], 1, 1));
        Assert.IsTrue(service.TryBeginCaptureFrame(203, out FrameCaptureStep effect));
        Assert.IsFalse(service.SubmitPreFinalDiagnostic(
            new FrameCaptureStep(
                new FrameCaptureRequest("another", VintageRtxDebugView.ReflectionSource),
                FrameCapturePhase.Effect),
            [8, 16, 24, 255],
            1,
            1));
        Assert.IsTrue(service.SubmitPreFinalDiagnostic(
            effect,
            [8, 16, 24, 255],
            1,
            1));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.PairSaved,
            service.SubmitPostFinalFrame(effect, [4, 5, 6, 255], 1, 1));

        string[] captures = Directory.GetFiles(
            service.CaptureDirectory,
            "*-reflection-source-*.png");
        Assert.AreEqual(3, captures.Length);
        string[] expectedSuffixes = ["-before.png", "-vintagertx.png", "-raw.png"];
        foreach (string suffix in expectedSuffixes)
        {
            Assert.IsTrue(
                captures.Any(path => path.EndsWith(suffix, StringComparison.Ordinal)),
                suffix);
        }
        string[] correlatedPrefixes = captures
            .Select(path => expectedSuffixes.Aggregate(
                path,
                static (value, suffix) => value.EndsWith(suffix, StringComparison.Ordinal)
                    ? value[..^suffix.Length]
                    : value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(1, correlatedPrefixes.Length);
    }

    /// <summary>
    /// Verifies that an entity-only carrier may retain its native half-resolution dimensions instead
    /// of being contaminated or resampled through the post-final full-size screen framebuffer.
    /// </summary>
    [TestMethod]
    public void FrameCaptureEntityMirrorSavesNativeResolutionRawEvidence()
    {
        DateTime now = new(2026, 9, 1, 12, 31, 0, DateTimeKind.Utc);
        FrameCaptureService service = CreateCaptureService(
            _ => null,
            () => now,
            out _);
        FrameCaptureRequest request = new("entity-mirror", VintageRtxDebugView.EntityMirror);

        Assert.IsTrue(service.QueueCapture(request));
        Assert.IsTrue(service.TryBeginCaptureFrame(300, out FrameCaptureStep baseline));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(baseline, [1, 2, 3, 255], 1, 1));
        Assert.IsTrue(service.TryBeginCaptureFrame(301, out FrameCaptureStep effect));
        Assert.IsTrue(service.SubmitPreFinalDiagnostic(
            effect,
            [8, 16, 24, 255, 32, 40, 48, 255],
            2,
            1));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.PairSaved,
            service.SubmitPostFinalFrame(effect, [4, 5, 6, 255], 1, 1));

        string rawPath = Directory.GetFiles(
            service.CaptureDirectory,
            "*-entity-mirror-raw.png").Single();
        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(rawPath);
        Assert.AreEqual(2, bitmap.Width);
        Assert.AreEqual(1, bitmap.Height);
    }

    /// <summary>
    /// Verifies mismatched final targets automatically restart the same request and explicit restart
    /// or cancellation never permits two transaction phases in one renderer frame.
    /// </summary>
    [TestMethod]
    public void FrameCaptureTransactionRestartsOnMismatchAndCanBeCancelled()
    {
        FrameCaptureService service = CreateCaptureService(
            _ => null,
            static () => new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            out _);
        FrameCaptureRequest request = new("resized", VintageRtxDebugView.Material);
        Assert.IsTrue(service.QueueCapture(request));
        Assert.IsTrue(service.TryBeginCaptureFrame(10, out FrameCaptureStep baseline));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(baseline, [10, 20, 30, 255], 1, 1));
        Assert.IsTrue(service.TryBeginCaptureFrame(11, out FrameCaptureStep effect));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.RestartQueued,
            service.SubmitPostFinalFrame(
                effect,
                [1, 2, 3, 255, 4, 5, 6, 255],
                2,
                1));
        StringAssert.Contains(service.Status, "retry queued: resized");
        Assert.IsFalse(service.TryBeginCaptureFrame(11, out _));
        Assert.IsTrue(service.TryBeginCaptureFrame(12, out FrameCaptureStep restartedBaseline));
        Assert.AreEqual(new FrameCaptureStep(request, FrameCapturePhase.Baseline), restartedBaseline);
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(restartedBaseline, [10, 20, 30, 255], 1, 1));

        Assert.IsTrue(service.RestartCaptureTransaction());
        Assert.IsFalse(service.TryBeginCaptureFrame(12, out _));
        Assert.IsTrue(service.TryBeginCaptureFrame(13, out FrameCaptureStep callerRestart));
        Assert.AreEqual(new FrameCaptureStep(request, FrameCapturePhase.Baseline), callerRestart);
        Assert.IsTrue(service.CancelCaptureTransaction());
        Assert.AreEqual("cancelled", service.Status);
        Assert.IsFalse(service.CancelCaptureTransaction());
        Assert.IsFalse(service.RestartCaptureTransaction());
        Assert.IsFalse(service.TryBeginCaptureFrame(14, out _));
        Assert.IsTrue(service.IsEventCaptureIdle);
        Assert.IsTrue(service.ReadyForBenchmark);
    }

    /// <summary>Rejects invalid final/raw payloads and preserves automatic requests after cancellation.</summary>
    [TestMethod]
    public void FrameCaptureInvalidPayloadAndAutomaticCancellationRemainRecoverable()
    {
        FrameCaptureService manual = CreateCaptureService(
            _ => null,
            static () => new DateTime(2026, 9, 1, 13, 0, 0, DateTimeKind.Utc),
            out _);
        FrameCaptureRequest reflection = new("invalid-raw", VintageRtxDebugView.ReflectionSource);
        Assert.IsTrue(manual.QueueCapture(reflection));
        Assert.IsTrue(manual.TryBeginCaptureFrame(1, out FrameCaptureStep baseline));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.RestartQueued,
            manual.SubmitPostFinalFrame(baseline, [], 0, 1));
        Assert.IsTrue(manual.TryBeginCaptureFrame(2, out baseline));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.RestartQueued,
            manual.SubmitPostFinalFrame(baseline, [], int.MaxValue, int.MaxValue));
        Assert.IsTrue(manual.TryBeginCaptureFrame(3, out baseline));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            manual.SubmitPostFinalFrame(baseline, [1, 2, 3, 255], 1, 1));
        Assert.IsTrue(manual.TryBeginCaptureFrame(4, out FrameCaptureStep effect));
        Assert.IsFalse(manual.SubmitPreFinalDiagnostic(effect, [], 1, 1));
        StringAssert.Contains(manual.Status, "invalid raw pre-final diagnostic payload");
        Assert.IsTrue(manual.CancelCaptureTransaction());

        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = "water-reflection",
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "180",
        };
        FrameCaptureService automatic = CreateCaptureService(
            name => environment.GetValueOrDefault(name),
            static () => new DateTime(2026, 9, 1, 13, 0, 0, DateTimeKind.Utc),
            out _);
        Assert.IsTrue(automatic.TryBeginCaptureFrame(180, out FrameCaptureStep selected));
        Assert.AreEqual("final", selected.Request.Label);
        Assert.IsTrue(automatic.CancelCaptureTransaction());
        Assert.IsTrue(automatic.TryBeginCaptureFrame(181, out FrameCaptureStep repeated));
        Assert.AreEqual(selected.Request, repeated.Request);
        Assert.IsTrue(automatic.CancelCaptureTransaction());
    }

    /// <summary>
    /// Verifies the frame Capture Automatic Sequence Emits Every Diagnostic Once regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void FrameCaptureAutomaticSequenceEmitsEveryDiagnosticOnce()
    {
        DateTime now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = "water-reflection",
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "180"
        };
        FrameCaptureService service = CreateCaptureService(
            name => environment.GetValueOrDefault(name),
            () => now,
            out List<string> notifications);
        Assert.IsFalse(service.ReadyForBenchmark);
        (long Frame, string Label, VintageRtxDebugView View)[] expected =
        [
            (180, "final", VintageRtxDebugView.Final),
            (200, "normal", VintageRtxDebugView.Normal),
            (220, "position", VintageRtxDebugView.Position),
            (240, "lighting", VintageRtxDebugView.Lighting),
            (250, "reflection", VintageRtxDebugView.Reflection),
            (260, "voxel-reflection", VintageRtxDebugView.VoxelReflection),
            (270, "voxel-albedo", VintageRtxDebugView.VoxelAlbedo),
            (280, "voxel-bounce", VintageRtxDebugView.VoxelBounce),
            (290, "voxel-visibility", VintageRtxDebugView.VoxelVisibility),
            (300, "transport-components", VintageRtxDebugView.TransportComponents),
            (310, "voxel-shadow", VintageRtxDebugView.VoxelShadow),
            (320, "material", VintageRtxDebugView.Material),
            (330, "water", VintageRtxDebugView.Water),
            (340, "wetness", VintageRtxDebugView.Wetness),
            (350, "entity-mirror", VintageRtxDebugView.EntityMirror)
        ];

        Assert.IsFalse(service.TryGetCapture(179, out _));
        FrameCaptureRequest terminalRequest = default;
        foreach ((long frame, string label, VintageRtxDebugView view) in expected)
        {
            Assert.IsTrue(service.TryGetCapture(frame, out terminalRequest), label);
            Assert.AreEqual(label, terminalRequest.Label);
            Assert.AreEqual(view, terminalRequest.DebugViewOverride);
            Assert.IsFalse(service.TryGetCapture(frame, out _), $"duplicate {label}");
        }
        Assert.IsFalse(service.TryGetCapture(100_000, out _));
        Assert.IsFalse(notifications.Any(static message =>
            message.Contains("Automatic capture sequence completed", StringComparison.Ordinal)));
        service.SavePair(
            new FrameCaptureRequest("not-terminal", VintageRtxDebugView.Final),
            [1, 2, 3, 255],
            [4, 5, 6, 255],
            1,
            1);
        Assert.IsFalse(notifications.Any(static message =>
            message.Contains("Automatic capture sequence completed", StringComparison.Ordinal)));
        service.SavePair(
            terminalRequest,
            [1, 2, 3, 255],
            [4, 5, 6, 255],
            1,
            1);
        int pairNotification = notifications.FindIndex(static message =>
            message.Contains("Comparison capture saved", StringComparison.Ordinal));
        int completionNotification = notifications.FindIndex(static message =>
            message.Contains("Automatic capture sequence completed", StringComparison.Ordinal));
        Assert.IsTrue(pairNotification >= 0);
        Assert.IsTrue(completionNotification > pairNotification);
        service.SavePair(
            terminalRequest,
            [1, 2, 3, 255],
            [4, 5, 6, 255],
            1,
            1);
        Assert.AreEqual(1, notifications.Count(static message =>
            message.Contains("Automatic capture sequence completed", StringComparison.Ordinal)));
        Assert.IsFalse(service.ReadyForBenchmark);
        now = now.AddSeconds(2);
        Assert.IsTrue(service.ReadyForBenchmark);

        environment["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "12";
        FrameCaptureService fallback = CreateCaptureService(
            name => environment.GetValueOrDefault(name),
            static () => new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
            out _);
        Assert.IsFalse(fallback.TryGetCapture(359, out _));
        Assert.IsTrue(fallback.TryGetCapture(360, out FrameCaptureRequest fallbackRequest));
        Assert.AreEqual("final", fallbackRequest.Label);
    }

    /// <summary>Verifies the fixed-lantern profile records three temporally separated final and shadow states.</summary>
    [TestMethod]
    public void FrameCaptureLightStabilityProfileSeparatesFinalAndShadowTriplets()
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = "light-stability",
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "900",
            ["VINTAGERTX_TEST_ENVIRONMENT_READY"] = "0"
        };
        FrameCaptureService service = CreateCaptureService(
            name => environment.GetValueOrDefault(name),
            static () => new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
            out List<string> notifications);
        (long Frame, string Label, VintageRtxDebugView View)[] expected =
        [
            (900, "final", VintageRtxDebugView.Final),
            (920, "normal", VintageRtxDebugView.Normal),
            (940, "position", VintageRtxDebugView.Position),
            (960, "material", VintageRtxDebugView.Material),
            (980, "lighting", VintageRtxDebugView.Lighting),
            (1000, "voxel-shadow", VintageRtxDebugView.VoxelShadow),
            (1120, "light-stability-final-b", VintageRtxDebugView.Final),
            (1140, "light-stability-shadow-b", VintageRtxDebugView.VoxelShadow),
            (1260, "light-stability-final-c", VintageRtxDebugView.Final),
            (1280, "light-stability-shadow-c", VintageRtxDebugView.VoxelShadow)
        ];

        Assert.IsFalse(service.TryGetCapture(10_000, out _));
        environment["VINTAGERTX_TEST_ENVIRONMENT_READY"] = "1";
        FrameCaptureRequest terminal = default;
        foreach ((long frame, string label, VintageRtxDebugView view) in expected)
        {
            Assert.IsTrue(service.TryGetCapture(frame, out terminal), label);
            Assert.AreEqual(new FrameCaptureRequest(label, view), terminal);
        }

        SaveNumericTerminal(service, terminal);
        Assert.IsTrue(notifications.Any(static message => message.Contains(
            "profile=light-stability, last=light-stability-shadow-c, captures=10",
            StringComparison.Ordinal)));
    }

    /// <summary>Verifies generic real-map campaigns omit an unavailable entity-only terminal target.</summary>
    [TestMethod]
    public void FrameCaptureDefaultAutomaticSequenceEndsOnWetnessWithoutEntityWitnesses()
    {
        DateTime now = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "180"
        };
        FrameCaptureService service = CreateCaptureService(
            name => environment.GetValueOrDefault(name),
            () => now,
            out List<string> notifications);

        FrameCaptureRequest terminal = default;
        int captureCount = 0;
        while (service.TryGetCapture(10_000, out FrameCaptureRequest request))
        {
            terminal = request;
            captureCount++;
        }

        Assert.AreEqual(14, captureCount);
        Assert.AreEqual("wetness", terminal.Label);
        Assert.AreEqual(VintageRtxDebugView.Wetness, terminal.DebugViewOverride);
        SaveNumericTerminal(service, terminal);
        Assert.IsTrue(notifications.Any(static message => message.Contains(
            "profile=default, last=wetness, captures=14",
            StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies render-lab capture rejects the uploaded generation visible at commit time and waits
    /// for a strictly newer, fully settled voxel snapshot without a wall-clock delay.
    /// </summary>
    [TestMethod]
    public void FrameCaptureRenderLabRequiresSettledPostCommitVoxelGeneration()
    {
        DateTime now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = "render-lab",
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "180",
            ["VINTAGERTX_RENDER_LAB_READY"] = "0"
        };
        FrameCaptureService service = CreateCaptureService(
            name => environment.GetValueOrDefault(name),
            () => now,
            out List<string> notifications);

        Assert.IsFalse(service.ReadyForBenchmark);
        service.ObserveVoxelSceneState(7, settled: true);
        Assert.IsFalse(service.TryGetCapture(180, out _));
        environment["VINTAGERTX_RENDER_LAB_READY"] = "1";
        Assert.IsFalse(
            service.TryGetCapture(180, out _),
            "the generation visible when the lab commits is the rejected floor");
        Assert.IsFalse(
            service.TryGetCapture(180, out _),
            "a settled but pre-commit generation must remain rejected");
        service.ObserveVoxelSceneState(8, settled: false);
        Assert.IsFalse(
            service.TryGetCapture(180, out _),
            "a newer generation must remain blocked while rebuild, dirty, or upload work is pending");
        service.ObserveVoxelSceneState(8, settled: true);

        long[] frames = [180, 200, 215, 230, 240, 250, 260, 270, 280];
        FrameCaptureRequest finalRequest = default;
        foreach (long frame in frames)
        {
            Assert.IsTrue(service.TryGetCapture(frame, out finalRequest), frame.ToString());
        }
        Assert.AreEqual("native-sun-shadow", finalRequest.Label);
        SaveNumericTerminal(service, finalRequest);
        Assert.IsTrue(service.Status.StartsWith("saved: ", StringComparison.Ordinal));
        Assert.IsTrue(notifications.Count > 0);
        Assert.IsFalse(service.ReadyForBenchmark);
        now = now.AddSeconds(2);
        Assert.IsTrue(service.ReadyForBenchmark);
    }

    /// <summary>
    /// Verifies that the liquid laboratory records two physically separated
    /// surface states together with final, reflection, material, and transport evidence.
    /// </summary>
    [TestMethod]
    public void FrameCaptureLiquidLabRecordsSeparatedMotionStates()
    {
        DateTime now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = "liquid-lab",
            ["VINTAGERTX_AUTO_CAPTURE"] = "1",
            ["VINTAGERTX_AUTO_CAPTURE_FRAME"] = "180",
            ["VINTAGERTX_RENDER_LAB_READY"] = "1"
        };
        FrameCaptureService service = CreateCaptureService(
            name => environment.GetValueOrDefault(name),
            () => now,
            out _);

        Assert.IsFalse(service.TryGetCapture(180, out _));
        now = now.AddSeconds(2);
        Assert.IsFalse(service.TryGetCapture(180, out _));
        now = now.AddSeconds(6);

        (long Frame, string Label, VintageRtxDebugView View)[] expected =
        [
            (180, "final", VintageRtxDebugView.Final),
            (200, "normal", VintageRtxDebugView.Normal),
            (215, "material", VintageRtxDebugView.Material),
            (230, "reflection", VintageRtxDebugView.Reflection),
            (245, "voxel-reflection", VintageRtxDebugView.VoxelReflection),
            (260, "liquid-final-a", VintageRtxDebugView.Final),
            (275, "water-motion-a", VintageRtxDebugView.Water),
            (395, "water-motion-b", VintageRtxDebugView.Water),
            (410, "liquid-final-b", VintageRtxDebugView.Final),
            (425, "liquid-transport", VintageRtxDebugView.TransportComponents)
        ];
        foreach ((long frame, string label, VintageRtxDebugView view) in expected)
        {
            Assert.IsTrue(service.TryGetCapture(frame, out FrameCaptureRequest request), label);
            Assert.AreEqual(new FrameCaptureRequest(label, view), request);
        }
    }

    /// <summary>
    /// Verifies the frame Capture Save Failure Is Contained And Logged regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void FrameCaptureSaveFailureIsContainedAndLogged()
    {
        FrameCaptureService service = CreateCaptureService(
            _ => null,
            static () => new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
            out List<string> notifications);

        service.SavePair(
            new FrameCaptureRequest("invalid", VintageRtxDebugView.Final),
            [],
            [0, 0, 0, 255],
            1,
            1);
        Assert.IsTrue(service.Status.StartsWith("failed: ", StringComparison.Ordinal));
        Assert.IsTrue(notifications.Any(static message => message.StartsWith("ERROR ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the performance Monitor Rejects Bad Samples Wraps And Resets regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void PerformanceMonitorRejectsBadSamplesWrapsAndResets()
    {
        using RenderPerformanceMonitor monitor = new();
        Assert.AreEqual("fps=collecting", monitor.BuildStatus());
        monitor.RecordFrame(float.NaN);
        monitor.RecordFrame(float.PositiveInfinity);
        monitor.RecordFrame(-1.0f);
        monitor.RecordFrame(0.0f);
        monitor.RecordFrame(1.01f);
        Assert.AreEqual(0, monitor.CreateSnapshot().FrameCount);
        monitor.EndGpuMeasurement();

        for (int index = 0; index < 700; index++)
        {
            monitor.RecordFrame(index % 2 == 0 ? 0.010f : 0.020f);
        }
        PerformanceSnapshot snapshot = monitor.CreateSnapshot();
        Assert.AreEqual(600, snapshot.FrameCount);
        Assert.IsTrue(snapshot.AverageFps > 60.0 && snapshot.AverageFps < 70.0);
        Assert.AreEqual(50.0, snapshot.OnePercentLowFps, 0.01);
        Assert.IsTrue(snapshot.JitterMilliseconds > 0.0);
        Assert.AreEqual(0.0, snapshot.GpuMilliseconds);
        StringAssert.Contains(snapshot.ToString(), "1%low=50.0");
        StringAssert.Contains(monitor.BuildStatus(), "fps=");

        monitor.ResetStatistics();
        Assert.AreEqual(default, monitor.CreateSnapshot());
        monitor.Dispose();
    }

    /// <summary>Guards GPU sampling against phase-locking to the two-frame Performance cadence.</summary>
    [TestMethod]
    public void PerformanceMonitorGpuSamplingIntervalIsCoprimeWithTwoFrameCadence()
    {
        const int performanceCadence = 2;
        int interval = (int)(typeof(RenderPerformanceMonitor)
            .GetField(
                "GpuQuerySampleInterval",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue()
            ?? throw new AssertFailedException("GPU query cadence constant is unavailable."));

        Assert.AreEqual(1, interval % performanceCadence);
    }

    /// <summary>
    /// Creates capture Service with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="environment">The environment input used to configure this deterministic test path.</param>
    /// <param name="utcNow">The utc Now input used to configure this deterministic test path.</param>
    /// <param name="notifications">The notifications input used to configure this deterministic test path.</param>
    /// <returns>The create Capture Service result consumed by the caller&apos;s assertion.</returns>
    private FrameCaptureService CreateCaptureService(
        System.Func<string, string?> environment,
        System.Func<DateTime> utcNow,
        out List<string> notifications)
    {
        string resultRoot = TestContext.TestRunResultsDirectory
            ?? throw new InvalidOperationException("MSTest did not provide a results directory.");
        string captureRoot = Path.Combine(resultRoot, $"captures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureRoot);
        notifications = [];
        List<string> capturedNotifications = notifications;
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is "Notification" or "Error")
            {
                string format = arguments?[0]?.ToString() ?? string.Empty;
                object?[] formatArguments = arguments?.Length > 1 && arguments[1] is object?[] packed
                    ? packed
                    : arguments?.Skip(1).ToArray() ?? [];
                string message;
                try
                {
                    message = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        format,
                        formatArguments);
                }
                catch (FormatException)
                {
                    message = format;
                }
                capturedNotifications.Add(
                    $"{(method.Name == "Error" ? "ERROR" : "INFO")} {message}");
            }
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
        {
            return method.Name switch
            {
                "GetOrCreateDataPath" => captureRoot,
                "get_Logger" => logger,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            };
        });
        return new FrameCaptureService(api, environment, utcNow);
    }
    /// <summary>Commits a selected channel through the same baseline/raw/effect transaction as the renderer.</summary>
    /// <param name="service">Production service whose automatic schedule has selected its terminal request.</param>
    /// <param name="request">Selected diagnostic; its raw bytes are mandatory rather than inferred from final colour.</param>
    private static void SaveNumericTerminal(FrameCaptureService service, FrameCaptureRequest request)
    {
        Assert.IsTrue(service.QueueCapture(request));
        Assert.IsTrue(service.TryBeginCaptureFrame(20_000, out FrameCaptureStep baseline));
        Assert.AreEqual(FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(baseline, [1, 2, 3, 255], 1, 1));
        Assert.IsTrue(service.TryBeginCaptureFrame(20_001, out FrameCaptureStep effect));
        Assert.IsTrue(service.SubmitPreFinalDiagnostic(effect, [0, 0, 0, 255], 1, 1));
        Assert.AreEqual(FrameCaptureAdvanceResult.PairSaved,
            service.SubmitPostFinalFrame(effect, [4, 5, 6, 255], 1, 1));
    }

}
