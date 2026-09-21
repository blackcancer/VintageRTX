using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Client;
using VintageRTX.Core.Geometry;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client.Tests;

[TestClass]
public sealed class MeshImportTests
{
    private static MeshData Mesh()=>new(false){xyz=new float[]{.5f,0,0,.5f,1,0,.5f,0,1,float.NaN,0,0},VerticesCount=3,
        Indices=new[]{0,1,2,999},IndicesCount=3,mode=EnumDrawMode.Triangles};
    [TestMethod] public void RealMeshDataUsesItsActiveCountsAndIsNeverRetained()
    {
        MeshData source=Mesh();var copy=ClientGeometryCollector.CopyMesh(source);source.xyz[0]=float.NaN;
        Assert.IsTrue(copy.Trace(new Ray(new(0,.2,.2),new(1,0,0),0,1),out var hit));Assert.AreEqual(.5,hit.Distance,1e-12);
    }
    [TestMethod] public void AnimatedTransparentAndMalformedRenderPassesAreNotOpaqueGeometry()
    {
        MeshData source=Mesh();source.HasAnyWindModeSet=true;Assert.ThrowsException<ArgumentException>(()=>ClientGeometryCollector.CopyMesh(source));
        source=Mesh();source.RenderPassCount=1;source.RenderPassesAndExtraBits=new[]{(short)EnumChunkRenderPass.Transparent};
        Assert.ThrowsException<ArgumentException>(()=>ClientGeometryCollector.CopyMesh(source));
        source=Mesh();source.IndicesCount=10;Assert.ThrowsException<ArgumentException>(()=>ClientGeometryCollector.CopyMesh(source));
    }
    [TestMethod] public void SupportedSubsetRejectsProceduralAndRandomizedVariants()
    {
        Block block=new(){BlockId=123,DrawType=EnumDrawType.Cube,RenderPass=EnumChunkRenderPass.Opaque};
        for(int i=0;i<6;i++)block.SideOpaque[i]=true;
        Assert.IsTrue(ClientGeometryCollector.SupportedBlock(block));
        block.RandomizeRotations=true;Assert.IsFalse(ClientGeometryCollector.SupportedBlock(block));block.RandomizeRotations=false;
        block.EntityClass="runtime-instance";Assert.IsFalse(ClientGeometryCollector.SupportedBlock(block));block.EntityClass=null!;
        block.HasAlternates=true;Assert.IsFalse(ClientGeometryCollector.SupportedBlock(block));
        Assert.IsFalse(ClientGeometryCollector.SupportedBlock(new CustomBlock(){BlockId=124,DrawType=EnumDrawType.Cube}));
    }
    private sealed class CustomBlock:Block { }
}
