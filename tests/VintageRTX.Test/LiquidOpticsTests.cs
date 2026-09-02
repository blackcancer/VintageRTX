using System.Reflection;
using System.Text.Json;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>
/// Contains deterministic regression checks for liquid Optics.
/// </summary>
internal static class LiquidOpticsTests
{
    private static readonly HashSet<string> ExpectedOpticalTargets = new(StringComparer.Ordinal)
    {
        "game:blocktypes/liquid/water.json",
        "game:blocktypes/liquid/rapidwater.json",
        "game:blocktypes/liquid/rivulet.json",
        "game:blocktypes/liquid/saltwater.json",
        "game:blocktypes/liquid/boilingwater.json",
        "game:blocktypes/liquid/lava.json",
        "game:itemtypes/liquid/waterportion.json",
        "game:itemtypes/liquid/saltwaterportion.json",
        "game:itemtypes/liquid/boilingwaterportion.json",
        "game:itemtypes/liquid/brineportion.json",
        "game:itemtypes/liquid/limewaterportion.json",
        "game:itemtypes/liquid/slakedlimeportion.json",
        "game:itemtypes/liquid/honeyportion.json",
        "game:itemtypes/liquid/jamhoneyportion.json",
        "game:itemtypes/liquid/oilportion.json",
        "game:itemtypes/liquid/dairy/milkportion.json",
        "game:itemtypes/liquid/dairy/curdledmilkportion.json",
        "game:itemtypes/liquid/dairy/curdportion.json",
        "game:itemtypes/liquid/dairy/wheycurdportion.json",
        "game:itemtypes/liquid/dairy/cottagecheeseportion.json",
        "game:itemtypes/liquid/alcohol.json",
        "game:itemtypes/liquid/spirit.json",
        "game:itemtypes/liquid/cider.json",
        "game:itemtypes/liquid/vinegarportion.json",
        "game:itemtypes/liquid/rawjuice.json",
        "game:itemtypes/liquid/acid.json",
        "game:itemtypes/liquid/dye.json",
        "game:itemtypes/liquid/glueportion.json",
        "game:itemtypes/liquid/tar.json",
        "game:itemtypes/liquid/weaktanninportion.json",
        "game:itemtypes/liquid/strongtanninportion.json",
        "game:itemtypes/liquid/dilutedalumportion.json",
        "game:itemtypes/liquid/dilutedboraxportion.json",
        "game:itemtypes/liquid/dilutedcassiteriteportion.json",
        "game:itemtypes/liquid/dilutedchromiteportion.json",
        "game:itemtypes/liquid/sulfate.json"
    };

    private static readonly HashSet<string> ExpectedContainerTargets = new(StringComparer.Ordinal)
    {
        "game:blocktypes/wood/bucket.json",
        "game:blocktypes/wood/barrel.json"
    };

    private static readonly (string Name, Action Test)[] Tests =
    [
            ("JSON patch coverage and runtime operation", TestPatchCoverageAndOperation),
            ("asset-authored optical ranges", TestOpticalRanges),
            ("distinct liquid physics", TestDistinctLiquidPhysics),
            ("lava-only bubble contract", TestLavaOnlyBubbles),
            ("opaque extinction contract", TestOpaqueExtinction),
            ("container visibility metadata", TestContainerMetadata),
            ("production GPU LUT packing", TestGpuLookupPacking),
            ("container surface height encoding", TestContainerSurfaceHeightEncoding)
    ];

    /// <summary>
    /// Gets the stable scenario sequence exposed to MSTest; ordering remains deterministic for reproducible diagnostics.
    /// </summary>
    internal static IEnumerable<string> TestNames => Tests.Select(static test => test.Name);

    /// <summary>
    /// Executes test as an isolated test step and propagates failures to the owning suite.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    internal static void RunTest(string name)
    {
        foreach ((string testName, Action test) in Tests)
        {
            if (string.Equals(testName, name, StringComparison.Ordinal))
            {
                test();
                return;
            }
        }

        throw new KeyNotFoundException($"Unknown liquid-optics test '{name}'.");
    }

