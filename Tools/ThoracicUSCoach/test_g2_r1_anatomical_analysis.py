import unittest

import numpy as np

from g2_r1_anatomical_analysis import (
    detect_geometry_gaps,
    cluster_offset_candidates,
    match_bilateral_candidates,
    posterior_profile,
    sample_triangle_surfaces,
)


class G2R1AnatomicalAnalysisTests(unittest.TestCase):
    def test_surface_sampling_is_deterministic(self):
        vertices = np.asarray([[0.0, 1.0, 0.0], [1.0, 2.0, 0.0], [0.0, 3.0, 1.0]])
        triangles = np.asarray([[0, 1, 2]])
        sampled = sample_triangle_surfaces(vertices, triangles)
        self.assertEqual((7, 3), sampled.shape)
        np.testing.assert_allclose(sampled[-1], np.mean(vertices, axis=0))

    def test_geometry_gap_detected_without_label_input(self):
        z = np.arange(-20.0, 20.1, 0.5)
        y = 20.0 - 5.0 * np.exp(-0.5 * (z / 2.0) ** 2)
        profile = {"z": z, "smoothed_y": y}
        candidates = detect_geometry_gaps(profile, minimum_prominence_mm=2.0)
        self.assertEqual(1, len(candidates))
        self.assertAlmostEqual(0.0, candidates[0]["zMm"], places=6)
        self.assertEqual("GEOMETRY_RECESS_OR_GAP_UNRESOLVED", candidates[0]["identity"])

    def test_flat_surface_does_not_manufacture_gap(self):
        z = np.arange(-20.0, 20.1, 0.5)
        profile = {"z": z, "smoothed_y": np.full_like(z, 20.0)}
        self.assertEqual([], detect_geometry_gaps(profile))

    def test_bilateral_matching_respects_tolerance(self):
        negative = [{"zMm": -10.0}, {"zMm": 15.0}]
        positive = [{"zMm": -8.0}, {"zMm": 24.0}]
        matched = match_bilateral_candidates(negative, positive, tolerance_mm=4.0)
        self.assertEqual(1, len(matched))
        self.assertAlmostEqual(-9.0, matched[0]["centerZMm"])

    def test_multi_offset_candidate_requires_independent_support(self):
        candidates = {
            "-6": [{"zMm": 10.0, "prominenceMm": 2.0}],
            "-3": [{"zMm": 11.0, "prominenceMm": 3.0}],
            "0": [{"zMm": 9.5, "prominenceMm": 4.0}],
            "3": [{"zMm": 10.5, "prominenceMm": 2.5}],
            "6": [{"zMm": 30.0, "prominenceMm": 9.0}],
        }
        clustered = cluster_offset_candidates(candidates, minimum_offset_support=4)
        self.assertEqual(1, len(clustered))
        self.assertAlmostEqual(10.25, clustered[0]["centerZMm"])

    def test_profile_uses_posterior_maximum(self):
        samples = np.asarray(
            [[-6.0, 10.0, 0.0], [-6.0, 15.0, 0.0], [-6.0, 12.0, 1.0], [8.0, 99.0, 0.0]]
        )
        profile = posterior_profile(samples, -6.0, np.asarray([0.0, 1.0]))
        self.assertEqual(15.0, profile["surface_y"][0])
        self.assertEqual(12.0, profile["surface_y"][1])


if __name__ == "__main__":
    unittest.main()
