using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Test;

/// <summary>
/// Exercises the production entity-mirror allocation and replay paths on a real hidden OpenGL
/// context. The Vintage Story interfaces remain deterministic test doubles, while every owned
/// texture, framebuffer, vertex array, state transition, and deletion is performed by the driver.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CoverageEntityMirrorGpuTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>Clears static replay state after each GPU assertion, including failed replays.</summary>
    [TestCleanup]
    public void CleanupReplayState()
    {
        EntityMirrorGeometryReplayPatch.Uninstall();
    }

    /// <summary>
    /// Covers projection validation, lazy shader setup, exact-size target reuse, entity-evidence
    /// submission, failed oblique clipping, incomplete framebuffer rejection, and owned GL cleanup.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void ProjectionAllocatesRendersReusesAndDisposesRealTargets()
    {
        RunWithContext("VintageRTX.EntityMirror.Projection.Coverage", () =>
        {
            List<string> logs = [];
            double[] ordinaryView = Identity();
            ordinaryView[13] = -2.0;
            double[] perspective = Perspective();
            EntityMirrorProjection projection = new(CreateProjectionApi(
                ordinaryView,
                perspective,
                logs));
            float[] matrix = Identity().Select(static value => (float)value).ToArray();

            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                0, 4, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 32.0f, false, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                4, 0, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 32.0f, false, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                4, 4, 0, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 32.0f, false, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                4, 4, 1, 0, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 32.0f, false, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                4, 4, 1, 2, 0, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 32.0f, false, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                4, 4, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, float.NaN, 32.0f, false, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                4, 4, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, float.PositiveInfinity, false, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => projection.Render(
                4, 4, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 32.0f, false, 0));

            projection.Render(
                5, 3, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 32.0f, true, 2);
            Assert.IsTrue(projection.IsAllocated);
            Assert.AreEqual(3, projection.Width);
            Assert.AreEqual(2, projection.Height);
            Assert.IsTrue(GL.IsTexture(projection.ColorTextureId));
            Assert.IsTrue(GL.IsTexture(projection.DepthTextureId));
            Assert.IsTrue(GL.IsFramebuffer(projection.FramebufferId));
            Assert.IsTrue(GL.IsFramebuffer(projection.EntityEvidenceFramebufferId));
            int color = projection.ColorTextureId;
            int depth = projection.DepthTextureId;
            int worldFramebuffer = projection.FramebufferId;
            int evidenceFramebuffer = projection.EntityEvidenceFramebufferId;

            projection.Render(
                5, 3, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 0.1f, false, 2);
            Assert.AreEqual(color, projection.ColorTextureId);
            Assert.AreEqual(depth, projection.DepthTextureId);
            Assert.AreEqual(worldFramebuffer, projection.FramebufferId);

            MethodInfo attach = PrivateMethod(
                typeof(EntityMirrorProjection),
                "AttachFramebuffer",
                parameterCount: 4,
                isStatic: true);
            int incomplete = GL.GenFramebuffer();
            try
            {
                TargetInvocationException failure = Assert.ThrowsException<TargetInvocationException>(
                    () => attach.Invoke(null, [incomplete, 0, 0, "coverage"]));
                Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
            }
            finally
            {
                GL.DeleteFramebuffer(incomplete);
            }

            projection.Dispose();
            projection.Dispose();
            Assert.IsFalse(GL.IsTexture(color));
            Assert.IsFalse(GL.IsTexture(depth));
            Assert.IsFalse(GL.IsFramebuffer(worldFramebuffer));
            Assert.IsFalse(GL.IsFramebuffer(evidenceFramebuffer));
            Assert.ThrowsException<ObjectDisposedException>(() => projection.Render(
                1, 1, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 1.0f, false, 1));
            DrainGlErrors();

            List<string> clipLogs = [];
            EntityMirrorProjection singular = new(CreateProjectionApi(
                ordinaryView,
                new double[16],
                clipLogs));
            singular.Render(
                2, 2, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 8.0f, false, 1);
            singular.Render(
                2, 2, 1, 2, 3, matrix, matrix, matrix,
                0.0, 0.0, 0.0, 0.0f, 8.0f, false, 1);
            Assert.AreEqual(
                1,
                clipLogs.Count(static entry => entry.StartsWith("Warning:", StringComparison.Ordinal)));
            singular.Dispose();
            DrainGlErrors();
        });
    }

    /// <summary>
    /// Covers official entity-only and terrain/entity replay success, callback capture guards,
    /// reflected carrier restoration, direct and reflected invocation failures, and warning latching.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void GeometryReplayExecutesAndRestoresRealGlState()
    {
        RunWithContext("VintageRTX.EntityMirror.Replay.Coverage", () =>
        {
            List<string> logs = [];
            double[] camera = Identity();
            float[] cameraFloat = ToFloat(camera);
            double[] perspective = Perspective();
            float[] currentProjection = ToFloat(perspective);
            StackMatrix4 stack = Stack(perspective);
            ICoreClientAPI api = CreateReplayApi(
                camera,
                cameraFloat,
                perspective,
                currentProjection,
                stack,
                logs,
                throwOnModelViewMode: false);
            MethodInfo pass = typeof(CoverageEntityMirrorGpuTests).GetMethod(
                nameof(SuccessfulPass),
                PrivateStatic)!;
            SetReplayState(api, pass, pass, pass);

            MethodInfo afterEntity = PrivateMethod(
                typeof(EntityMirrorGeometryReplayPatch),
                "AfterOpaqueEntityPass",
                parameterCount: 2,
                isStatic: true);
            MethodInfo afterTerrain = PrivateMethod(
                typeof(EntityMirrorGeometryReplayPatch),
                "AfterOpaqueTerrainPass",
                parameterCount: 2,
                isStatic: true);
            object entitySystem = new();
            object terrainSystem = new();
            afterEntity.Invoke(null, [entitySystem, 0.125f]);
            afterTerrain.Invoke(null, [terrainSystem, 0.25f]);
            Assert.AreSame(entitySystem, GetStaticField<object>("currentRenderSystem"));
            Assert.AreSame(terrainSystem, GetStaticField<object>("currentTerrainRenderSystem"));

            SetStaticField("replayInProgress", true);
            afterEntity.Invoke(null, [new object(), 9.0f]);
            afterTerrain.Invoke(null, [new object(), 9.0f]);
            Assert.AreSame(entitySystem, GetStaticField<object>("currentRenderSystem"));
            Assert.AreSame(terrainSystem, GetStaticField<object>("currentTerrainRenderSystem"));
            SetStaticField("replayInProgress", false);

            double[] mirrored = Identity();
            mirrored[5] = -1.0;
            Assert.IsTrue(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(
                mirrored,
                Perspective()));
            CollectionAssert.AreEqual(Identity(), camera);
            CollectionAssert.AreEqual(ToFloat(Identity()), cameraFloat);
            CollectionAssert.AreEqual(Perspective(), perspective);
            CollectionAssert.AreEqual(ToFloat(Perspective()), currentProjection);
            Assert.AreEqual(1, stack.Count);

            Assert.IsTrue(EntityMirrorGeometryReplayPatch.TryReplay(
                mirrored,
                Perspective()));
            Assert.AreEqual(1, stack.Count);
            Assert.AreEqual(0, logs.Count(static entry => entry.StartsWith("Error:", StringComparison.Ordinal)));

            MethodInfo throwing = typeof(CoverageEntityMirrorGpuTests).GetMethod(
                nameof(ThrowingPass),
                PrivateStatic)!;
            SetStaticField("opaqueEntityPass", throwing);
            Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(
                mirrored,
                Perspective()));
            Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(
                mirrored,
                Perspective()));
            Assert.AreEqual(
                1,
                logs.Count(static entry => entry.StartsWith("Error:", StringComparison.Ordinal)));
            SetStaticField("failureLogged", false);
            Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplay(
                mirrored,
                Perspective()));
            Assert.AreEqual(
                2,
                logs.Count(static entry => entry.StartsWith("Error:", StringComparison.Ordinal)));

            EntityMirrorGeometryReplayPatch.Uninstall();
            ICoreClientAPI throwingApi = CreateReplayApi(
                camera,
                cameraFloat,
                perspective,
                currentProjection,
                stack,
                logs,
                throwOnModelViewMode: true);
            SetReplayState(throwingApi, pass, pass, pass);
            Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(
                mirrored,
                Perspective()));
            Assert.IsFalse(GetStaticField<bool>("replayInProgress"));

            EntityMirrorGeometryReplayPatch.Uninstall();
            SetReplayState(api, pass, pass, pass);
            double[] invalidMirrored = Identity();
            invalidMirrored[7] = double.NaN;
            Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(
                invalidMirrored,
                Perspective()));
            Assert.IsFalse(GetStaticField<bool>("replayInProgress"));
            DrainGlErrors();
        });
    }

    /// <summary>
    /// Covers shader capability and compilation rejection independently of target allocation, and
    /// reaches both late numerical rejection points in the oblique clip-plane construction.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ProjectionRejectsUnsupportedShadersAndLateDegenerateClipPlanes()
    {
        double[] view = Identity();
        view[13] = -2.0;
        MethodInfo ensureShader = PrivateMethod(
            typeof(EntityMirrorProjection),
            "EnsureShader",
            parameterCount: 0,
            isStatic: false);
        EntityMirrorProjection unsupported = new(CreateProjectionApi(
            view,
            Perspective(),
            [],
            glslSupported: false));
        TargetInvocationException unsupportedFailure = Assert.ThrowsException<TargetInvocationException>(
            () => ensureShader.Invoke(unsupported, null));
        Assert.IsInstanceOfType<NotSupportedException>(unsupportedFailure.InnerException);

        foreach ((bool compile, int passId, bool loadError, bool disposed) in new[]
                 {
                     (false, 1, false, false),
                     (true, -1, false, false),
                     (true, 1, true, false),
                     (true, 1, false, true)
                 })
        {
            EntityMirrorProjection rejected = new(CreateProjectionApi(
                view,
                Perspective(),
                [],
                compile,
                passId,
                loadError,
                disposed));
            TargetInvocationException compileFailure = Assert.ThrowsException<TargetInvocationException>(
                () => ensureShader.Invoke(rejected, null));
            Assert.IsInstanceOfType<InvalidOperationException>(compileFailure.InnerException);
        }

        double[] zeroNormalView = Identity();
        zeroNormalView[7] = -1.0;
        double[] destination = Enumerable.Repeat(4.0, 16).ToArray();
        Assert.IsFalse(EntityMirrorProjection.BuildObliqueMirrorProjection(
            Identity(),
            zeroNormalView,
            1.0,
            0.0,
            destination));
        CollectionAssert.AreEqual(new double[16], destination);

        Assert.IsTrue(EntityMirrorProjection.IsUsableNormalLength(1.0));
        Assert.IsFalse(EntityMirrorProjection.IsUsableNormalLength(0.0));
        Assert.IsFalse(EntityMirrorProjection.IsUsableNormalLength(double.PositiveInfinity));
        Assert.IsTrue(EntityMirrorProjection.IsUsableClipDenominator(-1.0));
        Assert.IsFalse(EntityMirrorProjection.IsUsableClipDenominator(0.0));
        Assert.IsFalse(EntityMirrorProjection.IsUsableClipDenominator(double.NaN));
        Assert.IsTrue(EntityMirrorProjection.AreFiniteClipCoefficients(1.0, 2.0, 3.0, 4.0));
        Assert.IsFalse(EntityMirrorProjection.AreFiniteClipCoefficients(double.NaN, 2.0, 3.0, 4.0));
        Assert.IsFalse(EntityMirrorProjection.AreFiniteClipCoefficients(1.0, double.NaN, 3.0, 4.0));
        Assert.IsFalse(EntityMirrorProjection.AreFiniteClipCoefficients(1.0, 2.0, double.NaN, 4.0));
        Assert.IsFalse(EntityMirrorProjection.AreFiniteClipCoefficients(1.0, 2.0, 3.0, double.NaN));

        MethodInfo buildCore = PrivateMethod(
            typeof(EntityMirrorProjection),
            "BuildObliqueMirrorProjection",
            parameterCount: 7,
            isStatic: true);
        double[] nonFiniteProjection = Identity();
        nonFiniteProjection[0] = double.NaN;
        double[] nonFiniteView = Identity();
        nonFiniteView[15] = double.PositiveInfinity;
        object?[][] invalidCoreInputs =
        [
            [new double[15], Identity(), 0.0, 0.0, new double[16], new double[16], new double[16]],
            [Identity(), new double[15], 0.0, 0.0, new double[16], new double[16], new double[16]],
            [Identity(), Identity(), 0.0, 0.0, new double[15], new double[16], new double[16]],
            [Identity(), Identity(), 0.0, 0.0, new double[16], new double[15], new double[16]],
            [Identity(), Identity(), 0.0, 0.0, new double[16], new double[16], new double[15]],
            [Identity(), Identity(), double.NaN, 0.0, new double[16], new double[16], new double[16]],
            [Identity(), Identity(), 0.0, double.NaN, new double[16], new double[16], new double[16]],
            [Identity(), Identity(), 0.0, -1.0, new double[16], new double[16], new double[16]],
            [nonFiniteProjection, Identity(), 0.0, 0.0, new double[16], new double[16], new double[16]],
            [Identity(), nonFiniteView, 0.0, 0.0, new double[16], new double[16], new double[16]]
        ];
        foreach (object?[] inputs in invalidCoreInputs)
        {
            Assert.AreEqual(false, buildCore.Invoke(null, inputs));
        }

        double[] overflowingProjection = Identity();
        overflowingProjection[15] = -double.MaxValue;
        double[] largePlaneView = Identity();
        largePlaneView[13] = -1.0e298;
        Array.Fill(destination, 4.0);
        Assert.IsFalse(EntityMirrorProjection.BuildObliqueMirrorProjection(
            overflowingProjection,
            largePlaneView,
            0.0,
            0.0,
            destination));
        CollectionAssert.AreEqual(new double[16], destination);

        double[] finalRowOverflow = Identity();
        finalRowOverflow[3] = -double.MaxValue;
        Array.Fill(destination, 4.0);
        Assert.IsFalse(EntityMirrorProjection.TryWriteObliqueProjection(
            finalRowOverflow,
            double.MaxValue,
            0.0,
            0.0,
            0.0,
            destination));
        CollectionAssert.AreEqual(new double[16], destination);
    }

    /// <summary>
    /// Covers official-type discovery failure plus deterministic Harmony installation, notification,
    /// rejected abstract-target rollback, and null validation through the resolved-type seam.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void GeometryReplayInstallationPatchesResolvedTypesAndRollsBackRejectedTargets()
    {
        List<string> logs = [];
        double[] identity = Identity();
        ICoreClientAPI api = CreateReplayApi(
            identity,
            ToFloat(identity),
            Perspective(),
            ToFloat(Perspective()),
            Stack(Perspective()),
            logs,
            throwOnModelViewMode: false);
        Assert.ThrowsException<ArgumentNullException>(() =>
            EntityMirrorGeometryReplayPatch.Install(
                null!,
                typeof(ReplayInstallFixture),
                typeof(ReplayInstallFixture)));

        Assert.IsFalse(EntityMirrorGeometryReplayPatch.Install(api));
        Assert.IsTrue(logs.Any(static entry => entry.StartsWith("Warning:", StringComparison.Ordinal)));

        logs.Clear();
        Assert.IsTrue(EntityMirrorGeometryReplayPatch.Install(
            api,
            typeof(ReplayInstallFixture),
            typeof(ReplayInstallFixture)));
        Assert.IsTrue(logs.Any(static entry => entry.StartsWith("Notification:", StringComparison.Ordinal)));
        EntityMirrorGeometryReplayPatch.Uninstall();

        logs.Clear();
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.Install(
            api,
            typeof(RejectedReplayInstallFixture),
            typeof(RejectedReplayInstallFixture)));
        Assert.IsTrue(logs.Any(static entry => entry.StartsWith("Error:", StringComparison.Ordinal)));
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(
            Identity(),
            Perspective()));
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
            GL.LoadBindings(new GLFWBindingsContext());
            assertions();
        }
        finally
        {
            threadGuard.SetValue(null, previousGuard);
        }
    }

    /// <summary>Creates a complete projection API using file-backed entity-mirror shaders.</summary>
    /// <param name="cameraMatrix">Engine camera matrix returned to the reflected replay.</param>
    /// <param name="perspective">Engine perspective projection.</param>
    /// <param name="logs">Captured logger messages.</param>
    /// <param name="compile">Shader-program compilation result.</param>
    /// <param name="passId">Memory shader registration result.</param>
    /// <param name="loadError">Whether the program reports a driver load error.</param>
    /// <param name="disposed">Whether the shader registry returns an unusable disposed program.</param>
    /// <param name="glslSupported">Whether the simulated driver exposes GLSL 3.30.</param>
    /// <returns>Deterministic API proxy accepted by the production projector.</returns>
    private static ICoreClientAPI CreateProjectionApi(
        double[] cameraMatrix,
        double[] perspective,
        List<string> logs,
        bool compile = true,
        int passId = 1,
        bool loadError = false,
        bool disposed = false,
        bool glslSupported = true)
    {
        string root = TestPaths.FindRepositoryRoot();
        string vertexSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "shaders",
            "entitymirror.vert"));
        string fragmentSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "shaders",
            "entitymirror.frag"));
        IShader vertex = ShaderStage();
        IShader fragment = ShaderStage();
        IShaderProgram program = RuntimeCoverageDispatchProxy.Create<IShaderProgram>(
            (method, _) => method.Name switch
            {
                "get_VertexShader" => vertex,
                "get_FragmentShader" => fragment,
                "Compile" => compile,
                "get_LoadError" => loadError,
                "get_Disposed" => disposed,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        IShaderAPI shaderApi = RuntimeCoverageDispatchProxy.Create<IShaderAPI>(
            (method, arguments) => method.Name switch
            {
                "IsGLSLVersionSupported" => glslSupported,
                "NewShaderProgram" => program,
                "NewShader" => arguments![0] is EnumShaderType.VertexShader ? vertex : fragment,
                "RegisterMemoryShaderProgram" => passId,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        IAssetManager assets = RuntimeCoverageDispatchProxy.Create<IAssetManager>(
            (method, arguments) => method.Name == "TryGet"
                ? TextAsset(arguments![0]!.ToString() == EntityMirrorShaderSource.VertexAssetCode
                    ? vertexSource
                    : fragmentSource)
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>(
            (method, _) => method.Name switch
            {
                "get_CameraMatrixOrigin" => cameraMatrix,
                "get_PerspectiveProjectionMat" => perspective,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        ILogger logger = Logger(logs);
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>(
            (method, _) => method.Name switch
            {
                "get_Render" => render,
                "get_Shader" => shaderApi,
                "get_Assets" => assets,
                "get_Logger" => logger,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
    }

    /// <summary>Creates an API proxy exposing every mutable replay carrier and GL matrix callback.</summary>
    /// <param name="camera">Mutable double-precision view.</param>
    /// <param name="cameraFloat">Mutable single-precision view.</param>
    /// <param name="projection">Mutable double-precision projection.</param>
    /// <param name="currentProjection">Mutable single-precision projection.</param>
    /// <param name="stack">Mutable projection stack.</param>
    /// <param name="logs">Captured replay diagnostics.</param>
    /// <param name="throwOnModelViewMode">Whether the first model-view setup callback must fail.</param>
    /// <returns>Deterministic replay API.</returns>
    private static ICoreClientAPI CreateReplayApi(
        double[] camera,
        float[] cameraFloat,
        double[] projection,
        float[] currentProjection,
        StackMatrix4 stack,
        List<string> logs,
        bool throwOnModelViewMode)
    {
        IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) =>
        {
            if (throwOnModelViewMode && method.Name == "GlMatrixModeModelView")
            {
                throw new InvalidOperationException("Synthetic model-view setup failure.");
            }

            return method.Name switch
            {
                "get_CameraMatrixOrigin" => camera,
                "get_CameraMatrixOriginf" => cameraFloat,
                "get_PerspectiveProjectionMat" => projection,
                "get_CurrentProjectionMatrix" => currentProjection,
                "get_PMatrix" => stack,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            };
        });
        ILogger logger = Logger(logs);
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Render" => render,
            "get_Logger" => logger,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Creates a logger proxy that records all production severity calls.</summary>
    /// <param name="logs">Destination diagnostic list.</param>
    /// <returns>Logger proxy suitable for projection and replay assertions.</returns>
    private static ILogger Logger(List<string> logs) =>
        RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Warning)
                or nameof(ILogger.Error)
                or nameof(ILogger.Notification))
            {
                logs.Add($"{method.Name}:{arguments?[0]}");
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

    /// <summary>Creates a writable no-op shader-stage proxy.</summary>
    /// <returns>Shader stage accepted by the production shader registry.</returns>
    private static IShader ShaderStage() =>
        RuntimeCoverageDispatchProxy.Create<IShader>(
            (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

    /// <summary>Creates one deterministic text asset.</summary>
    /// <param name="source">Text returned from the asset.</param>
    /// <returns>Asset proxy containing the supplied GLSL source.</returns>
    private static IAsset TextAsset(string source) =>
        RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "ToText"
                ? source
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

    /// <summary>Installs deterministic callbacks and carriers directly into the replay state.</summary>
    /// <param name="api">Replay API.</param>
    /// <param name="entityPass">Entity callback.</param>
    /// <param name="terrainBefore">Terrain recull callback.</param>
    /// <param name="terrainPass">Terrain draw callback.</param>
    private static void SetReplayState(
        ICoreClientAPI api,
        MethodInfo entityPass,
        MethodInfo terrainBefore,
        MethodInfo terrainPass)
    {
        SetStaticField("api", api);
        SetStaticField("opaqueEntityPass", entityPass);
        SetStaticField("terrainBeforePass", terrainBefore);
        SetStaticField("opaqueTerrainPass", terrainPass);
        SetStaticField("currentRenderSystem", new object());
        SetStaticField("currentTerrainRenderSystem", new object());
        SetStaticField("replayInProgress", false);
    }

    /// <summary>Sets one private replay field.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">New static value.</param>
    private static void SetStaticField(string name, object? value)
    {
        typeof(EntityMirrorGeometryReplayPatch).GetField(name, PrivateStatic)!.SetValue(null, value);
    }

    /// <summary>Gets one private replay field.</summary>
    /// <typeparam name="T">Expected field type.</typeparam>
    /// <param name="name">Field name.</param>
    /// <returns>Current static value.</returns>
    private static T GetStaticField<T>(string name) =>
        (T)typeof(EntityMirrorGeometryReplayPatch).GetField(name, PrivateStatic)!.GetValue(null)!;

    /// <summary>Finds one unambiguous private method by name and parameter count.</summary>
    /// <param name="type">Declaring type.</param>
    /// <param name="name">Method name.</param>
    /// <param name="parameterCount">Expected parameter count.</param>
    /// <param name="isStatic">Whether the method is static.</param>
    /// <returns>Matching private method.</returns>
    private static MethodInfo PrivateMethod(
        Type type,
        string name,
        int parameterCount,
        bool isStatic) =>
        type.GetMethods(isStatic ? PrivateStatic : PrivateInstance)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);

    /// <summary>Returns a one-entry projection stack.</summary>
    /// <param name="matrix">Initial top matrix.</param>
    /// <returns>Projection stack containing the matrix.</returns>
    private static StackMatrix4 Stack(double[] matrix)
    {
        StackMatrix4 stack = new(4);
        stack.Push(matrix);
        return stack;
    }

    /// <summary>Returns a conventional finite perspective projection.</summary>
    /// <returns>Column-major OpenGL perspective matrix.</returns>
    private static double[] Perspective()
    {
        const double near = 0.1;
        const double far = 128.0;
        double focal = 1.0 / Math.Tan(Math.PI / 6.0);
        return
        [
            focal, 0, 0, 0,
            0, focal, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, (2.0 * far * near) / (near - far), 0
        ];
    }

    /// <summary>Returns a column-major identity matrix.</summary>
    /// <returns>New sixteen-coefficient identity matrix.</returns>
    private static double[] Identity() =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    ];

    /// <summary>Converts a double matrix to its mutable float companion.</summary>
    /// <param name="values">Source matrix.</param>
    /// <returns>Single-precision copy.</returns>
    private static float[] ToFloat(double[] values) =>
        values.Select(static value => (float)value).ToArray();

    /// <summary>Clears benign driver errors produced by no-op shader program proxies.</summary>
    private static void DrainGlErrors()
    {
        while (GL.GetError() != OpenTK.Graphics.OpenGL4.ErrorCode.NoError)
        {
        }
    }

    /// <summary>Successful official-pass shaped callback.</summary>
    /// <param name="deltaTime">Unused captured engine delta.</param>
    private static void SuccessfulPass(float deltaTime) => _ = deltaTime;

    /// <summary>Official-pass shaped callback that exposes invocation failure restoration.</summary>
    /// <param name="deltaTime">Unused captured engine delta.</param>
    private static void ThrowingPass(float deltaTime)
    {
        _ = deltaTime;
        throw new InvalidOperationException("Synthetic official pass failure.");
    }

    /// <summary>Concrete engine-shaped callback owner used to prove successful Harmony installation.</summary>
    private sealed class ReplayInstallFixture
    {
        /// <summary>Entity opaque callback matching Vintage Story 1.22.</summary>
        /// <param name="deltaTime">Unused fixture frame duration.</param>
        public void OnRenderOpaque3D(float deltaTime) => _ = deltaTime;

        /// <summary>Terrain recull callback matching Vintage Story 1.22.</summary>
        /// <param name="deltaTime">Unused fixture frame duration.</param>
        public void OnRenderBefore(float deltaTime) => _ = deltaTime;

        /// <summary>Terrain opaque callback matching Vintage Story 1.22.</summary>
        /// <param name="deltaTime">Unused fixture frame duration.</param>
        public void OnRenderOpaque(float deltaTime) => _ = deltaTime;
    }

    /// <summary>Abstract callback owner that Harmony must reject and roll back cleanly.</summary>
    private abstract class RejectedReplayInstallFixture
    {
        /// <summary>Abstract entity callback rejected by Harmony.</summary>
        /// <param name="deltaTime">Fixture frame duration.</param>
        public abstract void OnRenderOpaque3D(float deltaTime);

        /// <summary>Abstract terrain recull callback.</summary>
        /// <param name="deltaTime">Fixture frame duration.</param>
        public abstract void OnRenderBefore(float deltaTime);

        /// <summary>Abstract terrain opaque callback.</summary>
        /// <param name="deltaTime">Fixture frame duration.</param>
        public abstract void OnRenderOpaque(float deltaTime);
    }
}
