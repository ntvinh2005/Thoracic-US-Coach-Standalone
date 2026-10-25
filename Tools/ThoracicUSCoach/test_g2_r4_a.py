from __future__ import annotations

import inspect
import json
import unittest
from pathlib import Path

from run_g2_r4_a import SmallUltrasoundCNN, decide_stage_a


def passing_inputs() -> dict[str, object]:
    return {
        "integrityErrors": [],
        "medianStableMemorizationAccuracy": 1.0,
        "medianSamePoseStableRecall": 0.9,
        "medianSamePoseStableFalsePositiveRate": 0.0,
        "medianCnnCrossFamilyBalancedAccuracy": 0.85,
        "medianCnnCrossFamilyRecall": 0.8,
        "medianCnnCrossFamilyFalsePositiveRate": 0.1,
        "medianStructuralCrossFamilyBalancedAccuracy": 0.7,
        "frozenSpatialBalancedAccuracy": 0.75,
        "requiredBalancedAccuracy": 0.8,
    }


class G2R4ATests(unittest.TestCase):
    def test_decision_is_derived_not_fixed(self) -> None:
        self.assertEqual(decide_stage_a(passing_inputs())[0], "PASS")
        values = passing_inputs()
        values["medianCnnCrossFamilyRecall"] = 0.4
        self.assertEqual(decide_stage_a(values)[0], "INCONCLUSIVE")
        values["medianStableMemorizationAccuracy"] = 0.5
        self.assertEqual(decide_stage_a(values)[0], "FAIL")

    def test_cnn_forward_accepts_only_image_tensor(self) -> None:
        parameters = list(inspect.signature(SmallUltrasoundCNN.forward).parameters)
        self.assertEqual(parameters, ["self", "values"])

    def test_protocol_forbids_privileged_inputs_and_locked_test(self) -> None:
        protocol_path = Path(__file__).resolve().parent / "experiments" / "g2_r4_a_representation_v1.json"
        protocol = json.loads(protocol_path.read_text(encoding="utf-8"))
        self.assertEqual(protocol["input"]["privilegedActorInputs"], [])
        self.assertIn("locked-test access", protocol["forbidden"])
        self.assertEqual(protocol["decisionProcedure"]["dependency"], "R4-B is allowed only after R4-A PASS")

    def test_cross_family_folds_are_disjoint(self) -> None:
        protocol_path = Path(__file__).resolve().parent / "experiments" / "g2_r4_a_representation_v1.json"
        protocol = json.loads(protocol_path.read_text(encoding="utf-8"))
        groups = [set(item["evaluationFamilies"]) for item in protocol["crossFamilyFolds"]]
        self.assertFalse(groups[0] & groups[1])
        self.assertFalse(groups[0] & groups[2])
        self.assertFalse(groups[1] & groups[2])


if __name__ == "__main__":
    unittest.main()
