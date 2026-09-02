using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using VintageRTX.Configuration;

namespace VintageRTX.Test;

/// <summary>Guards durable proof that runtime scenarios exercised the requested hardware profile.</summary>
[TestClass]
public sealed class RuntimeProfileEvidenceTests
{
    /// <summary>Verifies every profile-owned field is checked and reported independently.</summary>
    [TestMethod]
    public void CanonicalProfileValidationCoversEveryAuthoredProfileAndMismatch()
    {
        foreach (VintageRtxRenderProfile profile in new[]
        {
            VintageRtxRenderProfile.Performance,
            VintageRtxRenderProfile.Balanced,
            VintageRtxRenderProfile.Quality,
            VintageRtxRenderProfile.Ultra,
            VintageRtxRenderProfile.Extreme,
            VintageRtxRenderProfile.Cinematic
        })
        {
            VintageRtxConfig config = new()
            {
                SchemaVersion = VintageRtxConfig.CurrentSchemaVersion
            };
            config.ApplyRenderProfile(profile);
            Assert.AreEqual(0, RuntimeHarness.ValidateArchivedRenderProfile(config, profile).Count, profile.ToString());
        }

        VintageRtxConfig altered = new()
        {
            SchemaVersion = VintageRtxConfig.CurrentSchemaVersion
        };
        altered.ApplyRenderProfile(VintageRtxRenderProfile.Quality);
        altered.RaySteps--;
        IReadOnlyList<string> mismatch = RuntimeHarness.ValidateArchivedRenderProfile(
            altered,
            VintageRtxRenderProfile.Quality);
        Assert.AreEqual(1, mismatch.Count);
        StringAssert.Contains(mismatch[0], nameof(VintageRtxConfig.RaySteps));
        Assert.AreEqual(
            1,
            RuntimeHarness.ValidateArchivedRenderProfile(null, VintageRtxRenderProfile.Quality).Count);
    }

    /// <summary>Verifies capture and benchmark tiers are parsed from the matching profile line only.</summary>
    [TestMethod]
    public void EffectiveTierEvidenceIsProfileScopedAndHonorsAdaptiveFloors()
    {
        const string Log = """
            [VintageRTX] Capture profile evidence: label=final, profile=Performance, effective-tier=performance.
            [VintageRTX] Capture profile evidence: label=final, profile=Balanced, effective-tier=balanced.
            [VintageRTX] Stabilized A/B/A benchmark started after 15.0s in-world (camera locked, warmup=90 frames, samples=300 frames, profile=Balanced, effective-tier=performance).
            """;
        Assert.AreEqual(
            "balanced",
            RuntimeHarness.ExtractEffectiveTier(
                Log,
                "Capture profile evidence: label=final",
                VintageRtxRenderProfile.Balanced));
        Assert.AreEqual(
            "performance",
            RuntimeHarness.ExtractEffectiveTier(
                Log,
                "Stabilized A/B/A benchmark started",
                VintageRtxRenderProfile.Balanced));
        Assert.IsNull(RuntimeHarness.ExtractEffectiveTier(
            Log,
            "Capture profile evidence: label=final",
            VintageRtxRenderProfile.Ultra));

        Assert.AreEqual(0, RuntimeHarness.ValidateEffectiveTier(
            "performance",
            VintageRtxRenderProfile.Performance,
            "capture").Count);
        Assert.AreEqual(0, RuntimeHarness.ValidateEffectiveTier(
            "performance",
            VintageRtxRenderProfile.Balanced,
            "benchmark").Count);
        Assert.AreEqual(0, RuntimeHarness.ValidateEffectiveTier(
            "balanced",
            VintageRtxRenderProfile.Quality,
            "capture").Count);
        Assert.AreEqual(0, RuntimeHarness.ValidateEffectiveTier(
            "high",
            VintageRtxRenderProfile.Ultra,
            "capture").Count);
        Assert.AreEqual(0, RuntimeHarness.ValidateEffectiveTier(
            "high",
            VintageRtxRenderProfile.Extreme,
            "capture").Count);
        Assert.AreEqual(0, RuntimeHarness.ValidateEffectiveTier(
            "high",
            VintageRtxRenderProfile.Cinematic,
            "capture").Count);
        Assert.AreEqual(1, RuntimeHarness.ValidateEffectiveTier(
            "balanced",
            VintageRtxRenderProfile.Extreme,
            "capture").Count);
        Assert.AreEqual(1, RuntimeHarness.ValidateEffectiveTier(
            "balanced",
            VintageRtxRenderProfile.Performance,
            "capture").Count);
        Assert.AreEqual(1, RuntimeHarness.ValidateEffectiveTier(
            null,
            VintageRtxRenderProfile.Ultra,
            "benchmark").Count);
        Assert.AreEqual(1, RuntimeHarness.ValidateEffectiveTier(
            "high",
            VintageRtxRenderProfile.Custom,
            "capture").Count);
    }

    /// <summary>Verifies the durable manifest links seeded/final configs and fingerprints their canonical fields.</summary>
    [TestMethod]
    public void ProfileEvidenceManifestIsReadableStableAndSkipsUnprofiledScenarios()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vintagertx-profile-evidence-{Guid.NewGuid():N}");
        try
        {
            ScenarioDefinition scenario = ScenarioCatalog.Get("render-lab-balanced");
            VintageRtxConfig finalConfig = new()
            {
                SchemaVersion = VintageRtxConfig.CurrentSchemaVersion
            };
            finalConfig.ApplyRenderProfile(VintageRtxRenderProfile.Balanced);
            string fingerprint = RuntimeHarness.CreateRenderProfileFingerprint(finalConfig);
            Assert.AreEqual(64, fingerprint.Length);

            RuntimeHarness.WriteRenderingProfileEvidence(
                root,
                scenario,
                finalConfig,
                "balanced",
                "performance");
            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(
                root,
                "Config",
                "profile-evidence.json")));
            Assert.AreEqual("Balanced", manifest.Value<string>("RequestedProfile"));
            Assert.AreEqual("balanced", manifest.Value<string>("ExpectedInitialTier"));
            Assert.AreEqual("balanced", manifest.Value<string>("NativeFinalCaptureTier"));
            Assert.AreEqual("performance", manifest.Value<string>("BenchmarkTier"));
            Assert.AreEqual("Seeded/vintagertx.json", manifest.Value<string>("SeededConfiguration"));
            Assert.AreEqual("Final/vintagertx.json", manifest.Value<string>("FinalConfiguration"));
            Assert.AreEqual(fingerprint, manifest.Value<string>("ExpectedFingerprint"));
            Assert.AreEqual(fingerprint, manifest.Value<string>("FinalFingerprint"));
            Assert.IsTrue(manifest.Value<bool>("CanonicalConfigurationValid"));

            finalConfig.RayCount++;
            Assert.AreNotEqual(fingerprint, RuntimeHarness.CreateRenderProfileFingerprint(finalConfig));

            string skippedRoot = root + "-skipped";
            RuntimeHarness.WriteRenderingProfileEvidence(
                skippedRoot,
                ScenarioCatalog.Get("reference-room"),
                null,
                null,
                null);
            Assert.IsFalse(Directory.Exists(skippedRoot));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
