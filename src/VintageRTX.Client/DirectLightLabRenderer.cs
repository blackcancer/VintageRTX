using VintageRTX.Core.Diagnostics;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client;

/// <summary>Opt-in developer preview. No allocations/uploads/draws while disabled; never a world-image replacement.</summary>
internal sealed class DirectLightLabRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly EmissionAssetCatalog emissions;
    private DirectSurfaceFrame? surfaces;
    private LightRegistry? registry;
    private SceneTextureSet? sceneTextures;
    private LightTexture? lightTexture;
    private DirectImagePass? pass;
    private GpuLightData lightData = new();
    private bool enabled, lit = true, resetRequested = true, disposed;
    private double startSeconds;
    private long frame;
    public string? LastError { get; private set; }
    public double RenderOrder => 0.98;
    public int RenderRange => 0;
    public DirectLightLabRenderer(ICoreClientAPI api, EmissionAssetCatalog emissions)
    {
        this.api = api; this.emissions = emissions;
        api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "vintagertx-direct-light-lab");
        api.Event.LeaveWorld += LeaveWorld;
    }
    public string Configure(string mode)
    {
        if (disposed) return "VintageRTX: laboratoire arrêté.";
        switch (mode.ToLowerInvariant())
        {
            case "on": enabled = true; lit = true; break;
            case "off": enabled = false; break;
            case "dark": enabled = true; lit = false; break;
            case "lit": enabled = true; lit = true; break;
            default: return "Utilisation : .vrtxlightlab on | off | dark | lit";
        }
        if (LastError is not null) { resetRequested = true; LastError = null; }
        return $"VintageRTX : laboratoire synthétique {(enabled ? "visible" : "masqué")}, sources {(lit ? "actives" : "éteintes")}. L'image du monde n'est pas remplacée.";
    }
    private void LeaveWorld() { enabled = false; resetRequested = true; }
    private void Initialize()
    {
        Release(); surfaces = DirectLightLab.Create(); registry = new(surfaces.World);
        var data = new GpuSceneData(); data.Update(surfaces.Scene);
        sceneTextures = new(); sceneTextures.Upload(data);
        lightTexture = new(); lightData = new();
        pass = new(Read("scene-query.glsl"), Read("light-query.glsl"), Read("material-query.glsl"), Read("direct-image.glsl"));
        startSeconds = api.InWorldEllapsedMilliseconds / 1000.0; frame = 0; resetRequested = false;
    }
    private string Read(string name) => api.Assets.Get(new AssetLocation("vintagertx", "shaderincludes/" + name)).ToText();
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (disposed || !enabled || stage != EnumRenderStage.Ortho || api.World?.Player?.Entity is null) return;
        try
        {
            if (resetRequested || surfaces is null) Initialize();
            // Consume the patched profile, including current candle-group multiplicity. These are
            // explicitly synthetic test intensities, not claimed photometry of a world chandelier.
            EmissionSelection profile = emissions.Resolve("game:bunchocandles-3", EmissionTarget.Block, null);
            DirectLightLab.SetLights(registry!, surfaces!.Anchor, profile, lit);
            LightFrame lights = registry!.Capture(++frame, Math.Max(0, api.InWorldEllapsedMilliseconds / 1000.0 - startSeconds));
            lightData.Update(lights, surfaces.Anchor); lightTexture!.Upload(lightData);
            pass!.Render(surfaces, sceneTextures!, lightTexture);
            double width = Math.Max(1, Math.Min(512, api.Render.FrameWidth - 32));
            api.Render.RenderTexture(pass.PreviewTexture, 16, 16, width, width * surfaces.Height / surfaces.Width);
        }
        catch (Exception exception)
        {
            enabled = false; resetRequested = true; LastError = exception.Message;
            api.Logger.Warning("[VintageRTX] Direct-light laboratory disabled after failure: {0}. Native world image remains active.", exception.Message);
        }
    }
    private void Release()
    {
        pass?.Dispose(); pass = null; sceneTextures?.Dispose(); sceneTextures = null;
        lightTexture?.Dispose(); lightTexture = null; surfaces = null; registry = null;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; enabled = false;
        api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho); api.Event.LeaveWorld -= LeaveWorld; Release();
    }
}
