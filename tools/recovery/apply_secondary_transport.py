"""Reviewed secondary-transport correction, applied to ordinary source before CI validation.
No acceptance thresholds are changed. All expected anchors must match before writing.
"""
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
p=ROOT/'src/VintageRTX/assets/vintagertx/shaders/display.frag'
s=p.read_text(encoding='utf-8')
if 'materialFresnel(f0, vh)' not in s: raise ValueError('Material migration must be published first.')
def once(old,new):
    global s
    if s.count(old)!=1: raise ValueError(f'Expected one anchor: {old[:110]} ({s.count(old)})')
    s=s.replace(old,new,1)
def span(signature):
    a=s.index(signature);i=s.index('{',a)+1;n=1
    while n:
        n+=(s[i]=='{')-(s[i]=='}');i+=1
    return a,i
once('float radial = sqrt(fract(sequence * 0.61803398875) * 0.78);',
     'float radial = sqrt(fract(sequence * 0.61803398875));')
# Translation is immaterial to a derivative, but adding a large float origin beforehand
# destroys local detail. Both full- and reduced-resolution passes use relative inputs.
once('vec3 positionDx = dFdx(rawWorldPosition);','vec3 positionDx = dFdx(rawRelativeWorldPosition);')
once('vec3 positionDy = dFdy(rawWorldPosition);','vec3 positionDy = dFdy(rawRelativeWorldPosition);')
once('vec3 positionDx = dFdx(worldPosition);','vec3 positionDx = dFdx(earlyRelativeWorldPosition);')
once('vec3 positionDy = dFdy(worldPosition);','vec3 positionDy = dFdy(earlyRelativeWorldPosition);')
a,b=span('vec3 traceVoxelDiffuseBounce(')
s=s[:a]+'''vec3 traceVoxelDiffuseBounce(
    vec3 worldPosition,
    vec3 worldNormal,
    vec3 worldGeometricNormal)
{
    int sampleCount = clamp(voxelBounceRayCount, 0, MAX_VOXEL_BOUNCE_RAYS);
    if (sampleCount == 0) return vec3(0.0);
    vec3 accumulated = vec3(0.0);
    vec3 rayOrigin = worldPosition + worldGeometricNormal * 0.16;
    for (int rayIndex = 0; rayIndex < MAX_VOXEL_BOUNCE_RAYS; rayIndex++)
    {
        if (rayIndex >= sampleCount) break;
        vec3 rayDirection = voxelBounceDirection(worldNormal, rayIndex);
        vec3 hitPosition;
        vec3 hitNormal;
        vec3 hitAlbedo;
        float hitDistance;
        if (!traceVoxelBounceSurface(rayOrigin, rayDirection,
            hitPosition, hitNormal, hitAlbedo, hitDistance)) continue;
        vec3 linearHitAlbedo = srgbToLinear(clamp(hitAlbedo, 0.0, 1.0));
        // Black surfaces do not acquire an invented ten-percent neutral reflectance.
        if (maximumComponent(linearHitAlbedo) <= 0.0) continue;
        vec3 incident = vec3(0.0);
        if (skyTraceSteps >= 12 && sunColorStrength.w >= 0.08)
        {
            vec3 skyDirection = skyProbeDirection(rayIndex);
            float bouncedSkyReceiver = max(dot(hitNormal, skyDirection), 0.0);
            if (bouncedSkyReceiver > 0.0)
            {
                float bouncedSkyVisibility = traceSkyRay(hitPosition + hitNormal * 0.04, skyDirection);
                incident += skyRadianceColor() * bouncedSkyReceiver * bouncedSkyVisibility * skyLightStrength;
            }
        }
        float bouncedSunReceiver = max(dot(hitNormal, sunDirection), 0.0);
        if (sunColorStrength.w >= 0.08 && sunLightStrength > 0.0 && bouncedSunReceiver > 0.0)
        {
            // A visible sky direction is not evidence that the different solar direction is clear.
            float bouncedSunVisibility = traceSunVisibility(hitPosition - floatingWorldOrigin,
                hitPosition + hitNormal * 0.04 + sunDirection * 0.005);
            incident += srgbToLinear(sunColorStrength.rgb) * sunColorStrength.w
                * bouncedSunReceiver * bouncedSunVisibility * sunLightStrength;
        }
        // The table is small and bounded. Every selected light participates; reordering slots
        // must never drop the warm lamp solely because it moved beyond the first two entries.
        for (int lightIndex = 0; lightIndex < MAX_VOXEL_LIGHTS; lightIndex++)
        {
            if (lightIndex >= voxelLightCount) break;
            vec4 lightPositionIntensity = voxelLightPositionIntensity[lightIndex];
            vec4 lightColorRadius = voxelLightColorRadius[lightIndex];
            vec3 toLight = lightPositionIntensity.xyz - hitPosition;
            float lightDistance = length(toLight);
            float lightRadius = physicalLightRange(lightIndex, lightPositionIntensity.w,
                min(lightColorRadius.w, pointLightRadius));
            if (lightDistance <= 0.001 || lightDistance >= lightRadius || lightPositionIntensity.w <= 0.0) continue;
            vec3 lightDirection = toLight / lightDistance;
            float emitterReceiver = max(dot(hitNormal, lightDirection), 0.0);
            if (emitterReceiver <= 0.0) continue;
            float radiusFade = photometricRangeFade(lightDistance, lightRadius);
            float incidentRadiance = photometricIncidentRadiance(lightIndex, lightPositionIntensity.w, lightDistance);
            // Preserve the emitter index so its detailed cage is evaluated at the ray endpoint.
            float visibility = traceVoxelVisibility(hitPosition + hitNormal * 0.04,
                lightPositionIntensity.xyz, lightIndex);
            incident += emitterColor(lightColorRadius.rgb) * emitterReceiver
                * radiusFade * incidentRadiance * visibility * emissiveLightStrength;
        }
        float distanceFade = 1.0 - smoothstep(voxelBounceDistance * 0.45, voxelBounceDistance, hitDistance);
        // The cosine-weighted hemisphere accounts for N.L and the Lambertian 1/pi.
        // Retain real RGB reflectance; a neutral floor or chroma blend changes the material.
        accumulated += linearHitAlbedo * incident * distanceFade;
    }
    return accumulated / float(sampleCount);
}
''' + s[b:]
p.write_text(s,encoding='utf-8',newline='\n')
print('Updated cosine domain, independent solar visibility, all selected emitters and relative geometric derivatives.')
