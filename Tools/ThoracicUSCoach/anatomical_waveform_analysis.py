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

from sweep_count_analysis import (
    detect_candidate_events_with_boundaries,
    extract_frame_features,
    moving_average,
    robust_z,
)


IMAGE_SIZE = 256
DEPTH_FRACTIONS = (0.36, 0.48, 0.60)
SHADOW_LATERAL_FRACTION = 0.55
SHADOW_MIN_WIDTH_FRACTION = 0.035
INTERFACE_X_FRACTIONS = (0.12, 0.18, 0.24, 0.30, 0.36, 0.42, 0.58, 0.64, 0.70, 0.76, 0.82, 0.88)
SMOOTHING_FRAMES = 3
PERIOD_LAG_SAMPLES = tuple(range(3, 13))
PERIOD_NULL_ITERATIONS = 400
PERIOD_RANDOM_SEED = 73129
PERIOD_MIN_CORRELATION = 0.35
PERIOD_MIN_PROMINENCE = 0.12
PERIOD_MAX_P_VALUE = 0.05
REVERSE_MATCH_TOLERANCE_MM = 1.6
MIN_RELIABLE_COVERAGE = 0.85
PRIMARY_PHASES = ("caudal_reverse", "cranial_outbound")
FEATURE_NAMES = ("shadow_width", "interface_arch_height", "bilateral_symmetry")
FORBIDDEN_ACTOR_KEYS = {
    "probeposition",
    "proberotation",
    "contactpoint",
    "surfacenormal",
    "evaluatorlongitudinalcoordinatemm",
    "diagnosticreferencelevel",
    "provenance",
    "meshidentity",
    "tlevel",
}


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_actor_isolation(actor: dict) -> None:
    def visit(value: object, path: str) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                normalized = str(key).replace("_", "").lower()
                if any(forbidden in normalized for forbidden in FORBIDDEN_ACTOR_KEYS):
                    raise ValueError(f"Privileged actor field at {path}{key}")
                visit(child, f"{path}{key}.")
        elif isinstance(value, list):
            for index, child in enumerate(value):
                visit(child, f"{path}[{index}].")

    visit(actor, "")


def _box_smooth(values: np.ndarray, width: int) -> np.ndarray:
    array = np.asarray(values, dtype=np.float64)
    if width <= 1 or len(array) < width:
        return array.copy()
    left = width // 2
    right = width - 1 - left
    padded = np.pad(array, (left, right), mode="edge")
    return np.convolve(padded, np.ones(width, dtype=np.float64) / width, mode="valid")


def _normalized_image(path: Path, size: int = IMAGE_SIZE) -> np.ndarray:
    with Image.open(path) as image:
        values = np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR),
            dtype=np.float64,
        ) / 255.0
    roi = values[int(0.12 * size):int(0.88 * size), int(0.05 * size):int(0.95 * size)]
    low, high = np.quantile(roi, (0.02, 0.98))
    return np.clip((values - low) / max(float(high - low), 1e-6), 0.0, 1.0)


def measure_central_shadow(values: np.ndarray) -> dict:
    height, width = values.shape
    measurements: list[dict] = []
    for depth_fraction in DEPTH_FRACTIONS:
        center_y = int(round(depth_fraction * (height - 1)))
        half_band = max(2, int(round(0.018 * height)))
        band = values[max(0, center_y - half_band):min(height, center_y + half_band + 1)]
        profile = _box_smooth(np.mean(band, axis=0), 9)
        lateral = np.concatenate((profile[int(0.08 * width):int(0.38 * width)],
                                  profile[int(0.62 * width):int(0.92 * width)]))
        lateral_reference = float(np.median(lateral))
        threshold = float(np.clip(SHADOW_LATERAL_FRACTION * lateral_reference, 0.10, 0.46))
        search_start, search_end = int(0.34 * width), int(0.66 * width)
        candidates = np.arange(search_start, search_end)
        penalty = 0.08 * np.abs(candidates / max(width - 1, 1) - 0.5)
        seed = int(candidates[int(np.argmin(profile[candidates] + penalty))])
        left = seed
        while left > int(0.16 * width) and profile[left - 1] < threshold:
            left -= 1
        right = seed
        while right < int(0.84 * width) and profile[right + 1] < threshold:
            right += 1
        width_fraction = float((right - left + 1) / width)
        midpoint = float((left + right) / (2.0 * max(width - 1, 1)))
        left_support = float(np.median(profile[max(0, left - int(0.08 * width)):left])) if left else 0.0
        right_support = float(np.median(profile[right + 1:min(width, right + 1 + int(0.08 * width))]))
        valid = (
            width_fraction >= SHADOW_MIN_WIDTH_FRACTION
            and width_fraction <= 0.48
            and abs(midpoint - 0.5) <= 0.18
            and left_support >= threshold
            and right_support >= threshold
        )
        measurements.append({
            "depthFraction": depth_fraction,
            "y": center_y,
            "left": left,
            "right": right,
            "widthFraction": width_fraction,
            "midpointFraction": midpoint,
            "threshold": threshold,
            "valid": bool(valid),
        })
    valid_rows = [row for row in measurements if row["valid"]]
    if not valid_rows:
        return {
            "valid": False,
            "validDepthCount": 0,
            "widthMean": float("nan"),
            "centroidOffset": float("nan"),
            "taper": float("nan"),
            "depths": measurements,
        }
    widths = [row["widthFraction"] for row in valid_rows]
    centroids = [row["midpointFraction"] - 0.5 for row in valid_rows]
    taper = widths[-1] - widths[0] if len(widths) >= 2 else 0.0
    return {
        "valid": len(valid_rows) >= 2,
        "validDepthCount": len(valid_rows),
        "widthMean": float(np.mean(widths)),
        "centroidOffset": float(np.mean(centroids)),
        "taper": float(taper),
        "depths": measurements,
    }


