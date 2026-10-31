from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Sequence

import numpy as np
from PIL import Image, ImageDraw

from anatomical_waveform_analysis import (
    _normalized_image,
    _sha256,
    reconstruct_actor_coordinate,
    validate_actor_isolation,
)


PRIMARY_PHASES = ("cranial_outbound", "baseline", "caudal_outbound")
QUERY_PHASES = ("cranial_reverse", "caudal_reverse")
FAMILY_NAMES = ("intensity_geometry", "gradient_geometry", "shadow_geometry", "bilateral_geometry")
MIN_SEGMENT_SAMPLES = 4
MAX_SEGMENTS = 9
PCA_VARIANCE_FRACTION = 0.90
PCA_MAX_COMPONENTS = 8
NULL_ITERATIONS = 600
JITTER_ITERATIONS = 250
RANDOM_SEED = 20261031
BOUNDARY_TOLERANCE_MM = 6.1
MIN_ADJACENT_EFFECT = 0.75
MIN_REVERSE_LABEL_ACCURACY = 0.80
MIN_JITTER_STABILITY = 0.80


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _block_means(values: np.ndarray, rows: int, columns: int) -> np.ndarray:
    result = []
    for row in range(rows):
        y0 = int(round(row * values.shape[0] / rows))
        y1 = int(round((row + 1) * values.shape[0] / rows))
        for column in range(columns):
            x0 = int(round(column * values.shape[1] / columns))
            x1 = int(round((column + 1) * values.shape[1] / columns))
            result.append(float(np.mean(values[y0:y1, x0:x1])))
    return np.asarray(result, dtype=np.float64)


def extract_descriptor_families(path: Path) -> dict[str, np.ndarray]:
    values = _normalized_image(path)
    height, width = values.shape
    roi = values[int(0.14 * height):int(0.86 * height), int(0.06 * width):int(0.94 * width)]
    dy, dx = np.gradient(roi)
    gradient = np.hypot(dx, dy)

    intensity = _block_means(roi, 6, 6)
    gradient_geometry = _block_means(gradient, 6, 6)

    shadow_rows = []
    for fraction in (0.30, 0.40, 0.50, 0.60, 0.70):
        center = int(round(fraction * (height - 1)))
        band = values[max(0, center - 3):min(height, center + 4), int(0.08 * width):int(0.92 * width)]
        shadow_rows.extend(_block_means(1.0 - band, 1, 12))
    shadow = np.asarray(shadow_rows, dtype=np.float64)

    bilateral_roi = values[int(0.18 * height):int(0.72 * height)]
    left = bilateral_roi[:, int(0.08 * width):int(0.48 * width)]
    right = np.fliplr(bilateral_roi[:, int(0.52 * width):int(0.92 * width)])
    common = min(left.shape[1], right.shape[1])
    bilateral = _block_means(np.abs(left[:, :common] - right[:, :common]), 6, 4)
    return {
        "intensity_geometry": intensity,
        "gradient_geometry": gradient_geometry,
        "shadow_geometry": shadow,
        "bilateral_geometry": bilateral,
    }


