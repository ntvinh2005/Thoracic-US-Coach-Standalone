from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import (
    ACTION_TO_INDEX,
    DEFAULT_DATASET_RUN_ID,
    DEFAULT_EXPERIMENT_ID,
    classification_metrics,
    dataset_fingerprint,
    feature_matrix,
    load_frames,
    load_transitions,
    project_root,
    save_json,
    stable_softmax,
    model_probabilities,
    transition_dataset_fingerprint,
    decision_transition_feature_matrix,
    valid_policy_decisions,
)
from contract_v3 import CONTRACT_VERSION, OBSERVATION_SCHEMA, TARGET_SEMANTICS
from protocol_v3 import PREPROCESSING_ID


def fit_softmax(x: np.ndarray, y: np.ndarray, epochs: int, lr: float, seed: int) -> tuple[np.ndarray, np.ndarray, list[float]]:
    rng = np.random.default_rng(seed)
    weights = rng.normal(0.0, 0.01, size=(x.shape[1], 3)).astype(np.float32)
    bias = np.zeros(3, dtype=np.float32)
    one_hot = np.eye(3, dtype=np.float32)[y]
    counts = np.maximum(1, np.bincount(y, minlength=3))
    class_weights = len(y) / (3.0 * counts)
    sample_weights = class_weights[y].reshape(-1, 1).astype(np.float32)
    losses: list[float] = []
    for _ in range(epochs):
        probabilities = stable_softmax(x @ weights + bias)
        loss = -np.mean(sample_weights[:, 0] * np.log(np.clip(probabilities[np.arange(len(y)), y], 1e-7, 1.0)))
        gradient = (probabilities - one_hot) * sample_weights / len(y)
        weights -= lr * (x.T @ gradient + 1e-4 * weights)
        bias -= lr * gradient.sum(axis=0)
        losses.append(float(loss))
        if not np.isfinite(loss) or not np.all(np.isfinite(weights)) or not np.all(np.isfinite(bias)):
            raise FloatingPointError("BC optimization produced NaN or Inf")
    return weights, bias, losses


def fit_mlp(
    x: np.ndarray, y: np.ndarray, epochs: int, lr: float, seed: int, hidden_size: int
) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, list[float]]:
    rng = np.random.default_rng(seed)
    input_weights = rng.normal(0.0, np.sqrt(2.0 / x.shape[1]), size=(x.shape[1], hidden_size)).astype(np.float32)
    input_bias = np.zeros(hidden_size, dtype=np.float32)
    output_weights = rng.normal(0.0, np.sqrt(2.0 / hidden_size), size=(hidden_size, 3)).astype(np.float32)
    output_bias = np.zeros(3, dtype=np.float32)
    parameters = [input_weights, input_bias, output_weights, output_bias]
    first = [np.zeros_like(value) for value in parameters]
    second = [np.zeros_like(value) for value in parameters]
    one_hot = np.eye(3, dtype=np.float32)[y]
    counts = np.maximum(1, np.bincount(y, minlength=3))
    sample_weights = (len(y) / (3.0 * counts))[y].reshape(-1, 1).astype(np.float32)
    losses: list[float] = []
    for step in range(1, epochs + 1):
        hidden_pre = x @ input_weights + input_bias
        hidden = np.maximum(hidden_pre, 0.0)
        probabilities = stable_softmax(hidden @ output_weights + output_bias)
        loss = -np.mean(sample_weights[:, 0] * np.log(np.clip(probabilities[np.arange(len(y)), y], 1e-7, 1.0)))
        gradient_logits = (probabilities - one_hot) * sample_weights / len(y)
        gradient_output_weights = hidden.T @ gradient_logits + 1e-4 * output_weights
        gradient_output_bias = gradient_logits.sum(axis=0)
        gradient_hidden = gradient_logits @ output_weights.T
        gradient_hidden[hidden_pre <= 0.0] = 0.0
        gradient_input_weights = x.T @ gradient_hidden + 1e-4 * input_weights
        gradient_input_bias = gradient_hidden.sum(axis=0)
        gradients = [gradient_input_weights, gradient_input_bias, gradient_output_weights, gradient_output_bias]
        for index, (parameter, gradient) in enumerate(zip(parameters, gradients)):
            first[index] = 0.9 * first[index] + 0.1 * gradient
            second[index] = 0.999 * second[index] + 0.001 * (gradient * gradient)
            corrected_first = first[index] / (1.0 - 0.9**step)
            corrected_second = second[index] / (1.0 - 0.999**step)
            parameter -= lr * corrected_first / (np.sqrt(corrected_second) + 1e-8)
        losses.append(float(loss))
        if not np.isfinite(loss) or any(not np.all(np.isfinite(value)) for value in parameters):
            raise FloatingPointError("MLP BC optimization produced NaN or Inf")
    return input_weights, input_bias, output_weights, output_bias, losses


