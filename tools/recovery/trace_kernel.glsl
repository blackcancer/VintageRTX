// All t values are distances in blocks along a normalized world-space ray.
// 0 = clear through the requested interval; 1 = hit; 2 = outside known volume;
// 3 = traversal budget exhausted. Unknown coverage is never a confirmed miss.
int traceVoxelSurface(
    vec3 rayOrigin, vec3 rayDirection, float maximumDistance, int maximumSteps,
    out float hitDistance, out vec3 hitNormal, out vec4 hitMaterial);

bool traceFineBlockHit(
    vec3 rayOrigin, vec3 worldRayDirection, float entryDistance, float exitDistance,
    out float hitDistance, out vec3 hitNormal)
{
    hitDistance = max(entryDistance, 0.0);
    hitNormal = vec3(0.0);
    if (exitDistance <= hitDistance || occupancyScale <= 0.0)
    {
        return false;
    }
    vec3 scaledOrigin = (rayOrigin - voxelOrigin) * occupancyScale;
    vec3 scaledDirection = worldRayDirection * occupancyScale;
    // Probe only to select the cell, never to bias the returned intersection.
    float entryProbe = min(0.00001 / occupancyScale,
        (exitDistance - hitDistance) * 0.25);
    vec3 cell = floor(scaledOrigin + scaledDirection * (hitDistance + entryProbe));
    vec3 gridSize = voxelSize * occupancyScale;
    vec3 stepDirection = sign(worldRayDirection);
    for (int stepIndex = 0; stepIndex < 12; stepIndex++)
    {
        if (any(lessThan(cell, vec3(0.0))) || any(greaterThanEqual(cell, gridSize)))
        {
            return false;
        }
        vec3 entry = vec3(-1e30);
        vec3 boundary = vec3(1e30);
        for (int axis = 0; axis < 3; axis++)
        {
            if (abs(scaledDirection[axis]) > 1e-20)
            {
                float nearT = (cell[axis] - scaledOrigin[axis]) / scaledDirection[axis];
                float farT = (cell[axis] + 1.0 - scaledOrigin[axis]) / scaledDirection[axis];
                entry[axis] = min(nearT, farT);
                boundary[axis] = max(nearT, farT);
            }
        }
        float cellEntry = max(entry.x, max(entry.y, entry.z));
        float cellExit = min(boundary.x, min(boundary.y, boundary.z));
        // A face/corner touched over a zero-length interval is not occupied volume.
        if (cellExit > max(cellEntry, hitDistance)
            && max(cellEntry, hitDistance) < exitDistance
            && texelFetch(voxelOccupancy, ivec3(cell), 0).r >= 0.50)
        {
            hitDistance = max(cellEntry, hitDistance);
            int entryAxis = entry.x >= entry.y && entry.x >= entry.z ? 0
                : entry.y >= entry.z ? 1 : 2;
            hitNormal[entryAxis] = -stepDirection[entryAxis];
            return true;
        }
        if (cellExit >= exitDistance)
        {
            return false;
        }
        // Advance exact ties together. No absolute epsilon that skips a thin interval.
        if (boundary.x <= cellExit) cell.x += stepDirection.x;
        if (boundary.y <= cellExit) cell.y += stepDirection.y;
        if (boundary.z <= cellExit) cell.z += stepDirection.z;
        hitDistance = max(hitDistance, cellExit);
    }
    return false;
}

float traceFineBlockVisibility(
    vec3 rayOrigin, vec3 worldRayDirection, float entryDistance, float exitDistance)
{
    float hitDistance;
    vec3 hitNormal;
    return traceFineBlockHit(rayOrigin, worldRayDirection, entryDistance, exitDistance,
        hitDistance, hitNormal) ? 0.0 : 1.0;
}

int traceVoxelSurface(
    vec3 rayOrigin, vec3 rayDirection, float maximumDistance, int maximumSteps,
    out float hitDistance, out vec3 hitNormal, out vec4 hitMaterial)
{
    hitDistance = 0.0;
    hitNormal = vec3(0.0);
    hitMaterial = vec4(0.0);
    vec3 localOrigin = rayOrigin - voxelOrigin;
    if (any(lessThan(localOrigin, vec3(0.0)))
        || any(greaterThanEqual(localOrigin, voxelSize))) return 2;
    if (maximumDistance <= 0.0) return 0;
    if (dot(rayDirection, rayDirection) < 0.5) return 3;
    vec3 cell = floor(localOrigin);
    vec3 stepDirection = sign(rayDirection);
    for (int stepIndex = 0; stepIndex < MAX_VOXEL_STEPS; stepIndex++)
    {
        if (stepIndex >= maximumSteps) return 3;
        if (any(lessThan(cell, vec3(0.0))) || any(greaterThanEqual(cell, voxelSize))) return 2;
        vec3 boundary = vec3(1e30);
        for (int axis = 0; axis < 3; axis++)
        {
            if (abs(rayDirection[axis]) > 1e-20)
            {
                float nextFace = cell[axis] + (rayDirection[axis] > 0.0 ? 1.0 : 0.0);
                boundary[axis] = (nextFace - localOrigin[axis]) / rayDirection[axis];
            }
        }
        float cellExit = min(boundary.x, min(boundary.y, boundary.z));
        vec4 material = texelFetch(voxelVolume, ivec3(cell), 0);
        if (material.a >= 0.45)
        {
            float fineDistance;
            vec3 fineNormal;
            if (traceFineBlockHit(rayOrigin, rayDirection, hitDistance,
                min(cellExit, maximumDistance), fineDistance, fineNormal))
            {
                hitDistance = fineDistance;
                hitNormal = fineNormal;
                hitMaterial = material;
                return 1;
            }
        }
        if (cellExit >= maximumDistance) { hitDistance = maximumDistance; return 0; }
        if (boundary.x <= cellExit) cell.x += stepDirection.x;
        if (boundary.y <= cellExit) cell.y += stepDirection.y;
        if (boundary.z <= cellExit) cell.z += stepDirection.z;
        hitDistance = max(hitDistance, cellExit);
    }
    return 3;
}
