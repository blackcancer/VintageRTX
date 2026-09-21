"""The production material GLSL against independent complex-number optics and float64 GGX.
Image/scene/light integration is additionally exercised by DirectImagePassTests against C# packets.
"""
from pathlib import Path
import os
import unittest
import numpy as np
import moderngl

ROOT = Path(__file__).resolve().parents[2]
VERTEX = '''#version 330 core
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}'''
BODY = '''
uniform sampler2D cases;
layout(location=0)out vec4 outputColor;
void main(){int row=int(gl_FragCoord.x);vec4 a=texelFetch(cases,ivec2(0,row),0),b=texelFetch(cases,ivec2(1,row),0);
DirectMaterial m=DirectMaterial(int(b.w),texelFetch(cases,ivec2(2,row),0).rgb,texelFetch(cases,ivec2(3,row),0).rgb,a.w);
outputColor=vec4(directBsdfCos(m,vec3(.4,.7,.9),vec3(0,0,1),a.xyz,b.xyz),1);}
'''

class MaterialQueryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE','1')
        cls.ctx=moderngl.create_standalone_context(require=330,backend='egl')
        folder=ROOT/'src/VintageRTX.Client/assets/vintagertx/shaderincludes'
        source='\n'.join((folder/name).read_text() for name in
            ('scene-query.glsl','light-query.glsl','material-query.glsl'))
        cls.program=cls.ctx.program(vertex_shader=VERTEX,fragment_shader='#version 330 core\n'+source+BODY)
        cls.vao=cls.ctx.vertex_array(cls.program,[])
    @classmethod
    def tearDownClass(cls):cls.vao.release();cls.program.release();cls.ctx.release()

    def evaluate(self,data):
        data=np.asarray(data,dtype='f4');n=len(data)
        texture=self.ctx.texture((4,n),4,data.tobytes(),dtype='f4')
        output=self.ctx.texture((n,1),4,dtype='f4');fbo=self.ctx.framebuffer([output])
        try:
            texture.filter=(moderngl.NEAREST,moderngl.NEAREST);texture.use(0);self.program['cases']=0
            fbo.use();self.ctx.viewport=(0,0,n,1);self.vao.render(vertices=3)
            return np.frombuffer(output.read(),dtype='f4').reshape(n,4)[:,:3].copy()
        finally:fbo.release();output.release();texture.release()

    @staticmethod
    def reference(data):
        # Complex Snell/Fresnel amplitudes, independent of the real-only shader formulation.
        data=np.asarray(data,dtype='f8');wo=data[:,0,:3];wi=data[:,1,:3]
        nv=wo[:,2];nl=wi[:,2];h=wo+wi;h/=np.linalg.norm(h,axis=1)[:,None]
        cosine=np.clip(np.sum(wo*h,axis=1),0,1)[:,None]
        index=data[:,2,:3]+1j*data[:,3,:3]
        q=np.sqrt(index*index-(1-cosine*cosine))
        rs=(cosine-q)/(cosine+q);rp=(index*index*cosine-q)/(index*index*cosine+q)
        fresnel=.5*(np.abs(rs)**2+np.abs(rp)**2)
        alpha=np.maximum(.0001,data[:,0,3]**2);a2=alpha*alpha
        denominator=1+(a2-1)*h[:,2]**2
        distribution=a2/(np.pi*denominator**2)
        lambda_v=.5*(np.sqrt(1+a2*(1-nv*nv)/(nv*nv))-1)
        lambda_l=.5*(np.sqrt(1+a2*(1-nl*nl)/(nl*nl))-1)
        g=1/(1+lambda_v+lambda_l)
        conductor=fresnel*(distribution*g/(4*nv))[:,None]
        diffuse=np.array([.4,.7,.9])[None,:]*(nl/np.pi)[:,None]
        return np.where((data[:,1,3]==0)[:,None],diffuse,conductor)

    def test_complex_optics_and_ggx_match_independent_reference(self):
        rng=np.random.default_rng(712093)
        n=768;data=np.zeros((n,4,4),dtype='f4')
        for column in [0,1]:
            mu=rng.uniform(.05,1,n);phi=rng.uniform(0,2*np.pi,n)
            data[:,column,:3]=np.stack([np.sqrt(1-mu*mu)*np.cos(phi),np.sqrt(1-mu*mu)*np.sin(phi),mu],axis=1)
        data[:,0,3]=rng.uniform(.08,1,n);data[:,1,3]=1;data[::11,1,3]=0
        data[:,2,:3]=rng.uniform(.15,4,(n,3));data[:,3,:3]=rng.uniform(.1,6,(n,3))
        actual=self.evaluate(data);expected=self.reference(data)
        np.testing.assert_allclose(actual,expected,rtol=0.0003,atol=0.000002)
        swapped=data.copy();swapped[:,0,:3]=data[:,1,:3];swapped[:,1,:3]=data[:,0,:3]
        reverse=self.evaluate(swapped)
        np.testing.assert_allclose(actual/data[:,1,2,None],reverse/data[:,0,2,None],rtol=.0003,atol=.000002)

    def test_narrow_lobe_hdr_and_lossless_indices_have_no_artificial_floor(self):
        data=np.zeros((3,4,4),dtype='f4');data[:,0,2]=data[:,1,2]=1;data[:,1,3]=1
        data[:,0,3]=[.005,.3,.5];data[:,2,:3]=[.2,.85,1.2];data[:,3,:3]=[3.2,2.8,2.5]
        data[1,2,:3]=1;data[1,3,:3]=0
        data[2,2,:3]=1.5;data[2,3,:3]=0
        actual=self.evaluate(data);expected=self.reference(data)
        np.testing.assert_allclose(actual,expected,rtol=.0003,atol=.000001)
        self.assertGreater(float(actual[0].min()),1_000_000)
        np.testing.assert_array_equal(actual[1],np.zeros(3))
        self.assertTrue(np.all(actual[2]>0))

    def test_single_scatter_furnace_never_generates_energy(self):
        mu=(np.arange(128)+.5)/128;phi=(np.arange(64)+.5)*2*np.pi/64
        mu,phi=np.meshgrid(mu,phi,indexing='ij');mu=mu.ravel();phi=phi.ravel()
        data=np.zeros((len(mu),4,4),dtype='f4')
        data[:,1,:3]=np.stack([np.sqrt(1-mu*mu)*np.cos(phi),np.sqrt(1-mu*mu)*np.sin(phi),mu],axis=1)
        data[:,1,3]=1;data[:,2,:3]=[.2,.85,1.2];data[:,3,:3]=[3.2,2.8,2.5]
        for roughness in [.3,.6,1.0]:
            for nv in [.3,.7,1.0]:
                data[:,0,:3]=[np.sqrt(1-nv*nv),0,nv];data[:,0,3]=roughness
                energy=self.evaluate(data).astype('f8').mean(axis=0)*(2*np.pi)
                self.assertTrue(np.all(energy>=0) and np.all(energy<=1.002),(roughness,nv,energy))
                self.assertTrue(np.all(energy>.05))

if __name__=='__main__':unittest.main()
