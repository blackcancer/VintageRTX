using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Client;

internal sealed record RuntimeCapturedImage(int Width, int Height, byte[] Rgba);

/// <summary>Called only at AfterBlit, before GUI rendering. Uses native viewport size, no resampling.</summary>
internal static class RuntimeFrameCapture
{
    internal static RuntimeCapturedImage ReadDefaultViewport()
    {
        int[] viewport = new int[4]; GL.GetInteger(GetPName.Viewport, viewport);
        if (GL.GetInteger(GetPName.DrawFramebufferBinding) != 0)
            throw new InvalidOperationException("AfterBlit did not expose the default framebuffer; no substitute capture taken.");
        int width = viewport[2], height = viewport[3];
        if (viewport[0] != 0 || viewport[1] != 0 || width <= 0 || height <= 0 || (long)width * height > 16_777_216)
            throw new InvalidOperationException("Unsupported native screenshot viewport (origin must be zero, maximum 16,777,216 pixels).");
        int oldRead = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int oldBuffer = GL.GetInteger(GetPName.ReadBuffer), oldPack = GL.GetInteger(GetPName.PixelPackBufferBinding);
        int alignment = GL.GetInteger(GetPName.PackAlignment), rowLength = GL.GetInteger(GetPName.PackRowLength);
        int skipRows = GL.GetInteger(GetPName.PackSkipRows), skipPixels = GL.GetInteger(GetPName.PackSkipPixels);
        byte[] pixels = new byte[checked(width * height * 4)];
        int defaultReadBuffer = -1;
        try
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            defaultReadBuffer = GL.GetInteger(GetPName.ReadBuffer);
            // glfw / game may expose a single- or double-buffered default drawable.
            GL.ReadBuffer(GL.GetBoolean(GetPName.Doublebuffer) ? ReadBufferMode.Back : ReadBufferMode.Front);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1); GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipRows, 0); GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            var error = GL.GetError();
            if (error != ErrorCode.NoError) throw new InvalidOperationException("GL error observed at runtime screenshot: " + error);
            return new(width, height, pixels);
        }
        finally
        {
            if (defaultReadBuffer >= 0) { GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0); GL.ReadBuffer((ReadBufferMode)defaultReadBuffer); }
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, oldRead); GL.ReadBuffer((ReadBufferMode)oldBuffer);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, oldPack);
            GL.PixelStore(PixelStoreParameter.PackAlignment, alignment); GL.PixelStore(PixelStoreParameter.PackRowLength, rowLength);
            GL.PixelStore(PixelStoreParameter.PackSkipRows, skipRows); GL.PixelStore(PixelStoreParameter.PackSkipPixels, skipPixels);
        }
    }
}
