using VintageRTX.Configuration;
using VintageRTX.Rendering;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageRTX;

/// <summary>
/// Vintage Story client lifecycle root. It isolates PBR assets before atlas discovery, then owns
/// configuration, voxelization, PBR terrain binding, final display rendering, runtime probes, startup
/// test state, chat commands, and symmetric event/resource teardown.
/// </summary>
public sealed class VintageRtxModSystem : ModSystem
{
    private readonly System.Action<ICoreAPI> installPbrDiscoveryPatch;
    private readonly System.Func<ICoreAPI, PbrSidecarAssetStore> takeOrCapturePbrAssets;
    private readonly System.Func<ICoreAPI, PbrSidecarAssetStore> capturePbrAssets;
    private ICoreClientAPI? api;
    private ConfigStore? configStore;
    private PbrSidecarAssetStore? pbrSidecarAssets;
    private VoxelScene? voxelScene;
    private PbrTerrainRenderer? pbrTerrainRenderer;
    private PbrEntityRenderer? pbrEntityRenderer;
    private FilmicDisplayRenderer? renderer;
    private RuntimeScenarioProbe? runtimeScenarioProbe;
    private long startupStateListenerId;
    private int startupWorldReadyTicks;
    private int startupGameMode = -1;
    private bool startupGameModeCommandSent;
    private bool startupWorldStateReady = true;

    /// <summary>Creates the production mod-system with the real asset-discovery services.</summary>
    public VintageRtxModSystem()
        : this(
            PbrAssetDiscoveryPatch.Install,
            PbrAssetDiscoveryPatch.TakeOrCapture,
            PbrSidecarAssetStore.CaptureAndHide)
    {
    }

    /// <summary>
    /// Creates a mod-system with test-controllable startup asset services. The
    /// seam stops before any rendering decision and preserves the exact
    /// production lifecycle ordering.
    /// </summary>
    internal VintageRtxModSystem(
        System.Action<ICoreAPI> installPbrDiscoveryPatch,
        System.Func<ICoreAPI, PbrSidecarAssetStore> takeOrCapturePbrAssets,
        System.Func<ICoreAPI, PbrSidecarAssetStore> capturePbrAssets)
    {
        this.installPbrDiscoveryPatch = installPbrDiscoveryPatch;
        this.takeOrCapturePbrAssets = takeOrCapturePbrAssets;
        this.capturePbrAssets = capturePbrAssets;
    }

    /// <summary>Restricts the renderer and its OpenGL dependencies to the client process.</summary>
    /// <param name="forSide">Current application side.</param><returns>True only for the client.</returns>
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    /// <summary>Runs before block/item loading order 0.2 so sidecars cannot enter vanilla albedo discovery.</summary>
    /// <returns>Lifecycle order 0.15.</returns>
    public override double ExecuteOrder() => 0.15;

    /// <summary>Installs asset-discovery interception before external asset enumeration.</summary>
    /// <param name="coreApi">Core API available on initial mod startup.</param>
    public override void Start(ICoreAPI coreApi)
    {
        installPbrDiscoveryPatch(coreApi);
    }

    /// <summary>Transfers sidecars isolated during discovery before vanilla texture collection proceeds.</summary>
    /// <param name="coreApi">Core API owning the active asset manager.</param>
    public override void AssetsLoaded(ICoreAPI coreApi)
    {
        // The block/item loader starts at order 0.2. Isolating exact PBR
        // suffixes here prevents the vanilla texture collector from treating
        // them as albedo while retaining their ordinary on-disk mod layout.
        pbrSidecarAssets = takeOrCapturePbrAssets(coreApi);
    }

