using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client;

/// <summary>The rewrite owns no legacy hooks/shaders. R00 observes sources; native rendering remains intact.</summary>
public sealed class VintageRTXModSystem : ModSystem
{
    private ClientSourceObserver? observer;
    public override bool ShouldLoad(EnumAppSide forSide) => forSide==EnumAppSide.Client;
    public override void StartClientSide(ICoreClientAPI api)
    {
        observer=new ClientSourceObserver(api);
        api.ChatCommands.Create("vrtxrewrite")
            .WithDescription("Report rewrite foundation state; this build does not replace the native renderer.")
            .HandleWith(_=>TextCommandResult.Success(observer?.Describe()??"VintageRTX rewrite stopped."));
        api.Logger.Notification("[VintageRTX] Rewrite R00: source observation only; GPU photorealistic rendering is not active.");
    }
    public override void Dispose() { observer?.Dispose(); observer=null; base.Dispose(); }
}
