#version 330 core

// VintageRTX PBR Standard Shader
// Full PBR implementation with graceful fallbacks for missing textures

// Standard uniforms
uniform sampler2D tex;
uniform float alphaTest;
uniform vec4 rgbaFog;
uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec4 renderColor;
uniform vec3 sunPosRel;
uniform vec3 sunColor;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform vec3 cameraPos;

// PBR texture uniforms
uniform sampler2D normalMap;
uniform sampler2D roughnessMap;
uniform sampler2D metallicMap;
uniform sampler2D aoMap; // Future: ambient occlusion

// PBR control uniforms
uniform float normalMapStrength = 1.0;
uniform float roughnessDefault = 0.5;
uniform float metallicDefault = 0.0;
uniform float aoDefault = 1.0;

// PBR availability flags (set by engine - using floats for compatibility)
uniform float hasNormalMap = 0.0;
uniform float hasRoughnessMap = 0.0;
uniform float hasMetallicMap = 0.0;
uniform float hasAOMap = 0.0;

// PBR enhancement settings
uniform float pbrEnabled = 1.0;
uniform float metallicBoost = 1.0;
uniform float roughnessAdjust = 0.0;
uniform float normalIntensity = 1.0;

// Input from vertex shader
in vec2 v_texCoord;
in vec3 v_worldPos;
in vec3 v_viewPos;
in vec3 v_normal;
in vec3 v_tangent;
in vec3 v_bitangent;
in vec4 v_color;
in vec4 v_lightColor;
in float v_fogAmount;

// Multiple render targets for deferred shading
layout(location = 0) out vec4 fragColor;
layout(location = 1) out vec4 fragNormal;
layout(location = 2) out vec4 fragMaterial; // roughness, metallic, ao, pbrMask
layout(location = 3) out vec4 fragPosition;

// Constants
const float PI = 3.14159265359;
const float EPSILON = 0.001;

// Safe texture sampling with fallback
vec3 safeNormalSample(vec2 texCoord) {
    if (hasNormalMap <= 0.0) {
        return vec3(0.0, 0.0, 1.0); // Default normal in tangent space
    }
    
    vec3 normal = texture(normalMap, texCoord).xyz;
    // Handle different normal map formats
    if (length(normal) < 0.1) {
        return vec3(0.0, 0.0, 1.0);
    }
    
    return normalize(normal * 2.0 - 1.0);
}

float safeRoughnessSample(vec2 texCoord) {
    if (hasRoughnessMap <= 0.0) {
        return roughnessDefault;
    }
    
    float roughness = texture(roughnessMap, texCoord).r;
    return clamp(roughness + roughnessAdjust, 0.01, 0.99);
}

float safeMetallicSample(vec2 texCoord) {
    if (hasMetallicMap <= 0.0) {
        return metallicDefault;
    }
    
    float metallic = texture(metallicMap, texCoord).r;
    return clamp(metallic * metallicBoost, 0.0, 1.0);
}

float safeAOSample(vec2 texCoord) {
    if (hasAOMap <= 0.0) {
        return aoDefault;
    }
    
    return texture(aoMap, texCoord).r;
}

// Apply normal mapping using TBN matrix with safe fallbacks
vec3 applyNormalMap(vec3 worldNormal, vec2 texCoord) {
    if (hasNormalMap <= 0.0 || normalMapStrength <= 0.0 || pbrEnabled <= 0.0) {
        return normalize(worldNormal);
    }
    
    // Sample normal map safely
    vec3 tangentNormal = safeNormalSample(texCoord);
    tangentNormal.xy *= normalMapStrength * normalIntensity;
    tangentNormal = normalize(tangentNormal);
    
    // Construct TBN matrix with safety checks
    vec3 T = normalize(v_tangent);
    vec3 B = normalize(v_bitangent);
    vec3 N = normalize(worldNormal);
    
    // Ensure TBN is orthogonal
    T = normalize(T - dot(T, N) * N); // Gram-Schmidt orthogonalization
    B = cross(N, T);
    
    mat3 TBN = mat3(T, B, N);
    
    return normalize(TBN * tangentNormal);
}