    /// <summary>Constructs services in dependency order, registers renderers/commands, and starts test hooks.</summary>
    /// <param name="clientApi">Client world, renderer, assets, events, commands, and logger.</param>
    public override void StartClientSide(ICoreClientAPI clientApi)
    {
        api = clientApi;
        RegisterStartupWorldState(clientApi);
        pbrSidecarAssets ??= capturePbrAssets(clientApi);
        configStore = new ConfigStore(clientApi);
        voxelScene = new VoxelScene(clientApi);
        pbrTerrainRenderer = new PbrTerrainRenderer(clientApi, pbrSidecarAssets);
        pbrEntityRenderer = new PbrEntityRenderer(clientApi, pbrSidecarAssets);
        renderer = new FilmicDisplayRenderer(clientApi, () => configStore.Current, voxelScene);
        renderer.InstallFirstPersonReflectionIsolation();
        renderer.InstallProjectileLiquidCollisionBridge();
        runtimeScenarioProbe = RuntimeScenarioProbe.TryStart(
            clientApi,
            () => pbrTerrainRenderer.ReloadShader()
                && pbrEntityRenderer.ReloadShader()
                && renderer.ReloadShader(),
            renderer.QueueCapture,
            () => startupWorldStateReady,
            renderer.QueueDiagnosticCapture,
            () => renderer.IsDiagnosticCaptureIdle);

        clientApi.Event.ReloadShader += pbrTerrainRenderer.ReloadShader;
        clientApi.Event.ReloadShader += pbrEntityRenderer.ReloadShader;
        clientApi.Event.ReloadShader += renderer.ReloadShader;
        clientApi.Event.RegisterRenderer(pbrTerrainRenderer, EnumRenderStage.Opaque, "vintagertx-pbr-terrain");
        clientApi.Event.RegisterRenderer(pbrEntityRenderer, EnumRenderStage.Opaque, "vintagertx-pbr-entities");
        clientApi.Event.RegisterRenderer(
            renderer.EntityMirrorSourceRenderer,
            EnumRenderStage.Opaque,
            "vintagertx-entity-mirror-source");
        clientApi.Event.RegisterRenderer(
            renderer.ReflectionSourceRenderer,
            EnumRenderStage.Opaque,
            "vintagertx-reflection-source");
        clientApi.Event.RegisterRenderer(renderer, EnumRenderStage.AfterBlit, "vintagertx-display");
        RegisterCommands(clientApi);

        clientApi.Logger.Notification("[VintageRTX] Clean renderer bootstrap complete.");
    }

    /// <summary>Registers the delayed startup game-mode callback only for a valid environment override.</summary>
    /// <param name="clientApi">Client event registry.</param>
    private void RegisterStartupWorldState(ICoreClientAPI clientApi)
    {
        string? requestedMode = Environment.GetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE");
        if (!TryParseStartupGameMode(requestedMode, out startupGameMode))
        {
            return;
        }

        startupWorldStateReady = false;
        startupGameModeCommandSent = false;
        startupWorldReadyTicks = 0;
        startupStateListenerId = clientApi.Event.RegisterGameTickListener(ApplyStartupWorldState, 100);
    }

    /// <summary>
    /// Parses the optional startup game-mode command. The sentinel value keeps
    /// the runtime callback unregistered when the environment value is absent
    /// or malformed.
    /// </summary>
    internal static bool TryParseStartupGameMode(string? requestedMode, out int gameMode)
    {
        if (int.TryParse(requestedMode, out gameMode))
        {
            return true;
        }

        gameMode = -1;
        return false;
    }

    /// <summary>
    /// Sends the requested game mode on the first tick with a live player, then
    /// releases runtime scenarios only after synchronized player data confirms
    /// that the server applied it.
    /// </summary>
    /// <param name="deltaTime">Unused tick duration required by the event callback.</param>
    private void ApplyStartupWorldState(float deltaTime)
    {
        IClientPlayer? player = api?.World.Player;
        if (player?.Entity is null)
        {
            return;
        }

        if (!startupGameModeCommandSent)
        {
            api!.SendChatMessage($"/gamemode {startupGameMode}", null!);
            startupGameModeCommandSent = true;
            api.Logger.Notification(
                "[VintageRTX] Startup validation state requested: /gamemode {0}.",
                startupGameMode);
            return;
        }

        startupWorldReadyTicks++;
        if (player.WorldData is null
            || (int)player.WorldData.CurrentGameMode != startupGameMode)
        {
            return;
        }

        startupWorldStateReady = true;
        api!.Logger.Notification(
            "[VintageRTX] Startup validation state applied: /gamemode {0}.",
            startupGameMode);
        api.Event.UnregisterGameTickListener(startupStateListenerId);
        startupStateListenerId = 0;
    }

