using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>
/// Allocation-free adapter from the public Vintage Story world/entity API to
/// the deterministic liquid-surface simulation inputs.
/// </summary>
internal sealed class LiquidSurfaceWorldInputs
{
    /// <summary>
    /// Calibration mapping a normalized engine rainfall signal of one to 50 mm/h, expressed in
    /// m/s. The public Vintage Story climate API does not claim that its 0..1 value is a rain flux.
    /// </summary>
    internal const float MaximumRainfallRateMetresPerSecond = 0.050f / 3_600.0f;

    /// <summary>
    /// Default conversion period for EntityPos.Motion. This 60 Hz calibration is explicit because
    /// the public motion vector does not publish a time unit; callers may pass their measured period.
    /// </summary>
    internal const float DefaultMotionSamplePeriodSeconds = 1.0f / 60.0f;

    /// <summary>Nominal engine motion vectors per real second in Vintage Story physics.</summary>
    internal const float NominalMotionStepsPerSecond = 60.0f;

    /// <summary>Substeps used by passive physics once an entity motion vector exceeds 0.1 blocks.</summary>
    private const int FastPhysicsSubsteps = 10;

    /// <summary>Motion-vector length above which passive physics selects ten collision substeps.</summary>
    private const double FastPhysicsSubstepMotionThreshold = 0.10;

    /// <summary>
    /// Uniform pseudo-mass of one dropped inventory item, calibrated as a 350 g hand-sized stone.
    /// Material identity is deliberately ignored so modded collectibles inherit the same rule.
    /// </summary>
    internal const float ReferenceDroppedItemMassKilograms = 0.35f;

    /// <summary>Measured representative mass of a completed hunting arrow in kilograms.</summary>
    internal const float ReferenceArrowMassKilograms = 0.060f;

    /// <summary>Representative throwable spear mass in kilograms.</summary>
    internal const float ReferenceSpearMassKilograms = 1.20f;

    /// <summary>Fallback projectile mass when a mod exposes no recognizable physical class.</summary>
    internal const float ReferenceProjectileMassKilograms = 0.35f;

    /// <summary>
    /// Minimum effective entry speed for a dropped item, preserving a visible ripple when engine
    /// motion is damped immediately before the liquid-contact observation.
    /// </summary>
    internal const float MinimumDroppedItemImpactSpeedMetresPerSecond = 1.5f;

    /// <summary>Maximum square-root stack multiplier, reached by stacks of 64 or more.</summary>
    private const float MaximumDroppedItemStackMassMultiplier = 8.0f;

    private readonly Vec3d samplePosition = new();
    private readonly BlockPos sampleBlockPosition;

    /// <summary>Creates reusable mutable sample positions for one Vintage Story dimension.</summary>
    /// <param name="dimensionId">Dimension encoded into block positions; defaults to the primary world.</param>
    public LiquidSurfaceWorldInputs(int dimensionId = 0)
    {
        sampleBlockPosition = new BlockPos(dimensionId);
    }

    /// <summary>Samples and calibrates horizontal wind and current rainfall to SI.</summary>
    /// <param name="blockAccessor">Thread-compatible world/block query service.</param>
    /// <param name="worldX">Surface X in world blocks.</param>
    /// <param name="worldSurfaceY">Surface Y in world blocks.</param>
    /// <param name="worldZ">Surface Z in world blocks.</param>
    /// <returns>Finite wind velocity in m/s and rainfall depth rate in m/s.</returns>
    public LiquidSurfaceForcing SampleEnvironment(
        IBlockAccessor blockAccessor,
        double worldX,
        double worldSurfaceY,
        double worldZ)
    {
        samplePosition.Set(worldX, worldSurfaceY, worldZ);
        sampleBlockPosition.Set(worldX, worldSurfaceY, worldZ);
        Vec3d? wind = blockAccessor.GetWindSpeedAt(samplePosition);
        ClimateCondition? climate = blockAccessor.GetClimateAt(
            sampleBlockPosition,
            EnumGetClimateMode.NowValues);
        float normalizedRainfall = Math.Clamp(
            climate is not null && float.IsFinite(climate.Rainfall) ? climate.Rainfall : 0.0f,
            0.0f,
            1.0f);
        return new LiquidSurfaceForcing(
            SafeScaledFloat(wind?.X ?? 0.0, LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit),
            SafeScaledFloat(wind?.Z ?? 0.0, LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit),
            normalizedRainfall * MaximumRainfallRateMetresPerSecond);
    }

