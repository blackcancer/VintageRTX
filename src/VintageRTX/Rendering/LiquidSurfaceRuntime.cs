using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>
/// Bridges immutable voxel/liquid snapshots, live Vintage Story weather/entities, the deterministic
/// SI surface solver, and its RGBA32F GPU upload. A two-cells-per-block grid covers the same
/// 64-by-64 world footprint as the material clipmap so impacts and wakes remain sub-block local.
/// </summary>
internal sealed class LiquidSurfaceRuntime : IDisposable
{
    /// <summary>Maximum horizontal/vertical entity query radius around the camera, in blocks.</summary>
    private const float EntityQueryRadiusBlocks = 40.0f;
    /// <summary>Entity observations per real second; the solver itself remains fixed at 120 Hz.</summary>
    private const float EntityObservationPeriodSeconds = 1.0f / 30.0f;
    /// <summary>
    /// Wind and climate sampling period. Twenty hertz is faster than the authored weather response
    /// while avoiding two mutable world queries on every render frame.
    /// </summary>
    private const float EnvironmentObservationPeriodSeconds = 1.0f / 20.0f;
    /// <summary>Maximum live entities copied per observation to bound render-thread work.</summary>
    private const int MaximumObservedEntities = 256;
    /// <summary>Maximum exact projectile contacts retained between two render updates.</summary>
    private const int MaximumPendingProjectileCollisions = 64;
    /// <summary>Frames aggregated before one opt-in runtime timing report is emitted.</summary>
    private const int TimingReportFrameCount = 300;

    private readonly ICoreClientAPI api;
    private readonly LiquidSurfaceWorldInputs worldInputs = new();
    private readonly LiquidSurfaceGpuUploader uploader;
    private readonly bool timingTelemetryEnabled;
    private LiquidEntitySurfaceSample[] entitySamples = new LiquidEntitySurfaceSample[32];
    /// <summary>Callback-side fixed buffer awaiting the next renderer update.</summary>
    private LiquidProjectileCollisionSample[] pendingProjectileCollisions =
        new LiquidProjectileCollisionSample[MaximumPendingProjectileCollisions];
    /// <summary>Renderer-side fixed buffer swapped with the callback buffer during a drain.</summary>
    private LiquidProjectileCollisionSample[] consumedProjectileCollisions =
        new LiquidProjectileCollisionSample[MaximumPendingProjectileCollisions];
    /// <summary>Protects the bounded callback buffers if a mod performs physics off-thread.</summary>
    private readonly object projectileCollisionGate = new();
    private bool[] configuredSurfaceCells = [];
    private LiquidSurfaceSimulation? simulation;
    private int configuredGeneration;
    private float entityObservationAccumulator = EntityObservationPeriodSeconds;
    private float environmentObservationAccumulator = EnvironmentObservationPeriodSeconds;
    private LiquidSurfaceForcing sampledForcing;
    private bool sampledForcingReady;
    private int loggedDroppedItemImpactCount;
    /// <summary>Latest exact projectile-impact sequence already emitted to opt-in test telemetry.</summary>
    private int loggedProjectileImpactCount;
    /// <summary>Number of initialized samples in <see cref="pendingProjectileCollisions"/>.</summary>
    private int pendingProjectileCollisionCount;
    /// <summary>Number of exact contacts discarded because the bounded callback queue was full.</summary>
    private int droppedProjectileCollisionCount;
    private int timingFrameCount;
    private int timingUploadCount;
    private int timingFixedStepCount;
    private long timingInputTicks;
    private long timingSimulationTicks;
    private long timingPackingTicks;
    private long timingDriverUploadTicks;
    private long timingMaximumSimulationTicks;
    private long timingMaximumDriverUploadTicks;
    private bool disposed;

    /// <summary>Creates the live bridge with a production OpenGL uploader.</summary>
    /// <param name="api">Client world, block, climate, entity, and camera services.</param>
    public LiquidSurfaceRuntime(ICoreClientAPI api)
        : this(api, new LiquidSurfaceGpuUploader())
    {
    }

