using VintageRTX.Core.Geometry;
using Vintagestory.API.Client;

namespace VintageRTX.Client;

/// <summary>
/// Active world integration. The native terrain (0.37) and opaque entities (0.4) evaluate the new
/// transport while rasterizing their real surfaces. No synthetic surfaces or framebuffer readback.
/// </summary>
internal sealed class WorldLightingRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly ClientSourceObserver observer;
    private readonly WorldShaderAssets shaders;
    private readonly EndStage end;
    private WorldLightingBinding? binding;
    private bool initialized, disposed;
    private int mode = 1;
    private long boundFrames, lastLightFrame = -1;
    private string status = "waiting for first world render";
    internal string? LastError { get; private set; }
    public double RenderOrder => .36;
    public int RenderRange => 0;
    internal WorldLightingRenderer(ICoreClientAPI api, ClientSourceObserver observer)
    {
        this.api = api; this.observer = observer; shaders = new(api.Assets); end = new(this);
        api.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintagertx-world-shaders");
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vintagertx-world-lighting");
        api.Event.RegisterRenderer(end, EnumRenderStage.Opaque, "vintagertx-world-lighting-end");
        api.Event.LeaveWorld += LeaveWorld;
    }
    private void LeaveWorld() { lastLightFrame = -1; status = "waiting for world"; }
    internal string Configure(string command)
    {
        if (disposed) return "VintageRTX world renderer stopped.";
        switch (command.ToLowerInvariant())
        {
            case "on": mode = 1; LastError = null; break;
            case "off": mode = 0; break;
            case "coverage": mode = 2; LastError = null; break;
            case "retry": mode = 1; initialized = false; LastError = null; break;
            case "status": return Describe();
            default: return "Utilisation : .vrtxworld on | off | coverage | status | retry";
        }
        return Describe();
    }
    internal string Describe() => $"VintageRTX world R03: mode={mode}, shader assets={shaders.Installed}, "
        + $"bound frames={boundFrames}, light frame={lastLightFrame}, status={status}, error={LastError ?? "none"}. "
        + "Native diffuse terrain/opaque entities consume traced local light. Unknown transport retains native shading; water, mirror transport and animated casters are not replaced.";
    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (disposed) return;
        if (stage == EnumRenderStage.Before)
        {
            End(); // Also release an interrupted previous opaque interval.
            if (mode == 0 || initialized || api.World?.Player?.Entity is null) return;
            initialized = true;
            try
            {
                if (!shaders.Install()) throw new InvalidOperationException(shaders.LastError);
                // Public API reloads the actual registered native programs from the patched assets.
                if (!api.Shader.ReloadShaders())
                {
                    shaders.Dispose();
                    api.Shader.ReloadShaders();
                    throw new InvalidOperationException("Patched native shader compilation failed; original assets restored.");
                }
                status = "native world shaders patched and recompiled";
                api.Logger.Notification("[VintageRTX] R03 native world lighting connected: chunkopaque + entityanimated. Use .vrtxworld status or coverage.");
            }
            catch (Exception e) { Fail(e); }
            return;
        }
        if (stage != EnumRenderStage.Opaque || !shaders.Installed) return;
        End();
        try
        {
            IShaderProgram? terrain = api.Shader.GetProgram((int)EnumShaderProgram.Chunkopaque);
            IShaderProgram? entities = api.Shader.GetProgram((int)EnumShaderProgram.Entityanimated);
            if (terrain is null || entities is null || terrain.Disposed || entities.Disposed)
            { status = "waiting for native opaque programs"; return; }
            WorldLightingBinding.Prime(terrain.ProgramId); WorldLightingBinding.Prime(entities.ProgramId);
            if (mode == 0 || LastError is not null) { status = "native mode"; return; }
            if (api.World?.Player?.Entity is null || !observer.TryBorrowWorldFrame(out WorldGpuFrame frame))
            { status = "waiting for coherent published world/light data (native fallback)"; return; }
            // The same double reference drives native chunk and entity camera-relative worldPos.
            // Do not use the rounded float PlayerPos or assume that the camera is the player's feet.
            var origin = api.Render.ShaderUniforms.playerReferencePos;
            binding = new([terrain.ProgramId, entities.ProgramId], frame, new DVec3(origin.X, origin.Y, origin.Z), mode);
            lastLightFrame = frame.Lights.Frame; boundFrames++; status = "native world lighting bound";
        }
        catch (Exception e) { End(); Fail(e); }
    }
    private void Fail(Exception e)
    {
        LastError = e.Message; status = "native fallback after error";
        api.Logger.Error("[VintageRTX] World lighting: {0}. Original rendering retained; .vrtxworld retry after correction.", e.Message);
    }
    private void End() { binding?.Dispose(); binding = null; }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        api.Event.UnregisterRenderer(end, EnumRenderStage.Opaque);
        api.Event.LeaveWorld -= LeaveWorld;
        End(); shaders.Dispose();
    }
    private sealed class EndStage(WorldLightingRenderer owner) : IRenderer
    {
        public double RenderOrder => .41;
        public int RenderRange => 0;
        public void OnRenderFrame(float dt, EnumRenderStage stage) { if (stage == EnumRenderStage.Opaque) owner.End(); }
        public void Dispose() { }
    }
}
