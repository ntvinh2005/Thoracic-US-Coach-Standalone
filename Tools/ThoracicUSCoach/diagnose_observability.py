from __future__ import annotations

import argparse
import json
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image

from coach_data import DEFAULT_DATASET_RUN_ID, DEFAULT_EXPERIMENT_ID, load_frames, pixel_features, project_root, save_json


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"
COMPONENTS = (
    "anatomyRelationship",
    "pleuraVisibility",
    "targetRegion",
    "acousticWindow",
    "needleGeometry",
    "shadowPenalty",
    "contactPenalty",
)


def image_stats(path: Path, size: int = 16) -> tuple[np.ndarray, float, float]:
    with Image.open(path) as image:
        values = np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR), dtype=np.float32
        ).reshape(-1) / 255.0
    return values, float(values.mean()), float(values.std())


def binary_metrics(labels: np.ndarray, predictions: np.ndarray) -> dict[str, object]:
    labels = labels.astype(bool)
    predictions = predictions.astype(bool)
    tp = int(np.sum(labels & predictions))
    fp = int(np.sum(~labels & predictions))
    fn = int(np.sum(labels & ~predictions))
    tn = int(np.sum(~labels & ~predictions))
    return {
        "count": len(labels),
        "tp": tp,
        "fp": fp,
        "fn": fn,
        "tn": tn,
        "precision": tp / (tp + fp) if tp + fp else None,
        "recall": tp / (tp + fn) if tp + fn else None,
        "falsePositiveRate": fp / (fp + tn) if fp + tn else None,
    }


