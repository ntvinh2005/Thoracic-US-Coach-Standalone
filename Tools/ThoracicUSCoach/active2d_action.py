from __future__ import annotations

"""Image-only Phase-2 factual transition-response utilities.

The privileged target score is used only to build offline regression labels.  A
serialized model accepts clean ultrasound-derived features and policy-owned
action history; it never accepts geometry, target coordinates, or scores.
"""

import hashlib
import json
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from typing import Iterable

import numpy as np

from active2d_contract import CONTRACT, EXPERIMENT_ID, canonical_sha256
from active2d_data import Active2DFrame, convolutional_features
from active2d_stop import convolutional_features_v2, stop_features


ACTION_SCHEMA = "active2d-phase2-factual-transition-response-v1"
ALLOWED_ACTIONS = ("MEDIAL", "LATERAL")
FORBIDDEN_INPUTS = (
    "pose",
    "targetCoordinates",
    "targetScore",
    "pathIndex",
    "oracleDirection",
    "privilegedAnnotations",
)


def validate_u08_manifest(manifest: dict[str, object]) -> None:
    if manifest.get("schemaVersion") != 1 or manifest.get("taskId") != "U08":
        raise ValueError("U08 manifest identity mismatch")
    if manifest.get("contract") != CONTRACT or manifest.get("experimentId") != EXPERIMENT_ID:
        raise ValueError("U08 contract mismatch")
    if manifest.get("allowedActions") != list(ALLOWED_ACTIONS):
        raise ValueError("U08 action set must remain MEDIAL/LATERAL")
    if manifest.get("selectionSplits") != ["train", "dev"] or manifest.get("lockedTestSplit") != "test":
        raise ValueError("U08 split protocol mismatch")
    configurations = manifest.get("configurations") or []
    if len(configurations) != 2:
        raise ValueError("U08 requires exactly two bounded configurations")
    if {item.get("featureMode") for item in configurations} != {"spatial-v2", "history-v1"}:
        raise ValueError("U08 configurations must be spatial-v2 and history-v1")
    for item in configurations:
        if item.get("seeds") != [13, 47]:
            raise ValueError("U08 seeds must remain frozen at 13 and 47")


def relative_gain(candidate: float, baseline: float) -> float:
    return (baseline - candidate) / max(abs(baseline), 1e-12)


@dataclass(frozen=True)
class ActionTransition:
    episode_id: str
    split: str
    family: str
    state: Active2DFrame
    history: Active2DFrame
    outcome: Active2DFrame
    action: str
    delta_score: float

    @property
    def identity(self) -> str:
        return f"{self.episode_id}:{self.state.index}->{self.outcome.index}:{self.action}"


def build_action_transitions(frames: Iterable[Active2DFrame]) -> list[ActionTransition]:
    """Build factual pre-action rows; the outcome action was executed from state."""
    grouped: dict[str, list[Active2DFrame]] = {}
    for frame in frames:
        grouped.setdefault(frame.episode_id, []).append(frame)
    rows: list[ActionTransition] = []
    for episode_id in sorted(grouped):
        episode = sorted(grouped[episode_id], key=lambda item: item.index)
        for position in range(1, len(episode)):
            state = episode[position - 1]
            outcome = episode[position]
            if outcome.index != state.index + 1:
                raise ValueError(f"Non-contiguous Active2D frames in {episode_id}: {state.index}->{outcome.index}")
            if outcome.episode_reset:
                raise ValueError(f"Non-initial reset frame in {episode_id}: {outcome.index}")
            if outcome.issued_action not in ALLOWED_ACTIONS:
                continue
            if not outcome.action_accepted or not outcome.contact_valid or not outcome.workspace_valid:
                continue
            history = episode[position - 2] if position >= 2 else state
            rows.append(ActionTransition(
                episode_id=episode_id,
                split=state.split,
                family=state.family,
                state=state,
                history=history,
                outcome=outcome,
                action=outcome.issued_action,
                delta_score=float(outcome.target_score - state.target_score),
            ))
    identities = [row.identity for row in rows]
    if len(identities) != len(set(identities)):
        raise ValueError("Duplicate factual transition identities")
    family_splits: dict[str, set[str]] = {}
    for row in rows:
        family_splits.setdefault(row.family, set()).add(row.split)
    leaking = {family: splits for family, splits in family_splits.items() if len(splits) > 1}
    if leaking:
        raise ValueError(f"Configuration-family leakage across splits: {sorted(leaking)[:5]}")
    return rows


@lru_cache(maxsize=32768)
def _spatial(path: str) -> np.ndarray:
    values = convolutional_features_v2(Path(path)).astype(np.float32)
    values.setflags(write=False)
    return values


@lru_cache(maxsize=32768)
def _compact(path: str) -> np.ndarray:
    values = convolutional_features(Path(path)).astype(np.float32)
    values.setflags(write=False)
    return values


