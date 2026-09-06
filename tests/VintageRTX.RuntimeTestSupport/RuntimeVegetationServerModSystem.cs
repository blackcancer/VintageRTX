using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageRTX.RuntimeTestSupport;

/// <summary>
/// Places a bounded, server-authoritative patch of stock vegetation in the disposable copy of
/// <c>foggy village world</c>. The command is absent from normal play and from every other runtime
/// scenario.
/// </summary>
public sealed class RuntimeVegetationServerModSystem : ModSystem
{
    /// <summary>Scenario identity that alone authorizes map mutation.</summary>
    internal const string VegetationShadowScenario = "vegetation-shadow-map";

    /// <summary>Immutable real-game block layout shared with deterministic tests.</summary>
    private static readonly VegetationPlacement[] Placements =
    [
        new(-3, -1, "game:tallgrass-verytall-free"),
        new(-1, 1, "game:tallgrass-tall-free"),
        new(1, -1, "game:tallgrass-medium-free"),
        new(3, 1, "game:flower-redtopgrass-free"),
        new(0, 2, "game:fern-eaglefern")
    ];

    private ICoreServerAPI? api;
    private bool placed;

    /// <summary>Restricts the injector to the integrated server.</summary>
    /// <param name="forSide">Mod-loader side being considered.</param>
    /// <returns><see langword="true"/> only for the authoritative server side.</returns>
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>
    /// Registers the isolated placement command only when the parent harness selected the
    /// vegetation-shadow scenario.
    /// </summary>
    /// <param name="serverApi">Authoritative public server API.</param>
    public override void StartServerSide(ICoreServerAPI serverApi)
    {
        if (!IsVegetationShadowScenario(
                Environment.GetEnvironmentVariable("VINTAGERTX_TEST_SCENARIO")))
        {
            return;
        }

        api = serverApi;
        CommandArgumentParsers parsers = serverApi.ChatCommands.Parsers;
        serverApi.ChatCommands.Create("vintagertxtestvegetation")
            .WithDescription("Place the isolated VintageRTX real-map vegetation witness")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("place")
                .WithArgs(
                    parsers.Int("centerX"),
                    parsers.Int("plantY"),
                    parsers.Int("centerZ"))
                .HandleWith(PlaceVegetationPatch)
            .EndSubCommand();

        serverApi.Logger.Notification(
            "[VintageRTX.Test] Server vegetation placement command ready: scenario={0}, plants={1}.",
            VegetationShadowScenario,
            Placements.Length);
    }

    /// <summary>
    /// Resolves and validates every stock plant before performing any write, then verifies every
    /// resulting block through the same public accessor.
    /// </summary>
    /// <param name="args">Integer center X/Z and the common plant-layer Y coordinate.</param>
    /// <returns>Typed command result identifying placement success or the exact rejected contract.</returns>
    private TextCommandResult PlaceVegetationPatch(TextCommandCallingArgs args)
    {
        int centerX = (int)args[0];
        int plantY = (int)args[1];
        int centerZ = (int)args[2];
        if (placed)
        {
            return TextCommandResult.Error("The vegetation witness has already been placed for this run.");
        }
        if (!ArePlacementCoordinatesBounded(centerX, plantY, centerZ))
        {
            return TextCommandResult.Error("Vegetation placement coordinates are outside the safe test contract.");
        }

        IBlockAccessor accessor = api!.World.BlockAccessor;
        List<(BlockPos Position, Block Plant, Block Previous)> resolved = [];
        foreach (VegetationPlacement placement in Placements)
        {
            Block? plant = accessor.GetBlock(new AssetLocation(placement.Code));
            BlockPos column = new(
                centerX + placement.OffsetX,
                0,
                centerZ + placement.OffsetZ);
            int resolvedPlantY = accessor.GetRainMapHeightAt(column) + 1;
            BlockPos position = new(
                centerX + placement.OffsetX,
                resolvedPlantY,
                centerZ + placement.OffsetZ);
            Block ground = accessor.GetBlock(position.DownCopy(), BlockLayersAccess.MostSolid);
            Block current = accessor.GetBlock(position, BlockLayersAccess.MostSolid);
            Block above = accessor.GetBlock(position.UpCopy(), BlockLayersAccess.MostSolid);
            if (Math.Abs(resolvedPlantY - plantY) > 2)
            {
                return TextCommandResult.Error(
                    $"Vegetation witness position {position} exceeds the accepted natural slope.");
            }
            if (plant is null || plant.Id == 0)
            {
                return TextCommandResult.Error($"Required stock plant '{placement.Code}' is unavailable.");
            }
            if (!IsSolidGround(ground) || !IsReplaceableAir(current) || !IsReplaceableAir(above))
            {
                return TextCommandResult.Error(
                    $"Vegetation witness position {position} is not open above solid ground.");
            }

            resolved.Add((position, plant, current));
        }

        foreach ((BlockPos position, Block plant, _) in resolved)
        {
            accessor.SetBlock(plant.Id, position, BlockLayersAccess.Solid);
        }

        if (resolved.Any(entry =>
                accessor.GetBlock(entry.Position, BlockLayersAccess.MostSolid).Id
                    != entry.Plant.Id))
        {
            foreach ((BlockPos position, _, Block previous) in resolved)
            {
                accessor.SetBlock(previous.Id, position, BlockLayersAccess.Solid);
            }
            return TextCommandResult.Error("The authoritative block accessor rejected part of the vegetation patch.");
        }

        placed = true;
        api.Logger.Notification(
            "[VintageRTX.Test] Server vegetation patch placed: count={0}, center=({1},{2},{3}), codes={4}.",
            resolved.Count,
            centerX.ToString(CultureInfo.InvariantCulture),
            plantY.ToString(CultureInfo.InvariantCulture),
            centerZ.ToString(CultureInfo.InvariantCulture),
            string.Join(',', resolved.Select(static entry => entry.Plant.Code)));
        return TextCommandResult.Success("VintageRTX real-map vegetation witness placed.");
    }

