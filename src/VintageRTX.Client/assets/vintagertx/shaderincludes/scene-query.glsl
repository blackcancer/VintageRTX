// R01 geometric query ABI. This include contains NO native-framebuffer or color-grade heuristics.
// regionData: 27x1 RGBA32I; cellData: 64x216 RGBA32I; geometryData: width 256 RGBA32F.
// All distances are block units. rayOrigin is relative to the integer anchor, never absolute float world coordinates.
uniform isampler2D regionData;
uniform isampler2D cellData;
uniform sampler2D geometryData;
uniform ivec3 sceneAnchor;
struct SceneQuery { int status; float distance; vec3 normal; int primitive; int visited; };
// 0 clear, 1 mesh hit, 2 unknown, 3 unsupported, 4 budget exhaustion, 5 invalid input.
vec4 geometryAt(int index) { return texelFetch(geometryData,ivec2(index%256,index/256),0); }
int floorDiv8(int x) { int q=x/8;return q-((x<0 && x%8!=0)?1:0); }
int positiveMod(int x,int m) { return (x%m+m)%m; }
ivec4 cellAt(ivec3 cell) {
    ivec3 r=ivec3(floorDiv8(cell.x),floorDiv8(cell.y),floorDiv8(cell.z));
    int slot=positiveMod(r.x,3)+3*positiveMod(r.y,3)+9*positiveMod(r.z,3);
    ivec4 tag=texelFetch(regionData,ivec2(slot,0),0);
    if(tag.w==0 || any(notEqual(tag.xyz,r))) return ivec4(0);
    int local=positiveMod(cell.x,8)+8*positiveMod(cell.y,8)+64*positiveMod(cell.z,8);
    return texelFetch(cellData,ivec2(local%64,slot*8+local/64),0);
}
bool queryBox(vec3 origin,vec3 direction,float lo,float hi,vec3 bmin,vec3 bmax) {
    for(int axis=0;axis<3;axis++) {
        if(direction[axis]==0.0) { if(origin[axis]<bmin[axis] || origin[axis]>bmax[axis]) return false; }
        else { float a=(bmin[axis]-origin[axis])/direction[axis],b=(bmax[axis]-origin[axis])/direction[axis];lo=max(lo,min(a,b));hi=min(hi,max(a,b));if(lo>hi)return false; }
    }
    return true;
}
bool queryTriangle(vec3 origin,vec3 direction,float lo,float hi,int index,out float distance,out vec3 normal,out int primitive) {
    vec4 a=geometryAt(index),b=geometryAt(index+1),c=geometryAt(index+2);
    vec3 e1=b.xyz-a.xyz,e2=c.xyz-a.xyz,p=cross(direction,e2);float det=dot(e1,p);
    distance=0.0;normal=vec3(0);primitive=int(a.w);
    if(det==0.0) return false;
    vec3 s=origin-a.xyz;float u=dot(s,p)/det;if(u<0.0 || u>1.0)return false;
    vec3 q=cross(s,e1);float v=dot(direction,q)/det;if(v<0.0 || u+v>1.0)return false;
    float t=dot(e2,q)/det;if(isnan(t) || isinf(t) || t<lo || t>hi)return false;
    distance=t;normal=normalize(cross(e1,e2));return true;
}
SceneQuery queryMesh(vec3 origin,vec3 direction,float lo,float hi,int root,int end) {
    SceneQuery result=SceneQuery(0,hi,vec3(0),-1,0);int node=root;
    // Median BVH, <=4096 triangles, leaves <=4: at most 2047 nodes. Failure is not a miss.
    for(int work=0;work<4096;work++) {
        if(node>=end) return result;
        vec4 a=geometryAt(node),b=geometryAt(node+1),meta=geometryAt(node+2);
        if(!queryBox(origin,direction,lo,result.distance,a.xyz,b.xyz)) { node=int(meta.z);continue; }
        int count=int(meta.y);
        if(count==0) { node+=3;continue; }
        for(int i=0;i<4;i++) {
            if(i>=count)break;
            float d;vec3 n;int primitive;
            if(queryTriangle(origin,direction,lo,result.distance,int(meta.x)+i*3,d,n,primitive)
                && (result.status==0 || d<result.distance || (d==result.distance && primitive<result.primitive)))
                result=SceneQuery(1,d,n,primitive,0);
        }
        node=int(meta.z);
    }
    return SceneQuery(4,lo,vec3(0),-1,0);
}
SceneQuery traceScene(vec3 rayOrigin,vec3 direction,float minimum,float maximum,int maximumCells) {
    if(any(isnan(rayOrigin)) || any(isinf(rayOrigin)) || any(isnan(direction)) || any(isinf(direction))
        || isnan(minimum) || isinf(minimum) || isnan(maximum) || isinf(maximum)
        || minimum<0.0 || maximum<=minimum || dot(direction,direction)<1e-20)
        return SceneQuery(5,0.0,vec3(0),-1,0);
    direction=normalize(direction);
    ivec3 cell=ivec3(floor(rayOrigin+direction*minimum));float entered=minimum;
    for(int stepIndex=0;stepIndex<256;stepIndex++) {
        if(stepIndex>=maximumCells) return SceneQuery(4,entered,vec3(0),-1,stepIndex);
        vec3 local=rayOrigin-vec3(cell),boundary=vec3(3.402823e38);
        for(int axis=0;axis<3;axis++)if(direction[axis]!=0.0)
            boundary[axis]=((direction[axis]>0.0?1.0:0.0)-local[axis])/direction[axis];
        float next=min(boundary.x,min(boundary.y,boundary.z)),exit=min(next,maximum);
        if(exit>entered) {
            ivec4 data=cellAt(cell+sceneAnchor);
            if(data.x==0) return SceneQuery(2,entered,vec3(0),-1,stepIndex+1);
            if(data.x==3) return SceneQuery(3,entered,vec3(0),-1,stepIndex+1);
            if(data.x==2) {
                SceneQuery hit=queryMesh(local,direction,entered,exit,data.y,data.z);
                if(hit.status!=0) { hit.visited=stepIndex+1;return hit; }
            }
        }
        if(exit>=maximum)return SceneQuery(0,maximum,vec3(0),-1,stepIndex+1);
        for(int axis=0;axis<3;axis++)if(boundary[axis]<=next)cell[axis]+=int(sign(direction[axis]));
        entered=max(entered,next);
    }
    return SceneQuery(4,entered,vec3(0),-1,256);
}
