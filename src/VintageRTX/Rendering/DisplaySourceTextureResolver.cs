using Vintagestory.API.Client;

namespace VintageRTX.Rendering;

/// <summary>
/// Selects an engine-owned Primary colour texture only when the display pass writes to a known,
/// distinct off-screen texture. Unknown layouts retain the owned framebuffer-copy fallback.
/// </summary>
internal static class DisplaySourceTextureResolver
{
    /// <summary>
    /// Resolves the current Primary colour attachment without creating a read/write feedback loop.
    /// </summary>
    /// <param name="frameBuffers">Live engine framebuffer registry.</param>
    /// <param name="readFramebufferId">Framebuffer from which the legacy copy would read.</param>
    /// <param name="frameWidth">Required source width.</param>
    /// <param name="frameHeight">Required source height.</param>
    /// <param name="distinctDestinationTextureId">
    /// Non-zero off-screen output texture; zero denotes a direct or unknown destination.
    /// </param>
    /// <param name="sourceTextureId">Borrowed Primary attachment zero when safe.</param>
    /// <returns>
    /// True only when source and destination are known textures with different names and the
    /// active read framebuffer exactly matches the live, exact-size Primary framebuffer.
    /// </returns>
    internal static bool TryResolve(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int readFramebufferId,
        int frameWidth,
        int frameHeight,
        int distinctDestinationTextureId,
        out int sourceTextureId)
    {
        sourceTextureId = 0;
        int primaryIndex = (int)EnumFrameBuffer.Primary;
        if (readFramebufferId <= 0
            || distinctDestinationTextureId <= 0
            || frameBuffers.Count <= primaryIndex)
        {
            return false;
        }

        FrameBufferRef primary = frameBuffers[primaryIndex];
        if (primary.Disposed
            || primary.FboId != readFramebufferId
            || primary.Width != frameWidth
            || primary.Height != frameHeight
            || primary.ColorTextureIds is null
            || primary.ColorTextureIds.Length == 0)
        {
            return false;
        }

        int candidate = primary.ColorTextureIds[0];
        if (candidate <= 0 || candidate == distinctDestinationTextureId)
        {
            return false;
        }

        sourceTextureId = candidate;
        return true;
    }
}
