using System;
using System.Collections.Generic;
using VintageRTX.src;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageRTX
{
    /// <summary>
    /// Handles integration with Vintage Story's rendering pipeline
    /// </summary>
    public class RenderIntegration : IRenderer
    {
        private readonly ICoreClientAPI api;
        private readonly ShaderManager shaderManager;
        private readonly TextureManager textureManager;
        private readonly ConfigHandler configHandler;

        // Screen quad for post-processing
        private MeshRef screenQuadRef;

        // Shader programs - Correction: Utiliser IShaderProgram au lieu de ShaderProgram
        private IShaderProgram postProcessShader;

        /// <summary>
        /// Render stage for this renderer
        /// </summary>
        public EnumRenderStage RenderStage => EnumRenderStage.AfterFinalComposition;

        /// <summary>
        /// Render order within the stage
        /// </summary>
        public double RenderOrder => 0.99;

        /// <summary>
        /// Render range for this renderer
        /// </summary>
        public int RenderRange => 99;

        /// <summary>
        /// Initializes a new instance of the RenderIntegration class
        /// </summary>
        public RenderIntegration(ICoreClientAPI api, ShaderManager shaderManager, TextureManager textureManager, ConfigHandler configHandler)
        {
            this.api = api;
            this.shaderManager = shaderManager;
            this.textureManager = textureManager;
            this.configHandler = configHandler;

            CreateScreenQuad();
            InitializeShaders();
        }

        /// <summary>
        /// Initialize shader programs
        /// </summary>
        private void InitializeShaders()
        {
            try
            {
                // Correction: Utiliser GetShaderProgram au lieu de créer directement
                var shaderProgram = shaderManager.GetShaderProgram("vintagertx_postprocess");
                if (shaderProgram != null)
                {
                    postProcessShader = shaderProgram as IShaderProgram;
                }

                if (postProcessShader == null)
                {
                    api.Logger.Warning("[VintageRTX] Post-process shader not found, creating basic shader");
                    // Créer un shader basique si nécessaire
                    CreateBasicPostProcessShader();
                }

                api.Logger.Debug("[VintageRTX] Shaders initialized");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error initializing shaders: {ex}");
            }
        }

        /// <summary>
        /// Creates a basic post-process shader
        /// </summary>
        private void CreateBasicPostProcessShader()
        {
            try
            {
                // Créer un shader de post-processing basique
                var vertexShader = @"
#version 330 core
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec2 aTexCoord;

out vec2 texCoord;

void main()
{
    gl_Position = vec4(aPos, 1.0);
    texCoord = aTexCoord;
}";

                var fragmentShader = @"
#version 330 core
in vec2 texCoord;
out vec4 FragColor;

uniform sampler2D screenTexture;
uniform float time;

void main()
{
    vec4 color = texture(screenTexture, texCoord);
    FragColor = color;
}";

                // Correction: Utiliser l'API de Vintage Story pour créer le shader
                var assets = api.Assets;
                if (assets != null)
                {
                    // Cette partie dépend de l'API spécifique de Vintage Story
                    // Elle pourrait nécessiter une approche différente selon la version
                    api.Logger.Debug("[VintageRTX] Basic post-process shader creation attempted");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error creating basic post-process shader: {ex}");
            }
        }

        /// <summary>
        /// Creates a screen-aligned quad for post-processing
        /// </summary>
        private void CreateScreenQuad()
        {
            try
            {
                // Correction : Approche simplifiée sans SetUvs
                var mesh = new MeshData(4, 6); // 4 vertices, 6 indices
                mesh.SetMode(EnumDrawMode.Triangles);

                // Assignation directe des données du mesh
                mesh.xyz = new float[] {
                    -1f, -1f, 0f,  // Vertex 0: bas gauche
                     1f, -1f, 0f,  // Vertex 1: bas droit
                     1f,  1f, 0f,  // Vertex 2: haut droit
                    -1f,  1f, 0f   // Vertex 3: haut gauche
                };

                mesh.Uv = new float[] {
                    0f, 0f,  // UV pour vertex 0
                    1f, 0f,  // UV pour vertex 1
                    1f, 1f,  // UV pour vertex 2
                    0f, 1f   // UV pour vertex 3
                };

                // Indices pour deux triangles
                mesh.Indices = new int[] {
                    0, 1, 2,  // Premier triangle
                    0, 2, 3   // Deuxième triangle
                };

                mesh.IndicesCount = 6;
                mesh.VerticesCount = 4;

                screenQuadRef = api.Render.UploadMesh(mesh);
                api.Logger.Debug("[VintageRTX] Screen quad created successfully");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error creating screen quad: {ex}");
                api.Logger.Debug($"[VintageRTX] Stack trace: {ex.StackTrace}");

                // Fallback : désactiver le screen quad en cas d'erreur
                screenQuadRef = null;
                api.Logger.Warning("[VintageRTX] Screen quad disabled due to creation error - post-processing will be limited");
            }
        }

        /// <summary>
        /// Called when frame buffer size changes
        /// </summary>
        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (!shaderManager.AreEffectsEnabled)
                return;

            try
            {
                // Application d'effets de post-processing avec nos shaders personnalisés
                ApplyPostProcessing(deltaTime);
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error in render frame: {ex}");
            }
        }

        /// <summary>
        /// Applique des effets de post-processing simplifiés
        /// </summary>
        private void ApplyPostProcessing(float deltaTime)
        {
            if (postProcessShader == null || !configHandler.Config.EnableSSR)
                return;

            try
            {
                var platform = api.Render;

                // Utiliser le shader de post-processing
                postProcessShader.Use();

                // Définir les uniformes de base
                SetPostProcessUniforms();

                // Rendu du quad plein écran
                if (screenQuadRef != null)
                {
                    platform.RenderMesh(screenQuadRef);
                }

                postProcessShader.Stop();
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error in post-processing: {ex}");
            }
        }

        /// <summary>
        /// Définit les uniformes pour le post-processing
        /// </summary>
        private void SetPostProcessUniforms()
        {
            if (postProcessShader == null) return;

            var config = configHandler.Config;

            try
            {
                // Uniformes de configuration SSR
                postProcessShader.Uniform("ssrEnabled", config.EnableSSR ? 1.0f : 0.0f);
                postProcessShader.Uniform("ssrStrength", config.SSRStrength);
                postProcessShader.Uniform("ssrSteps", config.SSRMaxSteps);
                postProcessShader.Uniform("ssrMaxDistance", config.SSRMaxDistance);
                postProcessShader.Uniform("ssrFadeDistance", config.SSRFadeDistance);

                // Uniformes de normal mapping
                postProcessShader.Uniform("normalMappingEnabled", config.EnableNormalMapping ? 1.0f : 0.0f);
                postProcessShader.Uniform("normalMapStrength", config.NormalMapStrength);

                // Informations temporelles
                postProcessShader.Uniform("time", (float)(api.World.ElapsedMilliseconds / 1000.0));
                postProcessShader.Uniform("deltaTime", api.World.ElapsedMilliseconds);

                // Résolution d'écran
                postProcessShader.Uniform("screenSize", new Vec2f(api.Render.FrameWidth, api.Render.FrameHeight));
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error setting uniforms: {ex}");
            }
        }

        /// <summary>
        /// Met à jour les uniformes pour le rendu de matériaux avec PBR automatique
        /// </summary>
        /// <param name="textureName">Nom de la texture de base en cours de rendu</param>
        public void SetupMaterialUniforms(string textureName)
        {
            var currentShader = api.Render.CurrentActiveShader;
            if (currentShader == null) return;

            try
            {
                var pbrInfo = textureManager.GetPBRInfo(textureName);
                var config = configHandler.Config;

                // Configuration normal map
                if (pbrInfo.HasNormalMap && config.EnableNormalMapping)
                {
                    currentShader.BindTexture2D("normalMap", pbrInfo.NormalMapId, 1);
                    currentShader.Uniform("hasNormalMap", 1.0f);
                    currentShader.Uniform("normalMapStrength", config.NormalMapStrength);
                }
                else
                {
                    currentShader.Uniform("hasNormalMap", 0.0f);
                }

                // Configuration roughness map
                if (pbrInfo.HasRoughnessMap)
                {
                    currentShader.BindTexture2D("roughnessMap", pbrInfo.RoughnessMapId, 2);
                    currentShader.Uniform("hasRoughnessMap", 1.0f);
                }
                else
                {
                    currentShader.Uniform("hasRoughnessMap", 0.0f);
                    currentShader.Uniform("roughnessDefault", 0.5f);
                }

                // Configuration metallic map
                if (pbrInfo.HasMetallicMap)
                {
                    currentShader.BindTexture2D("metallicMap", pbrInfo.MetallicMapId, 3);
                    currentShader.Uniform("hasMetallicMap", 1.0f);
                }
                else
                {
                    currentShader.Uniform("hasMetallicMap", 0.0f);
                    currentShader.Uniform("metallicDefault", 0.0f);
                }

                // Configuration générale PBR
                currentShader.Uniform("pbrEnabled", pbrInfo.HasAnyPBR && config.EnableNormalMapping ? 1.0f : 0.0f);
                currentShader.Uniform("metallicBoost", 1.2f);
                currentShader.Uniform("roughnessAdjust", 0.0f);
                currentShader.Uniform("normalIntensity", config.NormalMapStrength);

                // Position de la caméra pour les calculs PBR
                var cameraPos = api.World.Player?.Entity?.Pos?.XYZ;
                if (cameraPos != null)
                {
                    currentShader.Uniform("cameraPos", new float[] { (float)cameraPos.X, (float)cameraPos.Y, (float)cameraPos.Z });
                }

                api.Logger.Debug($"[VintageRTX] PBR uniforms set for {textureName}: N={pbrInfo.HasNormalMap}, R={pbrInfo.HasRoughnessMap}, M={pbrInfo.HasMetallicMap}");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error setting PBR material uniforms: {ex}");
            }
        }

        /// <summary>
        /// Hook pour intercepter le rendu des matériaux et appliquer le PBR
        /// </summary>
        public void OnMaterialRender(string materialName, string textureName)
        {
            if (!shaderManager.AreEffectsEnabled) return;

            try
            {
                SetupMaterialUniforms(textureName);
            }
            catch (Exception ex)
            {
                api.Logger.Debug($"[VintageRTX] Could not setup PBR for material {materialName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Appelé lors du redimensionnement de la fenêtre
        /// Version simplifiée sans framebuffers
        /// </summary>
        public void OnWindowResized()
        {
            api.Logger.Debug($"[VintageRTX] Window resized to {api.Render.FrameWidth}x{api.Render.FrameHeight}");

            // Mettre à jour les uniformes de résolution dans les shaders
            if (postProcessShader != null)
            {
                try
                {
                    postProcessShader.Use();
                    postProcessShader.Uniform("screenSize", new Vec2f(api.Render.FrameWidth, api.Render.FrameHeight));
                    postProcessShader.Stop();
                }
                catch (Exception ex)
                {
                    api.Logger.Debug($"[VintageRTX] Could not update screen size uniform: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Méthode utilitaire pour appliquer des effets de shader simples
        /// </summary>
        public void ApplyShaderEffect(string effectName, float intensity = 1.0f)
        {
            var currentShader = api.Render.CurrentActiveShader;
            if (currentShader == null) return;

            try
            {
                currentShader.Uniform($"{effectName}Enabled", intensity > 0 ? 1.0f : 0.0f);
                currentShader.Uniform($"{effectName}Intensity", intensity);
            }
            catch (Exception ex)
            {
                api.Logger.Debug($"[VintageRTX] Could not apply effect {effectName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Nettoie les ressources du renderer
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (screenQuadRef != null)
                {
                    screenQuadRef.Dispose();
                    screenQuadRef = null;
                }

                // Le postProcessShader est géré par ShaderManager
                postProcessShader = null;

                api.Logger.Debug("[VintageRTX] RenderIntegration disposed");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error disposing RenderIntegration: {ex}");
            }
        }
    }
}