    /// <summary>Compares a surface against the rain height map to reject covered containers/interiors.</summary>
    /// <param name="blockAccessor">World height-map query service.</param>
    /// <param name="worldX">Integer world-block X.</param>
    /// <param name="worldSurfaceY">Liquid surface height in world blocks.</param>
    /// <param name="worldZ">Integer world-block Z.</param>
    /// <returns>Whether precipitation can physically reach the surface.</returns>
    public bool IsRainExposed(
        IBlockAccessor blockAccessor,
        int worldX,
        float worldSurfaceY,
        int worldZ)
    {
        sampleBlockPosition.Set(worldX, (int)MathF.Floor(worldSurfaceY), worldZ);
        int rainMapHeight = blockAccessor.GetRainMapHeightAt(sampleBlockPosition);
        return worldSurfaceY >= rainMapHeight;
    }

    /// <summary>
    /// Copies stable identity, position, mass, motion sampling period, class, and liquid-contact
    /// flags from an entity. EntityProperties.Weight is documented by Vintage Story in kilograms.
    /// </summary>
    /// <param name="entity">Live Vintage Story entity.</param>
    /// <param name="motionSamplePeriodSeconds">
    /// Positive seconds represented by one EntityPos.Motion vector; defaults to the explicit 60 Hz
    /// renderer calibration and should be replaced by a measured runtime cadence when available.
    /// </param>
    /// <returns>Detached finite simulation input safe to process without retaining engine objects.</returns>
    public static LiquidEntitySurfaceSample SampleEntity(
        Entity entity,
        float motionSamplePeriodSeconds = DefaultMotionSamplePeriodSeconds)
    {
        if (!float.IsFinite(motionSamplePeriodSeconds) || motionSamplePeriodSeconds <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(motionSamplePeriodSeconds));
        }

