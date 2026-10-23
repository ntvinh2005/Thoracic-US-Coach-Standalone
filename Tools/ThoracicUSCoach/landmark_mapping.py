from __future__ import annotations

from collections import defaultdict
from typing import Iterable, Sequence

import numpy as np

from active2d_contract import ACTION_ORDER, LEVEL_ORDER


EVENT_ORDER = (
    "NONE",
    "OUTSIDE_TO_T6",
    "T6_TO_OUTSIDE",
    "T6_TO_T7",
    "T7_TO_T6",
    "T7_TO_T8",
    "T8_TO_T7",
    "T8_TO_OUTSIDE",
    "OUTSIDE_TO_T8",
    "UNMATCHED",
)
EVENT_TO_INDEX = {value: index for index, value in enumerate(EVENT_ORDER)}
LEVEL_TO_INDEX = {value: index for index, value in enumerate(LEVEL_ORDER)}
ACTION_FEATURE_ORDER = ("RESET", *ACTION_ORDER)
ACTION_TO_INDEX = {value: index for index, value in enumerate(ACTION_FEATURE_ORDER)}
OUTSIDE = "OUTSIDE_T6_T8"


def derive_landmark_event(previous_level: str | None, current_level: str, reset: bool = False) -> str:
    if reset or previous_level is None or previous_level == current_level:
        return "NONE"
    lookup = {
        (OUTSIDE, "T6"): "OUTSIDE_TO_T6",
        ("T6", OUTSIDE): "T6_TO_OUTSIDE",
        ("T6", "T7"): "T6_TO_T7",
        ("T7", "T6"): "T7_TO_T6",
        ("T7", "T8"): "T7_TO_T8",
        ("T8", "T7"): "T8_TO_T7",
        ("T8", OUTSIDE): "T8_TO_OUTSIDE",
        (OUTSIDE, "T8"): "OUTSIDE_TO_T8",
    }
    return lookup.get((previous_level, current_level), "UNMATCHED")


def event_labels(frames: Sequence) -> np.ndarray:
    labels: list[int] = []
    previous_episode = None
    previous_level = None
    for frame in frames:
        reset = frame.episode_id != previous_episode or frame.episode_reset
        labels.append(EVENT_TO_INDEX[derive_landmark_event(previous_level, frame.level, reset)])
        previous_episode = frame.episode_id
        previous_level = frame.level
    return np.asarray(labels, dtype=np.int64)


def context_features(frames: Sequence, image_features: np.ndarray) -> np.ndarray:
    """Build causal image/action context. Ground-truth labels never enter these features."""
    if len(frames) != len(image_features):
        raise ValueError("Frame/feature count mismatch")
    result: list[np.ndarray] = []
    previous_episode = None
    previous = None
    for frame, current in zip(frames, image_features):
        reset = frame.episode_id != previous_episode or frame.episode_reset
        delta = np.zeros_like(current) if reset or previous is None else current - previous
        # Compact temporal change into 32 contiguous bins; preserve signed and absolute evidence.
        signed = delta.reshape(32, -1).mean(axis=1)
        magnitude = np.abs(delta).reshape(32, -1).mean(axis=1)
        action = np.zeros(len(ACTION_FEATURE_ORDER), dtype=np.float32)
        action[ACTION_TO_INDEX.get("RESET" if reset else frame.issued_action, 0)] = 1.0
        result.append(np.concatenate((current, signed, magnitude, action)).astype(np.float32))
        previous_episode = frame.episode_id
        previous = current
    return np.stack(result) if result else np.empty((0, 256 + 64 + len(ACTION_FEATURE_ORDER)), np.float32)


def apply_event_threshold(probabilities: np.ndarray, threshold: float) -> np.ndarray:
    non_none = probabilities[:, 1:]
    best = np.argmax(non_none, axis=1) + 1
    confidence = non_none[np.arange(len(non_none)), best - 1]
    return np.where(confidence >= threshold, best, EVENT_TO_INDEX["NONE"]).astype(np.int64)


