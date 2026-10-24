from __future__ import annotations

import argparse
import hashlib
import json
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np

from active2d_data import Active2DFrame, dataset_fingerprint, load_active2d_frames
from active2d_stop import (
    DEPLOYMENT_GATE,
    frame_stop_feature_matrix_v2,
    frame_temporal_feature_matrix_v3,
    stop_probabilities,
    validate_stop_checkpoint,
)
from train_phase2_stop import classification_metrics


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fit_logistic(x: np.ndarray, y: np.ndarray, *, epochs: int = 600) -> tuple[np.ndarray, float]:
    n, width = x.shape
    weights = np.zeros(width, dtype=np.float64)
    bias = 0.0
    positives = max(int(y.sum()), 1)
    negatives = max(n - positives, 1)
    sample_weight = np.where(y > 0.5, n / (2.0 * positives), n / (2.0 * negatives))
    m = np.zeros_like(weights); v = np.zeros_like(weights)
    mb = vb = 0.0
    for step in range(1, epochs + 1):
        logits = np.clip(x @ weights + bias, -25.0, 25.0)
        probs = 1.0 / (1.0 + np.exp(-logits))
        error = sample_weight * (probs - y)
        grad = x.T @ error / n + 1e-3 * weights
        grad_b = float(error.mean())
        m = 0.9 * m + 0.1 * grad; v = 0.999 * v + 0.001 * grad * grad
        mb = 0.9 * mb + 0.1 * grad_b; vb = 0.999 * vb + 0.001 * grad_b * grad_b
        rate = 0.02
        weights -= rate * (m / (1.0 - 0.9**step)) / (np.sqrt(v / (1.0 - 0.999**step)) + 1e-8)
        bias -= rate * (mb / (1.0 - 0.9**step)) / (np.sqrt(vb / (1.0 - 0.999**step)) + 1e-8)
    return weights.astype(np.float32), float(bias)


def fit_quality(x: np.ndarray, y: np.ndarray, ridge: float = 5.0) -> tuple[np.ndarray, float]:
    bias = float(y.mean())
    centered = y - bias
    # Dual ridge avoids a 1035x1035 solve for this deliberately bounded dataset.
    alpha = np.linalg.solve(x @ x.T + ridge * np.eye(len(x)), centered)
    return (x.T @ alpha).astype(np.float32), bias


def rankdata(values: np.ndarray) -> np.ndarray:
    order = np.argsort(values, kind="mergesort")
    ranks = np.empty(len(values), dtype=np.float64)
    start = 0
    while start < len(values):
        end = start + 1
        while end < len(values) and values[order[end]] == values[order[start]]: end += 1
        ranks[order[start:end]] = 0.5 * (start + end - 1)
        start = end
    return ranks


def spearman(a: np.ndarray, b: np.ndarray) -> float:
    if len(a) < 2: return 0.0
    ra, rb = rankdata(a), rankdata(b)
    if np.std(ra) < 1e-12 or np.std(rb) < 1e-12: return 0.0
    return float(np.corrcoef(ra, rb)[0, 1])


def metrics(frames: list[Active2DFrame], stop: np.ndarray, quality: np.ndarray, threshold: float) -> dict[str, Any]:
    labels = np.asarray([item.target_acceptable for item in frames], dtype=bool)
    classification = classification_metrics(labels, stop, threshold)
    groups: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames): groups[frame.episode_id].append(index)
    observed = recognized = wrong = 0
    regrets: list[float] = []
    for indices in groups.values():
        actual = np.asarray([frames[index].target_score for index in indices])
        predicted = quality[indices]
        has_target = any(frames[index].target_acceptable for index in indices)
        observed += int(has_target)
        predicted_stop = [index for index in indices if stop[index] >= threshold]
        recognized += int(has_target and any(frames[index].target_acceptable for index in predicted_stop))
        wrong += int(any(not frames[index].target_acceptable for index in predicted_stop))
        regrets.append(float(actual.max() - actual[int(np.argmax(predicted))]))
    return {
        "classification": classification,
        "qualitySpearman": spearman(np.asarray([f.target_score for f in frames]), quality),
        "medianEpisodeBestViewRegret": float(np.median(regrets)),
        "episodes": len(groups), "targetObservedEpisodes": observed,
        "targetRecognizedEpisodes": recognized, "wrongStopEpisodes": wrong,
    }


