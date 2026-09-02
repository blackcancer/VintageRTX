using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Renders a horizontal mirrored world view into a half-resolution colour/depth target. The primary
/// path replays official terrain and entity geometry under the reflected camera; the clean late-
/// opaque point projection remains a compatibility fallback and never contains the deferred arm.
/// </summary>
internal sealed class EntityMirrorProjection : IDisposable
{
    /// <summary>Stable name used by the engine memory-shader registry.</summary>
    private const string ShaderName = "vintagertx-entity-mirror";

    /// <summary>Allocation-free transparent colour used to clear the mirror carrier.</summary>
    private static readonly float[] TransparentClear = [0.0f, 0.0f, 0.0f, 0.0f];

    /// <summary>Allocation-free far depth used to clear the mirror depth attachment.</summary>
    private static readonly float[] FarDepthClear = [1.0f];

    private readonly ICoreClientAPI api;
    private IShaderProgram? shader;
    private int colorTextureId;
    private int depthTextureId;
    private int framebufferId;
    private int entityEvidenceColorTextureId;
    private int entityEvidenceDepthTextureId;
    private int entityEvidenceFramebufferId;
    private int vertexArrayId;
    private int width;
    private int height;
    private bool disposed;
    private readonly double[] mirroredCameraMatrix = new double[16];

