from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

import numpy as np

from active2d_contract import LEVEL_ORDER
from active2d_data import softmax
from landmark_mapping import ACTION_FEATURE_ORDER, ACTION_TO_INDEX, EVENT_ORDER


@dataclass
class LandmarkObservation:
    step: int
    event: str
    confidence: float
    longitudinal_index: int
    lateral_index: int


@dataclass
class ImageDerivedHandoffState:
    level_belief: np.ndarray = field(default_factory=lambda: np.full(len(LEVEL_ORDER), 1.0 / len(LEVEL_ORDER)))
    estimated_level: str = "UNKNOWN"
    relative_landmark_identity: str = "UNMATCHED"
    localization_confidence: float = 0.0
    uncertainty: float = 1.0
    longitudinal_index: int = 0
    lateral_index: int = 0
    visited_regions: dict[str, int] = field(default_factory=dict)
    landmark_evidence: list[LandmarkObservation] = field(default_factory=list)
    promising_search_region: str = "longitudinal-relocalization"
    abstain_reason: str = "insufficient-image-evidence"
    frozen: bool = False

    def public_summary(self) -> dict[str, Any]:
        return {
            "estimatedLevel": self.estimated_level,
            "relativeLandmarkIdentity": self.relative_landmark_identity,
            "localizationConfidence": self.localization_confidence,
            "uncertainty": self.uncertainty,
            "levelBelief": {level: float(self.level_belief[index]) for index, level in enumerate(LEVEL_ORDER)},
            "landmarkEvidence": [observation.__dict__ for observation in self.landmark_evidence[-12:]],
            "visitedRegionCount": len(self.visited_regions),
            "promisingSearchRegion": self.promising_search_region,
            "abstainReason": self.abstain_reason,
            "frozen": self.frozen,
        }


def _predict(head: dict[str, Any], values: np.ndarray) -> np.ndarray:
    hidden = np.maximum(values @ np.asarray(head["inputWeights"], np.float32)
                        + np.asarray(head["inputBias"], np.float32), 0.0)
    return softmax(hidden @ np.asarray(head["outputWeights"], np.float32)
                   + np.asarray(head["outputBias"], np.float32))


def _context(current: np.ndarray, previous: np.ndarray | None, last_action: str) -> np.ndarray:
    delta = np.zeros_like(current) if previous is None else current - previous
    signed = delta.reshape(32, -1).mean(axis=1)
    magnitude = np.abs(delta).reshape(32, -1).mean(axis=1)
    action = np.zeros(len(ACTION_FEATURE_ORDER), np.float32)
    action[ACTION_TO_INDEX.get(last_action.upper(), ACTION_TO_INDEX["RESET"])] = 1.0
    return np.concatenate((current, signed, magnitude, action)).astype(np.float32)


def _advance_owned_odometry(state: ImageDerivedHandoffState, action: str) -> None:
    action = action.upper()
    if action == "CRANIAL":
        state.longitudinal_index -= 1
    elif action == "CAUDAL":
        state.longitudinal_index += 1
    elif action == "MEDIAL":
        state.lateral_index -= 1
    elif action == "LATERAL":
        state.lateral_index += 1


def update_handoff_state(model: dict[str, Any], current_features: np.ndarray, previous_features: np.ndarray | None,
                         last_action: str, step: int, state: ImageDerivedHandoffState) -> ImageDerivedHandoffState:
    """Update only from image features and policy-owned issued-action history."""
    if state.frozen:
        return state
    _advance_owned_odometry(state, last_action)
    context = _context(current_features, previous_features, last_action)
    scaler = model["contextScaler"]
    normalized = (context - np.asarray(scaler["mean"], np.float32)) / np.asarray(scaler["scale"], np.float32)
    level = _predict(model["contextLevelHead"], normalized)
    event = _predict(model["landmarkEventHead"], normalized)
    state.level_belief = (0.45 * state.level_belief + 0.55 * level).astype(np.float64)
    state.level_belief /= state.level_belief.sum()
    level_index = int(np.argmax(state.level_belief))
    state.estimated_level = LEVEL_ORDER[level_index]
    state.localization_confidence = float(state.level_belief[level_index])
    state.uncertainty = float(-np.sum(state.level_belief * np.log(np.maximum(state.level_belief, 1e-9))) / np.log(len(LEVEL_ORDER)))
    event_index = int(np.argmax(event[1:]) + 1)
    event_confidence = float(event[event_index])
    if event_confidence >= float(model["eventThreshold"]):
        state.relative_landmark_identity = EVENT_ORDER[event_index]
        if not state.landmark_evidence or state.landmark_evidence[-1].event != EVENT_ORDER[event_index]:
            state.landmark_evidence.append(LandmarkObservation(step, EVENT_ORDER[event_index], event_confidence,
                                                              state.longitudinal_index, state.lateral_index))
    key = f"{round(state.longitudinal_index / 2):+d}:{round(state.lateral_index / 2):+d}"
    state.visited_regions[key] = state.visited_regions.get(key, 0) + 1
    if state.estimated_level == "T7" and state.localization_confidence >= 0.70 and state.landmark_evidence:
        state.promising_search_region = "relative-T7-centered-symmetric-lateral"
        state.abstain_reason = ""
    elif state.estimated_level != "T7":
        state.promising_search_region = "longitudinal-relocalization"
        state.abstain_reason = "estimated-level-is-not-T7"
    elif not state.landmark_evidence:
        state.abstain_reason = "no-observed-landmark-transition"
    else:
        state.abstain_reason = "localization-confidence-too-low"
    return state


def handoff_ready(state: ImageDerivedHandoffState, *, min_confidence: float, max_uncertainty: float,
                  min_landmark_events: int) -> bool:
    return (state.estimated_level == "T7" and state.localization_confidence >= min_confidence
            and state.uncertainty <= max_uncertainty and len(state.landmark_evidence) >= min_landmark_events
            and state.promising_search_region.startswith("relative-T7"))


def freeze_handoff(state: ImageDerivedHandoffState) -> ImageDerivedHandoffState:
    state.frozen = True
    return state


def phase_two_prefix(state: ImageDerivedHandoffState | None, last_scan_direction: str) -> tuple[str, ...]:
    """Bounded image-derived confirmation before Phase 2; inverse actions return to handoff pose."""
    if state is None:
        return ()
    if state.uncertainty <= 0.12 and len(state.landmark_evidence) >= 2:
        return ()
    direction = last_scan_direction if last_scan_direction in {"Cranial", "Caudal"} else "Caudal"
    opposite = "Cranial" if direction == "Caudal" else "Caudal"
    return (direction, opposite)
