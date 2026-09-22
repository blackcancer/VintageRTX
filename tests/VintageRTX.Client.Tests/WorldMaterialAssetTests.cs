using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using VintageRTX.Core.Transport;
using VintageRTX.Core.Scene;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Client.Tests;

public sealed partial class EmissionAssetTests
{
    [TestMethod]
    public void NativeMaterialPatchPreservesOtherAttributesAndConfiguresOnlyAuthoredNewMetals()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(EmissionAssetTests),nameof(NativeMaterialPatchPreservesOtherAttributesAndConfiguresOnlyAuthoredNewMetals)))return;
        string source=File.ReadAllText(Path.Combine(game,"assets/survival/blocktypes/metal/metalblock.json"));
        var json=JObject.Parse(source);
        json["attributesByType"]=JObject.Parse("""{"metalblock-new-*-copper":{"thirdParty":{"retained":19}}}""");
        var memory=new MemoryContext(Asset());
        var original=memory.Add("game:blocktypes/metal/metalblock.json",json.ToString());
        memory.Add("vintagertx:patches/world-materials.json",File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/world-materials.json")));
        ModSystem patcher=NativePatcher();patcher.Start(memory.Api);patcher.AssetsLoaded(memory.Api);
        var patched=JObject.Parse(original.Asset.ToText());
        Assert.AreEqual(19,patched["attributesByType"]!["metalblock-new-*-copper"]!["thirdParty"]!["retained"]!.Value<int>());
        CollectionAssert.AreEquivalent(new[]{"metalblock-new-*-copper","metalblock-new-*-gold","metalblock-new-*-silver"},
            ((JObject)patched["attributesByType"]!).Properties().Select(p=>p.Name).ToArray());
        foreach(string metal in new[]{"copper","gold","silver"})
        {
            var attributes=new JsonObject(patched["attributesByType"]!["metalblock-new-*-"+metal]!);
            var m=WorldSurfaceMaterial.Parse(attributes[WorldSurfaceMaterial.Attribute].ToString()!).Material!;
            Assert.AreEqual(SurfaceKind.Conductor,m.Kind);Assert.IsTrue(m.Roughness>0 && m.Roughness<1);
        }
        Assert.AreEqual(json["code"]!.Value<string>(),patched["code"]!.Value<string>());
        Assert.IsTrue(JToken.DeepEquals(json["texturesByType"],patched["texturesByType"]));
        Assert.IsTrue(JToken.DeepEquals(json["vertexFlagsByType"],patched["vertexFlagsByType"]));
        Assert.AreEqual(0,memory.Errors.Count,string.Join("\n",memory.Errors));patcher.Dispose();
    }
}
public sealed partial class GeometryCollectorLifecycleTests
{
    [TestMethod]
    public void CollectorUsesPatchedBlockMaterialWithoutInventingCasterSupport()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(GeometryCollectorLifecycleTests),nameof(CollectorUsesPatchedBlockMaterialWithoutInventingCasterSupport)))return;
        var host=new Host();using var collector=new ClientGeometryCollector(host.Api);
        var world=new VintageRTX.Core.Lighting.WorldId(Guid.NewGuid(),0);collector.Reset(world);
        Block block=Block(),air=Block(0);var pos=new Vintagestory.API.MathTools.BlockPos(0,0,0,0);
        const string material="""{"vintageRtxMaterial":{"kind":"conductor","roughness":0.32,"eta":[0.2,0.92,1.1],"k":[3.91,2.45,2.14]}}""";
        block.Attributes=JsonObject.FromJson(material);
        collector.Observe(pos,block,air);collector.Publish();var before=collector.Frame!;
        Assert.AreEqual(SurfaceKind.Conductor,before.At(default).Surface!.Material!.Kind);
        int reads=host.Reads;collector.Observe(pos,block,air);collector.Publish();Assert.AreSame(before,collector.Frame);
        block.Attributes=JsonObject.FromJson(material.Replace("0.32","0.6",StringComparison.Ordinal));
        collector.Observe(pos,block,air);collector.Publish();Assert.AreEqual(.6,collector.Frame!.At(default).Surface!.Material!.Roughness);
        Assert.AreEqual(.32,before.At(default).Surface!.Material!.Roughness);Assert.AreEqual(reads,host.Reads);
        block.Attributes=JsonObject.FromJson("""{"vintageRtxMaterial":{"kind":"conductor","roughness":-1}}""");
        collector.Observe(pos,block,air);collector.Publish();Assert.IsNotNull(collector.Frame!.At(default).Surface);
        Assert.IsNull(collector.Frame.At(default).Surface!.Material,"Bad opt-in is native, not guessed diffuse/conductor.");
        Assert.AreEqual(1,host.Warnings.Count);
        collector.Observe(pos,block,air);Assert.AreEqual(1,host.Warnings.Count);
        Block complex=Block(20);complex.HasTiles=true;complex.Attributes=JsonObject.FromJson(material);
        collector.Observe(new(1,0,0,0),complex,air);collector.Publish();
        Assert.AreEqual(CellState.Unsupported,collector.Frame!.At(new(1,0,0)).State);
        Assert.IsNotNull(collector.Frame.At(new(1,0,0)).Surface!.Material);
        collector.Observe(pos,air,air);collector.Publish();Assert.IsNull(collector.Frame!.At(default).Surface);
        collector.Reset(new(Guid.NewGuid(),1));collector.Publish();Assert.AreEqual(0,collector.Frame!.RegionCount);
    }
}
