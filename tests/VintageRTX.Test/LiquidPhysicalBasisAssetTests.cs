using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Test;

/// <summary>Guards scientific provenance, SI units, and complete profile assignment for liquid assets.</summary>
[TestClass]
public sealed class LiquidPhysicalBasisAssetTests
{
    /// <summary>Checks every authored liquid profile resolves to an explicitly sourced physical family.</summary>
    [TestMethod]
    public void EveryLiquidProfileHasASourcedPhysicalBasis()
    {
        using JsonDocument basisDocument = LoadJson(
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "physics",
            "liquid-physical-basis.json");
        using JsonDocument patchDocument = LoadJson(
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "patches",
            "liquid-optical-profiles.json");
        using JsonDocument physicalPatchDocument = LoadJson(
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "patches",
            "liquid-physical-properties.json");

        JsonElement root = basisDocument.RootElement;
        JsonElement sources = root.GetProperty("sources");
        JsonElement families = root.GetProperty("basisFamilies");
        JsonElement assignments = root.GetProperty("profileAssignments");
        Assert.AreEqual(1.0, root.GetProperty("worldScale").GetProperty("metresPerBlock").GetDouble());
        Assert.AreEqual(
            VintageRTX.Rendering.LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit,
            root.GetProperty("engineInputCalibration")
                .GetProperty("windMetresPerSecondPerEngineUnit")
                .GetDouble());
        Assert.AreEqual(
            "m^-1",
            root.GetProperty("spectralApproximation").GetProperty("extinctionUnit").GetString());

        HashSet<string> authoredProfiles = [];
        Dictionary<string, string> profileByTarget = new(StringComparer.Ordinal);
        foreach (JsonElement operation in patchDocument.RootElement.EnumerateArray())
        {
            if (!operation.GetProperty("value").TryGetProperty("vintageRtxOptics", out JsonElement optics)
                || !optics.TryGetProperty("profile", out JsonElement profileElement))
            {
                continue;
            }

            string profile = profileElement.GetString() ?? string.Empty;
            if (profile.Length > 0)
            {
                authoredProfiles.Add(profile);
                profileByTarget.Add(operation.GetProperty("file").GetString() ?? string.Empty, profile);
            }
        }

        Assert.IsTrue(authoredProfiles.Count >= 30);
        foreach (string profile in authoredProfiles)
        {
            Assert.IsTrue(assignments.TryGetProperty(profile, out JsonElement assignment), profile);
            string familyName = assignment.GetProperty("basis").GetString() ?? string.Empty;
            Assert.IsTrue(families.TryGetProperty(familyName, out _), $"{profile}: {familyName}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(assignment.GetProperty("derivation").GetString()));
        }

        HashSet<string> physicalTargets = [];
        foreach (JsonElement operation in physicalPatchDocument.RootElement.EnumerateArray())
        {
            string target = operation.GetProperty("file").GetString() ?? string.Empty;
            JsonElement physics = operation.GetProperty("value").GetProperty("vintageRtxPhysics");
            Assert.IsTrue(profileByTarget.TryGetValue(target, out string? profile), target);
            string basis = physics.GetProperty("basis").GetString() ?? string.Empty;
            Assert.AreEqual(assignments.GetProperty(profile).GetProperty("basis").GetString(), basis, target);
            Assert.IsTrue(physics.GetProperty("referenceTemperatureK").GetDouble() > 0.0, target);
            Assert.IsTrue(physics.GetProperty("densityKgM3").GetDouble() > 0.0, target);
            Assert.IsTrue(physics.GetProperty("dynamicViscosityPaS").GetDouble() > 0.0, target);
            Assert.IsTrue(physics.GetProperty("surfaceTensionNm").GetDouble() > 0.0, target);
            Assert.IsTrue(physics.GetProperty("resolvedWaveEnergyFraction").GetDouble() is >= 0.0 and <= 1.0, target);
            Assert.IsTrue(physics.GetProperty("thermalTemperatureK").GetDouble() >= 0.0, target);
            Assert.IsTrue(physics.GetProperty("thermalEmissivity").GetDouble() is >= 0.0 and <= 1.0, target);
            physicalTargets.Add(target);
        }

        CollectionAssert.AreEquivalent(profileByTarget.Keys.ToArray(), physicalTargets.ToArray());

        foreach (JsonProperty familyProperty in families.EnumerateObject())
        {
            JsonElement family = familyProperty.Value;
            Assert.IsTrue(family.GetProperty("referenceTemperatureK").GetDouble() > 0.0);
            Assert.IsTrue(family.GetProperty("densityKgM3").GetDouble() > 0.0);
            Assert.IsTrue(family.GetProperty("dynamicViscosityPaS").GetDouble() >= 0.0);
            Assert.IsTrue(family.GetProperty("surfaceTensionNm").GetDouble() >= 0.0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(family.GetProperty("confidence").GetString()));

            JsonElement sourceIds = family.GetProperty("sourceIds");
            Assert.IsTrue(sourceIds.GetArrayLength() > 0);
            foreach (JsonElement sourceIdElement in sourceIds.EnumerateArray())
            {
                string sourceId = sourceIdElement.GetString() ?? string.Empty;
                Assert.IsTrue(sources.TryGetProperty(sourceId, out JsonElement source), sourceId);
                string url = source.GetProperty("url").GetString() ?? string.Empty;
                Assert.IsTrue(
                    Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                    && parsed.Scheme == Uri.UriSchemeHttps,
                    sourceId);
            }
        }
    }

    /// <summary>Checks representative measured data preserve physically meaningful ordering and units.</summary>
    [TestMethod]
    public void ReferenceFamiliesRetainMeasuredScaleAndSpectralOrdering()
    {
        using JsonDocument document = LoadJson(
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "physics",
            "liquid-physical-basis.json");
        JsonElement families = document.RootElement.GetProperty("basisFamilies");
        JsonElement water = families.GetProperty("pure-water-293k");
        JsonElement honey = families.GetProperty("natural-honey-303k");
        JsonElement lava = families.GetProperty("basaltic-melt-1473k");
        double[] waterAbsorption = water.GetProperty("absorptionRgbM1")
            .EnumerateArray()
            .Select(static component => component.GetDouble())
            .ToArray();

        Assert.IsTrue(waterAbsorption[0] > waterAbsorption[1]);
        Assert.IsTrue(waterAbsorption[1] > waterAbsorption[2]);
        Assert.IsTrue(honey.GetProperty("densityKgM3").GetDouble() > water.GetProperty("densityKgM3").GetDouble());
        Assert.IsTrue(honey.GetProperty("dynamicViscosityPaS").GetDouble() > 1.0);
        Assert.IsTrue(lava.GetProperty("referenceTemperatureK").GetDouble() > 1400.0);
        Assert.IsTrue(lava.GetProperty("thermalEmissivity").GetDouble() is > 0.0 and <= 1.0);
    }

    /// <summary>Locks direct water assets to the sourced 650/550/450 nm coefficients used by the shader.</summary>
    [TestMethod]
    public void DirectWaterTargetsUseMeasuredSpectralCoefficients()
    {
        using JsonDocument basisDocument = LoadJson(
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "physics",
            "liquid-physical-basis.json");
        using JsonDocument correctionDocument = LoadJson(
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "patches",
            "liquid-zz-measured-water-optics.json");

        JsonElement water = basisDocument.RootElement
            .GetProperty("basisFamilies")
            .GetProperty("pure-water-293k");
        double[] expectedAbsorption = water.GetProperty("absorptionRgbM1")
            .EnumerateArray()
            .Select(static value => value.GetDouble())
            .ToArray();
        double[] expectedScattering = water.GetProperty("outScatteringRgbM1")
            .EnumerateArray()
            .Select(static value => value.GetDouble())
            .ToArray();
        HashSet<string> expectedTargets =
        [
            "game:blocktypes/liquid/water.json",
            "game:blocktypes/liquid/rapidwater.json",
            "game:blocktypes/liquid/rivulet.json",
            "game:itemtypes/liquid/waterportion.json"
        ];

        foreach (JsonElement operation in correctionDocument.RootElement.EnumerateArray())
        {
            Assert.AreEqual("addMerge", operation.GetProperty("op").GetString());
            Assert.AreEqual("/attributes/vintageRtxOptics", operation.GetProperty("path").GetString());
            Assert.IsTrue(expectedTargets.Remove(operation.GetProperty("file").GetString() ?? string.Empty));
            JsonElement optics = operation.GetProperty("value");
            CollectionAssert.AreEqual(
                expectedAbsorption,
                optics.GetProperty("absorptionRgb").EnumerateArray()
                    .Select(static value => value.GetDouble()).ToArray());
            CollectionAssert.AreEqual(
                expectedScattering,
                optics.GetProperty("scatteringRgb").EnumerateArray()
                    .Select(static value => value.GetDouble()).ToArray());
        }

        Assert.AreEqual(0, expectedTargets.Count);
        CollectionAssert.Contains(
            water.GetProperty("sourceIds").EnumerateArray()
                .Select(static value => value.GetString()).ToArray(),
            "nasa-ocean-optics-pure-water-2003");
    }

    /// <summary>Proves the client bridge reconstructs every server patch profile without duplicating coefficients.</summary>
    [TestMethod]
    public void ClientBindingsReconstructServerPatchProfiles()
    {
        string assetRoot = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "assets",
            "vintagertx");
        VintageRTX.Rendering.LiquidClientFallbackData fallback =
            VintageRTX.Rendering.LiquidOpticalFallbackCatalog.Parse(
                File.ReadAllBytes(Path.Combine(assetRoot, "patches", "liquid-optical-profiles.json")),
                File.ReadAllBytes(Path.Combine(assetRoot, "patches", "liquid-physical-properties.json")),
                File.ReadAllBytes(Path.Combine(assetRoot, "patches", "liquid-zz-measured-water-optics.json")),
                File.ReadAllBytes(Path.Combine(assetRoot, "config", "liquid-client-bindings.json")));

        IReadOnlyDictionary<string, VintageRTX.Rendering.LiquidOpticalProfile> profiles =
            fallback.ProfilesByCodeRoot;
        Assert.AreEqual(36, profiles.Count);
        Assert.AreEqual(2, fallback.ContainersByCodeRoot.Count);
        Assert.IsTrue(VintageRTX.Rendering.LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:water-still-7"),
            profiles,
            out VintageRTX.Rendering.LiquidOpticalProfile water));
        Assert.AreEqual("water", water.ProfileKey);
        Assert.AreEqual(1.333f, water.IndexOfRefraction, 0.0001f);
        Assert.AreEqual(0.3594f, water.Absorption.Red, 0.000001f);
        Assert.AreEqual(0.001002f, water.PhysicalProperties.DynamicViscosityPascalSeconds, 0.0000001f);