def grouped_metrics(frames, probabilities: np.ndarray, attribute: str) -> dict[str, object]:
    result: dict[str, object] = {}
    for value in sorted({getattr(frame, attribute) for frame in frames}):
        indices = [index for index, frame in enumerate(frames) if getattr(frame, attribute) == value]
        labels = np.asarray([ACTION_TO_INDEX[frames[index].teacher_action] for index in indices], dtype=np.int64)
        result[str(value)] = classification_metrics(labels, probabilities[indices])
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description="Balanced linear BC baseline on clean ultrasound pixels")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--observation", choices=("current", "history"), default="current")
    parser.add_argument("--feature-size", type=int, choices=(16, 24, 32), default=16)
    parser.add_argument("--architecture", choices=("linear", "mlp"), default="linear")
    parser.add_argument("--hidden-size", type=int, choices=(32, 64, 96), default=64)
    parser.add_argument("--epochs", type=int, default=300)
    parser.add_argument("--lr", type=float)
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.output is None:
        safe_run = "".join(character if character.isalnum() or character in "-_" else "_" for character in args.dataset_run_id)
        args.output = project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/bc_pixel_{args.observation}_{safe_run}_seed{args.seed}.json"

    # Filter at manifest level before image reads/hashes.  Development training must
    # never enumerate heldout/stress observations, even if their files share a root.
    all_frames = load_frames(
        experiment_id=args.experiment_id,
        dataset_run_id=args.dataset_run_id,
        splits={"train", "dev"},
    )
    all_transitions = (
        load_transitions(
            experiment_id=args.experiment_id,
            dataset_run_id=args.dataset_run_id,
            splits={"train", "dev"},
        )
        if args.observation == "history"
        else []
    )
    rows = (
        valid_policy_decisions(all_transitions)
        if args.observation == "history"
        else [
            item
            for item in all_frames
            if item.teacher_action_defined and item.contact_valid and item.workspace_valid
        ]
    )
    train = [item for item in rows if item.split == "train"]
    dev = [item for item in rows if item.split == "dev"]
    if not train or not dev:
        raise SystemExit("The selected experiment needs complete train and dev trajectory families")
    started = time.perf_counter()
    matrix = (
        (lambda selected: decision_transition_feature_matrix(selected, size=args.feature_size))
        if args.observation == "history"
        else (lambda selected: feature_matrix(selected, size=args.feature_size, observation="current"))
    )
    x_train = matrix(train)
    y_train = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in train], dtype=np.int64)
    learning_rate = args.lr if args.lr is not None else (0.05 if args.architecture == "linear" else 0.001)
    if args.architecture == "linear":
        weights, bias, losses = fit_softmax(x_train, y_train, args.epochs, learning_rate, args.seed)
        architecture_fields: dict[str, object] = {
            "architecture": "linear-softmax-v1",
            "weights": weights.tolist(),
            "bias": bias.tolist(),
        }
    else:
        input_weights, input_bias, output_weights, output_bias, losses = fit_mlp(
            x_train, y_train, args.epochs, learning_rate, args.seed, args.hidden_size
        )
        architecture_fields = {
            "architecture": "mlp-relu-v1",
            "hiddenSize": args.hidden_size,
            "inputWeights": input_weights.tolist(),
            "inputBias": input_bias.tolist(),
            "outputWeights": output_weights.tolist(),
            "outputBias": output_bias.tolist(),
        }
    elapsed = time.perf_counter() - started

    report: dict[str, object] = {
        "schemaVersion": 3,
        "model": f"pixel{args.feature_size}-{args.architecture}-bc-{args.observation}-contract-v3",
        "input": "clean ultrasound pixels only" if args.observation == "current" else "previous/current clean ultrasound pixels plus previous action",
        "observationMode": args.observation,
        "featureSize": args.feature_size,
        "actionOrder": [-1, 0, 1],
        "experimentId": args.experiment_id,
        "datasetRunId": args.dataset_run_id,
        "environmentContractVersion": CONTRACT_VERSION,
        "observationSchema": OBSERVATION_SCHEMA,
        "targetSemantics": TARGET_SEMANTICS,
        "preprocessing": PREPROCESSING_ID,
        "datasetFingerprint": transition_dataset_fingerprint(rows) if args.observation == "history" else dataset_fingerprint(all_frames),
        "trainingRows": (
            "valid pre-decision transitions only; reset uses previous=current; terminal/truncated rows excluded"
            if args.observation == "history"
            else "fixed-pose current observations"
        ),
        **architecture_fields,
        "training": {
            "epochs": args.epochs,
            "learningRate": learning_rate,
            "seed": args.seed,
            "finalLoss": losses[-1],
            "classBalancing": "inverse-frequency weights on train only",
            "elapsedSeconds": elapsed,
            "selectionDataGroups": ["training", "development"],
            "heldoutOrStressUsedForSelection": False,
        },
        "metrics": {},
    }
    for split in ("train", "dev"):
        selected = [item for item in rows if item.split == split]
        x = matrix(selected)
        y = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in selected], dtype=np.int64)
        probabilities = model_probabilities(report, x)
        metrics = classification_metrics(y, probabilities)
        metrics["byTrajectoryFamily"] = grouped_metrics(selected, probabilities, "configuration_family")
        if args.observation == "history":
            metrics["byTransitionKind"] = grouped_metrics(selected, probabilities, "transition_kind")
        else:
            metrics["byStartSide"] = grouped_metrics(selected, probabilities, "start_side")
        report["metrics"][split] = metrics
    save_json(args.output, report)
    print(json.dumps({"checkpoint": str(args.output), "metrics": report["metrics"], "elapsedSeconds": elapsed}, indent=2))


if __name__ == "__main__":
    main()
