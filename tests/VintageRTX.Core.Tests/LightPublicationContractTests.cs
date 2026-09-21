using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class LightPublicationContractTests
{
    private static LightId Id(int n) => new(SourceKind.Block, n, 0, 0, 0, 0);
    private static LightRegistry Registry() => new(new(Guid.NewGuid(), 0));
    private static LightDefinition Definition(int n, EmissionProfile? profile = null, double birth = 0) =>
        new(new(n + .25, .5, .75), new(8, 4, 2), profile ?? EmissionProfile.Steady, birth);

    [TestMethod]
    public void StableSamplesAreSharedButEachFrameHasItsOwnCurrentIdentity()
    {
        var registry = Registry();
        for (int n = 0; n < 4096; n++) registry.Upsert(Id(n), Definition(n));
        LightFrame first = registry.Capture(1, 0);
        var packet = new GpuLightData(); packet.Update(first, default);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2; i < 202; i++)
        {
            LightFrame frame = registry.Capture(i, i * .02);
            Assert.IsTrue(frame.SharesSamplesWith(first));
            Assert.IsFalse(packet.Update(frame, default));
            Assert.AreEqual(i, frame.Frame); Assert.AreSame(frame, packet.SourceFrame);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // An old 4096-entry array per frame alone exceeded 40 MB here. Assertions and frame headers
        // are allowed, but copying the table every frame is not. This is allocation, not FPS timing.
        Assert.IsTrue(allocated < 1_000_000, $"Stable frame path allocated {allocated} bytes.");
        Assert.AreEqual(4096, registry.Count); Assert.AreEqual(first.World.Session, registry.World.Session);
        Vector3 oldEnergy = first.Samples[0].Intensity;
        registry.Upsert(Id(0), new(new(.25, .5, .75), Vector3.One, EmissionProfile.Steady));
        LightFrame changed = registry.Capture(202, 5);
        Assert.IsFalse(changed.SharesSamplesWith(first)); Assert.AreEqual(oldEnergy, first.Samples[0].Intensity);
        Assert.IsTrue(packet.Update(changed, default)); Assert.AreEqual(0, packet.ChangedRowStart); Assert.AreEqual(1, packet.ChangedRowCount);
    }

    [TestMethod]
    public void FutureBirthLiveFlameWindAndFinishedFlashBoundSampleReuseCorrectly()
    {
        var registry = Registry(); registry.Upsert(Id(0), Definition(0, birth: 2));
        LightFrame before = registry.Capture(1, 0), stillBefore = registry.Capture(2, 1);
        Assert.IsTrue(before.SharesSamplesWith(stillBefore)); Assert.AreEqual(Vector3.Zero, before.Samples[0].Intensity);
        LightFrame born = registry.Capture(3, 2);
        Assert.IsFalse(before.SharesSamplesWith(born)); Assert.AreEqual(new Vector3(8, 4, 2), born.Samples[0].Intensity);
        Assert.IsTrue(born.SharesSamplesWith(registry.Capture(4, 3, 1)));
        registry.Upsert(Id(0), Definition(0, EmissionProfile.Candle));
        LightFrame flame = registry.Capture(5, 4), same = registry.Capture(6, 4);
        Assert.IsTrue(flame.SharesSamplesWith(same));
        LightFrame wind = registry.Capture(7, 4, 1), later = registry.Capture(8, 4.1, 1);
        Assert.IsFalse(flame.SharesSamplesWith(wind)); Assert.IsFalse(wind.SharesSamplesWith(later));
        registry.Upsert(Id(0), Definition(0, EmissionProfile.Lightning, 5));
        LightFrame future = registry.Capture(9, 4.2), futureAgain = registry.Capture(10, 4.3, .1);
        Assert.IsTrue(future.SharesSamplesWith(futureAgain));
        LightFrame flash = registry.Capture(11, 5.01), flashAgain = registry.Capture(12, 5.02);
        Assert.IsFalse(flash.SharesSamplesWith(flashAgain));
        LightFrame ended = registry.Capture(13, 6), endedAgain = registry.Capture(14, 7, 1);
        Assert.IsTrue(ended.SharesSamplesWith(endedAgain)); Assert.AreEqual(Vector3.Zero, ended.Samples[0].Intensity);
        registry.Reset(new(Guid.NewGuid(), 1)); Assert.AreEqual(0, registry.Capture(1, 0).Samples.Length);
    }

    [TestMethod]
    public void DeltaBoundsSurviveUnchangedFramesAndContainEveryChangedTexel()
    {
        var registry = Registry(); var packet = new GpuLightData();
        for (int n = 0; n < 64; n++) registry.Upsert(Id(n), Definition(n));
        packet.Update(registry.Capture(1, 0), default); float[] prior = packet.Pixels.ToArray();
        registry.Upsert(Id(31), new(new(31.25, .5, .75), new(16, 8, 4), EmissionProfile.Steady));
        Assert.IsTrue(packet.Update(registry.Capture(2, 1), default));
        Assert.AreEqual(31, packet.ChangedRowStart); Assert.AreEqual(1, packet.ChangedRowCount);
        Assert.IsFalse(packet.Update(registry.Capture(3, 2), default));
        Assert.AreEqual(31, packet.ChangedRowStart); Assert.AreEqual(1, packet.ChangedRowCount);
        float[] after = packet.Pixels.ToArray();
        for (int i = 0; i < after.Length; i++) if (i / 8 != 31) Assert.AreEqual(prior[i], after[i]);
        registry.Remove(Id(63)); packet.Update(registry.Capture(4, 3), default);
        Assert.AreEqual(63, packet.ChangedRowStart); Assert.AreEqual(1, packet.ChangedRowCount);
        Assert.IsTrue(packet.Pixels[(63 * 8)..].ToArray().All(x => x == 0));
        registry.Remove(Id(0)); packet.Update(registry.Capture(5, 4), default);
        Assert.AreEqual(0, packet.ChangedRowStart); Assert.AreEqual(63, packet.ChangedRowCount);
        LightFrame last = registry.Capture(6, 5);
        Assert.IsTrue(packet.Update(last, new(1, 0, 0))); Assert.AreEqual(-.0f + 1.25f - 1, packet.Pixels[0]);
        Assert.IsFalse(packet.Update(last, new(1, 0, 0)));
        registry.Upsert(Id(0), Definition(0));
        registry.Upsert(Id(64), Definition(64)); registry.Upsert(Id(65), Definition(65));
        packet.Update(registry.Capture(7, 6), default);
        Assert.AreEqual(128, packet.Height); Assert.AreEqual(0, packet.ChangedRowStart); Assert.AreEqual(128, packet.ChangedRowCount);
    }

    [TestMethod]
    public void RejectedPacketsLeaveThePreviousPixelsRevisionAndDeltaIntact()
    {
        var registry = Registry(); registry.Upsert(Id(0), Definition(0));
        var packet = new GpuLightData(); LightFrame valid = registry.Capture(1, 0); packet.Update(valid, default);
        float[] original = packet.Pixels.ToArray(); long revision = packet.PixelRevision;
        LightSample good = valid.Samples[0];
        foreach (LightSample malformed in new[] {
            good with {Position = new(double.MaxValue, 0, 0)}, good with {Position = new(0, double.MaxValue, 0)},
            good with {Position = new(0, 0, double.MaxValue)}, good with {Radius = double.MaxValue},
            good with {Radius = -1}, good with {Intensity = new(float.NaN, 1, 1)} })
        {
            var frame = new LightFrame(valid.World, 2, 1, valid.Revisions, new[] {malformed});
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => packet.Update(frame, default));
            Assert.AreSame(valid, packet.SourceFrame); Assert.AreEqual(revision, packet.PixelRevision);
            CollectionAssert.AreEqual(original, packet.Pixels.ToArray());
        }
        Assert.ThrowsException<ArgumentNullException>(() => packet.Update(null!, default));
        Assert.ThrowsException<ArgumentException>(() => packet.Update(new(valid.World, 2, -1, valid.Revisions, new[] {good}), default));
        Assert.ThrowsException<ArgumentException>(() => packet.Update(new(valid.World, 1, 0, valid.Revisions, new[] {good}), default));
    }

    [TestMethod]
    public void RegistryGuardsPreserveExistingSourceAndClock()
    {
        var registry = Registry(); var definition = Definition(0); registry.Upsert(Id(0), definition);
        var revisions = registry.Revisions; registry.Upsert(Id(0), definition); Assert.AreEqual(revisions, registry.Revisions);
        Assert.IsFalse(registry.Remove(Id(99))); Assert.AreEqual(revisions, registry.Revisions);
        registry.Capture(1, 1);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => registry.Capture(2, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => registry.Capture(2, 2, double.NaN));
        Assert.ThrowsException<ArgumentNullException>(() => registry.Upsert(Id(0), null!));
        Assert.ThrowsException<ArgumentNullException>(() => new LightDefinition(default, Vector3.One, null!));
        foreach (int count in new[] {0, -1, 65})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LightDefinition(default, Vector3.One, EmissionProfile.Candle, componentCount:count));
        Assert.AreEqual(2L, registry.Capture(2, 2).Frame);
        registry.Upsert(Id(0), new(default, Vector3.Zero, EmissionProfile.Steady)); Assert.AreEqual(0, registry.Count);
        Assert.AreEqual(0, registry.Capture(3, 3).Samples.Length);
    }
}