        Assert.IsTrue(VintageRTX.Rendering.LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:honeyportion"),
            profiles,
            out VintageRTX.Rendering.LiquidOpticalProfile honey));
        Assert.AreEqual(1.481f, honey.IndexOfRefraction, 0.0001f);
        Assert.AreEqual(1496.0f, honey.PhysicalProperties.DensityKilogramsPerCubicMetre, 0.01f);

        Assert.IsTrue(VintageRTX.Rendering.LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:lava-still-7"),
            profiles,
            out VintageRTX.Rendering.LiquidOpticalProfile lava));
        Assert.AreEqual(1473.15f, lava.PhysicalProperties.ThermalTemperatureKelvins, 0.01f);
        Assert.IsTrue(VintageRTX.Rendering.LiquidOpticalFallbackCatalog.TryResolveContainer(
            new Vintagestory.API.Common.AssetLocation("game:woodbucket"),
            fallback.ContainersByCodeRoot,
            out VintageRTX.Rendering.LiquidContainerOptics bucket));
        Assert.AreEqual(10.0f, bucket.CapacityLitres, 0.001f);
        Assert.IsTrue(bucket.AlwaysOpen);
        Assert.IsFalse(VintageRTX.Rendering.LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:granite"),
            profiles,
            out _));
        Assert.IsFalse(VintageRTX.Rendering.LiquidOpticalFallbackCatalog.TryResolve(
            null,
            profiles,
            out _));
        Assert.IsFalse(VintageRTX.Rendering.LiquidOpticalFallbackCatalog.TryResolveContainer(
            null,
            fallback.ContainersByCodeRoot,
            out _));
    }

    /// <summary>Loads one repository JSON asset while retaining the document for deterministic disposal.</summary>
    /// <param name="segments">Path components below the repository root.</param>
    /// <returns>Parsed JSON document.</returns>
    private static JsonDocument LoadJson(params string[] segments)
    {
        string path = segments.Aggregate(
            TestPaths.FindRepositoryRoot(),
            static (current, segment) => Path.Combine(current, segment));
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }
}
