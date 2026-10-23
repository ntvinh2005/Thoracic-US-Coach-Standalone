from __future__ import annotations

from pathlib import Path
from typing import Any, Iterable

import numpy as np

from active2d_contract import ACTION_ORDER, CONTRACT, EXPERIMENT_ID
from active2d_data import Active2DFrame, convolutional_features
from active2d_data import FILTERS


STOP_SCHEMA = "active2d-image-only-stop-v1"


def _action_vector(action: str) -> np.ndarray:
    result = np.zeros(len(ACTION_ORDER), dtype=np.float32)
    normalized = action.replace("_", "").upper()
    if normalized in ACTION_ORDER:
        result[ACTION_ORDER.index(normalized)] = 1.0
    return result


def stop_features(current: np.ndarray, previous: np.ndarray, previous_action: str) -> np.ndarray:
    return np.concatenate((current, previous, current - previous, _action_vector(previous_action))).astype(np.float32)


def convolutional_features_v2(path: Path) -> np.ndarray:
    from PIL import Image

    with Image.open(path) as image:
        pixels = np.asarray(image.convert("L").resize((64, 64), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
    pixels = (pixels - pixels.mean()) / max(float(pixels.std()), 1e-6)
    windows = np.lib.stride_tricks.sliding_window_view(pixels, (3, 3))
    rows = np.array_split(np.arange(windows.shape[0]), 8)
    cols = np.array_split(np.arange(windows.shape[1]), 8)

    def pool8(values: np.ndarray) -> np.ndarray:
        return np.asarray([values[np.ix_(row, col)].mean() for row in rows for col in cols], dtype=np.float32)

    parts: list[np.ndarray] = []
    for kernel in FILTERS:
        response = np.einsum("ijxy,xy->ij", windows, kernel, optimize=True)
        parts.extend((pool8(response), pool8(np.abs(response))))
    raw = np.asarray(
        Image.fromarray(pixels).resize((16, 16), Image.Resampling.BILINEAR), dtype=np.float32
    ).reshape(-1)
    vector = np.concatenate([raw, *parts]).astype(np.float32)
    return (vector - vector.mean()) / max(float(vector.std()), 1e-6)


def request_stop_features(
    image_path: Path,
    previous_image: Path | None,
    previous_action: str,
    feature_extractor: str = "history-v1",
) -> np.ndarray:
    if feature_extractor == "spatial-v2":
        return np.concatenate((convolutional_features_v2(image_path), _action_vector(previous_action))).astype(np.float32)
    current = convolutional_features(image_path)
    previous = convolutional_features(previous_image) if previous_image is not None else current
    return stop_features(current, previous, previous_action)


def frame_stop_feature_matrix(frames: Iterable[Active2DFrame]) -> np.ndarray:
    selected = list(frames)
    rows: list[np.ndarray] = []
    previous_episode = None
    previous_features: np.ndarray | None = None
    for frame in selected:
        current = convolutional_features(frame.image)
        if frame.episode_id != previous_episode or frame.episode_reset or previous_features is None:
            previous_features = current
        rows.append(stop_features(current, previous_features, frame.issued_action))
        previous_features = current
        previous_episode = frame.episode_id
    return np.stack(rows) if rows else np.empty((0, 0), dtype=np.float32)


def frame_stop_feature_matrix_v2(frames: Iterable[Active2DFrame]) -> np.ndarray:
    selected = list(frames)
    return np.stack([
        np.concatenate((convolutional_features_v2(frame.image), _action_vector(frame.issued_action))).astype(np.float32)
        for frame in selected
    ]) if selected else np.empty((0, 0), dtype=np.float32)


def stop_probabilities(model: dict[str, Any], features: np.ndarray) -> np.ndarray:
    architecture = model["architecture"]
    values = np.asarray(features, dtype=np.float32)
    if values.ndim == 1:
        values = values.reshape(1, -1)
    if architecture.get("type") == "standardized-ridge-quality-score-v1":
        mean = np.asarray(architecture["featureMean"], dtype=np.float32)
        scale = np.asarray(architecture["featureScale"], dtype=np.float32)
        weights = np.asarray(architecture["weights"], dtype=np.float32)
        scores = ((values - mean) / scale) @ weights + float(architecture["bias"])
        return np.clip(scores.reshape(-1), 0.0, 1.0)
    if architecture.get("type") == "standardized-logistic-stop-v1":
        mean = np.asarray(architecture["featureMean"], dtype=np.float32)
        scale = np.asarray(architecture["featureScale"], dtype=np.float32)
        weights = np.asarray(architecture["weights"], dtype=np.float32)
        logits = ((values - mean) / scale) @ weights + float(architecture["bias"])
        return 1.0 / (1.0 + np.exp(-np.clip(logits.reshape(-1), -30.0, 30.0)))
    hidden = np.maximum(
        values @ np.asarray(architecture["inputWeights"], dtype=np.float32)
        + np.asarray(architecture["inputBias"], dtype=np.float32),
        0.0,
    )
    logits = (
        hidden @ np.asarray(architecture["outputWeights"], dtype=np.float32)
        + float(architecture["outputBias"])
    ).reshape(-1)
    return 1.0 / (1.0 + np.exp(-np.clip(logits, -30.0, 30.0)))


def validate_stop_checkpoint(model: dict[str, Any]) -> None:
    if model.get("schema") != STOP_SCHEMA:
        raise ValueError("Active 2D STOP checkpoint schema mismatch")
    if model.get("contract") != CONTRACT or model.get("experimentId") != EXPERIMENT_ID:
        raise ValueError("Active 2D STOP checkpoint identity mismatch")
    if model.get("policyInputs") != ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"]:
        raise ValueError("STOP checkpoint violates restricted actor inputs")
    architecture = model.get("architecture") or {}
    architecture_type = architecture.get("type")
    if architecture_type not in {
        "fixed-convolutional-history+binary-mlp-v1",
        "standardized-ridge-quality-score-v1",
        "standardized-logistic-stop-v1",
    }:
        raise ValueError("Unsupported Active 2D STOP architecture")
    if architecture_type in {"standardized-ridge-quality-score-v1", "standardized-logistic-stop-v1"}:
        if architecture.get("featureExtractor", "history-v1") not in {"history-v1", "spatial-v2"}:
            raise ValueError("Unsupported ridge STOP feature extractor")
        width = int(architecture.get("featureWidth", 0))
        if width <= 0 or any(len(architecture.get(key) or []) != width for key in ("featureMean", "featureScale", "weights")):
            raise ValueError("Malformed ridge STOP architecture")
        if any(float(value) <= 0.0 for value in architecture["featureScale"]):
            raise ValueError("Invalid ridge STOP feature scale")
        threshold = float(model.get("stopRule", {}).get("threshold", -1.0))
        if not 0.0 <= threshold <= 1.0:
            raise ValueError("Invalid STOP threshold")
        return
    input_weights = architecture.get("inputWeights") or []
    input_bias = architecture.get("inputBias") or []
    output_weights = architecture.get("outputWeights") or []
    width = int(architecture.get("featureWidth", 0))
    hidden = len(input_bias)
    if len(input_weights) != width or hidden <= 0 or any(len(row) != hidden for row in input_weights):
        raise ValueError("Malformed STOP input layer")
    if len(output_weights) != hidden:
        raise ValueError("Malformed STOP output layer")
    threshold = float(model.get("stopRule", {}).get("threshold", -1.0))
    if not 0.0 <= threshold <= 1.0:
        raise ValueError("Invalid STOP threshold")
