using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class BvhTraversalQualificationTests
{
    [TestMethod]
    public void RetainedSlabEntriesMatchHistoricalAndExhaustiveGeometryIncludingTies()
    {
        var random = new Random(492631); var triangles = new List<Triangle>();
        for(int i = 0; i < 384; i++)
        {
            var a = new DVec3(random.NextDouble() * 8, random.NextDouble() * 8, random.NextDouble() * 8);
            DVec3 x = i % 3 == 0 ? new(0, .9, 0) : new(.9, 0, 0);
            DVec3 y = i % 3 == 2 ? new(0, .9, 0) : new(0, 0, .9);
            triangles.Add(new(a, a + x, a + y, i % 5));
        }
        triangles.AddRange(triangles.Take(32).ToArray());
        Triangle[] data = triangles.ToArray(); var old = new LegacyBvhCounter(data); var candidate = new TriangleBvh(data);
        long oldBounds = 0, newBounds = 0, primitiveTests = 0; int hits = 0;
        for(int i = 0; i < 4096; i++)
        {
            DVec3 origin = new(random.NextDouble() * 12 - 2, random.NextDouble() * 12 - 2, -2);
            DVec3 target = new(random.NextDouble() * 8, random.NextDouble() * 8, random.NextDouble() * 8);
            var ray = new Ray(origin, target - origin, 0, 20);
            // Both traversals call the same outward-rounded slab kernel. Brute force has NO
            // bounding-box dependency and is the independent correctness oracle for every hit.
            bool a = old.Trace(ray, out SurfaceHit oldHit), b = candidate.Trace(ray, out SurfaceHit hit, out var cost);
            Assert.AreEqual(a, b); if(a) {Assert.AreEqual(oldHit, hit); hits++;}
            bool brute = Brute(data, ray, out var expected); Assert.AreEqual(brute, b);
            if(brute) Assert.AreEqual(expected, hit);
            Assert.IsTrue(cost.BoundsTests <= old.BoundsTests);
            oldBounds += old.BoundsTests; newBounds += cost.BoundsTests; primitiveTests += cost.TriangleTests;
        }
        Assert.IsTrue(hits > 500); Assert.IsTrue(newBounds < oldBounds);
        Console.WriteLine(JsonSerializer.Serialize(new {rays = 4096, triangles = data.Length, hits,
            historicalBoundsTests = oldBounds, retainedEntryBoundsTests = newBounds, primitiveTests,
            reduction = 1 - newBounds / (double)oldBounds, scope = "CPU traversal operation counts with a shared slab kernel, not FPS"}));
    }

    [TestMethod]
    public void EmptyMissesAndAxisSpecificTreesKeepAllocationFreeQueries()
    {
        var ray = new Ray(new(.2, .2, -1), new(0, 0, 1), 0, 4);
        var empty = new TriangleBvh([]); Assert.IsFalse(empty.Trace(ray, out _, out var noWork));
        Assert.AreEqual(0, noWork.BoundsTests); Assert.AreEqual(0, noWork.TriangleTests);
        foreach(int axis in new[] {0, 1, 2})
        {
            Triangle[] triangles = Enumerable.Range(0, 64).Select(i => {
                DVec3 shift = axis == 0 ? new(i, 0, 0) : axis == 1 ? new(0, i, 0) : new(0, 0, i);
                return new Triangle(shift, shift + new DVec3(1, 0, 0), shift + new DVec3(0, 1, 0), 0);
            }).ToArray();
            var bvh = new TriangleBvh(triangles);
            // Warm the isolated measurement method and runtime helpers, not just its callee.
            // Assertions, ray construction and result serialization are outside the measured loop.
            for(int i = 0; i < 4; i++) Measure(bvh, ray);
            var measurement = Measure(bvh, ray);
            Assert.AreEqual(4096, measurement.Hits); Assert.AreEqual(0L, measurement.Bytes);
            Assert.IsFalse(bvh.Trace(new(new(-2, -2, -2), new(-1, 0, 0)), out _, out var miss));
            Assert.AreEqual(1, miss.BoundsTests); Assert.AreEqual(0, miss.TriangleTests);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Bytes, int Hits) Measure(TriangleBvh bvh, Ray ray)
    {
        int hits = 0; long before = GC.GetAllocatedBytesForCurrentThread();
        for(int i = 0; i < 4096; i++) if(bvh.Trace(ray, out _)) hits++;
        return (GC.GetAllocatedBytesForCurrentThread() - before, hits);
    }
    private static bool Brute(Triangle[] data, in Ray ray, out SurfaceHit hit)
    {
        bool found = false; hit = default; double limit = ray.Maximum;
        for(int i = 0; i < data.Length; i++)
            if(data[i].Intersect(ray, limit, i, out var h) && (!found || h.Distance < limit || h.Distance == limit && h.Primitive < hit.Primitive))
            { found = true; limit = h.Distance; hit = h; }
        return found;
    }
}
