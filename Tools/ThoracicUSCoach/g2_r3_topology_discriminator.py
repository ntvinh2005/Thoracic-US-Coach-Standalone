"""G2-R3 image-only interface-termination topology diagnostics and filtering."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import numpy as np

from g2_r2_continuous_detector import build_relative_map, read_json
from g2_r2_interface_tracker import (
    CLUSTER_RADIUS_MM,
    configured_footprint_width,
    extract_detections,
    projected_coordinate,
    select_axis_sign,
)
from relative_spine_e2_analysis import load_gray


INNER_HALF_WIDTH_PIXELS = 5
SHOULDER_INNER_PIXELS = 14
SHOULDER_OUTER_PIXELS = 28
PATH_PATCH_HALF_HEIGHT_PIXELS = 2
MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS = 45.0


def _path_brightness(image: np.ndarray, track: dict[str, Any]) -> np.ndarray:
    values = []
    for x_value, y_value in zip(track["x"], track["pathY"]):
        x = int(round(float(x_value)))
        y = int(round(float(y_value)))
        patch = image[
            max(0, y - PATH_PATCH_HALF_HEIGHT_PIXELS):
            min(image.shape[0], y + PATH_PATCH_HALF_HEIGHT_PIXELS + 1),
            max(0, x - 1):min(image.shape[1], x + 2),
        ]
        values.append(float(np.mean(patch)))
    return np.asarray(values, dtype=np.float64)


def measure_interface_termination(
    image: np.ndarray, track: dict[str, Any], pixel_x: float
) -> dict[str, float | bool]:
    """Measure whether a tracked valley remains bridged or terminates and reappears."""
    x_values = np.asarray(track["x"], dtype=np.float64)
    center_index = int(np.argmin(np.abs(x_values - float(pixel_x))))
    left = slice(max(0, center_index - SHOULDER_OUTER_PIXELS),
                 max(1, center_index - SHOULDER_INNER_PIXELS))
    right = slice(min(len(x_values) - 1, center_index + SHOULDER_INNER_PIXELS),
                  min(len(x_values), center_index + SHOULDER_OUTER_PIXELS))
    center = slice(max(0, center_index - INNER_HALF_WIDTH_PIXELS),
                   min(len(x_values), center_index + INNER_HALF_WIDTH_PIXELS + 1))
    brightness = _path_brightness(image, track)
    ridge = np.asarray(track["ridgeScores"], dtype=np.float64)
    left_brightness = float(np.mean(brightness[left]))
    right_brightness = float(np.mean(brightness[right]))
    shoulder_brightness = 0.5 * (left_brightness + right_brightness)
    shoulder_ridge = 0.5 * (float(np.mean(ridge[left])) + float(np.mean(ridge[right])))
    center_brightness = float(np.mean(brightness[center]))
    center_ridge = float(np.mean(ridge[center]))
    local = brightness[
        max(0, center_index - SHOULDER_INNER_PIXELS):
        min(len(brightness), center_index + SHOULDER_INNER_PIXELS + 1)
    ]
    relative_cutoff = 0.55 * max(shoulder_brightness, 1e-6)
    low_mask = local < relative_cutoff
    longest_low_run = 0
    current = 0
    for value in low_mask:
        current = current + 1 if value else 0
        longest_low_run = max(longest_low_run, current)
    strip_x_min = max(0, int(round(pixel_x)) - INNER_HALF_WIDTH_PIXELS)
    strip_x_max = min(image.shape[1], int(round(pixel_x)) + INNER_HALF_WIDTH_PIXELS + 1)
    path_y = int(round(float(track["pathY"][center_index])))
    proximal = image[
        max(0, path_y - 30):max(1, path_y - 6), strip_x_min:strip_x_max
    ]
    distal = image[
        min(image.shape[0] - 1, path_y + 6):min(image.shape[0], path_y + 36),
        strip_x_min:strip_x_max,
    ]
    proximal_mean = float(np.mean(proximal)) if proximal.size else 0.0
    distal_mean = float(np.mean(distal)) if distal.size else 0.0
    distal_gradient = float(np.mean(np.abs(np.gradient(distal, axis=0)))) \
        if distal.shape[0] >= 2 else 0.0
    return {
        "centerPathBrightness": center_brightness,
        "leftShoulderBrightness": left_brightness,
        "rightShoulderBrightness": right_brightness,
        "bridgeBrightnessRatio": center_brightness / max(shoulder_brightness, 1e-6),
        "bridgeRidgeRatio": center_ridge / max(shoulder_ridge, 1e-6),
        "lowBridgeFraction": float(np.mean(low_mask)),
        "longestLowBridgeRunPixels": float(longest_low_run),
        "shoulderAsymmetry": abs(left_brightness - right_brightness)
        / max(shoulder_brightness, 1e-6),
        "bothShouldersVisible": bool(min(left_brightness, right_brightness) >= 0.025),
        "proximalMeanIntensity": proximal_mean,
        "distalMeanIntensity": distal_mean,
        "distalToProximalIntensityRatio": distal_mean / max(proximal_mean, 1e-6),
        "shadowOnsetContrast": (proximal_mean - distal_mean) / max(proximal_mean, 1e-6),
        "distalGradientEnergy": distal_gradient,
        "distalDarkFraction": float(np.mean(distal < 0.025)) if distal.size else 0.0,
    }


def candidate_topology_measurements(
    source_root: Path, candidate_root: Path
) -> dict[str, Any]:
    actor = read_json(source_root / "trajectory_actor.json")
    footprint_width_mm = configured_footprint_width(actor)
    detections, tracks = extract_detections(source_root, actor)
    chosen, alternatives = select_axis_sign(detections, footprint_width_mm)
    candidates = read_json(candidate_root / "event_candidates.json")["candidates"]
    rows = []
    summaries = []
    for candidate in candidates:
        candidate_rows = []
        frame_ids = set(candidate["supportingFrameIndices"])
        for detection in detections:
            if detection["observationIndex"] not in frame_ids:
                continue
            coordinate = projected_coordinate(detection, chosen["sign"], footprint_width_mm)
            if abs(coordinate - candidate["actorRelativeCoordinateMm"]) > CLUSTER_RADIUS_MM:
                continue
            image = load_gray(source_root / detection["image"])
            measurement = measure_interface_termination(
                image, tracks[detection["observationIndex"]]["track"], detection["pixelX"]
            )
            corrected_coordinate = depth_aware_projected_coordinate(
                detection, chosen["sign"], actor
            )
            row = {
                "candidateId": candidate["candidateId"],
                "observationIndex": detection["observationIndex"],
                "phase": detection["phase"],
                "image": detection["image"],
                "pixelX": detection["pixelX"],
                "depthPixelY": detection["depthPixelY"],
                "projectedCoordinateMm": coordinate,
                "depthAwareProjectedCoordinateMm": corrected_coordinate,
                **measurement,
            }
            rows.append(row)
            candidate_rows.append(row)
        summaries.append(summarize_candidate(candidate, candidate_rows))
    return {
        "footprintWidthMm": footprint_width_mm,
        "chosenImageAxisSign": chosen["sign"],
        "axisAlternatives": alternatives,
        "measurements": rows,
        "candidates": summaries,
    }


def summarize_candidate(candidate: dict[str, Any], rows: list[dict[str, Any]]) -> dict[str, Any]:
    fields = (
        "bridgeBrightnessRatio",
        "bridgeRidgeRatio",
        "lowBridgeFraction",
        "longestLowBridgeRunPixels",
        "shoulderAsymmetry",
        "distalToProximalIntensityRatio",
        "shadowOnsetContrast",
        "distalGradientEnergy",
        "distalDarkFraction",
    )
    summary = {
        "candidateId": candidate["candidateId"],
        "actorRelativeCoordinateMm": candidate["actorRelativeCoordinateMm"],
        "uncertaintyIntervalMm": candidate["uncertaintyIntervalMm"],
        "measurementCount": len(rows),
        "supportingFrameIndices": candidate["supportingFrameIndices"],
        "supportingImages": candidate["supportingImages"],
    }
    corrected = np.asarray([
        float(row["depthAwareProjectedCoordinateMm"]) for row in rows
    ], dtype=np.float64)
    summary["depthAwareCenterMm"] = float(np.median(corrected)) if len(corrected) else None
    summary["depthAwareSupportIntervalMm"] = [
        float(np.min(corrected)), float(np.max(corrected))
    ] if len(corrected) else None
    for field in fields:
        values = np.asarray([float(row[field]) for row in rows], dtype=np.float64)
        summary[field + "Median"] = float(np.median(values)) if len(values) else None
        summary[field + "Iqr"] = float(np.subtract(*np.percentile(values, [75, 25]))) \
            if len(values) else None
    summary["bothShouldersVisibleFraction"] = float(np.mean([
        row["bothShouldersVisible"] for row in rows
    ])) if rows else None
    depths = np.asarray([float(row["depthPixelY"]) for row in rows], dtype=np.float64)
    summary["medianInterfaceDepthPixelY"] = float(np.median(depths)) if len(depths) else None
    summary["minimumDistalObservationPixels"] = float(
        255.0 - np.quantile(depths, 0.75)
    ) if len(depths) else None
    summary["shadowTopologyVerifiable"] = bool(
        summary["minimumDistalObservationPixels"] is not None
        and summary["minimumDistalObservationPixels"]
        >= MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS
    )
    return summary


def depth_aware_projected_coordinate(
    detection: dict[str, Any], sign: int, actor: dict[str, Any]
) -> float:
    """Project a tracked image point through the fan width at its measured depth."""
    image_span = float(detection["imageWidth"] - 1)
    center = 0.5 * image_span
    bottom_width = configured_footprint_width(actor)
    top_width = float(actor.get("scanTopWidthMm", 45.0))
    scan_depth = float(actor.get("scanDepthMm", 50.0))
    texture_depth_pixels = image_span * min(1.0, scan_depth / bottom_width)
    depth_fraction = float(np.clip(
        float(detection["depthPixelY"]) / max(texture_depth_pixels, 1e-6), 0.0, 1.0
    ))
    local_width = top_width + (bottom_width - top_width) * depth_fraction
    image_offset = (float(detection["pixelX"]) - center) / max(image_span, 1.0)
    return float(detection["probeCoordinateMm"] + sign * image_offset * local_width)


def pixel_to_beam_index(
    pixel_x: float, depth_pixel_y: float, image_width: int,
    beam_count: int, scan_top_width_mm: float, scan_bottom_width_mm: float,
    scan_depth_mm: float,
) -> int:
    """Map an image point to the renderer beam using its depth-varying fan width."""
    texture_span = float(image_width - 1)
    texture_depth_pixels = texture_span * min(
        1.0, scan_depth_mm / scan_bottom_width_mm
    )
    depth_fraction = float(np.clip(depth_pixel_y / max(texture_depth_pixels, 1e-6), 0.0, 1.0))
    local_width_mm = scan_top_width_mm + (
        scan_bottom_width_mm - scan_top_width_mm
    ) * depth_fraction
    active_width_pixels = texture_span * local_width_mm / scan_bottom_width_mm
    normalized = (pixel_x - 0.5 * texture_span) / max(0.5 * active_width_pixels, 1e-6)
    beam_fraction = float(np.clip(0.5 * (normalized + 1.0), 0.0, 1.0))
    return int(round(beam_fraction * (beam_count - 1)))


def candidate_provenance_attribution(
    source_root: Path, topology: dict[str, Any]
) -> list[dict[str, Any]]:
    """Evaluator-only physical attribution; never used by blind acceptance."""
    actor = read_json(source_root / "trajectory_actor.json")
    results = []
    for candidate in topology["candidates"]:
        rows = [row for row in topology["measurements"]
                if row["candidateId"] == candidate["candidateId"]]
        visible_objects: dict[str, int] = {}
        first_shadows: dict[str, int] = {}
        lf_before_shadow = 0
        skeleton_before_shadow = 0
        records = []
        for row in rows:
            provenance = read_json(
                source_root / "evaluator_only" / "provenance"
                / f"frame_{row['observationIndex']:04d}.json"
            )
            beam_index = pixel_to_beam_index(
                row["pixelX"], row["depthPixelY"],
                int(actor["observations"][row["observationIndex"]]["imageWidth"]),
                int(provenance["beamCount"]), float(actor["scanTopWidthMm"]),
                float(actor["renderedFootprintWidthMm"]), float(actor["scanDepthMm"]),
            )
            beam = provenance["beams"][beam_index]
            before_shadow = [item for item in beam["interfaces"]
                             if item["reachedBeforeOrAtFirstShadow"]]
            names = [item["objectName"] for item in before_shadow]
            for name in names:
                visible_objects[name] = visible_objects.get(name, 0) + 1
            lf_names = sorted({name for name in names if name.startswith("LF T")})
            lf_before_shadow += int(bool(lf_names))
            skeleton_before_shadow += int("Skeleton" in names)
            shadow_name = beam.get("firstShadowObject") or "NONE"
            first_shadows[shadow_name] = first_shadows.get(shadow_name, 0) + 1
            records.append({
                "observationIndex": row["observationIndex"],
                "pixelX": row["pixelX"],
                "depthPixelY": row["depthPixelY"],
                "beamIndex": beam_index,
                "objectsBeforeOrAtFirstShadow": names,
                "lfObjectsBeforeOrAtFirstShadow": lf_names,
                "firstShadowObject": shadow_name,
                "firstShadowDepthFraction": beam.get("firstShadowDepthFraction"),
            })
        count = len(records)
        results.append({
            "candidateId": candidate["candidateId"],
            "actorRelativeCoordinateMm": candidate["actorRelativeCoordinateMm"],
            "supportCount": count,
            "lfBeforeShadowFraction": lf_before_shadow / count if count else None,
            "skeletonBeforeShadowFraction": skeleton_before_shadow / count if count else None,
            "visibleObjectCounts": visible_objects,
            "firstShadowCounts": first_shadows,
            "records": records,
            "evaluatorOnly": True,
        })
    return results


def apply_shadow_topology_gate(topology: dict[str, Any]) -> dict[str, Any]:
    """Apply one uniform image-only shadow-verifiability rule to every candidate."""
    accepted = []
    ambiguous = []
    for candidate in topology["candidates"]:
        item = {
            "candidateId": candidate["candidateId"],
            "actorRelativeCoordinateMm": candidate["actorRelativeCoordinateMm"],
            "uncertaintyIntervalMm": candidate["uncertaintyIntervalMm"],
            "supportingFrameIndices": candidate["supportingFrameIndices"],
            "supportingImages": candidate["supportingImages"],
            "eventType": "BRIGHT_INTERFACE_VALLEY_WITH_VERIFIABLE_DISTAL_SHADOW_TOPOLOGY",
            "minimumDistalObservationPixels": candidate["minimumDistalObservationPixels"],
            "medianInterfaceDepthPixelY": candidate["medianInterfaceDepthPixelY"],
            "shadowOnsetContrast": candidate["shadowOnsetContrastMedian"],
            "accepted": candidate["shadowTopologyVerifiable"],
        }
        if item["accepted"]:
            item["decision"] = "ACCEPTED_SHADOW_TOPOLOGY_VERIFIABLE"
            accepted.append(item)
        else:
            item["decision"] = "AMBIGUOUS_INSUFFICIENT_DISTAL_FIELD"
            item["reason"] = (
                "The tracked interface is too close to the distal image boundary to verify "
                "a persistent post-interface acoustic-shadow region."
            )
            ambiguous.append(item)
    relative_map = build_relative_map(accepted)
    return {
        "contract": "thoracic_g2_r3_shadow_topology_gate_v1",
        "inputIsolation": (
            "Clean ultrasound pixels, G2-R2 image candidates, acquisition geometry, and "
            "actor-relative executed motion only."
        ),
        "featureFamily": "ACOUSTIC_SHADOW_ONSET_TERMINATION_CONTINUITY",
        "minimumDistalShadowObservationPixels": MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
        "accepted": accepted,
        "ambiguous": ambiguous,
        "rejected": [],
        "relativeSpineMap": relative_map,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--candidate-root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--evaluator-output", type=Path)
    args = parser.parse_args()
    result = candidate_topology_measurements(
        args.source_root.resolve(), args.candidate_root.resolve()
    )
    text = json.dumps(result, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
    if args.evaluator_output:
        evaluator = candidate_provenance_attribution(args.source_root.resolve(), result)
        args.evaluator_output.parent.mkdir(parents=True, exist_ok=True)
        args.evaluator_output.write_text(
            json.dumps({"candidates": evaluator}, indent=2) + "\n", encoding="utf-8"
        )
    print(json.dumps({"candidates": result["candidates"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
