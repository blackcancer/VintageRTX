using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class EmissionMultiplicityTests
{
    private static string Asset() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "emission.json"));

    [TestMethod]
    public void EveryChandelierCountAndGroundCandleQuantityUsesCurrentCode()
    {
        var catalog = EmissionCatalog.Parse(Asset());
        for (int count = 0; count <= 8; count++)
            foreach (EmissionTarget target in new[] { EmissionTarget.Block, EmissionTarget.Item })
                Assert.AreEqual(count, catalog.Resolve("game:chandelier-brass-candle" + count, target).ComponentCount);
        for (int count = 1; count <= 9; count++)
        {
            Assert.AreEqual(count, catalog.Resolve("game:bunchocandles-" + count, EmissionTarget.Block).ComponentCount);
            Assert.AreEqual(count, catalog.Resolve("game:candles-" + count, EmissionTarget.Block).ComponentCount);
        }
        Assert.AreEqual(1, catalog.Resolve("game:candle", EmissionTarget.Item).ComponentCount);
        Assert.AreEqual(1, catalog.Resolve("other:candles-64", EmissionTarget.Block).ComponentCount);
    }

    [TestMethod]
    public void AddRemoveAndEmptyDoNotRetainPhantomFlamesOrMultiplyTheProvidersEnergy()
    {
        var catalog = EmissionCatalog.Parse(Asset());
        var registry = new LightRegistry(new(Guid.NewGuid(), 0));
        var owner = new LightId(SourceKind.Block, 10, 20, 30, 0, 0);
        long frame = 0;
        foreach (int count in new[] { 1, 2, 8, 3, 1, 0, 4, 0 })
        {
            var selection = catalog.Resolve("game:chandelier-brass-candle" + count, EmissionTarget.Block);
            Vector3 currentRuntimeTotal = count == 0 ? Vector3.Zero : new(2, 1, .5f);
            LightDefinition? definition = selection.CreateLight(new(10.5, 20.5, 30.5), currentRuntimeTotal);
            if (definition is null) registry.Remove(owner); else registry.Upsert(owner, definition);
            LightFrame captured = registry.Capture(++frame, 3);
            if (count == 0) { Assert.AreEqual(0, captured.Samples.Length); continue; }
            Assert.AreEqual(1, captured.Samples.Length); // One aggregate, not N coincident GPU lights.
            double modulation = 0;
            for (int component = 0; component < count; component++)
                modulation += EmissionWaveform.Evaluate(selection.Profile,
                    EmissionGroupWaveform.ComponentSeed(owner.Seed, component), 3, 0);
            Vector3 expected = currentRuntimeTotal * (float)(modulation / count);
            Assert.AreEqual(expected, captured.Samples[0].Intensity);
            Assert.AreEqual(currentRuntimeTotal, definition!.Intensity);
        }
    }

    [TestMethod]
    public void CountChangePreservesExistingPhasesAndDoesNotRestartTheTimeline()
    {
        ulong seed = 123;
        var profile = EmissionProfile.Candle;
        for (int i = 0; i < 300; i++)
        {
            double t = i / 30.0;
            double two = EmissionGroupWaveform.Evaluate(profile, seed, t, 0, 2);
            double three = EmissionGroupWaveform.Evaluate(profile, seed, t, 0, 3);
            double added = EmissionWaveform.Evaluate(profile, EmissionGroupWaveform.ComponentSeed(seed, 2), t, 0);
            Assert.AreEqual(three * 3, two * 2 + added, 1e-12);
            Assert.AreEqual(two, EmissionGroupWaveform.Evaluate(profile, seed, i * 4 / 120.0, 0, 2));
            Assert.AreEqual(EmissionWaveform.Evaluate(profile, seed, t, 0),
                EmissionGroupWaveform.Evaluate(profile, seed, t, 0, 1));
        }
    }

    [TestMethod]
    public void ProfilePatchAffectsAllCountsAndSteadyRetainsTotalIntensity()
    {
        var document = JsonNode.Parse(Asset())!;
        document["bindings"]!["vintagertx:chandelier"]!["profile"] = "vintagertx:steady";
        var catalog = EmissionCatalog.Parse(document.ToJsonString());
        for (int count = 1; count <= 8; count++)
        {
            var selection = catalog.Resolve("game:chandelier-iron-candle" + count, EmissionTarget.Block);
            Assert.AreEqual(count, selection.ComponentCount);
            Assert.AreEqual(1.0, EmissionGroupWaveform.Evaluate(selection.Profile, 23, 3, 0, count));
            Assert.IsNull(selection.CreateLight(default, Vector3.Zero));
            Assert.AreEqual(Vector3.One, selection.CreateLight(default, Vector3.One)!.Intensity);
        }
    }

    [TestMethod]
    public void SingleInventoryCandleDoesNotInheritTheNumberOfItemsInAStack()
    {
        var selection = EmissionCatalog.Parse(Asset()).Resolve("game:candle", EmissionTarget.Item);
        Assert.AreEqual(1, selection.CreateLight(default, Vector3.One)!.ComponentCount);
        var off = EmissionCatalog.Parse(Asset()).Resolve("game:chandelier-brass-candle0", EmissionTarget.Block);
        Assert.IsNull(off.CreateLight(default, Vector3.One));
    }

    [TestMethod]
    public void CountAndProfileChangesAreEmissionChangesWithinTheAggregateContract()
    {
        var registry = new LightRegistry(new(Guid.NewGuid(), 0)); var id = new LightId();
        registry.Upsert(id, new(new(0, 0, 2), Vector3.One, EmissionProfile.Candle, componentCount: 1));
        LightFrame old = registry.Capture(1, 1); Vector3 oldEnergy = old.Samples[0].Intensity;
        var revision = registry.Revisions;
        registry.Upsert(id, new(new(0, 0, 2), Vector3.One, EmissionProfile.Candle, componentCount: 5));
        Assert.AreEqual(revision.Layout, registry.Revisions.Layout);
        Assert.IsTrue(registry.Revisions.Emission > revision.Emission);
        registry.Capture(2, 1);
        Assert.AreEqual(oldEnergy, old.Samples[0].Intensity);
        registry.Reset(new(Guid.NewGuid(), 1)); Assert.AreEqual(0, registry.Capture(1, 0).Samples.Length);
    }

    [DataTestMethod]
    [DataRow("-1")]
    [DataRow("65")]
    [DataRow("1.5")]
    [DataRow("null")]
    [DataRow("\"3\"")]
    public void MalformedCountCannotReplaceThePreviousCatalog(string encoded)
    {
        var state = new EmissionCatalogState(); Assert.IsTrue(state.TryReplace(Asset(), "before"));
        var previous = state.Current;
        var json = JsonNode.Parse(Asset())!;
        json["bindings"]!["vintagertx:candle"]!["componentCounts"]!["game:candle"] = JsonNode.Parse(encoded);
        Assert.IsFalse(state.TryReplace(json.ToJsonString(), "invalid")); Assert.AreSame(previous, state.Current);
        StringAssert.Contains(state.LastError!, "componentCounts/game:candle");
    }

    [TestMethod]
    public void EngineAndLightningEnvelopesAreNotTurnedIntoIndependentFlames()
    {
        foreach (var profile in new[] { EmissionProfile.Engine, EmissionProfile.Lightning })
            for (int i = 0; i < 100; i++)
                Assert.AreEqual(EmissionWaveform.Evaluate(profile, 12, i / 100.0, 0),
                    EmissionGroupWaveform.Evaluate(profile, 12, i / 100.0, 0, 8));
    }
}
