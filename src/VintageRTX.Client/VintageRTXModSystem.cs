using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client;

/// <summary>Independent rewrite host: configured sources and regional GPU data, not a replacement image yet.</summary>
public sealed class VintageRTXModSystem : ModSystem
{
    private ClientSourceObserver? observer;
    private EmissionAssetCatalog? emissionAssets;
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
    // The native JSON patch loader runs at 0.05. Read its resulting asset, never the original disk bytes.
    public override double ExecuteOrder() => 0.15;
    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api is not ICoreClientAPI) return;
        emissionAssets ??= new(api.Assets, api.Logger);
        emissionAssets.LoadPatched();
    }
    public override void StartClientSide(ICoreClientAPI api)
    {
        emissionAssets ??= new(api.Assets, api.Logger);
        if (!emissionAssets.LoadAttempted) emissionAssets.LoadPatched();
        observer = new ClientSourceObserver(api, emissionAssets);
        api.ChatCommands.Create("vrtxrewrite")
            .WithDescription("Report rewrite data and upload state; native image rendering remains active.")
            .HandleWith(_ => TextCommandResult.Success(observer?.Describe() ?? "VintageRTX rewrite stopped."));
        api.ChatCommands.Create("vrtxemissions")
            .WithDescription("Report patched emission catalog, revision and validation status.")
            .HandleWith(_ => TextCommandResult.Success(emissionAssets?.Describe() ?? "VintageRTX emission catalog stopped."));
        api.Logger.Notification("[VintageRTX] Rewrite R01a: regional geometry and asset-configured sources. PBR lighting/reflection image passes are not active yet.");
    }
    public override void Dispose()
    { observer?.Dispose(); observer = null; emissionAssets = null; base.Dispose(); }
}
