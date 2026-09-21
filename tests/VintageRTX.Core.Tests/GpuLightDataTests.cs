using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class GpuLightDataTests
{
    private static LightRegistry Registry() => new(new(Guid.NewGuid(), 0));
    private static LightId Id(int n) => new(SourceKind.Block, n, 0, 0, 0, 0);
    [TestMethod] public void TransfersEvaluatedCandleWithoutClampingOrSecondModulation()
    {
        var registry=Registry(); registry.Upsert(Id(1),new(new(1,.25,.75),new(8,3,1),EmissionProfile.Candle));
        LightFrame frame=registry.Capture(1,123.456);var packet=new GpuLightData();
        Assert.IsTrue(packet.Update(frame,new(0,0,0)));
        Assert.AreEqual(frame.Samples[0].Intensity.X,packet.Pixels[4]);
        Assert.AreEqual(frame.Samples[0].Intensity.Y,packet.Pixels[5]);
        Assert.AreEqual(frame.Samples[0].Intensity.Z,packet.Pixels[6]);
        Assert.IsTrue(packet.Pixels[4]>1);Assert.AreSame(frame,packet.SourceFrame);
        Assert.AreEqual(0f,packet.Pixels[7]);
    }
    [TestMethod] public void SteadyFrameAdvancesWithoutChangingPixels()
    {
        var registry=Registry();registry.Upsert(Id(1),new(default,Vector3.One,EmissionProfile.Steady));var packet=new GpuLightData();
        packet.Update(registry.Capture(1,1),default);long revision=packet.PixelRevision;
        LightFrame next=registry.Capture(2,2);Assert.IsFalse(packet.Update(next,default));
        Assert.AreEqual(revision,packet.PixelRevision);Assert.AreSame(next,packet.SourceFrame);
    }
    [TestMethod] public void SubtractionPrecedesFloatConversionAtLargeNegativeWorldPositions()
    {
        var registry=Registry();registry.Upsert(Id(1),new(new(-1000000000.25,128.125,1000000000.5),Vector3.One,EmissionProfile.Steady));
        var packet=new GpuLightData();packet.Update(registry.Capture(1,0),new(-1000000000,128,1000000000));
        CollectionAssert.AreEqual(new[]{-.25f,.125f,.5f},packet.Pixels[..3].ToArray());
    }
    [TestMethod] public void MoreThanEightSourcesAreRetainedAndRemovalClearsTail()
    {
        var registry=Registry();for(int i=0;i<257;i++)registry.Upsert(Id(i),new(new(i,0,0),Vector3.One,EmissionProfile.Steady));
        var packet=new GpuLightData();packet.Update(registry.Capture(1,0),default);
        Assert.AreEqual(257,packet.Count);Assert.AreEqual(512,packet.Height);
        for(int i=1;i<257;i++)registry.Remove(Id(i));
        packet.Update(registry.Capture(2,1),default);Assert.AreEqual(1,packet.Count);
        Assert.IsTrue(packet.Pixels[8..].ToArray().All(x=>x==0));
        registry.Remove(Id(0));packet.Update(registry.Capture(3,2),default);
        Assert.AreEqual(0,packet.Count);Assert.IsTrue(packet.Pixels.ToArray().All(x=>x==0));
    }
    [TestMethod] public void BadCandidateDoesNotPartiallyOverwritePublishedPacket()
    {
        var registry=Registry();registry.Upsert(Id(1),new(default,Vector3.One,EmissionProfile.Steady));var packet=new GpuLightData();
        LightFrame before=registry.Capture(1,0);packet.Update(before,default);float[] bytes=packet.Pixels.ToArray();
        registry.Upsert(Id(2),new(new(double.MaxValue,0,0),Vector3.One,EmissionProfile.Steady));LightFrame invalid=registry.Capture(2,1);
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>packet.Update(invalid,default));
        Assert.AreSame(before,packet.SourceFrame);CollectionAssert.AreEqual(bytes,packet.Pixels.ToArray());
    }
    [TestMethod] public void RejectsOldFramesButAcceptsNewWorldAndNewAnchor()
    {
        var registry=Registry();registry.Upsert(Id(1),new(new(10,0,0),Vector3.One,EmissionProfile.Steady));
        LightFrame first=registry.Capture(1,0),second=registry.Capture(2,1);var packet=new GpuLightData();packet.Update(second,default);
        Assert.ThrowsException<ArgumentException>(()=>packet.Update(first,default));
        Assert.IsTrue(packet.Update(second,new(8,0,0)));Assert.AreEqual(2f,packet.Pixels[0]);
        registry.Reset(new(Guid.NewGuid(),1));packet.Update(registry.Capture(1,0),default);Assert.AreEqual(0,packet.Count);
    }
    [TestMethod] public void ProfileOnlyChangeDoesNotAlterGeometryLayout()
    {
        var registry=Registry();var position=new DVec3(1,2,3);registry.Upsert(Id(1),new(position,Vector3.One,EmissionProfile.Candle));
        var packet=new GpuLightData();LightFrame first=registry.Capture(1,1);packet.Update(first,default);float[] original=packet.Pixels.ToArray();
        registry.Upsert(Id(1),new(position,Vector3.One,EmissionProfile.Steady));LightFrame second=registry.Capture(2,2);packet.Update(second,default);
        Assert.AreEqual(first.Revisions.Layout,second.Revisions.Layout);
        CollectionAssert.AreEqual(original[..4],packet.Pixels[..4].ToArray());Assert.AreEqual(1f,packet.Pixels[4]);
    }
}
