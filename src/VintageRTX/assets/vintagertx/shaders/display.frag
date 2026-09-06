#version 330 core

uniform sampler2D sourceColor;
uniform sampler2D reflectionSourceColor;
uniform sampler2D entityMirrorColor;
uniform sampler2D entityMirrorDepth;
uniform sampler2D historyColor;
uniform sampler2D shadowPointCurrentA;
uniform sampler2D shadowPointCurrentB;
uniform sampler2D shadowSunCurrent;
uniform sampler2D shadowPointHistoryA;
uniform sampler2D shadowPointHistoryB;
uniform sampler2D shadowSunHistory;
uniform sampler2D gNormal;
uniform sampler2D gPosition;
uniform sampler2D gDirectPosition;
uniform sampler2D gOpaquePosition;
uniform sampler2D gOpaqueDepth;
uniform sampler2D gLiquidDepth;
uniform sampler2D gMaterial;
uniform sampler2DShadow nativeShadowMapFar;
uniform sampler2DShadow nativeShadowMapNear;
uniform sampler3D voxelVolume;
uniform sampler3D voxelOccupancy;
uniform usampler3D voxelSunOccupancy;
uniform sampler3D voxelLightCasterMasks;
uniform sampler3D voxelIrradiance;
uniform sampler3D voxelIrradianceDirection;
uniform sampler2D voxelFluidSurface;
uniform sampler3D voxelLiquidMetadata;
uniform sampler2D liquidOpticalProfiles;
uniform sampler2D voxelRainSurface;
uniform sampler2D dynamicLiquidSurface;
uniform mat4 projection;
uniform mat4 inverseProjection;
uniform mat4 viewMatrix;
uniform mat4 inverseViewMatrix;
uniform mat4 nativeShadowMatrixFar;
uniform mat4 nativeShadowMatrixNear;
uniform vec3 nativeShadowReferenceOffsetFar;
uniform vec3 nativeShadowReferenceOffsetNear;
uniform float nativeShadowRangeFar;
uniform float nativeShadowRangeNear;
uniform vec2 inverseFrameSize;
// Reciprocal tier-scaled shadow-mask dimensions in inverse shadow pixels.
uniform vec2 shadowInverseFrameSize;
// Performance uses one fixed symmetric diagonal pair; higher tiers resolve the complete cross.
uniform int shadowFilterTapCount;
uniform vec3 cameraWorldPosition;
uniform vec3 floatingWorldOrigin;
uniform vec3 voxelOrigin;
uniform vec3 voxelSize;
uniform vec2 fluidSurfaceOrigin;
uniform vec2 fluidSurfaceSize;
uniform vec3 sunVoxelOrigin;
uniform vec3 sunVoxelSize;
uniform float sunOccupancyScale;
uniform vec2 rainSurfaceOrigin;
uniform vec2 rainSurfaceSize;
uniform vec4 voxelLightPositionIntensity[8];
uniform vec4 voxelLightColorRadius[8];
// xy = luminous half-width/half-height in metres, z = trace cutoff in lux,
// w = one for authored SI photometry and zero for a game-range fallback.
uniform vec4 voxelLightPhotometry[8];
uniform float voxelLightCasterLayer[8];
uniform int voxelLightCount;
uniform float exposure;
uniform float contrast;
uniform float saturation;
uniform float vibrance;
uniform float vignette;
uniform float indirectLightStrength;
uniform float relightingStrength;
uniform float skyLightStrength;
uniform float emissiveLightStrength;
uniform float contactShadowStrength;
uniform float reflectionStrength;
uniform float reflectionDistance;
uniform float rainWetness;
uniform int voxelReflectionsEnabled;
uniform float pointLightShadowStrength;
uniform float pointLightBounceStrength;
uniform float voxelBounceDistance;
uniform float pointLightRadius;
uniform float pointLightSourceRadius;
uniform int pointLightShadowSamples;
uniform int denseDynamicLightCluster;
uniform int voxelBounceRayCount;
uniform int voxelBounceSteps;
uniform int voxelBounceShadowSteps;
uniform int skyRayCount;
uniform int skyTraceSteps;
uniform int albedoDetailSamples;
uniform int temporalDenoiseSamples;
uniform int transportInterlace;
uniform int secondaryBounceCadence;
uniform vec3 sunDirection;
uniform vec4 sunColorStrength;
uniform float sunLightStrength;
uniform float sunShadowDistance;
uniform float sunFineShadowDistance;
uniform float occupancyScale;
uniform float rayDistance;
uniform int rayCount;
uniform int raySteps;
uniform int screenSpaceLightingEnabled;
uniform int screenSpaceReflectionsEnabled;
uniform int entityMirrorEnabled;
uniform int opaquePositionEnabled;
uniform int opaqueDepthEnabled;
uniform int liquidDepthEnabled;
uniform int reflectionSteps;
uniform int voxelReflectionSteps;
uniform int voxelLightingEnabled;
uniform int temporalFrameIndex;
uniform float rendererTimeSeconds;
uniform vec2 liquidWindVector;
uniform float liquidWindMetresPerSecondPerEngineUnit;
uniform int liquidWaveModeLimit;
uniform vec3 dynamicLiquidOriginCell;
uniform vec2 dynamicLiquidGridSize;
uniform int dynamicLiquidSurfaceEnabled;
uniform int liquidImpactWaveActive;
const int MAX_SUBGRID_IMPACT_PACKETS = 4;
uniform vec4 liquidImpactOriginAgeAmplitude[MAX_SUBGRID_IMPACT_PACKETS];
uniform vec4 liquidImpactMaterial[MAX_SUBGRID_IMPACT_PACKETS];
uniform vec4 liquidImpactMotionWavelength[MAX_SUBGRID_IMPACT_PACKETS];
uniform vec4 liquidImpactEnergyLedger[MAX_SUBGRID_IMPACT_PACKETS];
uniform vec4 liquidImpactSplash[MAX_SUBGRID_IMPACT_PACKETS];
uniform float temporalBlend;
uniform float shadowTemporalBlend;
uniform int shadowPass;
uniform int prefilteredShadowVisibility;
uniform int nativeShadowFarEnabled;
uniform int nativeShadowNearEnabled;
uniform int debugView;
// 0 keeps the standalone display-preview contract. 1 returns the scene RGB
// carrier expected by Vintage Story's Luma framebuffer so final.fsh remains
// the single owner of bloom/SSAO composition, color grading and vignettes.
uniform int outputColorDomain;

in vec2 uv;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outShadowPointB;
layout(location = 2) out vec4 outShadowSun;

const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
const int MAX_RAYS = 8;
const int MAX_STEPS = 24;
const int MAX_REFLECTION_STEPS = 24;
const int MAX_VOXEL_REFLECTION_STEPS = 64;
const int MAX_VOXEL_STEPS = 128;
const int MAX_SUN_STEPS = 96;
const int MAX_VOXEL_LIGHTS = 8;
const int MAX_POINT_LIGHT_SAMPLES = 8;
const int MAX_VOXEL_BOUNCE_RAYS = 4;
const int MAX_VOXEL_BOUNCE_STEPS = 16;
const int MAX_VOXEL_BOUNCE_SHADOW_STEPS = 16;
const int MAX_SKY_RAYS = 4;
const int MAX_SKY_STEPS = 48;
const int MAX_LIGHT_CASTER_STEPS = 24;
const float LIGHT_CASTER_SCALE = 16.0;
const float ALPHA_CAGE_VISIBILITY = 0.46;

vec3 srgbToLinear(vec3 color)
{
    return pow(max(color, vec3(0.0)), vec3(2.2));
}

vec3 linearToSrgb(vec3 color)
{
    return pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
}

float maximumComponent(vec3 value)
{
    return max(value.r, max(value.g, value.b));
}

vec3 sampleReflectionSource(vec2 sampleUv)
{
    vec2 boundedUv = clamp(
        sampleUv,
        inverseFrameSize * 0.5,
        vec2(1.0) - inverseFrameSize * 0.5);
    // First-person pixels are removed once at the opaque boundary by the
    // dedicated asset-backed isolation pass. Every reflection/refraction ray
    // therefore pays the same single lookup as an ordinary scene texture.
    return texture(reflectionSourceColor, boundedUv).rgb;
}

vec3 emitterColor(vec3 color)
{
    vec3 positive = max(color, vec3(0.0));
    float sourceLuminance = max(dot(positive, LUMA), 0.0001);
    vec3 saturated = max(
        mix(vec3(sourceLuminance), positive, 1.08),
        vec3(0.0));
    // Light colours arrive in the same sRGB-like convention as the
    // public engine light arrays. Convert them before accumulating HDR
    // radiance; treating those values as linear caused the orange cast.
    return srgbToLinear(clamp(saturated, 0.0, 1.0)) * 1.20;
}

struct LightingResult
{
    vec3 indirect;
    float occlusion;
    float confidence;
};

struct LiquidOpticalProfile
{
    float ior;
    float transmission;
    float roughness;
    float microNormal;
    vec3 absorption;
    vec3 scattering;
    vec3 emission;
    float opaque;
    float anisotropy;
    float windCoupling;
    float waveAmplitude;
    float waveLength;
    float waveSpeed;
    float damping;
    float impactResponse;
    float surfaceTension;
    float opticalViscosity;
    float bubbleRate;
    float bubbleRadiusMin;
    float bubbleRadiusMax;
    float bubbleRiseDuration;
    float bubbleBurstStrength;
    float bubbleEmissionBoost;
    float densityKgM3;
    float dynamicViscosityPaS;
    float surfaceTensionNm;
    float resolvedWaveEnergyFraction;
    float thermalTemperatureK;
    float thermalEmissivity;
    float metresPerWorldBlock;
};

LiquidOpticalProfile neutralLiquidOpticalProfile()
{
    LiquidOpticalProfile profile;
    profile.ior = 1.000293;
    profile.transmission = 1.0;
    profile.roughness = 1.0;
    profile.microNormal = 0.0;
    profile.absorption = vec3(0.0);
    profile.scattering = vec3(0.0);
    profile.emission = vec3(0.0);
    profile.opaque = 0.0;
    profile.anisotropy = 0.0;
    profile.windCoupling = 0.0;
    profile.waveAmplitude = 0.0;
    profile.waveLength = 1.0;
    profile.waveSpeed = 0.0;
    profile.damping = 1.0;
    profile.impactResponse = 0.0;
    profile.surfaceTension = 0.0;
    profile.opticalViscosity = 1.0;
    profile.bubbleRate = 0.0;
    profile.bubbleRadiusMin = 0.0;
    profile.bubbleRadiusMax = 0.0;
    profile.bubbleRiseDuration = 0.0;
    profile.bubbleBurstStrength = 0.0;
    profile.bubbleEmissionBoost = 0.0;
    profile.densityKgM3 = 998.2;
    profile.dynamicViscosityPaS = 0.001002;
    profile.surfaceTensionNm = 0.07275;
    profile.resolvedWaveEnergyFraction = 0.0;
    profile.thermalTemperatureK = 0.0;
    profile.thermalEmissivity = 0.0;
    profile.metresPerWorldBlock = 1.0;
    return profile;
}

// Legacy R-only voxelFluidSurface uploads have no profile id or flags.
// Preserve them as water without guessing a liquid from framebuffer
// colour. New and modded liquids always use the vintageRtxOptics LUT.
LiquidOpticalProfile defaultWaterOpticalProfile()
{
    LiquidOpticalProfile profile = neutralLiquidOpticalProfile();
    // Fresh-water reference near 20 C. Extinction is Napierian m^-1 and
    // the renderer calibration maps one world block to one metre.
    profile.ior = 1.333;
    profile.absorption = vec3(0.3594, 0.0654, 0.00922);
    profile.scattering = vec3(0.0007, 0.0015, 0.0035);
    profile.anisotropy = 0.85;
    profile.roughness = 0.055;
    profile.microNormal = 0.75;
    profile.windCoupling = 1.0;
    profile.waveAmplitude = 0.12;
    profile.waveLength = 4.0;
    profile.waveSpeed = 1.30;
    profile.damping = 0.08;
    profile.impactResponse = 1.0;
    profile.surfaceTension = 0.07275;
    profile.opticalViscosity = 0.001002;
    profile.densityKgM3 = 998.2;
    profile.dynamicViscosityPaS = 0.001002;
    profile.surfaceTensionNm = 0.07275;
    profile.resolvedWaveEnergyFraction = 0.02;
    return profile;
}

float relativePlanckRadiance(float wavelengthNanometres, float temperatureKelvins)
{
    // Planck B_lambda ratio against a 650 nm, 1473.15 K reference. Working
    // with the ratio avoids enormous radiometric values in the display buffer.
    const float secondRadiationConstantMetreKelvins = 0.01438776877;
    const float referenceWavelengthNanometres = 650.0;
    const float referenceTemperatureKelvins = 1473.15;
    float wavelengthMetres = wavelengthNanometres * 1e-9;
    float referenceWavelengthMetres = referenceWavelengthNanometres * 1e-9;
    float referenceExponent = secondRadiationConstantMetreKelvins
        / (referenceWavelengthMetres * referenceTemperatureKelvins);
    float exponent = secondRadiationConstantMetreKelvins
        / (wavelengthMetres * max(temperatureKelvins, 1.0));
    float wavelengthRatio = referenceWavelengthNanometres / wavelengthNanometres;
    return pow(wavelengthRatio, 5.0)
        * (exp(referenceExponent) - 1.0)
        / max(exp(exponent) - 1.0, 1e-8);
}

vec3 thermalLiquidEmission(float temperatureKelvins, float emissivity)
{
    // The RGB channels sample the same representative wavelengths as the
    // asset basis. The final factor is display exposure only; hue and relative
    // channel energy remain temperature/emissivity driven by Planck's law.
    const float thermalDisplayExposure = 4.0;
    return vec3(
            relativePlanckRadiance(650.0, temperatureKelvins),
            relativePlanckRadiance(550.0, temperatureKelvins),
            relativePlanckRadiance(450.0, temperatureKelvins))
        * clamp(emissivity, 0.0, 1.0)
        * thermalDisplayExposure;
}

LiquidOpticalProfile lookupLiquidOpticalProfile(float encodedProfileId)
{
    int profileId = int(floor(encodedProfileId * 255.0 + 0.5));
    if (profileId <= 0 || profileId >= 255)
    {
        return neutralLiquidOpticalProfile();
    }

    vec4 surface = texelFetch(liquidOpticalProfiles, ivec2(0, profileId), 0);
    vec4 absorption = texelFetch(liquidOpticalProfiles, ivec2(1, profileId), 0);
    vec4 scattering = texelFetch(liquidOpticalProfiles, ivec2(2, profileId), 0);
    vec4 emission = texelFetch(liquidOpticalProfiles, ivec2(3, profileId), 0);
    vec4 waves = texelFetch(liquidOpticalProfiles, ivec2(4, profileId), 0);
    vec4 response = texelFetch(liquidOpticalProfiles, ivec2(5, profileId), 0);
    vec4 bubbles = texelFetch(liquidOpticalProfiles, ivec2(6, profileId), 0);
    vec4 burst = texelFetch(liquidOpticalProfiles, ivec2(7, profileId), 0);
    vec4 transport = texelFetch(liquidOpticalProfiles, ivec2(8, profileId), 0);
    vec4 thermal = texelFetch(liquidOpticalProfiles, ivec2(9, profileId), 0);
    LiquidOpticalProfile profile = neutralLiquidOpticalProfile();
    profile.ior = max(surface.r, 1.0001);
    profile.transmission = clamp(surface.g, 0.0, 1.0);
    profile.roughness = clamp(surface.b, 0.0, 1.0);
    profile.microNormal = max(surface.a, 0.0);
    profile.absorption = max(absorption.rgb, vec3(0.0));
    profile.opaque = clamp(absorption.a, 0.0, 1.0);
    profile.scattering = max(scattering.rgb, vec3(0.0));
    profile.thermalTemperatureK = max(thermal.r, 0.0);
    profile.thermalEmissivity = clamp(thermal.g, 0.0, 1.0);
    profile.metresPerWorldBlock = max(thermal.b, 0.0001);
    profile.emission = profile.thermalTemperatureK > 0.0
        ? thermalLiquidEmission(
            profile.thermalTemperatureK,
            profile.thermalEmissivity)
        : max(emission.rgb, vec3(0.0)) * max(scattering.a, 0.0);
    profile.anisotropy = clamp(emission.a, -0.95, 0.95);
    profile.windCoupling = max(waves.r, 0.0);
    profile.waveAmplitude = max(waves.g, 0.0);
    profile.waveLength = max(waves.b, 0.001);
    profile.waveSpeed = max(waves.a, 0.0);
    profile.damping = max(response.r, 0.0);
    profile.impactResponse = max(response.g, 0.0);
    profile.surfaceTension = max(response.b, 0.0);
    profile.opticalViscosity = max(response.a, 0.001);
    profile.bubbleRate = max(bubbles.r, 0.0);
    profile.bubbleRadiusMin = max(bubbles.g, 0.0);
    profile.bubbleRadiusMax = max(bubbles.b, profile.bubbleRadiusMin);
    profile.bubbleRiseDuration = max(bubbles.a, 0.0);
    profile.bubbleBurstStrength = max(burst.r, 0.0);
    profile.bubbleEmissionBoost = max(burst.g, 0.0);
    profile.densityKgM3 = max(transport.r, 1.0);
    profile.dynamicViscosityPaS = max(transport.g, 0.000001);
    profile.surfaceTensionNm = max(transport.b, 0.0001);
    profile.resolvedWaveEnergyFraction = clamp(transport.a, 0.0, 1.0);
    return profile;
}

void accumulateSubgridImpactPacket(
    int packetIndex,
    vec3 worldPosition,
    float baseHeightWorldBlocks,
    vec2 baseSlope,
    inout float accumulatedHeightWorldBlocks,
    inout vec2 accumulatedSlope)
{
    const float gravityMetresPerSecondSquared = 9.80665;
    vec4 originAgeAmplitude = liquidImpactOriginAgeAmplitude[packetIndex];
    vec4 material = liquidImpactMaterial[packetIndex];
    vec4 motionWavelength = liquidImpactMotionWavelength[packetIndex];
    vec4 energyLedger = liquidImpactEnergyLedger[packetIndex];
    vec4 splash = liquidImpactSplash[packetIndex];
    float ageSeconds = max(originAgeAmplitude.z, 0.0);
    float peakAmplitudeWorldBlocks = max(originAgeAmplitude.w, 0.0);
    float densityKilogramsPerCubicMetre = max(material.x, 1.0);
    float dynamicViscosityPascalSeconds = max(material.y, 0.0);
    float surfaceTensionNewtonsPerMetre = max(material.z, 0.0);
    float additionalDampingPerSecond = max(material.w, 0.0);
    float wavelengthMetres = max(motionWavelength.z, 0.01);
    float metresPerWorldBlock = max(motionWavelength.w, 0.001);
    float surfaceEnergyJoules = max(energyLedger.x, 0.0);
    float resolvedEnergyJoules = max(energyLedger.y, 0.0);
    float subgridEnergyJoules = max(energyLedger.z, 0.0);
    float renderedPacketEnergyJoules = max(energyLedger.w, 0.0);
    float localSplashEnergyJoules = max(splash.w, 0.0);
    float ledgerToleranceJoules = max(0.00001, surfaceEnergyJoules * 0.0001);
    if (resolvedEnergyJoules > surfaceEnergyJoules + ledgerToleranceJoules
        || subgridEnergyJoules > surfaceEnergyJoules + ledgerToleranceJoules
        || abs(surfaceEnergyJoules
            - resolvedEnergyJoules
            - subgridEnergyJoules) > ledgerToleranceJoules
        || renderedPacketEnergyJoules + localSplashEnergyJoules
            > subgridEnergyJoules + ledgerToleranceJoules)
    {
        return;
    }

    // The surface current induced by wind is approximately a few percent of
    // wind speed. Advect only the released capillary packet at the conventional
    // 3 % drift ratio. Projectile velocity shapes the impact ledger but must
    // never translate the reconstructed contact away from the physical crossing.
    vec2 windMetresPerSecond = liquidWindVector
        * max(liquidWindMetresPerSecondPerEngineUnit, 0.0);
    vec2 exactImpactOrigin = originAgeAmplitude.xy;
    vec2 localImpactOrigin = exactImpactOrigin
        + windMetresPerSecond * (0.03 * ageSeconds / metresPerWorldBlock);
    vec2 localWorldPosition = worldPosition.xz - dynamicLiquidOriginCell.xy;
    vec2 radialWorldBlocks = localWorldPosition - localImpactOrigin;
    float radiusWorldBlocks = length(radialWorldBlocks);
    vec2 radialDirection = radiusWorldBlocks > 0.00001
        ? radialWorldBlocks / radiusWorldBlocks
        : vec2(0.0);
    float radiusMetres = radiusWorldBlocks * metresPerWorldBlock;
    vec2 worldPixelDxMetres = dFdx(localWorldPosition) * metresPerWorldBlock;
    vec2 worldPixelDyMetres = dFdy(localWorldPosition) * metresPerWorldBlock;
    float radialPixelFootprintMetres = radiusWorldBlocks > 0.00001
        ? max(
            abs(dot(worldPixelDxMetres, radialDirection)),
            abs(dot(worldPixelDyMetres, radialDirection)))
        : max(length(worldPixelDxMetres), length(worldPixelDyMetres));
    vec2 splashRadialWorldBlocks = localWorldPosition - exactImpactOrigin;
    float splashDistanceWorldBlocks = length(splashRadialWorldBlocks);
    vec2 splashRadialDirection = splashDistanceWorldBlocks > 0.00001
        ? splashRadialWorldBlocks / splashDistanceWorldBlocks
        : vec2(0.0);

    // The short-lived entry cavity owns a separate, bounded part of the
    // near-interface ledger. Its compact polynomial has finite gravity and
    // surface-gradient energy. It decays over the physical pinch time while
    // the disjoint reversible cavity-plus-interface budget is released as the
    // outgoing gravity-capillary packet.
    float splashPeakWorldBlocks = max(splash.x, 0.0);
    float splashRadiusWorldBlocks = max(splash.y, 0.0);
    float splashReleaseSeconds = max(splash.z, 0.0001);
    float splashReleaseProgress = localSplashEnergyJoules > 0.0
        ? smoothstep(0.0, 1.0, clamp(ageSeconds / splashReleaseSeconds, 0.0, 1.0))
        : 1.0;
    if (localSplashEnergyJoules > 0.0
        && splashPeakWorldBlocks > 0.0
        && splashRadiusWorldBlocks > 0.0
        && splashDistanceWorldBlocks < splashRadiusWorldBlocks)
    {
        float normalizedSplashRadius = splashDistanceWorldBlocks
            / splashRadiusWorldBlocks;
        float oneMinusRadiusSquared = max(
            1.0 - normalizedSplashRadius * normalizedSplashRadius,
            0.0);
        float localAmplitude = splashPeakWorldBlocks
            * sqrt(max(1.0 - splashReleaseProgress, 0.0));
        float splashShape = oneMinusRadiusSquared * oneMinusRadiusSquared;
        float splashHeight = -localAmplitude * splashShape;
        float splashRadialSlope = 4.0 * localAmplitude
            * splashDistanceWorldBlocks
            * oneMinusRadiusSquared
            / max(
                splashRadiusWorldBlocks * splashRadiusWorldBlocks,
                0.000001);
        accumulatedHeightWorldBlocks += splashHeight;
        accumulatedSlope += splashRadialDirection * splashRadialSlope;
    }

    if (renderedPacketEnergyJoules <= 0.0
        || peakAmplitudeWorldBlocks <= 0.0)
    {
        return;
    }

    float physicalWaveNumberPerMetre = 6.28318530718 / wavelengthMetres;
    // A point-sampled carrier below the pixel Nyquist rate aliases to a static or vanishing
    // ripple. Project the same packet energy onto the narrowest stable geometric carrier at this
    // radial pixel footprint. Three samples per wavelength avoid the two-sample phase ambiguity;
    // packets already resolved by at least three pixels retain their authored physical wavelength.
    float reconstructionWavelengthMetres = max(
        wavelengthMetres,
        3.0 * radialPixelFootprintMetres);
    float reconstructionWaveNumberPerMetre = 6.28318530718
        / max(reconstructionWavelengthMetres, 0.01);
    float capillaryCoefficient = surfaceTensionNewtonsPerMetre
        / densityKilogramsPerCubicMetre;
    float dispersion = gravityMetresPerSecondSquared * physicalWaveNumberPerMetre
        + capillaryCoefficient
            * physicalWaveNumberPerMetre
            * physicalWaveNumberPerMetre
            * physicalWaveNumberPerMetre;
    float angularFrequencyPerSecond = sqrt(max(dispersion, 0.000001));
    float firstDispersionDerivative = gravityMetresPerSecondSquared
        + 3.0 * capillaryCoefficient
            * physicalWaveNumberPerMetre * physicalWaveNumberPerMetre;
    float groupVelocityMetresPerSecond = firstDispersionDerivative
        / (2.0 * angularFrequencyPerSecond);
    float secondDispersionDerivative = 6.0
        * capillaryCoefficient * physicalWaveNumberPerMetre;
    float angularFrequencySecondDerivative = secondDispersionDerivative
            / (2.0 * angularFrequencyPerSecond)
        - firstDispersionDerivative * firstDispersionDerivative
            / (4.0 * angularFrequencyPerSecond
                * angularFrequencyPerSecond * angularFrequencyPerSecond);

    // A Gaussian wave packet spreads from the exact curvature of the
    // gravity-capillary dispersion relation; cylindrical spreading preserves
    // finite impact energy as the circumference grows.
    float initialEnvelopeSigmaMetres = max(wavelengthMetres * 0.35, 0.05);
    float envelopeSigmaMetres = sqrt(
        initialEnvelopeSigmaMetres * initialEnvelopeSigmaMetres
        + pow(
            angularFrequencySecondDerivative * ageSeconds
                / initialEnvelopeSigmaMetres,
            2.0));
    float packetFrontMetres = groupVelocityMetresPerSecond * ageSeconds;
    float distanceFromPacketFrontMetres = radiusMetres - packetFrontMetres;
    if (abs(distanceFromPacketFrontMetres) > envelopeSigmaMetres * 4.0)
    {
        return;
    }

    float kinematicViscositySquareMetresPerSecond =
        dynamicViscosityPascalSeconds / densityKilogramsPerCubicMetre;
    float dampingPerSecond = 2.0 * kinematicViscositySquareMetresPerSecond
            * physicalWaveNumberPerMetre * physicalWaveNumberPerMetre
        + additionalDampingPerSecond;
    float temporalDamping = exp(-dampingPerSecond * ageSeconds);
    float normalizedFrontDistance = distanceFromPacketFrontMetres
        / envelopeSigmaMetres;
    float envelope = exp(-0.5
        * normalizedFrontDistance * normalizedFrontDistance);

    // The packet is not an independent normal overlay: resolved height h bends
    // its phase through theta += beta*k*h. This scalar warp is integrable from
    // the height and slope already carried by the surface field. The previous
    // dot(baseSlope, radialDirection) heuristic had no exact derivative without
    // a surface Hessian, so it could make the visible height and normal disagree.
    const float resolvedHeightPhaseCoupling = 0.45;
    float coupledPhaseWarp = baseHeightWorldBlocks
        * metresPerWorldBlock * reconstructionWaveNumberPerMetre
        * resolvedHeightPhaseCoupling;
    // baseSlope is dh_blocks/dx_blocks. Since h_metres = h_blocks*m and
    // x_metres = x_blocks*m, dh_metres/dx_metres is the same dimensionless
    // slope; therefore grad(beta*k*h_metres) = beta*k*baseSlope in m^-1.
    vec2 coupledPhaseWarpGradientPerMetre = baseSlope
        * reconstructionWaveNumberPerMetre * resolvedHeightPhaseCoupling;
    float phase = reconstructionWaveNumberPerMetre * radiusMetres
        - angularFrequencyPerSecond * ageSeconds
        + coupledPhaseWarp;
    float capillaryReleaseAmplitude = localSplashEnergyJoules > 0.0
        ? sqrt(splashReleaseProgress)
        : 1.0;
    // pi^(3/2)*r*sigma is the effective energy area of the axisymmetric
    // Gaussian carrier. Deriving amplitude from RenderedPacketEnergy keeps
    // the growing ring energy-bounded; SubgridEnergy also contains wake and
    // is deliberately never used as a render amplitude.
    float packetEnergyAreaSquareMetres = pow(3.14159265359, 1.5)
        * max(packetFrontMetres, initialEnvelopeSigmaMetres)
        * envelopeSigmaMetres;
    float physicalPacketStiffnessNewtonsPerMetre = (
            densityKilogramsPerCubicMetre
                * gravityMetresPerSecondSquared
            + surfaceTensionNewtonsPerMetre
                * physicalWaveNumberPerMetre * physicalWaveNumberPerMetre)
        * packetEnergyAreaSquareMetres;
    float reconstructionPacketStiffnessNewtonsPerMetre = (
            densityKilogramsPerCubicMetre
                * gravityMetresPerSecondSquared
            + surfaceTensionNewtonsPerMetre
                * reconstructionWaveNumberPerMetre
                * reconstructionWaveNumberPerMetre)
        * packetEnergyAreaSquareMetres;
    float energyAmplitudeWorldBlocks = sqrt(
            2.0 * renderedPacketEnergyJoules
            / max(reconstructionPacketStiffnessNewtonsPerMetre, 0.000001))
        / metresPerWorldBlock;
    float physicalEnergyAmplitudeWorldBlocks = sqrt(
            2.0 * renderedPacketEnergyJoules
            / max(physicalPacketStiffnessNewtonsPerMetre, 0.000001))
        / metresPerWorldBlock;
    float physicalSteepnessBoundWorldBlocks = 0.35
        / physicalWaveNumberPerMetre
        / metresPerWorldBlock;
    float reconstructionSteepnessBoundWorldBlocks = 0.35
        / reconstructionWaveNumberPerMetre
        / metresPerWorldBlock;
    // PeakAmplitude may also carry an authored height ceiling. Infer that case only when it is
    // materially below both physical energy and steepness bounds; otherwise let the footprint-
    // projected stiffness recover the same joules at a resolvable wavelength.
    float physicalUnboundedCeilingWorldBlocks = min(
        physicalEnergyAmplitudeWorldBlocks,
        physicalSteepnessBoundWorldBlocks);
    float authoredHeightCeilingWorldBlocks = peakAmplitudeWorldBlocks
            < physicalUnboundedCeilingWorldBlocks * 0.98
        ? peakAmplitudeWorldBlocks
        : reconstructionSteepnessBoundWorldBlocks;
    float amplitude = min(
            energyAmplitudeWorldBlocks,
            min(
                reconstructionSteepnessBoundWorldBlocks,
                authoredHeightCeilingWorldBlocks))
        * capillaryReleaseAmplitude
        * temporalDamping;
    float waveHeightWorldBlocks = -amplitude * envelope * cos(phase);
    float envelopeDerivativePerMetre = -distanceFromPacketFrontMetres
        / (envelopeSigmaMetres * envelopeSigmaMetres) * envelope;
    // For H = -A*E*cos(theta), the complete spatial derivative is
    // grad(H) = -A*(grad(E)*cos(theta) - E*sin(theta)*grad(theta)).
    // Keeping the vector gradient preserves both the radial carrier and the
    // resolved-height phase warp instead of projecting everything onto the ring.
    vec2 envelopeGradientPerMetre = radialDirection
        * envelopeDerivativePerMetre;
    vec2 phaseGradientPerMetre = radialDirection * reconstructionWaveNumberPerMetre
        + coupledPhaseWarpGradientPerMetre;
    vec2 heightGradientWorldBlocksPerMetre = -amplitude
        * (envelopeGradientPerMetre * cos(phase)
            - envelope * sin(phase) * phaseGradientPerMetre);
    accumulatedHeightWorldBlocks += waveHeightWorldBlocks;
    accumulatedSlope += heightGradientWorldBlocksPerMetre
        * metresPerWorldBlock;
}

void applySubgridImpactPackets(vec3 worldPosition, inout vec4 dynamicState)
{
    int packetCount = clamp(
        liquidImpactWaveActive,
        0,
        MAX_SUBGRID_IMPACT_PACKETS);
    if (packetCount == 0)
    {
        return;
    }

    vec2 baseHorizontalNormal = clamp(
        dynamicState.yz,
        vec2(-0.999),
        vec2(0.999));
    float baseVerticalNormal = sqrt(max(
        1.0 - dot(baseHorizontalNormal, baseHorizontalNormal),
        0.0001));
    vec2 baseSlope = -baseHorizontalNormal / max(baseVerticalNormal, 0.01);
    float accumulatedHeightWorldBlocks = 0.0;
    vec2 accumulatedSlope = vec2(0.0);
    for (int packetIndex = 0;
        packetIndex < MAX_SUBGRID_IMPACT_PACKETS;
        packetIndex++)
    {
        if (packetIndex >= packetCount)
        {
            break;
        }

        accumulateSubgridImpactPacket(
            packetIndex,
            worldPosition,
            dynamicState.x,
            baseSlope,
            accumulatedHeightWorldBlocks,
            accumulatedSlope);
    }

    vec2 combinedSlope = baseSlope + accumulatedSlope;
    vec3 combinedNormal = normalize(vec3(
        -combinedSlope.x,
        1.0,
        -combinedSlope.y));
    dynamicState.x += accumulatedHeightWorldBlocks;
    dynamicState.yz = combinedNormal.xz;
}

bool sampleDynamicLiquidSurface(vec3 worldPosition, out vec4 dynamicState)
{
    dynamicState = vec4(0.0);
    if (dynamicLiquidSurfaceEnabled == 0
        || dynamicLiquidOriginCell.z <= 0.0
        || any(lessThanEqual(dynamicLiquidGridSize, vec2(0.0))))
    {
        return false;
    }

    vec2 gridPosition = (worldPosition.xz - dynamicLiquidOriginCell.xy)
        / dynamicLiquidOriginCell.z;
    if (any(lessThan(gridPosition, vec2(0.0)))
        || any(greaterThanEqual(gridPosition, dynamicLiquidGridSize)))
    {
        return false;
    }

    dynamicState = texture(
        dynamicLiquidSurface,
        gridPosition / dynamicLiquidGridSize);
    applySubgridImpactPackets(worldPosition, dynamicState);
    return true;
}

bool refineDynamicLiquidSurfaceIntersection(
    vec3 cameraPosition,
    vec3 cameraRay,
    float baseSurfaceWorldY,
    float maximumDisplacement,
    inout float rayDistance,
    inout vec3 surfaceWorldPosition,
    out float displacedSurfaceWorldY)
{
    displacedSurfaceWorldY = baseSurfaceWorldY;
    if (abs(cameraRay.y) <= 0.0001)
    {
        return false;
    }

    bool resolved = false;
    // Two fixed iterations intersect the camera ray with the bilinearly
    // sampled height field. This gives impacts and wakes geometric parallax
    // without an unbounded march or a tessellated-water replacement.
    for (int refinement = 0; refinement < 2; refinement++)
    {
        vec4 dynamicState = vec4(0.0);
        if (!sampleDynamicLiquidSurface(surfaceWorldPosition, dynamicState))
        {
            break;
        }

        float displacement = clamp(
            dynamicState.x,
            -maximumDisplacement,
            maximumDisplacement);
        float candidateSurfaceWorldY = baseSurfaceWorldY + displacement;
        float candidateDistance = (
                candidateSurfaceWorldY - cameraPosition.y)
            / cameraRay.y;
        if (candidateDistance < 0.0)
        {
            break;
        }

        displacedSurfaceWorldY = candidateSurfaceWorldY;
        rayDistance = candidateDistance;
        surfaceWorldPosition = cameraPosition
            + cameraRay * candidateDistance;
        resolved = true;
    }

    return resolved;
}

