using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Scheduling;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class WorkCancellationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private sealed class CallbackWork(Func<bool> action) : IIncrementalWork { public bool Step() => action(); }

    [TestMethod]
    public void ClearDuringAWorldScanCannotResurrectItsOldRevision()
    {
        var queue = new WorkQueue<int>(); int calls = 0;
        queue.Enqueue(1, new CallbackWork(() => { calls++; Assert.IsTrue(queue.Contains(1)); queue.Clear(); Assert.IsFalse(queue.Contains(1)); return false; }), WorkPriority.Background);
        WorkReport report = queue.Drain(100, Budget);
        Assert.AreEqual(1, calls); Assert.AreEqual(1, report.Steps); Assert.AreEqual(0, report.Remaining);
        Assert.AreEqual(0, queue.Drain(100, Budget).Steps);
    }

    [TestMethod]
    public void ExplicitCancellationAndReplacementRespectTheInFlightIdentity()
    {
        var queue = new WorkQueue<string>(); int oldCalls = 0, newCalls = 0;
        queue.Enqueue("region", new CallbackWork(() => {
            oldCalls++; Assert.IsTrue(queue.Cancel("region")); Assert.IsFalse(queue.Cancel("region"));
            Assert.IsFalse(queue.Contains("region")); Assert.IsFalse(queue.Contains("other"));
            queue.Enqueue("region", new CallbackWork(() => {newCalls++; return true;}), WorkPriority.NearbyEdit);
            Assert.IsTrue(queue.Contains("region")); return false;
        }), WorkPriority.Background);
        queue.Drain(100, Budget); Assert.AreEqual(1, oldCalls); Assert.AreEqual(1, newCalls); Assert.AreEqual(0, queue.Count);
        queue.Enqueue("region", new CallbackWork(() => {
            queue.Enqueue("region", new CallbackWork(() => {newCalls++; return true;}), WorkPriority.Background); return false;
        }), WorkPriority.NearbyEdit);
        queue.Drain(100, Budget); Assert.AreEqual(2, newCalls); Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void ClearPreservesOnlyWorkEnqueuedAfterTheClear()
    {
        var queue = new WorkQueue<int>(); var calls = new List<int>();
        queue.Enqueue(1, new CallbackWork(() => {calls.Add(1); queue.Clear(); queue.Enqueue(1, new CallbackWork(() => {calls.Add(3); return true;}), WorkPriority.Background); return false;}), WorkPriority.NearbyEdit);
        queue.Enqueue(2, new CallbackWork(() => {calls.Add(2); return true;}), WorkPriority.Background);
        queue.Drain(100, Budget); CollectionAssert.AreEqual(new[] {1, 3}, calls); Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void UnrelatedCancelNestedDrainAndExceptionsLeaveTheQueueUsable()
    {
        var queue = new WorkQueue<int>(); int finished = 0;
        queue.Enqueue(1, new CallbackWork(() => {
            Assert.IsFalse(queue.Cancel(99)); Assert.IsFalse(queue.Contains(99));
            Assert.ThrowsException<InvalidOperationException>(() => queue.Drain(1, Budget));
            throw new ApplicationException("controlled-work-failure");
        }), WorkPriority.NearbyEdit);
        queue.Enqueue(2, new CallbackWork(() => {finished++; return true;}), WorkPriority.Background);
        Assert.ThrowsException<ApplicationException>(() => queue.Drain(2, Budget));
        Assert.IsFalse(queue.Contains(1)); Assert.IsTrue(queue.Contains(2));
        queue.Drain(10, Budget); Assert.AreEqual(1, finished);
        queue.Enqueue(3, new CallbackWork(() => true), WorkPriority.Background);
        Assert.IsTrue(queue.Cancel(3)); Assert.IsFalse(queue.Cancel(3));
        queue.Clear(); Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void AnInvalidationInsideTheCellProviderCompletesThenRepeatsTheRegion()
    {
        DiscoveryWindow? window = null; bool invalidated = false; int centerSamples = 0;
        window = new DiscoveryWindow((id, index) => {
            if(id != default) return;
            centerSamples++;
            if(!invalidated) { invalidated = true; window!.Invalidate(id); }
        }, _ => { });
        window.MoveTo(default); window.Drain(28 * 512, Budget);
        Assert.AreEqual(1024, centerSamples); Assert.AreEqual(0, window.Pending);
    }

    [TestMethod]
    public void ClearingDiscoveryFromItsProviderCannotResumeTheEvictedScan()
    {
        DiscoveryWindow? window = null; int observed = 0;
        window = new DiscoveryWindow((_, _) => {observed++; window!.Clear();}, _ => { });
        window.MoveTo(default); window.Drain(2048, Budget);
        Assert.AreEqual(1, observed); Assert.AreEqual(0, window.Pending); Assert.AreEqual(0, window.Active.Count);
        window.Drain(2048, Budget); Assert.AreEqual(1, observed);
    }
}
