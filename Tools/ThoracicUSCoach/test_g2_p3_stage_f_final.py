from __future__ import annotations

import unittest
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from g2_p2_freshness_audit import read_json
from g2_p3_stage_f_final_audit import (
    PROTOCOL,
    _live_test_provenance_errors,
    audit_synthetic_trials,
    canonical_object_sha256,
    derive_offline_transition,
    validate_synthetic_trial,
)
from g2_r6_audit import sha256


ROOT = Path(__file__).resolve().parents[2]
PROTOCOL_VALUE = read_json(ROOT / PROTOCOL)


def valid_trial() -> dict:
    actual_delta = {"lateralMm": 0.0, "longitudinalMm": 0.0, "yawDeg": 0.0}
    best = {"recordId": "best-7", "canonicalSha256": "a" * 64}
    history_tail = {
        "issuedAction": "LATERAL",
        "executedAction": "LATERAL",
        "executedDelta": actual_delta,
        "accepted": True,
        "completed": False,
        "feedbackObservedBeforeAppend": True,
    }
    transition = {
        "odometryDeltaApplied": actual_delta,
        "postVisitedCellIds": ["cell-1"],
        "postBestMemory": best,
        "controllerDecision": "ABSTAIN",
        "stopIssued": False,
        "causalHistoryTail": history_tail,
    }
    return {
        "trialId": "fault-trial-0",
        "faultId": "P3_ACCEPTED_INCOMPLETE_AFTER_PARTIAL_EXECUTION_V1",
        "evidenceDomain": "DETERMINISTIC_FAULT_INJECTION",
        "synthetic": True,
        "faultInjected": True,
        "decisionSupportEligible": False,
        "trainingEligible": False,
        "actionAccepted": True,
        "actionCompleted": False,
        "issuedAction": "LATERAL",
        "actualExecutedAction": "LATERAL",
        "issuedDelta": {"lateralMm": 3.0, "longitudinalMm": 0.0, "yawDeg": 0.0},
        "actualDelta": {"lateralMm": 0.0, "longitudinalMm": 0.0, "yawDeg": 0.0},
        "factualResultingDeltaProvided": False,
        "odometryDeltaApplied": {"lateralMm": 0.0, "longitudinalMm": 0.0, "yawDeg": 0.0},
        "preVisitedCellIds": ["cell-1"],
        "postVisitedCellIds": ["cell-1"],
        "plannedFullMoveTargetCellId": "cell-2",
        "preBestMemory": best,
        "postBestMemory": best,
        "safeReturn": {
            "returnAnchorSource": "last_confirmed_actual_pose",
            "assumesFullIssuedMove": False,
            "contactValid": True,
            "workspaceValid": True,
        },
        "controllerDecision": "ABSTAIN",
        "stopIssued": False,
        "causalActionHistory": [history_tail],
        "liveTransitionSemantics": transition,
    }