vec3 profileDrivenLiquidNormal(
    vec3 worldPosition,
    LiquidOpticalProfile profile,
    out float surfaceDynamics,
    out float bubbleEmission,
    out vec2 structuralSurfaceSlope)
{
    vec2 resolvedSurfaceSlope = vec2(0.0);
    structuralSurfaceSlope = vec2(0.0);
    float resolvedSurfaceHeight = 0.0;
    float resolvedSurfaceDynamics = 0.5;
    float resolvedBubbleEmission = 0.0;
    vec4 dynamicState;
    bool hasDynamicSurface = sampleDynamicLiquidSurface(
        worldPosition,
        dynamicState);
    if (hasDynamicSurface)
    {
        vec2 horizontalNormal = clamp(dynamicState.yz, vec2(-0.999), vec2(0.999));
        float verticalNormal = sqrt(max(
            1.0 - dot(horizontalNormal, horizontalNormal),
            0.0001));
        float referenceAmplitude = max(profile.waveAmplitude, 0.001);
        resolvedSurfaceDynamics = clamp(
            0.5 + dynamicState.x / (2.0 * referenceAmplitude),
            0.0,
            1.0);
        resolvedBubbleEmission = max(dynamicState.w, 0.0);
        resolvedSurfaceHeight = dynamicState.x;
        // The 0.5 m simulation grid carries true displaced height, impacts,
        // rain and wakes. Convert its normal back to a height gradient and
        // retain it while the unresolved wind/capillary spectrum below is
        // added. Returning here made every live lake lose those fine bands.
        resolvedSurfaceSlope = -horizontalNormal
            / max(verticalNormal, 0.01);
        structuralSurfaceSlope = resolvedSurfaceSlope;
    }

    // The 0.5 m CPU field already carries the authored dominant wavelength.
    // Repeating that same global sine here produced a second, differently
    // timed 4 m wave and the conspicuous parallel bands seen on whole lakes.
    // This pass therefore starts immediately below the simulation Nyquist
    // wavelength and resolves only its missing directional micro-spectrum.
    // World coordinates are converted to metres before applying k in m^-1.
    vec2 phasePositionMetres = mod(
        worldPosition.xz * profile.metresPerWorldBlock,
        vec2(4096.0));
    float animationTime = rendererTimeSeconds;
    // The dynamic solver deliberately remains a 64-block, 0.5 m field. Outside that field (or
    // before its texture is ready), start the procedural spectrum below the same Nyquist limit;
    // treating an absent binding as a 0.025 m cell injected implausibly tiny, unstable ripples.
    float simulationCellMetres = max(
        max(dynamicLiquidOriginCell.z, 0.5)
            * profile.metresPerWorldBlock,
        0.025);
    float authoredWavelengthMetres = max(
        profile.waveLength * profile.metresPerWorldBlock,
        0.05);
    float longestUnresolvedWavelengthMetres = max(
        min(simulationCellMetres * 1.84, authoredWavelengthMetres * 0.24),
        0.04);
    float baseWaveNumber = 6.28318530718
        / longestUnresolvedWavelengthMetres;
    float kinematicViscosity = profile.dynamicViscosityPaS
        / max(profile.densityKgM3, 1.0);
    float windSpeedMetresPerSecond = clamp(
        length(liquidWindVector)
            * max(liquidWindMetresPerSecondPerEngineUnit, 0.0)
            * clamp(profile.windCoupling, 0.0, 2.0),
        0.0,
        25.0);
    vec2 primaryDirection = windSpeedMetresPerSecond > 0.0001
        ? normalize(liquidWindVector)
        : normalize(vec2(0.86, 0.51));
    vec2 crossDirection = vec2(-primaryDirection.y, primaryDirection.x);
    // Cox-Munk measured a 0.00512*U wind-driven increase of clean-water
    // mean-square slope for U in m/s. The constant 0.003 ocean-swell floor is
    // deliberately omitted: an inland lake has no guaranteed remote swell.
    float unresolvedMeanSquareSlope = 0.00512
        * windSpeedMetresPerSecond
        * profile.microNormal * profile.microNormal
        * (1.0 - profile.resolvedWaveEnergyFraction);
    float unresolvedRmsSlope = sqrt(max(unresolvedMeanSquareSlope, 0.0));

    // Each mode stores tangent(angle from wind), k multiplier, variance weight
    // and a deterministic random phase. The directional distribution sums to
    // one, so sqrt(2*mss*weight) makes the time-averaged slope variance equal
    // the Cox-Munk target before physical viscous damping. Incommensurate wave
    // numbers and deliberately interleaved directions avoid the short spatial
    // recurrence produced by the former five symmetric sine bands.
    const int liquidWaveModeCount = 16;
    const vec4 liquidWaveModes[liquidWaveModeCount] = vec4[](
        vec4( 0.000, 1.00, 0.105, 0.37),
        vec4( 0.532, 1.12, 0.095, 2.11),
        vec4(-0.675, 1.27, 0.095, 4.83),
        vec4( 1.280, 1.43, 0.085, 1.29),
        vec4(-1.540, 1.61, 0.085, 5.62),
        vec4( 0.325, 1.82, 0.070, 3.47),
        vec4(-0.287, 2.05, 0.070, 0.91),
        vec4( 2.605, 2.31, 0.065, 4.16),
        vec4(-3.078, 2.61, 0.060, 2.73),
        vec4( 0.869, 2.94, 0.055, 5.19),
        vec4(-1.000, 3.33, 0.050, 1.83),
        vec4( 4.705, 3.77, 0.045, 3.89),
        vec4(-0.445, 4.26, 0.040, 0.14),
        vec4( 1.963, 4.83, 0.035, 2.97),
        vec4(-2.246, 5.49, 0.025, 5.91),
        vec4( 0.176, 6.23, 0.020, 1.57));
    // A real wind spectrum carries random phases rather than an endlessly
    // repeating wave train. Apply a low-amplitude, time-invariant phase warp
    // with irrational projections to lengthen spatial recurrence. Its exact
    // Jacobian is evaluated below, so the normal remains the derivative of the
    // same continuous height field and cannot flip from frame-indexed noise.
    float phaseWarpScaleMetres = max(
        longestUnresolvedWavelengthMetres * 4.37,
        0.40);
    float phaseWarpAmplitudeMetres = longestUnresolvedWavelengthMetres * 0.28;
    vec2 phaseWarpCoordinates = phasePositionMetres / phaseWarpScaleMetres;
    const vec2 phaseWarpAxis0 = vec2(0.75487767, 0.65586598);
    const vec2 phaseWarpAxis1 = vec2(-0.56984029, 0.82175536);
    const vec2 phaseWarpAxis2 = vec2(1.1129896, -0.3369772);
    float phaseWarp0 = dot(phaseWarpCoordinates, phaseWarpAxis0) + 0.73;
    float phaseWarp1 = dot(phaseWarpCoordinates, phaseWarpAxis1) + 2.19;
    float phaseWarp2 = dot(phaseWarpCoordinates, phaseWarpAxis2) + 4.07;
    vec2 phaseWarpMetres = phaseWarpAmplitudeMetres * vec2(
        sin(phaseWarp0) + 0.47 * sin(phaseWarp2),
        sin(phaseWarp1) + 0.43 * cos(phaseWarp2));
    float phaseWarpDerivativeScale = phaseWarpAmplitudeMetres
        / phaseWarpScaleMetres;
    vec2 phaseWarpDerivativeX = phaseWarpDerivativeScale * vec2(
        cos(phaseWarp0) * phaseWarpAxis0.x
            + 0.47 * cos(phaseWarp2) * phaseWarpAxis2.x,
        cos(phaseWarp1) * phaseWarpAxis1.x
            - 0.43 * sin(phaseWarp2) * phaseWarpAxis2.x);
    vec2 phaseWarpDerivativeZ = phaseWarpDerivativeScale * vec2(
        cos(phaseWarp0) * phaseWarpAxis0.y
            + 0.47 * cos(phaseWarp2) * phaseWarpAxis2.y,
        cos(phaseWarp1) * phaseWarpAxis1.y
            - 0.43 * sin(phaseWarp2) * phaseWarpAxis2.y);
    vec2 slope = resolvedSurfaceSlope;
    float spectralHeightSignal = 0.0;
    float spectralSignalWeight = 0.0;
    float normalizedResolvedHeight = hasDynamicSurface
        ? clamp(
            resolvedSurfaceHeight / max(profile.waveAmplitude, 0.001),
            -1.0,
            1.0)
        : 0.0;
    for (int modeIndex = 0; modeIndex < liquidWaveModeCount; modeIndex++)
    {
        if (modeIndex >= liquidWaveModeLimit)
        {
            break;
        }

        vec4 mode = liquidWaveModes[modeIndex];
        vec2 modeDirection = normalize(
            primaryDirection + crossDirection * mode.x);
        float modeWaveNumber = baseWaveNumber * mode.y;
        // Deep-water gravity-capillary dispersion:
        // omega^2 = g*k + (sigma/rho)*k^3.
        float angularFrequency = sqrt(max(
            9.80665 * modeWaveNumber
                + profile.surfaceTensionNm
                    / max(profile.densityKgM3, 1.0)
                    * modeWaveNumber * modeWaveNumber * modeWaveNumber,
            0.0));
        float modePeriodSeconds = 6.28318530718
            / max(angularFrequency, 0.001);
        float viscousMobility = exp(
            -2.0 * kinematicViscosity
                * modeWaveNumber * modeWaveNumber
                * modePeriodSeconds);
        float phase = dot(
                phasePositionMetres + phaseWarpMetres,
                modeDirection)
                * modeWaveNumber
            - animationTime * angularFrequency
            + mode.w;
        vec2 normalizedPhaseGradient = modeDirection + vec2(
            dot(phaseWarpDerivativeX, modeDirection),
            dot(phaseWarpDerivativeZ, modeDirection));
        // A finite wind fetch produces wave packets, not infinite parallel
        // crests. Give each spectral mode an independent, smooth phase
        // modulation. This is frequency modulation of the same height field;
        // the analytic derivative below keeps the normal integrable and avoids
        // frame-noise flips while spatially bending and splitting long crests.
        float packetAxisAngle = mode.w * 2.371 + mode.y * 0.193;
        vec2 packetAxis = vec2(cos(packetAxisAngle), sin(packetAxisAngle));
        float packetScaleMetres = max(
            longestUnresolvedWavelengthMetres
                * mix(2.15, 4.10, fract(mode.w * 0.61803398875)),
            0.24);
        float packetCarrier = dot(phasePositionMetres, packetAxis)
                / packetScaleMetres
            + mode.w * 1.731;
        float packetPhaseModulation = 0.92 * sin(packetCarrier)
            + 0.31 * sin(packetCarrier * 0.473 + mode.w);
        float packetPhaseDerivative = 0.92 * cos(packetCarrier)
            + 0.31 * 0.473 * cos(packetCarrier * 0.473 + mode.w);
        phase += packetPhaseModulation;
        normalizedPhaseGradient += packetAxis
            * packetPhaseDerivative
            / max(packetScaleMetres * modeWaveNumber, 0.001);
        if (hasDynamicSurface)
        {
            // Advect the unresolved crests through the resolved height and
            // gradient. Impact rings, rain and wakes consequently bend and
            // compress the wind spectrum instead of receiving an unrelated
            // screen-space normal overlay.
            float scaleProgress = clamp(
                log2(max(mode.y, 1.0)) / log2(6.23),
                0.0,
                1.0);
            float resolvedHeightPhaseCoupling = mix(
                0.48,
                1.20,
                scaleProgress);
            phase += normalizedResolvedHeight * resolvedHeightPhaseCoupling;
            // Chain rule for theta += beta*h/A. resolvedSurfaceSlope is measured
            // per world block, while k is per metre, hence A must be converted
            // to metres before normalizing grad(theta) by k. Including this term
            // keeps the reported gradient integrable for non-unit block scales.
            normalizedPhaseGradient += resolvedSurfaceSlope
                * resolvedHeightPhaseCoupling
                / max(
                    profile.waveAmplitude * profile.metresPerWorldBlock
                        * modeWaveNumber,
                    0.001);
        }

        // Suppress a mode once one pixel spans most of its phase. This analytic
        // footprint filter prevents distant capillary modes from temporal
        // aliasing or flipping while retaining their energy close to camera.
        float phaseFootprint = fwidth(phase);
        float footprintVisibility = 1.0 - smoothstep(
            2.1,
            3.4,
            phaseFootprint);
        float modeSlopeAmplitude = unresolvedRmsSlope
            * sqrt(2.0 * mode.z)
            * viscousMobility
            * footprintVisibility;
        slope += normalizedPhaseGradient * cos(phase) * modeSlopeAmplitude;
        spectralHeightSignal += sin(phase) * mode.z * footprintVisibility;
        spectralSignalWeight += mode.z * footprintVisibility;
    }

    // Profiles with a non-zero bubble rate (notably lava) receive sparse,
    // deterministic burst rings. The LUT controls density, size, lifetime and
    // strength; zero-rate water/honey execute the same bounded path with zero
    // contribution. This is a surface perturbation, never a texture replacement.
    float bubbleDensity = clamp(profile.bubbleRate * 24.0, 0.0, 1.0);
    float bubbleCellSize = mix(6.0, 1.5, bubbleDensity);
    vec2 bubbleGrid = phasePositionMetres / bubbleCellSize;
    vec2 bubbleCell = floor(bubbleGrid);
    vec2 bubbleLocal = fract(bubbleGrid) - vec2(0.5);
    float bubbleSeed = fract(sin(dot(
            bubbleCell,
            vec2(12.9898, 78.233))) * 43758.5453);
    float bubbleLifetime = max(profile.bubbleRiseDuration, 0.25);
    float bubbleCycle = fract(animationTime / bubbleLifetime + bubbleSeed);
    float bubbleRadius = mix(
            profile.bubbleRadiusMin,
            profile.bubbleRadiusMax,
            smoothstep(0.0, 0.82, bubbleCycle))
        / bubbleCellSize;
    float bubbleEnvelope = smoothstep(0.02, 0.18, bubbleCycle)
        * (1.0 - smoothstep(0.72, 0.98, bubbleCycle));
    float bubbleRing = exp(
            -abs(length(bubbleLocal) - bubbleRadius) * 92.0)
        * bubbleEnvelope
        * bubbleDensity;
    vec2 bubbleDirection = length(bubbleLocal) > 0.0001
        ? normalize(bubbleLocal)
        : vec2(0.0);
    slope += bubbleDirection * bubbleRing
        * profile.bubbleBurstStrength * 0.035;

    float waveActivity = step(0.00001, unresolvedRmsSlope);
    float animatedWaveSignal = 0.5 + 0.5 * spectralHeightSignal
        / max(spectralSignalWeight, 0.0001);
    surfaceDynamics = clamp(
        mix(
            resolvedSurfaceDynamics,
            animatedWaveSignal,
            waveActivity * (hasDynamicSurface ? 0.55 : 1.0))
            + bubbleRing * 0.50,
        0.0,
        1.0);
    bubbleEmission = max(
        resolvedBubbleEmission,
        bubbleRing * profile.bubbleEmissionBoost);
    return normalize(vec3(-slope.x, 1.0, -slope.y));
}

float exactDielectricFresnel(
    float cosIncident,
    float incidentIor,
    float transmittedIor)
{
    cosIncident = clamp(cosIncident, 0.0, 1.0);
    float eta = incidentIor / max(transmittedIor, 0.0001);
    float sinTransmittedSquared = eta * eta
        * max(1.0 - cosIncident * cosIncident, 0.0);
    if (sinTransmittedSquared >= 1.0)
    {
        return 1.0;
    }
    float cosTransmitted = sqrt(max(
        1.0 - sinTransmittedSquared,
        0.0));
    float sNumerator = incidentIor * cosIncident
        - transmittedIor * cosTransmitted;
    float sDenominator = incidentIor * cosIncident
        + transmittedIor * cosTransmitted;
    float pNumerator = transmittedIor * cosIncident
        - incidentIor * cosTransmitted;
    float pDenominator = transmittedIor * cosIncident
        + incidentIor * cosTransmitted;
    float reflectanceS = sNumerator / max(abs(sDenominator), 0.0001);
    float reflectanceP = pNumerator / max(abs(pDenominator), 0.0001);
    return 0.5 * (
        reflectanceS * reflectanceS
        + reflectanceP * reflectanceP);
}

vec3 liquidExtinctionCoefficient(
    vec3 absorption,
    vec3 scattering,
    float transmission,
    float opaque)
{
    // Absorption and out-scattering are already Napierian coefficients in m^-1.
    // transmission/opaque remain interface and classification metadata; adding
    // -log(transmission) here would double-count an arbitrary per-block loss.
    return max(absorption, vec3(0.0))
        + max(scattering, vec3(0.0));
}

struct ReflectionResult
{
    vec3 color;
    float confidence;
    vec3 voxelColor;
    float voxelConfidence;
    float planarConfidence;
    vec3 planarWorldPosition;
    float fluidColumnSupport;
    float waterColorHint;
    float waterEvidence;
    float liquidPathLength;
    float liquidFresnel;
    vec3 liquidAbsorption;
    vec3 liquidScattering;
    vec3 liquidEmission;
    float liquidTransmission;
    float liquidOpaque;
    float liquidAnisotropy;
    float liquidRoughness;
    float liquidSurfaceDynamics;
    float liquidBubbleEmission;
    float liquidMetresPerWorldBlock;
    vec2 liquidTransmissionUv;
    float liquidTransmissionConfidence;
    float liquidPartialGeometryFace;
    float liquidVerticalFaceEvidence;
    float liquidShoreFaceEvidence;
    float liquidShoreCarrierEvidence;
    float liquidShorePartialGeometryEvidence;
    float liquidShoreColumnEvidence;
    vec2 liquidShoreSurfaceUv;
    float liquidShoreSurfaceConfidence;
};

struct VoxelLightingResult
{
    vec3 direct;
    vec3 directSpecular;
    vec3 blockedDirect;
    vec3 bounce;
    vec3 irradianceCache;
    vec3 irradianceDirection;
    vec3 sunDirect;
    vec3 skyDirect;
    vec4 material;
    float shadow;
    float visibility;
    float sunVisibility;
    float sunShadow;
    float cameraAlignedShadow;
    float skyVisibility;
};

bool isInsideVoxelVolume(vec3 worldPosition)
{
    vec3 local = worldPosition - voxelOrigin;
    return all(greaterThanEqual(local, vec3(0.0)))
        && all(lessThan(local, voxelSize));
}

vec4 sampleVoxelAtWorld(vec3 worldPosition)
{
    vec3 textureUv = (floor(worldPosition) + vec3(0.5) - voxelOrigin) / voxelSize;
    return texture(voxelVolume, textureUv);
}

float partialGeometryMetadataAtWorldPosition(vec3 worldPosition)
{
    vec3 localPosition = worldPosition - voxelOrigin;
    if (any(lessThan(localPosition, vec3(0.0)))
        || any(greaterThanEqual(localPosition, voxelSize)))
    {
        return 0.0;
    }

    vec4 material = texture(
        voxelVolume,
        (floor(localPosition) + vec3(0.5)) / voxelSize);
    int encodedMaterialClass = int(floor(material.a * 255.0 + 0.5));
    return (encodedMaterialClass & 4) != 0 ? 1.0 : 0.0;
}

float solidGeometryMetadataAtWorldPosition(vec3 worldPosition)
{
    vec3 localPosition = worldPosition - voxelOrigin;
    if (any(lessThan(localPosition, vec3(0.0)))
        || any(greaterThanEqual(localPosition, voxelSize)))
    {
        return 0.0;
    }

    vec4 material = texture(
        voxelVolume,
        (floor(localPosition) + vec3(0.5)) / voxelSize);
    return step(0.45, material.a);
}

vec3 sampleVoxelIrradiance(
    vec3 worldPosition,
    vec3 worldGeometricNormal)
{
    // Irradiance lives in empty cells, not inside the receiver block.
    // Move half a voxel into the visible hemisphere before sampling the
    // linearly filtered cache. RGB stores sqrt(radiance / 2).
    vec3 samplePosition = worldPosition + worldGeometricNormal * 0.52;
    vec3 textureUv = (samplePosition - voxelOrigin) / voxelSize;
    vec3 encoded = texture(
        voxelIrradiance,
        clamp(textureUv, vec3(0.001), vec3(0.999))).rgb;
    return encoded * encoded * 2.0;
}

vec3 sampleVoxelIrradianceDirection(
    vec3 worldPosition,
    vec3 worldGeometricNormal)
{
    vec3 samplePosition = worldPosition + worldGeometricNormal * 0.52;
    vec3 textureUv = (samplePosition - voxelOrigin) / voxelSize;
    vec3 direction = texture(
        voxelIrradianceDirection,
        clamp(textureUv, vec3(0.001), vec3(0.999))).rgb * 2.0 - 1.0;
    float directionLength = length(direction);
    return directionLength > 0.08
        ? direction / directionLength
        : vec3(0.0, 1.0, 0.0);
}

float traceLightCasterVisibility(vec3 rayOrigin, vec3 rayTarget, int lightIndex)
{
    int safeLightIndex = clamp(lightIndex, 0, MAX_VOXEL_LIGHTS - 1);
    if (lightIndex != safeLightIndex || lightIndex >= voxelLightCount)
    {
        return 1.0;
    }

    float casterLayer = voxelLightCasterLayer[safeLightIndex];
    if (casterLayer < -0.5)
    {
        return 1.0;
    }

    vec3 blockOrigin = floor(voxelLightPositionIntensity[safeLightIndex].xyz);
    vec3 gridOrigin = (rayTarget - blockOrigin) * LIGHT_CASTER_SCALE;
    vec3 rayVector = (rayOrigin - rayTarget) * LIGHT_CASTER_SCALE;
    float maximumDistance = length(rayVector);
    if (maximumDistance < 0.001
        || any(lessThan(gridOrigin, vec3(0.0)))
        || any(greaterThanEqual(gridOrigin, vec3(LIGHT_CASTER_SCALE))))
    {
        return 1.0;
    }

    vec3 rayDirection = rayVector / maximumDistance;
    vec3 cell = floor(gridOrigin);
    vec3 stepDirection = sign(rayDirection);
    vec3 inverseDirection = 1.0 / max(abs(rayDirection), vec3(0.00001));
    vec3 nextBoundary = mix(cell, cell + vec3(1.0), greaterThan(stepDirection, vec3(0.0)));
    vec3 sideDistance = abs((nextBoundary - gridOrigin) * inverseDirection);
    vec3 deltaDistance = inverseDirection;
    sideDistance = mix(sideDistance, vec3(1e20), lessThan(abs(rayDirection), vec3(0.00001)));

    // The first cell contains the flame sample and is deliberately skipped.
    // From there, a 16^3 texture-aware mask resolves the metal cage without
    // turning its transparent glass or alpha-cutout holes into blockers.
    for (int stepIndex = 0; stepIndex < MAX_LIGHT_CASTER_STEPS; stepIndex++)
    {
        float traveled = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
        // A ray that crosses an exact subvoxel edge or corner enters only the
        // diagonally adjacent cell. Advancing a single tied axis visits a
        // zero-width neighbour and makes the 6.25 cm cage cells cast a wider
        // silhouette than the authored bars. Advance every tied axis, as the
        // conservative long-range traversal already does.
        if (sideDistance.x <= traveled + 0.00001)
        {
            sideDistance.x += deltaDistance.x;
            cell.x += stepDirection.x;
        }
        if (sideDistance.y <= traveled + 0.00001)
        {
            sideDistance.y += deltaDistance.y;
            cell.y += stepDirection.y;
        }
        if (sideDistance.z <= traveled + 0.00001)
        {
            sideDistance.z += deltaDistance.z;
            cell.z += stepDirection.z;
        }

        if (traveled >= maximumDistance - 0.35)
        {
            return 1.0;
        }

        vec3 cellCenter = cell + vec3(0.5);
        if (any(lessThan(cellCenter, vec3(0.0)))
            || any(greaterThanEqual(cellCenter, vec3(LIGHT_CASTER_SCALE))))
        {
            return 1.0;
        }

        vec3 textureUv = vec3(
            cellCenter.x / LIGHT_CASTER_SCALE,
            cellCenter.y / LIGHT_CASTER_SCALE,
            (casterLayer * LIGHT_CASTER_SCALE + cellCenter.z)
                / (LIGHT_CASTER_SCALE * float(MAX_VOXEL_LIGHTS)));
        float caster = texture(voxelLightCasterMasks, textureUv).r;
        if (caster >= 0.995)
        {
            return 0.0;
        }
        if (caster > 0.0)
        {
            // The CPU stores measured alpha coverage from the authored
            // cage texture. Glass contributes zero; metal bars attenuate
            // exactly by their subvoxel coverage and stay temporally stable.
            return 1.0 - caster;
        }
    }

    return 1.0;
}

float traceFineBlockVisibility(
    vec3 rayOrigin,
    vec3 worldRayDirection,
    float entryDistance,
    float exitDistance)
{
    vec3 occupancySize = voxelSize * occupancyScale;
    float safeEntry = max(entryDistance + 0.001, 0.0);
    float safeExit = max(exitDistance - 0.001, safeEntry);
    vec3 gridOrigin = (rayOrigin + worldRayDirection * safeEntry - voxelOrigin)
        * occupancyScale;
    vec3 gridTarget = (rayOrigin + worldRayDirection * safeExit - voxelOrigin)
        * occupancyScale;
    vec3 rayVector = gridTarget - gridOrigin;
    float maximumDistance = length(rayVector);
    if (maximumDistance < 0.001)
    {
        vec3 sampleCenter = floor(gridOrigin) + vec3(0.5);
        return texture(voxelOccupancy, sampleCenter / occupancySize).r >= 0.50
            ? 0.0
            : 1.0;
    }

    vec3 fineDirection = rayVector / maximumDistance;
    vec3 cell = floor(gridOrigin);
    vec3 targetCell = floor(gridTarget);
    vec3 stepDirection = sign(fineDirection);
    vec3 inverseDirection = 1.0 / max(abs(fineDirection), vec3(0.00001));
    vec3 nextBoundary = mix(cell, cell + vec3(1.0), greaterThan(stepDirection, vec3(0.0)));
    vec3 sideDistance = abs((nextBoundary - gridOrigin) * inverseDirection);
    vec3 deltaDistance = inverseDirection;
    sideDistance = mix(sideDistance, vec3(1e20), lessThan(abs(fineDirection), vec3(0.00001)));

    // A segment crossing one 4x block can visit at most 10 fine cells
    // (including edge/corner ties). Twelve keeps the loop bounded with
    // headroom while retaining the exact non-cubic silhouette.
    for (int stepIndex = 0; stepIndex < 12; stepIndex++)
    {
        vec3 cellCenter = cell + vec3(0.5);
        if (texture(voxelOccupancy, cellCenter / occupancySize).r >= 0.50)
        {
            return 0.0;
        }
        if (all(equal(cell, targetCell)))
        {
            return 1.0;
        }

        float traveled = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
        // Edge/corner ties have zero volume in the skipped axis-aligned
        // neighbours. Visiting them would thicken anvils, fences and other
        // non-cubic blockers by one fine cell.
        if (sideDistance.x <= traveled + 0.00001)
        {
            sideDistance.x += deltaDistance.x;
            cell.x += stepDirection.x;
        }
        if (sideDistance.y <= traveled + 0.00001)
        {
            sideDistance.y += deltaDistance.y;
            cell.y += stepDirection.y;
        }
        if (sideDistance.z <= traveled + 0.00001)
        {
            sideDistance.z += deltaDistance.z;
            cell.z += stepDirection.z;
        }

        if (traveled >= maximumDistance)
        {
            return 1.0;
        }
    }

    return 1.0;
}

float traceVoxelVisibility(vec3 rayOrigin, vec3 rayTarget, int lightIndex)
{
    vec3 gridOrigin = rayOrigin - voxelOrigin;
    vec3 gridTarget = rayTarget - voxelOrigin;
    vec3 originBlock = floor(gridOrigin);
    vec3 targetBlock = floor(gridTarget);
    vec3 rayVector = gridTarget - gridOrigin;
    float maximumDistance = length(rayVector);
    if (maximumDistance < 0.001)
    {
        return 1.0;
    }

    vec3 rayDirection = rayVector / maximumDistance;
    vec3 cell = floor(gridOrigin);
    vec3 stepDirection = sign(rayDirection);
    vec3 inverseDirection = 1.0 / max(abs(rayDirection), vec3(0.00001));
    vec3 nextBoundary = mix(cell, cell + vec3(1.0), greaterThan(stepDirection, vec3(0.0)));
    vec3 sideDistance = abs((nextBoundary - gridOrigin) * inverseDirection);
    vec3 deltaDistance = inverseDirection;
    sideDistance = mix(sideDistance, vec3(1e20), lessThan(abs(rayDirection), vec3(0.00001)));

    // Hierarchical DDA: empty space is crossed at one cell per block.
    // Only a block containing traceable material descends into the 4x
    // occupancy volume, preserving anvils, roofs and modded meshes.
    for (int stepIndex = 0; stepIndex < MAX_VOXEL_STEPS; stepIndex++)
    {
        float traveled = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
        // A simultaneous boundary crossing reaches the diagonal block
        // directly. Sampling intermediate zero-width cells inflates a caster
        // by a whole block and is especially visible around point lights.
        if (sideDistance.x <= traveled + 0.00001)
        {
            sideDistance.x += deltaDistance.x;
            cell.x += stepDirection.x;
        }
        if (sideDistance.y <= traveled + 0.00001)
        {
            sideDistance.y += deltaDistance.y;
            cell.y += stepDirection.y;
        }
        if (sideDistance.z <= traveled + 0.00001)
        {
            sideDistance.z += deltaDistance.z;
            cell.z += stepDirection.z;
        }

        if (traveled >= maximumDistance - 0.105)
        {
            return traceLightCasterVisibility(rayOrigin, rayTarget, lightIndex);
        }

        vec3 cellCenter = cell + vec3(0.5);
        if (any(lessThan(cellCenter, vec3(0.0)))
            || any(greaterThanEqual(cellCenter, voxelSize)))
        {
            return 1.0;
        }

        if (all(equal(cell, targetBlock)))
        {
            return traceLightCasterVisibility(rayOrigin, rayTarget, lightIndex);
        }

        // Coarse occupancy cannot represent a lantern cage. Ignore the
        // complete emitter block here; its texture-aware fine mask is
        // traversed separately at the end of the ray.
        if (all(equal(cell, originBlock))
            || (lightIndex >= 0 && all(equal(cell, targetBlock))))
        {
            continue;
        }

        float material = texture(voxelVolume, cellCenter / voxelSize).a;
        if (material >= 0.45)
        {
            float exitDistance = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
            if (traceFineBlockVisibility(
                rayOrigin,
                rayDirection,
                traveled,
                min(exitDistance, maximumDistance)) < 0.5)
            {
                return 0.0;
            }
        }
    }

    return traceLightCasterVisibility(rayOrigin, rayTarget, lightIndex);
}

bool sunMaskOccupied(uvec2 packedMask, int bitIndex)
{
    uint word = bitIndex < 32 ? packedMask.x : packedMask.y;
    uint bit = 1u << uint(bitIndex & 31);
    return (word & bit) != 0u;
}

float traceSunMaskCellVisibility(
    vec3 rayOrigin,
    vec3 rayDirection,
    ivec3 coarseCell,
    float entryDistance,
    float exitDistance,
    uvec2 packedMask)
{
    float subcellSize = sunOccupancyScale * 0.25;
    vec3 cellWorldMin = sunVoxelOrigin + vec3(coarseCell) * sunOccupancyScale;
    float currentDistance = max(entryDistance + 0.0005, 0.0);
    vec3 localPosition = clamp(
        (rayOrigin + rayDirection * currentDistance - cellWorldMin) / subcellSize,
        vec3(0.0),
        vec3(3.9999));
    ivec3 subcell = ivec3(floor(localPosition));
    ivec3 stepDirection = ivec3(sign(rayDirection));

    // A line can cross at most ten cells in a 4-cubed grid. Twelve iterations
    // leave room for exact edge/corner ties while retaining a fixed GPU budget.
    for (int stepIndex = 0; stepIndex < 12; stepIndex++)
    {
        if (any(lessThan(subcell, ivec3(0)))
            || any(greaterThanEqual(subcell, ivec3(4))))
        {
            return 1.0;
        }

        int bitIndex = (subcell.z * 4 + subcell.y) * 4 + subcell.x;
        if (sunMaskOccupied(packedMask, bitIndex))
        {
            return 0.0;
        }

        vec3 nextBoundaryIndex = vec3(subcell) + vec3(
            stepDirection.x > 0 ? 1.0 : 0.0,
            stepDirection.y > 0 ? 1.0 : 0.0,
            stepDirection.z > 0 ? 1.0 : 0.0);
        vec3 nextBoundaryWorld = cellWorldMin + nextBoundaryIndex * subcellSize;
        vec3 boundaryDistance = vec3(1e20);
        if (abs(rayDirection.x) > 0.00001)
        {
            boundaryDistance.x = (nextBoundaryWorld.x - rayOrigin.x) / rayDirection.x;
        }
        if (abs(rayDirection.y) > 0.00001)
        {
            boundaryDistance.y = (nextBoundaryWorld.y - rayOrigin.y) / rayDirection.y;
        }
        if (abs(rayDirection.z) > 0.00001)
        {
            boundaryDistance.z = (nextBoundaryWorld.z - rayOrigin.z) / rayDirection.z;
        }

        float nextDistance = min(
            boundaryDistance.x,
            min(boundaryDistance.y, boundaryDistance.z));
        if (nextDistance >= exitDistance - 0.0005)
        {
            return 1.0;
        }

        // Advance every tied axis. This avoids visiting zero-width neighbors
        // and keeps diagonal roof silhouettes from inflating at cell corners.
        if (boundaryDistance.x <= nextDistance + 0.00001)
        {
            subcell.x += stepDirection.x;
        }
        if (boundaryDistance.y <= nextDistance + 0.00001)
        {
            subcell.y += stepDirection.y;
        }
        if (boundaryDistance.z <= nextDistance + 0.00001)
        {
            subcell.z += stepDirection.z;
        }
        currentDistance = nextDistance;
    }

    return currentDistance >= exitDistance - 0.0005 ? 1.0 : 0.0;
}

