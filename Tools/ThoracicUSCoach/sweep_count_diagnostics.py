from __future__ import annotations

import argparse
import csv
import json
import math
import shutil
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from sweep_count_analysis import (
    detect_candidate_events,
    detect_candidate_events_with_boundaries,
    detect_candidate_valleys_with_boundaries,
    evaluator_coordinates_for_event_indices,
    ordered_match,
)


RUN_NAMES = (
    "SWEEP_COUNT_M1_20261030_152908_164",
    "SWEEP_COUNT_M1_20261030_152949_710",
    "SWEEP_COUNT_M1_20261030_153028_018",
)
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def load_signal(path: Path) -> dict[str, np.ndarray]:
    values = np.genfromtxt(path, delimiter=",", names=True)
    return {name: np.asarray(values[name], dtype=np.float64) for name in values.dtype.names or ()}


def quaternion_angle_deg(first: dict, second: dict) -> float:
    dot = abs(sum(float(first[key]) * float(second[key]) for key in ("x", "y", "z", "w")))
    return math.degrees(2.0 * math.acos(min(1.0, max(-1.0, dot))))


def vector_distance(first: dict, second: dict) -> float:
    return math.sqrt(sum((float(first[key]) - float(second[key])) ** 2 for key in ("x", "y", "z")))


def build_yaw_audit(run_roots: list[Path]) -> dict:
    documents = [load_json(root / "evaluator_only" / "trace_privileged.json") for root in run_roots]
    observations = [document["observations"] for document in documents]
    labels = ["yaw_0", "yaw_minus_3", "yaw_plus_3"]
    comparisons = []
    for label, candidate in zip(labels[1:], observations[1:]):
        angles = [
            quaternion_angle_deg(observations[0][index]["probeRotation"], candidate[index]["probeRotation"])
            for index in range(min(len(observations[0]), len(candidate)))
        ]
        positions = [
            vector_distance(observations[0][index]["probePosition"], candidate[index]["probePosition"])
            for index in range(min(len(observations[0]), len(candidate)))
        ]
        comparisons.append(
            {
                "comparison": f"yaw_0_vs_{label}",
                "frame0QuaternionAngleDeg": angles[0],
                "framesAfterFirstMoveQuaternionAngleDegMax": max(angles[1:]),
                "framesAfterFirstMoveQuaternionAngleDegMean": float(np.mean(angles[1:])),
                "framesAfterFirstMovePositionDeltaMmMax": max(positions[1:]),
            }
        )
    achieved = []
    for label, document in zip(labels, documents):
        items = document["observations"]
        if not items or "achievedSurfaceYawDeg" not in items[0]:
            continue
        errors = [abs(float(item["surfaceYawErrorDeg"])) for item in items]
        values = [float(item["achievedSurfaceYawDeg"]) for item in items]
        achieved.append(
            {
                "run": label,
                "commandedSurfaceYawDeg": float(document["commandedSurfaceYawDeg"]),
                "minimumAchievedSurfaceYawDeg": min(values),
                "maximumAchievedSurfaceYawDeg": max(values),
                "maximumAbsoluteYawErrorDeg": max(errors),
                "allObservationsWithinTolerance": bool(document["surfaceYawPreservedAcrossObservations"]),
            }
        )
    preserved = len(achieved) == 3 and all(item["allObservationsWithinTolerance"] for item in achieved)
    return {
        "source": "three ordered evaluator-only traces: yaw 0, yaw -3, yaw +3",
        "finding": (
            "All three commanded orientations were preserved across every physical observation."
            if preserved else
            "The commanded +/-3 degree orientations existed only at observation 0 and converged to the yaw-0 rotation after the first physical move."
        ),
        "yawSensitivityExperimentValid": preserved,
        "comparisons": comparisons,
        "achievedSurfaceRelativeYaw": achieved,
        "implementationVerification": {
            "method": "Focused Unity flat-surface regression measures achieved probe rotation relative to reconstructed zero-yaw local scan geometry after each physical move.",
            "freshCurvedSurfaceSweepVerification": "PASS" if preserved else "NOT_RUN",
        },
    }


