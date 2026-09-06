#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;
uniform sampler2D vintagertxMaterialTex;
uniform int vintagertxPbrEnabled;
uniform mat4 modelViewMatrix;

// This response is part of the file-backed material contract, not a per-draw
// setting. Keeping it in the shader prevents Vintage Story's generated
// Chunkopaque wrapper from restoring an unknown float uniform to its default
// zero value when it prepares the terrain pass.
// A tangent-space normal map already encodes its physical surface direction.
// One is the neutral OpenGL convention; amplifying every material globally
// turns albedo-derived fallback relief into non-physical embossed geometry.
const float VintagertxNormalStrength = 1.0;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in float glowLevel;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 vertexPosition;
in vec3 blockLight;
in vec4 gnormal;
in vec4 camPos;
in float lod0Fade;
in float nb;

uniform float alphaTest;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform float dayLight;
uniform int haxyFade;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include vertexflagbits.ash
#include fogandlight.fsh
#include dither.fsh
#include skycolor.fsh
#include colormap.fsh
#include underwatereffects.fsh

vec3 vintagertxPerturbNormal(vec3 baseNormal, vec2 encodedTangentNormalXY)
{
    if (vintagertxPbrEnabled == 0)
    {
        return normalize(baseNormal);
    }

    vec2 tangentXY = encodedTangentNormalXY * 2.0 - 1.0;
    float tangentZ = sqrt(max(1.0 - dot(tangentXY, tangentXY), 0.000001));
    vec3 tangentNormal = vec3(tangentXY, tangentZ);
    tangentNormal.xy *= VintagertxNormalStrength;
    tangentNormal = normalize(tangentNormal);

    vec3 positionDx = dFdx(worldPos.xyz);
    vec3 positionDy = dFdy(worldPos.xyz);
    vec2 uvDx = dFdx(uv);
    vec2 uvDy = dFdy(uv);
    vec3 normalWorld = normalize(baseNormal);

    // uv is expressed in atlas space, so its derivatives can be hundreds of
    // times smaller than local texture UVs. Normalize before applying any
    // epsilon: a fixed lower bound on the squared cotangent length flattens
    // every normal map stored in a small atlas rectangle.
    float uvDeterminant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    float uvOrientation = uvDeterminant < 0.0 ? -1.0 : 1.0;
    vec3 tangentRaw = (positionDx * uvDy.y - positionDy * uvDx.y) * uvOrientation;
    vec3 bitangentRaw = (positionDy * uvDx.x - positionDx * uvDy.x) * uvOrientation;

    // Gram-Schmidt keeps the TBN orthonormal even on interpolated or slightly
    // non-planar chunk geometry. Preserve mirrored-UV handedness explicitly.
    tangentRaw -= normalWorld * dot(normalWorld, tangentRaw);
    float tangentLengthSquared = dot(tangentRaw, tangentRaw);
    vec3 fallbackAxis = abs(normalWorld.z) < 0.999
        ? vec3(0.0, 0.0, 1.0)
        : vec3(0.0, 1.0, 0.0);
    vec3 tangent = tangentLengthSquared > 1e-20
        ? tangentRaw * inversesqrt(tangentLengthSquared)
        : normalize(cross(fallbackAxis, normalWorld));
    float handedness = dot(cross(normalWorld, tangent), bitangentRaw) < 0.0 ? -1.0 : 1.0;
    vec3 bitangent = normalize(cross(normalWorld, tangent)) * handedness;
    return normalize(mat3(tangent, bitangent, normalWorld) * tangentNormal);
}