float traceSunClipmapVisibility(vec3 rayOrigin, vec3 rayDirection, float maximumDistance)
{
    // The sun clipmap covers 152x116x152 world blocks independently of
    // the camera-centred material volume. Each texel conservatively
    // unions a 2x2x2 block group, retaining the former ~32 fetch budget
    // while making the configured 96-block range physically available.
    vec3 gridOrigin = (rayOrigin - sunVoxelOrigin) / sunOccupancyScale;
    if (any(lessThan(gridOrigin, vec3(0.0)))
        || any(greaterThanEqual(gridOrigin, sunVoxelSize)))
    {
        // Never turn an untraced interval into a falsely rounded,
        // suddenly illuminated shadow terminator.
        return 0.0;
    }

    vec3 cell = floor(gridOrigin);
    vec3 coarseDirection = rayDirection / sunOccupancyScale;
    vec3 stepDirection = sign(coarseDirection);
    vec3 inverseDirection = 1.0 / max(abs(coarseDirection), vec3(0.00001));
    vec3 nextBoundary = mix(cell, cell + vec3(1.0), greaterThan(stepDirection, vec3(0.0)));
    vec3 sideDistance = abs((nextBoundary - gridOrigin) * inverseDirection);
    vec3 deltaDistance = inverseDirection;
    sideDistance = mix(sideDistance, vec3(1e20), lessThan(abs(coarseDirection), vec3(0.00001)));
    for (int stepIndex = 0; stepIndex < MAX_SUN_STEPS; stepIndex++)
    {
        float traveled = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
        // Advance all axes that share a corner/edge crossing. Visiting
        // the zero-width neighbor cells one axis at a time inflated
        // diagonal roofs and rounded their projected silhouettes.
        if (sideDistance.x <= traveled + 0.00001)
        {
            sideDistance.x += deltaDistance.x;
            cell.x += stepDirection.x;
        }
        if (sideDistance.y <= traveled + 0.00001)
        {
            sideDistance.y += deltaDistance.y;
            cell.y += stepDirection.y;
        }
        if (sideDistance.z <= traveled + 0.00001)
        {
            sideDistance.z += deltaDistance.z;
            cell.z += stepDirection.z;
        }

        if (traveled >= maximumDistance)
        {
            return 1.0;
        }

        vec3 cellCenter = cell + vec3(0.5);
        if (any(lessThan(cellCenter, vec3(0.0)))
            || any(greaterThanEqual(cellCenter, sunVoxelSize)))
        {
            // The ray has not covered maximumDistance. Keep the
            // result conservative instead of manufacturing visibility
            // at the clipmap boundary.
            return 0.0;
        }

        ivec3 coarseCell = ivec3(cell);
        uvec2 occupancyMask = texelFetch(voxelSunOccupancy, coarseCell, 0).rg;
        if (any(notEqual(occupancyMask, uvec2(0u))))
        {
            float exitDistance = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
            if (traceSunMaskCellVisibility(
                    rayOrigin,
                    rayDirection,
                    coarseCell,
                    traveled,
                    min(exitDistance, maximumDistance),
                    occupancyMask) < 0.5)
            {
                return 0.0;
            }
        }
    }

    // MAX_SUN_STEPS must never silently shorten the configured range.
    return 0.0;
}

float traceCoarseSunVisibility(vec3 rayOrigin, vec3 rayDirection, float maximumDistance)
{
    return traceSunClipmapVisibility(rayOrigin, rayDirection, maximumDistance);
}

float traceVoxelSunVisibility(vec3 rayOrigin)
{
    float fineDistance = min(sunFineShadowDistance, sunShadowDistance);
    if (fineDistance > 0.01)
    {
        vec3 fineTarget = rayOrigin + sunDirection * fineDistance;
        if (!isInsideVoxelVolume(fineTarget))
        {
            // The fine clipmap cannot cover this complete interval.
            // Let the larger conservative sun clipmap trace it from the
            // receiver rather than leaving an untested gap.
            fineDistance = 0.0;
        }
        else if (traceVoxelVisibility(rayOrigin, fineTarget, -1) < 0.5)
        {
            return 0.0;
        }
    }

    float remainingDistance = sunShadowDistance - fineDistance;
    if (remainingDistance <= 0.01)
    {
        return 1.0;
    }

    vec3 coarseOrigin = rayOrigin + sunDirection * max(fineDistance - 0.45, 0.0);
    return traceCoarseSunVisibility(coarseOrigin, sunDirection, remainingDistance + 0.45);
}

float sampleNativeShadowFar(vec3 coordinate)
{
    vec2 texel = 1.0 / vec2(textureSize(nativeShadowMapFar, 0));
    float comparisonDepth = coordinate.z - 0.0009;
    float visibility = 0.0;
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            visibility += texture(
                nativeShadowMapFar,
                vec3(coordinate.xy + vec2(x, y) * texel, comparisonDepth));
        }
    }
    return visibility / 9.0;
}

float sampleNativeShadowNear(vec3 coordinate)
{
    vec2 texel = 1.0 / vec2(textureSize(nativeShadowMapNear, 0));
    float comparisonDepth = coordinate.z - 0.0005;
    float visibility = 0.0;
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            visibility += texture(
                nativeShadowMapNear,
                vec3(coordinate.xy + vec2(x, y) * texel, comparisonDepth));
        }
    }
    return visibility / 9.0;
}

float nativeShadowNearWeight(vec3 coordinate, float receiverDistance)
{
    float transition = clamp(
        max(max(0.0, 0.03 - coordinate.x) * 100.0,
            max(0.0, coordinate.x - 0.97) * 100.0)
        + max(max(0.0, 0.03 - coordinate.y) * 100.0,
            max(0.0, coordinate.y - 0.97) * 100.0)
        + max(0.0, coordinate.z - 0.98) * 100.0
        + max(0.0, receiverDistance / nativeShadowRangeNear - 0.15),
        0.0,
        1.0);
    float weight = clamp(1.0 - transition, 0.0, 1.0);
    return coordinate.z >= 0.999 ? 0.0 : weight;
}

float nativeShadowFarWeight(
    vec3 coordinate,
    float receiverDistance,
    float nearWeight)
{
    float transition = clamp(
        max(max(0.0, 0.03 - coordinate.x) * 10.0,
            max(0.0, coordinate.x - 0.97) * 10.0)
        + max(max(0.0, 0.03 - coordinate.y) * 10.0,
            max(0.0, coordinate.y - 0.97) * 10.0)
        + max(0.0, coordinate.z - 0.98) * 10.0
        + max(0.0, receiverDistance / nativeShadowRangeFar - 0.15),
        0.0,
        1.0);
    transition = transition * 2.0 - 0.5;
    float weight = max(
        0.0,
        clamp(1.0 - transition, 0.0, 1.0) - nearWeight);
    return coordinate.z >= 0.999 ? 0.0 : weight;
}

float nativeShadowRayCoverage(
    mat4 shadowMatrix,
    vec3 coordinate)
{
    // Solar cascades are orthographic. Projecting the world-space direction
    // with w=0 therefore gives texture-coordinate change per world metre and
    // lets the long-range voxel tail begin exactly where this cascade exits.
    vec3 coordinateDirection = (shadowMatrix * vec4(sunDirection, 0.0)).xyz;
    vec3 lowerDistance = vec3(sunShadowDistance);
    vec3 upperDistance = vec3(sunShadowDistance);
    for (int axis = 0; axis < 3; axis++)
    {
        if (coordinateDirection[axis] > 0.000001)
        {
            upperDistance[axis] = (0.9985 - coordinate[axis])
                / coordinateDirection[axis];
        }
        else if (coordinateDirection[axis] < -0.000001)
        {
            lowerDistance[axis] = coordinate[axis]
                / -coordinateDirection[axis];
        }
    }
    float coverage = min(
        min(lowerDistance.x, min(lowerDistance.y, lowerDistance.z)),
        min(upperDistance.x, min(upperDistance.y, upperDistance.z)));
    return clamp(coverage, 0.0, sunShadowDistance);
}

void traceNativeSunShadowVisibility(
    vec3 receiverRelativeWorldPosition,
    out float visibility,
    out float support,
    out float coveredDistance)
{
    // Reproduce the engine's near/far cascade transition with the true,
    // unbiased receiver. Native depth is the only representation here that
    // preserves alpha-tested crossed plants and their wind deformation.
    float nearWeight = 0.0;
    float farWeight = 0.0;
    float weightedVisibility = 0.0;
    coveredDistance = 0.0;

    if (nativeShadowNearEnabled != 0 && nativeShadowRangeNear > 0.0)
    {
        vec3 relativeReceiver = receiverRelativeWorldPosition
            - nativeShadowReferenceOffsetNear;
        vec3 coordinate = (nativeShadowMatrixNear
            * vec4(relativeReceiver, 1.0)).xyz;
        nearWeight = nativeShadowNearWeight(
            coordinate,
            length(vec4(relativeReceiver, 1.0)));
        if (nearWeight > 0.0)
        {
            weightedVisibility += sampleNativeShadowNear(coordinate) * nearWeight;
            coveredDistance = max(
                coveredDistance,
                nativeShadowRayCoverage(
                    nativeShadowMatrixNear,
                    coordinate));
        }
    }

    if (nativeShadowFarEnabled != 0 && nativeShadowRangeFar > 0.0)
    {
        vec3 relativeReceiver = receiverRelativeWorldPosition
            - nativeShadowReferenceOffsetFar;
        vec3 coordinate = (nativeShadowMatrixFar
            * vec4(relativeReceiver, 1.0)).xyz;
        farWeight = nativeShadowFarWeight(
            coordinate,
            length(vec4(relativeReceiver, 1.0)),
            nearWeight);
        if (farWeight > 0.0)
        {
            weightedVisibility += sampleNativeShadowFar(coordinate) * farWeight;
            coveredDistance = max(
                coveredDistance,
                nativeShadowRayCoverage(
                    nativeShadowMatrixFar,
                    coordinate));
        }
    }

    support = clamp(nearWeight + farWeight, 0.0, 1.0);
    visibility = support > 0.0001
        ? clamp(weightedVisibility / support, 0.0, 1.0)
        : 1.0;
}

float traceVoxelSunTail(vec3 rayOrigin, float coveredDistance)
{
    float tailStart = clamp(coveredDistance, 0.0, sunShadowDistance);
    float remainingDistance = sunShadowDistance - tailStart;
    if (remainingDistance <= 0.01)
    {
        return 1.0;
    }
    vec3 coarseOrigin = rayOrigin
        + sunDirection * max(tailStart - 0.45, 0.0);
    return traceCoarseSunVisibility(
        coarseOrigin,
        sunDirection,
        remainingDistance + 0.45);
}

float traceSunVisibility(
    vec3 receiverRelativeWorldPosition,
    vec3 voxelRayOrigin)
{
    float nativeVisibility;
    float nativeSupport;
    float nativeCoveredDistance;
    traceNativeSunShadowVisibility(
        receiverRelativeWorldPosition,
        nativeVisibility,
        nativeSupport,
        nativeCoveredDistance);

    if (nativeSupport <= 0.0001)
    {
        return traceVoxelSunVisibility(voxelRayOrigin);
    }

    // Within a supported cascade, the native alpha-tested geometry is
    // authoritative. The conservative voxel representation resumes only
    // beyond the exact cascade exit, retaining the independent 96 m reach.
    float nativeAndTailVisibility = min(
        nativeVisibility,
        traceVoxelSunTail(voxelRayOrigin, nativeCoveredDistance));
    if (nativeSupport >= 0.9999)
    {
        return nativeAndTailVisibility;
    }

    return mix(
        traceVoxelSunVisibility(voxelRayOrigin),
        nativeAndTailVisibility,
        nativeSupport);
}

vec3 skyRadianceColor()
{
    float daylight = clamp(sunColorStrength.w, 0.0, 1.0);
    vec3 daylightSky = srgbToLinear(mix(
        vec3(0.48, 0.58, 0.72),
        clamp(sunColorStrength.rgb, 0.0, 1.0),
        0.45));
    return mix(vec3(0.004, 0.008, 0.022), daylightSky, daylight)
        * (0.18 + daylight * 0.94);
}

vec3 skyEnvironmentRadiance(vec3 worldDirection)
{
    // A planar reflection can legitimately leave the framebuffer. Keep
    // a directional environment for those rays rather than a flat grey
    // fallback: elevation supplies the horizon/zenith gradient and the
    // same sun vector used by the transport adds a bounded solar lobe.
    vec3 direction = normalize(worldDirection);
    float daylight = clamp(sunColorStrength.w, 0.0, 1.0);
    float elevation = clamp(direction.y, 0.0, 1.0);
    vec3 baseSky = skyRadianceColor();
    vec3 horizon = baseSky * mix(
        vec3(0.72, 0.76, 0.82),
        vec3(0.88, 0.94, 1.02),
        daylight);
    vec3 zenith = baseSky * mix(
        vec3(0.45, 0.58, 0.92),
        vec3(0.54, 0.76, 1.18),
        daylight);
    vec3 environment = mix(
        horizon,
        zenith,
        pow(elevation, 0.58));
    float solarAlignment = max(dot(direction, normalize(sunDirection)), 0.0);
    float sunDisk = pow(solarAlignment, 384.0) * daylight;
    float sunHalo = pow(solarAlignment, 24.0) * daylight * 0.08;
    environment += srgbToLinear(clamp(sunColorStrength.rgb, 0.0, 1.0))
        * (sunDisk * 2.6 + sunHalo);
    return environment;
}

float decodeFluidSurfaceWorldY(vec4 columnData)
{
    // FluidSurface.r is a non-zero sentinel encoding local block Y + 1,
    // while B is Vintage Story's exact three-bit LiquidLevel. Entity physics
    // defines the free surface as blockY + LiquidLevel / 8.
    float encodedTopCell = floor(columnData.r * 255.0 + 0.5);
    float topLocalY = encodedTopCell - 1.0;
    float liquidLevel = clamp(
        floor(columnData.b * 255.0 + 0.5),
        0.0,
        7.0);
    return voxelOrigin.y + topLocalY + liquidLevel / 8.0;
}

float screenFluidEvidence(vec3 viewPosition)
{
    if (dot(viewPosition, viewPosition) < 0.0001)
    {
        return 0.0;
    }

    vec3 worldPosition = (
        inverseViewMatrix * vec4(viewPosition, 1.0)).xyz
        + floatingWorldOrigin;
    vec2 fluidSurfaceLocalPosition = worldPosition.xz - fluidSurfaceOrigin;
    bool insideFluidColumns = all(greaterThanEqual(
            fluidSurfaceLocalPosition,
            vec2(0.0)))
        && all(lessThan(fluidSurfaceLocalPosition, fluidSurfaceSize));
    if (!insideFluidColumns)
    {
        return 0.0;
    }

    vec4 fluidColumnData = texture(
        voxelFluidSurface,
        (floor(fluidSurfaceLocalPosition) + vec2(0.5)) / fluidSurfaceSize);
    float encodedSurface = fluidColumnData.r;
    float surfaceWorldY = decodeFluidSurfaceWorldY(fluidColumnData);
    // Some renderers expose the transparent water plane itself in the
    // G-buffer while others expose its bed. Reject both: transmission
    // already carries underwater geometry and recursively sampling the
    // water plane produces a flat cyan mirror.
    return step(0.5 / 255.0, encodedSurface)
        * step(worldPosition.y - 0.05, surfaceWorldY);
}

float firstPersonOverlayEvidence(vec3 viewPosition)
{
    float squaredDistance = dot(viewPosition, viewPosition);
    if (squaredDistance < 0.0001)
    {
        return 0.0;
    }

    // Vintage Story's stock helditem.fsh deliberately writes vec4(1) to
    // outGPosition instead of a physical view position. Use only that explicit
    // sentinel: EntityItem and nearby foliage write real positions and must not
    // disappear merely because they are less than one metre from the camera.
    float heldItemSentinel = 1.0 - smoothstep(
        0.002,
        0.080,
        length(viewPosition - vec3(1.0)));
    return heldItemSentinel;
}

float deferredFirstPersonOverlayEvidence(
    vec2 sampleUv,
    vec3 cleanViewPosition)
{
    vec3 directViewPosition = texture(gDirectPosition, sampleUv).xyz;
    float directDistanceSquared = dot(directViewPosition, directViewPosition);
    if (directDistanceSquared < 0.0001)
    {
        return 0.0;
    }

    // The clean late-opaque snapshot and live Primary differ only where the
    // deferred local-player draw (or a later held-item renderer) overwrote the
    // world G-buffer. The near-camera gate is therefore exact here and cannot
    // reject an ordinary EntityItem that already exists in both buffers.
    float cleanDistanceSquared = dot(cleanViewPosition, cleanViewPosition);
    float positionDifference = cleanDistanceSquared < 0.0001
        ? 1.0
        : length(directViewPosition - cleanViewPosition);
    float overwrittenWorld = smoothstep(0.015, 0.080, positionDifference);
    float nearCameraGeometry = 1.0 - smoothstep(
        0.80,
        1.80,
        sqrt(directDistanceSquared));
    return overwrittenWorld * max(
        nearCameraGeometry,
        firstPersonOverlayEvidence(directViewPosition));
}

float screenBelowReflectingSurfaceEvidence(
    vec3 viewPosition,
    float reflectingSurfaceWorldY)
{
    if (dot(viewPosition, viewPosition) < 0.0001)
    {
        return 0.0;
    }

    vec3 sourceWorldPosition = (
        inverseViewMatrix * vec4(viewPosition, 1.0)).xyz
        + floatingWorldOrigin;
    // Submerged objects belong to transmission/refraction, never to the
    // incident radiance reflected by the air-liquid interface.
    return step(
        sourceWorldPosition.y + 0.02,
        reflectingSurfaceWorldY);
}

bool projectToScreen(vec3 viewPosition, out vec2 screenUv);

vec4 maskedPlanarFallbackSample(
    vec2 sourceUv,
    float reflectingSurfaceWorldY)
{
    vec2 fallbackUv = clamp(
        sourceUv,
        inverseFrameSize * 0.5,
        vec2(1.0) - inverseFrameSize * 0.5);
    vec3 fallbackViewPosition = opaquePositionEnabled != 0
        ? texture(gOpaquePosition, fallbackUv).xyz
        : texture(gPosition, fallbackUv).xyz;
    // The colour source was captured before held-item rendering. Do not gate
    // its stable scenery fallback on the later live G-buffer: alpha-tested
    // foliage and OIT silhouettes may have colour without a trustworthy
    // position, while the held arm's sentinel can cover valid world colour
    // behind it. Use the position copied in the same transaction as the colour
    // so one leaf plane cannot be masked by a later liquid/entity attachment.
    // Fluid/submerged depth remains rejected when it is available.
    float fallbackRejectedGeometry = max(
        screenFluidEvidence(fallbackViewPosition),
        screenBelowReflectingSurfaceEvidence(
            fallbackViewPosition,
            reflectingSurfaceWorldY));
    float fallbackSupport = 1.0 - fallbackRejectedGeometry;
    return vec4(
        sampleReflectionSource(fallbackUv) * fallbackSupport,
        fallbackSupport);
}

vec4 filteredMaskedPlanarFallbackSample(
    vec2 sourceUv,
    float reflectingSurfaceWorldY,
    float interfaceRoughness)
{
    // One displaced point sample aliases a continuous microfacet footprint
    // into horizontal strips. Integrate a compact vertical kernel because a
    // planar water reflection maps vertical source structure predominantly
    // along screen Y. Every tap retains the submerged/fluid rejection of the
    // exact masked sampler, so smoothing cannot resurrect underwater entities.
    float filterRadiusPixels = mix(
        1.50,
        2.75,
        clamp(interfaceRoughness, 0.0, 1.0));
    vec2 filterAxis = vec2(
        0.0,
        inverseFrameSize.y * filterRadiusPixels);
    vec4 filtered = maskedPlanarFallbackSample(
            sourceUv,
            reflectingSurfaceWorldY)
        * 0.30;
    filtered += maskedPlanarFallbackSample(
            sourceUv + filterAxis,
            reflectingSurfaceWorldY)
        * 0.22;
    filtered += maskedPlanarFallbackSample(
            sourceUv - filterAxis,
            reflectingSurfaceWorldY)
        * 0.22;
    filtered += maskedPlanarFallbackSample(
            sourceUv + filterAxis * 2.0,
            reflectingSurfaceWorldY)
        * 0.13;
    filtered += maskedPlanarFallbackSample(
            sourceUv - filterAxis * 2.0,
            reflectingSurfaceWorldY)
        * 0.13;
    return filtered;
}

float sampleRainExposure(vec3 worldPosition, vec3 worldNormal)
{
    if (voxelLightingEnabled == 0 || rainWetness <= 0.001)
    {
        return 0.0;
    }

    vec2 localPosition = worldPosition.xz - rainSurfaceOrigin;
    bool insideRainSurface = localPosition.x >= 0.0
        && localPosition.y >= 0.0
        && localPosition.x < rainSurfaceSize.x
        && localPosition.y < rainSurfaceSize.y;
    if (!insideRainSurface)
    {
        return 0.0;
    }

    vec2 rainUv = (floor(localPosition) + vec2(0.5)) / rainSurfaceSize;
    float rainBlockY = texture(voxelRainSurface, rainUv).r;
    // GetRainMapHeightAt returns the block-coordinate Y of the highest
    // non-rain-permeable block. Vintage Story's public consumers treat a
    // receiver at that same Y as exposed (rainY > receiverY means covered),
    // so adding almost one complete block here incorrectly dries the actual
    // terrain carrier. A narrow band absorbs interpolated mesh/float error;
    // columns below a roof remain separated by whole blocks and stay dry.
    float rainReceiverY = worldPosition.y + worldNormal.y * 0.035;
    float heightExposure = smoothstep(
        rainBlockY - 0.08,
        rainBlockY + 0.08,
        rainReceiverY);
    float upwardExposure = smoothstep(0.50, 0.92, worldNormal.y);
    return heightExposure * upwardExposure;
}

vec3 skyProbeDirection(int rayIndex)
{
    // Two coherent, world-stable upper-hemisphere probes expose
    // roofs and room openings without the screen-space light leaks of a
    // depth-only ambient term. The vertical probe preserves stability;
    // the oblique probe discovers side-facing skylight. Do not rotate this
    // set per frame: with one or two rays the rotation changes the measured
    // aperture itself and is perceived as moving ambient light.
    float vertical = rayIndex == 0 ? 0.92 : 0.56;
    float radial = sqrt(max(1.0 - vertical * vertical, 0.0));
    float angle = float(rayIndex) * 2.39996322973;
    return normalize(vec3(cos(angle) * radial, vertical, sin(angle) * radial));
}

float traceSkyRay(vec3 rayOrigin, vec3 rayDirection)
{
    // Fixed-distance samples could step over a roof at a particular
    // block phase and turn texture-tile boundaries into bright lines.
    // Reuse the hierarchical DDA sky visibility path so every crossed
    // voxel is inspected and nonstandard roofs use fine occupancy.
    float maximumDistance = 0.42
        + float(max(skyTraceSteps, 1)) * 0.82;
    vec3 rayTarget = rayOrigin + rayDirection * maximumDistance;
    return traceVoxelVisibility(rayOrigin, rayTarget, -1);
}

vec3 traceVoxelSkyLighting(
    vec3 worldPosition,
    vec3 worldNormal,
    vec3 worldGeometricNormal,
    out float averageVisibility)
{
    averageVisibility = 0.0;
    if (skyRayCount <= 0 || sunColorStrength.w < 0.08)
    {
        // At night, and at the Performance tier, the authored ambient
        // contribution remains in the raster carrier. Performance also owns
        // a directional irradiance cache plus the long-range solar visibility
        // bank, so a per-pixel local DDA would duplicate its stable sky LOD.
        return vec3(0.0);
    }
    vec3 rayOrigin = worldPosition
        + worldGeometricNormal * 0.14
        + vec3(0.0, 0.035, 0.0);
    float irradiance = 0.0;
    float visibility = 0.0;
    int sampleCount = clamp(skyRayCount, 1, MAX_SKY_RAYS);
    for (int rayIndex = 0; rayIndex < MAX_SKY_RAYS; rayIndex++)
    {
        if (rayIndex >= sampleCount)
        {
            break;
        }

        vec3 rayDirection = skyProbeDirection(rayIndex);
        float sampleVisibility = traceSkyRay(rayOrigin, rayDirection);
        float receiver = max(dot(worldNormal, rayDirection), 0.0);
        irradiance += sampleVisibility * receiver;
        visibility += sampleVisibility;
    }

    float inverseSampleCount = 1.0 / float(sampleCount);
    averageVisibility = visibility * inverseSampleCount;
    // A small horizon term represents the remainder of the sky dome;
    // it is still multiplied by traced visibility and cannot illuminate
    // a sealed room as the previous unoccluded ambient overlay did.
    float horizonTerm = averageVisibility
        * (0.035 + 0.075 * (1.0 - abs(worldNormal.y)));
    return skyRadianceColor()
        * (irradiance * inverseSampleCount + horizonTerm);
}

vec3 samplePointLightSurface(
    vec3 lightPosition,
    vec3 lightDirection,
    int lightIndex,
    int sampleIndex,
    int sampleCount)
{
    vec2 sourceHalfSize = voxelLightPhotometry[lightIndex].xy;
    if (max(sourceHalfSize.x, sourceHalfSize.y) <= 0.001)
    {
        sourceHalfSize = vec2(pointLightSourceRadius);
    }
    if (max(sourceHalfSize.x, sourceHalfSize.y) <= 0.001)
    {
        return lightPosition;
    }

    // Model the flame as a vertical ellipsoid. Its projected support in the
    // receiver/source plane follows orthographic ellipsoid projection: a
    // horizontal ray sees the complete flame height, while a ray parallel to
    // the flame axis sees only its radial width. Keeping the full height for
    // every ray exaggerated cage penumbrae on floors and ceilings.
    vec3 projectedVertical = vec3(0.0, 1.0, 0.0)
        - lightDirection * lightDirection.y;
    if (dot(projectedVertical, projectedVertical) <= 0.000001)
    {
        projectedVertical = vec3(0.0, 0.0, 1.0);
    }
    vec3 verticalAxis = normalize(projectedVertical);
    vec3 horizontalAxis = normalize(cross(verticalAxis, lightDirection));
    float rayVerticalSquared = clamp(
        lightDirection.y * lightDirection.y,
        0.0,
        1.0);
    float projectedHalfHeight = sqrt(
        sourceHalfSize.y * sourceHalfSize.y * (1.0 - rayVerticalSquared)
        + sourceHalfSize.x * sourceHalfSize.x * rayVerticalSquared);

    // Keep the finite emitter centred for every supported sample count.
    // A rotating one-sample disk literally moves the apparent light source;
    // an unpaired Vogel prefix shifts its centroid as adaptive quality changes.
    // One ray therefore uses the physical centre. Larger sets use fixed,
    // diametrically opposed pairs, plus the centre for odd counts. This is a
    // deterministic quadrature of the same disk and cannot flicker between frames.
    if (sampleCount <= 1)
    {
        return lightPosition;
    }

    int centreSampleCount = sampleCount & 1;
    if (centreSampleCount != 0 && sampleIndex == 0)
    {
        return lightPosition;
    }

    int pairedSampleIndex = sampleIndex - centreSampleCount;
    int pairIndex = pairedSampleIndex / 2;
    int pairCount = max((sampleCount - centreSampleCount) / 2, 1);
    float pairSide = float(pairedSampleIndex & 1) * 3.14159265359;
    float angle = (float(pairIndex) + 0.5) * 2.39996322973
        + float(lightIndex) * 1.32471795724
        + pairSide;
    float radius = sqrt((float(pairIndex) + 0.5) / float(pairCount));
    return lightPosition
        + horizontalAxis * cos(angle) * radius * sourceHalfSize.x
        + verticalAxis * sin(angle) * radius * projectedHalfHeight;
}

const float PHOTOMETRIC_LUX_TO_RENDERER_RADIANCE = 0.35014087;

float physicalLightRange(int lightIndex, float intensityCandela, float configuredRange)
{
    float cutoffIlluminanceLux = max(
        voxelLightPhotometry[lightIndex].z,
        0.000001);
    float cutoffRange = sqrt(max(intensityCandela, 0.0) / cutoffIlluminanceLux);
    return min(configuredRange, cutoffRange);
}

float photometricRangeFade(float distanceMetres, float lightRangeMetres)
{
    // Only the final ten percent is a numerical trace truncation. Illumination
    // within the retained support continues to follow the inverse-square law.
    return 1.0 - smoothstep(
        lightRangeMetres * 0.90,
        lightRangeMetres,
        distanceMetres);
}

float photometricIncidentRadiance(
    int lightIndex,
    float intensityCandela,
    float distanceMetres)
{
    vec2 sourceHalfSize = max(voxelLightPhotometry[lightIndex].xy, vec2(0.0005));
    float nearFieldDistance = max(length(sourceHalfSize), 0.0005);
    float distanceSquared = max(
        distanceMetres * distanceMetres,
        nearFieldDistance * nearFieldDistance);
    // CIE E = I / r^2 on the source axis. Receiver cos(theta) remains at the
    // individual BRDF call site; this helper only transports incident energy.
    return max(intensityCandela, 0.0)
        * PHOTOMETRIC_LUX_TO_RENDERER_RADIANCE
        / distanceSquared;
}

vec3 dominantAxisNormal(vec3 rayDirection)
{
    vec3 absoluteDirection = abs(rayDirection);
    if (absoluteDirection.x >= absoluteDirection.y
        && absoluteDirection.x >= absoluteDirection.z)
    {
        return vec3(-sign(rayDirection.x), 0.0, 0.0);
    }
    if (absoluteDirection.y >= absoluteDirection.z)
    {
        return vec3(0.0, -sign(rayDirection.y), 0.0);
    }
    return vec3(0.0, 0.0, -sign(rayDirection.z));
}

vec3 voxelBounceDirection(vec3 normal, int rayIndex)
{
    vec3 helper = abs(normal.y) < 0.95
        ? vec3(0.0, 1.0, 0.0)
        : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(helper, normal));
    vec3 bitangent = cross(normal, tangent);
    // The radiance cache and temporal denoiser converge a fixed low-discrepancy
    // set. Advancing a one-ray lobe each frame changes indirect-light direction
    // rather than merely reducing noise, producing visible energy pulses.
    float sequence = float(rayIndex) + 0.5;
    float radial = sqrt(fract(sequence * 0.61803398875) * 0.78);
    float angle = sequence * 2.39996322973;
    float vertical = sqrt(max(1.0 - radial * radial, 0.0));
    return normalize(
        tangent * cos(angle) * radial
        + bitangent * sin(angle) * radial
        + normal * vertical);
}

bool traceVoxelBounceSurface(
    vec3 rayOrigin,
    vec3 rayDirection,
    out vec3 hitPosition,
    out vec3 hitNormal,
    out vec3 hitAlbedo,
    out float hitDistance)
{
    for (int stepIndex = 0; stepIndex < MAX_VOXEL_BOUNCE_STEPS; stepIndex++)
    {
        if (stepIndex >= voxelBounceSteps)
        {
            break;
        }
        float progress = float(stepIndex + 1) / float(max(voxelBounceSteps, 1));
        float distanceAlongRay = voxelBounceDistance * pow(progress, 1.25);
        vec3 samplePosition = rayOrigin + rayDirection * distanceAlongRay;
        if (!isInsideVoxelVolume(samplePosition))
        {
            break;
        }

        vec4 material = sampleVoxelAtWorld(samplePosition);
        if (material.a < 0.45)
        {
            continue;
        }

        hitNormal = dominantAxisNormal(rayDirection);
        vec3 hitCell = floor(samplePosition);
        hitPosition = samplePosition;
        // Fixed-distance probing can land near the centre of a solid
        // voxel. Reconstruct the entry face on the dominant axis so
        // the secondary visibility ray starts outside that voxel
        // instead of immediately self-occluding its own bounce.
        if (abs(hitNormal.x) > 0.5)
        {
            hitPosition.x = hitNormal.x > 0.0
                ? hitCell.x + 1.02
                : hitCell.x - 0.02;
        }
        else if (abs(hitNormal.y) > 0.5)
        {
            hitPosition.y = hitNormal.y > 0.0
                ? hitCell.y + 1.02
                : hitCell.y - 0.02;
        }
        else
        {
            hitPosition.z = hitNormal.z > 0.0
                ? hitCell.z + 1.02
                : hitCell.z - 0.02;
        }
        hitAlbedo = material.rgb;
        hitDistance = distanceAlongRay;
        return true;
    }

    hitPosition = vec3(0.0);
    hitNormal = vec3(0.0, 1.0, 0.0);
    hitAlbedo = vec3(0.0);
    hitDistance = voxelBounceDistance;
    return false;
}

float traceCoarseBounceVisibility(vec3 rayOrigin, vec3 rayTarget)
{
    vec3 segment = rayTarget - rayOrigin;
    for (int stepIndex = 0; stepIndex < MAX_VOXEL_BOUNCE_SHADOW_STEPS; stepIndex++)
    {
        if (stepIndex >= voxelBounceShadowSteps)
        {
            break;
        }
        // Do not sample either endpoint: the first is the bounced
        // surface and the last can be an emissive block/cage.
        float progress = float(stepIndex + 1)
            / float(max(voxelBounceShadowSteps, 1) + 1);
        vec3 samplePosition = rayOrigin + segment * progress;
        if (!isInsideVoxelVolume(samplePosition))
        {
            return 1.0;
        }
        if (sampleVoxelAtWorld(samplePosition).a >= 0.45)
        {
            return 0.0;
        }
    }
    return 1.0;
}

vec3 traceVoxelDiffuseBounce(
    vec3 worldPosition,
    vec3 worldNormal,
    vec3 worldGeometricNormal)
{
    if (voxelBounceRayCount <= 0)
    {
        return vec3(0.0);
    }

    vec3 accumulated = vec3(0.0);
    vec3 rayOrigin = worldPosition + worldGeometricNormal * 0.16;
    for (int rayIndex = 0; rayIndex < MAX_VOXEL_BOUNCE_RAYS; rayIndex++)
    {
        if (rayIndex >= voxelBounceRayCount)
        {
            break;
        }

        vec3 rayDirection = voxelBounceDirection(worldNormal, rayIndex);
        vec3 hitPosition;
        vec3 hitNormal;
        vec3 hitAlbedo;
        float hitDistance;
        if (!traceVoxelBounceSurface(
            rayOrigin,
            rayDirection,
            hitPosition,
            hitNormal,
            hitAlbedo,
            hitDistance))
        {
            continue;
        }
        vec3 incident = vec3(0.0);
        float bouncedSunVisibility = 0.0;
        if (skyTraceSteps >= 12)
        {
            vec3 skyDirection = skyProbeDirection(rayIndex);
            float bouncedSkyVisibility = traceSkyRay(
                hitPosition + hitNormal * 0.08,
                skyDirection);
            float bouncedSkyReceiver = max(dot(hitNormal, skyDirection), 0.0);
            incident += skyRadianceColor()
                * bouncedSkyReceiver
                * bouncedSkyVisibility;

            // The same traced opening that admits sky also admits the sun.
            // Balanced/performance tiers reconstruct this secondary sky
            // energy temporally instead of issuing another 3D traversal.
            bouncedSunVisibility = bouncedSkyVisibility;
        }
        else if (sunColorStrength.w >= 0.08)
        {
            // Performance mode retains a genuine indirect sun path.
            // Reuse its bounded coarse visibility budget instead of a
            // second full 64-block DDA; temporal accumulation supplies
            // the missing angular samples without blackening interiors.
            float bounceSunDistance = min(sunShadowDistance, 14.0);
            bouncedSunVisibility = traceCoarseBounceVisibility(
                hitPosition + hitNormal * 0.06,
                hitPosition + sunDirection * bounceSunDistance);
        }
        float bouncedSunReceiver = max(dot(hitNormal, sunDirection), 0.0);
        incident += srgbToLinear(sunColorStrength.rgb)
            * sunColorStrength.w
            * bouncedSunReceiver
            * bouncedSunVisibility
            * 0.72;
        // Trace the two dominant emitters. This covers the common
        // held-light plus lantern case while retaining a fixed upper
        // bound for frame pacing.
        for (int lightIndex = 0; lightIndex < MAX_VOXEL_LIGHTS; lightIndex++)
        {
            if (lightIndex >= voxelLightCount || lightIndex >= 2)
            {
                break;
            }

            vec4 lightPositionIntensity = voxelLightPositionIntensity[lightIndex];
            vec4 lightColorRadius = voxelLightColorRadius[lightIndex];
            vec3 toLight = lightPositionIntensity.xyz - hitPosition;
            float lightDistance = length(toLight);
            float lightRadius = physicalLightRange(
                lightIndex,
                lightPositionIntensity.w,
                min(lightColorRadius.w, pointLightRadius));
            if (lightDistance <= 0.001 || lightDistance >= lightRadius)
            {
                continue;
            }
            vec3 lightDirection = toLight / lightDistance;
            float emitterReceiver = max(dot(hitNormal, lightDirection), 0.0);
            float radiusFade = photometricRangeFade(lightDistance, lightRadius);
            float incidentRadiance = photometricIncidentRadiance(
                lightIndex,
                lightPositionIntensity.w,
                lightDistance);
            float visibility = traceCoarseBounceVisibility(
                hitPosition + hitNormal * 0.04,
                lightPositionIntensity.xyz);
            incident += emitterColor(lightColorRadius.rgb)
                * emitterReceiver
                * radiusFade
                * incidentRadiance
                * visibility;
        }

        float distanceFade = 1.0 - smoothstep(
            voxelBounceDistance * 0.45,
            voxelBounceDistance,
            hitDistance);
        vec3 linearHitAlbedo = srgbToLinear(hitAlbedo);
        float diffuseLuminance = dot(linearHitAlbedo, LUMA);
        // Avoid extinguishing an emitter's chromaticity on very dark
        // texels. The neutral floor represents unresolved sub-texel
        // diffuse response; the material still supplies most of the
        // reflected spectrum.
        vec3 diffuseReflectance = max(
            mix(vec3(diffuseLuminance), linearHitAlbedo, 0.24),
            vec3(0.10));
        // voxelBounceDirection is cosine-weighted. Its cos(theta) and
        // Lambertian 1/pi terms are already accounted for by the sample
        // distribution, so multiplying by receiver again would bias
        // secondary radiance toward black.
        accumulated += diffuseReflectance
            * incident
            * distanceFade;
    }

    return accumulated / float(max(voxelBounceRayCount, 1));
}