def transition_features(rows: Iterable[ActionTransition], feature_mode: str) -> np.ndarray:
    selected = list(rows)
    features: list[np.ndarray] = []
    for row in selected:
        if feature_mode == "spatial-v2":
            values = _spatial(str(row.state.image.resolve()))
        elif feature_mode == "history-v1":
            current = _compact(str(row.state.image.resolve()))
            previous = _compact(str(row.history.image.resolve()))
            values = stop_features(current, previous, row.state.issued_action)
        else:
            raise ValueError(f"Unsupported action feature mode: {feature_mode}")
        features.append(np.asarray(values, dtype=np.float32))
    width = 1024 if feature_mode == "spatial-v2" else 776
    return np.stack(features) if features else np.empty((0, width), dtype=np.float32)


def transition_fingerprint(rows: Iterable[ActionTransition]) -> str:
    payload = [{
        "identity": row.identity,
        "split": row.split,
        "family": row.family,
        "stateImageSha256": row.state.image_sha256,
        "historyImageSha256": row.history.image_sha256,
        "outcomeImageSha256": row.outcome.image_sha256,
        "action": row.action,
        "deltaScore": row.delta_score,
    } for row in sorted(rows, key=lambda item: item.identity)]
    return canonical_sha256(payload)


def fit_random_relu_ridge(
    features: np.ndarray,
    rows: list[ActionTransition],
    *,
    feature_mode: str,
    hidden_size: int,
    ridge: float,
    seed: int,
) -> dict[str, object]:
    if len(features) != len(rows) or not rows:
        raise ValueError("Action training features/rows mismatch")
    mean = features.mean(axis=0, dtype=np.float64).astype(np.float32)
    scale = np.maximum(features.std(axis=0, dtype=np.float64), 1e-3).astype(np.float32)
    normalized = (features - mean) / scale
    rng = np.random.default_rng(seed)
    projection = rng.normal(
        0.0, np.sqrt(2.0 / features.shape[1]), (features.shape[1], hidden_size)
    ).astype(np.float32)
    hidden = np.maximum(normalized @ projection, 0.0).astype(np.float64)
    design = np.concatenate((hidden, np.ones((len(hidden), 1), dtype=np.float64)), axis=1)
    targets = np.asarray([row.delta_score for row in rows], dtype=np.float64)
    heads: dict[str, object] = {}
    for action in ALLOWED_ACTIONS:
        indices = np.asarray([i for i, row in enumerate(rows) if row.action == action], dtype=np.int64)
        if len(indices) < hidden_size + 1:
            raise ValueError(f"Insufficient {action} rows for action-conditioned head: {len(indices)}")
        action_design = design[indices]
        gram = action_design.T @ action_design
        regularizer = np.eye(gram.shape[0], dtype=np.float64) * ridge
        regularizer[-1, -1] = 0.0
        parameters = np.linalg.solve(gram + regularizer, action_design.T @ targets[indices])
        heads[action] = {
            "weights": parameters[:-1].astype(np.float32).tolist(),
            "bias": float(parameters[-1]),
            "trainingRows": int(len(indices)),
        }
    return {
        "schemaVersion": 1,
        "schema": ACTION_SCHEMA,
        "contract": CONTRACT,
        "experimentId": EXPERIMENT_ID,
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": list(FORBIDDEN_INPUTS),
        "allowedActions": list(ALLOWED_ACTIONS),
        "architecture": {
            "type": "fixed-random-relu+per-action-ridge-v1",
            "featureMode": feature_mode,
            "featureWidth": int(features.shape[1]),
            "hiddenSize": hidden_size,
            "featureMean": mean.tolist(),
            "featureScale": scale.tolist(),
            "projection": projection.tolist(),
            "heads": heads,
            "ridge": ridge,
            "seed": seed,
        },
    }


def predict_action_values(model: dict[str, object], features: np.ndarray) -> np.ndarray:
    validate_action_model(model)
    architecture = model["architecture"]
    values = np.asarray(features, dtype=np.float32)
    if values.ndim == 1:
        values = values.reshape(1, -1)
    mean = np.asarray(architecture["featureMean"], dtype=np.float32)
    scale = np.asarray(architecture["featureScale"], dtype=np.float32)
    projection = np.asarray(architecture["projection"], dtype=np.float32)
    hidden = np.maximum(((values - mean) / scale) @ projection, 0.0)
    columns = []
    for action in ALLOWED_ACTIONS:
        head = architecture["heads"][action]
        columns.append(hidden @ np.asarray(head["weights"], dtype=np.float32) + float(head["bias"]))
    return np.stack(columns, axis=1)


def factual_predictions(model: dict[str, object], features: np.ndarray, rows: list[ActionTransition]) -> np.ndarray:
    values = predict_action_values(model, features)
    return np.asarray([values[index, ALLOWED_ACTIONS.index(row.action)] for index, row in enumerate(rows)])


