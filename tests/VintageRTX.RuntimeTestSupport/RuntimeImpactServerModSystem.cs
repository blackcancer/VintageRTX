using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageRTX.RuntimeTestSupport;

/// <summary>
/// Exposes a server-authoritative dropped-item command only while the isolated
/// water-reflection runtime scenario is active. The shipping VintageRTX mod
/// remains client-only; this support mod is loaded solely by VintageRTX.Test.
/// </summary>
public sealed class RuntimeImpactServerModSystem : ModSystem
{
    /// <summary>Scenario name that authorizes server-side test mutation.</summary>
    private const string WaterReflectionScenario = "water-reflection";
    /// <summary>Persistent marker used to identify only entities owned by the isolated scenario.</summary>
    private const string TestEntityOriginPrefix = "vintagertx-runtime-";
    /// <summary>Nominal engine motion steps integrated per second by projectile and item entities.</summary>
    internal const double EngineMotionStepsPerSecond = 60.0;
    private ICoreServerAPI? api;
    private readonly List<Entity> spawnedTestEntities = [];
    private readonly HashSet<string> spawnedProjectileKinds = new(StringComparer.Ordinal);
    private string runId = string.Empty;
    private bool staleEntitiesCleaned;
    private Entity? opaqueWitness;
    private Entity? alphaShapedWitness;
    private Vec3d? opaqueWitnessAnchor;
    private Vec3d? alphaShapedWitnessAnchor;
    private long witnessPinListenerId;
    private int witnessPinTicks;
    private double maximumWitnessDrift;

    /// <summary>Restricts the real-entity injector to the server world.</summary>
    /// <param name="forSide">Application side considered by the mod loader.</param>
    /// <returns><see langword="true"/> only for the server side.</returns>
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>
    /// Registers the typed impact command when the parent process identifies
    /// the water-reflection scenario. Normal game and server runs remain inert.
    /// </summary>
    /// <param name="serverApi">Authoritative server world and command API.</param>
    public override void StartServerSide(ICoreServerAPI serverApi)
    {
        if (!IsWaterReflectionScenario(Environment.GetEnvironmentVariable("VINTAGERTX_TEST_SCENARIO")))
        {
            return;
        }

        api = serverApi;
        runId = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID")?.Trim()
            ?? string.Empty;
        CommandArgumentParsers parsers = serverApi.ChatCommands.Parsers;
        serverApi.ChatCommands.Create("vintagertxtest")
            .WithDescription("VintageRTX isolated runtime-test controls")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("impact")
                .WithArgs(
                    parsers.Double("x"),
                    parsers.Double("y"),
                    parsers.Double("z"),
                    parsers.Double("velocityX"),
                    parsers.Double("velocityY"),
                    parsers.Double("velocityZ"),
                    parsers.Int("stackSize"),
                    parsers.Double("dropHeight"))
                .HandleWith(SpawnDroppedItemImpact)
            .EndSubCommand()
            .BeginSubCommand("witness")
                .WithArgs(
                    parsers.Double("itemX"),
                    parsers.Double("itemY"),
                    parsers.Double("itemZ"),
                    parsers.Double("humanoidX"),
                    parsers.Double("humanoidY"),
                    parsers.Double("humanoidZ"))
                .HandleWith(SpawnReflectionWitnesses)
            .EndSubCommand()
            .BeginSubCommand("projectile")
                .WithArgs(
                    parsers.Word("kind"),
                    parsers.Double("x"),
                    parsers.Double("y"),
                    parsers.Double("z"),
                    parsers.Double("velocityX"),
                    parsers.Double("velocityY"),
                    parsers.Double("velocityZ"))
                .HandleWith(SpawnLiquidProjectile)
            .EndSubCommand();

        serverApi.Logger.Notification(
            "[VintageRTX.Test] Server-authoritative dropped-item and projectile impact commands ready.");
    }

