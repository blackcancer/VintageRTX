using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Client;

/// <summary>Restores only states changed by owned full-screen passes, including indexed MRT state.</summary>
internal sealed class RewriteDrawState : IDisposable
{
    private readonly int program, vao, drawFramebuffer, readFramebuffer, activeTexture;
    private readonly int[] viewport = new int[4], textures, samplers;
    private readonly RewritePolygonModes polygon;
    private readonly bool[][] colorMasks;
    private readonly bool[] blends, enabled;
    private static readonly EnableCap[] Caps = [EnableCap.DepthTest, EnableCap.CullFace, EnableCap.ScissorTest,
        EnableCap.StencilTest, EnableCap.FramebufferSrgb, EnableCap.Dither, EnableCap.RasterizerDiscard,
        EnableCap.ColorLogicOp, EnableCap.SampleAlphaToCoverage, EnableCap.SampleCoverage, EnableCap.SampleMask];
    public RewriteDrawState(int textureUnits, int outputs)
    {
        program = GL.GetInteger(GetPName.CurrentProgram); vao = GL.GetInteger(GetPName.VertexArrayBinding);
        drawFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding); readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.GetInteger(GetPName.Viewport, viewport);
        // Keep room for both legacy values, but determine semantics from the actual context.
        // Drivers need not write the second slot alike in a core context. A leftover zero is
        // not evidence that separate front/back calls are legal.
        int[] reportedPolygon = new int[2];
        GL.GetInteger(GetPName.PolygonMode, reportedPolygon);
        polygon = new RewritePolygonModes(GL.GetInteger(GetPName.ContextProfileMask),
            GL.GetInteger(GetPName.ContextFlags), reportedPolygon[0], reportedPolygon[1]);
        enabled = new bool[Caps.Length];
        for (int i = 0; i < Caps.Length; i++) enabled[i] = GL.IsEnabled(Caps[i]);
        colorMasks = new bool[outputs][]; blends = new bool[outputs];
        for (int i = 0; i < outputs; i++)
        {
            colorMasks[i] = new bool[4];
            GL.GetBoolean(GetIndexedPName.ColorWritemask, i, colorMasks[i]);
            blends[i] = GL.IsEnabled(IndexedEnableCap.Blend, i);
        }
        textures = new int[textureUnits]; samplers = new int[textureUnits];
        for (int i = 0; i < textureUnits; i++)
        {
            GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + i));
            textures[i] = GL.GetInteger(GetPName.TextureBinding2D);
            samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
        }
        GL.ActiveTexture((TextureUnit)activeTexture);
    }
    public void Configure()
    {
        foreach (EnableCap cap in Caps) GL.Disable(cap);
        for (int i = 0; i < blends.Length; i++)
        { GL.Disable(IndexedEnableCap.Blend, i); GL.ColorMask(i, true, true, true, true); }
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        for (int i = 0; i < samplers.Length; i++) GL.BindSampler(i, 0);
    }
    public void Dispose()
    {
        for (int i = 0; i < textures.Length; i++)
        {
            GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + i));
            GL.BindTexture(TextureTarget.Texture2D, textures[i]); GL.BindSampler(i, samplers[i]);
        }
        GL.ActiveTexture((TextureUnit)activeTexture);
        GL.UseProgram(program); GL.BindVertexArray(vao);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
        GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        polygon.Restore();
        for (int i = 0; i < Caps.Length; i++) { if (enabled[i]) GL.Enable(Caps[i]); else GL.Disable(Caps[i]); }
        for (int i = 0; i < blends.Length; i++)
        {
            if (blends[i]) GL.Enable(IndexedEnableCap.Blend, i); else GL.Disable(IndexedEnableCap.Blend, i);
            bool[] mask = colorMasks[i]; GL.ColorMask(i, mask[0], mask[1], mask[2], mask[3]);
        }
    }
}

/// <summary>TexImage2D with client pointers must not inherit a host PBO, stride or byte swapping.</summary>
internal sealed class RewriteUnpackState : IDisposable
{
    private readonly int texture = GL.GetInteger(GetPName.TextureBinding2D);
    private readonly int pbo = GL.GetInteger(GetPName.PixelUnpackBufferBinding);
    private readonly int alignment = GL.GetInteger(GetPName.UnpackAlignment);
    private readonly int rowLength = GL.GetInteger(GetPName.UnpackRowLength);
    private readonly int skipRows = GL.GetInteger(GetPName.UnpackSkipRows);
    private readonly int skipPixels = GL.GetInteger(GetPName.UnpackSkipPixels);
    private readonly int swapBytes = GL.GetInteger(GetPName.UnpackSwapBytes);
    public RewriteUnpackState()
    {
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1); GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0); GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSwapBytes, 0);
    }
    public void Dispose()
    {
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, alignment); GL.PixelStore(PixelStoreParameter.UnpackRowLength, rowLength);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, skipRows); GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, skipPixels);
        GL.PixelStore(PixelStoreParameter.UnpackSwapBytes, swapBytes);
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pbo); GL.BindTexture(TextureTarget.Texture2D, texture);
    }
}
