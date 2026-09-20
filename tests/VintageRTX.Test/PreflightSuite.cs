using System.Security.Cryptography;
using System.Text.Json;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>
/// Supports preflight Suite within the deterministic VintageRTX test infrastructure.
/// </summary>
internal static class PreflightSuite
{
    private static readonly (string Name, Action Test)[] Tests =
    [
            ("configuration migration", TestConfigurationMigration),
            ("configuration clamps", TestConfigurationClamps),
            ("occlusion-aware irradiance cache", TestIrradianceCache),
            ("display shader contracts", TestDisplayShaderContracts),
            ("file-backed PBR contract", TestPbrContract),
            ("embedded base PBR assets", TestEmbeddedBasePbrAssets),
            ("deferred PBR transport contract", TestPbrDeferredTransport),
            ("unpremultiplied PBR decode", TestPbrUnpremultipliedDecode),
            ("deterministic PBR atlas mapping", TestPbrAtlasMapping),
            ("PBR asset references", TestPbrAssetReferences),
            ("temporal reconstruction artifact detection", TestTemporalArtifactDetection),
            ("weather wetness smoothing", TestWeatherWetnessSmoothing),
            ("deterministic environment verification", TestDeterministicEnvironmentVerification),
            ("standalone render lab contract", TestStandaloneRenderLabContract),
            ("runtime scenario coverage", TestScenarioCoverage)
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

        throw new KeyNotFoundException($"Unknown preflight test '{name}'.");
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
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"Preflight: {Tests.Length - failed}/{Tests.Length} passed.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Verifies the configuration Migration regression contract against deterministic fixture data.
    /// </summary>
    private static void TestConfigurationMigration()
    {
        VintageRtxConfig config = new() { SunShadowDistance = 24.0f };
        Assert(config.Migrate(), "schema 1 configuration should migrate");
        Assert(config.SchemaVersion == VintageRtxConfig.CurrentSchemaVersion, "schema must become current");
        Assert(config.SunShadowDistance == 64.0f, "migration must enable long sun reach");
        Assert(config.PointLightRadius == 18.0f, "migration must localize the legacy point-light radius");
        Assert(config.TemporalAccumulationEnabled, "migration must enable temporal accumulation");
        Assert(config.TemporalHistoryWeight == 0.92f, "migration must set the temporal history weight");
        Assert(config.PointLightShadowSamples == 4, "migration must enable full coherent shadow supersampling");
        Assert(config.VoxelBounceRayCount == 2, "migration must enable bounded voxel bounce rays");
        Assert(config.VoxelReflectionsEnabled, "migration must enable off-screen voxel reflections");
        Assert(config.RelightingStrength == 0.88f, "migration must enable transport-dominant relighting");
        Assert(config.SkyLightStrength == 0.78f, "migration must enable traced sky irradiance");
        Assert(config.EmissiveLightStrength == 1.25f, "migration must calibrate emissive transport");
        Assert(config.GpuBudgetMilliseconds == 2.00f, "migration must preserve native-resolution frame pacing");
        Assert(config.Contrast == 1.0f, "migration must remove the legacy contrast grade");
        Assert(config.Saturation == 1.0f, "migration must remove the legacy saturation grade");
        Assert(config.Vibrance == 0.0f, "migration must remove the legacy vibrance grade");
        Assert(config.Vignette == 0.04f, "migration must retain only a subtle vignette");
        Assert(!config.Migrate(), "migration must be idempotent");
    }

    /// <summary>
    /// Verifies the configuration Clamps regression contract against deterministic fixture data.
    /// </summary>
    private static void TestConfigurationClamps()
    {
        VintageRtxConfig config = new()
        {
            GpuBudgetMilliseconds = -10,
            SunShadowDistance = 1000,
            PointLightRadius = 1000,
            ReflectionDistance = -1,
            VoxelBounceDistance = 1000,
            VoxelBounceRayCount = 100,
            PointLightShadowSamples = 100,
            RayCount = 100,
            RaySteps = 100
        };
        config.Clamp();
        Assert(config.GpuBudgetMilliseconds == 0.75f, "GPU budget lower bound");
        Assert(config.SunShadowDistance == 640.0f, "sun range upper bound");
        Assert(config.PointLightRadius == 32.0f, "point range upper bound");
        Assert(config.ReflectionDistance == 1.0f, "reflection range lower bound");
        Assert(config.VoxelBounceDistance == 24.0f, "voxel bounce range upper bound");
        Assert(config.VoxelBounceRayCount == 4, "voxel bounce ray-count upper bound");
        Assert(config.PointLightShadowSamples == 8, "point-shadow sample upper bound");
        Assert(config.RayCount == 8, "scene-space ray-count upper bound");
        Assert(config.RaySteps == 24, "scene-space step upper bound");
    }

    /// <summary>
    /// Verifies the irradiance Cache regression contract against deterministic fixture data.
    /// </summary>
    private static void TestIrradianceCache()
    {
        byte[] voxels = new byte[VoxelScene.Width * VoxelScene.Height * VoxelScene.Depth * 4];
        for (int z = 0; z < VoxelScene.Depth; z++)
        {
            for (int y = 0; y < VoxelScene.Height; y++)
            {
                int wallCell = (z * VoxelScene.Height + y) * VoxelScene.Width + 32;
                // Warm reflective wall: the cache must contain its bounced
                // albedo, not a raw copy of the emitter colour in empty air.
                voxels[wallCell * 4] = 220;
                voxels[wallCell * 4 + 1] = 96;
                voxels[wallCell * 4 + 2] = 42;
                voxels[wallCell * 4 + 3] = 128;
            }
        }

        VoxelLight light = new(
            28.5f,
            24.5f,
            32.5f,
            1.0f,
            0.65f,
            0.34f,
            1.25f,
            "test:lantern",
            []);
        VoxelRadianceField field = VoxelScene.BuildRadianceField(
            voxels,
            [light],
            0,
            0,
            0,
            new Vintagestory.API.MathTools.Vec3f());
        byte[] cache = field.Irradiance;
        Assert(
            cache.Length == VoxelScene.Width * VoxelScene.Height * VoxelScene.Depth * 3,
            "irradiance cache dimensions");
        Assert(field.Direction.Length == cache.Length, "irradiance direction dimensions");
        int litCell = ((32 * VoxelScene.Height + 24) * VoxelScene.Width + 30) * 3;
        int blockedCell = ((32 * VoxelScene.Height + 24) * VoxelScene.Width + 34) * 3;
        Assert(cache[litCell] > 0, "cache must propagate through open cells");
        Assert(
            cache[litCell] > cache[litCell + 1] && cache[litCell + 1] > cache[litCell + 2],
            "cache must preserve the illuminated surface chromaticity");
        Assert(
            cache[blockedCell] == 0 && cache[blockedCell + 1] == 0 && cache[blockedCell + 2] == 0,
            "opaque wall must stop cached irradiance");
        Assert(
            field.Direction[litCell] > 127,
            "directional cache must point from the receiver toward the reflecting wall");
    }

    /// <summary>
    /// Verifies the display Shader Contracts regression contract against deterministic fixture data.
    /// </summary>
    private static void TestDisplayShaderContracts()
    {
        string shader = DisplayShaderSource.LoadFromFileSystem(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX")).Fragment.ReplaceLineEndings("\n");
        Assert(shader.Contains("traceSunVisibility", StringComparison.Ordinal), "hybrid long sun trace missing");
        Assert(shader.Contains("voxelLighting.sunDirect * sunLightStrength", StringComparison.Ordinal), "visible sun transport is not applied to the final frame");
        Assert(shader.Contains("diagnostic mask normalized across the day", StringComparison.Ordinal), "sun shadow debug normalization missing");
        Assert(shader.Contains("normalizedSunShadow", StringComparison.Ordinal), "receiver-independent sun debug mask missing");
        Assert(shader.Contains("float shadowPotential = geometricReceiver * attenuation", StringComparison.Ordinal)
            && shader.Contains("potentialWeight += shadowPotential", StringComparison.Ordinal)
            && shader.Contains("blockedShadowEnergy", StringComparison.Ordinal)
            && shader.Contains("result.sunShadow = sunGeometricReceiver", StringComparison.Ordinal)
            && shader.Contains("dot(worldGeometricNormal, sunDirection)", StringComparison.Ordinal)
            && !shader.Contains("potentialWeight += potential", StringComparison.Ordinal),
            "projected shadow masks must follow geometric receivers, not normal-map microfacets");
        Assert(shader.Contains("traceScreenSpaceReflection", StringComparison.Ordinal), "localized reflection trace missing");
        Assert(shader.Contains("traceVoxelReflection", StringComparison.Ordinal), "off-screen voxel reflection trace missing");
        Assert(
            shader.Contains(
                "screenSpaceReflectionsEnabled != 0 || voxelReflectionsEnabled != 0",
                StringComparison.Ordinal),
            "the performance reflection LOD must keep voxel/environment reflections when SSR is disabled");
        Assert(shader.Contains("const int MAX_RAYS = 8;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_STEPS = 24;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_REFLECTION_STEPS = 24;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_VOXEL_REFLECTION_STEPS = 64;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_POINT_LIGHT_SAMPLES = 8;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_VOXEL_BOUNCE_RAYS = 4;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_VOXEL_BOUNCE_STEPS = 16;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_VOXEL_BOUNCE_SHADOW_STEPS = 16;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_SKY_RAYS = 4;", StringComparison.Ordinal)
            && shader.Contains("const int MAX_SKY_STEPS = 48;", StringComparison.Ordinal),
            "shader traversal bounds must accommodate the Cinematic profile exactly");
        Assert(shader.Contains("voxelReflectionSteps", StringComparison.Ordinal), "adaptive voxel reflection budget missing");
        Assert(shader.Contains("shadeVoxelReflectionHit", StringComparison.Ordinal), "off-screen reflection lighting reconstruction missing");
        Assert(shader.Contains("reflection.color", StringComparison.Ordinal)
            && shader.Contains("* clamp(reflection.confidence", StringComparison.Ordinal)
            && shader.Contains("* 2.5", StringComparison.Ordinal),
            "reflection diagnostic channel or exposure missing");
        Assert(shader.Contains("2.0 * horizonUvY - uv.y", StringComparison.Ordinal)
            && shader.Contains("reflectedFarViewPosition", StringComparison.Ordinal)
            && shader.Contains("directionalProjectionValid", StringComparison.Ordinal)
            && shader.Contains("reflection.planarConfidence", StringComparison.Ordinal)
            && shader.Contains("transmissiveSurface", StringComparison.Ordinal)
            && shader.Contains("voxelLightingEnabled != 0", StringComparison.Ordinal)
            && shader.Contains("voxelFluidSurface", StringComparison.Ordinal)
            && shader.Contains("fluidColumnEvidence", StringComparison.Ordinal),
            "water reflection horizon or material response missing");
        Assert(shader.Contains("float voxelRoughnessLimit", StringComparison.Ordinal)
            && shader.Contains("allowVoxelFallback != 0", StringComparison.Ordinal)
            && shader.Contains("&& roughness < voxelRoughnessLimit", StringComparison.Ordinal)
            && shader.Contains("|| debugView == 9", StringComparison.Ordinal),
            "adaptive off-screen reflection roughness gate missing");
        Assert(shader.Contains("float ssrRoughnessLimit", StringComparison.Ordinal)
            && shader.Contains("planarFallback <= 0.001 && roughness < ssrRoughnessLimit", StringComparison.Ordinal),
            "adaptive screen-space reflection roughness gate missing");
        Assert(shader.Contains("reflectionSteps == 1", StringComparison.Ordinal)
            && shader.Contains("? 0.70710678", StringComparison.Ordinal)
            && shader.Contains("float distanceFade = mix(1.0, 0.35, stepFraction)", StringComparison.Ordinal),
            "single-step SSR must sample useful room range and retain nonzero hit confidence");
        Assert(shader.Contains("roughness >= maximumSpecularRoughness", StringComparison.Ordinal)
            && shader.Contains("planarEncodedFluidSurface", StringComparison.Ordinal)
            && shader.Contains("step(surfaceDistance + 0.025, opaqueDistance)", StringComparison.Ordinal)
            && shader.Contains("liquidInterfaceDepthVisibility", StringComparison.Ordinal)
            && shader.Contains("surfaceDistance - 0.018", StringComparison.Ordinal)
            && shader.Contains("gOpaquePosition", StringComparison.Ordinal)
            && shader.Contains("opaqueInterfaceDepthVisibility", StringComparison.Ordinal)
            && shader.Contains("gOpaqueDepth", StringComparison.Ordinal)
            && shader.Contains("opaqueDepthVisibility", StringComparison.Ordinal)
            && shader.Contains("interfaceGeometryProbeLocal", StringComparison.Ordinal)
            && shader.Contains("interfaceFineOccupancyVisibility", StringComparison.Ordinal)
            && shader.Contains("fineOccupancyCell / fineOccupancySize", StringComparison.Ordinal)
            && shader.Contains("visibleFluidColumnEvidence", StringComparison.Ordinal)
            && !shader.Contains("max(fluidColumnEvidence, supportedTransparentWater)", StringComparison.Ordinal)
            && shader.Contains("gLiquidDepth", StringComparison.Ordinal)
            && shader.Contains("inverseProjection", StringComparison.Ordinal)
            && shader.Contains("liquidDepthHorizontalSupport", StringComparison.Ordinal)
            && shader.Contains("* liquidInterfaceDepthVisibility", StringComparison.Ordinal)
            && shader.Contains("reflection.planarWorldPosition - vec3(0.0, 0.08, 0.0)", StringComparison.Ordinal),
            "camera-ray water-plane reconstruction, partial-geometry occlusion, or matte-material rejection missing");
        Assert(shader.Contains("rippleOffset", StringComparison.Ordinal)
            && shader.Contains("reflectedSky", StringComparison.Ordinal),
            "water ripple or sky-reflection reconstruction missing");
        Assert(shader.Contains("screenFluidEvidence", StringComparison.Ordinal)
            && shader.Contains("reflectedFarViewPosition", StringComparison.Ordinal)
            && shader.Contains("independently of scene occupancy", StringComparison.Ordinal)
            && shader.Contains("useDirectionalProjection", StringComparison.Ordinal)
            && shader.Contains("worldPosition.y - 0.05", StringComparison.Ordinal),
            "water reflection must reject recursive surface samples and preserve local parallax scenery");
        Assert(shader.Contains("float unresolvedEnvironmentBlend", StringComparison.Ordinal)
            && shader.Contains("mix(0.035, 0.080, 1.0 - liquidInterfaceRoughness)", StringComparison.Ordinal)
            && shader.Contains("screenSceneConfidence", StringComparison.Ordinal),
            "resolved water reflections must retain local contrast instead of receiving a global sky wash");
        Assert(shader.Contains("vec3 linearAlbedo = srgbToLinear", StringComparison.Ordinal)
            && shader.Contains("linearAlbedo * sampleVoxelIrradiance(hitPosition, hitNormal)", StringComparison.Ordinal)
            && shader.Contains("return linearToSrgb(max(reflectedColor, vec3(0.0)))", StringComparison.Ordinal),
            "secondary lighting must accumulate linear radiance before encoding its compatibility carrier");
        Assert(!shader.Contains("float coneWidth = roughness * roughness * 0.26", StringComparison.Ordinal)
            && shader.Contains("normalize(reflect(incidentDirection, worldNormal))", StringComparison.Ordinal)
            && !shader.Contains("float(temporalFrameIndex & 7) * 2.39996322973", StringComparison.Ordinal),
            "the compatibility mirror query must not shift scenery using a fixed or animated cone offset");
        Assert(shader.Contains("struct LiquidOpticalProfile", StringComparison.Ordinal)
            && shader.Contains("defaultWaterOpticalProfile", StringComparison.Ordinal)
            && shader.Contains("profile.ior = 1.333", StringComparison.Ordinal)
            && shader.Contains("const float airIor = 1.000293", StringComparison.Ordinal)
            && shader.Contains("exactDielectricFresnel", StringComparison.Ordinal)
            && shader.Contains("refract(", StringComparison.Ordinal),
            "liquid optics must expose explicit IOR, refraction and exact dielectric Fresnel");
        Assert(shader.Contains("uniform sampler3D voxelLiquidMetadata", StringComparison.Ordinal)
            && shader.Contains("uniform sampler2D liquidOpticalProfiles", StringComparison.Ordinal)
            && shader.Contains("lookupLiquidOpticalProfile", StringComparison.Ordinal)
            && shader.Contains("texelFetch(liquidOpticalProfiles, ivec2(7, profileId), 0)", StringComparison.Ordinal)
            && shader.Contains("texelFetch(liquidOpticalProfiles, ivec2(8, profileId), 0)", StringComparison.Ordinal)
            && shader.Contains("texelFetch(liquidOpticalProfiles, ivec2(9, profileId), 0)", StringComparison.Ordinal)
            && shader.Contains("legacyWaterColumn", StringComparison.Ordinal)
            && shader.Contains("profile.waveAmplitude", StringComparison.Ordinal)
            && shader.Contains("profile.dynamicViscosityPaS", StringComparison.Ordinal)
            && shader.Contains("profile.surfaceTensionNm", StringComparison.Ordinal)
            && shader.Contains("rendererTimeSeconds", StringComparison.Ordinal)
            && shader.Contains("liquidWindVector", StringComparison.Ordinal)
            && shader.Contains("liquidWindMetresPerSecondPerEngineUnit", StringComparison.Ordinal)
            && shader.Contains("0.00512", StringComparison.Ordinal)
            && shader.Contains("modePeriodSeconds", StringComparison.Ordinal)
            && shader.Contains("liquidWaveModeCount = 16", StringComparison.Ordinal)
            && shader.Contains("modeIndex >= liquidWaveModeLimit", StringComparison.Ordinal)
            && shader.Contains("packetPhaseModulation", StringComparison.Ordinal)
            && shader.Contains("packetPhaseDerivative", StringComparison.Ordinal)
            && shader.Contains("phasePositionMetres + phaseWarpMetres", StringComparison.Ordinal)
            && shader.Contains("phaseWarpDerivativeX", StringComparison.Ordinal)
            && shader.Contains("phaseWarpDerivativeZ", StringComparison.Ordinal)
            && shader.Contains("normalizedPhaseGradient += resolvedSurfaceSlope", StringComparison.Ordinal)
            && shader.Contains("profile.waveAmplitude * profile.metresPerWorldBlock", StringComparison.Ordinal)
            && shader.Contains("vec2 coupledPhaseWarpGradientPerMetre = baseSlope", StringComparison.Ordinal)
            && shader.Contains("vec2 directionalUv = uv;", StringComparison.Ordinal)
            && shader.Contains("vec2 waveDirectionalUv = uv;", StringComparison.Ordinal)
            && shader.Contains("profile.bubbleEmissionBoost", StringComparison.Ordinal),
            "per-liquid optics/dynamics LUT or legacy-only water fallback missing");
        Assert(shader.Contains("reflection.liquidAbsorption", StringComparison.Ordinal)
            && shader.Contains("reflection.liquidScattering", StringComparison.Ordinal)
            && shader.Contains("reflection.liquidEmission", StringComparison.Ordinal)
            && shader.Contains("reflection.liquidPathLength", StringComparison.Ordinal)
            && shader.Contains("liquidExtinctionCoefficient(", StringComparison.Ordinal)
            && !shader.Contains("float bulkExtinction = -log(bulkTransmission)", StringComparison.Ordinal)
            && shader.Contains("liquidTransmittance = exp(", StringComparison.Ordinal)
            && shader.Contains("reflection.liquidMetresPerWorldBlock", StringComparison.Ordinal)
            && shader.Contains("liquidVolumeAlbedo", StringComparison.Ordinal)
            && shader.Contains("reflection.liquidScattering / max(liquidExtinction", StringComparison.Ordinal)
            && shader.Contains("reflection.liquidTransmission", StringComparison.Ordinal)
            && shader.Contains("reflection.liquidOpaque", StringComparison.Ordinal)
            && shader.Contains("liquidBackscatterPhase", StringComparison.Ordinal)
            && shader.Contains("integratedLiquidEmission", StringComparison.Ordinal)
            && shader.Contains("transmittedLiquidTransport", StringComparison.Ordinal),
            "liquid volume must apply profile-driven Beer-Lambert transport to the lit receiver");
        Assert(shader.Contains("liquidTransmissionUv", StringComparison.Ordinal)
            && shader.Contains("refractedWorldTarget", StringComparison.Ordinal)
            && shader.Contains("refractedDepthValid", StringComparison.Ordinal)
            && shader.Contains("sampleReflectionSource(\n            reflection.liquidTransmissionUv)", StringComparison.Ordinal)
            && shader.Contains("One bounded tap provides visible refraction", StringComparison.Ordinal),
            "liquid transmission must use one bounded, validated refracted scene sample");
        Assert(shader.Contains("vec3 liquidSurfaceTransport = transmittedLiquidTransport", StringComparison.Ordinal)
            && shader.Contains("* (1.0 - planarFresnel)", StringComparison.Ordinal)
            && shader.Contains("trustedLiquidReflection * planarFresnel", StringComparison.Ordinal)
            && shader.Contains("liquidSurfaceTransport", StringComparison.Ordinal)
            && !shader.Contains("float planarFresnel = 0.0204", StringComparison.Ordinal),
            "liquid interface must split transmission and reflection once with exact Fresnel");
        Assert(shader.Contains("reflectedFarWorldPosition = planarWorldPosition", StringComparison.Ordinal)
            && shader.Contains("* directionalInsideFrame;", StringComparison.Ordinal)
            && shader.Contains("uniform sampler2D entityMirrorColor;", StringComparison.Ordinal)
            && shader.Contains("texture(entityMirrorColor, entityMirrorUv)", StringComparison.Ordinal)
            && shader.Contains("if (entityMirrorSupport <= 0.001)", StringComparison.Ordinal)
            && !shader.Contains("tracePlanarLiquidScreenSample", StringComparison.Ordinal)
            && !shader.Contains("for (int iteration = 0; iteration < 3; iteration++)", StringComparison.Ordinal),
            "rasterized entity mirror or bounded scenery fallback missing");
        Assert(shader.Contains("voxelRainSurface", StringComparison.Ordinal)
            && shader.Contains("sampleRainExposure", StringComparison.Ordinal)
            && shader.Contains("float rainReceiverY = worldPosition.y + worldNormal.y * 0.035", StringComparison.Ordinal)
            && shader.Contains("rainBlockY - 0.08", StringComparison.Ordinal)
            && shader.Contains("rainBlockY + 0.08", StringComparison.Ordinal)
            && shader.Contains("float wetSurface", StringComparison.Ordinal),
            "rain-height exposure or localized wetness missing");
        Assert(shader.Contains("allowVoxelFallback", StringComparison.Ordinal)
            && shader.Contains("wetEnvironmentRadiance", StringComparison.Ordinal)
            && shader.Contains("wetSurface * (1.0 - metallic)", StringComparison.Ordinal)
            && shader.Contains("debugView == 13", StringComparison.Ordinal),
            "bounded wet reflection, dielectric F0, or diagnostic mask missing");
        Assert(shader.Contains("float directSpecularHint", StringComparison.Ordinal)
            && shader.Contains("if (directSpecularHint > 0.001)", StringComparison.Ordinal)
            && shader.Contains("roughness >= maximumSpecularRoughness", StringComparison.Ordinal)
            && shader.Contains("one column lookup is sufficient", StringComparison.Ordinal),
            "rough dielectric specular or reflection-miss fast path missing");
        // MaterialTransportTests executes GGX against an independent Smith reference and
        // an integrated white furnace. This guard only checks its production wiring.
        Assert(shader.Contains("materialFresnel(f0, vh)", StringComparison.Ordinal)
            && shader.Contains("vec3 directSpecularRadiance = voxelLighting.directSpecular", StringComparison.Ordinal)
            && shader.Contains("materialF0) * emissiveLightStrength", StringComparison.Ordinal)
            && shader.Contains("materialF0) * sunLightStrength", StringComparison.Ordinal)
            && !shader.Contains("metallic * 3.80", StringComparison.Ordinal)
            && !shader.Contains("localEnvironmentSpecular", StringComparison.Ordinal),
            "direct conductor lighting must use material Fresnel, not post-hoc metal gains or a diffuse-cache spotlight");
        Assert(shader.Contains("MAX_VOXEL_STEPS", StringComparison.Ordinal), "bounded voxel loop missing");
        Assert(shader.Contains("traceFineBlockVisibility", StringComparison.Ordinal), "hierarchical fine occupancy trace missing");
        Assert(shader.Contains("Hierarchical DDA", StringComparison.Ordinal), "empty-space skip contract missing");
        Assert(shader.Contains("reflectionSteps", StringComparison.Ordinal), "adaptive reflection bound missing");
        Assert(shader.Contains("texture-aware fine mask", StringComparison.Ordinal), "emissive cage traversal missing");
        Assert(shader.Contains("voxelLightCasterMasks", StringComparison.Ordinal), "fine light-caster texture missing");
        Assert(shader.Contains("LIGHT_CASTER_SCALE", StringComparison.Ordinal), "fine light-caster scale missing");
        Assert(shader.Contains("ALPHA_CAGE_VISIBILITY", StringComparison.Ordinal), "alpha cage coverage missing");
        Assert(shader.Contains("occludedWeight", StringComparison.Ordinal)
            && shader.Contains("potentialWeight", StringComparison.Ordinal),
            "energy-weighted multi-source shadow aggregation missing");
        Assert(shader.Contains("blockedDirect", StringComparison.Ordinal)
            && shader.Contains("strongestBlockedEnergy", StringComparison.Ordinal)
            && shader.Contains("localShadowEnergy", StringComparison.Ordinal),
            "localized energy-weighted point-light shadow missing");
        Assert(!shader.Contains("tracedShadowAttenuation", StringComparison.Ordinal), "binary whole-frame shadow attenuation remains");
        Assert(shader.Contains("historyColor", StringComparison.Ordinal), "temporal history sampler missing");
        Assert(shader.Contains("samplePointLightSurface", StringComparison.Ordinal), "area-light quadrature missing");
        Assert(shader.Contains("diametrically opposed pairs", StringComparison.Ordinal), "centred point-shadow sampling missing");
        Assert(shader.Contains("cannot flicker between frames", StringComparison.Ordinal), "stationary shadow phase missing");
        Assert(shader.Contains("pointLightShadowSamples", StringComparison.Ordinal), "adaptive shadow sample uniform missing");
        Assert(shader.Contains("fixed coherent basis", StringComparison.Ordinal), "world-stable SSGI sampling missing");
        Assert(shader.Contains("Cross-bilateral temporal denoiser", StringComparison.Ordinal)
            && shader.Contains("centerHistory - centerCarrier", StringComparison.Ordinal)
            && shader.Contains("- neighborCarrier", StringComparison.Ordinal)
            && shader.Contains("centerCarrier", StringComparison.Ordinal)
            && shader.Contains("coherentNeighborCount >= 2", StringComparison.Ordinal)
            && shader.Contains("transportEnvelopeMargin", StringComparison.Ordinal)
            && shader.Contains("centerCarrier + filteredDelta", StringComparison.Ordinal),
            "source-detail-preserving temporal transport denoiser missing");
        Assert(shader.Contains("primaryFirstPersonOverlay", StringComparison.Ordinal)
            && shader.Contains("must remain the direct raster carrier", StringComparison.Ordinal)
            && shader.Contains("surfaceTemporalBlend", StringComparison.Ordinal)
            && shader.Contains("reflections of objects that have since sunk", StringComparison.Ordinal)
            && shader.Contains("length(viewPosition - vec3(1.0))", StringComparison.Ordinal)
            && shader.Contains("uniform sampler2D gDirectPosition;", StringComparison.Ordinal)
            && shader.Contains("deferredFirstPersonOverlayEvidence(uv, position)", StringComparison.Ordinal)
            && shader.Contains("uniform int entityMirrorEnabled;", StringComparison.Ordinal)
            && shader.Contains("entityMirrorSupport", StringComparison.Ordinal)
            && shader.Contains("uniform sampler2D reflectionSourceColor;", StringComparison.Ordinal)
            && shader.Contains("sampleReflectionSource(fallbackUv)", StringComparison.Ordinal)
            && shader.Contains("foliage and OIT silhouettes may have colour", StringComparison.Ordinal)
            && shader.Contains("float fallbackSupport = 1.0 - fallbackRejectedGeometry;", StringComparison.Ordinal)
            && !shader.Contains("float blurPhase", StringComparison.Ordinal),
            "pre-held reflection source, scenery fallback, or first-person overlay mask missing");
        Assert(shader.Contains("temporalDenoiseSamples", StringComparison.Ordinal)
            && shader.Contains("albedoDetailSamples", StringComparison.Ordinal),
            "adaptive full-screen texture-fetch budget missing");
        Assert(shader.Contains("Variance clipping", StringComparison.Ordinal), "temporal variance clipping missing");
        // The old two-neighbour dilation invented receivers at silhouettes. The replacement
        // preserves missing geometry and resolves shadow visibility against the true receiver.
        // Executable GPU witnesses live in tests/feedback/test_feedback_contracts.py.
        Assert(!shader.Contains("consistentNeighbours >= 2", StringComparison.Ordinal)
            && !shader.Contains("geometryWasRepaired = true", StringComparison.Ordinal)
            && shader.Contains("readGBufferTexel(gPosition, uv)", StringComparison.Ordinal)
            && shader.Contains("readGBufferTexel(gNormal, uv)", StringComparison.Ordinal)
            && shader.Contains("resolveSurfaceShadow(position, normal,", StringComparison.Ordinal)
            && shader.Contains("float planeError = abs(dot(geometricViewNormal, position - centerPosition))", StringComparison.Ordinal)
            && shader.Contains("traceRawPointShadowVisibilities(worldPosition, worldNormal, pointA, pointB)", StringComparison.Ordinal),
            "exact receiver geometry or depth-guided shadow reconstruction missing");
        Assert(shader.Contains("opaqueGeometryFallback", StringComparison.Ordinal)
            && shader.Contains("&& isInsideVoxelVolume(worldPosition) ? 0.86 : 0.0", StringComparison.Ordinal)
            && shader.Contains("riskyReliability", StringComparison.Ordinal),
            "volume-bounded opaque voxel-boundary material fallback missing");
        Assert(shader.Contains("evaluateDirectSpecular", StringComparison.Ordinal)
            && shader.Contains("directSpecularRadiance", StringComparison.Ordinal)
            && shader.Contains("surfaceRoughness", StringComparison.Ordinal)
            && shader.Contains("resolvedSpecularEligibility", StringComparison.Ordinal)
            && shader.Contains("metallicReflectance", StringComparison.Ordinal),
            "normal/roughness-driven microfacet lighting missing");
        Assert(shader.Contains("unresolvedDiffuseEnvironment", StringComparison.Ordinal)
            && !shader.Contains("vegetationSurface * 0.85", StringComparison.Ordinal)
            && shader.Contains("unresolvedConductorEnvironment", StringComparison.Ordinal)
            && shader.Contains("resolvedPbrRoughness * 0.28", StringComparison.Ordinal)
            && shader.Contains("packedSurfaceAvailable", StringComparison.Ordinal)
            && shader.Contains("unresolvedMaterialConfidence", StringComparison.Ordinal)
            && shader.Contains("compositeRadiance += unresolvedMaterialRadiance", StringComparison.Ordinal),
            "bounded unresolved material energy for foliage/conductors missing");
        // The same solar visibility path is evaluated independently of the sky-ray budget.
        // tests/transport also executes blocked-sun/visible-sky and converse fixtures.
        Assert(shader.Contains("float bouncedSunVisibility = traceSunVisibility(", StringComparison.Ordinal)
            && shader.Contains("bouncedSunReceiver * bouncedSunVisibility * sunLightStrength", StringComparison.Ordinal)
            && !shader.Contains("bouncedSunVisibility = bouncedSkyVisibility", StringComparison.Ordinal),
            "secondary solar visibility must be traced independently rather than borrowed from a sky sample");
        Assert(shader.Contains("traceVoxelDiffuseBounce", StringComparison.Ordinal), "off-screen voxel diffuse bounce missing");
        Assert(!shader.Contains("bounceTrace", StringComparison.Ordinal), "temporary voxel-bounce trace instrumentation remains");
        Assert(shader.Contains("secondary radiance", StringComparison.Ordinal), "voxel-bounce radiance diagnostic missing");
        Assert(shader.Contains("accumulated += linearHitAlbedo * incident * distanceFade", StringComparison.Ordinal)
            && shader.Contains("maximumComponent(linearHitAlbedo) <= 0.0", StringComparison.Ordinal),
            "secondary transport must preserve material RGB without inventing a neutral reflectance floor");
        Assert(shader.Contains("transparencyRisk", StringComparison.Ordinal), "opaque/transparent relighting split missing");
        Assert(shader.Contains("float daylightRelighting = smoothstep(", StringComparison.Ordinal)
            && shader.Contains("float enclosedPhysicalRelighting = mix(", StringComparison.Ordinal)
            && shader.Contains("float opaquePhysicalTransportConfidence = packedSurfaceAvailable", StringComparison.Ordinal)
            && shader.Contains("float opaquePhysicalTransportFloor = mix(0.96, 0.90, metallic)", StringComparison.Ordinal)
            && shader.Contains("mix(enclosedPhysicalRelighting, 0.84, exteriorConfidence)", StringComparison.Ordinal),
            "stable photometric local-light transport dominance missing");
        Assert(shader.Contains("vec3 exposedRadiance = positiveRadiance * exp2(exposure)", StringComparison.Ordinal)
            && shader.Contains("float value = exposedLuminance * 0.65", StringComparison.Ordinal)
            && shader.Contains("gamutCompression", StringComparison.Ordinal)
            && shader.Contains("negativeChromaPeak", StringComparison.Ordinal)
            && shader.Contains("lowerGamutCompression", StringComparison.Ordinal)
            && shader.Contains("highlightSaturation", StringComparison.Ordinal)
            && shader.Contains("vec3 relitDisplay = filmicToneMap(compositeRadiance)", StringComparison.Ordinal)
            && !shader.Contains("color = max(color * exp2(exposure)", StringComparison.Ordinal)
            && !shader.Contains("highlightShoulder", StringComparison.Ordinal),
            "scene-linear exposure, filmic shoulder or highlight gamut compression missing");
        AssertFilmicToneCurve();
        Assert(shader.Contains("emitterColor", StringComparison.Ordinal), "emitter chromaticity preservation missing");
        Assert(shader.Contains("texelFetch(gMaterial", StringComparison.Ordinal)
            && shader.Contains("packedMaterial & 64", StringComparison.Ordinal),
            "exact packed PBR material decode missing");
        Assert(shader.Contains("authoredMetallic", StringComparison.Ordinal)
            && shader.Contains("authoredEmissive", StringComparison.Ordinal)
            && shader.Contains("engineGlowGate", StringComparison.Ordinal)
            && shader.Contains("rasterGlow", StringComparison.Ordinal),
            "metallic or localized emissive material transport missing");
        string chunkShader = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "assets",
            "game",
            "shaders",
            "chunkopaque.fsh"));
        Assert(chunkShader.Contains("Only authored PBR emission is packed", StringComparison.Ordinal)
            && chunkShader.Contains("not a reliable emission classification", StringComparison.Ordinal),
            "ordinary terrain glowLevel can still be misclassified as emission");
        Assert(shader.Contains("sunColorStrength.w < 0.08", StringComparison.Ordinal)
            && shader.Split("sunColorStrength.w >= 0.08", StringSplitOptions.None).Length >= 3,
            "night sky, direct-sun or secondary-sun trace fast path missing");
        Assert(shader.Contains("hierarchical DDA sky visibility", StringComparison.Ordinal)
            && shader.Contains("return traceVoxelVisibility(rayOrigin, rayTarget, -1)", StringComparison.Ordinal),
            "continuous sky-occlusion traversal missing");
        Assert(shader.Contains("worldGeometricNormal", StringComparison.Ordinal)
            && shader.Contains("derivativeNormal = cross", StringComparison.Ordinal),
            "geometric-normal ray bias missing");
        Assert(!shader.Contains("floatingWorldOrigin) < 0.75", StringComparison.Ordinal)
            && shader.Contains("visibility += traceVoxelVisibility", StringComparison.Ordinal),
            "held emitters must trace world occlusion, not receive unconditional visibility");
        Assert(shader.Contains("boundedMultiLightCluster", StringComparison.Ordinal)
            && shader.Contains("denseDynamicLightCluster", StringComparison.Ordinal)
            && shader.Contains("visibility += traceVoxelVisibility", StringComparison.Ordinal)
            && shader.Contains("result.direct += unoccludedDirect * visibility", StringComparison.Ordinal)
            && shader.Contains("result.shadow = (1.0 - result.visibility) * localShadowEnergy", StringComparison.Ordinal)
            && !shader.Contains("dominantPotential", StringComparison.Ordinal),
            "independent bounded multi-light shadow accumulation missing");
        Assert(!shader.Contains("interlacePointShadows", StringComparison.Ordinal)
            && !shader.Contains("shadowSampleReweight", StringComparison.Ordinal)
            && !shader.Contains("traceablePotentialWeight / max(potential, 0.001)", StringComparison.Ordinal),
            "variance-amplifying temporal point-shadow source estimator remains enabled");
        Assert(shader.Contains("longRangeRoofGate", StringComparison.Ordinal)
            && shader.Contains("96-block sun ray", StringComparison.Ordinal)
            && shader.Contains("skyRayCount <= 1", StringComparison.Ordinal),
            "long-range sky roof gate missing");
        Assert(shader.Contains("R=blocked light, G=emitter proximity, B=authored/engine emission", StringComparison.Ordinal)
            && shader.Contains("debugView == 10", StringComparison.Ordinal),
            "transport component diagnostic missing");
        Assert(shader.Contains("voxelLighting.irradianceCache", StringComparison.Ordinal)
            && shader.Contains("sampleVoxelIrradiance", StringComparison.Ordinal)
            && shader.Contains("sampleVoxelIrradianceDirection", StringComparison.Ordinal)
            && shader.Contains("voxelIrradianceDirection", StringComparison.Ordinal)
            && shader.Contains("irradianceDirectionSum", StringComparison.Ordinal)
            && shader.Contains("directionalIrradiance", StringComparison.Ordinal)
            && !shader.Contains("irradianceSpecularRadiance", StringComparison.Ordinal)
            && shader.Contains("vec3 directSpecularRadiance = voxelLighting.directSpecular", StringComparison.Ordinal)
            && shader.Contains("voxelLighting.bounce * pointLightBounceStrength * 0.90", StringComparison.Ordinal)
            && !shader.Contains("voxelLighting.blockedDirect\n                    * pointLightBounceStrength", StringComparison.Ordinal),
            "stable irradiance cache or bounded detail bounce missing");
        Assert(shader.Contains("traceVoxelSkyLighting", StringComparison.Ordinal)
            && shader.Contains("traceSkyRay", StringComparison.Ordinal)
            && shader.Contains("skyRayCount", StringComparison.Ordinal),
            "bounded traced sky irradiance missing");
        Assert(shader.Contains("vec3 rasterCarrier = sourceLinear", StringComparison.Ordinal)
            && shader.Contains("vec3 hybridTransport = rasterCarrier", StringComparison.Ordinal)
            && shader.Contains("+ indirectDiffuseRadiance * 0.74", StringComparison.Ordinal)
            && shader.Contains("+ directDiffuseRadiance * 0.34", StringComparison.Ordinal)
            && shader.Contains("vec3 physicalTransport = surfaceAlbedo", StringComparison.Ordinal)
            && shader.Contains("vec3 energyMatchedPhysicalTransport = physicalTransport", StringComparison.Ordinal)
            && shader.Contains("float exteriorEnergyScale = clamp(", StringComparison.Ordinal)
            && shader.Contains("physicalRelightingWeight = opaquePhysicalTransportConfidence", StringComparison.Ordinal)
            && shader.Contains("authoredAlbedoLuminance", StringComparison.Ordinal)
            && shader.Contains("sourceLinear * tracedExteriorShadow", StringComparison.Ordinal)
            && !shader.Contains("shadowSubtraction", StringComparison.Ordinal),
            "detail-preserving traced relighting contract missing");
        Assert(shader.Contains("vec3 relitDisplay = filmicToneMap(compositeRadiance)", StringComparison.Ordinal)
            && shader.Contains("reconstructSurfaceAlbedo", StringComparison.Ordinal)
            && shader.Contains("reconstructSurfaceEmission", StringComparison.Ordinal),
            "linear PBR reconstruction or HDR tone mapping missing");
        Assert(shader.Contains("transmissionPreservation", StringComparison.Ordinal)
            && shader.Contains("surfaceReliability", StringComparison.Ordinal)
            && shader.Contains("exactDielectricFresnel", StringComparison.Ordinal)
            && shader.Contains("liquidSurfaceTransport", StringComparison.Ordinal)
            && shader.Contains("skyEnvironmentRadiance(environmentDirection)", StringComparison.Ordinal)
            && shader.Contains("float waterColorHint =", StringComparison.Ordinal)
            && shader.Contains("fluidColumnSupport", StringComparison.Ordinal)
            && shader.Contains("hasVisibleContainedSurface", StringComparison.Ordinal)
            && shader.Contains("containedSurfaceEvidence", StringComparison.Ordinal)
            && shader.Contains("entityMirrorColor", StringComparison.Ordinal)
            && shader.Contains("needsVolumeLiquidMetadata", StringComparison.Ordinal)
            && !shader.Contains("exteriorTransportScale", StringComparison.Ordinal)
            && shader.Contains("exteriorEnergyMatch", StringComparison.Ordinal)
            && shader.Contains("voxelLighting.skyVisibility * sunColorStrength.w", StringComparison.Ordinal)
            && shader.Contains("tracedExteriorShadow", StringComparison.Ordinal)
            && shader.Contains("metallicTransportScale", StringComparison.Ordinal)
            && shader.Contains("reflectionAddWeight", StringComparison.Ordinal),
            "water/glass, exterior exposure, or material-gated reflection missing");
        Assert(shader.Contains("MAX_VOXEL_BOUNCE_STEPS", StringComparison.Ordinal), "bounded voxel bounce traversal missing");
        Assert(shader.Contains("traceCoarseBounceVisibility", StringComparison.Ordinal), "voxel bounce occlusion missing");
        Assert(shader.Contains("voxelBounceRayCount", StringComparison.Ordinal), "adaptive voxel bounce ray budget missing");
        Assert(shader.Contains("voxelBounceSteps", StringComparison.Ordinal)
            && shader.Contains("voxelBounceShadowSteps", StringComparison.Ordinal),
            "adaptive voxel bounce traversal budgets missing");
        Assert(shader.Contains("temporalFrameIndex % max(secondaryBounceCadence, 1) == 0", StringComparison.Ordinal)
            && shader.Contains("debugView == 8", StringComparison.Ordinal),
            "performance-tier temporal bounce cadence missing");
        Assert(VoxelScene.ShouldCastShadow(EnumChunkRenderPass.Opaque), "opaque mesh faces must cast shadows");
        Assert(VoxelScene.ShouldCastShadow(EnumChunkRenderPass.OpaqueNoCull), "alpha-tested cage faces must be inspected");
        Assert(!VoxelScene.ShouldCastShadow(EnumChunkRenderPass.Transparent), "transparent mesh faces must transmit light");
        MeshData canonicalCube = CreateCanonicalCubeMesh();
        Assert(VoxelScene.IsCanonicalUnitCubeMesh(canonicalCube),
            "six proven axis-aligned faces must retain the full-cube fast path");
        MeshData cubeWithFaceHoles = CreateCanonicalCubeMesh();
        for (int face = 0; face < 6; face++)
        {
            int offset = face * 6;
            cubeWithFaceHoles.Indices[offset + 3] = cubeWithFaceHoles.Indices[offset];
            cubeWithFaceHoles.Indices[offset + 4] = cubeWithFaceHoles.Indices[offset + 1];
            cubeWithFaceHoles.Indices[offset + 5] = cubeWithFaceHoles.Indices[offset + 2];
        }
        Assert(!VoxelScene.IsCanonicalUnitCubeMesh(cubeWithFaceHoles),
            "two triangles per boundary are insufficient when they do not cover the face");
        MeshData alphaTestedCube = CreateCanonicalCubeMesh();
        alphaTestedCube.RenderPassesAndExtraBits = Enumerable.Repeat(
            (short)EnumChunkRenderPass.OpaqueNoCull,
            6).ToArray();
        Assert(!VoxelScene.IsCanonicalUnitCubeMesh(alphaTestedCube),
            "alpha-tested cube topology must not bypass authored texture coverage");
        MeshData crossedPlanes = CreateCrossedPlaneMesh();
        Assert(!VoxelScene.IsCanonicalUnitCubeMesh(crossedPlanes),
            "unit bounds and cube-like counts must not classify crossed vegetation as a cube");
        ulong crossedMask = VoxelScene.RasterizeOpaqueMeshMask(crossedPlanes);
        Assert(System.Numerics.BitOperations.PopCount(crossedMask) is > 0 and < 64,
            "crossed vegetation must occupy its authored local planes, not all 64 subvoxels");
        ulong leftCrossedMask = VoxelScene.RasterizeOpaqueMeshMask(
            CreateCrossedPlaneMesh(0.0f, 0.40f));
        ulong rightCrossedMask = VoxelScene.RasterizeOpaqueMeshMask(
            CreateCrossedPlaneMesh(0.60f, 1.0f));
        Assert(leftCrossedMask != 0
            && rightCrossedMask != 0
            && leftCrossedMask != rightCrossedMask
            && (leftCrossedMask & rightCrossedMask) == 0,
            "crossed-plane occupancy must preserve the authored local block offset");
        TextureAlphaSampler alphaSampler = new(
            new TextureAtlasPosition { x1 = 0.25f, y1 = 0.25f, x2 = 0.75f, y2 = 0.75f },
            new TextureAlphaData(2, 2, [0, 255, 255, 0]));
        Assert(!alphaSampler.IsOpaque(0.30f, 0.30f), "transparent cage texel must transmit light");
        Assert(alphaSampler.IsOpaque(0.70f, 0.30f), "opaque cage texel must cast a shadow");
        TextureAlphaSampler transparentPlane = new(
            new TextureAtlasPosition { x1 = 0.0f, y1 = 0.0f, x2 = 1.0f, y2 = 1.0f },
            new TextureAlphaData(2, 2, [0, 0, 0, 0]));
        byte transparentCoverage = VoxelScene.TriangleCellOpaqueAlphaCoverage(
            new Vec3f(0.125f, 0.125f, 0.50f),
            VoxelScene.OccupancyScale,
            new Vec3f(0.0f, 0.0f, 0.50f),
            new Vec3f(1.0f, 0.0f, 0.50f),
            new Vec3f(0.0f, 1.0f, 0.50f),
            new TextureUv(0.0f, 0.0f),
            new TextureUv(1.0f, 0.0f),
            new TextureUv(0.0f, 1.0f),
            transparentPlane);
        Assert(transparentCoverage == 0,
            "transparent vegetation/cage texels must not project a shadow");
        byte[] syntheticCage = new byte[VoxelScene.LightCasterVoxelCount];
        for (int z = 3; z < 13; z++)
        {
            for (int x = 3; x < 13; x++)
            {
                syntheticCage[(z * VoxelScene.LightCasterScale) * VoxelScene.LightCasterScale + x] = 255;
                syntheticCage[(z * VoxelScene.LightCasterScale + 15) * VoxelScene.LightCasterScale + x] = 255;
            }
        }
        for (int y = 1; y < 15; y++)
        {
            syntheticCage[(3 * VoxelScene.LightCasterScale + y) * VoxelScene.LightCasterScale + 3] = 255;
            syntheticCage[(12 * VoxelScene.LightCasterScale + y) * VoxelScene.LightCasterScale + 12] = 255;
        }
        VoxelScene.ClearEmissionPocket(syntheticCage, new Vec3f(0.5f, 0.5f, 0.5f));
        LightCasterBandCoverage cageBands = VoxelScene.MeasureLightCasterBands(syntheticCage);
        Assert(cageBands.LowerOccupied > 0
            && cageBands.MiddleOccupied > 0
            && cageBands.UpperOccupied > 0,
            "lantern lower plate, uprights and roof must occupy separate caster bands");
        Assert(cageBands.Empty > 0,
            "the synthetic glass volume must remain transmissive between cage members");
        ulong narrowInstance = VoxelScene.BuildInstanceOccupancyMask(
            [new Cuboidf(0.0f, 0.0f, 0.0f, 0.25f, 1.0f, 1.0f)],
            null,
            true);
        ulong wideInstance = VoxelScene.BuildInstanceOccupancyMask(
            [new Cuboidf(0.0f, 0.0f, 0.0f, 0.75f, 1.0f, 1.0f)],
            null,
            true);
        Assert(narrowInstance != wideInstance,
            "two dynamic/chiseled instances sharing a block id must retain distinct masks");
        const ulong lanternBaseMask = 0x0000_0660_0660_0000UL;
        Assert(VoxelScene.ResolveDynamicInstanceOccupancy(
                lanternBaseMask,
                true,
                false,
                ulong.MaxValue,
                ulong.MaxValue,
                true) == lanternBaseMask,
            "full interaction boxes must not replace a lantern/anvil default mesh");
        Assert(VoxelScene.ResolveDynamicInstanceOccupancy(
                lanternBaseMask,
                true,
                true,
                narrowInstance,
                0,
                true) == narrowInstance
            && VoxelScene.ResolveDynamicInstanceOccupancy(
                lanternBaseMask,
                true,
                true,
                wideInstance,
                0,
                true) == wideInstance,
            "position-defined chiseled instances must replace, not cache, their default mesh");
        Assert(!VoxelScene.InstanceGeometryReplacesDefaultMesh(
                "BlockEntityLantern",
                "game:lantern-large-east")
            && !VoxelScene.InstanceGeometryReplacesDefaultMesh(
                "BlockEntityAnvil",
                "game:anvil-iron")
            && VoxelScene.InstanceGeometryReplacesDefaultMesh(
                "BlockEntityMicroBlock",
                "game:chiseledblock"),
            "lantern/anvil must retain their default base mesh while chisel replaces it per instance");
        MeshData localInstanceMesh = CreateCrossedPlaneMesh(0.15f, 0.45f);
        ulong localInstanceMask = VoxelScene.RasterizeOpaqueMeshMask(localInstanceMesh);
        MeshData worldInstanceMesh = localInstanceMesh.Clone().Translate(512191, 115, 512153);
        ulong normalizedWorldMask = VoxelScene.RasterizeInstanceMeshes(
            [worldInstanceMesh],
            512191,
            115,
            512153);
        int chunkX = ((512191 % GlobalConstants.ChunkSize) + GlobalConstants.ChunkSize)
            % GlobalConstants.ChunkSize;
        int chunkY = ((115 % GlobalConstants.ChunkSize) + GlobalConstants.ChunkSize)
            % GlobalConstants.ChunkSize;
        int chunkZ = ((512153 % GlobalConstants.ChunkSize) + GlobalConstants.ChunkSize)
            % GlobalConstants.ChunkSize;
        MeshData chunkInstanceMesh = localInstanceMesh.Clone().Translate(chunkX, chunkY, chunkZ);
        ulong normalizedChunkMask = VoxelScene.RasterizeInstanceMeshes(
            [chunkInstanceMesh],
            512191,
            115,
            512153);
        Assert(localInstanceMask != 0
            && normalizedWorldMask == localInstanceMask
            && normalizedChunkMask == localInstanceMask,
            "instance tessellation must preserve block-local geometry from world or chunk coordinates");
        InstanceTerrainMeshCollector instanceCollector = new();
        MeshData callerOwnedMesh = CreateCrossedPlaneMesh(0.55f, 0.85f);
        float capturedFirstX = callerOwnedMesh.xyz[0];
        instanceCollector.AddMeshData(callerOwnedMesh, 0);
        callerOwnedMesh.xyz[0] = 0.0f;
        Assert(instanceCollector.Meshes.Count == 1
            && Math.Abs(instanceCollector.Meshes[0].xyz[0] - capturedFirstX) < 0.0001f,
            "ITerrainMeshPool collector must clone meshes that OnTesselation forbids reusing");
        MeshData transparentInstance = CreateQuadMesh(
            [
                new Vec3f(0, 0, 0.5f),
                new Vec3f(1, 0, 0.5f),
                new Vec3f(1, 1, 0.5f),
                new Vec3f(0, 1, 0.5f)
            ]);
        transparentInstance.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        ulong transparentInstanceMask = VoxelScene.RasterizeInstanceMeshes(
            [transparentInstance],
            0,
            0,
            0);
        Assert(transparentInstanceMask == 0,
            "transparent instance meshes must not enter chiseled shadow occupancy");
        InstanceMeshDiagnostics transparentDiagnostics = VoxelScene.InspectInstanceMeshes(
            [transparentInstance],
            0,
            0,
            0,
            transparentInstanceMask);
        Assert(transparentDiagnostics.VertexCount == 4
            && transparentDiagnostics.IndexCount == 6
            && transparentDiagnostics.OpaqueTriangles == 0
            && transparentDiagnostics.TransparentTriangles == 2
            && transparentDiagnostics.InvalidTriangles == 0
            && transparentDiagnostics.Reason == "transparent-only",
            "instance diagnostics must identify a valid transparent-only chiseled mesh");
        Assert(VoxelScene.TryResolveTessellatedInstanceOccupancy(
                true,
                1,
                true,
                true,
                ulong.MaxValue,
                transparentInstanceMask,
                out ulong authoritativeTransparentMask)
            && authoritativeTransparentMask == 0,
            "authoritative transparent chisel meshes must never fall back to a full interaction cube");
        Assert(VoxelScene.TryResolveTessellatedInstanceOccupancy(
                true,
                1,
                true,
                true,
                ulong.MaxValue,
                localInstanceMask,
                out ulong authoritativeMicroblockMask)
            && authoritativeMicroblockMask == localInstanceMask,
            "opaque microblock tessellation must retain its precise per-instance mask");
        Assert(!VoxelScene.TryResolveTessellatedInstanceOccupancy(
                true,
                1,
                false,
                false,
                lanternBaseMask,
                transparentInstanceMask,
                out _),
            "a transparent non-replacing BlockEntity overlay must not suppress its static base mesh");
        string voxelSceneSource = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "Rendering",
            "VoxelScene.cs"));
        Assert(voxelSceneSource.Contains("GetDefaultBlockMesh(block)", StringComparison.Ordinal)
            && voxelSceneSource.Contains("cached with { GeometryKind = BlockGeometryKind.DynamicInstance }", StringComparison.Ordinal)
            && voxelSceneSource.Contains("occupancy.HasDetailedMesh && occupancy.OpaqueTriangles > 0", StringComparison.Ordinal)
            && voxelSceneSource.Contains("bands lower={17}, middle={18}, upper={19}, empty={20}", StringComparison.Ordinal)
            && voxelSceneSource.Contains("Occupancy fallback: code={0}, reason={1}, count={2}", StringComparison.Ordinal),
            "BlockEntity default-mesh caster or runtime fallback/band diagnostics missing");
        Assert(voxelSceneSource.Contains("GetBlockEntity(position)", StringComparison.Ordinal)
            && voxelSceneSource.Contains("blockEntity.OnTesselation(collector, api.Tesselator)", StringComparison.Ordinal)
            && voxelSceneSource.Contains("instanceMeshOccupancyByPosition.Remove", StringComparison.Ordinal)
            && voxelSceneSource.Contains("Instance mesh {0} at ({1},{2},{3})", StringComparison.Ordinal)
            && voxelSceneSource.Contains("Instance mesh zero {0} at ({1},{2},{3})", StringComparison.Ordinal)
            && voxelSceneSource.Contains("TryResolveTessellatedInstanceOccupancy", StringComparison.Ordinal),
            "public per-instance OnTesselation capture/cache/invalidation contract missing");
        (bool fineChanged, bool sunChanged) = VoxelScene.ClassifyDirtyBlockVolumes(
            10, 10, 10,
            0, 0, 0,
            -32, -16, -32);
        Assert(fineChanged && sunChanged,
            "a nearby block edit must invalidate both fine and solar occupancy");
        (fineChanged, sunChanged) = VoxelScene.ClassifyDirtyBlockVolumes(
            -20, 10, 10,
            0, 0, 0,
            -32, -16, -32);
        Assert(!fineChanged && sunChanged,
            "a distant caster edit must still invalidate the solar clipmap cell");
        (fineChanged, sunChanged) = VoxelScene.ClassifyDirtyBlockVolumes(
            200, 200, 200,
            0, 0, 0,
            -32, -16, -32);
        Assert(!fineChanged && !sunChanged,
            "an edit outside both volumes must not consume an incremental update slot");
        (int sunX, int sunY, int sunZ) = VoxelScene.CalculateSunClipmapOrigin(
            100,
            80,
            100,
            new Vec3f(1.0f, 0.0f, 0.0f));
        Assert(sunX % VoxelScene.SunOccupancyScale == 0
            && sunY % VoxelScene.SunOccupancyScale == 0
            && sunZ % VoxelScene.SunOccupancyScale == 0,
            "sun clipmap origins must stay cell-aligned");
        Assert(
            sunX + VoxelScene.SunWorldWidth - (100 + 24)
                >= VoxelScene.MaximumSunTraceDistance,
            "sunward clipmap extent must cover 96 blocks beyond visible receivers");
        Vec3f[] sunDirections =
        [
            new Vec3f(1.0f, 0.0f, 0.0f),
            new Vec3f(-1.0f, 0.0f, 0.0f),
            new Vec3f(0.0f, 1.0f, 0.0f),
            new Vec3f(0.0f, -1.0f, 0.0f),
            new Vec3f(0.57735026f, 0.57735026f, 0.57735026f),
            new Vec3f(-0.57735026f, 0.57735026f, -0.57735026f)
        ];
        foreach (Vec3f direction in sunDirections)
        {
            (int originX, int originY, int originZ) = VoxelScene.CalculateSunClipmapOrigin(
                -101,
                79,
                -99,
                direction);
            Assert(VoxelScene.SunClipmapContainsFullTrace(
                    originX,
                    originY,
                    originZ,
                    -101,
                    79,
                    -99,
                    direction),
                $"sun clipmap must contain every 96-block trace for direction {direction}");
        }
        Assert(shader.Contains("voxelSunOccupancy", StringComparison.Ordinal)
            && shader.Contains("traceSunClipmapVisibility", StringComparison.Ordinal)
            && shader.Contains("MAX_SUN_STEPS must never silently shorten", StringComparison.Ordinal),
            "dedicated conservative 96-block sun clipmap contract missing");
        Assert(voxelSceneSource.Contains("Sun-shadow reach adapted:", StringComparison.Ordinal)
            && voxelSceneSource.Contains("desired-view={1}", StringComparison.Ordinal)
            && voxelSceneSource.Contains("approved-view={2}", StringComparison.Ordinal)
            && voxelSceneSource.Contains("effective-trace={3}", StringComparison.Ordinal)
            && voxelSceneSource.Contains("distant-cell={4}", StringComparison.Ordinal),
            "runtime view-distance shadow-reach evidence missing");
        MeshData emissiveMesh = new(false)
        {
            xyz =
            [
                0.20f, 0.30f, 0.20f,
                0.21f, 0.30f, 0.20f,
                0.20f, 0.31f, 0.20f,
                0.21f, 0.31f, 0.20f,
                0.205f, 0.305f, 0.21f,
                0.80f, 0.20f, 0.20f,
                0.80f, 0.80f, 0.80f
            ],
            Flags = [196, 196, 196, 196, 196, 196, 196],
            VerticesCount = 7
        };
        Vintagestory.API.MathTools.Vec3f emissionOffset = VoxelScene.FindEmissionOffset(emissiveMesh);
        Assert(emissionOffset.X < 0.30f && emissionOffset.Y < 0.40f,
            "the dense glowing flame cluster must drive the light position");
        Assert(
            VoxelScene.TriangleIntersectsVoxel(
                new Vintagestory.API.MathTools.Vec3f(0.125f, 0.125f, 0.125f),
                new Vintagestory.API.MathTools.Vec3f(0.0f, 0.0f, 0.20f),
                new Vintagestory.API.MathTools.Vec3f(0.25f, 0.0f, 0.20f),
                new Vintagestory.API.MathTools.Vec3f(0.0f, 0.25f, 0.20f)),
            "triangle crossing a subvoxel must be occupied");
        Assert(
            !VoxelScene.TriangleIntersectsVoxel(
                new Vintagestory.API.MathTools.Vec3f(0.125f, 0.125f, 0.125f),
                new Vintagestory.API.MathTools.Vec3f(0.0f, 0.0f, 0.40f),
                new Vintagestory.API.MathTools.Vec3f(0.25f, 0.0f, 0.40f),
                new Vintagestory.API.MathTools.Vec3f(0.0f, 0.25f, 0.40f)),
            "separated triangle must not inflate the caster");

        string renderer = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "Rendering",
            "FilmicDisplayRenderer.cs"));
        Assert(
            renderer.Contains("GpuBudgetMilliseconds * 1.02", StringComparison.Ordinal),
            "adaptive frame-pacing tolerance must stay close to the configured budget");
        Assert(
            !renderer.Contains("reflectionDiagnosticCapture", StringComparison.Ordinal)
                && !renderer.Contains("voxelReflectionDiagnosticCapture", StringComparison.Ordinal)
                && !renderer.Contains("voxelBounceDiagnosticCapture", StringComparison.Ordinal)
                && renderer.Contains("effectiveReflectionSteps = adaptiveQualityLevel switch", StringComparison.Ordinal)
                && renderer.Contains("effectiveVoxelReflectionSteps = adaptiveQualityLevel switch", StringComparison.Ordinal)
                && renderer.Contains("DiffuseTransportBudget.RayCount(", StringComparison.Ordinal),
            "native diagnostics must use exactly the runtime profile's reflection and bounce budgets");
        Assert(
            renderer.Contains("VintageRtxRenderProfile.Extreme => 16", StringComparison.Ordinal)
                && renderer.Contains("VintageRtxRenderProfile.Cinematic => 24", StringComparison.Ordinal)
                && renderer.Contains("VintageRtxRenderProfile.Extreme => 48", StringComparison.Ordinal)
                && renderer.Contains("VintageRtxRenderProfile.Cinematic => 64", StringComparison.Ordinal)
                && renderer.Contains("int maximumVoxelLights = VoxelScene.MaximumLightCount;", StringComparison.Ordinal),
            "fixed maximum-fidelity profiles must reach their authored reflection and light budgets");
        Assert(
            renderer.Contains("DynamicLightCandidate", StringComparison.Ordinal)
                && renderer.Contains("EntityLightCollector", StringComparison.Ordinal)
                && renderer.Contains("entity.SourceIndex", StringComparison.Ordinal)
                && renderer.Contains("SelectStableLightCandidates", StringComparison.Ordinal)
                && renderer.Contains("retained ? 1.08f : 1.0f", StringComparison.Ordinal)
                && renderer.Contains("FindDuplicateDynamicLight", StringComparison.Ordinal),
            "stable camera-relevant multi-light selection/deduplication missing");
        Span<int> stableSelection = stackalloc int[3];
        int stableCount = FilmicDisplayRenderer.SelectStableLightCandidates(
            [10, 20, 30, 40],
            [1.00f, 0.99f, 0.98f, 0.96f],
            [30, 10, 20],
            stableSelection);
        Assert(stableCount == 3
            && stableSelection[0] == 2
            && stableSelection[1] == 0
            && stableSelection[2] == 1,
            "stable top-K selection must preserve retained source order near score ties");
        Span<int> promotedSelection = stackalloc int[3];
        FilmicDisplayRenderer.SelectStableLightCandidates(
            [10, 20, 30, 40],
            [1.00f, 0.99f, 0.98f, 1.20f],
            [30, 10, 20],
            promotedSelection);
        Assert(promotedSelection[0] == 0
            && promotedSelection[1] == 1
            && promotedSelection[2] == 3,
            "a materially stronger source must enter top-K without reordering retained lights");
        FilmicDisplayRenderer.IndependentLightEnergy threeLightEnergy =
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy(
                [1.0f, 0.8f, 0.5f],
                [0.0f, 0.5f, 1.0f]);
        Assert(Math.Abs(threeLightEnergy.Potential - 2.3f) < 0.0001f
            && Math.Abs(threeLightEnergy.Direct - 0.9f) < 0.0001f
            && Math.Abs(threeLightEnergy.Blocked - 1.4f) < 0.0001f
            && Math.Abs(threeLightEnergy.Visibility - 0.9f / 2.3f) < 0.0001f,
            "three differently occluded sources must retain independent energy before summation");
        Assert(
            renderer.Contains("shader.Uniform(\"transportInterlace\", 0)", StringComparison.Ordinal)
                && !renderer.Contains("adaptiveQualityLevel == 2 ? 1 : 0", StringComparison.Ordinal),
            "performance tier must retain artifact-free full-frame transport");
        Assert(
            renderer.Contains("shader.Uniform(\"secondaryBounceCadence\", 1)", StringComparison.Ordinal)
                && shader.Contains("secondaryBounceCadence <= 1", StringComparison.Ordinal)
                && shader.Contains("temporalFrameIndex % max(secondaryBounceCadence, 1) == 0", StringComparison.Ordinal),
            "every profile must trace coherent secondary radiance without zero-energy cadence frames");
        Assert(
            renderer.Contains("shader.Uniform(\"shadowTemporalBlend\", 0.0f)", StringComparison.Ordinal)
                && shader.Contains("if (hitNormalRoughness.a < -0.0005)", StringComparison.Ordinal)
                && shader.Contains(
                    "result.indirect += srgbToLinear(sampleReflectionSource(hitUv)) * confidence;",
                    StringComparison.Ordinal)
                && !shader.Contains(
                    "result.indirect += texture(sourceColor, hitUv).rgb * confidence;",
                    StringComparison.Ordinal),
            "screen-space receiver history and mismatched moving-entity colour must not contaminate fixed lighting");
        Assert(
            renderer.Contains("adaptiveQualityLevel < 2", StringComparison.Ordinal),
            "performance tier must shed the secondary diffuse full-screen trace");
        Assert(
            renderer.Contains(
                "config.ScreenSpaceReflectionsEnabled\r\n                    && gBufferAvailable\r\n                    && adaptiveQualityLevel < 2",
                StringComparison.Ordinal)
            || renderer.Contains(
                "config.ScreenSpaceReflectionsEnabled\n                    && gBufferAvailable\n                    && adaptiveQualityLevel < 2",
                StringComparison.Ordinal),
            "performance tier must use voxel/environment reflection without the screen-depth walk");
        Assert(
            renderer.IndexOf("UpdateAdaptiveQuality(config, captureFrame);", StringComparison.Ordinal)
                >= 0
                && renderer.IndexOf("UpdateAdaptiveQuality(config, captureFrame);", StringComparison.Ordinal)
                    < renderer.IndexOf("int effectiveReflectionSteps = adaptiveQualityLevel switch", StringComparison.Ordinal),
            "adaptive quality must resolve atomically before any tier-dependent transport uniform");
        Assert(
            !renderer.Contains("!denseMultiLightCluster", StringComparison.Ordinal)
                && !renderer.Contains("denseMultiLightCluster\r\n                        ? 0", StringComparison.Ordinal)
                && !renderer.Contains("denseMultiLightCluster\n                        ? 0", StringComparison.Ordinal),
            "dense light clusters must bound ray work without disabling reflections or voxel bounce");
        Assert(
            renderer.Contains("2 => 3", StringComparison.Ordinal),
            "performance tier must retain a bounded off-screen reflection");
        Assert(
            renderer.Contains("2 => Math.Min(config.PointLightShadowSamples, 2)", StringComparison.Ordinal)
                && renderer.Contains("currentDynamicLightCount > 0", StringComparison.Ordinal)
                && renderer.Contains("availableDynamicLightCount > 1 ? 1 : 2", StringComparison.Ordinal)
                && renderer.Contains("denseMultiLightCluster = IsDenseMultiLightCluster(", StringComparison.Ordinal)
                && renderer.Contains("staticLights is { Length: > 1 }", StringComparison.Ordinal)
                && renderer.Contains("2 => 3", StringComparison.Ordinal)
                && renderer.Contains("effectivePointLightShadowSamples = Math.Min", StringComparison.Ordinal)
                && shader.Contains("if (sampleCount <= 1)", StringComparison.Ordinal)
                && shader.Contains("float pairSide = float(pairedSampleIndex & 1)", StringComparison.Ordinal)
                && !shader.Contains("frame * 0.75487766625", StringComparison.Ordinal),
            "performance tier must retain stationary centre-balanced emitter sampling");
        Assert(renderer.Contains("int maximumVoxelLights = VoxelScene.MaximumLightCount;", StringComparison.Ordinal)
            && VoxelScene.MaximumLightCount == 8,
            "profile changes must not shrink the represented source set");
        Assert(
            renderer.Contains("AdaptiveUpgradeFrames = 7200", StringComparison.Ordinal),
            "adaptive promotion must require sustained headroom");
        Assert(
            renderer.Contains("AdaptiveDowngradeFrames = 120", StringComparison.Ordinal)
                && renderer.Contains("AdaptiveTransitionCooldownFrames = 120", StringComparison.Ordinal),
            "adaptive transport must reach its stable native-resolution tier promptly");
        Assert(
            renderer.Contains("directional irradiance field is the stable", StringComparison.Ordinal)
                && renderer.Contains("2 => 0", StringComparison.Ordinal),
            "performance tier must use the stable irradiance LOD without periodic bounce spikes");
        Assert(
            renderer.Contains("Repeating a short hierarchical sky DDA", StringComparison.Ordinal)
                && renderer.Contains("int effectiveSkyRayCount = adaptiveQualityLevel switch", StringComparison.Ordinal)
                && shader.Contains("skyRayCount <= 0 || sunColorStrength.w < 0.08", StringComparison.Ordinal),
            "performance tier must reuse stable sky fields instead of duplicating a full-screen voxel DDA");
        Assert(
            renderer.Contains("voxelIrradianceTexture", StringComparison.Ordinal)
                && renderer.Contains("voxelIrradianceDirectionTexture", StringComparison.Ordinal)
                && renderer.Contains("snapshot.IrradianceDirection", StringComparison.Ordinal)
                && renderer.Contains("TextureMinFilter.Linear", StringComparison.Ordinal)
                && renderer.Contains("snapshot.Irradiance", StringComparison.Ordinal),
            "linearly filtered irradiance cache upload missing");
        Assert(
            Math.Abs(RuntimeLogValidator.FrameTimeCostMilliseconds(120.0, 80.0) - 4.1666667) < 0.001,
            "performance gate must compare added frame time across refresh rates");
        Assert(
            double.IsPositiveInfinity(RuntimeLogValidator.FrameTimeCostMilliseconds(0.0, 60.0))
                && double.IsPositiveInfinity(RuntimeLogValidator.FrameTimeCostMilliseconds(60.0, double.NaN)),
            "performance gate must reject invalid FPS samples before deriving frame time");
    }

    /// <summary>
    /// Asserts filmic Tone Curve and throws when the regression contract is violated.
    /// </summary>
    private static void AssertFilmicToneCurve()
    {
        static double Map(double radiance)
        {
            double value = Math.Max(radiance, 0.0) * 0.65;
            return Math.Clamp(
                value * (2.51 * value + 0.03)
                    / (value * (2.43 * value + 0.59) + 0.14),
                0.0,
                1.0);
        }

        double black = Map(0.0);
        double toe = Map(0.02);
        double middleGrey = Map(0.18);
        double diffuseWhite = Map(1.0);
        double highlight = Map(4.0);
        Assert(black == 0.0, "filmic curve does not preserve black");
        Assert(toe < 0.02, "filmic curve has no shadow toe");
        Assert(middleGrey is > 0.13 and < 0.19,
            "filmic middle-grey calibration drifted");
        Assert(diffuseWhite is > 0.65 and < 0.75,
            "filmic diffuse-white shoulder drifted");
        Assert(highlight > diffuseWhite && highlight < 1.0,
            "filmic highlights are clipped or non-monotonic");
    }

    /// <summary>
    /// Verifies the pbr Contract regression contract against deterministic fixture data.
    /// </summary>
    private static void TestPbrContract()
    {
        string root = TestPaths.FindRepositoryRoot();
        string runtimeSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "Rendering",
            "PbrTerrainRenderer.cs"));
        string sidecarStoreSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "Rendering",
            "PbrSidecarAssetStore.cs"));
        string assetDiscoveryPatchSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "Rendering",
            "PbrAssetDiscoveryPatch.cs"));
        string textureVariantSanitizerSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "Rendering",
            "PbrTextureVariantSanitizer.cs"));
        string modSystemSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "VintageRtxModSystem.cs"));
        string runtimeHarnessSource = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "VintageRTX.Test",
            "RuntimeHarness.cs"));
        Assert(!runtimeSource.Contains("PbrAtlasShaderSource", StringComparison.Ordinal), "runtime generator reference remains");
        Assert(runtimeSource.Contains("runtime generation=disabled", StringComparison.Ordinal), "runtime must declare offline-only loading");
        Assert(runtimeSource.Contains("vintagertxMaterialTex", StringComparison.Ordinal)
            && runtimeSource.Contains("_m.png", StringComparison.Ordinal)
            && runtimeSource.Contains("_e.png", StringComparison.Ordinal)
            && runtimeSource.Contains("BuildConsolidatedPbrPixels", StringComparison.Ordinal)
            && !runtimeSource.Contains("normalAtlasTexture", StringComparison.Ordinal),
            "runtime consolidated PBR atlas or open material sidecars missing");
        Assert(runtimeSource.Contains("TextureMinFilter.Linear", StringComparison.Ordinal)
            && runtimeSource.Contains("TextureMagFilter.Linear", StringComparison.Ordinal)
            && !runtimeSource.Contains("TextureMinFilter.Nearest", StringComparison.Ordinal),
            "consolidated normal/roughness atlas must preserve continuous filtering");
        Assert(runtimeSource.IndexOf("ApplyManifestOverrides", StringComparison.Ordinal)
                < runtimeSource.IndexOf("ApplySidecarOverrides", StringComparison.Ordinal)
            && !runtimeSource.Contains("ApplyBundledArchiveOverrides", StringComparison.Ordinal)
            && runtimeSource.Contains("manifestFallbackSources", StringComparison.Ordinal)
            && runtimeSource.Contains("api.Assets.Origins", StringComparison.Ordinal)
            && runtimeSource.Contains("GetAssets(location, true)", StringComparison.Ordinal)
            && runtimeSource.Contains("MatchesAnySha256", StringComparison.Ordinal),
            "native asset-manager sidecars must override manifest fallbacks");
        Assert(modSystemSource.Contains("public override double ExecuteOrder() => 0.15", StringComparison.Ordinal)
            && modSystemSource.Contains("public override void Start(ICoreAPI coreApi)", StringComparison.Ordinal)
            && modSystemSource.Contains("PbrAssetDiscoveryPatch.Install,", StringComparison.Ordinal)
            && modSystemSource.Contains("installPbrDiscoveryPatch(coreApi)", StringComparison.Ordinal)
            && modSystemSource.Contains("public override void AssetsLoaded(ICoreAPI coreApi)", StringComparison.Ordinal)
            && modSystemSource.Contains("PbrAssetDiscoveryPatch.TakeOrCapture,", StringComparison.Ordinal)
            && modSystemSource.Contains("takeOrCapturePbrAssets(coreApi)", StringComparison.Ordinal)
            && !sidecarStoreSource.Contains("assetManager.AllAssets.Remove(location)", StringComparison.Ordinal)
            && sidecarStoreSource.Contains("captured.TryAdd(location, asset)", StringComparison.Ordinal)
            && assetDiscoveryPatchSource.Contains("AddExternalAssets", StringComparison.Ordinal)
            && assetDiscoveryPatchSource.Contains("AfterExternalAssetsDiscovered", StringComparison.Ordinal)
            && assetDiscoveryPatchSource.Contains("nameof(CompositeTexture.Bake)", StringComparison.Ordinal)
            && assetDiscoveryPatchSource.Contains("WrapAtlasBakeCalls", StringComparison.Ordinal)
            && assetDiscoveryPatchSource.Contains("protected callsites=3", StringComparison.Ordinal)
            && textureVariantSanitizerSource.Contains("SanitizeBakedRoot", StringComparison.Ordinal)
            && textureVariantSanitizerSource.Contains("composite!.Baked = baked", StringComparison.Ordinal)
            && textureVariantSanitizerSource.Contains("BakedVariants", StringComparison.Ordinal)
            && textureVariantSanitizerSource.Contains("BakedTiles", StringComparison.Ordinal)
            && runtimeSource.Contains("sidecarAssets.NormalLocations", StringComparison.Ordinal)
            && runtimeSource.Contains("sidecarAssets.TryGet(location", StringComparison.Ordinal),
            "PBR sidecars can enter vanilla albedo discovery, survive wildcard baking, be hidden from other mods, or become unavailable to VintageRTX");
        Assert(PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone_n.png"))
            && PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone_r.png"))
            && PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone_m.png"))
            && PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone_e.png"))
            && !PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone.png"))
            && !PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone_normal.png"))
            && !PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "shapes/block/stone_n.png")),
            "exact _n/_r/_m/_e sidecar suffix filtering changed");
        Assert(!runtimeHarnessSource.Contains("--addOrigin", StringComparison.Ordinal),
            "runtime harness must load the packaged client mod, not remount its assets as a global server origin");
        Assert(runtimeSource.Contains("ambiguous rectangles={2}", StringComparison.Ordinal)
            && runtimeSource.Contains("TextureFilenames is { Length: 1 }", StringComparison.Ordinal)
            && runtimeSource.Contains("AllowDeclaredBaseFallback", StringComparison.Ordinal)
            && runtimeSource.Contains("ownersByPosition", StringComparison.Ordinal)
            && runtimeSource.Contains("ambiguousAtlasSources", StringComparison.Ordinal)
            && !runtimeSource.Contains("foreach (AssetLocation filename in current.TextureFilenames)", StringComparison.Ordinal),
            "PBR atlas mapping can still use composite overlays or last-writer-wins rectangles");
        Assert(!File.Exists(Path.Combine(root, "src", "VintageRTX", "Rendering", "PbrAtlasShaderSource.cs")), "generator shader source remains in runtime");

        string projectSource = File.ReadAllText(Path.Combine(root, "src", "VintageRTX", "VintageRTX.csproj"));
        Assert(projectSource.Contains("<Content Include=\"assets\\**\">", StringComparison.Ordinal)
            && !projectSource.Contains("DeployVintageRtxBasePbrArchive", StringComparison.Ordinal)
            && !projectSource.Contains("vintagertxbasepbr-1.0.0.vintagertxpbr", StringComparison.Ordinal)
            && !projectSource.Contains("generated\\pbr-packs\\base", StringComparison.Ordinal),
            "build must copy the normal Vintage Story asset tree without generating or mounting a private archive");

        string schemaPath = Path.Combine(root, "docs", "pbr-pack-manifest-v1.schema.json");
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        Assert(schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 1,
            "PBR manifest schema v1 is invalid");

        string schemaV2Path = Path.Combine(root, "docs", "pbr-pack-manifest-v2.schema.json");
        using JsonDocument schemaV2 = JsonDocument.Parse(File.ReadAllBytes(schemaV2Path));
        Assert(schemaV2.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 2,
            "PBR manifest schema v2 is invalid");
        JsonElement textureProperties = schemaV2.RootElement
            .GetProperty("properties")
            .GetProperty("textures")
            .GetProperty("items")
            .GetProperty("properties");
        Assert(textureProperties.TryGetProperty("metallic", out _)
            && textureProperties.TryGetProperty("emissive", out _),
            "PBR manifest v2 material maps are missing");

        string schemaV3Path = Path.Combine(root, "docs", "pbr-pack-manifest-v3.schema.json");
        using JsonDocument schemaV3 = JsonDocument.Parse(File.ReadAllBytes(schemaV3Path));
        Assert(schemaV3.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 3,
            "PBR manifest schema v3 is invalid");
        Assert(schemaV3.RootElement.GetProperty("description").GetString()!.Contains("native sidecar", StringComparison.Ordinal),
            "PBR manifest v3 must declare source-domain adjacent sidecars");

        string schemaV4Path = Path.Combine(root, "docs", "pbr-pack-manifest-v4.schema.json");
        using JsonDocument schemaV4 = JsonDocument.Parse(File.ReadAllBytes(schemaV4Path));
        Assert(schemaV4.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 4,
            "PBR manifest schema v4 is invalid");
        Assert(schemaV4.RootElement.GetProperty("required").EnumerateArray()
                .Any(item => item.GetString() == "defaultProvenance"),
            "PBR manifest v4 must make material provenance explicit");
    }

    /// <summary>
    /// Verifies the embedded Base Pbr Assets regression contract against deterministic fixture data.
    /// </summary>
    private static void TestEmbeddedBasePbrAssets()
    {
        string root = TestPaths.FindRepositoryRoot();
        string sourceTextureRoot = Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "game",
            "textures");
        Assert(Directory.Exists(sourceTextureRoot), "source PBR sidecar tree is missing");
        string[] sourceSidecars = Directory.GetFiles(
            sourceTextureRoot,
            "*.png",
            SearchOption.AllDirectories);
        Assert(sourceSidecars.All(path => path.EndsWith("_n.png", StringComparison.Ordinal)
                || path.EndsWith("_r.png", StringComparison.Ordinal)
                || path.EndsWith("_m.png", StringComparison.Ordinal)
                || path.EndsWith("_e.png", StringComparison.Ordinal)),
            "source PBR sidecar tree contains a copied albedo");

        string manifestPath = Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "vintagertxbasepbr",
            "config",
            "vintagertx",
            "pbr-manifest.json");
        Assert(File.Exists(manifestPath), "embedded base PBR manifest is missing");
        PbrManifest? manifest = JsonSerializer.Deserialize<PbrManifest>(
            File.ReadAllBytes(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(manifest?.SchemaVersion == 4
                && manifest.DefaultProvenance == "generated"
                && manifest.Textures.Count == 9_582,
            "embedded PBR manifest ledger mismatch");
        int expectedTextureCount = manifest!.Textures.Count;
        Assert(sourceSidecars.Length == expectedTextureCount * 4,
            "source PBR sidecar count does not match the manifest");
        foreach (string suffix in new[] { "_n.png", "_r.png", "_m.png", "_e.png" })
        {
            Assert(sourceSidecars.Count(path => path.EndsWith(suffix, StringComparison.Ordinal)) == expectedTextureCount,
                $"source asset tree {suffix} count mismatch");
        }

        HashSet<string> actualSidecars = sourceSidecars
            .Select(path => "textures/" + Path.GetRelativePath(sourceTextureRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> indexedSidecars = manifest.Textures
            .SelectMany(texture => new[]
            {
                texture.Normal.Asset,
                texture.Roughness.Asset,
                texture.Metallic.Asset,
                texture.Emissive.Asset
            })
            .Select(asset => asset.StartsWith("game:", StringComparison.Ordinal)
                ? asset["game:".Length..]
                : asset)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(actualSidecars.SetEquals(indexedSidecars),
            "source PBR sidecar tree and manifest asset index differ");

        using JsonDocument manifestDocument = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement manifestRoot = manifestDocument.RootElement;
        Assert(manifestRoot.GetProperty("generator").GetProperty("version").GetString()
                == "vintagertx-pbr-v6",
            "embedded PBR generator revision is stale");
        JsonElement sourceMod = manifestRoot.GetProperty("sourceMods")[0];
        Assert(sourceMod.GetProperty("modId").GetString() == "game"
                && sourceMod.GetProperty("version").GetString() == "1.22.7",
            "embedded PBR source installation identity is stale");
        Assert(manifest.Textures.All(texture => texture.Source.Domain == "game"),
            "embedded vanilla PBR assets must resolve to the logical game domain");
        string[] origins = manifestRoot.GetProperty("textures")
            .EnumerateArray()
            .Select(texture => texture.GetProperty("source").GetProperty("origin").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(origin => origin, StringComparer.Ordinal)
            .ToArray();
        Assert(origins.SequenceEqual(["creative", "game", "survival"]),
            "embedded PBR manifest does not cover all three vanilla physical origins");

#if DEBUG
        const string buildConfiguration = "Debug";
#else
        const string buildConfiguration = "Release";
#endif
        string deployedRoot = Path.Combine(
            root,
            "src",
            "VintageRTX",
            "bin",
            buildConfiguration,
            "Mods",
            "vintagertx");
        string deployedManifestPath = Path.Combine(
            deployedRoot,
            "assets",
            "vintagertxbasepbr",
            "config",
            "vintagertx",
            "pbr-manifest.json");
        Assert(File.Exists(deployedManifestPath),
            $"{buildConfiguration} runtime PBR manifest is missing");
        Assert(File.ReadAllBytes(manifestPath).SequenceEqual(File.ReadAllBytes(deployedManifestPath)),
            $"{buildConfiguration} runtime PBR manifest is stale");

        string projectFile = File.ReadAllText(Path.Combine(root, "src", "VintageRTX", "VintageRTX.csproj"));
        Assert(projectFile.Contains("assets\\game\\textures\\**\\*_n.png", StringComparison.Ordinal)
                && projectFile.Contains("<CopyToOutputDirectory>Always</CopyToOutputDirectory>", StringComparison.Ordinal),
            "generated PBR sidecars must always replace timestamp-normalized runtime copies");

        foreach (PbrManifestTexture entry in new[] { manifest!.Textures[0], manifest.Textures[^1] })
        {
            AssertEmbeddedMap(entry.Normal);
            AssertEmbeddedMap(entry.Roughness);
            AssertEmbeddedMap(entry.Metallic);
            AssertEmbeddedMap(entry.Emissive);
        }

        PbrManifestTexture representativeNormal = manifest.Textures.Single(entry =>
            entry.Source.Domain == "game"
            && entry.Source.Path == "textures/block/metal/anvil/iron.png");
        PbrManifestTexture polishedGranite = manifest.Textures.Single(entry =>
            entry.Source.Domain == "game"
            && entry.Source.Path == "textures/block/stone/polishedrock/granite.png");
        PbrManifestTexture graniteBrick = manifest.Textures.Single(entry =>
            entry.Source.Domain == "game"
            && entry.Source.Path == "textures/block/stone/brick/granite1.png");
        foreach (PbrManifestTexture entry in new[]
                 {
                     manifest.Textures[0],
                     representativeNormal,
                     polishedGranite,
                     graniteBrick,
                     manifest.Textures[^1],
                 })
        {
            AssertDeployedMap(entry.Normal);
            AssertDeployedMap(entry.Roughness);
            AssertDeployedMap(entry.Metallic);
            AssertDeployedMap(entry.Emissive);
        }

        string representativeNormalPath = EmbeddedMapPath(representativeNormal.Normal.Asset);
        byte[] representativeNormalBytes = File.ReadAllBytes(representativeNormalPath);
        using (SKBitmap normalBitmap = SKBitmap.Decode(representativeNormalBytes)
            ?? throw new InvalidDataException("representative generated normal could not be decoded"))
        {
            List<double> visibleTilts = [];
            foreach (SKColor pixel in normalBitmap.Pixels)
            {
                if (pixel.Alpha == 0)
                {
                    continue;
                }

                double x = pixel.Red / 255.0 * 2.0 - 1.0;
                double y = pixel.Green / 255.0 * 2.0 - 1.0;
                double z = pixel.Blue / 255.0 * 2.0 - 1.0;
                double visibleSlope = Math.Sqrt(x * x + y * y);
                visibleTilts.Add(Math.Atan2(visibleSlope, z) * 180.0 / Math.PI);
            }

            visibleTilts.Sort();
            int p90Index = (int)Math.Round(
                (visibleTilts.Count - 1) * 0.90,
                MidpointRounding.AwayFromZero);
            Assert(visibleTilts.Count > 0 && visibleTilts[p90Index] >= 3.0,
                $"representative file-backed normal remains visually flat after the shader response "
                + $"(p90={visibleTilts[p90Index]:F3} degrees)");
        }

        void AssertEmbeddedMap(PbrMapReference map)
        {
            string path = EmbeddedMapPath(map.Asset);
            Assert(File.Exists(path), $"embedded asset is missing: {map.Asset}");
            byte[] bytes = File.ReadAllBytes(path);
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert(string.Equals(actual, map.Sha256, StringComparison.OrdinalIgnoreCase),
                $"embedded asset checksum mismatch for {map.Asset}");
        }

        void AssertDeployedMap(PbrMapReference map)
        {
            string sourcePath = EmbeddedMapPath(map.Asset);
            string deployedPath = DeployedMapPath(map.Asset);
            Assert(File.Exists(deployedPath), $"runtime PBR asset is missing: {map.Asset}");
            Assert(File.ReadAllBytes(sourcePath).SequenceEqual(File.ReadAllBytes(deployedPath)),
                $"runtime PBR asset is stale: {map.Asset}");
        }

        string EmbeddedMapPath(string asset)
        {
            const string prefix = "game:";
            Assert(asset.StartsWith(prefix, StringComparison.Ordinal),
                $"base sidecar is not in the game asset domain: {asset}");
            return Path.Combine(
                root,
                "src",
                "VintageRTX",
                "assets",
                "game",
                asset[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        }


        string DeployedMapPath(string asset)
        {
            const string prefix = "game:";
            Assert(asset.StartsWith(prefix, StringComparison.Ordinal),
                $"base sidecar is not in the game asset domain: {asset}");
            return Path.Combine(
                deployedRoot,
                "assets",
                "game",
                asset[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        }
    }

    /// <summary>
    /// Verifies the pbr Deferred Transport regression contract against deterministic fixture data.
    /// </summary>
    private static void TestPbrDeferredTransport()
    {
        string root = TestPaths.FindRepositoryRoot();
        string terrainBridge = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "Rendering",
            "PbrTerrainRenderer.cs"));
        string chunkShader = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "game",
            "shaders",
            "chunkopaque.fsh"));
        string displayShader = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "shaders",
            "display.frag"));

        Assert(terrainBridge.Contains("BuildConsolidatedPbrPixels", StringComparison.Ordinal)
            && terrainBridge.Contains("pixels[offset] = normalX", StringComparison.Ordinal)
            && terrainBridge.Contains("pixels[offset + 1] = normalY", StringComparison.Ordinal)
            && terrainBridge.Contains("pixels[offset + 2] = roughness?.GetPixel(x, y).Red", StringComparison.Ordinal)
            && terrainBridge.Contains("pixels[offset + 3] = PackMaterialBits(", StringComparison.Ordinal)
            && terrainBridge.Contains("GeneratedFallbackMaximumSlope", StringComparison.Ordinal)
            && terrainBridge.Contains("maximumTangentSlope: null", StringComparison.Ordinal),
            "consolidated normal XY, roughness B or material-bit A upload channels changed");

        int desktopUnit = PbrTerrainRenderer.SelectPbrTextureUnit(32);
        int minimumUnit = PbrTerrainRenderer.SelectPbrTextureUnit(16);
        Assert(desktopUnit == 31
            && minimumUnit == 14
            && desktopUnit is not 15 and not 16
            && minimumUnit is not 15 and not 16,
            "consolidated PBR terrain sampler can collide with the observed unit-15/16 bindings");

        Assert(chunkShader.Contains("vec4 pbrSurface = texture(vintagertxMaterialTex, uv)", StringComparison.Ordinal)
            && chunkShader.Contains("texelFetch(vintagertxMaterialTex, pbrTexel, 0).a", StringComparison.Ordinal)
            && chunkShader.Contains("vintagertxPerturbNormal(normal, pbrSurface.rg)", StringComparison.Ordinal)
            && chunkShader.Contains("float tangentZ = sqrt(max(1.0 - dot(tangentXY, tangentXY)", StringComparison.Ordinal)
            && chunkShader.Contains("pbrSurface.b", StringComparison.Ordinal)
            && chunkShader.Contains("int materialBits = pbrMaterialBits", StringComparison.Ordinal)
            && !chunkShader.Contains("vintagertxNormalTex", StringComparison.Ordinal),
            "terrain shader does not decode every PBR sidecar from its single proven sampler");
        Assert(chunkShader.Contains("const float VintagertxNormalStrength = 1.0", StringComparison.Ordinal)
            && chunkShader.Contains("tangentNormal.xy *= VintagertxNormalStrength", StringComparison.Ordinal)
            && !chunkShader.Contains("uniform float vintagertxNormalStrength", StringComparison.Ordinal)
            && !terrainBridge.Contains("\"vintagertxNormalStrength\"", StringComparison.Ordinal),
            "normal-map visibility can still collapse to an unset per-draw strength uniform");
        Assert(chunkShader.Contains("float uvDeterminant = uvDx.x * uvDy.y - uvDx.y * uvDy.x", StringComparison.Ordinal)
            && chunkShader.Contains("float uvOrientation = uvDeterminant < 0.0 ? -1.0 : 1.0", StringComparison.Ordinal)
            && chunkShader.Contains("tangentRaw -= normalWorld * dot(normalWorld, tangentRaw)", StringComparison.Ordinal)
            && chunkShader.Contains("float handedness = dot(cross(normalWorld, tangent), bitangentRaw) < 0.0 ? -1.0 : 1.0", StringComparison.Ordinal)
            && chunkShader.Contains("mat3(tangent, bitangent, normalWorld) * tangentNormal", StringComparison.Ordinal)
            && !chunkShader.Contains("max(max(dot(tangent, tangent), dot(bitangent, bitangent)), 0.000001)", StringComparison.Ordinal),
            "normal-map TBN must normalize atlas-scale derivatives and preserve mirrored-UV handedness");

        (System.Numerics.Vector3 tangent, System.Numerics.Vector3 bitangent, System.Numerics.Vector3 normal) =
            BuildReferenceTangentBasis(mirroredU: false);
        Assert(Math.Abs(tangent.Length() - 1.0f) < 1e-5f
            && Math.Abs(bitangent.Length() - 1.0f) < 1e-5f
            && Math.Abs(System.Numerics.Vector3.Dot(tangent, bitangent)) < 1e-5f
            && Math.Abs(System.Numerics.Vector3.Dot(tangent, normal)) < 1e-5f,
            "atlas-scale UV derivatives do not produce an orthonormal reference TBN");
        (System.Numerics.Vector3 mirroredTangent, System.Numerics.Vector3 mirroredBitangent, System.Numerics.Vector3 mirroredNormal) =
            BuildReferenceTangentBasis(mirroredU: true);
        Assert(mirroredTangent.X < -0.999f
            && mirroredBitangent.Y > 0.999f
            && System.Numerics.Vector3.Dot(
                System.Numerics.Vector3.Cross(mirroredNormal, mirroredTangent),
                mirroredBitangent) < -0.999f,
            "mirrored atlas UV handedness is not preserved by the reference TBN");

        System.Numerics.Vector3 tangentNormal = System.Numerics.Vector3.Normalize(new(0.35f, -0.20f, 0.91515f));
        System.Numerics.Vector3 perturbed = System.Numerics.Vector3.Normalize(
            tangent * tangentNormal.X + bitangent * tangentNormal.Y + normal * tangentNormal.Z);
        double visibleTiltDegrees = Math.Acos(Math.Clamp(
            System.Numerics.Vector3.Dot(perturbed, normal),
            -1.0f,
            1.0f)) * 180.0 / Math.PI;
        Assert(visibleTiltDegrees > 20.0,
            "an authored tangent normal is still flattened by atlas-scale UV derivatives");
        Assert(chunkShader.Contains("outGNormal = vec4(", StringComparison.Ordinal)
            && chunkShader.Contains("vec4(shadingNormal, 0)", StringComparison.Ordinal)
            && chunkShader.Contains("roughnessBits * 32 + albedoBits", StringComparison.Ordinal)
            && chunkShader.Contains("int materialBits = pbrMaterialBits", StringComparison.Ordinal)
            && chunkShader.Contains("outGlow = vec4(", StringComparison.Ordinal),
            "terrain shader does not write perturbed normal/roughness/material payloads");

        Assert(displayShader.Contains("bool deferredGeometryRequired = voxelLightingEnabled != 0", StringComparison.Ordinal)
            && displayShader.Contains("if (deferredGeometryRequired)", StringComparison.Ordinal)
            && displayShader.Contains("position = readGBufferTexel(gPosition, uv).xyz", StringComparison.Ordinal)
            && displayShader.Contains("encodedNormalRoughness = readGBufferTexel(gNormal, uv)", StringComparison.Ordinal)
            && displayShader.Contains("if (debugView == 0 && !hasGeometry)", StringComparison.Ordinal),
            "performance-tier voxel lighting can bypass the terrain G-buffer and all PBR maps");
        Assert(displayShader.Contains("vec4 deferredMaterial = texelFetch(gMaterial", StringComparison.Ordinal)
            && displayShader.Contains("float packedSurfaceValue = abs(packedSurfaceAlpha)", StringComparison.Ordinal)
            && displayShader.Contains("packedSurfaceValue * 1025.0 - 1.0", StringComparison.Ordinal)
            && displayShader.Contains("float pbrPayloadPresent = (packedMaterial & 32) != 0 ? 1.0 : 0.0", StringComparison.Ordinal)
            && displayShader.Contains("float authoredMetallic = float(packedMaterial & 3) / 3.0", StringComparison.Ordinal)
            && displayShader.Contains("float authoredEmissive = float((packedMaterial >> 2) & 7) / 7.0", StringComparison.Ordinal),
            "deferred PBR payload decoding changed channels or filtering contract");
        Assert(displayShader.Contains("vec3 earlyWorldNormal = normalize(", StringComparison.Ordinal)
            && displayShader.Contains("worldNormal = earlyWorldNormal", StringComparison.Ordinal)
            && displayShader.Contains("traceVoxelPointLight(", StringComparison.Ordinal)
            && displayShader.Contains("evaluateDirectSpecular(", StringComparison.Ordinal)
            && displayShader.Contains("float metallic = mix(voxelMetallic, authoredMetallic, materialMapPresent)", StringComparison.Ordinal)
            && displayShader.Contains("max(authoredEmission, engineEmission)", StringComparison.Ordinal)
            && displayShader.Contains("+ emittedRadiance", StringComparison.Ordinal),
            "decoded normals or material maps do not influence final lighting energy");
        Assert(displayShader.Contains("materialFresnel(baseReflectance, normalView)", StringComparison.Ordinal)
            && displayShader.Contains("float reflectionAddWeight = 1.0", StringComparison.Ordinal)
            && displayShader.Contains("rawAlbedoMatchesSurface", StringComparison.Ordinal)
            && displayShader.Contains("* fresnelReflectance", StringComparison.Ordinal)
            && !displayShader.Contains("reflectedRadiance += localEnvironmentSpecular", StringComparison.Ordinal)
            && !displayShader.Contains("metallic * 3.00", StringComparison.Ordinal),
            "specular transport must preserve material reflectance without inventing unresolved reflection energy");

        for (int roughness = 0; roughness < 32; roughness++)
        {
            for (int albedo = 0; albedo < 32; albedo++)
            {
                double encoded = (1 + roughness * 32 + albedo) / 1025.0;
                bool available = encoded >= 0.0005 && encoded < 0.9995;
                int payload = Math.Clamp(
                    (int)Math.Floor(encoded * 1025.0 - 1.0 + 0.5),
                    0,
                    1023);
                Assert(available
                    && payload / 32 == roughness
                    && payload % 32 == albedo,
                    "10-bit roughness/albedo payload does not round-trip");
            }
        }

        for (int metallic = 0; metallic < 4; metallic++)
        {
            for (int emissive = 0; emissive < 8; emissive++)
            {
                int encodedBits = metallic | (emissive << 2) | 32 | 64;
                double encoded = encodedBits / 255.0;
                int decodedBits = (int)Math.Floor(encoded * 255.0 + 0.5);
                Assert((decodedBits & 3) == metallic
                    && ((decodedBits >> 2) & 7) == emissive
                    && (decodedBits & 32) != 0
                    && (decodedBits & 64) != 0,
                    "metallic/emissive/PBR-presence/override payload does not round-trip");
            }
        }
    }

    /// <summary>
    /// Executes the build Reference Tangent Basis step used by the deterministic preflight Suite fixture.
    /// </summary>
    /// <param name="mirroredU">The mirrored U input used to configure this deterministic test path.</param>
    /// <returns>The build Reference Tangent Basis result consumed by the caller&apos;s assertion.</returns>
    private static (
        System.Numerics.Vector3 Tangent,
        System.Numerics.Vector3 Bitangent,
        System.Numerics.Vector3 Normal) BuildReferenceTangentBasis(bool mirroredU)
    {
        const float atlasUvDerivative = 1.0f / 25_600.0f;
        System.Numerics.Vector3 positionDx = new(0.01f, 0.0f, 0.0f);
        System.Numerics.Vector3 positionDy = new(0.0f, 0.01f, 0.0f);
        System.Numerics.Vector2 uvDx = new(mirroredU ? -atlasUvDerivative : atlasUvDerivative, 0.0f);
        System.Numerics.Vector2 uvDy = new(0.0f, atlasUvDerivative);
        System.Numerics.Vector3 normal = System.Numerics.Vector3.UnitZ;
        float determinant = uvDx.X * uvDy.Y - uvDx.Y * uvDy.X;
        float orientation = determinant < 0.0f ? -1.0f : 1.0f;
        System.Numerics.Vector3 tangentRaw =
            (positionDx * uvDy.Y - positionDy * uvDx.Y) * orientation;
        System.Numerics.Vector3 bitangentRaw =
            (positionDy * uvDx.X - positionDx * uvDy.X) * orientation;
        tangentRaw -= normal * System.Numerics.Vector3.Dot(normal, tangentRaw);
        System.Numerics.Vector3 tangent = System.Numerics.Vector3.Normalize(tangentRaw);
        float handedness = System.Numerics.Vector3.Dot(
            System.Numerics.Vector3.Cross(normal, tangent),
            bitangentRaw) < 0.0f
            ? -1.0f
            : 1.0f;
        System.Numerics.Vector3 bitangent =
            System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(normal, tangent)) * handedness;
        return (tangent, bitangent, normal);
    }

    /// <summary>
    /// Verifies the pbr Unpremultiplied Decode regression contract against deterministic fixture data.
    /// </summary>
    private static void TestPbrUnpremultipliedDecode()
    {
        byte[] normalPng = EncodeUnpremultipliedPixel(new SKColor(200, 100, 50, 64));
        byte[] roughnessPng = EncodeUnpremultipliedPixel(new SKColor(120, 0, 0, 32));
        byte[] consolidated = PbrTerrainRenderer.BuildConsolidatedPbrPixels(
            normalPng,
            roughnessPng,
            EncodeUnpremultipliedPixel(new SKColor(180, 0, 0, 48)),
            EncodeUnpremultipliedPixel(new SKColor(90, 0, 0, 16)),
            1,
            1);
        Assert(
            consolidated.SequenceEqual(new byte[]
            {
                200,
                100,
                120,
                PbrTerrainRenderer.PackMaterialBits(180, 90, true, true)
            }),
            "consolidated PBR channels were premultiplied or packed incorrectly");
    }

    /// <summary>
    /// Executes the encode Unpremultiplied Pixel step used by the deterministic preflight Suite fixture.
    /// </summary>
    /// <param name="color">The color input used to configure this deterministic test path.</param>
    /// <returns>The encode Unpremultiplied Pixel result consumed by the caller&apos;s assertion.</returns>
    private static byte[] EncodeUnpremultipliedPixel(SKColor color)
    {
        using SKBitmap bitmap = new(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.SetPixel(0, 0, color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Verifies the pbr Atlas Mapping regression contract against deterministic fixture data.
    /// </summary>
    private static void TestPbrAtlasMapping()
    {
        TextureAtlasPosition[] positions = Enumerable.Range(0, 12)
            .Select(index => new TextureAtlasPosition
            {
                atlasTextureId = 404,
                atlasNumber = 0,
                x1 = index / 16f,
                y1 = 0.0f,
                x2 = (index + 1) / 16f,
                y2 = 1.0f / 16f
            })
            .ToArray();

        AssetLocation baseA = new("game", "textures/block/a.png");
        AssetLocation variantA = new("othermod", "textures/block/a-variant.png");
        AssetLocation baseB = new("game", "textures/block/b.png");
        AssetLocation overlayBase = new("game", "textures/block/composite-base.png");
        AssetLocation overlay = new("game", "textures/block/composite-overlay.png");
        AssetLocation collisionA = new("game", "textures/block/collision-a.png");
        AssetLocation collisionB = new("othermod", "textures/block/collision-b.png");

        CompositeTexture textureA = new(baseA)
        {
            Baked = new BakedCompositeTexture
            {
                TextureSubId = 7,
                TextureFilenames = [baseA],
                BakedVariants =
                [
                    new BakedCompositeTexture
                    {
                        TextureSubId = 2,
                        TextureFilenames = [variantA]
                    }
                ]
            }
        };
        CompositeTexture textureB = new(baseB)
        {
            Baked = new BakedCompositeTexture
            {
                TextureSubId = 11,
                TextureFilenames = [baseB]
            }
        };
        CompositeTexture composite = new(overlayBase)
        {
            Baked = new BakedCompositeTexture
            {
                TextureSubId = 5,
                TextureFilenames = [overlayBase, overlay]
            }
        };
        CompositeTexture collidingA = new(collisionA)
        {
            Baked = new BakedCompositeTexture
            {
                TextureSubId = 9,
                TextureFilenames = [collisionA]
            }
        };
        CompositeTexture collidingB = new(collisionB)
        {
            Baked = new BakedCompositeTexture
            {
                TextureSubId = 9,
                TextureFilenames = [collisionB]
            }
        };

        CompositeTexture[] firstOrder =
        [
            textureA,
            collidingB,
            composite,
            textureB,
            collidingA
        ];
        CompositeTexture[] permutedOrder =
        [
            collidingA,
            textureB,
            textureA,
            composite,
            collidingB
        ];
        PbrAtlasLookup first = PbrTerrainRenderer.BuildSourceAtlasPositionLookupForTextures(
            firstOrder,
            positions,
            position => position.atlasTextureId == 404 && position.atlasNumber == 0);
        PbrAtlasLookup permuted = PbrTerrainRenderer.BuildSourceAtlasPositionLookupForTextures(
            permutedOrder,
            positions,
            position => position.atlasTextureId == 404 && position.atlasNumber == 0);

        Assert(first.ExactPlacementCount == 3, "non-contiguous exact atlas indices should all survive");
        Assert(first.AmbiguousRectangleCount == 1, "shared atlas rectangle should be marked ambiguous once");
        Assert(first.SkippedSourceLinkCount == 2, "both owners of an ambiguous rectangle must be skipped");
        Assert(first.SkippedCompositeRectangleCount == 1, "base-plus-overlay composite must be skipped");
        Assert(first.Positions.Count == 3, "only exact base/variant sources should remain mapped");
        Assert(first.AmbiguousSources.SetEquals([collisionA, collisionB]), "ambiguous source identities should be retained");
        Assert(!first.Positions.ContainsKey(overlayBase)
            && !first.Positions.ContainsKey(overlay)
            && !first.Positions.ContainsKey(collisionA)
            && !first.Positions.ContainsKey(collisionB),
            "composite or ambiguous sources must never reach an atlas upload");

        AssertMappedTo(first, baseA, positions[7]);
        AssertMappedTo(first, variantA, positions[2]);
        AssertMappedTo(first, baseB, positions[11]);
        AssertMappedTo(permuted, baseA, positions[7]);
        AssertMappedTo(permuted, variantA, positions[2]);
        AssertMappedTo(permuted, baseB, positions[11]);

        // Simulate manifest/sidecar iteration in two unrelated orders. Lookup
        // is by canonical source identity, never by list index or insertion order.
        foreach (AssetLocation source in new[] { baseB, variantA, baseA })
        {
            Assert(first.Positions[source].Length == 1, "permuted sidecar source should keep one exact rectangle");
        }
        foreach (AssetLocation source in new[] { baseA, baseB, variantA })
        {
            Assert(permuted.Positions[source].Length == 1, "permuted domain/source order changed the atlas mapping");
        }

        string unsafeRuntimeCounts =
            "exact placements=3, ambiguous rectangles=1, skipped ambiguous links=2, skipped composite rectangles=1\n"
            + "manifest overrides=4";
        Assert(
            RuntimeLogValidator.Validate(
                unsafeRuntimeCounts,
                ScenarioCatalog.Get("reference-room"))
                .Any(failure => failure.Contains("exceed 3 exact atlas placements", StringComparison.Ordinal)),
            "runtime validation must reject more uploads than exact atlas placements");

        static void AssertMappedTo(
            PbrAtlasLookup lookup,
            AssetLocation source,
            TextureAtlasPosition expected)
        {
            Assert(lookup.Positions.TryGetValue(source, out TextureAtlasPosition[]? mapped)
                && mapped.Length == 1
                && ReferenceEquals(mapped[0], expected),
                $"{source} mapped to the wrong atlas rectangle");
        }
    }

    /// <summary>
    /// Verifies the pbr Asset References regression contract against deterministic fixture data.
    /// </summary>
    private static void TestPbrAssetReferences()
    {
        Assert(PbrTerrainRenderer.TryParseAssetReference(
            "othermod:textures/block/stone_n.png",
            "pack",
            out AssetLocation colonLocation), "colon AssetLocation should parse");
        Assert(colonLocation.Domain == "othermod", "colon domain");
        Assert(colonLocation.Path == "textures/block/stone_n.png", "colon path");

        Assert(PbrTerrainRenderer.TryParseAssetReference(
            "game/textures/block/stone.png",
            "pack",
            out AssetLocation slashLocation), "domain/path source should parse");
        Assert(slashLocation.Domain == "game", "source domain");
        Assert(!PbrTerrainRenderer.TryParseAssetReference("stone.png", "pack", out _), "ambiguous path must fail");
    }

    /// <summary>
    /// Verifies the temporal Artifact Detection regression contract against deterministic fixture data.
    /// </summary>
    private static void TestTemporalArtifactDetection()
    {
        using SKBitmap checker = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap flat = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap fireflies = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap texturedBefore = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap texturedAfter = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap thinLeak = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap reflectedOutline = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap reflectedSurface = new(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap reflectedSilhouette = new(96, 128, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap fragmentedSilhouette = new(96, 128, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap waterReceiver = new(96, 128, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < checker.Height; y++)
        {
            for (int x = 0; x < checker.Width; x++)
            {
                byte value = ((x + y) & 1) == 0 ? (byte)190 : (byte)70;
                checker.SetPixel(x, y, new SKColor(value, value, value));
                flat.SetPixel(x, y, new SKColor(120, 120, 120));
                fireflies.SetPixel(x, y, new SKColor(120, 120, 120));
                texturedBefore.SetPixel(x, y, new SKColor(120, 120, 120));
                texturedAfter.SetPixel(x, y, new SKColor(120, 120, 120));
                thinLeak.SetPixel(x, y, new SKColor(35, 35, 35));
                reflectedOutline.SetPixel(x, y, SKColors.Black);
                reflectedSurface.SetPixel(x, y, SKColors.Black);
            }
        }
        for (int y = 2; y < fireflies.Height - 1; y += 4)
        {
            for (int x = 2; x < fireflies.Width - 1; x += 4)
            {
                fireflies.SetPixel(x, y, new SKColor(245, 245, 245));
            }
        }
        // The centre receives a large relighting delta but remains darker
        // than the neighbouring albedo texel. It is not a displayed point
        // highlight and must not make the firefly metric scene-phase dependent.
        texturedBefore.SetPixel(16, 16, new SKColor(20, 20, 20));
        texturedAfter.SetPixel(16, 16, new SKColor(100, 100, 100));
        texturedBefore.SetPixel(17, 16, new SKColor(180, 180, 180));
        texturedAfter.SetPixel(17, 16, new SKColor(185, 185, 185));
        for (int x = 1; x < thinLeak.Width - 1; x++)
        {
            thinLeak.SetPixel(x, thinLeak.Height / 2, new SKColor(120, 120, 120));
        }
        for (int coordinate = 4; coordinate <= 27; coordinate++)
        {
            reflectedOutline.SetPixel(coordinate, 4, new SKColor(90, 140, 210));
            reflectedOutline.SetPixel(coordinate, 27, new SKColor(90, 140, 210));
            reflectedOutline.SetPixel(4, coordinate, new SKColor(90, 140, 210));
            reflectedOutline.SetPixel(27, coordinate, new SKColor(90, 140, 210));
        }
        for (int y = 4; y <= 27; y++)
        {
            for (int x = 4; x <= 27; x++)
            {
                reflectedSurface.SetPixel(x, y, new SKColor(90, 140, 210));
            }
        }
        reflectedSilhouette.Erase(new SKColor(150, 185, 235));
        fragmentedSilhouette.Erase(new SKColor(150, 185, 235));
        waterReceiver.Erase(SKColors.Black);
        for (int y = 24; y < waterReceiver.Height; y++)
        {
            for (int x = 0; x < waterReceiver.Width; x++)
            {
                waterReceiver.SetPixel(x, y, new SKColor(210, 130, 220));
            }
        }

        SKColor reflectedLeaf = new(82, 148, 38);
        for (int x = 12; x < 84; x++)
        {
            // Both fixtures contain exactly sixteen vegetation pixels per active
            // column. Only their topology differs: one solid silhouette versus
            // four separated horizontal strips.
            for (int y = 26; y < 42; y++)
            {
                reflectedSilhouette.SetPixel(x, y, reflectedLeaf);
            }
            for (int band = 0; band < 4; band++)
            {
                int bandStart = 26 + band * 7;
                for (int y = bandStart; y < bandStart + 4; y++)
                {
                    fragmentedSilhouette.SetPixel(x, y, reflectedLeaf);
                }
            }
        }

        Assert(
            RuntimeImageValidator.MeasureCheckerboardCorrelation(checker) > 0.95,
            "checkerboard detector must recognize a correlated interlace artifact");
        Assert(
            RuntimeImageValidator.MeasureCheckerboardCorrelation(flat, checker) > 0.95,
            "before/after checkerboard detector must recognize an effect-induced artifact");
        Assert(
            RuntimeImageValidator.MeasureCheckerboardCorrelation(flat) == 0.0,
            "checkerboard detector must ignore a flat converged frame");
        Assert(
            RuntimeImageValidator.MeasureCheckerboardCorrelation(flat, flat) == 0.0,
            "before/after checkerboard detector must ignore source texture structure");
        Assert(
            RuntimeImageValidator.MeasureIsolatedHighlightDensity(flat, fireflies) > 0.02,
            "firefly detector must recognize isolated bright transport pixels");
        Assert(
            RuntimeImageValidator.MeasureIsolatedHighlightDensity(flat, flat) == 0.0,
            "firefly detector must ignore a smooth converged frame");
        Assert(
            RuntimeImageValidator.MeasureIsolatedHighlightDensity(texturedBefore, texturedAfter) == 0.0,
            "firefly detector must ignore a relit texture delta that is not a displayed local maximum");
        Assert(
            RuntimeImageValidator.MeasureThinLeakDensity(flat, thinLeak) > 0.02,
            "thin-leak detector must recognize a preserved one-pixel seam");
        Assert(
            RuntimeImageValidator.MeasureThinLeakDensity(flat, flat) == 0.0,
            "thin-leak detector must ignore a smooth converged frame");
        Assert(
            RuntimeImageValidator.MeasureRadianceInteriorRatio(reflectedOutline) < 0.05,
            "reflection structure detector must reject a contour-only response");
        Assert(
            RuntimeImageValidator.MeasureRadianceInteriorRatio(reflectedSurface) > 0.75,
            "reflection structure detector must recognize filled reflected radiance");
        double continuousSilhouette = RuntimeImageValidator.MeasureReflectedSilhouetteContinuity(
            reflectedSilhouette,
            waterReceiver);
        double fragmentedBands = RuntimeImageValidator.MeasureReflectedSilhouetteContinuity(
            fragmentedSilhouette,
            waterReceiver);
        Assert(
            continuousSilhouette > 0.90,
            "reflection silhouette detector must recognize a continuous two-dimensional region");
        Assert(
            fragmentedBands < 0.35,
            "reflection silhouette detector must reject equal-area disjoint horizontal bands");
        Assert(
            continuousSilhouette - fragmentedBands > 0.55,
            "reflection silhouette topology must dominate equal pixel-count coverage");
    }

    /// <summary>
    /// Verifies the scenario Coverage regression contract against deterministic fixture data.
    /// </summary>
    private static void TestScenarioCoverage()
    {
        string[] required =
        [
            "render-lab",
            "render-lab-performance",
            "render-lab-balanced",
            "render-lab-quality",
            "render-lab-ultra",
            "render-lab-extreme",
            "render-lab-cinematic",
            "reference-room",
            "lantern-night",
            "held-light",
            "many-lights-stress",
            "exterior-roof",
            "sunrise-exterior",
            "vegetation-shadow-map",
            "moving-camera",
            "water-reflection",
            "rain-wetness",
            "cave-interior",
            "nonstandard-geometry",
            "third-party-pbr",
            "resize-and-reload"
        ];
        foreach (string name in required)
        {
            Assert(ScenarioCatalog.Get(name).Name == name, $"missing scenario {name}");
        }
        Assert(
            ScenarioCatalog.AutomatedScenarios.Select(static scenario => scenario.Name).SequenceEqual(required),
            "Test Explorer must expose every automated catalog scenario in declaration order");
        object[][] rows = InGameRealCaseTests.RepresentativeScenarios.ToArray();
        Assert(
            rows.Length == required.Length
                && rows.All(static row => row.Length == 1
                    && row[0] is string name
                    && !string.IsNullOrWhiteSpace(name)),
            "every InGame DynamicData row must contain one non-empty scenario name");
        Assert(
            rows.Select(static row => (string)row[0]).SequenceEqual(required),
            "MSTest DynamicData rows must mirror the automated catalog in declaration order");
        Assert(
            required.Distinct(StringComparer.OrdinalIgnoreCase).Count() == required.Length,
            "automated scenario names must be unique for unambiguous Test Explorer rows");

        Assert(ScenarioCatalog.Get("reference-room").WorldHour == 12.0, "reference room must run at fixed noon");
        Assert(
            ScenarioCatalog.Get("render-lab").UseIsolatedDataPath
            && ScenarioCatalog.Get("render-lab").WorldHour == 12.0,
            "render lab must keep its isolated noon reference");
        Assert(
            ScenarioCatalog.Get("render-lab-performance").UseIsolatedDataPath
            && ScenarioCatalog.Get("render-lab-performance").RunBenchmark
            && ScenarioCatalog.Get("render-lab-performance").RuntimeProbe == "render-lab"
            && ScenarioCatalog.Get("render-lab-performance").ValidateReflections
            && ScenarioCatalog.Get("render-lab-performance").ValidateVoxelReflections
            && ScenarioCatalog.Get("render-lab-performance").WorldSeed
                == ScenarioCatalog.RenderLabWorldSeed,
            "performance lab must reuse the isolated scene and gate reflections plus A/B/A timing");
        Dictionary<string, VintageRtxRenderProfile> renderLabProfiles = new(StringComparer.Ordinal)
        {
            ["render-lab-performance"] = VintageRtxRenderProfile.Performance,
            ["render-lab-balanced"] = VintageRtxRenderProfile.Balanced,
            ["render-lab-quality"] = VintageRtxRenderProfile.Quality,
            ["render-lab-ultra"] = VintageRtxRenderProfile.Ultra
        };
        foreach ((string scenarioName, VintageRtxRenderProfile profile) in renderLabProfiles)
        {
            ScenarioDefinition profileScenario = ScenarioCatalog.Get(scenarioName);
            VintageRtxConfig expectedProfile = new();
            expectedProfile.ApplyRenderProfile(profile);
            Assert(
                profileScenario.UseIsolatedDataPath
                    && profileScenario.RunBenchmark
                    && profileScenario.RuntimeProbe == "render-lab"
                    && profileScenario.RenderProfile == profile
                    && Math.Abs(profileScenario.MaximumGpuMilliseconds
                        - expectedProfile.GpuBudgetMilliseconds) < 0.001,
                $"{scenarioName} must archive and gate its exact authored hardware profile");
        }
        Dictionary<string, VintageRtxRenderProfile> visualRenderLabProfiles = new(StringComparer.Ordinal)
        {
            ["render-lab-extreme"] = VintageRtxRenderProfile.Extreme,
            ["render-lab-cinematic"] = VintageRtxRenderProfile.Cinematic
        };
        foreach ((string scenarioName, VintageRtxRenderProfile profile) in visualRenderLabProfiles)
        {
            ScenarioDefinition profileScenario = ScenarioCatalog.Get(scenarioName);
            Assert(
                profileScenario.UseIsolatedDataPath
                    && !profileScenario.RunBenchmark
                    && profileScenario.RuntimeProbe == "render-lab"
                    && profileScenario.RenderProfile == profile
                    && profileScenario.ValidateReflections
                    && profileScenario.ValidateVoxelReflections,
                $"{scenarioName} must validate native visual fidelity without an FPS gate");
        }
        Assert(
            ScenarioCatalog.Get("render-lab").RenderProfile == VintageRtxRenderProfile.Quality,
            "the visual RenderLab must capture the high-fidelity Quality profile rather than an adaptive Custom default");
        string captureSource = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "Rendering",
            "FrameCaptureService.cs"));
        Assert(
            captureSource.Contains("ReadyForBenchmark", StringComparison.Ordinal)
            && captureSource.Contains("automaticSequenceCompletedUtc.AddSeconds(2)", StringComparison.Ordinal),
            "render-lab benchmark must start only after diagnostic readback and encoding settle");
        Assert(
            ScenarioCatalog.Get("lantern-night") is
            {
                WorldHour: 0.0,
                RunBenchmark: false,
                RenderProfile: VintageRtxRenderProfile.Cinematic
            },
            "lantern visual validation must run at fixed midnight and maximum authored quality without an FPS gate");
        Assert(
            ScenarioCatalog.Get("held-light").ShadowValidation == ShadowValidation.CameraAligned,
            "held light must reject false camera-aligned self-occlusion");
        Assert(
            ScenarioCatalog.Get("many-lights-stress").ShadowValidation == ShadowValidation.Informational,
            "many-light stress must measure bounded cost independently of a fixed caster");
        Assert(ScenarioCatalog.Get("exterior-roof").Automated, "exterior roof scenario must be automated");
        Assert(
            ScenarioCatalog.Get("exterior-roof").ShadowValidation == ShadowValidation.SunProjected,
            "exterior roof must validate the sun-shadow channel");
        Assert(
            ScenarioCatalog.Get("sunrise-exterior").WorldHour == 9.0,
            "sunrise exterior must run at a fixed low-sun hour");
        Assert(
            ScenarioCatalog.Get("vegetation-shadow-map") is
            {
                World: "foggy village world",
                RuntimeProbe: "vegetation-shadow-map",
                RunBenchmark: false,
                CaptureProfile: "vegetation-shadow-map",
                ShadowValidation: ShadowValidation.SunProjected,
                RenderProfile: VintageRtxRenderProfile.Ultra
            },
            "real-map vegetation must be a capture-only fixed-Ultra sun-shadow case on foggy village");
        Assert(
            ScenarioCatalog.Get("moving-camera").RuntimeProbe == "moving-camera",
            "moving-camera scenario must exercise real camera history rejection");
        Assert(
            ScenarioCatalog.Get("water-reflection").ValidateReflections,
            "water scenario must require a visible SSR diagnostic channel");
        Assert(
            ScenarioCatalog.Get("water-reflection").ValidateVoxelReflections,
            "water scenario must require a visible off-screen voxel reflection channel");
        Assert(
            !ScenarioCatalog.Get("water-reflection").RunBenchmark
                && ScenarioCatalog.Get("water-reflection").RenderProfile
                    == VintageRtxRenderProfile.Cinematic,
            "water visual validation must use maximum authored quality without an FPS gate");
        Assert(
            ScenarioCatalog.Get("rain-wetness").ValidateWetness
                && ScenarioCatalog.Get("rain-wetness").ForcedPrecipitation == 1.0,
            "rain scenario must force precipitation and validate localized wetness");
        Assert(
            ScenarioCatalog.Get("cave-interior").ValidateVoxelBounce,
            "cave scenario must require visible off-screen voxel bounce");
        Assert(ScenarioCatalog.Get("resize-and-reload").Automated, "resize/reload scenario must be automated");
        Assert(
            ScenarioCatalog.Get("resize-and-reload").RuntimeProbe == "resize-and-reload",
            "resize/reload scenario must activate its public runtime probe");

        ScenarioDefinition completionScenario = ScenarioCatalog.Get("rain-wetness");
        string benchmarkOnly = string.Join('\n', completionScenario.RequiredLogTokens)
            + "\nStabilized A/B/A result";
        Assert(
            !RuntimeLogValidator.IsComplete(benchmarkOnly, completionScenario),
            "runtime harness must not stop before delayed diagnostic captures");
        Assert(
            RuntimeLogValidator.IsComplete(
                benchmarkOnly + "\nComparison capture saved: probe-wetness-before.png and probe-wetness-vintagertx.png",
                completionScenario),
            "runtime harness must complete after benchmark and final diagnostic channel");
        ScenarioDefinition waterCompletionScenario = ScenarioCatalog.Get("water-reflection");
        string waterBeforeImpactEvidence = string.Join(
            '\n',
            waterCompletionScenario.RequiredLogTokens.Where(static token =>
                !token.Contains("impact-1-reflection-source-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("impact-1-reflection-source-raw.png", StringComparison.Ordinal)
                && !token.Contains("projectile-stone-baseline-final-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-stone-baseline-earlier-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-stone-baseline-prior-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-stone-baseline-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-stone-final-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-stone-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-arrow-baseline-final-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-arrow-baseline-earlier-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-arrow-baseline-prior-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-arrow-baseline-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-arrow-final-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("projectile-arrow-surface-field-vintagertx.png", StringComparison.Ordinal)
                && !token.Contains("Automatic capture sequence completed", StringComparison.Ordinal)))
            + "\nComparison capture saved: probe-wetness-before.png and probe-wetness-vintagertx.png";
        Assert(
            !RuntimeLogValidator.IsComplete(waterBeforeImpactEvidence, waterCompletionScenario),
            "water runtime must not stop before the delayed impact evidence sequence reaches disk");
        const string completedImpactMarkers = """
            [VintageRTX.Test] Dropped-item surface impact applied: count=1
            [VintageRTX.Test] Dropped-item surface impact applied: count=2
            [VintageRTX.Test] Dropped-item surface impact applied: count=3
            [VintageRTX.Test] Server reflection witnesses stable: ticks=1750
            [VintageRTX.Test] Server liquid projectile requested: sequence=1/2, kind=stone
            [VintageRTX.Test] Server liquid projectile requested: sequence=2/2, kind=arrow
            [VintageRTX.Test] Server liquid projectile spawned: kind=stone
            [VintageRTX.Test] Server liquid projectile spawned: kind=arrow
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-final-before.png and C:\captures\projectile-stone-baseline-final-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-earlier-surface-field-before.png and C:\captures\projectile-stone-baseline-earlier-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-prior-surface-field-before.png and C:\captures\projectile-stone-baseline-prior-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-surface-field-before.png and C:\captures\projectile-stone-baseline-surface-field-vintagertx.png
            [VintageRTX.Test] Projectile surface impact applied: class=ThrownStone, kind=StoneRicochet
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-surface-field-before.png and C:\captures\projectile-stone-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-final-before.png and C:\captures\projectile-stone-final-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-final-before.png and C:\captures\projectile-arrow-baseline-final-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-earlier-surface-field-before.png and C:\captures\projectile-arrow-baseline-earlier-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-prior-surface-field-before.png and C:\captures\projectile-arrow-baseline-prior-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-surface-field-before.png and C:\captures\projectile-arrow-baseline-surface-field-vintagertx.png
            [VintageRTX.Test] Projectile surface impact applied: class=Projectile, kind=GenericEntry
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-surface-field-before.png and C:\captures\projectile-arrow-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-final-before.png and C:\captures\projectile-arrow-final-vintagertx.png
            """;
        string waterEvidenceBeforeAutomaticCompletion = waterBeforeImpactEvidence
            + "\nimpact-1-reflection-source-vintagertx.png"
            + "\nimpact-1-reflection-source-raw.png"
            + "\nprobe-entity-mirror-vintagertx.png"
            + "\n"
            + completedImpactMarkers;
        Assert(
            !RuntimeLogValidator.IsComplete(
                waterEvidenceBeforeAutomaticCompletion,
                waterCompletionScenario),
            "water runtime must wait until the terminal automatic entity-mirror pair reaches disk");
        string completedWaterEvidence = waterEvidenceBeforeAutomaticCompletion
            + "\n[VintageRTX] Automatic capture sequence completed: "
            + "profile=water-reflection, last=entity-mirror, captures=15.";
        Assert(
            RuntimeLogValidator.IsComplete(completedWaterEvidence, waterCompletionScenario),
            "water runtime may complete after the automatic entity-mirror terminal marker is saved");
        foreach (string savedProjectilePair in new[]
        {
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-stone-baseline-final-before.png and C:\\captures\\projectile-stone-baseline-final-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-stone-baseline-earlier-surface-field-before.png and C:\\captures\\projectile-stone-baseline-earlier-surface-field-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-stone-baseline-prior-surface-field-before.png and C:\\captures\\projectile-stone-baseline-prior-surface-field-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-stone-baseline-surface-field-before.png and C:\\captures\\projectile-stone-baseline-surface-field-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-stone-final-before.png and C:\\captures\\projectile-stone-final-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-stone-surface-field-before.png and C:\\captures\\projectile-stone-surface-field-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-baseline-final-before.png and C:\\captures\\projectile-arrow-baseline-final-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-baseline-earlier-surface-field-before.png and C:\\captures\\projectile-arrow-baseline-earlier-surface-field-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-baseline-prior-surface-field-before.png and C:\\captures\\projectile-arrow-baseline-prior-surface-field-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-baseline-surface-field-before.png and C:\\captures\\projectile-arrow-baseline-surface-field-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-final-before.png and C:\\captures\\projectile-arrow-final-vintagertx.png",
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-surface-field-before.png and C:\\captures\\projectile-arrow-surface-field-vintagertx.png"
        })
        {
            string queuedButUnsaved = completedWaterEvidence.Replace(
                savedProjectilePair,
                "[VintageRTX.Test] Projectile-synchronized capture queued: "
                    + savedProjectilePair[(savedProjectilePair.LastIndexOf("projectile-", StringComparison.Ordinal))..],
                StringComparison.Ordinal);
            Assert(
                !RuntimeLogValidator.IsComplete(queuedButUnsaved, waterCompletionScenario),
                "water runtime completion must wait for every projectile final/surface-field pair to reach disk");
        }
        const string validWaterWitnessEvidence = """
            [VintageRTX.Test] Reflection witnesses requested: opaque-item=game:stone-granite at (98.75,113.08,200.00), surface-clearance=1.20m; alpha-shaped=game:strawdummy at (101.25,111.92,200.00), surface-clearance=0.04m. First-person held arm/torch remains an independent exclusion challenge.
            [VintageRTX.Test] Server reflection witnesses spawned: opaque entity=41, item=game:stone-granite, anchor=(98.75,113.08,200.00); alpha-shaped entity=42, type=game:strawdummy, anchor=(101.25,111.92,200.00).
            [VintageRTX.Test] Server reflection witnesses stable: ticks=1000, opaque entity=41, alpha-shaped entity=42, maximum drift=0.0040m.
            [VintageRTX.Test] Server reflection witnesses stable: ticks=1500, opaque entity=41, alpha-shaped entity=42, maximum drift=0.0040m.
            [VintageRTX.Test] Server reflection witnesses stable: ticks=1750, opaque entity=41, alpha-shaped entity=42, maximum drift=0.0040m.
            [VintageRTX] Comparison capture saved: C:\captures\impact-1-reflection-source-before.png, C:\captures\impact-1-reflection-source-vintagertx.png, and raw pre-final C:\captures\impact-1-reflection-source-raw.png
            [VintageRTX] Comparison capture saved: C:\captures\reflection-before.png and C:\captures\reflection-vintagertx.png
            """;
        Assert(
            !RuntimeLogValidator.Validate(validWaterWitnessEvidence, waterCompletionScenario)
                .Any(static failure => failure.StartsWith(
                    "water reflection witness validation:",
                    StringComparison.Ordinal)),
            "water runtime validation must accept distinct pinned opaque and alpha-shaped witnesses bracketing source/reflection evidence");
        const string finalWitnessCheckpoint =
            "[VintageRTX.Test] Server reflection witnesses stable: ticks=1750, opaque entity=41, alpha-shaped entity=42, maximum drift=0.0040m.";
        const string finalReflectionCapture =
            "[VintageRTX] Comparison capture saved: C:\\captures\\reflection-before.png and C:\\captures\\reflection-vintagertx.png";
        string missingFinalWitnessCheckpoint = validWaterWitnessEvidence.Replace(
            finalWitnessCheckpoint,
            string.Empty,
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(missingFinalWitnessCheckpoint, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "missing the 1000/1500/1750-tick stability checkpoints",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a reflection capture without a final fixed-anchor checkpoint");
        string reflectedBeforeStable = validWaterWitnessEvidence
            .Replace(finalWitnessCheckpoint, "<final-witness-checkpoint>", StringComparison.Ordinal)
            .Replace(finalReflectionCapture, finalWitnessCheckpoint, StringComparison.Ordinal)
            .Replace("<final-witness-checkpoint>", finalReflectionCapture, StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(reflectedBeforeStable, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "evidence is not ordered",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a final reflection captured before witness stability");
        const string validWaterImpactEvidence = """
            [VintageRTX.Test] Water reflection camera applied: water=game:water-still-7 at (93,111,202), bank=(90,111,202), bank distance=3.0, verified look distance=22.0, target=(92.0,111.9,200.0), impact=(100.0,111.9,200.0), pitch=-0.180, patch=3x3.
            [VintageRTX.Test] Server dropped-item impact requested: sequence=1/3, target=(98.75,200.00), spawn=(98.67,112.40,200.03), drop-height=0.50 m, stack=1, expected pseudo-mass=0.350 kg, velocity=(0.24,-0.05,-0.08).
            [VintageRTX.Test] Server dropped-item impact spawned: entity=101, item=game:stone-granite, stack=1, expected pseudo-mass=0.350 kg, drop-height=0.50 m, position=(98.67,112.40,200.03), velocity-si=(0.24,-0.05,-0.08) m/s, motion-step=(0.0040,-0.0008,-0.0013) at 60 Hz.
            [VintageRTX.Test] Dropped-item surface impact applied: count=1, world=(98.800,199.980), cell=(31,30)/64x64, uv=(0.4922,0.4766), energy=1.3800 J, peak=0.0060 m, subgrid=0.0030 m, lambda=0.280 m, uploaded height=0.0050 m, normalXZ=(0.0100,-0.0100), impactVelocity=(0.200,-2.800,-0.067) m/s.
            [VintageRTX.Test] Server dropped-item impact requested: sequence=2/3, target=(100.00,200.00), spawn=(100.33,114.90,199.82), drop-height=3.00 m, stack=9, expected pseudo-mass=1.050 kg, velocity=(-0.52,-1.50,0.28).
            [VintageRTX.Test] Server dropped-item impact spawned: entity=102, item=game:stone-granite, stack=9, expected pseudo-mass=1.050 kg, drop-height=3.00 m, position=(100.33,114.90,199.82), velocity-si=(-0.52,-1.50,0.28) m/s, motion-step=(-0.0087,-0.0250,0.0047) at 60 Hz.
            [VintageRTX.Test] Dropped-item surface impact applied: count=2, world=(99.920,200.040), cell=(32,31)/64x64, uv=(0.5078,0.4922), energy=24.0000 J, peak=0.0120 m, subgrid=0.0060 m, lambda=0.360 m, uploaded height=0.0100 m, normalXZ=(-0.0200,0.0100), impactVelocity=(-0.400,-6.746,0.215) m/s.
            [VintageRTX.Test] Server dropped-item impact requested: sequence=3/3, target=(101.25,200.00), spawn=(100.65,116.90,200.31), drop-height=5.00 m, stack=64, expected pseudo-mass=2.800 kg, velocity=(0.88,-4.00,-0.46).
            [VintageRTX.Test] Server dropped-item impact spawned: entity=103, item=game:stone-granite, stack=64, expected pseudo-mass=2.800 kg, drop-height=5.00 m, position=(100.65,116.90,200.31), velocity-si=(0.88,-4.00,-0.46) m/s, motion-step=(0.0147,-0.0667,-0.0077) at 60 Hz.
            [VintageRTX.Test] Dropped-item surface impact applied: count=3, world=(101.380,199.930), cell=(33,32)/64x64, uv=(0.5234,0.5078), energy=92.0000 J, peak=0.0270 m, subgrid=0.0130 m, lambda=0.480 m, uploaded height=0.0220 m, normalXZ=(0.0300,-0.0200), impactVelocity=(0.600,-8.078,-0.315) m/s.
            """;
        Assert(
            !RuntimeLogValidator.Validate(validWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.StartsWith(
                    "water impact validation:",
                    StringComparison.Ordinal)),
            "water runtime validation must accept three ordered target-local impacts with coherent diagnostics");
        string invalidImpactMassEvidence = validWaterImpactEvidence.Replace(
            "stack=9, expected pseudo-mass=1.050 kg",
            "stack=9, expected pseudo-mass=1.051 kg",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(invalidImpactMassEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "stack, pseudo-mass or entity identity",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a stack whose logged mass diverges from sqrt(stack)");
        string invalidDropHeightEvidence = validWaterImpactEvidence.Replace(
            "drop-height=3.00 m",
            "drop-height=2.00 m",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(invalidDropHeightEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "drop height does not match",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a fall outside the deterministic height sequence");
        string invalidSpawnHeightEvidence = validWaterImpactEvidence.Replace(
            "position=(100.33,114.90,199.82)",
            "position=(100.33,115.90,199.82)",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(invalidSpawnHeightEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "spawn height implies surface",
                    StringComparison.Ordinal)),
            "water runtime validation must correlate release height with the measured liquid plane");
        string invalidBallisticSpawnEvidence = validWaterImpactEvidence.Replace(
            "spawn=(100.33,114.90,199.82)",
            "spawn=(100.83,114.90,199.82)",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(invalidBallisticSpawnEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "ballistic upstream point",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a requested spawn that cannot land on its target");
        string mismatchedServerSpawnEvidence = validWaterImpactEvidence.Replace(
            "position=(100.33,114.90,199.82)",
            "position=(100.53,114.90,199.82)",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(mismatchedServerSpawnEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "server spawn does not match its request",
                    StringComparison.Ordinal)),
            "water runtime validation must correlate the authoritative server spawn with the requested spawn");
        string staleWaterImpactEvidence = validWaterImpactEvidence.Replace(
            "[VintageRTX.Test] Server dropped-item impact requested: sequence=1/3",
            "[VintageRTX.Test] Dropped-item surface impact applied: count=1, world=(100.100,200.100), cell=(32,32)/64x64, uv=(0.5078,0.5078), energy=0.5000 J, peak=0.0020 m, subgrid=0.0010 m, lambda=0.200 m, uploaded height=0.0010 m, normalXZ=(0.0000,0.0000), impactVelocity=(0.000,-1.690,0.000) m/s.\n"
                + "[VintageRTX.Test] Server dropped-item impact requested: sequence=1/3",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(staleWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "expected exactly three requested, spawned and applied impacts",
                    StringComparison.Ordinal)),
            "water runtime validation must reject an impact from an old entity before the injected sequence");
        string misplacedWaterImpactEvidence = validWaterImpactEvidence.Replace(
            "world=(98.800,199.980)",
            "world=(102.000,200.000)",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(misplacedWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "from its requested target",
                    StringComparison.Ordinal)),
            "water runtime validation must reject an injected impact outside the target-local radius");
        string reorderedWaterImpactEvidence = validWaterImpactEvidence.Replace(
            "impactVelocity=(0.200,-2.800,-0.067)",
            "impactVelocity=(-0.400,-2.800,0.215)",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(reorderedWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "does not match requested",
                    StringComparison.Ordinal)),
            "water runtime validation must identify impacts by their ordered horizontal velocity signature");
        string ascendingWaterImpactEvidence = validWaterImpactEvidence.Replace(
            "impactVelocity=(0.200,-2.800,-0.067)",
            "impactVelocity=(0.200,2.800,-0.067)",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(ascendingWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "vertical impact velocity is not descending",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a non-descending impact vector even when its energy magnitude matches");
        string flatEnergyWaterImpactEvidence = validWaterImpactEvidence.Replace(
            "energy=24.0000 J",
            "energy=1.5000 J",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(flatEnergyWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "applied impact energies are not sufficiently increasing",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a non-increasing applied-energy sequence");
        string artificialEnergyWaterImpactEvidence = validWaterImpactEvidence.Replace(
            "energy=92.0000 J",
            "energy=500.0000 J",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(artificialEnergyWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "does not match its logged impact velocity energy",
                    StringComparison.Ordinal)),
            "water runtime validation must reject energy unsupported by the observed impact velocity");
        string excessiveConsistentImpactEvidence = artificialEnergyWaterImpactEvidence.Replace(
            "impactVelocity=(0.600,-8.078,-0.315)",
            "impactVelocity=(0.600,-18.886,-0.315)",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(excessiveConsistentImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "exceeds its ballistic upper bound",
                    StringComparison.Ordinal)),
            "water runtime validation must retain an anti-injection ceiling even for self-consistent energy and velocity");
        string flatPeakWaterImpactEvidence = validWaterImpactEvidence.Replace(
            "peak=0.0120 m",
            "peak=0.0050 m",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(flatPeakWaterImpactEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "applied impact peaks are not strictly increasing",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a non-increasing applied-peak sequence");
        const string validWaterProjectileEvidence = """
            [VintageRTX.Test] Server reflection witnesses stable: ticks=1750, opaque entity=41, alpha-shaped entity=42, maximum drift=0.0040m.
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-final-before.png and C:\captures\projectile-stone-baseline-final-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-earlier-surface-field-before.png and C:\captures\projectile-stone-baseline-earlier-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-prior-surface-field-before.png and C:\captures\projectile-stone-baseline-prior-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-baseline-surface-field-before.png and C:\captures\projectile-stone-baseline-surface-field-vintagertx.png
            [VintageRTX.Test] Server liquid projectile requested: sequence=1/2, kind=stone, entity-type=game:thrownitem, payload=game:stone-granite, target=(97.00,200.00), spawn=(96.04,112.55,199.92), drop-height=0.65 m, velocity=(5.50,-0.35,0.45) m/s.
            [VintageRTX.Test] Server liquid projectile spawned: kind=stone, entity=201, runtime-class=EntityThrownItem, entity-type=game:thrownitem, payload=game:stone-granite, position=(96.04,112.55,199.92), velocity-si=(5.50,-0.35,0.45) m/s, motion-step=(0.0917,-0.0058,0.0075) at 60 Hz.
            [VintageRTX.Test] Projectile surface impact applied: sequence=1, entity=201, class=ThrownStone, kind=StoneRicochet, world=(96.950,200.020), cell=(29,30)/64x64, mass=0.350 kg, energy=2.4000 J, incident=(4.800,-3.100,0.390) m/s, outgoing=(4.800,1.550,0.390) m/s. source=server-authoritative.
            [VintageRTX.Test] Projectile surface impact applied: sequence=2, entity=201, class=ThrownStone, kind=StoneRicochet, world=(99.200,200.200), cell=(31,30)/64x64, mass=0.350 kg, energy=0.6500 J, incident=(3.900,-1.700,0.320) m/s, outgoing=(3.900,0.850,0.320) m/s. source=server-authoritative.
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-surface-field-before.png and C:\captures\projectile-stone-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-stone-final-before.png and C:\captures\projectile-stone-final-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-final-before.png and C:\captures\projectile-arrow-baseline-final-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-earlier-surface-field-before.png and C:\captures\projectile-arrow-baseline-earlier-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-prior-surface-field-before.png and C:\captures\projectile-arrow-baseline-prior-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-baseline-surface-field-before.png and C:\captures\projectile-arrow-baseline-surface-field-vintagertx.png
            [VintageRTX.Test] Server liquid projectile requested: sequence=2/2, kind=arrow, entity-type=game:arrow-flint, payload=game:arrow-flint, target=(103.00,200.00), spawn=(103.37,113.30,200.09), drop-height=1.40 m, velocity=(-1.40,-2.20,-0.35) m/s.
            [VintageRTX.Test] Server liquid projectile spawned: kind=arrow, entity=202, runtime-class=EntityProjectile, entity-type=game:arrow-flint, payload=game:arrow-flint, position=(103.37,113.30,200.09), velocity-si=(-1.40,-2.20,-0.35) m/s, motion-step=(-0.0233,-0.0367,-0.0058) at 60 Hz.
            [VintageRTX.Test] Projectile surface impact applied: sequence=3, entity=202, class=Projectile, kind=GenericEntry, world=(102.980,199.990), cell=(35,30)/64x64, mass=0.060 kg, energy=0.9200 J, incident=(-1.200,-4.700,-0.300) m/s, outgoing=(-1.100,-3.800,-0.280) m/s. source=server-authoritative.
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-surface-field-before.png and C:\captures\projectile-arrow-surface-field-vintagertx.png
            [VintageRTX] Comparison capture saved: C:\captures\projectile-arrow-final-before.png and C:\captures\projectile-arrow-final-vintagertx.png
            [VintageRTX.Test] Server dropped-item impact requested: sequence=1/3, target=(98.75,200.00), spawn=(98.67,112.40,200.03), drop-height=0.50 m, stack=1, expected pseudo-mass=0.350 kg, velocity=(0.24,-0.05,-0.08).
            """;
        Assert(
            !RuntimeLogValidator.Validate(validWaterProjectileEvidence, waterCompletionScenario)
                .Any(static failure => failure.StartsWith(
                    "water projectile validation:",
                    StringComparison.Ordinal)),
            "water runtime validation must accept a repeated real thrown-stone ricochet and one real arrow entry after stabilization");
        string clientReconstructedProjectileEvidence = validWaterProjectileEvidence.Replace(
            "source=server-authoritative.",
            "source=client-remote-motion.",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(clientReconstructedProjectileEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "authoritative motion",
                    StringComparison.Ordinal)),
            "the local runtime gate must reject projectile energy reconstructed from client interpolation");
        Assert(
            !RuntimeLogValidator.Validate(validWaterProjectileEvidence, waterCompletionScenario)
                .Any(static failure => failure.StartsWith(
                    "water projectile capture validation:",
                    StringComparison.Ordinal)),
            "water runtime validation must accept final and surface-field pairs saved after both projectile callbacks");
        string missingArrowSurfaceEvidence = validWaterProjectileEvidence.Replace(
            "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-surface-field-before.png and C:\\captures\\projectile-arrow-surface-field-vintagertx.png",
            "[VintageRTX.Test] Projectile-synchronized capture queued: label=projectile-arrow-surface-field-vintagertx.png",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(missingArrowSurfaceEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "expected exactly one saved projectile-arrow-surface-field pair",
                    StringComparison.Ordinal)),
            "water runtime validation must reject a queued label when its surface-field PNG pair was not saved");
        string arrowCaptureBeforeEntry = validWaterProjectileEvidence
            .Replace(
                "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-final-before.png and C:\\captures\\projectile-arrow-final-vintagertx.png",
                "<arrow-final>",
                StringComparison.Ordinal)
            .Replace(
                "[VintageRTX.Test] Projectile surface impact applied: sequence=3, entity=202, class=Projectile, kind=GenericEntry, world=(102.980,199.990), cell=(35,30)/64x64, mass=0.060 kg, energy=0.9200 J, incident=(-1.200,-4.700,-0.300) m/s, outgoing=(-1.100,-3.800,-0.280) m/s. source=server-authoritative.",
                "[VintageRTX] Comparison capture saved: C:\\captures\\projectile-arrow-final-before.png and C:\\captures\\projectile-arrow-final-vintagertx.png",
                StringComparison.Ordinal)
            .Replace(
                "<arrow-final>",
                "[VintageRTX.Test] Projectile surface impact applied: sequence=3, entity=202, class=Projectile, kind=GenericEntry, world=(102.980,199.990), cell=(35,30)/64x64, mass=0.060 kg, energy=0.9200 J, incident=(-1.200,-4.700,-0.300) m/s, outgoing=(-1.100,-3.800,-0.280) m/s. source=server-authoritative.",
                StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(arrowCaptureBeforeEntry, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "clean baseline/callback/surface-field/final ordering was not preserved",
                    StringComparison.Ordinal)),
            "water runtime validation must reject an arrow frame captured before the real liquid entry");
        string duplicateArrowProjectileEvidence = validWaterProjectileEvidence
            + "\n[VintageRTX.Test] Projectile surface impact applied: sequence=4, entity=202, class=Projectile, kind=GenericEntry, world=(103.100,200.100), cell=(35,30)/64x64, mass=0.060 kg, energy=0.2000 J, incident=(-0.500,-1.200,-0.100) m/s, outgoing=(-0.400,-0.900,-0.080) m/s. source=server-authoritative.";
        Assert(
            RuntimeLogValidator.Validate(duplicateArrowProjectileEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "flint arrow must produce exactly one entry impulse",
                    StringComparison.Ordinal)),
            "water runtime validation must reject duplicate arrow entry impulses");
        string wrongStoneClassEvidence = validWaterProjectileEvidence.Replace(
            "class=ThrownStone, kind=StoneRicochet",
            "class=Projectile, kind=GenericEntry",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(wrongStoneClassEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "stone callback does not preserve its physical class",
                    StringComparison.Ordinal)),
            "water runtime validation must prove the thrownitem payload is classified as a ricocheting stone");
        string earlyProjectileEvidence = validWaterProjectileEvidence.Replace(
            "[VintageRTX.Test] Server reflection witnesses stable: ticks=1750, opaque entity=41, alpha-shaped entity=42, maximum drift=0.0040m.",
            string.Empty,
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(earlyProjectileEvidence, waterCompletionScenario)
                .Any(static failure => failure.Contains(
                    "must follow the 1750-tick stability checkpoint",
                    StringComparison.Ordinal)),
            "water runtime validation must reject projectile injection before final scene stabilization");
        Assert(
            RuntimeLogValidator.Validate(benchmarkOnly, completionScenario)
                .Contains("stabilized A/B/A metrics are missing or malformed"),
            "runtime validation must reject a result token without parseable A/B/A metrics");
        const string validBenchmark =
            "[VintageRTX] Stabilized A/B/A result | baseline: fps=100.0, 1%low=60.0, jitter=1.00ms, gpu=0.00ms | effect: fps=96.0, 1%low=54.0, jitter=1.20ms, gpu=2.50ms | delta fps=+4.0, delta 1%low=+6.0.";
        Assert(
            !RuntimeLogValidator.Validate(validBenchmark, completionScenario)
                .Any(static failure => failure.Contains("A/B/A metrics", StringComparison.Ordinal)),
            "runtime validation must accept one complete, coherent A/B/A result at the inclusive budgets");
        const string contendedBenchmark =
            "[VintageRTX] Stabilized A/B/A result | baseline: fps=59.2, 1%low=38.4, jitter=2.59ms, gpu=0.00ms | effect: fps=48.5, 1%low=22.1, jitter=7.73ms, gpu=6.56ms | delta fps=+10.7, delta 1%low=+16.3.";
        Assert(
            !RuntimeLogValidator.BaselineCanEvaluatePerformance(59.2, 38.4, completionScenario)
                && RuntimeLogValidator.TryDescribeBenchmarkContention(
                    contendedBenchmark,
                    completionScenario,
                    out string contentionDiagnostic)
                && contentionDiagnostic.Contains("performance gates are inconclusive", StringComparison.Ordinal)
                && !RuntimeLogValidator.Validate(contendedBenchmark, completionScenario)
                    .Any(static failure => failure.Contains("GPU cost", StringComparison.Ordinal)
                        || failure.Contains("frame-time", StringComparison.Ordinal)
                        || failure.Contains("effect average", StringComparison.Ordinal)
                        || failure.Contains("effect 1% low", StringComparison.Ordinal)
                        || failure.Contains("jitter increase", StringComparison.Ordinal)),
            "a baseline below the requested effect floors must be reported as contended, not as a renderer regression");
        Assert(
            RuntimeLogValidator.Validate(validBenchmark + "\n" + validBenchmark, completionScenario)
                .Contains("stabilized A/B/A metrics are missing or malformed"),
            "runtime validation must reject duplicate A/B/A result lines");
        string inconsistentBenchmark = validBenchmark.Replace(
            "delta fps=+4.0",
            "delta fps=+1.0",
            StringComparison.Ordinal);
        Assert(
            RuntimeLogValidator.Validate(inconsistentBenchmark, completionScenario)
                .Contains("stabilized A/B/A metrics contain invalid samples or inconsistent deltas"),
            "runtime validation must reject A/B/A deltas that disagree with the printed samples");
        const string unstableWaterBenchmark =
            "[VintageRTX] Stabilized A/B/A result | baseline: fps=120.1, 1%low=66.0, jitter=1.85ms, gpu=0.00ms | effect: fps=78.5, 1%low=37.7, jitter=3.09ms, gpu=3.19ms | delta fps=+41.6, delta 1%low=+28.3.";
        IReadOnlyList<string> unstableWaterFailures = RuntimeLogValidator.Validate(
            unstableWaterBenchmark,
            ScenarioCatalog.Get("water-reflection"));
        Assert(
            !unstableWaterFailures.Any(static failure =>
                failure.Contains("frame-time", StringComparison.Ordinal)
                || failure.Contains("effect 1% low", StringComparison.Ordinal)
                || failure.Contains("GPU cost", StringComparison.Ordinal)),
            "visual-only water validation must ignore FPS, 1% low, jitter, and GPU thresholds");

        string nonstandardTessellationFailure =
            "Voxel scene generation 3 ready\n"
            + "[Error] Exception: Index was outside the bounds of the array.\n"
            + "   at Vintagestory.GameContent.BlockFenceStackAware.OnJsonTesselation(args)\n"
            + "   at Vintagestory.Client.NoObf.JsonTesselator.Tesselate(args)\n"
            + "   at Vintagestory.Client.NoObf.ChunkTesselator.TesselateBlock(args)\n"
            + "Comparison capture saved: final-before.png and final-vintagertx.png\n"
            + validBenchmark;
        Assert(
            RuntimeLogValidator.Validate(
                nonstandardTessellationFailure,
                ScenarioCatalog.Get("nonstandard-geometry"))
                .Any(static failure => failure.Contains(
                    "nonstandard block tessellation failed while building the real-case geometry scene (count=1)",
                    StringComparison.Ordinal)),
            "the nonstandard-geometry gate must reject a failed real block tessellation");
        Assert(
            !RuntimeLogValidator.Validate(
                nonstandardTessellationFailure,
                ScenarioCatalog.Get("reference-room"))
                .Contains("nonstandard block tessellation failed while building the real-case geometry scene"),
            "unrelated world scenarios must not fail on the engine's generic fence-stack noise");
        string separatedTessellationFragments =
            "Voxel scene generation 3 ready\n"
            + "[Error] Exception: Index was outside the bounds of the array.\n"
            + "   at Some.Unrelated.Stack\n"
            + "[Notification] next entry\n"
            + "   at Vintagestory.GameContent.BlockFenceStackAware.OnJsonTesselation(args)\n"
            + "   at Vintagestory.Client.NoObf.ChunkTesselator.TesselateBlock(args)\n"
            + "Comparison capture saved: final-before.png and final-vintagertx.png\n"
            + validBenchmark;
        Assert(
            !RuntimeLogValidator.Validate(
                separatedTessellationFragments,
                ScenarioCatalog.Get("nonstandard-geometry"))
                .Any(static failure => failure.Contains("nonstandard block tessellation failed", StringComparison.Ordinal)),
            "separated tessellation log fragments must not be joined into a false failure");
    }

    /// <summary>
    /// Verifies the standalone Render Lab Contract regression contract against deterministic fixture data.
    /// </summary>
    private static void TestStandaloneRenderLabContract()
    {
        string root = TestPaths.FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(
            root, "tools", "VintageRTX.RenderLab", "VintageRTX.RenderLab.csproj"));
        string program = File.ReadAllText(Path.Combine(
            root, "tools", "VintageRTX.RenderLab", "Program.cs"));
        string renderer = File.ReadAllText(Path.Combine(
            root, "tools", "VintageRTX.RenderLab", "StandaloneRenderer.cs"));
        string pbrAnalyzer = File.ReadAllText(Path.Combine(
            root, "tools", "VintageRTX.RenderLab", "PbrSeparationAnalyzer.cs"));
        string scene = File.ReadAllText(Path.Combine(
            root, "tools", "VintageRTX.RenderLab", "SyntheticScene.cs"));
        Assert(
            project.Contains("OpenTK.Windowing.Desktop", StringComparison.Ordinal)
            && project.Contains("glfw3.dll", StringComparison.Ordinal)
            && program.Contains("StartVisible = false", StringComparison.Ordinal),
            "standalone lab must use its own hidden OpenGL context");
        Assert(
            renderer.Contains("DisplayShaderSource.LoadFromFileSystem", StringComparison.Ordinal)
            && renderer.Contains("AppContext.BaseDirectory", StringComparison.Ordinal)
            && renderer.Contains("voxelLightCasterMasks", StringComparison.Ordinal)
            && renderer.Contains("voxelFluidSurface", StringComparison.Ordinal)
            && renderer.Contains("voxelLiquidMetadata", StringComparison.Ordinal)
            && renderer.Contains("liquidOpticalProfiles", StringComparison.Ordinal)
            && renderer.Contains("PixelInternalFormat.Rgba32f", StringComparison.Ordinal)
            && renderer.Contains("voxelSunOccupancy", StringComparison.Ordinal),
            "standalone lab must execute the production shader with its complete texture contract");
        Assert(
            scene.Contains("TraceLantern", StringComparison.Ordinal)
            && scene.Contains("TraceAnvil", StringComparison.Ordinal)
            && scene.Contains("TraceCrossPlane", StringComparison.Ordinal)
            && scene.Contains("BuildLanternCaster", StringComparison.Ordinal)
            && scene.Contains("FluidSurface", StringComparison.Ordinal),
            "standalone fixture must include lantern cage, anvil, crossed vegetation and water");
        Assert(
            scene.Contains("Inside(point, LanternEnvelopeMinimum, LanternEnvelopeMaximum)", StringComparison.Ordinal)
            && scene.Contains("outsideCageProbes.Any(InsideLanternCage)", StringComparison.Ordinal)
            && scene.Contains("BlockOccupancyCoverage", StringComparison.Ordinal)
            && scene.Contains("ulong mask = 0", StringComparison.Ordinal)
            && scene.Contains("Occupancy[occupancyIndex] != 0", StringComparison.Ordinal)
            && scene.Contains("mask |= 1UL << bitIndex", StringComparison.Ordinal)
            && renderer.Contains("PixelInternalFormat.Rg32ui", StringComparison.Ordinal)
            && renderer.Contains("PixelFormat.RgInteger", StringComparison.Ordinal)
            && scene.Contains("GrassTexelIsOpaque(point, normalizedHeight)", StringComparison.Ordinal)
            && scene.Contains("BlockOccupancyCoverage(7, 3, 12) > 0.0f", StringComparison.Ordinal),
            "standalone shadows must keep finite alpha-cut geometry and pack fine sun occupancy into RG32UI subvoxel masks");
        Assert(
            renderer.Contains("PbrSeparationAnalyzer.Analyze", StringComparison.Ordinal)
            && renderer.Contains("pbr-response.png", StringComparison.Ordinal)
            && renderer.Contains("pbr-classes.png", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("RoughnessSeparation", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("MetallicSeparation", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("MeanLocalNormalVariationDegrees", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("FinalToSourceLuminanceRatio", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("anvil-metal", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("lantern-cage-metal", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("remains energy-starved", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("vegetation remains near-black", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("roughness gap", StringComparison.Ordinal)
            && pbrAnalyzer.Contains("metallic gap", StringComparison.Ordinal),
            "standalone lab must expose unambiguous PBR response/classes and quantitative separation");
        string combined = program + renderer + scene;
        Assert(
            !combined.Contains("Vintagestory.exe", StringComparison.OrdinalIgnoreCase)
            && !combined.Contains("--openWorld", StringComparison.OrdinalIgnoreCase),
            "standalone lab must not start or open a Vintage Story world");
    }

    /// <summary>
    /// Verifies the deterministic Environment Verification regression contract against deterministic fixture data.
    /// </summary>
    private static void TestDeterministicEnvironmentVerification()
    {
        Assert(
            RuntimeScenarioProbe.IsEnvironmentVerified(12.0f, true, 11.99f, 0.0f, 0.0f, -1.0f),
            "fixed noon and cloudless weather must pass");
        Assert(
            RuntimeScenarioProbe.IsEnvironmentVerified(0.0f, true, 23.99f, 0.0f, -1.0f, -1.0f),
            "hour comparison must wrap around midnight");
        Assert(
            !RuntimeScenarioProbe.IsEnvironmentVerified(12.0f, true, 12.0f, 60.0f, 0.0f, 0.0f),
            "running calendar must fail");
        Assert(
            !RuntimeScenarioProbe.IsEnvironmentVerified(12.0f, true, 12.0f, 0.0f, 0.25f, 0.5f),
            "rain or rain clouds must fail");
        Assert(
            RuntimeScenarioProbe.IsEnvironmentVerified(
                0.0f,
                true,
                24.0f,
                0.0f,
                0.0f,
                0.0f,
                true,
                -0.01f),
            "night verification must accept a sun below the horizon while allowing lunar ambient light");
        Assert(
            !RuntimeScenarioProbe.IsEnvironmentVerified(
                0.0f,
                true,
                24.0f,
                0.0f,
                0.0f,
                0.0f,
                true,
                0.01f),
            "night verification must reject a clock-compatible scene with the sun above the horizon");
        Assert(
            RuntimeScenarioProbe.IsEnvironmentVerified(
                12.0f,
                true,
                12.0f,
                0.0f,
                0.0f,
                0.0f,
                sunVertical: 0.05f,
                requireDay: true),
            "daylight verification must wait for the sun to reach the camera-placement threshold");
        Assert(
            !RuntimeScenarioProbe.IsEnvironmentVerified(
                12.0f,
                true,
                12.0f,
                0.0f,
                0.0f,
                0.0f,
                sunVertical: -0.01f,
                requireDay: true),
            "daylight verification must reject a nominal daytime clock while the sun is below the horizon");
        Assert(
            RuntimeScenarioProbe.IsEnvironmentVerified(
                12.0f,
                false,
                12.0f,
                0.0f,
                0.94f,
                0.72f,
                requestedPrecipitation: 1.0f),
            "forced rain must accept matching precipitation and a finite cloud signal");
        Assert(
            !RuntimeScenarioProbe.IsEnvironmentVerified(
                12.0f,
                false,
                12.0f,
                0.0f,
                0.45f,
                0.72f,
                requestedPrecipitation: 1.0f),
            "forced rain must reject a climate-only background rainfall value");
    }

    /// <summary>
    /// Verifies the weather Wetness Smoothing regression contract against deterministic fixture data.
    /// </summary>
    private static void TestWeatherWetnessSmoothing()
    {
        Assert(FilmicDisplayRenderer.ComputeRainWetnessTarget(0.0f, 1.0f) == 0.0f,
            "rain clouds alone must not wet surfaces");
        Assert(FilmicDisplayRenderer.ComputeRainWetnessTarget(1.0f, 0.0f) == 1.0f,
            "forced precipitation must reach full wetness even before cloud overlay convergence");
        float wetStep = FilmicDisplayRenderer.AdvanceRainWetness(0.0f, 1.0f, 0.1f);
        float dryStep = FilmicDisplayRenderer.AdvanceRainWetness(1.0f, 0.0f, 0.1f);
        Assert(wetStep > 0.05f, "rain accumulation must respond promptly");
        Assert(1.0f - dryStep < wetStep * 0.2f, "drying must be slower than accumulation");
        Assert(float.IsFinite(FilmicDisplayRenderer.ComputeRainWetnessTarget(float.NaN, float.PositiveInfinity)),
            "invalid weather data must be clamped to a finite target");
    }

    /// <summary>
    /// Creates canonical Cube Mesh with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Canonical Cube Mesh result consumed by the caller&apos;s assertion.</returns>
    private static MeshData CreateCanonicalCubeMesh()
    {
        return CreateQuadMesh(
            [new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0)],
            [new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1)],
            [new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)],
            [new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0)],
            [new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)],
            [new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)]);
    }

    /// <summary>
    /// Creates crossed Plane Mesh with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Crossed Plane Mesh result consumed by the caller&apos;s assertion.</returns>
    private static MeshData CreateCrossedPlaneMesh()
    {
        return CreateCrossedPlaneMesh(0.0f, 1.0f);
    }

    /// <summary>
    /// Creates crossed Plane Mesh with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <returns>The create Crossed Plane Mesh result consumed by the caller&apos;s assertion.</returns>
    private static MeshData CreateCrossedPlaneMesh(float minimum, float maximum)
    {
        Vec3f[] diagonalA =
        [
            new(minimum, 0, minimum),
            new(maximum, 0, maximum),
            new(maximum, 1, maximum),
            new(minimum, 1, minimum)
        ];
        Vec3f[] diagonalB =
        [
            new(maximum, 0, minimum),
            new(minimum, 0, maximum),
            new(minimum, 1, maximum),
            new(maximum, 1, minimum)
        ];
        return CreateQuadMesh(diagonalA, diagonalB, diagonalA, diagonalB, diagonalA, diagonalB);
    }

    /// <summary>
    /// Creates quad Mesh with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="quads">The quads input used to configure this deterministic test path.</param>
    /// <returns>The create Quad Mesh result consumed by the caller&apos;s assertion.</returns>
    private static MeshData CreateQuadMesh(params Vec3f[][] quads)
    {
        float[] xyz = new float[quads.Length * 12];
        int[] indices = new int[quads.Length * 6];
        for (int face = 0; face < quads.Length; face++)
        {
            for (int vertex = 0; vertex < 4; vertex++)
            {
                Vec3f value = quads[face][vertex];
                int xyzOffset = (face * 4 + vertex) * 3;
                xyz[xyzOffset] = value.X;
                xyz[xyzOffset + 1] = value.Y;
                xyz[xyzOffset + 2] = value.Z;
            }

            int vertexOffset = face * 4;
            int indexOffset = face * 6;
            indices[indexOffset] = vertexOffset;
            indices[indexOffset + 1] = vertexOffset + 1;
            indices[indexOffset + 2] = vertexOffset + 2;
            indices[indexOffset + 3] = vertexOffset;
            indices[indexOffset + 4] = vertexOffset + 2;
            indices[indexOffset + 5] = vertexOffset + 3;
        }

        return new MeshData(false)
        {
            xyz = xyz,
            Indices = indices,
            VerticesCount = quads.Length * 4,
            IndicesCount = quads.Length * 6,
            IndicesPerFace = 6
        };
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
