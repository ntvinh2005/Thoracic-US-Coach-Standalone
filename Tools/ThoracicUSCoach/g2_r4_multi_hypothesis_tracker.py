"""Bounded image-only multi-hypothesis interface tracking for G2-R4."""

from __future__ import annotations

import math
from pathlib import Path
from typing import Any

import numpy as np

from g2_r2_continuous_detector import read_json, validate_actor_trace
from g2_r2_interface_tracker import (
    CLUSTER_NMS_MM,
    CLUSTER_RADIUS_MM,
    MAXIMUM_CLUSTER_MAD_MM,
    MAXIMUM_INTERFACE_DEPTH_IQR_PIXELS,
    MAXIMUM_PASS_DISAGREEMENT_MM,
    MAX_PATH_STEP_PIXELS,
    MINIMUM_DISTINCT_FRAMES,
    OUTBOUND_PHASES,
    PATH_SMOOTHNESS_PENALTY,
    REPEAT_PHASES,
    TRACK_X_MARGIN_PIXELS,
    TRACK_Y_MAX_FRACTION,
    TRACK_Y_MIN_FRACTION,
    configured_footprint_width,
    detect_interface_valleys,
    projected_coordinate,
    select_axis_sign,
    smooth_vector,
    track_bright_interface,
)
from g2_r3_topology_discriminator import (
    MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
    measure_interface_termination,
)
from relative_spine_e2_analysis import load_gray


MAXIMUM_TRACK_HYPOTHESES = 3
MINIMUM_MEDIAN_TRACK_SEPARATION_PIXELS = 2.0
MINIMUM_SEPARATED_COLUMN_FRACTION = 0.15
MINIMUM_SHADOW_PERSISTENCE_FRACTION = 0.55
MINIMUM_SHADOW_ONSET_CONTRAST = 0.20
MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION = 0.50
DEPTH_COMPONENT_GAP_PIXELS = 8.0
MINIMUM_CANDIDATE_DENSITY_FRACTION = 0.25
SPATIAL_GRID_STEP_MM = 0.5
SPATIAL_VALLEY_RADIUS_BINS = 20
SPATIAL_VALLEY_NMS_BINS = 20
SPATIAL_VALLEY_PROMINENCE_PIXELS = 3.0


def _ridge_fields(image: np.ndarray) -> tuple[np.ndarray, list[tuple[str, np.ndarray]], np.ndarray, int]:
    height, width = image.shape
    x_values = np.arange(TRACK_X_MARGIN_PIXELS, width - TRACK_X_MARGIN_PIXELS)
    y_min = int(round(height * TRACK_Y_MIN_FRACTION))
    y_max = int(round(height * TRACK_Y_MAX_FRACTION))
    region = image[y_min:y_max, :]
    vertical_gradient = np.abs(np.gradient(region, axis=0))
    gradient_scale = max(float(np.quantile(vertical_gradient, 0.95)), 1e-6)
    gradient_ridge = np.clip(vertical_gradient / gradient_scale, 0.0, 2.0)
    baseline = 0.65 * region + 0.35 * gradient_ridge
    depth_bias = np.linspace(0.0, 1.0, region.shape[0], dtype=np.float64)[:, None]
    alternatives = [
        ("H1_EDGE_DEEP", 0.40 * region + 0.60 * gradient_ridge + 0.08 * depth_bias),
        ("H2_INTENSITY_DEEP", 0.75 * region + 0.25 * gradient_ridge + 0.06 * depth_bias),
    ]
    return baseline, alternatives, x_values, y_min


def _solve_path(
    ridge: np.ndarray, x_values: np.ndarray, y_min: int, penalty: np.ndarray
) -> dict[str, Any]:
    y_count = ridge.shape[0]
    effective = ridge - penalty
    cost = -effective[:, x_values[0]].copy()
    back = np.zeros((len(x_values), y_count), dtype=np.int8)
    shifts = np.arange(-MAX_PATH_STEP_PIXELS, MAX_PATH_STEP_PIXELS + 1, dtype=np.int8)
    for x_index, x_value in enumerate(x_values[1:], start=1):
        candidates = []
        for shift in shifts:
            shifted = np.full(y_count, np.inf)
            if shift < 0:
                shifted[:shift] = cost[-shift:]
            elif shift > 0:
                shifted[shift:] = cost[:-shift]
            else:
                shifted[:] = cost
            candidates.append(shifted + PATH_SMOOTHNESS_PENALTY * float(shift * shift))
        matrix = np.stack(candidates, axis=0)
        best = np.argmin(matrix, axis=0)
        back[x_index] = shifts[best]
        cost = matrix[best, np.arange(y_count)] - effective[:, x_value]
    path = np.zeros(len(x_values), dtype=np.int32)
    path[-1] = int(np.argmin(cost))
    for x_index in range(len(x_values) - 1, 0, -1):
        path[x_index - 1] = path[x_index] - int(back[x_index, path[x_index]])
    path = np.clip(path, 0, y_count - 1)
    absolute = path + y_min
    smoothed = smooth_vector(absolute.astype(np.float64))
    ridge_samples = ridge[path, x_values]
    jumps = np.abs(np.diff(smoothed))
    return {
        "x": x_values,
        "pathY": smoothed,
        "ridgeScores": ridge_samples,
        "meanRidgeScore": float(np.mean(ridge_samples)),
        "maximumPathJumpPixels": float(np.max(jumps, initial=0.0)),
        "valid": bool(np.mean(ridge_samples) >= 0.18 and np.max(jumps, initial=0.0) <= 4.0),
    }


