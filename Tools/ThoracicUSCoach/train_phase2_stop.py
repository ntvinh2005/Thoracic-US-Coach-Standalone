from __future__ import annotations

import argparse
import json
import time
from collections import defaultdict
from pathlib import Path

import numpy as np

from active2d_contract import CONTRACT, EXPERIMENT_ID, project_root
from active2d_data import dataset_fingerprint, load_active2d_frames
from active2d_stop import STOP_SCHEMA, frame_stop_feature_matrix, stop_probabilities


def fit_binary_mlp(
    features: np.ndarray,
    labels: np.ndarray,
    *,
    hidden_size: int,
    epochs: int,
    learning_rate: float,
    seed: int,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, float, list[float]]:
    rng = np.random.default_rng(seed)
    w1 = rng.normal(0.0, np.sqrt(2.0 / features.shape[1]), (features.shape[1], hidden_size)).astype(np.float32)
    b1 = np.zeros(hidden_size, dtype=np.float32)
    w2 = rng.normal(0.0, np.sqrt(2.0 / hidden_size), hidden_size).astype(np.float32)
    b2 = np.float32(0.0)
    parameters = [w1, b1, w2, np.asarray([b2], dtype=np.float32)]
    first = [np.zeros_like(value) for value in parameters]
    second = [np.zeros_like(value) for value in parameters]
    positives = max(1, int(labels.sum()))
    negatives = max(1, len(labels) - positives)
    weights = np.where(labels > 0.5, len(labels) / (2.0 * positives), len(labels) / (2.0 * negatives)).astype(np.float32)
    losses: list[float] = []
    for step in range(1, epochs + 1):
        hidden_pre = features @ w1 + b1
        hidden = np.maximum(hidden_pre, 0.0)
        logits = hidden @ w2 + parameters[3][0]
        probabilities = 1.0 / (1.0 + np.exp(-np.clip(logits, -30.0, 30.0)))
        loss = -np.mean(weights * (
            labels * np.log(np.clip(probabilities, 1e-7, 1.0))
            + (1.0 - labels) * np.log(np.clip(1.0 - probabilities, 1e-7, 1.0))
        ))
        gradient_logits = weights * (probabilities - labels) / len(labels)
        gradient_hidden = gradient_logits[:, None] * w2[None, :] * (hidden_pre > 0.0)
        gradients = [
            features.T @ gradient_hidden + 1e-4 * w1,
            gradient_hidden.sum(axis=0),
            hidden.T @ gradient_logits + 1e-4 * w2,
            np.asarray([gradient_logits.sum()], dtype=np.float32),
        ]
        for index, (parameter, gradient) in enumerate(zip(parameters, gradients)):
            first[index] = 0.9 * first[index] + 0.1 * gradient
            second[index] = 0.999 * second[index] + 0.001 * gradient * gradient
            parameter -= learning_rate * (first[index] / (1.0 - 0.9**step)) / (
                np.sqrt(second[index] / (1.0 - 0.999**step)) + 1e-8
            )
        if not np.isfinite(loss) or any(not np.all(np.isfinite(value)) for value in parameters):
            raise FloatingPointError("Phase 2 optimizer produced NaN or Inf")
        losses.append(float(loss))
    return w1, b1, w2, float(parameters[3][0]), losses


def classification_metrics(labels: np.ndarray, scores: np.ndarray, threshold: float) -> dict[str, float | int]:
    predicted = scores >= threshold
    actual = labels > 0.5
    tp = int(np.sum(predicted & actual))
    fp = int(np.sum(predicted & ~actual))
    fn = int(np.sum(~predicted & actual))
    tn = int(np.sum(~predicted & ~actual))
    precision = tp / max(1, tp + fp)
    recall = tp / max(1, tp + fn)
    return {
        "count": len(labels), "positiveSupport": int(actual.sum()),
        "truePositive": tp, "falsePositive": fp, "falseNegative": fn, "trueNegative": tn,
        "precision": precision, "recall": recall,
        "f1": 2.0 * precision * recall / max(1e-12, precision + recall),
        "accuracy": (tp + tn) / max(1, len(labels)),
    }


def replay_stop(frames, scores: np.ndarray, threshold: float, confirmations: int) -> dict[str, float | int]:
    episodes: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        episodes[frame.episode_id].append(index)
    successes = wrong_stops = misses = observable = 0
    steps: list[int] = []
    for indices in episodes.values():
        if any(frames[index].target_acceptable for index in indices):
            observable += 1
        streak = 0
        chosen = None
        for local_step, index in enumerate(indices):
            streak = streak + 1 if scores[index] >= threshold else 0
            if streak >= confirmations:
                chosen = index
                steps.append(local_step + 1)
                break
        if chosen is None:
            misses += 1
        elif frames[chosen].target_acceptable:
            successes += 1
        else:
            wrong_stops += 1
    total = len(episodes)
    return {
        "episodes": total,
        "episodesWithTargetObservation": observable,
        "successes": successes,
        "wrongStops": wrong_stops,
        "misses": misses,
        "acquisitionRate": successes / max(1, total),
        "conditionalAcquisitionRate": successes / max(1, observable),
        "stopPrecision": successes / max(1, successes + wrong_stops),
        "meanStepsToStop": float(np.mean(steps)) if steps else None,
    }


