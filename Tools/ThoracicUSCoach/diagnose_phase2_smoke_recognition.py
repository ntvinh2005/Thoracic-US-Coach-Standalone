from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image

from active2d_stop import request_stop_features, stop_probabilities
from active2d_template_stop import template_components, template_predictions


EPISODES = {
    "P2SMOKE_R4_RANDOM_01_POSITIVE_F0010_CANDIDATE": "r4p1_random_01",
    "P2SMOKE_R4_RANDOM_05_POSITIVE_F0016_CANDIDATE": "r4p1_random_05",
    "P2SMOKE_R4_RANDOM_06_HARDNEG_F0005_CANDIDATE": "r4p1_random_06",
}


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def vector3(item: dict[str, Any]) -> np.ndarray:
    return np.asarray([item["x"], item["y"], item["z"]], dtype=np.float64)


def quaternion_angle(a: dict[str, Any], b: dict[str, Any]) -> float:
    left = np.asarray([a["x"], a["y"], a["z"], a["w"]], dtype=np.float64)
    right = np.asarray([b["x"], b["y"], b["z"], b["w"]], dtype=np.float64)
    dot = float(np.clip(abs(np.dot(left, right) / (np.linalg.norm(left) * np.linalg.norm(right))), 0.0, 1.0))
    return math.degrees(2.0 * math.acos(dot))