    /// <summary>
    /// Spawns one real thrown loose stone, flint arrow, or flint spear through the public
    /// <see cref="IProjectile"/> contract. Each kind is accepted once per
    /// isolated run so a repeated client command cannot manufacture duplicate
    /// liquid events.
    /// </summary>
    /// <param name="args">Projectile kind followed by bounded world position and SI velocity.</param>
    /// <returns>A command result identifying the authoritative entity and payload.</returns>
    private TextCommandResult SpawnLiquidProjectile(TextCommandCallingArgs args)
    {
        string kind = ((string)args[0]).Trim().ToLowerInvariant();
        double x = (double)args[1];
        double y = (double)args[2];
        double z = (double)args[3];
        double velocityX = (double)args[4];
        double velocityY = (double)args[5];
        double velocityZ = (double)args[6];
        if (!IsSupportedProjectileKind(kind)
            || !AreFiniteAndBounded(x, y, z, velocityX, velocityY, velocityZ))
        {
            return TextCommandResult.Error(
                "Projectile kind, coordinates or velocity are outside the safe test contract.");
        }

        if (!spawnedProjectileKinds.Add(kind))
        {
            return TextCommandResult.Error(
                $"The {kind} projectile has already been spawned for this run.");
        }

        AssetLocation entityCode = ProjectileEntityCode(kind);
        AssetLocation payloadCode = ProjectilePayloadCode(kind);
        EntityProperties? entityType = api!.World.GetEntityType(entityCode);
        Item? payload = api.World.GetItem(payloadCode);
        if (entityType is null || payload is null)
        {
            spawnedProjectileKinds.Remove(kind);
            return TextCommandResult.Error(
                $"The required {entityCode} entity or {payloadCode} item is unavailable.");
        }

        Entity entity = api.World.ClassRegistry.CreateEntity(entityType);
        if (entity is not IProjectile projectile)
        {
            spawnedProjectileKinds.Remove(kind);
            return TextCommandResult.Error(
                $"The {entityCode} entity does not implement the public IProjectile API.");
        }

        Vec3d engineMotion = ConvertSiVelocityToEngineMotion(
            velocityX,
            velocityY,
            velocityZ);
        projectile.FiredBy = args.Caller.Player.Entity;
        projectile.Damage = 0.0f;
        projectile.DamageTier = 0;
        projectile.ProjectileStack = new ItemStack(payload, 1);
        projectile.DropOnImpactChance = 1.0f;
        projectile.DamageStackOnImpact = false;
        projectile.Collectible = true;
        entity.Pos.SetPosWithDimension(new Vec3d(x, y, z));
        entity.Pos.Motion.Set(engineMotion);
        entity.World = api.World;
        projectile.PreInitialize();
        TagScenarioEntity(entity, "projectile-" + kind);
        api.World.SpawnPriorityEntity(entity);
        spawnedTestEntities.Add(entity);

        api.Logger.Notification(
            "[VintageRTX.Test] Server liquid projectile spawned: kind={0}, entity={1}, runtime-class={2}, entity-type={3}, payload={4}, position=({5},{6},{7}), velocity-si=({8},{9},{10}) m/s, motion-step=({11},{12},{13}) at 60 Hz.",
            kind,
            entity.EntityId,
            entity.GetType().Name,
            entityType.Code,
            payload.Code,
            x.ToString("0.00", CultureInfo.InvariantCulture),
            y.ToString("0.00", CultureInfo.InvariantCulture),
            z.ToString("0.00", CultureInfo.InvariantCulture),
            velocityX.ToString("0.00", CultureInfo.InvariantCulture),
            velocityY.ToString("0.00", CultureInfo.InvariantCulture),
            velocityZ.ToString("0.00", CultureInfo.InvariantCulture),
            engineMotion.X.ToString("0.0000", CultureInfo.InvariantCulture),
            engineMotion.Y.ToString("0.0000", CultureInfo.InvariantCulture),
            engineMotion.Z.ToString("0.0000", CultureInfo.InvariantCulture));
        return TextCommandResult.Success($"VintageRTX real {kind} projectile spawned.");
    }