        EntityPos position = entity.Pos;
        Vec3d motion = position.Motion;
        bool isDroppedItem = entity is EntityItem;
        LiquidEntitySurfaceClass surfaceClass = isDroppedItem
            ? LiquidEntitySurfaceClass.DroppedItem
            : ClassifyEntity(entity);
        IProjectile? projectile = entity as IProjectile;
        AssetLocation? projectileIdentity = ProjectileIdentity(entity, projectile);
        int stackSize = isDroppedItem
            ? Math.Max(((EntityItem)entity).Itemstack?.StackSize ?? 1, 1)
            : 1;
        float massKilograms = isDroppedItem
            ? EstimateDroppedItemPseudoMassKilograms(stackSize)
            : projectile is not null
                ? EstimateProjectilePseudoMassKilograms(projectileIdentity)
                : entity.Properties?.Weight ?? 25.0f;
        if (!float.IsFinite(massKilograms) || massKilograms <= 0.0f)
        {
            massKilograms = 25.0f;
        }
        return new LiquidEntitySurfaceSample(
            entity.EntityId,
            surfaceClass,
            position.X,
            position.Y,
            position.Z,
            SafeFloat(motion.X),
            SafeFloat(motion.Y),
            SafeFloat(motion.Z),
            entity.FeetInLiquid,
            entity.Swimming,
            massKilograms,
            motionSamplePeriodSeconds);
    }

    /// <summary>
    /// Copies one exact projectile/liquid callback. Generic projectiles use the live contact motion;
    /// thrown stones may supply the separate vector their engine override uses for rebound.
    /// </summary>
    /// <param name="entity">Client or integrated-server projectile at its liquid collision callback.</param>
    /// <param name="incidentMotion">Engine motion vector immediately before liquid response.</param>
    /// <param name="motionSamplePeriodSeconds">Positive seconds represented by the motion vector.</param>
    /// <returns>Detached SI-calibrated collision observation.</returns>
    public static LiquidProjectileCollisionSample SampleProjectileLiquidCollision(
        Entity entity,
        Vec3d incidentMotion,
        float motionSamplePeriodSeconds = DefaultMotionSamplePeriodSeconds)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(incidentMotion);
        if (!float.IsFinite(motionSamplePeriodSeconds) || motionSamplePeriodSeconds <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(motionSamplePeriodSeconds));
        }

        LiquidEntitySurfaceClass surfaceClass = ClassifyEntity(entity);
        IProjectile? projectile = entity as IProjectile;
        float massKilograms = EstimateProjectilePseudoMassKilograms(
            ProjectileIdentity(entity, projectile));
        EntityPos position = entity.Pos;
        Vec3d contactMotion = position.Motion;
        double displacementScale = LiquidCollisionSubstepDisplacementScale(contactMotion);
        return new LiquidProjectileCollisionSample(
            entity.EntityId,
            surfaceClass,
            position.X,
            position.Y,
            position.Z,
            position.X - contactMotion.X * displacementScale,
            position.Y - contactMotion.Y * displacementScale,
            position.Z - contactMotion.Z * displacementScale,
            SafeFloat(incidentMotion.X),
            SafeFloat(incidentMotion.Y),
            SafeFloat(incidentMotion.Z),
            0.0f,
            0.0f,
            0.0f,
            massKilograms,
            motionSamplePeriodSeconds,
            entity.World?.Side == EnumAppSide.Server);
    }

    /// <summary>
    /// Reproduces passive physics' displacement multiplier for the substep that entered liquid:
    /// position changes by Motion times 60 times the substep duration.
    /// </summary>
    /// <param name="motion">Live finite engine motion used for the collision substep.</param>
    /// <returns>Multiplier converting the motion vector into world-block displacement.</returns>
    internal static double LiquidCollisionSubstepDisplacementScale(Vec3d motion)
    {
        double motionLength = motion.Length();
        int substeps = double.IsFinite(motionLength)
            && motionLength > FastPhysicsSubstepMotionThreshold
                ? FastPhysicsSubsteps
                : 1;
        return NominalMotionStepsPerSecond * GlobalConstants.PhysicsFrameTime / substeps;
    }

    /// <summary>
    /// Estimates a dropped stack from one material-agnostic stone reference. Square-root stack
    /// scaling gives a larger pile more inertia without treating a 64-stack as a rigid 22.4 kg rock.
    /// </summary>
    /// <param name="stackSize">Inventory units represented by the dropped entity.</param>
    /// <returns>Bounded pseudo-mass in kilograms.</returns>
    internal static float EstimateDroppedItemPseudoMassKilograms(int stackSize)
    {
        float stackMultiplier = Math.Clamp(
            MathF.Sqrt(Math.Max(stackSize, 1)),
            1.0f,
            MaximumDroppedItemStackMassMultiplier);
        return ReferenceDroppedItemMassKilograms * stackMultiplier;
    }

    /// <summary>
    /// Maps projectile semantics to conservative real-world mass priors. The public
    /// <see cref="IProjectile.Weight"/> value is deliberately ignored because Vintage Story
    /// documents it as a knockback coefficient rather than a physical mass in kilograms.
    /// </summary>
    /// <param name="code">Projectile entity code used to recognize arrows, spears, and stones.</param>
    /// <returns>Finite projectile pseudo-mass in kilograms.</returns>
    internal static float EstimateProjectilePseudoMassKilograms(AssetLocation? code)
    {
        string path = code?.Path ?? string.Empty;
        float referenceMass = path.Contains("arrow", StringComparison.OrdinalIgnoreCase)
            ? ReferenceArrowMassKilograms
            : path.Contains("spear", StringComparison.OrdinalIgnoreCase)
                || path.Contains("javelin", StringComparison.OrdinalIgnoreCase)
                ? ReferenceSpearMassKilograms
                : path.Contains("thrownstone", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("thrownboulder", StringComparison.OrdinalIgnoreCase)
                    ? ReferenceDroppedItemMassKilograms
                    : ReferenceProjectileMassKilograms;
        return referenceMass;
    }

    /// <summary>Classifies an entity, promoting any otherwise-generic <see cref="IProjectile"/>.</summary>
    /// <param name="entity">Loaded entity instance.</param>
    /// <returns>Specific surface behavior used by the solver.</returns>
    internal static LiquidEntitySurfaceClass ClassifyEntity(Entity entity)
    {
        LiquidEntitySurfaceClass classified = LiquidSurfaceEntityClassifier.Classify(
            entity.Properties?.Code,
            entity.IsCreature);
        if (entity is IProjectile projectile
            && IsSkippableStoneProjectile(ProjectileIdentity(entity, projectile)))
        {
            return LiquidEntitySurfaceClass.ThrownStone;
        }
        return classified == LiquidEntitySurfaceClass.Generic && entity is IProjectile
            ? LiquidEntitySurfaceClass.Projectile
            : classified;
    }

    /// <summary>Recognizes stock and modded loose stones that use the generic thrown-item entity.</summary>
    /// <param name="code">Projectile stack identity when available.</param>
    /// <returns>Whether the collectible is a stone eligible for repeated shallow ricochets.</returns>
    internal static bool IsSkippableStoneProjectile(AssetLocation? code) =>
        code is not null
        && (code.Path.StartsWith("stone-", StringComparison.OrdinalIgnoreCase)
            || code.Path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase));

    /// <summary>Prefers the thrown collectible over the generic entity code for physical semantics.</summary>
    /// <param name="entity">Projectile entity.</param>
    /// <param name="projectile">Optional public projectile interface.</param>
    /// <returns>Collectible code, then entity code, or null.</returns>
    private static AssetLocation? ProjectileIdentity(Entity entity, IProjectile? projectile)
    {
        ItemStack? stack = projectile?.ProjectileStack;
        CollectibleObject? collectible = stack?.Collectible;
        if (collectible is null && stack is not null && entity.World is not null)
        {
            collectible = stack.Class == EnumItemClass.Block
                ? entity.World.GetBlock(stack.Id)
                : entity.World.GetItem(stack.Id);
        }

        return collectible?.Code ?? entity.Properties?.Code;
    }

    /// <summary>Converts a double to finite float range, mapping NaN/infinity to zero.</summary>
    /// <param name="value">World API scalar.</param>
    /// <returns>Finite single-precision value.</returns>
    private static float SafeFloat(double value)
    {
        return double.IsFinite(value)
            ? (float)Math.Clamp(value, -float.MaxValue, float.MaxValue)
            : 0.0f;
    }

    /// <summary>Applies a positive calibration scale while saturating finite overflow.</summary>
    /// <param name="value">Engine scalar before unit conversion.</param>
    /// <param name="scale">Finite positive conversion factor.</param>
    /// <returns>Finite scaled single-precision value.</returns>
    private static float SafeScaledFloat(double value, double scale)
    {
        if (!double.IsFinite(value) || !double.IsFinite(scale) || scale <= 0.0)
        {
            return 0.0f;
        }

        double maximumInput = float.MaxValue / scale;
        return (float)(Math.Clamp(value, -maximumInput, maximumInput) * scale);
    }
}