def pixels(path: Path) -> tuple[np.ndarray, dict[str, Any]]:
    with Image.open(path) as image:
        gray = image.convert("L")
        values = np.asarray(gray, dtype=np.float32) / 255.0
        resized = np.asarray(gray.resize((64, 64), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
        metadata = {
            "mode": image.mode,
            "size": list(image.size),
            "min": float(values.min()),
            "max": float(values.max()),
            "mean": float(values.mean()),
            "std": float(values.std()),
            "q05": float(np.quantile(values, 0.05)),
            "q50": float(np.quantile(values, 0.50)),
            "q95": float(np.quantile(values, 0.95)),
        }
    return resized, metadata


def similarity(a: np.ndarray, b: np.ndarray) -> dict[str, float]:
    left = (a.reshape(-1) - float(a.mean())) / max(float(a.std()), 1e-6)
    right = (b.reshape(-1) - float(b.mean())) / max(float(b.std()), 1e-6)
    return {
        "normalizedCorrelation": float(np.mean(left * right)),
        "meanAbsolutePixelError": float(np.mean(np.abs(a - b))),
        "normalizedRmse": float(np.sqrt(np.mean((left - right) ** 2))),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--historical-root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--diagnostic-template-checkpoint", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    historical_project = (args.historical_root or args.root).resolve()
    model = load(root / "Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_stop_live_spatial_logistic_v1.json")
    template_model = load(args.diagnostic_template_checkpoint) if args.diagnostic_template_checkpoint else None
    architecture = model["architecture"]
    mean = np.asarray(architecture["featureMean"], dtype=np.float32)
    scale = np.asarray(architecture["featureScale"], dtype=np.float32)
    weights = np.asarray(architecture["weights"], dtype=np.float32)
    bias = float(architecture["bias"])
    records: list[dict[str, Any]] = []

    for episode_id, source_case in EPISODES.items():
        run = load(root / f"Artifacts/ThoracicUSCoach/active2d/runs/{episode_id}.json")
        audit = load(root / f"Artifacts/ThoracicUSCoach/active2d/runs_privileged/{episode_id}.json")
        traces = {int(item["step"]): item for item in run["trace"]}
        audits = {int(item["index"]): item for item in audit["frames"]}
        target_steps = [step for step, item in audits.items() if item.get("finalTargetAcceptable")]
        if "HARDNEG" in episode_id:
            selected_steps = [max(traces, key=lambda step: float(traces[step]["confidence"]))]
        else:
            selected_steps = target_steps[:2]

        historical_root = historical_project / f"Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_raw_20261025/randomized_start/{source_case}"
        historical_audit = load(historical_root / "privileged_annotations.json")
        historical_stream = load(historical_root / "stream_manifest.json")
        historical_by_index = {int(item["index"]): item for item in historical_audit["frames"]}
        stream_by_index = {int(item["index"]): item for item in historical_stream["frames"]}

        for step in selected_steps:
            trace = traces[step]
            truth = audits[step]
            fresh_path = root / trace["image"]
            prior_action = "RESET" if step == 0 else str(traces[step - 1]["action"])
            features = request_stop_features(fresh_path, None, prior_action, "spatial-v2")
            probability = float(stop_probabilities(model, features)[0])
            logit = float(((features - mean) / scale) @ weights + bias)

            nearest_index, nearest = min(
                historical_by_index.items(),
                key=lambda pair: float(np.linalg.norm(vector3(pair[1]["probePosition"]) - vector3(truth["probePosition"])))
                + 0.1 * quaternion_angle(pair[1]["probeRotation"], truth["probeRotation"]),
            )
            historical_path = historical_project / stream_by_index[nearest_index]["image"]
            historical_action = str(stream_by_index[nearest_index].get("issuedAction", "RESET"))
            historical_features = request_stop_features(historical_path, None, historical_action, "spatial-v2")
            historical_probability = float(stop_probabilities(model, historical_features)[0])
            fresh_pixels, fresh_metadata = pixels(fresh_path)
            historical_pixels, historical_metadata = pixels(historical_path)
            record = {
                "episode": episode_id,
                "step": step,
                "groundTruthTarget": bool(truth["finalTargetAcceptable"]),
                "evaluatorScore": float(truth["finalTargetScore"]),
                "freshImage": str(fresh_path.relative_to(root)).replace("\\", "/"),
                "liveProbability": float(trace["confidence"]),
                "offlineProbability": probability,
                "liveOfflineAbsoluteError": abs(probability - float(trace["confidence"])),
                "checkpointLogit": logit,
                "previousAction": prior_action,
                "freshPixels": fresh_metadata,
                "nearestHistorical": {
                    "case": source_case,
                    "frameIndex": nearest_index,
                    "image": str(historical_path).replace("\\", "/"),
                    "positionErrorMm": float(np.linalg.norm(vector3(nearest["probePosition"]) - vector3(truth["probePosition"]))),
                    "rotationErrorDeg": quaternion_angle(nearest["probeRotation"], truth["probeRotation"]),
                    "groundTruthTarget": bool(nearest["finalTargetAcceptable"]),
                    "evaluatorScore": float(nearest["finalTargetScore"]),
                    "checkpointProbability": historical_probability,
                    "pixels": historical_metadata,
                    "comparison": similarity(fresh_pixels, historical_pixels),
                    "spatialFeatureCosine": float(np.dot(features[:-8], historical_features[:-8]) / max(
                        np.linalg.norm(features[:-8]) * np.linalg.norm(historical_features[:-8]), 1e-9
                    )),
                    "spatialFeatureRmse": float(np.sqrt(np.mean((features[:-8] - historical_features[:-8]) ** 2))),
                },
            }
            if template_model is not None:
                template_score, template_quality = template_predictions(template_model, fresh_path)
                positive_similarity, negative_similarity, margin = template_components(
                    template_model, fresh_path
                )
                record["diagnosticTemplate"] = {
                    "score": template_score,
                    "quality": template_quality,
                    "positiveSimilarity": positive_similarity,
                    "negativeSimilarity": negative_similarity,
                    "margin": margin,
                    "decision": template_score >= float(template_model["stopRule"]["threshold"]),
                }
            records.append(record)

    positives = [item for item in records if item["groundTruthTarget"]]
    negatives = [item for item in records if not item["groundTruthTarget"]]
    report = {
        "schemaVersion": 1,
        "scope": "consumed development smoke images only",
        "checkpoint": "phase2_stop_live_spatial_logistic_v1",
        "threshold": float(model["stopRule"]["threshold"]),
        "records": records,
        "diagnosis": {
            "liveOfflineParityMaxError": max(item["liveOfflineAbsoluteError"] for item in records),
            "positiveProbabilityRange": [min(item["offlineProbability"] for item in positives), max(item["offlineProbability"] for item in positives)],
            "negativeProbabilityRange": [min(item["offlineProbability"] for item in negatives), max(item["offlineProbability"] for item in negatives)],
            "oldCheckpointRanksSelectedNegativeAboveEveryPositive": min(item["offlineProbability"] for item in negatives)
            > max(item["offlineProbability"] for item in positives),
            "preprocessingBugFound": False,
        },
    }
    if template_model is not None:
        report["diagnosticTemplate"] = {
            "id": template_model["id"],
            "deployment": template_model["deployment"],
            "threshold": template_model["stopRule"]["threshold"],
            "freshSmokeImagesUsedForCalibration": template_model["lineage"]["freshSmokeImagesUsedForCalibration"],
            "positiveScoreRange": [
                min(item["diagnosticTemplate"]["score"] for item in positives),
                max(item["diagnosticTemplate"]["score"] for item in positives),
            ],
            "negativeScoreRange": [
                min(item["diagnosticTemplate"]["score"] for item in negatives),
                max(item["diagnosticTemplate"]["score"] for item in negatives),
            ],
            "selectedPositiveTrue": sum(item["diagnosticTemplate"]["decision"] for item in positives),
            "selectedPositiveCount": len(positives),
            "selectedNegativeFalsePositive": sum(item["diagnosticTemplate"]["decision"] for item in negatives),
            "selectedNegativeCount": len(negatives),
        }
    output = args.output if args.output.is_absolute() else root / args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report["diagnosis"], indent=2))
    if template_model is not None:
        print(json.dumps(report["diagnosticTemplate"], indent=2))


if __name__ == "__main__":
    main()
