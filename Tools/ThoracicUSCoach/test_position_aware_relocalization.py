from __future__ import annotations

import unittest

import numpy as np

from position_aware_relocalization import (
    apply_pose_perturbation,
    assert_no_restricted_keys,
    block_ssim,
    confirmation_threshold,
    pose_distance_mm,
    quaternion_angle_deg,
)


class PositionAwareRelocalizationTests(unittest.TestCase):
    def test_identical_image_has_unit_structural_similarity(self) -> None:
        image = np.linspace(0.0, 1.0, 64 * 64).reshape(64, 64)
        self.assertAlmostEqual(block_ssim(image, image), 1.0, places=12)

    def test_different_structure_reduces_similarity(self) -> None:
        left = np.zeros((64, 64), dtype=np.float64)
        right = np.zeros((64, 64), dtype=np.float64)
        left[:, :32] = 1.0
        right[:32, :] = 1.0
        self.assertLess(block_ssim(left, right), 0.51)

    def test_pose_distance_combines_translation_and_orientation(self) -> None:
        position = np.zeros(3)
        rotation = np.asarray([0.0, 0.0, 0.0, 1.0])
        yaw = np.asarray([0.0, np.sin(np.deg2rad(2.0) / 2), 0.0, np.cos(np.deg2rad(2.0) / 2)])
        self.assertAlmostEqual(quaternion_angle_deg(rotation, yaw), 2.0, places=6)
        self.assertAlmostEqual(pose_distance_mm(position, rotation, np.asarray([3.0, 0.0, 0.0]), yaw),
                               np.sqrt(10.0), places=6)

    def test_pose_perturbation_is_target_independent(self) -> None:
        position, rotation = apply_pose_perturbation(
            np.asarray([1.0, 2.0, 3.0]), np.asarray([0.0, 0.0, 0.0, 1.0]),
            np.asarray([0.0, 0.0, 1.0]), 3.0, 2.0)
        np.testing.assert_allclose(position, [1.0, 2.0, 6.0])
        self.assertAlmostEqual(np.linalg.norm(rotation), 1.0)

    def test_confirmation_threshold_is_bounded_and_control_derived(self) -> None:
        self.assertAlmostEqual(confirmation_threshold([0.92, 0.93, 0.94, 0.95, 0.96]), 0.902)
        self.assertEqual(confirmation_threshold([0.99] * 5), 0.97)

    def test_relocalization_payload_rejects_anatomical_ground_truth(self) -> None:
        assert_no_restricted_keys({"robotPositionMm": [0, 0, 0], "image": "frame.png"})
        with self.assertRaises(ValueError):
            assert_no_restricted_keys({"t7TargetCoordinate": 10.0})
        with self.assertRaises(ValueError):
            assert_no_restricted_keys({"rendererProvenance": "Skeleton"})


if __name__ == "__main__":
    unittest.main()