def _is_diverse(track: dict[str, Any], accepted: list[dict[str, Any]]) -> tuple[bool, float, float]:
    if not accepted:
        return True, math.inf, 1.0
    best_median = math.inf
    best_fraction = 0.0
    for other in accepted:
        separation = np.abs(np.asarray(track["pathY"]) - np.asarray(other["pathY"]))
        median = float(np.median(separation))
        fraction = float(np.mean(separation >= MINIMUM_MEDIAN_TRACK_SEPARATION_PIXELS))
        if median < best_median:
            best_median = median
            best_fraction = fraction
    diverse = (
        best_median >= MINIMUM_MEDIAN_TRACK_SEPARATION_PIXELS
        and best_fraction >= MINIMUM_SEPARATED_COLUMN_FRACTION
    )
    return diverse, best_median, best_fraction


def track_multiple_interfaces(
    image: np.ndarray, maximum_hypotheses: int = MAXIMUM_TRACK_HYPOTHESES
) -> list[dict[str, Any]]:
    """Return the immutable baseline path plus at most two diverse alternatives."""
    ridge, alternative_ridges, x_values, y_min = _ridge_fields(image)
    tracks: list[dict[str, Any]] = []
    baseline = track_bright_interface(image)
    baseline["trackId"] = "H0_BASELINE"
    baseline["trackRank"] = 0
    baseline["medianSeparationFromNearestPixels"] = None
    baseline["separatedColumnFraction"] = None
    tracks.append(baseline)
    for rank, (track_id, alternative_ridge) in enumerate(
        alternative_ridges[:maximum_hypotheses - 1], start=1
    ):
        candidate = _solve_path(
            alternative_ridge, x_values, y_min, np.zeros_like(alternative_ridge)
        )
        diverse, median, fraction = _is_diverse(candidate, tracks)
        candidate["trackId"] = track_id
        candidate["trackRank"] = rank
        candidate["medianSeparationFromNearestPixels"] = median
        candidate["separatedColumnFraction"] = fraction
        if candidate["valid"] and diverse:
            tracks.append(candidate)
    return tracks


def measure_actual_shadow(
    image: np.ndarray, track: dict[str, Any], pixel_x: float
) -> dict[str, float | bool]:
    """Measure a local dark distal column field, separately from mere available image depth."""
    topology = measure_interface_termination(image, track, pixel_x)
    x_values = np.asarray(track["x"], dtype=np.float64)
    center_index = int(np.argmin(np.abs(x_values - float(pixel_x))))
    persistence = []
    distal_means = []
    proximal_means = []
    for offset in range(-6, 7):
        index = int(np.clip(center_index + offset, 0, len(x_values) - 1))
        x_value = int(round(float(x_values[index])))
        y_value = int(round(float(track["pathY"][index])))
        proximal = image[max(0, y_value - 24):max(1, y_value - 6), max(0, x_value - 1):x_value + 2]
        distal = image[min(image.shape[0], y_value + 6):min(image.shape[0], y_value + 36),
                       max(0, x_value - 1):x_value + 2]
        if not proximal.size or distal.shape[0] < 20:
            continue
        proximal_mean = float(np.mean(proximal))
        distal_mean = float(np.mean(distal))
        proximal_means.append(proximal_mean)
        distal_means.append(distal_mean)
        contrast = (proximal_mean - distal_mean) / max(proximal_mean, 1e-6)
        persistence.append(contrast >= MINIMUM_SHADOW_ONSET_CONTRAST)
    center_path_y = float(track["pathY"][center_index])
    available = float(image.shape[0] - 1 - center_path_y)
    persistence_fraction = float(np.mean(persistence)) if persistence else 0.0
    return {
        **topology,
        "minimumDistalObservationPixels": available,
        "shadowPersistenceFraction": persistence_fraction,
        "medianLocalProximalIntensity": float(np.median(proximal_means)) if proximal_means else 0.0,
        "medianLocalDistalIntensity": float(np.median(distal_means)) if distal_means else 0.0,
        "actualShadowSupported": bool(
            available >= MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS
            and persistence_fraction >= MINIMUM_SHADOW_PERSISTENCE_FRACTION
            and float(topology["shadowOnsetContrast"]) >= MINIMUM_SHADOW_ONSET_CONTRAST
        ),
    }