vec3 evaluateDirectSpecular(
    vec3 worldNormal,
    vec3 viewDirection,
    vec3 lightDirection,
    float roughness)
{
    float normalLight = max(dot(worldNormal, lightDirection), 0.0);
    float normalView = max(dot(worldNormal, viewDirection), 0.001);
    if (normalLight <= 0.001)
    {
        return vec3(0.0);
    }

    vec3 halfDirection = normalize(viewDirection + lightDirection);
    float normalHalf = max(dot(worldNormal, halfDirection), 0.0);
    float viewHalf = max(dot(viewDirection, halfDirection), 0.0);
    float alpha = max(roughness * roughness, 0.035);
    float alphaSquared = alpha * alpha;
    float denominator = normalHalf * normalHalf
        * (alphaSquared - 1.0) + 1.0;
    float distribution = alphaSquared
        / max(3.14159265 * denominator * denominator, 0.0001);
    float geometryK = (alpha + 1.0) * (alpha + 1.0) * 0.125;
    float geometryView = normalView
        / (normalView * (1.0 - geometryK) + geometryK);
    float geometryLight = normalLight
        / (normalLight * (1.0 - geometryK) + geometryK);
    vec3 fresnel = vec3(0.04)
        + vec3(0.96) * pow(1.0 - viewHalf, 5.0);
    // Incoming radiance is multiplied by this term; N.L is included
    // here so the result is the complete reflected light contribution.
    return min(
        fresnel * distribution * geometryView * geometryLight
            * normalLight / max(4.0 * normalView * normalLight, 0.001),
        vec3(3.0));
}

float filterSpecularRoughness(
    float authoredRoughness,
    vec3 shadingNormal)
{
    // A point-sampled normal map can place a very narrow GGX lobe on one
    // pixel and miss every neighbour, producing the familiar firefly pattern.
    // Microfacet slope variances add under convolution, so fold the finite
    // pixel footprint of the normal field into alpha squared. This broadens
    // unresolved highlights without replacing either the authored normal or
    // roughness, and contains no frame phase that could shimmer over time.
    vec3 normalDx = dFdx(shadingNormal);
    vec3 normalDy = dFdy(shadingNormal);
    float normalFootprintVariance = clamp(
        0.25 * (dot(normalDx, normalDx) + dot(normalDy, normalDy)),
        0.0,
        0.22);
    float authoredVariance = authoredRoughness * authoredRoughness;
    return clamp(
        sqrt(authoredVariance + normalFootprintVariance),
        authoredRoughness,
        1.0);
}

vec3 softLimitSpecularRadiance(
    vec3 specularRadiance,
    vec3 incidentRadiance,
    float filteredRoughness,
    float metallic)
{
    vec3 positiveSpecular = max(specularRadiance, vec3(0.0));
    float specularLuminance = dot(positiveSpecular, LUMA);
    if (specularLuminance <= 0.0001)
    {
        return positiveSpecular;
    }

    // A finite pixel integrates the microfacet lobe rather than observing its
    // mathematical point peak. Retain a wider conductor shoulder, while the
    // incident-radiance term lets legitimately bright emitters raise the
    // ceiling. The exponential shoulder is continuous, preserves chroma and
    // is deterministic, unlike a temporal or binary highlight rejection.
    float incidentLuminance = dot(max(incidentRadiance, vec3(0.0)), LUMA);
    float materialFloor = mix(0.16, 1.20, metallic);
    float incidentScale = mix(1.20, 3.20, metallic)
        * mix(1.0, 0.72, filteredRoughness);
    float specularEnergyCeiling = max(
        materialFloor,
        incidentLuminance * incidentScale);
    float shoulderStart = specularEnergyCeiling * 0.62;
    float shoulderRange = max(
        specularEnergyCeiling - shoulderStart,
        0.001);
    float excess = max(specularLuminance - shoulderStart, 0.0);
    float compressedLuminance = shoulderStart
        + shoulderRange * (1.0 - exp(-excess / shoulderRange));
    float shoulderWeight = smoothstep(
        shoulderStart,
        specularEnergyCeiling,
        specularLuminance);
    float resolvedLuminance = mix(
        specularLuminance,
        compressedLuminance,
        shoulderWeight);
    return positiveSpecular
        * (resolvedLuminance / max(specularLuminance, 0.0001));
}

float pointShadowVisibilityAt(
    vec4 pointVisibilityA,
    vec4 pointVisibilityB,
    int lightIndex)
{
    return lightIndex < 4
        ? pointVisibilityA[lightIndex]
        : pointVisibilityB[lightIndex - 4];
}

void setPointShadowVisibility(
    inout vec4 pointVisibilityA,
    inout vec4 pointVisibilityB,
    int lightIndex,
    float visibility)
{
    if (lightIndex < 4)
    {
        pointVisibilityA[lightIndex] = visibility;
    }
    else
    {
        pointVisibilityB[lightIndex - 4] = visibility;
    }
}

void traceRawPointShadowVisibilities(
    vec3 worldPosition,
    vec3 worldGeometricNormal,
    out vec4 pointVisibilityA,
    out vec4 pointVisibilityB)
{
    // One channel owns one stable light slot. Never average visibility before
    // the per-source BRDF/radiance evaluation: a cage blocking a warm lantern
    // must not attenuate a separate cool emitter whose segment remains clear.
    pointVisibilityA = vec4(1.0);
    pointVisibilityB = vec4(1.0);
    for (int lightIndex = 0; lightIndex < MAX_VOXEL_LIGHTS; lightIndex++)
    {
        if (lightIndex >= voxelLightCount)
        {
            break;
        }

        vec4 lightPositionIntensity = voxelLightPositionIntensity[lightIndex];
        vec4 lightColorRadius = voxelLightColorRadius[lightIndex];
        vec3 toLight = lightPositionIntensity.xyz - worldPosition;
        float lightDistance = length(toLight);
        float lightRadius = physicalLightRange(
            lightIndex,
            lightPositionIntensity.w,
            min(lightColorRadius.w, pointLightRadius));
        if (lightDistance <= 0.001 || lightDistance >= lightRadius)
        {
            continue;
        }

        vec3 lightDirection = toLight / lightDistance;
        float geometricReceiver = max(
            dot(worldGeometricNormal, lightDirection),
            0.0);
        float radiusFade = photometricRangeFade(lightDistance, lightRadius);
        float incidentRadiance = photometricIncidentRadiance(
            lightIndex,
            lightPositionIntensity.w,
            lightDistance);
        float attenuation = radiusFade
            * incidentRadiance;
        float shadowPotential = geometricReceiver * attenuation;
        if (shadowPotential <= 0.001)
        {
            continue;
        }

        int lightSampleCount = clamp(
            pointLightShadowSamples,
            1,
            MAX_POINT_LIGHT_SAMPLES);
        bool boundedMultiLightCluster = denseDynamicLightCluster != 0;
        if (boundedMultiLightCluster)
        {
            lightSampleCount = 1;
        }
        bool cameraAlignedLight = voxelLightCasterLayer[lightIndex] < -0.5
            && distance(lightPositionIntensity.xyz, floatingWorldOrigin) < 0.75;
        // A held/camera-aligned emitter shares the unobstructed primary-view
        // segment and is resolved per source in the final pass. Excluding it
        // here prevents its guaranteed visibility from brightening the
        // aggregate mask consumed by unrelated world-space lamps.
        if (cameraAlignedLight)
        {
            continue;
        }

        float visibility = 0.0;
        vec3 rayOrigin = worldPosition
            + worldGeometricNormal * 0.12
            + lightDirection * 0.04;
        for (int sampleIndex = 0; sampleIndex < MAX_POINT_LIGHT_SAMPLES; sampleIndex++)
        {
            if (sampleIndex >= lightSampleCount)
            {
                break;
            }

            vec3 sampledLightPosition = samplePointLightSurface(
                lightPositionIntensity.xyz,
                lightDirection,
                lightIndex,
                sampleIndex,
                lightSampleCount);
            visibility += traceVoxelVisibility(
                rayOrigin,
                sampledLightPosition,
                lightIndex);
        }
        visibility /= float(lightSampleCount);

        setPointShadowVisibility(
            pointVisibilityA,
            pointVisibilityB,
            lightIndex,
            visibility);
    }
}

float traceRawSunShadowVisibility(
    vec3 worldPosition,
    vec3 relativeWorldPosition,
    vec3 worldGeometricNormal)
{
    float sunGeometricReceiver = max(
        dot(worldGeometricNormal, sunDirection),
        0.0);
    if (sunColorStrength.w < 0.08 || sunGeometricReceiver <= 0.001)
    {
        return 1.0;
    }

    vec3 sunRayOrigin = worldPosition
        + worldGeometricNormal * 0.12
        + sunDirection * 0.04;
    return traceSunVisibility(relativeWorldPosition, sunRayOrigin);
}

vec2 shadowFilterOffset(int index)
{
    if (shadowFilterTapCount == 2)
    {
        return index == 0
            ? vec2(0.70710678, 0.70710678)
            : vec2(-0.70710678, -0.70710678);
    }
    if (index == 0)
    {
        return vec2(1.0, 0.0);
    }
    if (index == 1)
    {
        return vec2(-1.0, 0.0);
    }
    if (index == 2)
    {
        return vec2(0.0, 1.0);
    }
    return vec2(0.0, -1.0);
}

void filterShadowVisibilities(
    vec3 centerPosition,
    vec3 centerNormal,
    out vec4 filteredPointA,
    out vec4 filteredPointB,
    out float filteredSun)
{
    vec4 centerPointA = clamp(texture(shadowPointCurrentA, uv), 0.0, 1.0);
    vec4 centerPointB = clamp(texture(shadowPointCurrentB, uv), 0.0, 1.0);
    float centerSun = clamp(texture(shadowSunCurrent, uv).r, 0.0, 1.0);
    vec4 accumulatedPointA = centerPointA;
    vec4 accumulatedPointB = centerPointB;
    float accumulatedSun = centerSun;
    float accumulatedWeight = 1.0;
    vec4 neighborhoodMinimumPointA = centerPointA;
    vec4 neighborhoodMaximumPointA = centerPointA;
    vec4 neighborhoodMinimumPointB = centerPointB;
    vec4 neighborhoodMaximumPointB = centerPointB;
    float neighborhoodMinimumSun = centerSun;
    float neighborhoodMaximumSun = centerSun;
    float centerDepth = abs(centerPosition.z);
    float depthScale = max(0.025, centerDepth * 0.0025);

    for (int index = 0; index < 4; index++)
    {
        if (index >= shadowFilterTapCount)
        {
            break;
        }
        vec2 sampleUv = clamp(
            uv + shadowFilterOffset(index) * shadowInverseFrameSize,
            shadowInverseFrameSize * 0.5,
            vec2(1.0) - shadowInverseFrameSize * 0.5);
        vec3 neighborPosition = texture(gPosition, sampleUv).xyz;
        vec3 encodedNeighborNormal = texture(gNormal, sampleUv).xyz;
        float neighborNormalLength = length(encodedNeighborNormal);
        if (dot(neighborPosition, neighborPosition) <= 0.0001
            || neighborNormalLength <= 0.1)
        {
            continue;
        }

        vec3 neighborNormal = encodedNeighborNormal / neighborNormalLength;
        float normalAgreement = max(dot(centerNormal, neighborNormal), 0.0);
        float normalWeight = smoothstep(0.88, 0.995, normalAgreement);
        float depthWeight = exp(
            -abs(neighborPosition.z - centerPosition.z) / depthScale);
        float weight = normalWeight * depthWeight;
        if (weight <= 0.02)
        {
            continue;
        }

        vec4 neighborPointA = clamp(
            texture(shadowPointCurrentA, sampleUv),
            0.0,
            1.0);
        vec4 neighborPointB = clamp(
            texture(shadowPointCurrentB, sampleUv),
            0.0,
            1.0);
        float neighborSun = clamp(
            texture(shadowSunCurrent, sampleUv).r,
            0.0,
            1.0);
        accumulatedPointA += neighborPointA * weight;
        accumulatedPointB += neighborPointB * weight;
        accumulatedSun += neighborSun * weight;
        accumulatedWeight += weight;
        neighborhoodMinimumPointA = min(neighborhoodMinimumPointA, neighborPointA);
        neighborhoodMaximumPointA = max(neighborhoodMaximumPointA, neighborPointA);
        neighborhoodMinimumPointB = min(neighborhoodMinimumPointB, neighborPointB);
        neighborhoodMaximumPointB = max(neighborhoodMaximumPointB, neighborPointB);
        neighborhoodMinimumSun = min(neighborhoodMinimumSun, neighborSun);
        neighborhoodMaximumSun = max(neighborhoodMaximumSun, neighborSun);
    }

    vec4 spatialPointA = accumulatedPointA / max(accumulatedWeight, 0.001);
    vec4 spatialPointB = accumulatedPointB / max(accumulatedWeight, 0.001);
    float spatialSun = accumulatedSun / max(accumulatedWeight, 0.001);
    if (shadowTemporalBlend <= 0.001)
    {
        filteredPointA = spatialPointA;
        filteredPointB = spatialPointB;
        filteredSun = spatialSun;
        return;
    }

    vec4 unclampedHistoryPointA = texture(shadowPointHistoryA, uv);
    vec4 unclampedHistoryPointB = texture(shadowPointHistoryB, uv);
    float unclampedHistorySun = texture(shadowSunHistory, uv).r;
    vec4 previousPointA = clamp(
        unclampedHistoryPointA,
        neighborhoodMinimumPointA,
        neighborhoodMaximumPointA);
    vec4 previousPointB = clamp(
        unclampedHistoryPointB,
        neighborhoodMinimumPointB,
        neighborhoodMaximumPointB);
    float previousSun = clamp(
        unclampedHistorySun,
        neighborhoodMinimumSun,
        neighborhoodMaximumSun);
    vec4 pointRejectionA = smoothstep(
        vec4(0.04),
        vec4(0.15),
        abs(unclampedHistoryPointA - spatialPointA));
    vec4 pointRejectionB = smoothstep(
        vec4(0.04),
        vec4(0.15),
        abs(unclampedHistoryPointB - spatialPointB));
    float sunRejection = smoothstep(
        0.04,
        0.15,
        abs(unclampedHistorySun - spatialSun));
    float historyWeight = clamp(shadowTemporalBlend, 0.0, 0.94);
    filteredPointA = mix(
        spatialPointA,
        previousPointA,
        vec4(historyWeight) * (vec4(1.0) - pointRejectionA));
    filteredPointB = mix(
        spatialPointB,
        previousPointB,
        vec4(historyWeight) * (vec4(1.0) - pointRejectionB));
    filteredSun = mix(
        spatialSun,
        previousSun,
        historyWeight * (1.0 - sunRejection));
}

VoxelLightingResult traceVoxelPointLight(
    vec3 worldPosition,
    vec3 relativeWorldPosition,
    vec3 worldNormal,
    vec3 worldGeometricNormal,
    float surfaceRoughness,
    float authoredMetallicHint,
    vec4 filteredPointVisibilityA,
    vec4 filteredPointVisibilityB,
    float filteredSunVisibility)
{
    VoxelLightingResult result;
    result.direct = vec3(0.0);
    result.directSpecular = vec3(0.0);
    result.blockedDirect = vec3(0.0);
    result.bounce = vec3(0.0);
    result.irradianceCache = sampleVoxelIrradiance(
        worldPosition,
        worldGeometricNormal);
    float cachedIrradianceWeight = dot(result.irradianceCache, LUMA);
    result.irradianceDirection = cachedIrradianceWeight > 0.0001
        ? sampleVoxelIrradianceDirection(
            worldPosition,
            worldGeometricNormal)
        : vec3(0.0, 1.0, 0.0);
    result.sunDirect = vec3(0.0);
    result.skyDirect = vec3(0.0);
    result.material = sampleVoxelAtWorld(
        worldPosition - worldGeometricNormal * 0.08);
    float voxelMetallicHint = smoothstep(0.66, 0.74, result.material.a);
    float directSpecularHint = max(
        max(authoredMetallicHint, voxelMetallicHint),
        1.0 - smoothstep(0.38, 0.72, surfaceRoughness));
    result.shadow = 0.0;
    result.visibility = 1.0;
    result.sunVisibility = 1.0;
    result.sunShadow = 0.0;
    result.cameraAlignedShadow = 0.0;
    result.skyVisibility = 0.0;

    float potentialWeight = 0.0;
    float occludedWeight = 0.0;
    float strongestBlockedEnergy = 0.0;
    vec3 irradianceDirectionSum = result.irradianceDirection
        * cachedIrradianceWeight;
    float irradianceDirectionWeight = cachedIrradianceWeight;
    vec3 worldViewDirection = vec3(0.0, 0.0, 1.0);
    bool worldViewDirectionReady = false;
    for (int lightIndex = 0; lightIndex < MAX_VOXEL_LIGHTS; lightIndex++)
    {
        if (lightIndex >= voxelLightCount)
        {
            break;
        }

        vec4 lightPositionIntensity = voxelLightPositionIntensity[lightIndex];
        vec4 lightColorRadius = voxelLightColorRadius[lightIndex];
        vec3 linearEmitterColor = emitterColor(lightColorRadius.rgb);
        vec3 toLight = lightPositionIntensity.xyz - worldPosition;
        float lightDistance = length(toLight);
        float lightRadius = physicalLightRange(
            lightIndex,
            lightPositionIntensity.w,
            min(lightColorRadius.w, pointLightRadius));
        if (lightDistance <= 0.001 || lightDistance >= lightRadius)
        {
            continue;
        }

        vec3 lightDirection = toLight / lightDistance;
        float receiver = max(dot(worldNormal, lightDirection), 0.0);
        float geometricReceiver = max(
            dot(worldGeometricNormal, lightDirection),
            0.0);
        float radiusFade = photometricRangeFade(lightDistance, lightRadius);
        float attenuation = radiusFade * photometricIncidentRadiance(
            lightIndex,
            lightPositionIntensity.w,
            lightDistance);
        float directionWeight = dot(
            linearEmitterColor,
            LUMA) * attenuation;
        irradianceDirectionSum += lightDirection * directionWeight;
        irradianceDirectionWeight += directionWeight;
        float directPotential = receiver * attenuation;
        // Visibility belongs to the tessellated receiver plane, not to
        // the PBR normal-map microfacets. Weighting the shadow mask by
        // worldNormal stamped brick/rock grain into an otherwise stable
        // cage or anvil silhouette (the former "point cloud" look).
        float shadowPotential = geometricReceiver * attenuation;
        if (max(directPotential, shadowPotential) <= 0.001)
        {
            continue;
        }

        bool cameraAlignedLight = voxelLightCasterLayer[lightIndex] < -0.5
            && distance(lightPositionIntensity.xyz, floatingWorldOrigin) < 0.75;
        // Each half-resolution channel belongs to exactly one world-space
        // lamp. A camera-aligned held emitter remains independently
        // unobstructed because it shares the primary-view origin.
        float visibility = cameraAlignedLight
            ? 1.0
            : clamp(pointShadowVisibilityAt(
                filteredPointVisibilityA,
                filteredPointVisibilityB,
                lightIndex), 0.0, 1.0);
        if (prefilteredShadowVisibility == 0 && !cameraAlignedLight)
        {
            vec3 rayOrigin = worldPosition
                + worldGeometricNormal * 0.12
                + lightDirection * 0.04;
            int lightSampleCount = clamp(
                pointLightShadowSamples,
                1,
                MAX_POINT_LIGHT_SAMPLES);
            if (denseDynamicLightCluster != 0)
            {
                lightSampleCount = 1;
            }
            // The dedicated raw pass traces every shadowable world-space
            // source. This fallback preserves identical physics only when
            // the owned MRT visibility history is unavailable.
            visibility = 0.0;
            for (int sampleIndex = 0; sampleIndex < MAX_POINT_LIGHT_SAMPLES; sampleIndex++)
            {
                if (sampleIndex >= lightSampleCount)
                {
                    break;
                }

                vec3 sampledLightPosition = samplePointLightSurface(
                    lightPositionIntensity.xyz,
                    lightDirection,
                    lightIndex,
                    sampleIndex,
                    lightSampleCount);
                visibility += traceVoxelVisibility(
                    rayOrigin,
                    sampledLightPosition,
                    lightIndex);
            }
            visibility /= float(lightSampleCount);
        }
        result.cameraAlignedShadow = max(
            result.cameraAlignedShadow,
            cameraAlignedLight ? 1.0 - visibility : 0.0);

        potentialWeight += shadowPotential;
        occludedWeight += shadowPotential * (1.0 - visibility);
        vec3 unoccludedDirect = linearEmitterColor
            * receiver * attenuation;
        result.direct += unoccludedDirect * visibility;
        if (directSpecularHint > 0.001)
        {
            // Most interior pixels are rough dielectric walls. Avoid
            // GGX pow/division work that the material eligibility gate
            // would multiply by zero during final composition.
            if (!worldViewDirectionReady)
            {
                worldViewDirection = normalize(cameraWorldPosition - worldPosition);
                worldViewDirectionReady = true;
            }
            result.directSpecular += linearEmitterColor
                * attenuation
                * visibility
                * evaluateDirectSpecular(
                    worldNormal,
                    worldViewDirection,
                    lightDirection,
                    surfaceRoughness);
        }
        vec3 blockedDirect = unoccludedDirect * (1.0 - visibility);
        result.blockedDirect += blockedDirect;
        // Several lamps can be visible to the voxel volume while being
        // separated from this receiver by unrelated rooms. Summing all
        // of those hypothetical blocked contributions turns nearly the
        // whole frame into a shadow. A projected shadow is instead
        // driven by the strongest local source that could materially
        // illuminate this receiver.
        float blockedShadowEnergy = dot(
            linearEmitterColor,
            LUMA) * shadowPotential * (1.0 - visibility);
        strongestBlockedEnergy = max(
            strongestBlockedEnergy,
            blockedShadowEnergy);
    }

    if (irradianceDirectionWeight > 0.0001
        && dot(irradianceDirectionSum, irradianceDirectionSum) > 0.000001)
    {
        // Reuse the static light uniforms already paid for by the
        // direct pass. This supplies a stable dominant incoming lobe
        // without a second 3D texture sample on every shaded pixel.
        result.irradianceDirection = normalize(irradianceDirectionSum);
    }

    // Retain the cadence uniform for shader ABI compatibility, but normal
    // runtime profiles bind one: writing zero radiance on skipped frames made
    // temporal history decay twice and then spike on the traced frame.
    bool traceSecondaryBounce = secondaryBounceCadence <= 1
        || temporalBlend <= 0.001
        || debugView == 8
        || temporalFrameIndex % max(secondaryBounceCadence, 1) == 0;
    result.bounce = traceSecondaryBounce
        ? traceVoxelDiffuseBounce(
            worldPosition,
            worldNormal,
            worldGeometricNormal)
        : vec3(0.0);

    // Preserve the weighted visibility of every selected emitter. The
    // raster carrier contains their aggregate engine lighting, so its
    // shadow factor must be the occluded fraction of the same aggregate,
    // never the visibility of whichever source happened to rank first.
    result.visibility = potentialWeight > 0.001
        ? 1.0 - clamp(occludedWeight / potentialWeight, 0.0, 1.0)
        : 1.0;
    // strongestBlockedEnergy is scene-linear incident radiance after the
    // CIE candela-to-lux transport above. The response begins near 0.034 lux
    // and reaches full display contrast near 0.34 lux after undoing the fixed
    // 1.1/pi exposure conversion. This preserves low-light shadow perception
    // without extending the physical source or altering its inverse-square law.
    float localShadowEnergy = smoothstep(
        0.012,
        0.120,
        strongestBlockedEnergy);
    result.shadow = (1.0 - result.visibility) * localShadowEnergy;

    float sunReceiver = max(dot(worldNormal, sunDirection), 0.0);
    float sunGeometricReceiver = max(
        dot(worldGeometricNormal, sunDirection),
        0.0);
    if (sunColorStrength.w >= 0.08
        && max(sunReceiver, sunGeometricReceiver) > 0.001)
    {
        vec3 sunRayOrigin = worldPosition
            + worldGeometricNormal * 0.12
            + sunDirection * 0.04;
        result.sunVisibility = prefilteredShadowVisibility != 0
            ? clamp(filteredSunVisibility, 0.0, 1.0)
            : traceSunVisibility(relativeWorldPosition, sunRayOrigin);
        result.sunDirect = srgbToLinear(sunColorStrength.rgb) * sunColorStrength.w
            * sunReceiver * result.sunVisibility;
        if (directSpecularHint > 0.001)
        {
            if (!worldViewDirectionReady)
            {
                worldViewDirection = normalize(cameraWorldPosition - worldPosition);
                worldViewDirectionReady = true;
            }
            result.directSpecular += srgbToLinear(sunColorStrength.rgb)
                * sunColorStrength.w
                * result.sunVisibility
                * evaluateDirectSpecular(
                    worldNormal,
                    worldViewDirection,
                    sunDirection,
                    surfaceRoughness);
        }
        // Keep the diagnostic mask normalized across the day. Lighting
        // energy still uses the PBR normal above, while projected
        // visibility follows the stable tessellated receiver plane.
        result.sunShadow = sunGeometricReceiver
            * (1.0 - result.sunVisibility);
    }

    result.skyDirect = traceVoxelSkyLighting(
        worldPosition,
        worldNormal,
        worldGeometricNormal,
        result.skyVisibility);
    if (sunGeometricReceiver > 0.001)
    {
        // The 96-block sun ray is already paid for and reliably finds
        // distant roofs. Use it as a conservative long-range roof gate
        // for the short local sky probe at performance quality.
        // One-ray adaptive tiers cannot estimate a wide sky dome
        // without structured seams. In those tiers, accept sky only
        // when the long-range roof ray also reaches the exterior;
        // higher quality retains a bounded diffuse-shadow floor.
        float longRangeRoofGate = skyRayCount <= 1
            ? result.sunVisibility
            : mix(0.16, 1.0, result.sunVisibility);
        result.skyDirect *= longRangeRoofGate;
    }
    return result;
}

bool projectToScreen(vec3 viewPosition, out vec2 screenUv)
{
    vec4 clip = projection * vec4(viewPosition, 1.0);
    if (clip.w <= 0.0001)
    {
        return false;
    }

    screenUv = clip.xy / clip.w * 0.5 + 0.5;
    vec2 margin = inverseFrameSize * 2.0;
    return all(greaterThan(screenUv, margin))
        && all(lessThan(screenUv, vec2(1.0) - margin));
}

LightingResult traceScreenSpaceLighting(vec3 origin, vec3 normal)
{
    LightingResult result;
    result.indirect = vec3(0.0);
    result.occlusion = 0.0;
    result.confidence = 0.0;

    vec3 helper = abs(normal.z) < 0.999
        ? vec3(0.0, 0.0, 1.0)
        : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(helper, normal));
    vec3 bitangent = cross(normal, tangent);

    // A coherent hemisphere rotation gives adjacent receivers the same
    // low-discrepancy ray set. Per-pixel white noise produced coloured
    // point clouds on broad walls. While the camera moves, hold the
    // phase fixed; once history is valid, eight golden-angle phases
    // converge without spatial sparkle.
    // Screen-space transport uses a fixed coherent basis. Rotating a one-ray
    // basis per frame makes broad walls change irradiance even when neither
    // the receiver nor any source moved.
    float rotation = 0.0;
    float rotationCos = cos(rotation);
    float rotationSin = sin(rotation);

    for (int rayIndex = 0; rayIndex < MAX_RAYS; rayIndex++)
    {
        if (rayIndex >= rayCount)
        {
            break;
        }

        float rayAngle = (float(rayIndex) + 0.5) * 1.57079632679;
        vec2 diskDirection = vec2(cos(rayAngle), sin(rayAngle));
        diskDirection = mat2(rotationCos, -rotationSin, rotationSin, rotationCos) * diskDirection;

        vec3 rayDirection = normalize(
            tangent * diskDirection.x * 0.62
            + bitangent * diskDirection.y * 0.62
            + normal * 0.78);
        vec3 rayOrigin = origin + normal * 0.035;

        for (int stepIndex = 0; stepIndex < MAX_STEPS; stepIndex++)
        {
            if (stepIndex >= raySteps)
            {
                break;
            }

            float stepFraction = float(stepIndex + 1) / float(raySteps);
            float distanceAlongRay = rayDistance * stepFraction * stepFraction;
            vec3 rayPosition = rayOrigin + rayDirection * distanceAlongRay;

            vec2 hitUv;
            if (!projectToScreen(rayPosition, hitUv))
            {
                break;
            }

            vec3 scenePosition = texture(gPosition, hitUv).xyz;
            if (dot(scenePosition, scenePosition) < 0.0001)
            {
                continue;
            }

            float depthGap = scenePosition.z - rayPosition.z;
            float thickness = 0.045 + distanceAlongRay * 0.075;
            if (depthGap < 0.0 || depthGap > thickness)
            {
                continue;
            }

            vec4 hitNormalRoughness = texture(gNormal, hitUv);
            // Animated entities have no motion vectors or previous geometry
            // identity in this screen-space transport buffer. Treating them as
            // diffuse bounce sources made one passing animal relight broad,
            // otherwise static walls and then linger in temporal history.
            // They remain fully shaded as receivers and keep their dedicated
            // reflection path; only their unstable SSGI source role is skipped.
            if (hitNormalRoughness.a < -0.0005)
            {
                continue;
            }

            vec3 hitNormal = normalize(hitNormalRoughness.xyz);
            float receiverTerm = max(dot(normal, rayDirection), 0.0);
            float emitterTerm = max(dot(hitNormal, -rayDirection), 0.0);
            float distanceFade = 1.0 - stepFraction;
            vec2 edgeDistance = min(hitUv, vec2(1.0) - hitUv);
            float edgeFade = smoothstep(0.0, 0.08, min(edgeDistance.x, edgeDistance.y));
            float confidence = receiverTerm * (0.2 + 0.8 * emitterTerm) * distanceFade * edgeFade;

            // Colour, position and normal must come from the same late-opaque
            // snapshot. The post-processed source can already contain a moving
            // entity at a pixel whose retained G-buffer still describes terrain,
            // which incorrectly turns that entity into wall-bounce radiance.
            result.indirect += sampleReflectionSource(hitUv) * confidence;
            result.occlusion += receiverTerm * distanceFade * distanceFade;
            result.confidence += confidence;
            break;
        }
    }

    float inverseRayCount = 1.0 / float(max(rayCount, 1));
    result.indirect *= inverseRayCount;
    result.occlusion = clamp(result.occlusion * inverseRayCount, 0.0, 1.0);
    result.confidence = clamp(result.confidence * inverseRayCount, 0.0, 1.0);
    return result;
}

vec3 shadeVoxelReflectionHit(
    vec3 hitPosition,
    vec3 hitNormal,
    vec3 hitAlbedo)
{
    // The voxel volume stores authored albedo rather than the fully lit
    // framebuffer color. Reconstruct a bounded local lighting estimate
    // so off-screen reflections remain spatially plausible in daylight
    // and near emissive/held lights without inventing a global sky tint.
    // A confirmed off-screen hit needs enough unresolved indirect
    // radiance to survive Fresnel on dark metals. This remains entirely
    // hit-local, adds no lookup and cannot create a global specular veil.
    vec3 reflectedColor = hitAlbedo * 0.115;
    float sunReceiver = max(dot(hitNormal, sunDirection), 0.0);
    reflectedColor += hitAlbedo
        * sunColorStrength.rgb
        * sunColorStrength.w
        * sunReceiver
        * 0.42;

    for (int lightIndex = 0; lightIndex < MAX_VOXEL_LIGHTS; lightIndex++)
    {
        if (lightIndex >= voxelLightCount || lightIndex >= 2)
        {
            break;
        }

        vec4 lightPositionIntensity = voxelLightPositionIntensity[lightIndex];
        vec4 lightColorRadius = voxelLightColorRadius[lightIndex];
        vec3 toLight = lightPositionIntensity.xyz - hitPosition;
        float distanceToLight = length(toLight);
        float lightRadius = physicalLightRange(
            lightIndex,
            lightPositionIntensity.w,
            min(lightColorRadius.w, pointLightRadius));
        if (distanceToLight <= 0.001 || distanceToLight >= lightRadius)
        {
            continue;
        }

        vec3 lightDirection = toLight / distanceToLight;
        float receiver = max(dot(hitNormal, lightDirection), 0.0);
        float radiusFade = photometricRangeFade(distanceToLight, lightRadius);
        float incidentRadiance = photometricIncidentRadiance(
            lightIndex,
            lightPositionIntensity.w,
            distanceToLight);
        reflectedColor += hitAlbedo
            * lightColorRadius.rgb
            * receiver
            * radiusFade
            * incidentRadiance
            * 0.75;
    }

    return clamp(reflectedColor, 0.0, 1.4);
}

