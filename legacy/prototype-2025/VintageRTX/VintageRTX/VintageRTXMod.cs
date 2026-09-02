using HarmonyLib;
using System;
using System.Linq;
using VintageRTX.GUI;
using VintageRTX.src;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageRTX
{
    /// <summary>
    /// Main mod class for VintageRTX - Advanced graphics enhancements for Vintage Story
    /// </summary>
    public class VintageRTXMod : ModSystem
    {
        private ICoreClientAPI clientApi;
        private ShaderManager shaderManager;
        private TextureManager textureManager;
        private RenderIntegration renderIntegration;
        private ConfigHandler configHandler;
        private VintageRTXConfigGUI configGui;
        private Harmony harmony;
        private float frameTime = 0f;
        private int frameCount = 0;
        private float currentFps = 60f; // Valeur par défaut

        /// <summary>
        /// Determines if the mod should load on the specified app side
        /// </summary>
        /// <param name="forSide">The app side (Client/Server/Universal)</param>
        /// <returns>True if the mod should load on client side only</returns>
        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        /// <summary>
        /// Called when the mod is loaded on client side
        /// </summary>
        /// <param name="api">The client API interface</param>
        public override void StartClientSide(ICoreClientAPI api)
        {
            clientApi = api;

            api.Logger.Notification(Lang.Get("vintagertx:loading"));

            // Load configuration
            configHandler = new ConfigHandler(api);

            // Initialize Harmony for runtime patching
            harmony = new Harmony("com.vintagertx.shadermod");

            // Initialize texture manager
            textureManager = new TextureManager(api);

            // Initialize shader manager with configuration
            shaderManager = new ShaderManager(api, configHandler);

            // Apply Harmony patches to intercept shader loading
            ShaderPatcher.ApplyPatches(harmony, shaderManager);

            // Initialize render integration
            renderIntegration = new RenderIntegration(api, shaderManager, textureManager, configHandler);
            api.Event.RegisterRenderer(renderIntegration, EnumRenderStage.AfterFinalComposition);

            // Initialize configuration GUI
            configGui = new VintageRTXConfigGUI(api, configHandler, shaderManager);

            // Register chat commands
            RegisterDebugCommands();

            // Register event handlers
            RegisterEventHandlers();

            // Handle window resize - l'événement ReloadShader attend un bool en retour
            api.Event.ReloadShader += () => {
                try
                {
                    renderIntegration?.OnWindowResized();
                    return true; // Indiquer que l'événement a été traité avec succès
                }
                catch (Exception ex)
                {
                    api.Logger.Error($"[VintageRTX] Error handling window resize: {ex}");
                    return false; // Indiquer qu'il y a eu une erreur
                }
            };

            // Register hotkey for config GUI
            api.Input.RegisterHotKey("vintagertxconfig", Lang.Get("Open VintageRTX Config"), GlKeys.F9, HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler("vintagertxconfig", (a) => {
                configGui.TryOpen();
                return true;
            });

            api.Logger.Notification(Lang.Get("vintagertx:loaded"));
        }

        /// <summary>
        /// Registers chat commands for debugging and configuration
        /// </summary>
        private void RegisterDebugCommands()
        {
            clientApi.ChatCommands.Create("rtx")
                .WithDescription(Lang.Get("vintagertx:commands.description"))
                .BeginSubCommand("reload")
                    .WithDescription(Lang.Get("vintagertx:commands.reload"))
                    .HandleWith(OnReloadShaders)
                .EndSubCommand()
                .BeginSubCommand("toggle")
                    .WithDescription(Lang.Get("vintagertx:commands.toggle"))
                    .HandleWith(OnToggleEffects)
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription(Lang.Get("vintagertx:commands.status"))
                    .HandleWith(OnShowStatus)
                .EndSubCommand()
                .BeginSubCommand("preset")
                    .WithArgs(clientApi.ChatCommands.Parsers.Word("quality"))
                    .WithDescription(Lang.Get("vintagertx:commands.preset"))
                    .HandleWith(OnSetPreset)
                .EndSubCommand()
                .BeginSubCommand("config")
                    .WithDescription(Lang.Get("vintagertx:commands.config"))
                    .HandleWith(OnReloadConfig)
                .EndSubCommand()
                .BeginSubCommand("gui")
                    .WithDescription(Lang.Get("Open configuration GUI"))
                    .HandleWith(OnOpenGUI)
                .EndSubCommand()
                .BeginSubCommand("test")
                    .WithDescription("Test VintageRTX shader effects")
                    .HandleWith(OnTestEffects)
                .EndSubCommand()
                .BeginSubCommand("pbr")
                    .WithDescription("Show PBR texture statistics")
                    .HandleWith(OnShowPBRStats)
                .EndSubCommand();
        }

        /// <summary>
        /// Registers event handlers for shader hot reload and adaptive quality
        /// </summary>
        private void RegisterEventHandlers()
        {
            // Monitor configuration changes for hot reload
            if (configHandler.Config.EnableShaderHotReload)
            {
                clientApi.Event.RegisterGameTickListener(OnGameTick, 1000); // Check every second
            }

            // Handle adaptive quality
            if (configHandler.Config.AdaptiveQuality)
            {
                clientApi.Event.RegisterGameTickListener(OnAdaptiveQualityTick, 5000); // Every 5 seconds
            }

            // Add FPS counter update
            clientApi.Event.RegisterGameTickListener(UpdateFpsCounter, 16); // ~60 FPS
        }

        /// <summary>
        /// Handles the reload shaders command
        /// </summary>
        private TextCommandResult OnReloadShaders(TextCommandCallingArgs args)
        {
            try
            {
                shaderManager.ReloadShaders();
                return TextCommandResult.Success(Lang.Get("vintagertx:commands.reload.success"));
            }
            catch (Exception ex)
            {
                return TextCommandResult.Error(Lang.Get("vintagertx:commands.reload.error", ex.Message));
            }
        }

        /// <summary>
        /// Handles the toggle effects command
        /// </summary>
        private TextCommandResult OnToggleEffects(TextCommandCallingArgs args)
        {
            shaderManager.ToggleEffects();
            string statusKey = shaderManager.AreEffectsEnabled ?
                "vintagertx:commands.toggle.enabled" :
                "vintagertx:commands.toggle.disabled";
            return TextCommandResult.Success(Lang.Get(statusKey));
        }

        /// <summary>
        /// Handles the show status command
        /// </summary>
        private TextCommandResult OnShowStatus(TextCommandCallingArgs args)
        {
            var status = shaderManager.GetStatus();
            return TextCommandResult.Success(status);
        }

        /// <summary>
        /// Handles the quality preset command
        /// </summary>
        private TextCommandResult OnSetPreset(TextCommandCallingArgs args)
        {
            string preset = (string)args[0];

            if (!new[] { "low", "medium", "high", "ultra", "extreme" }.Contains(preset.ToLower()))
            {
                return TextCommandResult.Error("Presets disponibles: low, medium, high, ultra, extreme");
            }

            configHandler.ApplyQualityPreset(preset);
            // Correction : Passer directement le configHandler au lieu d'appeler une méthode inexistante
            shaderManager.UpdateFromConfig(configHandler);
            shaderManager.ReloadShaders();

            return TextCommandResult.Success($"Preset '{preset}' appliqué - Effets de ray marching {(preset == "extreme" ? "EXTRÊMES" : "améliorés")} !");
        }

        /// <summary>
        /// Handles the reload configuration command
        /// </summary>
        private TextCommandResult OnReloadConfig(TextCommandCallingArgs args)
        {
            configHandler.LoadConfig();
            shaderManager.UpdateFromConfig(configHandler);
            return TextCommandResult.Success(Lang.Get("vintagertx:commands.config.reloaded"));
        }

        /// <summary>
        /// Handles the open GUI command
        /// </summary>
        private TextCommandResult OnOpenGUI(TextCommandCallingArgs args)
        {
            configGui.TryOpen();
            return TextCommandResult.Success("Configuration GUI opened");
        }

        /// <summary>
        /// Handles the test effects command
        /// </summary>
        private TextCommandResult OnTestEffects(TextCommandCallingArgs args)
        {
            try
            {
                // Forcer le rechargement des shaders
                shaderManager.ReloadShaders();

                // Vérifier si nos shaders sont chargés
                var pbrStats = textureManager.GetStatistics();
                var status = $"VintageRTX Test Status:\n" +
                           $"- Effects enabled: {shaderManager.AreEffectsEnabled}\n" +
                           $"- Config quality: {configHandler.Config.QualityPreset}\n" +
                           $"- Normal mapping: {configHandler.Config.EnableNormalMapping}\n" +
                           $"- SSR: {configHandler.Config.EnableSSR}\n" +
                           $"- PBR textures found: {pbrStats.TotalTextures} total\n" +
                           $"- Normal maps: {pbrStats.NormalMaps}\n" +
                           $"- Roughness maps: {pbrStats.RoughnessMaps}\n" +
                           $"- Metallic maps: {pbrStats.MetallicMaps}";

                return TextCommandResult.Success(status);
            }
            catch (Exception ex)
            {
                return TextCommandResult.Error($"Test failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows PBR texture statistics
        /// </summary>
        private TextCommandResult OnShowPBRStats(TextCommandCallingArgs args)
        {
            try
            {
                // Forcer un nouveau scan
                textureManager.RescanPBRTextures();

                var stats = textureManager.GetStatistics();
                var message = $"PBR Texture Statistics:\n" +
                            $"Total textures scanned: {stats.TotalTextures}\n" +
                            $"Normal maps found: {stats.NormalMaps}\n" +
                            $"Roughness maps found: {stats.RoughnessMaps}\n" +
                            $"Metallic maps found: {stats.MetallicMaps}\n\n" +
                            $"Place your PBR textures alongside base textures:\n" +
                            $"- [texture]_n.png for normal maps\n" +
                            $"- [texture]_r.png for roughness maps\n" +
                            $"- [texture]_m.png for metallic maps";

                return TextCommandResult.Success(message);
            }
            catch (Exception ex)
            {
                return TextCommandResult.Error($"PBR stats failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Game tick handler for shader hot reload
        /// </summary>
        private void OnGameTick(float dt)
        {
            // Hot reload shaders during development
            shaderManager.CheckForShaderChanges();
        }

        /// <summary>
        /// Updates the frames-per-second (FPS) counter based on the elapsed time.
        /// </summary>
        /// <remarks>This method calculates the FPS by accumulating frame times and counts over a
        /// one-second interval. Once a second has passed, the FPS is updated and the counters are reset.</remarks>
        /// <param name="deltaTime">The time elapsed since the last update, in seconds. Must be a positive value.</param>
        private void UpdateFpsCounter(float deltaTime)
        {
            frameTime += deltaTime * 1000f; // Convert to milliseconds
            frameCount++;

            if (frameTime >= 1000f) // One second has passed
            {
                currentFps = frameCount / (frameTime / 1000f);
                frameCount = 0;
                frameTime = 0f;
            }
        }

        /// <summary>
        /// Game tick handler for adaptive quality adjustments
        /// </summary>
        private void OnAdaptiveQualityTick(float dt)
        {
            // Adjust quality based on FPS
            int targetFps = configHandler.Config.TargetFPS;

            if (currentFps < targetFps * 0.9f)
            {
                // Reduce quality if FPS is too low
                shaderManager.AdjustQualityDown();
            }
            else if (currentFps > targetFps * 1.1f)
            {
                // Increase quality if FPS is sufficient
                shaderManager.AdjustQualityUp();
            }
        }

        /// <summary>
        /// Handles window resize events
        /// </summary>
        private void OnWindowResized()
        {
            try
            {
                renderIntegration?.OnWindowResized();
            }
            catch (Exception ex)
            {
                clientApi.Logger.Error($"[VintageRTX] Error handling window resize: {ex}");
            }
        }

        /// <summary>
        /// Cleans up mod resources on disposal
        /// </summary>
        public override void Dispose()
        {
            harmony?.UnpatchAll("com.vintagertx.shadermod");
            renderIntegration?.Dispose();
            textureManager?.Dispose();
            shaderManager?.Dispose();
            configHandler?.SaveConfig();

            // Note: On ne peut pas désabonner facilement une lambda, donc on laisse l'événement se nettoyer automatiquement
            // clientApi.Event.ReloadShader -= ... (pas nécessaire pour les lambdas)

            base.Dispose();
        }

    }
}