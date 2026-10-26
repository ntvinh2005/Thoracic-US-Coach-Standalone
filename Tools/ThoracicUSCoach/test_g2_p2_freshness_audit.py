from __future__ import annotations

import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from g2_p2_freshness_audit import (
    audit_movements,
    derive_movement_categories,
    handshake_errors,
    movement_semantics,
    record_image_errors,
)
from g2_r6_audit import sha256


RULE = {
    "requiredFieldsPerAcceptedCapture": [
        "poseEpoch", "producerPoseEpoch", "poseAppliedUnityFrame", "producerUnityFrame",
        "producerScanSerial", "producerSamplesPublished", "unityFrameCount", "acquisitionOrder",
        "aggregatorCount", "aggregatorHistoryLength",
        "aggregatorFreshSampleCount", "aggregatorAllSamplesPoseEpochMatch", "captureDisposition",
    ]
}


def record() -> dict:
    return {
        "poseEpoch": 4,
        "producerPoseEpoch": 4,
        "poseAppliedUnityFrame": 10,
        "producerUnityFrame": 15,
        "producerScanSerial": 100,
        "producerSamplesPublished": 5,
        "unityFrameCount": 15,
        "acquisitionOrder": 2,
        "aggregatorHistoryLength": 5,
        "aggregatorCount": 1,
        "aggregatorFreshSampleCount": 5,
        "aggregatorAllSamplesPoseEpochMatch": True,
        "captureDisposition": "accepted",
    }


class Prompt2FreshnessAuditTests(unittest.TestCase):
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
                "accepted": group, "discardedCount": 1, "acceptedCount": len(group),
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

    def test_partially_refilled_aggregator_fails(self) -> None:
        value = record()
        value["aggregatorFreshSampleCount"] = 4
        self.assertIn("aggregator lacks five current-pose samples", handshake_errors(value, RULE))

    def test_hardcoded_zero_aggregator_or_short_publish_fails(self) -> None:
        value = record()
        value["aggregatorCount"] = 0
        value["producerSamplesPublished"] = 4
        errors = handshake_errors(value, RULE)
        self.assertIn("active aggregator count is not exactly one", errors)
        self.assertIn("fewer than five producer samples published", errors)

    def test_multiple_active_aggregators_fail_closed(self) -> None:
        value = record()
        value["aggregatorCount"] = 2
        self.assertIn("active aggregator count is not exactly one", handshake_errors(value, RULE))

    def test_mixed_pose_aggregator_fails(self) -> None:
        value = record()
        value["aggregatorAllSamplesPoseEpochMatch"] = False
        self.assertIn("aggregator contains another pose epoch", handshake_errors(value, RULE))

    def test_discarded_capture_cannot_satisfy_handshake(self) -> None:
        value = record()
        value["captureDisposition"] = "discarded"
        self.assertIn("handshake evaluated on non-accepted capture", handshake_errors(value, RULE))


if __name__ == "__main__":
    unittest.main()
