import unittest

import numpy as np

from relative_spine_e0_e1_analysis import (
    causal_contribution,
    difference_metrics,
    interface_summary,
)


class RelativeSpineE0E1AnalysisTests(unittest.TestCase):
    def test_identical_images_are_not_causal(self):
        image = np.zeros((32, 32), dtype=np.float64)
        metrics = difference_metrics(image, image.copy())
        self.assertFalse(causal_contribution(metrics, metrics))

    def test_counterfactual_must_exceed_noise_and_coverage(self):
        noise = {
            "meanAbsoluteDifference": 0.001,
            "changedPixelFractionAt2Levels": 0.5,
        }
        weak = {
            "meanAbsoluteDifference": 0.0025,
            "changedPixelFractionAt2Levels": 0.5,
        }
        supported = {
            "meanAbsoluteDifference": 0.01,
            "changedPixelFractionAt2Levels": 0.2,
        }
        self.assertFalse(causal_contribution(weak, noise))
        self.assertTrue(causal_contribution(supported, noise))

    def test_full_interfaces_distinguish_reached_and_occluded(self):
        beams = [{
            "hasShadowInterface": True,
            "firstShadowObject": "Skeleton",
            "interfaces": [
                {"objectName": "LF T7", "enteringMaterial": True,
                 "reachedBeforeOrAtFirstShadow": True, "occludedByEarlierShadow": False},
                {"objectName": "Skeleton", "enteringMaterial": True,
                 "reachedBeforeOrAtFirstShadow": True, "occludedByEarlierShadow": False},
                {"objectName": "Pleura", "enteringMaterial": True,
                 "reachedBeforeOrAtFirstShadow": False, "occludedByEarlierShadow": True},
            ],
        }]
        result = interface_summary(beams, "LF T7")
        self.assertEqual(1.0, result["combinedSkeletonFirstShadowBeamFraction"])
        self.assertEqual(1.0, result["expectedLevelObjectReachedBeamFraction"])
        self.assertEqual(0.0, result["expectedLevelObjectOccludedBeamFraction"])
        self.assertEqual(1, result["occludedObjectCounts"]["Pleura"])


if __name__ == "__main__":
    unittest.main()
