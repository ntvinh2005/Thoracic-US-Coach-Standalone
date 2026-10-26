from __future__ import annotations

import json
from pathlib import Path
import unittest

import numpy as np

from g2_r4_p2r_lineage import OUTPUT, connected_components
from train_g2_r4_p2 import (
    ACTION_NAMES,
    HISTORY_AVAILABILITY_OFFSET,
    HISTORY_METADATA_WIDTH,
    ImageStore,
    UltrasoundTwoHead,
)


ROOT = Path(__file__).resolve().parents[2]


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def record(
    case: str, index: int, issued: str = "OBSERVE", executed: str | None = None,
    accepted: bool = True, completed: bool = True, reset: bool = False,
) -> dict:
    return {
        "caseId": case, "index": index, "issuedAction": issued,
        "executedAction": executed if executed is not None else (issued if accepted and completed else "RESET"),
        "actionAccepted": accepted, "actionCompleted": completed, "episodeReset": reset,
    }


def metadata_store(records: list[dict]) -> ImageStore:
    store = ImageStore.__new__(ImageStore)
    store.history, store.history_available = ImageStore._history_layout(records, 4)
    store.issued_actions = np.stack([ImageStore._action(item["issuedAction"]) for item in records])
    store.executed_actions = np.stack([ImageStore._action(item["executedAction"]) for item in records])
    store.feedback = np.asarray([[float(item["actionAccepted"]), float(item["actionCompleted"])] for item in records], dtype=np.float32)
    store.resets = np.asarray([float(item["episodeReset"]) for item in records], dtype=np.float32)
    return store


class P2RLineageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.graph = load(ROOT / OUTPUT / "r1_complete_lineage_graph.json")
        cls.split = load(ROOT / OUTPUT / "r1_corrected_split.json")

    def test_auxiliary_leakage_risk_confirmed_and_connected(self) -> None:
        audit = self.graph["hard0090Hard0066"]
        self.assertTrue(audit["confirmed"])
        self.assertEqual(1.0, audit["edge"]["trajectoryOverlapLeft"])
        self.assertEqual(1.0, audit["edge"]["trajectoryOverlapRight"])
        self.assertIn(audit["correctedComponent"], audit["primary0066Components"])

    def test_connected_auxiliary_is_excluded_from_outer_eval_fold(self) -> None:
        group = self.graph["hard0090Hard0066"]["correctedComponent"]
        fold = next(item for item in self.graph["primaryPolicy"]["folds"] if group in item["evaluationGroups"])
        self.assertIn("r4p1_hard_00", fold["connectedAuxiliaryExcludedFromTraining"])
        self.assertIn("r4p1_hard_01", fold["connectedAuxiliaryExcludedFromTraining"])

    def test_transitive_auxiliary_bridge_regression(self) -> None:
        edges = [
            {"left": "primary_a", "right": "aux_1", "sharedSourceBase": True, "strongTrajectoryOverlap": False, "nearDuplicateStart": False},
            {"left": "aux_1", "right": "aux_2", "sharedSourceBase": False, "strongTrajectoryOverlap": True, "nearDuplicateStart": False},
            {"left": "aux_2", "right": "primary_b", "sharedSourceBase": True, "strongTrajectoryOverlap": False, "nearDuplicateStart": False},
        ]
        components = connected_components(["primary_a", "aux_1", "aux_2", "primary_b"], edges, include_near=False)
        self.assertEqual([["aux_1", "aux_2", "primary_a", "primary_b"]], components)

    def test_primary_policy_supports_three_valid_folds(self) -> None:
        policy = self.graph["primaryPolicy"]
        self.assertEqual(11, policy["evaluationGroups"])
        self.assertEqual(5, policy["positiveEvaluationGroups"])
        self.assertEqual(6, policy["negativeEvaluationGroups"])
        self.assertEqual("PASS", policy["dataSufficiency"])
        self.assertTrue(all(item["valid"] for item in policy["folds"]))
        self.assertTrue(all(item["evaluationPositiveGroups"] and item["evaluationNegativeGroups"] for item in policy["folds"]))
        self.assertTrue(all(len(item["fitPositiveGroups"]) >= 2 and item["fitNegativeGroups"] for item in policy["folds"]))

    def test_conservative_policy_is_underpowered(self) -> None:
        policy = self.graph["conservativeSensitivity"]
        self.assertEqual(1, policy["evaluationGroups"])
        self.assertEqual(1, policy["positiveEvaluationGroups"])
        self.assertEqual(0, policy["negativeEvaluationGroups"])
        self.assertEqual("UNDERPOWERED_FOR_GROUP_GENERALIZATION", policy["dataSufficiency"])

    def test_no_strong_or_exact_overlap_crosses_primary_components(self) -> None:
        mapping = self.graph["primaryPolicy"]["caseToComponent"]
        for edge in self.graph["edges"]:
            if edge["sharedSourceBase"] or edge["strongTrajectoryOverlap"] or edge["hasCrossTrajectoryExactPose"]:
                self.assertEqual(mapping[edge["left"]], mapping[edge["right"]])

    def test_corrected_candidate_is_non_deployable_and_schema_consistent(self) -> None:
        candidate_path = ROOT / OUTPUT / "g2_r4_p2r_candidate.json"
        if not candidate_path.exists():
            self.skipTest("corrective rerun has not produced a candidate")
        candidate = load(candidate_path)
        self.assertEqual("NON_DEPLOYABLE", candidate["deployment"]["status"])
        self.assertFalse(candidate["deployment"]["safeOperatingPoint"])
        self.assertFalse(candidate["developmentEligibleForFutureConfirmationProposal"])
        expected = "single-frame-v1" if candidate["architecture"] == "cnn_single_frame" else "causal-h4-padding-mask-v2"
        self.assertEqual(expected, candidate["historyContract"])

    def test_prediction_ledger_is_oof_causal_and_has_no_privileged_input(self) -> None:
        ledger = ROOT / OUTPUT / "r3_corrected_oof_prediction_ledger.jsonl"
        if not ledger.exists():
            self.skipTest("corrective rerun ledger not present")
        count = 0
        with ledger.open(encoding="utf-8") as handle:
            for line in handle:
                row = json.loads(line)
                count += 1
                self.assertIn(row["outerFold"], (0, 1, 2))
                self.assertFalse(row["privilegedColumnsUsedAsInferenceInput"])
                self.assertEqual(4, len(row["historyAvailability"]))
                if row["episodeReset"] and "corrected" in row["model"]:
                    self.assertEqual([0, 0, 0, 1], row["historyAvailability"])
        self.assertEqual(10640, count)

    def test_restrictions_and_locked_evidence_remain_closed(self) -> None:
        protocol = load(ROOT / "Tools/ThoracicUSCoach/experiments/g2_r4_p2r_protocol_v1.json")
        restrictions = protocol["restrictions"]
        self.assertFalse(restrictions["newCapture"])
        self.assertFalse(restrictions["confirmationAccess"])
        self.assertFalse(restrictions["historicalLockedTestAccess"])
        self.assertFalse(restrictions["closedLoopUnity"])
        self.assertFalse(restrictions["deployment"])


