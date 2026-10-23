from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image

from coach_data import (
    ACTION_TO_INDEX,
    DEFAULT_DATASET_RUN_ID,
    DEFAULT_EXPERIMENT_ID,
    load_frames,
    load_transitions,
    model_probabilities,
    pixel_features,
    project_root,
    save_json,
    transition_feature_matrix,
)


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"


def binary_metrics(labels: np.ndarray, predicted: np.ndarray) -> dict[str, object]:
    labels = labels.astype(bool)
    predicted = predicted.astype(bool)
    tp = int(np.sum(labels & predicted))
    fp = int(np.sum(~labels & predicted))
    fn = int(np.sum(labels & ~predicted))
    tn = int(np.sum(~labels & ~predicted))
    return {
        "count": len(labels),
        "truePositive": tp,
        "falsePositive": fp,
        "falseNegative": fn,
        "trueNegative": tn,
        "precision": tp / (tp + fp) if tp + fp else None,
        "recall": tp / (tp + fn) if tp + fn else None,
        "falsePositiveRate": fp / (fp + tn) if fp + tn else None,
        "accuracy": (tp + tn) / len(labels) if len(labels) else None,
        "predictedStopFraction": float(np.mean(predicted)) if len(labels) else None,
    }


def select_threshold(
    labels: np.ndarray,
    scores: np.ndarray,
    *,
    minimum_recall: float | None = None,
    objective: str = "balanced-accuracy",
) -> dict[str, object]:
    candidates = np.unique(np.concatenate(([0.0, 1.0], scores)))
    choices = []
    for threshold in candidates:
        metrics = binary_metrics(labels, scores >= threshold)
        if minimum_recall is not None and float(metrics["recall"] or 0.0) < minimum_recall:
            continue
        specificity = 1.0 - float(metrics["falsePositiveRate"] or 0.0)
        recall = float(metrics["recall"] or 0.0)
        precision = float(metrics["precision"] or 0.0)
        f1 = 2.0 * precision * recall / (precision + recall) if precision + recall else 0.0
        balanced_accuracy = 0.5 * (recall + specificity)
        if objective == "minimum-fpr":
            key = (float(metrics["falsePositiveRate"]), -precision, -float(threshold))
        elif objective == "f1":
            key = (-f1, -balanced_accuracy, -float(threshold))
        else:
            key = (-balanced_accuracy, -f1, -float(threshold))
        choices.append((key, metrics))
    if not choices:
        threshold = 0.0
        metrics = binary_metrics(labels, scores >= threshold)
    else:
        (key, metrics) = min(choices, key=lambda item: item[0])
        threshold = -key[2]
    return {
        "threshold": float(threshold),
        "objective": objective,
        "minimumRecall": minimum_recall,
        "metrics": metrics,
    }


def grayscale_values(path: Path, size: int) -> tuple[np.ndarray, float, float]:
    with Image.open(path) as image:
        values = np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR), dtype=np.float32
        ).reshape(-1) / 255.0
    return values, float(values.mean()), float(values.std())


def frame_matrix(frames, size: int, preprocessing: str) -> np.ndarray:
    rows = []
    for frame in frames:
        values, mean, std = grayscale_values(frame.image, size)
        if preprocessing == "scaled01":
            row = values
        else:
            standardized = (values - mean) / (std + 1e-6)
            row = standardized if preprocessing == "per-image-zscore" else np.concatenate(
                (standardized, np.asarray([mean, std], dtype=np.float32))
            )
        rows.append(row.astype(np.float32))
    width = size * size + (2 if preprocessing == "zscore-plus-mean-std" else 0)
    return np.stack(rows) if rows else np.empty((0, width), dtype=np.float32)


def sigmoid(values: np.ndarray) -> np.ndarray:
    values = np.clip(values, -40.0, 40.0)
    return 1.0 / (1.0 + np.exp(-values))


def fit_binary(x: np.ndarray, y: np.ndarray, epochs: int, learning_rate: float, seed: int):
    rng = np.random.default_rng(seed)
    weights = rng.normal(0.0, 0.01, size=x.shape[1]).astype(np.float32)
    bias = np.float32(0.0)
    counts = np.maximum(1, np.bincount(y.astype(np.int64), minlength=2))
    class_weights = len(y) / (2.0 * counts)
    sample_weights = class_weights[y.astype(np.int64)].astype(np.float32)
    losses = []
    for _ in range(epochs):
        probabilities = sigmoid(x @ weights + bias)
        loss = -np.mean(sample_weights * (
            y * np.log(np.clip(probabilities, 1e-7, 1.0))
            + (1.0 - y) * np.log(np.clip(1.0 - probabilities, 1e-7, 1.0))
        ))
        gradient = sample_weights * (probabilities - y) / len(y)
        weights -= learning_rate * (x.T @ gradient + 1e-4 * weights)
        bias -= learning_rate * np.sum(gradient)
        losses.append(float(loss))
    return weights, float(bias), losses


