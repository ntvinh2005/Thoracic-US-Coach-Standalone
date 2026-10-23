from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image

from cached_env import CachedTrajectory, load_cached_trajectories
from coach_data import ACTION_TO_INDEX, model_probabilities
from contract_v3 import CONTRACT_VERSION, EXPERIMENT_ID, OBSERVATION_SCHEMA, TARGET_SEMANTICS


STOP_MODEL_SCHEMA = "thoracic-stop-detector-v1"
STOP_EXPERIMENT_ID = "stop_history_robust_v1_20261022"


@dataclass(frozen=True)
class DecisionContext:
    split: str
    sweep_id: str
    configuration_family: str
    current_index: int
    previous_index: int
    current_image: Path
    previous_image: Path
    previous_action: int
    reset: bool
    acceptable: bool
    distance_to_target_steps: int
    boundary_positive: bool

    @property
    def identity(self) -> str:
        kind = "reset" if self.reset else f"move{self.previous_action:+d}"
        return f"{self.sweep_id}:{self.previous_index}:{self.current_index}:{kind}"


def build_decision_contexts(trajectories: Iterable[CachedTrajectory]) -> list[DecisionContext]:
    """Construct exact next-decision contexts from deterministic v3 path dynamics.

    Every valid pose contributes a serving-equivalent reset context (previous=current,
    STOP sentinel) and both physically possible approach directions.  No context is
    emitted after STOP or truncation.  This targeted augmentation uses only captured
    clean images from the same training-family trajectory.
    """
    contexts: list[DecisionContext] = []
    for trajectory in trajectories:
        targets = [index for index, value in enumerate(trajectory.acceptable) if value]
        if not targets:
            continue
        low, high = min(targets), max(targets)
        for current in range(len(trajectory.frames)):
            distance = min(abs(current - target) for target in targets)
            acceptable = bool(trajectory.acceptable[current])
            boundary_positive = acceptable and (current == low or current == high)
            current_frame = trajectory.frames[current]
            contexts.append(
                DecisionContext(
                    split=trajectory.split,
                    sweep_id=trajectory.sweep_id,
                    configuration_family=trajectory.configuration_family,
                    current_index=current,
                    previous_index=current,
                    current_image=current_frame.image,
                    previous_image=current_frame.image,
                    previous_action=0,
                    reset=True,
                    acceptable=acceptable,
                    distance_to_target_steps=distance,
                    boundary_positive=boundary_positive,
                )
            )
            for action in (-1, 1):
                previous = current - action * trajectory.step_count
                if previous < 0 or previous >= len(trajectory.frames):
                    continue
                # The contract clamps only at the boundary.  This inverse maps an
                # actually executed movement to its destination decision state.
                destination = min(
                    len(trajectory.frames) - 1,
                    max(0, previous + action * trajectory.step_count),
                )
                if destination != current:
                    continue
                contexts.append(
                    DecisionContext(
                        split=trajectory.split,
                        sweep_id=trajectory.sweep_id,
                        configuration_family=trajectory.configuration_family,
                        current_index=current,
                        previous_index=previous,
                        current_image=current_frame.image,
                        previous_image=trajectory.frames[previous].image,
                        previous_action=action,
                        reset=False,
                        acceptable=acceptable,
                        distance_to_target_steps=distance,
                        boundary_positive=boundary_positive,
                    )
                )
    identities = [context.identity for context in contexts]
    if len(identities) != len(set(identities)):
        raise ValueError("Decision-context identities are not unique")
    return contexts


@lru_cache(maxsize=32_768)
def raw_pixels(path: str, size: int) -> np.ndarray:
    with Image.open(path) as image:
        values = np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR),
            dtype=np.float32,
        ).reshape(-1) / 255.0
    values.setflags(write=False)
    return values


def fit_global_normalization(paths: Iterable[Path], size: int) -> dict[str, object]:
    unique = sorted({str(path.resolve()) for path in paths})
    if not unique:
        raise ValueError("Cannot fit normalization without training images")
    matrix = np.stack([raw_pixels(path, size) for path in unique])
    mean = matrix.mean(axis=0).astype(np.float32)
    std = matrix.std(axis=0).astype(np.float32)
    std = np.maximum(std, 1e-3)
    return {
        "kind": "global-pixel-train-v1",
        "featureSize": size,
        "mean": mean.tolist(),
        "std": std.tolist(),
        "trainingImageCount": len(unique),
    }


