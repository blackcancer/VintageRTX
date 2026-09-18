"""Exact edge/corner witnesses run the production GLSL DDA, not a source-string count."""
from pathlib import Path
import sys
import unittest
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'recovery'))
import test_shader_rays as rays


class GridTieRegressionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        rays.ShaderRayTests.setUpClass()
        cls.fixture = rays.ShaderRayTests()

    @classmethod
    def tearDownClass(cls):
        rays.ShaderRayTests.tearDownClass()

    def test_zero_width_neighbours_do_not_occlude_diagonal_rays(self):
        cases = [
            ([.125, .125, .125], [1, 1, 1], (4, 3, 4), (4, 4, 4)),
            ([.125, .125, .375], [1, 1, 0], (1, 3, 4), (1, 4, 4)),
            ([3.875, 3.875, 3.875], [-1, -1, -1], (11, 12, 11), (11, 11, 11)),
            ([3.875, 3.875, .375], [-1, -1, 0], (1, 12, 11), (1, 11, 11)),
        ]
        for origin, direction, touched_only, entered in cases:
            origin = np.array(origin, dtype='f4')
            direction = np.array(direction, dtype='f4')
            direction /= np.linalg.norm(direction)
            occupied = np.zeros((16, 16, 16), dtype='u1')
            occupied[touched_only] = 255
            with self.subTest(origin=origin.tolist(), direction=direction.tolist()):
                self.assertIsNone(rays.oracle(origin.astype('f8'), direction.astype('f8'), occupied, 6))
                result, _ = self.fixture.trace([origin], [direction], occupied)
                self.assertNotEqual(result[0, 0], 1, 'A zero-length corner contact became occupied volume')
                occupied[entered] = 255
                expected = rays.oracle(origin.astype('f8'), direction.astype('f8'), occupied, 6)
                self.assertIsNotNone(expected)
                result, normals = self.fixture.trace([origin], [direction], occupied)
                self.assertEqual(result[0, 0], 1)
                self.assertAlmostEqual(result[0, 1], expected[0], delta=.00004)
                np.testing.assert_allclose(normals[0, :3], expected[1], atol=.00001)


if __name__ == '__main__':
    unittest.main()
