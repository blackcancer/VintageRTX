using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class EmissionShapeAssetTests
{
    private static JsonObject Document() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "emission.json")))!.AsObject();

    [TestMethod]
    public void RadiusIsOptionalAndConfiguredValuesReachTheUnchangedFrameContract()
    {
        var json = Document();
        Assert.AreEqual(0.0, EmissionCatalog.Parse(json.ToJsonString())
            .Resolve("game:candle", EmissionTarget.Block).SourceRadius);
        json["bindings"]!["vintagertx:candle"]!["sourceRadius"] = .018;
        var selection = EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:candle", EmissionTarget.Block);
        LightDefinition light = selection.CreateLight(new(1, 2, 3), new(4, 2, 1))!;
        Assert.AreEqual(.018, light.Radius);
        Assert.AreEqual(new Vector3(4, 2, 1), light.Intensity);
        Assert.IsNull(selection.CreateLight(default, Vector3.Zero));
        Assert.AreEqual(0.0, selection.CreateLight(default, Vector3.One, radius: 0)!.Radius);
        var registry = new LightRegistry(new(Guid.NewGuid(), 0));
        registry.Upsert(new(SourceKind.Block, 1, 2, 3, 0, 0), light);
        Assert.AreEqual(.018, registry.Capture(1, 0).Samples[0].Radius);
    }

    [TestMethod]
    public void RadiusAttributeOverridesOnlyShapeNotRuntimeColorOrTemporalProfile()
    {
        var catalog = EmissionCatalog.Parse(Document().ToJsonString());
        var before = catalog.Resolve("game:candle", EmissionTarget.Item);
        var after = catalog.Resolve("game:candle", EmissionTarget.Item,
            EmissionOverride.Parse("""{"sourceRadius":0.025}"""));
        Assert.AreEqual(before.Profile, after.Profile);
        Assert.AreEqual(.025, after.SourceRadius);
        Assert.AreEqual(Vector3.One, after.CreateLight(default, Vector3.One)!.Intensity);
        Assert.IsNull(after.CreateLight(default, Vector3.Zero));
    }

    [DataTestMethod]
    [DataRow("-0.01")]
    [DataRow("16.01")]
    [DataRow("1e999")]
    [DataRow("null")]
    [DataRow("\"0.01\"")]
    public void InvalidRadiusRejectsTheWholeCandidateWithItsPath(string encoded)
    {
        var json = Document();
        var state = new EmissionCatalogState();
        Assert.IsTrue(state.TryReplace(json.ToJsonString(), "before"));
        var previous = state.Current;
        json["bindings"]!["vintagertx:candle"]!["sourceRadius"] = JsonNode.Parse(encoded);
        Assert.IsFalse(state.TryReplace(json.ToJsonString(), "patched"));
        Assert.AreSame(previous, state.Current);
        StringAssert.Contains(state.LastError!, "bindings/vintagertx:candle/sourceRadius");
        Assert.ThrowsException<EmissionConfigurationException>(() =>
            EmissionOverride.Parse("{\"sourceRadius\":" + encoded + "}"));
    }

    [TestMethod]
    public void AnExplicitProfileMustNotHideAnAmbiguousSizeOrEnergy()
    {
        var json = Document();
        JsonObject first = json["bindings"]!["vintagertx:candle"]!.AsObject();
        first["sourceRadius"] = .02;
        var second = first.DeepClone().AsObject(); second["sourceRadius"] = .08; second["intensityScale"] = 2;
        json["bindings"]!["custom:candle"] = second;
        var catalog = EmissionCatalog.Parse(json.ToJsonString());
        Assert.ThrowsException<EmissionConfigurationException>(() => catalog.Resolve("game:candle", EmissionTarget.Block,
            new(Profile: "vintagertx:steady")));
        Assert.ThrowsException<EmissionConfigurationException>(() => catalog.Resolve("game:candle", EmissionTarget.Block,
            new(Profile: "vintagertx:steady", SourceRadius: .03)));
        var result = catalog.Resolve("game:candle", EmissionTarget.Block,
            new(Profile: "vintagertx:steady", SourceRadius: .03, IntensityScale: 1));
        Assert.AreEqual(.03, result.SourceRadius); Assert.AreEqual(1.0, result.IntensityScale);
        var reversed = new JsonObject();
        foreach (var field in json["bindings"]!.AsObject().Reverse()) reversed[field.Key] = field.Value!.DeepClone();
        json["bindings"] = reversed;
        Assert.AreEqual(result, EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:candle", EmissionTarget.Block,
            new(Profile: "vintagertx:steady", SourceRadius: .03, IntensityScale: 1)));
    }
}
