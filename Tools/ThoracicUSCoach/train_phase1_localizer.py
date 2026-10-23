from __future__ import annotations

import argparse
import json
import time
from collections import defaultdict
from pathlib import Path

import numpy as np

from active2d_contract import CONTRACT, EXPERIMENT_ID, LEVEL_ORDER, project_root
from active2d_data import dataset_fingerprint, feature_matrix, load_active2d_frames, model_probabilities, softmax
from active2d_stop import stop_probabilities
from train_phase2_stop import classification_metrics, fit_binary_mlp


LEVEL_TO_INDEX = {level: index for index, level in enumerate(LEVEL_ORDER)}


def fit_mlp(
    x: np.ndarray,
    y: np.ndarray,
    *,
    hidden_size: int,
    epochs: int,
    learning_rate: float,
    seed: int,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, list[float]]:
    classes = len(LEVEL_ORDER)
    rng = np.random.default_rng(seed)
    w1 = rng.normal(0.0, np.sqrt(2.0 / x.shape[1]), size=(x.shape[1], hidden_size)).astype(np.float32)
    b1 = np.zeros(hidden_size, dtype=np.float32)
    w2 = rng.normal(0.0, np.sqrt(2.0 / hidden_size), size=(hidden_size, classes)).astype(np.float32)
    b2 = np.zeros(classes, dtype=np.float32)
    parameters = (w1, b1, w2, b2)
    first = [np.zeros_like(value) for value in parameters]
    second = [np.zeros_like(value) for value in parameters]
    one_hot = np.eye(classes, dtype=np.float32)[y]
    counts = np.maximum(1, np.bincount(y, minlength=classes))
    sample_weights = (len(y) / (classes * counts))[y].reshape(-1, 1).astype(np.float32)
    losses: list[float] = []
    for step in range(1, epochs + 1):
        hidden_pre = x @ w1 + b1
        hidden = np.maximum(hidden_pre, 0.0)
        probabilities = softmax(hidden @ w2 + b2)
        loss = -np.mean(sample_weights[:, 0] * np.log(np.clip(probabilities[np.arange(len(y)), y], 1e-7, 1.0)))
        gradient_logits = (probabilities - one_hot) * sample_weights / len(y)
        gradients = [
            x.T @ (gradient_logits @ w2.T * (hidden_pre > 0.0)) + 1e-4 * w1,
            (gradient_logits @ w2.T * (hidden_pre > 0.0)).sum(axis=0),
            hidden.T @ gradient_logits + 1e-4 * w2,
            gradient_logits.sum(axis=0),
        ]
        for index, (parameter, gradient) in enumerate(zip(parameters, gradients)):
            first[index] = 0.9 * first[index] + 0.1 * gradient
            second[index] = 0.999 * second[index] + 0.001 * gradient * gradient
            parameter -= learning_rate * (first[index] / (1.0 - 0.9**step)) / (
                np.sqrt(second[index] / (1.0 - 0.999**step)) + 1e-8
            )
        if not np.isfinite(loss) or any(not np.all(np.isfinite(value)) for value in parameters):
            raise FloatingPointError("Phase 1 optimizer produced NaN or Inf")
        losses.append(float(loss))
    return w1, b1, w2, b2, losses


def transition_matrix(action: str, shift: float) -> np.ndarray:
    matrix = np.eye(len(LEVEL_ORDER), dtype=np.float64)
    if action not in {"CAUDAL", "CRANIAL"}:
        return matrix
    matrix[:3] *= 1.0 - shift
    if action == "CAUDAL":
        matrix[0, 1] += shift
        matrix[1, 2] += shift
        matrix[2, 2] += shift
    else:
        matrix[2, 1] += shift
        matrix[1, 0] += shift
        matrix[0, 0] += shift
    matrix[3, 3] = 1.0
    return matrix


def temporal_posteriors(frames, emissions: np.ndarray, shift: float) -> np.ndarray:
    result = np.zeros_like(emissions, dtype=np.float64)
    belief = np.full(len(LEVEL_ORDER), 1.0 / len(LEVEL_ORDER), dtype=np.float64)
    episode = None
    for index, frame in enumerate(frames):
        if frame.episode_id != episode or frame.episode_reset:
            belief[:] = 1.0 / len(LEVEL_ORDER)
            episode = frame.episode_id
        else:
            belief = belief @ transition_matrix(frame.issued_action, shift)
        belief *= np.maximum(emissions[index], 1e-6)
        belief /= belief.sum()
        result[index] = belief
    return result.astype(np.float32)


def confusion(labels: np.ndarray, probabilities: np.ndarray) -> list[list[int]]:
    predicted = np.argmax(probabilities, axis=1)
    matrix = np.zeros((len(LEVEL_ORDER), len(LEVEL_ORDER)), dtype=np.int64)
    for actual, chosen in zip(labels, predicted):
        matrix[int(actual), int(chosen)] += 1
    return matrix.tolist()


