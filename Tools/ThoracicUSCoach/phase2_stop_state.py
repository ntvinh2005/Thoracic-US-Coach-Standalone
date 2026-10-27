from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable


MOVEMENT_DELTAS = {
    "LATERAL": (1, 0, 0),
    "MEDIAL": (-1, 0, 0),
    "CAUDAL": (0, 1, 0),
    "CRANIAL": (0, -1, 0),
    "ROTATECLOCKWISE": (0, 0, 1),
    "ROTATECOUNTERCLOCKWISE": (0, 0, -1),
}


def normalized_action(action: str) -> str:
    return action.replace("_", "").upper()


@dataclass(frozen=True)
class Phase2Feedback:
    action: str
    accepted: bool
    completed: bool


@dataclass(frozen=True)
class Phase2Decision:
    action: str | None
    plan: str
    stage: str
    reason: str = ""

    @property
    def terminal(self) -> bool:
        return self.action in {"Stop", None}


@dataclass
class Phase2DecisionState:
    stage: str = "PREFIX"
    prefix_step: int = 0
    exploration_step: int = 0
    lateral_index: int = 0
    longitudinal_index: int = 0
    yaw_index: int = 0
    best_lateral_index: int = 0
    best_longitudinal_index: int = 0
    best_yaw_index: int = 0
    best_quality: float = float("-inf")
    best_stop_score: float = float("-inf")
    best_observation_index: int = -1
    observations: int = 0
    pending_action: str = ""
    pending_plan: str = ""
    issued_actions: int = 0
    accepted_actions: int = 0
    completed_actions: int = 0
    rejected_actions: int = 0
    incomplete_actions: int = 0
    return_attempts: int = 0
    return_failures: int = 0
    confirmation_count: int = 0
    confirmation_requests: int = 0
    outcome: str = ""
    reason: str = ""


@dataclass(frozen=True)
class Phase2DecisionConfig:
    prefix_actions: tuple[str, ...]
    exploration_actions: tuple[str, ...]
    stop_threshold: float
    required_confirmations: int = 1
    max_return_attempts: int = 64
    max_return_failures: int = 6
    max_confirmation_quality_drop: float = 0.05
    remember_prefix_evidence: bool = False

    def __post_init__(self) -> None:
        if not 0.0 <= self.stop_threshold <= 1.0:
            raise ValueError("stop_threshold must be in [0, 1]")
        if self.required_confirmations < 1:
            raise ValueError("required_confirmations must be positive")
        if self.max_return_attempts < 1 or self.max_return_failures < 1:
            raise ValueError("return limits must be positive")


def register_issued_action(state: Phase2DecisionState, action: str, plan: str) -> None:
    if state.pending_action:
        raise RuntimeError("Cannot issue a Phase-2 action while feedback is pending")
    if action.upper() == "STOP":
        return
    state.pending_action = action
    state.pending_plan = plan
    state.issued_actions += 1


def apply_execution_feedback(
    state: Phase2DecisionState,
    feedback: Phase2Feedback,
) -> None:
    if not state.pending_action:
        raise RuntimeError("Phase-2 feedback arrived without a pending action")
    actual = normalized_action(feedback.action)
    expected = normalized_action(state.pending_action)
    if actual != expected:
        raise RuntimeError(f"Phase-2 feedback mismatch: expected {expected}, received {actual}")
    if feedback.completed and not feedback.accepted:
        raise RuntimeError("A completed Phase-2 action cannot be rejected")

    if state.pending_plan == "prefix":
        state.prefix_step += 1
    elif state.pending_plan == "explore":
        # Acknowledged attempts consume the bounded schedule, but only verified
        # full execution advances physical odometry.
        state.exploration_step += 1
    elif state.pending_plan == "return":
        state.return_attempts += 1

    if feedback.accepted:
        state.accepted_actions += 1
    else:
        state.rejected_actions += 1
    if feedback.accepted and feedback.completed:
        state.completed_actions += 1
        dx, dy, dyaw = MOVEMENT_DELTAS.get(actual, (0, 0, 0))
        state.lateral_index += dx
        state.longitudinal_index += dy
        state.yaw_index += dyaw
    elif feedback.accepted:
        state.incomplete_actions += 1

    if state.pending_plan == "return" and not (feedback.accepted and feedback.completed):
        state.return_failures += 1
    if state.pending_plan == "confirm" and not (feedback.accepted and feedback.completed):
        state.stage = "ABSTAIN"
        state.outcome = "TERMINAL_REJECTION"
        state.reason = "fresh-confirmation-action-rejected-or-incomplete"

    state.pending_action = ""
    state.pending_plan = ""


def _return_action(state: Phase2DecisionState) -> str | None:
    lateral_error = state.best_lateral_index - state.lateral_index
    longitudinal_error = state.best_longitudinal_index - state.longitudinal_index
    yaw_error = state.best_yaw_index - state.yaw_index
    if lateral_error > 0:
        return "Lateral"
    if lateral_error < 0:
        return "Medial"
    if longitudinal_error > 0:
        return "Caudal"
    if longitudinal_error < 0:
        return "Cranial"
    if yaw_error > 0:
        return "RotateClockwise"
    if yaw_error < 0:
        return "RotateCounterClockwise"
    return None


