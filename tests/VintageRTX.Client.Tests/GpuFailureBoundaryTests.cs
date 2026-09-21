using System.Reflection;
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
public sealed class GpuFailureBoundaryTests
{
    private static NativeWindow Context()
    {
        GLFWProvider.CheckForMainThread = false;
        var w = new NativeWindow(new NativeWindowSettings {ClientSize = new(8, 8), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new(3, 3), Profile = ContextProfile.Core});
        w.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return w;
    }
    private static DirectImagePass Pass() => new(Read("scene-query.glsl"), Read("light-query.glsl"), Read("material-query.glsl"), Read("direct-image.glsl"));
    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
    private static void Set(object target, string property, int value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static Exception? OnWorker(Action work)
    {
        Exception? result = null; var thread = new Thread(() => {try {work();} catch(Exception exception) {result = exception;}});
        thread.Start(); thread.Join(); return result;
    }
    private static void Upload(DirectSurfaceFrame surfaces, SceneTextureSet geometry, LightTexture lights, long frame = 1)
    {
        var scene = new GpuSceneData(); scene.Update(surfaces.Scene); geometry.Upload(scene);
        var registry = new LightRegistry(surfaces.World); DirectLightLab.SetLights(registry, surfaces.Anchor, new(null, null, EmissionProfile.Steady), true);
        var packet = new GpuLightData(); packet.Update(registry.Capture(frame, 0), surfaces.Anchor); lights.Upload(packet);
    }

    [TestMethod]
    public void InvalidParametersAndOlderFramesCannotLeaveTheLastImageReady()
    {
        using var gl = Context(); using var pass = Pass(); using var geometry = new SceneTextureSet(); using var lights = new LightTexture();
        var surfaces = DirectLightLab.Create(2, 2); Upload(surfaces, geometry, lights, 2); pass.Render(surfaces, geometry, lights);
        Assert.IsTrue(pass.Ready);
        foreach(int samples in new[] {0, 65})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => pass.Render(surfaces, geometry, lights, finiteSamples: samples));
        foreach(float minimum in new[] {float.NaN, -1f})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => pass.Render(surfaces, geometry, lights, rayMinimum: minimum));
        foreach(int count in new[] {0, 257})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => pass.Render(surfaces, geometry, lights, maximumCells: count));
        foreach(float exposure in new[] {float.NaN, -17f, 17f})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => pass.Render(surfaces, geometry, lights, previewExposure: exposure));
        Assert.IsFalse(pass.Ready); Assert.AreEqual(0, pass.DiagnosticTexture);
        Assert.IsNull(pass.PublishedSurfaces); Assert.IsNull(pass.PublishedLights);
        Upload(surfaces, geometry, lights, 1);
        Assert.ThrowsException<InvalidOperationException>(() => pass.Render(surfaces, geometry, lights));
        Assert.IsFalse(pass.Ready);
        lights.Invalidate(); Assert.ThrowsException<InvalidOperationException>(() => pass.Render(surfaces, geometry, lights));
        using var notUploaded = new SceneTextureSet(); Upload(surfaces, geometry, lights, 3);
        Assert.ThrowsException<InvalidOperationException>(() => pass.Render(surfaces, notUploaded, lights));
        Assert.IsInstanceOfType<InvalidOperationException>(OnWorker(() => pass.Render(surfaces, geometry, lights)));
        Assert.IsInstanceOfType<InvalidOperationException>(OnWorker(pass.Dispose));
        pass.Render(surfaces, geometry, lights); Assert.IsTrue(pass.Ready);
        pass.Dispose(); pass.Dispose(); Assert.ThrowsException<ObjectDisposedException>(() => pass.Render(surfaces, geometry, lights));
    }

    [TestMethod]
    public void FeedbackAndMissingOwnedInputsAreRejectedBeforeADraw()
    {
        using var gl = Context(); using var pass = Pass(); using var geometry = new SceneTextureSet(); using var lights = new LightTexture();
        var surfaces = DirectLightLab.Create(2, 2); Upload(surfaces, geometry, lights); pass.Render(surfaces, geometry, lights);
        int region = geometry.RegionTexture;
        int[] invalid = [0, pass.RadianceTexture, pass.DiagnosticTexture, pass.PreviewTexture];
        try
        {
            foreach(int texture in invalid)
            {
                Set(geometry, nameof(geometry.RegionTexture), texture);
                var error = Assert.ThrowsException<InvalidOperationException>(() => pass.Render(surfaces, geometry, lights));
                StringAssert.Contains(error.Message, "feedback"); Assert.IsFalse(pass.Ready);
            }
        }
        finally {Set(geometry, nameof(geometry.RegionTexture), region);}
        pass.Render(surfaces, geometry, lights); Assert.IsTrue(pass.Ready);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [TestMethod]
    public void ShaderCompileLinkAndUnallocatedDisposalPathsRemainUsable()
    {
        using var gl = Context();
        Assert.ThrowsException<InvalidOperationException>(() => new DirectImagePass("", "", "", "invalid shader"));
        int count = GL.GetInteger(GetPName.MaxTextureImageUnits) + 1;
        // A missing varying can legally link with undefined values on some drivers. Instead,
        // activate one more fragment sampler than the advertised limit: valid syntax, invalid link resources.
        string declarations = string.Join("\n", Enumerable.Range(0, count).Select(i => $"uniform sampler2D t{i};"));
        string sum = string.Join("+", Enumerable.Range(0, count).Select(i => $"texture(t{i},gl_FragCoord.xy)"));
        string fragment = declarations + "\nout vec4 outputColor;void main(){outputColor=" + sum + ";}";
        Assert.ThrowsException<InvalidOperationException>(() => new DirectImagePass("", "", "", fragment));
        using var untouched = Pass(); Assert.AreEqual(0, untouched.DiagnosticTexture); untouched.Dispose(); untouched.Dispose();
        using var scene = new SceneTextureSet(); scene.NoUpload(); Assert.AreEqual(0L, scene.LastUploadBytes);
        Assert.ThrowsException<ArgumentException>(() => scene.Upload(new GpuSceneData()));
        Assert.IsInstanceOfType<InvalidOperationException>(OnWorker(() => scene.Upload(new GpuSceneData())));
        Assert.IsInstanceOfType<InvalidOperationException>(OnWorker(scene.Dispose));
        scene.Dispose(); scene.Dispose(); Assert.ThrowsException<ObjectDisposedException>(() => scene.Upload(new GpuSceneData()));
    }

    [TestMethod]
    public void DriverUploadErrorDoesNotPublishAFrameAfterPrivateTextureTargetLoss()
    {
        using var gl = Context(); using var texture = new LightTexture();
        var registry = new LightRegistry(new(Guid.NewGuid(), 0));
        registry.Upsert(default, new(new(1, 2, 3), System.Numerics.Vector3.One, EmissionProfile.Steady));
        var packet = new GpuLightData(); packet.Update(registry.Capture(1, 0), default); texture.Upload(packet);
        int original = texture.Texture, wrongTarget = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, wrongTarget); GL.BindTexture(TextureTarget.Texture3D, 0);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        try
        {
            Set(texture, nameof(texture.Texture), wrongTarget);
            registry.Remove(default); packet.Update(registry.Capture(2, 1), default);
            Assert.ThrowsException<InvalidOperationException>(() => texture.Upload(packet));
            Assert.IsFalse(texture.Ready); Assert.IsNull(texture.PublishedFrame);
        }
        finally {Set(texture, nameof(texture.Texture), original); GL.DeleteTexture(wrongTarget);}
        for(int i = 0; i < 8; i++) if(GL.GetError() == ErrorCode.NoError) break;
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        texture.Upload(packet); Assert.IsTrue(texture.Ready); Assert.AreEqual(0, texture.PublishedFrame!.Samples.Length);
    }
}
