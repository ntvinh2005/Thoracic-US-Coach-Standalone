from __future__ import annotations

import argparse
import csv
import hashlib
import json
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image, ImageDraw

from g2_r2_features import normalized_grayscale


@dataclass(frozen=True)
class FrameFeature:
    travel_mm: float
    bone_interface: float
    shadow_fraction: float
    interface_depth: float


def robust_z(values: np.ndarray) -> np.ndarray:
    values = np.asarray(values, dtype=np.float64)
    median = float(np.median(values))
    mad = float(np.median(np.abs(values - median)))
    scale = max(1.4826 * mad, 1e-6)
    return (values - median) / scale


def moving_average(values: np.ndarray, width: int = 3) -> np.ndarray:
    values = np.asarray(values, dtype=np.float64)
    if len(values) < width or width <= 1:
        return values.copy()
    padding = width // 2
    padded = np.pad(values, (padding, padding), mode="edge")
    return np.convolve(padded, np.ones(width) / width, mode="valid")[: len(values)]


def extract_frame_features(image_path: Path, travel_mm: float) -> FrameFeature:
    values = normalized_grayscale(image_path, 256).astype(np.float64)
    height, width = values.shape
    roi = values[int(0.15 * height) : int(0.90 * height), int(0.08 * width) : int(0.92 * width)]
    p99 = max(float(np.quantile(roi, 0.99)), 1e-4)
    normalized = np.clip(roi / p99, 0.0, 1.0)
    vertical_gradient = np.maximum(np.diff(normalized, axis=0), 0.0)
    search_start = int(0.10 * vertical_gradient.shape[0])
    search_end = int(0.82 * vertical_gradient.shape[0])
    searchable = vertical_gradient[search_start:search_end]
    peak_rows = np.argmax(searchable, axis=0) + search_start
    peak_values = np.max(searchable, axis=0)
    valid_columns = peak_values >= max(0.06, float(np.quantile(peak_values, 0.55)))
    bone_interface = float(np.mean(peak_values[valid_columns])) if np.any(valid_columns) else 0.0
    shadow_scores: list[float] = []
    for column, row in enumerate(peak_rows):
        if not valid_columns[column]:
            continue
        below = normalized[min(row + 8, normalized.shape[0] - 1) :, column]
        if below.size:
            shadow_scores.append(float(np.mean(below < 0.12)))
    shadow_fraction = float(np.mean(shadow_scores)) if shadow_scores else 0.0
    interface_depth = float(np.mean(peak_rows[valid_columns]) / max(1, normalized.shape[0] - 1)) \
        if np.any(valid_columns) else 1.0
    return FrameFeature(travel_mm, bone_interface, shadow_fraction, interface_depth)


def detect_candidate_events(travel: np.ndarray, combined: np.ndarray) -> list[int]:
    if len(combined) < 3:
        return []
    threshold = float(np.median(combined) + 0.75 * max(np.median(np.abs(combined - np.median(combined))), 1e-6))
    candidates = [
        index
        for index in range(1, len(combined) - 1)
        if combined[index] >= combined[index - 1]
        and combined[index] > combined[index + 1]
        and combined[index] >= threshold
    ]
    merged: list[int] = []
    for candidate in sorted(candidates, key=lambda index: float(combined[index]), reverse=True):
        if all(abs(float(travel[candidate] - travel[kept])) >= 12.0 for kept in merged):
            merged.append(candidate)
    return sorted(merged, key=lambda index: float(travel[index]))


def _merge_event_candidates(
    travel: np.ndarray,
    values: np.ndarray,
    candidates: Iterable[int],
    *,
    descending: bool,
) -> list[int]:
    merged: list[int] = []
    for candidate in sorted(candidates, key=lambda index: float(values[index]), reverse=descending):
        if all(abs(float(travel[candidate] - travel[kept])) >= 12.0 for kept in merged):
            merged.append(int(candidate))
    return sorted(merged, key=lambda index: float(travel[index]))


