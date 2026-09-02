using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Owns a distinct RGBA16F render target and repacks its HDR RGB into Vintage Story's public Luma input.
/// Every method must run on the render thread. The caller owns GL-state capture/restoration around
/// calls; the bridge deliberately leaves its framebuffer, program, texture-unit, viewport, and
/// capability changes visible so no hidden state query or synchronization is introduced.
/// </summary>
internal sealed class LumaRenderBridge : IDisposable
{
    /// <summary>Stable shader-registry identity for the asset-backed repack program.</summary>
    private const string ShaderName = "vintagertx-luma-repack";
    private readonly ICoreClientAPI api;
    private IShaderProgram? shader;
    private int colorTextureId;
    private int framebufferId;
    private int vertexArrayId;
    private int width;
    private int height;
    private bool disposed;

    /// <summary>Creates an unallocated bridge bound to the active client renderer.</summary>
    /// <param name="api">Client API used to load and register the asset-backed repack shader.</param>
    internal LumaRenderBridge(ICoreClientAPI api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>Gets the owned temporary RGBA texture, or zero before first allocation.</summary>
    internal int ColorTextureId => colorTextureId;

    /// <summary>Gets the owned temporary framebuffer, or zero before first allocation.</summary>
    internal int FramebufferId => framebufferId;

    /// <summary>Gets the allocated texture width, or zero when unavailable.</summary>
    internal int Width => width;

    /// <summary>Gets the allocated texture height, or zero when unavailable.</summary>
    internal int Height => height;

    /// <summary>Gets whether all owned GL target objects exist at positive dimensions.</summary>
    internal bool IsAllocated =>
        !disposed
        && colorTextureId > 0
        && framebufferId > 0
        && vertexArrayId > 0
        && width > 0
        && height > 0;

    /// <summary>
    /// Allocates or resizes the owned RGBA16F colour target and compiles the repack shader lazily.
    /// Bindings changed during allocation are intentionally not restored.
    /// </summary>
    /// <param name="frameWidth">Required positive target width.</param>
    /// <param name="frameHeight">Required positive target height.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The bridge has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Shader compilation or framebuffer completion fails.</exception>
    internal void EnsureSize(int frameWidth, int frameHeight)
    {
        ThrowIfDisposed();
        if (frameWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        }
        if (frameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameHeight));
        }

        EnsureShader();
        colorTextureId = EnsureTextureName(colorTextureId);
        framebufferId = EnsureFramebufferName(framebufferId);
        vertexArrayId = EnsureVertexArrayName(vertexArrayId);
        if (width == frameWidth && height == frameHeight)
        {
            return;
        }

