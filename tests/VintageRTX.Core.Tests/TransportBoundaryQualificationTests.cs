using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class TransportBoundaryQualificationTests
{
    private static readonly DVec3 N = new(0, 0, 1);
    private static readonly SurfaceMaterial White = new(SurfaceKind.Diffuse, Vector3.One);
    private static readonly SurfaceMaterial Metal = new(SurfaceKind.Conductor, Vector3.One, 1, Vector3.One, Vector3.One);
    private static LightFrame Lights(params LightSample[] samples) => new(new(Guid.NewGuid(), 0), 1, 0, default, samples);
    private static LightSample Light(DVec3 p, Vector3 color) => new(default, p, color, 0);

    [TestMethod]
    public void SpectralAndSamplingInputBoundariesHaveNoSilentPositiveEnergy()
    {
        Vector3 rgb = new(.01f, .2f, 1);
        Assert.AreEqual(new Vector3(ColorSpace.Decode(rgb.X), ColorSpace.Decode(rgb.Y), ColorSpace.Decode(rgb.Z)), ColorSpace.Decode(rgb));
        Assert.ThrowsException<ArgumentException>(() => new DVec3(double.PositiveInfinity, 0, 0).Normalized());
        Assert.ThrowsException<ArgumentException>(() => Bsdf.ConductorFresnel(.5, double.NaN, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => OpticalInterface.Transmittance(new(-1, 0, 0), 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => FiniteEmitterSampling.TrySample(Light(N, Vector3.One), default, .5, -.1, out _));
        Assert.AreEqual(Vector3.Zero, Bsdf.Evaluate(Metal, N, N, -N));
        // These directions remain unit length to binary64 precision but approach opposite grazing directions.
        DVec3 almostEast = new(1, 0, 1e-200), almostWest = new(-1, 0, 1e-200);
        Assert.AreEqual(Vector3.Zero, Bsdf.Evaluate(Metal, N, almostEast, almostWest));
        Assert.AreEqual(0d, Bsdf.Pdf(Metal, N, almostEast, almostWest));
        Assert.AreEqual(Vector3.Zero, Bsdf.Evaluate(Metal, N, almostEast, N));
        var mirror = new SurfaceMaterial(SurfaceKind.Conductor, Vector3.One, 0, Vector3.One, Vector3.One);
        Assert.AreEqual(0d, Bsdf.Pdf(mirror, N, N, N));
        // Finite vertices can still overflow the arithmetic of an unrepresentably large triangle.
        var huge = new Triangle(default, new(1e308, 0, 0), new(0, 1e308, 0), 0);
        Assert.IsFalse(huge.Intersect(new(new(1, 1, 1), -N), 10, 0, out _));
    }

    [TestMethod]
    public void BlackBackgroundAndOccludedOrInactivePointSourcesStayBlack()
    {
        Triangle[] geometry = [new(new(-4, -4, 0), new(4, -4, 0), new(0, 4, 0), 0)];
        var ray = new Ray(new(0, 0, 1), -N);
        var tracer = new ReferenceTracer(geometry, new[] {White});
        Assert.ThrowsException<ArgumentException>(() => new ReferenceTracer(geometry, Array.Empty<SurfaceMaterial>()));
        foreach (int bounces in new[] {0, 65})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => tracer.Trace(ray, Lights(), 1, bounces));
        Assert.AreEqual(Vector3.Zero, new ReferenceTracer([], new[] {White}).Trace(ray, Lights(), 1));
        LightFrame excluded = Lights(Light(N, Vector3.Zero), Light(default, Vector3.One),
            Light(-N, Vector3.One), Light(new(0, 0, 1.5e-7), Vector3.One));
        Assert.AreEqual(Vector3.Zero, tracer.Trace(ray, excluded, 1));
        var black = new ReferenceTracer(geometry, new[] {new SurfaceMaterial(SurfaceKind.Diffuse, Vector3.Zero)});
        Assert.AreEqual(Vector3.Zero, black.Trace(ray, Lights(Light(N, Vector3.One)), 1));
        var back = new ReferenceTracer(new[] {new Triangle(geometry[0].C, geometry[0].B, geometry[0].A, 0)}, new[] {White});
        Vector3 reference = tracer.Trace(ray, Lights(Light(N, Vector3.One)), 1, 1);
        Assert.AreEqual(reference, back.Trace(ray, Lights(Light(N, Vector3.One)), 1, 1));
        Assert.IsTrue(reference.X > 0);
        var metal = new ReferenceTracer(geometry, new[] {Metal});
        for(ulong seed = 0; seed < 128; seed++)
            Assert.IsTrue(LightDefinition.FiniteNonnegative(metal.Trace(new(new(3, 0, .1), new(-3, 0, -.1)), Lights(), seed)));
    }

    [TestMethod]
    public void FiniteSourcesCrossingEitherReceiverHorizonKeepAllSamplesInTheEstimator()
    {
        var scene = new CellScene(new(Guid.NewGuid(), 0));
        for(int z = -4; z < 5; z++) for(int y = -4; y < 5; y++) for(int x = -4; x < 5; x++)
            scene.Observe(new(x, y, z), CellGeometry.Empty);
        var snapshot = scene.Capture();
        var receiver = new SurfaceReceiver(new(.5, .5, .5), new DVec3(1, 0, .1).Normalized(), N, Vector3.One, 0);
        var surfaces = new DirectSurfaceFrame(snapshot, default, new(.6, .5, 2), 1, 1, new[] {receiver}, new[] {White});
        foreach (DVec3 center in new[] {new DVec3(2, .5, .4), new(-.1, .5, 2)})
        {
            var lights = new LightFrame(snapshot.World, 1, 0, default, new[] {new LightSample(default, center, Vector3.One, 1)});
            DirectLightingResult value = DirectLightingReference.Evaluate(surfaces, 0, lights, finiteSamples: 64);
            Assert.IsTrue(value.Traced < 64); Assert.IsTrue(value.Traced > 0);
            Assert.IsTrue(LightDefinition.FiniteNonnegative(value.Radiance));
        }
    }

    [TestMethod]
    public void UploadTicketsCannotExposeBuildingEvictedOrAlreadyPublishedRevisions()
    {
        var store = new RegionStore(new(Guid.NewGuid(), 0)); var ticket = store.Begin(default);
        Assert.IsFalse(store.TryGetUpload(ticket, out var missing)); Assert.IsNull(missing);
        Assert.IsFalse(store.TryGetUpload(ticket with {Region = new(1, 0, 0)}, out _));
        Assert.IsTrue(store.Complete(ticket, new([]))); Assert.IsTrue(store.TryGetUpload(ticket, out var ready));
        Assert.IsNotNull(ready); Assert.IsTrue(store.AcknowledgeUpload(ticket));
        Assert.IsFalse(store.TryGetUpload(ticket, out _));
    }

    [TestMethod]
    public void LowerRankCountPatternsCannotOverwriteAnExactRuntimeQuantity()
    {
        JsonNode json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "emission.json")))!;
        var counts = json["bindings"]!["vintagertx:candle"]!["componentCounts"]!.AsObject();
        counts["game:candle"] = 1; counts["game:candle*"] = 9;
        Assert.AreEqual(1, EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:candle", EmissionTarget.Block).ComponentCount);
    }
}
