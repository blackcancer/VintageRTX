using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
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
public sealed class LightTextureTests
{
    private static NativeWindow Context()
    {
        GLFWProvider.CheckForMainThread = false;
        var window = new NativeWindow(new NativeWindowSettings { ClientSize=new(8,8), StartVisible=false,
            API=ContextAPI.OpenGL, APIVersion=new(3,3), Profile=ContextProfile.Core });
        window.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return window;
    }
    private static LightId Id(int i) => new(SourceKind.Entity,i,0,0,0,0);

    [TestMethod, TestCategory("GPU")]
    public void UploadPreservesStateAndStableFramesAvoidTransfers()
    {
        using var window=Context(); using var texture=new LightTexture();var packet=new GpuLightData();
        var registry=new LightRegistry(new(Guid.NewGuid(),0));
        for(int i=0;i<32;i++) registry.Upsert(Id(i),new(new(i,.25,.5),new(8,4,2),EmissionProfile.Steady));
        LightFrame first=registry.Capture(1,1);packet.Update(first,default);
        int sentinel=GL.GenTexture(),pbo=GL.GenBuffer();
        try
        {
            GL.ActiveTexture(TextureUnit.Texture6);GL.BindTexture(TextureTarget.Texture2D,sentinel);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer,pbo);GL.BufferData(BufferTarget.PixelUnpackBuffer,512,IntPtr.Zero,BufferUsageHint.StaticDraw);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment,8);GL.PixelStore(PixelStoreParameter.UnpackRowLength,13);
            GL.PixelStore(PixelStoreParameter.UnpackSkipRows,2);GL.PixelStore(PixelStoreParameter.UnpackSkipPixels,3);
            GL.PixelStore(PixelStoreParameter.UnpackSwapBytes,1);
            texture.Upload(packet);
            Assert.IsTrue(texture.Ready);Assert.AreSame(first,texture.PublishedFrame);
            Assert.AreEqual(1024L,texture.LastUploadBytes);
            Assert.AreEqual(sentinel,GL.GetInteger(GetPName.TextureBinding2D));Assert.AreEqual(pbo,GL.GetInteger(GetPName.PixelUnpackBufferBinding));
            Assert.AreEqual(8,GL.GetInteger(GetPName.UnpackAlignment));Assert.AreEqual(13,GL.GetInteger(GetPName.UnpackRowLength));
            Assert.AreEqual(2,GL.GetInteger(GetPName.UnpackSkipRows));Assert.AreEqual(3,GL.GetInteger(GetPName.UnpackSkipPixels));
            Assert.AreEqual(1,GL.GetInteger(GetPName.UnpackSwapBytes));Assert.AreEqual((int)TextureUnit.Texture6,GL.GetInteger(GetPName.ActiveTexture));
            LightFrame next=registry.Capture(2,2);Assert.IsFalse(packet.Update(next,default));texture.Upload(packet);
            Assert.AreEqual(0L,texture.LastUploadBytes);Assert.AreSame(next,texture.PublishedFrame);
            GL.BindTexture(TextureTarget.Texture2D,texture.Texture);float[] read=new float[packet.Pixels.Length];
            GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.Float,read);CollectionAssert.AreEqual(packet.Pixels.ToArray(),read);
            registry.Reset(new(Guid.NewGuid(),1));packet.Update(registry.Capture(1,0),default);texture.Upload(packet);
            GL.BindTexture(TextureTarget.Texture2D,texture.Texture);GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.Float,read);
            Assert.IsTrue(read.All(x=>x==0));Assert.AreEqual(0,texture.PublishedFrame!.Samples.Length);
            texture.Invalidate();Assert.IsFalse(texture.Ready);Assert.IsNull(texture.PublishedFrame);
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer,0);GL.PixelStore(PixelStoreParameter.UnpackSwapBytes,0);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment,4);GL.PixelStore(PixelStoreParameter.UnpackRowLength,0);
            GL.PixelStore(PixelStoreParameter.UnpackSkipRows,0);GL.PixelStore(PixelStoreParameter.UnpackSkipPixels,0);
            GL.DeleteBuffer(pbo);GL.DeleteTexture(sentinel);
        }
    }

    [TestMethod, TestCategory("GPU")]
    public void ConfiguredCandleNinthSourceIsSharedByDirectQueriesAndExtinguishes()
    {
        using var window=Context();using var lights=new LightTexture();using var geometry=new SceneTextureSet();
        var scene=new CellScene(new(Guid.NewGuid(),0));
        for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)scene.Observe(new(x,y,z),CellGeometry.Empty);
        var sceneData=new GpuSceneData();sceneData.Update(scene.Capture());geometry.Upload(sceneData);
        var registry=new LightRegistry(new(Guid.NewGuid(),0));var packet=new GpuLightData();
        for(int i=0;i<8;i++)registry.Upsert(Id(i),new(new(-1,.3,.4),V3.One,EmissionProfile.Steady));
        EmissionCatalog catalog=EmissionCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","emission.json")));
        LightDefinition candle=catalog.Resolve("game:candle",EmissionTarget.Block).CreateLight(new(2.2,.3,.4),new V3(8,4,2))!;
        registry.Upsert(Id(8),candle);LightFrame first=registry.Capture(1,3.25);packet.Update(first,default);lights.Upload(packet);
        int program=MakeProgram(),fbo=GL.GenFramebuffer(),output=GL.GenTexture(),vao=GL.GenVertexArray();
        try
        {
            GL.UseProgram(program);GL.Uniform3(GL.GetUniformLocation(program,"sceneAnchor"),0,0,0);
            GL.BindTexture(TextureTarget.Texture2D,output);GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,2,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);Assert.AreEqual(FramebufferErrorCode.FramebufferComplete,GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.CullFace);GL.Viewport(0,0,2,1);GL.BindVertexArray(vao);
            float[] lit=Draw();V3 expected=first.Samples[8].Intensity/(4*MathF.PI)*.5f;
            Assert.AreEqual(expected.X,lit[0],1e-5f);Assert.AreEqual(expected.Y,lit[1],1e-5f);Assert.AreEqual(expected.Z,lit[2],1e-5f);
            Assert.AreEqual(0f,lit[3]);CollectionAssert.AreEqual(lit[..4],lit[4..]);
            var mesh=new BlockMesh(new Triangle[]{new(new(.5,0,0),new(.5,1,0),new(.5,0,1),0)});
            scene.Observe(new(1,0,0),CellGeometry.FromMesh(mesh));sceneData.Update(scene.Capture());geometry.Upload(sceneData);
            float[] blocked=Draw();Assert.AreEqual(0f,blocked[0]);Assert.AreEqual(-1f,blocked[3]);
            scene.Observe(new(1,0,0),CellGeometry.Unknown);sceneData.Update(scene.Capture());geometry.Upload(sceneData);
            float[] unknown=Draw();Assert.AreEqual(0f,unknown[0]);Assert.AreEqual(1f,unknown[3]);
            scene.Observe(new(1,0,0),CellGeometry.Empty);sceneData.Update(scene.Capture());geometry.Upload(sceneData);
            registry.Remove(Id(8));packet.Update(registry.Capture(2,3.26),default);lights.Upload(packet);
            float[] dark=Draw();Assert.IsTrue(dark.All(x=>x==0));
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
        }
        finally
        {
            GL.UseProgram(0);GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.DeleteProgram(program);
            GL.DeleteTexture(output);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);
        }
        float[] Draw()
        {
            int[] textures={geometry.RegionTexture,geometry.CellTexture,geometry.GeometryTexture,lights.Texture};
            string[] names={"regionData","cellData","geometryData","lightData"};
            for(int i=0;i<4;i++){GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0+i));GL.BindTexture(TextureTarget.Texture2D,textures[i]);GL.Uniform1(GL.GetUniformLocation(program,names[i]),i);}
            GL.Uniform1(GL.GetUniformLocation(program,"lightCount"),lights.PublishedFrame!.Samples.Length);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);float[] pixels=new float[8];GL.ReadPixels(0,0,2,1,PixelFormat.Rgba,PixelType.Float,pixels);return pixels;
        }
    }

    [TestMethod, TestCategory("GPU")]
    public void FailedUploadCannotLeaveAnOldLightFrameAdvertisedAsReady()
    {
        using var window=Context();using var texture=new LightTexture();var packet=new GpuLightData();
        var registry=new LightRegistry(new(Guid.NewGuid(),0));registry.Upsert(Id(0),new(default,V3.One,EmissionProfile.Steady));
        packet.Update(registry.Capture(1,0),default);texture.Upload(packet);
        registry.Remove(Id(0));packet.Update(registry.Capture(2,1),default);
        GL.Enable((EnableCap)(-1)); // deliberately inject a pre-existing GL error
        Assert.ThrowsException<InvalidOperationException>(()=>texture.Upload(packet));
        Assert.IsFalse(texture.Ready);Assert.IsNull(texture.PublishedFrame);
        texture.Upload(packet);Assert.IsTrue(texture.Ready);Assert.AreEqual(0,texture.PublishedFrame!.Samples.Length);
    }
    private static int MakeProgram()
    {
        string vertex="#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        string fragment="#version 330 core\n"+File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"scene-query.glsl"))+"\n"
            +File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"light-query.glsl"))+"\nlayout(location=0)out vec4 outputColor;void main(){DiffuseDirect q=queryDiffuseDirect(vec3(.2,.3,.4),vec3(1,0,0),vec3(1,0,0),vec3(.5),.0001,256);outputColor=vec4(q.radiance,float(q.unresolved-q.blocked));}";
        int v=Compile(ShaderType.VertexShader,vertex),f=Compile(ShaderType.FragmentShader,fragment),p=GL.CreateProgram();
        GL.AttachShader(p,v);GL.AttachShader(p,f);GL.LinkProgram(p);GL.DeleteShader(v);GL.DeleteShader(f);
        GL.GetProgram(p,GetProgramParameterName.LinkStatus,out int ok);if(ok==0){string error=GL.GetProgramInfoLog(p);GL.DeleteProgram(p);throw new InvalidOperationException(error);}return p;
    }
    private static int Compile(ShaderType type,string source)
    {
        int s=GL.CreateShader(type);GL.ShaderSource(s,source);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);
        if(ok==0){string error=GL.GetShaderInfoLog(s);GL.DeleteShader(s);throw new InvalidOperationException(error);}return s;
    }
}
