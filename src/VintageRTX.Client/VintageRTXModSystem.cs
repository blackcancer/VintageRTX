using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client;

/// <summary>Rewrite host with independent scene observations and an opt-in direct-image laboratory.</summary>
public sealed class VintageRTXModSystem : ModSystem
{
    private ClientSourceObserver? observer;
    private EmissionAssetCatalog? emissionAssets;
    private DirectLightLabRenderer? lightLab;
    private WorldLightingRenderer? worldRenderer;
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
    public override double ExecuteOrder() => 0.15;
    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api is not ICoreClientAPI) return;
        emissionAssets ??= new(api.Assets, api.Logger); emissionAssets.LoadPatched();
    }
    public override void StartClientSide(ICoreClientAPI api)
    {
        emissionAssets ??= new(api.Assets, api.Logger);
        if (!emissionAssets.LoadAttempted) emissionAssets.LoadPatched();
        observer = new ClientSourceObserver(api, emissionAssets);
        lightLab = new DirectLightLabRenderer(api, emissionAssets);
        worldRenderer = new WorldLightingRenderer(api, observer);
        api.ChatCommands.Create("vrtxrewrite")
            .WithDescription("Report rewrite data and world rendering state.")
            .HandleWith(_ => TextCommandResult.Success((observer?.Describe() ?? "VintageRTX rewrite stopped.") + "\n" + (worldRenderer?.Describe() ?? "World renderer stopped.")));
        api.ChatCommands.Create("vrtxemissions")
            .WithDescription("Report patched emission catalog, revision and validation status.")
            .HandleWith(_ => TextCommandResult.Success(emissionAssets?.Describe() ?? "VintageRTX emission catalog stopped."));
        api.ChatCommands.Create("vrtxlightlab")
            .WithDescription("Synthetic direct PBR image laboratory: on, off, dark or lit. Does not replace the world image.")
            .WithArgs(api.ChatCommands.Parsers.Word("mode"))
            .HandleWith(args => TextCommandResult.Success(lightLab?.Configure(args[0]?.ToString() ?? "") ?? "VintageRTX laboratory stopped."));
        api.ChatCommands.Create("vrtxworld")
            .WithDescription("Native world lighting: on, off, coverage, status or retry.")
            .WithArgs(api.ChatCommands.Parsers.Word("mode"))
            .HandleWith(args => TextCommandResult.Success(worldRenderer?.Configure(args[0]?.ToString() ?? "status") ?? "VintageRTX world renderer stopped."));
        api.Logger.Notification("[VintageRTX] Rewrite R03: native world direct-light integration enabled. Shader connection is deferred to the first render frame; .vrtxworld status reports actual readiness. Laboratory remains opt-in.");
    }
    public override void Dispose()
    {
        worldRenderer?.Dispose(); worldRenderer = null;
        lightLab?.Dispose(); lightLab = null; observer?.Dispose(); observer = null;
        emissionAssets = null; base.Dispose();
    }
}
