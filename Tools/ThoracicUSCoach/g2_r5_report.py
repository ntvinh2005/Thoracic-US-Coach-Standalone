"""Generate actor-visible G2-R5 diagnostics and isolated evaluator scoring."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt
import numpy as np

from g2_r4_multi_hypothesis_tracker import track_multiple_interfaces
from g2_r5_targeted_multiview import extract_targeted_detections, read_json, write_json
from relative_spine_e2_analysis import load_gray


VARIANTS = ("YAW90_DEPTH50", "YAW91_DEPTH50", "YAW90_DEPTH60")


def ordered_match(
    predictions: list[float], references: list[dict[str, Any]], tolerance_mm: float
) -> list[dict[str, Any]]:
    """Maximum-cardinality, minimum-error order-preserving evaluator match."""
    count_p, count_r = len(predictions), len(references)
    table: list[list[tuple[int, float, list[tuple[int, int]]]]] = [
        [(0, 0.0, []) for _ in range(count_r + 1)] for _ in range(count_p + 1)
    ]
    for i in range(count_p + 1):
        for j in range(count_r + 1):
            if i < count_p:
                candidate = table[i][j]
                if (candidate[0], -candidate[1]) > (table[i + 1][j][0], -table[i + 1][j][1]):
                    table[i + 1][j] = candidate
            if j < count_r:
                candidate = table[i][j]
                if (candidate[0], -candidate[1]) > (table[i][j + 1][0], -table[i][j + 1][1]):
                    table[i][j + 1] = candidate
            if i < count_p and j < count_r:
                error = abs(predictions[i] - float(references[j]["coordinateMm"]))
                if error <= tolerance_mm:
                    previous = table[i][j]
                    candidate = (previous[0] + 1, previous[1] + error,
                                 previous[2] + [(i, j)])
                    current = table[i + 1][j + 1]
                    if (candidate[0], -candidate[1]) > (current[0], -current[1]):
                        table[i + 1][j + 1] = candidate
    pairs = table[count_p][count_r][2]
    pair_by_prediction = {left: right for left, right in pairs}
    rows = []
    for index, coordinate in enumerate(predictions):
        reference_index = pair_by_prediction.get(index)
        reference = references[reference_index] if reference_index is not None else None
        signed = coordinate - float(reference["coordinateMm"]) if reference else None
        rows.append({
            "predictedCoordinateMm": coordinate,
            "matchedLevel": reference["level"] if reference else None,
            "referenceCoordinateMm": float(reference["coordinateMm"]) if reference else None,
            "signedErrorMm": signed,
            "absoluteErrorMm": abs(signed) if signed is not None else None,
            "matched": reference is not None,
        })
    return rows


def _representative(
    detections: list[dict[str, Any]], region_id: str, variant_id: str, center: float
) -> dict[str, Any] | None:
    candidates = [item for item in detections
                  if item["regionId"] == region_id and item["variantId"] == variant_id]
    if not candidates:
        return None
    return min(candidates, key=lambda item: (
        abs(float(item["projectedCoordinateMm"]) - center),
        -float(item["prominencePixels"]),
    ))


def make_multiview_plot(
    session_root: Path, actor: dict[str, Any], fusion: dict[str, Any], output: Path
) -> None:
    detections = extract_targeted_detections(session_root, actor)
    observations = {item["observationIndex"]: item for item in actor["observations"]}
    rows = fusion["regions"]
    figure, axes = plt.subplots(len(rows), len(VARIANTS), figsize=(14, 12))
    for row_index, region in enumerate(rows):
        for column_index, variant in enumerate(VARIANTS):
            axis = axes[row_index, column_index]
            detection = _representative(
                detections, region["regionId"], variant, region["updatedCenterMm"]
            )
            if detection is None:
                axis.axis("off")
                axis.text(0.5, 0.5, "not acquired", ha="center", va="center")
                continue
            observation = observations[detection["observationIndex"]]
            image = load_gray(session_root / observation["image"])
            tracks = {item["trackId"]: item for item in track_multiple_interfaces(image)}
            track = tracks.get(detection["trackId"])
            axis.imshow(image, cmap="gray", vmin=0.0, vmax=1.0)
            if track:
                axis.plot(track["x"], track["pathY"], color="#00d4ff", linewidth=1.2)
            axis.scatter([detection["pixelX"]], [detection["depthPixelY"]],
                         s=35, color="#ff3b30", marker="x")
            axis.set_title(
                f"{region['regionId']} {variant}\n"
                f"s={observation['signedActorRelativeMm']:.2f} mm, "
                f"{observation['direction']}"
            )
            axis.set_xlim(0, image.shape[1] - 1)
            axis.set_ylim(image.shape[0] - 1, 0)
            axis.set_xticks([])
            axis.set_yticks([])
        axes[row_index, 0].set_ylabel(region["classification"], fontsize=9)
    figure.suptitle(
        "G2-R5 actor-visible multi-view evidence\ncyan: contour hypothesis; red: projected valley",
        fontsize=14,
    )
    figure.tight_layout(rect=(0, 0, 1, 0.95))
    output.parent.mkdir(parents=True, exist_ok=True)
    figure.savefig(output, dpi=170)
    plt.close(figure)


def generate_actor_reports(root: Path) -> None:
    session = root / "session_01"
    actor = read_json(session / "trajectory_actor.json")
    protocol = read_json(root / "actor_selected_revisit_windows.json")
    fusion = read_json(root / "event_morphology_before_after.json")
    make_multiview_plot(session, actor, fusion, root / "multi_view_comparison.png")

    protocol_by_id = {item["regionId"]: item for item in protocol["regions"]}
    with (root / "candidate_support_before_after.csv").open(
        "w", newline="", encoding="utf-8"
    ) as handle:
        fields = [
            "region_id", "category", "original_center_mm", "updated_center_mm",
            "original_frames", "new_independent_support", "new_positions",
            "new_directions", "new_variants", "new_shadow_fraction", "classification",
        ]
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for result in fusion["regions"]:
            before = protocol_by_id[result["regionId"]]
            writer.writerow({
                "region_id": result["regionId"],
                "category": before["category"],
                "original_center_mm": result["originalCenterMm"],
                "updated_center_mm": result["updatedCenterMm"],
                "original_frames": before["distinctFrameCount"],
                "new_independent_support": result["newIndependentSupportCount"],
                "new_positions": result["newPhysicalPositionCount"],
                "new_directions": "|".join(result["newDirections"]),
                "new_variants": "|".join(result["newVariants"]),
                "new_shadow_fraction": result["newActualShadowSupportFraction"],
                "classification": result["classification"],
            })
    write_json(root / "event_classification_decisions.json", {
        "contract": "thoracic_g2_r5_actor_event_decisions_v1",
        "evaluatorInputsUsed": False,
        "regions": fusion["regions"],
    })
    relative_map = read_json(root / "relative_spine_map.json")
    write_json(root / "mapping_uncertainty.json", {
        "contract": "thoracic_g2_r5_actor_mapping_uncertainty_v1",
        "supportIntervalsAreNotCalibratedConfidenceIntervals": True,
        "acceptedEventCount": relative_map["acceptedEventCount"],
        "ambiguousEventCount": relative_map["ambiguousEventCount"],
        "consequentialAmbiguousCoordinatesMm":
            relative_map["consequentialAmbiguousCoordinatesMm"],
        "overallMapStatus": relative_map["overallMapStatus"],
        "predictedT7RelativeMm": relative_map["predictedT7RelativeMm"],
        "abstentionReason": relative_map["abstentionReason"],
    })


def generate_evaluator_reports(root: Path, references_path: Path) -> None:
    fusion = read_json(root / "event_morphology_before_after.json")
    relative_map = read_json(root / "relative_spine_map.json")
    references = read_json(references_path)["references"]
    tolerance = 5.0
    accepted = sorted(
        [float(item["centerRelativeMm"]) for item in relative_map["levels"]
         if item["accepted"]]
    )
    if not accepted:
        # An inconclusive map intentionally leaves ordinal slots null. Reconstruct the six
        # actor-accepted coordinates from the frozen R4 accepted set and R5 supported fusion.
        r4 = read_json(root.parent /
            "G2_R4_MULTI_HYPOTHESIS_REACQUISITION_20261101_R01/mapping_uncertainty.json")
        accepted = [float(item["centerMm"]) for item in r4["accepted"]]
        accepted.extend(float(item["updatedCenterMm"]) for item in fusion["regions"]
                        if item["classification"] == "SUPPORTED_RECESS_EVENT")
        accepted.sort()
    matches = ordered_match(accepted, references, tolerance)
    true_positive = sum(item["matched"] for item in matches)
    false_positive = len(matches) - true_positive
    false_negative = len(references) - true_positive
    precision = true_positive / max(true_positive + false_positive, 1)
    recall = true_positive / len(references)
    f1 = 2.0 * precision * recall / max(precision + recall, 1e-12)
    region_evaluation = []
    for region in fusion["regions"]:
        rows = ordered_match([float(region["updatedCenterMm"])], references, tolerance)
        region_evaluation.append({"regionId": region["regionId"],
                                  "actorClassification": region["classification"],
                                  **rows[0]})
    evaluator_root = root / "evaluator_only"
    evaluator_root.mkdir(parents=True, exist_ok=True)
    write_json(evaluator_root / "anatomical_correspondence.json", {
        "contract": "thoracic_g2_r5_post_freeze_evaluator_v1",
        "blindFreezeVerifiedBeforeEvaluation": True,
        "referenceSemantics": "Validated LF-associated posterior bone recess regions.",
        "toleranceMm": tolerance,
        "acceptedEventMatches": matches,
        "targetedRegionEvaluation": region_evaluation,
        "metrics": {
            "truePositive": true_positive,
            "falsePositive": false_positive,
            "falseNegative": false_negative,
            "precision": precision,
            "recall": recall,
            "f1": f1,
            "exactSevenEventSequence": true_positive == 7 and false_positive == 0,
        },
    })
    t7 = next((item for item in matches if item["matchedLevel"] == "T7"), None)
    write_json(root / "coordinate_error_analysis.json", {
        "contract": "thoracic_g2_r5_post_freeze_coordinate_evaluation_v1",
        "evaluatorOnlyAnalysis": True,
        "targetedCranialRegions": region_evaluation,
        "t7": {
            "predictedCoordinateMm": t7["predictedCoordinateMm"] if t7 else None,
            "referenceCoordinateMm": t7["referenceCoordinateMm"] if t7 else None,
            "signedErrorMm": t7["signedErrorMm"] if t7 else None,
            "absoluteErrorMm": t7["absoluteErrorMm"] if t7 else None,
            "toleranceMm": tolerance,
            "marginToToleranceMm": tolerance - t7["absoluteErrorMm"] if t7 else None,
            "updatedByR5": False,
        },
        "actorEmittedT7": relative_map["predictedT7RelativeMm"] is not None,
    })


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--references", required=True)
    args = parser.parse_args()
    root = Path(args.root)
    generate_actor_reports(root)
    generate_evaluator_reports(root, Path(args.references))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
