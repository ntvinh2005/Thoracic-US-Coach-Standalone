from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from active2d_stop import request_stop_features
from g2_r4_baseline_parity import (
    RESET,
    align_outgoing_trace_action,
    causal_live_action,
    frozen_training_action,
)


class G2R4BaselineParityTests(unittest.TestCase):
    def test_current_image_uses_previous_outgoing_trace_action(self) -> None:
        rows = [
            {"reset": True, "action": "Lateral", "accepted": True, "completed": True},
            {"action": "Medial", "accepted": True, "completed": True},
        ]
        self.assertEqual(RESET, align_outgoing_trace_action(rows, 0))
        self.assertEqual("LATERAL", align_outgoing_trace_action(rows, 1))

    def test_reset_rejected_incomplete_and_successful_feedback(self) -> None:
        self.assertEqual(RESET, causal_live_action(episode_reset=True, feedback_available=False))
        self.assertIsNone(causal_live_action(episode_reset=False, feedback_available=False))
        self.assertEqual(RESET, causal_live_action(
            episode_reset=False, feedback_available=True, action="Lateral", accepted=False, completed=False))
        self.assertEqual(RESET, causal_live_action(
            episode_reset=False, feedback_available=True, action="Lateral", accepted=True, completed=False))
        self.assertEqual("LATERAL", causal_live_action(
            episode_reset=False, feedback_available=True, action="Lateral", accepted=True, completed=True))
        with self.assertRaises(ValueError):
            causal_live_action(
                episode_reset=False, feedback_available=True, action="Lateral", accepted=False, completed=True)

    def test_historical_training_protocol_is_explicitly_verbatim(self) -> None:
        frame = {"issuedAction": "Rotate_Clockwise", "actionAccepted": False, "actionCompleted": False}
        self.assertEqual("ROTATECLOCKWISE", frozen_training_action(frame))

    def test_spatial_feature_vector_parity_and_action_tail(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "frame.png"
            Image.fromarray(np.arange(64 * 64, dtype=np.uint8).reshape(64, 64)).save(path)
            reset = request_stop_features(path, None, RESET, "spatial-v2")
            rejected = request_stop_features(
                path, None,
                causal_live_action(episode_reset=False, feedback_available=True,
                                   action="Lateral", accepted=False, completed=False),
                "spatial-v2",
            )
            executed = request_stop_features(path, None, "LATERAL", "spatial-v2")
            self.assertTrue(np.array_equal(reset, rejected))
            self.assertTrue(np.array_equal(reset[:-8], executed[:-8]))
            self.assertFalse(np.array_equal(reset[-8:], executed[-8:]))

    def test_frozen_identity_and_threshold(self) -> None:
        root = Path(__file__).resolve().parents[2]
        path = root / "Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_stop_live_spatial_logistic_v1.json"
        checkpoint = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual("spatial-v2", checkpoint["architecture"]["featureExtractor"])
        self.assertAlmostEqual(0.93, float(checkpoint["stopRule"]["threshold"]))


if __name__ == "__main__":
    unittest.main()
