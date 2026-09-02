using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>
/// Builds a camera-centred, GPU-friendly voxel snapshot from the public world API.
/// World reads are deliberately budgeted so moving the clipmap never causes a large
/// synchronous frame-time spike.
/// </summary>
internal sealed class VoxelScene : IDisposable
{
    /// <summary>Material/radiance clipmap width in one-block voxels.</summary>
    public const int Width = 64;
    /// <summary>Material/radiance clipmap height in one-block voxels.</summary>
    public const int Height = 48;
    /// <summary>Material/radiance clipmap depth in one-block voxels.</summary>
    public const int Depth = 64;
    /// <summary>Geometry occupancy samples per block edge.</summary>
    public const int OccupancyScale = 4;
    /// <summary>
    /// Low material-alpha bit declaring that the owning solid uses partial or instance geometry.
    /// Base material classes occupy the upper two bits, so this metadata does not change the
    /// dielectric, transmissive, or conductor thresholds consumed by the shader.
    /// </summary>
    internal const byte PartialGeometryMaterialFlag = 4;
    /// <summary>Fine occupancy texture width in quarter-block cells.</summary>
    public const int OccupancyWidth = Width * OccupancyScale;
    /// <summary>Fine occupancy texture height in quarter-block cells.</summary>
    public const int OccupancyHeight = Height * OccupancyScale;
    /// <summary>Fine occupancy texture depth in quarter-block cells.</summary>
    public const int OccupancyDepth = Depth * OccupancyScale;
    /// <summary>Subvoxel edge count used to evaluate per-light caster geometry.</summary>
    public const int LightCasterScale = 16;
    /// <summary>Total 16-cubed binary samples in one detailed light-caster mask.</summary>
    public const int LightCasterVoxelCount = LightCasterScale * LightCasterScale * LightCasterScale;
    /// <summary>Maximum static/dynamic emitters uploaded to the display shader.</summary>
    public const int MaximumLightCount = 8;
    /// <summary>World blocks represented by one long-range sun occupancy cell.</summary>
    public const int SunOccupancyScale = 2;
    /// <summary>Maximum configured long-range sun trace represented without leaving the clipmap.</summary>
    public const int MaximumSunTraceDistance = 96;
    /// <summary>Long-range sun clipmap width in occupancy cells.</summary>
    public const int SunOccupancyWidth = 76;
    /// <summary>Long-range sun clipmap height in occupancy cells.</summary>
    public const int SunOccupancyHeight = 58;
    /// <summary>Long-range sun clipmap depth in occupancy cells.</summary>
    public const int SunOccupancyDepth = 76;
    /// <summary>Sun clipmap world reach along X in blocks.</summary>
    public const int SunWorldWidth = SunOccupancyWidth * SunOccupancyScale;
    /// <summary>Sun clipmap world reach along Y in blocks.</summary>
    public const int SunWorldHeight = SunOccupancyHeight * SunOccupancyScale;
    /// <summary>Sun clipmap world reach along Z in blocks.</summary>
    public const int SunWorldDepth = SunOccupancyDepth * SunOccupancyScale;
    /// <summary>Rain-height map width, aligned to the sun clipmap world footprint.</summary>
    public const int RainSurfaceWidth = SunWorldWidth;
    /// <summary>Rain-height map depth, aligned to the sun clipmap world footprint.</summary>
    public const int RainSurfaceDepth = SunWorldDepth;
    /// <summary>Per-column bytes: local Y+1, profile ID, liquid level, and flags.</summary>
    public const int FluidSurfaceChannels = 4;
    /// <summary>
    /// Static liquid-surface footprint in one-block columns. A 128-block span with a 48-block
    /// protected margin guarantees the Cinematic reflection reach while the expensive material
    /// volume remains 64 blocks wide.
    /// </summary>
    public const int FluidSurfaceWidth = 128;
    /// <summary>Static liquid-surface footprint depth in one-block columns.</summary>
    public const int FluidSurfaceDepth = 128;
    /// <summary>Per-voxel bytes: profile ID, flags, level/surface, and reserved.</summary>
    public const int LiquidMetadataChannels = 4;

    /// <summary>Per-voxel material bytes in the CPU/GPU ABI.</summary>
    private const int BytesPerVoxel = 4;
    /// <summary>Linear irradiance RGB bytes per voxel.</summary>
    private const int IrradianceChannels = 3;
    /// <summary>Dominant irradiance direction bytes per voxel.</summary>
    private const int IrradianceDirectionChannels = 3;
    /// <summary>Bounded CPU propagation passes used to build diffuse radiance.</summary>
    private const int IrradiancePropagationIterations = 18;
    /// <summary>Linear radiance represented by byte value 255.</summary>
    private const float IrradianceEncodingRange = 2.0f;
    /// <summary>Maximum emissive seed influence radius in world blocks.</summary>
    private const float IrradianceEmitterRadius = 16.0f;
    /// <summary>Diffuse neighbor transfer gain per propagation iteration.</summary>
    private const float DiffuseBounceGain = 0.62f;
    /// <summary>Main voxels sampled per 20 ms game tick.</summary>
    private const int VoxelsPerTick = 4096;
    /// <summary>Long-range sun cells sampled per 20 ms game tick.</summary>
    private const int SunOccupancyCellsPerTick = 1024;
    /// <summary>Rain-height columns sampled per 20 ms game tick.</summary>
    private const int RainSurfaceCellsPerTick = 512;
    /// <summary>Dedicated liquid-surface columns sampled per 20 ms game tick.</summary>
    private const int FluidSurfaceCellsPerTick = 128;
    /// <summary>Horizontal block margin triggering material clipmap recentering.</summary>
    private const int HorizontalRecenterMargin = 16;
    /// <summary>Vertical block margin triggering material clipmap recentering.</summary>
    private const int VerticalRecenterMargin = 12;
    /// <summary>
    /// Protected liquid-surface margin. With a 128-block footprint this leaves 48 blocks in the
    /// camera's shortest direction until the shared rebuild recenters all immutable snapshots.
    /// </summary>
    private const int FluidSurfaceRecenterMargin = 48;
    /// <summary>Delayed confirmation rebuild after recentering, in seconds.</summary>
    private const float SceneSettleRebuildDelaySeconds = 1.5f;

    private static readonly (int X, int Y, int Z)[] IrradianceFaces =
    [
        (-1, 0, 0),
        (1, 0, 0),
        (0, -1, 0),
        (0, 1, 0),
        (0, 0, -1),
        (0, 0, 1)
    ];

    private readonly ICoreClientAPI api;
    private readonly LiquidOpticalRegistry liquidOptics;
    private readonly byte[] buildVoxels = new byte[Width * Height * Depth * BytesPerVoxel];
    private readonly byte[] buildOccupancy = new byte[OccupancyWidth * OccupancyHeight * OccupancyDepth];
    private readonly byte[] buildFluidSurface = new byte[
        FluidSurfaceWidth * FluidSurfaceDepth * FluidSurfaceChannels];
    private readonly byte[] buildLiquidMetadata = new byte[
        Width * Height * Depth * LiquidMetadataChannels];
    private readonly ulong[] buildSunOccupancy = new ulong[
        SunOccupancyWidth * SunOccupancyHeight * SunOccupancyDepth];
    private readonly float[] buildRainSurface = new float[RainSurfaceWidth * RainSurfaceDepth];
    private readonly List<VoxelLight> buildLights = new();
    private readonly Dictionary<int, CachedBlockOccupancy> cachedBlockOccupancy = new();
    private readonly Dictionary<int, CachedLightCaster> cachedLightCasters = new();
    private readonly Dictionary<int, LiquidContainerOptics?> cachedLiquidContainers = new();
    private readonly Dictionary<AssetLocation, TextureAlphaData?> cachedTextureAlpha = new();
    private readonly Dictionary<(int X, int Y, int Z, int Dimension), InstanceMeshOccupancy>
        instanceMeshOccupancyByPosition = new();
    private readonly Dictionary<(string Code, string Reason), int> fallbackOccupancyHistogram = new();
    private readonly HashSet<(int X, int Y, int Z)> dirtyBlocks = [];
    private readonly List<VoxelSceneBlockUpdate> pendingBlockUpdates = [];
    private readonly List<VoxelFluidSurfaceUpdate> pendingFluidSurfaceUpdates = [];
    private readonly List<VoxelSunOccupancyUpdate> pendingSunOccupancyUpdates = [];
    private readonly List<VoxelRainSurfaceUpdate> pendingRainSurfaceUpdates = [];
    private readonly BlockPos samplePosition = new(0);

    private byte[] readyVoxels = new byte[Width * Height * Depth * BytesPerVoxel];
    private byte[] readyOccupancy = new byte[OccupancyWidth * OccupancyHeight * OccupancyDepth];
    private byte[] readyFluidSurface = new byte[
        FluidSurfaceWidth * FluidSurfaceDepth * FluidSurfaceChannels];
    private byte[] readyLiquidMetadata = new byte[
        Width * Height * Depth * LiquidMetadataChannels];
    private ulong[] readySunOccupancy = new ulong[
        SunOccupancyWidth * SunOccupancyHeight * SunOccupancyDepth];
    private float[] readyRainSurface = new float[RainSurfaceWidth * RainSurfaceDepth];
    private byte[] readyIrradiance = new byte[Width * Height * Depth * IrradianceChannels];
    private byte[] readyIrradianceDirection = new byte[
        Width * Height * Depth * IrradianceDirectionChannels];
    private VoxelLight[] readyLights = Array.Empty<VoxelLight>();
    private int originX;
    private int originY;
    private int originZ;
    private int sunOriginX;
    private int sunOriginY;
    private int sunOriginZ;
    private int fluidSurfaceOriginX;
    private int fluidSurfaceOriginZ;
    private int buildIndex;
    private int fluidSurfaceBuildIndex;
    private int sunBuildIndex;
    private int rainSurfaceBuildIndex;
    private int generation;
    private bool building;
    private bool uploadPending;
    private bool rebuildRequested;
    private bool settleRebuildPending;
    private float settleRebuildDelaySeconds;
    private bool disposed;
    private long tickListenerId;
    private int meshOccupancyBlocks;
    private int collisionOccupancyBlocks;
    private int selectionOccupancyBlocks;
    private int fallbackOccupancyBlocks;
    private int buildFluidVoxels;
    private int readyFluidVoxels;
    private int buildVisibleLiquidContainers;
    private int readyVisibleLiquidContainers;

    /// <summary>Initializes liquid profiles and registers budgeted tick/block-change observers.</summary>
    /// <param name="api">Client world, tessellator, block accessor, events, and logging services.</param>
    public VoxelScene(ICoreClientAPI api)
    {
        this.api = api;
        liquidOptics = new LiquidOpticalRegistry(api.World.Collectibles, api.Assets, api.Logger);
        tickListenerId = api.Event.RegisterGameTickListener(OnGameTick, 20);
        api.Event.BlockChanged += OnBlockChanged;
    }

    /// <summary>Gets deterministic rebuild progress or ready generation/origin/light diagnostics.</summary>
    public string Status
    {
        get
        {
            if (building)
            {
                int completed = buildIndex + fluidSurfaceBuildIndex
                    + sunBuildIndex + rainSurfaceBuildIndex;
                int total = Width * Height * Depth
                    + FluidSurfaceWidth * FluidSurfaceDepth
                    + SunOccupancyWidth * SunOccupancyHeight * SunOccupancyDepth
                    + RainSurfaceWidth * RainSurfaceDepth;
                double progress = 100.0 * completed / total;
                return $"building {progress:0}% at ({originX},{originY},{originZ})";
            }

            return generation == 0
                ? "waiting for player"
                : $"ready gen={generation}, origin=({originX},{originY},{originZ}), lights={readyLights.Length}";
        }
    }

    /// <summary>Gets whether at least one complete generation is available and no rebuild is active.</summary>
    public bool IsReady => generation > 0 && !building;

    /// <summary>
    /// Gets whether the latest complete generation and every incremental edit have been consumed
    /// for GPU upload, with no rebuild or delayed confirmation scan still able to replace them.
    /// </summary>
    public bool IsSettled => generation > 0
        && !building
        && !rebuildRequested
        && !settleRebuildPending
        && dirtyBlocks.Count == 0
        && !uploadPending
        && pendingBlockUpdates.Count == 0
        && pendingFluidSurfaceUpdates.Count == 0
        && pendingSunOccupancyUpdates.Count == 0
        && pendingRainSurfaceUpdates.Count == 0;

    /// <summary>Exposes one complete generation exactly once without transferring array ownership.</summary>
    /// <param name="snapshot">Dimensions/origins plus scene-owned immutable-until-next-swap arrays.</param>
    /// <returns>Whether a new full GPU upload is pending.</returns>
    public bool TryConsumeUpload(out VoxelSceneSnapshot snapshot)
    {
        if (!uploadPending || generation == 0)
        {
            snapshot = default;
            return false;
        }

        uploadPending = false;
        snapshot = new VoxelSceneSnapshot(
            readyVoxels,
            readyOccupancy,
            Width,
            Height,
            Depth,
            originX,
            originY,
            originZ,
            readyLights,
            readyIrradiance,
            readyIrradianceDirection,
            readyFluidSurface,
            readyLiquidMetadata,
            liquidOptics.GpuLookup,
            liquidOptics.ProfileCount,
            readySunOccupancy,
            readyRainSurface,
            SunOccupancyWidth,
            SunOccupancyHeight,
            SunOccupancyDepth,
            SunOccupancyScale,
            sunOriginX,
            sunOriginY,
            sunOriginZ,
            RainSurfaceWidth,
            RainSurfaceDepth,
            readyFluidVoxels,
            readyVisibleLiquidContainers,
            generation,
            FluidSurfaceWidth,
            FluidSurfaceDepth,
            fluidSurfaceOriginX,
            fluidSurfaceOriginZ);
        return true;
    }

    /// <summary>Consumes coalesced incremental material/occupancy/liquid block updates.</summary>
    /// <param name="updates">Detached update array, empty when unavailable.</param>
    /// <returns>Whether partial uploads may replace the current generation.</returns>
    public bool TryConsumeBlockUpdates(out VoxelSceneBlockUpdate[] updates)
    {
        if (uploadPending || pendingBlockUpdates.Count == 0)
        {
            updates = [];
            return false;
        }

        updates = pendingBlockUpdates.ToArray();
        pendingBlockUpdates.Clear();
        return true;
    }

    /// <summary>Consumes coalesced updates for the independent static liquid footprint.</summary>
    /// <param name="updates">Detached one-column RGBA payloads.</param>
    /// <returns>Whether partial liquid-surface uploads are available.</returns>
    public bool TryConsumeFluidSurfaceUpdates(out VoxelFluidSurfaceUpdate[] updates)
    {
        if (uploadPending || pendingFluidSurfaceUpdates.Count == 0)
        {
            updates = [];
            return false;
        }

        updates = pendingFluidSurfaceUpdates.ToArray();
        pendingFluidSurfaceUpdates.Clear();
        return true;
    }

    /// <summary>Consumes coalesced long-range sun occupancy cell updates.</summary>
    /// <param name="updates">Detached update array.</param>
    /// <returns>Whether partial sun texture uploads are available.</returns>
    public bool TryConsumeSunOccupancyUpdates(out VoxelSunOccupancyUpdate[] updates)
    {
        if (uploadPending || pendingSunOccupancyUpdates.Count == 0)
        {
            updates = [];
            return false;
        }

        updates = pendingSunOccupancyUpdates.ToArray();
        pendingSunOccupancyUpdates.Clear();
        return true;
    }

    /// <summary>Consumes coalesced rain-height column updates.</summary>
    /// <param name="updates">Detached update array.</param>
    /// <returns>Whether partial rain texture uploads are available.</returns>
    public bool TryConsumeRainSurfaceUpdates(out VoxelRainSurfaceUpdate[] updates)
    {
        if (uploadPending || pendingRainSurfaceUpdates.Count == 0)
        {
            updates = [];
            return false;
        }

        updates = pendingRainSurfaceUpdates.ToArray();
        pendingRainSurfaceUpdates.Clear();
        return true;
    }

    /// <summary>Budgets recenter/rebuild sampling across main, sun, and rain grids on the game tick.</summary>
    /// <param name="deltaTime">Game-tick duration in seconds used by delayed settle logic.</param>
    private void OnGameTick(float deltaTime)
    {
        if (disposed || api.World.Player?.Entity is null)
        {
            return;
        }

        Vec3d cameraPosition = api.World.Player.Entity.CameraPos;
        int cameraX = (int)Math.Floor(cameraPosition.X);
        int cameraY = (int)Math.Floor(cameraPosition.Y);
        int cameraZ = (int)Math.Floor(cameraPosition.Z);
        samplePosition.dimension = api.World.Player.Entity.Pos.Dimension;

        if (!building && settleRebuildPending)
        {
            settleRebuildDelaySeconds -= Math.Max(deltaTime, 0.0f);
            if (settleRebuildDelaySeconds <= 0.0f)
            {
                // A camera teleport can finish the first budgeted scan while
                // surrounding chunks are still arriving. One delayed
                // confirmation scan captures those chunks before benchmark or
                // gameplay settles, while later block changes stay incremental.
                settleRebuildPending = false;
                rebuildRequested = true;
            }
        }

        bool needsRecentering = NeedsRecentering(cameraX, cameraY, cameraZ);
        bool fluidSurfaceNeedsRecentering = NeedsFluidSurfaceRecentering(cameraX, cameraZ);
        bool sunNeedsRecentering = NeedsSunRecentering(
            cameraX,
            cameraY,
            cameraZ,
            GetNormalizedSunDirection());
        if (!building
            && (rebuildRequested
                || needsRecentering
                || fluidSurfaceNeedsRecentering
                || sunNeedsRecentering))
        {
            BeginRebuild(
                cameraX,
                cameraY,
                cameraZ,
                needsRecentering || fluidSurfaceNeedsRecentering || sunNeedsRecentering);
        }

        if (!building)
        {
            ProcessDirtyBlocks();
            return;
        }

        int endIndex = Math.Min(buildIndex + VoxelsPerTick, Width * Height * Depth);
        while (buildIndex < endIndex)
        {
            SampleVoxel(buildIndex++);
        }

        int fluidSurfaceEndIndex = Math.Min(
            fluidSurfaceBuildIndex + FluidSurfaceCellsPerTick,
            FluidSurfaceWidth * FluidSurfaceDepth);
        while (fluidSurfaceBuildIndex < fluidSurfaceEndIndex)
        {
            SampleFluidSurfaceColumn(fluidSurfaceBuildIndex++);
        }

        int sunEndIndex = Math.Min(
            sunBuildIndex + SunOccupancyCellsPerTick,
            SunOccupancyWidth * SunOccupancyHeight * SunOccupancyDepth);
        while (sunBuildIndex < sunEndIndex)
        {
            SampleSunOccupancyCell(sunBuildIndex++);
        }

        int rainSurfaceEndIndex = Math.Min(
            rainSurfaceBuildIndex + RainSurfaceCellsPerTick,
            RainSurfaceWidth * RainSurfaceDepth);
        while (rainSurfaceBuildIndex < rainSurfaceEndIndex)
        {
            SampleRainSurfaceCell(rainSurfaceBuildIndex++);
        }

        if (buildIndex == Width * Height * Depth
            && fluidSurfaceBuildIndex == FluidSurfaceWidth * FluidSurfaceDepth
            && sunBuildIndex == SunOccupancyWidth * SunOccupancyHeight * SunOccupancyDepth
            && rainSurfaceBuildIndex == RainSurfaceWidth * RainSurfaceDepth)
        {
            CompleteRebuild();
        }
    }

    /// <summary>Checks whether the camera crossed the protected inner material-clipmap volume.</summary>
    /// <param name="cameraX">Integer camera X in blocks.</param>
    /// <param name="cameraY">Integer camera Y in blocks.</param>
    /// <param name="cameraZ">Integer camera Z in blocks.</param>
    /// <returns>Whether a full material/radiance rebuild is required.</returns>
    private bool NeedsRecentering(int cameraX, int cameraY, int cameraZ)
    {
        if (generation == 0)
        {
            return true;
        }

        return cameraX < originX + HorizontalRecenterMargin
            || cameraX >= originX + Width - HorizontalRecenterMargin
            || cameraY < originY + VerticalRecenterMargin
            || cameraY >= originY + Height - VerticalRecenterMargin
            || cameraZ < originZ + HorizontalRecenterMargin
            || cameraZ >= originZ + Depth - HorizontalRecenterMargin;
    }

    /// <summary>Checks the protected 48-block reflection radius inside the static liquid map.</summary>
    /// <param name="cameraX">Integer camera X in blocks.</param>
    /// <param name="cameraZ">Integer camera Z in blocks.</param>
    /// <returns>Whether the independent liquid footprint must be recentered.</returns>
    private bool NeedsFluidSurfaceRecentering(int cameraX, int cameraZ)
    {
        if (generation == 0)
        {
            return true;
        }

        return cameraX < fluidSurfaceOriginX + FluidSurfaceRecenterMargin
            || cameraX >= fluidSurfaceOriginX
                + FluidSurfaceWidth - FluidSurfaceRecenterMargin
            || cameraZ < fluidSurfaceOriginZ + FluidSurfaceRecenterMargin
            || cameraZ >= fluidSurfaceOriginZ
                + FluidSurfaceDepth - FluidSurfaceRecenterMargin;
    }

    /// <summary>Computes the shortest guaranteed horizontal reach inside a surface footprint.</summary>
    /// <param name="origin">Footprint minimum world coordinate.</param>
    /// <param name="extent">Footprint extent in blocks.</param>
    /// <param name="camera">Integer camera coordinate.</param>
    /// <returns>Whole-block reach before either footprint boundary.</returns>
    internal static int CalculateSurfaceCoverageReach(int origin, int extent, int camera)
    {
        return Math.Min(camera - origin, origin + extent - camera);
    }

    /// <summary>Maps one world column into a bounded static liquid-footprint coordinate.</summary>
    /// <param name="worldX">World X.</param>
    /// <param name="worldZ">World Z.</param>
    /// <param name="surfaceOriginX">Footprint origin X.</param>
    /// <param name="surfaceOriginZ">Footprint origin Z.</param>
    /// <param name="localX">Resolved local X when contained.</param>
    /// <param name="localZ">Resolved local Z when contained.</param>
    /// <returns>Whether the column lies inside the 128-square footprint.</returns>
    internal static bool TryMapFluidSurfaceColumn(
        int worldX,
        int worldZ,
        int surfaceOriginX,
        int surfaceOriginZ,
        out int localX,
        out int localZ)
    {
        localX = worldX - surfaceOriginX;
        localZ = worldZ - surfaceOriginZ;
        return (uint)localX < FluidSurfaceWidth && (uint)localZ < FluidSurfaceDepth;
    }

    /// <summary>Checks containment of receiver volume plus the long solar ray sweep.</summary>
    /// <param name="cameraX">Integer camera X.</param>
    /// <param name="cameraY">Integer camera Y.</param>
    /// <param name="cameraZ">Integer camera Z.</param>
    /// <param name="sunDirection">Normalized world direction toward the sun.</param>
    /// <returns>Whether long-range occupancy must be recentered.</returns>
    private bool NeedsSunRecentering(
        int cameraX,
        int cameraY,
        int cameraZ,
        Vec3f sunDirection)
    {
        if (generation == 0)
        {
            return true;
        }

        return !SunClipmapContainsFullTrace(
            sunOriginX,
            sunOriginY,
            sunOriginZ,
            cameraX,
            cameraY,
            cameraZ,
            sunDirection);
    }

    /// <summary>Tests containment of the receiver footprint swept through the configured sun-ray reach.</summary>
    /// <param name="clipmapOriginX">Sun volume minimum X.</param>
    /// <param name="clipmapOriginY">Sun volume minimum Y.</param>
    /// <param name="clipmapOriginZ">Sun volume minimum Z.</param>
    /// <param name="cameraX">Receiver center X.</param>
    /// <param name="cameraY">Receiver center Y.</param>
    /// <param name="cameraZ">Receiver center Z.</param>
    /// <param name="sunDirection">Normalized sweep direction.</param>
    /// <returns>Whether no traced receiver ray can leave the clipmap.</returns>
    internal static bool SunClipmapContainsFullTrace(
        int clipmapOriginX,
        int clipmapOriginY,
        int clipmapOriginZ,
        int cameraX,
        int cameraY,
        int cameraZ,
        Vec3f sunDirection)
    {
        const float horizontalReceiverRadius = 24.0f;
        const float verticalReceiverRadius = 8.0f;
        return ContainsSweptAxis(
                clipmapOriginX,
                SunWorldWidth,
                cameraX,
                horizontalReceiverRadius,
                sunDirection.X)
            && ContainsSweptAxis(
                clipmapOriginY,
                SunWorldHeight,
                cameraY,
                verticalReceiverRadius,
                sunDirection.Y)
            && ContainsSweptAxis(
                clipmapOriginZ,
                SunWorldDepth,
                cameraZ,
                horizontalReceiverRadius,
                sunDirection.Z);
    }

