// Compose after scene-query, light-query and material-query. The caller verifies the common
// receiver snapshot, geometry revision, evaluated light frame, world and integer anchor.
uniform sampler2D receiverPosition;
uniform sampler2D receiverNormal;
uniform sampler2D receiverGeometricNormal;
uniform sampler2D receiverColor;
uniform sampler2D materialData;
uniform vec3 cameraRelative;
uniform int finiteSourceSamples;
uniform int directMaximumCells;
uniform float directRayMinimum;
uniform vec2 directSampleRotation;
uniform float previewExposure;
layout(location=0)out vec4 directRadiance;
layout(location=1)out vec4 directDiagnostics;
layout(location=2)out vec4 directPreview;

vec3 previewTransfer(vec3 radiance) {
    // Developer display only. Algebra avoids overflow from multiplying a large HDR value by
    // exposure. This preview is never read by lighting, reflections or an acceptance radiance test.
    float peak=max(radiance.r,max(radiance.g,radiance.b));
    vec3 value=radiance/(exp2(-previewExposure)+peak);
    return mix(12.92*value,1.055*pow(max(value,vec3(0)),vec3(1.0/2.4))-0.055,
        greaterThan(value,vec3(0.0031308)));
}
void invalidDirect(float status) {
    directRadiance=vec4(0);directDiagnostics=vec4(1,0,0,status);directPreview=vec4(1,0,1,1);
}
void main() {
    directRadiance=vec4(0);directDiagnostics=vec4(0);directPreview=vec4(0,0,0,1);
    ivec2 pixel=ivec2(gl_FragCoord.xy),extent=textureSize(receiverPosition,0);
    if(any(greaterThanEqual(pixel,extent)) || any(notEqual(textureSize(receiverNormal,0),extent))
        || any(notEqual(textureSize(receiverGeometricNormal,0),extent)) || any(notEqual(textureSize(receiverColor,0),extent)))
        {invalidDirect(5.0);return;}
    vec4 p=texelFetch(receiverPosition,pixel,0);
    if(p.w==0.0)return;
    vec4 n=texelFetch(receiverNormal,pixel,0),g=texelFetch(receiverGeometricNormal,pixel,0);
    vec3 color=texelFetch(receiverColor,pixel,0).rgb;
    ivec2 table=textureSize(materialData,0);
    if(p.w!=1.0 || !finiteRgb(p.xyz) || !finiteRgb(cameraRelative) || !validLightTable()
        || !validUnitNormal(n.xyz) || !validUnitNormal(g.xyz) || dot(n.xyz,g.xyz)<=0.0
        || !finiteRgb(color) || any(lessThan(color,vec3(0))) || any(greaterThan(color,vec3(1)))
        || isnan(n.w) || isinf(n.w) || n.w!=floor(n.w) || n.w<0.0 || n.w>=float(table.y) || table.x!=3
        || finiteSourceSamples<1 || finiteSourceSamples>64 || directMaximumCells<1 || directMaximumCells>256
        || isnan(directRayMinimum) || isinf(directRayMinimum) || directRayMinimum<0.0
        || any(isnan(directSampleRotation)) || any(isinf(directSampleRotation))) {invalidDirect(5.0);return;}
    vec4 a=texelFetch(materialData,ivec2(0,int(n.w)),0),b=texelFetch(materialData,ivec2(1,int(n.w)),0);
    vec3 k=texelFetch(materialData,ivec2(2,int(n.w)),0).rgb;
    if((a.w!=0.0 && a.w!=1.0) || !finiteRgb(b.xyz) || !finiteRgb(k)
        || isnan(b.w) || isinf(b.w) || b.w<0.0 || b.w>1.0
        || (a.w==1.0 && (any(lessThanEqual(b.xyz,vec3(0))) || any(lessThan(k,vec3(0)))))) {invalidDirect(5.0);return;}
    DirectMaterial material=DirectMaterial(int(a.w),b.xyz,k,b.w);
    vec3 outgoing=cameraRelative-p.xyz;float outgoing2=dot(outgoing,outgoing);
    if(outgoing2<=0.0 || isnan(outgoing2) || isinf(outgoing2)) {invalidDirect(5.0);return;}
    outgoing*=inversesqrt(outgoing2);
    if(dot(outgoing,n.xyz)<=0.0 || dot(outgoing,g.xyz)<=0.0) {invalidDirect(3.0);return;}
    if(material.kind==1 && material.roughness==0.0) {invalidDirect(3.0);return;}
    MaterialDirectResult result=evaluateMaterialDirect(p.xyz,n.xyz,g.xyz,outgoing,color,material,
        finiteSourceSamples,directRayMinimum,directMaximumCells,directSampleRotation);
    vec3 radiance=result.radiance;int unresolved=result.unresolved,blocked=result.blocked,traced=result.traced;
    if(!finiteRgb(radiance) || any(lessThan(radiance,vec3(0)))) {invalidDirect(5.0);return;}
    directRadiance=vec4(radiance,1);
    directDiagnostics=vec4(float(unresolved),float(blocked),float(traced),unresolved>0 ? 2.0 : 1.0);
    directPreview=vec4(unresolved>0 ? vec3(1.0,0.0,1.0) : previewTransfer(radiance),1);
}
