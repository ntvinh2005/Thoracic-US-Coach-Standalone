from __future__ import annotations

import unittest

from g2_p2_freshness_audit import handshake_errors


RULE = {
    "requiredFieldsPerAcceptedCapture": [
        "poseEpoch", "producerPoseEpoch", "poseAppliedUnityFrame", "producerUnityFrame",
        "unityFrameCount", "acquisitionOrder", "aggregatorHistoryLength",
        "aggregatorFreshSampleCount", "aggregatorAllSamplesPoseEpochMatch", "captureDisposition",
    ]
}


def record() -> dict:
    return {
        "poseEpoch": 4,
        "producerPoseEpoch": 4,
        "poseAppliedUnityFrame": 10,
        "producerUnityFrame": 15,
        "unityFrameCount": 15,
        "acquisitionOrder": 2,
        "aggregatorHistoryLength": 5,
        "aggregatorFreshSampleCount": 5,
        "aggregatorAllSamplesPoseEpochMatch": True,
        "captureDisposition": "accepted",
    }


class Prompt2FreshnessAuditTests(unittest.TestCase):
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
