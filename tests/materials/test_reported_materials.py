"""Executable regressions for raw/dynamic albedo and finite normal footprints.
These execute extracted production GLSL, not a C# string-presence surrogate.
"""
import os
import unittest
from pathlib import Path
import moderngl
import numpy as np
from test_material_transport import function, VERTEX

ROOT = Path(__file__).resolve().parents[2]

class ReportedMaterialTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE', '1')
        cls.ctx = moderngl.create_standalone_context(require=330, backend='egl')
        cls.source = (ROOT/'src/VintageRTX/assets/vintagertx/shaders/display.frag').read_text(encoding='utf-8')
        cls.fragment = '''#version 330 core
const vec3 LUMA=vec3(.2126,.7152,.0722);
vec2 uv=vec2(.5);uniform sampler2D sourceColor;uniform int albedoDetailSamples;
uniform int rawAlbedoEnabled;
uniform vec3 fixtureSource;uniform vec4 fixtureMaterial;uniform float fixtureLuminance;
uniform float fixtureDynamic;uniform vec4 fixtureRaw;uniform vec3 fixturePosition;uniform float fixturePacked;
layout(location=0) out vec4 result;
''' + '\n'.join(function(cls.source, name) for name in [
            'vec3 srgbToLinear(', 'vec3 reconstructSurfaceAlbedo(',
            'bool rawAlbedoMatchesSurface(', 'vec3 resolveSurfaceBaseColor(']) + '''
void main(){result=vec4(resolveSurfaceBaseColor(fixtureSource,fixtureMaterial,fixtureLuminance,
fixtureDynamic,fixtureRaw,fixturePosition,fixturePacked),1.);}
'''
        cls.program = cls.ctx.program(vertex_shader=VERTEX, fragment_shader=cls.fragment)

    @classmethod
    def tearDownClass(cls):
        cls.program.release()
        cls.ctx.release()

    def render(self, material=(1.,0.,0.,1.), raw=(0.,0.,0.,0.), enabled=0, program=None):
        program = program or self.program
        values = {'rawAlbedoEnabled':enabled, 'fixtureSource':(.2,.4,.8),
                  'fixtureMaterial':material, 'fixtureLuminance':.18, 'fixtureDynamic':1.,
                  'fixtureRaw':raw, 'fixturePosition':(0.,0.,-3.), 'fixturePacked':1.,
                  'albedoDetailSamples':0, 'sourceColor':0}
        for name,value in values.items():
            if name in program:
                program[name].value=value
        carrier=self.ctx.texture((1,1),3,np.array([.2,.4,.8],dtype='f4').tobytes(),dtype='f4')
        carrier.use(0)
        vao=self.ctx.vertex_array(program,[])
        target=self.ctx.texture((1,1),4,dtype='f4')
        fbo=self.ctx.framebuffer([target])
        try:
            fbo.use(); self.ctx.viewport=(0,0,1,1); vao.render(vertices=3)
            return np.frombuffer(target.read(),dtype='f4')[:3].copy()
        finally:
            fbo.release(); target.release(); vao.release(); carrier.release()

    def test_dynamic_fallback_never_borrows_voxel_chroma(self):
        red=self.render(material=(1.,0.,0.,1.))
        green=self.render(material=(0.,1.,0.,1.))
        expected=np.power([.2,.4,.8],2.2)
        expected=np.clip(expected*.18/max(float(np.dot(expected,[.2126,.7152,.0722])),.02),0,1)
        np.testing.assert_allclose(red, expected, rtol=2e-6,atol=2e-7)
        np.testing.assert_array_equal(red, green)
        mutant=self.fragment.replace('if (dynamicSurface > 0.5)', 'if (false)')
        self.assertNotEqual(mutant,self.fragment)
        program=self.ctx.program(vertex_shader=VERTEX,fragment_shader=mutant)
        try:
            difference=self.render(material=(1.,0.,0.,1.),program=program)-self.render(material=(0.,1.,0.,1.),program=program)
            self.assertGreater(float(np.linalg.norm(difference)),.05,
                               'The regression oracle must detect reintroduced voxel contamination.')
        finally:
            program.release()

    def test_exact_albedo_precedes_fallback_only_at_matching_depth(self):
        raw=(.72,.31,.08,-3.)
        np.testing.assert_allclose(self.render(raw=raw,enabled=1),raw[:3],atol=1e-7)
        fallback=self.render()
        np.testing.assert_array_equal(self.render(raw=(.72,.31,.08,-2.),enabled=1),fallback)
        np.testing.assert_array_equal(self.render(raw=(float('nan'),.31,.08,-3.),enabled=1),fallback)
        np.testing.assert_array_equal(self.render(raw=raw,enabled=0),fallback)

    def test_normal_footprint_broadens_roughness_without_replacing_authored_floor(self):
        fragment='''#version 330 core
uniform float slope;uniform float authored;layout(location=0) out vec4 result;
'''+function(self.source,'float filterSpecularRoughness(')+'''
void main(){vec3 n=normalize(vec3(slope*(gl_FragCoord.x-4.5),0.,1.));
result=vec4(filterSpecularRoughness(authored,n));}
'''
        program=self.ctx.program(vertex_shader=VERTEX,fragment_shader=fragment)
        vao=self.ctx.vertex_array(program,[])
        target=self.ctx.texture((8,8),4,dtype='f4'); fbo=self.ctx.framebuffer([target])
        try:
            def evaluate(slope,authored):
                program['slope'].value=slope;program['authored'].value=authored
                fbo.use();self.ctx.viewport=(0,0,8,8);vao.render(vertices=3)
                values=np.frombuffer(target.read(),dtype='f4').reshape(8,8,4)[:,:,0].copy()
                self.assertTrue(np.all(np.isfinite(values)))
                self.assertTrue(np.all(values>=authored-1e-6));self.assertTrue(np.all(values<=1.))
                return values[4,4]
            self.assertAlmostEqual(evaluate(0.,.2),.2,places=6)
            self.assertGreater(evaluate(.5,.2),.2)
            self.assertAlmostEqual(evaluate(.5,1.),1.,places=6)
        finally:
            fbo.release();target.release();vao.release();program.release()

if __name__=='__main__':
    unittest.main()
