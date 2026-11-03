"""Image-only longitudinal interface tracking for G2-R2 development and freeze."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw

from g2_r2_continuous_detector import (
    LEVELS,
    build_relative_map,
    read_json,
    validate_actor_trace,
    write_json,
    write_map_csv,
)
from relative_spine_e2_analysis import load_gray, sha256


FOOTPRINT_WIDTH_MM = 45.0 + 2.0 * 50.0 * math.sin(math.radians(3.0))
TRACK_X_MARGIN_PIXELS = 18
TRACK_Y_MIN_FRACTION = 0.50
TRACK_Y_MAX_FRACTION = 0.96
MAX_PATH_STEP_PIXELS = 6
PATH_SMOOTHNESS_PENALTY = 0.014
VALLEY_PROMINENCE_PIXELS = 3.0
VALLEY_SIDE_RADIUS_PIXELS = 24
VALLEY_NMS_PIXELS = 24
CLUSTER_RADIUS_MM = 3.0
CLUSTER_NMS_MM = 8.0
MINIMUM_DISTINCT_FRAMES = 6
MAXIMUM_CLUSTER_MAD_MM = 2.5
MAXIMUM_PASS_DISAGREEMENT_MM = 3.0
MAXIMUM_INTERFACE_DEPTH_IQR_PIXELS = 12.0
OUTBOUND_PHASES = {"cranial_outbound", "forward_baseline", "caudal_outbound"}
REPEAT_PHASES = {"cranial_return", "caudal_return"}
FREEZE_FILES = (
    "interface_tracking_observations.csv",
    "event_candidates.json",
    "event_tracking_diagnostics.json",
    "event_rejection_reasons.json",
    "relative_spine_map.json",
    "relative_spine_map.csv",
    "mapping_uncertainty.json",
)


def smooth_vector(values: np.ndarray, width: int = 7) -> np.ndarray:
    width = max(1, int(width) | 1)
    pad = width // 2
    padded = np.pad(values, (pad, pad), mode="edge")
    return np.convolve(padded, np.ones(width) / width, mode="valid")


def track_bright_interface(image: np.ndarray) -> dict[str, Any]:
    height, width = image.shape
    x_values = np.arange(TRACK_X_MARGIN_PIXELS, width - TRACK_X_MARGIN_PIXELS)
    y_min = int(round(height * TRACK_Y_MIN_FRACTION))
    y_max = int(round(height * TRACK_Y_MAX_FRACTION))
    region = image[y_min:y_max, :]
    vertical_gradient = np.abs(np.gradient(region, axis=0))
    gradient_scale = max(float(np.quantile(vertical_gradient, 0.95)), 1e-6)
    ridge = 0.65 * region + 0.35 * np.clip(vertical_gradient / gradient_scale, 0.0, 2.0)
    y_count = y_max - y_min
    cost = -ridge[:, x_values[0]].copy()
    back = np.zeros((len(x_values), y_count), dtype=np.int8)
    for x_index, x_value in enumerate(x_values[1:], start=1):
        candidates = []
        shifts = range(-MAX_PATH_STEP_PIXELS, MAX_PATH_STEP_PIXELS + 1)
        for shift in shifts:
            shifted = np.full(y_count, np.inf)
            if shift < 0:
                shifted[:shift] = cost[-shift:]
            elif shift > 0:
                shifted[shift:] = cost[:-shift]
            else:
                shifted[:] = cost
            candidates.append(shifted + PATH_SMOOTHNESS_PENALTY * shift * shift)
        matrix = np.stack(candidates, axis=0)
        best = np.argmin(matrix, axis=0)
        back[x_index] = np.asarray(list(shifts), dtype=np.int8)[best]
        cost = matrix[best, np.arange(y_count)] - ridge[:, x_value]
    path = np.zeros(len(x_values), dtype=np.int32)
    path[-1] = int(np.argmin(cost))
    for x_index in range(len(x_values) - 1, 0, -1):
        path[x_index - 1] = path[x_index] - int(back[x_index, path[x_index]])
    path = np.clip(path, 0, y_count - 1) + y_min
    smoothed = smooth_vector(path.astype(np.float64))
    ridge_samples = ridge[path - y_min, x_values]
    jumps = np.abs(np.diff(smoothed))
    return {
        "x": x_values,
        "pathY": smoothed,
        "ridgeScores": ridge_samples,
        "meanRidgeScore": float(np.mean(ridge_samples)),
        "maximumPathJumpPixels": float(np.max(jumps, initial=0.0)),
        "valid": bool(np.mean(ridge_samples) >= 0.18 and np.max(jumps, initial=0.0) <= 4.0),
    }


def detect_interface_valleys(track: dict[str, Any]) -> list[dict[str, float]]:
    if not track["valid"]:
        return []
    x_values = track["x"]
    path = track["pathY"]
    candidates = []
    radius = VALLEY_SIDE_RADIUS_PIXELS
    for index in range(radius, len(path) - radius):
        if path[index] < np.max(path[index - 3:index + 4]):
            continue
        left_base = float(np.min(path[index - radius:index - 3]))
        right_base = float(np.min(path[index + 4:index + radius + 1]))
        prominence = float(path[index] - max(left_base, right_base))
        if prominence < VALLEY_PROMINENCE_PIXELS:
            continue
        candidates.append({
            "pixelX": float(x_values[index]),
            "depthPixelY": float(path[index]),
            "prominencePixels": prominence,
        })
    kept = []
    for candidate in sorted(candidates, key=lambda item: item["prominencePixels"], reverse=True):
        if all(abs(candidate["pixelX"] - other["pixelX"]) >= VALLEY_NMS_PIXELS
               for other in kept):
            kept.append(candidate)
    return sorted(kept, key=lambda item: item["pixelX"])


def extract_detections(root: Path, actor: dict[str, Any]) -> tuple[list[dict[str, Any]], dict[int, Any]]:
    observations = validate_actor_trace(root, actor)
    detections = []
    tracks = {}
    for observation in observations:
        image = load_gray(root / observation["image"])
        track = track_bright_interface(image)
        valleys = detect_interface_valleys(track)
        tracks[observation["observationIndex"]] = {"track": track, "valleys": valleys}
        for valley in valleys:
            detections.append({
                "observationIndex": observation["observationIndex"],
                "phase": observation["phase"],
                "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
                "image": observation["image"],
                "imageSha256": observation["imageSha256"],
                "imageWidth": image.shape[1],
                "pixelX": valley["pixelX"],
                "depthPixelY": valley["depthPixelY"],
                "prominencePixels": valley["prominencePixels"],
                "trackMeanRidgeScore": track["meanRidgeScore"],
            })
    return detections, tracks


def configured_footprint_width(actor: dict[str, Any]) -> float:
    value = float(actor.get("renderedFootprintWidthMm", FOOTPRINT_WIDTH_MM))
    if not 8.0 <= value <= 60.0:
        raise ValueError(f"Implausible rendered ultrasound footprint: {value}")
    return value


def projected_coordinate(
    detection: dict[str, Any], sign: int, footprint_width_mm: float = FOOTPRINT_WIDTH_MM
) -> float:
    center = 0.5 * (float(detection["imageWidth"]) - 1.0)
    image_offset = (float(detection["pixelX"]) - center) / max(center * 2.0, 1.0)
    return float(detection["probeCoordinateMm"] + sign * image_offset * footprint_width_mm)


def cluster_for_sign(
    detections: list[dict[str, Any]], sign: int,
    footprint_width_mm: float = FOOTPRINT_WIDTH_MM,
) -> dict[str, Any]:
    projected = [{**item, "projectedCoordinateMm": projected_coordinate(
        item, sign, footprint_width_mm)}
                 for item in detections]
    if not projected:
        return {"sign": sign, "clusters": [], "qualityScore": -np.inf}
    minimum = min(item["projectedCoordinateMm"] for item in projected) - 10.0
    maximum = max(item["projectedCoordinateMm"] for item in projected) + 10.0
    grid = np.arange(minimum, maximum + 0.5, 0.5)
    density = np.zeros_like(grid)
    for item in projected:
        weight = item["prominencePixels"] * item["trackMeanRidgeScore"]
        density += weight * np.exp(-0.5 * ((grid - item["projectedCoordinateMm"]) / 1.5) ** 2)
    threshold = float(np.median(density) + max(0.5, 1.25 * 1.4826
                                               * np.median(np.abs(density - np.median(density)))))
    peak_indices = [index for index in range(2, len(grid) - 2)
                    if density[index] >= threshold
                    and density[index] == np.max(density[index - 2:index + 3])]
    peak_indices.sort(key=lambda index: density[index], reverse=True)
    selected = []
    for index in peak_indices:
        coordinate = float(grid[index])
        if all(abs(coordinate - item) >= CLUSTER_NMS_MM for item in selected):
            selected.append(coordinate)
    selected.sort()
    clusters = []
    for coordinate in selected:
        support = [item for item in projected
                   if abs(item["projectedCoordinateMm"] - coordinate) <= CLUSTER_RADIUS_MM]
        frame_ids = sorted({item["observationIndex"] for item in support})
        outbound = [item for item in support if item["phase"] in OUTBOUND_PHASES]
        repeat = [item for item in support if item["phase"] in REPEAT_PHASES]
        values = np.asarray([item["projectedCoordinateMm"] for item in support])
        depths = np.asarray([item["depthPixelY"] for item in support])
        center = float(np.median(values)) if len(values) else coordinate
        mad = float(np.median(np.abs(values - center))) if len(values) else float("inf")
        outbound_center = float(np.median([item["projectedCoordinateMm"] for item in outbound])) \
            if outbound else None
        repeat_center = float(np.median([item["projectedCoordinateMm"] for item in repeat])) \
            if repeat else None
        disagreement = abs(outbound_center - repeat_center) \
            if outbound_center is not None and repeat_center is not None else None
        depth_quartiles = np.quantile(depths, (0.25, 0.75)) if len(depths) else (0.0, np.inf)
        depth_iqr = float(depth_quartiles[1] - depth_quartiles[0])
        accepted = (
            len(frame_ids) >= MINIMUM_DISTINCT_FRAMES
            and outbound_center is not None and repeat_center is not None
            and mad <= MAXIMUM_CLUSTER_MAD_MM
            and disagreement <= MAXIMUM_PASS_DISAGREEMENT_MM
            and depth_iqr <= MAXIMUM_INTERFACE_DEPTH_IQR_PIXELS
        )
        clusters.append({
            "centerMm": center,
            "supportIntervalMm": [float(np.min(values)), float(np.max(values))] if len(values) else None,
            "distinctFrameCount": len(frame_ids),
            "supportingFrameIndices": frame_ids,
            "supportingImages": list(dict.fromkeys(item["image"] for item in support)),
            "medianAbsoluteDeviationMm": mad,
            "outboundCenterMm": outbound_center,
            "repeatCenterMm": repeat_center,
            "forwardReverseDisagreementMm": disagreement,
            "meanProminencePixels": float(np.mean([item["prominencePixels"] for item in support]))
                if support else 0.0,
            "medianInterfaceDepthPixelY": float(np.median(depths)) if len(depths) else None,
            "interfaceDepthIqrPixels": depth_iqr,
            "accepted": accepted,
            "rejectionReasons": [
                reason for condition, reason in (
                    (len(frame_ids) < MINIMUM_DISTINCT_FRAMES, "INSUFFICIENT_DISTINCT_FRAMES"),
                    (outbound_center is None or repeat_center is None, "MISSING_DIRECTIONAL_SUPPORT"),
                    (mad > MAXIMUM_CLUSTER_MAD_MM, "SPATIALLY_DIFFUSE"),
                    (disagreement is not None and disagreement > MAXIMUM_PASS_DISAGREEMENT_MM,
                     "FORWARD_REVERSE_DISAGREEMENT"),
                    (depth_iqr > MAXIMUM_INTERFACE_DEPTH_IQR_PIXELS,
                     "MIXED_INTERFACE_DEPTH_POPULATIONS"),
                ) if condition
            ],
        })
    accepted = [item for item in clusters if item["accepted"]]
    compactness = float(np.mean([item["medianAbsoluteDeviationMm"] for item in accepted])) \
        if accepted else 10.0
    support_score = sum(item["distinctFrameCount"] for item in accepted)
    quality = support_score / (1.0 + compactness)
    return {"sign": sign, "densityThreshold": threshold, "clusters": clusters,
            "acceptedCount": len(accepted), "qualityScore": quality}


def select_axis_sign(
    detections: list[dict[str, Any]], footprint_width_mm: float = FOOTPRINT_WIDTH_MM
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    alternatives = [cluster_for_sign(detections, sign, footprint_width_mm) for sign in (-1, 1)]
    alternatives.sort(key=lambda item: (item["qualityScore"], item["acceptedCount"]), reverse=True)
    return alternatives[0], alternatives


def write_tracking_csv(
    path: Path, detections: list[dict[str, Any]], sign: int, footprint_width_mm: float
) -> None:
    fields = (
        "observationIndex", "phase", "probeCoordinateMm", "pixelX", "depthPixelY",
        "prominencePixels", "trackMeanRidgeScore", "projectedCoordinateMm", "image",
    )
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for item in detections:
            row = {name: item.get(name) for name in fields}
            row["projectedCoordinateMm"] = projected_coordinate(item, sign, footprint_width_mm)
            writer.writerow(row)


def draw_tracking_plot(
    path: Path, detections: list[dict[str, Any]], chosen: dict[str, Any],
    footprint_width_mm: float,
) -> None:
    figure, axis = plt.subplots(figsize=(15, 7), constrained_layout=True)
    for phases, label, color in ((OUTBOUND_PHASES, "outbound", "tab:blue"),
                                 (REPEAT_PHASES, "reverse/revisit", "tab:orange")):
        selected = [item for item in detections if item["phase"] in phases]
        axis.scatter([item["probeCoordinateMm"] for item in selected],
                     [projected_coordinate(item, chosen["sign"], footprint_width_mm)
                      for item in selected],
                     s=12, alpha=0.45, label=label, color=color)
    for cluster in chosen["clusters"]:
        axis.axhline(cluster["centerMm"], color="tab:green" if cluster["accepted"] else "tab:red",
                     alpha=0.35, linestyle="-" if cluster["accepted"] else "--")
    axis.set_xlabel("Probe actor-relative coordinate from fixed Home (mm)")
    axis.set_ylabel("Tracked interface valley coordinate after 40 mm footprint projection (mm)")
    axis.grid(alpha=0.25)
    axis.legend()
    axis.set_title("G2-R2 image-only interface tracking and spatial consolidation")
    figure.savefig(path, dpi=170)
    plt.close(figure)


def draw_event_sheet(source_root: Path, candidates: list[dict[str, Any]], output: Path) -> None:
    if not candidates:
        image = Image.new("RGB", (1024, 180), "white")
        ImageDraw.Draw(image).text((20, 70), "NO ACCEPTED INTERFACE-TRACKED EVENTS", fill="black")
        image.save(output)
        return
    rows = []
    for candidate in candidates:
        images = candidate["supportingImages"][:4]
        tile = Image.new("RGB", (1024, 300), "white")
        for index, image_name in enumerate(images):
            with Image.open(source_root / image_name) as source:
                tile.paste(source.convert("RGB").resize((256, 256)), (index * 256, 0))
        ImageDraw.Draw(tile).text(
            (8, 270), f"{candidate['candidateId']} {candidate['actorRelativeCoordinateMm']:.2f} mm; "
            f"frames={candidate['distinctFrameCount']}", fill="black")
        rows.append(tile)
    sheet = Image.new("RGB", (1024, 300 * len(rows)), "white")
    for index, tile in enumerate(rows):
        sheet.paste(tile, (0, index * 300))
    sheet.save(output)


def freeze(output_root: Path, source_root: Path, source_path: Path) -> str:
    entries = [(name, sha256(output_root / name)) for name in FREEZE_FILES]
    entries.extend((
        ("INPUT/trajectory_actor.json", sha256(source_root / "trajectory_actor.json")),
        (source_path.as_posix(), sha256(source_path)),
    ))
    text = "".join(f"{digest}  {name}\n" for name, digest in entries)
    (output_root / "blind_results.sha256").write_text(text, encoding="utf-8")
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def verify_freeze(output_root: Path, source_root: Path, source_path: Path) -> None:
    expected = {}
    for line in (output_root / "blind_results.sha256").read_text(encoding="utf-8").splitlines():
        digest, name = line.split("  ", 1)
        expected[name] = digest
    for name in FREEZE_FILES:
        if expected.get(name) != sha256(output_root / name):
            raise ValueError(f"Frozen tracker output changed: {name}")
    if expected.get("INPUT/trajectory_actor.json") != sha256(source_root / "trajectory_actor.json"):
        raise ValueError("Frozen tracker input changed")
    if expected.get(source_path.as_posix()) != sha256(source_path):
        raise ValueError("Frozen tracker source changed")


def run_blind(source_root: Path, output_root: Path, source_path: Path) -> dict[str, Any]:
    output_root.mkdir(parents=True, exist_ok=False)
    actor = read_json(source_root / "trajectory_actor.json")
    footprint_width_mm = configured_footprint_width(actor)
    detections, tracks = extract_detections(source_root, actor)
    chosen, alternatives = select_axis_sign(detections, footprint_width_mm)
    accepted_clusters = sorted((item for item in chosen["clusters"] if item["accepted"]),
                               key=lambda item: item["centerMm"])
    candidates = []
    for index, cluster in enumerate(accepted_clusters):
        candidates.append({
            "candidateId": f"E{index + 1:02d}",
            "actorRelativeCoordinateMm": cluster["centerMm"],
            "uncertaintyIntervalMm": cluster["supportIntervalMm"],
            "distinctFrameCount": cluster["distinctFrameCount"],
            "supportingFrameIndices": cluster["supportingFrameIndices"],
            "supportingImages": cluster["supportingImages"],
            "meanProminencePixels": cluster["meanProminencePixels"],
            "medianInterfaceDepthPixelY": cluster["medianInterfaceDepthPixelY"],
            "interfaceDepthIqrPixels": cluster["interfaceDepthIqrPixels"],
            "forwardReverseDisagreementMm": cluster["forwardReverseDisagreementMm"],
            "eventType": "CONTINUITY_TRACKED_BRIGHT_INTERFACE_RECESS_VALLEY",
            "accepted": True,
            "acceptReason": "FOOTPRINT_PROJECTED_MULTI_FRAME_CLUSTER_WITH_REVERSE_SUPPORT",
        })
    relative_map = build_relative_map(candidates)
    write_tracking_csv(output_root / "interface_tracking_observations.csv", detections,
                       chosen["sign"], footprint_width_mm)
    write_json(output_root / "event_candidates.json", {
        "contract": "thoracic_g2_r2_interface_tracked_events_v1",
        "inputIsolation": "Ultrasound pixels, acquisition footprint, and actor-relative motion only.",
        "candidateCount": len(candidates), "candidates": candidates,
    })
    write_json(output_root / "event_tracking_diagnostics.json", {
        "contract": "thoracic_g2_r2_interface_tracking_diagnostics_v1",
        "physicalInterpretation": (
            "A continuity-constrained lower bright-interface path is tracked in image coordinates. "
            "Downward valleys are projected through the configured rendered longitudinal footprint and clustered "
            "in actor-relative coordinates across independent traversal directions."
        ),
        "parameters": {
            "footprintWidthMm": footprint_width_mm,
            "footprintSource": "actor acquisition configuration or locked scene-config fallback",
            "pathSmoothnessPenalty": PATH_SMOOTHNESS_PENALTY,
            "valleyProminencePixels": VALLEY_PROMINENCE_PIXELS,
            "clusterRadiusMm": CLUSTER_RADIUS_MM,
            "minimumDistinctFrames": MINIMUM_DISTINCT_FRAMES,
            "maximumClusterMadMm": MAXIMUM_CLUSTER_MAD_MM,
            "maximumPassDisagreementMm": MAXIMUM_PASS_DISAGREEMENT_MM,
            "maximumInterfaceDepthIqrPixels": MAXIMUM_INTERFACE_DEPTH_IQR_PIXELS,
        },
        "axisSignSelection": (
            "Selected by repeat-supported cluster compactness and support count; event count and evaluator "
            "coordinates are not selection inputs."
        ),
        "chosenImageAxisSign": chosen["sign"],
        "signAlternatives": alternatives,
        "validTrackedFrameCount": sum(item["track"]["valid"] for item in tracks.values()),
        "totalFrameCount": len(tracks),
        "rawValleyDetectionCount": len(detections),
    })
    rejected = [item for item in chosen["clusters"] if not item["accepted"]]
    write_json(output_root / "event_rejection_reasons.json", {
        "contract": "thoracic_g2_r2_interface_rejections_v1", "rejected": rejected,
    })
    write_json(output_root / "relative_spine_map.json", relative_map)
    write_map_csv(output_root / "relative_spine_map.csv", relative_map)
    write_json(output_root / "mapping_uncertainty.json", {
        "contract": "thoracic_g2_r2_interface_mapping_uncertainty_v1",
        "candidateCount": len(candidates),
        "mapAccepted": relative_map["overallMapStatus"] == "ACCEPTED_BLIND_HYPOTHESIS",
        "criticalRule": "Counts other than seven abstain; no evaluator coordinate removes extra clusters.",
        "coordinateIntervals": {item["candidateId"]: item["uncertaintyIntervalMm"]
                                for item in candidates},
    })
    draw_tracking_plot(output_root / "anatomical_waveform.png", detections, chosen,
                       footprint_width_mm)
    draw_event_sheet(source_root, candidates, output_root / "event_support_contact_sheet.png")
    blind_hash = freeze(output_root, source_root, source_path)
    return {"candidateCount": len(candidates), "mapStatus": relative_map["overallMapStatus"],
            "predictedT7RelativeMm": relative_map["predictedT7RelativeMm"],
            "chosenImageAxisSign": chosen["sign"], "blindManifestSha256": blind_hash}


def reference_coordinates(source_root: Path, r1_root: Path) -> tuple[list[dict[str, Any]], float]:
    actor = read_json(source_root / "trajectory_actor.json")
    evaluator = read_json(source_root / "evaluator_only" / "trajectory_privileged.json")
    pairs = sorted(
        (float(item["contactPoint"]["z"]),
         float(actor["observations"][item["observationIndex"]]["signedActorRelativeMm"]))
        for item in evaluator["observations"] if item["phase"] in OUTBOUND_PHASES
    )
    z = np.asarray([item[0] for item in pairs])
    s = np.asarray([item[1] for item in pairs])
    order = np.argsort(z)
    interpolate = lambda value: float(np.interp(value, z[order], s[order]))
    reference_map = read_json(r1_root / "bone_structure_reference_map.json")
    references = [{
        "level": item["level"],
        "coordinateMm": interpolate(float(item["validatedPosteriorRecessCoordinateZMm"])),
        "semantics": item["anatomicalIdentity"],
    } for item in reference_map["validatedAnatomyDerivedLandmarks"]]
    references.sort(key=lambda item: item["coordinateMm"])
    for index, item in enumerate(references):
        neighbors = []
        if index:
            neighbors.append(item["coordinateMm"] - references[index - 1]["coordinateMm"])
        if index + 1 < len(references):
            neighbors.append(references[index + 1]["coordinateMm"] - item["coordinateMm"])
        item["toleranceMm"] = min(5.0, 0.25 * min(abs(value) for value in neighbors))
    geometry = read_json(r1_root / "geometry_gap_candidates.json")
    extra = next(item for item in geometry["multiOffsetCandidates"]
                 if abs(float(item["centerZMm"]) - 33.5) < 1e-6)
    return references, interpolate(float(extra["centerZMm"]))


def order_preserving_match(candidates: list[dict[str, Any]], references: list[dict[str, Any]]) -> list[dict[str, Any]]:
    n, m = len(candidates), len(references)
    dp = [[(0, 0.0, []) for _ in range(m + 1)] for _ in range(n + 1)]
    for i in range(n + 1):
        for j in range(m + 1):
            matches, negative_error, pairs = dp[i][j]
            if i < n and dp[i + 1][j][0:2] < (matches, negative_error):
                dp[i + 1][j] = (matches, negative_error, pairs)
            if j < m and dp[i][j + 1][0:2] < (matches, negative_error):
                dp[i][j + 1] = (matches, negative_error, pairs)
            if i < n and j < m:
                error = abs(candidates[i]["actorRelativeCoordinateMm"] - references[j]["coordinateMm"])
                if error <= references[j]["toleranceMm"]:
                    proposed = (matches + 1, negative_error - error, pairs + [(i, j, error)])
                    if dp[i + 1][j + 1][0:2] < proposed[0:2]:
                        dp[i + 1][j + 1] = proposed
    matched = {(i, j): error for i, j, error in dp[n][m][2]}
    rows = []
    for i, candidate in enumerate(candidates):
        pair = next(((j, error) for (candidate_i, j), error in matched.items() if candidate_i == i), None)
        rows.append({
            "candidateId": candidate["candidateId"],
            "predictedCoordinateMm": candidate["actorRelativeCoordinateMm"],
            "matchedLevel": references[pair[0]]["level"] if pair else None,
            "referenceCoordinateMm": references[pair[0]]["coordinateMm"] if pair else None,
            "absoluteErrorMm": pair[1] if pair else None,
            "matched": pair is not None,
        })
    return rows


def run_evaluate(source_root: Path, output_root: Path, r1_root: Path, source_path: Path) -> dict[str, Any]:
    verify_freeze(output_root, source_root, source_path)
    candidates = read_json(output_root / "event_candidates.json")["candidates"]
    relative_map = read_json(output_root / "relative_spine_map.json")
    references, extra_coordinate = reference_coordinates(source_root, r1_root)
    matches = order_preserving_match(candidates, references)
    true_positive = sum(item["matched"] for item in matches)
    false_positive = len(candidates) - true_positive
    false_negative = len(references) - true_positive
    precision = true_positive / max(len(candidates), 1)
    recall = true_positive / len(references)
    f1 = 2 * precision * recall / max(precision + recall, 1e-12)
    exact = len(candidates) == 7 and true_positive == 7
    result = {
        "contract": "thoracic_g2_r2_interface_evaluator_v1",
        "blindFreezeVerified": True,
        "referenceSemantics": "Validated LF-associated posterior bone recess regions.",
        "references": references,
        "candidateMatches": matches,
        "metrics": {"truePositive": true_positive, "falsePositive": false_positive,
                    "falseNegative": false_negative, "precision": precision, "recall": recall,
                    "f1": f1, "exactSevenEventSequence": exact},
        "g2R2": "PASS" if exact and f1 >= 0.95 else "FAIL",
        "g3R2": "PASS" if exact and relative_map["predictedT7RelativeMm"] is not None else "NOT_RUN",
    }
    evaluator_root = output_root / "evaluator_only"
    evaluator_root.mkdir(exist_ok=True)
    write_json(evaluator_root / "anatomical_correspondence.json", result)
    near_extra = [item for item in candidates
                  if abs(item["actorRelativeCoordinateMm"] - extra_coordinate) <= 5.0]
    negative = {
        "contract": "thoracic_g2_r2_interface_extra_recess_control_v1",
        "revealedOnlyAfterBlindFreeze": True,
        "extraCandidateActorRelativeMm": extra_coordinate,
        "blindCandidatesWithinFiveMm": [item["candidateId"] for item in near_extra],
        "incorrectlyAcceptedAsVertebralLevel": bool(
            near_extra and relative_map["overallMapStatus"] == "ACCEPTED_BLIND_HYPOTHESIS"
        ),
        "actorWasToldExtraCandidateCoordinate": False,
    }
    write_json(evaluator_root / "negative_control_results.json", negative)
    return {"metrics": result["metrics"], "g2R2": result["g2R2"],
            "g3R2": result["g3R2"], "negativeControl": negative}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", choices=("blind", "evaluate"), required=True)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--r1-root", type=Path)
    args = parser.parse_args()
    source_root = args.source_root.resolve()
    output_root = args.output_root.resolve()
    source_path = Path(__file__).resolve()
    if args.stage == "blind":
        result = run_blind(source_root, output_root, source_path)
    else:
        if args.r1_root is None:
            parser.error("--r1-root is required for evaluator stage")
        result = run_evaluate(source_root, output_root, args.r1_root.resolve(), source_path)
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
