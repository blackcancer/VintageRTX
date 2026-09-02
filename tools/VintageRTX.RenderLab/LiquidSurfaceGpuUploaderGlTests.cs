using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Rendering;

namespace VintageRTX.RenderLab;

/// <summary>Exercises the production dynamic-liquid texture backend on a real OpenGL context.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LiquidSurfaceGpuUploaderGlTests
{
    /// <summary>
    /// Verifies RGBA32F allocation, same-size update, resize, state restoration,
    /// and final texture deletion through the production OpenGL implementation.
    /// </summary>
    [TestMethod]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void ProductionUploaderOwnsAndUpdatesRealRgba32FloatTexture()
    {
        Type glfwProvider = typeof(NativeWindow).Assembly.GetType(
            "OpenTK.Windowing.Desktop.GLFWProvider",
            throwOnError: true)!;
        PropertyInfo threadGuard = glfwProvider.GetProperty(
            "CheckForMainThread",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMemberException(glfwProvider.FullName, "CheckForMainThread");
        bool previousGuard = (bool)(threadGuard.GetValue(null) ?? true);
        try
        {
            threadGuard.SetValue(null, false);
            NativeWindowSettings settings = new()
            {
                API = ContextAPI.OpenGL,
                APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                ClientSize = new Vector2i(32, 32),
                StartVisible = false,
                StartFocused = false,
                AutoLoadBindings = false,
                NumberOfSamples = 0,
                Title = "VintageRTX.LiquidSurfaceGpuUploader.Test"
            };
            using NativeWindow window = new(settings);
            window.MakeCurrent();
            GL.LoadBindings(new GLFWBindingsContext());
            ExerciseUploader();
        }
        finally
        {
            threadGuard.SetValue(null, previousGuard);
        }
    }

    /// <summary>Runs texture assertions after the caller has made an OpenGL 4.3 context current.</summary>
    private static void ExerciseUploader()
    {
        int sentinelTexture = GL.GenTexture();
        int uploadedTexture = 0;
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, sentinelTexture);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 8);
            LiquidSurfaceSimulation first = CreateSimulation(2, 2);
            using (LiquidSurfaceGpuUploader uploader = new())
            {
                LiquidSurfaceGpuBinding allocated = uploader.Upload(first);
                uploadedTexture = allocated.TextureId;
                Assert.IsTrue(GL.IsTexture(uploadedTexture));
                Assert.AreEqual(sentinelTexture, GL.GetInteger(GetPName.TextureBinding2D));
                Assert.AreEqual(8, GL.GetInteger(GetPName.UnpackAlignment));

                GL.BindTexture(TextureTarget.Texture2D, uploadedTexture);
                AssertTextureSize(2, 2);
                float[] pixels = new float[2 * 2 * LiquidSurfaceSimulation.GpuChannels];
                GL.GetTexImage(
                    TextureTarget.Texture2D,
                    0,
                    PixelFormat.Rgba,
                    PixelType.Float,
                    pixels);
                Assert.IsTrue(pixels.All(float.IsFinite));

                GL.BindTexture(TextureTarget.Texture2D, sentinelTexture);
                LiquidSurfaceGpuBinding updated = uploader.Upload(first);
                Assert.AreEqual(uploadedTexture, updated.TextureId);
                Assert.AreEqual(2L, updated.Revision);
                Assert.AreEqual(sentinelTexture, GL.GetInteger(GetPName.TextureBinding2D));

                LiquidSurfaceGpuBinding resized = uploader.Upload(CreateSimulation(3, 1));
                Assert.AreEqual(uploadedTexture, resized.TextureId);
                GL.BindTexture(TextureTarget.Texture2D, uploadedTexture);
                AssertTextureSize(3, 1);
                GL.BindTexture(TextureTarget.Texture2D, sentinelTexture);
            }

            Assert.IsFalse(GL.IsTexture(uploadedTexture));
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DeleteTexture(sentinelTexture);
            if (uploadedTexture != 0 && GL.IsTexture(uploadedTexture))
            {
                GL.DeleteTexture(uploadedTexture);
            }
        }
    }

    /// <summary>Asserts the currently bound texture's level-zero dimensions.</summary>
    /// <param name="expectedWidth">Expected cell count along X.</param>
    /// <param name="expectedDepth">Expected cell count along Z.</param>
    private static void AssertTextureSize(int expectedWidth, int expectedDepth)
    {
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D,
            0,
            GetTextureParameter.TextureWidth,
            out int width);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D,
            0,
            GetTextureParameter.TextureHeight,
            out int depth);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D,
            0,
            GetTextureParameter.TextureInternalFormat,
            out int internalFormat);
        Assert.AreEqual(expectedWidth, width);
        Assert.AreEqual(expectedDepth, depth);
        Assert.AreEqual((int)PixelInternalFormat.Rgba32f, internalFormat);
    }

    /// <summary>Creates a fully active grid suitable for direct texture upload.</summary>
    /// <param name="width">Cell count along X.</param>
    /// <param name="depth">Cell count along Z.</param>
    /// <returns>Configured deterministic surface.</returns>
    private static LiquidSurfaceSimulation CreateSimulation(int width, int depth)
    {
        LiquidSurfaceSimulation simulation = new(5, 7, width, depth, 0.5f);
        LiquidSurfaceDynamics dynamics = new(
            0.2f,
            0.08f,
            2.0f,
            1.5f,
            0.1f,
            1.0f,
            0.1f,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            0.0f);
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                simulation.SetSurfaceCell(x, z, 80.0f, 1, in dynamics, true);
            }
        }
        return simulation;
    }
}
