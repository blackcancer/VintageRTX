using System.Globalization;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageRTX.Testing;

/// <summary>
/// Opt-in runtime probes used only when VintageRTX.Test supplies an environment scenario.
/// They exercise the same public dynamic-light API used by held torches and lanterns.
/// </summary>
internal sealed class RuntimeScenarioProbe : IRenderer
{
    /// <summary>
    /// Number of 20-ms game ticks used for the temporal-stability sweep.
    /// The resulting 4.8-second motion is long enough to expose history
    /// ghosting without keeping an automated run moving indefinitely.
    /// </summary>
    private const int MovingCameraDurationTicks = 240;

    private readonly ICoreClientAPI api;
    private readonly string scenario;
    private readonly float? requestedWorldHour;
    private readonly bool clearWeather;
    private readonly float? requestedPrecipitation;
    private readonly Func<bool> reloadShaders;
    private readonly Func<bool> queueCapture;
    private readonly Func<bool> isStartupWorldStateReady;
    private readonly System.Func<string, VintageRtxDebugView, bool>? queueDiagnosticCapture;
    private readonly Func<bool> isDiagnosticCaptureIdle;
    private readonly Action<int, BlockPos, int?>? stageRenderLabBlock;
    private readonly Action? commitRenderLabBlocks;
    private readonly System.Func<BlockPos, bool>? isRenderLabChunkAvailable;
    private readonly System.Func<BlockPos, int, Block>? getRenderLabBlock;
    private readonly System.Func<BlockPos, int>? getRenderLabRainHeight;
    private readonly List<TestPointLight> lights = [];
    private readonly Vec3d caveLightPosition = new();
    private readonly Vec3d renderLabLightPosition = new();
    private readonly Vec3d renderLabSecondaryLightPosition = new();
    /// <summary>Stock plant layout mirrored by the isolated server-side placement command.</summary>
    private static readonly VegetationWitness[] VegetationWitnesses =
    [
        new(-3, -1, "game:tallgrass-verytall-free"),
        new(-1, 1, "game:tallgrass-tall-free"),
        new(1, -1, "game:tallgrass-medium-free"),
        new(3, 1, "game:flower-redtopgrass-free"),
        new(0, 2, "game:fern-eaglefern")
    ];
    private long tickListenerId;
    private int readyTicks;
    private bool environmentApplied;
    private bool environmentVerified;
    private int environmentVerificationAttempts;
    private bool injected;
    private bool isolatedHandsApplied;
    private bool heldItemMaskChallengeLogged;
    private bool exteriorPositionApplied;
    private bool exteriorPositionAttempted;
    private int lanternCameraSearchTicks;
    private int lanternCameraSearchAttempts;
    /// <summary>Whether the server was asked to remove moving entities from the copied lantern room.</summary>
    private bool lightStabilityIsolationRequested;
    /// <summary>Ticks allowed for authoritative removals to reach the client before captures begin.</summary>
    private int lightStabilityIsolationSettleTicks;
    private bool testCaptureReleased;
    private int exteriorCameraLockTicks;
    private float exteriorYaw;
    private float exteriorPitch;
    private float exteriorEntityPitch;
    private bool useDirectPublicCameraAngles;
    private CameraCalibrationPhase cameraCalibrationPhase;
    private int cameraCalibrationStep;
    private bool cameraCalibrationCandidatePending;
    private int cameraCalibrationHoldTicks;
    private double cameraCalibrationBestAlignment;
    private float cameraCalibrationBestYaw;
    private float cameraCalibrationBestPitch;
    private float cameraCalibrationCoarsePitch;
    private readonly Vec3d cameraCalibrationDesired = new();
    private readonly Vec3d cameraCalibrationBestForward = new();
    private bool exteriorPositionLocked;
    private bool caveLightPositionSet;
    private bool renderLabBuilt;
    /// <summary>Whether the real client was moved to the fixed world-centre laboratory anchor.</summary>
    private bool renderLabAnchorRequested;
    private bool renderLabLightPositionSet;
    private bool runtimePauseReleaseLogged;
    private bool renderLabCaptureReleased;
    private double exteriorPositionX;
    private double exteriorPositionY;
    private double exteriorPositionZ;
    private readonly Vec3d[] waterImpactLandingPositions = [new(), new(), new()];
    private readonly Vec3d[] waterProjectileLandingPositions = [new(), new()];
    private bool waterImpactTargetSet;
    private int waterImpactTicks;
    private int waterImpactCommandIndex;
    private int waterProjectileTicks;
    private int waterProjectileCommandIndex;
    private int waterProjectileBaselinePhase;
    private bool waterProjectileBaselineQueued;
    /// <summary>Post-impact tick hold that separates the body proof from asynchronous wave captures.</summary>
    private int waterLocalBodyCaptureDelayTicks;
    /// <summary>Current ownership state of the temporary physically reflected body camera.</summary>
    private LocalBodyMirrorCaptureState waterLocalBodyCaptureState;
    /// <summary>Near-water camera yaw whose mirrored frustum contains the local world body.</summary>
    private float waterLocalBodyYaw;
    /// <summary>Near-water public camera pitch whose mirrored frustum contains the body.</summary>
    private float waterLocalBodyPitch;
    /// <summary>Periodic entity pitch paired with <see cref="waterLocalBodyPitch"/>.</summary>
    private float waterLocalBodyEntityPitch;
    /// <summary>Long-range lake yaw restored after the dedicated body carrier reaches disk.</summary>
    private float waterWideYaw;
    /// <summary>Long-range public lake pitch restored after the dedicated body carrier reaches disk.</summary>
    private float waterWidePitch;
    /// <summary>Periodic entity pitch paired with <see cref="waterWidePitch"/>.</summary>
    private float waterWideEntityPitch;
    private ResizeProbeState resizeProbeState;
    private int resizeProbeTicks;
    private int originalFrameWidth;
    private int originalFrameHeight;
    private int alternateFrameWidth;
    private int alternateFrameHeight;
    private int movingCameraTicks;
    private float movingCameraBaseYaw;
    private int vegetationPlacementTicks;
    private bool vegetationPlacementArmed;
    private bool vegetationPlacementRequested;
    private bool vegetationPatchVerified;
    private int vegetationCenterX;
    private int vegetationPlantY;
    private int vegetationCenterZ;
    /// <summary>Authoritative per-witness plant elevations sampled before server placement.</summary>
    private readonly int[] vegetationWitnessPlantYs = new int[VegetationWitnesses.Length];

    /// <summary>
    /// Gets whether the selected scenario owns and stages the synthetic render
    /// laboratory; this also controls the capture-ready environment gate.
    /// </summary>
    private bool IsRenderLab => scenario == "render-lab";

    /// <summary>Gets whether this probe stages plants in a disposable copy of a real save.</summary>
    private bool IsVegetationShadowMap => scenario == "vegetation-shadow-map";

    /// <summary>
    /// Installs a scenario controller into an already initialized client world.
    /// The controller owns its renderer registration, tick listener and any
    /// point lights it creates until <see cref="Dispose"/> is called.
    /// </summary>
    /// <param name="api">Client API whose world and renderer are being tested.</param>
    /// <param name="scenario">Normalized scenario identifier, or an empty string for environment-only validation.</param>
    /// <param name="requestedWorldHour">Optional in-game hour on the circular 0-to-24-hour clock.</param>
    /// <param name="clearWeather">Whether precipitation and the active weather pattern must be cleared.</param>
    /// <param name="requestedPrecipitation">Optional normalized precipitation intensity in the inclusive range [0, 1].</param>
    /// <param name="reloadShaders">Callback that recompiles the active display shader and reports success.</param>
    /// <param name="queueCapture">Callback that queues a frame capture and reports whether the request was accepted.</param>
    /// <param name="isStartupWorldStateReady">Gate that prevents environment or camera changes before startup game mode is confirmed.</param>
    /// <param name="queueDiagnosticCapture">Optional named-channel capture callback used by projectile baselines.</param>
    /// <param name="isDiagnosticCaptureIdle">Reports whether the named capture transaction reached stable storage.</param>
    /// <param name="stageRenderLabBlock">Optional direct scene writer used by deterministic tests.</param>
    /// <param name="commitRenderLabBlocks">Optional direct commit callback paired with the scene writer.</param>
    /// <param name="isRenderLabChunkAvailable">Optional direct chunk-availability reader used by deterministic tests.</param>
    /// <param name="getRenderLabBlock">Optional direct block reader used by deterministic tests.</param>
    /// <param name="getRenderLabRainHeight">Optional direct rain-map reader used by deterministic tests.</param>
    private RuntimeScenarioProbe(
        ICoreClientAPI api,
        string scenario,
        float? requestedWorldHour,
        bool clearWeather,
        float? requestedPrecipitation,
        Func<bool> reloadShaders,
        Func<bool> queueCapture,
        Func<bool> isStartupWorldStateReady,
        System.Func<string, VintageRtxDebugView, bool>? queueDiagnosticCapture,
        Func<bool>? isDiagnosticCaptureIdle,
        Action<int, BlockPos, int?>? stageRenderLabBlock,
        Action? commitRenderLabBlocks,
        System.Func<BlockPos, bool>? isRenderLabChunkAvailable,
        System.Func<BlockPos, int, Block>? getRenderLabBlock,
        System.Func<BlockPos, int>? getRenderLabRainHeight)
    {
        this.api = api;
        this.scenario = scenario;
        this.requestedWorldHour = requestedWorldHour;
        this.clearWeather = clearWeather;
        this.requestedPrecipitation = requestedPrecipitation;
        this.reloadShaders = reloadShaders;
        this.queueCapture = queueCapture;
        this.isStartupWorldStateReady = isStartupWorldStateReady;
        this.queueDiagnosticCapture = queueDiagnosticCapture;
        this.isDiagnosticCaptureIdle = isDiagnosticCaptureIdle ?? (static () => true);
        this.stageRenderLabBlock = stageRenderLabBlock;
        this.commitRenderLabBlocks = commitRenderLabBlocks;
        this.isRenderLabChunkAvailable = isRenderLabChunkAvailable;
        this.getRenderLabBlock = getRenderLabBlock;
        this.getRenderLabRainHeight = getRenderLabRainHeight;
        api.Logger?.Notification(
            "[VintageRTX.Test] Runtime world seed: {0}.",
            api.World?.Seed ?? 0);
        if (IsRenderLab)
        {
            // FrameCaptureService is constructed before this probe. Keep its
            // automatic sequence gated until the deterministic room, camera
            // and point light have all been installed.
            Environment.SetEnvironmentVariable("VINTAGERTX_RENDER_LAB_READY", "0");
        }
        if (IsVegetationShadowMap)
        {
            // FrameCaptureService already exists at this point and reads the gate dynamically.
            // Keep its default diagnostic sequence blocked until server placement, client-side
            // geometry verification, and the locked exterior camera all agree.
            Environment.SetEnvironmentVariable("VINTAGERTX_VEGETATION_MAP_READY", "0");
        }
        tickListenerId = api.Event.RegisterGameTickListener(OnTick, 20);
        api.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintagertx-test-camera-lock");
    }

    /// <summary>
    /// Gets an order preceding Vintage Story's documented order-zero camera
    /// renderer so every rendered frame observes the locked test pose.
    /// </summary>
    public double RenderOrder => -1.0;

    /// <summary>
    /// Gets zero because this global validation renderer is not culled by a
    /// world-space range around the player.
    /// </summary>
    public int RenderRange => 0;

    /// <summary>
    /// Creates the environment-selected runtime probe when at least one test
    /// behavior is enabled. Normal gameplay remains untouched when no opt-in
    /// variable is present.
    /// </summary>
    /// <param name="api">Initialized client API for the world under test.</param>
    /// <param name="reloadShaders">Callback used by the resize/reload scenario.</param>
    /// <param name="queueCapture">Callback used by scenarios that require a recorded frame.</param>
    /// <param name="isStartupWorldStateReady">Optional gate used to wait for confirmed startup game mode.</param>
    /// <param name="queueDiagnosticCapture">Optional named-channel capture callback used by projectile baselines.</param>
    /// <param name="isDiagnosticCaptureIdle">Optional named-capture completion gate.</param>
    /// <returns>A registered probe, or <see langword="null"/> when runtime testing is disabled.</returns>
    public static RuntimeScenarioProbe? TryStart(
        ICoreClientAPI api,
        Func<bool> reloadShaders,
        Func<bool> queueCapture,
        Func<bool>? isStartupWorldStateReady = null,
        System.Func<string, VintageRtxDebugView, bool>? queueDiagnosticCapture = null,
        Func<bool>? isDiagnosticCaptureIdle = null)
    {
        return TryStart(
            api,
            reloadShaders,
            queueCapture,
            Environment.GetEnvironmentVariable,
            isStartupWorldStateReady: isStartupWorldStateReady,
            queueDiagnosticCapture: queueDiagnosticCapture,
            isDiagnosticCaptureIdle: isDiagnosticCaptureIdle);
    }

    /// <summary>
    /// Starts an opt-in probe using an injectable environment reader. Keeping
    /// parsing outside the game API makes malformed CI input independently
    /// verifiable without constructing a world or an OpenGL context.
    /// </summary>
    /// <param name="api">Initialized client API for the world under test.</param>
    /// <param name="reloadShaders">Callback used by the resize/reload scenario.</param>
    /// <param name="queueCapture">Callback used by scenarios that require a recorded frame.</param>
    /// <param name="readEnvironment">Injectable environment-variable reader.</param>
    /// <param name="stageRenderLabBlock">Optional direct writer for the in-memory test world.</param>
    /// <param name="commitRenderLabBlocks">Optional direct commit notification for the in-memory test world.</param>
    /// <param name="isRenderLabChunkAvailable">Optional direct chunk-availability reader for the in-memory test world.</param>
    /// <param name="getRenderLabBlock">Optional direct block reader for the in-memory test world.</param>
    /// <param name="getRenderLabRainHeight">Optional direct rain-map reader for the in-memory test world.</param>
    /// <param name="isStartupWorldStateReady">Optional gate checked before the scenario advances its first tick.</param>
    /// <param name="queueDiagnosticCapture">Optional named-channel capture callback used by projectile baselines.</param>
    /// <param name="isDiagnosticCaptureIdle">Optional named-capture completion gate.</param>
    /// <returns>A registered probe, or <see langword="null"/> when runtime testing is disabled.</returns>
    internal static RuntimeScenarioProbe? TryStart(
        ICoreClientAPI api,
        Func<bool> reloadShaders,
        Func<bool> queueCapture,
        System.Func<string, string?> readEnvironment,
        Action<int, BlockPos, int?>? stageRenderLabBlock = null,
        Action? commitRenderLabBlocks = null,
        System.Func<BlockPos, bool>? isRenderLabChunkAvailable = null,
        System.Func<BlockPos, int, Block>? getRenderLabBlock = null,
        System.Func<BlockPos, int>? getRenderLabRainHeight = null,
        Func<bool>? isStartupWorldStateReady = null,
        System.Func<string, VintageRtxDebugView, bool>? queueDiagnosticCapture = null,
        Func<bool>? isDiagnosticCaptureIdle = null)
    {
        RuntimeScenarioSettings settings = ReadSettings(readEnvironment);
        if (!settings.Enabled)
        {
            return null;
        }

        return new RuntimeScenarioProbe(
            api,
            settings.Scenario,
            settings.RequestedWorldHour,
            settings.ClearWeather,
            settings.RequestedPrecipitation,
            reloadShaders,
            queueCapture,
            isStartupWorldStateReady ?? (static () => true),
            queueDiagnosticCapture,
            isDiagnosticCaptureIdle,
            stageRenderLabBlock,
            commitRenderLabBlocks,
            isRenderLabChunkAvailable,
            getRenderLabBlock,
            getRenderLabRainHeight);
    }

    /// <summary>
    /// Normalizes the environment contract used by runtime validation. Invalid
    /// numeric values deliberately mean "leave unchanged"; precipitation is
    /// physically bounded before it reaches the game command layer.
    /// </summary>
    internal static RuntimeScenarioSettings ReadSettings(System.Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        string? requested = readEnvironment("VINTAGERTX_TEST_SCENARIO");
        string? hourText = readEnvironment("VINTAGERTX_TEST_TIME_HOUR");
        float? hour = float.TryParse(
            hourText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float parsedHour)
            && float.IsFinite(parsedHour)
            ? parsedHour
            : null;
        bool clearWeather = string.Equals(
            readEnvironment("VINTAGERTX_TEST_CLEAR_WEATHER"),
            "1",
            StringComparison.Ordinal);
        string? precipitationText = readEnvironment("VINTAGERTX_TEST_PRECIPITATION");
        float? precipitation = float.TryParse(
            precipitationText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float parsedPrecipitation)
            && float.IsFinite(parsedPrecipitation)
            ? Math.Clamp(parsedPrecipitation, 0.0f, 1.0f)
            : null;
        return new RuntimeScenarioSettings(
            requested?.Trim().ToLowerInvariant() ?? string.Empty,
            hour,
            clearWeather,
            precipitation);
    }

    /// <summary>
    /// Advances environment verification and the selected scenario state
    /// machine. It deliberately waits for a usable player and verified weather
    /// before mutating geometry, camera state or dynamic lights.
    /// </summary>
    /// <param name="deltaTime">Elapsed game time in seconds; scenario timing uses registered tick counts for repeatability.</param>
    private void OnTick(float deltaTime)
    {
        if (api.World.Player?.Entity is null)
        {
            return;
        }

        if (!isStartupWorldStateReady())
        {
            return;
        }

        readyTicks++;
        if (!environmentApplied && readyTicks >= 100)
        {
            ApplyDeterministicEnvironment();

            environmentApplied = true;
            api.Logger.Notification(
                "[VintageRTX.Test] Deterministic environment applied: hour={0}, time stopped={1}, clear weather={2}, forced precipitation={3}.",
                requestedWorldHour?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unchanged",
                requestedWorldHour.HasValue,
                clearWeather,
                requestedPrecipitation?.ToString("0.###", CultureInfo.InvariantCulture) ?? "none");
        }

        if (environmentApplied
            && !environmentVerified
            && readyTicks >= 175
            && (readyTicks - 175) % 50 == 0)
        {
            VerifyDeterministicEnvironment();
        }

        if (!injected && environmentVerified)
        {
            if (IsRenderLab && !renderLabBuilt)
            {
                if (!TryBuildRenderLab())
                {
                    return;
                }

                renderLabBuilt = true;
                if (!isolatedHandsApplied)
                {
                    isolatedHandsApplied = true;
                    SelectEmptyHotbarSlot("Render lab");
                }
            }

            if (scenario == "water-reflection" && !heldItemMaskChallengeLogged)
            {
                heldItemMaskChallengeLogged = true;
                LogHeldItemReflectionMaskChallenge();
            }

            if (IsVegetationShadowMap && !exteriorPositionApplied)
            {
                if (!exteriorPositionAttempted)
                {
                    exteriorPositionAttempted = true;
                    exteriorPositionApplied = TryApplyVegetationShadowMapCamera();
                }

                if (!exteriorPositionApplied)
                {
                    api.Logger.Error(
                        "[VintageRTX.Test] Vegetation map camera failed: no loaded, flat, exposed staging patch was found.");
                    injected = true;
                    return;
                }
            }

            if (scenario == "lantern-night" && !exteriorPositionApplied)
            {
                if (!exteriorPositionAttempted)
                {
                    exteriorPositionAttempted = true;
                    // The copied authored map has one durable reference room.
                    // Move close enough to request its chunks, then search the
                    // actual loaded blocks rather than assuming the lantern or
                    // its clear camera eye exists at a hard-coded final pose.
                    api.SendChatMessage("/tp =512139.50 =115.88 =512089.50", null!);
                    api.Logger.Notification(
                        "[VintageRTX.Test] Lantern night staging teleport requested after /gamemode 2; waiting for authored-room chunks.");
                    return;
                }

                lanternCameraSearchTicks++;
                if (lanternCameraSearchTicks < 75 || lanternCameraSearchTicks % 25 != 0)
                {
                    return;
                }

                lanternCameraSearchAttempts++;
                exteriorPositionApplied = TryApplyLanternNightCamera();
                if (!exteriorPositionApplied && lanternCameraSearchAttempts >= 20)
                {
                    api.Logger.Error(
                        "[VintageRTX.Test] Lantern night camera failed after {0} loaded-chunk attempts: no lit lantern with a clear nearby camera cell was found.",
                        lanternCameraSearchAttempts);
                    injected = true;
                    return;
                }
                if (!exteriorPositionApplied)
                {
                    if (lanternCameraSearchAttempts == 1
                        || lanternCameraSearchAttempts % 5 == 0)
                    {
                        api.Logger.Warning(
                            "[VintageRTX.Test] Lantern night camera pending ({0}/20): authored-room chunks or clear eye not ready.",
                            lanternCameraSearchAttempts);
                    }
                    return;
                }
            }

            if (scenario == "lantern-night" && exteriorPositionApplied)
            {
                if (!lightStabilityIsolationRequested)
                {
                    lightStabilityIsolationRequested = true;
                    api.SendChatMessage("/vintagertxtestlight isolate", null!);
                    api.Logger.Notification(
                        "[VintageRTX.Test] Fixed-light entity isolation requested after the lantern camera was established.");
                    return;
                }

                if (lightStabilityIsolationSettleTicks < 50)
                {
                    lightStabilityIsolationSettleTicks++;
                    return;
                }
            }

            if (scenario is "exterior-roof" or "rain-wetness" && !exteriorPositionApplied)
            {
                if (!exteriorPositionAttempted)
                {
                    exteriorPositionAttempted = true;
                    exteriorPositionApplied = TryApplyExteriorRoofCamera();
                }

                if (!exteriorPositionApplied)
                {
                    if (!injected)
                    {
                        api.Logger.Error(
                            "[VintageRTX.Test] Exterior roof camera failed: no nearby open ground candidate.");
                        injected = true;
                    }
                    return;
                }
            }

            if (scenario == "water-reflection" && !exteriorPositionApplied)
            {
                if (!exteriorPositionAttempted)
                {
                    exteriorPositionAttempted = true;
                    exteriorPositionApplied = TryApplyWaterReflectionCamera();
                }

                if (!exteriorPositionApplied)
                {
                    api.Logger.Error(
                        "[VintageRTX.Test] Water reflection camera failed: no open bank beside a visible water patch.");
                    injected = true;
                    return;
                }
            }

            if (scenario == "cave-interior" && !exteriorPositionApplied)
            {
                if (!exteriorPositionAttempted)
                {
                    exteriorPositionAttempted = true;
                    exteriorPositionApplied = TryApplyCaveInteriorCamera();
                }

                if (!exteriorPositionApplied)
                {
                    api.Logger.Error(
                        "[VintageRTX.Test] Cave interior camera failed: no nearby covered, dark and open cave volume.");
                    injected = true;
                    return;
                }
            }

            if (scenario == "resize-and-reload")
            {
                injected = true;
                BeginResizeAndReloadProbe();
            }

            if (scenario == "moving-camera")
            {
                injected = true;
                BeginMovingCameraProbe();
            }

            int count = scenario switch
            {
                "held-light" => 1,
                "cave-interior" => 1,
                "many-lights-stress" => 12,
                _ when IsRenderLab => 3,
                _ => 0
            };

            for (int index = 0; index < count; index++)
            {
                // IPointLight.Pos is a world-space position. Seed it at its real
                // location before registration: registering at (0, 0, 0) made
                // the renderer cull the probe before the following tick could
                // move it into the newly selected cave.
                Vec3d cameraPosition = api.World.Player.Entity.CameraPos;
                Vec3d initialPosition = scenario switch
                {
                    "cave-interior" when caveLightPositionSet =>
                        new Vec3d(caveLightPosition.X, caveLightPosition.Y, caveLightPosition.Z),
                    _ when IsRenderLab && renderLabLightPositionSet =>
                        index switch
                        {
                            1 => new Vec3d(
                                cameraPosition.X + 0.28,
                                cameraPosition.Y - 0.32,
                                cameraPosition.Z + 0.18),
                            2 => new Vec3d(
                                renderLabSecondaryLightPosition.X,
                                renderLabSecondaryLightPosition.Y,
                                renderLabSecondaryLightPosition.Z),
                            _ => new Vec3d(
                                renderLabLightPosition.X,
                                renderLabLightPosition.Y,
                                renderLabLightPosition.Z)
                        },
                    _ => cameraPosition.Clone()
                };
                Vec3f color = IsRenderLab
                    ? index switch
                    {
                        1 => new Vec3f(4.2f, 4.0f, 3.6f),
                        2 => new Vec3f(3.4f, 4.6f, 6.8f),
                        _ => new Vec3f(10.5f, 7.2f, 3.8f)
                    }
                    : new Vec3f(10.5f, 7.2f, 3.8f);
                TestPointLight light = new(
                    color,
                    initialPosition);
                lights.Add(light);
                api.Render.AddPointLight(light);
            }

            if (scenario is not "resize-and-reload" and not "moving-camera")
            {
                injected = true;
                if (IsRenderLab)
                {
                    api.Logger.Notification(
                        "[VintageRTX.Test] Scenario {0} injected 1 moving point light plus 2 fixed/controlled sources through IRenderAPI.AddPointLight; total={1}.",
                        scenario,
                        lights.Count);
                    api.Logger.Notification(
                        "[VintageRTX.Test] Render lab light rig verified: sources={0}, lantern-fixed=0, carried-camera-relative=1 offset=(+0.28,-0.32,+0.18), secondary-fixed=2, distinct colors=3.",
                        lights.Count);
                }
                else
                {
                    api.Logger.Notification(
                        "[VintageRTX.Test] Scenario {0} injected {1} moving point light(s) through IRenderAPI.AddPointLight.",
                        scenario,
                        lights.Count);
                }
            }

            if (IsRenderLab && renderLabBuilt && injected && !renderLabCaptureReleased)
            {
                renderLabCaptureReleased = true;
                Environment.SetEnvironmentVariable("VINTAGERTX_RENDER_LAB_READY", "1");
                api.Logger.Notification(
                    "[VintageRTX.Test] Render lab capture gate released after scene, camera and light setup.");
            }

            if (injected && !testCaptureReleased)
            {
                testCaptureReleased = true;
                Environment.SetEnvironmentVariable(
                    "VINTAGERTX_TEST_ENVIRONMENT_READY",
                    "1",
                    EnvironmentVariableTarget.Process);
                api.Logger.Notification(
                    "[VintageRTX.Test] Automatic capture gate released after environment and scenario setup.");
            }
        }

        UpdateResizeAndReloadProbe();
        UpdateMovingCameraProbe();
        UpdateWaterImpactProbe();
        UpdateWaterProjectileProbe();
        UpdateLocalBodyMirrorCapture();
        UpdateVegetationShadowMapProbe();

        if (exteriorCameraLockTicks > 0)
        {
            UpdateCameraOrientationCalibration();
            ApplyLockedCameraState();
            exteriorCameraLockTicks--;
        }

        Vec3d player = api.World.Player.Entity.CameraPos;
        for (int index = 0; index < lights.Count; index++)
        {
            if (IsRenderLab && renderLabLightPositionSet)
            {
                if (index == 1)
                {
                    lights[index].Pos.Set(
                        player.X + 0.28,
                        player.Y - 0.32,
                        player.Z + 0.18);
                }
                else if (index == 2)
                {
                    lights[index].Pos.Set(
                        renderLabSecondaryLightPosition.X,
                        renderLabSecondaryLightPosition.Y,
                        renderLabSecondaryLightPosition.Z);
                }
                else
                {
                    lights[index].Pos.Set(
                        renderLabLightPosition.X,
                        renderLabLightPosition.Y,
                        renderLabLightPosition.Z);
                }
                continue;
            }

            if (scenario == "cave-interior" && caveLightPositionSet)
            {
                lights[index].Pos.Set(
                    caveLightPosition.X,
                    caveLightPosition.Y,
                    caveLightPosition.Z);
                continue;
            }

            if (lights.Count == 1)
            {
                lights[index].Pos.Set(player.X + 0.28, player.Y - 0.32, player.Z + 0.18);
                continue;
            }

            double angle = index * Math.PI * 2.0 / lights.Count;
            lights[index].Pos.Set(
                player.X + Math.Cos(angle) * 5.0,
                player.Y + 0.6 + (index % 3) * 0.8,
                player.Z + Math.Sin(angle) * 5.0);
        }
    }

