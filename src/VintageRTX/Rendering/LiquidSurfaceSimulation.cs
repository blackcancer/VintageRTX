using System.Runtime.CompilerServices;

namespace VintageRTX.Rendering;

/// <summary>Semantic source of a surface impulse, used to preserve distinct authored responses.</summary>
internal enum LiquidSurfaceImpulseKind : byte
{
    /// <summary>Ordinary object or entity crossing the free surface.</summary>
    GenericEntry,
    /// <summary>Repeated directional wake from a swimming entity near the surface.</summary>
    SwimmingWake,
    /// <summary>Fishing bobber entry or periodic surface motion.</summary>
    Bobber,
    /// <summary>Wake emitted by a fish moving within the configured surface band.</summary>
    NearSurfaceFish,
    /// <summary>Shallow high-horizontal-speed impact from a thrown stone.</summary>
    StoneRicochet,
    /// <summary>Small stochastic impact on a rain-exposed cell.</summary>
    Rain,
    /// <summary>Entry of a material-agnostic dropped inventory stack.</summary>
    DroppedItemEntry,
    /// <summary>Terminal impulse and transient emission from an authored bubble.</summary>
    BubbleBurst
}

/// <summary>Stable entity behavior classes consumed by transition/event detection.</summary>
internal enum LiquidEntitySurfaceClass : byte
{
    /// <summary>Default entry and swimming semantics.</summary>
    Generic,
    /// <summary>Dropped inventory stack using the shared stone-reference pseudo-mass model.</summary>
    DroppedItem,
    /// <summary>Fishing float with persistent near-surface wake behavior.</summary>
    Bobber,
    /// <summary>Creature eligible for near-surface fish wakes.</summary>
    Fish,
    /// <summary>Projectile eligible for shallow-angle ricochet events.</summary>
    ThrownStone,
    /// <summary>Arrow, spear, or modded projectile receiving one liquid-entry impulse.</summary>
    Projectile
}

/// <summary>
/// Detached per-entity observation. Positions use world blocks (one metre by renderer convention),
/// motion uses blocks per <see cref="MotionSamplePeriodSeconds"/>, and mass uses kilograms.
/// <see cref="EntityId"/> provides continuity between successive observations.
/// </summary>
internal readonly record struct LiquidEntitySurfaceSample(
    long EntityId,
    LiquidEntitySurfaceClass SurfaceClass,
    double WorldX,
    double WorldY,
    double WorldZ,
    float MotionX,
    float MotionY,
    float MotionZ,
    bool FeetInLiquid,
    bool Swimming,
    float MassKilograms = 25.0f,
    float MotionSamplePeriodSeconds = 1.0f);

/// <summary>
/// Exact engine liquid-collision observation for a projectile. Unlike the periodic entity sample,
/// this record is emitted at the physics callback and therefore cannot miss a short ricochet that
/// begins and ends between two 30 Hz world queries. Integrated-server samples are tagged so their
/// authoritative motion can precede the client's remote-position reconstruction.
/// </summary>
internal readonly record struct LiquidProjectileCollisionSample(
    long EntityId,
    LiquidEntitySurfaceClass SurfaceClass,
    double WorldX,
    double WorldY,
    double WorldZ,
    double PreviousWorldX,
    double PreviousWorldY,
    double PreviousWorldZ,
    float IncidentMotionX,
    float IncidentMotionY,
    float IncidentMotionZ,
    float OutgoingMotionX,
    float OutgoingMotionY,
    float OutgoingMotionZ,
    float MassKilograms,
    float MotionSamplePeriodSeconds,
    bool IsServerAuthoritative = false);

/// <summary>
/// Exact evidence for the most recently accepted projectile/free-surface contact, expressed in SI
/// after endpoint-to-surface reconstruction and before the fixed-step impulse is applied.
/// </summary>
internal readonly record struct LiquidProjectileImpactDiagnostic(
    int Sequence,
    long EntityId,
    LiquidEntitySurfaceClass SurfaceClass,
    LiquidSurfaceImpulseKind ImpulseKind,
    double WorldX,
    double WorldZ,
    int CellX,
    int CellZ,
    float SupportRadiusWorldBlocks,
    float MassKilograms,
    float EnergyJoules,
    float IncidentVelocityXMetresPerSecond,
    float IncidentVelocityYMetresPerSecond,
    float IncidentVelocityZMetresPerSecond,
    float OutgoingVelocityXMetresPerSecond,
    float OutgoingVelocityYMetresPerSecond,
    float OutgoingVelocityZMetresPerSecond,
    bool IsServerAuthoritative = false,
    float SurfaceCoupledEnergyJoules = 0.0f,
    float ResolvedWaveEnergyJoules = 0.0f,
    float SubgridWaveEnergyJoules = 0.0f,
    float NearInterfaceEnergyJoules = 0.0f,
    float CavityEnergyJoules = 0.0f,
    float CapillaryPacketEnergyJoules = 0.0f,
    float SplashEnergyJoules = 0.0f,
    float WakeEnergyJoules = 0.0f);

/// <summary>
/// Conservative energy ledger for an exact projectile/free-surface contact. The legacy surface
/// term is the hydrodynamic-column ceiling; near-interface, capillary, cavity, splash, and wake
/// fields identify which part can actually reach visible surface geometry.
/// </summary>
internal readonly record struct LiquidProjectileWaveEnergyPartition(
    float SurfaceCoupledEnergyJoules,
    float ResolvedWaveEnergyJoules,
    float SubgridWaveEnergyJoules,
    float CavityRadiusMetres,
    float ResolvedFraction,
    float NearInterfaceEnergyJoules,
    float CavityEnergyJoules,
    float CapillaryPacketEnergyJoules,
    float SplashEnergyJoules,
    float WakeEnergyJoules);

/// <summary>
/// Environmental forcing in SI: horizontal wind velocity in m/s and vertical rainfall depth rate
/// in m/s. The zero value represents dry, still air.
/// </summary>
internal readonly record struct LiquidSurfaceForcing(
    float WindX,
    float WindZ,
    float Rainfall)
{
    /// <summary>Horizontal wind X in metres per second.</summary>
    public float WindVelocityXMetresPerSecond => WindX;

    /// <summary>Horizontal wind Z in metres per second.</summary>
    public float WindVelocityZMetresPerSecond => WindZ;

    /// <summary>Vertical rainfall depth flux in metres per second.</summary>
    public float RainfallRateMetresPerSecond => Rainfall;
}

/// <summary>
/// Physical bulk/interface properties consumed by the free-surface solver. Density uses kg/m^3,
/// dynamic viscosity uses Pa*s, surface tension uses N/m, and additional unresolved damping uses
/// s^-1. The latter represents losses outside the linear clean-interface viscosity model.
/// </summary>
internal readonly record struct LiquidSurfacePhysicalProperties(
    float DensityKilogramsPerCubicMetre,
    float DynamicViscosityPascalSeconds,
    float SurfaceTensionNewtonsPerMetre,
    float AdditionalDampingPerSecond = 0.0f,
    float ResolvedWaveEnergyFraction = 1.0f)
{
    /// <summary>Validates every SI material scalar before it enters persistent cell storage.</summary>
    public void Validate()
    {
        if (!float.IsFinite(DensityKilogramsPerCubicMetre)
            || DensityKilogramsPerCubicMetre <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(DensityKilogramsPerCubicMetre));
        }
        if (!float.IsFinite(DynamicViscosityPascalSeconds)
            || DynamicViscosityPascalSeconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(DynamicViscosityPascalSeconds));
        }
        if (!float.IsFinite(SurfaceTensionNewtonsPerMetre)
            || SurfaceTensionNewtonsPerMetre < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(SurfaceTensionNewtonsPerMetre));
        }
        if (!float.IsFinite(AdditionalDampingPerSecond)
            || AdditionalDampingPerSecond < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(AdditionalDampingPerSecond));
        }
        if (!float.IsFinite(ResolvedWaveEnergyFraction)
            || ResolvedWaveEnergyFraction < 0.0f
            || ResolvedWaveEnergyFraction > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(ResolvedWaveEnergyFraction));
        }
    }

    /// <summary>
    /// Adapts the original dynamics-only call to SI without the old hidden 60 FPS multiplier.
    /// Density defaults to water; legacy damping is interpreted explicitly as s^-1 and the
    /// legacy surface-tension scalar as N/m.
    /// </summary>
    /// <param name="dynamics">Existing authored surface dynamics.</param>
    /// <returns>Finite SI properties suitable for compatibility callers.</returns>
    internal static LiquidSurfacePhysicalProperties FromLegacy(
        in LiquidSurfaceDynamics dynamics)
    {
        return new LiquidSurfacePhysicalProperties(
            1_000.0f,
            0.0f,
            Math.Max(0.0f, dynamics.SurfaceTension),
            Math.Max(0.0f, dynamics.Damping),
            1.0f);
    }
}

/// <summary>
/// CPU diagnostic sample for one cell. Height is signed metres/world blocks, velocity is m/s,
/// normal X/Z are unit-vector components, and transient emission is linear intensity.
/// </summary>
internal readonly record struct LiquidSurfaceCellSample(
    float Height,
    float Velocity,
    float NormalX,
    float NormalZ,
    float TransientEmission);

/// <summary>
/// End-to-end diagnostic for the most recently applied dropped-item entry. Texture coordinates
/// identify the exact RGBA32F texel neighborhood sampled by the display shader. The two horizontal
/// direction components plus <c>ImpactVelocityYMetresPerSecond</c> form the exact velocity vector
/// whose kinetic energy was queued.
/// </summary>
internal readonly record struct LiquidSurfaceImpactDiagnostic(
    int Sequence,
    double WorldX,
    double WorldZ,
    int CellX,
    int CellZ,
    float TextureU,
    float TextureV,
    float PeakDisplacement,
    float UploadedHeight,
    float UploadedNormalX,
    float UploadedNormalZ,
    float SubgridPeakDisplacement,
    float EnergyJoules,
    float DirectionXMetresPerSecond,
    float DirectionZMetresPerSecond,
    float ImpactVelocityYMetresPerSecond,
    float DensityKilogramsPerCubicMetre,
    float DynamicViscosityPascalSeconds,
    float SurfaceTensionNewtonsPerMetre,
    float AdditionalDampingPerSecond,
    float DominantWavelengthMetres);

/// <summary>
/// Generic sub-grid impact emitted by dropped items and exact projectiles. Its global sequence is
/// shared by every source class, while entity/class identity prevents collisions between unrelated
/// impacts. The ledger distinguishes rendered capillary/splash energy from non-rendered wake.
/// </summary>
internal readonly record struct LiquidSurfaceSubgridImpactDiagnostic(
    int Sequence,
    int SourceSequence,
    long EntityId,
    LiquidEntitySurfaceClass SurfaceClass,
    LiquidSurfaceImpulseKind ImpulseKind,
    double WorldX,
    double WorldZ,
    int CellX,
    int CellZ,
    float TextureU,
    float TextureV,
    float PeakDisplacement,
    float SplashPeakDisplacement,
    float SplashRadiusWorldBlocks,
    float SplashReleaseSeconds,
    float UploadedHeight,
    float UploadedNormalX,
    float UploadedNormalZ,
    float SurfaceCoupledEnergyJoules,
    float ResolvedWaveEnergyJoules,
    float SubgridWaveEnergyJoules,
    float RenderedPacketEnergyJoules,
    float LocalSplashEnergyJoules,
    float WakeEnergyJoules,
    float DirectionXMetresPerSecond,
    float DirectionZMetresPerSecond,
    float ImpactVelocityYMetresPerSecond,
    float DensityKilogramsPerCubicMetre,
    float DynamicViscosityPascalSeconds,
    float SurfaceTensionNewtonsPerMetre,
    float AdditionalDampingPerSecond,
    float DominantWavelengthMetres);

/// <summary>
/// Compact procedural bubble primitive for a GPU uniform/SSBO. Local X/Z are
/// measured from the simulation origin in blocks. The renderer evaluates a
/// radial smoothstep from Radius and Growth, preserving a round sub-cell shape
/// even when the height texture itself is sampled at half-block resolution.
/// </summary>
internal readonly record struct LiquidSurfaceBubbleSample(
    float LocalX,
    float LocalZ,
    float Radius,
    float Growth,
    float Emission)
{
    /// <summary>Evaluates the smooth radial surface bulge at a clipmap-local position.</summary>
    /// <param name="localX">X offset from simulation origin in blocks.</param>
    /// <param name="localZ">Z offset from simulation origin in blocks.</param>
    /// <returns>Non-negative bubble height contribution in blocks.</returns>
    public float EvaluateHeight(float localX, float localZ)
    {
        return Radius * Growth * EvaluateRadialWeight(localX, localZ);
    }

    /// <summary>Evaluates localized linear burst emission at a clipmap-local position.</summary>
    /// <param name="localX">X offset from simulation origin in blocks.</param>
    /// <param name="localZ">Z offset from simulation origin in blocks.</param>
    /// <returns>Non-negative emission contribution.</returns>
    public float EvaluateEmission(float localX, float localZ)
    {
        return Emission * EvaluateRadialWeight(localX, localZ);
    }

    /// <summary>Computes a clamped cubic smoothstep falloff from center to authored radius.</summary>
    /// <param name="localX">X offset from simulation origin in blocks.</param>
    /// <param name="localZ">Z offset from simulation origin in blocks.</param>
    /// <returns>Radial weight in 0..1.</returns>
    private float EvaluateRadialWeight(float localX, float localZ)
    {
        float dx = localX - LocalX;
        float dz = localZ - LocalZ;
        float normalizedRadius = Math.Clamp(
            MathF.Sqrt(dx * dx + dz * dz) / Math.Max(Radius, float.Epsilon),
            0.0f,
            1.0f);
        float smoothRadius = normalizedRadius * normalizedRadius
            * (3.0f - 2.0f * normalizedRadius);
        return 1.0f - smoothRadius;
    }
}

/// <summary>
/// Bounded, deterministic height-field simulation for visible liquid surfaces.
/// One RGBA32F texel is produced per cell: signed height, normal X, normal Z,
/// and transient bubble emission. All liquid-dependent behaviour comes from the
/// asset-authored LiquidSurfaceDynamics associated with each registry profile.
/// </summary>
/// <remarks>
/// The height equation is a narrow-band envelope surrogate: its dominant wavelength follows the
/// finite-depth dispersion relation omega^2=(g*k+(sigma/rho)*k^3)*tanh(k*h), its localized impact
/// envelope travels at d(omega)/dk, and its clean-interface amplitude decays at 2*nu*k^2.
/// Highly viscous profiles retain the same inviscid dispersion term but are rapidly attenuated;
/// this is a bounded linear-wave approximation rather than a full Navier-Stokes solve. One world
/// block equals one metre by renderer calibration.
/// </remarks>
internal sealed partial class LiquidSurfaceSimulation
{
    /// <summary>Deterministic solver step in seconds; decouples propagation from render FPS.</summary>
    internal const float FixedStepSeconds = 1.0f / 120.0f;
    /// <summary>RGBA32F channels written per cell: height, normal X, normal Z, emission.</summary>
    internal const int GpuChannels = 4;
    /// <summary>Recommended horizontal sampling density for sub-block ripples.</summary>
    internal const int RecommendedCellsPerBlock = 2;
    /// <summary>Recommended cell edge length in world blocks.</summary>
    internal const float RecommendedCellSize = 1.0f / RecommendedCellsPerBlock;
    /// <summary>Recommended square clipmap reach in world blocks.</summary>
    internal const int RecommendedClipmapWorldSize = 64;
    /// <summary>Recommended texture resolution derived from reach and sampling density.</summary>
    internal const int RecommendedClipmapResolution =
        RecommendedClipmapWorldSize * RecommendedCellsPerBlock;

    // bubbleRate is an areal density authored as expected bursts per second
    // per square world block. It therefore remains stable when the same 64x64
    // clipmap changes from 64x64 to the recommended 128x128 cells.
    /// <summary>Reference square-block area used by authored bubble rates.</summary>
    internal const float BubbleRateUnitArea = 1.0f;

    /// <summary>Maximum render-frame time accepted into the accumulator, in seconds.</summary>
    private const float MaximumFrameSeconds = 0.10f;
    /// <summary>Maximum fixed steps per render call, preventing spiral-of-death stalls.</summary>
    private const int MaximumStepsPerAdvance = 12;
    /// <summary>Courant-Friedrichs-Lewy safety fraction for the 2D explicit wave solve.</summary>
    private const float CflSafety = 0.68f;
    /// <summary>Dry-air density at 15 C and sea-level reference pressure, in kg/m^3.</summary>
    private const float ReferenceAirDensityKilogramsPerCubicMetre = 1.225f;
    /// <summary>Representative resolved raindrop diameter in metres.</summary>
    private const float RepresentativeRainDropDiameterMetres = 0.002f;
    /// <summary>Representative 2 mm raindrop terminal velocity in m/s.</summary>
    private const float RepresentativeRainDropVelocityMetresPerSecond = 6.5f;
    /// <summary>Representative rain-water density in kg/m^3.</summary>
    private const float RainWaterDensityKilogramsPerCubicMetre = 998.2f;
    /// <summary>Representative axial arrow shaft diameter used by the entry-drag closure, in metres.</summary>
    private const float ReferenceArrowShaftDiameterMetres = 0.008f;
    /// <summary>Central bounded axial drag coefficient for an arrow entering water point-first.</summary>
    private const float ReferenceArrowAxialDragCoefficient = 0.40f;
    /// <summary>Representative granite density used to recover thrown-stone diameter, in kg/m^3.</summary>
    private const float ReferenceGraniteDensityKilogramsPerCubicMetre = 2_700.0f;
    /// <summary>Maximum neighboring surface-height separation, in cell sizes, that still propagates waves.</summary>
    private const float SurfaceConnectionTolerance = 0.55f;
    /// <summary>Motion magnitude below which direction and event movement are treated as zero.</summary>
    private const float MinimumMotion = 0.001f;
    /// <summary>
    /// Minimum measured speed for recovering an item first observed inside the surface band. This
    /// rejects old floating/resting world items while retaining a newly spawned fast crossing.
    /// </summary>
    private const float MinimumInitiallyObservedEntrySpeedMetresPerSecond = 0.50f;
    /// <summary>Bounded open-address probes used for entity tracking.</summary>
    private const int TrackerProbeLimit = 8;
    /// <summary>Fixed simulation steps between repeated near-surface wake impulses.</summary>
    private const int WakeStepInterval = 6;
    /// <summary>Hard per-step bubble spawn cap protecting CPU work during high authored rates.</summary>
    internal const int MaximumBubbleSpawnsPerStep = 4;
    /// <summary>Maximum spatial rain representatives per step; their energy is aggregated, not discarded.</summary>
    internal const int MaximumRainImpulsesPerStep = 32;
    /// <summary>Recent packet history retained so several impacts in one runtime update remain observable.</summary>
    internal const int SubgridImpactHistoryCapacity = 16;
    /// <summary>Burst/emission lifetime as a fraction of bubble rise duration.</summary>
    private const float BubbleBurstDecayFraction = 0.10f;

