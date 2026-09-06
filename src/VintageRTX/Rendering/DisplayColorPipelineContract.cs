using Vintagestory.API.Client;

namespace VintageRTX.Rendering;

/// <summary>Semantic signal carried by Vintage Story's Primary colour attachment at a render stage.</summary>
internal enum DisplayColorSignal
{
    /// <summary>The signal could not be established from the engine stage or GL attachment.</summary>
    Unknown,
    /// <summary>
    /// Scene-referred colour before Vintage Story&apos;s final shader; the public stage alone does
    /// not prove a radiometrically linear transfer function.
    /// </summary>
    SceneReferredPreFinal,
    /// <summary>Display-referred colour after Vintage Story's final grade, gamma, and vignette shader.</summary>
    DisplayReferred
}

/// <summary>OpenGL transfer encoding declared by a framebuffer colour attachment.</summary>
internal enum GlColorEncoding
{
    /// <summary>The driver returned neither GL_LINEAR nor GL_SRGB.</summary>
    Unknown,
    /// <summary>Framebuffer writes are stored without an automatic sRGB transfer conversion.</summary>
    Linear,
    /// <summary>Framebuffer writes are converted through the OpenGL sRGB transfer function when enabled.</summary>
    Srgb
}

/// <summary>
/// Pure validation and classification for the Vintage Story 1.22.7 colour pipeline. The engine
/// invokes AfterPostProcessing before final.fsh and AfterBlit after final composition but before UI.
/// </summary>
internal static class DisplayColorPipelineContract
{
    /// <summary>OpenGL token returned for a linear framebuffer attachment.</summary>
    internal const int LinearColorEncoding = 0x2601;
    /// <summary>OpenGL token returned for an sRGB framebuffer attachment.</summary>
    internal const int SrgbColorEncoding = 0x8C40;

    /// <summary>Maps the two relevant public render stages to their documented engine signal.</summary>
    /// <param name="stage">Current Vintage Story render stage.</param>
    /// <returns>Scene-referred before final composition, display-referred after blit, otherwise unknown.</returns>
    internal static DisplayColorSignal ExpectedSignal(EnumRenderStage stage) => stage switch
    {
        EnumRenderStage.AfterPostProcessing => DisplayColorSignal.SceneReferredPreFinal,
        EnumRenderStage.AfterBlit => DisplayColorSignal.DisplayReferred,
        _ => DisplayColorSignal.Unknown
    };

    /// <summary>Classifies the GL attachment encoding token without inferring shader colour grading.</summary>
    /// <param name="attachmentEncoding">Value of FRAMEBUFFER_ATTACHMENT_COLOR_ENCODING.</param>
    /// <returns>Linear for GL_LINEAR, sRGB for GL_SRGB, otherwise unknown.</returns>
    internal static GlColorEncoding ClassifyAttachmentEncoding(int attachmentEncoding) =>
        attachmentEncoding switch
        {
            LinearColorEncoding => GlColorEncoding.Linear,
            SrgbColorEncoding => GlColorEncoding.Srgb,
            _ => GlColorEncoding.Unknown
        };

    /// <summary>Validates the exact live Primary colour source used by the stage diagnostic.</summary>
    /// <param name="frameBuffers">Engine framebuffer registry.</param>
    /// <param name="frameWidth">Required current frame width.</param>
    /// <param name="frameHeight">Required current frame height.</param>
    /// <param name="primary">Validated Primary framebuffer.</param>
    /// <param name="textureId">Positive Primary colour attachment zero.</param>
    /// <returns>Whether the registry exposes a live exact-size Primary colour target.</returns>
    internal static bool TryResolvePrimary(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int frameWidth,
        int frameHeight,
        out FrameBufferRef? primary,
        out int textureId) => TryResolveColorAttachment(
            frameBuffers,
            EnumFrameBuffer.Primary,
            frameWidth,
            frameHeight,
            out primary,
            out textureId);

