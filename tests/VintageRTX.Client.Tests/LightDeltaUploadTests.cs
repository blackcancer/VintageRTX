using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Client;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using V3 = System.Numerics.Vector3;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class LightDeltaUploadTests
{
    private static NativeWindow Context()
    {
        GLFWProvider.CheckForMainThread = false;
        var window = new NativeWindow(new NativeWindowSettings { ClientSize = new(8,8), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new(3,3), Profile = ContextProfile.Core });
        window.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return window;
    }
    private static LightId Id(int n) => new(SourceKind.Extension, n, 0, 0, 0, 0);
    private static LightDefinition Lamp(int n, float energy = 1) => new(new(n, .25, .5), new V3(energy), EmissionProfile.Steady);
    private static void ExactPixels(LightTexture texture, GpuLightData packet)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture.Texture);
        float[] actual = new float[packet.Pixels.Length];
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, actual);
        CollectionAssert.AreEqual(packet.Pixels.ToArray(), actual);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [TestMethod, TestCategory("GPU")]
    public void OneChangedLightUploadsOneRowAndSkippedRevisionsRecoverTheCompleteTexture()
    {
        using var window = Context(); using var first = new LightTexture(); using var delayed = new LightTexture();
        var registry = new LightRegistry(new(Guid.NewGuid(), 0)); var packet = new GpuLightData();
        for (int i = 0; i < 256; i++) registry.Upsert(Id(i), Lamp(i));
        packet.Update(registry.Capture(1, 0), default); first.Upload(packet); delayed.Upload(packet);
        Assert.AreEqual(8192L, first.LastUploadBytes); ExactPixels(first, packet);
        registry.Upsert(Id(117), Lamp(117, 4)); packet.Update(registry.Capture(2, 1), default); first.Upload(packet);
        Assert.AreEqual(32L, first.LastUploadBytes); ExactPixels(first, packet);
        packet.Update(registry.Capture(3, 2), default); first.Upload(packet);
        Assert.AreEqual(0L, first.LastUploadBytes);
        Assert.AreEqual(117, packet.ChangedRowStart); // delta is retained across no-op updates
        registry.Upsert(Id(118), Lamp(118, 7)); packet.Update(registry.Capture(4, 3), default);
        first.Upload(packet); Assert.AreEqual(32L, first.LastUploadBytes);
        delayed.Upload(packet); Assert.AreEqual(8192L, delayed.LastUploadBytes);
        ExactPixels(first, packet); ExactPixels(delayed, packet);
        registry.Remove(Id(255)); packet.Update(registry.Capture(5, 4), default); first.Upload(packet);
        Assert.AreEqual(32L, first.LastUploadBytes); Assert.AreEqual(255, first.PublishedFrame!.Samples.Length);
        ExactPixels(first, packet); Assert.IsTrue(packet.Pixels[(255 * 8)..].ToArray().All(x => x == 0));
        first.Invalidate(); first.Upload(packet); Assert.AreEqual(8192L, first.LastUploadBytes); ExactPixels(first, packet);
        var alternatePacket = new GpuLightData(); alternatePacket.Update(registry.Capture(6, 5), default);
        first.Upload(alternatePacket); Assert.AreEqual(8192L, first.LastUploadBytes); ExactPixels(first, alternatePacket);
        registry.Upsert(Id(256), Lamp(256)); registry.Upsert(Id(257), Lamp(257));
        alternatePacket.Update(registry.Capture(7, 6), default); first.Upload(alternatePacket);
        Assert.AreEqual(16384L, first.LastUploadBytes); ExactPixels(first, alternatePacket);
        Console.WriteLine("Verified transfer sizes: 8192-byte initial table; 32-byte single-source edit; 0-byte stable frame; 8192-byte missed-revision recovery.");
    }

    [TestMethod, TestCategory("GPU")]
    public void FailedDeltaWithdrawsReadinessAndRetryCannotReuseAnIncompleteGpuRevision()
    {
        using var window = Context(); using var texture = new LightTexture();
        var registry = new LightRegistry(new(Guid.NewGuid(), 0)); var packet = new GpuLightData();
        for (int i = 0; i < 16; i++) registry.Upsert(Id(i), Lamp(i));
        packet.Update(registry.Capture(1, 0), default); texture.Upload(packet);
        registry.Upsert(Id(7), Lamp(7, 3)); packet.Update(registry.Capture(2, 1), default);
        GL.Enable((EnableCap)(-1));
        Assert.ThrowsException<InvalidOperationException>(() => texture.Upload(packet));
        Assert.IsFalse(texture.Ready); Assert.IsNull(texture.PublishedFrame);
        texture.Upload(packet); Assert.AreEqual(512L, texture.LastUploadBytes); ExactPixels(texture, packet);
        Assert.ThrowsException<ArgumentNullException>(() => texture.Upload(null!));
        Assert.ThrowsException<ArgumentException>(() => texture.Upload(new GpuLightData()));
    }

    [TestMethod, TestCategory("GPU")]
    public void HardwareCapacityOwnerThreadAndDisposalAreExplicitBoundaries()
    {
        using var window = Context(); var texture = new LightTexture();
        var registry = new LightRegistry(new(Guid.NewGuid(), 0)); var packet = new GpuLightData();
        int limit = GL.GetInteger(GetPName.MaxTextureSize);
        Assert.IsTrue(limit > 0 && limit <= 65536, "Unexpected test-driver texture extent.");
        for (int i = 0; i <= limit; i++) registry.Upsert(Id(i), Lamp(i));
        packet.Update(registry.Capture(1, 0), default);
        Assert.ThrowsException<InvalidOperationException>(() => texture.Upload(packet)); Assert.IsFalse(texture.Ready);
        Exception? error = null;
        var thread = new Thread(() => { try { texture.Upload(packet); } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join(); Assert.IsInstanceOfType<InvalidOperationException>(error);
        error = null;
        thread = new Thread(() => { try { texture.Dispose(); } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join(); Assert.IsInstanceOfType<InvalidOperationException>(error);
        texture.Dispose(); texture.Dispose(); Assert.ThrowsException<ObjectDisposedException>(() => texture.Upload(packet));
    }
}