    private float[] heights;
    private float[] velocities;
    private float[] nextHeights;
    private float[] nextVelocities;
    private readonly float[] transientEmission;
    private readonly float[] bubbleBulge;
    private readonly float[] surfaceWorldY;
    private readonly float[] depthsMetres;
    private readonly float[] normalX;
    private readonly float[] normalZ;
    private readonly LiquidSurfaceDynamics[] dynamics;
    private readonly LiquidSurfacePhysicalProperties[] physicalProperties;
    private readonly CellStepCoefficients[] cachedStepCoefficients;
    private readonly CellWindCoefficients[] cachedWindCoefficients;
    private readonly float[] cachedWindAmplitudes;
    private readonly float[] cachedWindSpatialPhases;
    private readonly float[] cachedWindSpreadPhasesA;
    private readonly float[] cachedWindSpreadPhasesB;
    private readonly byte[] profileIds;
    private readonly byte[] cellFlags;
    private readonly PendingImpulse[] pendingImpulses;
    private readonly BubbleState[] bubbles;
    private readonly EntityTracker[] entityTrackers;
    private readonly LiquidSurfaceSubgridImpactDiagnostic[] subgridImpactHistory;
    private readonly float[] bubbleAreaByProfile = new float[LiquidOpticalRegistry.LookupHeight];
    private readonly int[] bubbleCellCountByProfile = new int[LiquidOpticalRegistry.LookupHeight];

    private int pendingImpulseCount;
    private int subgridImpactHistoryCount;
    private int nextSubgridImpactHistoryIndex;
    private int activeBubbleCount;
    private int stepIndex;
    private float accumulator;
    private float rainDropAccumulator;
    private uint randomState;
    private float cachedWindX = float.NaN;
    private float cachedWindZ = float.NaN;
    private bool windForcingCacheDirty = true;
    private int lastSubgridImpactCellIndex = -1;
    private int lastSubgridImpactSequence;
    private int lastSubgridImpactSourceSequence;
    private long lastSubgridImpactEntityId;
    private LiquidEntitySurfaceClass lastSubgridImpactSurfaceClass;
    private LiquidSurfaceImpulseKind lastSubgridImpactKind;
    private double lastSubgridImpactWorldX;
    private double lastSubgridImpactWorldZ;
    private float lastSubgridImpactPeakDisplacement;
    private float lastSubgridImpactSplashPeakDisplacement;
    private float lastSubgridImpactSplashRadiusWorldBlocks;
    private float lastSubgridImpactSplashReleaseSeconds;
    private float lastSubgridImpactIncidentEnergyJoules;
    private float lastSubgridImpactSurfaceEnergyJoules;
    private float lastSubgridImpactResolvedEnergyJoules;
    private float lastSubgridImpactEnergyJoules;
    private float lastSubgridImpactRenderedEnergyJoules;
    private float lastSubgridImpactLocalSplashEnergyJoules;
    private float lastSubgridImpactWakeEnergyJoules;
    private float lastSubgridImpactDirectionX;
    private float lastSubgridImpactDirectionZ;
    private float lastSubgridImpactVelocityY;
    private float lastSubgridImpactDensity;
    private float lastSubgridImpactDynamicViscosity;
    private float lastSubgridImpactSurfaceTension;
    private float lastSubgridImpactAdditionalDamping;
    private float lastSubgridImpactWavelength;

    /// <summary>
    /// Allocates all bounded solver/event/tracker storage up front; no steady-state step allocates.
    /// The grid is X-major inside each Z row and covers width/depth times <paramref name="cellSize"/> blocks.
    /// </summary>
    /// <param name="originWorldX">Grid minimum X in world blocks.</param>
    /// <param name="originWorldZ">Grid minimum Z in world blocks.</param>
    /// <param name="width">Positive cell count along X.</param>
    /// <param name="depth">Positive cell count along Z.</param>
    /// <param name="cellSize">Positive cell edge length in world blocks.</param>
    /// <param name="eventBudget">Maximum queued impulses between solver steps.</param>
    /// <param name="bubbleBudget">Maximum simultaneous rising/bursting bubbles.</param>
    /// <param name="trackedEntityBudget">Fixed entity continuity table capacity.</param>
    /// <param name="deterministicSeed">Non-zero xorshift seed; zero selects the canonical seed.</param>
    public LiquidSurfaceSimulation(
        int originWorldX,
        int originWorldZ,
        int width,
        int depth,
        float cellSize = 1.0f,
        int eventBudget = 128,
        int bubbleBudget = 128,
        int trackedEntityBudget = 256,
        uint deterministicSeed = 0x9E3779B9u)
    {
        if (width <= 0 || depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!float.IsFinite(cellSize) || cellSize <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        if (eventBudget <= 0 || bubbleBudget <= 0 || trackedEntityBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventBudget));
        }

