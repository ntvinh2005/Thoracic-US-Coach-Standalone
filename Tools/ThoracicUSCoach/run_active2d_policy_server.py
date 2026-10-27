from __future__ import annotations

import argparse
import json
import os
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np

from active2d_contract import (
    CONTRACT,
    FEEDBACK_CONTRACT,
    LEVEL_ORDER,
    project_root,
    validate_actor_request,
    validate_checkpoint,
)
from active2d_action import (
    ALLOWED_ACTIONS as U08_ALLOWED_ACTIONS,
    file_sha256,
    predict_action_values,
    validate_action_model,
)
from active2d_data import convolutional_features, model_probabilities
from active2d_handoff import (ImageDerivedHandoffState, freeze_handoff, handoff_ready,
                              phase_two_prefix, update_handoff_state)
from active2d_stop import (
    convolutional_features_v2,
    request_stop_features,
    quality_predictions,
    stop_probabilities as binary_probabilities,
    validate_stop_checkpoint,
)
from phase2_stop_state import (
    Phase2DecisionConfig,
    Phase2DecisionState,
    Phase2Feedback,
    apply_execution_feedback as apply_phase2_state_feedback,
    decide_phase2,
    register_issued_action,
)
from train_phase1_localizer import LEVEL_TO_INDEX, transition_matrix


U08_SELECTED_CHECKPOINT_SHA256 = "6fc38218be910aaeb1ca8af143e4f8785aed1e8483edbe91e9cf2a5212520c26"
U08_SELECTED_CONFIGURATION = "spatial_v2_random_relu32_ridge"
U08_SELECTED_SEED = 47


@dataclass
class PolicyMemory:
    episode_id: str
    belief: np.ndarray = field(default_factory=lambda: np.full(len(LEVEL_ORDER), 1.0 / len(LEVEL_ORDER)))
    phase: str = "LongitudinalLocalization"
    last_action: str = "RESET"
    previous_image: Path | None = None
    phase_two_image_history: list[Path] = field(default_factory=list)
    phase_two_action_history: list[str] = field(default_factory=list)
    phase_two_accepted_history: list[bool] = field(default_factory=list)
    phase_two_completed_history: list[bool] = field(default_factory=list)
    step: int = 0
    handoff_probe_active: bool = False
    handoff_return_pending: bool = False
    handoff_probe_direction: str = "Caudal"
    handoff_probe_best: float = 0.0
    scan_direction: str = "Caudal"
    repeated_observation_count: int = 0
    previous_features: np.ndarray | None = None
    previous_predicted_level: str = ""
    last_anatomic_level: str = ""
    steps_in_direction: int = 0
    phase_two_step: int = 0
    phase_two_position_initialized: bool = False
    phase_two_lateral_index: int = 0
    phase_two_longitudinal_index: int = 0
    phase_two_best_lateral_index: int = 0
    phase_two_best_longitudinal_index: int = 0
    phase_two_best_score: float = -1.0
    phase_two_best_frozen: bool = False
    handoff_state: ImageDerivedHandoffState = field(default_factory=ImageDerivedHandoffState)
    phase_two_prefix_actions: tuple[str, ...] | None = None
    phase_two_prefix_step: int = 0
    pending_action: str = ""
    pending_request_id: str = ""
    pending_phase_two_plan: str = ""
    candidate_enabled: bool = False
    candidate_selected: bool = False
    candidate_medial_value: float = 0.0
    candidate_lateral_value: float = 0.0
    candidate_fallback: bool = False
    candidate_fallback_reason: str = ""
    execution_feedback_applied: bool = False
    execution_feedback_action: str = ""
    execution_feedback_accepted: bool = False
    execution_feedback_completed: bool = False
    last_action_accepted: bool = False
    last_action_completed: bool = False
    phase_two_state: Phase2DecisionState = field(default_factory=Phase2DecisionState)