def extract_multi_hypothesis_detections(
    root: Path, actor: dict[str, Any]
) -> tuple[list[dict[str, Any]], dict[int, Any]]:
    observations = validate_actor_trace(root, actor)
    detections: list[dict[str, Any]] = []
    frame_tracks: dict[int, Any] = {}
    for observation in observations:
        image = load_gray(root / observation["image"])
        tracks = track_multiple_interfaces(image)
        frame_tracks[observation["observationIndex"]] = []
        for track in tracks:
            valleys = detect_interface_valleys(track)
            frame_tracks[observation["observationIndex"]].append({
                "track": track,
                "valleys": valleys,
            })
            for valley in valleys:
                morphology = measure_actual_shadow(image, track, valley["pixelX"])
                detections.append({
                    "observationIndex": observation["observationIndex"],
                    "phase": observation["phase"],
                    "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
                    "image": observation["image"],
                    "imageSha256": observation["imageSha256"],
                    "imageWidth": image.shape[1],
                    "trackId": track["trackId"],
                    "trackRank": track["trackRank"],
                    "pixelX": valley["pixelX"],
                    "depthPixelY": valley["depthPixelY"],
                    "prominencePixels": valley["prominencePixels"],
                    "trackMeanRidgeScore": track["meanRidgeScore"],
                    "trackMedianSeparationPixels": track["medianSeparationFromNearestPixels"],
                    **morphology,
                })
    return detections, frame_tracks


def build_spatial_interface_field(
    source_root: Path, actor: dict[str, Any], sign: int
) -> dict[str, Any]:
    """Fuse per-frame contour hypotheses into an actor-relative interface score field."""
    observations = validate_actor_trace(source_root, actor)
    footprint = configured_footprint_width(actor)
    coordinates = np.asarray([
        float(item["signedActorRelativeMm"]) for item in observations
    ], dtype=np.float64)
    grid = np.arange(
        float(np.min(coordinates)), float(np.max(coordinates)) + SPATIAL_GRID_STEP_MM,
        SPATIAL_GRID_STEP_MM,
    )
    y_min = int(round(256 * TRACK_Y_MIN_FRACTION))
    y_max = int(round(256 * TRACK_Y_MAX_FRACTION))
    score = np.zeros((y_max - y_min, len(grid)), dtype=np.float64)
    coverage = np.zeros(len(grid), dtype=np.float64)
    frame_records = []
    for observation in observations:
        image = load_gray(source_root / observation["image"])
        tracks = track_multiple_interfaces(image)
        frame_score = np.zeros_like(score)
        frame_covered = np.zeros(len(grid), dtype=bool)
        for track in tracks:
            for pixel_x, pixel_y, ridge_score in zip(
                track["x"], track["pathY"], track["ridgeScores"]
            ):
                detection = {
                    "imageWidth": image.shape[1],
                    "pixelX": float(pixel_x),
                    "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
                }
                spatial = projected_coordinate(detection, sign, footprint)
                grid_index = int(round((spatial - grid[0]) / SPATIAL_GRID_STEP_MM))
                local_y = int(round(float(pixel_y))) - y_min
                if not 0 <= grid_index < len(grid) or not 0 <= local_y < score.shape[0]:
                    continue
                frame_covered[grid_index] = True
                for y_offset, weight in ((-2, 0.25), (-1, 0.6), (0, 1.0), (1, 0.6), (2, 0.25)):
                    target_y = local_y + y_offset
                    if 0 <= target_y < score.shape[0]:
                        frame_score[target_y, grid_index] = max(
                            frame_score[target_y, grid_index],
                            float(ridge_score) * weight,
                        )
        score += frame_score
        coverage += frame_covered.astype(np.float64)
        frame_records.append({
            "observationIndex": observation["observationIndex"],
            "phase": observation["phase"],
            "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
            "image": observation["image"],
            "tracks": tracks,
        })
    normalized = score / np.maximum(coverage[None, :], 1.0)
    return {
        "gridMm": grid,
        "yMin": y_min,
        "yMax": y_max,
        "score": normalized,
        "coverage": coverage,
        "frames": frame_records,
        "footprintWidthMm": footprint,
    }


