using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using VintageRTX.Client;
using VintageRTX.Core.Transport;
using Vintagestory.API.Common;

namespace VintageRTX.Client.Tests;

[TestClass,DoNotParallelize]
public sealed class WorldShaderAssetTests
{
    public static void Initialize(TestContext? _) { }
    internal sealed class Memory
    {
        internal readonly Dictionary<string,IAsset> Assets = new();
        internal readonly IAssetManager Manager;
        internal Memory()
        {
            string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
            foreach(string n in new[]{"chunkopaque.vsh","chunkopaque.fsh","entityanimated.vsh","entityanimated.fsh","chunktopsoil.vsh","chunktopsoil.fsh","standard.vsh","standard.fsh","fogandlight.fsh"})
                Add("game:"+(n=="fogandlight.fsh"?"shaderincludes/":"shaders/")+n,
                    File.ReadAllText(Directory.GetFiles(Path.Combine(game,"assets"),n,SearchOption.AllDirectories).Single()));
            foreach(string n in new[]{"scene-query.glsl","light-query.glsl","material-query.glsl","world-lighting.glsl"})
                Add("vintagertx:shaderincludes/"+n,File.ReadAllText(Path.Combine(AppContext.BaseDirectory,n)));
            Manager=Stub.Create<IAssetManager>((m,a)=>m.Name is "Get" or "TryGet"
                ?Assets.GetValueOrDefault(a[0]!.ToString()!):throw new InvalidOperationException(m.Name));
        }
        private void Add(string name,string source)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(source);
            Assets[name]=Stub.Create<IAsset>((m,a)=>m.Name switch {
                "get_Data"=>bytes,"ToText"=>Encoding.UTF8.GetString(bytes),
                "set_Data"=>Set((byte[])a[0]!),"get_Location"=>new AssetLocation(name),
                _=>throw new InvalidOperationException(m.Name)});
            object? Set(byte[] value){bytes=value;return null;}
        }
    }
    [TestMethod]
    public void RegisteredWorldStagesBindObservedFramesAndRestoreNativeProgramsAfterOpaque()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(WorldShaderAssetTests),nameof(RegisteredWorldStagesBindObservedFramesAndRestoreNativeProgramsAfterOpaque)))return;
        using var gl=PortableGlContext.Create();var host=new LifecycleHost();var memory=new Memory();
        var catalog=new EmissionAssetCatalog(host.Assets,host.Logger);Assert.IsTrue(catalog.LoadPatched());
        using var observer=new ClientSourceObserver(host.Api,catalog);
        var programs=new Dictionary<int,IShaderProgram>();var handles=new List<int>();int reloads=0;
        bool failReload=true,throwReload=false;
        var shaders=Stub.Create<IShaderAPI>((m,a)=> {
            if(m.Name=="GetProgram")return programs.GetValueOrDefault((int)a[0]!);
            if(m.Name!="ReloadShaders")throw new InvalidOperationException(m.Name);
            reloads++;
            if(throwReload){throwReload=false;throw new InvalidOperationException("driver reload exception");}
            if(failReload){failReload=false;return false;}
            foreach(var pair in new[]{("chunkopaque",EnumShaderProgram.Chunkopaque),("entityanimated",EnumShaderProgram.Entityanimated),("chunktopsoil",EnumShaderProgram.Chunktopsoil),("standard",EnumShaderProgram.Standard)}) {
                int handle=DirectImagePass.LinkSources(
                    NativeWorldLightingTests.Expand(memory.Assets["game:shaders/"+pair.Item1+".vsh"].ToText()),
                    NativeWorldLightingTests.Expand(memory.Assets["game:shaders/"+pair.Item1+".fsh"].ToText()));
                handles.Add(handle);
                programs[(int)pair.Item2]=Stub.Create<IShaderProgram>((method,_)=>method.Name switch {
                    "get_ProgramId"=>handle,"get_Disposed"=>false,_=>throw new InvalidOperationException(method.Name)});
            }
            return true;
        });
        var uniform=new DefaultShaderUniforms();uniform.playerReferencePos = new Vintagestory.API.MathTools.Vec3d(4,4,4);
        var render=Stub.Create<IRenderAPI>((m,a)=>m.Name=="get_ShaderUniforms"?uniform:m.Invoke(host.Api.Render,a));
        var api=Stub.Create<ICoreClientAPI>((m,a)=>m.Name switch {
            "get_Assets"=>memory.Manager,"get_Shader"=>shaders,"get_Render"=>render,_=>m.Invoke(host.Api,a)});
        var renderer=new WorldLightingRenderer(api,observer);
        try {
            Assert.AreEqual(.36,renderer.RenderOrder);
            host.HasPlayer=false;renderer.OnRenderFrame(0,EnumRenderStage.Before);Assert.AreEqual(0,reloads);
            host.HasPlayer=true;renderer.OnRenderFrame(0,EnumRenderStage.Before);
            Assert.IsNotNull(renderer.LastError);Assert.AreEqual(2,reloads);
            Assert.IsFalse(memory.Assets["game:shaders/chunkopaque.fsh"].ToText().Contains(WorldShaderSource.Marker,StringComparison.Ordinal));
            throwReload=true;renderer.Configure("retry");renderer.OnRenderFrame(0,EnumRenderStage.Before);
            StringAssert.Contains(renderer.LastError!,"driver reload exception");
            Assert.IsFalse(memory.Assets["game:shaders/standard.fsh"].ToText().Contains(WorldShaderSource.Marker,StringComparison.Ordinal));
            renderer.Configure("retry");renderer.OnRenderFrame(0,EnumRenderStage.Before);Assert.IsNull(renderer.LastError,renderer.LastError);
            renderer.OnRenderFrame(0,EnumRenderStage.Opaque);StringAssert.Contains(renderer.Describe(),"waiting for coherent");
            host.Tick();observer.OnRenderFrame(0,EnumRenderStage.Before);Assert.IsTrue(observer.TryBorrowWorldFrame(out var frame));
            renderer.OnRenderFrame(0,EnumRenderStage.Opaque);Assert.IsNull(renderer.LastError,renderer.LastError);
            foreach(var p in programs.Values) {
                GL.GetUniform(p.ProgramId,GL.GetUniformLocation(p.ProgramId,"vrtxWorldEnabled"),out int enabled);Assert.AreEqual(1,enabled);
                GL.GetUniform(p.ProgramId,GL.GetUniformLocation(p.ProgramId,"lightCount"),out int count);Assert.AreEqual(frame.Lights.Samples.Length,count);
            }
            host.Renderers.Single(r=>r.RenderOrder==.79).OnRenderFrame(0,EnumRenderStage.Opaque);
            foreach(var p in programs.Values) {GL.GetUniform(p.ProgramId,GL.GetUniformLocation(p.ProgramId,"vrtxWorldEnabled"),out int value);Assert.AreEqual(0,value);}
            renderer.Configure("off");renderer.OnRenderFrame(0,EnumRenderStage.Opaque);StringAssert.Contains(renderer.Describe(),"native mode");
            renderer.Configure("coverage");renderer.OnRenderFrame(0,EnumRenderStage.Opaque);
            renderer.OnRenderFrame(0,EnumRenderStage.Before); // interrupted interval also restored
            host.Raise("LeaveWorld");Assert.IsFalse(observer.TryBorrowWorldFrame(out _));
            renderer.OnRenderFrame(0,EnumRenderStage.Opaque);Assert.IsNull(renderer.LastError,renderer.LastError);
            renderer.Dispose();renderer.Dispose();StringAssert.Contains(renderer.Configure("on"),"stopped");
            renderer.OnRenderFrame(0,EnumRenderStage.Before);Assert.AreEqual(1,host.Renderers.Count); // only observer remains
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
        }
        finally {renderer.Dispose();foreach(int handle in handles)GL.DeleteProgram(handle);}
    }

    [TestMethod]
    public void AllNativeProgramsArePatchedOnceAndOwnedBytesAreRestored()
    {
        var memory=new Memory();var originals=memory.Assets.ToDictionary(p=>p.Key,p=>p.Value.Data);
        var assets=new WorldShaderAssets(memory.Manager);
        Assert.IsTrue(assets.Install(),assets.LastError);Assert.IsTrue(assets.Installed);
        foreach(var pair in memory.Assets.Where(p=>p.Key.StartsWith("game:shaders/",StringComparison.Ordinal)))
            StringAssert.Contains(pair.Value.ToText(),WorldShaderSource.Marker);
        var patched=memory.Assets.ToDictionary(p=>p.Key,p=>p.Value.Data);
        Assert.IsTrue(assets.Install());
        foreach(var pair in patched)Assert.AreSame(pair.Value,memory.Assets[pair.Key].Data);
        assets.Dispose();assets.Dispose();Assert.IsFalse(assets.Installed);
        foreach(var pair in originals)Assert.AreSame(pair.Value,memory.Assets[pair.Key].Data);
    }
    [TestMethod]
    public void MissingOrChangedContractsCannotPartlyModifyNativeTerrain()
    {
        var memory=new Memory();var terrain=memory.Assets["game:shaders/chunkopaque.fsh"];
        byte[] before=terrain.Data;
        var entity=memory.Assets["game:shaders/entityanimated.fsh"];byte[] original=entity.Data;
        entity.Data=Encoding.UTF8.GetBytes("#version 330 core\nvoid main(){}");
        using var assets=new WorldShaderAssets(memory.Manager);
        Assert.IsFalse(assets.Install());Assert.IsFalse(assets.Installed);Assert.IsNotNull(assets.LastError);
        Assert.AreSame(before,terrain.Data);
        entity.Data=original;memory.Assets.Remove("vintagertx:shaderincludes/world-lighting.glsl");
        Assert.IsFalse(assets.Install());Assert.AreSame(before,terrain.Data);Assert.AreSame(original,entity.Data);
    }
    [TestMethod]
    public void DisposingCannotOverwriteAnotherModsNewerAsset()
    {
        var memory=new Memory();using var assets=new WorldShaderAssets(memory.Manager);
        Assert.IsTrue(assets.Install());var terrain=memory.Assets["game:shaders/chunkopaque.fsh"];
        byte[] foreign=Encoding.UTF8.GetBytes(terrain.ToText()+"\n// another mod\n");terrain.Data=foreign;
        assets.Dispose();Assert.AreSame(foreign,terrain.Data);
    }
}