def regression_metrics(rows: list[ActionTransition], predictions: np.ndarray) -> dict[str, object]:
    truth = np.asarray([row.delta_score for row in rows], dtype=np.float64)
    predicted = np.asarray(predictions, dtype=np.float64)
    if len(truth) != len(predicted):
        raise ValueError("Prediction count mismatch")
    error = predicted - truth
    significant = np.abs(truth) >= 1e-6
    result: dict[str, object] = {
        "count": int(len(rows)),
        "mae": float(np.mean(np.abs(error))) if len(rows) else None,
        "rmse": float(np.sqrt(np.mean(error * error))) if len(rows) else None,
        "signAccuracy": float(np.mean(np.sign(predicted[significant]) == np.sign(truth[significant])))
        if np.any(significant) else None,
        "meanObservedDelta": float(np.mean(truth)) if len(rows) else None,
    }
    by_action: dict[str, object] = {}
    for action in ALLOWED_ACTIONS:
        indices = np.asarray([i for i, row in enumerate(rows) if row.action == action], dtype=np.int64)
        action_error = error[indices]
        action_truth = truth[indices]
        action_significant = np.abs(action_truth) >= 1e-6
        by_action[action] = {
            "count": int(len(indices)),
            "mae": float(np.mean(np.abs(action_error))) if len(indices) else None,
            "rmse": float(np.sqrt(np.mean(action_error * action_error))) if len(indices) else None,
            "signAccuracy": float(np.mean(
                np.sign(predicted[indices][action_significant]) == np.sign(action_truth[action_significant])
            )) if np.any(action_significant) else None,
        }
    result["byAction"] = by_action
    return result


def baseline_predictions(
    train_rows: list[ActionTransition],
    train_features: np.ndarray,
    rows: list[ActionTransition],
    features: np.ndarray,
    *,
    ridge: float,
) -> dict[str, np.ndarray]:
    targets = np.asarray([row.delta_score for row in train_rows], dtype=np.float64)
    priors = {
        action: float(np.mean([row.delta_score for row in train_rows if row.action == action]))
        for action in ALLOWED_ACTIONS
    }
    action_prior = np.asarray([priors[row.action] for row in rows], dtype=np.float64)
    mean = train_features.mean(axis=0, dtype=np.float64)
    scale = np.maximum(train_features.std(axis=0, dtype=np.float64), 1e-3)
    train_normalized = (train_features.astype(np.float64) - mean) / scale
    selected_normalized = (features.astype(np.float64) - mean) / scale
    # A small deterministic projection keeps the action-agnostic baseline cheap.
    rng = np.random.default_rng(0)
    projection = rng.normal(0.0, np.sqrt(2.0 / train_features.shape[1]),
                            (train_features.shape[1], 32))
    train_hidden = np.maximum(train_normalized @ projection, 0.0)
    selected_hidden = np.maximum(selected_normalized @ projection, 0.0)
    design = np.concatenate((train_hidden, np.ones((len(train_hidden), 1))), axis=1)
    gram = design.T @ design
    regularizer = np.eye(gram.shape[0]) * ridge
    regularizer[-1, -1] = 0.0
    parameters = np.linalg.solve(gram + regularizer, design.T @ targets)
    image_agnostic = selected_hidden @ parameters[:-1] + parameters[-1]
    return {"actionPrior": action_prior, "imageAgnostic": image_agnostic}


def validate_action_model(model: dict[str, object]) -> None:
    if model.get("schema") != ACTION_SCHEMA or model.get("contract") != CONTRACT:
        raise ValueError("Phase-2 action checkpoint identity mismatch")
    if model.get("experimentId") != EXPERIMENT_ID:
        raise ValueError("Phase-2 action checkpoint experiment mismatch")
    if model.get("policyInputs") != ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"]:
        raise ValueError("Phase-2 action checkpoint violates restricted actor inputs")
    if model.get("allowedActions") != list(ALLOWED_ACTIONS):
        raise ValueError("Phase-2 action checkpoint changes the bounded action set")
    architecture = model.get("architecture") or {}
    if architecture.get("type") != "fixed-random-relu+per-action-ridge-v1":
        raise ValueError("Unsupported Phase-2 action architecture")
    width = int(architecture.get("featureWidth", 0))
    hidden = int(architecture.get("hiddenSize", 0))
    if width <= 0 or hidden <= 0:
        raise ValueError("Invalid Phase-2 action dimensions")
    if any(len(architecture.get(key) or []) != width for key in ("featureMean", "featureScale", "projection")):
        raise ValueError("Phase-2 action feature shape mismatch")
    if any(len(row) != hidden for row in architecture["projection"]):
        raise ValueError("Phase-2 action projection shape mismatch")
    for action in ALLOWED_ACTIONS:
        head = (architecture.get("heads") or {}).get(action) or {}
        if len(head.get("weights") or []) != hidden:
            raise ValueError(f"Malformed Phase-2 action head: {action}")


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def save_json(path: Path, payload: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    temporary.replace(path)
