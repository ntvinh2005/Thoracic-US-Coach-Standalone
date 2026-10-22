from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import (
    ACTION_TO_INDEX,
    DEFAULT_EXPERIMENT_ID,
    classification_metrics,
    dataset_fingerprint,
    feature_matrix,
    load_frames,
    load_transitions,
    project_root,
    save_json,
    stable_softmax,
    transition_dataset_fingerprint,
    transition_feature_matrix,
)


def fit_softmax(x: np.ndarray, y: np.ndarray, epochs: int, lr: float, seed: int) -> tuple[np.ndarray, np.ndarray, list[float]]:
    rng = np.random.default_rng(seed)
    weights = rng.normal(0.0, 0.01, size=(x.shape[1], 3)).astype(np.float32)
    bias = np.zeros(3, dtype=np.float32)
    one_hot = np.eye(3, dtype=np.float32)[y]
    counts = np.maximum(1, np.bincount(y, minlength=3))
    class_weights = len(y) / (3.0 * counts)
    sample_weights = class_weights[y].reshape(-1, 1).astype(np.float32)
    losses: list[float] = []
    for _ in range(epochs):
        probabilities = stable_softmax(x @ weights + bias)
        loss = -np.mean(sample_weights[:, 0] * np.log(np.clip(probabilities[np.arange(len(y)), y], 1e-7, 1.0)))
        gradient = (probabilities - one_hot) * sample_weights / len(y)
        weights -= lr * (x.T @ gradient + 1e-4 * weights)
        bias -= lr * gradient.sum(axis=0)
        losses.append(float(loss))
    return weights, bias, losses


def grouped_metrics(frames, probabilities: np.ndarray, attribute: str) -> dict[str, object]:
    result: dict[str, object] = {}
    for value in sorted({getattr(frame, attribute) for frame in frames}):
        indices = [index for index, frame in enumerate(frames) if getattr(frame, attribute) == value]
        labels = np.asarray([ACTION_TO_INDEX[frames[index].teacher_action] for index in indices], dtype=np.int64)
        result[str(value)] = classification_metrics(labels, probabilities[indices])
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description="Balanced linear BC baseline on clean ultrasound pixels")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--observation", choices=("current", "history"), default="current")
    parser.add_argument("--epochs", type=int, default=300)
    parser.add_argument("--lr", type=float, default=0.05)
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/bc_pixel_current_contract_v3.json")
    args = parser.parse_args()

    all_frames = load_frames(experiment_id=args.experiment_id)
    all_transitions = load_transitions(experiment_id=args.experiment_id) if args.observation == "history" else []
    rows = [
        item for item in (all_transitions if args.observation == "history" else all_frames)
        if item.teacher_action_defined and item.contact_valid and getattr(item, "workspace_valid", True)
    ]
    train = [item for item in rows if item.split == "train"]
    dev = [item for item in rows if item.split == "dev"]
    if not train or not dev:
        raise SystemExit("The selected experiment needs complete train and dev trajectory families")
    started = time.perf_counter()
    matrix = transition_feature_matrix if args.observation == "history" else lambda selected: feature_matrix(selected, observation="current")
    x_train = matrix(train)
    y_train = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in train], dtype=np.int64)
    weights, bias, losses = fit_softmax(x_train, y_train, args.epochs, args.lr, args.seed)
    elapsed = time.perf_counter() - started

    report: dict[str, object] = {
        "schemaVersion": 3,
        "model": f"pixel16-softmax-bc-{args.observation}-contract-v3",
        "input": "clean ultrasound pixels only" if args.observation == "current" else "previous/current clean ultrasound pixels plus previous action",
        "observationMode": args.observation,
        "featureSize": 16,
        "actionOrder": [-1, 0, 1],
        "experimentId": args.experiment_id,
        "datasetFingerprint": transition_dataset_fingerprint(all_transitions) if args.observation == "history" else dataset_fingerprint(all_frames),
        "trainingRows": "genuine executed transitions" if args.observation == "history" else "fixed-pose current observations",
        "weights": weights.tolist(),
        "bias": bias.tolist(),
        "training": {
            "epochs": args.epochs,
            "learningRate": args.lr,
            "seed": args.seed,
            "finalLoss": losses[-1],
            "classBalancing": "inverse-frequency weights on train only",
            "elapsedSeconds": elapsed,
            "testSetInspected": False,
        },
        "metrics": {},
    }
    for split in ("train", "dev"):
        selected = [item for item in rows if item.split == split]
        x = matrix(selected)
        y = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in selected], dtype=np.int64)
        probabilities = stable_softmax(x @ weights + bias)
        metrics = classification_metrics(y, probabilities)
        metrics["byTrajectoryFamily"] = grouped_metrics(selected, probabilities, "configuration_family")
        if args.observation == "history":
            metrics["byTransitionKind"] = grouped_metrics(selected, probabilities, "transition_kind")
        else:
            metrics["byStartSide"] = grouped_metrics(selected, probabilities, "start_side")
        report["metrics"][split] = metrics
    save_json(args.output, report)
    print(json.dumps({"checkpoint": str(args.output), "metrics": report["metrics"], "elapsedSeconds": elapsed}, indent=2))


if __name__ == "__main__":
    main()