def confusion_metrics(labels: np.ndarray, predictions: np.ndarray, names: Sequence[str]) -> dict:
    matrix = np.zeros((len(names), len(names)), dtype=np.int64)
    for actual, predicted in zip(labels, predictions):
        matrix[int(actual), int(predicted)] += 1
    per_class = {}
    f1_values = []
    for index, name in enumerate(names):
        tp = int(matrix[index, index])
        support = int(matrix[index].sum())
        selected = int(matrix[:, index].sum())
        precision = tp / max(1, selected)
        recall = tp / max(1, support)
        f1 = 2 * precision * recall / max(1e-12, precision + recall)
        per_class[name] = {"support": support, "precision": precision, "recall": recall, "f1": f1}
        if support:
            f1_values.append(f1)
    return {
        "count": int(len(labels)),
        "accuracy": float(np.mean(labels == predictions)) if len(labels) else 0.0,
        "macroF1": float(np.mean(f1_values)) if f1_values else 0.0,
        "confusion": matrix.tolist(),
        "perClass": per_class,
    }


def transition_metrics(labels: np.ndarray, predictions: np.ndarray) -> dict:
    actual = labels != EVENT_TO_INDEX["NONE"]
    predicted = predictions != EVENT_TO_INDEX["NONE"]
    tp = int(np.sum(actual & predicted))
    fp = int(np.sum(~actual & predicted))
    fn = int(np.sum(actual & ~predicted))
    precision = tp / max(1, tp + fp)
    recall = tp / max(1, tp + fn)
    return {
        "truePositives": tp,
        "falsePositives": fp,
        "falseNegatives": fn,
        "precision": precision,
        "recall": recall,
        "f1": 2 * precision * recall / max(1e-12, precision + recall),
    }


def event_endpoints(event: str) -> tuple[str, str] | None:
    if event in {"NONE", "UNMATCHED"}:
        return None
    source, destination = event.split("_TO_")
    return (OUTSIDE if source == "OUTSIDE" else source, OUTSIDE if destination == "OUTSIDE" else destination)


def sequence_diagnostics(frames: Sequence, event_predictions: np.ndarray, level_predictions: np.ndarray) -> dict:
    by_episode: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        by_episode[frame.episode_id].append(index)
    consistent = 0
    comparable = 0
    revisit_total = 0
    revisit_consistent = 0
    for indices in by_episode.values():
        endpoints = [event_endpoints(EVENT_ORDER[int(event_predictions[index])]) for index in indices]
        endpoints = [item for item in endpoints if item is not None]
        for left, right in zip(endpoints, endpoints[1:]):
            comparable += 1
            consistent += int(left[1] == right[0])
        runs: dict[str, list[int]] = defaultdict(list)
        previous_actual = None
        for index in indices:
            actual = frames[index].level
            if actual != previous_actual:
                runs[actual].append(int(level_predictions[index]))
            previous_actual = actual
        for values in runs.values():
            if len(values) > 1:
                revisit_total += len(values)
                counts = np.bincount(values, minlength=len(LEVEL_ORDER))
                revisit_consistent += int(counts.max())
    return {
        "orderedEventPairs": comparable,
        "orderingConsistency": consistent / max(1, comparable),
        "revisitRuns": revisit_total,
        "revisitConsistency": revisit_consistent / max(1, revisit_total),
    }


def episode_maps(frames: Sequence, event_probabilities: np.ndarray, event_predictions: np.ndarray,
                 level_probabilities: np.ndarray) -> list[dict]:
    grouped: dict[str, list[dict]] = defaultdict(list)
    for index, frame in enumerate(frames):
        event_index = int(event_predictions[index])
        if event_index == 0:
            continue
        level_index = int(np.argmax(level_probabilities[index]))
        grouped[frame.episode_id].append({
            "frameIndex": frame.index,
            "issuedAction": frame.issued_action,
            "event": EVENT_ORDER[event_index],
            "eventConfidence": float(event_probabilities[index, event_index]),
            "predictedLevel": LEVEL_ORDER[level_index],
            "levelConfidence": float(level_probabilities[index, level_index]),
        })
    return [{"episodeId": episode, "landmarkEvents": events} for episode, events in sorted(grouped.items())]