def track_spatial_interface_hypotheses(field: dict[str, Any]) -> list[dict[str, Any]]:
    """Extract up to three diverse continuous contours in actor-relative map coordinates."""
    score = np.asarray(field["score"], dtype=np.float64)
    grid = np.asarray(field["gridMm"], dtype=np.float64)
    x_values = np.arange(score.shape[1])
    depth_bias = np.linspace(0.0, 1.0, score.shape[0], dtype=np.float64)[:, None]
    effective = score + 0.06 * depth_bias
    penalty = np.zeros_like(effective)
    hypotheses = []
    for rank in range(MAXIMUM_TRACK_HYPOTHESES):
        track = _solve_path(effective, x_values, int(field["yMin"]), penalty)
        track["trackId"] = f"S{rank}_SPATIAL"
        track["trackRank"] = rank
        track["spatialCoordinatesMm"] = grid
        if rank == 0 or _is_diverse(track, hypotheses)[0]:
            hypotheses.append(track)
        local_path = np.asarray(track["pathY"], dtype=np.float64) - float(field["yMin"])
        y_axis = np.arange(effective.shape[0], dtype=np.float64)[:, None]
        distance = np.abs(y_axis - local_path[None, :])
        penalty += 1.5 * np.clip(1.0 - distance / 9.0, 0.0, 1.0)
    return hypotheses


def detect_spatial_valleys(track: dict[str, Any]) -> list[dict[str, float]]:
    path = np.asarray(track["pathY"], dtype=np.float64)
    grid = np.asarray(track["spatialCoordinatesMm"], dtype=np.float64)
    radius = SPATIAL_VALLEY_RADIUS_BINS
    candidates = []
    for index in range(radius, len(path) - radius):
        if path[index] < np.max(path[index - 2:index + 3]):
            continue
        left_base = float(np.min(path[index - radius:index - 2]))
        right_base = float(np.min(path[index + 3:index + radius + 1]))
        prominence = float(path[index] - max(left_base, right_base))
        if prominence < SPATIAL_VALLEY_PROMINENCE_PIXELS:
            continue
        candidates.append({
            "actorRelativeCoordinateMm": float(grid[index]),
            "depthPixelY": float(path[index]),
            "prominencePixels": prominence,
            "spatialGridIndex": index,
            "trackId": track["trackId"],
        })
    kept = []
    for candidate in sorted(candidates, key=lambda item: item["prominencePixels"], reverse=True):
        if all(
            abs(candidate["spatialGridIndex"] - other["spatialGridIndex"])
            >= SPATIAL_VALLEY_NMS_BINS for other in kept
        ):
            kept.append(candidate)
    return sorted(kept, key=lambda item: item["actorRelativeCoordinateMm"])


def spatial_event_support(
    source_root: Path, field: dict[str, Any], event: dict[str, Any], sign: int
) -> dict[str, Any]:
    """Collect real frame evidence supporting one spatial-map valley."""
    footprint = float(field["footprintWidthMm"])
    support = []
    for frame in field["frames"]:
        image = load_gray(source_root / frame["image"])
        center = 0.5 * float(image.shape[1] - 1)
        normalized = (
            (event["actorRelativeCoordinateMm"] - frame["probeCoordinateMm"])
            / float(sign * footprint)
        )
        pixel_x = center + normalized * max(2.0 * center, 1.0)
        if pixel_x < TRACK_X_MARGIN_PIXELS or pixel_x >= image.shape[1] - TRACK_X_MARGIN_PIXELS:
            continue
        best = None
        for track in frame["tracks"]:
            index = int(np.argmin(np.abs(np.asarray(track["x"], dtype=np.float64) - pixel_x)))
            depth = float(track["pathY"][index])
            depth_error = abs(depth - float(event["depthPixelY"]))
            if best is None or depth_error < best[0]:
                best = (depth_error, track, index, depth)
        if best is None or best[0] > 8.0:
            continue
        _, track, index, depth = best
        morphology = measure_actual_shadow(image, track, float(track["x"][index]))
        support.append({
            "observationIndex": frame["observationIndex"],
            "phase": frame["phase"],
            "image": frame["image"],
            "pixelX": float(track["x"][index]),
            "depthPixelY": depth,
            "trackId": track["trackId"],
            **morphology,
        })
    outbound = [item for item in support if item["phase"] in OUTBOUND_PHASES]
    repeat = [item for item in support if item["phase"] in REPEAT_PHASES]
    shadow_fraction = float(np.mean([
        item["actualShadowSupported"] for item in support
    ])) if support else 0.0
    accepted = (
        len(support) >= MINIMUM_DISTINCT_FRAMES
        and bool(outbound) and bool(repeat)
        and shadow_fraction >= MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION
    )
    reasons = []
    if len(support) < MINIMUM_DISTINCT_FRAMES:
        reasons.append("INSUFFICIENT_DISTINCT_FRAMES")
    if not outbound or not repeat:
        reasons.append("MISSING_DIRECTIONAL_SUPPORT")
    if shadow_fraction < MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION:
        reasons.append("INSUFFICIENT_ACTUAL_SHADOW_SUPPORT")
    return {
        **event,
        "distinctFrameCount": len(support),
        "outboundFrameCount": len(outbound),
        "repeatFrameCount": len(repeat),
        "actualShadowSupportFraction": shadow_fraction,
        "supportingFrameIndices": [item["observationIndex"] for item in support],
        "supportingImages": list(dict.fromkeys(item["image"] for item in support)),
        "accepted": accepted,
        "classification": (
            "SUPPORTED_ANATOMICAL_EVENT" if accepted
            else "AMBIGUOUS_INSUFFICIENT_EVIDENCE"
        ),
        "rejectionReasons": reasons,
        "support": support,
    }