    /// <summary>
    /// Validates the public Luma framebuffer consumed as <c>primaryScene</c> by Vintage Story&apos;s
    /// final shader. Its RGB channels carry the pre-final scene and alpha carries the FXAA luma.
    /// </summary>
    /// <param name="frameBuffers">Engine framebuffer registry.</param>
    /// <param name="frameWidth">Required current frame width.</param>
    /// <param name="frameHeight">Required current frame height.</param>
    /// <param name="luma">Validated Luma framebuffer.</param>
    /// <param name="textureId">Positive Luma colour attachment zero.</param>
    /// <returns>Whether the registry exposes a live exact-size Luma input to <c>final.fsh</c>.</returns>
    internal static bool TryResolveLuma(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int frameWidth,
        int frameHeight,
        out FrameBufferRef? luma,
        out int textureId) => TryResolveColorAttachment(
            frameBuffers,
            EnumFrameBuffer.Luma,
            frameWidth,
            frameHeight,
            out luma,
            out textureId);

    /// <summary>
    /// Validates Vintage Story&apos;s dedicated liquid-depth attachment. Unlike Primary depth, this
    /// texture follows the actual tessellated liquid faces, including vertical faces beside slabs,
    /// stairs, plants, and other partial geometry.
    /// </summary>
    /// <param name="frameBuffers">Engine framebuffer registry.</param>
    /// <param name="frameWidth">Required current frame width.</param>
    /// <param name="frameHeight">Required current frame height.</param>
    /// <param name="liquidDepth">Validated liquid-depth framebuffer.</param>
    /// <param name="textureId">Positive liquid depth texture.</param>
    /// <returns>Whether an exact-size, live liquid-depth target is available.</returns>
    internal static bool TryResolveLiquidDepth(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int frameWidth,
        int frameHeight,
        out FrameBufferRef? liquidDepth,
        out int textureId)
    {
        liquidDepth = null;
        textureId = 0;
        int frameBufferIndex = (int)EnumFrameBuffer.LiquidDepth;
        if (frameWidth <= 0
            || frameHeight <= 0
            || frameBuffers.Count <= frameBufferIndex)
        {
            return false;
        }

        FrameBufferRef candidate = frameBuffers[frameBufferIndex];
        if (candidate.Disposed
            || candidate.FboId <= 0
            || candidate.Width != frameWidth
            || candidate.Height != frameHeight
            || candidate.DepthTextureId <= 0)
        {
            return false;
        }

        liquidDepth = candidate;
        textureId = candidate.DepthTextureId;
        return true;
    }

    /// <summary>Validates one exact-size named framebuffer colour attachment without GL access.</summary>
    /// <param name="frameBuffers">Engine framebuffer registry.</param>
    /// <param name="kind">Public framebuffer slot to validate.</param>
    /// <param name="frameWidth">Required current frame width.</param>
    /// <param name="frameHeight">Required current frame height.</param>
    /// <param name="frameBuffer">Validated framebuffer reference.</param>
    /// <param name="textureId">Positive colour attachment-zero texture.</param>
    /// <returns>Whether the requested framebuffer is live and matches the current viewport.</returns>
    private static bool TryResolveColorAttachment(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        EnumFrameBuffer kind,
        int frameWidth,
        int frameHeight,
        out FrameBufferRef? frameBuffer,
        out int textureId)
    {
        frameBuffer = null;
        textureId = 0;
        int frameBufferIndex = (int)kind;
        if (frameWidth <= 0
            || frameHeight <= 0
            || frameBuffers.Count <= frameBufferIndex)
        {
            return false;
        }

        FrameBufferRef candidate = frameBuffers[frameBufferIndex];
        if (candidate.Disposed
            || candidate.FboId <= 0
            || candidate.Width != frameWidth
            || candidate.Height != frameHeight
            || candidate.ColorTextureIds is null
            || candidate.ColorTextureIds.Length == 0
            || candidate.ColorTextureIds[0] <= 0)
        {
            return false;
        }

        frameBuffer = candidate;
        textureId = candidate.ColorTextureIds[0];
        return true;
    }
}
