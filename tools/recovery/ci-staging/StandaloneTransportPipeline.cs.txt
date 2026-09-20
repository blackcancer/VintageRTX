using System.Diagnostics;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using VintageRTX.Configuration;

namespace VintageRTX.RenderLab;

/// <summary>Runs the production raw/filter/resolve passes instead of timing its full-resolution fallback.</summary>
internal sealed partial class StandaloneRenderer
{
    private readonly int[][] transportTextures = [new int[3], new int[3]];
    private readonly int[] transportFramebuffers = new int[2];
    private readonly Dictionary<string, double> phaseMilliseconds = new(StringComparer.Ordinal);
    private int dynamicLiquidTexture;
    private int transportWidth;
    private int transportHeight;
    private long transportDrawCount;

    /// <summary>Number of actual GL draws, including raw visibility, spatial filtering and final resolve.</summary>
    internal long TransportDrawCount => transportDrawCount;

    /// <summary>Persists phase costs even when the outer test deadline interrupts a later phase.</summary>
    private void RecordPhase(string name, Stopwatch timer)
    {
        phaseMilliseconds[name] = timer.Elapsed.TotalMilliseconds;
        File.WriteAllText(Path.Combine(options.OutputDirectory, "phase-timings.json"),
            JsonSerializer.Serialize(phaseMilliseconds, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RenderLab phase {name}: {timer.Elapsed.TotalMilliseconds:0.0} ms");
        Console.Out.Flush();
    }

    /// <summary>Allocates disjoint HDR transport banks and initializes the complete sampler contract.</summary>
    private void InitializeTransportPipeline()
    {
        if (GL.GetInteger(GetPName.MaxTextureImageUnits) < 24
            || GL.GetInteger(GetPName.MaxDrawBuffers) < 3)
            throw new NotSupportedException("The production transport laboratory needs 24 texture units and three draw buffers.");

        // Match full/Quality runtime resolution without lowering any angular, distance or frame budget.
        transportWidth = Math.Max(1, options.Width / 2);
        transportHeight = Math.Max(1, options.Height / 2);
        int previousActive = GL.GetInteger(GetPName.ActiveTexture);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        try
        {
            for (int bank = 0; bank < 2; bank++)
            {
                for (int slot = 0; slot < 3; slot++)
                {
                    int texture = GL.GenTexture();
                    transportTextures[bank][slot] = texture;
                    textures.Add(texture);
                    GL.BindTexture(TextureTarget.Texture2D, texture);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
                        transportWidth, transportHeight, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
                }
                transportFramebuffers[bank] = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, transportFramebuffers[bank]);
                for (int slot = 0; slot < 3; slot++)
                    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                        (FramebufferAttachment)((int)FramebufferAttachment.ColorAttachment0 + slot),
                        TextureTarget.Texture2D, transportTextures[bank][slot], 0);
                GL.DrawBuffers(3, TransportDrawBuffers);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                    throw new InvalidOperationException($"Transport bank {bank} is not framebuffer-complete.");
                GL.ClearBuffer(ClearBuffer.Color, 0, new float[] { 1, 1, 1, 1 });
                GL.ClearBuffer(ClearBuffer.Color, 1, new float[] { 1, 1, 1, 1 });
                GL.ClearBuffer(ClearBuffer.Color, 2, new float[] { 1, 0, 0, 0 });
            }
        }
        finally
        {
            GL.ActiveTexture((TextureUnit)previousActive);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        }

        // Native cascades are absent in the synthetic world. Give comparison and ordinary
        // depth samplers separate valid units; never rely on unit-zero defaults of another type.
        CreateTexture2D(22, PixelInternalFormat.DepthComponent32f, PixelFormat.DepthComponent,
            PixelType.Float, 1, 1, new float[] { 1 }, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        CreateTexture2D(23, PixelInternalFormat.DepthComponent32f, PixelFormat.DepthComponent,
            PixelType.Float, 1, 1, new float[] { 1 }, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        Set("nativeShadowMapFar", 22); Set("nativeShadowMapNear", 22);
        Set("gOpaqueDepth", 23); Set("gLiquidDepth", 23); Set("entityMirrorDepth", 23);
        Set("gOpaquePosition", 2); Set("gDirectPosition", 2); Set("gUnlitAlbedo", 0);
        Set("nativeShadowFarEnabled", 0); Set("nativeShadowNearEnabled", 0);
        Set("opaquePositionEnabled", 1); Set("opaqueDepthEnabled", 0); Set("liquidDepthEnabled", 0);
        Set("rawAlbedoEnabled", 0); Set("entityMirrorEnabled", 0); Set("entityMirrorColor", 0);
        SetRequired("shadowPointCurrentA", 16); SetRequired("shadowPointCurrentB", 17);
        SetRequired("shadowSunCurrent", 18);
        SetRequired("shadowPointHistoryA", 19); SetRequired("shadowPointHistoryB", 20);
        SetRequired("shadowSunHistory", 21);
        SetRequired("shadowInverseFrameSize", 1f / transportWidth, 1f / transportHeight);
        SetRequired("shadowFilterTapCount", 4);
        SetRequired("shadowTemporalBlend", 0f);
        SetRequired("secondaryBounceCadence", 1);
        BindTransportInputs(0, 1);
        GL.ActiveTexture((TextureUnit)previousActive);
        CheckGl("transport initialization");
    }

    private static readonly DrawBuffersEnum[] TransportDrawBuffers =
        [DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2];

    /// <summary>Binds read-only banks that never alias the bank being written in this pass.</summary>
    private void BindTransportInputs(int currentBank, int historyBank)
    {
        for (int slot = 0; slot < 3; slot++)
        {
            BindTextureUnit(16 + slot, TextureTarget.Texture2D, transportTextures[currentBank][slot]);
            BindTextureUnit(19 + slot, TextureTarget.Texture2D, transportTextures[historyBank][slot]);
        }
    }

    /// <summary>Rejects texture feedback and loss of the SI liquid-state binding before issuing a draw.</summary>
    internal void ValidateTextureOwnership(params int[] outputs)
    {
        int previousActive = GL.GetInteger(GetPName.ActiveTexture);
        try
        {
            GL.ActiveTexture(TextureUnit.Texture15);
            if (GL.GetInteger(GetPName.TextureBinding2D) != dynamicLiquidTexture)
                throw new InvalidOperationException("The liquid sampler lost its simulation texture.");
            for (int unit = 0; unit < 24; unit++)
            {
                GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                int texture = GL.GetInteger(GetPName.TextureBinding2D);
                if (texture != 0 && Array.IndexOf(outputs, texture) >= 0)
                    throw new InvalidOperationException($"Render target {texture} is also bound for sampling on unit {unit}.");
            }
        }
        finally { GL.ActiveTexture((TextureUnit)previousActive); }
    }

    /// <summary>Draws and counts one complete full-screen pass using the unchanged production shader.</summary>
    private void DrawTransportPass(int pass, int target, int width, int height, int[] outputTextures)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, target);
        GL.Viewport(0, 0, width, height);
        if (pass == 0) GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        else GL.DrawBuffers(3, TransportDrawBuffers);
        SetRequired("shadowPass", pass);
        SetRequired("prefilteredShadowVisibility", pass == 0 ? 1 : 0);
        ValidateTextureOwnership(outputTextures);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        transportDrawCount++;
    }

    /// <summary>Runs the same three transport stages for normal, diagnostic, warm-up and timed frames.</summary>
    private void RenderTransportFrame(VintageRtxDebugView view, int frameIndex, int outputColorDomain)
    {
        GL.UseProgram(program);
        GL.BindVertexArray(vertexArray);
        Set("debugView", (int)view);
        Set("outputColorDomain", outputColorDomain);
        Set("temporalFrameIndex", frameIndex);
        // No bank attached for writing is bound as an input, even for unused uniform branches.
        BindTransportInputs(1, 1);
        DrawTransportPass(1, transportFramebuffers[0], transportWidth, transportHeight, transportTextures[0]);
        BindTransportInputs(0, 0);
        DrawTransportPass(2, transportFramebuffers[1], transportWidth, transportHeight, transportTextures[1]);
        BindTransportInputs(0, 1);
        DrawTransportPass(0, framebuffer, options.Width, options.Height, [outputTexture]);
        CheckGl($"three-pass render {view}");
    }
}