def run_spatial_actor_tracking(source_root: Path) -> dict[str, Any]:
    actor = read_json(source_root / "trajectory_actor.json")
    baseline_detections, _ = extract_multi_hypothesis_detections(source_root, actor)
    baseline_only = [item for item in baseline_detections if item["trackRank"] == 0]
    baseline_choice, _ = select_axis_sign(
        baseline_only, configured_footprint_width(actor)
    )
    sign = int(baseline_choice["sign"])
    field = build_spatial_interface_field(source_root, actor, sign)
    tracks = track_spatial_interface_hypotheses(field)
    raw_events = []
    for track in tracks:
        raw_events.extend(detect_spatial_valleys(track))
    raw_events.sort(key=lambda item: item["prominencePixels"], reverse=True)
    selected = []
    for event in raw_events:
        if all(
            abs(event["actorRelativeCoordinateMm"] - other["actorRelativeCoordinateMm"])
            >= CLUSTER_NMS_MM for other in selected
        ):
            selected.append(event)
    selected.sort(key=lambda item: item["actorRelativeCoordinateMm"])
    candidates = [spatial_event_support(source_root, field, item, sign) for item in selected]
    return {
        "contract": "thoracic_g2_r4_spatial_multi_hypothesis_tracking_v1",
        "inputIsolation": "Clean pixels, acquisition geometry, and actor-relative motion only.",
        "chosenImageAxisSign": sign,
        "gridStepMm": SPATIAL_GRID_STEP_MM,
        "spatialTrackCount": len(tracks),
        "rawSpatialValleyCount": len(raw_events),
        "candidates": candidates,
        "spatialTracks": [{
            "trackId": track["trackId"],
            "pathY": np.asarray(track["pathY"]).tolist(),
            "spatialCoordinatesMm": np.asarray(track["spatialCoordinatesMm"]).tolist(),
        } for track in tracks],
        "coverage": np.asarray(field["coverage"]).tolist(),
    }


def _frame_capped_density(
    projected: list[dict[str, Any]], grid: np.ndarray
) -> np.ndarray:
    """Let each frame contribute only its strongest hypothesis at each spatial position."""
    density = np.zeros_like(grid, dtype=np.float64)
    frame_ids = sorted({int(item["observationIndex"]) for item in projected})
    for frame_id in frame_ids:
        frame = [item for item in projected if int(item["observationIndex"]) == frame_id]
        contributions = []
        for item in frame:
            weight = float(item["prominencePixels"]) * float(item["trackMeanRidgeScore"])
            contributions.append(
                weight * np.exp(-0.5 * ((grid - item["projectedCoordinateMm"]) / 1.5) ** 2)
            )
        if contributions:
            density += np.max(np.stack(contributions, axis=0), axis=0)
    return density


