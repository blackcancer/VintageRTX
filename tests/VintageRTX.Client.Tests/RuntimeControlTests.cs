using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using VintageRTX.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class RuntimeControlTests
{
    public static void Initialize(TestContext? _) { }
    private sealed class Candle : Block
    {
        public override byte[] GetLightHsv(IBlockAccessor accessor, BlockPos pos, ItemStack stack = null!) => [5, 7, 16];
    }
    [TestMethod]
    public void GlobalOffStopsWorldReadsAndUploadsAndOnNeverReusesAStaleLight()
    {
        if (GameTestIsolation.InvokeIfDefault(typeof(RuntimeControlTests),nameof(GlobalOffStopsWorldReadsAndUploadsAndOnNeverReusesAStaleLight))) return;
        using var gl = PortableGlContext.Create(); var host = new LifecycleHost(); int worldReads = 0;
        var api = Stub.Create<ICoreClientAPI>((m,a) => {if(m.Name=="get_World")worldReads++; return m.Invoke(host.Api,a);});
        var catalog = new EmissionAssetCatalog(host.Assets,host.Logger); Assert.IsTrue(catalog.LoadPatched());
        using var observer = new ClientSourceObserver(api,catalog); using var renderer = new WorldLightingRenderer(api,observer);
        var pos = new BlockPos(4,4,4,0); var candle = new Candle{Code=new("game:candle"),BlockId=201};
        host.Set(pos,candle); host.Tick(); host.Raise("BlockChanged",pos,host.Air);host.Tick();observer.OnRenderFrame(0,EnumRenderStage.Before);
        Assert.AreEqual(1,observer.CurrentFrame!.Samples.Length);
        var geom = (ClientGeometryCollector)typeof(ClientSourceObserver).GetField("geometry",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(observer)!;
        var texture = (LightTexture)typeof(ClientSourceObserver).GetField("lightTexture",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(observer)!;
        long geometryBytes = geom.TotalUploadBytes, lightBytes = texture.TotalUploadBytes;
        var frozen = observer.CurrentFrame; renderer.Configure("off"); int reads = worldReads;
        Assert.IsFalse(observer.Enabled); Assert.IsNull(observer.CurrentFrame); Assert.IsFalse(observer.TryBorrowWorldFrame(out _));
        host.Set(pos,host.Air); host.Raise("BlockChanged",pos,candle);
        for(int i=0;i<30;i++){host.Tick();observer.OnRenderFrame(.02f,EnumRenderStage.Before);host.Milliseconds++;}
        Assert.AreEqual(reads,worldReads);Assert.AreEqual(geometryBytes,geom.TotalUploadBytes);Assert.AreEqual(lightBytes,texture.TotalUploadBytes);
        renderer.Configure("on");observer.OnRenderFrame(0,EnumRenderStage.Before);
        Assert.IsNull(observer.CurrentFrame,"Resume must wait for a fresh owner-thread world observation.");
        host.Tick();observer.OnRenderFrame(0,EnumRenderStage.Before);Assert.AreEqual(0,observer.CurrentFrame!.Samples.Length);
        Assert.AreEqual(1,frozen.Samples.Length); // Old immutable frame remains old, never reused as current.
        renderer.Configure("toggle");Assert.IsFalse(observer.Enabled);renderer.Configure("toggle");Assert.IsTrue(observer.Enabled);
        Assert.AreEqual(ErrorCode.NoError,GL.GetError());
    }
    [TestMethod]
    public void BoundedOffNotificationsRecoverThroughAFullFreshObservationWithoutGhostSources()
    {
        if (GameTestIsolation.InvokeIfDefault(typeof(RuntimeControlTests),nameof(BoundedOffNotificationsRecoverThroughAFullFreshObservationWithoutGhostSources))) return;
        using var gl = PortableGlContext.Create();var host = new LifecycleHost();var catalog=new EmissionAssetCatalog(host.Assets,host.Logger);catalog.LoadPatched();
        using var observer=new ClientSourceObserver(host.Api,catalog);host.Tick();observer.OnRenderFrame(0,EnumRenderStage.Before);
        observer.SetEnabled(false);observer.SetEnabled(false);
        for(int i=0;i<4100;i++)host.Raise("BlockChanged",new BlockPos(i,0,0,0),host.Air);
        for(int i=0;i<1030;i++)host.Raise("ChunkDirty",new Vec3i(i,0,0),host.Chunk,default(EnumChunkDirtyReason));
        Assert.AreEqual(4100,observer.EditRevision);
        observer.SetEnabled(true);host.Tick();host.Milliseconds++;observer.OnRenderFrame(0,EnumRenderStage.Before);
        Assert.IsNotNull(observer.CurrentFrame);Assert.AreEqual(0,observer.CurrentFrame.Samples.Length);
        observer.SetEnabled(false);host.Raise("LeaveWorld");Assert.IsNull(observer.CurrentFrame);
        observer.Dispose();observer.SetEnabled(true);Assert.IsFalse(observer.Enabled); // Inert after disposal.
        Assert.AreEqual(ErrorCode.NoError,GL.GetError());
    }
    [TestMethod]
    public void ScreenshotReadsTheDefaultWorldViewportAndRestoresForeignPackAndReadState()
    {
        if (GameTestIsolation.InvokeIfDefault(typeof(RuntimeControlTests),nameof(ScreenshotReadsTheDefaultWorldViewportAndRestoresForeignPackAndReadState))) return;
        using var gl=PortableGlContext.Create();GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.Viewport(0,0,8,8);
        GL.Disable(EnableCap.FramebufferSrgb);GL.ColorMask(true,true,true,true);GL.ClearColor(1,0,0,1);GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Enable(EnableCap.ScissorTest);GL.Scissor(0,4,8,4);GL.ClearColor(0,0,1,1);GL.Clear(ClearBufferMask.ColorBufferBit);GL.Disable(EnableCap.ScissorTest);
        int read=GL.GenFramebuffer(),pack=GL.GenBuffer();
        try
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,read);GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindBuffer(BufferTarget.PixelPackBuffer,pack);GL.BufferData(BufferTarget.PixelPackBuffer,4096,IntPtr.Zero,BufferUsageHint.StreamRead);
            GL.PixelStore(PixelStoreParameter.PackAlignment,8);GL.PixelStore(PixelStoreParameter.PackRowLength,13);
            GL.PixelStore(PixelStoreParameter.PackSkipRows,3);GL.PixelStore(PixelStoreParameter.PackSkipPixels,2);
            var image=RuntimeFrameCapture.ReadDefaultViewport();Assert.AreEqual(8,image.Width);Assert.AreEqual(8,image.Height);
            CollectionAssert.AreEqual(new byte[]{255,0,0,255},image.Rgba[..4]);
            CollectionAssert.AreEqual(new byte[]{0,0,255,255},image.Rgba[(4*8*4)..(4*8*4+4)]);
            Assert.AreEqual(read,GL.GetInteger(GetPName.ReadFramebufferBinding));Assert.AreEqual(pack,GL.GetInteger(GetPName.PixelPackBufferBinding));
            Assert.AreEqual((int)ReadBufferMode.ColorAttachment0,GL.GetInteger(GetPName.ReadBuffer));
            Assert.AreEqual(8,GL.GetInteger(GetPName.PackAlignment));Assert.AreEqual(13,GL.GetInteger(GetPName.PackRowLength));
            Assert.AreEqual(3,GL.GetInteger(GetPName.PackSkipRows));Assert.AreEqual(2,GL.GetInteger(GetPName.PackSkipPixels));
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,read);
            Assert.ThrowsException<InvalidOperationException>(()=>RuntimeFrameCapture.ReadDefaultViewport());
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,0);GL.Viewport(1,0,8,8);
            Assert.ThrowsException<InvalidOperationException>(()=>RuntimeFrameCapture.ReadDefaultViewport());
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
        }
        finally {GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.BindBuffer(BufferTarget.PixelPackBuffer,0);GL.DeleteFramebuffer(read);GL.DeleteBuffer(pack);}
    }
    [TestMethod]
    public void TestStartIsOptInUnreadyWorldTimesOutAndAbortRestoresThePreviousMode()
    {
        if (GameTestIsolation.InvokeIfDefault(typeof(RuntimeControlTests),nameof(TestStartIsOptInUnreadyWorldTimesOutAndAbortRestoresThePreviousMode))) return;
        var host=new LifecycleHost();var catalog=new EmissionAssetCatalog(host.Assets,host.Logger);catalog.LoadPatched();
        using var observer=new ClientSourceObserver(host.Api,catalog);using var renderer=new WorldLightingRenderer(host.Api,observer);
        string root=Path.Combine(Path.GetTempPath(),"VintageRTX-runtime-tests-"+Guid.NewGuid().ToString("N"));
        double clock=0;int captures=0,hides=0;
        using var tests=new RuntimeWorldTests(host.Api,observer,renderer,()=>hides++,root,
            ()=>{captures++;throw new InvalidOperationException("Never capture an unready world.");},()=>clock);
        try
        {
            Assert.IsFalse(Directory.Exists(root));Assert.IsFalse(tests.Active);
            StringAssert.Contains(tests.Configure("status"),"NOT_RUN");StringAssert.Contains(tests.Configure("bad"),"Utilisation");
            host.HasWorld=false;StringAssert.Contains(tests.Configure("start"),"Chargez");host.HasWorld=true;
            renderer.Configure("off");StringAssert.Contains(tests.Configure("start"),"RUNNING");Assert.AreEqual(1,renderer.Mode);
            StringAssert.Contains(tests.Configure("start"),"déjà active");clock=1;tests.OnRenderFrame(.1f,EnumRenderStage.AfterBlit);
            Assert.AreEqual(0,captures);clock=91;tests.OnRenderFrame(.1f,EnumRenderStage.AfterBlit);
            Assert.IsFalse(tests.Active);Assert.AreEqual(0,renderer.Mode);Assert.AreEqual(0,captures);
            using(var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(tests.ArtifactDirectory!,"runtime-result.json"))))
            {Assert.AreEqual("FAIL",report.RootElement.GetProperty("status").GetString());Assert.AreEqual(0,report.RootElement.GetProperty("samples").GetArrayLength());}
            tests.Configure("start");string previous=tests.ArtifactDirectory!;tests.Configure("abort");
            Assert.AreEqual(0,renderer.Mode);StringAssert.Contains(tests.Describe(),"ABORTED");
            tests.Configure("start");Assert.AreNotEqual(previous,tests.ArtifactDirectory);host.Raise("LeaveWorld");
            Assert.IsFalse(tests.Active);Assert.AreEqual(0,renderer.Mode);Assert.AreEqual(3,hides);
            tests.Dispose();StringAssert.Contains(tests.Configure("start"),"stopped");
            typeof(WorldLightingRenderer).GetMethod("Fail",BindingFlags.Instance|BindingFlags.NonPublic)!
                .Invoke(renderer,new object[]{new InvalidOperationException("driver witness")});
            renderer.RestoreMode(1);StringAssert.Contains(renderer.LastError!,"driver witness");Assert.IsFalse(observer.Enabled);
            Assert.ThrowsException<ArgumentOutOfRangeException>(()=>renderer.RestoreMode(3));
            renderer.Configure("retry");Assert.IsNull(renderer.LastError);Assert.IsTrue(observer.Enabled);
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [TestMethod]
    public void ControllerReplayExportsAndRestoresEachModeAndHandlesCaptureFailure_NotAnInGameRun()
    {
        if (GameTestIsolation.InvokeIfDefault(typeof(RuntimeControlTests),nameof(ControllerReplayExportsAndRestoresEachModeAndHandlesCaptureFailure_NotAnInGameRun))) return;
        using var gl=PortableGlContext.Create();var host=new LifecycleHost();var memory=new WorldShaderAssetTests.Memory();
        var catalog=new EmissionAssetCatalog(host.Assets,host.Logger);catalog.LoadPatched();
        var programs=new Dictionary<int,IShaderProgram>();var handles=new List<int>();
        double[] identity=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
        var uniform=new DefaultShaderUniforms{playerReferencePos=new Vec3d(4,4,4)};
        var render=Stub.Create<IRenderAPI>((m,a)=>m.Name switch {
            "get_ShaderUniforms"=>uniform,"get_PerspectiveViewMat"=>identity,"get_PerspectiveProjectionMat"=>identity,
            _=>m.Invoke(host.Api.Render,a)});
        var shaders=Stub.Create<IShaderAPI>((m,a)=> {
            if(m.Name=="GetProgram")return programs.GetValueOrDefault((int)a[0]!);
            if(m.Name!="ReloadShaders")throw new InvalidOperationException(m.Name);
            foreach(var pair in new[]{("chunkopaque",EnumShaderProgram.Chunkopaque),("chunktopsoil",EnumShaderProgram.Chunktopsoil),
                ("entityanimated",EnumShaderProgram.Entityanimated),("standard",EnumShaderProgram.Standard)})
            {
                int handle=DirectImagePass.LinkSources(NativeWorldLightingTests.Expand(memory.Assets["game:shaders/"+pair.Item1+".vsh"].ToText()),
                    NativeWorldLightingTests.Expand(memory.Assets["game:shaders/"+pair.Item1+".fsh"].ToText()));
                handles.Add(handle);programs[(int)pair.Item2]=Stub.Create<IShaderProgram>((mm,_)=>mm.Name switch{
                    "get_ProgramId"=>handle,"get_Disposed"=>false,_=>throw new InvalidOperationException(mm.Name)});
            }
            return true;
        });
        var api=Stub.Create<ICoreClientAPI>((m,a)=>m.Name switch{
            "get_Assets"=>memory.Manager,"get_Shader"=>shaders,"get_Render"=>render,_=>m.Invoke(host.Api,a)});
        using var observer=new ClientSourceObserver(api,catalog);using var renderer=new WorldLightingRenderer(api,observer);
        string root=Path.Combine(Path.GetTempPath(),"VintageRTX-controller-replay-"+Guid.NewGuid().ToString("N"));
        double clock=0;
        try
        {
            // Controlled provider and color fixture isolate the recorder. These PNGs are deleted;
            // they are NEVER published as evidence of an actual played world or physical lighting.
            for(int scenario=0;scenario<3;scenario++)
            {
                renderer.Configure("off");uniform.playerReferencePos.X=4;int captures=0;
                using var campaign=new RuntimeWorldTests(api,observer,renderer,()=>{},root,()=>{
                    captures++;
                    if(scenario==1 && captures==2)throw new IOException("controlled capture failure");
                    return RuntimeFrameCapture.ReadDefaultViewport();
                },()=>clock);
                campaign.Configure("start");
                for(int i=0;i<150 && campaign.Active;i++)
                {
                    clock+=.125;host.Milliseconds+=125;host.Tick();
                    observer.OnRenderFrame(.125f,EnumRenderStage.Before);renderer.OnRenderFrame(.125f,EnumRenderStage.Before);
                    renderer.OnRenderFrame(.125f,EnumRenderStage.Opaque);
                    host.Renderers.Single(r=>r.RenderOrder==.79).OnRenderFrame(.125f,EnumRenderStage.Opaque);
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.Viewport(0,0,8,8);GL.Disable(EnableCap.ScissorTest);
                    GL.ColorMask(true,true,true,true);
                    if(renderer.Mode==0)GL.ClearColor(.1f,.1f,.1f,1);
                    else if(renderer.Mode==2)GL.ClearColor(0,1,0,1);else GL.ClearColor(.8f,.4f,.2f,1);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    if(scenario==2 && captures==1)uniform.playerReferencePos.X=4.5;
                    campaign.OnRenderFrame(.125f,EnumRenderStage.AfterBlit);
                }
                Assert.IsFalse(campaign.Active);Assert.AreEqual(0,renderer.Mode);Assert.IsFalse(observer.Enabled);
                using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(campaign.ArtifactDirectory!,"runtime-result.json")));
                Assert.AreEqual(new[]{"PASS","ERROR","INCONCLUSIVE"}[scenario],report.RootElement.GetProperty("status").GetString());
                Assert.AreEqual(0,report.RootElement.GetProperty("restoredMode").GetInt32());
                if(scenario==0)
                {
                    var snapshots=report.RootElement.GetProperty("samples").EnumerateArray().ToArray();Assert.AreEqual(4,snapshots.Length);
                    CollectionAssert.AreEqual(new[]{0,1,2,0},snapshots.Select(x=>x.GetProperty("appliedMode").GetInt32()).ToArray());
                    foreach(var sample in snapshots)Assert.IsTrue(File.Exists(Path.Combine(campaign.ArtifactDirectory!,sample.GetProperty("screenshot").GetString()!)));
                }
            }
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
        }
        finally{renderer.Dispose();foreach(int handle in handles)GL.DeleteProgram(handle);if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [TestMethod]
    public void CameraWitnessRejectsMovementDimensionProjectionAndNonfiniteData()
    {
        double[] identity=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
        var a=new RuntimeWorldTests.CameraWitness(0,0,0,0,identity,identity);
        Assert.IsTrue(a.Matches(a));Assert.IsFalse(a.Matches(a with{X=.02}));Assert.IsFalse(a.Matches(a with{Dimension=1}));
        Assert.IsFalse(a.Matches(a with{Y=double.NaN}));Assert.IsFalse(a.Matches(a with{View=[]}));
        var changed=(double[])identity.Clone();changed[0]=2;
        Assert.IsFalse(a.Matches(a with{Projection=changed}));changed[0]=double.NaN;
        Assert.IsFalse(a.Matches(a with{View=changed}));
    }
}
