import inspect
import unittest

import numpy as np

from autonomous_t2t8_patch_likelihood import (
    _patch_feature,
    _pixel_x_for_coordinate,
    build_relative_map,
    candidate_events,
    command_infer,
    score_candidates,
)


class AutonomousT2T8PatchLikelihoodTests(unittest.TestCase):
    def test_projection_round_trip_for_negative_axis(self):
        x = _pixel_x_for_coordinate(-10.0, 0.0, 256, 50.0)
        center = 127.5
        projected = 0.0 - ((x - center) / (2.0 * center)) * 50.0
        self.assertAlmostEqual(projected, -10.0)

    def test_patch_feature_is_pixel_only_and_finite(self):
        image = np.zeros((256, 256), dtype=np.float64)
        image[120:124, :] = 1.0
        feature = _patch_feature(image, 128.0, 122.0)
        self.assertIsNotNone(feature)
        self.assertTrue(np.all(np.isfinite(feature)))
        self.assertGreater(len(feature), 700)

    def test_candidate_events_requires_directional_support(self):
        result = {
            "gridStepMm": 1.0,
            "records": [
                {"coordinateMm": float(index), "scoreQ75": 0.95 if index == 10 else 0.1,
                 "forwardCount": 3, "reverseCount": 0}
                for index in range(30)
            ],
        }
        candidates = candidate_events(result, 0.5)
        self.assertEqual(len(candidates), 1)
        self.assertFalse(candidates[0]["accepted"])

    def test_scoring_does_not_force_seven(self):
        candidates = [{"coordinateMm": -10.0, "accepted": True}]
        scored = score_candidates(candidates, [-10.0, 10.0])
        self.assertEqual(scored["truePositive"], 1)
        self.assertEqual(scored["falseNegative"], 1)
        self.assertFalse(scored["exactSeven"])

    def test_low_classifier_peak_is_explicitly_rejected(self):
        result = {
            "gridStepMm": 1.0,
            "records": [
                {"coordinateMm": float(index), "scoreQ75": 0.62 if index == 10 else 0.1,
                 "forwardCount": 3, "reverseCount": 3,
                 "topSupportingObservations": []}
                for index in range(30)
            ],
        }
        candidates = candidate_events(result, 0.70)
        self.assertEqual(len(candidates), 1)
        self.assertFalse(candidates[0]["accepted"])
        self.assertIn("CLASSIFIER_SCORE_BELOW_FROZEN_MINIMUM",
                      candidates[0]["rejectionReasons"])

    def test_map_emits_t7_only_for_exact_seven_accepted_events(self):
        candidates = [
            {"coordinateMm": float(index * 20), "accepted": True,
             "supportIntervalMm": [float(index * 20 - 1), float(index * 20 + 1)],
             "supportingObservations": [], "score": 0.9}
            for index in range(7)
        ]
        result = build_relative_map(candidates)
        self.assertEqual(result["predictedT7RelativeMm"], 100.0)
        candidates.append({**candidates[-1], "coordinateMm": 130.0})
        self.assertIsNone(build_relative_map(candidates)["predictedT7RelativeMm"])

    def test_actor_inference_does_not_load_evaluator_references(self):
        source = inspect.getsource(command_infer)
        self.assertNotIn("references", source)
        self.assertNotIn("score_candidates", source)


if __name__ == "__main__":
    unittest.main()