def cluster_multi_hypothesis_detections(
    detections: list[dict[str, Any]], sign: int, footprint_width_mm: float
) -> dict[str, Any]:
    projected = [{**item, "projectedCoordinateMm": projected_coordinate(
        item, sign, footprint_width_mm
    )} for item in detections]
    if not projected:
        return {"sign": sign, "clusters": [], "densityThreshold": None}
    minimum = min(item["projectedCoordinateMm"] for item in projected) - 10.0
    maximum = max(item["projectedCoordinateMm"] for item in projected) + 10.0
    grid = np.arange(minimum, maximum + 0.5, 0.5)
    density = _frame_capped_density(projected, grid)
    median = float(np.median(density))
    mad = float(np.median(np.abs(density - median)))
    acceptance_density_threshold = median + max(0.5, 1.0 * 1.4826 * mad)
    candidate_density_threshold = max(
        0.5, MINIMUM_CANDIDATE_DENSITY_FRACTION * acceptance_density_threshold
    )
    peak_indices = [
        index for index in range(2, len(grid) - 2)
        if density[index] >= candidate_density_threshold
        and density[index] == np.max(density[index - 2:index + 3])
    ]
    peak_indices.sort(key=lambda index: density[index], reverse=True)
    selected: list[float] = []
    for index in peak_indices:
        coordinate = float(grid[index])
        if all(abs(coordinate - other) >= CLUSTER_NMS_MM for other in selected):
            selected.append(coordinate)
    selected.sort()
    clusters = []
    for coordinate in selected:
        raw_support = [item for item in projected
                       if abs(item["projectedCoordinateMm"] - coordinate) <= CLUSTER_RADIUS_MM]
        depth_sorted = sorted(raw_support, key=lambda item: float(item["depthPixelY"]))
        components: list[list[dict[str, Any]]] = []
        for item in depth_sorted:
            if not components or (
                float(item["depthPixelY"]) - float(components[-1][-1]["depthPixelY"])
                > DEPTH_COMPONENT_GAP_PIXELS
            ):
                components.append([item])
            else:
                components[-1].append(item)
        component_summaries = []
        for component_index, component in enumerate(components):
            by_frame: dict[int, dict[str, Any]] = {}
            for item in component:
                frame_id = int(item["observationIndex"])
                score = float(item["prominencePixels"]) * float(item["trackMeanRidgeScore"])
                current = by_frame.get(frame_id)
                if current is None or score > current["_score"]:
                    by_frame[frame_id] = {**item, "_score": score}
            support = list(by_frame.values())
            values = np.asarray([item["projectedCoordinateMm"] for item in support], dtype=np.float64)
            depths = np.asarray([item["depthPixelY"] for item in support], dtype=np.float64)
            center = float(np.median(values)) if len(values) else coordinate
            mad_mm = float(np.median(np.abs(values - center))) if len(values) else math.inf
            outbound = [item for item in support if item["phase"] in OUTBOUND_PHASES]
            repeat = [item for item in support if item["phase"] in REPEAT_PHASES]
            outbound_center = float(np.median([
                item["projectedCoordinateMm"] for item in outbound
            ])) if outbound else None
            repeat_center = float(np.median([
                item["projectedCoordinateMm"] for item in repeat
            ])) if repeat else None
            disagreement = abs(outbound_center - repeat_center) \
                if outbound_center is not None and repeat_center is not None else None
            depth_iqr = float(np.subtract(*np.percentile(depths, [75, 25]))) \
                if len(depths) else math.inf
            shadow_fraction = float(np.mean([
                item["actualShadowSupported"] for item in support
            ])) if support else 0.0
            distal_verifiable_fraction = float(np.mean([
                float(item["minimumDistalObservationPixels"])
                >= MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS for item in support
            ])) if support else 0.0
            support_valid = (
                len(support) >= MINIMUM_DISTINCT_FRAMES
                and outbound_center is not None and repeat_center is not None
                and mad_mm <= MAXIMUM_CLUSTER_MAD_MM
                and disagreement <= MAXIMUM_PASS_DISAGREEMENT_MM
                and depth_iqr <= MAXIMUM_INTERFACE_DEPTH_IQR_PIXELS
            )
            morphology_valid = shadow_fraction >= MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION
            component_summaries.append({
                "componentId": f"D{component_index}",
                "centerMm": center,
                "supportIntervalMm": [float(np.min(values)), float(np.max(values))]
                    if len(values) else None,
                "distinctFrameCount": len(support),
                "supportingFrameIndices": sorted(by_frame),
                "supportingImages": list(dict.fromkeys(item["image"] for item in support)),
                "trackIds": sorted({item["trackId"] for item in support}),
                "medianDepthPixelY": float(np.median(depths)) if len(depths) else None,
                "medianAbsoluteDeviationMm": mad_mm,
                "outboundCenterMm": outbound_center,
                "repeatCenterMm": repeat_center,
                "forwardReverseDisagreementMm": disagreement,
                "interfaceDepthIqrPixels": depth_iqr,
                "actualShadowSupportFraction": shadow_fraction,
                "distalFieldVerifiableFraction": distal_verifiable_fraction,
                "supportValid": support_valid,
                "morphologyValid": morphology_valid,
                "accepted": support_valid and morphology_valid,
                "support": support,
            })
        component_summaries.sort(key=lambda item: (
            item["accepted"], item["distinctFrameCount"],
            item["actualShadowSupportFraction"], -item["medianAbsoluteDeviationMm"],
        ), reverse=True)
        best = component_summaries[0] if component_summaries else {
            "centerMm": coordinate, "supportIntervalMm": None,
            "distinctFrameCount": 0, "supportingFrameIndices": [],
            "supportingImages": [], "trackIds": [], "medianDepthPixelY": None,
            "medianAbsoluteDeviationMm": math.inf, "outboundCenterMm": None,
            "repeatCenterMm": None, "forwardReverseDisagreementMm": None,
            "interfaceDepthIqrPixels": math.inf, "actualShadowSupportFraction": 0.0,
            "distalFieldVerifiableFraction": 0.0,
            "supportValid": False, "morphologyValid": False, "accepted": False,
            "support": [],
        }
        accepted_components = [item for item in component_summaries if item["accepted"]]
        competing_identity = len(accepted_components) > 1
        accepted = best["accepted"] and not competing_identity
        reasons = []
        if best["distinctFrameCount"] < MINIMUM_DISTINCT_FRAMES:
            reasons.append("INSUFFICIENT_DISTINCT_FRAMES")
        if best["outboundCenterMm"] is None or best["repeatCenterMm"] is None:
            reasons.append("MISSING_DIRECTIONAL_SUPPORT")
        if best["medianAbsoluteDeviationMm"] > MAXIMUM_CLUSTER_MAD_MM:
            reasons.append("SPATIALLY_DIFFUSE")
        if (best["forwardReverseDisagreementMm"] is not None
                and best["forwardReverseDisagreementMm"] > MAXIMUM_PASS_DISAGREEMENT_MM):
            reasons.append("FORWARD_REVERSE_DISAGREEMENT")
        if best["interfaceDepthIqrPixels"] > MAXIMUM_INTERFACE_DEPTH_IQR_PIXELS:
            reasons.append("MIXED_INTERFACE_DEPTH_POPULATIONS")
        if not best["morphologyValid"]:
            reasons.append("INSUFFICIENT_ACTUAL_SHADOW_SUPPORT")
        if competing_identity:
            reasons.append("COMPETING_INTERFACE_IDENTITIES")
        classification = "SUPPORTED_ANATOMICAL_EVENT" if accepted else (
            "REJECTED_SECONDARY_OR_DUPLICATE_EVENT"
            if best["distalFieldVerifiableFraction"] >= 0.50
            and best["actualShadowSupportFraction"] < MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION
            else "AMBIGUOUS_INSUFFICIENT_EVIDENCE"
        )
        clusters.append({
            "densityPeakCoordinateMm": coordinate,
            "densityAtPeak": float(np.interp(coordinate, grid, density)),
            **{key: value for key, value in best.items() if key != "support"},
            "depthComponents": [
                {key: value for key, value in component.items() if key != "support"}
                for component in component_summaries
            ],
            "accepted": accepted,
            "classification": classification,
            "rejectionReasons": reasons,
            "support": support,
        })
    return {
        "sign": sign,
        "acceptanceDensityThreshold": acceptance_density_threshold,
        "candidateDensityThreshold": candidate_density_threshold,
        "gridMm": grid.tolist(),
        "density": density.tolist(),
        "clusters": clusters,
    }


