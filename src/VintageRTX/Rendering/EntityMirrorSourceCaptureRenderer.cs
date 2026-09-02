using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Captures Primary view positions after terrain and before the engine's entity renderer. Comparing
/// this immutable image with the existing late-opaque position snapshot produces an entity-only
/// visibility mask without depending on entity classes, meshes, or mod-specific renderer types.
/// </summary>
internal sealed class EntityMirrorSourceCaptureRenderer : IRenderer
{
    /// <summary>Primary attachment containing view-space positions.</summary>
    private const int PositionAttachment = 3;

    /// <summary>Private texture unit used only while allocating the owned copy.</summary>
    private const TextureUnit AllocationTextureUnit = TextureUnit.Texture23;

    private readonly ICoreClientAPI api;
    private int positionTextureId;
    private int sourceTextureId;
    private int width;
    private int height;
    private bool ready;
    private bool enabled = true;
    private bool failureLogged;

    /// <summary>Creates an initially enabled terrain-boundary capture.</summary>
    /// <param name="api">Client renderer and framebuffer registry.</param>
    internal EntityMirrorSourceCaptureRenderer(ICoreClientAPI api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>Runs after opaque terrain order 0.37 and before opaque entities order 0.4.</summary>
    public double RenderOrder => 0.39;

    /// <summary>Requests every camera distance because the source is screen-wide.</summary>
    public int RenderRange => 0;

    /// <summary>Gets or sets whether the current frame should retain an early position snapshot.</summary>
    internal bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            if (!value)
            {
                ready = false;
            }
        }
    }

    /// <summary>Gets the owned pre-entity view-position texture, or zero before allocation.</summary>
    internal int PositionTextureId => positionTextureId;

    /// <summary>Reports whether the owned position image belongs to the current exact-size frame.</summary>
    /// <param name="frameWidth">Expected source width.</param>
    /// <param name="frameHeight">Expected source height.</param>
    /// <returns>True only for a current successful copy.</returns>
    internal bool IsReady(int frameWidth, int frameHeight) =>
        ready
        && positionTextureId > 0
        && width == frameWidth
        && height == frameHeight;

    /// <summary>Copies the terrain-complete Primary position attachment at the opaque boundary.</summary>
    /// <param name="deltaTime">Unused render-frame duration.</param>
    /// <param name="stage">Render stage invoking this boundary.</param>
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        if (stage != EnumRenderStage.Opaque)
        {
            return;
        }

        if (!Enabled)
        {
            ready = false;
            return;
        }

        try
        {
            int frameWidth = api.Render.FrameWidth;
            int frameHeight = api.Render.FrameHeight;
            string reason = "the frame dimensions are not positive";
            if (frameWidth <= 0
                || frameHeight <= 0
                || !ReflectionSourceCaptureRenderer.TryResolvePrimarySnapshotSource(
                    api.Render.FrameBuffers,
                    frameWidth,
                    frameHeight,
                    out FrameBufferRef? primary,
                    out reason))
            {
                ready = false;
                LogFailureOnce(reason);
                return;
            }

            int currentSourceTextureId = primary!.ColorTextureIds[PositionAttachment];
            EnsureTexture(currentSourceTextureId, frameWidth, frameHeight);
            GL.CopyImageSubData(
                currentSourceTextureId,
                ImageTarget.Texture2D,
                0,
                0,
                0,
                0,
                positionTextureId,
                ImageTarget.Texture2D,
                0,
                0,
                0,
                0,
                frameWidth,
                frameHeight,
                1);
            ready = true;
            failureLogged = false;
        }
        catch (Exception exception)
        {
            ready = false;
            LogFailureOnce(exception.Message);
        }
    }

    /// <summary>Allocates exact-format position storage only when the engine texture or size changes.</summary>
    /// <param name="currentSourceTextureId">Live engine position texture.</param>
    /// <param name="frameWidth">Required width.</param>
    /// <param name="frameHeight">Required height.</param>
    private void EnsureTexture(int currentSourceTextureId, int frameWidth, int frameHeight)
    {
        positionTextureId = positionTextureId == 0 ? GL.GenTexture() : positionTextureId;
        if (sourceTextureId == currentSourceTextureId
            && width == frameWidth
            && height == frameHeight)
        {
            return;
        }

        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.ActiveTexture(AllocationTextureUnit);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTextureBinding);
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, currentSourceTextureId);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D,
                0,
                GetTextureParameter.TextureInternalFormat,
                out int internalFormat);
            if (internalFormat == 0)
            {
                throw new InvalidOperationException(
                    $"Primary position texture {currentSourceTextureId} exposes no sized format.");
            }

            GL.BindTexture(TextureTarget.Texture2D, positionTextureId);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest);
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
                (PixelInternalFormat)internalFormat,
                frameWidth,
                frameHeight,
                0,
                PixelFormat.Rgba,
                PixelType.Float,
                IntPtr.Zero);
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, previousTextureBinding);
            GL.ActiveTexture((TextureUnit)previousActiveTexture);
        }

        sourceTextureId = currentSourceTextureId;
        width = frameWidth;
        height = frameHeight;
        ready = false;
        api.Logger.Notification(
            "[VintageRTX] Pre-entity position snapshot resized to {0}x{1}.",
            frameWidth,
            frameHeight);
    }

    /// <summary>Emits one stable warning until a later successful frame resets the latch.</summary>
    /// <param name="reason">Failure reason.</param>
    private void LogFailureOnce(string reason)
    {
        if (failureLogged)
        {
            return;
        }

        failureLogged = true;
        api.Logger.Warning(
            "[VintageRTX] Entity mirror pre-entity capture unavailable: {0}.",
            reason);
    }

    /// <summary>Deletes the owned position texture.</summary>
    public void Dispose()
    {
        if (positionTextureId != 0)
        {
            GL.DeleteTexture(positionTextureId);
            positionTextureId = 0;
        }

        sourceTextureId = 0;
        width = 0;
        height = 0;
        ready = false;
    }
}
