using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.RuntimeTestSupport;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Test;

/// <summary>Verifies the strict opt-in and player-protection rules of copied-world light isolation.</summary>
[TestClass]
public sealed class RuntimeLightStabilityServerModSystemTests
{
    /// <summary>Accepts only the exact lantern-night scenario selected by the runtime harness.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void IsolationRequiresExactLanternNightScenario()
    {
        Assert.IsTrue(RuntimeLightStabilityServerModSystem.IsLanternNightScenario(" LANTERN-NIGHT "));
        Assert.IsFalse(RuntimeLightStabilityServerModSystem.IsLanternNightScenario(null));
        Assert.IsFalse(RuntimeLightStabilityServerModSystem.IsLanternNightScenario("reference-room"));
    }

    /// <summary>Never removes players but accepts representative dynamic non-player entities.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void IsolationProtectsPlayersAndAcceptsOtherEntities()
    {
        Assert.IsFalse(RuntimeLightStabilityServerModSystem.IsRemovableDynamicEntity(null));
        Assert.IsFalse(RuntimeLightStabilityServerModSystem.IsRemovableDynamicEntity(new EntityPlayer()));
        Assert.IsTrue(RuntimeLightStabilityServerModSystem.IsRemovableDynamicEntity(new EntityAgent()));
        Assert.AreEqual(24.0f, RuntimeLightStabilityServerModSystem.IsolationHorizontalRadius);
        Assert.AreEqual(12.0f, RuntimeLightStabilityServerModSystem.IsolationVerticalRadius);
    }
}