def atomic_write(path: Path, payload: dict[str, Any]) -> None:
    temporary = path.with_name(f"{path.stem}_{time.time_ns()}.tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    last_error: OSError | None = None
    for _ in range(20):
        try:
            os.replace(temporary, path)
            return
        except (PermissionError, OSError) as error:
            last_error = error
            time.sleep(0.005)
    temporary.unlink(missing_ok=True)
    if last_error is not None:
        raise last_error


def load_stop_checkpoint(path: Path, threshold_override: float | None = None) -> dict[str, Any]:
    model = json.loads(path.read_text(encoding="utf-8"))
    validate_stop_checkpoint(model, require_deployable=True)
    if threshold_override is not None:
        if not 0.0 <= threshold_override <= 1.0:
            raise ValueError("--stop-threshold must be in [0, 1]")
        stored = float(model["stopRule"]["threshold"])
        if int(model.get("version", 1)) >= 2 and not np.isclose(threshold_override, stored):
            raise ValueError("Versioned STOP checkpoints forbid deployment threshold overrides")
        model["stopRule"]["threshold"] = float(threshold_override)
    return model


def phase_one_action(model: dict[str, Any], image_path: Path, memory: PolicyMemory) -> tuple[str, float, float]:
    features = convolutional_features(image_path)
    landmark_model = model.get("_u07LandmarkModel")
    if landmark_model is not None:
        update_handoff_state(landmark_model, features, memory.previous_features, memory.last_action,
                             memory.step, memory.handoff_state)
    emission = model_probabilities(model, features.reshape(1, -1))[0].astype(np.float64)
    shift = float(model["temporalFilter"]["transitionShiftPerLongitudinalStep"])
    prior = memory.belief @ transition_matrix(memory.last_action.upper(), shift)
    prior = 0.70 * prior + 0.30 / len(prior)
    posterior = prior * np.maximum(emission, 0.01)
    posterior /= posterior.sum()
    memory.belief = posterior
    t7_probability = float(posterior[LEVEL_TO_INDEX["T7"]])
    handoff_probability = float(binary_probabilities(
        {"architecture": model["handoffHead"]}, features
    )[0])
    threshold = float(model["temporalFilter"]["handoffThreshold"])
    uncertainty = float(-np.sum(posterior * np.log(np.maximum(posterior, 1e-9))) / np.log(len(posterior)))
    observation_change = float("inf") if memory.previous_features is None else float(
        np.sqrt(np.mean((features - memory.previous_features) ** 2))
    )
    u07_gate = model.get("_u07Gate", {})
    landmark_ready = landmark_model is None or handoff_ready(
        memory.handoff_state,
        min_confidence=float(u07_gate.get("minConfidence", 0.70)),
        max_uncertainty=float(u07_gate.get("maxUncertainty", 0.55)),
        min_landmark_events=int(u07_gate.get("minLandmarkEvents", 1)),
    )
    opposite = lambda action: "Cranial" if action == "Caudal" else "Caudal"
    if memory.handoff_return_pending:
        # The previous action physically returned to the best image-observed
        # point after crossing the local T7-probability peak.
        memory.handoff_return_pending = False
        if handoff_probability >= threshold and landmark_ready:
            memory.phase = "LateralAcquisition"
            freeze_handoff(memory.handoff_state)
            action = "PhaseHandoff"
        else:
            # Curved-surface inverse moves are not guaranteed to return to the
            # same image. Never hand off unless the returned image itself still
            # passes the frozen image-only threshold.
            memory.scan_direction = opposite(memory.handoff_probe_direction)
            memory.steps_in_direction = 0
            action = memory.scan_direction
    elif memory.handoff_probe_active:
        if observation_change < 0.005 and handoff_probability >= threshold and landmark_ready:
            # A workspace/contact boundary left the observation unchanged;
            # hand off at the last reachable high-confidence image.
            memory.phase = "LateralAcquisition"
            freeze_handoff(memory.handoff_state)
            action = "PhaseHandoff"
            memory.handoff_probe_active = False
        elif handoff_probability >= threshold and landmark_ready and handoff_probability >= memory.handoff_probe_best - 0.01:
            memory.handoff_probe_best = max(memory.handoff_probe_best, handoff_probability)
            action = memory.handoff_probe_direction
        else:
            action = opposite(memory.handoff_probe_direction)
            memory.handoff_probe_active = False
            memory.handoff_return_pending = True
    elif handoff_probability >= threshold and landmark_ready:
        # Do not hand off at the first high-probability edge of the T7 band.
        # Walk through the image-only probability peak, then physically return
        # one step. This replaces the old rotation confirmation that could
        # confirm a visually T7-like frame while still too far from its center.
        memory.handoff_probe_active = True
        memory.handoff_probe_direction = memory.scan_direction
        memory.handoff_probe_best = handoff_probability
        action = memory.handoff_probe_direction
    else:
        # Direction is inferred from the observed level sequence, not assumed
        # from hidden scene geometry. Local surface orientation can invert the
        # nominal world-down axis, so crossing from an anatomic class into
        # OUTSIDE means the attempted direction was wrong and must reverse.
        predicted = LEVEL_ORDER[int(np.argmax(emission))]
        crossed_boundary = (
            predicted == "OUTSIDE_T6_T8"
            and memory.previous_predicted_level not in {"", "OUTSIDE_T6_T8"}
        )
        if crossed_boundary or memory.steps_in_direction >= 40:
            memory.scan_direction = "Cranial" if memory.scan_direction == "Caudal" else "Caudal"
            memory.steps_in_direction = 0
        if predicted != "OUTSIDE_T6_T8":
            memory.last_anatomic_level = predicted
        action = memory.scan_direction
        memory.steps_in_direction += 1
        if memory.previous_features is not None:
            memory.repeated_observation_count = memory.repeated_observation_count + 1 if observation_change < 0.015 else 0
        if memory.repeated_observation_count >= 3:
            memory.scan_direction = "Cranial" if memory.scan_direction == "Caudal" else "Caudal"
            memory.steps_in_direction = 0
            memory.repeated_observation_count = 0
            action = memory.scan_direction
        memory.previous_predicted_level = predicted
    memory.previous_features = features
    return action, handoff_probability, uncertainty


def _build_phase_two_raster() -> tuple[str, ...]:
    # Move to one longitudinal edge at row center, then cover the entire
    # -24..+24 mm by ±15 mm region at 3 mm resolution with a serpentine scan.
    # The narrower lateral edge keeps inter-row moves valid on the curved skin.
    actions = ["Cranial"] * 8 + ["Lateral"] * 5
    for row in range(17):
        actions.extend((["Medial"] if row % 2 == 0 else ["Lateral"]) * 10)
        if row < 16:
            actions.append("Caudal")
    return tuple(actions)


PHASE_TWO_RASTER_ACTIONS = _build_phase_two_raster()


def phase_two_raster_action(step: int) -> str:
    if step < len(PHASE_TWO_RASTER_ACTIONS):
        return PHASE_TWO_RASTER_ACTIONS[step]
    # Keep searching deterministically if Phase 1 leaves extra budget.
    tail = step - len(PHASE_TWO_RASTER_ACTIONS)
    row = ("Lateral",) * 5 + ("Medial",) * 10 + ("Lateral",) * 5
    return row[tail % len(row)]


def reset_candidate_telemetry(memory: PolicyMemory, enabled: bool) -> None:
    memory.candidate_enabled = enabled
    memory.candidate_selected = False
    memory.candidate_medial_value = 0.0
    memory.candidate_lateral_value = 0.0
    memory.candidate_fallback = False
    memory.candidate_fallback_reason = ""


def apply_execution_feedback(memory: PolicyMemory, request: dict[str, Any]) -> None:
    """Apply versioned actuator feedback to every Phase-2 policy mode."""
    memory.execution_feedback_applied = False
    if not request.get("hasPreviousActionFeedback", False):
        if memory.pending_action:
            raise RuntimeError("Phase-2 v2 requires previous actuator execution feedback")
        return
    previous = str(request.get("previousAction", "")).replace("_", "").upper()
    accepted = bool(request.get("previousActionAccepted", False))
    completed = bool(request.get("previousActionCompleted", False))
    if completed and not accepted:
        raise RuntimeError("Actuator feedback cannot be completed but rejected")
    issued_action = memory.pending_action
    if issued_action:
        expected = memory.pending_action.replace("_", "").upper()
        if previous != expected:
            raise RuntimeError(f"Actuator feedback action mismatch: expected {expected}, received {previous}")
        if not memory.phase_two_state.pending_action:
            legacy_plan = "explore" if memory.pending_phase_two_plan == "raster" else memory.pending_phase_two_plan
            register_issued_action(memory.phase_two_state, memory.pending_action, legacy_plan or "explore")
        apply_phase2_state_feedback(
            memory.phase_two_state,
            Phase2Feedback(previous, accepted=accepted, completed=completed),
        )
        memory.pending_action = ""
        memory.pending_request_id = ""
        memory.pending_phase_two_plan = ""
    memory.last_action = (issued_action or previous) if accepted and completed else "RESET"
    memory.last_action_accepted = accepted
    memory.last_action_completed = completed
    state = memory.phase_two_state
    memory.phase_two_prefix_step = state.prefix_step
    memory.phase_two_step = state.exploration_step
    memory.phase_two_lateral_index = state.lateral_index
    memory.phase_two_longitudinal_index = state.longitudinal_index
    memory.execution_feedback_applied = True
    memory.execution_feedback_action = previous
    memory.execution_feedback_accepted = accepted
    memory.execution_feedback_completed = completed


def register_phase_two_action(memory: PolicyMemory, action: str, plan: str) -> None:
    register_issued_action(memory.phase_two_state, action, plan)
    memory.pending_action = action
    memory.pending_phase_two_plan = plan


def choose_u08_action(
    raster_action: str,
    image_path: Path,
    action_model: dict[str, Any],
    memory: PolicyMemory,
) -> str:
    """Select only between the two factual U08 response heads.

    The frozen pilot rule is argmax predicted score delta.  It has no trained
    support for longitudinal movement, prefix collection, return-to-best, or
    terminal actions, so all of those remain explicit raster fallbacks.
    """
    normalized = raster_action.upper()
    if normalized not in U08_ALLOWED_ACTIONS:
        memory.candidate_fallback = True
        memory.candidate_fallback_reason = f"unsupported_action:{normalized}"
        return raster_action
    try:
        features = convolutional_features_v2(image_path)
        values = predict_action_values(action_model, features)[0]
        medial, lateral = float(values[0]), float(values[1])
    except Exception as error:
        memory.candidate_fallback = True
        memory.candidate_fallback_reason = f"prediction_error:{type(error).__name__}"
        return raster_action
    memory.candidate_medial_value = medial
    memory.candidate_lateral_value = lateral
    if not np.isfinite(medial) or not np.isfinite(lateral):
        memory.candidate_fallback = True
        memory.candidate_fallback_reason = "non_finite_prediction"
        return raster_action
    if medial == lateral:
        memory.candidate_fallback = True
        memory.candidate_fallback_reason = "prediction_tie"
        return raster_action
    memory.candidate_selected = True
    return "Medial" if medial > lateral else "Lateral"


def phase_two_action(
    stop_model: dict[str, Any] | None,
    image_path: Path,
    memory: PolicyMemory,
    action_model: dict[str, Any] | None = None,
) -> tuple[str, float, float]:
    reset_candidate_telemetry(memory, action_model is not None)
    if memory.phase_two_prefix_actions is None:
        memory.phase_two_prefix_actions = phase_two_prefix(memory.handoff_state, memory.scan_direction)
    if stop_model is None:
        state = memory.phase_two_state
        if state.prefix_step < len(memory.phase_two_prefix_actions):
            action = memory.phase_two_prefix_actions[state.prefix_step]
            plan = "prefix"
            memory.candidate_fallback = action_model is not None
            memory.candidate_fallback_reason = f"prefix_action:{action.upper()}" if action_model is not None else ""
        else:
            raster = "Lateral" if (state.exploration_step // 12) % 2 == 0 else "Medial"
            action = choose_u08_action(raster, image_path, action_model, memory) if action_model is not None else raster
            plan = "explore"
        register_phase_two_action(memory, action, plan)
        return action, 0.0, 1.0
    extractor = str(stop_model.get("architecture", {}).get("featureExtractor", "history-v1"))
    features = request_stop_features(
        image_path,
        memory.previous_image,
        memory.last_action,
        extractor,
        memory.last_action_accepted,
        memory.last_action_completed,
        memory.phase_two_image_history,
        memory.phase_two_action_history,
        memory.phase_two_accepted_history,
        memory.phase_two_completed_history,
    )
    score = float(binary_probabilities(stop_model, features)[0])
    quality = float(quality_predictions(stop_model, features)[0])
    threshold = float(stop_model["stopRule"]["threshold"])
    exploration_limit = int(stop_model.get("_diagnosticExplorationLimit", len(PHASE_TWO_RASTER_ACTIONS)))
    exploration_actions = PHASE_TWO_RASTER_ACTIONS[:exploration_limit]
    decision = decide_phase2(
        memory.phase_two_state,
        Phase2DecisionConfig(
            prefix_actions=memory.phase_two_prefix_actions,
            exploration_actions=exploration_actions,
            stop_threshold=threshold,
            required_confirmations=int(stop_model.get("stopRule", {}).get("requiredConfirmations", 1)),
        ),
        stop_score=score,
        quality_score=quality,
    )
    state = memory.phase_two_state
    memory.phase_two_position_initialized = True
    memory.phase_two_prefix_step = state.prefix_step
    memory.phase_two_step = state.exploration_step
    memory.phase_two_lateral_index = state.lateral_index
    memory.phase_two_longitudinal_index = state.longitudinal_index
    memory.phase_two_best_lateral_index = state.best_lateral_index
    memory.phase_two_best_longitudinal_index = state.best_longitudinal_index
    memory.phase_two_best_score = state.best_stop_score
    memory.phase_two_best_frozen = state.stage in {"RETURN", "CONFIRM", "STOPPED", "ABSTAIN"}
    probability = min(max(score, 1e-9), 1.0 - 1e-9)
    uncertainty = float(-(probability * np.log(probability) + (1.0 - probability) * np.log(1.0 - probability)) / np.log(2.0))
    if decision.action is None:
        memory.candidate_fallback = True
        memory.candidate_fallback_reason = f"safe_abstain:{decision.reason}"
        return "Abstain", score, uncertainty
    action = decision.action
    if action_model is not None and decision.plan == "explore":
        action = choose_u08_action(action, image_path, action_model, memory)
    elif action_model is not None and decision.plan in {"prefix", "return", "confirm"}:
        memory.candidate_fallback = True
        memory.candidate_fallback_reason = f"state_machine_{decision.plan}"
    if action not in {"Stop", "Abstain"}:
        register_phase_two_action(memory, action, decision.plan)
    return action, score, uncertainty


def act(
    model: dict[str, Any],
    stop_model: dict[str, Any] | None,
    image_path: Path,
    memory: PolicyMemory,
    action_model: dict[str, Any] | None = None,
) -> tuple[str, float, float]:
    if memory.phase == "LongitudinalLocalization":
        reset_candidate_telemetry(memory, False)
        return phase_one_action(model, image_path, memory)
    return phase_two_action(stop_model, image_path, memory, action_model)


def main() -> None:
    parser = argparse.ArgumentParser(description="Restricted image-only Active 2D policy bridge")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--stop-checkpoint", type=Path)
    parser.add_argument(
        "--phase2-action-checkpoint",
        type=Path,
        help="Optional exact U08 spatial-v2 seed-47 checkpoint; omission preserves the raster baseline",
    )
    parser.add_argument(
        "--phase-two-only-pilot",
        action="store_true",
        help="Start each opaque episode at an evaluator-established Phase-1 handoff; U08-P1 batch use only",
    )
    parser.add_argument(
        "--phase2-exploration-limit",
        type=int,
        help="Development-smoke-only prefix length for the frozen raster before return/abstention",
    )
    parser.add_argument("--landmark-checkpoint", type=Path,
                        help="Optional U06 image-derived landmark model enabling the U07 explicit handoff gate")
    parser.add_argument(
        "--handoff-threshold",
        type=float,
        help="Frozen Phase-1 image threshold override (record this in the evaluation command)",
    )
    parser.add_argument(
        "--stop-threshold",
        type=float,
        help="Frozen deployment threshold override (record this in the evaluation command)",
    )
    parser.add_argument("--bridge", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/bridge")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    validate_checkpoint(model)
    if args.landmark_checkpoint is not None:
        landmark_model = json.loads(args.landmark_checkpoint.read_text(encoding="utf-8"))
        if landmark_model.get("taskId") != "U06" or landmark_model.get("policyInputs") != [
            "cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"
        ]:
            raise ValueError("U07 requires a validated U06 image-only landmark checkpoint")
        model["_u07LandmarkModel"] = landmark_model
        model["_u07Gate"] = {"minConfidence": 0.70, "maxUncertainty": 0.55, "minLandmarkEvents": 1}
    if args.handoff_threshold is not None:
        if not 0.0 <= args.handoff_threshold <= 1.0:
            raise ValueError("--handoff-threshold must be in [0, 1]")
        model["temporalFilter"]["handoffThreshold"] = float(args.handoff_threshold)
    stop_model = None
    if args.stop_checkpoint is not None:
        stop_model = load_stop_checkpoint(args.stop_checkpoint, args.stop_threshold)
        if args.phase2_exploration_limit is not None:
            if not 1 <= args.phase2_exploration_limit <= len(PHASE_TWO_RASTER_ACTIONS):
                raise ValueError("--phase2-exploration-limit is outside the frozen raster length")
            stop_model["_diagnosticExplorationLimit"] = int(args.phase2_exploration_limit)
    action_model = None
    if args.phase2_action_checkpoint is not None:
        checkpoint_hash = file_sha256(args.phase2_action_checkpoint)
        if checkpoint_hash.lower() != U08_SELECTED_CHECKPOINT_SHA256:
            raise ValueError(
                "U08 pilot requires the exact selected checkpoint: "
                f"expected {U08_SELECTED_CHECKPOINT_SHA256}, received {checkpoint_hash.lower()}"
            )
        action_model = json.loads(args.phase2_action_checkpoint.read_text(encoding="utf-8"))
        validate_action_model(action_model)
        architecture = action_model.get("architecture", {})
        if (
            architecture.get("featureMode") != "spatial-v2"
            or int(architecture.get("seed", -1)) != U08_SELECTED_SEED
            or action_model.get("training", {}).get("configurationId") != U08_SELECTED_CONFIGURATION
        ):
            raise ValueError("U08 pilot checkpoint is not the selected spatial-v2 seed-47 configuration")

    args.bridge.mkdir(parents=True, exist_ok=True)
    request_path = args.bridge / "active2d_request.json"
    response_path = args.bridge / "active2d_response.json"
    handled: str | None = None
    memory: PolicyMemory | None = None
    print(f"Watching restricted image-only request {request_path}", flush=True)
    while True:
        if request_path.exists():
            try:
                request = json.loads(request_path.read_text(encoding="utf-8"))
                validate_actor_request(request)
            except (OSError, json.JSONDecodeError, ValueError):
                time.sleep(0.02)
                continue
            request_id = request["requestId"]
            if request_id and request_id != handled:
                episode_id = request["episodeId"]
                if memory is None or request["reset"] or memory.episode_id != episode_id:
                    memory = PolicyMemory(
                        episode_id=episode_id,
                        phase="LateralAcquisition" if args.phase_two_only_pilot else "LongitudinalLocalization",
                    )
                feedback_error: Exception | None = None
                memory.execution_feedback_applied = False
                memory.execution_feedback_action = ""
                memory.execution_feedback_accepted = False
                memory.execution_feedback_completed = False
                if request.get("contract") == FEEDBACK_CONTRACT and not request["reset"]:
                    try:
                        apply_execution_feedback(memory, request)
                    except Exception as caught:
                        feedback_error = caught
                elif memory.pending_action and not request["reset"]:
                    feedback_error = RuntimeError(
                        "Pending Phase-2 action requires active2d_image_only_v2 feedback"
                    )
                image_path = Path(request["imagePath"])
                started = time.perf_counter()
                error = ""
                phase_before_action = memory.phase
                try:
                    if feedback_error is not None:
                        raise feedback_error
                    action, confidence, uncertainty = act(
                        model, stop_model, image_path, memory, action_model
                    )
                except Exception as caught:  # bridge must return an explicit terminal error
                    action, confidence, uncertainty = "Stop", 0.0, 1.0
                    error = f"{type(caught).__name__}: {caught}"
                response = {
                    "schemaVersion": request["schemaVersion"],
                    "contract": request["contract"],
                    "requestId": request_id,
                    "episodeId": episode_id,
                    "action": action,
                    "phase": memory.phase,
                    "confidence": confidence,
                    "uncertainty": uncertainty,
                    "memoryStep": memory.step,
                    "model": model.get("architecture", {}).get("type", "unknown"),
                    "latencyMs": (time.perf_counter() - started) * 1000.0,
                    "error": error,
                    "handoffState": memory.handoff_state.public_summary(),
                    "candidateEnabled": memory.candidate_enabled,
                    "candidateSelected": memory.candidate_selected,
                    "candidateMedialValue": memory.candidate_medial_value,
                    "candidateLateralValue": memory.candidate_lateral_value,
                    "candidateFallback": memory.candidate_fallback,
                    "candidateFallbackReason": memory.candidate_fallback_reason,
                    "executionFeedbackApplied": memory.execution_feedback_applied,
                    "executionFeedbackAction": memory.execution_feedback_action,
                    "executionFeedbackAccepted": memory.execution_feedback_accepted,
                    "executionFeedbackCompleted": memory.execution_feedback_completed,
                    "phaseTwoActionModel": U08_SELECTED_CONFIGURATION if action_model is not None else "",
                }
                atomic_write(response_path, response)
                if phase_before_action != "LongitudinalLocalization":
                    memory.phase_two_image_history.append(image_path)
                    memory.phase_two_action_history.append(memory.last_action)
                    memory.phase_two_accepted_history.append(memory.last_action_accepted)
                    memory.phase_two_completed_history.append(memory.last_action_completed)
                    memory.phase_two_image_history = memory.phase_two_image_history[-4:]
                    memory.phase_two_action_history = memory.phase_two_action_history[-4:]
                    memory.phase_two_accepted_history = memory.phase_two_accepted_history[-4:]
                    memory.phase_two_completed_history = memory.phase_two_completed_history[-4:]
                memory.previous_image = image_path
                if phase_before_action == "LongitudinalLocalization" or action in {"Stop", "Abstain"}:
                    memory.last_action = action
                    memory.last_action_accepted = True
                    memory.last_action_completed = True
                elif memory.pending_action and not error:
                    memory.pending_request_id = request_id
                memory.step += 1
                handled = request_id
                if args.once:
                    return
        time.sleep(0.02)


if __name__ == "__main__":
    main()
