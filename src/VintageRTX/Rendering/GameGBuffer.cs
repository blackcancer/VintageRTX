using Vintagestory.API.Client;

namespace VintageRTX.Rendering;

/// <summary>
/// Identifies the three live textures consumed from Vintage Story's Primary framebuffer. Texture
/// identifiers are borrowed OpenGL handles and must never be deleted by VintageRTX.
/// </summary>
internal readonly record struct GameGBuffer(int GlowTextureId, int NormalTextureId, int PositionTextureId)
{
    // Vintage Story 1.22.x writes outColor, outGlow, outGNormal and outGPosition
    // to locations 0..3 of the documented Primary framebuffer.
    /// <summary>Primary framebuffer color attachment containing emissive/glow contribution.</summary>
    private const int GlowAttachment = 1;
    /// <summary>Primary framebuffer color attachment containing packed view-space normals.</summary>
    private const int NormalAttachment = 2;
    /// <summary>Primary framebuffer color attachment containing view-space position and depth.</summary>
    private const int PositionAttachment = 3;

    /// <summary>
    /// Validates the documented 1.22.x Primary framebuffer layout before exposing borrowed handles.
    /// </summary>
    /// <param name="render">Active client renderer and framebuffer registry.</param>
    /// <param name="gBuffer">Resolved handles when all required attachments are alive.</param>
    /// <param name="reason">Stable readiness or rejection reason suitable for logs.</param>
    /// <returns><see langword="true"/> only when all three non-zero textures can be sampled.</returns>
    public static bool TryResolve(IRenderAPI render, out GameGBuffer gBuffer, out string reason)
    {
        gBuffer = default;

        int primaryIndex = (int)EnumFrameBuffer.Primary;
        if (primaryIndex < 0 || render.FrameBuffers.Count <= primaryIndex)
        {
            reason = "the Primary framebuffer is not available";
            return false;
        }

        FrameBufferRef primary = render.FrameBuffers[primaryIndex];
        if (primary.Disposed)
        {
            reason = "the Primary framebuffer is disposed";
            return false;
        }

        if (primary.ColorTextureIds is null || primary.ColorTextureIds.Length <= PositionAttachment)
        {
            reason = $"the Primary framebuffer exposes only {primary.ColorTextureIds?.Length ?? 0} color attachments";
            return false;
        }

        int glowTextureId = primary.ColorTextureIds[GlowAttachment];
        int normalTextureId = primary.ColorTextureIds[NormalAttachment];
        int positionTextureId = primary.ColorTextureIds[PositionAttachment];
        if (glowTextureId <= 0 || normalTextureId <= 0 || positionTextureId <= 0)
        {
            reason = "the glow, normal or position attachment has no texture";
            return false;
        }

        gBuffer = new GameGBuffer(glowTextureId, normalTextureId, positionTextureId);
        reason = "ready";
        return true;
    }
}
