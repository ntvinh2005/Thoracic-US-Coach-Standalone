from __future__ import annotations

import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

from phase2_stop_state import (
    Phase2DecisionConfig,
    Phase2DecisionState,
    Phase2Feedback,
    apply_execution_feedback,
    decide_phase2,
    register_issued_action,
    replay_phase2,
)
from run_active2d_policy_server import (
    PolicyMemory,
    apply_execution_feedback as apply_server_feedback,
    phase_two_action,
)


def config(actions=("Lateral",), confirmations=1) -> Phase2DecisionConfig:
    return Phase2DecisionConfig(
        prefix_actions=(),
        exploration_actions=tuple(actions),
        stop_threshold=0.8,
        required_confirmations=confirmations,
        max_return_attempts=4,
        max_return_failures=2,
    )


class Phase2StopStateTests(unittest.TestCase):
    def test_rejected_and_incomplete_moves_do_not_advance_odometry(self) -> None:
        for accepted, completed in ((False, False), (True, False)):
            state = Phase2DecisionState(stage="EXPLORE")
            decision = decide_phase2(state, config(), stop_score=0.1, quality_score=0.2)
            register_issued_action(state, decision.action, decision.plan)
            apply_execution_feedback(
                state, Phase2Feedback("Lateral", accepted=accepted, completed=completed)
            )
            self.assertEqual(1, state.exploration_step)
            self.assertEqual(0, state.lateral_index)

    def test_completed_move_advances_acknowledged_odometry(self) -> None:
        state = Phase2DecisionState(stage="EXPLORE")
        decision = decide_phase2(state, config(), stop_score=0.1, quality_score=0.2)
        register_issued_action(state, decision.action, decision.plan)
        apply_execution_feedback(state, Phase2Feedback("Lateral", True, True))
        self.assertEqual(1, state.lateral_index)

    def test_return_restores_yaw_before_confirmation(self) -> None:
        state = Phase2DecisionState(
            stage="RETURN", yaw_index=1, best_yaw_index=0, best_observation_index=2
        )
        decision = decide_phase2(state, config(actions=()), stop_score=0.9, quality_score=0.9)
        self.assertEqual("RotateCounterClockwise", decision.action)

    def test_return_requires_fresh_observe_before_stop(self) -> None:
        state = Phase2DecisionState(stage="EXPLORE")
        first = decide_phase2(state, config(), stop_score=0.95, quality_score=0.9)
        register_issued_action(state, first.action, first.plan)
        apply_execution_feedback(state, Phase2Feedback("Lateral", True, True))
        returning = decide_phase2(state, config(), stop_score=0.1, quality_score=0.1)
        self.assertEqual("Medial", returning.action)
        register_issued_action(state, returning.action, returning.plan)
        apply_execution_feedback(state, Phase2Feedback("Medial", True, True))
        confirm = decide_phase2(state, config(), stop_score=0.95, quality_score=0.9)
        self.assertEqual("Observe", confirm.action)
        register_issued_action(state, confirm.action, confirm.plan)
        apply_execution_feedback(state, Phase2Feedback("Observe", True, True))
        terminal = decide_phase2(state, config(), stop_score=0.94, quality_score=0.87)
        self.assertEqual("Stop", terminal.action)
        self.assertEqual("fresh-image-confirmed", terminal.reason)

    def test_fresh_confirmation_failure_abstains_instead_of_stopping(self) -> None:
        state = Phase2DecisionState(
            stage="CONFIRM", best_quality=0.9, best_observation_index=3
        )
        decision = decide_phase2(state, config(), stop_score=0.2, quality_score=0.9)
        self.assertIsNone(decision.action)
        self.assertEqual("FRESH_CONFIRMATION_FAILED", state.outcome)

    def test_return_failure_is_bounded(self) -> None:
        state = Phase2DecisionState(
            stage="RETURN", best_lateral_index=1, best_observation_index=2
        )
        for _ in range(2):
            decision = decide_phase2(state, config(), stop_score=0.1, quality_score=0.1)
            register_issued_action(state, decision.action, decision.plan)
            apply_execution_feedback(state, Phase2Feedback("Lateral", False, False))
        terminal = decide_phase2(state, config(), stop_score=0.1, quality_score=0.1)
        self.assertIsNone(terminal.action)
        self.assertEqual("TARGET_RECOGNIZED_NOT_RECOVERED", state.outcome)

    def test_offline_replay_uses_same_transition_logic(self) -> None:
        sequence = [
            (0.95, 0.9, None),
            (0.1, 0.1, Phase2Feedback("Lateral", True, True)),
            (0.95, 0.9, Phase2Feedback("Medial", True, True)),
            (0.94, 0.87, Phase2Feedback("Observe", True, True)),
        ]
        state, decisions = replay_phase2(sequence, config())
        self.assertEqual(["Lateral", "Medial", "Observe", "Stop"], [d.action for d in decisions])
        self.assertEqual("EXPLICIT_STOP", state.outcome)

    @patch("run_active2d_policy_server.PHASE_TWO_RASTER_ACTIONS", ("Lateral",))
    @patch("run_active2d_policy_server.quality_predictions")
    @patch("run_active2d_policy_server.binary_probabilities")
    @patch("run_active2d_policy_server.request_stop_features")
    def test_live_server_and_offline_replay_have_identical_decisions(
        self, feature_mock, stop_mock, quality_mock
    ) -> None:
        feature_mock.return_value = np.zeros(8, dtype=np.float32)
        stop_mock.side_effect = [np.asarray([value]) for value in (0.95, 0.1, 0.95, 0.94)]
        quality_mock.side_effect = [np.asarray([value]) for value in (0.9, 0.1, 0.9, 0.87)]
        model = {
            "architecture": {"featureExtractor": "temporal-v3"},
            "stopRule": {"threshold": 0.8, "requiredConfirmations": 1},
        }
        memory = PolicyMemory(
            "episode", phase="LateralAcquisition", phase_two_prefix_actions=()
        )
        live_actions = []
        feedback_rows = [
            None,
            {"hasPreviousActionFeedback": True, "previousAction": "LATERAL",
             "previousActionAccepted": True, "previousActionCompleted": True},
            {"hasPreviousActionFeedback": True, "previousAction": "MEDIAL",
             "previousActionAccepted": True, "previousActionCompleted": True},
            {"hasPreviousActionFeedback": True, "previousAction": "OBSERVE",
             "previousActionAccepted": True, "previousActionCompleted": True},
        ]
        for row in feedback_rows:
            if row is not None:
                apply_server_feedback(memory, row)
            action, _, _ = phase_two_action(model, Path("unused.png"), memory)
            live_actions.append(action)

        replay_sequence = [
            (0.95, 0.9, None),
            (0.1, 0.1, Phase2Feedback("Lateral", True, True)),
            (0.95, 0.9, Phase2Feedback("Medial", True, True)),
            (0.94, 0.87, Phase2Feedback("Observe", True, True)),
        ]
        _, offline = replay_phase2(replay_sequence, config())
        self.assertEqual([decision.action for decision in offline], live_actions)


if __name__ == "__main__":
    unittest.main()
