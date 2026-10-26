from __future__ import annotations

import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from g2_p2_freshness_audit import (
    accepted_visit_audit,
    audit_movements,
    derive_movement_categories,
    failure_fact_summary,
    handshake_errors,
    movement_semantics,
    record_capture_path_errors,
    record_image_errors,
)
from g2_r6_audit import sha256


RULE = {
    "requiredFieldsPerAcceptedCapture": [
        "poseEpoch", "producerPoseEpoch", "poseAppliedUnityFrame", "producerUnityFrame",
        "producerScanSerial", "producerSamplesPublished", "unityFrameCount", "acquisitionOrder",
        "capturePath", "capturePathMode", "activeAggregatorCount", "captureDisposition",
    ],
    "requiredCapturePath": "direct_producer_texture",
    "requiredCapturePathMode": "DIRECT_PRODUCER_CAMERA",
}


def record() -> dict:
    return {
        "poseEpoch": 4,
        "producerPoseEpoch": 4,
        "poseAppliedUnityFrame": 10,
        "producerUnityFrame": 15,
        "producerScanSerial": 100,
        "producerSamplesPublished": 1,
        "capturePath": "direct_producer_texture",
        "capturePathMode": "DIRECT_PRODUCER_CAMERA",
        "unityFrameCount": 15,
        "acquisitionOrder": 2,
        "activeAggregatorCount": 0,
        "captureDisposition": "accepted",
    }


