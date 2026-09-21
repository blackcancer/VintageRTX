using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Client;
using VintageRTX.Core.Diagnostics;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class DriverFailureQualificationTests
{
    private static NativeWindow Context(ContextProfile profile = ContextProfile.Core)
    {
        GLFWProvider.CheckForMainThread = false;
        var w = new NativeWindow(new NativeWindowSettings {ClientSize = new(8, 8), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new(3, 3), Profile = profile,
            Flags = profile == ContextProfile.Compatability ? ContextFlags.Default : ContextFlags.ForwardCompatible});
        w.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return w;
    }
    private sealed class Report : IGraphicsStatus
    {
        internal readonly Dictionary<GetPName, int> Limits = new();
        internal int ErrorOnRead, Reads;
        internal bool Incomplete;
        public int Limit(GetPName name) => Limits.TryGetValue(name, out int value) ? value : GL.GetInteger(name);
        public ErrorCode Error()
        {
            Assert.AreEqual(ErrorCode.NoError, GL.GetError(), "Unexpected real driver error during fault injection.");
            return ++Reads == ErrorOnRead ? ErrorCode.InvalidOperation : ErrorCode.NoError;
        }
        public FramebufferErrorCode Framebuffer(FramebufferTarget target)
        {
            Assert.AreEqual(FramebufferErrorCode.FramebufferComplete, GL.CheckFramebufferStatus(target));
            return Incomplete ? FramebufferErrorCode.FramebufferIncompleteAttachment : FramebufferErrorCode.FramebufferComplete;
        }
    }
    private static DirectImagePass Pass(IGraphicsStatus report) => new(Read("scene-query.glsl"), Read("light-query.glsl"),
        Read("material-query.glsl"), Read("direct-image.glsl"), report);
    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
    private static void Upload(DirectSurfaceFrame frame, SceneTextureSet geometry, LightTexture lights)
    {
        var data = new GpuSceneData(); data.Update(frame.Scene); geometry.Upload(data);
        var registry = new LightRegistry(frame.World); var packet = new GpuLightData();
        packet.Update(registry.Capture(1, 0), frame.Anchor); lights.Upload(packet);
    }

    [TestMethod]
    public void InsufficientSamplerOutputAndTextureLimitsAreExplicitFailures()
    {
        using var gl = Context();
        foreach (var limit in new[] {(GetPName.MaxTextureImageUnits, 8), (GetPName.MaxDrawBuffers, 2)})
        {
            var report = new Report(); report.Limits[limit.Item1] = limit.Item2;
            Assert.ThrowsException<NotSupportedException>(() => Pass(report));
        }
        foreach (var dimensions in new[] {(3, 1, 1), (1, 3, 1), (1, 1, 3)})
        {
            var report = new Report(); report.Limits[GetPName.MaxTextureSize] = 2;
            using var pass = Pass(report); using var geometry = new SceneTextureSet(); using var lights = new LightTexture();
            var scene = new CellScene(new(Guid.NewGuid(), 0));
            var frame = new DirectSurfaceFrame(scene.Capture(), default, new(0, 0, 1), dimensions.Item1, dimensions.Item2,
                new SurfaceReceiver[dimensions.Item1 * dimensions.Item2],
                Enumerable.Repeat(new SurfaceMaterial(SurfaceKind.Diffuse, Vector3.One), dimensions.Item3).ToArray());
            Upload(frame, geometry, lights);
            Assert.ThrowsException<NotSupportedException>(() => pass.Render(frame, geometry, lights));
            Assert.IsFalse(pass.Ready); Assert.IsNull(pass.PublishedSurfaces);
        }
        var sceneReport = new Report(); sceneReport.Limits[GetPName.MaxTextureSize] = 0;
        using var constrained = new SceneTextureSet(sceneReport);
        var packet = new GpuSceneData(); packet.Update(new CellScene(new(Guid.NewGuid(), 0)).Capture());
        Assert.ThrowsException<NotSupportedException>(() => constrained.Upload(packet));
        Assert.IsFalse(constrained.Ready); Assert.AreEqual(0, constrained.RegionTexture);
    }

    [TestMethod]
    public void EveryImageFailureStageInvalidatesOutputAndCanRetryWithoutRelaxingChecks()
    {
        using var gl = Context(); var frame = DirectLightLab.Create(2, 2);
        using var geometry = new SceneTextureSet(); using var lights = new LightTexture(); Upload(frame, geometry, lights);
        for (int failure = 1; failure <= 4; failure++)
        {
            var report = new Report {ErrorOnRead = failure}; using var pass = Pass(report);
            int oldFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
            Assert.ThrowsException<InvalidOperationException>(() => pass.Render(frame, geometry, lights));
            Assert.IsFalse(pass.Ready); Assert.IsNull(pass.PublishedLights); Assert.AreEqual(0, pass.RadianceTexture);
            Assert.AreEqual(oldFramebuffer, GL.GetInteger(GetPName.DrawFramebufferBinding));
            report.ErrorOnRead = 0; report.Reads = 0;
            pass.Render(frame, geometry, lights); Assert.IsTrue(pass.Ready);
        }
        var incomplete = new Report {Incomplete = true}; using var retry = Pass(incomplete);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => retry.Render(frame, geometry, lights)).Message, "incomplete");
        Assert.IsFalse(retry.Ready); incomplete.Incomplete = false;
        retry.Render(frame, geometry, lights); Assert.IsTrue(retry.Ready);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [TestMethod]
    public void ScenePublicationDoesNotSurviveAReportedDriverFailure()
    {
        using var gl = Context(); var packet = new GpuSceneData(); packet.Update(new CellScene(new(Guid.NewGuid(), 0)).Capture());
        foreach (int failAt in new[] {1, 2})
        {
            var report = new Report {ErrorOnRead = failAt}; using var texture = new SceneTextureSet(report);
            Assert.ThrowsException<InvalidOperationException>(() => texture.Upload(packet));
            Assert.IsFalse(texture.Ready); Assert.IsNull(texture.PublishedFrame);
            report.ErrorOnRead = 0; report.Reads = 0; texture.Upload(packet);
            Assert.IsTrue(texture.Ready); Assert.AreSame(packet.SourceFrame, texture.PublishedFrame);
        }
    }

    [TestMethod]
    public void CompatibilityFrontBackPolygonModesAreRestoredIndependently()
    {
        using var gl = Context(ContextProfile.Compatability);
        // Forward-compatible creation removes legacy state even when a compatibility profile
        // was requested. Verify that this test obtained the intended native context.
        Assert.AreEqual(0, GL.GetInteger(GetPName.ContextFlags) & 1);
        Assert.AreNotEqual(0, GL.GetInteger(GetPName.ContextProfileMask) & 2);
        GL.PolygonMode(TriangleFace.Front, PolygonMode.Line); GL.PolygonMode(TriangleFace.Back, PolygonMode.Point);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        using (var state = new RewriteDrawState(1, 1)) state.Configure();
        int[] modes = new int[2]; GL.GetInteger(GetPName.PolygonMode, modes);
        CollectionAssert.AreEqual(new[] {(int)PolygonMode.Line, (int)PolygonMode.Point}, modes);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }
}
