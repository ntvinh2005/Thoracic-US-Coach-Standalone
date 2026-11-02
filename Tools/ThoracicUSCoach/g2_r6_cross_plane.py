"""G2-R6 actor-only cross-plane selection and interface-topology analysis."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
from typing import Any

import numpy as np

from g2_r2_interface_tracker import projected_coordinate
from g2_r3_topology_discriminator import measure_interface_termination
from g2_r4_multi_hypothesis_tracker import (
    detect_interface_valleys,
    track_multiple_interfaces,
)
from g2_r5_targeted_multiview import _validate_capture_trace
from relative_spine_e2_analysis import load_gray


MOVEMENT_STEP_MM = 1.5
MAXIMUM_REGIONS = 3
MAXIMUM_FRESH_FRAMES = 160
BASELINE_YAW_DEG = 90.0
OBLIQUE_YAW_DEG = 60.0
MATCHED_DEPTH_MM = 60
MINIMUM_INITIAL_PIXEL_MAE = 0.02
MINIMUM_VIEW_POSITIONS = 3
MINIMUM_VIEW_OBSERVATIONS = 4
POSITION_BUCKET_MM = 0.5
TARGET_RADIUS_MM = 4.0
MAXIMUM_CONTROL_DISTANCE = 0.25
MINIMUM_SECONDARY_DISTANCE = 0.50
MINIMUM_DISTANCE_MARGIN = 0.25


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _expanded_window(
    center: float, interval: list[float], coverage: tuple[float, float]
) -> tuple[list[float], bool]:
    support_half = 0.5 * abs(float(interval[1]) - float(interval[0]))
    half_width = float(np.clip(support_half + 5.0, 6.0, 8.0))
    desired = [center - half_width, center + half_width]
    window = [max(coverage[0], desired[0]), min(coverage[1], desired[1])]
    return window, window != desired


def estimate_frame_budget(regions: list[dict[str, Any]]) -> int:
    total = 0
    for region in regions:
        width = float(region["windowMm"][1]) - float(region["windowMm"][0])
        steps = int(math.ceil(width / MOVEMENT_STEP_MM))
        total += (1 + 2 * steps) * len(region["variants"])
    return total


def select_regions(
    r5_protocol: dict[str, Any], r5_decisions: dict[str, Any]
) -> list[dict[str, Any]]:
    """Select weak, stable unresolved, and supported control from actor evidence only."""
    protocol_by_id = {item["regionId"]: item for item in r5_protocol["regions"]}
    combined = []
    for decision in r5_decisions["regions"]:
        source = protocol_by_id[decision["regionId"]]
        combined.append({**source, **decision})
    weak = [item for item in combined
            if item["category"] == "WEAK_EVENT_HYPOTHESIS"
            and item["classification"] == "AMBIGUOUS_UNRESOLVED"]
    stable = [item for item in combined
              if item["category"] == "STABLE_UNRESOLVED_INTERFACE"
              and item["classification"] == "AMBIGUOUS_UNRESOLVED"]
    supported = [item for item in combined
                 if item["classification"] == "SUPPORTED_RECESS_EVENT"]
    if not weak or not stable or not supported:
        raise ValueError("R6 requires one actor-ambiguous weak event, stable interface, and control")
    weak.sort(key=lambda item: (
        item["newActualShadowSupportFraction"], item["newIndependentSupportCount"]
    ), reverse=True)
    stable.sort(key=lambda item: item["newIndependentSupportCount"], reverse=True)
    supported.sort(key=lambda item: (
        item["newActualShadowSupportFraction"], item["newIndependentSupportCount"]
    ), reverse=True)
    selected = [
        ("UNRESOLVED_WEAK_EVENT", weak[0]),
        ("STABLE_SECONDARY_HYPOTHESIS", stable[0]),
        ("SUPPORTED_EVENT_CONTROL", supported[0]),
    ]
    coverage = tuple(float(value) for value in r5_protocol["safeActorCoverageMm"])
    regions = []
    for index, (role, item) in enumerate(selected, start=1):
        center = float(item["updatedCenterMm"])
        interval = [float(value) for value in item["updatedSupportIntervalMm"]]
        window, clamped = _expanded_window(center, interval, coverage)
        regions.append({
            "regionId": f"R6R{index:02d}",
            "role": role,
            "actorRelativeCenterMm": center,
            "supportIntervalMm": interval,
            "windowMm": window,
            "windowClampedByWorkspace": clamped,
            "supportingFrameIndices": list(item["supportingObservations"]),
            "distinctFrameCount": int(item["newIndependentSupportCount"]),
            "priorImageOnlyClassification": item["classification"],
            "coordinateSource": "ACTOR_RELATIVE_IMAGE_HYPOTHESIS",
            "variants": [
                {"variantId": "LONGITUDINAL_YAW90_DEPTH60",
                 "surfaceYawDeg": BASELINE_YAW_DEG, "scanDepthMm": MATCHED_DEPTH_MM},
                {"variantId": "OBLIQUE_YAW60_DEPTH60",
                 "surfaceYawDeg": OBLIQUE_YAW_DEG, "scanDepthMm": MATCHED_DEPTH_MM},
            ],
        })
    if len(regions) > MAXIMUM_REGIONS or estimate_frame_budget(regions) > MAXIMUM_FRESH_FRAMES:
        raise ValueError("R6 actor-selected protocol exceeds its bounded budget")
    return regions


def build_protocol(
    r5_protocol: dict[str, Any], r5_decisions: dict[str, Any], output_root: str
) -> dict[str, Any]:
    regions = select_regions(r5_protocol, r5_decisions)
    return {
        "contract": "thoracic_g2_r6_cross_plane_protocol_v1",
        "inputIsolation": (
            "Only G2-R5 actor classifications, pixels, executed Home-relative motion, "
            "uncertainty, and robotic acquisition geometry are used."
        ),
        "mode": "ACTOR_GUIDED_CROSS_PLANE_DISCRIMINATION",
        "homeRelativeOriginMm": 0.0,
        "directionConvention": "cranial_negative_caudad_positive",
        "safeActorCoverageMm": list(r5_protocol["safeActorCoverageMm"]),
        "movementStepMm": MOVEMENT_STEP_MM,
        "maximumFreshFrames": MAXIMUM_FRESH_FRAMES,
        "estimatedFreshFrames": estimate_frame_budget(regions),
        "minimumInitialCrossPlanePixelMae": MINIMUM_INITIAL_PIXEL_MAE,
        "outputRoot": output_root,
        "acquisition": {
            "lateralOffsetMm": -6.0,
            "baselineYawDeg": BASELINE_YAW_DEG,
            "obliqueYawDeg": OBLIQUE_YAW_DEG,
            "matchedDepthMm": MATCHED_DEPTH_MM,
            "obliqueJustification": (
                "Historical M6 yaw-60 captures were safe and scan width projected along the "
                "travel axis near sin(60), providing a genuinely different plane."
            ),
        },
        "predeclaredFeature": {
            "name": "CROSS_VIEW_INTERFACE_ENTRY_EXIT_TOPOLOGY_V1",
            "pixelMeasurements": [
                "bridgeBrightnessRatio", "lowBridgeFraction", "shoulderAsymmetry",
                "neighboringInterfaceCount",
            ],
            "maximumControlDistance": MAXIMUM_CONTROL_DISTANCE,
            "minimumSecondaryDistance": MINIMUM_SECONDARY_DISTANCE,
            "minimumDistanceMargin": MINIMUM_DISTANCE_MARGIN,
            "warning": "This measures image topology; anatomical identity is evaluator-only.",
        },
        "regions": regions,
    }


def _neighbor_count(tracks: list[dict[str, Any]], chosen: dict[str, Any], pixel_x: float) -> int:
    x = np.asarray(chosen["x"], dtype=np.float64)
    chosen_index = int(np.argmin(np.abs(x - pixel_x)))
    chosen_y = float(chosen["pathY"][chosen_index])
    count = 0
    for track in tracks:
        tx = np.asarray(track["x"], dtype=np.float64)
        index = int(np.argmin(np.abs(tx - pixel_x)))
        if float(track["ridgeScores"][index]) < 0.18:
            continue
        if track is chosen or abs(float(track["pathY"][index]) - chosen_y) >= 8.0:
            count += 1
    return count


def extract_measurements(
    session_root: Path, actor: dict[str, Any], protocol: dict[str, Any]
) -> list[dict[str, Any]]:
    observations = _validate_capture_trace(session_root, actor)
    centers = {item["regionId"]: float(item["actorRelativeCenterMm"])
               for item in protocol["regions"]}
    rows = []
    for observation in observations:
        image = load_gray(session_root / observation["image"])
        tracks = track_multiple_interfaces(image)
        footprint = float(observation.get(
            "longitudinalFootprintWidthMm", observation["renderedFootprintWidthMm"]
        ))
        candidates = []
        for track in tracks:
            for valley in detect_interface_valleys(track):
                projected = projected_coordinate({
                    "imageWidth": image.shape[1],
                    "pixelX": valley["pixelX"],
                    "probeCoordinateMm": observation["signedActorRelativeMm"],
                }, -1, footprint)
                if abs(projected - centers[observation["regionId"]]) > TARGET_RADIUS_MM:
                    continue
                topology = measure_interface_termination(
                    image, track, float(valley["pixelX"])
                )
                candidates.append({
                    "observationIndex": int(observation["observationIndex"]),
                    "regionId": observation["regionId"],
                    "variantId": observation["variantId"],
                    "direction": observation["direction"],
                    "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
                    "image": observation["image"],
                    "imageSha256": observation["imageSha256"],
                    "trackId": track["trackId"],
                    "pixelX": float(valley["pixelX"]),
                    "depthPixelY": float(valley["depthPixelY"]),
                    "prominencePixels": float(valley["prominencePixels"]),
                    "projectedCoordinateMm": projected,
                    "neighboringInterfaceCount": _neighbor_count(
                        tracks, track, float(valley["pixelX"])
                    ),
                    **topology,
                })
        if candidates:
            rows.append(max(candidates, key=lambda item: item["prominencePixels"]))
    return rows


def _independent(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    best = {}
    for row in rows:
        key = (row["variantId"], row["direction"],
               round(float(row["probeCoordinateMm"]) / POSITION_BUCKET_MM))
        if key not in best or row["prominencePixels"] > best[key]["prominencePixels"]:
            best[key] = row
    return list(best.values())


def _view_summary(rows: list[dict[str, Any]]) -> dict[str, Any]:
    independent = _independent(rows)
    positions = {round(float(row["probeCoordinateMm"]) / POSITION_BUCKET_MM)
                 for row in independent}
    directions = {row["direction"] for row in independent}
    def median(name: str) -> float | None:
        values = [float(row[name]) for row in independent]
        return float(np.median(values)) if values else None
    bridge = median("bridgeBrightnessRatio")
    low = median("lowBridgeFraction")
    termination = None if bridge is None or low is None else float(
        0.5 * np.clip(1.0 - bridge, 0.0, 1.0) + 0.5 * np.clip(low, 0.0, 1.0)
    )
    adequate = (
        len(independent) >= MINIMUM_VIEW_OBSERVATIONS
        and len(positions) >= MINIMUM_VIEW_POSITIONS
        and {"FORWARD", "REVERSE"}.issubset(directions)
    )
    return {
        "independentObservationCount": len(independent),
        "physicalPositionCount": len(positions),
        "directions": sorted(directions),
        "adequate": adequate,
        "bridgeBrightnessRatioMedian": bridge,
        "lowBridgeFractionMedian": low,
        "shoulderAsymmetryMedian": median("shoulderAsymmetry"),
        "neighboringInterfaceCountMedian": median("neighboringInterfaceCount"),
        "terminationStrength": termination,
        "supportingObservationIndices": sorted(
            int(row["observationIndex"]) for row in independent
        ),
    }


def summarize_regions(
    rows: list[dict[str, Any]], protocol: dict[str, Any]
) -> list[dict[str, Any]]:
    summaries = []
    for region in protocol["regions"]:
        region_rows = [row for row in rows if row["regionId"] == region["regionId"]]
        baseline = _view_summary([
            row for row in region_rows if row["variantId"].startswith("LONGITUDINAL")
        ])
        oblique = _view_summary([
            row for row in region_rows if row["variantId"].startswith("OBLIQUE")
        ])
        signature = None
        if baseline["adequate"] and oblique["adequate"]:
            signature = [
                oblique["terminationStrength"] - baseline["terminationStrength"],
                oblique["shoulderAsymmetryMedian"] - baseline["shoulderAsymmetryMedian"],
                0.5 * (oblique["neighboringInterfaceCountMedian"]
                       - baseline["neighboringInterfaceCountMedian"]),
            ]
        summaries.append({
            "regionId": region["regionId"],
            "role": region["role"],
            "actorRelativeCenterMm": region["actorRelativeCenterMm"],
            "baseline": baseline,
            "oblique": oblique,
            "crossPlaneTopologySignature": signature,
        })
    return summaries


def _distance(first: list[float], second: list[float]) -> float:
    return float(np.mean(np.abs(np.asarray(first) - np.asarray(second))))


def classify(summaries: list[dict[str, Any]]) -> dict[str, Any]:
    by_role = {item["role"]: item for item in summaries}
    if any(item["crossPlaneTopologySignature"] is None for item in summaries):
        verdict = "INCONCLUSIVE_ACQUISITION"
        supported = False
        weak_distance = None
        secondary_distance = None
    else:
        control = by_role["SUPPORTED_EVENT_CONTROL"]["crossPlaneTopologySignature"]
        weak_distance = _distance(
            by_role["UNRESOLVED_WEAK_EVENT"]["crossPlaneTopologySignature"], control
        )
        secondary_distance = _distance(
            by_role["STABLE_SECONDARY_HYPOTHESIS"]["crossPlaneTopologySignature"], control
        )
        supported = (
            weak_distance <= MAXIMUM_CONTROL_DISTANCE
            and secondary_distance >= MINIMUM_SECONDARY_DISTANCE
            and secondary_distance - weak_distance >= MINIMUM_DISTANCE_MARGIN
        )
        verdict = "DISCRIMINATING_FEATURE_SUPPORTED" if supported \
            else "DISCRIMINATION_NOT_SUPPORTED"
    decisions = []
    for item in summaries:
        if not supported:
            classification = "AMBIGUOUS_INSUFFICIENT_IMAGE_EVIDENCE" \
                if verdict != "INCONCLUSIVE_ACQUISITION" else "INCONCLUSIVE_ACQUISITION"
        elif item["role"] == "STABLE_SECONDARY_HYPOTHESIS":
            classification = "SUPPORTED_SECONDARY_INTERFACE_MORPHOLOGY"
        else:
            classification = "SUPPORTED_DISTINCT_RECESS_MORPHOLOGY"
        decisions.append({
            "regionId": item["regionId"],
            "role": item["role"],
            "classification": classification,
            "crossPlaneTopologySignature": item["crossPlaneTopologySignature"],
        })
    return {
        "contract": "thoracic_g2_r6_actor_classification_v1",
        "evaluatorInputsUsed": False,
        "scientificVerdict": verdict,
        "weakDistanceToPositiveControl": weak_distance,
        "secondaryDistanceToPositiveControl": secondary_distance,
        "thresholds": {
            "maximumControlDistance": MAXIMUM_CONTROL_DISTANCE,
            "minimumSecondaryDistance": MINIMUM_SECONDARY_DISTANCE,
            "minimumDistanceMargin": MINIMUM_DISTANCE_MARGIN,
        },
        "decisions": decisions,
    }


def write_measurements_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    fields = list(rows[0]) if rows else ["observationIndex"]
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def freeze_manifest(paths: list[Path], output: Path) -> None:
    output.write_text("\n".join(
        f"{sha256(path)}  {path.as_posix()}" for path in paths
    ) + "\n", encoding="utf-8")


def command_select(args: argparse.Namespace) -> None:
    protocol = build_protocol(
        read_json(Path(args.r5_protocol)), read_json(Path(args.r5_decisions)),
        args.session_output,
    )
    write_json(Path(args.output), protocol)
    print(json.dumps({"regions": len(protocol["regions"]),
                      "estimatedFreshFrames": protocol["estimatedFreshFrames"]}, indent=2))


def command_analyze(args: argparse.Namespace) -> None:
    session = Path(args.session_root)
    actor = read_json(session / "trajectory_actor.json")
    protocol = read_json(Path(args.protocol))
    rows = extract_measurements(session, actor, protocol)
    summaries = summarize_regions(rows, protocol)
    decisions = classify(summaries)
    output_root = Path(args.output_root)
    write_measurements_csv(output_root / "image_only_feature_measurements.csv", rows)
    write_json(output_root / "cross_view_tracking_diagnostics.json", {
        "contract": "thoracic_g2_r6_cross_view_diagnostics_v1",
        "featureDefinition": protocol["predeclaredFeature"],
        "regions": summaries,
    })
    write_json(output_root / "actor_classification_decisions.json", decisions)
    existing_map = read_json(Path(args.r5_map))
    updated_map = {
        **existing_map,
        "contract": "thoracic_g2_r6_diagnostic_relative_map_v1",
        "g2R6ScientificVerdict": decisions["scientificVerdict"],
        "predictedT7RelativeMm": None,
        "overallMapStatus": "INCONCLUSIVE",
        "diagnosticOnly": True,
        "abstentionReason": (
            "R6 does not emit T7: diagnostic cross-plane evidence is not a held-out full map, "
            "and pre-existing consequential candidates remain unresolved."
        ),
    }
    write_json(output_root / "relative_spine_map.json", updated_map)
    write_json(output_root / "mapping_uncertainty.json", {
        "contract": "thoracic_g2_r6_mapping_uncertainty_v1",
        "supportIntervalsAreNotCalibratedConfidenceIntervals": True,
        "crossPlaneScientificVerdict": decisions["scientificVerdict"],
        "preExistingConsequentialAmbiguitiesMm": existing_map.get(
            "consequentialAmbiguousCoordinatesMm", []
        ),
        "predictedT7RelativeMm": None,
    })
    freeze_manifest([
        session / "trajectory_actor.json", Path(args.protocol),
        output_root / "cross_view_tracking_diagnostics.json",
        output_root / "actor_classification_decisions.json",
        output_root / "relative_spine_map.json",
    ], output_root / "blind_results.sha256")
    print(json.dumps(decisions, indent=2))


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    select = sub.add_parser("select")
    select.add_argument("--r5-protocol", required=True)
    select.add_argument("--r5-decisions", required=True)
    select.add_argument("--session-output", required=True)
    select.add_argument("--output", required=True)
    select.set_defaults(func=command_select)
    analyze = sub.add_parser("analyze")
    analyze.add_argument("--session-root", required=True)
    analyze.add_argument("--protocol", required=True)
    analyze.add_argument("--r5-map", required=True)
    analyze.add_argument("--output-root", required=True)
    analyze.set_defaults(func=command_analyze)
    args = parser.parse_args()
    args.func(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
