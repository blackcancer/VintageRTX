using Vintagestory.API.Client;

namespace VintageRTX.Rendering;

/// <summary>
/// Immutable binding between an owned pre-final source and Vintage Story's exact Luma destination.
/// The source and destination names are guaranteed to be distinct by <see cref="LumaBridgeContract"/>.
/// </summary>
internal readonly record struct LumaBridgeLayout(
    int SourceFramebufferId,
    int SourceTextureId,
    int DestinationFramebufferId,
    int DestinationTextureId,
    int Width,
    int Height);

/// <summary>
/// Performs CPU-only validation and luminance calculations for the pre-final Luma bridge. No method
/// in this type calls OpenGL, so malformed or stale engine layouts can be rejected before GL state
/// is changed.
/// </summary>
internal static class LumaBridgeContract
{
    /// <summary>Red coefficient used by Vintage Story's <c>luma.fsh</c>.</summary>
    internal const float RedCoefficient = 0.299f;
    /// <summary>Green coefficient used by Vintage Story's <c>luma.fsh</c>.</summary>
    internal const float GreenCoefficient = 0.587f;
    /// <summary>Blue coefficient used by Vintage Story's <c>luma.fsh</c>.</summary>
    internal const float BlueCoefficient = 0.114f;

    /// <summary>Calculates the exact alpha value expected by Vintage Story's FXAA Luma carrier.</summary>
    /// <param name="red">Red source channel.</param>
    /// <param name="green">Green source channel.</param>
    /// <param name="blue">Blue source channel.</param>
    /// <returns>Weighted luminance using the engine's 0.299/0.587/0.114 coefficients.</returns>
    internal static float ComputeLuma(float red, float green, float blue) =>
        red * RedCoefficient + green * GreenCoefficient + blue * BlueCoefficient;

    /// <summary>
    /// Resolves the exact-size public Luma target and proves that an owned temporary source cannot
    /// alias either its framebuffer or colour texture.
    /// </summary>
    /// <param name="frameBuffers">Live engine framebuffer registry.</param>
    /// <param name="frameWidth">Required source and destination width.</param>
    /// <param name="frameHeight">Required source and destination height.</param>
    /// <param name="sourceFramebufferId">Positive owned temporary framebuffer name.</param>
    /// <param name="sourceTextureId">Positive owned temporary RGBA texture name.</param>
    /// <param name="layout">Validated immutable binding when successful.</param>
    /// <param name="reason">Stable rejection reason suitable for diagnostics.</param>
    /// <returns>True only for a live exact-size Luma attachment distinct from the owned source.</returns>
    internal static bool TryResolve(
        IReadOnlyList<FrameBufferRef>? frameBuffers,
        int frameWidth,
        int frameHeight,
        int sourceFramebufferId,
        int sourceTextureId,
        out LumaBridgeLayout layout,
        out string reason)
    {
        layout = default;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            reason = "the frame dimensions are not positive";
            return false;
        }

        if (sourceFramebufferId <= 0 || sourceTextureId <= 0)
        {
            reason = "the owned temporary framebuffer or texture is unavailable";
            return false;
        }

        int lumaIndex = (int)EnumFrameBuffer.Luma;
        if (frameBuffers is null || frameBuffers.Count <= lumaIndex)
        {
            reason = "the public Luma framebuffer slot is unavailable";
            return false;
        }

        FrameBufferRef? luma = frameBuffers[lumaIndex];
        if (luma is null || luma.Disposed || luma.FboId <= 0)
        {
            reason = "the public Luma framebuffer is null, disposed, or unnamed";
            return false;
        }

        if (luma.Width != frameWidth || luma.Height != frameHeight)
        {
            reason = "the public Luma framebuffer dimensions do not match the bridge source";
            return false;
        }

        if (luma.ColorTextureIds is null
            || luma.ColorTextureIds.Length == 0
            || luma.ColorTextureIds[0] <= 0)
        {
            reason = "the public Luma colour attachment zero is unavailable";
            return false;
        }

        int destinationTextureId = luma.ColorTextureIds[0];
        if (luma.FboId == sourceFramebufferId || destinationTextureId == sourceTextureId)
        {
            reason = "the owned temporary source aliases the public Luma destination";
            return false;
        }

        layout = new LumaBridgeLayout(
            sourceFramebufferId,
            sourceTextureId,
            luma.FboId,
            destinationTextureId,
            frameWidth,
            frameHeight);
        reason = "ready";
        return true;
    }
}
