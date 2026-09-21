"""Production finite-emitter GLSL with an analytical visibility provider.
The provider is an explicit unit-test oracle (plane/half-plane/unknown), NOT the terrain traversal.
The existing test_scene_query suite separately executes the real regional traversal on C# packets.
"""
from pathlib import Path
import os
import unittest
import numpy as np
import moderngl

ROOT = Path(__file__).resolve().parents[2]
VERTEX = '''#version 330 core
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}'''
VISIBILITY = '''
struct SceneQuery {int status;float distance;vec3 normal;int primitive;int visited;};
uniform int testVisibility;
SceneQuery traceScene(vec3 o,vec3 d,float lo,float hi,int budget) {
    int status=0;
    if(testVisibility==2)status=2;
    if(testVisibility==3)status=3;
    if(testVisibility==4)status=4;
    if(testVisibility==5)status=1;
    if(testVisibility==1 && d.z>0.0) {
        float t=(1.0-o.z)/d.z;
        if(t>=lo && t<=hi && o.x+t*d.x>0.0)status=1;
    }
    if(testVisibility==6 && d.z>0.0) {
        float t=(3.99-o.z)/d.z;
        if(t>=lo && t<=hi)status=1;
    }
    return SceneQuery(status,hi,vec3(0),-1,1);
}
'''
WRAPPER = '''
uniform vec3 receiver;
uniform vec3 normal;
layout(location=0)out vec4 outputEnergy;
layout(location=1)out vec4 outputCounts;
void main(){DiffuseDirect r=queryDiffuseDirectSampled(receiver,normal,normal,vec3(1),
    0.00001,128,64,vec2(0,0.1234));
    outputEnergy=vec4(r.radiance,1);outputCounts=vec4(r.unresolved,r.blocked,0,1);}
'''

class FiniteEmitterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE','1')
        cls.ctx=moderngl.create_standalone_context(require=330,backend='egl')
        cls.source=(ROOT/'src/VintageRTX.Client/assets/vintagertx/shaderincludes/light-query.glsl').read_text()
        print('Finite emitter GLSL:',cls.ctx.info['GL_RENDERER'])
    @classmethod
    def tearDownClass(cls):cls.ctx.release()

    def evaluate(self,lights,mode=0,receiver=(0,0,0),normal=(0,0,1),source=None):
        resources=[]
        try:
            p=self.ctx.program(vertex_shader=VERTEX,fragment_shader='#version 330 core\n'+VISIBILITY+(source or self.source)+WRAPPER);resources.append(p)
            vao=self.ctx.vertex_array(p,[]);resources.append(vao)
            # Two production texels per source: relative position/radius and evaluated intensity.
            data=np.zeros((max(1,len(lights)),2,4),dtype='f4')
            for i,(position,radius,intensity) in enumerate(lights):
                data[i,0]=(*position,radius);data[i,1,:3]=intensity
            tex=self.ctx.texture((2,len(data)),4,data.tobytes(),dtype='f4');resources.append(tex)
            tex.filter=(moderngl.NEAREST,moderngl.NEAREST);tex.use(0);p['lightData']=0;p['lightCount']=len(lights)
            p['testVisibility']=mode;p['receiver']=receiver;p['normal']=normal
            a=self.ctx.texture((1,1),4,dtype='f4');b=self.ctx.texture((1,1),4,dtype='f4');resources.extend([a,b])
            fbo=self.ctx.framebuffer([a,b]);resources.append(fbo);fbo.use();self.ctx.viewport=(0,0,1,1);vao.render(vertices=3)
            return np.frombuffer(a.read(),dtype='f4').copy(),np.frombuffer(b.read(),dtype='f4').copy()
        finally:
            for r in reversed(resources):r.release()

    def test_projected_solid_angle_oracle_preserves_power_and_hdr(self):
        intensity=np.array([100.,50.,10.])
        expected=intensity/(16*np.pi)
        for radius in [0,1e-8,.01,.5,1.5,3.9]:
            with self.subTest(radius=radius):
                energy,counts=self.evaluate([((0,0,4),radius,intensity)])
                np.testing.assert_allclose(energy[:3],expected,rtol=3e-5,atol=2e-6)
                np.testing.assert_array_equal(counts[:2],[0,0])
        self.assertGreater(energy[0],1)

    def test_half_plane_produces_penumbra_without_clear_sample_renormalization(self):
        light=[((0,0,4),.5,(10,5,1))]
        full,_=self.evaluate(light)
        half,counts=self.evaluate(light,mode=1)
        self.assertEqual(counts[1],32)
        self.assertEqual(counts[0],0)
        np.testing.assert_allclose(half[:3]/full[:3],[.5,.5,.5],atol=.001)
        # Mutation must expose renormalization by clear samples, a known penumbra-erasing bug.
        anchor='3.141592653589793*float(count)'
        self.assertEqual(self.source.count(anchor),1)
        mutant=self.source.replace(anchor,'3.141592653589793*float(max(1,count-result.blocked))')
        broken,_=self.evaluate(light,mode=1,source=mutant)
        self.assertGreater(broken[0]/half[0],1.9)

    def test_unknown_unsupported_and_exhausted_are_not_visible(self):
        for mode in [2,3,4]:
            energy,counts=self.evaluate([((0,0,4),.5,(10,5,1))],mode)
            np.testing.assert_array_equal(energy[:3],np.zeros(3))
            self.assertEqual(counts[0],64)
        energy,counts=self.evaluate([((0,0,4),.5,(10,5,1))],5)
        np.testing.assert_array_equal(energy[:3],np.zeros(3));self.assertEqual(counts[1],64)

    def test_visibility_stops_at_near_surface_not_source_center(self):
        full,_=self.evaluate([((0,0,4),.5,(10,5,1))])
        actual,counts=self.evaluate([((0,0,4),.5,(10,5,1))],6)
        np.testing.assert_array_equal(actual,full);np.testing.assert_array_equal(counts[:2],[0,0])
        point,counts=self.evaluate([((0,0,4),0,(10,5,1))],6)
        self.assertEqual(counts[1],1);np.testing.assert_array_equal(point[:3],np.zeros(3))

    def test_finite_source_above_horizon_is_not_culled_by_its_center(self):
        energy,_=self.evaluate([((2,0,-.1),.5,(10,5,1))])
        self.assertGreater(energy[0],0)
        point,_=self.evaluate([((2,0,-.1),0,(10,5,1))])
        np.testing.assert_array_equal(point[:3],np.zeros(3))

    def test_ninth_emitter_reorders_without_changing_energy_and_switches_off(self):
        inactive=[((0,0,4),.2,(0,0,0))]*8
        lamp=((0,0,4),.2,(100,50,10))
        last,_=self.evaluate(inactive+[lamp]);first,_=self.evaluate([lamp]+inactive)
        np.testing.assert_array_equal(last,first)
        off,_=self.evaluate(inactive+[((0,0,4),.2,(0,0,0))])
        np.testing.assert_array_equal(off[:3],np.zeros(3))
        np.testing.assert_allclose(last[:3],np.array(lamp[2])/(16*np.pi),rtol=3e-5)

    def test_interior_and_malformed_inputs_are_explicitly_unresolved(self):
        for radius in [4,5,-1]:
            energy,counts=self.evaluate([((0,0,4),radius,(1,1,1))])
            np.testing.assert_array_equal(energy[:3],np.zeros(3));self.assertGreater(counts[0],0)
        energy,counts=self.evaluate([((0,0,4),.5,(1,1,1))],normal=(0,0,2))
        self.assertEqual(counts[0],1);np.testing.assert_array_equal(energy[:3],np.zeros(3))

if __name__=='__main__':unittest.main()