def _normalize(values: np.ndarray, preprocessing: str, normalization: dict[str, object] | None) -> np.ndarray:
    if preprocessing == "raw-01":
        return values.astype(np.float32)
    if preprocessing == "per-image-zscore":
        return ((values - float(values.mean())) / (float(values.std()) + 1e-6)).astype(np.float32)
    if preprocessing == "structural-zscore-v1":
        size = int(round(np.sqrt(values.size)))
        if size * size != values.size:
            raise ValueError("Structural preprocessing expects a square image vector")
        zscore = (values - float(values.mean())) / (float(values.std()) + 1e-6)
        image = zscore.reshape(size, size)
        gradient_y, gradient_x = np.gradient(image)
        gradient = np.sqrt(gradient_x * gradient_x + gradient_y * gradient_y)
        bright = values.reshape(size, size) >= float(np.quantile(values, 0.95))
        profiles = np.concatenate(
            (
                image.mean(axis=1),
                image.std(axis=1),
                image.max(axis=1),
                image.min(axis=1),
                bright.mean(axis=1),
                image.mean(axis=0),
                image.std(axis=0),
                image.max(axis=0),
                image.min(axis=0),
                bright.mean(axis=0),
            )
        )
        # All terms are derived from the clean image.  Profiles make the bright
        # interfaces and shadow boundaries less geometry-specific than a fully
        # connected model over absolute pixels, while gradients preserve shape.
        return np.concatenate((zscore, gradient.reshape(-1), profiles)).astype(np.float32)
    if preprocessing.startswith("global-pixel"):
        if normalization is None or normalization.get("kind") != "global-pixel-train-v1":
            raise ValueError("Global preprocessing requires train-fitted normalization")
        mean = np.asarray(normalization["mean"], dtype=np.float32)
        std = np.asarray(normalization["std"], dtype=np.float32)
        if mean.shape != values.shape or std.shape != values.shape:
            raise ValueError("Global normalization dimension mismatch")
        return ((values - mean) / std).astype(np.float32)
    raise ValueError(f"Unknown STOP preprocessing: {preprocessing}")


def _image_stats(previous: np.ndarray, current: np.ndarray) -> np.ndarray:
    delta = current - previous
    return np.asarray(
        [
            previous.mean(),
            previous.std(),
            current.mean(),
            current.std(),
            np.mean(np.abs(delta)),
            np.sqrt(np.mean(delta * delta)),
        ],
        dtype=np.float32,
    )


def feature_width(size: int, observation: str, layout: str) -> int:
    pixels = size * size
    image_width = pixels * 2 + size * 10 if layout.startswith("structural") else pixels
    if observation == "current":
        if layout == "structural-basic":
            return image_width
        return image_width + (2 if layout == "stats" else 0)
    if observation != "history":
        raise ValueError(f"Unknown observation mode: {observation}")
    if layout == "basic":
        return image_width * 2 + 3
    if layout == "diff-stats":
        return image_width * 3 + 3 + 6
    if layout == "structural-basic":
        return image_width * 2 + 3
    if layout == "structural-diff-stats":
        return image_width * 3 + 3 + 6
    raise ValueError(f"Unknown history layout: {layout}")