void main()
{
    // Preserve one scalar from the authored, colour-mapped texel before the
    // stock vertex light and shadow are applied. The deferred transport pass
    // combines this exact high-frequency luminance with the voxel base colour,
    // which lets it remove baked raster lighting without flattening cracks,
    // grain or painted detail.
    vec4 unlitTexColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv));
    vec4 texColor = unlitTexColor * rgba;
    // One proven sampler transports every sidecar: filtered RG are tangent
    // normal XY, filtered B is roughness, and exact A carries two-bit metallic,
    // three-bit emissive, PBR-presence and reliable-override flags. Z is
    // reconstructed from the normalized XY disk.
    vec4 pbrSurface = texture(vintagertxMaterialTex, uv);
    ivec2 pbrAtlasSize = textureSize(vintagertxMaterialTex, 0);
    ivec2 pbrTexel = clamp(
        ivec2(uv * vec2(pbrAtlasSize)),
        ivec2(0),
        pbrAtlasSize - ivec2(1));
    int pbrMaterialBits = vintagertxPbrEnabled != 0
        ? int(round(texelFetch(vintagertxMaterialTex, pbrTexel, 0).a * 255.0))
        : 0;
    vec3 shadingNormal = vintagertxPerturbNormal(normal, pbrSurface.rg);
    float surfaceRoughness = vintagertxPbrEnabled != 0 ? pbrSurface.b : 0.72;

    if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition * 2, 0);
    if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, shadingNormal, vertexPosition, 1);

    float b = getBrightnessFromShadowMap();

    float murkiness = getUnderwaterMurkiness();
    outColor = applyFogAndShadowFromBrightness(
        texColor,
        clamp(fogAmount - 50 * murkiness, 0, 1),
        min(b, nb),
        worldPos.xyz);

    float glow = 0;
    float godrayLevel = 0;

    if (haxyFade > 0)
    {
        if (rgba.a < 0.999)
        {
            vec4 skyColor = vec4(1);
            vec4 skyGlow = vec4(1);
            float sealevelOffsetFactor = 0.25;
            getSkyColorAt(
                worldPos.xyz,
                sunPosition,
                sealevelOffsetFactor,
                clamp(dayLight, 0, 1),
                horizonFog,
                skyColor,
                skyGlow);
            godrayLevel = skyGlow.g;
            outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1 - dayLight, max(0.0, rgba.a)));
        }
    }

    outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

#if NORMALVIEW == 0
    float aTest = outColor.a + max(0.0, 1 - rgba.a) * min(1, outColor.a * 10) - lod0Fade;
    if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4;
    if (aTest < alphaTest || rgba.a < 0.005) discard;
#endif

#if SHINYEFFECT > 0
    if ((renderFlags & ReflectiveBitMask) != 0)
    {
        vec4 reflectedColor = applyReflectiveEffect(
            outColor,
            glow,
            renderFlags,
            uv,
            shadingNormal,
            worldPos,
            camPos,
            blockLight);
        float reflectionVisibility = 1.0 - clamp(2 * fogAmount + 2 * (1 - b), 0, 1);
        float smoothReflection = clamp(1.05 - surfaceRoughness, 0.08, 1.0);
        outColor = mix(outColor, reflectedColor, reflectionVisibility * smoothReflection);
    }
    float highlightPower = mix(28.0, 5.0, surfaceRoughness);
    float highlightStrength = mix(0.20, 0.035, surfaceRoughness);
    glow += pow(max(0.0, dot(shadingNormal, lightPosition)), highlightPower)
        * highlightStrength * shadowIntensity * (1 - fogAmount - murkiness);
#endif

#if SSAOLEVEL > 0
    outGPosition = vec4(camPos.xyz, fogAmount * 2 + glowLevel + murkiness);
    // The stock normal alpha is only a foliage flag. Pack five bits of PBR
    // roughness and five bits of unlit albedo luminance into that channel. A
    // half-float attachment represents all 1024 payloads deterministically;
    // values 0 and 1 remain reserved for untouched entity/legacy shaders.
    vec3 unlitLinear = pow(max(unlitTexColor.rgb, vec3(0.0)), vec3(2.2));
    float unlitLuminance = dot(unlitLinear, vec3(0.2126, 0.7152, 0.0722));
    int roughnessBits = int(round(clamp(surfaceRoughness, 0.0, 1.0) * 31.0));
    int albedoBits = int(round(clamp(unlitLuminance, 0.0, 1.0) * 31.0));
    float packedSurface = float(1 + roughnessBits * 32 + albedoBits) / 1025.0;
    outGNormal = vec4(
        (modelViewMatrix * vec4(shadingNormal, 0)).xyz,
        packedSurface);
#endif

#if NORMALVIEW > 0
    outColor = vec4((shadingNormal.x + 1) / 2, (shadingNormal.y + 1) / 2, (shadingNormal.z + 1) / 2, 1);
#endif

    // The blue channel is unused by the stock opaque pass. Preserve a stable
    // vegetation/transparency-risk flag so the deferred transport pass can
    // avoid re-lighting alpha-tested foliage with the voxel behind it.
    // Only authored PBR emission is packed here. The engine glowLevel varies
    // on ordinary lit terrain and is not a reliable emission classification;
    // genuine engine emitters are identified by the voxel-light proximity path.
    int materialBits = pbrMaterialBits;
    if ((renderFlags & WindModeBitMask) != 0) materialBits |= 128;
    float vintagertxPackedMaterial = float(materialBits) / 255.0;
    outGlow = vec4(
        glowLevel + glow,
        godrayLevel,
        vintagertxPackedMaterial,
        min(1, fogAmount + outColor.a));
}