ReflectionResult traceVoxelReflection(
    vec3 worldPosition,
    vec3 worldNormal,
    float roughness)
{
    ReflectionResult result;
    result.color = vec3(0.0);
    result.confidence = 0.0;
    result.voxelColor = vec3(0.0);
    result.voxelConfidence = 0.0;
    result.planarConfidence = 0.0;
    result.planarWorldPosition = vec3(0.0);
    result.fluidColumnSupport = 0.0;
    result.waterColorHint = 0.0;
    result.waterEvidence = 0.0;
    result.liquidPathLength = 0.0;
    result.liquidFresnel = 0.0;
    result.liquidAbsorption = vec3(0.0);
    result.liquidScattering = vec3(0.0);
    result.liquidEmission = vec3(0.0);
    result.liquidTransmission = 1.0;
    result.liquidOpaque = 0.0;
    result.liquidAnisotropy = 0.0;
    result.liquidRoughness = 1.0;
    result.liquidSurfaceDynamics = 0.0;
    result.liquidBubbleEmission = 0.0;
    result.liquidMetresPerWorldBlock = 1.0;
    result.liquidTransmissionUv = uv;
    result.liquidTransmissionConfidence = 0.0;
    result.liquidPartialGeometryFace = 0.0;
    result.liquidVerticalFaceEvidence = 0.0;
    result.liquidShoreFaceEvidence = 0.0;
    result.liquidShoreCarrierEvidence = 0.0;
    result.liquidShorePartialGeometryEvidence = 0.0;
    result.liquidShoreColumnEvidence = 0.0;
    result.liquidShoreSurfaceUv = uv;
    result.liquidShoreSurfaceConfidence = 0.0;
    if (voxelReflectionsEnabled == 0
        || voxelReflectionSteps <= 0
        || roughness >= 0.88
        || !isInsideVoxelVolume(worldPosition))
    {
        return result;
    }

    vec3 incidentDirection = normalize(worldPosition - cameraWorldPosition);
    vec3 rayDirection = normalize(reflect(incidentDirection, worldNormal));

    // One coherent cone sample widens rough reflections. Keep its phase
    // deterministic: rotating this single sample every frame produces a
    // sparse point-cloud before history converges and visibly sparkles
    // whenever camera motion invalidates that history. The authored
    // normal map already supplies spatial variation at material scale.
    if (roughness > 0.04)
    {
        vec3 helper = abs(rayDirection.y) < 0.95
            ? vec3(0.0, 1.0, 0.0)
            : vec3(1.0, 0.0, 0.0);
        vec3 tangent = normalize(cross(helper, rayDirection));
        vec3 bitangent = cross(rayDirection, tangent);
        float phase = 0.0;
        float coneWidth = roughness * roughness * 0.26;
        rayDirection = normalize(
            rayDirection
            + tangent * cos(phase) * coneWidth
            + bitangent * sin(phase) * coneWidth);
    }

    vec3 rayOrigin = worldPosition
        + worldNormal * 0.10
        + rayDirection * 0.045;
    if (!isInsideVoxelVolume(rayOrigin))
    {
        return result;
    }

    vec3 gridOrigin = rayOrigin - voxelOrigin;
    vec3 cell = floor(gridOrigin);
    vec3 stepDirection = sign(rayDirection);
    vec3 inverseDirection = 1.0 / max(abs(rayDirection), vec3(0.00001));
    vec3 nextBoundary = mix(
        cell,
        cell + vec3(1.0),
        greaterThan(stepDirection, vec3(0.0)));
    vec3 sideDistance = abs((nextBoundary - gridOrigin) * inverseDirection);
    vec3 deltaDistance = inverseDirection;
    sideDistance = mix(
        sideDistance,
        vec3(1e20),
        lessThan(abs(rayDirection), vec3(0.00001)));
    float maximumDistance = reflectionDistance * mix(1.0, 0.52, roughness);

    for (int stepIndex = 0; stepIndex < MAX_VOXEL_REFLECTION_STEPS; stepIndex++)
    {
        if (stepIndex >= voxelReflectionSteps)
        {
            break;
        }

        float traveled = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
        // Reflection rays obey the same measure-zero boundary rule as shadow
        // rays; otherwise a diagonal contact reports a neighbouring full
        // block that the ray never enters.
        if (sideDistance.x <= traveled + 0.00001)
        {
            sideDistance.x += deltaDistance.x;
            cell.x += stepDirection.x;
        }
        if (sideDistance.y <= traveled + 0.00001)
        {
            sideDistance.y += deltaDistance.y;
            cell.y += stepDirection.y;
        }
        if (sideDistance.z <= traveled + 0.00001)
        {
            sideDistance.z += deltaDistance.z;
            cell.z += stepDirection.z;
        }

        if (traveled >= maximumDistance)
        {
            break;
        }

        vec3 cellCenter = cell + vec3(0.5);
        if (any(lessThan(cellCenter, vec3(0.0)))
            || any(greaterThanEqual(cellCenter, voxelSize)))
        {
            break;
        }

        vec4 hitMaterial = texture(voxelVolume, cellCenter / voxelSize);
        if (hitMaterial.a < 0.45)
        {
            continue;
        }

        float exitDistance = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
        if (traceFineBlockVisibility(
            rayOrigin,
            rayDirection,
            traveled,
            min(exitDistance, maximumDistance)) >= 0.5)
        {
            continue;
        }

        vec3 hitPosition = rayOrigin + rayDirection * (traveled + 0.025);
        vec3 hitNormal = dominantAxisNormal(rayDirection);
        float distanceFade = 1.0 - smoothstep(
            maximumDistance * 0.55,
            maximumDistance,
            traveled);
        float grazingConfidence = 0.35
            + 0.65 * (1.0 - abs(dot(worldNormal, incidentDirection)));
        result.voxelColor = shadeVoxelReflectionHit(
            hitPosition,
            hitNormal,
            hitMaterial.rgb);
        result.voxelConfidence = clamp(
            distanceFade * grazingConfidence * (1.0 - roughness * 0.55),
            0.0,
            1.0);
        result.color = result.voxelColor;
        result.confidence = result.voxelConfidence;
        return result;
    }

    return result;
}

ReflectionResult traceScreenSpaceReflection(
    vec3 origin,
    vec3 normal,
    vec3 worldPosition,
    vec3 worldNormal,
    float roughness,
    vec3 surfaceColor,
    int allowVoxelFallback)
{
    ReflectionResult result;
    result.color = vec3(0.0);
    result.confidence = 0.0;
    result.voxelColor = vec3(0.0);
    result.voxelConfidence = 0.0;
    result.planarConfidence = 0.0;
    result.planarWorldPosition = vec3(0.0);
    result.fluidColumnSupport = 0.0;
    result.waterColorHint = 0.0;
    result.waterEvidence = 0.0;
    result.liquidPathLength = 0.0;
    result.liquidFresnel = 0.0;
    result.liquidAbsorption = vec3(0.0);
    result.liquidScattering = vec3(0.0);
    result.liquidEmission = vec3(0.0);
    result.liquidTransmission = 1.0;
    result.liquidOpaque = 0.0;
    result.liquidAnisotropy = 0.0;
    result.liquidRoughness = 1.0;
    result.liquidSurfaceDynamics = 0.0;
    result.liquidBubbleEmission = 0.0;
    result.liquidMetresPerWorldBlock = 1.0;
    result.liquidTransmissionUv = uv;
    result.liquidTransmissionConfidence = 0.0;
    result.liquidPartialGeometryFace = 0.0;
    result.liquidVerticalFaceEvidence = 0.0;
    result.liquidShoreFaceEvidence = 0.0;
    result.liquidShoreCarrierEvidence = 0.0;
    result.liquidShorePartialGeometryEvidence = 0.0;
    result.liquidShoreColumnEvidence = 0.0;
    result.liquidShoreSurfaceUv = uv;
    result.liquidShoreSurfaceConfidence = 0.0;
    if (reflectionSteps <= 0)
    {
        return result;
    }

    float voxelRoughnessLimit = voxelReflectionSteps >= 24
        ? 0.78
        : voxelReflectionSteps >= 12
            ? 0.68
            : 0.58;
    float ssrRoughnessLimit = reflectionSteps >= 8
        ? 0.82
        : reflectionSteps >= 4
            ? 0.68
            : 0.55;
    float maximumSpecularRoughness = max(
        voxelRoughnessLimit,
        ssrRoughnessLimit);

    // Open water commonly reflects the sky or distant scenery, which
    // has no G-buffer depth for a conventional SSR hit. The voxel
    // material alpha marks fluids/glass at 0.25; combine it with an
    // upward-facing world normal to provide a stable planar fallback
    // instead of returning a black, stippled miss mask.
    vec3 localWorldPosition = worldPosition - voxelOrigin;
    vec2 fluidSurfaceLocalWorldPosition = worldPosition.xz - fluidSurfaceOrigin;
    bool insideFluidColumns = all(greaterThanEqual(
            fluidSurfaceLocalWorldPosition,
            vec2(0.0)))
        && all(lessThan(fluidSurfaceLocalWorldPosition, fluidSurfaceSize));
    vec4 fluidColumnData = insideFluidColumns
        ? texture(
            voxelFluidSurface,
            (floor(fluidSurfaceLocalWorldPosition) + vec2(0.5))
                / fluidSurfaceSize)
        : vec4(0.0);
    float encodedFluidSurface = fluidColumnData.r;
    // Inventory-backed liquids have no 2D fluid-layer column. Probe just
    // below the visible G-buffer receiver so an exact block-top boundary
    // remains associated with its barrel, bucket, bowl, or other container.
    // Channel B stores the authored block-local surface height as UNorm8.
    vec3 containedMetadataProbeLocal = localWorldPosition - vec3(0.0, 0.02, 0.0);
    bool insideContainedMetadata = all(greaterThanEqual(
            containedMetadataProbeLocal,
            vec3(0.0)))
        && all(lessThan(containedMetadataProbeLocal, voxelSize));
    vec4 containedMetadata = insideContainedMetadata
        ? texture(
            voxelLiquidMetadata,
            (floor(containedMetadataProbeLocal) + vec3(0.5)) / voxelSize)
        : vec4(0.0);
    int containedFlags = int(floor(containedMetadata.g * 255.0 + 0.5));
    int containedProfileId = int(floor(containedMetadata.r * 255.0 + 0.5));
    bool hasVisibleContainedSurface = (containedFlags & 2) != 0
        && (containedFlags & 4) != 0
        && containedProfileId > 0
        && containedProfileId < 255;
    float containedSurfaceWorldY = voxelOrigin.y
        + floor(containedMetadataProbeLocal.y)
        + containedMetadata.b;
    // With a ready voxel scene, one column lookup is sufficient to
    // reject the overwhelmingly common rough, non-fluid wall. Avoid the
    // second projected-column sample and plane reconstruction on a path
    // that cannot emit SSR or voxel reflection radiance.
    if (voxelLightingEnabled != 0
        && encodedFluidSurface <= 0.5 / 255.0
        && !hasVisibleContainedSurface
        && roughness >= maximumSpecularRoughness)
    {
        return result;
    }
    vec3 viewRay = normalize(origin);
    vec3 rayDirection = normalize(reflect(viewRay, normal));
    vec3 rayOrigin = origin + normal * 0.055 + rayDirection * 0.035;
    float roughDistance = reflectionDistance * mix(1.0, 0.45, roughness);
    float surfaceLuminance = dot(surfaceColor, LUMA);
    float blueGreenLead = min(
        surfaceColor.g - surfaceColor.r,
        surfaceColor.b - surfaceColor.r);
    float waterColorHint = (1.0 - smoothstep(0.42, 0.68, surfaceLuminance))
        * smoothstep(0.015, 0.12, blueGreenLead);
    float fluidSurfaceWorldY = encodedFluidSurface > 0.5 / 255.0
        ? decodeFluidSurfaceWorldY(fluidColumnData)
        : containedSurfaceWorldY;
    vec3 cameraRayWorld = normalize(worldPosition - cameraWorldPosition);
    float opaqueDistance = distance(worldPosition, cameraWorldPosition);
    float surfaceDistance = abs(cameraRayWorld.y) > 0.0001
        ? (fluidSurfaceWorldY - cameraWorldPosition.y) / cameraRayWorld.y
        : -1.0;
    vec3 planarWorldPosition = cameraWorldPosition
        + cameraRayWorld * max(surfaceDistance, 0.0);

    // The opaque G-buffer describes the lake bed, not the transparent
    // water plane. Intersect the camera ray with the recorded fluid
    // height and validate the X/Z column at that intersection. This
    // prevents every underwater terrain step from becoming an isolated
    // mirror tile with its own (often vertical) bottom normal.
    vec3 planarLocalPosition = planarWorldPosition - voxelOrigin;
    vec2 planarFluidSurfaceLocalPosition = planarWorldPosition.xz
        - fluidSurfaceOrigin;
    bool insidePlanarFluidColumns = all(greaterThanEqual(
            planarFluidSurfaceLocalPosition,
            vec2(0.0)))
        && all(lessThan(planarFluidSurfaceLocalPosition, fluidSurfaceSize));
    vec4 planarFluidColumnData = insidePlanarFluidColumns
        ? texture(
            voxelFluidSurface,
            (floor(planarFluidSurfaceLocalPosition) + vec2(0.5))
                / fluidSurfaceSize)
        : vec4(0.0);
    float planarEncodedFluidSurface = planarFluidColumnData.r;
    float planarFluidSurfaceWorldY = planarEncodedFluidSurface > 0.5 / 255.0
        ? decodeFluidSurfaceWorldY(planarFluidColumnData)
        : fluidSurfaceWorldY;
    surfaceDistance = abs(cameraRayWorld.y) > 0.0001
        ? (planarFluidSurfaceWorldY - cameraWorldPosition.y) / cameraRayWorld.y
        : -1.0;
    planarWorldPosition = cameraWorldPosition
        + cameraRayWorld * max(surfaceDistance, 0.0);
    float fluidDepth = planarFluidSurfaceWorldY - worldPosition.y;
    float fluidColumnEvidence = step(
            0.5 / 255.0,
            planarEncodedFluidSurface)
        * step(0.0, surfaceDistance)
        * step(surfaceDistance + 0.025, opaqueDistance)
        * step(-0.35, fluidDepth)
        * (1.0 - smoothstep(16.0, 20.0, fluidDepth));
    vec2 containedCellDelta = abs(
        floor(planarLocalPosition.xz)
            - floor(containedMetadataProbeLocal.xz));
    float sameContainedCell = step(
        max(containedCellDelta.x, containedCellDelta.y),
        0.0);
    float containedSurfaceEvidence = hasVisibleContainedSurface
        ? sameContainedCell
            * step(0.0, surfaceDistance)
            * step(surfaceDistance - 0.04, opaqueDistance)
            * step(-0.08, fluidDepth)
            * step(fluidDepth, 1.25)
        : 0.0;
    // Transparent water from Vintage Story and RealisticWater can leave
    // the opaque G-buffer at the bed or at a late-composited depth that
    // fails the strict plane-before-depth test. The voxel column is
    // still authoritative. Combine that column proof with the visible
    // blue/green transmission hint so the entire lake, not only the far
    // shoreline band, receives the planar reflection. Ordinary green
    // terrain has no encoded fluid column and therefore cannot qualify.
    float fluidColumnSupport = max(
        max(
            step(0.5 / 255.0, encodedFluidSurface),
            step(0.5 / 255.0, planarEncodedFluidSurface)),
        containedSurfaceEvidence);
    // A fluid column proves that water exists in this X/Z cell, but it does
    // not prove that the interface is the first surface on the camera ray.
    // Crossed plants, fences and other non-full meshes can occupy the same
    // cell while remaining in front of the water plane. Keep an equal-depth
    // tolerance for engines that write the transparent plane itself to the
    // G-buffer, and reject only geometry measurably closer than the interface.
    float liquidInterfaceDepthVisibility = step(
        surfaceDistance - 0.018,
        opaqueDistance);
    // Consult the immutable late-opaque snapshot taken before transparent
    // liquids. Unlike the pre-entity snapshot, this contains alpha-tested
    // crossed vegetation and other non-full meshes as well as opaque terrain.
    vec3 opaqueViewPosition = texture(gOpaquePosition, uv).xyz;
    float opaquePositionValid = step(
        0.0001,
        dot(opaqueViewPosition, opaqueViewPosition));
    vec3 exactOpaqueWorldPosition = (
        inverseViewMatrix * vec4(opaqueViewPosition, 1.0)).xyz
        + floatingWorldOrigin;
    // Material metadata classifies the shared cell, while the immutable
    // opaque G-buffer supplies the exact alpha-tested pixel coverage. Their
    // product distinguishes a crossed blade/rail fragment from the open hole
    // beside it; using the cell flag alone turns the complete cell rectangular.
    float exactOpaquePartialSilhouette = opaquePositionValid
        * partialGeometryMetadataAtWorldPosition(exactOpaqueWorldPosition);
    float interfaceNearDistance = max(surfaceDistance - 0.018, 0.0);
    float opaqueInterfaceDepthVisibility = step(
        interfaceNearDistance * interfaceNearDistance,
        dot(opaqueViewPosition, opaqueViewPosition));
    liquidInterfaceDepthVisibility *= mix(
        1.0,
        opaqueInterfaceDepthVisibility,
        float(opaquePositionEnabled) * opaquePositionValid);
    // Alpha-tested crossed planes can write the depth attachment without
    // writing a usable view position. Compare the immutable late-opaque depth
    // against the analytically projected liquid plane so their exact raster
    // silhouettes remain in front of water even inside the same voxel column.
    vec3 interfaceNearWorldPosition = cameraWorldPosition
        + cameraRayWorld * max(surfaceDistance - 0.018, 0.0);
    vec3 interfaceNearViewPosition = (
        viewMatrix
        * vec4(interfaceNearWorldPosition - floatingWorldOrigin, 1.0)).xyz;
    vec4 interfaceNearClip = projection * vec4(interfaceNearViewPosition, 1.0);
    float interfaceNearDepth = interfaceNearClip.z
            / max(interfaceNearClip.w, 0.0001)
        * 0.5
        + 0.5;
    float opaqueSceneDepth = texture(gOpaqueDepth, uv).r;
    float opaqueDepthVisibility = step(interfaceNearDepth, opaqueSceneDepth);
    liquidInterfaceDepthVisibility *= mix(
        1.0,
        opaqueDepthVisibility,
        float(opaqueDepthEnabled));
    // Preserve the exact per-pixel verdict before the coarse occupancy fallback.
    // A shared solid/fluid block needs this distinction: alpha-tested blades
    // occlude their own pixels, while the holes between them still expose the
    // physically continuous horizontal free surface.
    float exactLiquidInterfaceDepthVisibility = liquidInterfaceDepthVisibility;
    // Some non-full blocks share a cell with the fluid layer but render in a
    // pass that does not leave an authoritative opaque depth sample. The
    // camera-centred 4^3 occupancy volume already contains their tessellated,
    // alpha-aware shape. One nearest lookup just above the interface therefore
    // rejects water where a crossed plant, fence, chiseled block, or other
    // partial mesh physically traverses the free surface. Sampling above the
    // plane deliberately leaves submerged receivers visible through water.
    vec3 interfaceGeometryProbeLocal = planarWorldPosition
        + vec3(0.0, 0.06, 0.0)
        - voxelOrigin;
    bool interfaceGeometryProbeInside = all(greaterThanEqual(
            interfaceGeometryProbeLocal,
            vec3(0.0)))
        && all(lessThan(interfaceGeometryProbeLocal, voxelSize));
    float interfaceFineOccupancyVisibility = 1.0;
    if (voxelLightingEnabled != 0 && interfaceGeometryProbeInside)
    {
        vec3 fineOccupancySize = voxelSize * occupancyScale;
        vec3 fineOccupancyCell = floor(
                interfaceGeometryProbeLocal * occupancyScale)
            + vec3(0.5);
        float interfaceFineOccupancy = texture(
            voxelOccupancy,
            fineOccupancyCell / fineOccupancySize).r;
        interfaceFineOccupancyVisibility = 1.0
            - step(0.50, interfaceFineOccupancy);
    }
    liquidInterfaceDepthVisibility *= interfaceFineOccupancyVisibility;
    float layeredPartialGeometry = max(
        partialGeometryMetadataAtWorldPosition(
            planarWorldPosition + vec3(0.0, 0.06, 0.0)),
        partialGeometryMetadataAtWorldPosition(
            planarWorldPosition - vec3(0.0, 0.06, 0.0)));
    float layeredPartialWaterEvidence = fluidColumnSupport
        * layeredPartialGeometry
        * exactLiquidInterfaceDepthVisibility;
    // Visibility is a property of the interface, not only of the
    // colour-assisted fallback. Apply it to the opaque-bed proof as well;
    // otherwise the former ungated maximum let a submerged receiver bypass
    // every partial-geometry occluder above it.
    float visibleFluidColumnEvidence = fluidColumnEvidence
        * liquidInterfaceDepthVisibility;
    float supportedTransparentWater = fluidColumnSupport
        * waterColorHint
        * step(0.0, surfaceDistance)
        * liquidInterfaceDepthVisibility;
    // Vintage Story exposes a dedicated liquid-depth framebuffer generated by
    // the real chunk-liquid mesh. Reconstruct that surface independently of
    // the opaque bed G-buffer and derive its geometric orientation from screen
    // derivatives. This distinguishes a horizontal free surface from the
    // vertical faces emitted beside stairs, slabs, crossed plants, and other
    // non-full blocks; those side faces must retain the engine's liquid resolve
    // instead of receiving a second horizontal planar reflection.
    float liquidDepthGeometryValid = 0.0;
    float liquidDepthHorizontalSupport = 1.0;
    float liquidDepthPartialGeometrySupport = 0.0;
    float liquidDepthShoreGeometrySupport = 0.0;
    float liquidFaceLayeredEvidence = 0.0;
    if (liquidDepthEnabled != 0)
    {
        // This uniform branch is coherent for the complete draw, so screen
        // derivatives remain defined while unavailable standalone/compatibility
        // paths never evaluate an unbound depth texture or inverse matrix.
        float resolvedLiquidDepth = texture(gLiquidDepth, uv).r;
        vec4 liquidClipPosition = vec4(
            uv * 2.0 - 1.0,
            resolvedLiquidDepth * 2.0 - 1.0,
            1.0);
        vec4 liquidViewPositionHomogeneous = inverseProjection
            * liquidClipPosition;
        vec3 liquidViewPosition = liquidViewPositionHomogeneous.xyz
            / max(abs(liquidViewPositionHomogeneous.w), 0.0001);
        vec3 liquidViewDerivativeX = dFdx(liquidViewPosition);
        vec3 liquidViewDerivativeY = dFdy(liquidViewPosition);
        vec3 liquidViewGeometricNormalUnnormalized = cross(
            liquidViewDerivativeX,
            liquidViewDerivativeY);
        float liquidNormalLengthSquared = dot(
            liquidViewGeometricNormalUnnormalized,
            liquidViewGeometricNormalUnnormalized);
        vec3 liquidViewGeometricNormal = liquidViewGeometricNormalUnnormalized
            * inversesqrt(max(liquidNormalLengthSquared, 0.00000001));
        vec3 liquidWorldGeometricNormal = normalize(
            mat3(inverseViewMatrix) * liquidViewGeometricNormal);
        liquidDepthGeometryValid = step(resolvedLiquidDepth, 0.99999)
            * step(0.000001, liquidNormalLengthSquared);
        float liquidDepthSurfaceUpness = abs(liquidWorldGeometricNormal.y);
        liquidDepthHorizontalSupport = smoothstep(
            0.58,
            0.86,
            liquidDepthSurfaceUpness);
        // A real vertical liquid face beside a partial solid is the engine's
        // block-boundary fallback, not a second free surface. Sample both sides
        // of the exact reconstructed face normal: one side is the fluid cell,
        // while the other can carry crossed vegetation, a slab, a stair, or a
        // chiseled instance. The material volume stores that classification in
        // a low metadata bit without changing its optical class.
        vec3 liquidWorldPosition = (
            inverseViewMatrix * vec4(liquidViewPosition, 1.0)).xyz
            + floatingWorldOrigin;
        vec3 horizontalLiquidNormal = vec3(
            liquidWorldGeometricNormal.x,
            0.0,
            liquidWorldGeometricNormal.z);
        float horizontalLiquidNormalLength = length(horizontalLiquidNormal);
        if (horizontalLiquidNormalLength > 0.001)
        {
            horizontalLiquidNormal /= horizontalLiquidNormalLength;
            vec3 positiveFaceProbe = liquidWorldPosition
                + horizontalLiquidNormal * 0.08;
            vec3 negativeFaceProbe = liquidWorldPosition
                - horizontalLiquidNormal * 0.08;
            float positivePartialGeometry = partialGeometryMetadataAtWorldPosition(
                positiveFaceProbe);
            float negativePartialGeometry = partialGeometryMetadataAtWorldPosition(
                negativeFaceProbe);
            float positiveSolidGeometry = solidGeometryMetadataAtWorldPosition(
                positiveFaceProbe);
            float negativeSolidGeometry = solidGeometryMetadataAtWorldPosition(
                negativeFaceProbe);
            vec3 positiveFaceLocal = positiveFaceProbe - voxelOrigin;
            vec3 negativeFaceLocal = negativeFaceProbe - voxelOrigin;
            bool positiveFaceInside = all(greaterThanEqual(
                    positiveFaceLocal,
                    vec3(0.0)))
                && all(lessThan(positiveFaceLocal, voxelSize));
            bool negativeFaceInside = all(greaterThanEqual(
                    negativeFaceLocal,
                    vec3(0.0)))
                && all(lessThan(negativeFaceLocal, voxelSize));
            vec4 positiveFaceLiquidMetadata = positiveFaceInside
                ? texture(
                    voxelLiquidMetadata,
                    (floor(positiveFaceLocal) + vec3(0.5)) / voxelSize)
                : vec4(0.0);
            vec4 negativeFaceLiquidMetadata = negativeFaceInside
                ? texture(
                    voxelLiquidMetadata,
                    (floor(negativeFaceLocal) + vec3(0.5)) / voxelSize)
                : vec4(0.0);
            int positiveFaceLiquidFlags = int(floor(
                positiveFaceLiquidMetadata.g * 255.0 + 0.5));
            int negativeFaceLiquidFlags = int(floor(
                negativeFaceLiquidMetadata.g * 255.0 + 0.5));
            float positiveFaceHasFluid = (positiveFaceLiquidFlags & 1) != 0
                ? 1.0
                : 0.0;
            float negativeFaceHasFluid = (negativeFaceLiquidFlags & 1) != 0
                ? 1.0
                : 0.0;
            float liquidFaceFluidSupport = max(
                positiveFaceHasFluid,
                negativeFaceHasFluid);
            // A shared-layer plant/slab has solid and fluid metadata in the
            // same cell; an ordinary bank has them on opposite sides of the
            // face. Both are shore contacts. An air-backed vertical liquid
            // face has no solid evidence and remains a waterfall/flow sheet.
            float positiveFluidShoreContact = positiveFaceHasFluid
                * max(positiveSolidGeometry, negativeSolidGeometry);
            float negativeFluidShoreContact = negativeFaceHasFluid
                * max(negativeSolidGeometry, positiveSolidGeometry);
            liquidDepthShoreGeometrySupport = max(
                    positiveFluidShoreContact,
                    negativeFluidShoreContact)
                * (1.0 - liquidDepthHorizontalSupport)
                * liquidDepthGeometryValid;
            liquidDepthPartialGeometrySupport = max(
                    positivePartialGeometry,
                    negativePartialGeometry)
                * liquidFaceFluidSupport
                * (1.0 - liquidDepthHorizontalSupport)
                * liquidDepthGeometryValid;
            if (liquidDepthPartialGeometrySupport > 0.001)
            {
                vec3 liquidFaceFluidProbe = positiveFaceHasFluid
                        >= negativeFaceHasFluid
                    ? positiveFaceProbe
                    : negativeFaceProbe;
                vec2 liquidFaceFluidLocal = liquidFaceFluidProbe.xz
                    - fluidSurfaceOrigin;
                bool liquidFaceInsideFluidSurface = all(greaterThanEqual(
                        liquidFaceFluidLocal,
                        vec2(0.0)))
                    && all(lessThan(
                        liquidFaceFluidLocal,
                        fluidSurfaceSize));
                vec4 liquidFaceFluidColumn = liquidFaceInsideFluidSurface
                    ? texture(
                        voxelFluidSurface,
                        (floor(liquidFaceFluidLocal) + vec2(0.5))
                            / fluidSurfaceSize)
                    : vec4(0.0);
                float liquidFaceEncodedSurface = liquidFaceFluidColumn.r;
                float liquidFaceSurfaceWorldY = decodeFluidSurfaceWorldY(
                    liquidFaceFluidColumn);
                float liquidFaceSurfaceDistance = abs(cameraRayWorld.y) > 0.0001
                    ? (liquidFaceSurfaceWorldY - cameraWorldPosition.y)
                        / cameraRayWorld.y
                    : -1.0;
                vec3 liquidFacePlanarWorldPosition = cameraWorldPosition
                    + cameraRayWorld * max(liquidFaceSurfaceDistance, 0.0);
                vec3 liquidFaceNearWorldPosition = cameraWorldPosition
                    + cameraRayWorld * max(
                        liquidFaceSurfaceDistance - 0.018,
                        0.0);
                vec3 liquidFaceNearViewPosition = (
                    viewMatrix * vec4(
                        liquidFaceNearWorldPosition - floatingWorldOrigin,
                        1.0)).xyz;
                vec4 liquidFaceNearClip = projection
                    * vec4(liquidFaceNearViewPosition, 1.0);
                float liquidFaceNearDepth = liquidFaceNearClip.z
                        / max(liquidFaceNearClip.w, 0.0001)
                    * 0.5
                    + 0.5;
                float liquidFaceExactVisibility = step(
                    liquidFaceNearDepth,
                    opaqueSceneDepth);
                liquidFaceLayeredEvidence = liquidDepthPartialGeometrySupport
                    * step(0.5 / 255.0, liquidFaceEncodedSurface)
                    * step(0.0, liquidFaceSurfaceDistance)
                    * mix(
                        1.0,
                        liquidFaceExactVisibility,
                        float(opaqueDepthEnabled));
                if (liquidFaceLayeredEvidence > 0.001)
                {
                    planarFluidColumnData = liquidFaceFluidColumn;
                    planarEncodedFluidSurface = liquidFaceEncodedSurface;
                    planarFluidSurfaceWorldY = liquidFaceSurfaceWorldY;
                    surfaceDistance = liquidFaceSurfaceDistance;
                    planarWorldPosition = liquidFacePlanarWorldPosition;
                    planarLocalPosition = planarWorldPosition - voxelOrigin;
                    fluidDepth = planarFluidSurfaceWorldY - worldPosition.y;
                }
            }
        }
    }
    float horizontalReflector = voxelLightingEnabled != 0
        ? mix(
            1.0,
            liquidDepthHorizontalSupport,
            liquidDepthGeometryValid)
        : smoothstep(0.68, 0.94, abs(worldNormal.y));
    // Vintage Story emits a vertical liquid decal beside partial blocks to
    // hide raster cracks. When the same X/Z cell contains an authored liquid,
    // that decal is not the physical interface: reconstruct the horizontal
    // surface through its alpha holes and retain the exact opaque silhouettes.
    horizontalReflector = max(
        horizontalReflector,
        max(layeredPartialWaterEvidence, liquidFaceLayeredEvidence));
    float waterCandidate = voxelLightingEnabled != 0
        ? max(
            max(
                max(visibleFluidColumnEvidence, supportedTransparentWater),
                containedSurfaceEvidence),
            max(layeredPartialWaterEvidence, liquidFaceLayeredEvidence))
        : waterColorHint;
    result.fluidColumnSupport = fluidColumnSupport;
    result.waterColorHint = waterColorHint;
    result.liquidPartialGeometryFace = max(
            liquidDepthPartialGeometrySupport,
            liquidDepthShoreGeometrySupport)
        * (1.0 - liquidFaceLayeredEvidence);
    result.liquidVerticalFaceEvidence = liquidDepthGeometryValid
        * (1.0 - liquidDepthHorizontalSupport);
    result.liquidShoreFaceEvidence = liquidDepthShoreGeometrySupport;
    // The common crossed-plant case is not an adjacent shoreline: Vintage
    // Story stores the non-full solid and the fluid layer in the same block.
    // The immutable opaque depth already provides the exact alpha-tested
    // silhouette. Its complement to exact interface visibility therefore
    // restores only occupied blades/rails/shape fragments while leaving the
    // holes as physically continuous water. Require an actual late-liquid
    // colour delta so an ordinary partial block above a lake is untouched.
    vec3 opaqueCarrierColor = sampleReflectionSource(uv);
    float lateLiquidCarrierDifference = length(
        surfaceColor - opaqueCarrierColor);
    result.liquidShoreCarrierEvidence = step(
            0.025,
            lateLiquidCarrierDifference)
        * step(0.02, waterColorHint);
    result.liquidShorePartialGeometryEvidence =
        exactOpaquePartialSilhouette;
    result.liquidShoreColumnEvidence = fluidColumnSupport;
    float sharedPartialLayerOccluderEvidence = fluidColumnSupport
        * layeredPartialGeometry
        * (1.0 - exactLiquidInterfaceDepthVisibility)
        * step(0.025, lateLiquidCarrierDifference)
        * step(0.02, waterColorHint);
    result.liquidPartialGeometryFace = max(
        result.liquidPartialGeometryFace,
        sharedPartialLayerOccluderEvidence);
    result.liquidVerticalFaceEvidence = max(
        result.liquidVerticalFaceEvidence,
        sharedPartialLayerOccluderEvidence);
    result.liquidShoreFaceEvidence = max(
        result.liquidShoreFaceEvidence,
        sharedPartialLayerOccluderEvidence);
    // The official colour/OIT liquid pass can emit its stair/slab shoreline
    // decal even when the dedicated LiquidDepth pass omits that face. Recover
    // this second path from independent evidence: the visible receiver is a
    // solid, the late-liquid carrier differs from the immutable opaque frame,
    // and one adjacent world column contains fluid whose surface covers the
    // receiver height. A waterfall backed by air fails the solid test.
    float screenSpaceShoreDecalEvidence = 0.0;
    if (voxelLightingEnabled != 0
        && waterColorHint > 0.02
        && fluidColumnSupport < 0.001)
    {
        vec3 opaqueReceiverWorldPosition = (
            inverseViewMatrix * vec4(opaqueViewPosition, 1.0)).xyz
            + floatingWorldOrigin;
        vec3 shoreReceiverWorldPosition = opaquePositionValid > 0.5
            ? opaqueReceiverWorldPosition
            : worldPosition;
        // Only a non-full receiver can share the liquid cell. A generic opaque
        // receiver also matches the bank or distant terrain seen through an
        // alpha hole and was the source of rectangular turquoise slabs around
        // shoreline vegetation.
        float visiblePartialReceiver = partialGeometryMetadataAtWorldPosition(
            shoreReceiverWorldPosition);
        float opaqueDepthReceiver = opaqueDepthEnabled != 0
            ? step(opaqueSceneDepth, 0.99999)
            : opaquePositionValid;
        float exactOpaqueReceiverEvidence = max(
            opaquePositionValid,
            opaqueDepthReceiver);
        float opaqueReceiverEvidence = visiblePartialReceiver
            * exactOpaqueReceiverEvidence;
        vec2 shoreReceiverFluidLocalPosition = shoreReceiverWorldPosition.xz
            - fluidSurfaceOrigin;
        vec2 receiverColumn = floor(shoreReceiverFluidLocalPosition);
        float adjacentFluidCoverage = 0.0;
        float adjacentFluidProfileId = 0.0;
        const vec2 adjacentColumnOffsets[4] = vec2[4](
            vec2(1.0, 0.0),
            vec2(-1.0, 0.0),
            vec2(0.0, 1.0),
            vec2(0.0, -1.0));
        for (int neighborIndex = 0; neighborIndex < 4; neighborIndex++)
        {
            vec2 neighborColumn = receiverColumn
                + adjacentColumnOffsets[neighborIndex];
            bool neighborInside = all(greaterThanEqual(
                    neighborColumn,
                    vec2(0.0)))
                && all(lessThan(neighborColumn, fluidSurfaceSize));
            vec4 neighborFluidColumn = neighborInside
                ? texture(
                    voxelFluidSurface,
                    (neighborColumn + vec2(0.5)) / fluidSurfaceSize)
                : vec4(0.0);
            float neighborEncodedSurface = neighborFluidColumn.r;
            float neighborSurfaceWorldY = neighborEncodedSurface > 0.5 / 255.0
                ? decodeFluidSurfaceWorldY(neighborFluidColumn)
                : shoreReceiverWorldPosition.y - 2.0;
            float receiverToSurface = neighborSurfaceWorldY
                - shoreReceiverWorldPosition.y;
            float neighborFluidCoverage = step(
                    0.5 / 255.0,
                    neighborEncodedSurface)
                * step(-0.12, receiverToSurface)
                * step(receiverToSurface, 1.25);
            if (neighborFluidCoverage > adjacentFluidCoverage)
            {
                adjacentFluidCoverage = neighborFluidCoverage;
                adjacentFluidProfileId = neighborFluidColumn.g;
            }
        }
        // The colour/OIT pass can rasterize its anti-crack vertical decal while
        // LiquidDepth omits that very face. At a grazing bank the opaque
        // receiver may project several world columns behind it, so a world
        // neighbour lookup alone is insufficient. Search a bounded vertical
        // screen footprint for the real liquid-depth surface immediately above
        // or below the missing face. A waterfall keeps valid LiquidDepth at the
        // current pixel and never enters this recovery path.
        float nearbyLiquidDepthSupport = 0.0;
        vec3 nearbyLiquidWorldPosition = shoreReceiverWorldPosition;
        if (liquidDepthEnabled != 0 && liquidDepthGeometryValid < 0.001)
        {
            const float liquidDepthSearchPixels[5] = float[5](
                2.0,
                8.0,
                24.0,
                64.0,
                128.0);
            for (int searchIndex = 0; searchIndex < 5; searchIndex++)
            {
                vec2 screenOffset = vec2(
                    0.0,
                    liquidDepthSearchPixels[searchIndex]
                        * inverseFrameSize.y);
                float positiveDepth = texture(
                    gLiquidDepth,
                    clamp(uv + screenOffset, vec2(0.0), vec2(1.0))).r;
                float negativeDepth = texture(
                    gLiquidDepth,
                    clamp(uv - screenOffset, vec2(0.0), vec2(1.0))).r;
                float positiveSupport = step(positiveDepth, 0.99999);
                float negativeSupport = step(negativeDepth, 0.99999);
                if (nearbyLiquidDepthSupport < 0.001
                    && max(positiveSupport, negativeSupport) > 0.001)
                {
                    float selectedLiquidDepth = positiveSupport
                            >= negativeSupport
                        ? positiveDepth
                        : negativeDepth;
                    result.liquidShoreSurfaceUv = clamp(
                        positiveSupport >= negativeSupport
                            ? uv + screenOffset
                            : uv - screenOffset,
                        vec2(0.0),
                        vec2(1.0));
                    vec4 nearbyLiquidClipPosition = vec4(
                        result.liquidShoreSurfaceUv * 2.0 - 1.0,
                        selectedLiquidDepth * 2.0 - 1.0,
                        1.0);
                    vec4 nearbyLiquidViewHomogeneous = inverseProjection
                        * nearbyLiquidClipPosition;
                    vec3 nearbyLiquidViewPosition =
                        nearbyLiquidViewHomogeneous.xyz
                        / max(
                            abs(nearbyLiquidViewHomogeneous.w),
                            0.0001);
                    nearbyLiquidWorldPosition = (
                        inverseViewMatrix
                            * vec4(nearbyLiquidViewPosition, 1.0)).xyz
                        + floatingWorldOrigin;
                    result.liquidShoreSurfaceConfidence = 1.0;
                }
                nearbyLiquidDepthSupport = max(
                    nearbyLiquidDepthSupport,
                    max(positiveSupport, negativeSupport));
            }
        }
        // Screen proximity alone is not a world-space contact. At a grazing
        // bank, a vertical search can encounter the lake many blocks in front
        // of a distant plant or terrain receiver. Keep only liquid geometry
        // within the same cell or an immediately adjacent cell.
        float nearbyLiquidWorldContact = nearbyLiquidDepthSupport
            * (1.0 - smoothstep(
                1.35,
                2.10,
                length(
                    nearbyLiquidWorldPosition
                        - shoreReceiverWorldPosition)));
        nearbyLiquidDepthSupport *= nearbyLiquidWorldContact;
        result.liquidShoreSurfaceConfidence *= nearbyLiquidWorldContact;
        float shoreFluidSupport = max(
            adjacentFluidCoverage,
            nearbyLiquidDepthSupport);
        result.liquidShorePartialGeometryEvidence = max(
            result.liquidShorePartialGeometryEvidence,
            opaqueReceiverEvidence);
        result.liquidShoreColumnEvidence = max(
            result.liquidShoreColumnEvidence,
            shoreFluidSupport);
        screenSpaceShoreDecalEvidence = opaqueReceiverEvidence
            * shoreFluidSupport
            * step(0.025, lateLiquidCarrierDifference)
            * step(0.02, waterColorHint);
        if (screenSpaceShoreDecalEvidence > 0.001)
        {
            int adjacentProfileIndex = int(floor(
                adjacentFluidProfileId * 255.0 + 0.5));
            LiquidOpticalProfile shoreProfile = adjacentProfileIndex > 0
                    && adjacentProfileIndex < 255
                ? lookupLiquidOpticalProfile(adjacentFluidProfileId)
                : defaultWaterOpticalProfile();
            float shoreIncidenceCosine = clamp(
                abs(cameraRayWorld.y),
                0.001,
                1.0);
            // A shared partial cell contributes a short water chord, not an
            // opaque one-metre wall. Convert a quarter-block local thickness
            // to the view-ray optical path and cap it to one world metre.
            result.liquidPathLength = clamp(
                0.25 / max(shoreIncidenceCosine, 0.25),
                0.25,
                1.0);
            result.liquidFresnel = exactDielectricFresnel(
                shoreIncidenceCosine,
                1.000293,
                shoreProfile.ior);
            result.liquidAbsorption = shoreProfile.absorption;
            result.liquidScattering = shoreProfile.scattering;
            result.liquidTransmission = shoreProfile.transmission;
            result.liquidOpaque = shoreProfile.opaque;
            result.liquidMetresPerWorldBlock = shoreProfile.metresPerWorldBlock;
        }
    }
    result.liquidPartialGeometryFace = max(
        result.liquidPartialGeometryFace,
        screenSpaceShoreDecalEvidence);
    result.liquidVerticalFaceEvidence = max(
        result.liquidVerticalFaceEvidence,
        screenSpaceShoreDecalEvidence);
    result.liquidShoreFaceEvidence = max(
        result.liquidShoreFaceEvidence,
        screenSpaceShoreDecalEvidence);
    bool possibleWater = waterCandidate * horizontalReflector > 0.02;
    if (!possibleWater
        && roughness >= maximumSpecularRoughness)
    {
        return result;
    }

    // A ready voxel scene is authoritative: require an actual fluid
    // column and never classify ordinary green ground as water. The
    // colour hint exists only for the safe no-voxel compatibility path.
    float waterEvidence = voxelLightingEnabled != 0
        ? max(
            max(
                max(visibleFluidColumnEvidence, supportedTransparentWater),
                containedSurfaceEvidence),
            max(layeredPartialWaterEvidence, liquidFaceLayeredEvidence))
        : waterColorHint;
    result.waterEvidence = waterEvidence;
    // The opaque G-buffer normally records the first lit receiver below
    // transparent liquid. Convert its camera-ray separation to vertical
    // depth, then divide by the vertical component of the refracted ray.
    // This is the optical distance inside the liquid rather than the
    // unrefracted screen ray. Some transparent pipelines instead leave
    // the liquid plane itself in the G-buffer; retain a shallow
    // single-layer fallback only on an authoritative fluid column.
    // Column metadata is the cheapest surface lookup. A 3D metadata
    // sample supplies the same profile id for contained/partial liquids
    // when the 2D column has no id. IDs 0 and 255 are neutral by contract;
    // only an R-only legacy column (no flags and no id) falls back to the
    // safe water profile.
    const float airIor = 1.000293;
    LiquidOpticalProfile liquidProfile = neutralLiquidOpticalProfile();
    vec3 liquidSurfaceNormal = vec3(0.0, 1.0, 0.0);
    vec2 liquidStructuralSurfaceSlope = vec2(0.0);
    vec3 refractedLiquidDirection = cameraRayWorld;
    if (waterEvidence > 0.001)
    {
        vec3 liquidMetadataWorldPosition = planarWorldPosition
            - vec3(0.0, 0.08, 0.0);
        vec3 liquidMetadataLocalPosition = liquidMetadataWorldPosition - voxelOrigin;
        bool insideLiquidMetadata = all(greaterThanEqual(
                liquidMetadataLocalPosition,
                vec3(0.0)))
            && all(lessThan(liquidMetadataLocalPosition, voxelSize));
        float columnLiquidProfileId = planarFluidColumnData.g > 0.5 / 255.0
            ? planarFluidColumnData.g
            : fluidColumnData.g;
        float columnLiquidFlags = max(
            planarFluidColumnData.a,
            fluidColumnData.a);
        // Surface columns carry both the profile and flags for ordinary
        // world liquids. Only consult the larger 3D volume for legacy or
        // contained-liquid paths whose column has no optical payload.
        bool needsVolumeLiquidMetadata = columnLiquidProfileId <= 0.5 / 255.0
            && columnLiquidFlags <= 0.5 / 255.0;
        vec4 liquidMetadata = insideLiquidMetadata && needsVolumeLiquidMetadata
            ? texture(
                voxelLiquidMetadata,
                (floor(liquidMetadataLocalPosition) + vec3(0.5)) / voxelSize)
            : vec4(0.0);
        float encodedLiquidProfileId = columnLiquidProfileId > 0.5 / 255.0
            ? columnLiquidProfileId
                : liquidMetadata.r;
        float encodedLiquidFlags = max(
            columnLiquidFlags,
            liquidMetadata.g);
        int resolvedLiquidProfileId = int(floor(
            encodedLiquidProfileId * 255.0 + 0.5));
        bool legacyWaterColumn = resolvedLiquidProfileId == 0
            && encodedLiquidFlags < 0.5 / 255.0
            && fluidColumnSupport > 0.0;
        liquidProfile = legacyWaterColumn
            ? defaultWaterOpticalProfile()
            : lookupLiquidOpticalProfile(encodedLiquidProfileId);
        float displacedSurfaceWorldY;
        float maximumDynamicDisplacement = max(
            liquidProfile.waveAmplitude + liquidProfile.bubbleRadiusMax,
            0.001);
        if (refineDynamicLiquidSurfaceIntersection(
                cameraWorldPosition,
                cameraRayWorld,
                planarFluidSurfaceWorldY,
                maximumDynamicDisplacement,
                surfaceDistance,
                planarWorldPosition,
                displacedSurfaceWorldY))
        {
            planarFluidSurfaceWorldY = displacedSurfaceWorldY;
        }
        liquidSurfaceNormal = profileDrivenLiquidNormal(
            planarWorldPosition,
            liquidProfile,
            result.liquidSurfaceDynamics,
            result.liquidBubbleEmission,
            liquidStructuralSurfaceSlope);
        refractedLiquidDirection = refract(
            cameraRayWorld,
            liquidSurfaceNormal,
            airIor / liquidProfile.ior);
        float cosLiquidIncident = clamp(
            dot(-cameraRayWorld, liquidSurfaceNormal),
            0.0,
            1.0);
        result.liquidFresnel = exactDielectricFresnel(
            cosLiquidIncident,
            airIor,
            liquidProfile.ior);
        result.liquidAbsorption = liquidProfile.absorption;
        result.liquidScattering = liquidProfile.scattering;
        result.liquidEmission = liquidProfile.emission;
        result.liquidTransmission = liquidProfile.transmission;
        result.liquidOpaque = liquidProfile.opaque;
        result.liquidAnisotropy = liquidProfile.anisotropy;
        result.liquidRoughness = liquidProfile.roughness;
        result.liquidMetresPerWorldBlock = liquidProfile.metresPerWorldBlock;
    }
    float liquidInterfaceRoughness = waterEvidence > 0.001
        ? clamp(liquidProfile.roughness, 0.0, 1.0)
        : roughness;
    float exactViewRaySeparation = max(
        opaqueDistance - max(surfaceDistance, 0.0),
        0.0);
    float verticalWaterDepth = max(
        planarFluidSurfaceWorldY - worldPosition.y,
        0.0);
    float exactVerticalWaterDepth = exactViewRaySeparation
        * abs(cameraRayWorld.y);
    float transparentPlaneFallbackDepth = supportedTransparentWater
        * (1.0 - fluidColumnEvidence)
        * 0.65;
    float resolvedVerticalWaterDepth = max(
        verticalWaterDepth,
        max(
            exactVerticalWaterDepth,
            transparentPlaneFallbackDepth));
    result.liquidPathLength = clamp(
        resolvedVerticalWaterDepth
            / max(abs(refractedLiquidDirection.y), 0.12)
            * waterEvidence,
        0.0,
        18.0);
    // Refract the visible transmission, not only the attenuation length.
    // The target projection is analytic so this adds exactly one colour
    // sample later in composition and no depth/ray-march loop. Validation
    // keeps the target behind the proven surface, within the current
    // voxel-fluid bounds and near the opaque receiver that supplied the
    // thickness. Zero thickness deliberately leaves confidence at zero,
    // which means no screen-space displacement.
    if (result.liquidPathLength > 0.001)
    {
        vec3 refractedWorldTarget = planarWorldPosition
            + refractedLiquidDirection * result.liquidPathLength;
        vec2 refractedFluidSurfaceLocalTarget = refractedWorldTarget.xz
            - fluidSurfaceOrigin;
        float refractedInsideFluidBounds = step(
                0.0,
                refractedFluidSurfaceLocalTarget.x)
            * step(0.0, refractedFluidSurfaceLocalTarget.y)
            * step(
                refractedFluidSurfaceLocalTarget.x,
                fluidSurfaceSize.x - 0.001)
            * step(
                refractedFluidSurfaceLocalTarget.y,
                fluidSurfaceSize.y - 0.001);
        float refractedBelowSurface = step(
            refractedWorldTarget.y,
            planarFluidSurfaceWorldY + 0.02);
        float refractedTargetDistance = distance(
            refractedWorldTarget,
            cameraWorldPosition);
        float refractedDepthValid = step(
                surfaceDistance + 0.015,
                refractedTargetDistance)
            * step(
                refractedTargetDistance,
                opaqueDistance + max(resolvedVerticalWaterDepth * 0.75, 0.65));
        vec3 refractedViewTarget = (viewMatrix
            * vec4(refractedWorldTarget - floatingWorldOrigin, 1.0)).xyz;
        vec2 refractedUv = uv;
        float refractedProjectionValid = projectToScreen(
            refractedViewTarget,
            refractedUv) ? 1.0 : 0.0;
        vec3 refractedSourceViewPosition = texture(
            gPosition,
            refractedUv).xyz;
        float refractedSourceHasGeometry = step(
            0.0001,
            dot(refractedSourceViewPosition, refractedSourceViewPosition));
        vec3 refractedSourceWorldPosition = (
            inverseViewMatrix * vec4(refractedSourceViewPosition, 1.0)).xyz
            + floatingWorldOrigin;
        vec3 refractedSourceOffset = refractedSourceWorldPosition
            - planarWorldPosition;
        float refractedSourceRayDistance = dot(
            refractedSourceOffset,
            refractedLiquidDirection);
        float refractedSourceLateralError = length(
            refractedSourceOffset
                - refractedLiquidDirection * refractedSourceRayDistance);
        float refractedSourceTolerance = 0.10
            + max(refractedSourceRayDistance, 0.0) * 0.035;
        float refractedSourceAlignment = 1.0 - smoothstep(
            refractedSourceTolerance,
            refractedSourceTolerance * 2.5,
            refractedSourceLateralError);
        float refractedSourceBehindInterface = step(
            0.015,
            refractedSourceRayDistance);
        float refractedSourceBelowInterface = step(
            refractedSourceWorldPosition.y,
            planarFluidSurfaceWorldY + 0.02);
        result.liquidTransmissionUv = mix(uv, refractedUv, refractedProjectionValid);
        result.liquidTransmissionConfidence = refractedProjectionValid
            * refractedInsideFluidBounds
            * refractedBelowSurface
            * refractedDepthValid
            * refractedSourceHasGeometry
            * refractedSourceAlignment
            * refractedSourceBehindInterface
            * refractedSourceBelowInterface
            * waterEvidence;
    }
    float planarFallback = waterEvidence * horizontalReflector;
    if (planarFallback > 0.001)
    {
        result.planarWorldPosition = planarWorldPosition;
        // Preserve the projected horizon as a conservative fallback,
        // then project the actual reflected camera ray. A vertical UV
        // mirror only works for an unpitched camera; while looking down
        // it compresses all distant scenery into a thin shoreline band.
        vec3 cameraForwardWorld = normalize(
            (inverseViewMatrix * vec4(0.0, 0.0, -1.0, 0.0)).xyz);
        vec3 horizontalForwardWorld = vec3(
            cameraForwardWorld.x,
            0.0,
            cameraForwardWorld.z);
        float horizontalForwardLength = length(horizontalForwardWorld);
        float horizonUvY = 0.5;
        if (horizontalForwardLength > 0.001)
        {
            horizontalForwardWorld /= horizontalForwardLength;
            vec3 horizonView = transpose(mat3(inverseViewMatrix))
                * horizontalForwardWorld;
            vec2 horizonUv;
            if (projectToScreen(horizonView * 128.0, horizonUv))
            {
                horizonUvY = horizonUv.y;
            }
        }

        horizonUvY = clamp(horizonUvY, 0.32, 0.90);
        // No additional screen-space horizon clip is necessary here:
        // waterEvidence already requires a real fluid column and a
        // valid projected water plane. Both screen UV and view-ray Y
        // can be inverted by the engine copy path; either extra gate
        // reduced a fully proven lake to a narrow far-shore strip.
        vec2 horizonMirroredUv = vec2(
            uv.x,
            clamp(2.0 * horizonUvY - uv.y, 0.002, 0.998));
        vec3 environmentDirection = normalize(reflect(
            cameraRayWorld,
            liquidSurfaceNormal));
        // Structural scene placement must follow the resolved liquid plane,
        // not the high-frequency shading normal. Projecting a far point with
        // the wave normal makes adjacent water pixels look in widely separated
        // directions and slices tall scenery into horizontal bands. Keep the
        // translated water hit for parallax, then apply waves only as the
        // bounded local ripple below.
        vec3 planarReflectionDirection = normalize(reflect(
            cameraRayWorld,
            vec3(0.0, 1.0, 0.0)));
        vec3 reflectedFarWorldPosition = planarWorldPosition
            + planarReflectionDirection * 128.0;
        vec3 reflectedFarViewPosition = (viewMatrix
            * vec4(reflectedFarWorldPosition - floatingWorldOrigin, 1.0)).xyz;
        // Both projections can fail for off-screen/behind-camera directions.
        // A finite fallback prevents undefined locals (and 0*NaN propagation)
        // before the validity masks select the horizon carrier.
        vec2 directionalUv = uv;
        float directionalProjectionValid = projectToScreen(
            reflectedFarViewPosition,
            directionalUv) ? 1.0 : 0.0;
        // Convert the world-space wave normal through the actual camera view
        // and projection before deriving an image-space displacement. Adding
        // normal.xz directly to screen UV incorrectly maps world Z onto screen
        // Y; camera yaw/pitch then turns one crest family into horizontal bands
        // and makes it flip as the view rotates. The exact angular displacement
        // is deliberately bounded so the screen-space carrier keeps complete
        // tree/entity silhouettes while the resolved surface remains geometric.
        vec3 structuralLiquidNormal = normalize(vec3(
            -liquidStructuralSurfaceSlope.x,
            1.0,
            -liquidStructuralSurfaceSlope.y));
        vec3 structuralReflectionDirection = normalize(reflect(
            cameraRayWorld,
            structuralLiquidNormal));
        vec3 waveReflectedFarWorldPosition = planarWorldPosition
            + structuralReflectionDirection * 128.0;
        vec3 waveReflectedFarViewPosition = (viewMatrix
            * vec4(waveReflectedFarWorldPosition - floatingWorldOrigin, 1.0)).xyz;
        vec2 waveDirectionalUv = uv;
        float waveDirectionalProjectionValid = projectToScreen(
            waveReflectedFarViewPosition,
            waveDirectionalUv) ? 1.0 : 0.0;
        vec2 angularRippleOffset = (waveDirectionalUv - directionalUv)
            * directionalProjectionValid
            * waveDirectionalProjectionValid;
        vec2 rippleOffsetPixels = angularRippleOffset
            / max(inverseFrameSize, vec2(0.000001));
        float ripplePixelLength = length(rippleOffsetPixels);
        float maximumRipplePixels = mix(
            4.0,
            10.0,
            1.0 - liquidInterfaceRoughness);
        vec2 rippleOffset = angularRippleOffset * min(
            1.0,
            maximumRipplePixels / max(ripplePixelLength, 0.0001));
        float directionalInsideFrame = step(0.002, directionalUv.x)
            * step(directionalUv.x, 0.998)
            * step(0.002, directionalUv.y)
            * step(directionalUv.y, 0.998);
        vec2 directionalCandidateUv = clamp(
            directionalUv + rippleOffset,
            vec2(0.002),
            vec2(0.998));
        vec2 horizonCandidateUv = clamp(
            horizonMirroredUv + rippleOffset,
            vec2(0.002),
            vec2(0.998));
        // Choose one geometric mapping independently of scene occupancy. A
        // G-buffer-driven per-texel choice makes the opaque texels and alpha
        // holes of one leaf plane use different projections, fragmenting trees.
        float useDirectionalProjection = directionalProjectionValid
            * directionalInsideFrame;
        vec2 mirroredUv = mix(
            horizonCandidateUv,
            directionalCandidateUv,
            useDirectionalProjection);
        // Finite entity geometry is forward-rasterized through the same planar
        // reflection before this pass. One half-resolution filtered lookup is
        // continuous for item/animal/player silhouettes and cannot contain the
        // deferred local first-person arm. Waves perturb that physical carrier;
        // the existing directional projection remains the scenery/off-screen
        // fallback and is evaluated only when no entity covers this water pixel.
        vec2 entityMirrorUv = clamp(
            uv + rippleOffset,
            vec2(0.002),
            vec2(0.998));
        vec4 filteredReflection = entityMirrorEnabled != 0
            ? texture(entityMirrorColor, entityMirrorUv)
            : vec4(0.0);
        float entityMirrorSupport = clamp(filteredReflection.a, 0.0, 1.0);
        if (entityMirrorSupport > 0.001)
        {
            // The mirror depth reconstructs the already-reflected world point
            // with the ordinary inverse view. Geometry physically above the
            // interface appears below it after reflection; a reconstructed
            // point above the plane therefore came from a submerged source and
            // must contribute only through transmission/refraction, never the
            // surface mirror. This clips floating/crossing entities per pixel
            // instead of accepting the whole entity from one alpha value.
            float reflectedEntityDepth = texture(entityMirrorDepth, entityMirrorUv).r;
            vec4 reflectedEntityClipPosition = vec4(
                entityMirrorUv * 2.0 - 1.0,
                reflectedEntityDepth * 2.0 - 1.0,
                1.0);
            vec4 reflectedEntityViewHomogeneous = inverseProjection
                * reflectedEntityClipPosition;
            vec3 reflectedEntityViewPosition = reflectedEntityViewHomogeneous.xyz
                / max(abs(reflectedEntityViewHomogeneous.w), 0.0001);
            vec3 reflectedEntityWorldPosition = (
                inverseViewMatrix * vec4(reflectedEntityViewPosition, 1.0)).xyz
                + floatingWorldOrigin;
            float validReflectedEntityDepth = step(reflectedEntityDepth, 0.99999);
            float sourceAboveInterface = step(
                reflectedEntityWorldPosition.y,
                planarFluidSurfaceWorldY + 0.015);
            entityMirrorSupport *= validReflectedEntityDepth
                * sourceAboveInterface;
        }
        if (entityMirrorSupport <= 0.001)
        {
            filteredReflection = filteredMaskedPlanarFallbackSample(
                mirroredUv,
                planarFluidSurfaceWorldY,
                liquidInterfaceRoughness);
        }
        float filteredSceneSupport = clamp(filteredReflection.a, 0.0, 1.0);
        result.color = filteredReflection.rgb
            / max(filteredSceneSupport, 0.0001);
        vec2 reflectionCarrierUv = entityMirrorSupport > 0.001
            ? entityMirrorUv
            : mirroredUv;
        vec2 fallbackEdge = min(
            reflectionCarrierUv,
            vec2(1.0) - reflectionCarrierUv);
        float fallbackEdgeFade = smoothstep(
            0.0,
            0.08,
            min(fallbackEdge.x, fallbackEdge.y));
        // Never recursively reflect the lake's raster pixels, near-camera
        // first-person geometry, or geometry below this liquid interface.
        // A player model on the bank remains above and outside the near volume.
        float screenSceneConfidence = fallbackEdgeFade
            * filteredSceneSupport;
        vec3 reflectedSky = linearToSrgb(
            skyEnvironmentRadiance(environmentDirection));
        // Once the mirrored ray leaves the framebuffer there is no
        // screen-space scenery to sample, but open water still sees the
        // sky. Fade the clamped edge texel into a stable environment
        // radiance instead of fading the whole reflection to zero. This
        // is what keeps the near half of a lake reflective when looking
        // down from the bank.
        result.color = mix(
            reflectedSky,
            result.color,
            screenSceneConfidence);
        // A resolved local scene already represents the dominant
        // mirror direction. Only retain a narrow unresolved sky lobe
        // in that case; the previous unconditional 18-32% sky wash
        // erased wall/object contrast on otherwise valid water hits.
        // Misses and frame edges still receive the full stable sky.
        float unresolvedEnvironmentBlend = mix(
            mix(0.18, 0.32, 1.0 - liquidInterfaceRoughness),
            mix(0.035, 0.080, 1.0 - liquidInterfaceRoughness),
            screenSceneConfidence);
        result.color = mix(
            result.color,
            reflectedSky,
            unresolvedEnvironmentBlend);
        float environmentConfidence = mix(
            0.44,
            0.94,
            screenSceneConfidence);
        result.confidence = planarFallback * environmentConfidence
            * mix(0.96, 0.72, liquidInterfaceRoughness);
        result.planarConfidence = planarFallback;
    }

    if (planarFallback <= 0.001 && roughness < ssrRoughnessLimit)
    {
        for (int stepIndex = 0; stepIndex < MAX_REFLECTION_STEPS; stepIndex++)
        {
            if (stepIndex >= reflectionSteps)
            {
                break;
            }

            // The performance tier deliberately keeps a single SSR
            // probe. Sampling it at 100% of the configured range both
            // skipped nearby room geometry and produced a zero
            // distance confidence below, so even a valid hit stayed
            // black. Put that one probe at the middle of the ray while
            // preserving the multi-step distribution at higher tiers.
            float stepFraction = reflectionSteps == 1
                ? 0.70710678
                : float(stepIndex + 1) / float(reflectionSteps);
            float distanceAlongRay = roughDistance * stepFraction * stepFraction;
            vec3 rayPosition = rayOrigin + rayDirection * distanceAlongRay;
            vec2 hitUv;
            if (!projectToScreen(rayPosition, hitUv))
            {
                break;
            }

            vec3 scenePosition = texture(gPosition, hitUv).xyz;
            if (dot(scenePosition, scenePosition) < 0.0001)
            {
                continue;
            }

            float depthGap = scenePosition.z - rayPosition.z;
            float thickness = 0.06 + distanceAlongRay * 0.055;
            if (depthGap < 0.0 || depthGap > thickness)
            {
                continue;
            }

            vec3 hitNormal = normalize(texture(gNormal, hitUv).xyz);
            if (planarFallback > 0.001 && dot(hitNormal, normal) > 0.90)
            {
                // Do not let a horizontal fluid ray immediately hit the
                // same water plane and replace the coherent sky fallback
                // with dark depth-edge fragments.
                continue;
            }

            float facing = clamp(dot(hitNormal, -rayDirection), 0.0, 1.0);
            vec2 edgeDistance = min(hitUv, vec2(1.0) - hitUv);
            float edgeFade = smoothstep(0.0, 0.10, min(edgeDistance.x, edgeDistance.y));
            // Retain a bounded far-hit confidence. The previous linear
            // fade reached exactly zero on the final (and only, at the
            // performance tier) sample.
            float distanceFade = mix(1.0, 0.35, stepFraction);
            float hitConfidence = edgeFade * distanceFade * (0.25 + 0.75 * facing);
            if (hitConfidence > result.confidence)
            {
                result.color = sampleReflectionSource(hitUv);
                result.confidence = hitConfidence;
                break;
            }
        }
    }

    // Off-screen specular DDA is the most expensive reflection path.
    // Spend it on water or materially smooth receivers instead of every
    // default-roughness wall. Run the bounded SSR probe first: when its
    // confidence is strictly above the mathematical maximum of a voxel
    // hit, the DDA cannot change the selected result and is skipped.
    bool voxelReflectionRequested = (allowVoxelFallback != 0
            && planarFallback <= 0.001
            && roughness < voxelRoughnessLimit)
        || debugView == 9;
    if (voxelReflectionRequested)
    {
        vec3 voxelIncidentDirection = normalize(
            worldPosition - cameraWorldPosition);
        float voxelGrazingConfidence = 0.35
            + 0.65 * (1.0 - abs(dot(worldNormal, voxelIncidentDirection)));
        float voxelMaximumConfidence = voxelGrazingConfidence
            * (1.0 - roughness * 0.55);
        bool voxelCanChangeResult = debugView == 9
            || result.confidence <= voxelMaximumConfidence;
        if (voxelCanChangeResult)
        {
            // A confirmed water plane already has a coherent screen
            // reflection. Replacing it with the coarse voxel DDA exposed
            // stair-stepped terrain silhouettes on lake surfaces.
            ReflectionResult voxelReflection = traceVoxelReflection(
                worldPosition,
                worldNormal,
                roughness);
            result.voxelColor = voxelReflection.voxelColor;
            result.voxelConfidence = voxelReflection.voxelConfidence;
            if (planarFallback <= 0.001
                && voxelReflection.confidence > result.confidence)
            {
                result.color = voxelReflection.color;
                result.confidence = voxelReflection.confidence;
            }
        }
    }

    return result;
}