def context_features(
    context: DecisionContext,
    *,
    size: int,
    observation: str,
    preprocessing: str,
    layout: str,
    normalization: dict[str, object] | None,
    rng: np.random.Generator | None = None,
    noise_std: float = 0.0,
) -> np.ndarray:
    previous_raw = raw_pixels(str(context.previous_image), size).copy()
    current_raw = raw_pixels(str(context.current_image), size).copy()
    if rng is not None and noise_std > 0.0:
        previous_raw = np.clip(previous_raw + rng.normal(0.0, noise_std, previous_raw.shape), 0.0, 1.0).astype(np.float32)
        current_raw = np.clip(current_raw + rng.normal(0.0, noise_std, current_raw.shape), 0.0, 1.0).astype(np.float32)
        if context.reset:
            # Serving reset aliases the same observation, not two independent renders.
            previous_raw = current_raw.copy()
    previous = _normalize(previous_raw, preprocessing, normalization)
    current = _normalize(current_raw, preprocessing, normalization)
    if observation == "current":
        if layout == "structural-basic":
            return current
        if layout == "stats":
            return np.concatenate((current, np.asarray([current_raw.mean(), current_raw.std()], dtype=np.float32)))
        if layout != "basic":
            raise ValueError(f"Unknown current layout: {layout}")
        return current
    action = np.zeros(3, dtype=np.float32)
    action[ACTION_TO_INDEX[context.previous_action]] = 1.0
    if layout == "basic":
        return np.concatenate((previous, current, action))
    if layout == "diff-stats":
        return np.concatenate((previous, current, current - previous, action, _image_stats(previous_raw, current_raw)))
    if layout == "structural-basic":
        return np.concatenate((previous, current, action))
    if layout == "structural-diff-stats":
        return np.concatenate((previous, current, current - previous, action, _image_stats(previous_raw, current_raw)))
    raise ValueError(f"Unknown history layout: {layout}")


def feature_matrix(
    contexts: Iterable[DecisionContext],
    *,
    size: int,
    observation: str,
    preprocessing: str,
    layout: str,
    normalization: dict[str, object] | None,
    seed: int | None = None,
    noise_std: float = 0.0,
) -> np.ndarray:
    selected = list(contexts)
    rng = np.random.default_rng(seed) if seed is not None and noise_std > 0.0 else None
    rows = [
        context_features(
            context,
            size=size,
            observation=observation,
            preprocessing=preprocessing,
            layout=layout,
            normalization=normalization,
            rng=rng,
            noise_std=noise_std,
        )
        for context in selected
    ]
    width = feature_width(size, observation, layout)
    return np.stack(rows) if rows else np.empty((0, width), dtype=np.float32)


def training_weights(
    contexts: Iterable[DecisionContext], hard_negative_weight: float = 2.0
) -> np.ndarray:
    selected = list(contexts)
    labels = np.asarray([context.acceptable for context in selected], dtype=np.int64)
    counts = np.maximum(1, np.bincount(labels, minlength=2))
    weights = (len(labels) / (2.0 * counts))[labels].astype(np.float32)
    for index, context in enumerate(selected):
        if not context.acceptable and context.distance_to_target_steps <= 2:
            weights[index] *= hard_negative_weight
        elif context.boundary_positive:
            weights[index] *= 1.25
    return weights


def _sigmoid(logits: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-np.clip(logits, -40.0, 40.0)))


def fit_binary_linear(
    x: np.ndarray,
    y: np.ndarray,
    sample_weights: np.ndarray,
    *,
    epochs: int,
    lr: float,
    seed: int,
) -> tuple[dict[str, object], list[float]]:
    rng = np.random.default_rng(seed)
    weights = rng.normal(0.0, 0.01, size=x.shape[1]).astype(np.float32)
    bias = np.float32(0.0)
    losses: list[float] = []
    denominator = float(np.sum(sample_weights))
    for _ in range(epochs):
        probability = _sigmoid(x @ weights + bias)
        losses.append(
            float(
                -np.sum(
                    sample_weights
                    * (y * np.log(np.clip(probability, 1e-7, 1.0)) + (1 - y) * np.log(np.clip(1 - probability, 1e-7, 1.0)))
                )
                / denominator
            )
        )
        gradient = (probability - y) * sample_weights / denominator
        weights -= lr * (x.T @ gradient + 1e-4 * weights)
        bias -= np.float32(lr * np.sum(gradient))
    return {"architecture": "binary-linear-v1", "weights": weights.tolist(), "bias": float(bias)}, losses


