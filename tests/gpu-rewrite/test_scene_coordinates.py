"""Execute the production integer coordinate functions, including full int32 limits."""
from pathlib import Path
import os
import unittest
import numpy as np
import moderngl

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'src/VintageRTX.Client/assets/vintagertx/shaderincludes/scene-query.glsl'
VERTEX = '''#version 330 core
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}'''

def function(source, signature):
    start = source.index(signature)
    end = source.index('{', start) + 1
    depth = 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]

class SceneCoordinateTests(unittest.TestCase):
    def test_signed_coordinates_match_integer_euclidean_oracle(self):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE', '1')
        ctx = moderngl.create_standalone_context(require=330, backend='egl')
        source = SOURCE.read_text(encoding='utf-8')
        fragment = '''#version 330 core
uniform isampler2D inputs;
layout(location=0) out ivec4 result;
''' + function(source, 'int floorDiv8(') + '\n' + function(source, 'int positiveMod(') + '''
void main(){int x=texelFetch(inputs,ivec2(int(gl_FragCoord.x),0),0).r;
result=ivec4(floorDiv8(x),positiveMod(x,3),positiveMod(x,8),x);}'''
        rng = np.random.default_rng(832751)
        values = np.concatenate((np.arange(-257,258,dtype='i4'),
            np.array([-2147483648,-2147483647,2147483647,2147483646,-1000000000,1000000000],dtype='i4'),
            rng.integers(-2147483648,2147483647,size=1024,dtype='i4')))
        program = ctx.program(vertex_shader=VERTEX, fragment_shader=fragment)
        vao = ctx.vertex_array(program, [])
        inputs = ctx.texture((len(values),1),1,values.tobytes(),dtype='i4')
        output = ctx.texture((len(values),1),4,dtype='i4')
        fbo = ctx.framebuffer([output])
        try:
            inputs.use(0);program['inputs'].value=0
            fbo.use();ctx.viewport=(0,0,len(values),1);vao.render(vertices=3)
            actual=np.frombuffer(output.read(),dtype='i4').reshape(-1,4)
            expected=np.array([[int(x)//8,int(x)%3,int(x)%8,int(x)] for x in values],dtype='i4')
            np.testing.assert_array_equal(actual,expected)
        finally:
            fbo.release();output.release();inputs.release();vao.release();program.release();ctx.release()

if __name__ == '__main__':
    unittest.main()
