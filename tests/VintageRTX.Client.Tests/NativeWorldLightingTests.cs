using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using VintageRTX.Client;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed partial class NativeWorldLightingTests
{
    private static readonly float[] Identity = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
    private static string Game => Environment.GetEnvironmentVariable("VINTAGE_STORY")
        ?? throw new InvalidOperationException("Official native shader references required.");
    private static string Native(string name) => File.ReadAllText(Directory.GetFiles(Path.Combine(Game,"assets"),name,SearchOption.AllDirectories).Single());
    private static string Own(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,name));
    private static WorldShaderPair Build(string name) => WorldShaderSource.Build(name,Native(name+".vsh"),Native(name+".fsh"),
        Native("fogandlight.fsh"),Own("scene-query.glsl"),Own("light-query.glsl"),Own("material-query.glsl"),Own("world-lighting.glsl"));
    internal static string Expand(string source,int ssao=0,int oit=0,int shadows=0,int ssbo=0,int depth=0,int shiny=0)
    {
        // Same single-inclusion rule as the native loader. Inputs are files from the actual game,
        // not hand-written stubs for fog, atlas mapping, warping, animation or SSAO.
        var seen=new HashSet<string>(StringComparer.Ordinal);
        string Recurse(string text) => Regex.Replace(text,@"(?m)^\s*#include\s+([\w.]+)\s*$",m=>
            seen.Add(m.Groups[1].Value)?Recurse(Native(m.Groups[1].Value)):"",RegexOptions.CultureInvariant);
        string result=Recurse(source).Replace("\r\n","\n",StringComparison.Ordinal);
        if(ssbo>0)result=result.Replace("#version 330 core","#version 430 core",StringComparison.Ordinal);
        int line=result.IndexOf('\n');
        return result.Insert(line+1,$"#define SSAOLEVEL {ssao}\n#define USEOIT {oit}\n#define USESSBO {ssbo}\n#define SHADOWQUALITY {shadows}\n#define DYNLIGHTS 1\n#define MAXANIMATEDELEMENTS 1\n#define NORMALVIEW 0\n#define SHINYEFFECT {shiny}\n#define ALLOWDEPTHOFFSET {depth}\n");
    }
    private static int Program(string name,int ssao=0,int oit=0,int shadows=0,int ssbo=0,int depth=0,int shiny=0)
    {var pair=Build(name);return DirectImagePass.LinkSources(Expand(pair.Vertex,ssao,oit,shadows,ssbo,depth,shiny),Expand(pair.Fragment,ssao,oit,shadows,ssbo,depth,shiny));}

    [TestMethod]
    public void RealNativeTerrainEntitySsaoAndOitShaderVariantsCompile()
    {
        using var gl=PortableGlContext.Create();
        foreach(var c in new[]{("chunkopaque",0,0,0,0,0),("chunkopaque",1,0,1,0,0),("chunkopaque",1,0,2,1,0),
            ("chunktopsoil",0,0,0,0,0),("chunktopsoil",1,0,2,1,0),
            ("standard",0,0,0,0,0),("standard",1,0,2,0,0),("standard",1,0,1,0,1),
            ("entityanimated",0,0,0,0,0),("entityanimated",1,0,2,0,0),("entityanimated",0,1,0,0,0),("entityanimated",1,0,1,0,1)})
        {
            int program=Program(c.Item1,c.Item2,c.Item3,c.Item4,c.Item5,c.Item6);
            try
            {
                Assert.IsTrue(GL.IsProgram(program));
                if(c.Item3==0 && c.Item6==0) {Assert.IsTrue(WorldLightingBinding.HasBridge(program));WorldLightingBinding.Prime(program);}
                else Assert.IsFalse(WorldLightingBinding.HasBridge(program),"OIT and first-person depth-offset variants retain their native path.");
            }
            finally{GL.DeleteProgram(program);}
        }
        Assert.AreEqual(ErrorCode.NoError,GL.GetError());
    }

    [TestMethod]
    public void NativeWorldPixelsUseCurrentLightAndOccluderInsteadOfBakedVertexLight()
    {
        using var gl=PortableGlContext.Create();
        using var draw=new NativeProbe();
        var scene=new CellScene(new(Guid.NewGuid(),0));
        for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)scene.Observe(new(x,y,z),CellGeometry.Empty);
        var registry=new LightRegistry(scene.Capture().World);
        registry.Upsert(default,new(new(3.5,3.5,4),new(8,2,.5f),EmissionProfile.Candle));
        using var geometry=new SceneTextureSet();using var lights=new LightTexture();var gp=new GpuSceneData();var lp=new GpuLightData();
        WorldGpuFrame Frame(long frame,double time)
        {
            var s=scene.Capture();gp.Update(s);geometry.Upload(gp);var l=registry.Capture(frame,time);lp.Update(l,default);lights.Upload(lp);
            return new(s,l,default,geometry.RegionTexture,geometry.CellTexture,geometry.GeometryTexture,lights.Texture);
        }
        WorldGpuFrame first=Frame(1,1.25);
        float[] native=draw.Render(first,0),lit=draw.Render(first,1);
        Assert.IsTrue(lit[0]>lit[2],"The actual warm LightFrame must reach the real native shader.");
        Assert.IsTrue(Math.Abs(native[0]-lit[0])>.01,"World output must not remain the native baked-light carrier.");
        // Replace the deliberately red native vertex light by blue. New transport must be identical.
        float[] blue=draw.Render(first,1,blueBaked:true);
        CollectionAssert.AreEqual(lit,blue,"Baked block lighting must not be added a second time.");
        long bytes=geometry.TotalUploadBytes;
        WorldGpuFrame flicker=Frame(2,1.75);float[] changed=draw.Render(flicker,1);
        Assert.IsTrue(Math.Abs(changed[0]-lit[0])>1e-6);Assert.AreEqual(bytes,geometry.TotalUploadBytes);
        registry.Remove(default);WorldGpuFrame off=Frame(3,1.8);float[] dark=draw.Render(off,1);
        Assert.IsTrue(dark.Take(3).All(c=>c==0),"An extinguished source leaves no retained direct energy.");
        registry.Upsert(default,new(new(3.5,3.5,4),new(8,2,.5f),EmissionProfile.Steady));
        float[] wall=[0,0,.5f,1,0,.5f,1,1,.5f,0,1,.5f];int[] indices=[0,1,2,0,2,3];
        scene.Observe(new(3,3,3),CellGeometry.FromMesh(BlockMesh.CopyIndexed(wall,4,indices,6)));
        float[] blocked=draw.Render(Frame(4,2),1);Assert.IsTrue(blocked.Take(3).All(c=>c==0),"A real imported triangle must block the world light.");
        scene.Observe(new(3,3,3),CellGeometry.Unknown);
        float[] unknown=draw.Render(Frame(5,2.1),1);
        CollectionAssert.AreEqual(native,unknown,"Unknown transport retains native shading rather than manufacturing a miss or black shadow.");
        float[] nativeAgain=draw.Render(Frame(6,2.2),0);CollectionAssert.AreEqual(native,nativeAgain);
        Assert.AreEqual(ErrorCode.NoError,GL.GetError());
    }

    [TestMethod]
    public void AnimatedNativeReceiverUsesTheSameFrameAndExtinguishesWithoutAGeometryUpload()
    {
        using var gl=PortableGlContext.Create();using var draw=new NativeProbe();
        var scene=new CellScene(new(Guid.NewGuid(),0));
        for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)scene.Observe(new(x,y,z),CellGeometry.Empty);
        var snapshot=scene.Capture();var gp=new GpuSceneData();gp.Update(snapshot);
        using var geometry=new SceneTextureSet();geometry.Upload(gp);using var lights=new LightTexture();
        var registry=new LightRegistry(snapshot.World);var lp=new GpuLightData();
        registry.Upsert(default,new(new(3.5,3.5,4),new(8,2,.5f),EmissionProfile.Steady));
        WorldGpuFrame Frame(long index){var f=registry.Capture(index,index);lp.Update(f,default);lights.Upload(lp);
            return new(snapshot,f,default,geometry.RegionTexture,geometry.CellTexture,geometry.GeometryTexture,lights.Texture);}
        var frame=Frame(1);float[] terrain=draw.Render(frame,1),entity=draw.Render(frame,1,entity:true);
        for(int channel=0;channel<3;channel++)Assert.AreEqual(terrain[channel],entity[channel],.0001f);
        float[] alteredNative=draw.Render(frame,1,blueBaked:true,entity:true);
        CollectionAssert.AreEqual(entity,alteredNative);
        long bytes=geometry.TotalUploadBytes;registry.Remove(default);
        float[] dark=draw.Render(Frame(2),1,entity:true);
        Assert.IsTrue(dark.Take(3).All(value=>value==0));Assert.AreEqual(bytes,geometry.TotalUploadBytes);
    }

    [TestMethod]
    public void TopsoilAndDroppedItemShadersConsumeRealAlbedoAndImmediateLightChanges()
    {
        using var gl=PortableGlContext.Create();using var draw=new NativeProbe();
        var scene=new CellScene(new(Guid.NewGuid(),0));
        for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)scene.Observe(new(x,y,z),CellGeometry.Empty);
        var snapshot=scene.Capture();var gp=new GpuSceneData();gp.Update(snapshot);
        using var geometry=new SceneTextureSet();geometry.Upload(gp);using var lights=new LightTexture();
        var registry=new LightRegistry(snapshot.World);var lp=new GpuLightData();
        registry.Upsert(default,new(new(3.5,3.5,4),new(8,2,.5f),EmissionProfile.Steady));
        WorldGpuFrame Frame(long index) {var f=registry.Capture(index,index);lp.Update(f,default);lights.Upload(lp);
            return new(snapshot,f,default,geometry.RegionTexture,geometry.CellTexture,geometry.GeometryTexture,lights.Texture);}
        var first=Frame(1);float[] terrain=draw.Render(first,1);
        var names=new[]{"chunkopaque","entityanimated","chunktopsoil","standard"};
        var native=new float[4][];var lit=new float[4][];var blue=new float[4][];var dark=new float[4][];
        for(int index=0;index<4;index++) {
            native[index]=draw.Render(first,0,programIndex:index);
            lit[index]=draw.Render(first,1,programIndex:index);
            blue[index]=draw.Render(first,1,blueBaked:true,programIndex:index);
            Assert.IsTrue(lit[index][0]>lit[index][2]);
            for(int channel=0;channel<3;channel++)Assert.AreEqual(terrain[channel],lit[index][channel],.0001f);
            CollectionAssert.AreEqual(lit[index],blue[index]);
        }
        long bytes=geometry.TotalUploadBytes;registry.Remove(default);var off=Frame(2);
        for(int index=0;index<4;index++) {
            dark[index]=draw.Render(off,1,programIndex:index);
            Assert.IsTrue(dark[index].Take(3).All(c=>c==0));
        }
        Assert.AreEqual(bytes,geometry.TotalUploadBytes);
        if(Environment.GetEnvironmentVariable("VINTAGERTX_WORLD_EVIDENCE") is {Length:>0} destination) {
            Directory.CreateDirectory(destination);
            var report=new {
                scope="Official 1.22.7 native shader programs rasterizing a controlled textured mesh; NOT a game-world capture",
                renderer=GL.GetString(StringName.Renderer),
                geometryBytesBeforeExtinction=bytes,geometryBytesAfterExtinction=geometry.TotalUploadBytes,
                programs=Enumerable.Range(0,4).Select(i=>new {name=names[i],native=native[i],lit=lit[i],changedBakedLight=blue[i],extinguished=dark[i]})
            };
            File.WriteAllText(Path.Combine(destination,"native-world-pixels.json"),
                System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
        }
    }

    [TestMethod]
    public void DistantUnknownSourceDoesNotDisableNearbyResolvedLightOrFakeAnOccluder()
    {
        using var gl=PortableGlContext.Create();using var draw=new NativeProbe();
        var scene=new CellScene(new(Guid.NewGuid(),0));
        for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)scene.Observe(new(x,y,z),CellGeometry.Empty);
        // Real mesh pixels are at Z=2. The block behind this visible surface has no certified
        // caster provider (e.g. topsoil). Its outgoing ray is nevertheless entirely known air.
        scene.Observe(new(3,3,1),CellGeometry.Unsupported);
        using var geometry=new SceneTextureSet();using var lights=new LightTexture();
        var gp=new GpuSceneData();var lp=new GpuLightData();var registry=new LightRegistry(scene.Capture().World);
        registry.Upsert(default,new(new(3.5,3.5,4),new(8,2,.5f),EmissionProfile.Steady));
        WorldGpuFrame Frame(long i) {var snapshot=scene.Capture();gp.Update(snapshot);geometry.Upload(gp);
            var f=registry.Capture(i,i);lp.Update(f,default);lights.Upload(lp);
            return new(snapshot,f,default,geometry.RegionTexture,geometry.CellTexture,geometry.GeometryTexture,lights.Texture);}
        var first=Frame(1);float[] near=draw.Render(first,1);float[] native=draw.Render(first,0);
        Assert.IsTrue(near[0]>near[2]);Assert.IsTrue(Math.Abs(near[0]-native[0])>.01);
        LightId far=new(SourceKind.Entity,12,0,0,0,0);
        registry.Upsert(far,new(new(3.5,3.5,12),new(0,0,400),EmissionProfile.Steady));
        var partial=Frame(2);
        CollectionAssert.AreEqual(near,draw.Render(partial,1),"An untraced far source must not discard or recolor measured near light.");
        CollectionAssert.AreEqual(new float[]{0,1,1},draw.Render(partial,2).Take(3).ToArray(),"Partial transport is reported explicitly.");
        registry.Remove(default);var onlyUnknown=Frame(3);
        CollectionAssert.AreEqual(native,draw.Render(onlyUnknown,1),"No proven segment means native fallback, not an invented black shadow.");
        registry.Remove(far);registry.Upsert(default,new(new(3.5,3.5,4),new(8,2,.5f),EmissionProfile.Steady));
        scene.Observe(new(3,3,3),CellGeometry.Unsupported);
        CollectionAssert.AreEqual(native,draw.Render(Frame(4),1),"Unsupported geometry in FRONT remains unresolved, never skipped.");
    }

    [TestMethod]
    public void ActualNativePixelsKeepBlockPrecisionAtPositiveAndNegativeWorldOrigins()
    {
        using var gl=PortableGlContext.Create();using var draw=new NativeProbe();float[]? reference=null;
        foreach(CellId anchor in new[]{new CellId(0,0,0),new CellId(-8,-8,-8),new CellId(1000000000,0,-1000000000)})
        {
            var scene=new CellScene(new(Guid.NewGuid(),0));
            for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)
                scene.Observe(new(anchor.X+x,anchor.Y+y,anchor.Z+z),CellGeometry.Empty);
            var snapshot=scene.Capture();var gp=new GpuSceneData();gp.Update(snapshot);
            using var geometry=new SceneTextureSet();geometry.Upload(gp);using var lights=new LightTexture();
            var registry=new LightRegistry(snapshot.World);var lp=new GpuLightData();
            registry.Upsert(default,new(anchor.Position+new DVec3(3.5,3.5,4),new(8,2,.5f),EmissionProfile.Steady));
            var evaluated=registry.Capture(1,1);lp.Update(evaluated,anchor);lights.Upload(lp);
            var frame=new WorldGpuFrame(snapshot,evaluated,anchor,geometry.RegionTexture,geometry.CellTexture,geometry.GeometryTexture,lights.Texture);
            for(int index=0;index<4;index++)
            {
                var pixel=draw.Render(frame,1,programIndex:index,nativeReference:anchor.Position);
                reference ??= pixel;
                CollectionAssert.AreEqual(reference,pixel,"Absolute world coordinates must not round through a float before subtracting the anchor.");
            }
        }
    }

    [TestMethod]
    public void ShaderContractsRejectUnexpectedEditsWithoutSilentlyGuessingAnAlbedo()
    {
        Assert.ThrowsException<InvalidDataException>(()=>WorldShaderSource.Build("chunkopaque","void main(){}","void main(){}",Native("fogandlight.fsh"),"a","b","c","d"));
        var pair=Build("chunkopaque");
        Assert.ThrowsException<InvalidDataException>(()=>WorldShaderSource.Build("chunkopaque",pair.Vertex,pair.Fragment,Native("fogandlight.fsh"),"a","b","c","d"));
        StringAssert.Contains(pair.Fragment,"getColorMapped(terrainTexLinear, texture(terrainTex, uv));");
        StringAssert.Contains(pair.Fragment,"evaluateMaterialDirect");
        StringAssert.Contains(pair.Vertex,"transpose(mat3(modelViewMatrix)) * -camPos.xyz");
    }

    private sealed class NativeProbe:IDisposable
    {
        internal readonly int[] Programs;
        private readonly int animation=GL.GenBuffer();
        private readonly int vao=GL.GenVertexArray(),vbo=GL.GenBuffer(),target=GL.GenTexture(),atlas=GL.GenTexture(),fbo=GL.GenFramebuffer();
        internal NativeProbe(int shiny=0)
        {
            Programs=[Program("chunkopaque",shiny:shiny),Program("entityanimated",shiny:shiny),Program("chunktopsoil",shiny:shiny),Program("standard",shiny:shiny)];
            foreach(int p in Programs)WorldLightingBinding.Prime(p);
            GL.BindBuffer(BufferTarget.UniformBuffer,animation);
            GL.BufferData(BufferTarget.UniformBuffer,16*sizeof(float),Identity,BufferUsageHint.StaticDraw);
            GL.UniformBlockBinding(Programs[1],GL.GetUniformBlockIndex(Programs[1],"Animation"),0);
            GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
            float[] points=[3,3,2,4,3,2,4,4,2,3,3,2,4,4,2,3,4,2];
            GL.BufferData(BufferTarget.ArrayBuffer,points.Length*sizeof(float),points,BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,0,0);GL.EnableVertexAttribArray(0);
            GL.VertexAttrib2(1,.5f,.5f);GL.VertexAttribI1(3,7<<22);GL.VertexAttribI1(4,0);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,atlas);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,new[]{.5f,.5f,.5f,1});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D,target);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,8,8,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            Assert.AreEqual(FramebufferErrorCode.FramebufferComplete,GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        }
        internal float[] Render(WorldGpuFrame frame,int mode,bool blueBaked=false,bool entity=false,int? programIndex=null,DVec3? nativeReference=null,int flags=0)
        {
            using var state=new RewriteDrawState(1,1);state.Configure();
            using var bindings=new WorldLightingBinding(Programs,frame,nativeReference ?? default,Math.Max(1,mode));
            int index=programIndex ?? (entity?1:0);int p=Programs[index];GL.UseProgram(p);
            GL.Uniform1(GL.GetUniformLocation(p,"vrtxWorldEnabled"),mode);
            Set("terrainTex",0);Set("terrainTexLinear",0);Set("entityTex",0);Set("tex",0);Set("tex2dOverlay",0);SetFloat("viewDistance",512f);SetFloat("viewDistanceLod0",512f);
            GL.Uniform3(GL.GetUniformLocation(p,"rgbaAmbientIn"),0f,0f,0f);GL.Uniform3(GL.GetUniformLocation(p,"lightPosition"),0f,0f,1f);
            GL.Uniform4(GL.GetUniformLocation(p,"rgbaFogIn"),0f,0f,0f,1f);
            GL.Uniform1(GL.GetUniformLocation(p,"shadowIntensity"),0f);
            float[] view=(float[])Identity.Clone();view[12]=-3.5f;view[13]=-3.5f;view[14]=-5;
            float[] projection=(float[])Identity.Clone();projection[0]=projection[5]=2;projection[10]=.1f;
            GL.UniformMatrix4(GL.GetUniformLocation(p,"modelViewMatrix"),1,false,view);
            GL.UniformMatrix4(GL.GetUniformLocation(p,"projectionMatrix"),1,false,projection);
            GL.BindVertexArray(vao);GL.VertexAttribI1(3,(7<<22)|flags);
            if(index is 1 or 3) {
                GL.VertexAttrib4(2,1f,1f,1f,1f);GL.VertexAttrib1(4,0f);GL.VertexAttribI1(5,0);
                GL.Uniform4(GL.GetUniformLocation(p,"renderColor"),1f,1f,1f,1f);
                GL.Uniform4(GL.GetUniformLocation(p,"rgbaTint"),1f,1f,1f,1f);
                GL.Uniform4(GL.GetUniformLocation(p,"rgbaLightIn"),blueBaked?.05f:.8f,.05f,blueBaked?.8f:.05f,0f);
                GL.UniformMatrix4(GL.GetUniformLocation(p,"viewMatrix"),1,false,view);
                GL.UniformMatrix4(GL.GetUniformLocation(p,"modelMatrix"),1,false,Identity);
                GL.BindBufferBase(BufferRangeTarget.UniformBuffer,0,animation);
            } else {
                if(index==2) {GL.VertexAttrib2(4,.25f,.25f);GL.VertexAttribI1(5,0);} else GL.VertexAttribI1(4,0);
                GL.VertexAttrib4(2,blueBaked?.05f:.8f,.05f,blueBaked?.8f:.05f,0f);
            }
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,atlas);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.Viewport(0,0,8,8);GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.DrawArrays(PrimitiveType.Triangles,0,6);
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
            float[] pixel=new float[4];GL.ReadPixels(4,4,1,1,PixelFormat.Rgba,PixelType.Float,pixel);return pixel;
            void Set(string name,int value)=>GL.Uniform1(GL.GetUniformLocation(p,name),value);
            void SetFloat(string name,float value)=>GL.Uniform1(GL.GetUniformLocation(p,name),value);
        }
        public void Dispose(){foreach(int p in Programs)GL.DeleteProgram(p);GL.DeleteVertexArray(vao);GL.DeleteBuffer(vbo);GL.DeleteBuffer(animation);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(target);GL.DeleteTexture(atlas);}
    }
}