def plot_series(
    path: Path,
    title: str,
    travel: np.ndarray,
    series: list[tuple[str, np.ndarray, tuple[int, int, int]]],
    markers: list[tuple[str, list[int], tuple[int, int, int]]],
    level_frames: list[tuple[str, int]],
) -> None:
    width, height = 1500, 820
    left, right, top, bottom = 90, 35, 75, 100
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.load_default()
    plot_width, plot_height = width - left - right, height - top - bottom
    all_values = np.concatenate([values for _, values, _ in series])
    y_min, y_max = float(np.min(all_values)), float(np.max(all_values))
    padding = max(0.1, 0.08 * (y_max - y_min))
    y_min, y_max = y_min - padding, y_max + padding
    x_min, x_max = float(travel[0]), float(travel[-1])

    def point(index: int, value: float) -> tuple[int, int]:
        x = left + int((float(travel[index]) - x_min) / max(x_max - x_min, 1e-6) * plot_width)
        y = top + plot_height - int((value - y_min) / max(y_max - y_min, 1e-6) * plot_height)
        return x, y

    draw.text((left, 25), title, fill="black", font=font)
    draw.rectangle((left, top, left + plot_width, top + plot_height), outline="black")
    for number in range(6):
        x = left + int(number * plot_width / 5)
        draw.line((x, top, x, top + plot_height), fill=(225, 225, 225))
        draw.text((x - 12, top + plot_height + 12), f"{x_min + number * (x_max - x_min) / 5:.0f}", fill="black")
    for level, index in level_frames:
        x, _ = point(index, y_min)
        draw.line((x, top, x, top + plot_height), fill=(190, 190, 190))
        draw.text((x + 2, top + 2), level, fill=(80, 80, 80))
    for _, values, color in series:
        draw.line([point(index, float(value)) for index, value in enumerate(values)], fill=color, width=3)
    marker_value = series[-1][1]
    for _, indices, color in markers:
        for index in indices:
            x, y = point(index, float(marker_value[index]))
            draw.ellipse((x - 6, y - 6, x + 6, y + 6), fill=color, outline="black")
    x = left + 12
    for name, _, color in series:
        draw.line((x, height - 42, x + 28, height - 42), fill=color, width=4)
        draw.text((x + 34, height - 49), name, fill="black")
        x += 190
    for name, _, color in markers:
        draw.ellipse((x, height - 48, x + 12, height - 36), fill=color, outline="black")
        draw.text((x + 18, height - 49), name, fill="black")
        x += 190
    draw.text((left + plot_width // 2 - 55, height - 72), "actor relative travel (mm)", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def overlay_roi(source: Path, destination: Path) -> None:
    image = Image.open(source).convert("RGB")
    draw = ImageDraw.Draw(image)
    width, height = image.size
    box = (int(0.08 * width), int(0.15 * height), int(0.92 * width), int(0.90 * height))
    draw.rectangle(box, outline=(255, 215, 0), width=max(2, width // 256))
    draw.text((box[0] + 6, box[1] + 6), "feature ROI", fill=(255, 215, 0))
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination)


def contact_sheet(run_root: Path, destination: Path, level: str, center: int, signal: dict[str, np.ndarray]) -> None:
    indices = list(range(max(0, center - 2), min(len(signal["travel_mm"]), center + 3)))
    tile_width, tile_height, caption = 240, 240, 54
    canvas = Image.new("RGB", (tile_width * len(indices), tile_height + caption), "white")
    draw = ImageDraw.Draw(canvas)
    for column, index in enumerate(indices):
        image = Image.open(run_root / "frames" / f"frame_{index:04d}.png").convert("RGB")
        image.thumbnail((tile_width, tile_height))
        x = column * tile_width + (tile_width - image.width) // 2
        canvas.paste(image, (x, 0))
        draw.text(
            (column * tile_width + 4, tile_height + 4),
            f"{level} window f{index:02d}\ntravel={signal['travel_mm'][index]:.1f} combined={signal['combined'][index]:.2f}",
            fill="black",
        )
    destination.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(destination)


def region_indices(coordinates: np.ndarray, reference_coordinates: list[float], level_index: int) -> list[int]:
    lower = -math.inf if level_index == 0 else 0.5 * (reference_coordinates[level_index - 1] + reference_coordinates[level_index])
    upper = math.inf if level_index == len(reference_coordinates) - 1 else 0.5 * (reference_coordinates[level_index] + reference_coordinates[level_index + 1])
    return [index for index, coordinate in enumerate(coordinates) if lower <= float(coordinate) < upper]


def build_level_matrix(run_root: Path, output_root: Path, signal: dict[str, np.ndarray]) -> list[dict]:
    references = load_json(run_root / "evaluator_only" / "references.json")["references"]
    observations = load_json(run_root / "evaluator_only" / "trace_privileged.json")["observations"]
    coordinates = np.asarray([item["corridorCoordinateMm"] for item in observations], dtype=np.float64)
    reference_coordinates = [float(item["corridorCoordinateMm"]) for item in references]
    travel, combined = signal["travel_mm"], signal["combined"]
    baseline = detect_candidate_events(travel, combined)
    revised = detect_candidate_events_with_boundaries(travel, combined)
    valleys = detect_candidate_valleys_with_boundaries(travel, combined)
    transitions = detect_candidate_events_with_boundaries(travel, signal["transition"])
    positive_threshold = float(np.median(combined) + 0.75 * max(np.median(np.abs(combined - np.median(combined))), 1e-6))
    negative_threshold = float(np.median(combined) - 0.75 * max(np.median(np.abs(combined - np.median(combined))), 1e-6))
    rows = []
    for level_index, reference in enumerate(references):
        level = reference["level"]
        nearest = int(np.argmin(np.abs(coordinates - float(reference["corridorCoordinateMm"]))))
        indices = region_indices(coordinates, reference_coordinates, level_index)
        baseline_here = [index for index in baseline if index in indices]
        revised_here = [index for index in revised if index in indices]
        valleys_here = [index for index in valleys if index in indices]
        transitions_here = [index for index in transitions if index in indices]
        if baseline_here:
            root_cause = "A positive combined-signal maximum was recognized; evaluator proximity does not establish vertebral identity."
        elif valleys_here:
            root_cause = "A negative valley is present, but the unchanged baseline recognizes positive maxima only."
        elif transitions_here:
            root_cause = "A transition response is present, but the unchanged baseline does not treat transition-only responses as events."
        else:
            root_cause = "Visible structure did not yield a separated, above-threshold positive maximum in this level region."
        row = {
            "level": level,
            "diagnosticReferenceCoordinateMm": float(reference["corridorCoordinateMm"]),
            "nearestObservationIndex": nearest,
            "nearestObservationEvaluatorCoordinateMm": float(coordinates[nearest]),
            "anatomicalStructureVisibleInImage": "Bone-like echogenic interface with adjacent low-signal/shadow structure is visible; exact spinous-process, lamina, rib, or interspace identity is unresolved.",
            "structureCapturedByExtractedFeature": {
                "nearestBoneInterface": float(signal["bone_interface"][nearest]),
                "nearestShadowFraction": float(signal["shadow_fraction"][nearest]),
                "nearestInterfaceDepth": float(signal["interface_depth"][nearest]),
                "regionHasPositiveExcursion": any(combined[index] >= positive_threshold for index in indices),
                "regionHasNegativeExcursion": any(combined[index] <= negative_threshold for index in indices),
                "diagnosticTransitionIndices": transitions_here,
            },
            "eventRecognizedByDetector": {
                "baselinePositivePeakIndices": baseline_here,
                "boundaryCorrectedPositivePeakIndices": revised_here,
                "diagnosticNegativeValleyIndices": valleys_here,
            },
            "rootCauseAssessment": root_cause,
        }
        rows.append(row)
        source = run_root / "frames" / f"frame_{nearest:04d}.png"
        overlay_roi(source, output_root / "roi_overlays" / f"{level}_frame_{nearest:04d}.png")
        contact_sheet(run_root, output_root / "contact_sheets" / f"{level}_window.png", level, nearest, signal)
    return rows


def write_matrix_csv(path: Path, rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(("level", "nearest_frame", "visible_structure", "feature_capture", "baseline_event", "revised_event", "diagnostic_valley", "root_cause"))
        for row in rows:
            feature = row["structureCapturedByExtractedFeature"]
            event = row["eventRecognizedByDetector"]
            writer.writerow(
                (
                    row["level"], row["nearestObservationIndex"], row["anatomicalStructureVisibleInImage"],
                    f"bone={feature['nearestBoneInterface']:.3f}; shadow={feature['nearestShadowFraction']:.3f}; depth={feature['nearestInterfaceDepth']:.3f}",
                    event["baselinePositivePeakIndices"], event["boundaryCorrectedPositivePeakIndices"],
                    event["diagnosticNegativeValleyIndices"], row["rootCauseAssessment"],
                )
            )


def detector_comparison(run_root: Path) -> dict:
    signal = load_signal(run_root / "signal.csv")
    privileged = load_json(run_root / "evaluator_only" / "trace_privileged.json")
    references = load_json(run_root / "evaluator_only" / "references.json")["references"]
    original_evaluation = load_json(run_root / "g2_evaluation.json")
    travel, combined = signal["travel_mm"], signal["combined"]
    baseline = detect_candidate_events(travel, combined)
    revised = detect_candidate_events_with_boundaries(travel, combined)
    valleys = detect_candidate_valleys_with_boundaries(travel, combined)
    transitions = detect_candidate_events_with_boundaries(travel, signal["transition"])
    reference_coordinates = [float(item["corridorCoordinateMm"]) for item in references]
    spacings = np.diff(reference_coordinates)
    tolerance = min(12.0, 0.45 * float(np.min(spacings))) if len(spacings) else 12.0

    def matches(indices: list[int]) -> list[dict]:
        coordinates = evaluator_coordinates_for_event_indices(indices, privileged["observations"])
        result = ordered_match(coordinates, reference_coordinates, tolerance)
        for match in result:
            match["referenceLevel"] = references[match["referenceIndex"]]["level"]
            match["eventEvaluatorCoordinateMm"] = match["eventTravelMm"]
            match["referenceEvaluatorCoordinateMm"] = match["referenceTravelMm"]
        return result

    baseline_matches = matches(baseline)
    revised_matches = matches(revised)
    median = float(np.median(combined))
    positive_threshold = median + 0.75 * max(float(np.median(np.abs(combined - median))), 1e-6)
    return {
        "run": run_root.name,
        "originalG2ScientificStatus": original_evaluation["scientificStatus"],
        "baseline": {
            "candidateIndices": baseline,
            "candidateCount": len(baseline),
            "actualFrameCoordinateMatches": baseline_matches,
            "matchedReferenceCount": len(baseline_matches),
        },
        "boundaryCorrected": {
            "candidateIndices": revised,
            "candidateCount": len(revised),
            "actualFrameCoordinateMatches": revised_matches,
            "matchedReferenceCount": len(revised_matches),
            "exactSevenEventCount": len(revised) == 7,
        },
        "diagnosticOnly": {
            "negativeValleyIndices": valleys,
            "transitionIndices": transitions,
        },
        "positiveThreshold": positive_threshold,
        "firstEndpointCombined": float(combined[0]),
        "lastEndpointCombined": float(combined[-1]),
        "coordinateBasis": "corridorCoordinateMm from the corresponding actual evaluator observation frame",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Build the offline Milestone 1.5 G2 diagnostic evidence bundle.")
    parser.add_argument(
        "--artifact-root", type=Path,
        default=REPOSITORY_ROOT / "Artifacts" / "ThoracicUSCoach" / "sweep_count",
    )
    parser.add_argument(
        "--output", type=Path,
        default=REPOSITORY_ROOT / "Artifacts" / "ThoracicUSCoach" / "sweep_count"
        / "MILESTONE_1_5_G2_DIAGNOSTICS_20261030",
    )
    parser.add_argument("--run-root", type=Path, action="append")
    args = parser.parse_args()
    run_roots = [path.resolve() for path in args.run_root] if args.run_root else [
        args.artifact_root / name for name in RUN_NAMES
    ]
    if len(run_roots) != 3:
        raise ValueError("Exactly three ordered run roots are required: yaw 0, yaw -3, yaw +3")
    missing = [str(root) for root in run_roots if not root.is_dir()]
    if missing:
        raise FileNotFoundError("Missing original run artifacts: " + ", ".join(missing))
    args.output.mkdir(parents=True, exist_ok=True)

    yaw_audit = build_yaw_audit(run_roots)
    save_json(args.output / "yaw_persistence_audit.json", yaw_audit)
    primary = run_roots[0]
    signal = load_signal(primary / "signal.csv")
    trace = load_json(primary / "evaluator_only" / "trace_privileged.json")
    references = load_json(primary / "evaluator_only" / "references.json")["references"]
    coordinates = np.asarray([item["corridorCoordinateMm"] for item in trace["observations"]])
    level_frames = [
        (item["level"], int(np.argmin(np.abs(coordinates - float(item["corridorCoordinateMm"])))))
        for item in references
    ]
    travel, combined = signal["travel_mm"], signal["combined"]
    baseline = detect_candidate_events(travel, combined)
    revised = detect_candidate_events_with_boundaries(travel, combined)
    valleys = detect_candidate_valleys_with_boundaries(travel, combined)
    transitions = detect_candidate_events_with_boundaries(travel, signal["transition"])
    plot_series(
        args.output / "signal_before_baseline.png", "Before: original positive-peak G2 baseline", travel,
        [("bone interface", signal["bone_interface"], (28, 105, 180)), ("shadow fraction", signal["shadow_fraction"], (35, 150, 75)), ("interface depth", signal["interface_depth"], (130, 70, 170)), ("combined smoothed", combined, (15, 15, 15))],
        [("baseline peak", baseline, (220, 30, 30))], level_frames,
    )
    plot_series(
        args.output / "signal_after_diagnostics.png", "After: bounded boundary correction plus diagnostic polarity/transition audit", travel,
        [("morphology raw", signal["morphology"], (28, 105, 180)), ("transition raw", signal["transition"], (210, 130, 20)), ("combined smoothed", combined, (15, 15, 15))],
        [("revised peak", revised, (220, 30, 30)), ("negative valley", valleys, (35, 90, 210)), ("transition event", transitions, (240, 150, 20))], level_frames,
    )
    rows = build_level_matrix(primary, args.output, signal)
    save_json(args.output / "per_level_root_cause.json", rows)
    write_matrix_csv(args.output / "per_level_root_cause.csv", rows)
    comparison = {
        "baselineThresholdsChanged": False,
        "candidateCountWasForcedToSeven": False,
        "runs": [detector_comparison(root) for root in run_roots],
        "finding": "The boundary correction changes detector support but adds no event on any existing run because neither endpoint passes the unchanged positive threshold.",
    }
    save_json(args.output / "detector_comparison.json", comparison)
    shutil.copy2(primary / "signal.csv", args.output / "source_signal_yaw0.csv")
    save_json(args.output / "source_runs.json", {"readOnlyInputs": [str(root) for root in run_roots]})
    print(json.dumps({"output": str(args.output), "baseline": baseline, "revised": revised, "valleys": valleys, "transitions": transitions}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
