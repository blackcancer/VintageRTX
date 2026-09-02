using System;
using System.Collections.Generic;
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

        // Framebuffer objects for deferred rendering
        private int gBufferFBO;
        private int gBufferColorTexture;
        private int gBufferNormalTexture;
        private int gBufferMaterialTexture;
        private int gBufferDepthTexture;

        // Screen quad for post-processing
        private MeshRef screenQuadRef;

        /// <summary>
        /// Render stage for this renderer
        /// </summary>
        public EnumRenderStage RenderStage => EnumRenderStage.AfterFinalComposition;

        /// <summary>
        /// Render order within the stage
        /// </summary>
        public double RenderOrder => 0.99;

        /// <summary>
        /// Initializes a new instance of the RenderIntegration class
        /// </summary>
        public RenderIntegration(ICoreClientAPI api, ShaderManager shaderManager, TextureManager textureManager, ConfigHandler configHandler)
        {
            this.api = api;
            this.shaderManager = shaderManager;
            this.textureManager = textureManager;
            this.configHandler = configHandler;

            InitializeFramebuffers();
            CreateScreenQuad();
        }

        /// <summary>
        /// Initializes framebuffer objects for deferred rendering
        /// </summary>
        private void InitializeFramebuffers()
        {
            try
            {
                var platform = api.Render;
                int width = platform.FrameWidth;
                int height = platform.FrameHeight;

                // Create framebuffer
                gBufferFBO = platform.CreateFramebuffer(width, height);

                // Create textures for G-buffer
                gBufferColorTexture = platform.CreateTexture(width, height);
                gBufferNormalTexture = platform.CreateTexture(width, height);
                gBufferMaterialTexture = platform.CreateTexture(width, height);
                gBufferDepthTexture = platform.CreateDepthTexture(width, height);

                // Attach textures to framebuffer
                platform.AttachTextureToFramebuffer(gBufferFBO, gBufferColorTexture, 0);
                platform.AttachTextureToFramebuffer(gBufferFBO, gBufferNormalTexture, 1);
                platform.AttachTextureToFramebuffer(gBufferFBO, gBufferMaterialTexture, 2);
                platform.AttachDepthTextureToFramebuffer(gBufferFBO, gBufferDepthTexture);

                api.Logger.Debug("[VintageRTX] Framebuffers initialized");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error initializing framebuffers: {ex}");
            }
        }

        /// <summary>
        /// Creates a screen-aligned quad for post-processing
        /// </summary>
        private void CreateScreenQuad()
        {
            var mesh = new MeshData();
            mesh.SetMode(EnumDrawMode.Triangles);

            // Vertices for full-screen quad
            mesh.AddVertex(-1, -1, 0, 0, 0);
            mesh.AddVertex(1, -1, 0, 1, 0);
            mesh.AddVertex(1, 1, 0, 1, 1);
            mesh.AddVertex(-1, 1, 0, 0, 1);

            // Indices
            mesh.AddIndex(0, 1, 2);
            mesh.AddIndex(0, 2, 3);

            screenQuadRef = api.Render.UploadMesh(mesh);
        }

        /// <summary>
        /// Called when frame buffer size changes
        /// </summary>
        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (!shaderManager.AreEffectsEnabled || !configHandler.Config.EnableSSR)
                return;

            try
            {
                var platform = api.Render;

                // Bind our G-buffer
                platform.BindFramebuffer(gBufferFBO);

                // Set up multiple render targets
                platform.SetDrawBuffers(new[] { 0, 1, 2 });

                // Clear buffers
                platform.Clear(EnumClearFlag.ColorBuffer | EnumClearFlag.DepthBuffer);

                // The main scene rendering happens here (handled by game)
                // We just need to capture the results

                // Unbind our framebuffer
                platform.BindDefaultFramebuffer();

                // Apply post-processing effects
                ApplyPostProcessing(deltaTime);
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error in render frame: {ex}");
            }
        }

        /// <summary>
        /// Applies post-processing effects like SSR
        /// </summary>
        private void ApplyPostProcessing(float deltaTime)
        {
            var shader = shaderManager.GetShaderProgram("final");
            if (shader == null) return;

            var platform = api.Render;

            // Use our post-processing shader
            shader.Use();

            // Bind G-buffer textures
            shader.BindTexture2D("sceneColor", gBufferColorTexture, 0);
            shader.BindTexture2D("sceneDepth", gBufferDepthTexture, 1);
            shader.BindTexture2D("sceneNormals", gBufferNormalTexture, 2);
            shader.BindTexture2D("sceneMaterial", gBufferMaterialTexture, 3);

            // Set uniforms
            shader.UniformMatrix("invProjectionMatrix", api.Render.InvProjectionMatrix);
            shader.UniformMatrix("invViewMatrix", api.Render.InvCameraMatrix);
            shader.Uniform("cameraPos", api.Render.CameraPos);

            // SSR parameters from config
            var config = configHandler.Config;
            shader.Uniform("ssrEnabled", config.EnableSSR ? 1.0f : 0.0f);
            shader.Uniform("ssrStrength", config.SSRStrength);
            shader.Uniform("ssrSteps", config.SSRMaxSteps);
            shader.Uniform("ssrMaxDistance", config.SSRMaxDistance);

            // Render screen quad
            platform.RenderMesh(screenQuadRef);

            shader.Stop();
        }

        /// <summary>
        /// Updates uniforms for material rendering
        /// </summary>
        /// <param name="textureName">Name of the base texture being rendered</param>
        public void SetupMaterialUniforms(string textureName)
        {
            var currentShader = api.Render.CurrentActiveShader;
            if (currentShader == null) return;

            var pbrInfo = textureManager.GetPBRInfo(textureName);

            // Bind normal map if available
            if (pbrInfo.HasNormalMap)
            {
                currentShader.BindTexture2D("normalMap", pbrInfo.NormalMapId, 1);
                currentShader.Uniform("hasNormalMap", 1.0f);
                currentShader.Uniform("normalMapStrength", configHandler.Config.NormalMapStrength);
            }
            else
            {
                currentShader.Uniform("hasNormalMap", 0.0f);
            }

            // Bind roughness map if available
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

            // Bind metallic map if available
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
        }

        /// <summary>
        /// Called when window is resized
        /// </summary>
        public void OnWindowResized()
        {
            // Recreate framebuffers with new size
            DisposeFramebuffers();
            InitializeFramebuffers();
        }

        /// <summary>
        /// Disposes framebuffer resources
        /// </summary>
        private void DisposeFramebuffers()
        {
            var platform = api.Render;

            if (gBufferFBO != 0)
            {
                platform.DeleteFramebuffer(gBufferFBO);
                platform.DeleteTexture(gBufferColorTexture);
                platform.DeleteTexture(gBufferNormalTexture);
                platform.DeleteTexture(gBufferMaterialTexture);
                platform.DeleteTexture(gBufferDepthTexture);
            }
        }

        /// <summary>
        /// Cleans up renderer resources
        /// </summary>
        public void Dispose()
        {
            DisposeFramebuffers();

            if (screenQuadRef != null)
            {
                api.Render.DeleteMesh(screenQuadRef);
            }
        }
    }
}