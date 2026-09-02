#version 330 core

// VintageRTX Enhanced Water Shader
// Adds realistic water reflections and refractions

// Standard uniforms
uniform sampler2D tex;
uniform sampler2D depthTex;
uniform float waterFlowCounter;
uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;
uniform vec3 sunPosRel;
uniform vec3 sunColor;
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform vec2 frameSizeInverse;

// VintageRTX uniforms
uniform sampler2D sceneColor;      // Scene color for reflections
uniform sampler2D sceneDepth;      // Scene depth for soft edges
uniform sampler2D normalMap;       // Water normal map
uniform samplerCube skyboxTex;     // Skybox for environment reflections
uniform float waterTransparency = 0.7;
uniform float waterRoughness = 0.1;
uniform float waveStrength = 0.02;
uniform float refractionStrength = 0.05;
uniform bool enableSSR = true;
uniform float ssrStrength = 0.5;
uniform vec3 cameraPos;

// Input from vertex shader
in vec3 v_worldPos;
in vec3 v_viewPos;
in vec3 v_normal;
in vec2 v_texCoord;
in vec2 v_flowCoords;
in vec4 v_color;
in float v_fogAmount;
in vec4 v_screenPos;

// Output
out vec4 fragColor;

// Calculate fresnel effect
float calculateFresnel(vec3 viewDir, vec3 normal, float f0) {
    float cosTheta = max(dot(normal, viewDir), 0.0);
    return f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
}

// Sample water normal from animated normal map
vec3 getWaterNormal(vec2 texCoord) {
    // Animate water by sampling normal map at different offsets
    vec2 flow1 = texCoord + vec2(waterFlowCounter * 0.03, waterFlowCounter * 0.01);
    vec2 flow2 = texCoord - vec2(waterFlowCounter * 0.02, waterFlowCounter * 0.015);
    
    vec3 normal1 = texture(normalMap, flow1 * 2.0).xyz * 2.0 - 1.0;
    vec3 normal2 = texture(normalMap, flow2 * 3.0).xyz * 2.0 - 1.0;
    
    // Blend the two normal samples
    vec3 waterNormal = normalize(normal1 + normal2);
    waterNormal.xy *= waveStrength;
    
    return normalize(waterNormal);
}

// Screen space reflections for water
vec3 getSSR(vec3 viewPos, vec3 reflectDir, vec2 screenCoord) {
    if (!enableSSR) return vec3(0.0);
    
    const int maxSteps = 32;
    const float stepSize = 0.1;
    
    vec3 currentPos = viewPos;
    
    for (int i = 0; i < maxSteps; i++) {
        currentPos += reflectDir * stepSize;
        
        // Project to screen space
        vec4 projPos = projectionMatrix * vec4(currentPos, 1.0);
        vec2 sampleCoord = projPos.xy / projPos.w * 0.5 + 0.5;
        
        // Check bounds
        if (sampleCoord.x < 0.0 || sampleCoord.x > 1.0 ||
            sampleCoord.y < 0.0 || sampleCoord.y > 1.0) {
            break;
        }
        
        // Sample depth
        float sampleDepth = texture(sceneDepth, sampleCoord).r;
        float currentDepth = projPos.z / projPos.w * 0.5 + 0.5;
        
        // Check for intersection
        if (currentDepth > sampleDepth && currentDepth - sampleDepth < 0.01) {
            return texture(sceneColor, sampleCoord).rgb * ssrStrength;
        }
    }
    
    return vec3(0.0);
}

void main() {
    // Get base water color
    vec4 waterColor = texture(tex, v_texCoord) * v_color;
    
    // Calculate screen coordinates
    vec2 screenCoord = v_screenPos.xy / v_screenPos.w * 0.5 + 0.5;
    
    // Get water normal
    vec3 waterNormal = getWaterNormal(v_flowCoords);
    
    // Transform to world space
    mat3 tbn = mat3(
        normalize(dFdx(v_worldPos)),
        normalize(dFdy(v_worldPos)),
        normalize(v_normal)
    );
    waterNormal = normalize(tbn * waterNormal);
    
    // Calculate view direction
    vec3 viewDir = normalize(cameraPos - v_worldPos);
    
    // Refraction
    vec2 refractionOffset = waterNormal.xy * refractionStrength;
    vec2 refractCoord = screenCoord + refractionOffset;
    
    // Sample scene behind water
    vec3 refractedColor = texture(sceneColor, refractCoord).rgb;
    float sceneDepth = texture(sceneDepth, refractCoord).r;
    
    // Calculate water depth for soft edges
    float waterDepth = length(v_viewPos);
    float depthDiff = sceneDepth - waterDepth;
    float edgeFactor = clamp(depthDiff * 10.0, 0.0, 1.0);
    
    // Reflection
    vec3 reflectDir = reflect(-viewDir, waterNormal);
    vec3 reflection = vec3(0.0);
    
    // Try SSR first
    vec3 viewReflectDir = (viewMatrix * vec4(reflectDir, 0.0)).xyz;
    reflection = getSSR(v_viewPos, normalize(viewReflectDir), screenCoord);
    
    // Fallback to skybox if SSR didn't hit anything
    if (length(reflection) < 0.01) {
        reflection = texture(skyboxTex, reflectDir).rgb;
    }
    
    // Calculate fresnel
    float fresnel = calculateFresnel(viewDir, waterNormal, 0.02);
    
    // Lighting
    vec3 lightDir = normalize(sunPosRel);
    float NdotL = max(dot(waterNormal, lightDir), 0.0);
    vec3 diffuse = waterColor.rgb * sunColor * NdotL;
    
    // Specular highlights
    vec3 halfVec = normalize(lightDir + viewDir);
    float NdotH = max(dot(waterNormal, halfVec), 0.0);
    float specular = pow(NdotH, 256.0 * (1.0 - waterRoughness));
    vec3 specularColor = sunColor * specular;
    
    // Combine refraction and reflection
    vec3 finalColor = mix(refractedColor, reflection, fresnel);
    finalColor = mix(finalColor, waterColor.rgb, waterTransparency * (1.0 - fresnel));
    finalColor += specularColor;
    
    // Apply fog
    finalColor = mix(finalColor, rgbaFogIn.rgb, v_fogAmount);
    
    // Soft edges
    float alpha = waterColor.a * edgeFactor;
    
    fragColor = vec4(finalColor, alpha);
}