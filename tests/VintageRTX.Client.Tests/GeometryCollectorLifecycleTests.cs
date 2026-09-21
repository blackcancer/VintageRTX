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
using Vintagestory.API.MathTools;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class GeometryCollectorLifecycleTests
{
    public static void Initialize(TestContext? _) { }
    private static NativeWindow Context()
    {
        GLFWProvider.CheckForMainThread = false;
        var w = new NativeWindow(new NativeWindowSettings {ClientSize = new(8, 8), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new(3, 3), Profile = ContextProfile.Core});
        w.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return w;
    }
    private static MeshData Mesh() => new(false) {xyz = [.5f, 0, 0, .5f, 1, 0, .5f, 0, 1],
        VerticesCount = 3, Indices = [0, 1, 2], IndicesCount = 3, mode = EnumDrawMode.Triangles};
    private static Block Block(int id = 1)
    {
        var block = new Block {BlockId = id, Code = new("test:mesh-" + id), DrawType = EnumDrawType.Cube,
            RenderPass = EnumChunkRenderPass.Opaque};
        for(int i = 0; i < 6; i++) block.SideOpaque[i] = true;
        return block;
    }
    private sealed class Host
    {
        internal MeshData? Source = Mesh();
        internal int Reads;
        internal readonly List<string> Warnings = new();
        internal ICoreClientAPI Api {get;}
        internal Host()
        {
            var tess = Stub.Create<ITesselatorManager>((m, _) => {if(m.Name == "GetDefaultBlockMesh") {Reads++; return Source;} return null;});
            var logger = Stub.Create<ILogger>((_, a) => {Warnings.Add(string.Join(" ", a.Select(x => x?.ToString()))); return null;});
            Api = Stub.Create<ICoreClientAPI>((m, _) => m.Name switch {"get_TesselatorManager" => tess, "get_Logger" => logger, _ => null});
        }
    }

    [TestMethod]
    public void RegionalPublicationReusesTemplatesAndUnchangedGpuFrames()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(GeometryCollectorLifecycleTests), nameof(RegionalPublicationReusesTemplatesAndUnchangedGpuFrames))) return;
        using var gl = Context(); var host = new Host(); using var collector = new ClientGeometryCollector(host.Api);
        var pos = new BlockPos(0, 0, 0, 0); Block solid = Block(), air = Block(0);
        collector.Observe(pos, solid, air); collector.Unknown(pos); collector.Invalidate(default); collector.Publish(); collector.Upload();
        Assert.IsNull(collector.Frame); Assert.IsFalse(collector.GpuAllocated); Assert.AreEqual(0L, collector.TotalUploadBytes);
        collector.Reset(new(Guid.NewGuid(), 0)); host.Source = null;
        collector.Observe(pos, solid, air); collector.Publish(); Assert.AreEqual(CellState.Unknown, collector.Frame!.At(default).State);
        host.Source = Mesh(); collector.Observe(pos, solid, air); collector.Publish();
        CellSceneFrame first = collector.Frame!; Assert.AreEqual(CellState.Mesh, first.At(default).State);
        Assert.AreEqual(1, collector.TemplateCount); collector.Upload(); Assert.IsTrue(collector.GpuAllocated);
        Assert.AreEqual(1, collector.GpuTemplateCount); Assert.IsTrue(collector.LastUploadBytes > 0);
        long total = collector.TotalUploadBytes; collector.Upload(); Assert.AreEqual(0L, collector.LastUploadBytes);
        Assert.AreEqual(total, collector.TotalUploadBytes);
        int reads = host.Reads; collector.Observe(new(1, 0, 0, 0), solid, air); collector.Publish();
        Assert.IsFalse(collector.GpuAllocated, "A new CPU frame is not its old GPU publication."); collector.Upload();
        Assert.AreEqual(reads, host.Reads); Assert.AreEqual(8224L, collector.LastUploadBytes);
        collector.Observe(pos, air, air); collector.Publish(); collector.Upload();
        Assert.AreEqual(CellState.Mesh, first.At(default).State); Assert.AreEqual(CellState.Empty, collector.Frame!.At(default).State);
        collector.Observe(pos, solid, Block(99)); collector.Publish(); Assert.AreEqual(CellState.Unsupported, collector.Frame!.At(default).State);
        collector.Unknown(pos); collector.Publish(); Assert.AreEqual(CellState.Unknown, collector.Frame!.At(default).State);
        collector.Invalidate(default); collector.Publish(); Assert.AreEqual(0, collector.Frame!.RegionCount);
        collector.Reset(new(Guid.NewGuid(), 1)); Assert.IsFalse(collector.GpuAllocated); collector.Publish(); collector.Upload();
        Assert.AreEqual(0, collector.TemplateCount); Assert.AreEqual(0, collector.GpuTemplateCount);
        collector.Clear(); collector.Publish(); Assert.IsNull(collector.Frame); Assert.IsFalse(collector.GpuAllocated);
        collector.Dispose(); collector.Dispose(); Assert.IsFalse(collector.GpuAllocated);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [TestMethod]
    public void FailedGeometryUploadStopsPublicationUntilAnExplicitReset()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(GeometryCollectorLifecycleTests), nameof(FailedGeometryUploadStopsPublicationUntilAnExplicitReset))) return;
        using var gl = Context(); var host = new Host(); using var collector = new ClientGeometryCollector(host.Api);
        collector.Reset(new(Guid.NewGuid(), 0)); collector.Observe(new(0, 0, 0, 0), Block(), Block(0)); collector.Publish(); collector.Upload();
        Assert.IsTrue(collector.GpuAllocated);
        collector.Observe(new(1, 0, 0, 0), Block(), Block(0)); collector.Publish();
        GL.Enable((EnableCap)(-1)); collector.Upload(); Assert.IsFalse(collector.GpuAllocated);
        Assert.AreEqual(1, host.Warnings.Count); collector.Upload(); Assert.AreEqual(1, host.Warnings.Count);
        collector.Reset(new(Guid.NewGuid(), 1)); collector.Publish(); collector.Upload(); Assert.IsTrue(collector.GpuAllocated);
    }

    [TestMethod]
    public void MalformedMeshesBecomeUnsupportedAndWarningsAreBoundedNotRepeated()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(GeometryCollectorLifecycleTests), nameof(MalformedMeshesBecomeUnsupportedAndWarningsAreBoundedNotRepeated))) return;
        var host = new Host {Source = new MeshData(false)}; using var collector = new ClientGeometryCollector(host.Api);
        collector.Reset(new(Guid.NewGuid(), 0)); Block first = Block(); first.Code = null!;
        collector.Observe(new(0, 0, 0, 0), first, Block(0)); collector.Publish();
        Assert.AreEqual(CellState.Unsupported, collector.Frame!.At(default).State); Assert.AreEqual(1, host.Warnings.Count);
        collector.Observe(new(1, 0, 0, 0), first, Block(0)); Assert.AreEqual(1, host.Warnings.Count);
        Block sameCode = Block(2); sameCode.Code = new("test:shared"); collector.Observe(new(2, 0, 0, 0), sameCode, Block(0));
        Block duplicate = Block(3); duplicate.Code = sameCode.Code; collector.Observe(new(3, 0, 0, 0), duplicate, Block(0));
        Assert.AreEqual(2, host.Warnings.Count);
        for(int i = 4; i < 80; i++) collector.Observe(new(i % 8, 1, 0, 0), Block(i), Block(0));
        Assert.AreEqual(64, host.Warnings.Count); collector.Reset(new(Guid.NewGuid(), 0));
        collector.Observe(new(0, 0, 0, 0), first, Block(0)); Assert.AreEqual(65, host.Warnings.Count);
        Block procedural = Block(101); procedural.HasTiles = true;
        collector.Observe(new(1, 0, 0, 0), procedural, Block(0)); collector.Publish();
        Assert.AreEqual(CellState.Unsupported, collector.Frame!.At(new(1, 0, 0)).State);
    }

    [TestMethod]
    public void EveryUnsupportedMeshChannelAndMalformedPassPrefixIsRejected()
    {
        MeshData source = Mesh(); source.mode = EnumDrawMode.Lines;
        Assert.ThrowsException<ArgumentException>(() => ClientGeometryCollector.CopyMesh(source));
        foreach(string name in new[] {"CustomBytes", "CustomFloats", "CustomInts", "CustomShorts"})
        {
            source = Mesh(); FieldInfo field = typeof(MeshData).GetField(name)!;
            field.SetValue(source, Activator.CreateInstance(field.FieldType));
            Assert.ThrowsException<ArgumentException>(() => ClientGeometryCollector.CopyMesh(source));
        }
        source = Mesh(); source.RenderPassCount = 1; source.RenderPassesAndExtraBits = null!;
        Assert.ThrowsException<ArgumentException>(() => ClientGeometryCollector.CopyMesh(source));
        source.RenderPassCount = 2; source.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Opaque];
        Assert.ThrowsException<ArgumentException>(() => ClientGeometryCollector.CopyMesh(source));
        source.RenderPassCount = 2; source.RenderPassesAndExtraBits = [-1, (short)((int)EnumChunkRenderPass.Opaque | 1024)];
        Assert.AreEqual(1, ClientGeometryCollector.CopyMesh(source).TriangleCount);
    }

    [TestMethod]
    public void EveryBlockEligibilityFlagRetainsAnExplicitUnsupportedResult()
    {
        Assert.IsFalse(ClientGeometryCollector.SupportedBlock(Block(0)));
        Block transparent = Block(); transparent.RenderPass = EnumChunkRenderPass.Transparent;
        Assert.IsFalse(ClientGeometryCollector.SupportedBlock(transparent));
        foreach(string name in new[] {"HasTiles", "RandomizeRotations", "HasAlternates"})
        {
            Block b = Block(); typeof(Block).GetField(name)!.SetValue(b, true);
            Assert.IsFalse(ClientGeometryCollector.SupportedBlock(b), name);
        }
        foreach(string name in new[] {"RandomDrawOffset", "RandomSizeAdjust"})
        {
            Block b = Block(); FieldInfo field = typeof(Block).GetField(name)!;
            field.SetValue(b, Convert.ChangeType(1, field.FieldType));
            Assert.IsFalse(ClientGeometryCollector.SupportedBlock(b), name);
        }
        foreach(string name in new[] {"BlockBehaviors", "BlockEntityBehaviors"})
        {
            Block b = Block(); FieldInfo field = typeof(Block).GetField(name)!;
            field.SetValue(b, Array.CreateInstance(field.FieldType.GetElementType()!, 1));
            Assert.IsFalse(ClientGeometryCollector.SupportedBlock(b), name);
        }
        Block lodMesh = Block(); lodMesh.Lod0Mesh = Mesh(); Assert.IsFalse(ClientGeometryCollector.SupportedBlock(lodMesh));
        Block lodShape = Block(); lodShape.Lod0Shape = new CompositeShape(); Assert.IsFalse(ClientGeometryCollector.SupportedBlock(lodShape));
        Block json = Block(); json.DrawType = EnumDrawType.JSON; Assert.IsTrue(ClientGeometryCollector.SupportedBlock(json));
        Block custom = Block(); custom.DrawType = (EnumDrawType)99; Assert.IsFalse(ClientGeometryCollector.SupportedBlock(custom));
        Block missingFace = Block(); missingFace.SideOpaque[5] = false; Assert.IsFalse(ClientGeometryCollector.SupportedBlock(missingFace));
    }
}
