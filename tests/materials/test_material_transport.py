"""Production material GLSL versus independent double-precision radiometry and TBN witnesses.
These are algorithm/MRT tests, not Vintage Story visual acceptance or a GPU performance benchmark.
"""
from pathlib import Path
import os
import unittest
import numpy as np
import moderngl

ROOT=Path(__file__).resolve().parents[2]
VERTEX='''#version 330 core
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0.,1.);}'''
def function(s,name):
    a=s.index(name);i=s.index('{',a)+1;n=1
    while n:
        n+=(s[i]=='{')-(s[i]=='}');i+=1
    return s[a:i]

def independent_ggx(n,v,l,r,f0):
    """G2 derived via the sum of Smith lambdas, not the optimized production visibility formula."""
    nv=np.sum(n*v,axis=1);nl=np.sum(n*l,axis=1)
    h=v+l;length=np.linalg.norm(h,axis=1)
    h=h/np.maximum(length[:,None],1e-30)
    nh=np.clip(np.sum(n*h,axis=1),0,1);vh=np.clip(np.sum(v*h,axis=1),0,1)
    a=np.maximum(r*r,.0025)
    distribution=a*a/(np.pi*(1+(a*a-1)*nh*nh)**2)
    lambda_v=(np.sqrt(1+a*a*np.maximum(0,1-nv*nv)/np.maximum(nv*nv,1e-30))-1)/2
    lambda_l=(np.sqrt(1+a*a*np.maximum(0,1-nl*nl)/np.maximum(nl*nl,1e-30))-1)/2
    g2=1/(1+lambda_v+lambda_l)
    fresnel=f0+(1-f0)*(1-vh[:,None])**5
    result=fresnel*(distribution*g2/(4*np.maximum(nv,1e-30)))[:,None]
    result[(nv<=0)|(nl<=0)|(length<=1e-6)]=0
    return result

class MaterialTransportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE','1')
        cls.ctx=moderngl.create_standalone_context(require=330,backend='egl')
        cls.source=(ROOT/'src/VintageRTX/assets/vintagertx/shaders/display.frag').read_text(encoding='utf-8')
        kernel=function(cls.source,'vec3 materialFresnel(')+'\n'+function(cls.source,'vec3 evaluateDirectSpecular(')
        cls.program=cls.ctx.program(vertex_shader=VERTEX,fragment_shader='''#version 330 core
uniform sampler2D normals;uniform sampler2D views;uniform sampler2D lights;uniform sampler2D materials;
layout(location=0) out vec4 result;
'''+kernel+'''
void main(){ivec2 p=ivec2(gl_FragCoord.xy);vec4 m=texelFetch(materials,p,0);
result=vec4(evaluateDirectSpecular(texelFetch(normals,p,0).xyz,texelFetch(views,p,0).xyz,
texelFetch(lights,p,0).xyz,m.a,m.rgb),1.);}''')
        cls.vao=cls.ctx.vertex_array(cls.program,[])
        print('Material renderer:',cls.ctx.info['GL_RENDERER'])
    @classmethod
    def tearDownClass(cls):
        cls.vao.release();cls.program.release();cls.ctx.release()
    def evaluate(self,n,v,l,r,f0):
        count=len(n);width=min(count,1024);height=(count+width-1)//width
        values=[np.asarray(n,dtype='f4'),np.asarray(v,dtype='f4'),np.asarray(l,dtype='f4'),
            np.column_stack([f0,r]).astype('f4')]
        textures=[];target=None;fbo=None
        try:
            for unit,(name,data) in enumerate(zip(['normals','views','lights','materials'],values)):
                data=np.pad(data,((0,width*height-count),(0,0)))
                texture=self.ctx.texture((width,height),data.shape[1],data.tobytes(),dtype='f4')
                textures.append(texture);texture.use(unit);self.program[name].value=unit
            target=self.ctx.texture((width,height),4,dtype='f4');fbo=self.ctx.framebuffer([target])
            fbo.use();self.ctx.viewport=(0,0,width,height);self.vao.render(vertices=3)
            return np.frombuffer(target.read(),dtype='f4').reshape(-1,4)[:count,:3].copy()
        finally:
            if fbo:fbo.release()
            if target:target.release()
            for t in textures:t.release()
    def test_ggx_matches_independent_smith_reference(self):
        rng=np.random.default_rng(73198);count=512
        n=np.tile([0.,0.,1.],(count,1))
        v=rng.normal(size=(count,3));v[:,2]=np.abs(v[:,2])+.01;v/=np.linalg.norm(v,axis=1)[:,None]
        l=rng.normal(size=(count,3));l[:,2]=np.abs(l[:,2])+.01;l/=np.linalg.norm(l,axis=1)[:,None]
        r=rng.uniform(.08,1,count);f0=rng.uniform(0,1,(count,3))
        expected=independent_ggx(n,v,l,r,f0);actual=self.evaluate(n,v,l,r,f0)
        np.testing.assert_allclose(actual,expected,rtol=.001,atol=2e-6)
    def test_copper_fresnel_not_a_white_dielectric_times_an_artistic_gain(self):
        n=np.tile([0.,0.,1.],(3,1));v=n.copy();l=n.copy()
        f0=np.tile([.72,.31,.08],(3,1));r=np.array([.15,.45,.9])
        actual=self.evaluate(n,v,l,r,f0)
        self.assertTrue(np.all(actual[:,0]>actual[:,1]));self.assertTrue(np.all(actual[:,1]>actual[:,2]))
        np.testing.assert_allclose(actual[:,0]/actual[:,2],.72/.08,rtol=1e-5)
        self.assertGreater(actual[0,0],actual[2,0])
    def test_white_furnace_single_scatter_energy_is_bounded(self):
        count=65536;i=np.arange(count,dtype='f8')
        z=(i+.5)/count;angle=i*2.399963229728653;rad=np.sqrt(1-z*z)
        l=np.column_stack([rad*np.cos(angle),rad*np.sin(angle),z])
        n=np.tile([0.,0.,1.],(count,1));f0=np.ones((count,3))
        for roughness in [.35,.65,1.0]:
            for cosine in [1.,.6,.2]:
                with self.subTest(roughness=roughness,cosine=cosine):
                    v=np.tile([np.sqrt(1-cosine*cosine),0,cosine],(count,1))
                    actual=self.evaluate(n,v,l,np.full(count,roughness),f0)
                    energy=np.mean(actual.astype('f8'),axis=0)*2*np.pi
                    self.assertTrue(np.all(np.isfinite(energy)))
                    self.assertTrue(np.all(energy<=1.015),energy)
                    self.assertTrue(np.all(energy>0),energy)
    def test_backfaces_and_degenerate_half_vector_return_no_specular(self):
        n=np.tile([0.,0.,1.],(3,1));v=np.array([[0,0,-1],[0,0,1],[1,0,0.]])
        l=np.array([[0,0,1],[0,0,-1],[-1,0,0.]])
        np.testing.assert_array_equal(self.evaluate(n,v,l,np.full(3,.4),np.ones((3,3))),0)
    def test_raw_material_rejects_different_geometry(self):
        source='''#version 330 core
uniform vec4 raw;uniform vec3 position;uniform float surfacePresent;
layout(location=0) out vec4 result;
'''+function(self.source,'bool rawAlbedoMatchesSurface(')+'''
void main(){result=vec4(rawAlbedoMatchesSurface(raw,position,surfacePresent)?1.:0.);}'''
        program=self.ctx.program(vertex_shader=VERTEX,fragment_shader=source)
        vao=self.ctx.vertex_array(program,[]);tex=self.ctx.texture((1,1),4,dtype='f4');fbo=self.ctx.framebuffer([tex])
        try:
            cases=[((.72,.31,.08,-3),(0,0,-3),1,1),((.72,.31,.08,-3),(0,0,-2),1,0),
                ((.72,.31,.08,0),(0,0,-3),1,0),((.72,.31,.08,-3),(0,0,-3),0,0),
                ((float('nan'),.3,.1,-3),(0,0,-3),1,0)]
            for raw,position,packed,expected in cases:
                program['raw'].value=raw;program['position'].value=position;program['surfacePresent'].value=packed
                fbo.use();self.ctx.viewport=(0,0,1,1);vao.render(vertices=3)
                self.assertEqual(np.frombuffer(tex.read(),dtype='f4')[0],expected)
        finally:fbo.release();tex.release();vao.release();program.release()
    def test_authored_normal_survives_atlas_scale_and_mirrored_uv(self):
        chunk=(ROOT/'src/VintageRTX/assets/game/shaders/chunkopaque.fsh').read_text(encoding='utf-8')
        vertex='''#version 330 core
uniform float scale;uniform float mirror;out vec2 uv;out vec4 worldPos;
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);uv=p*scale*vec2(mirror,1.);worldPos=vec4(p,0.,1.);gl_Position=vec4(p*2.-1.,0.,1.);}'''
        fragment='''#version 330 core
in vec2 uv;in vec4 worldPos;uniform int vintagertxPbrEnabled;const float VintagertxNormalStrength=1.;
layout(location=0) out vec4 result;
'''+function(chunk,'vec3 vintagertxPerturbNormal(')+'''
void main(){vec3 n=vintagertxPerturbNormal(vec3(0,0,1),vec2(.675,.4));result=vec4(n,max(dot(n,normalize(vec3(1,0,1))),0.));}'''
        program=self.ctx.program(vertex_shader=vertex,fragment_shader=fragment)
        vao=self.ctx.vertex_array(program,[]);tex=self.ctx.texture((8,8),4,dtype='f4');fbo=self.ctx.framebuffer([tex])
        try:
            measured=[]
            for scale,mirror in [(1.,1.),(1./25600,1.),(1./25600,-1.)]:
                program['scale'].value=scale;program['mirror'].value=mirror;program['vintagertxPbrEnabled'].value=1
                fbo.use();self.ctx.viewport=(0,0,8,8);vao.render(vertices=3)
                measured.append(np.frombuffer(tex.read(),dtype='f4').reshape(8,8,4)[3,3].copy())
            np.testing.assert_allclose(measured[0],measured[1],atol=1e-5)
            np.testing.assert_allclose(measured[0][:3],[.35,-.2,np.sqrt(1-.35**2-.2**2)],atol=1e-5)
            np.testing.assert_allclose(measured[2][:3],measured[0][:3]*[-1,1,1],atol=1e-5)
            self.assertGreater(measured[0][3],measured[2][3]+.4)
        finally:fbo.release();tex.release();vao.release();program.release()

if __name__=='__main__':unittest.main()