def run_actor_tracking(source_root: Path) -> dict[str, Any]:
    actor = read_json(source_root / "trajectory_actor.json")
    footprint = configured_footprint_width(actor)
    detections, tracks = extract_multi_hypothesis_detections(source_root, actor)
    baseline_detections = [item for item in detections if item["trackRank"] == 0]
    baseline_choice, _ = select_axis_sign(baseline_detections, footprint)
    alternatives = [
        cluster_multi_hypothesis_detections(detections, sign, footprint)
        for sign in (-1, 1)
    ]
    chosen = next(item for item in alternatives if item["sign"] == baseline_choice["sign"])
    return {
        "contract": "thoracic_g2_r4_multi_hypothesis_tracking_v1",
        "inputIsolation": "Clean pixels, acquisition geometry, and actor-relative motion only.",
        "parameters": {
            "maximumTrackHypotheses": MAXIMUM_TRACK_HYPOTHESES,
            "minimumMedianTrackSeparationPixels": MINIMUM_MEDIAN_TRACK_SEPARATION_PIXELS,
            "minimumShadowPersistenceFraction": MINIMUM_SHADOW_PERSISTENCE_FRACTION,
            "minimumActualShadowSupportFraction": MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION,
        },
        "frameCount": len(tracks),
        "rawDetectionCount": len(detections),
        "imageAxisSignSelection": (
            "Locked to the immutable single-path baseline selection using repeat-supported "
            "cluster compactness; multi-path event count cannot change the image convention."
        ),
        "chosen": chosen,
        "alternatives": alternatives,
    }