/// <summary>Maps public entity asset identity to the small simulation behavior taxonomy.</summary>
internal static class LiquidSurfaceEntityClassifier
{
    /// <summary>Classifies a loaded entity from domain/path identity and creature semantics.</summary>
    /// <param name="code">Entity asset code, or null for generic behavior.</param>
    /// <param name="isCreature">Whether the engine classifies the entity as a creature.</param>
    /// <returns>Bobber, thrown stone, fish, or generic surface behavior.</returns>
    public static LiquidEntitySurfaceClass Classify(AssetLocation? code, bool isCreature)
    {
        return code is null
            ? LiquidEntitySurfaceClass.Generic
            : Classify(code.Domain, code.Path, isCreature);
    }

    /// <summary>String overload used by deterministic tests without constructing engine assets.</summary>
    /// <param name="domain">Asset domain; only exact <c>game:bobber</c> receives bobber semantics.</param>
    /// <param name="path">Entity asset path inspected case-insensitively for stone/fish identities.</param>
    /// <param name="isCreature">Prevents non-creature assets containing “fish” from being classified as fish.</param>
    /// <returns>Simulation behavior class.</returns>
    internal static LiquidEntitySurfaceClass Classify(
        string? domain,
        string? path,
        bool isCreature)
    {
        if (string.Equals(domain, "game", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "bobber", StringComparison.OrdinalIgnoreCase))
        {
            return LiquidEntitySurfaceClass.Bobber;
        }

        if (path?.Contains("thrownstone", StringComparison.OrdinalIgnoreCase) == true
            || path?.Contains("thrownboulder", StringComparison.OrdinalIgnoreCase) == true)
        {
            return LiquidEntitySurfaceClass.ThrownStone;
        }

        if (isCreature && path?.Contains("fish", StringComparison.OrdinalIgnoreCase) == true)
        {
            return LiquidEntitySurfaceClass.Fish;
        }

        return LiquidEntitySurfaceClass.Generic;
    }
}
