"""Evaluator-only E2 acquisition-geometry screening for relative spine mapping."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image

from position_aware_relocalization import block_ssim


LEVELS = [f"T{number}" for number in range(2, 9)]
GEOMETRIES = ("PARAMEDIAN_LONGITUDINAL_P6", "PARAMEDIAN_LONGITUDINAL_M6")


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def load_gray(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        array = np.asarray(image.convert("L"), dtype=np.float64) / 255.0
    if array.shape[0] > 256 and array.shape[0] == array.shape[1] and array.shape[0] % 256 == 0:
        factor = array.shape[0] // 256
        array = array.reshape(256, factor, 256, factor).mean(axis=(1, 3))
    return array


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def morphology_descriptor(image: np.ndarray) -> np.ndarray:
    if image.ndim != 2 or image.shape[0] != image.shape[1] or image.shape[0] % 16 != 0:
        raise ValueError("E2 analysis requires square clean ultrasound images divisible into 16 blocks")
    block = image.shape[0] // 16
    normalized = (image - np.mean(image)) / max(np.std(image), 1e-6)
    pooled = normalized.reshape(16, block, 16, block).mean(axis=(1, 3))
    gy, gx = np.gradient(normalized)
    edge = np.hypot(gx, gy).reshape(16, block, 16, block).mean(axis=(1, 3))
    descriptor = np.concatenate((pooled.ravel(), edge.ravel()))
    return descriptor / max(np.linalg.norm(descriptor), 1e-9)


def cosine_distance(left: np.ndarray, right: np.ndarray) -> float:
    return float(1.0 - np.clip(np.dot(left, right), -1.0, 1.0))


def nearest_level_predictions(reference: dict[str, np.ndarray], query: dict[str, np.ndarray]) -> list[dict]:
    rows = []
    for level in LEVELS:
        distances = {candidate: cosine_distance(query[level], reference[candidate])
                     for candidate in LEVELS}
        ordered = sorted(distances.items(), key=lambda item: item[1])
        rows.append({
            "queryLevel": level,
            "predictedLevel": ordered[0][0],
            "correct": ordered[0][0] == level,
            "bestDistance": ordered[0][1],
            "secondBestDistance": ordered[1][1],
            "margin": ordered[1][1] - ordered[0][1],
        })
    return rows


def provenance_features(path: Path) -> dict:
    provenance = load_json(path)
    beams = provenance["beams"]
    shadow_indices = [beam["beamIndex"] for beam in beams
                      if beam.get("hasShadowInterface")
                      and beam.get("firstShadowObject") == "Skeleton"]
    depths = [beam["firstShadowDepthFraction"] for beam in beams
              if beam.get("hasShadowInterface")
              and beam.get("firstShadowObject") == "Skeleton"]
    return {
        "beamCount": len(beams),
        "skeletonShadowFraction": len(shadow_indices) / len(beams),
        "skeletonShadowSpanFraction": (
            (max(shadow_indices) - min(shadow_indices) + 1) / len(beams)
            if shadow_indices else 0.0),
        "medianSkeletonShadowDepth": float(np.median(depths)) if depths else None,
        "minimumSkeletonShadowDepth": float(np.min(depths)) if depths else None,
    }


def validate_capture(experiment_root: Path, capture: dict) -> None:
    required = ("contactValid", "workspaceValid", "frameIntegrityValid", "provenanceMatched")
    if not all(capture.get(key) for key in required):
        raise ValueError(f"Invalid E2 capture: {capture['geometry']} {capture['level']} {capture['repeat']}")
    image_path = experiment_root / capture["image"]
    if sha256(image_path) != capture["imageSha256"]:
        raise ValueError(f"E2 image hash mismatch: {image_path}")


def analyze_geometry(experiment_root: Path, captures: list[dict], geometry: str) -> dict:
    lookup = {(item["level"], item["repeat"]): item for item in captures
              if item["geometry"] == geometry}
    if set(lookup) != {(level, repeat) for level in LEVELS for repeat in ("repeat_a", "repeat_b")}:
        raise ValueError(f"Incomplete E2 targeted captures for {geometry}")
    images = {}
    descriptors = {}
    provenance = {}
    for key, capture in lookup.items():
        validate_capture(experiment_root, capture)
        images[key] = load_gray(experiment_root / capture["image"])
        descriptors[key] = morphology_descriptor(images[key])
        provenance[key] = provenance_features(experiment_root / capture["provenance"])

    same_pose = []
    for level in LEVELS:
        same_pose.append({
            "level": level,
            "ssim": float(block_ssim(images[(level, "repeat_a")], images[(level, "repeat_b")])),
            "descriptorDistance": cosine_distance(
                descriptors[(level, "repeat_a")], descriptors[(level, "repeat_b")]),
        })
    adjacent = []
    for before, after in zip(LEVELS[:-1], LEVELS[1:]):
        adjacent.append({
            "before": before,
            "after": after,
            "ssim": float(block_ssim(images[(before, "repeat_a")], images[(after, "repeat_a")])),
            "descriptorDistance": cosine_distance(
                descriptors[(before, "repeat_a")], descriptors[(after, "repeat_a")]),
        })
    predictions = nearest_level_predictions(
        {level: descriptors[(level, "repeat_a")] for level in LEVELS},
        {level: descriptors[(level, "repeat_b")] for level in LEVELS})
    within = np.asarray([item["descriptorDistance"] for item in same_pose])
    adjacent_distance = np.asarray([item["descriptorDistance"] for item in adjacent])
    all_different = np.asarray([
        cosine_distance(descriptors[(left, "repeat_a")], descriptors[(right, "repeat_a")])
        for index, left in enumerate(LEVELS) for right in LEVELS[index + 1:]
    ])
    correct = sum(item["correct"] for item in predictions)
    minimum_repeat_margin = float(np.min(adjacent_distance) - np.max(within))
    diagnostic_separable = correct == 7 and minimum_repeat_margin > 0.0
    per_level = []
    for level in LEVELS:
        item = lookup[(level, "repeat_a")]
        per_level.append({
            "level": level,
            "image": item["image"],
            "repeatImage": lookup[(level, "repeat_b")]["image"],
            "samePoseSsim": next(row["ssim"] for row in same_pose if row["level"] == level),
            "samePoseDescriptorDistance": next(
                row["descriptorDistance"] for row in same_pose if row["level"] == level),
            **provenance[(level, "repeat_a")],
        })
    return {
        "geometry": geometry,
        "captureCount": len(lookup),
        "validCaptureCount": len(lookup),
        "samePose": same_pose,
        "adjacentLevels": adjacent,
        "repeatSplitNearestLevel": predictions,
        "repeatSplitCorrect": correct,
        "repeatSplitAccuracy": correct / 7.0,
        "medianWithinRepeatDescriptorDistance": float(np.median(within)),
        "maximumWithinRepeatDescriptorDistance": float(np.max(within)),
        "medianAdjacentDescriptorDistance": float(np.median(adjacent_distance)),
        "minimumAdjacentDescriptorDistance": float(np.min(adjacent_distance)),
        "medianAllDifferentLevelDescriptorDistance": float(np.median(all_different)),
        "minimumAdjacentMinusMaximumRepeatDistance": minimum_repeat_margin,
        "targetedReferenceSeparability": "SUPPORTED" if diagnostic_separable else "NOT_SUPPORTED",
        "mappingPotential": (
            "PROMISING_REQUIRES_CONTINUOUS_SPATIAL_AND_ANATOMICAL_VALIDATION"
            if diagnostic_separable else "LOW"
        ),
        "perLevel": per_level,
        "limitation": (
            "Evaluator-guided reference-pose separability is an upper-bound diagnostic. It does not localize events "
            "from an unlabeled sweep and cannot establish vertebral identity within the combined Skeleton mesh."
        ),
    }


def draw_contact_sheet(experiment_root: Path, captures: list[dict], output: Path) -> None:
    lookup = {(item["geometry"], item["level"], item["repeat"]): item for item in captures}
    fig, axes = plt.subplots(4, 7, figsize=(17, 10), squeeze=False)
    for column, level in enumerate(LEVELS):
        for row, (geometry, repeat) in enumerate((
                (GEOMETRIES[0], "repeat_a"), (GEOMETRIES[0], "repeat_b"),
                (GEOMETRIES[1], "repeat_a"), (GEOMETRIES[1], "repeat_b"))):
            image = load_gray(experiment_root / lookup[(geometry, level, repeat)]["image"])
            axes[row, column].imshow(image, cmap="gray", vmin=0, vmax=1)
            axes[row, column].axis("off")
            if row == 0:
                axes[row, column].set_title(level)
            if column == 0:
                axes[row, column].set_ylabel(f"{geometry[-2:]} {repeat[-1]}")
    fig.suptitle("E2 evaluator-guided paramedian longitudinal original-scene captures")
    fig.tight_layout()
    fig.savefig(output, dpi=160)
    plt.close(fig)


def draw_distance_matrices(results: list[dict], output: Path) -> None:
    fig, axes = plt.subplots(1, 2, figsize=(12, 5), squeeze=False)
    for axis, result in zip(axes[0], results):
        distances = np.zeros((7, 7))
        for prediction_index, prediction in enumerate(result["repeatSplitNearestLevel"]):
            query_level = prediction["queryLevel"]
            for candidate_index, candidate in enumerate(LEVELS):
                row = next(item for item in result["repeatSplitNearestLevel"]
                           if item["queryLevel"] == query_level)
                if candidate == row["predictedLevel"]:
                    distances[prediction_index, candidate_index] = row["bestDistance"]
                else:
                    distances[prediction_index, candidate_index] = np.nan
        axis.bar(np.arange(7), [item["margin"] for item in result["repeatSplitNearestLevel"]])
        axis.axhline(0.0, color="black", linewidth=0.8)
        axis.set_xticks(np.arange(7), LEVELS)
        axis.set_ylabel("NN second-best margin")
        axis.set_title(result["geometry"])
    fig.suptitle("Independent repeat-to-reference level discrimination (diagnostic upper bound)")
    fig.tight_layout()
    fig.savefig(output, dpi=160)
    plt.close(fig)


def write_csv(path: Path, rows: list[dict]) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def analyze(experiment_root: Path, historical_root: Path) -> dict:
    manifest_path = experiment_root / "evaluator_only" / "e2_geometry" / "capture_manifest.json"
    manifest = load_json(manifest_path)
    results = [analyze_geometry(experiment_root, manifest["captures"], geometry)
               for geometry in GEOMETRIES]
    comparison = [
        {
            "geometry": "TRANSVERSE_CENTER",
            "safe": True,
            "t2ToT8ImageEvidence": "CAUSAL_COMBINED_SKELETON_7_OF_7_LEVEL_IDENTITY_UNRESOLVED",
            "adjacentSeparability": "HISTORICAL_G2_FAIL",
            "reverseRepeatability": "HISTORICAL_AVAILABLE_NOT_LEVEL_ENUMERATING",
            "mappingPotential": "LOW",
            "source": "E1 causal audit plus M0-C/M0-D historical analysis",
        },
        {
            "geometry": "TRANSVERSE_PARAMEDIAN_P6",
            "safe": True,
            "t2ToT8ImageEvidence": "HISTORICAL_FULL_SWEEP",
            "adjacentSeparability": "HISTORICAL_G2_FAIL",
            "reverseRepeatability": "SUPPORTED_IMAGE_REPEATABILITY_EVENT_SEMANTICS_FAIL",
            "mappingPotential": "LOW",
            "source": str(historical_root / "TRANSVERSE_PARAMEDIAN_P6"),
        },
        {
            "geometry": "PARASAGITTAL_CENTER",
            "safe": True,
            "t2ToT8ImageEvidence": "HISTORICAL_FULL_SWEEP",
            "adjacentSeparability": "HISTORICAL_G2_FAIL",
            "reverseRepeatability": "SUPPORTED_IMAGE_REPEATABILITY_EVENT_SEMANTICS_FAIL",
            "mappingPotential": "LOW",
            "source": str(historical_root / "PARASAGITTAL_CENTER"),
        },
    ]
    for result in results:
        comparison.append({
            "geometry": result["geometry"],
            "safe": True,
            "t2ToT8ImageEvidence": f"TARGETED_REFERENCE_IMAGES_{result['repeatSplitCorrect']}_OF_7_REPEAT_NN",
            "adjacentSeparability": result["targetedReferenceSeparability"],
            "reverseRepeatability": "NOT_TESTED_TARGETED_SAME_POSE_REPEATS_ONLY",
            "mappingPotential": result["mappingPotential"],
            "source": "E2 new original-scene evaluator-guided diagnostic",
        })
    output_root = experiment_root / "evaluator_only" / "e2_geometry"
    supported = [item for item in results if item["targetedReferenceSeparability"] == "SUPPORTED"]
    selected = max(
        supported,
        key=lambda item: item["minimumAdjacentMinusMaximumRepeatDistance"],
        default=None,
    )
    result = {
        "contract": "thoracic_relative_spine_e2_geometry_analysis_v1",
        "analysisIsolation": "Evaluator-only. T-levels are used solely for diagnostic upper-bound scoring.",
        "predeclaredRepresentation": (
            "Deterministic 4x4 area pooling of 1024x1024 clean captures to 256x256, followed by per-image "
            "normalized 16x16 pooled intensity plus pooled gradient magnitude; cosine distance; "
            "repeat_a reference and independently rendered repeat_b query."
        ),
        "geometries": results,
        "comparison": comparison,
        "decision": (
            "PROCEED_TO_E4_ON_BEST_PARAMEDIAN_LONGITUDINAL_GEOMETRY"
            if supported
            else "STOP_NO_GEOMETRY_WITH_TARGETED_REFERENCE_SEPARABILITY"
        ),
        "selectedGeometry": selected["geometry"] if selected else None,
        "selectionBasis": (
            "Maximum minimum-adjacent minus maximum-same-pose descriptor-distance margin among supported geometries."
        ),
        "scientificGate": "G2_REMAINS_FAIL_PENDING_CONTINUOUS_SPATIAL_EVENT_VALIDATION",
    }
    (output_root / "geometry_analysis.json").write_text(
        json.dumps(result, indent=2) + "\n", encoding="utf-8")
    write_csv(experiment_root / "geometry_comparison.csv", comparison)
    draw_contact_sheet(experiment_root, manifest["captures"], output_root / "geometry_contact_sheet.png")
    draw_distance_matrices(results, output_root / "repeat_split_discrimination.png")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--experiment-root", type=Path, required=True)
    parser.add_argument("--historical-root", type=Path, required=True)
    args = parser.parse_args()
    result = analyze(args.experiment_root.resolve(), args.historical_root.resolve())
    print(json.dumps({
        "decision": result["decision"],
        "selectedGeometry": result["selectedGeometry"],
        "repeatSplitAccuracy": {
            item["geometry"]: item["repeatSplitAccuracy"] for item in result["geometries"]
        },
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