    /// <summary>
    /// Executes requested fixture operation as an isolated test step and propagates failures to the owning suite.
    /// </summary>
    /// <returns>The run result consumed by the caller&apos;s assertion.</returns>
    public static int Run()
    {

        int failed = 0;
        foreach ((string name, Action test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS liquid optics: {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL liquid optics: {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"Liquid optics: {Tests.Length - failed}/{Tests.Length} passed.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Verifies the patch Coverage And Operation regression contract against deterministic fixture data.
    /// </summary>
    private static void TestPatchCoverageAndOperation()
    {
        using JsonDocument document = LoadPatchDocument();
        JsonElement[] entries = [.. document.RootElement.EnumerateArray()];
        Assert(entries.Length == 38, "the patch set must contain 38 exact entries");
        Assert(entries.All(static entry => entry.GetProperty("op").GetString() == "addMerge"),
            "all patches must use compatibility-preserving addMerge");
        Assert(entries.All(static entry => entry.GetProperty("path").GetString() == "/attributes"),
            "all patches must merge into the collectible attributes object");

        HashSet<string> optics = entries
            .Where(static entry => entry.GetProperty("value").TryGetProperty("vintageRtxOptics", out _))
            .Select(static entry => RequiredString(entry, "file"))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> containers = entries
            .Where(static entry => entry.GetProperty("value").TryGetProperty("vintageRtxLiquidContainer", out _))
            .Select(static entry => RequiredString(entry, "file"))
            .ToHashSet(StringComparer.Ordinal);
        Assert(optics.SetEquals(ExpectedOpticalTargets), "optical targets must match all 6 blocks and 30 liquid itemtypes");
        Assert(containers.SetEquals(ExpectedContainerTargets), "container targets must be exactly bucket and barrel");

        string gameRoot = TestPaths.ResolveGameRoot();
        string assetsRoot = Path.Combine(gameRoot, "assets");
        foreach (string target in ExpectedOpticalTargets.Concat(ExpectedContainerTargets))
        {
            string relative = target[(target.IndexOf(':') + 1)..]
                .Replace('/', Path.DirectorySeparatorChar);
            bool found = Directory.EnumerateDirectories(assetsRoot)
                .Any(contentRoot => File.Exists(Path.Combine(contentRoot, relative)));
            Assert(found, $"installed Vintage Story target is missing: {target}");
        }

        string essentialsPath = Path.Combine(gameRoot, "Mods", "VSEssentials.dll");
        Assembly essentials = Assembly.LoadFrom(essentialsPath);
        Type? patchOperation = essentials.GetType("Vintagestory.ServerMods.NoObf.EnumJsonPatchOp");
        Assert(patchOperation is not null && patchOperation.IsEnum,
            "installed VSEssentials must expose EnumJsonPatchOp");
        Assert(Enum.GetNames(patchOperation!).Contains("AddMerge", StringComparer.Ordinal),
            "installed Vintage Story patch loader must support AddMerge");
    }

    /// <summary>
    /// Verifies the optical Ranges regression contract against deterministic fixture data.
    /// </summary>
    private static void TestOpticalRanges()
    {
        using JsonDocument document = LoadPatchDocument();
        List<JsonElement> optics = OpticalProfiles(document).ToList();
        Assert(optics.Count == 36, "expected one optical asset contract per liquid block/item target");
        Assert(optics.Select(static profile => RequiredString(profile, "profile"))
            .Distinct(StringComparer.Ordinal).Count() == 33,
            "expected 33 distinct profiles after intentional water/saltwater/boiling-water sharing");

        foreach (JsonElement profile in optics)
        {
            string name = RequiredString(profile, "profile");
            ValidateScalar(profile, "ior", 1.0f, 3.0f, name);
            ValidateRgb(profile, "absorptionRgb", 0.0f, 64.0f, name);
            ValidateRgb(profile, "scatteringRgb", 0.0f, 64.0f, name);
            ValidateScalar(profile, "scatteringAnisotropy", -0.95f, 0.95f, name);
            ValidateScalar(profile, "transmission", 0.0f, 1.0f, name);
            ValidateRgb(profile, "emissionRgb", 0.0f, 64.0f, name);
            ValidateScalar(profile, "emissionIntensity", 0.0f, 64.0f, name);
            ValidateScalar(profile, "roughness", 0.0f, 1.0f, name);
            ValidateScalar(profile, "microNormalStrength", 0.0f, 4.0f, name);
            ValidateScalar(profile, "opticalViscosity", 0.0f, 1.0f, name);
            Assert(profile.TryGetProperty("opaque", out JsonElement opaque)
                && opaque.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"{name}.opaque must be a boolean");

            JsonElement dynamics = profile.GetProperty("surfaceDynamics");
            ValidateScalar(dynamics, "windCoupling", 0.0f, 4.0f, name);
            ValidateScalar(dynamics, "waveAmplitude", 0.0f, 4.0f, name);
            ValidateScalar(dynamics, "waveLength", 0.01f, 256.0f, name);
            ValidateScalar(dynamics, "waveSpeed", 0.0f, 32.0f, name);
            ValidateScalar(dynamics, "damping", 0.0f, 1.0f, name);
            ValidateScalar(dynamics, "impactResponse", 0.0f, 4.0f, name);
            ValidateScalar(dynamics, "surfaceTension", 0.0f, 4.0f, name);
            ValidateScalar(dynamics, "bubbleRate", 0.0f, 64.0f, name);
            ValidateScalar(dynamics, "bubbleRadiusMin", 0.0f, 4.0f, name);
            ValidateScalar(dynamics, "bubbleRadiusMax", 0.0f, 4.0f, name);
            ValidateScalar(dynamics, "bubbleRiseDuration", 0.0f, 64.0f, name);
            ValidateScalar(dynamics, "bubbleBurstStrength", 0.0f, 16.0f, name);
            ValidateScalar(dynamics, "bubbleEmissionBoost", 0.0f, 64.0f, name);
            Assert(dynamics.GetProperty("bubbleRadiusMax").GetSingle()
                >= dynamics.GetProperty("bubbleRadiusMin").GetSingle(),
                $"{name} bubble radius maximum must not be below its minimum");
        }
    }

    /// <summary>
    /// Verifies the distinct Liquid Physics regression contract against deterministic fixture data.
    /// </summary>
    private static void TestDistinctLiquidPhysics()
    {
        using JsonDocument document = LoadPatchDocument();
        JsonElement water = FindProfile(document, "water");
        JsonElement honey = FindProfile(document, "honey");
        JsonElement lava = FindProfile(document, "lava");
        JsonElement milk = FindProfile(document, "milk");
        JsonElement oil = FindProfile(document, "plant-and-rendered-oil");

        Assert(water.GetProperty("ior").GetSingle() != honey.GetProperty("ior").GetSingle(),
            "water and honey must not share an IOR");
        Assert(honey.GetProperty("surfaceDynamics").GetProperty("damping").GetSingle()
            > water.GetProperty("surfaceDynamics").GetProperty("damping").GetSingle(),
            "honey must damp waves more strongly than water");
        Assert(lava.GetProperty("surfaceDynamics").GetProperty("waveSpeed").GetSingle()
            < water.GetProperty("surfaceDynamics").GetProperty("waveSpeed").GetSingle(),
            "lava must move more heavily than water");
        Assert(milk.GetProperty("transmission").GetSingle() == 0.0f,
            "milk must be optically opaque");
        Assert(oil.GetProperty("roughness").GetSingle() != water.GetProperty("roughness").GetSingle(),
            "oil and water must retain distinct micro-surface behavior");
    }

    /// <summary>
    /// Verifies the lava Only Bubbles regression contract against deterministic fixture data.
    /// </summary>
    private static void TestLavaOnlyBubbles()
    {
        using JsonDocument document = LoadPatchDocument();
        foreach (JsonElement profile in OpticalProfiles(document))
        {
            string name = RequiredString(profile, "profile");
            JsonElement dynamics = profile.GetProperty("surfaceDynamics");
            float[] bubbleValues =
            [
                dynamics.GetProperty("bubbleRate").GetSingle(),
                dynamics.GetProperty("bubbleRadiusMin").GetSingle(),
                dynamics.GetProperty("bubbleRadiusMax").GetSingle(),
                dynamics.GetProperty("bubbleRiseDuration").GetSingle(),
                dynamics.GetProperty("bubbleBurstStrength").GetSingle(),
                dynamics.GetProperty("bubbleEmissionBoost").GetSingle()
            ];
            if (name == "lava")
            {
                Assert(bubbleValues.All(static value => value > 0.0f),
                    "lava must author all bubble and burst controls");
                Assert(bubbleValues[0] >= 0.01f && bubbleValues[0] <= 0.05f,
                    "lava bubble rate must stay subtle at roughly 1-5 events/s per 100 surface cells");
            }
            else
            {
                Assert(bubbleValues.All(static value => value == 0.0f),
                    $"non-lava profile {name} must default all bubble controls to zero");
            }
        }
    }

    /// <summary>
    /// Verifies the opaque Extinction regression contract against deterministic fixture data.
    /// </summary>
    private static void TestOpaqueExtinction()
    {
        using JsonDocument document = LoadPatchDocument();
        foreach (JsonElement profile in OpticalProfiles(document)
            .Where(static profile => profile.GetProperty("opaque").GetBoolean()))
        {
            string name = RequiredString(profile, "profile");
            Assert(profile.GetProperty("transmission").GetSingle() == 0.0f,
                $"opaque profile {name} must set transmission to zero");
            float extinction = profile.GetProperty("absorptionRgb").EnumerateArray()
                .Concat(profile.GetProperty("scatteringRgb").EnumerateArray())
                .Max(static component => component.GetSingle());
            Assert(extinction >= 1.0f,
                $"opaque profile {name} must author strong inverse-block extinction");
        }

        JsonElement lava = FindProfile(document, "lava");
        Assert(lava.GetProperty("emissionIntensity").GetSingle() > 0.0f,
            "lava must be emissive");
        Assert(lava.GetProperty("emissionRgb").EnumerateArray().Any(static value => value.GetSingle() > 0.0f),
            "lava must provide a non-black linear emission color");
    }

    /// <summary>
    /// Verifies the container Metadata regression contract against deterministic fixture data.
    /// </summary>
    private static void TestContainerMetadata()
    {
        using JsonDocument document = LoadPatchDocument();
        Dictionary<string, JsonElement> containers = document.RootElement.EnumerateArray()
            .Where(static entry => entry.GetProperty("value").TryGetProperty("vintageRtxLiquidContainer", out _))
            .ToDictionary(
                static entry => RequiredString(entry, "file"),
                static entry => entry.GetProperty("value").GetProperty("vintageRtxLiquidContainer"),
                StringComparer.Ordinal);

        JsonElement bucket = containers["game:blocktypes/wood/bucket.json"];
        Assert(bucket.GetProperty("contentSlot").GetInt32() == 0, "bucket liquid slot must be 0");
        Assert(bucket.GetProperty("capacityLitres").GetSingle() == 10.0f, "bucket capacity must be 10 litres");
        Assert(bucket.GetProperty("alwaysOpen").GetBoolean(), "bucket liquid surface must be explicitly open");

        JsonElement barrel = containers["game:blocktypes/wood/barrel.json"];
        Assert(barrel.GetProperty("contentSlot").GetInt32() == 1, "barrel liquid slot must be 1");
        Assert(barrel.GetProperty("capacityLitres").GetSingle() == 50.0f, "barrel capacity must be 50 litres");
        Assert(!barrel.GetProperty("alwaysOpen").GetBoolean(), "barrel must respect its lid/sealed state");
        Assert(RequiredString(barrel, "visibilityTreeBool") == "sealed", "barrel visibility must read the serialized sealed flag");
        Assert(!barrel.GetProperty("visibleWhen").GetBoolean(), "barrel liquid surface is visible only while unsealed");
    }

    /// <summary>
    /// Verifies the gpu Lookup Packing regression contract against deterministic fixture data.
    /// </summary>
    private static void TestGpuLookupPacking()
    {
        LiquidOpticalProfile profile = new(
            "fixture",
            1.42f,
            new LiquidRgb(0.1f, 0.2f, 0.3f),
            new LiquidRgb(0.4f, 0.5f, 0.6f),
            0.7f,
            new LiquidRgb(0.8f, 0.9f, 1.0f),
            1.1f,
            0.12f,
            0.13f,
            0.14f,
            0.15f,
            new LiquidSurfaceDynamics(
                0.16f, 0.17f, 1.8f, 1.9f, 0.20f, 0.21f, 1.22f,
                0.23f, 0.24f, 0.25f, 2.6f, 0.27f, 0.28f),
            true,
            new LiquidPhysicalProperties(
                "fixture-basis",
                1473.15f,
                2800.0f,
                10.0f,
                0.4f,
                0.005f,
                1473.15f,
                0.8f));
        float[] lookup = LiquidOpticalRegistry.BuildGpuLookupForProfiles(
            new Dictionary<byte, LiquidOpticalProfile> { [7] = profile });
        Assert(
            lookup.Length == LiquidOpticalRegistry.LookupWidth
                * LiquidOpticalRegistry.LookupHeight
                * LiquidOpticalRegistry.LookupChannels,
            "LUT must preserve the declared RGBA32F dimensions");
        AssertTexel(lookup, 0, 0, 1.0f, 1.0f, 1.0f, 0.0f);
        AssertTexel(lookup, byte.MaxValue, 0, 1.0f, 1.0f, 1.0f, 0.0f);
        AssertTexel(lookup, 7, 0, 1.42f, 0.7f, 0.12f, 0.13f);
        AssertTexel(lookup, 7, 1, 0.1f, 0.2f, 0.3f, 1.0f);
        AssertTexel(lookup, 7, 2, 0.4f, 0.5f, 0.6f, 1.1f);
        AssertTexel(lookup, 7, 3, 0.8f, 0.9f, 1.0f, 0.15f);
        AssertTexel(lookup, 7, 4, 0.16f, 0.17f, 1.8f, 1.9f);
        AssertTexel(lookup, 7, 5, 0.20f, 0.21f, 1.22f, 0.14f);
        AssertTexel(lookup, 7, 6, 0.23f, 0.24f, 0.25f, 2.6f);
        AssertTexel(lookup, 7, 7, 0.27f, 0.28f, 0.0f, 0.0f);
        AssertTexel(lookup, 7, 8, 2800.0f, 10.0f, 0.4f, 0.005f);
        AssertTexel(lookup, 7, 9, 1473.15f, 0.8f, 1.0f, 0.0f);

        bool rejectedReservedRow = false;
        try
        {
            _ = LiquidOpticalRegistry.BuildGpuLookupForProfiles(
                new Dictionary<byte, LiquidOpticalProfile> { [0] = profile });
        }
        catch (ArgumentOutOfRangeException)
        {
            rejectedReservedRow = true;
        }
        Assert(rejectedReservedRow, "fixture LUT builder must preserve neutral reserved rows");
    }

    /// <summary>
    /// Verifies the container Surface Height Encoding regression contract against deterministic fixture data.
    /// </summary>
    private static void TestContainerSurfaceHeightEncoding()
    {
        LiquidContainerOptics container = new(0, 10.0f, 0.2f, 0.8f, true, string.Empty, false);
        Assert(container.EncodeSurfaceHeight(-1.0f) == 51, "negative fill must clamp to the minimum surface height");
        Assert(container.EncodeSurfaceHeight(0.5f) == 128, "half fill must encode the interpolated surface height");
        Assert(container.EncodeSurfaceHeight(2.0f) == 204, "overfill must clamp to the maximum surface height");
        Assert((byte)VoxelLiquidFlags.FluidLayer == 1
            && (byte)VoxelLiquidFlags.Contained == 2
            && (byte)VoxelLiquidFlags.VisibleSurface == 4,
            "GPU liquid flag bit assignments must remain stable");
    }

    /// <summary>
    /// Executes the load Patch Document step used by the deterministic liquid Optics Tests fixture.
    /// </summary>
    /// <returns>The load Patch Document result consumed by the caller&apos;s assertion.</returns>
    private static JsonDocument LoadPatchDocument()
    {
        string path = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "assets", "vintagertx", "patches",
            "liquid-optical-profiles.json");
        JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert(document.RootElement.ValueKind == JsonValueKind.Array,
            "liquid optical patch root must be an array");
        return document;
    }

    /// <summary>
    /// Executes the optical Profiles step used by the deterministic liquid Optics Tests fixture.
    /// </summary>
    /// <param name="document">The document input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IEnumerable<JsonElement> OpticalProfiles(JsonDocument document)
    {
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("value").TryGetProperty("vintageRtxOptics", out JsonElement optics))
            {
                yield return optics;
            }
        }
    }

    /// <summary>
    /// Executes the find Profile step used by the deterministic liquid Optics Tests fixture.
    /// </summary>
    /// <param name="document">The document input used to configure this deterministic test path.</param>
    /// <param name="profileName">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The find Profile result consumed by the caller&apos;s assertion.</returns>
    private static JsonElement FindProfile(JsonDocument document, string profileName)
    {
        return OpticalProfiles(document).First(profile =>
            string.Equals(RequiredString(profile, "profile"), profileName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Executes the required String step used by the deterministic liquid Optics Tests fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <param name="propertyName">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The required String result consumed by the caller&apos;s assertion.</returns>
    private static string RequiredString(JsonElement value, string propertyName)
    {
        Assert(value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()),
            $"{propertyName} must be a non-empty string");
        return property.GetString()!;
    }

    /// <summary>
    /// Validates scalar and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="parent">The parent input used to configure this deterministic test path.</param>
    /// <param name="propertyName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <param name="profileName">Stable identifier selecting the deterministic fixture case.</param>
    private static void ValidateScalar(
        JsonElement parent,
        string propertyName,
        float minimum,
        float maximum,
        string profileName)
    {
        Assert(parent.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetSingle(out float value)
            && float.IsFinite(value)
            && value >= minimum
            && value <= maximum,
            $"{profileName}.{propertyName} must be finite and in [{minimum}, {maximum}]");
    }

    /// <summary>
    /// Validates rgb and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="parent">The parent input used to configure this deterministic test path.</param>
    /// <param name="propertyName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <param name="profileName">Stable identifier selecting the deterministic fixture case.</param>
    private static void ValidateRgb(
        JsonElement parent,
        string propertyName,
        float minimum,
        float maximum,
        string profileName)
    {
        Assert(parent.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Array,
            $"{profileName}.{propertyName} must be an RGB array");
        JsonElement[] components = [.. property.EnumerateArray()];
        Assert(components.Length == 3, $"{profileName}.{propertyName} must contain exactly 3 components");
        Assert(components.All(component => component.ValueKind == JsonValueKind.Number
            && component.TryGetSingle(out float value)
            && float.IsFinite(value)
            && value >= minimum
            && value <= maximum),
            $"{profileName}.{propertyName} components must be finite and in [{minimum}, {maximum}]");
    }

    /// <summary>
    /// Asserts texel and throws when the regression contract is violated.
    /// </summary>
    /// <param name="lookup">The lookup input used to configure this deterministic test path.</param>
    /// <param name="profileId">The profile Id input used to configure this deterministic test path.</param>
    /// <param name="texel">The texel input used to configure this deterministic test path.</param>
    /// <param name="red">The red input used to configure this deterministic test path.</param>
    /// <param name="green">The green input used to configure this deterministic test path.</param>
    /// <param name="blue">The blue input used to configure this deterministic test path.</param>
    /// <param name="alpha">The alpha input used to configure this deterministic test path.</param>
    private static void AssertTexel(
        float[] lookup,
        int profileId,
        int texel,
        float red,
        float green,
        float blue,
        float alpha)
    {
        int offset = (profileId * LiquidOpticalRegistry.LookupWidth + texel)
            * LiquidOpticalRegistry.LookupChannels;
        Assert(lookup[offset] == red
            && lookup[offset + 1] == green
            && lookup[offset + 2] == blue
            && lookup[offset + 3] == alpha,
            $"unexpected LUT packing at profile {profileId}, texel {texel}");
    }

    /// <summary>
    /// Asserts requested fixture operation and throws when the regression contract is violated.
    /// </summary>
    /// <param name="condition">The condition input used to configure this deterministic test path.</param>
    /// <param name="message">The message input used to configure this deterministic test path.</param>
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
