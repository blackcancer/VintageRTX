#version 330 core

uniform sampler2D cleanColor;

in vec2 sourceUv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 color = texture(cleanColor, sourceUv).rgb;
    outColor = vec4(color, 1.0);
}
