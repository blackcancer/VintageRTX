using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Configuration;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Drives the stateful runtime probe against the deterministic in-memory client
/// so production tick, camera, resize and environment paths remain testable.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuntimeCoverageProbeBehaviorTests
{
    /// <summary>
    /// Verifies the hotbar Isolation Covers Missing Occupied And Empty Slots regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void HotbarIsolationCoversMissingOccupiedAndEmptySlots()
    {
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("water-reflection");
        harness.RemoveHotbar();
        Invoke(probe, "SelectEmptyHotbarSlot", "missing");

        DummySlot occupied = new(new ItemStack(harness.Solid));
        harness.SetHotbar(null, occupied);
        Invoke(probe, "SelectEmptyHotbarSlot", "occupied");

        DummySlot empty = new();
        harness.SetHotbar(occupied, empty);
        harness.SetOffhand(null);
        Invoke(probe, "SelectEmptyHotbarSlot", "empty-offhand-null");
        Assert.AreEqual(1, harness.ActiveHotbarSlot);

        harness.SetOffhand(new DummySlot());
        Invoke(probe, "SelectEmptyHotbarSlot", "empty-offhand-empty");
        harness.SetOffhand(occupied);
        Invoke(probe, "SelectEmptyHotbarSlot", "empty-offhand-full");

        harness.SetHotbar(occupied, empty);
        SetField(harness, "activeHotbarSlot", 0);
        Invoke(probe, "LogHeldItemReflectionMaskChallenge");
        Assert.AreEqual(0, harness.ActiveHotbarSlot, "The reflection challenge must retain the occupied active hand.");
        Assert.IsTrue(harness.Logs.Any(static entry =>
            entry.Message.Contains("held-item mask challenge retained", StringComparison.Ordinal)
            && entry.Message.Contains("active empty=False", StringComparison.Ordinal)
            && entry.Message.Contains("offhand empty=False", StringComparison.Ordinal)));

        Assert.IsTrue(harness.Logs.Count(static entry => entry.Level == nameof(ILogger.Warning)) >= 2);
        Assert.IsTrue(harness.Logs.Count(static entry => entry.Level == nameof(ILogger.Notification)) >= 3);
        probe.Dispose();
    }

    /// <summary>
    /// Verifies the moving Camera Probe Queues Sweeps Restores And Reports Queue Failure regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void MovingCameraProbeQueuesSweepsRestoresAndReportsQueueFailure()
    {
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("moving-camera");
        SetField(harness, "mouseYaw", 1.2f);
        harness.CaptureResult = false;
        Invoke(probe, "BeginMovingCameraProbe");
        Assert.AreEqual(1, harness.CaptureCalls);
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Level == nameof(ILogger.Error)));

        Invoke(probe, "UpdateMovingCameraProbe");
        Assert.AreNotEqual(0, GetField<int>(probe, "movingCameraTicks"));
        SetField(probe, "movingCameraTicks", 1);
        Invoke(probe, "UpdateMovingCameraProbe");
        Assert.AreEqual(1.2f, harness.MouseYaw, 0.0001f);
        Invoke(probe, "UpdateMovingCameraProbe");

        harness.CaptureResult = true;
        Invoke(probe, "BeginMovingCameraProbe");
        Assert.AreEqual(2, harness.CaptureCalls);
        probe.Dispose();
    }

    /// <summary>
    /// Verifies the resize Probe Covers Invalid Sizes Transitions Reload And Timeouts regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ResizeProbeCoversInvalidSizesTransitionsReloadAndTimeouts()
    {
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("resize-and-reload");

        harness.FrameWidth = 0;
        harness.FrameHeight = 1080;
        Invoke(probe, "BeginResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Failed");
        harness.FrameWidth = 1920;
        harness.FrameHeight = 0;
        Invoke(probe, "BeginResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Failed");

        harness.FrameWidth = 1920;
        harness.FrameHeight = 1080;
        Invoke(probe, "BeginResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "WaitingForAlternateSize");
        Assert.AreEqual(1600, GetField<int>(probe, "alternateFrameWidth"));
        Assert.AreEqual(900, GetField<int>(probe, "alternateFrameHeight"));

        Invoke(probe, "UpdateResizeAndReloadProbe");
        SetField(probe, "resizeProbeTicks", 251);
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Failed");

        harness.FrameWidth = 800;
        harness.FrameHeight = 600;
        Invoke(probe, "BeginResizeAndReloadProbe");
        int alternateWidth = GetField<int>(probe, "alternateFrameWidth");
        int alternateHeight = GetField<int>(probe, "alternateFrameHeight");
        Assert.AreEqual(960, alternateWidth);
        Assert.AreEqual(690, alternateHeight);
        harness.FrameWidth = alternateWidth;
        harness.FrameHeight = alternateHeight;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "HoldingAlternateSize");

        harness.FrameWidth++;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Failed");

        harness.FrameWidth = 800;
        harness.FrameHeight = 600;
        Invoke(probe, "BeginResizeAndReloadProbe");
        alternateWidth = GetField<int>(probe, "alternateFrameWidth");
        alternateHeight = GetField<int>(probe, "alternateFrameHeight");
        harness.FrameWidth = alternateWidth;
        harness.FrameHeight = alternateHeight;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        Invoke(probe, "UpdateResizeAndReloadProbe");
        SetField(probe, "resizeProbeTicks", 15);
        harness.ReloadResult = false;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Failed");

        harness.FrameWidth = 800;
        harness.FrameHeight = 600;
        Invoke(probe, "BeginResizeAndReloadProbe");
        alternateWidth = GetField<int>(probe, "alternateFrameWidth");
        alternateHeight = GetField<int>(probe, "alternateFrameHeight");
        harness.FrameWidth = alternateWidth;
        harness.FrameHeight = alternateHeight;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        SetField(probe, "resizeProbeTicks", 15);
        harness.ReloadResult = true;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "WaitingForOriginalSize");

        SetField(probe, "resizeProbeTicks", 251);
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Failed");

        SetEnumField(probe, "resizeProbeState", "WaitingForOriginalSize");
        SetField(probe, "originalFrameWidth", 800);
        SetField(probe, "originalFrameHeight", 600);
        harness.FrameWidth = 800;
        harness.FrameHeight = 600;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "HoldingOriginalSize");
        Invoke(probe, "UpdateResizeAndReloadProbe");
        SetField(probe, "resizeProbeTicks", 15);
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Complete");

        SetEnumField(probe, "resizeProbeState", "HoldingOriginalSize");
        harness.FrameWidth = 801;
        Invoke(probe, "UpdateResizeAndReloadProbe");
        AssertEnumField(probe, "resizeProbeState", "Failed");

        foreach (string terminal in new[] { "Inactive", "Complete", "Failed" })
        {
            SetEnumField(probe, "resizeProbeState", terminal);
            Invoke(probe, "UpdateResizeAndReloadProbe");
        }

        probe.Dispose();
    }

    /// <summary>
    /// Verifies the render callback resumes laboratory and real-map worlds and applies both camera lock modes.
    /// </summary>
    [TestMethod]
    public void RenderCallbackResumesLabAndAppliesBothCameraLockModes()
    {
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("render-lab");
        probe.OnRenderFrame(0.016f, EnumRenderStage.AfterBlit);

        harness.IsPaused = true;
        probe.OnRenderFrame(0.016f, EnumRenderStage.Before);
        Assert.AreEqual(1, harness.PauseCalls);
        probe.OnRenderFrame(0.016f, EnumRenderStage.Before);
        Assert.AreEqual(1, harness.PauseCalls);

        RuntimeCoverageProbeHarness mapHarness = new() { IsPaused = true };
        using RuntimeScenarioProbe mapProbe = mapHarness.CreateProbe("lantern-night");
        mapProbe.OnRenderFrame(0.016f, EnumRenderStage.Before);
        Assert.AreEqual(1, mapHarness.PauseCalls);

        SetField(probe, "exteriorCameraLockTicks", 5);
        SetField(probe, "exteriorYaw", 0.7f);
        SetField(probe, "exteriorPitch", -0.3f);
        SetField(probe, "exteriorEntityPitch", 5.9f);
        SetField(probe, "exteriorPositionLocked", false);
        SetField(probe, "useDirectPublicCameraAngles", false);
        probe.OnRenderFrame(0.016f, EnumRenderStage.Before);
        Assert.AreEqual(0.7f, harness.MouseYaw, 0.0001f);

        SetField(probe, "exteriorPositionLocked", true);
        SetField(probe, "exteriorPositionX", 4.0);
        SetField(probe, "exteriorPositionY", 5.0);
        SetField(probe, "exteriorPositionZ", 6.0);
        SetField(probe, "useDirectPublicCameraAngles", true);
        Invoke(probe, "ApplyLockedCameraState");
        Assert.AreEqual(4.0, harness.Entity.Pos.X, 0.001);
        Assert.AreEqual(0.7f, harness.CameraYaw, 0.0001f);
        Assert.AreEqual(-0.3f, harness.CameraPitch, 0.0001f);
        Assert.AreEqual(0.0f, harness.CameraRoll, 0.0001f);

        harness.PlayerAvailable = false;
        probe.OnRenderFrame(0.016f, EnumRenderStage.Before);
        probe.Dispose();
    }

    /// <summary>Verifies the real-map lantern camera locates only active lanterns and issues a stable teleport.</summary>
    [TestMethod]
    public void LanternNightCameraFindsLitAuthoredBlockAndClearView()
    {
        RuntimeCoverageProbeHarness harness = new();
        TestLightBlock unlit = new("game:lantern-large-up", 0);
        TestLightBlock lit = new("game:lantern-large-up", 20);
        TestLightBlock torch = new("game:torch-up", 20);
        BlockPos sample = new(2, 80, 0);

        Assert.IsFalse(RuntimeScenarioProbe.IsLitLantern(null!, harness.BlockAccessor, sample));
        Assert.IsFalse(RuntimeScenarioProbe.IsLitLantern(torch, harness.BlockAccessor, sample));
        Assert.IsFalse(RuntimeScenarioProbe.IsLitLantern(unlit, harness.BlockAccessor, sample));
        Assert.IsTrue(RuntimeScenarioProbe.IsLitLantern(lit, harness.BlockAccessor, sample));

        harness.SetBlock(sample.X, sample.Y, sample.Z, lit);
        using RuntimeScenarioProbe probe = harness.CreateProbe("lantern-night");
        Assert.IsTrue((bool)Invoke(probe, "TryApplyLanternNightCamera")!);
        Assert.IsTrue(harness.ChatMessages.Any(static message => message.StartsWith("/tp =", StringComparison.Ordinal)));
        Assert.IsTrue(harness.Logs.Any(static entry => entry.Message.Contains(
            "Lantern night camera applied",
            StringComparison.Ordinal)));
        Assert.IsTrue(GetField<bool>(probe, "exteriorPositionLocked"));
    }

    /// <summary>
    /// Proves the local-body mirror proof waits for earlier captures, owns only one named entity
    /// carrier, and restores the long-range lake pose at the first render boundary after readback.
    /// </summary>
    [TestMethod]
    public void LocalBodyMirrorCaptureUsesTemporaryPhysicalPoseAndRestoresWideView()
    {
        CollectionAssert.Contains(
            ScenarioCatalog.Get("water-reflection").RequiredLogTokens,
            "Local-body entity-mirror capture completed");
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("water-reflection");
        SetField(probe, "waterImpactCommandIndex", 3);
        SetField(probe, "waterLocalBodyCaptureDelayTicks", 249);
        SetField(probe, "exteriorYaw", 1.1f);
        SetField(probe, "exteriorPitch", -2.7f);
        SetField(probe, "exteriorEntityPitch", 3.5f);
        SetField(probe, "waterLocalBodyYaw", 1.2f);
        SetField(probe, "waterLocalBodyPitch", -2.0f);
        SetField(probe, "waterLocalBodyEntityPitch", 4.2f);
        SetField(probe, "useDirectPublicCameraAngles", true);

        harness.DiagnosticCaptureIdle = false;
        Invoke(probe, "UpdateLocalBodyMirrorCapture");
        Assert.AreEqual(0, harness.DiagnosticCaptures.Count);
        AssertEnumField(probe, "waterLocalBodyCaptureState", "Inactive");

        harness.DiagnosticCaptureIdle = true;
        harness.DiagnosticCaptureResult = false;
        Invoke(probe, "UpdateLocalBodyMirrorCapture");
        Assert.AreEqual(1.1f, harness.CameraYaw, 0.0001f);
        Assert.AreEqual(-2.7f, harness.CameraPitch, 0.0001f);
        AssertEnumField(probe, "waterLocalBodyCaptureState", "Inactive");

        harness.DiagnosticCaptureResult = true;
        Invoke(probe, "UpdateLocalBodyMirrorCapture");
        Assert.AreEqual(
            ("local-body-entity-mirror", VintageRtxDebugView.EntityMirror),
            harness.DiagnosticCaptures.Single());
        Assert.AreEqual(1.2f, harness.CameraYaw, 0.0001f);
        Assert.AreEqual(-2.0f, harness.CameraPitch, 0.0001f);
        AssertEnumField(probe, "waterLocalBodyCaptureState", "Capturing");

        harness.DiagnosticCaptureIdle = false;
        probe.OnRenderFrame(0.016f, EnumRenderStage.Before);
        Assert.AreEqual(1.2f, harness.CameraYaw, 0.0001f);
        harness.DiagnosticCaptureIdle = true;
        probe.OnRenderFrame(0.016f, EnumRenderStage.Before);
        Assert.AreEqual(1.1f, harness.CameraYaw, 0.0001f);
        Assert.AreEqual(-2.7f, harness.CameraPitch, 0.0001f);
        AssertEnumField(probe, "waterLocalBodyCaptureState", "Complete");
        Assert.IsTrue(harness.Logs.Any(static entry =>
            entry.Message.Contains("wide-lake camera restored", StringComparison.Ordinal)));
        probe.Dispose();
    }

    /// <summary>Verifies that real-map lantern framing rejects an animal volume near the eye.</summary>
    [TestMethod]
    public void LanternNightCameraAvoidsNearbyAgentOcclusion()
    {
        RuntimeCoverageProbeHarness harness = new();
        BlockPos lantern = new(2, 80, 0);
        harness.SetBlock(lantern.X, lantern.Y, lantern.Z, harness.Solid);

        Vec3d? unobstructed = RuntimeScenarioProbe.FindLanternCameraEye(
            harness.BlockAccessor,
            lantern);
        Assert.IsNotNull(unobstructed);
        LanternCameraEntityBounds body = new(
            unobstructed.X - 0.4,
            unobstructed.Y - 0.8,
            unobstructed.Z - 0.7,
            unobstructed.X + 1.6,
            unobstructed.Y + 0.8,
            unobstructed.Z + 0.7);
        Assert.IsFalse(RuntimeScenarioProbe.IsLanternCameraEntityClear(
            unobstructed.X,
            unobstructed.Y,
            unobstructed.Z,
            [body]));

        Vec3d? displaced = RuntimeScenarioProbe.FindLanternCameraEye(
            harness.BlockAccessor,
            lantern,
            [body]);
        Assert.IsNotNull(displaced);
        Assert.AreNotEqual(unobstructed, displaced);
        Assert.IsTrue(RuntimeScenarioProbe.IsLanternCameraEntityClear(
            displaced.X,
            displaced.Y,
            displaced.Z,
            [body]));
        Assert.IsTrue(RuntimeScenarioProbe.IsLanternCameraEntityClear(
            displaced.X,
            displaced.Y,
            displaced.Z,
            null));

        EntityPlayer entity = new();
        LanternCameraEntityBounds fallbackBounds = LanternCameraEntityBounds.FromEntity(entity);
        Assert.AreEqual(-0.5, fallbackBounds.MinimumX, 0.0001);
        Assert.AreEqual(2.0, fallbackBounds.MaximumY, 0.0001);
        entity.Pos.SetPos(10.0, 20.0, 30.0);
        entity.SetSelectionBox(2.0f, 3.0f);
        LanternCameraEntityBounds entityBounds = LanternCameraEntityBounds.FromEntity(entity);
        Assert.AreEqual(9.0, entityBounds.MinimumX, 0.0001);
        Assert.AreEqual(23.0, entityBounds.MaximumY, 0.0001);
        Assert.AreEqual(0.0, entityBounds.SquaredDistanceTo(10.0, 21.0, 30.0), 0.0001);
        Assert.AreEqual(1.0, entityBounds.SquaredDistanceTo(12.0, 21.0, 30.0), 0.0001);
    }

    /// <summary>
    /// Verifies the camera Solver And Calibration Cover Direct Coarse Fine And Matrix Guards regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CameraSolverAndCalibrationCoverDirectCoarseFineAndMatrixGuards()
    {
        RuntimeCoverageProbeHarness harness = new();
        RuntimeScenarioProbe probe = harness.CreateProbe("cave-interior");
        Invoke(probe, "SelectCameraOrientation", 0.0, 0.0, 0.0, true);
        AssertEnumField(probe, "cameraCalibrationPhase", "Inactive");
        Invoke(probe, "SelectCameraOrientation", 4.0, -2.0, 7.0, false);
        AssertEnumField(probe, "cameraCalibrationPhase", "PitchCoarse");

        harness.CameraMatrix = new double[8];
        SetField(probe, "cameraCalibrationCandidatePending", true);
        SetField(probe, "cameraCalibrationHoldTicks", 2);
        Invoke(probe, "UpdateCameraOrientationCalibration");

        harness.CameraMatrix = new double[16];
        harness.CameraMatrix[10] = -1.0;
        for (int iteration = 0; iteration < 400; iteration++)
        {
            Invoke(probe, "UpdateCameraOrientationCalibration");
        }
        AssertEnumField(probe, "cameraCalibrationPhase", "Inactive");
        Invoke(probe, "UpdateCameraOrientationCalibration");

        SetField(probe, "cameraCalibrationBestAlignment", double.MaxValue);
        Invoke(probe, "EvaluateCameraCalibrationCandidate");
        SetEnumField(probe, "cameraCalibrationPhase", "Inactive");
        Invoke(probe, "AdvanceCameraCalibrationPhase");

        FieldInfo phase = probe.GetType().GetField(
            "cameraCalibrationPhase",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        phase.SetValue(probe, Enum.ToObject(phase.FieldType, 99));
        SetField(probe, "cameraCalibrationStep", 0);
        SetField(probe, "cameraCalibrationCandidatePending", false);
        Invoke(probe, "UpdateCameraOrientationCalibration");
        Assert.AreEqual(1, GetField<int>(probe, "cameraCalibrationStep"));
        probe.Dispose();
    }

    /// <summary>
    /// Verifies the environment Commands And Verification Cover Pass Retry And Terminal Failure regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void EnvironmentCommandsAndVerificationCoverPassRetryAndTerminalFailure()
    {
        RuntimeCoverageProbeHarness north = new();
        north.Hemisphere = EnumHemisphere.North;
        RuntimeScenarioProbe night = north.CreateProbe("lantern-night", 23.0f, clearWeather: true);
        Invoke(night, "ApplyDeterministicEnvironment");
        CollectionAssert.Contains(north.ChatMessages, "/time setmonth jan");
        CollectionAssert.Contains(north.ChatMessages, "/time set 23");
        CollectionAssert.Contains(north.ChatMessages, "/time stop");
        CollectionAssert.Contains(north.ChatMessages, "/weather setir clearsky");
        CollectionAssert.Contains(north.ChatMessages, "/weather setprecip -1");

        RuntimeScenarioProbe northDay = north.CreateProbe("exterior-roof", 10.0f, clearWeather: true);
        Invoke(northDay, "ApplyDeterministicEnvironment");
        CollectionAssert.Contains(north.ChatMessages, "/time setmonth jul");

        north.HourOfDay = 23.0f;
        north.SpeedOfTime = 0.0f;
        north.DaylightStrength = 0.0f;
        north.SunDirection = new Vec3f(0.2f, -0.8f, 0.3f);
        north.Climate = new ClimateCondition { Rainfall = 0, RainCloudOverlay = 0 };
        Invoke(night, "VerifyDeterministicEnvironment");
        Assert.IsTrue(GetField<bool>(night, "environmentVerified"));

        RuntimeCoverageProbeHarness south = new();
        south.Hemisphere = EnumHemisphere.South;
        RuntimeScenarioProbe southDay = south.CreateProbe("exterior-roof", 10.0f, clearWeather: true);
        Invoke(southDay, "ApplyDeterministicEnvironment");
        CollectionAssert.Contains(south.ChatMessages, "/time setmonth jan");
        RuntimeScenarioProbe rainy = south.CreateProbe("nonstandard-geometry", 6.0f, precipitation: 0.7f);
        Invoke(rainy, "ApplyDeterministicEnvironment");
        CollectionAssert.Contains(south.ChatMessages, "/time setmonth jul");
        CollectionAssert.Contains(south.ChatMessages, "/weather setprecip 0.7");

        south.HourOfDay = 8.0f;
        south.SpeedOfTime = 1.0f;
        south.DaylightStrength = 1.0f;
        south.Climate = new ClimateCondition { Rainfall = 0.0f, RainCloudOverlay = 1.0f };
        Invoke(rainy, "VerifyDeterministicEnvironment");
        Assert.IsFalse(GetField<bool>(rainy, "environmentVerified"));
        Assert.IsTrue(south.ChatMessages.Count > 4);

        SetField(rainy, "environmentVerificationAttempts", 19);
        Invoke(rainy, "VerifyDeterministicEnvironment");
        Assert.IsTrue(south.Logs.Any(static entry => entry.Level == nameof(ILogger.Error)));

        south.PlayerAvailable = false;
        Invoke(rainy, "ApplyDeterministicEnvironment");
        northDay.Dispose();
        southDay.Dispose();
        night.Dispose();
        rainy.Dispose();
    }

    /// <summary>
    /// Verifies the environment Verification Covers Every Clock And Weather Lock Combination regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void EnvironmentVerificationCoversEveryClockAndWeatherLockCombination()
    {
        RuntimeCoverageProbeHarness unconstrained = new();
        RuntimeScenarioProbe passWithoutHour = unconstrained.CreateProbe("environment-only");
        Invoke(passWithoutHour, "VerifyDeterministicEnvironment");
        Assert.IsTrue(GetField<bool>(passWithoutHour, "environmentVerified"));
        Assert.IsTrue(unconstrained.Logs.Any(static entry =>
            entry.Message.Contains("requested hour=unchanged", StringComparison.Ordinal)));
        passWithoutHour.Dispose();

        VerifyPendingClearWeather(float.NaN, 0.0f, "non-finite precipitation");
        VerifyPendingClearWeather(0.0f, float.NaN, "non-finite overlay");
        VerifyPendingClearWeather(0.2f, 0.0f, "wet precipitation");
        VerifyPendingClearWeather(0.0f, 0.2f, "cloud overlay");

        RuntimeCoverageProbeHarness night = new();
        night.DaylightStrength = 1.0f;
        night.DirectSunLightStrength = 1.0f;
        night.Climate = new ClimateCondition { Rainfall = 0.0f, RainCloudOverlay = 0.0f };
        RuntimeScenarioProbe nightProbe = night.CreateProbe("lantern-night", clearWeather: true);
        Invoke(nightProbe, "VerifyDeterministicEnvironment");
        Assert.IsFalse(GetField<bool>(nightProbe, "environmentVerified"));
        Assert.AreEqual(0, night.ChatMessages.Count);
        nightProbe.Dispose();

        RuntimeCoverageProbeHarness runningClock = new();
        runningClock.HourOfDay = 8.0f;
        runningClock.SpeedOfTime = 1.0f;
        RuntimeScenarioProbe runningProbe = runningClock.CreateProbe("environment-only", hour: 8.0f);
        Invoke(runningProbe, "VerifyDeterministicEnvironment");
        Assert.IsFalse(GetField<bool>(runningProbe, "environmentVerified"));
        runningProbe.Dispose();

        RuntimeCoverageProbeHarness settlingDay = new();
        settlingDay.HourOfDay = 10.0f;
        settlingDay.SpeedOfTime = 0.0f;
        settlingDay.SunDirection = new Vec3f(0.2f, -0.6f, 0.2f);
        settlingDay.Climate = new ClimateCondition { Rainfall = 0.0f, RainCloudOverlay = 0.0f };
        RuntimeScenarioProbe settlingProbe = settlingDay.CreateProbe(
            "exterior-roof",
            hour: 10.0f,
            clearWeather: true);
        Invoke(settlingProbe, "VerifyDeterministicEnvironment");
        // The hour and effective stopped rate already match. A stale client
        // solar cache must settle without resuming or repeatedly resetting the
        // authoritative 1.22.7 calendar.
        Assert.AreEqual(0, settlingDay.ChatMessages.Count);
        settlingDay.SpeedOfTime = 60.0f;
        settlingDay.SunDirection = new Vec3f(0.2f, 0.2f, 0.2f);
        settlingDay.DaylightStrength = 1.0f;
        settlingDay.DirectSunLightStrength = 1.0f;
        settlingDay.MoonLightStrength = 0.0f;
        Invoke(settlingProbe, "VerifyDeterministicEnvironment");
        CollectionAssert.Contains(settlingDay.ChatMessages, "/time stop");
        settlingDay.SpeedOfTime = 0.0f;
        Invoke(settlingProbe, "VerifyDeterministicEnvironment");
        Assert.IsTrue(GetField<bool>(settlingProbe, "environmentVerified"));
        settlingProbe.Dispose();

        RuntimeCoverageProbeHarness lockedClockBadWeather = new();
        lockedClockBadWeather.HourOfDay = 8.0f;
        lockedClockBadWeather.SpeedOfTime = 0.0f;
        lockedClockBadWeather.Climate = new ClimateCondition { Rainfall = 0.2f, RainCloudOverlay = 0.0f };
        RuntimeScenarioProbe lockedProbe = lockedClockBadWeather.CreateProbe(
            "environment-only",
            hour: 8.0f,
            clearWeather: true);
        Invoke(lockedProbe, "VerifyDeterministicEnvironment");
        Assert.IsFalse(GetField<bool>(lockedProbe, "environmentVerified"));
        lockedProbe.Dispose();
    }

    /// <summary>
    /// Executes the verify Pending Clear Weather step used by the deterministic runtime Coverage Probe Behavior Tests fixture.
    /// </summary>
    /// <param name="rainfall">The rainfall input used to configure this deterministic test path.</param>
    /// <param name="overlay">Coordinate component in the space defined by the tested API.</param>
    /// <param name="because">The because input used to configure this deterministic test path.</param>
    private static void VerifyPendingClearWeather(float rainfall, float overlay, string because)
    {
        RuntimeCoverageProbeHarness harness = new();
        harness.Climate = new ClimateCondition { Rainfall = rainfall, RainCloudOverlay = overlay };
        RuntimeScenarioProbe probe = harness.CreateProbe("environment-only", clearWeather: true);
        Invoke(probe, "VerifyDeterministicEnvironment");
        Assert.IsFalse(GetField<bool>(probe, "environmentVerified"), because);
        Assert.IsTrue(harness.Logs.Any(static entry =>
            entry.Message.Contains("requested hour=unchanged", StringComparison.Ordinal)), because);
        probe.Dispose();
    }

    /// <summary>
    /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="methodName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The invoke result consumed by the caller&apos;s assertion.</returns>
    private static object? Invoke(object instance, string methodName, params object?[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
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
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
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
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, value);
    }

    /// <summary>
    /// Sets enum Field on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private static void SetEnumField(object instance, string fieldName, string value)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, Enum.Parse(field.FieldType, value));
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

    /// <summary>Minimal collectible whose emitted HSV value is controlled by the lantern-camera fixture.</summary>
    private sealed class TestLightBlock : Block
    {
        private readonly byte value;

        /// <summary>Creates a block with a stable code, identifier, and emitted-value channel.</summary>
        /// <param name="code">Full asset identifier.</param>
        /// <param name="value">HSV emission value.</param>
        internal TestLightBlock(string code, byte value)
        {
            Code = new AssetLocation(code);
            this.value = value;
        }

        /// <summary>Returns the fixture-controlled warm-light HSV payload.</summary>
        /// <param name="blockAccessor">Unused accessor required by the game API.</param>
        /// <param name="pos">Unused position required by the game API.</param>
        /// <param name="stack">Optional placed item state.</param>
        /// <returns>A three-channel HSV emission value.</returns>
        public override byte[] GetLightHsv(
            IBlockAccessor blockAccessor,
            BlockPos pos,
            ItemStack? stack = null) => [24, 7, value];
    }
}
