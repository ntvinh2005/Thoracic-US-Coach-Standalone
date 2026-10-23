from __future__ import annotations

import argparse
import json
import os
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np

from active2d_contract import CONTRACT, LEVEL_ORDER, project_root, validate_actor_request, validate_checkpoint
from active2d_data import convolutional_features, model_probabilities
from active2d_stop import (
    request_stop_features,
    stop_probabilities as binary_probabilities,
    validate_stop_checkpoint,
)
from train_phase1_localizer import LEVEL_TO_INDEX, transition_matrix


@dataclass
class PolicyMemory:
    episode_id: str
    belief: np.ndarray = field(default_factory=lambda: np.full(len(LEVEL_ORDER), 1.0 / len(LEVEL_ORDER)))
    phase: str = "LongitudinalLocalization"
    last_action: str = "RESET"
    previous_image: Path | None = None
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


def phase_one_action(model: dict[str, Any], image_path: Path, memory: PolicyMemory) -> tuple[str, float, float]:
    features = convolutional_features(image_path)
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
    opposite = lambda action: "Cranial" if action == "Caudal" else "Caudal"
    if memory.handoff_return_pending:
        # The previous action physically returned to the best image-observed
        # point after crossing the local T7-probability peak.
        memory.handoff_return_pending = False
        if handoff_probability >= threshold:
            memory.phase = "LateralAcquisition"
            action = "PhaseHandoff"
        else:
            # Curved-surface inverse moves are not guaranteed to return to the
            # same image. Never hand off unless the returned image itself still
            # passes the frozen image-only threshold.
            memory.scan_direction = opposite(memory.handoff_probe_direction)
            memory.steps_in_direction = 0
            action = memory.scan_direction
    elif memory.handoff_probe_active:
        if observation_change < 0.005 and handoff_probability >= threshold:
            # A workspace/contact boundary left the observation unchanged;
            # hand off at the last reachable high-confidence image.
            memory.phase = "LateralAcquisition"
            action = "PhaseHandoff"
            memory.handoff_probe_active = False
        elif handoff_probability >= threshold and handoff_probability >= memory.handoff_probe_best - 0.01:
            memory.handoff_probe_best = max(memory.handoff_probe_best, handoff_probability)
            action = memory.handoff_probe_direction
        else:
            action = opposite(memory.handoff_probe_direction)
            memory.handoff_probe_active = False
            memory.handoff_return_pending = True
    elif handoff_probability >= threshold:
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


def phase_two_action(
    stop_model: dict[str, Any] | None,
    image_path: Path,
    memory: PolicyMemory,
) -> tuple[str, float, float]:
    if stop_model is None:
        # Explicitly a sensor-valid scripted search baseline, not a learned
        # Phase-2 success claim. It never receives target direction.
        action = "Lateral" if (memory.phase_two_step // 12) % 2 == 0 else "Medial"
        memory.phase_two_step += 1
        return action, 0.0, 1.0
    extractor = str(stop_model.get("architecture", {}).get("featureExtractor", "history-v1"))
    features = request_stop_features(image_path, memory.previous_image, memory.last_action, extractor)
    score = float(binary_probabilities(stop_model, features)[0])
    threshold = float(stop_model["stopRule"]["threshold"])
    if memory.phase_two_position_initialized:
        previous = memory.last_action.upper()
        if previous == "LATERAL":
            memory.phase_two_lateral_index += 1
        elif previous == "MEDIAL":
            memory.phase_two_lateral_index -= 1
        elif previous == "CAUDAL":
            memory.phase_two_longitudinal_index += 1
        elif previous == "CRANIAL":
            memory.phase_two_longitudinal_index -= 1
    else:
        memory.phase_two_position_initialized = True

    if not memory.phase_two_best_frozen and score > memory.phase_two_best_score:
        memory.phase_two_best_score = score
        memory.phase_two_best_lateral_index = memory.phase_two_lateral_index
        memory.phase_two_best_longitudinal_index = memory.phase_two_longitudinal_index

    if memory.phase_two_step < len(PHASE_TWO_RASTER_ACTIONS):
        # Complete the target-independent grid before making a terminal choice.
        action = phase_two_raster_action(memory.phase_two_step)
        memory.phase_two_step += 1
    elif memory.phase_two_best_score >= threshold:
        # Freeze and physically revisit the best image-observed cell using only
        # policy-owned issued-action odometry. No Unity pose or evaluator score
        # enters this return plan.
        memory.phase_two_best_frozen = True
        lateral_error = memory.phase_two_best_lateral_index - memory.phase_two_lateral_index
        longitudinal_error = memory.phase_two_best_longitudinal_index - memory.phase_two_longitudinal_index
        if lateral_error > 0:
            action = "Lateral"
        elif lateral_error < 0:
            action = "Medial"
        elif longitudinal_error > 0:
            action = "Caudal"
        elif longitudinal_error < 0:
            action = "Cranial"
        else:
            action = "Stop"
    else:
        # No image crossed the calibrated evidence floor. Keep sensing rather
        # than issuing an unsupported STOP.
        action = phase_two_raster_action(memory.phase_two_step)
        memory.phase_two_step += 1
    probability = min(max(score, 1e-9), 1.0 - 1e-9)
    uncertainty = float(-(probability * np.log(probability) + (1.0 - probability) * np.log(1.0 - probability)) / np.log(2.0))
    return action, score, uncertainty


def act(
    model: dict[str, Any],
    stop_model: dict[str, Any] | None,
    image_path: Path,
    memory: PolicyMemory,
) -> tuple[str, float, float]:
    if memory.phase == "LongitudinalLocalization":
        return phase_one_action(model, image_path, memory)
    return phase_two_action(stop_model, image_path, memory)


def main() -> None:
    parser = argparse.ArgumentParser(description="Restricted image-only Active 2D policy bridge")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--stop-checkpoint", type=Path)
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
    if args.handoff_threshold is not None:
        if not 0.0 <= args.handoff_threshold <= 1.0:
            raise ValueError("--handoff-threshold must be in [0, 1]")
        model["temporalFilter"]["handoffThreshold"] = float(args.handoff_threshold)
    stop_model = None
    if args.stop_checkpoint is not None:
        stop_model = json.loads(args.stop_checkpoint.read_text(encoding="utf-8"))
        validate_stop_checkpoint(stop_model)
        if args.stop_threshold is not None:
            if not 0.0 <= args.stop_threshold <= 1.0:
                raise ValueError("--stop-threshold must be in [0, 1]")
            stop_model["stopRule"]["threshold"] = float(args.stop_threshold)

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
                    memory = PolicyMemory(episode_id=episode_id)
                image_path = Path(request["imagePath"])
                started = time.perf_counter()
                error = ""
                try:
                    action, confidence, uncertainty = act(model, stop_model, image_path, memory)
                except Exception as caught:  # bridge must return an explicit terminal error
                    action, confidence, uncertainty = "Stop", 0.0, 1.0
                    error = f"{type(caught).__name__}: {caught}"
                response = {
                    "schemaVersion": 1,
                    "contract": CONTRACT,
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
                }
                atomic_write(response_path, response)
                memory.previous_image = image_path
                memory.last_action = action
                memory.step += 1
                handled = request_id
                if args.once:
                    return
        time.sleep(0.02)


if __name__ == "__main__":
    main()
