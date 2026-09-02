#version 330 core

uniform sampler2D completeOpaqueScene;
uniform sampler2D beforeFirstPerson;
uniform sampler2D afterFirstPerson;

in vec2 uv;
layout(location = 0) out vec4 outColor;

float maximumComponent(vec3 value)
{
    return max(value.r, max(value.g, value.b));
}

void main()
{
    vec3 completeColor = texture(completeOpaqueScene, uv).rgb;
    vec3 beforeColor = texture(beforeFirstPerson, uv).rgb;
    vec3 afterColor = texture(afterFirstPerson, uv).rgb;
    float colorDelta = maximumComponent(abs(afterColor - beforeColor));
    float firstPersonCoverage = smoothstep(
        1.5 / 255.0,
        6.0 / 255.0,
        colorDelta);
    outColor = vec4(
        mix(completeColor, beforeColor, firstPersonCoverage),
        1.0);
}