vec3 reconstructSurfaceAlbedo(
    vec3 source,
    vec4 voxelMaterial,
    float authoredAlbedoLuminance)
{
    vec3 sourceLinear = srgbToLinear(source);
    vec3 materialLinear = srgbToLinear(clamp(voxelMaterial.rgb, 0.0, 1.0));
    float sourceLuminance = dot(sourceLinear, LUMA);

    // Patched opaque terrain carries a quantized luminance sampled from
    // the authored atlas before Vintage Story applies vertex lighting.
    // Use it as the texture-detail carrier and take broad chroma from
    // GetColor, so the reconstructed albedo is stable across time of
    // day while still retaining high-frequency painted hue variation.
    if (authoredAlbedoLuminance >= 0.0)
    {
        float materialLuminance = max(dot(materialLinear, LUMA), 0.012);
        float exactDetail = clamp(
            authoredAlbedoLuminance / materialLuminance,
            0.28,
            2.75);
        vec3 detailedMaterial = materialLinear * exactDetail;
        vec3 sourceChroma = sourceLinear / max(sourceLuminance, 0.015);
        vec3 detailedSource = sourceChroma * authoredAlbedoLuminance;
        float sourceChromaConfidence = smoothstep(0.010, 0.090, sourceLuminance);
        return clamp(mix(
            detailedMaterial,
            detailedSource,
            sourceChromaConfidence * 0.58), 0.0, 1.0);
    }

    // Separate high-frequency texture detail from the broad raster light
    // gradient. The voxel colour supplies a stable unlit base while the
    // local ratio preserves authored cracks, grain and normal-map detail.
    float localLuminance = sourceLuminance;
    for (int index = 0; index < 2; index++)
    {
        if (index >= albedoDetailSamples)
        {
            break;
        }
        vec2 offset = index == 0 ? vec2(1.0, 0.0)
            : vec2(-1.0, 0.0);
        vec3 neighbor = texture(
            sourceColor,
            clamp(uv + offset * inverseFrameSize, vec2(0.0), vec2(1.0))).rgb;
        float neighborLuminance = dot(srgbToLinear(neighbor), LUMA);
        localLuminance = index == 0
            ? neighborLuminance
            : localLuminance + neighborLuminance;
    }
    if (albedoDetailSamples > 1)
    {
        localLuminance *= 0.5;
    }

    float detail = clamp(
        sourceLuminance / max(localLuminance, 0.012),
        0.52,
        1.62);
    vec3 detailedMaterial = materialLinear * mix(1.0, detail, 0.72);

    // GetColor supplies the block base colour but can flatten intentional
    // hue variation in a texture. Blend back chroma without retaining the
    // baked large-scale illumination that the transport pass replaces.
    vec3 sourceChroma = sourceLinear / max(sourceLuminance, 0.015);
    vec3 chromaMaterial = sourceChroma * max(dot(materialLinear, LUMA), 0.012);
    float chromaWeight = smoothstep(0.008, 0.060, sourceLuminance) * 0.92;
    return clamp(mix(detailedMaterial, chromaMaterial, chromaWeight), 0.0, 1.0);
}

vec3 reconstructSurfaceEmission(vec3 sourceLinear, vec3 worldPosition)
{
    float proximity = 0.0;
    for (int lightIndex = 0; lightIndex < MAX_VOXEL_LIGHTS; lightIndex++)
    {
        if (lightIndex >= voxelLightCount)
        {
            break;
        }
        float lightDistance = distance(
            worldPosition,
            voxelLightPositionIntensity[lightIndex].xyz);
        proximity = max(proximity, 1.0 - smoothstep(0.12, 0.62, lightDistance));
    }

    // Proximity alone identifies every wall or pane near a lantern as
    // emissive. Require a genuinely hot texel and keep the support
    // inside the flame/candle volume; the cage remains reflective and
    // casts shadows but no longer turns adjacent windows into light.
    float hotTexel = smoothstep(0.30, 0.78, maximumComponent(sourceLinear));
    return sourceLinear
        * proximity
        * hotTexel
        * (0.35 + hotTexel * 2.65);
}

vec3 nearestEmitterColor(vec3 worldPosition, vec3 fallbackColor)
{
    float bestWeight = 0.0;
    vec3 selectedColor = fallbackColor;
    for (int lightIndex = 0; lightIndex < MAX_VOXEL_LIGHTS; lightIndex++)
    {
        if (lightIndex >= voxelLightCount)
        {
            break;
        }

        float lightDistance = distance(
            worldPosition,
            voxelLightPositionIntensity[lightIndex].xyz);
        float weight = 1.0 - smoothstep(0.20, 1.80, lightDistance);
        if (weight > bestWeight)
        {
            bestWeight = weight;
            selectedColor = voxelLightColorRadius[lightIndex].rgb;
        }
    }
    return emitterColor(selectedColor);
}

