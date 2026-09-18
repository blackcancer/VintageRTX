using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace VintageRTX.Rendering;

/// <summary>
/// Captures actual unlit colour during native opaque rasterization. The hook resolves the concrete
/// implementation of the public IShaderProgram.Use contract rather than guessing a private type.
/// Absence of the required MRT slot or hook disables only this optional material capture.
/// </summary>
internal sealed class RawAlbedoCapture : IRenderer
{
    /// <summary>Owner of this feature's shader-activation postfixes only.</summary>
    private const string HarmonyId = "vintagertx.raw-albedo";
    private readonly ICoreClientAPI api;
    private readonly RawAlbedoTarget target = new();
    private readonly HashSet<MethodInfo> patchedMethods = [];
    private readonly Dictionary<int, bool> writers = [];
    private Harmony? harmony;
    private static RawAlbedoCapture? current;
    private bool faulted;
    private bool warned;
    private bool publishedLogged;
    private bool enabled = true;
    private bool disposed;

    /// <summary>Creates the capture coordinator without accessing OpenGL.</summary>
    /// <param name="api">Client rendering and shader services.</param>
    internal RawAlbedoCapture(ICoreClientAPI api) { this.api = api; }
    /// <summary>Starts before PBR binding and native opaque terrain submission.</summary>
    public double RenderOrder => 0.355;
    /// <summary>Participates in every visible frame.</summary>
    public int RenderRange => 0;
    /// <summary>Gets a complete published albedo/depth texture or zero.</summary>
    internal int TextureId => enabled && !faulted ? target.TextureId : 0;
    /// <summary>Reports actual frame availability rather than only the user's desired option.</summary>
    internal string Status => faulted ? "unavailable" : TextureId > 0 ? "captured-rgb" : "compatibility";
    /// <summary>Enables capture on the next opaque boundary, never publishes stale disabled data.</summary>
    internal bool Enabled { get => enabled; set => enabled = value; }

    /// <summary>Starts an optional material attachment transaction for the current Primary target.</summary>
    /// <param name="deltaTime">Unused renderer frame duration.</param>
    /// <param name="stage">Only opaque callbacks are accepted.</param>
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (disposed || stage != EnumRenderStage.Opaque) return;
        if (!enabled || faulted) { target.End(false); return; }
        try
        {
            IShaderProgram terrain = api.Shader.GetProgram((int)EnumShaderProgram.Chunkopaque);
            IShaderProgram entity = api.Shader.GetProgram((int)EnumShaderProgram.Entityanimated);
            if (!EnsureHook(terrain)) return;
            if (entity is not null) EnsureHook(entity);
            int index = (int)EnumFrameBuffer.Primary;
            if (api.Render.FrameBuffers.Count <= index) return;
            FrameBufferRef primary = api.Render.FrameBuffers[index];
            if (primary.Disposed || primary.ColorTextureIds is not { Length: 4 }
                || primary.Width != api.Render.FrameWidth || primary.Height != api.Render.FrameHeight) return;
            current = this;
            if (!target.Begin(primary.FboId, primary.Width, primary.Height) && !warned)
            {
                warned = true;
                api.Logger.Warning("[VintageRTX] Raw albedo capture unavailable: a free fifth Primary attachment is required. Compatibility materials remain active.");
            }
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>Completes capture before the clean snapshot and the deferred first-person replay.</summary>
    internal void EndFrame()
    {
        if (disposed) return;
        try
        {
            target.End(enabled && !faulted);
            if (target.Ready && !publishedLogged)
            {
                publishedLogged = true;
                api.Logger.Notification("[VintageRTX] Unlit RGB capture active: opaque attachment 4, paired view depth, {0}x{1}.",
                    api.Render.FrameWidth, api.Render.FrameHeight);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>Installs a narrowly owned postfix on the actual implementation of public Use().</summary>
    /// <param name="program">A native opaque program; null during startup is not an error.</param>
    /// <returns>Whether program activation can control the extra draw route.</returns>
    private bool EnsureHook(IShaderProgram? program)
    {
        if (program is null || program.Disposed || program.ProgramId <= 0) return false;
        InterfaceMapping map = program.GetType().GetInterfaceMap(typeof(IShaderProgram));
        int slot = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(IShaderProgram.Use)
            && method.GetParameters().Length == 0);
        if (slot < 0) return false;
        MethodInfo method = map.TargetMethods[slot];
        if (patchedMethods.Contains(method)) return true;
        harmony ??= new Harmony(HarmonyId);
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(RawAlbedoCapture), nameof(AfterUse)));
        patchedMethods.Add(method);
        return true;
    }

    /// <summary>Disables the extra route for all programs not positively identified as albedo writers.</summary>
    /// <param name="__instance">Concrete native shader implementing the public interface.</param>
    private static void AfterUse(object __instance)
    {
        RawAlbedoCapture? capture = current;
        if (capture is null || !capture.target.Active || capture.faulted) return;
        try
        {
            bool known = false;
            if (__instance is IShaderProgram program && program.ProgramId > 0)
            {
                if (!capture.writers.TryGetValue(program.ProgramId, out known))
                {
                    known = !program.Oit
                        && (string.Equals(program.PassName, "chunkopaque", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(program.PassName, "entityanimated", StringComparison.OrdinalIgnoreCase))
                        && GL.GetFragDataLocation(program.ProgramId, "vintagertxUnlitAlbedo") == RawAlbedoTarget.AttachmentIndex;
                    capture.writers[program.ProgramId] = known;
                }
            }
            capture.target.SetWriter(known);
        }
        catch (Exception error) { capture.Fail(error); }
    }

    /// <summary>Disables just raw material capture and avoids repeated frame-level log spam.</summary>
    /// <param name="error">Original hook or OpenGL failure.</param>
    private void Fail(Exception error)
    {
        faulted = true;
        try { target.End(false); } catch { /* Preserve the original failure for diagnosis. */ }
        if (warned) return;
        warned = true;
        api.Logger.Warning("[VintageRTX] Raw albedo capture disabled; compatibility materials retained: {0}", error);
    }

    /// <summary>Invalidates program-ID caches after shader replacement and permits a clean retry.</summary>
    /// <returns>True; actual resource activation is deferred until the next opaque frame.</returns>
    internal bool ReloadShader()
    {
        target.End(false);
        writers.Clear();
        faulted = false;
        warned = false;
        publishedLogged = false;
        return true;
    }

    /// <summary>Removes only this feature's hooks and resources; never deletes native attachments.</summary>
    public void Dispose()
    {
        if (disposed) return;
        if (ReferenceEquals(current, this)) current = null;
        foreach (MethodInfo method in patchedMethods) harmony?.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        patchedMethods.Clear();
        writers.Clear();
        target.Dispose();
        disposed = true;
    }
}
