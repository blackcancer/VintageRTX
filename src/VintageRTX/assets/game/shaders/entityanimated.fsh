#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec2 uv;
in vec4 color;
in vec4 rgbaFog;
in float fogAmount;
in float glowLevel;
in vec3 vertexPosition;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 blockLight;
in vec4 camPos;
in float damageEffect;
in float fragFrostAlpha;

// Our include system is dumb and does not do conditional includes
// So we add a OIT preprocceor test to oit.fsh as well
#include oit.fsh

#if USEOIT==0
    layout(location = 0) out vec4 outColor;
    layout(location = 1) out vec4 outGlow;
    #if SSAOLEVEL > 0
    in vec4 fragPosition;
    in vec4 gnormal;
    layout(location = 2) out vec4 outGNormal;
    layout(location = 3) out vec4 outGPosition;
    #endif
#endif

uniform sampler2D entityTex;
uniform sampler2D vintagertxEntityMaterialTex;
uniform int vintagertxEntityPbrEnabled;
uniform float alphaTest = 0.001;
uniform float glitchEffectStrength;
uniform int entityId;
uniform int glitchFlicker;
#if defined(ALLOWDEPTHOFFSET)
#if ALLOWDEPTHOFFSET > 0
uniform float depthOffset;
#endif
#endif

#include vertexflagbits.ash
#include fogandlight.fsh
#include noise3d.ash
#include noise2d.ash
#include underwatereffects.fsh

const float VintagertxEntityNormalStrength = 0.72;

vec3 vintagertxPerturbEntityNormal(
    vec3 baseNormal,
    vec3 surfacePosition,
    vec2 encodedTangentNormalXY)
{
    vec2 tangentXY = encodedTangentNormalXY * 2.0 - 1.0;
    float tangentZ = sqrt(max(1.0 - dot(tangentXY, tangentXY), 0.000001));
    vec3 tangentNormal = normalize(vec3(
        tangentXY * VintagertxEntityNormalStrength,
        tangentZ));
    vec3 positionDx = dFdx(surfacePosition);
    vec3 positionDy = dFdy(surfacePosition);
    vec2 uvDx = dFdx(uv);
    vec2 uvDy = dFdy(uv);
    vec3 surfaceNormal = normalize(baseNormal);
    float uvDeterminant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    float uvOrientation = uvDeterminant < 0.0 ? -1.0 : 1.0;
    vec3 tangentRaw = (positionDx * uvDy.y - positionDy * uvDx.y) * uvOrientation;
    vec3 bitangentRaw = (positionDy * uvDx.x - positionDx * uvDy.x) * uvOrientation;
    tangentRaw -= surfaceNormal * dot(surfaceNormal, tangentRaw);
    float tangentLengthSquared = dot(tangentRaw, tangentRaw);
    vec3 fallbackAxis = abs(surfaceNormal.z) < 0.999
        ? vec3(0.0, 0.0, 1.0)
        : vec3(0.0, 1.0, 0.0);
    vec3 tangent = tangentLengthSquared > 1e-20
        ? tangentRaw * inversesqrt(tangentLengthSquared)
        : normalize(cross(fallbackAxis, surfaceNormal));
    float handedness = dot(cross(surfaceNormal, tangent), bitangentRaw) < 0.0 ? -1.0 : 1.0;
    vec3 bitangent = normalize(cross(surfaceNormal, tangent)) * handedness;
    return normalize(mat3(tangent, bitangent, surfaceNormal) * tangentNormal);
}