class Prompt3StageFFinalTests(unittest.TestCase):
    def live_artifacts(self, root: Path, trial: dict) -> set[str]:
        source = "b" * 40
        bridge_path = root / "bridge.json"
        reducer_path = root / "reducer.json"
        bridge = {
            "sourceCommit": source,
            "exportedBy": "CSharp",
            "assembly": "ThoracicUSCoach.Tests.Editor",
            "testName": "ThoracicP3FaultInjectionTests.AcceptedIncompleteBridgeSerializesZeroDisplacement",
            "result": "PASS",
            "bridgeFeedback": {
                "accepted": True,
                "completed": False,
                "factualResultingDeltaProvided": False,
                "actualDelta": trial["actualDelta"],
            },
        }
        compared = {
            field: trial[field] for field in (
                "faultId", "issuedAction", "actualExecutedAction", "actionAccepted",
                "actionCompleted", "issuedDelta", "actualDelta", "factualResultingDeltaProvided",
                "preVisitedCellIds", "plannedFullMoveTargetCellId", "preBestMemory",
                "liveTransitionSemantics",
            )
        }
        reducer = {
            "sourceCommit": source,
            "exportedBy": "Python",
            "module": "Tools.ThoracicUSCoach.run_active2d_policy_server",
            "testName": "test_p3_accepted_incomplete_live_feedback_fail_closed",
            "result": "PASS",
            "trial": compared,
        }
        bridge_path.write_text(json.dumps(bridge), encoding="utf-8")
        reducer_path.write_text(json.dumps(reducer), encoding="utf-8")
        trial["bridgeTestEvidence"] = {
            **{key: bridge[key] for key in ("sourceCommit", "exportedBy", "assembly", "testName", "result")},
            "artifact": "bridge.json", "artifactSha256": sha256(bridge_path),
        }
        trial["productionReducerTestEvidence"] = {
            **{key: reducer[key] for key in ("sourceCommit", "exportedBy", "module", "testName", "result")},
            "artifact": "reducer.json", "artifactSha256": sha256(reducer_path),
        }
        return {"bridge.json", "reducer.json"}

    def test_valid_deterministic_incomplete_fault_passes(self) -> None:
        self.assertEqual(validate_synthetic_trial(valid_trial(), PROTOCOL_VALUE), [])

    def test_full_move_odometry_is_rejected(self) -> None:
        trial = valid_trial()
        trial["actualDelta"] = trial["issuedDelta"]
        trial["odometryDeltaApplied"] = trial["issuedDelta"]
        errors = validate_synthetic_trial(trial, PROTOCOL_VALUE)
        self.assertIn("fault did not prevent the full issued move", errors)
        self.assertIn("odometry incorrectly applied the full issued delta", errors)

    def test_odometry_must_equal_zero_actual_feedback(self) -> None:
        trial = valid_trial()
        trial["odometryDeltaApplied"] = {"lateralMm": 1.0, "longitudinalMm": 0.0, "yawDeg": 0.0}
        self.assertIn(
            "odometry does not equal actual executed delta",
            validate_synthetic_trial(trial, PROTOCOL_VALUE),
        )

    def test_bridge_cannot_invent_partial_displacement(self) -> None:
        trial = valid_trial()
        trial["actualDelta"] = {"lateralMm": 1.25, "longitudinalMm": 0.0, "yawDeg": 0.0}
        errors = validate_synthetic_trial(trial, PROTOCOL_VALUE)
        self.assertIn("bridge invented a partial displacement absent factual feedback", errors)

    def test_bridge_must_declare_absent_resulting_delta(self) -> None:
        trial = valid_trial()
        trial["factualResultingDeltaProvided"] = True
        self.assertIn(
            "bridge must declare that no factual partial displacement was supplied",
            validate_synthetic_trial(trial, PROTOCOL_VALUE),
        )

    def test_false_target_cell_visit_is_rejected(self) -> None:
        trial = valid_trial()
        trial["postVisitedCellIds"] = ["cell-1", "cell-2"]
        errors = validate_synthetic_trial(trial, PROTOCOL_VALUE)
        self.assertIn("incomplete action changed visited cells", errors)
        self.assertIn("planned full-move target cell was falsely visited", errors)

    def test_best_memory_corruption_is_rejected(self) -> None:
        trial = valid_trial()
        trial["postBestMemory"] = {"recordId": "bad", "canonicalSha256": "b" * 64}
        self.assertIn(
            "incomplete action corrupted best memory",
            validate_synthetic_trial(trial, PROTOCOL_VALUE),
        )

    def test_safe_return_cannot_assume_full_move(self) -> None:
        trial = valid_trial()
        trial["safeReturn"]["assumesFullIssuedMove"] = True
        self.assertIn(
            "safe return assumes the full issued move",
            validate_synthetic_trial(trial, PROTOCOL_VALUE),
        )

    def test_incomplete_feedback_requires_abstain_and_no_stop(self) -> None:
        trial = valid_trial()
        trial["controllerDecision"] = "STOP"
        trial["stopIssued"] = True
        self.assertIn(
            "incomplete feedback did not force abstain/no-STOP",
            validate_synthetic_trial(trial, PROTOCOL_VALUE),
        )

    def test_history_must_use_actual_executed_feedback_causally(self) -> None:
        trial = valid_trial()
        trial["causalActionHistory"][-1]["completed"] = True
        trial["causalActionHistory"][-1]["executedDelta"] = trial["issuedDelta"]
        errors = validate_synthetic_trial(trial, PROTOCOL_VALUE)
        self.assertIn("causal history mismatch: completed", errors)
        self.assertIn("causal history does not contain actual executed delta", errors)

    def test_offline_live_semantics_must_match(self) -> None:
        trial = valid_trial()
        trial["liveTransitionSemantics"] = dict(trial["liveTransitionSemantics"])
        trial["liveTransitionSemantics"]["stopIssued"] = True
        self.assertIn(
            "independent offline replay differs from live C# transition",
            validate_synthetic_trial(trial, PROTOCOL_VALUE),
        )

    def test_mutated_independent_offline_replay_is_detected(self) -> None:
        trial = valid_trial()
        broken = derive_offline_transition(trial)
        broken["stopIssued"] = True
        with patch("g2_p3_stage_f_final_audit.derive_offline_transition", return_value=broken):
            self.assertIn(
                "independent offline replay differs from live C# transition",
                validate_synthetic_trial(trial, PROTOCOL_VALUE),
            )

    def test_hand_authored_offline_semantics_are_rejected(self) -> None:
        trial = valid_trial()
        trial["offlineTransitionSemantics"] = trial["liveTransitionSemantics"]
        self.assertIn(
            "hand-authored offline transition semantics are forbidden",
            validate_synthetic_trial(trial, PROTOCOL_VALUE),
        )

    def test_two_distinct_indexed_live_artifacts_are_required(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory)
            trial = valid_trial()
            indexed = self.live_artifacts(root, trial)
            self.assertEqual(_live_test_provenance_errors(
                trial, PROTOCOL_VALUE, root=root, indexed_paths=indexed,
                expected_source_commit="b" * 40,
            ), [])
            trial["productionReducerTestEvidence"]["artifact"] = "bridge.json"
            errors = _live_test_provenance_errors(
                trial, PROTOCOL_VALUE, root=root, indexed_paths=indexed,
                expected_source_commit="b" * 40,
            )
            self.assertIn("bridge and production-reducer artifacts must be distinct", errors)

    def test_production_reducer_artifact_must_match_live_transition(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory)
            trial = valid_trial()
            indexed = self.live_artifacts(root, trial)
            reducer = json.loads((root / "reducer.json").read_text(encoding="utf-8"))
            reducer["trial"]["liveTransitionSemantics"]["stopIssued"] = True
            (root / "reducer.json").write_text(json.dumps(reducer), encoding="utf-8")
            trial["productionReducerTestEvidence"]["artifactSha256"] = sha256(root / "reducer.json")
            errors = _live_test_provenance_errors(
                trial, PROTOCOL_VALUE, root=root, indexed_paths=indexed,
                expected_source_commit="b" * 40,
            )
            self.assertIn("production-reducer artifact/trial mismatch: liveTransitionSemantics", errors)

    def test_synthetic_image_or_eligibility_is_rejected(self) -> None:
        trial = valid_trial()
        trial["image"] = "fake.png"
        trial["decisionSupportEligible"] = True
        errors = validate_synthetic_trial(trial, PROTOCOL_VALUE)
        self.assertIn("synthetic trial must not contain image evidence", errors)
        self.assertIn("synthetic evidence is not quarantined", errors)

    def test_synthetic_gate_requires_at_least_one_valid_trial(self) -> None:
        self.assertEqual(audit_synthetic_trials({"syntheticTrials": []}, PROTOCOL_VALUE)["verdict"], "FAIL")
        result = audit_synthetic_trials({"syntheticTrials": [valid_trial()]}, PROTOCOL_VALUE)
        self.assertEqual(result["verdict"], "PASS")

    def test_synthetic_trial_must_match_prefrozen_manifest_plan(self) -> None:
        trial = valid_trial()
        fields = (
            "faultId", "trialId", "issuedAction", "actualExecutedAction",
            "issuedDelta", "actualDelta", "preVisitedCellIds",
            "plannedFullMoveTargetCellId", "preBestMemory",
        )
        manifest = {"syntheticFaultPlan": {field: trial[field] for field in fields}}
        self.assertEqual(
            audit_synthetic_trials({"syntheticTrials": [trial]}, PROTOCOL_VALUE, manifest=manifest)["verdict"],
            "PASS",
        )
        manifest["syntheticFaultPlan"]["plannedFullMoveTargetCellId"] = "different"
        result = audit_synthetic_trials({"syntheticTrials": [trial]}, PROTOCOL_VALUE, manifest=manifest)
        self.assertEqual(result["verdict"], "FAIL")
        self.assertIn(
            "synthetic trial differs from prefrozen plan: plannedFullMoveTargetCellId",
            result["trials"][0]["errors"],
        )

    def test_malformed_extra_synthetic_trial_fails_closed(self) -> None:
        result = audit_synthetic_trials({"syntheticTrials": [valid_trial(), "not-an-object"]}, PROTOCOL_VALUE)
        self.assertEqual(result["verdict"], "FAIL")
        self.assertEqual(result["trials"][-1]["errors"], ["synthetic trial is not an object"])

    def test_canonical_semantics_hash_ignores_key_order_only(self) -> None:
        self.assertEqual(canonical_object_sha256({"a": 1, "b": 2}), canonical_object_sha256({"b": 2, "a": 1}))


if __name__ == "__main__":
    unittest.main()
