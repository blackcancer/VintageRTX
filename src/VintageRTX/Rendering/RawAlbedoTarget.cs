using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Rendering;

/// <summary>
/// Owns an unlit RGB/view-depth attachment without replacing any native G-buffer channel.
/// It is attached only during the opaque pass and detached before late overlays or reflections.
/// Unsupported writers have their extra draw route disabled rather than writing undefined data.
/// </summary>
internal sealed class RawAlbedoTarget : IDisposable
{
    /// <summary>First attachment after the four native Primary colour attachments.</summary>
    internal const int AttachmentIndex = 4;
    private int texture;
    private int primary;
    private int width;
    private int height;
    private bool active;
    private bool previousBlend;
    private readonly bool[] previousMask = new bool[4];
    private DrawBuffersEnum[] routes = [];
    private bool hasWriter;
    private bool disposed;

    /// <summary>Gets a texture only after the opaque transaction has ended successfully.</summary>
    internal int TextureId => Ready ? texture : 0;
    /// <summary>Gets whether a known opaque writer participated in the published frame.</summary>
    internal bool Ready { get; private set; }
    /// <summary>Gets whether draw routing currently belongs to an opaque capture transaction.</summary>
    internal bool Active => active;

    /// <summary>Attaches owned storage to an unused native slot, preserving all four native outputs.</summary>
    /// <param name="framebuffer">Borrowed Primary framebuffer, never deleted.</param>
    /// <param name="frameWidth">Exact native attachment width.</param>
    /// <param name="frameHeight">Exact native attachment height.</param>
    /// <returns>False for unsupported layouts, foreign ownership or incomplete targets.</returns>
    internal bool Begin(int framebuffer, int frameWidth, int frameHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        End(false);
        Ready = false;
        if (framebuffer <= 0 || frameWidth <= 0 || frameHeight <= 0) return false;
        GL.GetInteger(GetPName.MaxDrawBuffers, out int maximum);
        GL.GetInteger(GetPName.MaxColorAttachments, out int attachments);
        if (maximum <= AttachmentIndex || attachments <= AttachmentIndex) return false;
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int previousFramebuffer);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTexture);
        bool previousScissor = GL.IsEnabled(EnableCap.ScissorTest);
        bool attached = false;
        bool statesCaptured = false;
        try
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment4, FramebufferParameterName.FramebufferAttachmentObjectType, out int owner);
            if (owner != 0) return false;
            routes = new DrawBuffersEnum[Math.Min(maximum, 16)];
            for (int i = 0; i < routes.Length; i++)
            {
                GL.GetInteger((GetPName)((int)GetPName.DrawBuffer0 + i), out int route);
                routes[i] = (DrawBuffersEnum)route;
                if (i >= AttachmentIndex && route != 0) return false;
            }
            if (texture == 0 || width != frameWidth || height != frameHeight)
            {
                int replacement = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, replacement);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
                    frameWidth, frameHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
                if (texture != 0) GL.DeleteTexture(texture);
                texture = replacement;
                width = frameWidth;
                height = frameHeight;
            }
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
                TextureTarget.Texture2D, texture, 0);
            attached = true;
            if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
                return false;
            previousBlend = GL.IsEnabled(IndexedEnableCap.Blend, AttachmentIndex);
            GL.GetBoolean(GetIndexedPName.ColorWritemask, AttachmentIndex, previousMask);
            statesCaptured = true;
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(IndexedEnableCap.Blend, AttachmentIndex);
            GL.ColorMask(AttachmentIndex, true, true, true, true);
            routes[AttachmentIndex] = DrawBuffersEnum.ColorAttachment4;
            GL.DrawBuffers(routes.Length, routes);
            GL.ClearBuffer(ClearBuffer.Color, AttachmentIndex, new float[4]);
            // Only a shader positively identified as a writer may open this route.
            routes[AttachmentIndex] = DrawBuffersEnum.None;
            GL.DrawBuffers(routes.Length, routes);
            primary = framebuffer;
            active = true;
            hasWriter = false;
            return true;
        }
        finally
        {
            if (!active && attached)
            {
                if (routes.Length > AttachmentIndex)
                {
                    routes[AttachmentIndex] = DrawBuffersEnum.None;
                    GL.DrawBuffers(routes.Length, routes);
                }
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
                    TextureTarget.Texture2D, 0, 0);
                if (statesCaptured) RestoreIndexedState();
            }
            if (previousScissor) GL.Enable(EnableCap.ScissorTest); else GL.Disable(EnableCap.ScissorTest);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousFramebuffer);
        }
    }

    /// <summary>Routes only the optional attachment; native shader outputs retain their current routes.</summary>
    /// <param name="writesAlbedo">Whether the bound shader supplies the explicit raw-albedo output.</param>
    internal void SetWriter(bool writesAlbedo)
    {
        if (!active) return;
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int bound);
        if (bound != primary) return;
        for (int i = 0; i < routes.Length; i++)
        {
            GL.GetInteger((GetPName)((int)GetPName.DrawBuffer0 + i), out int route);
            routes[i] = (DrawBuffersEnum)route;
        }
        routes[AttachmentIndex] = writesAlbedo ? DrawBuffersEnum.ColorAttachment4 : DrawBuffersEnum.None;
        GL.DrawBuffers(routes.Length, routes);
        if (writesAlbedo)
        {
            GL.Disable(IndexedEnableCap.Blend, AttachmentIndex);
            GL.ColorMask(AttachmentIndex, true, true, true, true);
            hasWriter = true;
        }
    }

    /// <summary>Detaches owned storage so the final shader samples immutable, non-feedback data.</summary>
    /// <param name="publish">Whether this completed opaque frame may be consumed.</param>
    internal void End(bool publish = true)
    {
        if (!active)
        {
            if (!publish) Ready = false;
            return;
        }
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int previousFramebuffer);
        bool ownershipPreserved = false;
        try
        {
            if (GL.IsFramebuffer(primary))
            {
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, primary);
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                    FramebufferAttachment.ColorAttachment4, FramebufferParameterName.FramebufferAttachmentObjectName, out int owner);
                ownershipPreserved = owner == texture;
                if (ownershipPreserved)
                {
                    for (int i = 0; i < routes.Length; i++)
                    {
                        GL.GetInteger((GetPName)((int)GetPName.DrawBuffer0 + i), out int route);
                        routes[i] = (DrawBuffersEnum)route;
                    }
                    routes[AttachmentIndex] = DrawBuffersEnum.None;
                    GL.DrawBuffers(routes.Length, routes);
                    GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
                        TextureTarget.Texture2D, 0, 0);
                }
            }
            RestoreIndexedState();
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousFramebuffer);
            active = false;
            Ready = publish && hasWriter && ownershipPreserved;
            primary = 0;
        }
    }

    /// <summary>Restores only the indexed blend and write-mask state owned by this optional slot.</summary>
    private void RestoreIndexedState()
    {
        if (previousBlend) GL.Enable(IndexedEnableCap.Blend, AttachmentIndex);
        else GL.Disable(IndexedEnableCap.Blend, AttachmentIndex);
        GL.ColorMask(AttachmentIndex, previousMask[0], previousMask[1], previousMask[2], previousMask[3]);
    }

    /// <summary>Releases only owned storage; construction and unused disposal require no GL context.</summary>
    public void Dispose()
    {
        if (disposed) return;
        End(false);
        if (texture != 0) GL.DeleteTexture(texture);
        texture = 0;
        disposed = true;
    }
}
