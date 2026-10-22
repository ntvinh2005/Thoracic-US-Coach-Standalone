from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, DEFAULT_EXPERIMENT_ID, classification_metrics, dataset_fingerprint, load_frames, project_root, save_json, stable_softmax
from train_reference_head import metrics as reference_metrics, sigmoid


def main() -> None:
    parser = argparse.ArgumentParser(description="Final held-out evaluation for frozen-USFM heads")
    parser.add_argument("task", choices=("reference", "action"))
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--cache", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/usfm_features_diverse_v2.npz")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--evaluation-group", choices=("heldout", "stress", "all"), default="heldout")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    frames_all = load_frames(experiment_id=args.experiment_id)
    fingerprint = dataset_fingerprint(frames_all)
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    cache = np.load(args.cache, allow_pickle=False)
    identities = (model.get("experimentId"), model.get("datasetFingerprint"), str(cache["experiment_id"].item()), str(cache["dataset_fingerprint"].item()))
    if identities != (args.experiment_id, fingerprint, args.experiment_id, fingerprint):
        raise SystemExit(f"Model/cache dataset identity mismatch: {identities}")
    lookup = {str(path): feature.astype(np.float32) for path, feature in zip(cache["image_paths"], cache["features"])}
    frames = [frame for frame in frames_all if frame.split == "test"]
    if args.evaluation_group != "all":
        frames = [frame for frame in frames if frame.evaluation_group == args.evaluation_group]
    if args.task == "action":
        frames = [frame for frame in frames if frame.teacher_action_defined]
    raw = np.stack([lookup[frame.image_relative] for frame in frames])
    mean = np.asarray(model["featureMean"], dtype=np.float32)
    std = np.asarray(model["featureStd"], dtype=np.float32)
    x = (raw - mean) / std
    weights = np.asarray(model["weights"], dtype=np.float32)
    bias = np.asarray(model["bias"], dtype=np.float32)
    started = time.perf_counter()
    if args.task == "reference":
        probabilities = sigmoid(x @ weights + bias)
        result = reference_metrics(np.asarray([frame.target_acceptable for frame in frames], dtype=np.int64), probabilities)
    else:
        probabilities = stable_softmax(x @ weights + bias)
        result = classification_metrics(np.asarray([ACTION_TO_INDEX[frame.teacher_action] for frame in frames], dtype=np.int64), probabilities)
    head_latency = (time.perf_counter() - started) * 1000.0 / len(frames)
    report = {
        "schemaVersion": 2,
        "task": args.task,
        "checkpoint": str(args.checkpoint),
        "experimentId": args.experiment_id,
        "datasetFingerprint": fingerprint,
        "split": "test",
        "evaluationGroup": args.evaluation_group,
        "metrics": result,
        "cachedHeadLatencyMsPerFrameCpu": head_latency,
        "latencyScope": "linear head on cached 768-D embeddings; excludes online USFM encoder",
        "finalTestWasNotUsedForModelSelection": True,
    }
    save_json(args.output, report)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
