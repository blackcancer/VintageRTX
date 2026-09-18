using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using VintageRTX.Configuration;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>
/// Executes the final full-screen transport pass, owns its OpenGL resources, uploads voxel/liquid
/// state, stabilizes temporal history, selects bounded lights, and adapts ray quality to measured GPU
/// cost. All borrowed game handles and GL state are restored before returning to Vintage Story.
/// </summary>
internal sealed class FilmicDisplayRenderer : IRenderer
{
    /// <summary>Projectile event whose final and surface-field evidence remains to be captured.</summary>
    private enum ProjectileEvidenceKind
    {
        /// <summary>No projectile evidence transaction is pending.</summary>
        None,
        /// <summary>First accepted real thrown-item stone ricochet.</summary>
        Stone,
        /// <summary>First accepted real flint-arrow liquid entry.</summary>
        Arrow
    }

    /// <summary>Stable runtime name used to create and diagnose the in-memory shader.</summary>
    private const string ShaderName = "vintagertx-display";
    /// <summary>Frames discarded after each automatic benchmark mode transition.</summary>
    private const int BenchmarkWarmupFrames = 120;
    /// <summary>Valid frame samples retained for each automatic benchmark measurement.</summary>
    private const int BenchmarkSampleFrames = 600;
    /// <summary>Minimum wall-clock world stabilization before the benchmark begins, in milliseconds.</summary>
    private const long BenchmarkWorldWarmupMilliseconds = 45_000;
    /// <summary>Budget fraction below which an adaptive quality promotion may accumulate credit.</summary>
    private const double AdaptiveUpgradeHeadroom = 0.55;
    /// <summary>
    /// Budget ratio above which an initial high tier is known to need Performance rather than an
    /// intermediate Balanced probe. One direct transition avoids two visible transport changes.
    /// </summary>
    private const double AdaptiveSevereOverBudgetRatio = 1.20;
    /// <summary>Consecutive over-budget frames required before reducing ray work.</summary>
    private const int AdaptiveDowngradeFrames = 120;
    // A promotion changes several full-screen ray budgets at once. Require a
    // full minute of sustained headroom at 120 FPS before probing the higher
    // tier; short upgrades repeatedly followed by a downgrade are worse for
    // frame pacing and temporal convergence than staying on the stable tier.
    /// <summary>Consecutive high-headroom frames required before probing a more expensive tier.</summary>
    private const int AdaptiveUpgradeFrames = 7200;
    /// <summary>Frames suppressing a second tier transition so temporal history can reconverge.</summary>
    private const int AdaptiveTransitionCooldownFrames = 120;
    // CameraMatrixOrigin carries a tiny render-stage translation jitter even
    // when world position and input angles are fixed. 0.0005 is just above the
    // measured 0.000226 noise floor and remains far below a visible movement.
    /// <summary>Maximum matrix-element jitter considered stationary for temporal/benchmark gates.</summary>
    private const double CameraMatrixStabilityEpsilon = 0.0005;
    /// <summary>Number of separately accumulated CPU-wall stages in the effect benchmark.</summary>
    private const int BenchmarkCpuStageCount = 6;
    /// <summary>Texture unit carrying point-light slots 0..3 during final/filter shadow passes.</summary>
    private const int ShadowPointATextureUnit = 15;
    /// <summary>Texture unit carrying point-light slots 4..7 during final/filter shadow passes.</summary>
    private const int ShadowPointBTextureUnit = 24;
    /// <summary>Texture unit carrying the independent solar visibility during final/filter passes.</summary>
    private const int ShadowSunTextureUnit = 25;
    /// <summary>Texture unit borrowing Vintage Story's alpha-tested far solar depth map.</summary>
    private const int NativeShadowFarTextureUnit = 26;
    /// <summary>Texture unit borrowing Vintage Story's alpha-tested near solar depth map.</summary>
    private const int NativeShadowNearTextureUnit = 27;
    /// <summary>Three independent MRT banks written by both raw and temporal shadow passes.</summary>
    private static readonly DrawBuffersEnum[] ShadowDrawBuffers =
    [
        DrawBuffersEnum.ColorAttachment0,
        DrawBuffersEnum.ColorAttachment1,
        DrawBuffersEnum.ColorAttachment2
    ];

    private readonly ICoreClientAPI api;
    private EntityLightCollector? entityLightCollector;
    private readonly byte[] liveCasterUpload = new byte[VoxelScene.LightCasterVoxelCount * VoxelScene.MaximumLightCount];

    private readonly Func<VintageRtxConfig> getConfig;
    private readonly VoxelScene voxelScene;
    private readonly FrameCaptureService captureService;
    private readonly LumaRenderBridge lumaBridge;
    private readonly LiquidSurfaceRuntime liquidSurfaceRuntime;
    private readonly ReflectionSourceCaptureRenderer reflectionSourceCapture;
    private readonly EntityMirrorSourceCaptureRenderer entityMirrorSourceCapture;
    private readonly EntityMirrorProjection entityMirrorProjection;
    /// <summary>Whether this renderer is registered to snapshot the per-frame floating origin.</summary>
    private bool cameraOriginCaptureRegistered;
    /// <summary>Whether the current frame owns a player origin captured before world rendering.</summary>
    private bool renderFloatingOriginReady;
    /// <summary>World X origin paired with the current frame's camera matrices.</summary>
    private double renderFloatingOriginX;
    /// <summary>World Y origin paired with the current frame's camera matrices.</summary>
    private double renderFloatingOriginY;
    /// <summary>World Z origin paired with the current frame's camera matrices.</summary>
    private double renderFloatingOriginZ;
    private readonly RenderPerformanceMonitor performanceMonitor = new();
    private readonly bool automaticBenchmark;
    private readonly bool automatedCameraLock;
    private readonly int benchmarkWarmupFrames;
    private readonly int benchmarkSampleFrames;
    private readonly long benchmarkWorldWarmupMilliseconds;

    private IShaderProgram? shader;
    private int sceneCopyTexture;
    private readonly int[] temporalHistoryTextures = new int[2];
    private readonly int[] temporalFramebuffers = new int[2];
    private int temporalHistoryIndex;
    private int shadowCurrentPointATexture;
    private int shadowCurrentPointBTexture;
    private int shadowCurrentSunTexture;
    private int shadowCurrentFramebuffer;
    private readonly int[] shadowHistoryPointATextures = new int[2];
    private readonly int[] shadowHistoryPointBTextures = new int[2];
    private readonly int[] shadowHistorySunTextures = new int[2];
    private readonly int[] shadowHistoryFramebuffers = new int[2];
    private int shadowHistoryIndex;
    private int voxelTexture;
    private int voxelOccupancyTexture;
    private int voxelLightCasterTexture;
    private int voxelFluidSurfaceTexture;
    private int voxelLiquidMetadataTexture;
    private int liquidOpticalProfilesTexture;
    private int voxelIrradianceTexture;
    private int voxelIrradianceDirectionTexture;
    private int voxelSunOccupancyTexture;
    private int voxelRainSurfaceTexture;
    private int fullscreenVertexArray;
    private int textureWidth;
    private int textureHeight;
    /// <summary>Allocated independent-shadow carrier width for the active quality tier.</summary>
    private int shadowTextureWidth;
    /// <summary>Allocated independent-shadow carrier height for the active quality tier.</summary>
    private int shadowTextureHeight;
    private bool initialized;
    private bool faulted;
    private bool gBufferAvailable;
    private bool gBufferAvailabilityLogged;
    /// <summary>Whether the runtime already reported successful zero-copy Primary sampling.</summary>
    private bool displaySourceBorrowLogged;
    /// <summary>Whether the auxiliary pre-final stage registration still belongs to this renderer.</summary>
    private bool preFinalDiagnosticRegistered;
    /// <summary>Whether both native solar-matrix capture stage registrations still belong to this renderer.</summary>
    private bool nativeSunShadowCaptureRegistered;
    /// <summary>Whether the pre-final Primary/Luma GL colour contract was reported.</summary>
    private bool preFinalColorContractLogged;
    /// <summary>Whether the post-blit Primary/default GL colour contract was reported.</summary>
    private bool postBlitColorContractLogged;
    /// <summary>Whether successful borrowing of native alpha/wind solar cascades was reported.</summary>
    private bool nativeSunShadowContractLogged;
    private string gBufferStatus = "not inspected";
    private string? failureReason;
    private readonly float[] projectionMatrix = new float[16];
    private readonly float[] inverseProjectionMatrix = new float[16];
    private readonly float[] viewMatrix = new float[16];
    private readonly float[] inverseViewMatrix = new float[16];
    /// <summary>Latest world-relative matrix used by Vintage Story's far alpha-tested shadow pass.</summary>
    private readonly float[] nativeShadowMatrixFar = new float[16];
    /// <summary>Latest world-relative matrix used by Vintage Story's near alpha-tested shadow pass.</summary>
    private readonly float[] nativeShadowMatrixNear = new float[16];
    /// <summary>Absolute player-reference origin paired with the captured far receiver matrix.</summary>
    private readonly Vec3d nativeShadowReferenceFar = new();
    /// <summary>Absolute player-reference origin paired with the captured near receiver matrix.</summary>
    private readonly Vec3d nativeShadowReferenceNear = new();
    /// <summary>World-space receiver range paired with the captured far cascade.</summary>
    private float nativeShadowRangeFar;
    /// <summary>World-space receiver range paired with the captured near cascade.</summary>
    private float nativeShadowRangeNear;
    /// <summary>Whether <see cref="nativeShadowMatrixFar"/> contains one complete finite render-stage capture.</summary>
    private bool nativeShadowMatrixFarReady;
    /// <summary>Whether <see cref="nativeShadowMatrixNear"/> contains one complete finite render-stage capture.</summary>
    private bool nativeShadowMatrixNearReady;
    private readonly double[] inverseProjectionMatrixDouble = new double[16];
    private readonly double[] inverseViewMatrixDouble = new double[16];
    /// <summary>Outer-scope snapshot protecting the primary view from official mirror callbacks.</summary>
    private readonly double[] mirrorGuardCameraMatrix = new double[16];
    /// <summary>Single-precision companion of <see cref="mirrorGuardCameraMatrix"/>.</summary>
    private readonly float[] mirrorGuardCameraMatrixFloat = new float[16];
    /// <summary>Outer-scope snapshot of the primary perspective projection.</summary>
    private readonly double[] mirrorGuardPerspectiveProjection = new double[16];
    /// <summary>Outer-scope snapshot of the engine's current float projection.</summary>
    private readonly float[] mirrorGuardCurrentProjection = new float[16];
    /// <summary>Outer-scope snapshot of the original projection-stack top.</summary>
    private readonly double[] mirrorGuardProjectionStackTop = new double[16];
    private VoxelSceneSnapshot voxelSnapshot;
    /// <summary>Newest complete CPU snapshot retained until all settle/edit queues are quiet.</summary>
    private VoxelSceneSnapshot pendingVoxelSnapshot;
    /// <summary>Whether <see cref="pendingVoxelSnapshot"/> is waiting for one atomic GPU publication.</summary>
    private bool pendingVoxelSnapshotAvailable;
    private LiquidSurfaceGpuBinding dynamicLiquidSurfaceBinding;
    private bool voxelTextureReady;
    private int voxelSamplerLocation = -1;
    private int voxelOccupancySamplerLocation = -1;
    private int voxelLightCasterSamplerLocation = -1;
    private int voxelFluidSurfaceSamplerLocation = -1;
    private int voxelLiquidMetadataSamplerLocation = -1;
    private int liquidOpticalProfilesSamplerLocation = -1;
    private int dynamicLiquidSurfaceSamplerLocation = -1;
    private int voxelIrradianceSamplerLocation = -1;
    private int voxelIrradianceDirectionSamplerLocation = -1;
    private int voxelSunOccupancySamplerLocation = -1;
    private int voxelRainSurfaceSamplerLocation = -1;
    private int voxelLightPositionLocation = -1;
    private int voxelLightColorLocation = -1;
    private int voxelLightPhotometryLocation = -1;
    private int voxelLightCasterLayerLocation = -1;
    private int liquidImpactOriginLocation = -1;
    private int liquidImpactMaterialLocation = -1;
    private int liquidImpactMotionLocation = -1;
    private int liquidImpactEnergyLocation = -1;
    private int liquidImpactSplashLocation = -1;
    private readonly float[] voxelLightPositions = new float[VoxelScene.MaximumLightCount * 4];
    private readonly float[] voxelLightColors = new float[VoxelScene.MaximumLightCount * 4];
    private readonly float[] voxelLightPhotometry = new float[VoxelScene.MaximumLightCount * 4];
    private readonly float[] voxelLightCasterLayers = new float[VoxelScene.MaximumLightCount];
    private readonly float[] voxelLightSelectionScores = new float[VoxelScene.MaximumLightCount];
    private readonly DynamicLightCandidate[] dynamicLightCandidates = new DynamicLightCandidate[64];
    private readonly float[] selectedDynamicLightViewPositions = new float[VoxelScene.MaximumLightCount * 3];
    private readonly long[] currentShadowLightSlotKeys = new long[VoxelScene.MaximumLightCount];
    private readonly long[] previousShadowLightSlotKeys = new long[VoxelScene.MaximumLightCount];
    private readonly float[] previousShadowLightPositions = new float[VoxelScene.MaximumLightCount * 3];
    private bool shadowLightSlotsInitialized;
    private int previousShadowLightSlotCount;
    private int currentVoxelLightCount;
    private readonly int[] lastSelectedDynamicSourceIndices = new int[VoxelScene.MaximumLightCount];
    private int lastSelectedDynamicSourceCount;
    private int lastDynamicLightCount = -1;
    private int lastPrimaryDynamicLightSourceIndex = -1;
    private int currentDynamicLightCount;
    private int availableDynamicLightCount;
    private bool sunConfigurationLogged;
    private int adaptiveQualityLevel;
    private int adaptiveOverBudgetFrames;
    private int adaptiveUnderBudgetFrames;
    private int adaptiveCaptureCooldownFrames;
    private int adaptiveTransitionCooldownFrames;
    private string adaptiveQualityStatus = "high";
    /// <summary>Profile whose adaptive floor currently owns <see cref="adaptiveQualityLevel"/>.</summary>
    private VintageRtxRenderProfile activeRenderProfile = (VintageRtxRenderProfile)(-1);
    private long renderedFrameCount;
    /// <summary>Post-final readback owed for the baseline or effect frame started pre-final.</summary>
    private FrameCaptureStep activeCaptureStep;
    /// <summary>Whether <see cref="activeCaptureStep"/> must be submitted at AfterBlit.</summary>
    private bool activeCaptureStepPending;
    private readonly double[] benchmarkCameraMatrix = new double[16];
    private BenchmarkPhase benchmarkPhase;
    private int benchmarkPhaseFrame;
    private int benchmarkStableFrames;
    private bool benchmarkCameraInitialized;
    private int benchmarkMatrixDiagnosticFrames;
    private double benchmarkMatrixDiagnosticMaximumDelta;
    private int benchmarkMatrixDiagnosticMaximumIndex = -1;
    private double benchmarkPositionDiagnosticMaximumDelta;
    private double benchmarkYawDiagnosticMaximumDelta;
    private double benchmarkPitchDiagnosticMaximumDelta;
    private float benchmarkPitchDiagnosticFrom;
    private float benchmarkPitchDiagnosticTo;
    private double benchmarkCameraX;
    private double benchmarkCameraY;
    private double benchmarkCameraZ;
    private float benchmarkCameraYaw;
    private float benchmarkCameraPitch;
    private PerformanceSnapshot benchmarkBaselineFirst;
    private PerformanceSnapshot benchmarkEffect;
    private string benchmarkStatus = "idle";
    /// <summary>Accumulated Stopwatch ticks for preparation, voxel, G-buffer, liquid, impact, and display.</summary>
    private readonly long[] benchmarkCpuStageTicks = new long[BenchmarkCpuStageCount];
    /// <summary>Maximum single-frame ticks observed for each measured effect stage.</summary>
    private readonly long[] benchmarkCpuStageMaximumTicks = new long[BenchmarkCpuStageCount];
    /// <summary>Complete effect frames represented by the CPU-wall diagnostic accumulators.</summary>
    private int benchmarkCpuDiagnosticFrames;
    /// <summary>Maximum total measured VintageRTX CPU-wall duration of one effect frame.</summary>
    private long benchmarkCpuMaximumFrameTicks;
    private readonly double[] temporalCameraMatrix = new double[16];
    private bool temporalCameraInitialized;
    private bool temporalHistoryValid;
    private bool shadowHistoryValid;
    private double temporalCameraX;
    private double temporalCameraY;
    private double temporalCameraZ;
    private int temporalMotionResetLogCooldownFrames;
    private float weatherSampleAccumulator = 0.25f;
    private float rawPrecipitation;
    private float rawRainCloudOverlay;
    private float rainWetnessTarget;
    private float smoothedRainWetness;
    private int lastCapturedDroppedItemImpactCount;
    private int pendingImpactEvidenceCount;
    private int pendingImpactEvidencePhase;
    private float pendingImpactEvidenceDelaySeconds;
    /// <summary>Latest projectile diagnostic sequence inspected by the event capture scheduler.</summary>
    private int lastObservedProjectileImpactSequence;
    /// <summary>Whether the first real stone ricochet has been retained for evidence.</summary>
    private bool stoneProjectileEvidenceObserved;
    /// <summary>Whether the first real arrow entry has been retained for evidence.</summary>
    private bool arrowProjectileEvidenceObserved;
    /// <summary>Whether stone evidence is waiting behind another capture transaction.</summary>
    private bool stoneProjectileEvidenceWaiting;
    /// <summary>Whether arrow evidence is waiting behind another capture transaction.</summary>
    private bool arrowProjectileEvidenceWaiting;
    /// <summary>Source diagnostic sequence retained for the first stone ricochet.</summary>
    private int stoneProjectileEvidenceSequence;
    /// <summary>Source diagnostic sequence retained for the first arrow entry.</summary>
    private int arrowProjectileEvidenceSequence;
    /// <summary>Transferred energy retained for the first stone ricochet.</summary>
    private float stoneProjectileEvidenceEnergy;
    /// <summary>Transferred energy retained for the first arrow entry.</summary>
    private float arrowProjectileEvidenceEnergy;
    /// <summary>Projectile kind currently emitting its two named capture pairs.</summary>
    private ProjectileEvidenceKind pendingProjectileEvidenceKind;
    /// <summary>Source diagnostic sequence associated with the active named capture pair.</summary>
    private int pendingProjectileEvidenceSequence;
    /// <summary>Transferred joules associated with the active named capture pair.</summary>
    private float pendingProjectileEvidenceEnergy;
    /// <summary>One-based surface-field/final phase of the active projectile evidence pair.</summary>
    private int pendingProjectileEvidencePhase;
    /// <summary>Physics-time delay before the next projectile evidence view is queued.</summary>
    private float pendingProjectileEvidenceDelaySeconds;
    /// <summary>Maximum simultaneous procedural impacts evaluated by the display shader.</summary>
    internal const int MaximumActiveSubgridImpactCount = 4;
    /// <summary>Maximum lifetime of one gravity-capillary packet before slot reuse.</summary>
    private const float MaximumSubgridImpactAgeSeconds = 8.0f;
    private readonly LiquidSurfaceSubgridImpactDiagnostic[] activeSubgridImpacts =
        new LiquidSurfaceSubgridImpactDiagnostic[MaximumActiveSubgridImpactCount];
    private readonly float[] activeSubgridImpactAges =
        new float[MaximumActiveSubgridImpactCount];
    private readonly LiquidSurfaceSubgridImpactDiagnostic[] pendingSubgridImpacts =
        new LiquidSurfaceSubgridImpactDiagnostic[MaximumActiveSubgridImpactCount];
    private readonly float[] liquidImpactOrigins =
        new float[MaximumActiveSubgridImpactCount * 4];
    private readonly float[] liquidImpactMaterials =
        new float[MaximumActiveSubgridImpactCount * 4];
    private readonly float[] liquidImpactMotions =
        new float[MaximumActiveSubgridImpactCount * 4];
    private readonly float[] liquidImpactEnergies =
        new float[MaximumActiveSubgridImpactCount * 4];
    private readonly float[] liquidImpactSplashes =
        new float[MaximumActiveSubgridImpactCount * 4];
    private int lastConsumedSubgridImpactSequence;
    private int nextSubgridImpactSlot;
    private readonly ulong[] singleSunMaskTextureUpload = new ulong[1];
    private readonly float[] singleFloatTextureUpload = new float[1];

    /// <summary>Creates the render coordinator; OpenGL allocation remains deferred to the render thread.</summary>
    /// <param name="api">Client render, world, shader, and logging services.</param>
    /// <param name="getConfig">Accessor returning the currently normalized configuration.</param>
    /// <param name="voxelScene">CPU scene producer whose snapshots are transferred to GPU textures.</param>
    public FilmicDisplayRenderer(
        ICoreClientAPI api,
        Func<VintageRtxConfig> getConfig,
        VoxelScene voxelScene)
    {
        this.api = api;
        this.getConfig = getConfig;
        this.voxelScene = voxelScene;
        captureService = new FrameCaptureService(api);
        lumaBridge = new LumaRenderBridge(api);
        liquidSurfaceRuntime = new LiquidSurfaceRuntime(api);
        reflectionSourceCapture = new ReflectionSourceCaptureRenderer(api);
        entityMirrorSourceCapture = new EntityMirrorSourceCaptureRenderer(api);
        entityMirrorProjection = new EntityMirrorProjection(api);
        automaticBenchmark = string.Equals(
            Environment.GetEnvironmentVariable("VINTAGERTX_AUTO_BENCHMARK"),
            "1",
            StringComparison.Ordinal);
        benchmarkWarmupFrames = ReadBenchmarkSetting(
            "VINTAGERTX_BENCHMARK_WARMUP_FRAMES",
            BenchmarkWarmupFrames,
            minimum: 30,
            maximum: 1_200);
        benchmarkSampleFrames = ReadBenchmarkSetting(
            "VINTAGERTX_BENCHMARK_SAMPLE_FRAMES",
            BenchmarkSampleFrames,
            minimum: 180,
            maximum: 3_600);
        benchmarkWorldWarmupMilliseconds = ReadBenchmarkSetting(
            "VINTAGERTX_BENCHMARK_WORLD_WARMUP_MS",
            BenchmarkWorldWarmupMilliseconds,
            minimum: 5_000,
            maximum: 180_000);
        automatedCameraLock = string.Equals(
            Environment.GetEnvironmentVariable("VINTAGERTX_TEST_CAMERA_LOCKED"),
            "1",
            StringComparison.Ordinal);
        api.Event.RegisterRenderer(
            this,
            EnumRenderStage.Before,
            "vintagertx-camera-origin");
        cameraOriginCaptureRegistered = true;
        api.Event.RegisterRenderer(
            this,
            EnumRenderStage.AfterPostProcessing,
            "vintagertx-color-contract");
        preFinalDiagnosticRegistered = true;
        api.Event.RegisterRenderer(
            this,
            EnumRenderStage.ShadowFar,
            "vintagertx-native-shadow-far");
        api.Event.RegisterRenderer(
            this,
            EnumRenderStage.ShadowNear,
            "vintagertx-native-shadow-near");
        nativeSunShadowCaptureRegistered = true;
    }

    /// <summary>Reads and clamps an integer benchmark environment override.</summary>
    /// <param name="variable">Environment variable name.</param>
    /// <param name="fallback">Value used for absent or malformed input.</param>
    /// <param name="minimum">Inclusive accepted lower bound.</param>
    /// <param name="maximum">Inclusive accepted upper bound.</param>
    /// <returns>Fallback or clamped parsed value.</returns>
    private static int ReadBenchmarkSetting(
        string variable,
        int fallback,
        int minimum,
        int maximum)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(variable), out int parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    /// <summary>Reads and clamps a long-duration benchmark environment override.</summary>
    /// <param name="variable">Environment variable name.</param>
    /// <param name="fallback">Value used for absent or malformed input.</param>
    /// <param name="minimum">Inclusive accepted lower bound.</param>
    /// <param name="maximum">Inclusive accepted upper bound.</param>
    /// <returns>Fallback or clamped parsed value.</returns>
    private static long ReadBenchmarkSetting(
        string variable,
        long fallback,
        long minimum,
        long maximum)
    {
        return long.TryParse(Environment.GetEnvironmentVariable(variable), out long parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    /// <summary>Runs late inside each registered stage while leaving final composition to the game.</summary>
    public double RenderOrder => 0.95;

    /// <summary>Requests every camera distance because the pass is screen-wide.</summary>
    public int RenderRange => 0;

    /// <summary>Gets the late-opaque source capture registered by the mod lifecycle root.</summary>
    internal IRenderer ReflectionSourceRenderer => reflectionSourceCapture;

    /// <summary>Gets the terrain-complete position capture registered before opaque entities.</summary>
    internal IRenderer EntityMirrorSourceRenderer => entityMirrorSourceCapture;

    /// <summary>Installs runtime interception around Vintage Story's first-person opaque draw.</summary>
    /// <returns>Whether the official player renderer was found and patched.</returns>
    internal bool InstallFirstPersonReflectionIsolation()
    {
        bool firstPersonIsolation = FirstPersonReflectionCapturePatch.Install(
            api,
            reflectionSourceCapture);
        bool geometryReplay = EntityMirrorGeometryReplayPatch.Install(api);
        return firstPersonIsolation && geometryReplay;
    }

    /// <summary>Installs exact projectile/liquid callbacks for ricochets and fast entries.</summary>
    /// <returns>Whether the common engine liquid callback was patched.</returns>
    internal bool InstallProjectileLiquidCollisionBridge() =>
        ProjectileLiquidCollisionPatch.Install(
            api,
            liquidSurfaceRuntime.ObserveProjectileLiquidCollision);

    /// <summary>Installs the narrowly scoped Vintage Story 1.22.7 fence boundary guard.</summary>
    /// <returns>Whether the exact survival-mod tessellation method was patched.</returns>
    internal bool InstallFenceStackAwareTessellationGuard() =>
        FenceStackAwareTessellationGuardPatch.Install(api);

    /// <summary>Gets whether shader, frame textures, and full-screen vertex array are allocated.</summary>
    public bool Initialized => initialized;

    /// <summary>Gets comprehensive renderer, G-buffer, timing, capture, quality, and benchmark state.</summary>
    public string Status => faulted
        ? $"faulted: {failureReason}"
        : initialized
            ? $"ready ({textureWidth}x{textureHeight}), g-buffer={gBufferStatus}, "
                + $"{performanceMonitor.BuildStatus()}, capture={captureService.Status}"
                + $", quality={adaptiveQualityStatus}, temporal="
                + $"{(temporalHistoryValid ? "accumulated" : "reset")}, benchmark={benchmarkStatus}"
            : "waiting for the render thread";

    /// <summary>Gets the absolute lossless comparison-capture directory.</summary>
    public string CaptureDirectory => captureService.CaptureDirectory;

    /// <summary>Queues one manual before/after comparison without replacing a pending request.</summary>
    /// <returns>Whether the request entered the queue.</returns>
    public bool QueueCapture() => captureService.QueueCapture();

    /// <summary>Queues a named runtime diagnostic without replacing an active capture.</summary>
    /// <param name="label">Stable evidence label written into both PNG filenames.</param>
    /// <param name="view">Display channel that must be active for the effect image.</param>
    /// <returns>Whether the request entered the single event slot.</returns>
    internal bool QueueDiagnosticCapture(string label, VintageRtxDebugView view) =>
        captureService.QueueCapture(new FrameCaptureRequest(label, view));

    /// <summary>
    /// Gets whether the named-capture slot and its current two-frame transaction are both empty.
    /// </summary>
    internal bool IsDiagnosticCaptureIdle => captureService.IsEventCaptureIdle;

    /// <summary>
    /// Injects transport into the official pre-final Luma carrier, then performs readback only after
    /// the game's final composition. Failures are contained so an unsafe bridge cannot corrupt Luma.
    /// </summary>
    /// <param name="deltaTime">Current client frame duration in seconds.</param>
    /// <param name="stage">Vintage Story render stage invoking this renderer.</param>
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage == EnumRenderStage.Before)
        {
            CaptureRenderFloatingOrigin();
            return;
        }
        if (stage is EnumRenderStage.ShadowFar or EnumRenderStage.ShadowNear)
        {
            CaptureNativeSunShadowMatrix(stage);
            return;
        }
        if (stage == EnumRenderStage.AfterPostProcessing)
        {
            RenderPreFinalFrame(deltaTime);
            return;
        }
        if (stage == EnumRenderStage.AfterBlit)
        {
            CompletePostFinalCapture();
        }
    }