    /// <summary>Registers the complete <c>/vrtx</c> command tree and typed argument parsers.</summary>
    /// <param name="clientApi">Client chat-command service.</param>
    private void RegisterCommands(ICoreClientAPI clientApi)
    {
        clientApi.ChatCommands.Create("vrtx")
            .WithDescription("VintageRTX rendering controls")
            .BeginSubCommand("status")
                .HandleWith(_ => TextCommandResult.Success(BuildStatus()))
            .EndSubCommand()
            .BeginSubCommand("toggle")
                .HandleWith(_ => Toggle())
            .EndSubCommand()
            .BeginSubCommand("reload")
                .HandleWith(_ => Reload())
            .EndSubCommand()
            .BeginSubCommand("lighting")
                .HandleWith(_ => ToggleLighting())
            .EndSubCommand()
            .BeginSubCommand("voxel")
                .HandleWith(_ => ToggleVoxelLighting())
            .EndSubCommand()
            .BeginSubCommand("capture")
                .HandleWith(_ => QueueCapture())
            .EndSubCommand()
            .BeginSubCommand("debug")
                .WithArgs(clientApi.ChatCommands.Parsers.Word("view"))
                .HandleWith(SetDebugView)
            .EndSubCommand()
            .BeginSubCommand("preset")
                .WithArgs(clientApi.ChatCommands.Parsers.Word("name"))
                .HandleWith(SetPreset)
            .EndSubCommand()
            .BeginSubCommand("profile")
                .WithArgs(clientApi.ChatCommands.Parsers.Word("name"))
                .HandleWith(SetRenderProfile)
            .EndSubCommand();
    }

    /// <summary>Builds one operational snapshot spanning config, renderer, voxel scene, PBR, and debug state.</summary>
    /// <returns>Human-readable status text.</returns>
    private string BuildStatus()
    {
        VintageRtxConfig config = configStore!.Current;
        return $"VintageRTX 0.2 | enabled={config.Enabled} | profile={config.RenderProfile.ToString().ToLowerInvariant()} | renderer={renderer!.Status} | "
            + $"exposure={config.Exposure:0.00}, contrast={config.Contrast:0.00}, "
            + $"saturation={config.Saturation:0.00}, vibrance={config.Vibrance:0.00} | "
            + $"ssgi={config.ScreenSpaceLightingEnabled}, rays={config.RayCount}x{config.RaySteps}, "
            + $"distance={config.RayDistance:0.00}, voxel={config.VoxelLightingEnabled}, "
            + $"scene={voxelScene!.Status}, pbr={pbrTerrainRenderer!.Status}, "
            + $"entity-pbr={pbrEntityRenderer!.Status}, debug={config.DebugView}";
    }

    /// <summary>Toggles the entire final pass and persists the normalized configuration.</summary>
    /// <returns>Localized command result.</returns>
    private TextCommandResult Toggle()
    {
        VintageRtxConfig config = configStore!.Current;
        config.Enabled = !config.Enabled;
        configStore.Save();

        string key = config.Enabled ? "vintagertx:effects-enabled" : "vintagertx:effects-disabled";
        return TextCommandResult.Success(Lang.Get(key));
    }

    /// <summary>Reloads disk configuration and clears sticky renderer faults/history.</summary>
    /// <returns>Localized success result.</returns>
    private TextCommandResult Reload()
    {
        configStore!.Reload();
        renderer!.ResetFault();
        return TextCommandResult.Success(Lang.Get("vintagertx:config-reloaded"));
    }

    /// <summary>Toggles screen-space lighting, restores final view, and persists the change.</summary>
    /// <returns>Localized command result.</returns>
    private TextCommandResult ToggleLighting()
    {
        VintageRtxConfig config = configStore!.Current;
        config.ScreenSpaceLightingEnabled = !config.ScreenSpaceLightingEnabled;
        config.DebugView = VintageRtxDebugView.Final;
        configStore.Save();

        string key = config.ScreenSpaceLightingEnabled
            ? "vintagertx:lighting-enabled"
            : "vintagertx:lighting-disabled";
        return TextCommandResult.Success(Lang.Get(key));
    }