    /// <summary>
    /// Selects an empty main-hand slot so held emissive or opaque items cannot
    /// contaminate a material comparison. The method only logs a warning when
    /// isolation is impossible because an automated run must remain observable.
    /// </summary>
    /// <param name="purpose">Human-readable scenario name included in diagnostics.</param>
    private void SelectEmptyHotbarSlot(string purpose = "Water reflection")
    {
        IPlayerInventoryManager inventoryManager = api.World.Player.InventoryManager;
        IInventory? hotbar = inventoryManager.GetHotbarInventory();
        if (hotbar is null)
        {
            api.Logger.Warning(
                "[VintageRTX.Test] {0} hands isolation unavailable: no hotbar inventory.",
                purpose);
            return;
        }

        for (int slotIndex = 0; slotIndex < hotbar.Count; slotIndex++)
        {
            ItemSlot? slot = hotbar[slotIndex];
            if (slot is null || !slot.Empty)
            {
                continue;
            }

            inventoryManager.ActiveHotbarSlotNumber = slotIndex;
            bool offhandEmpty = inventoryManager.OffhandHotbarSlot?.Empty ?? true;
            api.Logger.Notification(
                "[VintageRTX.Test] {0} active hand isolated through public inventory API: active slot={1}, offhand empty={2}.",
                purpose,
                slotIndex,
                offhandEmpty);
            return;
        }

        api.Logger.Warning(
            "[VintageRTX.Test] {0} hands isolation unavailable: no empty hotbar slot.",
            purpose);
    }

    /// <summary>
    /// Records the active and off-hand state without changing either slot. The
    /// water scenario deliberately retains held geometry so its reflection mask
    /// is exercised by the same torch, tool, or modded item visible in gameplay.
    /// </summary>
    private void LogHeldItemReflectionMaskChallenge()
    {
        IPlayerInventoryManager inventoryManager = api.World.Player.InventoryManager;
        IInventory? hotbar = inventoryManager.GetHotbarInventory();
        int activeSlotIndex = inventoryManager.ActiveHotbarSlotNumber;
        ItemSlot? activeSlot = hotbar is not null
            && activeSlotIndex >= 0
            && activeSlotIndex < hotbar.Count
                ? hotbar[activeSlotIndex]
                : null;
        bool activeEmpty = activeSlot?.Empty ?? true;
        bool offhandEmpty = inventoryManager.OffhandHotbarSlot?.Empty ?? true;
        api.Logger.Notification(
            "[VintageRTX.Test] Water reflection held-item mask challenge retained through public inventory API: active slot={0}, active empty={1}, offhand empty={2}.",
            activeSlotIndex,
            activeEmpty,
            offhandEmpty);
    }

    /// <summary>
    /// Requests three server-authoritative loose-stone impacts after both projectile
    /// witnesses and their isolated baselines have completed. Keeping the 3.2-to-235 J
    /// dropped-item sequence last prevents its long-lived rings from being mistaken for
    /// the roughly one-joule stone and arrow responses.
    /// </summary>
    private void UpdateWaterImpactProbe()
    {
        if (scenario != "water-reflection"
            || !waterImpactTargetSet
            || waterImpactCommandIndex >= 3)
        {
            return;
        }

        waterImpactTicks++;
        int triggerTick = waterImpactCommandIndex switch
        {
            // The projectiles are injected at 1,800 and 2,150 ticks. The first
            // dropped item therefore cannot enter until both post-impact capture
            // transactions have had seven seconds to finish at the nominal rate.
            0 => 2_500,
            // Five and three seconds between entries leave each three-channel
            // dropped-item capture sequence enough room before the next event.
            1 => 2_750,
            _ => 2_900
        };
        if (waterImpactTicks < triggerTick)
        {
            return;
        }

        (int stackSize, double expectedMassKilograms, double velocityX, double velocityY, double velocityZ) =
            waterImpactCommandIndex switch
            {
                0 => (1, 0.350, 0.24, -0.05, -0.08),
                // Nine reference stones preserve a distinct intermediate
                // pseudo-mass while leaving enough separation from the strong
                // impact after Vintage Story's item-specific contact drag.
                1 => (9, 1.050, -0.52, -1.50, 0.28),
                // The highest legal stack reaches the liquid model's sqrt(stack)
                // mass ceiling. A bounded downward throw plus five metres of
                // gravity supplies a clearly separated impact-energy class.
                _ => (64, 2.800, 0.88, -4.00, -0.46)
            };
        int requestNumber = waterImpactCommandIndex + 1;
        double dropHeightMetres = WaterImpactDropHeightMetres(waterImpactCommandIndex);
        Vec3d landingPosition = waterImpactLandingPositions[waterImpactCommandIndex];
        double flightSeconds = WaterImpactBallisticFlightSeconds(dropHeightMetres, velocityY);
        double spawnX = landingPosition.X - velocityX * flightSeconds;
        double spawnY = landingPosition.Y + dropHeightMetres;
        double spawnZ = landingPosition.Z - velocityZ * flightSeconds;
        waterImpactCommandIndex++;
        string command = string.Format(
            CultureInfo.InvariantCulture,
            "/vintagertxtest impact {0:0.00} {1:0.00} {2:0.00} {3:0.00} {4:0.00} {5:0.00} {6} {7:0.00}",
            spawnX,
            spawnY,
            spawnZ,
            velocityX,
            velocityY,
            velocityZ,
            stackSize,
            dropHeightMetres);
        api.SendChatMessage(command, null!);
        api.Logger.Notification(
            "[VintageRTX.Test] Server dropped-item impact requested: sequence={0}/3, target=({1:0.00},{2:0.00}), spawn=({3:0.00},{4:0.00},{5:0.00}), drop-height={6:0.00} m, stack={7}, expected pseudo-mass={8:0.000} kg, velocity=({9:0.00},{10:0.00},{11:0.00}).",
            requestNumber,
            landingPosition.X,
            landingPosition.Z,
            spawnX,
            spawnY,
            spawnZ,
            dropHeightMetres,
            stackSize,
            expectedMassKilograms,
            velocityX,
            velocityY,
            velocityZ);
    }

    /// <summary>
    /// Captures the local third-person body from a physically valid planar-reflection frustum after
    /// every wide-lake impact witness is complete. The principal water diagnostics retain their
    /// long-range camera; only this named entity-only carrier looks down at the nearest verified
    /// water cell. Its reflected camera therefore looks back up at the real body instead of
    /// correctly clipping that body as being roughly 69 degrees outside the wide-view frustum.
    /// </summary>
    private void UpdateLocalBodyMirrorCapture()
    {
        if (scenario != "water-reflection"
            || queueDiagnosticCapture is null
            || waterImpactCommandIndex < waterImpactLandingPositions.Length
            || waterLocalBodyCaptureState == LocalBodyMirrorCaptureState.Complete)
        {
            return;
        }

        if (waterLocalBodyCaptureState == LocalBodyMirrorCaptureState.Inactive)
        {
            // The last impact callback queues three diagnostic transactions asynchronously. Five
            // seconds keeps this camera-only proof clear of those captures without depending on FPS.
            waterLocalBodyCaptureDelayTicks++;
            if (waterLocalBodyCaptureDelayTicks < 250 || !isDiagnosticCaptureIdle())
            {
                return;
            }

            waterWideYaw = exteriorYaw;
            waterWidePitch = exteriorPitch;
            waterWideEntityPitch = exteriorEntityPitch;
            exteriorYaw = waterLocalBodyYaw;
            exteriorPitch = waterLocalBodyPitch;
            exteriorEntityPitch = waterLocalBodyEntityPitch;
            ApplyLockedCameraState();
            if (!queueDiagnosticCapture(
                    "local-body-entity-mirror",
                    VintageRtxDebugView.EntityMirror))
            {
                exteriorYaw = waterWideYaw;
                exteriorPitch = waterWidePitch;
                exteriorEntityPitch = waterWideEntityPitch;
                ApplyLockedCameraState();
                return;
            }

            waterLocalBodyCaptureState = LocalBodyMirrorCaptureState.Capturing;
            api.Logger.Notification(
                "[VintageRTX.Test] Local-body entity-mirror capture queued with a physical near-water reflected frustum; the wide-lake pose will resume after durable readback.");
            return;
        }

        // Complete is rejected by the method guard and Inactive always returns from the branch
        // above, so Capturing is the only state that can reach this completion checkpoint.
        if (!isDiagnosticCaptureIdle())
        {
            return;
        }

        exteriorYaw = waterWideYaw;
        exteriorPitch = waterWidePitch;
        exteriorEntityPitch = waterWideEntityPitch;
        ApplyLockedCameraState();
        waterLocalBodyCaptureState = LocalBodyMirrorCaptureState.Complete;
        api.Logger.Notification(
            "[VintageRTX.Test] Local-body entity-mirror capture completed; wide-lake camera restored for range evidence.");
    }

    /// <summary>
    /// Requests one real thrown-item stone and one real flint-arrow entity only
    /// after the final 1750-tick witness-stability checkpoint. Each request is
    /// preceded by one named Final and three ordered LiquidSurfaceField baselines that must be saved
    /// before the world is mutated. The three field samples provide time-local wind velocity and
    /// curvature, rather than treating an evolving wind field as projectile energy. Their public
    /// <c>IProjectile.ProjectileStack</c>
    /// payloads then prove repeated ricochet and generic one-shot entry paths.
    /// </summary>
    private void UpdateWaterProjectileProbe()
    {
        if (scenario != "water-reflection"
            || !waterImpactTargetSet
            || waterProjectileCommandIndex >= waterProjectileLandingPositions.Length)
        {
            return;
        }

        waterProjectileTicks++;
        // Start the second baseline as soon as the first post-impact capture transaction has had
        // nominally two seconds to drain. The arrow uses the verified central open-water target,
        // three metres beyond the stone target; the three pre-impact field captures measure the
        // remaining stone/wind evolution instead of moving the arrow into an occluded far-shore cell.
        int triggerTick = waterProjectileCommandIndex == 0 ? 1_800 : 1_900;
        if (waterProjectileTicks < triggerTick)
        {
            return;
        }

        (string kind, string entityCode, string payloadCode, double dropHeightMetres,
            double velocityX, double velocityY, double velocityZ) =
            waterProjectileCommandIndex == 0
                ? ("stone", "game:thrownitem", "game:stone-granite", 0.65, 5.50, -0.35, 0.45)
                // A bow-fired arrow is a high-speed projectile. Keep every component inside the
                // server's 32 m/s safety envelope while retaining an oblique, descending entry;
                // the liquid callback still derives its impulse solely from 1/2*m*v^2.
                : ("arrow", "game:arrow-flint", "game:arrow-flint", 1.40, -18.00, -20.00, -12.00);

        if (!TryCompleteProjectileBaseline(kind))
        {
            return;
        }

        Vec3d target = waterProjectileLandingPositions[waterProjectileCommandIndex];
        double flightSeconds = WaterImpactBallisticFlightSeconds(dropHeightMetres, velocityY);
        double spawnX = target.X - velocityX * flightSeconds;
        double spawnY = target.Y + dropHeightMetres;
        double spawnZ = target.Z - velocityZ * flightSeconds;
        int sequence = ++waterProjectileCommandIndex;
        api.SendChatMessage(
            string.Format(
                CultureInfo.InvariantCulture,
                "/vintagertxtest projectile {0} {1:0.00} {2:0.00} {3:0.00} {4:0.00} {5:0.00} {6:0.00}",
                kind,
                spawnX,
                spawnY,
                spawnZ,
                velocityX,
                velocityY,
                velocityZ),
            null!);
        api.Logger.Notification(
            "[VintageRTX.Test] Server liquid projectile requested: sequence={0}/2, kind={1}, entity-type={2}, payload={3}, target=({4:0.00},{5:0.00}), spawn=({6:0.00},{7:0.00},{8:0.00}), drop-height={9:0.00} m, velocity=({10:0.00},{11:0.00},{12:0.00}) m/s.",
            sequence,
            kind,
            entityCode,
            payloadCode,
            target.X,
            target.Z,
            spawnX,
            spawnY,
            spawnZ,
            dropHeightMetres,
            velocityX,
            velocityY,
            velocityZ);
        waterProjectileBaselinePhase = 0;
        waterProjectileBaselineQueued = false;
    }

    /// <summary>
    /// Saves a Final and three LiquidSurfaceField witnesses before one projectile is spawned. The
    /// field triplet supports a bounded quadratic wind-only counterfactual at response time. The
    /// single capture slot is explicitly observed between phases, so a slow GPU
    /// cannot turn a queued request into a post-impact image and invalidate the delta measurement.
    /// </summary>
    /// <param name="kind">Stable <c>stone</c> or <c>arrow</c> label.</param>
    /// <returns>Whether all four baseline pairs are durably complete and the projectile may be spawned.</returns>
    private bool TryCompleteProjectileBaseline(string kind)
    {
        if (queueDiagnosticCapture is null)
        {
            return true;
        }

        if (waterProjectileBaselinePhase == 0)
        {
            waterProjectileBaselinePhase = 1;
        }

        if (!isDiagnosticCaptureIdle())
        {
            return false;
        }

        if (waterProjectileBaselinePhase == 5 && waterProjectileBaselineQueued)
        {
            return true;
        }

        VintageRtxDebugView view = waterProjectileBaselinePhase == 1
            ? VintageRtxDebugView.Final
            : VintageRtxDebugView.LiquidSurfaceField;
        string viewLabel = waterProjectileBaselinePhase switch
        {
            1 => "final",
            2 => "earlier-surface-field",
            3 => "prior-surface-field",
            _ => "surface-field"
        };
        string label = $"projectile-{kind}-baseline-{viewLabel}";
        if (!queueDiagnosticCapture(label, view))
        {
            return false;
        }

        api.Logger.Notification(
            "[VintageRTX.Test] Projectile baseline capture queued: projectile={0}, phase={1}/4, view={2}, label={3}.",
            kind,
            waterProjectileBaselinePhase,
            view,
            label);
        if (waterProjectileBaselinePhase < 4)
        {
            waterProjectileBaselinePhase++;
            return false;
        }

        waterProjectileBaselinePhase = 5;
        waterProjectileBaselineQueued = true;
        return false;
    }