def decide_phase2(
    state: Phase2DecisionState,
    config: Phase2DecisionConfig,
    *,
    stop_score: float,
    quality_score: float,
) -> Phase2Decision:
    """Make one image-only Phase-2 decision.

    Quality ranks locations only after STOP evidence makes them eligible. It
    never establishes target acceptability by itself. The `Observe` control
    action requests a separate Unity render at the recovered location before
    an explicit STOP can be returned.
    """
    if state.pending_action:
        raise RuntimeError("Phase-2 decision requested before actuator feedback")
    if state.stage in {"STOPPED", "ABSTAIN"}:
        return Phase2Decision(None, "terminal", state.stage, state.reason)
    if not 0.0 <= stop_score <= 1.0 or not 0.0 <= quality_score <= 1.0:
        raise ValueError("Phase-2 model scores must be in [0, 1]")

    observation_index = state.observations
    state.observations += 1

    def remember_current_observation() -> None:
        if stop_score < config.stop_threshold or quality_score <= state.best_quality:
            return
        state.best_quality = quality_score
        state.best_stop_score = stop_score
        state.best_lateral_index = state.lateral_index
        state.best_longitudinal_index = state.longitudinal_index
        state.best_yaw_index = state.yaw_index
        state.best_observation_index = observation_index

    if state.stage == "PREFIX":
        # The diagnostic engineering path may preserve eligible image-only
        # evidence seen at the physically visited prefix pose.  The default is
        # false, leaving the frozen scientific behavior unchanged.
        if config.remember_prefix_evidence:
            remember_current_observation()
        if state.prefix_step < len(config.prefix_actions):
            return Phase2Decision(config.prefix_actions[state.prefix_step], "prefix", state.stage)
        state.stage = "EXPLORE"

    if state.stage == "EXPLORE":
        remember_current_observation()
        if state.exploration_step < len(config.exploration_actions):
            return Phase2Decision(
                config.exploration_actions[state.exploration_step], "explore", state.stage
            )
        if state.best_observation_index < 0:
            state.stage = "ABSTAIN"
            state.outcome = "TARGET_NOT_RECOGNIZED"
            state.reason = "exploration-complete-without-stop-eligible-observation"
            return Phase2Decision(None, "terminal", state.stage, state.reason)
        state.stage = "RETURN"

    if state.stage == "RETURN":
        if (
            state.return_attempts >= config.max_return_attempts
            or state.return_failures >= config.max_return_failures
        ):
            state.stage = "ABSTAIN"
            state.outcome = "TARGET_RECOGNIZED_NOT_RECOVERED"
            state.reason = "bounded-return-recovery-exhausted"
            return Phase2Decision(None, "terminal", state.stage, state.reason)
        action = _return_action(state)
        if action is not None:
            return Phase2Decision(action, "return", state.stage)
        state.stage = "CONFIRM"
        state.confirmation_requests += 1
        return Phase2Decision("Observe", "confirm", state.stage, "fresh-image-required")

    if state.stage == "CONFIRM":
        evidence_valid = stop_score >= config.stop_threshold
        ranking_valid = quality_score >= state.best_quality - config.max_confirmation_quality_drop
        if not evidence_valid or not ranking_valid:
            state.stage = "ABSTAIN"
            state.outcome = "FRESH_CONFIRMATION_FAILED"
            state.reason = (
                "fresh-stop-evidence-below-threshold" if not evidence_valid
                else "fresh-quality-no-longer-matches-recovered-best"
            )
            return Phase2Decision(None, "terminal", state.stage, state.reason)
        state.confirmation_count += 1
        if state.confirmation_count >= config.required_confirmations:
            state.stage = "STOPPED"
            state.outcome = "EXPLICIT_STOP"
            state.reason = "fresh-image-confirmed"
            return Phase2Decision("Stop", "terminal", state.stage, state.reason)
        state.confirmation_requests += 1
        return Phase2Decision("Observe", "confirm", state.stage, "additional-confirmation-required")

    raise RuntimeError(f"Unsupported Phase-2 decision stage: {state.stage}")


def replay_phase2(
    observations: Iterable[tuple[float, float, Phase2Feedback | None]],
    config: Phase2DecisionConfig,
) -> tuple[Phase2DecisionState, list[Phase2Decision]]:
    """Replay the same state transition function used by live inference."""
    state = Phase2DecisionState()
    decisions: list[Phase2Decision] = []
    for stop_score, quality_score, feedback in observations:
        if state.pending_action:
            if feedback is None:
                raise RuntimeError("Offline replay is missing required execution feedback")
            apply_execution_feedback(state, feedback)
        elif feedback is not None:
            raise RuntimeError("Offline replay supplied feedback without a pending action")
        decision = decide_phase2(
            state,
            config,
            stop_score=float(stop_score),
            quality_score=float(quality_score),
        )
        decisions.append(decision)
        if decision.action is None or decision.action == "Stop":
            break
        register_issued_action(state, decision.action, decision.plan)
    return state, decisions
