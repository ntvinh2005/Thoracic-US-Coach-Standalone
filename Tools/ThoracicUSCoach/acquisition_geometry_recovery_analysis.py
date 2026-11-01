"""Evaluator-only comparison for the frozen G2 acquisition-geometry runs."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Iterable, Sequence

import matplotlib.pyplot as plt
from PIL import Image

from autonomous_t7_sequence_analysis import verify_freeze


PRIMARY_PHASES = {"baseline", "caudal_outbound", "cranial_outbound"}
LOCAL_WINDOW_MM = 6.0
MIN_DIAGNOSTIC_LF_FRACTION = 0.05
MIN_DIAGNOSTIC_LF_FRAMES = 2


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def select_nearest_primary(actor_rows: Sequence[dict], evaluator_rows: Sequence[dict],
                           coordinate_mm: float) -> tuple[dict, dict]:
    evaluator_by_index = {int(row["observationIndex"]): row for row in evaluator_rows}
    candidates = []
    for actor_row in actor_rows:
        if actor_row["phase"] not in PRIMARY_PHASES:
            continue
        evaluator_row = evaluator_by_index[int(actor_row["observationIndex"])]
        distance = abs(float(evaluator_row["evaluatorLongitudinalCoordinateMm"]) - coordinate_mm)
        candidates.append((distance, int(actor_row["observationIndex"]), actor_row, evaluator_row))
    if not candidates:
        raise ValueError("No primary traversal observation is available")
    _, _, actor_row, evaluator_row = min(candidates, key=lambda item: (item[0], item[1]))
    return actor_row, evaluator_row


def provenance_metrics(beams: Iterable[dict], expected_object: str) -> dict:
    rows = list(beams)
    if not rows:
        raise ValueError("Provenance contains no beams")
    visible = Counter(str(row.get("firstVisibleObject") or "NONE") for row in rows)
    shadow = Counter(str(row.get("firstShadowObject") or "NONE") for row in rows)
    beam_count = len(rows)
    return {
        "beamCount": beam_count,
        "expectedObject": expected_object,
        "expectedFirstVisibleBeamCount": visible[expected_object],
        "expectedFirstVisibleBeamFraction": visible[expected_object] / beam_count,
        "combinedSkeletonFirstVisibleFraction": visible["Skeleton"] / beam_count,
        "combinedSkeletonShadowFraction": shadow["Skeleton"] / beam_count,
        "topFirstVisibleObjects": [
            {"object": name, "beamCount": count, "beamFraction": count / beam_count}
            for name, count in visible.most_common(5)
        ],
    }


def diagnostic_provenance_conclusion(max_fraction: float, supporting_frames: int) -> str:
    if max_fraction >= MIN_DIAGNOSTIC_LF_FRACTION and supporting_frames >= MIN_DIAGNOSTIC_LF_FRAMES:
        return "SUPPORTED_PROVENANCE_INTERSECTION_ONLY"
    if max_fraction > 0.0:
        return "WEAK_OR_SPARSE_PROVENANCE_INTERSECTION"
    return "NOT_OBSERVED_IN_PROVENANCE"


def analyze_geometry(name: str, run_root: Path, offline_root: Path, references: Sequence[dict]) -> tuple[dict, list[dict]]:
    verify_freeze(offline_root)
    actor = load_json(run_root / "trajectory_actor.json")
    evaluator = load_json(run_root / "evaluator_only" / "trajectory_privileged.json")
    summary = load_json(run_root / "run_summary.json")
    sequence = load_json(offline_root / "blind_sequence_candidates.json")
    segmentation = load_json(offline_root / "segmentation_model_selection.json")
    evaluator_by_index = {int(row["observationIndex"]): row for row in evaluator["observations"]}
    primary_actor_rows = [row for row in actor["observations"] if row["phase"] in PRIMARY_PHASES]

    rows = []
    for reference in references:
        label = str(reference["label"])
        reference_coordinate = float(reference["coordinateMm"])
        expected_object = f"LF {label}"
        actor_row, evaluator_row = select_nearest_primary(
            actor["observations"], evaluator["observations"], reference_coordinate)
        provenance = load_json(run_root / str(evaluator_row["provenance"]))
        representative = provenance_metrics(provenance["beams"], expected_object)

        window_metrics = []
        for candidate in primary_actor_rows:
            candidate_evaluator = evaluator_by_index[int(candidate["observationIndex"])]
            candidate_coordinate = float(candidate_evaluator["evaluatorLongitudinalCoordinateMm"])
            if abs(candidate_coordinate - reference_coordinate) > LOCAL_WINDOW_MM:
                continue
            candidate_provenance = load_json(run_root / str(candidate_evaluator["provenance"]))
            candidate_metrics = provenance_metrics(candidate_provenance["beams"], expected_object)
            window_metrics.append(candidate_metrics)
        max_fraction = max((item["expectedFirstVisibleBeamFraction"] for item in window_metrics), default=0.0)
        supporting_frames = sum(item["expectedFirstVisibleBeamCount"] > 0 for item in window_metrics)
        rows.append({
            "geometry": name,
            "referenceLevel": label,
            "referenceCoordinateMm": reference_coordinate,
            "representativeObservationIndex": int(actor_row["observationIndex"]),
            "representativeEvaluatorCoordinateMm": float(evaluator_row["evaluatorLongitudinalCoordinateMm"]),
            "representativeCoordinateErrorMm": abs(
                float(evaluator_row["evaluatorLongitudinalCoordinateMm"]) - reference_coordinate),
            "representativeImage": str(run_root / str(actor_row["image"])),
            "expectedEvaluatorObject": expected_object,
            "representativeExpectedLfBeamFraction": representative["expectedFirstVisibleBeamFraction"],
            "representativeCombinedSkeletonFirstVisibleFraction": representative["combinedSkeletonFirstVisibleFraction"],
            "representativeCombinedSkeletonShadowFraction": representative["combinedSkeletonShadowFraction"],
            "localWindowMm": LOCAL_WINDOW_MM,
            "localWindowFrameCount": len(window_metrics),
            "localWindowFramesWithExpectedLf": supporting_frames,
            "localWindowMaximumExpectedLfBeamFraction": max_fraction,
            "evaluatorProvenanceConclusion": diagnostic_provenance_conclusion(max_fraction, supporting_frames),
            "actorEventConclusion": "NOT_RECOGNIZED",
            "identityLimitation": "Combined Skeleton remains unresolved; LF object names are evaluator-only diagnostic provenance.",
            "topFirstVisibleObjects": representative["topFirstVisibleObjects"],
        })

    gate = sequence["gate"]
    geometry_summary = {
        "geometry": name,
        "physicalSampling": summary["physicalSampling"],
        "imageIntegrity": summary["imageIntegrity"],
        "acceptedMovements": summary["acceptedMovements"],
        "rejectedMovements": summary["rejectedMovements"],
        "validFrames": summary["validFrames"],
        "minimumEvaluatorCoordinateMm": summary["minimumEvaluatorCoordinateMm"],
        "maximumEvaluatorCoordinateMm": summary["maximumEvaluatorCoordinateMm"],
        "maximumYawErrorDeg": summary["maximumYawErrorDeg"],
        "maximumNormalAlignmentErrorDeg": summary["maximumNormalAlignmentErrorDeg"],
        "minimumScanWidthAgainstExpectedAxis": summary.get("minimumScanWidthAgainstExpectedAxis"),
        "blindFreezeVerified": True,
        "blindStatus": sequence["status"],
        "acceptedEventCount": sequence["acceptedEventCount"],
        "bestBicK": segmentation["modelSelection"]["bestBicK"],
        "k7SmoothTrendNullPValue": segmentation["smoothTrendNull"]["pValue"],
        "reverseTraversalAccuracy": segmentation["reverseTraversal"]["labelAccuracy"],
        "maximumFeatureAblationBoundaryShiftMm": segmentation["familyAblation"]["maximumBoundaryDifferenceMm"],
        "failedBlindChecks": [key for key, passed in gate["checks"].items() if not passed],
        "levelsWithSupportedProvenanceIntersection": sum(
            row["evaluatorProvenanceConclusion"] == "SUPPORTED_PROVENANCE_INTERSECTION_ONLY" for row in rows),
        "g2Verdict": "FAIL",
    }
    return geometry_summary, rows


def draw_contact_sheet(rows: Sequence[dict], output_path: Path) -> None:
    geometries = list(dict.fromkeys(row["geometry"] for row in rows))
    levels = [f"T{index}" for index in range(2, 9)]
    fig, axes = plt.subplots(len(geometries), len(levels), figsize=(18, 3.2 * len(geometries)), squeeze=False)
    lookup = {(row["geometry"], row["referenceLevel"]): row for row in rows}
    for row_index, geometry in enumerate(geometries):
        for column_index, level in enumerate(levels):
            ax = axes[row_index][column_index]
            item = lookup[(geometry, level)]
            with Image.open(item["representativeImage"]) as image:
                ax.imshow(image.convert("L"), cmap="gray", vmin=0, vmax=255)
            ax.set_title(
                f"{level}  x={item['representativeEvaluatorCoordinateMm']:.1f} mm\n"
                f"LF={100.0 * item['localWindowMaximumExpectedLfBeamFraction']:.1f}%  "
                f"SkSh={100.0 * item['representativeCombinedSkeletonShadowFraction']:.1f}%",
                fontsize=8,
            )
            if column_index == 0:
                ax.text(-0.08, 0.5, geometry, rotation=90, va="center", ha="right",
                        transform=ax.transAxes, fontsize=9, fontweight="bold")
            ax.axis("off")
    fig.suptitle("Fresh ultrasound near T2-T8 references (labels/provenance revealed evaluator-only after blind freeze)")
    fig.tight_layout(rect=(0, 0, 1, 0.96))
    fig.savefig(output_path, dpi=160)
    plt.close(fig)


def save_csv(path: Path, rows: Sequence[dict]) -> None:
    scalar_rows = [{key: value for key, value in row.items() if key != "topFirstVisibleObjects"} for row in rows]
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(scalar_rows[0]))
        writer.writeheader()
        writer.writerows(scalar_rows)


def main() -> None:
    parser = argparse.ArgumentParser(description="Evaluator-only G2 acquisition-geometry comparison")
    parser.add_argument("--experiment-root", type=Path, required=True)
    parser.add_argument("--m0c-root", type=Path, required=True)
    parser.add_argument("--baseline-run-root", type=Path, required=True)
    parser.add_argument("--baseline-offline-root", type=Path, required=True)
    args = parser.parse_args()
    experiment_root = args.experiment_root.resolve()
    references = sorted(
        load_json(args.m0c_root.resolve() / "evaluator_only" / "anatomical_comparison.json")["references"],
        key=lambda item: float(item["coordinateMm"]),
    )
    summaries = []
    per_level = []
    geometry_inputs = (
        ("TRANSVERSE_CENTER_BASELINE", args.baseline_run_root.resolve(), args.baseline_offline_root.resolve()),
        ("TRANSVERSE_PARAMEDIAN_P6", experiment_root / "TRANSVERSE_PARAMEDIAN_P6",
         experiment_root / "TRANSVERSE_PARAMEDIAN_P6" / "offline_analysis"),
        ("PARASAGITTAL_CENTER", experiment_root / "PARASAGITTAL_CENTER",
         experiment_root / "PARASAGITTAL_CENTER" / "offline_analysis"),
    )
    for geometry, run_root, offline_root in geometry_inputs:
        summary, rows = analyze_geometry(
            geometry,
            run_root,
            offline_root,
            references,
        )
        summaries.append(summary)
        per_level.extend(rows)

    output = {
        "contract": "thoracic_g2_acquisition_geometry_evaluator_v1",
        "analysisIsolation": "Evaluator-only analysis executed after both blind outputs were hash-frozen.",
        "thresholds": {
            "localWindowMm": LOCAL_WINDOW_MM,
            "minimumDiagnosticLfBeamFraction": MIN_DIAGNOSTIC_LF_FRACTION,
            "minimumDiagnosticLfFrames": MIN_DIAGNOSTIC_LF_FRAMES,
        },
        "geometries": summaries,
        "gates": {
            "G0_anatomical_reference_validity": "PASS",
            "G1_physical_coverage": "PASS",
            "G2_anatomical_event_observability": "FAIL",
            "G3_enumeration_accuracy": "NOT_RUN",
            "G4_T7_inference_and_reacquisition": "NOT_RUN",
        },
        "physicalDevelopmentRunsConsumed": 2,
        "confirmationEpisodesConsumed": 0,
        "stopReason": "All safe authorized acquisition geometries failed the frozen seven-level observability gate.",
    }
    (experiment_root / "geometry_comparison.json").write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    (experiment_root / "per_level_anatomical_correspondence.json").write_text(
        json.dumps(per_level, indent=2) + "\n", encoding="utf-8")
    save_csv(experiment_root / "per_level_anatomical_correspondence.csv", per_level)
    draw_contact_sheet(per_level, experiment_root / "geometry_per_level_contact_sheet.png")
    evidence_files = [
        experiment_root / "geometry_comparison.json",
        experiment_root / "per_level_anatomical_correspondence.json",
        experiment_root / "per_level_anatomical_correspondence.csv",
        experiment_root / "geometry_per_level_contact_sheet.png",
    ]
    (experiment_root / "evaluator_outputs.sha256").write_text(
        "".join(f"{sha256(path)}  {path.name}\n" for path in evidence_files), encoding="utf-8")
    print(json.dumps(output, indent=2))


if __name__ == "__main__":
    main()
