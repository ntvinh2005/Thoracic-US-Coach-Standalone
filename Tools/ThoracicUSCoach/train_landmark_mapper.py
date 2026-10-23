from __future__ import annotations

import argparse
import hashlib
import json
import time
from pathlib import Path

import numpy as np

from active2d_contract import CONTRACT, EXPERIMENT_ID, LEVEL_ORDER, project_root
from active2d_data import dataset_fingerprint, feature_matrix, load_active2d_frames, softmax
from landmark_mapping import (EVENT_ORDER, LEVEL_TO_INDEX, apply_event_threshold, confusion_metrics,
                              context_features, episode_maps, event_labels, sequence_diagnostics,
                              transition_metrics)


FORBIDDEN = ["pose", "markerIdentity", "targetCoordinates", "score", "pathIndex", "candidateManifest", "oracleDirection"]


def emit(phase: str, current: int, total: int, message: str) -> None:
    print("::localjobs_progress " + json.dumps({"phase": phase, "current": current, "total": total, "message": message}), flush=True)


def scale_fit(x: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    mean = x.mean(axis=0)
    scale = x.std(axis=0)
    scale[scale < 1e-6] = 1.0
    return mean.astype(np.float32), scale.astype(np.float32)


def train_mlp(x: np.ndarray, y: np.ndarray, classes: int, hidden: int, epochs: int, lr: float,
              seed: int, label: str, progress_start: int, progress_total: int) -> dict:
    rng = np.random.default_rng(seed)
    w1 = rng.normal(0, np.sqrt(2 / x.shape[1]), (x.shape[1], hidden)).astype(np.float32)
    b1 = np.zeros(hidden, np.float32)
    w2 = rng.normal(0, np.sqrt(2 / hidden), (hidden, classes)).astype(np.float32)
    b2 = np.zeros(classes, np.float32)
    params = [w1, b1, w2, b2]
    first = [np.zeros_like(value) for value in params]
    second = [np.zeros_like(value) for value in params]
    one_hot = np.eye(classes, dtype=np.float32)[y]
    counts = np.maximum(1, np.bincount(y, minlength=classes))
    weights = (len(y) / (classes * counts))[y, None].astype(np.float32)
    loss = float("nan")
    for step in range(1, epochs + 1):
        pre = x @ w1 + b1
        hidden_values = np.maximum(pre, 0)
        probs = softmax(hidden_values @ w2 + b2)
        loss = float(-np.mean(weights[:, 0] * np.log(np.clip(probs[np.arange(len(y)), y], 1e-7, 1))))
        grad_logits = (probs - one_hot) * weights / len(y)
        grad_hidden = grad_logits @ w2.T * (pre > 0)
        grads = [x.T @ grad_hidden + 1e-4 * w1, grad_hidden.sum(0),
                 hidden_values.T @ grad_logits + 1e-4 * w2, grad_logits.sum(0)]
        for index, (parameter, gradient) in enumerate(zip(params, grads)):
            first[index] = .9 * first[index] + .1 * gradient
            second[index] = .999 * second[index] + .001 * gradient * gradient
            parameter -= lr * (first[index] / (1 - .9 ** step)) / (np.sqrt(second[index] / (1 - .999 ** step)) + 1e-8)
        if step % max(1, epochs // 5) == 0 or step == epochs:
            emit(label, progress_start, progress_total, f"{label}: epoch {step}/{epochs}, loss={loss:.5f}")
        if not np.isfinite(loss):
            raise FloatingPointError(f"Non-finite loss in {label}")
    return {"inputWeights": w1, "inputBias": b1, "outputWeights": w2, "outputBias": b2, "finalLoss": loss}


def predict(model: dict, x: np.ndarray) -> np.ndarray:
    hidden = np.maximum(x @ model["inputWeights"] + model["inputBias"], 0)
    return softmax(hidden @ model["outputWeights"] + model["outputBias"])


def serial_model(model: dict, model_type: str) -> dict:
    return {"type": model_type, "inputWeights": model["inputWeights"].tolist(),
            "inputBias": model["inputBias"].tolist(), "outputWeights": model["outputWeights"].tolist(),
            "outputBias": model["outputBias"].tolist(), "finalLoss": model["finalLoss"]}


def atomic_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    temporary.replace(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="U06 explicit image/action landmark mapper")
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    config_path = args.config.resolve()
    config_bytes = config_path.read_bytes()
    config = json.loads(config_bytes)
    root = project_root()
    if config.get("taskId") != "U06" or config.get("schemaVersion") != 1:
        raise SystemExit("Expected immutable U06 schemaVersion 1 config")
    started = time.perf_counter()
    emit("load", 0, 5, "Loading hash-verified image-only episodes")
    frames = [frame for frame in load_active2d_frames(config["datasetRunId"], root)
              if frame.contact_valid and frame.workspace_valid]
    splits = {name: [frame for frame in frames if frame.split == name] for name in ("train", "dev", "test")}
    if any(not values for values in splits.values()):
        raise SystemExit("U06 requires non-empty train/dev/test splits")
    family_sets = {name: {frame.family for frame in values} for name, values in splits.items()}
    if any(family_sets[left] & family_sets[right] for left, right in (("train", "dev"), ("train", "test"), ("dev", "test"))):
        raise SystemExit("Configuration-family leakage across splits")
    ordered = [*splits["train"], *splits["dev"], *splits["test"]]
    image = feature_matrix(ordered)
    offsets = np.cumsum([0, len(splits["train"]), len(splits["dev"]), len(splits["test"])])
    image_by_split = {name: image[offsets[i]:offsets[i + 1]] for i, name in enumerate(("train", "dev", "test"))}
    context_by_split = {name: context_features(splits[name], image_by_split[name]) for name in splits}
    image_mean, image_scale = scale_fit(image_by_split["train"])
    context_mean, context_scale = scale_fit(context_by_split["train"])
    image_by_split = {name: (value - image_mean) / image_scale for name, value in image_by_split.items()}
    context_by_split = {name: (value - context_mean) / context_scale for name, value in context_by_split.items()}
    level_y = {name: np.asarray([LEVEL_TO_INDEX[frame.level] for frame in values], np.int64) for name, values in splits.items()}
    event_y = {name: event_labels(values) for name, values in splits.items()}
    emit("features", 1, 5, f"Prepared {len(frames)} frames with causal temporal context")

    training = config["training"]
    seeds = [int(value) for value in training["seeds"]]
    if args.smoke:
        seeds = seeds[:1]
    epochs = {key: (2 if args.smoke else int(value)) for key, value in training["epochs"].items()}
    candidates = []
    models = {}
    for candidate_index, seed in enumerate(seeds):
        direct = train_mlp(image_by_split["train"], level_y["train"], len(LEVEL_ORDER), training["hiddenSize"],
                           epochs["direct"], training["learningRate"], seed, f"seed-{seed}-direct", 2, 5)
        context = train_mlp(context_by_split["train"], level_y["train"], len(LEVEL_ORDER), training["hiddenSize"],
                            epochs["context"], training["learningRate"], seed + 101, f"seed-{seed}-context", 3, 5)
        event = train_mlp(context_by_split["train"], event_y["train"], len(EVENT_ORDER), training["hiddenSize"],
                          epochs["event"], training["learningRate"], seed + 211, f"seed-{seed}-event", 4, 5)
        direct_dev = predict(direct, image_by_split["dev"])
        context_dev = predict(context, context_by_split["dev"])
        event_dev = predict(event, context_by_split["dev"])
        best_threshold = None
        for threshold in np.arange(.10, .81, .025):
            predicted = apply_event_threshold(event_dev, float(threshold))
            event_score = confusion_metrics(event_y["dev"], predicted, EVENT_ORDER)
            binary = transition_metrics(event_y["dev"], predicted)
            score = event_score["macroF1"] + .25 * binary["f1"] - .1 * binary["falsePositives"] / len(predicted)
            row = (score, float(threshold), event_score, binary, predicted)
            if best_threshold is None or row[0] > best_threshold[0]:
                best_threshold = row
        level_pred = np.argmax(context_dev, axis=1)
        sequence = sequence_diagnostics(splits["dev"], best_threshold[4], level_pred)
        level_metrics = confusion_metrics(level_y["dev"], level_pred, LEVEL_ORDER)
        direct_metrics = confusion_metrics(level_y["dev"], np.argmax(direct_dev, axis=1), LEVEL_ORDER)
        selection_score = level_metrics["macroF1"] + .5 * best_threshold[2]["macroF1"] + .2 * sequence["orderingConsistency"]
        candidates.append({"seed": seed, "eventThreshold": best_threshold[1], "selectionScore": selection_score,
                           "directLevel": direct_metrics, "contextLevel": level_metrics,
                           "landmarkEvent": best_threshold[2], "transitionDetection": best_threshold[3],
                           "sequence": sequence})
        models[seed] = (direct, context, event)
        emit("selection", 4, 5, f"Completed dev-only candidate {candidate_index + 1}/{len(seeds)}")
    selected = max(candidates, key=lambda item: (item["selectionScore"], -item["seed"]))
    direct, context, event = models[selected["seed"]]
    direct_test = predict(direct, image_by_split["test"])
    context_test = predict(context, context_by_split["test"])
    event_test = predict(event, context_by_split["test"])
    event_pred = apply_event_threshold(event_test, selected["eventThreshold"])
    level_pred = np.argmax(context_test, axis=1)
    test_metrics = {
        "directSingleImageLevel": confusion_metrics(level_y["test"], np.argmax(direct_test, axis=1), LEVEL_ORDER),
        "causalContextLevel": confusion_metrics(level_y["test"], level_pred, LEVEL_ORDER),
        "landmarkEvent": confusion_metrics(event_y["test"], event_pred, EVENT_ORDER),
        "transitionDetection": transition_metrics(event_y["test"], event_pred),
        "sequence": sequence_diagnostics(splits["test"], event_pred, level_pred),
    }
    config_hash = hashlib.sha256(config_bytes).hexdigest()
    common = {"schemaVersion": 1, "taskId": "U06", "contract": CONTRACT, "experimentId": EXPERIMENT_ID,
              "u06ExperimentId": config["experimentId"], "datasetRunId": config["datasetRunId"],
              "datasetFingerprint": dataset_fingerprint(frames), "configSha256": config_hash,
              "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
              "forbiddenInputs": FORBIDDEN}
    checkpoint = {**common, "levelOrder": list(LEVEL_ORDER), "eventOrder": list(EVENT_ORDER),
                  "eventThreshold": selected["eventThreshold"], "selectedSeed": selected["seed"],
                  "featureDefinition": "fixed-conv-current + binned(current-previous) + abs-delta + issued-action",
                  "imageScaler": {"mean": image_mean.tolist(), "scale": image_scale.tolist()},
                  "contextScaler": {"mean": context_mean.tolist(), "scale": context_scale.tolist()},
                  "directBaselineHead": serial_model(direct, "u06-direct-image-level-mlp-v1"),
                  "contextLevelHead": serial_model(context, "u06-causal-context-level-mlp-v1"),
                  "landmarkEventHead": serial_model(event, "u06-landmark-event-mlp-v1")}
    report = {**common, "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "splitIntegrity": {name: {"frames": len(values), "episodes": len({f.episode_id for f in values}),
                                         "families": sorted(family_sets[name])} for name, values in splits.items()},
              "modelSelection": {"rule": "dev-only; heldout test evaluated once after seed/threshold selection",
                                   "selectedSeed": selected["seed"], "eventThreshold": selected["eventThreshold"],
                                   "candidates": candidates},
              "heldoutEvaluation": test_metrics,
              "qualityGate": {"informationalOnly": True, "autoPromoted": False,
                              "contextLevelAccuracyAtLeast080": test_metrics["causalContextLevel"]["accuracy"] >= .8,
                              "transitionF1AtLeast050": test_metrics["transitionDetection"]["f1"] >= .5},
              "limitations": ["Synthetic simulator labels provide training/evaluation anchors only.",
                              "A single anatomy cannot establish clinical or cross-patient generalization.",
                              "Absolute vertebral identity remains ambiguous without a learned sequence anchor."],
              "elapsedSeconds": time.perf_counter() - started}
    sequences = {**common, "split": "test", "maps": episode_maps(splits["test"], event_test, event_pred, context_test),
                 "diagnostics": test_metrics["sequence"]}
    outputs = dict(config["outputs"])
    if args.smoke:
        outputs = {key: str(Path(value).with_suffix(".smoke.json")) for key, value in outputs.items()}
    atomic_json(root / outputs["checkpoint"], checkpoint)
    atomic_json(root / outputs["report"], report)
    atomic_json(root / outputs["sequences"], sequences)
    emit("complete", 5, 5, "U06 artifacts written; no automatic promotion")
    print(json.dumps({"outputs": outputs, "selectedSeed": selected["seed"], "test": test_metrics,
                      "qualityGate": report["qualityGate"]}, indent=2))


if __name__ == "__main__":
    main()