    /// <summary>Runs one complete VintageRTX frame before the official final shader consumes Luma.</summary>
    /// <param name="deltaTime">Current client frame duration in seconds.</param>
    private void RenderPreFinalFrame(float deltaTime)
    {
        TryLogColorPipelineContract(
            EnumRenderStage.AfterPostProcessing,
            ref preFinalColorContractLogged);
        performanceMonitor.RecordFrame(deltaTime);
        bool skipForBenchmarkBaseline = UpdateAutomaticBenchmark();
        VintageRtxConfig config = getConfig();
        reflectionSourceCapture.Enabled = config.Enabled
            && !faulted
            && config.ScreenSpaceReflectionsEnabled;
        entityMirrorSourceCapture.Enabled = reflectionSourceCapture.Enabled;

        int width = api.Render.FrameWidth;
        int height = api.Render.FrameHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // A missing AfterBlit callback must not let an old readback attach to
        // a later frame. Restart the same request from a fresh baseline.
        if (activeCaptureStepPending)
        {
            captureService.RestartCaptureTransaction();
            activeCaptureStepPending = false;
        }

        FrameCaptureStep captureStep = default;
        captureService.ObserveVoxelSceneState(
            voxelTextureReady ? voxelSnapshot.Generation : 0,
            voxelScene.IsSettled);
        bool captureFrame = !config.Enabled || faulted
            ? false
            : captureService.TryBeginCaptureFrame(
                ++renderedFrameCount,
                out captureStep);
        if (captureFrame)
        {
            activeCaptureStep = captureStep;
            activeCaptureStepPending = true;
        }

        bool renderEffect = config.Enabled && !faulted && !skipForBenchmarkBaseline;
        if (captureFrame)
        {
            // A transaction is authoritative for presentation: baseline must
            // remain untouched and effect must execute even across two frames.
            renderEffect = captureStep.Phase == FrameCapturePhase.Effect;
        }
        if (!renderEffect)
        {
            temporalHistoryValid = false;
            shadowHistoryValid = false;
            return;
        }

        bool collectCpuDiagnostics = benchmarkPhase == BenchmarkPhase.Effect;
        long stageMarker = collectCpuDiagnostics ? Stopwatch.GetTimestamp() : 0L;
        long preparationTicks = 0L;
        long voxelTicks = 0L;
        long gBufferTicks = 0L;
        long liquidTicks = 0L;
        long impactTicks = 0L;
        long displayTicks = 0L;
        try
        {
            UpdateWeatherWetness(deltaTime);
            EnsureResources(width, height, adaptiveQualityLevel);
            preparationTicks = ReadStageTicks(collectCpuDiagnostics, ref stageMarker);
            UpdateVoxelTexture();
            voxelTicks = ReadStageTicks(collectCpuDiagnostics, ref stageMarker);
            ResolveGBuffer(out GameGBuffer gBuffer);
            gBufferTicks = ReadStageTicks(collectCpuDiagnostics, ref stageMarker);
            dynamicLiquidSurfaceBinding = liquidSurfaceRuntime.Update(
                deltaTime,
                in voxelSnapshot,
                voxelTextureReady);
            liquidTicks = ReadStageTicks(collectCpuDiagnostics, ref stageMarker);
            UpdateSubgridImpactWave(deltaTime);
            QueueDroppedItemImpactEvidence(deltaTime);
            QueueProjectileImpactEvidence(deltaTime);
            impactTicks = ReadStageTicks(collectCpuDiagnostics, ref stageMarker);
            VintageRtxDebugView effectiveDebugView = captureFrame
                && captureStep.Request.DebugViewOverride.HasValue
                    ? captureStep.Request.DebugViewOverride.Value
                    : config.DebugView;
            RenderDisplayPass(
                config,
                width,
                height,
                gBuffer,
                effectiveDebugView,
                captureFrame);
            if (captureFrame && captureStep.Phase == FrameCapturePhase.Effect)
            {
                api.Logger.Notification(
                    "[VintageRTX] Capture profile evidence: label={0}, profile={1}, effective-tier={2}.",
                    captureStep.Request.Label,
                    config.RenderProfile,
                    QualityLevelName(adaptiveQualityLevel));
                double[] captureCameraMatrix = api.Render.CameraMatrixOrigin;
                var captureEntityPosition = api.World.Player.Entity.Pos;
                Vec3d captureCameraPosition = api.World.Player.Entity.CameraPos;
                if (captureCameraMatrix.Length >= 16)
                {
                    api.Logger.Notification(
                        "[VintageRTX.Test] Capture camera evidence: label={0}, entity=({1:0.000000},{2:0.000000},{3:0.000000}), camera=({4:0.000000},{5:0.000000},{6:0.000000}), frame-origin=({7:0.000000},{8:0.000000},{9:0.000000}), basis=({10:0.000000000},{11:0.000000000},{12:0.000000000};{13:0.000000000},{14:0.000000000},{15:0.000000000};{16:0.000000000},{17:0.000000000},{18:0.000000000}), translation=({19:0.000000000},{20:0.000000000},{21:0.000000000}).",
                        captureStep.Request.Label,
                        captureEntityPosition.X,
                        captureEntityPosition.Y,
                        captureEntityPosition.Z,
                        captureCameraPosition.X,
                        captureCameraPosition.Y,
                        captureCameraPosition.Z,
                        renderFloatingOriginX,
                        renderFloatingOriginY,
                        renderFloatingOriginZ,
                        captureCameraMatrix[0],
                        captureCameraMatrix[1],
                        captureCameraMatrix[2],
                        captureCameraMatrix[4],
                        captureCameraMatrix[5],
                        captureCameraMatrix[6],
                        captureCameraMatrix[8],
                        captureCameraMatrix[9],
                        captureCameraMatrix[10],
                        captureCameraMatrix[12],
                        captureCameraMatrix[13],
                        captureCameraMatrix[14]);
                }
            }
            displayTicks = ReadStageTicks(collectCpuDiagnostics, ref stageMarker);
            if (collectCpuDiagnostics)
            {
                RecordBenchmarkCpuDiagnostics(
                    preparationTicks,
                    voxelTicks,
                    gBufferTicks,
                    liquidTicks,
                    impactTicks,
                    displayTicks);
            }
        }
        catch (Exception exception)
        {
            if (activeCaptureStepPending)
            {
                captureService.RestartCaptureTransaction();
                activeCaptureStepPending = false;
            }
            temporalHistoryValid = false;
            shadowHistoryValid = false;
            faulted = true;
            failureReason = exception.Message;
            api.Logger.Error("[VintageRTX] Display pass disabled after an OpenGL failure: {0}", exception);
        }
    }

    /// <summary>Reads the official final output for the active two-frame capture transaction.</summary>
    private void CompletePostFinalCapture()
    {
        TryLogColorPipelineContract(EnumRenderStage.AfterBlit, ref postBlitColorContractLogged);
        if (!activeCaptureStepPending)
        {
            return;
        }

        int width = api.Render.FrameWidth;
        int height = api.Render.FrameHeight;
        try
        {
            byte[] pixels = FrameCaptureService.ReadCurrentFrame(width, height);
            _ = captureService.SubmitPostFinalFrame(
                activeCaptureStep,
                pixels,
                width,
                height);
        }
        catch (Exception exception)
        {
            captureService.RestartCaptureTransaction();
            api.Logger.Warning(
                "[VintageRTX] Post-final capture restarted after readback failure: {0}",
                exception.Message);
        }
        finally
        {
            activeCaptureStepPending = false;
        }
    }

    /// <summary>
    /// Reports the exact GL colour carrier at the two engine boundaries relevant to a future
    /// pre-final capture. Validation occurs before GL access and every touched binding/capability
    /// is restored, so unsupported framebuffer layouts remain a diagnostic-only no-op.
    /// </summary>
    /// <param name="stage">AfterPostProcessing or AfterBlit.</param>
    /// <param name="alreadyLogged">Sticky per-stage success flag.</param>
    private void TryLogColorPipelineContract(EnumRenderStage stage, ref bool alreadyLogged)
    {
        if (alreadyLogged
            || !getConfig().Enabled
            || !DisplayColorPipelineContract.TryResolvePrimary(
                api.Render.FrameBuffers,
                api.Render.FrameWidth,
                api.Render.FrameHeight,
                out FrameBufferRef? primary,
                out int primaryTextureId)
            || !DisplayColorPipelineContract.TryResolveLuma(
                api.Render.FrameBuffers,
                api.Render.FrameWidth,
                api.Render.FrameHeight,
                out FrameBufferRef? luma,
                out int lumaTextureId))
        {
            return;
        }

        GlState state = GlState.Capture();
        try
        {
            GL.GetTextureLevelParameter(
                primaryTextureId,
                0,
                GetTextureParameter.TextureInternalFormat,
                out int primaryInternalFormat);
            GL.GetTextureLevelParameter(
                lumaTextureId,
                0,
                GetTextureParameter.TextureInternalFormat,
                out int lumaInternalFormat);
            int primaryEncoding = QueryFramebufferColorEncoding(
                primary!.FboId,
                FramebufferAttachment.ColorAttachment0);
            int lumaEncoding = QueryFramebufferColorEncoding(
                luma!.FboId,
                FramebufferAttachment.ColorAttachment0);
            int defaultEncoding = QueryFramebufferColorEncoding(
                0,
                FramebufferAttachment.BackLeft);
            bool framebufferSrgb = GL.IsEnabled(EnableCap.FramebufferSrgb);
            DisplayColorSignal expectedSignal =
                DisplayColorPipelineContract.ExpectedSignal(stage);
            GlColorEncoding attachmentEncoding =
                DisplayColorPipelineContract.ClassifyAttachmentEncoding(primaryEncoding);
            api.Logger.Notification(
                "[VintageRTX] Color pipeline contract | stage={0}, expected-signal={1}, Primary fbo={2}, texture={3}, internal=0x{4:X}, attachment-encoding=0x{5:X} ({6}); Luma/final-input fbo={7}, texture={8}, internal=0x{9:X}, attachment-encoding=0x{10:X} ({11}); default-encoding=0x{12:X}, framebuffer-srgb={13}, read-fbo={14}, draw-fbo={15}. Vintage Story final.fsh consumes Luma RGB plus FXAA luma alpha between AfterPostProcessing and AfterBlit; pre-final transport must use a distinct temporary target and preserve that alpha contract.",
                stage,
                expectedSignal,
                primary.FboId,
                primaryTextureId,
                primaryInternalFormat,
                primaryEncoding,
                attachmentEncoding,
                luma.FboId,
                lumaTextureId,
                lumaInternalFormat,
                lumaEncoding,
                DisplayColorPipelineContract.ClassifyAttachmentEncoding(lumaEncoding),
                defaultEncoding,
                framebufferSrgb,
                state.ReadFramebuffer,
                state.DrawFramebuffer);
            alreadyLogged = true;
        }
        catch (Exception exception)
        {
            api.Logger.Warning(
                "[VintageRTX] Color pipeline contract probe deferred at {0}: {1}",
                stage,
                exception.Message);
        }
        finally
        {
            state.Restore();
        }
    }

