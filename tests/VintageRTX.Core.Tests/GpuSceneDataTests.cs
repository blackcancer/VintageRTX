using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class GpuSceneDataTests
{
    private static BlockMesh Plane()=>new(new Triangle[]{new(new(.5,0,0),new(.5,1,0),new(.5,0,1),0)});
    [TestMethod] public void GeometryIsSharedAndAOneRegionEditHasOneUploadRectangle()
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();var mesh=Plane();
        scene.Observe(new(0,0,0),CellGeometry.FromMesh(mesh));scene.Observe(new(8,0,0),CellGeometry.FromMesh(mesh));
        Assert.IsTrue(data.Update(scene.Capture()));Assert.AreEqual(1,data.TemplateCount);int used=data.GeometryUsedTexels;
        scene.Observe(new(1,0,0),CellGeometry.FromMesh(mesh));data.Update(scene.Capture());
        Assert.AreEqual(1,data.ChangedRegions.Length);Assert.IsFalse(data.GeometryChanged);Assert.AreEqual(used,data.GeometryUsedTexels);
        Assert.IsFalse(data.Update(scene.Capture()));Assert.AreEqual(0,data.ChangedRegions.Length);
    }
    [TestMethod] public void ReusedRingSlotCarriesNewIntegerIdentityNotStaleGeometry()
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();scene.Observe(new(0,0,0),CellGeometry.FromMesh(Plane()));data.Update(scene.Capture());
        scene.Invalidate(new(0,0,0));scene.Observe(new(24,0,0),CellGeometry.Empty);data.Update(scene.Capture());
        Assert.AreEqual(3,data.RegionData[0]);Assert.AreEqual((int)CellState.Empty,data.CellData[0]);
        Assert.AreEqual((int)CellState.Unknown,data.CellData[4]);
    }
    [TestMethod] public void EvictionInvalidatesTagsAndWorldResetDiscardsMeshAddresses()
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();scene.Observe(new(0,0,0),CellGeometry.FromMesh(Plane()));data.Update(scene.Capture());
        scene.Invalidate(new(0,0,0));data.Update(scene.Capture());Assert.AreEqual(0,data.RegionData[3]);
        scene.Reset(new(Guid.NewGuid(),2));data.Update(scene.Capture());Assert.IsTrue(data.ResetOccurred);Assert.AreEqual(0,data.TemplateCount);Assert.AreEqual(0,data.GeometryUsedTexels);
    }
    [TestMethod] public void CollidingWorkingSetsAreRejectedBeforePreviousDataIsChanged()
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();scene.Observe(new(0,0,0),CellGeometry.Empty);data.Update(scene.Capture());int[] before=data.RegionData.ToArray();
        scene.Observe(new(24,0,0),CellGeometry.Empty);
        Assert.ThrowsException<ArgumentException>(()=>data.Update(scene.Capture()));CollectionAssert.AreEqual(before,data.RegionData.ToArray());
    }
    [TestMethod] public void GpuBoundsContainRoundedVerticesAndPointersStayWithinPacket()
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();scene.Observe(new(0,0,0),CellGeometry.FromMesh(Plane()));data.Update(scene.Capture());
        var g=data.GeometryData;Assert.IsTrue(g[0]<=.5f && g[4]>=.5f);Assert.AreEqual(3f,g[8]);Assert.AreEqual(1f,g[9]);Assert.AreEqual(3f,g[10]);
        Assert.AreEqual(.5f,g[12]);Assert.AreEqual(6,data.GeometryUsedTexels);
    }
    [TestMethod] public void LargeCoordinatesUseIntegerRegionTags()
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();CellId cell=new(1000000000,0,0);scene.Observe(cell,CellGeometry.Empty);data.Update(scene.Capture());
        Assert.AreEqual(125000000,data.RegionData[GpuSceneData.Slot(cell.Region)*4]);
    }
}
