from __future__ import annotations

import argparse
import hashlib
import json
import time
from pathlib import Path

import numpy as np

from active2d_contract import CONTRACT, EXPERIMENT_ID, canonical_sha256, project_root
from active2d_data import Active2DFrame, load_active2d_frames
from active2d_stop import (
    STOP_SCHEMA,
    frame_stop_feature_matrix,
    frame_stop_feature_matrix_v2,
    stop_probabilities,
)
from train_phase2_stop import calibrate, classification_metrics, replay_stop


def load_live_frames(before_episode: str) -> list[Active2DFrame]:
    root = project_root()
    run_root = root / "Artifacts/ThoracicUSCoach/active2d/runs"
    audit_root = root / "Artifacts/ThoracicUSCoach/active2d/runs_privileged"
    frames: list[Active2DFrame] = []
    for run_path in sorted(run_root.glob("LIVE_A2D_*.json")):
        episode_id = run_path.stem
        if episode_id >= before_episode:
            continue
        audit_path = audit_root / run_path.name
        if not audit_path.is_file():
            continue
        run = json.loads(run_path.read_text(encoding="utf-8"))
        audit = json.loads(audit_path.read_text(encoding="utf-8"))
        trace = run.get("trace", [])
        handoff = next((int(item["step"]) for item in trace if item.get("action") == "PHASEHANDOFF"), None)
        if handoff is None:
            continue
        annotations = {int(item["index"]): item for item in audit.get("frames", [])}
        bucket = int(hashlib.sha256(episode_id.encode("utf-8")).hexdigest()[:8], 16) % 5
        split = "dev" if bucket == 4 else "train"
        previous_action = "PHASEHANDOFF"
        first = True
        for item in trace:
            step = int(item["step"])
            if step <= handoff:
                continue
            if item.get("phase") not in {"LateralAcquisition", "FinalSuccess", "FinalFailure"}:
                continue
            annotation = annotations.get(step)
            if annotation is None:
                continue
            image_relative = str(item["image"]).replace("\\", "/")
            image = (root / image_relative).resolve()
            if not image.is_file():
                continue
            frames.append(Active2DFrame(
                image=image,
                image_relative=image_relative,
                image_sha256=canonical_sha256({"episode": episode_id, "step": step}),
                episode_id=episode_id,
                dataset_run_id="live_tuning_pre_freeze",
                split=split,
                family=f"live_family_{episode_id}",
                seed=0,
                index=step,
                issued_action=previous_action,
                action_accepted=bool(item.get("accepted", True)),
                episode_reset=first,
                level=str(annotation.get("nearestLevel", "OUTSIDE_T6_T8")),
                t7_distance_mm=float(annotation.get("t7DistanceMm", float("inf"))),
                t7_acceptable=bool(annotation.get("t7LevelAcceptable", False)),
                target_acceptable=bool(annotation.get("finalTargetAcceptable", False)),
                target_score=float(annotation.get("finalTargetScore", 0.0)),
                contact_valid=bool(annotation.get("contactValid", False)),
                workspace_valid=bool(annotation.get("workspaceValid", False)),
            ))
            previous_action = str(item.get("action") or "RESET").upper()
            first = False
    return frames


