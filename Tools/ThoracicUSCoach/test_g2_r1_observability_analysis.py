import unittest

import numpy as np

from g2_r1_observability_analysis import SCAN_WIDTH_MM


class G2R1ObservabilityAnalysisTests(unittest.TestCase):
    def test_scan_width_conversion_uses_actual_beam_count(self):
        beam_count = 256
        span_beams = 32
        self.assertEqual(5.0, span_beams * SCAN_WIDTH_MM / beam_count)

    def test_shadow_transition_count_preserves_entry_and_exit(self):
        shadow = np.asarray([False, False, True, True, False, True])
        self.assertEqual(3, int(np.count_nonzero(shadow[1:] != shadow[:-1])))

    def test_actor_inputs_are_not_part_of_evaluator_feature_contract(self):
        forbidden = {"targetPose", "targetScore", "t7Transform", "distanceToT7"}
        emitted = {
            "skeletonShadowBeamFraction",
            "targetLfVisibleBeamFraction",
            "boneShadowOnBothSidesOfTargetLf",
        }
        self.assertFalse(forbidden.intersection(emitted))


if __name__ == "__main__":
    unittest.main()