vec3 filmicToneMap(vec3 radiance)
{
    // Exposure belongs to scene-linear radiance. Applying it after the
    // sRGB transfer function changed saturation and contrast instead of
    // photographic exposure, especially around warm emitters.
    vec3 positiveRadiance = max(radiance, vec3(0.0));
    vec3 exposedRadiance = positiveRadiance * exp2(exposure);
    float exposedLuminance = max(dot(exposedRadiance, LUMA), 0.0);
    if (exposedLuminance <= 0.000001)
    {
        return vec3(0.0);
    }

    // A calibrated scene-linear fit supplies a toe for enclosed rooms
    // and a broad shoulder for sun/specular energy. The previous 1.72
    // pre-scale lifted diffuse midtones into a bleached display range;
    // 0.65 keeps 18% radiance close to perceptual middle grey.
    float value = exposedLuminance * 0.65;
    float mappedLuminance = clamp(
        value * (2.51 * value + 0.03)
            / (value * (2.43 * value + 0.59) + 0.14),
        0.0,
        1.0);

    // Rescale RGB together to preserve emitter hue. If the resulting
    // chroma leaves the display gamut, compress only the chromatic
    // component around the mapped luminance; peak normalization would
    // lower luminance and turn bright terrain into neon colour bands.
    vec3 neutral = vec3(mappedLuminance);
    vec3 chroma = exposedRadiance
        * (mappedLuminance / exposedLuminance)
        - neutral;
    // Keep both ends of the display gamut continuous. Limiting only the
    // positive chroma excursion allowed a saturated green to push red and
    // blue below zero; the final clamp then changed its hue instead of
    // compressing its chroma around the preserved luminance.
    float positiveChromaPeak = maximumComponent(chroma);
    float negativeChromaPeak = maximumComponent(-chroma);
    float upperGamutCompression = positiveChromaPeak > 0.00001
        ? (1.0 - mappedLuminance) / positiveChromaPeak
        : 1.0;
    float lowerGamutCompression = negativeChromaPeak > 0.00001
        ? mappedLuminance / negativeChromaPeak
        : 1.0;
    float gamutCompression = min(
        1.0,
        min(upperGamutCompression, lowerGamutCompression));
    float highlightSaturation = 1.0
        - smoothstep(0.55, 0.92, mappedLuminance) * 0.18;
    vec3 mapped = neutral
        + chroma * gamutCompression * highlightSaturation;
    return linearToSrgb(clamp(mapped, 0.0, 1.0));
}

vec3 applyDisplayGrade(vec3 color)
{
    float luminance = dot(color, LUMA);
    color = mix(vec3(luminance), color, saturation);

    float colorRange = max(color.r, max(color.g, color.b))
        - min(color.r, min(color.g, color.b));
    float adaptiveVibrance = vibrance * (1.0 - clamp(colorRange, 0.0, 1.0));
    color = mix(vec3(luminance), color, 1.0 + adaptiveVibrance);

    color = (color - vec3(0.5)) * contrast + vec3(0.5);
    return clamp(color, 0.0, 1.0);
}

vec2 temporalDenoiseOffset(int index)
{
    if (index == 0) return vec2(1.0, 0.0);
    if (index == 1) return vec2(-1.0, 0.0);
    if (index == 2) return vec2(0.0, 1.0);
    return vec2(0.0, -1.0);
}

vec3 denoiseTemporalHistory(
    vec3 centerPosition,
    vec3 centerNormal,
    vec3 centerSource,
    vec3 currentColor,
    bool hasGeometry)
{
    vec3 centerHistory = texture(historyColor, uv).rgb;
    if (!hasGeometry)
    {
        return centerHistory;
    }

    // Cross-bilateral temporal denoiser: only transport deltas belonging
    // to the same surface and source-colour neighbourhood are mixed. Keeping
    // each sample relative to its own full-resolution carrier prevents the
    // denoiser from blurring texture, alpha-test and lantern-bar detail.
    vec3 centerCarrier = outputColorDomain != 0
        ? centerSource
        : applyDisplayGrade(centerSource);
    vec3 accumulatedDelta = centerHistory - centerCarrier;
    float accumulatedWeight = 1.0;
    vec3 coherentMinimumDelta = vec3(1000.0);
    vec3 coherentMaximumDelta = vec3(-1000.0);
    int coherentNeighborCount = 0;
    float centerLuminance = dot(centerSource, LUMA);
    float luminanceSum = centerLuminance;
    float luminanceSquaredSum = centerLuminance * centerLuminance;
    float depthScale = max(abs(centerPosition.z) * 0.015, 0.035);

    for (int index = 0; index < 4; index++)
    {
        if (index >= temporalDenoiseSamples)
        {
            break;
        }
        vec2 sampleUv = clamp(
            uv + temporalDenoiseOffset(index) * inverseFrameSize,
            inverseFrameSize * 0.5,
            vec2(1.0) - inverseFrameSize * 0.5);
        vec3 neighborSource = texture(sourceColor, sampleUv).rgb;
        vec3 neighborCarrier = outputColorDomain != 0
            ? neighborSource
            : applyDisplayGrade(neighborSource);
        float neighborLuminance = dot(neighborSource, LUMA);
        luminanceSum += neighborLuminance;
        luminanceSquaredSum += neighborLuminance * neighborLuminance;

        vec3 neighborPosition = texture(gPosition, sampleUv).xyz;
        vec3 encodedNeighborNormal = texture(gNormal, sampleUv).xyz;
        float neighborNormalLength = length(encodedNeighborNormal);
        if (dot(neighborPosition, neighborPosition) < 0.0001
            || neighborNormalLength <= 0.1)
        {
            continue;
        }

        vec3 neighborNormal = encodedNeighborNormal / neighborNormalLength;
        float normalWeight = pow(max(dot(centerNormal, neighborNormal), 0.0), 24.0);
        float depthWeight = exp(
            -abs(neighborPosition.z - centerPosition.z) / depthScale);
        float sourceWeight = exp(-length(neighborSource - centerSource) * 28.0);
        float geometryWeight = normalWeight * depthWeight;
        float weight = geometryWeight * sourceWeight;
        vec3 neighborDelta = texture(historyColor, sampleUv).rgb
            - neighborCarrier;
        // A one-pixel-long voxel ray can occasionally miss a distant caster
        // while every coherent neighbour hits it. Build a source-independent
        // transport envelope from the same geometric surface: the carrier-
        // relative representation preserves authored texture even when the
        // correction is clamped to that neighbourhood.
        if (geometryWeight > 0.05)
        {
            coherentMinimumDelta = min(coherentMinimumDelta, neighborDelta);
            coherentMaximumDelta = max(coherentMaximumDelta, neighborDelta);
            coherentNeighborCount++;
        }
        accumulatedDelta += neighborDelta * weight;
        accumulatedWeight += weight;
    }

    vec3 filteredDelta = accumulatedDelta / max(accumulatedWeight, 0.001);
    if (coherentNeighborCount >= 2)
    {
        // Excluding the centre from this envelope is intentional: a stable
        // isolated visibility miss would otherwise enlarge its own bounds and
        // survive forever. A small radiometric margin retains soft penumbrae.
        const float transportEnvelopeMargin = 0.020;
        filteredDelta = clamp(
            filteredDelta,
            coherentMinimumDelta - vec3(transportEnvelopeMargin),
            coherentMaximumDelta + vec3(transportEnvelopeMargin));
    }
    vec3 filteredHistory = centerCarrier + filteredDelta;
    float temporalSampleCount = float(temporalDenoiseSamples + 1);
    float meanLuminance = luminanceSum / temporalSampleCount;
    float variance = max(
        luminanceSquaredSum / temporalSampleCount
            - meanLuminance * meanLuminance,
        0.0);
    float sigma = sqrt(variance);
    float effectMagnitude = min(length(currentColor - centerCarrier), 0.16);
    float clippingRadius = 0.018 + sigma * 0.9 + effectMagnitude * 0.65;

    // Variance clipping prevents one anomalous ray from being retained
    // for dozens of frames while leaving bounded bounce and shadows.
    return clamp(
        filteredHistory,
        currentColor - vec3(clippingRadius),
        currentColor + vec3(clippingRadius));
}

