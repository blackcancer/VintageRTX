"""Production secondary-lighting GLSL with explicit, controlled geometry/visibility inputs.
The fixtures replace world queries, not production radiometry. Ray tests validate traversal
separately. These are not Vintage Story screenshots or performance measurements.
"""
from pathlib import Path
import os
import unittest
import numpy as np
import moderngl

ROOT = Path(__file__).resolve().parents[2]
VERTEX = '''#version 330 core
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0.,1.);}'''

def function(source, signature):
    start=source.index(signature); i=source.index('{',start)+1; depth=1
    while depth:
        depth+=(source[i]=='{')-(source[i]=='}'); i+=1
    return source[start:i]

HEADER = '''#version 330 core
const int MAX_VOXEL_LIGHTS=8;
const int MAX_VOXEL_BOUNCE_RAYS=4;
const float PHOTOMETRIC_LUX_TO_RENDERER_RADIANCE=0.35014087;
const vec3 LUMA=vec3(.2126,.7152,.0722);
uniform int voxelBounceRayCount;
uniform float voxelBounceDistance;
uniform int voxelLightCount;
uniform vec4 voxelLightPositionIntensity[8];
uniform vec4 voxelLightColorRadius[8];
uniform vec4 voxelLightPhotometry[8];
uniform float pointLightRadius;
uniform float skyLightStrength;
uniform float sunLightStrength;
uniform float emissiveLightStrength;
uniform int skyTraceSteps;
uniform vec4 sunColorStrength;
uniform vec3 sunDirection;
uniform vec3 floatingWorldOrigin;
uniform vec3 fixtureAlbedo;
uniform float fixtureVisibility[8];
uniform float fixtureSkyVisibility;
uniform float fixtureSunVisibility;
uniform vec3 fixtureSkyRadiance;
layout(location=0) out vec4 result;
// One known diffuse hit: isolates radiometry from the independent geometry oracle.
bool traceVoxelBounceSurface(vec3 o,vec3 d,out vec3 p,out vec3 n,out vec3 a,out float t)
{p=vec3(0.);n=vec3(0.,1.,0.);a=fixtureAlbedo;t=.5;return true;}
vec3 skyProbeDirection(int i){return vec3(0.,1.,0.);}
vec3 skyRadianceColor(){return fixtureSkyRadiance;}
float traceSkyRay(vec3 o,vec3 d){return fixtureSkyVisibility;}
float traceSunVisibility(vec3 p,vec3 o){return fixtureSunVisibility;}
float traceVoxelVisibility(vec3 o,vec3 p,int index)
{return index>=0 && index<8 ? fixtureVisibility[index] : 1.;}
float traceCoarseBounceVisibility(vec3 o,vec3 p){return 1.;}
'''

class SecondaryTransportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE','1')
        cls.ctx=moderngl.create_standalone_context(require=330,backend='egl')
        cls.source=(ROOT/'src/VintageRTX/assets/vintagertx/shaders/display.frag').read_text(encoding='utf-8')
        signatures=['vec3 srgbToLinear(', 'float maximumComponent(', 'vec3 emitterColor(',
            'float physicalLightRange(', 'float photometricRangeFade(', 'float photometricIncidentRadiance(',
            'vec3 voxelBounceDirection(', 'vec3 traceVoxelDiffuseBounce(']
        cls.fragment=HEADER+'\n'.join(function(cls.source,s) for s in signatures)+'''
void main(){result=vec4(traceVoxelDiffuseBounce(vec3(0.),vec3(0.,1.,0.),vec3(0.,1.,0.)),1.);}'''
        cls.program=cls.ctx.program(vertex_shader=VERTEX,fragment_shader=cls.fragment)
        print('Secondary transport GL renderer:',cls.ctx.info['GL_RENDERER'])

    @classmethod
    def tearDownClass(cls):
        cls.program.release();cls.ctx.release()

    def render(self, sources=(), albedo=(.5,.5,.5), sky=0., solar=0., daylight=0., sky_radiance=(0.,0.,0.), program=None):
        program=program or self.program
        positions=np.zeros((8,4),dtype='f4'); colors=np.zeros((8,4),dtype='f4')
        photometry=np.tile(np.array([.025,.025,.001,1.],dtype='f4'),(8,1))
        visibility=np.ones(8,dtype='f4')
        for index,color,clear in sources:
            positions[index]=[0.,2.,0.,4.]; colors[index]=[*color,18.];visibility[index]=clear
        values={'voxelBounceRayCount':1,'voxelBounceDistance':6.,'voxelLightCount':8,
            'pointLightRadius':18.,'skyLightStrength':1.,'sunLightStrength':1.,'emissiveLightStrength':1.,
            'skyTraceSteps':12,'sunColorStrength':(1.,1.,1.,daylight),'sunDirection':(0.,1.,0.),
            'floatingWorldOrigin':(0.,0.,0.),'fixtureAlbedo':albedo,'fixtureSkyVisibility':sky,
            'fixtureSunVisibility':solar,'fixtureSkyRadiance':sky_radiance}
        for name,value in values.items():
            if name in program: program[name].value=value
        for name,value in [('voxelLightPositionIntensity',positions),('voxelLightColorRadius',colors),
            ('voxelLightPhotometry',photometry),('fixtureVisibility',visibility)]:
            if name in program:
                uniform=program[name]
                # The intentional two-light mutant optimizes array elements 2..7 away.
                # Respect its linked ABI while requiring all eight in the real production path.
                if program is self.program: self.assertEqual(uniform.array_length,8,name)
                uniform.write(value[:uniform.array_length].tobytes())
        vao=self.ctx.vertex_array(program,[]);target=self.ctx.texture((1,1),4,dtype='f4');fbo=self.ctx.framebuffer([target])
        try:
            fbo.use();self.ctx.viewport=(0,0,1,1);vao.render(vertices=3)
            return np.frombuffer(target.read(),dtype='f4')[:3].copy()
        finally:fbo.release();target.release();vao.release()

    def test_selected_warm_source_is_invariant_under_slot_permutation(self):
        warm=(1.,.5,.15)
        first=self.render([(0,warm,1.)]); last=self.render([(7,warm,1.)])
        self.assertGreater(float(first[0]),0.01)
        self.assertGreater(first[0],first[1]);self.assertGreater(first[1],first[2])
        np.testing.assert_allclose(first,last,rtol=1e-6,atol=1e-7)
        mutant=self.fragment.replace('if (lightIndex >= voxelLightCount) break;',
            'if (lightIndex >= voxelLightCount || lightIndex >= 2) break;')
        self.assertNotEqual(mutant,self.fragment)
        program=self.ctx.program(vertex_shader=VERTEX,fragment_shader=mutant)
        try:
            self.assertGreater(self.render([(0,warm,1.)],program=program)[0],.01)
            np.testing.assert_array_equal(self.render([(7,warm,1.)],program=program),0.)
        finally:program.release()

    def test_visibility_remains_owned_by_each_emitter(self):
        warm=(1.,.5,.15);cool=(.1,.3,1.)
        expected=self.render([(0,cool,1.)])
        with_blocked=self.render([(0,cool,1.),(7,warm,0.)])
        np.testing.assert_allclose(with_blocked,expected,rtol=1e-6,atol=1e-7)
        self.assertGreater(self.render([(7,warm,1.)])[0],self.render([(7,warm,0.)])[0])

    def test_visible_sky_does_not_prove_solar_visibility(self):
        blocked=self.render(sky=1.,solar=0.,daylight=1.)
        np.testing.assert_array_equal(blocked,0.)
        unblocked=self.render(sky=0.,solar=1.,daylight=1.)
        np.testing.assert_allclose(unblocked,np.full(3,.5**2.2),rtol=2e-6,atol=1e-6)
        sky_only=self.render(sky=1.,solar=0.,daylight=1.,sky_radiance=(.1,.2,.3))
        np.testing.assert_allclose(sky_only,np.array([.1,.2,.3])*(.5**2.2),rtol=2e-6,atol=1e-6)

    def test_black_diffuse_surface_cannot_create_reflected_energy(self):
        result=self.render([(0,(1.,.5,.15),1.)],albedo=(0.,0.,0.),sky=1.,solar=1.,daylight=1.,sky_radiance=(1.,1.,1.))
        np.testing.assert_array_equal(result,0.)

    def test_cosine_sampler_covers_complete_hemisphere(self):
        fragment='''#version 330 core
layout(location=0) out vec4 result;
'''+function(self.source,'vec3 voxelBounceDirection(')+'''
void main(){result=vec4(voxelBounceDirection(vec3(0.,1.,0.),int(gl_FragCoord.x)),1.);}'''
        program=self.ctx.program(vertex_shader=VERTEX,fragment_shader=fragment)
        vao=self.ctx.vertex_array(program,[]);target=self.ctx.texture((4096,1),4,dtype='f4');fbo=self.ctx.framebuffer([target])
        try:
            fbo.use();self.ctx.viewport=(0,0,4096,1);vao.render(vertices=3)
            data=np.frombuffer(target.read(),dtype='f4').reshape(-1,4)[:,:3]
            np.testing.assert_allclose(np.linalg.norm(data,axis=1),1.,atol=2e-6)
            self.assertTrue(np.all(data[:,1]>=0.))
            radii_squared=data[:,0]**2+data[:,2]**2
            self.assertAlmostEqual(float(np.mean(radii_squared)),.5,delta=.001)
            self.assertGreater(float(np.max(radii_squared)),.995)
        finally:fbo.release();target.release();vao.release();program.release()

if __name__=='__main__':unittest.main()
