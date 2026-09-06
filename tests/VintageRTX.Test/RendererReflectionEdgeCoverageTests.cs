using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Test;

/// <summary>
/// Exercises renderer and reflection control-flow that is independent of a live OpenGL context.
/// The cases deliberately stop before driver calls while validating every fail-closed boundary.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RendererReflectionEdgeCoverageTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>Clears the static official-replay state after every edge-case test.</summary>
    [TestCleanup]
    public void CleanupReplayPatch()
    {
        EntityMirrorGeometryReplayPatch.Uninstall();
    }

    /// <summary>
    /// Covers the pre-entity source's complete non-GL lifecycle, exact-size readiness predicate,
    /// disabled path, invalid dimensions, missing Primary layout, and one-shot warning latch.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void EntityMirrorSourceFailsClosedWithoutTouchingGl()
    {
        Assert.ThrowsException<ArgumentNullException>(
            static () => new EntityMirrorSourceCaptureRenderer(null!));

        List<string> logs = [];
        EntityMirrorSourceCaptureRenderer renderer = new(CreateApi(1, 1, [], logs));
        Assert.AreEqual(0.39, renderer.RenderOrder, 0.0);
        Assert.AreEqual(0, renderer.RenderRange);
        Assert.IsTrue(renderer.Enabled);
        Assert.AreEqual(0, renderer.PositionTextureId);
        Assert.IsFalse(renderer.IsReady(1, 1));

        renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterBlit);
        renderer.Enabled = false;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
        Assert.IsFalse(renderer.IsReady(1, 1));

        renderer.Enabled = true;
        renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
        renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
        Assert.AreEqual(1, logs.Count(static entry => entry.StartsWith("Warning:", StringComparison.Ordinal)));

        SetField(renderer, "ready", true);
        SetField(renderer, "positionTextureId", 41);
        SetField(renderer, "width", 1);
        SetField(renderer, "height", 1);
        Assert.IsTrue(renderer.IsReady(1, 1));
        Assert.IsFalse(renderer.IsReady(2, 1));
        Assert.IsFalse(renderer.IsReady(1, 2));
        SetField(renderer, "positionTextureId", 0);
        Assert.IsFalse(renderer.IsReady(1, 1));
        SetField(renderer, "positionTextureId", 41);
        SetField(renderer, "ready", false);
        Assert.IsFalse(renderer.IsReady(1, 1));

        SetField(renderer, "positionTextureId", 0);
        renderer.Dispose();
        Assert.AreEqual(0, GetField<int>(renderer, "width"));
        Assert.AreEqual(0, GetField<int>(renderer, "height"));

        EntityMirrorSourceCaptureRenderer zeroWidth = new(CreateApi(0, 1, [], []));
        zeroWidth.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
        EntityMirrorSourceCaptureRenderer zeroHeight = new(CreateApi(1, 0, [], []));
        zeroHeight.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
    }

    /// <summary>
    /// Covers owned reflection snapshot properties, readiness short-circuiting, diagnostic
    /// accumulation, invalid-frame paths, and zero-name disposal without entering OpenGL.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ReflectionSourceCoversCpuAndInvalidFrameBoundaries()
    {
        List<string> logs = [];
        ICoreClientAPI api = CreateApi(2, 2, [], logs);
        ReflectionSourceCaptureRenderer renderer = new(api);
        Assert.AreEqual(0.79, renderer.RenderOrder, 0.0);
        Assert.AreEqual(0, renderer.RenderRange);
        Assert.AreSame(api, renderer.Api);
        Assert.IsTrue(renderer.Enabled);
        Assert.AreEqual(0, renderer.TextureId);
        Assert.AreEqual(0, renderer.PositionTextureId);
        Assert.AreEqual(0, renderer.DepthTextureId);

        renderer.OnRenderFrame(0.0f, EnumRenderStage.AfterBlit);
        renderer.Enabled = false;
        renderer.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
        renderer.Enabled = true;
        renderer.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
        renderer.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
        Assert.AreEqual(1, logs.Count(static entry => entry.StartsWith("Warning:", StringComparison.Ordinal)));

        SetSnapshotFields(renderer, 10, 20);
        Assert.IsTrue(renderer.IsReady(10, 20));
        Assert.IsFalse(renderer.IsReady(11, 20));
        Assert.IsFalse(renderer.IsReady(10, 21));
        foreach (string textureField in new[]
                 {
                     "colorTextureId",
                     "glowTextureId",
                     "normalTextureId",
                     "positionTextureId",
                     "depthTextureId"
                 })
        {
            SetField(renderer, textureField, 0);
            Assert.IsFalse(renderer.IsReady(10, 20), textureField);
            SetField(renderer, textureField, 1);
        }
        SetField(renderer, "ready", false);
        Assert.IsFalse(renderer.IsReady(10, 20));

        renderer.BeginCpuDiagnostics();
        renderer.EndAndLogCpuDiagnostics();
        SetField(renderer, "cpuDiagnosticFrames", 2);
        SetField(renderer, "snapshotDiagnosticTicks", Stopwatch.Frequency / 100);
        SetField(renderer, "replayDiagnosticTicks", Stopwatch.Frequency / 200);
        SetField(renderer, "maximumDiagnosticFrameTicks", Stopwatch.Frequency / 50);
        renderer.EndAndLogCpuDiagnostics();
        Assert.IsTrue(logs.Any(static entry => entry.StartsWith("Notification:", StringComparison.Ordinal)));

        MethodInfo replay = Method(typeof(ReflectionSourceCaptureRenderer), "ReplayDeferredFirstPersonDraw", 1);
        replay.Invoke(renderer, [null]);
        MethodInfo ensureName = Method(typeof(ReflectionSourceCaptureRenderer), "EnsureTextureName", 1, isStatic: true);
        Assert.AreEqual(77, ensureName.Invoke(null, [77]));

        foreach (string textureField in new[]
                 {
                     "colorTextureId",
                     "glowTextureId",
                     "normalTextureId",
                     "positionTextureId",
                     "depthTextureId"
                 })
        {
            SetField(renderer, textureField, 0);
        }
        renderer.Dispose();

        ReflectionSourceCaptureRenderer zeroWidth = new(CreateApi(0, 2, [], []));
        zeroWidth.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
        ReflectionSourceCaptureRenderer zeroHeight = new(CreateApi(2, 0, [], []));
        zeroHeight.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
    }

    /// <summary>
    /// Covers every allocation predicate component and the matrix input guards, then disposes an
    /// all-zero projector twice to validate the idempotent non-GL cleanup path.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void EntityMirrorProjectionCoversPropertiesAndMatrixGuards()
    {
        Assert.ThrowsException<ArgumentNullException>(
            static () => new EntityMirrorProjection(null!));
        EntityMirrorProjection projection = new(CreateApi(1, 1, [], []));
        string[] names =
        [
            "colorTextureId",
            "depthTextureId",
            "framebufferId",
            "entityEvidenceColorTextureId",
            "entityEvidenceDepthTextureId",
            "entityEvidenceFramebufferId",
            "vertexArrayId",
            "width",
            "height"
        ];
        foreach (string name in names)
        {
            SetField(projection, name, 1);
        }

        Assert.AreEqual(1, projection.ColorTextureId);
        Assert.AreEqual(1, projection.DepthTextureId);
        Assert.AreEqual(1, projection.FramebufferId);
        Assert.AreEqual(1, projection.EntityEvidenceFramebufferId);
        Assert.AreEqual(1, projection.Width);
        Assert.AreEqual(1, projection.Height);
        Assert.IsTrue(projection.IsAllocated);
        foreach (string name in names)
        {
            SetField(projection, name, 0);
            Assert.IsFalse(projection.IsAllocated, name);
            SetField(projection, name, 1);
        }
        SetField(projection, "disposed", true);
        Assert.IsFalse(projection.IsAllocated);
        SetField(projection, "disposed", false);

        double[] identity = Identity();
        Assert.ThrowsException<ArgumentException>(() =>
            EntityMirrorProjection.BuildReflectedViewMatrix(new double[15], 0.0, new double[16]));
        Assert.ThrowsException<ArgumentException>(() =>
            EntityMirrorProjection.BuildReflectedViewMatrix(identity, 0.0, new double[15]));
        double[] reflected = new double[16];
        EntityMirrorProjection.BuildReflectedViewMatrix(identity, 2.0, reflected);
        Assert.AreEqual(-1.0, reflected[5], 0.0);
        Assert.AreEqual(4.0, reflected[13], 0.0);

        foreach (string name in names)
        {
            SetField(projection, name, 0);
        }
        projection.Dispose();
        projection.Dispose();
        Assert.IsFalse(projection.IsAllocated);
    }

    /// <summary>
    /// Forces three distinct oblique-projection rejection points: a zero transformed normal, a
    /// zero/non-finite Lengyel denominator, and an otherwise valid plane whose W clip overflows.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ObliqueProjectionRejectsDegenerateFiniteMatricesAtEachStage()
    {
        double[] destination = Enumerable.Repeat(9.0, 16).ToArray();
        double[] zeroNormalView =
        [
            1, 0, 0, 0,
            0, 0, 0, 1,
            0, 0, 1, 0,
            0, 1, 0, 0
        ];
        Assert.IsFalse(EntityMirrorProjection.BuildObliqueMirrorProjection(
            Identity(), zeroNormalView, 2.0, 0.0, destination));
        CollectionAssert.AreEqual(new double[16], destination);

        Array.Fill(destination, 9.0);
        Assert.IsFalse(EntityMirrorProjection.BuildObliqueMirrorProjection(
            Identity(), Identity(), 1.0, 0.0, destination));
        CollectionAssert.AreEqual(new double[16], destination);

        double[] infiniteDenominatorProjection = Identity();
        infiniteDenominatorProjection[5] = 1.0e-308;
        infiniteDenominatorProjection[15] = -1.0e-308;
        Array.Fill(destination, 9.0);
        Assert.IsFalse(EntityMirrorProjection.BuildObliqueMirrorProjection(
            infiniteDenominatorProjection, Identity(), 1.0, 0.0, destination));
        CollectionAssert.AreEqual(new double[16], destination);

        double[] hugeTranslationView = Identity();
        hugeTranslationView[13] = 1.0e308;
        double[] overflowingClipProjection = Identity();
        overflowingClipProjection[5] = 0.5;
        overflowingClipProjection[15] = 1.0e308;
        Array.Fill(destination, 9.0);
        Assert.IsFalse(EntityMirrorProjection.BuildObliqueMirrorProjection(
            overflowingClipProjection, hugeTranslationView, 0.0, 0.0, destination));
        CollectionAssert.AreEqual(new double[16], destination);
    }

    /// <summary>
    /// Drives all pre-OpenGL official replay gates, including every missing callback/carrier and
    /// malformed mutable camera array, while keeping the dummy pass unreachable.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void GeometryReplayRejectsIncompleteStateBeforeGl()
    {
        Assert.ThrowsException<ArgumentNullException>(
            static () => EntityMirrorGeometryReplayPatch.Install(null!));
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplay(new double[16], new double[16]));
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(new double[16], new double[16]));

        MethodInfo sentinel = typeof(RendererReflectionEdgeCoverageTests).GetMethod(
            nameof(ReplaySentinel),
            PrivateStatic)
            ?? throw new MissingMethodException(nameof(ReplaySentinel));
        double[] matrix = Identity();
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "api", CreateReplayApi(matrix, ToFloat(matrix), matrix, ToFloat(matrix), Stack(matrix)));
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "opaqueEntityPass", sentinel);
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "currentRenderSystem", new object());

        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "replayInProgress", true);
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(matrix, matrix));
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "replayInProgress", false);

        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "opaqueEntityPass", null);
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(matrix, matrix));
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "opaqueEntityPass", sentinel);
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "currentRenderSystem", null);
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(matrix, matrix));
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "currentRenderSystem", new object());

        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "terrainBeforePass", null);
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplay(matrix, matrix));
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "terrainBeforePass", sentinel);
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "opaqueTerrainPass", null);
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplay(matrix, matrix));
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "opaqueTerrainPass", sentinel);
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "currentTerrainRenderSystem", null);
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplay(matrix, matrix));
        SetStaticField(typeof(EntityMirrorGeometryReplayPatch), "currentTerrainRenderSystem", new object());

        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(new double[15], matrix));
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(matrix, new double[15]));

        AssertReplayCarrierRejected(new double[15], ToFloat(matrix), matrix, ToFloat(matrix), Stack(matrix), matrix);
        AssertReplayCarrierRejected(matrix, new float[15], matrix, ToFloat(matrix), Stack(matrix), matrix);
        AssertReplayCarrierRejected(matrix, ToFloat(matrix), new double[15], ToFloat(matrix), Stack(matrix), matrix);
        AssertReplayCarrierRejected(matrix, ToFloat(matrix), matrix, new float[15], Stack(matrix), matrix);
        AssertReplayCarrierRejected(matrix, ToFloat(matrix), matrix, ToFloat(matrix), new StackMatrix4(4), matrix);
        double[] nonFiniteProjection = Identity();
        nonFiniteProjection[15] = double.NaN;
        AssertReplayCarrierRejected(matrix, ToFloat(matrix), matrix, ToFloat(matrix), Stack(matrix), nonFiniteProjection);
    }

    /// <summary>
    /// Covers exact camera-carrier restoration validation, projection-stack argument branches,
    /// finite-matrix scanning, and the private matrix application success/failure paths.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void GeometryReplayMatrixHelpersCoverEveryPureBranch()
    {
        double[] doubles = Identity();
        float[] floats = ToFloat(doubles);
        StackMatrix4 stack = Stack(doubles);
        Assert.ThrowsException<ArgumentNullException>(() =>
            EntityMirrorGeometryReplayPatch.RestoreProjectionStack(null!, 0, false, doubles));
        Assert.ThrowsException<ArgumentNullException>(() =>
            EntityMirrorGeometryReplayPatch.RestoreProjectionStack(stack, 0, false, null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            EntityMirrorGeometryReplayPatch.RestoreProjectionStack(stack, -1, false, doubles));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            EntityMirrorGeometryReplayPatch.RestoreProjectionStack(stack, 0, false, new double[15]));

        object?[][] invalidCarriers =
        [
            [new double[15], doubles, floats, floats, doubles, doubles, floats, floats],
            [doubles, new double[15], floats, floats, doubles, doubles, floats, floats],
            [doubles, doubles, new float[15], floats, doubles, doubles, floats, floats],
            [doubles, doubles, floats, new float[15], doubles, doubles, floats, floats],
            [doubles, doubles, floats, floats, new double[15], doubles, floats, floats],
            [doubles, doubles, floats, floats, doubles, new double[15], floats, floats],
            [doubles, doubles, floats, floats, doubles, doubles, new float[15], floats],
            [doubles, doubles, floats, floats, doubles, doubles, floats, new float[15]]
        ];
        foreach (object?[] values in invalidCarriers)
        {
            Assert.ThrowsException<ArgumentException>(() =>
                EntityMirrorGeometryReplayPatch.RestoreMutableCameraCarriers(
                    (double[])values[0]!,
                    (double[])values[1]!,
                    (float[])values[2]!,
                    (float[])values[3]!,
                    (double[])values[4]!,
                    (double[])values[5]!,
                    (float[])values[6]!,
                    (float[])values[7]!,
                    Stack(doubles),
                    1,
                    doubles));
        }

        MethodInfo finite = Method(typeof(EntityMirrorGeometryReplayPatch), "IsFiniteMatrix", 1, isStatic: true);
        Assert.AreEqual(true, finite.Invoke(null, [Identity()]));
        double[] nonFinite = Identity();
        nonFinite[0] = double.PositiveInfinity;
        Assert.AreEqual(false, finite.Invoke(null, [nonFinite]));
        nonFinite = Identity();
        nonFinite[15] = double.NaN;
        Assert.AreEqual(false, finite.Invoke(null, [nonFinite]));

        double[] camera = new double[16];
        float[] cameraFloat = new float[16];
        double[] perspective = new double[16];
        float[] current = new float[16];
        StackMatrix4 projectionStack = Stack(Identity());
        ICoreClientAPI api = CreateReplayApi(camera, cameraFloat, perspective, current, projectionStack);
        MethodInfo apply = Method(typeof(EntityMirrorGeometryReplayPatch), "ApplyMirrorMatrices", 4, isStatic: true);
        apply.Invoke(null, [api, Identity(), Identity(), 1]);
        CollectionAssert.AreEqual(Identity(), camera);
        CollectionAssert.AreEqual(ToFloat(Identity()), cameraFloat);

        TargetInvocationException depthFailure = Assert.ThrowsException<TargetInvocationException>(() =>
            apply.Invoke(null, [api, Identity(), Identity(), 2]));
        Assert.IsInstanceOfType<InvalidOperationException>(depthFailure.InnerException);
        double[] invalidView = Identity();
        invalidView[4] = double.NaN;
        TargetInvocationException viewFailure = Assert.ThrowsException<TargetInvocationException>(() =>
            apply.Invoke(null, [api, invalidView, Identity(), 1]));
        Assert.IsInstanceOfType<InvalidOperationException>(viewFailure.InnerException);
        double[] invalidProjection = Identity();
        invalidProjection[4] = double.NaN;
        TargetInvocationException projectionFailure = Assert.ThrowsException<TargetInvocationException>(() =>
            apply.Invoke(null, [api, Identity(), invalidProjection, 1]));
        Assert.IsInstanceOfType<InvalidOperationException>(projectionFailure.InnerException);
    }

    /// <summary>
    /// Covers the remaining pure Filmic display branches and materializes private GL-state value
    /// objects solely to execute their generated property accessors without invoking the driver.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void FilmicPureEdgesAndPrivateStateAccessorsAreCovered()
    {
        LiquidSurfaceSubgridImpactDiagnostic inactive = default;
        Assert.IsFalse(FilmicDisplayRenderer.IsNewSubgridImpact(in inactive, in inactive));
        Assert.ThrowsException<ArgumentException>(() =>
            FilmicDisplayRenderer.SelectSubgridImpactSlot([], [], 0));
        Assert.ThrowsException<ArgumentException>(() =>
            FilmicDisplayRenderer.SelectSubgridImpactSlot(
                new LiquidSurfaceSubgridImpactDiagnostic[1],
                [],
                0));

        MethodInfo readTicks = Method(typeof(FilmicDisplayRenderer), "ReadStageTicks", 2, isStatic: true);
        object?[] disabledArguments = [false, 123L];
        Assert.AreEqual(0L, readTicks.Invoke(null, disabledArguments));
        Assert.AreEqual(123L, disabledArguments[1]);
        object?[] enabledArguments = [true, Stopwatch.GetTimestamp()];
        Assert.IsInstanceOfType<long>(readTicks.Invoke(null, enabledArguments));
        Assert.IsTrue((long)enabledArguments[1]! > 0L);

        MethodInfo milliseconds = Method(
            typeof(FilmicDisplayRenderer),
            "StopwatchTicksToMilliseconds",
            1,
            isStatic: true);
        Assert.AreEqual(1000.0, (double)milliseconds.Invoke(null, [Stopwatch.Frequency])!, 1.0e-9);

        FilmicDisplayRenderer renderer = (FilmicDisplayRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(FilmicDisplayRenderer));
        renderer.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
        SetField(renderer, "benchmarkCpuStageTicks", new long[6]);
        SetField(renderer, "benchmarkCpuStageMaximumTicks", new long[6]);
        MethodInfo record = Method(typeof(FilmicDisplayRenderer), "RecordBenchmarkCpuDiagnostics", 6);
        record.Invoke(renderer, [1L, 2L, 3L, 4L, 5L, 6L]);
        record.Invoke(renderer, [6L, 5L, 4L, 3L, 2L, 1L]);
        CollectionAssert.AreEqual(new long[] { 7, 7, 7, 7, 7, 7 }, GetField<long[]>(renderer, "benchmarkCpuStageTicks"));
        CollectionAssert.AreEqual(new long[] { 6, 5, 4, 4, 5, 6 }, GetField<long[]>(renderer, "benchmarkCpuStageMaximumTicks"));
        Assert.AreEqual(2, GetField<int>(renderer, "benchmarkCpuDiagnosticFrames"));
        Assert.AreEqual(21L, GetField<long>(renderer, "benchmarkCpuMaximumFrameTicks"));

        Method(typeof(FilmicDisplayRenderer), "ResetBenchmarkCpuDiagnostics", 0).Invoke(renderer, null);
        Assert.AreEqual(0, GetField<int>(renderer, "benchmarkCpuDiagnosticFrames"));
        Method(typeof(FilmicDisplayRenderer), "ResetProjectileImpactEvidence", 0).Invoke(renderer, null);

        AssertPrivateRecordAccessors(
            typeof(FilmicDisplayRenderer),
            "GlState",
            [
                true,
                false,
                513,
                true,
                false,
                true,
                false,
                true,
                33,
                34,
                35,
                36,
                37,
                38,
                new[] { DrawBuffersEnum.ColorAttachment0 },
                new[] { 0, 0, 640, 480 },
                new[] { 1, 2, 3, 4 },
                new[] { true, false, true, false }
            ]);
        AssertPrivateRecordAccessors(
            typeof(ReflectionSourceCaptureRenderer),
            "ReplayGlState",
            [true, false, true, false, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
    }

    /// <summary>Dummy reflection target used only to satisfy replay gate identity checks.</summary>
    /// <param name="deltaTime">Unused official-pass shaped argument.</param>
    private static void ReplaySentinel(float deltaTime) => _ = deltaTime;

    /// <summary>Asserts a complete carrier set is rejected before the first OpenGL query.</summary>
    private static void AssertReplayCarrierRejected(
        double[] camera,
        float[] cameraFloat,
        double[] projection,
        float[] currentProjection,
        StackMatrix4 stack,
        double[] obliqueProjection)
    {
        SetStaticField(
            typeof(EntityMirrorGeometryReplayPatch),
            "api",
            CreateReplayApi(camera, cameraFloat, projection, currentProjection, stack));
        Assert.IsFalse(EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(Identity(), obliqueProjection));
    }

    /// <summary>Creates an API proxy exposing dimensions, framebuffer registry, and captured logs.</summary>
    private static ICoreClientAPI CreateApi(
        int width,
        int height,
        List<FrameBufferRef> frameBuffers,
        List<string> logs)
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Warning) or nameof(ILogger.Error) or nameof(ILogger.Notification))
            {
                logs.Add($"{method.Name}:{arguments?[0]}");
            }
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) => method.Name switch
        {
            "get_FrameWidth" => width,
            "get_FrameHeight" => height,
            "get_FrameBuffers" => frameBuffers,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Logger" => logger,
            "get_Render" => render,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Creates a render API exposing mutable matrix carriers for replay validation.</summary>
    private static ICoreClientAPI CreateReplayApi(
        double[] camera,
        float[] cameraFloat,
        double[] projection,
        float[] currentProjection,
        StackMatrix4 stack)
    {
        IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) => method.Name switch
        {
            "get_CameraMatrixOrigin" => camera,
            "get_CameraMatrixOriginf" => cameraFloat,
            "get_PerspectiveProjectionMat" => projection,
            "get_CurrentProjectionMatrix" => currentProjection,
            "get_PMatrix" => stack,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>(
            static (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Render" => render,
            "get_Logger" => logger,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Sets every owned reflection snapshot handle to a coherent ready state.</summary>
    private static void SetSnapshotFields(ReflectionSourceCaptureRenderer renderer, int width, int height)
    {
        foreach (string name in new[]
                 {
                     "colorTextureId",
                     "glowTextureId",
                     "normalTextureId",
                     "positionTextureId",
                     "depthTextureId"
                 })
        {
            SetField(renderer, name, 1);
        }
        SetField(renderer, "width", width);
        SetField(renderer, "height", height);
        SetField(renderer, "ready", true);
    }

    /// <summary>Creates a one-element projection stack containing the supplied matrix.</summary>
    private static StackMatrix4 Stack(double[] matrix)
    {
        StackMatrix4 stack = new(4);
        stack.Push(matrix);
        return stack;
    }

    /// <summary>Returns a column-major identity matrix.</summary>
    private static double[] Identity() =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    ];

    /// <summary>Converts one double-precision carrier to its float companion.</summary>
    private static float[] ToFloat(double[] values) =>
        values.Select(static value => (float)value).ToArray();

    /// <summary>Gets an unambiguous private method by name and parameter count.</summary>
    private static MethodInfo Method(Type type, string name, int parameterCount, bool isStatic = false) =>
        type.GetMethods(isStatic ? PrivateStatic : PrivateInstance)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);

    /// <summary>Sets one private instance field.</summary>
    private static void SetField<T>(object target, string name, T value)
    {
        target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    }

    /// <summary>Gets one private instance field.</summary>
    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;

    /// <summary>Sets one private static field.</summary>
    private static void SetStaticField(Type type, string name, object? value)
    {
        type.GetField(name, PrivateStatic)!.SetValue(null, value);
    }

    /// <summary>Constructs a private record and executes each generated property accessor.</summary>
    private static void AssertPrivateRecordAccessors(Type owner, string nestedName, object?[] arguments)
    {
        Type nested = owner.GetNestedType(nestedName, BindingFlags.NonPublic)
            ?? throw new MissingMemberException(owner.FullName, nestedName);
        object value = Activator.CreateInstance(
            nested,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null)
            ?? throw new InvalidOperationException($"Could not create {nested.FullName}.");
        PropertyInfo[] properties = nested.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.AreEqual(arguments.Length, properties.Length);
        foreach (PropertyInfo property in properties)
        {
            Assert.IsNotNull(property.GetValue(value), property.Name);
        }
    }
}