    /// <summary>Per-axis containment test for a receiver interval swept through the maximum sun reach.</summary>
    /// <param name="origin">Clipmap minimum coordinate.</param>
    /// <param name="extent">Clipmap world extent.</param>
    /// <param name="camera">Receiver center coordinate.</param>
    /// <param name="receiverRadius">Receiver half-extent.</param>
    /// <param name="direction">Normalized ray-axis component.</param>
    /// <returns>Whether the complete swept interval is contained.</returns>
    private static bool ContainsSweptAxis(
        int origin,
        int extent,
        int camera,
        float receiverRadius,
        float direction)
    {
        float rayOffset = Math.Clamp(direction, -1.0f, 1.0f) * MaximumSunTraceDistance;
        float minimum = camera - receiverRadius + Math.Min(rayOffset, 0.0f);
        float maximum = camera + receiverRadius + Math.Max(rayOffset, 0.0f);
        return minimum >= origin && maximum <= origin + extent;
    }

    /// <summary>Recenters all grids, clears build state/partial queues, and starts budgeted sampling.</summary>
    /// <param name="cameraX">Integer camera X.</param>
    /// <param name="cameraY">Integer camera Y.</param>
    /// <param name="cameraZ">Integer camera Z.</param>
    /// <param name="scheduleSettleRebuild">Whether to rescan after surrounding chunks settle.</param>
    private void BeginRebuild(
        int cameraX,
        int cameraY,
        int cameraZ,
        bool scheduleSettleRebuild)
    {
        originX = cameraX - Width / 2;
        originY = cameraY - Height / 2;
        originZ = cameraZ - Depth / 2;
        fluidSurfaceOriginX = cameraX - FluidSurfaceWidth / 2;
        fluidSurfaceOriginZ = cameraZ - FluidSurfaceDepth / 2;
        (sunOriginX, sunOriginY, sunOriginZ) = CalculateSunClipmapOrigin(
            cameraX,
            cameraY,
            cameraZ,
            GetNormalizedSunDirection());
        buildIndex = 0;
        fluidSurfaceBuildIndex = 0;
        sunBuildIndex = 0;
        rainSurfaceBuildIndex = 0;
        buildLights.Clear();
        Array.Clear(buildVoxels);
        Array.Clear(buildOccupancy);
        Array.Clear(buildFluidSurface);
        Array.Clear(buildLiquidMetadata);
        Array.Clear(buildSunOccupancy);
        Array.Clear(buildRainSurface);
        meshOccupancyBlocks = 0;
        collisionOccupancyBlocks = 0;
        selectionOccupancyBlocks = 0;
        fallbackOccupancyBlocks = 0;
        fallbackOccupancyHistogram.Clear();
        buildFluidVoxels = 0;
        buildVisibleLiquidContainers = 0;
        rebuildRequested = false;
        dirtyBlocks.Clear();
        pendingBlockUpdates.Clear();
        pendingFluidSurfaceUpdates.Clear();
        pendingSunOccupancyUpdates.Clear();
        pendingRainSurfaceUpdates.Clear();
        building = true;
        if (scheduleSettleRebuild)
        {
            settleRebuildPending = true;
            settleRebuildDelaySeconds = SceneSettleRebuildDelaySeconds;
        }
    }

    /// <summary>Samples solid/fluid layers, contained liquids, material, detailed occupancy, and emitters.</summary>
    /// <param name="index">Flat main-grid index.</param>
    private void SampleVoxel(int index)
    {
        int localX = index % Width;
        int yz = index / Width;
        int localY = yz % Height;
        int localZ = yz / Height;

        int worldX = originX + localX;
        int worldY = originY + localY;
        int worldZ = originZ + localZ;
        samplePosition.Set(worldX, worldY, worldZ);

        IBlockAccessor accessor = api.World.BlockAccessor;
        Block? block = accessor.GetBlock(samplePosition, BlockLayersAccess.Solid);
        Block? fluid = accessor.GetBlock(samplePosition, BlockLayersAccess.Fluid);
        bool hasSolid = block is not null && IsTraceableSolid(block);
        bool hasFluidLayerBlock = fluid is not null
            && fluid.Id != 0
            && fluid.BlockMaterial != EnumBlockMaterial.Air;
        bool hasLiquid = hasFluidLayerBlock && IsOpticalLiquid(fluid!, accessor, samplePosition);
        if (hasLiquid)
        {
            buildFluidVoxels++;
            byte profileId = liquidOptics.GetProfileId(fluid!);
            WriteLiquidMetadata(
                buildLiquidMetadata,
                index,
                profileId,
                VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface,
                EncodeLiquidLevel(fluid!));
            if (TryMapFluidSurfaceColumn(
                    worldX,
                    worldZ,
                    fluidSurfaceOriginX,
                    fluidSurfaceOriginZ,
                    out int fluidLocalX,
                    out int fluidLocalZ))
            {
                WriteFluidColumn(
                    buildFluidSurface,
                    fluidLocalX,
                    localY,
                    fluidLocalZ,
                    profileId,
                    EncodeLiquidLevel(fluid!),
                    VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface);
            }
        }
        else if (block is not null
            && TryGetVisibleContainedLiquid(
                block,
                samplePosition,
                out byte containedProfileId,
                out byte containedSurfaceHeight))
        {
            buildVisibleLiquidContainers++;
            WriteLiquidMetadata(
                buildLiquidMetadata,
                index,
                containedProfileId,
                VoxelLiquidFlags.Contained | VoxelLiquidFlags.VisibleSurface,
                containedSurfaceHeight);
        }

        int byteIndex = index * BytesPerVoxel;
        if (block is not null && hasSolid)
        {
            WriteBlockMaterial(
                buildVoxels,
                block,
                byteIndex,
                worldX,
                worldY,
                worldZ,
                partialGeometry: HasPotentialPartialGeometry(block));
            WriteOccupancy(
                buildOccupancy,
                block,
                localX,
                localY,
                localZ,
                worldX,
                worldY,
                worldZ,
                countStatistics: true);
        }
        else if (fluid is not null && hasFluidLayerBlock)
        {
            WriteBlockMaterial(
                buildVoxels,
                fluid,
                byteIndex,
                worldX,
                worldY,
                worldZ,
                64);
        }

        if (block is not null)
        {
            CollectLight(block, worldX, worldY, worldZ);
        }

        if (fluid is not null && !ReferenceEquals(fluid, block))
        {
            CollectLight(fluid, worldX, worldY, worldZ);
        }
    }

    /// <summary>
    /// Samples the highest optical fluid surface in one column of the independent 128-square
    /// footprint. The vertical encoding remains relative to the unchanged 48-block voxel origin.
    /// </summary>
    /// <param name="index">Flat liquid-surface column index.</param>
    private void SampleFluidSurfaceColumn(int index)
    {
        int localX = index % FluidSurfaceWidth;
        int localZ = index / FluidSurfaceWidth;
        int worldX = fluidSurfaceOriginX + localX;
        int worldZ = fluidSurfaceOriginZ + localZ;
        IBlockAccessor accessor = api.World.BlockAccessor;
        for (int localY = 0; localY < Height; localY++)
        {
            samplePosition.Set(worldX, originY + localY, worldZ);
            Block? fluid = accessor.GetBlock(samplePosition, BlockLayersAccess.Fluid);
            if (fluid is null
                || fluid.Id == 0
                || fluid.BlockMaterial == EnumBlockMaterial.Air
                || !IsOpticalLiquid(fluid, accessor, samplePosition))
            {
                continue;
            }

            WriteFluidColumn(
                buildFluidSurface,
                localX,
                localY,
                localZ,
                liquidOptics.GetProfileId(fluid),
                EncodeLiquidLevel(fluid),
                VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface);
        }
    }

    /// <summary>Samples one coarse long-range sun cell into the build volume.</summary>
    /// <param name="index">Flat sun-grid index.</param>
    private void SampleSunOccupancyCell(int index)
    {
        int localX = index % SunOccupancyWidth;
        int yz = index / SunOccupancyWidth;
        int localY = yz % SunOccupancyHeight;
        int localZ = yz / SunOccupancyHeight;
        buildSunOccupancy[index] = SampleSunOccupancyMask(localX, localY, localZ);
    }

    /// <summary>Samples engine-maintained rain-map surface Y for one world X/Z column.</summary>
    /// <param name="index">Flat rain-grid index.</param>
    private void SampleRainSurfaceCell(int index)
    {
        int localX = index % RainSurfaceWidth;
        int localZ = index / RainSurfaceWidth;
        samplePosition.Set(sunOriginX + localX, 0, sunOriginZ + localZ);
        // Public rain height respects Block.RainPermeable and is maintained by
        // the engine after block edits. Store world Y directly in R32F so tall
        // worlds and negative dimensions do not need a lossy byte encoding.
        buildRainSurface[index] = api.World.BlockAccessor.GetRainMapHeightAt(samplePosition);
    }

    /// <summary>Coalesces up to sixteen world changes per tick into main/sun/rain partial updates.</summary>
    private void ProcessDirtyBlocks()
    {
        const int maximumUpdatesPerTick = 16;
        if (generation == 0 || dirtyBlocks.Count == 0)
        {
            return;
        }

        List<(int X, int Y, int Z)> batch = new(maximumUpdatesPerTick);
        foreach ((int X, int Y, int Z) position in dirtyBlocks)
        {
            batch.Add(position);
            if (batch.Count == maximumUpdatesPerTick)
            {
                break;
            }
        }

        foreach ((int X, int Y, int Z) position in batch)
        {
            dirtyBlocks.Remove(position);
            UpdateReadyVoxel(position.X, position.Y, position.Z);
            UpdateReadyFluidSurface(position.X, position.Z);
            UpdateReadySunOccupancy(position.X, position.Y, position.Z);
            UpdateReadyRainSurface(position.X, position.Z);
        }
    }

    /// <summary>Refreshes one rain-height column when a changed block intersects the rain footprint.</summary>
    /// <param name="worldX">Changed world X.</param>
    /// <param name="worldZ">Changed world Z.</param>
    private void UpdateReadyRainSurface(int worldX, int worldZ)
    {
        int localX = worldX - sunOriginX;
        int localZ = worldZ - sunOriginZ;
        if ((uint)localX >= RainSurfaceWidth || (uint)localZ >= RainSurfaceDepth)
        {
            return;
        }

        samplePosition.Set(worldX, 0, worldZ);
        float height = api.World.BlockAccessor.GetRainMapHeightAt(samplePosition);
        readyRainSurface[localZ * RainSurfaceWidth + localX] = height;
        VoxelRainSurfaceUpdate update = new(localX, localZ, height);
        int existing = pendingRainSurfaceUpdates.FindIndex(
            candidate => candidate.LocalX == localX && candidate.LocalZ == localZ);
        if (existing >= 0)
        {
            pendingRainSurfaceUpdates[existing] = update;
        }
        else
        {
            pendingRainSurfaceUpdates.Add(update);
        }
    }

    /// <summary>Refreshes the coarse sun cell covering a changed world block.</summary>
    /// <param name="worldX">Changed world X.</param>
    /// <param name="worldY">Changed world Y.</param>
    /// <param name="worldZ">Changed world Z.</param>
    private void UpdateReadySunOccupancy(int worldX, int worldY, int worldZ)
    {
        int localX = (worldX - sunOriginX) / SunOccupancyScale;
        int localY = (worldY - sunOriginY) / SunOccupancyScale;
        int localZ = (worldZ - sunOriginZ) / SunOccupancyScale;
        if (worldX < sunOriginX || worldY < sunOriginY || worldZ < sunOriginZ
            || (uint)localX >= SunOccupancyWidth
            || (uint)localY >= SunOccupancyHeight
            || (uint)localZ >= SunOccupancyDepth)
        {
            return;
        }

        int index = (localZ * SunOccupancyHeight + localY) * SunOccupancyWidth + localX;
        ulong occupancy = SampleSunOccupancyMask(localX, localY, localZ);
        readySunOccupancy[index] = occupancy;
        VoxelSunOccupancyUpdate update = new(localX, localY, localZ, occupancy);
        int existing = pendingSunOccupancyUpdates.FindIndex(
            candidate => candidate.LocalX == localX
                && candidate.LocalY == localY
                && candidate.LocalZ == localZ);
        if (existing >= 0)
        {
            pendingSunOccupancyUpdates[existing] = update;
        }
        else
        {
            pendingSunOccupancyUpdates.Add(update);
        }
    }

    /// <summary>Recomputes one ready main voxel and its complete liquid column/occupancy payload.</summary>
    /// <param name="worldX">Changed world X.</param>
    /// <param name="worldY">Changed world Y.</param>
    /// <param name="worldZ">Changed world Z.</param>
    private void UpdateReadyVoxel(int worldX, int worldY, int worldZ)
    {
        int localX = worldX - originX;
        int localY = worldY - originY;
        int localZ = worldZ - originZ;
        if ((uint)localX >= Width || (uint)localY >= Height || (uint)localZ >= Depth)
        {
            return;
        }

        int voxelIndex = (localZ * Height + localY) * Width + localX;
        int byteIndex = voxelIndex * BytesPerVoxel;
        Array.Clear(readyVoxels, byteIndex, BytesPerVoxel);
        Array.Clear(
            readyLiquidMetadata,
            voxelIndex * LiquidMetadataChannels,
            LiquidMetadataChannels);
        ClearReadyOccupancy(localX, localY, localZ);

        samplePosition.Set(worldX, worldY, worldZ);
        IBlockAccessor accessor = api.World.BlockAccessor;
        Block? block = accessor.GetBlock(samplePosition, BlockLayersAccess.Solid);
        Block? fluid = accessor.GetBlock(samplePosition, BlockLayersAccess.Fluid);
        bool hasSolid = block is not null && IsTraceableSolid(block);
        bool hasFluidLayerBlock = fluid is not null
            && fluid.Id != 0
            && fluid.BlockMaterial != EnumBlockMaterial.Air;
        bool hasLiquid = hasFluidLayerBlock && IsOpticalLiquid(fluid!, accessor, samplePosition);
        if (hasSolid)
        {
            WriteBlockMaterial(
                readyVoxels,
                block!,
                byteIndex,
                worldX,
                worldY,
                worldZ,
                partialGeometry: HasPotentialPartialGeometry(block!));
            WriteOccupancy(
                readyOccupancy,
                block!,
                localX,
                localY,
                localZ,
                worldX,
                worldY,
                worldZ,
                countStatistics: false);
        }
        else if (hasFluidLayerBlock)
        {
            WriteBlockMaterial(
                readyVoxels,
                fluid!,
                byteIndex,
                worldX,
                worldY,
                worldZ,
                64);
        }

        if (hasLiquid)
        {
            WriteLiquidMetadata(
                readyLiquidMetadata,
                voxelIndex,
                liquidOptics.GetProfileId(fluid!),
                VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface,
                EncodeLiquidLevel(fluid!));
        }
        else if (block is not null
            && TryGetVisibleContainedLiquid(
                block,
                samplePosition,
                out byte containedProfileId,
                out byte containedSurfaceHeight))
        {
            WriteLiquidMetadata(
                readyLiquidMetadata,
                voxelIndex,
                containedProfileId,
                VoxelLiquidFlags.Contained | VoxelLiquidFlags.VisibleSurface,
                containedSurfaceHeight);
        }

        byte[] material = new byte[BytesPerVoxel];
        Array.Copy(readyVoxels, byteIndex, material, 0, BytesPerVoxel);
        byte[] occupancy = CopyReadyOccupancy(localX, localY, localZ);
        byte[] liquidMetadata = new byte[LiquidMetadataChannels];
        Array.Copy(
            readyLiquidMetadata,
            voxelIndex * LiquidMetadataChannels,
            liquidMetadata,
            0,
            LiquidMetadataChannels);
        VoxelSceneBlockUpdate update = new(
            localX,
            localY,
            localZ,
            material,
            occupancy,
            liquidMetadata,
            []);
        int existing = pendingBlockUpdates.FindIndex(
            candidate => candidate.LocalX == localX
                && candidate.LocalY == localY
                && candidate.LocalZ == localZ);
        if (existing >= 0)
        {
            pendingBlockUpdates[existing] = update;
        }
        else
        {
            pendingBlockUpdates.Add(update);
        }
    }

    /// <summary>Clears the 4-cubed fine occupancy region owned by one main voxel.</summary>
    /// <param name="localX">Main-grid X.</param>
    /// <param name="localY">Main-grid Y.</param>
    /// <param name="localZ">Main-grid Z.</param>
    private void ClearReadyOccupancy(int localX, int localY, int localZ)
    {
        for (int subZ = 0; subZ < OccupancyScale; subZ++)
        {
            for (int subY = 0; subY < OccupancyScale; subY++)
            {
                int occupancyX = localX * OccupancyScale;
                int occupancyY = localY * OccupancyScale + subY;
                int occupancyZ = localZ * OccupancyScale + subZ;
                int occupancyIndex = (occupancyZ * OccupancyHeight + occupancyY)
                    * OccupancyWidth + occupancyX;
                Array.Clear(readyOccupancy, occupancyIndex, OccupancyScale);
            }
        }
    }

    /// <summary>Copies one 4-cubed occupancy tile into tightly packed update storage.</summary>
    /// <param name="localX">Main-grid X.</param>
    /// <param name="localY">Main-grid Y.</param>
    /// <param name="localZ">Main-grid Z.</param>
    /// <returns>64-byte X-major occupancy tile.</returns>
    private byte[] CopyReadyOccupancy(int localX, int localY, int localZ)
    {
        byte[] occupancy = new byte[OccupancyScale * OccupancyScale * OccupancyScale];
        for (int subZ = 0; subZ < OccupancyScale; subZ++)
        {
            for (int subY = 0; subY < OccupancyScale; subY++)
            {
                int sourceX = localX * OccupancyScale;
                int sourceY = localY * OccupancyScale + subY;
                int sourceZ = localZ * OccupancyScale + subZ;
                int sourceIndex = (sourceZ * OccupancyHeight + sourceY)
                    * OccupancyWidth + sourceX;
                int destinationIndex = (subZ * OccupancyScale + subY) * OccupancyScale;
                Array.Copy(
                    readyOccupancy,
                    sourceIndex,
                    occupancy,
                    destinationIndex,
                    OccupancyScale);
            }
        }
        return occupancy;
    }

    /// <summary>Finds the highest optical fluid surface in one independent footprint column.</summary>
    /// <param name="localX">Liquid-footprint X.</param>
    /// <param name="localZ">Liquid-footprint Z.</param>
    /// <param name="worldX">World X.</param>
    /// <param name="worldZ">World Z.</param>
    /// <returns>Four-byte column surface payload.</returns>
    private byte[] RecalculateFluidColumn(int localX, int localZ, int worldX, int worldZ)
    {
        byte[] surface = new byte[FluidSurfaceChannels];
        IBlockAccessor accessor = api.World.BlockAccessor;
        for (int localY = 0; localY < Height; localY++)
        {
            samplePosition.Set(worldX, originY + localY, worldZ);
            Block? fluid = accessor.GetBlock(samplePosition, BlockLayersAccess.Fluid);
            if (fluid is not null
                && fluid.Id != 0
                && fluid.BlockMaterial != EnumBlockMaterial.Air
                && IsOpticalLiquid(fluid, accessor, samplePosition))
            {
                surface[0] = checked((byte)(localY + 1));
                surface[1] = liquidOptics.GetProfileId(fluid);
                surface[2] = EncodeLiquidLevel(fluid);
                surface[3] = (byte)(VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface);
            }
        }

        Array.Copy(
            surface,
            0,
            readyFluidSurface,
            (localZ * FluidSurfaceWidth + localX) * FluidSurfaceChannels,
            FluidSurfaceChannels);
        return surface;
    }

    /// <summary>Recomputes and queues one column of the independent static liquid footprint.</summary>
    /// <param name="worldX">Changed world X.</param>
    /// <param name="worldZ">Changed world Z.</param>
    private void UpdateReadyFluidSurface(int worldX, int worldZ)
    {
        if (!TryMapFluidSurfaceColumn(
                worldX,
                worldZ,
                fluidSurfaceOriginX,
                fluidSurfaceOriginZ,
                out int localX,
                out int localZ))
        {
            return;
        }

        byte[] surface = RecalculateFluidColumn(localX, localZ, worldX, worldZ);
        VoxelFluidSurfaceUpdate update = new(localX, localZ, surface);
        int existing = pendingFluidSurfaceUpdates.FindIndex(
            candidate => candidate.LocalX == localX && candidate.LocalZ == localZ);
        if (existing >= 0)
        {
            pendingFluidSurfaceUpdates[existing] = update;
        }
        else
        {
            pendingFluidSurfaceUpdates.Add(update);
        }
    }

    /// <summary>Recognizes world liquids from public state/code contracts with a mod-safe fallback.</summary>
    /// <param name="block">Fluid-layer block candidate.</param>
    /// <param name="accessor">World accessor used by dynamic liquid-code fallback.</param>
    /// <param name="position">World block position.</param>
    /// <returns>Whether the block should receive liquid optics.</returns>
    private static bool IsOpticalLiquid(
        Block block,
        IBlockAccessor accessor,
        BlockPos position)
    {
        if (block.IsLiquid()
            || block.MatterState == EnumMatterState.Liquid
            || !string.IsNullOrWhiteSpace(block.LiquidCode))
        {
            return true;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(block.GetLiquidCode(accessor, position));
        }
        catch
        {
            // A modded accessor may require a fully initialized block entity.
            // MatterState/LiquidCode remain the safe, allocation-free contract.
            return false;
        }
    }

    /// <summary>Clamps Vintage Story's liquid level to its documented three-bit range.</summary>
    /// <param name="block">Liquid block.</param>
    /// <returns>Level 0..7.</returns>
    private static byte EncodeLiquidLevel(Block block)
    {
        return checked((byte)Math.Clamp(block.LiquidLevel, 0, 7));
    }

    /// <summary>Writes one voxel's profile/flags/level ABI into RGBA8 storage.</summary>
    /// <param name="destination">Complete voxel liquid metadata volume.</param>
    /// <param name="voxelIndex">Flat main-grid voxel index.</param>
    /// <param name="profileId">Optical LUT row.</param>
    /// <param name="flags">Fluid/container/visibility bits.</param>
    /// <param name="surfaceOrLevel">Liquid level or encoded contained-surface height.</param>
    private static void WriteLiquidMetadata(
        byte[] destination,
        int voxelIndex,
        byte profileId,
        VoxelLiquidFlags flags,
        byte surfaceOrLevel)
    {
        int offset = voxelIndex * LiquidMetadataChannels;
        destination[offset] = profileId;
        destination[offset + 1] = (byte)flags;
        destination[offset + 2] = surfaceOrLevel;
        destination[offset + 3] = 0;
    }

    /// <summary>Replaces column metadata only when the candidate is at least as high as the stored surface.</summary>
    /// <param name="destination">RGBA8 surface column texture.</param>
    /// <param name="localX">Main-grid X.</param>
    /// <param name="localY">Candidate main-grid Y.</param>
    /// <param name="localZ">Main-grid Z.</param>
    /// <param name="profileId">Optical LUT row.</param>
    /// <param name="liquidLevel">Vintage Story level 0..7.</param>
    /// <param name="flags">Surface semantics.</param>
    private static void WriteFluidColumn(
        byte[] destination,
        int localX,
        int localY,
        int localZ,
        byte profileId,
        byte liquidLevel,
        VoxelLiquidFlags flags)
    {
        int offset = (localZ * FluidSurfaceWidth + localX) * FluidSurfaceChannels;
        byte encodedSurface = checked((byte)(localY + 1));
        if (encodedSurface < destination[offset])
        {
            return;
        }

        // Channel R remains exactly the historical localY + 1 encoding.
        destination[offset] = encodedSurface;
        destination[offset + 1] = profileId;
        destination[offset + 2] = liquidLevel;
        destination[offset + 3] = (byte)flags;
    }

    /// <summary>Resolves open inventory-backed liquid volume, profile, and fill-derived surface height.</summary>
    /// <param name="block">Container block type.</param>
    /// <param name="position">Live block-entity position.</param>
    /// <param name="profileId">Contained collectible optical LUT row.</param>
    /// <param name="surfaceHeight">Block-local surface Y encoded UNorm8.</param>
    /// <returns>Whether a non-empty visible liquid slot satisfies the JSON contract.</returns>
    private bool TryGetVisibleContainedLiquid(
        Block block,
        BlockPos position,
        out byte profileId,
        out byte surfaceHeight)
    {
        profileId = LiquidOpticalRegistry.NoLiquidProfileId;
        surfaceHeight = 0;
        if (!cachedLiquidContainers.TryGetValue(block.Id, out LiquidContainerOptics? cached))
        {
            cached = LiquidContainerOptics.TryRead(block, out LiquidContainerOptics parsed)
                || LiquidOpticalFallbackCatalog.TryResolveContainer(
                    block.Code,
                    liquidOptics.ContainerFallbacks,
                    out parsed)
                    ? parsed
                    : null;
            cachedLiquidContainers[block.Id] = cached;
        }

        if (cached is not LiquidContainerOptics metadata)
        {
            return false;
        }

        BlockEntity? blockEntity = api.World.BlockAccessor.GetBlockEntity(position);
        if (blockEntity is not IBlockEntityContainer container
            || !metadata.IsSurfaceVisible(blockEntity)
            || metadata.ContentSlot >= container.Inventory.Count)
        {
            return false;
        }

        ItemSlot? slot = container.Inventory[metadata.ContentSlot];
        ItemStack? stack = slot?.Itemstack;
        CollectibleObject? collectible = stack?.Collectible;
        if (stack is null
            || collectible is null
            || !IsContainerLiquid(collectible))
        {
            return false;
        }

        JsonObject? attributes = collectible.Attributes;
        float itemsPerLitre = attributes is null
            ? 0.0f
            : attributes["waterTightContainerProps"]["itemsPerLitre"].AsFloat(0.0f);
        if (!float.IsFinite(itemsPerLitre) || itemsPerLitre <= 0.0f)
        {
            return false;
        }

        float fillRatio = stack.StackSize / itemsPerLitre / metadata.CapacityLitres;
        if (!float.IsFinite(fillRatio) || fillRatio <= 0.0f)
        {
            return false;
        }

        profileId = liquidOptics.GetProfileId(collectible);
        surfaceHeight = metadata.EncodeSurfaceHeight(fillRatio);
        return true;
    }