def measure_bright_interface(values: np.ndarray) -> dict:
    height, width = values.shape
    rows: list[dict] = []
    for x_fraction in INTERFACE_X_FRACTIONS:
        center_x = int(round(x_fraction * (width - 1)))
        half_band = max(2, int(round(0.018 * width)))
        patch = values[:, max(0, center_x - half_band):min(width, center_x + half_band + 1)]
        profile = np.quantile(patch, 0.75, axis=1)
        profile = _box_smooth(profile, 5)
        future = np.concatenate((profile[5:], np.repeat(profile[-1], 5)))
        downward_drop = np.maximum(profile - future, 0.0)
        y_start, y_end = int(0.38 * height), int(0.82 * height)
        y_values = np.arange(y_start, y_end)
        depth_bias = 0.04 * (y_values - y_start) / max(y_end - y_start - 1, 1)
        score = 0.45 * profile[y_values] + 1.40 * downward_drop[y_values] + depth_bias
        local = int(np.argmax(score))
        y = int(y_values[local])
        peak_score = float(score[local])
        rows.append({
            "xFraction": x_fraction,
            "x": center_x,
            "y": y,
            "depthFraction": float(y / max(height - 1, 1)),
            "score": peak_score,
            "valid": peak_score >= 0.18,
        })
    left = [row for row in rows[:6] if row["valid"]]
    right = [row for row in rows[6:] if row["valid"]]
    valid = len(left) >= 4 and len(right) >= 4
    if not valid:
        return {
            "valid": False,
            "validPointCount": len(left) + len(right),
            "depthMean": float("nan"),
            "archHeight": float("nan"),
            "leftRightDepthDifference": float("nan"),
            "continuity": 0.0,
            "points": rows,
        }
    left_depths = np.array([row["depthFraction"] for row in left], dtype=np.float64)
    right_depths = np.array([row["depthFraction"] for row in right], dtype=np.float64)
    inner = np.mean((left_depths[-2:].mean(), right_depths[:2].mean()))
    outer = np.mean((left_depths[:2].mean(), right_depths[-2:].mean()))
    all_depths = np.concatenate((left_depths, right_depths))
    jumps = np.concatenate((np.abs(np.diff(left_depths)), np.abs(np.diff(right_depths))))
    continuity = float(np.mean(jumps <= 0.12)) if len(jumps) else 0.0
    return {
        "valid": True,
        "validPointCount": len(all_depths),
        "depthMean": float(np.mean(all_depths)),
        "archHeight": float(outer - inner),
        "leftRightDepthDifference": float(left_depths.mean() - right_depths.mean()),
        "continuity": continuity,
        "points": rows,
    }


def measure_bilateral_structure(values: np.ndarray) -> dict:
    height, width = values.shape
    y0, y1 = int(0.18 * height), int(0.70 * height)
    left = values[y0:y1, int(0.08 * width):int(0.46 * width)]
    right = np.fliplr(values[y0:y1, int(0.54 * width):int(0.92 * width)])
    common_width = min(left.shape[1], right.shape[1])
    left, right = left[:, :common_width], right[:, :common_width]
    left_flat, right_flat = left.ravel(), right.ravel()
    if np.std(left_flat) < 1e-6 or np.std(right_flat) < 1e-6:
        correlation = 0.0
    else:
        correlation = float(np.corrcoef(left_flat, right_flat)[0, 1])
    left_coverage = float(np.mean(left >= 0.28))
    right_coverage = float(np.mean(right >= 0.28))
    return {
        "valid": True,
        "symmetry": correlation,
        "leftCoverage": left_coverage,
        "rightCoverage": right_coverage,
        "coverageAsymmetry": float(left_coverage - right_coverage),
    }


def extract_morphology(path: Path) -> tuple[dict, dict]:
    values = _normalized_image(path)
    shadow = measure_central_shadow(values)
    interface = measure_bright_interface(values)
    bilateral = measure_bilateral_structure(values)
    row = {
        "shadow_valid": bool(shadow["valid"]),
        "shadow_width": shadow["widthMean"],
        "shadow_centroid_offset": shadow["centroidOffset"],
        "shadow_taper": shadow["taper"],
        "interface_valid": bool(interface["valid"]),
        "interface_depth": interface["depthMean"],
        "interface_arch_height": interface["archHeight"],
        "interface_left_right_difference": interface["leftRightDepthDifference"],
        "interface_continuity": interface["continuity"],
        "bilateral_valid": bool(bilateral["valid"]),
        "bilateral_symmetry": bilateral["symmetry"],
        "bilateral_coverage_asymmetry": bilateral["coverageAsymmetry"],
    }
    overlay = {"shadow": shadow, "interface": interface, "bilateral": bilateral}
    return row, overlay


def reconstruct_actor_coordinate(observations: Sequence[dict]) -> dict:
    coordinate = 0.0
    maximum_error = 0.0
    reconstructed: list[float] = []
    for observation in observations:
        action = observation["requestedAction"].upper()
        achieved = float(observation["achievedDisplacementMm"])
        if action == "CAUDAL":
            coordinate += achieved
        elif action == "CRANIAL":
            coordinate -= achieved
        elif action != "OBSERVE":
            raise ValueError(f"Unsupported action {action}")
        expected = float(observation["signedActorRelativeMm"])
        maximum_error = max(maximum_error, abs(coordinate - expected))
        reconstructed.append(coordinate)
    return {"coordinates": reconstructed, "maximumAbsoluteErrorMm": maximum_error}


def _interpolate_missing(rows: list[dict], feature: str) -> np.ndarray:
    values = np.asarray([float(row[feature]) for row in rows], dtype=np.float64)
    valid = np.isfinite(values)
    if np.count_nonzero(valid) < 2:
        return values
    indices = np.arange(len(values), dtype=np.float64)
    values[~valid] = np.interp(indices[~valid], indices[valid], values[valid])
    return values


def robust_standardize(values: np.ndarray) -> np.ndarray:
    array = np.asarray(values, dtype=np.float64)
    median = float(np.median(array))
    mad = float(np.median(np.abs(array - median)))
    return (array - median) / max(1.4826 * mad, 1e-6)


def _correlation(left: np.ndarray, right: np.ndarray) -> float:
    if len(left) < 3 or np.std(left) < 1e-8 or np.std(right) < 1e-8:
        return 0.0
    return float(np.corrcoef(left, right)[0, 1])


