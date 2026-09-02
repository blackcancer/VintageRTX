using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Test;

/// <summary>
/// Native MSTest entry points for Visual Studio Test Explorer. The command-line
/// harness remains available through Program.Main, while these data rows expose
/// every deterministic contract as an independently discoverable test.
/// </summary>
[TestClass]
public sealed class VisualStudioExplorerTests
{
    /// <summary>
    /// Gets the preflight Cases value exposed to the deterministic fixture.
    /// </summary>
    public static IEnumerable<object[]> PreflightCases =>
        PreflightSuite.TestNames.Select(static name => new object[] { name });

    /// <summary>
    /// Gets the liquid Optics Cases value exposed to the deterministic fixture.
    /// </summary>
    public static IEnumerable<object[]> LiquidOpticsCases =>
        LiquidOpticsTests.TestNames.Select(static name => new object[] { name });

    /// <summary>
    /// Verifies the preflight Contract regression contract against deterministic fixture data.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    [TestMethod]
    [DynamicData(nameof(PreflightCases))]
    public void PreflightContract(string name)
    {
        PreflightSuite.RunTest(name);
    }

    /// <summary>
    /// Verifies the liquid Optics Contract regression contract against deterministic fixture data.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    [TestMethod]
    [DynamicData(nameof(LiquidOpticsCases))]
    public void LiquidOpticsContract(string name)
    {
        LiquidOpticsTests.RunTest(name);
    }
}