    /// <summary>Recognizes liquid items by state/code or the standard watertight-container contract.</summary>
    /// <param name="collectible">Contained stack collectible.</param>
    /// <returns>Whether its volume may form a rendered free surface.</returns>
    private static bool IsContainerLiquid(CollectibleObject collectible)
    {
        return collectible.IsLiquid()
            || collectible.MatterState == EnumMatterState.Liquid
            || collectible.Attributes?["waterTightContainerProps"].Exists == true;
    }

    /// <summary>
    /// Chooses authoritative per-instance geometry: tessellated mesh, canonical cube, alpha-tested
    /// static mesh, collision/selection boxes, then conservative absorption fallback. Chiseled,
    /// anvil, lantern, and crossed-plane shapes therefore retain their actual shadow origin.
    /// </summary>
    /// <param name="destination">Fine occupancy volume.</param>
    /// <param name="block">Solid block definition.</param>
    /// <param name="localBlockX">Main-grid X.</param>
    /// <param name="localBlockY">Main-grid Y.</param>
    /// <param name="localBlockZ">Main-grid Z.</param>
    /// <param name="worldX">World X.</param>
    /// <param name="worldY">World Y.</param>
    /// <param name="worldZ">World Z.</param>
    /// <param name="countStatistics">Whether geometry-source diagnostics are accumulated.</param>
    private void WriteOccupancy(
        byte[] destination,
        Block block,
        int localBlockX,
        int localBlockY,
        int localBlockZ,
        int worldX,
        int worldY,
        int worldZ,
        bool countStatistics)
    {
        CachedBlockOccupancy cached = GetCachedBlockOccupancy(block);
        if (cached.GeometryKind == BlockGeometryKind.DynamicInstance)
        {
            samplePosition.Set(worldX, worldY, worldZ);
            bool hasInstanceTessellation = TryGetInstanceMeshOccupancy(
                block,
                samplePosition,
                out InstanceMeshOccupancy tessellatedInstance);
            bool replacesDefaultMesh = InstanceGeometryReplacesDefaultMesh(block)
                || (hasInstanceTessellation && tessellatedInstance.SkipsDefaultMesh);
            if (TryResolveTessellatedInstanceOccupancy(
                    hasInstanceTessellation,
                    tessellatedInstance.CapturedMeshCount,
                    tessellatedInstance.SkipsDefaultMesh,
                    replacesDefaultMesh,
                    cached.Mask,
                    tessellatedInstance.Mask,
                    out ulong tessellatedMask))
            {
                if (tessellatedMask != 0)
                {
                    if (countStatistics)
                    {
                        meshOccupancyBlocks++;
                    }
                    WriteOccupancyMask(
                        destination,
                        tessellatedMask,
                        localBlockX,
                        localBlockY,
                        localBlockZ);
                }

                // A replacing OnTesselation result is authoritative even when
                // all captured triangles are transparent (for example chiseled
                // glass) or the authored instance is intentionally empty. Never
                // promote that zero mask to its full interaction AABB.
                return;
            }

            ulong dynamicCollisionMask = BuildBoxMask(
                block.GetCollisionBoxes(api.World.BlockAccessor, samplePosition));
            ulong dynamicSelectionMask = BuildBoxMask(
                block.GetSelectionBoxes(api.World.BlockAccessor, samplePosition));
            ulong instanceMask = ResolveDynamicInstanceOccupancy(
                cached.Mask,
                cached.HasDetailedMesh,
                replacesDefaultMesh,
                dynamicCollisionMask,
                dynamicSelectionMask,
                block.LightAbsorption > 0);
            if (instanceMask == 0)
            {
                return;
            }

            if (countStatistics)
            {
                bool usedDefaultMesh = !replacesDefaultMesh && cached.Mask != 0;
                if (usedDefaultMesh)
                {
                    meshOccupancyBlocks++;
                }
                else if (dynamicCollisionMask is not 0 and not ulong.MaxValue)
                {
                    collisionOccupancyBlocks++;
                }
                else if (dynamicSelectionMask is not 0 and not ulong.MaxValue)
                {
                    selectionOccupancyBlocks++;
                }
                else
                {
                    CountFallbackOccupancy(
                        block,
                        hasInstanceTessellation && tessellatedInstance.CapturedMeshCount > 0
                            ? "dynamic-instance-mesh-empty"
                            : dynamicCollisionMask == ulong.MaxValue
                                ? "dynamic-full-collision"
                                : dynamicSelectionMask == ulong.MaxValue
                                    ? "dynamic-full-selection"
                                    : "dynamic-absorbing-no-shape");
                }
            }

            WriteOccupancyMask(
                destination,
                instanceMask,
                localBlockX,
                localBlockY,
                localBlockZ);
            return;
        }

        if (cached.GeometryKind == BlockGeometryKind.FullCubeStatic)
        {
            if (countStatistics)
            {
                meshOccupancyBlocks++;
            }
            WriteOccupancyMask(
                destination,
                ulong.MaxValue,
                localBlockX,
                localBlockY,
                localBlockZ);
            return;
        }

        if (cached.HasDetailedMesh)
        {
            // A real alpha-tested crossed-plane mesh with no opaque texels is
            // empty, not a cue to replace it with a full collision/selection
            // AABB. This keeps vegetation rooted at its authored local triangles.
            if (cached.Mask == 0)
            {
                return;
            }
            if (countStatistics)
            {
                meshOccupancyBlocks++;
            }
            WriteOccupancyMask(
                destination,
                cached.Mask,
                localBlockX,
                localBlockY,
                localBlockZ);
            return;
        }

        samplePosition.Set(worldX, worldY, worldZ);
        ulong collisionMask = BuildBoxMask(block.GetCollisionBoxes(api.World.BlockAccessor, samplePosition));
        ulong selectionMask = 0;

        ulong occupancyMask;
        if (collisionMask != 0 && collisionMask != ulong.MaxValue)
        {
            occupancyMask = collisionMask;
            if (countStatistics)
            {
                collisionOccupancyBlocks++;
            }
        }
        else
        {
            selectionMask = BuildBoxMask(block.GetSelectionBoxes(api.World.BlockAccessor, samplePosition));
            if (selectionMask != 0 && selectionMask != ulong.MaxValue)
            {
                occupancyMask = selectionMask;
                if (countStatistics)
                {
                    selectionOccupancyBlocks++;
                }
            }
            else if (cached.Mask != 0)
            {
                occupancyMask = cached.Mask;
                if (countStatistics)
                {
                    meshOccupancyBlocks++;
                }
            }
            else if (collisionMask != 0 || selectionMask != 0 || block.LightAbsorption > 0)
            {
                occupancyMask = collisionMask != 0
                    ? collisionMask
                    : selectionMask != 0
                        ? selectionMask
                        : ulong.MaxValue;
                if (countStatistics)
                {
                    CountFallbackOccupancy(
                        block,
                        collisionMask == ulong.MaxValue
                            ? "static-full-collision"
                            : selectionMask == ulong.MaxValue
                                ? "static-full-selection"
                                : "static-absorbing-no-shape");
                }
            }
            else
            {
                return;
            }
        }

        WriteOccupancyMask(
            destination,
            occupancyMask,
            localBlockX,
            localBlockY,
            localBlockZ);
    }

    /// <summary>Records exact block-code/reason cardinality when detailed geometry is unavailable.</summary>
    /// <param name="block">Block receiving conservative occupancy.</param>
    /// <param name="reason">Stable diagnostic reason code.</param>
    private void CountFallbackOccupancy(Block block, string reason)
    {
        fallbackOccupancyBlocks++;
        string code = block.Code?.ToString() ?? $"id:{block.Id}";
        (string Code, string Reason) key = (code, reason);
        fallbackOccupancyHistogram.TryGetValue(key, out int count);
        fallbackOccupancyHistogram[key] = count + 1;
    }

