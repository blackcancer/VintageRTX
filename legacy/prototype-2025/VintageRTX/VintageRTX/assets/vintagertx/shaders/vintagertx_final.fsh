#version 330 core

// VintageRTX Final Post-Processing Shader
// Implements Screen Space Reflections and final compositing

// G-Buffer inputs
uniform sampler2D sceneColor;
uniform sampler2D sceneDepth;
uniform sampler2D sceneNormals;
uniform sampler2D sceneMaterial; // roughness, metallic, ao, unused
uniform sampler2D scenePosition;

// SSR uniforms
uniform mat4 projectionMatrix;
uniform mat4 invProjectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 invViewMatrix;
uniform vec3 cameraPos;
uniform float ssrEnabled = 1.0;
uniform float ssrStrength = 0.5;
uniform int ssrSteps = 32;
uniform float ssrMaxDistance = 10.0;
uniform float ssrThickness = 0.5;
uniform float ssrFadeStart = 8.0;
uniform float ssrFadeEnd = 10.0;

// Ray tracing uniforms
uniform float rtEnabled = 0.0;
uniform int rtSamples = 16;
uniform float rtIntensity = 1.0;

// Screen coordinates
in vec2 v_texCoord;

// Output
out vec4 fragColor;

// Convert depth to view space position
vec3 getViewPosition(vec2 texCoord, float depth) {
    vec4 clipSpace = vec4(texCoord * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 viewSpace = invProjectionMatrix * clipSpace;
    return viewSpace.xyz / viewSpace.w;
}

// Convert view space to world space
vec3 getWorldPosition(vec3 viewPos) {
    vec4 worldPos = invViewMatrix * vec4(viewPos, 1.0);
    return worldPos.xyz;
}

// Binary search refinement for SSR
vec2 binarySearch(vec3 rayDir, vec3 hitPos, float stride) {
    for (int i = 0; i < 5; i++) {
        stride *= 0.5;
        vec3 testPos = hitPos - rayDir * stride;
        vec4 projPos = projectionMatrix * vec4(testPos, 1.0);
        vec2 testCoord = projPos.xy / projPos.w * 0.5 + 0.5;
        
        float testDepth = texture(sceneDepth, testCoord).r * 2.0 - 1.0;
        float testZ = projPos.z / projPos.w;
        
        if (testZ > testDepth) {
            hitPos = testPos;
        }
    }
    
    vec4 finalProj = projectionMatrix * vec4(hitPos, 1.0);
    return finalProj.xy / finalProj.w * 0.5 + 0.5;
}

// Screen Space Reflections implementation
vec3 screenSpaceReflection(vec3 viewPos, vec3 normal, float roughness) {
    if (ssrEnabled <= 0.0 || roughness > 0.8) {
        return vec3(0.0);
    }
    
    // Calculate reflection vector in view space
    vec3 viewDir = normalize(viewPos);
    vec3 reflectDir = reflect(viewDir, normal);
    
    // Ray marching setup
    float stepSize = ssrMaxDistance / float(ssrSteps);
    vec3 rayPos = viewPos;
    vec3 rayStep = reflectDir * stepSize;
    
    // Ray marching
    for (int i = 0; i < ssrSteps; i++) {
        rayPos += rayStep;
        
        // Project to screen space
        vec4 projectedPos = projectionMatrix * vec4(rayPos, 1.0);
        vec2 screenPos = projectedPos.xy / projectedPos.w * 0.5 + 0.5;
        
        // Check if we're still on screen
        if (screenPos.x < 0.0 || screenPos.x > 1.0 || 
            screenPos.y < 0.0 || screenPos.y > 1.0 ||
            projectedPos.w <= 0.0) {
            break;
        }
        
        // Sample depth at current position
        float sampledDepth = texture(sceneDepth, screenPos).r * 2.0 - 1.0;
        float currentDepth = projectedPos.z / projectedPos.w;
        
        // Check for intersection
        if (currentDepth > sampledDepth && currentDepth - sampledDepth < ssrThickness) {
            // Refine hit position with binary search
            vec2 hitCoord = binarySearch(reflectDir, rayPos, stepSize);
            
            // Sample reflected color
            vec3 reflectedColor = texture(sceneColor, hitCoord).rgb;
            
            // Calculate fade factors
            float distanceFade = 1.0 - smoothstep(ssrFadeStart, ssrFadeEnd, distance(viewPos, rayPos));
            float edgeFade = (1.0 - max(abs(hitCoord.x - 0.5), abs(hitCoord.y - 0.5)) * 2.0);
            float roughnessFade = 1.0 - roughness;
            
            return reflectedColor * ssrStrength * distanceFade * edgeFade * roughnessFade;
        }
    }
    
    return vec3(0.0);
}

// Simple screen-space ray traced ambient occlusion
float rayTracedAO(vec3 viewPos, vec3 normal) {
    if (rtEnabled <= 0.0) {
        return 1.0;
    }
    
    float ao = 0.0;
    float radius = 1.0;
    
    for (int i = 0; i < rtSamples; i++) {
        // Generate random ray direction in hemisphere
        float theta = float(i) / float(rtSamples) * 6.28318;
        float phi = acos(1.0 - 2.0 * fract(sin(float(i)) * 43758.5453));
        
        vec3 sampleDir = vec3(
            sin(phi) * cos(theta),
            sin(phi) * sin(theta),
            cos(phi)
        );
        
        // Align to normal hemisphere
        if (dot(sampleDir, normal) < 0.0) {
            sampleDir = -sampleDir;
        }
        
        // Test occlusion
        vec3 samplePos = viewPos + sampleDir * radius;
        vec4 projPos = projectionMatrix * vec4(samplePos, 1.0);
        vec2 sampleCoord = projPos.xy / projPos.w * 0.5 + 0.5;
        
        if (sampleCoord.x >= 0.0 && sampleCoord.x <= 1.0 &&
            sampleCoord.y >= 0.0 && sampleCoord.y <= 1.0) {
            float sampleDepth = texture(sceneDepth, sampleCoord).r * 2.0 - 1.0;
            float testDepth = projPos.z / projPos.w;
            
            if (testDepth > sampleDepth) {
                ao += 1.0;
            }
        }
    }
    
    return 1.0 - (ao / float(rtSamples)) * rtIntensity;
}

void main() {
    // Sample G-buffer
    vec3 color = texture(sceneColor, v_texCoord).rgb;
    float depth = texture(sceneDepth, v_texCoord).r;
    vec3 normal = texture(sceneNormals, v_texCoord).xyz * 2.0 - 1.0;
    vec4 material = texture(sceneMaterial, v_texCoord);
    float roughness = material.r;
    float metallic = material.g;
    
    // Skip background
    if (depth >= 1.0) {
        fragColor = vec4(color, 1.0);
        return;
    }
    
    // Get positions
    vec3 viewPos = getViewPosition(v_texCoord, depth);
    vec3 worldPos = getWorldPosition(viewPos);
    
    // Transform normal to view space
    vec3 viewNormal = normalize((viewMatrix * vec4(normal, 0.0)).xyz);
    
    // Calculate SSR
    vec3 reflection = screenSpaceReflection(viewPos, viewNormal, roughness);
    
    // Calculate screen-space AO
    float ao = rayTracedAO(viewPos, viewNormal);
    
    // Combine with original color
    vec3 finalColor = color * ao;
    
    // Add reflections (more for metallic surfaces)
    float reflectionStrength = mix(0.04, 1.0, metallic) * (1.0 - roughness);
    finalColor = mix(finalColor, reflection, reflectionStrength);
    
    fragColor = vec4(finalColor, 1.0);
}