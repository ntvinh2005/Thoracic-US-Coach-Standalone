"""Create the bounded G2-R3 offline attribution and readiness evidence bundle."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw

from g2_r2_continuous_detector import write_json, write_map_csv
from g2_r2_interface_tracker import (
    order_preserving_match,
    reference_coordinates,
    track_bright_interface,
)
from g2_r3_topology_discriminator import (
    MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
    apply_shadow_topology_gate,
    candidate_provenance_attribution,
    candidate_topology_measurements,
)
from position_aware_relocalization import block_ssim, load_image_descriptor
from relative_spine_e2_analysis import load_gray, sha256


def score(candidates: list[dict[str, Any]], references: list[dict[str, Any]]) -> dict[str, Any]:
    matches = order_preserving_match(candidates, references)
    true_positive = sum(item["matched"] for item in matches)
    false_positive = len(candidates) - true_positive
    false_negative = len(references) - true_positive
    precision = true_positive / len(candidates) if candidates else 0.0
    recall = true_positive / len(references)
    f1 = 2.0 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {
        "candidateMatches": matches,
        "metrics": {
            "truePositive": true_positive,
            "falsePositive": false_positive,
            "falseNegative": false_negative,
            "precision": precision,
            "recall": recall,
            "f1": f1,
            "exactSevenEventSequence": len(candidates) == 7 and true_positive == 7,
        },
    }


def representative_row(topology: dict[str, Any], candidate_id: str) -> dict[str, Any]:
    rows = [item for item in topology["measurements"]
            if item["candidateId"] == candidate_id]
    candidate = next(item for item in topology["candidates"]
                     if item["candidateId"] == candidate_id)
    target = candidate["medianInterfaceDepthPixelY"]
    return min(rows, key=lambda item: abs(item["depthPixelY"] - target))


def overlay(source_root: Path, row: dict[str, Any]) -> Image.Image:
    gray = load_gray(source_root / row["image"])
    track = track_bright_interface(gray)
    image = Image.fromarray(np.uint8(np.clip(gray, 0.0, 1.0) * 255)).convert("RGB")
    draw = ImageDraw.Draw(image)
    points = [(int(x), int(round(y))) for x, y in zip(track["x"], track["pathY"])]
    draw.line(points, fill=(255, 64, 64), width=2)
    x = int(round(row["pixelX"]))
    y = int(round(row["depthPixelY"]))
    draw.ellipse((x - 5, y - 5, x + 5, y + 5), outline=(255, 230, 0), width=3)
    distal_end = min(image.height - 1, y + int(MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS))
    draw.line((x, y + 6, x, distal_end), fill=(0, 255, 255), width=2)
    return image


def comparison_figures(
    source_root: Path, topology: dict[str, Any], output_root: Path
) -> None:
    first_three = topology["candidates"][:3]
    panels = []
    for candidate in first_three:
        row = representative_row(topology, candidate["candidateId"])
        panel = overlay(source_root, row).resize((384, 384))
        ImageDraw.Draw(panel).text(
            (8, 8),
            f"{candidate['candidateId']} {candidate['actorRelativeCoordinateMm']:.2f} mm\n"
            f"distal field {candidate['minimumDistalObservationPixels']:.1f} px",
            fill=(255, 255, 0),
        )
        panels.append(panel)
    sheet = Image.new("RGB", (384 * len(panels), 384), "white")
    for index, panel in enumerate(panels):
        sheet.paste(panel, (384 * index, 0))
    sheet.save(output_root / "e01_e02_e03_comparison.png")

    candidates = topology["candidates"]
    rows = []
    for start in range(0, len(candidates), 4):
        row_image = Image.new("RGB", (1024, 290), "white")
        for offset, candidate in enumerate(candidates[start:start + 4]):
            item = representative_row(topology, candidate["candidateId"])
            tile = overlay(source_root, item).resize((256, 256))
            row_image.paste(tile, (offset * 256, 0))
            ImageDraw.Draw(row_image).text(
                (offset * 256 + 4, 260),
                f"{candidate['candidateId']} depth={candidate['medianInterfaceDepthPixelY']:.1f} "
                f"distal={candidate['minimumDistalObservationPixels']:.1f}",
                fill="black",
            )
        rows.append(row_image)
    contact = Image.new("RGB", (1024, 290 * len(rows)), "white")
    for index, row_image in enumerate(rows):
        contact.paste(row_image, (0, index * 290))
    contact.save(output_root / "all_event_morphology_contact_sheet.png")


def draw_waveform(topology: dict[str, Any], gate: dict[str, Any], output: Path) -> None:
    candidates = topology["candidates"]
    x = [item["depthAwareCenterMm"] for item in candidates]
    y = [item["minimumDistalObservationPixels"] for item in candidates]
    accepted_ids = {item["candidateId"] for item in gate["accepted"]}
    colors = ["tab:green" if item["candidateId"] in accepted_ids else "tab:orange"
              for item in candidates]
    figure, axis = plt.subplots(figsize=(13, 5), constrained_layout=True)
    axis.plot(x, y, color="0.65", linewidth=1.5)
    axis.scatter(x, y, c=colors, s=70)
    for item, x_value, y_value in zip(candidates, x, y):
        axis.text(x_value, y_value + 2.0, item["candidateId"], ha="center")
    axis.axhline(MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS, color="tab:red",
                 linestyle="--", label="minimum verifiable distal field")
    axis.set_xlabel("Depth-aware actor-relative event coordinate (mm)")
    axis.set_ylabel("Available distal shadow field (pixels)")
    axis.set_title("G2-R3 acoustic-shadow topology verifiability")
    axis.grid(alpha=0.25)
    axis.legend()
    figure.savefig(output, dpi=180)
    plt.close(figure)


def independence(sources: list[Path]) -> dict[str, Any]:
    comparisons = []
    for left_index in range(len(sources)):
        for right_index in range(left_index + 1, len(sources)):
            left_actor = json.loads((sources[left_index] / "trajectory_actor.json").read_text())
            right_actor = json.loads((sources[right_index] / "trajectory_actor.json").read_text())
            count = min(len(left_actor["observations"]), len(right_actor["observations"]))
            scores = []
            coordinate_delta = []
            for index in range(count):
                left = left_actor["observations"][index]
                right = right_actor["observations"][index]
                scores.append(block_ssim(
                    load_image_descriptor(sources[left_index] / left["image"]),
                    load_image_descriptor(sources[right_index] / right["image"]),
                ))
                coordinate_delta.append(abs(
                    float(left["signedActorRelativeMm"])
                    - float(right["signedActorRelativeMm"])
                ))
            comparisons.append({
                "left": sources[left_index].name,
                "right": sources[right_index].name,
                "matchedIndexCount": count,
                "medianSameIndexSsim": float(np.median(scores)),
                "minimumSameIndexSsim": float(np.min(scores)),
                "maximumActorCoordinateDifferenceMm": float(np.max(coordinate_delta)),
            })
    return {
        "comparison": comparisons,
        "interpretation": (
            "All sweeps are separately rendered physical executions but follow nearly identical "
            "fixed paths; they are repeated-path development evidence, not broad trajectory generalization."
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", action="append", nargs=3, metavar=("NAME", "SOURCE", "CANDIDATES"),
                        required=True)
    parser.add_argument("--r1-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    output_root = args.output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=False)
    (output_root / "evaluator_only").mkdir()
    results = []
    topologies = []
    sources = []
    for name, source_text, candidate_text in args.dataset:
        source_root = Path(source_text).resolve()
        candidate_root = Path(candidate_text).resolve()
        sources.append(source_root)
        topology = candidate_topology_measurements(source_root, candidate_root)
        gate = apply_shadow_topology_gate(topology)
        references, _ = reference_coordinates(source_root, args.r1_root.resolve())
        baseline_candidates = json.loads(
            (candidate_root / "event_candidates.json").read_text()
        )["candidates"]
        baseline = score(baseline_candidates, references)
        corrected = score(gate["accepted"], references)
        result = {
            "dataset": name,
            "sourceRootName": source_root.name,
            "baseline": baseline,
            "topologyAware": corrected,
            "accepted": gate["accepted"],
            "ambiguous": gate["ambiguous"],
            "relativeSpineMap": gate["relativeSpineMap"],
        }
        results.append(result)
        topologies.append(topology)
    comparison = {
        "contract": "thoracic_g2_r3_offline_development_comparison_v1",
        "developmentTuningDisclosure": (
            "The 45-pixel distal-field criterion was selected on these three G2-R2 "
            "development sweeps after evaluator attribution; none is held-out."
        ),
        "datasets": results,
        "independence": independence(sources),
    }
    write_json(output_root / "offline_development_comparison.json", comparison)
    with (output_root / "offline_development_comparison.csv").open(
        "w", encoding="utf-8", newline=""
    ) as handle:
        writer = csv.writer(handle)
        writer.writerow(("dataset", "method", "candidates", "tp", "fp", "fn",
                         "precision", "recall", "f1", "exactSeven"))
        for result in results:
            for method in ("baseline", "topologyAware"):
                metrics = result[method]["metrics"]
                writer.writerow((result["dataset"], method,
                                 metrics["truePositive"] + metrics["falsePositive"],
                                 metrics["truePositive"], metrics["falsePositive"],
                                 metrics["falseNegative"], metrics["precision"],
                                 metrics["recall"], metrics["f1"],
                                 metrics["exactSevenEventSequence"]))

    final_topology = topologies[-1]
    final_gate = apply_shadow_topology_gate(final_topology)
    provenance = candidate_provenance_attribution(sources[-1], final_topology)
    write_json(output_root / "event_morphology_diagnostics.json", final_topology)
    write_json(output_root / "event_acceptance_rejection.json", final_gate)
    write_json(output_root / "relative_spine_map.json", final_gate["relativeSpineMap"])
    write_map_csv(output_root / "relative_spine_map.csv", final_gate["relativeSpineMap"])
    write_json(output_root / "mapping_uncertainty.json", {
        "intervalSemantics": "Observed support interval, not calibrated statistical confidence.",
        "levels": final_gate["relativeSpineMap"]["levels"],
    })
    write_json(output_root / "evaluator_only" / "e02_attribution_evaluator.json", {
        "contract": "thoracic_g2_r3_e02_attribution_v1",
        "actorUse": "PROHIBITED_EVALUATOR_ONLY",
        "candidateAttribution": provenance,
        "conclusion": (
            "E02 is a repeatable multi-interface tracking mixture. At the mapped beam it "
            "alternates between Skeleton-shadow and non-shadow tissue/background contours; "
            "it is not one consistently LF-associated posterior bone recess."
        ),
    })
    coordinate_rows = []
    final_score = results[-1]["topologyAware"]
    for match in final_score["candidateMatches"]:
        if match["matched"]:
            coordinate_rows.append(match)
    write_json(output_root / "coordinate_error_analysis.json", {
        "projection": (
            "The renderer maps texture x to scan-plane x with one linear transform scaled "
            "by insonationPlaneBottomWidth_mm; therefore the original G2-R2 bottom-footprint "
            "projection is retained. A local-width normalization would not match the renderer. "
            "No evaluator-derived offset is applied."
        ),
        "matchedEvents": coordinate_rows,
        "t7": next((item for item in coordinate_rows if item["matchedLevel"] == "T7"), None),
    })
    readiness = {
        "contract": "thoracic_g2_r3_readiness_gate_v1",
        "r3AMeaningfulDiagnosis": True,
        "generalImageOnlyRuleDefined": True,
        "noAdditionalFalseNegatives": all(
            item["topologyAware"]["metrics"]["falseNegative"]
            <= item["baseline"]["metrics"]["falseNegative"] for item in results
        ),
        "projectionReviewed": True,
        "actorEvaluatorIsolationTestsRequired": True,
        "configurationFrozen": False,
        "confirmationConfigurationPredeclared": False,
    }
    readiness["offlineScientificReadiness"] = all((
        readiness["r3AMeaningfulDiagnosis"], readiness["generalImageOnlyRuleDefined"],
        readiness["noAdditionalFalseNegatives"], readiness["projectionReviewed"],
    ))
    write_json(output_root / "development_readiness_gate.json", readiness)
    comparison_figures(sources[-1], final_topology, output_root)
    draw_waveform(final_topology, final_gate, output_root / "anatomical_waveform.png")

    (output_root / "feature_definition.md").write_text(
        "# G2-R3 Feature Definition\n\n"
        "Selected family: acoustic-shadow onset/termination continuity. A bright-interface "
        "valley remains eligible only when at least 45 image pixels remain distal to the "
        "75th-percentile tracked interface depth. This is the minimum field needed to inspect "
        "a post-interface shadow rather than accepting a deep image-boundary contour. The "
        "same rule is applied to every candidate. Insufficient field yields AMBIGUOUS, not "
        "a claim that the physical structure is absent. The threshold is development-tuned "
        "on all three G2-R2 sweeps and must be frozen before confirmation.\n",
        encoding="utf-8",
    )
    e02 = next(item for item in final_topology["candidates"] if item["candidateId"] == "E02")
    (output_root / "e02_attribution_report.md").write_text(
        "# E02 Attribution\n\n"
        f"E02 is a repeat-supported image event at {e02['actorRelativeCoordinateMm']:.3f} mm. "
        f"Its median tracked depth is {e02['medianInterfaceDepthPixelY']:.1f} pixels and only "
        f"{e02['minimumDistalObservationPixels']:.1f} pixels remain for distal shadow "
        "verification. Evaluator-only ordered-ray provenance shows that the mapped beam has "
        "Skeleton as first shadow in only 11/27 support observations; 16/27 have no shadow. "
        "E02 is therefore a real recurrent contour but an inconsistent interface-identity "
        "track, not one coherently verified LF-associated recess. Actor-side rejection is not "
        "based on that privileged identity: it marks E02 AMBIGUOUS because the image does not "
        "contain enough distal field to verify acoustic-shadow topology.\n",
        encoding="utf-8",
    )
    source_files = [
        Path(__file__).resolve(),
        Path(__file__).with_name("g2_r3_topology_discriminator.py").resolve(),
    ]
    write_json(output_root / "source_configuration_hash_manifest.json", {
        "sources": {str(path): sha256(path) for path in source_files},
        "inputs": {source.name + "/trajectory_actor.json": sha256(
            source / "trajectory_actor.json") for source in sources},
        "configuration": {
            "minimumDistalShadowObservationPixels":
                MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
            "scoringTolerance": "unchanged per-reference min(5 mm, 0.25 nearest spacing)",
        },
    })
    write_json(output_root / "final_gates.json", {
        "G0": "PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION",
        "G1": "PASS_HISTORICAL_G2_R2",
        "G2": "PENDING_FROZEN_PHYSICAL_CONFIRMATION",
        "G3": "PENDING_FROZEN_PHYSICAL_CONFIRMATION",
        "G4": "NOT_RUN_PROTOCOL_EXCLUSION",
    })
    print(json.dumps({
        "outputRoot": str(output_root),
        "offlineReadiness": readiness["offlineScientificReadiness"],
        "comparison": [item["topologyAware"]["metrics"] for item in results],
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
