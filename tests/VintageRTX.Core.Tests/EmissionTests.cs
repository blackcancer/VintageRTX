using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class EmissionTests
{
    private static LightRegistry Registry()=>new(new(Guid.NewGuid(),0));
    private static LightId Id(long i)=>new(SourceKind.Block,i,0,0,0,0);
    private static LightDefinition Lamp(EmissionProfile? profile=null)=>new(new(0,0,2),new(4,2,1),profile??EmissionProfile.Steady);
    [TestMethod]
    public void SteadyAndEngineDrivenSourcesNeverReceiveFlameNoise()
    {
        foreach(EmissionProfile p in new[]{EmissionProfile.Steady,EmissionProfile.Engine})
            for(int i=0;i<1000;i++) Assert.AreEqual(1.0,EmissionWaveform.Evaluate(p,123,i*0.13,0,1));
    }
    [TestMethod]
    public void FlameIsContinuousBoundedAndIndependentOfFrameRate()
    {
        double total=0,min=2,max=0;
        for(int i=0;i<10000;i++)
        {
            double t=i/60.0,y=EmissionWaveform.Evaluate(EmissionProfile.Torch,42,t,0,1);
            Assert.IsTrue(y>=0.70 && y<=1.30); total+=y; min=Math.Min(min,y);max=Math.Max(max,y);
            Assert.AreEqual(y,EmissionWaveform.Evaluate(EmissionProfile.Torch,42,(i*2)/120.0,0,1));
            Assert.IsTrue(Math.Abs(y-EmissionWaveform.Evaluate(EmissionProfile.Torch,42,t+1e-8,0,1))<1e-5);
        }
        Assert.IsTrue(max-min>0.1); Assert.AreEqual(1,total/10000,0.035);
    }
    [TestMethod]
    public void DistinctFlamesAreNotSynchronized()
    {
        double difference=0;
        for(int i=0;i<1000;i++) difference+=Math.Abs(EmissionWaveform.Evaluate(EmissionProfile.Fire,1,i*.01,0)
            -EmissionWaveform.Evaluate(EmissionProfile.Fire,2,i*.01,0));
        Assert.IsTrue(difference/1000>0.02);
    }
    [TestMethod]
    public void EnclosedLanternIsCalmerThanExposedTorch()
    {
        double torch=0,lantern=0;
        for(int i=0;i<1000;i++)
        { torch+=Math.Pow(EmissionWaveform.Evaluate(EmissionProfile.Torch,5,i*.01,0,1)-1,2);
          lantern+=Math.Pow(EmissionWaveform.Evaluate(EmissionProfile.Lantern,5,i*.01,0,1)-1,2); }
        Assert.IsTrue(torch>lantern*3);
    }
    [TestMethod]
    public void LightningIsAnEventNotAPeriodicFlame()
    {
        var p=EmissionProfile.Lightning;
        Assert.AreEqual(0.0,EmissionWaveform.Evaluate(p,1,9,10));
        Assert.IsTrue(EmissionWaveform.Evaluate(p,1,10+.035*p.DurationSeconds,10)>.99);
        Assert.AreEqual(0.0,EmissionWaveform.Evaluate(p,1,10+p.DurationSeconds,10));
        Assert.AreEqual(0.0,EmissionWaveform.Evaluate(p,1,100,10));
    }
    [TestMethod]
    public void ProfileMappingDoesNotGuessFireFromColorOrModSubstrings()
    {
        EmissionCatalog catalog = EmissionCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","emission.json")));
        Assert.AreEqual(EmissionKind.Flame,EmissionProfiles.ForCode(catalog,"game:torch-up").Kind);
        Assert.AreEqual(EmissionKind.Flame,EmissionProfiles.ForCode(catalog,"game:oillamp-clay").Kind);
        Assert.AreEqual(EmissionKind.Steady,EmissionProfiles.ForCode(catalog,"other:torch-battery").Kind);
        Assert.AreEqual(EmissionKind.EngineDriven,EmissionProfiles.ForCode(catalog,"game:locust-corrupt",true).Kind);
    }
    [TestMethod]
    public void InvalidProfileOrTimeIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>new EmissionProfile(EmissionKind.Flame,double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>new EmissionProfile(EmissionKind.Flame,.9));
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>EmissionWaveform.Evaluate(EmissionProfile.Torch,1,double.NaN,0));
    }
    [TestMethod]
    public void NoGlobalEightLightLimitOrDistanceDeduplication()
    {
        var registry=Registry(); for(int i=0;i<32;i++) registry.Upsert(Id(i),Lamp());
        Assert.AreEqual(32,registry.Capture(1,0).Samples.Length);
    }
    [TestMethod]
    public void ExtinguishedSourceCannotBeResurrectedByItsProfile()
    {
        var registry=Registry(); registry.Upsert(Id(1),Lamp(EmissionProfile.Fire));
        registry.Upsert(Id(1),new(new(0,0,2),Vector3.Zero,EmissionProfile.Fire));
        Assert.AreEqual(0,registry.Capture(1,10).Samples.Length);
    }
    [TestMethod]
    public void RemovalDoesNotMutateAlreadyCapturedFrames()
    {
        var registry=Registry(); registry.Upsert(Id(1),Lamp()); var before=registry.Capture(1,1);
        registry.Remove(Id(1)); var after=registry.Capture(2,2);
        Assert.AreEqual(1,before.Samples.Length);Assert.AreEqual(0,after.Samples.Length);
        Assert.AreEqual(new Vector3(4,2,1),before.Samples[0].Intensity);
    }
    [TestMethod]
    public void EmissionChangeDoesNotInvalidateVisibilityGeometry()
    {
        var registry=Registry(); registry.Upsert(Id(1),Lamp()); var first=registry.Revisions;
        registry.Upsert(Id(1),new(new(0,0,2),new(8,4,2),EmissionProfile.Steady));
        Assert.AreEqual(first.Layout,registry.Revisions.Layout); Assert.IsTrue(registry.Revisions.Emission>first.Emission);
        registry.Upsert(Id(1),new(new(0,0,3),new(8,4,2),EmissionProfile.Steady));
        Assert.IsTrue(registry.Revisions.Layout>first.Layout);
    }
    [TestMethod]
    public void WaveformDoesNotInvalidateTopologyEachFrame()
    {
        var registry=Registry(); registry.Upsert(Id(1),Lamp(EmissionProfile.Torch));var revisions=registry.Revisions;
        for(int i=0;i<100;i++) registry.Capture(i,i*.01);
        Assert.AreEqual(revisions,registry.Revisions);
    }
    [TestMethod]
    public void ResetDropsOldWorldSourcesAndAcceptsNewClock()
    {
        var registry=Registry(); registry.Upsert(Id(1),Lamp());registry.Capture(100,200);
        registry.Reset(new(Guid.NewGuid(),3));Assert.AreEqual(0,registry.Capture(0,0).Samples.Length);
    }
    [TestMethod]
    public void OwnerThreadAndMonotonicFrameContractAreEnforced()
    {
        var registry=Registry();registry.Capture(2,2);
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>registry.Capture(1,3));
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>registry.Capture(3,1));
        Assert.ThrowsException<InvalidOperationException>(()=>Task.Run(()=>registry.Upsert(Id(1),Lamp())).GetAwaiter().GetResult());
    }
    [TestMethod]
    public void NonfiniteLightIsRejected()
    { Assert.ThrowsException<ArgumentOutOfRangeException>(()=>new LightDefinition(new(0,0,1),new(float.NaN,1,1),EmissionProfile.Steady)); }
    [TestMethod]
    public void SourceIdentityIncludesChannelIncarnationAndKind()
    {
        var id=Id(1);Assert.AreNotEqual(id.Seed,(id with {Channel=1}).Seed);
        Assert.AreNotEqual(id.Seed,(id with {Incarnation=1}).Seed);
        Assert.AreNotEqual(id.Seed,(id with {Kind=SourceKind.Entity}).Seed);
    }
    [TestMethod]
    public void SrgbReferenceValuesAndRoundTrip()
    {
        Assert.AreEqual(.21404114f,ColorSpace.Decode(.5f),1e-7);
        for(int i=0;i<=1000;i++) Assert.AreEqual(i/1000f,ColorSpace.Encode(ColorSpace.Decode(i/1000f)),2e-6);
    }
}