def calibrate(frames, labels: np.ndarray, scores: np.ndarray) -> dict[str, object]:
    candidates: list[dict[str, object]] = []
    for threshold in np.linspace(0.30, 0.99, 70):
        for confirmations in (1, 2):
            episode = replay_stop(frames, scores, float(threshold), confirmations)
            classification = classification_metrics(labels, scores, float(threshold))
            score = (
                float(episode["acquisitionRate"])
                + 0.35 * float(episode["stopPrecision"])
                + 0.10 * float(classification["f1"])
                - 0.5 * float(episode["wrongStops"]) / max(1, int(episode["episodes"]))
            )
            candidates.append({
                "threshold": float(threshold), "confirmations": confirmations,
                "selectionScore": score, "classification": classification, "episodeReplay": episode,
            })
    safe = [
        item for item in candidates
        if float(item["episodeReplay"]["stopPrecision"]) >= 0.90
        and float(item["classification"]["precision"]) >= 0.75
    ]
    selected = max(
        safe or candidates,
        key=lambda item: (
            item["selectionScore"], item["episodeReplay"]["acquisitionRate"],
            item["episodeReplay"]["stopPrecision"], item["threshold"], item["confirmations"],
        ),
    )
    return {"selected": selected, "candidateCount": len(candidates)}


def main() -> None:
    parser = argparse.ArgumentParser(description="Train image/history-only Active 2D STOP detector")
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--hidden-size", type=int, default=64)
    parser.add_argument("--epochs", type=int, default=500)
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--positive-augment", type=int, default=0)
    parser.add_argument("--augment-noise", type=float, default=0.015)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    frames = load_active2d_frames(args.dataset_run_id)
    train = [frame for frame in frames if frame.split == "train" and frame.contact_valid and frame.workspace_valid]
    dev = [frame for frame in frames if frame.split == "dev" and frame.contact_valid and frame.workspace_valid]
    if not train or not dev:
        raise SystemExit("Phase 2 training requires non-empty train and dev splits")
    if {frame.family for frame in train} & {frame.family for frame in dev}:
        raise SystemExit("Configuration-family leakage between train and dev")
    for name, split, minimum in (("train", train, 20), ("dev", dev, 5)):
        positives = sum(frame.target_acceptable for frame in split)
        if positives < minimum:
            raise SystemExit(f"Phase 2 {name} split has only {positives} positive frames; need at least {minimum}")

    started = time.perf_counter()
    fingerprint = dataset_fingerprint([*train, *dev])
    cache = (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/cache"
        / f"phase2_features_{args.dataset_run_id}_{fingerprint}.npz"
    )
    if cache.is_file():
        cached = np.load(cache, allow_pickle=False)
        x_train = np.asarray(cached["x_train"], dtype=np.float32)
        x_dev = np.asarray(cached["x_dev"], dtype=np.float32)
        if len(x_train) != len(train) or len(x_dev) != len(dev):
            raise SystemExit("Phase 2 feature cache count mismatch")
    else:
        x_train = frame_stop_feature_matrix(train)
        x_dev = frame_stop_feature_matrix(dev)
        cache.parent.mkdir(parents=True, exist_ok=True)
        temporary_cache = cache.with_suffix(cache.suffix + ".tmp")
        with temporary_cache.open("wb") as handle:
            np.savez_compressed(handle, x_train=x_train, x_dev=x_dev)
        temporary_cache.replace(cache)
    y_train = np.asarray([frame.target_acceptable for frame in train], dtype=np.float32)
    y_dev = np.asarray([frame.target_acceptable for frame in dev], dtype=np.float32)
    fit_x = x_train
    fit_y = y_train
    if args.positive_augment > 0:
        positive = x_train[y_train > 0.5]
        if len(positive):
            rng = np.random.default_rng(args.seed + 7919)
            augmented = np.repeat(positive, args.positive_augment, axis=0)
            augmented += rng.normal(0.0, args.augment_noise, augmented.shape).astype(np.float32)
            fit_x = np.concatenate((x_train, augmented), axis=0)
            fit_y = np.concatenate((y_train, np.ones(len(augmented), dtype=np.float32)), axis=0)
    w1, b1, w2, b2, losses = fit_binary_mlp(
        fit_x, fit_y, hidden_size=args.hidden_size, epochs=args.epochs,
        learning_rate=args.lr, seed=args.seed,
    )
    architecture = {
        "type": "fixed-convolutional-history+binary-mlp-v1",
        "featureWidth": int(x_train.shape[1]), "hiddenSize": args.hidden_size,
        "inputWeights": w1.tolist(), "inputBias": b1.tolist(),
        "outputWeights": w2.tolist(), "outputBias": b2,
    }
    provisional = {"architecture": architecture}
    train_scores = stop_probabilities(provisional, x_train)
    dev_scores = stop_probabilities(provisional, x_dev)
    calibration = calibrate(dev, y_dev, dev_scores)
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
            "seed": args.seed, "epochs": args.epochs, "learningRate": args.lr,
            "positiveFeatureAugmentCopies": args.positive_augment,
            "positiveFeatureAugmentNoiseStd": args.augment_noise,
            "finalLoss": losses[-1], "elapsedSeconds": time.perf_counter() - started,
            "featureCache": str(cache),
            "heldoutTestUsedForSelection": False, "splitUnit": "independent episode/configuration family",
        },
        "metrics": {
            "trainClassification": classification_metrics(y_train, train_scores, threshold),
            "devClassification": classification_metrics(y_dev, dev_scores, threshold),
            "devEpisodeReplay": replay_stop(dev, dev_scores, threshold, confirmations),
            "devCalibration": calibration,
        },
    }
    output = args.output or (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/checkpoints"
        / f"phase2_stop_{args.dataset_run_id}_seed{args.seed}.json"
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(json.dumps(report, indent=2), encoding="utf-8")
    temporary.replace(output)
    print(json.dumps({
        "output": str(output), "train": report["metrics"]["trainClassification"],
        "dev": report["metrics"]["devClassification"],
        "devEpisodeReplay": report["metrics"]["devEpisodeReplay"],
        "stopRule": report["stopRule"], "training": report["training"],
    }, indent=2))


if __name__ == "__main__":
    main()
