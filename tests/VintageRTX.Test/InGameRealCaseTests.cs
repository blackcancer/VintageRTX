using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Test;

/// <summary>
/// Exposes the representative real-game runtime campaign as individually
/// selectable MSTest rows in Visual Studio Test Explorer.
/// </summary>
[TestClass]
[TestCategory("InGame")]
[TestCategory("RealCase")]
[DoNotParallelize]
public sealed class InGameRealCaseTests
{
    /// <summary>Attaches the exact per-scenario report to its Visual Studio/TRX result.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Gets every automated multi-scene campaign identifier directly from the
    /// catalog. Each row is resolved immediately before launch.
    /// </summary>
    public static IEnumerable<object[]> RepresentativeScenarios
    {
        get => ScenarioCatalog.AutomatedScenarios
            .Select(static scenario => new object[] { scenario.Name });
    }

    /// <summary>
    /// Launches Vintage Story for one catalogued real-case scenario and fails
    /// the MSTest row when the runtime harness reports any validation failure.
    /// </summary>
    /// <param name="scenarioName">Catalogued runtime scenario selected by the Test Explorer row.</param>
    /// <returns>A task that completes after the real game process and artifact validation finish.</returns>
    [TestMethod]
    [DynamicData(nameof(RepresentativeScenarios), DynamicDataSourceType.Property)]
    public async Task RepresentativeScenarioCompletesSuccessfully(string scenarioName)
    {
        ScenarioDefinition scenario = ScenarioCatalog.Get(scenarioName);
        RuntimeScenarioResult result = await RuntimeHarness.RunDetailedAsync(scenario, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.ArtifactDirectory))
            TestContext.AddResultFile(Path.Combine(result.ArtifactDirectory, "runtime-result.json"));
        Assert.AreEqual(0, result.ExitCode, result.FormatFailure());
    }
}