    /// <summary>Creates an unallocated mirror projector.</summary>
    /// <param name="api">Client shader and OpenGL services.</param>
    internal EntityMirrorProjection(ICoreClientAPI api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>Gets the half-resolution reflected world colour/coverage texture.</summary>
    internal int ColorTextureId => colorTextureId;

    /// <summary>Gets reflected world depth for reconstructing and clipping source geometry.</summary>
    internal int DepthTextureId => depthTextureId;

    /// <summary>Gets the owned framebuffer used for raw entity-only evidence readback.</summary>
    internal int FramebufferId => framebufferId;

    /// <summary>Gets the entity-only evidence framebuffer used by automated overlay validation.</summary>
    internal int EntityEvidenceFramebufferId => entityEvidenceFramebufferId;

    /// <summary>Gets the allocated mirror-carrier width.</summary>
    internal int Width => width;

    /// <summary>Gets the allocated mirror-carrier height.</summary>
    internal int Height => height;

    /// <summary>Gets whether a complete target currently exists.</summary>
    internal bool IsAllocated =>
        !disposed
        && colorTextureId > 0
        && depthTextureId > 0
        && framebufferId > 0
        && entityEvidenceColorTextureId > 0
        && entityEvidenceDepthTextureId > 0
        && entityEvidenceFramebufferId > 0
        && vertexArrayId > 0
        && width > 0
        && height > 0;

    /// <summary>
    /// Clears and rebuilds the mirrored world for one dominant liquid plane. Callers restore GL
    /// state after the complete display pass, so this bounded helper intentionally performs no queries.
    /// </summary>
    /// <param name="frameWidth">Full source width.</param>
    /// <param name="frameHeight">Full source height.</param>
    /// <param name="cleanColorTextureId">Late-opaque colour without local first person.</param>
    /// <param name="cleanPositionTextureId">Matching late-opaque position.</param>
    /// <param name="terrainPositionTextureId">Position captured immediately before entities.</param>
    /// <param name="projectionMatrix">Current column-major perspective projection.</param>
    /// <param name="viewMatrix">Current floating-origin view transform.</param>
    /// <param name="inverseViewMatrix">Inverse floating-origin view matrix.</param>
    /// <param name="floatingOriginX">Absolute player-origin X restored after inverse view.</param>
    /// <param name="floatingOriginY">Absolute player-origin Y restored after inverse view.</param>
    /// <param name="floatingOriginZ">Absolute player-origin Z restored after inverse view.</param>
    /// <param name="surfaceWorldY">Dominant horizontal liquid interface.</param>
    /// <param name="maximumDistance">Maximum reflected entity distance in world blocks.</param>
    /// <param name="captureEntityEvidence">Whether to render an isolated entity carrier this frame.</param>
    internal void Render(
        int frameWidth,
        int frameHeight,
        int cleanColorTextureId,
        int cleanPositionTextureId,
        int terrainPositionTextureId,
        float[] projectionMatrix,
        float[] viewMatrix,
        float[] inverseViewMatrix,
        double floatingOriginX,
        double floatingOriginY,
        double floatingOriginZ,
        float surfaceWorldY,
        float maximumDistance,
        bool captureEntityEvidence)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        }
        if (cleanColorTextureId <= 0
            || cleanPositionTextureId <= 0
            || terrainPositionTextureId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanColorTextureId));
        }
        if (!float.IsFinite(surfaceWorldY) || !float.IsFinite(maximumDistance))
        {
            throw new ArgumentOutOfRangeException(nameof(surfaceWorldY));
        }

        EnsureSize(Math.Max(1, (frameWidth + 1) / 2), Math.Max(1, (frameHeight + 1) / 2));
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebufferId);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(0, 0, width, height);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ProgramPointSize);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.ColorMask(true, true, true, true);
        GL.ClearBuffer(ClearBuffer.Color, 0, TransparentClear);
        GL.ClearBuffer(ClearBuffer.Depth, 0, FarDepthClear);
        BuildReflectedViewMatrix(
            api.Render.CameraMatrixOrigin,
            surfaceWorldY - floatingOriginY,
            mirroredCameraMatrix);
        _ = EntityMirrorGeometryReplayPatch.TryReplay(mirroredCameraMatrix);

        // Conservative entity supplement for future engine builds whose exact
        // renderer moves. It cannot reveal occluded faces by itself, but the
        // official replay remains beneath it whenever available.
        RenderProjectedEntities(
            framebufferId,
            false,
            frameWidth,
            frameHeight,
            cleanColorTextureId,
            cleanPositionTextureId,
            terrainPositionTextureId,
            projectionMatrix,
            viewMatrix,
            inverseViewMatrix,
            floatingOriginX,
            floatingOriginY,
            floatingOriginZ,
            surfaceWorldY,
            maximumDistance);
        if (captureEntityEvidence)
        {
            RenderProjectedEntities(
                entityEvidenceFramebufferId,
                true,
                frameWidth,
                frameHeight,
                cleanColorTextureId,
                cleanPositionTextureId,
                terrainPositionTextureId,
                projectionMatrix,
                viewMatrix,
                inverseViewMatrix,
                floatingOriginX,
                floatingOriginY,
                floatingOriginZ,
                surfaceWorldY,
                maximumDistance);
            // The primary first-person frame has no local third-person body,
            // so screen-derived points alone cannot prove its reflection.
            // Replay official entities without terrain into the evidence target
            // after the clean supplement; reflected depth keeps the result
            // entity-only while exposing occluded and lower body faces.
            _ = EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(mirroredCameraMatrix);
        }
    }

    /// <summary>Projects the clean late-opaque entity delta into one owned mirror framebuffer.</summary>
    /// <param name="targetFramebuffer">Destination framebuffer.</param>
    /// <param name="clearTarget">Whether to clear color/depth before drawing entity evidence.</param>
    /// <param name="frameWidth">Full source width.</param>
    /// <param name="frameHeight">Full source height.</param>
    /// <param name="cleanColorTextureId">Late-opaque color without first person.</param>
    /// <param name="cleanPositionTextureId">Matching late-opaque position.</param>
    /// <param name="terrainPositionTextureId">Position captured before entities.</param>
    /// <param name="projectionMatrix">Current projection.</param>
    /// <param name="viewMatrix">Current view transform.</param>
    /// <param name="inverseViewMatrix">Inverse view transform.</param>
    /// <param name="floatingOriginX">Floating-origin X.</param>
    /// <param name="floatingOriginY">Floating-origin Y.</param>
    /// <param name="floatingOriginZ">Floating-origin Z.</param>
    /// <param name="surfaceWorldY">Dominant liquid interface.</param>
    /// <param name="maximumDistance">Maximum entity distance.</param>
    private void RenderProjectedEntities(
        int targetFramebuffer,
        bool clearTarget,
        int frameWidth,
        int frameHeight,
        int cleanColorTextureId,
        int cleanPositionTextureId,
        int terrainPositionTextureId,
        float[] projectionMatrix,
        float[] viewMatrix,
        float[] inverseViewMatrix,
        double floatingOriginX,
        double floatingOriginY,
        double floatingOriginZ,
        float surfaceWorldY,
        float maximumDistance)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFramebuffer);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(0, 0, width, height);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ProgramPointSize);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.ColorMask(true, true, true, true);
        if (clearTarget)
        {
            GL.ClearBuffer(ClearBuffer.Color, 0, TransparentClear);
            GL.ClearBuffer(ClearBuffer.Depth, 0, FarDepthClear);
        }

        shader!.Use();
        shader.BindTexture2D("cleanColor", cleanColorTextureId, 0);
        shader.BindTexture2D("cleanPosition", cleanPositionTextureId, 1);
        shader.BindTexture2D("terrainPosition", terrainPositionTextureId, 2);
        shader.Uniform("sourceSize", (float)frameWidth, (float)frameHeight);
        shader.Uniform(
            "floatingWorldOrigin",
            (float)floatingOriginX,
            (float)floatingOriginY,
            (float)floatingOriginZ);
        shader.Uniform("surfaceWorldY", surfaceWorldY);
        shader.Uniform("maximumDistance", Math.Max(maximumDistance, 1.0f));
        shader.UniformMatrix("projection", projectionMatrix);
        shader.UniformMatrix("viewMatrix", viewMatrix);
        shader.UniformMatrix("inverseViewMatrix", inverseViewMatrix);
        GL.BindVertexArray(vertexArrayId);
        // One instance enumerates one source row and one vertex enumerates one source column.
        GL.DrawArraysInstanced(PrimitiveType.Points, 0, frameWidth, frameHeight);
        shader.Stop();
    }

    /// <summary>
    /// Composes a horizontal world reflection after the engine's floating-origin view matrix.
    /// Reflecting geometry and reflecting the camera are projectively equivalent; this form lets
    /// every official entity renderer keep its ordinary entity-local model transform.
    /// </summary>
    /// <param name="cameraMatrix">Column-major floating-origin view matrix.</param>
    /// <param name="surfaceLocalY">Liquid plane Y relative to the floating player origin.</param>
    /// <param name="destination">Sixteen-element column-major output.</param>
    internal static void BuildReflectedViewMatrix(
        double[] cameraMatrix,
        double surfaceLocalY,
        double[] destination)
    {
        if (cameraMatrix.Length < 16 || destination.Length < 16)
        {
            throw new ArgumentException("A complete 4x4 matrix is required.", nameof(cameraMatrix));
        }

        Array.Copy(cameraMatrix, destination, 16);
        for (int row = 0; row < 4; row++)
        {
            destination[4 + row] = -cameraMatrix[4 + row];
            destination[12 + row] = cameraMatrix[12 + row]
                + 2.0 * surfaceLocalY * cameraMatrix[4 + row];
        }
    }

    /// <summary>Allocates the RGBA16F colour and depth24 target and compiles its shader lazily.</summary>
    /// <param name="targetWidth">Positive half-resolution width.</param>
    /// <param name="targetHeight">Positive half-resolution height.</param>
    private void EnsureSize(int targetWidth, int targetHeight)
    {
        EnsureShader();
        colorTextureId = colorTextureId == 0 ? GL.GenTexture() : colorTextureId;
        depthTextureId = depthTextureId == 0 ? GL.GenTexture() : depthTextureId;
        framebufferId = framebufferId == 0 ? GL.GenFramebuffer() : framebufferId;
        entityEvidenceColorTextureId = entityEvidenceColorTextureId == 0
            ? GL.GenTexture()
            : entityEvidenceColorTextureId;
        entityEvidenceDepthTextureId = entityEvidenceDepthTextureId == 0
            ? GL.GenTexture()
            : entityEvidenceDepthTextureId;
        entityEvidenceFramebufferId = entityEvidenceFramebufferId == 0
            ? GL.GenFramebuffer()
            : entityEvidenceFramebufferId;
        vertexArrayId = vertexArrayId == 0 ? GL.GenVertexArray() : vertexArrayId;
        if (width == targetWidth && height == targetHeight)
        {
            return;
        }

        AllocateTexture(
            colorTextureId,
            PixelInternalFormat.Rgba16f,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear,
            targetWidth,
            targetHeight);
        AllocateTexture(
            depthTextureId,
            PixelInternalFormat.DepthComponent24,
            PixelFormat.DepthComponent,
            PixelType.Float,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            targetWidth,
            targetHeight);
        AllocateTexture(
            entityEvidenceColorTextureId,
            PixelInternalFormat.Rgba16f,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear,
            targetWidth,
            targetHeight);
        AllocateTexture(
            entityEvidenceDepthTextureId,
            PixelInternalFormat.DepthComponent24,
            PixelFormat.DepthComponent,
            PixelType.Float,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            targetWidth,
            targetHeight);
        AttachFramebuffer(framebufferId, colorTextureId, depthTextureId, "world mirror");
        AttachFramebuffer(
            entityEvidenceFramebufferId,
            entityEvidenceColorTextureId,
            entityEvidenceDepthTextureId,
            "entity evidence");

        width = targetWidth;
        height = targetHeight;
        api.Logger.Notification(
            "[VintageRTX] Half-resolution entity mirror target resized to {0}x{1}.",
            width,
            height);
    }

    /// <summary>Attaches and validates one owned color/depth mirror target.</summary>
    /// <param name="targetFramebuffer">Framebuffer name.</param>
    /// <param name="targetColorTexture">RGBA16F color attachment.</param>
    /// <param name="targetDepthTexture">Depth24 attachment.</param>
    /// <param name="label">Diagnostic target label.</param>
    private static void AttachFramebuffer(
        int targetFramebuffer,
        int targetColorTexture,
        int targetDepthTexture,
        string label)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, targetFramebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            targetColorTexture,
            0);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D,
            targetDepthTexture,
            0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)
            != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"The {label} framebuffer is incomplete.");
        }
    }

    /// <summary>Allocates one clamp-to-edge two-dimensional target texture.</summary>
    /// <param name="textureId">Owned texture name.</param>
    /// <param name="internalFormat">Sized storage format.</param>
    /// <param name="pixelFormat">External component layout.</param>
    /// <param name="pixelType">External scalar type.</param>
    /// <param name="minimumFilter">Minification filter.</param>
    /// <param name="magnificationFilter">Magnification filter.</param>
    /// <param name="targetWidth">Allocation width.</param>
    /// <param name="targetHeight">Allocation height.</param>
    private static void AllocateTexture(
        int textureId,
        PixelInternalFormat internalFormat,
        PixelFormat pixelFormat,
        PixelType pixelType,
        TextureMinFilter minimumFilter,
        TextureMagFilter magnificationFilter,
        int targetWidth,
        int targetHeight)
    {
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)minimumFilter);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)magnificationFilter);
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
            internalFormat,
            targetWidth,
            targetHeight,
            0,
            pixelFormat,
            pixelType,
            IntPtr.Zero);
    }

    /// <summary>Loads and compiles the asset-backed projection program once.</summary>
    private void EnsureShader()
    {
        if (shader is not null && !shader.Disposed)
        {
            return;
        }

        if (!api.Shader.IsGLSLVersionSupported("330"))
        {
            throw new NotSupportedException("Entity mirror projection requires GLSL 3.30 or newer.");
        }

        DisplayShaderProgramSource source = EntityMirrorShaderSource.Load(api.Assets);
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
                $"The entity mirror shader did not compile ({EntityMirrorShaderSource.VertexAssetCode}, "
                + $"{EntityMirrorShaderSource.FragmentAssetCode}).");
        }

        shader = program;
    }

    /// <summary>Deletes all owned OpenGL targets; the engine shader registry owns the program.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        DeleteTexture(ref colorTextureId);
        DeleteTexture(ref depthTextureId);
        DeleteTexture(ref entityEvidenceColorTextureId);
        DeleteTexture(ref entityEvidenceDepthTextureId);
        if (framebufferId != 0)
        {
            GL.DeleteFramebuffer(framebufferId);
            framebufferId = 0;
        }
        if (entityEvidenceFramebufferId != 0)
        {
            GL.DeleteFramebuffer(entityEvidenceFramebufferId);
            entityEvidenceFramebufferId = 0;
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

    /// <summary>Deletes one optional texture name and clears its slot.</summary>
    /// <param name="textureId">Owned texture slot.</param>
    private static void DeleteTexture(ref int textureId)
    {
        if (textureId == 0)
        {
            return;
        }

        GL.DeleteTexture(textureId);
        textureId = 0;
    }
}
