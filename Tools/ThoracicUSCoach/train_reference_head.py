from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import DEFAULT_EXPERIMENT_ID, dataset_fingerprint, feature_matrix, load_frames, project_root, save_json


def sigmoid(value: np.ndarray) -> np.ndarray:
    value = np.clip(value, -30.0, 30.0)
    return 1.0 / (1.0 + np.exp(-value))


def metrics(labels: np.ndarray, probabilities: np.ndarray) -> dict[str, object]:
    predictions = probabilities >= 0.5
    tp = int(np.sum(predictions & (labels == 1)))
    tn = int(np.sum(~predictions & (labels == 0)))
    fp = int(np.sum(predictions & (labels == 0)))
    fn = int(np.sum(~predictions & (labels == 1)))
    precision = tp / max(1, tp + fp)
    recall = tp / max(1, tp + fn)
    return {
        "count": int(len(labels)),
        "classDistribution": {"acceptable": int(np.sum(labels == 1)), "unacceptable": int(np.sum(labels == 0))},
        "accuracy": float((predictions == labels).mean()) if len(labels) else float("nan"),
        "precision": precision,
        "recall": recall,
        "f1": 2 * precision * recall / max(1e-12, precision + recall),
        "confusion": [[tn, fp], [fn, tp]],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Frozen-feature reference-view recognition head")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--features", choices=("pixels", "usfm"), default="pixels")
    parser.add_argument("--usfm-cache", type=Path, help="NPZ from usfm_adapter.py extract-dataset")
    parser.add_argument("--epochs", type=int, default=300)
    parser.add_argument("--lr", type=float, default=0.05)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/reference_head_diverse_v2.json")
    args = parser.parse_args()

    frames = load_frames(experiment_id=args.experiment_id)
    fingerprint = dataset_fingerprint(frames)
    if args.features == "usfm":
        if args.usfm_cache is None:
            raise SystemExit("--features usfm requires --usfm-cache")
        cached = np.load(args.usfm_cache, allow_pickle=False)
        cache_experiment = str(cached["experiment_id"].item()) if "experiment_id" in cached else "missing"
        cache_fingerprint = str(cached["dataset_fingerprint"].item()) if "dataset_fingerprint" in cached else "missing"
        if cache_experiment != args.experiment_id or cache_fingerprint != fingerprint:
            raise SystemExit(f"Stale USFM cache: cache experiment/fingerprint {cache_experiment}/{cache_fingerprint}, expected {args.experiment_id}/{fingerprint}")
        lookup = {str(name): vector for name, vector in zip(cached["image_paths"], cached["features"])}
        missing = [frame.image_relative for frame in frames if frame.image_relative not in lookup]
        if missing:
            raise SystemExit(f"USFM cache misses {len(missing)} selected frames; regenerate it")
        raw_get_x = lambda selected: np.stack([lookup[frame.image_relative] for frame in selected]).astype(np.float32)
    else:
        raw_get_x = feature_matrix

    train = [frame for frame in frames if frame.split == "train"]
    dev = [frame for frame in frames if frame.split == "dev"]
    if not train or not dev:
        raise SystemExit("The selected experiment needs train and dev sweeps")
    started = time.perf_counter()
    raw_train = raw_get_x(train)
    feature_mean = raw_train.mean(axis=0)
    feature_std = raw_train.std(axis=0) + 1e-5
    get_x = lambda selected: (raw_get_x(selected) - feature_mean) / feature_std
    x = get_x(train)
    y = np.asarray([frame.target_acceptable for frame in train], dtype=np.float32)
    weights = np.zeros(x.shape[1], dtype=np.float32)
    bias = 0.0
    positive_weight = float(np.sum(y == 0) / max(1, np.sum(y == 1)))
    for _ in range(args.epochs):
        probabilities = sigmoid(x @ weights + bias)
        sample_weight = np.where(y == 1, positive_weight, 1.0)
        gradient = (probabilities - y) * sample_weight / len(y)
        weights -= args.lr * (x.T @ gradient + 1e-4 * weights)
        bias -= args.lr * float(gradient.sum())
    elapsed = time.perf_counter() - started

    report: dict[str, object] = {
        "schemaVersion": 2,
        "model": f"{args.features}-frozen-linear-reference-head-diverse-v2",
        "encoderFrozen": True,
        "experimentId": args.experiment_id,
        "datasetFingerprint": fingerprint,
        "weights": weights.tolist(),
        "bias": bias,
        "featureMean": feature_mean.tolist(),
        "featureStd": feature_std.tolist(),
        "training": {"epochs": args.epochs, "learningRate": args.lr, "elapsedSeconds": elapsed, "testSetInspected": False},
        "metrics": {},
    }
    for split in ("train", "dev"):
        selected = [frame for frame in frames if frame.split == split]
        labels = np.asarray([frame.target_acceptable for frame in selected], dtype=np.int64)
        report["metrics"][split] = metrics(labels, sigmoid(get_x(selected) @ weights + bias))
    save_json(args.output, report)
    print(json.dumps({"checkpoint": str(args.output), "metrics": report["metrics"], "elapsedSeconds": elapsed}, indent=2))


if __name__ == "__main__":
    main()
