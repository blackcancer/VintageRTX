#version 330 core

// VintageRTX Enhanced Standard Shader - Vertex
// This shader calculates TBN matrix for normal mapping

// Original Vintage Story attributes
in vec3 vertexPosition;
in vec2 uv;
in vec4 color;
in vec3 normal;
in vec4 tangent; // xyz = tangent direction, w = handedness
in int renderPass;
in vec3 rgbaLightIn;

// Standard uniforms
uniform mat4 projectionMatrix;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelViewMatrix;
uniform mat3 normalMatrix;
uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec3 rgbaBlockIn;
uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;
uniform vec3 origin;

// Output to fragment shader
out vec2 v_texCoord;
out vec3 v_worldPos;
out vec3 v_viewPos;
out vec3 v_normal;
out vec3 v_tangent;
out vec3 v_bitangent;
out vec4 v_color;
out vec4 v_lightColor;
out float v_fogAmount;

// Calculate fog amount based on view distance
float calculateFog(float viewDistance) {
    float fogFactor = (viewDistance - fogMinIn) * fogDensityIn;
    return clamp(fogFactor, 0.0, 1.0);
}

void main() {
    // Transform vertex position
    vec4 worldPos = modelMatrix * vec4(vertexPosition, 1.0);
    vec4 viewPos = viewMatrix * worldPos;
    gl_Position = projectionMatrix * viewPos;
    
    // Pass world and view positions
    v_worldPos = worldPos.xyz;
    v_viewPos = viewPos.xyz;
    
    // Transform normal to world space
    v_normal = normalize(normalMatrix * normal);
    
    // Calculate TBN matrix for normal mapping
    vec3 T = normalize(normalMatrix * tangent.xyz);
    vec3 N = v_normal;
    vec3 B = cross(N, T) * tangent.w; // tangent.w contains handedness (+1 or -1)
    
    // Pass TBN vectors
    v_tangent = T;
    v_bitangent = B;
    
    // Pass texture coordinates
    v_texCoord = uv;
    
    // Pass vertex color
    v_color = color;
    
    // Calculate and pass lighting
    v_lightColor = vec4(rgbaLightIn, 1.0);
    
    // Calculate fog
    float viewDistance = length(viewPos.xyz);
    v_fogAmount = calculateFog(viewDistance);
}