    /// <summary>Reads one framebuffer attachment's declared GL colour encoding.</summary>
    /// <param name="framebufferId">Zero for the default target or a positive named framebuffer.</param>
    /// <param name="attachment">ColorAttachment0 for Primary or BackLeft for the default target.</param>
    /// <returns>GL_LINEAR, GL_SRGB, or zero when the driver exposes no encoding.</returns>
    private static int QueryFramebufferColorEncoding(
        int framebufferId,
        FramebufferAttachment attachment)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebufferId);
        GL.GetFramebufferAttachmentParameter(
            FramebufferTarget.DrawFramebuffer,
            attachment,
            FramebufferParameterName.FramebufferAttachmentColorEncoding,
            out int encoding);
        return encoding;
    }

    /// <summary>
    /// Starts and ages a bounded set of recent sub-grid packets. Fixed arrays retain rapid stone
    /// ricochets simultaneously without allocating in the render loop.
    /// </summary>
    /// <param name="deltaTimeSeconds">Finite render-frame duration in seconds.</param>
    private void UpdateSubgridImpactWave(float deltaTimeSeconds)
    {
        int totalSubgridImpactCount = liquidSurfaceRuntime.TotalSubgridImpactCount;
        if (totalSubgridImpactCount < lastConsumedSubgridImpactSequence)
        {
            Array.Clear(activeSubgridImpacts);
            Array.Clear(activeSubgridImpactAges);
            lastConsumedSubgridImpactSequence = 0;
            nextSubgridImpactSlot = 0;
        }

        float elapsedSeconds = Math.Clamp(
            float.IsFinite(deltaTimeSeconds) ? deltaTimeSeconds : 0.0f,
            0.0f,
            0.10f);
        for (int slot = 0; slot < activeSubgridImpacts.Length; slot++)
        {
            if (activeSubgridImpacts[slot].Sequence > 0)
            {
                activeSubgridImpactAges[slot] += elapsedSeconds;
            }
        }

        int pendingCount = liquidSurfaceRuntime.WriteSubgridImpactsAfter(
            lastConsumedSubgridImpactSequence,
            pendingSubgridImpacts);
        for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
        {
            LiquidSurfaceSubgridImpactDiagnostic candidate =
                pendingSubgridImpacts[pendingIndex];
            int slot = SelectSubgridImpactSlot(
                activeSubgridImpacts,
                activeSubgridImpactAges,
                nextSubgridImpactSlot);
            activeSubgridImpacts[slot] = candidate;
            activeSubgridImpactAges[slot] = 0.0f;
            nextSubgridImpactSlot = (slot + 1) % activeSubgridImpacts.Length;
            lastConsumedSubgridImpactSequence = Math.Max(
                lastConsumedSubgridImpactSequence,
                candidate.Sequence);
        }
    }

    /// <summary>
    /// Captures the player-relative world origin before opaque rendering. Reading <see cref="Entity.Pos"/>
    /// again after post-processing can observe the following physics interpolation step while
    /// <see cref="IRenderAPI.CameraMatrixOrigin"/> still describes the rendered frame, shifting every
    /// reconstructed voxel ray by several centimetres.
    /// </summary>
    private void CaptureRenderFloatingOrigin()
    {
        EntityPlayer? player = api.World.Player?.Entity;
        if (player is null)
        {
            renderFloatingOriginReady = false;
            return;
        }

        renderFloatingOriginX = player.Pos.X;
        renderFloatingOriginY = player.Pos.Y;
        renderFloatingOriginZ = player.Pos.Z;
        renderFloatingOriginReady = true;
    }

    /// <summary>Returns the origin captured with this frame, with a startup-only live fallback.</summary>
    /// <param name="x">Resolved world X origin.</param>
    /// <param name="y">Resolved world Y origin.</param>
    /// <param name="z">Resolved world Z origin.</param>
    private void ResolveRenderFloatingOrigin(out double x, out double y, out double z)
    {
        if (renderFloatingOriginReady)
        {
            x = renderFloatingOriginX;
            y = renderFloatingOriginY;
            z = renderFloatingOriginZ;
            return;
        }

        var liveOrigin = api.World.Player.Entity.Pos;
        x = liveOrigin.X;
        y = liveOrigin.Y;
        z = liveOrigin.Z;
    }

    /// <summary>
    /// Retains the exact matrices used by Vintage Story's wind-deformed, alpha-tested terrain
    /// shadow passes. The borrowed depth maps are sampled later; no native GL ownership moves here.
    /// </summary>
    /// <param name="stage">Near or far solar shadow stage currently rendering.</param>
    private void CaptureNativeSunShadowMatrix(EnumRenderStage stage)
    {
        DefaultShaderUniforms uniforms = api.Render.ShaderUniforms;
        float[] source = stage == EnumRenderStage.ShadowNear
            ? uniforms.ToShadowMapSpaceMatrixNear
            : uniforms.ToShadowMapSpaceMatrixFar;
        float[] destination = stage == EnumRenderStage.ShadowNear
            ? nativeShadowMatrixNear
            : nativeShadowMatrixFar;
        Vec3d? sourceReference = uniforms.playerReferencePos;
        float sourceRange = stage == EnumRenderStage.ShadowNear
            ? uniforms.ShadowRangeNear
            : uniforms.ShadowRangeFar;
        bool complete = source is { Length: >= 16 }
            && sourceReference is not null
            && double.IsFinite(sourceReference.X)
            && double.IsFinite(sourceReference.Y)
            && double.IsFinite(sourceReference.Z)
            && float.IsFinite(sourceRange)
            && sourceRange > 0.0f;
        for (int index = 0; complete && index < destination.Length; index++)
        {
            float value = source[index];
            if (!float.IsFinite(value))
            {
                complete = false;
                break;
            }
            destination[index] = value;
        }

        if (stage == EnumRenderStage.ShadowNear)
        {
            if (complete)
            {
                nativeShadowReferenceNear.Set(
                    sourceReference!.X,
                    sourceReference.Y,
                    sourceReference.Z);
                nativeShadowRangeNear = sourceRange;
            }
            nativeShadowMatrixNearReady = complete;
        }
        else
        {
            if (complete)
            {
                nativeShadowReferenceFar.Set(
                    sourceReference!.X,
                    sourceReference.Y,
                    sourceReference.Z);
                nativeShadowRangeFar = sourceRange;
            }
            nativeShadowMatrixFarReady = complete;
        }
    }

    /// <summary>
    /// Resolves only borrowed, complete native solar depth attachments. VintageRTX never deletes,
    /// resizes, or changes the comparison state of these engine-owned textures.
    /// </summary>
    /// <param name="frameBuffers">Current public Vintage Story framebuffer registry.</param>
    /// <returns>Available far/near depth texture identifiers; zero denotes an unavailable map.</returns>
    internal static NativeSunShadowDepthMaps ResolveNativeSunShadowDepthMaps(
        IReadOnlyList<FrameBufferRef>? frameBuffers)
    {
        return new NativeSunShadowDepthMaps(
            ResolveNativeShadowDepthTexture(frameBuffers, EnumFrameBuffer.ShadowmapFar),
            ResolveNativeShadowDepthTexture(frameBuffers, EnumFrameBuffer.ShadowmapNear));
    }

    /// <summary>Validates one engine-owned shadow framebuffer before its depth texture is borrowed.</summary>
    /// <param name="frameBuffers">Public framebuffer registry, possibly unavailable during startup.</param>
    /// <param name="kind">Near or far native solar framebuffer slot.</param>
    /// <returns>Positive depth texture identifier or zero.</returns>
    private static int ResolveNativeShadowDepthTexture(
        IReadOnlyList<FrameBufferRef>? frameBuffers,
        EnumFrameBuffer kind)
    {
        int index = (int)kind;
        if (frameBuffers is null || index < 0 || index >= frameBuffers.Count)
        {
            return 0;
        }

        FrameBufferRef? frameBuffer = frameBuffers[index];
        return frameBuffer is not null
            && !frameBuffer.Disposed
            && frameBuffer.Width > 0
            && frameBuffer.Height > 0
            && frameBuffer.DepthTextureId > 0
                ? frameBuffer.DepthTextureId
                : 0;
    }

    /// <summary>Checks globally sequenced packet identity without conflating source-local counters.</summary>
    /// <param name="active">Packet currently aged by the renderer.</param>
    /// <param name="latest">Latest packet published by the solver runtime.</param>
    /// <returns>Whether the renderer must activate the candidate at age zero.</returns>
    internal static bool IsNewSubgridImpact(
        in LiquidSurfaceSubgridImpactDiagnostic active,
        in LiquidSurfaceSubgridImpactDiagnostic latest) => latest.Sequence > 0
            && (latest.Sequence != active.Sequence
                || latest.EntityId != active.EntityId
                || latest.SurfaceClass != active.SurfaceClass);

    /// <summary>Selects an empty slot first, otherwise the oldest packet from a rotating start.</summary>
    /// <param name="active">Fixed active packet slots.</param>
    /// <param name="ages">Age in seconds paired one-to-one with <paramref name="active"/>.</param>
    /// <param name="startSlot">Rotating search origin for deterministic tie-breaking.</param>
    /// <returns>Reusable slot index.</returns>
    internal static int SelectSubgridImpactSlot(
        ReadOnlySpan<LiquidSurfaceSubgridImpactDiagnostic> active,
        ReadOnlySpan<float> ages,
        int startSlot)
    {
        if (active.IsEmpty || active.Length != ages.Length)
        {
            throw new ArgumentException("Sub-grid packet and age storage must be non-empty and aligned.");
        }

        int normalizedStart = ((startSlot % active.Length) + active.Length) % active.Length;
        int oldestSlot = normalizedStart;
        float oldestAge = float.NegativeInfinity;
        for (int offset = 0; offset < active.Length; offset++)
        {
            int slot = (normalizedStart + offset) % active.Length;
            if (active[slot].Sequence <= 0)
            {
                return slot;
            }
            if (ages[slot] > oldestAge)
            {
                oldestAge = ages[slot];
                oldestSlot = slot;
            }
        }

        return oldestSlot;
    }

    /// <summary>
    /// Schedules final and surface-field evidence shortly after the physical solver accepts a real
    /// dropped-item entry. The small physics-time delay lets the velocity impulse form a ring while
    /// removing nondeterministic entity/network delay from the water scenario.
    /// </summary>
    /// <param name="deltaTimeSeconds">Current frame duration used for a physics-time delay after entry.</param>
    private void QueueDroppedItemImpactEvidence(float deltaTimeSeconds)
    {
        int impactCount = liquidSurfaceRuntime.TotalDroppedItemImpactCount;
        if (impactCount < lastCapturedDroppedItemImpactCount)
        {
            lastCapturedDroppedItemImpactCount = impactCount;
            pendingImpactEvidenceCount = 0;
            pendingImpactEvidencePhase = 0;
            pendingImpactEvidenceDelaySeconds = 0.0f;
            return;
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_SCENARIO"),
                "water-reflection",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (impactCount > lastCapturedDroppedItemImpactCount)
        {
            lastCapturedDroppedItemImpactCount = impactCount;
            pendingImpactEvidenceCount = impactCount;
            pendingImpactEvidencePhase = 1;
            // The impulse first changes velocity. Wait long enough for the
            // physically dispersed gravity-capillary packet to leave its
            // sub-20 cm source envelope: at the water profile's measured group
            // velocity this exposes a readable ring without slowing the wave.
            pendingImpactEvidenceDelaySeconds = 0.65f;
        }

        if (pendingImpactEvidencePhase == 0)
        {
            return;
        }

        pendingImpactEvidenceDelaySeconds -= Math.Clamp(
            float.IsFinite(deltaTimeSeconds) ? deltaTimeSeconds : 0.0f,
            0.0f,
            0.10f);
        if (pendingImpactEvidenceDelaySeconds > 0.0f)
        {
            return;
        }

        VintageRtxDebugView view = pendingImpactEvidencePhase switch
        {
            1 => VintageRtxDebugView.Final,
            2 => VintageRtxDebugView.LiquidSurfaceField,
            _ => VintageRtxDebugView.ReflectionSource
        };
        string viewLabel = view switch
        {
            VintageRtxDebugView.Final => "final",
            VintageRtxDebugView.LiquidSurfaceField => "surface-field",
            _ => "reflection-source"
        };
        string label = $"impact-{pendingImpactEvidenceCount}-{viewLabel}";
        bool queued = captureService.QueueCapture(new FrameCaptureRequest(label, view));
        api.Logger.Notification(
            "[VintageRTX.Test] Impact-synchronized capture {0}: count={1}, peak={2:0.0000} m, view={3}.",
            queued ? "queued" : "skipped (capture slot busy)",
            pendingImpactEvidenceCount,
            liquidSurfaceRuntime.LastAppliedImpactPeakDisplacement,
            view);
        if (!queued)
        {
            return;
        }

        pendingImpactEvidencePhase++;
        if (pendingImpactEvidencePhase > 3)
        {
            pendingImpactEvidencePhase = 0;
            pendingImpactEvidenceCount = 0;
            pendingImpactEvidenceDelaySeconds = 0.0f;
        }
        else
        {
            pendingImpactEvidenceDelaySeconds = 0.04f;
        }
    }

    /// <summary>
    /// Queues a stable surface-field/final pair after the first real stone ricochet and the first
    /// real arrow entry. The diagnostic field is deliberately first so capture-transaction latency
    /// cannot age a compact projectile wave behind the unrelated final-frame witness; retained waiting
    /// flags prevent a busy capture slot from losing either event, while later stone bounces cannot
    /// replace the first comparable witness.
    /// </summary>
    /// <param name="deltaTimeSeconds">Current finite frame duration used to age the wave delay.</param>
    private void QueueProjectileImpactEvidence(float deltaTimeSeconds)
    {
        LiquidProjectileImpactDiagnostic latest =
            liquidSurfaceRuntime.LastProjectileImpactDiagnostic;
        if (latest.Sequence < lastObservedProjectileImpactSequence)
        {
            ResetProjectileImpactEvidence();
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_SCENARIO"),
                "water-reflection",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (latest.Sequence > lastObservedProjectileImpactSequence)
        {
            lastObservedProjectileImpactSequence = latest.Sequence;
            LogProjectileImpactScreenAnchor(in latest);
            if (latest.SurfaceClass == LiquidEntitySurfaceClass.ThrownStone
                && latest.ImpulseKind == LiquidSurfaceImpulseKind.StoneRicochet
                && !stoneProjectileEvidenceObserved)
            {
                stoneProjectileEvidenceObserved = true;
                stoneProjectileEvidenceWaiting = true;
                stoneProjectileEvidenceSequence = latest.Sequence;
                stoneProjectileEvidenceEnergy = latest.EnergyJoules;
            }
            else if (latest.SurfaceClass == LiquidEntitySurfaceClass.Projectile
                && latest.ImpulseKind == LiquidSurfaceImpulseKind.GenericEntry
                && !arrowProjectileEvidenceObserved)
            {
                arrowProjectileEvidenceObserved = true;
                arrowProjectileEvidenceWaiting = true;
                arrowProjectileEvidenceSequence = latest.Sequence;
                arrowProjectileEvidenceEnergy = latest.EnergyJoules;
            }
        }

        if (pendingProjectileEvidenceKind == ProjectileEvidenceKind.None)
        {
            if (stoneProjectileEvidenceWaiting)
            {
                BeginProjectileImpactEvidence(
                    ProjectileEvidenceKind.Stone,
                    stoneProjectileEvidenceSequence,
                    stoneProjectileEvidenceEnergy,
                    delaySeconds: 0.12f);
            }
            else if (arrowProjectileEvidenceWaiting)
            {
                BeginProjectileImpactEvidence(
                    ProjectileEvidenceKind.Arrow,
                    arrowProjectileEvidenceSequence,
                    arrowProjectileEvidenceEnergy,
                    delaySeconds: 0.08f);
            }
            else
            {
                return;
            }
        }

        pendingProjectileEvidenceDelaySeconds -= Math.Clamp(
            float.IsFinite(deltaTimeSeconds) ? deltaTimeSeconds : 0.0f,
            0.0f,
            0.10f);
        if (pendingProjectileEvidenceDelaySeconds > 0.0f)
        {
            return;
        }

        VintageRtxDebugView view = pendingProjectileEvidencePhase == 1
            ? VintageRtxDebugView.LiquidSurfaceField
            : VintageRtxDebugView.Final;
        string kindLabel = pendingProjectileEvidenceKind == ProjectileEvidenceKind.Stone
            ? "stone"
            : "arrow";
        string viewLabel = view == VintageRtxDebugView.Final
            ? "final"
            : "surface-field";
        string label = $"projectile-{kindLabel}-{viewLabel}";
        bool queued = captureService.QueueCapture(new FrameCaptureRequest(label, view));
        api.Logger.Notification(
            "[VintageRTX.Test] Projectile-synchronized capture {0}: projectile={1}, source-sequence={2}, energy={3:0.0000} J, view={4}, label={5}.",
            queued ? "queued" : "skipped (capture slot busy)",
            kindLabel,
            pendingProjectileEvidenceSequence,
            pendingProjectileEvidenceEnergy,
            view,
            label);
        if (!queued)
        {
            return;
        }

        pendingProjectileEvidencePhase++;
        if (pendingProjectileEvidencePhase > 2)
        {
            if (pendingProjectileEvidenceKind == ProjectileEvidenceKind.Stone)
            {
                stoneProjectileEvidenceWaiting = false;
            }
            else
            {
                arrowProjectileEvidenceWaiting = false;
            }

            pendingProjectileEvidenceKind = ProjectileEvidenceKind.None;
            pendingProjectileEvidenceSequence = 0;
            pendingProjectileEvidenceEnergy = 0.0f;
            pendingProjectileEvidencePhase = 0;
            pendingProjectileEvidenceDelaySeconds = 0.0f;
        }
        else
        {
            // The capture service latches a complete two-frame transaction.
            // Leave one render interval between named views so the first pair
            // is selected before the second occupies the single event slot.
            pendingProjectileEvidenceDelaySeconds = 0.04f;
        }
    }

    /// <summary>
    /// Projects the exact solver contact into the captured framebuffer and records a local image
    /// anchor. This removes any need for an offline validator to guess the camera FOV or infer the
    /// impact from unrelated wind rings and moving projectile silhouettes.
    /// </summary>
    /// <param name="impact">Accepted projectile contact expressed in world X/Z.</param>
    private void LogProjectileImpactScreenAnchor(in LiquidProjectileImpactDiagnostic impact)
    {
        int width = api.Render.FrameWidth;
        int height = api.Render.FrameHeight;
        var origin = api.World.Player.Entity.Pos;
        if (!liquidSurfaceRuntime.TryGetNearestSurfaceWorldY(
                impact.WorldX,
                impact.WorldZ,
                out float surfaceWorldY)
            || !TryProjectWorldPoint(
                impact.WorldX,
                surfaceWorldY,
                impact.WorldZ,
                origin.X,
                origin.Y,
                origin.Z,
                api.Render.CameraMatrixOrigin,
                api.Render.PerspectiveProjectionMat,
                width,
                height,
                out double screenX,
                out double screenY))
        {
            api.Logger.Warning(
                "[VintageRTX.Test] Projectile impact screen anchor unavailable: sequence={0}, viewport={1}x{2}.",
                impact.Sequence,
                width,
                height);
            return;
        }

        string kind = impact.SurfaceClass == LiquidEntitySurfaceClass.ThrownStone
            ? "stone"
            : "arrow";
        api.Logger.Notification(
            "[VintageRTX.Test] Projectile impact screen anchor: sequence={0}, projectile={1}, pixel=({2:0.00},{3:0.00}), viewport={4}x{5}.",
            impact.Sequence,
            kind,
            screenX,
            screenY,
            width,
            height);

        // A circle in the horizontal liquid plane is generally a strongly foreshortened ellipse
        // in the framebuffer. Record the local projective tangent frame at the contact and at four
        // non-overlapping, physically unforced controls; the offline validator can then compare
        // equal world-space areas instead of unrelated fixed pixel discs.
        double controlDistance = Math.Max(
            impact.SupportRadiusWorldBlocks * 2.75,
            impact.SupportRadiusWorldBlocks + 1.0);
        (string Role, double OffsetX, double OffsetZ)[] frames =
        [
            ("contact", 0.0, 0.0),
            ("control-1", controlDistance, 0.0),
            ("control-2", -controlDistance, 0.0),
            ("control-3", 0.0, controlDistance),
            ("control-4", 0.0, -controlDistance)
        ];
        const double tangentHalfStepWorldBlocks = 0.5;
        foreach ((string role, double offsetX, double offsetZ) in frames)
        {
            double frameWorldX = impact.WorldX + offsetX;
            double frameWorldZ = impact.WorldZ + offsetZ;
            if (!TryProjectWorldPoint(
                    frameWorldX,
                    surfaceWorldY,
                    frameWorldZ,
                    origin.X,
                    origin.Y,
                    origin.Z,
                    api.Render.CameraMatrixOrigin,
                    api.Render.PerspectiveProjectionMat,
                    width,
                    height,
                    out double centerScreenX,
                    out double centerScreenY)
                || !TryProjectWorldPoint(
                    frameWorldX - tangentHalfStepWorldBlocks,
                    surfaceWorldY,
                    frameWorldZ,
                    origin.X,
                    origin.Y,
                    origin.Z,
                    api.Render.CameraMatrixOrigin,
                    api.Render.PerspectiveProjectionMat,
                    width,
                    height,
                    out double minusXScreenX,
                    out double minusXScreenY)
                || !TryProjectWorldPoint(
                    frameWorldX + tangentHalfStepWorldBlocks,
                    surfaceWorldY,
                    frameWorldZ,
                    origin.X,
                    origin.Y,
                    origin.Z,
                    api.Render.CameraMatrixOrigin,
                    api.Render.PerspectiveProjectionMat,
                    width,
                    height,
                    out double plusXScreenX,
                    out double plusXScreenY)
                || !TryProjectWorldPoint(
                    frameWorldX,
                    surfaceWorldY,
                    frameWorldZ - tangentHalfStepWorldBlocks,
                    origin.X,
                    origin.Y,
                    origin.Z,
                    api.Render.CameraMatrixOrigin,
                    api.Render.PerspectiveProjectionMat,
                    width,
                    height,
                    out double minusZScreenX,
                    out double minusZScreenY)
                || !TryProjectWorldPoint(
                    frameWorldX,
                    surfaceWorldY,
                    frameWorldZ + tangentHalfStepWorldBlocks,
                    origin.X,
                    origin.Y,
                    origin.Z,
                    api.Render.CameraMatrixOrigin,
                    api.Render.PerspectiveProjectionMat,
                    width,
                    height,
                    out double plusZScreenX,
                    out double plusZScreenY))
            {
                continue;
            }

            api.Logger.Notification(
                "[VintageRTX.Test] Projectile impact surface frame: sequence={0}, projectile={1}, role={2}, center=({3:0.00},{4:0.00}), dscreen-dworld-x=({5:0.00},{6:0.00}), dscreen-dworld-z=({7:0.00},{8:0.00}), support-radius={9:0.000} m.",
                impact.Sequence,
                kind,
                role,
                centerScreenX,
                centerScreenY,
                plusXScreenX - minusXScreenX,
                plusXScreenY - minusXScreenY,
                plusZScreenX - minusZScreenX,
                plusZScreenY - minusZScreenY,
                impact.SupportRadiusWorldBlocks);
        }
    }

    /// <summary>Projects one floating-origin world point into top-left framebuffer coordinates.</summary>
    /// <param name="worldX">Absolute world X.</param>
    /// <param name="worldY">Absolute world Y.</param>
    /// <param name="worldZ">Absolute world Z.</param>
    /// <param name="originX">Floating renderer origin X.</param>
    /// <param name="originY">Floating renderer origin Y.</param>
    /// <param name="originZ">Floating renderer origin Z.</param>
    /// <param name="view">Column-major floating-origin view matrix.</param>
    /// <param name="projection">Column-major perspective matrix.</param>
    /// <param name="width">Positive framebuffer width.</param>
    /// <param name="height">Positive framebuffer height.</param>
    /// <param name="screenX">Projected top-left pixel X.</param>
    /// <param name="screenY">Projected top-left pixel Y.</param>
    /// <returns>Whether the finite point lies in front of the camera and near the viewport.</returns>
    internal static bool TryProjectWorldPoint(
        double worldX,
        double worldY,
        double worldZ,
        double originX,
        double originY,
        double originZ,
        double[] view,
        double[] projection,
        int width,
        int height,
        out double screenX,
        out double screenY)
    {
        screenX = 0.0;
        screenY = 0.0;
        if (view.Length < 16 || projection.Length < 16 || width <= 0 || height <= 0)
        {
            return false;
        }

        double x = worldX - originX;
        double y = worldY - originY;
        double z = worldZ - originZ;
        double viewX = view[0] * x + view[4] * y + view[8] * z + view[12];
        double viewY = view[1] * x + view[5] * y + view[9] * z + view[13];
        double viewZ = view[2] * x + view[6] * y + view[10] * z + view[14];
        double viewW = view[3] * x + view[7] * y + view[11] * z + view[15];
        double clipX = projection[0] * viewX
            + projection[4] * viewY
            + projection[8] * viewZ
            + projection[12] * viewW;
        double clipY = projection[1] * viewX
            + projection[5] * viewY
            + projection[9] * viewZ
            + projection[13] * viewW;
        double clipW = projection[3] * viewX
            + projection[7] * viewY
            + projection[11] * viewZ
            + projection[15] * viewW;
        if (!double.IsFinite(clipX)
            || !double.IsFinite(clipY)
            || !double.IsFinite(clipW)
            || clipW <= 0.0001)
        {
            return false;
        }

        double normalizedX = clipX / clipW;
        double normalizedY = clipY / clipW;
        if (Math.Abs(normalizedX) > 1.08 || Math.Abs(normalizedY) > 1.08)
        {
            return false;
        }

        screenX = (normalizedX * 0.5 + 0.5) * width;
        screenY = (0.5 - normalizedY * 0.5) * height;
        return true;
    }

    /// <summary>Begins one retained projectile's named final/surface-field capture pair.</summary>
    /// <param name="kind">Stone or arrow evidence kind.</param>
    /// <param name="sequence">Exact solver diagnostic sequence.</param>
    /// <param name="energyJoules">Transferred surface energy in joules.</param>
    /// <param name="delaySeconds">Delay allowing the first resolved wave ring to form.</param>
    private void BeginProjectileImpactEvidence(
        ProjectileEvidenceKind kind,
        int sequence,
        float energyJoules,
        float delaySeconds)
    {
        pendingProjectileEvidenceKind = kind;
        pendingProjectileEvidenceSequence = sequence;
        pendingProjectileEvidenceEnergy = energyJoules;
        pendingProjectileEvidencePhase = 1;
        pendingProjectileEvidenceDelaySeconds = delaySeconds;
    }

    /// <summary>Clears retained projectile evidence when the liquid simulation sequence restarts.</summary>
    private void ResetProjectileImpactEvidence()
    {
        lastObservedProjectileImpactSequence = 0;
        stoneProjectileEvidenceObserved = false;
        arrowProjectileEvidenceObserved = false;
        stoneProjectileEvidenceWaiting = false;
        arrowProjectileEvidenceWaiting = false;
        stoneProjectileEvidenceSequence = 0;
        arrowProjectileEvidenceSequence = 0;
        stoneProjectileEvidenceEnergy = 0.0f;
        arrowProjectileEvidenceEnergy = 0.0f;
        pendingProjectileEvidenceKind = ProjectileEvidenceKind.None;
        pendingProjectileEvidenceSequence = 0;
        pendingProjectileEvidenceEnergy = 0.0f;
        pendingProjectileEvidencePhase = 0;
        pendingProjectileEvidenceDelaySeconds = 0.0f;
    }

    /// <summary>Samples current climate at 4 Hz and advances the persistent wet-film response each frame.</summary>
    /// <param name="deltaTime">Frame duration in seconds, clamped to avoid large simulation jumps.</param>
    private void UpdateWeatherWetness(float deltaTime)
    {
        float seconds = Math.Clamp(float.IsFinite(deltaTime) ? deltaTime : 0.0f, 0.0f, 0.10f);
        weatherSampleAccumulator += seconds;
        if (weatherSampleAccumulator >= 0.25f && api.World.Player?.Entity is not null)
        {
            weatherSampleAccumulator %= 0.25f;
            ClimateCondition? climate = api.World.BlockAccessor.GetClimateAt(
                api.World.Player.Entity.Pos.AsBlockPos,
                EnumGetClimateMode.NowValues);
            rawPrecipitation = climate is not null && float.IsFinite(climate.Rainfall)
                ? climate.Rainfall
                : 0.0f;
            rawRainCloudOverlay = climate is not null && float.IsFinite(climate.RainCloudOverlay)
                ? climate.RainCloudOverlay
                : 0.0f;
            rainWetnessTarget = ComputeRainWetnessTarget(
                rawPrecipitation,
                rawRainCloudOverlay);
        }

        smoothedRainWetness = AdvanceRainWetness(
            smoothedRainWetness,
            rainWetnessTarget,
            seconds);
        if (ShouldSnapDeterministicClearWeather(
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID"),
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_CLEAR_WEATHER"),
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_ENVIRONMENT_READY"),
                rainWetnessTarget))
        {
            // Runtime tests request an explicit dry baseline rather than a
            // simulation of a formerly wet world. Once the game climate and
            // authored scene are both verified, discard only that test copy's
            // startup deposition so the first capture shares the same physical
            // surface state as the following captures.
            smoothedRainWetness = 0.0f;
        }
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID"))
            && benchmarkPhase == BenchmarkPhase.Waiting
            && (renderedFrameCount & 255) == 0)
        {
            api.Logger.Notification(
                "[VintageRTX] Weather wetness: precipitation={0:0.000}, rain-cloud overlay={1:0.000}, target={2:0.000}, smoothed={3:0.000}.",
                rawPrecipitation,
                rawRainCloudOverlay,
                rainWetnessTarget,
                smoothedRainWetness);
        }
    }

    /// <summary>Combines physical precipitation with visible cloud-front confirmation.</summary>
    /// <param name="precipitation">Current rainfall intensity in 0..1.</param>
    /// <param name="rainCloudOverlay">Visible rain-cloud overlay in 0..1; never creates rain alone.</param>
    /// <returns>Bounded wetness target in 0..1.</returns>
    internal static float ComputeRainWetnessTarget(float precipitation, float rainCloudOverlay)
    {
        float rain = Math.Clamp(float.IsFinite(precipitation) ? precipitation : 0.0f, 0.0f, 1.0f);
        float cloud = Math.Clamp(float.IsFinite(rainCloudOverlay) ? rainCloudOverlay : 0.0f, 0.0f, 1.0f);
        // NowValues.Rainfall is the public current precipitation signal. The
        // cloud overlay confirms that the visible weather front is present, but
        // never creates wetness by itself and never suppresses forced rain.
        return rain * (0.62f + 0.38f * Math.Max(rain, cloud));
    }

    /// <summary>Advances asymmetric exponential deposition/evaporation without frame-rate dependence.</summary>
    /// <param name="current">Current surface film in 0..1.</param>
    /// <param name="target">Weather-derived equilibrium in 0..1.</param>
    /// <param name="deltaSeconds">Step duration in seconds, saturated at 0.25.</param>
    /// <returns>New bounded wetness; deposition uses 1.6 s and evaporation 14 s time constants.</returns>
    internal static float AdvanceRainWetness(float current, float target, float deltaSeconds)
    {
        float clampedCurrent = Math.Clamp(current, 0.0f, 1.0f);
        float clampedTarget = Math.Clamp(target, 0.0f, 1.0f);
        float seconds = Math.Clamp(float.IsFinite(deltaSeconds) ? deltaSeconds : 0.0f, 0.0f, 0.25f);
        // Rain deposits a film quickly; evaporation is deliberately slower so
        // small network/weather fluctuations cannot flash the material state.
        float timeConstant = clampedTarget > clampedCurrent ? 1.6f : 14.0f;
        float response = 1.0f - MathF.Exp(-seconds / timeConstant);
        return clampedCurrent + (clampedTarget - clampedCurrent) * response;
    }

    /// <summary>Advances the paired baseline/effect benchmark state machine on a stable locked camera.</summary>
    /// <returns>Whether VintageRTX rendering must be skipped for the current baseline frame.</returns>
    private bool UpdateAutomaticBenchmark()
    {
        if (!automaticBenchmark || benchmarkPhase == BenchmarkPhase.Complete)
        {
            return false;
        }

        bool cameraStable = UpdateBenchmarkCameraStability();
        if (benchmarkPhase != BenchmarkPhase.Waiting && !cameraStable)
        {
            reflectionSourceCapture.EndAndLogCpuDiagnostics();
            benchmarkPhase = BenchmarkPhase.Waiting;
            benchmarkPhaseFrame = 0;
            benchmarkStableFrames = 0;
            performanceMonitor.ResetStatistics();
            benchmarkStatus = "restarted: camera moved";
            api.Logger.Notification("[VintageRTX] A/B/A benchmark restarted because the camera moved.");
            return false;
        }

        if (benchmarkPhase == BenchmarkPhase.Waiting)
        {
            bool worldReady = api.InWorldEllapsedMilliseconds >= benchmarkWorldWarmupMilliseconds
                && voxelScene.IsReady
                && captureService.ReadyForBenchmark;
            if (!worldReady || benchmarkStableFrames < benchmarkWarmupFrames)
            {
                benchmarkStatus = $"waiting world/camera ({api.InWorldEllapsedMilliseconds / 1000}s)";
                return false;
            }

            benchmarkPhase = BenchmarkPhase.BaselineFirst;
            benchmarkPhaseFrame = 0;
            performanceMonitor.ResetStatistics();
            benchmarkStatus = $"baseline A1 0/{benchmarkSampleFrames}";
            api.Logger.Notification(
                "[VintageRTX] Stabilized A/B/A benchmark started after {0:0.0}s in-world (camera locked, warmup={1} frames, samples={2} frames, profile={3}, effective-tier={4}).",
                api.InWorldEllapsedMilliseconds / 1000.0,
                benchmarkWarmupFrames,
                benchmarkSampleFrames,
                activeRenderProfile,
                QualityLevelName(adaptiveQualityLevel));
        }

        benchmarkPhaseFrame++;
        switch (benchmarkPhase)
        {
            case BenchmarkPhase.BaselineFirst:
                benchmarkStatus = $"baseline A1 {benchmarkPhaseFrame}/{benchmarkSampleFrames}";
                if (benchmarkPhaseFrame >= benchmarkSampleFrames)
                {
                    benchmarkBaselineFirst = performanceMonitor.CreateSnapshot();
                    BeginBenchmarkPhase(BenchmarkPhase.EffectWarmup, "effect warmup");
                }
                return true;

            case BenchmarkPhase.EffectWarmup:
                benchmarkStatus = $"effect warmup {benchmarkPhaseFrame}/{benchmarkWarmupFrames}";
                if (benchmarkPhaseFrame >= benchmarkWarmupFrames)
                {
                    BeginBenchmarkPhase(BenchmarkPhase.Effect, $"effect 0/{benchmarkSampleFrames}");
                }
                return false;

            case BenchmarkPhase.Effect:
                benchmarkStatus = $"effect {benchmarkPhaseFrame}/{benchmarkSampleFrames}";
                if (benchmarkPhaseFrame >= benchmarkSampleFrames)
                {
                    benchmarkEffect = performanceMonitor.CreateSnapshot();
                    BeginBenchmarkPhase(BenchmarkPhase.BaselineSecondWarmup, "baseline A2 warmup");
                }
                return false;

            case BenchmarkPhase.BaselineSecondWarmup:
                benchmarkStatus = $"baseline A2 warmup {benchmarkPhaseFrame}/{benchmarkWarmupFrames}";
                if (benchmarkPhaseFrame >= benchmarkWarmupFrames)
                {
                    BeginBenchmarkPhase(BenchmarkPhase.BaselineSecond, $"baseline A2 0/{benchmarkSampleFrames}");
                }
                return true;

            case BenchmarkPhase.BaselineSecond:
                benchmarkStatus = $"baseline A2 {benchmarkPhaseFrame}/{benchmarkSampleFrames}";
                if (benchmarkPhaseFrame >= benchmarkSampleFrames)
                {
                    CompleteAutomaticBenchmark(performanceMonitor.CreateSnapshot());
                }
                return true;

            default:
                return false;
        }
    }

    /// <summary>Rejects benchmark samples until the camera matrix remains below measured jitter bounds.</summary>
    /// <returns>Whether the required consecutive stable-frame gate is satisfied.</returns>
    private bool UpdateBenchmarkCameraStability()
    {
        double[] current = api.Render.CameraMatrixOrigin;
        if (current.Length < benchmarkCameraMatrix.Length)
        {
            benchmarkStableFrames = 0;
            benchmarkCameraInitialized = false;
            return false;
        }

        // CameraPos includes render-only head sway/bobbing. The automated
        // scenarios lock the physical entity pose, so use Entity.Pos for the
        // semantic A/B/A gate and retain CameraMatrixOrigin above as the
        // diagnostic of the final rendered view.
        var cameraPosition = api.World.Player.Entity.Pos;
        float cameraYaw = api.Input.MouseYaw;
        float cameraPitch = api.Input.MousePitch;
        bool stable = benchmarkCameraInitialized;
        double maximumDelta = 0.0;
        int maximumDeltaIndex = -1;
        double positionDelta = 0.0;
        double yawDelta = 0.0;
        double pitchDelta = 0.0;
        float previousCameraPitch = benchmarkCameraPitch;
        if (benchmarkCameraInitialized)
        {
            for (int index = 0; index < benchmarkCameraMatrix.Length; index++)
            {
                double delta = Math.Abs(current[index] - benchmarkCameraMatrix[index]);
                if (delta > maximumDelta)
                {
                    maximumDelta = delta;
                    maximumDeltaIndex = index;
                }

            }

            double dx = cameraPosition.X - benchmarkCameraX;
            double dy = cameraPosition.Y - benchmarkCameraY;
            double dz = cameraPosition.Z - benchmarkCameraZ;
            positionDelta = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            yawDelta = Math.Abs(cameraYaw - benchmarkCameraYaw) % GameMath.TWOPI;
            pitchDelta = Math.Abs(cameraPitch - benchmarkCameraPitch) % GameMath.TWOPI;
            yawDelta = Math.Min(yawDelta, GameMath.TWOPI - yawDelta);
            pitchDelta = Math.Min(pitchDelta, GameMath.TWOPI - pitchDelta);
            // MouseYaw/MousePitch are engine input accumulators and can be
            // recanonicalized even while an automated camera lock keeps the
            // physical view fixed. Use the physical entity position as the
            // benchmark gate; keep the input and final render matrix deltas in
            // the diagnostic log above for transparency.
            stable = automatedCameraLock || positionDelta <= 0.001;
        }

        Array.Copy(current, benchmarkCameraMatrix, benchmarkCameraMatrix.Length);
        benchmarkCameraX = cameraPosition.X;
        benchmarkCameraY = cameraPosition.Y;
        benchmarkCameraZ = cameraPosition.Z;
        benchmarkCameraYaw = cameraYaw;
        benchmarkCameraPitch = cameraPitch;
        benchmarkCameraInitialized = true;
        benchmarkStableFrames = stable ? benchmarkStableFrames + 1 : 0;
        benchmarkMatrixDiagnosticFrames++;
        if (maximumDelta > benchmarkMatrixDiagnosticMaximumDelta)
        {
            benchmarkMatrixDiagnosticMaximumDelta = maximumDelta;
            benchmarkMatrixDiagnosticMaximumIndex = maximumDeltaIndex;
        }
        benchmarkPositionDiagnosticMaximumDelta = Math.Max(
            benchmarkPositionDiagnosticMaximumDelta,
            positionDelta);
        benchmarkYawDiagnosticMaximumDelta = Math.Max(
            benchmarkYawDiagnosticMaximumDelta,
            yawDelta);
        if (pitchDelta > benchmarkPitchDiagnosticMaximumDelta)
        {
            benchmarkPitchDiagnosticMaximumDelta = pitchDelta;
            benchmarkPitchDiagnosticFrom = previousCameraPitch;
            benchmarkPitchDiagnosticTo = cameraPitch;
        }

        if (benchmarkMatrixDiagnosticFrames >= 300
            && benchmarkPhase == BenchmarkPhase.Waiting)
        {
            api.Logger.Notification(
                "[VintageRTX] Benchmark camera diagnostic: semantic stable frames={0}, rolling max matrix delta={1:0.000000} at index {2}, position={3:0.000000}, yaw={4:0.000000}, pitch={5:0.000000} ({6:0.000000} -> {7:0.000000}).",
                benchmarkStableFrames,
                benchmarkMatrixDiagnosticMaximumDelta,
                benchmarkMatrixDiagnosticMaximumIndex,
                benchmarkPositionDiagnosticMaximumDelta,
                benchmarkYawDiagnosticMaximumDelta,
                benchmarkPitchDiagnosticMaximumDelta,
                benchmarkPitchDiagnosticFrom,
                benchmarkPitchDiagnosticTo);
            benchmarkMatrixDiagnosticFrames = 0;
            benchmarkMatrixDiagnosticMaximumDelta = 0.0;
            benchmarkMatrixDiagnosticMaximumIndex = -1;
            benchmarkPositionDiagnosticMaximumDelta = 0.0;
            benchmarkYawDiagnosticMaximumDelta = 0.0;
            benchmarkPitchDiagnosticMaximumDelta = 0.0;
            benchmarkPitchDiagnosticFrom = 0.0f;
            benchmarkPitchDiagnosticTo = 0.0f;
        }

        return stable;
    }

    /// <summary>Returns one stage's elapsed Stopwatch ticks and advances its marker without allocation.</summary>
    /// <param name="enabled">Whether the current frame belongs to the measured effect phase.</param>
    /// <param name="marker">Previous timestamp, replaced by the current timestamp when enabled.</param>
    /// <returns>Non-negative elapsed ticks, or zero outside diagnostic sampling.</returns>
    private static long ReadStageTicks(bool enabled, ref long marker)
    {
        if (!enabled)
        {
            return 0L;
        }

        long current = Stopwatch.GetTimestamp();
        long elapsed = Math.Max(0L, current - marker);
        marker = current;
        return elapsed;
    }

    /// <summary>Accumulates one complete effect frame and its per-stage maxima.</summary>
    /// <param name="preparationTicks">Weather and size-dependent resource ticks.</param>
    /// <param name="voxelTicks">Incremental voxel upload ticks.</param>
    /// <param name="gBufferTicks">G-buffer validation ticks.</param>
    /// <param name="liquidTicks">Liquid observation, physics, packing, and upload ticks.</param>
    /// <param name="impactTicks">Impact evidence bookkeeping ticks.</param>
    /// <param name="displayTicks">Final display-pass wall ticks, including driver waits.</param>
    private void RecordBenchmarkCpuDiagnostics(
        long preparationTicks,
        long voxelTicks,
        long gBufferTicks,
        long liquidTicks,
        long impactTicks,
        long displayTicks)
    {
        Span<long> stages = stackalloc long[BenchmarkCpuStageCount]
        {
            preparationTicks,
            voxelTicks,
            gBufferTicks,
            liquidTicks,
            impactTicks,
            displayTicks
        };
        long totalTicks = 0L;
        for (int index = 0; index < stages.Length; index++)
        {
            benchmarkCpuStageTicks[index] += stages[index];
            benchmarkCpuStageMaximumTicks[index] = Math.Max(
                benchmarkCpuStageMaximumTicks[index],
                stages[index]);
            totalTicks += stages[index];
        }

        benchmarkCpuDiagnosticFrames++;
        benchmarkCpuMaximumFrameTicks = Math.Max(benchmarkCpuMaximumFrameTicks, totalTicks);
    }

    /// <summary>Clears effect-only CPU-wall counters before the measured phase begins.</summary>
    private void ResetBenchmarkCpuDiagnostics()
    {
        Array.Clear(benchmarkCpuStageTicks);
        Array.Clear(benchmarkCpuStageMaximumTicks);
        benchmarkCpuDiagnosticFrames = 0;
        benchmarkCpuMaximumFrameTicks = 0L;
    }

    /// <summary>Converts Stopwatch ticks to milliseconds using the platform frequency.</summary>
    /// <param name="ticks">Non-negative elapsed Stopwatch ticks.</param>
    /// <returns>Elapsed milliseconds.</returns>
    private static double StopwatchTicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Starts one benchmark phase with zeroed statistics and phase-local frame count.</summary>
    /// <param name="phase">Next measurement/warm-up mode.</param>
    /// <param name="status">Human-readable progress text.</param>
    private void BeginBenchmarkPhase(BenchmarkPhase phase, string status)
    {
        benchmarkPhase = phase;
        benchmarkPhaseFrame = 0;
        performanceMonitor.ResetStatistics();
        benchmarkStatus = status;
        if (phase == BenchmarkPhase.Effect)
        {
            ResetBenchmarkCpuDiagnostics();
            reflectionSourceCapture.BeginCpuDiagnostics();
        }
        else if (phase == BenchmarkPhase.BaselineSecondWarmup)
        {
            reflectionSourceCapture.EndAndLogCpuDiagnostics();
        }
    }

    /// <summary>Averages bracketed baselines and reports absolute FPS, 1% low, jitter, and GPU cost.</summary>
    /// <param name="baselineSecond">Post-effect baseline used to reduce thermal/time drift bias.</param>
    private void CompleteAutomaticBenchmark(PerformanceSnapshot baselineSecond)
    {
        PerformanceSnapshot baseline = new(
            Math.Min(benchmarkBaselineFirst.FrameCount, baselineSecond.FrameCount),
            (benchmarkBaselineFirst.AverageFps + baselineSecond.AverageFps) * 0.5,
            (benchmarkBaselineFirst.OnePercentLowFps + baselineSecond.OnePercentLowFps) * 0.5,
            (benchmarkBaselineFirst.JitterMilliseconds + baselineSecond.JitterMilliseconds) * 0.5,
            0.0);
        double fpsCost = baseline.AverageFps - benchmarkEffect.AverageFps;
        double onePercentCost = baseline.OnePercentLowFps - benchmarkEffect.OnePercentLowFps;
        benchmarkStatus = $"done: base {baseline.AverageFps:0.0}, effect {benchmarkEffect.AverageFps:0.0}";
        benchmarkPhase = BenchmarkPhase.Complete;
        api.Logger.Notification(
            "[VintageRTX] Stabilized A/B/A result | baseline: {0} | effect: {1} | delta fps={2:+0.0;-0.0;0.0}, delta 1%low={3:+0.0;-0.0;0.0}.",
            baseline,
            benchmarkEffect,
            fpsCost,
            onePercentCost);
        if (benchmarkCpuDiagnosticFrames > 0)
        {
            double divisor = benchmarkCpuDiagnosticFrames;
            api.Logger.Notification(
                "[VintageRTX] Effect CPU-wall stages | frames={0}, avg ms prep={1:0.000}, voxel={2:0.000}, gbuffer={3:0.000}, liquid={4:0.000}, impact={5:0.000}, display={6:0.000}; max ms liquid={7:0.000}, display={8:0.000}, measured-frame={9:0.000}.",
                benchmarkCpuDiagnosticFrames,
                StopwatchTicksToMilliseconds(benchmarkCpuStageTicks[0]) / divisor,
                StopwatchTicksToMilliseconds(benchmarkCpuStageTicks[1]) / divisor,
                StopwatchTicksToMilliseconds(benchmarkCpuStageTicks[2]) / divisor,
                StopwatchTicksToMilliseconds(benchmarkCpuStageTicks[3]) / divisor,
                StopwatchTicksToMilliseconds(benchmarkCpuStageTicks[4]) / divisor,
                StopwatchTicksToMilliseconds(benchmarkCpuStageTicks[5]) / divisor,
                StopwatchTicksToMilliseconds(benchmarkCpuStageMaximumTicks[3]),
                StopwatchTicksToMilliseconds(benchmarkCpuStageMaximumTicks[5]),
                StopwatchTicksToMilliseconds(benchmarkCpuMaximumFrameTicks));
        }
    }

    /// <summary>Clears sticky render/G-buffer diagnostics and invalidates temporal history for a safe retry.</summary>
    public void ResetFault()
    {
        faulted = false;
        failureReason = null;
        gBufferAvailabilityLogged = false;
        temporalHistoryValid = false;
        shadowHistoryValid = false;
    }

    /// <summary>Compiles a replacement program in memory and preserves the previous program on failure.</summary>
    /// <returns>Whether the GLSL program compiled and linked successfully.</returns>
    public bool ReloadShader()
    {
        try
        {
            shader = CreateShader();
            faulted = false;
            failureReason = null;
            temporalHistoryValid = false;
            shadowHistoryValid = false;
            api.Logger.Notification("[VintageRTX] In-memory shader recompiled.");
            return true;
        }
        catch (Exception exception)
        {
            faulted = true;
            failureReason = exception.Message;
            api.Logger.Error("[VintageRTX] Shader reload failed: {0}", exception);
            return false;
        }
    }

    /// <summary>
    /// Prefers the immutable late-opaque snapshot so reflected geometry cannot see the local
    /// first-person draw, then falls back to borrowed live Primary attachments when capture failed.
    /// </summary>
    /// <param name="gBuffer">Resolved clean or fallback handles, or default when unavailable.</param>
    private void ResolveGBuffer(out GameGBuffer gBuffer)
    {
        int frameWidth = api.Render.FrameWidth;
        int frameHeight = api.Render.FrameHeight;
        bool liveAvailable = GameGBuffer.TryResolve(
            api.Render,
            out GameGBuffer liveGBuffer,
            out string fallbackStatus);
        if (liveAvailable
            && reflectionSourceCapture.TryGetGBuffer(
                frameWidth,
                frameHeight,
                in liveGBuffer,
                out gBuffer))
        {
            gBufferAvailable = true;
            gBufferStatus = "clean late-opaque material/position snapshot with live normal";
        }
        else
        {
            gBufferAvailable = liveAvailable;
            gBuffer = liveGBuffer;
            gBufferStatus = gBufferAvailable
                ? $"live Primary fallback ({fallbackStatus})"
                : fallbackStatus;
        }

        if (gBufferAvailabilityLogged)
        {
            return;
        }

        if (gBufferAvailable)
        {
            api.Logger.Notification(
                "[VintageRTX] Reflection G-buffer material, normal, and position attachments are ready: {0}.",
                gBufferStatus);
        }
        else
        {
            api.Logger.Warning(
                "[VintageRTX] Screen-space lighting disabled; {0}. The display pass remains active.",
                gBufferStatus);
        }

        gBufferAvailabilityLogged = true;
    }

    /// <summary>Creates thread-bound GL resources and reallocates size-dependent textures on viewport or tier changes.</summary>
    /// <param name="width">Current framebuffer width in pixels.</param>
    /// <param name="height">Current framebuffer height in pixels.</param>
    /// <param name="qualityLevel">Current adaptive tier controlling shadow carrier resolution.</param>
    private void EnsureResources(int width, int height, int qualityLevel)
    {
        if (shader is null || shader.Disposed)
        {
            shader = CreateShader();
        }

        if (fullscreenVertexArray == 0)
        {
            fullscreenVertexArray = GL.GenVertexArray();
        }

        if (sceneCopyTexture == 0)
        {
            sceneCopyTexture = GL.GenTexture();
        }
        if (temporalHistoryTextures[0] == 0)
        {
            for (int index = 0; index < temporalHistoryTextures.Length; index++)
            {
                temporalHistoryTextures[index] = GL.GenTexture();
                temporalFramebuffers[index] = GL.GenFramebuffer();
            }
        }
        if (shadowCurrentPointATexture == 0)
        {
            shadowCurrentPointATexture = GL.GenTexture();
            shadowCurrentPointBTexture = GL.GenTexture();
            shadowCurrentSunTexture = GL.GenTexture();
            shadowCurrentFramebuffer = GL.GenFramebuffer();
            for (int index = 0; index < shadowHistoryPointATextures.Length; index++)
            {
                shadowHistoryPointATextures[index] = GL.GenTexture();
                shadowHistoryPointBTextures[index] = GL.GenTexture();
                shadowHistorySunTextures[index] = GL.GenTexture();
                shadowHistoryFramebuffers[index] = GL.GenFramebuffer();
            }
        }

        int shadowDivisor = ShadowResolutionDivisor(qualityLevel);
        int expectedShadowWidth = Math.Max(1, (width + shadowDivisor - 1) / shadowDivisor);
        int expectedShadowHeight = Math.Max(1, (height + shadowDivisor - 1) / shadowDivisor);
        if (textureWidth != width
            || textureHeight != height
            || shadowTextureWidth != expectedShadowWidth
            || shadowTextureHeight != expectedShadowHeight)
        {
            ResizeSceneTexture(width, height, shadowDivisor);
        }

        if (!initialized)
        {
            initialized = true;
            api.Logger.Notification(
                "[VintageRTX] Display pass ready on {0} / OpenGL {1}",
                NormalizeGlLabel(GL.GetString(StringName.Renderer), "unknown GPU"),
                NormalizeGlLabel(GL.GetString(StringName.Version), "unknown version"));
        }
    }

    /// <summary>Normalizes an optional OpenGL identity without hiding unavailable driver metadata.</summary>
    /// <param name="value">Driver-supplied renderer or version text.</param>
    /// <param name="fallback">Stable diagnostic text used when the driver returns null.</param>
    /// <returns>The supplied non-null value or its fallback.</returns>
    internal static string NormalizeGlLabel(string? value, string fallback) => value ?? fallback;

    /// <summary>Loads, compiles, and links the asset-backed GLSL 3.30 program.</summary>
    /// <returns>Owned Vintage Story shader program.</returns>
    private IShaderProgram CreateShader()
    {
        if (!api.Shader.IsGLSLVersionSupported("330"))
        {
            throw new NotSupportedException("VintageRTX requires GLSL 3.30 or newer.");
        }

        DisplayShaderProgramSource source = DisplayShaderSource.Load(api.Assets);
        IShaderProgram program = api.Shader.NewShaderProgram();
        program.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
        program.VertexShader.Code = source.Vertex;
        program.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
        program.FragmentShader.Code = source.Fragment;

        int passId = api.Shader.RegisterMemoryShaderProgram(ShaderName, program);
        bool compiled = program.Compile();
        if (passId < 0 || !compiled || program.LoadError || program.Disposed)
        {
            throw new InvalidOperationException(
                $"The asset-backed display shader did not compile "
                + $"({DisplayShaderSource.VertexAssetCode}, {DisplayShaderSource.FragmentAssetCode}).");
        }

        voxelSamplerLocation = GL.GetUniformLocation(program.ProgramId, "voxelVolume");
        voxelOccupancySamplerLocation = GL.GetUniformLocation(program.ProgramId, "voxelOccupancy");
        voxelLightCasterSamplerLocation = GL.GetUniformLocation(program.ProgramId, "voxelLightCasterMasks");
        voxelFluidSurfaceSamplerLocation = GL.GetUniformLocation(program.ProgramId, "voxelFluidSurface");
        voxelLiquidMetadataSamplerLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelLiquidMetadata");
        liquidOpticalProfilesSamplerLocation = GL.GetUniformLocation(
            program.ProgramId,
            "liquidOpticalProfiles");
        dynamicLiquidSurfaceSamplerLocation = GL.GetUniformLocation(
            program.ProgramId,
            LiquidSurfaceGpuContract.SamplerUniformName);
        voxelIrradianceSamplerLocation = GL.GetUniformLocation(program.ProgramId, "voxelIrradiance");
        voxelIrradianceDirectionSamplerLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelIrradianceDirection");
        voxelSunOccupancySamplerLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelSunOccupancy");
        voxelRainSurfaceSamplerLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelRainSurface");
        voxelLightPositionLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelLightPositionIntensity[0]");
        voxelLightColorLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelLightColorRadius[0]");
        voxelLightPhotometryLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelLightPhotometry[0]");
        voxelLightCasterLayerLocation = GL.GetUniformLocation(
            program.ProgramId,
            "voxelLightCasterLayer[0]");
        liquidImpactOriginLocation = GL.GetUniformLocation(
            program.ProgramId,
            "liquidImpactOriginAgeAmplitude[0]");
        liquidImpactMaterialLocation = GL.GetUniformLocation(
            program.ProgramId,
            "liquidImpactMaterial[0]");
        liquidImpactMotionLocation = GL.GetUniformLocation(
            program.ProgramId,
            "liquidImpactMotionWavelength[0]");
        liquidImpactEnergyLocation = GL.GetUniformLocation(
            program.ProgramId,
            "liquidImpactEnergyLedger[0]");
        liquidImpactSplashLocation = GL.GetUniformLocation(
            program.ProgramId,
            "liquidImpactSplash[0]");
        ValidateVoxelUniformLocations(
            voxelSamplerLocation,
            voxelOccupancySamplerLocation,
            voxelLightCasterSamplerLocation,
            voxelFluidSurfaceSamplerLocation,
            voxelIrradianceSamplerLocation,
            voxelIrradianceDirectionSamplerLocation,
            voxelSunOccupancySamplerLocation,
            voxelRainSurfaceSamplerLocation,
            dynamicLiquidSurfaceSamplerLocation,
            voxelLightPositionLocation,
            voxelLightColorLocation,
            voxelLightPhotometryLocation,
            voxelLightCasterLayerLocation);
        ValidateImpactUniformLocations(
            liquidImpactOriginLocation,
            liquidImpactMaterialLocation,
            liquidImpactMotionLocation,
            liquidImpactEnergyLocation,
            liquidImpactSplashLocation);

        return program;
    }

    /// <summary>Rejects a linked display program when a required voxel uniform was optimized away.</summary>
    /// <param name="locations">Required locations returned by the active OpenGL program.</param>
    internal static void ValidateVoxelUniformLocations(params int[] locations)
    {
        foreach (int location in locations)
        {
            if (location < 0)
            {
                throw new InvalidOperationException("The voxel lighting uniforms were not linked.");
            }
        }
    }

    /// <summary>Recognizes a verified runtime-test request for an immediately dry reference surface.</summary>
    /// <param name="runId">Non-empty isolated runtime run identifier.</param>
    /// <param name="clearWeather">One when the scenario explicitly requires clear weather.</param>
    /// <param name="environmentReady">One after time, weather, camera and scene setup have converged.</param>
    /// <param name="wetnessTarget">Current physical precipitation-derived equilibrium.</param>
    /// <returns><see langword="true"/> only for a ready, clear and actually dry test world.</returns>
    internal static bool ShouldSnapDeterministicClearWeather(
        string? runId,
        string? clearWeather,
        string? environmentReady,
        float wetnessTarget) =>
        !string.IsNullOrWhiteSpace(runId)
        && string.Equals(clearWeather, "1", StringComparison.Ordinal)
        && string.Equals(environmentReady, "1", StringComparison.Ordinal)
        && float.IsFinite(wetnessTarget)
        && wetnessTarget <= 0.01f;

    /// <summary>Rejects a linked display program when a required impact-array uniform was optimized away.</summary>
    /// <param name="locations">Impact-array locations returned by the active OpenGL program.</param>
    internal static void ValidateImpactUniformLocations(params int[] locations)
    {
        foreach (int location in locations)
        {
            if (location < 0)
            {
                throw new InvalidOperationException("The impact packet uniforms were not linked.");
            }
        }
    }

    /// <summary>Reallocates full-resolution color and tier-scaled shadow textures and invalidates temporal data.</summary>
    /// <param name="width">New width in pixels.</param>
    /// <param name="height">New height in pixels.</param>
    /// <param name="shadowDivisor">Full-frame divisor used by point and sun visibility carriers.</param>
    private void ResizeSceneTexture(int width, int height, int shadowDivisor)
    {
        int previousWidth = textureWidth;
        int previousHeight = textureHeight;
        AllocateFrameTexture(sceneCopyTexture, width, height);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out int previousReadFramebuffer);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int previousDrawFramebuffer);
        for (int index = 0; index < temporalHistoryTextures.Length; index++)
        {
            AllocateFrameTexture(temporalHistoryTextures[index], width, height);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, temporalFramebuffers[index]);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                temporalHistoryTextures[index],
                0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)
                != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException("The temporal accumulation framebuffer is incomplete.");
            }
        }
        int shadowWidth = Math.Max(1, (width + shadowDivisor - 1) / shadowDivisor);
        int shadowHeight = Math.Max(1, (height + shadowDivisor - 1) / shadowDivisor);
        AllocateShadowPointTexture(shadowCurrentPointATexture, shadowWidth, shadowHeight);
        AllocateShadowPointTexture(shadowCurrentPointBTexture, shadowWidth, shadowHeight);
        AllocateShadowSunTexture(shadowCurrentSunTexture, shadowWidth, shadowHeight);
        AttachShadowFramebuffer(
            shadowCurrentFramebuffer,
            shadowCurrentPointATexture,
            shadowCurrentPointBTexture,
            shadowCurrentSunTexture,
            "The current shadow framebuffer is incomplete.");
        for (int index = 0; index < shadowHistoryPointATextures.Length; index++)
        {
            AllocateShadowPointTexture(shadowHistoryPointATextures[index], shadowWidth, shadowHeight);
            AllocateShadowPointTexture(shadowHistoryPointBTextures[index], shadowWidth, shadowHeight);
            AllocateShadowSunTexture(shadowHistorySunTextures[index], shadowWidth, shadowHeight);
            AttachShadowFramebuffer(
                shadowHistoryFramebuffers[index],
                shadowHistoryPointATextures[index],
                shadowHistoryPointBTextures[index],
                shadowHistorySunTextures[index],
                "A shadow history framebuffer is incomplete.");
        }
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousReadFramebuffer);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousDrawFramebuffer);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        textureWidth = width;
        textureHeight = height;
        shadowTextureWidth = shadowWidth;
        shadowTextureHeight = shadowHeight;
        temporalHistoryIndex = 0;
        temporalHistoryValid = false;
        shadowHistoryIndex = 0;
        shadowHistoryValid = false;
        temporalCameraInitialized = false;
        api.Logger.Notification(
            "[VintageRTX] Size-dependent GPU resources resized: {0}x{1} -> {2}x{3}; 8-channel point plus solar shadow visibility={4}x{5}; color and shadow history reset.",
            previousWidth,
            previousHeight,
            width,
            height,
            shadowWidth,
            shadowHeight);
    }

    /// <summary>Defines a clamp-to-edge RGBA16F carrier/history texture at exact viewport size.</summary>
    /// <param name="texture">Existing owned OpenGL texture handle.</param>
    /// <param name="width">Allocation width in pixels.</param>
    /// <param name="height">Allocation height in pixels.</param>
    private static void AllocateFrameTexture(int texture, int width, int height)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba16f,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            IntPtr.Zero);
    }

    /// <summary>Defines one linearly sampled RGBA16F bank carrying four independent point sources.</summary>
    /// <param name="texture">Existing owned OpenGL texture handle.</param>
    /// <param name="width">Half-resolution allocation width in shadow pixels.</param>
    /// <param name="height">Half-resolution allocation height in shadow pixels.</param>
    private static void AllocateShadowPointTexture(int texture, int width, int height)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba16f,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            IntPtr.Zero);
    }

    /// <summary>
    /// Returns the coherent spatial divisor used for independent shadow visibility carriers.
    /// Performance resolves all sources together at one-third linear resolution; higher tiers keep
    /// one-half resolution. A tier change performs one explicit history reset instead of alternating
    /// pixel phases, so the resulting shadow field remains temporally uniform.
    /// </summary>
    /// <param name="qualityLevel">Current adaptive tier retained for the renderer contract.</param>
    /// <returns>Three for Performance (tier two), otherwise two.</returns>
    internal static int ShadowResolutionDivisor(int qualityLevel) => qualityLevel == 2 ? 3 : 2;

    /// <summary>Defines one linearly sampled R16F solar-visibility texture.</summary>
    /// <param name="texture">Existing owned OpenGL texture handle.</param>
    /// <param name="width">Half-resolution allocation width in shadow pixels.</param>
    /// <param name="height">Half-resolution allocation height in shadow pixels.</param>
    private static void AllocateShadowSunTexture(int texture, int width, int height)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.R16f,
            width,
            height,
            0,
            PixelFormat.Red,
            PixelType.HalfFloat,
            IntPtr.Zero);
    }

    /// <summary>Attaches two four-source visibility banks and one independent solar bank.</summary>
    /// <param name="framebuffer">Owned framebuffer receiving the attachment.</param>
    /// <param name="pointA">Owned RGBA16F slots 0..3.</param>
    /// <param name="pointB">Owned RGBA16F slots 4..7.</param>
    /// <param name="sun">Owned R16F direct-sun visibility.</param>
    /// <param name="failureMessage">Diagnostic used if the driver rejects the framebuffer.</param>
    private static void AttachShadowFramebuffer(
        int framebuffer,
        int pointA,
        int pointB,
        int sun,
        string failureMessage)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            pointA,
            0);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D,
            pointB,
            0);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment2,
            TextureTarget.Texture2D,
            sun,
            0);
        GL.DrawBuffers(ShadowDrawBuffers.Length, ShadowDrawBuffers);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)
            != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    /// <summary>
    /// Atomically consumes a full CPU scene snapshot, creates missing 3D/2D textures, and uploads all
    /// geometry, material, radiance, occupancy, liquid metadata, and optical-profile payloads.
    /// </summary>
    private void UpdateVoxelTexture()
    {
        if (voxelScene.TryConsumeUpload(out VoxelSceneSnapshot newestSnapshot))
        {
            // A later generation supersedes an earlier deferred one without
            // mutating its arrays. VoxelScene swaps immutable ready buffers.
            pendingVoxelSnapshot = newestSnapshot;
            pendingVoxelSnapshotAvailable = true;
        }

        if (!TrySelectSettledVoxelSnapshot(
                ref pendingVoxelSnapshot,
                ref pendingVoxelSnapshotAvailable,
                voxelScene.CanPublishRetainedSnapshot,
                out VoxelSceneSnapshot snapshot))
        {
            // A recenter publishes one provisional CPU snapshot while nearby
            // chunks are still arriving, followed by a delayed confirmation
            // scan. Retain rather than discard it: if an incremental edit was
            // queued on the consume frame, the same snapshot becomes publishable
            // as soon as that queue drains even when no fourth generation follows.
            if (!pendingVoxelSnapshotAvailable)
            {
                UpdateVoxelBlockTextures();
            }
            return;
        }

        if (voxelTexture == 0)
        {
            voxelTexture = GL.GenTexture();
        }
        if (voxelOccupancyTexture == 0)
        {
            voxelOccupancyTexture = GL.GenTexture();
        }
        if (voxelLightCasterTexture == 0)
        {
            voxelLightCasterTexture = GL.GenTexture();
        }
        if (voxelFluidSurfaceTexture == 0)
        {
            voxelFluidSurfaceTexture = GL.GenTexture();
        }
        if (voxelLiquidMetadataTexture == 0)
        {
            voxelLiquidMetadataTexture = GL.GenTexture();
        }
        if (liquidOpticalProfilesTexture == 0)
        {
            liquidOpticalProfilesTexture = GL.GenTexture();
        }
        if (voxelIrradianceTexture == 0)
        {
            voxelIrradianceTexture = GL.GenTexture();
        }
        if (voxelIrradianceDirectionTexture == 0)
        {
            voxelIrradianceDirectionTexture = GL.GenTexture();
        }
        if (voxelSunOccupancyTexture == 0)
        {
            voxelSunOccupancyTexture = GL.GenTexture();
        }
        if (voxelRainSurfaceTexture == 0)
        {
            voxelRainSurfaceTexture = GL.GenTexture();
        }

        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture3D, voxelTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.Rgba8,
            snapshot.Width,
            snapshot.Height,
            snapshot.Depth,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            snapshot.Voxels);
        // voxelVolume uses nearest base-level sampling. Generating an unused
        // mip chain here only stalls the render thread during scene swaps.
        GL.BindTexture(TextureTarget.Texture3D, 0);

        GL.ActiveTexture(TextureUnit.Texture11);
        GL.BindTexture(TextureTarget.Texture3D, voxelSunOccupancyTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.Rg32ui,
            snapshot.SunOccupancyWidth,
            snapshot.SunOccupancyHeight,
            snapshot.SunOccupancyDepth,
            0,
            PixelFormat.RgInteger,
            PixelType.UnsignedInt,
            snapshot.SunOccupancy);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        GL.ActiveTexture(TextureUnit.Texture12);
        GL.BindTexture(TextureTarget.Texture2D, voxelRainSurfaceTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.R32f,
            snapshot.RainSurfaceWidth,
            snapshot.RainSurfaceDepth,
            0,
            PixelFormat.Red,
            PixelType.Float,
            snapshot.RainSurface);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        GL.ActiveTexture(TextureUnit.Texture13);
        GL.BindTexture(TextureTarget.Texture3D, voxelLiquidMetadataTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.Rgba8,
            snapshot.Width,
            snapshot.Height,
            snapshot.Depth,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            snapshot.LiquidMetadata);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2D, liquidOpticalProfilesTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba32f,
            LiquidOpticalRegistry.LookupWidth,
            LiquidOpticalRegistry.LookupHeight,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            snapshot.LiquidOpticalProfileLookup);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        GL.ActiveTexture(TextureUnit.Texture7);
        GL.BindTexture(TextureTarget.Texture2D, voxelFluidSurfaceTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba8,
            snapshot.FluidSurfaceWidth,
            snapshot.FluidSurfaceDepth,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            snapshot.FluidSurface);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        GL.ActiveTexture(TextureUnit.Texture9);
        GL.BindTexture(TextureTarget.Texture3D, voxelIrradianceTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.Rgb8,
            snapshot.Width,
            snapshot.Height,
            snapshot.Depth,
            0,
            PixelFormat.Rgb,
            PixelType.UnsignedByte,
            snapshot.Irradiance);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        GL.ActiveTexture(TextureUnit.Texture10);
        GL.BindTexture(TextureTarget.Texture3D, voxelIrradianceDirectionTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.Rgb8,
            snapshot.Width,
            snapshot.Height,
            snapshot.Depth,
            0,
            PixelFormat.Rgb,
            PixelType.UnsignedByte,
            snapshot.IrradianceDirection);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.Texture3D, voxelOccupancyTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.R8,
            snapshot.Width * VoxelScene.OccupancyScale,
            snapshot.Height * VoxelScene.OccupancyScale,
            snapshot.Depth * VoxelScene.OccupancyScale,
            0,
            PixelFormat.Red,
            PixelType.UnsignedByte,
            snapshot.Occupancy);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        byte[] lightCasterVolume = new byte[
            VoxelScene.LightCasterVoxelCount * VoxelScene.MaximumLightCount];
        int casterLightCount = Math.Min(snapshot.Lights.Length, VoxelScene.MaximumLightCount);
        for (int lightIndex = 0; lightIndex < casterLightCount; lightIndex++)
        {
            byte[] source = snapshot.Lights[lightIndex].CasterMask;
            if (source.Length != VoxelScene.LightCasterVoxelCount)
            {
                continue;
            }

            Array.Copy(
                source,
                0,
                lightCasterVolume,
                lightIndex * VoxelScene.LightCasterVoxelCount,
                source.Length);
        }

        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture3D, voxelLightCasterTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.R8,
            VoxelScene.LightCasterScale,
            VoxelScene.LightCasterScale,
            VoxelScene.LightCasterScale * VoxelScene.MaximumLightCount,
            0,
            PixelFormat.Red,
            PixelType.UnsignedByte,
            lightCasterVolume);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        voxelSnapshot = snapshot;
        voxelTextureReady = true;
        temporalHistoryValid = false;
        shadowHistoryValid = false;
        api.Render.CheckGlError("VintageRTX voxel scene upload");
    }

    /// <summary>
    /// Selects a retained complete voxel snapshot only after all asynchronous
    /// replacement scene queues have settled. A provisional snapshot remains pending rather
    /// than being lost when no newer generation follows it.
    /// </summary>
    /// <param name="pendingSnapshot">Newest immutable CPU snapshot.</param>
    /// <param name="pendingAvailable">Whether a snapshot is retained.</param>
    /// <param name="generationStable">Whether no rebuild or unprocessed edit can replace it.</param>
    /// <param name="selectedSnapshot">Snapshot selected for one GPU upload.</param>
    /// <returns><see langword="true"/> when an upload may proceed.</returns>
    internal static bool TrySelectSettledVoxelSnapshot(
        ref VoxelSceneSnapshot pendingSnapshot,
        ref bool pendingAvailable,
        bool generationStable,
        out VoxelSceneSnapshot selectedSnapshot)
    {
        if (!pendingAvailable || !generationStable)
        {
            selectedSnapshot = default;
            return false;
        }

        selectedSnapshot = pendingSnapshot;
        pendingAvailable = false;
        return true;
    }

    /// <summary>Applies bounded dirty-region subuploads after the initial full voxel snapshot.</summary>
    private void UpdateVoxelBlockTextures()
    {
        if (!voxelTextureReady)
        {
            return;
        }

        bool hasBlockUpdates = voxelScene.TryConsumeBlockUpdates(
            out VoxelSceneBlockUpdate[] updates);
        bool hasFluidSurfaceUpdates = voxelScene.TryConsumeFluidSurfaceUpdates(
            out VoxelFluidSurfaceUpdate[] fluidSurfaceUpdates);
        bool hasSunUpdates = voxelScene.TryConsumeSunOccupancyUpdates(
            out VoxelSunOccupancyUpdate[] sunUpdates);
        bool hasRainSurfaceUpdates = voxelScene.TryConsumeRainSurfaceUpdates(
            out VoxelRainSurfaceUpdate[] rainSurfaceUpdates);
        if (!hasBlockUpdates
            && !hasFluidSurfaceUpdates
            && !hasSunUpdates
            && !hasRainSurfaceUpdates)
        {
            return;
        }

        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        foreach (VoxelSceneBlockUpdate update in updates)
        {
            GL.ActiveTexture(TextureUnit.Texture3);
            GL.BindTexture(TextureTarget.Texture3D, voxelTexture);
            GL.TexSubImage3D(
                TextureTarget.Texture3D,
                0,
                update.LocalX,
                update.LocalY,
                update.LocalZ,
                1,
                1,
                1,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                update.Material);

            GL.ActiveTexture(TextureUnit.Texture4);
            GL.BindTexture(TextureTarget.Texture3D, voxelOccupancyTexture);
            GL.TexSubImage3D(
                TextureTarget.Texture3D,
                0,
                update.LocalX * VoxelScene.OccupancyScale,
                update.LocalY * VoxelScene.OccupancyScale,
                update.LocalZ * VoxelScene.OccupancyScale,
                VoxelScene.OccupancyScale,
                VoxelScene.OccupancyScale,
                VoxelScene.OccupancyScale,
                PixelFormat.Red,
                PixelType.UnsignedByte,
                update.Occupancy);

            GL.ActiveTexture(TextureUnit.Texture13);
            GL.BindTexture(TextureTarget.Texture3D, voxelLiquidMetadataTexture);
            GL.TexSubImage3D(
                TextureTarget.Texture3D,
                0,
                update.LocalX,
                update.LocalY,
                update.LocalZ,
                1,
                1,
                1,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                update.LiquidMetadata);
        }

        foreach (VoxelFluidSurfaceUpdate update in fluidSurfaceUpdates)
        {
            GL.ActiveTexture(TextureUnit.Texture7);
            GL.BindTexture(TextureTarget.Texture2D, voxelFluidSurfaceTexture);
            GL.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                update.LocalX,
                update.LocalZ,
                1,
                1,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                update.FluidSurface);
        }

        foreach (VoxelSunOccupancyUpdate update in sunUpdates)
        {
            singleSunMaskTextureUpload[0] = update.Occupancy;
            GL.ActiveTexture(TextureUnit.Texture11);
            GL.BindTexture(TextureTarget.Texture3D, voxelSunOccupancyTexture);
            GL.TexSubImage3D(
                TextureTarget.Texture3D,
                0,
                update.LocalX,
                update.LocalY,
                update.LocalZ,
                1,
                1,
                1,
                PixelFormat.RgInteger,
                PixelType.UnsignedInt,
                singleSunMaskTextureUpload);
        }

        foreach (VoxelRainSurfaceUpdate update in rainSurfaceUpdates)
        {
            singleFloatTextureUpload[0] = update.Height;
            GL.ActiveTexture(TextureUnit.Texture12);
            GL.BindTexture(TextureTarget.Texture2D, voxelRainSurfaceTexture);
            GL.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                update.LocalX,
                update.LocalZ,
                1,
                1,
                PixelFormat.Red,
                PixelType.Float,
                singleFloatTextureUpload);
        }

        if (hasBlockUpdates)
        {
            // All material consumers use nearest base-level sampling. Avoid a
            // full 3D mip rebuild for each small batch of world block changes.
            GL.ActiveTexture(TextureUnit.Texture3);
            GL.BindTexture(TextureTarget.Texture3D, voxelTexture);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        temporalHistoryValid = false;
        shadowHistoryValid = false;
        api.Render.CheckGlError("VintageRTX incremental voxel update");
    }

    /// <summary>
    /// Copies the raster carrier, binds the transport ABI, resolves half-resolution point/sun
    /// visibility, draws the final full-screen triangle, rotates temporal histories, and restores
    /// captured GL state even when an exception occurs.
    /// </summary>
    /// <param name="config">Normalized render controls for this frame.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <param name="gBuffer">Validated borrowed glow, normal, and position textures.</param>
    /// <param name="effectiveDebugView">Latched configuration or capture-specific diagnostic view.</param>
    /// <param name="captureFrame">Whether adaptive quality must remain stable for evidence capture.</param>
    private void RenderDisplayPass(
        VintageRtxConfig config,
        int width,
        int height,
        GameGBuffer gBuffer,
        VintageRtxDebugView effectiveDebugView,
        bool captureFrame)
    {
        GlState state = GlState.Capture();
        bool shaderActive = false;
        bool gpuMeasurementActive = performanceMonitor.TryBeginGpuMeasurement();

        try
        {
            if (!DisplayColorPipelineContract.TryResolveLuma(
                    api.Render.FrameBuffers,
                    width,
                    height,
                    out _,
                    out int sourceColorTexture))
            {
                throw new InvalidOperationException(
                    "Vintage Story's exact-size pre-final Luma attachment is unavailable.");
            }

            CopyCameraMatrices();
            bool temporalCameraStable = UpdateTemporalCameraStability();
            bool entityMirrorReady = RenderEntityMirror(
                config,
                width,
                height,
                captureFrame && effectiveDebugView == VintageRtxDebugView.EntityMirror);

            lumaBridge.BindTarget(width, height);
            int temporalWriteIndex = temporalHistoryIndex ^ 1;
            bool renderToTemporalHistory = effectiveDebugView == VintageRtxDebugView.Final;
            if (sourceColorTexture == lumaBridge.ColorTextureId
                || sourceColorTexture == temporalHistoryTextures[temporalWriteIndex])
            {
                throw new InvalidOperationException(
                    "The pre-final Luma source aliases an owned VintageRTX write target.");
            }
            if (!displaySourceBorrowLogged)
            {
                displaySourceBorrowLogged = true;
                api.Logger.Notification(
                    "[VintageRTX] Pre-final transport borrows the official Luma RGB carrier; final.fsh remains engine-owned.");
            }

            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.ScissorTest);
            GL.ColorMask(true, true, true, true);

            shader!.Use();
            shaderActive = true;
            shader.BindTexture2D("sourceColor", sourceColorTexture, 0);
            NativeSunShadowDepthMaps nativeShadowMaps = ResolveNativeSunShadowDepthMaps(
                api.Render.FrameBuffers);
            bool nativeShadowFarReady = nativeShadowMatrixFarReady
                && nativeShadowMaps.FarTextureId > 0;
            bool nativeShadowNearReady = nativeShadowMatrixNearReady
                && nativeShadowMaps.NearTextureId > 0;
            if (!nativeSunShadowContractLogged && nativeShadowFarReady)
            {
                nativeSunShadowContractLogged = true;
                api.Logger.Notification(
                    "[VintageRTX] Native solar shadow detail active: far-depth={0}, near-depth={1}; alpha-tested and wind-deformed casters refine the independent 96 m voxel trace.",
                    nativeShadowMaps.FarTextureId,
                    nativeShadowNearReady ? nativeShadowMaps.NearTextureId : 0);
            }
            shader.BindTexture2D(
                "nativeShadowMapFar",
                nativeShadowFarReady ? nativeShadowMaps.FarTextureId : 0,
                NativeShadowFarTextureUnit);
            shader.BindTexture2D(
                "nativeShadowMapNear",
                nativeShadowNearReady ? nativeShadowMaps.NearTextureId : 0,
                NativeShadowNearTextureUnit);
            shader.Uniform("nativeShadowFarEnabled", nativeShadowFarReady ? 1 : 0);
            shader.Uniform("nativeShadowNearEnabled", nativeShadowNearReady ? 1 : 0);
            shader.UniformMatrix("nativeShadowMatrixFar", nativeShadowMatrixFar);
            shader.UniformMatrix("nativeShadowMatrixNear", nativeShadowMatrixNear);
            ResolveRenderFloatingOrigin(
                out double nativeShadowFloatingOriginX,
                out double nativeShadowFloatingOriginY,
                out double nativeShadowFloatingOriginZ);
            shader.Uniform(
                "nativeShadowReferenceOffsetFar",
                (float)(nativeShadowReferenceFar.X - nativeShadowFloatingOriginX),
                (float)(nativeShadowReferenceFar.Y - nativeShadowFloatingOriginY),
                (float)(nativeShadowReferenceFar.Z - nativeShadowFloatingOriginZ));
            shader.Uniform(
                "nativeShadowReferenceOffsetNear",
                (float)(nativeShadowReferenceNear.X - nativeShadowFloatingOriginX),
                (float)(nativeShadowReferenceNear.Y - nativeShadowFloatingOriginY),
                (float)(nativeShadowReferenceNear.Z - nativeShadowFloatingOriginZ));
            shader.Uniform("nativeShadowRangeFar", nativeShadowRangeFar);
            shader.Uniform("nativeShadowRangeNear", nativeShadowRangeNear);
            shader.BindTexture2D(
                "reflectionSourceColor",
                reflectionSourceCapture.IsReady(width, height)
                    ? reflectionSourceCapture.TextureId
                    : sourceColorTexture,
                17);
            shader.BindTexture2D(
                "entityMirrorColor",
                entityMirrorReady
                    ? entityMirrorProjection.ColorTextureId
                    : sourceColorTexture,
                22);
            shader.BindTexture2D(
                "entityMirrorDepth",
                entityMirrorReady
                    ? entityMirrorProjection.DepthTextureId
                    : sourceColorTexture,
                23);
            shader.Uniform("entityMirrorEnabled", entityMirrorReady ? 1 : 0);
            shader.UniformMatrix(
                "inverseEntityMirrorProjection",
                entityMirrorReady
                    ? entityMirrorProjection.InverseDepthProjectionMatrix
                    : inverseProjectionMatrix);
            shader.BindTexture2D("historyColor", temporalHistoryTextures[temporalHistoryIndex], 5);
            shader.BindTexture2D("gNormal", gBufferAvailable ? gBuffer.NormalTextureId : sourceColorTexture, 1);
            shader.BindTexture2D("gPosition", gBufferAvailable ? gBuffer.PositionTextureId : sourceColorTexture, 2);
            int directPositionTextureId = gBufferAvailable
                ? gBuffer.PositionTextureId
                : sourceColorTexture;
            if (reflectionSourceCapture.IsReady(width, height)
                && GameGBuffer.TryResolve(api.Render, out GameGBuffer liveGBuffer, out _))
            {
                directPositionTextureId = liveGBuffer.PositionTextureId;
            }
            shader.BindTexture2D("gDirectPosition", directPositionTextureId, 20);
            bool opaquePositionReady = reflectionSourceCapture.IsReady(width, height);
            shader.BindTexture2D(
                "gOpaquePosition",
                opaquePositionReady
                    ? reflectionSourceCapture.PositionTextureId
                    : directPositionTextureId,
                21);
            shader.Uniform("opaquePositionEnabled", opaquePositionReady ? 1 : 0);
            shader.BindTexture2D(
                "gOpaqueDepth",
                opaquePositionReady
                    ? reflectionSourceCapture.DepthTextureId
                    : sourceColorTexture,
                18);
            shader.Uniform("opaqueDepthEnabled", opaquePositionReady ? 1 : 0);
            bool liquidDepthReady = DisplayColorPipelineContract.TryResolveLiquidDepth(
                api.Render.FrameBuffers,
                width,
                height,
                out _,
                out int liquidDepthTextureId);
            shader.BindTexture2D(
                "gLiquidDepth",
                liquidDepthReady ? liquidDepthTextureId : sourceColorTexture,
                19);
            shader.Uniform("liquidDepthEnabled", liquidDepthReady ? 1 : 0);
            shader.BindTexture2D("gMaterial", gBufferAvailable ? gBuffer.GlowTextureId : sourceColorTexture, 8);
            GL.ActiveTexture(TextureUnit.Texture3);
            GL.BindTexture(TextureTarget.Texture3D, voxelTextureReady ? voxelTexture : 0);
            GL.Uniform1(voxelSamplerLocation, 3);
            GL.ActiveTexture(TextureUnit.Texture4);
            GL.BindTexture(TextureTarget.Texture3D, voxelTextureReady ? voxelOccupancyTexture : 0);
            GL.Uniform1(voxelOccupancySamplerLocation, 4);
            GL.ActiveTexture(TextureUnit.Texture6);
            GL.BindTexture(TextureTarget.Texture3D, voxelTextureReady ? voxelLightCasterTexture : 0);
            GL.Uniform1(voxelLightCasterSamplerLocation, 6);
            GL.ActiveTexture(TextureUnit.Texture7);
            GL.BindTexture(TextureTarget.Texture2D, voxelTextureReady ? voxelFluidSurfaceTexture : 0);
            GL.Uniform1(voxelFluidSurfaceSamplerLocation, 7);
            if (voxelLiquidMetadataSamplerLocation >= 0)
            {
                GL.ActiveTexture(TextureUnit.Texture13);
                GL.BindTexture(
                    TextureTarget.Texture3D,
                    voxelTextureReady ? voxelLiquidMetadataTexture : 0);
                GL.Uniform1(voxelLiquidMetadataSamplerLocation, 13);
            }
            if (liquidOpticalProfilesSamplerLocation >= 0)
            {
                GL.ActiveTexture(TextureUnit.Texture14);
                GL.BindTexture(
                    TextureTarget.Texture2D,
                    voxelTextureReady ? liquidOpticalProfilesTexture : 0);
                GL.Uniform1(liquidOpticalProfilesSamplerLocation, 14);
            }
            GL.ActiveTexture(TextureUnit.Texture16);
            GL.BindTexture(
                TextureTarget.Texture2D,
                dynamicLiquidSurfaceBinding.TextureId);
            GL.Uniform1(dynamicLiquidSurfaceSamplerLocation, 16);
            bool dynamicLiquidReady = dynamicLiquidSurfaceBinding.TextureId > 0;
            shader.Uniform(
                LiquidSurfaceGpuContract.OriginCellUniformName,
                (float)dynamicLiquidSurfaceBinding.OriginWorldX,
                (float)dynamicLiquidSurfaceBinding.OriginWorldZ,
                dynamicLiquidSurfaceBinding.CellSize);
            shader.Uniform(
                LiquidSurfaceGpuContract.GridSizeUniformName,
                (float)dynamicLiquidSurfaceBinding.Width,
                (float)dynamicLiquidSurfaceBinding.Depth);
            shader.Uniform("dynamicLiquidSurfaceEnabled", dynamicLiquidReady ? 1 : 0);
            BindSubgridImpactUniforms(dynamicLiquidReady);
            GL.ActiveTexture(TextureUnit.Texture9);
            GL.BindTexture(TextureTarget.Texture3D, voxelTextureReady ? voxelIrradianceTexture : 0);
            GL.Uniform1(voxelIrradianceSamplerLocation, 9);
            GL.ActiveTexture(TextureUnit.Texture10);
            GL.BindTexture(
                TextureTarget.Texture3D,
                voxelTextureReady ? voxelIrradianceDirectionTexture : 0);
            GL.Uniform1(voxelIrradianceDirectionSamplerLocation, 10);
            GL.ActiveTexture(TextureUnit.Texture11);
            GL.BindTexture(
                TextureTarget.Texture3D,
                voxelTextureReady ? voxelSunOccupancyTexture : 0);
            GL.Uniform1(voxelSunOccupancySamplerLocation, 11);
            GL.ActiveTexture(TextureUnit.Texture12);
            GL.BindTexture(
                TextureTarget.Texture2D,
                voxelTextureReady ? voxelRainSurfaceTexture : 0);
            GL.Uniform1(voxelRainSurfaceSamplerLocation, 12);
            shader.UniformMatrix("projection", projectionMatrix);
            shader.UniformMatrix("inverseProjection", inverseProjectionMatrix);
            shader.UniformMatrix("viewMatrix", viewMatrix);
            shader.UniformMatrix("inverseViewMatrix", inverseViewMatrix);
            shader.Uniform("inverseFrameSize", 1.0f / width, 1.0f / height);
            shader.Uniform("exposure", config.Exposure);
            shader.Uniform("contrast", config.Contrast);
            shader.Uniform("saturation", config.Saturation);
            shader.Uniform("vibrance", config.Vibrance);
            shader.Uniform("vignette", config.Vignette);
            shader.Uniform("indirectLightStrength", config.IndirectLightStrength);
            shader.Uniform("relightingStrength", config.RelightingStrength);
            shader.Uniform("skyLightStrength", config.SkyLightStrength);
            shader.Uniform("emissiveLightStrength", config.EmissiveLightStrength);
            shader.Uniform("contactShadowStrength", config.ContactShadowStrength);
            shader.Uniform("reflectionStrength", config.ReflectionStrength);
            shader.Uniform("reflectionDistance", config.ReflectionDistance);
            shader.Uniform("rainWetness", smoothedRainWetness);
            UpdateAdaptiveQuality(config, captureFrame);
            bool denseMultiLightCluster = IsDenseMultiLightCluster(
                adaptiveQualityLevel, availableDynamicLightCount, voxelSnapshot.Lights);
            shader.Uniform(
                "voxelReflectionsEnabled",
                config.VoxelReflectionsEnabled
                    && voxelTextureReady
                    ? 1
                    : 0);
            shader.Uniform("pointLightShadowStrength", config.PointLightShadowStrength);
            shader.Uniform("pointLightBounceStrength", config.PointLightBounceStrength);
            shader.Uniform("voxelBounceDistance", config.VoxelBounceDistance);
            shader.Uniform("pointLightRadius", config.PointLightRadius);
            shader.Uniform("pointLightSourceRadius", config.PointLightSourceRadius);
            int highQualityPointLightShadowSamples = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 6,
                VintageRtxRenderProfile.Cinematic => 8,
                _ => 3
            };
            int effectivePointLightShadowSamples = adaptiveQualityLevel switch
            {
                1 => Math.Min(config.PointLightShadowSamples, 2),
                // Two coherent samples keep thin lantern-cage silhouettes
                // continuous at the interlaced performance tier. Temporal
                // Vogel phases then converge the remaining soft-shadow disk.
                2 => Math.Min(config.PointLightShadowSamples, 2),
                _ => Math.Min(config.PointLightShadowSamples, highQualityPointLightShadowSamples)
            };
            shader.Uniform("sunLightStrength", config.SunLightStrength);
            // Full-frame transport remains mandatory at every tier. The former
            // alternating half-frame path met its average GPU budget by showing
            // a different wave/reflection solution on adjacent pixels and was
            // visible as checkerboard flips during continuous play.
            shader.Uniform("transportInterlace", 0);
            // A skipped bounce frame writes zero radiance before temporal blending,
            // which makes indirect light decay and then spike on the next traced frame.
            // Profiles reduce ray count/steps instead; every displayed frame remains coherent.
            shader.Uniform("secondaryBounceCadence", 1);
            denseMultiLightCluster = IsDenseMultiLightCluster(
                adaptiveQualityLevel, availableDynamicLightCount, voxelSnapshot.Lights);
            int effectiveVoxelBounceRayCount = adaptiveQualityLevel switch
                {
                    1 => Math.Min(config.VoxelBounceRayCount, 1),
                    // The CPU-built directional irradiance field is the stable
                    // secondary-light LOD at the minimum tier. A full-screen ray
                    // every frame exceeded the pacing budget, while skipping it
                    // periodically produced the visible zero/radiance pulse.
                    2 => 0,
                    _ => config.VoxelBounceRayCount
                };
            shader.Uniform("voxelBounceRayCount", effectiveVoxelBounceRayCount);
            int highQualityVoxelBounceSteps = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 12,
                VintageRtxRenderProfile.Cinematic => 16,
                _ => 8
            };
            int effectiveVoxelBounceSteps = adaptiveQualityLevel switch
            {
                1 => 7,
                // No per-pixel bounce ray is issued at this tier; these values
                // remain valid ABI inputs and diagnostic-capture fallbacks.
                2 => 2,
                _ => highQualityVoxelBounceSteps
            };
            int effectiveVoxelBounceShadowSteps = adaptiveQualityLevel switch
            {
                1 => 6,
                2 => 2,
                _ => highQualityVoxelBounceSteps
            };
            shader.Uniform("voxelBounceSteps", effectiveVoxelBounceSteps);
            shader.Uniform("voxelBounceShadowSteps", effectiveVoxelBounceShadowSteps);
            int highQualitySkyRayCount = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 3,
                VintageRtxRenderProfile.Cinematic => 4,
                _ => 2
            };
            int highQualitySkyTraceSteps = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 32,
                VintageRtxRenderProfile.Cinematic => 48,
                _ => 20
            };
            int effectiveSkyRayCount = adaptiveQualityLevel switch
            {
                1 => 1,
                // Performance already consumes the CPU-built directional
                // irradiance field and the independent long-range sun mask.
                // Repeating a short hierarchical sky DDA on every full-size
                // receiver duplicated occlusion work and exceeded the profile
                // budget. The raster carrier retains Vintage Story's ambient
                // sky solution at this lowest tier; Balanced and above keep an
                // explicit world-space probe.
                2 => 0,
                _ => highQualitySkyRayCount
            };
            int effectiveSkyTraceSteps = adaptiveQualityLevel switch
            {
                1 => 10,
                // One coherent sky probe is accumulated temporally. Three DDA
                // steps are sufficient for nearby cover; the independent long
                // sun visibility remains the conservative distant-roof gate.
                2 => 3,
                _ => highQualitySkyTraceSteps
            };
            shader.Uniform("skyRayCount", effectiveSkyRayCount);
            shader.Uniform("skyTraceSteps", effectiveSkyTraceSteps);
            int effectiveAlbedoDetailSamples = adaptiveQualityLevel switch
            {
                1 => 1,
                2 => 0,
                _ => 2
            };
            int effectiveTemporalDenoiseSamples = adaptiveQualityLevel switch
            {
                1 => 2,
                2 => 0,
                _ => 4
            };
            shader.Uniform("albedoDetailSamples", effectiveAlbedoDetailSamples);
            shader.Uniform("temporalDenoiseSamples", effectiveTemporalDenoiseSamples);
            int effectiveRayCount = adaptiveQualityLevel switch
            {
                1 => Math.Min(config.RayCount, 2),
                2 => 1,
                _ => config.RayCount
            };
            int effectiveRaySteps = adaptiveQualityLevel switch
            {
                1 => Math.Min(config.RaySteps, 6),
                2 => 3,
                _ => config.RaySteps
            };
            float highQualitySunFineShadowDistance = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 16.0f,
                VintageRtxRenderProfile.Cinematic => 20.0f,
                _ => 12.0f
            };
            float effectiveSunFineShadowDistance = adaptiveQualityLevel switch
            {
                1 => 8.0f,
                2 => 4.0f,
                _ => highQualitySunFineShadowDistance
            };
            float effectiveSunShadowDistance = voxelTextureReady
                ? Math.Max(config.SunShadowDistance, voxelSnapshot.SunTraceDistance)
                : config.SunShadowDistance;
            shader.Uniform("sunShadowDistance", effectiveSunShadowDistance);
            shader.Uniform("sunFineShadowDistance", effectiveSunFineShadowDistance);
            shader.Uniform("occupancyScale", (float)VoxelScene.OccupancyScale);
            shader.Uniform("rayDistance", config.RayDistance);
            shader.Uniform("rayCount", effectiveRayCount);
            shader.Uniform("raySteps", effectiveRaySteps);
            int highQualityReflectionSteps = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 16,
                VintageRtxRenderProfile.Cinematic => 24,
                _ => 10
            };
            int effectiveReflectionSteps = adaptiveQualityLevel switch
                {
                    1 => 6,
                    2 => 1,
                    _ => highQualityReflectionSteps
                };
            shader.Uniform("reflectionSteps", effectiveReflectionSteps);
            int highQualityVoxelReflectionSteps = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 48,
                VintageRtxRenderProfile.Cinematic => 64,
                _ => 32
            };
            int effectiveVoxelReflectionSteps = adaptiveQualityLevel switch
                {
                    1 => 18,
                    // Keep a short off-screen reflection at the minimum tier. A
                    // performance mode without any reflection is not a faithful
                    // degradation of an RTX-style renderer.
                    2 => 3,
                    _ => highQualityVoxelReflectionSteps
                };
            shader.Uniform("voxelReflectionSteps", effectiveVoxelReflectionSteps);
            shader.Uniform(
                "screenSpaceLightingEnabled",
                config.ScreenSpaceLightingEnabled
                    && gBufferAvailable
                    && adaptiveQualityLevel < 2
                    ? 1
                    : 0);
            shader.Uniform(
                "screenSpaceReflectionsEnabled",
                config.ScreenSpaceReflectionsEnabled
                    && gBufferAvailable
                    && adaptiveQualityLevel < 2
                    ? 1
                    : 0);
            int highQualityMaximumVoxelLights = config.RenderProfile switch
            {
                VintageRtxRenderProfile.Extreme => 6,
                VintageRtxRenderProfile.Cinematic => 8,
                _ => 4
            };
            int maximumVoxelLights = VoxelScene.MaximumLightCount;
            BindVoxelUniforms(config, maximumVoxelLights);
            shader.Uniform(
                "denseDynamicLightCluster",
                denseMultiLightCluster ? 1 : 0);
            if (adaptiveQualityLevel == 2 && currentDynamicLightCount > 0)
            {
                // An isolated moving penumbra gets a second coherent ray. With
                // several selected sources, keep one independent visibility ray
                // per source so total traversal cost stays deterministic.
                effectivePointLightShadowSamples = Math.Min(
                    config.PointLightShadowSamples,
                    availableDynamicLightCount > 1 ? 1 : 2);
            }
            shader.Uniform("pointLightShadowSamples", effectivePointLightShadowSamples);
            shader.Uniform("debugView", gBufferAvailable ? (int)effectiveDebugView : 0);
            shader.Uniform("temporalFrameIndex", (int)(renderedFrameCount & 0x7fffffff));
            float rendererTimeSeconds = (float)(
                (api.InWorldEllapsedMilliseconds % 4_096_000L) / 1000.0);
            LiquidSurfaceForcing sampledLiquidForcing = liquidSurfaceRuntime.CurrentForcing;
            float windUnitScale = (float)LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit;
            float liquidWindX = sampledLiquidForcing.WindVelocityXMetresPerSecond / windUnitScale;
            float liquidWindZ = sampledLiquidForcing.WindVelocityZMetresPerSecond / windUnitScale;
            shader.Uniform("rendererTimeSeconds", rendererTimeSeconds);
            shader.Uniform("liquidWindVector", liquidWindX, liquidWindZ);
            int effectiveLiquidWaveModeLimit = adaptiveQualityLevel switch
            {
                1 => 12,
                2 => 8,
                _ => 16
            };
            shader.Uniform("liquidWaveModeLimit", effectiveLiquidWaveModeLimit);
            shader.Uniform(
                "liquidWindMetresPerSecondPerEngineUnit",
                (float)LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit);
            float temporalBlend = config.TemporalAccumulationEnabled
                && temporalHistoryValid
                && temporalCameraStable
                && effectiveDebugView == VintageRtxDebugView.Final
                ? config.TemporalHistoryWeight
                : 0.0f;
            // Final RGB has no previous-position/object contract. Retain only independently
            // validated shadow history; do not ghost moving geometry or reflection silhouettes.
            shader.Uniform("temporalBlend", 0.0f);

            GL.BindVertexArray(fullscreenVertexArray);
            int shadowWidth = shadowTextureWidth;
            int shadowHeight = shadowTextureHeight;
            // Reciprocal tier-scaled texel dimensions, in shadow-pixel^-1.
            shader.Uniform(
                "shadowInverseFrameSize",
                1.0f / shadowWidth,
                1.0f / shadowHeight);
            shader.Uniform("shadowFilterTapCount", adaptiveQualityLevel == 2 ? 2 : 4);
            bool selectedPointShadowSources = currentVoxelLightCount > 0;
            bool selectedSunShadowSource = config.SunShadowsEnabled
                && config.SunLightStrength > 0.0f;
            bool shadowFilteringActive = config.VoxelLightingEnabled
                && voxelTextureReady
                && gBufferAvailable
                && (selectedPointShadowSources || selectedSunShadowSource);
            bool shadowRefreshActive = shadowFilteringActive
                && ShouldRefreshShadowVisibility(
                    adaptiveQualityLevel,
                    temporalCameraStable,
                    captureFrame,
                    shadowHistoryValid,
                    renderedFrameCount);
            shader.Uniform("shadowPass", 0);
            shader.Uniform("prefilteredShadowVisibility", 0);
            shader.Uniform("shadowTemporalBlend", 0.0f);
            shader.Uniform("outputColorDomain", 1);
            if (shadowRefreshActive)
            {
                // A coherent tier-scaled viewport evaluates every receiver;
                // there is no checkerboard/interlaced shadow phase at tier 2.
                // Pass 1 preserves one dimensionless visibility per light slot
                // across two RGBA16F banks and stores sun separately in R16F.
                // No final color is accumulated and no source is averaged.
                GL.Viewport(0, 0, shadowWidth, shadowHeight);
                GL.BindFramebuffer(
                    FramebufferTarget.DrawFramebuffer,
                    shadowCurrentFramebuffer);
                GL.DrawBuffers(ShadowDrawBuffers.Length, ShadowDrawBuffers);
                shader.Uniform("shadowPass", 1);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

                // Pass 2 performs the same geometric bilateral support for all
                // slots, but rejection/clamping remains independent per source.
                // Units 0/5/8 already belong to float sampler2D uniforms whose
                // original inputs are unused by this pass. Reusing only the
                // same sampler type avoids illegal sampler2D/sampler3D aliasing;
                // all three final-pass bindings are restored below.
                int shadowWriteIndex = shadowHistoryIndex ^ 1;
                shader.BindTexture2D(
                    "shadowPointCurrentA",
                    shadowCurrentPointATexture,
                    0);
                shader.BindTexture2D(
                    "shadowPointCurrentB",
                    shadowCurrentPointBTexture,
                    5);
                shader.BindTexture2D(
                    "shadowSunCurrent",
                    shadowCurrentSunTexture,
                    8);
                shader.BindTexture2D(
                    "shadowPointHistoryA",
                    shadowHistoryPointATextures[shadowHistoryIndex],
                    ShadowPointATextureUnit);
                shader.BindTexture2D(
                    "shadowPointHistoryB",
                    shadowHistoryPointBTextures[shadowHistoryIndex],
                    ShadowPointBTextureUnit);
                shader.BindTexture2D(
                    "shadowSunHistory",
                    shadowHistorySunTextures[shadowHistoryIndex],
                    ShadowSunTextureUnit);
                shader.Uniform("shadowPass", 2);
                // Raw visibility uses a fixed finite-emitter quadrature, so a
                // current-frame bilateral resolve is already deterministic.
                // Reusing screen-space visibility across frames without a
                // previous receiver depth/normal buffer transfers an animal's
                // shadow history onto the wall it vacates. Keep the history
                // textures as persistent per-source storage for tier-two reuse,
                // but never blend different receiver identities here.
                shader.Uniform("shadowTemporalBlend", 0.0f);
                GL.BindFramebuffer(
                    FramebufferTarget.DrawFramebuffer,
                    shadowHistoryFramebuffers[shadowWriteIndex]);
                GL.DrawBuffers(ShadowDrawBuffers.Length, ShadowDrawBuffers);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

                shadowHistoryIndex = shadowWriteIndex;
                shadowHistoryValid = true;
                shader.BindTexture2D("sourceColor", sourceColorTexture, 0);
                shader.BindTexture2D(
                    "historyColor",
                    temporalHistoryTextures[temporalHistoryIndex],
                    5);
                shader.BindTexture2D(
                    "gMaterial",
                    gBuffer.GlowTextureId,
                    8);
                shader.BindTexture2D(
                    "shadowPointHistoryA",
                    shadowHistoryPointATextures[shadowHistoryIndex],
                    ShadowPointATextureUnit);
                shader.BindTexture2D(
                    "shadowPointHistoryB",
                    shadowHistoryPointBTextures[shadowHistoryIndex],
                    ShadowPointBTextureUnit);
                shader.BindTexture2D(
                    "shadowSunHistory",
                    shadowHistorySunTextures[shadowHistoryIndex],
                    ShadowSunTextureUnit);
                GL.ActiveTexture(TextureUnit.Texture3);
                GL.BindTexture(TextureTarget.Texture3D, voxelTexture);
                GL.ActiveTexture(TextureUnit.Texture4);
                GL.BindTexture(TextureTarget.Texture3D, voxelOccupancyTexture);
                shader.Uniform("shadowPass", 0);
                shader.Uniform("prefilteredShadowVisibility", 1);
            }
            else if (shadowFilteringActive)
            {
                // Stable Performance frames alternate with the entity-mirror
                // replay. Rebind the prior independent source banks explicitly;
                // their temporal ownership remains valid and the final pass sees
                // exactly the same visibility solution as the preceding frame.
                shader.BindTexture2D(
                    "shadowPointHistoryA",
                    shadowHistoryPointATextures[shadowHistoryIndex],
                    ShadowPointATextureUnit);
                shader.BindTexture2D(
                    "shadowPointHistoryB",
                    shadowHistoryPointBTextures[shadowHistoryIndex],
                    ShadowPointBTextureUnit);
                shader.BindTexture2D(
                    "shadowSunHistory",
                    shadowHistorySunTextures[shadowHistoryIndex],
                    ShadowSunTextureUnit);
                shader.Uniform("prefilteredShadowVisibility", 1);
            }
            else
            {
                shadowHistoryValid = false;
            }

            // Shadow passes use an owned tier-scaled target. The final
            // color/diagnostic pass must cover the complete engine framebuffer.
            // GlState.Restore provides the exception-safe outer restoration.
            GL.Viewport(0, 0, width, height);

            if (renderToTemporalHistory)
            {
                GL.BindFramebuffer(
                    FramebufferTarget.DrawFramebuffer,
                    temporalFramebuffers[temporalWriteIndex]);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            }
            else
            {
                // The shadow resolve leaves its three-bank history target bound.
                // Diagnostic views are final colour outputs, not shadow masks;
                // explicitly restore the game's destination before drawing them.
                // Without this branch the screen remains unchanged and an A/B
                // capture silently validates raster colour as PBR evidence.
                GL.BindFramebuffer(
                    FramebufferTarget.DrawFramebuffer,
                    lumaBridge.FramebufferId);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            }

            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            if (renderToTemporalHistory)
            {
                GL.BindFramebuffer(
                    FramebufferTarget.ReadFramebuffer,
                    temporalFramebuffers[temporalWriteIndex]);
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                GL.BindFramebuffer(
                    FramebufferTarget.DrawFramebuffer,
                    lumaBridge.FramebufferId);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
                GL.BlitFramebuffer(
                    0,
                    0,
                    width,
                    height,
                    0,
                    0,
                    width,
                    height,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Nearest);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, state.ReadFramebuffer);
                GL.ReadBuffer((ReadBufferMode)state.ReadBuffer);
            }

            if (captureFrame
                && effectiveDebugView
                    is VintageRtxDebugView.ReflectionSource or VintageRtxDebugView.EntityMirror)
            {
                CaptureRawPreFinalDiagnostic(
                    state,
                    width,
                    height,
                    effectiveDebugView,
                    entityMirrorReady);
            }

            // Vintage Story enforces a single active IShaderProgram wrapper,
            // even though raw OpenGL would allow replacing GL_CURRENT_PROGRAM.
            // End the transport program before the independent repack pass.
            shader.Stop();
            shaderActive = false;
            lumaBridge.RepackToLuma(api.Render.FrameBuffers, width, height);
            if (renderToTemporalHistory)
            {
                // Commit only after the official Luma destination accepted the
                // repack; failed transport must never expose partial history.
                temporalHistoryIndex = temporalWriteIndex;
                temporalHistoryValid = true;
            }

            if (captureFrame || (renderedFrameCount & 127) == 0)
            {
                api.Render.CheckGlError("VintageRTX display and screen-space lighting pass");
            }
        }
        finally
        {
            try
            {
                if (shaderActive)
                {
                    shader!.Stop();
                }
            }
            finally
            {
                try
                {
                    state.Restore();
                }
                finally
                {
                    if (gpuMeasurementActive)
                    {
                        performanceMonitor.EndGpuMeasurement();
                    }
                }
            }
        }
    }

    /// <summary>Binds up to four local-coordinate physical packets without per-frame allocation.</summary>
    /// <param name="dynamicLiquidReady">Whether the packet's surface grid is currently available.</param>
    private void BindSubgridImpactUniforms(bool dynamicLiquidReady)
    {
        int boundCount = 0;
        if (dynamicLiquidReady)
        {
            for (int slot = 0; slot < activeSubgridImpacts.Length; slot++)
            {
                LiquidSurfaceSubgridImpactDiagnostic impact = activeSubgridImpacts[slot];
                float ageSeconds = activeSubgridImpactAges[slot];
                if (impact.Sequence <= 0
                    || ageSeconds > MaximumSubgridImpactAgeSeconds
                    || (impact.PeakDisplacement <= 0.0f
                        && impact.SplashPeakDisplacement <= 0.0f))
                {
                    continue;
                }

                PackSubgridImpactUniform(
                    in impact,
                    (float)(impact.WorldX - dynamicLiquidSurfaceBinding.OriginWorldX),
                    (float)(impact.WorldZ - dynamicLiquidSurfaceBinding.OriginWorldZ),
                    ageSeconds,
                    boundCount,
                    liquidImpactOrigins,
                    liquidImpactMaterials,
                    liquidImpactMotions,
                    liquidImpactEnergies,
                    liquidImpactSplashes);
                boundCount++;
            }
        }

        shader!.Uniform("liquidImpactWaveActive", boundCount);
        if (boundCount > 0)
        {
            GL.Uniform4(liquidImpactOriginLocation, boundCount, liquidImpactOrigins);
            GL.Uniform4(liquidImpactMaterialLocation, boundCount, liquidImpactMaterials);
            GL.Uniform4(liquidImpactMotionLocation, boundCount, liquidImpactMotions);
            GL.Uniform4(liquidImpactEnergyLocation, boundCount, liquidImpactEnergies);
            GL.Uniform4(liquidImpactSplashLocation, boundCount, liquidImpactSplashes);
        }
    }

    /// <summary>Packs one physical packet into five contiguous vec4 arrays for direct OpenGL upload.</summary>
    /// <param name="impact">Source identity, material, motion, and conservative energy ledger.</param>
    /// <param name="localWorldX">Impact X relative to the active liquid-grid origin.</param>
    /// <param name="localWorldZ">Impact Z relative to the active liquid-grid origin.</param>
    /// <param name="ageSeconds">Non-negative renderer age of this packet.</param>
    /// <param name="packetIndex">Zero-based destination vec4 index.</param>
    /// <param name="origins">Origin, age, and peak-amplitude vec4 storage.</param>
    /// <param name="materials">Density, viscosity, surface tension, and damping vec4 storage.</param>
    /// <param name="motions">Horizontal motion, wavelength, and world scale vec4 storage.</param>
    /// <param name="energies">Surface, resolved, unresolved, and rendered-energy vec4 storage.</param>
    /// <param name="splashes">Local peak, radius, release time, and splash-energy vec4 storage.</param>
    internal static void PackSubgridImpactUniform(
        in LiquidSurfaceSubgridImpactDiagnostic impact,
        float localWorldX,
        float localWorldZ,
        float ageSeconds,
        int packetIndex,
        Span<float> origins,
        Span<float> materials,
        Span<float> motions,
        Span<float> energies,
        Span<float> splashes)
    {
        int offset = packetIndex * 4;
        int requiredLength = offset + 4;
        if ((uint)packetIndex >= MaximumActiveSubgridImpactCount
            || origins.Length < requiredLength
            || materials.Length < requiredLength
            || motions.Length < requiredLength
            || energies.Length < requiredLength
            || splashes.Length < requiredLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(packetIndex),
                "Sub-grid packet vec4 storage is missing or the packet index is out of range.");
        }

        origins[offset] = localWorldX;
        origins[offset + 1] = localWorldZ;
        origins[offset + 2] = Math.Max(0.0f, ageSeconds);
        origins[offset + 3] = impact.PeakDisplacement;
        materials[offset] = impact.DensityKilogramsPerCubicMetre;
        materials[offset + 1] = impact.DynamicViscosityPascalSeconds;
        materials[offset + 2] = impact.SurfaceTensionNewtonsPerMetre;
        materials[offset + 3] = impact.AdditionalDampingPerSecond;
        motions[offset] = impact.DirectionXMetresPerSecond;
        motions[offset + 1] = impact.DirectionZMetresPerSecond;
        motions[offset + 2] = impact.DominantWavelengthMetres;
        motions[offset + 3] = (float)LiquidPhysicalModel.MetresPerWorldBlock;
        energies[offset] = impact.SurfaceCoupledEnergyJoules;
        energies[offset + 1] = impact.ResolvedWaveEnergyJoules;
        energies[offset + 2] = impact.SubgridWaveEnergyJoules;
        energies[offset + 3] = impact.RenderedPacketEnergyJoules;
        splashes[offset] = impact.SplashPeakDisplacement;
        splashes[offset + 1] = impact.SplashRadiusWorldBlocks;
        splashes[offset + 2] = impact.SplashReleaseSeconds;
        splashes[offset + 3] = impact.LocalSplashEnergyJoules;
    }

    /// <summary>Builds the entity-only mirror texture for the nearest active liquid interface.</summary>
    /// <param name="config">Normalized reflection distance and enablement.</param>
    /// <param name="frameWidth">Full source width.</param>
    /// <param name="frameHeight">Full source height.</param>
    /// <param name="captureEntityEvidence">Whether this frame needs an isolated entity readback.</param>
    /// <returns>Whether the current frame owns a valid projected entity texture.</returns>
    private bool RenderEntityMirror(
        VintageRtxConfig config,
        int frameWidth,
        int frameHeight,
        bool captureEntityEvidence)
    {
        if (!config.ScreenSpaceReflectionsEnabled
            || !reflectionSourceCapture.IsReady(frameWidth, frameHeight)
            || !entityMirrorSourceCapture.IsReady(frameWidth, frameHeight))
        {
            return false;
        }

        ResolveRenderFloatingOrigin(
            out double floatingOriginX,
            out double floatingOriginY,
            out double floatingOriginZ);
        if (!liquidSurfaceRuntime.TryGetNearestSurfaceWorldY(
                floatingOriginX,
                floatingOriginZ,
                out float surfaceWorldY))
        {
            return false;
        }

        double[] cameraMatrix = api.Render.CameraMatrixOrigin;
        float[] cameraMatrixFloat = api.Render.CameraMatrixOriginf;
        double[] perspectiveProjection = api.Render.PerspectiveProjectionMat;
        float[] currentProjection = api.Render.CurrentProjectionMatrix;
        var projectionStack = api.Render.PMatrix;
        if (cameraMatrix.Length < 16
            || cameraMatrixFloat.Length < 16
            || perspectiveProjection.Length < 16
            || currentProjection.Length < 16
            || projectionStack.Count < 1
            || projectionStack.Top.Length < 16)
        {
            return false;
        }

        Array.Copy(cameraMatrix, mirrorGuardCameraMatrix, 16);
        Array.Copy(cameraMatrixFloat, mirrorGuardCameraMatrixFloat, 16);
        Array.Copy(perspectiveProjection, mirrorGuardPerspectiveProjection, 16);
        Array.Copy(currentProjection, mirrorGuardCurrentProjection, 16);
        Array.Copy(projectionStack.Top, mirrorGuardProjectionStackTop, 16);
        int savedProjectionStackCount = projectionStack.Count;
        try
        {
            entityMirrorProjection.Render(
                frameWidth,
                frameHeight,
                reflectionSourceCapture.TextureId,
                reflectionSourceCapture.PositionTextureId,
                entityMirrorSourceCapture.PositionTextureId,
                projectionMatrix,
                viewMatrix,
                inverseViewMatrix,
                floatingOriginX,
                floatingOriginY,
                floatingOriginZ,
                surfaceWorldY,
                config.ReflectionDistance,
                captureEntityEvidence,
                MirrorResolutionDivisor(adaptiveQualityLevel));
            return true;
        }
        finally
        {
            EntityMirrorGeometryReplayPatch.RestoreMutableCameraCarriers(
                cameraMatrix,
                mirrorGuardCameraMatrix,
                cameraMatrixFloat,
                mirrorGuardCameraMatrixFloat,
                perspectiveProjection,
                mirrorGuardPerspectiveProjection,
                currentProjection,
                mirrorGuardCurrentProjection,
                projectionStack,
                savedProjectionStackCount,
                mirrorGuardProjectionStackTop);
        }
    }

    /// <summary>Returns the full-frame divisor used by the official mirrored geometry carrier.</summary>
    /// <param name="qualityLevel">Current adaptive tier, where two is Performance.</param>
    /// <returns>Two for high/balanced tiers and four for stable full-rate Performance replay.</returns>
    internal static int MirrorResolutionDivisor(int qualityLevel) => qualityLevel == 2 ? 4 : 2;

    /// <summary>
    /// Decides whether independent point/sun visibility banks need a fresh raw and bilateral pass.
    /// Stable Performance frames reuse one visibility solution while the lower-resolution entity
    /// mirror remains current at full display rate.
    /// </summary>
    /// <param name="qualityLevel">Current adaptive quality tier, where two is Performance.</param>
    /// <param name="cameraStable">Whether the camera matches the preceding rendered frame.</param>
    /// <param name="captureFrame">Whether automated evidence requires a current visibility mask.</param>
    /// <param name="shadowHistoryReady">Whether reusable per-source visibility banks exist.</param>
    /// <param name="frameIndex">Monotonic renderer frame index.</param>
    /// <returns><see langword="true"/> when raw and filtered visibility must be regenerated.</returns>
    internal static bool ShouldRefreshShadowVisibility(
        int qualityLevel,
        bool cameraStable,
        bool captureFrame,
        bool shadowHistoryReady,
        long frameIndex)
    {
        return qualityLevel != 2
            || !cameraStable
            || captureFrame
            || !shadowHistoryReady
            || (frameIndex & 1L) != 0L;
    }

    /// <summary>Copies projection/view data into float arrays matching GLSL column-major upload.</summary>
    private void CopyCameraMatrices()
    {
        double[] source = api.Render.PerspectiveProjectionMat;
        if (source.Length < projectionMatrix.Length)
        {
            throw new InvalidOperationException("The perspective projection matrix is incomplete.");
        }

        for (int index = 0; index < projectionMatrix.Length; index++)
        {
            projectionMatrix[index] = (float)source[index];
        }

        Mat4d.Invert(inverseProjectionMatrixDouble, source);
        for (int index = 0; index < inverseProjectionMatrix.Length; index++)
        {
            inverseProjectionMatrix[index] = (float)inverseProjectionMatrixDouble[index];
        }

        double[] cameraMatrix = api.Render.CameraMatrixOrigin;
        if (cameraMatrix.Length < inverseViewMatrix.Length)
        {
            throw new InvalidOperationException("The camera matrix is incomplete.");
        }

        Mat4d.Invert(inverseViewMatrixDouble, cameraMatrix);
        for (int index = 0; index < inverseViewMatrix.Length; index++)
        {
            viewMatrix[index] = (float)cameraMatrix[index];
            inverseViewMatrix[index] = (float)inverseViewMatrixDouble[index];
        }
    }

    /// <summary>Invalidates history on real camera motion while ignoring measured render-stage matrix jitter.</summary>
    /// <returns>Whether previous-frame color/depth may be reprojected safely.</returns>
    private bool UpdateTemporalCameraStability()
    {
        if (temporalMotionResetLogCooldownFrames > 0)
        {
            temporalMotionResetLogCooldownFrames--;
        }

        double[] currentMatrix = api.Render.CameraMatrixOrigin;
        // CameraPos includes engine eye/bob offsets which can oscillate while
        // the rendered CameraMatrixOrigin and the player's world transform are
        // unchanged. Anchor temporal reprojection to the authoritative entity
        // position; the matrix still catches real eye rotation/translation.
        var currentPosition = api.World.Player.Entity.Pos;
        bool stable = temporalCameraInitialized && currentMatrix.Length >= temporalCameraMatrix.Length;
        if (stable)
        {
            for (int index = 0; index < temporalCameraMatrix.Length; index++)
            {
                if (Math.Abs(currentMatrix[index] - temporalCameraMatrix[index])
                    > CameraMatrixStabilityEpsilon)
                {
                    stable = false;
                    break;
                }
            }

            double dx = currentPosition.X - temporalCameraX;
            double dy = currentPosition.Y - temporalCameraY;
            double dz = currentPosition.Z - temporalCameraZ;
            if (!automatedCameraLock)
            {
                stable &= dx * dx + dy * dy + dz * dz <= 0.000001;
            }
        }

        if (currentMatrix.Length >= temporalCameraMatrix.Length)
        {
            Array.Copy(currentMatrix, temporalCameraMatrix, temporalCameraMatrix.Length);
            temporalCameraInitialized = true;
        }
        else
        {
            temporalCameraInitialized = false;
        }

        temporalCameraX = currentPosition.X;
        temporalCameraY = currentPosition.Y;
        temporalCameraZ = currentPosition.Z;
        if (!stable)
        {
            if (temporalHistoryValid && temporalMotionResetLogCooldownFrames == 0)
            {
                api.Logger.Notification(
                    "[VintageRTX] Temporal history reset after camera movement; coherent single-frame sampling active.");
                // Continuous play should not turn ordinary mouse movement into
                // a renderer-log flood. One diagnostic per minute is enough.
                temporalMotionResetLogCooldownFrames = 3600;
            }

            temporalHistoryValid = false;
            shadowHistoryValid = false;
        }

        return stable;
    }

    /// <summary>Applies hysteretic high/balanced/performance tier changes from smoothed GPU budget pressure.</summary>
    /// <param name="config">Normalized budget and adaptive-quality setting.</param>
    /// <param name="captureFrame">Whether diagnostic readback is active and tier transitions must pause.</param>
    private void UpdateAdaptiveQuality(VintageRtxConfig config, bool captureFrame)
    {
        int profileFloor = config.AdaptiveQualityFloor;
        if (activeRenderProfile != config.RenderProfile)
        {
            activeRenderProfile = config.RenderProfile;
            adaptiveQualityLevel = profileFloor;
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
            adaptiveTransitionCooldownFrames = 0;
            temporalHistoryValid = false;
            shadowHistoryValid = false;
            api.Logger.Notification(
                "[VintageRTX] Rendering profile {0} active: initial-tier={1}, adaptive={2}, gpu-budget={3:0.00}ms.",
                config.RenderProfile,
                QualityLevelName(adaptiveQualityLevel),
                config.AdaptiveQualityEnabled,
                config.GpuBudgetMilliseconds);
        }
        else if (adaptiveQualityLevel < profileFloor)
        {
            adaptiveQualityLevel = profileFloor;
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
        }

        if (!config.AdaptiveQualityEnabled)
        {
            adaptiveQualityLevel = profileFloor;
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
            adaptiveQualityStatus = $"{QualityLevelName(adaptiveQualityLevel)} (fixed)";
            return;
        }

        if (captureFrame)
        {
            adaptiveCaptureCooldownFrames = 120;
        }

        if (adaptiveCaptureCooldownFrames > 0)
        {
            adaptiveCaptureCooldownFrames--;
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
            adaptiveQualityStatus = $"{QualityLevelName(adaptiveQualityLevel)} (capture excluded)";
            return;
        }

        if (adaptiveTransitionCooldownFrames > 0)
        {
            adaptiveTransitionCooldownFrames--;
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
            adaptiveQualityStatus = $"{QualityLevelName(adaptiveQualityLevel)} (transition settling)";
            return;
        }

        double gpuMilliseconds = performanceMonitor.SmoothedGpuMilliseconds;
        if (gpuMilliseconds <= 0.0)
        {
            adaptiveQualityStatus = QualityLevelName(adaptiveQualityLevel);
            return;
        }

        // A renderer sitting only a few percent above budget can still miss the
        // next v-sync boundary when the base game has a busy frame. Keep a small
        // 2% tolerance, then favor stable frame pacing over a marginal quality tier.
        if (gpuMilliseconds > config.GpuBudgetMilliseconds * 1.02)
        {
            adaptiveOverBudgetFrames++;
            adaptiveUnderBudgetFrames = 0;
        }
        else if (gpuMilliseconds < config.GpuBudgetMilliseconds * AdaptiveUpgradeHeadroom)
        {
            adaptiveUnderBudgetFrames++;
            adaptiveOverBudgetFrames = 0;
        }
        else
        {
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
        }

        if (adaptiveOverBudgetFrames >= AdaptiveDowngradeFrames && adaptiveQualityLevel < 2)
        {
            adaptiveQualityLevel = AdaptiveDowngradeTarget(
                adaptiveQualityLevel,
                gpuMilliseconds,
                config.GpuBudgetMilliseconds);
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
            adaptiveTransitionCooldownFrames = AdaptiveTransitionCooldownFrames;
            api.Logger.Notification(
                "[VintageRTX] Adaptive quality reduced to {0}: GPU {1:0.00}ms exceeds {2:0.00}ms budget.",
                QualityLevelName(adaptiveQualityLevel),
                gpuMilliseconds,
                config.GpuBudgetMilliseconds);
        }
        else if (adaptiveUnderBudgetFrames >= AdaptiveUpgradeFrames
            && adaptiveQualityLevel > profileFloor)
        {
            adaptiveQualityLevel--;
            adaptiveOverBudgetFrames = 0;
            adaptiveUnderBudgetFrames = 0;
            adaptiveTransitionCooldownFrames = AdaptiveTransitionCooldownFrames;
            api.Logger.Notification(
                "[VintageRTX] Adaptive quality increased to {0}: GPU headroom is stable at {1:0.00}ms.",
                QualityLevelName(adaptiveQualityLevel),
                gpuMilliseconds);
        }

        adaptiveQualityStatus = $"{QualityLevelName(adaptiveQualityLevel)} ({gpuMilliseconds:0.00}/{config.GpuBudgetMilliseconds:0.00}ms)";
    }

    /// <summary>
    /// Selects the next lower-cost tier. A severe initial overload skips the short-lived Balanced
    /// probe so temporal transport changes only once before it converges; mild pressure still moves
    /// by one tier and retains the existing hysteresis.
    /// </summary>
    /// <param name="currentLevel">Current tier: zero high, one balanced, two performance.</param>
    /// <param name="gpuMilliseconds">Smoothed VintageRTX GPU duration.</param>
    /// <param name="budgetMilliseconds">Configured VintageRTX GPU budget.</param>
    /// <returns>A bounded target tier in the inclusive range zero through two.</returns>
    internal static int AdaptiveDowngradeTarget(
        int currentLevel,
        double gpuMilliseconds,
        double budgetMilliseconds)
    {
        int boundedLevel = Math.Clamp(currentLevel, 0, 2);
        bool severeInitialOverload = boundedLevel == 0
            && budgetMilliseconds > 0.0
            && gpuMilliseconds >= budgetMilliseconds * AdaptiveSevereOverBudgetRatio;
        return severeInitialOverload ? 2 : Math.Min(2, boundedLevel + 1);
    }

    /// <summary>Maps the internal zero-based tier to stable status text.</summary>
    /// <param name="level">0 high, 1 balanced, or 2 performance.</param>
    /// <returns>User-facing tier label.</returns>
    private static string QualityLevelName(int level)
    {
        return level switch
        {
            1 => "balanced",
            2 => "performance",
            _ => "high"
        };
    }

    /// <summary>Updates small light/caster tables independently from full geometry publication.</summary>
    private void UpdateLiveEmitterTextures()
    {
        if (!voxelTextureReady) return;
        if (voxelScene.TryConsumeLiveLights(voxelSnapshot.Generation, out VoxelLight[] lights))
        {
            voxelSnapshot = voxelSnapshot with { Lights = lights };
            Array.Clear(liveCasterUpload);
            for (int i = 0; i < Math.Min(lights.Length, VoxelScene.MaximumLightCount); i++)
                if (lights[i].CasterMask.Length == VoxelScene.LightCasterVoxelCount)
                    Array.Copy(lights[i].CasterMask, 0, liveCasterUpload, i * VoxelScene.LightCasterVoxelCount, VoxelScene.LightCasterVoxelCount);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.ActiveTexture(TextureUnit.Texture6);
            GL.BindTexture(TextureTarget.Texture3D, voxelLightCasterTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                VoxelScene.LightCasterScale, VoxelScene.LightCasterScale,
                VoxelScene.LightCasterScale * VoxelScene.MaximumLightCount,
                PixelFormat.Red, PixelType.UnsignedByte, liveCasterUpload);
            temporalHistoryValid = false;
            shadowHistoryValid = false;
        }
        if (voxelScene.TryConsumeLiveRadiance(voxelSnapshot.Generation, out VoxelRadianceField field))
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.ActiveTexture(TextureUnit.Texture9);
            GL.BindTexture(TextureTarget.Texture3D, voxelIrradianceTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                voxelSnapshot.Width, voxelSnapshot.Height, voxelSnapshot.Depth,
                PixelFormat.Rgb, PixelType.UnsignedByte, field.Irradiance);
            GL.ActiveTexture(TextureUnit.Texture10);
            GL.BindTexture(TextureTarget.Texture3D, voxelIrradianceDirectionTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                voxelSnapshot.Width, voxelSnapshot.Height, voxelSnapshot.Depth,
                PixelFormat.Rgb, PixelType.UnsignedByte, field.Direction);
            temporalHistoryValid = false;
        }
    }

    /// <summary>Binds one coherent scene and light selection after incremental light uploads.</summary>
    /// <param name="config">Current normalized rendering options.</param>
    /// <param name="maximumVoxelLights">Maximum simultaneous light slots.</param>
    private void BindVoxelUniforms(VintageRtxConfig config, int maximumVoxelLights)
    {
        UpdateLiveEmitterTextures();

        bool enabled = config.VoxelLightingEnabled
            && voxelTextureReady
            && gBufferAvailable;
        shader!.Uniform("voxelLightingEnabled", enabled ? 1 : 0);
        shader.Uniform(
            "voxelOrigin",
            (float)voxelSnapshot.OriginX,
            (float)voxelSnapshot.OriginY,
            (float)voxelSnapshot.OriginZ);
        shader.Uniform(
            "voxelSize",
            (float)voxelSnapshot.Width,
            (float)voxelSnapshot.Height,
            (float)voxelSnapshot.Depth);
        shader.Uniform(
            "fluidSurfaceOrigin",
            (float)voxelSnapshot.FluidSurfaceOriginX,
            (float)voxelSnapshot.FluidSurfaceOriginZ);
        shader.Uniform(
            "fluidSurfaceSize",
            (float)voxelSnapshot.FluidSurfaceWidth,
            (float)voxelSnapshot.FluidSurfaceDepth);
        shader.Uniform(
            "sunVoxelOrigin",
            (float)voxelSnapshot.SunOriginX,
            (float)voxelSnapshot.SunOriginY,
            (float)voxelSnapshot.SunOriginZ);
        shader.Uniform(
            "sunVoxelSize",
            (float)voxelSnapshot.SunOccupancyWidth,
            (float)voxelSnapshot.SunOccupancyHeight,
            (float)voxelSnapshot.SunOccupancyDepth);
        shader.Uniform("sunOccupancyScale", (float)voxelSnapshot.SunOccupancyScale);
        shader.Uniform(
            "rainSurfaceOrigin",
            (float)voxelSnapshot.RainSurfaceOriginX,
            (float)voxelSnapshot.RainSurfaceOriginZ);
        shader.Uniform(
            "rainSurfaceSize",
            (float)voxelSnapshot.RainSurfaceWidth,
            (float)voxelSnapshot.RainSurfaceDepth);

        ResolveRenderFloatingOrigin(
            out double floatingOriginX,
            out double floatingOriginY,
            out double floatingOriginZ);
        Vec3d cameraPosition = new(
            floatingOriginX + inverseViewMatrixDouble[12],
            floatingOriginY + inverseViewMatrixDouble[13],
            floatingOriginZ + inverseViewMatrixDouble[14]);
        shader.Uniform(
            "floatingWorldOrigin",
            (float)floatingOriginX,
            (float)floatingOriginY,
            (float)floatingOriginZ);
        shader.Uniform(
            "cameraWorldPosition",
            (float)cameraPosition.X,
            (float)cameraPosition.Y,
            (float)cameraPosition.Z);

        Array.Clear(voxelLightPositions);
        Array.Clear(voxelLightColors);
        Array.Clear(voxelLightPhotometry);
        Array.Clear(voxelLightSelectionScores);
        Array.Clear(currentShadowLightSlotKeys);
        Array.Fill(voxelLightCasterLayers, -1.0f);
        int lightCount = enabled
            ? CopyDynamicPointLights(
                config,
                floatingOriginX,
                floatingOriginY,
                floatingOriginZ,
                maximumVoxelLights)
            : 0;
        if (enabled && voxelSnapshot.Lights is { Length: > 0 })
        {
            for (int staticIndex = 0; staticIndex < voxelSnapshot.Lights.Length; staticIndex++)
            {
                VoxelLight light = voxelSnapshot.Lights[staticIndex];
                float staticScore = StaticLightSelectionScore(light, cameraPosition, config.PointLightRadius);
                int duplicateIndex = FindDuplicateDynamicLight(light, lightCount);
                int targetIndex = SelectStaticLightTarget(
                    duplicateIndex,
                    maximumVoxelLights,
                    staticScore,
                    ref lightCount);

                if (targetIndex >= 0)
                {
                    // Engine point-light uniforms also contain nearby static
                    // lanterns. Replace their duplicate with the voxel-scene
                    // source so its authored 16^3 cage/alpha caster is retained
                    // without counting the same emitted energy twice.
                    WriteStaticLight(targetIndex, staticIndex, light, config.PointLightRadius, staticScore);
                }
            }
        }
        currentDynamicLightCount = 0;
        for (int index = 0; index < lightCount; index++)
        {
            if (voxelLightCasterLayers[index] < -0.5f)
            {
                currentDynamicLightCount++;
            }
        }

        BindSunUniforms(config, enabled, cameraPosition);

        currentVoxelLightCount = lightCount;
        if (!UpdateShadowLightSlotHistory(
                lightCount,
                floatingOriginX,
                floatingOriginY,
                floatingOriginZ))
        {
            shadowHistoryValid = false;
        }

        shader.Uniform("voxelLightCount", lightCount);
        if (lightCount > 0)
        {
            GL.Uniform4(voxelLightPositionLocation, lightCount, voxelLightPositions);
            GL.Uniform4(voxelLightColorLocation, lightCount, voxelLightColors);
            GL.Uniform4(voxelLightPhotometryLocation, lightCount, voxelLightPhotometry);
            GL.Uniform1(voxelLightCasterLayerLocation, lightCount, voxelLightCasterLayers);
        }
    }

    /// <summary>Selects a duplicate, free, or stronger replacement slot while updating bounded light count.</summary>
    /// <param name="duplicateIndex">Existing dynamic slot matching the static light, or -1.</param>
    /// <param name="maximumVoxelLights">Shader slot capacity for the active quality tier.</param>
    /// <param name="staticScore">Camera-relative score of the static candidate.</param>
    /// <param name="lightCount">Populated slot count, incremented only when a free slot is claimed.</param>
    /// <returns>Writable slot, or -1 when the candidate cannot displace the current selection.</returns>
    private int SelectStaticLightTarget(
        int duplicateIndex,
        int maximumVoxelLights,
        float staticScore,
        ref int lightCount)
    {
        if (duplicateIndex >= 0)
        {
            return duplicateIndex;
        }
        if (lightCount < maximumVoxelLights)
        {
            return lightCount++;
        }

        int weakestIndex = FindWeakestSelectedLight(lightCount);
        return weakestIndex >= 0
            && staticScore > voxelLightSelectionScores[weakestIndex] * 1.08f
                ? weakestIndex
                : -1;
    }

    /// <summary>Finds a selected dynamic light spatially duplicating one candidate within 0.85 blocks.</summary>
    /// <param name="light">Dynamic candidate in world space.</param>
    /// <param name="lightCount">Currently populated GPU light slots.</param>
    /// <returns>Closest duplicate index or -1.</returns>
    private int FindDuplicateDynamicLight(VoxelLight light, int lightCount)
    {
        // Explicit entities may legitimately stand beside a placed lamp. Never collapse them
        // using the legacy proximity-only static/engine-light association below.

        const float duplicateDistanceSquared = 0.85f * 0.85f;
        int closestIndex = -1;
        float closestDistanceSquared = duplicateDistanceSquared;
        for (int index = 0; index < lightCount; index++)
        {
            if (index < lastSelectedDynamicSourceCount && lastSelectedDynamicSourceIndices[index] >= 1_000_000) continue;
            if (voxelLightCasterLayers[index] >= -0.5f)
            {
                continue;
            }

            int offset = index * 4;
            float dx = voxelLightPositions[offset] - light.X;
            float dy = voxelLightPositions[offset + 1] - light.Y;
            float dz = voxelLightPositions[offset + 2] - light.Z;
            float distanceSquared = dx * dx + dy * dy + dz * dz;
            if (distanceSquared < closestDistanceSquared)
            {
                closestIndex = index;
                closestDistanceSquared = distanceSquared;
            }
        }

        return closestIndex;
    }

    /// <summary>Finds the minimum selection-score slot for bounded replacement.</summary>
    /// <param name="lightCount">Currently populated GPU light slots.</param>
    /// <returns>Weakest index or -1 when empty.</returns>
    private int FindWeakestSelectedLight(int lightCount)
    {
        int weakestIndex = -1;
        float weakestScore = float.PositiveInfinity;
        for (int index = 0; index < lightCount; index++)
        {
            if (voxelLightSelectionScores[index] < weakestScore)
            {
                weakestIndex = index;
                weakestScore = voxelLightSelectionScores[index];
            }
        }

        return weakestIndex;
    }

    /// <summary>Writes one static voxel emitter into packed shader arrays and stable-source metadata.</summary>
    /// <param name="targetIndex">Destination GPU light slot.</param>
    /// <param name="staticIndex">Stable index inside the voxel snapshot.</param>
    /// <param name="light">World-space light data.</param>
    /// <param name="pointLightRadius">Configured range in blocks.</param>
    /// <param name="selectionScore">Camera-relative importance used by later replacement.</param>
    private void WriteStaticLight(
        int targetIndex,
        int staticIndex,
        VoxelLight light,
        float pointLightRadius,
        float selectionScore)
    {
        int offset = targetIndex * 4;
        voxelLightPositions[offset] = light.X;
        voxelLightPositions[offset + 1] = light.Y;
        voxelLightPositions[offset + 2] = light.Z;
        voxelLightPositions[offset + 3] = light.Intensity;
        voxelLightColors[offset] = light.Red;
        voxelLightColors[offset + 1] = light.Green;
        voxelLightColors[offset + 2] = light.Blue;
        voxelLightColors[offset + 3] = Math.Min(pointLightRadius, light.TraceRadiusMetres());
        voxelLightPhotometry[offset] = light.SourceHalfWidthMetres;
        voxelLightPhotometry[offset + 1] = light.SourceHalfHeightMetres;
        voxelLightPhotometry[offset + 2] = light.CutoffIlluminanceLux;
        voxelLightPhotometry[offset + 3] = 1.0f;
        voxelLightCasterLayers[targetIndex] = light.CasterMask.Length
            == VoxelScene.LightCasterVoxelCount
                ? staticIndex
                : -1.0f;
        voxelLightSelectionScores[targetIndex] = selectionScore;
        currentShadowLightSlotKeys[targetIndex] = StaticShadowLightSlotKey(light);
    }

    /// <summary>Builds a stable tagged key for one engine-owned dynamic light slot.</summary>
    /// <param name="sourceIndex">Stable source index exposed by the engine point-light arrays.</param>
    /// <returns>Non-zero dynamic-source identity disjoint from static emitter keys.</returns>
    internal static long DynamicShadowLightSlotKey(int sourceIndex) =>
        unchecked((long)(0x4000000000000000UL | (uint)(sourceIndex + 1)));

    /// <summary>Builds a stable tagged key for one world-space voxel emitter.</summary>
    /// <param name="light">Static light whose exact source position owns visibility history.</param>
    /// <returns>Non-zero position identity disjoint from dynamic emitter keys.</returns>
    internal static long StaticShadowLightSlotKey(VoxelLight light)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(light.X)) * prime;
        hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(light.Y)) * prime;
        hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(light.Z)) * prime;
        return unchecked((long)(0x8000000000000000UL | (hash & 0x3fffffffffffffffUL)));
    }

    /// <summary>Compares ordered active source identities before temporal visibility reuse.</summary>
    /// <param name="previousKeys">Previous-frame slot identities.</param>
    /// <param name="previousCount">Number of active previous slots.</param>
    /// <param name="currentKeys">Current-frame slot identities.</param>
    /// <param name="currentCount">Number of active current slots.</param>
    /// <returns>Whether every active visibility channel still belongs to the same source.</returns>
    internal static bool ShadowLightSlotsAreStable(
        ReadOnlySpan<long> previousKeys,
        int previousCount,
        ReadOnlySpan<long> currentKeys,
        int currentCount)
    {
        if (previousCount != currentCount
            || previousCount < 0
            || previousCount > previousKeys.Length
            || currentCount < 0
            || currentCount > currentKeys.Length)
        {
            return false;
        }

        return previousKeys[..previousCount].SequenceEqual(currentKeys[..currentCount]);
    }

    /// <summary>Commits current slot identities and reports whether temporal channels are reusable.</summary>
    /// <param name="lightCount">Number of active point-light slots.</param>
    /// <param name="anchorX">World-space player-origin X used by held-light classification.</param>
    /// <param name="anchorY">World-space player-origin Y used by held-light classification.</param>
    /// <param name="anchorZ">World-space player-origin Z used by held-light classification.</param>
    /// <returns>Whether all current channels retain their previous source identity.</returns>
    private bool UpdateShadowLightSlotHistory(
        int lightCount,
        double anchorX,
        double anchorY,
        double anchorZ)
    {
        bool stableIdentities = shadowLightSlotsInitialized
            && ShadowLightSlotsAreStable(
                previousShadowLightSlotKeys,
                previousShadowLightSlotCount,
                currentShadowLightSlotKeys,
                lightCount);
        bool stablePositions = stableIdentities
            && StabilizeShadowLightPositions(
                previousShadowLightPositions,
                voxelLightPositions,
                voxelLightCasterLayers,
                lightCount,
                0.01f,
                (float)anchorX,
                (float)anchorY,
                (float)anchorZ,
                0.75f);
        Array.Copy(
            currentShadowLightSlotKeys,
            previousShadowLightSlotKeys,
            currentShadowLightSlotKeys.Length);
        for (int index = 0; index < lightCount; index++)
        {
            int packedOffset = index * 4;
            int positionOffset = index * 3;
            previousShadowLightPositions[positionOffset] = voxelLightPositions[packedOffset];
            previousShadowLightPositions[positionOffset + 1] = voxelLightPositions[packedOffset + 1];
            previousShadowLightPositions[positionOffset + 2] = voxelLightPositions[packedOffset + 2];
        }
        previousShadowLightSlotCount = lightCount;
        shadowLightSlotsInitialized = true;
        return stableIdentities && stablePositions;
    }

    /// <summary>Snaps sub-centimetre source jitter and rejects history for genuine emitter movement.</summary>
    /// <param name="previousPositions">Previous packed XYZ positions.</param>
    /// <param name="currentPositions">Current packed XYZI shader positions, modified for stable sources.</param>
    /// <param name="lightCount">Active source count.</param>
    /// <param name="stabilityDistance">Maximum position delta eligible for snapping.</param>
    /// <returns>Whether every active source remained within the stability radius.</returns>
    internal static bool StabilizeShadowLightPositions(
        ReadOnlySpan<float> previousPositions,
        Span<float> currentPositions,
        int lightCount,
        float stabilityDistance)
    {
        return StabilizeShadowLightPositions(
            previousPositions,
            currentPositions,
            [],
            lightCount,
            stabilityDistance,
            0.0f,
            0.0f,
            0.0f,
            0.0f);
    }

    /// <summary>
    /// Snaps real world-space sources while ignoring camera-aligned slots whose
    /// raw shadow channels are intentionally constant and unused by final shading.
    /// </summary>
    /// <param name="previousPositions">Previous packed XYZ positions.</param>
    /// <param name="currentPositions">Current packed XYZI shader positions.</param>
    /// <param name="casterLayers">Per-slot caster layer; negative values identify dynamic sources.</param>
    /// <param name="lightCount">Active source count.</param>
    /// <param name="stabilityDistance">Maximum real-source displacement eligible for snapping.</param>
    /// <param name="anchorX">Current world-space player-origin X.</param>
    /// <param name="anchorY">Current world-space player-origin Y.</param>
    /// <param name="anchorZ">Current world-space player-origin Z.</param>
    /// <param name="cameraAlignedDistance">Maximum source-to-player distance for a shadowless camera slot.</param>
    /// <returns>Whether every shadow-consuming source retained a reusable position.</returns>
    internal static bool StabilizeShadowLightPositions(
        ReadOnlySpan<float> previousPositions,
        Span<float> currentPositions,
        ReadOnlySpan<float> casterLayers,
        int lightCount,
        float stabilityDistance,
        float anchorX,
        float anchorY,
        float anchorZ,
        float cameraAlignedDistance)
    {
        if (lightCount < 0
            || previousPositions.Length < lightCount * 3
            || currentPositions.Length < lightCount * 4
            || (!casterLayers.IsEmpty && casterLayers.Length < lightCount)
            || !float.IsFinite(stabilityDistance)
            || stabilityDistance < 0.0f
            || (!casterLayers.IsEmpty
                && (!float.IsFinite(anchorX)
                    || !float.IsFinite(anchorY)
                    || !float.IsFinite(anchorZ)
                    || !float.IsFinite(cameraAlignedDistance)
                    || cameraAlignedDistance < 0.0f)))
        {
            return false;
        }

        float maximumDistanceSquared = stabilityDistance * stabilityDistance;
        float cameraAlignedDistanceSquared = cameraAlignedDistance * cameraAlignedDistance;
        bool stable = true;
        for (int index = 0; index < lightCount; index++)
        {
            int previousOffset = index * 3;
            int currentOffset = index * 4;
            float cameraDx = currentPositions[currentOffset] - anchorX;
            float cameraDy = currentPositions[currentOffset + 1] - anchorY;
            float cameraDz = currentPositions[currentOffset + 2] - anchorZ;
            float cameraDistanceSquared = cameraDx * cameraDx
                + cameraDy * cameraDy
                + cameraDz * cameraDz;
            bool cameraAlignedShadowless = !casterLayers.IsEmpty
                && casterLayers[index] < -0.5f
                && float.IsFinite(cameraAlignedDistanceSquared)
                && cameraAlignedDistance > 0.0f
                && cameraDistanceSquared < cameraAlignedDistanceSquared;
            if (cameraAlignedShadowless)
            {
                continue;
            }

            float dx = currentPositions[currentOffset] - previousPositions[previousOffset];
            float dy = currentPositions[currentOffset + 1] - previousPositions[previousOffset + 1];
            float dz = currentPositions[currentOffset + 2] - previousPositions[previousOffset + 2];
            float distanceSquared = dx * dx + dy * dy + dz * dz;
            if (!float.IsFinite(distanceSquared) || distanceSquared > maximumDistanceSquared)
            {
                stable = false;
                continue;
            }




        }

        return stable;
    }

    /// <summary>Scores an emitter by intensity/range with distance-squared attenuation for selection only.</summary>
    /// <param name="light">Static emitter.</param>
    /// <param name="cameraPosition">Current world-space camera in blocks.</param>
    /// <param name="pointLightRadius">Configured range in blocks.</param>
    /// <returns>Non-negative relative importance; not radiometric energy.</returns>
    private static float StaticLightSelectionScore(
        VoxelLight light,
        Vec3d cameraPosition,
        float pointLightRadius)
    {
        double dx = light.X - cameraPosition.X;
        double dy = light.Y - cameraPosition.Y;
        double dz = light.Z - cameraPosition.Z;
        double distanceSquared = dx * dx + dy * dy + dz * dz;
        return (float)(pointLightRadius * light.Intensity / (1.0 + distanceSquared * 0.35));
    }

    /// <summary>Converts game view-space point lights to world space and merges them into bounded stable slots.</summary>
    /// <param name="config">Normalized range and strength settings.</param>
    /// <param name="originX">Floating player-origin X in world blocks.</param>
    /// <param name="originY">Floating player-origin Y in world blocks.</param>
    /// <param name="originZ">Floating player-origin Z in world blocks.</param>
    /// <param name="maximumVoxelLights">Total shader slot capacity.</param>
    /// <returns>Populated light count after de-duplication and stable selection.</returns>
    private int CopyDynamicPointLights(
        VintageRtxConfig config,
        double originX,
        double originY,
        double originZ,
        int maximumVoxelLights)
    {
        DefaultShaderUniforms uniforms = api.Render.ShaderUniforms;
        float[] pointLights = uniforms.PointLights3 ?? [];
        float[] pointLightColors = uniforms.PointLightColors3 ?? [];
        int availableCount = Math.Min(
            uniforms.PointLightsCount,
            Math.Min(
                pointLights.Length / 3,
                pointLightColors.Length / 3));
        availableDynamicLightCount = availableCount;
        int candidateCount = 0;
        entityLightCollector ??= new EntityLightCollector(api);
        IReadOnlyList<TrackedEntityLight> entities = entityLightCollector.Collect(originX, originY, originZ, config.PointLightRadius);
        HashSet<int> matched = new();
        Dictionary<int, VoxelLight> entityDefinitions = new();
        foreach (TrackedEntityLight entity in entities) entityDefinitions[entity.SourceIndex] = entity.Light;
        Array.Clear(selectedDynamicLightViewPositions);
        for (int index = 0; index < Math.Min(availableCount, dynamicLightCandidates.Length); index++)
        {
            int offset = index * 3;
            float r = pointLightColors[offset], g = pointLightColors[offset + 1], b = pointLightColors[offset + 2];
            float range = MathF.Sqrt(r * r + g * g + b * b);
            if (!float.IsFinite(range) || range < 0.25f) continue;
            float vx = pointLights[offset], vy = pointLights[offset + 1], vz = pointLights[offset + 2];
            if (!float.IsFinite(vx + vy + vz)) continue;
            TransformViewToWorld(vx, vy, vz, originX, originY, originZ, out float wx, out float wy, out float wz);
            TrackedEntityLight? owner = null;
            double closest = double.PositiveInfinity;
            foreach (TrackedEntityLight entity in entities)
            {
                if (matched.Contains(entity.SourceIndex)) continue;
                VoxelLight light = entity.Light;
                double dx = wx - light.X, dy = wy - light.Y, dz = wz - light.Z;
                double distanceSquared = dx * dx + dy * dy + dz * dz;
                // The public light arrays have no owner IDs. This one-to-one association only
                // substitutes photometry/identity; unmatched entity sources remain explicit.
                double tolerance = entity.LocalPlayer ? 2.0 : 0.65;
                if (distanceSquared < tolerance * tolerance && distanceSquared < closest)
                { closest = distanceSquared; owner = entity; }
            }
            if (owner is TrackedEntityLight known)
            {
                matched.Add(known.SourceIndex);
                AddEntity(known, wx, wy, wz, vx, vy, vz);
            }
            else
            {
                dynamicLightCandidates[candidateCount++] = new DynamicLightCandidate(index, vx, vy, vz, wx, wy, wz,
                    r / range, g / range, b / range, Math.Min(range, config.PointLightRadius),
                    EmitterPhotometry.CandelaAtCutoffRange(range, EmitterPhotometry.DefaultCutoffIlluminanceLux),
                    range / (1f + (vx * vx + vy * vy + vz * vz) * 0.35f));
            }
        }
        foreach (TrackedEntityLight entity in entities.OrderByDescending(e => e.Light.Intensity /
            (1.0 + Math.Pow(e.Light.X - originX, 2) + Math.Pow(e.Light.Y - originY, 2) + Math.Pow(e.Light.Z - originZ, 2))))
        {
            if (matched.Contains(entity.SourceIndex) || candidateCount >= dynamicLightCandidates.Length) continue;
            VoxelLight light = entity.Light;
            float x = (float)(light.X - originX), y = (float)(light.Y - originY), z = (float)(light.Z - originZ);
            float vx = viewMatrix[0] * x + viewMatrix[4] * y + viewMatrix[8] * z + viewMatrix[12];
            float vy = viewMatrix[1] * x + viewMatrix[5] * y + viewMatrix[9] * z + viewMatrix[13];
            float vz = viewMatrix[2] * x + viewMatrix[6] * y + viewMatrix[10] * z + viewMatrix[14];
            AddEntity(entity, light.X, light.Y, light.Z, vx, vy, vz);
        }
        availableDynamicLightCount = candidateCount;

        void AddEntity(TrackedEntityLight entity, float x, float y, float z, float vx, float vy, float vz)
        {
            VoxelLight light = entity.Light;
            float range = Math.Min(light.TraceRadiusMetres(), config.PointLightRadius);
            dynamicLightCandidates[candidateCount++] = new DynamicLightCandidate(entity.SourceIndex,
                vx, vy, vz, x, y, z, light.Red, light.Green, light.Blue, range, light.Intensity,
                range * light.Intensity / (1f + (vx * vx + vy * vy + vz * vz) * 0.35f));
        }

        Span<int> candidateSourceIndices = stackalloc int[64];
        Span<float> candidateScores = stackalloc float[64];
        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            candidateSourceIndices[candidateIndex] = dynamicLightCandidates[candidateIndex].SourceIndex;
            candidateScores[candidateIndex] = dynamicLightCandidates[candidateIndex].Score;
        }
        Span<int> selectedCandidateIndices = stackalloc int[VoxelScene.MaximumLightCount];
        int copiedCount = SelectStableLightCandidates(
            candidateSourceIndices[..candidateCount],
            candidateScores[..candidateCount],
            lastSelectedDynamicSourceIndices.AsSpan(0, lastSelectedDynamicSourceCount),
            selectedCandidateIndices[..Math.Min(maximumVoxelLights, VoxelScene.MaximumLightCount)]);
        for (int copiedIndex = 0; copiedIndex < copiedCount; copiedIndex++)
        {
            DynamicLightCandidate candidate = dynamicLightCandidates[selectedCandidateIndices[copiedIndex]];
            int copiedOffset = copiedIndex * 4;
            voxelLightPositions[copiedOffset] = candidate.WorldX;
            voxelLightPositions[copiedOffset + 1] = candidate.WorldY;
            voxelLightPositions[copiedOffset + 2] = candidate.WorldZ;
            voxelLightPositions[copiedOffset + 3] = candidate.IntensityCandela;
            voxelLightColors[copiedOffset] = candidate.Red;
            voxelLightColors[copiedOffset + 1] = candidate.Green;
            voxelLightColors[copiedOffset + 2] = candidate.Blue;
            voxelLightColors[copiedOffset + 3] = candidate.Range;
            voxelLightPhotometry[copiedOffset] = config.PointLightSourceRadius;
            voxelLightPhotometry[copiedOffset + 1] = config.PointLightSourceRadius;
            voxelLightPhotometry[copiedOffset + 2] =
                EmitterPhotometry.DefaultCutoffIlluminanceLux;
            voxelLightPhotometry[copiedOffset + 3] = 0.0f;
            if (entityDefinitions.TryGetValue(candidate.SourceIndex, out VoxelLight definition))
            {
                voxelLightPhotometry[copiedOffset] = definition.SourceHalfWidthMetres;
                voxelLightPhotometry[copiedOffset + 1] = definition.SourceHalfHeightMetres;
                voxelLightPhotometry[copiedOffset + 2] = definition.CutoffIlluminanceLux;
                voxelLightPhotometry[copiedOffset + 3] = 1.0f;
            }
            voxelLightSelectionScores[copiedIndex] = candidate.Score;
            currentShadowLightSlotKeys[copiedIndex] = DynamicShadowLightSlotKey(
                candidate.SourceIndex);
            int selectedViewOffset = copiedIndex * 3;
            selectedDynamicLightViewPositions[selectedViewOffset] = candidate.ViewX;
            selectedDynamicLightViewPositions[selectedViewOffset + 1] = candidate.ViewY;
            selectedDynamicLightViewPositions[selectedViewOffset + 2] = candidate.ViewZ;
        }
        Array.Fill(lastSelectedDynamicSourceIndices, -1);
        for (int index = 0; index < copiedCount; index++)
        {
            lastSelectedDynamicSourceIndices[index] =
                dynamicLightCandidates[selectedCandidateIndices[index]].SourceIndex;
        }
        lastSelectedDynamicSourceCount = copiedCount;
        int primarySourceIndex = copiedCount > 0
            ? lastSelectedDynamicSourceIndices[0]
            : -1;

        if (copiedCount != lastDynamicLightCount
            || primarySourceIndex != lastPrimaryDynamicLightSourceIndex)
        {
            api.Logger.Notification(
                "[VintageRTX] Dynamic point lights tracked: {0} (held items and engine lights included).",
                copiedCount);
            if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID")))
            {
                for (int index = 0; index < copiedCount; index++)
                {
                    int offset = index * 4;
                    api.Logger.Notification(
                        "[VintageRTX.Test] Dynamic light {0}: view=({1:0.00},{2:0.00},{3:0.00}), world=({4:0.00},{5:0.00},{6:0.00}), radius={7:0.00}, intensity={8:0.00} cd, source={9:0.000}x{10:0.000} m.",
                        index,
                        selectedDynamicLightViewPositions[index * 3],
                        selectedDynamicLightViewPositions[index * 3 + 1],
                        selectedDynamicLightViewPositions[index * 3 + 2],
                        voxelLightPositions[offset],
                        voxelLightPositions[offset + 1],
                        voxelLightPositions[offset + 2],
                        voxelLightColors[offset + 3],
                        voxelLightPositions[offset + 3],
                        voxelLightPhotometry[offset] * 2.0f,
                        voxelLightPhotometry[offset + 1] * 2.0f);
                }
            }
            lastDynamicLightCount = copiedCount;
            lastPrimaryDynamicLightSourceIndex = primarySourceIndex;
        }

        return copiedCount;
    }

    /// <summary>
    /// Selects highest-scoring bounded candidates while retaining previously selected source IDs on
    /// near ties, preventing frame-to-frame flicker when many lights compete for the same slots.
    /// </summary>
    /// <param name="sourceIndices">Stable game/source identifiers.</param>
    /// <param name="scores">Selection importance parallel to <paramref name="sourceIndices"/>.</param>
    /// <param name="previousSourceIndices">Previous-frame selected source identifiers.</param>
    /// <param name="selectedCandidateIndices">Destination indices into the candidate spans.</param>
    /// <returns>Number of destination entries written.</returns>
    internal static int SelectStableLightCandidates(
        ReadOnlySpan<int> sourceIndices,
        ReadOnlySpan<float> scores,
        ReadOnlySpan<int> previousSourceIndices,
        Span<int> selectedCandidateIndices)
    {
        if (sourceIndices.Length != scores.Length
            || sourceIndices.Length > 64
            || selectedCandidateIndices.Length > VoxelScene.MaximumLightCount)
        {
            throw new ArgumentException(
                "Light candidates must have equal lengths and respect the 64/8 source budgets.");
        }

        int targetCount = Math.Min(sourceIndices.Length, selectedCandidateIndices.Length);
        Span<bool> chosen = stackalloc bool[64];
        Span<bool> emitted = stackalloc bool[64];
        Span<int> ranked = stackalloc int[VoxelScene.MaximumLightCount];
        int rankedCount = 0;
        while (rankedCount < targetCount)
        {
            int bestIndex = -1;
            float bestScore = float.NegativeInfinity;
            int bestSourceIndex = int.MaxValue;
            for (int candidateIndex = 0; candidateIndex < sourceIndices.Length; candidateIndex++)
            {
                if (chosen[candidateIndex])
                {
                    continue;
                }

                int sourceIndex = sourceIndices[candidateIndex];
                bool retained = previousSourceIndices.IndexOf(sourceIndex) >= 0;
                float stableScore = scores[candidateIndex] * (retained ? 1.08f : 1.0f);
                if (stableScore > bestScore + 0.000001f
                    || (MathF.Abs(stableScore - bestScore) <= 0.000001f
                        && sourceIndex < bestSourceIndex))
                {
                    bestIndex = candidateIndex;
                    bestScore = stableScore;
                    bestSourceIndex = sourceIndex;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            chosen[bestIndex] = true;
            ranked[rankedCount++] = bestIndex;
        }

        int written = 0;
        for (int previousIndex = 0; previousIndex < previousSourceIndices.Length; previousIndex++)
        {
            int previousSourceIndex = previousSourceIndices[previousIndex];
            for (int rankedIndex = 0; rankedIndex < rankedCount; rankedIndex++)
            {
                int candidateIndex = ranked[rankedIndex];
                if (!emitted[candidateIndex]
                    && sourceIndices[candidateIndex] == previousSourceIndex)
                {
                    selectedCandidateIndices[written++] = candidateIndex;
                    emitted[candidateIndex] = true;
                    break;
                }
            }
        }

        for (int rankedIndex = 0; rankedIndex < rankedCount; rankedIndex++)
        {
            int candidateIndex = ranked[rankedIndex];
            if (!emitted[candidateIndex])
            {
                selectedCandidateIndices[written++] = candidateIndex;
            }
        }

        return written;
    }

    /// <summary>Separates potential, visible direct, and blocked energy without cross-light averaging.</summary>
    /// <param name="potentialEnergy">Non-negative unoccluded energy per independent source.</param>
    /// <param name="visibility">Per-source visibility fractions in 0..1.</param>
    /// <returns>Energy totals and potential-weighted aggregate visibility.</returns>
    internal static IndependentLightEnergy AccumulateIndependentLightEnergy(
        ReadOnlySpan<float> potentialEnergy,
        ReadOnlySpan<float> visibility)
    {
        if (potentialEnergy.Length != visibility.Length)
        {
            throw new ArgumentException("Potential and visibility arrays must have equal lengths.");
        }

        float potential = 0.0f;
        float direct = 0.0f;
        float blocked = 0.0f;
        for (int index = 0; index < potentialEnergy.Length; index++)
        {
            float sourcePotential = Math.Max(potentialEnergy[index], 0.0f);
            float sourceVisibility = Math.Clamp(visibility[index], 0.0f, 1.0f);
            potential += sourcePotential;
            direct += sourcePotential * sourceVisibility;
            blocked += sourcePotential * (1.0f - sourceVisibility);
        }

        return new IndependentLightEnergy(
            potential,
            direct,
            blocked,
            potential > 0.0001f ? direct / potential : 1.0f);
    }

    /// <summary>Transforms a view-space point through inverse model-view and restores the floating player origin.</summary>
    /// <param name="x">View-space X.</param>
    /// <param name="y">View-space Y.</param>
    /// <param name="z">View-space Z.</param>
    /// <param name="originX">Floating player-origin X in blocks.</param>
    /// <param name="originY">Floating player-origin Y in blocks.</param>
    /// <param name="originZ">Floating player-origin Z in blocks.</param>
    /// <param name="worldX">Resulting world X.</param>
    /// <param name="worldY">Resulting world Y.</param>
    /// <param name="worldZ">Resulting world Z.</param>
    private void TransformViewToWorld(
        float x,
        float y,
        float z,
        double originX,
        double originY,
        double originZ,
        out float worldX,
        out float worldY,
        out float worldZ)
    {
        // Vintage Story exposes PointLights3 in the same camera/view space used
        // by fogandlight.vsh. Reuse the inverse CameraMatrixOrigin transform used
        // by the fullscreen shader, then restore the floating world origin.
        double worldOffsetX = inverseViewMatrixDouble[0] * x
            + inverseViewMatrixDouble[4] * y
            + inverseViewMatrixDouble[8] * z
            + inverseViewMatrixDouble[12];
        double worldOffsetY = inverseViewMatrixDouble[1] * x
            + inverseViewMatrixDouble[5] * y
            + inverseViewMatrixDouble[9] * z
            + inverseViewMatrixDouble[13];
        double worldOffsetZ = inverseViewMatrixDouble[2] * x
            + inverseViewMatrixDouble[6] * y
            + inverseViewMatrixDouble[10] * z
            + inverseViewMatrixDouble[14];
        worldX = (float)(originX + worldOffsetX);
        worldY = (float)(originY + worldOffsetY);
        worldZ = (float)(originZ + worldOffsetZ);
    }

    /// <summary>
    /// One decoded game point light with stable ID, view/world positions, colour, physical intensity,
    /// bounded trace range, and selection score.
    /// </summary>
    private readonly record struct DynamicLightCandidate(
        int SourceIndex,
        float ViewX,
        float ViewY,
        float ViewZ,
        float WorldX,
        float WorldY,
        float WorldZ,
        float Red,
        float Green,
        float Blue,
        float Range,
        float IntensityCandela,
        float Score);

    /// <summary>Energy-conserving diagnostic decomposition across independent shadow-casting sources.</summary>
    internal readonly record struct IndependentLightEnergy(
        float Potential,
        float Direct,
        float Blocked,
        float Visibility);

    /// <summary>Binds normalized solar direction/color and hybrid trace range/enable state.</summary>
    /// <param name="config">Normalized sun strength, range, and enable settings.</param>
    /// <param name="voxelLightingEnabled">Whether occupancy data is valid for long-range visibility.</param>
    /// <param name="cameraPosition">World-space camera position reconstructed from the same rendered-frame origin as the G-buffer.</param>
    private void BindSunUniforms(
        VintageRtxConfig config,
        bool voxelLightingEnabled,
        Vec3d cameraPosition)
    {
        if (!sunConfigurationLogged)
        {
            api.Logger.Notification(
                "[VintageRTX] Hybrid sun trace configured: sun range={0:0} blocks, detailed occupancy={1}x.",
                config.SunShadowDistance,
                VoxelScene.OccupancyScale);
            sunConfigurationLogged = true;
        }

        IGameCalendar worldCalendar = api.World.Calendar;
        IClientGameCalendar? calendar = worldCalendar as IClientGameCalendar;
        Vec3f sunDirection = VoxelScene.ResolveSunDirection(worldCalendar, cameraPosition);
        float directionLength = MathF.Sqrt(
            sunDirection.X * sunDirection.X
            + sunDirection.Y * sunDirection.Y
            + sunDirection.Z * sunDirection.Z);
        if (directionLength > 0.0001f)
        {
            float inverseLength = 1.0f / directionLength;
            shader!.Uniform(
                "sunDirection",
                sunDirection.X * inverseLength,
                sunDirection.Y * inverseLength,
                sunDirection.Z * inverseLength);
        }
        else
        {
            shader!.Uniform("sunDirection", 0.0f, 1.0f, 0.0f);
        }

        Vec3f sunColor = calendar?.SunColor ?? new Vec3f(1.0f, 0.95f, 0.85f);
        // The coordinate-aware API recomputes the astronomical term after an
        // automated time jump. IClientGameCalendar.DayLightStrength can retain
        // the pre-jump value on the first frame of a freshly created world.
        float daylightStrength = calendar?.GetDayLightStrength(
            cameraPosition.X,
            cameraPosition.Z) ?? 0.0f;
        shader.Uniform(
            "sunColorStrength",
            sunColor.X,
            sunColor.Y,
            sunColor.Z,
            voxelLightingEnabled && config.SunShadowsEnabled ? daylightStrength : 0.0f);
    }

    /// <summary>Deletes all owned GL objects and renderer helpers; borrowed game resources remain untouched.</summary>
    public void Dispose()
    {
        if (preFinalDiagnosticRegistered)
        {
            api.Event.UnregisterRenderer(this, EnumRenderStage.AfterPostProcessing);
            preFinalDiagnosticRegistered = false;
        }
        if (cameraOriginCaptureRegistered)
        {
            api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
            cameraOriginCaptureRegistered = false;
        }
        if (nativeSunShadowCaptureRegistered)
        {
            api.Event.UnregisterRenderer(this, EnumRenderStage.ShadowFar);
            api.Event.UnregisterRenderer(this, EnumRenderStage.ShadowNear);
            nativeSunShadowCaptureRegistered = false;
        }

        if (sceneCopyTexture != 0)
        {
            GL.DeleteTexture(sceneCopyTexture);
            sceneCopyTexture = 0;
        }

        if (fullscreenVertexArray != 0)
        {
            GL.DeleteVertexArray(fullscreenVertexArray);
            fullscreenVertexArray = 0;
        }

        if (voxelTexture != 0)
        {
            GL.DeleteTexture(voxelTexture);
            voxelTexture = 0;
        }
        if (voxelOccupancyTexture != 0)
        {
            GL.DeleteTexture(voxelOccupancyTexture);
            voxelOccupancyTexture = 0;
        }
        if (voxelLightCasterTexture != 0)
        {
            GL.DeleteTexture(voxelLightCasterTexture);
            voxelLightCasterTexture = 0;
        }
        if (voxelFluidSurfaceTexture != 0)
        {
            GL.DeleteTexture(voxelFluidSurfaceTexture);
            voxelFluidSurfaceTexture = 0;
        }
        if (voxelLiquidMetadataTexture != 0)
        {
            GL.DeleteTexture(voxelLiquidMetadataTexture);
            voxelLiquidMetadataTexture = 0;
        }
        if (liquidOpticalProfilesTexture != 0)
        {
            GL.DeleteTexture(liquidOpticalProfilesTexture);
            liquidOpticalProfilesTexture = 0;
        }
        if (voxelIrradianceTexture != 0)
        {
            GL.DeleteTexture(voxelIrradianceTexture);
            voxelIrradianceTexture = 0;
        }
        if (voxelIrradianceDirectionTexture != 0)
        {
            GL.DeleteTexture(voxelIrradianceDirectionTexture);
            voxelIrradianceDirectionTexture = 0;
        }
        if (voxelSunOccupancyTexture != 0)
        {
            GL.DeleteTexture(voxelSunOccupancyTexture);
            voxelSunOccupancyTexture = 0;
        }
        if (voxelRainSurfaceTexture != 0)
        {
            GL.DeleteTexture(voxelRainSurfaceTexture);
            voxelRainSurfaceTexture = 0;
        }
        for (int index = 0; index < temporalHistoryTextures.Length; index++)
        {
            if (temporalHistoryTextures[index] != 0)
            {
                GL.DeleteTexture(temporalHistoryTextures[index]);
                temporalHistoryTextures[index] = 0;
            }
            if (temporalFramebuffers[index] != 0)
            {
                GL.DeleteFramebuffer(temporalFramebuffers[index]);
                temporalFramebuffers[index] = 0;
            }
        }
        if (shadowCurrentPointATexture != 0)
        {
            GL.DeleteTexture(shadowCurrentPointATexture);
            shadowCurrentPointATexture = 0;
        }
        if (shadowCurrentPointBTexture != 0)
        {
            GL.DeleteTexture(shadowCurrentPointBTexture);
            shadowCurrentPointBTexture = 0;
        }
        if (shadowCurrentSunTexture != 0)
        {
            GL.DeleteTexture(shadowCurrentSunTexture);
            shadowCurrentSunTexture = 0;
        }
        if (shadowCurrentFramebuffer != 0)
        {
            GL.DeleteFramebuffer(shadowCurrentFramebuffer);
            shadowCurrentFramebuffer = 0;
        }
        for (int index = 0; index < shadowHistoryPointATextures.Length; index++)
        {
            if (shadowHistoryPointATextures[index] != 0)
            {
                GL.DeleteTexture(shadowHistoryPointATextures[index]);
                shadowHistoryPointATextures[index] = 0;
            }
            if (shadowHistoryPointBTextures[index] != 0)
            {
                GL.DeleteTexture(shadowHistoryPointBTextures[index]);
                shadowHistoryPointBTextures[index] = 0;
            }
            if (shadowHistorySunTextures[index] != 0)
            {
                GL.DeleteTexture(shadowHistorySunTextures[index]);
                shadowHistorySunTextures[index] = 0;
            }
            if (shadowHistoryFramebuffers[index] != 0)
            {
                GL.DeleteFramebuffer(shadowHistoryFramebuffers[index]);
                shadowHistoryFramebuffers[index] = 0;
            }
        }

        shader = null;
        lumaBridge.Dispose();
        FenceStackAwareTessellationGuardPatch.Uninstall();
        ProjectileLiquidCollisionPatch.Uninstall();
        liquidSurfaceRuntime.Dispose();
        EntityMirrorGeometryReplayPatch.Uninstall();
        reflectionSourceCapture.Dispose();
        entityMirrorSourceCapture.Dispose();
        entityMirrorProjection.Dispose();
        dynamicLiquidSurfaceBinding = default;
        pendingVoxelSnapshot = default;
        pendingVoxelSnapshotAvailable = false;
        voxelSnapshot = default;
        voxelTextureReady = false;
        performanceMonitor.Dispose();
        Array.Clear(previousShadowLightSlotKeys);
        shadowLightSlotsInitialized = false;
        previousShadowLightSlotCount = 0;
        currentVoxelLightCount = 0;
        initialized = false;
    }

    /// <summary>
    /// OpenGL state shared across renderer callbacks: framebuffer/program/VAO, viewport/scissor,
    /// colour/depth writes, capabilities, active unit, and draw/read buffers. Texture-unit contents
    /// are intentionally not queried because every following engine consumer binds its samplers.
    /// </summary>
    private readonly record struct GlState(
        bool DepthTest,
        bool DepthWrite,
        int DepthFunctionValue,
        bool Blend,
        bool CullFace,
        bool FramebufferSrgb,
        bool ScissorTest,
        bool ProgramPointSize,
        int ActiveTexture,
        int VertexArray,
        int Program,
        int ReadFramebuffer,
        int DrawFramebuffer,
        int ReadBuffer,
        DrawBuffersEnum[] DrawBuffers,
        int[] Viewport,
        int[] ScissorBox,
        bool[] ColorMask)
    {
        /// <summary>Snapshots mutable GL state before VintageRTX binds any pass resource.</summary>
        /// <returns>Value object that can restore every captured binding/capability.</returns>
        public static GlState Capture()
        {
            GL.GetInteger(GetPName.ActiveTexture, out int activeTexture);
            GL.GetInteger(GetPName.VertexArrayBinding, out int vertexArray);
            GL.GetInteger(GetPName.CurrentProgram, out int program);
            GL.GetInteger(GetPName.ReadFramebufferBinding, out int readFramebuffer);
            GL.GetInteger(GetPName.DrawFramebufferBinding, out int drawFramebuffer);
            GL.GetInteger(GetPName.ReadBuffer, out int readBuffer);
            GL.GetInteger(GetPName.MaxDrawBuffers, out int maximumDrawBuffers);
            maximumDrawBuffers = Math.Clamp(maximumDrawBuffers, 1, 16);
            DrawBuffersEnum[] drawBuffers = new DrawBuffersEnum[maximumDrawBuffers];
            for (int index = 0; index < drawBuffers.Length; index++)
            {
                GL.GetInteger(
                    (GetPName)((int)GetPName.DrawBuffer0 + index),
                    out int drawBuffer);
                drawBuffers[index] = (DrawBuffersEnum)drawBuffer;
            }
            int activeDrawBufferCount = 1;
            for (int index = drawBuffers.Length - 1; index >= 1; index--)
            {
                if ((int)drawBuffers[index] != 0)
                {
                    activeDrawBufferCount = index + 1;
                    break;
                }
            }
            Array.Resize(ref drawBuffers, activeDrawBufferCount);
            GL.GetBoolean(GetPName.DepthWritemask, out bool depthWrite);
            GL.GetInteger(GetPName.DepthFunc, out int depthFunction);
            int[] viewport = new int[4];
            int[] scissorBox = new int[4];
            bool[] colorMask = new bool[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            GL.GetInteger(GetPName.ScissorBox, scissorBox);
            GL.GetBoolean(GetPName.ColorWritemask, colorMask);

            return new GlState(
                GL.IsEnabled(EnableCap.DepthTest),
                depthWrite,
                depthFunction,
                GL.IsEnabled(EnableCap.Blend),
                GL.IsEnabled(EnableCap.CullFace),
                GL.IsEnabled(EnableCap.FramebufferSrgb),
                GL.IsEnabled(EnableCap.ScissorTest),
                GL.IsEnabled(EnableCap.ProgramPointSize),
                activeTexture,
                vertexArray,
                program,
                readFramebuffer,
                drawFramebuffer,
                readBuffer,
                drawBuffers,
                viewport,
                scissorBox,
                colorMask);
        }

        /// <summary>Restores shared callback state in deterministic order and the original active unit.</summary>
        public void Restore()
        {
            SetEnabled(EnableCap.DepthTest, DepthTest);
            GL.DepthMask(DepthWrite);
            GL.DepthFunc((DepthFunction)DepthFunctionValue);
            SetEnabled(EnableCap.Blend, Blend);
            SetEnabled(EnableCap.CullFace, CullFace);
            SetEnabled(EnableCap.FramebufferSrgb, FramebufferSrgb);
            SetEnabled(EnableCap.ScissorTest, ScissorTest);
            SetEnabled(EnableCap.ProgramPointSize, ProgramPointSize);
            GL.Viewport(Viewport[0], Viewport[1], Viewport[2], Viewport[3]);
            GL.Scissor(ScissorBox[0], ScissorBox[1], ScissorBox[2], ScissorBox[3]);
            GL.ColorMask(ColorMask[0], ColorMask[1], ColorMask[2], ColorMask[3]);
            GL.BindVertexArray(VertexArray);
            GL.UseProgram(Program);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, ReadFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, DrawFramebuffer);
            GL.ReadBuffer((ReadBufferMode)ReadBuffer);
            GL.DrawBuffers(DrawBuffers.Length, DrawBuffers);
            GL.ActiveTexture((TextureUnit)ActiveTexture);
        }

        /// <summary>Restores one OpenGL capability without assuming its prior state.</summary>
        /// <param name="capability">Capability to toggle.</param>
        /// <param name="enabled">Captured enable state.</param>
        private static void SetEnabled(EnableCap capability, bool enabled)
        {
            if (enabled)
            {
                GL.Enable(capability);
            }
            else
            {
                GL.Disable(capability);
            }
        }
    }

    /// <summary>
    /// Reads either the full-size reflection source or the half-size entity-only evidence before the
    /// official final shader can add bloom and first-person geometry. Failure restarts only the
    /// evidence transaction and never disables rendering, because the transport frame remains valid.
    /// </summary>
    /// <param name="state">Callback state whose read framebuffer and buffer must be restored.</param>
    /// <param name="width">Intermediate target width in pixels.</param>
    /// <param name="height">Intermediate target height in pixels.</param>
    /// <param name="debugView">Diagnostic selecting the full reflection source or entity-only carrier.</param>
    /// <param name="entityMirrorReady">Whether this frame completed the entity-mirror projection.</param>
    private void CaptureRawPreFinalDiagnostic(
        GlState state,
        int width,
        int height,
        VintageRtxDebugView debugView,
        bool entityMirrorReady)
    {
        if (!activeCaptureStepPending)
        {
            return;
        }

        try
        {
            bool captureEntityMirror = debugView == VintageRtxDebugView.EntityMirror;
            if (captureEntityMirror
                && (!entityMirrorReady || !entityMirrorProjection.IsAllocated))
            {
                throw new InvalidOperationException(
                    "The entity-only mirror target is unavailable for raw readback.");
            }

            int diagnosticFramebuffer = captureEntityMirror
                ? entityMirrorProjection.EntityEvidenceFramebufferId
                : lumaBridge.FramebufferId;
            int diagnosticWidth = captureEntityMirror ? entityMirrorProjection.Width : width;
            int diagnosticHeight = captureEntityMirror ? entityMirrorProjection.Height : height;
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, diagnosticFramebuffer);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            byte[] pixels = FrameCaptureService.ReadCurrentFrame(
                diagnosticWidth,
                diagnosticHeight);
            if (!captureService.SubmitPreFinalDiagnostic(
                    activeCaptureStep,
                    pixels,
                    diagnosticWidth,
                    diagnosticHeight))
            {
                _ = captureService.RestartCaptureTransaction();
                activeCaptureStepPending = false;
                api.Logger.Warning(
                    "[VintageRTX] Raw pre-final diagnostic capture was rejected and restarted.");
            }
        }
        catch (Exception exception)
        {
            _ = captureService.RestartCaptureTransaction();
            activeCaptureStepPending = false;
            api.Logger.Warning(
                "[VintageRTX] Raw pre-final diagnostic capture restarted after readback failure: {0}",
                exception.Message);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, state.ReadFramebuffer);
            GL.ReadBuffer((ReadBufferMode)state.ReadBuffer);
        }
    }

    /// <summary>
    /// Detects the performance-tier multi-light cluster without dereferencing
    /// the default voxel snapshot before its first asynchronous upload.
    /// </summary>
    /// <param name="qualityLevel">Current adaptive quality tier.</param>
    /// <param name="dynamicLightCount">Engine point lights visible before bounded selection.</param>
    /// <param name="staticLights">Voxel-scene lights, or <see langword="null"/> before scene readiness.</param>
    /// <returns>Whether expensive reflection and bounce work should yield to a dense light cluster.</returns>
    private static bool IsDenseMultiLightCluster(
        int qualityLevel,
        int dynamicLightCount,
        VoxelLight[]? staticLights)
    {
        return qualityLevel == 2
            && (dynamicLightCount > 1 || staticLights is { Length: > 1 });
    }
}

/// <summary>Borrowed native solar depth texture identifiers used to restore exact alpha silhouettes.</summary>
/// <param name="FarTextureId">Far cascaded shadow-map depth texture, or zero.</param>
/// <param name="NearTextureId">Near cascaded shadow-map depth texture, or zero.</param>
internal readonly record struct NativeSunShadowDepthMaps(
    int FarTextureId,
    int NearTextureId);

/// <summary>Paired A/B benchmark phases used to isolate effect cost from drift.</summary>
internal enum BenchmarkPhase
{
    /// <summary>Waiting for world age, renderer readiness, captures, and stable camera.</summary>
    Waiting,
    /// <summary>First no-effect measurement before any effect sampling.</summary>
    BaselineFirst,
    /// <summary>Effect enabled while GPU queues, caches, and temporal history settle.</summary>
    EffectWarmup,
    /// <summary>Effect-enabled measurement window.</summary>
    Effect,
    /// <summary>Effect disabled while the second baseline settles.</summary>
    BaselineSecondWarmup,
    /// <summary>Second no-effect measurement used to bracket drift.</summary>
    BaselineSecond,
    /// <summary>Results were logged and no further automatic transitions occur.</summary>
    Complete
}
