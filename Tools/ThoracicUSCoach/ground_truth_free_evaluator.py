"""Evaluator-only scoring for a frozen ground-truth-free exploration output."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt
import numpy as np

from autonomous_t2t8_patch_likelihood import score_candidates
from g2_r2_continuous_detector import read_json, write_json


def reference_coordinates_from_executed_path(
    privileged_trajectory: dict[str, Any],
    anatomy_reference_map: dict[str, Any],
) -> list[dict[str, Any]]:
    samples: dict[float, list[float]] = {}
    for observation in privileged_trajectory["observations"]:
        z_mm = float(observation["contactPoint"]["z"])
        coordinate = float(observation["evaluatorLongitudinalCoordinateMm"])
        samples.setdefault(round(z_mm, 5), []).append(coordinate)
    ordered_z = np.array(sorted(samples), dtype=float)
    ordered_s = np.array(
        [float(np.median(samples[value])) for value in ordered_z], dtype=float)
    references = []
    for landmark in anatomy_reference_map["validatedAnatomyDerivedLandmarks"]:
        z_mm = float(landmark["validatedPosteriorRecessCoordinateZMm"])
        covered = bool(ordered_z[0] <= z_mm <= ordered_z[-1])
        coordinate = float(np.interp(z_mm, ordered_z, ordered_s)) if covered else None
        references.append({
            "level": landmark["level"],
            "referenceWorldZMm": z_mm,
            "referenceHomeRelativeMm": coordinate,
            "insideExecutedCoverage": covered,
            "method": "INTERPOLATED_EXECUTED_CONTACT_PATH_EVALUATOR_ONLY",
        })
    return references


def evaluate(
    candidates_path: Path,
    privileged_trajectory_path: Path,
    anatomy_reference_map_path: Path,
    output_root: Path,
) -> dict[str, Any]:
    candidates_document = read_json(candidates_path)
    candidates = candidates_document["candidates"]
    trajectory = read_json(privileged_trajectory_path)
    anatomy = read_json(anatomy_reference_map_path)
    references = reference_coordinates_from_executed_path(trajectory, anatomy)
    covered_values = [
        float(item["referenceHomeRelativeMm"])
        for item in references if item["insideExecutedCoverage"]
    ]
    score = score_candidates(candidates, covered_values)
    result = {
        "contract": "g2_5_ground_truth_free_post_freeze_evaluator_v1",
        "warning": "Evaluator-only anatomy loaded after actor output freeze; never an actor input.",
        "actorOutputFrozenBeforeEvaluation": True,
        "referenceConstruction": (
            "Validated R1 recess world-z coordinates interpolated onto the actually "
            "executed contact path. Out-of-coverage references are not extrapolated."),
        "references": references,
        "coveredReferenceCount": len(covered_values),
        "acceptedActorEventCount": len(
            [item for item in candidates if item["accepted"]]),
        **score,
    }
    output_root.mkdir(parents=True, exist_ok=True)
    write_json(output_root / "anatomical_correspondence.json", result)
    with (output_root / "per_level_reference_coverage.csv").open(
        "w", newline="", encoding="utf-8"
    ) as handle:
        writer = csv.DictWriter(handle, fieldnames=list(references[0]))
        writer.writeheader()
        writer.writerows(references)
    figure, axis = plt.subplots(figsize=(12, 3.8))
    for item in references:
        if item["referenceHomeRelativeMm"] is not None:
            axis.axvline(item["referenceHomeRelativeMm"], color="black", alpha=0.45)
            axis.text(item["referenceHomeRelativeMm"], 0.92, item["level"],
                      rotation=90, ha="right", va="top")
    for candidate in candidates:
        axis.scatter(
            candidate["coordinateMm"], 0.35 if candidate["accepted"] else 0.15,
            color="green" if candidate["accepted"] else "orange")
    axis.set_ylim(0, 1)
    axis.set_yticks([0.15, 0.35], ["rejected candidate", "accepted candidate"])
    axis.set_xlabel("Home-relative coordinate (mm)")
    axis.set_title("Evaluator-only correspondence after actor freeze")
    axis.grid(axis="x", alpha=0.2)
    figure.tight_layout()
    figure.savefig(output_root / "evaluator_correspondence.png", dpi=160)
    plt.close(figure)
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidates", required=True, type=Path)
    parser.add_argument("--trajectory", required=True, type=Path)
    parser.add_argument("--anatomy", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    evaluate(args.candidates, args.trajectory, args.anatomy, args.output)


if __name__ == "__main__":
    main()