def detect_candidate_events_with_boundaries(travel: np.ndarray, combined: np.ndarray) -> list[int]:
    if len(combined) < 2:
        return []
    median = float(np.median(combined))
    threshold = median + 0.75 * max(float(np.median(np.abs(combined - median))), 1e-6)
    candidates = [
        index
        for index in range(1, len(combined) - 1)
        if combined[index] >= combined[index - 1]
        and combined[index] > combined[index + 1]
        and combined[index] >= threshold
    ]
    if combined[0] > combined[1] and combined[0] >= threshold:
        candidates.append(0)
    if combined[-1] > combined[-2] and combined[-1] >= threshold:
        candidates.append(len(combined) - 1)
    return _merge_event_candidates(travel, combined, candidates, descending=True)


def detect_candidate_valleys_with_boundaries(travel: np.ndarray, combined: np.ndarray) -> list[int]:
    if len(combined) < 2:
        return []
    median = float(np.median(combined))
    threshold = median - 0.75 * max(float(np.median(np.abs(combined - median))), 1e-6)
    candidates = [
        index
        for index in range(1, len(combined) - 1)
        if combined[index] <= combined[index - 1]
        and combined[index] < combined[index + 1]
        and combined[index] <= threshold
    ]
    if combined[0] < combined[1] and combined[0] <= threshold:
        candidates.append(0)
    if combined[-1] < combined[-2] and combined[-1] <= threshold:
        candidates.append(len(combined) - 1)
    return _merge_event_candidates(travel, combined, candidates, descending=False)


def evaluator_coordinates_for_event_indices(
    event_indices: Iterable[int], privileged_observations: Iterable[dict]
) -> list[float]:
    observations = {int(item["observationIndex"]): item for item in privileged_observations}
    result: list[float] = []
    for index in event_indices:
        if int(index) not in observations:
            raise ValueError(f"Missing privileged observation for event index {index}")
        result.append(float(observations[int(index)]["corridorCoordinateMm"]))
    return result


