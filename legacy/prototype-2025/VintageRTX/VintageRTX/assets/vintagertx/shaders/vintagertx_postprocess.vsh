#version 330 core

// VintageRTX Post-Process Vertex Shader
// Simple full-screen quad vertex shader for post-processing effects

// Input attributes
layout (location = 0) in vec3 vertex;
layout (location = 1) in vec2 uv;

// Output to fragment shader
out vec2 v_texCoord;
out vec2 v_screenPos;

void main()
{
    // Pass texture coordinates directly
    v_texCoord = uv;
    v_screenPos = vertex.xy;
    
    // Output vertex position for full-screen quad
    // vertex.xy should already be in [-1, 1] range for a full-screen quad
    gl_Position = vec4(vertex.xy, 0.0, 1.0);
}