    /// <summary>Returns the deterministic physical fall height for one ordered impact.</summary>
    /// <param name="index">Zero-based impact ordinal.</param>
    /// <returns>Height above the verified liquid surface in metres.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown outside the three-impact sequence.</exception>
    private static double WaterImpactDropHeightMetres(int index) => index switch
    {
        0 => 0.50,
        1 => 3.00,
        2 => 5.00,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>Computes the gravity-only flight time from a release point to its liquid surface.</summary>
    /// <param name="dropHeightMetres">Vertical release height above the liquid surface.</param>
    /// <param name="velocityY">Initial vertical velocity in metres per second.</param>
    /// <returns>The positive ballistic flight time in seconds.</returns>
    private static double WaterImpactBallisticFlightSeconds(double dropHeightMetres, double velocityY) =>
        (velocityY
            + Math.Sqrt(
                velocityY * velocityY
                + 2.0 * LiquidPhysicalModel.StandardGravityMetresPerSecondSquared * dropHeightMetres))
        / LiquidPhysicalModel.StandardGravityMetresPerSecondSquared;

    /// <summary>
    /// Resolves three separated, individually verified liquid targets along the continuous
    /// camera-to-lake run. Separation prevents previously floating stones from changing the
    /// incident velocity of a later reference impact through entity collision.
    /// </summary>
    /// <param name="accessor">Loaded world used to verify each exact landing column.</param>
    /// <param name="sample">Reusable mutable block coordinate.</param>
    /// <param name="cameraX">Locked camera X coordinate.</param>
    /// <param name="cameraZ">Locked camera Z coordinate.</param>
    /// <param name="directionX">Normalized water-run direction X.</param>
    /// <param name="directionZ">Normalized water-run direction Z.</param>
    /// <param name="centralDistance">Distance of the middle target from the camera.</param>
    /// <returns><see langword="true"/> when all three exact target columns expose liquid.</returns>
    private bool TryConfigureWaterImpactTargets(
        IBlockAccessor accessor,
        BlockPos sample,
        double cameraX,
        double cameraZ,
        double directionX,
        double directionZ,
        double centralDistance)
    {
        for (int index = 0; index < waterImpactLandingPositions.Length; index++)
        {
            double distanceOffset = index switch
            {
                0 => -1.25,
                1 => 0.0,
                _ => 1.25
            };
            double worldX = cameraX + directionX * (centralDistance + distanceOffset);
            double worldZ = cameraZ + directionZ * (centralDistance + distanceOffset);
            if (!TryGetWaterSurface(
                    accessor,
                    sample,
                    (int)Math.Floor(worldX),
                    (int)Math.Floor(worldZ),
                    out int waterY,
                    out Block water))
            {
                return false;
            }

            double surfaceY = waterY + Math.Clamp(water.LiquidLevel, 0, 7) / 8.0;
            waterImpactLandingPositions[index].Set(
                worldX,
                surfaceY,
                worldZ);
        }

        for (int index = 0; index < waterProjectileLandingPositions.Length; index++)
        {
            // The far (+3 m) point is physically wet but hidden behind the distant witness/shore
            // in the locked camera. Keep the stone near (-3 m) and put the arrow on the already
            // verified central open-water column so exact image-space validation observes water.
            double distanceOffset = index == 0 ? -3.0 : 0.0;
            double worldX = cameraX + directionX * (centralDistance + distanceOffset);
            double worldZ = cameraZ + directionZ * (centralDistance + distanceOffset);
            if (!TryGetWaterSurface(
                    accessor,
                    sample,
                    (int)Math.Floor(worldX),
                    (int)Math.Floor(worldZ),
                    out int waterY,
                    out Block water))
            {
                return false;
            }

            double surfaceY = waterY + Math.Clamp(water.LiquidLevel, 0, 7) / 8.0;
            waterProjectileLandingPositions[index].Set(worldX, surfaceY, worldZ);
        }

        return true;
    }

    /// <summary>
    /// Stages the deterministic elevated render laboratory through public block
    /// APIs, including real block-entity-backed lantern and anvil geometry.
    /// Existing terrain is not used as comparison geometry, preventing biome
    /// and spawn variance from changing the reference frame.
    /// </summary>
    /// <returns><see langword="true"/> once all required chunks are loaded and the laboratory has been committed; otherwise <see langword="false"/> so a later tick can retry.</returns>
    private bool TryBuildRenderLab()
    {
        IBlockAccessor accessor = api.World.BlockAccessor;
        BlockPos playerBlock = api.World.Player.Entity.Pos.AsBlockPos.Copy();
        int anchorX = accessor.MapSizeX / 2;
        int anchorY = Math.Max(1, accessor.MapSizeY - 24);
        int anchorZ = accessor.MapSizeZ / 2;
        if (stageRenderLabBlock is null && !renderLabAnchorRequested)
        {
            renderLabAnchorRequested = true;
            api.SendChatMessage(
                $"/tp ={(anchorX + 0.5).ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"={anchorY.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"={(anchorZ + 0.5).ToString("0.00", CultureInfo.InvariantCulture)}",
                null!);
            api.Logger.Notification(
                "[VintageRTX.Test] Render lab fixed anchor requested: world=({0},{1},{2}).",
                anchorX,
                anchorY,
                anchorZ);
            return false;
        }

        // Test doubles retain their authored player position. A real generated
        // world instead uses the centre anchor above, because Vintage Story's
        // spawn search may choose a different valid position for the same seed.
        if (stageRenderLabBlock is null)
        {
            playerBlock.Set(anchorX, anchorY, anchorZ);
        }
        int floorY = playerBlock.Y - 1;
        bool foundFloor = false;
        for (int y = playerBlock.Y; y >= playerBlock.Y - 12; y--)
        {
            playerBlock.Set(playerBlock.X, y, playerBlock.Z);
            Block candidate = getRenderLabBlock?.Invoke(playerBlock, BlockLayersAccess.MostSolid)
                ?? accessor.GetBlock(playerBlock, BlockLayersAccess.MostSolid);
            if (!IsCaveSolid(candidate))
            {
                continue;
            }

            floorY = y;
            foundFloor = true;
            break;
        }

        if (!foundFloor)
        {
            floorY = getRenderLabRainHeight?.Invoke(playerBlock)
                ?? accessor.GetRainMapHeightAt(playerBlock);
        }

        // Build above the generated landscape instead of trying to reuse it as
        // a floor. A normal survival preset is important for representative
        // lighting, but its bushes, ponds and relief must not leak into the
        // fixed comparison frame.
        floorY = Math.Min(floorY + 24, accessor.MapSizeY - 12);

        int originX = playerBlock.X - 6;
        int originZ = playerBlock.Z + 4;
        BlockPos minimum = new(originX - 3, floorY, originZ - 8);
        BlockPos maximum = new(originX + 15, floorY + 8, originZ + 12);
        if (!IsRenderLabChunkAvailable(accessor, minimum)
            || !IsRenderLabChunkAvailable(accessor, maximum))
        {
            return false;
        }

        Block bricks = RequireRenderLabBlock(accessor, "game:stonebricks-granite");
        Block polished = RequireRenderLabBlock(accessor, "game:rockpolished-granite");
        Block soil = RequireRenderLabBlock(accessor, "game:soil-medium-normal");
        Block grass = RequireRenderLabBlock(accessor, "game:tallgrass-tall-free");
        Block water = RequireRenderLabBlock(accessor, "game:water-still-7");
        Block lantern = RequireRenderLabBlock(accessor, "game:lantern-large-up");
        Block anvil = RequireRenderLabBlock(accessor, "game:anvil-iron");
        Block targetBlue = RequireRenderLabBlock(accessor, "game:daub-blue-normal");
        Block targetOrange = RequireRenderLabBlock(accessor, "game:daub-orange-normal");
        Block targetGreen = RequireRenderLabBlock(accessor, "game:daub-green-normal");
        IBulkBlockAccessor bulk = api.World.GetBlockAccessorBulkUpdate(
            synchronize: false,
            relight: true,
            debug: false);
        BlockPos staging = new(playerBlock.dimension);
        void Stage(Block block, int localX, int localY, int localZ)
        {
            staging.Set(originX + localX, floorY + localY, originZ + localZ);
            StageRenderLabBlock(bulk, block.Id, staging, null);
        }

        void Clear(int localX, int localY, int localZ)
        {
            staging.Set(originX + localX, floorY + localY, originZ + localZ);
            StageRenderLabBlock(bulk, 0, staging, BlockLayersAccess.Solid);
            StageRenderLabBlock(bulk, 0, staging, BlockLayersAccess.Fluid);
        }

        // Clear the room and camera corridor in both render layers. The lab is
        // elevated, and a small stage below the camera hides the normal-world
        // terrain without replacing it with the special creative environment.
        for (int y = 1; y <= 8; y++)
        {
            for (int z = -8; z <= 12; z++)
            {
                for (int x = -3; x <= 15; x++)
                {
                    staging.Set(originX + x, floorY + y, originZ + z);
                    StageRenderLabBlock(bulk, 0, staging, BlockLayersAccess.Solid);
                    StageRenderLabBlock(bulk, 0, staging, BlockLayersAccess.Fluid);
                }
            }
        }
        // A neutral stage extends under the camera, then becomes the 13x11
        // receiving floor. This makes the capture independent from spawn
        // vegetation while retaining open sky on the exposed half.
        for (int z = -7; z <= 10; z++)
        {
            int minimumX = z < 0 ? -2 : 0;
            int maximumX = z < 0 ? 14 : 12;
            for (int x = minimumX; x <= maximumX; x++)
            {
                Stage(bricks, x, 0, z);
            }
        }

        for (int y = 1; y <= 6; y++)
        {
            for (int x = 0; x <= 12; x++)
            {
                Stage(y is >= 2 and <= 5 && x is >= 2 and <= 5 ? polished : bricks, x, y, 10);
            }

            for (int z = 1; z < 10; z++)
            {
                Stage(bricks, 0, y, z);
                Stage(bricks, 12, y, z);
            }
        }

        // At the deterministic noon azimuth, rays travel strongly toward +X.
        // Leave a real open-sky aperture beyond the crossed-plane witness so
        // its floor shadow is not swallowed by the laboratory's side wall.
        // The cleared corridor is outside the water and reflection targets.
        for (int y = 1; y <= 6; y++)
        {
            for (int z = 5; z <= 9; z++)
            {
                Clear(12, y, z);
            }
        }

        for (int z = 5; z <= 10; z++)
        {
            for (int x = 0; x <= 6; x++)
            {
                Stage(bricks, x, 7, z);
            }
        }

        // Material islands: a polished pedestal/cube, soil plus a genuine
        // crossed-plane grass mesh, and a shallow water basin in the open half.
        Stage(polished, 3, 1, 7);
        Stage(polished, 9, 1, 8);
        Stage(soil, 10, 0, 6);
        Stage(grass, 10, 1, 6);
        for (int z = 1; z <= 3; z++)
        {
            for (int x = 7; x <= 10; x++)
            {
                Stage(water, x, 1, z);
            }
        }

        // Three full-height, non-emissive color references face the basin.
        // Their distinct authored albedos make a contour-only or noisy
        // reflection fail visibly without changing any shader/material path.
        for (int y = 1; y <= 3; y++)
        {
            Stage(targetBlue, 7, y, 10);
            Stage(targetOrange, 9, y, 10);
            Stage(targetGreen, 11, y, 10);
        }
        CommitRenderLabBlocks(bulk);

        // Place BlockEntity-backed geometry through the normal public accessor
        // after the bulk commit. This preserves the lantern's authored cage and
        // glass attributes and the anvil's actual non-cubic tessellation.
        BlockPos lanternPosition = new(originX + 3, floorY + 2, originZ + 7);
        ItemStack lanternStack = new(lantern);
        lanternStack.Attributes.SetString("material", "iron");
        lanternStack.Attributes.SetString("lining", "plain");
        lanternStack.Attributes.SetString("glass", "quartz");
        accessor.SetBlock(lantern.Id, lanternPosition, lanternStack);

        BlockPos anvilPosition = new(originX + 6, floorY + 1, originZ + 7);
        accessor.SetBlock(anvil.Id, anvilPosition, new ItemStack(anvil));
        accessor.MarkBlockDirty(lanternPosition, (IPlayer)null!);
        accessor.MarkBlockDirty(anvilPosition, (IPlayer)null!);

        BlockPos grassPosition = new(originX + 10, floorY + 1, originZ + 6);
        BlockPos waterPosition = new(originX + 9, floorY + 1, originZ + 2);
        BlockPos targetBluePosition = new(originX + 7, floorY + 1, originZ + 10);
        BlockPos targetOrangePosition = new(originX + 9, floorY + 1, originZ + 10);
        BlockPos targetGreenPosition = new(originX + 11, floorY + 1, originZ + 10);
        bool solarApertureMatches = true;
        for (int y = 1; y <= 6; y++)
        {
            for (int z = 5; z <= 9; z++)
            {
                staging.Set(originX + 12, floorY + y, originZ + z);
                solarApertureMatches &= accessor.GetBlock(
                        staging,
                        BlockLayersAccess.Solid).Id == 0
                    && accessor.GetBlock(
                        staging,
                        BlockLayersAccess.Fluid).Id == 0;
            }
        }
        bool placementMatches = accessor.GetBlock(lanternPosition).Id == lantern.Id
            && accessor.GetBlock(anvilPosition).Id == anvil.Id
            && accessor.GetBlock(grassPosition).Id == grass.Id
            && accessor.GetBlock(waterPosition, BlockLayersAccess.Fluid).Id == water.Id
            && accessor.GetBlock(targetBluePosition).Id == targetBlue.Id
            && accessor.GetBlock(targetOrangePosition).Id == targetOrange.Id
            && accessor.GetBlock(targetGreenPosition).Id == targetGreen.Id
            && solarApertureMatches;
        if (!placementMatches)
        {
            throw new InvalidOperationException(
                "Render lab material placement verification failed after the bulk commit.");
        }

        renderLabLightPosition.Set(
            lanternPosition.X + 0.5,
            lanternPosition.Y + 0.48,
            lanternPosition.Z + 0.5);
        renderLabSecondaryLightPosition.Set(
            originX + 10.5,
            floorY + 3.2,
            originZ + 7.5);
        renderLabLightPositionSet = true;

        double destinationX = originX + 6.5;
        double destinationY = floorY + 1.05;
        double destinationZ = originZ - 0.75;
        double targetX = originX + 6.5;
        double targetY = floorY + 1.65;
        double targetZ = originZ + 6.5;
        SelectCameraOrientation(
            targetX - destinationX,
            targetY - (destinationY + 1.62),
            targetZ - destinationZ,
            useDirectCameraAngles: true);
        exteriorCameraLockTicks = 36_000;
        exteriorPositionLocked = true;
        exteriorPositionX = destinationX;
        exteriorPositionY = destinationY;
        exteriorPositionZ = destinationZ;
        api.SendChatMessage(
            $"/tp ={destinationX.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationY.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationZ.ToString("0.00", CultureInfo.InvariantCulture)}",
            null!);

        api.Logger.Notification(
            "[VintageRTX.Test] Render lab built: origin=({0},{1},{2}), room=13x11x7, elevated=24, cleared=19x21x8, roof=sheltered-left, water=4x3.",
            originX,
            floorY,
            originZ);
        Vec3f renderLabSun = VoxelScene.ResolveSunDirection(
            api.World.Calendar,
            api.World.Player.Entity.CameraPos);
        api.Logger.Notification(
            "[VintageRTX.Test] Render lab solar aperture verified: side=+X, local-x=12, z=5..9, height=6, sun=({0:0.000},{1:0.000},{2:0.000}).",
            renderLabSun.X,
            renderLabSun.Y,
            renderLabSun.Z);
        api.Logger.Notification(
            "[VintageRTX.Test] Render lab materials verified: receiver={0}, polished={1}, lantern={2}, anvil={3}, crossed={4} draw={5} collision boxes={6}, fluid={7}; chisel omitted because no public authored instance-state constructor is safe.",
            bricks.Code,
            polished.Code,
            lantern.Code,
            anvil.Code,
            grass.Code,
            grass.DrawType,
            grass.CollisionBoxes?.Length ?? 0,
            water.Code);
        api.Logger.Notification(
            "[VintageRTX.Test] Render lab reflection targets verified: count=3, emissive=none, basin-facing=true; blue={0}@({1},{2},{3}), orange={4}@({5},{6},{7}), green={8}@({9},{10},{11}).",
            targetBlue.Code,
            targetBluePosition.X,
            targetBluePosition.Y,
            targetBluePosition.Z,
            targetOrange.Code,
            targetOrangePosition.X,
            targetOrangePosition.Y,
            targetOrangePosition.Z,
            targetGreen.Code,
            targetGreenPosition.X,
            targetGreenPosition.Y,
            targetGreenPosition.Z);
        api.Logger.Notification(
            "[VintageRTX.Test] Render lab camera applied: close=true, position=({0:0.00},{1:0.00},{2:0.00}), target=({3:0.00},{4:0.00},{5:0.00}), lantern light=({6:0.00},{7:0.00},{8:0.00}), secondary light=({9:0.00},{10:0.00},{11:0.00}), water and authored casters in frame=true.",
            destinationX,
            destinationY,
            destinationZ,
            targetX,
            targetY,
            targetZ,
            renderLabLightPosition.X,
            renderLabLightPosition.Y,
            renderLabLightPosition.Z,
            renderLabSecondaryLightPosition.X,
            renderLabSecondaryLightPosition.Y,
            renderLabSecondaryLightPosition.Z);
        return true;
    }

    /// <summary>
    /// Reports whether a render-lab boundary chunk is loaded. Production uses
    /// the public world accessor; deterministic tests may bypass the emitted
    /// interface proxy while exercising the same short-circuit decisions.
    /// </summary>
    /// <param name="accessor">Accessor for the loaded test world.</param>
    /// <param name="position">Boundary position whose containing chunk is required.</param>
    /// <returns><see langword="true"/> when the containing chunk is available.</returns>
    private bool IsRenderLabChunkAvailable(IBlockAccessor accessor, BlockPos position)
    {
        return isRenderLabChunkAvailable?.Invoke(position)
            ?? accessor.GetChunkAtBlockPos(position) is not null;
    }

    /// <summary>
    /// Stages one render-lab block through the real bulk accessor or the
    /// allocation-free in-memory writer supplied by unit tests.
    /// </summary>
    /// <param name="bulk">Vintage Story bulk accessor for real game runs.</param>
    /// <param name="blockId">Block identifier to stage.</param>
    /// <param name="position">World position copied synchronously by the destination.</param>
    /// <param name="layer">Explicit solid/fluid layer, or <see langword="null"/> for automatic selection.</param>
    private void StageRenderLabBlock(
        IBulkBlockAccessor bulk,
        int blockId,
        BlockPos position,
        int? layer)
    {
        if (stageRenderLabBlock is not null)
        {
            stageRenderLabBlock(blockId, position, layer);
            return;
        }

        if (layer.HasValue)
        {
            bulk.SetBlock(blockId, position, layer.Value);
        }
        else
        {
            bulk.SetBlock(blockId, position);
        }
    }

    /// <summary>Commits the staged laboratory through the matching real or in-memory writer.</summary>
    /// <param name="bulk">Vintage Story bulk accessor for real game runs.</param>
    private void CommitRenderLabBlocks(IBulkBlockAccessor bulk)
    {
        if (commitRenderLabBlocks is not null)
        {
            commitRenderLabBlocks();
            return;
        }

        bulk.Commit();
    }

    /// <summary>
    /// Resolves a mandatory laboratory material and rejects the air fallback.
    /// A missing asset is a harness configuration error rather than a scene
    /// variation, so continuing would produce a misleading visual baseline.
    /// </summary>
    /// <param name="accessor">Accessor for the loaded test world.</param>
    /// <param name="code">Fully qualified Vintage Story block asset code.</param>
    /// <returns>The loaded, non-air block.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the requested block was not loaded.</exception>
    private static Block RequireRenderLabBlock(IBlockAccessor accessor, string code)
    {
        Block block = accessor.GetBlock(new AssetLocation(code));
        if (block is null || block.Id == 0)
        {
            throw new InvalidOperationException(
                $"Render lab requires block '{code}', but it was not loaded.");
        }

        return block;
    }

    /// <summary>
    /// Starts a smooth 36-degree camera sweep and queues its capture. The
    /// original yaw is retained so temporal validation leaves the player pose
    /// unchanged after the fixed-duration probe.
    /// </summary>
    private void BeginMovingCameraProbe()
    {
        movingCameraBaseYaw = api.Input.MouseYaw;
        movingCameraTicks = MovingCameraDurationTicks;
        api.Logger.Notification(
            "[VintageRTX.Test] Moving-camera probe started: smooth {0:0.0}-degree sweep over {1:0.0}s.",
            36.0,
            MovingCameraDurationTicks * 0.02);
        if (!queueCapture())
        {
            api.Logger.Error(
                "[VintageRTX.Test] Moving-camera capture queue: FAIL; a capture was already pending.");
        }
    }

    /// <summary>
    /// Applies the deterministic pose before the engine camera renderer and
    /// resumes a runtime-test world opened in the paused single-player state.
    /// No scene mutation occurs during other render stages.
    /// </summary>
    /// <param name="deltaTime">Elapsed render time in seconds; pose locking itself is time independent.</param>
    /// <param name="stage">Current engine render stage.</param>
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Before)
        {
            return;
        }

        // A named capture finishes after the previous frame's post-final readback. Restore the
        // long-range pose at the next Before stage so no automatic diagnostic can observe the
        // temporary local-body framing, even on a high-refresh client between game ticks.
        if (waterLocalBodyCaptureState == LocalBodyMirrorCaptureState.Capturing)
        {
            UpdateLocalBodyMirrorCapture();
        }

        // A world opened through --openWorld can start paused when the automated
        // client does not own foreground focus. Every scenario relies on game-tick
        // listeners for ordered game mode, time, weather, placement, and camera
        // commands, so resume it through the public client API from the render
        // callback, which remains active while single-player simulation is paused.
        if (api.World.Player?.Entity is not null
            && api.IsGamePaused)
        {
            api.PauseGame(false);
            if (!runtimePauseReleaseLogged)
            {
                runtimePauseReleaseLogged = true;
                api.Logger.Notification(
                    "[VintageRTX.Test] Runtime world automatically resumed through ICoreClientAPI.PauseGame(false).");
            }
        }

        if (exteriorCameraLockTicks <= 0
            || api.World.Player?.Entity is null)
        {
            return;
        }

        ApplyLockedCameraState();
    }

    /// <summary>
    /// Finds an authored, lit lantern in the loaded real map and locks a nearby unobstructed camera
    /// on its flame. No blocks are created or replaced, so the evidence retains the building's
    /// actual cage, walls, furniture, receivers, and occluders.
    /// </summary>
    /// <returns><see langword="true"/> when both a lantern and a clear camera ray were found.</returns>
    private bool TryApplyLanternNightCamera()
    {
        EntityPlayer entity = api.World.Player.Entity;
        IBlockAccessor accessor = api.World.BlockAccessor;
        BlockPos origin = entity.Pos.AsBlockPos.Copy();
        BlockPos? lanternPosition = null;
        Block? lanternBlock = null;
        int[] searchRadii = [24, 48, 96, 144];

        foreach (int radius in searchRadii)
        {
            int minimumY = Math.Max(1, origin.Y - 32);
            int maximumY = Math.Min(api.World.MapSizeY - 2, origin.Y + 32);
            BlockPos minimum = new(origin.X - radius, minimumY, origin.Z - radius, origin.dimension);
            BlockPos maximum = new(origin.X + radius, maximumY, origin.Z + radius, origin.dimension);
            accessor.SearchBlocks(
                minimum,
                maximum,
                (block, position) =>
                {
                    if (!IsLitLantern(block, accessor, position))
                    {
                        return true;
                    }

                    lanternBlock = block;
                    lanternPosition = position.Copy();
                    return false;
                });
            if (lanternPosition is not null)
            {
                break;
            }
        }

        if (lanternPosition is null || lanternBlock is null)
        {
            return false;
        }

        Vec3d lanternCenter = new(
            lanternPosition.X + 0.5,
            lanternPosition.Y + 0.5,
            lanternPosition.Z + 0.5);
        Entity[] nearbyEntities = api.World.GetEntitiesAround(
            lanternCenter,
            8.0f,
            4.0f,
            candidate => candidate is EntityAgent
                && candidate.EntityId != entity.EntityId
                && candidate.Alive) ?? [];
        LanternCameraEntityBounds[] occupiedEntityBounds = nearbyEntities
            .Select(static candidate => LanternCameraEntityBounds.FromEntity(candidate))
            .ToArray();
        api.Logger.Notification(
            "[VintageRTX.Test] Lantern camera near-field scan: entities={0}, clearance=0.75m.",
            occupiedEntityBounds.Length);
        foreach (Entity nearbyEntity in nearbyEntities)
        {
            LanternCameraEntityBounds bounds = LanternCameraEntityBounds.FromEntity(nearbyEntity);
            api.Logger.Notification(
                "[VintageRTX.Test] Lantern camera candidate entity: id={0}, code={1}, bounds=({2:0.00},{3:0.00},{4:0.00})-({5:0.00},{6:0.00},{7:0.00}).",
                nearbyEntity.EntityId,
                nearbyEntity.Code,
                bounds.MinimumX,
                bounds.MinimumY,
                bounds.MinimumZ,
                bounds.MaximumX,
                bounds.MaximumY,
                bounds.MaximumZ);
        }
        Vec3d? cameraEye = FindLanternCameraEye(
            accessor,
            lanternPosition,
            occupiedEntityBounds);
        if (cameraEye is null)
        {
            return false;
        }

        SelectEmptyHotbarSlot("Lantern night isolation");
        double destinationX = cameraEye.X;
        double destinationY = cameraEye.Y - 1.62;
        double destinationZ = cameraEye.Z;
        double targetX = lanternPosition.X + 0.5;
        double targetY = lanternPosition.Y + 0.5;
        double targetZ = lanternPosition.Z + 0.5;
        SelectCameraOrientation(
            targetX - destinationX,
            targetY - cameraEye.Y,
            targetZ - destinationZ,
            useDirectCameraAngles: true);
        exteriorCameraLockTicks = 36_000;
        exteriorPositionLocked = true;
        exteriorPositionX = destinationX;
        exteriorPositionY = destinationY;
        exteriorPositionZ = destinationZ;
        api.SendChatMessage(
            $"/tp ={destinationX.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationY.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationZ.ToString("0.00", CultureInfo.InvariantCulture)}",
            null!);
        api.Logger.Notification(
            "[VintageRTX.Test] Lantern night camera applied: lantern={0} at ({1},{2},{3}), camera-eye=({4:0.00},{5:0.00},{6:0.00}), authored-map=true, clear-ray=true, entity-bounds={7}.",
            lanternBlock.Code,
            lanternPosition.X,
            lanternPosition.Y,
            lanternPosition.Z,
            cameraEye.X,
            cameraEye.Y,
            cameraEye.Z,
            occupiedEntityBounds.Length);
        foreach (Entity nearbyEntity in nearbyEntities)
        {
            LanternCameraEntityBounds bounds = LanternCameraEntityBounds.FromEntity(nearbyEntity);
            api.Logger.Notification(
                "[VintageRTX.Test] Lantern camera entity witness: id={0}, code={1}, pos=({2:0.00},{3:0.00},{4:0.00}), bounds=({5:0.00},{6:0.00},{7:0.00})-({8:0.00},{9:0.00},{10:0.00}), eye-clearance={11:0.00}m.",
                nearbyEntity.EntityId,
                nearbyEntity.Code,
                nearbyEntity.Pos.X,
                nearbyEntity.Pos.Y,
                nearbyEntity.Pos.Z,
                bounds.MinimumX,
                bounds.MinimumY,
                bounds.MinimumZ,
                bounds.MaximumX,
                bounds.MaximumY,
                bounds.MaximumZ,
                Math.Sqrt(bounds.SquaredDistanceTo(cameraEye.X, cameraEye.Y, cameraEye.Z)));
        }
        return true;
    }

