using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class WorldSurfaceMaterialTests
{
    private const string Copper = """{"kind":"conductor","roughness":0.32,"eta":[0.2,0.92,1.1],"k":[3.91,2.45,2.14]}""";
    [TestMethod]
    public void ExplicitOpticsPreserveRgbAndDistinguishNativeFromMissingMetadata()
    {
        Assert.ThrowsException<FormatException>(()=>WorldSurfaceMaterial.Parse("""{"kind":"conductor","roughness":0.3,"k":[1,1,1]}"""));
        var m = WorldSurfaceMaterial.Parse(Copper).Material!;
        Assert.AreEqual(SurfaceKind.Conductor,m.Kind); Assert.AreEqual(.32,m.Roughness);
        Assert.AreEqual(new Vector3(.2f,.92f,1.1f),m.Eta);Assert.AreEqual(new Vector3(3.91f,2.45f,2.14f),m.K);
        Assert.AreEqual(Vector3.One,m.Reflectance);
        Assert.IsNull(WorldSurfaceMaterial.Parse("""{"kind":"native"}""").Material);
        Assert.AreEqual(SurfaceKind.Diffuse,WorldSurfaceMaterial.Parse("""{"kind":"diffuse"}""").Material!.Kind);
        Assert.AreEqual(0.0,WorldSurfaceMaterial.Parse(Copper.Replace("0.32","0",StringComparison.Ordinal)).Material!.Roughness);
        Assert.AreEqual(1.0,WorldSurfaceMaterial.Parse(Copper.Replace("0.32","1",StringComparison.Ordinal)).Material!.Roughness);
    }
    [DataTestMethod]
    [DataRow("{}")] [DataRow("[]")] [DataRow("null")] [DataRow("{")]
    [DataRow("{\"kind\":1}")] [DataRow("{\"kind\":\"metal\"}")]
    [DataRow("{\"kind\":\"native\",\"roughness\":0.2}")]
    [DataRow("{\"kind\":\"diffuse\",\"eta\":[1,1,1]}")]
    [DataRow("{\"kind\":\"conductor\"}")]
    [DataRow("{\"kind\":\"native\",\"kind\":\"native\"}")]
    [DataRow("{\"kind\":\"native\",\"normalMap\":\"not-implemented\"}")]
    public void MalformedDefinitionsAreNotSilentlyRepaired(string json) =>
        Assert.ThrowsException<FormatException>(()=>WorldSurfaceMaterial.Parse(json));
    [DataTestMethod]
    [DataRow("roughness","-0.01")] [DataRow("roughness","1.01")]
    [DataRow("roughness","null")] [DataRow("roughness","1e999")] [DataRow("roughness","\"0.3\"")]
    [DataRow("eta","[0,1,1]")] [DataRow("eta","[1,33,1]")] [DataRow("eta","[1,1]")]
    [DataRow("eta","[1,1,1e999]")] [DataRow("k","[1,1,1e999]")] [DataRow("eta","[1,\"1\",1]")]
    [DataRow("k","[-1,1,1]")] [DataRow("k","[1,33,1]")] [DataRow("k","null")]
    public void EachOpticalFieldIsValidated(string field,string value)
    {
        var document=System.Text.Json.Nodes.JsonNode.Parse(Copper)!;
        document[field]=System.Text.Json.Nodes.JsonNode.Parse(value);
        var e=Assert.ThrowsException<FormatException>(()=>WorldSurfaceMaterial.Parse(document.ToJsonString()));
        StringAssert.Contains(e.Message,field);
    }
    [TestMethod]
    public void MaterialRevisionIsLocalAndDoesNotRebuildItsMeshOrOldSnapshots()
    {
        var scene=new CellScene(new(Guid.NewGuid(),0));var data=new GpuSceneData();
        var mesh=BlockMesh.CopyIndexed([0,0,0,1,0,0,0,1,0],3,[0,1,2],3);
        var cell=new CellId(-1,0,-1);var initial=CellGeometry.FromMesh(mesh);
        scene.Observe(cell,initial);var before=scene.Capture();data.Update(before);
        int geometrySize=data.GeometryUsedTexels;Assert.AreEqual(1,data.TemplateCount);
        var material=WorldSurfaceMaterial.Parse(Copper);
        scene.Observe(cell,initial.WithSurface(material));var after=scene.Capture();Assert.IsTrue(data.Update(after));
        Assert.IsNull(before.At(cell).Surface);Assert.AreSame(material,after.At(cell).Surface);
        Assert.IsFalse(data.GeometryChanged);Assert.AreEqual(geometrySize,data.GeometryUsedTexels);Assert.AreEqual(1,data.TemplateCount);
        Assert.AreEqual(1,data.ChangedRegions.Length);Assert.AreEqual(1,data.MaterialChangedRegions.Length);
        int address=(GpuSceneData.Slot(cell.Region)*512+cell.Index)*8;
        Assert.AreEqual(GpuSceneData.SurfacePresentBit|1,data.CellData[address/2+3]);
        CollectionAssert.AreEqual(new float[]{.2f,.92f,1.1f,2,3.91f,2.45f,2.14f,.32f},data.MaterialData.Slice(address,8).ToArray());
        scene.Observe(new(-2,0,-1),CellGeometry.Empty);data.Update(scene.Capture());
        Assert.AreEqual(0,data.MaterialChangedRegions.Length,"An unrelated geometry edit does not rewrite material texels.");
        Assert.IsFalse(data.Update(scene.Capture()));Assert.AreEqual(0,data.MaterialChangedRegions.Length);
        scene.Observe(cell,initial.WithSurface(WorldSurfaceMaterial.Parse("""{"kind":"native"}""")));
        data.Update(scene.Capture());Assert.AreEqual(-1f,data.MaterialData[address+3]);
        scene.Observe(cell,initial.WithSurface(WorldSurfaceMaterial.Parse("""{"kind":"diffuse"}""")));data.Update(scene.Capture());
        Assert.AreEqual(1f,data.MaterialData[address+3]);
        scene.Observe(cell,CellGeometry.Empty);data.Update(scene.Capture());
        Assert.IsTrue(data.MaterialData.Slice(address,8).ToArray().All(v=>v==0));
        scene.Reset(new(Guid.NewGuid(),1));data.Update(scene.Capture());
        Assert.IsTrue(data.ResetOccurred);Assert.IsTrue(data.MaterialData.ToArray().All(v=>v==0));
    }
    [TestMethod]
    public void SurfaceDeclarationDoesNotInventGeometry()
    {
        var m=WorldSurfaceMaterial.Parse(Copper);
        Assert.ThrowsException<InvalidOperationException>(()=>CellGeometry.Empty.WithSurface(m));
        Assert.ThrowsException<InvalidOperationException>(()=>CellGeometry.Unknown.WithSurface(m));
        var unsupported=CellGeometry.Unsupported.WithSurface(m);
        Assert.AreEqual(CellState.Unsupported,unsupported.State);Assert.IsNull(unsupported.Mesh);
        Assert.ThrowsException<ArgumentException>(()=>WorldSurfaceMaterial.Parse(" "));
        Assert.ThrowsException<FormatException>(()=>WorldSurfaceMaterial.Parse("{"+new string(' ',4096)+"}"));
    }
}