def fit_binary_mlp(
    x: np.ndarray,
    y: np.ndarray,
    sample_weights: np.ndarray,
    *,
    hidden_size: int,
    epochs: int,
    lr: float,
    seed: int,
) -> tuple[dict[str, object], list[float]]:
    rng = np.random.default_rng(seed)
    w1 = rng.normal(0.0, np.sqrt(2.0 / x.shape[1]), (x.shape[1], hidden_size)).astype(np.float32)
    b1 = np.zeros(hidden_size, dtype=np.float32)
    w2 = rng.normal(0.0, np.sqrt(2.0 / hidden_size), hidden_size).astype(np.float32)
    b2 = np.float32(0.0)
    parameters = [w1, b1, w2]
    first = [np.zeros_like(value) for value in parameters]
    second = [np.zeros_like(value) for value in parameters]
    first_b2 = np.float32(0.0)
    second_b2 = np.float32(0.0)
    denominator = float(np.sum(sample_weights))
    losses: list[float] = []
    for step in range(1, epochs + 1):
        pre = x @ w1 + b1
        hidden = np.maximum(pre, 0.0)
        probability = _sigmoid(hidden @ w2 + b2)
        loss = -np.sum(
            sample_weights
            * (y * np.log(np.clip(probability, 1e-7, 1.0)) + (1 - y) * np.log(np.clip(1 - probability, 1e-7, 1.0)))
        ) / denominator
        losses.append(float(loss))
        g_logit = (probability - y) * sample_weights / denominator
        g_w2 = hidden.T @ g_logit + 1e-4 * w2
        g_b2 = np.sum(g_logit).astype(np.float32)
        g_hidden = g_logit[:, None] * w2[None, :]
        g_hidden[pre <= 0.0] = 0.0
        g_w1 = x.T @ g_hidden + 1e-4 * w1
        g_b1 = g_hidden.sum(axis=0)
        gradients = [g_w1, g_b1, g_w2]
        for index, (parameter, gradient) in enumerate(zip(parameters, gradients)):
            first[index] = 0.9 * first[index] + 0.1 * gradient
            second[index] = 0.999 * second[index] + 0.001 * gradient * gradient
            parameter -= lr * (first[index] / (1.0 - 0.9**step)) / (
                np.sqrt(second[index] / (1.0 - 0.999**step)) + 1e-8
            )
        first_b2 = np.float32(0.9 * first_b2 + 0.1 * g_b2)
        second_b2 = np.float32(0.999 * second_b2 + 0.001 * g_b2 * g_b2)
        b2 -= np.float32(
            lr * (first_b2 / (1.0 - 0.9**step)) / (np.sqrt(second_b2 / (1.0 - 0.999**step)) + 1e-8)
        )
        if not np.isfinite(loss) or any(not np.all(np.isfinite(value)) for value in parameters):
            raise FloatingPointError("STOP MLP optimization produced NaN/Inf")
    return {
        "architecture": "binary-mlp-relu-v1",
        "hiddenSize": hidden_size,
        "inputWeights": w1.tolist(),
        "inputBias": b1.tolist(),
        "outputWeights": w2.tolist(),
        "outputBias": float(b2),
    }, losses


def stop_probabilities(model: dict[str, object], features: np.ndarray) -> np.ndarray:
    values = np.asarray(features, dtype=np.float32)
    one = values.ndim == 1
    if one:
        values = values[None, :]
    architecture = str(model["architecture"])
    if architecture == "binary-linear-v1":
        logits = values @ np.asarray(model["weights"], dtype=np.float32) + float(model["bias"])
    elif architecture == "binary-mlp-relu-v1":
        hidden = np.maximum(
            values @ np.asarray(model["inputWeights"], dtype=np.float32)
            + np.asarray(model["inputBias"], dtype=np.float32),
            0.0,
        )
        logits = hidden @ np.asarray(model["outputWeights"], dtype=np.float32) + float(model["outputBias"])
    else:
        raise ValueError(f"Unsupported STOP architecture: {architecture}")
    result = _sigmoid(logits)
    return result[0] if one else result


