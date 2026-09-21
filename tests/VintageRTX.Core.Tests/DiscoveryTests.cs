using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Geometry;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class DiscoveryTests
{
    [TestMethod]
    public void NegativeWorldCoordinatesUseFloorNotTruncation()
    { Assert.AreEqual(new RegionId(-1,-2,0),DiscoveryWindow.At(new(-.1,-8.01,7.9))); }
    [TestMethod]
    public void MovingOneRegionOnlyDiscoversNineNewRegions()
    {
        var reads=new HashSet<RegionId>();var evicted=new HashSet<RegionId>();
        var w=new DiscoveryWindow((r,i)=>reads.Add(r),r=>evicted.Add(r));w.MoveTo(new(0,0,0));
        w.Drain(27*512,TimeSpan.FromSeconds(10));Assert.AreEqual(27,reads.Count);Assert.AreEqual(0,w.Pending);
        reads.Clear();w.MoveTo(new(1,0,0));w.Drain(27*512,TimeSpan.FromSeconds(10));
        Assert.AreEqual(9,reads.Count);Assert.AreEqual(9,evicted.Count);Assert.AreEqual(27,w.Active.Count);
    }
    [TestMethod]
    public void NearbyDiscoveryIsReportedBeforeTheCompleteWindow()
    {
        int reads=0;RegionId first=default;var w=new DiscoveryWindow((r,i)=>{if(reads++==0)first=r;},_=>{});
        w.MoveTo(new(2,3,4));w.Drain(1,TimeSpan.FromSeconds(10));
        Assert.AreEqual(1,reads);Assert.AreEqual(new RegionId(2,3,4),first);Assert.AreEqual(27,w.Pending);
    }
    [TestMethod]
    public void InvalidatingRunningScanDoesNotRestartItsCurrentProgress()
    {
        var sequence=new List<int>();RegionId center=new(0,0,0);
        var w=new DiscoveryWindow((r,i)=>{if(r==center)sequence.Add(i);},_=>{});
        w.MoveTo(center);w.Drain(27*10,TimeSpan.FromSeconds(10));w.Invalidate(center);
        w.Drain(27*510,TimeSpan.FromSeconds(10));
        for(int i=0;i<512;i++) Assert.AreEqual(i,sequence[i]);
    }
    [TestMethod]
    public void TeleportCancelsTasksOutsideTheNewWindow()
    {
        var reads=new HashSet<RegionId>();var w=new DiscoveryWindow((r,i)=>reads.Add(r),_=>{});
        w.MoveTo(new(0,0,0));w.Drain(27,TimeSpan.FromSeconds(10));reads.Clear();
        w.MoveTo(new(100,0,0));w.Drain(27,TimeSpan.FromSeconds(10));
        Assert.IsTrue(reads.All(r=>r.X>=99));
    }
}
