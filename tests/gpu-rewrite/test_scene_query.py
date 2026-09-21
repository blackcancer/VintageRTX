"""Execute the production regional shader on packets exported by the actual C# packer.
These are hidden-context geometric tests, not claims about in-game lighting or GPU performance.
"""
from pathlib import Path
import json
import os
import unittest
import numpy as np
import moderngl

ROOT = Path(__file__).resolve().parents[2]
VERTEX = '''#version 330 core
void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}'''
WRAPPER = '''
uniform sampler2D testRays;
layout(location=0) out vec4 outputQuery;
layout(location=1) out vec4 outputNormal;
void main(){int x=int(gl_FragCoord.x);vec4 a=texelFetch(testRays,ivec2(x,0),0);
vec4 b=texelFetch(testRays,ivec2(x,1),0);int budget=int(texelFetch(testRays,ivec2(x,2),0).x);
SceneQuery q=traceScene(a.xyz,b.xyz,a.w,b.w,budget);
outputQuery=vec4(float(q.status),q.distance,float(q.primitive),float(q.visited));outputNormal=vec4(q.normal,1);}
'''

class SceneQueryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE','1')
        cls.ctx=moderngl.create_standalone_context(require=330,backend='egl')
        cls.shader=(ROOT/'src/VintageRTX.Client/assets/vintagertx/shaderincludes/scene-query.glsl').read_text()
        cls.cases=json.loads(Path(os.environ['VINTAGERTX_GPU_CASES']).read_text())
        print('GPU query renderer:',cls.ctx.info['GL_RENDERER'])
    @classmethod
    def tearDownClass(cls): cls.ctx.release()
    def run_case(self,case,source):
        resources=[]
        try:
            program=self.ctx.program(vertex_shader=VERTEX,fragment_shader='#version 330 core\n'+source+WRAPPER);resources.append(program)
            vao=self.ctx.vertex_array(program,[]);resources.append(vao)
            n=len(case['Rays']); rays=np.zeros((3,n,4),dtype='f4')
            for i,r in enumerate(case['Rays']): rays[0,i]=r['Origin'];rays[1,i]=r['Direction'];rays[2,i,0]=r['Budget']
            for unit,(name,shape,dtype,values) in enumerate([
                ('regionData',(27,1),'i4',case['Regions']),('cellData',(64,216),'i4',case['Cells']),
                ('geometryData',(256,case['GeometryHeight']),'f4',case['Geometry']),('testRays',(n,3),'f4',rays)]):
                tex=self.ctx.texture(shape,4,np.asarray(values,dtype=dtype).tobytes(),dtype=dtype);resources.append(tex)
                tex.filter=(moderngl.NEAREST,moderngl.NEAREST);tex.repeat_x=False;tex.repeat_y=False;tex.use(unit);program[name].value=unit
            program['sceneAnchor'].value=tuple(case['Anchor'])
            out0=self.ctx.texture((n,1),4,dtype='f4');out1=self.ctx.texture((n,1),4,dtype='f4');resources.extend([out0,out1])
            target=self.ctx.framebuffer([out0,out1]);resources.append(target);target.use();self.ctx.viewport=(0,0,n,1);vao.render(vertices=3)
            return np.frombuffer(out0.read(),dtype='f4').reshape(n,4).copy(),np.frombuffer(out1.read(),dtype='f4').reshape(n,4).copy()
        finally:
            for resource in reversed(resources):resource.release()
    def test_production_packets_match_cpu_geometry_and_coverage(self):
        for case in self.cases:
            with self.subTest(case=case['Name']):
                output,normals=self.run_case(case,self.shader)
                self.assertTrue(np.isfinite(output).all());self.assertTrue(np.isfinite(normals).all())
                expected=np.array([r['Status'] for r in case['Rays']],dtype='i4')
                np.testing.assert_array_equal(output[:,0].astype('i4'),expected)
                np.testing.assert_allclose(output[:,1],[r['Distance'] for r in case['Rays']],atol=2e-5,rtol=2e-5)
                hits=expected==1
                np.testing.assert_array_equal(output[hits,2].astype('i4'),np.array([r['Primitive'] for r in case['Rays']])[hits])
                np.testing.assert_allclose(normals[hits,:3],np.array([r['Normal'] for r in case['Rays']])[hits],atol=2e-5,rtol=2e-5)
    def test_known_wrong_unknown_as_clear_mutation_is_detected(self):
        anchor='if(data.x==0) return SceneQuery(2,entered,vec3(0),-1,stepIndex+1);'
        self.assertEqual(self.shader.count(anchor),1)
        mutant=self.shader.replace(anchor,anchor.replace('SceneQuery(2,','SceneQuery(0,'))
        case=next(c for c in self.cases if c['Name']=='ring-tag-reuse')
        output,_=self.run_case(case,mutant)
        self.assertTrue(any(int(value)!=r['Status'] for value,r in zip(output[:,0],case['Rays'])))
    def test_exact_ties_cannot_be_replaced_by_single_axis_advancement(self):
        anchor='if(boundary[axis]<=next)cell[axis]+=int(sign(direction[axis]));'
        self.assertEqual(self.shader.count(anchor),1)
        mutant=self.shader.replace(anchor,'if(axis==0 && boundary[axis]<=next)cell[axis]+=int(sign(direction[axis]));')
        case=next(c for c in self.cases if c['Name']=='hierarchy')
        output,_=self.run_case(case,mutant)
        self.assertTrue(any(int(value)!=r['Status'] for value,r in zip(output[:,0],case['Rays'])))

if __name__=='__main__': unittest.main()
