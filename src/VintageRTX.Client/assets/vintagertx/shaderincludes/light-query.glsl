// Include after scene-query.glsl. lightData uses the SAME integer sceneAnchor as the scene query.
// The CPU publishes one evaluated light frame; no pass-specific clock or random modulation exists.
uniform sampler2D lightData;
uniform int lightCount;
struct PointIncident { vec3 radiance; vec3 direction; int status; };
struct DiffuseDirect { vec3 radiance; int unresolved; int blocked; };
PointIncident queryPointIncident(vec3 receiver, int index, float rayMinimum, int maximumCells) {
    if (index < 0 || index >= lightCount || lightCount > textureSize(lightData,0).y
        || rayMinimum < 0.0 || isnan(rayMinimum) || isinf(rayMinimum))
        return PointIncident(vec3(0),vec3(0),5);
    vec4 source=texelFetch(lightData,ivec2(0,index),0);
    vec3 intensity=texelFetch(lightData,ivec2(1,index),0).rgb;
    if (any(isnan(source)) || any(isinf(source)) || any(isnan(intensity)) || any(isinf(intensity))
        || any(lessThan(intensity,vec3(0)))) return PointIncident(vec3(0),vec3(0),5);
    if (source.w != 0.0) return PointIncident(vec3(0),vec3(0),3); // finite emitters need their own estimator
    if (all(equal(intensity,vec3(0)))) return PointIncident(vec3(0),vec3(0),0);
    vec3 segment=source.xyz-receiver;float r2=dot(segment,segment);
    if (isnan(r2) || isinf(r2) || r2 <= rayMinimum*rayMinimum || r2 <= 0.0)
        return PointIncident(vec3(0),vec3(0),5);
    float distance=sqrt(r2);vec3 direction=segment/distance;
    SceneQuery visibility=traceScene(receiver,direction,rayMinimum,distance,maximumCells);
    // Clear is a proven interval. Unknown/unsupported/exhausted never becomes a visible light.
    return PointIncident(visibility.status==0 ? intensity/r2 : vec3(0),direction,visibility.status);
}
DiffuseDirect queryDiffuseDirect(vec3 receiver,vec3 shadingNormal,vec3 geometricNormal,
    vec3 linearAlbedo,float rayMinimum,int maximumCells) {
    DiffuseDirect result=DiffuseDirect(vec3(0),0,0);
    if (lightCount<0 || lightCount>textureSize(lightData,0).y) { result.unresolved=1;return result; }
    // All supplied sources participate. Spatial selection is a separate, explicit future contract.
    for (int i=0;i<lightCount;i++) {
        vec3 toLight=texelFetch(lightData,ivec2(0,i),0).xyz-receiver;
        if (dot(toLight,geometricNormal)<=0.0 || dot(toLight,shadingNormal)<=0.0)continue;
        PointIncident incoming=queryPointIncident(receiver,i,rayMinimum,maximumCells);
        if(incoming.status>1)result.unresolved++;
        else if(incoming.status==1)result.blocked++;
        else result.radiance+=linearAlbedo*incoming.radiance
            *max(dot(shadingNormal,incoming.direction),0.0)/3.141592653589793;
    }
    return result;
}
