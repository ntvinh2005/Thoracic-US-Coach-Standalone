from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from active2d_contract import CONTRACT, EXPERIMENT_ID, project_root
from active2d_data import dataset_fingerprint, load_active2d_frames
from active2d_stop import (
    STOP_SCHEMA,
    frame_stop_feature_matrix,
    frame_stop_feature_matrix_v2,
    stop_probabilities,
)
from train_phase2_stop import calibrate, classification_metrics, replay_stop


def load_or_create_features(
    dataset_run_id: str,
    train,
    dev,
    fingerprint: str,
    feature_extractor: str,
) -> tuple[np.ndarray, np.ndarray, Path]:
    cache = (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/cache"
        / f"phase2_features_{feature_extractor}_{dataset_run_id}_{fingerprint}.npz"
    )
    if cache.is_file():
        cached = np.load(cache, allow_pickle=False)
        x_train = np.asarray(cached["x_train"], dtype=np.float32)
        x_dev = np.asarray(cached["x_dev"], dtype=np.float32)
    else:
        builder = frame_stop_feature_matrix_v2 if feature_extractor == "spatial-v2" else frame_stop_feature_matrix
        x_train = builder(train)
        x_dev = builder(dev)
        cache.parent.mkdir(parents=True, exist_ok=True)
        temporary = cache.with_suffix(cache.suffix + ".tmp")
        with temporary.open("wb") as handle:
            np.savez_compressed(handle, x_train=x_train, x_dev=x_dev)
        temporary.replace(cache)
    if len(x_train) != len(train) or len(x_dev) != len(dev):
        raise SystemExit("Phase 2 feature cache count mismatch")
    return x_train, x_dev, cache


def main() -> None:
    parser = argparse.ArgumentParser(description="Train image/history-only continuous quality STOP regressor")
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--ridge", type=float, default=10.0)
    parser.add_argument("--features", choices=("history-v1", "spatial-v2"), default="history-v1")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    frames = load_active2d_frames(args.dataset_run_id)
    train = [frame for frame in frames if frame.split == "train" and frame.contact_valid and frame.workspace_valid]
    dev = [frame for frame in frames if frame.split == "dev" and frame.contact_valid and frame.workspace_valid]
    if not train or not dev:
        raise SystemExit("Phase 2 score training requires non-empty train and dev splits")
    if {frame.family for frame in train} & {frame.family for frame in dev}:
        raise SystemExit("Configuration-family leakage between train and dev")

    started = time.perf_counter()
    fingerprint = dataset_fingerprint([*train, *dev])
    x_train, x_dev, cache = load_or_create_features(
        args.dataset_run_id, train, dev, fingerprint, args.features
    )
    y_train = np.asarray([frame.target_score for frame in train], dtype=np.float64)
    y_dev_score = np.asarray([frame.target_score for frame in dev], dtype=np.float64)
    y_train_stop = np.asarray([frame.target_acceptable for frame in train], dtype=np.float32)
    y_dev_stop = np.asarray([frame.target_acceptable for frame in dev], dtype=np.float32)

    mean = x_train.mean(axis=0, dtype=np.float64)
    scale = x_train.std(axis=0, dtype=np.float64)
    scale = np.maximum(scale, 1e-3)
    normalized = (x_train.astype(np.float64) - mean) / scale
    target_mean = float(y_train.mean())
    gram = normalized.T @ normalized
    gram.flat[:: gram.shape[0] + 1] += args.ridge
    weights = np.linalg.solve(gram, normalized.T @ (y_train - target_mean))
    architecture = {
        "type": "standardized-ridge-quality-score-v1",
        "featureExtractor": args.features,
        "featureWidth": int(x_train.shape[1]),
        "featureMean": mean.astype(np.float32).tolist(),
        "featureScale": scale.astype(np.float32).tolist(),
        "weights": weights.astype(np.float32).tolist(),
        "bias": target_mean,
        "ridge": args.ridge,
    }
    provisional = {"architecture": architecture}
    train_scores = stop_probabilities(provisional, x_train)
    dev_scores = stop_probabilities(provisional, x_dev)
    calibration = calibrate(dev, y_dev_stop, dev_scores)
    selected = calibration["selected"]
    threshold = float(selected["threshold"])
    confirmations = int(selected["confirmations"])
    report = {
        "schemaVersion": 1, "schema": STOP_SCHEMA, "contract": CONTRACT,
        "experimentId": EXPERIMENT_ID, "datasetRunId": args.dataset_run_id,
        "datasetFingerprint": fingerprint,
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": ["pose", "targetCoordinates", "score", "pathIndex", "oracleDirection"],
        "architecture": architecture,
        "stopRule": {"threshold": threshold, "requiredConfirmations": confirmations},
        "training": {
            "objective": "continuous privileged quality score as training label only",
            "ridge": args.ridge, "featureExtractor": args.features,
            "elapsedSeconds": time.perf_counter() - started,
            "featureCache": str(cache), "heldoutTestUsedForSelection": False,
            "splitUnit": "independent episode/configuration family",
        },
        "metrics": {
            "trainQualityMae": float(np.mean(np.abs(train_scores - y_train))),
            "devQualityMae": float(np.mean(np.abs(dev_scores - y_dev_score))),
            "trainClassification": classification_metrics(y_train_stop, train_scores, threshold),
            "devClassification": classification_metrics(y_dev_stop, dev_scores, threshold),
            "devEpisodeReplay": replay_stop(dev, dev_scores, threshold, confirmations),
            "devCalibration": calibration,
        },
    }
    output = args.output or (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/checkpoints"
        / f"phase2_score_ridge{args.ridge:g}.json"
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(json.dumps(report, indent=2), encoding="utf-8")
    temporary.replace(output)
    print(json.dumps({
        "output": str(output), "ridge": args.ridge,
        "trainQualityMae": report["metrics"]["trainQualityMae"],
        "devQualityMae": report["metrics"]["devQualityMae"],
        "dev": report["metrics"]["devClassification"],
        "devEpisodeReplay": report["metrics"]["devEpisodeReplay"],
        "stopRule": report["stopRule"], "training": report["training"],
    }, indent=2))


if __name__ == "__main__":
    main()
