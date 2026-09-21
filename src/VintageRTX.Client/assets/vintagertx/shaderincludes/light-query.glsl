// Include after scene-query.glsl. Source positions use the SAME integer sceneAnchor.
// One CPU-evaluated LightFrame supplies every pass. No shader-side flicker or source-slot seed.
// Radius 0: delta point. Radius >0: uniform outward spherical emitter, NOT a volume flame.
uniform sampler2D lightData;
uniform int lightCount;
struct PointIncident { vec3 radiance; vec3 direction; int status; };
// Counters count sampled visibility rays; an unresolved sample is never renormalized away.
struct DiffuseDirect { vec3 radiance; int unresolved; int blocked; };
struct EmitterSegment { vec3 weight; vec3 direction; float distance; int status; };

bool validEmitter(vec4 source,vec3 intensity) {
    return !any(isnan(source)) && !any(isinf(source)) && source.w>=0.0
        && !any(isnan(intensity)) && !any(isinf(intensity)) && all(greaterThanEqual(intensity,vec3(0)));
}
bool validLightTable() { ivec2 size=textureSize(lightData,0);return size.x>=2 && lightCount>=0 && lightCount<=size.y; }

// Weight is incident radiance divided by the directional PDF. For a sphere L=I/(pi*R^2),
// PDF=1/[2*pi*(1-cosThetaMax)]. Keep the algebra rationalized near the point-light limit.
EmitterSegment sampleEmitterSegment(vec3 receiver,vec4 source,vec3 intensity,vec2 sampleUv) {
    EmitterSegment invalid=EmitterSegment(vec3(0),vec3(0),0.0,5);
    if (!validEmitter(source,intensity) || any(isnan(receiver)) || any(isinf(receiver))
        || any(isnan(sampleUv)) || any(isinf(sampleUv))
        || any(lessThan(sampleUv,vec2(0))) || any(greaterThanEqual(sampleUv,vec2(1)))) return invalid;
    if (all(equal(intensity,vec3(0)))) return EmitterSegment(vec3(0),vec3(0),0.0,0);
    vec3 delta=source.xyz-receiver;float d2=dot(delta,delta);
    if (isnan(d2) || isinf(d2) || d2<=0.0) return invalid;
    float d=sqrt(d2);vec3 axis=delta/d;
    if(source.w==0.0) {
        vec3 weight=intensity/d2;
        if(any(isnan(weight)) || any(isinf(weight)))return invalid;
        return EmitterSegment(weight,axis,d,0);
    }
    // The outward-surface model has no contract for a receiver inside it. Do not turn it into a point.
    if(source.w>=d) return EmitterSegment(vec3(0),vec3(0),0.0,3);
    float ratio=source.w/d,sinMaximum2=ratio*ratio;
    float cosMaximum=sqrt(max(0.0,1.0-sinMaximum2));
    float oneMinusCosMaximum=sinMaximum2/(1.0+cosMaximum);
    float oneMinusCosTheta=sampleUv.x*oneMinusCosMaximum;
    float cosTheta=1.0-oneMinusCosTheta;
    float sinTheta2=oneMinusCosTheta*(2.0-oneMinusCosTheta);
    float phi=6.283185307179586*sampleUv.y;
    vec3 helper=abs(axis.y)<0.9 ? vec3(0,1,0) : vec3(1,0,0);
    vec3 tangent=normalize(cross(helper,axis)),bitangent=cross(axis,tangent);
    vec3 direction=axis*cosTheta+sqrt(max(0.0,sinTheta2))*(tangent*cos(phi)+bitangent*sin(phi));
    float nearDistance=d*(1.0-sinMaximum2)/(cosTheta+sqrt(max(0.0,sinMaximum2-sinTheta2)));
    vec3 weight=intensity*(2.0/(d2*(1.0+cosMaximum)));
    if(any(isnan(weight)) || any(isinf(weight)) || isnan(nearDistance) || isinf(nearDistance)
        || nearDistance<=0.0)return invalid;
    return EmitterSegment(weight,direction,nearDistance,0);
}

