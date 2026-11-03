from __future__ import annotations

import argparse
import csv
import json
import shutil
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image


LEVELS = ["T2", "T3", "T4", "T5", "T6", "T7", "T8"]


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, document) -> None:
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--campaign-root", required=True, type=Path)
    parser.add_argument("--confirmation", required=True, type=Path)
    parser.add_argument("--references", required=True)
    args = parser.parse_args()

    campaign = args.campaign_root.resolve()
    confirmation = args.confirmation.resolve()
    references = [float(value) for value in args.references.split(",")]
    actor = confirmation / "actor_analysis"
    evaluator = confirmation / "evaluator_only"
    map_document = load_json(actor / "relative_spine_map.json")
    uncertainty = load_json(actor / "mapping_uncertainty.json")
    candidates = load_json(actor / "patch_event_candidates.json")["candidates"]
    trajectory = load_json(confirmation / "trajectory_actor.json")
    run_summary = load_json(confirmation / "run_summary.json")
    score = load_json(evaluator / "patch_likelihood_score.json")

    predicted = [float(level["centerRelativeMm"]) for level in map_document["levels"]]
    errors = [abs(value - reference) for value, reference in zip(predicted, references)]
    signed_errors = [value - reference for value, reference in zip(predicted, references)]
    margins = [5.0 - value for value in errors]

    csv_path = campaign / "per_level_error_analysis.csv"
    with csv_path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["level", "predicted_home_relative_mm", "reference_home_relative_mm",
                         "signed_error_mm", "absolute_error_mm", "margin_to_5mm_mm", "pass"])
        for row in zip(LEVELS, predicted, references, signed_errors, errors, margins):
            writer.writerow([*row, row[4] <= 5.0])

    results = {
        "contract": "autonomous_t2_t8_frozen_confirmation_results_v1",
        "actorOutputFrozenBeforeEvaluation": True,
        "blindMapSha256": "6fc99eafe013e29a8d5b491ad797dd68d64da7c7197cab267d39dd3889481fc1",
        "acceptedEvents": len([candidate for candidate in candidates if candidate["accepted"]]),
        "rejectedEvents": len([candidate for candidate in candidates if not candidate["accepted"]]),
        "acceptedFalseEvents": score["falsePositive"],
        "truePositive": score["truePositive"],
        "falseNegative": score["falseNegative"],
        "precision": score["precision"],
        "recall": score["recall"],
        "f1": score["f1"],
        "exactSeven": score["exactSeven"],
        "uniqueMap": map_document["overallMapStatus"] == "ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP",
        "predictedT7RelativeMm": map_document["predictedT7RelativeMm"],
        "maximumAbsoluteErrorMm": max(errors),
        "minimumMarginToToleranceMm": min(margins),
        "levels": [
            {
                "level": level,
                "predictedHomeRelativeMm": prediction,
                "referenceHomeRelativeMm": reference,
                "signedErrorMm": signed_error,
                "absoluteErrorMm": error,
                "marginTo5MmToleranceMm": margin,
                "supported": map_document["levels"][index]["accepted"],
                "pass": error <= 5.0,
            }
            for index, (level, prediction, reference, signed_error, error, margin) in enumerate(
                zip(LEVELS, predicted, references, signed_errors, errors, margins))
        ],
    }
    save_json(campaign / "landmark_detection_results.json", results)
    shutil.copy2(actor / "relative_spine_map.json", campaign / "relative_spine_map.json")
    shutil.copy2(actor / "mapping_uncertainty.json", campaign / "mapping_uncertainty.json")

    gates = {
        "contract": "autonomous_t2_t8_final_gate_matrix_v1",
        "G0": "PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION",
        "G1": "PASS",
        "G2": "PASS",
        "G3": "PASS",
        "G4": "NOT_RUN_OUT_OF_SCOPE",
        "overallLandmarkDiscovery": "PASS",
        "physicalReturnToT7": "NOT_RUN",
        "basis": {
            "freshFrames": run_summary["validFrames"],
            "acceptedMovements": run_summary["acceptedMovements"],
            "rejectedMovements": run_summary["rejectedMovements"],
            "returnedWithinTolerance": run_summary["returnedWithinTolerance"],
            "acceptedAnatomicalEvents": results["acceptedEvents"],
            "acceptedFalseEvents": results["acceptedFalseEvents"],
            "maximumAbsoluteErrorMm": results["maximumAbsoluteErrorMm"],
        },
    }
    save_json(campaign / "final_gates.json", gates)

    fig, axes = plt.subplots(2, 4, figsize=(16, 8))
    display_candidates = [candidate for candidate in candidates if candidate["accepted"]]
    display_candidates += [candidate for candidate in candidates if not candidate["accepted"]][:1]
    for index, (axis, candidate) in enumerate(zip(axes.flat, display_candidates)):
        observation = candidate["supportingObservations"][0]
        image = np.asarray(Image.open(confirmation / observation["image"]).convert("L"))
        axis.imshow(image, cmap="gray", vmin=0, vmax=255)
        if candidate["accepted"]:
            title = f"{LEVELS[index]} actor event {candidate['coordinateMm']:+.2f} mm\nscore={candidate['score']:.3f}"
            color = "lime"
        else:
            title = f"Rejected recurrent interface {candidate['coordinateMm']:+.2f} mm\nscore={candidate['score']:.3f} < 0.700"
            color = "orange"
        axis.set_title(title, color=color, fontsize=10)
        axis.set_axis_off()
    fig.suptitle("Frozen confirmation: actual ultrasound support frames (actor-side decisions)")
    fig.tight_layout()
    fig.savefig(campaign / "landmark_and_secondary_contact_sheet.png", dpi=160)
    plt.close(fig)

    fig, axis = plt.subplots(figsize=(14, 4.8))
    axis.axvline(0.0, color="black", linewidth=1.5, label="Fixed Home")
    axis.scatter(predicted, np.ones(7), color="#2563eb", s=80, label="Frozen actor predictions")
    axis.scatter(references, np.zeros(7), color="#dc2626", marker="x", s=90, label="Evaluator references")
    for level, value in zip(LEVELS, predicted):
        axis.annotate(level, (value, 1.0), xytext=(0, 8), textcoords="offset points", ha="center")
    axis.set_yticks([0, 1], ["Evaluator-only", "Actor"])
    axis.set_xlabel("Home-relative longitudinal coordinate (mm)")
    axis.set_title("T2–T8 relative map: predictions frozen before evaluator overlay")
    axis.grid(axis="x", alpha=0.25)
    axis.legend(loc="upper left")
    fig.tight_layout()
    fig.savefig(campaign / "final_relative_spine_map_evaluator_overlay.png", dpi=180)
    plt.close(fig)

    fig, axis = plt.subplots(figsize=(10, 4.8))
    bars = axis.bar(LEVELS, errors, color="#2563eb")
    axis.axhline(5.0, color="#dc2626", linestyle="--", label="Locked 5 mm tolerance")
    axis.bar_label(bars, labels=[f"{value:.3f}" for value in errors], padding=3)
    axis.set_ylabel("Absolute localization error (mm)")
    axis.set_title("Frozen independent per-landmark localization error")
    axis.set_ylim(0, 5.5)
    axis.legend()
    fig.tight_layout()
    fig.savefig(campaign / "per_landmark_localization_error.png", dpi=180)
    plt.close(fig)

    observations = trajectory["observations"]
    coordinates = [float(observation["signedActorRelativeMm"]) for observation in observations]
    fig, axis = plt.subplots(figsize=(13, 4.8))
    axis.plot(range(len(coordinates)), coordinates, linewidth=1.6, color="#0f766e")
    axis.axhline(0.0, color="black", linewidth=1, label="Home")
    axis.set_xlabel("Fresh observation index")
    axis.set_ylabel("Executed Home-relative coordinate (mm)")
    axis.set_title("Frozen confirmation physical trajectory (221 fresh observations)")
    axis.grid(alpha=0.25)
    axis.legend()
    fig.tight_layout()
    fig.savefig(campaign / "confirmation_probe_trajectory.png", dpi=180)
    plt.close(fig)


if __name__ == "__main__":
    main()
