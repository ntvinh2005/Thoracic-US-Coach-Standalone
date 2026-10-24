from __future__ import annotations

import json
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

from active2d_action import file_sha256, validate_action_model
from run_active2d_policy_server import (
    U08_SELECTED_CHECKPOINT_SHA256,
    U08_SELECTED_CONFIGURATION,
    U08_SELECTED_SEED,
    PolicyMemory,
    apply_execution_feedback,
    choose_u08_action,
    phase_two_action,
)


ROOT = Path(__file__).resolve().parents[2]
SELECTED = (
    ROOT
    / "Artifacts/ThoracicUSCoach/active2d/u08_action_selector/u08_selected_action_response.json"
)


def feedback(action: str, *, accepted: bool, completed: bool) -> dict[str, object]:
    return {
        "hasPreviousActionFeedback": True,
        "previousAction": action,
        "previousActionAccepted": accepted,
        "previousActionCompleted": completed,
    }


class U08LiveIntegrationTests(unittest.TestCase):
    def test_exact_selected_checkpoint_is_frozen_spatial_seed47(self) -> None:
        self.assertEqual(file_sha256(SELECTED), U08_SELECTED_CHECKPOINT_SHA256)
        model = json.loads(SELECTED.read_text(encoding="utf-8"))
        validate_action_model(model)
        self.assertEqual(model["architecture"]["featureMode"], "spatial-v2")
        self.assertEqual(model["architecture"]["seed"], U08_SELECTED_SEED)
        self.assertEqual(model["training"]["configurationId"], U08_SELECTED_CONFIGURATION)
        self.assertEqual(model["allowedActions"], ["MEDIAL", "LATERAL"])

    @patch("run_active2d_policy_server.predict_action_values")
    @patch("run_active2d_policy_server.convolutional_features_v2")
    def test_candidate_argmax_is_limited_to_medial_lateral(self, features, predict) -> None:
        features.return_value = np.zeros(1024, dtype=np.float32)
        predict.return_value = np.asarray([[0.2, 0.6]], dtype=np.float32)
        memory = PolicyMemory("episode")
        action = choose_u08_action("Medial", Path("clean.png"), {}, memory)
        self.assertEqual(action, "Lateral")
        self.assertTrue(memory.candidate_selected)
        self.assertFalse(memory.candidate_fallback)
        self.assertAlmostEqual(memory.candidate_medial_value, 0.2, places=6)
        self.assertAlmostEqual(memory.candidate_lateral_value, 0.6, places=6)

    def test_unsupported_longitudinal_action_uses_explicit_raster_fallback(self) -> None:
        memory = PolicyMemory("episode")
        action = choose_u08_action("Cranial", Path("clean.png"), {}, memory)
        self.assertEqual(action, "Cranial")
        self.assertFalse(memory.candidate_selected)
        self.assertTrue(memory.candidate_fallback)
        self.assertEqual(memory.candidate_fallback_reason, "unsupported_action:CRANIAL")

    def test_accepted_completed_feedback_advances_plan_and_odometry(self) -> None:
        memory = PolicyMemory(
            "episode", pending_action="Lateral", pending_request_id="request-1",
            pending_phase_two_plan="raster",
        )
        apply_execution_feedback(memory, feedback("LATERAL", accepted=True, completed=True))
        self.assertEqual(memory.phase_two_step, 1)
        self.assertEqual(memory.phase_two_lateral_index, 1)
        self.assertEqual(memory.last_action, "Lateral")
        self.assertTrue(memory.execution_feedback_applied)

    def test_rejected_feedback_consumes_attempt_but_not_odometry(self) -> None:
        memory = PolicyMemory(
            "episode", pending_action="Medial", pending_request_id="request-1",
            pending_phase_two_plan="raster",
        )
        apply_execution_feedback(memory, feedback("MEDIAL", accepted=False, completed=False))
        self.assertEqual(memory.phase_two_step, 1)
        self.assertEqual(memory.phase_two_lateral_index, 0)
        self.assertEqual(memory.last_action, "RESET")
        self.assertFalse(memory.execution_feedback_accepted)

    def test_clamped_feedback_consumes_attempt_but_not_odometry(self) -> None:
        memory = PolicyMemory(
            "episode", pending_action="Lateral", pending_request_id="request-1",
            pending_phase_two_plan="raster",
        )
        apply_execution_feedback(memory, feedback("LATERAL", accepted=True, completed=False))
        self.assertEqual(memory.phase_two_step, 1)
        self.assertEqual(memory.phase_two_lateral_index, 0)
        self.assertEqual(memory.last_action, "RESET")
        self.assertTrue(memory.execution_feedback_accepted)
        self.assertFalse(memory.execution_feedback_completed)

    def test_pending_candidate_action_requires_matching_feedback(self) -> None:
        memory = PolicyMemory("episode", pending_action="Lateral")
        with self.assertRaisesRegex(RuntimeError, "requires previous actuator"):
            apply_execution_feedback(memory, {})
        with self.assertRaisesRegex(RuntimeError, "action mismatch"):
            apply_execution_feedback(
                memory, feedback("MEDIAL", accepted=True, completed=True)
            )

    def test_raster_progress_waits_for_versioned_execution_feedback(self) -> None:
        memory = PolicyMemory("episode", phase="LateralAcquisition", phase_two_prefix_actions=())
        action, confidence, uncertainty = phase_two_action(None, Path("unused.png"), memory)
        self.assertEqual(action, "Lateral")
        self.assertEqual(memory.phase_two_step, 0)
        self.assertEqual(memory.pending_action, "Lateral")
        apply_execution_feedback(memory, feedback("LATERAL", accepted=True, completed=True))
        self.assertEqual(memory.phase_two_step, 1)
        self.assertEqual((confidence, uncertainty), (0.0, 1.0))
        self.assertFalse(memory.candidate_enabled)

    def test_unity_bridge_persists_feedback_and_candidate_telemetry(self) -> None:
        types = (ROOT / "Assets/ThoracicUSCoach/Runtime/ThoracicActive2DTypes.cs").read_text(
            encoding="utf-8"
        )
        controller = (
            ROOT / "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs"
        ).read_text(encoding="utf-8")
        for field in (
            "hasPreviousActionFeedback", "previousActionAccepted", "previousActionCompleted",
            "candidateMedialValue", "candidateLateralValue", "candidateFallbackReason",
            "executionFeedbackApplied",
        ):
            self.assertIn(field, types)
            self.assertIn(field, controller)
        live_start = controller.index("Active2DActorRequest request = new Active2DActorRequest")
        live_end = controller.index("try", live_start)
        live = controller[live_start:live_end]
        for forbidden in ("t7RightSeed", "finalTargetScore", "nearestLevel", "probePosition"):
            self.assertNotIn(forbidden, live)


if __name__ == "__main__":
    unittest.main()
