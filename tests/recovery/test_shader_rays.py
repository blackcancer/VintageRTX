"""Production GLSL traversal versus an independent exhaustive ray/AABB oracle.
Linux EGL: pip install numpy moderngl; python -m unittest discover -s tests/recovery -v
This does not validate Vintage Story's real framebuffer or animation integration.
"""
from pathlib import Path
import os
import unittest
import numpy as np
import moderngl

ROOT = Path(__file__).resolve().parents[2]
SHADERS = ROOT / 'src/VintageRTX/assets/vintagertx/shaders'
VERTEX = '''#version 330 core
void main() {
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}'''


def oracle(origin, direction, occupancy, maximum):
    """Brute force occupied boxes; no grid stepping or production DDA logic."""
    best = None
    for z, y, x in np.argwhere(occupancy != 0):
        lo = np.array([x, y, z], dtype=np.float64) / 4
        hi = lo + 0.25
        enter, leave, axis = -np.inf, np.inf, 0
        possible = True
        for k in range(3):
            if abs(direction[k]) < 1e-20:
                if not lo[k] <= origin[k] < hi[k]:
                    possible = False
                    break
                continue
            t0 = (lo[k] - origin[k]) / direction[k]
            t1 = (hi[k] - origin[k]) / direction[k]
            if min(t0, t1) > enter:
                enter, axis = min(t0, t1), k
            leave = min(leave, max(t0, t1))
        hit = max(enter, 0)
        if possible and leave > hit and hit < maximum and (best is None or hit < best[0]):
            normal = np.zeros(3)
            normal[axis] = -np.sign(direction[axis])
            best = hit, normal
    return best


class ShaderRayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault('LIBGL_ALWAYS_SOFTWARE', '1')
        cls.ctx = moderngl.create_standalone_context(require=330, backend='egl')
        cls.shader = (SHADERS / 'display.frag').read_text(encoding='utf-8')
        start = cls.shader.index('bool traceFineBlockHit(')
        end = cls.shader.index('float traceVoxelVisibility(', start)
        kernel = cls.shader[start:end]
        fragment = '''#version 330 core
uniform sampler3D voxelVolume;
uniform sampler3D voxelOccupancy;
uniform vec3 voxelOrigin;
uniform vec3 voxelSize;
uniform float occupancyScale;
uniform sampler2D rayOrigins;
uniform sampler2D rayDirections;
uniform float maximumDistance;
uniform int maximumSteps;
const int MAX_VOXEL_STEPS = 128;
layout(location=0) out vec4 result;
layout(location=1) out vec4 normalResult;
''' + kernel + '''
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    vec3 origin = texelFetch(rayOrigins, p, 0).xyz;
    vec3 direction = texelFetch(rayDirections, p, 0).xyz;
    float distance; vec3 normal; vec4 material;
    int status = traceVoxelSurface(origin, direction, maximumDistance, maximumSteps,
        distance, normal, material);
    result = vec4(float(status), distance, 0.0, 1.0);
    normalResult = vec4(normal, 1.0);
}'''
        cls.program = cls.ctx.program(vertex_shader=VERTEX, fragment_shader=fragment)
        cls.vao = cls.ctx.vertex_array(cls.program, [])
        print('GL renderer:', cls.ctx.info['GL_RENDERER'])

    @classmethod
    def tearDownClass(cls):
        cls.vao.release()
        cls.program.release()
        cls.ctx.release()

    def trace(self, origins, directions, occupancy, maximum=6.0, steps=128):
        origins = np.asarray(origins, dtype='f4')
        directions = np.asarray(directions, dtype='f4')
        count = len(origins)
        material = np.zeros((4, 4, 4, 4), dtype='u1')
        for z, y, x in np.argwhere(occupancy):
            material[z // 4, y // 4, x // 4] = [128, 128, 128, 128]
        textures = [
            self.ctx.texture3d((4, 4, 4), 4, material.tobytes(), dtype='f1'),
            self.ctx.texture3d((16, 16, 16), 1, occupancy.tobytes(), dtype='f1'),
            self.ctx.texture((count, 1), 3, origins.tobytes(), dtype='f4'),
            self.ctx.texture((count, 1), 3, directions.tobytes(), dtype='f4')]
        targets = [self.ctx.texture((count, 1), 4, dtype='f4') for _ in range(2)]
        fbo = self.ctx.framebuffer(targets)
        try:
            for i, (name, texture) in enumerate(zip(
                ['voxelVolume', 'voxelOccupancy', 'rayOrigins', 'rayDirections'], textures)):
                texture.use(i)
                self.program[name].value = i
            self.program['voxelOrigin'].value = (0, 0, 0)
            self.program['voxelSize'].value = (4, 4, 4)
            self.program['occupancyScale'].value = 4
            self.program['maximumDistance'].value = maximum
            self.program['maximumSteps'].value = steps
            fbo.use()
            self.ctx.viewport = (0, 0, count, 1)
            self.vao.render(vertices=3)
            return tuple(np.frombuffer(t.read(), dtype='f4').reshape(count, 4).copy() for t in targets)
        finally:
            fbo.release()
            for texture in textures + targets: texture.release()

    def test_random_rays_against_exhaustive_boxes(self):
        rng = np.random.default_rng(18473)
        occupancy = (rng.random((16, 16, 16)) < 0.065).astype('u1') * 255
        origins = rng.uniform(0.02, 3.98, (256, 3)).astype('f4')
        directions = rng.normal(size=(256, 3))
        directions = (directions / np.linalg.norm(directions, axis=1)[:, None]).astype('f4')
        results, normals = self.trace(origins, directions, occupancy)
        for i, (origin, direction) in enumerate(zip(origins, directions)):
            expected = oracle(origin.astype('f8'), direction.astype('f8'), occupancy, 6.0)
            with self.subTest(ray=i, origin=origin.tolist(), direction=direction.tolist()):
                if expected is None:
                    self.assertNotEqual(results[i, 0], 1)
                else:
                    distance, normal = expected
                    self.assertEqual(results[i, 0], 1)
                    self.assertAlmostEqual(results[i, 1], distance, delta=0.00004)
                    if distance > 0.0001:
                        np.testing.assert_allclose(normals[i, :3], normal, atol=0.00001)

    def test_axis_parallel_thin_shape_and_exact_entry(self):
        occupancy = np.zeros((16, 16, 16), dtype='u1')
        occupancy[6, 6, 11] = 255
        results, normals = self.trace([[0.125, 1.625, 1.625]], [[1, 0, 0]], occupancy)
        self.assertEqual(results[0, 0], 1)
        self.assertAlmostEqual(results[0, 1], 2.625, places=5)
        np.testing.assert_array_equal(normals[0, :3], [-1, 0, 0])

    def test_clear_budget_and_outside_are_distinct(self):
        empty = np.zeros((16, 16, 16), dtype='u1')
        clear, _ = self.trace([[1.5, 1.5, 1.5]], [[1, 0, 0]], empty, maximum=0.1)
        exhausted, _ = self.trace([[1.5, 1.5, 1.5]], [[1, 0, 0]], empty, steps=0)
        outside, _ = self.trace([[-0.1, 1.5, 1.5]], [[1, 0, 0]], empty)
        self.assertEqual((clear[0, 0], exhausted[0, 0], outside[0, 0]), (0, 3, 2))

    def test_full_production_shader_links(self):
        program = self.ctx.program(vertex_shader=(SHADERS / 'display.vert').read_text(encoding='utf-8'),
            fragment_shader=self.shader)
        self.assertIn('inverseEntityMirrorProjection', program)
        program.release()


if __name__ == '__main__':
    unittest.main()
