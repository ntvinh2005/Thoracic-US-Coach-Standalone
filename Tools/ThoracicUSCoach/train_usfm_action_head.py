from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, DEFAULT_EXPERIMENT_ID, classification_metrics, dataset_fingerprint, load_frames, project_root, save_json, stable_softmax
from train_bc_policy import fit_softmax, grouped_metrics


def main() -> None:
    parser = argparse.ArgumentParser(description="Frozen-USFM linear action head")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--usfm-cache", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/usfm_features_diverse_v2.npz")
    parser.add_argument("--epochs", type=int, default=300)
    parser.add_argument("--lr", type=float, default=0.02)
    parser.add_argument("--seed", type=int, default=17)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/usfm_action_head_diverse_v2.json")
    args = parser.parse_args()

    all_frames = load_frames(experiment_id=args.experiment_id)
    fingerprint = dataset_fingerprint(all_frames)
    cache = np.load(args.usfm_cache, allow_pickle=False)
    if str(cache["experiment_id"].item()) != args.experiment_id or str(cache["dataset_fingerprint"].item()) != fingerprint:
        raise SystemExit("USFM feature cache does not match selected dataset experiment/fingerprint")
    lookup = {str(path): feature.astype(np.float32) for path, feature in zip(cache["image_paths"], cache["features"])}
    frames = [frame for frame in all_frames if frame.teacher_action_defined]
    if any(frame.image_relative not in lookup for frame in frames):
        raise SystemExit("USFM cache is incomplete for selected action frames")
    train = [frame for frame in frames if frame.split == "train"]
    dev = [frame for frame in frames if frame.split == "dev"]
    get_raw = lambda selected: np.stack([lookup[frame.image_relative] for frame in selected])
    raw_train = get_raw(train)
    mean = raw_train.mean(axis=0)
    std = raw_train.std(axis=0) + 1e-5
    get_x = lambda selected: (get_raw(selected) - mean) / std
    y_train = np.asarray([ACTION_TO_INDEX[frame.teacher_action] for frame in train], dtype=np.int64)
    started = time.perf_counter()
    weights, bias, losses = fit_softmax(get_x(train), y_train, args.epochs, args.lr, args.seed)
    elapsed = time.perf_counter() - started
    report: dict[str, object] = {
        "schemaVersion": 2,
        "model": "usfm-frozen-linear-action-head-diverse-v2",
        "encoderFrozen": True,
        "onlineDeployment": False,
        "onlineDeploymentReason": "CPU USFM encoder latency is measured separately; the Unity demo keeps the lightweight pixel policy",
        "experimentId": args.experiment_id,
        "datasetFingerprint": fingerprint,
        "actionOrder": [-1, 0, 1],
        "weights": weights.tolist(),
        "bias": bias.tolist(),
        "featureMean": mean.tolist(),
        "featureStd": std.tolist(),
        "training": {"epochs": args.epochs, "learningRate": args.lr, "seed": args.seed, "finalLoss": losses[-1], "elapsedSeconds": elapsed, "testSetInspected": False},
        "metrics": {},
    }
    for split in ("train", "dev"):
        selected = train if split == "train" else dev
        probabilities = stable_softmax(get_x(selected) @ weights + bias)
        labels = np.asarray([ACTION_TO_INDEX[frame.teacher_action] for frame in selected], dtype=np.int64)
        result = classification_metrics(labels, probabilities)
        result["byStartSide"] = grouped_metrics(selected, probabilities, "start_side")
        result["byTrajectoryFamily"] = grouped_metrics(selected, probabilities, "configuration_family")
        report["metrics"][split] = result
    save_json(args.output, report)
    print(json.dumps({"checkpoint": str(args.output), "metrics": report["metrics"], "elapsedSeconds": elapsed}, indent=2))


if __name__ == "__main__":
    main()
