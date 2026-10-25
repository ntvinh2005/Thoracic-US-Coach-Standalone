from __future__ import annotations

import unittest

from analyze_g2_r3_a2 import derive_verdict


def base_inputs() -> dict[str, object]:
    return {
        "validCases": 16,
        "mandatoryValid": True,
        "measurementConsistencyFailed": False,
        "separatedFraction": 0.8,
        "frozenFreshRecall": 0.5,
        "frozenFreshFalsePositive": 0,
        "ambiguousPairs": 2,
        "ambiguousFraction": 0.2,
        "partialEvidencePairs": 0,
        "privilegedDifferenceFractionMedian": 0.9,
        "causalHistoryAPGain": 0.05,
        "causalResolvedFraction": 1.0,
    }


class G2R3A2DecisionTests(unittest.TestCase):
    def test_actual_shape_remains_inconclusive(self) -> None:
        verdict, _ = derive_verdict(base_inputs())
        self.assertEqual(verdict, "INCONCLUSIVE")

    def test_go_requires_visual_and_frozen_model_gate(self) -> None:
        values = base_inputs()
        values["frozenFreshRecall"] = 0.8
        verdict, _ = derive_verdict(values)
        self.assertEqual(verdict, "GO_FOR_IMAGE_ONLY_RESEARCH")

    def test_partial_requires_two_evidence_pairs(self) -> None:
        values = base_inputs()
        values["partialEvidencePairs"] = 2
        verdict, _ = derive_verdict(values)
        self.assertEqual(verdict, "PARTIALLY_OBSERVABLE_WITH_EVIDENCE")

    def test_invalid_mandatory_pose_forces_inconclusive(self) -> None:
        values = base_inputs()
        values["mandatoryValid"] = False
        values["frozenFreshRecall"] = 1.0
        verdict, _ = derive_verdict(values)
        self.assertEqual(verdict, "INCONCLUSIVE")


if __name__ == "__main__":
    unittest.main()