    /// <summary>Toggles voxel transport, restores final view, and persists the change.</summary>
    /// <returns>Command result describing the new state.</returns>
    private TextCommandResult ToggleVoxelLighting()
    {
        VintageRtxConfig config = configStore!.Current;
        config.VoxelLightingEnabled = !config.VoxelLightingEnabled;
        config.DebugView = VintageRtxDebugView.Final;
        configStore.Save();
        return TextCommandResult.Success(
            $"VintageRTX voxel lighting: {(config.VoxelLightingEnabled ? "enabled" : "disabled")}.");
    }

    /// <summary>Queues one comparison pair while rejecting overwrite of an existing request.</summary>
    /// <returns>Success with output directory, or a pending-request error.</returns>
    private TextCommandResult QueueCapture()
    {
        if (!renderer!.QueueCapture())
        {
            return TextCommandResult.Error("A VintageRTX comparison capture is already queued.");
        }

        return TextCommandResult.Success(
            $"VintageRTX comparison capture queued. Output: {renderer.CaptureDirectory}");
    }

    /// <summary>Parses a command alias, persists the resolved diagnostic view, and reports invalid choices.</summary>
    /// <param name="args">Chat arguments whose first value is the requested view.</param>
    /// <returns>Success or available-view error.</returns>
    private TextCommandResult SetDebugView(TextCommandCallingArgs args)
    {
        string requestedView = ((string)args[0]).ToLowerInvariant();
        if (!TryResolveDebugView(requestedView, out VintageRtxDebugView debugView))
        {
            return TextCommandResult.Error(
                "Available debug views: final, normal, position, lighting, reflection, reflectionsource, voxel, bounce, voxelreflection, visibility, shadowmask, components, material, water, surfacefield, wetness.");
        }

        configStore!.Current.DebugView = debugView;
        configStore.Save();
        return TextCommandResult.Success($"VintageRTX debug view: {debugView}.");
    }

    /// <summary>
    /// Maps every documented command alias to its stable debug-view enum. The
    /// parser is kept independent of chat argument objects so aliases and
    /// rejection behavior can be exhaustively verified by unit tests.
    /// </summary>
    internal static bool TryResolveDebugView(
        string? requestedView,
        out VintageRtxDebugView debugView)
    {
        debugView = requestedView?.ToLowerInvariant() switch
        {
            "final" or "off" => VintageRtxDebugView.Final,
            "normal" or "normals" => VintageRtxDebugView.Normal,
            "position" or "depth" => VintageRtxDebugView.Position,
            "lighting" or "ssgi" => VintageRtxDebugView.Lighting,
            "voxel" or "albedo" => VintageRtxDebugView.VoxelAlbedo,
            "visibility" or "shadow" => VintageRtxDebugView.VoxelVisibility,
            "shadowmask" or "voxel-shadow" => VintageRtxDebugView.VoxelShadow,
            "voxelreflection" or "voxel-reflection" or "offscreen" => VintageRtxDebugView.VoxelReflection,
            "reflection" or "reflections" or "ssr" => VintageRtxDebugView.Reflection,
            "bounce" or "gi" or "voxelbounce" => VintageRtxDebugView.VoxelBounce,
            "components" or "transport" => VintageRtxDebugView.TransportComponents,
            "material" or "pbr" or "roughness" or "metallic" => VintageRtxDebugView.Material,
            "water" or "fluid" or "planar" => VintageRtxDebugView.Water,
            "wet" or "wetness" or "rain" => VintageRtxDebugView.Wetness,
            "reflectionsource" or "reflection-source" or "source" => VintageRtxDebugView.ReflectionSource,
            "surfacefield" or "surface-field" or "liquidfield" => VintageRtxDebugView.LiquidSurfaceField,
            "entitymirror" or "entity-mirror" or "entities" => VintageRtxDebugView.EntityMirror,
            _ => (VintageRtxDebugView)(-1)
        };

        if (!Enum.IsDefined(debugView))
        {
            return false;
        }

        return true;
    }

    /// <summary>Applies one authored preset atomically and resets render history/faults.</summary>
    /// <param name="args">Chat arguments whose first value is a preset name.</param>
    /// <returns>Success or supported-preset error.</returns>
    private TextCommandResult SetPreset(TextCommandCallingArgs args)
    {
        string preset = ((string)args[0]).ToLowerInvariant();

        try
        {
            configStore!.Current.ApplyPreset(preset);
            configStore.Save();
            renderer!.ResetFault();
            return TextCommandResult.Success($"VintageRTX preset '{preset}' applied.");
        }
        catch (ArgumentOutOfRangeException)
        {
            return TextCommandResult.Error("Available presets: neutral, cinematic, vivid.");
        }
    }

