#version 330 core

uniform sampler2D sourceColor;

in vec2 uv;
layout(location = 0) out vec4 outColor;

const vec3 LUMA = vec3(0.299, 0.587, 0.114);

void main()
{
    vec3 color = texture(sourceColor, uv).rgb;
    outColor = vec4(color, dot(color, LUMA));
}