// Fresnel-Schlick approximation
vec3 fresnelSchlick(float cosTheta, vec3 F0) {
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

// Distribution function (GGX/Trowbridge-Reitz)
float distributionGGX(vec3 N, vec3 H, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;
    
    float num = a2;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;
    
    return num / denom;
}

// Geometry function (Smith's method)
float geometrySchlickGGX(float NdotV, float roughness) {
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    
    float num = NdotV;
    float denom = NdotV * (1.0 - k) + k;
    
    return num / denom;
}

float geometrySmith(vec3 N, vec3 V, vec3 L, float roughness) {
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx2 = geometrySchlickGGX(NdotV, roughness);
    float ggx1 = geometrySchlickGGX(NdotL, roughness);
    
    return ggx1 * ggx2;
}

// Calculate PBR lighting
vec3 calculatePBRLighting(vec3 albedo, vec3 normal, vec3 viewDir, float metallic, float roughness, float ao) {
    // Base reflectivity
    vec3 F0 = vec3(0.04);
    F0 = mix(F0, albedo, metallic);
    
    vec3 Lo = vec3(0.0);
    
    // Directional light (sun)
    vec3 lightDir = normalize(sunPosRel);
    vec3 halfwayDir = normalize(viewDir + lightDir);
    float distance = length(sunPosRel);
    float attenuation = 1.0 / (distance * distance + 1.0);
    vec3 radiance = sunColor * attenuation;
    
    // Calculate BRDF
    float NDF = distributionGGX(normal, halfwayDir, roughness);
    float G = geometrySmith(normal, viewDir, lightDir, roughness);
    vec3 F = fresnelSchlick(max(dot(halfwayDir, viewDir), 0.0), F0);
    
    vec3 kS = F;
    vec3 kD = vec3(1.0) - kS;
    kD *= 1.0 - metallic;
    
    vec3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(normal, viewDir), 0.0) * max(dot(normal, lightDir), 0.0) + EPSILON;
    vec3 specular = numerator / denominator;
    
    float NdotL = max(dot(normal, lightDir), 0.0);
    Lo += (kD * albedo / PI + specular) * radiance * NdotL;
    
    // Ambient lighting
    vec3 ambient = rgbaAmbientIn * albedo * ao;
    
    vec3 color = ambient + Lo;
    
    return color;
}

// Fallback lighting for non-PBR mode
vec3 calculateStandardLighting(vec3 albedo, vec3 normal, vec3 viewDir) {
    // Simple Blinn-Phong lighting
    vec3 ambient = rgbaAmbientIn * albedo;
    
    vec3 lightDir = normalize(sunPosRel);
    float NdotL = max(dot(normal, lightDir), 0.0);
    vec3 diffuse = NdotL * sunColor * albedo;
    
    vec3 halfwayDir = normalize(lightDir + viewDir);
    float NdotH = max(dot(normal, halfwayDir), 0.0);
    float spec = pow(NdotH, 32.0);
    vec3 specular = spec * sunColor * 0.5;
    
    return ambient + diffuse + specular;
}

void main() {
    // Sample main texture
    vec4 texColor = texture(tex, v_texCoord);
    
    // Alpha test
    if (texColor.a < alphaTest) {
        discard;
    }
    
    // Apply vertex color and render color
    vec4 baseColor = texColor * v_color * renderColor;
    vec3 albedo = baseColor.rgb;
    
    // Calculate normal with potential normal mapping
    vec3 normal = normalize(v_normal);
    normal = applyNormalMap(normal, v_texCoord);
    
    // Sample PBR material properties with safe fallbacks
    float roughness = safeRoughnessSample(v_texCoord);
    float metallic = safeMetallicSample(v_texCoord);
    float ao = safeAOSample(v_texCoord);
    
    // Calculate view direction
    vec3 viewDir = normalize(cameraPos - v_worldPos);
    
    // Calculate lighting
    vec3 litColor;
    float pbrMask = 0.0;
    
    if (pbrEnabled > 0.0 && (hasNormalMap > 0.0 || hasRoughnessMap > 0.0 || hasMetallicMap > 0.0)) {
        // Use PBR lighting
        litColor = calculatePBRLighting(albedo, normal, viewDir, metallic, roughness, ao);
        pbrMask = 1.0;
    } else {
        // Use standard lighting
        litColor = calculateStandardLighting(albedo, normal, viewDir);
    }
    
    // Apply block lighting from Vintage Story
    litColor *= v_lightColor.rgb;
    
    // Apply fog
    vec3 finalColor = mix(litColor, rgbaFog.rgb, v_fogAmount);
    
    // Write to multiple render targets
    fragColor = vec4(finalColor, baseColor.a);
    fragNormal = vec4(normal * 0.5 + 0.5, 1.0);
    fragMaterial = vec4(roughness, metallic, ao, pbrMask);
    fragPosition = vec4(v_worldPos, 1.0);
}