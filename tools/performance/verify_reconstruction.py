"""Execute production reconstruction before/after zero-weight culling on real OpenGL.
The reference is the immutable SHA-verified cdcc00ae source. Not an in-game FPS benchmark.
"""
from pathlib import Path
import argparse, hashlib, json, os
import numpy as np
import moderngl
ROOT=Path(__file__).resolve().parents[2]
VERTEX='''#version 330 core
out vec2 uv;
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);uv=p;gl_Position=vec4(p*2.-1.,0.,1.);}'''
HEADER='''#version 330 core
in vec2 uv;
uniform sampler2D gPosition, gNormal;
uniform sampler2D shadowPointHistoryA, shadowPointHistoryB, shadowSunHistory;
uniform mat4 inverseViewMatrix;
uniform vec3 floatingWorldOrigin;
int guideReads=0;
int fallbackCalls=0;
layout(location=0) out vec4 first;
layout(location=1) out vec4 second;
layout(location=2) out vec4 transport;
layout(location=3) out vec4 counters;
void traceRawPointShadowVisibilities(vec3 p,vec3 n,out vec4 a,out vec4 b)
{fallbackCalls++;a=vec4(.2,.3,.4,.5);b=vec4(.6,.7,.8,.9);}
float traceRawSunShadowVisibility(vec3 p,vec3 r,vec3 n){return .35;}
vec3 traceVoxelDiffuseBounce(vec3 p,vec3 n,vec3 g){return vec3(8.,3.,1.);}
'''
def function(s,signature):
    a=s.index(signature);b=s.index('{',a)+1;d=1
    while d:d+=(s[b]=='{')-(s[b]=='}');b+=1
    return s[a:b]
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--baseline',required=True,type=Path);parser.add_argument('--report',required=True,type=Path)
    args=parser.parse_args();data=args.baseline.read_bytes()
    if hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()!='9398ede01bc98aef3d3f6b2ed7b2bcc6322037f3':raise ValueError('Wrong shader reference')
    sources=[data.decode(),(ROOT/'src/VintageRTX/assets/vintagertx/shaders/display.frag').read_text()]
    os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE','1');ctx=moderngl.create_standalone_context(require=330,backend='egl')
    programs=[];results=[]
    try:
        for source in sources:
            read=function(source,'vec4 readGBufferTexel(').replace('{','{ guideReads++;',1)
            fragment=HEADER+read+'\n'+function(source,'void resolveSurfaceShadow(')+'''
void main(){vec3 p=readGBufferTexel(gPosition,uv).xyz;vec3 n=normalize(readGBufferTexel(gNormal,uv).xyz);
vec4 a,b;float sun;vec3 bounce;resolveSurfaceShadow(p,n,vec3(0.,0.,1.),a,b,sun,bounce);
first=a;second=b;transport=vec4(sun,bounce);counters=vec4(float(guideReads),float(fallbackCalls),0.,1.);}'''
            program=ctx.program(vertex_shader=VERTEX,fragment_shader=fragment)
            if 'inverseViewMatrix' in program:program['inverseViewMatrix'].write(np.eye(4,dtype='f4').tobytes())
            if 'floatingWorldOrigin' in program:program['floatingWorldOrigin'].value=(0.,0.,0.)
            programs.append(program)
        for w,h,sw,sh in [(1,1,1,1),(3,3,1,1),(24,18,8,6),(24,18,12,9),(25,19,8,6),(31,23,16,12)]:
            for discontinuous in [False,True]:
                random=np.random.default_rng(892);y,x=np.mgrid[0:h,0:w]
                positions=np.ones((h,w,4),dtype='f4');positions[:,:,0]=x/10;positions[:,:,1]=y/10;positions[:,:,2]=-3
                normals=np.zeros_like(positions);normals[:,:,2]=1
                if discontinuous:
                    mask=(x>w//2)&(y>h//3);positions[mask,2]=-7;normals[mask,0]=.8;normals[mask,2]=.6
                inputs=[ctx.texture((w,h),4,positions.tobytes(),dtype='f4'),ctx.texture((w,h),4,normals.tobytes(),dtype='f4')]
                inputs += [ctx.texture((sw,sh),4,random.random((sh,sw,4),dtype='f4').tobytes(),dtype='f4') for i in range(2)]
                radiance=random.random((sh,sw,4),dtype='f4');radiance[:,:,1:]*=12
                inputs.append(ctx.texture((sw,sh),4,radiance.tobytes(),dtype='f4'))
                arrays=[]
                for program in programs:
                    targets=[ctx.texture((w,h),4,dtype='f4') for i in range(4)];fbo=ctx.framebuffer(targets);vao=ctx.vertex_array(program,[])
                    try:
                        for unit,(name,texture) in enumerate(zip(['gPosition','gNormal','shadowPointHistoryA','shadowPointHistoryB','shadowSunHistory'],inputs)):
                            texture.filter=(moderngl.NEAREST,moderngl.NEAREST);texture.use(unit);program[name].value=unit
                        fbo.use();ctx.viewport=(0,0,w,h);vao.render(vertices=3)
                        arrays.append([np.frombuffer(t.read(),dtype='f4').reshape(h,w,4).copy() for t in targets])
                    finally:
                        vao.release();fbo.release()
                        for t in targets:t.release()
                for texture in inputs:texture.release()
                for i in range(3):np.testing.assert_array_equal(arrays[0][i],arrays[1][i])
                np.testing.assert_array_equal(arrays[0][3][:,:,1],arrays[1][3][:,:,1])
                old=int(arrays[0][3][:,:,0].sum());new=int(arrays[1][3][:,:,0].sum())
                if new>old:raise AssertionError('Guide work increased')
                if w==h==1 and new>=old:raise AssertionError('Zero-weight work was not removed')
                results.append(dict(width=w,height=h,shadowWidth=sw,shadowHeight=sh,discontinuous=discontinuous,
                    baselineGuideReads=old,optimizedGuideReads=new,exactOutput=True,fallbackPixels=int(arrays[1][3][:,:,1].sum())))
        args.report.write_text(json.dumps(dict(renderer=ctx.info['GL_RENDERER'],scope='Production resolveSurfaceShadow only; controlled inputs',cases=results),indent=2)+'\n')
        print('GL equivalence:',len(results),'cases, identical HDR outputs and fallback decisions')
    finally:
        for p in programs:p.release()
        ctx.release()
if __name__=='__main__':main()
