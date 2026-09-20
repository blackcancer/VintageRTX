using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>Independent lifecycle and real PNG transaction tests; no world or game process is launched.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RuntimeCampaignContractTests
{
    /// <summary>All fifteen numeric views preserve raw channels even when post-final is contaminated.</summary>
    [TestMethod]
    public void DiagnosticChannelsRetainZeroAndRgbWithoutFinalContamination()
    {
        foreach (VintageRtxDebugView view in Enum.GetValues<VintageRtxDebugView>())
        {
            if (!FrameCaptureDiagnosticContract.IsChannelDiagnostic(view)) continue;
            string root = NewRoot();
            try
            {
                List<string> logs = [];
                FrameCaptureService service = Service(root, logs);
                Assert.IsTrue(service.QueueCapture(new FrameCaptureRequest("channel", view)));
                Assert.IsTrue(service.TryBeginCaptureFrame(10, out FrameCaptureStep baseline));
                Assert.AreEqual(FrameCaptureAdvanceResult.BaselineStored,
                    service.SubmitPostFinalFrame(baseline, [10, 20, 30, 255, 20, 30, 40, 255], 2, 1));
                Assert.IsTrue(service.TryBeginCaptureFrame(11, out FrameCaptureStep effect));
                byte[] raw = [0, 0, 0, 255, 231, 64, 7, 255];
                byte[] display = [12, 13, 19, 255, 55, 60, 70, 255];
                Assert.IsTrue(service.SubmitPreFinalDiagnostic(effect, raw, 2, 1));
                Assert.AreEqual(FrameCaptureAdvanceResult.PairSaved,
                    service.SubmitPostFinalFrame(effect, display, 2, 1));
                using SKBitmap numeric = Decode(root, "*-vintagertx.png");
                using SKBitmap shown = Decode(root, "*-postfinal.png");
                Assert.AreEqual(new SKColor(0, 0, 0), numeric.GetPixel(0, 0), view.ToString());
                Assert.AreEqual(new SKColor(231, 64, 7), numeric.GetPixel(1, 0), view.ToString());
                Assert.AreEqual(new SKColor(12, 13, 19), shown.GetPixel(0, 0), view.ToString());
                JObject contract = JObject.Parse(File.ReadAllText(Directory.GetFiles(root, "*-capture.json").Single()));
                Assert.AreEqual("shader-diagnostic-rgba8", (string?)contract["effectSignal"]);
                Assert.IsTrue(logs.Any(line => line.Contains("-before.png and ", StringComparison.Ordinal)
                    && line.Contains("and raw pre-final", StringComparison.Ordinal)));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    /// <summary>Missing or differently sized numeric evidence cannot silently use the final picture.</summary>
    [TestMethod]
    public void MissingAndWrongSizeRawFramesRestartWithoutSaving()
    {
        string root = NewRoot();
        try
        {
            FrameCaptureService service = Service(root, []);
            Assert.IsTrue(service.QueueCapture(new FrameCaptureRequest("voxel-bounce", VintageRtxDebugView.VoxelBounce)));
            Assert.IsTrue(service.TryBeginCaptureFrame(20, out FrameCaptureStep before));
            _ = service.SubmitPostFinalFrame(before, [1, 2, 3, 255], 1, 1);
            Assert.IsTrue(service.TryBeginCaptureFrame(21, out FrameCaptureStep effect));
            Assert.AreEqual(FrameCaptureAdvanceResult.RestartQueued, service.SubmitPostFinalFrame(effect, [8, 9, 10, 255], 1, 1));
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
            Assert.IsTrue(service.TryBeginCaptureFrame(22, out before));
            _ = service.SubmitPostFinalFrame(before, [1, 2, 3, 255], 1, 1);
            Assert.IsTrue(service.TryBeginCaptureFrame(23, out effect));
            Assert.IsTrue(service.SubmitPreFinalDiagnostic(effect, [0, 0, 0, 255, 0, 0, 0, 255], 2, 1));
            Assert.AreEqual(FrameCaptureAdvanceResult.RestartQueued, service.SubmitPostFinalFrame(effect, [8, 9, 10, 255], 1, 1));
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
        Assert.IsFalse(FrameCaptureDiagnosticContract.RequiresRaw(null));
        Assert.IsFalse(FrameCaptureDiagnosticContract.RequiresRaw(VintageRtxDebugView.Final));
        Assert.IsFalse(FrameCaptureDiagnosticContract.RequiresRaw((VintageRtxDebugView)99));
    }

    /// <summary>Failure of a light-count assertion cannot delay completed capture and benchmark producers.</summary>
    [TestMethod]
    public void FinishedEvidenceDoesNotWaitForSuccessTokens()
    {
        ScenarioDefinition scenario = ScenarioCatalog.Get("many-lights-stress");
        const string log = "[VintageRTX] Automatic capture sequence completed: profile=default, last=wetness, captures=14.\nStabilized A/B/A result";
        Assert.IsTrue(RuntimeScenarioCompletion.HasCollectedEvidence(log, scenario));
        Assert.IsFalse(RuntimeLogValidator.IsComplete(log, scenario));
        Assert.IsTrue(RuntimeLogValidator.Validate(log, scenario).Any(value => value.Contains("missing log token", StringComparison.Ordinal)));
        Assert.IsFalse(RuntimeScenarioCompletion.HasCollectedEvidence("Stabilized A/B/A result\n-wetness-vintagertx.png", scenario));
        Assert.IsFalse(RuntimeScenarioCompletion.HasCollectedEvidence(log.Replace("Stabilized A/B/A result", "benchmark pending"), scenario));
    }

    /// <summary>Capture-only and event-driven rows retain their own completion boundaries.</summary>
    [TestMethod]
    public void CompletionRequiresCorrectProfileAndIndependentEvents()
    {
        const string done = "[VintageRTX] Automatic capture sequence completed: profile=render-lab, last=native-sun-shadow, captures=9.\n";
        Assert.IsTrue(RuntimeScenarioCompletion.HasCollectedEvidence(done, ScenarioCatalog.Get("render-lab")));
        Assert.IsFalse(RuntimeScenarioCompletion.HasCollectedEvidence(done, ScenarioCatalog.Get("render-lab-quality")));
        Assert.IsFalse(RuntimeScenarioCompletion.HasCollectedEvidence(done, ScenarioCatalog.Get("lantern-night")));
        string moving = done.Replace("profile=render-lab", "profile=default") + "Stabilized A/B/A result\n";
        Assert.IsFalse(RuntimeScenarioCompletion.HasCollectedEvidence(moving, ScenarioCatalog.Get("moving-camera")));
        Assert.IsTrue(RuntimeScenarioCompletion.HasCollectedEvidence(moving + "Moving-camera probe completed", ScenarioCatalog.Get("moving-camera")));
        Assert.IsFalse(RuntimeScenarioCompletion.HasCollectedEvidence(done.Replace("profile=render-lab", "profile=water-reflection"), ScenarioCatalog.Get("water-reflection")));
    }

    /// <summary>Each report retains the original failures and fingerprints selected files without client settings.</summary>
    [TestMethod]
    public void ReportsKeepExactFailuresAndInputIdentity()
    {
        string root = NewRoot();
        try
        {
            RuntimeScenarioResult result = new("cave-interior", root, "EvidenceCollected", 1,
                ["GPU cost 3.51ms exceeds 3.00ms", "voxel bounce does not preserve the controlled warm source (0.01 %)"]);
            result.Write();
            string text = File.ReadAllText(Path.Combine(root, "runtime-result.txt"));
            StringAssert.Contains(text, result.Failures[0]);
            StringAssert.Contains(text, result.Failures[1]);
            JObject saved = JObject.Parse(File.ReadAllText(Path.Combine(root, "runtime-result.json")));
            Assert.AreEqual(1, (int?)saved["ExitCode"]);
            Assert.AreEqual(2, ((JArray)saved["Failures"]!).Count);
            string selected = Path.Combine(root, "Mods", "vintagertx");
            Directory.CreateDirectory(selected);
            File.WriteAllText(Path.Combine(selected, "VintageRTX.dll"), "synthetic-fingerprint-fixture-not-a-dll");
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), "never-archive-authentication");
            RuntimeScenarioResult.WriteInputIdentity(root, Path.Combine(root, "Mods"));
            string identity = File.ReadAllText(Path.Combine(root, "runtime-inputs.json"));
            Assert.IsFalse(identity.Contains("never-archive-authentication", StringComparison.Ordinal));
            string hash = (string)JObject.Parse(identity)["files"]!["VintageRTX.dll"]!;
            Assert.AreEqual(64, hash.Length);
            Assert.IsFalse(new RuntimeScenarioResult("held-light", root, "EvidenceCollected", 0, []).FormatFailure().Contains(result.Failures[0], StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Creates an isolated writable fixture directory.</summary>
    /// <returns>New absolute temporary root.</returns>
    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "vintagertx-campaign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Decodes a real persisted fixture PNG.</summary>
    /// <param name="root">Fixture capture directory.</param>
    /// <param name="pattern">Expected unique PNG suffix.</param>
    /// <returns>Owned decoded bitmap.</returns>
    private static SKBitmap Decode(string root, string pattern) => SKBitmap.Decode(File.ReadAllBytes(Directory.GetFiles(root, pattern).Single()))
        ?? throw new InvalidDataException("Fixture PNG decode failed.");

    /// <summary>Provides only filesystem and logging services; PNG encoding remains production code.</summary>
    /// <param name="root">Fixture capture directory.</param>
    /// <param name="logs">Owned log messages recorded by the fixture.</param>
    /// <returns>Production capture service with deterministic time and disabled automatic schedule.</returns>
    private static FrameCaptureService Service(string root, List<string> logs)
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, args) =>
        {
            if (args is { Length: > 0 } && args[0] is string message)
                logs.Add(args.Length > 1 && args[1] is object[] values
                    ? string.Format(CultureInfo.InvariantCulture, message, values) : message);
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "GetOrCreateDataPath" => root,
            "get_Logger" => logger,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        return new FrameCaptureService(api, static _ => null,
            static () => new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
    }
}