def split_families(items) -> tuple[list, list, dict[str, str]]:
    families = sorted({item.configuration_family for item in items})
    assignment = {
        family: ("calibration" if int(hashlib.sha256(family.encode()).hexdigest(), 16) % 2 == 0 else "check")
        for family in families
    }
    calibration = [item for item in items if assignment[item.configuration_family] == "calibration"]
    check = [item for item in items if assignment[item.configuration_family] == "check"]
    return calibration, check, assignment


def subset_metrics(labels, scores, threshold, mask) -> dict[str, object]:
    selected = np.asarray(mask, dtype=bool)
    return binary_metrics(labels[selected], scores[selected] >= threshold) if np.any(selected) else {"count": 0}


def main() -> None:
    parser = argparse.ArgumentParser(description="Calibrated STOP-rule and binary-detector diagnostic")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--feature-size", type=int, default=16)
    parser.add_argument("--epochs", type=int, default=500)
    parser.add_argument("--learning-rate", type=float, default=0.05)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))

    transitions = load_transitions(
        experiment_id=args.experiment_id,
        dataset_run_id=args.dataset_run_id,
        verify_hashes=True,
        splits={"train", "dev"},
    )
    transitions = [
        item for item in transitions
        if item.teacher_action_defined and item.contact_valid and item.workspace_valid
        and not item.terminated and not item.truncated
    ]
    dev_transitions = [item for item in transitions if item.split == "dev"]
    transition_calibration, transition_check, transition_assignment = split_families(dev_transitions)

    def transition_scores(items):
        features = transition_feature_matrix(items, size=int(model.get("featureSize", 16)))
        return model_probabilities(model, features)[:, ACTION_TO_INDEX[0]]

    calibration_scores = transition_scores(transition_calibration)
    check_scores = transition_scores(transition_check)
    calibration_labels = np.asarray([item.target_acceptable for item in transition_calibration], dtype=bool)
    check_labels = np.asarray([item.target_acceptable for item in transition_check], dtype=bool)
    b1_calibration_recall95 = select_threshold(
        calibration_labels, calibration_scores, minimum_recall=0.95, objective="minimum-fpr"
    )
    b1_calibration = select_threshold(calibration_labels, calibration_scores, objective="balanced-accuracy")
    b1_threshold = float(b1_calibration["threshold"])
    b0_check_predictions = []
    if transition_check:
        b0_probabilities = model_probabilities(
            model, transition_feature_matrix(transition_check, size=int(model.get("featureSize", 16)))
        )
        b0_check_predictions = np.argmax(b0_probabilities, axis=1) == ACTION_TO_INDEX[0]
    b0_check = binary_metrics(check_labels, np.asarray(b0_check_predictions, dtype=bool))
    b1_check = binary_metrics(check_labels, check_scores >= b1_threshold)

    frames = load_frames(
        experiment_id=args.experiment_id,
        dataset_run_id=args.dataset_run_id,
        require_complete=True,
        splits={"train", "dev"},
    )
    frames = [item for item in frames if item.contact_valid and item.workspace_valid]
    train_frames = [item for item in frames if item.split == "train"]
    dev_frames = [item for item in frames if item.split == "dev"]
    frame_calibration, frame_check, frame_assignment = split_families(dev_frames)
    y_train = np.asarray([item.target_acceptable for item in train_frames], dtype=np.float32)
    y_frame_calibration = np.asarray([item.target_acceptable for item in frame_calibration], dtype=bool)
    y_frame_check = np.asarray([item.target_acceptable for item in frame_check], dtype=bool)

    detector_variants = {}
    saved_detector = None
    for preprocessing in ("per-image-zscore", "scaled01", "zscore-plus-mean-std"):
        x_train = frame_matrix(train_frames, args.feature_size, preprocessing)
        x_calibration = frame_matrix(frame_calibration, args.feature_size, preprocessing)
        x_check = frame_matrix(frame_check, args.feature_size, preprocessing)
        weights, bias, losses = fit_binary(x_train, y_train, args.epochs, args.learning_rate, args.seed)
        calibration_detector_scores = sigmoid(x_calibration @ weights + bias)
        check_detector_scores = sigmoid(x_check @ weights + bias)
        calibration_recall95 = select_threshold(
            y_frame_calibration, calibration_detector_scores, minimum_recall=0.95, objective="minimum-fpr"
        )
        calibration = select_threshold(
            y_frame_calibration, calibration_detector_scores, objective="balanced-accuracy"
        )
        threshold = float(calibration["threshold"])
        check = binary_metrics(y_frame_check, check_detector_scores >= threshold)
        detector_variants[preprocessing] = {
            "featureWidth": int(x_train.shape[1]),
            "trainRows": len(train_frames),
            "calibrationRows": len(frame_calibration),
            "checkRows": len(frame_check),
            "finalLoss": float(losses[-1]),
            "calibration": calibration,
            "calibrationRecall95": calibration_recall95,
            "check": check,
            "checkAtRecall95Threshold": binary_metrics(
                y_frame_check, check_detector_scores >= float(calibration_recall95["threshold"])
            ),
        }
        if preprocessing == "per-image-zscore":
            saved_detector = {
                "schemaVersion": 1,
                "phase": "DIAGNOSTIC",
                "diagnosticRunId": args.diagnostic_run_id,
                "sourceExperimentId": args.experiment_id,
                "sourceDatasetRunId": args.dataset_run_id,
                "model": "binary-target-detector-linear-v1",
                "input": "current clean ultrasound pixels only",
                "featureSize": args.feature_size,
                "preprocessing": "grayscale-bilinear-per-image-zscore-v1",
                "weights": weights.tolist(),
                "bias": bias,
                "stopThreshold": threshold,
                "thresholdSelection": "development calibration families; minimum target recall 0.95 then minimum FPR",
                "trainingRows": len(train_frames),
                "heldoutOrStressUsed": False,
            }

    detector_path = output_dir / "binary_target_detector_seed13.json"
    save_json(detector_path, saved_detector)

    # First-decision contexts are reset rows. Target-entry contexts are
    # non-STOP moves whose current pose is acceptable and previous pose was not.
    frame_acceptability = {(item.sweep_id, item.step_index): item.target_acceptable for item in frames}
    check_index = {id(item): index for index, item in enumerate(transition_check)}
    reset_outside = [item.transition_kind == "reset" and not item.target_acceptable for item in transition_check]
    first_arrival = [
        item.action_was_executed and item.executed_action != 0 and item.target_acceptable
        and not frame_acceptability.get((item.sweep_id, item.previous_index), False)
        for item in transition_check
    ]
    approach_backward = [value and item.executed_action == -1 for value, item in zip(first_arrival, transition_check)]
    approach_forward = [value and item.executed_action == 1 for value, item in zip(first_arrival, transition_check)]

    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "sourceExperimentId": args.experiment_id,
        "sourceDatasetRunId": args.dataset_run_id,
        "heldoutOrStressAccessed": False,
        "B0ThreeActionArgmax": {"check": b0_check},
        "B1CalibratedSameLogits": {
            "decisionRule": "STOP iff P(STOP)>=threshold; otherwise choose highest-probability non-STOP action",
            "weightedSoftmaxProbabilitiesTreatedAsScoresNotCalibration": True,
            "calibration": b1_calibration,
            "calibrationRecall95": b1_calibration_recall95,
            "check": b1_check,
            "checkAtRecall95Threshold": binary_metrics(
                check_labels, check_scores >= float(b1_calibration_recall95["threshold"])
            ),
            "firstDecisionOutsideCheck": subset_metrics(check_labels, check_scores, b1_threshold, reset_outside),
            "firstTargetArrivalCheck": subset_metrics(check_labels, check_scores, b1_threshold, first_arrival),
            "firstTargetArrivalFromBackward": subset_metrics(check_labels, check_scores, b1_threshold, approach_backward),
            "firstTargetArrivalFromForward": subset_metrics(check_labels, check_scores, b1_threshold, approach_forward),
        },
        "B2BinaryTargetDetector": {
            "trainingData": "training fixed-pose frames only",
            "calibrationData": "development calibration families only",
            "checkData": "disjoint development check families",
            "checkpoint": str(detector_path),
            "preprocessingVariants": detector_variants,
        },
        "familySplit": {
            "method": "sha256(configurationFamily) parity; fixed before threshold selection",
            "transitionCalibrationFamilies": sum(value == "calibration" for value in transition_assignment.values()),
            "transitionCheckFamilies": sum(value == "check" for value in transition_assignment.values()),
            "frameCalibrationFamilies": sum(value == "calibration" for value in frame_assignment.values()),
            "frameCheckFamilies": sum(value == "check" for value in frame_assignment.values()),
        },
        "limitations": [
            "Offline STOP measurements do not establish closed-loop acquisition.",
            "First-target-arrival rows are captured boundary-crossing decision contexts, not reconstructed full episodes.",
            "No heldout or stress data was loaded.",
        ],
    }
    output = output_dir / "stop_rule_audit.json"
    save_json(output, report)
    print(json.dumps({
        "phase": "DIAGNOSTIC",
        "output": str(output),
        "B0Check": b0_check,
        "B1Threshold": b1_threshold,
        "B1Check": b1_check,
        "detectorVariants": detector_variants,
    }, indent=2))


if __name__ == "__main__":
    main()
