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

namespace VintageRTX.Test;

/// <summary>
/// Exercises both production source-capture renderers on a real hidden OpenGL context. Engine
/// framebuffer metadata and shader identities are deterministic proxies; texture allocation,
/// exact-format image copies, state capture/restoration, and deletion use the actual driver.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CoverageSourceCaptureGpuTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>Clears deferred first-person state after every capture assertion.</summary>
    [TestCleanup]
    public void CleanupDeferredDraw()
    {
        FirstPersonReflectionCapturePatch.Uninstall();
    }

    /// <summary>
    /// Covers complete late-opaque allocation, all five compatible image copies, exact-source reuse,
    /// every size/source reallocation predicate, copy failure latching, diagnostics, and disposal.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void ReflectionSourceCopiesReallocatesDiagnosesAndDisposesRealTextures()
    {
        RunWithContext("VintageRTX.ReflectionSource.Coverage", () =>
        {
            List<int> sourceTextures = [];
            List<string> logs = [];
            CaptureFixture fixture = new(logs);
            fixture.SetPrimary(CreatePrimary(
                fixture.Width,
                fixture.Height,
                sourceTextures,
                includeStorage: true));
            ReflectionSourceCaptureRenderer renderer = new(fixture.Api);

            renderer.BeginCpuDiagnostics();
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsTrue(renderer.IsReady(fixture.Width, fixture.Height));
            Assert.IsTrue(GL.IsTexture(renderer.TextureId));
            Assert.IsTrue(GL.IsTexture(renderer.PositionTextureId));
            Assert.IsTrue(GL.IsTexture(renderer.DepthTextureId));
            int color = renderer.TextureId;
            int position = renderer.PositionTextureId;
            int depth = renderer.DepthTextureId;
            Assert.IsTrue(renderer.TryGetGBuffer(
                fixture.Width,
                fixture.Height,
                new GameGBuffer(501, 502, 503),
                out GameGBuffer clean));
            Assert.IsTrue(clean.GlowTextureId > 0);
            Assert.IsTrue(clean.NormalTextureId > 0);
            Assert.AreEqual(position, clean.PositionTextureId);

            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.AreEqual(color, renderer.TextureId);
            Assert.AreEqual(position, renderer.PositionTextureId);
            Assert.AreEqual(depth, renderer.DepthTextureId);

            fixture.Width++;
            fixture.Primary.Width = fixture.Width;
            ReplaceAllSources(fixture.Primary, sourceTextures);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);

            fixture.Height++;
            fixture.Primary.Height = fixture.Height;
            ReplaceAllSources(fixture.Primary, sourceTextures);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);

            for (int attachment = 0; attachment < 4; attachment++)
            {
                fixture.Primary.ColorTextureIds[attachment] = CreateColorTexture(
                    fixture.Width,
                    fixture.Height,
                    sourceTextures);
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            }

            fixture.Primary.DepthTextureId = CreateDepthTexture(
                fixture.Width,
                fixture.Height,
                sourceTextures);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            renderer.EndAndLogCpuDiagnostics();
            Assert.IsTrue(logs.Any(static entry => entry.StartsWith("Notification:", StringComparison.Ordinal)));

            Assert.ThrowsException<InvalidOperationException>(() =>
                ReflectionSourceCaptureRenderer.RequireSizedInternalFormat(0, 77));
            Assert.AreEqual(
                PixelInternalFormat.Rgba16f,
                ReflectionSourceCaptureRenderer.RequireSizedInternalFormat(
                    (int)PixelInternalFormat.Rgba16f,
                    77));

            fixture.ThrowFrameBuffers = true;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsFalse(renderer.IsReady(fixture.Width, fixture.Height));
            Assert.AreEqual(
                1,
                logs.Count(static entry => entry.StartsWith("Error:", StringComparison.Ordinal)));

            fixture.ThrowFrameBuffers = false;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsTrue(renderer.IsReady(fixture.Width, fixture.Height));

            fixture.Primary.ColorTextureIds = [fixture.Primary.ColorTextureIds[0]];
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.AreEqual(
                1,
                logs.Count(static entry => entry.StartsWith("Warning:", StringComparison.Ordinal)));

            fixture.Primary = CreatePrimary(
                fixture.Width,
                fixture.Height,
                sourceTextures,
                includeStorage: true);
            fixture.SetPrimary(fixture.Primary);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsTrue(renderer.IsReady(fixture.Width, fixture.Height));

            renderer.Dispose();
            renderer.Dispose();
            Assert.IsFalse(GL.IsTexture(color));
            Assert.IsFalse(GL.IsTexture(position));
            Assert.IsFalse(GL.IsTexture(depth));
            DeleteTextures(sourceTextures);
            DrainGlErrors();
        });
    }

    /// <summary>
    /// Covers pre-entity position allocation, exact-source reuse, source and dimension changes,
    /// missing-format failure latching, successful latch reset, and non-zero texture deletion.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void EntityMirrorSourceCopiesReusesFailsAndDisposesRealTexture()
    {
        RunWithContext("VintageRTX.EntityMirrorSource.Coverage", () =>
        {
            List<int> sourceTextures = [];
            List<string> logs = [];
            CaptureFixture fixture = new(logs);
            fixture.SetPrimary(CreatePrimary(
                fixture.Width,
                fixture.Height,
                sourceTextures,
                includeStorage: true));
            EntityMirrorSourceCaptureRenderer renderer = new(fixture.Api);

            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsTrue(renderer.IsReady(fixture.Width, fixture.Height));
            int owned = renderer.PositionTextureId;
            Assert.IsTrue(GL.IsTexture(owned));

            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.AreEqual(owned, renderer.PositionTextureId);

            fixture.Primary.ColorTextureIds[3] = CreateColorTexture(
                fixture.Width,
                fixture.Height,
                sourceTextures);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);

            fixture.Width++;
            fixture.Height++;
            fixture.Primary.Width = fixture.Width;
            fixture.Primary.Height = fixture.Height;
            ReplaceAllSources(fixture.Primary, sourceTextures);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsTrue(renderer.IsReady(fixture.Width, fixture.Height));

            Assert.ThrowsException<InvalidOperationException>(() =>
                EntityMirrorSourceCaptureRenderer.RequireSizedInternalFormat(0, 88));
            Assert.AreEqual(
                PixelInternalFormat.Rgba16f,
                EntityMirrorSourceCaptureRenderer.RequireSizedInternalFormat(
                    (int)PixelInternalFormat.Rgba16f,
                    88));

            fixture.ThrowFrameBuffers = true;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsFalse(renderer.IsReady(fixture.Width, fixture.Height));
            Assert.AreEqual(
                1,
                logs.Count(static entry => entry.StartsWith("Warning:", StringComparison.Ordinal)));

            fixture.ThrowFrameBuffers = false;
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.IsTrue(renderer.IsReady(fixture.Width, fixture.Height));

            renderer.Dispose();
            renderer.Dispose();
            Assert.IsFalse(GL.IsTexture(owned));
            DeleteTextures(sourceTextures);
            DrainGlErrors();
        });
    }

    /// <summary>
    /// Covers the private replay state snapshot and restoration with both enabled and disabled GL
    /// capabilities, then drives deferred draw replay with absent, preserved, and replaced shaders.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void DeferredReplayCapturesAndRestoresEveryGlCarrier()
    {
        RunWithContext("VintageRTX.ReflectionReplay.Coverage", () =>
        {
            Type stateType = typeof(ReflectionSourceCaptureRenderer).GetNestedType(
                "ReplayGlState",
                BindingFlags.NonPublic)!;
            MethodInfo capture = stateType.GetMethod(
                "Capture",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            MethodInfo restore = stateType.GetMethod(
                "Restore",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.CullFace);
            GL.DepthMask(true);
            object enabledState = capture.Invoke(null, null)!;
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.DepthMask(false);
            restore.Invoke(enabledState, null);
            Assert.IsTrue(GL.IsEnabled(EnableCap.DepthTest));
            Assert.IsFalse(GL.IsEnabled(EnableCap.Blend));
            Assert.IsTrue(GL.IsEnabled(EnableCap.CullFace));

            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            object disabledState = capture.Invoke(null, null)!;
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.CullFace);
            restore.Invoke(disabledState, null);
            Assert.IsFalse(GL.IsEnabled(EnableCap.DepthTest));
            Assert.IsTrue(GL.IsEnabled(EnableCap.Blend));
            Assert.IsFalse(GL.IsEnabled(EnableCap.CullFace));

            List<string> logs = [];
            CaptureFixture fixture = new(logs);
            ReflectionSourceCaptureRenderer renderer = new(fixture.Api);
            MethodInfo replay = PrivateMethod(
                typeof(ReflectionSourceCaptureRenderer),
                "ReplayDeferredFirstPersonDraw",
                parameterCount: 1);
            ReplayTarget target = new();
            MethodInfo draw = typeof(ReplayTarget).GetMethod(nameof(ReplayTarget.Draw))!;
            SetPatchField("capture", renderer);

            QueueDeferred(target, draw);
            replay.Invoke(renderer, [null]);
            Assert.AreEqual(1, target.DrawCount);

            int previousStops = 0;
            int previousUses = 0;
            int activeStops = 0;
            IShaderProgram previous = Shader(
                () => previousStops++,
                () => previousUses++);
            IShaderProgram replacement = Shader(
                () => activeStops++,
                static () => { });
            fixture.CurrentShader = previous;
            target.AfterDraw = () => fixture.CurrentShader = replacement;
            QueueDeferred(target, draw);
            replay.Invoke(renderer, [enabledState]);
            Assert.AreEqual(1, previousStops);
            Assert.AreEqual(1, previousUses);
            Assert.AreEqual(1, activeStops);

            fixture.CurrentShader = previous;
            target.AfterDraw = null;
            QueueDeferred(target, draw);
            replay.Invoke(renderer, [disabledState]);
            Assert.AreEqual(2, previousStops);
            Assert.AreEqual(2, previousUses);
            Assert.AreEqual(3, target.DrawCount);

            fixture.CurrentShader = null;
            QueueDeferred(target, draw);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
            Assert.AreEqual(4, target.DrawCount);
            DrainGlErrors();
        });
    }

    /// <summary>
    /// Covers null asset managers, independently missing and invalid vertex/fragment assets, and
    /// the successful ordered source pair returned to the mirror projection compiler.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void EntityMirrorShaderSourceRejectsEveryInvalidAssetCombination()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            EntityMirrorShaderSource.Load(null!));
        Assert.ThrowsException<FileNotFoundException>(() =>
            EntityMirrorShaderSource.Load(ShaderAssets(null, "#version 330 core\nfragment")));
        Assert.ThrowsException<FileNotFoundException>(() =>
            EntityMirrorShaderSource.Load(ShaderAssets("#version 330 core\nvertex", null)));
        Assert.ThrowsException<InvalidDataException>(() =>
            EntityMirrorShaderSource.Load(ShaderAssets("#version 450 core\nvertex", "#version 330 core\nfragment")));
        Assert.ThrowsException<InvalidDataException>(() =>
            EntityMirrorShaderSource.Load(ShaderAssets("#version 330 core\nvertex", "#version 450 core\nfragment")));

        DisplayShaderProgramSource source = EntityMirrorShaderSource.Load(ShaderAssets(
            "#version 330 core\nvertex",
            "#version 330 core\nfragment"));
        Assert.AreEqual("#version 330 core\nvertex", source.Vertex);
        Assert.AreEqual("#version 330 core\nfragment", source.Fragment);
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

    /// <summary>Creates a complete Primary framebuffer with four colour textures and depth.</summary>
    /// <param name="width">Texture width.</param>
    /// <param name="height">Texture height.</param>
    /// <param name="textures">Owned source names collected for cleanup.</param>
    /// <param name="includeStorage">Whether every texture receives sized storage.</param>
    /// <returns>Framebuffer metadata accepted by both source capture renderers.</returns>
    private static FrameBufferRef CreatePrimary(
        int width,
        int height,
        List<int> textures,
        bool includeStorage)
    {
        int[] colors = new int[4];
        for (int index = 0; index < colors.Length; index++)
        {
            colors[index] = includeStorage
                ? CreateColorTexture(width, height, textures)
                : CreateTextureName(textures);
        }

        return new FrameBufferRef
        {
            FboId = GL.GenFramebuffer(),
            Width = width,
            Height = height,
            ColorTextureIds = colors,
            DepthTextureId = includeStorage
                ? CreateDepthTexture(width, height, textures)
                : CreateTextureName(textures)
        };
    }

    /// <summary>Replaces all Primary source names with exact-size allocated textures.</summary>
    /// <param name="primary">Framebuffer metadata to mutate.</param>
    /// <param name="textures">Owned source names collected for cleanup.</param>
    private static void ReplaceAllSources(FrameBufferRef primary, List<int> textures)
    {
        for (int index = 0; index < primary.ColorTextureIds.Length; index++)
        {
            primary.ColorTextureIds[index] = CreateColorTexture(
                primary.Width,
                primary.Height,
                textures);
        }

        primary.DepthTextureId = CreateDepthTexture(primary.Width, primary.Height, textures);
    }

    /// <summary>Creates one RGBA16F source texture.</summary>
    /// <param name="width">Texture width.</param>
    /// <param name="height">Texture height.</param>
    /// <param name="textures">Owned name collection.</param>
    /// <returns>Allocated texture name.</returns>
    private static int CreateColorTexture(int width, int height, List<int> textures)
    {
        int texture = CreateTextureName(textures);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba16f,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            IntPtr.Zero);
        return texture;
    }

    /// <summary>Creates one depth24 source texture.</summary>
    /// <param name="width">Texture width.</param>
    /// <param name="height">Texture height.</param>
    /// <param name="textures">Owned name collection.</param>
    /// <returns>Allocated depth texture name.</returns>
    private static int CreateDepthTexture(int width, int height, List<int> textures)
    {
        int texture = CreateTextureName(textures);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.DepthComponent24,
            width,
            height,
            0,
            PixelFormat.DepthComponent,
            PixelType.Float,
            IntPtr.Zero);
        return texture;
    }

    /// <summary>Creates an unallocated OpenGL texture name and records ownership.</summary>
    /// <param name="textures">Owned name collection.</param>
    /// <returns>New texture name.</returns>
    private static int CreateTextureName(List<int> textures)
    {
        int texture = GL.GenTexture();
        textures.Add(texture);
        return texture;
    }

    /// <summary>Deletes all distinct source textures created by one assertion.</summary>
    /// <param name="textures">Owned source names.</param>
    private static void DeleteTextures(IEnumerable<int> textures)
    {
        foreach (int texture in textures.Distinct())
        {
            if (GL.IsTexture(texture))
            {
                GL.DeleteTexture(texture);
            }
        }
    }

    /// <summary>Queues one synthetic deferred official draw directly into the patch state.</summary>
    /// <param name="target">Draw receiver.</param>
    /// <param name="draw">Official-shaped draw method.</param>
    private static void QueueDeferred(ReplayTarget target, MethodInfo draw)
    {
        SetPatchField("deferredRenderer", target);
        SetPatchField("deferredDeltaTime", 0.25f);
        SetPatchField("opaqueDrawMethod", draw);
    }

    /// <summary>Sets one private first-person patch field.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">New static value.</param>
    private static void SetPatchField(string name, object? value)
    {
        typeof(FirstPersonReflectionCapturePatch).GetField(name, PrivateStatic)!.SetValue(null, value);
    }

    /// <summary>Creates a shader proxy with observable Stop and Use calls.</summary>
    /// <param name="stop">Stop callback.</param>
    /// <param name="use">Use callback.</param>
    /// <returns>Deterministic shader-program proxy.</returns>
    private static IShaderProgram Shader(Action stop, Action use) =>
        RuntimeCoverageDispatchProxy.Create<IShaderProgram>((method, _) =>
        {
            if (method.Name == "Stop")
            {
                stop();
            }
            else if (method.Name == "Use")
            {
                use();
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

    /// <summary>Creates an asset manager with independently configurable canonical shader files.</summary>
    /// <param name="vertex">Vertex source, or null when the canonical asset is absent.</param>
    /// <param name="fragment">Fragment source, or null when the canonical asset is absent.</param>
    /// <returns>Deterministic shader-asset manager.</returns>
    private static IAssetManager ShaderAssets(string? vertex, string? fragment) =>
        RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) =>
        {
            if (method.Name != "TryGet")
            {
                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            }

            string? source = arguments![0]!.ToString() == EntityMirrorShaderSource.VertexAssetCode
                ? vertex
                : fragment;
            return source is null ? null : TextAsset(source);
        });

    /// <summary>Creates one immutable text asset.</summary>
    /// <param name="source">Source returned by <see cref="IAsset.ToText"/>.</param>
    /// <returns>Text-backed asset proxy.</returns>
    private static IAsset TextAsset(string source) =>
        RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "ToText"
                ? source
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

    /// <summary>Finds one private instance method by exact name and parameter count.</summary>
    /// <param name="type">Declaring type.</param>
    /// <param name="name">Method name.</param>
    /// <param name="parameterCount">Expected parameter count.</param>
    /// <returns>Matching private method.</returns>
    private static MethodInfo PrivateMethod(Type type, string name, int parameterCount) =>
        type.GetMethods(PrivateInstance)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);

    /// <summary>Clears benign driver errors after intentionally program-less synthetic draws.</summary>
    private static void DrainGlErrors()
    {
        while (GL.GetError() != OpenTK.Graphics.OpenGL4.ErrorCode.NoError)
        {
        }
    }

    /// <summary>Mutable engine API fixture shared by both capture renderers.</summary>
    private sealed class CaptureFixture
    {
        private readonly List<FrameBufferRef> frameBuffers = [];

        /// <summary>Creates the API graph and a registry large enough for the Primary slot.</summary>
        /// <param name="logs">Destination diagnostics.</param>
        internal CaptureFixture(List<string> logs)
        {
            for (int index = 0; index <= (int)EnumFrameBuffer.Primary; index++)
            {
                frameBuffers.Add(new FrameBufferRef());
            }

            ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
            {
                if (method.Name is nameof(ILogger.Warning)
                    or nameof(ILogger.Error)
                    or nameof(ILogger.Notification))
                {
                    logs.Add($"{method.Name}:{arguments?[0]}");
                }

                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            });
            IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) =>
                method.Name switch
                {
                    "get_FrameWidth" => Width,
                    "get_FrameHeight" => Height,
                    "get_FrameBuffers" => ThrowFrameBuffers
                        ? throw new InvalidOperationException("Synthetic framebuffer registry failure.")
                        : frameBuffers,
                    "get_CurrentActiveShader" => CurrentShader,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
            Api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
                method.Name switch
                {
                    "get_Render" => render,
                    "get_Logger" => logger,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
        }

        /// <summary>Gets the deterministic client API.</summary>
        internal ICoreClientAPI Api { get; }

        /// <summary>Gets or sets the current frame width.</summary>
        internal int Width { get; set; } = 4;

        /// <summary>Gets or sets the current frame height.</summary>
        internal int Height { get; set; } = 3;

        /// <summary>Gets or sets the active shader observed by deferred replay.</summary>
        internal IShaderProgram? CurrentShader { get; set; }

        /// <summary>Gets or sets whether framebuffer registry access must fail.</summary>
        internal bool ThrowFrameBuffers { get; set; }

        /// <summary>Gets or sets the Primary framebuffer metadata.</summary>
        internal FrameBufferRef Primary { get; set; } = new();

        /// <summary>Places one framebuffer in the engine-defined Primary registry slot.</summary>
        /// <param name="primary">Framebuffer metadata.</param>
        internal void SetPrimary(FrameBufferRef primary)
        {
            Primary = primary;
            frameBuffers[(int)EnumFrameBuffer.Primary] = primary;
        }
    }

    /// <summary>Synthetic official draw receiver used by deferred replay.</summary>
    private sealed class ReplayTarget
    {
        /// <summary>Gets the number of replayed official draws.</summary>
        internal int DrawCount { get; private set; }

        /// <summary>Gets or sets a callback that can replace the active shader during the draw.</summary>
        internal Action? AfterDraw { get; set; }

        /// <summary>Matches the official first-person opaque callback signature.</summary>
        /// <param name="deltaTime">Unused captured frame duration.</param>
        /// <param name="isShadowPass">Unused direct-view shadow flag.</param>
        public void Draw(float deltaTime, bool isShadowPass)
        {
            _ = deltaTime;
            _ = isShadowPass;
            DrawCount++;
            AfterDraw?.Invoke();
        }
    }
}
