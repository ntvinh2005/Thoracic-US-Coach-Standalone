from __future__ import annotations

import json
import unittest
from pathlib import Path

from active2d_template_stop import validate_template_checkpoint


ROOT = Path(__file__).resolve().parents[2]
CHECKPOINT = (
    ROOT
    / "Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_fixed_anatomy_template_demo_v1.json"
)
class DiagnosticTemplateStopTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.model = json.loads(CHECKPOINT.read_text(encoding="utf-8"))
        validate_template_checkpoint(cls.model)

    def test_checkpoint_is_explicitly_nondeployable_and_image_only(self) -> None:
        self.assertEqual(["cleanUltrasoundImage"], self.model["policyInputs"])
        self.assertEqual("DIAGNOSTIC_ONLY", self.model["deployment"]["status"])
        self.assertFalse(self.model["deployment"]["scientificValidation"])

    def test_consumed_calibration_groups_are_separated(self) -> None:
        calibration = self.model["calibration"]
        threshold = self.model["operatingRule"]["minimumPositiveSimilarity"]
        self.assertTrue(calibration["separable"])
        self.assertGreater(calibration["minimumPositiveSimilarity"], threshold)
        self.assertLess(calibration["maximumNegativeSimilarity"], threshold)
        self.assertEqual(0, calibration["observedFalsePositive"])
        self.assertEqual(calibration["positiveCount"], calibration["observedTruePositive"])


if __name__ == "__main__":
    unittest.main()
