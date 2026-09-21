using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client;

/// <summary>Rewrite host with independent scene observations and an opt-in direct-image laboratory.</summary>
public sealed class VintageRTXModSystem : ModSystem
{
    private ClientSourceObserver? observer;
    private EmissionAssetCatalog? emissionAssets;
    private DirectLightLabRenderer? lightLab;
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
        api.ChatCommands.Create("vrtxrewrite")
            .WithDescription("Report rewrite data and upload state; native world rendering remains active.")
            .HandleWith(_ => TextCommandResult.Success(observer?.Describe() ?? "VintageRTX rewrite stopped."));
        api.ChatCommands.Create("vrtxemissions")
            .WithDescription("Report patched emission catalog, revision and validation status.")
            .HandleWith(_ => TextCommandResult.Success(emissionAssets?.Describe() ?? "VintageRTX emission catalog stopped."));
        api.ChatCommands.Create("vrtxlightlab")
            .WithDescription("Synthetic direct PBR image laboratory: on, off, dark or lit. Does not replace the world image.")
            .WithArgs(api.ChatCommands.Parsers.Word("mode"))
            .HandleWith(args => TextCommandResult.Success(lightLab?.Configure(args[0]?.ToString() ?? "") ?? "VintageRTX laboratory stopped."));
        api.Logger.Notification("[VintageRTX] Rewrite R02: material-aware direct image pass available through .vrtxlightlab on. Laboratory disabled by default; native world image remains active.");
    }
    public override void Dispose()
    {
        lightLab?.Dispose(); lightLab = null; observer?.Dispose(); observer = null;
        emissionAssets = null; base.Dispose();
    }
}
