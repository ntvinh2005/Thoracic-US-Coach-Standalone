from __future__ import annotations

import argparse
import copy
import hashlib
import json
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from coach_data import (
    ACTION_TO_INDEX,
    DEFAULT_DATASET_RUN_ID,
    DEFAULT_EXPERIMENT_ID,
    classification_metrics,
    load_transitions,
    model_probabilities,
    pixel_features,
    project_root,
    save_json,
)
from train_bc_policy import fit_softmax


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"


def row_identity(samples, reset_history: str) -> str:
    payload = [
        {
            "sweepId": item.sweep_id,
            "transitionId": item.transition_id,
            "previousImageSha256": item.previous_image_sha256,
            "currentImageSha256": item.current_image_sha256,
            "executedAction": item.executed_action,
            "actionWasExecuted": item.action_was_executed,
            "teacherAction": item.teacher_action,
            "terminated": item.terminated,
            "truncated": item.truncated,
            "resetHistory": reset_history if item.transition_kind == "reset" else "captured-consecutive",
        }
        for item in sorted(samples, key=lambda value: (value.sweep_id, value.transition_id))
    ]
    encoded = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def history_matrix(samples, size: int, *, online_reset_history: bool) -> np.ndarray:
    rows = []
    for item in samples:
        current = pixel_features(item.current_image, size)
        if online_reset_history and item.transition_kind == "reset":
            previous = current
        else:
            previous = pixel_features(item.previous_image, size)
        action = np.zeros(3, dtype=np.float32)
        action[ACTION_TO_INDEX[item.executed_action]] = 1.0
        rows.append(np.concatenate((previous, current, action)))
    return np.stack(rows) if rows else np.empty((0, size * size * 2 + 3), dtype=np.float32)


def stop_metrics(labels: np.ndarray, probabilities: np.ndarray) -> dict[str, object]:
    predicted = np.argmax(probabilities, axis=1)
    stop = ACTION_TO_INDEX[0]
    positives = labels == stop
    predicted_positive = predicted == stop
    tp = int(np.sum(positives & predicted_positive))
    fp = int(np.sum(~positives & predicted_positive))
    fn = int(np.sum(positives & ~predicted_positive))
    tn = int(np.sum(~positives & ~predicted_positive))
    return {
        "truePositive": tp,
        "falsePositive": fp,
        "falseNegative": fn,
        "trueNegative": tn,
        "precision": tp / (tp + fp) if tp + fp else None,
        "recall": tp / (tp + fn) if tp + fn else None,
        "falsePositiveRate": fp / (fp + tn) if fp + tn else None,
        "predictedStopFraction": float(np.mean(predicted_positive)),
    }


def evaluate(labels: np.ndarray, probabilities: np.ndarray) -> dict[str, object]:
    return {
        "threeAction": classification_metrics(labels, probabilities),
        "stopVsMove": stop_metrics(labels, probabilities),
    }


def row_audit(samples) -> dict[str, object]:
    grouped = Counter(
        (
            item.split,
            item.transition_kind,
            item.executed_action,
            item.action_was_executed,
            item.teacher_action,
            item.terminated,
            item.truncated,
        )
        for item in samples
    )
    by_split = {}
    for split in sorted({item.split for item in samples}):
        selected = [item for item in samples if item.split == split]
        reset = [item for item in selected if item.transition_kind == "reset"]
        by_split[split] = {
            "rows": len(selected),
            "teacherActionCounts": dict(sorted(Counter(str(item.teacher_action) for item in selected).items())),
            "postTerminalStopDecisionRows": sum(
                item.terminated and item.executed_action == 0 and item.teacher_action == 0 for item in selected
            ),
            "postTruncationDecisionRows": sum(item.truncated for item in selected),
            "validPreStopDecisionRows": sum(
                item.teacher_action == 0
                and not item.terminated
                and not item.truncated
                and item.transition_kind != "reset"
                for item in selected
            ),
            "resetRows": len(reset),
            "resetInside": sum(item.target_acceptable for item in reset),
            "resetOutside": sum(not item.target_acceptable for item in reset),
            "resetExactImageHashMatches": sum(
                item.previous_image_sha256 == item.current_image_sha256 for item in reset
            ),
            "resetStopSentinelWithoutExecutedAction": sum(
                not item.action_was_executed and item.executed_action == 0 for item in reset
            ),
        }
    return {
        "bySplit": by_split,
        "groupedRows": [
            {
                "split": key[0],
                "transitionKind": key[1],
                "executedAction": key[2],
                "actionWasExecuted": key[3],
                "teacherAction": key[4],
                "terminated": key[5],
                "truncated": key[6],
                "count": count,
            }
            for key, count in sorted(grouped.items(), key=lambda value: tuple(str(part) for part in value[0]))
        ],
    }