void main()
{
    vec3 position = vec3(0.0);
    vec4 encodedNormalRoughness = vec4(0.0);
    // The performance tier deliberately disables both screen-space
    // traces, but voxel lighting still consumes the terrain position,
    // authored normal and packed roughness. Omitting that consumer made
    // hasGeometry false and returned the untouched raster before any
    // PBR material contribution could run.
    bool deferredGeometryRequired = voxelLightingEnabled != 0
        || screenSpaceLightingEnabled != 0
        || screenSpaceReflectionsEnabled != 0
        || shadowPass != 0
        || debugView != 0;
    if (deferredGeometryRequired)
    {
        position = texture(gPosition, uv).xyz;
        encodedNormalRoughness = texture(gNormal, uv);
    }
    vec3 encodedNormal = encodedNormalRoughness.xyz;
    float normalLength = length(encodedNormal);
    bool hasGeometry = dot(position, position) > 0.0001 && normalLength > 0.1;
    bool geometryWasRepaired = false;
    vec4 repairedPointVisibilityA = vec4(0.0);
    vec4 repairedPointVisibilityB = vec4(0.0);
    float repairedSunVisibility = 0.0;
    int repairedShadowVisibilityCount = 0;
    // Raw/filter masks classify only real G-buffer receivers. Repeating the
    // four-neighbour hole repair in both half-resolution passes spent ten
    // G-buffer reads on every sky fragment and traced synthetic receivers.
    // The final pass already owns this repair and inherits the temporally
    // filtered RG visibility of the exact coherent neighbours below.
    if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)
    {
        vec3 filledPosition = vec3(0.0);
        vec3 filledNormal = vec3(0.0);
        vec3 referencePosition = vec3(0.0);
        vec3 referenceNormal = vec3(0.0);
        float filledRoughness = 0.0;
        int consistentNeighbours = 0;
        for (int index = 0; index < 4; index++)
        {
            vec2 neighbourUv = clamp(
                uv + temporalDenoiseOffset(index) * inverseFrameSize,
                inverseFrameSize * 0.5,
                vec2(1.0) - inverseFrameSize * 0.5);
            vec3 candidatePosition = texture(gPosition, neighbourUv).xyz;
            vec4 candidateNormalRoughness = texture(gNormal, neighbourUv);
            float candidateNormalLength = length(candidateNormalRoughness.xyz);
            if (dot(candidatePosition, candidatePosition) <= 0.0001
                || candidateNormalLength <= 0.1)
            {
                continue;
            }

            vec3 candidateNormal = candidateNormalRoughness.xyz
                / candidateNormalLength;
            if (consistentNeighbours > 0
                && (dot(candidateNormal, referenceNormal) < 0.92
                    || abs(candidatePosition.z - referencePosition.z)
                        > max(0.05, abs(referencePosition.z) * 0.006)))
            {
                continue;
            }

            if (consistentNeighbours == 0)
            {
                referencePosition = candidatePosition;
                referenceNormal = candidateNormal;
            }
            filledPosition += candidatePosition;
            filledNormal += candidateNormal;
            filledRoughness += candidateNormalRoughness.a;
            if (prefilteredShadowVisibility != 0)
            {
                // Every point-light slot and the sun inherit only the same
                // coherent receiver neighbours. Linear sampling performs the
                // intended half-resolution mask reconstruction without
                // collapsing physically independent sources.
                repairedPointVisibilityA += clamp(
                    texture(shadowPointHistoryA, neighbourUv),
                    0.0,
                    1.0);
                repairedPointVisibilityB += clamp(
                    texture(shadowPointHistoryB, neighbourUv),
                    0.0,
                    1.0);
                repairedSunVisibility += clamp(
                    texture(shadowSunHistory, neighbourUv).r,
                    0.0,
                    1.0);
                repairedShadowVisibilityCount++;
            }
            consistentNeighbours++;
        }

        // Repair only a surrounded one-pixel hole. A silhouette has at
        // most one coherent neighbour in the cross, so sky and foliage
        // outlines are not grown by this conservative G-buffer dilation.
        if (consistentNeighbours >= 2)
        {
            position = filledPosition / float(consistentNeighbours);
            encodedNormal = normalize(filledNormal);
            encodedNormalRoughness = vec4(
                encodedNormal,
                filledRoughness / float(consistentNeighbours));
            normalLength = 1.0;
            hasGeometry = true;
            geometryWasRepaired = true;
        }
    }
    vec3 normal = hasGeometry
        ? encodedNormal / normalLength
        : vec3(0.0, 0.0, 1.0);
    if (shadowPass == 2)
    {
        vec4 filteredPointA = vec4(1.0);
        vec4 filteredPointB = vec4(1.0);
        float filteredSun = 1.0;
        if (hasGeometry)
        {
            filterShadowVisibilities(
                position,
                normal,
                filteredPointA,
                filteredPointB,
                filteredSun);
        }
        outColor = filteredPointA;
        outShadowPointB = filteredPointB;
        outShadowSun = vec4(filteredSun, 0.0, 0.0, 1.0);
        return;
    }
    if (shadowPass == 1)
    {
        vec4 rawPointA = vec4(1.0);
        vec4 rawPointB = vec4(1.0);
        float rawSun = 1.0;
        if (hasGeometry && voxelLightingEnabled != 0)
        {
            vec3 rawRelativeWorldPosition = (
                inverseViewMatrix * vec4(position, 1.0)).xyz;
            vec3 rawWorldPosition = rawRelativeWorldPosition + floatingWorldOrigin;
            if (isInsideVoxelVolume(rawWorldPosition))
            {
                vec3 rawWorldNormal = normalize(
                    (inverseViewMatrix * vec4(normal, 0.0)).xyz);
                vec3 positionDx = dFdx(rawWorldPosition);
                vec3 positionDy = dFdy(rawWorldPosition);
                vec3 derivativeNormal = cross(positionDx, positionDy);
                vec3 worldGeometricNormal = rawWorldNormal;
                if (dot(derivativeNormal, derivativeNormal) > 0.000001)
                {
                    derivativeNormal = normalize(derivativeNormal);
                    worldGeometricNormal = dot(derivativeNormal, rawWorldNormal) < 0.0
                        ? -derivativeNormal
                        : derivativeNormal;
                }
                traceRawPointShadowVisibilities(
                    rawWorldPosition,
                    worldGeometricNormal,
                    rawPointA,
                    rawPointB);
                rawSun = traceRawSunShadowVisibility(
                    rawWorldPosition,
                    rawRelativeWorldPosition,
                    worldGeometricNormal);
            }
        }
        outColor = rawPointA;
        outShadowPointB = rawPointB;
        outShadowSun = vec4(rawSun, 0.0, 0.0, 1.0);
        return;
    }
    if (debugView == 0 && !hasGeometry)
    {
        outColor = vec4(texture(sourceColor, uv).rgb, 1.0);
        return;
    }
    vec3 earlyRelativeWorldPosition = (
        inverseViewMatrix * vec4(position, 1.0)).xyz;
    vec3 earlyWorldPosition = earlyRelativeWorldPosition + floatingWorldOrigin;
    vec3 earlyWorldNormal = normalize(
        (inverseViewMatrix * vec4(normal, 0.0)).xyz);
    float earlyRainExposure = hasGeometry
        ? sampleRainExposure(earlyWorldPosition, earlyWorldNormal)
        : 0.0;

    // First-person hands and held meshes can either omit the deferred
    // position or write their own near-camera geometry. In both cases they
    // must remain the direct raster carrier: relighting/interlacing them as
    // the lake or terrain behind the overlay creates the visible arm parity
    // pattern and lets a carried torch leak into temporal reflections.
    float primaryFirstPersonOverlay = hasGeometry
        ? max(
            firstPersonOverlayEvidence(position),
            deferredFirstPersonOverlayEvidence(uv, position))
        : 0.0;
    if (debugView == 0
        && primaryFirstPersonOverlay > 0.001)
    {
        outColor = vec4(texture(sourceColor, uv).rgb, 1.0);
        return;
    }

    // Reject alternate transport pixels before any screen/voxel ray is
    // traced. The old late return preserved a checkerboard image but
    // still paid every traversal, so it did not stabilize high-refresh
    // frame times. Water participates as well: each half-resolution
    // phase is refreshed on the next frame and the spatially filtered
    // history below reconstructs the missing reflection samples.
    if (transportInterlace != 0
        && temporalBlend > 0.001
        && debugView == 0
        && hasGeometry
        && rainWetness * earlyRainExposure <= 0.02
        && ((int(gl_FragCoord.x) + int(gl_FragCoord.y) + temporalFrameIndex) & 1) != 0)
    {
        // Reconstruct skipped transport from a compact spatial cross.
        // A single history texel preserved the alternating phase on
        // small, bright emitters and exposed the interlace as a visible
        // checkerboard. Neighbour agreement removes that phase without
        // spending another screen/voxel ray on the skipped pixel.
        vec3 interlacedHistoryCenter = texture(historyColor, uv).rgb;
        // Alternate the reconstruction axis with the transport phase. The
        // next frame supplies the orthogonal pair, retaining a two-axis
        // footprint over time while avoiding two history reads per skipped
        // pixel at the performance tier.
        vec2 interlacedHistoryAxis = (temporalFrameIndex & 1) == 0
            ? vec2(inverseFrameSize.x, 0.0)
            : vec2(0.0, inverseFrameSize.y);
        vec3 interlacedHistoryCross =
            texture(historyColor, uv + interlacedHistoryAxis).rgb
            + texture(historyColor, uv - interlacedHistoryAxis).rgb;
        vec3 interlacedHistoryAverage = interlacedHistoryCross * 0.50;
        float interlacedCrossDeviation = maximumComponent(
            abs(interlacedHistoryAverage - interlacedHistoryCenter));
        float interlacedReconstructionWeight = 0.58
            * (1.0 - smoothstep(0.010, 0.050, interlacedCrossDeviation));
        vec3 reconstructedHistory = mix(
            interlacedHistoryCenter,
            interlacedHistoryAverage,
            interlacedReconstructionWeight);
        reconstructedHistory = clamp(
            reconstructedHistory,
            interlacedHistoryCenter - vec3(0.10),
            interlacedHistoryCenter + vec3(0.10));
        outColor = vec4(reconstructedHistory, 1.0);
        return;
    }

    // Alternate performance pixels have already returned. Defer the
    // full-resolution carrier and packed-material fetches so the skipped
    // half pays only the G-buffer classification plus history reconstruction.
    vec3 source = texture(sourceColor, uv).rgb;
    // Packed material flags are integer data stored in the glow buffer.
    // Fetch the exact framebuffer texel: linear filtering would blend
    // adjacent bit fields and randomly turn edges metallic/emissive.
    ivec2 materialSize = textureSize(gMaterial, 0);
    ivec2 materialPixel = clamp(
        ivec2(gl_FragCoord.xy),
        ivec2(0),
        materialSize - ivec2(1));
    vec4 deferredMaterial = texelFetch(gMaterial, materialPixel, 0);
    // VintageRTX terrain encodes 5-bit roughness and unlit albedo
    // luminance in positive normal alpha. Animated entities use the same
    // payload with a negative sign, which prevents their material from ever
    // borrowing the unrelated voxel/albedo behind the rasterized entity.
    float packedSurfaceAlpha = encodedNormalRoughness.a;
    float dynamicSurface = step(packedSurfaceAlpha, -0.0005);
    float packedSurfaceValue = abs(packedSurfaceAlpha);
    float packedSurfaceAvailable = step(0.0005, packedSurfaceValue)
        * (1.0 - step(0.9995, packedSurfaceValue));
    float packedSurfacePayload = clamp(
        floor(packedSurfaceValue * 1025.0 - 1.0 + 0.5),
        0.0,
        1023.0);
    float packedRoughnessBits = floor(packedSurfacePayload / 32.0);
    float packedAlbedoBits = packedSurfacePayload
        - packedRoughnessBits * 32.0;
    float surfaceRoughness = packedSurfaceAvailable > 0.5
        ? clamp((packedRoughnessBits + 0.5) / 32.0, 0.04, 1.0)
        : 0.72;
    float authoredAlbedoLuminance = packedSurfaceAvailable > 0.5
        ? (packedAlbedoBits + 0.5) / 32.0
        : -1.0;
    int packedMaterial = int(floor(deferredMaterial.b * 255.0 + 0.5));
    float pbrPayloadPresent = (packedMaterial & 32) != 0 ? 1.0 : 0.0;
    float materialMapPresent = (packedMaterial & 64) != 0 ? 1.0 : 0.0;
    float authoredMetallic = float(packedMaterial & 3) / 3.0;
    float authoredEmissive = float((packedMaterial >> 2) & 7) / 7.0;
    float rasterGlow = clamp(deferredMaterial.r, 0.0, 1.0);
    float vegetationSurface = dynamicSurface > 0.5
        ? 0.0
        : ((packedMaterial & 128) != 0 ? 1.0 : 0.0);

    LightingResult lighting;
    lighting.indirect = vec3(0.0);
    lighting.occlusion = 0.0;
    lighting.confidence = 0.0;

    VoxelLightingResult voxelLighting;
    voxelLighting.direct = vec3(0.0);
    voxelLighting.directSpecular = vec3(0.0);
    voxelLighting.blockedDirect = vec3(0.0);
    voxelLighting.bounce = vec3(0.0);
    voxelLighting.irradianceCache = vec3(0.0);
    voxelLighting.irradianceDirection = vec3(0.0, 1.0, 0.0);
    voxelLighting.sunDirect = vec3(0.0);
    voxelLighting.skyDirect = vec3(0.0);
    voxelLighting.material = vec4(0.0);
    voxelLighting.shadow = 0.0;
    voxelLighting.visibility = 1.0;
    voxelLighting.sunVisibility = 1.0;
    voxelLighting.sunShadow = 0.0;
    voxelLighting.cameraAlignedShadow = 0.0;
    voxelLighting.skyVisibility = 0.0;

    if (screenSpaceLightingEnabled != 0 && hasGeometry)
    {
        lighting = traceScreenSpaceLighting(position, normal);
    }

    ReflectionResult reflection;
    reflection.color = vec3(0.0);
    reflection.confidence = 0.0;
    reflection.voxelColor = vec3(0.0);
    reflection.voxelConfidence = 0.0;
    reflection.planarConfidence = 0.0;
    reflection.planarWorldPosition = vec3(0.0);
    reflection.fluidColumnSupport = 0.0;
    reflection.waterColorHint = 0.0;
    reflection.waterEvidence = 0.0;
    reflection.liquidPathLength = 0.0;
    reflection.liquidFresnel = 0.0;
    reflection.liquidAbsorption = vec3(0.0);
    reflection.liquidScattering = vec3(0.0);
    reflection.liquidEmission = vec3(0.0);
    reflection.liquidTransmission = 1.0;
    reflection.liquidOpaque = 0.0;
    reflection.liquidAnisotropy = 0.0;
    reflection.liquidRoughness = 1.0;
    reflection.liquidSurfaceDynamics = 0.0;
    reflection.liquidBubbleEmission = 0.0;
    reflection.liquidMetresPerWorldBlock = 1.0;
    reflection.liquidTransmissionUv = uv;
    reflection.liquidTransmissionConfidence = 0.0;
    reflection.liquidPartialGeometryFace = 0.0;
    reflection.liquidVerticalFaceEvidence = 0.0;
    reflection.liquidShoreFaceEvidence = 0.0;
    reflection.liquidShoreCarrierEvidence = 0.0;
    reflection.liquidShorePartialGeometryEvidence = 0.0;
    reflection.liquidShoreColumnEvidence = 0.0;
    reflection.liquidShoreSurfaceUv = uv;
    reflection.liquidShoreSurfaceConfidence = 0.0;
    // Performance skips the screen-depth walk but retains the off-screen
    // voxel/environment branch. Keeping this call alive for either source
    // makes the lowest tier a real reflection LOD instead of silently
    // disabling reflections together with SSR.
    if ((screenSpaceReflectionsEnabled != 0 || voxelReflectionsEnabled != 0)
        && hasGeometry)
    {
        reflection = traceScreenSpaceReflection(
            position,
            normal,
            earlyWorldPosition,
            earlyWorldNormal,
            surfaceRoughness,
            source,
            1);
    }
    // The clean late-opaque carrier predates transparent liquids. Where the
    // official liquid mesh emitted an anti-crack face through partial geometry,
    // combine that carrier with the liquid through dielectric Fresnel and
    // Beer-Lambert transmission. Replacing it outright exposes a black lake-bed
    // stripe; retaining the late face outright leaves an opaque turquoise wall.
    float partialLiquidRecovery = clamp(
        reflection.liquidPartialGeometryFace,
        0.0,
        1.0);
    if (partialLiquidRecovery > 0.001)
    {
        LiquidOpticalProfile fallbackShoreProfile = defaultWaterOpticalProfile();
        float shoreOpticsEvidence = step(
            0.0001,
            dot(
                reflection.liquidAbsorption + reflection.liquidScattering,
                vec3(1.0)));
        vec3 shoreAbsorption = mix(
            fallbackShoreProfile.absorption,
            reflection.liquidAbsorption,
            shoreOpticsEvidence);
        vec3 shoreScattering = mix(
            fallbackShoreProfile.scattering,
            reflection.liquidScattering,
            shoreOpticsEvidence);
        float shoreTransmission = mix(
            fallbackShoreProfile.transmission,
            reflection.liquidTransmission,
            shoreOpticsEvidence);
        float shoreOpaque = mix(
            fallbackShoreProfile.opaque,
            reflection.liquidOpaque,
            shoreOpticsEvidence);
        float shoreMetresPerWorldBlock = mix(
            fallbackShoreProfile.metresPerWorldBlock,
            reflection.liquidMetresPerWorldBlock,
            shoreOpticsEvidence);
        vec3 shoreViewDirection = normalize(
            earlyWorldPosition - cameraWorldPosition);
        float fallbackShoreFresnel = exactDielectricFresnel(
            clamp(abs(shoreViewDirection.y), 0.001, 1.0),
            1.000293,
            fallbackShoreProfile.ior);
        float shoreFresnel = mix(
            fallbackShoreFresnel,
            reflection.liquidFresnel,
            shoreOpticsEvidence);
        float shorePathLength = mix(
            clamp(
                0.25 / max(abs(shoreViewDirection.y), 0.25),
                0.25,
                1.0),
            reflection.liquidPathLength,
            shoreOpticsEvidence);
        vec3 shoreExtinction = liquidExtinctionCoefficient(
            shoreAbsorption,
            shoreScattering,
            shoreTransmission,
            shoreOpaque);
        vec3 shoreTransmittance = exp(
            -shoreExtinction
                * shorePathLength
                * shoreMetresPerWorldBlock);
        vec3 horizontalLiquidCarrier = mix(
            source,
            texture(sourceColor, reflection.liquidShoreSurfaceUv).rgb,
            reflection.liquidShoreSurfaceConfidence);
        vec3 lateLiquidLinear = srgbToLinear(horizontalLiquidCarrier);
        vec3 opaqueShoreLinear = srgbToLinear(sampleReflectionSource(uv));
        float shoreMeanTransmittance = dot(shoreTransmittance, LUMA);
        float liquidCarrierWeight = shoreFresnel
            + (1.0 - shoreMeanTransmittance)
                * (1.0 - shoreFresnel)
                * 0.35;
        vec3 physicalShoreComposite = opaqueShoreLinear
                * shoreTransmittance
                * (1.0 - shoreFresnel)
            + lateLiquidLinear * liquidCarrierWeight;
        // In the occupied pixels of a crossed plant, fence, stair, or other
        // partial mesh, the opaque snapshot is the exact alpha-tested solid
        // silhouette. Water remains visible and reflective in its holes, but
        // cannot optically pass through the solid fragments themselves.
        float exactPartialOccluder = clamp(
            partialLiquidRecovery
                * reflection.liquidShorePartialGeometryEvidence,
            0.0,
            1.0);
        vec3 recoveredShoreSource = mix(
            linearToSrgb(clamp(
                physicalShoreComposite,
                vec3(0.0),
                vec3(1.0))),
            sampleReflectionSource(uv),
            exactPartialOccluder);
        source = mix(
            source,
            recoveredShoreSource,
            partialLiquidRecovery);
    }

    vec3 worldPosition = earlyWorldPosition;
    vec3 worldNormal = earlyWorldNormal;
    vec3 worldGeometricNormal = vec3(0.0, 1.0, 0.0);
    float specularFilteredSurfaceRoughness = surfaceRoughness;
    if (voxelLightingEnabled != 0 && hasGeometry)
    {
        specularFilteredSurfaceRoughness = filterSpecularRoughness(
            surfaceRoughness,
            worldNormal);
        vec3 positionDx = dFdx(worldPosition);
        vec3 positionDy = dFdy(worldPosition);
        vec3 derivativeNormal = cross(positionDx, positionDy);
        if (dot(derivativeNormal, derivativeNormal) > 0.000001)
        {
            derivativeNormal = normalize(derivativeNormal);
            worldGeometricNormal = dot(derivativeNormal, worldNormal) < 0.0
                ? -derivativeNormal
                : derivativeNormal;
        }
        else
        {
            worldGeometricNormal = worldNormal;
        }
        if (isInsideVoxelVolume(worldPosition))
        {
            if (reflection.planarConfidence > 0.001)
            {
                // Confirmed water preserves engine transmission and uses
                // the planar specular path. Do not pay sun/sky/GI/point
                // traversals whose diffuse result would be discarded.
                voxelLighting.material = sampleVoxelAtWorld(
                    reflection.planarWorldPosition - vec3(0.0, 0.08, 0.0));
            }
            else
            {
                // Real receivers keep the exact center-mask path. A synthetic
                // one-pixel receiver inherits at least two already filtered,
                // geometrically coherent neighbours rather than forcing both
                // half-resolution passes to reconstruct and trace empty sky.
                vec4 resolvedPointVisibilityA = vec4(1.0);
                vec4 resolvedPointVisibilityB = vec4(1.0);
                float resolvedSunVisibility = 1.0;
                if (prefilteredShadowVisibility != 0)
                {
                    if (geometryWasRepaired
                        && repairedShadowVisibilityCount >= 2)
                    {
                        float inverseRepairCount = 1.0
                            / float(repairedShadowVisibilityCount);
                        resolvedPointVisibilityA = clamp(
                            repairedPointVisibilityA * inverseRepairCount,
                            0.0,
                            1.0);
                        resolvedPointVisibilityB = clamp(
                            repairedPointVisibilityB * inverseRepairCount,
                            0.0,
                            1.0);
                        resolvedSunVisibility = clamp(
                            repairedSunVisibility * inverseRepairCount,
                            0.0,
                            1.0);
                    }
                    else
                    {
                        resolvedPointVisibilityA = clamp(
                            texture(shadowPointHistoryA, uv),
                            0.0,
                            1.0);
                        resolvedPointVisibilityB = clamp(
                            texture(shadowPointHistoryB, uv),
                            0.0,
                            1.0);
                        resolvedSunVisibility = clamp(
                            texture(shadowSunHistory, uv).r,
                            0.0,
                            1.0);
                    }
                }
                voxelLighting = traceVoxelPointLight(
                    worldPosition,
                    earlyRelativeWorldPosition,
                    worldNormal,
                    worldGeometricNormal,
                    specularFilteredSurfaceRoughness,
                    materialMapPresent * authoredMetallic,
                    resolvedPointVisibilityA,
                    resolvedPointVisibilityB,
                    resolvedSunVisibility);
            }
        }
    }

    if (debugView == 1)
    {
        outColor = vec4(hasGeometry ? normal * 0.5 + 0.5 : vec3(0.0), 1.0);
        return;
    }

    // Wind animation is not a complete vegetation classifier: static flowers,
    // reeds and some crossed-plane plants legitimately omit WindModeBitMask.
    // The voxel scene therefore carries the block material as a separate low
    // alpha bit. It is sampled at the exact visible surface, so ground below a
    // plant remains eligible for physical relighting while the plant fragment
    // retains its alpha-tested raster chroma and coverage.
    int voxelMaterialBits = int(floor(voxelLighting.material.a * 255.0 + 0.5));
    float voxelVegetationSurface = (voxelMaterialBits & 8) != 0 ? 1.0 : 0.0;
    vegetationSurface = max(
        vegetationSurface,
        voxelVegetationSurface * (1.0 - dynamicSurface));

    if (debugView == 12)
    {
        // Liquid diagnostic ABI: R=surface presence modulated by animated
        // wave/impact/bubble response, G=refracted path depth, and
        // B=Beer-Lambert mean transmittance. Presence keeps a 0.55 floor so
        // temporal capture differences expose dynamics without hiding volume.
        vec3 diagnosticLiquidExtinction = liquidExtinctionCoefficient(
            reflection.liquidAbsorption,
            reflection.liquidScattering,
            reflection.liquidTransmission,
            reflection.liquidOpaque);
        vec3 diagnosticLiquidTransmittance = exp(
            -diagnosticLiquidExtinction * reflection.liquidPathLength);
        float diagnosticMeanTransmittance = dot(
            diagnosticLiquidTransmittance,
            LUMA);
        vec3 surfaceDiagnostic = vec3(
            reflection.waterEvidence
                * mix(0.55, 1.0, reflection.liquidSurfaceDynamics),
            reflection.waterEvidence
                * (1.0 - exp(-reflection.liquidPathLength * 0.22)),
            reflection.waterEvidence * diagnosticMeanTransmittance);
        float unresolvedVerticalFace = reflection.liquidVerticalFaceEvidence
            * (1.0 - step(0.001, reflection.waterEvidence));
        vec3 verticalFaceDiagnostic = vec3(
            unresolvedVerticalFace,
            reflection.liquidShoreFaceEvidence,
            reflection.liquidPartialGeometryFace);
        // Keep this view a strict liquid-presence mask. Classification factors
        // are deliberately excluded: opaque vegetation and terrain can satisfy
        // those factors while merely bordering water, and would otherwise be
        // misidentified as liquid by real-image validation.
        outColor = vec4(max(surfaceDiagnostic, verticalFaceDiagnostic), 1.0);
        return;
    }

    if (debugView == 14)
    {
        // Unmodified color copied at VintageRTX's opaque-stage boundary.
        // This view proves whether a held first-person mesh contaminated the
        // reflection carrier before any planar/SSR/refraction computation.
        outColor = vec4(sampleReflectionSource(uv), 1.0);
        return;
    }

    if (debugView == 15)
    {
        // Dynamic surface ABI: red/blue encode signed height around 50 %,
        // green exposes horizontal-normal magnitude, and inactive/non-liquid
        // pixels remain black. Sampling at the resolved planar world point
        // exercises the same CPU-origin/cell-size/texture mapping as shading.
        vec4 dynamicState = vec4(0.0);
        bool hasDynamicState = reflection.waterEvidence > 0.001
            && sampleDynamicLiquidSurface(
                reflection.planarWorldPosition,
                dynamicState);
        // Five centimetres covers ordinary thrown-item and wind response while
        // still making millimetric rings visible in lossless captures.
        const float diagnosticFieldHeightRangeMetres = 0.05;
        float signedFieldHeight = clamp(
            dynamicState.x / diagnosticFieldHeightRangeMetres,
            -1.0,
            1.0);
        float horizontalNormalMagnitude = clamp(
            length(dynamicState.yz),
            0.0,
            1.0);
        outColor = hasDynamicState
            ? vec4(
                0.5 + 0.5 * signedFieldHeight,
                horizontalNormalMagnitude,
                0.5 - 0.5 * signedFieldHeight,
                1.0)
            : vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    if (debugView == 16)
    {
        // Lossless view of the dedicated entity carrier. Alpha is shown as
        // luminance so holes and disconnected small-item silhouettes remain
        // directly inspectable in ordinary RGB screenshots.
        vec4 entityMirror = entityMirrorEnabled != 0
            ? texture(entityMirrorColor, uv)
            : vec4(0.0);
        outColor = vec4(entityMirror.rgb * entityMirror.a, 1.0);
        return;
    }

    if (debugView == 2)
    {
        float viewDepth = hasGeometry ? max(-position.z, 0.0) : 0.0;
        float depthDisplay = 1.0 - exp(-viewDepth * 0.025);
        outColor = vec4(vec3(depthDisplay), 1.0);
        return;
    }

    if (debugView == 3)
    {
        outColor = vec4(lighting.indirect + vec3(lighting.occlusion * 0.35), 1.0);
        return;
    }

    if (debugView == 4)
    {
        outColor = vec4(voxelLighting.material.rgb, 1.0);
        return;
    }

    if (debugView == 5)
    {
        outColor = vec4(
            voxelLighting.visibility,
            voxelLighting.sunVisibility,
            min(voxelLighting.visibility, voxelLighting.sunVisibility),
            1.0);
        return;
    }

    if (debugView == 6)
    {
        // R=aggregate point shadow, G=normalized projected sun shadow,
        // B=false self-shadow assigned to a camera-aligned held emitter.
        float sunReceiverForDebug = max(
            dot(worldGeometricNormal, sunDirection),
            0.0);
        float normalizedSunShadow = sunReceiverForDebug > 0.001
            ? voxelLighting.sunShadow / sunReceiverForDebug
            : 0.0;
        outColor = vec4(
            clamp(voxelLighting.shadow, 0.0, 1.0),
            clamp(normalizedSunShadow, 0.0, 1.0),
            clamp(voxelLighting.cameraAlignedShadow, 0.0, 1.0),
            1.0);
        return;
    }

    if (debugView == 17)
    {
        // R=full voxel solar occlusion, G=native alpha-tested occlusion,
        // B=near/far cascade support. This intentionally traces both paths so
        // a real-map capture can prove which representation shaped vegetation.
        if (!hasGeometry)
        {
            outColor = vec4(0.0, 0.0, 0.0, 1.0);
            return;
        }
        vec3 diagnosticVoxelOrigin = worldPosition
            + worldGeometricNormal * 0.12
            + sunDirection * 0.04;
        float diagnosticVoxelVisibility = traceVoxelSunVisibility(
            diagnosticVoxelOrigin);
        float diagnosticNativeVisibility;
        float diagnosticNativeSupport;
        float diagnosticNativeCoverage;
        traceNativeSunShadowVisibility(
            earlyRelativeWorldPosition,
            diagnosticNativeVisibility,
            diagnosticNativeSupport,
            diagnosticNativeCoverage);
        outColor = vec4(
            1.0 - clamp(diagnosticVoxelVisibility, 0.0, 1.0),
            (1.0 - clamp(diagnosticNativeVisibility, 0.0, 1.0))
                * diagnosticNativeSupport,
            diagnosticNativeSupport,
            1.0);
        return;
    }

    if (debugView == 7)
    {
        // Reflection tracing above consumes the clean opaque G-buffer and the
        // pre-held colour carrier. Consequently it already supplies the
        // coherent world reflection behind a detected first-person overlay;
        // the planar branch additionally integrates a five-tap vertical
        // neighbourhood. Keep that inpainted result instead of cutting a black,
        // arm-shaped hole into this diagnostic. The direct final composite is
        // still returned untouched for overlays at the early exit above.
        // Moderate diagnostic exposure keeps reflected shapes and
        // colour readable. The previous 8x mask saturated the complete
        // lake to white, allowing broad hit coverage to pass while the
        // actual reflected scene remained impossible to assess.
        outColor = vec4(clamp(
            reflection.color
                * clamp(reflection.confidence, 0.0, 1.0)
                * 2.5,
            0.0,
            1.0), 1.0);
        return;
    }

    if (debugView == 8)
    {
        // Diagnostic exposure only: the channel contains genuine
        // secondary radiance, not hit/miss instrumentation.
        outColor = vec4(clamp(
            voxelLighting.bounce * pointLightBounceStrength * 8.0,
            0.0,
            1.0), 1.0);
        return;
    }

    if (debugView == 9)
    {
        outColor = vec4(
            reflection.voxelColor
                * clamp(reflection.voxelConfidence, 0.0, 1.0)
                * 2.5,
            1.0);
        return;
    }

    vec3 sourceLinear = srgbToLinear(source);
    float voxelMaterialConfidence = step(0.05, voxelLighting.material.a);
    float materialConfidence = mix(
        voxelMaterialConfidence,
        packedSurfaceAvailable,
        dynamicSurface);
    // The dynamic path retains the entity framebuffer chromaticity and only
    // removes its scalar baked-light level using the entity's own encoded
    // unlit luminance. It never samples voxel material colour, so an animal or
    // dropped animated entity cannot randomly inherit the block behind it.
    float sourceLinearLuminance = dot(
        sourceLinear,
        vec3(0.2126, 0.7152, 0.0722));
    vec3 dynamicSurfaceAlbedo = authoredAlbedoLuminance >= 0.0
        ? clamp(
            sourceLinear
                * (authoredAlbedoLuminance
                    / max(sourceLinearLuminance, 0.02)),
            0.0,
            1.0)
        : sourceLinear;
    vec3 surfaceAlbedo = dynamicSurface > 0.5
        ? dynamicSurfaceAlbedo
        : (materialConfidence > 0.0
            ? reconstructSurfaceAlbedo(
                source,
                voxelLighting.material,
                authoredAlbedoLuminance)
            : sourceLinear);

    // Alpha is a compact material class supplied by the voxel scene:
    // fluid/glass ~= 0.25, ordinary dielectric ~= 0.50, metal ~= 0.75.
    // The authored roughness/normal texture remains the higher-frequency
    // PBR source, while this class gives nonstandard blocks a safe base.
    float voxelMetallic = (1.0 - dynamicSurface) * materialConfidence
        * smoothstep(0.66, 0.74, voxelLighting.material.a);
    float metallic = mix(voxelMetallic, authoredMetallic, materialMapPresent);
    float transmissiveSurface = (1.0 - dynamicSurface) * materialConfidence
        * (1.0 - smoothstep(0.30, 0.44, voxelLighting.material.a));
    float planarResponse = (1.0 - dynamicSurface)
        * clamp(reflection.planarConfidence, 0.0, 1.0);
    if (debugView == 11)
    {
        // R=metallic, G=smoothness, B=file-backed normal/roughness payload.
        outColor = vec4(
            clamp(metallic, 0.0, 1.0),
            clamp(1.0 - surfaceRoughness, 0.0, 1.0),
            min(packedSurfaceAvailable, pbrPayloadPresent),
            1.0);
        return;
    }
    float sourceEnergy = dot(source, vec3(1.0));
    float materialEnergy = dot(voxelLighting.material.rgb, vec3(1.0));
    vec3 sourceChromaticity = source / max(sourceEnergy, 0.08);
    vec3 materialChromaticity = voxelLighting.material.rgb
        / max(materialEnergy, 0.08);
    float materialAgreement = dynamicSurface > 0.5 || sourceEnergy < 0.08
        ? 1.0
        : exp(-length(sourceChromaticity - materialChromaticity) * 5.0);
    // Rain only changes the uppermost rain-blocking receiver in a
    // column. Material agreement rejects late hands/held-item overlays
    // whose source colour belongs to a different surface than the
    // terrain G-buffer behind them. Water keeps its own transmission.
    float rainMaterialAgreement = materialConfidence > 0.0
        ? smoothstep(0.12, 0.52, materialAgreement)
        : 0.0;
    float rainExposure = earlyRainExposure
        * (1.0 - dynamicSurface)
        * (1.0 - transmissiveSurface)
        * (1.0 - vegetationSurface)
        * rainMaterialAgreement;
    float wetSurface = clamp(rainWetness * rainExposure, 0.0, 1.0);
    surfaceAlbedo *= mix(1.0, 0.82, wetSurface);
    if (debugView == 13)
    {
        // R=physically wet receiver, G=rain-height exposure,
        // B=CPU-smoothed precipitation state.
        outColor = vec4(
            wetSurface,
            earlyRainExposure,
            clamp(rainWetness, 0.0, 1.0),
            1.0);
        return;
    }
    // Opaque terrain can be re-lit confidently even when its voxel
    // average and high-frequency source texel have different chroma.
    // Agreement is only required where the G-buffer can describe the
    // opaque surface behind alpha-tested foliage or transmission.
    float transparencyRisk = max(
        vegetationSurface,
        max(transmissiveSurface, planarResponse));
    // A valid opaque G-buffer pixel inside the volume can fall exactly on a
    // voxel/block boundary where the nearest material lookup returns air.
    // Use the reconstructed source albedo as a bounded opaque fallback so the
    // transport does not leave bright vanilla seams in deep shadows. Geometry
    // outside the volume must retain the exact source carrier: no authoritative
    // voxel material or occlusion information exists there.
    // Foliage and transmission still require authoritative material
    // agreement and therefore never inherit this fallback.
    float opaqueGeometryFallback = hasGeometry
        && isInsideVoxelVolume(worldPosition) ? 0.86 : 0.0;
    float opaqueReliability = max(
        materialConfidence,
        opaqueGeometryFallback);
    float riskyReliability = materialConfidence * materialAgreement;
    float surfaceReliability = mix(
        opaqueReliability,
        riskyReliability,
        transparencyRisk);
    surfaceReliability = mix(
        surfaceReliability,
        packedSurfaceAvailable,
        dynamicSurface);
    if (screenSpaceReflectionsEnabled != 0 && wetSurface > 0.02)
    {
        // The dry pass correctly rejects default-rough terrain early.
        // A wet dielectric needs one bounded SSR pass, but deliberately
        // skips the costly off-screen voxel DDA. Environment radiance
        // below handles misses without a stochastic point pattern.
        float wetTraceRoughness = mix(
            surfaceRoughness,
            min(surfaceRoughness, 0.18),
            wetSurface * 0.92);
        ReflectionResult wetReflection = traceScreenSpaceReflection(
            position,
            normal,
            worldPosition,
            worldNormal,
            wetTraceRoughness,
            source,
            0);
        if (wetReflection.confidence > reflection.confidence)
        {
            reflection.color = wetReflection.color;
            reflection.confidence = wetReflection.confidence;
        }
    }
    float ambientVisibility = 1.0
        - lighting.occlusion * contactShadowStrength * 0.72;
    // The directional field is an incoming radiance lobe, not an
    // isotropic ambient colour. A small floor represents later bounces;
    // most energy must face the receiver to avoid the former uniform
    // orange wash across every wall of a lantern-lit room.
    float irradianceReceiver = 0.12 + 0.88 * max(
        dot(worldNormal, voxelLighting.irradianceDirection),
        0.0);
    vec3 directionalIrradiance = voxelLighting.irradianceCache
        * irradianceReceiver;

    vec3 tracedAmbient = (
        voxelLighting.skyDirect * skyLightStrength
        + directionalIrradiance
            * pointLightBounceStrength * 1.15
        + voxelLighting.bounce * pointLightBounceStrength * 0.90
        + srgbToLinear(lighting.indirect)
            * indirectLightStrength * (0.38 + 0.32 * lighting.confidence))
        * ambientVisibility;
    // Visibility was already integrated per emitter above. Shadow opacity is
    // a presentation control for the inherited raster carrier; it must never
    // increase unoccluded candela. Coupling it here made a stronger shadow
    // setting brighten the same lantern by up to 40 percent.
    vec3 tracedDirect = voxelLighting.sunDirect * sunLightStrength * 1.42
        + voxelLighting.direct * emissiveLightStrength;

    // Keep diffuse transport split by path. The framebuffer is the only
    // full-resolution source of authored texel detail exposed at this
    // stage; indirect light can be accumulated strongly, while direct
    // light is used as a calibrated local correction instead of being
    // counted a second time over Vintage Story's raster contribution.
    float diffuseTransportEligibility = (1.0 - metallic * 0.88)
        * (1.0 - transmissiveSurface * 0.96)
        * (1.0 - planarResponse * 0.98);
    vec3 indirectDiffuseRadiance = surfaceAlbedo
        * diffuseTransportEligibility
        * tracedAmbient;
    vec3 directDiffuseRadiance = surfaceAlbedo
        * diffuseTransportEligibility
        * tracedDirect;
    vec3 proximityEmission = reconstructSurfaceEmission(sourceLinear, worldPosition);
    vec3 engineEmissionColor = nearestEmitterColor(
        worldPosition,
        max(sourceLinear, voxelLighting.direct));
    vec3 authoredEmission = surfaceAlbedo
        * mix(
            vec3(1.0),
            engineEmissionColor / max(surfaceAlbedo, vec3(0.08)),
            smoothstep(0.08, 0.55, authoredEmissive) * 0.82)
        * authoredEmissive
        * (1.05 + authoredEmissive * 2.45);
    // Vintage Story already marks genuinely glowing mesh fragments in
    // the G-buffer. Preserve that local mask (flame/filament), while
    // authored _e maps provide the same contract to content mods.
    // outGlow.r also contains the stock raster specular term. Gate it
    // with an authored/engine emission bit or verified emitter
    // proximity so a shiny texture seam never becomes a light source.
    float proximityGlowConfidence = smoothstep(
        0.08,
        0.42,
        maximumComponent(proximityEmission));
    float engineGlowGate = max(
        authoredEmissive,
        proximityGlowConfidence);
    vec3 engineEmission = engineEmissionColor
        * rasterGlow
        * engineGlowGate
        * (2.2 + rasterGlow * 5.4);
    vec3 emittedRadiance = max(
        proximityEmission,
        max(authoredEmission, engineEmission))
        * emissiveLightStrength;
    if (debugView == 10)
    {
        // R=blocked light, G=emitter proximity, B=authored/engine emission.
        outColor = vec4(
            clamp(dot(voxelLighting.blockedDirect, LUMA) * 4.0, 0.0, 1.0),
            clamp(dot(proximityEmission, LUMA) * 0.25, 0.0, 1.0),
            clamp(dot(max(authoredEmission, engineEmission), LUMA) * 0.25, 0.0, 1.0),
            1.0);
        return;
    }
    // The G-buffer normal includes the authored _n sidecar and roughness
    // comes from _r. This direct microfacet path therefore produces
    // localized highlights on hammered metal, wet stone and lantern
    // cages instead of a uniform post-process sheen.
    vec3 materialSpecularTint = mix(
        vec3(1.0),
        max(surfaceAlbedo * 2.6, vec3(0.48)),
        metallic);
    float resolvedPbrRoughness = mix(
        surfaceRoughness,
        min(surfaceRoughness, 0.42),
        metallic);
    // Water films fill dielectric micro-cavities. Retain some authored
    // roughness so stone stays wet stone rather than becoming chrome.
    resolvedPbrRoughness = mix(
        resolvedPbrRoughness,
        min(resolvedPbrRoughness, 0.18),
        wetSurface * 0.88);
    resolvedPbrRoughness = filterSpecularRoughness(
        resolvedPbrRoughness,
        worldNormal);
    float resolvedSpecularEligibility = mix(
        1.0 - smoothstep(
            0.38,
            0.72,
            resolvedPbrRoughness),
        1.0,
        metallic);
    vec3 worldViewDirection = normalize(
        cameraWorldPosition - worldPosition);
    vec3 irradianceSpecularRadiance = vec3(0.0);
    if (resolvedSpecularEligibility > 0.001)
    {
        irradianceSpecularRadiance = voxelLighting.irradianceCache
            * evaluateDirectSpecular(
                worldNormal,
                worldViewDirection,
                voxelLighting.irradianceDirection,
                resolvedPbrRoughness)
            * materialSpecularTint
            * resolvedSpecularEligibility
            * (0.45 + metallic * 3.20);
    }
    // The aggregate light loop evaluates a dielectric F0 before the
    // authored material is decoded. Restore a bounded conductor gain
    // here; it remains tied to the GGX normal/light alignment and never
    // becomes a screen-wide metallic brightness adjustment.
    vec3 directSpecularRadiance = voxelLighting.directSpecular
        * materialSpecularTint
        * resolvedSpecularEligibility
        * (0.82 + metallic * 3.80)
        + irradianceSpecularRadiance;
    vec3 specularIncidentRadiance = voxelLighting.direct
        + voxelLighting.sunDirect
        + voxelLighting.irradianceCache;
    directSpecularRadiance = softLimitSpecularRadiance(
        directSpecularRadiance,
        specularIncidentRadiance,
        resolvedPbrRoughness,
        metallic);
    // Water, ice and glass already contain transmission/refraction in
    // the engine framebuffer. Keep that coherent base and add traced
    // specular energy instead of replacing it with opaque voxel albedo.
    float transmissionPreservation = mix(
        1.0,
        0.08,
        max(transmissiveSurface, planarResponse));
    float transportWeight = relightingStrength
        * surfaceReliability
        * transmissionPreservation;
    // Transparent/late-composited geometry keeps the opaque G-buffer
    // behind it. Only the VintageRTX packed surface proves that colour,
    // normal and position describe the same visible fragment. Retain a
    // small compatibility correction elsewhere, but never flood glass
    // with diffuse light reconstructed for the wall behind it.
    transportWeight *= mix(0.16, 1.0, packedSurfaceAvailable);
    // Sky visibility identifies exterior receivers, but it must not erase
    // the traced transport itself. The former exterior scale reduced the
    // whole relighting path to 16 %, then a second 34 % mix left less than
    // five percent physical radiance in the final landscape. Exposure is
    // matched below instead, after the direct/indirect lobes are known.
    float exteriorConfidence = clamp(
        voxelLighting.skyVisibility * sunColorStrength.w,
        0.0,
        1.0);
    // A conductor has almost no diffuse lobe, so replacing the raster
    // before a sparse reflection hit can make dark iron disappear. Keep
    // a bounded part of its authored body colour, then add the traced
    // microfacet/reflection terms below. Dielectrics remain unchanged.
    float metallicTransportScale = mix(1.0, 0.72, metallic);
    transportWeight *= metallicTransportScale;
    // A verified static opaque texel has coherent albedo, normal, position,
    // photometric emitters and traced visibility. Keeping most of the stock
    // lightmap in that case double-counts the lamp and imports its animated
    // luminance into an otherwise fixed scene. Make the physical transport
    // dominant while retaining a small high-frequency carrier remainder;
    // conductors keep slightly more of it when their environment ray misses.
    float opaquePhysicalTransportConfidence = packedSurfaceAvailable
        * surfaceReliability
        * (1.0 - transparencyRisk)
        * (1.0 - dynamicSurface);
    float opaquePhysicalTransportFloor = mix(0.96, 0.90, metallic);
    transportWeight = mix(
        transportWeight,
        max(transportWeight, opaquePhysicalTransportFloor),
        opaquePhysicalTransportConfidence);

    vec3 viewDirection = hasGeometry ? normalize(-position) : vec3(0.0, 0.0, 1.0);
    float normalView = hasGeometry
        ? clamp(dot(normal, viewDirection), 0.0, 1.0)
        : 1.0;
    if (planarResponse > 0.001)
    {
        vec3 planarViewDirection = normalize(
            cameraWorldPosition - reflection.planarWorldPosition);
        normalView = clamp(abs(planarViewDirection.y), 0.0, 1.0);
    }
    // Dark raster albedo is not a valid conductor F0. Iron, steel and
    // other metals still reflect a broad environment lobe even when
    // their diffuse texture is nearly black; a bounded floor prevents
    // anvils from disappearing while retaining authored tint.
    vec3 metallicReflectance = max(surfaceAlbedo * 1.55, vec3(0.50));
    vec3 baseReflectance = mix(vec3(0.04), metallicReflectance, metallic);
    baseReflectance = mix(
        baseReflectance,
        max(baseReflectance, vec3(0.075)),
        wetSurface * (1.0 - metallic));
    vec3 fresnelReflectance = baseReflectance
        + (vec3(1.0) - baseReflectance) * pow(1.0 - normalView, 5.0);
    float roughnessResponse = mix(
        0.18 + 0.82 * pow(1.0 - resolvedPbrRoughness, 1.45),
        0.94,
        max(planarResponse, transmissiveSurface * 0.72));
    float reflectionVisibility = clamp(reflection.confidence, 0.0, 1.0)
        * reflectionStrength
        * roughnessResponse
        * max(surfaceReliability, planarResponse * materialConfidence * 0.90);
    vec3 reflectedSceneRadiance = srgbToLinear(reflection.color);
    float puddleResponse = 0.0;
    if (wetSurface > 0.001)
    {
        puddleResponse = wetSurface
            * smoothstep(0.78, 0.96, worldGeometricNormal.y);
        vec3 puddleNormal = normalize(worldNormal + vec3(
            sin(worldPosition.x * 1.61 + worldPosition.z * 0.83) * 0.010,
            0.0,
            cos(worldPosition.x * 0.91 - worldPosition.z * 1.37) * 0.010)
                * puddleResponse);
        vec3 wetEnvironmentDirection = normalize(reflect(
            normalize(worldPosition - cameraWorldPosition),
            puddleNormal));
        vec3 wetEnvironmentRadiance = skyEnvironmentRadiance(
            wetEnvironmentDirection);
        float wetEnvironmentWeight = wetSurface
            * (1.0 - clamp(reflection.confidence, 0.0, 1.0))
            * mix(0.38, 0.64, puddleResponse);
        reflectedSceneRadiance = mix(
            reflectedSceneRadiance,
            wetEnvironmentRadiance,
            wetEnvironmentWeight);
        reflectionVisibility = max(
            reflectionVisibility,
            wetSurface
                * reflectionStrength
                * roughnessResponse
                * mix(0.24, 0.48, puddleResponse)
                * surfaceReliability);
    }
    vec3 reflectedRadiance = reflectedSceneRadiance
        * fresnelReflectance
        * reflectionVisibility
        * (1.15 + metallic * 1.25 + planarResponse * 0.85);
    // Low-cost tiers deliberately shorten SSR and off-screen voxel
    // traces, so a small metal object can miss both and appear matte
    // despite a valid _m/_r/_n payload. Reuse the directional local
    // irradiance already fetched for GI as the unresolved environment
    // lobe. This adds no ray or texture lookup and stays material-,
    // roughness- and Fresnel-gated instead of becoming a screen-wide
    // post-process sheen.
    vec3 localEnvironmentSpecular = vec3(0.0);
    if (resolvedSpecularEligibility > 0.001)
    {
        vec3 environmentReflectionDirection = reflect(
            -worldViewDirection,
            worldNormal);
        float environmentAlignment = pow(
            max(dot(
                environmentReflectionDirection,
                voxelLighting.irradianceDirection), 0.0),
            mix(1.5, 10.0, 1.0 - resolvedPbrRoughness));
        float environmentLobe = mix(
            0.28,
            1.0,
            environmentAlignment);
        localEnvironmentSpecular = voxelLighting.irradianceCache
            * fresnelReflectance
            * resolvedSpecularEligibility
            * (1.0 - clamp(reflection.confidence, 0.0, 1.0))
            * (1.0 - resolvedPbrRoughness * 0.65)
            * environmentLobe
            * surfaceReliability
            * (0.035 + metallic * 0.460);
    }
    reflectedRadiance += localEnvironmentSpecular;

    // The normal map shapes BRDF response, never the footprint of an
    // occluder. Use the geometric receiver for carrier attenuation so
    // long roof shadows remain continuous over textured surfaces.
    float exteriorSunReceiver = max(
        dot(worldGeometricNormal, sunDirection),
        0.0);
    float tracedExteriorShadow = 1.0
        - exteriorConfidence
            * (1.0 - voxelLighting.sunVisibility)
            * exteriorSunReceiver
            * 0.55;

    // Use the exposed framebuffer as a high-resolution radiance carrier
    // and let traced visibility remove only the direct component that
    // is demonstrably blocked. This keeps cracks, grain and the game's
    // calibrated adaptation while producing lantern-cage and sun
    // shadows without the former black/orange block plateaus.
    float pointShadowMask = smoothstep(
            0.006,
            0.22,
            voxelLighting.shadow)
        * pointLightShadowStrength;
    // Retain unresolved ambient/bounce energy in the raster carrier.
    // The traced diffuse replacement below supplies the stronger local
    // contrast; erasing 82% of the complete framebuffer also removed
    // sky and indirect light which never belonged to the blocked lamp.
    float pointShadowFactor = 1.0 - pointShadowMask * 0.62;
    vec3 rasterCarrier = sourceLinear
        * pointShadowFactor
        * tracedExteriorShadow;
    vec3 hybridTransport = rasterCarrier
        + indirectDiffuseRadiance * 0.74
        + directDiffuseRadiance * 0.34
        + directSpecularRadiance * 1.15
        + emittedRadiance;
    // Opaque terrain now supplies an authored albedo luminance instead
    // of forcing the post pass to infer texture detail from already-lit
    // pixels. Use that confidence to replace most of the stock diffuse
    // solution with traced direct/indirect paths. The small neutral sky
    // floor represents unresolved higher-order energy and prevents a
    // zero-cache cell from crushing a valid dark material to black.
    vec3 physicalTransport = surfaceAlbedo
            * vec3(0.010, 0.012, 0.016)
            * ambientVisibility
        + indirectDiffuseRadiance * 0.96
        + directDiffuseRadiance * 0.72
        + directSpecularRadiance * 1.28
        + emittedRadiance;
    // Match only broad exposure, never chroma or visibility. This keeps
    // high-frequency authored albedo and the traced sun/sky/light balance,
    // while preventing the physical solution from jumping several stops
    // away from Vintage Story's adapted carrier on an exterior transition.
    float rasterCarrierLuminance = dot(rasterCarrier, LUMA);
    float physicalTransportLuminance = dot(physicalTransport, LUMA);
    float exteriorEnergyScale = clamp(
        rasterCarrierLuminance / max(physicalTransportLuminance, 0.015),
        0.72,
        1.25);
    float exteriorEnergyMatch = exteriorConfidence
        * packedSurfaceAvailable
        * surfaceReliability
        * (1.0 - transparencyRisk);
    vec3 energyMatchedPhysicalTransport = physicalTransport * mix(
        1.0,
        exteriorEnergyScale,
        exteriorEnergyMatch * 0.55);
    // Local emitter chromaticity and candela are now supplied by the authored
    // photometry catalogue, so fixed opaque receivers no longer need the
    // engine lightmap as their primary night-time lighting solution. Daylight
    // retains a little more carrier energy for unresolved distant transport.
    float daylightRelighting = smoothstep(
        0.18,
        0.62,
        clamp(sunColorStrength.w, 0.0, 1.0));
    float enclosedPhysicalRelighting = mix(
        0.94,
        0.84,
        daylightRelighting);
    float physicalRelightingWeight = opaquePhysicalTransportConfidence
        * mix(enclosedPhysicalRelighting, 0.84, exteriorConfidence);
    vec3 reconstructedTransport = mix(
        hybridTransport,
        energyMatchedPhysicalTransport,
        physicalRelightingWeight);
    vec3 compositeRadiance = mix(
        sourceLinear * tracedExteriorShadow,
        reconstructedTransport,
        transportWeight);

    // The finite directional cache cannot integrate the complete
    // environment lobe. Preserve a small, authored-material-gated
    // remainder so thin foliage does not collapse to black and a dark
    // conductor still returns ambient energy when no discrete GGX
    // direction happens to align. This is higher-order material energy:
    // it adds no ray, changes no visibility result and remains AO-,
    // roughness-, reliability- and PBR-presence-gated.
    vec3 unresolvedDiffuseEnvironment = surfaceAlbedo
        * vec3(0.032, 0.039, 0.050)
        * (1.0 - metallic)
        * (1.0 - transmissiveSurface)
        * (1.0 - planarResponse);
    vec3 unresolvedConductorEnvironment = metallicReflectance
        * vec3(0.080, 0.085, 0.094)
        * metallic
        * (1.0 - resolvedPbrRoughness * 0.28);
    float unresolvedMaterialConfidence = packedSurfaceAvailable
        * surfaceReliability
        * ambientVisibility;
    vec3 unresolvedMaterialRadiance = (
            unresolvedDiffuseEnvironment
            + unresolvedConductorEnvironment)
        * unresolvedMaterialConfidence;
    compositeRadiance += unresolvedMaterialRadiance;
    // Alpha-tested foliage carries sub-pixel transmission and multiple
    // scattering already resolved by the engine raster but absent from a
    // one-voxel material sample. Retain at least the shadowed high-resolution
    // carrier energy instead of relighting thin leaves as a solid dark voxel.
    // sourceLinear is already the engine-lit and shadowed carrier; applying
    // the traced masks a second time here would double-darken leaf planes.
    vec3 vegetationCarrierFloor = sourceLinear * 0.82;
    compositeRadiance = mix(
        compositeRadiance,
        max(compositeRadiance, vegetationCarrierFloor),
        vegetationSurface);

    // A liquid transmits the already lit opaque receiver. Estimate the
    // travelled distance from the reconstructed surface plane to that
    // receiver and apply Beer-Lambert extinction per colour channel.
    // This preserves lantern/sun shading carried by the submerged block
    // instead of replacing it with an unlit water mask. A bounded
    // single-scattering term returns part of the absorbed energy as
    // profile-coloured volume radiance. Profile parameters are transiently
    // water-only above and are replaced by vintageRtxOptics metadata once
    // a stable liquid profile id reaches this pass.
    vec3 liquidCarrierRadiance = compositeRadiance;
    if (reflection.liquidTransmissionConfidence > 0.001)
    {
        // One bounded tap provides visible refraction. Invalid, off-screen
        // or zero-depth targets keep the already lit current-pixel carrier.
        vec3 refractedSourceRadiance = srgbToLinear(sampleReflectionSource(
            reflection.liquidTransmissionUv));
        liquidCarrierRadiance = mix(
            liquidCarrierRadiance,
            refractedSourceRadiance,
            reflection.liquidTransmissionConfidence);
    }
    vec3 liquidExtinction = liquidExtinctionCoefficient(
        reflection.liquidAbsorption,
        reflection.liquidScattering,
        reflection.liquidTransmission,
        reflection.liquidOpaque);
    vec3 liquidTransmittance = exp(
        -liquidExtinction
            * reflection.liquidPathLength
            * reflection.liquidMetresPerWorldBlock);
    float transmittedCarrierLuminance = dot(liquidCarrierRadiance, LUMA);
    float reflectedLiquidLuminance = dot(reflectedSceneRadiance, LUMA);
    float liquidVolumeIlluminance = 0.08
        + transmittedCarrierLuminance * 0.55
        + reflectedLiquidLuminance * 0.12;
    vec3 liquidVolumeAlbedo = clamp(
        reflection.liquidScattering / max(liquidExtinction, vec3(0.0001)),
        vec3(0.0),
        vec3(1.0));
    // One isotropic single-scattering estimate. The 0.55 phase/escape
    // factor keeps returned radiance below the energy removed from the
    // transmitted carrier while retaining readable blue-green depth.
    vec3 liquidSingleScattering = liquidVolumeAlbedo
        * (vec3(1.0) - liquidTransmittance);
    float liquidAnisotropy = clamp(
        reflection.liquidAnisotropy,
        -0.95,
        0.95);
    float liquidBackscatterPhase = clamp(
        (1.0 - liquidAnisotropy)
            / max(
                (1.0 + liquidAnisotropy)
                    * (1.0 + liquidAnisotropy),
                0.0025),
        0.25,
        1.50);
    liquidSingleScattering *= liquidVolumeIlluminance
        * 0.55
        * liquidBackscatterPhase;
    vec3 integratedLiquidEmission = reflection.liquidEmission
        * (vec3(1.0) - liquidTransmittance)
        / max(liquidExtinction, vec3(0.0001))
        * (1.0 + reflection.liquidBubbleEmission);
    vec3 transmittedLiquidTransport = liquidCarrierRadiance * liquidTransmittance
        + liquidSingleScattering
        + integratedLiquidEmission;

    // Reflections keep their own specular path. The luminance shoulder
    // below is still shared with the complete composed radiance so the
    // base exposure stays unchanged and bright transport rolls off.
    float reflectionAddWeight = 0.82
        + metallic * 3.00
        + max(planarResponse, transmissiveSurface) * 0.18;
    // Opaque/metal reflections remain a localized specular addition.
    // A confirmed water plane instead shares energy between the engine's
    // transmitted lake bed and reflected radiance through Fresnel. Pure
    // addition made water brighter but still visually flat; replacement
    // keeps transmission at normal incidence and becomes mirror-like at
    // grazing angles without a global exposure or colour adjustment.
    compositeRadiance += reflectedRadiance
        * reflectionAddWeight
        * (1.0 - planarResponse);
    // Split the confirmed liquid interface exactly once: (1-F) carries
    // the lit submerged receiver through the volume, while F carries the
    // reflected scene. Reflection confidence changes only how much of the
    // resolved scene is trusted; it never changes dielectric Fresnel.
    // At zero thickness Beer-Lambert is one and the volume terms are zero,
    // so the transmitted branch retains the original lit receiver.
    float planarFresnel = clamp(reflection.liquidFresnel, 0.0, 1.0);
    float resolvedLiquidReflectionTrust = mix(
        0.72,
        1.0,
        clamp(reflection.confidence, 0.0, 1.0));
    vec3 trustedLiquidReflection = reflectedSceneRadiance
        * reflectionStrength
        * roughnessResponse
        * (0.25 + 0.75 * pow(
            1.0 - clamp(reflection.liquidRoughness, 0.0, 1.0),
            1.20))
        * resolvedLiquidReflectionTrust;
    vec3 liquidSurfaceTransport = transmittedLiquidTransport
            * (1.0 - planarFresnel)
        + trustedLiquidReflection * planarFresnel;
    compositeRadiance = mix(
        compositeRadiance,
        liquidSurfaceTransport,
        planarResponse);
    vec3 finalColor;
    if (outputColorDomain != 0)
    {
        // The game's pre-final carrier is sRGB-like LDR scene colour. The
        // transport math above is linear, so encode it once while preserving
        // exact source identity whenever no lighting contribution changed.
        // The RGBA8 bridge performs the unavoidable display-range clamp and
        // restores the official FXAA luma alpha before final.fsh consumes it.
        finalColor = linearToSrgb(max(compositeRadiance, vec3(0.0)));
    }
    else
    {
        // Standalone previews do not execute Vintage Story's final.fsh and
        // therefore retain their self-contained display transform.
        vec3 relitDisplay = filmicToneMap(compositeRadiance);
        vec3 color = applyDisplayGrade(relitDisplay);
        vec2 centered = uv * 2.0 - 1.0;
        float radial = dot(centered, centered);
        float vignetteFactor = 1.0 - vignette * smoothstep(0.20, 1.45, radial);
        finalColor = color * vignetteFactor;
        // RenderLab owns no equivalent of Vintage Story's final pass. Its
        // source texture is already a display-domain alpha-tested resolve,
        // while the relit branch above deliberately applies a second,
        // self-contained filmic transform. Preserve the resolved coverage of
        // sub-pixel foliage after that transform so the laboratory measures
        // material response rather than double-tonemapping loss. The runtime
        // outputColorDomain path remains fully scene-linear and is unaffected.
        vec3 standaloneVegetationCarrier = source.rgb * 0.90;
        finalColor = mix(
            finalColor,
            max(finalColor, standaloneVegetationCarrier),
            vegetationSurface);
    }
    // The liquid interface moves even while the camera is stationary. Without
    // per-pixel motion vectors, reusing its previous final colour preserves
    // reflections of objects that have since sunk and makes item/wind waves
    // flip against stale normals. Resolve animated liquid from the current
    // height field; opaque stationary receivers retain temporal denoising.
    float surfaceTemporalBlend = temporalBlend
        * (1.0 - smoothstep(0.001, 0.02, planarResponse));
    if (surfaceTemporalBlend > 0.001)
    {
        vec3 filteredHistory = denoiseTemporalHistory(
            position,
            normal,
            source,
            finalColor,
            hasGeometry);
        finalColor = mix(
            finalColor,
            filteredHistory,
            clamp(surfaceTemporalBlend, 0.0, 0.95));
    }

    outColor = vec4(finalColor, 1.0);
}