    /// <summary>Checks the exact opt-in scenario name without accepting prefixes or aliases.</summary>
    /// <param name="scenario">Environment-provided scenario identity.</param>
    /// <returns>Whether real-map vegetation placement is authorized.</returns>
    internal static bool IsVegetationShadowScenario(string? scenario) => string.Equals(
        scenario?.Trim(),
        VegetationShadowScenario,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>Restricts placement to legal world-scale integer coordinates.</summary>
    /// <param name="centerX">Patch center X.</param>
    /// <param name="plantY">Common plant layer Y.</param>
    /// <param name="centerZ">Patch center Z.</param>
    /// <returns>Whether all coordinates remain inside the bounded test contract.</returns>
    internal static bool ArePlacementCoordinatesBounded(int centerX, int plantY, int centerZ) =>
        Math.Abs((long)centerX) <= 30_000_000L
        && plantY is >= -1024 and <= 1_048_576
        && Math.Abs((long)centerZ) <= 30_000_000L;

    /// <summary>Returns a detached copy of the authored placement contract.</summary>
    /// <returns>Five stock plant identities and their center-relative offsets.</returns>
    internal static IReadOnlyList<VegetationPlacement> GetPlacements() =>
        Placements.ToArray();

    /// <summary>Determines whether a candidate can support a placed plant.</summary>
    /// <param name="block">Ground candidate from the solid layer.</param>
    /// <returns>Whether it has non-air collision geometry.</returns>
    internal static bool IsSolidGround(Block? block) => block is not null
        && block.Id != 0
        && (block.BlockMaterial is EnumBlockMaterial.Soil
            or EnumBlockMaterial.Gravel
            or EnumBlockMaterial.Sand
            or EnumBlockMaterial.Stone)
        && block.CollisionBoxes is { Length: > 0 };

    /// <summary>Determines whether a solid-layer cell may receive the isolated witness.</summary>
    /// <param name="block">Current block occupying the target cell.</param>
    /// <returns>Whether the cell is empty air or harmless non-collidable ground vegetation.</returns>
    internal static bool IsReplaceableAir(Block? block) => block is null
        || block.Id == 0
        || block.BlockMaterial == EnumBlockMaterial.Air
        || (block.BlockMaterial == EnumBlockMaterial.Plant
            && block.CollisionBoxes is not { Length: > 0 });
}

/// <summary>One stock plant and its horizontal offset from the staged patch center.</summary>
/// <param name="OffsetX">Signed center-relative X offset.</param>
/// <param name="OffsetZ">Signed center-relative Z offset.</param>
/// <param name="Code">Fully qualified stock block code.</param>
internal readonly record struct VegetationPlacement(
    int OffsetX,
    int OffsetZ,
    string Code);