class Prompt2FreshnessAuditTests(unittest.TestCase):
    def test_failed_budget_cannot_relabel_rejection_as_incomplete(self) -> None:
        actions = [
            {"index": index, "plannedAction": "LATERAL", "executedAction": "LATERAL",
             "accepted": True, "completed": True, "rollbackAttempted": False}
            for index in range(12)
        ] + [
            {"index": index, "plannedAction": "LATERAL", "executedAction": "NONE",
             "accepted": False, "completed": False, "rollbackAttempted": True}
            for index in range(12, 16)
        ]
        result = failure_fact_summary(actions)
        self.assertEqual(result["counts"], {"rejected": 4, "incomplete": 0, "rollback": 4})
        self.assertEqual(result["missingRequiredFactualCategories"], ["incomplete"])

    def test_only_accepted_but_incomplete_satisfies_incomplete_fact(self) -> None:
        result = failure_fact_summary([{
            "index": 0, "plannedAction": "LATERAL", "executedAction": "LATERAL",
            "accepted": True, "completed": False, "rollbackAttempted": False,
        }])
        self.assertEqual(result["counts"]["incomplete"], 1)

    def test_both_named_warmups_must_be_discarded(self) -> None:
        accepted_a = {**record(), "recordId": "a", "acquisitionOrder": 2, "unityFrameCount": 20,
                      "producerUnityFrame": 19, "producerScanSerial": 100, "image": "a.png"}
        accepted_b = {**record(), "recordId": "b", "acquisitionOrder": 3, "unityFrameCount": 21,
                      "producerUnityFrame": 20, "producerScanSerial": 101, "image": "b.png"}
        immediate = {"recordId": "d0", "captureDisposition": "discarded", "acquisitionOrder": 0,
                     "actionRejectionReason": "immediate_post_pose"}
        published = {"recordId": "d1", "captureDisposition": "discarded", "acquisitionOrder": 1,
                     "actionRejectionReason": "published_warmup"}
        audit_protocol = {
            "producerPoseHandshake": RULE,
            "repeatNoise": {"maximumAcceptedPairRms128": 1.0, "maximumAcceptedPairStructuralDistance": 1.0},
        }
        with patch("g2_p2_freshness_audit.pair_metrics", return_value=[{
            "left": "a", "right": "b", "rms128": 0.0, "structuralDistance": 0.0,
        }]):
            complete = accepted_visit_audit(
                Path("."), [immediate, published, accepted_a, accepted_b], audit_protocol
            )
            incomplete = accepted_visit_audit(Path("."), [immediate, accepted_a, accepted_b], audit_protocol)
        self.assertTrue(complete["requiredWarmupsDiscarded"])
        self.assertFalse(incomplete["requiredWarmupsDiscarded"])

    def test_movement_stability_is_evaluated_per_visit_not_across_poses(self) -> None:
        records = []
        for visit_id in ("lateral-a", "lateral-b"):
            records.append({
                "recordId": visit_id, "sourceKind": "freshness_movement", "visitId": visit_id,
                "captureDisposition": "accepted", "plannedAction": "Lateral", "executedAction": "Lateral",
                "actionAccepted": True, "actionCompleted": True, "rollbackAttempted": False,
                "actualResultingPoseRecorded": True,
            })
        protocol = {
            "evidenceInterface": {"movementSourceKind": "freshness_movement"},
            "movementFreshnessGate": {
                "requiredCategories": ["lateral"], "minimumAcceptedFreshCapturesPerCase": 1,
            },
        }

        def fake_visit(_root: Path, group: list[dict], _protocol: dict) -> dict:
            return {
                "accepted": group, "discardedCount": 2,
                "discardedKinds": ["immediate_post_pose", "published_warmup"],
                "requiredWarmupsDiscarded": True, "acceptedCount": len(group),
                "handshakePass": True, "monotonicCaptureOrder": True,
                "stableFreshPair": True, "pairMetrics": [], "handshakeErrors": {},
            }

        with patch("g2_p2_freshness_audit.accepted_visit_audit", side_effect=fake_visit) as mocked:
            result = audit_movements(Path("."), {"records": records}, protocol)
        self.assertEqual(mocked.call_count, 2)
        self.assertEqual(result["categories"]["lateral"]["visitCount"], 2)
        self.assertEqual(result["verdict"], "PASS")

    def test_rollback_requires_actual_pose_restoration(self) -> None:
        gate = {
            "rollbackMaximumPoseRestorePositionErrorMm": 0.25,
            "rollbackMaximumPoseRestoreAngleErrorDeg": 0.25,
        }
        value = {
            "actualResultingPoseRecorded": True, "plannedAction": "Lateral", "executedAction": "NONE",
            "actionAccepted": False, "actionCompleted": False, "rollbackAttempted": True,
            "rollbackContactRestored": True,
            "beforeProbePosition": {"x": 0, "y": 0, "z": 0},
            "probePosition": {"x": 1, "y": 0, "z": 0},
            "beforeProbeRotation": {"x": 0, "y": 0, "z": 0, "w": 1},
            "probeRotation": {"x": 0, "y": 0, "z": 0, "w": 1},
        }
        self.assertFalse(movement_semantics("rollback", value, gate))
        value["probePosition"]["x"] = 0.1
        self.assertTrue(movement_semantics("rollback", value, gate))

    def test_movement_categories_come_from_actual_flags(self) -> None:
        rolled_back = {
            "actionCategory": "incomplete", "plannedAction": "Lateral", "actionAccepted": False,
            "executedAction": "NONE", "actionCompleted": False, "rollbackAttempted": True,
        }
        self.assertEqual(derive_movement_categories(rolled_back), ["lateral", "rejected", "rollback"])
        true_incomplete = {
            "actionCategory": "rejected", "plannedAction": "Cranial", "actionAccepted": True,
            "executedAction": "CRANIAL", "actionCompleted": False, "rollbackAttempted": False,
        }
        self.assertEqual(derive_movement_categories(true_incomplete), ["incomplete", "longitudinal"])

    def test_direction_may_be_confirmed_by_executed_action(self) -> None:
        value = {
            "actionCategory": "observe", "plannedAction": "NONE", "executedAction": "RotateClockwise",
            "actionAccepted": True, "actionCompleted": True, "rollbackAttempted": False,
        }
        self.assertEqual(derive_movement_categories(value), ["rotation"])

    def test_observe_may_be_confirmed_by_executed_action(self) -> None:
        value = {
            "actionCategory": "rotation", "plannedAction": "NONE", "executedAction": "Observe",
            "actionAccepted": True, "actionCompleted": True, "rollbackAttempted": False,
        }
        self.assertEqual(derive_movement_categories(value), ["observe"])

    def test_discarded_duplicate_image_is_retained_but_allowed(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "a.bin").write_bytes(b"same")
            (root / "b.bin").write_bytes(b"same")
            digest = sha256(root / "a.bin")
            records = [
                {"recordId": "accepted", "image": "a.bin", "imageSha256": digest, "captureDisposition": "accepted"},
                {"recordId": "discarded", "image": "b.bin", "imageSha256": digest, "captureDisposition": "discarded"},
            ]
            self.assertEqual(record_image_errors(root, records), [])

    def test_duplicate_accepted_image_fails_closed(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "a.bin").write_bytes(b"same")
            (root / "b.bin").write_bytes(b"same")
            digest = sha256(root / "a.bin")
            records = [
                {"recordId": "first", "image": "a.bin", "imageSha256": digest, "captureDisposition": "accepted"},
                {"recordId": "second", "image": "b.bin", "imageSha256": digest, "captureDisposition": "accepted"},
            ]
            self.assertEqual(record_image_errors(root, records), ["duplicate accepted image hash: second"])

    def test_complete_pose_producer_handshake_passes(self) -> None:
        self.assertEqual(handshake_errors(record(), RULE), [])

    def test_camera_only_frame_without_producer_advance_fails(self) -> None:
        value = record()
        value["producerUnityFrame"] = value["poseAppliedUnityFrame"]
        self.assertIn("producer frame did not advance after pose application", handshake_errors(value, RULE))

    def test_old_pose_epoch_fails(self) -> None:
        value = record()
        value["producerPoseEpoch"] = value["poseEpoch"] - 1
        self.assertIn("producer pose epoch differs from physical pose epoch", handshake_errors(value, RULE))

    def test_missing_explicit_producer_publication_fails(self) -> None:
        value = record()
        value["producerSamplesPublished"] = 0
        self.assertIn("no explicit producer sample published", handshake_errors(value, RULE))

    def test_direct_capture_requires_zero_active_aggregators(self) -> None:
        value = record()
        value["activeAggregatorCount"] = 1
        self.assertIn("direct producer mode requires zero active aggregators", handshake_errors(value, RULE))

    def test_capture_path_mode_mismatch_fails_closed(self) -> None:
        value = record()
        value["capturePathMode"] = "AGGREGATED_HISTORY"
        self.assertIn("capture path mode mismatch", handshake_errors(value, RULE))

    def test_discarded_record_requires_explicit_direct_capture_provenance(self) -> None:
        discarded = {
            "captureDisposition": "discarded", "capturePath": "", "capturePathMode": "",
            "activeAggregatorCount": 0,
        }
        errors = record_capture_path_errors(discarded, RULE)
        self.assertIn("capture path mismatch", errors)
        self.assertIn("capture path mode mismatch", errors)
        discarded["capturePath"] = "direct_producer_texture"
        discarded["capturePathMode"] = "DIRECT_PRODUCER_CAMERA"
        self.assertEqual(record_capture_path_errors(discarded, RULE), [])

    def test_discarded_capture_cannot_satisfy_handshake(self) -> None:
        value = record()
        value["captureDisposition"] = "discarded"
        self.assertIn("handshake evaluated on non-accepted capture", handshake_errors(value, RULE))


if __name__ == "__main__":
    unittest.main()
