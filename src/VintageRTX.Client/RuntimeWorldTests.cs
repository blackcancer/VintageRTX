using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using VintageRTX.Core.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VintageRTX.Client;

/// <summary>
/// Opt-in campaign in the user's currently loaded world. No synthetic lights, blocks, camera writes,
/// commands to the server, network upload or authentication access. It never starts automatically.
/// A PASS is exclusively an A/B/A world-toggle smoke test, not full RTX acceptance.
/// </summary>
internal sealed class RuntimeWorldTests : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly ClientSourceObserver observer;
    private readonly WorldLightingRenderer renderer;
    private readonly Action hideLab;
    private readonly Func<RuntimeCapturedImage> capture;
    private readonly Func<double> clock;
    private readonly string root;
    private RuntimeToggleSequence? sequence;
    private readonly List<object> samples = new();
    private readonly Dictionary<string, RuntimeCapturedImage> images = new();
    private readonly List<double> frameMilliseconds = new();
    private int savedMode, phaseFrame, totalFrames;
    private long editRevision;
    private CameraWitness? camera;
    private bool disposed, autoArmed;
    private string? directory;
    private string status = "NOT_RUN", reason = "No in-game campaign has been requested.";
    private RuntimeToggleVerdict? verdict;
    private object? hardware;
    private double startTime;
    internal bool Active => sequence is not null;
    internal string? ArtifactDirectory => directory;
    public double RenderOrder => 1;
    public int RenderRange => 0;

    internal RuntimeWorldTests(ICoreClientAPI api, ClientSourceObserver observer, WorldLightingRenderer renderer,
        Action hideLab, string? outputRoot = null, Func<RuntimeCapturedImage>? capture = null, Func<double>? clock = null)
    {
        this.api = api; this.observer = observer; this.renderer = renderer; this.hideLab = hideLab;
        this.capture = capture ?? RuntimeFrameCapture.ReadDefaultViewport;
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        string? configuredRoot = Environment.GetEnvironmentVariable("VINTAGERTX_RUNTIME_OUTPUT");
        root = outputRoot ?? (!string.IsNullOrWhiteSpace(configuredRoot) && Path.IsPathRooted(configuredRoot)
            ? configuredRoot : Path.Combine(GamePaths.Logs, "VintageRTX", "runtime"));
        autoArmed = string.Equals(Environment.GetEnvironmentVariable("VINTAGERTX_RUNTIME_AUTOTEST"), "toggle", StringComparison.OrdinalIgnoreCase);
        api.Event.RegisterRenderer(this, EnumRenderStage.AfterBlit, "vintagertx-runtime-acceptance");
        api.Event.LeaveWorld += OnLeaveWorld;
    }
    internal string Configure(string command)
    {
        if (disposed) return "VintageRTX runtime tests stopped.";
        switch (command.ToLowerInvariant())
        {
            case "start":
                if (Active) return "Une campagne est déjà active. .vrtxtest status | abort";
                if (api.World?.Player?.Entity?.Pos is null) return "Chargez un monde avant .vrtxtest start.";
                try { Start(); }
                catch (Exception e) { Finish("ERROR", e.Message); }
                return Describe();
            case "abort": Abort("Cancelled by the user."); return Describe();
            case "status": return Describe();
            default: return "Utilisation : .vrtxtest start | status | abort. Test A/B/A dans le monde courant, sans modification de blocs.";
        }
    }
    internal string Describe() => $"VintageRTX runtime-toggle: {status}, phase={sequence?.Phase.ToString() ?? "none"}, "
        + $"frames={totalFrames}. {reason} Artefacts: {directory ?? "none"}. "
        + "Portée : activation et retour natif, pas validation physique des ombres/reflets/GI ni benchmark GPU.";

    private void Start()
    {
        directory = null; autoArmed = false; savedMode = renderer.Mode; samples.Clear(); images.Clear(); frameMilliseconds.Clear();
        camera = null; verdict = null; hardware = null; totalFrames = phaseFrame = 0;
        startTime = clock(); sequence = new(startTime); status = "RUNNING";
        reason = "Fermez le chat, restez immobile devant une surface éclairée. N'ouvrez pas le menu de pause.";
        directory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        // Only mod binaries and shader assets are fingerprinted. No clientsettings, tokens, account
        // details, savegame bytes, chat contents or multiplayer address are read or exported.
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var modules = new SortedDictionary<string, Guid>(StringComparer.Ordinal);
        void AssemblyInput(string name, Assembly assembly)
        {
            modules[name] = assembly.ManifestModule.ModuleVersionId;
            if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                inputs[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        }
        AssemblyInput("VintageRTX.dll", typeof(VintageRTXModSystem).Assembly);
        AssemblyInput("VintageRTX.Core.dll", typeof(VintageRTX.Core.Lighting.LightRegistry).Assembly);
        // Stream-loaded assemblies legitimately have no location. Preserve that limitation and
        // their MVID instead of failing a manual campaign or hashing a guessed sidecar DLL.
        string? location = Path.GetDirectoryName(typeof(VintageRTXModSystem).Assembly.Location);
        if (!string.IsNullOrEmpty(location))
        {
            string info = Path.Combine(location, "modinfo.json");
            if (File.Exists(info)) inputs["modinfo.json"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(info)));
            string shaders = Path.Combine(location, "assets", "vintagertx", "shaderincludes");
            if (Directory.Exists(shaders))
                foreach (string full in Directory.EnumerateFiles(shaders, "*.glsl"))
                    inputs["shaderincludes/" + Path.GetFileName(full)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
        }
        WriteJson("runtime-inputs.json", new { targetGame = "1.22.7", developmentBase = "95d5798c", controlProtocol = "R05", inputs, modules,
            provenanceLimit = "File hashes and loaded module identities, not an attestation of all patched in-memory assets. Stream-loaded modules may have no file hash." });
        hideLab(); renderer.Configure("on"); WriteResult();
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (disposed || stage != EnumRenderStage.AfterBlit) return;
        if (autoArmed && api.PlayerReadyFired && api.World?.Player?.Entity?.Pos is not null)
            Configure("start");
        if (sequence is null) return;
        try
        {
            totalFrames++;
            if (api.World?.Player?.Entity?.Pos is null) { Finish("ABORTED", "World or player left during campaign."); return; }
            if (renderer.LastError is not null) { Finish("FAIL", "World renderer: " + renderer.LastError); return; }
            if (clock() - startTime > 90) { Finish("FAIL", "Campaign deadline exceeded. " + renderer.Describe()); return; }
            if (api.IsGamePaused) return; // Wall-clock timeout still applies when rendering continues.
            if (camera is not null && (!camera.Matches(ReadCamera()) || observer.EditRevision != editRevision))
            { Finish("INCONCLUSIVE", "Camera, projection, dimension or blocks changed during the A/B/A comparison."); return; }
            bool ready = renderer.Installed && renderer.LastAppliedMode == renderer.Mode;
            if (renderer.Mode != 0)
                ready &= observer.Enabled && observer.CurrentFrame is not null
                    && renderer.LastLightFrame == observer.CurrentFrame.Frame;
            else ready &= !observer.Enabled && observer.CurrentFrame is null;
            // AfterBlit measures the actual world draw that just completed, not the command's intent.
            RuntimeToggleStep step = sequence.Advance(clock(), ready);
            phaseFrame++;
            if (phaseFrame > 5 && ready && dt > 0 && float.IsFinite(dt) && frameMilliseconds.Count < 2048)
                frameMilliseconds.Add(dt * 1000.0); // Host frame delta, not isolated GPU time.
            if (step.Capture is { } name)
            {
                var observedCamera = ReadCamera();
                if (camera is null) { camera = observedCamera; editRevision = observer.EditRevision; }
                RuntimeCapturedImage image = capture();
                if (images.Count != 0)
                {
                    var first = images.Values.First();
                    if (image.Width != first.Width || image.Height != first.Height)
                    { Finish("INCONCLUSIVE", "Framebuffer size changed during comparison."); return; }
                }
                hardware ??= new { vendor = GL.GetString(StringName.Vendor), renderer = GL.GetString(StringName.Renderer), version = GL.GetString(StringName.Version) };
                string path = Path.Combine(directory!, name + ".png");
                using (var stream = File.Create(path)) RuntimePng.Write(stream, image.Width, image.Height, image.Rgba);
                // Keep three A/B/A arrays only. Coverage is exported as evidence, not color-guessed
                // into a fictitious physical visibility percentage after tone mapping and fog.
                if (name != "coverage") images.Add(name, image);
                samples.Add(new { name, image.Width, image.Height, stage = "AfterBlit-before-GUI",
                    encoding = "RGBA8-display-native-viewport", screenshot = name + ".png",
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    camera = observedCamera, observer = observer.Describe(), world = renderer.Describe(),
                    mode = renderer.Mode, appliedMode = renderer.LastAppliedMode, boundFrames = renderer.BoundFrames,
                    lightFrame = renderer.LastLightFrame, lightCount = observer.CurrentFrame?.Samples.Length,
                    editRevision = observer.EditRevision, seconds = clock() - startTime,
                    hostFrameMilliseconds = frameMilliseconds.ToArray(),
                    timingScope = "Host dt only; phase warmup and screenshot/encoding frames excluded; no performance gate." });
                WriteResult();
            }
            if (step.SetMode is { } mode)
            {
                renderer.Configure(mode switch { 0 => "off", 2 => "coverage", _ => "on" });
                frameMilliseconds.Clear(); phaseFrame = 0;
            }
            if (step.Complete)
            {
                verdict = RuntimeToggleAnalysis.Compare(images["native-before"].Rgba, images["enabled"].Rgba, images["native-after"].Rgba);
                Finish(verdict.Status, verdict.Reason);
            }
        }
        catch (Exception e) { Finish("ERROR", e.GetType().Name + ": " + e.Message); }
    }

    internal void Abort(string message)
    { autoArmed = false; if (Active) Finish("ABORTED", message); }
    private void OnLeaveWorld() => Abort("LeaveWorld event.");
    private void Finish(string outcome, string detail)
    {
        bool restore = Active; sequence = null; status = outcome; reason = detail;
        try
        {
            if (restore) renderer.RestoreMode(savedMode);
            if (directory is not null) WriteResult();
        }
        catch (Exception e)
        {
            status = "ERROR"; reason += " Report or state restoration failed: " + e.Message;
            api.Logger.Error("[VintageRTX] Runtime test cleanup: {0}", reason);
        }
        finally { images.Clear(); frameMilliseconds.Clear(); }
        api.Logger.Notification("[VintageRTX] Runtime test {0}: {1}. Artifacts: {2}", status, reason, directory ?? "none");
    }
    private void WriteResult() => WriteJson("runtime-result.json", new {
        schemaVersion = 1, test = "runtime-toggle-smoke", status, reason,
        scope = "Actual current world when invoked by the player. PASS does not qualify physical light transport, shadows, reflections, GI, latency or performance.",
        targetGame = "1.22.7", beforeMode = savedMode, restoredMode = sequence is null ? renderer.Mode : (int?)null,
        totalFrames, elapsedSeconds = clock() - startTime, phase = sequence?.Phase.ToString(),
        hardware, verdict, samples,
        notTested = new[] { "source-placement latency", "held-light sockets", "individual candle wick shadows", "environment reflections", "water optics", "GI", "GPU performance" }
    });
    private void WriteJson(string name, object value)
    {
        string path = Path.Combine(directory!, name), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
    private CameraWitness ReadCamera()
    {
        var pos = api.World.Player.Entity.Pos; var origin = api.Render.ShaderUniforms.playerReferencePos;
        return new(pos.Dimension, origin.X, origin.Y, origin.Z,
            (double[])api.Render.PerspectiveViewMat.Clone(), (double[])api.Render.PerspectiveProjectionMat.Clone());
    }
    internal sealed record CameraWitness(int Dimension, double X, double Y, double Z, double[] View, double[] Projection)
    {
        internal bool Matches(CameraWitness other)
        {
            if (Dimension != other.Dimension || !double.IsFinite(X + Y + Z + other.X + other.Y + other.Z)
                || Math.Abs(X - other.X) > .01 || Math.Abs(Y - other.Y) > .01 || Math.Abs(Z - other.Z) > .01) return false;
            return Close(View, other.View) && Close(Projection, other.Projection);
        }
        private static bool Close(double[] a, double[] b)
        {
            if (a.Length != 16 || b.Length != 16) return false;
            for (int i = 0; i < 16; i++) if (!double.IsFinite(a[i]) || !double.IsFinite(b[i]) || Math.Abs(a[i] - b[i]) > .0001) return false;
            return true;
        }
    }
    public void Dispose()
    {
        if (disposed) return; Abort("Mod disposed."); disposed = true;
        api.Event.UnregisterRenderer(this, EnumRenderStage.AfterBlit); api.Event.LeaveWorld -= OnLeaveWorld;
    }
}
