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
    private static readonly EnumShaderProgram[] ProgramKinds = [EnumShaderProgram.Chunkopaque,
        EnumShaderProgram.Chunktopsoil, EnumShaderProgram.Entityanimated, EnumShaderProgram.Standard];
    private readonly int[] handles = new int[ProgramKinds.Length];
    private WorldLightingBinding? binding;
    private bool initialized, disposed;
    private int mode = 1;
    private long boundFrames, lastLightFrame = -1;
    private string status = "waiting for first world render";
    internal string? LastError { get; private set; }
    internal int Mode => mode;
    internal long BoundFrames => boundFrames;
    internal long LastLightFrame => lastLightFrame;
    internal bool Installed => shaders.Installed;
    internal int LastAppliedMode { get; private set; } = -1;
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
            case "on":
                if (LastError is not null) { initialized = false; observer.RequestRefresh(); }
                mode = 1; LastError = null; break;
            case "off": mode = 0; break;
            case "toggle": mode = mode == 0 ? 1 : 0; LastError = null; break;
            case "coverage": mode = 2; LastError = null; break;
            case "retry": mode = 1; initialized = false; LastError = null; observer.RequestRefresh(); break;
            case "status": return Describe();
            default: return "Utilisation : .vrtxworld on | off | toggle | coverage | status | retry";
        }
        observer.SetEnabled(mode != 0);
        return Describe();
    }
    internal void RestoreMode(int previousMode)
    {
        if (previousMode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(previousMode));
        if (disposed) return;
        // Restoring a test's prior preference must not erase a real shader/driver error. Only an
        // explicit activation/retry command may retry it; a failed test cannot silently clear it.
        mode = previousMode;
        observer.SetEnabled(mode != 0 && LastError is null);
    }
    internal string Describe() => $"VintageRTX world R04: mode={mode}, last opaque mode={LastAppliedMode}, shader assets={shaders.Installed}, "
        + $"bound frames={boundFrames}, light frame={lastLightFrame}, status={status}, error={LastError ?? "none"}. "
        + "Native terrain consumes authored diffuse/conductor materials; dynamic receivers retain diffuse light. "
        + "Coverage: green=resolved, blue=blocked, cyan=partial, magenta=unresolved, orange=unobserved receiver. "
        + "Partial transport keeps measured contributions only; entirely unresolved transport retains native shading. "
        + "Unknown materials, unsupported casters, emissive surfaces, water and secondary reflections retain their native path.";
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
                    throw new InvalidOperationException("Patched native shader compilation failed.");
                status = "native world shaders patched and recompiled";
                api.Logger.Notification("[VintageRTX] R03 native world lighting connected: chunkopaque + chunktopsoil + entityanimated + standard. Use .vrtxworld status or coverage.");
            }
            catch (Exception e)
            {
                // A thrown reload is just as transactional as a false return. Never leave patched
                // assets behind after a partially compiled pipeline, or retry that pipeline every frame.
                if (shaders.Installed)
                {
                    shaders.Dispose();
                    try
                    {
                        if (!api.Shader.ReloadShaders())
                            e = new InvalidOperationException(e.Message + " Native shader restoration also failed.", e);
                    }
                    catch (Exception restore)
                    { e = new AggregateException("World shader connection and native restoration failed.", e, restore); }
                }
                Fail(e);
            }
            return;
        }
        if (stage != EnumRenderStage.Opaque) return;
        LastAppliedMode = -1;
        if (!shaders.Installed) return;
        End();
        try
        {
            for (int i = 0; i < ProgramKinds.Length; i++)
            {
                IShaderProgram? program = api.Shader.GetProgram((int)ProgramKinds[i]);
                if (program is null || program.Disposed)
                { status = "waiting for native opaque programs"; return; }
                // Query current objects each frame: settings/reload may replace linked programs.
                // Reuse the small handle array, not uniform locations that could belong to an old link.
                handles[i] = program.ProgramId;
            }
            foreach (int handle in handles) WorldLightingBinding.Prime(handle);
            LastAppliedMode = 0;
            if (mode == 0 || LastError is not null) { status = "native mode"; return; }
            if (api.World?.Player?.Entity is null || !observer.TryBorrowWorldFrame(out WorldGpuFrame frame))
            { status = "waiting for coherent published world/light data (native fallback)"; return; }
            // The same double reference drives native chunk and entity camera-relative worldPos.
            // Do not use the rounded float PlayerPos or assume that the camera is the player's feet.
            var origin = api.Render.ShaderUniforms.playerReferencePos;
            binding = new(handles, frame, new DVec3(origin.X, origin.Y, origin.Z), mode);
            LastAppliedMode = mode;
            lastLightFrame = frame.Lights.Frame; boundFrames++; status = "native world lighting bound";
        }
        catch (Exception e) { End(); Fail(e); }
    }
    private void Fail(Exception e)
    {
        LastError = e.Message; status = "native fallback after error";
        api.Logger.Error("[VintageRTX] World lighting: {0}. World replacement disabled; .vrtxworld retry after correction.", e.Message);
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
        public double RenderOrder => .79;
        public int RenderRange => 0;
        public void OnRenderFrame(float dt, EnumRenderStage stage) { if (stage == EnumRenderStage.Opaque) owner.End(); }
        public void Dispose() { }
    }
}
