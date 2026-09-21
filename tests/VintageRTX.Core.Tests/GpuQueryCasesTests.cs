using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class GpuQueryCasesTests
{
    private static BlockMesh Layers()
    {
        var triangles=new List<Triangle>();
        for(int i=0;i<64;i++)
        {
            double x=(i+.5)/64;
            triangles.Add(new(new(x,0,0),new(x,1,0),new(x,0,1),0));
            triangles.Add(new(new(x,1,0),new(x,1,1),new(x,0,1),0));
        }
        return new(triangles.ToArray());
    }
    private static CellScene KnownRegion(RegionId region)
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));
        for(int z=0;z<8;z++)for(int y=0;y<8;y++)for(int x=0;x<8;x++)
            scene.Observe(new(region.X*8+x,region.Y*8+y,region.Z*8+z),CellGeometry.Empty);
        return scene;
    }
    [TestMethod] public void RegionalQueriesMatchExhaustiveWorldTriangles()
    {
        var scene=KnownRegion(new(0,0,0));var mesh=Layers();scene.Observe(new(3,3,3),CellGeometry.FromMesh(mesh));
        var slope=new BlockMesh(new Triangle[]{new(new(0,.1,0),new(1,.9,0),new(0,.1,1),0)});
        scene.Observe(new(4,3,3),CellGeometry.FromMesh(slope));var frame=scene.Capture();var random=new Random(24351);
        for(int i=0;i<1000;i++)
        {
            Ray ray=i%2==0?new(new(2.1,2.7+random.NextDouble()*1.7,2.7+random.NextDouble()*1.7),new(1,0,0),0,3)
                :new(new(5.1,2.7+random.NextDouble()*1.7,2.7+random.NextDouble()*1.7),new(-1,0,0),0,3);
            SurfaceHit best=default;bool found=false;double distance=ray.Maximum;
            foreach(var pair in new[]{(Cell:new CellId(3,3,3),Mesh:mesh),(Cell:new CellId(4,3,3),Mesh:slope)})
            {
                int primitive=0;
                foreach(Triangle t in pair.Mesh.Triangles)
                {
                    Triangle world=new(t.A+pair.Cell.Position,t.B+pair.Cell.Position,t.C+pair.Cell.Position,t.Material);
                    if(world.Intersect(ray,distance,primitive++,out SurfaceHit h)) { found=true;distance=h.Distance;best=h; }
                }
            }
            SceneTrace actual=CellSceneTracer.Trace(frame,ray);
            Assert.AreEqual(found?SceneTraceStatus.Hit:SceneTraceStatus.Clear,actual.Status);
            if(found) { Assert.AreEqual(best.Distance,actual.Distance,1e-10);Assert.IsTrue((best.GeometricNormal-actual.Hit.GeometricNormal).Length<1e-10); }
        }
    }
    [TestMethod] public void ExportExactProductionPacketsForGpuQualification()
    {
        var cases=new List<object>();var mesh=Layers();var scene=KnownRegion(new(0,0,0));var gpu=new GpuSceneData();
        scene.Observe(new(3,3,3),CellGeometry.FromMesh(mesh));var random=new Random(13517);var rays=new List<(Ray Ray,int Budget)>();
        for(int i=0;i<512;i++)
        {
            double y=2.5+random.NextDouble()*2,z=2.5+random.NextDouble()*2;
            rays.Add((i%2==0?new(new(2.1,y,z),new(1,0,0),0,3):new(new(5.1,y,z),new(-1,0,0),0,3),256));
        }
        rays.Add((new(new(0,.3,.3),new(-1,0,0),0,1),256));
        rays.Add((new(new(.5,.5,.5),new(1,1,1),0,2),256));
        rays.Add((new(new(.5,.5,.5),new(1,1,1),0,2),1));
        rays.Add((new(new(.5,.5,.5),new(1,0,0),0,2),0));
        Add("hierarchy",scene,gpu,new(0,0,0),rays);
        scene.Observe(new(3,3,3),CellGeometry.Empty);Add("source-cell-removed",scene,gpu,new(0,0,0),rays);
        scene.Observe(new(3,3,3),CellGeometry.Unsupported);Add("unresolved-provider",scene,gpu,new(0,0,0),rays);
        scene.Invalidate(new(0,0,0));scene.Observe(new(24,0,0),CellGeometry.FromMesh(mesh));
        Add("ring-tag-reuse",scene,gpu,new(24,0,0),new(){(new(new(24.001,.3,.3),new(1,0,0),0,.9),256),(new(new(.1,.3,.3),new(1,0,0),0,.8),256)});
        scene.Reset(new(Guid.NewGuid(),1));scene.Observe(new(1000000000,0,0),CellGeometry.FromMesh(mesh));
        Add("large-integer-world",scene,gpu,new(1000000000,0,0),new(){(new(new(1000000000.001,.3,.3),new(1,0,0),0,.9),256)});
        var negative=KnownRegion(new(-1,-1,-1));negative.Observe(new(-4,-4,-4),CellGeometry.FromMesh(mesh));
        Add("negative-world",negative,new(),new(-8,-8,-8),new(){(new(new(-5.2,-3.7,-3.7),new(1,0,0),0,2),256),(new(new(0,-.5,-.5),new(-1,0,0),0,.8),256)});
        string path=Environment.GetEnvironmentVariable("VINTAGERTX_GPU_CASES")??Path.Combine(AppContext.BaseDirectory,"scene-gpu-cases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path,JsonSerializer.Serialize(cases));Assert.AreEqual(6,cases.Count);
        void Add(string name,CellScene source,GpuSceneData data,CellId anchor,List<(Ray Ray,int Budget)> queries)
        {
            CellSceneFrame frame=source.Capture();data.Update(frame);
            object[] records=queries.Select(item=>
            {
                var origin=item.Ray.Origin.RelativeTo(anchor.Position);var direction=item.Ray.Direction.RelativeTo(DVec3.Zero);
                Ray quantized=new(anchor.Position+new DVec3(origin.X,origin.Y,origin.Z),new(direction.X,direction.Y,direction.Z),(float)item.Ray.Minimum,(float)item.Ray.Maximum);
                SceneTrace result=CellSceneTracer.Trace(frame,quantized,item.Budget);
                return (object)new {Origin=new[]{origin.X,origin.Y,origin.Z,(float)quantized.Minimum},Direction=new[]{direction.X,direction.Y,direction.Z,(float)quantized.Maximum},Budget=item.Budget,
                    Status=(int)result.Status,Distance=result.Distance,Primitive=result.Status==SceneTraceStatus.Hit?result.Hit.Primitive:-1,
                    Normal=new[]{result.Hit.GeometricNormal.X,result.Hit.GeometricNormal.Y,result.Hit.GeometricNormal.Z}};
            }).ToArray();
            cases.Add(new{Name=name,Anchor=new[]{anchor.X,anchor.Y,anchor.Z},Regions=data.RegionData.ToArray(),Cells=data.CellData.ToArray(),Geometry=data.GeometryData.ToArray(),GeometryHeight=data.GeometryHeight,Rays=records});
        }
    }
}