    /// <summary>Creates the bridge with an injected uploader for deterministic tests.</summary>
    /// <param name="api">Client world services.</param>
    /// <param name="uploader">Owned uploader disposed with this bridge.</param>
    internal LiquidSurfaceRuntime(ICoreClientAPI api, LiquidSurfaceGpuUploader uploader)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        timingTelemetryEnabled = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID"));
    }

    /// <summary>Gets the most recent successful GPU binding, or default before scene readiness.</summary>
    public LiquidSurfaceGpuBinding Current => uploader.Current;

    /// <summary>Gets the configured solver for diagnostics and focused tests.</summary>
    internal LiquidSurfaceSimulation? Simulation => simulation;

    /// <summary>
    /// Gets the latest finite weather/wind sample shared by both the CPU solver and display shader.
    /// </summary>
    internal LiquidSurfaceForcing CurrentForcing => sampledForcing;

    /// <summary>Gets the number of real dropped-item entries accepted by the active surface solver.</summary>
    internal int TotalDroppedItemImpactCount => simulation?.TotalDroppedItemImpactCount ?? 0;

    /// <summary>Gets the most recent physically resolved peak displacement in metres.</summary>
    internal float LastAppliedImpactPeakDisplacement =>
        simulation?.LastDroppedItemImpactPeakDisplacement ?? 0.0f;

    /// <summary>Gets the complete latest dropped-item packet used by the sub-grid renderer.</summary>
    internal LiquidSurfaceImpactDiagnostic LastDroppedItemImpactDiagnostic =>
        simulation?.LastDroppedItemImpactDiagnostic ?? default;

    /// <summary>Gets the latest globally sequenced dropped-item or projectile sub-grid packet.</summary>
    internal LiquidSurfaceSubgridImpactDiagnostic LastSubgridImpactDiagnostic =>
        simulation?.LastSubgridImpactDiagnostic ?? default;

    /// <summary>Gets the latest global sub-grid sequence, resetting with solver reconstruction.</summary>
    internal int TotalSubgridImpactCount => simulation?.TotalSubgridImpactCount ?? 0;

    /// <summary>Copies recent unconsumed packets without allocating in the render loop.</summary>
    /// <param name="afterSequence">Last global sequence already consumed.</param>
    /// <param name="destination">Fixed renderer scratch storage.</param>
    /// <returns>Number of newest packets copied in ascending sequence order.</returns>
    internal int WriteSubgridImpactsAfter(
        int afterSequence,
        Span<LiquidSurfaceSubgridImpactDiagnostic> destination) => simulation is null
            ? 0
            : simulation.WriteSubgridImpactsAfter(afterSequence, destination);

    /// <summary>Gets the number of exact projectile contacts accepted by the active solver.</summary>
    internal int TotalProjectileImpactCount => simulation?.TotalProjectileImpactCount ?? 0;

    /// <summary>Gets exact SI evidence for the latest accepted projectile contact.</summary>
    internal LiquidProjectileImpactDiagnostic LastProjectileImpactDiagnostic =>
        simulation?.LastProjectileImpactDiagnostic ?? default;

    /// <summary>Gets exact projectile contacts discarded by the bounded callback queue.</summary>
    internal int DroppedProjectileCollisionCount => droppedProjectileCollisionCount;

    /// <summary>Finds the active free-surface plane horizontally nearest to a world position.</summary>
    /// <param name="worldX">Reference world X.</param>
    /// <param name="worldZ">Reference world Z.</param>
    /// <param name="worldY">Resolved current surface Y.</param>
    /// <returns>Whether the configured topology contains an active surface.</returns>
    internal bool TryGetNearestSurfaceWorldY(double worldX, double worldZ, out float worldY)
    {
        if (simulation is null)
        {
            worldY = 0.0f;
            return false;
        }

        return simulation.TryGetNearestSurfaceWorldY(worldX, worldZ, out worldY);
    }

    /// <summary>
    /// Rebuilds the surface topology on a new voxel generation, samples live forcing/entities,
    /// advances fixed physics, and uploads only when the state can have changed.
    /// </summary>
    /// <param name="deltaTimeSeconds">Current render-frame duration in seconds.</param>
    /// <param name="snapshot">Last complete voxel snapshot.</param>
    /// <param name="voxelTextureReady">Whether the snapshot and its GPU textures are authoritative.</param>
    /// <returns>Current dynamic texture binding, or default while unavailable.</returns>
    public LiquidSurfaceGpuBinding Update(
        float deltaTimeSeconds,
        in VoxelSceneSnapshot snapshot,
        bool voxelTextureReady)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!voxelTextureReady || snapshot.Generation <= 0)
        {
            return default;
        }

        long inputsStarted = timingTelemetryEnabled ? Stopwatch.GetTimestamp() : 0;
        bool reconfigured = simulation is null || configuredGeneration != snapshot.Generation;
        if (reconfigured)
        {
            Configure(in snapshot);
            sampledForcingReady = false;
            environmentObservationAccumulator = EnvironmentObservationPeriodSeconds;
        }

        LiquidSurfaceSimulation activeSimulation = simulation!;
        float safeDelta = Math.Clamp(
            float.IsFinite(deltaTimeSeconds) ? deltaTimeSeconds : 0.0f,
            0.0f,
            0.10f);
        ConsumeProjectileLiquidCollisions(activeSimulation);
        ObserveEntities(activeSimulation, safeDelta);
        environmentObservationAccumulator += safeDelta;
        if (!sampledForcingReady
            || environmentObservationAccumulator >= EnvironmentObservationPeriodSeconds)
        {
            environmentObservationAccumulator %= EnvironmentObservationPeriodSeconds;
            Vec3d camera = api.World.Player.Entity.CameraPos;
            sampledForcing = worldInputs.SampleEnvironment(
                api.World.BlockAccessor,
                camera.X,
                camera.Y,
                camera.Z);
            sampledForcingReady = true;
        }

        long inputsElapsed = timingTelemetryEnabled
            ? Stopwatch.GetTimestamp() - inputsStarted
            : 0;
        long simulationStarted = timingTelemetryEnabled ? Stopwatch.GetTimestamp() : 0;
        int completedSteps = activeSimulation.Advance(safeDelta, in sampledForcing);
        long simulationElapsed = timingTelemetryEnabled
            ? Stopwatch.GetTimestamp() - simulationStarted
            : 0;
        if (completedSteps > 0
            && activeSimulation.TotalDroppedItemImpactCount > loggedDroppedItemImpactCount
            && timingTelemetryEnabled)
        {
            loggedDroppedItemImpactCount = activeSimulation.TotalDroppedItemImpactCount;
            LiquidSurfaceImpactDiagnostic impact =
                activeSimulation.LastDroppedItemImpactDiagnostic;
            api.Logger.Notification(
                "[VintageRTX.Test] Dropped-item surface impact applied: count={0}, world=({1:0.000},{2:0.000}), cell=({3},{4})/{5}x{6}, uv=({7:0.0000},{8:0.0000}), energy={9:0.0000} J, peak={10:0.0000} m, subgrid={11:0.0000} m, lambda={12:0.000} m, uploaded height={13:0.0000} m, normalXZ=({14:0.0000},{15:0.0000}), impactVelocity=({16:0.000},{17:0.000},{18:0.000}) m/s.",
                activeSimulation.TotalDroppedItemImpactCount,
                impact.WorldX,
                impact.WorldZ,
                impact.CellX,
                impact.CellZ,
                activeSimulation.Width,
                activeSimulation.Depth,
                impact.TextureU,
                impact.TextureV,
                impact.EnergyJoules,
                impact.PeakDisplacement,
                impact.SubgridPeakDisplacement,
                impact.DominantWavelengthMetres,
                impact.UploadedHeight,
                impact.UploadedNormalX,
                impact.UploadedNormalZ,
                impact.DirectionXMetresPerSecond,
                impact.ImpactVelocityYMetresPerSecond,
                impact.DirectionZMetresPerSecond);
        }
        if (completedSteps > 0
            && activeSimulation.TotalProjectileImpactCount > loggedProjectileImpactCount
            && timingTelemetryEnabled)
        {
            loggedProjectileImpactCount = activeSimulation.TotalProjectileImpactCount;
            LiquidProjectileImpactDiagnostic projectile =
                activeSimulation.LastProjectileImpactDiagnostic;
            LiquidSurfaceSubgridImpactDiagnostic packet =
                activeSimulation.LastSubgridImpactDiagnostic;
            bool packetMatchesProjectile = packet.EntityId == projectile.EntityId
                && packet.SourceSequence == projectile.Sequence
                && packet.SurfaceClass == projectile.SurfaceClass;
            api.Logger.Notification(
                "[VintageRTX.Test] Projectile surface impact applied: sequence={0}, entity={1}, class={2}, kind={3}, world=({4:0.000},{5:0.000}), cell=({6},{7})/{8}x{9}, mass={10:0.000} kg, energy={11:0.0000} J, incident=({12:0.000},{13:0.000},{14:0.000}) m/s, outgoing=({15:0.000},{16:0.000},{17:0.000}) m/s. source={18}. subgrid-packet-sequence={19}, surface={20:0.000000} J, resolved={21:0.000000} J, subgrid={22:0.000000} J, amplitude={23:0.000000} m, wavelength={24:0.000000} m, near={25:0.000000} J, cavity={26:0.000000} J, capillary={27:0.000000} J, splash={28:0.000000} J, wake={29:0.000000} J, rendered={30:0.000000} J, local-splash={31:0.000000} J, local-amplitude={32:0.000000} m.",
                projectile.Sequence,
                projectile.EntityId,
                projectile.SurfaceClass,
                projectile.ImpulseKind,
                projectile.WorldX,
                projectile.WorldZ,
                projectile.CellX,
                projectile.CellZ,
                activeSimulation.Width,
                activeSimulation.Depth,
                projectile.MassKilograms,
                projectile.EnergyJoules,
                projectile.IncidentVelocityXMetresPerSecond,
                projectile.IncidentVelocityYMetresPerSecond,
                projectile.IncidentVelocityZMetresPerSecond,
                projectile.OutgoingVelocityXMetresPerSecond,
                projectile.OutgoingVelocityYMetresPerSecond,
                projectile.OutgoingVelocityZMetresPerSecond,
                projectile.IsServerAuthoritative
                    ? "server-authoritative"
                    : "client-remote-motion",
                packetMatchesProjectile ? packet.Sequence : 0,
                projectile.SurfaceCoupledEnergyJoules,
                projectile.ResolvedWaveEnergyJoules,
                projectile.SubgridWaveEnergyJoules,
                packetMatchesProjectile ? packet.PeakDisplacement : 0.0f,
                packetMatchesProjectile ? packet.DominantWavelengthMetres : 0.0f,
                projectile.NearInterfaceEnergyJoules,
                projectile.CavityEnergyJoules,
                projectile.CapillaryPacketEnergyJoules,
                projectile.SplashEnergyJoules,
                projectile.WakeEnergyJoules,
                packetMatchesProjectile ? packet.RenderedPacketEnergyJoules : 0.0f,
                packetMatchesProjectile ? packet.LocalSplashEnergyJoules : 0.0f,
                packetMatchesProjectile ? packet.SplashPeakDisplacement : 0.0f);
        }

        bool requiresUpload = reconfigured
            || completedSteps > 0
            || uploader.Current.TextureId == 0;
        LiquidSurfaceGpuBinding binding = requiresUpload
            ? uploader.Upload(activeSimulation)
            : uploader.Current;
        if (timingTelemetryEnabled)
        {
            RecordTiming(
                inputsElapsed,
                simulationElapsed,
                requiresUpload ? uploader.LastPackingElapsedTicks : 0,
                requiresUpload ? uploader.LastDriverUploadElapsedTicks : 0,
                completedSteps,
                requiresUpload);
        }
        return binding;
    }

    /// <summary>
    /// Accepts one exact client or integrated-server physics callback without touching solver state
    /// from the callback. Fixed storage keeps the bridge allocation-free and safe if a mod performs
    /// physics off-thread.
    /// </summary>
    /// <param name="sample">Detached projectile collision observation.</param>
    internal void ObserveProjectileLiquidCollision(LiquidProjectileCollisionSample sample)
    {
        lock (projectileCollisionGate)
        {
            if (pendingProjectileCollisionCount >= pendingProjectileCollisions.Length)
            {
                droppedProjectileCollisionCount++;
                return;
            }

            pendingProjectileCollisions[pendingProjectileCollisionCount++] = sample;
        }
    }

    /// <summary>Atomically drains callback storage and forwards the batch on the renderer thread.</summary>
    /// <param name="activeSimulation">Configured deterministic surface solver.</param>
    private void ConsumeProjectileLiquidCollisions(LiquidSurfaceSimulation activeSimulation)
    {
        int count;
        lock (projectileCollisionGate)
        {
            count = pendingProjectileCollisionCount;
            if (count == 0)
            {
                return;
            }

            (pendingProjectileCollisions, consumedProjectileCollisions) =
                (consumedProjectileCollisions, pendingProjectileCollisions);
            pendingProjectileCollisionCount = 0;
        }

        ObservePrioritizedProjectileCollisions(
            activeSimulation,
            consumedProjectileCollisions.AsSpan(0, count));
        Array.Clear(consumedProjectileCollisions, 0, count);
    }

    /// <summary>
    /// Processes integrated-server contacts before remote client reconstructions. Harmony is
    /// process-wide in a local game, and the solver's existing entity/contact deduplication then
    /// rejects the later 15 Hz reconstruction instead of allowing it to inflate physical energy.
    /// A remote multiplayer client naturally supplies only the non-authoritative pass.
    /// </summary>
    /// <param name="activeSimulation">Configured renderer-owned surface solver.</param>
    /// <param name="samples">Detached callback batch in arbitrary arrival order.</param>
    internal static void ObservePrioritizedProjectileCollisions(
        LiquidSurfaceSimulation activeSimulation,
        ReadOnlySpan<LiquidProjectileCollisionSample> samples)
    {
        ArgumentNullException.ThrowIfNull(activeSimulation);
        for (int pass = 0; pass < 2; pass++)
        {
            bool serverPass = pass == 0;
            for (int index = 0; index < samples.Length; index++)
            {
                ref readonly LiquidProjectileCollisionSample sample = ref samples[index];
                if (sample.IsServerAuthoritative == serverPass)
                {
                    activeSimulation.ObserveProjectileLiquidCollisions(
                        samples.Slice(index, 1));
                }
            }
        }
    }

    /// <summary>
    /// Aggregates render-thread liquid timings without per-frame logging or allocation and emits
    /// one test-only report separating world inputs, fixed-step simulation, packing, and GL upload.
    /// </summary>
    /// <param name="inputTicks">Snapshot configuration and live input sampling timestamp ticks.</param>
    /// <param name="simulationTicks">Fixed-step solver timestamp ticks.</param>
    /// <param name="packingTicks">RGBA staging timestamp ticks, or zero without an upload.</param>
    /// <param name="driverUploadTicks">Texture API timestamp ticks, or zero without an upload.</param>
    /// <param name="completedSteps">Fixed solver steps completed this frame.</param>
    /// <param name="uploaded">Whether a texture payload was submitted this frame.</param>
    private void RecordTiming(
        long inputTicks,
        long simulationTicks,
        long packingTicks,
        long driverUploadTicks,
        int completedSteps,
        bool uploaded)
    {
        timingFrameCount++;
        timingFixedStepCount += completedSteps;
        timingInputTicks += inputTicks;
        timingSimulationTicks += simulationTicks;
        timingPackingTicks += packingTicks;
        timingDriverUploadTicks += driverUploadTicks;
        timingMaximumSimulationTicks = Math.Max(
            timingMaximumSimulationTicks,
            simulationTicks);
        timingMaximumDriverUploadTicks = Math.Max(
            timingMaximumDriverUploadTicks,
            driverUploadTicks);
        if (uploaded)
        {
            timingUploadCount++;
        }
        if (timingFrameCount < TimingReportFrameCount)
        {
            return;
        }

        double millisecondsPerTick = 1_000.0 / Stopwatch.Frequency;
        api.Logger.Notification(
            "[VintageRTX.Test] Liquid CPU stages | frames={0}, steps={1}, uploads={2}, avg ms inputs={3:0.000}, simulation={4:0.000}, pack={5:0.000}, driver-upload={6:0.000}; max ms simulation={7:0.000}, driver-upload={8:0.000}.",
            timingFrameCount,
            timingFixedStepCount,
            timingUploadCount,
            timingInputTicks * millisecondsPerTick / timingFrameCount,
            timingSimulationTicks * millisecondsPerTick / timingFrameCount,
            timingPackingTicks * millisecondsPerTick / timingFrameCount,
            timingDriverUploadTicks * millisecondsPerTick / timingFrameCount,
            timingMaximumSimulationTicks * millisecondsPerTick,
            timingMaximumDriverUploadTicks * millisecondsPerTick);
        timingFrameCount = 0;
        timingUploadCount = 0;
        timingFixedStepCount = 0;
        timingInputTicks = 0;
        timingSimulationTicks = 0;
        timingPackingTicks = 0;
        timingDriverUploadTicks = 0;
        timingMaximumSimulationTicks = 0;
        timingMaximumDriverUploadTicks = 0;
    }

    /// <summary>Builds free surfaces for world liquids and visible open containers from one snapshot.</summary>
    /// <param name="snapshot">Complete immutable scene generation.</param>
    private void Configure(in VoxelSceneSnapshot snapshot)
    {
        int cellsPerBlock = LiquidSurfaceSimulation.RecommendedCellsPerBlock;
        int gridWidth = snapshot.Width * cellsPerBlock;
        int gridDepth = snapshot.Depth * cellsPerBlock;
        bool topologyReusable = simulation is not null
            && simulation.OriginWorldX == snapshot.OriginX
            && simulation.OriginWorldZ == snapshot.OriginZ
            && simulation.Width == gridWidth
            && simulation.Depth == gridDepth
            && Math.Abs(
                simulation.CellSize - LiquidSurfaceSimulation.RecommendedCellSize) < 0.0001f;
        LiquidSurfaceSimulation configured = topologyReusable
            ? simulation!
            : new LiquidSurfaceSimulation(
                snapshot.OriginX,
                snapshot.OriginZ,
                gridWidth,
                gridDepth,
                LiquidSurfaceSimulation.RecommendedCellSize,
                eventBudget: 512,
                bubbleBudget: 256,
                trackedEntityBudget: MaximumObservedEntities);
        int gridCellCount = checked(gridWidth * gridDepth);
        if (configuredSurfaceCells.Length != gridCellCount)
        {
            configuredSurfaceCells = new bool[gridCellCount];
        }
        else
        {
            Array.Clear(configuredSurfaceCells);
        }

        for (int localZ = 0; localZ < snapshot.Depth; localZ++)
        {
            for (int localX = 0; localX < snapshot.Width; localX++)
            {
                if (!TryResolveColumnSurface(
                        in snapshot,
                        localX,
                        localZ,
                        out float surfaceWorldY,
                        out byte profileId,
                        out float depthMetres)
                    || !TryDecodeProfile(
                        snapshot.LiquidOpticalProfileLookup,
                        profileId,
                        out LiquidSurfaceDynamics dynamics,
                        out LiquidSurfacePhysicalProperties physics))
                {
                    continue;
                }

                bool rainExposed = IsRainExposed(
                    in snapshot,
                    snapshot.OriginX + localX,
                    snapshot.OriginZ + localZ,
                    surfaceWorldY);
                for (int subZ = 0; subZ < cellsPerBlock; subZ++)
                {
                    for (int subX = 0; subX < cellsPerBlock; subX++)
                    {
                        configured.SetSurfaceCell(
                            localX * cellsPerBlock + subX,
                            localZ * cellsPerBlock + subZ,
                            surfaceWorldY,
                            profileId,
                            in dynamics,
                            in physics,
                            rainExposed,
                            depthMetres);
                        int configuredX = localX * cellsPerBlock + subX;
                        int configuredZ = localZ * cellsPerBlock + subZ;
                        configuredSurfaceCells[configuredZ * gridWidth + configuredX] = true;
                    }
                }
            }
        }

        if (topologyReusable)
        {
            // A voxel generation commonly changes materials/lights without
            // moving the clipmap. Preserve active heights, velocities and
            // entity trackers in that case; only cells no longer containing a
            // liquid surface are reset. Recreating the full solver here erased
            // item impacts and visibly flipped the wind field on every rebuild.
            for (int z = 0; z < gridDepth; z++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    if (!configuredSurfaceCells[z * gridWidth + x])
                    {
                        configured.ClearSurfaceCell(x, z);
                    }
                }
            }
        }

        simulation = configured;
        configuredGeneration = snapshot.Generation;
        if (!topologyReusable)
        {
            entityObservationAccumulator = EntityObservationPeriodSeconds;
            loggedDroppedItemImpactCount = 0;
        }
    }

    /// <summary>Finds the highest fluid-layer or visible contained-liquid surface in one column.</summary>
    /// <param name="snapshot">Complete voxel/liquid payload.</param>
    /// <param name="localX">Column X in the main clipmap.</param>
    /// <param name="localZ">Column Z in the main clipmap.</param>
    /// <param name="surfaceWorldY">Resolved absolute surface height in blocks/metres.</param>
    /// <param name="profileId">Resolved optical profile row.</param>
    /// <returns>Whether a non-reserved visible surface exists.</returns>
    internal static bool TryResolveColumnSurface(
        in VoxelSceneSnapshot snapshot,
        int localX,
        int localZ,
        out float surfaceWorldY,
        out byte profileId)
    {
        return TryResolveColumnSurface(
            in snapshot,
            localX,
            localZ,
            out surfaceWorldY,
            out profileId,
            out _);
    }

    /// <summary>
    /// Finds the highest visible surface and its effective local column depth. Fluid levels use
    /// Vintage Story's 0..7 eighth-block encoding; contained surfaces use their encoded block-local
    /// fill height because no container-bottom scalar is present in the voxel ABI.
    /// </summary>
    /// <param name="snapshot">Complete voxel/liquid payload.</param>
    /// <param name="localX">Column X in the main clipmap.</param>
    /// <param name="localZ">Column Z in the main clipmap.</param>
    /// <param name="surfaceWorldY">Resolved absolute free-surface height in metres/world blocks.</param>
    /// <param name="profileId">Resolved optical profile row.</param>
    /// <param name="depthMetres">Positive effective liquid depth below the selected surface.</param>
    /// <returns>Whether a non-reserved visible surface exists.</returns>
    internal static bool TryResolveColumnSurface(
        in VoxelSceneSnapshot snapshot,
        int localX,
        int localZ,
        out float surfaceWorldY,
        out byte profileId,
        out float depthMetres)
    {
        surfaceWorldY = float.NegativeInfinity;
        profileId = LiquidOpticalRegistry.NoLiquidProfileId;
        depthMetres = 0.0f;
        int fluidLocalX = snapshot.OriginX + localX - snapshot.FluidSurfaceOriginX;
        int fluidLocalZ = snapshot.OriginZ + localZ - snapshot.FluidSurfaceOriginZ;
        bool insideFluidSurface = (uint)fluidLocalX < (uint)snapshot.FluidSurfaceWidth
            && (uint)fluidLocalZ < (uint)snapshot.FluidSurfaceDepth;
        int columnOffset = insideFluidSurface
            ? (fluidLocalZ * snapshot.FluidSurfaceWidth + fluidLocalX)
                * VoxelScene.FluidSurfaceChannels
            : -VoxelScene.FluidSurfaceChannels;
        if (insideFluidSurface
            && (uint)(columnOffset + 3) < (uint)snapshot.FluidSurface.Length)
        {
            byte encodedY = snapshot.FluidSurface[columnOffset];
            byte candidateProfile = snapshot.FluidSurface[columnOffset + 1];
            if (encodedY > 0 && IsUsableProfile(candidateProfile))
            {
                int topLocalY = encodedY - 1;
                byte liquidLevel = snapshot.FluidSurface[columnOffset + 2];
                float topFillMetres = DecodeFluidFillHeightMetres(liquidLevel);
                surfaceWorldY = snapshot.OriginY + topLocalY + topFillMetres;
                profileId = candidateProfile;
                depthMetres = ResolveFluidColumnDepthMetres(
                    in snapshot,
                    localX,
                    localZ,
                    topLocalY,
                    candidateProfile,
                    topFillMetres);
            }
        }

        for (int localY = 0; localY < snapshot.Height; localY++)
        {
            int voxelIndex = (localZ * snapshot.Height + localY) * snapshot.Width + localX;
            int metadataOffset = voxelIndex * VoxelScene.LiquidMetadataChannels;
            if ((uint)(metadataOffset + 3) >= (uint)snapshot.LiquidMetadata.Length)
            {
                break;
            }

            byte flags = snapshot.LiquidMetadata[metadataOffset + 1];
            byte candidateProfile = snapshot.LiquidMetadata[metadataOffset];
            bool visibleContainer = (flags & (byte)VoxelLiquidFlags.Contained) != 0
                && (flags & (byte)VoxelLiquidFlags.VisibleSurface) != 0;
            if (!visibleContainer || !IsUsableProfile(candidateProfile))
            {
                continue;
            }

            float candidateY = snapshot.OriginY + localY
                + snapshot.LiquidMetadata[metadataOffset + 2] / 255.0f;
            if (candidateY > surfaceWorldY)
            {
                surfaceWorldY = candidateY;
                profileId = candidateProfile;
                depthMetres = Math.Max(
                    snapshot.LiquidMetadata[metadataOffset + 2] / 255.0f,
                    1.0f / 255.0f);
            }
        }

        return float.IsFinite(surfaceWorldY)
            && IsUsableProfile(profileId)
            && float.IsFinite(depthMetres)
            && depthMetres > 0.0f;
    }

    /// <summary>Decodes the exact free-surface convention used by Vintage Story entity physics.</summary>
    /// <param name="liquidLevel">Three-bit level whose surface is <c>blockY + level / 8</c>.</param>
    /// <returns>Clamped fill height in metres/world blocks.</returns>
    internal static float DecodeFluidFillHeightMetres(byte liquidLevel)
    {
        return Math.Min(liquidLevel, (byte)7) / 8.0f;
    }

    /// <summary>Accumulates contiguous same-profile fluid voxels below the selected free surface.</summary>
    /// <param name="snapshot">Complete voxel/liquid payload.</param>
    /// <param name="localX">Column X.</param>
    /// <param name="localZ">Column Z.</param>
    /// <param name="topLocalY">Voxel containing the selected free surface.</param>
    /// <param name="profileId">Selected liquid profile.</param>
    /// <param name="fallbackTopFillMetres">Column-ABI fill used when voxel metadata is incomplete.</param>
    /// <returns>Known positive liquid depth represented by the clipmap.</returns>
    private static float ResolveFluidColumnDepthMetres(
        in VoxelSceneSnapshot snapshot,
        int localX,
        int localZ,
        int topLocalY,
        byte profileId,
        float fallbackTopFillMetres)
    {
        float topFillMetres = fallbackTopFillMetres;
        int connectedVoxelCount = 0;
        for (int localY = topLocalY; localY >= 0; localY--)
        {
            int voxelIndex = (localZ * snapshot.Height + localY) * snapshot.Width + localX;
            int metadataOffset = voxelIndex * VoxelScene.LiquidMetadataChannels;
            if ((uint)(metadataOffset + 3) >= (uint)snapshot.LiquidMetadata.Length)
            {
                break;
            }

            byte candidateProfile = snapshot.LiquidMetadata[metadataOffset];
            byte flags = snapshot.LiquidMetadata[metadataOffset + 1];
            bool connectedFluid = candidateProfile == profileId
                && (flags & (byte)VoxelLiquidFlags.FluidLayer) != 0;
            if (!connectedFluid)
            {
                break;
            }

            if (connectedVoxelCount == 0)
            {
                topFillMetres = DecodeFluidFillHeightMetres(
                    snapshot.LiquidMetadata[metadataOffset + 2]);
            }
            connectedVoxelCount++;
        }

        // Only the top voxel carries the free-surface fraction. Every
        // contiguous voxel below spans the complete distance to the next block
        // boundary; summing each lower LiquidLevel would invent dry gaps.
        return connectedVoxelCount > 0
            ? topFillMetres + Math.Max(connectedVoxelCount - 1, 0)
            : fallbackTopFillMetres;
    }

    /// <summary>Decodes the ten-texel optical ABI into the solver's dynamics and SI properties.</summary>
    /// <param name="lookup">Row-major RGBA32F profile lookup.</param>
    /// <param name="profileId">Profile row in 1..254.</param>
    /// <param name="dynamics">Decoded wave, impact, and bubble controls.</param>
    /// <param name="physics">Decoded density, viscosity, tension, damping, and resolved energy.</param>
    /// <returns>Whether all required ranges are valid.</returns>
    internal static bool TryDecodeProfile(
        float[] lookup,
        byte profileId,
        out LiquidSurfaceDynamics dynamics,
        out LiquidSurfacePhysicalProperties physics)
    {
        dynamics = default;
        physics = default;
        if (!IsUsableProfile(profileId))
        {
            return false;
        }

        int rowStride = LiquidOpticalRegistry.LookupWidth * LiquidOpticalRegistry.LookupChannels;
        int offset = profileId * rowStride;
        if (lookup is null || offset < 0 || offset + rowStride > lookup.Length)
        {
            return false;
        }

        dynamics = new LiquidSurfaceDynamics(
            lookup[offset + 16],
            lookup[offset + 17],
            lookup[offset + 18],
            lookup[offset + 19],
            lookup[offset + 20],
            lookup[offset + 21],
            lookup[offset + 22],
            lookup[offset + 24],
            lookup[offset + 25],
            lookup[offset + 26],
            lookup[offset + 27],
            lookup[offset + 28],
            lookup[offset + 29]);
        physics = new LiquidSurfacePhysicalProperties(
            lookup[offset + 32],
            lookup[offset + 33],
            lookup[offset + 34],
            Math.Max(0.0f, lookup[offset + 20]),
            lookup[offset + 35]);
        try
        {
            physics.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            dynamics = default;
            physics = default;
            return false;
        }

        return float.IsFinite(dynamics.WaveLength) && dynamics.WaveLength > 0.0f
            && float.IsFinite(dynamics.WaveAmplitude) && dynamics.WaveAmplitude >= 0.0f;
    }

    /// <summary>Uses the snapshot rain-height map to reject roofs and sealed containers.</summary>
    /// <param name="snapshot">Complete rain surface and origin.</param>
    /// <param name="worldX">World column X.</param>
    /// <param name="worldZ">World column Z.</param>
    /// <param name="surfaceWorldY">Liquid surface height.</param>
    /// <returns>Whether rain reaches the surface.</returns>
    internal static bool IsRainExposed(
        in VoxelSceneSnapshot snapshot,
        int worldX,
        int worldZ,
        float surfaceWorldY)
    {
        int rainX = worldX - snapshot.SunOriginX;
        int rainZ = worldZ - snapshot.SunOriginZ;
        if ((uint)rainX >= (uint)snapshot.RainSurfaceWidth
            || (uint)rainZ >= (uint)snapshot.RainSurfaceDepth)
        {
            return false;
        }

        int index = rainZ * snapshot.RainSurfaceWidth + rainX;
        return (uint)index < (uint)snapshot.RainSurface.Length
            && surfaceWorldY >= snapshot.RainSurface[index];
    }

    /// <summary>Queries nearby live entities at 30 Hz and feeds bounded detached observations.</summary>
    /// <param name="activeSimulation">Configured solver.</param>
    /// <param name="deltaTimeSeconds">Finite real frame duration.</param>
    private void ObserveEntities(LiquidSurfaceSimulation activeSimulation, float deltaTimeSeconds)
    {
        entityObservationAccumulator += deltaTimeSeconds;
        if (entityObservationAccumulator < EntityObservationPeriodSeconds)
        {
            return;
        }
        entityObservationAccumulator %= EntityObservationPeriodSeconds;

        Vec3d camera = api.World.Player.Entity.CameraPos;
        Entity[] entities = api.World.GetEntitiesAround(
            camera,
            EntityQueryRadiusBlocks,
            EntityQueryRadiusBlocks,
            IsRelevantSurfaceEntity) ?? [];
        int count = Math.Min(entities.Length, MaximumObservedEntities);
        if (entitySamples.Length < count)
        {
            entitySamples = new LiquidEntitySurfaceSample[Math.Min(
                MaximumObservedEntities,
                Math.Max(count, entitySamples.Length * 2))];
        }

        for (int index = 0; index < count; index++)
        {
            entitySamples[index] = LiquidSurfaceWorldInputs.SampleEntity(
                entities[index]);
        }
        activeSimulation.ObserveEntities(entitySamples.AsSpan(0, count));
    }

    /// <summary>
    /// Keeps the bounded observation budget for entities that can affect a free surface. Dropped
    /// items remain eligible throughout flight so their pre-contact velocity is never displaced by
    /// unrelated land creatures on entity-dense maps.
    /// </summary>
    /// <param name="entity">Nearby live-world candidate supplied by Vintage Story.</param>
    /// <returns>Whether the entity can currently produce an entry, wake, bobber, fish, or ricochet event.</returns>
    internal static bool IsRelevantSurfaceEntity(Entity entity)
    {
        if (!entity.Alive)
        {
            return false;
        }

        if (entity is EntityItem || entity.FeetInLiquid || entity.Swimming)
        {
            return true;
        }

        LiquidEntitySurfaceClass surfaceClass = LiquidSurfaceWorldInputs.ClassifyEntity(entity);
        return surfaceClass is LiquidEntitySurfaceClass.Bobber
            or LiquidEntitySurfaceClass.Fish
            or LiquidEntitySurfaceClass.ThrownStone
            or LiquidEntitySurfaceClass.Projectile;
    }

    /// <summary>Checks the two reserved profile identifiers.</summary>
    /// <param name="profileId">Encoded optical row.</param>
    /// <returns>Whether the row can hold an authored liquid.</returns>
    private static bool IsUsableProfile(byte profileId) =>
        profileId is not LiquidOpticalRegistry.NoLiquidProfileId
            and not LiquidOpticalRegistry.UnknownLiquidProfileId;

    /// <summary>Deletes the owned dynamic texture and releases CPU simulation buffers.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        simulation = null;
        lock (projectileCollisionGate)
        {
            pendingProjectileCollisionCount = 0;
            Array.Clear(pendingProjectileCollisions);
            Array.Clear(consumedProjectileCollisions);
        }
        uploader.Dispose();
    }
}