    /// <summary>
    /// Spawns one opaque dropped-item renderer and one vanilla humanoid shape,
    /// then pins both above the measured liquid surface for the capture window.
    /// </summary>
    /// <param name="args">Six finite world coordinates for the two witness anchors.</param>
    /// <returns>A command result identifying success or the missing vanilla asset.</returns>
    private TextCommandResult SpawnReflectionWitnesses(TextCommandCallingArgs args)
    {
        double itemX = (double)args[0];
        double itemY = (double)args[1];
        double itemZ = (double)args[2];
        double humanoidX = (double)args[3];
        double humanoidY = (double)args[4];
        double humanoidZ = (double)args[5];
        if (!AreWitnessCoordinatesBounded(itemX, itemY, itemZ, humanoidX, humanoidY, humanoidZ))
        {
            return TextCommandResult.Error("Reflection-witness coordinates are outside the safe test range.");
        }

        if (opaqueWitness is not null || alphaShapedWitness is not null)
        {
            return TextCommandResult.Error("Reflection witnesses have already been spawned for this run.");
        }

        RemoveStaleTestEntities(
            new Vec3d(itemX, itemY, itemZ),
            new Vec3d(humanoidX, humanoidY, humanoidZ));

        CollectibleObject? collectible = ResolveReferenceCollectible(api!.World);
        EntityProperties? humanoidType = api.World.GetEntityType(new AssetLocation("game", "strawdummy"));
        if (collectible is null || humanoidType is null)
        {
            return TextCommandResult.Error("A required game:stone-granite or game:strawdummy witness asset is unavailable.");
        }

        opaqueWitnessAnchor = new Vec3d(itemX, itemY, itemZ);
        alphaShapedWitnessAnchor = new Vec3d(humanoidX, humanoidY, humanoidZ);
        alphaShapedWitness = api.World.ClassRegistry.CreateEntity(humanoidType);
        if (alphaShapedWitness is null)
        {
            return TextCommandResult.Error("The entity registry rejected the game:strawdummy witness type.");
        }

        alphaShapedWitness.Pos.SetPosWithDimension(alphaShapedWitnessAnchor);
        alphaShapedWitness.Pos.Yaw = 0.0f;
        alphaShapedWitness.PositionBeforeFalling.Set(
            alphaShapedWitnessAnchor.X,
            alphaShapedWitnessAnchor.Y,
            alphaShapedWitnessAnchor.Z);
        TagScenarioEntity(alphaShapedWitness, "witness");
        api.World.SpawnEntity(alphaShapedWitness);
        opaqueWitness = api.World.SpawnItemEntity(
            new ItemStack(collectible, 1),
            opaqueWitnessAnchor,
            new Vec3d());
        if (opaqueWitness is null)
        {
            alphaShapedWitness.Die(EnumDespawnReason.Removed);
            alphaShapedWitness = null;
            return TextCommandResult.Error("The authoritative world rejected the opaque item witness.");
        }

        ConfigureStationaryWitness(opaqueWitness);
        ConfigureStationaryWitness(alphaShapedWitness);
        TagScenarioEntity(opaqueWitness, "witness");
        spawnedTestEntities.Add(opaqueWitness);
        spawnedTestEntities.Add(alphaShapedWitness);
        PinReflectionWitnesses(0.0f);
        witnessPinListenerId = api.Event.RegisterGameTickListener(PinReflectionWitnesses, 20);

        api.Logger.Notification(
            "[VintageRTX.Test] Server reflection witnesses spawned: opaque entity={0}, item={1}, anchor=({2},{3},{4}); alpha-shaped entity={5}, type={6}, anchor=({7},{8},{9}).",
            opaqueWitness.EntityId,
            collectible.Code,
            itemX.ToString("0.00", CultureInfo.InvariantCulture),
            itemY.ToString("0.00", CultureInfo.InvariantCulture),
            itemZ.ToString("0.00", CultureInfo.InvariantCulture),
            alphaShapedWitness.EntityId,
            humanoidType.Code,
            humanoidX.ToString("0.00", CultureInfo.InvariantCulture),
            humanoidY.ToString("0.00", CultureInfo.InvariantCulture),
            humanoidZ.ToString("0.00", CultureInfo.InvariantCulture));
        return TextCommandResult.Success("VintageRTX reflection witnesses spawned and pinned.");
    }

