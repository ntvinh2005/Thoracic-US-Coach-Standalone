from __future__ import annotations

import unittest

from analyze_g2_r4_p1 import distance, quaternion_angle, quantiles


class G2R4P1AnalysisTests(unittest.TestCase):
    def test_pose_distance_and_rotation(self) -> None:
        origin = {"x": 0.0, "y": 0.0, "z": 0.0}
        point = {"x": 3.0, "y": 4.0, "z": 0.0}
        self.assertAlmostEqual(5.0, distance(origin, point))
        identity = {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}
        half_turn = {"x": 0.0, "y": 1.0, "z": 0.0, "w": 0.0}
        self.assertAlmostEqual(0.0, quaternion_angle(identity, identity))
        self.assertAlmostEqual(180.0, quaternion_angle(identity, half_turn))

    def test_quantiles_empty_and_ordered(self) -> None:
        self.assertEqual(0, quantiles([])["count"])
        result = quantiles([3.0, 1.0, 2.0])
        self.assertEqual(1.0, result["min"])
        self.assertEqual(2.0, result["median"])
        self.assertEqual(3.0, result["max"])


if __name__ == "__main__":
    unittest.main()