    /// <summary>Recognizes an active lantern from its authored block code and emitted HSV value.</summary>
    /// <param name="block">Candidate solid-layer block.</param>
    /// <param name="accessor">World accessor required by the collectible light API.</param>
    /// <param name="position">Candidate world position.</param>
    /// <returns><see langword="true"/> only for a lantern whose current light value is non-zero.</returns>
    internal static bool IsLitLantern(Block block, IBlockAccessor accessor, BlockPos position)
    {
        if (block is null
            || string.IsNullOrWhiteSpace(block.Code?.Path)
            || !block.Code.Path.StartsWith("lantern", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] lightHsv = block.GetLightHsv(accessor, position, null);
        return lightHsv is { Length: >= 3 } && lightHsv[2] > 0;
    }

    /// <summary>
    /// Selects the most open eye cell between three and six metres from a lantern while requiring
    /// an unobstructed half-block ray to the flame centre.
    /// </summary>
    /// <param name="accessor">Loaded real-map block accessor.</param>
    /// <param name="lantern">Authored lantern position.</param>
    /// <param name="occupiedEntityBounds">
    /// Animated selection volumes of non-player entities that must remain outside the camera's
    /// near-field safety envelope. This keeps a long animal mesh from covering a large part of a
    /// nominally pixel-comparable capture even when its centre remains several metres away.
    /// </param>
    /// <returns>World-space camera eye, or <see langword="null"/> when the lantern is enclosed.</returns>
    internal static Vec3d? FindLanternCameraEye(
        IBlockAccessor accessor,
        BlockPos lantern,
        IReadOnlyList<LanternCameraEntityBounds>? occupiedEntityBounds = null)
    {
        Vec3d? best = null;
        int bestScore = int.MinValue;
        for (int offsetX = -6; offsetX <= 6; offsetX++)
        {
            for (int offsetZ = -6; offsetZ <= 6; offsetZ++)
            {
                int distanceSquared = offsetX * offsetX + offsetZ * offsetZ;
                if (distanceSquared is < 9 or > 36)
                {
                    continue;
                }

                int eyeX = lantern.X + offsetX;
                int eyeY = lantern.Y;
                int eyeZ = lantern.Z + offsetZ;
                double eyeCenterX = eyeX + 0.5;
                double eyeCenterY = eyeY + 0.5;
                double eyeCenterZ = eyeZ + 0.5;
                if (!IsLanternCameraEntityClear(
                        eyeCenterX,
                        eyeCenterY,
                        eyeCenterZ,
                        occupiedEntityBounds)
                    || !IsOpenForCamera(accessor.GetBlock(new BlockPos(
                        eyeX,
                        eyeY,
                        eyeZ,
                        lantern.dimension)))
                    || !HasClearLanternSight(
                        accessor,
                        eyeCenterX,
                        eyeCenterY,
                        eyeCenterZ,
                        lantern))
                {
                    continue;
                }

                int openness = 0;
                for (int localX = -1; localX <= 1; localX++)
                {
                    for (int localY = -1; localY <= 1; localY++)
                    {
                        for (int localZ = -1; localZ <= 1; localZ++)
                        {
                            openness += IsOpenForCamera(accessor.GetBlock(new BlockPos(
                                eyeX + localX,
                                eyeY + localY,
                                eyeZ + localZ,
                                lantern.dimension))) ? 1 : 0;
                        }
                    }
                }

                int score = openness * 100 - Math.Abs(distanceSquared - 16);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new Vec3d(eyeCenterX, eyeCenterY, eyeCenterZ);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Rejects prospective eye cells closer than 0.75 metre to an animated non-player selection
    /// volume. Measuring from the volume rather than its centre accounts for long bodies, heads,
    /// horns, and other posed meshes that can cross the near frustum while their origin stays clear.
    /// </summary>
    /// <param name="eyeX">Prospective eye X coordinate.</param>
    /// <param name="eyeY">Prospective eye Y coordinate.</param>
    /// <param name="eyeZ">Prospective eye Z coordinate.</param>
    /// <param name="occupiedEntityBounds">World-space animated entity bounds, or no exclusions.</param>
    /// <returns>Whether the nearest supplied body volume remains at least 0.75 metre away.</returns>
    internal static bool IsLanternCameraEntityClear(
        double eyeX,
        double eyeY,
        double eyeZ,
        IReadOnlyList<LanternCameraEntityBounds>? occupiedEntityBounds)
    {
        if (occupiedEntityBounds is null)
        {
            return true;
        }

        const double minimumDistanceSquared = 0.75 * 0.75;
        foreach (LanternCameraEntityBounds bounds in occupiedEntityBounds)
        {
            if (bounds.SquaredDistanceTo(eyeX, eyeY, eyeZ) < minimumDistanceSquared)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks the segment from a prospective eye cell to the lantern at half-block spacing.</summary>
    /// <param name="accessor">Map blocks used as opaque witnesses.</param>
    /// <param name="eyeX">Eye X coordinate.</param>
    /// <param name="eyeY">Eye Y coordinate.</param>
    /// <param name="eyeZ">Eye Z coordinate.</param>
    /// <param name="lantern">Terminal lantern block, which is deliberately not treated as an obstruction.</param>
    /// <returns><see langword="true"/> when every intermediate cell is visually empty.</returns>
    private static bool HasClearLanternSight(
        IBlockAccessor accessor,
        double eyeX,
        double eyeY,
        double eyeZ,
        BlockPos lantern)
    {
        double targetX = lantern.X + 0.5;
        double targetY = lantern.Y + 0.5;
        double targetZ = lantern.Z + 0.5;
        double distance = Math.Sqrt(
            Math.Pow(targetX - eyeX, 2.0)
            + Math.Pow(targetY - eyeY, 2.0)
            + Math.Pow(targetZ - eyeZ, 2.0));
        int steps = Math.Max(2, (int)Math.Ceiling(distance * 2.0));
        for (int step = 1; step < steps; step++)
        {
            double amount = step / (double)steps;
            int sampleX = (int)Math.Floor(eyeX + (targetX - eyeX) * amount);
            int sampleY = (int)Math.Floor(eyeY + (targetY - eyeY) * amount);
            int sampleZ = (int)Math.Floor(eyeZ + (targetZ - eyeZ) * amount);
            if (sampleX == lantern.X && sampleY == lantern.Y && sampleZ == lantern.Z)
            {
                continue;
            }

            if (!IsVisuallyOpen(accessor.GetBlock(new BlockPos(
                sampleX,
                sampleY,
                sampleZ,
                lantern.dimension))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reasserts the captured camera position and angles through public player
    /// APIs. Motion is cleared on every application to prevent physics or camera
    /// sway from invalidating pixel-level A/B comparisons.
    /// </summary>
    private void ApplyLockedCameraState()
    {
        var player = api.World.Player;
        var entity = player.Entity;
        // The public client API exposes camera shake independently from the
        // player's world transform. Clear both that accumulator and the
        // EntityPlayer special-effect offset so hunger, damage, temporal
        // instability or a prior world state cannot perturb a fixed render
        // comparison after /gamemode 2 and teleportation.
        api.World.SetCameraShake(0.0f);
        entity.CameraPosOffset.Set(0.0, 0.0, 0.0);
        entity.HeadBobbingAmplitude = 0.0f;
        if (exteriorPositionLocked)
        {
            entity.Controls.IsFlying = true;
            entity.Controls.NoClip = true;
            entity.Pos.SetPos(exteriorPositionX, exteriorPositionY, exteriorPositionZ);
            entity.Pos.Motion.Set(0.0, 0.0, 0.0);
        }

        api.Input.MouseYaw = exteriorYaw;
        api.Input.MousePitch = exteriorPitch;
        float entityYaw = exteriorYaw;
        if (useDirectPublicCameraAngles)
        {
            MapDirectCameraToEntityAngles(
                exteriorYaw,
                exteriorPitch,
                out entityYaw,
                out exteriorEntityPitch);
        }

        entity.Pos.SetAngles(0.0f, entityYaw, exteriorEntityPitch);
        entity.BodyYaw = entityYaw;
        if (useDirectPublicCameraAngles)
        {
            player.CameraYaw = exteriorYaw;
            player.CameraPitch = exteriorPitch;
            player.CameraRoll = 0.0f;
        }
    }

    /// <summary>
    /// Advances the sinusoidal temporal-stability sweep by one game tick and
    /// restores the exact starting yaw when the 240-tick window completes.
    /// </summary>
    private void UpdateMovingCameraProbe()
    {
        if (movingCameraTicks <= 0)
        {
            return;
        }

        int elapsedTicks = MovingCameraDurationTicks - movingCameraTicks;
        float progress = elapsedTicks / (float)MovingCameraDurationTicks;
        api.Input.MouseYaw = movingCameraBaseYaw
            + MathF.Sin(progress * MathF.PI) * GameMath.DEG2RAD * 36.0f;
        movingCameraTicks--;
        if (movingCameraTicks > 0)
        {
            return;
        }

        api.Input.MouseYaw = movingCameraBaseYaw;
        api.Logger.Notification(
            "[VintageRTX.Test] Moving-camera probe completed: camera restored and temporal convergence resumed.");
    }

    /// <summary>
    /// Records the current framebuffer dimensions and requests a bounded
    /// alternate resolution through the public client command. Dimensions are
    /// expressed in physical framebuffer pixels and constrained to 640x480–3840x2160.
    /// </summary>
    private void BeginResizeAndReloadProbe()
    {
        originalFrameWidth = api.Render.FrameWidth;
        originalFrameHeight = api.Render.FrameHeight;
        if (originalFrameWidth <= 0 || originalFrameHeight <= 0)
        {
            resizeProbeState = ResizeProbeState.Failed;
            api.Logger.Error(
                "[VintageRTX.Test] Resize/reload verification: FAIL; the initial framebuffer size is invalid ({0}x{1}).",
                originalFrameWidth,
                originalFrameHeight);
            return;
        }

        alternateFrameWidth = originalFrameWidth >= 1200
            ? originalFrameWidth - 320
            : originalFrameWidth + 160;
        alternateFrameHeight = originalFrameHeight >= 720
            ? originalFrameHeight - 180
            : originalFrameHeight + 90;
        alternateFrameWidth = Math.Clamp(alternateFrameWidth, 640, 3840);
        alternateFrameHeight = Math.Clamp(alternateFrameHeight, 480, 2160);

        resizeProbeTicks = 0;
        resizeProbeState = ResizeProbeState.WaitingForAlternateSize;
        api.Logger.Notification(
            "[VintageRTX.Test] Resize requested through the public client command: {0}x{1} -> {2}x{3}.",
            originalFrameWidth,
            originalFrameHeight,
            alternateFrameWidth,
            alternateFrameHeight);
        api.TriggerChatMessage($".resolution {alternateFrameWidth} {alternateFrameHeight}");
    }

    /// <summary>
    /// Advances the asynchronous resize/reload/restore state machine. Each
    /// observed size is held for 15 ticks before the next transition, and any
    /// transition taking more than 250 ticks is treated as a failure.
    /// </summary>
    private void UpdateResizeAndReloadProbe()
    {
        if (resizeProbeState is ResizeProbeState.Inactive
            or ResizeProbeState.Complete
            or ResizeProbeState.Failed)
        {
            return;
        }

        resizeProbeTicks++;
        int width = api.Render.FrameWidth;
        int height = api.Render.FrameHeight;
        if (resizeProbeState == ResizeProbeState.WaitingForAlternateSize
            && width == alternateFrameWidth
            && height == alternateFrameHeight)
        {
            api.Logger.Notification(
                "[VintageRTX.Test] Alternate framebuffer size observed: {0}x{1}; holding it for VintageRTX rendering.",
                width,
                height);
            resizeProbeTicks = 0;
            resizeProbeState = ResizeProbeState.HoldingAlternateSize;
            return;
        }

        if (resizeProbeState == ResizeProbeState.HoldingAlternateSize)
        {
            if (width != alternateFrameWidth || height != alternateFrameHeight)
            {
                FailResizeProbe(alternateFrameWidth, alternateFrameHeight, width, height);
                return;
            }

            if (resizeProbeTicks < 15)
            {
                return;
            }

            if (!reloadShaders())
            {
                resizeProbeState = ResizeProbeState.Failed;
                api.Logger.Error(
                    "[VintageRTX.Test] Resize/reload verification: FAIL; the in-memory shader did not recompile.");
                return;
            }

            api.Logger.Notification(
                "[VintageRTX.Test] Shader reload at alternate framebuffer size: PASS; restoring {0}x{1}.",
                originalFrameWidth,
                originalFrameHeight);
            resizeProbeTicks = 0;
            resizeProbeState = ResizeProbeState.WaitingForOriginalSize;
            api.TriggerChatMessage($".resolution {originalFrameWidth} {originalFrameHeight}");
            return;
        }

        if (resizeProbeState == ResizeProbeState.WaitingForOriginalSize
            && width == originalFrameWidth
            && height == originalFrameHeight)
        {
            api.Logger.Notification(
                "[VintageRTX.Test] Original framebuffer size observed again: {0}x{1}; validating restored rendering.",
                width,
                height);
            resizeProbeTicks = 0;
            resizeProbeState = ResizeProbeState.HoldingOriginalSize;
            return;
        }

        if (resizeProbeState == ResizeProbeState.HoldingOriginalSize)
        {
            if (width != originalFrameWidth || height != originalFrameHeight)
            {
                FailResizeProbe(originalFrameWidth, originalFrameHeight, width, height);
                return;
            }

            if (resizeProbeTicks < 15)
            {
                return;
            }

            resizeProbeState = ResizeProbeState.Complete;
            api.Logger.Notification(
                "[VintageRTX.Test] Resize/reload verification: PASS; framebuffer restored to {0}x{1} after real GPU resource recreation.",
                width,
                height);
            return;
        }

        if (resizeProbeTicks <= 250)
        {
            return;
        }

        int expectedWidth = resizeProbeState is ResizeProbeState.WaitingForAlternateSize
            or ResizeProbeState.HoldingAlternateSize
            ? alternateFrameWidth
            : originalFrameWidth;
        int expectedHeight = resizeProbeState is ResizeProbeState.WaitingForAlternateSize
            or ResizeProbeState.HoldingAlternateSize
            ? alternateFrameHeight
            : originalFrameHeight;
        FailResizeProbe(expectedWidth, expectedHeight, width, height);
    }

    /// <summary>
    /// Terminates framebuffer validation and records the requested-versus-observed
    /// dimensions without attempting another resize transition.
    /// </summary>
    /// <param name="expectedWidth">Required framebuffer width in pixels.</param>
    /// <param name="expectedHeight">Required framebuffer height in pixels.</param>
    /// <param name="width">Observed framebuffer width in pixels.</param>
    /// <param name="height">Observed framebuffer height in pixels.</param>
    private void FailResizeProbe(int expectedWidth, int expectedHeight, int width, int height)
    {
        resizeProbeState = ResizeProbeState.Failed;
        api.Logger.Error(
            "[VintageRTX.Test] Resize/reload verification: FAIL; expected {0}x{1}, observed {2}x{3}.",
            expectedWidth,
            expectedHeight,
            width,
            height);
    }

    /// <summary>
    /// Finds an exposed flat patch in the loaded real save, requests five stock plants through the
    /// isolated public server command, and locks an elevated oblique camera on their sun receiver.
    /// </summary>
    /// <returns><see langword="true"/> when the placement request and camera pose were established.</returns>
    private bool TryApplyVegetationShadowMapCamera()
    {
        Vec3d origin = api.World.Player.Entity.CameraPos.Clone();
        IBlockAccessor accessor = api.World.BlockAccessor;
        BlockPos sample = api.World.Player.Entity.Pos.AsBlockPos.Copy();
        if (!TryFindVegetationStagingPatch(
                accessor,
                sample,
                (int)Math.Floor(origin.X),
                (int)Math.Floor(origin.Y),
                (int)Math.Floor(origin.Z),
                out vegetationCenterX,
                out int surfaceY,
                out vegetationCenterZ))
        {
            return false;
        }

        vegetationPlantY = surfaceY + 1;
        for (int index = 0; index < VegetationWitnesses.Length; index++)
        {
            VegetationWitness witness = VegetationWitnesses[index];
            sample.Set(
                vegetationCenterX + witness.OffsetX,
                0,
                vegetationCenterZ + witness.OffsetZ);
            vegetationWitnessPlantYs[index] = accessor.GetRainMapHeightAt(sample) + 1;
        }
        int fenceStackAwareBlocks = CountFenceStackAwareBlocksInChunk(
            accessor,
            sample,
            vegetationCenterX,
            vegetationPlantY,
            vegetationCenterZ,
            out int targetChunkX,
            out int targetChunkY,
            out int targetChunkZ);
        Block receiverGround = GetBlock(
            accessor,
            sample,
            vegetationCenterX,
            surfaceY,
            vegetationCenterZ);
        api.Logger.Notification(
            "[VintageRTX.Test] Vegetation map chunk audit: PASS | chunk=({0},{1},{2}), scanned={3}, BlockFenceStackAware={4}, receiver-ground={5}, material={6}.",
            targetChunkX,
            targetChunkY,
            targetChunkZ,
            GlobalConstants.ChunkSize * GlobalConstants.ChunkSize * GlobalConstants.ChunkSize,
            fenceStackAwareBlocks,
            receiverGround.Code,
            receiverGround.BlockMaterial);
        Vec3f sun = VoxelScene.ResolveSunDirection(
            api.World.Calendar,
            api.World.Player.Entity.CameraPos);
        double shadowX = -sun.X;
        double shadowZ = -sun.Z;
        double horizontalLength = Math.Sqrt(shadowX * shadowX + shadowZ * shadowZ);
        if (horizontalLength <= 0.001)
        {
            shadowX = 0.70710678118;
            shadowZ = 0.70710678118;
        }
        else
        {
            shadowX /= horizontalLength;
            shadowZ /= horizontalLength;
        }

        double perpendicularX = -shadowZ;
        double perpendicularZ = shadowX;
        double destinationX = vegetationCenterX + 0.5 - shadowX * 8.0 + perpendicularX * 4.0;
        double destinationY = vegetationPlantY + 5.25;
        double destinationZ = vegetationCenterZ + 0.5 - shadowZ * 8.0 + perpendicularZ * 4.0;
        double targetX = vegetationCenterX + 0.5 + shadowX * 1.35;
        double targetY = vegetationPlantY + 0.18;
        double targetZ = vegetationCenterZ + 0.5 + shadowZ * 1.35;
        SelectCameraOrientation(
            targetX - destinationX,
            targetY - (destinationY + 1.62),
            targetZ - destinationZ,
            useDirectCameraAngles: true);
        exteriorCameraLockTicks = 36_000;
        exteriorPositionLocked = true;
        exteriorPositionX = destinationX;
        exteriorPositionY = destinationY;
        exteriorPositionZ = destinationZ;
        api.SendChatMessage(
            $"/tp ={destinationX.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationY.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationZ.ToString("0.00", CultureInfo.InvariantCulture)}",
            null!);

        vegetationPlacementArmed = true;
        vegetationPlacementRequested = false;
        vegetationPlacementTicks = 0;
        Environment.SetEnvironmentVariable("VINTAGERTX_VEGETATION_MAP_READY", "staging");
        api.Logger.Notification(
            "[VintageRTX.Test] Vegetation map placement armed: count={0}, center=({1},{2},{3}), pre-mutation capture generation requested.",
            VegetationWitnesses.Length,
            vegetationCenterX,
            vegetationPlantY,
            vegetationCenterZ);
        api.Logger.Notification(
            "[VintageRTX.Test] Vegetation map camera applied: position=({0:0.00},{1:0.00},{2:0.00}), target=({3:0.00},{4:0.00},{5:0.00}), pitch={6:0.000}, sun=({7:0.000},{8:0.000},{9:0.000}), locked=true.",
            destinationX,
            destinationY,
            destinationZ,
            targetX,
            targetY,
            targetZ,
            exteriorPitch,
            sun.X,
            sun.Y,
            sun.Z);
        return true;
    }

    /// <summary>
    /// Waits for authoritative block replication, validates each real block's alpha-cut render
    /// contract, then releases automatic capture for a fresh settled voxel generation.
    /// </summary>
    private void UpdateVegetationShadowMapProbe()
    {
        if (!IsVegetationShadowMap
            || vegetationPatchVerified)
        {
            return;
        }

        vegetationPlacementTicks++;
        if (vegetationPlacementArmed && !vegetationPlacementRequested)
        {
            if (vegetationPlacementTicks < 5)
            {
                return;
            }

            api.SendChatMessage(
                $"/vintagertxtestvegetation place {vegetationCenterX.ToString(CultureInfo.InvariantCulture)} "
                + $"{vegetationPlantY.ToString(CultureInfo.InvariantCulture)} "
                + vegetationCenterZ.ToString(CultureInfo.InvariantCulture),
                null!);
            vegetationPlacementRequested = true;
            vegetationPlacementTicks = 0;
            api.Logger.Notification(
                "[VintageRTX.Test] Vegetation map placement requested: count={0}, center=({1},{2},{3}), command=/vintagertxtestvegetation place, world=foggy village world.",
                VegetationWitnesses.Length,
                vegetationCenterX,
                vegetationPlantY,
                vegetationCenterZ);
            return;
        }

        if (!vegetationPlacementRequested)
        {
            return;
        }

        if (vegetationPlacementTicks < 25)
        {
            return;
        }

        IBlockAccessor accessor = api.World.BlockAccessor;
        List<(VegetationWitness Witness, BlockPos Position, Block Block)> observed = [];
        for (int index = 0; index < VegetationWitnesses.Length; index++)
        {
            VegetationWitness witness = VegetationWitnesses[index];
            BlockPos position = new(
                vegetationCenterX + witness.OffsetX,
                vegetationWitnessPlantYs[index],
                vegetationCenterZ + witness.OffsetZ);
            Block? candidate = accessor.GetBlock(position, BlockLayersAccess.MostSolid);
            if (candidate is null
                || !string.Equals(candidate.Code?.ToString(), witness.Code, StringComparison.Ordinal))
            {
                if (vegetationPlacementTicks == 750)
                {
                    api.Logger.Error(
                        "[VintageRTX.Test] Vegetation map placement verification timed out: expected={0} at {1}, observed={2}.",
                        witness.Code,
                        position,
                        candidate?.Code?.ToString() ?? "missing");
                }
                return;
            }

            Block block = candidate;
            observed.Add((witness, position, block));
        }

        int alphaCutout = 0;
        int crossedPlanes = 0;
        int jsonShapes = 0;
        for (int index = 0; index < observed.Count; index++)
        {
            (VegetationWitness witness, BlockPos position, Block block) = observed[index];
            bool alpha = block.RenderPass == EnumChunkRenderPass.OpaqueNoCull;
            bool crossed = block.DrawType == EnumDrawType.Cross;
            bool json = block.DrawType == EnumDrawType.JSON;
            alphaCutout += alpha ? 1 : 0;
            crossedPlanes += crossed ? 1 : 0;
            jsonShapes += json ? 1 : 0;
            api.Logger.Notification(
                "[VintageRTX.Test] Vegetation map block verified: index={0}/{1}, code={2}, position={3}, draw-type={4}, render-pass={5}, alpha-cutout={6}, crossed-planes={7}.",
                index + 1,
                observed.Count,
                witness.Code,
                position,
                block.DrawType,
                block.RenderPass,
                alpha,
                crossed);
        }

        vegetationPatchVerified = true;
        if (observed.Count != VegetationWitnesses.Length
            || alphaCutout != VegetationWitnesses.Length
            || crossedPlanes != 3
            || jsonShapes != 2)
        {
            api.Logger.Error(
                "[VintageRTX.Test] Vegetation map geometry verified: FAIL | count={0}, alpha-cutout={1}, crossed-planes={2}, json-shapes={3}.",
                observed.Count,
                alphaCutout,
                crossedPlanes,
                jsonShapes);
            return;
        }

        api.Logger.Notification(
            "[VintageRTX.Test] Vegetation map geometry verified: PASS | count=5, alpha-cutout=5, crossed-planes=3, json-shapes=2, receiver=real-map-terrain.");
        Environment.SetEnvironmentVariable("VINTAGERTX_VEGETATION_MAP_READY", "1");
        api.Logger.Notification(
            "[VintageRTX.Test] Vegetation map capture gate released after authoritative placement, client geometry verification, and camera lock.");
    }

    /// <summary>
    /// Searches the loaded terrain around the starting player for the nearest gently sloped,
    /// rain-exposed patch large enough for the five authored witness offsets. Existing harmless
    /// ground plants are replaceable on the disposable save copy and must not make every natural
    /// field ineligible.
    /// </summary>
    /// <param name="accessor">Real-world block and rain-map accessor.</param>
    /// <param name="sample">Reusable mutable coordinate.</param>
    /// <param name="originX">Player-origin X.</param>
    /// <param name="originY">Player-origin Y.</param>
    /// <param name="originZ">Player-origin Z.</param>
    /// <param name="centerX">Selected patch-center X.</param>
    /// <param name="surfaceY">Selected common ground-surface Y.</param>
    /// <param name="centerZ">Selected patch-center Z.</param>
    /// <returns>Whether a loaded qualifying patch was found.</returns>
    internal static bool TryFindVegetationStagingPatch(
        IBlockAccessor accessor,
        BlockPos sample,
        int originX,
        int originY,
        int originZ,
        out int centerX,
        out int surfaceY,
        out int centerZ)
    {
        bool TryCandidate(int x, int z, out int candidateSurfaceY)
        {
            sample.Set(x, 0, z);
            candidateSurfaceY = accessor.GetRainMapHeightAt(sample);
            int plantY = candidateSurfaceY + 1;
            return IsVegetationSurfaceWithinTeleportRange(originY, candidateSurfaceY)
                && AreVegetationWitnessesInSingleChunk(x, plantY, z)
                && HasVegetationStagingPatch(
                    accessor,
                    sample,
                    x,
                    candidateSurfaceY,
                    z);
        }

        // Visit every integer center on expanding square rings. The former angular sampling left
        // large holes between rounded circumference points and repeatedly missed narrow village
        // clearings even though those cells were already loaded by the single-player view range.
        for (int radius = 8; radius <= 72; radius++)
        {
            for (int xOffset = -radius; xOffset <= radius; xOffset++)
            {
                int x = originX + xOffset;
                int nearZ = originZ - radius;
                if (TryCandidate(x, nearZ, out int nearSurfaceY))
                {
                    centerX = x;
                    surfaceY = nearSurfaceY;
                    centerZ = nearZ;
                    return true;
                }

                int farZ = originZ + radius;
                if (TryCandidate(x, farZ, out int farSurfaceY))
                {
                    centerX = x;
                    surfaceY = farSurfaceY;
                    centerZ = farZ;
                    return true;
                }
            }

            for (int zOffset = -radius + 1; zOffset < radius; zOffset++)
            {
                int z = originZ + zOffset;
                int nearX = originX - radius;
                if (TryCandidate(nearX, z, out int nearSurfaceY))
                {
                    centerX = nearX;
                    surfaceY = nearSurfaceY;
                    centerZ = z;
                    return true;
                }

                int farX = originX + radius;
                if (TryCandidate(farX, z, out int farSurfaceY))
                {
                    centerX = farX;
                    surfaceY = farSurfaceY;
                    centerZ = z;
                    return true;
                }
            }
        }

        centerX = 0;
        surfaceY = 0;
        centerZ = 0;
        return false;
    }

    /// <summary>
    /// Allows the copied save's creative/flying spawn to relocate onto nearby ground without
    /// accepting an unrelated vertical world layer. The foggy-village save starts roughly forty
    /// blocks above its surrounding natural receivers.
    /// </summary>
    /// <param name="originY">Current player/camera block elevation.</param>
    /// <param name="surfaceY">Rain-map elevation of the candidate receiver.</param>
    /// <returns>Whether a direct scenario teleport remains within the local 96-block column.</returns>
    internal static bool IsVegetationSurfaceWithinTeleportRange(int originY, int surfaceY) =>
        Math.Abs((long)surfaceY - originY) <= 96L;

    /// <summary>
    /// Counts fence-stack-aware blocks in the exact 32-cubed chunk that will own every plant.
    /// The count is retained as evidence that the complete mesh owner was audited. The production
    /// array guard now permits a nonzero count while the runtime validator still rejects any
    /// correlated tessellation abort after vegetation placement.
    /// </summary>
    /// <param name="accessor">Loaded real-world accessor.</param>
    /// <param name="sample">Reusable mutable coordinate.</param>
    /// <param name="worldX">Representative world X.</param>
    /// <param name="worldY">Representative world Y.</param>
    /// <param name="worldZ">Representative world Z.</param>
    /// <param name="chunkX">Receives signed chunk X.</param>
    /// <param name="chunkY">Receives signed chunk Y.</param>
    /// <param name="chunkZ">Receives signed chunk Z.</param>
    /// <returns>Number of blocks whose runtime type is <c>BlockFenceStackAware</c>.</returns>
    internal static int CountFenceStackAwareBlocksInChunk(
        IBlockAccessor accessor,
        BlockPos sample,
        int worldX,
        int worldY,
        int worldZ,
        out int chunkX,
        out int chunkY,
        out int chunkZ)
    {
        (chunkX, chunkY, chunkZ) = WorldToChunk(worldX, worldY, worldZ);
        int minimumX = chunkX * GlobalConstants.ChunkSize;
        int minimumY = chunkY * GlobalConstants.ChunkSize;
        int minimumZ = chunkZ * GlobalConstants.ChunkSize;
        int count = 0;
        for (int y = minimumY; y < minimumY + GlobalConstants.ChunkSize; y++)
        {
            for (int z = minimumZ; z < minimumZ + GlobalConstants.ChunkSize; z++)
            {
                for (int x = minimumX; x < minimumX + GlobalConstants.ChunkSize; x++)
                {
                    Block block = GetBlock(
                        accessor,
                        sample,
                        x,
                        y,
                        z,
                        BlockLayersAccess.MostSolid);
                    count += string.Equals(
                        block?.GetType().Name,
                        "BlockFenceStackAware",
                        StringComparison.Ordinal)
                            ? 1
                            : 0;
                }
            }
        }

        return count;
    }

    /// <summary>Ensures the complete five-plant layout belongs to one 32-cubed mesh owner.</summary>
    /// <param name="centerX">Patch center X.</param>
    /// <param name="plantY">Common plant-layer Y.</param>
    /// <param name="centerZ">Patch center Z.</param>
    /// <returns>Whether all authored offsets share one chunk coordinate.</returns>
    internal static bool AreVegetationWitnessesInSingleChunk(
        int centerX,
        int plantY,
        int centerZ)
    {
        (int X, int Y, int Z) owner = WorldToChunk(centerX, plantY, centerZ);
        return VegetationWitnesses.All(witness => WorldToChunk(
            centerX + witness.OffsetX,
            plantY,
            centerZ + witness.OffsetZ) == owner);
    }

    /// <summary>Maps signed world coordinates to floor-divided chunk coordinates.</summary>
    /// <param name="worldX">World X.</param>
    /// <param name="worldY">World Y.</param>
    /// <param name="worldZ">World Z.</param>
    /// <returns>Signed owner chunk coordinates.</returns>
    private static (int X, int Y, int Z) WorldToChunk(int worldX, int worldY, int worldZ) =>
        ((int)Math.Floor(worldX / (double)GlobalConstants.ChunkSize),
            (int)Math.Floor(worldY / (double)GlobalConstants.ChunkSize),
            (int)Math.Floor(worldZ / (double)GlobalConstants.ChunkSize));

    /// <summary>
    /// Verifies every plant target stays within two blocks of the center ground, has solid natural
    /// support and two air-or-ground-plant cells. This retains a real slope instead of requiring an
    /// artificial platform; trees, crops, constructions, and collidable content remain rejected.
    /// </summary>
    /// <param name="accessor">Block accessor to inspect.</param>
    /// <param name="sample">Reusable mutable position.</param>
    /// <param name="centerX">Candidate center X.</param>
    /// <param name="surfaceY">Required common surface Y.</param>
    /// <param name="centerZ">Candidate center Z.</param>
    /// <returns>Whether all five targets satisfy the non-destructive staging contract.</returns>
    internal static bool HasVegetationStagingPatch(
        IBlockAccessor accessor,
        BlockPos sample,
        int centerX,
        int surfaceY,
        int centerZ)
    {
        (int X, int Y, int Z) owner = WorldToChunk(
            centerX,
            surfaceY + 1,
            centerZ);
        Block centerGround = GetBlock(accessor, sample, centerX, surfaceY, centerZ);
        if (!IsCaveSolid(centerGround)
            || !IsVegetationReceiverGround(centerGround)
            || !IsReplaceableVegetationCell(GetBlock(
                accessor,
                sample,
                centerX,
                surfaceY + 1,
                centerZ)))
        {
            return false;
        }
        foreach (VegetationWitness witness in VegetationWitnesses)
        {
            int x = centerX + witness.OffsetX;
            int z = centerZ + witness.OffsetZ;
            sample.Set(x, 0, z);
            int witnessSurfaceY = accessor.GetRainMapHeightAt(sample);
            Block ground = GetBlock(accessor, sample, x, witnessSurfaceY, z);
            if (Math.Abs(witnessSurfaceY - surfaceY) > 2
                || WorldToChunk(x, witnessSurfaceY + 1, z) != owner
                || !IsCaveSolid(ground)
                || !IsVegetationReceiverGround(ground)
                || !IsReplaceableVegetationCell(GetBlock(
                    accessor,
                    sample,
                    x,
                    witnessSurfaceY + 1,
                    z))
                || !IsReplaceableVegetationCell(GetBlock(
                    accessor,
                    sample,
                    x,
                    witnessSurfaceY + 2,
                    z)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rejects roofs and constructed floors while accepting exposed natural terrain.</summary>
    /// <param name="block">Solid support block below one staged plant.</param>
    /// <returns>Whether the material is a natural outdoor receiver.</returns>
    internal static bool IsVegetationReceiverGround(Block? block) => block?.BlockMaterial is
        EnumBlockMaterial.Soil
        or EnumBlockMaterial.Gravel
        or EnumBlockMaterial.Sand
        or EnumBlockMaterial.Stone;

    /// <summary>
    /// Accepts empty cells and non-collidable ground plants that the isolated server witness may
    /// replace. Leaves and every other material remain real scene geometry and are never cleared.
    /// </summary>
    /// <param name="block">Solid-layer content above a candidate receiver.</param>
    /// <returns>Whether the disposable scenario may replace this cell with one authored plant.</returns>
    internal static bool IsReplaceableVegetationCell(Block? block) => block is null
        || block.Id == 0
        || block.BlockMaterial == EnumBlockMaterial.Air
        || (block.BlockMaterial == EnumBlockMaterial.Plant
            && block.CollisionBoxes is not { Length: > 0 });

    /// <summary>
    /// Finds an open receiver down-sun from the current roof and locks a camera
    /// that shows the complete long-distance roof shadow. Rain validation also
    /// requires a nearby exposed/sheltered pair before accepting the position.
    /// </summary>
    /// <returns><see langword="true"/> when a loaded, unobstructed receiver was found and the teleport was issued.</returns>
    private bool TryApplyExteriorRoofCamera()
    {
        Vec3d playerOrigin = api.World.Player.Entity.CameraPos.Clone();
        BlockPos sample = api.World.Player.Entity.Pos.AsBlockPos.Copy();
        IBlockAccessor accessor = api.World.BlockAccessor;
        EntityPos? defaultSpawn = api.World.DefaultSpawnPosition;
        int searchCenterX = defaultSpawn is null
            ? (int)Math.Floor(playerOrigin.X)
            : (int)Math.Floor(defaultSpawn.X);
        int searchCenterZ = defaultSpawn is null
            ? (int)Math.Floor(playerOrigin.Z)
            : (int)Math.Floor(defaultSpawn.Z);
        if (!TryFindExteriorRoofAnchor(
                accessor,
                sample,
                searchCenterX,
                searchCenterZ,
                out int originX,
                out int originRoofY,
                out int originZ,
                out Block roofBlock))
        {
            api.Logger.Error(
                "[VintageRTX.Test] Exterior roof camera rejected: no physical roof anchor was found within 384 blocks of world spawn ({0},{1}).",
                searchCenterX,
                searchCenterZ);
            return false;
        }

        int originY = originRoofY - 2;
        Vec3d origin = new(originX + 0.5, originY, originZ + 0.5);
        Vec3f sunDirection = VoxelScene.ResolveSunDirection(
            api.World.Calendar,
            origin);
        api.Logger.Notification(
            "[VintageRTX.Test] Exterior physical roof anchor selected: {0} at ({1},{2},{3}), spawn search center=({4},{5}).",
            roofBlock.Code?.ToString() ?? $"block-{roofBlock.Id}",
            originX,
            originRoofY,
            originZ,
            searchCenterX,
            searchCenterZ);
        if (sunDirection.Y <= 0.05f)
        {
            api.Logger.Error(
                "[VintageRTX.Test] Exterior roof camera rejected: sun is below the usable low-angle threshold ({0:0.000}).",
                sunDirection.Y);
            return false;
        }

        double shadowX = -sunDirection.X;
        double shadowZ = -sunDirection.Z;
        double shadowHorizontal = Math.Sqrt(shadowX * shadowX + shadowZ * shadowZ);
        if (shadowHorizontal <= 0.000001)
        {
            // The exterior-roof scenario requires an exposed, horizontally projected witness.
            // A vertical sun casts underneath the roof, not onto that exterior witness. Reject
            // this unsuitable scenario setup rather than inventing a distant shadow target.
            api.Logger.Error("[VintageRTX.Test] Exterior roof camera rejected: vertical sun has no exposed horizontal shadow witness.");
            return false;
        }
        if (shadowHorizontal > 0.001)
        {
            shadowX /= shadowHorizontal;
            shadowZ /= shadowHorizontal;
        }

        int bestX = 0;
        int bestY = 0;
        int bestZ = 0;
        bool bestRepresentativeReceiver = false;
        double bestScore = double.MaxValue;
        for (int radius = 10; radius <= 48; radius++)
        {
            for (int direction = 0; direction < 72; direction++)
            {
                double angle = direction * Math.PI * 2.0 / 72.0;
                int x = originX + (int)Math.Round(Math.Cos(angle) * radius);
                int z = originZ + (int)Math.Round(Math.Sin(angle) * radius);
                sample.Set(x, 0, z);
                int surfaceY = accessor.GetRainMapHeightAt(sample);
                if (surfaceY < originRoofY - 20 || surfaceY > originRoofY - 2)
                {
                    continue;
                }

                Block feet = GetBlock(accessor, sample, x, surfaceY + 1, z);
                Block head = GetBlock(accessor, sample, x, surfaceY + 2, z);
                if (!IsOpenForCamera(feet)
                    || !IsOpenForCamera(head)
                    || !HasOpenGroundPatch(accessor, sample, x, surfaceY, z)
                    || !HasRoofLineOfSight(
                        accessor,
                        sample,
                        x,
                        surfaceY,
                        z,
                        originX,
                        originRoofY,
                        originZ))
                {
                    continue;
                }

                bool representativeReceiver = HasRepresentativeExteriorReceiverPatch(
                    accessor,
                    sample,
                    x,
                    surfaceY,
                    z);

                double offsetLength = Math.Sqrt(
                    (x - originX) * (double)(x - originX)
                    + (z - originZ) * (double)(z - originZ));
                double alignment = offsetLength > 0.001 && shadowHorizontal > 0.001
                    ? ((x - originX) * shadowX + (z - originZ) * shadowZ) / offsetLength
                    : 0.0;
                double score = Math.Abs(radius - 24) * 1.5
                    + Math.Abs(surfaceY - (originY - 3)) * 1.75
                    + (representativeReceiver ? 0.0 : 18.0)
                    + (1.0 - alignment) * 42.0;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestX = x;
                bestY = surfaceY + 1;
                bestZ = z;
                bestRepresentativeReceiver = representativeReceiver;
            }
        }

        if (bestScore == double.MaxValue)
        {
            return false;
        }

        if (scenario == "rain-wetness")
        {
            int shelteredGroundY = int.MinValue;
            for (int y = originY; y >= originY - 12; y--)
            {
                if (IsCaveSolid(GetBlock(accessor, sample, originX, y, originZ)))
                {
                    shelteredGroundY = y;
                    break;
                }
            }

            sample.Set(bestX, 0, bestZ);
            int exposedRainY = accessor.GetRainMapHeightAt(sample);
            bool exposedMatches = Math.Abs(exposedRainY - (bestY - 1)) <= 1;
            bool shelterMatches = shelteredGroundY != int.MinValue
                && originRoofY >= shelteredGroundY + 2;
            if (!exposedMatches || !shelterMatches)
            {
                api.Logger.Error(
                    "[VintageRTX.Test] Rain wetness exposure pair failed: exposed ground y={0}, rain y={1}, sheltered ground y={2}, roof/rain y={3}.",
                    bestY - 1,
                    exposedRainY,
                    shelteredGroundY,
                    originRoofY);
                return false;
            }

            api.Logger.Notification(
                "[VintageRTX.Test] Rain wetness exposure pair verified: exposed ground y={0}, rain y={1}; sheltered ground y={2}, roof/rain y={3}.",
                bestY - 1,
                exposedRainY,
                shelteredGroundY,
                originRoofY);
        }

        double destinationX = bestX + 0.5;
        // A real outdoor receiver commonly carries grass, flowers or shrubs.
        // Keep those blocks in the scene (their shadows are part of the test),
        // but put the creative-mode camera above their local canopy so the
        // first-person view never starts inside an alpha-cutout plane.
        double destinationY = bestY + 3.05;
        double destinationZ = bestZ + 0.5;
        double receiverDistance = Math.Sqrt(
            (origin.X - destinationX) * (origin.X - destinationX)
            + (origin.Z - destinationZ) * (origin.Z - destinationZ));
        // Aim at the physical intersection of a ray leaving the roof along the
        // opposite solar direction. A fixed midpoint is not a shadow witness:
        // at a high solar elevation it lies well beyond the geometrically
        // possible roof shadow and makes a correct trace look artificially
        // short. The ray must first leave every sloped roof cell; treating the
        // roof height itself as ground collapses the result back onto the caster.
        if (!TryFindProjectedSunShadowTarget(
                accessor,
                sample,
                origin,
                originRoofY + 1.0,
                shadowX,
                shadowZ,
                sunDirection,
                Math.Max(1.5, receiverDistance - 2.0),
                out double targetX,
                out double targetY,
                out double targetZ,
                out double physicalShadowDistance))
        {
            api.Logger.Error(
                "[VintageRTX.Test] Exterior roof camera rejected: the solar ray did not reach exposed ground before the selected camera.");
            return false;
        }
        double dx = targetX - destinationX;
        double dy = targetY - (destinationY + 1.62);
        double dz = targetZ - destinationZ;
        // Camera pitch is not a conventional signed look angle on every game
        // path. Solve it through the public EntityPos.GetViewVector contract,
        // as the water and cave probes do, then hold both pose and position for
        // the entire capture/benchmark window. The former direct formula could
        // wrap a downward target to > PI and leave the test staring at the sky.
        SelectCameraOrientation(dx, dy, dz, useDirectCameraAngles: true);
        exteriorCameraLockTicks = 36_000;
        exteriorPositionLocked = true;
        exteriorPositionX = destinationX;
        exteriorPositionY = destinationY;
        exteriorPositionZ = destinationZ;

        api.SendChatMessage(
            $"/tp ={destinationX.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationY.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationZ.ToString("0.00", CultureInfo.InvariantCulture)}",
            null!);
        api.Logger.Notification(
            "[VintageRTX.Test] Exterior roof camera applied: roof={0} at ({1:0.00},{2:0.00},{3:0.00}), camera=({4:0.00},{5:0.00},{6:0.00}), physical shadow target=({7:0.00},{8:0.00},{9:0.00}), receiver distance={10:0.0}, projected shadow distance={11:0.0}, pitch={12:0.000}, sun=({13:0.000},{14:0.000},{15:0.000}), representative receiver={16}, sun range=64.",
            roofBlock.Code?.ToString() ?? $"block-{roofBlock.Id}",
            origin.X,
            origin.Y,
            origin.Z,
            destinationX,
            destinationY,
            destinationZ,
            targetX,
            targetY,
            targetZ,
            receiverDistance,
            physicalShadowDistance,
            exteriorPitch,
            sunDirection.X,
            sunDirection.Y,
            sunDirection.Z,
            bestRepresentativeReceiver);
        return true;
    }

    /// <summary>
    /// Searches loaded terrain for a covered, low-sunlight cave volume with a
    /// visible wall, then positions both camera and reference point light. The
    /// public engine light map is used so unloaded space is never mistaken for a cave.
    /// </summary>
    /// <returns><see langword="true"/> when a qualifying cave and target wall were found.</returns>
    private bool TryApplyCaveInteriorCamera()
    {
        Vec3d origin = api.World.Player.Entity.CameraPos.Clone();
        IBlockAccessor accessor = api.World.BlockAccessor;
        BlockPos sample = api.World.Player.Entity.Pos.AsBlockPos.Copy();
        int originX = (int)Math.Floor(origin.X);
        int originZ = (int)Math.Floor(origin.Z);
        HashSet<(int X, int Z)> testedColumns = [];

        double bestScore = double.MaxValue;
        int bestX = 0;
        int bestY = 0;
        int bestZ = 0;
        int bestRoofDistance = 0;
        int bestSunLight = 0;
        int bestBlockLight = 0;
        int bestOpenCells = 0;
        int bestWallDistance = 0;
        double bestTargetX = 0.0;
        double bestTargetY = 0.0;
        double bestTargetZ = 0.0;

        // GetLightLevel(OnlySunLight) is the public, engine-lit signal that
        // distinguishes a cave from an outdoor hollow. Unloaded chunks report
        // the default sunlight value, so they are naturally rejected rather
        // than mistaken for dark geometry.
        for (int radius = 0; radius <= 96; radius += 4)
        {
            int directionCount = radius == 0 ? 1 : 64;
            for (int direction = 0; direction < directionCount; direction++)
            {
                double angle = direction * Math.PI * 2.0 / directionCount;
                int x = originX + (int)Math.Round(Math.Cos(angle) * radius);
                int z = originZ + (int)Math.Round(Math.Sin(angle) * radius);
                if (!testedColumns.Add((x, z)))
                {
                    continue;
                }

                sample.Set(x, 0, z);
                int rainHeight = accessor.GetRainMapHeightAt(sample);
                int minimumY = Math.Max(4, rainHeight - 80);
                for (int y = rainHeight - 5; y >= minimumY; y--)
                {
                    if (!IsOpenForCamera(GetBlock(accessor, sample, x, y, z))
                        || !IsOpenForCamera(GetBlock(accessor, sample, x, y + 1, z))
                        || !IsCaveSolid(GetBlock(accessor, sample, x, y - 1, z)))
                    {
                        continue;
                    }

                    sample.Set(x, y, z);
                    int sunLight = accessor.GetLightLevel(sample, EnumLightLevelType.OnlySunLight);
                    int blockLight = accessor.GetLightLevel(sample, EnumLightLevelType.OnlyBlockLight);
                    if (sunLight > 2 || blockLight > 8)
                    {
                        continue;
                    }

                    int roofDistance = FindCaveRoofDistance(accessor, sample, x, y, z);
                    if (roofDistance is < 3 or > 14)
                    {
                        continue;
                    }

                    int openCells = CountCaveOpenCells(accessor, sample, x, y, z);
                    if (openCells < 28)
                    {
                        continue;
                    }

                    if (!TryFindCaveWallTarget(
                        accessor,
                        sample,
                        x,
                        y,
                        z,
                        out double targetX,
                        out double targetY,
                        out double targetZ,
                        out int wallDistance))
                    {
                        continue;
                    }

                    int depth = rainHeight - y;
                    double score = radius * 0.08
                        + Math.Abs(wallDistance - 8) * 2.0
                        + Math.Abs(roofDistance - 7) * 0.75
                        + Math.Abs(depth - 28) * 0.10
                        + sunLight * 8.0
                        + blockLight * 1.5
                        - openCells * 0.06;
                    if (score >= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    bestX = x;
                    bestY = y;
                    bestZ = z;
                    bestRoofDistance = roofDistance;
                    bestSunLight = sunLight;
                    bestBlockLight = blockLight;
                    bestOpenCells = openCells;
                    bestWallDistance = wallDistance;
                    bestTargetX = targetX;
                    bestTargetY = targetY;
                    bestTargetZ = targetZ;
                }
            }
        }

        if (bestScore == double.MaxValue)
        {
            api.Logger.Notification(
                "[VintageRTX.Test] Cave scan exhausted: radius=96, tested columns={0}, no covered low-sun open volume.",
                testedColumns.Count);
            return false;
        }

        double destinationX = bestX + 0.5;
        double destinationY = bestY + 0.05;
        double destinationZ = bestZ + 0.5;
        double dx = bestTargetX - destinationX;
        double dy = bestTargetY - (destinationY + 1.62);
        double dz = bestTargetZ - destinationZ;
        SelectCameraOrientation(dx, dy, dz);
        exteriorCameraLockTicks = 36_000;
        exteriorPositionLocked = true;
        exteriorPositionX = destinationX;
        exteriorPositionY = destinationY;
        exteriorPositionZ = destinationZ;

        double horizontalLength = Math.Max(Math.Sqrt(dx * dx + dz * dz), 0.001);
        double lightAdvance = Math.Min(2.2, horizontalLength * 0.35);
        caveLightPosition.Set(
            destinationX + dx / horizontalLength * lightAdvance,
            destinationY + 1.35,
            destinationZ + dz / horizontalLength * lightAdvance);
        caveLightPositionSet = true;

        api.SendChatMessage(
            $"/tp ={destinationX.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationY.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"={destinationZ.ToString("0.00", CultureInfo.InvariantCulture)}",
            null!);
        sample.Set(bestX, 0, bestZ);
        int bestDepth = accessor.GetRainMapHeightAt(sample) - bestY;
        api.Logger.Notification(
            "[VintageRTX.Test] Cave interior camera applied: camera=({0:0.00},{1:0.00},{2:0.00}), target=({3:0.00},{4:0.00},{5:0.00}), light=({6:0.00},{7:0.00},{8:0.00}), depth={9}, roof={10}, wall={11}, open cells={12}/75, OnlySunLight={13}, OnlyBlockLight={14}.",
            destinationX,
            destinationY,
            destinationZ,
            bestTargetX,
            bestTargetY,
            bestTargetZ,
            caveLightPosition.X,
            caveLightPosition.Y,
            caveLightPosition.Z,
            bestDepth,
            bestRoofDistance,
            bestWallDistance,
            bestOpenCells,
            bestSunLight,
            bestBlockLight);
        return true;
    }

    /// <summary>
    /// Measures the first collidable roof above a camera cell, ignoring the
    /// player's two occupied blocks and limiting the search to the accepted cave depth.
    /// </summary>
    /// <param name="accessor">Block accessor for the loaded world.</param>
    /// <param name="sample">Reusable mutable coordinate used to avoid allocation in the scan.</param>
    /// <param name="x">Camera column X coordinate in blocks.</param>
    /// <param name="y">Camera feet Y coordinate in blocks.</param>
    /// <param name="z">Camera column Z coordinate in blocks.</param>
    /// <returns>The roof offset in blocks in [2, 14], or -1 when no acceptable roof exists.</returns>
    internal static int FindCaveRoofDistance(
        IBlockAccessor accessor,
        BlockPos sample,
        int x,
        int y,
        int z)
    {
        for (int offset = 2; offset <= 14; offset++)
        {
            if (IsCaveSolid(GetBlock(accessor, sample, x, y + offset, z)))
            {
                return offset;
            }
        }

        return -1;
    }

    /// <summary>
    /// Counts camera-passable blocks in the 5x3x5 neighborhood around a cave
    /// candidate. The maximum is 75 and the caller requires at least 28 to
    /// reject narrow cracks that cannot show representative indirect lighting.
    /// </summary>
    /// <param name="accessor">Block accessor for the loaded world.</param>
    /// <param name="sample">Reusable mutable coordinate used by all 75 samples.</param>
    /// <param name="centerX">Neighborhood center X coordinate in blocks.</param>
    /// <param name="centerY">Neighborhood base Y coordinate in blocks.</param>
    /// <param name="centerZ">Neighborhood center Z coordinate in blocks.</param>
    /// <returns>The number of passable cells, from 0 through 75.</returns>
    internal static int CountCaveOpenCells(
        IBlockAccessor accessor,
        BlockPos sample,
        int centerX,
        int centerY,
        int centerZ)
    {
        int open = 0;
        for (int offsetY = 0; offsetY <= 2; offsetY++)
        {
            for (int offsetZ = -2; offsetZ <= 2; offsetZ++)
            {
                for (int offsetX = -2; offsetX <= 2; offsetX++)
                {
                    open += IsOpenForCamera(GetBlock(
                        accessor,
                        sample,
                        centerX + offsetX,
                        centerY + offsetY,
                        centerZ + offsetZ))
                        ? 1
                        : 0;
                }
            }
        }

        return open;
    }

    /// <summary>
    /// Casts horizontal radial probes for a collidable cave wall five to
    /// thirteen blocks from the camera and favors the eight-block reference distance.
    /// </summary>
    /// <param name="accessor">Block accessor for the loaded world.</param>
    /// <param name="sample">Reusable mutable coordinate for radial samples.</param>
    /// <param name="cameraX">Camera X coordinate in blocks.</param>
    /// <param name="cameraY">Camera feet Y coordinate in blocks.</param>
    /// <param name="cameraZ">Camera Z coordinate in blocks.</param>
    /// <param name="targetX">Receives the center X coordinate of the selected wall block.</param>
    /// <param name="targetY">Receives the eye-level Y coordinate used as the camera target.</param>
    /// <param name="targetZ">Receives the center Z coordinate of the selected wall block.</param>
    /// <param name="wallDistance">Receives the integer camera-to-wall distance in blocks.</param>
    /// <returns><see langword="true"/> when a collidable wall exists in the accepted range.</returns>
    internal static bool TryFindCaveWallTarget(
        IBlockAccessor accessor,
        BlockPos sample,
        int cameraX,
        int cameraY,
        int cameraZ,
        out double targetX,
        out double targetY,
        out double targetZ,
        out int wallDistance)
    {
        double bestScore = double.MaxValue;
        targetX = 0.0;
        targetY = 0.0;
        targetZ = 0.0;
        wallDistance = 0;
        for (int direction = 0; direction < 64; direction++)
        {
            double angle = direction * Math.PI * 2.0 / 64.0;
            double directionX = Math.Cos(angle);
            double directionZ = Math.Sin(angle);
            for (int distance = 2; distance <= 13; distance++)
            {
                int x = cameraX + (int)Math.Round(directionX * distance);
                int z = cameraZ + (int)Math.Round(directionZ * distance);
                Block eyeBlock = GetBlock(accessor, sample, x, cameraY + 1, z);
                if (IsOpenForCamera(eyeBlock))
                {
                    continue;
                }

                if (distance < 5 || !IsCaveSolid(eyeBlock))
                {
                    break;
                }

                double score = Math.Abs(distance - 8);
                if (score < bestScore)
                {
                    bestScore = score;
                    targetX = x + 0.5;
                    targetY = cameraY + 1.25;
                    targetZ = z + 0.5;
                    wallDistance = distance;
                }
                break;
            }
        }

        return wallDistance > 0;
    }

    /// <summary>
    /// Reads one block while reusing a caller-owned <see cref="BlockPos"/>.
    /// The helper mutates <paramref name="sample"/> and therefore must not be
    /// used concurrently or relied upon to preserve its previous coordinates.
    /// </summary>
    /// <param name="accessor">Block accessor that resolves the requested layer.</param>
    /// <param name="sample">Scratch position mutated to the requested coordinates.</param>
    /// <param name="x">World X coordinate in blocks.</param>
    /// <param name="y">World Y coordinate in blocks.</param>
    /// <param name="z">World Z coordinate in blocks.</param>
    /// <param name="layer">Vintage Story block layer; the most-solid layer is used by default.</param>
    /// <returns>The block reported by the accessor for the mutated position and layer.</returns>
    internal static Block GetBlock(
        IBlockAccessor accessor,
        BlockPos sample,
        int x,
        int y,
        int z,
        int layer = BlockLayersAccess.MostSolid)
    {
        sample.Set(x, y, z);
        return accessor.GetBlock(sample, layer);
    }

    /// <summary>
    /// Determines whether a block is a usable physical cave boundary. Rendered
    /// but non-collidable planes such as grass are deliberately not considered solid.
    /// </summary>
    /// <param name="block">Block to classify; null-like engine results are treated as open.</param>
    /// <returns><see langword="true"/> for non-air blocks with at least one collision box.</returns>
    internal static bool IsCaveSolid(Block block)
    {
        return block is not null
            && block.Id != 0
            && block.BlockMaterial != EnumBlockMaterial.Air
            && block.CollisionBoxes is { Length: > 0 };
    }

    /// <summary>
    /// Searches loaded terrain for a broad visible liquid surface and an open
    /// shoreline with at least eight additional blocks of reflected view, then
    /// locks an elevated gameplay-scale camera toward the far surface.
    /// </summary>
    /// <returns><see langword="true"/> when a shoreline and continuous visible liquid run were found.</returns>
    private bool TryApplyWaterReflectionCamera()
    {
        Vec3d origin = api.World.Player.Entity.CameraPos.Clone();
        IBlockAccessor accessor = api.World.BlockAccessor;
        BlockPos sample = api.World.Player.Entity.Pos.AsBlockPos.Copy();
        int originX = (int)Math.Floor(origin.X);
        int originZ = (int)Math.Floor(origin.Z);
        int waterColumns = 0;
        int waterPatches = 0;
        HashSet<(int X, int Z)> testedWaterAreas = [];

        for (int radius = 8; radius <= 384; radius += 2)
        {
            for (int direction = 0; direction < 192; direction++)
            {
                double angle = direction * Math.PI * 2.0 / 192.0;
                int waterX = originX + (int)Math.Round(Math.Cos(angle) * radius);
                int waterZ = originZ + (int)Math.Round(Math.Sin(angle) * radius);
                if (!TryGetWaterSurface(accessor, sample, waterX, waterZ, out int waterY, out Block water))
                {
                    continue;
                }

                waterColumns++;
                if (!HasWaterPatch(accessor, sample, waterX, waterY, waterZ))
                {
                    continue;
                }

                waterPatches++;
                if (!testedWaterAreas.Add((waterX >> 2, waterZ >> 2)))
                {
                    continue;
                }

                if (!TryFindWaterView(
                        accessor,
                        sample,
                        waterX,
                        waterY,
                        waterZ,
                        out int bankX,
                        out int bankY,
                        out int bankZ,
                        out double targetX,
                        out double targetY,
                        out double targetZ,
                        out double verifiedLookDistance))
                {
                    continue;
                }

                double destinationX = bankX + 0.5;
                // Elevate the test entity above shoreline vegetation so the
                // verified water run remains visible at gameplay scale.
                double destinationY = bankY + 3.05;
                double destinationZ = bankZ + 0.5;
                double dx = targetX - destinationX;
                // Aim below the far water coordinate so the exposed surface,
                // rather than the opposite vegetation, occupies the lower
                // half of the frame where SSR can be read at gameplay scale.
                double cameraAimY = targetY - 5.0;
                double dy = cameraAimY - (destinationY + 1.62);
                double dz = targetZ - destinationZ;
                SelectCameraOrientation(dx, dy, dz, useDirectCameraAngles: true);
                float wideYaw = exteriorYaw;
                float widePitch = exteriorPitch;
                float wideEntityPitch = exteriorEntityPitch;
                // The wide lake view is intentionally shallow. In its reflected camera the local
                // body lies about 69 degrees above/behind the frustum and must be clipped. Derive a
                // second, still physical pose through the nearest verified water cell: the virtual
                // camera then looks upward toward the body while the actual camera looks down at
                // the liquid, exactly as a player inspecting their shoreline reflection would.
                SelectCameraOrientation(
                    waterX + 0.5 - destinationX,
                    targetY - (destinationY + 1.62),
                    waterZ + 0.5 - destinationZ,
                    useDirectCameraAngles: true);
                waterLocalBodyYaw = exteriorYaw;
                waterLocalBodyPitch = exteriorPitch;
                waterLocalBodyEntityPitch = exteriorEntityPitch;
                exteriorYaw = wideYaw;
                exteriorPitch = widePitch;
                exteriorEntityPitch = wideEntityPitch;
                // Keep the automated view locked for the complete six-minute
                // harness window. This world has a persistent camera sway;
                // releasing after five seconds invalidates temporal and A/B/A
                // comparisons even though the player is stationary.
                exteriorCameraLockTicks = 36_000;
                exteriorPositionLocked = true;
                exteriorPositionX = destinationX;
                exteriorPositionY = destinationY;
                exteriorPositionZ = destinationZ;
                // The far target proves that the whole lake is suitable for
                // reflection, but a physically plausible centimetre-scale
                // ripple is sub-pixel at 22-24 m. Place the impact on the same
                // already-verified continuous run, about eight metres from the
                // bank, so the test assesses actual deformation rather than
                // compensating it with a non-physical amplitude multiplier.
                double horizontalLookLength = Math.Max(
                    Math.Sqrt(dx * dx + dz * dz),
                    0.001);
                double lookDirectionX = dx / horizontalLookLength;
                double lookDirectionZ = dz / horizontalLookLength;
                double impactDistance = Math.Clamp(
                    verifiedLookDistance * 0.36,
                    6.0,
                    9.0);
                if (!TryConfigureWaterImpactTargets(
                        accessor,
                        sample,
                        destinationX,
                        destinationZ,
                        lookDirectionX,
                        lookDirectionZ,
                        impactDistance))
                {
                    continue;
                }

                Vec3d centralImpactTarget = waterImpactLandingPositions[1];
                double impactX = centralImpactTarget.X;
                double impactSurfaceY = centralImpactTarget.Y;
                double impactZ = centralImpactTarget.Z;
                // The impact targets lie on the view axis by design. Reflection
                // witnesses must not: collinear silhouettes overlap after the
                // planar transform and cannot prove that both entities survived.
                // The 0.40 m lateral displacement is known to keep both mirror
                // silhouettes inside the clean water corridor. Add longitudinal
                // separation because a straw dummy's collision body is wider
                // than its thin visible shape; this keeps each ballistic column
                // almost one metre from its witness without hiding its reflection.
                const double witnessLateralOffset = 0.40;
                const double witnessLongitudinalOffset = 0.90;
                double perpendicularX = -lookDirectionZ;
                double perpendicularZ = lookDirectionX;
                if (!TryResolveWaterWitnessAnchor(
                        accessor,
                        sample,
                        waterImpactLandingPositions[0],
                        perpendicularX * witnessLateralOffset
                            - lookDirectionX * witnessLongitudinalOffset,
                        perpendicularZ * witnessLateralOffset
                            - lookDirectionZ * witnessLongitudinalOffset,
                        out Vec3d opaqueItemAnchor)
                    || !TryResolveWaterWitnessAnchor(
                        accessor,
                        sample,
                        waterImpactLandingPositions[2],
                        -perpendicularX * witnessLateralOffset
                            + lookDirectionX * witnessLongitudinalOffset,
                        -perpendicularZ * witnessLateralOffset
                            + lookDirectionZ * witnessLongitudinalOffset,
                        out Vec3d humanoidAnchor))
                {
                    continue;
                }
                double opaqueItemY = opaqueItemAnchor.Y + 1.20;
                double humanoidY = humanoidAnchor.Y + 0.04;
                waterImpactTargetSet = true;
                waterImpactTicks = 0;
                waterImpactCommandIndex = 0;
                waterProjectileTicks = 0;
                waterProjectileCommandIndex = 0;
                waterProjectileBaselinePhase = 0;
                waterProjectileBaselineQueued = false;
                api.SendChatMessage(
                    $"/tp ={destinationX.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"={destinationY.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"={destinationZ.ToString("0.00", CultureInfo.InvariantCulture)}",
                    null!);
                api.Logger.Notification(
                    "[VintageRTX.Test] Reflection witnesses requested: opaque-item=game:stone-granite at ({0:0.00},{1:0.00},{2:0.00}), surface-clearance=1.20m; alpha-shaped=game:strawdummy at ({3:0.00},{4:0.00},{5:0.00}), surface-clearance=0.04m. First-person held arm/torch remains an independent exclusion challenge.",
                    opaqueItemAnchor.X,
                    opaqueItemY,
                    opaqueItemAnchor.Z,
                    humanoidAnchor.X,
                    humanoidY,
                    humanoidAnchor.Z);
                api.SendChatMessage(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "/vintagertxtest witness {0:0.00} {1:0.00} {2:0.00} {3:0.00} {4:0.00} {5:0.00}",
                        opaqueItemAnchor.X,
                        opaqueItemY,
                        opaqueItemAnchor.Z,
                        humanoidAnchor.X,
                        humanoidY,
                        humanoidAnchor.Z),
                    null!);
                api.Logger.Notification(
                    "[VintageRTX.Test] Water reflection camera applied: water={0} at ({1},{2},{3}), bank=({4},{5},{6}), bank distance={7:0.0}, verified look distance={8:0.0}, target=({9:0.0},{10:0.0},{11:0.0}), impact=({12:0.0},{13:0.0},{14:0.0}), pitch={15:0.000}, patch=3x3.",
                    water.Code?.ToString() ?? "unknown",
                    waterX,
                    waterY,
                    waterZ,
                    bankX,
                    bankY,
                    bankZ,
                    Math.Sqrt(
                        (waterX - bankX) * (double)(waterX - bankX)
                        + (waterZ - bankZ) * (double)(waterZ - bankZ)),
                    verifiedLookDistance,
                    targetX,
                    targetY,
                    targetZ,
                    impactX,
                    impactSurfaceY,
                    impactZ,
                    exteriorPitch);
                return true;
            }
        }

        api.Logger.Notification(
            "[VintageRTX.Test] Water scan exhausted: radius=384, fluid columns={0}, 3x3 patches={1}, tested water areas={2}, no bank with an 8-block reflected view.",
            waterColumns,
            waterPatches,
            testedWaterAreas.Count);
        return false;
    }

    /// <summary>
    /// Solves the engine's periodic yaw/pitch representation by comparing
    /// candidate <c>EntityPos.GetViewVector()</c> results with a desired
    /// world-space direction. Paths using player camera angles normalize pitch
    /// to [-pi, pi]; legacy cave paths continue with render-matrix calibration.
    /// </summary>
    /// <param name="dx">World-space X component from camera eye to target.</param>
    /// <param name="dy">World-space Y component from camera eye to target.</param>
    /// <param name="dz">World-space Z component from camera eye to target.</param>
    /// <param name="useDirectCameraAngles">Whether to drive signed public camera angles instead of calibrating the render matrix.</param>
    private void SelectCameraOrientation(
        double dx,
        double dy,
        double dz,
        bool useDirectCameraAngles = false)
    {
        double desiredLength = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 0.001);
        Vec3d desired = new(dx / desiredLength, dy / desiredLength, dz / desiredLength);
        double horizontal = Math.Max(Math.Sqrt(dx * dx + dz * dz), 0.001);
        float horizontalAngle = (float)Math.Atan2(-dx, dz);
        float verticalAngle = (float)Math.Atan2(dy, horizontal);
        float[] yawCandidates =
        [
            horizontalAngle,
            -horizontalAngle,
            horizontalAngle + GameMath.PI,
            -horizontalAngle + GameMath.PI
        ];
        float[] pitchCandidates =
        [
            GameMath.PI - verticalAngle,
            GameMath.PI + verticalAngle,
            verticalAngle,
            -verticalAngle,
            GameMath.TWOPI - verticalAngle,
            GameMath.TWOPI + verticalAngle
        ];

        double bestAlignment = double.NegativeInfinity;
        Vec3d bestView = new();
        foreach (float yaw in yawCandidates)
        {
            foreach (float pitch in pitchCandidates)
            {
                var orientation = api.World.Player.Entity.Pos.Copy();
                orientation.SetAngles(0.0f, yaw, pitch);
                Vec3f view = orientation.GetViewVector();
                double viewLength = Math.Max(
                    Math.Sqrt(view.X * view.X + view.Y * view.Y + view.Z * view.Z),
                    0.001);
                double alignment = (view.X * desired.X + view.Y * desired.Y + view.Z * desired.Z)
                    / viewLength;
                if (alignment <= bestAlignment)
                {
                    continue;
                }

                bestAlignment = alignment;
                exteriorYaw = yaw;
                exteriorPitch = pitch;
                bestView.Set(view.X / viewLength, view.Y / viewLength, view.Z / viewLength);
            }
        }

        // EntityPos uses periodic angles, while IClientPlayer.CameraPitch uses
        // the signed camera angle exposed by the public client API.
        float solvedViewPitch = exteriorPitch;
        exteriorEntityPitch = solvedViewPitch;
        useDirectPublicCameraAngles = useDirectCameraAngles;
        if (useDirectCameraAngles)
        {
            exteriorPitch = NormalizeSignedAngle(solvedViewPitch);
            MapDirectCameraToEntityAngles(
                exteriorYaw,
                exteriorPitch,
                out _,
                out exteriorEntityPitch);
        }

        api.Logger.Notification(
            "[VintageRTX.Test] Camera orientation solved through EntityPos.GetViewVector: alignment={0:0.0000}, yaw={1:0.000}, pitch={2:0.000}, desired=({3:0.000},{4:0.000},{5:0.000}), view=({6:0.000},{7:0.000},{8:0.000}).",
            bestAlignment,
            exteriorYaw,
            exteriorPitch,
            desired.X,
            desired.Y,
            desired.Z,
            bestView.X,
            bestView.Y,
            bestView.Z);
        api.Logger.Notification(
            "[VintageRTX.Test] Camera pitch mapping: entity={0:0.000}, input={1:0.000}, vertical={2:0.000}.",
            exteriorEntityPitch,
            exteriorPitch,
            verticalAngle);
        if (useDirectCameraAngles)
        {
            cameraCalibrationPhase = CameraCalibrationPhase.Inactive;
            api.Logger.Notification(
                "[VintageRTX.Test] Direct public camera lock enabled: yaw={0:0.000}, signed pitch={1:0.000}.",
                exteriorYaw,
                exteriorPitch);
        }
        else
        {
            StartCameraOrientationCalibration(desired);
        }
    }

    /// <summary>
    /// Initializes the two-pass pitch calibration used when the public camera
    /// angle mapping does not match the entity's periodic angle convention.
    /// </summary>
    /// <param name="desired">Normalized world-space forward direction that the rendered matrix must approach.</param>
    private void StartCameraOrientationCalibration(Vec3d desired)
    {
        cameraCalibrationDesired.Set(desired.X, desired.Y, desired.Z);
        cameraCalibrationPhase = CameraCalibrationPhase.PitchCoarse;
        cameraCalibrationStep = 0;
        cameraCalibrationCandidatePending = false;
        cameraCalibrationHoldTicks = 0;
        ResetCameraCalibrationBest();
    }

    /// <summary>
    /// Advances one candidate in the coarse or fine pitch scan. A candidate is
    /// held for three render ticks before matrix evaluation so the engine has
    /// applied the pose being measured.
    /// </summary>
    private void UpdateCameraOrientationCalibration()
    {
        if (cameraCalibrationPhase == CameraCalibrationPhase.Inactive)
        {
            return;
        }

        if (cameraCalibrationCandidatePending)
        {
            cameraCalibrationHoldTicks++;
            if (cameraCalibrationHoldTicks < 3)
            {
                return;
            }

            EvaluateCameraCalibrationCandidate();
            cameraCalibrationCandidatePending = false;
        }

        int candidateCount = cameraCalibrationPhase switch
        {
            CameraCalibrationPhase.PitchCoarse => 64,
            CameraCalibrationPhase.PitchFine => 32,
            _ => 0
        };
        if (cameraCalibrationStep >= candidateCount)
        {
            AdvanceCameraCalibrationPhase();
            if (cameraCalibrationPhase == CameraCalibrationPhase.Inactive)
            {
                return;
            }

            candidateCount = cameraCalibrationPhase == CameraCalibrationPhase.PitchFine ? 32 : 64;
        }

        float progress = cameraCalibrationStep / (float)candidateCount;
        switch (cameraCalibrationPhase)
        {
            case CameraCalibrationPhase.PitchCoarse:
                exteriorPitch = progress * GameMath.TWOPI;
                exteriorEntityPitch = exteriorPitch;
                break;

            case CameraCalibrationPhase.PitchFine:
                float pitchOffset = (progress - 0.5f) * GameMath.PI / 3.0f;
                exteriorPitch = cameraCalibrationCoarsePitch + pitchOffset;
                exteriorEntityPitch = exteriorPitch;
                break;
        }

        cameraCalibrationStep++;
        cameraCalibrationCandidatePending = true;
        cameraCalibrationHoldTicks = 0;
    }

    /// <summary>
    /// Extracts and normalizes the rendered forward vector from the current
    /// camera matrix, then retains the pitch with the greatest dot-product
    /// alignment. Matrices shorter than 4x4 are ignored as not yet initialized.
    /// </summary>
    private void EvaluateCameraCalibrationCandidate()
    {
        double[] matrix = api.Render.CameraMatrixOrigin;
        if (matrix.Length < 16)
        {
            return;
        }

        // The cave probe retains the established render-matrix pitch mapping.
        // Water uses IClientPlayer.CameraYaw/CameraPitch directly and never
        // enters this compatibility calibration path.
        double forwardX = -matrix[8];
        double forwardY = -matrix[9];
        double forwardZ = -matrix[10];
        double length = Math.Max(
            Math.Sqrt(forwardX * forwardX + forwardY * forwardY + forwardZ * forwardZ),
            0.001);
        forwardX /= length;
        forwardY /= length;
        forwardZ /= length;
        double alignment = forwardX * cameraCalibrationDesired.X
            + forwardY * cameraCalibrationDesired.Y
            + forwardZ * cameraCalibrationDesired.Z;
        if (alignment <= cameraCalibrationBestAlignment)
        {
            return;
        }

        cameraCalibrationBestAlignment = alignment;
        cameraCalibrationBestYaw = exteriorYaw;
        cameraCalibrationBestPitch = exteriorPitch;
        cameraCalibrationBestForward.Set(forwardX, forwardY, forwardZ);
    }

    /// <summary>
    /// Promotes the best coarse pitch into the fine search or commits the best
    /// fine pitch and stops calibration. Per-phase candidate state is reset at
    /// the boundary so a stale matrix sample cannot win the next pass.
    /// </summary>
    private void AdvanceCameraCalibrationPhase()
    {
        switch (cameraCalibrationPhase)
        {
            case CameraCalibrationPhase.PitchCoarse:
                cameraCalibrationCoarsePitch = cameraCalibrationBestPitch;
                exteriorPitch = cameraCalibrationCoarsePitch;
                exteriorEntityPitch = exteriorPitch;
                cameraCalibrationPhase = CameraCalibrationPhase.PitchFine;
                break;

            case CameraCalibrationPhase.PitchFine:
                exteriorPitch = cameraCalibrationBestPitch;
                exteriorEntityPitch = exteriorPitch;
                cameraCalibrationPhase = CameraCalibrationPhase.Inactive;
                api.Logger.Notification(
                    "[VintageRTX.Test] Render-matrix camera calibration complete: alignment={0:0.0000}, yaw={1:0.000}, pitch={2:0.000}, forward=({3:0.000},{4:0.000},{5:0.000}).",
                    cameraCalibrationBestAlignment,
                    exteriorYaw,
                    exteriorPitch,
                    cameraCalibrationBestForward.X,
                    cameraCalibrationBestForward.Y,
                    cameraCalibrationBestForward.Z);
                return;
        }

        cameraCalibrationStep = 0;
        cameraCalibrationCandidatePending = false;
        cameraCalibrationHoldTicks = 0;
        ResetCameraCalibrationBest();
    }

    /// <summary>
    /// Clears the best-alignment accumulator while retaining the current pose as
    /// a safe fallback if the render API yields no valid matrix candidate.
    /// </summary>
    private void ResetCameraCalibrationBest()
    {
        cameraCalibrationBestAlignment = double.NegativeInfinity;
        cameraCalibrationBestYaw = exteriorYaw;
        cameraCalibrationBestPitch = exteriorPitch;
        cameraCalibrationBestForward.Set(0.0, 0.0, 0.0);
    }

    /// <summary>
    /// Wraps an engine angle into the signed interval [-pi, pi] required by the
    /// public camera-pitch API while preserving equivalent orientation.
    /// </summary>
    /// <param name="angle">Angle in radians; finite input is required.</param>
    /// <returns>The equivalent signed angle in radians.</returns>
    internal static float NormalizeSignedAngle(float angle)
    {
        angle %= GameMath.TWOPI;
        if (angle > GameMath.PI)
        {
            angle -= GameMath.TWOPI;
        }
        else if (angle < -GameMath.PI)
        {
            angle += GameMath.TWOPI;
        }

        return angle;
    }

    /// <summary>
    /// Maps signed public first-person camera angles to Vintage Story's equivalent entity-pose
    /// representation. The renderer anchors first-person arms around <c>Pitch - pi</c>; using the
    /// camera's zero-centred pitch directly rotates the body by roughly 180 degrees even though the
    /// world camera still faces the correct target.
    /// </summary>
    /// <param name="cameraYaw">Public camera yaw in radians.</param>
    /// <param name="cameraPitch">Signed public camera pitch in radians.</param>
    /// <param name="entityYaw">Equivalent body/entity yaw, shifted by pi.</param>
    /// <param name="entityPitch">Equivalent entity pitch centred on pi for a level view.</param>
    internal static void MapDirectCameraToEntityAngles(
        float cameraYaw,
        float cameraPitch,
        out float entityYaw,
        out float entityPitch)
    {
        entityYaw = NormalizeSignedAngle(cameraYaw + GameMath.PI);
        entityPitch = GameMath.PI - cameraPitch;
    }

    /// <summary>
    /// Locates a visible liquid surface within two blocks above through three
    /// blocks below the rain-map height. A fluid hidden by solid-layer ice,
    /// snow or another block is rejected because it cannot validate reflections.
    /// </summary>
    /// <param name="accessor">Block accessor for solid and fluid layers.</param>
    /// <param name="sample">Reusable mutable coordinate used during the vertical search.</param>
    /// <param name="x">World X coordinate of the sampled column.</param>
    /// <param name="z">World Z coordinate of the sampled column.</param>
    /// <param name="waterY">Receives the visible liquid Y coordinate when found.</param>
    /// <param name="water">Receives the liquid-layer block when found.</param>
    /// <returns><see langword="true"/> when an uncovered liquid surface exists in the bounded vertical window.</returns>
    internal static bool TryGetWaterSurface(
        IBlockAccessor accessor,
        BlockPos sample,
        int x,
        int z,
        out int waterY,
        out Block water)
    {
        sample.Set(x, 0, z);
        int rainHeight = accessor.GetRainMapHeightAt(sample);
        for (int offset = 2; offset >= -3; offset--)
        {
            int y = rainHeight + offset;
            Block candidate = GetBlock(accessor, sample, x, y, z, BlockLayersAccess.Fluid);
            if (candidate is not null
                && candidate.IsLiquid()
                // Fluid can coexist with ice, snow or another solid layer.
                // Such a column is physically wet but cannot validate visible
                // water reflections, so reject it at the probe boundary.
                && IsVisuallyOpen(GetBlock(accessor, sample, x, y, z))
                && IsVisuallyOpen(GetBlock(accessor, sample, x, y + 1, z)))
            {
                waterY = y;
                water = candidate;
                return true;
            }
        }

        waterY = 0;
        water = null!;
        return false;
    }

    /// <summary>
    /// Verifies that a liquid candidate belongs to a locally coherent 3x3
    /// surface rather than a single puddle cell. At least seven columns must be
    /// liquid and their heights may differ by at most one block.
    /// </summary>
    /// <param name="accessor">Block accessor for the loaded world.</param>
    /// <param name="sample">Reusable mutable coordinate for the nine columns.</param>
    /// <param name="centerX">Center X coordinate of the candidate patch.</param>
    /// <param name="centerY">Reference liquid-surface Y coordinate.</param>
    /// <param name="centerZ">Center Z coordinate of the candidate patch.</param>
    /// <returns><see langword="true"/> when at least seven compatible visible liquid columns are present.</returns>
    internal static bool HasWaterPatch(
        IBlockAccessor accessor,
        BlockPos sample,
        int centerX,
        int centerY,
        int centerZ)
    {
        int waterColumns = 0;
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (TryGetWaterSurface(
                        accessor,
                        sample,
                        centerX + offsetX,
                        centerZ + offsetZ,
                        out int waterY,
                        out _)
                    && Math.Abs(waterY - centerY) <= 1)
                {
                    waterColumns++;
                }
            }
        }

        return waterColumns >= 7;
    }

    /// <summary>
    /// Finds an open bank two to five blocks from a liquid patch and a far
    /// target connected by a continuous 24-block-or-shorter liquid run. This
    /// rejects technically valid shorelines that cannot show useful SSR content.
    /// </summary>
    /// <param name="accessor">Block accessor for terrain and liquid queries.</param>
    /// <param name="sample">Reusable mutable coordinate used throughout the radial search.</param>
    /// <param name="waterX">X coordinate of the near liquid reference column.</param>
    /// <param name="waterY">Y coordinate of the near liquid surface.</param>
    /// <param name="waterZ">Z coordinate of the near liquid reference column.</param>
    /// <param name="bankX">Receives the selected solid bank X coordinate.</param>
    /// <param name="bankY">Receives the selected solid bank surface Y coordinate.</param>
    /// <param name="bankZ">Receives the selected solid bank Z coordinate.</param>
    /// <param name="targetX">Receives the far liquid target center X coordinate.</param>
    /// <param name="targetY">Receives the far liquid target height with a small surface offset.</param>
    /// <param name="targetZ">Receives the far liquid target center Z coordinate.</param>
    /// <param name="verifiedLookDistance">Receives the verified bank-to-target horizontal distance in blocks.</param>
    /// <returns><see langword="true"/> when both a usable bank and continuous far liquid target are found.</returns>
    internal static bool TryFindWaterView(
        IBlockAccessor accessor,
        BlockPos sample,
        int waterX,
        int waterY,
        int waterZ,
        out int bankX,
        out int bankY,
        out int bankZ,
        out double targetX,
        out double targetY,
        out double targetZ,
        out double verifiedLookDistance)
    {
        bankX = 0;
        bankY = 0;
        bankZ = 0;
        targetX = 0.0;
        targetY = 0.0;
        targetZ = 0.0;
        verifiedLookDistance = 0.0;

        // Evaluate the bank and the visible water run together. A technically
        // valid shore only three blocks from the target produces a vertical
        // close-up with no screen-space scene available to reflect.
        for (int radius = 2; radius <= 5; radius++)
        {
            for (int direction = 0; direction < 96; direction++)
            {
                double angle = direction * Math.PI * 2.0 / 96.0;
                int x = waterX + (int)Math.Round(Math.Cos(angle) * radius);
                int z = waterZ + (int)Math.Round(Math.Sin(angle) * radius);
                sample.Set(x, 0, z);
                int surfaceY = accessor.GetRainMapHeightAt(sample);
                Block surface = GetBlock(accessor, sample, x, surfaceY, z);
                if (surface is null
                    || surface.Id == 0
                    || surface.IsLiquid()
                    || Math.Abs(surfaceY - waterY) > 2
                    || !IsVisuallyOpen(GetBlock(accessor, sample, x, surfaceY + 1, z))
                    || !IsVisuallyOpen(GetBlock(accessor, sample, x, surfaceY + 2, z)))
                {
                    continue;
                }

                double directionX = waterX - x;
                double directionZ = waterZ - z;
                double directionLength = Math.Sqrt(
                    directionX * directionX + directionZ * directionZ);
                // Radius starts at two blocks, so the rounded bank offset cannot be zero.
                // Keeping an unreachable near-zero guard here only hides the actual search paths
                // from coverage and would silently mask a future radius-contract regression.
                directionX /= directionLength;
                directionZ /= directionLength;
                int minimumLookDistance = (int)Math.Ceiling(directionLength) + 8;
                for (int lookDistance = 24; lookDistance >= minimumLookDistance; lookDistance--)
                {
                    int lookX = x + (int)Math.Round(directionX * lookDistance);
                    int lookZ = z + (int)Math.Round(directionZ * lookDistance);
                    if (!TryGetWaterSurface(
                            accessor,
                            sample,
                            lookX,
                            lookZ,
                            out int lookWaterY,
                            out Block lookWater)
                        || Math.Abs(lookWaterY - waterY) > 1
                        || !HasWaterPatch(accessor, sample, lookX, lookWaterY, lookZ)
                        || !HasContinuousWaterRun(
                            accessor,
                            sample,
                            x,
                            z,
                            directionX,
                            directionZ,
                            directionLength,
                            lookDistance,
                            waterY))
                    {
                        continue;
                    }

                    bankX = x;
                    bankY = surfaceY;
                    bankZ = z;
                    targetX = lookX + 0.5;
                    targetY = lookWaterY
                        + Math.Clamp(lookWater.LiquidLevel, 0, 7) / 8.0;
                    targetZ = lookZ + 0.5;
                    verifiedLookDistance = lookDistance;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Samples every integer block along a normalized horizontal ray to ensure
    /// the proposed reflection view is uninterrupted liquid at a consistent level.
    /// </summary>
    /// <param name="accessor">Block accessor for liquid-surface queries.</param>
    /// <param name="sample">Reusable mutable coordinate used for each ray step.</param>
    /// <param name="bankX">Starting bank X coordinate.</param>
    /// <param name="bankZ">Starting bank Z coordinate.</param>
    /// <param name="directionX">Normalized horizontal X direction toward the water target.</param>
    /// <param name="directionZ">Normalized horizontal Z direction toward the water target.</param>
    /// <param name="firstWaterDistance">Measured horizontal distance in blocks to the first known liquid column.</param>
    /// <param name="lookDistance">Inclusive ray length in blocks.</param>
    /// <param name="referenceWaterY">Expected liquid-surface height; one-block variation is accepted.</param>
    /// <returns><see langword="true"/> when every sampled column exposes compatible liquid.</returns>
    internal static bool HasContinuousWaterRun(
        IBlockAccessor accessor,
        BlockPos sample,
        int bankX,
        int bankZ,
        double directionX,
        double directionZ,
        double firstWaterDistance,
        int lookDistance,
        int referenceWaterY)
    {
        int firstStep = Math.Max(2, (int)Math.Ceiling(firstWaterDistance));
        for (int step = firstStep; step <= lookDistance; step++)
        {
            int x = bankX + (int)Math.Round(directionX * step);
            int z = bankZ + (int)Math.Round(directionZ * step);
            if (!TryGetWaterSurface(accessor, sample, x, z, out int waterY, out _)
                || Math.Abs(waterY - referenceWaterY) > 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Moves one reflection witness away from the ballistic impact line and resolves the actual
    /// liquid height at the displaced column. This keeps the witnesses physically supported by the
    /// same lake while guaranteeing separable projected silhouettes and collision-free impacts.
    /// </summary>
    /// <param name="accessor">Loaded world used to verify the displaced liquid column.</param>
    /// <param name="sample">Reusable mutable coordinate.</param>
    /// <param name="source">Verified on-axis impact point used as the longitudinal anchor.</param>
    /// <param name="offsetX">Horizontal world-X displacement.</param>
    /// <param name="offsetZ">Horizontal world-Z displacement.</param>
    /// <param name="anchor">Receives the exact displaced liquid-surface point.</param>
    /// <returns><see langword="true"/> when the displaced column remains exposed liquid.</returns>
    private static bool TryResolveWaterWitnessAnchor(
        IBlockAccessor accessor,
        BlockPos sample,
        Vec3d source,
        double offsetX,
        double offsetZ,
        out Vec3d anchor)
    {
        double worldX = source.X + offsetX;
        double worldZ = source.Z + offsetZ;
        if (!TryGetWaterSurface(
                accessor,
                sample,
                (int)Math.Floor(worldX),
                (int)Math.Floor(worldZ),
                out int waterY,
                out Block water))
        {
            anchor = new Vec3d();
            return false;
        }

        double surfaceY = waterY + Math.Clamp(water.LiquidLevel, 0, 7) / 8.0;
        anchor = new Vec3d(worldX, surfaceY, worldZ);
        return true;
    }

    /// <summary>
    /// Performs a conservative height-map obstruction test over the first half
    /// of the segment from camera eye to roof. Terrain more than two blocks
    /// above the interpolated sight line makes the comparison unusable.
    /// </summary>
    /// <param name="accessor">Block accessor providing rain-map heights.</param>
    /// <param name="sample">Reusable mutable coordinate for the five samples.</param>
    /// <param name="cameraX">Camera X coordinate in blocks.</param>
    /// <param name="cameraSurfaceY">Ground surface below the camera in blocks.</param>
    /// <param name="cameraZ">Camera Z coordinate in blocks.</param>
    /// <param name="roofX">Reference roof X coordinate in blocks.</param>
    /// <param name="roofY">Reference roof height in blocks.</param>
    /// <param name="roofZ">Reference roof Z coordinate in blocks.</param>
    /// <returns><see langword="true"/> when no sampled terrain height obstructs the roof view.</returns>
    internal static bool HasRoofLineOfSight(
        IBlockAccessor accessor,
        BlockPos sample,
        int cameraX,
        int cameraSurfaceY,
        int cameraZ,
        int roofX,
        int roofY,
        int roofZ)
    {
        double eyeY = cameraSurfaceY + 2.62;
        for (int step = 1; step <= 5; step++)
        {
            double progress = step / 10.0;
            int x = (int)Math.Round(cameraX + (roofX - cameraX) * progress);
            int z = (int)Math.Round(cameraZ + (roofZ - cameraZ) * progress);
            sample.Set(x, 0, z);
            int obstructionY = accessor.GetRainMapHeightAt(sample);
            double sightY = eyeY + (roofY - eyeY) * progress;
            if (obstructionY > sightY + 2.0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Requires a level, camera-passable 3x3 receiver around an exterior
    /// candidate. This rejects narrow ledges and alleys where a long roof shadow
    /// cannot expose its complete projected silhouette.
    /// </summary>
    /// <param name="accessor">Block accessor for surface heights and collision geometry.</param>
    /// <param name="sample">Reusable mutable coordinate for the nine columns.</param>
    /// <param name="centerX">Center X coordinate of the receiver.</param>
    /// <param name="centerSurfaceY">Reference ground Y coordinate; one-block variation is accepted.</param>
    /// <param name="centerZ">Center Z coordinate of the receiver.</param>
    /// <returns><see langword="true"/> when all nine columns are level enough and provide two passable blocks.</returns>
    internal static bool HasOpenGroundPatch(
        IBlockAccessor accessor,
        BlockPos sample,
        int centerX,
        int centerSurfaceY,
        int centerZ)
    {
        // Reject narrow alleys and ledges: the exterior validation needs enough
        // receiving ground to reveal a long roof shadow and its full silhouette.
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int x = centerX + offsetX;
                int z = centerZ + offsetZ;
                sample.Set(x, 0, z);
                int surfaceY = accessor.GetRainMapHeightAt(sample);
                if (Math.Abs(surfaceY - centerSurfaceY) > 1
                    || !IsOpenForCamera(GetBlock(accessor, sample, x, surfaceY + 1, z))
                    || !IsOpenForCamera(GetBlock(accessor, sample, x, surfaceY + 2, z)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Projects a roof point onto a horizontal receiver using the normalized
    /// real-world solar direction. The horizontal displacement follows similar
    /// triangles: vertical drop multiplied by horizontal solar magnitude and
    /// divided by its positive vertical component.
    /// </summary>
    /// <param name="casterY">World height of the top of the shadow caster.</param>
    /// <param name="receiverY">World height of the receiving surface.</param>
    /// <param name="sunDirection">Normalized direction from receiver toward the sun.</param>
    /// <returns>Non-negative horizontal shadow distance in blocks.</returns>
    internal static double CalculateProjectedSunShadowDistance(
        double casterY,
        double receiverY,
        Vec3f sunDirection)
    {
        double verticalDrop = casterY - receiverY;
        double horizontalMagnitude = Math.Sqrt(
            sunDirection.X * (double)sunDirection.X
            + sunDirection.Z * (double)sunDirection.Z);
        if (!double.IsFinite(verticalDrop)
            || verticalDrop <= 0.0
            || !double.IsFinite(horizontalMagnitude)
            || horizontalMagnitude <= 0.000001
            || !float.IsFinite(sunDirection.Y)
            || sunDirection.Y <= 0.000001f)
        {
            return 0.0;
        }

        return verticalDrop * horizontalMagnitude / sunDirection.Y;
    }

    /// <summary>
    /// Searches the down-sun height profile for exposed ground whose horizontal
    /// position agrees with the similar-triangle shadow projection. Roof and
    /// ridge cells are explicitly skipped until the ray has left the caster.
    /// </summary>
    /// <param name="accessor">Loaded-world height-map accessor.</param>
    /// <param name="sample">Reusable mutable coordinate.</param>
    /// <param name="casterOrigin">Horizontal center of the selected roof anchor.</param>
    /// <param name="casterTopY">World height of the casting roof point.</param>
    /// <param name="shadowX">Normalized down-sun X direction.</param>
    /// <param name="shadowZ">Normalized down-sun Z direction.</param>
    /// <param name="sunDirection">Normalized direction from receiver toward the sun.</param>
    /// <param name="maximumDistance">Farthest ground distance still visible before the camera.</param>
    /// <param name="targetX">Receives the selected world X coordinate.</param>
    /// <param name="targetY">Receives a point eight centimetres above the ground top.</param>
    /// <param name="targetZ">Receives the selected world Z coordinate.</param>
    /// <param name="shadowDistance">Receives the horizontal distance from roof anchor to target.</param>
    /// <returns><see langword="true"/> when a below-roof receiver was found.</returns>
    internal static bool TryFindProjectedSunShadowTarget(
        IBlockAccessor accessor,
        BlockPos sample,
        Vec3d casterOrigin,
        double casterTopY,
        double shadowX,
        double shadowZ,
        Vec3f sunDirection,
        double maximumDistance,
        out double targetX,
        out double targetY,
        out double targetZ,
        out double shadowDistance)
    {
        double bestScore = double.MaxValue;
        double bestGroundTopY = 0.0;
        shadowDistance = 0.0;
        for (double distance = 1.5; distance <= maximumDistance; distance += 0.5)
        {
            double x = casterOrigin.X + shadowX * distance;
            double z = casterOrigin.Z + shadowZ * distance;
            int blockX = (int)Math.Floor(x);
            int blockZ = (int)Math.Floor(z);
            sample.Set(blockX, 0, blockZ);
            double groundTopY = accessor.GetRainMapHeightAt(sample) + 1.0;
            if (groundTopY >= casterTopY - 1.0)
            {
                continue;
            }

            double expectedDistance = CalculateProjectedSunShadowDistance(
                casterTopY,
                groundTopY,
                sunDirection);
            if (expectedDistance <= 0.0)
            {
                continue;
            }

            double score = Math.Abs(distance - expectedDistance);
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            shadowDistance = distance;
            bestGroundTopY = groundTopY;
        }

        if (bestScore == double.MaxValue)
        {
            targetX = 0.0;
            targetY = 0.0;
            targetZ = 0.0;
            return false;
        }

        targetX = casterOrigin.X + shadowX * shadowDistance;
        targetY = bestGroundTopY + 0.08;
        targetZ = casterOrigin.Z + shadowZ * shadowDistance;
        return true;
    }

    /// <summary>
    /// Locates a genuine elevated roof around the deterministic world spawn.
    /// A candidate must have a coherent 3x3 solid top and open volume directly
    /// below it, which rejects ordinary hills as well as leaf canopies.
    /// </summary>
    /// <param name="accessor">Loaded-world block accessor.</param>
    /// <param name="sample">Reusable mutable block coordinate.</param>
    /// <param name="centerX">Deterministic search center X.</param>
    /// <param name="centerZ">Deterministic search center Z.</param>
    /// <param name="roofX">Receives the selected roof X coordinate.</param>
    /// <param name="roofY">Receives the selected top-surface Y coordinate.</param>
    /// <param name="roofZ">Receives the selected roof Z coordinate.</param>
    /// <param name="roofBlock">Receives the solid roof material.</param>
    /// <returns><see langword="true"/> when a physical roof footprint is found.</returns>
    internal static bool TryFindExteriorRoofAnchor(
        IBlockAccessor accessor,
        BlockPos sample,
        int centerX,
        int centerZ,
        out int roofX,
        out int roofY,
        out int roofZ,
        out Block roofBlock)
    {
        for (int radius = 0; radius <= 384; radius += 2)
        {
            int directionCount = radius == 0 ? 1 : Math.Max(32, radius * 2);
            for (int direction = 0; direction < directionCount; direction++)
            {
                double angle = direction * Math.PI * 2.0 / directionCount;
                int x = centerX + (int)Math.Round(Math.Cos(angle) * radius);
                int z = centerZ + (int)Math.Round(Math.Sin(angle) * radius);
                if (!TryResolveExteriorRoofSurface(
                        accessor,
                        sample,
                        x,
                        z,
                        out int y,
                        out Block candidate)
                    || (!IsOpenForCamera(GetBlock(accessor, sample, x, y - 1, z))
                        && !IsOpenForCamera(GetBlock(accessor, sample, x, y - 2, z))))
                {
                    continue;
                }

                int coherentTopCells = 0;
                for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int adjacentX = x + offsetX;
                        int adjacentZ = z + offsetZ;
                        coherentTopCells += TryResolveExteriorRoofSurface(
                                accessor,
                                sample,
                                adjacentX,
                                adjacentZ,
                                out int adjacentY,
                                out _)
                            && Math.Abs(adjacentY - y) <= 1
                                ? 1
                                : 0;
                    }
                }

                if (coherentTopCells < 6)
                {
                    continue;
                }

                roofX = x;
                roofY = y;
                roofZ = z;
                roofBlock = candidate;
                return true;
            }
        }

        roofX = 0;
        roofY = 0;
        roofZ = 0;
        roofBlock = null!;
        return false;
    }

    /// <summary>
    /// Resolves the solid construction surface beneath an optional thin snow
    /// cover. Ordinary snow-covered ground is rejected later because it has no
    /// open underside; an actual snow-covered roof retains that cavity.
    /// </summary>
    /// <param name="accessor">Loaded-world block accessor.</param>
    /// <param name="sample">Reusable mutable block coordinate.</param>
    /// <param name="x">World X coordinate.</param>
    /// <param name="z">World Z coordinate.</param>
    /// <param name="surfaceY">Receives the construction surface Y.</param>
    /// <param name="surfaceBlock">Receives the construction block.</param>
    /// <returns><see langword="true"/> when an eligible surface exists within two cells below the rain map.</returns>
    internal static bool TryResolveExteriorRoofSurface(
        IBlockAccessor accessor,
        BlockPos sample,
        int x,
        int z,
        out int surfaceY,
        out Block surfaceBlock)
    {
        sample.Set(x, 0, z);
        int rainY = accessor.GetRainMapHeightAt(sample);
        for (int depth = 0; depth <= 2; depth++)
        {
            int y = rainY - depth;
            Block candidate = GetBlock(accessor, sample, x, y, z);
            if (IsExteriorRoofMaterial(candidate))
            {
                surfaceY = y;
                surfaceBlock = candidate;
                return true;
            }

            if (candidate is not null
                && candidate.Id != 0
                && candidate.BlockMaterial != EnumBlockMaterial.Air
                && !(depth == 0
                    && (candidate.Code?.Path?.Contains(
                        "snow",
                        StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                break;
            }
        }

        surfaceY = 0;
        surfaceBlock = null!;
        return false;
    }

    /// <summary>Rejects liquids, foliage and transparent blocks as roof anchors.</summary>
    /// <param name="block">Candidate rain-map surface block.</param>
    /// <returns>Whether the block is a solid opaque construction candidate.</returns>
    internal static bool IsExteriorRoofMaterial(Block? block)
    {
        if (block is null
            || !IsCaveSolid(block)
            || block.BlockMaterial is EnumBlockMaterial.Water
                or EnumBlockMaterial.Glass
                or EnumBlockMaterial.Ice)
        {
            return false;
        }

        string path = block.Code?.Path ?? string.Empty;
        return !path.Contains("leaves", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("foliage", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("branch", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("snow", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("roof", StringComparison.OrdinalIgnoreCase)
                || path.Contains("shingle", StringComparison.OrdinalIgnoreCase)
                || path.Contains("thatch", StringComparison.OrdinalIgnoreCase)
                || path.Contains("tile", StringComparison.OrdinalIgnoreCase)
                || path.Contains("ridge", StringComparison.OrdinalIgnoreCase)
                || block.BlockMaterial is EnumBlockMaterial.Wood
                    or EnumBlockMaterial.Brick
                    or EnumBlockMaterial.Metal);
    }

    /// <summary>
    /// Requires the exterior camera receiver to sit on a coherent patch of
    /// opaque terrain rather than a leaf canopy. Decorative plants above the
    /// support remain valid real-scene geometry; the caller elevates the camera
    /// above them instead of deleting or ignoring their shadows.
    /// </summary>
    /// <param name="accessor">Block accessor for the solid terrain layer.</param>
    /// <param name="sample">Reusable mutable coordinate for the nine columns.</param>
    /// <param name="centerX">Center X coordinate of the receiver.</param>
    /// <param name="centerSurfaceY">Reference ground Y coordinate.</param>
    /// <param name="centerZ">Center Z coordinate of the receiver.</param>
    /// <returns><see langword="true"/> when all supports are representative opaque terrain.</returns>
    internal static bool HasRepresentativeExteriorReceiverPatch(
        IBlockAccessor accessor,
        BlockPos sample,
        int centerX,
        int centerSurfaceY,
        int centerZ)
    {
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int x = centerX + offsetX;
                int z = centerZ + offsetZ;
                sample.Set(x, 0, z);
                int surfaceY = accessor.GetRainMapHeightAt(sample);
                Block support = GetBlock(accessor, sample, x, surfaceY, z);
                bool representativeSupport = support?.BlockMaterial is
                    EnumBlockMaterial.Soil
                    or EnumBlockMaterial.Gravel
                    or EnumBlockMaterial.Sand
                    or EnumBlockMaterial.Stone
                    or EnumBlockMaterial.Brick
                    or EnumBlockMaterial.Wood
                    or EnumBlockMaterial.Metal;
                if (Math.Abs(surfaceY - centerSurfaceY) > 1
                    || !representativeSupport
                    || !IsCaveSolid(support))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether the camera may occupy a block cell. Non-collidable
    /// rendered planes are passable even when they are visually non-empty.
    /// </summary>
    /// <param name="block">Block to classify.</param>
    /// <returns><see langword="true"/> for air, empty identifiers or blocks without collision boxes.</returns>
    internal static bool IsOpenForCamera(Block block)
    {
        return block is null
            || block.Id == 0
            || block.BlockMaterial == EnumBlockMaterial.Air
            || block.CollisionBoxes is not { Length: > 0 };
    }

    /// <summary>
    /// Determines whether a block leaves the underlying liquid visually exposed.
    /// Unlike <see cref="IsOpenForCamera"/>, decorative non-air planes count as occluders.
    /// </summary>
    /// <param name="block">Solid-layer block above or coincident with a liquid surface.</param>
    /// <returns><see langword="true"/> only for air or empty block identifiers.</returns>
    internal static bool IsVisuallyOpen(Block block)
    {
        return block is null
            || block.Id == 0
            || block.BlockMaterial == EnumBlockMaterial.Air;
    }

    /// <summary>
    /// Issues public time and weather commands for the requested baseline. The
    /// local season is selected as well as the clock: night geometry uses winter,
    /// while daylight scenarios use summer so polar latitude cannot contradict
    /// the intended solar condition.
    /// </summary>
    private void ApplyDeterministicEnvironment()
    {
        bool requireNight = scenario is "lantern-night" or "nonstandard-geometry";
        bool requireDay = !requireNight
            && requestedWorldHour is >= 6.0f and <= 18.0f;
        if ((requireNight || requireDay) && api.World.Player?.Entity is not null)
        {
            EnumHemisphere hemisphere = api.World.Calendar.GetHemisphere(
                api.World.Player.Entity.Pos.AsBlockPos);
            // Select the season before fixing the clock on the disposable
            // test-world copy. This prevents both midnight polar daylight and
            // a nominal daytime capture whose sun remains below the horizon.
            string month = (hemisphere, requireNight) switch
            {
                (EnumHemisphere.South, true) => "jul",
                (EnumHemisphere.South, false) => "jan",
                (_, true) => "jan",
                _ => "jul"
            };
            api.SendChatMessage($"/time setmonth {month}", null!);
        }

        if (requestedWorldHour.HasValue)
        {
            string hour = requestedWorldHour.Value.ToString("0.###", CultureInfo.InvariantCulture);
            api.SendChatMessage($"/time set {hour}", null!);
            api.SendChatMessage("/time stop", null!);
        }

        if (clearWeather)
        {
            // setprecip removes rain clouds, while setir switches the local
            // weather pattern immediately so stale rain particles do not
            // survive into the short deterministic capture sequence.
            api.SendChatMessage("/weather setir clearsky", null!);
            api.SendChatMessage("/weather setprecip -1", null!);
        }
        else if (requestedPrecipitation.HasValue)
        {
            string precipitation = requestedPrecipitation.Value.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
            api.SendChatMessage($"/weather setprecip {precipitation}", null!);
        }
    }

    /// <summary>
    /// Samples the authoritative calendar, climate and daylight signals and
    /// retries commands that have not converged. A fresh-world daylight
    /// interpolation is allowed to settle without repeatedly resetting its month.
    /// </summary>
    private void VerifyDeterministicEnvironment()
    {
        environmentVerificationAttempts++;

        // The server time packet updates the shared calendar immediately, but
        // Vintage Story 1.22.7 keeps the client-only celestial colours and
        // normalized sun vector in ClientGameCalendar.Update(). A stopped clock
        // can leave that cache on the previous night indefinitely, producing a
        // starry framebuffer while GetSunPosition correctly reports daylight.
        bool clientCalendarRefreshed = TryRefreshClientCalendar(api.World.Calendar);
        float actualHour = api.World.Calendar.HourOfDay;
        float speedOfTime = api.World.Calendar.SpeedOfTime;
        float calendarSpeedMultiplier = api.World.Calendar.CalendarSpeedMul;
        float calendarProgressionRate = speedOfTime * calendarSpeedMultiplier;
        BlockPos playerPosition = api.World.Player.Entity.Pos.AsBlockPos;
        ClimateCondition climate = api.World.BlockAccessor.GetClimateAt(
            playerPosition,
            EnumGetClimateMode.NowValues);
        float precipitation = climate.Rainfall;
        float rainCloudOverlay = climate.RainCloudOverlay;
        float daylightStrength = api.World.Calendar.GetDayLightStrength(
            playerPosition.X,
            playerPosition.Z);
        float clientDaylightStrength = api.World.Calendar.DayLightStrength;
        float directSunLightStrength = api.World.Calendar.SunLightStrength;
        float moonLightStrength = api.World.Calendar.MoonLightStrength;
        float cachedSunVertical = (api.World.Calendar as IClientGameCalendar)?
            .SunPositionNormalized.Y ?? float.NaN;
        Vec3f sunPosition = VoxelScene.ResolveSunDirection(
            api.World.Calendar,
            api.World.Player.Entity.CameraPos);
        float sunVertical = sunPosition.Y;
        bool requireNight = scenario is "lantern-night" or "nonstandard-geometry";
        bool requireDay = !requireNight
            && requestedWorldHour is >= 6.0f and <= 18.0f;

        bool cachedSolarConditionMatches = (!requireNight
                || (!float.IsFinite(cachedSunVertical) || cachedSunVertical <= 0.0f))
            && (!requireDay
                || (!float.IsFinite(cachedSunVertical) || cachedSunVertical >= 0.05f))
            && HasConvergedClientSolarLighting(
                requireNight,
                requireDay,
                daylightStrength,
                clientDaylightStrength,
                directSunLightStrength,
                moonLightStrength);
        environmentVerified = cachedSolarConditionMatches && IsEnvironmentVerified(
            requestedWorldHour,
            clearWeather,
            actualHour,
            calendarProgressionRate,
            precipitation,
            rainCloudOverlay,
            requireNight,
            sunVertical,
            requestedPrecipitation,
            requireDay);

        if (environmentVerified)
        {
            api.Logger.Notification(
                "[VintageRTX.Test] Deterministic environment verified: PASS | requested hour={0}, actual hour={1:0.000}, daylight={2:0.000}, client-daylight={3:0.000}, direct-sun={4:0.000}, moon={5:0.000}, sun-y={6:0.000}, cached-sun-y={7:0.000}, client refresh={8}, physics-speed={9:0.000}, calendar-mul={10:0.000}, calendar-rate={11:0.000}, precipitation={12:0.000}, rain-cloud overlay={13:0.000}, attempts={14}.",
                requestedWorldHour?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unchanged",
                actualHour,
                daylightStrength,
                clientDaylightStrength,
                directSunLightStrength,
                moonLightStrength,
                sunVertical,
                cachedSunVertical,
                clientCalendarRefreshed,
                speedOfTime,
                calendarSpeedMultiplier,
                calendarProgressionRate,
                precipitation,
                rainCloudOverlay,
                environmentVerificationAttempts);
            return;
        }

        api.Logger.Warning(
            "[VintageRTX.Test] Deterministic environment verification pending ({0}/20) | requested hour={1}, actual hour={2:0.000}, daylight={3:0.000}, client-daylight={4:0.000}, direct-sun={5:0.000}, moon={6:0.000}, sun-y={7:0.000}, cached-sun-y={8:0.000}, client refresh={9}, physics-speed={10:0.000}, calendar-mul={11:0.000}, calendar-rate={12:0.000}, precipitation={13:0.000}, rain-cloud overlay={14:0.000}.",
            environmentVerificationAttempts,
            requestedWorldHour?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unchanged",
            actualHour,
            daylightStrength,
            clientDaylightStrength,
            directSunLightStrength,
            moonLightStrength,
            sunVertical,
            cachedSunVertical,
            clientCalendarRefreshed,
            speedOfTime,
            calendarSpeedMultiplier,
            calendarProgressionRate,
            precipitation,
            rainCloudOverlay);

        bool hourMatches = !requestedWorldHour.HasValue
            || CircularHourDistance(requestedWorldHour.Value, actualHour) <= 0.1f;
        // The official API exposes the base/summed SpeedOfTime separately from
        // CalendarSpeedMul. Vintage Story 1.22.7 may stop through either factor,
        // so their product is the authoritative effective calendar rate.
        bool clockStopped = Math.Abs(calendarProgressionRate) <= 0.001f;
        bool weatherAlreadyLocked = !clearWeather
            || (float.IsFinite(precipitation)
                && float.IsFinite(rainCloudOverlay)
                && precipitation <= 0.01f
                && rainCloudOverlay <= 0.01f);
        if (environmentVerificationAttempts < 20)
        {
            // ClientGameCalendar.Update above refreshes the cached sky without
            // advancing world time. Never resume here: on 1.22.7 that command
            // can arrive after the original stop and leave later captures on a
            // running clock. Re-issue only the unmet target and terminal stop.
            if (requestedWorldHour.HasValue)
            {
                string requestedHourText = requestedWorldHour.Value.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture);
                if (!hourMatches)
                {
                    api.SendChatMessage($"/time set {requestedHourText}", null!);
                }
                if (!clockStopped)
                {
                    api.SendChatMessage("/time stop", null!);
                }
            }

            if (!weatherAlreadyLocked)
            {
                if (clearWeather)
                {
                    api.SendChatMessage("/weather setir clearsky", null!);
                    api.SendChatMessage("/weather setprecip -1", null!);
                }
                else if (requestedPrecipitation.HasValue)
                {
                    string targetPrecipitation = requestedPrecipitation.Value.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture);
                    api.SendChatMessage(
                        $"/weather setprecip {targetPrecipitation}",
                        null!);
                }
            }
        }
        else
        {
            api.Logger.Error(
                "[VintageRTX.Test] Deterministic environment verified: FAIL after 20 attempts.");
        }
    }

    /// <summary>
    /// Invokes Vintage Story's public concrete client-calendar refresh without
    /// taking a compile-time dependency on the implementation assembly. The
    /// API interface intentionally omits this render-cache operation.
    /// </summary>
    /// <param name="calendar">Runtime calendar instance, or a test double.</param>
    /// <returns><see langword="true"/> when a parameterless update completed successfully.</returns>
    internal static bool TryRefreshClientCalendar(object? calendar)
    {
        if (calendar is null)
        {
            return false;
        }

        System.Reflection.MethodInfo? update = calendar.GetType().GetMethod(
            "Update",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (update is null || update.ReturnType != typeof(void))
        {
            return false;
        }

        try
        {
            update.Invoke(calendar, null);
            return true;
        }
        catch (System.Reflection.TargetInvocationException)
        {
            return false;
        }
        catch (System.Reflection.TargetException)
        {
            return false;
        }
    }

    /// <summary>
    /// Evaluates deterministic environment tolerances independently from the
    /// game API. Hours use circular distance; a locked clock tolerates 0.1 hour,
    /// precipitation tolerates 0.12, required night places the normalized sun at or below the
    /// horizon, and required daylight keeps it above the camera-placement threshold.
    /// </summary>
    /// <param name="requestedHour">Optional target hour on the circular 24-hour clock.</param>
    /// <param name="clearWeather">Whether both rainfall and cloud overlay must be nearly zero.</param>
    /// <param name="actualHour">Observed world hour on the circular 24-hour clock.</param>
    /// <param name="calendarProgressionRate">Observed effective SpeedOfTime times CalendarSpeedMul; absolute values at most 0.001 count as stopped.</param>
    /// <param name="precipitation">Observed normalized rainfall signal.</param>
    /// <param name="rainCloudOverlay">Observed normalized rain-cloud overlay.</param>
    /// <param name="requireNight">Whether the daylight signal must also satisfy the night threshold.</param>
    /// <param name="sunVertical">Vertical component of the normalized sun direction; non-positive values place the solar disc below the horizon.</param>
    /// <param name="requestedPrecipitation">Optional normalized target precipitation, superseding the clear-weather predicate.</param>
    /// <param name="requireDay">Whether the normalized sun must be at least 0.05 above the horizon.</param>
    /// <returns><see langword="true"/> when every requested finite signal is within its tolerance.</returns>
    internal static bool IsEnvironmentVerified(
        float? requestedHour,
        bool clearWeather,
        float actualHour,
        float calendarProgressionRate,
        float precipitation,
        float rainCloudOverlay,
        bool requireNight = false,
        float sunVertical = -1.0f,
        float? requestedPrecipitation = null,
        bool requireDay = false)
    {
        bool timeMatches = !requestedHour.HasValue
            || (CircularHourDistance(requestedHour.Value, actualHour) <= 0.1f
                && Math.Abs(calendarProgressionRate) <= 0.001f);
        bool weatherSignalsFinite = float.IsFinite(precipitation)
            && float.IsFinite(rainCloudOverlay);
        bool weatherMatches = requestedPrecipitation.HasValue
            ? weatherSignalsFinite
                && Math.Abs(precipitation - requestedPrecipitation.Value) <= 0.12f
            : !clearWeather
                || (weatherSignalsFinite
                    && precipitation <= 0.01f
                    && rainCloudOverlay <= 0.01f);
        bool lightingMatches = (!requireNight
                || (float.IsFinite(sunVertical) && sunVertical <= 0.0f))
            && (!requireDay
                || (float.IsFinite(sunVertical) && sunVertical >= 0.05f));
        return timeMatches && weatherMatches && lightingMatches;
    }

    /// <summary>
    /// Verifies that the client-only celestial-light cache has converged to the
    /// coordinate-aware calendar value. Comparing those public values supports
    /// physically valid low-sun tests; fixed daytime brightness floors wrongly
    /// reject dawn even though its long shadows are intentional.
    /// </summary>
    /// <param name="requireNight">Whether the requested scenario is nocturnal.</param>
    /// <param name="requireDay">Whether the requested scenario places the sun above the horizon.</param>
    /// <param name="spatialDaylight">Coordinate-aware daylight returned for the player position.</param>
    /// <param name="clientDaylight">Cached client daylight used by sky and chunk rendering.</param>
    /// <param name="directSunlight">Cached direct solar strength used by rendering.</param>
    /// <param name="moonlight">Cached lunar strength.</param>
    /// <returns>Whether required cached lighting is finite and agrees with the authoritative calendar.</returns>
    internal static bool HasConvergedClientSolarLighting(
        bool requireNight,
        bool requireDay,
        float spatialDaylight,
        float clientDaylight,
        float directSunlight,
        float moonlight)
    {
        if (!requireNight && !requireDay)
        {
            return true;
        }

        if (!float.IsFinite(directSunlight))
        {
            return false;
        }

        if (requireNight)
        {
            // Vintage Story 1.22.7 retains a stable ~0.058 night floor in
            // SunLightStrength even with the solar vector far below the
            // horizon. The independent sun-position gate proves nighttime;
            // this cache gate only rejects stale daylight-scale brightness.
            return directSunlight <= 0.08f;
        }

        return float.IsFinite(spatialDaylight)
            && float.IsFinite(clientDaylight)
            && float.IsFinite(moonlight)
            && spatialDaylight >= 0.05f
            && clientDaylight >= 0.05f
            && directSunlight >= 0.05f
            && moonlight <= 0.10f
            && Math.Abs(clientDaylight - spatialDaylight) <= 0.08f
            && Math.Abs(directSunlight - spatialDaylight) <= 0.08f;
    }

    /// <summary>
    /// Computes the shortest separation between two times on a 24-hour clock,
    /// correctly treating values around midnight as adjacent.
    /// </summary>
    /// <param name="expected">Reference hour.</param>
    /// <param name="actual">Observed hour.</param>
    /// <returns>Absolute circular distance in hours in the inclusive range [0, 12].</returns>
    internal static float CircularHourDistance(float expected, float actual)
    {
        float distance = Math.Abs(expected - actual) % 24.0f;
        return Math.Min(distance, 24.0f - distance);
    }

    /// <summary>
    /// Unregisters renderer and tick callbacks, removes all point lights owned
    /// by the probe, and makes repeated disposal harmless by clearing the listener id.
    /// </summary>
    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        if (tickListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }

        foreach (TestPointLight light in lights)
        {
            api.Render.RemovePointLight(light);
        }

        lights.Clear();
    }

    /// <summary>
    /// Minimal mutable-position implementation of Vintage Story's point-light
    /// contract used to exercise the same renderer path as held lights.
    /// </summary>
    /// <param name="color">Linear RGB light intensity supplied to the engine.</param>
    /// <param name="position">Mutable world-space light position in blocks.</param>
    private sealed class TestPointLight(Vec3f color, Vec3d position) : IPointLight
    {
        /// <summary>
        /// Gets the linear RGB intensity vector. Values may exceed one because
        /// this is radiometric light energy rather than a display color.
        /// </summary>
        public Vec3f Color { get; } = color;

        /// <summary>
        /// Gets the mutable world-space position in block units. The probe
        /// updates this instance in place after it has been registered.
        /// </summary>
        public Vec3d Pos { get; } = position;
    }

    /// <summary>One expected stock plant and its horizontal offset from the patch center.</summary>
    /// <param name="OffsetX">Signed X offset in blocks.</param>
    /// <param name="OffsetZ">Signed Z offset in blocks.</param>
    /// <param name="Code">Fully qualified stock block code.</param>
    private readonly record struct VegetationWitness(
        int OffsetX,
        int OffsetZ,
        string Code);

    /// <summary>
    /// States of the asynchronous framebuffer resize, shader reload and restore
    /// transaction. Terminal states never initiate further client commands.
    /// </summary>
    private enum ResizeProbeState
    {
        /// <summary>No resize validation is active.</summary>
        Inactive,

        /// <summary>The alternate resolution was requested but is not yet observable.</summary>
        WaitingForAlternateSize,

        /// <summary>The alternate framebuffer is stable and awaiting the shader reload hold time.</summary>
        HoldingAlternateSize,

        /// <summary>The original resolution was requested but is not yet observable.</summary>
        WaitingForOriginalSize,

        /// <summary>The restored framebuffer is stable and awaiting its final validation hold time.</summary>
        HoldingOriginalSize,

        /// <summary>The alternate resize, shader reload and original-size restoration all succeeded.</summary>
        Complete,

        /// <summary>A dimension timeout, mismatch or shader reload failure terminated validation.</summary>
        Failed
    }

    /// <summary>
    /// Stages of render-matrix pitch calibration for engine paths whose entity
    /// and public camera angle conventions differ.
    /// </summary>
    private enum CameraCalibrationPhase
    {
        /// <summary>No render-matrix calibration is active.</summary>
        Inactive,

        /// <summary>Samples a full 2-pi pitch rotation to locate the best broad candidate.</summary>
        PitchCoarse,

        /// <summary>Samples a pi/3 neighborhood around the winning coarse pitch.</summary>
        PitchFine
    }

    /// <summary>Lifecycle of the dedicated physically framed local-body mirror proof.</summary>
    private enum LocalBodyMirrorCaptureState
    {
        /// <summary>The last impact and its asynchronous captures have not settled yet.</summary>
        Inactive,

        /// <summary>The named entity-only transaction owns the temporary near-water camera.</summary>
        Capturing,

        /// <summary>The raw carrier was stored and the long-range lake camera was restored.</summary>
        Complete
    }
}

/// <summary>
/// World-space animated entity volume used to keep deterministic real-map cameras outside large
/// forward-rendered meshes that are intentionally absent from the terrain G-buffer.
/// </summary>
/// <param name="MinimumX">Minimum world X coordinate.</param>
/// <param name="MinimumY">Minimum world Y coordinate.</param>
/// <param name="MinimumZ">Minimum world Z coordinate.</param>
/// <param name="MaximumX">Maximum world X coordinate.</param>
/// <param name="MaximumY">Maximum world Y coordinate.</param>
/// <param name="MaximumZ">Maximum world Z coordinate.</param>
internal readonly record struct LanternCameraEntityBounds(
    double MinimumX,
    double MinimumY,
    double MinimumZ,
    double MaximumX,
    double MaximumY,
    double MaximumZ)
{
    /// <summary>
    /// Converts Vintage Story's entity-relative animated selection box to world coordinates. The
    /// collision box is included because a behavior may enlarge one volume but not the other; a
    /// conservative one-block fallback protects partially initialized entities.
    /// </summary>
    /// <param name="entity">Live non-player entity whose rendered body must stay out of the near field.</param>
    /// <returns>Conservative world-space union of its selection and collision boxes.</returns>
    internal static LanternCameraEntityBounds FromEntity(Entity entity)
    {
        Cuboidf? selection = entity.SelectionBox;
        Cuboidf? collision = entity.CollisionBox;
        double minimumX = Math.Min(selection?.X1 ?? -0.5f, collision?.X1 ?? -0.5f);
        double minimumY = Math.Min(selection?.Y1 ?? 0.0f, collision?.Y1 ?? 0.0f);
        double minimumZ = Math.Min(selection?.Z1 ?? -0.5f, collision?.Z1 ?? -0.5f);
        double maximumX = Math.Max(selection?.X2 ?? 0.5f, collision?.X2 ?? 0.5f);
        double maximumY = Math.Max(selection?.Y2 ?? 2.0f, collision?.Y2 ?? 2.0f);
        double maximumZ = Math.Max(selection?.Z2 ?? 0.5f, collision?.Z2 ?? 0.5f);
        return new LanternCameraEntityBounds(
            entity.Pos.X + minimumX,
            entity.Pos.Y + minimumY,
            entity.Pos.Z + minimumZ,
            entity.Pos.X + maximumX,
            entity.Pos.Y + maximumY,
            entity.Pos.Z + maximumZ);
    }

    /// <summary>Computes the squared Euclidean distance from a point to this closed volume.</summary>
    /// <param name="x">World X coordinate.</param>
    /// <param name="y">World Y coordinate.</param>
    /// <param name="z">World Z coordinate.</param>
    /// <returns>Zero inside the volume; otherwise the sum of squared axis separations.</returns>
    internal double SquaredDistanceTo(double x, double y, double z)
    {
        double deltaX = Math.Max(MinimumX - x, Math.Max(0.0, x - MaximumX));
        double deltaY = Math.Max(MinimumY - y, Math.Max(0.0, y - MaximumY));
        double deltaZ = Math.Max(MinimumZ - z, Math.Max(0.0, z - MaximumZ));
        return deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
    }
}

/// <summary>
/// Immutable, normalized startup contract for an opt-in runtime scenario.
/// </summary>
internal readonly record struct RuntimeScenarioSettings(
    string Scenario,
    float? RequestedWorldHour,
    bool ClearWeather,
    float? RequestedPrecipitation)
{
    /// <summary>
    /// Indicates whether at least one deterministic runtime behavior was
    /// requested. An empty scenario alone does not install game callbacks.
    /// </summary>
    internal bool Enabled => !string.IsNullOrWhiteSpace(Scenario)
        || RequestedWorldHour.HasValue
        || ClearWeather
        || RequestedPrecipitation.HasValue;
}
