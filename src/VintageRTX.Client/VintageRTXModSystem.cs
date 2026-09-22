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
    private RuntimeWorldTests? runtimeTests;
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
        runtimeTests = new RuntimeWorldTests(api, observer, worldRenderer, () => lightLab?.Configure("off"));
        api.ChatCommands.Create("vrtx")
            .WithDescription("Global VintageRTX control: on, off, toggle, status, coverage or retry. Off suspends observations and GPU uploads.")
            .WithArgs(api.ChatCommands.Parsers.Word("mode"))
            .HandleWith(args => TextCommandResult.Success(ConfigureGlobal(args[0]?.ToString() ?? "status")));
        api.ChatCommands.Create("vrtxtest")
            .WithDescription("In-game A/B/A smoke test of the current view: start, status or abort. No world edits; exports actual framebuffer evidence.")
            .WithArgs(api.ChatCommands.Parsers.Word("mode"))
            .HandleWith(args => TextCommandResult.Success(runtimeTests?.Configure(args[0]?.ToString() ?? "status") ?? "VintageRTX runtime tests stopped."));
        api.ChatCommands.Create("vrtxrewrite")
            .WithDescription("Report rewrite data and world rendering state.")
            .HandleWith(_ => TextCommandResult.Success((observer?.Describe() ?? "VintageRTX rewrite stopped.") + "\n" + (worldRenderer?.Describe() ?? "World renderer stopped.")));
        api.ChatCommands.Create("vrtxemissions")
            .WithDescription("Report patched emission catalog, revision and validation status.")
            .HandleWith(_ => TextCommandResult.Success(emissionAssets?.Describe() ?? "VintageRTX emission catalog stopped."));
        api.ChatCommands.Create("vrtxlightlab")
            .WithDescription("Synthetic direct PBR image laboratory: on, off, dark or lit. Does not replace the world image.")
            .WithArgs(api.ChatCommands.Parsers.Word("mode"))
            .HandleWith(args => TextCommandResult.Success(ConfigureLab(args[0]?.ToString() ?? "")));
        api.ChatCommands.Create("vrtxworld")
            .WithDescription("Native world lighting: on, off, coverage, status or retry.")
            .WithArgs(api.ChatCommands.Parsers.Word("mode"))
            .HandleWith(args => TextCommandResult.Success(ConfigureGlobal(args[0]?.ToString() ?? "status")));
        api.Logger.Notification("[VintageRTX] Rewrite R05: native world direct-light integration enabled; .vrtx on/off/toggle controls all mod observations and .vrtxtest start records a world A/B/A run. Shader connection is deferred to the first render frame; .vrtxworld status reports actual readiness. Laboratory remains opt-in.");
    }
    private string ConfigureLab(string mode)
    {
        if (lightLab is null) return "VintageRTX laboratory stopped.";
        if (mode.ToLowerInvariant() is "on" or "dark" or "lit")
        {
            if (worldRenderer?.Mode == 0) return "VintageRTX désactivé : utilisez .vrtx on avant le laboratoire.";
            runtimeTests?.Abort("Interrupted by a laboratory command.");
        }
        return lightLab.Configure(mode);
    }
    private string ConfigureGlobal(string mode)
    {
        if (worldRenderer is null) return "VintageRTX world renderer stopped.";
        if (mode.ToLowerInvariant() is "on" or "off" or "toggle" or "coverage" or "retry")
        {
            runtimeTests?.Abort("Interrupted by a user rendering command.");
            if (mode.Equals("off", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("toggle", StringComparison.OrdinalIgnoreCase) && worldRenderer.Mode != 0)
                lightLab?.Configure("off");
        }
        return worldRenderer.Configure(mode) + "\n" + observer?.Describe();
    }
    public override void Dispose()
    {
        runtimeTests?.Dispose(); runtimeTests = null;
        worldRenderer?.Dispose(); worldRenderer = null;
        lightLab?.Dispose(); lightLab = null; observer?.Dispose(); observer = null;
        emissionAssets = null; base.Dispose();
    }
}
