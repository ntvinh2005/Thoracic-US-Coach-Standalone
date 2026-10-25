from __future__ import annotations

import json
import unittest
from pathlib import Path


class G2R4P1ProtocolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = Path(__file__).resolve().parents[2]
        path = cls.root / "Tools/ThoracicUSCoach/experiments/g2_r4_p1_capture_v1.json"
        cls.manifest = json.loads(path.read_text(encoding="utf-8"))

    def test_budgets_and_arms(self) -> None:
        cases = self.manifest["cases"]
        independent = [item for item in cases if item["countsAsNewIndependentFamily"]]
        self.assertEqual(16, len(independent))
        self.assertEqual(8, sum(item["arm"] == "randomized_start" for item in independent))
        self.assertEqual(8, sum(item["arm"] == "target_enriched" for item in independent))
        self.assertTrue(all(len(item["trajectoryActions"]) + 1 <= 121 for item in independent))
        hard = [item for item in cases if item["arm"] == "hard_causal"]
        self.assertEqual(56, sum(len(item["trajectoryActions"]) + 1 for item in hard))

    def test_identity_and_role_separation(self) -> None:
        cases = self.manifest["cases"]
        self.assertEqual(len(cases), len({item["caseId"] for item in cases}))
        self.assertEqual(len(cases), len({item["familyId"] for item in cases}))
        self.assertFalse({item["familyId"] for item in cases}
                         & set(self.manifest["historicallyLockedFamilies"]))
        self.assertIn("No pilot family is assigned", self.manifest["predeclaration"]["rolePolicy"])

    def test_hard_causal_is_genuine_and_excluded_from_support(self) -> None:
        hard = [item for item in self.manifest["cases"] if item["arm"] == "hard_causal"]
        references = " ".join(item["planningReference"] for item in hard)
        self.assertIn("0090", references)
        self.assertIn("0066", references)
        self.assertTrue(all(not item["countsAsNewIndependentFamily"] for item in hard))
        for item in hard:
            actions = {action.lower() for action in item["trajectoryActions"]}
            self.assertTrue({"lateral", "medial", "rotateclockwise", "rotatecounterclockwise"} <= actions)

    def test_frozen_target_and_no_actor_privilege(self) -> None:
        self.assertAlmostEqual(0.5912307500839233, self.manifest["acceptableScoreFloor"])
        serialized = json.dumps(self.manifest["cases"])
        for forbidden in ("teacherAction", "targetDirection", "modelInput", "oracleAction"):
            self.assertNotIn(forbidden, serialized)


if __name__ == "__main__":
    unittest.main()
