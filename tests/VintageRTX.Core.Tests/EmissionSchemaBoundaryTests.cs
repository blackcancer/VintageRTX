using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class EmissionSchemaBoundaryTests
{
    private static JsonObject Catalog() => JsonNode.Parse("""
        {"schemaVersion":1,"profiles":{"test:steady":{"kind":"steady"},"test:flame":{"kind":"flame","amplitude":0.1}},
         "bindings":{"test:rule":{"codes":["game:lamp-*"],"targets":["block"],"profile":"test:steady"}}}
        """)!.AsObject();
    private static void Reject(JsonObject json) => Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse(json.ToJsonString()));

    [TestMethod]
    public void RejectsMissingOversizedAndAmbiguousJsonInsteadOfKeepingPartialFields()
    {
        foreach (string json in new[] {"null", "[]", "{}", "{\"schemaVersion\":1}", "{\"schemaVersion\":1,\"profiles\":{}}", "{\"schemaVersion\":1,\"schemaVersion\":1}"})
            Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse(json));
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse(null!));
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse(new string(' ', 2 * 1024 * 1024 + 1)));
        foreach (string encoded in new[] {"null", "1.1", "2147483648", "\"1\""})
        {var json = Catalog(); json["schemaVersion"] = JsonNode.Parse(encoded); Reject(json);}
        var manyProfiles = Catalog(); var profiles = manyProfiles["profiles"]!.AsObject();
        for(int i = 0; i < 2049; i++) profiles["extra:p" + i] = new JsonObject {["kind"] = "steady"};
        Reject(manyProfiles);
        var manyBindings = Catalog(); var bindings = manyBindings["bindings"]!.AsObject();
        for(int i = 0; i < 8193; i++) bindings["extra:b" + i] = new JsonObject();
        Reject(manyBindings);
    }

    [TestMethod]
    public void EveryVariantOfMalformedBindingReportsItsAssetPath()
    {
        foreach (string code in new[] {new string('a', 256) + ":b", "missingdomain", ":x", "test:", "test:x:y", "test:UPPER", "test:x?"})
        {
            var json = Catalog(); json["bindings"]!["test:rule"]!["codes"] = new JsonArray(code); Reject(json);
            Assert.ThrowsException<EmissionConfigurationException>(() => EmissionOverride.Parse("{\"profile\":" + System.Text.Json.JsonSerializer.Serialize(code) + "}"));
        }
        foreach (string encoded in new[] {"null", "[]", "[\"game:x\",\"game:x\"]", "[1]", "[\"\"]", "[\" \" ]"})
        {var json = Catalog(); json["bindings"]!["test:rule"]!["codes"] = JsonNode.Parse(encoded); Reject(json);}
        var tooMany = Catalog(); tooMany["bindings"]!["test:rule"]!["codes"] = new JsonArray(Enumerable.Range(0,33).Select(n => (JsonNode?)JsonValue.Create("game:x" + n)).ToArray()); Reject(tooMany);
        foreach (string encoded in new[] {"null", "1.5", "2147483648", "-10001", "10001", "\"high\""})
        {var json = Catalog(); json["bindings"]!["test:rule"]!["priority"] = JsonNode.Parse(encoded); Reject(json);}
        foreach (string encoded in new[] {"1", "null", "\"false\""})
        {var json = Catalog(); json["bindings"]!["test:rule"]!["enabled"] = JsonNode.Parse(encoded); Reject(json);}
        var target = Catalog(); target["bindings"]!["test:rule"]!["targets"] = new JsonArray("invalid"); Reject(target);
        var overflow = Catalog(); overflow["profiles"]!["test:flame"]!["amplitude"] = JsonNode.Parse("1e999"); Reject(overflow);
        var catalog = EmissionCatalog.Parse(Catalog().ToJsonString());
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => catalog.Resolve("game:lamp-red", (EmissionTarget)99));
        Assert.ThrowsException<EmissionConfigurationException>(() => catalog.GetProfile("unknown:profile"));
    }

    [TestMethod]
    public void AttributeAndSelectionValidationNeverManufacturesEmission()
    {
        var catalog = EmissionCatalog.Parse(Catalog().ToJsonString());
        foreach(double invalid in new[] {double.NaN, -1, 101})
        {
            Assert.ThrowsException<EmissionConfigurationException>(() => catalog.Resolve("game:lamp-red", EmissionTarget.Block, new(IntensityScale: invalid)));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionSelection(null, null, EmissionProfile.Candle, IntensityScale: invalid).CreateLight(default, Vector3.One));
        }
        foreach(double invalid in new[] {double.NaN, -1, 17})
        {
            Assert.ThrowsException<EmissionConfigurationException>(() => catalog.Resolve("game:lamp-red", EmissionTarget.Block, new(SourceRadius: invalid)));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionSelection(null, null, EmissionProfile.Candle, SourceRadius: invalid).CreateLight(default, Vector3.One));
        }
        foreach(int invalid in new[] {-1, 65})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionSelection(null, null, EmissionProfile.Candle, ComponentCount: invalid).CreateLight(default, Vector3.One));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionSelection(null, null, EmissionProfile.Candle).CreateLight(default, new(float.NaN)));
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionOverride.Parse("{\"enabled\":1}"));
        Assert.IsFalse(EmissionOverride.Parse("{\"enabled\":false}").Enabled!.Value);
    }

    [TestMethod]
    public void EqualRankConflictsAreResolvedOnlyByTheCorrespondingExplicitOverride()
    {
        foreach(string field in new[] {"profile", "enabled", "intensityScale", "sourceRadius", "componentCounts"})
        {
            var json = Catalog(); var second = json["bindings"]!["test:rule"]!.DeepClone().AsObject();
            second[field] = field switch {
                "profile" => JsonValue.Create("test:flame"), "enabled" => JsonValue.Create(false),
                "intensityScale" => JsonValue.Create(2), "sourceRadius" => JsonValue.Create(.1),
                _ => new JsonObject {["game:lamp-*"] = 3}
            };
            json["bindings"]!["test:second"] = second;
            var catalog = EmissionCatalog.Parse(json.ToJsonString());
            Assert.ThrowsException<EmissionConfigurationException>(() => catalog.Resolve("game:lamp-red", EmissionTarget.Block));
            Assert.ThrowsException<EmissionConfigurationException>(() => catalog.Resolve("game:lamp-red", EmissionTarget.Block, new()));
            EmissionOverride attributes = field switch {
                "profile" => new(Profile: "test:steady"), "enabled" => new(Enabled: true),
                "intensityScale" => new(IntensityScale: 1), _ => new(SourceRadius: 0)
            };
            if(field == "componentCounts")
                Assert.ThrowsException<EmissionConfigurationException>(() => catalog.Resolve("game:lamp-red", EmissionTarget.Block, attributes));
            else Assert.IsNotNull(catalog.Resolve("game:lamp-red", EmissionTarget.Block, attributes).CreateLight(default, Vector3.One));
            second["priority"] = 1; var selected = EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:lamp-red", EmissionTarget.Block);
            Assert.AreEqual("test:second", selected.BindingId);
        }
    }

    [TestMethod]
    public void ComponentPatternsRespectSpecificityAndRejectOnlyGenuineEqualRankConflicts()
    {
        var json = Catalog();
        json["bindings"]!["test:rule"]!["codes"] = new JsonArray("game:lamp-*", "game:lamp-red", "game:*");
        var counts = new JsonObject { ["game:*"] = 1, ["game:lamp-*"] = 2, ["game:lamp-red"] = 3};
        json["bindings"]!["test:rule"]!["componentCounts"] = counts;
        Assert.AreEqual(3, EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:lamp-red", EmissionTarget.Block).ComponentCount);
        counts.Remove("game:lamp-red"); counts["game:*-red"] = 4;
        Assert.AreEqual(2, EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:lamp-red", EmissionTarget.Block).ComponentCount);
        counts["game:lamp-r**"] = 6; counts["game:lamp-*d*"] = 7;
        Assert.ThrowsException<EmissionConfigurationException>(() => EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:lamp-red", EmissionTarget.Block));
        counts["game:lamp-r**"] = 7;
        Assert.AreEqual(7, EmissionCatalog.Parse(json.ToJsonString()).Resolve("game:lamp-red", EmissionTarget.Block).ComponentCount);
        for(int i=0;i<129;i++)counts["extra:n"+i]=1;
        Reject(json);
    }
}