def summarize(values) -> dict[str, object]:
    array = np.asarray(list(values), dtype=np.float64)
    if len(array) == 0:
        return {"count": 0}
    return {
        "count": len(array),
        "mean": float(np.mean(array)),
        "std": float(np.std(array)),
        "min": float(np.min(array)),
        "q25": float(np.quantile(array, 0.25)),
        "median": float(np.median(array)),
        "q75": float(np.quantile(array, 0.75)),
        "max": float(np.max(array)),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Development-only preprocessing and target-observability diagnostic")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--detector", type=Path, required=True)
    parser.add_argument("--cache-live-audit", type=Path, required=True)
    parser.add_argument("--feature-size", type=int, default=16)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    detector = json.loads(args.detector.read_text(encoding="utf-8"))
    cache_live = json.loads(args.cache_live_audit.read_text(encoding="utf-8"))
    threshold = float(detector["stopThreshold"])
    weights = np.asarray(detector["weights"], dtype=np.float32)
    bias = float(detector["bias"])

    frames = load_frames(
        experiment_id=args.experiment_id,
        dataset_run_id=args.dataset_run_id,
        require_complete=True,
        splits={"train", "dev"},
    )
    allowed = {(item.sweep_id, item.step_index): item for item in frames}
    records = []
    for split in ("train", "dev"):
        for manifest_path in sorted((root / "Artifacts" / "ThoracicUSCoach" / "dataset" / split).glob("*/sweep_manifest.json")):
            payload = json.loads(manifest_path.read_text(encoding="utf-8"))
            if payload.get("experimentId") != args.experiment_id or payload.get("datasetRunId") != args.dataset_run_id:
                continue
            trajectory = payload["trajectory"]
            floor = float(trajectory["referenceScoreFloor"])
            sweep_id = str(payload["sweepId"])
            for item in payload["frames"]:
                index = int(item["stepIndex"])
                frame = allowed[(sweep_id, index)]
                raw, mean, std = image_stats(frame.image, args.feature_size)
                zscore = pixel_features(frame.image, args.feature_size)
                probability = float(1.0 / (1.0 + np.exp(-np.clip(float(zscore @ weights + bias), -40.0, 40.0))))
                score = item["score"]
                records.append({
                    "split": split,
                    "sweepId": sweep_id,
                    "configurationFamily": str(trajectory["configurationFamily"]),
                    "pathConstructor": "BuildVariedPath",
                    "index": index,
                    "image": str(frame.image.relative_to(root)).replace("\\", "/"),
                    "acceptable": bool(item["targetAcceptable"]),
                    "raw": raw,
                    "zscore": zscore,
                    "mean": mean,
                    "std": std,
                    "score": float(score["total"]),
                    "floor": floor,
                    "margin": float(score["total"]) - floor,
                    "components": {key: float(score.get(key, 0.0)) for key in COMPONENTS},
                    "detectorProbability": probability,
                    "detectorPrediction": probability >= threshold,
                })

    dev = [item for item in records if item["split"] == "dev"]
    train = [item for item in records if item["split"] == "train"]
    labels = np.asarray([item["acceptable"] for item in dev], dtype=bool)
    predictions = np.asarray([item["detectorPrediction"] for item in dev], dtype=bool)

    component_summary = {}
    for component in COMPONENTS:
        component_summary[component] = {
            "acceptable": summarize(item["components"][component] for item in dev if item["acceptable"]),
            "unacceptable": summarize(item["components"][component] for item in dev if not item["acceptable"]),
        }

    by_sweep = defaultdict(list)
    for item in dev:
        by_sweep[item["sweepId"]].append(item)
    boundary_pairs = []
    internal_holes = []
    for sweep_id, items in by_sweep.items():
        items.sort(key=lambda item: item["index"])
        acceptable_indices = [item["index"] for item in items if item["acceptable"]]
        if acceptable_indices:
            low, high = min(acceptable_indices), max(acceptable_indices)
            internal_holes.extend(
                item for item in items if low < item["index"] < high and not item["acceptable"]
            )
        for left, right in zip(items, items[1:]):
            if left["acceptable"] == right["acceptable"]:
                continue
            boundary_pairs.append({
                "sweepId": sweep_id,
                "leftIndex": left["index"],
                "rightIndex": right["index"],
                "leftAcceptable": left["acceptable"],
                "rightAcceptable": right["acceptable"],
                "rawRms16": float(np.sqrt(np.mean((left["raw"] - right["raw"]) ** 2))),
                "zscoreRms16": float(np.sqrt(np.mean((left["zscore"] - right["zscore"]) ** 2))),
                "scoreDelta": abs(left["score"] - right["score"]),
                "leftMargin": left["margin"],
                "rightMargin": right["margin"],
                "leftImage": left["image"],
                "rightImage": right["image"],
            })

    positive = [item for item in dev if item["acceptable"]]
    negative = [item for item in dev if not item["acceptable"]]
    positive_matrix = np.stack([item["raw"] for item in positive])
    negative_matrix = np.stack([item["raw"] for item in negative])
    dimensions = positive_matrix.shape[1]
    squared = (
        np.sum(positive_matrix * positive_matrix, axis=1)[:, None]
        + np.sum(negative_matrix * negative_matrix, axis=1)[None, :]
        - 2.0 * positive_matrix @ negative_matrix.T
    )
    distances = np.sqrt(np.maximum(0.0, squared) / dimensions)
    nearest_pairs = []
    for positive_index, negative_index in zip(*np.unravel_index(np.argsort(distances, axis=None)[:20], distances.shape)):
        inside = positive[int(positive_index)]
        outside = negative[int(negative_index)]
        nearest_pairs.append({
            "rawRms16": float(distances[positive_index, negative_index]),
            "acceptableImage": inside["image"],
            "unacceptableImage": outside["image"],
            "sameSweep": inside["sweepId"] == outside["sweepId"],
            "acceptableMargin": inside["margin"],
            "unacceptableMargin": outside["margin"],
            "acceptableDetectorProbability": inside["detectorProbability"],
            "unacceptableDetectorProbability": outside["detectorProbability"],
        })

    std_cut = float(np.quantile([item["std"] for item in dev], 0.25))
    low_contrast_mask = np.asarray([item["std"] <= std_cut for item in dev], dtype=bool)
    error_mask = predictions != labels
    pair_distances = np.asarray([item["rawRms16"] for item in nearest_pairs])
    cache_raw = float(cache_live["summary"]["rawImageRms32Mean"])
    cache_feature = float(cache_live["summary"]["cachedVsLiveFeatureRmsMean"])
    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "sourceDatasetRunId": args.dataset_run_id,
        "sourceSplits": ["train", "dev"],
        "heldoutOrStressAccessed": False,
        "pathConstructorAudit": {
            "observedConstructors": ["BuildVariedPath"],
            "capturedFrames": len(records),
            "targetFloor": "trajectory.referenceScoreFloor locked from discovery",
            "demoDifference": "BuildPath uses path-relative max * acceptableRelativeToBest and is not this dataset constructor",
        },
        "targetCounts": {
            "train": {"acceptable": sum(item["acceptable"] for item in train), "total": len(train)},
            "dev": {"acceptable": int(np.sum(labels)), "total": len(dev)},
            "devInternalUnacceptableHoles": len(internal_holes),
        },
        "detectorOnAllDevAtFrozenThreshold": binary_metrics(labels, predictions),
        "lowContrast": {
            "definition": "bottom quartile of 16x16 raw grayscale standard deviation",
            "stdThreshold": std_cut,
            "count": int(np.sum(low_contrast_mask)),
            "errorRate": float(np.mean(error_mask[low_contrast_mask])),
            "otherErrorRate": float(np.mean(error_mask[~low_contrast_mask])),
            "acceptableRate": float(np.mean(labels[low_contrast_mask])),
        },
        "imageDerivedStatistics": {
            "meanAcceptable": summarize(item["mean"] for item in dev if item["acceptable"]),
            "meanUnacceptable": summarize(item["mean"] for item in dev if not item["acceptable"]),
            "stdAcceptable": summarize(item["std"] for item in dev if item["acceptable"]),
            "stdUnacceptable": summarize(item["std"] for item in dev if not item["acceptable"]),
        },
        "scoreComponents": component_summary,
        "scoreMargin": {
            "acceptable": summarize(item["margin"] for item in dev if item["acceptable"]),
            "unacceptable": summarize(item["margin"] for item in dev if not item["acceptable"]),
        },
        "adjacentBoundaryPairs": {
            "count": len(boundary_pairs),
            "rawRms16": summarize(item["rawRms16"] for item in boundary_pairs),
            "zscoreRms16": summarize(item["zscoreRms16"] for item in boundary_pairs),
            "scoreDelta": summarize(item["scoreDelta"] for item in boundary_pairs),
            "closestExamples": sorted(boundary_pairs, key=lambda item: item["rawRms16"])[:12],
        },
        "nearestOppositeLabelPairs": {
            "countReported": len(nearest_pairs),
            "minimumRawRms16": float(np.min(pair_distances)),
            "pairsBelow0_005": int(np.sum(pair_distances < 0.005)),
            "pairsBelow0_01": int(np.sum(pair_distances < 0.01)),
            "examples": nearest_pairs,
        },
        "cacheLiveAmplification": {
            "rawImageRms32Mean": cache_raw,
            "perImageZscoreFeatureRmsMean": cache_feature,
            "featureToRawRmsRatio": cache_feature / cache_raw if cache_raw else None,
            "note": "Different feature dimensions, but the ratio quantifies sensitivity of the actual preprocessing to small render variation.",
        },
        "interpretation": [
            "The target score combines rendered intensity with privileged anatomy labels and geometry.",
            "Opposite-label near-neighbors and detector errors establish ambiguity for this linear representation, not impossibility for ultrasound observations.",
            "The acceptability threshold was not changed.",
        ],
    }
    output = output_dir / "observability_audit.json"
    save_json(output, report)
    print(json.dumps({
        "phase": "DIAGNOSTIC",
        "output": str(output),
        "targetCounts": report["targetCounts"],
        "lowContrast": report["lowContrast"],
        "adjacentBoundaryPairs": report["adjacentBoundaryPairs"],
        "nearestOppositeLabelPairs": report["nearestOppositeLabelPairs"],
        "cacheLiveAmplification": report["cacheLiveAmplification"],
    }, indent=2))


if __name__ == "__main__":
    main()
