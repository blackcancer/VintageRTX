using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class FiniteEmitterSamplingTests
{
    private static LightSample Source(double radius, DVec3 position = default) =>
        new(new(SourceKind.Extension, 1, 0, 0, 0, 0), position, new(4, 2, .5f), radius);

    [DataTestMethod]
    [DataRow(0.0)]
    [DataRow(0.00000001)]
    [DataRow(0.01)]
    [DataRow(0.5)]
    [DataRow(1.99)]
    public void AlignedLambertReceiverMatchesIndependentProjectedSolidAngle(double radius)
    {
        LightSample light = Source(radius, new(0, 0, 2));
        Vector3 sum = Vector3.Zero;
        const int count = 4096;
        for (int i = 0; i < count; i++)
        {
            Assert.IsTrue(FiniteEmitterSampling.TrySample(light, default, (i + .5) / count,
                (i * .6180339887498949) % 1, out var sample));
            // E = pi L sin^2(thetaMax) = I/d^2, independent of sphere radius here.
            sum += sample.RadianceOverPdf * (float)(sample.Direction.Z / Math.PI / count);
            Assert.AreEqual(1, sample.Direction.LengthSquared, 1e-12);
        }
        Vector3 expected = light.Intensity / (float)(4 * Math.PI);
        Assert.IsTrue(Vector3.Distance(sum, expected) < 2e-5f, $"{sum} != {expected}");
    }

    [TestMethod]
    public void SamplesEndOnTheVisibleHemisphereNotAtTheCenterOrFarSurface()
    {
        LightSample light = Source(.25, new(3, 2, 4));
        DVec3 receiver = new(3, 2, 1);
        for (int i = 0; i < 512; i++)
        {
            Assert.IsTrue(FiniteEmitterSampling.TrySample(light, receiver, (i + .5) / 512,
                (i * .3819660112501051) % 1, out var sample));
            DVec3 relative = receiver + sample.Direction * sample.Distance - light.Position;
            Assert.AreEqual(.25 * .25, relative.LengthSquared, 1e-12);
            Assert.IsTrue(DVec3.Dot(relative, -sample.Direction) >= 0);
            Assert.IsTrue(sample.Distance < 3);
            Assert.IsTrue(sample.SolidAngle > 0);
        }
    }

    [TestMethod]
    public void TranslationDoesNotChangeRelativeEmissionAndPointLimitIsFinite()
    {
        DVec3 shift = new(10_000_000, -4_000_000, 8_000_000);
        LightSample a = Source(.01, new(1, 2, 3));
        LightSample b = a with { Position = a.Position + shift };
        Assert.IsTrue(FiniteEmitterSampling.TrySample(a, default, .2, .8, out var first));
        Assert.IsTrue(FiniteEmitterSampling.TrySample(b, shift, .2, .8, out var second));
        Assert.AreEqual(first, second);
        Assert.IsTrue(FiniteEmitterSampling.TrySample(a with { Radius = 1e-12 }, default, .2, .8, out var tiny));
        Assert.IsTrue(FiniteEmitterSampling.TrySample(a with { Radius = 0 }, default, .2, .8, out var point));
        Assert.IsTrue(Vector3.Distance(tiny.RadianceOverPdf, point.RadianceOverPdf) < 1e-7);
        Assert.AreEqual(point.Distance, tiny.Distance, 1e-10);
    }

    [TestMethod]
    public void DarkSourcesRemainDarkAndUnsupportedInteriorDoesNotBecomeAPoint()
    {
        LightSample light = Source(.5, new(0, 0, 2)) with { Intensity = Vector3.Zero };
        Assert.IsTrue(FiniteEmitterSampling.TrySample(light, default, .5, .5, out var dark));
        Assert.AreEqual(Vector3.Zero, dark.RadianceOverPdf);
        Assert.IsFalse(FiniteEmitterSampling.TrySample(Source(2, new(0, 0, 2)), default, .5, .5, out _));
        Assert.IsFalse(FiniteEmitterSampling.TrySample(Source(3, new(0, 0, 2)), default, .5, .5, out _));
        Assert.IsFalse(FiniteEmitterSampling.TrySample(Source(0), default, .5, .5, out _));
    }

    [TestMethod]
    public void ARadiusChangeAndFlickerHaveDifferentInvalidationContracts()
    {
        var registry = new LightRegistry(new(Guid.NewGuid(), 0));
        LightId id = new(SourceKind.Block, 1, 2, 3, 0, 0);
        registry.Upsert(id, new(new(0, 0, 2), Vector3.One, EmissionProfile.Candle, radius: .02));
        var before = registry.Revisions;
        LightFrame frame = registry.Capture(1, 1);
        LightFrame later = registry.Capture(2, 1.5);
        Assert.AreEqual(before, registry.Revisions);
        Assert.AreNotEqual(frame.Samples[0].Intensity, later.Samples[0].Intensity);
        Assert.IsTrue(FiniteEmitterSampling.TrySample(frame.Samples[0], default, .2, .4, out var direct));
        Assert.IsTrue(FiniteEmitterSampling.TrySample(frame.Samples[0], default, .2, .4, out var reflected));
        Assert.AreEqual(direct, reflected);
        registry.Upsert(id, new(new(0, 0, 2), Vector3.One, EmissionProfile.Candle, radius: .04));
        Assert.IsTrue(registry.Revisions.Layout > before.Layout);
    }

    [DataTestMethod]
    [DataRow(-0.1, 0.5)]
    [DataRow(1.0, 0.5)]
    [DataRow(0.5, 1.0)]
    [DataRow(double.NaN, 0.5)]
    [DataRow(0.5, double.PositiveInfinity)]
    public void InvalidVariatesAreRejected(double u, double v) =>
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            FiniteEmitterSampling.TrySample(Source(.1, new(0, 0, 2)), default, u, v, out _));
}
