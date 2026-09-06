using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Reflection;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>
/// Covers the remaining deterministic error and real-driver paths in the small render services.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CoverageRenderingServicesResidualTests
{
    /// <summary>Covers Luma construction, dimension, allocation-state, and disposal guards.</summary>
    [TestMethod]
    public void LumaBridgeCpuGuardsExposeEveryAllocationState()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new LumaRenderBridge(null!));

        LumaRenderBridge bridge = new(CreateBridgeApi(glslSupported: false));
        Assert.AreEqual(0, bridge.ColorTextureId);
        Assert.AreEqual(0, bridge.FramebufferId);
        Assert.AreEqual(0, bridge.Width);
        Assert.AreEqual(0, bridge.Height);
        Assert.IsFalse(bridge.IsAllocated);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => bridge.EnsureSize(0, 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => bridge.EnsureSize(1, 0));
        Assert.ThrowsException<InvalidOperationException>(() => bridge.RepackToLuma([], 1, 1));

        SetField(bridge, "colorTextureId", 1);
        Assert.IsFalse(bridge.IsAllocated);
        SetField(bridge, "framebufferId", 2);
        Assert.IsFalse(bridge.IsAllocated);
        SetField(bridge, "vertexArrayId", 3);
        Assert.IsFalse(bridge.IsAllocated);
        SetField(bridge, "width", 1);
        Assert.IsFalse(bridge.IsAllocated);
        SetField(bridge, "height", 1);
        Assert.IsTrue(bridge.IsAllocated);
        SetField(bridge, "disposed", true);
        Assert.IsFalse(bridge.IsAllocated);

        SetField(bridge, "colorTextureId", 0);
        SetField(bridge, "framebufferId", 0);
        SetField(bridge, "vertexArrayId", 0);
        SetField(bridge, "disposed", false);
        bridge.Dispose();
        bridge.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => bridge.EnsureSize(1, 1));
        Assert.ThrowsException<ObjectDisposedException>(() => bridge.RepackToLuma([], 1, 1));
    }

    /// <summary>Covers each asset-backed shader rejection and the successful registration path.</summary>
    [TestMethod]
    public void LumaBridgeShaderRegistrationRejectsEveryInvalidProgramState()
    {
        LumaRenderBridge unsupported = new(CreateBridgeApi(glslSupported: false));
        AssertPrivateThrows<NotSupportedException>(unsupported, "EnsureShader");

        IShaderProgram retained = CreateShaderProgram(
            compiled: true,
            loadError: false,
            disposed: false);
        LumaRenderBridge alreadyReady = new(CreateBridgeApi(glslSupported: false));
        SetField(alreadyReady, "shader", retained);
        InvokePrivate(alreadyReady, "EnsureShader");
        Assert.AreSame(retained, GetField<IShaderProgram>(alreadyReady, "shader"));

        LumaRenderBridge stale = new(CreateBridgeApi(glslSupported: false));
        SetField(stale, "shader", CreateShaderProgram(true, false, disposed: true));
        AssertPrivateThrows<NotSupportedException>(stale, "EnsureShader");

        foreach ((int passId, bool compiled, bool loadError, bool disposed) in new[]
        {
            (-1, true, false, false),
            (0, false, false, false),
            (0, true, true, false),
            (0, true, false, true)
        })
        {
            IShaderProgram rejected = CreateShaderProgram(compiled, loadError, disposed);
            LumaRenderBridge bridge = new(CreateBridgeApi(
                glslSupported: true,
                rejected,
                passId));
            AssertPrivateThrows<InvalidOperationException>(bridge, "EnsureShader");
        }

        IShaderProgram accepted = CreateShaderProgram(true, false, disposed: false);
        LumaRenderBridge successful = new(CreateBridgeApi(
            glslSupported: true,
            accepted,
            passId: 0));
        InvokePrivate(successful, "EnsureShader");
        Assert.AreSame(accepted, GetField<IShaderProgram>(successful, "shader"));
    }

    /// <summary>Covers exact-size Luma allocation, repack, invalid layouts, and incomplete storage.</summary>
    [TestMethod]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void LumaBridgeUsesAndReleasesRealDriverObjects()
    {
        RunWithContext("VintageRTX.LumaBridge.Coverage", () =>
        {
            IShaderProgram shader = CreateShaderProgram(true, false, disposed: false);
            using LumaRenderBridge bridge = new(CreateBridgeApi(glslSupported: false));
            SetField(bridge, "shader", shader);
            bridge.EnsureSize(4, 4);
            Assert.IsTrue(bridge.IsAllocated);
            Assert.IsTrue(bridge.ColorTextureId > 0);
            Assert.IsTrue(bridge.FramebufferId > 0);
            Assert.AreEqual(4, bridge.Width);
            Assert.AreEqual(4, bridge.Height);
            bridge.EnsureSize(4, 4);
            bridge.BindTarget(4, 4);

            Assert.ThrowsException<InvalidOperationException>(() =>
                bridge.RepackToLuma([], 4, 4));
            Assert.ThrowsException<InvalidOperationException>(() =>
                bridge.RepackToLuma([], 5, 4));

            int destinationTexture = CreateColorTexture(4, 4);
            int destinationFramebuffer = CreateColorFramebuffer(destinationTexture);
            try
            {
                List<FrameBufferRef> frameBuffers = [];
                while (frameBuffers.Count <= (int)EnumFrameBuffer.Luma)
                {
                    frameBuffers.Add(new FrameBufferRef());
                }
                frameBuffers[(int)EnumFrameBuffer.Luma] = new FrameBufferRef
                {
                    FboId = destinationFramebuffer,
                    Width = 4,
                    Height = 4,
                    ColorTextureIds = [destinationTexture]
                };
                LumaBridgeLayout layout = bridge.RepackToLuma(frameBuffers, 4, 4);
                Assert.AreEqual(bridge.FramebufferId, layout.SourceFramebufferId);
                Assert.AreEqual(destinationFramebuffer, layout.DestinationFramebufferId);
            }
            finally
            {
                GL.DeleteFramebuffer(destinationFramebuffer);
                GL.DeleteTexture(destinationTexture);
            }

            using LumaRenderBridge incomplete = new(CreateBridgeApi(glslSupported: false));
            SetField(incomplete, "shader", shader);
            GL.GetInteger(GetPName.MaxTextureSize, out int maximumTextureSize);
            Assert.ThrowsException<InvalidOperationException>(() =>
                incomplete.EnsureSize(checked(maximumTextureSize + 1), 1));
            Assert.AreEqual(0, incomplete.Width);
            Assert.AreEqual(0, incomplete.Height);
            DrainGlErrors();
        });
    }

    /// <summary>Covers unavailable, invalid, first, and smoothed timestamp-query retirement.</summary>
    [TestMethod]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void PerformanceMonitorRetiresEveryTimestampQueryState()
    {
        RunWithContext("VintageRTX.PerformanceMonitor.Coverage", () =>
        {
            using RenderPerformanceMonitor monitor = new();
            for (int frame = 1; frame < 7; frame++)
            {
                Assert.IsFalse(monitor.TryBeginGpuMeasurement(), $"frame {frame}");
            }
            Assert.IsTrue(monitor.TryBeginGpuMeasurement());
            InvokePrivate(monitor, "EnsureQueries");
            monitor.EndGpuMeasurement();
            monitor.EndGpuMeasurement();

            int[] startQueries = GetField<int[]>(monitor, "startQueries");
            int[] endQueries = GetField<int[]>(monitor, "endQueries");
            bool[] pending = GetField<bool[]>(monitor, "pendingQueries");

            pending[3] = false;
            InvokePrivate(monitor, "ResolveQueryIfAvailable", 3);

            int savedEnd = endQueries[1];
            endQueries[1] = 0;
            pending[1] = true;
            InvokePrivate(monitor, "ResolveQueryIfAvailable", 1);
            Assert.IsTrue(pending[1]);
            SetField(monitor, "queryIndex", 1);
            SetField(monitor, "gpuQueryFrame", 6);
            Assert.IsFalse(monitor.TryBeginGpuMeasurement());
            Assert.AreEqual(2, GetField<int>(monitor, "queryIndex"));
            endQueries[1] = savedEnd;
            pending[1] = false;
            DrainGlErrors();

            int savedEndTwo = endQueries[2];
            endQueries[2] = startQueries[2];
            GL.QueryCounter(startQueries[2], QueryCounterTarget.Timestamp);
            GL.Finish();
            pending[2] = true;
            double beforeInvalid = monitor.SmoothedGpuMilliseconds;
            InvokePrivate(monitor, "ResolveQueryIfAvailable", 2);
            Assert.IsFalse(pending[2]);
            Assert.AreEqual(beforeInvalid, monitor.SmoothedGpuMilliseconds);
            endQueries[2] = savedEndTwo;

            SetField(monitor, "smoothedGpuMilliseconds", 0.0);
            IssueTimestampPair(startQueries[0], endQueries[0]);
            pending[0] = true;
            InvokePrivate(monitor, "ResolveQueryIfAvailable", 0);
            Assert.IsTrue(monitor.SmoothedGpuMilliseconds > 0.0);
            double first = monitor.SmoothedGpuMilliseconds;

            IssueTimestampPair(startQueries[0], endQueries[0]);
            pending[0] = true;
            InvokePrivate(monitor, "ResolveQueryIfAvailable", 0);
            Assert.IsTrue(monitor.SmoothedGpuMilliseconds > 0.0);
            double latest = GetField<double>(monitor, "latestGpuMilliseconds");
            Assert.AreEqual(
                first * 0.9 + latest * 0.1,
                monitor.SmoothedGpuMilliseconds,
                0.000_001);

            monitor.ResetStatistics();
            Assert.AreEqual(0.0, monitor.SmoothedGpuMilliseconds);
            monitor.Dispose();
            monitor.Dispose();
            DrainGlErrors();
        });
    }

    /// <summary>Covers failed transactional persistence and every raw-diagnostic guard operand.</summary>
    [TestMethod]
    public void FrameCaptureTransactionsExposeSaveFailureAndRawGuardOperands()
    {
        FrameCaptureService failing = CreateCaptureService(
            Path.Combine(Path.GetTempPath(), $"missing-capture-root-{Guid.NewGuid():N}"));
        Assert.IsTrue(failing.QueueCapture(new FrameCaptureRequest("save-failure", VintageRtxDebugView.Final)));
        FrameCaptureStep effect = BeginEffect(failing, 10);
        Assert.AreEqual(
            FrameCaptureAdvanceResult.SaveFailed,
            failing.SubmitPostFinalFrame(effect, [4, 5, 6, 255], 1, 1));

        FrameCaptureService reflection = CreateCaptureService(CreateExistingCaptureRoot());
        Assert.IsTrue(reflection.QueueCapture(new FrameCaptureRequest(
            "raw-guards",
            VintageRtxDebugView.ReflectionSource)));
        FrameCaptureStep reflectionEffect = BeginEffect(reflection, 20);
        Assert.IsFalse(reflection.SubmitPreFinalDiagnostic(
            reflectionEffect with { Phase = FrameCapturePhase.Baseline },
            [1, 2, 3, 255],
            1,
            1));
        Assert.IsFalse(reflection.SubmitPreFinalDiagnostic(
            reflectionEffect with
            {
                Request = new FrameCaptureRequest("wrong", VintageRtxDebugView.ReflectionSource)
            },
            [1, 2, 3, 255],
            1,
            1));

        FrameCaptureService material = CreateCaptureService(CreateExistingCaptureRoot());
        Assert.IsTrue(material.QueueCapture(new FrameCaptureRequest(
            "not-raw",
            VintageRtxDebugView.Material)));
        FrameCaptureStep materialEffect = BeginEffect(material, 30);
        Assert.IsFalse(material.SubmitPreFinalDiagnostic(
            materialEffect,
            [1, 2, 3, 255],
            1,
            1));
    }

    /// <summary>Creates a capture service with no automatic sequence and a deterministic clock.</summary>
    /// <param name="captureRoot">Path returned by the client storage API.</param>
    /// <returns>Isolated capture service.</returns>
    private static FrameCaptureService CreateCaptureService(string captureRoot)
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name switch
            {
                "GetOrCreateDataPath" => captureRoot,
                "get_Logger" => logger,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        return new FrameCaptureService(
            api,
            static _ => null,
            static () => new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>Creates a unique directory suitable for successful capture transactions.</summary>
    /// <returns>Existing writable directory.</returns>
    private static string CreateExistingCaptureRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Stores a one-pixel baseline and returns the following effect step.</summary>
    /// <param name="service">Capture service owning the transaction.</param>
    /// <param name="firstFrame">Frame used for the baseline.</param>
    /// <returns>Effect step ready for submission.</returns>
    private static FrameCaptureStep BeginEffect(FrameCaptureService service, long firstFrame)
    {
        Assert.IsTrue(service.TryBeginCaptureFrame(firstFrame, out FrameCaptureStep baseline));
        Assert.AreEqual(
            FrameCaptureAdvanceResult.BaselineStored,
            service.SubmitPostFinalFrame(baseline, [1, 2, 3, 255], 1, 1));
        Assert.IsTrue(service.TryBeginCaptureFrame(firstFrame + 1, out FrameCaptureStep effect));
        return effect;
    }

    /// <summary>Creates a client API exposing deterministic asset and shader doubles.</summary>
    /// <param name="glslSupported">Whether GLSL 3.30 is reported as supported.</param>
    /// <param name="program">Optional shader program returned for compilation.</param>
    /// <param name="passId">Registration identifier returned by the shader API.</param>
    /// <returns>Client API suitable for direct Luma bridge tests.</returns>
    private static ICoreClientAPI CreateBridgeApi(
        bool glslSupported,
        IShaderProgram? program = null,
        int passId = 0)
    {
        program ??= CreateShaderProgram(true, false, disposed: false);
        IShader stage = RuntimeCoverageDispatchProxy.Create<IShader>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IShaderProgram selectedProgram = program;
        IShaderAPI shaders = RuntimeCoverageDispatchProxy.Create<IShaderAPI>((method, _) =>
            method.Name switch
            {
                "IsGLSLVersionSupported" => glslSupported,
                "NewShaderProgram" => selectedProgram,
                "NewShader" => stage,
                "RegisterMemoryShaderProgram" => passId,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        IAsset textAsset = RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "ToText"
                ? "#version 330 core\nvoid main() {}\n"
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IAssetManager assets = RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, _) =>
            method.Name == "TryGet"
                ? textAsset
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name switch
            {
                "get_Shader" => shaders,
                "get_Assets" => assets,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
    }

    /// <summary>Creates a shader-program double with explicit compilation state.</summary>
    /// <param name="compiled">Compile return value.</param>
    /// <param name="loadError">Load-error property.</param>
    /// <param name="disposed">Disposed property.</param>
    /// <returns>Deterministic shader program.</returns>
    private static IShaderProgram CreateShaderProgram(bool compiled, bool loadError, bool disposed)
    {
        IShader stage = RuntimeCoverageDispatchProxy.Create<IShader>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        return RuntimeCoverageDispatchProxy.Create<IShaderProgram>((method, _) => method.Name switch
        {
            "Compile" => compiled,
            "get_LoadError" => loadError,
            "get_Disposed" => disposed,
            "get_VertexShader" or "get_FragmentShader" => stage,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Allocates one four-channel texture on the current driver context.</summary>
    /// <param name="width">Texture width.</param>
    /// <param name="height">Texture height.</param>
    /// <returns>Owned texture identifier.</returns>
    private static int CreateColorTexture(int width, int height)
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba16f,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            IntPtr.Zero);
        return texture;
    }

    /// <summary>Attaches one colour texture to a complete framebuffer.</summary>
    /// <param name="texture">Allocated texture.</param>
    /// <returns>Owned framebuffer identifier.</returns>
    private static int CreateColorFramebuffer(int texture)
    {
        int framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            texture,
            0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        Assert.AreEqual(
            FramebufferErrorCode.FramebufferComplete,
            GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        return framebuffer;
    }

    /// <summary>Issues one timestamp pair separated by completed driver work.</summary>
    /// <param name="startQuery">Start query identifier.</param>
    /// <param name="endQuery">End query identifier.</param>
    private static void IssueTimestampPair(int startQuery, int endQuery)
    {
        GL.QueryCounter(startQuery, QueryCounterTarget.Timestamp);
        GL.Finish();
        GL.ClearColor(0.1f, 0.2f, 0.3f, 1.0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Finish();
        GL.QueryCounter(endQuery, QueryCounterTarget.Timestamp);
        GL.Finish();
    }

    /// <summary>Runs assertions within an isolated invisible OpenGL 4.3 context.</summary>
    /// <param name="title">Native diagnostic title.</param>
    /// <param name="assertions">Assertions requiring a current context.</param>
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
            GL.LoadBindings(new GLFWBindingsContext());
            assertions();
        }
        finally
        {
            threadGuard.SetValue(null, previousGuard);
        }
    }

    /// <summary>Invokes a private method and unwraps no result.</summary>
    /// <param name="instance">Method target.</param>
    /// <param name="name">Exact method name.</param>
    /// <param name="arguments">Method arguments.</param>
    private static void InvokePrivate(object instance, string name, params object?[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, name);
        _ = method.Invoke(instance, arguments);
    }

    /// <summary>Asserts that a private method throws the requested underlying exception.</summary>
    /// <typeparam name="TException">Expected underlying exception type.</typeparam>
    /// <param name="instance">Method target.</param>
    /// <param name="name">Exact method name.</param>
    private static void AssertPrivateThrows<TException>(object instance, string name)
        where TException : Exception
    {
        TargetInvocationException wrapper = Assert.ThrowsException<TargetInvocationException>(
            () => InvokePrivate(instance, name));
        Assert.IsInstanceOfType<TException>(wrapper.InnerException);
    }

    /// <summary>Gets a private instance field.</summary>
    /// <typeparam name="T">Expected field type.</typeparam>
    /// <param name="instance">Field owner.</param>
    /// <param name="name">Exact field name.</param>
    /// <returns>Current field value.</returns>
    private static T GetField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().FullName, name));

    /// <summary>Sets a private instance field.</summary>
    /// <typeparam name="T">Assigned field type.</typeparam>
    /// <param name="instance">Field owner.</param>
    /// <param name="name">Exact field name.</param>
    /// <param name="value">New field value.</param>
    private static void SetField<T>(object instance, string name, T value)
    {
        FieldInfo field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        field.SetValue(instance, value);
    }

    /// <summary>Clears every pending driver error produced by intentional invalid-state probes.</summary>
    private static void DrainGlErrors()
    {
        while (GL.GetError() != OpenTK.Graphics.OpenGL4.ErrorCode.NoError)
        {
        }
    }
}
