using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;

namespace VintageRTX.Client.Tests;

public sealed partial class NativeWorldLightingTests
{
    private static WorldSurfaceMaterial Copper(double roughness=.32) => new(new SurfaceMaterial(
        SurfaceKind.Conductor,Vector3.One,roughness,new(.2f,.92f,1.1f),new(3.91f,2.45f,2.14f)));
    private sealed class MaterialWorld : IDisposable
    {
        internal readonly CellScene Scene=new(new(Guid.NewGuid(),0));
        internal readonly LightRegistry Lights;
        internal readonly SceneTextureSet Geometry=new();
        private readonly LightTexture emission=new();
        private readonly GpuSceneData sceneData=new();
        private readonly GpuLightData lightData=new();
        private long frame;
        internal MaterialWorld()
        {
            for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)Scene.Observe(new(x,y,z),CellGeometry.Empty);
            Lights=new(Scene.Capture().World);
            Lights.Upsert(default,new(new(3.5,3.5,4),new(8,8,8),EmissionProfile.Steady));
        }
        internal void Surface(WorldSurfaceMaterial? material)
        {
            // True raster receiver is at z=2; its block is immediately behind it, z=1.
            var cell=CellGeometry.Unsupported.WithSurface(material);
            Scene.Observe(new(3,3,1),cell);
        }
        internal WorldGpuFrame Publish()
        {
            var scene=Scene.Capture();if(sceneData.Update(scene))Geometry.Upload(sceneData);
            var lights=Lights.Capture(++frame,frame);lightData.Update(lights,default);emission.Upload(lightData);
            return new(scene,lights,default,Geometry.RegionTexture,Geometry.CellTexture,Geometry.GeometryTexture,
                emission.Texture,Geometry.MaterialTexture);
        }
        public void Dispose(){Geometry.Dispose();emission.Dispose();}
    }
    [TestMethod]
    public void AuthoredConductorReachesActualTerrainShaderAndMatchesCpuBsdf()
    {
        using var gl=PortableGlContext.Create();using var world=new MaterialWorld();using var draw=new NativeProbe();
        var surface=Copper();world.Surface(surface);var frame=world.Publish();
        float[] lit=draw.Render(frame,1,flags:1<<11);
        var receiver=new DVec3(3.5625,3.5625,2);var n=new DVec3(0,0,1);
        var toLight=new DVec3(3.5,3.5,4)-receiver;
        var outgoing=(new DVec3(3.5,3.5,5)-receiver).Normalized();
        Vector3 expected=Bsdf.Evaluate(surface.Material!,n,outgoing,toLight.Normalized())
            *(float)(DVec3.Dot(n,toLight.Normalized())/toLight.LengthSquared)*new Vector3(8);
        expected=new(ColorSpace.Encode(expected.X),ColorSpace.Encode(expected.Y),ColorSpace.Encode(expected.Z));
        Assert.AreEqual(expected.X,lit[0],.0003f);Assert.AreEqual(expected.Y,lit[1],.0003f);Assert.AreEqual(expected.Z,lit[2],.0003f);
        Assert.IsTrue(lit[0]>lit[1] && lit[1]>lit[2],"Neutral incident light must retain the conductor's optical RGB order.");
        Assert.IsTrue(lit[0]>1,"HDR must not be clipped to the display gamut.");
        CollectionAssert.AreEqual(lit,draw.Render(frame,1,flags:1<<11,blueBaked:true));
        long bytes=world.Geometry.TotalUploadBytes;
        world.Lights.Remove(default);var off=draw.Render(world.Publish(),1,flags:1<<11);
        Assert.IsTrue(off.Take(3).All(v=>v==0));Assert.AreEqual(bytes,world.Geometry.TotalUploadBytes);
        if(Environment.GetEnvironmentVariable("VINTAGERTX_WORLD_EVIDENCE") is {Length:>0} directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"native-material-pixels.json"),System.Text.Json.JsonSerializer.Serialize(new {
                scope="Actual native terrain shader, controlled rasterized mesh and production GPU packet; NOT an in-game capture",
                material="authored copper",roughness=surface.Material!.Roughness,
                expectedEncodedRgb=new[]{expected.X,expected.Y,expected.Z},actualRgba=lit,extinguishedRgba=off,
                geometryBytesBeforeExtinction=bytes,geometryBytesAfterExtinction=world.Geometry.TotalUploadBytes,
                shinyModesIndependentlyTested=6
            },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
    }
    [TestMethod]
    public void LocalMaterialChangeDoesNotBleedIntoEntitiesOrDroppedItems()
    {
        using var gl=PortableGlContext.Create();using var world=new MaterialWorld();using var draw=new NativeProbe();
        world.Surface(null);var before=world.Publish();float[] native=draw.Render(before,0,flags:1<<11);
        CollectionAssert.AreEqual(native,draw.Render(before,1,flags:1<<11),"Reflective bits alone cannot select a metal.");
        var recipients=Enumerable.Range(1,3).Select(i=>draw.Render(before,1,programIndex:i)).ToArray();
        world.Surface(Copper());var copper=world.Publish();
        Assert.AreEqual(24608L,world.Geometry.LastUploadBytes,"Only the edited cell-region packet, not geometry templates, is transferred.");
        for(int i=1;i<4;i++)CollectionAssert.AreEqual(recipients[i-1],draw.Render(copper,1,programIndex:i));
        float[] colored=draw.Render(copper,1,flags:1<<11);
        world.Surface(new(new SurfaceMaterial(SurfaceKind.Conductor,Vector3.One,.6,new(.16f,.12f,.14f),new(4.83f,3.12f,2.15f))));
        float[] silver=draw.Render(world.Publish(),1,flags:1<<11);
        Assert.IsTrue(Math.Abs(colored[0]-silver[0])>.01f);
        world.Surface(new((SurfaceMaterial?)null));var explicitNative=world.Publish();
        CollectionAssert.AreEqual(draw.Render(explicitNative,0),draw.Render(explicitNative,1),"Explicit native override is not the absent/default diffuse material.");
        world.Surface(Copper(0));var mirror=world.Publish();
        CollectionAssert.AreEqual(draw.Render(mirror,0,flags:1<<11),draw.Render(mirror,1,flags:1<<11),"An ideal mirror is not approximated with an arbitrary nonzero roughness.");
    }
    [TestMethod]
    public void NativeShinyModeDoesNotModulateAnAlreadyEvaluatedConductor()
    {
        using var gl=PortableGlContext.Create();using var world=new MaterialWorld();using var draw=new NativeProbe(shiny:1);
        world.Surface(Copper());var frame=world.Publish();
        using(var reference=new NativeProbe(shiny:0))
            for(int mode=0;mode<=5;mode++)
            {
                int flags=(1<<11)|(mode<<29);
                CollectionAssert.AreEqual(reference.Render(frame,1,flags:flags),draw.Render(frame,1,flags:flags),
                    "Native artistic reflection mode must not tint/boost/whiten the GGX response a second time.");
            }
        float[] red=draw.Render(frame,1,flags:1<<11),blue=draw.Render(frame,1,flags:1<<11,blueBaked:true);
        CollectionAssert.AreEqual(red,blue,"Native vertex light must not be reintroduced by the shiny path.");
        world.Surface(null);var fallback=world.Publish();
        CollectionAssert.AreEqual(draw.Render(fallback,0,flags:1<<11),draw.Render(fallback,1,flags:1<<11));
        Assert.AreEqual(ErrorCode.NoError,GL.GetError());
    }
    [TestMethod]
    public void MissingOrMalformedSurfacePacketDoesNotEnableReflectiveReplacement()
    {
        using var gl=PortableGlContext.Create();using var world=new MaterialWorld();using var draw=new NativeProbe();
        world.Surface(Copper());var frame=world.Publish();float[] native=draw.Render(frame,0,flags:1<<11);
        CollectionAssert.AreEqual(native,draw.Render(frame with {Materials=0},1,flags:1<<11));
        int bad=GL.GenTexture();
        try {
            GL.BindTexture(TextureTarget.Texture2D,bad);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,new float[]{1,1,1,2});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            CollectionAssert.AreEqual(native,draw.Render(frame with {Materials=bad},1,flags:1<<11));
        } finally {GL.DeleteTexture(bad);}
        Assert.AreEqual(ErrorCode.NoError,GL.GetError());
    }
}
