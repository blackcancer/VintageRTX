using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class EmissionCatalogTests
{
    private static string Asset() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "emission.json"));
    private static JsonObject Document() => JsonNode.Parse(Asset())!.AsObject();
    private static EmissionCatalog Load() => EmissionCatalog.Parse(Asset());
    private static EmissionCatalog Edit(Action<JsonObject> change)
    { JsonObject json = Document(); change(json); return EmissionCatalog.Parse(json.ToJsonString()); }
    private static JsonObject Binding(string profile, string pattern = "game:chandelier-*", int priority = 0) => new()
    {
        ["profile"] = profile, ["codes"] = new JsonArray(pattern),
        ["targets"] = new JsonArray("block", "item"), ["priority"] = priority
    };

    [DataTestMethod]
    [DataRow("game:candle", EmissionTarget.Item, "vintagertx:candle")]
    [DataRow("game:candle", EmissionTarget.Block, "vintagertx:candle")]
    [DataRow("game:candles-3", EmissionTarget.Block, "vintagertx:candle")]
    [DataRow("game:bunchocandles-7", EmissionTarget.Block, "vintagertx:candle")]
    [DataRow("game:chandelier-brass-candle1", EmissionTarget.Block, "vintagertx:chandelier")]
    [DataRow("game:chandelier-brass-candle8", EmissionTarget.Item, "vintagertx:chandelier")]
    [DataRow("game:torch-up", EmissionTarget.Block, "vintagertx:torch")]
    [DataRow("game:oillamp", EmissionTarget.Item, "vintagertx:oillamp")]
    [DataRow("game:lantern-brass", EmissionTarget.Item, "vintagertx:lantern")]
    public void BundledAssetResolvesSourceFamilies(string code, EmissionTarget target, string expected)
    {
        EmissionSelection result = Load().Resolve(code, target);
        Assert.AreEqual(expected, result.ProfileId);
        Assert.AreEqual(EmissionKind.Flame, result.Profile.Kind);
    }

    [TestMethod]
    public void EmptyChandelierDoesNotAcquireEnergyOrMultiplyRuntimeCandleCount()
    {
        EmissionSelection selection = Load().Resolve("game:chandelier-brass-candle0", EmissionTarget.Block);
        Assert.IsNull(selection.CreateLight(new(0, 0, 0), Vector3.Zero));
        Vector3 runtime = new(2, 1, .5f);
        LightDefinition light = Load().Resolve("game:chandelier-brass-candle8", EmissionTarget.Block)
            .CreateLight(new(0, 0, 0), runtime)!;
        Assert.AreEqual(runtime, light.Intensity); // The game already owns brightness versus candle count.
    }

    [TestMethod]
    public void EditedAssetActuallyChangesWaveformWithoutChangingGeometry()
    {
        EmissionSelection original = Load().Resolve("game:candle", EmissionTarget.Item);
        EmissionCatalog patched = Edit(json => json["profiles"]!["vintagertx:candle"]!["amplitude"] = 0);
        EmissionSelection after = patched.Resolve("game:candle", EmissionTarget.Item);
        var registry = new LightRegistry(new(Guid.NewGuid(), 0));
        var id = new LightId(SourceKind.Entity, 123, 0, 0, 0, 0);
        registry.Upsert(id, original.CreateLight(new(2, 3, 4), new(2, 1, .5f))!);
        LightFrame before = registry.Capture(1, 0);
        LightRevisions revision = registry.Revisions;
        registry.Upsert(id, after.CreateLight(new(2, 3, 4), new(2, 1, .5f))!);
        Assert.AreEqual(revision.Layout, registry.Revisions.Layout);
        Assert.IsTrue(registry.Revisions.Emission > revision.Emission);
        double deviation = 0;
        for (int i = 1; i <= 120; i++)
        {
            double seconds = i / 60.0;
            Assert.AreEqual(new Vector3(2, 1, .5f), registry.Capture(i + 1, seconds).Samples[0].Intensity);
            deviation += Math.Abs(EmissionWaveform.Evaluate(original.Profile, id.Seed, seconds, 0) - 1);
        }
        Assert.IsTrue(deviation > .1);
        Assert.AreEqual(1, before.Samples.Length); // Published frames remain independent of reconfiguration.
    }

    [TestMethod]
    public void LocustAndPlayerFollowRuntimeAndUnknownModsDoNotInheritFlameNoise()
    {
        EmissionCatalog catalog = Load();
        Assert.AreEqual(EmissionKind.EngineDriven, catalog.Resolve("game:locust-sawblade", EmissionTarget.Entity).Profile.Kind);
        Assert.IsNull(catalog.Resolve("game:locust-sawblade", EmissionTarget.Entity).CreateLight(default, Vector3.Zero));
        Assert.AreEqual(EmissionKind.EngineDriven, catalog.Resolve("game:player", EmissionTarget.Entity).Profile.Kind);
        Assert.AreEqual(EmissionKind.Steady, catalog.Resolve("other:torch-red", EmissionTarget.Block).Profile.Kind);
        Assert.AreEqual(EmissionKind.Steady, catalog.Resolve("game:torchbattery", EmissionTarget.Block).Profile.Kind);
        Assert.AreEqual(EmissionKind.EngineDriven, catalog.Resolve("game:torch-up", EmissionTarget.Entity).Profile.Kind);
    }

    [TestMethod]
    public void AddedModProfileAndBindingNeedNoCSharpChange()
    {
        EmissionCatalog catalog = Edit(json =>
        {
            json["profiles"]!["other:soft-flame"] = new JsonObject { ["kind"] = "flame", ["amplitude"] = .3, ["frequencyHz"] = 4 };
            json["bindings"]!["other:lamp"] = Binding("other:soft-flame", "other:lamp-*");
        });
        EmissionSelection result = catalog.Resolve("other:lamp-blue", EmissionTarget.Item);
        Assert.AreEqual("other:soft-flame", result.ProfileId);
        Assert.AreEqual(.3, result.Profile.Amplitude);
        Assert.AreEqual(EmissionKind.Steady, catalog.Resolve("another:lamp-blue", EmissionTarget.Item).Profile.Kind);
    }

    [TestMethod]
    public void PriorityThenSpecificityAreExplicitAndIndependentOfJsonOrder()
    {
        EmissionCatalog catalog = Edit(json =>
        {
            json["bindings"]!["custom:exact"] = Binding("vintagertx:steady", "game:chandelier-brass-candle8");
            json["bindings"]!["custom:high"] = Binding("vintagertx:lantern", "game:chandelier-*", 10);
        });
        Assert.AreEqual("vintagertx:lantern", catalog.Resolve("game:chandelier-brass-candle8", EmissionTarget.Block).ProfileId);
        EmissionCatalog exact = Edit(json => json["bindings"]!["custom:exact"] = Binding("vintagertx:steady", "game:chandelier-brass-candle8"));
        Assert.AreEqual("vintagertx:steady", exact.Resolve("game:chandelier-brass-candle8", EmissionTarget.Block).ProfileId);
        Assert.AreEqual("vintagertx:chandelier", exact.Resolve("game:chandelier-brass-candle7", EmissionTarget.Block).ProfileId);
    }

    [TestMethod]
    public void EqualRankConflictsAreNotHiddenByEnumerationOrder()
    {
        JsonObject json = Document();
        json["bindings"]!["custom:conflict"] = Binding("vintagertx:steady");
        string encoded = json.ToJsonString();
        EmissionCatalog catalog = EmissionCatalog.Parse(encoded);
        EmissionConfigurationException error = Assert.ThrowsException<EmissionConfigurationException>(
            () => catalog.Resolve("game:chandelier-brass-candle8", EmissionTarget.Block));
        StringAssert.Contains(error.Message, "ambiguous");
        var reversed = new JsonObject();
        foreach (var pair in json["bindings"]!.AsObject().Reverse()) reversed[pair.Key] = pair.Value!.DeepClone();
        json["bindings"] = reversed;
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse(json.ToJsonString())
            .Resolve("game:chandelier-brass-candle8", EmissionTarget.Block));
    }

    [TestMethod]
    public void OverrideChangesProfileButNeverOverridesRuntimeActivationOrColor()
    {
        var settings = EmissionOverride.Parse("""{"profile":"vintagertx:steady","intensityScale":2} """);
        EmissionSelection result = Load().Resolve("game:lantern-red", EmissionTarget.Item, settings);
        Assert.AreEqual(EmissionKind.Steady, result.Profile.Kind);
        Vector3 color = new(.4f, .1f, .02f);
        Assert.AreEqual(color * 2, result.CreateLight(default, color)!.Intensity);
        Assert.IsNull(result.CreateLight(default, Vector3.Zero));
        Assert.IsNull(Load().Resolve("game:lantern-red", EmissionTarget.Item,
            EmissionOverride.Parse("""{"enabled":false}""")).CreateLight(default, color));
    }

    [TestMethod]
    public void DisabledBindingOverridesLowerPriorityAndZeroScaleIsNotMissingData()
    {
        EmissionCatalog catalog = Edit(json =>
        {
            JsonObject rule = Binding("vintagertx:candle", "game:candle", 100);
            rule["enabled"] = false; json["bindings"]!["custom:off"] = rule;
        });
        Assert.IsFalse(catalog.Resolve("game:candle", EmissionTarget.Block).Enabled);
        Assert.IsNull(catalog.Resolve("game:candle", EmissionTarget.Block).CreateLight(default, Vector3.One));
        Assert.IsNull(Load().Resolve("game:candle", EmissionTarget.Item,
            EmissionOverride.Parse("""{"intensityScale":0}""")).CreateLight(default, Vector3.One));
    }

    [DataTestMethod]
    [DataRow("amplitude", "-0.1")]
    [DataRow("amplitude", "0.81")]
    [DataRow("amplitude", "\"0.2\"")]
    [DataRow("amplitude", "null")]
    [DataRow("amplitude", "1e999")]
    [DataRow("frequencyHz", "0")]
    [DataRow("frequencyHz", "101")]
    [DataRow("windSensitivity", "2")]
    [DataRow("durationSeconds", "0")]
    [DataRow("durationSeconds", "61")]
    [DataRow("frequencyHzz", "5")]
    [DataRow("kind", "\"Flame\"")]
    [DataRow("kind", "\"flicker\"")]
    public void MalformedProfileIsRejectedWithItsAssetPath(string field, string value)
    {
        JsonObject json = Document();
        json["profiles"]!["vintagertx:candle"]![field] = JsonNode.Parse(value);
        var error = Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse(json.ToJsonString(), "custom:config/emission.json"));
        StringAssert.Contains(error.Message, "custom:config/emission.json/profiles/vintagertx:candle");
    }

    [TestMethod]
    public void UnknownProfilesFieldsVersionsAndDuplicateKeysAreRejected()
    {
        Assert.ThrowsException<EmissionConfigurationException>(() => Edit(json => json["schemaVersion"] = 2));
        Assert.ThrowsException<EmissionConfigurationException>(() => Edit(json => json["bindngs"] = new JsonObject()));
        Assert.ThrowsException<EmissionConfigurationException>(() => Edit(json => json["bindings"]!["vintagertx:candle"]!["profile"] = "missing:profile"));
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse("""{"schemaVersion":1,"schemaVersion":1,"profiles":{},"bindings":{}}"""));
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionOverride.Parse("""{"profile":"vintagertx:candle","color":[1,0,0]}"""));
        Assert.ThrowsException<EmissionConfigurationException>(() => Load().Resolve("game:candle", EmissionTarget.Item, new("missing:profile")));
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionOverride.Parse("null"));
    }

    [TestMethod]
    public void InvalidReloadCannotPartiallyReplaceCatalogOrLoseRevision()
    {
        var state = new EmissionCatalogState();
        Assert.IsTrue(state.TryReplace(Asset(), "original"));
        EmissionCatalog before = state.Current; long revision = state.Revision;
        Assert.IsFalse(state.TryReplace("{bad json", "broken"));
        Assert.AreSame(before, state.Current); Assert.AreEqual(revision, state.Revision);
        StringAssert.Contains(state.LastError!, "broken");
        Assert.IsTrue(state.TryReplace(Asset(), "original"));
        Assert.AreEqual(revision, state.Revision); Assert.IsNull(state.LastError);
        JsonObject json = Document(); json["profiles"]!["vintagertx:candle"]!["amplitude"] = .02;
        Assert.IsTrue(state.TryReplace(json.ToJsonString(), "patched"));
        Assert.AreEqual(revision + 1, state.Revision);
        Assert.AreEqual(.12, before.GetProfile("vintagertx:candle").Amplitude);
        Assert.AreEqual(.02, state.Current.GetProfile("vintagertx:candle").Amplitude);
    }

    [TestMethod]
    public void LightningRequiresWeatherBirthTimeAndDoesNotReplayOnLookup()
    {
        Assert.ThrowsException<EmissionConfigurationException>(() => Edit(json =>
            json["bindings"]!["vintagertx:candle"]!["profile"] = "vintagertx:lightning"));
        EmissionCatalog catalog = Edit(json => json["bindings"]!["custom:flash"] = new JsonObject
        { ["codes"] = new JsonArray("game:lightning"), ["targets"] = new JsonArray("weather"), ["profile"] = "vintagertx:lightning" });
        EmissionSelection selection = catalog.Resolve("game:lightning", EmissionTarget.Weather);
        Assert.IsTrue(EmissionWaveform.Evaluate(selection.Profile, 7, 10.01, 10) > 0);
        Assert.AreEqual(0.0, EmissionWaveform.Evaluate(catalog.Resolve("game:lightning", EmissionTarget.Weather).Profile, 7, 11, 10));
    }

    [TestMethod]
    public void PatchedFlamesKeepFrameRateAndPerSourceIndependence()
    {
        EmissionProfile profile = Edit(json => json["profiles"]!["vintagertx:candle"]!["amplitude"] = .3).GetProfile("vintagertx:candle");
        double separation = 0;
        for (int i = 0; i < 1000; i++)
        {
            double value = EmissionWaveform.Evaluate(profile, 1, i / 30.0, 0, .5);
            Assert.AreEqual(value, EmissionWaveform.Evaluate(profile, 1, i * 4 / 120.0, 0, .5));
            separation += Math.Abs(value - EmissionWaveform.Evaluate(profile, 2, i / 30.0, 0, .5));
        }
        Assert.IsTrue(separation > 1);
    }
}
