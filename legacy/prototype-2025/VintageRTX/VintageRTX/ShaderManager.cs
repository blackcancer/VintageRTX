using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace VintageRTX.src
{
    /// <summary>
    /// Manages shader modifications and loading for VintageRTX
    /// </summary>
    public class ShaderManager
    {
        private readonly ICoreClientAPI api;
        private readonly ConfigHandler configHandler;
        private readonly Dictionary<string, ShaderModification> shaderModifications;
        private readonly Dictionary<string, string> customShaderCode;
        private readonly List<ShaderProgram> registeredPrograms;
        private bool effectsEnabled = true;
        private DateTime lastShaderCheckTime = DateTime.Now;

        /// <summary>
        /// Gets the logger instance
        /// </summary>
        public ILogger Logger => api.Logger;

        /// <summary>
        /// Gets whether effects are currently enabled
        /// </summary>
        public bool AreEffectsEnabled => effectsEnabled;

        // Target shaders to modify - CORRECTION: Utiliser nos propres noms de shaders
        private readonly HashSet<string> targetShaders = new HashSet<string>
        {
            "vintagertx_standard",
            "vintagertx_chunkopaque",
            "vintagertx_chunkliquid",
            "vintagertx_final",
            "vintagertx_postprocess"
        };

        /// <summary>
        /// Initializes a new instance of the ShaderManager class
        /// </summary>
        /// <param name="api">The client API interface</param>
        /// <param name="configHandler">The configuration handler</param>
        public ShaderManager(ICoreClientAPI api, ConfigHandler configHandler)
        {
            this.api = api;
            this.configHandler = configHandler;
            shaderModifications = new Dictionary<string, ShaderModification>();
            customShaderCode = new Dictionary<string, string>();
            registeredPrograms = new List<ShaderProgram>();

            UpdateFromConfig(configHandler);
            LoadCustomShaders();
            InitializeShaderModifications();

            // NOUVEAU: Créer et enregistrer nos shaders personnalisés
            CreateCustomShaderPrograms();
        }

        /// <summary>
        /// Creates and registers our custom shader programs
        /// </summary>
        private void CreateCustomShaderPrograms()
        {
            try
            {
                // Créer notre shader de post-processing
                if (customShaderCode.ContainsKey("vintagertx_postprocess.vsh") &&
                    customShaderCode.ContainsKey("vintagertx_postprocess.fsh"))
                {
                    CreateShaderProgram("vintagertx_postprocess",
                        customShaderCode["vintagertx_postprocess.vsh"],
                        customShaderCode["vintagertx_postprocess.fsh"]);
                }

                // Créer notre shader standard amélioré
                if (customShaderCode.ContainsKey("vintagertx_standard.vsh") &&
                    customShaderCode.ContainsKey("vintagertx_standard.fsh"))
                {
                    CreateShaderProgram("vintagertx_standard",
                        customShaderCode["vintagertx_standard.vsh"],
                        customShaderCode["vintagertx_standard.fsh"]);
                }

                Logger.Debug("[VintageRTX] Custom shader programs created");
            }
            catch (Exception ex)
            {
                Logger.Error($"[VintageRTX] Error creating custom shader programs: {ex}");
            }
        }

        /// <summary>
        /// Creates a shader program from vertex and fragment shader code
        /// </summary>
        private void CreateShaderProgram(string name, string vertexCode, string fragmentCode)
        {
            try
            {
                // Utiliser l'API de Vintage Story pour créer le shader
                var shaderAssets = new Dictionary<EnumShaderType, string>
                {
                    { EnumShaderType.VertexShader, vertexCode },
                    { EnumShaderType.FragmentShader, fragmentCode }
                };

                // Note: Cette partie nécessite l'accès à l'API interne de VS
                // Pour l'instant, on stocke juste les shaders pour utilisation future
                Logger.Debug($"[VintageRTX] Prepared shader program: {name}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[VintageRTX] Error creating shader program {name}: {ex}");
            }
        }

        /// <summary>
        /// Updates shader settings from configuration
        /// </summary>
        /// <param name="config">The configuration to apply</param>
        public void UpdateFromConfig(ConfigHandler config)
        {
            var cfg = config.Config;
            effectsEnabled = cfg.EnableNormalMapping || cfg.EnableSSR || cfg.EnableRayTracing;
            UpdateShaderParameters(cfg);
        }

        /// <summary>
        /// Updates shader parameters with current configuration
        /// </summary>
        /// <param name="config">Configuration to apply</param>
        private void UpdateShaderParameters(VintageRTXConfig config)
        {
            // Mise à jour des paramètres des shaders avec la nouvelle configuration
            try
            {
                foreach (var program in registeredPrograms)
                {
                    if (program?.Disposed == false)
                    {
                        try
                        {
                            program.Use();

                            // Uniformes de normal mapping
                            program.Uniform("normalMapStrength", config.NormalMapStrength);
                            program.Uniform("enableNormalMapping", config.EnableNormalMapping ? 1.0f : 0.0f);

                            // Uniformes SSR
                            program.Uniform("ssrStrength", config.SSRStrength);
                            program.Uniform("ssrSteps", config.SSRMaxSteps);
                            program.Uniform("ssrMaxDistance", config.SSRMaxDistance);
                            program.Uniform("enableSSR", config.EnableSSR ? 1.0f : 0.0f);

                            // Uniformes Ray Tracing
                            program.Uniform("enableRayTracing", config.EnableRayTracing ? 1.0f : 0.0f);
                            program.Uniform("rayTracingSamples", config.RayTracingSamples);

                            program.Stop();
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"[VintageRTX] Could not update uniforms for shader {program.PassName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[VintageRTX] Error updating shader parameters: {ex}");
            }
        }

        /// <summary>
        /// Updates shader modifications from current configuration
        /// </summary>
        private void UpdateShaderModificationsFromConfig()
        {
            // Réinitialiser les modifications de shaders basées sur la configuration actuelle
            InitializeShaderModifications();
        }

        /// <summary>
        /// Loads custom shader files from the assets directory
        /// </summary>
        private void LoadCustomShaders()
        {
            try
            {
                // CORRECTION: Charger depuis les assets du mod plutôt que ModData
                var assetManager = api.Assets;

                // Charger les shaders personnalisés
                var shaderAssets = new Dictionary<string, string>
                {
                    { "vintagertx_standard.vsh", "vintagertx:shaders/rtx_standard.vsh" },
                    { "vintagertx_standard.fsh", "vintagertx:shaders/rtx_standard.fsh" },
                    { "vintagertx_postprocess.vsh", "vintagertx:shaders/vintagertx_postprocess.vsh" },
                    { "vintagertx_postprocess.fsh", "vintagertx:shaders/vintagertx_postprocess.fsh" }, // Version simple
                    { "vintagertx_chunkliquid.fsh", "vintagertx:shaders/rtx_chunkliquid.fsh" },
                    { "vintagertx_final.fsh", "vintagertx:shaders/rtx_final.fsh" }
                };

                foreach (var shaderPair in shaderAssets)
                {
                    try
                    {
                        var asset = assetManager.TryGet(new AssetLocation(shaderPair.Value));
                        if (asset != null)
                        {
                            customShaderCode[shaderPair.Key] = asset.ToText();
                            Logger.Debug($"[VintageRTX] Loaded custom shader: {shaderPair.Key}");
                        }
                        else
                        {
                            Logger.Warning($"[VintageRTX] Could not find shader asset: {shaderPair.Value}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[VintageRTX] Error loading shader {shaderPair.Key}: {ex.Message}");
                    }
                }

                Logger.Notification($"[VintageRTX] Loaded {customShaderCode.Count} custom shaders");
            }
            catch (Exception ex)
            {
                Logger.Error($"[VintageRTX] Error loading custom shaders: {ex}");
            }
        }

        private void InitializeShaderModifications()
        {
            // CORRECTION: Créer nos propres shaders au lieu de modifier les existants
            shaderModifications["vintagertx_standard"] = new ShaderModification
            {
                Name = "vintagertx_standard",
                AddUniforms = new List<string>
                {
                    "uniform sampler2D normalMap;",
                    "uniform float normalMapStrength;",
                    "uniform bool hasNormalMap;",
                    "uniform mat4 u_toScreenMatrix;"
                },
                VertexModifications = @"
// Ajout du calcul de la matrice TBN pour le normal mapping
varying vec3 v_tangent;
varying vec3 v_bitangent;
varying vec3 v_normal;

void calculateTBN() {
    vec3 T = normalize(normalMatrix * tangent.xyz);
    vec3 N = normalize(normalMatrix * normal);
    vec3 B = cross(N, T) * tangent.w;
    
    v_tangent = T;
    v_bitangent = B;
    v_normal = N;
}",
                FragmentModifications = @"
// Normal mapping
vec3 applyNormalMap(vec3 normal, vec2 texCoord) {
    if (!hasNormalMap || normalMapStrength <= 0.0) return normal;
    
    vec3 normalFromMap = texture(normalMap, texCoord).xyz * 2.0 - 1.0;
    normalFromMap.xy *= normalMapStrength;
    
    vec3 T = normalize(v_tangent);
    vec3 B = normalize(v_bitangent);
    vec3 N = normalize(v_normal);
    mat3 TBN = mat3(T, B, N);
    
    return normalize(TBN * normalFromMap);
}"
            };

            shaderModifications["vintagertx_postprocess"] = new ShaderModification
            {
                Name = "vintagertx_postprocess",
                AddUniforms = new List<string>
                {
                    "uniform sampler2D sceneColor;",
                    "uniform sampler2D sceneDepth;",
                    "uniform sampler2D sceneNormals;",
                    "uniform mat4 u_invProjectionMatrix;",
                    "uniform mat4 u_invViewMatrix;",
                    "uniform float ssrStrength;",
                    "uniform int ssrSteps;",
                    "uniform float ssrMaxDistance;",
                    "uniform vec2 screenSize;"
                },
                FragmentModifications = @"
// Screen Space Reflections
vec3 screenSpaceReflection(vec3 viewPos, vec3 normal, vec2 texCoord) {
    if (ssrStrength <= 0.0) return vec3(0.0);
    
    vec3 viewDir = normalize(viewPos);
    vec3 reflectDir = reflect(viewDir, normal);
    
    // Ray marching dans l'espace écran
    vec3 currentPos = viewPos;
    vec3 rayStep = reflectDir * (ssrMaxDistance / float(ssrSteps));
    
    for (int i = 0; i < ssrSteps; i++) {
        currentPos += rayStep;
        
        // Projeter en espace écran
        vec4 projectedPos = u_projectionMatrix * vec4(currentPos, 1.0);
        vec2 screenPos = projectedPos.xy / projectedPos.w * 0.5 + 0.5;
        
        if (screenPos.x < 0.0 || screenPos.x > 1.0 || 
            screenPos.y < 0.0 || screenPos.y > 1.0) {
            break;
        }
        
        float depth = texture(sceneDepth, screenPos).r;
        float currentDepth = projectedPos.z / projectedPos.w;
        
        if (currentDepth > depth && currentDepth - depth < 0.01) {
            return texture(sceneColor, screenPos).rgb * ssrStrength;
        }
    }
    
    return vec3(0.0);
}"
            };
        }

        /// <summary>
        /// Determines if a shader should be modified
        /// </summary>
        /// <param name="shaderName">Name of the shader</param>
        /// <returns>True if the shader should be modified</returns>
        public bool ShouldModifyShader(string shaderName)
        {
            if (!effectsEnabled) return false;

            foreach (var target in targetShaders)
            {
                if (shaderName.Contains(target))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Registers a shader program for management
        /// </summary>
        /// <param name="program">The shader program to register</param>
        public void RegisterShaderProgram(ShaderProgram program)
        {
            if (!registeredPrograms.Contains(program))
            {
                registeredPrograms.Add(program);
                Logger.Debug($"[VintageRTX] Registered shader program: {program.PassName}");
            }
        }

        /// <summary>
        /// Gets a registered shader program by name
        /// </summary>
        /// <param name="name">Name of the shader program</param>
        /// <returns>The shader program or null if not found</returns>
        public ShaderProgram GetShaderProgram(string name)
        {
            return registeredPrograms.FirstOrDefault(p => p.PassName.Contains(name));
        }

        /// <summary>
        /// Reloads all registered shaders
        /// </summary>
        public void ReloadShaders()
        {
            Logger.Notification("[VintageRTX] Reloading shaders...");

            // Reload custom shader files
            LoadCustomShaders();

            // Update modifications from config
            UpdateShaderModificationsFromConfig();

            // Force recompilation of all registered shaders
            foreach (var program in registeredPrograms)
            {
                try
                {
                    program.Compile();
                    Logger.Debug($"[VintageRTX] Reloaded shader: {program.PassName}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[VintageRTX] Error reloading shader {program.PassName}: {ex}");
                }
            }

            // Update shader parameters with new config
            UpdateShaderParameters(configHandler.Config);
        }

        /// <summary>
        /// Toggles effects on/off
        /// </summary>
        public void ToggleEffects()
        {
            effectsEnabled = !effectsEnabled;
            ReloadShaders();
        }

        /// <summary>
        /// Modifies shader code based on configuration and custom shaders
        /// </summary>
        /// <param name="shaderName">Name of the shader being modified</param>
        /// <param name="shaderType">Type of shader (vertex/fragment)</param>
        /// <param name="originalCode">Original shader code</param>
        /// <param name="prefixCode">Prefix code to add</param>
        /// <returns>Modified shader code</returns>
        public string ModifyShaderCode(string shaderName, EnumShaderType shaderType, string originalCode, string prefixCode)
        {
            try
            {
                // Check if we have a custom shader to use instead
                string customKey = shaderName + (shaderType == EnumShaderType.FragmentShader ? ".fsh" : ".vsh");
                if (customShaderCode.ContainsKey(customKey))
                {
                    Logger.Debug($"[VintageRTX] Using custom shader: {customKey}");
                    return customShaderCode[customKey];
                }

                // Find matching modification
                ShaderModification modification = null;
                foreach (var kvp in shaderModifications)
                {
                    if (shaderName.Contains(kvp.Key))
                    {
                        modification = kvp.Value;
                        break;
                    }
                }

                if (modification == null) return originalCode;

                string modifiedCode = originalCode;

                // Add uniforms
                if (modification.AddUniforms != null && modification.AddUniforms.Count > 0)
                {
                    string uniformsBlock = string.Join("\n", modification.AddUniforms);
                    modifiedCode = InsertAfterVersion(modifiedCode, uniformsBlock);
                }

                // Apply modifications based on shader type
                if (shaderType == EnumShaderType.VertexShader && !string.IsNullOrEmpty(modification.VertexModifications))
                {
                    modifiedCode = InsertInMain(modifiedCode, modification.VertexModifications);
                }
                else if (shaderType == EnumShaderType.FragmentShader && !string.IsNullOrEmpty(modification.FragmentModifications))
                {
                    modifiedCode = InsertInMain(modifiedCode, modification.FragmentModifications);
                }

                // Add configuration defines
                string configDefines = configHandler.GetShaderDefines();
                modifiedCode = InsertAfterVersion(modifiedCode, configDefines);

                // Add effects enabled flag
                modifiedCode = InsertAfterVersion(modifiedCode, $"const bool effectsEnabled = {effectsEnabled.ToString().ToLower()};");

                return prefixCode + "\n" + modifiedCode;
            }
            catch (Exception ex)
            {
                Logger.Error(Lang.Get("vintagertx:error.shader", shaderName, ex.Message));
                return originalCode;
            }
        }

        private string InsertAfterVersion(string code, string insertion)
        {
            var versionMatch = Regex.Match(code, @"#version\s+\d+.*?\n");
            if (versionMatch.Success)
            {
                int insertPos = versionMatch.Index + versionMatch.Length;
                return code.Insert(insertPos, insertion + "\n");
            }
            return insertion + "\n" + code;
        }

        private string InsertInMain(string code, string insertion)
        {
            var mainMatch = Regex.Match(code, @"void\s+main\s*\(\s*\)\s*{");
            if (mainMatch.Success)
            {
                int insertPos = mainMatch.Index + mainMatch.Length;
                return code.Insert(insertPos, "\n" + insertion + "\n");
            }
            return code;
        }

        /// <summary>
        /// Gets the current status of shader effects
        /// </summary>
        /// <returns>A formatted status string</returns>
        public string GetStatus()
        {
            var config = configHandler.Config;
            return Lang.Get("vintagertx:commands.status.format",
                effectsEnabled ? Lang.Get("vintagertx:status.enabled") : Lang.Get("vintagertx:status.disabled"),
                registeredPrograms.Count,
                config.EnableNormalMapping ? Lang.Get("vintagertx:status.enabled") : Lang.Get("vintagertx:status.disabled"),
                config.NormalMapStrength,
                config.EnableSSR ? Lang.Get("vintagertx:status.enabled") : Lang.Get("vintagertx:status.disabled"),
                config.SSRQuality,
                config.EnableRayTracing ? Lang.Get("vintagertx:status.enabled") : Lang.Get("vintagertx:status.disabled"),
                config.QualityPreset
            );
        }

        /// <summary>
        /// Checks for shader file changes and reloads if necessary
        /// </summary>
        public void CheckForShaderChanges()
        {
            // Check if shader files have been modified (for development)
            if ((DateTime.Now - lastShaderCheckTime).TotalSeconds < 1.0)
                return;

            lastShaderCheckTime = DateTime.Now;

            // TODO: Implement file change detection for hot reload
        }

        /// <summary>
        /// Adjusts quality settings down one level
        /// </summary>
        public void AdjustQualityDown()
        {
            var config = configHandler.Config;

            switch (config.QualityPreset.ToLower())
            {
                case "ultra":
                    configHandler.ApplyQualityPreset("high");
                    break;
                case "high":
                    configHandler.ApplyQualityPreset("medium");
                    break;
                case "medium":
                    configHandler.ApplyQualityPreset("low");
                    break;
                case "low":
                    // Already at minimum
                    break;
            }

            Logger.Debug(Lang.Get("vintagertx:quality.reduced", configHandler.Config.QualityPreset));
        }

        /// <summary>
        /// Adjusts quality settings up one level
        /// </summary>
        public void AdjustQualityUp()
        {
            var config = configHandler.Config;

            switch (config.QualityPreset.ToLower())
            {
                case "low":
                    configHandler.ApplyQualityPreset("medium");
                    break;
                case "medium":
                    configHandler.ApplyQualityPreset("high");
                    break;
                case "high":
                    configHandler.ApplyQualityPreset("ultra");
                    break;
                case "ultra":
                    // Already at maximum
                    break;
            }

            Logger.Debug(Lang.Get("vintagertx:quality.increased", configHandler.Config.QualityPreset));
        }

        public void Dispose()
        {
            registeredPrograms.Clear();
            shaderModifications.Clear();
        }
    }

    public class ShaderModification
    {
        public string Name { get; set; }
        public List<string> AddUniforms { get; set; }
        public string VertexModifications { get; set; }
        public string FragmentModifications { get; set; }
    }
}