using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client;

/// <summary>Independent rewrite host: source observation and regional GPU data, not a replacement image yet.</summary>
public sealed class VintageRTXModSystem : ModSystem
{
    private ClientSourceObserver? observer;
    public override bool ShouldLoad(EnumAppSide forSide)=>forSide==EnumAppSide.Client;
    public override void StartClientSide(ICoreClientAPI api)
    {
        observer=new(api);
        api.ChatCommands.Create("vrtxrewrite")
            .WithDescription("Report rewrite data and upload state; native image rendering remains active.")
            .HandleWith(_=>TextCommandResult.Success(observer?.Describe()??"VintageRTX rewrite stopped."));
        api.Logger.Notification("[VintageRTX] Rewrite R01a: regional geometry and source data. PBR lighting/reflection image passes are not active yet.");
    }
    public override void Dispose() { observer?.Dispose();observer=null;base.Dispose(); }
}