def main() -> None:
    parser = argparse.ArgumentParser(description="Adapt image-only STOP score model with pre-freeze live audits")
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--live-before", required=True)
    parser.add_argument("--ridge", type=float, default=0.1)
    parser.add_argument("--features", choices=("history-v1", "spatial-v2"), default="history-v1")
    parser.add_argument("--objective", choices=("ridge", "logistic"), default="ridge")
    parser.add_argument("--epochs", type=int, default=300)
    parser.add_argument("--lr", type=float, default=0.01)
    parser.add_argument("--near-positive-weight", type=int, default=8)
    parser.add_argument("--positive-weight", type=int, default=24)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    captured = load_active2d_frames(args.dataset_run_id)
    live = load_live_frames(args.live_before)
    train = [f for f in captured if f.split == "train" and f.contact_valid and f.workspace_valid]
    dev = [f for f in captured if f.split == "dev" and f.contact_valid and f.workspace_valid]
    live_train = [f for f in live if f.split == "train" and f.contact_valid and f.workspace_valid]
    live_dev = [f for f in live if f.split == "dev" and f.contact_valid and f.workspace_valid]
    train.extend(live_train)
    dev.extend(live_dev)
    if not live_train or not live_dev:
        raise SystemExit("Need non-empty family-separated live train and dev episodes")
    if {f.family for f in train} & {f.family for f in dev}:
        raise SystemExit("Family leakage between adapted train and dev")

    started = time.perf_counter()
    identity = canonical_sha256({
        "dataset": args.dataset_run_id,
        "liveBefore": args.live_before,
        "features": args.features,
        "train": [(f.episode_id, f.index, f.image_sha256) for f in train],
        "dev": [(f.episode_id, f.index, f.image_sha256) for f in dev],
    })
    cache = project_root() / "Artifacts/ThoracicUSCoach/active2d/cache" / f"phase2_live_adapted_{identity}.npz"
    if cache.is_file():
        values = np.load(cache, allow_pickle=False)
        x_train = np.asarray(values["x_train"], dtype=np.float32)
        x_dev = np.asarray(values["x_dev"], dtype=np.float32)
    else:
        builder = frame_stop_feature_matrix_v2 if args.features == "spatial-v2" else frame_stop_feature_matrix
        x_train = builder(train)
        x_dev = builder(dev)
        cache.parent.mkdir(parents=True, exist_ok=True)
        temporary = cache.with_suffix(cache.suffix + ".tmp")
        with temporary.open("wb") as handle:
            np.savez_compressed(handle, x_train=x_train, x_dev=x_dev)
        temporary.replace(cache)

    y_train = np.asarray([f.target_score for f in train], dtype=np.float64)
    y_dev_score = np.asarray([f.target_score for f in dev], dtype=np.float64)
    y_train_stop = np.asarray([f.target_acceptable for f in train], dtype=np.float32)
    y_dev_stop = np.asarray([f.target_acceptable for f in dev], dtype=np.float32)
    if args.objective == "ridge":
        repeats = np.ones(len(train), dtype=np.int32)
        repeats[y_train >= 0.52] = np.maximum(repeats[y_train >= 0.52], args.near_positive_weight)
        repeats[y_train_stop > 0.5] = np.maximum(repeats[y_train_stop > 0.5], args.positive_weight)
        fit_x = np.repeat(x_train, repeats, axis=0).astype(np.float64)
        fit_y = np.repeat(y_train, repeats)
        mean = fit_x.mean(axis=0)
        scale = np.maximum(fit_x.std(axis=0), 1e-3)
        normalized = (fit_x - mean) / scale
        bias = float(fit_y.mean())
        gram = normalized.T @ normalized
        gram.flat[:: gram.shape[0] + 1] += args.ridge
        weights = np.linalg.solve(gram, normalized.T @ (fit_y - bias))
        architecture_type = "standardized-ridge-quality-score-v1"
    else:
        mean = x_train.mean(axis=0, dtype=np.float64)
        scale = np.maximum(x_train.std(axis=0, dtype=np.float64), 1e-3)
        normalized = ((x_train.astype(np.float64) - mean) / scale).astype(np.float32)
        labels = y_train_stop.astype(np.float32)
        positives = max(1, int(labels.sum()))
        sample_weights = np.ones(len(labels), dtype=np.float32)
        sample_weights[labels > 0.5] = min(100.0, (len(labels) - positives) / positives)
        sample_weights[(labels < 0.5) & (y_train >= 0.52)] *= float(args.near_positive_weight)
        denominator = float(sample_weights.sum())
        weights = np.zeros(normalized.shape[1], dtype=np.float32)
        bias = 0.0
        first_w = np.zeros_like(weights)
        second_w = np.zeros_like(weights)
        first_b = second_b = 0.0
        for epoch in range(1, args.epochs + 1):
            logits = normalized @ weights + bias
            probabilities = 1.0 / (1.0 + np.exp(-np.clip(logits, -30.0, 30.0)))
            gradient_logits = sample_weights * (probabilities - labels) / denominator
            gradient_w = normalized.T @ gradient_logits + args.ridge * 1e-4 * weights
            gradient_b = float(gradient_logits.sum())
            first_w = 0.9 * first_w + 0.1 * gradient_w
            second_w = 0.999 * second_w + 0.001 * gradient_w * gradient_w
            first_b = 0.9 * first_b + 0.1 * gradient_b
            second_b = 0.999 * second_b + 0.001 * gradient_b * gradient_b
            weights -= args.lr * (first_w / (1.0 - 0.9**epoch)) / (
                np.sqrt(second_w / (1.0 - 0.999**epoch)) + 1e-8
            )
            bias -= args.lr * (first_b / (1.0 - 0.9**epoch)) / (
                np.sqrt(second_b / (1.0 - 0.999**epoch)) + 1e-8
            )
        architecture_type = "standardized-logistic-stop-v1"
    architecture = {
        "type": architecture_type,
        "featureExtractor": args.features,
        "featureWidth": int(x_train.shape[1]),
        "featureMean": mean.astype(np.float32).tolist(),
        "featureScale": scale.astype(np.float32).tolist(),
        "weights": weights.astype(np.float32).tolist(),
        "bias": float(bias),
        "ridge": args.ridge,
    }
    provisional = {"architecture": architecture}
    train_scores = stop_probabilities(provisional, x_train)
    dev_scores = stop_probabilities(provisional, x_dev)
    calibration = calibrate(dev, y_dev_stop, dev_scores)
    selected = calibration["selected"]
    checkpoint = {
        "schemaVersion": 1,
        "schema": STOP_SCHEMA,
        "contract": CONTRACT,
        "experimentId": EXPERIMENT_ID,
        "datasetRunId": args.dataset_run_id + "+live_tuning_pre_freeze",
        "datasetFingerprint": identity,
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": ["pose", "targetCoordinates", "score", "pathIndex", "oracleDirection"],
        "architecture": architecture,
        "stopRule": {
            "threshold": float(selected["threshold"]),
            "requiredConfirmations": int(selected["confirmations"]),
        },
        "training": {
            "objective": args.objective,
            "liveEpisodeExclusiveUpperBound": args.live_before,
            "ridge": args.ridge,
            "featureExtractor": args.features,
            "nearPositiveWeight": args.near_positive_weight,
            "positiveWeight": args.positive_weight,
            "epochs": args.epochs if args.objective == "logistic" else None,
            "learningRate": args.lr if args.objective == "logistic" else None,
            "heldoutFutureLiveUsedForSelection": False,
            "familySeparated": True,
            "capturedTrainFrames": len(train) - len(live_train),
            "capturedDevFrames": len(dev) - len(live_dev),
            "liveTrainFrames": len(live_train),
            "liveDevFrames": len(live_dev),
            "elapsedSeconds": time.perf_counter() - started,
            "featureCache": str(cache),
        },
        "metrics": {
            "trainQualityMae": float(np.mean(np.abs(train_scores - y_train))),
            "devQualityMae": float(np.mean(np.abs(dev_scores - y_dev_score))),
            "trainClassification": classification_metrics(y_train_stop, train_scores, float(selected["threshold"])),
            "devClassification": classification_metrics(y_dev_stop, dev_scores, float(selected["threshold"])),
            "devEpisodeReplay": replay_stop(dev, dev_scores, float(selected["threshold"]), int(selected["confirmations"])),
            "devCalibration": calibration,
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".tmp")
    temporary.write_text(json.dumps(checkpoint, indent=2), encoding="utf-8")
    temporary.replace(args.output)
    print(json.dumps({
        "output": str(args.output),
        "stopRule": checkpoint["stopRule"],
        "training": checkpoint["training"],
        "metrics": {key: value for key, value in checkpoint["metrics"].items() if key != "devCalibration"},
    }, indent=2))


if __name__ == "__main__":
    main()
