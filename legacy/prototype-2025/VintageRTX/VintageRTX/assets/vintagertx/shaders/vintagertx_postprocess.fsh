#version 330 core

// VintageRTX Simple Post-Process Fragment Shader
// Adds simple visual effects to test the pipeline

// Input from vertex shader
in vec2 v_texCoord;
in vec2 v_screenPos;

// Uniforms
uniform sampler2D sceneColor;    // Main scene texture
uniform vec2 screenSize;         // Screen resolution
uniform float time;              // Time for animations
uniform float effectStrength = 1.0;

// VintageRTX configuration
uniform float ssrEnabled = 0.0;
uniform float ssrStrength = 0.5;
uniform float normalMappingEnabled = 0.0;
uniform float normalMapStrength = 1.0;

// Output
out vec4 fragColor;

// Simple chromatic aberration effect
vec3 chromaticAberration(sampler2D tex, vec2 coord, float strength) {
    vec2 offset = (coord - 0.5) * strength * 0.01;
    
    float r = texture(tex, coord + offset).r;
    float g = texture(tex, coord).g;
    float b = texture(tex, coord - offset).b;
    
    return vec3(r, g, b);
}

// Simple contrast and saturation adjustment
vec3 enhanceColors(vec3 color) {
    // Increase contrast slightly
    color = (color - 0.5) * 1.1 + 0.5;
    
    // Increase saturation
    float luminance = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luminance), color, 1.2);
    
    return color;
}

// Subtle vignette effect
float vignette(vec2 coord) {
    vec2 position = coord - 0.5;
    float dist = length(position);
    return 1.0 - smoothstep(0.3, 0.8, dist);
}

void main() {
    vec2 texCoord = v_texCoord;
    
    // Sample the original scene color
    vec3 color = texture(sceneColor, texCoord).rgb;
    
    // Apply effects only if VintageRTX is enabled
    if (effectStrength > 0.0) {
        // Add a subtle pulsing effect to show the shader is working
        float pulse = sin(time * 2.0) * 0.05 + 1.0;
        
        // Apply chromatic aberration if SSR is enabled (as a test)
        if (ssrEnabled > 0.0) {
            color = chromaticAberration(sceneColor, texCoord, ssrStrength * 2.0);
        }
        
        // Enhance colors if normal mapping is enabled
        if (normalMappingEnabled > 0.0) {
            color = enhanceColors(color);
        }
        
        // Apply subtle color tint to show the effect is working
        vec3 tint = vec3(1.0, 0.95, 0.9); // Slightly warm tint
        color *= mix(vec3(1.0), tint, effectStrength * 0.1);
        
        // Apply pulsing effect
        color *= pulse;
        
        // Apply vignette
        float vignetteEffect = vignette(texCoord);
        color *= mix(1.0, vignetteEffect, effectStrength * 0.3);
    }
    
    fragColor = vec4(color, 1.0);
}