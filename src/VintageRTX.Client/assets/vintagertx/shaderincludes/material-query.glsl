// R02 BSDF contract: linear Lambert diffuse and exact-complex-Fresnel isotropic GGX conductors.
// No metallic tint multiplier, HDR clamp, albedo reconstruction or diffuse term on a conductor.
struct DirectMaterial { int kind; vec3 eta; vec3 k; float roughness; };
const float VRTX_PI=3.141592653589793;
bool finiteRgb(vec3 v) { return !any(isnan(v)) && !any(isinf(v)); }
float dielectricReflectance(float cosine,float eta) {
    if(eta==1.0)return 0.0;
    float c=clamp(abs(cosine),0.0,1.0);if(c==0.0)return 1.0;
    float ratio=1.0/eta,s2=ratio*ratio*max(0.0,1.0-c*c);
    if(s2>=1.0)return 1.0;
    float ct=sqrt(max(0.0,1.0-s2));
    float rs=(ratio*c-ct)/(ratio*c+ct),rp=(c-ratio*ct)/(c+ratio*ct);
    return 0.5*(rs*rs+rp*rp);
}
float conductorReflectance(float cosine,float eta,float k) {
    if(k==0.0)return dielectricReflectance(cosine,eta);
    float c=clamp(abs(cosine),0.0,1.0);if(c==0.0)return 1.0;
    float c2=c*c,s2=1.0-c2,e2=eta*eta,k2=k*k,t0=e2-k2-s2;
    float a2b2=sqrt(t0*t0+4.0*e2*k2),a=sqrt(max(0.0,0.5*(a2b2+t0)));
    float t1=a2b2+c2,t2=2.0*c*a,t3=c2*a2b2+s2*s2,t4=t2*s2;
    float rs=(t1-t2)/(t1+t2);
    return clamp(0.5*rs*(1.0+(t3-t4)/(t3+t4)),0.0,1.0);
}
vec3 conductorFresnel(DirectMaterial m,float cosine) {
    return vec3(conductorReflectance(cosine,m.eta.x,m.k.x),
        conductorReflectance(cosine,m.eta.y,m.k.y),conductorReflectance(cosine,m.eta.z,m.k.z));
}
// Returns BSDF * projected receiver cosine. Normals/directions must be normalized by the producer.
vec3 directBsdfCos(DirectMaterial m,vec3 baseColor,vec3 normal,vec3 outgoing,vec3 incoming) {
    float nv=dot(normal,outgoing),nl=dot(normal,incoming);
    if(nv<=0.0 || nl<=0.0)return vec3(0);
    if(m.kind==0)return baseColor*(nl/VRTX_PI);
    if(m.roughness==0.0)return vec3(0); // ideal specular paths have a different measure
    vec3 sum=outgoing+incoming;if(dot(sum,sum)<=1e-24)return vec3(0);
    vec3 h=normalize(sum);float alpha=max(0.0001,m.roughness*m.roughness),a2=alpha*alpha;
    // Stable at the narrow lobe peak: 1 + N.H^2*(a^2-1) cancels in float for small alpha.
    vec3 nhCross=cross(normal,h);float sine2=clamp(dot(nhCross,nhCross),0.0,1.0);
    float denominator=sine2+a2*(1.0-sine2);
    float distribution=a2/(VRTX_PI*denominator*denominator);
    float sv=sqrt(nv*nv+a2*max(0.0,1.0-nv*nv));
    float sl=sqrt(nl*nl+a2*max(0.0,1.0-nl*nl));
    float visibility=0.5/(nl*sv+nv*sl);
    return conductorFresnel(m,dot(outgoing,h))*(distribution*visibility*nl);
}

// Shared estimator used by BOTH the full-screen reference pass and the native world shaders.
// The caller owns valid unit directions, material, texture publication and sampling bounds.
struct MaterialDirectResult { vec3 radiance; int unresolved; int blocked; int traced; };
MaterialDirectResult evaluateMaterialDirect(vec3 position,vec3 normal,vec3 geometricNormal,
    vec3 outgoing,vec3 color,DirectMaterial material,int finiteSamples,float rayMinimum,
    int maximumCells,vec2 sampleRotation) {
    vec3 radiance=vec3(0);int unresolved=0,blocked=0,traced=0;
    for(int i=0;i<lightCount;i++) {
        vec4 source=texelFetch(lightData,ivec2(0,i),0);
        vec3 intensity=texelFetch(lightData,ivec2(1,i),0).rgb;
        if(!validEmitter(source,intensity)) {unresolved++;continue;}
        if(all(equal(intensity,vec3(0))))continue;
        vec3 delta=source.xyz-position;
        if(dot(delta,geometricNormal)+source.w<=0.0 || dot(delta,normal)+source.w<=0.0)continue;
        int count=source.w>0.0 ? finiteSamples : 1;vec3 sum=vec3(0);
        for(int j=0;j<64;j++) {
            if(j>=count)break;
            EmitterSegment sample=sampleEmitterSegment(position,source,intensity,
                emitterQuadrature(j,count,sampleRotation));
            if(sample.status!=0) {unresolved++;continue;}
            if(dot(normal,sample.direction)<=0.0 || dot(geometricNormal,sample.direction)<=0.0)continue;
            if(sample.distance<=rayMinimum) {unresolved++;continue;}
            vec3 response=directBsdfCos(material,color,normal,outgoing,sample.direction);
            if(!finiteRgb(response)) {unresolved++;continue;}
            if(all(equal(response,vec3(0))))continue;
            SceneQuery visibility=traceScene(position,sample.direction,rayMinimum,sample.distance,maximumCells);
            traced++;
            if(visibility.status==1)blocked++;
            else if(visibility.status!=0)unresolved++;
            else sum+=response*sample.weight;
        }
        radiance+=sum/float(count);
    }
    return MaterialDirectResult(radiance,unresolved,blocked,traced);
}
