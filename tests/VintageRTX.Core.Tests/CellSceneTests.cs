using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class CellSceneTests
{
    private static WorldId World()=>new(Guid.NewGuid(),0);
    private static BlockMesh Plane(double x=.5) => new(new Triangle[]{new(new(x,0,0),new(x,1,0),new(x,0,1),0)});
    [TestMethod] public void FirstEditedCellPublishesWithoutWaitingForAnyOtherCell()
    {
        var scene=new CellScene(World());scene.Observe(new(0,0,0),CellGeometry.FromMesh(Plane()));
        var frame=scene.Capture(); Assert.AreEqual(CellState.Mesh,frame.At(new(0,0,0)).State);
        Assert.AreEqual(CellState.Unknown,frame.At(new(1,0,0)).State);Assert.AreEqual(1,frame.RegionCount);
        Assert.AreSame(frame,scene.Capture());
    }
    [TestMethod] public void OldFramesStayImmutableAndUnchangedRegionsRetainIdentity()
    {
        var scene=new CellScene(World());scene.Observe(new(0,0,0),CellGeometry.Empty);scene.Observe(new(8,0,0),CellGeometry.Empty);
        var a=scene.Capture();scene.Observe(new(0,0,0),CellGeometry.Unsupported);var b=scene.Capture();
        Assert.AreEqual(CellState.Empty,a.At(new(0,0,0)).State);Assert.AreEqual(CellState.Unsupported,b.At(new(0,0,0)).State);
        Assert.AreSame(a.Regions.Single(r=>r.Id.X==1),b.Regions.Single(r=>r.Id.X==1));
    }
    [TestMethod] public void InvalidationAndResetCannotLeakPreviousWorldGeometry()
    {
        var scene=new CellScene(World());scene.Observe(new(-1,-8,-9),CellGeometry.Empty);var a=scene.Capture();
        scene.Invalidate(new(-1,-1,-2));Assert.AreEqual(CellState.Unknown,scene.Capture().At(new(-1,-8,-9)).State);
        Assert.AreEqual(CellState.Empty,a.At(new(-1,-8,-9)).State);
        WorldId other=new(Guid.NewGuid(),1);scene.Reset(other);Assert.AreEqual(other,scene.Capture().World);Assert.AreEqual(0,scene.Capture().RegionCount);
    }
    [TestMethod] public void NegativeCoordinatesUseFloorDivision()
    {
        Assert.AreEqual(new RegionId(-1,-1,-2),new CellId(-1,-8,-9).Region);
        Assert.AreEqual(7+0+7*64,new CellId(-1,-8,-9).Index);
    }
    [TestMethod] public void UnknownAndUnsupportedNeverReportClearVisibility()
    {
        var scene=new CellScene(World());scene.Observe(new(0,0,0),CellGeometry.Empty);
        Ray ray=new(new(.1,.2,.2),new(1,0,0),0,3);
        var unknown=CellSceneTracer.Trace(scene.Capture(),ray);Assert.AreEqual(SceneTraceStatus.Unknown,unknown.Status);Assert.AreEqual(.9,unknown.Distance,1e-12);
        scene.Observe(new(1,0,0),CellGeometry.Unsupported);
        Assert.AreEqual(SceneTraceStatus.Unsupported,CellSceneTracer.Trace(scene.Capture(),ray).Status);
    }
    [TestMethod] public void MeshHitUsesTrueTriangleNotItsBoundingCell()
    {
        var scene=new CellScene(World());scene.Observe(new(0,0,0),CellGeometry.Empty);scene.Observe(new(1,0,0),CellGeometry.FromMesh(Plane(.75)));
        var result=CellSceneTracer.Trace(scene.Capture(),new(new(.1,.2,.2),new(1,0,0),0,1.8));
        Assert.AreEqual(SceneTraceStatus.Hit,result.Status);Assert.AreEqual(1.65,result.Distance,1e-12);Assert.AreEqual(1.75,result.Hit.Position.X,1e-12);
        Assert.AreEqual(SceneTraceStatus.Clear,CellSceneTracer.Trace(scene.Capture(),new(new(.1,.9,.9),new(1,0,0),0,1.8)).Status);
    }
    [TestMethod] public void ExactCornerDoesNotInspectZeroWidthSideNeighbours()
    {
        var scene=new CellScene(World());scene.Observe(new(0,0,0),CellGeometry.Empty);scene.Observe(new(1,1,1),CellGeometry.Empty);
        var result=CellSceneTracer.Trace(scene.Capture(),new(new(.5,.5,.5),new(1,1,1),0,2));
        Assert.AreEqual(SceneTraceStatus.Clear,result.Status);
        Assert.AreEqual(SceneTraceStatus.BudgetExhausted,CellSceneTracer.Trace(scene.Capture(),new(new(.5,.5,.5),new(1,1,1),0,2),1).Status);
    }
    [TestMethod] public void NegativeRayOnBoundaryEntersNegativeCellWithoutFalseUnknown()
    {
        var scene=new CellScene(World());scene.Observe(new(-1,0,0),CellGeometry.Empty);
        Assert.AreEqual(SceneTraceStatus.Clear,CellSceneTracer.Trace(scene.Capture(),new(new(0,.5,.5),new(-1,0,0),0,.9)).Status);
    }
    [TestMethod] public void LargeWorldCoordinatesAreSubtractedBeforeMeshQuery()
    {
        var scene=new CellScene(World());scene.Observe(new(1000000000,0,0),CellGeometry.FromMesh(Plane(.75)));
        var result=CellSceneTracer.Trace(scene.Capture(),new(new(1000000000.125,.2,.2),new(1,0,0),0,.8));
        Assert.AreEqual(SceneTraceStatus.Hit,result.Status);Assert.AreEqual(.625,result.Distance,1e-10);
    }
    [TestMethod] public void ActiveMeshPrefixesIgnoreCapacityGarbageAndCopyInput()
    {
        float[] xyz={.5f,0,0,.5f,1,0,.5f,0,1,float.NaN,float.NaN,float.NaN};
        int[] indices={0,1,2,-12,999,100};BlockMesh mesh=BlockMesh.CopyIndexed(xyz,3,indices,3);
        xyz[0]=float.NaN;indices[0]=999;
        Assert.IsTrue(mesh.Trace(new(new(0,.1,.1),new(1,0,0),0,1),out var hit));Assert.AreEqual(.5,hit.Distance,1e-12);
    }
    [TestMethod] public void MalformedMeshesAreRejectedRatherThanChangedIntoOpaqueCubes()
    {
        float[] xyz={0,0,0,1,0,0,0,1,0};
        Assert.ThrowsException<ArgumentException>(()=>BlockMesh.CopyIndexed(xyz,3,new[]{0,1,8},3));
        Assert.ThrowsException<ArgumentException>(()=>BlockMesh.CopyIndexed(xyz,4,new[]{0,1,2},3));
        Assert.ThrowsException<ArgumentException>(()=>BlockMesh.CopyIndexed(xyz,3,new[]{0,0,0},3));
        Assert.ThrowsException<ArgumentException>(()=>new BlockMesh(new Triangle[]{new(new(0,0,0),new(2,0,0),new(0,1,0),0)}));
    }
    [TestMethod] public void RegionEditsHaveNoSideEffectsOnEmissionOrFlameClock()
    {
        WorldId world=World();var scene=new CellScene(world);var lights=new LightRegistry(world);
        var before=lights.Revisions;scene.Observe(new(0,0,0),CellGeometry.Empty);scene.Capture();Assert.AreEqual(before,lights.Revisions);
    }
}
