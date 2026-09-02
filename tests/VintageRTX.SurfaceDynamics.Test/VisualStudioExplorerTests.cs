using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.SurfaceDynamics.Test;

/// <summary>
/// Contains deterministic regression checks for visual Studio Explorer.
/// </summary>
[TestClass]
public sealed class VisualStudioExplorerTests
{
    /// <summary>
    /// Gets the stable scenario sequence exposed to MSTest; ordering remains deterministic for reproducible diagnostics.
    /// </summary>
    public static IEnumerable<object[]> SurfaceDynamicsCases =>
        Program.TestNames.Select(static name => new object[] { name });

    /// <summary>
    /// Verifies the surface Dynamics Contract regression contract against deterministic fixture data.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    [TestMethod]
    [DynamicData(nameof(SurfaceDynamicsCases))]
    public void SurfaceDynamicsContract(string name)
    {
        Program.RunTest(name);
    }
}
