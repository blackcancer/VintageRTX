using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Covers scenario orchestration through the registered production tick
/// callback, including point-light lifetime and one-shot failure behavior.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuntimeCoverageProbeTickTests
{
    /// <summary>
    /// Verifies the tick Waits For Player Applies And Verifies Environment At Scheduled Boundaries regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TickWaitsForPlayerAppliesAndVerifiesEnvironmentAtScheduledBoundaries()
    {
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("environment-only", 12.0f, clearWeather: true);
        Assert.IsNotNull(harness.Tick);

        harness.PlayerAvailable = false;
        harness.Tick(0.02f);
        Assert.AreEqual(0, GetField<int>(probe, "readyTicks"));

        harness.PlayerAvailable = true;
        harness.StartupWorldStateReady = false;
        harness.Tick(0.02f);
        Assert.AreEqual(0, GetField<int>(probe, "readyTicks"));
        Assert.AreEqual(0, harness.ChatMessages.Count);

        harness.StartupWorldStateReady = true;
        SetField(probe, "readyTicks", 99);
        harness.Tick(0.02f);
        Assert.IsTrue(GetField<bool>(probe, "environmentApplied"));
        CollectionAssert.Contains(harness.ChatMessages, "/time set 12");

        harness.HourOfDay = 12.0f;
        harness.SpeedOfTime = 0.0f;
        harness.Climate = new ClimateCondition { Rainfall = 0.0f, RainCloudOverlay = 0.0f };
        SetField(probe, "readyTicks", 174);
        harness.Tick(0.02f);
        Assert.IsTrue(GetField<bool>(probe, "environmentVerified"));
        Assert.IsTrue(GetField<bool>(probe, "injected"));
        probe.Dispose();

        RuntimeCoverageProbeHarness precipitationOnly = new();
        RuntimeScenarioProbe precipitationProbe = precipitationOnly.CreateProbe(
            "environment-only",
            precipitation: 0.45f);
        SetField(precipitationProbe, "readyTicks", 99);
        precipitationOnly.Tick!(0.02f);
        Assert.IsTrue(precipitationOnly.Logs.Any(static entry =>
            entry.Message.Contains("hour=unchanged", StringComparison.Ordinal)
            && entry.Message.Contains("forced precipitation=0.45", StringComparison.Ordinal)));
        precipitationProbe.Dispose();
    }

    /// <summary>
    /// Verifies the held Light And Stress Lights Expose Color Position Movement And Removal regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void HeldLightAndStressLightsExposeColorPositionMovementAndRemoval()
    {
        RuntimeCoverageProbeHarness held = new();
        RuntimeScenarioProbe heldProbe = held.CreateProbe("held-light");
        EnableInjection(heldProbe);
        held.Tick!(0.02f);
        Assert.AreEqual(1, held.PointLights.Count);
        IPointLight point = held.PointLights[0];
        Assert.AreEqual(10.5f, point.Color.X, 0.0001f);
        Assert.AreEqual(held.Entity.CameraPos.X + 0.28, point.Pos.X, 0.0001);
        held.Entity.CameraPos.Set(4.0, 5.0, 6.0);
        held.Tick(0.02f);
        Assert.AreEqual(4.28, point.Pos.X, 0.0001);
        heldProbe.Dispose();
        Assert.AreEqual(0, held.PointLights.Count);

        RuntimeCoverageProbeHarness stress = new();
        RuntimeScenarioProbe stressProbe = stress.CreateProbe("many-lights-stress");
        EnableInjection(stressProbe);
        stress.Tick!(0.02f);
        Assert.AreEqual(12, stress.PointLights.Count);
        Assert.AreEqual(stress.Entity.CameraPos.X + 5.0, stress.PointLights[0].Pos.X, 0.0001);
        Assert.IsTrue(stress.PointLights.Select(static light => light.Pos.Y).Distinct().Count() >= 3);
        stressProbe.Dispose();
    }

    /// <summary>
    /// Verifies the tick Starts Resize Moving And No Light Scenarios Only Once regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TickStartsResizeMovingAndNoLightScenariosOnlyOnce()
    {
        RuntimeCoverageProbeHarness resize = new();
        RuntimeScenarioProbe resizeProbe = resize.CreateProbe("resize-and-reload");
        EnableInjection(resizeProbe);
        resize.Tick!(0.02f);
        AssertEnumField(resizeProbe, "resizeProbeState", "WaitingForAlternateSize");
        resize.Tick(0.02f);
        Assert.AreEqual(0, resize.PointLights.Count);
        resizeProbe.Dispose();

        RuntimeCoverageProbeHarness moving = new();
        RuntimeScenarioProbe movingProbe = moving.CreateProbe("moving-camera");
        EnableInjection(movingProbe);
        moving.Tick!(0.02f);
        Assert.AreEqual(1, moving.CaptureCalls);
        Assert.IsTrue(GetField<int>(movingProbe, "movingCameraTicks") > 0);
        movingProbe.Dispose();

        RuntimeCoverageProbeHarness empty = new();
        RuntimeScenarioProbe emptyProbe = empty.CreateProbe("environment-only");
        EnableInjection(emptyProbe);
        empty.Tick!(0.02f);
        Assert.AreEqual(0, empty.PointLights.Count);
        Assert.IsTrue(GetField<bool>(emptyProbe, "injected"));
        emptyProbe.Dispose();
    }

    /// <summary>
    /// Verifies the tick Reports Exterior Water And Cave Selection Failures Once regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TickReportsExteriorWaterAndCaveSelectionFailuresOnce()
    {
        foreach (string scenario in new[] { "exterior-roof", "rain-wetness", "water-reflection", "cave-interior" })
        {
            RuntimeCoverageProbeHarness harness = new();
            harness.FallbackBlockAt = (_, _, _, _) => harness.Air;
            RuntimeScenarioProbe probe = harness.CreateProbe(scenario);
            EnableInjection(probe);
            harness.Tick!(0.02f);
            int errorCount = harness.Logs.Count(static entry => entry.Level == nameof(ILogger.Error));
            Assert.IsTrue(errorCount >= 1, scenario);
            harness.Tick(0.02f);
            Assert.AreEqual(errorCount, harness.Logs.Count(static entry => entry.Level == nameof(ILogger.Error)), scenario);
            probe.Dispose();
        }
    }

    /// <summary>
    /// Verifies the tick Builds Render Lab Releases Capture Gate And Locks Its Light regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TickBuildsRenderLabReleasesCaptureGateAndLocksItsLight()
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid && y == 79 ? harness.Solid : harness.Air;
        RuntimeScenarioProbe probe = harness.CreateProbe("render-lab");
        EnableInjection(probe);

        harness.Tick!(0.02f);
        Assert.AreEqual(3, harness.PointLights.Count);
        Assert.IsTrue(GetField<bool>(probe, "renderLabBuilt"));
        Assert.IsTrue(GetField<bool>(probe, "renderLabCaptureReleased"));
        Vec3d expected = GetField<Vec3d>(probe, "renderLabLightPosition");
        Vec3d secondary = GetField<Vec3d>(probe, "renderLabSecondaryLightPosition");
        Assert.AreEqual(expected.X, harness.PointLights[0].Pos.X, 0.0001);
        Assert.AreEqual(harness.Entity.CameraPos.X + 0.28, harness.PointLights[1].Pos.X, 0.0001);
        Assert.AreEqual(secondary.X, harness.PointLights[2].Pos.X, 0.0001);
        Assert.AreEqual(10.5f, harness.PointLights[0].Color.X, 0.0001f);
        Assert.AreEqual(4.2f, harness.PointLights[1].Color.X, 0.0001f);
        Assert.AreEqual(3.4f, harness.PointLights[2].Color.X, 0.0001f);
        Assert.IsTrue(harness.Logs.Any(static entry =>
            entry.Message.Contains("Render lab light rig verified: sources=3", StringComparison.Ordinal)));
        harness.Entity.CameraPos.Set(4.0, 5.0, 6.0);
        harness.Tick(0.02f);
        Assert.AreEqual(expected.Z, harness.PointLights[0].Pos.Z, 0.0001);
        Assert.AreEqual(4.28, harness.PointLights[1].Pos.X, 0.0001);
        Assert.AreEqual(secondary.Z, harness.PointLights[2].Pos.Z, 0.0001);
        probe.Dispose();

        RuntimeCoverageProbeHarness unavailable = new();
        unavailable.ChunksAvailable = false;
        RuntimeScenarioProbe deferred = unavailable.CreateProbe("render-lab");
        EnableInjection(deferred);
        unavailable.Tick!(0.02f);
        Assert.IsFalse(GetField<bool>(deferred, "renderLabBuilt"));
        Assert.IsFalse(GetField<bool>(deferred, "injected"));
        deferred.Dispose();
    }

    /// <summary>
    /// Verifies the tick Uses Selected Cave Light And Dispose Is Idempotent regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TickUsesSelectedCaveLightAndDisposeIsIdempotent()
    {
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("cave-interior");
        EnableInjection(probe);
        SetField(probe, "exteriorPositionApplied", true);
        SetField(probe, "caveLightPositionSet", true);
        GetField<Vec3d>(probe, "caveLightPosition").Set(2.0, 3.0, 4.0);
        SetField(probe, "exteriorCameraLockTicks", 1);
        harness.Tick!(0.02f);
        Assert.AreEqual(2.0, harness.PointLights[0].Pos.X, 0.0001);
        Assert.AreEqual(0, GetField<int>(probe, "exteriorCameraLockTicks"));
        probe.Dispose();
        probe.Dispose();
        Assert.AreEqual(0, harness.PointLights.Count);
    }

    /// <summary>
    /// Executes the enable Injection step used by the deterministic runtime Coverage Probe Tick Tests fixture.
    /// </summary>
    /// <param name="probe">The probe input used to configure this deterministic test path.</param>
    private static void EnableInjection(RuntimeScenarioProbe probe)
    {
        SetField(probe, "environmentApplied", true);
        SetField(probe, "environmentVerified", true);
    }

    /// <summary>
    /// Returns field from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The get Field result consumed by the caller&apos;s assertion.</returns>
    private static T GetField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>
    /// Sets field on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private static void SetField(object instance, string fieldName, object? value)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, value);
    }

    /// <summary>
    /// Asserts enum Field and throws when the regression contract is violated.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="expected">Expected value enforced by the regression contract.</param>
    private static void AssertEnumField(object instance, string fieldName, string expected)
    {
        Assert.AreEqual(expected, GetField<object>(instance, fieldName).ToString());
    }
}