def request_stop_features(
    model: dict[str, object],
    current_image: Path,
    previous_image: Path | None,
    previous_action: int,
) -> np.ndarray:
    if previous_image is not None and not previous_image.is_file():
        raise FileNotFoundError(f"Previous history image was not retained: {previous_image}")
    current = current_image.resolve()
    previous = (previous_image or current_image).resolve()
    context = DecisionContext(
        split="serving",
        sweep_id="serving",
        configuration_family="serving",
        current_index=0,
        previous_index=0,
        current_image=current,
        previous_image=previous,
        previous_action=int(previous_action),
        reset=previous_image is None,
        acceptable=False,
        distance_to_target_steps=0,
        boundary_positive=False,
    )
    return context_features(
        context,
        size=int(model["featureSize"]),
        observation=str(model["observationMode"]),
        preprocessing=str(model["preprocessing"]),
        layout=str(model["featureLayout"]),
        normalization=model.get("normalization"),
    )


def two_stage_action(
    navigation_model: dict[str, object],
    stop_model: dict[str, object],
    navigation_features: np.ndarray,
    stop_features: np.ndarray,
) -> tuple[int, float, np.ndarray]:
    stop_score = float(stop_probabilities(stop_model, stop_features))
    navigation = np.asarray(model_probabilities(navigation_model, navigation_features), dtype=np.float64)
    if stop_score >= float(stop_model["stopThreshold"]):
        return 0, stop_score, navigation
    movement_index = 0 if navigation[0] >= navigation[2] else 2
    return (-1 if movement_index == 0 else 1), stop_score, navigation


def binary_metrics(labels: np.ndarray, scores: np.ndarray, threshold: float) -> dict[str, object]:
    truth = np.asarray(labels, dtype=bool)
    predicted = np.asarray(scores >= threshold, dtype=bool)
    tp = int(np.sum(truth & predicted))
    fp = int(np.sum(~truth & predicted))
    fn = int(np.sum(truth & ~predicted))
    tn = int(np.sum(~truth & ~predicted))
    return {
        "count": int(len(truth)),
        "threshold": float(threshold),
        "tp": tp,
        "fp": fp,
        "fn": fn,
        "tn": tn,
        "precision": tp / (tp + fp) if tp + fp else None,
        "recall": tp / (tp + fn) if tp + fn else None,
        "falsePositiveRate": fp / (fp + tn) if fp + tn else None,
    }


def context_fingerprint(contexts: Iterable[DecisionContext]) -> str:
    payload = [
        {
            "identity": context.identity,
            "current": str(context.current_image).replace("\\", "/"),
            "previous": str(context.previous_image).replace("\\", "/"),
            "previousAction": context.previous_action,
            "reset": context.reset,
            "acceptable": context.acceptable,
        }
        for context in sorted(contexts, key=lambda item: item.identity)
    ]
    return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def validate_stop_model(model: dict[str, object]) -> None:
    if model.get("schema") != STOP_MODEL_SCHEMA:
        raise ValueError("STOP checkpoint schema mismatch")
    if model.get("experimentId") != EXPERIMENT_ID:
        raise ValueError("STOP checkpoint experiment mismatch")
    if model.get("environmentContractVersion") != CONTRACT_VERSION:
        raise ValueError("STOP checkpoint contract mismatch")
    if model.get("observationSchema") != OBSERVATION_SCHEMA:
        raise ValueError("STOP checkpoint observation mismatch")
    if model.get("targetSemantics") != TARGET_SEMANTICS:
        raise ValueError("STOP checkpoint target semantics mismatch")
    width = feature_width(
        int(model["featureSize"]), str(model["observationMode"]), str(model["featureLayout"])
    )
    architecture = str(model["architecture"])
    if architecture == "binary-linear-v1" and len(model.get("weights") or []) != width:
        raise ValueError("STOP linear feature dimension mismatch")
    if architecture == "binary-mlp-relu-v1":
        if len(model.get("inputWeights") or []) != width:
            raise ValueError("STOP MLP feature dimension mismatch")
    if not 0.0 <= float(model.get("stopThreshold", -1.0)) <= 1.0:
        raise ValueError("STOP threshold is invalid")


def load_contexts(
    *, root: Path, dataset_run_id: str, split: str
) -> tuple[list[CachedTrajectory], list[DecisionContext]]:
    trajectories = load_cached_trajectories(
        root=root,
        experiment_id=EXPERIMENT_ID,
        dataset_run_id=dataset_run_id,
        split=split,
    )
    return trajectories, build_decision_contexts(trajectories)
