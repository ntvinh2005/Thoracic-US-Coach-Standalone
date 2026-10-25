from __future__ import annotations

import json
from pathlib import Path
import unittest

from g2_r4_p2_lineage import normalized_action
from train_g2_r4_p2 import ImageStore


ROOT = Path(__file__).resolve().parents[2]
PROTOCOL = ROOT / "Tools/ThoracicUSCoach/experiments/g2_r4_p2_protocol_v1.json"
OUTPUT = ROOT / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2_20261025"


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


class G2R4P2Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.protocol = load(PROTOCOL)
        cls.lineage = load(OUTPUT / "p2_a_lineage_graph.json")
        cls.results = load(OUTPUT / "p2_cd_development_results.json")
        cls.checkpoint = load(OUTPUT / "g2_r4_p2_candidate.json")

    def test_protocol_was_frozen_before_training(self) -> None:
        self.assertTrue(self.protocol["frozenBeforeTraining"])
        self.assertEqual([13, 29, 47], self.protocol["training"]["seeds"])
        self.assertEqual(0.5912307500839233, self.protocol["acceptableScoreFloor"])
        self.assertEqual(0.93, self.results["frozenBaseline"]["threshold"])

    def test_integrity_and_image_hashes(self) -> None:
        integrity = self.lineage["integrity"]
        self.assertEqual("PASS", integrity["status"])
        self.assertEqual([], integrity["errors"])
        self.assertEqual(1120, integrity["allP1FramesValidated"])
        self.assertEqual(1120, integrity["uniqueImageHashes"])
        self.assertEqual(0, integrity["unsafeFrames"])

    def test_independence_levels_are_not_conflated(self) -> None:
        support = self.lineage["independence"]
        self.assertEqual(1064, support["frameLevel"])
        self.assertEqual(16, support["trajectoryLevel"])
        self.assertEqual(12, support["sourceBaseLevel"])
        self.assertEqual(11, support["sourceBaseSpatialGroupLevel"])
        self.assertEqual(5, support["positiveSourceBaseSpatialGroups"])

    def test_overlapping_0052_and_0058_are_same_spatial_group(self) -> None:
        groups = self.lineage["spatialGroupSources"]
        matching = [set(sources) for sources in groups.values() if "train_family_0052" in sources]
        self.assertEqual(1, len(matching))
        self.assertIn("dev_family_0058", matching[0])

    def test_fold_groups_are_disjoint_and_cover_once(self) -> None:
        all_eval: list[str] = []
        universe = set(self.lineage["positiveSpatialGroups"]) | set(self.lineage["negativeSpatialGroups"])
        for fold in self.lineage["developmentFolds"]:
            evaluation = set(fold["evaluationSpatialGroups"])
            training = set(fold["trainingSpatialGroups"])
            self.assertFalse(evaluation & training)
            self.assertEqual(universe, evaluation | training)
            all_eval.extend(evaluation)
        self.assertEqual(len(universe), len(all_eval))
        self.assertEqual(universe, set(all_eval))

    def test_rejected_and_incomplete_actions_are_reset(self) -> None:
        base = {"issuedAction": "LATERAL", "episodeReset": False}
        self.assertEqual("RESET", normalized_action({**base, "actionAccepted": False, "actionCompleted": False}))
        self.assertEqual("RESET", normalized_action({**base, "actionAccepted": True, "actionCompleted": False}))
        self.assertEqual("LATERAL", normalized_action({**base, "actionAccepted": True, "actionCompleted": True}))

    def test_causal_history_never_uses_future_or_crosses_episode(self) -> None:
        records = [
            {"caseId": "a", "index": 0}, {"caseId": "a", "index": 1},
            {"caseId": "a", "index": 2}, {"caseId": "b", "index": 0},
        ]
        history = ImageStore._history_indices(records, 4)
        self.assertEqual([0, 0, 0, 0], history[0].tolist())
        self.assertEqual([0, 0, 0, 1], history[1].tolist())
        self.assertEqual([0, 0, 1, 2], history[2].tolist())
        self.assertEqual([3, 3, 3, 3], history[3].tolist())

    def test_image_only_checkpoint_and_fail_closed_deployment(self) -> None:
        self.assertEqual(
            ["cleanUltrasoundImage", "policyOwnedMemory", "actuallyExecutedActionFeedback"],
            self.checkpoint["policyInputs"],
        )
        serialized = json.dumps(self.checkpoint["policyInputs"]).lower()
        for forbidden in ("pose", "geometry", "targetscore", "anatomy"):
            self.assertNotIn(forbidden, serialized)
        self.assertEqual("BLOCKED", self.checkpoint["deployment"]["status"])
        self.assertFalse(self.checkpoint["deployment"]["safeOperatingPoint"])
        self.assertFalse(self.checkpoint["developmentEligibleForConfirmation"])

    def test_all_declared_seed_fold_results_exist(self) -> None:
        for model in ("structural_v4_equivalent", "cnn_single_frame", "cnn_causal_multiview_h4"):
            rows = self.results["foldRuns"][model]
            self.assertEqual(9, len(rows))
            self.assertEqual({13, 29, 47}, {row["seed"] for row in rows})
            self.assertEqual({0, 1, 2}, {row["fold"] for row in rows})

    def test_confirmation_isolation_and_single_use(self) -> None:
        confirmation = self.protocol["confirmation"]
        self.assertEqual(16, confirmation["maximumGroups"])
        self.assertEqual(81, confirmation["maximumObservationsPerGroup"])
        self.assertEqual(8, len(confirmation["randomizedSeeds"]))
        self.assertEqual(8, len(confirmation["targetEnrichedSeeds"]))
        self.assertFalse((OUTPUT / "p2_e_confirmation_results.json").exists())
        self.assertFalse(self.results["selection"]["eligibleForIndependentConfirmation"])

    def test_historical_locked_test_was_not_opened(self) -> None:
        self.assertEqual([], self.lineage["historicalLockedFamiliesOpened"])
        self.assertEqual([], self.results["lockedFamiliesOpened"])


if __name__ == "__main__":
    unittest.main()