        OriginWorldX = originWorldX;
        OriginWorldZ = originWorldZ;
        Width = width;
        Depth = depth;
        CellSize = cellSize;
        int cellCount = checked(width * depth);
        heights = new float[cellCount];
        velocities = new float[cellCount];
        nextHeights = new float[cellCount];
        nextVelocities = new float[cellCount];
        transientEmission = new float[cellCount];
        bubbleBulge = new float[cellCount];
        surfaceWorldY = new float[cellCount];
        depthsMetres = new float[cellCount];
        normalX = new float[cellCount];
        normalZ = new float[cellCount];
        dynamics = new LiquidSurfaceDynamics[cellCount];
        physicalProperties = new LiquidSurfacePhysicalProperties[cellCount];
        cachedStepCoefficients = new CellStepCoefficients[cellCount];
        cachedWindCoefficients = new CellWindCoefficients[cellCount];
        cachedWindAmplitudes = new float[cellCount];
        cachedWindSpatialPhases = new float[cellCount];
        cachedWindSpreadPhasesA = new float[cellCount];
        cachedWindSpreadPhasesB = new float[cellCount];
        profileIds = new byte[cellCount];
        cellFlags = new byte[cellCount];
        pendingImpulses = new PendingImpulse[eventBudget];
        bubbles = new BubbleState[bubbleBudget];
        entityTrackers = new EntityTracker[trackedEntityBudget];
        subgridImpactHistory = new LiquidSurfaceSubgridImpactDiagnostic[
            SubgridImpactHistoryCapacity];
        randomState = deterministicSeed == 0 ? 0x9E3779B9u : deterministicSeed;
    }

    /// <summary>Gets grid minimum X in world blocks.</summary>
    public int OriginWorldX { get; }

    /// <summary>Gets grid minimum Z in world blocks.</summary>
    public int OriginWorldZ { get; }

    /// <summary>Gets horizontal cell count along X.</summary>
    public int Width { get; }

    /// <summary>Gets horizontal cell count along Z.</summary>
    public int Depth { get; }

    /// <summary>Gets cell edge length in world blocks.</summary>
    public float CellSize { get; }

    /// <summary>Gets impulses rejected because the fixed event queue was full.</summary>
    public int DroppedEventCount { get; private set; }

    /// <summary>Gets currently rising or decaying bubble primitives.</summary>
    public int ActiveBubbleCount => activeBubbleCount;

    /// <summary>Gets preallocated simultaneous-bubble capacity.</summary>
    public int BubbleBudget => bubbles.Length;

    /// <summary>Gets cumulative successful spawns for deterministic diagnostics.</summary>
    public int TotalBubbleSpawnCount { get; private set; }

    /// <summary>Gets cumulative impulses accepted by the bounded event queue.</summary>
    public int TotalImpulseCount { get; private set; }

    /// <summary>Gets cumulative dropped-item entries accepted by the solver.</summary>
    public int TotalDroppedItemImpactCount { get; private set; }

    /// <summary>Gets cumulative exact projectile contacts accepted by the solver.</summary>
    public int TotalProjectileImpactCount { get; private set; }

    /// <summary>Gets the collision-free sequence shared by every rendered sub-grid impact source.</summary>
    public int TotalSubgridImpactCount { get; private set; }

    /// <summary>Gets the peak displacement of the most recently applied impact, in world blocks.</summary>
    public float LastAppliedImpactPeakDisplacement { get; private set; }

    /// <summary>Gets the peak displacement of the most recently applied dropped-item entry.</summary>
    public float LastDroppedItemImpactPeakDisplacement { get; private set; }

    /// <summary>Gets CPU-to-GPU mapping evidence for the latest dropped-item entry.</summary>
    public LiquidSurfaceImpactDiagnostic LastDroppedItemImpactDiagnostic { get; private set; }

    /// <summary>Gets the latest generic dropped-item or projectile sub-grid packet.</summary>
    public LiquidSurfaceSubgridImpactDiagnostic LastSubgridImpactDiagnostic { get; private set; }

    /// <summary>
    /// Copies the newest globally sequenced packets after a consumed sequence in ascending order.
    /// Fixed history and caller storage keep this path allocation-free when several contacts occur
    /// in one runtime update.
    /// </summary>
    /// <param name="afterSequence">Last sequence already consumed by the renderer.</param>
    /// <param name="destination">Fixed caller storage receiving at most its own length.</param>
    /// <returns>Number of packets written.</returns>
    internal int WriteSubgridImpactsAfter(
        int afterSequence,
        Span<LiquidSurfaceSubgridImpactDiagnostic> destination)
    {
        if (destination.IsEmpty || subgridImpactHistoryCount == 0)
        {
            return 0;
        }

        int oldestIndex = (nextSubgridImpactHistoryIndex
            - subgridImpactHistoryCount
            + subgridImpactHistory.Length) % subgridImpactHistory.Length;
        int matchingCount = 0;
        for (int offset = 0; offset < subgridImpactHistoryCount; offset++)
        {
            LiquidSurfaceSubgridImpactDiagnostic candidate =
                subgridImpactHistory[(oldestIndex + offset) % subgridImpactHistory.Length];
            if (candidate.Sequence > afterSequence)
            {
                matchingCount++;
            }
        }

        int skipCount = Math.Max(0, matchingCount - destination.Length);
        int written = 0;
        for (int offset = 0; offset < subgridImpactHistoryCount; offset++)
        {
            LiquidSurfaceSubgridImpactDiagnostic candidate =
                subgridImpactHistory[(oldestIndex + offset) % subgridImpactHistory.Length];
            if (candidate.Sequence <= afterSequence)
            {
                continue;
            }
            if (skipCount > 0)
            {
                skipCount--;
                continue;
            }

            destination[written++] = candidate;
            if (written == destination.Length)
            {
                break;
            }
        }

        return written;
    }

    /// <summary>Gets exact SI evidence for the latest accepted projectile contact.</summary>
    public LiquidProjectileImpactDiagnostic LastProjectileImpactDiagnostic { get; private set; }

    /// <summary>Gets impulses waiting for the next fixed simulation step.</summary>
    public int PendingImpulseCount => pendingImpulseCount;

    /// <summary>
    /// Activates/configures one free-surface cell through the legacy dynamics-only signature.
    /// New callers should pass explicit <see cref="LiquidSurfacePhysicalProperties"/>.
    /// </summary>
    /// <param name="x">Zero-based grid X.</param>
    /// <param name="z">Zero-based grid Z.</param>
    /// <param name="worldSurfaceY">Undisturbed surface Y in world blocks.</param>
    /// <param name="profileId">Non-reserved optical profile byte ID.</param>
    /// <param name="surfaceDynamics">Validated per-liquid wave/impact/bubble coefficients.</param>
    /// <param name="rainExposed">Whether rain-map visibility permits drop impulses.</param>
    /// <param name="depthMetres">Positive local liquid depth, or infinity when deliberately unknown.</param>
    public void SetSurfaceCell(
        int x,
        int z,
        float worldSurfaceY,
        byte profileId,
        in LiquidSurfaceDynamics surfaceDynamics,
        bool rainExposed,
        float depthMetres = float.PositiveInfinity)
    {
        LiquidSurfacePhysicalProperties compatibilityProperties =
            LiquidSurfacePhysicalProperties.FromLegacy(in surfaceDynamics);
        SetSurfaceCell(
            x,
            z,
            worldSurfaceY,
            profileId,
            in surfaceDynamics,
            in compatibilityProperties,
            rainExposed,
            depthMetres);
    }

    /// <summary>Activates/configures one free-surface cell from explicit SI material properties.</summary>
    /// <param name="x">Zero-based grid X.</param>
    /// <param name="z">Zero-based grid Z.</param>
    /// <param name="worldSurfaceY">Undisturbed surface Y in metres/world blocks.</param>
    /// <param name="profileId">Non-reserved optical profile byte ID.</param>
    /// <param name="surfaceDynamics">Wave scale and dimensionless coupling efficiencies.</param>
    /// <param name="surfacePhysics">Density, viscosity, tension, and damping in SI.</param>
    /// <param name="rainExposed">Whether rain-map visibility permits drop impulses.</param>
    /// <param name="depthMetres">Positive local liquid depth, or infinity when deliberately unknown.</param>
    public void SetSurfaceCell(
        int x,
        int z,
        float worldSurfaceY,
        byte profileId,
        in LiquidSurfaceDynamics surfaceDynamics,
        in LiquidSurfacePhysicalProperties surfacePhysics,
        bool rainExposed,
        float depthMetres = float.PositiveInfinity)
    {
        int index = GetIndexChecked(x, z);
        if (profileId is LiquidOpticalRegistry.NoLiquidProfileId
            or LiquidOpticalRegistry.UnknownLiquidProfileId)
        {
            ClearSurfaceCell(x, z);
            return;
        }

        if (!float.IsFinite(worldSurfaceY))
        {
            throw new ArgumentOutOfRangeException(nameof(worldSurfaceY));
        }
        if ((!float.IsFinite(depthMetres) && !float.IsPositiveInfinity(depthMetres))
            || depthMetres <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(depthMetres));
        }
        surfacePhysics.Validate();
        topologyDirty = true;

        profileIds[index] = profileId;
        dynamics[index] = surfaceDynamics;
        physicalProperties[index] = surfacePhysics;
        surfaceWorldY[index] = worldSurfaceY;
        depthsMetres[index] = depthMetres;
        cellFlags[index] = (byte)(CellActive | (rainExposed ? CellRainExposed : 0));
        CacheInvariantCoefficients(
            index,
            in surfaceDynamics,
            in surfacePhysics,
            depthMetres);
        windForcingCacheDirty = true;
    }

    /// <summary>Deactivates one cell and clears every persistent/transient solver channel.</summary>
    /// <param name="x">Zero-based grid X.</param>
    /// <param name="z">Zero-based grid Z.</param>
    public void ClearSurfaceCell(int x, int z)
    {
        int index = GetIndexChecked(x, z);
        topologyDirty = true;
        heights[index] = 0.0f;
        velocities[index] = 0.0f;
        nextHeights[index] = 0.0f;
        nextVelocities[index] = 0.0f;
        transientEmission[index] = 0.0f;
        bubbleBulge[index] = 0.0f;
        normalX[index] = 0.0f;
        normalZ[index] = 0.0f;
        surfaceWorldY[index] = 0.0f;
        depthsMetres[index] = 0.0f;
        dynamics[index] = default;
        physicalProperties[index] = default;
        cachedStepCoefficients[index] = default;
        cachedWindCoefficients[index] = default;
        cachedWindAmplitudes[index] = 0.0f;
        cachedWindSpatialPhases[index] = 0.0f;
        cachedWindSpreadPhasesA[index] = 0.0f;
        cachedWindSpreadPhasesB[index] = 0.0f;
        profileIds[index] = LiquidOpticalRegistry.NoLiquidProfileId;
        cellFlags[index] = 0;
        windForcingCacheDirty = true;
    }

    /// <summary>Samples current non-bubble surface height for an active world position.</summary>
    /// <param name="worldX">World X in blocks.</param>
    /// <param name="worldZ">World Z in blocks.</param>
    /// <param name="worldY">Undisturbed Y plus signed wave height.</param>
    /// <returns>Whether the position maps to an active liquid cell.</returns>
    public bool TryGetSurfaceWorldY(double worldX, double worldZ, out float worldY)
    {
        if (!TryWorldToCell(worldX, worldZ, out int x, out int z))
        {
            worldY = 0.0f;
            return false;
        }

        int index = GetIndex(x, z);
        if (!IsActive(index))
        {
            worldY = 0.0f;
            return false;
        }

        worldY = surfaceWorldY[index] + heights[index];
        return true;
    }

    /// <summary>
    /// Finds the active liquid interface horizontally nearest to a world position. The query is used
    /// by the entity-mirror pass to choose the physically relevant local reflection plane without
    /// assuming that the player currently stands inside a liquid cell.
    /// </summary>
    /// <param name="worldX">Reference world X, normally the camera.</param>
    /// <param name="worldZ">Reference world Z, normally the camera.</param>
    /// <param name="worldY">Nearest undisturbed surface height.</param>
    /// <returns>Whether at least one active free-surface cell exists.</returns>
    public bool TryGetNearestSurfaceWorldY(double worldX, double worldZ, out float worldY)
    {
        double nearestDistanceSquared = double.PositiveInfinity;
        int nearestIndex = -1;
        for (int z = 0; z < Depth; z++)
        {
            double cellWorldZ = OriginWorldZ + (z + 0.5) * CellSize;
            double dz = cellWorldZ - worldZ;
            for (int x = 0; x < Width; x++)
            {
                int index = GetIndex(x, z);
                if (!IsActive(index))
                {
                    continue;
                }

                double cellWorldX = OriginWorldX + (x + 0.5) * CellSize;
                double dx = cellWorldX - worldX;
                double distanceSquared = dx * dx + dz * dz;
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestIndex = index;
                }
            }
        }

        if (nearestIndex < 0)
        {
            worldY = 0.0f;
            return false;
        }

        // Wave and impact displacement is applied later from the dynamic normal/height texture.
        // Keeping the raster mirror on the undisturbed plane prevents double displacement and jitter.
        worldY = surfaceWorldY[nearestIndex];
        return true;
    }

    /// <summary>Reads rendered height (waves plus bubbles), velocity, normal, and emission for diagnostics.</summary>
    /// <param name="x">Zero-based grid X.</param>
    /// <param name="z">Zero-based grid Z.</param>
    /// <returns>Detached cell state.</returns>
    public LiquidSurfaceCellSample GetCellSample(int x, int z)
    {
        int index = GetIndexChecked(x, z);
        return new LiquidSurfaceCellSample(
            heights[index] + bubbleBulge[index],
            velocities[index],
            normalX[index],
            normalZ[index],
            transientEmission[index]);
    }

    /// <summary>Tracks entity transitions and queues entries, wakes, fish motion, bobbers, and ricochets.</summary>
    /// <param name="samples">Current observations; no reference is retained after the call.</param>
    public void ObserveEntities(ReadOnlySpan<LiquidEntitySurfaceSample> samples)
    {
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            ref readonly LiquidEntitySurfaceSample sample = ref samples[sampleIndex];
            int trackerIndex = FindTracker(sample.EntityId, out bool existed);
            ref EntityTracker tracker = ref entityTrackers[trackerIndex];
            if (!existed)
            {
                // A bounded hash probe may replace another entity. Clear collision provenance,
                // airborne history, and wake flags before assigning the new stable entity ID.
                tracker = default;
            }

            bool initiallyImpacted = false;
            if (existed)
            {
                DetectEntitySurfaceEvent(ref tracker, in sample);
            }
            else if (sample.SurfaceClass == LiquidEntitySurfaceClass.DroppedItem)
            {
                initiallyImpacted = DetectInitiallyObservedDroppedItem(in sample);
            }

            tracker.EntityId = sample.EntityId;
            tracker.WorldX = sample.WorldX;
            tracker.WorldY = sample.WorldY;
            tracker.WorldZ = sample.WorldZ;
            tracker.FeetInLiquid = sample.FeetInLiquid;
            tracker.Swimming = sample.Swimming;
            tracker.SurfaceClass = sample.SurfaceClass;
            tracker.Occupied = true;
            tracker.LastSeenStep = stepIndex;
            if (sample.SurfaceClass == LiquidEntitySurfaceClass.DroppedItem
                && !sample.FeetInLiquid
                && (!tracker.HasLastAirMotion || sample.MotionY < tracker.LastAirMotionY))
            {
                tracker.LastAirMotionX = sample.MotionX;
                tracker.LastAirMotionY = sample.MotionY;
                tracker.LastAirMotionZ = sample.MotionZ;
                tracker.HasLastAirMotion = true;
            }
            if (!existed)
            {
                tracker.HasImpactedSurface = initiallyImpacted;
            }
        }
    }

    /// <summary>
    /// Converts exact projectile/liquid callbacks into bounded surface impulses. Any projectile with
    /// a measured upward rebound may produce several separated contacts; non-rebounding projectiles
    /// emit only their first entry so an entity resting in liquid cannot manufacture energy every tick.
    /// </summary>
    /// <param name="samples">Detached collision observations copied by the Harmony boundary.</param>
    public void ObserveProjectileLiquidCollisions(
        ReadOnlySpan<LiquidProjectileCollisionSample> samples)
    {
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            ref readonly LiquidProjectileCollisionSample sample = ref samples[sampleIndex];
            if (!float.IsFinite(sample.MassKilograms)
                || sample.MassKilograms <= 0.0f
                || !float.IsFinite(sample.MotionSamplePeriodSeconds)
                || sample.MotionSamplePeriodSeconds <= 0.0f
                || !TryWorldToCell(sample.WorldX, sample.WorldZ, out int x, out int z))
            {
                continue;
            }

            int cellIndex = GetIndex(x, z);
            if (!IsActive(cellIndex))
            {
                continue;
            }

            ResolveProjectileSurfaceContact(
                in sample,
                ref cellIndex,
                out double impactWorldX,
                out double impactWorldZ);

            float motionScale = (float)LiquidPhysicalModel.MetresPerWorldBlock
                / sample.MotionSamplePeriodSeconds;
            float velocityX = sample.IncidentMotionX * motionScale;
            float velocityY = sample.IncidentMotionY * motionScale;
            float velocityZ = sample.IncidentMotionZ * motionScale;
            float outgoingVelocityX = sample.OutgoingMotionX * motionScale;
            float outgoingVelocityY = sample.OutgoingMotionY * motionScale;
            float outgoingVelocityZ = sample.OutgoingMotionZ * motionScale;
            float horizontalSpeed = MathF.Sqrt(
                velocityX * velocityX + velocityZ * velocityZ);
            if (!float.IsFinite(horizontalSpeed)
                || !float.IsFinite(velocityY)
                || velocityY >= -MinimumMotion)
            {
                continue;
            }

            int trackerIndex = FindTracker(sample.EntityId, out bool existed);
            ref EntityTracker tracker = ref entityTrackers[trackerIndex];
            if (!existed)
            {
                // FindTracker may reuse the oldest probed slot. Provenance and collision flags
                // belong to the previous entity and must not leak into the replacement.
                tracker = default;
            }

            if (!sample.IsServerAuthoritative
                && existed
                && tracker.HasServerAuthoritativeLiquidCollision)
            {
                // An integrated server exposes the authoritative physics motion. Client-side
                // interpolation may reconstruct the same crossing in a later render drain, well
                // after the short same-contact window; it must never inject that energy twice.
                continue;
            }

            if (sample.IsServerAuthoritative)
            {
                // Persist provenance before temporal/spatial duplicate filtering. Even a nested
                // server callback establishes that subsequent client reconstruction is secondary;
                // server callbacks themselves remain eligible for distinct measured ricochets.
                tracker.HasServerAuthoritativeLiquidCollision = true;
            }

            bool measuredRicochet = outgoingVelocityY > MinimumMotion;
            bool thrownStoneRicochet = measuredRicochet
                && sample.SurfaceClass == LiquidEntitySurfaceClass.ThrownStone;
            if (existed && tracker.HasLiquidCollision)
            {
                int elapsedSteps = stepIndex - tracker.LastLiquidCollisionStep;
                double dx = impactWorldX - tracker.LastLiquidCollisionWorldX;
                double dz = impactWorldZ - tracker.LastLiquidCollisionWorldZ;
                bool sameContact = elapsedSteps < WakeStepInterval
                    && dx * dx + dz * dz < CellSize * CellSize * 0.25;
                if (sameContact || (!measuredRicochet && tracker.HasImpactedSurface))
                {
                    continue;
                }
            }

            // A real rebound exposes a measurable kinetic-energy loss. A non-rebounding
            // projectile exposes its incident kinetic energy; the class-specific drag/cavity
            // partition below determines the bounded interface, capillary, splash, and wake terms.
            float incidentSpeedSquared = velocityX * velocityX
                + velocityY * velocityY
                + velocityZ * velocityZ;
            float transferredSpeedSquared = measuredRicochet
                // A rebound is defined on the retained normal component. Its tangential vector can
                // be sampled at a different substep, so only the physically comparable normal
                // kinetic-energy loss is attributed to the water for every projectile implementation.
                ? Math.Max(
                    velocityY * velocityY - outgoingVelocityY * outgoingVelocityY,
                    0.0f)
                : incidentSpeedSquared;
            float transferredEnergy = 0.5f
                * sample.MassKilograms
                * transferredSpeedSquared;
            LiquidSurfaceImpulseKind kind = thrownStoneRicochet
                ? LiquidSurfaceImpulseKind.StoneRicochet
                : LiquidSurfaceImpulseKind.GenericEntry;
            LiquidProjectileWaveEnergyPartition energyPartition =
                ResolveProjectileWaveEnergyPartition(
                    sample.SurfaceClass,
                    sample.MassKilograms,
                    transferredEnergy,
                    in physicalProperties[cellIndex],
                    CellSize);
            // The cavity's gravitational potential and newly-created interface area are
            // reversible stores. At pinch-off they feed the outgoing gravity-capillary packet;
            // only the remaining spray/jet share stays in the compact local splash. Keeping
            // these allocations disjoint conserves the measured surface ledger while preventing
            // the cavity energy from disappearing when the short-lived depression closes.
            float collapseWaveEnergyJoules = energyPartition.CavityEnergyJoules
                + energyPartition.CapillaryPacketEnergyJoules;
            float supportRadiusWorldBlocks = ResolveProjectileImpulseSupportRadius(
                sample.SurfaceClass,
                sample.MassKilograms,
                transferredEnergy,
                in dynamics[cellIndex],
                in physicalProperties[cellIndex],
                CellSize);
            PendingSubgridImpact subgridImpact = new(
                TotalProjectileImpactCount + 1,
                sample.EntityId,
                sample.SurfaceClass,
                energyPartition.SurfaceCoupledEnergyJoules,
                energyPartition.ResolvedWaveEnergyJoules,
                energyPartition.SubgridWaveEnergyJoules,
                collapseWaveEnergyJoules,
                energyPartition.SplashEnergyJoules,
                energyPartition.CavityRadiusMetres,
                ResolveProjectileSubgridWavelengthMetres(
                    energyPartition.CavityRadiusMetres,
                    CellSize));
            bool accepted = QueueImpulseAtCell(
                cellIndex,
                impactWorldX,
                impactWorldZ,
                energyPartition.ResolvedWaveEnergyJoules,
                velocityX,
                velocityZ,
                kind,
                velocityY,
                supportRadiusWorldBlocks,
                couplingEfficiencyOverride: 1.0f,
                subgridImpact: subgridImpact);
            if (!accepted)
            {
                continue;
            }

            tracker.Occupied = true;
            tracker.EntityId = sample.EntityId;
            tracker.WorldX = sample.WorldX;
            tracker.WorldY = sample.WorldY;
            tracker.WorldZ = sample.WorldZ;
            tracker.FeetInLiquid = true;
            tracker.SurfaceClass = sample.SurfaceClass;
            tracker.LastSeenStep = stepIndex;
            tracker.LastLiquidCollisionStep = stepIndex;
            tracker.LastLiquidCollisionWorldX = impactWorldX;
            tracker.LastLiquidCollisionWorldZ = impactWorldZ;
            tracker.HasLiquidCollision = true;
            tracker.HasImpactedSurface = !measuredRicochet;
            TotalProjectileImpactCount++;
            LastProjectileImpactDiagnostic = new LiquidProjectileImpactDiagnostic(
                TotalProjectileImpactCount,
                sample.EntityId,
                sample.SurfaceClass,
                kind,
                impactWorldX,
                impactWorldZ,
                cellIndex % Width,
                cellIndex / Width,
                supportRadiusWorldBlocks,
                sample.MassKilograms,
                transferredEnergy,
                velocityX,
                velocityY,
                velocityZ,
                outgoingVelocityX,
                outgoingVelocityY,
                outgoingVelocityZ,
                sample.IsServerAuthoritative,
                energyPartition.SurfaceCoupledEnergyJoules,
                energyPartition.ResolvedWaveEnergyJoules,
                energyPartition.SubgridWaveEnergyJoules,
                energyPartition.NearInterfaceEnergyJoules,
                energyPartition.CavityEnergyJoules,
                energyPartition.CapillaryPacketEnergyJoules,
                energyPartition.SplashEnergyJoules,
                energyPartition.WakeEnergyJoules);
        }
    }

    /// <summary>
    /// Reconstructs the free-surface point from the exact physics substep ending at the callback
    /// position. Using the endpoint directly can shift a fast arrow or stone into the next cell.
    /// </summary>
    /// <param name="sample">Exact projectile collision observation.</param>
    /// <param name="impactCellIndex">Initially the endpoint cell; remapped to the crossing cell.</param>
    /// <param name="impactWorldX">Resolved surface X, or endpoint X when no finite crossing exists.</param>
    /// <param name="impactWorldZ">Resolved surface Z, or endpoint Z when no finite crossing exists.</param>
    private void ResolveProjectileSurfaceContact(
        in LiquidProjectileCollisionSample sample,
        ref int impactCellIndex,
        out double impactWorldX,
        out double impactWorldZ)
    {
        impactWorldX = sample.WorldX;
        impactWorldZ = sample.WorldZ;
        double previousWorldX = sample.PreviousWorldX;
        double previousWorldY = sample.PreviousWorldY;
        double previousWorldZ = sample.PreviousWorldZ;
        double verticalTravel = previousWorldY - sample.WorldY;
        if (!double.IsFinite(verticalTravel) || verticalTravel <= 1.0e-8)
        {
            return;
        }

        for (int iteration = 0; iteration < 2; iteration++)
        {
            float surfaceY = surfaceWorldY[impactCellIndex] + heights[impactCellIndex];
            double crossingFraction = (previousWorldY - surfaceY) / verticalTravel;
            if (!double.IsFinite(crossingFraction)
                || crossingFraction < 0.0
                || crossingFraction > 1.0 + CellSize * 0.15f / verticalTravel)
            {
                return;
            }

            crossingFraction = Math.Clamp(crossingFraction, 0.0, 1.0);
            impactWorldX = previousWorldX
                + (sample.WorldX - previousWorldX) * crossingFraction;
            impactWorldZ = previousWorldZ
                + (sample.WorldZ - previousWorldZ) * crossingFraction;
            if (!TryWorldToCell(impactWorldX, impactWorldZ, out int x, out int z))
            {
                impactWorldX = sample.WorldX;
                impactWorldZ = sample.WorldZ;
                return;
            }

            int remappedIndex = GetIndex(x, z);
            if (!IsActive(remappedIndex))
            {
                impactWorldX = sample.WorldX;
                impactWorldZ = sample.WorldZ;
                return;
            }
            if (remappedIndex == impactCellIndex)
            {
                return;
            }
            impactCellIndex = remappedIndex;
        }
    }

    /// <summary>Queues a bounded positive-energy world-space impulse on an active surface.</summary>
    /// <param name="worldX">Impact X in world blocks.</param>
    /// <param name="worldZ">Impact Z in world blocks.</param>
    /// <param name="energy">Incident kinetic or surface energy in joules.</param>
    /// <param name="directionX">Optional horizontal velocity X in m/s controlling wake direction.</param>
    /// <param name="directionZ">Optional horizontal velocity Z in m/s controlling wake direction.</param>
    /// <param name="kind">Semantic source for emission/diagnostics.</param>
    /// <returns>Whether the event maps to active liquid and fits the queue.</returns>
    public bool QueueImpulse(
        double worldX,
        double worldZ,
        float energy,
        float directionX = 0.0f,
        float directionZ = 0.0f,
        LiquidSurfaceImpulseKind kind = LiquidSurfaceImpulseKind.GenericEntry)
    {
        if (!float.IsFinite(energy) || energy <= 0.0f
            || !TryWorldToCell(worldX, worldZ, out int x, out int z))
        {
            return false;
        }

        int index = GetIndex(x, z);
        if (!IsActive(index))
        {
            return false;
        }

        return QueueImpulseAtCell(index, worldX, worldZ, energy, directionX, directionZ, kind);
    }

    /// <summary>Converts an SI mass and speed to kinetic energy and queues the resulting impact.</summary>
    /// <param name="worldX">Impact X in metres/world blocks.</param>
    /// <param name="worldZ">Impact Z in metres/world blocks.</param>
    /// <param name="massKilograms">Positive impacting mass in kilograms.</param>
    /// <param name="speedMetresPerSecond">Non-negative impact speed in m/s.</param>
    /// <param name="directionXMetresPerSecond">Horizontal X velocity in m/s.</param>
    /// <param name="directionZMetresPerSecond">Horizontal Z velocity in m/s.</param>
    /// <param name="kind">Semantic impact class.</param>
    /// <returns>Whether the event maps to active liquid and fits the bounded queue.</returns>
    public bool QueueImpact(
        double worldX,
        double worldZ,
        float massKilograms,
        float speedMetresPerSecond,
        float directionXMetresPerSecond = 0.0f,
        float directionZMetresPerSecond = 0.0f,
        LiquidSurfaceImpulseKind kind = LiquidSurfaceImpulseKind.GenericEntry)
    {
        if (!float.IsFinite(massKilograms)
            || massKilograms <= 0.0f
            || !float.IsFinite(speedMetresPerSecond)
            || speedMetresPerSecond < 0.0f)
        {
            return false;
        }

        float kineticEnergyJoules = (float)LiquidPhysicalModel.KineticEnergyJoules(
            massKilograms,
            speedMetresPerSecond);
        return QueueImpulse(
            worldX,
            worldZ,
            kineticEnergyJoules,
            directionXMetresPerSecond,
            directionZMetresPerSecond,
            kind);
    }

    /// <summary>Consumes bounded elapsed time through deterministic 120 Hz fixed steps.</summary>
    /// <param name="elapsedSeconds">Render-frame duration; invalid/negative values become zero and large values saturate.</param>
    /// <param name="forcing">Wind and rainfall held constant across completed substeps.</param>
    /// <returns>Fixed steps completed; at most <see cref="MaximumStepsPerAdvance"/>.</returns>
    public int Advance(float elapsedSeconds, in LiquidSurfaceForcing forcing)
    {
        float safeElapsed = Math.Clamp(
            float.IsFinite(elapsedSeconds) ? elapsedSeconds : 0.0f,
            0.0f,
            MaximumFrameSeconds);
        accumulator += safeElapsed;
        int completedSteps = 0;
        while (accumulator >= FixedStepSeconds && completedSteps < MaximumStepsPerAdvance)
        {
            Step(in forcing);
            accumulator -= FixedStepSeconds;
            completedSteps++;
        }

        if (completedSteps == MaximumStepsPerAdvance && accumulator >= FixedStepSeconds)
        {
            accumulator %= FixedStepSeconds;
        }

        return completedSteps;
    }

    /// <summary>Writes row-major RGBA32F height/normal/emission without allocating.</summary>
    /// <param name="destination">Span of at least width times depth times four floats.</param>
    public void WriteGpuTexture(Span<float> destination)
    {
        int required = checked(heights.Length * GpuChannels);
        if (destination.Length < required)
        {
            throw new ArgumentException($"Destination requires at least {required} floats.", nameof(destination));
        }

        for (int index = 0, output = 0; index < heights.Length; index++, output += GpuChannels)
        {
            destination[output] = heights[index] + bubbleBulge[index];
            destination[output + 1] = normalX[index];
            destination[output + 2] = normalZ[index];
            destination[output + 3] = transientEmission[index];
        }
    }

    /// <summary>Compacts active round bubble primitives into caller-provided GPU upload storage.</summary>
    /// <param name="destination">Capacity-bounded destination; excess active bubbles remain simulated.</param>
    /// <returns>Number of samples written.</returns>
    public int WriteActiveBubbles(Span<LiquidSurfaceBubbleSample> destination)
    {
        int written = 0;
        for (int bubbleIndex = 0; bubbleIndex < bubbles.Length && written < destination.Length; bubbleIndex++)
        {
            ref readonly BubbleState bubble = ref bubbles[bubbleIndex];
            if (!bubble.Active)
            {
                continue;
            }

            int x = bubble.CellIndex % Width;
            int z = bubble.CellIndex / Width;
            float progress = Math.Clamp(bubble.Age / bubble.Duration, 0.0f, 1.0f);
            float growth = bubble.Bursting
                ? 0.0f
                : progress * progress * (3.0f - 2.0f * progress);
            LiquidSurfaceDynamics coefficients = dynamics[bubble.CellIndex];
            float burstEnvelope = bubble.Bursting
                ? (1.0f - progress) * (1.0f - progress)
                : 0.0f;
            destination[written++] = new LiquidSurfaceBubbleSample(
                (x + bubble.OffsetX) * CellSize,
                (z + bubble.OffsetZ) * CellSize,
                bubble.Radius,
                growth,
                coefficients.BubbleEmissionBoost * burstEnvelope);
        }

        return written;
    }

    /// <summary>
    /// Computes resolved mechanical energy in joules. Potential energy uses
    /// 0.5*rho*g*h^2*S; vertical kinetic energy uses the deep-water e-folding depth 1/(2k).
    /// </summary>
    /// <returns>Finite non-negative energy over active cells in joules.</returns>
    internal float ComputeTotalEnergy()
    {
        double energyJoules = 0.0;
        double cellAreaSquareMetres = CellSize * CellSize
            * LiquidPhysicalModel.MetresPerWorldBlock
            * LiquidPhysicalModel.MetresPerWorldBlock;
        for (int index = 0; index < heights.Length; index++)
        {
            if (IsActive(index))
            {
                LiquidSurfaceDynamics coefficients = dynamics[index];
                LiquidSurfacePhysicalProperties physics = physicalProperties[index];
                double wavelengthMetres = ResolveWavelengthMetres(
                    coefficients.WaveLength,
                    CellSize);
                double waveNumberPerMetre = 2.0 * Math.PI / wavelengthMetres;
                double effectiveDepthMetres = 1.0 / (2.0 * waveNumberPerMetre);
                double heightMetres = heights[index] * LiquidPhysicalModel.MetresPerWorldBlock;
                double velocityMetresPerSecond = velocities[index]
                    * LiquidPhysicalModel.MetresPerWorldBlock;
                energyJoules += 0.5
                    * physics.DensityKilogramsPerCubicMetre
                    * cellAreaSquareMetres
                    * (LiquidPhysicalModel.StandardGravityMetresPerSecondSquared
                        * heightMetres * heightMetres
                        + effectiveDepthMetres
                            * velocityMetresPerSecond * velocityMetresPerSecond);
            }
        }

        return (float)energyJoules;
    }

    /// <summary>Resolves narrow-band gravity-capillary crest phase speed from SI properties.</summary>
    /// <param name="coefficients">Dominant authored wavelength in metres/world blocks.</param>
    /// <param name="physics">Density and surface tension in SI.</param>
    /// <param name="cellSize">Grid edge length in metres/world blocks.</param>
    /// <param name="depthMetres">Positive local depth, or infinity for the deep-water limit.</param>
    /// <returns>Finite-depth crest phase speed in m/s.</returns>
    internal static float ComputePhaseSpeedMetresPerSecond(
        in LiquidSurfaceDynamics coefficients,
        in LiquidSurfacePhysicalProperties physics,
        float cellSize,
        float depthMetres = float.PositiveInfinity)
    {
        double wavelengthMetres = ResolveWavelengthMetres(coefficients.WaveLength, cellSize);
        return (float)LiquidPhysicalModel.GravityCapillaryPhaseVelocityMetresPerSecond(
            wavelengthMetres,
            depthMetres,
            physics.DensityKilogramsPerCubicMetre,
            physics.SurfaceTensionNewtonsPerMetre);
    }

    /// <summary>Resolves localized wave-packet propagation speed from SI properties and depth.</summary>
    /// <param name="coefficients">Dominant authored wavelength in metres/world blocks.</param>
    /// <param name="physics">Density and surface tension in SI.</param>
    /// <param name="cellSize">Grid edge length in metres/world blocks.</param>
    /// <param name="depthMetres">Positive local liquid depth, or infinity for deep water.</param>
    /// <returns>Energy-envelope group velocity in m/s.</returns>
    internal static float ComputeGroupVelocityMetresPerSecond(
        in LiquidSurfaceDynamics coefficients,
        in LiquidSurfacePhysicalProperties physics,
        float cellSize,
        float depthMetres)
    {
        double wavelengthMetres = ResolveWavelengthMetres(coefficients.WaveLength, cellSize);
        return (float)LiquidPhysicalModel.GravityCapillaryGroupVelocityMetresPerSecond(
            wavelengthMetres,
            depthMetres,
            physics.DensityKilogramsPerCubicMetre,
            physics.SurfaceTensionNewtonsPerMetre);
    }

    /// <summary>Resolves clean-interface viscous decay plus explicit unresolved losses in s^-1.</summary>
    /// <param name="coefficients">Dominant authored wavelength in metres/world blocks.</param>
    /// <param name="physics">Density, dynamic viscosity, and additional decay in SI.</param>
    /// <param name="cellSize">Grid edge length in metres/world blocks.</param>
    /// <returns>Non-negative amplitude damping rate in s^-1.</returns>
    internal static float ComputeDampingPerSecond(
        in LiquidSurfaceDynamics coefficients,
        in LiquidSurfacePhysicalProperties physics,
        float cellSize)
    {
        double wavelengthMetres = ResolveWavelengthMetres(coefficients.WaveLength, cellSize);
        return (float)(LiquidPhysicalModel.ViscousAmplitudeDampingPerSecond(
            wavelengthMetres,
            physics.DensityKilogramsPerCubicMetre,
            physics.DynamicViscosityPascalSeconds)
            + physics.AdditionalDampingPerSecond);
    }

    /// <summary>
    /// Resolves cell-local dispersion, damping, clamp, emission, and wind-frequency terms once
    /// when topology/material inputs change instead of recomputing transcendental SI equations
    /// on every 120 Hz fixed step.
    /// </summary>
    /// <param name="index">Configured flat cell index.</param>
    /// <param name="coefficients">Authored dynamics for the cell.</param>
    /// <param name="physics">Validated SI material properties for the cell.</param>
    /// <param name="depthMetres">Positive finite-depth or deep-water marker.</param>
    private void CacheInvariantCoefficients(
        int index,
        in LiquidSurfaceDynamics coefficients,
        in LiquidSurfacePhysicalProperties physics,
        float depthMetres)
    {
        float physicalSpeed = ComputeGroupVelocityMetresPerSecond(
            in coefficients,
            in physics,
            CellSize,
            depthMetres);
        float maximumStableSpeed = CflSafety * CellSize
            / (FixedStepSeconds * MathF.Sqrt(2.0f));
        float stableSpeed = Math.Min(physicalSpeed, maximumStableSpeed);
        float dampingPerSecond = ComputeDampingPerSecond(
            in coefficients,
            in physics,
            CellSize);
        cachedStepCoefficients[index] = new CellStepCoefficients(
            stableSpeed * stableSpeed,
            MathF.Exp(-dampingPerSecond * FixedStepSeconds),
            ComputeMaximumHeight(in coefficients),
            MathF.Exp(
                -FixedStepSeconds / Math.Max(
                    FixedStepSeconds,
                    coefficients.BubbleRiseDuration * BubbleBurstDecayFraction)));

        if (coefficients.WindCoupling <= 0.0f || coefficients.WaveAmplitude <= 0.0f)
        {
            cachedWindCoefficients[index] = default;
            return;
        }

        float wavelength = (float)ResolveWavelengthMetres(
            coefficients.WaveLength,
            CellSize);
        float phaseSpeed = ComputePhaseSpeedMetresPerSecond(
            in coefficients,
            in physics,
            CellSize,
            depthMetres);
        float angularFrequency = 2.0f * MathF.PI * phaseSpeed / wavelength;
        cachedWindCoefficients[index] = new CellWindCoefficients(
            wavelength,
            phaseSpeed,
            phaseSpeed / wavelength,
            angularFrequency * angularFrequency);
    }

    /// <summary>
    /// Rebuilds only wind-direction/strength-dependent terms when the sampled environment changes.
    /// Runtime weather is sampled at 20 Hz, so the 120 Hz solver reuses these exact physical terms
    /// for the intervening fixed steps while retaining per-step phase evolution and feedback.
    /// </summary>
    /// <param name="windX">Finite wind X in metres per second.</param>
    /// <param name="windZ">Finite wind Z in metres per second.</param>
    /// <param name="windLength">Positive horizontal wind magnitude.</param>
    /// <param name="directionX">Normalized wind X direction.</param>
    /// <param name="directionZ">Normalized wind Z direction.</param>
    private void RefreshWindForcingCache(
        float windX,
        float windZ,
        float windLength,
        float directionX,
        float directionZ)
    {
        EnsureActiveTopology();
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
            int index = activeStencils[ordinal].Center;
            int x = index % Width;
            int z = index / Width;
            ref readonly CellWindCoefficients cached =
                ref cachedWindCoefficients[index];
            if (cached.AngularFrequencySquared <= 0.0f)
            {
                cachedWindAmplitudes[index] = 0.0f;
                cachedWindSpatialPhases[index] = 0.0f;
                cachedWindSpreadPhasesA[index] = 0.0f;
                cachedWindSpreadPhasesB[index] = 0.0f;
                continue;
            }

            LiquidSurfaceDynamics coefficients = dynamics[index];
            LiquidSurfacePhysicalProperties physics = physicalProperties[index];
            float wavePeriodSeconds = cached.Wavelength / cached.PhaseSpeed;
            float flowExposure = Math.Clamp(coefficients.WindCoupling, 0.0f, 1.0f);
            float exposedWindSpeedMetresPerSecond = windLength * flowExposure;
            double incidentEnergyPerSquareMetre = 0.5
                * ReferenceAirDensityKilogramsPerCubicMetre
                * exposedWindSpeedMetresPerSecond
                * exposedWindSpeedMetresPerSecond
                * exposedWindSpeedMetresPerSecond
                * wavePeriodSeconds;
            float windAmplitudeMetres = (float)LiquidPhysicalModel.ImpactDisplacementMetres(
                incidentEnergyPerSquareMetre,
                physics.ResolvedWaveEnergyFraction,
                physics.DensityKilogramsPerCubicMetre,
                1.0);
            cachedWindAmplitudes[index] = Math.Min(
                coefficients.WaveAmplitude,
                windAmplitudeMetres / (float)LiquidPhysicalModel.MetresPerWorldBlock);
            float worldX = OriginWorldX + (x + 0.5f) * CellSize;
            float worldZ = OriginWorldZ + (z + 0.5f) * CellSize;
            float crossX = -directionZ;
            float crossZ = directionX;
            const float spreadTangentA = 0.58f;
            const float spreadTangentB = -0.72f;
            float spreadNormalizationA = 1.0f / MathF.Sqrt(
                1.0f + spreadTangentA * spreadTangentA);
            float spreadNormalizationB = 1.0f / MathF.Sqrt(
                1.0f + spreadTangentB * spreadTangentB);
            float spreadDirectionAX = (
                directionX + crossX * spreadTangentA) * spreadNormalizationA;
            float spreadDirectionAZ = (
                directionZ + crossZ * spreadTangentA) * spreadNormalizationA;
            float spreadDirectionBX = (
                directionX + crossX * spreadTangentB) * spreadNormalizationB;
            float spreadDirectionBZ = (
                directionZ + crossZ * spreadTangentB) * spreadNormalizationB;
            cachedWindSpatialPhases[index] = (
                worldX * directionX + worldZ * directionZ)
                / cached.Wavelength;
            cachedWindSpreadPhasesA[index] = (
                worldX * spreadDirectionAX + worldZ * spreadDirectionAZ)
                / (cached.Wavelength * 0.73f)
                + 0.173f;
            cachedWindSpreadPhasesB[index] = (
                worldX * spreadDirectionBX + worldZ * spreadDirectionBZ)
                / (cached.Wavelength * 0.47f)
                + 0.619f;
        }

        cachedWindX = windX;
        cachedWindZ = windZ;
        windForcingCacheDirty = false;
    }

    /// <summary>
    /// Executes one explicit damped wave step after applying queued, rain, wind, and bubble forcing.
    /// Propagation speed is locally CFL-clamped and disconnected profiles/heights form reflecting boundaries.
    /// </summary>
    /// <param name="forcing">Environmental forcing for this fixed step.</param>
    private void Step(in LiquidSurfaceForcing forcing)
    {
        EnsureActiveTopology();
        ApplyPendingImpulses();
        ApplyRain(in forcing);
        ApplyWind(in forcing);
        AdvanceBubbles();

        float inverseCellSizeSquared = 1.0f / (CellSize * CellSize);
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
            ref readonly CellStencil stencil = ref activeStencils[ordinal];
            int index = stencil.Center;
            ref readonly CellStepCoefficients cached =
                ref cachedStepCoefficients[index];
            float center = heights[index];
            float laplacian = (
                heights[stencil.Left]
                + heights[stencil.Right]
                + heights[stencil.Up]
                + heights[stencil.Down]
                - 4.0f * center) * inverseCellSizeSquared;

            // The local field represents the envelope of a narrow-band
            // impact packet, so its front advances at d(omega)/dk. Crest
            // phase remains the responsibility of the procedural bands.
            float acceleration = cached.PropagationSpeedSquared * laplacian;
            float velocity = velocities[index] + acceleration * FixedStepSeconds;
            velocity *= cached.VelocityRetention;

            float height = Math.Clamp(
                center + velocity * FixedStepSeconds,
                -cached.MaximumHeight,
                cached.MaximumHeight);
            if (Math.Abs(height) >= cached.MaximumHeight
                && Math.Sign(velocity) == Math.Sign(height))
            {
                velocity = 0.0f;
            }

            nextHeights[index] = float.IsFinite(height) ? height : 0.0f;
            nextVelocities[index] = float.IsFinite(velocity) ? velocity : 0.0f;
            transientEmission[index] *= cached.EmissionRetention;
        }

        // All active destinations are written. ClearSurfaceCell zeros BOTH banks when a cell
        // is removed, so inactive cells remain zero without revisiting the entire dry grid.
        // Preserve the existing ping-pong ownership and fixed-step update order.
        (heights, nextHeights) = (nextHeights, heights);
        (velocities, nextVelocities) = (nextVelocities, velocities);
        ComputeNormals();
        if (lastSubgridImpactCellIndex >= 0)
        {
            int impactX = lastSubgridImpactCellIndex % Width;
            int impactZ = lastSubgridImpactCellIndex / Width;
            float uploadedHeight = heights[lastSubgridImpactCellIndex]
                + bubbleBulge[lastSubgridImpactCellIndex];
            LastSubgridImpactDiagnostic = new LiquidSurfaceSubgridImpactDiagnostic(
                lastSubgridImpactSequence,
                lastSubgridImpactSourceSequence,
                lastSubgridImpactEntityId,
                lastSubgridImpactSurfaceClass,
                lastSubgridImpactKind,
                lastSubgridImpactWorldX,
                lastSubgridImpactWorldZ,
                impactX,
                impactZ,
                (impactX + 0.5f) / Width,
                (impactZ + 0.5f) / Depth,
                lastSubgridImpactPeakDisplacement,
                lastSubgridImpactSplashPeakDisplacement,
                lastSubgridImpactSplashRadiusWorldBlocks,
                lastSubgridImpactSplashReleaseSeconds,
                uploadedHeight,
                normalX[lastSubgridImpactCellIndex],
                normalZ[lastSubgridImpactCellIndex],
                lastSubgridImpactSurfaceEnergyJoules,
                lastSubgridImpactResolvedEnergyJoules,
                lastSubgridImpactEnergyJoules,
                lastSubgridImpactRenderedEnergyJoules,
                lastSubgridImpactLocalSplashEnergyJoules,
                lastSubgridImpactWakeEnergyJoules,
                lastSubgridImpactDirectionX,
                lastSubgridImpactDirectionZ,
                lastSubgridImpactVelocityY,
                lastSubgridImpactDensity,
                lastSubgridImpactDynamicViscosity,
                lastSubgridImpactSurfaceTension,
                lastSubgridImpactAdditionalDamping,
                lastSubgridImpactWavelength);
            if (lastSubgridImpactSurfaceClass == LiquidEntitySurfaceClass.DroppedItem)
            {
                LastDroppedItemImpactDiagnostic = new LiquidSurfaceImpactDiagnostic(
                    lastSubgridImpactSourceSequence,
                    lastSubgridImpactWorldX,
                    lastSubgridImpactWorldZ,
                    impactX,
                    impactZ,
                    (impactX + 0.5f) / Width,
                    (impactZ + 0.5f) / Depth,
                    LastDroppedItemImpactPeakDisplacement,
                    uploadedHeight,
                    normalX[lastSubgridImpactCellIndex],
                    normalZ[lastSubgridImpactCellIndex],
                    lastSubgridImpactPeakDisplacement,
                    lastSubgridImpactIncidentEnergyJoules,
                    lastSubgridImpactDirectionX,
                    lastSubgridImpactDirectionZ,
                    lastSubgridImpactVelocityY,
                    lastSubgridImpactDensity,
                    lastSubgridImpactDynamicViscosity,
                    lastSubgridImpactSurfaceTension,
                    lastSubgridImpactAdditionalDamping,
                    lastSubgridImpactWavelength);
            }
        }
        stepIndex++;
    }

    /// <summary>Distributes queued impulses radially over connected cells, then empties the queue.</summary>
    private void ApplyPendingImpulses()
    {
        for (int eventIndex = 0; eventIndex < pendingImpulseCount; eventIndex++)
        {
            ref readonly PendingImpulse impulse = ref pendingImpulses[eventIndex];
            int centerIndex = impulse.CellIndex;
            if (!IsActive(centerIndex))
            {
                continue;
            }

            LiquidSurfaceDynamics coefficients = dynamics[centerIndex];
            LiquidSurfacePhysicalProperties physics = physicalProperties[centerIndex];
            float radiusWorld = impulse.SupportRadiusWorldBlocks > 0.0f
                ? Math.Clamp(
                    impulse.SupportRadiusWorldBlocks,
                    CellSize,
                    ResolveImpulseSupportRadius(in coefficients, CellSize))
                : ResolveImpulseSupportRadius(in coefficients, CellSize);
            int radiusCells = Math.Max(1, (int)MathF.Ceiling(radiusWorld / CellSize));
            int centerX = centerIndex % Width;
            int centerZ = centerIndex / Width;
            float directionLength = MathF.Sqrt(
                impulse.DirectionX * impulse.DirectionX
                + impulse.DirectionZ * impulse.DirectionZ);
            float directionX = directionLength > MinimumMotion
                ? impulse.DirectionX / directionLength
                : 0.0f;
            float directionZ = directionLength > MinimumMotion
                ? impulse.DirectionZ / directionLength
                : 0.0f;
            bool directionalWake = impulse.Kind is LiquidSurfaceImpulseKind.SwimmingWake
                or LiquidSurfaceImpulseKind.Bobber
                or LiquidSurfaceImpulseKind.NearSurfaceFish
                or LiquidSurfaceImpulseKind.StoneRicochet;

            // The compact radial basis defines only where energy is deposited.
            // Its squared integral supplies S_eff, so changing grid resolution
            // does not invent or remove coupled impact energy.
            double effectiveAreaSquareMetres = 0.0;
            double cellAreaSquareMetres = CellSize * CellSize
                * LiquidPhysicalModel.MetresPerWorldBlock
                * LiquidPhysicalModel.MetresPerWorldBlock;
            for (int offsetZ = -radiusCells; offsetZ <= radiusCells; offsetZ++)
            {
                int z = centerZ + offsetZ;
                if ((uint)z >= (uint)Depth)
                {
                    continue;
                }

                for (int offsetX = -radiusCells; offsetX <= radiusCells; offsetX++)
                {
                    int x = centerX + offsetX;
                    if ((uint)x >= (uint)Width)
                    {
                        continue;
                    }

                    int index = GetIndex(x, z);
                    if (!IsConnected(centerIndex, index))
                    {
                        continue;
                    }

                    float worldX = OriginWorldX + (x + 0.5f) * CellSize;
                    float worldZ = OriginWorldZ + (z + 0.5f) * CellSize;
                    float weight = ComputeImpulseKernelWeight(
                        worldX - (float)impulse.WorldX,
                        worldZ - (float)impulse.WorldZ,
                        radiusWorld,
                        directionX,
                        directionZ,
                        directionalWake);
                    effectiveAreaSquareMetres += weight * weight * cellAreaSquareMetres;
                }
            }

            if (effectiveAreaSquareMetres <= double.Epsilon)
            {
                continue;
            }

            float couplingEfficiency = impulse.CouplingEfficiencyOverride >= 0.0f
                ? Math.Clamp(impulse.CouplingEfficiencyOverride, 0.0f, 1.0f)
                : Math.Clamp(
                    impulse.Kind == LiquidSurfaceImpulseKind.BubbleBurst
                        ? coefficients.BubbleBurstStrength
                        : coefficients.ImpactResponse,
                    0.0f,
                    1.0f);
            float peakDisplacement = (float)(LiquidPhysicalModel.ImpactDisplacementMetres(
                impulse.Energy,
                couplingEfficiency,
                physics.DensityKilogramsPerCubicMetre,
                effectiveAreaSquareMetres)
                / LiquidPhysicalModel.MetresPerWorldBlock);
            LastAppliedImpactPeakDisplacement = peakDisplacement;
            float resolvedPeakDisplacement = peakDisplacement;
            if (impulse.Kind == LiquidSurfaceImpulseKind.DroppedItemEntry)
            {
                // Linear-wave energy is proportional to amplitude squared. Split the physical
                // impact between the grid-resolved band and the sub-grid gravity-capillary packet
                // without duplicating energy merely to make a small thrown item visible.
                float resolvedEnergyFraction = Math.Clamp(
                    physics.ResolvedWaveEnergyFraction,
                    0.0f,
                    1.0f);
                resolvedPeakDisplacement = peakDisplacement
                    * MathF.Sqrt(resolvedEnergyFraction);
                float subgridPeakDisplacement = peakDisplacement
                    * MathF.Sqrt(1.0f - resolvedEnergyFraction);
                LastDroppedItemImpactPeakDisplacement = peakDisplacement;
                double equivalentCouplingDiameterMetres = 2.0
                    * Math.Sqrt(effectiveAreaSquareMetres / Math.PI);
                float dominantWavelengthMetres = (float)Math.Clamp(
                    equivalentCouplingDiameterMetres * 0.65,
                    0.12,
                    0.55);
                float surfaceEnergyJoules = impulse.Energy * couplingEfficiency;
                float resolvedEnergyJoules = surfaceEnergyJoules * resolvedEnergyFraction;
                float subgridEnergyJoules = Math.Max(
                    0.0f,
                    surfaceEnergyJoules - resolvedEnergyJoules);
                RecordSubgridImpact(
                    in impulse,
                    centerIndex,
                    LiquidEntitySurfaceClass.DroppedItem,
                    surfaceEnergyJoules,
                    resolvedEnergyJoules,
                    subgridEnergyJoules,
                    subgridEnergyJoules,
                    0.0f,
                    0.0f,
                    subgridPeakDisplacement,
                    dominantWavelengthMetres,
                    radiusWorld,
                    physics);
            }
            else if (impulse.SubgridImpact.SubgridWaveEnergyJoules > 0.0f)
            {
                float surfaceEnergyJoules = Math.Max(
                    0.0f,
                    impulse.SubgridImpact.SurfaceCoupledEnergyJoules);
                float resolvedEnergyJoules = Math.Clamp(
                    impulse.SubgridImpact.ResolvedWaveEnergyJoules,
                    0.0f,
                    surfaceEnergyJoules);
                float subgridEnergyJoules = Math.Clamp(
                    impulse.SubgridImpact.SubgridWaveEnergyJoules,
                    0.0f,
                    Math.Max(0.0f, surfaceEnergyJoules - resolvedEnergyJoules));
                float requestedPacketEnergyJoules = Math.Clamp(
                    impulse.SubgridImpact.RenderedPacketEnergyJoules,
                    0.0f,
                    subgridEnergyJoules);
                double packetWaveNumberPerMetre = 2.0 * Math.PI
                    / Math.Max(0.012f, impulse.SubgridImpact.DominantWavelengthMetres);
                double initialEnvelopeSigmaMetres = Math.Max(
                    impulse.SubgridImpact.DominantWavelengthMetres * 0.35,
                    0.05);
                // A radial Gaussian carrier initially occupies pi^(3/2)*sigma^2 of effective
                // energy area. The shader grows that area with the dispersive ring radius and
                // derives its instantaneous amplitude from the same rendered-energy term.
                double packetAreaSquareMetres = Math.Pow(Math.PI, 1.5)
                    * initialEnvelopeSigmaMetres * initialEnvelopeSigmaMetres;
                double packetStiffnessNewtonsPerMetre = (
                    physics.DensityKilogramsPerCubicMetre
                        * LiquidPhysicalModel.StandardGravityMetresPerSecondSquared
                    + physics.SurfaceTensionNewtonsPerMetre
                        * packetWaveNumberPerMetre * packetWaveNumberPerMetre)
                    * packetAreaSquareMetres;
                float subgridPeakDisplacement = (float)(Math.Sqrt(
                    2.0 * requestedPacketEnergyJoules
                    / Math.Max(packetStiffnessNewtonsPerMetre, double.Epsilon))
                    / LiquidPhysicalModel.MetresPerWorldBlock);
                float linearSteepnessBound = (float)(0.35
                    / packetWaveNumberPerMetre
                    / LiquidPhysicalModel.MetresPerWorldBlock);
                // Saturation can only discard unresolved energy; it never increases amplitude or
                // transfers the packet's energy back into the already-resolved height field.
                subgridPeakDisplacement = Math.Min(
                    Math.Min(subgridPeakDisplacement, linearSteepnessBound),
                    ComputeMaximumHeight(in coefficients));
                // PeakDisplacement is a steepness-safe instantaneous ceiling, not a second
                // energy partition. Energy that cannot appear at the singular newborn ring stays
                // in the same capillary packet and becomes representable as its circumference
                // expands; it must not be reclassified as wake merely because t is near zero.
                float renderedPacketEnergyJoules = requestedPacketEnergyJoules;
                float localSplashEnergyJoules = Math.Clamp(
                    impulse.SubgridImpact.LocalSplashEnergyJoules,
                    0.0f,
                    Math.Max(0.0f, subgridEnergyJoules - renderedPacketEnergyJoules));
                float wakeEnergyJoules = Math.Max(
                    0.0f,
                    subgridEnergyJoules
                        - renderedPacketEnergyJoules
                        - localSplashEnergyJoules);
                RecordSubgridImpact(
                    in impulse,
                    centerIndex,
                    impulse.SubgridImpact.SurfaceClass,
                    surfaceEnergyJoules,
                    resolvedEnergyJoules,
                    subgridEnergyJoules,
                    renderedPacketEnergyJoules,
                    localSplashEnergyJoules,
                    wakeEnergyJoules,
                    subgridPeakDisplacement,
                    impulse.SubgridImpact.DominantWavelengthMetres,
                    radiusWorld,
                    physics);
            }
            float displacementSign = impulse.Kind == LiquidSurfaceImpulseKind.BubbleBurst
                ? 1.0f
                : -1.0f;
            float maximumHeight = ComputeMaximumHeight(in coefficients);

            for (int offsetZ = -radiusCells; offsetZ <= radiusCells; offsetZ++)
            {
                int z = centerZ + offsetZ;
                if ((uint)z >= (uint)Depth)
                {
                    continue;
                }

                for (int offsetX = -radiusCells; offsetX <= radiusCells; offsetX++)
                {
                    int x = centerX + offsetX;
                    if ((uint)x >= (uint)Width)
                    {
                        continue;
                    }

                    int index = GetIndex(x, z);
                    if (!IsConnected(centerIndex, index))
                    {
                        continue;
                    }

                    float worldX = OriginWorldX + (x + 0.5f) * CellSize;
                    float worldZ = OriginWorldZ + (z + 0.5f) * CellSize;
                    float dx = worldX - (float)impulse.WorldX;
                    float dz = worldZ - (float)impulse.WorldZ;
                    float weight = ComputeImpulseKernelWeight(
                        dx,
                        dz,
                        radiusWorld,
                        directionX,
                        directionZ,
                        directionalWake);
                    heights[index] = Math.Clamp(
                        heights[index] + displacementSign * resolvedPeakDisplacement * weight,
                        -maximumHeight,
                        maximumHeight);
                }
            }

            if (impulse.Kind == LiquidSurfaceImpulseKind.BubbleBurst)
            {
                transientEmission[centerIndex] = Math.Max(
                    transientEmission[centerIndex],
                    coefficients.BubbleEmissionBoost * couplingEfficiency);
            }
        }

        pendingImpulseCount = 0;
    }

    /// <summary>Publishes one globally sequenced sub-grid packet without modifying resolved energy.</summary>
    /// <param name="impulse">Applied impulse retaining exact position, direction, and identity.</param>
    /// <param name="centerIndex">Active source cell used for uploaded height/normal evidence.</param>
    /// <param name="surfaceClass">Semantic source class exposed to the renderer.</param>
    /// <param name="surfaceEnergyJoules">Energy captured by the liquid interface.</param>
    /// <param name="resolvedEnergyJoules">Energy already deposited in the numerical height field.</param>
    /// <param name="subgridEnergyJoules">
    /// Coarse unresolved budget, including rendered capillary/splash terms and non-rendered wake.
    /// </param>
    /// <param name="renderedPacketEnergyJoules">Energy represented by the linear capillary packet.</param>
    /// <param name="localSplashEnergyJoules">Energy represented by the short cavity/splash geometry.</param>
    /// <param name="wakeEnergyJoules">Non-rendered subsurface/turbulent remainder.</param>
    /// <param name="subgridPeakDisplacement">Energy-derived packet amplitude in world blocks.</param>
    /// <param name="dominantWavelengthMetres">Gravity-capillary carrier wavelength in metres.</param>
    /// <param name="supportRadiusWorldBlocks">Numerically resolvable compact support radius.</param>
    /// <param name="physics">SI liquid properties controlling shader dispersion and damping.</param>
    private void RecordSubgridImpact(
        in PendingImpulse impulse,
        int centerIndex,
        LiquidEntitySurfaceClass surfaceClass,
        float surfaceEnergyJoules,
        float resolvedEnergyJoules,
        float subgridEnergyJoules,
        float renderedPacketEnergyJoules,
        float localSplashEnergyJoules,
        float wakeEnergyJoules,
        float subgridPeakDisplacement,
        float dominantWavelengthMetres,
        float supportRadiusWorldBlocks,
        LiquidSurfacePhysicalProperties physics)
    {
        TotalSubgridImpactCount++;
        lastSubgridImpactSequence = TotalSubgridImpactCount;
        // Exact projectile contacts always construct a positive source sequence before queueing.
        // The only source-less path is the public dropped-item QueueImpact compatibility overload.
        lastSubgridImpactSourceSequence = impulse.SubgridImpact.SourceSequence > 0
            ? impulse.SubgridImpact.SourceSequence
            : TotalDroppedItemImpactCount;
        lastSubgridImpactEntityId = impulse.SubgridImpact.EntityId;
        lastSubgridImpactSurfaceClass = surfaceClass;
        lastSubgridImpactKind = impulse.Kind;
        lastSubgridImpactCellIndex = centerIndex;
        lastSubgridImpactWorldX = impulse.WorldX;
        lastSubgridImpactWorldZ = impulse.WorldZ;
        lastSubgridImpactPeakDisplacement = Math.Max(0.0f, subgridPeakDisplacement);
        bool projectileSplash = (surfaceClass is LiquidEntitySurfaceClass.Projectile
                or LiquidEntitySurfaceClass.ThrownStone)
            && localSplashEnergyJoules > 0.0f;
        lastSubgridImpactSplashRadiusWorldBlocks = projectileSplash
            ? Math.Max(
                CellSize * 0.20f,
                2.0f * impulse.SubgridImpact.CavityRadiusMetres
                    / (float)LiquidPhysicalModel.MetresPerWorldBlock)
            : 0.0f;
        lastSubgridImpactSplashPeakDisplacement = projectileSplash
            ? Math.Min(
                ComputeSubgridSplashPeakDisplacement(
                    localSplashEnergyJoules,
                    physics.DensityKilogramsPerCubicMetre,
                    physics.SurfaceTensionNewtonsPerMetre,
                    lastSubgridImpactSplashRadiusWorldBlocks),
                ComputeMaximumHeight(in dynamics[centerIndex]))
            : 0.0f;
        // The thrown-stone partition is a measured rebound and assigns no local splash energy;
        // therefore projectileSplash can only denote the authored slender-projectile cavity.
        lastSubgridImpactSplashReleaseSeconds = projectileSplash ? 0.045f : 0.0f;
        lastSubgridImpactIncidentEnergyJoules = impulse.Energy;
        lastSubgridImpactSurfaceEnergyJoules = Math.Max(0.0f, surfaceEnergyJoules);
        lastSubgridImpactResolvedEnergyJoules = Math.Clamp(
            resolvedEnergyJoules,
            0.0f,
            lastSubgridImpactSurfaceEnergyJoules);
        lastSubgridImpactEnergyJoules = Math.Clamp(
            subgridEnergyJoules,
            0.0f,
            Math.Max(
                0.0f,
                lastSubgridImpactSurfaceEnergyJoules
                    - lastSubgridImpactResolvedEnergyJoules));
        lastSubgridImpactRenderedEnergyJoules = Math.Clamp(
            renderedPacketEnergyJoules,
            0.0f,
            lastSubgridImpactEnergyJoules);
        lastSubgridImpactLocalSplashEnergyJoules = Math.Clamp(
            localSplashEnergyJoules,
            0.0f,
            Math.Max(
                0.0f,
                lastSubgridImpactEnergyJoules
                    - lastSubgridImpactRenderedEnergyJoules));
        lastSubgridImpactWakeEnergyJoules = Math.Max(
            0.0f,
            lastSubgridImpactEnergyJoules
                - lastSubgridImpactRenderedEnergyJoules
                - lastSubgridImpactLocalSplashEnergyJoules);
        lastSubgridImpactDirectionX = impulse.DirectionX;
        lastSubgridImpactDirectionZ = impulse.DirectionZ;
        lastSubgridImpactVelocityY = impulse.ImpactVelocityY;
        lastSubgridImpactDensity = physics.DensityKilogramsPerCubicMetre;
        lastSubgridImpactDynamicViscosity = physics.DynamicViscosityPascalSeconds;
        lastSubgridImpactSurfaceTension = physics.SurfaceTensionNewtonsPerMetre;
        lastSubgridImpactAdditionalDamping = physics.AdditionalDampingPerSecond;
        lastSubgridImpactWavelength = Math.Max(0.012f, dominantWavelengthMetres);
        LastSubgridImpactDiagnostic = CreateSubgridImpactDiagnostic(
            heights[centerIndex] + bubbleBulge[centerIndex],
            normalX[centerIndex],
            normalZ[centerIndex]);
        subgridImpactHistory[nextSubgridImpactHistoryIndex] =
            LastSubgridImpactDiagnostic;
        nextSubgridImpactHistoryIndex = (nextSubgridImpactHistoryIndex + 1)
            % subgridImpactHistory.Length;
        subgridImpactHistoryCount = Math.Min(
            subgridImpactHistoryCount + 1,
            subgridImpactHistory.Length);
    }

    /// <summary>Builds the current packet diagnostic from persistent identity and energy state.</summary>
    private LiquidSurfaceSubgridImpactDiagnostic CreateSubgridImpactDiagnostic(
        float uploadedHeight,
        float uploadedNormalX,
        float uploadedNormalZ)
    {
        int impactX = lastSubgridImpactCellIndex % Width;
        int impactZ = lastSubgridImpactCellIndex / Width;
        return new LiquidSurfaceSubgridImpactDiagnostic(
            lastSubgridImpactSequence,
            lastSubgridImpactSourceSequence,
            lastSubgridImpactEntityId,
            lastSubgridImpactSurfaceClass,
            lastSubgridImpactKind,
            lastSubgridImpactWorldX,
            lastSubgridImpactWorldZ,
            impactX,
            impactZ,
            (impactX + 0.5f) / Width,
            (impactZ + 0.5f) / Depth,
            lastSubgridImpactPeakDisplacement,
            lastSubgridImpactSplashPeakDisplacement,
            lastSubgridImpactSplashRadiusWorldBlocks,
            lastSubgridImpactSplashReleaseSeconds,
            uploadedHeight,
            uploadedNormalX,
            uploadedNormalZ,
            lastSubgridImpactSurfaceEnergyJoules,
            lastSubgridImpactResolvedEnergyJoules,
            lastSubgridImpactEnergyJoules,
            lastSubgridImpactRenderedEnergyJoules,
            lastSubgridImpactLocalSplashEnergyJoules,
            lastSubgridImpactWakeEnergyJoules,
            lastSubgridImpactDirectionX,
            lastSubgridImpactDirectionZ,
            lastSubgridImpactVelocityY,
            lastSubgridImpactDensity,
            lastSubgridImpactDynamicViscosity,
            lastSubgridImpactSurfaceTension,
            lastSubgridImpactAdditionalDamping,
            lastSubgridImpactWavelength);
    }

    /// <summary>
    /// Converts a compact cavity/splash energy into the peak of f(r)=(1-r^2/R^2)^2. The exact
    /// gravity integral is pi*R^2/5 and its surface-gradient integral is 4*pi/3, so the resulting
    /// amplitude cannot contain more than the supplied sub-grid energy.
    /// </summary>
    /// <param name="energyJoules">Non-negative energy assigned to the local splash.</param>
    /// <param name="densityKilogramsPerCubicMetre">Positive liquid density in kg/m^3.</param>
    /// <param name="surfaceTensionNewtonsPerMetre">Non-negative surface tension in N/m.</param>
    /// <param name="radiusWorldBlocks">Positive resolvable splash radius in world blocks.</param>
    /// <returns>Peak splash displacement in world blocks.</returns>
    internal static float ComputeSubgridSplashPeakDisplacement(
        float energyJoules,
        float densityKilogramsPerCubicMetre,
        float surfaceTensionNewtonsPerMetre,
        float radiusWorldBlocks)
    {
        if (!float.IsFinite(energyJoules)
            || energyJoules <= 0.0f
            || !float.IsFinite(densityKilogramsPerCubicMetre)
            || densityKilogramsPerCubicMetre <= 0.0f
            || !float.IsFinite(surfaceTensionNewtonsPerMetre)
            || surfaceTensionNewtonsPerMetre < 0.0f
            || !float.IsFinite(radiusWorldBlocks)
            || radiusWorldBlocks <= 0.0f)
        {
            return 0.0f;
        }

        double radiusMetres = radiusWorldBlocks
            * LiquidPhysicalModel.MetresPerWorldBlock;
        double gravityShapeIntegralSquareMetres = Math.PI
            * radiusMetres * radiusMetres / 5.0;
        double gradientShapeIntegral = 4.0 * Math.PI / 3.0;
        double stiffnessNewtonsPerMetre = densityKilogramsPerCubicMetre
                * LiquidPhysicalModel.StandardGravityMetresPerSecondSquared
                * gravityShapeIntegralSquareMetres
            + surfaceTensionNewtonsPerMetre * gradientShapeIntegral;
        double amplitudeMetres = Math.Sqrt(2.0 * energyJoules / stiffnessNewtonsPerMetre);
        return (float)(amplitudeMetres / LiquidPhysicalModel.MetresPerWorldBlock);
    }

    /// <summary>Evaluates the compact, energy-normalized spatial impact basis.</summary>
    /// <param name="offsetX">Cell-center X offset from impact in metres/world blocks.</param>
    /// <param name="offsetZ">Cell-center Z offset from impact in metres/world blocks.</param>
    /// <param name="radius">Positive support radius in metres/world blocks.</param>
    /// <param name="directionX">Normalized horizontal motion X.</param>
    /// <param name="directionZ">Normalized horizontal motion Z.</param>
    /// <param name="directionalWake">Whether forward momentum shapes the otherwise radial basis.</param>
    /// <returns>Non-negative dimensionless deposition weight.</returns>
    private static float ComputeImpulseKernelWeight(
        float offsetX,
        float offsetZ,
        float radius,
        float directionX,
        float directionZ,
        bool directionalWake)
    {
        float distanceSquared = offsetX * offsetX + offsetZ * offsetZ;
        if (distanceSquared > radius * radius)
        {
            return 0.0f;
        }

        float distance = MathF.Sqrt(distanceSquared);
        float radial = 1.0f - distance / radius;
        if (!directionalWake || distance <= MinimumMotion)
        {
            return radial * radial;
        }

        float forwardProjection = (offsetX * directionX + offsetZ * directionZ) / distance;
        return radial * radial * Math.Max(0.0f, forwardProjection);
    }

    /// <summary>Resolves the exact compact support used to deposit one surface impulse.</summary>
    /// <param name="coefficients">Validated liquid dynamics at the contact cell.</param>
    /// <param name="cellSize">Positive solver cell size in world blocks.</param>
    /// <returns>Impulse support radius in world blocks.</returns>
    internal static float ResolveImpulseSupportRadius(
        in LiquidSurfaceDynamics coefficients,
        float cellSize) => Math.Max(
            cellSize,
            Math.Min(coefficients.WaveLength * 0.5f, cellSize * 6.0f));

    /// <summary>
    /// Resolves the compact initial support of an exact projectile impact. The dominant liquid
    /// wavelength describes later propagation, not the size of the entry cavity: initial support
    /// instead follows the larger of one grid-resolved cell, physical projectile diameter, and the
    /// class-specific cavity radius. The legacy wavelength support is retained only as an upper bound.
    /// </summary>
    /// <param name="surfaceClass">Exact projectile taxonomy supplied by the physics callback.</param>
    /// <param name="massKilograms">Positive pseudo-mass used by the collision energy model.</param>
    /// <param name="impactEnergyJoules">Non-negative kinetic energy transferred by the collision.</param>
    /// <param name="coefficients">Validated liquid dynamics at the contact cell.</param>
    /// <param name="physics">Validated SI liquid properties at the contact cell.</param>
    /// <param name="cellSize">Positive solver cell size in world blocks.</param>
    /// <returns>Finite support radius in world blocks, bounded by resolution and legacy support.</returns>
    internal static float ResolveProjectileImpulseSupportRadius(
        LiquidEntitySurfaceClass surfaceClass,
        float massKilograms,
        float impactEnergyJoules,
        in LiquidSurfaceDynamics coefficients,
        in LiquidSurfacePhysicalProperties physics,
        float cellSize)
    {
        float legacySupport = ResolveImpulseSupportRadius(in coefficients, cellSize);
        if (surfaceClass is not LiquidEntitySurfaceClass.ThrownStone
            and not LiquidEntitySurfaceClass.Projectile
            || !float.IsFinite(massKilograms)
            || massKilograms <= 0.0f
            || !float.IsFinite(impactEnergyJoules)
            || impactEnergyJoules < 0.0f
            || !float.IsFinite(physics.DensityKilogramsPerCubicMetre)
            || physics.DensityKilogramsPerCubicMetre <= 0.0f)
        {
            return legacySupport;
        }

        LiquidProjectileWaveEnergyPartition partition = ResolveProjectileWaveEnergyPartition(
            surfaceClass,
            massKilograms,
            impactEnergyJoules,
            in physics,
            cellSize);
        double metresPerWorldBlock = LiquidPhysicalModel.MetresPerWorldBlock;
        double physicalDiameterWorldBlocks = ResolveProjectilePhysicalDiameterMetres(
            surfaceClass,
            massKilograms)
            / metresPerWorldBlock;
        double physicalSupportWorldBlocks = Math.Max(
            physicalDiameterWorldBlocks,
            partition.CavityRadiusMetres / metresPerWorldBlock);
        return (float)Math.Min(
            legacySupport,
            Math.Max(cellSize, physicalSupportWorldBlocks));
    }

    /// <summary>
    /// Partitions an exact projectile's kinetic loss without treating it all as a resolvable gravity
    /// wave. Point-first shaft entries use bounded axial drag through one resolution-depth column,
    /// then isolate the much smaller near-interface share and a slender-body cavity. The cavity's
    /// gravitational potential plus interface energy is the reversible collapse-wave budget;
    /// spray/jet energy remains local and the deeper drag loss remains wake. Measured stone
    /// rebounds already expose their surface-energy ceiling and use a spherical cavity balance.
    /// The spectral projection f=1-exp(-(k_max*R_c)^2) retains only grid-resolvable energy.
    /// </summary>
    /// <param name="surfaceClass">Thrown-stone or elongated-projectile taxonomy.</param>
    /// <param name="massKilograms">Positive projectile pseudo-mass in kilograms.</param>
    /// <param name="impactEnergyJoules">Non-negative incident/lost kinetic energy in joules.</param>
    /// <param name="physics">Validated liquid density and surface tension in SI.</param>
    /// <param name="cellSize">Positive solver sample spacing in world blocks.</param>
    /// <returns>A bounded energy ledger plus class-specific cavity radius.</returns>
    internal static LiquidProjectileWaveEnergyPartition ResolveProjectileWaveEnergyPartition(
        LiquidEntitySurfaceClass surfaceClass,
        float massKilograms,
        float impactEnergyJoules,
        in LiquidSurfacePhysicalProperties physics,
        float cellSize)
    {
        if (surfaceClass is not LiquidEntitySurfaceClass.ThrownStone
            and not LiquidEntitySurfaceClass.Projectile
            || !float.IsFinite(massKilograms)
            || massKilograms <= 0.0f
            || !float.IsFinite(impactEnergyJoules)
            || impactEnergyJoules < 0.0f
            || !float.IsFinite(cellSize)
            || cellSize <= 0.0f
            || !float.IsFinite(physics.DensityKilogramsPerCubicMetre)
            || physics.DensityKilogramsPerCubicMetre <= 0.0f
            || !float.IsFinite(physics.SurfaceTensionNewtonsPerMetre)
            || physics.SurfaceTensionNewtonsPerMetre < 0.0f)
        {
            return default;
        }

        double sampleSpacingMetres = cellSize * LiquidPhysicalModel.MetresPerWorldBlock;
        // Require four samples per wavelength for a conservative, directionally stable band;
        // the two-sample Nyquist limit is not a credible visual-wave reconstruction target.
        double maximumResolvedWaveNumberPerMetre = Math.PI / (2.0 * sampleSpacingMetres);
        double surfaceCoupledEnergyJoules;
        double cavityRadiusMetres;
        double nearInterfaceEnergyJoules;
        double cavityEnergyJoules;
        double capillaryPacketEnergyJoules;
        double splashEnergyJoules;
        if (surfaceClass == LiquidEntitySurfaceClass.ThrownStone)
        {
            // Normal kinetic-energy loss is measured across the authoritative rebound itself;
            // applying a second drag closure would count the same transfer twice.
            surfaceCoupledEnergyJoules = impactEnergyJoules;
            cavityRadiusMetres = ResolveSphericalCavityRadiusMetres(
                surfaceCoupledEnergyJoules,
                physics.DensityKilogramsPerCubicMetre,
                physics.SurfaceTensionNewtonsPerMetre);
            nearInterfaceEnergyJoules = surfaceCoupledEnergyJoules;
            cavityEnergyJoules = 0.0;
            capillaryPacketEnergyJoules = 0.0;
            splashEnergyJoules = 0.0;
        }
        else
        {
            double shaftRadiusMetres = ReferenceArrowShaftDiameterMetres * 0.5;
            double frontalAreaSquareMetres = Math.PI
                * shaftRadiusMetres * shaftRadiusMetres;
            double dragOpticalDepth = physics.DensityKilogramsPerCubicMetre
                * ReferenceArrowAxialDragCoefficient
                * frontalAreaSquareMetres
                / (massKilograms * maximumResolvedWaveNumberPerMetre);
            double surfaceCaptureFraction = 1.0 - Math.Exp(-dragOpticalDepth);
            surfaceCoupledEnergyJoules = impactEnergyJoules
                * Math.Clamp(surfaceCaptureFraction, 0.0, 1.0);
            // Slender point-first cavities remain tied to shaft radius rather than the energy-scale
            // radius appropriate to a sphere. 1.15 is the centre of the bounded 1.0..1.3 ratio.
            cavityRadiusMetres = Math.Min(0.006, shaftRadiusMetres * 1.15);
            double resolvedPenetrationDepthMetres = 1.0
                / maximumResolvedWaveNumberPerMetre;
            double columnCaptureRatio = impactEnergyJoules > 0.0f
                ? Math.Clamp(surfaceCoupledEnergyJoules / impactEnergyJoules, 0.0, 1.0)
                : 0.0;
            double nearInterfaceCaptureFraction = 1.0 - Math.Pow(
                1.0 - columnCaptureRatio,
                cavityRadiusMetres / resolvedPenetrationDepthMetres);
            nearInterfaceEnergyJoules = impactEnergyJoules
                * Math.Clamp(nearInterfaceCaptureFraction, 0.0, 1.0);

            // A point-first arrow forms a slender 0.185..0.235 m cavity before pinch-off.
            // The central 0.210 m length yields 12..16 ml for the bounded shaft radius.
            const double representativeCavityLengthMetres = 0.210;
            double cavityVolumeCubicMetres = Math.PI
                * cavityRadiusMetres * cavityRadiusMetres
                * representativeCavityLengthMetres;
            cavityEnergyJoules = physics.DensityKilogramsPerCubicMetre
                * LiquidPhysicalModel.StandardGravityMetresPerSecondSquared
                * cavityVolumeCubicMetres
                * representativeCavityLengthMetres * 0.5;
            capillaryPacketEnergyJoules = 2.0 * Math.PI
                * cavityRadiusMetres
                * representativeCavityLengthMetres
                * physics.SurfaceTensionNewtonsPerMetre;
            cavityEnergyJoules = Math.Min(
                cavityEnergyJoules,
                nearInterfaceEnergyJoules);
            capillaryPacketEnergyJoules = Math.Min(
                capillaryPacketEnergyJoules,
                Math.Max(0.0, nearInterfaceEnergyJoules - cavityEnergyJoules));
            splashEnergyJoules = Math.Max(
                0.0,
                nearInterfaceEnergyJoules
                    - cavityEnergyJoules
                    - capillaryPacketEnergyJoules);
        }

        double resolvedFraction = 1.0 - Math.Exp(-Math.Pow(
            maximumResolvedWaveNumberPerMetre * cavityRadiusMetres,
            2.0));
        resolvedFraction = Math.Clamp(resolvedFraction, 0.0, 1.0);
        double resolvedWaveEnergyJoules = Math.Min(
            surfaceCoupledEnergyJoules,
            surfaceCoupledEnergyJoules * resolvedFraction);
        double subgridWaveEnergyJoules = Math.Max(
            0.0,
            surfaceCoupledEnergyJoules - resolvedWaveEnergyJoules);
        if (surfaceClass == LiquidEntitySurfaceClass.ThrownStone)
        {
            capillaryPacketEnergyJoules = subgridWaveEnergyJoules;
        }
        double wakeEnergyJoules = Math.Max(
            0.0,
            subgridWaveEnergyJoules
                - cavityEnergyJoules
                - capillaryPacketEnergyJoules
                - splashEnergyJoules);
        return new LiquidProjectileWaveEnergyPartition(
            (float)surfaceCoupledEnergyJoules,
            (float)resolvedWaveEnergyJoules,
            (float)subgridWaveEnergyJoules,
            (float)cavityRadiusMetres,
            (float)resolvedFraction,
            (float)nearInterfaceEnergyJoules,
            (float)cavityEnergyJoules,
            (float)capillaryPacketEnergyJoules,
            (float)splashEnergyJoules,
            (float)wakeEnergyJoules);
    }

    /// <summary>
    /// Maps a physical entry-cavity radius to the unresolved carrier wavelength while keeping it
    /// at or below the solver's conservative four-cell cutoff.
    /// </summary>
    /// <param name="cavityRadiusMetres">Non-negative class-specific cavity radius in metres.</param>
    /// <param name="cellSize">Positive solver sample spacing in world blocks.</param>
    /// <returns>Finite gravity-capillary packet wavelength in metres.</returns>
    internal static float ResolveProjectileSubgridWavelengthMetres(
        float cavityRadiusMetres,
        float cellSize)
    {
        if (!float.IsFinite(cavityRadiusMetres)
            || cavityRadiusMetres < 0.0f
            || !float.IsFinite(cellSize)
            || cellSize <= 0.0f)
        {
            return 0.012f;
        }

        double cutoffWavelengthMetres = 4.0
            * cellSize
            * LiquidPhysicalModel.MetresPerWorldBlock;
        double cavityWavelengthMetres = 4.0 * Math.Max(0.0f, cavityRadiusMetres);
        return (float)Math.Clamp(
            cavityWavelengthMetres,
            0.012,
            Math.Max(0.012, cutoffWavelengthMetres));
    }

    /// <summary>Solves E=(pi/4)*rho*g*R^4+pi*sigma*R^2 for a spherical-entry cavity radius.</summary>
    private static double ResolveSphericalCavityRadiusMetres(
        double surfaceEnergyJoules,
        double densityKilogramsPerCubicMetre,
        double surfaceTensionNewtonsPerMetre)
    {
        if (surfaceEnergyJoules <= 0.0)
        {
            return 0.0;
        }

        double gravityCoefficient = Math.PI * 0.25
            * densityKilogramsPerCubicMetre
            * LiquidPhysicalModel.StandardGravityMetresPerSecondSquared;
        double tensionCoefficient = Math.PI * surfaceTensionNewtonsPerMetre;
        double discriminantRoot = Math.Sqrt(
            tensionCoefficient * tensionCoefficient
            + 4.0 * gravityCoefficient * surfaceEnergyJoules);
        double radiusSquared = 2.0 * surfaceEnergyJoules
            / (tensionCoefficient + discriminantRoot);
        return Math.Sqrt(Math.Max(0.0, radiusSquared));
    }

    /// <summary>Returns a class-specific physical diameter used only to bound initial support.</summary>
    private static double ResolveProjectilePhysicalDiameterMetres(
        LiquidEntitySurfaceClass surfaceClass,
        double massKilograms)
    {
        if (surfaceClass == LiquidEntitySurfaceClass.Projectile)
        {
            return ReferenceArrowShaftDiameterMetres;
        }

        return 2.0 * Math.Cbrt(
            3.0 * massKilograms
            / (4.0 * Math.PI * ReferenceGraniteDensityKilogramsPerCubicMetre));
    }

    /// <summary>Converts rainfall depth flux and exposed area into representative drop impact energy.</summary>
    /// <param name="forcing">Rainfall depth rate in m/s; wind fields are ignored here.</param>
    private void ApplyRain(in LiquidSurfaceForcing forcing)
    {
        float rainfallRateMetresPerSecond = float.IsFinite(forcing.RainfallRateMetresPerSecond)
            ? Math.Max(0.0f, forcing.RainfallRateMetresPerSecond)
            : 0.0f;
        if (rainfallRateMetresPerSecond <= 0.0f)
        {
            return;
        }

        EnsureActiveTopology();
        int exposedCellCount = rainExposedCellCount;
        if (exposedCellCount == 0)
        {
            return;
        }

        double dropRadiusMetres = RepresentativeRainDropDiameterMetres * 0.5;
        double dropVolumeCubicMetres = 4.0 / 3.0 * Math.PI
            * dropRadiusMetres * dropRadiusMetres * dropRadiusMetres;
        double exposedAreaSquareMetres = exposedCellCount * CellSize * CellSize
            * LiquidPhysicalModel.MetresPerWorldBlock
            * LiquidPhysicalModel.MetresPerWorldBlock;
        rainDropAccumulator += (float)(rainfallRateMetresPerSecond
            * exposedAreaSquareMetres
            * FixedStepSeconds
            / dropVolumeCubicMetres);
        int physicalDropCount = (int)Math.Min(MathF.Floor(rainDropAccumulator), int.MaxValue);
        rainDropAccumulator -= physicalDropCount;
        if (physicalDropCount <= 0)
        {
            return;
        }

        int availableEvents = pendingImpulses.Length - pendingImpulseCount;
        int representativeCount = Math.Min(
            physicalDropCount,
            Math.Min(availableEvents, MaximumRainImpulsesPerStep));
        if (representativeCount <= 0)
        {
            DroppedEventCount += physicalDropCount;
            return;
        }

        double dropMassKilograms = RainWaterDensityKilogramsPerCubicMetre
            * dropVolumeCubicMetres;
        double singleDropEnergyJoules = LiquidPhysicalModel.KineticEnergyJoules(
            dropMassKilograms,
            RepresentativeRainDropVelocityMetresPerSecond);
        float representativeEnergyJoules = (float)(singleDropEnergyJoules
            * physicalDropCount / representativeCount);
        for (int drop = 0; drop < representativeCount; drop++)
        {
            int start = (int)(NextRandomUInt() % (uint)heights.Length);
            int index = rainSuccessors[start];
            int x = index % Width;
            int z = index / Width;
            float jitterX = NextRandomUnit();
            float jitterZ = NextRandomUnit();
            QueueImpulseAtCell(
                index,
                OriginWorldX + (x + jitterX) * CellSize,
                OriginWorldZ + (z + jitterZ) * CellSize,
                representativeEnergyJoules,
                0.0f,
                0.0f,
                LiquidSurfaceImpulseKind.Rain);

        }
    }

    /// <summary>
    /// Drives a narrow directional spectrum from wind kinetic-energy flux over one wave period.
    /// One dominant train preserves wind direction while two shorter oblique components represent
    /// measured directional spreading and break the nonphysical perfectly parallel crest pattern.
    /// </summary>
    /// <param name="forcing">Finite world-space horizontal wind velocity in m/s.</param>
    private void ApplyWind(in LiquidSurfaceForcing forcing)
    {
        float windX = float.IsFinite(forcing.WindVelocityXMetresPerSecond)
            ? forcing.WindVelocityXMetresPerSecond
            : 0.0f;
        float windZ = float.IsFinite(forcing.WindVelocityZMetresPerSecond)
            ? forcing.WindVelocityZMetresPerSecond
            : 0.0f;
        float windLength = MathF.Sqrt(windX * windX + windZ * windZ);
        if (windLength <= MinimumMotion)
        {
            cachedWindX = windX;
            cachedWindZ = windZ;
            return;
        }

        float directionX = windX / windLength;
        float directionZ = windZ / windLength;
        if (windForcingCacheDirty || windX != cachedWindX || windZ != cachedWindZ)
        {
            RefreshWindForcingCache(
                windX,
                windZ,
                windLength,
                directionX,
                directionZ);
        }

        float simulationTime = stepIndex * FixedStepSeconds;
        EnsureActiveTopology();
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
            int index = activeStencils[ordinal].Center;
            ref readonly CellWindCoefficients cached =
                ref cachedWindCoefficients[index];
            if (cached.AngularFrequencySquared <= 0.0f)
            {
                continue;
            }

            float basePhaseTime = simulationTime * cached.PhaseSpeedOverWavelength;
            float phase = 2.0f * MathF.PI
                * (cachedWindSpatialPhases[index] - basePhaseTime);
            float spreadPhaseA = 2.0f * MathF.PI
                * (cachedWindSpreadPhasesA[index]
                    - basePhaseTime * MathF.Sqrt(1.0f / 0.73f));
            float spreadPhaseB = 2.0f * MathF.PI
                * (cachedWindSpreadPhasesB[index]
                    - basePhaseTime * MathF.Sqrt(1.0f / 0.47f));
            float targetHeight = cachedWindAmplitudes[index]
                * (0.57f * MathF.Sin(phase)
                    + 0.28f * MathF.Sin(spreadPhaseA)
                    + 0.15f * MathF.Sin(spreadPhaseB));
            float forcingAcceleration = cached.AngularFrequencySquared
                * (targetHeight - heights[index]);
            velocities[index] += forcingAcceleration * FixedStepSeconds;
        }
    }

    /// <summary>
    /// Advances rise/burst lifecycles, queues terminal impulses/emission, accumulates active liquid area
    /// by profile, then performs bounded Poisson-style spawning independent of cell resolution.
    /// </summary>
    private void AdvanceBubbles()
    {
        Array.Clear(bubbleBulge);
        for (int bubbleIndex = 0; bubbleIndex < bubbles.Length; bubbleIndex++)
        {
            ref BubbleState bubble = ref bubbles[bubbleIndex];
            if (!bubble.Active)
            {
                continue;
            }

            bubble.Age += FixedStepSeconds;
            if (bubble.Bursting)
            {
                if (bubble.Age >= bubble.Duration)
                {
                    bubble = default;
                    activeBubbleCount--;
                }

                continue;
            }

            if (bubble.Age < bubble.Duration)
            {
                float progress = Math.Clamp(bubble.Age / bubble.Duration, 0.0f, 1.0f);
                float smoothProgress = progress * progress * (3.0f - 2.0f * progress);
                bubbleBulge[bubble.CellIndex] += bubble.Radius * smoothProgress;
                continue;
            }

            if (IsActive(bubble.CellIndex))
            {
                LiquidSurfaceDynamics coefficients = dynamics[bubble.CellIndex];
                LiquidSurfacePhysicalProperties physics = physicalProperties[bubble.CellIndex];
                int x = bubble.CellIndex % Width;
                int z = bubble.CellIndex / Width;
                // Surface energy of a spherical interface is sigma*4*pi*r^2.
                // BubbleBurstStrength is applied later as the coupled fraction.
                float bubbleSurfaceEnergyJoules = physics.SurfaceTensionNewtonsPerMetre
                    * 4.0f * MathF.PI * bubble.Radius * bubble.Radius;
                QueueImpulseAtCell(
                    bubble.CellIndex,
                    OriginWorldX + (x + bubble.OffsetX) * CellSize,
                    OriginWorldZ + (z + bubble.OffsetZ) * CellSize,
                    bubbleSurfaceEnergyJoules,
                    0.0f,
                    0.0f,
                    LiquidSurfaceImpulseKind.BubbleBurst);
            }

            bubble.Bursting = true;
            bubble.Age = 0.0f;
            bubble.Duration = Math.Max(
                FixedStepSeconds,
                bubble.Duration * BubbleBurstDecayFraction);
        }

        EnsureActiveTopology();

        int spawnedThisStep = 0;
        for (int profileId = 1;
            profileId < LiquidOpticalRegistry.UnknownLiquidProfileId
            && activeBubbleCount < bubbles.Length
            && spawnedThisStep < MaximumBubbleSpawnsPerStep;
            profileId++)
        {
            int cellCount = bubbleCellCountByProfile[profileId];
            if (cellCount == 0)
            {
                continue;
            }

            int representativeIndex = FindProfileCell(profileId, 0);
            LiquidSurfaceDynamics coefficients = dynamics[representativeIndex];
            float expectedThisStep = coefficients.BubbleRate
                * (bubbleAreaByProfile[profileId] / BubbleRateUnitArea)
                * FixedStepSeconds;
            float probability = 1.0f - MathF.Exp(-expectedThisStep);
            if (NextRandomUnit() >= probability)
            {
                continue;
            }

            int ordinal = (int)(NextRandomUInt() % (uint)cellCount);
            int cellIndex = FindProfileCell(profileId, ordinal);
            SpawnBubble(cellIndex, in coefficients);
            spawnedThisStep++;
        }
    }

    /// <summary>Initializes the first free bubble slot with deterministic position/radius samples.</summary>
    /// <param name="cellIndex">Active host cell.</param>
    /// <param name="coefficients">Authored radius interval and rise duration.</param>
    private void SpawnBubble(int cellIndex, in LiquidSurfaceDynamics coefficients)
    {
        for (int bubbleIndex = 0; bubbleIndex < bubbles.Length; bubbleIndex++)
        {
            ref BubbleState bubble = ref bubbles[bubbleIndex];
            if (bubble.Active)
            {
                continue;
            }

            float radiusRange = Math.Max(
                0.0f,
                coefficients.BubbleRadiusMaximum - coefficients.BubbleRadiusMinimum);
            bubble.Active = true;
            bubble.CellIndex = cellIndex;
            bubble.Radius = coefficients.BubbleRadiusMinimum + radiusRange * NextRandomUnit();
            bubble.Duration = coefficients.BubbleRiseDuration;
            bubble.OffsetX = NextRandomUnit();
            bubble.OffsetZ = NextRandomUnit();
            bubble.Age = 0.0f;
            activeBubbleCount++;
            TotalBubbleSpawnCount++;
            return;
        }
    }

    /// <summary>Selects the ordinal active cell carrying one byte profile ID.</summary>
    /// <param name="profileId">Registry row in 1..254.</param>
    /// <param name="ordinal">Zero-based ordinal known to be below the accumulated profile count.</param>
    /// <returns>Flat grid index; zero is a defensive fallback for inconsistent counts.</returns>
    private int FindProfileCell(int profileId, int ordinal)
    {
        EnsureActiveTopology();
        if ((uint)profileId >= (uint)profileCellCounts.Length
            || (uint)ordinal >= (uint)profileCellCounts[profileId]) return 0;
        return profileCellIndices[profileCellOffsets[profileId] + ordinal];
    }

    /// <summary>Compares consecutive entity observations to classify one-shot crossings and periodic wakes.</summary>
    /// <param name="previous">Mutable tracker state from the prior observation.</param>
    /// <param name="current">Current detached entity observation.</param>
    private void DetectEntitySurfaceEvent(
        ref EntityTracker previous,
        in LiquidEntitySurfaceSample current)
    {
        if (!TryWorldToCell(current.WorldX, current.WorldZ, out int x, out int z))
        {
            return;
        }

        int index = GetIndex(x, z);
        if (!IsActive(index))
        {
            return;
        }

        float surfaceY = surfaceWorldY[index] + heights[index];
        bool enteredLiquid = !previous.FeetInLiquid && current.FeetInLiquid;
        bool crossedSurface = previous.WorldY > surfaceY
            && current.WorldY <= surfaceY + CellSize * 0.15f;
        if (!float.IsFinite(current.MassKilograms)
            || current.MassKilograms <= 0.0f
            || !float.IsFinite(current.MotionSamplePeriodSeconds)
            || current.MotionSamplePeriodSeconds <= 0.0f)
        {
            return;
        }

        int impactIndex = index;
        double impactWorldX = current.WorldX;
        double impactWorldZ = current.WorldZ;
        bool resolvedCrossing = crossedSurface
            && TryResolveSurfaceCrossing(
                in previous,
                in current,
                index,
                out impactIndex,
                out impactWorldX,
                out impactWorldZ);

        float motionToMetresPerSecond = (float)LiquidPhysicalModel.MetresPerWorldBlock
            / current.MotionSamplePeriodSeconds;
        bool useRetainedIncidentMotion = current.SurfaceClass == LiquidEntitySurfaceClass.DroppedItem
            && resolvedCrossing
            && previous.HasLastAirMotion
            && previous.LastAirMotionY < current.MotionY;
        float velocityX = (useRetainedIncidentMotion ? previous.LastAirMotionX : current.MotionX)
            * motionToMetresPerSecond;
        float velocityY = (useRetainedIncidentMotion ? previous.LastAirMotionY : current.MotionY)
            * motionToMetresPerSecond;
        float velocityZ = (useRetainedIncidentMotion ? previous.LastAirMotionZ : current.MotionZ)
            * motionToMetresPerSecond;
        float horizontalSpeed = MathF.Sqrt(
            velocityX * velocityX + velocityZ * velocityZ);
        float speed = MathF.Sqrt(
            horizontalSpeed * horizontalSpeed + velocityY * velocityY);
        float impactSpeed = current.SurfaceClass == LiquidEntitySurfaceClass.DroppedItem
            ? Math.Max(
                speed,
                LiquidSurfaceWorldInputs.MinimumDroppedItemImpactSpeedMetresPerSecond)
            : speed;
        // Preserve the measured horizontal motion and encode any minimum visible-entry floor in
        // the descending component, so the diagnostic vector exactly reconstructs queued energy.
        float impactVelocityY = current.SurfaceClass == LiquidEntitySurfaceClass.DroppedItem
            ? -MathF.Sqrt(Math.Max(0.0f, impactSpeed * impactSpeed - horizontalSpeed * horizontalSpeed))
            : velocityY;
        float kineticEnergyJoules = (float)LiquidPhysicalModel.KineticEnergyJoules(
            current.MassKilograms,
            impactSpeed);

        if (current.SurfaceClass == LiquidEntitySurfaceClass.ThrownStone
            && resolvedCrossing
            && velocityY < -MinimumMotion
            && horizontalSpeed > Math.Abs(velocityY))
        {
            if (previous.HasImpactedSurface)
            {
                return;
            }

            if (previous.HasLiquidCollision
                && stepIndex - previous.LastLiquidCollisionStep < WakeStepInterval)
            {
                return;
            }

            bool queued = QueueImpulseAtCell(
                impactIndex,
                impactWorldX,
                impactWorldZ,
                kineticEnergyJoules,
                velocityX,
                velocityZ,
                LiquidSurfaceImpulseKind.StoneRicochet);
            if (queued)
            {
                previous.LastLiquidCollisionStep = stepIndex;
                previous.LastLiquidCollisionWorldX = impactWorldX;
                previous.LastLiquidCollisionWorldZ = impactWorldZ;
                previous.HasLiquidCollision = true;
            }
            return;
        }

        bool entryEvent = current.SurfaceClass == LiquidEntitySurfaceClass.DroppedItem
            ? resolvedCrossing
            : enteredLiquid || resolvedCrossing;
        if (entryEvent)
        {
            if (current.SurfaceClass is LiquidEntitySurfaceClass.DroppedItem
                    or LiquidEntitySurfaceClass.Projectile
                    or LiquidEntitySurfaceClass.ThrownStone
                && previous.HasImpactedSurface)
            {
                // Passive-physics buoyancy can alternate FeetInLiquid while a dropped stack or
                // embedded projectile settles. It is one entry, not repeated full-energy impacts.
                return;
            }

            LiquidSurfaceImpulseKind kind = current.SurfaceClass switch
            {
                LiquidEntitySurfaceClass.Bobber => LiquidSurfaceImpulseKind.Bobber,
                LiquidEntitySurfaceClass.DroppedItem => LiquidSurfaceImpulseKind.DroppedItemEntry,
                _ => LiquidSurfaceImpulseKind.GenericEntry
            };
            PendingSubgridImpact subgridImpact = kind == LiquidSurfaceImpulseKind.DroppedItemEntry
                ? new PendingSubgridImpact(
                    TotalDroppedItemImpactCount + 1,
                    current.EntityId,
                    current.SurfaceClass,
                    0.0f,
                    0.0f,
                    0.0f,
                    0.0f,
                    0.0f,
                    0.0f,
                    0.0f)
                : default;
            bool queued = QueueImpulseAtCell(
                resolvedCrossing ? impactIndex : index,
                resolvedCrossing ? impactWorldX : current.WorldX,
                resolvedCrossing ? impactWorldZ : current.WorldZ,
                kineticEnergyJoules,
                velocityX,
                velocityZ,
                kind,
                impactVelocityY,
                subgridImpact: subgridImpact);
            if (queued && current.SurfaceClass is LiquidEntitySurfaceClass.DroppedItem
                    or LiquidEntitySurfaceClass.Projectile
                    or LiquidEntitySurfaceClass.ThrownStone)
            {
                previous.HasImpactedSurface = true;
            }
            return;
        }

        bool nearSurface = Math.Abs(current.WorldY - surfaceY) <= CellSize * 0.45f;
        bool wakeIntervalElapsed = stepIndex - previous.LastWakeStep >= WakeStepInterval;
        if (!nearSurface || !wakeIntervalElapsed || horizontalSpeed <= MinimumMotion)
        {
            return;
        }

        LiquidSurfaceImpulseKind? wakeKind = current.SurfaceClass switch
        {
            LiquidEntitySurfaceClass.Bobber => LiquidSurfaceImpulseKind.Bobber,
            LiquidEntitySurfaceClass.Fish => LiquidSurfaceImpulseKind.NearSurfaceFish,
            _ when current.Swimming => LiquidSurfaceImpulseKind.SwimmingWake,
            _ => null
        };
        if (!wakeKind.HasValue)
        {
            return;
        }

        if (QueueImpulseAtCell(
                index,
                current.WorldX,
                current.WorldZ,
                (float)LiquidPhysicalModel.KineticEnergyJoules(
                    current.MassKilograms,
                    horizontalSpeed),
                velocityX,
                velocityZ,
                wakeKind.Value))
        {
            previous.LastWakeStep = stepIndex;
        }
    }

    /// <summary>
    /// Intersects the observed world-space segment with the engine-aligned liquid plane and remaps
    /// the result to its exact sub-block cell. A second solve handles a crossing that enters a
    /// neighbouring column with a different authored surface height.
    /// </summary>
    /// <param name="previous">Previous detached entity observation.</param>
    /// <param name="current">Current detached entity observation.</param>
    /// <param name="initialCellIndex">Active cell below the current observation.</param>
    /// <param name="impactCellIndex">Resolved active impact cell.</param>
    /// <param name="impactWorldX">Interpolated world X at surface crossing.</param>
    /// <param name="impactWorldZ">Interpolated world Z at surface crossing.</param>
    /// <returns>Whether a finite descending segment crosses an active liquid surface.</returns>
    private bool TryResolveSurfaceCrossing(
        in EntityTracker previous,
        in LiquidEntitySurfaceSample current,
        int initialCellIndex,
        out int impactCellIndex,
        out double impactWorldX,
        out double impactWorldZ)
    {
        impactCellIndex = initialCellIndex;
        impactWorldX = current.WorldX;
        impactWorldZ = current.WorldZ;
        double verticalTravel = previous.WorldY - current.WorldY;
        if (!double.IsFinite(verticalTravel) || verticalTravel <= 1.0e-8)
        {
            return false;
        }

        for (int iteration = 0; iteration < 2; iteration++)
        {
            float surfaceY = surfaceWorldY[impactCellIndex] + heights[impactCellIndex];
            double crossingFraction = (previous.WorldY - surfaceY) / verticalTravel;
            // DetectEntitySurfaceEvent establishes a finite descending segment whose endpoints
            // straddle this plane (with the authored 15% surface tolerance), so the quotient is
            // finite and belongs to that segment. Saturation retains the exact endpoint contract
            // across floating-point roundoff without a second, logically redundant rejection.
            crossingFraction = Math.Clamp(crossingFraction, 0.0, 1.0);
            impactWorldX = previous.WorldX
                + (current.WorldX - previous.WorldX) * crossingFraction;
            impactWorldZ = previous.WorldZ
                + (current.WorldZ - previous.WorldZ) * crossingFraction;
            if (!TryWorldToCell(impactWorldX, impactWorldZ, out int x, out int z))
            {
                return false;
            }

            int remappedIndex = GetIndex(x, z);
            if (!IsActive(remappedIndex))
            {
                return false;
            }
            if (remappedIndex == impactCellIndex)
            {
                return true;
            }
            impactCellIndex = remappedIndex;
        }

        return true;
    }

    /// <summary>
    /// Recovers an entry that happened between solver creation and its first 30 Hz entity sample.
    /// A dropped item already inside the narrow free-surface band receives exactly one minimum
    /// stone-reference impulse when its tracker is created; deeply submerged/resting items do not.
    /// </summary>
    /// <param name="current">First detached observation of one dropped item.</param>
    /// <returns>Whether the bounded solver accepted the recovered entry impulse.</returns>
    private bool DetectInitiallyObservedDroppedItem(in LiquidEntitySurfaceSample current)
    {
        if (!TryWorldToCell(current.WorldX, current.WorldZ, out int x, out int z))
        {
            return false;
        }

        int index = GetIndex(x, z);
        if (!IsActive(index)
            || !float.IsFinite(current.MassKilograms)
            || current.MassKilograms <= 0.0f
            || !float.IsFinite(current.MotionSamplePeriodSeconds)
            || current.MotionSamplePeriodSeconds <= 0.0f)
        {
            return false;
        }

        float surfaceY = surfaceWorldY[index] + heights[index];
        bool insideEntryBand = current.WorldY <= surfaceY + CellSize * 0.30f
            && current.WorldY >= surfaceY - CellSize * 1.10f;
        if (!insideEntryBand)
        {
            return false;
        }

        float motionScale = (float)LiquidPhysicalModel.MetresPerWorldBlock
            / current.MotionSamplePeriodSeconds;
        float velocityX = current.MotionX * motionScale;
        float velocityY = current.MotionY * motionScale;
        float velocityZ = current.MotionZ * motionScale;
        float observedSpeed = MathF.Sqrt(
            velocityX * velocityX
            + velocityY * velocityY
            + velocityZ * velocityZ);
        float impactSpeed = Math.Max(
            observedSpeed,
            LiquidSurfaceWorldInputs.MinimumDroppedItemImpactSpeedMetresPerSecond);
        float horizontalSpeedSquared = velocityX * velocityX + velocityZ * velocityZ;
        // The first-observation recovery follows the same energy/vector identity as a tracked crossing.
        float impactVelocityY = -MathF.Sqrt(Math.Max(
            0.0f,
            impactSpeed * impactSpeed - horizontalSpeedSquared));
        if (observedSpeed < MinimumInitiallyObservedEntrySpeedMetresPerSecond)
        {
            return false;
        }
        return QueueImpulseAtCell(
            index,
            current.WorldX,
            current.WorldZ,
            (float)LiquidPhysicalModel.KineticEnergyJoules(
                current.MassKilograms,
                impactSpeed),
            velocityX,
            velocityZ,
            LiquidSurfaceImpulseKind.DroppedItemEntry,
            impactVelocityY,
            subgridImpact: new PendingSubgridImpact(
                TotalDroppedItemImpactCount + 1,
                current.EntityId,
                current.SurfaceClass,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                0.0f));
    }

    /// <summary>Appends a validated impulse to fixed storage and counts overflow rather than allocating.</summary>
    /// <param name="cellIndex">Known active flat cell index.</param>
    /// <param name="worldX">Impact X in world blocks.</param>
    /// <param name="worldZ">Impact Z in world blocks.</param>
    /// <param name="energy">Event intensity clamped non-negative.</param>
    /// <param name="directionX">Horizontal motion X.</param>
    /// <param name="directionZ">Horizontal motion Z.</param>
    /// <param name="kind">Semantic event class.</param>
    /// <param name="impactVelocityY">Vertical impact velocity retained for dropped-item energy evidence.</param>
    /// <param name="supportRadiusWorldBlocks">
    /// Optional class-specific initial support; zero retains the liquid-wavelength support.
    /// </param>
    /// <param name="couplingEfficiencyOverride">
    /// Optional already-partitioned energy coupling in 0..1; negative retains material response.
    /// </param>
    /// <param name="subgridImpact">
    /// Optional identity plus resolved/rendered/wake ledger for procedural packet rendering.
    /// </param>
    /// <returns>Whether fixed queue capacity remained.</returns>
    private bool QueueImpulseAtCell(
        int cellIndex,
        double worldX,
        double worldZ,
        float energy,
        float directionX,
        float directionZ,
        LiquidSurfaceImpulseKind kind,
        float impactVelocityY = 0.0f,
        float supportRadiusWorldBlocks = 0.0f,
        float couplingEfficiencyOverride = -1.0f,
        PendingSubgridImpact subgridImpact = default)
    {
        if (pendingImpulseCount >= pendingImpulses.Length)
        {
            DroppedEventCount++;
            return false;
        }

        pendingImpulses[pendingImpulseCount++] = new PendingImpulse(
            cellIndex,
            worldX,
            worldZ,
            Math.Max(0.0f, energy),
            directionX,
            directionZ,
            impactVelocityY,
            supportRadiusWorldBlocks,
            couplingEfficiencyOverride,
            subgridImpact,
            kind);
        TotalImpulseCount++;
        if (kind == LiquidSurfaceImpulseKind.DroppedItemEntry)
        {
            TotalDroppedItemImpactCount++;
        }
        return true;
    }

    /// <summary>Derives unit normals from central differences of waves plus sub-cell bubble bulges.</summary>
    private void ComputeNormals()
    {
        EnsureActiveTopology();
        float inverseDoubleCellSize = 0.5f / CellSize;
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
            ref readonly CellStencil stencil = ref activeStencils[ordinal];
            int index = stencil.Center;
            float slopeX = ((heights[stencil.Right] + bubbleBulge[stencil.Right])
                - (heights[stencil.Left] + bubbleBulge[stencil.Left])) * inverseDoubleCellSize;
            float slopeZ = ((heights[stencil.Down] + bubbleBulge[stencil.Down])
                - (heights[stencil.Up] + bubbleBulge[stencil.Up])) * inverseDoubleCellSize;
            float inverseLength = 1.0f / MathF.Sqrt(1.0f + slopeX * slopeX + slopeZ * slopeZ);
            normalX[index] = -slopeX * inverseLength;
            normalZ[index] = -slopeZ * inverseLength;
        }
    }

    /// <summary>Samples a wave neighbor only when grid bounds, liquid profile, and base height connect it.</summary>
    /// <param name="x">Neighbor grid X.</param>
    /// <param name="z">Neighbor grid Z.</param>
    /// <param name="centerIndex">Flat center cell index.</param>
    /// <param name="fallback">Center value used to create a no-flux boundary.</param>
    /// <returns>Connected neighbor height or fallback.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ConnectedHeight(int x, int z, int centerIndex, float fallback)
    {
        if ((uint)x >= (uint)Width || (uint)z >= (uint)Depth)
        {
            return fallback;
        }

        int neighbour = GetIndex(x, z);
        return IsConnected(centerIndex, neighbour) ? heights[neighbour] : fallback;
    }

    /// <summary>Samples connected wave plus bubble height for normal reconstruction.</summary>
    /// <param name="x">Neighbor grid X.</param>
    /// <param name="z">Neighbor grid Z.</param>
    /// <param name="centerIndex">Flat center cell index.</param>
    /// <param name="fallback">Center rendered height at boundaries.</param>
    /// <returns>Connected rendered height or fallback.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ConnectedRenderedHeight(int x, int z, int centerIndex, float fallback)
    {
        if ((uint)x >= (uint)Width || (uint)z >= (uint)Depth)
        {
            return fallback;
        }

        int neighbour = GetIndex(x, z);
        return IsConnected(centerIndex, neighbour)
            ? heights[neighbour] + bubbleBulge[neighbour]
            : fallback;
    }

    /// <summary>Tests wave continuity across active cells of one profile and nearly equal base height.</summary>
    /// <param name="first">Reference flat cell index.</param>
    /// <param name="second">Candidate neighbor flat cell index.</param>
    /// <returns>Whether propagation/normal sampling may cross the edge.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsConnected(int first, int second)
    {
        return IsActive(second)
            && profileIds[first] == profileIds[second]
            && Math.Abs(surfaceWorldY[first] - surfaceWorldY[second])
                <= CellSize * SurfaceConnectionTolerance;
    }

    /// <summary>Checks the active bit in packed per-cell flags.</summary>
    /// <param name="index">Flat cell index.</param>
    /// <returns>Whether the cell represents visible simulated liquid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsActive(int index)
    {
        return (cellFlags[index] & CellActive) != 0;
    }

    /// <summary>Requires both active-liquid and rain-map exposure bits.</summary>
    /// <param name="index">Flat cell index.</param>
    /// <returns>Whether stochastic rain impulses may target the cell.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsRainExposed(int index)
    {
        return (cellFlags[index] & (CellActive | CellRainExposed))
            == (CellActive | CellRainExposed);
    }

    /// <summary>Finds/inserts an entity in bounded open addressing, replacing the oldest probed entry.</summary>
    /// <param name="entityId">Stable Vintage Story entity identifier.</param>
    /// <param name="existed">Whether returned storage contains the same entity's previous observation.</param>
    /// <returns>Tracker slot, always within fixed capacity.</returns>
    private int FindTracker(long entityId, out bool existed)
    {
        int start = FindTrackerSlot(entityId);
        for (int probe = 0; probe < TrackerProbeLimit; probe++)
        {
            int index = (start + probe) % entityTrackers.Length;
            ref EntityTracker tracker = ref entityTrackers[index];
            if (!tracker.Occupied)
            {
                existed = false;
                return index;
            }

            if (tracker.EntityId == entityId)
            {
                existed = true;
                return index;
            }
        }

        int replacement = start;
        int oldestStep = entityTrackers[replacement].LastSeenStep;
        for (int probe = 1; probe < TrackerProbeLimit; probe++)
        {
            int index = (start + probe) % entityTrackers.Length;
            if (entityTrackers[index].LastSeenStep < oldestStep)
            {
                replacement = index;
                oldestStep = entityTrackers[index].LastSeenStep;
            }
        }

        existed = false;
        return replacement;
    }

    /// <summary>Hashes a signed entity ID into the fixed tracker table using multiplicative mixing.</summary>
    /// <param name="entityId">Stable engine identifier.</param>
    /// <returns>Initial open-address slot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindTrackerSlot(long entityId)
    {
        ulong mixed = unchecked((ulong)entityId * 11400714819323198485ul);
        return (int)(mixed % (uint)entityTrackers.Length);
    }

    /// <summary>Maps world X/Z to floor-indexed grid coordinates.</summary>
    /// <param name="worldX">World X in blocks.</param>
    /// <param name="worldZ">World Z in blocks.</param>
    /// <param name="x">Resulting grid X.</param>
    /// <param name="z">Resulting grid Z.</param>
    /// <returns>Whether the result lies inside the clipmap.</returns>
    private bool TryWorldToCell(double worldX, double worldZ, out int x, out int z)
    {
        x = (int)Math.Floor((worldX - OriginWorldX) / CellSize);
        z = (int)Math.Floor((worldZ - OriginWorldZ) / CellSize);
        return (uint)x < (uint)Width && (uint)z < (uint)Depth;
    }

    /// <summary>Converts valid grid coordinates to Z-row-major flat storage.</summary>
    /// <param name="x">Valid grid X.</param>
    /// <param name="z">Valid grid Z.</param>
    /// <returns>Flat array index.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetIndex(int x, int z)
    {
        return z * Width + x;
    }

    /// <summary>Validates coordinates before converting them to flat storage.</summary>
    /// <param name="x">Candidate grid X.</param>
    /// <param name="z">Candidate grid Z.</param>
    /// <returns>Flat array index.</returns>
    private int GetIndexChecked(int x, int z)
    {
        if ((uint)x >= (uint)Width || (uint)z >= (uint)Depth)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        return GetIndex(x, z);
    }

    /// <summary>Resolves a finite wavelength no shorter than the grid's two-cell Nyquist limit.</summary>
    /// <param name="authoredWavelength">Dominant wavelength in metres/world blocks.</param>
    /// <param name="cellSize">Positive grid cell edge in metres/world blocks.</param>
    /// <returns>Resolved dominant wavelength in metres.</returns>
    private static double ResolveWavelengthMetres(float authoredWavelength, float cellSize)
    {
        double minimumResolvedWavelength = 2.0
            * cellSize
            * LiquidPhysicalModel.MetresPerWorldBlock;
        double authoredMetres = float.IsFinite(authoredWavelength)
            ? authoredWavelength * LiquidPhysicalModel.MetresPerWorldBlock
            : 0.0;
        return Math.Max(minimumResolvedWavelength, authoredMetres);
    }

    /// <summary>Derives a conservative small-wave displacement clamp from resolved spatial scale.</summary>
    /// <param name="coefficients">Per-profile dynamics.</param>
    /// <returns>Maximum absolute cell displacement in metres/world blocks, never below 0.001.</returns>
    private static float ComputeMaximumHeight(in LiquidSurfaceDynamics coefficients)
    {
        float amplitude = float.IsFinite(coefficients.WaveAmplitude)
            ? Math.Max(0.0f, coefficients.WaveAmplitude)
            : 0.0f;
        float wavelength = float.IsFinite(coefficients.WaveLength)
            ? Math.Max(0.0f, coefficients.WaveLength)
            : 0.0f;
        return Math.Max(
            0.001f,
            Math.Max(amplitude * 4.0f, wavelength * 0.10f));
    }

    /// <summary>Advances the deterministic non-zero xorshift32 stream.</summary>
    /// <returns>Pseudorandom unsigned 32-bit value.</returns>
    private uint NextRandomUInt()
    {
        uint value = randomState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        randomState = value;
        return value;
    }

    /// <summary>Converts the high 24 random bits to a uniform float in the half-open interval [0,1).</summary>
    /// <returns>Deterministic unit variate.</returns>
    private float NextRandomUnit()
    {
        return (NextRandomUInt() >> 8) * (1.0f / 16777216.0f);
    }

    /// <summary>Packed flag marking a cell as simulated visible liquid.</summary>
    private const byte CellActive = 1;
    /// <summary>Packed flag marking an active surface as reachable by rain.</summary>
    private const byte CellRainExposed = 2;

    /// <summary>Immutable fixed-step terms derived from cell material, depth, and grid scale.</summary>
    private readonly record struct CellStepCoefficients(
        float PropagationSpeedSquared,
        float VelocityRetention,
        float MaximumHeight,
        float EmissionRetention);

    /// <summary>Immutable wind-frequency terms derived from cell material, depth, and grid scale.</summary>
    private readonly record struct CellWindCoefficients(
        float Wavelength,
        float PhaseSpeed,
        float PhaseSpeedOverWavelength,
        float AngularFrequencySquared);

    /// <summary>Fixed-queue impulse payload retained until the next solver step.</summary>
    private readonly record struct PendingImpulse(
        int CellIndex,
        double WorldX,
        double WorldZ,
        float Energy,
        float DirectionX,
        float DirectionZ,
        float ImpactVelocityY,
        float SupportRadiusWorldBlocks,
        float CouplingEfficiencyOverride,
        PendingSubgridImpact SubgridImpact,
        LiquidSurfaceImpulseKind Kind);

    /// <summary>Optional source identity and conservative energy ledger carried beside an impulse.</summary>
    private readonly record struct PendingSubgridImpact(
        int SourceSequence,
        long EntityId,
        LiquidEntitySurfaceClass SurfaceClass,
        float SurfaceCoupledEnergyJoules,
        float ResolvedWaveEnergyJoules,
        float SubgridWaveEnergyJoules,
        float RenderedPacketEnergyJoules,
        float LocalSplashEnergyJoules,
        float CavityRadiusMetres,
        float DominantWavelengthMetres);

    /// <summary>Mutable fixed-pool lifecycle state for one rising or bursting bubble.</summary>
    private struct BubbleState
    {
        /// <summary>Whether this pool slot participates in update/upload.</summary>
        public bool Active;
        /// <summary>Whether the bubble finished rising and is in its short decay phase.</summary>
        public bool Bursting;
        /// <summary>Flat host cell index.</summary>
        public int CellIndex;
        /// <summary>Authored randomized radius in world blocks.</summary>
        public float Radius;
        /// <summary>Current phase duration in seconds.</summary>
        public float Duration;
        /// <summary>Elapsed phase time in seconds.</summary>
        public float Age;
        /// <summary>Sub-cell X location in the interval [0,1).</summary>
        public float OffsetX;
        /// <summary>Sub-cell Z location in the interval [0,1).</summary>
        public float OffsetZ;
    }

    /// <summary>Previous observation retained in the bounded entity continuity table.</summary>
    private struct EntityTracker
    {
        /// <summary>Whether the slot contains a valid entity observation.</summary>
        public bool Occupied;
        /// <summary>Stable engine entity identifier.</summary>
        public long EntityId;
        /// <summary>Previous world X in blocks.</summary>
        public double WorldX;
        /// <summary>Previous world Y in blocks.</summary>
        public double WorldY;
        /// <summary>Previous world Z in blocks.</summary>
        public double WorldZ;
        /// <summary>Previous engine feet-in-liquid state.</summary>
        public bool FeetInLiquid;
        /// <summary>Previous engine swimming state.</summary>
        public bool Swimming;
        /// <summary>Behavior taxonomy retained across observations.</summary>
        public LiquidEntitySurfaceClass SurfaceClass;
        /// <summary>Solver step of the latest observation, used for bounded replacement.</summary>
        public int LastSeenStep;
        /// <summary>Solver step of the latest emitted wake, used for rate limiting.</summary>
        public int LastWakeStep;
        /// <summary>Solver step of the latest exact projectile/liquid callback.</summary>
        public int LastLiquidCollisionStep;
        /// <summary>World X of the latest exact liquid collision, used to merge nested callbacks.</summary>
        public double LastLiquidCollisionWorldX;
        /// <summary>World Z of the latest exact liquid collision, used to merge nested callbacks.</summary>
        public double LastLiquidCollisionWorldZ;
        /// <summary>Whether the exact-collision timestamp and position fields are initialized.</summary>
        public bool HasLiquidCollision;
        /// <summary>
        /// Whether integrated-server physics established authoritative collision provenance for
        /// this entity, permanently suppressing later client interpolation reconstructions.
        /// </summary>
        public bool HasServerAuthoritativeLiquidCollision;
        /// <summary>Whether this dropped entity already emitted its one physical entry impulse.</summary>
        public bool HasImpactedSurface;
        /// <summary>X component paired with the strongest descending airborne sample.</summary>
        public float LastAirMotionX;
        /// <summary>Most negative airborne Y motion retained before liquid damping.</summary>
        public float LastAirMotionY;
        /// <summary>Z component paired with the strongest descending airborne sample.</summary>
        public float LastAirMotionZ;
        /// <summary>Whether a strongest-descending airborne motion sample is available.</summary>
        public bool HasLastAirMotion;
    }
}