def metrics(labels: np.ndarray, probabilities: np.ndarray) -> dict[str, object]:
    predicted = np.argmax(probabilities, axis=1)
    matrix = np.asarray(confusion(labels, probabilities), dtype=np.int64)
    per_level: dict[str, object] = {}
    for index, level in enumerate(LEVEL_ORDER):
        true_positive = int(matrix[index, index])
        support = int(matrix[index].sum())
        predicted_count = int(matrix[:, index].sum())
        precision = true_positive / max(1, predicted_count)
        recall = true_positive / max(1, support)
        per_level[level] = {
            "support": support,
            "precision": precision,
            "recall": recall,
            "f1": 2.0 * precision * recall / max(1e-12, precision + recall),
        }
    return {
        "count": len(labels),
        "accuracy": float(np.mean(predicted == labels)),
        "confusion": matrix.tolist(),
        "perLevel": per_level,
    }


def calibrate_handoff(frames, handoff_scores: np.ndarray) -> dict[str, object]:
    grouped: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        grouped[frame.episode_id].append(index)
    candidates: list[dict[str, object]] = []
    for threshold in np.arange(0.50, 0.951, 0.025):
        for confirmations in (1, 2, 3, 4):
            successes = 0
            handoffs = 0
            premature = 0
            steps: list[int] = []
            for indices in grouped.values():
                streak = 0
                chosen = None
                for local_step, index in enumerate(indices):
                    streak = streak + 1 if handoff_scores[index] >= threshold else 0
                    if streak >= confirmations:
                        chosen = index
                        steps.append(local_step + 1)
                        break
                if chosen is None:
                    continue
                handoffs += 1
                if frames[chosen].t7_acceptable:
                    successes += 1
                else:
                    premature += 1
            episodes = len(grouped)
            precision = successes / max(1, handoffs)
            acquisition = successes / max(1, episodes)
            candidates.append(
                {
                    "threshold": float(threshold),
                    "confirmations": confirmations,
                    "episodes": episodes,
                    "handoffs": handoffs,
                    "successes": successes,
                    "prematureHandoffs": premature,
                    "handoffPrecision": precision,
                    "correctAcquisitionRate": acquisition,
                    "meanStepsToHandoff": float(np.mean(steps)) if steps else None,
                    "selectionScore": acquisition + 0.35 * precision - 0.15 * (premature / max(1, episodes)),
                }
            )
    eligible = [item for item in candidates if item["handoffPrecision"] >= 0.80]
    selected = max(
        eligible or candidates,
        key=lambda item: (
            item["selectionScore"],
            item["handoffPrecision"],
            item["threshold"],
            -abs(item["confirmations"] - 2),
        ),
    )
    return {"selected": selected, "candidates": candidates}


