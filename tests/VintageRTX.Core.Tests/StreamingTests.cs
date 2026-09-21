using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Scheduling;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class StreamingTests
{
    private static RegionStore Store()=>new(new(Guid.NewGuid(),0));
    private static RegionGeometry Empty()=>new(ReadOnlySpan<Triangle>.Empty);
    [TestMethod]
    public void ReadyNearRegionDoesNotWaitForFarWork()
    {
        var s=Store();var near=s.Begin(new(0,0,0));s.Begin(new(50,0,0));
        Assert.IsTrue(s.Complete(near,Empty()));Assert.IsTrue(s.AcknowledgeUpload(near));
        Assert.IsTrue(s.TryGetReady(near.Region,out _)); Assert.AreEqual(RegionState.Building,s.StateOf(new(50,0,0)));
    }
    [TestMethod]
    public void StaleCpuAndGpuCompletionsAreRejectedAfterEdit()
    {
        var s=Store();var old=s.Begin(new(1,2,3));s.Complete(old,Empty());var current=s.Begin(old.Region);
        Assert.IsFalse(s.AcknowledgeUpload(old));Assert.IsFalse(s.Complete(old,Empty()));
        Assert.IsFalse(s.TryGetReady(old.Region,out _));Assert.IsTrue(s.Complete(current,Empty()));
        Assert.IsTrue(s.AcknowledgeUpload(current));
    }
    [TestMethod]
    public void EvictionAndRecreationCannotCauseAnAbaPublication()
    {
        var s=Store();var a=s.Begin(new(1,2,3));s.Evict(a.Region);var b=s.Begin(a.Region);
        Assert.AreNotEqual(a.Revision,b.Revision);Assert.IsFalse(s.Complete(a,Empty()));Assert.IsTrue(s.Complete(b,Empty()));
    }
    [TestMethod]
    public void TeleportWorldResetRejectsOldWorkEvenAtIdenticalCoordinates()
    {
        var s=Store();var a=s.Begin(new(0,0,0));s.Reset(new(Guid.NewGuid(),1));s.Begin(a.Region);
        Assert.IsFalse(s.Complete(a,Empty()));
    }
    [TestMethod]
    public void MissingRegionIsNotKnownEmptyAndCpuReadyIsNotGpuReady()
    {
        var s=Store();var id=new RegionId(0,0,0);
        Assert.AreEqual(RegionState.Unknown,s.StateOf(id));var t=s.Begin(id);s.Complete(t,Empty());
        Assert.IsTrue(s.TryGetUpload(t,out var payload));Assert.IsNotNull(payload);
        Assert.IsFalse(s.TryGetReady(id,out _));s.AcknowledgeUpload(t);
        Assert.IsTrue(s.TryGetReady(id,out payload));Assert.AreEqual(0,payload!.Triangles.Length);
    }
    [TestMethod]
    public void PublishedPayloadDoesNotAliasCallerArray()
    {
        Triangle a=new(new(0,0,0),new(1,0,0),new(0,1,0),0);
        Triangle[] array=[a];var payload=new RegionGeometry(array);array[0]=default;
        Assert.AreEqual(a,payload.Triangles[0]);
    }
    [TestMethod]
    public void WorkerCompletionsCanPublishIndependentDetachedRegions()
    {
        var s=Store();var tickets=Enumerable.Range(0,64).Select(i=>s.Begin(new(i,0,0))).ToArray();
        Parallel.ForEach(tickets,t=>Assert.IsTrue(s.Complete(t,Empty())));
        foreach(var t in tickets) Assert.IsTrue(s.AcknowledgeUpload(t));
    }
    private sealed class Unit(Func<bool> step):IIncrementalWork { public bool Step()=>step(); }
    [TestMethod]
    public void QueueCoalescesAndRunsLocalEditBeforeBackground()
    {
        var q=new WorkQueue<int>();var order=new List<int>();
        q.Enqueue(0,new Unit(()=>{order.Add(0);return true;}),WorkPriority.Background);
        q.Enqueue(1,new Unit(()=>{order.Add(1);return true;}),WorkPriority.NearbyEdit);
        q.Enqueue(1,new Unit(()=>{order.Add(2);return true;}),WorkPriority.NearbyEdit);
        var report=q.Drain(2,TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(new[]{2,0},order);Assert.AreEqual(2,report.Steps);Assert.AreEqual(0,q.Count);
    }
    [TestMethod]
    public void BackgroundCannotStarveUnderContinuousUrgentWork()
    {
        var q=new WorkQueue<int>();int background=0,urgent=0;
        q.Enqueue(0,new Unit(()=>{background++;return false;}),WorkPriority.Background);
        q.Enqueue(1,new Unit(()=>{urgent++;return false;}),WorkPriority.NearbyEdit);
        q.Drain(16,TimeSpan.FromSeconds(10));Assert.AreEqual(2,background);Assert.AreEqual(14,urgent);
    }
    [TestMethod]
    public void ZeroBudgetDoesNoWorkAndStepLimitIsRespected()
    {
        var q=new WorkQueue<int>();int calls=0;
        q.Enqueue(1,new Unit(()=>{calls++;return false;}),WorkPriority.NearbyEdit);
        Assert.AreEqual(0,q.Drain(10,TimeSpan.Zero).Steps);Assert.AreEqual(0,calls);
        Assert.AreEqual(3,q.Drain(3,TimeSpan.FromSeconds(10)).Steps);Assert.AreEqual(3,calls);
    }
    [TestMethod]
    public void NewRevisionQueuedInsideStepIsNotOverwritten()
    {
        var q=new WorkQueue<int>();int calls=0;
        q.Enqueue(1,new Unit(()=>{q.Enqueue(1,new Unit(()=>{calls++;return true;}),WorkPriority.NearbyEdit);return false;}),WorkPriority.Background);
        q.Drain(2,TimeSpan.FromSeconds(10));Assert.AreEqual(1,calls);Assert.AreEqual(0,q.Count);
    }
    [TestMethod]
    public void RegistryPublishesWithoutAnyGeometrySunRainOrWaterObject()
    {
        var registry=new LightRegistry(new(Guid.NewGuid(),0));
        registry.Upsert(new(SourceKind.Block,0,0,0,0,0),new(new(0,1,0),System.Numerics.Vector3.One,EmissionProfile.Torch));
        Assert.AreEqual(1,registry.Capture(1,0).Samples.Length);
    }
}