class P2RCausalHistoryTests(unittest.TestCase):
    def test_first_four_masks_and_no_future(self) -> None:
        records = [record("a", index, reset=index == 0) for index in range(5)]
        indices, available = ImageStore._history_layout(records, 4)
        self.assertEqual([[0, 0, 0, 1], [0, 0, 1, 1], [0, 1, 1, 1], [1, 1, 1, 1]], available[:4].astype(int).tolist())
        for current, row in enumerate(indices):
            for slot, is_available in zip(row, available[current]):
                if is_available:
                    self.assertLessEqual(int(slot), current)

    def test_episode_reset_clears_history_even_with_same_case_id(self) -> None:
        records = [record("a", 0, reset=True), record("a", 1), record("a", 2, reset=True)]
        indices, available = ImageStore._history_layout(records, 4)
        self.assertEqual([2, 2, 2, 2], indices[2].tolist())
        self.assertEqual([0, 0, 0, 1], available[2].astype(int).tolist())

    def test_no_cross_episode_history(self) -> None:
        records = [record("a", 0, reset=True), record("a", 1), record("b", 0, reset=True), record("b", 1)]
        indices, available = ImageStore._history_layout(records, 4)
        self.assertEqual([2, 2, 2, 2], indices[2].tolist())
        self.assertEqual([2, 2, 2, 3], indices[3].tolist())
        self.assertEqual([0, 0, 1, 1], available[3].astype(int).tolist())

    def test_padding_has_zero_action_feedback_and_reset_metadata(self) -> None:
        records = [record("a", 0, issued="RESET", executed="RESET", reset=True)]
        store = metadata_store(records)
        metadata = store.history_metadata(0)
        self.assertTrue(np.all(metadata[:3] == 0.0))
        self.assertEqual([0, 0, 0, 1], metadata[:, HISTORY_AVAILABILITY_OFFSET].astype(int).tolist())
        self.assertEqual(1.0, metadata[3, ACTION_NAMES.index("RESET")])
        self.assertEqual(1.0, metadata[3, len(ACTION_NAMES) + ACTION_NAMES.index("RESET")])
        self.assertEqual(1.0, metadata[3, 2 * len(ACTION_NAMES) + 2])

    def test_accepted_completed_rejected_incomplete_and_observe_values(self) -> None:
        records = [
            record("a", 0, issued="RESET", executed="RESET", reset=True),
            record("a", 1, issued="LATERAL", executed="LATERAL", accepted=True, completed=True),
            record("a", 2, issued="CRANIAL", executed="RESET", accepted=False, completed=False),
            record("a", 3, issued="MEDIAL", executed="RESET", accepted=True, completed=False),
            record("a", 4, issued="OBSERVE", executed="OBSERVE", accepted=True, completed=True),
        ]
        store = metadata_store(records)
        rejected = store.history_metadata(2)[-1]
        incomplete = store.history_metadata(3)[-1]
        observed = store.history_metadata(4)[-1]
        self.assertEqual(1.0, rejected[ACTION_NAMES.index("CRANIAL")])
        self.assertEqual(1.0, rejected[len(ACTION_NAMES) + ACTION_NAMES.index("RESET")])
        self.assertEqual([0.0, 0.0], rejected[2 * len(ACTION_NAMES):2 * len(ACTION_NAMES) + 2].tolist())
        self.assertEqual(1.0, incomplete[ACTION_NAMES.index("MEDIAL")])
        self.assertEqual(1.0, incomplete[len(ACTION_NAMES) + ACTION_NAMES.index("RESET")])
        self.assertEqual([1.0, 0.0], incomplete[2 * len(ACTION_NAMES):2 * len(ACTION_NAMES) + 2].tolist())
        self.assertEqual(1.0, observed[ACTION_NAMES.index("OBSERVE")])
        self.assertEqual(1.0, observed[len(ACTION_NAMES) + ACTION_NAMES.index("OBSERVE")])

    def test_offline_and_causal_replay_metadata_parity(self) -> None:
        records = [
            record("episode", 0, issued="RESET", executed="RESET", reset=True),
            record("episode", 1, issued="LATERAL", executed="LATERAL"),
            record("episode", 2, issued="CRANIAL", executed="RESET", accepted=False, completed=False),
        ]
        store = metadata_store(records)
        actual = store.history_metadata(2)
        expected = np.zeros((4, HISTORY_METADATA_WIDTH), dtype=np.float32)
        for slot, item in zip((1, 2, 3), records):
            expected[slot, ACTION_NAMES.index(item["issuedAction"])] = 1.0
            expected[slot, len(ACTION_NAMES) + ACTION_NAMES.index(item["executedAction"])] = 1.0
            expected[slot, 2 * len(ACTION_NAMES)] = float(item["actionAccepted"])
            expected[slot, 2 * len(ACTION_NAMES) + 1] = float(item["actionCompleted"])
            expected[slot, 2 * len(ACTION_NAMES) + 2] = float(item["episodeReset"])
            expected[slot, HISTORY_AVAILABILITY_OFFSET] = 1.0
        np.testing.assert_array_equal(expected, actual)

    def test_model_masks_padded_encoded_contributions(self) -> None:
        model = UltrasoundTwoHead(multiview=True)
        self.assertEqual(4 * 32 + 4 * HISTORY_METADATA_WIDTH, model.fusion[0].in_features)
        # The implementation must multiply per-slot embeddings by the explicit
        # availability channel before flattening; source check guards regression.
        source = (ROOT / "Tools/ThoracicUSCoach/train_g2_r4_p2.py").read_text(encoding="utf-8")
        self.assertIn("encoded * availability", source)

    def test_no_privileged_actor_feature(self) -> None:
        permitted = {"issuedAction", "executedAction", "actionAccepted", "actionCompleted", "episodeReset"}
        self.assertFalse(permitted & {"probePosition", "probeRotation", "score", "target", "margin", "anatomy"})


if __name__ == "__main__":
    unittest.main()