PointIncident traceEmitterIncident(vec3 receiver,vec4 source,vec3 intensity,vec2 sampleUv,
    float rayMinimum,int maximumCells) {
    if(rayMinimum<0.0 || isnan(rayMinimum) || isinf(rayMinimum))return PointIncident(vec3(0),vec3(0),5);
    EmitterSegment sample=sampleEmitterSegment(receiver,source,intensity,sampleUv);
    if(sample.status!=0)return PointIncident(vec3(0),sample.direction,sample.status);
    if(all(equal(sample.weight,vec3(0))))return PointIncident(vec3(0),sample.direction,0);
    if(sample.distance<=rayMinimum)return PointIncident(vec3(0),sample.direction,5);
    // Stop at the sampled near surface, not the center: an obstacle behind that surface cannot
    // occlude this path. Every real obstacle in front is retained, including the source's cage.
    SceneQuery visibility=traceScene(receiver,sample.direction,rayMinimum,sample.distance,maximumCells);
    return PointIncident(visibility.status==0 ? sample.weight : vec3(0),sample.direction,visibility.status);
}
PointIncident queryEmitterIncident(vec3 receiver,int index,vec2 sampleUv,float rayMinimum,int maximumCells) {
    if(!validLightTable() || index<0 || index>=lightCount)return PointIncident(vec3(0),vec3(0),5);
    return traceEmitterIncident(receiver,texelFetch(lightData,ivec2(0,index),0),
        texelFetch(lightData,ivec2(1,index),0).rgb,sampleUv,rayMinimum,maximumCells);
}
// Preserve the explicitly POINT query: callers cannot mistake a finite source for a delta.
PointIncident queryPointIncident(vec3 receiver,int index,float rayMinimum,int maximumCells) {
    if(!validLightTable() || index<0 || index>=lightCount)return PointIncident(vec3(0),vec3(0),5);
    vec4 source=texelFetch(lightData,ivec2(0,index),0);
    vec3 intensity=texelFetch(lightData,ivec2(1,index),0).rgb;
    if(!validEmitter(source,intensity))return PointIncident(vec3(0),vec3(0),5);
    if(source.w!=0.0)return PointIncident(vec3(0),vec3(0),3);
    return traceEmitterIncident(receiver,source,intensity,vec2(0.5),rayMinimum,maximumCells);
}
float lightRadicalInverse(uint bits) {
    bits=(bits<<16u)|(bits>>16u);
    bits=((bits&0x55555555u)<<1u)|((bits&0xAAAAAAAAu)>>1u);
    bits=((bits&0x33333333u)<<2u)|((bits&0xCCCCCCCCu)>>2u);
    bits=((bits&0x0F0F0F0Fu)<<4u)|((bits&0xF0F0F0F0u)>>4u);
    bits=((bits&0x00FF00FFu)<<8u)|((bits&0xFF00FF00u)>>8u);
    return float(bits)*2.3283064365386963e-10;
}
// Cranley-Patterson rotation is supplied by the consumer, shared by its passes. No time modulation.
vec2 emitterQuadrature(int ordinal,int count,vec2 rotation) {
    return fract(vec2((float(ordinal)+0.5)/float(count),lightRadicalInverse(uint(ordinal)))+rotation);
}
bool validUnitNormal(vec3 n) { return !any(isnan(n)) && !any(isinf(n)) && abs(dot(n,n)-1.0)<=0.001; }
DiffuseDirect queryDiffuseDirectSampled(vec3 receiver,vec3 shadingNormal,vec3 geometricNormal,
    vec3 linearAlbedo,float rayMinimum,int maximumCells,int finiteSamples,vec2 rotation) {
    DiffuseDirect result=DiffuseDirect(vec3(0),0,0);
    if(!validLightTable() || finiteSamples<1 || finiteSamples>64
        || !validUnitNormal(shadingNormal) || !validUnitNormal(geometricNormal)
        || any(isnan(linearAlbedo)) || any(isinf(linearAlbedo))
        || any(lessThan(linearAlbedo,vec3(0))) || any(greaterThan(linearAlbedo,vec3(1)))
        || any(isnan(rotation)) || any(isinf(rotation))) { result.unresolved=1;return result; }
    // Sources are read once, not once per shadow ray. Points always need only one ray.
    for(int i=0;i<lightCount;i++) {
        vec4 source=texelFetch(lightData,ivec2(0,i),0);
        vec3 intensity=texelFetch(lightData,ivec2(1,i),0).rgb;
        if(!validEmitter(source,intensity)){result.unresolved++;continue;}
        if(all(equal(intensity,vec3(0))))continue;
        vec3 delta=source.xyz-receiver;
        // Conservative plane support: the CENTER may be below the horizon while part of a sphere is above it.
        if(dot(delta,geometricNormal)+source.w<=0.0 || dot(delta,shadingNormal)+source.w<=0.0)continue;
        int count=source.w>0.0 ? finiteSamples : 1;
        vec3 sum=vec3(0);
        for(int j=0;j<64;j++) {
            if(j>=count)break;
            EmitterSegment sample=sampleEmitterSegment(receiver,source,intensity,emitterQuadrature(j,count,rotation));
            if(sample.status!=0){result.unresolved++;continue;}
            float cosine=dot(shadingNormal,sample.direction);
            if(cosine<=0.0 || dot(geometricNormal,sample.direction)<=0.0)continue;
            if(rayMinimum<0.0 || isnan(rayMinimum) || isinf(rayMinimum) || sample.distance<=rayMinimum)
            {result.unresolved++;continue;}
            SceneQuery visibility=traceScene(receiver,sample.direction,rayMinimum,sample.distance,maximumCells);
            if(visibility.status>1)result.unresolved++;
            else if(visibility.status==1)result.blocked++;
            else sum+=sample.weight*cosine;
        }
        // Divide by ALL generated directions. Renormalizing only clear samples would erase penumbrae.
        result.radiance+=linearAlbedo*sum/(3.141592653589793*float(count));
    }
    return result;
}
DiffuseDirect queryDiffuseDirect(vec3 receiver,vec3 shadingNormal,vec3 geometricNormal,
    vec3 linearAlbedo,float rayMinimum,int maximumCells) {
    return queryDiffuseDirectSampled(receiver,shadingNormal,geometricNormal,linearAlbedo,
        rayMinimum,maximumCells,8,vec2(0));
}
