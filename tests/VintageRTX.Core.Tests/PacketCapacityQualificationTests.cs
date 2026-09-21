using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Diagnostics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class PacketCapacityQualificationTests
{
    [TestMethod]
    public void CapacityFailureRemovesPacketIdentityAndAllowsACleanRebuild()
    {
        var scene = new CellScene(new(Guid.NewGuid(), 0)); var triangle = new Triangle(default, new(1, 0, 0), new(0, 1, 0), 0);
        scene.Observe(default, CellGeometry.FromMesh(new(new[] {triangle})));
        CellSceneFrame first = scene.Capture(); var packet = new GpuSceneData(); packet.Update(first);
        Triangle[] dense = Enumerable.Repeat(triangle, 4096).ToArray();
        // 60 distinct templates exceed the explicit 1,048,576-texel cache. This is bounded CPU
        // storage, not an attempt to trigger an operating-system/GPU out-of-memory condition.
        for(int i = 0; i < 60; i++) scene.Observe(new(i % 8, i / 8, 0), CellGeometry.FromMesh(new(dense)));
        Assert.ThrowsException<InvalidOperationException>(() => packet.Update(scene.Capture()));
        Assert.IsNull(packet.SourceFrame, "Partially rewritten arrays must not retain the preceding valid frame identity.");
        Assert.IsTrue(packet.Update(first)); Assert.IsTrue(packet.ResetOccurred);
        Assert.AreSame(first, packet.SourceFrame); Assert.AreEqual(1, packet.TemplateCount);
        Assert.IsFalse(packet.Update(first)); Assert.IsFalse(packet.GeometryChanged);
    }

    [TestMethod]
    public void RingReuseValidatesEveryWorldCoordinateNotOnlyItsXTag()
    {
        var scene = new CellScene(new(Guid.NewGuid(), 0)); var packet = new GpuSceneData(); CellId previous = default;
        foreach (CellId next in new[] {new CellId(), new(0, 24, 0), new(0, 24, 24)})
        {
            scene.Invalidate(previous.Region); scene.Observe(next, CellGeometry.Empty);
            Assert.IsTrue(packet.Update(scene.Capture())); int tag = GpuSceneData.Slot(next.Region) * 4;
            Assert.AreEqual(next.Region.X, packet.RegionData[tag]); Assert.AreEqual(next.Region.Y, packet.RegionData[tag + 1]);
            Assert.AreEqual(next.Region.Z, packet.RegionData[tag + 2]); Assert.AreEqual(1, packet.RegionData[tag + 3]); previous = next;
        }
        scene.Invalidate(previous.Region); packet.Update(scene.Capture());
        scene.Observe(previous, CellGeometry.Empty); Assert.IsTrue(packet.Update(scene.Capture()));
        Assert.AreEqual(1, packet.ChangedRegions.Length);
        CellSceneFrame valid = packet.SourceFrame!;
        scene.Observe(new(24, 24, 24), CellGeometry.Empty);
        Assert.ThrowsException<ArgumentException>(() => packet.Update(scene.Capture()));
        Assert.AreSame(valid, packet.SourceFrame, "Ring aliases are rejected before mutating a valid packet.");
    }

    [TestMethod]
    public void TrianglePackersHandleAllSplitAxesAndRejectCorruptedDetachedVertices()
    {
        foreach (int axis in new[] {0, 1, 2})
        {
            Triangle[] triangles = Enumerable.Range(0, 6).Select(i => {
                DVec3 p = axis == 0 ? new(.1 + .1 * i, .1, .1) : axis == 1 ? new(.1, .1 + .1 * i, .1) : new(.1, .1, .1 + .1 * i);
                return new Triangle(p, p + new DVec3(.01, 0, 0), p + new DVec3(0, .01, 0), 0);
            }).ToArray();
            var scene = new CellScene(new(Guid.NewGuid(), 0)); scene.Observe(default, CellGeometry.FromMesh(new(triangles)));
            var packet = new GpuSceneData(); Assert.IsTrue(packet.Update(scene.Capture()));
            Assert.AreEqual(1, packet.TemplateCount); Assert.AreEqual(6, packet.CellData[3]);
        }
        object corrupt = new Triangle(default, new(1, 0, 0), new(0, 1, 0), 0);
        // Validate the mesh import boundary even if a serializer bypassed Triangle's constructor.
        typeof(Triangle).GetField("<A>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(corrupt, new DVec3(double.NaN, 0, 0));
        Assert.ThrowsException<ArgumentException>(() => new BlockMesh(new[] {(Triangle)corrupt}));
    }

    [TestMethod]
    public void AlternateLabViewsExposeBackfacesAndUnknownCoverageWithoutFabricatedReceivers()
    {
        var backside = DirectLightLab.CreateFromView(new(4, 4, .1), 8, 8, blocker: false);
        Assert.IsTrue(backside.Receivers.ToArray().All(r => r.Present && r.GeometricNormal.Z < 0));
        foreach (var receiver in backside.Receivers)
            Assert.IsTrue(DVec3.Dot(receiver.GeometricNormal, receiver.ShadingNormal) > 0);
        var outside = DirectLightLab.CreateFromView(new(4, 4, 9), 8, 8, blocker: false);
        Assert.IsTrue(outside.Receivers.ToArray().All(r => !r.Present));
        Assert.IsTrue(outside.Positions.ToArray().All(v => v == 0));
    }
}
