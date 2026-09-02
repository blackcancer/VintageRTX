using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Test;

/// <summary>Validates chronological fusion of client and integrated-server runtime evidence.</summary>
[TestClass]
[TestCategory("Runtime")]
public sealed class RuntimeHarnessLogMergeTests
{
    /// <summary>Preserves request, authoritative spawn, and observed impact causality within one second.</summary>
    [TestMethod]
    public void SameSecondRuntimeEventsRetainCrossSideCausality()
    {
        const string client = """
            1.9.2026 09:59:14 [Notification] impact requested: sequence=1
            1.9.2026 09:59:14 [Notification] impact applied: count=1
            1.9.2026 09:59:16 [Notification] final capture
            """;
        const string server = """
            1.9.2026 09:59:14 [Notification] impact spawned: entity=41
            1.9.2026 09:59:15 [Notification] witness stable
            """;

        string merged = RuntimeHarness.MergeRuntimeLogs(client, server);
        int request = merged.IndexOf("requested", StringComparison.Ordinal);
        int spawn = merged.IndexOf("spawned", StringComparison.Ordinal);
        int impact = merged.IndexOf("applied", StringComparison.Ordinal);
        int stable = merged.IndexOf("stable", StringComparison.Ordinal);
        int capture = merged.IndexOf("capture", StringComparison.Ordinal);

        Assert.IsTrue(request >= 0);
        Assert.IsTrue(request < spawn);
        Assert.IsTrue(spawn < impact);
        Assert.IsTrue(impact < stable);
        Assert.IsTrue(stable < capture);
    }

    /// <summary>
    /// Orders the authoritative witness checkpoint before a same-second projectile request even
    /// though the two independent logs expose no sub-second timestamp.
    /// </summary>
    [TestMethod]
    public void SameSecondWitnessCheckpointPrecedesProjectileRequest()
    {
        const string client = """
            1.9.2026 23:42:57 [Notification] [VintageRTX.Test] Server liquid projectile requested: sequence=1/2, kind=stone
            1.9.2026 23:42:57 [Notification] [VintageRTX.Test] Projectile surface impact applied: sequence=1
            """;
        const string server = """
            1.9.2026 23:42:57 [Notification] [VintageRTX.Test] Server reflection witnesses stable: ticks=1750, opaque entity=41, alpha-shaped entity=42, maximum drift=0.0040m.
            1.9.2026 23:42:57 [Notification] [VintageRTX.Test] Server liquid projectile spawned: kind=stone, entity=43
            """;

        string merged = RuntimeHarness.MergeRuntimeLogs(client, server);
        int stable = merged.IndexOf("ticks=1750", StringComparison.Ordinal);
        int request = merged.IndexOf("requested", StringComparison.Ordinal);
        int spawn = merged.IndexOf("spawned", StringComparison.Ordinal);
        int impact = merged.IndexOf("applied", StringComparison.Ordinal);

        Assert.IsTrue(stable >= 0);
        Assert.IsTrue(stable < request);
        Assert.IsTrue(request < spawn);
        Assert.IsTrue(spawn < impact);
    }

    /// <summary>Handles either side being empty and retains malformed diagnostic lines deterministically.</summary>
    [TestMethod]
    public void PartialAndUntimestampedLogsRemainAvailableToValidation()
    {
        Assert.AreEqual(string.Empty, RuntimeHarness.MergeRuntimeLogs(string.Empty, string.Empty));

        string merged = RuntimeHarness.MergeRuntimeLogs(
            "client diagnostic without timestamp\n",
            "1.9.2026 10:00:00 [Notification] server ready\nserver diagnostic without timestamp\n");

        StringAssert.StartsWith(merged, "1.9.2026 10:00:00 [Notification] server ready");
        StringAssert.Contains(merged, "client diagnostic without timestamp");
        StringAssert.Contains(merged, "server diagnostic without timestamp");
    }
}
