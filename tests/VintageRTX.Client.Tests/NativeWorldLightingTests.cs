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
public sealed class NativeWorldLightingTests
{
    private static readonly float[] Identity = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
    private static string Game => Environment.GetEnvironmentVariable("VINTAGE_STORY")
        ?? throw new InvalidOperationException("Official native shader references required.");
    private static string Native(string name) => File.ReadAllText(Directory.GetFiles(Path.Combine(Game,"assets"),name,SearchOption.AllDirectories).Single());
    private static string Own(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,name));
    private static WorldShaderPair Build(string name) => WorldShaderSource.Build(name,Native(name+".vsh"),Native(name+".fsh"),
        Native("fogandlight.fsh"),Own("scene-query.glsl"),Own("light-query.glsl"),Own("material-query.glsl"),Own("world-lighting.glsl"));
    private static string Expand(string source,int ssao=0,int oit=0)
    {
        // Same single-inclusion rule as the native loader. Inputs are files from the actual game,
        // not hand-written stubs for fog, atlas mapping, warping, animation or SSAO.
        var seen=new HashSet<string>(StringComparer.Ordinal);
        string Recurse(string text) => Regex.Replace(text,@"(?m)^\s*#include\s+([\w.]+)\s*$",m=>
            seen.Add(m.Groups[1].Value)?Recurse(Native(m.Groups[1].Value)):"",RegexOptions.CultureInvariant);
        string result=Recurse(source).Replace("\r\n","\n",StringComparison.Ordinal);
        int line=result.IndexOf('\n');
        return result.Insert(line+1,$"#define SSAOLEVEL {ssao}\n#define USEOIT {oit}\n#define USESSBO 0\n#define SHADOWQUALITY 0\n#define DYNLIGHTS 1\n#define MAXANIMATEDELEMENTS 1\n#define NORMALVIEW 0\n#define SHINYEFFECT 0\n#define ALLOWDEPTHOFFSET 0\n");
    }
    private static int Program(string name,int ssao=0,int oit=0)
    {var pair=Build(name);return DirectImagePass.LinkSources(Expand(pair.Vertex,ssao,oit),Expand(pair.Fragment,ssao,oit));}

    [TestMethod]
    public void RealNativeTerrainEntitySsaoAndOitShaderVariantsCompile()
    {
        using var gl=PortableGlContext.Create();
        foreach(var c in new[]{("chunkopaque",0,0),("chunkopaque",1,0),("entityanimated",0,0),("entityanimated",1,0),("entityanimated",0,1)})
        {
            int program=Program(c.Item1,c.Item2,c.Item3);
            try
            {
                Assert.IsTrue(GL.IsProgram(program));
                if(c.Item3==0) {Assert.IsTrue(WorldLightingBinding.HasBridge(program));WorldLightingBinding.Prime(program);}
                else Assert.IsFalse(WorldLightingBinding.HasBridge(program),"OIT must not accidentally run the opaque world integration.");
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
        internal readonly int[] Programs=[Program("chunkopaque"),Program("entityanimated")];
        private readonly int vao=GL.GenVertexArray(),vbo=GL.GenBuffer(),target=GL.GenTexture(),atlas=GL.GenTexture(),fbo=GL.GenFramebuffer();
        internal NativeProbe()
        {
            foreach(int p in Programs)WorldLightingBinding.Prime(p);
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
        internal float[] Render(WorldGpuFrame frame,int mode,bool blueBaked=false)
        {
            using var state=new RewriteDrawState(1,1);state.Configure();
            using var bindings=new WorldLightingBinding(Programs,frame,default,Math.Max(1,mode));
            int p=Programs[0];GL.UseProgram(p);
            GL.Uniform1(GL.GetUniformLocation(p,"vrtxWorldEnabled"),mode);
            Set("terrainTex",0);Set("terrainTexLinear",0);SetFloat("viewDistance",512f);SetFloat("viewDistanceLod0",512f);
            GL.Uniform3(GL.GetUniformLocation(p,"rgbaAmbientIn"),0f,0f,0f);GL.Uniform3(GL.GetUniformLocation(p,"lightPosition"),0f,0f,1f);
            GL.Uniform4(GL.GetUniformLocation(p,"rgbaFogIn"),0f,0f,0f,1f);
            GL.Uniform1(GL.GetUniformLocation(p,"shadowIntensity"),0f);
            float[] view=(float[])Identity.Clone();view[12]=-3.5f;view[13]=-3.5f;view[14]=-5;
            float[] projection=(float[])Identity.Clone();projection[0]=projection[5]=2;projection[10]=.1f;
            GL.UniformMatrix4(GL.GetUniformLocation(p,"modelViewMatrix"),1,false,view);
            GL.UniformMatrix4(GL.GetUniformLocation(p,"projectionMatrix"),1,false,projection);
            GL.BindVertexArray(vao);GL.VertexAttrib4(2,blueBaked?.05f:.8f,.05f,blueBaked?.8f:.05f,0f);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,atlas);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.Viewport(0,0,8,8);GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.DrawArrays(PrimitiveType.Triangles,0,6);
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
            float[] pixel=new float[4];GL.ReadPixels(4,4,1,1,PixelFormat.Rgba,PixelType.Float,pixel);return pixel;
            void Set(string name,int value)=>GL.Uniform1(GL.GetUniformLocation(p,name),value);
            void SetFloat(string name,float value)=>GL.Uniform1(GL.GetUniformLocation(p,name),value);
        }
        public void Dispose(){foreach(int p in Programs)GL.DeleteProgram(p);GL.DeleteVertexArray(vao);GL.DeleteBuffer(vbo);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(target);GL.DeleteTexture(atlas);}
    }
}
