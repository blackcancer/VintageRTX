using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Rendering;
using VintageRTX.Test;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.RenderLab;

/// <summary>Exercises the canonical Luma repack asset on a real headless OpenGL context.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LumaRepackGlTests
{
    /// <summary>Compiles the asset shader and verifies HDR RGB preservation plus luma alpha.</summary>
    [TestMethod]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void AssetShaderPreservesRgbAndWritesEngineLumaAlpha()
    {
        RunWithContext("VintageRTX.LumaRepack.Test", ExerciseRepackShader);
    }

    /// <summary>Verifies production target allocation, stable resize, and idempotent deletion.</summary>
    [TestMethod]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void ProductionBridgeOwnsResizesAndDisposesDistinctRgbaTarget()
    {
        RunWithContext("VintageRTX.LumaBridge.Target.Test", ExerciseProductionBridgeTarget);
    }

    /// <summary>Compiles and links the production entity-mirror point projection on a real driver.</summary>
    [TestMethod]
    [TestCategory("GPU")]
    [TestCategory("Reflection")]
    [Timeout(60_000)]
    public void EntityMirrorAssetsCompileAndLink()
    {
        RunWithContext("VintageRTX.EntityMirror.Shader.Test", ExerciseEntityMirrorShader);
    }

    /// <summary>Runs one assertion body inside an isolated invisible OpenGL 4.3 context.</summary>
    /// <param name="title">Diagnostic native-window title.</param>
    /// <param name="assertions">Assertions requiring a current loaded GL context.</param>
    private static void RunWithContext(string title, Action assertions)
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
                ClientSize = new Vector2i(16, 16),
                StartVisible = false,
                StartFocused = false,
                AutoLoadBindings = false,
                NumberOfSamples = 0,
                Title = title
            };
            using NativeWindow window = new(settings);
            window.MakeCurrent();
            GLFWBindingsContext bindings = new();
            GL.LoadBindings(bindings);
            assertions();
        }
        finally
        {
            threadGuard.SetValue(null, previousGuard);
        }
    }

    /// <summary>Exercises the real owned target while replacing only the engine shader registry.</summary>
    private static void ExerciseProductionBridgeTarget()
    {
        LumaRenderBridge bridge = new(CreateClientApi());
        bridge.BindTarget(2, 2);
        int texture = bridge.ColorTextureId;
        int framebuffer = bridge.FramebufferId;
        Assert.IsTrue(bridge.IsAllocated);
        Assert.AreEqual(2, bridge.Width);
        Assert.AreEqual(2, bridge.Height);
        Assert.IsTrue(GL.IsTexture(texture));
        Assert.IsTrue(GL.IsFramebuffer(framebuffer));

        bridge.EnsureSize(2, 2);
        Assert.AreEqual(texture, bridge.ColorTextureId);
        Assert.AreEqual(framebuffer, bridge.FramebufferId);
        bridge.EnsureSize(3, 1);
        Assert.AreEqual(texture, bridge.ColorTextureId);
        Assert.AreEqual(framebuffer, bridge.FramebufferId);
        Assert.AreEqual(3, bridge.Width);
        Assert.AreEqual(1, bridge.Height);

        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D,
            0,
            GetTextureParameter.TextureWidth,
            out int textureWidth);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D,
            0,
            GetTextureParameter.TextureHeight,
            out int textureHeight);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D,
            0,
            GetTextureParameter.TextureInternalFormat,
            out int internalFormat);
        Assert.AreEqual(3, textureWidth);
        Assert.AreEqual(1, textureHeight);
        Assert.AreEqual((int)PixelInternalFormat.Rgba16f, internalFormat);

        bridge.Dispose();
        bridge.Dispose();
        Assert.IsFalse(bridge.IsAllocated);
        Assert.IsFalse(GL.IsTexture(texture));
        Assert.IsFalse(GL.IsFramebuffer(framebuffer));
        Assert.ThrowsException<ObjectDisposedException>(() => bridge.EnsureSize(1, 1));
        Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, GL.GetError());
    }

    /// <summary>Creates a client API double whose shader registry satisfies lazy bridge setup.</summary>
    /// <returns>Client API using canonical text assets and a successful no-op shader program.</returns>
    private static ICoreClientAPI CreateClientApi()
    {
        DisplayShaderProgramSource files = LumaBridgeShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory);
        IShader vertex = ShaderStage();
        IShader fragment = ShaderStage();
        IShaderProgram program = RuntimeCoverageDispatchProxy.Create<IShaderProgram>(
            (method, _) => method.Name switch
            {
                "get_VertexShader" => vertex,
                "get_FragmentShader" => fragment,
                "Compile" => true,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        IShaderAPI shaderApi = RuntimeCoverageDispatchProxy.Create<IShaderAPI>(
            (method, arguments) => method.Name switch
            {
                "IsGLSLVersionSupported" => true,
                "NewShaderProgram" => program,
                "NewShader" => arguments![0] is EnumShaderType.VertexShader ? vertex : fragment,
                "RegisterMemoryShaderProgram" => 1,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        IAssetManager assets = RuntimeCoverageDispatchProxy.Create<IAssetManager>(
            (method, arguments) => method.Name == "TryGet"
                ? TextAsset(arguments![0]!.ToString() == LumaBridgeShaderSource.VertexAssetCode
                    ? files.Vertex
                    : files.Fragment)
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>(
            (method, _) => method.Name switch
            {
                "get_Shader" => shaderApi,
                "get_Assets" => assets,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
    }

    /// <summary>Creates a writable no-op Vintage Story shader-stage double.</summary>
    /// <returns>Shader stage accepted by the bridge's shader-program setup.</returns>
    private static IShader ShaderStage() =>
        RuntimeCoverageDispatchProxy.Create<IShader>(
            (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

    /// <summary>Creates a deterministic text asset for bridge shader loading.</summary>
    /// <param name="source">Shader text returned by <c>ToText</c>.</param>
    /// <returns>Asset double containing the supplied source.</returns>
    private static IAsset TextAsset(string source) =>
        RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "ToText"
                ? source
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

    /// <summary>Runs the shader assertions after its caller has made an OpenGL context current.</summary>
    private static void ExerciseRepackShader()
    {
        DisplayShaderProgramSource source = LumaBridgeShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory);
        int vertexShader = CompileShader(ShaderType.VertexShader, source.Vertex);
        int fragmentShader = CompileShader(ShaderType.FragmentShader, source.Fragment);
        int program = GL.CreateProgram();
        int sourceTexture = 0;
        int destinationTexture = 0;
        int framebuffer = 0;
        int vertexArray = 0;
        try
        {
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linkStatus);
            Assert.AreEqual(1, linkStatus, GL.GetProgramInfoLog(program));

            float[] input =
            [
                1.0f, 0.0f, 0.0f, 1.0f,
                0.0f, 1.0f, 0.0f, 1.0f,
                0.0f, 0.0f, 1.0f, 1.0f,
                2.0f, 1.5f, 0.5f, 1.0f
            ];
            sourceTexture = CreateTexture(2, 2, input);
            destinationTexture = CreateTexture(2, 2, null);
            framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                destinationTexture,
                0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            Assert.AreEqual(
                FramebufferErrorCode.FramebufferComplete,
                GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            vertexArray = GL.GenVertexArray();
            GL.BindVertexArray(vertexArray);
            GL.Viewport(0, 0, 2, 2);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.UseProgram(program);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, sourceTexture);
            GL.Uniform1(GL.GetUniformLocation(program, "sourceColor"), 0);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            float[] output = new float[input.Length];
            GL.ReadPixels(0, 0, 2, 2, PixelFormat.Rgba, PixelType.Float, output);
            AssertPixel(input, output, 0, 0.299f);
            AssertPixel(input, output, 1, 0.587f);
            AssertPixel(input, output, 2, 0.114f);
            AssertPixel(input, output, 3, 1.5355f);
            Assert.IsTrue(output[12] > 1.0f, "HDR red was clipped before final.fsh.");
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            GL.UseProgram(0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindVertexArray(0);
            if (vertexArray != 0)
            {
                GL.DeleteVertexArray(vertexArray);
            }
            if (framebuffer != 0)
            {
                GL.DeleteFramebuffer(framebuffer);
            }
            if (sourceTexture != 0)
            {
                GL.DeleteTexture(sourceTexture);
            }
            if (destinationTexture != 0)
            {
                GL.DeleteTexture(destinationTexture);
            }
            GL.DeleteProgram(program);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
        }
    }

    /// <summary>Compiles and links the canonical entity-mirror shader pair.</summary>
    private static void ExerciseEntityMirrorShader()
    {
        string shaderDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "vintagertx",
            "shaders");
        int vertexShader = CompileShader(
            ShaderType.VertexShader,
            File.ReadAllText(Path.Combine(shaderDirectory, "entitymirror.vert")));
        int fragmentShader = CompileShader(
            ShaderType.FragmentShader,
            File.ReadAllText(Path.Combine(shaderDirectory, "entitymirror.frag")));
        int program = GL.CreateProgram();
        try
        {
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linkStatus);
            Assert.AreEqual(1, linkStatus, GL.GetProgramInfoLog(program));
            Assert.IsTrue(GL.GetUniformLocation(program, "cleanPosition") >= 0);
            Assert.IsTrue(GL.GetUniformLocation(program, "terrainPosition") >= 0);
            Assert.IsTrue(GL.GetUniformLocation(program, "surfaceWorldY") >= 0);
        }
        finally
        {
            GL.DeleteProgram(program);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
        }
    }

    /// <summary>Compiles one GLSL stage and reports the driver log on failure.</summary>
    /// <param name="type">OpenGL shader stage.</param>
    /// <param name="source">Canonical asset source.</param>
    /// <returns>Compiled shader name.</returns>
    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            Assert.Fail($"{type} compilation failed: {log}");
        }
        return shader;
    }

    /// <summary>Creates a nearest-filtered RGBA16F texture with optional initial HDR pixels.</summary>
    /// <param name="width">Texture width.</param>
    /// <param name="height">Texture height.</param>
    /// <param name="pixels">Optional tightly packed floating-point RGBA values.</param>
    /// <returns>Allocated texture name.</returns>
    private static int CreateTexture(int width, int height, float[]? pixels)
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba16f,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            pixels);
        return texture;
    }

    /// <summary>Checks preserved HDR RGB and one half-float luma-alpha output pixel.</summary>
    /// <param name="input">Original tightly packed floating-point RGBA values.</param>
    /// <param name="output">Rendered tightly packed floating-point RGBA values.</param>
    /// <param name="pixelIndex">Pixel to inspect.</param>
    /// <param name="expectedAlpha">Expected luma alpha before half-float quantization.</param>
    private static void AssertPixel(
        IReadOnlyList<float> input,
        IReadOnlyList<float> output,
        int pixelIndex,
        float expectedAlpha)
    {
        int offset = pixelIndex * 4;
        Assert.AreEqual(input[offset], output[offset], 0.002f);
        Assert.AreEqual(input[offset + 1], output[offset + 1], 0.002f);
        Assert.AreEqual(input[offset + 2], output[offset + 2], 0.002f);
        Assert.AreEqual(expectedAlpha, output[offset + 3], 0.002f);
    }
}