def make_checkpoint(base, weights, bias, samples, diagnostic_run_id: str, variant: str, losses) -> dict[str, object]:
    checkpoint = copy.deepcopy(base)
    reset_history = "online-current-equals-previous" if variant == "A2" else "captured-separate-renders"
    checkpoint["model"] = f"{base['model']}-{variant.lower()}-diagnostic"
    checkpoint["weights"] = weights.tolist()
    checkpoint["bias"] = bias.tolist()
    checkpoint["datasetFingerprint"] = row_identity(samples, reset_history)
    checkpoint["trainingRows"] = {
        "A1": "valid decision contexts; post-termination and post-truncation rows removed",
        "A2": "A1 rows; reset previous image replaced by current image to match online initialization",
    }[variant]
    checkpoint["diagnosticRunId"] = diagnostic_run_id
    checkpoint["diagnosticVariant"] = variant
    checkpoint["training"] = {
        **checkpoint.get("training", {}),
        "finalLoss": float(losses[-1]),
        "elapsedSeconds": None,
        "rowCount": len(samples),
        "heldoutOrStressUsedForSelection": False,
        "selectionDataGroups": ["training", "development"],
        "phase": "DIAGNOSTIC",
    }
    return checkpoint


def main() -> None:
    parser = argparse.ArgumentParser(description="Bounded history-context and shortcut diagnostic")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--epochs", type=int, default=500)
    parser.add_argument("--learning-rate", type=float, default=0.05)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    base = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    size = int(base.get("featureSize", 16))
    all_samples = load_transitions(
        experiment_id=args.experiment_id,
        dataset_run_id=args.dataset_run_id,
        verify_hashes=True,
        splits={"train", "dev"},
    )
    eligible = [
        item
        for item in all_samples
        if item.teacher_action_defined and item.contact_valid and item.workspace_valid
    ]
    train_a0 = [item for item in eligible if item.split == "train"]
    dev_a0 = [item for item in eligible if item.split == "dev"]
    train_a1 = [item for item in train_a0 if not item.terminated and not item.truncated]
    dev_a1 = [item for item in dev_a0 if not item.terminated and not item.truncated]

    x_train_a0 = history_matrix(train_a0, size, online_reset_history=False)
    x_dev_a0 = history_matrix(dev_a0, size, online_reset_history=False)
    y_train_a0 = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in train_a0], dtype=np.int64)
    y_dev_a0 = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in dev_a0], dtype=np.int64)
    base_probabilities = model_probabilities(base, x_dev_a0)

    shortcuts = {}
    shortcut_inputs = {
        "previousActionOnly": (x_train_a0[:, -3:], x_dev_a0[:, -3:]),
        "currentImageOnly": (x_train_a0[:, size * size : size * size * 2], x_dev_a0[:, size * size : size * size * 2]),
        "historyImagePlusActionRetrainedA0": (x_train_a0, x_dev_a0),
    }
    retrained_a0_weights = retrained_a0_bias = None
    for name, (x_train, x_dev) in shortcut_inputs.items():
        weights, bias, losses = fit_softmax(x_train, y_train_a0, args.epochs, args.learning_rate, args.seed)
        probabilities = model_probabilities(
            {"architecture": "linear-softmax-v1", "weights": weights.tolist(), "bias": bias.tolist()}, x_dev
        )
        shortcuts[name] = {**evaluate(y_dev_a0, probabilities), "finalLoss": float(losses[-1])}
        if name == "historyImagePlusActionRetrainedA0":
            retrained_a0_weights, retrained_a0_bias = weights, bias

    a1_x_train = history_matrix(train_a1, size, online_reset_history=False)
    a1_x_dev = history_matrix(dev_a1, size, online_reset_history=False)
    a2_x_train = history_matrix(train_a1, size, online_reset_history=True)
    a2_x_dev = history_matrix(dev_a1, size, online_reset_history=True)
    y_train_a1 = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in train_a1], dtype=np.int64)
    y_dev_a1 = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in dev_a1], dtype=np.int64)
    a0_on_a1_probabilities = model_probabilities(base, a1_x_dev)
    a0_on_a2_probabilities = model_probabilities(base, a2_x_dev)

    variants = {
        "A0_existing": {
            "checkpoint": base,
            "samples": train_a0,
            "xDev": x_dev_a0,
            "yDev": y_dev_a0,
            "probabilities": base_probabilities,
        }
    }
    for variant, x_train, x_dev, reset_online in (
        ("A1", a1_x_train, a1_x_dev, False),
        ("A2", a2_x_train, a2_x_dev, True),
    ):
        weights, bias, losses = fit_softmax(x_train, y_train_a1, args.epochs, args.learning_rate, args.seed)
        checkpoint = make_checkpoint(base, weights, bias, train_a1, args.diagnostic_run_id, variant, losses)
        checkpoint["metrics"] = {
            "developmentDecisionRows": evaluate(
                y_dev_a1, model_probabilities(checkpoint, x_dev)
            )
        }
        path = output_dir / f"bc_history_linear16_seed13_{variant.lower()}.json"
        save_json(path, checkpoint)
        variants[variant] = {
            "checkpoint": checkpoint,
            "samples": train_a1,
            "xDev": x_dev,
            "yDev": y_dev_a1,
            "probabilities": model_probabilities(checkpoint, x_dev),
            "path": str(path),
            "resetOnline": reset_online,
        }

    counterfactual = {}
    baseline_predictions = np.argmax(base_probabilities, axis=1)
    for action in (-1, 0, 1):
        perturbed = x_dev_a0.copy()
        perturbed[:, -3:] = 0.0
        perturbed[:, -3 + ACTION_TO_INDEX[action]] = 1.0
        probabilities = model_probabilities(base, perturbed)
        predictions = np.argmax(probabilities, axis=1)
        counterfactual[str(action)] = {
            "label": "counterfactual sensitivity; not a valid trajectory",
            "threeActionPredictionFlips": int(np.sum(predictions != baseline_predictions)),
            "stopNonStopFlips": int(
                np.sum((predictions == ACTION_TO_INDEX[0]) != (baseline_predictions == ACTION_TO_INDEX[0]))
            ),
            "predictedStopFraction": float(np.mean(predictions == ACTION_TO_INDEX[0])),
        }

    variant_metrics = {}
    for name, item in variants.items():
        variant_metrics[name] = {
            "trainRows": len(item["samples"]),
            "developmentRows": len(item["yDev"]),
            "development": evaluate(item["yDev"], item["probabilities"]),
            "checkpoint": item.get("path", str(args.checkpoint.resolve())),
        }

    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "sourceExperimentId": args.experiment_id,
        "sourceDatasetRunId": args.dataset_run_id,
        "heldoutOrStressAccessed": False,
        "hypothesis": "Post-terminal STOP rows and reset-history mismatch inflate or distort STOP decisions.",
        "rowAudit": row_audit(all_samples),
        "eligibleRows": row_audit(eligible),
        "shortcutProbes": shortcuts,
        "existingCheckpointDevelopment": evaluate(y_dev_a0, base_probabilities),
        "sameContextComparisons": {
            "A0ExistingOnA1DecisionRowsCapturedReset": evaluate(y_dev_a1, a0_on_a1_probabilities),
            "A1OnA1DecisionRowsCapturedReset": evaluate(y_dev_a1, variants["A1"]["probabilities"]),
            "A0ExistingOnA2DecisionRowsOnlineReset": evaluate(y_dev_a1, a0_on_a2_probabilities),
            "A2OnA2DecisionRowsOnlineReset": evaluate(y_dev_a1, variants["A2"]["probabilities"]),
        },
        "retrainedA0MaxAbsWeightDifference": float(
            np.max(np.abs(retrained_a0_weights - np.asarray(base["weights"], dtype=np.float32)))
        ),
        "retrainedA0MaxAbsBiasDifference": float(
            np.max(np.abs(retrained_a0_bias - np.asarray(base["bias"], dtype=np.float32)))
        ),
        "counterfactualPreviousActionSensitivity": counterfactual,
        "variants": variant_metrics,
        "interpretationLimits": [
            "Decision-row classification is not closed-loop acquisition.",
            "Counterfactual previous-action changes are sensitivity tests, not valid trajectories.",
            "No heldout or stress scenario was read or evaluated.",
        ],
    }
    output = output_dir / "history_context_audit.json"
    save_json(output, report)
    print(json.dumps({
        "phase": "DIAGNOSTIC",
        "output": str(output),
        "variantMetrics": variant_metrics,
        "shortcutProbes": shortcuts,
    }, indent=2))


if __name__ == "__main__":
    main()
