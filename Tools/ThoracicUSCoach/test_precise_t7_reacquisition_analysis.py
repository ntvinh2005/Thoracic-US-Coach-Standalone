import unittest

from precise_t7_reacquisition_analysis import classify_outcome


class PreciseT7ReacquisitionAnalysisTests(unittest.TestCase):
    def test_all_gates_are_required_for_pass(self):
        confirmation, overall = classify_outcome(True, True, True, True)
        self.assertEqual("PASS", confirmation)
        self.assertEqual("P1_R2_REACQUISITION_PASS", overall)

    def test_precise_pose_with_failed_ssim_is_not_retried_or_called_pass(self):
        confirmation, overall = classify_outcome(True, True, False, True)
        self.assertEqual("FAIL", confirmation)
        self.assertEqual("POSE_PRECISE_IMAGE_MISMATCH", overall)

    def test_position_precision_failure_remains_distinct(self):
        confirmation, overall = classify_outcome(False, True, True, True)
        self.assertEqual("PASS", confirmation)
        self.assertEqual("POSITION_PRECISION_FAIL", overall)

    def test_integrity_and_evaluator_failures_are_distinct(self):
        self.assertEqual("IMAGE_INTEGRITY_FAIL", classify_outcome(True, False, True, True)[1])
        self.assertEqual("EVALUATOR_T7_MISMATCH", classify_outcome(True, True, True, False)[1])


if __name__ == "__main__":
    unittest.main()
