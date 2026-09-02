using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using VintageRTX.Configuration;

namespace VintageRTX.Test;

/// <summary>Guards persistent hardware profiles and their chat-command aliases.</summary>
[TestClass]
public sealed class RenderProfileTests
{
    /// <summary>Verifies every authored profile keeps the complete renderer and scales cost monotonically.</summary>
    [TestMethod]
    public void AuthoredProfilesRetainCoreFeaturesAndScaleSpatialWork()
    {
        (VintageRtxRenderProfile Profile, float Budget, float ReflectionDistance,
            float BounceDistance, int BounceRays, int PointSamples, float SunDistance,
            float RayDistance, int Rays, int Steps, int Floor, bool Adaptive)[] expected =
        [
            (VintageRtxRenderProfile.Performance, 4.50f, 8.0f, 4.0f, 1, 2, 64.0f, 1.5f, 1, 4, 2, true),
            (VintageRtxRenderProfile.Balanced, 6.00f, 10.0f, 6.0f, 1, 3, 64.0f, 2.4f, 2, 6, 1, true),
            (VintageRtxRenderProfile.Quality, 8.50f, 16.0f, 8.0f, 2, 4, 80.0f, 3.2f, 3, 9, 0, true),
            (VintageRtxRenderProfile.Ultra, 12.0f, 24.0f, 12.0f, 2, 4, 96.0f, 4.5f, 4, 12, 0, false),
            (VintageRtxRenderProfile.Extreme, 20.0f, 32.0f, 16.0f, 3, 6, 96.0f, 6.0f, 6, 18, 0, false),
            (VintageRtxRenderProfile.Cinematic, 40.0f, 48.0f, 24.0f, 4, 8, 96.0f, 8.0f, 8, 24, 0, false)
        ];

        VintageRtxConfig[] configurations = expected.Select(static item =>
        {
            VintageRtxConfig config = new()
            {
                ScreenSpaceLightingEnabled = false,
                ScreenSpaceReflectionsEnabled = false,
                VoxelReflectionsEnabled = false,
                VoxelLightingEnabled = false,
                TemporalAccumulationEnabled = false,
                SunShadowsEnabled = false
            };
            config.ApplyRenderProfile(item.Profile);
            return config;
        }).ToArray();

        for (int index = 0; index < expected.Length; index++)
        {
            VintageRtxConfig config = configurations[index];
            var authored = expected[index];
            Assert.AreEqual(authored.Profile, config.RenderProfile);
            Assert.AreEqual(authored.Budget, config.GpuBudgetMilliseconds);
            Assert.AreEqual(authored.ReflectionDistance, config.ReflectionDistance);
            Assert.AreEqual(authored.BounceDistance, config.VoxelBounceDistance);
            Assert.AreEqual(authored.BounceRays, config.VoxelBounceRayCount);
            Assert.AreEqual(authored.PointSamples, config.PointLightShadowSamples);
            Assert.AreEqual(authored.SunDistance, config.SunShadowDistance);
            Assert.AreEqual(authored.RayDistance, config.RayDistance);
            Assert.AreEqual(authored.Rays, config.RayCount);
            Assert.AreEqual(authored.Steps, config.RaySteps);
            Assert.AreEqual(authored.Floor, config.AdaptiveQualityFloor);
            Assert.AreEqual(authored.Adaptive, config.AdaptiveQualityEnabled);
            Assert.IsTrue(config.ScreenSpaceLightingEnabled);
            Assert.IsTrue(config.ScreenSpaceReflectionsEnabled);
            Assert.IsTrue(config.VoxelReflectionsEnabled);
            Assert.IsTrue(config.VoxelLightingEnabled);
            Assert.IsTrue(config.TemporalAccumulationEnabled);
            Assert.IsTrue(config.SunShadowsEnabled);
        }

        for (int index = 1; index < configurations.Length; index++)
        {
            Assert.IsTrue(configurations[index].GpuBudgetMilliseconds
                > configurations[index - 1].GpuBudgetMilliseconds);
            Assert.IsTrue(configurations[index].ReflectionDistance
                > configurations[index - 1].ReflectionDistance);
            Assert.IsTrue(configurations[index].VoxelBounceDistance
                > configurations[index - 1].VoxelBounceDistance);
            Assert.IsTrue(configurations[index].RayDistance
                > configurations[index - 1].RayDistance);
            Assert.IsTrue(configurations[index].RayCount
                > configurations[index - 1].RayCount);
            Assert.IsTrue(configurations[index].RaySteps
                > configurations[index - 1].RaySteps);
        }
    }

    /// <summary>Verifies Custom preserves manually authored values while still normalizing their ranges.</summary>
    [TestMethod]
    public void CustomProfilePreservesManualControls()
    {
        VintageRtxConfig config = new()
        {
            RenderProfile = VintageRtxRenderProfile.Quality,
            AdaptiveQualityEnabled = false,
            GpuBudgetMilliseconds = 7.25f,
            ScreenSpaceLightingEnabled = false,
            ReflectionDistance = 13.0f,
            RayCount = 2,
            RaySteps = 7
        };

        config.ApplyRenderProfile(VintageRtxRenderProfile.Custom);

        Assert.AreEqual(VintageRtxRenderProfile.Custom, config.RenderProfile);
        Assert.IsFalse(config.AdaptiveQualityEnabled);
        Assert.AreEqual(7.25f, config.GpuBudgetMilliseconds);
        Assert.IsFalse(config.ScreenSpaceLightingEnabled);
        Assert.AreEqual(13.0f, config.ReflectionDistance);
        Assert.AreEqual(2, config.RayCount);
        Assert.AreEqual(7, config.RaySteps);
        Assert.AreEqual(0, config.AdaptiveQualityFloor);
    }

