#version 330 core

uniform sampler2D cleanPosition;
uniform sampler2D terrainPosition;
uniform vec2 sourceSize;
uniform mat4 projection;
uniform mat4 viewMatrix;
uniform mat4 inverseViewMatrix;
uniform vec3 floatingWorldOrigin;
uniform float surfaceWorldY;
uniform float maximumDistance;

out vec2 sourceUv;

void rejectVertex()
{
    sourceUv = vec2(0.0);
    gl_PointSize = 1.0;
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
}

void main()
{
    // The CPU submits sourceWidth vertices across sourceHeight instances. Direct
    // column/row IDs avoid integer division while retaining every source pixel,
    // including isolated one-pixel alpha-tested entity features.
    int sourceX = gl_VertexID;
    int sourceY = gl_InstanceID;
    sourceUv = (vec2(sourceX, sourceY) + vec2(0.5)) / sourceSize;

    vec3 entityViewPosition = texelFetch(
        cleanPosition,
        ivec2(sourceX, sourceY),
        0).xyz;
    float entityDistanceSquared = dot(entityViewPosition, entityViewPosition);
    float maximumDistanceSquared = maximumDistance * maximumDistance;
    if (entityDistanceSquared <= 0.0001
        || entityDistanceSquared > maximumDistanceSquared)
    {
        rejectVertex();
        return;
    }

    // Terrain is fetched only for a valid, in-range late-opaque source sample.
    // Comparing squared terrain distance with (entityDistance + epsilon)^2 is
    // algebraically identical to the former two-length comparison but removes
    // one square root per eligible source pixel.
    vec3 terrainViewPosition = texelFetch(
        terrainPosition,
        ivec2(sourceX, sourceY),
        0).xyz;
    float terrainDistanceSquared = dot(terrainViewPosition, terrainViewPosition);
    bool terrainValid = terrainDistanceSquared > 0.0001;
    float separatedEntityDistance = sqrt(entityDistanceSquared) + 0.025;
    bool replacedTerrain = !terrainValid
        || separatedEntityDistance * separatedEntityDistance < terrainDistanceSquared;
    if (!replacedTerrain)
    {
        rejectVertex();
        return;
    }

    vec3 sourceWorldOffset = (inverseViewMatrix
        * vec4(entityViewPosition, 1.0)).xyz;
    vec3 sourceWorldPosition = floatingWorldOrigin + sourceWorldOffset;
    // Geometry submerged under the interface contributes to refraction only.
    if (sourceWorldPosition.y <= surfaceWorldY + 0.015)
    {
        rejectVertex();
        return;
    }

    vec3 reflectedWorldPosition = sourceWorldPosition;
    reflectedWorldPosition.y = 2.0 * surfaceWorldY - sourceWorldPosition.y;
    vec3 reflectedViewPosition = (viewMatrix
        * vec4(reflectedWorldPosition - floatingWorldOrigin, 1.0)).xyz;
    vec4 clip = projection * vec4(reflectedViewPosition, 1.0);
    if (clip.w <= 0.0001
        || abs(clip.x) > clip.w * 1.08
        || abs(clip.y) > clip.w * 1.08)
    {
        rejectVertex();
        return;
    }

    // Half-resolution bilinear resolve turns adjacent source samples into a
    // continuous silhouette without a costly full-frame liquid ray march.
    gl_PointSize = 1.75;
    gl_Position = clip;
}