    /// <summary>
    /// Disables passive gravity for a visual witness on both the authoritative instance and the
    /// watched state serialized to clients. The opaque item must remain a real entity renderer, but
    /// it must not masquerade as one of the three independently scripted physical water impacts.
    /// </summary>
    /// <param name="entity">Spawned real entity retained as a reflection witness.</param>
    private static void ConfigureStationaryWitness(Entity entity)
    {
        entity.WatchedAttributes.SetDouble("gravityFactor", 0.0);
        EntityBehaviorPassivePhysics? passivePhysics =
            entity.GetBehavior<EntityBehaviorPassivePhysics>();
        if (passivePhysics is not null)
        {
            passivePhysics.GravityPerSecond = 0.0;
        }

        entity.Pos.Motion.Set(0.0, 0.0, 0.0);
    }

    /// <summary>Measures physics drift and restores both witnesses to their fixed capture anchors.</summary>
    /// <param name="deltaTime">Elapsed game-tick time; position pinning is intentionally frame-rate independent.</param>
    private void PinReflectionWitnesses(float deltaTime)
    {
        _ = deltaTime;
        maximumWitnessDrift = Math.Max(maximumWitnessDrift, PinWitness(opaqueWitness, opaqueWitnessAnchor));
        maximumWitnessDrift = Math.Max(maximumWitnessDrift, PinWitness(alphaShapedWitness, alphaShapedWitnessAnchor));
        witnessPinTicks++;
        if (witnessPinTicks is 1_000 or 1_500 or 1_750)
        {
            api!.Logger.Notification(
                "[VintageRTX.Test] Server reflection witnesses stable: ticks={0}, opaque entity={1}, alpha-shaped entity={2}, maximum drift={3}m.",
                witnessPinTicks,
                opaqueWitness?.EntityId ?? 0,
                alphaShapedWitness?.EntityId ?? 0,
                maximumWitnessDrift.ToString("0.0000", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Returns the measured drift before restoring a single witness.</summary>
    /// <param name="entity">Spawned entity to pin.</param>
    /// <param name="anchor">Authoritative capture anchor.</param>
    /// <returns>Euclidean displacement in metres, or zero when the witness is unavailable.</returns>
    private static double PinWitness(Entity? entity, Vec3d? anchor)
    {
        if (entity is null || anchor is null)
        {
            return 0.0;
        }

        double drift = entity.Pos.DistanceTo(anchor);
        EntityBehaviorPassivePhysics? passivePhysics =
            entity.GetBehavior<EntityBehaviorPassivePhysics>();
        if (passivePhysics is not null)
        {
            passivePhysics.GravityPerSecond = 0.0;
        }
        entity.Pos.SetPosWithDimension(anchor);
        entity.Pos.Motion.Set(0.0, 0.0, 0.0);
        entity.PositionBeforeFalling.Set(anchor.X, anchor.Y, anchor.Z);
        return drift;
    }

    /// <summary>Normalizes the opt-in scenario identifier used by the isolated harness.</summary>
    /// <param name="scenario">Untrusted environment value supplied to the game process.</param>
    /// <returns><see langword="true"/> only for the water-reflection scenario.</returns>
    internal static bool IsWaterReflectionScenario(string? scenario)
    {
        return string.Equals(
            scenario?.Trim(),
            WaterReflectionScenario,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the loose-stone reference collectible and asks the authoritative
    /// world to spawn a real item entity with the requested physical velocity.
    /// </summary>
    /// <param name="args">Six finite world-position/velocity arguments followed by a bounded stack size and fall height.</param>
    /// <returns>A command result reporting the spawned entity or a bounded validation error.</returns>
    private TextCommandResult SpawnDroppedItemImpact(TextCommandCallingArgs args)
    {
        double x = (double)args[0];
        double y = (double)args[1];
        double z = (double)args[2];
        double velocityX = (double)args[3];
        double velocityY = (double)args[4];
        double velocityZ = (double)args[5];
        int stackSize = (int)args[6];
        double dropHeightMetres = (double)args[7];
        if (!AreFiniteAndBounded(x, y, z, velocityX, velocityY, velocityZ)
            || !IsImpactStackSizeValid(stackSize)
            || !IsImpactDropHeightValid(dropHeightMetres))
        {
            return TextCommandResult.Error("Impact coordinates, velocity, stack size or drop height are outside the safe test range.");
        }

        CollectibleObject? collectible = ResolveReferenceCollectible(api!.World);
        if (collectible is null)
        {
            return TextCommandResult.Error("The game:stone-granite reference item is unavailable.");
        }

        Vec3d position = new(x, y, z);
        // EntityPos.Motion is expressed per nominal 1/60-second motion step,
        // independently of GlobalConstants.PhysicsFrameTime. Keep the command
        // SI-facing and convert exactly once at this API boundary.
        Vec3d engineMotion = ConvertSiVelocityToEngineMotion(
            velocityX,
            velocityY,
            velocityZ);
        Entity? entity = api.World.SpawnItemEntity(
            new ItemStack(collectible, stackSize),
            position,
            engineMotion);
        EntityItem? entityItem = entity as EntityItem;
        ItemStack? spawnedItemStack = entityItem?.Itemstack;
        if (entityItem is null || spawnedItemStack is null || spawnedItemStack.StackSize != stackSize)
        {
            entity?.Die(EnumDespawnReason.Removed);
            return TextCommandResult.Error("The authoritative world rejected the dropped item entity.");
        }

        TagScenarioEntity(entityItem, "impact");
        spawnedTestEntities.Add(entityItem);

        api.Logger.Notification(
            "[VintageRTX.Test] Server dropped-item impact spawned: entity={0}, item={1}, stack={2}, expected pseudo-mass={3} kg, drop-height={4} m, position=({5},{6},{7}), velocity-si=({8},{9},{10}) m/s, motion-step=({11},{12},{13}) at 60 Hz.",
            entityItem.EntityId,
            collectible.Code,
            spawnedItemStack.StackSize,
            ExpectedPseudoMassKilograms(spawnedItemStack.StackSize).ToString("0.000", CultureInfo.InvariantCulture),
            dropHeightMetres.ToString("0.00", CultureInfo.InvariantCulture),
            x.ToString("0.00", CultureInfo.InvariantCulture),
            y.ToString("0.00", CultureInfo.InvariantCulture),
            z.ToString("0.00", CultureInfo.InvariantCulture),
            velocityX.ToString("0.00", CultureInfo.InvariantCulture),
            velocityY.ToString("0.00", CultureInfo.InvariantCulture),
            velocityZ.ToString("0.00", CultureInfo.InvariantCulture),
            engineMotion.X.ToString("0.0000", CultureInfo.InvariantCulture),
            engineMotion.Y.ToString("0.0000", CultureInfo.InvariantCulture),
            engineMotion.Z.ToString("0.0000", CultureInfo.InvariantCulture));
        return TextCommandResult.Success("VintageRTX real dropped-item impact spawned.");
    }

    /// <summary>Rejects non-finite values, remote coordinates and implausible test velocities.</summary>
    /// <param name="values">Position followed by velocity components.</param>
    /// <returns><see langword="true"/> when all values are finite and within the narrow test contract.</returns>
    internal static bool AreFiniteAndBounded(params double[] values)
    {
        if (values.Length != 6 || values.Any(static value => !double.IsFinite(value)))
        {
            return false;
        }

        return Math.Abs(values[0]) <= 30_000_000.0
            && values[1] is >= -1024.0 and <= 1_048_576.0
            && Math.Abs(values[2]) <= 30_000_000.0
            && Math.Abs(values[3]) <= 32.0
            && Math.Abs(values[4]) <= 32.0
            && Math.Abs(values[5]) <= 32.0;
    }

    /// <summary>Recognizes the real projectile kinds exposed by the bounded runtime command.</summary>
    /// <param name="kind">Normalized or user-supplied projectile selector.</param>
    /// <returns><see langword="true"/> for <c>stone</c>, <c>arrow</c>, or <c>spear</c>.</returns>
    internal static bool IsSupportedProjectileKind(string? kind) =>
        string.Equals(kind?.Trim(), "stone", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind?.Trim(), "arrow", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind?.Trim(), "spear", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a validated selector to the concrete vanilla entity type.</summary>
    /// <param name="kind">Validated normalized projectile kind.</param>
    /// <returns>The concrete vanilla entity variant for the requested projectile.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an unsupported selector.</exception>
    internal static AssetLocation ProjectileEntityCode(string kind) =>
        kind.Trim().ToLowerInvariant() switch
        {
            "stone" => new AssetLocation("game", "thrownitem"),
            "arrow" => new AssetLocation("game", "arrow-flint"),
            "spear" => new AssetLocation("game", "spear-generic-flint"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported runtime projectile kind.")
        };

    /// <summary>Maps a validated selector to the item serialized through <see cref="IProjectile.ProjectileStack"/>.</summary>
    /// <param name="kind">Validated normalized projectile kind.</param>
    /// <returns>The loose granite stone, flint arrow, or flint spear item code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an unsupported selector.</exception>
    internal static AssetLocation ProjectilePayloadCode(string kind) =>
        kind.Trim().ToLowerInvariant() switch
        {
            "stone" => new AssetLocation("game", "stone-granite"),
            "arrow" => new AssetLocation("game", "arrow-flint"),
            "spear" => new AssetLocation("game", "spear-generic-flint"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported runtime projectile kind.")
        };

    /// <summary>Restricts deterministic impact witnesses to legal loose-stone stack sizes.</summary>
    /// <param name="stackSize">Requested quantity carried by one <see cref="EntityItem"/>.</param>
    /// <returns><see langword="true"/> for one through sixty-four items.</returns>
    internal static bool IsImpactStackSizeValid(int stackSize) => stackSize is >= 1 and <= 64;

    /// <summary>Restricts the logged physical fall to a plausible, bounded test height.</summary>
    /// <param name="dropHeightMetres">Vertical distance from release point to liquid surface.</param>
    /// <returns><see langword="true"/> for finite heights from half a metre through twelve metres.</returns>
    internal static bool IsImpactDropHeightValid(double dropHeightMetres) =>
        double.IsFinite(dropHeightMetres) && dropHeightMetres is >= 0.5 and <= 12.0;

    /// <summary>Mirrors the client liquid model's square-root stack pseudo-mass.</summary>
    /// <param name="stackSize">Positive item count represented by the dropped entity.</param>
    /// <returns>Expected bounded pseudo-mass in kilograms.</returns>
    internal static double ExpectedPseudoMassKilograms(int stackSize)
    {
        if (!IsImpactStackSizeValid(stackSize))
        {
            throw new ArgumentOutOfRangeException(nameof(stackSize));
        }

        return 0.35 * Math.Clamp(Math.Sqrt(stackSize), 1.0, 8.0);
    }

    /// <summary>Rejects non-finite or remote world coordinates for two reflection witnesses.</summary>
    /// <param name="values">Two consecutive XYZ world positions.</param>
    /// <returns><see langword="true"/> when both positions fit the bounded test world.</returns>
    internal static bool AreWitnessCoordinatesBounded(params double[] values)
    {
        return values.Length == 6
            && values.All(static value => double.IsFinite(value))
            && Math.Abs(values[0]) <= 30_000_000.0
            && values[1] is >= -1024.0 and <= 1_048_576.0
            && Math.Abs(values[2]) <= 30_000_000.0
            && Math.Abs(values[3]) <= 30_000_000.0
            && values[4] is >= -1024.0 and <= 1_048_576.0
            && Math.Abs(values[5]) <= 30_000_000.0;
    }

    /// <summary>Recognizes the persistent ownership marker used by isolated test entities.</summary>
    /// <param name="origin">Entity attribute read from a copied test world.</param>
    /// <returns>True only for an origin written by this runtime-test support mod.</returns>
    internal static bool IsScenarioOwnedOrigin(string? origin) =>
        origin?.StartsWith(TestEntityOriginPrefix, StringComparison.Ordinal) == true;

    /// <summary>Recognizes the two loose-granite fallbacks used for controlled ballistic impacts.</summary>
    /// <param name="code">Loaded collectible code.</param>
    /// <returns>True for the official loose stone or stripped-fixture rock fallback.</returns>
    internal static bool IsReferenceImpactCollectible(AssetLocation? code) =>
        code is not null
        && string.Equals(code.Domain, "game", StringComparison.Ordinal)
        && (string.Equals(code.Path, "stone-granite", StringComparison.Ordinal)
            || string.Equals(code.Path, "rock-granite", StringComparison.Ordinal));

    /// <summary>
    /// Removes copied leftovers inside the dedicated lake staging region before spawning new
    /// witnesses. The harness always mutates a disposable world copy, so this also migrates old
    /// untagged loose-stone test entities without touching the source save or unrelated creatures.
    /// </summary>
    /// <param name="itemAnchor">Opaque witness anchor near the scripted impact targets.</param>
    /// <param name="humanoidAnchor">Second witness anchor bounding the staging region.</param>
    private void RemoveStaleTestEntities(Vec3d itemAnchor, Vec3d humanoidAnchor)
    {
        if (staleEntitiesCleaned)
        {
            return;
        }

        staleEntitiesCleaned = true;
        Vec3d center = new(
            (itemAnchor.X + humanoidAnchor.X) * 0.5,
            (itemAnchor.Y + humanoidAnchor.Y) * 0.5,
            (itemAnchor.Z + humanoidAnchor.Z) * 0.5);
        double anchorDeltaX = itemAnchor.X - humanoidAnchor.X;
        double anchorDeltaZ = itemAnchor.Z - humanoidAnchor.Z;
        float horizontalRange = (float)Math.Max(
            12.0,
            Math.Sqrt(anchorDeltaX * anchorDeltaX + anchorDeltaZ * anchorDeltaZ) * 0.5 + 12.0);
        Entity[] staleEntities = api!.World.GetEntitiesAround(
            center,
            horizontalRange,
            16.0f,
            entity => IsStaleTestEntity(entity, itemAnchor, humanoidAnchor));
        HashSet<long> removedIds = [];
        foreach (Entity entity in staleEntities)
        {
            if (removedIds.Add(entity.EntityId))
            {
                entity.Die(EnumDespawnReason.Removed);
            }
        }

        api.Logger.Notification(
            "[VintageRTX.Test] Isolated reflection staging cleanup removed {0} stale test entities.",
            removedIds.Count);
    }

    /// <summary>Matches tagged test entities and legacy untagged granite items near either anchor.</summary>
    /// <param name="entity">Loaded entity from the disposable copied world.</param>
    /// <param name="itemAnchor">First bounded staging anchor.</param>
    /// <param name="humanoidAnchor">Second bounded staging anchor.</param>
    /// <returns>True only for deterministic scenario debris safe to remove from the copy.</returns>
    private static bool IsStaleTestEntity(Entity entity, Vec3d itemAnchor, Vec3d humanoidAnchor)
    {
        if (IsScenarioOwnedOrigin(entity.Attributes.GetString("origin")))
        {
            return true;
        }

        if (entity is not EntityItem item
            || !IsReferenceImpactCollectible(item.Itemstack?.Collectible?.Code))
        {
            return false;
        }

        return IsInsideLegacyImpactRegion(entity.Pos, itemAnchor)
            || IsInsideLegacyImpactRegion(entity.Pos, humanoidAnchor);
    }

    /// <summary>Tests the narrow disposable basin region used by pre-tagging scenario versions.</summary>
    /// <param name="position">Candidate legacy loose-stone position.</param>
    /// <param name="anchor">Known test witness anchor.</param>
    /// <returns>True within twelve horizontal and vertical metres.</returns>
    private static bool IsInsideLegacyImpactRegion(EntityPos position, Vec3d anchor)
    {
        double deltaX = position.X - anchor.X;
        double deltaZ = position.Z - anchor.Z;
        return deltaX * deltaX + deltaZ * deltaZ <= 144.0
            && Math.Abs(position.Y - anchor.Y) <= 12.0;
    }

    /// <summary>Marks one entity for deterministic cleanup without altering its inventory payload.</summary>
    /// <param name="entity">Newly spawned witness or impact entity.</param>
    /// <param name="kind">Stable diagnostic kind appended to the ownership prefix.</param>
    private void TagScenarioEntity(Entity entity, string kind)
    {
        entity.Attributes.SetString("origin", TestEntityOriginPrefix + kind);
        entity.Attributes.SetString("vintagertxRunId", runId);
    }

    /// <summary>Stops the capture-only pin listener when the isolated support mod unloads.</summary>
    public override void Dispose()
    {
        if (api is not null && witnessPinListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(witnessPinListenerId);
            witnessPinListenerId = 0;
        }

        HashSet<long> removedIds = [];
        foreach (Entity entity in spawnedTestEntities)
        {
            if (removedIds.Add(entity.EntityId))
            {
                entity.Die(EnumDespawnReason.Removed);
            }
        }
        spawnedTestEntities.Clear();
        spawnedProjectileKinds.Clear();

        base.Dispose();
    }

    /// <summary>Converts metres per second to Vintage Story motion per nominal 60 Hz step.</summary>
    /// <param name="velocityX">World X velocity in metres per second.</param>
    /// <param name="velocityY">World Y velocity in metres per second.</param>
    /// <param name="velocityZ">World Z velocity in metres per second.</param>
    /// <returns>World displacement integrated during one nominal engine motion step.</returns>
    internal static Vec3d ConvertSiVelocityToEngineMotion(
        double velocityX,
        double velocityY,
        double velocityZ)
    {
        return new Vec3d(
            velocityX / EngineMotionStepsPerSecond,
            velocityY / EngineMotionStepsPerSecond,
            velocityZ / EngineMotionStepsPerSecond);
    }

    /// <summary>Finds the real loose-stone item with a block fallback for stripped fixtures.</summary>
    /// <param name="world">World registry containing loaded game collectibles.</param>
    /// <returns>The reference collectible, or <see langword="null"/> when neither code exists.</returns>
    internal static CollectibleObject? ResolveReferenceCollectible(IWorldAccessor world)
    {
        CollectibleObject? collectible = world.GetItem(new AssetLocation("game", "stone-granite"));
        return collectible ?? world.GetBlock(new AssetLocation("game", "rock-granite"));
    }
}