void main() {
    float b = 1;

    if (damageEffect > 0) {
        float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
        if (f < damageEffect - 1.3) discard;
        b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
    }

    vec4 unlitTexColor = texture(entityTex, uv);
    vec4 texColor = unlitTexColor;
    vec4 pbrSurface = texture(vintagertxEntityMaterialTex, uv);
    ivec2 pbrAtlasSize = textureSize(vintagertxEntityMaterialTex, 0);
    ivec2 pbrTexel = clamp(
        ivec2(uv * vec2(pbrAtlasSize)),
        ivec2(0),
        pbrAtlasSize - ivec2(1));
    int pbrMaterialBits = vintagertxEntityPbrEnabled != 0
        ? int(round(texelFetch(vintagertxEntityMaterialTex, pbrTexel, 0).a * 255.0))
        : 0;
    bool vintagertxEntitySurface = (pbrMaterialBits & 128) != 0;
    vec3 shadingNormal = vintagertxEntitySurface
        ? vintagertxPerturbEntityNormal(normal, worldPos.xyz, pbrSurface.rg)
        : normalize(normal);
    float surfaceRoughness = vintagertxEntitySurface ? pbrSurface.b : 0.72;

    #if SHADOWQUALITY > 0
    float intensity = 0.34 + (1 - shadowIntensity)/8.0;
    #else
    float intensity = 0.45;
    #endif

    int eidfloor = (entityId / 100) * 100;
    float seed = (entityId - eidfloor) / 5.0;

    texColor = applyFrostEffect(fragFrostAlpha, texColor, shadingNormal, vertexPosition + vec3(seed));
    if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition, 0);
    if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, shadingNormal, vertexPosition + vec3(seed), 0);

    texColor *= color;
    texColor.rgb *= b;

#if USEOIT>0
    vec4 outColor;
#endif

    float murkiness=getUnderwaterMurkiness();
    if (murkiness > 0) {
        outColor = applyFogAndShadowWithNormal(texColor, 0, shadingNormal, 1, intensity, worldPos.xyz);
        outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
    } else {
        outColor = applyFogAndShadowWithNormal(texColor, fogAmount, shadingNormal, 1, intensity, worldPos.xyz);
    }

    if (glitchFlicker >0 && glitchEffectStrength > 0) {
        float g = gnoise(vec3(gl_FragCoord.y / 2.0, gl_FragCoord.x / 2.0, windWaveCounter*30 + entityId * 3));
        outColor.a *= mix(1, clamp(0.7 + g / 2, 0, 1), glitchEffectStrength);

        float flicker = gnoise(vec3(0, 0, windWaveCounter*60 + entityId * 3));
        outColor.a *= mix(1, clamp(flicker * 10 + 2, 0, 1), glitchEffectStrength);
    }

#if NORMALVIEW == 0
    if (outColor.a < alphaTest) discard;
#endif

    float glow = 0;
#if SHINYEFFECT > 0
    outColor = mix(
        applyReflectiveEffect(outColor, glow, renderFlags, uv, shadingNormal, worldPos, camPos, vec3(1)),
        outColor,
        min(1, 2 * fogAmount));
#endif

#if USEOIT==0 && SSAOLEVEL > 0
    outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
    vec3 viewShadingNormal = vintagertxEntitySurface
        ? vintagertxPerturbEntityNormal(gnormal.xyz, camPos.xyz, pbrSurface.rg)
        : normalize(gnormal.xyz);
    vec3 unlitLinear = pow(max(unlitTexColor.rgb, vec3(0.0)), vec3(2.2));
    float unlitLuminance = dot(unlitLinear, vec3(0.2126, 0.7152, 0.0722));
    int roughnessBits = int(round(clamp(surfaceRoughness, 0.0, 1.0) * 31.0));
    int albedoBits = int(round(clamp(unlitLuminance, 0.0, 1.0) * 31.0));
    // Entity identity and unlit luminance exist even when this skin has no PBR sidecars.
    // The first-person depth-offset branch below still clears this marker deliberately.
    float packedSurface = -float(1 + roughnessBits * 32 + albedoBits) / 1025.0;
    outGNormal = vec4(viewShadingNormal, packedSurface);
#endif

#if NORMALVIEW > 0
    outColor = vec4(
        (shadingNormal.x + 1) / 2,
        (shadingNormal.y + 1) / 2,
        (shadingNormal.z + 1) / 2,
        1);
#endif

#if USEOIT > 0
    OIT(outColor, glowLevel+glow);
#else
    outGlow = vec4(
        glowLevel + glow,
        0,
        float(pbrMaterialBits) / 255.0,
        color.a);
#endif

#if defined(ALLOWDEPTHOFFSET) && ALLOWDEPTHOFFSET > 0
    gl_FragDepth = gl_FragCoord.z + depthOffset;

    #if USEOIT==0 && SSAOLEVEL > 0
        outGPosition.w=1;
        outGNormal.a=0;
        outGlow.b=0;
    #endif
#endif
}