def periodicity_test(matrix: np.ndarray, spacing_mm: float, iterations: int = PERIOD_NULL_ITERATIONS) -> dict:
    values = np.asarray(matrix, dtype=np.float64)
    if values.ndim == 1:
        values = values[:, None]
    standardized = np.column_stack([robust_standardize(values[:, column]) for column in range(values.shape[1])])
    lags = np.array([lag for lag in PERIOD_LAG_SAMPLES if lag < len(standardized) - 2], dtype=int)
    if len(lags) < 3:
        return {"status": "NOT_SUPPORTED", "reason": "insufficient samples", "lags": []}

    def acf(candidate: np.ndarray) -> np.ndarray:
        result = []
        for lag in lags:
            correlations = [_correlation(candidate[:-lag, column], candidate[lag:, column])
                            for column in range(candidate.shape[1])]
            result.append(float(np.mean(correlations)))
        return np.asarray(result, dtype=np.float64)

    observed = acf(standardized)
    local_indices = [index for index in range(1, len(observed) - 1)
                     if observed[index] > observed[index - 1] and observed[index] >= observed[index + 1]]
    if local_indices:
        peak_index = max(local_indices, key=lambda index: float(observed[index]))
        local_peak = True
    else:
        peak_index = int(np.argmax(observed))
        local_peak = False
    peak = float(observed[peak_index])
    prominence = float(peak - np.median(observed))

    rng = np.random.default_rng(PERIOD_RANDOM_SEED)
    null_maxima: list[float] = []
    for _ in range(iterations):
        surrogate = np.empty_like(standardized)
        for column in range(standardized.shape[1]):
            source = standardized[:, column]
            phi = float(np.clip(_correlation(source[:-1], source[1:]), -0.95, 0.95))
            innovations = rng.normal(0.0, math.sqrt(max(1.0 - phi * phi, 1e-4)), len(source))
            surrogate[0, column] = innovations[0]
            for index in range(1, len(source)):
                surrogate[index, column] = phi * surrogate[index - 1, column] + innovations[index]
        candidate_acf = acf(surrogate)
        candidate_locals = [index for index in range(1, len(candidate_acf) - 1)
                            if candidate_acf[index] > candidate_acf[index - 1]
                            and candidate_acf[index] >= candidate_acf[index + 1]]
        null_maxima.append(float(max((candidate_acf[index] for index in candidate_locals), default=np.max(candidate_acf))))
    p_value = float((1 + np.count_nonzero(np.asarray(null_maxima) >= peak)) / (iterations + 1))
    supported = (
        local_peak
        and peak >= PERIOD_MIN_CORRELATION
        and prominence >= PERIOD_MIN_PROMINENCE
        and p_value <= PERIOD_MAX_P_VALUE
    )
    return {
        "status": "SUPPORTED" if supported else "NOT_SUPPORTED",
        "reason": "predeclared autocorrelation and AR(1) null criteria",
        "lags": lags.tolist(),
        "lagMm": [float(lag * spacing_mm) for lag in lags],
        "autocorrelation": observed.tolist(),
        "peakLagSamples": int(lags[peak_index]),
        "peakLagMm": float(lags[peak_index] * spacing_mm),
        "peakCorrelation": peak,
        "peakProminence": prominence,
        "localPeak": local_peak,
        "pValueAgainstAr1Null": p_value,
        "null95": float(np.quantile(null_maxima, 0.95)),
        "criteria": {
            "minimumCorrelation": PERIOD_MIN_CORRELATION,
            "minimumProminence": PERIOD_MIN_PROMINENCE,
            "maximumPValue": PERIOD_MAX_P_VALUE,
            "lagSamples": list(PERIOD_LAG_SAMPLES),
            "iterations": iterations,
            "seed": PERIOD_RANDOM_SEED,
        },
    }


def match_phase_rows(rows: Sequence[dict], phase_a: str, phase_b: str,
                     tolerance_mm: float = REVERSE_MATCH_TOLERANCE_MM) -> list[tuple[dict, dict]]:
    left = [row for row in rows if row["phase"] == phase_a]
    right = [row for row in rows if row["phase"] == phase_b]
    matches: list[tuple[dict, dict]] = []
    used: set[int] = set()
    for item in left:
        candidates = [candidate for candidate in right if int(candidate["observation_index"]) not in used]
        if not candidates:
            continue
        candidate = min(candidates, key=lambda other: abs(float(item["coordinate_mm"]) - float(other["coordinate_mm"])))
        difference = abs(float(item["coordinate_mm"]) - float(candidate["coordinate_mm"]))
        if difference <= tolerance_mm:
            matches.append((item, candidate))
            used.add(int(candidate["observation_index"]))
    return matches


def reversal_analysis(rows: Sequence[dict]) -> dict:
    comparisons = {
        "caudalHalf": match_phase_rows(rows, "caudal_outbound", "caudal_reverse"),
        "cranialPartial": match_phase_rows(rows, "cranial_outbound", "cranial_reverse"),
    }
    output: dict[str, dict] = {}
    for name, matches in comparisons.items():
        item: dict[str, object] = {
            "matchedObservationCount": len(matches),
            "coordinateMinMm": min((min(float(a["coordinate_mm"]), float(b["coordinate_mm"])) for a, b in matches), default=None),
            "coordinateMaxMm": max((max(float(a["coordinate_mm"]), float(b["coordinate_mm"])) for a, b in matches), default=None),
            "features": {},
        }
        for feature in FEATURE_NAMES:
            left = np.asarray([float(a[feature]) for a, _ in matches], dtype=np.float64)
            right = np.asarray([float(b[feature]) for _, b in matches], dtype=np.float64)
            valid = np.isfinite(left) & np.isfinite(right)
            combined = np.concatenate((left[valid], right[valid])) if np.any(valid) else np.asarray([])
            scale = max(float(np.median(np.abs(combined - np.median(combined)))) * 1.4826, 1e-6) \
                if len(combined) else 1.0
            item["features"][feature] = {
                "validPairs": int(np.count_nonzero(valid)),
                "correlation": _correlation(left[valid], right[valid]) if np.count_nonzero(valid) >= 3 else None,
                "meanAbsoluteDifference": float(np.mean(np.abs(left[valid] - right[valid]))) if np.any(valid) else None,
                "normalizedMeanAbsoluteDifference": float(np.mean(np.abs(left[valid] - right[valid])) / scale)
                if np.any(valid) else None,
            }
        if matches:
            same_distances = []
            distinct_distances = []
            all_rows = [row for row in rows if row["phase"] in {matches[0][0]["phase"], matches[0][1]["phase"]}]
            matrix = np.asarray([[float(row[feature]) for feature in FEATURE_NAMES] for row in all_rows], dtype=np.float64)
            medians = np.nanmedian(matrix, axis=0)
            scales = np.nanmedian(np.abs(matrix - medians), axis=0) * 1.4826
            scales = np.maximum(scales, 1e-6)
            for left_row, right_row in matches:
                left_state = np.asarray([float(left_row[feature]) for feature in FEATURE_NAMES])
                right_state = np.asarray([float(right_row[feature]) for feature in FEATURE_NAMES])
                if np.all(np.isfinite(left_state)) and np.all(np.isfinite(right_state)):
                    same_distances.append(float(np.linalg.norm((left_state - right_state) / scales)))
                far = [candidate for candidate in all_rows
                       if abs(float(candidate["coordinate_mm"]) - float(left_row["coordinate_mm"])) >= 18.0]
                if far:
                    candidate = far[(int(left_row["observation_index"]) * 17) % len(far)]
                    far_state = np.asarray([float(candidate[feature]) for feature in FEATURE_NAMES])
                    if np.all(np.isfinite(left_state)) and np.all(np.isfinite(far_state)):
                        distinct_distances.append(float(np.linalg.norm((left_state - far_state) / scales)))
            item["samePositionMedianStateDistance"] = float(np.median(same_distances)) if same_distances else None
            item["distinctPositionMedianStateDistance"] = float(np.median(distinct_distances)) if distinct_distances else None
        output[name] = item
    return output