def contrastive_diagnostic(hidden: np.ndarray, labels: np.ndarray, limit: int = 20_000) -> dict[str, float | int]:
    same: list[float] = []
    different: list[float] = []
    hard_negative: list[float] = []
    pairs = 0
    for left in range(len(hidden)):
        for right in range(left + 1, len(hidden)):
            distance = float(np.linalg.norm(hidden[left] - hidden[right]) / np.sqrt(hidden.shape[1]))
            if labels[left] == labels[right]:
                same.append(distance)
            else:
                different.append(distance)
                if {int(labels[left]), int(labels[right])} in ({0, 1}, {1, 2}):
                    hard_negative.append(distance)
            pairs += 1
            if pairs >= limit:
                break
        if pairs >= limit:
            break
    return {
        "sampledPairs": pairs,
        "sameLevelMeanDistance": float(np.mean(same)) if same else 0.0,
        "differentLevelMeanDistance": float(np.mean(different)) if different else 0.0,
        "adjacentHardNegativeMeanDistance": float(np.mean(hard_negative)) if hard_negative else 0.0,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="CPU Phase-1 image/history vertebral localizer")
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--hidden-size", type=int, default=64)
    parser.add_argument("--epochs", type=int, default=600)
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--transition-shift", type=float, default=0.15)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = args.output or project_root() / f"Artifacts/ThoracicUSCoach/active2d/checkpoints/phase1_{args.dataset_run_id}_seed{args.seed}.json"

    frames = load_active2d_frames(args.dataset_run_id)
    train = [frame for frame in frames if frame.split == "train" and frame.contact_valid and frame.workspace_valid]
    dev = [frame for frame in frames if frame.split == "dev" and frame.contact_valid and frame.workspace_valid]
    if not train or not dev:
        raise SystemExit("Phase 1 training requires non-empty family-separated train and dev episodes")
    if {frame.family for frame in train} & {frame.family for frame in dev}:
        raise SystemExit("Configuration-family leakage between train and dev")
    missing = set(LEVEL_ORDER) - {frame.level for frame in train}
    if missing:
        raise SystemExit(f"Training split is missing absolute-level classes: {sorted(missing)}")
    missing_dev = set(LEVEL_ORDER) - {frame.level for frame in dev}
    if missing_dev:
        raise SystemExit(
            "Absolute-level identifiability gate failed: "
            f"dev split is missing classes {sorted(missing_dev)}"
        )

    started = time.perf_counter()
    x_train = feature_matrix(train)
    x_dev = feature_matrix(dev)
    y_train = np.asarray([LEVEL_TO_INDEX[frame.level] for frame in train], dtype=np.int64)
    y_dev = np.asarray([LEVEL_TO_INDEX[frame.level] for frame in dev], dtype=np.int64)
    w1, b1, w2, b2, losses = fit_mlp(
        x_train,
        y_train,
        hidden_size=args.hidden_size,
        epochs=args.epochs,
        learning_rate=args.lr,
        seed=args.seed,
    )
    architecture = {
        "type": "fixed-convolutional-feature-bank+mlp+bayes-v1",
        "featureWidth": int(x_train.shape[1]),
        "hiddenSize": args.hidden_size,
        "inputWeights": w1.tolist(),
        "inputBias": b1.tolist(),
        "outputWeights": w2.tolist(),
        "outputBias": b2.tolist(),
    }
    provisional = {"architecture": architecture}
    train_emissions = model_probabilities(provisional, x_train)
    dev_emissions = model_probabilities(provisional, x_dev)
    train_temporal = temporal_posteriors(train, train_emissions, args.transition_shift)
    dev_temporal = temporal_posteriors(dev, dev_emissions, args.transition_shift)
    handoff_y_train = np.asarray([frame.t7_acceptable for frame in train], dtype=np.float32)
    handoff_y_dev = np.asarray([frame.t7_acceptable for frame in dev], dtype=np.float32)
    hw1, hb1, hw2, hb2, handoff_losses = fit_binary_mlp(
        x_train,
        handoff_y_train,
        hidden_size=max(16, args.hidden_size // 2),
        epochs=max(300, args.epochs // 2),
        learning_rate=args.lr,
        seed=args.seed + 1009,
    )
    handoff_head = {
        "type": "fixed-convolutional-binary-handoff-v1",
        "featureWidth": int(x_train.shape[1]),
        "hiddenSize": int(len(hb1)),
        "inputWeights": hw1.tolist(),
        "inputBias": hb1.tolist(),
        "outputWeights": hw2.tolist(),
        "outputBias": hb2,
    }
    handoff_train_scores = stop_probabilities({"architecture": handoff_head}, x_train)
    handoff_dev_scores = stop_probabilities({"architecture": handoff_head}, x_dev)
    calibration = calibrate_handoff(dev, handoff_dev_scores)
    hidden_dev = np.maximum(x_dev @ w1 + b1, 0.0)
    report = {
        "schemaVersion": 1,
        "contract": CONTRACT,
        "experimentId": EXPERIMENT_ID,
        "datasetRunId": args.dataset_run_id,
        "datasetFingerprint": dataset_fingerprint([*train, *dev]),
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": [
            "pose", "markerIdentity", "targetCoordinates", "score", "pathIndex", "candidateManifest", "oracleDirection"
        ],
        "levelOrder": list(LEVEL_ORDER),
        "architecture": architecture,
        "handoffHead": handoff_head,
        "temporalFilter": {
            "type": "action-conditioned-bayesian-level-belief-v1",
            "transitionShiftPerLongitudinalStep": args.transition_shift,
            "handoffThreshold": calibration["selected"]["threshold"],
            "requiredConfirmations": calibration["selected"]["confirmations"],
        },
        "training": {
            "seed": args.seed,
            "epochs": args.epochs,
            "learningRate": args.lr,
            "finalLoss": losses[-1],
            "handoffFinalLoss": handoff_losses[-1],
            "elapsedSeconds": time.perf_counter() - started,
            "heldoutTestUsedForSelection": False,
            "splitUnit": "independent episode/configuration family",
        },
        "metrics": {
            "trainSingleFrame": metrics(y_train, train_emissions),
            "trainTemporal": metrics(y_train, train_temporal),
            "devSingleFrame": metrics(y_dev, dev_emissions),
            "devTemporal": metrics(y_dev, dev_temporal),
            "devHandoffCalibration": calibration,
            "trainHandoffClassification": classification_metrics(
                handoff_y_train, handoff_train_scores, calibration["selected"]["threshold"]
            ),
            "devHandoffClassification": classification_metrics(
                handoff_y_dev, handoff_dev_scores, calibration["selected"]["threshold"]
            ),
            "devRepresentationDiagnostic": contrastive_diagnostic(hidden_dev, y_dev),
        },
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(json.dumps(report, indent=2), encoding="utf-8")
    temporary.replace(output)
    print(
        json.dumps(
            {
                "output": str(output),
                "trainTemporal": report["metrics"]["trainTemporal"],
                "devSingleFrame": report["metrics"]["devSingleFrame"],
                "devTemporal": report["metrics"]["devTemporal"],
                "selectedHandoff": calibration["selected"],
                "representation": report["metrics"]["devRepresentationDiagnostic"],
                "training": report["training"],
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