def ordered_match(events: Iterable[float], references: Iterable[float], tolerance_mm: float) -> list[dict]:
    detected = list(events)
    truth = list(references)
    rows, columns = len(detected) + 1, len(truth) + 1
    cost = np.full((rows, columns), np.inf, dtype=np.float64)
    back: dict[tuple[int, int], tuple[int, int, str]] = {}
    cost[0, 0] = 0.0
    miss_penalty = tolerance_mm
    for row in range(rows):
        for column in range(columns):
            current = cost[row, column]
            if not np.isfinite(current):
                continue
            if row < len(detected) and current + miss_penalty < cost[row + 1, column]:
                cost[row + 1, column] = current + miss_penalty
                back[(row + 1, column)] = (row, column, "extra")
            if column < len(truth) and current + miss_penalty < cost[row, column + 1]:
                cost[row, column + 1] = current + miss_penalty
                back[(row, column + 1)] = (row, column, "miss")
            if row < len(detected) and column < len(truth):
                error = abs(detected[row] - truth[column])
                if error <= tolerance_mm and current + error < cost[row + 1, column + 1]:
                    cost[row + 1, column + 1] = current + error
                    back[(row + 1, column + 1)] = (row, column, "match")
    matches: list[dict] = []
    cursor = (len(detected), len(truth))
    while cursor in back:
        previous_row, previous_column, action = back[cursor]
        if action == "match":
            matches.append(
                {
                    "eventIndex": previous_row,
                    "referenceIndex": previous_column,
                    "eventTravelMm": detected[previous_row],
                    "referenceTravelMm": truth[previous_column],
                    "absoluteErrorMm": abs(detected[previous_row] - truth[previous_column]),
                }
            )
        cursor = (previous_row, previous_column)
    return list(reversed(matches))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _draw_plot(
    path: Path,
    travel: np.ndarray,
    series: list[tuple[str, np.ndarray, tuple[int, int, int]]],
    events: list[int],
    references: list[tuple[str, float]] | None = None,
) -> None:
    width, height = 1400, 760
    margin_left, margin_right, margin_top, margin_bottom = 90, 35, 50, 90
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    plot_width = width - margin_left - margin_right
    plot_height = height - margin_top - margin_bottom
    x_min, x_max = float(travel.min()), float(travel.max())
    all_values = np.concatenate([values for _, values, _ in series])
    y_min, y_max = float(all_values.min()), float(all_values.max())
    if abs(y_max - y_min) < 1e-6:
        y_max = y_min + 1.0

    def xy(x_value: float, y_value: float) -> tuple[int, int]:
        x = margin_left + int((x_value - x_min) / max(x_max - x_min, 1e-6) * plot_width)
        y = margin_top + plot_height - int((y_value - y_min) / (y_max - y_min) * plot_height)
        return x, y

    draw.rectangle((margin_left, margin_top, margin_left + plot_width, margin_top + plot_height), outline="black")
    for number in range(6):
        x_value = x_min + (x_max - x_min) * number / 5
        x, _ = xy(x_value, y_min)
        draw.line((x, margin_top, x, margin_top + plot_height), fill=(225, 225, 225))
        draw.text((x - 15, margin_top + plot_height + 12), f"{x_value:.0f}", fill="black")
    for name, values, color in series:
        points = [xy(float(x_value), float(y_value)) for x_value, y_value in zip(travel, values)]
        if len(points) > 1:
            draw.line(points, fill=color, width=3)
    for event_index in events:
        x, y = xy(float(travel[event_index]), float(series[-1][1][event_index]))
        draw.ellipse((x - 6, y - 6, x + 6, y + 6), fill=(220, 30, 30), outline="black")
    if references:
        for label, coordinate in references:
            x, _ = xy(coordinate, y_min)
            draw.line((x, margin_top, x, margin_top + plot_height), fill=(100, 100, 100), width=1)
            draw.text((x + 2, margin_top + 3), label, fill=(60, 60, 60))
    legend_x = margin_left + 10
    for name, _, color in series:
        draw.line((legend_x, 22, legend_x + 28, 22), fill=color, width=4)
        draw.text((legend_x + 34, 14), name, fill="black")
        legend_x += 230
    draw.text((width // 2 - 70, height - 32), "Actor-relative travel (mm)", fill="black")
    draw.text((8, 8), "Classical ultrasound sweep signals (3-frame moving average)", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _make_contact_sheet(run_root: Path, observations: list[dict], selected: list[int], output: Path) -> None:
    tile, caption, columns = 260, 44, 4
    rows = max(1, (len(selected) + columns - 1) // columns)
    sheet = Image.new("RGB", (columns * tile, rows * (tile + caption)), "white")
    draw = ImageDraw.Draw(sheet)
    for ordinal, index in enumerate(selected):
        observation = observations[index]
        image_path = run_root / observation["image"]
        with Image.open(image_path) as source:
            image = source.convert("RGB").resize((tile, tile), Image.Resampling.BILINEAR)
        x = (ordinal % columns) * tile
        y = (ordinal // columns) * (tile + caption)
        sheet.paste(image, (x, y))
        draw.text((x + 4, y + tile + 3), f"frame {index}  travel {observation['cumulativeActorRelativeTravelMm']:.1f} mm", fill="black")
        draw.text((x + 4, y + tile + 21), image_path.name, fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def analyze_run(run_root: Path) -> dict:
    trace = json.loads((run_root / "trace.json").read_text(encoding="utf-8"))
    observations = trace["observations"]
    if len(observations) < 3:
        raise ValueError(f"{run_root}: fewer than three observations")
    features: list[FrameFeature] = []
    for observation in observations:
        image_path = run_root / observation["image"]
        if _sha256(image_path).lower() != observation["imageSha256"].lower():
            raise ValueError(f"image hash mismatch: {image_path}")
        features.append(extract_frame_features(image_path, float(observation["cumulativeActorRelativeTravelMm"])))
    travel = np.asarray([item.travel_mm for item in features], dtype=np.float64)
    bone = moving_average(np.asarray([item.bone_interface for item in features]))
    shadow = moving_average(np.asarray([item.shadow_fraction for item in features]))
    depth = moving_average(np.asarray([item.interface_depth for item in features]))
    morphology = robust_z(bone) + robust_z(shadow) - 0.35 * robust_z(depth)
    transition = np.zeros_like(morphology)
    transition[1:] = np.abs(np.diff(robust_z(bone))) + np.abs(np.diff(robust_z(shadow))) \
        + 0.35 * np.abs(np.diff(robust_z(depth)))
    combined = moving_average(morphology + 0.40 * transition)
    event_indices = detect_candidate_events(travel, combined)
    revised_event_indices = detect_candidate_events_with_boundaries(travel, combined)
    valley_indices = detect_candidate_valleys_with_boundaries(travel, combined)
    transition_indices = detect_candidate_events_with_boundaries(travel, transition)

    references_document = json.loads(
        (run_root / "evaluator_only" / "references.json").read_text(encoding="utf-8")
    )
    references = [(item["level"], float(item["corridorCoordinateMm"])) for item in references_document["references"]]
    reference_coordinates = [coordinate for _, coordinate in references]
    privileged_document = json.loads(
        (run_root / "evaluator_only" / "trace_privileged.json").read_text(encoding="utf-8")
    )
    spacings = np.diff(reference_coordinates)
    tolerance = min(12.0, 0.45 * float(np.min(spacings))) if len(spacings) else 12.0
    matches = ordered_match([float(travel[index]) for index in event_indices], reference_coordinates, tolerance)
    for match in matches:
        match["referenceLevel"] = references[match["referenceIndex"]][0]
    frame_coordinate_matches = ordered_match(
        evaluator_coordinates_for_event_indices(event_indices, privileged_document["observations"]),
        reference_coordinates,
        tolerance,
    )
    for match in frame_coordinate_matches:
        match["referenceLevel"] = references[match["referenceIndex"]][0]
        match["eventEvaluatorCoordinateMm"] = match["eventTravelMm"]
        match["referenceEvaluatorCoordinateMm"] = match["referenceTravelMm"]
    revised_matches = ordered_match(
        evaluator_coordinates_for_event_indices(revised_event_indices, privileged_document["observations"]),
        reference_coordinates,
        tolerance,
    )
    for match in revised_matches:
        match["referenceLevel"] = references[match["referenceIndex"]][0]
        match["eventEvaluatorCoordinateMm"] = match["eventTravelMm"]
        match["referenceEvaluatorCoordinateMm"] = match["referenceTravelMm"]
    dynamic_range = float(np.quantile(combined, 0.90) - np.quantile(combined, 0.10))
    median_error = float(np.median([item["absoluteErrorMm"] for item in matches])) if matches else None
    false_positives = len(event_indices) - len(matches)
    scientific_status = "FEASIBLE" if len(matches) >= 6 and false_positives <= 2 \
        and dynamic_range >= 0.75 else "NOT_DEMONSTRATED"

    with (run_root / "signal.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            ["observation_index", "travel_mm", "bone_interface", "shadow_fraction", "interface_depth",
             "morphology", "transition", "combined", "candidate_event"]
        )
        for index in range(len(travel)):
            writer.writerow(
                [index, travel[index], bone[index], shadow[index], depth[index], morphology[index],
                 transition[index], combined[index], int(index in event_indices)]
            )

    event_document = {
        "detectorContract": "classical_bone_shadow_morphology_v1",
        "baselineThresholdAndMinimumSeparationUnchanged": True,
        "expectedEventCountWasNotUsedByDetector": True,
        "smoothing": "three-frame centered moving average",
        "minimumEventSeparationMm": 12.0,
        "candidateEvents": [
            {
                "observationIndex": index,
                "actorRelativeTravelMm": float(travel[index]),
                "combinedSignal": float(combined[index]),
            }
            for index in event_indices
        ],
        "boundaryCorrectedCandidateEvents": [
            {
                "observationIndex": index,
                "actorRelativeTravelMm": float(travel[index]),
                "combinedSignal": float(combined[index]),
            }
            for index in revised_event_indices
        ],
        "diagnosticNegativeValleys": [
            {
                "observationIndex": index,
                "actorRelativeTravelMm": float(travel[index]),
                "combinedSignal": float(combined[index]),
            }
            for index in valley_indices
        ],
        "diagnosticTransitionEvents": [
            {
                "observationIndex": index,
                "actorRelativeTravelMm": float(travel[index]),
                "transitionSignal": float(transition[index]),
            }
            for index in transition_indices
        ],
    }
    _save_json(run_root / "candidate_events.json", event_document)
    evaluation = {
        "scientificStatus": scientific_status,
        "candidateEventCount": len(event_indices),
        "referenceCount": len(references),
        "orderedMatchedReferenceCount": len(matches),
        "falsePositiveCount": false_positives,
        "matchToleranceMm": tolerance,
        "medianAbsoluteErrorMm": median_error,
        "combinedSignalRobustDynamicRange": dynamic_range,
        "matches": matches,
        "baselineActorCoordinateEvaluation": {
            "candidateEventCount": len(event_indices),
            "orderedMatchedReferenceCount": len(matches),
            "matches": matches,
        },
        "baselineActualFrameCoordinateEvaluation": {
            "coordinateBasis": "corridorCoordinateMm from the corresponding evaluator observation frame",
            "candidateEventCount": len(event_indices),
            "orderedMatchedReferenceCount": len(frame_coordinate_matches),
            "matches": frame_coordinate_matches,
        },
        "boundaryCorrectedActualFrameCoordinateEvaluation": {
            "coordinateBasis": "corridorCoordinateMm from the corresponding evaluator observation frame",
            "candidateEventCount": len(revised_event_indices),
            "orderedMatchedReferenceCount": len(revised_matches),
            "matches": revised_matches,
            "exactSevenEventCount": len(revised_event_indices) == 7,
        },
        "diagnosticCandidateCounts": {
            "negativeValleys": len(valley_indices),
            "transitionEvents": len(transition_indices),
        },
        "signalFeasibility": scientific_status,
        "exactVertebralEnumeration": "NOT_RUN",
        "warning": "Reference coordinates were loaded only after actor-only signal/event extraction and are used solely for evaluation.",
    }
    _save_json(run_root / "g2_evaluation.json", evaluation)
    series = [
        ("bone interface z", robust_z(bone), (0, 95, 190)),
        ("shadow z", robust_z(shadow), (20, 150, 80)),
        ("combined", combined, (215, 70, 35)),
    ]
    _draw_plot(run_root / "signal_plot.png", travel, series, event_indices)
    _draw_plot(run_root / "evaluator_only" / "signal_plot_with_references.png", travel, series,
               event_indices, references)
    selected = sorted(set(event_indices + [0, len(observations) - 1]))
    if len(selected) < 8:
        selected.extend(
            index for index in np.argsort(transition)[::-1]
            if int(index) not in selected
        )
    selected = sorted(set(int(index) for index in selected[:12]))
    _make_contact_sheet(run_root, observations, selected, run_root / "contact_sheet.png")
    return {
        "runRoot": str(run_root),
        "g1Status": json.loads((run_root / "run_summary.json").read_text(encoding="utf-8"))["g1Status"],
        **evaluation,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Analyze real T2-T8 sweep/count frames with classical signals")
    parser.add_argument("--run-root", type=Path, action="append", required=True)
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    results = [analyze_run(path.resolve()) for path in args.run_root]
    feasible = sum(item["scientificStatus"] == "FEASIBLE" for item in results)
    if len(results) < 3:
        overall = "INCONCLUSIVE"
        rationale = "Fewer than three real sweeps; a single good-looking trajectory cannot establish G2 reliability."
    elif feasible >= 2 and all(item["g1Status"] == "PASS" for item in results):
        overall = "PASS"
        rationale = "At least two of three real physical sweeps met the predeclared signal/reference feasibility criteria."
    else:
        overall = "FAIL"
        rationale = "The three-sweep result did not meet the predeclared repeatability criterion."
    combined = {
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "gate": "G2",
        "status": overall,
        "rationale": rationale,
        "g3Status": "NOT_RUN",
        "g4Status": "NOT_RUN",
        "runs": results,
    }
    output_root = args.output_root.resolve() if args.output_root else args.run_root[0].resolve()
    _save_json(output_root / "g2_combined_evaluation.json", combined)
    lines = [
        "# Thoracic longitudinal sweep/count — Milestone 1",
        "",
        f"G2 status: **{overall}**",
        "",
        rationale,
        "",
        "| Run | G1 | Candidate events | Matched T2–T8 refs | False positives | Per-run feasibility |",
        "|---|---:|---:|---:|---:|---|",
    ]
    for result in results:
        lines.append(
            f"| `{Path(result['runRoot']).name}` | {result['g1Status']} | {result['candidateEventCount']} | "
            f"{result['orderedMatchedReferenceCount']}/7 | {result['falsePositiveCount']} | "
            f"{result['scientificStatus']} |"
        )
    lines.extend(
        [
            "",
            "The detector used clean ultrasound pixels and actor-relative travel only. Hidden T-level references were loaded afterward for evaluation.",
            "",
            "G3: NOT_RUN. G4: NOT_RUN.",
        ]
    )
    (output_root / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(combined, indent=2))


if __name__ == "__main__":
    main()