def select_threshold(labels: np.ndarray, scores: np.ndarray) -> tuple[float | None, dict[str, Any]]:
    candidates = sorted(set([0.0, 1.0, *map(float, scores)]))
    rows = [(threshold, classification_metrics(labels, scores, threshold)) for threshold in candidates]
    eligible = [row for row in rows if row[1]["falsePositive"] == 0 and row[1]["recall"] >= 0.80]
    if eligible:
        selected = max(eligible, key=lambda row: (row[1]["recall"], row[0]))
        return float(selected[0]), {
            "status": "SAFE_OPERATING_POINT",
            "eligibleZeroWrongStopRecall80": True,
            **selected[1],
        }
    fallback = max(rows, key=lambda row: (-row[1]["falsePositive"], row[1]["recall"], row[0]))
    return None, {
        "status": "NO_SAFE_OPERATING_POINT",
        "eligibleZeroWrongStopRecall80": False,
        "diagnosticFallbackThreshold": float(fallback[0]),
        "diagnosticFallbackMetrics": fallback[1],
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    frames = load_active2d_frames(
        "g2_temporal_stop_v1_20261024", root=root,
        dataset_relative="Artifacts/ThoracicUSCoach/active2d/g2_temporal_capture",
    )
    splits = {name: [f for f in frames if f.split == name] for name in ("train", "calibration", "heldout_dev")}
    families = {name: {f.family for f in values} for name, values in splits.items()}
    if not all(splits.values()) or any(families[a] & families[b] for a in families for b in families if a < b):
        raise RuntimeError("Missing G2 split or family leakage")
    x = {name: frame_temporal_feature_matrix_v3(values) for name, values in splits.items()}
    mean = x["train"].mean(axis=0)
    scale = x["train"].std(axis=0); scale[scale < 1e-5] = 1.0
    z = {name: (values - mean) / scale for name, values in x.items()}
    y_stop = {name: np.asarray([f.target_acceptable for f in values], dtype=np.float64) for name, values in splits.items()}
    y_quality = {name: np.asarray([f.target_score for f in values], dtype=np.float64) for name, values in splits.items()}
    stop_weights, stop_bias = fit_logistic(z["train"], y_stop["train"])
    quality_weights, quality_bias = fit_quality(z["train"], y_quality["train"])
    provisional = {
        "schema": "active2d-image-only-stop-v1", "version": 2,
        "contract": "active2d_image_only_v1",
        "experimentId": "thoracic_active2d_image_only_v1_20261023",
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": ["UnityAnatomyLabels", "probePose", "targetCoordinates", "finalTargetScore"],
        "architecture": {
            "type": "standardized-temporal-quality-stop-v1", "featureExtractor": "temporal-v3",
            "featureWidth": int(x["train"].shape[1]), "featureMean": mean.tolist(), "featureScale": scale.tolist(),
            "qualityWeights": quality_weights.tolist(), "qualityBias": quality_bias,
            "stopWeights": stop_weights.tolist(), "stopBias": stop_bias,
        },
        "stopRule": {"threshold": 0.5, "requiredConfirmations": 1},
    }
    calibration_stop = stop_probabilities(provisional, x["calibration"])
    threshold, calibration_selection = select_threshold(y_stop["calibration"].astype(bool), calibration_stop)
    provisional["stopRule"]["threshold"] = threshold
    provisional["deployment"] = {
        "gate": DEPLOYMENT_GATE,
        "status": "DEPLOYABLE" if threshold is not None else "BLOCKED",
        "safeOperatingPoint": threshold is not None,
        "reason": "calibration-gate-pass" if threshold is not None else "no-safe-calibration-operating-point",
    }
    provisional["training"] = {
        "taskId": "G2", "candidateCount": 1, "thresholdSelectionSplit": "calibration",
        "datasetFingerprint": dataset_fingerprint(frames),
        "splitFamilies": {name: sorted(values) for name, values in families.items()},
    }
    validate_stop_checkpoint(provisional)
    args.checkpoint.parent.mkdir(parents=True, exist_ok=True)
    args.checkpoint.write_text(json.dumps(provisional, indent=2) + "\n", encoding="utf-8")
    candidate_scores = {name: stop_probabilities(provisional, x[name]) for name in splits}
    evaluation_threshold = threshold if threshold is not None else float(
        calibration_selection["diagnosticFallbackThreshold"]
    )
    candidate_quality = {
        name: np.clip(z[name] @ quality_weights + quality_bias, 0.0, 1.0) for name in splits
    }
    baseline = json.loads(args.baseline.read_text(encoding="utf-8")); validate_stop_checkpoint(baseline)
    heldout_baseline_features = frame_stop_feature_matrix_v2(splits["heldout_dev"])
    baseline_scores = stop_probabilities(baseline, heldout_baseline_features)
    baseline_quality = baseline_scores
    heldout = metrics(splits["heldout_dev"], candidate_scores["heldout_dev"], candidate_quality["heldout_dev"], evaluation_threshold)
    baseline_result = metrics(splits["heldout_dev"], baseline_scores, baseline_quality, 0.93)
    positive_families = len({f.family for f in splits["heldout_dev"] if f.target_acceptable})
    offline_pass = (
        int(y_stop["heldout_dev"].sum()) >= 4 and positive_families >= 2
        and heldout["classification"]["recall"] >= 0.80
        and heldout["classification"]["falsePositive"] == 0
        and heldout["qualitySpearman"] >= 0.60
        and heldout["medianEpisodeBestViewRegret"] <= 0.015
        and calibration_selection["eligibleZeroWrongStopRecall80"]
    )
    report = {
        "schemaVersion": 1, "taskId": "G2", "status": "OFFLINE_PASS" if offline_pass else "OFFLINE_BLOCKED",
        "checkpoint": args.checkpoint.relative_to(root).as_posix(), "checkpointSha256": sha256(args.checkpoint),
        "baseline": args.baseline.relative_to(root).as_posix(), "baselineSha256": sha256(args.baseline),
        "baselineThresholdUnchanged": 0.93,
        "dataset": {
            "datasetRunId": "g2_temporal_stop_v1_20261024", "fingerprint": dataset_fingerprint(frames),
            "frames": {name: len(values) for name, values in splits.items()},
            "positiveFrames": {name: int(y_stop[name].sum()) for name in splits},
            "families": {name: sorted(values) for name, values in families.items()},
            "heldoutPositiveFamilies": positive_families,
            "u08ABUse": "regression only; excluded from train/calibration/heldout-development",
        },
        "calibration": {"selectedThreshold": threshold, **calibration_selection},
        "candidate": {
            "train": metrics(splits["train"], candidate_scores["train"], candidate_quality["train"], evaluation_threshold),
            "calibration": metrics(splits["calibration"], candidate_scores["calibration"], candidate_quality["calibration"], evaluation_threshold),
            "heldoutDevelopment": heldout,
        },
        "frozenSpatialV2HeldoutDevelopment": baseline_result,
        "offlineGate": {
            "pass": offline_pass,
            "requirements": {"targetRecall": 0.80, "wrongStopFrames": 0, "qualitySpearman": 0.60,
                             "medianEpisodeBestViewRegretMax": 0.015, "positiveFramesMin": 4,
                             "positiveFamiliesMin": 2},
        },
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "checkpoint": report["checkpoint"], "report": str(args.report)}, indent=2))


if __name__ == "__main__":
    main()
