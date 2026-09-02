using System;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageRTX
{
    /// <summary>
    /// Configuration data for VintageRTX mod
    /// </summary>
    public class VintageRTXConfig
    {
        /// <summary>
        /// Enable normal mapping effects
        /// </summary>
        public bool EnableNormalMapping { get; set; } = true;

        /// <summary>
        /// Strength of normal map effect (0.0 - 2.0)
        /// </summary>
        public float NormalMapStrength { get; set; } = 1.0f;

        /// <summary>
        /// Enable Screen Space Reflections
        /// </summary>
        public bool EnableSSR { get; set; } = true;

        /// <summary>
        /// SSR quality level: low, medium, high, ultra
        /// </summary>
        public string SSRQuality { get; set; } = "medium";

        /// <summary>
        /// Maximum ray marching steps for SSR
        /// </summary>
        public int SSRMaxSteps { get; set; } = 32;

        /// <summary>
        /// Maximum distance for SSR ray marching
        /// </summary>
        public float SSRMaxDistance { get; set; } = 10.0f;

        /// <summary>
        /// Strength of SSR effect (0.0 - 1.0)
        /// </summary>
        public float SSRStrength { get; set; } = 0.5f;

        /// <summary>
        /// Distance at which SSR starts to fade
        /// </summary>
        public float SSRFadeDistance { get; set; } = 5.0f;

        /// <summary>
        /// Enable screen-space ray tracing
        /// </summary>
        public bool EnableRayTracing { get; set; } = false;

        /// <summary>
        /// Number of ray tracing samples
        /// </summary>
        public int RayTracingSamples { get; set; } = 16;

        /// <summary>
        /// Overall quality preset
        /// </summary>
        public string QualityPreset { get; set; } = "medium";

        /// <summary>
        /// Enable automatic quality adjustment based on FPS
        /// </summary>
        public bool AdaptiveQuality { get; set; } = true;

        /// <summary>
        /// Target FPS for adaptive quality
        /// </summary>
        public int TargetFPS { get; set; } = 60;

        /// <summary>
        /// Show debug information overlay
        /// </summary>
        public bool ShowDebugInfo { get; set; } = false;

        /// <summary>
        /// Enable shader hot reload for development
        /// </summary>
        public bool EnableShaderHotReload { get; set; } = true;
    }

    /// <summary>
    /// Handles loading and saving of VintageRTX configuration
    /// </summary>
    public class ConfigHandler
    {
        private readonly ICoreClientAPI api;
        private readonly string configPath;
        private VintageRTXConfig config;

        /// <summary>
        /// Gets the current configuration
        /// </summary>
        public VintageRTXConfig Config => config;

        /// <summary>
        /// Initializes a new instance of the ConfigHandler class
        /// </summary>
        /// <param name="api">The client API interface</param>
        public ConfigHandler(ICoreClientAPI api)
        {
            this.api = api;

            // Correction : Utiliser GamePaths.ModConfig au lieu de DataBasePath/ModConfig
            string modConfigPath = Path.Combine(GamePaths.ModConfig, "vintagertx.json");
            this.configPath = modConfigPath;

            LoadConfig();
        }

        /// <summary>
        /// Loads configuration from file or creates default
        /// </summary>
        public void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    config = JsonConvert.DeserializeObject<VintageRTXConfig>(json);
                    api.Logger.Notification(Lang.Get("vintagertx:config.loaded"));
                }
                else
                {
                    config = new VintageRTXConfig();
                    SaveConfig();
                    api.Logger.Notification(Lang.Get("vintagertx:config.created"));
                }

                ValidateConfig();
            }
            catch (Exception ex)
            {
                api.Logger.Error(Lang.Get("vintagertx:config.error.load", ex.Message));
                config = new VintageRTXConfig();
            }
        }

        /// <summary>
        /// Saves current configuration to file
        /// </summary>
        public void SaveConfig()
        {
            try
            {
                // Correction : Vérifier et créer le répertoire ModConfig
                string directory = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(configPath, json);

                api.Logger.Notification(Lang.Get("vintagertx:config.saved"));
            }
            catch (UnauthorizedAccessException ex)
            {
                api.Logger.Warning($"[VintageRTX] Cannot save config due to permissions: {ex.Message}");
                api.Logger.Warning("[VintageRTX] Configuration will be stored in memory only");
            }
            catch (Exception ex)
            {
                api.Logger.Error(Lang.Get("vintagertx:config.error.save", ex.Message));
            }
        }

        private void ValidateConfig()
        {
            // Valider les valeurs
            config.NormalMapStrength = Math.Clamp(config.NormalMapStrength, 0.0f, 2.0f);
            config.SSRMaxSteps = Math.Clamp(config.SSRMaxSteps, 8, 128);
            config.SSRMaxDistance = Math.Clamp(config.SSRMaxDistance, 1.0f, 50.0f);
            config.SSRStrength = Math.Clamp(config.SSRStrength, 0.0f, 1.0f);
            config.RayTracingSamples = Math.Clamp(config.RayTracingSamples, 4, 64);
            config.TargetFPS = Math.Clamp(config.TargetFPS, 30, 240);
        }

        public void ApplyQualityPreset(string preset)
        {
            config.QualityPreset = preset;

            switch (preset.ToLower())
            {
                case "low":
                    config.EnableNormalMapping = true;
                    config.NormalMapStrength = 0.5f;
                    config.EnableSSR = false;
                    config.EnableRayTracing = false;
                    break;

                case "medium":
                    config.EnableNormalMapping = true;
                    config.NormalMapStrength = 1.0f;
                    config.EnableSSR = true;
                    config.SSRMaxSteps = 32;
                    config.SSRQuality = "medium";
                    config.EnableRayTracing = false;
                    break;

                case "high":
                    config.EnableNormalMapping = true;
                    config.NormalMapStrength = 1.0f;
                    config.EnableSSR = true;
                    config.SSRMaxSteps = 64;
                    config.SSRQuality = "high";
                    config.EnableRayTracing = true;
                    config.RayTracingSamples = 16;
                    break;

                case "ultra":
                    config.EnableNormalMapping = true;
                    config.NormalMapStrength = 1.0f;
                    config.EnableSSR = true;
                    config.SSRMaxSteps = 128;
                    config.SSRQuality = "ultra";
                    config.EnableRayTracing = true;
                    config.RayTracingSamples = 32;
                    break;
            }

            SaveConfig();
        }

        /// <summary>
        /// Remet la configuration aux valeurs par défaut
        /// </summary>
        public void ResetToDefault()
        {
            config = new VintageRTXConfig();
            ValidateConfig();
        }

        public string GetShaderDefines()
        {
            // Générer les defines pour les shaders basés sur la configuration
            var defines = new System.Text.StringBuilder();

            if (config.EnableNormalMapping)
                defines.AppendLine("#define ENABLE_NORMAL_MAPPING");

            if (config.EnableSSR)
            {
                defines.AppendLine("#define ENABLE_SSR");
                defines.AppendLine($"#define SSR_MAX_STEPS {config.SSRMaxSteps}");
                defines.AppendLine($"#define SSR_QUALITY_{config.SSRQuality.ToUpper()}");
            }

            if (config.EnableRayTracing)
            {
                defines.AppendLine("#define ENABLE_RAYTRACING");
                defines.AppendLine($"#define RT_SAMPLES {config.RayTracingSamples}");
            }

            if (config.ShowDebugInfo)
                defines.AppendLine("#define DEBUG_MODE");

            return defines.ToString();
        }
    }
}