using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Client;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class SourceProviderBoundaryTests
{
    public static void Initialize(TestContext? _) { }
    private static NativeWindow Context()
    {
        GLFWProvider.CheckForMainThread = false;
        var w = new NativeWindow(new NativeWindowSettings {ClientSize = new(8, 8), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new(3, 3), Profile = ContextProfile.Core});
        w.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return w;
    }
    private sealed class EmitterBlock : Block
    {
        internal byte[]? Hsv = [5, 7, 16];
        internal bool Fail;
        public override byte[] GetLightHsv(IBlockAccessor a, BlockPos p, ItemStack stack = null!) =>
            Fail ? throw new InvalidOperationException("controlled-block-provider") : Hsv!;
    }
    private sealed class EmitterEntity : EntityPlayer
    {
        internal byte[]? Hsv = [5, 7, 16]; internal bool Fail;
        internal void SetDefinitionAttributes(JsonObject attributes) => Properties = new EntityProperties {Attributes = attributes};
        public override byte[] LightHsv => Fail ? throw new InvalidOperationException("controlled-entity-provider") : Hsv!;
    }
    private static EmissionAssetCatalog Catalog(LifecycleHost host)
    {var value = new EmissionAssetCatalog(host.Assets, host.Logger); Assert.IsTrue(value.LoadPatched()); return value;}
    private static void InvokeCallback(ClientSourceObserver observer, string method, params object[] arguments) =>
        typeof(ClientSourceObserver).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(observer, arguments);

    [TestMethod]
    public void MissingChunksCodesAndInvalidHsvNeverLeaveTheFormerSourceAlive()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(SourceProviderBoundaryTests), nameof(MissingChunksCodesAndInvalidHsvNeverLeaveTheFormerSourceAlive))) return;
        using var gl = Context(); var host = new LifecycleHost(); using var observer = new ClientSourceObserver(host.Api, Catalog(host));
        host.Tick(); var pos = new BlockPos(4, 4, 4, 0); var block = new EmitterBlock {Code = new("game:candle"), BlockId = 100};
        host.Set(pos, block); host.Raise("BlockChanged", pos, host.Air); host.Tick(); observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        foreach(byte[]? hsv in new byte[]?[] {null, [], [5, 7], [5, 7, 0]})
        {
            block.Hsv = hsv; host.Raise("BlockChanged", pos, block); host.Tick(); host.Milliseconds++;
            observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        }
        block.Hsv = [5, 7, 16]; block.Code = null!; host.Raise("BlockChanged", pos, block); host.Tick();
        host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        block.Code = new("game:candle"); host.Raise("BlockChanged", pos, block); host.Tick();
        host.Loaded = false; host.Raise("BlockChanged", pos, block); host.Tick(); host.Milliseconds++;
        observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        StringAssert.Contains(observer.Describe(), "watched blocks=0");
        host.Loaded = true; block.Fail = true; host.Raise("BlockChanged", pos, block); host.Tick();
        Assert.IsTrue(host.Messages.Any(m => m.Contains("controlled-block-provider")));
        block.Fail = false; block.Code = new("other:intrinsic"); block.Hsv = [5, 7, 0];
        block.LightHsv = new ThreeBytes(new byte[] {5, 7, 16});
        Assert.AreEqual((byte)16, block.LightHsv[2], "Fixture must expose the intended intrinsic emission.");
        host.Raise("BlockChanged", pos, block); host.Tick(); StringAssert.Contains(observer.Describe(), "watched blocks=1");
        block.Hsv = [5, 7, 16]; host.Tick(); host.Milliseconds++;
        observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        host.Player.Pos.X = 80; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
    }

    [TestMethod]
    public void ProviderFailuresAreBoundedAndAttributesCannotTurnRejectedSourcesOn()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(SourceProviderBoundaryTests), nameof(ProviderFailuresAreBoundedAndAttributesCannotTurnRejectedSourcesOn))) return;
        using var gl = Context(); var host = new LifecycleHost(); var assets = Catalog(host);
        using var observer = new ClientSourceObserver(host.Api, assets); host.Tick();
        for(int i = 0; i < 140; i++)
        {
            var entity = new EmitterEntity {EntityId = i + 1, Code = i == 0 ? null! : new AssetLocation("test:broken-" + i), Fail = true};
            entity.Pos.SetPos(5, 4, 4); host.Entities![entity.EntityId] = entity;
        }
        host.Tick(); int warnings = host.Messages.Count(m => m.Contains("Source provider")); Assert.AreEqual(128, warnings);
        host.Tick(); Assert.AreEqual(warnings, host.Messages.Count(m => m.Contains("Source provider")));
        host.Entities!.Clear(); var valid = new EmitterEntity {EntityId = 1000, Code = new("test:configured")}; valid.Pos.SetPos(5, 4, 4);
        valid.SetDefinitionAttributes(JsonObject.FromJson("""{"vintageRtxEmission":{"enabled":false}}""")); host.Entities[1000] = valid;
        host.Tick(); observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        valid.SetDefinitionAttributes(JsonObject.FromJson("""{"vintageRtxEmission":{"enabled":true}}""")); host.Tick(); host.Milliseconds++;
        observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        valid.Pos.X = double.NaN; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        valid.Pos.X = 5; valid.Code = null!; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
    }

    [TestMethod]
    public void GeometryProviderFailureAndGpuFailureDoNotEraseTheCpuSourceFrame()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(SourceProviderBoundaryTests), nameof(GeometryProviderFailureAndGpuFailureDoNotEraseTheCpuSourceFrame))) return;
        using var gl = Context(); var host = new LifecycleHost(); using var observer = new ClientSourceObserver(host.Api, Catalog(host)); host.Tick();
        var block = new Block {BlockId = 700, Code = new("test:missing-tessellator"), DrawType = EnumDrawType.Cube, RenderPass = EnumChunkRenderPass.Opaque};
        for(int f = 0; f < 6; f++) block.SideOpaque[f] = true;
        host.Set(new(4, 4, 4, 0), block); host.Raise("BlockChanged", new BlockPos(4, 4, 4, 0), host.Air); host.Tick();
        Assert.IsTrue(host.Messages.Any(m => m.Contains("geometry:test:missing-tessellator")));
        host.Player.Pos.X = (double)int.MaxValue + 8; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.IsNotNull(observer.CurrentFrame); StringAssert.Contains(observer.Describe(), "GPU light frame=-1");
        host.Player.Pos.X = 4; host.Raise("LeaveWorld"); host.Tick();
        observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.IsNotNull(observer.CurrentFrame);
        host.Milliseconds++; var entity = new EmitterEntity {EntityId = 9, Code = new("game:locust-bronze")}; entity.Pos.SetPos(5, 4, 4); host.Entities![9] = entity; host.Tick();
        GL.Enable((EnableCap)(-1)); observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length); StringAssert.Contains(observer.Describe(), "GPU light frame=-1");
        host.HasWorld = false; host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before); host.HasWorld = true;
        host.Raise("LeaveWorld"); host.Tick(); host.HasPlayer = false; observer.OnRenderFrame(0, EnumRenderStage.Before); host.HasPlayer = true;
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [TestMethod]
    public void TeardownMakesRetainedCallbacksInertAndLargeNoticeBatchesRemainBounded()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(SourceProviderBoundaryTests), nameof(TeardownMakesRetainedCallbacksInertAndLargeNoticeBatchesRemainBounded))) return;
        var host = new LifecycleHost(); var observer = new ClientSourceObserver(host.Api, Catalog(host));
        InvokeCallback(observer, "SampleRegion", new RegionId(), 0);
        InvokeCallback(observer, "ObserveBlock", 0, 0, 0, 0, true);
        InvokeCallback(observer, "RemoveSource", new LightId());
        InvokeCallback(observer, "Observe", new LightId(), "test:late", new byte[] {1, 1, 1}, new VintageRTX.Core.Geometry.DVec3(), EmissionTarget.Entity, null!);
        host.Tick();
        for(int i = 0; i < 65; i++) host.Raise("ChunkDirty", new Vec3i(i, 0, 0), host.Chunk, default(EnumChunkDirtyReason));
        for(int i = 0; i < 129; i++) host.Raise("BlockChanged", new BlockPos(i, 0, 0, 0), host.Air);
        typeof(ClientSourceObserver).GetField("pollOffset", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(observer, 1000001);
        host.Tick(); Assert.AreEqual(0, typeof(ClientSourceObserver).GetField("pollOffset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(observer));
        observer.Dispose(); observer.Dispose(); Assert.IsNull(observer.CurrentFrame);
        Assert.AreEqual(0, host.Renderers.Count);
    }
}
