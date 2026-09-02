using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.RuntimeTestSupport;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>Verifies bounded, SI-facing server injection for real dropped-item scenarios.</summary>
[TestClass]
public sealed class RuntimeImpactServerModSystemTests
{
    /// <summary>Checks scenario selection is trimmed, case-insensitive, and narrowly opt-in.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void WaterReflectionScenarioRequiresTheExplicitIdentifier()
    {
        Assert.IsTrue(RuntimeImpactServerModSystem.IsWaterReflectionScenario(" WATER-REFLECTION "));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsWaterReflectionScenario(null));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsWaterReflectionScenario("reflection"));
    }

    /// <summary>Checks six finite world and velocity values are required inside the safe envelope.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void ImpactArgumentsRejectInvalidOrUnboundedValues()
    {
        Assert.IsTrue(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 2, 3, 4, -5, 6));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 2, 3));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 2, 3, double.NaN, 0, 0));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(30_000_001, 2, 3, 0, 0, 0));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, -1025, 3, 0, 0, 0));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 1_048_577, 3, 0, 0, 0));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 2, -30_000_001, 0, 0, 0));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 2, 3, 33, 0, 0));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 2, 3, 0, -33, 0));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreFiniteAndBounded(1, 2, 3, 0, 0, 33));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsImpactStackSizeValid(1));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsImpactStackSizeValid(64));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsImpactStackSizeValid(0));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsImpactStackSizeValid(65));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsImpactDropHeightValid(0.5));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsImpactDropHeightValid(12.0));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsImpactDropHeightValid(0.49));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsImpactDropHeightValid(12.01));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsImpactDropHeightValid(double.NaN));
    }

    /// <summary>Checks the impact masses mirror the client square-root stack model exactly.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void ImpactPseudoMassUsesTheBoundedSquareRootStackModel()
    {
        Assert.AreEqual(0.35, RuntimeImpactServerModSystem.ExpectedPseudoMassKilograms(1), 1e-12);
        Assert.AreEqual(0.70, RuntimeImpactServerModSystem.ExpectedPseudoMassKilograms(4), 1e-12);
        Assert.AreEqual(2.80, RuntimeImpactServerModSystem.ExpectedPseudoMassKilograms(64), 1e-12);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            RuntimeImpactServerModSystem.ExpectedPseudoMassKilograms(0));
    }

    /// <summary>Checks that both fixed witness anchors remain finite and inside world bounds.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void ReflectionWitnessArgumentsValidateBothAnchors()
    {
        Assert.IsTrue(RuntimeImpactServerModSystem.AreWitnessCoordinatesBounded(1, 2, 3, 4, 5, 6));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreWitnessCoordinatesBounded(1, 2, 3));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreWitnessCoordinatesBounded(1, 2, 3, 4, double.NaN, 6));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreWitnessCoordinatesBounded(1, 2, 3, 30_000_001, 5, 6));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreWitnessCoordinatesBounded(1, 2, 3, 4, -1025, 6));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreWitnessCoordinatesBounded(1, 2, 3, 4, 1_048_577, 6));
        Assert.IsFalse(RuntimeImpactServerModSystem.AreWitnessCoordinatesBounded(1, 2, 3, 4, 5, -30_000_001));
    }

    /// <summary>Checks persistent ownership tags and the exact legacy granite migration scope.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void ScenarioEntityCleanupRecognizesOnlyOwnedOriginsAndReferenceGranite()
    {
        Assert.IsTrue(RuntimeImpactServerModSystem.IsScenarioOwnedOrigin("vintagertx-runtime-impact"));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsScenarioOwnedOrigin("vintagertx-runtime-witness"));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsScenarioOwnedOrigin(null));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsScenarioOwnedOrigin("other-mod"));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsReferenceImpactCollectible(
            new Vintagestory.API.Common.AssetLocation("game", "stone-granite")));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsReferenceImpactCollectible(
            new Vintagestory.API.Common.AssetLocation("game", "rock-granite")));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsReferenceImpactCollectible(
            new Vintagestory.API.Common.AssetLocation("game", "stick")));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsReferenceImpactCollectible(null));
    }

    /// <summary>Checks SI velocity is scaled exactly once by the engine's nominal 60 Hz motion step.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void SiVelocityConvertsToPerPhysicsFrameMotion()
    {
        Vec3d motion = RuntimeImpactServerModSystem.ConvertSiVelocityToEngineMotion(
            4.0,
            -3.0,
            1.5);

        Assert.AreEqual(60.0, RuntimeImpactServerModSystem.EngineMotionStepsPerSecond, 0.0);
        Assert.AreEqual(4.0 / 60.0, motion.X, 1e-12);
        Assert.AreEqual(-3.0 / 60.0, motion.Y, 1e-12);
        Assert.AreEqual(1.5 / 60.0, motion.Z, 1e-12);
    }

    /// <summary>Checks real projectile selectors map to their exact vanilla entity and payload assets.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    public void ProjectileKindsUseRealStoneArrowAndSpearAssets()
    {
        Assert.IsTrue(RuntimeImpactServerModSystem.IsSupportedProjectileKind(" STONE "));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsSupportedProjectileKind("Arrow"));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsSupportedProjectileKind(null));
        Assert.IsTrue(RuntimeImpactServerModSystem.IsSupportedProjectileKind("spear"));
        Assert.IsFalse(RuntimeImpactServerModSystem.IsSupportedProjectileKind("bolt"));

        Assert.AreEqual(
            "game:thrownitem",
            RuntimeImpactServerModSystem.ProjectileEntityCode("stone").ToString());
        Assert.AreEqual(
            "game:stone-granite",
            RuntimeImpactServerModSystem.ProjectilePayloadCode("stone").ToString());
        Assert.AreEqual(
            "game:arrow-flint",
            RuntimeImpactServerModSystem.ProjectileEntityCode("arrow").ToString());
        Assert.AreEqual(
            "game:arrow-flint",
            RuntimeImpactServerModSystem.ProjectilePayloadCode("arrow").ToString());
        Assert.AreEqual(
            "game:spear-generic-flint",
            RuntimeImpactServerModSystem.ProjectileEntityCode("spear").ToString());
        Assert.AreEqual(
            "game:spear-generic-flint",
            RuntimeImpactServerModSystem.ProjectilePayloadCode("spear").ToString());
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            RuntimeImpactServerModSystem.ProjectileEntityCode("bolt"));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            RuntimeImpactServerModSystem.ProjectilePayloadCode("bolt"));
    }
}