    /// <summary>Verifies malformed persisted values recover safely and cannot be applied explicitly.</summary>
    [TestMethod]
    public void InvalidProfileRecoversToCustomAndExplicitApplicationThrows()
    {
        VintageRtxConfig config = new()
        {
            RenderProfile = (VintageRtxRenderProfile)int.MaxValue
        };

        config.Clamp();
        Assert.AreEqual(VintageRtxRenderProfile.Custom, config.RenderProfile);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            config.ApplyRenderProfile((VintageRtxRenderProfile)(-1)));
    }

    /// <summary>Verifies all documented English and French command aliases and rejection behavior.</summary>
    [TestMethod]
    public void ProfileParserCoversDocumentedAliases()
    {
        Dictionary<VintageRtxRenderProfile, string[]> aliases = new()
        {
            [VintageRtxRenderProfile.Performance] = ["performance", "low", "faible"],
            [VintageRtxRenderProfile.Balanced] = ["balanced", "medium", "equilibre", "équilibré"],
            [VintageRtxRenderProfile.Quality] = ["quality", "high", "qualite", "qualité"],
            [VintageRtxRenderProfile.Ultra] = ["ultra", "maximum", "max"],
            [VintageRtxRenderProfile.Extreme] = ["extreme", "enthusiast", "nouvelle-generation"],
            [VintageRtxRenderProfile.Cinematic] = ["cinematic", "cinematique", "cinématique", "offline"],
            [VintageRtxRenderProfile.Custom] = ["custom", "manual", "personnalise", "personnalisé"]
        };

        foreach ((VintageRtxRenderProfile expected, string[] names) in aliases)
        {
            foreach (string name in names)
            {
                Assert.IsTrue(VintageRtxModSystem.TryResolveRenderProfile(
                    name,
                    out VintageRtxRenderProfile actual));
                Assert.AreEqual(expected, actual);
            }
        }

        Assert.IsTrue(VintageRtxModSystem.TryResolveRenderProfile(
            "QUALITY",
            out VintageRtxRenderProfile uppercase));
        Assert.AreEqual(VintageRtxRenderProfile.Quality, uppercase);
        Assert.IsFalse(VintageRtxModSystem.TryResolveRenderProfile(null, out _));
        Assert.IsFalse(VintageRtxModSystem.TryResolveRenderProfile("unknown", out _));
    }

    /// <summary>Verifies artistic presets cannot overwrite the selected hardware work budget.</summary>
    [TestMethod]
    public void ArtisticPresetsDoNotMutateHardwareProfileControls()
    {
        VintageRtxConfig config = new();
        config.ApplyRenderProfile(VintageRtxRenderProfile.Quality);
        (VintageRtxRenderProfile Profile, bool Adaptive, float Budget, float ReflectionDistance,
            float BounceDistance, int BounceRays, int PointSamples, float SunDistance,
            float RayDistance, int Rays, int Steps) expected =
        (
            config.RenderProfile,
            config.AdaptiveQualityEnabled,
            config.GpuBudgetMilliseconds,
            config.ReflectionDistance,
            config.VoxelBounceDistance,
            config.VoxelBounceRayCount,
            config.PointLightShadowSamples,
            config.SunShadowDistance,
            config.RayDistance,
            config.RayCount,
            config.RaySteps);

        foreach (string preset in new[] { "neutral", "cinematic", "vivid" })
        {
            config.ApplyPreset(preset);
            Assert.AreEqual(expected.Profile, config.RenderProfile, preset);
            Assert.AreEqual(expected.Adaptive, config.AdaptiveQualityEnabled, preset);
            Assert.AreEqual(expected.Budget, config.GpuBudgetMilliseconds, preset);
            Assert.AreEqual(expected.ReflectionDistance, config.ReflectionDistance, preset);
            Assert.AreEqual(expected.BounceDistance, config.VoxelBounceDistance, preset);
            Assert.AreEqual(expected.BounceRays, config.VoxelBounceRayCount, preset);
            Assert.AreEqual(expected.PointSamples, config.PointLightShadowSamples, preset);
            Assert.AreEqual(expected.SunDistance, config.SunShadowDistance, preset);
            Assert.AreEqual(expected.RayDistance, config.RayDistance, preset);
            Assert.AreEqual(expected.Rays, config.RayCount, preset);
            Assert.AreEqual(expected.Steps, config.RaySteps, preset);
        }
    }

    /// <summary>Verifies profiles use readable stable names in the persisted JSON contract.</summary>
    [TestMethod]
    public void ProfilesRoundTripAsReadableJsonNames()
    {
        foreach (VintageRtxRenderProfile profile in Enum.GetValues<VintageRtxRenderProfile>())
        {
            VintageRtxConfig source = new()
            {
                SchemaVersion = VintageRtxConfig.CurrentSchemaVersion,
                RenderProfile = profile
            };

            string json = JsonConvert.SerializeObject(source);
            StringAssert.Contains(json, $"\"RenderProfile\":\"{profile}\"");
            VintageRtxConfig restored = JsonConvert.DeserializeObject<VintageRtxConfig>(json)!;
            Assert.AreEqual(profile, restored.RenderProfile);
        }
    }
}