        GL.BindTexture(TextureTarget.Texture2D, colorTextureId);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba16f,
            frameWidth,
            frameHeight,
            0,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            IntPtr.Zero);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebufferId);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            colorTextureId,
            0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)
            != FramebufferErrorCode.FramebufferComplete)
        {
            width = 0;
            height = 0;
            throw new InvalidOperationException("The owned Luma bridge framebuffer is incomplete.");
        }

        width = frameWidth;
        height = frameHeight;
    }

    /// <summary>
    /// Binds the owned temporary target for the caller's pre-final transport draw. The method sets
    /// its draw buffer and viewport but leaves depth, blending, culling, and shader state untouched.
    /// </summary>
    /// <param name="frameWidth">Required positive target width.</param>
    /// <param name="frameHeight">Required positive target height.</param>
    internal void BindTarget(int frameWidth, int frameHeight)
    {
        EnsureSize(frameWidth, frameHeight);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebufferId);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(0, 0, frameWidth, frameHeight);
    }

    /// <summary>
    /// Draws the owned RGB result into the exact public Luma attachment while reconstructing FXAA
    /// alpha. The source and destination are validated as distinct before any GL mutation.
    /// </summary>
    /// <param name="frameBuffers">Live engine framebuffer registry.</param>
    /// <param name="frameWidth">Expected current frame width.</param>
    /// <param name="frameHeight">Expected current frame height.</param>
    /// <returns>The validated source/destination binding used for the draw.</returns>
    /// <exception cref="ObjectDisposedException">The bridge has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The bridge is stale or the Luma layout is unsafe.</exception>
    internal LumaBridgeLayout RepackToLuma(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int frameWidth,
        int frameHeight)
    {
        ThrowIfDisposed();
        if (!IsAllocated || width != frameWidth || height != frameHeight)
        {
            throw new InvalidOperationException(
                "The Luma bridge target must contain an exact-size completed draw before repacking.");
        }

        if (!LumaBridgeContract.TryResolve(
                frameBuffers,
                frameWidth,
                frameHeight,
                framebufferId,
                colorTextureId,
                out LumaBridgeLayout layout,
                out string reason))
        {
            throw new InvalidOperationException($"The Luma bridge destination is unsafe: {reason}.");
        }

        EnsureShader();
        shader!.Use();
        try
        {
            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.FramebufferSrgb);
            GL.BindFramebuffer(
                FramebufferTarget.DrawFramebuffer,
                layout.DestinationFramebufferId);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.Viewport(0, 0, layout.Width, layout.Height);
            shader.BindTexture2D("sourceColor", layout.SourceTextureId, 0);
            GL.BindVertexArray(vertexArrayId);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        finally
        {
            shader.Stop();
        }

        return layout;
    }

    /// <summary>Loads, compiles, and registers the asset-backed repack shader once.</summary>
    /// <exception cref="NotSupportedException">The active driver lacks GLSL 3.30.</exception>
    /// <exception cref="InvalidOperationException">The registered shader cannot link.</exception>
    private void EnsureShader()
    {
        if (shader is not null && !shader.Disposed)
        {
            return;
        }

        if (!api.Shader.IsGLSLVersionSupported("330"))
        {
            throw new NotSupportedException("The Luma bridge requires GLSL 3.30 or newer.");
        }

        DisplayShaderProgramSource source = LumaBridgeShaderSource.Load(api.Assets);
        IShaderProgram program = api.Shader.NewShaderProgram();
        program.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
        program.VertexShader.Code = source.Vertex;
        program.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
        program.FragmentShader.Code = source.Fragment;
        int passId = api.Shader.RegisterMemoryShaderProgram(ShaderName, program);
        bool compiled = program.Compile();
        if (passId < 0 || !compiled || program.LoadError || program.Disposed)
        {
            throw new InvalidOperationException(
                $"The asset-backed Luma bridge shader did not compile "
                + $"({LumaBridgeShaderSource.VertexAssetCode}, "
                + $"{LumaBridgeShaderSource.FragmentAssetCode}).");
        }

        shader = program;
    }

    /// <summary>Generates a texture name only when the existing slot is empty.</summary>
    /// <param name="textureId">Existing texture name or zero.</param>
    /// <returns>The existing or newly generated texture name.</returns>
    private static int EnsureTextureName(int textureId) =>
        textureId == 0 ? GL.GenTexture() : textureId;

    /// <summary>Generates a framebuffer name only when the existing slot is empty.</summary>
    /// <param name="existingFramebufferId">Existing framebuffer name or zero.</param>
    /// <returns>The existing or newly generated framebuffer name.</returns>
    private static int EnsureFramebufferName(int existingFramebufferId) =>
        existingFramebufferId == 0 ? GL.GenFramebuffer() : existingFramebufferId;

    /// <summary>Generates a vertex-array name only when the existing slot is empty.</summary>
    /// <param name="existingVertexArrayId">Existing vertex-array name or zero.</param>
    /// <returns>The existing or newly generated vertex-array name.</returns>
    private static int EnsureVertexArrayName(int existingVertexArrayId) =>
        existingVertexArrayId == 0 ? GL.GenVertexArray() : existingVertexArrayId;

    /// <summary>Throws when a render-thread operation is attempted after disposal.</summary>
    /// <exception cref="ObjectDisposedException">The bridge has already been disposed.</exception>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    /// <summary>
    /// Deletes every owned GL target object. The engine shader registry retains ownership of the
    /// registered program, matching the lifecycle used by the main VintageRTX renderer.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (colorTextureId != 0)
        {
            GL.DeleteTexture(colorTextureId);
            colorTextureId = 0;
        }
        if (framebufferId != 0)
        {
            GL.DeleteFramebuffer(framebufferId);
            framebufferId = 0;
        }
        if (vertexArrayId != 0)
        {
            GL.DeleteVertexArray(vertexArrayId);
            vertexArrayId = 0;
        }

        shader = null;
        width = 0;
        height = 0;
        disposed = true;
    }
}