def select_active_reacquisition_windows(
    clusters: list[dict[str, Any]], coverage: tuple[float, float], maximum_windows: int = 2
) -> list[dict[str, Any]]:
    """Select actor-relative revisit windows solely from unresolved image evidence."""
    eligible = []
    for cluster in clusters:
        if cluster["classification"] != "AMBIGUOUS_INSUFFICIENT_EVIDENCE":
            continue
        if cluster["actualShadowSupportFraction"] < MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION:
            continue
        coordinate = float(cluster["centerMm"])
        if not coverage[0] <= coordinate <= coverage[1]:
            continue
        missing_support = max(0, MINIMUM_DISTINCT_FRAMES - int(cluster["distinctFrameCount"]))
        missing_direction = int(
            cluster["outboundCenterMm"] is None or cluster["repeatCenterMm"] is None
        )
        score = (
            2.0 * min(int(cluster["distinctFrameCount"]), MINIMUM_DISTINCT_FRAMES)
            + 2.0 * missing_direction
            + 0.5 / (1.0 + missing_support)
        )
        interval = cluster.get("supportIntervalMm") or [coordinate, coordinate]
        half_width = float(np.clip(
            0.5 * abs(float(interval[1]) - float(interval[0])) + 3.0, 3.0, 6.0
        ))
        eligible.append({
            "actorRelativeCenterMm": coordinate,
            "windowMm": [
                max(float(coverage[0]), coordinate - half_width),
                min(float(coverage[1]), coordinate + half_width),
            ],
            "priorityScore": score,
            "trigger": list(cluster["rejectionReasons"]),
            "imageEvidence": {
                "distinctFrameCount": cluster["distinctFrameCount"],
                "actualShadowSupportFraction": cluster["actualShadowSupportFraction"],
                "outboundSupport": cluster["outboundCenterMm"] is not None,
                "repeatSupport": cluster["repeatCenterMm"] is not None,
            },
            "coordinateSource": "ACTOR_RELATIVE_IMAGE_EVENT_HYPOTHESIS",
        })
    eligible.sort(key=lambda item: (
        item["priorityScore"], item["imageEvidence"]["distinctFrameCount"]
    ), reverse=True)
    selected = []
    for item in eligible:
        if all(
            abs(item["actorRelativeCenterMm"] - other["actorRelativeCenterMm"])
            >= CLUSTER_NMS_MM for other in selected
        ):
            selected.append(item)
        if len(selected) >= maximum_windows:
            break
    return selected


def build_ambiguity_aware_map(clusters: list[dict[str, Any]]) -> dict[str, Any]:
    accepted = sorted(
        (item for item in clusters if item["classification"] == "SUPPORTED_ANATOMICAL_EVENT"),
        key=lambda item: item["centerMm"],
    )
    ambiguous = sorted(
        (item for item in clusters if item["classification"] == "AMBIGUOUS_INSUFFICIENT_EVIDENCE"),
        key=lambda item: item["centerMm"],
    )
    consequential = []
    if accepted:
        low = accepted[0]["centerMm"]
        high = accepted[-1]["centerMm"]
        consequential = [item for item in ambiguous if low <= item["centerMm"] <= high]
    unique = len(accepted) == 7 and not consequential
    levels = []
    names = ("T2", "T3", "T4", "T5", "T6", "T7", "T8")
    for index, name in enumerate(names):
        event = accepted[index] if unique else None
        levels.append({
            "ordinal": index + 1,
            "inferredLevel": name,
            "centerRelativeMm": event["centerMm"] if event else None,
            "uncertaintyIntervalMm": event["supportIntervalMm"] if event else None,
            "supportingFrames": event["supportingFrameIndices"] if event else [],
            "eventType": "IMAGE_SUPPORTED_RECESS_EVENT" if event else "UNRESOLVED",
            "accepted": bool(event),
        })
    return {
        "contract": "thoracic_g2_r4_ambiguity_aware_relative_map_v1",
        "homeRelativeOriginMm": 0.0,
        "coordinateSource": "accepted_actual_achieved_relative_motion",
        "coveragePrior": "exactly_seven_consecutive_levels_T2_to_T8",
        "directionConvention": "cranial_negative_caudad_positive",
        "acceptedEventCount": len(accepted),
        "ambiguousEventCount": len(ambiguous),
        "consequentialAmbiguousCoordinatesMm": [item["centerMm"] for item in consequential],
        "levels": levels,
        "predictedT7RelativeMm": levels[5]["centerRelativeMm"] if unique else None,
        "overallMapStatus": "ACCEPTED_UNIQUE_SEQUENCE" if unique else "INCONCLUSIVE",
        "abstentionReason": None if unique else (
            "Seven supported events and no consequential ambiguous insertion are required; "
            f"observed {len(accepted)} supported and {len(consequential)} consequential ambiguous."
        ),
    }

