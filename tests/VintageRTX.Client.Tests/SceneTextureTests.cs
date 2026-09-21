using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Client;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Scene;

namespace VintageRTX.Client.Tests;

[TestClass,DoNotParallelize]
public sealed class SceneTextureTests
{
    [TestMethod,TestCategory("GPU")]
    public void ActualUploaderPreservesStateSupportsLocalEditsAndFeedsProductionShader()
    {
        GLFWProvider.CheckForMainThread=false;
        using NativeWindow window=new(new NativeWindowSettings {ClientSize=new Vector2i(8,8),StartVisible=false,API=ContextAPI.OpenGL,APIVersion=new Version(3,3),Profile=ContextProfile.Core});
        window.Context.MakeCurrent();GL.LoadBindings(new GLFWBindingsContext());
        using SceneTextureSet target=new();var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();
        var mesh=new BlockMesh(new Triangle[]{new(new(.5,0,0),new(.5,1,0),new(.5,0,1),0)});
        scene.Observe(new(0,0,0),CellGeometry.FromMesh(mesh));data.Update(scene.Capture());
        int sentinel=GL.GenTexture(),pbo=GL.GenBuffer(),fbo=GL.GenFramebuffer(),output=GL.GenTexture(),vao=GL.GenVertexArray(),program=0;
        try
        {
            GL.ActiveTexture(TextureUnit.Texture5);GL.BindTexture(TextureTarget.Texture2D,sentinel);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer,pbo);GL.BufferData(BufferTarget.PixelUnpackBuffer,256,IntPtr.Zero,BufferUsageHint.StaticDraw);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment,8);GL.PixelStore(PixelStoreParameter.UnpackRowLength,11);
            GL.PixelStore(PixelStoreParameter.UnpackSkipRows,2);GL.PixelStore(PixelStoreParameter.UnpackSkipPixels,3);
            target.Upload(data);Assert.IsTrue(target.Ready);
            Assert.AreEqual(sentinel,GL.GetInteger(GetPName.TextureBinding2D));Assert.AreEqual(pbo,GL.GetInteger(GetPName.PixelUnpackBufferBinding));
            Assert.AreEqual(8,GL.GetInteger(GetPName.UnpackAlignment));Assert.AreEqual(11,GL.GetInteger(GetPName.UnpackRowLength));
            Assert.AreEqual(2,GL.GetInteger(GetPName.UnpackSkipRows));Assert.AreEqual(3,GL.GetInteger(GetPName.UnpackSkipPixels));
            Assert.AreEqual((int)TextureUnit.Texture5,GL.GetInteger(GetPName.ActiveTexture));
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer,0);GL.PixelStore(PixelStoreParameter.UnpackAlignment,1);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength,0);GL.PixelStore(PixelStoreParameter.UnpackSkipRows,0);GL.PixelStore(PixelStoreParameter.UnpackSkipPixels,0);
            GL.BindTexture(TextureTarget.Texture2D,target.CellTexture);int[] actual=new int[64*216*4];
            GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.RgbaInteger,PixelType.Int,actual);CollectionAssert.AreEqual(data.CellData.ToArray(),actual);
            int originalRegion=target.RegionTexture,originalCell=target.CellTexture,originalGeometry=target.GeometryTexture;
            scene.Observe(new(1,0,0),CellGeometry.FromMesh(mesh));data.Update(scene.Capture());target.Upload(data);
            Assert.AreEqual(8224L,target.LastUploadBytes);Assert.AreEqual(originalRegion,target.RegionTexture);Assert.AreEqual(originalCell,target.CellTexture);Assert.AreEqual(originalGeometry,target.GeometryTexture);
            program=MakeProgram();GL.UseProgram(program);GL.Uniform3(GL.GetUniformLocation(program,"sceneAnchor"),0,0,0);
            GL.BindTexture(TextureTarget.Texture2D,output);GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);Assert.AreEqual(FramebufferErrorCode.FramebufferComplete,GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            GL.Viewport(0,0,1,1);GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.Blend);GL.BindVertexArray(vao);
            Bind();float[] hit=Draw();Assert.AreEqual(1f,hit[0]);Assert.AreEqual(.4f,hit[1],1e-5f);
            scene.Observe(new(0,0,0),CellGeometry.Empty);data.Update(scene.Capture());target.Upload(data);Bind();Assert.AreEqual(0f,Draw()[0]);
            scene.Reset(new(Guid.NewGuid(),1));data.Update(scene.Capture());target.Upload(data);Bind();Assert.AreEqual(2f,Draw()[0]);
            Assert.AreEqual(program,GL.GetInteger(GetPName.CurrentProgram));Assert.AreEqual(fbo,GL.GetInteger(GetPName.DrawFramebufferBinding));
            Assert.AreEqual(ErrorCode.NoError,GL.GetError());
        }
        finally
        {
            GL.UseProgram(0);GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.BindBuffer(BufferTarget.PixelUnpackBuffer,0);
            if(program!=0)GL.DeleteProgram(program);GL.DeleteVertexArray(vao);GL.DeleteTexture(output);GL.DeleteTexture(sentinel);GL.DeleteBuffer(pbo);GL.DeleteFramebuffer(fbo);
        }
        void Bind()
        {
            int[] textures={target.RegionTexture,target.CellTexture,target.GeometryTexture};string[] names={"regionData","cellData","geometryData"};
            for(int i=0;i<3;i++){GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0+i));GL.BindTexture(TextureTarget.Texture2D,textures[i]);GL.Uniform1(GL.GetUniformLocation(program,names[i]),i);}
        }
        float[] Draw() { GL.DrawArrays(PrimitiveType.Triangles,0,3);float[] pixel=new float[4];GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);return pixel; }
    }
    private static int MakeProgram()
    {
        string vertex="#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        string fragment="#version 330 core\n"+File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"scene-query.glsl"))+"\nlayout(location=0)out vec4 result;void main(){SceneQuery q=traceScene(vec3(.1,.2,.2),vec3(1,0,0),0.,.8,256);result=vec4(float(q.status),q.distance,q.normal.xy);}";
        int v=Compile(ShaderType.VertexShader,vertex),f=Compile(ShaderType.FragmentShader,fragment),p=GL.CreateProgram();
        GL.AttachShader(p,v);GL.AttachShader(p,f);GL.LinkProgram(p);GL.DeleteShader(v);GL.DeleteShader(f);
        GL.GetProgram(p,GetProgramParameterName.LinkStatus,out int ok);if(ok==0){string error=GL.GetProgramInfoLog(p);GL.DeleteProgram(p);throw new InvalidOperationException(error);}return p;
    }
    private static int Compile(ShaderType type,string source)
    { int s=GL.CreateShader(type);GL.ShaderSource(s,source);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0){string error=GL.GetShaderInfoLog(s);GL.DeleteShader(s);throw new InvalidOperationException(error);}return s; }
}