def _principal_waveform(matrix: np.ndarray) -> np.ndarray:
    centered = matrix - np.mean(matrix, axis=0, keepdims=True)
    _, _, right = np.linalg.svd(centered, full_matrices=False)
    waveform = centered @ right[0]
    if _correlation(waveform, matrix[:, 0]) < 0:
        waveform = -waveform
    return moving_average(waveform, SMOOTHING_FRAMES)


def discover_cycles(primary_rows: list[dict], periodicity: dict, reversal: dict) -> dict:
    if periodicity["status"] != "SUPPORTED":
        return {
            "status": "NO_DEFENSIBLE_CYCLES",
            "reason": "multifeature periodicity did not meet frozen AR(1)-null criteria",
            "candidateCycles": [],
        }
    matrix = np.column_stack([
        robust_standardize(moving_average(_interpolate_missing(primary_rows, feature), SMOOTHING_FRAMES))
        for feature in FEATURE_NAMES
    ])
    waveform = _principal_waveform(matrix)
    period_samples = int(periodicity["peakLagSamples"])
    candidates = [index for index in range(1, len(waveform) - 1)
                  if waveform[index] > waveform[index - 1]
                  and waveform[index] >= waveform[index + 1]
                  and waveform[index] >= float(np.median(waveform) + 0.25 * np.std(waveform))]
    kept: list[int] = []
    minimum_separation = max(2, int(round(0.60 * period_samples)))
    for candidate in sorted(candidates, key=lambda index: float(waveform[index]), reverse=True):
        if all(abs(candidate - existing) >= minimum_separation for existing in kept):
            kept.append(candidate)
    kept.sort()
    cycles: list[dict] = []
    for number, index in enumerate(kept, start=1):
        left = max(0, index - period_samples // 2)
        right = min(len(primary_rows) - 1, index + period_samples // 2)
        support = int(np.count_nonzero(np.abs(matrix[index] - 0.5 * (matrix[left] + matrix[right])) >= 0.50))
        if support < 2:
            continue
        row = primary_rows[index]
        cycles.append({
            "cycleId": f"C{number}",
            "centerObservationIndex": int(row["observation_index"]),
            "centerActorRelativeMm": float(row["coordinate_mm"]),
            "intervalActorRelativeMm": [
                float(primary_rows[left]["coordinate_mm"]),
                float(primary_rows[right]["coordinate_mm"]),
            ],
            "supportingObservationIndices": [
                int(primary_rows[left]["observation_index"]),
                int(row["observation_index"]),
                int(primary_rows[right]["observation_index"]),
            ],
            "supportingFeatureFamilyCount": support,
            "principalWaveformAmplitude": float(waveform[index]),
            "anatomicalIdentity": "UNRESOLVED_IMAGE_MORPHOLOGY",
        })
    if len(cycles) < 2:
        return {
            "status": "NO_DEFENSIBLE_CYCLES",
            "reason": "fewer than two multifeature rise-fall candidates survived",
            "candidateCycles": cycles,
        }
    return {
        "status": "CANDIDATE_CYCLES",
        "reason": "multifeature periodicity and coherent extrema met frozen criteria",
        "estimatedPeriodMm": float(periodicity["peakLagMm"]),
        "candidateCycles": cycles,
    }


def _write_csv(path: Path, rows: Sequence[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fields = list(rows[0].keys()) if rows else []
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def _plot_series(path: Path, title: str, panels: Sequence[tuple[str, Sequence[tuple[str, np.ndarray, np.ndarray, tuple[int, int, int]]]]],
                 events: Sequence[float] = ()) -> None:
    width, panel_height = 1500, 260
    margin_left, margin_right, margin_top, margin_bottom = 100, 40, 50, 55
    height = margin_top + panel_height * len(panels) + margin_bottom
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    draw.text((margin_left, 14), title, fill="black")
    all_x = np.concatenate([series[1] for _, collection in panels for series in collection])
    x_min, x_max = float(np.min(all_x)), float(np.max(all_x))
    plot_width = width - margin_left - margin_right
    for panel_index, (label, collection) in enumerate(panels):
        top = margin_top + panel_index * panel_height
        bottom = top + panel_height - 35
        all_y = np.concatenate([series[2][np.isfinite(series[2])] for series in collection])
        y_min, y_max = float(np.min(all_y)), float(np.max(all_y))
        if abs(y_max - y_min) < 1e-8:
            y_max = y_min + 1.0

        def xy(x_value: float, y_value: float) -> tuple[int, int]:
            x = margin_left + int((x_value - x_min) / max(x_max - x_min, 1e-8) * plot_width)
            y = bottom - int((y_value - y_min) / max(y_max - y_min, 1e-8) * (bottom - top))
            return x, y

        draw.rectangle((margin_left, top, width - margin_right, bottom), outline="black")
        draw.text((8, top + 4), label, fill="black")
        for tick in range(7):
            x_value = x_min + tick * (x_max - x_min) / 6
            x, _ = xy(x_value, y_min)
            draw.line((x, top, x, bottom), fill=(230, 230, 230))
            if panel_index == len(panels) - 1:
                draw.text((x - 18, bottom + 10), f"{x_value:.0f}", fill="black")
        legend_x = margin_left + 8
        for series_name, x_values, y_values, color in collection:
            points = [xy(float(x), float(y)) for x, y in zip(x_values, y_values) if np.isfinite(y)]
            if len(points) > 1:
                draw.line(points, fill=color, width=3)
            draw.line((legend_x, top + 12, legend_x + 24, top + 12), fill=color, width=4)
            draw.text((legend_x + 29, top + 4), series_name, fill="black")
            legend_x += 235
        for event in events:
            x, _ = xy(float(event), y_min)
            draw.line((x, top, x, bottom), fill=(210, 30, 30), width=2)
    draw.text((width // 2 - 110, height - 30), "Actor-relative executed displacement (mm)", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _draw_periodicity(path: Path, result: dict) -> None:
    width, height = 1200, 700
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    margin = (100, 60, 50, 90)
    left, top, right_margin, bottom_margin = margin
    right, bottom = width - right_margin, height - bottom_margin
    draw.rectangle((left, top, right, bottom), outline="black")
    lags = np.asarray(result.get("lagMm", []), dtype=np.float64)
    acf = np.asarray(result.get("autocorrelation", []), dtype=np.float64)
    if len(lags):
        y_min = min(-0.25, float(np.min(acf)) - 0.1)
        y_max = max(0.75, float(np.max(acf)) + 0.1, float(result.get("null95", 0.0)) + 0.1)

        def xy(x_value: float, y_value: float) -> tuple[int, int]:
            x = left + int((x_value - lags.min()) / max(float(np.ptp(lags)), 1e-8) * (right - left))
            y = bottom - int((y_value - y_min) / max(y_max - y_min, 1e-8) * (bottom - top))
            return x, y

        points = [xy(float(x), float(y)) for x, y in zip(lags, acf)]
        draw.line(points, fill=(30, 90, 210), width=4)
        for point in points:
            draw.ellipse((point[0] - 5, point[1] - 5, point[0] + 5, point[1] + 5), fill=(30, 90, 210))
        _, null_y = xy(float(lags.min()), float(result["null95"]))
        draw.line((left, null_y, right, null_y), fill=(210, 50, 50), width=2)
        draw.text((left + 8, null_y - 20), f"AR(1) null 95% = {result['null95']:.3f}", fill=(180, 30, 30))
        for x in lags:
            px, _ = xy(float(x), y_min)
            draw.text((px - 12, bottom + 12), f"{x:.0f}", fill="black")
    draw.text((left, 18), "Blind multifeature spatial autocorrelation", fill="black")
    draw.text((left, height - 34), "Spatial lag (mm)", fill="black")
    draw.text((left + 520, 18),
              f"status={result.get('status')} peak={result.get('peakCorrelation', 0):.3f} "
              f"p={result.get('pValueAgainstAr1Null', 1):.3f}", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _make_overlays(path: Path, run_root: Path, rows: Sequence[dict], overlays: dict[int, dict]) -> None:
    selected_coordinates = np.linspace(min(float(row["coordinate_mm"]) for row in rows),
                                       max(float(row["coordinate_mm"]) for row in rows), 9)
    selected = [min(rows, key=lambda row: abs(float(row["coordinate_mm"]) - coordinate))
                for coordinate in selected_coordinates]
    tile_size, label_height, columns = 360, 42, 3
    canvas = Image.new("RGB", (columns * tile_size, 3 * (tile_size + label_height)), "white")
    draw = ImageDraw.Draw(canvas)
    for tile_index, row in enumerate(selected):
        with Image.open(run_root / row["image"]) as image:
            tile = image.convert("RGB").resize((tile_size, tile_size), Image.Resampling.BILINEAR)
        tile_draw = ImageDraw.Draw(tile)
        scale = tile_size / IMAGE_SIZE
        overlay = overlays[int(row["observation_index"])]
        for depth in overlay["shadow"]["depths"]:
            color = (230, 40, 40) if depth["valid"] else (140, 140, 140)
            y = int(depth["y"] * scale)
            tile_draw.line((int(depth["left"] * scale), y, int(depth["right"] * scale), y), fill=color, width=3)
            tile_draw.ellipse((int(depth["left"] * scale) - 3, y - 3,
                               int(depth["left"] * scale) + 3, y + 3), fill=color)
            tile_draw.ellipse((int(depth["right"] * scale) - 3, y - 3,
                               int(depth["right"] * scale) + 3, y + 3), fill=color)
        valid_points = [(int(point["x"] * scale), int(point["y"] * scale))
                        for point in overlay["interface"]["points"] if point["valid"]]
        for point in valid_points:
            tile_draw.ellipse((point[0] - 4, point[1] - 4, point[0] + 4, point[1] + 4), fill=(20, 220, 220))
        x = (tile_index % columns) * tile_size
        y = (tile_index // columns) * (tile_size + label_height)
        canvas.paste(tile, (x, y))
        draw.text((x + 4, y + tile_size + 3),
                  f"obs {row['observation_index']}  x={float(row['coordinate_mm']):.1f} mm\n"
                  "red=shadow widths, cyan=bright-interface samples", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _feature_definitions() -> dict:
    return {
        "contract": "thoracic_m0c_feature_definitions_v1",
        "generatedUtc": _utc_now(),
        "blindFrozenParameters": True,
        "imageNormalization": "grayscale 256x256; robust 2nd/98th percentile normalization in fixed ROI",
        "featureFamilies": [
            {
                "name": "central_shadow_geometry",
                "primarySignal": "shadow_width",
                "definition": "mean width of the central low-intensity interval at normalized depths 0.36, 0.48, 0.60",
                "threshold": "0.55 times the lateral band median, clipped to [0.10, 0.46]",
                "exclusions": "requires at least two valid depths and brighter support on both sides",
                "semanticClaim": "dark central morphology only; no bone or vertebra identity",
            },
            {
                "name": "bright_interface_geometry",
                "primarySignal": "interface_arch_height",
                "definition": "outer-minus-inner depth of twelve fixed-x bright-to-dark ridge samples",
                "threshold": "ridge score >= 0.18; at least four samples per side",
                "semanticClaim": "bright interface geometry only; not assumed to be bone",
            },
            {
                "name": "bilateral_structure",
                "primarySignal": "bilateral_symmetry",
                "definition": "correlation of mirrored fixed lateral ROIs over normalized depths 0.18 to 0.70",
                "semanticClaim": "bilateral image morphology only",
            },
        ],
        "smoothing": {"type": "three-frame centered moving average", "approximateSpanMm": 9.0},
        "periodicity": {
            "lagSamples": list(PERIOD_LAG_SAMPLES),
            "reason": "6-36 mm-class recurrence search at approximately 3 mm sampling without using reference spacing",
            "ar1NullIterations": PERIOD_NULL_ITERATIONS,
            "seed": PERIOD_RANDOM_SEED,
            "minimumCorrelation": PERIOD_MIN_CORRELATION,
            "minimumProminence": PERIOD_MIN_PROMINENCE,
            "maximumPValue": PERIOD_MAX_P_VALUE,
        },
        "cycleRule": "only run after multifeature periodicity is supported; local principal-waveform maximum, >=2 family support, >=0.6 period separation",
        "explicitlyNotUsed": ["frame ordinal as position", "T labels", "world coordinates", "anatomy bounds",
                                "evaluator references", "hardcoded event count", "T7 coordinate"],
    }


def _freeze_outputs(output_root: Path, names: Iterable[str]) -> dict:
    payload = {
        "contract": "thoracic_m0c_blind_freeze_v1",
        "generatedUtc": _utc_now(),
        "phase": "BLIND_IMAGE_ONLY_FROZEN_BEFORE_EVALUATOR",
        "sha256": {name: _sha256(output_root / name) for name in names},
    }
    _save_json(output_root / "blind_results.sha256", payload)
    return payload


def verify_freeze(output_root: Path) -> dict:
    payload = _load_json(output_root / "blind_results.sha256")
    for name, expected in payload["sha256"].items():
        actual = _sha256(output_root / name)
        if actual != expected:
            raise ValueError(f"Frozen blind output changed: {name}")
    return payload


def blind_analyze(run_root: Path, output_root: Path) -> dict:
    actor_path = run_root / "trajectory_actor.json"
    manifest_path = run_root / "frame_manifest.json"
    actor = _load_json(actor_path)
    validate_actor_isolation(actor)
    observations = actor["observations"]
    reconstruction = reconstruct_actor_coordinate(observations)
    if reconstruction["maximumAbsoluteErrorMm"] > 0.05:
        raise ValueError("Actor-relative coordinate does not agree with executed action history")
    frame_entries = []
    for observation in observations:
        image = run_root / observation["image"]
        if not image.is_file():
            raise FileNotFoundError(image)
        digest = _sha256(image)
        if digest.lower() != observation["imageSha256"].lower():
            raise ValueError(f"Frame hash mismatch: {image}")
        frame_entries.append({
            "observationIndex": int(observation["observationIndex"]),
            "relativePath": observation["image"],
            "sha256": digest,
        })
    output_root.mkdir(parents=True, exist_ok=True)
    input_manifest = {
        "contract": "thoracic_m0c_input_manifest_v1",
        "generatedUtc": _utc_now(),
        "sourceRun": str(run_root),
        "actorTraceSha256": _sha256(actor_path),
        "frameManifestSha256": _sha256(manifest_path),
        "observationCount": len(observations),
        "acceptedMovementCount": int(actor["acceptedMovements"]),
        "rejectedMovementCount": int(actor["rejectedMovements"]),
        "coordinateSource": "actual accepted achievedDisplacementMm accumulated by executed Cranial/Caudal action",
        "coordinateReconstructionMaximumErrorMm": reconstruction["maximumAbsoluteErrorMm"],
        "phaseCoverage": {},
        "frames": frame_entries,
        "evaluatorDataRead": False,
        "historicalInspectionWarning": "Blind-first execution on a dataset previously inspected during M0-B; not untouched held-out data.",
    }
    for phase in sorted({observation["phase"] for observation in observations}):
        phase_rows = [observation for observation in observations if observation["phase"] == phase]
        input_manifest["phaseCoverage"][phase] = {
            "count": len(phase_rows),
            "minimumActorRelativeMm": min(float(row["signedActorRelativeMm"]) for row in phase_rows),
            "maximumActorRelativeMm": max(float(row["signedActorRelativeMm"]) for row in phase_rows),
        }
    _save_json(output_root / "input_manifest.json", input_manifest)
    _save_json(output_root / "feature_definitions.json", _feature_definitions())

    rows: list[dict] = []
    overlays: dict[int, dict] = {}
    for observation, reconstructed_coordinate in zip(observations, reconstruction["coordinates"]):
        features, overlay = extract_morphology(run_root / observation["image"])
        index = int(observation["observationIndex"])
        row = {
            "observation_index": index,
            "phase": observation["phase"],
            "action": observation["requestedAction"],
            "coordinate_mm": float(reconstructed_coordinate),
            "image": observation["image"],
            "image_sha256": observation["imageSha256"],
            **features,
        }
        rows.append(row)
        overlays[index] = overlay
    _write_csv(output_root / "morphology_features.csv", rows)

    reliability = {
        "central_shadow_geometry": float(np.mean([row["shadow_valid"] for row in rows])),
        "bright_interface_geometry": float(np.mean([row["interface_valid"] for row in rows])),
        "bilateral_structure": float(np.mean([row["bilateral_valid"] for row in rows])),
    }
    selected_families = [name for name, coverage in reliability.items() if coverage >= MIN_RELIABLE_COVERAGE]
    primary_rows = [row for row in rows if row["phase"] in PRIMARY_PHASES]
    primary_rows.sort(key=lambda row: float(row["coordinate_mm"]))
    spacing = float(np.median(np.diff([float(row["coordinate_mm"]) for row in primary_rows])))
    feature_arrays = {
        feature: moving_average(_interpolate_missing(primary_rows, feature), SMOOTHING_FRAMES)
        for feature in FEATURE_NAMES
    }
    matrix = np.column_stack([feature_arrays[feature] for feature in FEATURE_NAMES])
    periodicity = periodicity_test(matrix, spacing)
    reversal = reversal_analysis(rows)
    cycles = discover_cycles(primary_rows, periodicity, reversal)

    old_features = [extract_frame_features(run_root / row["image"], float(row["coordinate_mm"])) for row in primary_rows]
    old_combined = moving_average(
        robust_z(np.asarray([item.bone_interface for item in old_features]))
        + robust_z(np.asarray([item.shadow_fraction for item in old_features]))
        - 0.5 * robust_z(np.asarray([item.interface_depth for item in old_features])),
        SMOOTHING_FRAMES,
    )
    old_events = detect_candidate_events_with_boundaries(
        np.asarray([item.travel_mm for item in old_features]), old_combined
    )
    old_event_rows = [{
        "observationIndex": int(primary_rows[index]["observation_index"]),
        "actorRelativeMm": float(primary_rows[index]["coordinate_mm"]),
        "combinedValue": float(old_combined[index]),
    } for index in old_events]

    ablation: dict[str, dict] = {}
    for removed_index, feature in enumerate(FEATURE_NAMES):
        subset = np.delete(matrix, removed_index, axis=1)
        result = periodicity_test(subset, spacing)
        ablation[f"without_{feature}"] = {
            "status": result["status"],
            "peakLagMm": result.get("peakLagMm"),
            "peakCorrelation": result.get("peakCorrelation"),
            "pValueAgainstAr1Null": result.get("pValueAgainstAr1Null"),
        }
    controls = {
        "contract": "thoracic_m0c_controls_v1",
        "oldScalarPositivePeakDetector": {
            "eventCount": len(old_event_rows),
            "events": old_event_rows,
            "warning": "Control only; a positive scalar peak is not assigned vertebral identity.",
        },
        "noiseAndPeriodicity": periodicity,
        "featureAblation": ablation,
        "reverseAgreement": reversal,
    }
    _save_json(output_root / "controls.json", controls)
    blind_cycles = {
        "contract": "thoracic_m0c_blind_cycles_v1",
        "generatedUtc": _utc_now(),
        "phase": "BLIND_IMAGE_ONLY",
        "reliableFeatureCoverage": reliability,
        "selectedFeatureFamilies": selected_families,
        "primaryTraversal": {
            "phases": list(PRIMARY_PHASES),
            "observationCount": len(primary_rows),
            "minimumActorRelativeMm": float(primary_rows[0]["coordinate_mm"]),
            "maximumActorRelativeMm": float(primary_rows[-1]["coordinate_mm"]),
            "medianSpacingMm": spacing,
            "direction": "cranial physical traversal, reordered by coordinate for spatial analysis",
        },
        "periodicity": periodicity,
        "cycleDiscovery": cycles,
        "reversalCoverage": {
            name: {
                "matchedObservationCount": value["matchedObservationCount"],
                "coordinateMinMm": value["coordinateMinMm"],
                "coordinateMaxMm": value["coordinateMaxMm"],
            } for name, value in reversal.items()
        },
        "t7Inference": "NOT_ATTEMPTED",
        "warning": "No T-level labels, world coordinates, evaluator references, or anatomy provenance were read.",
    }
    _save_json(output_root / "blind_cycle_candidates.json", blind_cycles)

    _make_overlays(output_root / "morphology_overlays.png", run_root, primary_rows, overlays)
    colors = {
        "caudal_outbound": (210, 80, 30),
        "caudal_reverse": (20, 100, 210),
        "cranial_outbound": (30, 150, 70),
        "cranial_reverse": (150, 60, 170),
    }
    panels = []
    for feature, label in zip(FEATURE_NAMES, ("Central shadow width", "Bright-interface arch height", "Bilateral symmetry")):
        collection = []
        for phase in colors:
            phase_rows = sorted((row for row in rows if row["phase"] == phase), key=lambda row: float(row["coordinate_mm"]))
            x = np.asarray([float(row["coordinate_mm"]) for row in phase_rows])
            y = moving_average(_interpolate_missing(phase_rows, feature), SMOOTHING_FRAMES)
            collection.append((phase, x, y, colors[phase]))
        panels.append((label, collection))
    cycle_coordinates = [float(item["centerActorRelativeMm"]) for item in cycles["candidateCycles"]]
    _plot_series(output_root / "waveform_vs_displacement.png",
                 "M0-C blind interpretable morphology waveforms", panels, cycle_coordinates)
    _plot_series(output_root / "forward_reverse_comparison.png",
                 "M0-C actual overlap only; traversal directions plotted independently", panels, cycle_coordinates)
    _draw_periodicity(output_root / "periodicity_analysis.png", periodicity)

    freeze_names = [
        "input_manifest.json",
        "feature_definitions.json",
        "morphology_features.csv",
        "morphology_overlays.png",
        "waveform_vs_displacement.png",
        "forward_reverse_comparison.png",
        "periodicity_analysis.png",
        "blind_cycle_candidates.json",
        "controls.json",
    ]
    freeze = _freeze_outputs(output_root, freeze_names)
    return {
        "blindCycleStatus": cycles["status"],
        "candidateCycleCount": len(cycles["candidateCycles"]),
        "periodicityStatus": periodicity["status"],
        "reliableFeatureCoverage": reliability,
        "freeze": freeze,
    }


def _reference_records(reference_payload: object) -> list[dict]:
    if isinstance(reference_payload, list):
        candidates = reference_payload
    elif isinstance(reference_payload, dict):
        for key in ("references", "levels", "referenceLevels"):
            if isinstance(reference_payload.get(key), list):
                candidates = reference_payload[key]
                break
        else:
            candidates = []
    else:
        candidates = []
    records = []
    for item in candidates:
        label = item.get("level") or item.get("label") or item.get("name")
        if label in {f"T{number}" for number in range(2, 9)}:
            records.append(item)
    return records


def evaluator_validate(run_root: Path, output_root: Path, references_path: Path) -> dict:
    verify_freeze(output_root)
    blind = _load_json(output_root / "blind_cycle_candidates.json")
    privileged = _load_json(run_root / "evaluator_only" / "trajectory_privileged.json")
    references_payload = _load_json(references_path)
    references = _reference_records(references_payload)
    if len(references) != 7:
        raise ValueError(f"Expected independently recorded T2-T8 references, found {len(references)}")
    observations = {int(row["observationIndex"]): row for row in privileged["observations"]}
    with (output_root / "morphology_features.csv").open(newline="", encoding="utf-8") as stream:
        morphology_rows = {int(row["observation_index"]): row for row in csv.DictReader(stream)}
    candidate_cycles = blind["cycleDiscovery"]["candidateCycles"]
    mapped_cycles = []
    for cycle in candidate_cycles:
        index = int(cycle["centerObservationIndex"])
        observation = observations[index]
        mapped_cycles.append({
            **cycle,
            "evaluatorCoordinateMm": float(observation["evaluatorLongitudinalCoordinateMm"]),
            "rendererProvenance": observation.get("provenance", ""),
            "provenanceMatchedPublishedFrame": bool(observation.get("provenanceMatchedPublishedFrame", False)),
        })

    start = privileged["startContactPoint"]
    axis = privileged["corridorAxis"]

    def reference_coordinate(item: dict) -> float | None:
        contact = item.get("contactPoint")
        if not isinstance(contact, dict):
            return None
        return float(
            (float(contact["x"]) - float(start["x"])) * float(axis["x"])
            + (float(contact["y"]) - float(start["y"])) * float(axis["y"])
            + (float(contact["z"]) - float(start["z"])) * float(axis["z"])
        )

    usable_references = [{
        "label": item.get("level") or item.get("label") or item.get("name"),
        "coordinateMm": reference_coordinate(item),
        "sourceCorridorCoordinateMm": item.get("corridorCoordinateMm"),
        "anatomyObject": item.get("anatomyObject"),
        "anatomyEvidence": item.get("anatomyEvidence"),
    } for item in references]
    comparable = all(item["coordinateMm"] is not None for item in usable_references)
    per_level = []
    for reference in usable_references:
        nearest = min(observations.values(),
                      key=lambda row: abs(float(row["evaluatorLongitudinalCoordinateMm"])
                                          - float(reference["coordinateMm"])))
        index = int(nearest["observationIndex"])
        feature_row = morphology_rows[index]
        provenance_path = run_root / nearest["provenance"]
        provenance = _load_json(provenance_path)
        shadow_objects = sorted({beam["firstShadowObject"] for beam in provenance["beams"]
                                 if beam.get("hasShadowInterface") and beam.get("firstShadowObject")})
        visible_objects = sorted({beam["firstVisibleObject"] for beam in provenance["beams"]
                                  if beam.get("hasVisibleInterface") and beam.get("firstVisibleObject")})
        nearby_cycles = [cycle["cycleId"] for cycle in mapped_cycles
                         if abs(float(cycle["evaluatorCoordinateMm"]) - float(reference["coordinateMm"])) <= 6.0]
        per_level.append({
            "level": reference["label"],
            "projectedM0BEvaluatorCoordinateMm": reference["coordinateMm"],
            "nearestObservationIndex": index,
            "nearestObservationEvaluatorCoordinateMm": float(nearest["evaluatorLongitudinalCoordinateMm"]),
            "nearestObservationErrorMm": abs(float(nearest["evaluatorLongitudinalCoordinateMm"])
                                             - float(reference["coordinateMm"])),
            "blindCandidateCyclesWithin6Mm": nearby_cycles,
            "morphology": {
                "shadowWidth": float(feature_row["shadow_width"]),
                "interfaceArchHeight": float(feature_row["interface_arch_height"]),
                "bilateralSymmetry": float(feature_row["bilateral_symmetry"]),
            },
            "rendererProvenance": {
                "poseAndPublishedFrameMatched": bool(nearest.get("provenanceMatchedPublishedFrame", False)),
                "firstVisibleObjects": visible_objects,
                "firstShadowObjects": shadow_objects,
            },
            "identityConclusion": "COMBINED_SKELETON_UNRESOLVED",
        })
    anatomical_comparison = {
        "contract": "thoracic_m0c_anatomical_comparison_v1",
        "generatedUtc": _utc_now(),
        "blindFreezeVerified": True,
        "referenceSource": str(references_path),
        "candidateCycleCount": len(mapped_cycles),
        "cycles": mapped_cycles,
        "references": usable_references,
        "coordinateComparability": comparable,
        "coordinateMethod": "T2-T8 reference contact points projected onto the M0-B evaluator start and corridor axis; blind cycles mapped by observation index to the corresponding privileged frame",
        "perLevelComparison": per_level,
        "vertebralCorrespondence": "NOT_SUPPORTED" if not mapped_cycles else "AMBIGUOUS",
        "t7Inference": "NOT_ATTEMPTED",
        "t7Reason": "No absolute anchor or independently validated one-to-one vertebral sequence was established.",
        "combinedSkeletonIdentity": "UNRESOLVED",
        "warnings": [
            "Marker proximity alone does not establish anatomical identity.",
            "Combined Skeleton provenance cannot distinguish rib, lamina, interspace, or vertebral body.",
            "The blind dataset was historically viewed during M0-B and is not an untouched held-out trial.",
        ],
    }
    if mapped_cycles:
        anatomical_comparison["vertebralCorrespondence"] = "AMBIGUOUS"
    _save_json(output_root / "evaluator_only" / "anatomical_comparison.json", anatomical_comparison)
    _draw_alignment(output_root / "evaluator_only" / "vertebral_alignment.png", mapped_cycles, usable_references)
    return anatomical_comparison


def _draw_alignment(path: Path, cycles: Sequence[dict], references: Sequence[dict]) -> None:
    width, height = 1400, 480
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    coordinates = [float(item["evaluatorCoordinateMm"]) for item in cycles]
    coordinates.extend(float(item["coordinateMm"]) for item in references if item["coordinateMm"] is not None)
    if not coordinates:
        draw.text((50, 50), "No comparable candidate cycles or reference coordinates", fill="black")
        canvas.save(path)
        return
    x_min, x_max = min(coordinates), max(coordinates)

    def x_position(value: float) -> int:
        return 90 + int((value - x_min) / max(x_max - x_min, 1e-6) * (width - 180))

    draw.text((90, 25), "Evaluator-only alignment; proximity is diagnostic, not anatomical identity", fill="black")
    draw.line((90, 190, width - 90, 190), fill="black", width=2)
    for item in references:
        if item["coordinateMm"] is None:
            continue
        x = x_position(float(item["coordinateMm"]))
        draw.line((x, 120, x, 190), fill=(80, 80, 80), width=2)
        draw.text((x - 10, 95), str(item["label"]), fill="black")
    for item in cycles:
        x = x_position(float(item["evaluatorCoordinateMm"]))
        draw.line((x, 190, x, 285), fill=(210, 40, 40), width=3)
        draw.text((x - 12, 300), str(item["cycleId"]), fill=(180, 20, 20))
    draw.text((90, 390), "Gray: Unity reference markers. Red: frozen blind morphology candidates.", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="Offline M0-C anatomical waveform discovery")
    parser.add_argument("--run-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--phase", choices=("blind", "evaluate"), required=True)
    parser.add_argument("--references", type=Path)
    args = parser.parse_args()
    if args.phase == "blind":
        result = blind_analyze(args.run_root.resolve(), args.output_root.resolve())
    else:
        if args.references is None:
            parser.error("--references is required for evaluator phase")
        result = evaluator_validate(args.run_root.resolve(), args.output_root.resolve(), args.references.resolve())
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
