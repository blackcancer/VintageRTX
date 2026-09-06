using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageRTX.RuntimeTestSupport;

/// <summary>
/// Removes moving non-player entities around the authored lantern room only in the disposable
/// <c>lantern-night</c> world copy. This separates fixed-emitter/shadow stability from physically
/// correct animal motion while leaving the source save and normal gameplay untouched.
/// </summary>
public sealed class RuntimeLightStabilityServerModSystem : ModSystem
{
    /// <summary>Exact opt-in scenario allowed to mutate the copied world.</summary>
    private const string LanternNightScenario = "lantern-night";
    /// <summary>Horizontal extent that covers the authored room and its immediately visible pens.</summary>
    internal const float IsolationHorizontalRadius = 24.0f;
    /// <summary>Vertical extent that covers every visible floor around the fixed camera.</summary>
    internal const float IsolationVerticalRadius = 12.0f;

    /// <summary>Server API retained only after the exact isolated scenario has been authorized.</summary>
    private ICoreServerAPI? api;

    /// <summary>Restricts entity removal to the authoritative server world.</summary>
    /// <param name="forSide">Application side considered by the mod loader.</param>
    /// <returns><see langword="true"/> only for the server side.</returns>
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>Registers the one-shot isolation command only for the opted-in copied world.</summary>
    /// <param name="serverApi">Authoritative world and command API.</param>
    public override void StartServerSide(ICoreServerAPI serverApi)
    {
        if (!IsLanternNightScenario(Environment.GetEnvironmentVariable("VINTAGERTX_TEST_SCENARIO")))
        {
            return;
        }

        api = serverApi;
        serverApi.ChatCommands.Create("vintagertxtestlight")
            .WithDescription("VintageRTX isolated fixed-light test controls")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("isolate")
                .HandleWith(IsolateDynamicEntities)
            .EndSubCommand();
        serverApi.Logger.Notification(
            "[VintageRTX.Test] Fixed-light entity isolation command ready for the disposable lantern world.");
    }

    /// <summary>Despawns non-player entities near the caller in the disposable scenario copy.</summary>
    /// <param name="args">Authorized player invocation defining the isolation centre.</param>
    /// <returns>Successful command result including the exact number of removed entities.</returns>
    private TextCommandResult IsolateDynamicEntities(TextCommandCallingArgs args)
    {
        EntityPlayer player = args.Caller.Player.Entity;
        Vec3d center = new(player.Pos.X, player.Pos.Y, player.Pos.Z);
        Entity[] candidates = api!.World.GetEntitiesAround(
            center,
            IsolationHorizontalRadius,
            IsolationVerticalRadius,
            static entity => IsRemovableDynamicEntity(entity));
        HashSet<long> removedIds = [];
        foreach (Entity entity in candidates)
        {
            if (removedIds.Add(entity.EntityId))
            {
                entity.Die(EnumDespawnReason.Removed);
            }
        }

        api.Logger.Notification(
            "[VintageRTX.Test] Light-stability isolation removed {0} non-player entities from the disposable lantern world.",
            removedIds.Count);
        return TextCommandResult.Success(
            $"VintageRTX fixed-light isolation removed {removedIds.Count} non-player entities.");
    }

    /// <summary>Recognizes only the exact environment scenario allowed to remove copied entities.</summary>
    /// <param name="scenario">Untrusted environment value supplied by the runtime harness.</param>
    /// <returns><see langword="true"/> only for <c>lantern-night</c>, ignoring surrounding whitespace.</returns>
    internal static bool IsLanternNightScenario(string? scenario) => string.Equals(
        scenario?.Trim(),
        LanternNightScenario,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>Protects every player while accepting animals, items, projectiles, and other movers.</summary>
    /// <param name="entity">Loaded entity near the isolated camera.</param>
    /// <returns><see langword="true"/> when the copied-world entity is safe to remove.</returns>
    internal static bool IsRemovableDynamicEntity(Entity? entity) => entity is not null
        && entity is not EntityPlayer;
}
