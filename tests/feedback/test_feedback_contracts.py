"""Real GLSL tests of visibility and non-interpolated G-buffer metadata.
These tests exercise production functions, not a replacement renderer or game integration.
Run on Linux/Mesa EGL with numpy and moderngl.
"""
from pathlib import Path
import os
import unittest
import numpy as np
import moderngl

ROOT = Path(__file__).resolve().parents[2]
SHADER = ROOT / 'src/VintageRTX/assets/vintagertx/shaders/display.frag'
VERTEX = '''#version 330 core
void main() { vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
 gl_Position=vec4(p*2.0-1.0,0.0,1.0); }'''

def function(source, signature):
    start = source.index(signature)
    i = source.index('{', start) + 1
    depth = 1
    while depth:
        depth += (source[i] == '{') - (source[i] == '}')
        i += 1
    return source[start:i]

class FeedbackShaderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE', '1')
        cls.ctx = moderngl.create_standalone_context(require=330, backend='egl')
        cls.source = SHADER.read_text(encoding='utf-8')
        print('Feedback GL renderer:', cls.ctx.info['GL_RENDERER'])

    @classmethod
    def tearDownClass(cls):
        cls.ctx.release()

    def render(self, fragment, uniforms, textures=(), width=1):
        program = self.ctx.program(vertex_shader=VERTEX, fragment_shader=fragment)
        vao = self.ctx.vertex_array(program, [])
        target = self.ctx.texture((width, 1), 4, dtype='f4')
        fbo = self.ctx.framebuffer([target])
        try:
            for i, (name, texture) in enumerate(textures):
                texture.use(i); program[name].value = i
            for name, value in uniforms.items(): program[name].value = value
            fbo.use(); self.ctx.viewport = (0, 0, width, 1)
            vao.render(vertices=3)
            return np.frombuffer(target.read(), dtype='f4').reshape(width, 4).copy()
        finally:
            fbo.release(); target.release(); vao.release(); program.release()

    def test_water_only_above_interface_and_in_front_of_solids(self):
        fragment = '''#version 330 core
uniform float cameraY; uniform float surfaceY; uniform float rayY;
uniform float surfaceDepth; uniform float opaqueDepth;
layout(location=0) out vec4 result;
''' + function(self.source, 'float aboveWaterVisibility(') + '''
void main() { result=vec4(aboveWaterVisibility(cameraY,surfaceY,rayY,surfaceDepth,opaqueDepth)); }'''
        cases = [
            ((3, 1, -1, .4, .6), 1),
            ((3, 1, -1, .7, .6), 0),
            ((3, 1, -1, .6, .6), 0),
            ((0, 1, 1, .4, .6), 0),
            ((1, 1, -1, .4, .6), 0),
            ((3, 1, 0, .4, .6), 0),
            ((3, 1, -1, -0.1, .6), 0),
            ((3, 1, -1, 1.0, 1.0), 0),
            ((3, 1, -1, float('nan'), .6), 0),
            ((float('inf'), 1, -1, .4, .6), 0)]
        names = ['cameraY', 'surfaceY', 'rayY', 'surfaceDepth', 'opaqueDepth']
        for values, expected in cases:
            with self.subTest(values=values):
                actual = self.render(fragment, dict(zip(names, values)))
                self.assertEqual(actual[0, 0], expected)

    def test_packed_metadata_is_not_blended_at_edges(self):
        fragment = '''#version 330 core
uniform sampler2D payload;
layout(location=0) out vec4 result;
''' + function(self.source, 'vec4 readGBufferTexel(') + '''
void main() { float x=(gl_FragCoord.x-0.5)/4.0;
 result=readGBufferTexel(payload,vec2(x,0.5)); }'''
        # A sign change represents terrain/entity ownership, not two values to average.
        data = np.array([[1., 0., 0., .125], [0., 1., 0., -.875]], dtype='f4')
        texture = self.ctx.texture((2, 1), 4, data.tobytes(), dtype='f4')
        texture.filter = (moderngl.LINEAR, moderngl.LINEAR)
        try:
            actual = self.render(fragment, {}, [('payload', texture)], width=5)
            expected = data[[0, 0, 1, 1, 1]]
            np.testing.assert_array_equal(actual, expected)
        finally:
            texture.release()

    def test_no_silhouette_geometry_invention_or_held_visibility_bypass(self):
        # Wiring guards complement numerical GPU tests; they are not visual acceptance.
        self.assertNotIn('if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)', self.source)
        self.assertNotIn('distance(lightPositionIntensity.xyz, floatingWorldOrigin) < 0.75', self.source)
        self.assertIn('resolveSurfaceShadow(position, normal,', self.source)
        self.assertIn('waterEvidence *= strictSupport;', self.source)

if __name__ == '__main__':
    unittest.main()
