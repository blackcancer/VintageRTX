// R04 forward consumer of the SAME scene, emission and material estimator as DirectImagePass.
// Does not sample the lit framebuffer. Receivers come from the native rasterized primitive.
uniform int vrtxWorldEnabled; // 0 native, 1 replacement, 2 coverage diagnostic
uniform vec3 vrtxReferenceOffset; // native double reference minus the LightFrame integer anchor
uniform float vrtxWorldExposure; // direct-light exposure, relative source calibration is not SI
uniform int vrtxFiniteSamples;
uniform int vrtxMaximumCells;
// Homogeneous block surface table: two texels per cell, same ready tag/revision as scene cells.
// Dynamic/standard meshes NEVER use a terrain-cell lookup to infer their own material.
#if VRTX_TERRAIN_SURFACE > 0
uniform sampler2D vrtxSurfaceData;
uniform int vrtxSurfaceReady;
int vrtxReadSurface(ivec3 cell,out DirectMaterial m) {
    m=DirectMaterial(0,vec3(1.5),vec3(0),1.0);
    if(vrtxSurfaceReady==0)return 0;
    if(any(notEqual(textureSize(vrtxSurfaceData,0),ivec2(128,216))))return -1;
    ivec3 r=ivec3(floorDiv8(cell.x),floorDiv8(cell.y),floorDiv8(cell.z));
    int slot=positiveMod(r.x,3)+3*positiveMod(r.y,3)+9*positiveMod(r.z,3);
    int local=positiveMod(cell.x,8)+8*positiveMod(cell.y,8)+64*positiveMod(cell.z,8);
    ivec2 pixel=ivec2(2*(local%64),slot*8+local/64);
    vec4 eta=texelFetch(vrtxSurfaceData,pixel,0),k=texelFetch(vrtxSurfaceData,pixel+ivec2(1,0),0);
    if(eta.w==0.0)return 0;
    if(eta.w==-1.0)return -1;
    if(any(isnan(eta)) || any(isinf(eta)) || any(isnan(k)) || any(isinf(k)))return -1;
    if(eta.w==1.0)return 1;
    if(eta.w!=2.0 || any(lessThanEqual(eta.xyz,vec3(0))) || any(greaterThan(eta.xyz,vec3(32)))
        || any(lessThan(k.xyz,vec3(0))) || any(greaterThan(k.xyz,vec3(32))) || k.w<0.0 || k.w>1.0)return -1;
    m=DirectMaterial(1,eta.xyz,k.xyz,k.w);return 2;
}
#endif
vec3 vrtxDecode(vec3 c) {
    c=max(c,vec3(0));
    return mix(c/12.92,pow((c+0.055)/1.055,vec3(2.4)),greaterThan(c,vec3(0.04045)));
}
vec3 vrtxEncode(vec3 c) {
    c=max(c,vec3(0));
    return mix(c*12.92,1.055*pow(c,vec3(1.0/2.4))-0.055,greaterThan(c,vec3(0.0031308)));
}
bool vrtxResolveWorld(vec3 rawAlbedo,vec3 nativeRelative,vec3 nativeNormal,vec3 viewVector,
    float skyVisibility,int flags,float intrinsicGlow,out vec3 carrier) {
    // Evaluate derivatives on the native primitive, before any data-dependent early exit.
    vec3 geometric=cross(dFdx(nativeRelative),dFdy(nativeRelative));
    carrier=vec3(0);
    if(vrtxWorldEnabled==0)return false;
    // Intrinsic glow keeps its distinct emitter contract. Authored conductors are resolved below.
    if(intrinsicGlow>0.0
        || psychedelicStrength>Epsilon || glitchStrength>Epsilon)return false;
    if(!finiteRgb(rawAlbedo) || !finiteRgb(nativeRelative) || !finiteRgb(nativeNormal)
        || !finiteRgb(viewVector) || dot(nativeNormal,nativeNormal)<1e-12
        || dot(geometric,geometric)<1e-18 || dot(viewVector,viewVector)<1e-12)return false;
    vec3 n=normalize(nativeNormal),g=normalize(geometric),outgoing=normalize(viewVector);
    if(dot(n,g)<0.0)g=-g;
    if(dot(outgoing,n)<=0.0 || dot(outgoing,g)<=0.0)return false;
    vec3 position=nativeRelative+vrtxReferenceOffset;
    if(!finiteRgb(position) || !validLightTable() || vrtxFiniteSamples<1 || vrtxFiniteSamples>64
        || vrtxMaximumCells<1 || vrtxMaximumCells>256)return false;
    // Require an observed receiver region even for an empty LightFrame (extinction).
    // The real rasterized primitive, not a cached block template, owns this receiver surface.
    // An observed but unsupported block can RECEIVE light along a known outgoing segment;
    // entering its unknown mesh as a CASTER still returns Unsupported in traceScene below.
    // Never skip the receiver cell in traversal or move the physical shading position.
    ivec3 receiverId=ivec3(floor(position-g*0.0005))+sceneAnchor;
    ivec4 receiverCell=cellAt(receiverId);
    if(receiverCell.x==0) {
        if(vrtxWorldEnabled==2) {carrier=vec3(1,0.5,0);return true;}
        return false;
    }
    DirectMaterial surface=DirectMaterial(0,vec3(1.5),vec3(0),1.0);
    int authored=0;
    #if VRTX_TERRAIN_SURFACE > 0
    // No surface-table fetch on ordinary terrain: the existing cell header carries opt-in.
    if((receiverCell.w & 65536)!=0)authored=vrtxReadSurface(receiverId,surface);
    #endif
    // Flags never choose an optical metal. Unconfigured reflective surfaces remain native.
    // Ideal mirrors have a delta measure and wait for the secondary-transport connection.
    if(authored<0 || ((flags & ReflectiveBitMask)!=0 && authored==0)
        || (surface.kind==1 && surface.roughness==0.0))return false;
    vec3 albedo=vrtxDecode(clamp(rawAlbedo,vec3(0),vec3(1)));
    MaterialDirectResult result=evaluateMaterialDirect(position,n,g,outgoing,albedo,
        surface,vrtxFiniteSamples,0.0005,vrtxMaximumCells,vec2(0));
    if(vrtxWorldEnabled==2) {
        carrier=result.unresolved>0 ? (result.resolved>0 ? vec3(0,1,1) : vec3(1,0,1))
            : result.blocked>0 ? vec3(0,0.35,1) : vec3(0,1,0);
        return true;
    }
    // Publish measured contributions immediately: a distant source outside the cache must not
    // disable a nearby resolved lamp. Unresolved samples contribute no INVENTED energy and stay
    // in each source's denominator (a partial lower estimate, exposed as cyan in coverage mode).
    // When no segment is resolved at all, keep the native carrier. No native block-light RGB is
    // added to a partially resolved estimate; doing so would light occluded receivers twice.
    if((result.unresolved>0 && result.resolved==0) || !finiteRgb(result.radiance))return false;
    float nativeSkyFactor=max(skyVisibility,0.0)*(1.0+max(0.0,shadowIntensity*2.0-1.66)/1.5);
    // The existing sky carrier is still a directional-light approximation, NOT an environment
    // reflection. A conductor must not acquire a Lambert diffuse term or a second copper tint.
    // This view-Fresnel sky placeholder will be replaced by secondary environment integration.
    vec3 sky=vrtxDecode(max(vrtxSkyLight,vec3(0))*nativeSkyFactor);
    vec3 environment=(surface.kind==1 ? conductorFresnel(surface,dot(n,outgoing)) : albedo)*sky;
    // Scene carrier for native fog, water, bloom and final grading. No second display tonemapper.
    carrier=vrtxEncode(environment+result.radiance*exp2(vrtxWorldExposure));
    return finiteRgb(carrier);
}