    /// <summary>Builds/caches type-static mesh occupancy while marking entity-backed shapes dynamic.</summary>
    /// <param name="block">Loaded block type.</param>
    /// <returns>Geometry classification, 4-cubed mask, and triangle diagnostics.</returns>
    private CachedBlockOccupancy GetCachedBlockOccupancy(Block block)
    {
        if (cachedBlockOccupancy.TryGetValue(block.Id, out CachedBlockOccupancy cached))
        {
            return cached;
        }

        bool hasInstanceGeometry = !string.IsNullOrWhiteSpace(block.EntityClass);
        try
        {
            MeshData mesh = api.TesselatorManager.GetDefaultBlockMesh(block);
            cached = BuildMeshMask(block, mesh);
        }
        catch (Exception exception)
        {
            api.Logger.Debug(
                "[VintageRTX] Could not obtain the default mesh for {0}: {1}",
                block.Code?.ToString() ?? block.Id.ToString(),
                exception.Message);
            cached = default;
        }

        if (hasInstanceGeometry)
        {
            // GetDefaultBlockMesh remains the reusable base geometry for a
            // lantern cage, anvil or other BlockEntity-backed static shape.
            // Only the instance contribution is position-dependent and must
            // never be cached globally by block id.
            cached = cached with { GeometryKind = BlockGeometryKind.DynamicInstance };
        }

        cachedBlockOccupancy[block.Id] = cached;
        string code = block.Code?.ToString() ?? string.Empty;
        bool isCrossedPlane = block.DrawType == EnumDrawType.Cross;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID"))
            && (code.Contains("lantern", StringComparison.OrdinalIgnoreCase)
                || code.Contains("anvil", StringComparison.OrdinalIgnoreCase)
                || isCrossedPlane))
        {
            CasterGeometryEvidence evidence = MeasureCasterGeometry(
                cached,
                isCrossedPlane,
                null);
            api.Logger.Notification(
                "[VintageRTX.Test] Detailed caster {0}: opaque triangles={1}, alpha-tested triangles={2}, transparent triangles ignored={3}, occupied subvoxels={4}/64.",
                code,
                cached.OpaqueTriangles,
                cached.AlphaTestedTriangles,
                cached.TransparentTriangles,
                System.Numerics.BitOperations.PopCount(cached.Mask));
            api.Logger.Notification(
                "[VintageRTX.Test] Geometry evidence {0}: kind={1}, detailed non-cube={2}, crossed planes={3}, coarse occupied={4}/64.",
                code,
                cached.GeometryKind,
                evidence.DetailedNonCube,
                evidence.CrossedPlanePartial,
                evidence.CoarseOccupied);
        }
        return cached;
    }

    /// <summary>Rasterizes default block triangles with material-aware alpha rejection into 4-cubed occupancy.</summary>
    /// <param name="block">Owning block used for shadow/material semantics.</param>
    /// <param name="mesh">Default tessellated mesh, possibly absent.</param>
    /// <returns>Mask, geometry class, and opaque/alpha/transparent triangle counts.</returns>
    private CachedBlockOccupancy BuildMeshMask(Block block, MeshData? mesh)
    {
        if (mesh?.xyz is not { Length: >= 9 }
            || mesh.Indices is not { Length: >= 3 }
            || mesh.VerticesCount < 3
            || mesh.IndicesCount < 3)
        {
            return default;
        }

        int vertexCount = Math.Min(mesh.VerticesCount, mesh.xyz.Length / 3);
        int indexCount = Math.Min(mesh.IndicesCount, mesh.Indices.Length);
        if (IsCanonicalUnitCubeMesh(mesh))
        {
            return new CachedBlockOccupancy(
                ulong.MaxValue,
                false,
                12,
                0,
                0,
                BlockGeometryKind.FullCubeStatic);
        }

        ulong mask = 0;
        int opaqueTriangles = 0;
        int transparentTriangles = 0;
        int alphaTestedTriangles = 0;
        List<TextureAlphaCandidate>? alphaCandidates = null;
        short[]? renderPasses = mesh.RenderPassesAndExtraBits;
        int indicesPerFace = mesh.IndicesPerFace > 0 ? mesh.IndicesPerFace : 6;
        for (int triangleOffset = 0; triangleOffset + 2 < indexCount; triangleOffset += 3)
        {
            int faceIndex = triangleOffset / indicesPerFace;
            EnumChunkRenderPass renderPass = renderPasses is { Length: > 0 }
                && faceIndex < renderPasses.Length
                    ? (EnumChunkRenderPass)(renderPasses[faceIndex] & 0x03ff)
                    : EnumChunkRenderPass.Opaque;
            if (!ShouldCastShadow(renderPass))
            {
                transparentTriangles++;
                continue;
            }

            int indexA = mesh.Indices[triangleOffset];
            int indexB = mesh.Indices[triangleOffset + 1];
            int indexC = mesh.Indices[triangleOffset + 2];
            if ((uint)indexA >= vertexCount || (uint)indexB >= vertexCount || (uint)indexC >= vertexCount)
            {
                continue;
            }

            opaqueTriangles++;
            Vec3f a = ReadVertex(mesh.xyz, indexA);
            Vec3f b = ReadVertex(mesh.xyz, indexB);
            Vec3f c = ReadVertex(mesh.xyz, indexC);
            TextureAlphaSampler? alphaSampler = null;
            if (ShouldTextureAlphaTest(block, renderPass))
            {
                alphaCandidates ??= BuildTextureAlphaCandidates(block);
                alphaSampler = ResolveTextureAlphaSampler(
                    mesh,
                    faceIndex,
                    indexA,
                    indexB,
                    indexC,
                    alphaCandidates);
                if (alphaSampler is not null)
                {
                    alphaTestedTriangles++;
                }
            }

            for (int subZ = 0; subZ < OccupancyScale; subZ++)
            {
                float sampleZ = (subZ + 0.5f) / OccupancyScale;
                for (int subY = 0; subY < OccupancyScale; subY++)
                {
                    float sampleY = (subY + 0.5f) / OccupancyScale;
                    for (int subX = 0; subX < OccupancyScale; subX++)
                    {
                        int bitIndex = (subZ * OccupancyScale + subY) * OccupancyScale + subX;
                        ulong bit = 1UL << bitIndex;
                        if ((mask & bit) != 0)
                        {
                            continue;
                        }

                        Vec3f center = new(
                            (subX + 0.5f) / OccupancyScale,
                            sampleY,
                            sampleZ);
                        if (TriangleIntersectsVoxel(center, a, b, c)
                            && (alphaSampler is null
                                || TriangleCellOpaqueAlphaCoverage(
                                    center,
                                    OccupancyScale,
                                    a,
                                    b,
                                    c,
                                    ReadUv(mesh.Uv, indexA),
                                    ReadUv(mesh.Uv, indexB),
                                    ReadUv(mesh.Uv, indexC),
                                    alphaSampler) > 0))
                        {
                            mask |= bit;
                        }
                    }
                }
            }
        }

        return new CachedBlockOccupancy(
            mask,
            true,
            opaqueTriangles,
            transparentTriangles,
            alphaTestedTriangles,
            BlockGeometryKind.StaticComplex);
    }

    /// <summary>Builds/caches 16-cubed alpha-aware caster geometry and a flame-safe emission offset.</summary>
    /// <param name="block">Emitter block type.</param>
    /// <param name="occupancy">Its previously classified static occupancy.</param>
    /// <returns>Detailed mask plus block-local emission origin.</returns>
    private CachedLightCaster GetLightCaster(Block block, CachedBlockOccupancy occupancy)
    {
        if (cachedLightCasters.TryGetValue(block.Id, out CachedLightCaster cached))
        {
            return cached;
        }

        try
        {
            MeshData mesh = api.TesselatorManager.GetDefaultBlockMesh(block);
            Vec3f emissionOffset = FindEmissionOffset(mesh);
            // A lantern caster is not only its alpha-tested bars: the opaque
            // roof, uprights and lower plate must be present as well. Build the
            // fine mask for every proven complex emitter mesh; transparent
            // glass faces are still rejected per triangle inside the builder.
            byte[] mask = occupancy.HasDetailedMesh && occupancy.OpaqueTriangles > 0
                ? BuildLightCasterMask(block, mesh)
                : [];
            if (mask.Length == LightCasterVoxelCount)
            {
                ClearEmissionPocket(mask, emissionOffset);
            }
            cached = new CachedLightCaster(mask, emissionOffset);

            string code = block.Code?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID"))
                && code.Contains("lantern", StringComparison.OrdinalIgnoreCase))
            {
                CasterGeometryEvidence evidence = MeasureCasterGeometry(
                    occupancy,
                    isCrossedPlane: false,
                    mask);
                string verdict = evidence.BaseIncluded
                    && evidence.CageHeightCovered
                    && evidence.TransparentSurfaceIgnored
                        ? "PASS"
                        : "FAIL";
                api.Logger.Notification(
                    "[VintageRTX.Test] Lantern cage evidence {0}: verdict={1}, base included={2}, cage complete={3}, glass ignored={4}, bands lower={5}, middle={6}, upper={7}, empty={8}.",
                    code,
                    verdict,
                    evidence.BaseIncluded,
                    evidence.CageHeightCovered,
                    evidence.TransparentSurfaceIgnored,
                    evidence.FineBands.LowerOccupied,
                    evidence.FineBands.MiddleOccupied,
                    evidence.FineBands.UpperOccupied,
                    evidence.FineBands.Empty);
            }
        }
        catch (Exception exception)
        {
            api.Logger.Debug(
                "[VintageRTX] Could not build the fine light-caster mask for {0}: {1}",
                block.Code?.ToString() ?? block.Id.ToString(),
                exception.Message);
            cached = new CachedLightCaster([], new Vec3f(0.5f, 0.5f, 0.5f));
        }

        cachedLightCasters[block.Id] = cached;
        return cached;
    }

    /// <summary>Finds the centroid of glow-flagged vertices, falling back to block center.</summary>
    /// <param name="mesh">Emitter mesh with per-vertex glow flags.</param>
    /// <returns>Block-local light origin clamped to the unit cube.</returns>
    internal static Vec3f FindEmissionOffset(MeshData? mesh)
    {
        if (mesh?.xyz is not { Length: >= 3 }
            || mesh.Flags is not { Length: > 0 }
            || mesh.VerticesCount <= 0)
        {
            return new Vec3f(0.5f, 0.5f, 0.5f);
        }

        int vertexCount = Math.Min(
            mesh.VerticesCount,
            Math.Min(mesh.xyz.Length / 3, mesh.Flags.Length));
        List<(Vec3f Position, int Glow)> emissive = [];
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            int glow = mesh.Flags[vertexIndex] & VertexFlags.GlowLevelBitMask;
            if (glow >= 96)
            {
                emissive.Add((ReadVertex(mesh.xyz, vertexIndex), glow));
            }
        }

        if (emissive.Count == 0)
        {
            return new Vec3f(0.5f, 0.5f, 0.5f);
        }

        const float clusterRadiusSquared = 0.20f * 0.20f;
        int bestIndex = 0;
        int bestScore = int.MinValue;
        for (int candidateIndex = 0; candidateIndex < emissive.Count; candidateIndex++)
        {
            int score = 0;
            Vec3f candidate = emissive[candidateIndex].Position;
            for (int neighborIndex = 0; neighborIndex < emissive.Count; neighborIndex++)
            {
                if (LengthSquared(Subtract(emissive[neighborIndex].Position, candidate))
                    <= clusterRadiusSquared)
                {
                    score += emissive[neighborIndex].Glow;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = candidateIndex;
            }
        }

        Vec3f clusterCenter = emissive[bestIndex].Position;
        float totalWeight = 0.0f;
        Vec3f weighted = new(0.0f, 0.0f, 0.0f);
        foreach ((Vec3f position, int glow) in emissive)
        {
            if (LengthSquared(Subtract(position, clusterCenter)) > clusterRadiusSquared)
            {
                continue;
            }

            weighted = Add(weighted, Scale(position, glow));
            totalWeight += glow;
        }

        // Every retained vertex contributes a glow value of at least 96, so a
        // non-empty emissive cluster always has a strictly positive weight.
        return Scale(weighted, 1.0f / totalWeight);
    }

    /// <summary>Clears a 0.17-block bulb around the flame so its own mesh cannot occlude every outgoing ray.</summary>
    /// <param name="mask">Mutable 16-cubed caster mask.</param>
    /// <param name="emissionOffset">Block-local flame centroid.</param>
    internal static void ClearEmissionPocket(byte[] mask, Vec3f emissionOffset)
    {
        // The glow vertices mark the flame volume, not a zero-size point. Keep a
        // small physically empty bulb around it so the wick/flame mesh cannot
        // shadow every outgoing ray while the texture-cut cage stays outside.
        const float pocketRadius = 0.17f;
        float radiusSquared = pocketRadius * pocketRadius;
        for (int z = 0; z < LightCasterScale; z++)
        {
            for (int y = 0; y < LightCasterScale; y++)
            {
                for (int x = 0; x < LightCasterScale; x++)
                {
                    Vec3f center = new(
                        (x + 0.5f) / LightCasterScale,
                        (y + 0.5f) / LightCasterScale,
                        (z + 0.5f) / LightCasterScale);
                    if (LengthSquared(Subtract(center, emissionOffset)) <= radiusSquared)
                    {
                        int index = (z * LightCasterScale + y) * LightCasterScale + x;
                        mask[index] = 0;
                    }
                }
            }
        }
    }

    /// <summary>Counts lower/middle/upper occupied samples to diagnose incomplete lantern cage capture.</summary>
    /// <param name="mask">Optional 16-cubed caster mask.</param>
    /// <returns>Band and empty-sample cardinalities totaling <see cref="LightCasterVoxelCount"/>.</returns>
    internal static LightCasterBandCoverage MeasureLightCasterBands(byte[]? mask)
    {
        int lower = 0;
        int middle = 0;
        int upper = 0;
        int empty = 0;
        for (int y = 0; y < LightCasterScale; y++)
        {
            for (int z = 0; z < LightCasterScale; z++)
            {
                for (int x = 0; x < LightCasterScale; x++)
                {
                    int index = (z * LightCasterScale + y) * LightCasterScale + x;
                    bool occupied = mask is { Length: LightCasterVoxelCount }
                        && mask[index] != 0;
                    if (!occupied)
                    {
                        empty++;
                    }
                    else if (y < LightCasterScale / 3)
                    {
                        lower++;
                    }
                    else if (y >= LightCasterScale - LightCasterScale / 3)
                    {
                        upper++;
                    }
                    else
                    {
                        middle++;
                    }
                }
            }
        }

        return new LightCasterBandCoverage(lower, middle, upper, empty);
    }

    /// <summary>
    /// Converts raw occupancy counters into stable semantic evidence used by
    /// runtime captures. The fine-mask predicates deliberately require a
    /// complete 16-cubed mask so a missing or truncated buffer cannot report a
    /// valid lantern base/cage by accident.
    /// </summary>
    /// <param name="occupancy">Coarse mesh classification and triangle provenance.</param>
    /// <param name="isCrossedPlane">Whether the authored block draw type is crossed planes.</param>
    /// <param name="fineMask">Optional alpha-aware 16-cubed caster mask.</param>
    /// <returns>Geometry predicates suitable for tests and stable runtime logs.</returns>
    internal static CasterGeometryEvidence MeasureCasterGeometry(
        CachedBlockOccupancy occupancy,
        bool isCrossedPlane,
        byte[]? fineMask)
    {
        int coarseOccupied = System.Numerics.BitOperations.PopCount(occupancy.Mask);
        bool detailedNonCube = occupancy.HasDetailedMesh
            && occupancy.GeometryKind is BlockGeometryKind.StaticComplex
                or BlockGeometryKind.DynamicInstance
            && coarseOccupied is > 0 and < 64;
        LightCasterBandCoverage fineBands = MeasureLightCasterBands(fineMask);
        bool hasCompleteFineMask = fineMask is { Length: LightCasterVoxelCount };
        bool baseIncluded = hasCompleteFineMask && fineBands.LowerOccupied > 0;
        bool cageHeightCovered = baseIncluded
            && fineBands.MiddleOccupied > 0
            && fineBands.UpperOccupied > 0;
        bool transparentSurfaceIgnored = hasCompleteFineMask
            && occupancy.TransparentTriangles > 0
            && fineBands.Empty > 0;
        // Cross-drawn vegetation is defined by both its two authored planes and
        // their cutout texture. Merely finding a non-cubic plane mask is not
        // sufficient evidence: without alpha-tested triangles every blade is a
        // solid diagonal rectangle and casts the former oversized shadow.
        bool crossedPlanePartial = isCrossedPlane
            && detailedNonCube
            && occupancy.AlphaTestedTriangles > 0;
        return new CasterGeometryEvidence(
            coarseOccupied,
            detailedNonCube,
            crossedPlanePartial,
            fineBands,
            baseIncluded,
            cageHeightCovered,
            transparentSurfaceIgnored);
    }

    /// <summary>Rasterizes shadow-casting triangles into 16-cubed occupancy using texture alpha tests.</summary>
    /// <param name="block">Block supplying texture graph and render-pass semantics.</param>
    /// <param name="mesh">Detailed default mesh.</param>
    /// <returns>Binary mask; transparent glass contributes no caster samples.</returns>
    private byte[] BuildLightCasterMask(Block block, MeshData? mesh)
    {
        if (mesh?.xyz is not { Length: >= 9 }
            || mesh.Indices is not { Length: >= 3 }
            || mesh.VerticesCount < 3
            || mesh.IndicesCount < 3)
        {
            return [];
        }

        int vertexCount = Math.Min(mesh.VerticesCount, mesh.xyz.Length / 3);
        int indexCount = Math.Min(mesh.IndicesCount, mesh.Indices.Length);
        int indicesPerFace = mesh.IndicesPerFace > 0 ? mesh.IndicesPerFace : 6;
        short[]? renderPasses = mesh.RenderPassesAndExtraBits;
        List<TextureAlphaCandidate>? alphaCandidates = null;
        byte[] mask = new byte[LightCasterVoxelCount];

        for (int triangleOffset = 0; triangleOffset + 2 < indexCount; triangleOffset += 3)
        {
            int faceIndex = triangleOffset / indicesPerFace;
            EnumChunkRenderPass renderPass = renderPasses is { Length: > 0 }
                && faceIndex < renderPasses.Length
                    ? (EnumChunkRenderPass)(renderPasses[faceIndex] & 0x03ff)
                    : EnumChunkRenderPass.Opaque;
            if (!ShouldCastShadow(renderPass))
            {
                continue;
            }

            int indexA = mesh.Indices[triangleOffset];
            int indexB = mesh.Indices[triangleOffset + 1];
            int indexC = mesh.Indices[triangleOffset + 2];
            if ((uint)indexA >= vertexCount || (uint)indexB >= vertexCount || (uint)indexC >= vertexCount)
            {
                continue;
            }

            Vec3f a = ReadVertex(mesh.xyz, indexA);
            Vec3f b = ReadVertex(mesh.xyz, indexB);
            Vec3f c = ReadVertex(mesh.xyz, indexC);
            TextureUv uvA = ReadUv(mesh.Uv, indexA);
            TextureUv uvB = ReadUv(mesh.Uv, indexB);
            TextureUv uvC = ReadUv(mesh.Uv, indexC);
            TextureAlphaSampler? alphaSampler = null;
            if (ShouldTextureAlphaTest(block, renderPass))
            {
                alphaCandidates ??= BuildTextureAlphaCandidates(block);
                alphaSampler = ResolveTextureAlphaSampler(
                    mesh,
                    faceIndex,
                    indexA,
                    indexB,
                    indexC,
                    alphaCandidates);
            }

            for (int subZ = 0; subZ < LightCasterScale; subZ++)
            {
                for (int subY = 0; subY < LightCasterScale; subY++)
                {
                    for (int subX = 0; subX < LightCasterScale; subX++)
                    {
                        int voxelIndex = (subZ * LightCasterScale + subY)
                            * LightCasterScale + subX;
                        if (mask[voxelIndex] == 255)
                        {
                            continue;
                        }

                        Vec3f center = new(
                            (subX + 0.5f) / LightCasterScale,
                            (subY + 0.5f) / LightCasterScale,
                            (subZ + 0.5f) / LightCasterScale);
                        if (!TriangleIntersectsVoxel(center, a, b, c, LightCasterScale))
                        {
                            continue;
                        }

                        byte coverage = alphaSampler is null
                            ? (byte)255
                            : TriangleCellOpaqueAlphaCoverage(
                                center,
                                LightCasterScale,
                                a,
                                b,
                                c,
                                uvA,
                                uvB,
                                uvC,
                                alphaSampler);
                        mask[voxelIndex] = Math.Max(mask[voxelIndex], coverage);
                    }
                }
            }
        }

        return mask;
    }

    /// <summary>Maps block composite/baked texture sources to their exact atlas rectangles.</summary>
    /// <param name="block">Block whose texture graph is traversed.</param>
    /// <returns>Canonical source/atlas candidates used to sample file alpha.</returns>
    private List<TextureAlphaCandidate> BuildTextureAlphaCandidates(Block block)
    {
        List<TextureAlphaCandidate> candidates = [];
        if (block.Textures is null)
        {
            return candidates;
        }

        TextureAtlasPosition[] atlasPositions = api.BlockTextureAtlas.Positions;
        foreach (CompositeTexture texture in block.Textures.Values)
        {
            if (texture is null)
            {
                continue;
            }

            Stack<CompositeTexture> pending = new();
            HashSet<CompositeTexture> visited = new(ReferenceEqualityComparer.Instance);
            pending.Push(texture);
            while (pending.TryPop(out CompositeTexture? current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                AddBakedAlphaCandidates(current.Base, current.Baked, atlasPositions, candidates);
                if (current.Alternates is not null)
                {
                    foreach (CompositeTexture alternate in current.Alternates)
                    {
                        if (alternate is not null)
                        {
                            pending.Push(alternate);
                        }
                    }
                }

                if (current.Tiles is not null)
                {
                    foreach (CompositeTexture tile in current.Tiles)
                    {
                        if (tile is not null)
                        {
                            pending.Push(tile);
                        }
                    }
                }
            }
        }

        return candidates;
    }

    /// <summary>Traverses baked variants/tiles and registers single-source atlas rectangles.</summary>
    /// <param name="baseTexture">Root fallback source.</param>
    /// <param name="baked">Baked texture graph.</param>
    /// <param name="atlasPositions">Positions indexed by texture sub-ID.</param>
    /// <param name="candidates">Destination candidate list.</param>
    private static void AddBakedAlphaCandidates(
        AssetLocation? baseTexture,
        BakedCompositeTexture? baked,
        TextureAtlasPosition[] atlasPositions,
        List<TextureAlphaCandidate> candidates)
    {
        if (baked is null)
        {
            return;
        }

        Stack<BakedCompositeTexture> pending = new();
        HashSet<BakedCompositeTexture> visited = new(ReferenceEqualityComparer.Instance);
        pending.Push(baked);
        while (pending.TryPop(out BakedCompositeTexture? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (current.TextureSubId >= 0 && current.TextureSubId < atlasPositions.Length)
            {
                TextureAtlasPosition position = atlasPositions[current.TextureSubId];
                if (baseTexture is not null)
                {
                    AddAlphaCandidate(position, baseTexture, candidates);
                }

                if (current.TextureFilenames is not null)
                {
                    foreach (AssetLocation filename in current.TextureFilenames)
                    {
                        if (filename is not null)
                        {
                            AddAlphaCandidate(position, filename, candidates);
                        }
                    }
                }
            }

            if (current.BakedVariants is not null)
            {
                foreach (BakedCompositeTexture variant in current.BakedVariants)
                {
                    if (variant is not null)
                    {
                        pending.Push(variant);
                    }
                }
            }

            if (current.BakedTiles is not null)
            {
                foreach (BakedCompositeTexture tile in current.BakedTiles)
                {
                    if (tile is not null)
                    {
                        pending.Push(tile);
                    }
                }
            }
        }
    }

    /// <summary>Adds one canonical source/rectangle pair without duplicates.</summary>
    /// <param name="position">Atlas rectangle.</param>
    /// <param name="source">Source texture asset.</param>
    /// <param name="candidates">Destination list.</param>
    private static void AddAlphaCandidate(
        TextureAtlasPosition position,
        AssetLocation source,
        List<TextureAlphaCandidate> candidates)
    {
        AssetLocation canonical = CanonicalTextureAsset(source);
        if (candidates.Any(candidate => candidate.Position.atlasTextureId == position.atlasTextureId
            && NearlyEqual(candidate.Position.x1, position.x1)
            && NearlyEqual(candidate.Position.y1, position.y1)
            && candidate.Source.Equals(canonical)))
        {
            return;
        }

        candidates.Add(new TextureAlphaCandidate(position, canonical));
    }

    /// <summary>Matches triangle UV centroid/atlas position to a decoded source-alpha sampler.</summary>
    /// <param name="mesh">Triangle mesh containing UV and optional texture indices.</param>
    /// <param name="faceIndex">Triangle ordinal.</param>
    /// <param name="indexA">First vertex index.</param>
    /// <param name="indexB">Second vertex index.</param>
    /// <param name="indexC">Third vertex index.</param>
    /// <param name="candidates">Known source/atlas mappings.</param>
    /// <returns>Sampler plus atlas rectangle, or null when identity cannot be proven.</returns>
    private TextureAlphaSampler? ResolveTextureAlphaSampler(
        MeshData mesh,
        int faceIndex,
        int indexA,
        int indexB,
        int indexC,
        List<TextureAlphaCandidate> candidates)
    {
        if (mesh.Uv is not { Length: > 0 }
            || mesh.TextureIndices is not { Length: > 0 }
            || mesh.TextureIds is not { Length: > 0 }
            || faceIndex >= mesh.TextureIndices.Length)
        {
            return null;
        }

        int textureIndex = mesh.TextureIndices[faceIndex];
        if ((uint)textureIndex >= mesh.TextureIds.Length)
        {
            return null;
        }

        int atlasTextureId = mesh.TextureIds[textureIndex];
        TextureUv uvA = ReadUv(mesh.Uv, indexA);
        TextureUv uvB = ReadUv(mesh.Uv, indexB);
        TextureUv uvC = ReadUv(mesh.Uv, indexC);
        float centerU = (uvA.U + uvB.U + uvC.U) / 3.0f;
        float centerV = (uvA.V + uvB.V + uvC.V) / 3.0f;

        foreach (TextureAlphaCandidate candidate in candidates
            .Where(candidate => candidate.Position.atlasTextureId == atlasTextureId
                && ContainsUv(candidate.Position, centerU, centerV))
            .OrderBy(candidate => (candidate.Position.x2 - candidate.Position.x1)
                * (candidate.Position.y2 - candidate.Position.y1)))
        {
            TextureAlphaData? alpha = GetTextureAlpha(candidate.Source);
            if (alpha is not null)
            {
                return new TextureAlphaSampler(candidate.Position, alpha);
            }
        }

        return null;
    }

    /// <summary>Loads/caches source PNG alpha in top-left row-major order without retaining decoder objects.</summary>
    /// <param name="source">Canonical source texture asset.</param>
    /// <returns>Decoded alpha bytes or null on unavailable/malformed input.</returns>
    private TextureAlphaData? GetTextureAlpha(AssetLocation source)
    {
        if (cachedTextureAlpha.TryGetValue(source, out TextureAlphaData? cached))
        {
            return cached;
        }

        try
        {
            IAsset? asset = api.Assets.TryGet(source);
            if (asset is null)
            {
                cachedTextureAlpha[source] = null;
                return null;
            }

            using BitmapRef bitmap = asset.ToBitmap(api);
            byte[] alpha = new byte[bitmap.Width * bitmap.Height];
            bool hasTransparency = false;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    byte value = bitmap.GetPixel(x, y).Alpha;
                    alpha[y * bitmap.Width + x] = value;
                    hasTransparency |= value < 255;
                }
            }

            cached = hasTransparency
                ? new TextureAlphaData(bitmap.Width, bitmap.Height, alpha)
                : null;
        }
        catch (Exception exception)
        {
            api.Logger.Debug(
                "[VintageRTX] Could not read alpha from texture {0}: {1}",
                source,
                exception.Message);
            cached = null;
        }

        cachedTextureAlpha[source] = cached;
        return cached;
    }

    /// <summary>Supersamples barycentric UV/alpha over a triangle-cell intersection.</summary>
    /// <param name="center">Candidate subvoxel center in block space.</param>
    /// <param name="scale">Subvoxels per block edge.</param>
    /// <param name="a">First triangle vertex.</param>
    /// <param name="b">Second triangle vertex.</param>
    /// <param name="c">Third triangle vertex.</param>
    /// <param name="uvA">First atlas UV.</param>
    /// <param name="uvB">Second atlas UV.</param>
    /// <param name="uvC">Third atlas UV.</param>
    /// <param name="sampler">Decoded source alpha and atlas mapping.</param>
    /// <returns>Opaque sample coverage encoded UNorm8.</returns>
    internal static byte TriangleCellOpaqueAlphaCoverage(
        Vec3f center,
        int scale,
        Vec3f a,
        Vec3f b,
        Vec3f c,
        TextureUv uvA,
        TextureUv uvB,
        TextureUv uvC,
        TextureAlphaSampler sampler)
    {
        float halfExtent = 0.48f / scale;
        ReadOnlySpan<float> offsets = [-halfExtent, 0.0f, halfExtent];
        int coveredSamples = 0;
        int opaqueSamples = 0;
        foreach (float offsetZ in offsets)
        {
            foreach (float offsetY in offsets)
            {
                foreach (float offsetX in offsets)
                {
                    Vec3f sample = new(
                        center.X + offsetX,
                        center.Y + offsetY,
                        center.Z + offsetZ);
                    if (!TryInterpolateUv(sample, a, b, c, uvA, uvB, uvC, out TextureUv uv))
                    {
                        continue;
                    }

                    coveredSamples++;
                    opaqueSamples += sampler.IsOpaque(uv.U, uv.V) ? 1 : 0;
                }
            }
        }

        return coveredSamples > 0
            ? checked((byte)Math.Clamp(
                (int)MathF.Round(255.0f * opaqueSamples / coveredSamples),
                0,
                255))
            : (byte)0;
    }

    /// <summary>Projects a point onto a non-degenerate triangle and interpolates barycentric UVs.</summary>
    /// <param name="point">Block-space sample point.</param>
    /// <param name="a">First triangle vertex.</param>
    /// <param name="b">Second triangle vertex.</param>
    /// <param name="c">Third triangle vertex.</param>
    /// <param name="uvA">First UV.</param>
    /// <param name="uvB">Second UV.</param>
    /// <param name="uvC">Third UV.</param>
    /// <param name="uv">Interpolated UV when the point lies within tolerance.</param>
    /// <returns>Whether interpolation is geometrically valid.</returns>
    private static bool TryInterpolateUv(
        Vec3f point,
        Vec3f a,
        Vec3f b,
        Vec3f c,
        TextureUv uvA,
        TextureUv uvB,
        TextureUv uvC,
        out TextureUv uv)
    {
        Vec3f edge0 = Subtract(b, a);
        Vec3f edge1 = Subtract(c, a);
        Vec3f normal = Cross(edge0, edge1);
        float normalLengthSquared = LengthSquared(normal);
        if (normalLengthSquared < 0.0000001f)
        {
            uv = default;
            return false;
        }

        float planeDistance = Dot(Subtract(point, a), normal) / normalLengthSquared;
        Vec3f projected = Subtract(point, Scale(normal, planeDistance));
        Vec3f relative = Subtract(projected, a);
        float d00 = Dot(edge0, edge0);
        float d01 = Dot(edge0, edge1);
        float d11 = Dot(edge1, edge1);
        float d20 = Dot(relative, edge0);
        float d21 = Dot(relative, edge1);
        float denominator = d00 * d11 - d01 * d01;

        float weightB = (d11 * d20 - d01 * d21) / denominator;
        float weightC = (d00 * d21 - d01 * d20) / denominator;
        float weightA = 1.0f - weightB - weightC;
        const float edgeTolerance = 0.015f;
        if (weightA < -edgeTolerance || weightB < -edgeTolerance || weightC < -edgeTolerance)
        {
            uv = default;
            return false;
        }

        uv = new TextureUv(
            uvA.U * weightA + uvB.U * weightB + uvC.U * weightC,
            uvA.V * weightA + uvB.V * weightB + uvC.V * weightC);
        return true;
    }

    /// <summary>Reads one UV pair defensively from the packed mesh array.</summary>
    /// <param name="uv">Packed UV array.</param>
    /// <param name="vertexIndex">Vertex index.</param>
    /// <returns>UV pair or zero when unavailable.</returns>
    private static TextureUv ReadUv(float[]? uv, int vertexIndex)
    {
        int offset = vertexIndex * 2;
        return uv is not null && offset + 1 < uv.Length
            ? new TextureUv(uv[offset], uv[offset + 1])
            : default;
    }

    /// <summary>Tests an atlas UV against a rectangle with a one-micro-unit edge tolerance.</summary>
    /// <param name="position">Atlas rectangle.</param>
    /// <param name="u">Atlas U.</param>
    /// <param name="v">Atlas V.</param>
    /// <returns>Whether the coordinate belongs to the rectangle.</returns>
    private static bool ContainsUv(TextureAtlasPosition position, float u, float v)
    {
        const float epsilon = 0.000001f;
        return u >= position.x1 - epsilon
            && u <= position.x2 + epsilon
            && v >= position.y1 - epsilon
            && v <= position.y2 + epsilon;
    }

    /// <summary>Normalizes slash, texture prefix, and PNG extension for alpha-cache identity.</summary>
    /// <param name="source">Possibly abbreviated asset location.</param>
    /// <returns>Canonical texture asset location.</returns>
    private static AssetLocation CanonicalTextureAsset(AssetLocation source)
    {
        string path = source.Path.Replace('\\', '/');
        if (!path.StartsWith("textures/", StringComparison.Ordinal))
        {
            path = $"textures/{path}";
        }
        if (!path.EndsWith(".png", StringComparison.Ordinal))
        {
            path += ".png";
        }

        return new AssetLocation(source.Domain, path);
    }

    /// <summary>
    /// Selects triangles whose authored texture alpha is part of their shadow geometry.
    /// </summary>
    /// <param name="block">Owning block and draw strategy.</param>
    /// <param name="renderPass">Per-face chunk render pass emitted by the tessellator.</param>
    /// <returns>
    /// <see langword="true"/> for the engine alpha-cutout pass and for ordinary crossed-plane
    /// blocks, even when their default mesh reports the broad opaque pass.
    /// </returns>
    private static bool ShouldTextureAlphaTest(Block block, EnumChunkRenderPass renderPass)
    {
        // OpaqueNoCull was the original texture-cutout contract and must remain
        // valid for lantern cages and other non-cross meshes. Vintage Story 1.22
        // emits common DrawType.Cross vegetation through Opaque instead, even
        // though the visible silhouette is still owned by texture alpha.
        // Entity-backed/chiseled geometry stays on its position-specific mesh
        // path unless it explicitly opted into OpaqueNoCull; a coincidental
        // Cross draw flag must never cut its authored instance shape.
        return renderPass == EnumChunkRenderPass.OpaqueNoCull
            || (block.DrawType == EnumDrawType.Cross
                && string.IsNullOrWhiteSpace(block.EntityClass));
    }

    /// <summary>Rejects blend, transparent, and liquid passes from opaque shadow occupancy.</summary>
    /// <param name="renderPass">Per-triangle Vintage Story chunk pass.</param>
    /// <returns>Whether the triangle may block traced light.</returns>
    internal static bool ShouldCastShadow(EnumChunkRenderPass renderPass)
    {
        return renderPass is not EnumChunkRenderPass.BlendNoCull
            and not EnumChunkRenderPass.Transparent
            and not EnumChunkRenderPass.Liquid;
    }

    /// <summary>Recognizes the exact 24-vertex/36-index six-face unit-cube topology.</summary>
    /// <param name="mesh">Candidate block mesh.</param>
    /// <returns>Whether full occupancy is safe without expensive triangle rasterization.</returns>
    internal static bool IsCanonicalUnitCubeMesh(MeshData? mesh)
    {
        if (mesh?.xyz is not { Length: >= 72 }
            || mesh.Indices is not { Length: >= 36 }
            || mesh.VerticesCount != 24
            || mesh.IndicesCount != 36)
        {
            return false;
        }

        int[] trianglesPerFace = new int[6];
        int[] firstTriangleCorners = new int[6];
        short[]? renderPasses = mesh.RenderPassesAndExtraBits;
        int indicesPerFace = mesh.IndicesPerFace > 0 ? mesh.IndicesPerFace : 6;
        for (int triangleOffset = 0; triangleOffset < 36; triangleOffset += 3)
        {
            int meshFaceIndex = triangleOffset / indicesPerFace;
            EnumChunkRenderPass renderPass = renderPasses is { Length: > 0 }
                && meshFaceIndex < renderPasses.Length
                    ? (EnumChunkRenderPass)(renderPasses[meshFaceIndex] & 0x03ff)
                    : EnumChunkRenderPass.Opaque;
            if (!ShouldCastShadow(renderPass)
                || renderPass == EnumChunkRenderPass.OpaqueNoCull)
            {
                // OpaqueNoCull is the alpha-tested vegetation/cage path. Unit
                // bounds and cube topology cannot prove that its texture covers
                // the face, so it must retain triangle/alpha rasterization.
                return false;
            }

            int indexA = mesh.Indices[triangleOffset];
            int indexB = mesh.Indices[triangleOffset + 1];
            int indexC = mesh.Indices[triangleOffset + 2];
            if ((uint)indexA >= 24 || (uint)indexB >= 24 || (uint)indexC >= 24)
            {
                return false;
            }

            Vec3f a = ReadVertex(mesh.xyz, indexA);
            Vec3f b = ReadVertex(mesh.xyz, indexB);
            Vec3f c = ReadVertex(mesh.xyz, indexC);
            int face = UnitCubeBoundaryFace(a, b, c);
            if (face < 0 || LengthSquared(Cross(Subtract(b, a), Subtract(c, a))) < 0.000001f)
            {
                return false;
            }

            int cornerA = UnitCubeFaceCorner(face, a);
            int cornerB = UnitCubeFaceCorner(face, b);
            int cornerC = UnitCubeFaceCorner(face, c);
            if (cornerA < 0 || cornerB < 0 || cornerC < 0)
            {
                return false;
            }
            int triangleCorners = (1 << cornerA) | (1 << cornerB) | (1 << cornerC);
            if (trianglesPerFace[face] == 0)
            {
                firstTriangleCorners[face] = triangleCorners;
            }
            else if (trianglesPerFace[face] == 1)
            {
                int sharedCorners = firstTriangleCorners[face] & triangleCorners;
                if (System.Numerics.BitOperations.PopCount((uint)sharedCorners) != 2)
                {
                    return false;
                }
                int firstSharedCorner = System.Numerics.BitOperations.TrailingZeroCount(
                    (uint)sharedCorners);
                int secondSharedCorner = System.Numerics.BitOperations.TrailingZeroCount(
                    (uint)(sharedCorners & ~(1 << firstSharedCorner)));
                if ((firstSharedCorner ^ secondSharedCorner) != 3)
                {
                    // The two triangles must share the face diagonal. Sharing
                    // a boundary edge leaves a hole while still presenting two
                    // non-degenerate triangles and unit bounds.
                    return false;
                }
            }
            trianglesPerFace[face]++;
        }

        return trianglesPerFace.All(count => count == 2);
    }

    /// <summary>Identifies which unit-cube boundary plane contains a triangle.</summary>
    /// <param name="a">First vertex.</param>
    /// <param name="b">Second vertex.</param>
    /// <param name="c">Third vertex.</param>
    /// <returns>Face index 0..5 or -1.</returns>
    private static int UnitCubeBoundaryFace(Vec3f a, Vec3f b, Vec3f c)
    {
        if (NearlyEqual(a.X, 0.0f) && NearlyEqual(b.X, 0.0f) && NearlyEqual(c.X, 0.0f)) return 0;
        if (NearlyEqual(a.X, 1.0f) && NearlyEqual(b.X, 1.0f) && NearlyEqual(c.X, 1.0f)) return 1;
        if (NearlyEqual(a.Y, 0.0f) && NearlyEqual(b.Y, 0.0f) && NearlyEqual(c.Y, 0.0f)) return 2;
        if (NearlyEqual(a.Y, 1.0f) && NearlyEqual(b.Y, 1.0f) && NearlyEqual(c.Y, 1.0f)) return 3;
        if (NearlyEqual(a.Z, 0.0f) && NearlyEqual(b.Z, 0.0f) && NearlyEqual(c.Z, 0.0f)) return 4;
        if (NearlyEqual(a.Z, 1.0f) && NearlyEqual(b.Z, 1.0f) && NearlyEqual(c.Z, 1.0f)) return 5;
        return -1;
    }

    /// <summary>Maps a boundary vertex to one of four canonical corners in face-local coordinates.</summary>
    /// <param name="face">Boundary face index.</param>
    /// <param name="vertex">Unit-cube vertex.</param>
    /// <returns>Corner index 0..3 or -1.</returns>
    private static int UnitCubeFaceCorner(int face, Vec3f vertex)
    {
        float u;
        float v;
        if (face is 0 or 1)
        {
            u = vertex.Y;
            v = vertex.Z;
        }
        else if (face is 2 or 3)
        {
            u = vertex.X;
            v = vertex.Z;
        }
        else
        {
            u = vertex.X;
            v = vertex.Y;
        }

        int uBit = NearlyEqual(u, 0.0f) ? 0 : NearlyEqual(u, 1.0f) ? 1 : -1;
        int vBit = NearlyEqual(v, 0.0f) ? 0 : NearlyEqual(v, 1.0f) ? 1 : -1;
        return uBit < 0 || vBit < 0 ? -1 : uBit | (vBit << 1);
    }

    /// <summary>Tests triangle intersection against one default quarter-block occupancy cell.</summary>
    /// <param name="center">Cell center.</param>
    /// <param name="a">First vertex.</param>
    /// <param name="b">Second vertex.</param>
    /// <param name="c">Third vertex.</param>
    /// <returns>Whether triangle and cell overlap.</returns>
    internal static bool TriangleIntersectsVoxel(Vec3f center, Vec3f a, Vec3f b, Vec3f c)
    {
        return TriangleIntersectsVoxel(center, a, b, c, OccupancyScale);
    }

    /// <summary>Rasterizes shadow-eligible mesh triangles into the 64-bit quarter-block mask.</summary>
    /// <param name="mesh">Block-local mesh.</param>
    /// <returns>One bit per 4-cubed occupancy cell.</returns>
    internal static ulong RasterizeOpaqueMeshMask(MeshData mesh)
    {
        if (mesh.xyz is not { Length: >= 9 } || mesh.Indices is not { Length: >= 3 })
        {
            return 0;
        }

        int vertexCount = Math.Min(mesh.VerticesCount, mesh.xyz.Length / 3);
        int indexCount = Math.Min(mesh.IndicesCount, mesh.Indices.Length);
        short[]? renderPasses = mesh.RenderPassesAndExtraBits;
        int indicesPerFace = mesh.IndicesPerFace > 0 ? mesh.IndicesPerFace : 6;
        ulong mask = 0;
        for (int triangleOffset = 0; triangleOffset + 2 < indexCount; triangleOffset += 3)
        {
            int faceIndex = triangleOffset / indicesPerFace;
            EnumChunkRenderPass renderPass = renderPasses is { Length: > 0 }
                && faceIndex < renderPasses.Length
                    ? (EnumChunkRenderPass)(renderPasses[faceIndex] & 0x03ff)
                    : EnumChunkRenderPass.Opaque;
            if (!ShouldCastShadow(renderPass))
            {
                continue;
            }

            int indexA = mesh.Indices[triangleOffset];
            int indexB = mesh.Indices[triangleOffset + 1];
            int indexC = mesh.Indices[triangleOffset + 2];
            if ((uint)indexA >= vertexCount || (uint)indexB >= vertexCount || (uint)indexC >= vertexCount)
            {
                continue;
            }

            Vec3f a = ReadVertex(mesh.xyz, indexA);
            Vec3f b = ReadVertex(mesh.xyz, indexB);
            Vec3f c = ReadVertex(mesh.xyz, indexC);
            for (int subZ = 0; subZ < OccupancyScale; subZ++)
            {
                for (int subY = 0; subY < OccupancyScale; subY++)
                {
                    for (int subX = 0; subX < OccupancyScale; subX++)
                    {
                        int bitIndex = (subZ * OccupancyScale + subY) * OccupancyScale + subX;
                        Vec3f center = new(
                            (subX + 0.5f) / OccupancyScale,
                            (subY + 0.5f) / OccupancyScale,
                            (subZ + 0.5f) / OccupancyScale);
                        if (TriangleIntersectsVoxel(center, a, b, c))
                        {
                            mask |= 1UL << bitIndex;
                        }
                    }
                }
            }
        }
        return mask;
    }

    /// <summary>Separating-axis triangle/AABB test at an explicit subvoxel resolution.</summary>
    /// <param name="center">Cell center.</param>
    /// <param name="a">First vertex.</param>
    /// <param name="b">Second vertex.</param>
    /// <param name="c">Third vertex.</param>
    /// <param name="scale">Cells per block edge.</param>
    /// <returns>Whether triangle and centered cell overlap.</returns>
    internal static bool TriangleIntersectsVoxel(
        Vec3f center,
        Vec3f a,
        Vec3f b,
        Vec3f c,
        int scale)
    {
        Vec3f v0 = Subtract(a, center);
        Vec3f v1 = Subtract(b, center);
        Vec3f v2 = Subtract(c, center);
        Vec3f edge0 = Subtract(v1, v0);
        Vec3f edge1 = Subtract(v2, v1);
        Vec3f edge2 = Subtract(v0, v2);
        Vec3f triangleNormal = Cross(edge0, edge1);
        int normalAxes = (Math.Abs(triangleNormal.X) > 0.00001f ? 1 : 0)
            + (Math.Abs(triangleNormal.Y) > 0.00001f ? 1 : 0)
            + (Math.Abs(triangleNormal.Z) > 0.00001f ? 1 : 0);
        // Non-axis-aligned crossed planes often pass exactly through fine-grid
        // corners. A tiny open-cell inset prevents zero-area corner contact from
        // marking all neighboring cells, while axis-aligned boundary faces keep
        // the exact half extent and cannot disappear at block edges.
        float halfExtent = (normalAxes <= 1 ? 0.5f : 0.49f) / scale;

        if (!OverlapsVoxelAxis(new Vec3f(1, 0, 0), v0, v1, v2, halfExtent)
            || !OverlapsVoxelAxis(new Vec3f(0, 1, 0), v0, v1, v2, halfExtent)
            || !OverlapsVoxelAxis(new Vec3f(0, 0, 1), v0, v1, v2, halfExtent)
            || !OverlapsVoxelAxis(triangleNormal, v0, v1, v2, halfExtent))
        {
            return false;
        }

        Vec3f[] edges = [edge0, edge1, edge2];
        foreach (Vec3f edge in edges)
        {
            if (!OverlapsVoxelAxis(new Vec3f(0, edge.Z, -edge.Y), v0, v1, v2, halfExtent)
                || !OverlapsVoxelAxis(new Vec3f(-edge.Z, 0, edge.X), v0, v1, v2, halfExtent)
                || !OverlapsVoxelAxis(new Vec3f(edge.Y, -edge.X, 0), v0, v1, v2, halfExtent))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Projects triangle vertices and the cubic half-extent onto one SAT axis.</summary>
    /// <param name="axis">Candidate separating axis.</param>
    /// <param name="v0">Triangle vertex relative to cell center.</param>
    /// <param name="v1">Triangle vertex relative to cell center.</param>
    /// <param name="v2">Triangle vertex relative to cell center.</param>
    /// <param name="halfExtent">Cubic cell half-size.</param>
    /// <returns>Whether projection intervals overlap.</returns>
    private static bool OverlapsVoxelAxis(
        Vec3f axis,
        Vec3f v0,
        Vec3f v1,
        Vec3f v2,
        float halfExtent)
    {
        if (LengthSquared(axis) < 0.0000001f)
        {
            return true;
        }

        float p0 = Dot(v0, axis);
        float p1 = Dot(v1, axis);
        float p2 = Dot(v2, axis);
        float radius = halfExtent * (Math.Abs(axis.X) + Math.Abs(axis.Y) + Math.Abs(axis.Z));
        return Math.Min(p0, Math.Min(p1, p2)) <= radius
            && Math.Max(p0, Math.Max(p1, p2)) >= -radius;
    }

    /// <summary>Computes a three-dimensional cross product without allocating.</summary>
    /// <param name="left">Left vector.</param>
    /// <param name="right">Right vector.</param>
    /// <returns>Perpendicular vector.</returns>
    private static Vec3f Cross(Vec3f left, Vec3f right)
    {
        return new Vec3f(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
    }

    /// <summary>Rasterizes collision/selection AABBs into the 64-bit quarter-block mask.</summary>
    /// <param name="boxes">Block-local cuboids.</param>
    /// <returns>Union occupancy mask.</returns>
    private static ulong BuildBoxMask(Cuboidf[]? boxes)
    {
        if (boxes is not { Length: > 0 })
        {
            return 0;
        }

        ulong mask = 0;
        for (int subZ = 0; subZ < OccupancyScale; subZ++)
        {
            float sampleZ = (subZ + 0.5f) / OccupancyScale;
            for (int subY = 0; subY < OccupancyScale; subY++)
            {
                float sampleY = (subY + 0.5f) / OccupancyScale;
                for (int subX = 0; subX < OccupancyScale; subX++)
                {
                    float sampleX = (subX + 0.5f) / OccupancyScale;
                    bool occupied = false;
                    for (int boxIndex = 0; boxIndex < boxes.Length; boxIndex++)
                    {
                        Cuboidf box = boxes[boxIndex];
                        if (sampleX >= box.X1 && sampleX <= box.X2
                            && sampleY >= box.Y1 && sampleY <= box.Y2
                            && sampleZ >= box.Z1 && sampleZ <= box.Z2)
                        {
                            occupied = true;
                            break;
                        }
                    }

                    if (!occupied)
                    {
                        continue;
                    }

                    int bitIndex = (subZ * OccupancyScale + subY) * OccupancyScale + subX;
                    mask |= 1UL << bitIndex;
                }
            }
        }

        return mask;
    }

    /// <summary>Selects partial collision/selection geometry, falling back to full only for absorbing blocks.</summary>
    /// <param name="collisionBoxes">Instance collision boxes.</param>
    /// <param name="selectionBoxes">Instance selection boxes.</param>
    /// <param name="absorbsLight">Whether empty geometry still requires conservative opacity.</param>
    /// <returns>Quarter-block occupancy mask.</returns>
    internal static ulong BuildInstanceOccupancyMask(
        Cuboidf[]? collisionBoxes,
        Cuboidf[]? selectionBoxes,
        bool absorbsLight)
    {
        ulong mask = SelectInstanceOccupancyMask(
            BuildBoxMask(collisionBoxes),
            BuildBoxMask(selectionBoxes));
        return mask == 0 && absorbsLight ? ulong.MaxValue : mask;
    }

    /// <summary>Combines reusable default mesh and position-dependent boxes under replace/append semantics.</summary>
    /// <param name="defaultMeshMask">Type-static mesh occupancy.</param>
    /// <param name="defaultMeshWasDetailed">Whether zero means proven transparent/empty geometry.</param>
    /// <param name="replacesDefaultMesh">Whether instance tessellation removes default geometry.</param>
    /// <param name="collisionMask">Position-dependent collision occupancy.</param>
    /// <param name="selectionMask">Position-dependent selection occupancy.</param>
    /// <param name="absorbsLight">Whether final empty geometry needs conservative full occupancy.</param>
    /// <returns>Resolved per-instance quarter-block mask.</returns>
    internal static ulong ResolveDynamicInstanceOccupancy(
        ulong defaultMeshMask,
        bool defaultMeshWasDetailed,
        bool replacesDefaultMesh,
        ulong collisionMask,
        ulong selectionMask,
        bool absorbsLight)
    {
        ulong instanceMask = SelectInstanceOccupancyMask(collisionMask, selectionMask);
        if (replacesDefaultMesh)
        {
            return instanceMask != 0
                ? instanceMask
                : absorbsLight
                    ? ulong.MaxValue
                    : 0;
        }

        if (defaultMeshMask != 0)
        {
            // Full collision/selection cubes are commonly only interaction
            // bounds for lanterns and anvils. Do not let them erase the proven
            // default tessellation; add only genuinely detailed instance boxes.
            return instanceMask is not 0 and not ulong.MaxValue
                ? defaultMeshMask | instanceMask
                : defaultMeshMask;
        }

        if (instanceMask != 0)
        {
            return instanceMask;
        }

        // A detailed alpha-tested default mesh with zero opaque texels is
        // intentionally empty, not an absorbing cube fallback.
        return absorbsLight && !defaultMeshWasDetailed ? ulong.MaxValue : 0;
    }

    /// <summary>Resolves authoritative captured instance meshes, including valid all-transparent zero masks.</summary>
    /// <param name="hasInstanceTessellation">Whether block entity tessellation executed.</param>
    /// <param name="capturedMeshCount">Meshes emitted by the instance.</param>
    /// <param name="skipsDefaultMesh">Engine signal that default mesh must be omitted.</param>
    /// <param name="replacesDefaultMesh">Known chisel/microblock replacement identity.</param>
    /// <param name="defaultMeshMask">Reusable default occupancy.</param>
    /// <param name="instanceMeshMask">Captured instance occupancy.</param>
    /// <param name="resolvedMask">Authoritative combined/replaced mask.</param>
    /// <returns>Whether captured tessellation provides an authoritative result.</returns>
    internal static bool TryResolveTessellatedInstanceOccupancy(
        bool hasInstanceTessellation,
        int capturedMeshCount,
        bool skipsDefaultMesh,
        bool replacesDefaultMesh,
        ulong defaultMeshMask,
        ulong instanceMeshMask,
        out ulong resolvedMask)
    {
        bool capturedAuthoritativeInstance = hasInstanceTessellation
            && replacesDefaultMesh
            && (capturedMeshCount > 0 || skipsDefaultMesh);
        if (capturedAuthoritativeInstance)
        {
            resolvedMask = instanceMeshMask;
            return true;
        }

        if (hasInstanceTessellation
            && capturedMeshCount > 0
            && instanceMeshMask != 0)
        {
            resolvedMask = defaultMeshMask | instanceMeshMask;
            return true;
        }

        resolvedMask = 0;
        return false;
    }

    /// <summary>Determines replacement semantics from a loaded block's entity/code identity.</summary>
    /// <param name="block">Entity-backed block.</param>
    /// <returns>Whether default geometry must not be unioned.</returns>
    internal static bool InstanceGeometryReplacesDefaultMesh(Block block)
    {
        return InstanceGeometryReplacesDefaultMesh(
            block.EntityClass,
            block.Code?.ToString());
    }

    /// <summary>Recognizes chisel and microblock identities case-insensitively.</summary>
    /// <param name="entityClass">Block entity class name.</param>
    /// <param name="blockCode">Block asset code.</param>
    /// <returns>Whether instance geometry replaces rather than augments the default mesh.</returns>
    internal static bool InstanceGeometryReplacesDefaultMesh(
        string? entityClass,
        string? blockCode)
    {
        string identity = $"{entityClass} {blockCode}";
        return identity.Contains("chisel", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("microblock", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("micro-block", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the public serialized owner coordinates used by multiblock proxy entities.</summary>
    /// <param name="blockEntityIdentity">Runtime block-entity type name.</param>
    /// <param name="attributes">Public state written by <see cref="BlockEntity.ToTreeAttributes"/>.</param>
    /// <param name="dimension">Dimension inherited from the proxy position.</param>
    /// <param name="principalPosition">Resolved geometry-owner position.</param>
    /// <returns>Whether this is a valid multiblock proxy with a principal position.</returns>
    internal static bool TryResolveMultiblockPrincipal(
        string? blockEntityIdentity,
        ITreeAttribute attributes,
        int dimension,
        out BlockPos principalPosition)
    {
        principalPosition = new BlockPos(dimension);
        if (string.IsNullOrWhiteSpace(blockEntityIdentity)
            || !blockEntityIdentity.Contains("multiblock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int? x = attributes.TryGetInt("cx");
        int? y = attributes.TryGetInt("cy");
        int? z = attributes.TryGetInt("cz");
        if (!x.HasValue || !y.HasValue || !z.HasValue
            || (x.Value == -1 && y.Value == -1 && z.Value == -1))
        {
            return false;
        }

        principalPosition = new BlockPos(x.Value, y.Value, z.Value, dimension);
        return true;
    }

    /// <summary>Captures a multiblock principal's authored mesh for one proxy-owned world cell.</summary>
    /// <param name="proxyEntity">Entity carrying public principal coordinates.</param>
    /// <param name="proxyPosition">Cell whose occupancy is being built.</param>
    /// <param name="principalPosition">Resolved geometry-owner position.</param>
    /// <param name="collector">Meshes emitted by the principal entity.</param>
    /// <param name="skipsDefaultMesh">Principal replacement semantics.</param>
    /// <returns>Whether a distinct principal emitted authored geometry.</returns>
    private bool TryGetMultiblockPrincipalMeshes(
        BlockEntity proxyEntity,
        BlockPos proxyPosition,
        out BlockPos principalPosition,
        out InstanceTerrainMeshCollector collector,
        out bool skipsDefaultMesh)
    {
        principalPosition = new BlockPos(proxyPosition.dimension);
        collector = new InstanceTerrainMeshCollector();
        skipsDefaultMesh = false;

        TreeAttribute attributes = new();
        proxyEntity.ToTreeAttributes(attributes);
        if (!TryResolveMultiblockPrincipal(
                proxyEntity.GetType().FullName,
                attributes,
                proxyPosition.dimension,
                out principalPosition)
            || principalPosition.Equals(proxyPosition))
        {
            return false;
        }

        BlockEntity? principalEntity = api.World.BlockAccessor.GetBlockEntity(principalPosition);
        if (principalEntity is null || ReferenceEquals(principalEntity, proxyEntity))
        {
            return false;
        }

        skipsDefaultMesh = principalEntity.OnTesselation(collector, api.Tesselator);
        return collector.Meshes.Count > 0;
    }

    /// <summary>Runs block-entity tessellation into a non-reusing collector and rasterizes captured meshes.</summary>
    /// <param name="block">Dynamic block type.</param>
    /// <param name="position">Exact world instance position.</param>
    /// <param name="occupancy">Mask and diagnostics on success.</param>
    /// <returns>Whether an instance block entity was available and tessellated.</returns>
    private bool TryGetInstanceMeshOccupancy(
        Block block,
        BlockPos position,
        out InstanceMeshOccupancy occupancy)
    {
        BlockEntity? blockEntity = api.World.BlockAccessor.GetBlockEntity(position);
        if (blockEntity is null)
        {
            occupancy = default;
            return false;
        }

        (int X, int Y, int Z, int Dimension) key =
            (position.X, position.Y, position.Z, position.dimension);
        if (instanceMeshOccupancyByPosition.TryGetValue(key, out occupancy)
            && occupancy.BlockId == block.Id
            && ReferenceEquals(occupancy.BlockEntity, blockEntity))
        {
            return true;
        }

        try
        {
            InstanceTerrainMeshCollector collector = new();
            bool skipsDefaultMesh = blockEntity.OnTesselation(collector, api.Tesselator);
            IReadOnlyList<MeshData> capturedMeshes = collector.Meshes;
            BlockPos meshOwnerPosition = position;
            bool projectedFromPrincipal = false;
            if (capturedMeshes.Count == 0
                && TryGetMultiblockPrincipalMeshes(
                    blockEntity,
                    position,
                    out BlockPos principalPosition,
                    out InstanceTerrainMeshCollector principalCollector,
                    out bool principalSkipsDefaultMesh))
            {
                capturedMeshes = principalCollector.Meshes;
                meshOwnerPosition = principalPosition;
                skipsDefaultMesh = principalSkipsDefaultMesh;
                projectedFromPrincipal = true;
            }

            ulong meshMask = projectedFromPrincipal
                ? RasterizeInstanceMeshesAtRelativeBlock(
                    capturedMeshes,
                    meshOwnerPosition.X,
                    meshOwnerPosition.Y,
                    meshOwnerPosition.Z,
                    position.X,
                    position.Y,
                    position.Z)
                : RasterizeInstanceMeshes(
                    capturedMeshes,
                    position.X,
                    position.Y,
                    position.Z);
            occupancy = new InstanceMeshOccupancy(
                blockEntity,
                block.Id,
                meshMask,
                skipsDefaultMesh,
                capturedMeshes.Count);
            if (instanceMeshOccupancyByPosition.Count >= 2048)
            {
                // Position/revision identity makes entries safe, but a long
                // exploration session should not retain unloaded block entities.
                instanceMeshOccupancyByPosition.Clear();
            }
            instanceMeshOccupancyByPosition[key] = occupancy;

            if (!string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID"))
                && (InstanceGeometryReplacesDefaultMesh(block)
                    || capturedMeshes.Count > 0))
            {
                api.Logger.Notification(
                    "[VintageRTX.Test] Instance mesh {0} at ({1},{2},{3}): meshes={4}, skipDefault={5}, occupied={6}/64.",
                    block.Code?.ToString() ?? block.Id.ToString(),
                    position.X,
                    position.Y,
                    position.Z,
                    capturedMeshes.Count,
                    skipsDefaultMesh,
                    System.Numerics.BitOperations.PopCount(meshMask));
                if (projectedFromPrincipal)
                {
                    api.Logger.Notification(
                        "[VintageRTX.Test] Multiblock proxy mesh {0} at ({1},{2},{3}): principal=({4},{5},{6}), occupied={7}/64.",
                        block.Code?.ToString() ?? block.Id.ToString(),
                        position.X,
                        position.Y,
                        position.Z,
                        meshOwnerPosition.X,
                        meshOwnerPosition.Y,
                        meshOwnerPosition.Z,
                        System.Numerics.BitOperations.PopCount(meshMask));
                }
                else if (meshMask == 0 && capturedMeshes.Count > 0)
                {
                    InstanceMeshDiagnostics diagnostics = InspectInstanceMeshes(
                        capturedMeshes,
                        position.X,
                        position.Y,
                        position.Z,
                        meshMask);
                    api.Logger.Notification(
                        "[VintageRTX.Test] Instance mesh zero {0} at ({1},{2},{3}): vertices={4}, indices={5}, opaqueTriangles={6}, transparentTriangles={7}, invalidTriangles={8}, rawBounds={9}, localBounds={10}, reason={11}.",
                        block.Code?.ToString() ?? block.Id.ToString(),
                        position.X,
                        position.Y,
                        position.Z,
                        diagnostics.VertexCount,
                        diagnostics.IndexCount,
                        diagnostics.OpaqueTriangles,
                        diagnostics.TransparentTriangles,
                        diagnostics.InvalidTriangles,
                        diagnostics.RawBounds,
                        diagnostics.LocalBounds,
                        diagnostics.Reason);
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            instanceMeshOccupancyByPosition.Remove(key);
            api.Logger.Debug(
                "[VintageRTX] Instance tessellation failed for {0} at ({1},{2},{3}): {4}",
                block.Code?.ToString() ?? block.Id.ToString(),
                position.X,
                position.Y,
                position.Z,
                exception.Message);
            occupancy = default;
            return false;
        }
    }

    /// <summary>Normalizes each captured mesh to block-local coordinates and unions opaque masks.</summary>
    /// <param name="meshes">Captured instance meshes.</param>
    /// <param name="worldX">Instance world X.</param>
    /// <param name="worldY">Instance world Y.</param>
    /// <param name="worldZ">Instance world Z.</param>
    /// <returns>Quarter-block occupancy union.</returns>
    internal static ulong RasterizeInstanceMeshes(
        IReadOnlyList<MeshData> meshes,
        int worldX,
        int worldY,
        int worldZ)
    {
        ulong mask = 0;
        foreach (MeshData source in meshes)
        {
            MeshData mesh = NormalizeInstanceMeshToBlock(source, worldX, worldY, worldZ);
            mask |= RasterizeOpaqueMeshMask(mesh);
        }
        return mask;
    }

    /// <summary>Projects owner-local multiblock meshes into a distinct target block cell.</summary>
    /// <param name="meshes">Meshes emitted together by the geometry owner.</param>
    /// <param name="ownerWorldX">Geometry-owner world X.</param>
    /// <param name="ownerWorldY">Geometry-owner world Y.</param>
    /// <param name="ownerWorldZ">Geometry-owner world Z.</param>
    /// <param name="targetWorldX">Target proxy world X.</param>
    /// <param name="targetWorldY">Target proxy world Y.</param>
    /// <param name="targetWorldZ">Target proxy world Z.</param>
    /// <returns>Only the authored geometry intersecting the target cell.</returns>
    internal static ulong RasterizeInstanceMeshesAtRelativeBlock(
        IReadOnlyList<MeshData> meshes,
        int ownerWorldX,
        int ownerWorldY,
        int ownerWorldZ,
        int targetWorldX,
        int targetWorldY,
        int targetWorldZ)
    {
        MeshBounds bounds = MeshBounds.Empty;
        foreach (MeshData mesh in meshes)
        {
            bounds = bounds.Include(mesh);
        }
        if (bounds.IsEmpty)
        {
            return 0;
        }

        float sourceOriginX = InstanceMeshOwnerAxisOffset(
            bounds.MinimumX, bounds.MaximumX, ownerWorldX);
        float sourceOriginY = InstanceMeshOwnerAxisOffset(
            bounds.MinimumY, bounds.MaximumY, ownerWorldY);
        float sourceOriginZ = InstanceMeshOwnerAxisOffset(
            bounds.MinimumZ, bounds.MaximumZ, ownerWorldZ);
        float offsetX = sourceOriginX + targetWorldX - ownerWorldX;
        float offsetY = sourceOriginY + targetWorldY - ownerWorldY;
        float offsetZ = sourceOriginZ + targetWorldZ - ownerWorldZ;

        ulong mask = 0;
        foreach (MeshData source in meshes)
        {
            MeshData projected = CloneInstanceMesh(source);
            int vertexCount = Math.Min(
                projected.VerticesCount,
                projected.xyz?.Length / 3 ?? 0);
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                int vertexOffset = vertexIndex * 3;
                projected.xyz![vertexOffset] -= offsetX;
                projected.xyz[vertexOffset + 1] -= offsetY;
                projected.xyz[vertexOffset + 2] -= offsetZ;
            }
            mask |= RasterizeOpaqueMeshMask(projected);
        }
        return mask;
    }

    /// <summary>Infers the shared local/world/chunk origin of a possibly multi-cell mesh collection.</summary>
    /// <param name="minimum">Lowest captured coordinate on one axis.</param>
    /// <param name="maximum">Highest captured coordinate on one axis.</param>
    /// <param name="ownerWorldCoordinate">Principal world coordinate on the same axis.</param>
    /// <returns>Coordinate-space origin to remove before applying the proxy-cell offset.</returns>
    private static float InstanceMeshOwnerAxisOffset(
        float minimum,
        float maximum,
        int ownerWorldCoordinate)
    {
        const float tolerance = 0.02f;
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum))
        {
            return 0.0f;
        }

        // Rotated authored elements may protrude slightly before the owner
        // origin. That negative coordinate unambiguously identifies local
        // shape space (world/chunk tessellation cannot begin before its own
        // origin for this principal-owned collection).
        if (minimum >= -2.0f - tolerance && minimum < -tolerance)
        {
            return 0.0f;
        }

        // Multiblock meshes may begin in the principal cell and extend into
        // later cells, so the lower bound (rather than total extent) proves
        // their coordinate space.
        if (minimum >= ownerWorldCoordinate - tolerance
            && minimum <= ownerWorldCoordinate + 1.0f + tolerance)
        {
            return ownerWorldCoordinate;
        }

        int chunkCoordinate = ownerWorldCoordinate % GlobalConstants.ChunkSize;
        if (chunkCoordinate < 0)
        {
            chunkCoordinate += GlobalConstants.ChunkSize;
        }
        if (minimum >= chunkCoordinate - tolerance
            && minimum <= chunkCoordinate + 1.0f + tolerance)
        {
            return chunkCoordinate;
        }

        if (minimum >= -tolerance && minimum <= 1.0f + tolerance)
        {
            return 0.0f;
        }
        return InstanceMeshAxisOffset(minimum, maximum, ownerWorldCoordinate);
    }

    /// <summary>Measures vertex/index counts, coordinate bounds, normalization mode, and rasterized coverage.</summary>
    /// <param name="meshes">Captured instance meshes.</param>
    /// <param name="worldX">Instance world X.</param>
    /// <param name="worldY">Instance world Y.</param>
    /// <param name="worldZ">Instance world Z.</param>
    /// <param name="rasterizedMask">Final quarter-block mask.</param>
    /// <returns>Developer diagnostics for unexpected modded geometry spaces.</returns>
    internal static InstanceMeshDiagnostics InspectInstanceMeshes(
        IReadOnlyList<MeshData> meshes,
        int worldX,
        int worldY,
        int worldZ,
        ulong rasterizedMask)
    {
        int vertexCount = 0;
        int indexCount = 0;
        int opaqueTriangles = 0;
        int transparentTriangles = 0;
        int invalidTriangles = 0;
        MeshBounds rawBounds = MeshBounds.Empty;
        MeshBounds localBounds = MeshBounds.Empty;
        foreach (MeshData source in meshes)
        {
            rawBounds = rawBounds.Include(source);
            MeshData mesh = NormalizeInstanceMeshToBlock(source, worldX, worldY, worldZ);
            localBounds = localBounds.Include(mesh);
            // NormalizeInstanceMeshToBlock always clones through MeshData.Clone(),
            // whose contract materializes omitted vertex/index buffers as empty arrays.
            int meshVertexCount = Math.Min(mesh.VerticesCount, mesh.xyz!.Length / 3);
            int[] indices = mesh.Indices!;
            int meshIndexCount = Math.Min(mesh.IndicesCount, indices.Length);
            vertexCount += meshVertexCount;
            indexCount += meshIndexCount;
            short[]? renderPasses = mesh.RenderPassesAndExtraBits;
            int indicesPerFace = mesh.IndicesPerFace > 0 ? mesh.IndicesPerFace : 6;
            for (int triangleOffset = 0;
                 triangleOffset + 2 < meshIndexCount;
                 triangleOffset += 3)
            {
                int faceIndex = triangleOffset / indicesPerFace;
                EnumChunkRenderPass renderPass = renderPasses is { Length: > 0 }
                    && faceIndex < renderPasses.Length
                        ? (EnumChunkRenderPass)(renderPasses[faceIndex] & 0x03ff)
                        : EnumChunkRenderPass.Opaque;
                if (!ShouldCastShadow(renderPass))
                {
                    transparentTriangles++;
                    continue;
                }

                int indexA = indices[triangleOffset];
                int indexB = indices[triangleOffset + 1];
                int indexC = indices[triangleOffset + 2];
                if ((uint)indexA >= meshVertexCount
                    || (uint)indexB >= meshVertexCount
                    || (uint)indexC >= meshVertexCount)
                {
                    invalidTriangles++;
                    continue;
                }
                opaqueTriangles++;
            }
        }

        string reason = rasterizedMask != 0
            ? "rasterized"
            : vertexCount == 0 || indexCount < 3
                ? "empty-buffers"
                : opaqueTriangles == 0 && transparentTriangles > 0
                    ? "transparent-only"
                    : opaqueTriangles == 0 && invalidTriangles > 0
                        ? "invalid-indices"
                        : !localBounds.OverlapsUnitBlock
                            ? "outside-local-block"
                            : "opaque-triangles-missed-grid";
        return new InstanceMeshDiagnostics(
            vertexCount,
            indexCount,
            opaqueTriangles,
            transparentTriangles,
            invalidTriangles,
            rawBounds.ToString(),
            localBounds.ToString(),
            reason);
    }

    /// <summary>Clones a captured mesh and removes world/chunk offsets only when bounds prove that space.</summary>
    /// <param name="source">Captured mesh; never mutated.</param>
    /// <param name="worldX">Instance world X.</param>
    /// <param name="worldY">Instance world Y.</param>
    /// <param name="worldZ">Instance world Z.</param>
    /// <returns>Independent block-local mesh.</returns>
    internal static MeshData NormalizeInstanceMeshToBlock(
        MeshData source,
        int worldX,
        int worldY,
        int worldZ)
    {
        MeshData mesh = CloneInstanceMesh(source);
        if (mesh.xyz is not { Length: >= 3 } || mesh.VerticesCount <= 0)
        {
            return mesh;
        }

        int vertexCount = Math.Min(mesh.VerticesCount, mesh.xyz.Length / 3);
        float minimumX = float.PositiveInfinity;
        float minimumY = float.PositiveInfinity;
        float minimumZ = float.PositiveInfinity;
        float maximumX = float.NegativeInfinity;
        float maximumY = float.NegativeInfinity;
        float maximumZ = float.NegativeInfinity;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            int offset = vertexIndex * 3;
            minimumX = Math.Min(minimumX, mesh.xyz[offset]);
            minimumY = Math.Min(minimumY, mesh.xyz[offset + 1]);
            minimumZ = Math.Min(minimumZ, mesh.xyz[offset + 2]);
            maximumX = Math.Max(maximumX, mesh.xyz[offset]);
            maximumY = Math.Max(maximumY, mesh.xyz[offset + 1]);
            maximumZ = Math.Max(maximumZ, mesh.xyz[offset + 2]);
        }

        float offsetX = InstanceMeshAxisOffset(minimumX, maximumX, worldX);
        float offsetY = InstanceMeshAxisOffset(minimumY, maximumY, worldY);
        float offsetZ = InstanceMeshAxisOffset(minimumZ, maximumZ, worldZ);
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            int offset = vertexIndex * 3;
            mesh.xyz[offset] -= offsetX;
            mesh.xyz[offset + 1] -= offsetY;
            mesh.xyz[offset + 2] -= offsetZ;
        }
        return mesh;
    }

    /// <summary>Clones all geometry and preserves optional render-pass arrays omitted by some engine clones.</summary>
    /// <param name="source">Mesh returned by block-entity tessellation.</param>
    /// <returns>Safe retained copy; the engine-owned source is not reused.</returns>
    internal static MeshData CloneInstanceMesh(MeshData source)
    {
        MeshData clone = source.Clone();
        if (source.RenderPassesAndExtraBits is { Length: > 0 } renderPasses)
        {
            // MeshData.Clone() sizes optional arrays from their logical count.
            // Several BlockEntity tessellators only fill the public array, so
            // preserve it explicitly or transparent glass can silently become
            // an opaque instance caster after capture/normalisation.
            clone.RenderPassesAndExtraBits = (short[])renderPasses.Clone();
            clone.RenderPassCount = source.RenderPassCount > 0
                ? source.RenderPassCount
                : renderPasses.Length;
        }
        clone.IndicesPerFace = source.IndicesPerFace;
        return clone;
    }

    /// <summary>Infers block-local, world, or 32-block chunk coordinate offset from one axis bounds.</summary>
    /// <param name="minimum">Observed axis minimum.</param>
    /// <param name="maximum">Observed axis maximum.</param>
    /// <param name="worldCoordinate">Owning block world coordinate.</param>
    /// <returns>Coordinate offset to subtract.</returns>
    private static float InstanceMeshAxisOffset(float minimum, float maximum, int worldCoordinate)
    {
        const float tolerance = 0.02f;
        if (minimum >= -tolerance && maximum <= 1.0f + tolerance)
        {
            return 0.0f;
        }
        if (minimum >= worldCoordinate - tolerance
            && maximum <= worldCoordinate + 1.0f + tolerance)
        {
            return worldCoordinate;
        }

        int chunkCoordinate = worldCoordinate % GlobalConstants.ChunkSize;
        if (chunkCoordinate < 0)
        {
            chunkCoordinate += GlobalConstants.ChunkSize;
        }
        if (minimum >= chunkCoordinate - tolerance
            && maximum <= chunkCoordinate + 1.0f + tolerance)
        {
            return chunkCoordinate;
        }

        return MathF.Floor((minimum + maximum) * 0.5f);
    }

    /// <summary>Prefers detailed partial collision then selection, retaining full masks only as final evidence.</summary>
    /// <param name="collisionMask">Collision-box occupancy.</param>
    /// <param name="selectionMask">Selection-box occupancy.</param>
    /// <returns>Best available mask or zero.</returns>
    private static ulong SelectInstanceOccupancyMask(ulong collisionMask, ulong selectionMask)
    {
        if (collisionMask is not 0 and not ulong.MaxValue)
        {
            return collisionMask;
        }
        if (selectionMask is not 0 and not ulong.MaxValue)
        {
            return selectionMask;
        }
        return collisionMask != 0 ? collisionMask : selectionMask;
    }

    /// <summary>Expands one 64-bit block mask into the global R8 fine occupancy volume.</summary>
    /// <param name="destination">Complete occupancy volume.</param>
    /// <param name="mask">One bit per 4-cubed local cell.</param>
    /// <param name="localBlockX">Main-grid X.</param>
    /// <param name="localBlockY">Main-grid Y.</param>
    /// <param name="localBlockZ">Main-grid Z.</param>
    private void WriteOccupancyMask(
        byte[] destination,
        ulong mask,
        int localBlockX,
        int localBlockY,
        int localBlockZ)
    {
        for (int subZ = 0; subZ < OccupancyScale; subZ++)
        {
            for (int subY = 0; subY < OccupancyScale; subY++)
            {
                for (int subX = 0; subX < OccupancyScale; subX++)
                {
                    int bitIndex = (subZ * OccupancyScale + subY) * OccupancyScale + subX;
                    if ((mask & (1UL << bitIndex)) == 0)
                    {
                        continue;
                    }

                    int occupancyX = localBlockX * OccupancyScale + subX;
                    int occupancyY = localBlockY * OccupancyScale + subY;
                    int occupancyZ = localBlockZ * OccupancyScale + subZ;
                    int occupancyIndex = (occupancyZ * OccupancyHeight + occupancyY)
                        * OccupancyWidth + occupancyX;
                    destination[occupancyIndex] = 255;
                }
            }
        }
    }

    /// <summary>Reads one XYZ triple from packed mesh storage.</summary>
    /// <param name="xyz">Packed coordinates.</param>
    /// <param name="vertexIndex">Vertex index.</param>
    /// <returns>Detached vector.</returns>
    private static Vec3f ReadVertex(float[] xyz, int vertexIndex)
    {
        int offset = vertexIndex * 3;
        return new Vec3f(xyz[offset], xyz[offset + 1], xyz[offset + 2]);
    }

    /// <summary>Compares block geometry coordinates with a 1e-4 tolerance.</summary>
    /// <param name="left">First scalar.</param>
    /// <param name="right">Second scalar.</param>
    /// <returns>Whether absolute difference is at most 0.0001.</returns>
    private static bool NearlyEqual(float left, float right)
    {
        return Math.Abs(left - right) <= 0.0001f;
    }

    /// <summary>Computes squared Euclidean distance from a point to the closest triangle region.</summary>
    /// <param name="point">Query point.</param>
    /// <param name="a">First triangle vertex.</param>
    /// <param name="b">Second triangle vertex.</param>
    /// <param name="c">Third triangle vertex.</param>
    /// <returns>Non-negative squared distance.</returns>
    private static float PointTriangleDistanceSquared(Vec3f point, Vec3f a, Vec3f b, Vec3f c)
    {
        Vec3f ab = Subtract(b, a);
        Vec3f ac = Subtract(c, a);
        Vec3f ap = Subtract(point, a);
        float d1 = Dot(ab, ap);
        float d2 = Dot(ac, ap);
        if (d1 <= 0.0f && d2 <= 0.0f)
        {
            return LengthSquared(ap);
        }

        Vec3f bp = Subtract(point, b);
        float d3 = Dot(ab, bp);
        float d4 = Dot(ac, bp);
        if (d3 >= 0.0f && d4 <= d3)
        {
            return LengthSquared(bp);
        }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
        {
            float v = d1 / (d1 - d3);
            return LengthSquared(Subtract(point, Add(a, Scale(ab, v))));
        }

        Vec3f cp = Subtract(point, c);
        float d5 = Dot(ab, cp);
        float d6 = Dot(ac, cp);
        if (d6 >= 0.0f && d5 <= d6)
        {
            return LengthSquared(cp);
        }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
        {
            float w = d2 / (d2 - d6);
            return LengthSquared(Subtract(point, Add(a, Scale(ac, w))));
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0.0f && d4 - d3 >= 0.0f && d5 - d6 >= 0.0f)
        {
            Vec3f bc = Subtract(c, b);
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return LengthSquared(Subtract(point, Add(b, Scale(bc, w))));
        }

        float denominator = 1.0f / (va + vb + vc);
        float faceV = vb * denominator;
        float faceW = vc * denominator;
        Vec3f projection = Add(a, Add(Scale(ab, faceV), Scale(ac, faceW)));
        return LengthSquared(Subtract(point, projection));
    }

    /// <summary>Adds vector components.</summary>
    /// <param name="left">Left vector.</param><param name="right">Right vector.</param>
    /// <returns>Component sum.</returns>
    private static Vec3f Add(Vec3f left, Vec3f right)
    {
        return new Vec3f(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    }

    /// <summary>Subtracts vector components.</summary>
    /// <param name="left">Left vector.</param><param name="right">Right vector.</param>
    /// <returns>Component difference.</returns>
    private static Vec3f Subtract(Vec3f left, Vec3f right)
    {
        return new Vec3f(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    }

    /// <summary>Scales vector components uniformly.</summary>
    /// <param name="value">Input vector.</param><param name="scale">Scalar multiplier.</param>
    /// <returns>Scaled vector.</returns>
    private static Vec3f Scale(Vec3f value, float scale)
    {
        return new Vec3f(value.X * scale, value.Y * scale, value.Z * scale);
    }

    /// <summary>Computes the Euclidean dot product.</summary>
    /// <param name="left">Left vector.</param><param name="right">Right vector.</param>
    /// <returns>Scalar product.</returns>
    private static float Dot(Vec3f left, Vec3f right)
    {
        return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    }

    /// <summary>Computes squared vector magnitude without a square root.</summary>
    /// <param name="value">Input vector.</param>
    /// <returns>Non-negative squared length.</returns>
    private static float LengthSquared(Vec3f value)
    {
        return Dot(value, value);
    }

    /// <summary>Classifies solids for tracing while retaining crossed-plane plants and excluding fluids/air.</summary>
    /// <param name="block">Solid-layer block candidate.</param>
    /// <returns>Whether material/occupancy sampling should include the block.</returns>
    private static bool IsTraceableSolid(Block block)
    {
        if (block is null || block.Id == 0 || block.BlockMaterial == EnumBlockMaterial.Air)
        {
            return false;
        }

        if (block.BlockMaterial == EnumBlockMaterial.Water)
        {
            return false;
        }

        if (block.BlockMaterial == EnumBlockMaterial.Plant)
        {
            // Grasses, flowers, ferns and crops are crossed alpha-tested planes.
            // They are traceable through their tessellated triangles even when
            // they deliberately expose no collision or selection AABB.
            return ShouldCastShadow(block.RenderPass);
        }

        return block.CollisionBoxes is { Length: > 0 } || block.LightAbsorption > 0;
    }

    /// <summary>
    /// Packs all eight world blocks represented by one sun cell into a 4-cubed, 0.5-metre mask.
    /// Each block contributes a downsampled 2-cubed mask at its exact position in the parent.
    /// </summary>
    /// <param name="localX">Sun-grid X.</param>
    /// <param name="localY">Sun-grid Y.</param>
    /// <param name="localZ">Sun-grid Z.</param>
    /// <returns>Bit-packed parent occupancy in X-fastest, then Y, then Z order.</returns>
    private ulong SampleSunOccupancyMask(int localX, int localY, int localZ)
    {
        int worldBaseX = sunOriginX + localX * SunOccupancyScale;
        int worldBaseY = sunOriginY + localY * SunOccupancyScale;
        int worldBaseZ = sunOriginZ + localZ * SunOccupancyScale;
        IBlockAccessor accessor = api.World.BlockAccessor;
        ulong parentMask = 0;
        for (int offsetZ = 0; offsetZ < SunOccupancyScale; offsetZ++)
        {
            for (int offsetY = 0; offsetY < SunOccupancyScale; offsetY++)
            {
                for (int offsetX = 0; offsetX < SunOccupancyScale; offsetX++)
                {
                    samplePosition.Set(
                        worldBaseX + offsetX,
                        worldBaseY + offsetY,
                        worldBaseZ + offsetZ);
                    Block? block = accessor.GetBlock(samplePosition, BlockLayersAccess.Solid);
                    if (block is null || !IsSunShadowCaster(block))
                    {
                        continue;
                    }

                    parentMask = PackSunBlockMask(
                        parentMask,
                        ResolveSunShadowMask(block, samplePosition),
                        offsetX,
                        offsetY,
                        offsetZ);
                }
            }
        }

        return parentMask;
    }

    /// <summary>Resolves per-block sun occupancy from instance/static detailed geometry.</summary>
    /// <param name="block">Shadow-caster candidate.</param>
    /// <param name="position">Exact world instance position.</param>
    /// <returns>Quarter-block occupancy mask.</returns>
    private ulong ResolveSunShadowMask(Block block, BlockPos position)
    {
        CachedBlockOccupancy occupancy = GetCachedBlockOccupancy(block);
        if (occupancy.GeometryKind == BlockGeometryKind.DynamicInstance)
        {
            bool hasInstanceTessellation = TryGetInstanceMeshOccupancy(
                block,
                position,
                out InstanceMeshOccupancy tessellatedInstance);
            bool replacesDefaultMesh = InstanceGeometryReplacesDefaultMesh(block)
                || (hasInstanceTessellation && tessellatedInstance.SkipsDefaultMesh);
            if (TryResolveTessellatedInstanceOccupancy(
                    hasInstanceTessellation,
                    tessellatedInstance.CapturedMeshCount,
                    tessellatedInstance.SkipsDefaultMesh,
                    replacesDefaultMesh,
                    occupancy.Mask,
                    tessellatedInstance.Mask,
                    out ulong tessellatedMask))
            {
                return tessellatedMask;
            }

            ulong mask = ResolveDynamicInstanceOccupancy(
                occupancy.Mask,
                occupancy.HasDetailedMesh,
                InstanceGeometryReplacesDefaultMesh(block),
                BuildBoxMask(block.GetCollisionBoxes(api.World.BlockAccessor, position)),
                BuildBoxMask(block.GetSelectionBoxes(api.World.BlockAccessor, position)),
                block.LightAbsorption > 0);
            return mask;
        }

        if (occupancy.GeometryKind == BlockGeometryKind.FullCubeStatic)
        {
            return ulong.MaxValue;
        }

        if (occupancy.HasDetailedMesh)
        {
            return occupancy.Mask;
        }

        if (block.BlockMaterial == EnumBlockMaterial.Plant)
        {
            // An absent crossed-plane mesh carries no trustworthy silhouette.
            // Its broad interaction box must not become a distant opaque cube.
            return 0;
        }

        // Match the fine-volume fallback contract without turning every
        // non-plant static mesh into a full cube. Partial collision/selection
        // boxes retain their location; only an absorbing shape with no usable
        // geometry receives conservative full occupancy.
        ulong collisionMask = BuildBoxMask(
            block.GetCollisionBoxes(api.World.BlockAccessor, position));
        ulong selectionMask = BuildBoxMask(
            block.GetSelectionBoxes(api.World.BlockAccessor, position));
        ulong fallback = SelectInstanceOccupancyMask(collisionMask, selectionMask);
        if (fallback != 0)
        {
            return fallback;
        }

        return block.LightAbsorption > 0 ? ulong.MaxValue : 0;
    }

    /// <summary>
    /// Downsamples one 4-cubed block mask to 2-cubed and places it in its 2-metre parent cell.
    /// Any occupied quarter-block sample marks the containing half-metre parent sample.
    /// </summary>
    /// <param name="parentMask">Accumulated 4-cubed parent mask.</param>
    /// <param name="blockMask">Source 4-cubed quarter-block mask.</param>
    /// <param name="blockX">Block offset 0..1 within the parent.</param>
    /// <param name="blockY">Block offset 0..1 within the parent.</param>
    /// <param name="blockZ">Block offset 0..1 within the parent.</param>
    /// <returns>Parent mask with the block contribution unioned in place.</returns>
    internal static ulong PackSunBlockMask(
        ulong parentMask,
        ulong blockMask,
        int blockX,
        int blockY,
        int blockZ)
    {
        if (blockMask == 0)
        {
            return parentMask;
        }

        for (int coarseZ = 0; coarseZ < 2; coarseZ++)
        {
            for (int coarseY = 0; coarseY < 2; coarseY++)
            {
                for (int coarseX = 0; coarseX < 2; coarseX++)
                {
                    bool occupied = false;
                    for (int fineZ = coarseZ * 2; fineZ < coarseZ * 2 + 2 && !occupied; fineZ++)
                    {
                        for (int fineY = coarseY * 2; fineY < coarseY * 2 + 2 && !occupied; fineY++)
                        {
                            for (int fineX = coarseX * 2; fineX < coarseX * 2 + 2; fineX++)
                            {
                                int sourceBit = (fineZ * OccupancyScale + fineY)
                                    * OccupancyScale + fineX;
                                if ((blockMask & (1UL << sourceBit)) != 0)
                                {
                                    occupied = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!occupied)
                    {
                        continue;
                    }

                    int parentX = blockX * 2 + coarseX;
                    int parentY = blockY * 2 + coarseY;
                    int parentZ = blockZ * 2 + coarseZ;
                    int destinationBit = (parentZ * OccupancyScale + parentY)
                        * OccupancyScale + parentX;
                    parentMask |= 1UL << destinationBit;
                }
            }
        }

        return parentMask;
    }

    /// <summary>Rejects non-solids, glass/ice, and transparent render passes from long-range sun occlusion.</summary>
    /// <param name="block">Candidate solid block.</param>
    /// <returns>Whether it contributes sun coverage.</returns>
    private static bool IsSunShadowCaster(Block block)
    {
        if (!IsTraceableSolid(block)
            || block.BlockMaterial is EnumBlockMaterial.Glass or EnumBlockMaterial.Ice)
        {
            return false;
        }

        return ShouldCastShadow(block.RenderPass);
    }

    /// <summary>Reads the client calendar sun direction and guarantees a finite unit/up fallback vector.</summary>
    /// <returns>Normalized world-space direction toward the sun.</returns>
    private Vec3f GetNormalizedSunDirection()
    {
        IClientGameCalendar? calendar = api.World.Calendar as IClientGameCalendar;
        Vec3f direction = calendar?.SunPositionNormalized ?? new Vec3f(0.0f, 1.0f, 0.0f);
        float lengthSquared = LengthSquared(direction);
        return lengthSquared > 0.000001f
            ? Scale(direction, 1.0f / MathF.Sqrt(lengthSquared))
            : new Vec3f(0.0f, 1.0f, 0.0f);
    }

    /// <summary>Places an aligned 152x116x152 world volume asymmetrically toward the sun.</summary>
    /// <param name="cameraX">Integer camera X.</param>
    /// <param name="cameraY">Integer camera Y.</param>
    /// <param name="cameraZ">Integer camera Z.</param>
    /// <param name="sunDirection">Normalized world sun direction.</param>
    /// <returns>Origin aligned to two-block occupancy cells, including negative coordinates.</returns>
    internal static (int X, int Y, int Z) CalculateSunClipmapOrigin(
        int cameraX,
        int cameraY,
        int cameraZ,
        Vec3f sunDirection)
    {
        // Center the complete 96-block ray sweep inside the volume while keeping
        // at least 24 horizontal and 8 vertical blocks of receiver coverage.
        // The remaining 8x4x8 blocks are symmetric hysteresis against small sun
        // movements. Origins stay aligned to the 2-block cells so incremental
        // updates and DDA texture coordinates remain exact for negative worlds.
        int cameraLocalX = SunWorldWidth / 2
            - (int)MathF.Round(
                Math.Clamp(sunDirection.X, -1.0f, 1.0f)
                    * (MaximumSunTraceDistance * 0.5f));
        int cameraLocalY = SunWorldHeight / 2
            - (int)MathF.Round(
                Math.Clamp(sunDirection.Y, -1.0f, 1.0f)
                    * (MaximumSunTraceDistance * 0.5f));
        int cameraLocalZ = SunWorldDepth / 2
            - (int)MathF.Round(
                Math.Clamp(sunDirection.Z, -1.0f, 1.0f)
                    * (MaximumSunTraceDistance * 0.5f));
        return (
            AlignDown(cameraX - cameraLocalX, SunOccupancyScale),
            AlignDown(cameraY - cameraLocalY, SunOccupancyScale),
            AlignDown(cameraZ - cameraLocalZ, SunOccupancyScale));
    }

    /// <summary>Floors an integer coordinate to a positive grid scale, including negative worlds.</summary>
    /// <param name="value">World coordinate.</param>
    /// <param name="scale">Positive alignment quantum.</param>
    /// <returns>Greatest aligned value not exceeding input.</returns>
    private static int AlignDown(int value, int scale)
    {
        int remainder = value % scale;
        return remainder < 0 ? value - remainder - scale : value - remainder;
    }

    /// <summary>Writes atlas-derived BGR-corrected albedo and a coarse transmission/material class.</summary>
    /// <param name="destination">Packed material volume.</param>
    /// <param name="block">Sampled block.</param>
    /// <param name="byteIndex">Destination byte offset.</param>
    /// <param name="worldX">World X.</param>
    /// <param name="worldY">World Y.</param>
    /// <param name="worldZ">World Z.</param>
    /// <param name="alphaOverride">Optional explicit material class; zero derives it from block material.</param>
    /// <param name="partialGeometry">Whether to encode the non-full authored-geometry metadata bit.</param>
    private void WriteBlockMaterial(
        byte[] destination,
        Block block,
        int byteIndex,
        int worldX,
        int worldY,
        int worldZ,
        byte alphaOverride = 0,
        bool partialGeometry = false)
    {
        samplePosition.Set(worldX, worldY, worldZ);
        int packedColor = block.GetColor(api, samplePosition);
        // Block.GetColor is sourced from the atlas/map colour path whose packed
        // red/blue order differs from light-HSV colours in the current client.
        // Runtime voxel-albedo captures are the authoritative check here: using
        // ColorR as red turns brown wood blue, while the reversed atlas pair
        // restores the source texture chromaticity. Keep light colours below on
        // their separate, documented HSV path.
        destination[byteIndex] = ColorUtil.ColorB(packedColor);
        destination[byteIndex + 1] = ColorUtil.ColorG(packedColor);
        destination[byteIndex + 2] = ColorUtil.ColorR(packedColor);
        byte baseMaterialClass = alphaOverride != 0
            ? alphaOverride
            : block.BlockMaterial switch
            {
                EnumBlockMaterial.Glass or EnumBlockMaterial.Ice => (byte)64,
                EnumBlockMaterial.Metal => (byte)192,
                _ => (byte)128
            };
        destination[byteIndex + 3] = EncodeMaterialGeometryClass(
            baseMaterialClass,
            partialGeometry);
    }

    /// <summary>
    /// Adds partial-geometry metadata without crossing any coarse material-class boundary.
    /// </summary>
    /// <param name="baseMaterialClass">Existing normalized-byte material class.</param>
    /// <param name="partialGeometry">Whether the solid can leave open volume inside its block.</param>
    /// <returns>Material byte retaining its original upper-bit class and optional geometry bit.</returns>
    internal static byte EncodeMaterialGeometryClass(
        byte baseMaterialClass,
        bool partialGeometry) => partialGeometry
            ? (byte)(baseMaterialClass | PartialGeometryMaterialFlag)
            : baseMaterialClass;

    /// <summary>
    /// Classifies cached authored shapes for the GPU material ABI. Dynamic instances remain marked
    /// because a chiseled or block-entity mesh can vary per position; static shapes are marked only
    /// when their alpha-aware quarter-block occupancy is genuinely partial.
    /// </summary>
    /// <param name="block">Traceable solid whose cached geometry contract is available.</param>
    /// <returns>Whether water and other layered effects must respect non-full geometry.</returns>
    private bool HasPotentialPartialGeometry(Block block)
    {
        CachedBlockOccupancy occupancy = GetCachedBlockOccupancy(block);
        return occupancy.GeometryKind == BlockGeometryKind.DynamicInstance
            || occupancy.Mask is not 0 and not ulong.MaxValue;
    }

    /// <summary>Decodes block HSV emission, calibrates warm emitters, and records geometry-aware light data.</summary>
    /// <param name="block">Potential emitter.</param>
    /// <param name="worldX">World X.</param>
    /// <param name="worldY">World Y.</param>
    /// <param name="worldZ">World Z.</param>
    private void CollectLight(Block block, int worldX, int worldY, int worldZ)
    {
        if (block is null || block.Id == 0)
        {
            return;
        }

        samplePosition.Set(worldX, worldY, worldZ);
        byte[] lightHsv = block.GetLightHsv(api.World.BlockAccessor, samplePosition, null);
        if (lightHsv is not { Length: >= 3 } || lightHsv[2] == 0)
        {
            return;
        }

        int rgb = ColorUtil.HsvToRgb(
            Math.Min(lightHsv[0] * ColorUtil.HueMul, 255),
            Math.Min(lightHsv[1] * ColorUtil.SatMul, 255),
            Math.Min(lightHsv[2] * ColorUtil.BrightMul, 255));
        float red = ColorUtil.ColorR(rgb) / 255.0f;
        float green = ColorUtil.ColorG(rgb) / 255.0f;
        float blue = ColorUtil.ColorB(rgb) / 255.0f;
        string blockCode = block.Code?.ToString() ?? "unknown";
        if (IsWarmEmitter(blockCode))
        {
            // Vanilla lanterns intentionally use a low-saturation lightHsv.
            // Blend it with a 2700 K sRGB response while retaining part of the
            // authored hue. The previous green/blue targets were far below a
            // black-body response and covered interiors in an orange veil.
            red = red * 0.22f + 0.78f;
            green = green * 0.22f + 0.507f;
            blue = blue * 0.22f + 0.265f;
        }
        float brightness = Math.Clamp(lightHsv[2] / 16.0f, 0.25f, 2.0f);
        CachedBlockOccupancy occupancy = GetCachedBlockOccupancy(block);
        CachedLightCaster caster = GetLightCaster(block, occupancy);
        buildLights.Add(new VoxelLight(
            worldX + caster.EmissionOffset.X,
            worldY + caster.EmissionOffset.Y,
            worldZ + caster.EmissionOffset.Z,
            red,
            green,
            blue,
            brightness,
            blockCode,
            caster.Mask));
    }

    /// <summary>Recognizes fire-temperature emitter families requiring the calibrated 2700 K blend.</summary>
    /// <param name="blockCode">Canonical block code.</param>
    /// <returns>Whether warm color correction applies.</returns>
    private static bool IsWarmEmitter(string blockCode)
    {
        return blockCode.Contains("lantern", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("torch", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("candle", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("fire", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("flame", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("ember", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("forge", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("bloomery", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Atomically publishes completed grids, selects lights, builds radiance, and logs geometry coverage.</summary>
    private void CompleteRebuild()
    {
        readyVoxels = buildVoxels.ToArray();
        readyOccupancy = buildOccupancy.ToArray();
        readyFluidSurface = buildFluidSurface.ToArray();
        readyLiquidMetadata = buildLiquidMetadata.ToArray();
        readySunOccupancy = buildSunOccupancy.ToArray();
        readyRainSurface = buildRainSurface.ToArray();
        readyFluidVoxels = buildFluidVoxels;
        readyVisibleLiquidContainers = buildVisibleLiquidContainers;

        Vec3d cameraPosition = api.World.Player.Entity.CameraPos;
        readyLights = buildLights
            .OrderByDescending(light => light.Score(cameraPosition))
            .Take(MaximumLightCount)
            .ToArray();
        IClientGameCalendar? calendar = api.World.Calendar as IClientGameCalendar;
        float daylight = Math.Clamp(
            calendar?.GetDayLightStrength(cameraPosition.X, cameraPosition.Z) ?? 0.0f,
            0.0f,
            1.0f);
        Vec3f skyRadiance = new(
            0.0025f + daylight * 0.068f,
            0.0045f + daylight * 0.096f,
            0.0100f + daylight * 0.142f);
        VoxelRadianceField radianceField = BuildRadianceField(
            readyVoxels,
            readyLights,
            originX,
            originY,
            originZ,
            skyRadiance);
        readyIrradiance = radianceField.Irradiance;
        readyIrradianceDirection = radianceField.Direction;
        generation++;
        uploadPending = true;
        building = false;

        api.Logger.Notification(
            "[VintageRTX] Voxel scene generation {0} ready: {1}x{2}x{3} materials, {4}x occupancy, {5} emissive blocks, {6} fluid voxels, {7} visible liquid containers, {8} optical profiles; geometry mesh={9}, collision={10}, selection={11}, fallback={12}; sun clipmap={13}x{14}x{15} blocks at ({16},{17},{18}); liquid surface={19}x{20} at ({21},{22}).",
            generation,
            Width,
            Height,
            Depth,
            OccupancyScale,
            readyLights.Length,
            readyFluidVoxels,
            readyVisibleLiquidContainers,
            liquidOptics.ProfileCount,
            meshOccupancyBlocks,
            collisionOccupancyBlocks,
            selectionOccupancyBlocks,
            fallbackOccupancyBlocks,
            SunWorldWidth,
            SunWorldHeight,
            SunWorldDepth,
            sunOriginX,
            sunOriginY,
            sunOriginZ,
            FluidSurfaceWidth,
            FluidSurfaceDepth,
            fluidSurfaceOriginX,
            fluidSurfaceOriginZ);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID")))
        {
            foreach (KeyValuePair<(string Code, string Reason), int> entry in fallbackOccupancyHistogram
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key.Code, StringComparer.Ordinal)
                .Take(12))
            {
                api.Logger.Notification(
                    "[VintageRTX.Test] Occupancy fallback: code={0}, reason={1}, count={2}.",
                    entry.Key.Code,
                    entry.Key.Reason,
                    entry.Value);
            }
        }
        for (int index = 0; index < readyLights.Length; index++)
        {
            VoxelLight light = readyLights[index];
            Action<string, object[]> log = string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID"))
                ? api.Logger.Debug
                : api.Logger.Notification;
            LightCasterBandCoverage bands = MeasureLightCasterBands(light.CasterMask);
            log(
                "[VintageRTX] Emissive {0}: {1} at ({2:0.000},{3:0.000},{4:0.000}), rgb=({5:0.000},{6:0.000},{7:0.000}), intensity={8:0.00}, fine caster solid={9}, alpha={10}, total={11}/{12} voxels; bands lower={13}, middle={14}, upper={15}, empty={16}.",
                [
                    index,
                    light.Code,
                    light.X,
                    light.Y,
                    light.Z,
                    light.Red,
                    light.Green,
                    light.Blue,
                    light.Intensity,
                    light.CasterMask.Count(value => value == 255),
                    light.CasterMask.Count(value => value is > 0 and < 255),
                    light.CasterMask.Count(value => value != 0),
                    LightCasterVoxelCount,
                    bands.LowerOccupied,
                    bands.MiddleOccupied,
                    bands.UpperOccupied,
                    bands.Empty
                ]);
        }
    }

    /// <summary>
    /// Builds a stable low-frequency radiance cache around static emitters.
    /// Direct shadows remain ray traced in the display shader; this volume only
    /// represents the diffuse energy that survives several wall-bounded bounces.
    /// Keeping that low-frequency transport in a linearly filtered texture avoids
    /// the one-ray, one-block patches that a per-pixel performance path produces.
    /// </summary>
    internal static byte[] BuildIrradianceVolume(
        byte[] voxels,
        IReadOnlyList<VoxelLight> lights,
        int sceneOriginX,
        int sceneOriginY,
        int sceneOriginZ)
    {
        return BuildRadianceField(
            voxels,
            lights,
            sceneOriginX,
            sceneOriginY,
            sceneOriginZ,
            new Vec3f()).Irradiance;
    }

    /// <summary>
    /// Seeds only actually illuminated diffuse surfaces, propagates bounded lossy RGB transport through
    /// air, and encodes HDR magnitude plus dominant direction. Opaque material cells stop leakage.
    /// </summary>
    /// <param name="voxels">Packed main-grid material volume.</param>
    /// <param name="lights">World-space emitters with intensity/color.</param>
    /// <param name="sceneOriginX">World origin X.</param>
    /// <param name="sceneOriginY">World origin Y.</param>
    /// <param name="sceneOriginZ">World origin Z.</param>
    /// <param name="skyRadiance">Linear environment RGB.</param>
    /// <returns>Square-root encoded irradiance and UNorm8 signed direction textures.</returns>
    internal static VoxelRadianceField BuildRadianceField(
        byte[] voxels,
        IReadOnlyList<VoxelLight> lights,
        int sceneOriginX,
        int sceneOriginY,
        int sceneOriginZ,
        Vec3f skyRadiance)
    {
        int cellCount = Width * Height * Depth;
        if (voxels.Length != cellCount * BytesPerVoxel)
        {
            throw new ArgumentException("Unexpected voxel material volume size.", nameof(voxels));
        }

        float[] seeds = new float[cellCount * IrradianceChannels];
        float[] seedDirections = new float[cellCount * IrradianceDirectionChannels];
        float[] seedDirectionWeights = new float[cellCount];

        // Cache only reflected energy. Injecting the emitter colour directly
        // into nearby air made several lanterns merge into a uniform orange
        // fog, even when their rooms were separated by opaque walls. A diffuse
        // bounce originates at an illuminated surface: visibility, face
        // orientation and the block albedo all have to participate first.
        foreach (VoxelLight light in lights)
        {
            float lightRed = SrgbToLinear(light.Red) * 1.20f;
            float lightGreen = SrgbToLinear(light.Green) * 1.20f;
            float lightBlue = SrgbToLinear(light.Blue) * 1.20f;
            int centerX = (int)MathF.Floor(light.X) - sceneOriginX;
            int centerY = (int)MathF.Floor(light.Y) - sceneOriginY;
            int centerZ = (int)MathF.Floor(light.Z) - sceneOriginZ;
            int radius = (int)MathF.Ceiling(IrradianceEmitterRadius);
            for (int z = Math.Max(centerZ - radius, 0); z <= Math.Min(centerZ + radius, Depth - 1); z++)
            {
                for (int y = Math.Max(centerY - radius, 0); y <= Math.Min(centerY + radius, Height - 1); y++)
                {
                    for (int x = Math.Max(centerX - radius, 0); x <= Math.Min(centerX + radius, Width - 1); x++)
                    {
                        int cellIndex = CellIndex(x, y, z);
                        if (!IsIrradianceObstacle(voxels, cellIndex))
                        {
                            continue;
                        }

                        int materialIndex = cellIndex * BytesPerVoxel;
                        float albedoRed = SrgbToLinear(voxels[materialIndex] / 255.0f);
                        float albedoGreen = SrgbToLinear(voxels[materialIndex + 1] / 255.0f);
                        float albedoBlue = SrgbToLinear(voxels[materialIndex + 2] / 255.0f);
                        for (int faceIndex = 0; faceIndex < IrradianceFaces.Length; faceIndex++)
                        {
                            (int faceX, int faceY, int faceZ) = IrradianceFaces[faceIndex];
                            int airX = x + faceX;
                            int airY = y + faceY;
                            int airZ = z + faceZ;
                            if ((uint)airX >= Width || (uint)airY >= Height || (uint)airZ >= Depth)
                            {
                                continue;
                            }

                            int airCellIndex = CellIndex(airX, airY, airZ);
                            if (IsIrradianceObstacle(voxels, airCellIndex))
                            {
                                continue;
                            }

                            float surfaceX = sceneOriginX + x + 0.5f + faceX * 0.501f;
                            float surfaceY = sceneOriginY + y + 0.5f + faceY * 0.501f;
                            float surfaceZ = sceneOriginZ + z + 0.5f + faceZ * 0.501f;
                            float toLightX = light.X - surfaceX;
                            float toLightY = light.Y - surfaceY;
                            float toLightZ = light.Z - surfaceZ;
                            float distanceSquared = toLightX * toLightX
                                + toLightY * toLightY
                                + toLightZ * toLightZ;
                            if (distanceSquared <= 0.0001f
                                || distanceSquared >= IrradianceEmitterRadius * IrradianceEmitterRadius)
                            {
                                continue;
                            }

                            float distance = MathF.Sqrt(distanceSquared);
                            float receiver = (faceX * toLightX + faceY * toLightY + faceZ * toLightZ)
                                / distance;
                            if (receiver <= 0.001f
                                || !TraceIrradianceVisibility(
                                    voxels,
                                    airX + 0.5f,
                                    airY + 0.5f,
                                    airZ + 0.5f,
                                    light.X - sceneOriginX,
                                    light.Y - sceneOriginY,
                                    light.Z - sceneOriginZ))
                            {
                                continue;
                            }

                            float radiusFade = 1.0f - SmoothStep(
                                IrradianceEmitterRadius * 0.58f,
                                IrradianceEmitterRadius,
                                distance);
                            float attenuation = light.Intensity
                                * receiver
                                * radiusFade
                                / (1.0f + 0.075f * distanceSquared);
                            AddIrradianceSeed(
                                seeds,
                                seedDirections,
                                seedDirectionWeights,
                                airCellIndex,
                                lightRed * albedoRed * attenuation * DiffuseBounceGain,
                                lightGreen * albedoGreen * attenuation * DiffuseBounceGain,
                                lightBlue * albedoBlue * attenuation * DiffuseBounceGain,
                                -faceX,
                                -faceY,
                                -faceZ);
                        }
                    }
                }
            }
        }

        // Mark vertically sky-visible air, then seed only the surfaces reached
        // from that air. The cache consequently stores reflected environment
        // light rather than adding the sky radiance itself a second time.
        float skyWeight = skyRadiance.X * 0.2126f
            + skyRadiance.Y * 0.7152f
            + skyRadiance.Z * 0.0722f;
        if (skyWeight > 0.00001f)
        {
            bool[] skyVisibleAir = new bool[cellCount];
            for (int z = 0; z < Depth; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    bool skyVisible = true;
                    for (int y = Height - 1; y >= 0; y--)
                    {
                        int cellIndex = CellIndex(x, y, z);
                        if (IsIrradianceObstacle(voxels, cellIndex))
                        {
                            skyVisible = false;
                            continue;
                        }
                        if (!skyVisible)
                        {
                            continue;
                        }

                        skyVisibleAir[cellIndex] = true;
                    }
                }
            }

            for (int z = 0; z < Depth; z++)
            {
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int cellIndex = CellIndex(x, y, z);
                        if (!IsIrradianceObstacle(voxels, cellIndex))
                        {
                            continue;
                        }

                        int materialIndex = cellIndex * BytesPerVoxel;
                        float albedoRed = SrgbToLinear(voxels[materialIndex] / 255.0f);
                        float albedoGreen = SrgbToLinear(voxels[materialIndex + 1] / 255.0f);
                        float albedoBlue = SrgbToLinear(voxels[materialIndex + 2] / 255.0f);
                        for (int faceIndex = 0; faceIndex < IrradianceFaces.Length; faceIndex++)
                        {
                            (int faceX, int faceY, int faceZ) = IrradianceFaces[faceIndex];
                            int airX = x + faceX;
                            int airY = y + faceY;
                            int airZ = z + faceZ;
                            if ((uint)airX >= Width || (uint)airY >= Height || (uint)airZ >= Depth)
                            {
                                continue;
                            }

                            int airCellIndex = CellIndex(airX, airY, airZ);
                            if (!skyVisibleAir[airCellIndex])
                            {
                                continue;
                            }

                            // A downward-facing side can never border sky-visible air: the
                            // opaque receiver itself lies directly above that cell. Only the
                            // upward and horizontal cases survive the visibility test above.
                            float skyReceiver = faceY > 0 ? 1.0f : 0.28f;
                            AddIrradianceSeed(
                                seeds,
                                seedDirections,
                                seedDirectionWeights,
                                airCellIndex,
                                skyRadiance.X * albedoRed * skyReceiver * DiffuseBounceGain,
                                skyRadiance.Y * albedoGreen * skyReceiver * DiffuseBounceGain,
                                skyRadiance.Z * albedoBlue * skyReceiver * DiffuseBounceGain,
                                -faceX,
                                -faceY,
                                -faceZ);
                        }
                    }
                }
            }
        }

        float[] current = seeds.ToArray();
        float[] next = new float[current.Length];
        for (int iteration = 0; iteration < IrradiancePropagationIterations; iteration++)
        {
            Array.Clear(next);
            for (int z = 0; z < Depth; z++)
            {
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int cellIndex = CellIndex(x, y, z);
                        if (IsIrradianceObstacle(voxels, cellIndex))
                        {
                            continue;
                        }

                        int output = cellIndex * IrradianceChannels;
                        for (int channel = 0; channel < IrradianceChannels; channel++)
                        {
                            float maximumNeighbor = 0.0f;
                            float neighborSum = 0.0f;
                            int neighborCount = 0;
                            AccumulateIrradianceNeighbor(
                                current,
                                voxels,
                                x - 1,
                                y,
                                z,
                                channel,
                                ref maximumNeighbor,
                                ref neighborSum,
                                ref neighborCount);
                            AccumulateIrradianceNeighbor(
                                current,
                                voxels,
                                x + 1,
                                y,
                                z,
                                channel,
                                ref maximumNeighbor,
                                ref neighborSum,
                                ref neighborCount);
                            AccumulateIrradianceNeighbor(
                                current,
                                voxels,
                                x,
                                y - 1,
                                z,
                                channel,
                                ref maximumNeighbor,
                                ref neighborSum,
                                ref neighborCount);
                            AccumulateIrradianceNeighbor(
                                current,
                                voxels,
                                x,
                                y + 1,
                                z,
                                channel,
                                ref maximumNeighbor,
                                ref neighborSum,
                                ref neighborCount);
                            AccumulateIrradianceNeighbor(
                                current,
                                voxels,
                                x,
                                y,
                                z - 1,
                                channel,
                                ref maximumNeighbor,
                                ref neighborSum,
                                ref neighborCount);
                            AccumulateIrradianceNeighbor(
                                current,
                                voxels,
                                x,
                                y,
                                z + 1,
                                channel,
                                ref maximumNeighbor,
                                ref neighborSum,
                                ref neighborCount);

                            float neighborAverage = neighborCount > 0
                                ? neighborSum / neighborCount
                                : 0.0f;
                            // A dominant-path term retains readable bounce
                            // direction while the lower average term rounds
                            // corners. Their sum stays below one so every cell
                            // crossing loses energy instead of filling a room
                            // with a constant source colour.
                            float propagated = maximumNeighbor * 0.62f
                                + neighborAverage * 0.14f;
                            next[output + channel] = Math.Max(
                                seeds[output + channel],
                                propagated);
                        }
                    }
                }
            }

            (current, next) = (next, current);
        }

        byte[] encoded = new byte[cellCount * IrradianceChannels];
        for (int index = 0; index < encoded.Length; index++)
        {
            // Square-root HDR encoding retains useful precision in the dark end;
            // the shader squares the linearly filtered sample and restores range.
            float normalized = Math.Clamp(
                current[index] / IrradianceEncodingRange,
                0.0f,
                1.0f);
            encoded[index] = (byte)Math.Clamp(
                (int)MathF.Round(MathF.Sqrt(normalized) * 255.0f),
                0,
                255);
        }

        byte[] encodedDirection = new byte[cellCount * IrradianceDirectionChannels];
        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int cellIndex = CellIndex(x, y, z);
                    int directionIndex = cellIndex * IrradianceDirectionChannels;
                    float directionX = IrradianceLuminance(current, voxels, x + 1, y, z)
                        - IrradianceLuminance(current, voxels, x - 1, y, z);
                    float directionY = IrradianceLuminance(current, voxels, x, y + 1, z)
                        - IrradianceLuminance(current, voxels, x, y - 1, z);
                    float directionZ = IrradianceLuminance(current, voxels, x, y, z + 1)
                        - IrradianceLuminance(current, voxels, x, y, z - 1);
                    if (seedDirectionWeights[cellIndex] > 0.00001f)
                    {
                        float seedInfluence = 0.45f / seedDirectionWeights[cellIndex];
                        directionX += seedDirections[directionIndex] * seedInfluence;
                        directionY += seedDirections[directionIndex + 1] * seedInfluence;
                        directionZ += seedDirections[directionIndex + 2] * seedInfluence;
                    }

                    float directionLength = MathF.Sqrt(
                        directionX * directionX
                        + directionY * directionY
                        + directionZ * directionZ);
                    if (directionLength <= 0.00001f)
                    {
                        directionX = 0.0f;
                        directionY = 1.0f;
                        directionZ = 0.0f;
                    }
                    else
                    {
                        float inverseLength = 1.0f / directionLength;
                        directionX *= inverseLength;
                        directionY *= inverseLength;
                        directionZ *= inverseLength;
                    }

                    encodedDirection[directionIndex] = EncodeDirection(directionX);
                    encodedDirection[directionIndex + 1] = EncodeDirection(directionY);
                    encodedDirection[directionIndex + 2] = EncodeDirection(directionZ);
                }
            }
        }

        return new VoxelRadianceField(encoded, encodedDirection);
    }

    /// <summary>Samples Rec.709 luminance from an in-bounds non-obstacle irradiance cell.</summary>
    /// <param name="values">Linear RGB irradiance array.</param>
    /// <param name="voxels">Packed material volume.</param>
    /// <param name="x">Grid X.</param><param name="y">Grid Y.</param><param name="z">Grid Z.</param>
    /// <returns>Luminance or zero outside air volume.</returns>
    private static float IrradianceLuminance(
        float[] values,
        byte[] voxels,
        int x,
        int y,
        int z)
    {
        if ((uint)x >= Width || (uint)y >= Height || (uint)z >= Depth)
        {
            return 0.0f;
        }

        int cellIndex = CellIndex(x, y, z);
        if (IsIrradianceObstacle(voxels, cellIndex))
        {
            return 0.0f;
        }

        int channelIndex = cellIndex * IrradianceChannels;
        return values[channelIndex] * 0.2126f
            + values[channelIndex + 1] * 0.7152f
            + values[channelIndex + 2] * 0.0722f;
    }

    /// <summary>Maps a signed normalized direction component from -1..1 to UNorm8.</summary>
    /// <param name="value">Signed component.</param>
    /// <returns>Encoded byte.</returns>
    private static byte EncodeDirection(float value)
    {
        return (byte)Math.Clamp(
            (int)MathF.Round((value * 0.5f + 0.5f) * 255.0f),
            0,
            255);
    }

    /// <summary>Adds clamped linear RGB and luminance-weighted incoming direction to one surface-adjacent cell.</summary>
    /// <param name="seeds">Linear RGB seed array.</param>
    /// <param name="seedDirections">Weighted XYZ direction sums.</param>
    /// <param name="seedDirectionWeights">Per-cell luminance weights.</param>
    /// <param name="cellIndex">Target air cell.</param>
    /// <param name="red">Linear red energy.</param><param name="green">Linear green energy.</param><param name="blue">Linear blue energy.</param>
    /// <param name="directionX">Incoming X.</param><param name="directionY">Incoming Y.</param><param name="directionZ">Incoming Z.</param>
    private static void AddIrradianceSeed(
        float[] seeds,
        float[] seedDirections,
        float[] seedDirectionWeights,
        int cellIndex,
        float red,
        float green,
        float blue,
        float directionX,
        float directionY,
        float directionZ)
    {
        int channelIndex = cellIndex * IrradianceChannels;
        seeds[channelIndex] = Math.Min(
            IrradianceEncodingRange,
            seeds[channelIndex] + Math.Max(red, 0.0f));
        seeds[channelIndex + 1] = Math.Min(
            IrradianceEncodingRange,
            seeds[channelIndex + 1] + Math.Max(green, 0.0f));
        seeds[channelIndex + 2] = Math.Min(
            IrradianceEncodingRange,
            seeds[channelIndex + 2] + Math.Max(blue, 0.0f));

        float directionWeight = Math.Max(
            red * 0.2126f + green * 0.7152f + blue * 0.0722f,
            0.0f);
        if (directionWeight <= 0.000001f)
        {
            return;
        }

        int directionIndex = cellIndex * IrradianceDirectionChannels;
        seedDirections[directionIndex] += directionX * directionWeight;
        seedDirections[directionIndex + 1] += directionY * directionWeight;
        seedDirections[directionIndex + 2] += directionZ * directionWeight;
        seedDirectionWeights[cellIndex] += directionWeight;
    }

    /// <summary>Traces a bounded half-cell-step visibility segment through coarse material occupancy.</summary>
    /// <param name="voxels">Packed material volume.</param>
    /// <param name="startX">Grid-local start X.</param><param name="startY">Start Y.</param><param name="startZ">Start Z.</param>
    /// <param name="endX">Grid-local emitter X.</param><param name="endY">Emitter Y.</param><param name="endZ">Emitter Z.</param>
    /// <returns>Whether no intermediate opaque voxel blocks the segment.</returns>
    private static bool TraceIrradianceVisibility(
        byte[] voxels,
        float startX,
        float startY,
        float startZ,
        float endX,
        float endY,
        float endZ)
    {
        float deltaX = endX - startX;
        float deltaY = endY - startY;
        float deltaZ = endZ - startZ;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
        int stepCount = Math.Clamp((int)MathF.Ceiling(distance * 2.0f), 1, 64);
        int endCellX = (int)MathF.Floor(endX);
        int endCellY = (int)MathF.Floor(endY);
        int endCellZ = (int)MathF.Floor(endZ);
        for (int step = 1; step < stepCount; step++)
        {
            float progress = step / (float)stepCount;
            int x = (int)MathF.Floor(startX + deltaX * progress);
            int y = (int)MathF.Floor(startY + deltaY * progress);
            int z = (int)MathF.Floor(startZ + deltaZ * progress);
            if ((uint)x >= Width || (uint)y >= Height || (uint)z >= Depth)
            {
                return false;
            }

            // The endpoint can be the lantern's own block. Its detailed cage
            // remains the responsibility of the per-pixel direct-light trace;
            // it must not self-occlude every low-frequency bounce seed.
            if (x == endCellX && y == endCellY && z == endCellZ)
            {
                continue;
            }

            if (IsIrradianceObstacle(voxels, CellIndex(x, y, z)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Evaluates clamped cubic Hermite interpolation between two edges.</summary>
    /// <param name="edge0">Lower edge.</param><param name="edge1">Upper edge.</param><param name="value">Sample.</param>
    /// <returns>Smoothed fraction in 0..1.</returns>
    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float amount = Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 0.00001f), 0.0f, 1.0f);
        return amount * amount * (3.0f - 2.0f * amount);
    }

    /// <summary>Accumulates maximum/sum/count for one valid air neighbor and one RGB channel.</summary>
    /// <param name="values">Current irradiance field.</param><param name="voxels">Material volume.</param>
    /// <param name="x">Neighbor X.</param><param name="y">Neighbor Y.</param><param name="z">Neighbor Z.</param>
    /// <param name="channel">RGB channel 0..2.</param><param name="maximum">Running dominant value.</param>
    /// <param name="sum">Running sum.</param><param name="count">Running valid-neighbor count.</param>
    private static void AccumulateIrradianceNeighbor(
        float[] values,
        byte[] voxels,
        int x,
        int y,
        int z,
        int channel,
        ref float maximum,
        ref float sum,
        ref int count)
    {
        if ((uint)x >= Width || (uint)y >= Height || (uint)z >= Depth)
        {
            return;
        }

        int cellIndex = CellIndex(x, y, z);
        if (IsIrradianceObstacle(voxels, cellIndex))
        {
            return;
        }

        float value = values[cellIndex * IrradianceChannels + channel];
        maximum = Math.Max(maximum, value);
        sum += value;
        count++;
    }

    /// <summary>Classifies packed material classes 128/192 as opaque and class 64 as transmissive.</summary>
    /// <param name="voxels">Packed material volume.</param><param name="cellIndex">Flat voxel index.</param>
    /// <returns>Whether low-frequency transport must stop.</returns>
    private static bool IsIrradianceObstacle(byte[] voxels, int cellIndex)
    {
        // Fluids/glass (class 64) transmit the cache; ordinary and metallic
        // blocks (128/192) stop propagation, so thin walls do not leak light.
        return voxels[cellIndex * BytesPerVoxel + 3] >= 96;
    }

    /// <summary>Converts main-grid coordinates to X-fastest flat storage.</summary>
    /// <param name="x">Grid X.</param><param name="y">Grid Y.</param><param name="z">Grid Z.</param>
    /// <returns>Flat voxel index.</returns>
    private static int CellIndex(int x, int y, int z)
    {
        return (z * Height + y) * Width + x;
    }

    /// <summary>Approximates sRGB-to-linear conversion with the renderer's calibrated gamma 2.2 curve.</summary>
    /// <param name="value">sRGB channel in 0..1.</param><returns>Linear channel.</returns>
    private static float SrgbToLinear(float value)
    {
        return MathF.Pow(Math.Clamp(value, 0.0f, 1.0f), 2.2f);
    }

    /// <summary>Queries dynamic block emission conservatively during a world-change transition.</summary>
    /// <param name="block">Candidate old/new block.</param><param name="position">World position.</param>
    /// <returns>Whether it emits, or true when a modded query fails and a rebuild is safest.</returns>
    private bool BlockEmitsLight(Block? block, BlockPos position)
    {
        if (block is null || block.Id == 0)
        {
            return false;
        }

        try
        {
            byte[] lightHsv = block.GetLightHsv(api.World.BlockAccessor, position, null);
            return lightHsv is { Length: >= 3 } && lightHsv[2] > 0;
        }
        catch
        {
            // A modded block may require a block entity which is between states
            // during BlockChanged. Rebuild conservatively in that rare case.
            return true;
        }
    }

    /// <summary>Invalidates instance cache and schedules incremental or full rebuilds for world edits.</summary>
    /// <param name="position">Changed block position.</param><param name="oldBlock">Previous solid block.</param>
    private void OnBlockChanged(BlockPos position, Block oldBlock)
    {
        instanceMeshOccupancyByPosition.Remove(
            (position.X, position.Y, position.Z, position.dimension));
        (bool insideMainVolume, bool insideSunVolume) = ClassifyDirtyBlockVolumes(
            position.X,
            position.Y,
            position.Z,
            originX,
            originY,
            originZ,
            sunOriginX,
            sunOriginY,
            sunOriginZ);
        bool insideFluidSurface = position.X >= fluidSurfaceOriginX
            && position.X < fluidSurfaceOriginX + FluidSurfaceWidth
            && position.Y >= originY
            && position.Y < originY + Height
            && position.Z >= fluidSurfaceOriginZ
            && position.Z < fluidSurfaceOriginZ + FluidSurfaceDepth;
        if (!insideMainVolume && !insideFluidSurface && !insideSunVolume)
        {
            return;
        }

        if (insideMainVolume)
        {
            Block? currentSolid = api.World.BlockAccessor.GetBlock(
                position,
                BlockLayersAccess.Solid);
            Block? currentFluid = api.World.BlockAccessor.GetBlock(
                position,
                BlockLayersAccess.Fluid);
            if (BlockEmitsLight(oldBlock, position)
                || BlockEmitsLight(currentSolid, position)
                || (!ReferenceEquals(currentFluid, currentSolid)
                    && BlockEmitsLight(currentFluid, position)))
            {
                rebuildRequested = true;
                dirtyBlocks.Clear();
                return;
            }
        }

        dirtyBlocks.Add((position.X, position.Y, position.Z));
    }

    /// <summary>Classifies one world edit independently against fine and long-range clipmap volumes.</summary>
    /// <param name="worldX">Edit X.</param><param name="worldY">Edit Y.</param><param name="worldZ">Edit Z.</param>
    /// <param name="mainOriginX">Fine origin X.</param><param name="mainOriginY">Fine origin Y.</param><param name="mainOriginZ">Fine origin Z.</param>
    /// <param name="clipmapOriginX">Sun origin X.</param><param name="clipmapOriginY">Sun origin Y.</param><param name="clipmapOriginZ">Sun origin Z.</param>
    /// <returns>Containment flags for both volumes.</returns>
    internal static (bool FineOccupancy, bool SunOccupancy) ClassifyDirtyBlockVolumes(
        int worldX,
        int worldY,
        int worldZ,
        int mainOriginX,
        int mainOriginY,
        int mainOriginZ,
        int clipmapOriginX,
        int clipmapOriginY,
        int clipmapOriginZ)
    {
        bool fine = worldX >= mainOriginX && worldX < mainOriginX + Width
            && worldY >= mainOriginY && worldY < mainOriginY + Height
            && worldZ >= mainOriginZ && worldZ < mainOriginZ + Depth;
        bool sun = worldX >= clipmapOriginX && worldX < clipmapOriginX + SunWorldWidth
            && worldY >= clipmapOriginY && worldY < clipmapOriginY + SunWorldHeight
            && worldZ >= clipmapOriginZ && worldZ < clipmapOriginZ + SunWorldDepth;
        return (fine, sun);
    }

    /// <summary>Unregisters tick/block observers and releases retained instance cache exactly once.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        api.Event.BlockChanged -= OnBlockChanged;
        instanceMeshOccupancyByPosition.Clear();
        if (tickListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }
    }
}

/// <summary>Type-static 4-cubed occupancy plus mesh provenance and triangle diagnostics.</summary>
internal readonly record struct CachedBlockOccupancy(
    ulong Mask,
    bool HasDetailedMesh,
    int OpaqueTriangles,
    int TransparentTriangles,
    int AlphaTestedTriangles,
    BlockGeometryKind GeometryKind);

/// <summary>Geometry strategy chosen before per-instance occupancy work.</summary>
internal enum BlockGeometryKind
{
    /// <summary>No authoritative mesh classification was available.</summary>
    Unknown,
    /// <summary>Canonical unit cube eligible for full-mask fast path.</summary>
    FullCubeStatic,
    /// <summary>Reusable alpha/render-pass-aware non-cubic static mesh.</summary>
    StaticComplex,
    /// <summary>Block entity or chiseled shape requiring position-specific tessellation.</summary>
    DynamicInstance
}

/// <summary>Detailed 16-cubed caster mask and block-local emission origin.</summary>
internal readonly record struct CachedLightCaster(
    byte[] Mask,
    Vec3f EmissionOffset);

/// <summary>Captured instance mesh topology, bounds, transparency, and normalization diagnostics.</summary>
internal readonly record struct InstanceMeshDiagnostics(
    int VertexCount,
    int IndexCount,
    int OpaqueTriangles,
    int TransparentTriangles,
    int InvalidTriangles,
    string RawBounds,
    string LocalBounds,
    string Reason);

/// <summary>Immutable axis-aligned bounds accumulated from captured mesh vertices.</summary>
internal readonly record struct MeshBounds(
    float MinimumX,
    float MinimumY,
    float MinimumZ,
    float MaximumX,
    float MaximumY,
    float MaximumZ)
{
    /// <summary>Gets the neutral infinity bounds used before any vertex is included.</summary>
    public static MeshBounds Empty => new(
        float.PositiveInfinity,
        float.PositiveInfinity,
        float.PositiveInfinity,
        float.NegativeInfinity,
        float.NegativeInfinity,
        float.NegativeInfinity);

    /// <summary>Gets whether no finite vertex has been included.</summary>
    public bool IsEmpty => !float.IsFinite(MinimumX) || !float.IsFinite(MaximumX);

    /// <summary>Gets whether bounds intersect the block-local unit cube.</summary>
    public bool OverlapsUnitBlock => !IsEmpty
        && MaximumX >= 0.0f && MinimumX <= 1.0f
        && MaximumY >= 0.0f && MinimumY <= 1.0f
        && MaximumZ >= 0.0f && MinimumZ <= 1.0f;

    /// <summary>Returns bounds expanded over all valid vertices of one mesh.</summary>
    /// <param name="mesh">Captured mesh.</param><returns>Expanded immutable bounds.</returns>
    public MeshBounds Include(MeshData mesh)
    {
        if (mesh.xyz is not { Length: >= 3 } || mesh.VerticesCount <= 0)
        {
            return this;
        }

        float minimumX = MinimumX;
        float minimumY = MinimumY;
        float minimumZ = MinimumZ;
        float maximumX = MaximumX;
        float maximumY = MaximumY;
        float maximumZ = MaximumZ;
        int vertexCount = Math.Min(mesh.VerticesCount, mesh.xyz.Length / 3);
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            int offset = vertexIndex * 3;
            minimumX = Math.Min(minimumX, mesh.xyz[offset]);
            minimumY = Math.Min(minimumY, mesh.xyz[offset + 1]);
            minimumZ = Math.Min(minimumZ, mesh.xyz[offset + 2]);
            maximumX = Math.Max(maximumX, mesh.xyz[offset]);
            maximumY = Math.Max(maximumY, mesh.xyz[offset + 1]);
            maximumZ = Math.Max(maximumZ, mesh.xyz[offset + 2]);
        }
        return new MeshBounds(
            minimumX,
            minimumY,
            minimumZ,
            maximumX,
            maximumY,
            maximumZ);
    }

    /// <summary>Formats invariant compact min/max coordinates for runtime diagnostics.</summary>
    /// <returns><c>empty</c> or a bounded coordinate interval.</returns>
    public override string ToString()
    {
        return IsEmpty
            ? "empty"
            : FormattableString.Invariant(
                $"[{MinimumX:0.###},{MinimumY:0.###},{MinimumZ:0.###}]-[{MaximumX:0.###},{MaximumY:0.###},{MaximumZ:0.###}]");
    }
}

/// <summary>Per-position block-entity tessellation mask and replace/default semantics.</summary>
internal readonly record struct InstanceMeshOccupancy(
    BlockEntity BlockEntity,
    int BlockId,
    ulong Mask,
    bool SkipsDefaultMesh,
    int CapturedMeshCount);

/// <summary>Non-reusing terrain mesh sink that clones every block-entity tessellation submission.</summary>
internal sealed class InstanceTerrainMeshCollector : ITerrainMeshPool
{
    private readonly List<MeshData> meshes = new();

    /// <summary>Gets independent captured meshes in submission order.</summary>
    public IReadOnlyList<MeshData> Meshes => meshes;

    /// <summary>Clones and captures an untransformed mesh.</summary>
    /// <param name="data">Engine-owned mesh.</param><param name="lodLevel">LOD metadata; geometry is captured as submitted.</param>
    public void AddMeshData(MeshData data, int lodLevel = 1)
    {
        if (data is not null)
        {
            // OnTesselation explicitly forbids retaining/reusing its MeshData.
            meshes.Add(VoxelScene.CloneInstanceMesh(data));
        }
    }

    /// <summary>Clones, applies the submitted transform matrix, and captures a mesh.</summary>
    /// <param name="data">Engine-owned mesh.</param><param name="tfMatrix">Transform applied to the clone.</param><param name="lodLevel">LOD metadata.</param>
    public void AddMeshData(MeshData data, float[] tfMatrix, int lodLevel = 1)
    {
        if (data is not null)
        {
            MeshData transformed = VoxelScene.CloneInstanceMesh(data);
            transformed.MatrixTransform(tfMatrix);
            meshes.Add(transformed);
        }
    }

    /// <summary>Captures geometry while intentionally ignoring color-map metadata irrelevant to shadows.</summary>
    /// <param name="data">Engine-owned mesh.</param><param name="colorMapData">Unused color metadata.</param><param name="lodLevel">LOD metadata.</param>
    public void AddMeshData(MeshData data, ColorMapData colorMapData, int lodLevel = 1)
    {
        AddMeshData(data, lodLevel);
    }
}

/// <summary>Occupied sample counts across lower/middle/upper caster bands plus empty cells.</summary>
internal readonly record struct LightCasterBandCoverage(
    int LowerOccupied,
    int MiddleOccupied,
    int UpperOccupied,
    int Empty);

/// <summary>
/// Semantic proof derived from authored coarse geometry and the alpha-aware
/// fine caster used for local-light shadow traversal.
/// </summary>
internal readonly record struct CasterGeometryEvidence(
    int CoarseOccupied,
    bool DetailedNonCube,
    bool CrossedPlanePartial,
    LightCasterBandCoverage FineBands,
    bool BaseIncluded,
    bool CageHeightCovered,
    bool TransparentSurfaceIgnored);

/// <summary>World-space colored emitter with intensity, provenance code, and detailed self-caster mask.</summary>
internal readonly record struct VoxelLight(
    float X,
    float Y,
    float Z,
    float Red,
    float Green,
    float Blue,
    float Intensity,
    string Code,
    byte[] CasterMask)
{
    /// <summary>Scores selection by intensity over one plus squared camera distance.</summary>
    /// <param name="cameraPosition">World camera position.</param><returns>Relative bounded-selection importance.</returns>
    public double Score(Vec3d cameraPosition)
    {
        double dx = X - cameraPosition.X;
        double dy = Y - cameraPosition.Y;
        double dz = Z - cameraPosition.Z;
        return Intensity / (1.0 + dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>Complete full-upload contract for material, occupancy, radiance, liquids, sun, rain, and lights.</summary>
internal readonly record struct VoxelSceneSnapshot(
    byte[] Voxels,
    byte[] Occupancy,
    int Width,
    int Height,
    int Depth,
    int OriginX,
    int OriginY,
    int OriginZ,
    VoxelLight[] Lights,
    byte[] Irradiance,
    byte[] IrradianceDirection,
    byte[] FluidSurface,
    byte[] LiquidMetadata,
    float[] LiquidOpticalProfileLookup,
    int LiquidOpticalProfileCount,
    ulong[] SunOccupancy,
    float[] RainSurface,
    int SunOccupancyWidth,
    int SunOccupancyHeight,
    int SunOccupancyDepth,
    int SunOccupancyScale,
    int SunOriginX,
    int SunOriginY,
    int SunOriginZ,
    int RainSurfaceWidth,
    int RainSurfaceDepth,
    int FluidVoxelCount,
    int VisibleLiquidContainerCount,
    int Generation,
    int FluidSurfaceWidth,
    int FluidSurfaceDepth,
    int FluidSurfaceOriginX,
    int FluidSurfaceOriginZ);

/// <summary>Encoded irradiance RGB and dominant signed direction 3D textures.</summary>
internal readonly record struct VoxelRadianceField(
    byte[] Irradiance,
    byte[] Direction);

/// <summary>One main-grid partial update including material, 4-cubed occupancy, and liquid metadata.</summary>
internal readonly record struct VoxelSceneBlockUpdate(
    int LocalX,
    int LocalY,
    int LocalZ,
    byte[] Material,
    byte[] Occupancy,
    byte[] LiquidMetadata,
    byte[] FluidSurface);

/// <summary>One independent static liquid-surface column partial update.</summary>
internal readonly record struct VoxelFluidSurfaceUpdate(
    int LocalX,
    int LocalZ,
    byte[] FluidSurface);

/// <summary>One coarse sun occupancy cell partial update.</summary>
internal readonly record struct VoxelSunOccupancyUpdate(
    int LocalX,
    int LocalY,
    int LocalZ,
    ulong Occupancy);

/// <summary>One rain-height column partial update, with height in world blocks.</summary>
internal readonly record struct VoxelRainSurfaceUpdate(
    int LocalX,
    int LocalZ,
    float Height);

/// <summary>Atlas UV coordinate pair.</summary>
internal readonly record struct TextureUv(float U, float V);

/// <summary>Canonical source texture associated with one atlas rectangle.</summary>
internal sealed record TextureAlphaCandidate(
    TextureAtlasPosition Position,
    AssetLocation Source);

/// <summary>Decoded top-left row-major source alpha bytes and dimensions.</summary>
internal sealed record TextureAlphaData(
    int Width,
    int Height,
    byte[] Alpha);

/// <summary>Maps atlas UVs into decoded source-alpha texels for cutout-aware occupancy.</summary>
internal sealed record TextureAlphaSampler(
    TextureAtlasPosition Position,
    TextureAlphaData Alpha)
{
    /// <summary>Samples nearest source alpha with a 128 cutoff; invalid rectangles remain conservatively opaque.</summary>
    /// <param name="atlasU">Atlas U.</param><param name="atlasV">Atlas V.</param>
    /// <returns>Whether source alpha is at least 128.</returns>
    public bool IsOpaque(float atlasU, float atlasV)
    {
        float width = Position.x2 - Position.x1;
        float height = Position.y2 - Position.y1;
        if (width <= 0.0f || height <= 0.0f)
        {
            return true;
        }

        float localU = Math.Clamp((atlasU - Position.x1) / width, 0.0f, 0.999999f);
        float localV = Math.Clamp((atlasV - Position.y1) / height, 0.0f, 0.999999f);
        int x = Math.Min((int)(localU * Alpha.Width), Alpha.Width - 1);
        int y = Math.Min((int)(localV * Alpha.Height), Alpha.Height - 1);
        return Alpha.Alpha[y * Alpha.Width + x] >= 128;
    }
}