    /// <summary>Applies and persists one hardware rendering profile, then invalidates stale history.</summary>
    /// <param name="args">Chat arguments whose first value is a profile name or documented alias.</param>
    /// <returns>Success with the canonical profile name, or an error listing supported profiles.</returns>
    private TextCommandResult SetRenderProfile(TextCommandCallingArgs args)
    {
        string requestedProfile = (string)args[0];
        if (!TryResolveRenderProfile(requestedProfile, out VintageRtxRenderProfile profile))
        {
            return TextCommandResult.Error(
                "Available rendering profiles: performance, balanced, quality, ultra, extreme, cinematic, custom.");
        }

        VintageRtxConfig config = configStore!.Current;
        config.ApplyRenderProfile(profile);
        configStore.Save();
        renderer!.ResetFault();
        return TextCommandResult.Success(
            $"VintageRTX rendering profile '{profile.ToString().ToLowerInvariant()}' applied.");
    }

    /// <summary>Maps English and French hardware-profile aliases to their stable enum value.</summary>
    /// <param name="requestedProfile">User-entered profile name.</param>
    /// <param name="profile">Resolved profile when the method returns true.</param>
    /// <returns>Whether the supplied name identifies one supported profile.</returns>
    internal static bool TryResolveRenderProfile(
        string? requestedProfile,
        out VintageRtxRenderProfile profile)
    {
        profile = requestedProfile?.ToLowerInvariant() switch
        {
            "performance" or "low" or "faible" => VintageRtxRenderProfile.Performance,
            "balanced" or "medium" or "equilibre" or "équilibré" => VintageRtxRenderProfile.Balanced,
            "quality" or "high" or "qualite" or "qualité" => VintageRtxRenderProfile.Quality,
            "ultra" or "maximum" or "max" => VintageRtxRenderProfile.Ultra,
            "extreme" or "enthusiast" or "nouvelle-generation" => VintageRtxRenderProfile.Extreme,
            "cinematic" or "cinematique" or "cinématique" or "offline" => VintageRtxRenderProfile.Cinematic,
            "custom" or "manual" or "personnalise" or "personnalisé" => VintageRtxRenderProfile.Custom,
            _ => (VintageRtxRenderProfile)(-1)
        };

        return Enum.IsDefined(profile);
    }

    /// <summary>Unregisters handlers/renderers, deletes owned resources, persists config, and clears patches.</summary>
    public override void Dispose()
    {
        if (api is not null
            && renderer is not null
            && pbrTerrainRenderer is not null
            && pbrEntityRenderer is not null)
        {
            api.Event.ReloadShader -= pbrTerrainRenderer.ReloadShader;
            api.Event.ReloadShader -= pbrEntityRenderer.ReloadShader;
            api.Event.ReloadShader -= renderer.ReloadShader;
            api.Event.UnregisterRenderer(pbrTerrainRenderer, EnumRenderStage.Opaque);
            api.Event.UnregisterRenderer(pbrEntityRenderer, EnumRenderStage.Opaque);
            api.Event.UnregisterRenderer(renderer.EntityMirrorSourceRenderer, EnumRenderStage.Opaque);
            api.Event.UnregisterRenderer(renderer.ReflectionSourceRenderer, EnumRenderStage.Opaque);
            api.Event.UnregisterRenderer(renderer, EnumRenderStage.AfterBlit);
            pbrTerrainRenderer.Dispose();
            pbrEntityRenderer.Dispose();
            renderer.Dispose();
        }

        if (api is not null && startupStateListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(startupStateListenerId);
            startupStateListenerId = 0;
        }

        configStore?.Save();
        PbrAssetDiscoveryPatch.Uninstall();
        runtimeScenarioProbe?.Dispose();
        voxelScene?.Dispose();
        renderer = null;
        pbrTerrainRenderer = null;
        pbrEntityRenderer = null;
        pbrSidecarAssets = null;
        voxelScene = null;
        runtimeScenarioProbe = null;
        configStore = null;
        api = null;

        base.Dispose();
    }
}