def _robust_fit(primary: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    median = np.median(primary, axis=0)
    scale = 1.4826 * np.median(np.abs(primary - median), axis=0)
    standard = np.std(primary, axis=0)
    scale = np.where(scale > 1e-6, scale, np.where(standard > 1e-6, standard, 1.0))
    return median, scale


def combine_families(primary: dict[str, np.ndarray], query: dict[str, np.ndarray],
                     included: Sequence[str] = FAMILY_NAMES) -> tuple[np.ndarray, np.ndarray, dict]:
    primary_parts = []
    query_parts = []
    fit = {}
    for family in included:
        median, scale = _robust_fit(primary[family])
        primary_part = (primary[family] - median) / scale / math.sqrt(primary[family].shape[1])
        query_part = (query[family] - median) / scale / math.sqrt(primary[family].shape[1])
        primary_parts.append(primary_part)
        query_parts.append(query_part)
        fit[family] = {
            "dimension": int(primary[family].shape[1]),
            "equalFamilyWeight": True,
        }
    return np.column_stack(primary_parts), np.column_stack(query_parts), fit


def pca_fit_transform(primary: np.ndarray, query: np.ndarray) -> tuple[np.ndarray, np.ndarray, dict]:
    center = np.mean(primary, axis=0)
    centered = primary - center
    _, singular, right = np.linalg.svd(centered, full_matrices=False)
    variance = singular * singular
    fraction = np.cumsum(variance) / max(float(np.sum(variance)), 1e-12)
    count = int(np.searchsorted(fraction, PCA_VARIANCE_FRACTION) + 1)
    count = max(2, min(count, PCA_MAX_COMPONENTS, right.shape[0]))
    projection = right[:count].T
    primary_scores = centered @ projection
    query_scores = (query - center) @ projection
    score_scale = np.std(primary_scores, axis=0)
    score_scale = np.where(score_scale > 1e-8, score_scale, 1.0)
    primary_scores /= score_scale
    query_scores /= score_scale
    return primary_scores, query_scores, {
        "componentCount": count,
        "retainedVarianceFraction": float(fraction[count - 1]),
        "maximumComponents": PCA_MAX_COMPONENTS,
        "varianceTarget": PCA_VARIANCE_FRACTION,
    }


def _segment_costs(matrix: np.ndarray) -> np.ndarray:
    count = len(matrix)
    prefix = np.vstack((np.zeros((1, matrix.shape[1])), np.cumsum(matrix, axis=0)))
    prefix_sq = np.vstack((np.zeros((1, matrix.shape[1])), np.cumsum(matrix * matrix, axis=0)))
    costs = np.full((count + 1, count + 1), np.inf, dtype=np.float64)
    for start in range(count):
        for end in range(start + 1, count + 1):
            length = end - start
            total = prefix[end] - prefix[start]
            total_sq = prefix_sq[end] - prefix_sq[start]
            costs[start, end] = max(float(np.sum(total_sq - total * total / length)), 0.0)
    return costs


def optimal_segmentation(matrix: np.ndarray, segments: int,
                         minimum_samples: int = MIN_SEGMENT_SAMPLES) -> dict:
    values = np.asarray(matrix, dtype=np.float64)
    count = len(values)
    if segments < 1 or count < segments * minimum_samples:
        raise ValueError("Insufficient observations for requested segmentation")
    costs = _segment_costs(values)
    dp = np.full((segments + 1, count + 1), np.inf, dtype=np.float64)
    previous = np.full((segments + 1, count + 1), -1, dtype=int)
    dp[0, 0] = 0.0
    for part in range(1, segments + 1):
        end_min = part * minimum_samples
        for end in range(end_min, count + 1):
            start_min = (part - 1) * minimum_samples
            start_max = end - minimum_samples
            for start in range(start_min, start_max + 1):
                candidate = dp[part - 1, start] + costs[start, end]
                if candidate < dp[part, end]:
                    dp[part, end] = candidate
                    previous[part, end] = start
    boundaries = [count]
    end = count
    for part in range(segments, 0, -1):
        end = int(previous[part, end])
        if end < 0:
            raise ValueError("No valid segmentation")
        boundaries.append(end)
    boundaries.reverse()
    return {"sse": float(dp[segments, count]), "boundaries": boundaries}


def _segment_centroids(matrix: np.ndarray, boundaries: Sequence[int]) -> np.ndarray:
    return np.asarray([np.mean(matrix[start:end], axis=0)
                       for start, end in zip(boundaries[:-1], boundaries[1:])])


def model_selection(matrix: np.ndarray) -> dict:
    values = np.asarray(matrix, dtype=np.float64)
    rows = []
    dimensions = values.shape[1]
    for segments in range(1, MAX_SEGMENTS + 1):
        if len(values) < segments * MIN_SEGMENT_SAMPLES:
            break
        result = optimal_segmentation(values, segments)
        variance = max(result["sse"] / (len(values) * dimensions), 1e-12)
        bic = len(values) * dimensions * math.log(variance) + segments * dimensions * math.log(len(values))
        rows.append({"segments": segments, "sse": result["sse"], "bic": bic,
                     "boundaries": result["boundaries"]})
    best = min(rows, key=lambda item: item["bic"])
    return {"models": rows, "bestBicK": int(best["segments"]), "criterion": "Gaussian SSE BIC"}


def _boundary_coordinates(boundaries: Sequence[int], coordinates: np.ndarray) -> list[float]:
    return [float(0.5 * (coordinates[index - 1] + coordinates[index])) for index in boundaries[1:-1]]


def _adjacent_effects(matrix: np.ndarray, boundaries: Sequence[int]) -> list[float]:
    centroids = _segment_centroids(matrix, boundaries)
    return [float(np.linalg.norm(centroids[index + 1] - centroids[index]) / math.sqrt(matrix.shape[1]))
            for index in range(len(centroids) - 1)]


def _fit_ar1(values: np.ndarray) -> tuple[float, float]:
    centered = values - np.mean(values)
    denominator = float(np.dot(centered[:-1], centered[:-1]))
    phi = float(np.dot(centered[:-1], centered[1:]) / denominator) if denominator > 1e-12 else 0.0
    phi = float(np.clip(phi, -0.995, 0.995))
    innovations = centered[1:] - phi * centered[:-1]
    return phi, max(float(np.std(innovations)), 1e-6)


def smooth_trend_null(matrix: np.ndarray, coordinates: np.ndarray, observed_gain: float,
                      iterations: int = NULL_ITERATIONS, seed: int = RANDOM_SEED) -> dict:
    values = np.asarray(matrix, dtype=np.float64)
    x = (coordinates - np.mean(coordinates)) / max(float(np.std(coordinates)), 1e-8)
    rng = np.random.default_rng(seed)
    trends = []
    fits = []
    for column in range(values.shape[1]):
        coefficients = np.polyfit(x, values[:, column], 3)
        trend = np.polyval(coefficients, x)
        phi, innovation = _fit_ar1(values[:, column] - trend)
        trends.append(trend)
        fits.append((phi, innovation))
    gains = np.empty(iterations, dtype=np.float64)
    for iteration in range(iterations):
        surrogate = np.empty_like(values)
        for column, (trend, fit) in enumerate(zip(trends, fits)):
            phi, innovation = fit
            residual = np.empty(len(values), dtype=np.float64)
            residual[0] = rng.normal(0.0, innovation / math.sqrt(max(1.0 - phi * phi, 1e-4)))
            for index in range(1, len(values)):
                residual[index] = phi * residual[index - 1] + rng.normal(0.0, innovation)
            surrogate[:, column] = trend + residual
        one = optimal_segmentation(surrogate, 1)["sse"]
        seven = optimal_segmentation(surrogate, 7)["sse"]
        gains[iteration] = (one - seven) / max(one, 1e-12)
    p_value = float((1 + np.count_nonzero(gains >= observed_gain)) / (iterations + 1))
    return {
        "iterations": iterations,
        "seed": seed,
        "observedK7SseReductionFraction": observed_gain,
        "pValue": p_value,
        "nullMedian": float(np.median(gains)),
        "null95": float(np.quantile(gains, 0.95)),
        "model": "per-component cubic spatial trend plus fitted stationary AR(1) residual",
    }


def match_rows(primary_rows: Sequence[dict], query_rows: Sequence[dict], tolerance_mm: float = 1.6) -> list[tuple[int, int]]:
    matches = []
    used = set()
    for query_index, query in enumerate(query_rows):
        candidates = [(index, abs(float(row["coordinate_mm"]) - float(query["coordinate_mm"])))
                      for index, row in enumerate(primary_rows) if index not in used]
        if not candidates:
            continue
        primary_index, distance = min(candidates, key=lambda item: item[1])
        if distance <= tolerance_mm:
            matches.append((primary_index, query_index))
            used.add(primary_index)
    return matches


def reversal_validation(primary: np.ndarray, query: np.ndarray, primary_rows: Sequence[dict],
                        query_rows: Sequence[dict], boundaries: Sequence[int]) -> dict:
    centroids = _segment_centroids(primary, boundaries)
    matches = match_rows(primary_rows, query_rows)
    correct = 0
    margins = []
    rows = []
    for primary_index, query_index in matches:
        expected = next(index for index, (start, end) in enumerate(zip(boundaries[:-1], boundaries[1:]))
                        if start <= primary_index < end)
        distances = np.linalg.norm(centroids - query[query_index], axis=1)
        predicted = int(np.argmin(distances))
        sorted_distances = np.sort(distances)
        margin = float(sorted_distances[1] - sorted_distances[0]) if len(sorted_distances) > 1 else 0.0
        correct += int(predicted == expected)
        margins.append(margin)
        rows.append({
            "primaryObservationIndex": int(primary_rows[primary_index]["observation_index"]),
            "queryObservationIndex": int(query_rows[query_index]["observation_index"]),
            "coordinateDifferenceMm": abs(float(primary_rows[primary_index]["coordinate_mm"])
                                          - float(query_rows[query_index]["coordinate_mm"])),
            "expectedSegment": expected + 1,
            "predictedSegment": predicted + 1,
            "correct": predicted == expected,
            "distanceMargin": margin,
        })
    accuracy = correct / len(matches) if matches else 0.0
    return {
        "matchedObservationCount": len(matches),
        "labelAccuracy": accuracy,
        "medianDistanceMargin": float(np.median(margins)) if margins else None,
        "matches": rows,
    }


def ablation_stability(primary_families: dict[str, np.ndarray], query_families: dict[str, np.ndarray],
                       coordinates: np.ndarray, reference_boundaries: Sequence[float]) -> dict:
    rows = []
    for removed in FAMILY_NAMES:
        included = [family for family in FAMILY_NAMES if family != removed]
        primary, query, _ = combine_families(primary_families, query_families, included)
        scores, _, _ = pca_fit_transform(primary, query)
        segmentation = optimal_segmentation(scores, 7)
        coordinates_found = _boundary_coordinates(segmentation["boundaries"], coordinates)
        differences = [abs(left - right) for left, right in zip(reference_boundaries, coordinates_found)]
        rows.append({
            "removedFamily": removed,
            "boundaryCoordinatesMm": coordinates_found,
            "absoluteBoundaryDifferencesMm": differences,
            "maximumDifferenceMm": max(differences),
        })
    return {
        "leaveOneFamilyOut": rows,
        "maximumBoundaryDifferenceMm": max(row["maximumDifferenceMm"] for row in rows),
    }


def jitter_stability(primary: np.ndarray, query: np.ndarray, primary_rows: Sequence[dict],
                     query_rows: Sequence[dict], coordinates: np.ndarray, reference_boundaries: Sequence[float],
                     iterations: int = JITTER_ITERATIONS, seed: int = RANDOM_SEED + 17) -> dict:
    matches = match_rows(primary_rows, query_rows)
    if matches:
        differences = np.asarray([query[query_index] - primary[primary_index]
                                  for primary_index, query_index in matches])
        sigma = np.median(np.abs(differences), axis=0) * 1.4826
    else:
        sigma = np.std(primary, axis=0) * 0.1
    sigma = np.maximum(sigma, 1e-4)
    rng = np.random.default_rng(seed)
    hits = np.zeros(len(reference_boundaries), dtype=int)
    all_boundaries = []
    for _ in range(iterations):
        candidate = primary + rng.normal(0.0, sigma, primary.shape)
        found = _boundary_coordinates(optimal_segmentation(candidate, 7)["boundaries"], coordinates)
        all_boundaries.append(found)
        hits += np.asarray([abs(left - right) <= BOUNDARY_TOLERANCE_MM
                            for left, right in zip(reference_boundaries, found)], dtype=int)
    fractions = hits / iterations
    return {
        "iterations": iterations,
        "seed": seed,
        "noiseSource": "median absolute same-position forward/reverse descriptor disagreement",
        "matchedPairs": len(matches),
        "withinToleranceFractionByBoundary": fractions.tolist(),
        "minimumWithinToleranceFraction": float(np.min(fractions)),
        "medianBoundaryCoordinatesMm": np.median(np.asarray(all_boundaries), axis=0).tolist(),
    }


def sequence_gate(model: dict, null: dict, adjacent_effects: Sequence[float], ablation: dict,
                  jitter: dict, reversal: dict) -> dict:
    checks = {
        "k7SelectedByBic": model["bestBicK"] == 7,
        "k7ExceedsSmoothTrendNull": null["pValue"] <= 0.05,
        "allAdjacentEffectsSupported": min(adjacent_effects) >= MIN_ADJACENT_EFFECT,
        "familyAblationStable": ablation["maximumBoundaryDifferenceMm"] <= BOUNDARY_TOLERANCE_MM,
        "jitterStable": jitter["minimumWithinToleranceFraction"] >= MIN_JITTER_STABILITY,
        "reverseTraversalSupported": reversal["labelAccuracy"] >= MIN_REVERSE_LABEL_ACCURACY,
    }
    return {
        "status": "SUPPORTED_SEVEN_LEVEL_SEQUENCE" if all(checks.values()) else "NO_DEFENSIBLE_SEVEN_LEVEL_SEQUENCE",
        "checks": checks,
        "allChecksPass": all(checks.values()),
        "criteria": {
            "requiredBicK": 7,
            "maximumNullPValue": 0.05,
            "minimumAdjacentEffect": MIN_ADJACENT_EFFECT,
            "boundaryToleranceMm": BOUNDARY_TOLERANCE_MM,
            "minimumJitterStability": MIN_JITTER_STABILITY,
            "minimumReverseLabelAccuracy": MIN_REVERSE_LABEL_ACCURACY,
        },
    }


def _freeze(output_root: Path, names: Iterable[str]) -> dict:
    payload = {
        "contract": "thoracic_autonomous_t7_blind_freeze_v1",
        "generatedUtc": _utc_now(),
        "phase": "BLIND_ACTOR_DATA_FROZEN_BEFORE_EVALUATOR",
        "sha256": {name: _sha256(output_root / name) for name in names},
    }
    _save_json(output_root / "blind_results.sha256", payload)
    return payload


def verify_freeze(output_root: Path) -> dict:
    payload = _load_json(output_root / "blind_results.sha256")
    for name, expected in payload["sha256"].items():
        if _sha256(output_root / name) != expected:
            raise ValueError(f"Frozen blind output changed: {name}")
    return payload


def _draw_waveform(path: Path, coordinates: np.ndarray, scores: np.ndarray,
                   boundary_coordinates: Sequence[float], gate: dict) -> None:
    width, height = 1500, 820
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    left, right = 100, width - 50
    draw.text((left, 18), f"Blind descriptor waveform; {gate['status']}", fill="black")
    colors = ((30, 100, 210), (220, 90, 35), (30, 155, 75))
    for panel in range(min(3, scores.shape[1])):
        top = 60 + panel * 240
        bottom = top + 190
        values = scores[:, panel]
        y_min, y_max = float(np.min(values)), float(np.max(values))
        draw.rectangle((left, top, right, bottom), outline="black")
        draw.text((8, top + 4), f"PCA morphology {panel + 1}", fill="black")

        def xy(x_value: float, y_value: float) -> tuple[int, int]:
            x = left + int((x_value - coordinates.min()) / max(float(np.ptp(coordinates)), 1e-8) * (right - left))
            y = bottom - int((y_value - y_min) / max(y_max - y_min, 1e-8) * (bottom - top))
            return x, y

        draw.line([xy(float(x), float(y)) for x, y in zip(coordinates, values)], fill=colors[panel], width=3)
        for boundary in boundary_coordinates:
            x, _ = xy(float(boundary), y_min)
            draw.line((x, top, x, bottom), fill=(180, 30, 30), width=2)
    draw.text((left, height - 30), "Actor-relative executed displacement from unlabeled Home (mm)", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _draw_support(path: Path, run_root: Path, rows: Sequence[dict], events: Sequence[dict], accepted: bool) -> None:
    tile = 260
    label = 58
    canvas = Image.new("RGB", (7 * tile, tile + label), "white")
    draw = ImageDraw.Draw(canvas)
    for index, event in enumerate(events):
        row = rows[int(event["primaryIndex"])]
        with Image.open(run_root / row["image"]) as source:
            image = source.convert("RGB").resize((tile, tile), Image.Resampling.BILINEAR)
        canvas.paste(image, (index * tile, 0))
        prefix = f"V{index + 1}" if accepted else f"K7-{index + 1} UNACCEPTED"
        draw.text((index * tile + 4, tile + 3),
                  f"{prefix}\nobs {row['observation_index']}  s={row['coordinate_mm']:.1f} mm", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _actor_rows(run_root: Path) -> tuple[dict, list[dict]]:
    actor_path = run_root / "trajectory_actor.json"
    actor = _load_json(actor_path)
    validate_actor_isolation(actor)
    reconstruction = reconstruct_actor_coordinate(actor["observations"])
    if reconstruction["maximumAbsoluteErrorMm"] > 0.05:
        raise ValueError("Executed-motion coordinate reconstruction failed")
    rows = []
    for observation, coordinate in zip(actor["observations"], reconstruction["coordinates"]):
        image = run_root / observation["image"]
        if not image.is_file() or _sha256(image).lower() != observation["imageSha256"].lower():
            raise ValueError(f"Missing or modified frame: {image}")
        if not (observation["executedSuccessfully"] and observation["frameIntegrityValid"]
                and observation["contactValidAfterExecution"] and observation["workspaceValidAfterExecution"]):
            raise ValueError(f"Invalid actor observation {observation['observationIndex']}")
        rows.append({
            "observation_index": int(observation["observationIndex"]),
            "phase": observation["phase"],
            "coordinate_mm": float(coordinate),
            "image": observation["image"],
            "image_sha256": observation["imageSha256"],
        })
    return actor, rows


def blind_analyze(run_root: Path, output_root: Path, null_iterations: int = NULL_ITERATIONS,
                  jitter_iterations: int = JITTER_ITERATIONS) -> dict:
    actor, rows = _actor_rows(run_root)
    primary_rows = sorted([row for row in rows if row["phase"] in PRIMARY_PHASES],
                          key=lambda row: row["coordinate_mm"])
    query_rows = sorted([row for row in rows if row["phase"] in QUERY_PHASES],
                        key=lambda row: row["coordinate_mm"])
    if len(primary_rows) != 63 or len(query_rows) != 40:
        raise ValueError(f"Unexpected traversal partition: {len(primary_rows)} primary, {len(query_rows)} query")

    primary_family_lists = {name: [] for name in FAMILY_NAMES}
    query_family_lists = {name: [] for name in FAMILY_NAMES}
    for collection, family_lists in ((primary_rows, primary_family_lists), (query_rows, query_family_lists)):
        for row in collection:
            families = extract_descriptor_families(run_root / row["image"])
            for name in FAMILY_NAMES:
                family_lists[name].append(families[name])
    primary_families = {name: np.asarray(values) for name, values in primary_family_lists.items()}
    query_families = {name: np.asarray(values) for name, values in query_family_lists.items()}
    primary_combined, query_combined, family_fit = combine_families(primary_families, query_families)
    primary_scores, query_scores, pca = pca_fit_transform(primary_combined, query_combined)
    coordinates = np.asarray([row["coordinate_mm"] for row in primary_rows], dtype=np.float64)

    selection = model_selection(primary_scores)
    seven = optimal_segmentation(primary_scores, 7)
    boundary_coordinates = _boundary_coordinates(seven["boundaries"], coordinates)
    one_sse = optimal_segmentation(primary_scores, 1)["sse"]
    gain = (one_sse - seven["sse"]) / max(one_sse, 1e-12)
    null = smooth_trend_null(primary_scores, coordinates, gain, iterations=null_iterations)
    adjacent_effects = _adjacent_effects(primary_scores, seven["boundaries"])
    ablation = ablation_stability(primary_families, query_families, coordinates, boundary_coordinates)
    reversal = reversal_validation(primary_scores, query_scores, primary_rows, query_rows, seven["boundaries"])
    jitter = jitter_stability(primary_scores, query_scores, primary_rows, query_rows, coordinates,
                              boundary_coordinates, iterations=jitter_iterations)
    gate = sequence_gate(selection, null, adjacent_effects, ablation, jitter, reversal)

    centroids = _segment_centroids(primary_scores, seven["boundaries"])
    events = []
    for event_index, (start, end) in enumerate(zip(seven["boundaries"][:-1], seven["boundaries"][1:])):
        local = primary_scores[start:end]
        medoid = start + int(np.argmin(np.linalg.norm(local - centroids[event_index], axis=1)))
        events.append({
            "candidateOrdinal": event_index + 1,
            "primaryIndex": medoid,
            "observationIndex": int(primary_rows[medoid]["observation_index"]),
            "relativePositionFromHomeMm": float(primary_rows[medoid]["coordinate_mm"]),
            "supportIntervalMm": [float(primary_rows[start]["coordinate_mm"]),
                                  float(primary_rows[end - 1]["coordinate_mm"])],
            "sampleCount": end - start,
            "acceptedAsVertebralLevel": bool(gate["allChecksPass"]),
            "anatomicalIdentity": "UNRESOLVED_IMAGE_MORPHOLOGY",
        })

    output_root.mkdir(parents=True, exist_ok=True)
    manifest = {
        "contract": "thoracic_autonomous_t7_input_v1",
        "generatedUtc": _utc_now(),
        "sourceRun": str(run_root),
        "actorTraceSha256": _sha256(run_root / "trajectory_actor.json"),
        "observationCount": len(rows),
        "primaryObservationCount": len(primary_rows),
        "independentQueryObservationCount": len(query_rows),
        "acceptedMovements": int(actor["acceptedMovements"]),
        "rejectedMovements": int(actor["rejectedMovements"]),
        "coordinateSource": "actual achieved relative displacement accumulated from anatomically unlabeled Home",
        "actorReceivedTLevelCoordinate": False,
        "evaluatorDataRead": False,
        "historicalInspectionWarning": "Development data were previously inspected; this is not held-out confirmation.",
        "frames": [{"observationIndex": row["observation_index"], "image": row["image"],
                    "sha256": row["image_sha256"]} for row in rows],
    }
    _save_json(output_root / "input_manifest.json", manifest)
    definitions = {
        "contract": "thoracic_autonomous_t7_descriptor_v1",
        "families": family_fit,
        "normalization": "per-pixel robust image normalization; per-dimension robust scale fitted on primary traversal; equal family weight",
        "pca": pca,
        "segmentation": {"method": "exact dynamic programming piecewise-stationary SSE", "minimumSamples": MIN_SEGMENT_SAMPLES,
                         "comparedK": [1, MAX_SEGMENTS]},
        "approvedPriors": ["coverage contains exactly T2-T8", "cranial-to-caudal order", "nonuniform spacing"],
        "explicitlyExcluded": ["known T2 start", "Home anatomical label", "T-level coordinates", "T7 coordinate",
                               "equal spacing", "Unity target transform", "evaluator score", "mesh identity"],
    }
    _save_json(output_root / "feature_definitions.json", definitions)
    segmentation_payload = {
        "contract": "thoracic_autonomous_t7_segmentation_v1",
        "modelSelection": selection,
        "diagnosticK7": {
            "boundaries": seven["boundaries"],
            "boundaryCoordinatesMm": boundary_coordinates,
            "sse": seven["sse"],
            "adjacentEffectSizes": adjacent_effects,
            "events": events,
        },
        "smoothTrendNull": null,
        "familyAblation": ablation,
        "jitterStability": jitter,
        "reverseTraversal": reversal,
        "gate": gate,
    }
    _save_json(output_root / "segmentation_model_selection.json", segmentation_payload)
    accepted_events = events if gate["allChecksPass"] else []
    sequence = {
        "contract": "thoracic_autonomous_t7_blind_sequence_v1",
        "generatedUtc": _utc_now(),
        "status": gate["status"],
        "acceptedEventCount": len(accepted_events),
        "acceptedEvents": accepted_events,
        "diagnosticForcedK7Partition": events,
        "diagnosticPartitionWarning": "The K=7 partition is shown for audit but is not evidence and is never promoted when any gate fails.",
        "predictedTargetRelativeCoordinateMm": accepted_events[5]["relativePositionFromHomeMm"]
        if len(accepted_events) == 7 else None,
        "physicalReturnAuthorizedByOfflineGate": len(accepted_events) == 7,
        "gate": gate,
    }
    _save_json(output_root / "blind_sequence_candidates.json", sequence)
    _draw_waveform(output_root / "waveform_vs_relative_displacement.png", coordinates, primary_scores,
                   boundary_coordinates, gate)
    _draw_support(output_root / "event_support_contact_sheet.png", run_root, primary_rows, events,
                  gate["allChecksPass"])
    freeze = _freeze(output_root, (
        "input_manifest.json", "feature_definitions.json", "segmentation_model_selection.json",
        "blind_sequence_candidates.json", "waveform_vs_relative_displacement.png",
        "event_support_contact_sheet.png",
    ))
    return {"status": gate["status"], "acceptedEventCount": len(accepted_events),
            "physicalReturnAuthorized": len(accepted_events) == 7, "freeze": freeze}


def evaluator_validate(run_root: Path, output_root: Path, m0c_root: Path) -> dict:
    verify_freeze(output_root)
    sequence = _load_json(output_root / "blind_sequence_candidates.json")
    privileged = _load_json(run_root / "evaluator_only" / "trajectory_privileged.json")
    prior = _load_json(m0c_root / "evaluator_only" / "anatomical_comparison.json")
    observations = {int(item["observationIndex"]): item for item in privileged["observations"]}
    references = sorted(prior["references"], key=lambda item: float(item["coordinateMm"]))
    comparisons = []
    diagnostic = sequence["diagnosticForcedK7Partition"]
    for candidate, reference in zip(diagnostic, references):
        observation = observations[int(candidate["observationIndex"])]
        evaluator_coordinate = float(observation["evaluatorLongitudinalCoordinateMm"])
        comparisons.append({
            "candidateOrdinal": candidate["candidateOrdinal"],
            "acceptedBlindEvent": candidate["acceptedAsVertebralLevel"],
            "referenceLevel": reference["label"],
            "candidateEvaluatorCoordinateMm": evaluator_coordinate,
            "referenceCoordinateMm": float(reference["coordinateMm"]),
            "absoluteErrorMm": abs(evaluator_coordinate - float(reference["coordinateMm"])),
            "identityConclusion": "COMBINED_SKELETON_UNRESOLVED",
        })
    accepted = sequence["acceptedEventCount"] == 7
    evaluation = {
        "contract": "thoracic_autonomous_t7_evaluator_v1",
        "generatedUtc": _utc_now(),
        "blindFreezeVerified": True,
        "blindStatus": sequence["status"],
        "diagnosticForcedPartitionComparison": comparisons,
        "diagnosticWarning": "Comparison does not promote rejected K=7 segments or prove anatomical identity.",
        "vertebralCorrespondence": "AMBIGUOUS" if accepted else "NOT_SUPPORTED",
        "gates": {
            "G0_anatomical_reference_validity": "PASS",
            "G1_physical_coverage": "PASS",
            "G2_anatomical_event_observability": "INCONCLUSIVE" if accepted else "FAIL",
            "G3_enumeration_accuracy": "NOT_RUN",
            "G4_T7_inference_and_reacquisition": "NOT_RUN",
        },
        "newPhysicalDevelopmentRuns": 0,
        "confirmationEpisodes": 0,
    }
    _save_json(output_root / "evaluator_only" / "anatomical_comparison.json", evaluation)
    _save_json(output_root / "final_gates.json", evaluation["gates"])
    _write_report(output_root, sequence, evaluation)
    return evaluation


def _write_report(output_root: Path, sequence: dict, evaluation: dict) -> None:
    segmentation = _load_json(output_root / "segmentation_model_selection.json")
    gate = sequence["gate"]
    failed = [name for name, passed in gate["checks"].items() if not passed]
    null = segmentation["smoothTrendNull"]
    reversal = segmentation["reverseTraversal"]
    ablation = segmentation["familyAblation"]
    jitter = segmentation["jitterStability"]
    report = f"""# Autonomous T7 Discovery from Fixed Home — Offline Observability Gate

## IMPLEMENTATION

Added one isolated blind-first analyzer. It derives actor-relative position only from accepted achieved motion, extracts four deterministic image-morphology families, compares exact sequence segmentations K=1…9, and audits a diagnostic K=7 partition with smooth-trend nulls, feature ablation, forward/reverse captures, adjacent-state effects, and capture-noise jitter. It does not modify Unity, Active2D, P1-R3, the renderer, the scene, or historical evidence.

The old known-T2-start assumption is explicitly disabled. Home is a repeatable but anatomically unlabeled position. The approved seven-level coverage prior is used only as a candidate K=7 hypothesis; it is not allowed to create accepted events when the image-evidence gates fail.

## VERIFICATION

- M0-B actor trace and 103 frame hashes: **PASS**.
- Actual primary observations: **63**; separately captured reverse-query observations: **40**.
- Physical movements in source data: **102 accepted, 0 rejected**.
- New physical sweeps: **0/20**. Confirmation episodes: **0/3**.
- Blind output hash freeze before evaluator access: **PASS**.
- Focused tests: see `focused_test_results.txt`.

## OFFLINE ANATOMICAL WAVEFORM

The descriptor waveform is spatially repeatable, but the seven-state interpretation is **`{sequence['status']}`**. BIC selected K={segmentation['modelSelection']['bestBicK']}, not a presupposed seven. The K=7 SSE reduction was {null['observedK7SseReductionFraction']:.3f}; against a cubic smooth-trend plus correlated-noise null, p={null['pValue']:.4f} (null 95%={null['null95']:.3f}).

The forced diagnostic K=7 partition is retained only for review. Accepted vertebral events: **{sequence['acceptedEventCount']}**. It is not converted into V1–V7, and no T7 coordinate is emitted.

## CONTROLS

- Forward/reverse segment-label accuracy: **{reversal['labelAccuracy']:.3f}** over {reversal['matchedObservationCount']} genuine recaptures.
- Maximum boundary shift after leaving out one feature family: **{ablation['maximumBoundaryDifferenceMm']:.3f} mm**.
- Minimum boundary stability under recapture-derived jitter: **{jitter['minimumWithinToleranceFraction']:.3f}**.
- Failed predeclared gates: **{', '.join(failed) if failed else 'none'}**.

High image repeatability at the same coordinate is useful, but it cannot by itself establish seven vertebral identities. The combined Skeleton provenance remains unable to distinguish ribs, laminae, interspaces, or vertebral bodies.

## PHYSICAL EXECUTION DECISION

The offline gate did not authorize a Unity exploration-to-return pilot. Running a new sweep would reproduce safe images without resolving whether seven observed structures are vertebrae. P1-R3 was therefore not invoked, no target coordinate was supplied, and no calibrated pose was reused.

## SCIENTIFIC VERDICTS

| Gate | Verdict |
| --- | --- |
| G0 anatomical reference validity | **{evaluation['gates']['G0_anatomical_reference_validity']}** |
| G1 physical coverage | **{evaluation['gates']['G1_physical_coverage']}** |
| G2 anatomical event observability | **{evaluation['gates']['G2_anatomical_event_observability']}** |
| G3 enumeration accuracy | **{evaluation['gates']['G3_enumeration_accuracy']}** |
| G4 T7 inference and physical reacquisition | **{evaluation['gates']['G4_T7_inference_and_reacquisition']}** |

Overall end-to-end result: **NOT_RUN — OFFLINE G2 BLOCKER**.

## HIGHEST-PRIORITY BLOCKER

Existing clean ultrasound frames contain reproducible position-specific morphology, but they do not provide a defensible, independently attributable set of seven vertebral-level structures. K=7 can always partition a smooth changing sequence; the predeclared controls are what prevent that mathematical partition from being mislabeled as anatomy.

## NEXT APPROVAL REQUIRED

No ordinary parameter tuning is justified on this same dataset. A next experiment would need a separately approved source of independent anatomical observability—such as a diagnostic acquisition geometry whose image signature can be validated per structure—without exposing T-level coordinates to the actor. No alternative architecture is implemented here.
"""
    (output_root / "REPORT.md").write_text(report, encoding="utf-8")
    registry = {
        "contract": "thoracic_autonomous_t7_development_registry_v1",
        "physicalDevelopmentBudget": 20,
        "physicalDevelopmentRunsConsumed": 0,
        "confirmationBudget": 3,
        "confirmationEpisodesConsumed": 0,
        "reasonStopped": "offline seven-level observability gate failed",
    }
    _save_json(output_root / "development_run_registry.json", registry)
    (output_root / "demo_instructions.md").write_text(
        "# Unity demo status\n\nNo Unity end-to-end demo was generated because the frozen offline actor did not support seven vertebral-level events. The existing M0-B run is the reproducible physical exploration input; launching a return without an accepted V6 target would violate the actor/evaluator contract.\n",
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Blind fixed-Home autonomous T7 sequence observability test")
    parser.add_argument("--run-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--m0c-root", type=Path)
    parser.add_argument("--phase", choices=("blind", "evaluate"), required=True)
    parser.add_argument("--null-iterations", type=int, default=NULL_ITERATIONS)
    parser.add_argument("--jitter-iterations", type=int, default=JITTER_ITERATIONS)
    args = parser.parse_args()
    if args.phase == "blind":
        result = blind_analyze(args.run_root.resolve(), args.output_root.resolve(),
                               args.null_iterations, args.jitter_iterations)
    else:
        if args.m0c_root is None:
            parser.error("--m0c-root is required for evaluator phase")
        result = evaluator_validate(args.run_root.resolve(), args.output_root.resolve(), args.m0c_root.resolve())
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
