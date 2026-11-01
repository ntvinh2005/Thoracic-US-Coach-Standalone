"""Offline evaluator-only analysis of G2-R1 anatomy-guided ultrasound captures."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
from typing import Any

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from anatomical_waveform_analysis import extract_morphology
from position_aware_relocalization import block_ssim
from relative_spine_e2_analysis import (
    cosine_distance,
    load_gray,
    morphology_descriptor,
    sha256,
)


LEVELS = [f"T{number}" for number in range(2, 9)]
GEOMETRIES = (
    "PARAMEDIAN_LONGITUDINAL_M6",
    "PARAMEDIAN_LONGITUDINAL_P6",
    "ANATOMY_DERIVED_LONGITUDINAL_M3",
)
SCAN_WIDTH_MM = 40.0
SOURCE_PATHS = (
    "Assets/ThoracicUSCoach/Editor/ThoracicG2R1AnatomyBatch.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicG2R1ObservabilityBatch.cs",
    "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R1Observability.cs",
    "Assets/ThoracicUSCoach/Tests/Editor/G2R1RecoveryTests.cs",
    "Tools/ThoracicUSCoach/g2_r1_anatomical_analysis.py",
    "Tools/ThoracicUSCoach/g2_r1_observability_analysis.py",
    "Tools/ThoracicUSCoach/test_g2_r1_anatomical_analysis.py",
    "Tools/ThoracicUSCoach/test_g2_r1_observability_analysis.py",
)


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def validate_capture(root: Path, capture: dict[str, Any]) -> None:
    if not all(capture.get(key) for key in
               ("contactValid", "workspaceValid", "frameIntegrityValid", "provenanceMatched")):
        raise ValueError(f"Invalid R1-B capture: {capture['geometry']} {capture['level']} {capture['repeat']}")
    image_path = root / capture["image"]
    if sha256(image_path) != capture["imageSha256"]:
        raise ValueError(f"R1-B image hash mismatch: {image_path}")
    provenance = load_json(root / capture["provenance"])
    if provenance["poseEpoch"] != capture["poseEpoch"]:
        raise ValueError("R1-B pose epoch mismatch")
    if provenance["producerScanSerial"] != capture["producerScanSerial"]:
        raise ValueError("R1-B producer serial mismatch")


def interface_provenance(path: Path, target_level: str) -> dict[str, Any]:
    provenance = load_json(path)
    beams = provenance["beams"]
    skeleton = np.asarray([
        bool(beam.get("hasShadowInterface") and beam.get("firstShadowObject") == "Skeleton")
        for beam in beams
    ])
    lf_names: dict[str, int] = {}
    any_lf = np.zeros(len(beams), dtype=bool)
    target_lf = np.zeros(len(beams), dtype=bool)
    for beam_index, beam in enumerate(beams):
        for interface in beam["interfaces"]:
            name = interface["objectName"]
            if not name.startswith("LF T") or not interface["reachedBeforeOrAtFirstShadow"]:
                continue
            any_lf[beam_index] = True
            lf_names[name] = lf_names.get(name, 0) + 1
            if name == "LF " + target_level:
                target_lf[beam_index] = True
    depths = np.asarray([
        beam["firstShadowDepthFraction"]
        for beam in beams
        if beam.get("hasShadowInterface") and beam.get("firstShadowObject") == "Skeleton"
    ], dtype=np.float64)
    transitions = int(np.count_nonzero(skeleton[1:] != skeleton[:-1]))
    target_indices = np.flatnonzero(target_lf)
    any_indices = np.flatnonzero(any_lf)
    bone_on_both_sides = False
    target_span_mm = 0.0
    if len(target_indices):
        lower, upper = int(target_indices.min()), int(target_indices.max())
        bone_on_both_sides = bool(np.any(skeleton[:lower]) and np.any(skeleton[upper + 1:]))
        target_span_mm = (upper - lower + 1) * SCAN_WIDTH_MM / len(beams)
    any_span_mm = 0.0
    if len(any_indices):
        any_span_mm = (int(any_indices.max()) - int(any_indices.min()) + 1) * SCAN_WIDTH_MM / len(beams)
    return {
        "beamCount": len(beams),
        "skeletonShadowBeamFraction": float(np.mean(skeleton)),
        "skeletonShadowTransitionCount": transitions,
        "medianSkeletonShadowDepth": float(np.median(depths)) if len(depths) else None,
        "minimumSkeletonShadowDepth": float(np.min(depths)) if len(depths) else None,
        "anyLfVisibleBeamFraction": float(np.mean(any_lf)),
        "targetLfVisibleBeamFraction": float(np.mean(target_lf)),
        "targetLfVisibleBeamCount": int(np.count_nonzero(target_lf)),
        "targetLfSpanMm": float(target_span_mm),
        "anyLfSpanMm": float(any_span_mm),
        "boneShadowOnBothSidesOfTargetLf": bone_on_both_sides,
        "visibleLfObjects": lf_names,
        "semanticClaim": (
            "Ordered ray interfaces prove intersection before or at first shadow; they do not by themselves "
            "quantify the LF pixel amplitude."
        ),
    }


def image_features(path: Path) -> dict[str, float | bool]:
    image = load_gray(path)
    gy, gx = np.gradient(image)
    morphology, _ = extract_morphology(path)
    return {
        "meanIntensity": float(np.mean(image)),
        "intensityStd": float(np.std(image)),
        "gradientEnergy": float(np.mean(np.hypot(gx, gy))),
        "shadowValid": bool(morphology["shadow_valid"]),
        "shadowWidth": float(morphology["shadow_width"]),
        "shadowCentroidOffset": float(morphology["shadow_centroid_offset"]),
        "shadowTaper": float(morphology["shadow_taper"]),
    }


def analyze_geometry(root: Path, captures: list[dict[str, Any]], geometry: str) -> dict[str, Any]:
    lookup = {(item["level"], item["repeat"]): item for item in captures if item["geometry"] == geometry}
    expected = {(level, repeat) for level in LEVELS for repeat in ("repeat_a", "repeat_b")}
    if set(lookup) != expected:
        raise ValueError(f"Incomplete R1-B capture set for {geometry}")
    images: dict[tuple[str, str], np.ndarray] = {}
    descriptors: dict[tuple[str, str], np.ndarray] = {}
    features: dict[tuple[str, str], dict[str, Any]] = {}
    provenance: dict[tuple[str, str], dict[str, Any]] = {}
    for key, capture in lookup.items():
        validate_capture(root, capture)
        image_path = root / capture["image"]
        images[key] = load_gray(image_path)
        descriptors[key] = morphology_descriptor(images[key])
        features[key] = image_features(image_path)
        provenance[key] = interface_provenance(root / capture["provenance"], capture["level"])
    repeat_rows = []
    for level in LEVELS:
        repeat_rows.append({
            "level": level,
            "ssim": float(block_ssim(images[(level, "repeat_a")], images[(level, "repeat_b")])),
            "descriptorDistance": cosine_distance(
                descriptors[(level, "repeat_a")], descriptors[(level, "repeat_b")]
            ),
            "shadowWidthDifference": abs(
                float(features[(level, "repeat_a")]["shadowWidth"])
                - float(features[(level, "repeat_b")]["shadowWidth"])
            ),
        })
    adjacent_rows = []
    for left, right in zip(LEVELS[:-1], LEVELS[1:]):
        adjacent_rows.append({
            "before": left,
            "after": right,
            "ssim": float(block_ssim(images[(left, "repeat_a")], images[(right, "repeat_a")])),
            "descriptorDistance": cosine_distance(
                descriptors[(left, "repeat_a")], descriptors[(right, "repeat_a")]
            ),
        })
    predictions = []
    for level in LEVELS:
        distances = sorted(
            ((
                candidate,
                cosine_distance(descriptors[(level, "repeat_b")], descriptors[(candidate, "repeat_a")]),
            ) for candidate in LEVELS),
            key=lambda item: item[1],
        )
        predictions.append({
            "queryLevel": level,
            "predictedLevel": distances[0][0],
            "correct": distances[0][0] == level,
            "bestDistance": distances[0][1],
            "secondBestDistance": distances[1][1],
            "margin": distances[1][1] - distances[0][1],
        })
    per_level = []
    for level in LEVELS:
        capture = lookup[(level, "repeat_a")]
        per_level.append({
            "level": level,
            "lfCenterZMm": float(capture["lfBoundsCenter"]["z"]),
            "image": capture["image"],
            "repeatImage": lookup[(level, "repeat_b")]["image"],
            **features[(level, "repeat_a")],
            **provenance[(level, "repeat_a")],
        })
    within = np.asarray([row["descriptorDistance"] for row in repeat_rows])
    adjacent = np.asarray([row["descriptorDistance"] for row in adjacent_rows])
    exact_lf_count = sum(item["targetLfVisibleBeamCount"] > 0 for item in per_level)
    both_sides_count = sum(item["boneShadowOnBothSidesOfTargetLf"] for item in per_level)
    correct = sum(item["correct"] for item in predictions)
    return {
        "geometry": geometry,
        "captureCount": len(lookup),
        "validCaptureCount": len(lookup),
        "samePoseRepeatability": repeat_rows,
        "adjacentRegionDifferences": adjacent_rows,
        "repeatSplitNearestRegion": predictions,
        "repeatSplitCorrect": correct,
        "repeatSplitAccuracy": correct / 7.0,
        "maximumSamePoseDescriptorDistance": float(np.max(within)),
        "minimumAdjacentDescriptorDistance": float(np.min(adjacent)),
        "minimumAdjacentMinusMaximumRepeatDistance": float(np.min(adjacent) - np.max(within)),
        "targetLfReachedBeforeShadowCount": exact_lf_count,
        "boneShadowOnBothSidesOfTargetLfCount": both_sides_count,
        "perLevel": per_level,
        "recurringLfAssociatedTransitionEvidence": (
            "SUPPORTED_AT_ALL_SEVEN_DIAGNOSTIC_REGIONS"
            if exact_lf_count == 7 and both_sides_count >= 5
            else "PARTIAL"
        ),
        "limitation": (
            "This is evaluator-guided diagnostic placement. Repeat-split spatial discrimination and ordered-interface "
            "provenance do not establish an actor-side continuous-sweep event detector."
        ),
    }


def write_signal_csv(path: Path, results: list[dict[str, Any]]) -> None:
    rows: list[dict[str, Any]] = []
    for result in results:
        repeat_lookup = {item["level"]: item for item in result["samePoseRepeatability"]}
        for item in result["perLevel"]:
            rows.append({
                "geometry": result["geometry"],
                "level": item["level"],
                "lf_center_z_mm": item["lfCenterZMm"],
                "same_pose_ssim": repeat_lookup[item["level"]]["ssim"],
                "same_pose_descriptor_distance": repeat_lookup[item["level"]]["descriptorDistance"],
                "mean_intensity": item["meanIntensity"],
                "intensity_std": item["intensityStd"],
                "gradient_energy": item["gradientEnergy"],
                "shadow_width": item["shadowWidth"],
                "skeleton_shadow_fraction": item["skeletonShadowBeamFraction"],
                "skeleton_shadow_transitions": item["skeletonShadowTransitionCount"],
                "target_lf_visible_beams": item["targetLfVisibleBeamCount"],
                "target_lf_span_mm": item["targetLfSpanMm"],
                "bone_shadow_both_sides": item["boneShadowOnBothSidesOfTargetLf"],
            })
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def draw_contact_sheet(root: Path, captures: list[dict[str, Any]], output: Path) -> None:
    lookup = {(item["geometry"], item["level"], item["repeat"]): item for item in captures}
    figure, axes = plt.subplots(6, 7, figsize=(18, 15), squeeze=False)
    for column, level in enumerate(LEVELS):
        for row, (geometry, repeat) in enumerate(
            (pair for geometry in GEOMETRIES for pair in ((geometry, "repeat_a"), (geometry, "repeat_b")))
        ):
            image = load_gray(root / lookup[(geometry, level, repeat)]["image"])
            axes[row, column].imshow(image, cmap="gray", vmin=0, vmax=1)
            axes[row, column].axis("off")
            if row == 0:
                axes[row, column].set_title(level)
            if column == 0:
                axes[row, column].set_ylabel(geometry.split("_")[-1] + " " + repeat[-1])
    figure.suptitle("G2-R1 original-scene LF-associated posterior recess captures (evaluator-guided)")
    figure.tight_layout()
    figure.savefig(output, dpi=170)
    plt.close(figure)


def draw_waveform(results: list[dict[str, Any]], output: Path) -> None:
    figure, axes = plt.subplots(4, 1, figsize=(14, 13), sharex=True, constrained_layout=True)
    for result in results:
        rows = sorted(result["perLevel"], key=lambda item: item["lfCenterZMm"])
        z = np.asarray([item["lfCenterZMm"] for item in rows])
        label = result["geometry"]
        axes[0].plot(z, [item["skeletonShadowBeamFraction"] for item in rows], marker="o", label=label)
        axes[1].plot(z, [item["targetLfVisibleBeamFraction"] for item in rows], marker="o", label=label)
        axes[2].plot(z, [item["shadowWidth"] for item in rows], marker="o", label=label)
        axes[3].plot(z, [item["gradientEnergy"] for item in rows], marker="o", label=label)
    axes[0].set_ylabel("Skeleton shadow fraction")
    axes[1].set_ylabel("Target LF before-shadow fraction")
    axes[2].set_ylabel("Image shadow width")
    axes[3].set_ylabel("Gradient energy")
    axes[3].set_xlabel("LF-associated recess world z (mm; evaluator-only diagnostic axis)")
    for axis in axes:
        axis.grid(alpha=0.25)
        axis.legend(fontsize=7)
    figure.suptitle("G2-R1 anatomy-guided renderer and image features")
    figure.savefig(output, dpi=180)
    plt.close(figure)


def run(root: Path, historical_root: Path) -> dict[str, Any]:
    manifest_path = root / "evaluator_only" / "r1b_observability" / "capture_manifest.json"
    manifest = load_json(manifest_path)
    results = [analyze_geometry(root, manifest["captures"], geometry) for geometry in GEOMETRIES]
    best = max(
        results,
        key=lambda item: (
            item["targetLfReachedBeforeShadowCount"],
            item["boneShadowOnBothSidesOfTargetLfCount"],
            item["repeatSplitCorrect"],
            item["minimumAdjacentMinusMaximumRepeatDistance"],
        ),
    )
    renderer = {
        "contract": "g2_r1_renderer_contribution_audit_v1",
        "actorUse": "PROHIBITED_EVALUATOR_ONLY",
        "geometries": results,
        "bestDiagnosticGeometry": best["geometry"],
        "interpretation": (
            "Full ordered interfaces show the combined Skeleton Shadow and level-named LF Normal interfaces in "
            "the same longitudinal scans. Pixel identity remains combined-Skeleton unresolved; no per-level bone "
            "mesh identity is introduced."
        ),
    }
    (root / "renderer_contribution_audit.json").write_text(
        json.dumps(renderer, indent=2) + "\n", encoding="utf-8"
    )
    write_signal_csv(root / "bone_gap_signal_analysis.csv", results)
    draw_contact_sheet(root, manifest["captures"], root / "bone_gap_contact_sheet.png")
    draw_waveform(results, root / "bone_gap_waveform.png")
    historical_report = historical_root / "evaluator_only" / "e4_separability" / "image_separability_report.json"
    historical = load_json(historical_report) if historical_report.exists() else None
    controls = {
        "contract": "g2_r1_repeatability_negative_controls_v1",
        "samePoseRepeatability": {
            item["geometry"]: item["samePoseRepeatability"] for item in results
        },
        "adjacentRegionDifferences": {
            item["geometry"]: item["adjacentRegionDifferences"] for item in results
        },
        "wrongRegionRepeatSplit": {
            item["geometry"]: item["repeatSplitNearestRegion"] for item in results
        },
        "historicalLabelCenterArithmeticMidpointControl": (
            {
                "conclusion": historical.get("anatomicalTopologyConclusion"),
                "decision": historical.get("e4Decision"),
                "centerInterlevelAccuracy": historical.get("centerInterlevelAccuracy"),
                "permutationPValue": historical.get("permutationControl", {}).get("pValue"),
            }
            if historical else "NOT_AVAILABLE"
        ),
        "scientificLimitation": (
            "Correct repeat-region matching is position specificity, not proof that a shared image-only event "
            "detector can locate the recesses in a continuous sweep."
        ),
    }
    (root / "repeatability_and_negative_controls.json").write_text(
        json.dumps(controls, indent=2) + "\n", encoding="utf-8"
    )
    acquisition = {
        "contract": manifest["contract"],
        "status": manifest["status"],
        "captureCount": len(manifest["captures"]),
        "allContactValid": all(item["contactValid"] for item in manifest["captures"]),
        "allWorkspaceValid": all(item["workspaceValid"] for item in manifest["captures"]),
        "allFrameIntegrityValid": all(item["frameIntegrityValid"] for item in manifest["captures"]),
        "allProvenanceMatched": all(item["provenanceMatched"] for item in manifest["captures"]),
        "uniqueImageHashCount": len({item["imageSha256"] for item in manifest["captures"]}),
        "sourceManifest": "evaluator_only/r1b_observability/capture_manifest.json",
        "sourceManifestSha256": sha256(manifest_path),
        "geometries": list(GEOMETRIES),
    }
    (root / "anatomical_acquisition_manifest.json").write_text(
        json.dumps(acquisition, indent=2) + "\n", encoding="utf-8"
    )
    repo_root = Path.cwd().resolve()
    hash_manifest_path = root / "source_configuration_hash_manifest.json"
    artifact_files = sorted(
        path for path in root.rglob("*")
        if path.is_file() and path != hash_manifest_path
    )
    hash_manifest = {
        "contract": "g2_r1_source_configuration_hash_manifest_v1",
        "sources": [
            {"path": path, "sha256": sha256(repo_root / path)} for path in SOURCE_PATHS
        ],
        "artifacts": [
            {"path": path.relative_to(root).as_posix(), "sha256": sha256(path)}
            for path in artifact_files
        ],
    }
    hash_manifest_path.write_text(json.dumps(hash_manifest, indent=2) + "\n", encoding="utf-8")
    summary = {
        "bestDiagnosticGeometry": best["geometry"],
        "captureCount": len(manifest["captures"]),
        "allIntegrityChecksPass": all(
            acquisition[key] for key in (
                "allContactValid", "allWorkspaceValid", "allFrameIntegrityValid", "allProvenanceMatched"
            )
        ),
        "results": [
            {
                "geometry": item["geometry"],
                "repeatSplitCorrect": item["repeatSplitCorrect"],
                "targetLfReachedBeforeShadowCount": item["targetLfReachedBeforeShadowCount"],
                "boneShadowOnBothSidesCount": item["boneShadowOnBothSidesOfTargetLfCount"],
                "minimumAdjacentMinusMaximumRepeatDistance": item[
                    "minimumAdjacentMinusMaximumRepeatDistance"
                ],
                "recurringEvidence": item["recurringLfAssociatedTransitionEvidence"],
            }
            for item in results
        ],
    }
    return summary


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--historical-root", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(run(args.root.resolve(), args.historical_root.resolve()), indent=2))


if __name__ == "__main__":
    main()
