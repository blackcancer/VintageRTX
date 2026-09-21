using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Client;
using VintageRTX.Core.Diagnostics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;
using V3 = System.Numerics.Vector3;
using GlError = OpenTK.Graphics.OpenGL4.ErrorCode;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class DirectImagePassTests
{
    private static NativeWindow Context()
    {
        GLFWProvider.CheckForMainThread = false;
        var w = new NativeWindow(new NativeWindowSettings { ClientSize = new(128,96), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new(3,3), Profile = ContextProfile.Core });
        w.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return w;
    }
    private static DirectImagePass Pass() => new(Read("scene-query.glsl"),Read("light-query.glsl"),Read("material-query.glsl"),Read("direct-image.glsl"));
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,name));
    private static CellScene Scene()
    {
        var s = new CellScene(new(Guid.NewGuid(),0));
        for (int z=0;z<8;z++) for(int y=0;y<8;y++) for(int x=0;x<8;x++) s.Observe(new(x,y,z),CellGeometry.Empty);
        return s;
    }
    private static DirectSurfaceFrame Surfaces(CellScene scene, int width=1, int height=1)
    {
        SurfaceMaterial[] material = [new(SurfaceKind.Diffuse,new(.5f,.7f,.9f)),
            new(SurfaceKind.Conductor,V3.Zero,.22,new(.2f,.85f,1.2f),new(3.2f,2.8f,2.5f))];
        var pixels = new SurfaceReceiver[width*height];
        for(int i=0;i<pixels.Length;i++) pixels[i]=new(new(1.25,2.5,3.75),new(0,0,1),new(0,0,1),V3.One,i%2);
        return new(scene.Capture(),default,new(1.25,2.5,7),width,height,pixels,material);
    }
    private static LightRegistry Registry(DirectSurfaceFrame surfaces, bool finite=false)
    {
        var r = new LightRegistry(surfaces.World);
        r.Upsert(default,new(new(1.25,2.5,5.75),new(8,4,2),EmissionProfile.Steady,radius:finite?.4:0));
        return r;
    }
    private static void UploadGeometry(DirectSurfaceFrame surfaces,SceneTextureSet gpu)
    { var data=new GpuSceneData();data.Update(surfaces.Scene);gpu.Upload(data); }
    private static void UploadLight(LightFrame frame,LightTexture gpu,CellId anchor=default)
    { var data=new GpuLightData();data.Update(frame,anchor);gpu.Upload(data); }
    private static float[] Pixels(int texture,int width,int height)
    {
        int previous=GL.GetInteger(GetPName.TextureBinding2D);
        try { GL.BindTexture(TextureTarget.Texture2D,texture);float[] p=new float[width*height*4];GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.Float,p);return p; }
        finally { GL.BindTexture(TextureTarget.Texture2D,previous); }
    }
    private static void Match(DirectImagePass pass,DirectSurfaceFrame surfaces,LightFrame frame,int samples=8)
    {
        float[] pixels=Pixels(pass.RadianceTexture,surfaces.Width,surfaces.Height);
        float[] diagnostics=Pixels(pass.DiagnosticTexture,surfaces.Width,surfaces.Height);
        double maximumError=0;
        for(int i=0;i<surfaces.Receivers.Length;i++)
        {
            DirectLightingResult expected=DirectLightingReference.Evaluate(surfaces,i,frame,samples);
            V3 actual=new(pixels[i*4],pixels[i*4+1],pixels[i*4+2]);
            double error=V3.Distance(actual,expected.Radiance)/Math.Max(1,expected.Radiance.Length());maximumError=Math.Max(maximumError,error);
            Assert.IsTrue(error<0.0004,$"pixel {i}: {actual} != {expected.Radiance}, normalized error {error}");
            Assert.AreEqual((float)expected.Unresolved,diagnostics[i*4],$"unresolved pixel {i}");
            Assert.AreEqual((float)expected.Blocked,diagnostics[i*4+1],$"blocked pixel {i}");
            Assert.AreEqual((float)expected.Traced,diagnostics[i*4+2],$"traced pixel {i}");
        }
        Console.WriteLine("Direct image maximum normalized RGB error: {0:R}",maximumError);
    }

    [TestMethod,TestCategory("GPU")]
    public void FullLabImageMatchesCpuTrianglesMaterialsAndFiniteSourceReference()
    {
        using var window=Context();using var geometry=new SceneTextureSet();using var lights=new LightTexture();using var pass=Pass();
        DirectSurfaceFrame surfaces=DirectLightLab.Create(40,24);UploadGeometry(surfaces,geometry);
        var registry=new LightRegistry(surfaces.World);
        DirectLightLab.SetLights(registry,surfaces.Anchor,new(null,null,EmissionProfile.Candle,ComponentCount:3),true);
        LightFrame frame=registry.Capture(1,3.25);UploadLight(frame,lights);
        pass.Render(surfaces,geometry,lights);Assert.IsTrue(pass.Ready);Match(pass,surfaces,frame);
        float[] raw=Pixels(pass.RadianceTexture,40,24);float[] mask=Pixels(pass.DiagnosticTexture,40,24);
        Assert.IsTrue(raw.Where((_,i)=>i%4!=3).Any(x=>x>0));
        Assert.IsTrue(mask.Where((_,i)=>i%4==1).Any(x=>x>0));
        Assert.IsTrue(mask.Where((_,i)=>i%4==0).All(x=>x==0));
        string? output=Environment.GetEnvironmentVariable("VINTAGERTX_DIRECT_EVIDENCE");
        if(!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            using var stream=File.Create(Path.Combine(output,"direct-hdr-rgba32f.bin"));using var writer=new BinaryWriter(stream);
            foreach(float value in raw)writer.Write(value);
            File.WriteAllText(Path.Combine(output,"direct-image.json"),System.Text.Json.JsonSerializer.Serialize(new {
                width=40,height=24,encoding="RGBA32F little-endian, row 0 at bottom",scene="synthetic direct-light lab, not game acceptance",sources=frame.Samples.Length}));
            WritePpm(Path.Combine(output,"direct-preview.ppm"),Pixels(pass.PreviewTexture,40,24),40,24);
        }
        Assert.AreEqual(GlError.NoError,GL.GetError());
    }
    private static void WritePpm(string path,float[] rgba,int width,int height)
    {
        using var file=File.Create(path);byte[] header=System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");file.Write(header);
        for(int y=height-1;y>=0;y--)for(int x=0;x<width;x++)for(int c=0;c<3;c++)
            file.WriteByte((byte)Math.Clamp((int)Math.Round(rgba[(y*width+x)*4+c]*255),0,255));
    }
    [TestMethod,TestCategory("GPU")]
    public void DirectHdrDoesNotDependOnPreviewExposureOrPreviouslyLitFrames()
    {
        using var window=Context();using var geometry=new SceneTextureSet();using var lights=new LightTexture();using var pass=Pass();
        var surfaces=Surfaces(Scene(),4,2);UploadGeometry(surfaces,geometry);var registry=Registry(surfaces);
        LightFrame frame=registry.Capture(1,0);UploadLight(frame,lights);pass.Render(surfaces,geometry,lights);
        float[] before=Pixels(pass.RadianceTexture,4,2),preview=Pixels(pass.PreviewTexture,4,2);
        long geometryBytes=geometry.TotalUploadBytes;
        pass.Render(surfaces,geometry,lights,previewExposure:3);
        CollectionAssert.AreEqual(before,Pixels(pass.RadianceTexture,4,2));
        Assert.IsFalse(preview.SequenceEqual(Pixels(pass.PreviewTexture,4,2)));
        Assert.AreEqual(0L,pass.LastSurfaceUploadBytes);Assert.AreEqual(geometryBytes,geometry.TotalUploadBytes);
        Match(pass,surfaces,frame);Assert.IsTrue(before.Any(x=>x>1));
        registry.Remove(default);LightFrame dark=registry.Capture(2,.01);UploadLight(dark,lights);pass.Render(surfaces,geometry,lights);
        Assert.IsTrue(Pixels(pass.RadianceTexture,4,2).Where((_,i)=>i%4!=3).All(x=>x==0));
        Assert.IsTrue(Pixels(pass.DiagnosticTexture,4,2).Where((_,i)=>i%4!=3).All(x=>x==0));
        Assert.AreSame(dark,pass.PublishedLights);Assert.AreEqual(0L,pass.LastSurfaceUploadBytes);
    }
    [TestMethod,TestCategory("GPU")]
    public void GeometryRevisionWorldAndAnchorMismatchesCannotPublishOldOutput()
    {
        using var window=Context();using var geometry=new SceneTextureSet();using var lights=new LightTexture();using var pass=Pass();
        var scene=Scene();var surfaces=Surfaces(scene);UploadGeometry(surfaces,geometry);var registry=Registry(surfaces);
        LightFrame first=registry.Capture(1,0);UploadLight(first,lights);pass.Render(surfaces,geometry,lights);
        scene.Observe(new(7,7,7),CellGeometry.Unknown);var next=Surfaces(scene);UploadGeometry(next,geometry);
        Assert.ThrowsException<InvalidOperationException>(()=>pass.Render(surfaces,geometry,lights));
        Assert.IsFalse(pass.Ready);Assert.AreEqual(0,pass.RadianceTexture);Assert.IsNull(pass.PublishedLights);
        pass.Render(next,geometry,lights);Assert.IsTrue(pass.Ready);
        UploadLight(first,lights,new(8,0,0));Assert.ThrowsException<InvalidOperationException>(()=>pass.Render(next,geometry,lights));
        Assert.IsFalse(pass.Ready);
        var foreign=new LightRegistry(new(Guid.NewGuid(),0));UploadLight(foreign.Capture(1,0),lights);
        Assert.ThrowsException<InvalidOperationException>(()=>pass.Render(next,geometry,lights));Assert.IsFalse(pass.Ready);
        UploadLight(first,lights);pass.Render(next,geometry,lights);Assert.IsTrue(pass.Ready);
    }
    [TestMethod,TestCategory("GPU")]
    public void UnknownCoverageIsMeasuredSeparatelyAndNeverBrightenedAsClear()
    {
        using var window=Context();using var geometry=new SceneTextureSet();using var lights=new LightTexture();using var pass=Pass();
        var scene=Scene();scene.Observe(new(1,2,4),CellGeometry.Unknown);var surfaces=Surfaces(scene,2,1);
        UploadGeometry(surfaces,geometry);var registry=Registry(surfaces,true);LightFrame frame=registry.Capture(1,0);UploadLight(frame,lights);
        pass.Render(surfaces,geometry,lights,64);Match(pass,surfaces,frame,64);
        Assert.IsTrue(Pixels(pass.RadianceTexture,2,1).Where((_,i)=>i%4!=3).All(x=>x==0));
        float[] masks=Pixels(pass.DiagnosticTexture,2,1);Assert.AreEqual(64f,masks[0]);Assert.AreEqual(2f,masks[3]);
        float[] visible=Pixels(pass.PreviewTexture,2,1);Assert.AreEqual(1f,visible[0]);Assert.AreEqual(0f,visible[1]);Assert.AreEqual(1f,visible[2]);
    }
    [TestMethod,TestCategory("GPU")]
    public void ResizeReallocatesOnlyOwnedTargetsAndFailureIsRecoverable()
    {
        using var window=Context();using var geometry=new SceneTextureSet();using var lights=new LightTexture();using var pass=Pass();
        var scene=Scene();var small=Surfaces(scene,3,2);UploadGeometry(small,geometry);var registry=Registry(small);
        LightFrame frame=registry.Capture(1,0);UploadLight(frame,lights);pass.Render(small,geometry,lights);
        int name=pass.PreviewTexture;var large=Surfaces(scene,7,5);pass.Render(large,geometry,lights);
        Assert.AreEqual(7,pass.Width);Assert.AreEqual(5,pass.Height);Assert.AreEqual(name,pass.PreviewTexture);Match(pass,large,frame);
        GL.Enable((EnableCap)(-1));Assert.ThrowsException<InvalidOperationException>(()=>pass.Render(large,geometry,lights));
        Assert.IsFalse(pass.Ready);Assert.AreEqual(0,pass.PreviewTexture);
        pass.Render(large,geometry,lights);Assert.IsTrue(pass.Ready);Assert.AreEqual(GlError.NoError,GL.GetError());
    }
    [TestMethod,TestCategory("GPU")]
    public void ForeignGlStateCannotChangeTheImageAndIsRestoredIncludingIndexedMasks()
    {
        using var window=Context();using var geometry=new SceneTextureSet();using var lights=new LightTexture();using var pass=Pass();
        var surfaces=Surfaces(Scene(),4,2);UploadGeometry(surfaces,geometry);var registry=Registry(surfaces);
        LightFrame frame=registry.Capture(1,0);UploadLight(frame,lights);
        int texture=GL.GenTexture(),sampler=GL.GenSampler(),pbo=GL.GenBuffer(),vao=GL.GenVertexArray();
        int draw=GL.GenFramebuffer(),read=GL.GenFramebuffer();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D,texture);GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,8,8,0,PixelFormat.Rgba,PixelType.UnsignedByte,new byte[8*8*4]);
            foreach(int fbo in new[]{draw,read})
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            }
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,draw);GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,read);
            GL.Viewport(3,4,8,7);GL.Enable(EnableCap.DepthTest);GL.Enable(EnableCap.ScissorTest);GL.Scissor(0,0,0,0);
            GL.Enable(EnableCap.RasterizerDiscard);GL.Enable(EnableCap.FramebufferSrgb);GL.PolygonMode(TriangleFace.FrontAndBack,PolygonMode.Line);
            GL.Enable(IndexedEnableCap.Blend,0);GL.Disable(IndexedEnableCap.Blend,1);GL.Enable(IndexedEnableCap.Blend,2);
            GL.ColorMask(0,false,true,false,true);GL.ColorMask(1,true,false,true,false);GL.ColorMask(2,false,false,false,false);
            GL.ActiveTexture(TextureUnit.Texture6);GL.BindTexture(TextureTarget.Texture2D,texture);GL.BindSampler(6,sampler);GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer,pbo);GL.BufferData(BufferTarget.PixelUnpackBuffer,2048,IntPtr.Zero,BufferUsageHint.StaticDraw);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment,8);GL.PixelStore(PixelStoreParameter.UnpackRowLength,17);
            GL.PixelStore(PixelStoreParameter.UnpackSkipRows,2);GL.PixelStore(PixelStoreParameter.UnpackSkipPixels,3);GL.PixelStore(PixelStoreParameter.UnpackSwapBytes,1);
            pass.Render(surfaces,geometry,lights);Match(pass,surfaces,frame);
            Assert.AreEqual(draw,GL.GetInteger(GetPName.DrawFramebufferBinding));Assert.AreEqual(read,GL.GetInteger(GetPName.ReadFramebufferBinding));
            Assert.AreEqual((int)TextureUnit.Texture6,GL.GetInteger(GetPName.ActiveTexture));Assert.AreEqual(texture,GL.GetInteger(GetPName.TextureBinding2D));
            Assert.AreEqual(sampler,GL.GetInteger(GetPName.SamplerBinding));Assert.AreEqual(vao,GL.GetInteger(GetPName.VertexArrayBinding));
            Assert.AreEqual(pbo,GL.GetInteger(GetPName.PixelUnpackBufferBinding));Assert.AreEqual(1,GL.GetInteger(GetPName.UnpackSwapBytes));
            Assert.AreEqual(17,GL.GetInteger(GetPName.UnpackRowLength));Assert.IsTrue(GL.IsEnabled(EnableCap.RasterizerDiscard));
            Assert.IsTrue(GL.IsEnabled(EnableCap.ScissorTest));Assert.IsTrue(GL.IsEnabled(EnableCap.FramebufferSrgb));
            Assert.IsTrue(GL.IsEnabled(IndexedEnableCap.Blend,0));Assert.IsFalse(GL.IsEnabled(IndexedEnableCap.Blend,1));Assert.IsTrue(GL.IsEnabled(IndexedEnableCap.Blend,2));
            bool[] mask=new bool[4];GL.GetBoolean(GetIndexedPName.ColorWritemask,0,mask);CollectionAssert.AreEqual(new[]{false,true,false,true},mask);
            GL.GetBoolean(GetIndexedPName.ColorWritemask,1,mask);CollectionAssert.AreEqual(new[]{true,false,true,false},mask);
            GL.GetBoolean(GetIndexedPName.ColorWritemask,2,mask);Assert.IsTrue(mask.All(x=>!x));
            int[] viewport=new int[4];GL.GetInteger(GetPName.Viewport,viewport);CollectionAssert.AreEqual(new[]{3,4,8,7},viewport);
            Assert.IsTrue(Pixels(texture,8,8).All(x=>x==0));Assert.AreEqual(GlError.NoError,GL.GetError());
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.BindBuffer(BufferTarget.PixelUnpackBuffer,0);GL.BindSampler(6,0);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment,4);GL.PixelStore(PixelStoreParameter.UnpackRowLength,0);GL.PixelStore(PixelStoreParameter.UnpackSkipRows,0);
            GL.PixelStore(PixelStoreParameter.UnpackSkipPixels,0);GL.PixelStore(PixelStoreParameter.UnpackSwapBytes,0);
            GL.Disable(EnableCap.RasterizerDiscard);GL.DeleteTexture(texture);GL.DeleteSampler(sampler);GL.DeleteBuffer(pbo);
            GL.DeleteVertexArray(vao);GL.DeleteFramebuffer(draw);GL.DeleteFramebuffer(read);
        }
    }
    [TestMethod,TestCategory("GPU")]
    public void SceneUploadDisablesByteSwappingWithoutChangingTheHostState()
    {
        using var window=Context();using var geometry=new SceneTextureSet();var data=new GpuSceneData();data.Update(Scene().Capture());
        GL.PixelStore(PixelStoreParameter.UnpackSwapBytes,1);
        try
        {
            geometry.Upload(data);Assert.AreEqual(1,GL.GetInteger(GetPName.UnpackSwapBytes));Assert.AreSame(data.SourceFrame,geometry.PublishedFrame);
            int[] actual=new int[data.RegionData.Length];GL.BindTexture(TextureTarget.Texture2D,geometry.RegionTexture);
            GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.RgbaInteger,PixelType.Int,actual);CollectionAssert.AreEqual(data.RegionData.ToArray(),actual);
        }
        finally { GL.PixelStore(PixelStoreParameter.UnpackSwapBytes,0); }
    }
}
