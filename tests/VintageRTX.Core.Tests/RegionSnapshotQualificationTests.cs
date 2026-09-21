using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Scheduling;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class RegionSnapshotQualificationTests
{
    [TestMethod]
    public void HotRegionQueriesAllocateNothingAndOldSnapshotsStayImmutable()
    {
        var window = new DiscoveryWindow((_, _) => { }, _ => { });
        Assert.AreEqual(0, window.Active.Count);
        window.MoveTo(default); IReadOnlyCollection<RegionId> first = window.Active;
        Assert.AreEqual(27, first.Count); window.MoveTo(default); Assert.AreSame(first, window.Active);
        for (int i = 0; i < 4; i++) ReadMembership(window);
        var cost = ReadMembership(window); Assert.AreEqual(0L, cost.Bytes); Assert.AreEqual(27 * 4096, cost.Count);
        Assert.ThrowsException<NotSupportedException>(() => ((ICollection<RegionId>)first).Add(new(8, 0, 0)));
        window.MoveTo(new(1, 0, 0)); Assert.AreNotSame(first, window.Active);
        Assert.IsTrue(first.Contains(new(-1, 0, 0))); Assert.IsFalse(window.Active.Contains(new(-1, 0, 0)));
        IReadOnlyCollection<RegionId> second = window.Active;
        Assert.ThrowsException<OverflowException>(() => window.MoveTo(new(int.MaxValue, 0, 0)));
        Assert.AreSame(second, window.Active); Assert.AreEqual(27, window.Active.Count);
        window.Clear(); Assert.AreEqual(0, window.Active.Count); Assert.AreEqual(27, first.Count);
        Console.WriteLine("4096 repeated Active queries: 0 allocated bytes; 27 stable immutable region tags.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Bytes, int Count) ReadMembership(DiscoveryWindow window)
    {
        int count = 0; long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) count += window.Active.Count;
        return (GC.GetAllocatedBytesForCurrentThread() - start, count);
    }

    [TestMethod]
    public void RetainedScanCannotReadAnEvictedRegion()
    {
        int calls = 0; var window = new DiscoveryWindow((_, _) => calls++, _ => { });
        Type scanType = typeof(DiscoveryWindow).GetNestedType("Scan", BindingFlags.NonPublic)!;
        var scan = (IIncrementalWork)Activator.CreateInstance(scanType, window, new RegionId())!;
        Assert.IsTrue(scan.Step()); Assert.AreEqual(0, calls);
        window.MoveTo(default); Assert.IsFalse(scan.Step()); Assert.AreEqual(1, calls);
        window.Clear(); Assert.IsTrue(scan.Step()); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void DuplicateHitsAndOppositeOpticalDirectionsKeepDeterministicBoundaries()
    {
        var triangle = new Triangle(default, new(1, 0, 0), new(0, 1, 0), 0);
        var bvh = new TriangleBvh(Enumerable.Repeat(triangle, 4).ToArray());
        Assert.IsTrue(bvh.Trace(new(new(.2, .2, 1), new(0, 0, -1)), out var hit));
        Assert.AreEqual(0, hit.Primitive);
        var material = new SurfaceMaterial(SurfaceKind.Conductor, Vector3.One, .5, Vector3.One, Vector3.One);
        Assert.ThrowsException<ArgumentException>(() => Bsdf.ConductorFresnel(.5, 0, 1));
        Assert.AreEqual(Vector3.Zero, Bsdf.Evaluate(material, new(0, 0, 1), new(0, 0, -1), new(0, 0, 1)));
        // Near-grazing inputs can differ by a few ULPs after normalization. A nonpositive
        // half-vector dot product must never become a negative directional density.
        Assert.AreEqual(0d, Bsdf.Pdf(material, new(0, 0, 1), new(1, 0, 1e-14), new(-1 - 4e-12, 0, 1e-14)));
    }
}
