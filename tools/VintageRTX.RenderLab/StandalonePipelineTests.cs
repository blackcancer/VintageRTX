using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Configuration;

namespace VintageRTX.RenderLab;

/// <summary>Checks actual sampler ownership and framebuffer transitions in the standalone production pipeline.</summary>
[TestClass]
[TestCategory("GPU")]
[DoNotParallelize]
public sealed class StandalonePipelineTests
{
    /// <summary>MSTest owns the isolated artifacts directory.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Final-target allocation may not replace the liquid input; a deliberate alias must be rejected.</summary>
    [TestMethod]
    public void OutputAllocationPreservesLiquidSamplerAndDetectsFeedback()
    {
        InContext(() =>
        {
            using StandaloneRenderer renderer = NewRenderer();
            int output = Field<int>(renderer, "outputTexture");
            int simulation = Field<int>(renderer, "dynamicLiquidTexture");
            Assert.AreNotEqual(output, simulation);
            renderer.ValidateTextureOwnership(output);
            GL.ActiveTexture(TextureUnit.Texture15);
            Assert.AreEqual(simulation, GL.GetInteger(GetPName.TextureBinding2D));
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int format);
            Assert.AreEqual((int)PixelInternalFormat.Rgba32f, format);
            GL.BindTexture(TextureTarget.Texture2D, output);
            Assert.ThrowsException<InvalidOperationException>(() => renderer.ValidateTextureOwnership(output));
            GL.BindTexture(TextureTarget.Texture2D, simulation);
            renderer.ValidateTextureOwnership(output);
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, GL.GetError());
        });
    }

    /// <summary>Every frame includes raw, filter and final draws, restores the full viewport and uses HDR banks.</summary>
    [TestMethod]
    public void RawFilterResolveDrawsUseDisjointHdrTargets()
    {
        InContext(() =>
        {
            using StandaloneRenderer renderer = NewRenderer();
            renderer.RenderFrame(VintageRtxDebugView.VoxelBounce, 0);
            GL.Finish();
            Assert.AreEqual(3L, renderer.TransportDrawCount);
            int[] viewport = new int[4]; GL.GetInteger(GetPName.Viewport, viewport);
            CollectionAssert.AreEqual(new int[] { 0, 0, 320, 180 }, viewport);
            int program = Field<int>(renderer, "program");
            GL.GetUniform(program, GL.GetUniformLocation(program, "shadowPass"), out int pass);
            GL.GetUniform(program, GL.GetUniformLocation(program, "prefilteredShadowVisibility"), out int filtered);
            Assert.AreEqual(0, pass); Assert.AreEqual(1, filtered);
            int[][] banks = Field<int[][]>(renderer, "transportTextures");
            Assert.AreEqual(6, banks.SelectMany(bank => bank).Distinct().Count());
            GL.ActiveTexture(TextureUnit.Texture18);
            GL.BindTexture(TextureTarget.Texture2D, banks[0][2]);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int format);
            Assert.AreEqual((int)PixelInternalFormat.Rgba16f, format);
            renderer.RenderFrame(VintageRtxDebugView.Final, 1);
            GL.Finish();
            Assert.AreEqual(6L, renderer.TransportDrawCount);
            renderer.ValidateTextureOwnership(Field<int>(renderer, "outputTexture"));
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, GL.GetError());
        });
    }

    /// <summary>Creates the minimum supported lab definition without changing the long smoke test.</summary>
    private StandaloneRenderer NewRenderer() => new(new RenderLabOptions(320, 180, 60,
        Path.Combine(TestContext.TestRunResultsDirectory!, "pipeline-" + Guid.NewGuid().ToString("N"))));

    /// <summary>Reads fixture state solely for driver round-trip assertions.</summary>
    private static T Field<T>(StandaloneRenderer instance, string name) =>
        (T)(typeof(StandaloneRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance) ?? throw new InvalidOperationException("Missing fixture state: " + name));

    /// <summary>Owns a real hidden OpenGL context and restores OpenTK's non-parallel test thread guard.</summary>
    private static void InContext(Action action)
    {
        Type provider = typeof(NativeWindow).Assembly.GetType("OpenTK.Windowing.Desktop.GLFWProvider", true)!;
        PropertyInfo guard = provider.GetProperty("CheckForMainThread", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        bool previous = (bool)guard.GetValue(null)!;
        try
        {
            guard.SetValue(null, false);
            using NativeWindow window = new(new NativeWindowSettings
            {
                API = ContextAPI.OpenGL, APIVersion = new Version(4, 3), Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible, ClientSize = new Vector2i(64, 64),
                StartVisible = false, StartFocused = false, AutoLoadBindings = false, NumberOfSamples = 0
            });
            window.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); action();
        }
        finally { guard.SetValue(null, previous); }
    }
}
