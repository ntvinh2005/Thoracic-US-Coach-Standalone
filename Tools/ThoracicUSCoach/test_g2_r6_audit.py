from __future__ import annotations

import unittest

import numpy as np

from g2_r6_audit import classify_stage_bc, eligibility, q_angle, visual_distances


class R6AuditRulesTests(unittest.TestCase):
    def test_off_t7_negative_is_not_decision_support(self) -> None:
        eligible, agreement = eligibility(
            score=0.40, target=False, t7=False, contact=True, workspace=True,
            hash_valid=True, floor=0.5912307500839233,
        )
        self.assertTrue(agreement)
        self.assertFalse(eligible)

    def test_unknown_t7_is_not_decision_support(self) -> None:
        eligible, _ = eligibility(
            score=0.60, target=True, t7=None, contact=True, workspace=True,
            hash_valid=True, floor=0.5912307500839233,
        )
        self.assertFalse(eligible)

    def test_both_labels_require_safety_and_hash(self) -> None:
        for target, score in ((True, 0.60), (False, 0.50)):
            for contact, workspace, hash_valid in (
                (False, True, True), (True, False, True), (True, True, False)
            ):
                eligible, agreement = eligibility(
                    score=score, target=target, t7=True, contact=contact,
                    workspace=workspace, hash_valid=hash_valid, floor=0.5912307500839233,
                )
                self.assertTrue(agreement)
                self.assertFalse(eligible)

    def test_label_floor_disagreement_fails_closed(self) -> None:
        eligible, agreement = eligibility(
            score=0.60, target=False, t7=True, contact=True, workspace=True,
            hash_valid=True, floor=0.5912307500839233,
        )
        self.assertFalse(agreement)
        self.assertFalse(eligible)

    def test_quaternion_sign_is_equivalent(self) -> None:
        self.assertAlmostEqual(q_angle([0.0, 0.0, 0.0, 1.0], [0.0, 0.0, 0.0, -1.0]), 0.0)

    def test_structural_distance_is_separate_from_rms(self) -> None:
        intensity = np.zeros((128, 128), dtype=np.float32)
        first = np.asarray([1.0, 0.0], dtype=np.float64)
        second = np.asarray([0.0, 1.0], dtype=np.float64)
        rms, structural = visual_distances((intensity, first), (intensity, second))
        self.assertEqual(rms, 0.0)
        self.assertEqual(structural, 1.0)

    def test_stage_bc_machine_verdict_fails_closed(self) -> None:
        evidence = {
            "immutableCompleteHandoff": True,
            "integrityStatus": "PASS",
            "positiveControlReproducibility": "PASS",
            "basinStability": "PASS",
            "targetIndependentReachability": "PASS",
        }
        self.assertEqual(classify_stage_bc(evidence), ("GO_R6_BOUNDED_DEVELOPMENT_CAPTURE", []))
        evidence["targetIndependentReachability"] = "FAIL"
        verdict, blockers = classify_stage_bc(evidence)
        self.assertEqual(verdict, "NO_GO")
        self.assertEqual(blockers, ["targetIndependentReachability"])


if __name__ == "__main__":
    unittest.main()
