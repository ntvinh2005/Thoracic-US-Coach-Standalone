from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from coach_data import save_json
from stop_policy import binary_metrics, feature_matrix, load_contexts, stop_probabilities, validate_stop_model


def summarize(values: np.ndarray) -> dict[str, object]:
    values = np.asarray(values, dtype=np.float64)
    if not len(values):
        return {"count": 0}
    return {
        "count": int(len(values)),
        "mean": float(values.mean()),
        "std": float(values.std()),
        "min": float(values.min()),
        "q25": float(np.quantile(values, 0.25)),
        "median": float(np.median(values)),
        "q75": float(np.quantile(values, 0.75)),
        "max": float(values.max()),
    }


def auc(labels: np.ndarray, scores: np.ndarray) -> float:
    order = np.argsort(scores, kind="mergesort")
    ranks = np.empty(len(scores), dtype=np.float64)
    start = 0
    while start < len(scores):
        end = start + 1
        while end < len(scores) and scores[order[end]] == scores[order[start]]:
            end += 1
        ranks[order[start:end]] = (start + 1 + end) / 2.0
        start = end
    positives = labels.astype(bool)
    n_pos = int(positives.sum())
    n_neg = len(labels) - n_pos
    return float((ranks[positives].sum() - n_pos * (n_pos + 1) / 2.0) / (n_pos * n_neg))


def best_recall95(labels: np.ndarray, scores: np.ndarray) -> dict[str, object]:
    candidates = np.unique(np.concatenate(([0.0, 1.0], scores)))
    eligible = []
    for threshold in candidates:
        metrics = binary_metrics(labels, scores, float(threshold))
        if metrics["recall"] is not None and metrics["recall"] >= 0.95:
            eligible.append(metrics)
    return min(eligible, key=lambda item: (item["falsePositiveRate"], -item["precision"], -item["threshold"]))


def evaluate(contexts, model) -> dict[str, object]:
    x = feature_matrix(
        contexts,
        size=int(model["featureSize"]),
        observation=str(model["observationMode"]),
        preprocessing=str(model["preprocessing"]),
        layout=str(model["featureLayout"]),
        normalization=model.get("normalization"),
    )
    labels = np.asarray([context.acceptable for context in contexts], dtype=bool)
    scores = np.asarray(stop_probabilities(model, x), dtype=np.float64)
    masks = {
        "reset": np.asarray([context.reset for context in contexts]),
        "movement": np.asarray([not context.reset for context in contexts]),
        "positiveBoundary": np.asarray([context.boundary_positive for context in contexts]),
        "positiveInterior": np.asarray([context.acceptable and not context.boundary_positive for context in contexts]),
        "negativeNearTarget": np.asarray([not context.acceptable and context.distance_to_target_steps <= 2 for context in contexts]),
        "negativeEasy": np.asarray([not context.acceptable and context.distance_to_target_steps >= 3 for context in contexts]),
    }
    groups = {}
    for name, mask in masks.items():
        groups[name] = {
            "labels": {"positive": int(labels[mask].sum()), "total": int(mask.sum())},
            "scores": summarize(scores[mask]),
            "metrics": binary_metrics(labels[mask], scores[mask], float(model["stopThreshold"])) if mask.any() and len(np.unique(labels[mask])) > 1 else None,
        }
    return {
        "count": len(contexts),
        "auc": auc(labels, scores),
        "frozenThreshold": binary_metrics(labels, scores, float(model["stopThreshold"])),
        "bestFrameThresholdAtRecall95": best_recall95(labels, scores),
        "positiveScores": summarize(scores[labels]),
        "negativeScores": summarize(scores[~labels]),
        "groups": groups,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Train/calibration/check STOP separability audit")
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--split-manifest", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    validate_stop_model(model)
    split = json.loads(args.split_manifest.read_text(encoding="utf-8"))
    _, train_contexts = load_contexts(root=args.root, dataset_run_id=args.dataset_run_id, split="train")
    _, dev_contexts = load_contexts(root=args.root, dataset_run_id=args.dataset_run_id, split="dev")
    calibration_ids = set(split["calibrationSweepIds"])
    check_ids = set(split["checkSweepIds"])
    report = {
        "checkpoint": str(args.checkpoint.resolve()),
        "stopThreshold": model["stopThreshold"],
        "heldoutOrStressAccessed": False,
        "train": evaluate(train_contexts, model),
        "calibration": evaluate([item for item in dev_contexts if item.sweep_id in calibration_ids], model),
        "check": evaluate([item for item in dev_contexts if item.sweep_id in check_ids], model),
    }
    save_json(args.output, report)
    print(json.dumps({key: {"auc": value["auc"], "frozen": value["frozenThreshold"], "recall95": value["bestFrameThresholdAtRecall95"]} for key, value in report.items() if key in ("train", "calibration", "check")}, indent=2))


if __name__ == "__main__":
    main()
