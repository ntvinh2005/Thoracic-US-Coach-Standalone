"""Frozen actor-only R6.1 repaired cross-plane protocol and analysis."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

import numpy as np

from g2_r3_topology_discriminator import measure_interface_termination
from g2_r4_multi_hypothesis_tracker import (
    detect_interface_valleys,
    track_multiple_interfaces,
)
from g2_r5_targeted_multiview import _validate_capture_trace
from g2_r6_cross_plane import (
    MAXIMUM_CONTROL_DISTANCE,
    MAXIMUM_FRESH_FRAMES,
    MINIMUM_DISTANCE_MARGIN,
    MINIMUM_SECONDARY_DISTANCE,
    MINIMUM_VIEW_OBSERVATIONS,
    MINIMUM_VIEW_POSITIONS,
    POSITION_BUCKET_MM,
    TARGET_RADIUS_MM,
    classify,
    estimate_frame_budget,
    read_json,
    select_regions,
    write_json,
    write_measurements_csv,
)
from relative_spine_e2_analysis import load_gray


BASELINE_YAW_DEG = 90.0
ALTERNATIVE_YAW_DEG = 65.0
MATCHED_DEPTH_MM = 60
HISTORICAL_GLOBAL_MAE = 0.02
MINIMUM_CENTRAL_MAE = 0.01
MINIMUM_LOCAL_GRADIENT_MAE = 0.005


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def build_protocol(
    r5_protocol: dict[str, Any], r5_decisions: dict[str, Any], output_root: str
) -> dict[str, Any]:
    regions = select_regions(r5_protocol, r5_decisions)
    for region in regions:
        region["variants"] = [
            {"variantId": "LONGITUDINAL_YAW90_DEPTH60",
             "surfaceYawDeg": BASELINE_YAW_DEG, "scanDepthMm": MATCHED_DEPTH_MM},
            {"variantId": "OBLIQUE_YAW65_DEPTH60",
             "surfaceYawDeg": ALTERNATIVE_YAW_DEG, "scanDepthMm": MATCHED_DEPTH_MM},
        ]
    budget = estimate_frame_budget(regions)
    if budget > MAXIMUM_FRESH_FRAMES:
        raise ValueError("R6.1 repaired protocol exceeds 160 fresh frames")
    return {
        "contract": "thoracic_g2_r6_1_repaired_cross_plane_protocol_v1",
        "inputIsolation": (
            "Only prior actor hypotheses, clean pixels, executed Home-relative motion, "
            "uncertainty, and robotic acquisition geometry are used."
        ),
        "mode": "ACTOR_GUIDED_REPAIRED_CROSS_PLANE_DISCRIMINATION",
        "homeRelativeOriginMm": 0.0,
        "directionConvention": "cranial_negative_caudad_positive",
        "safeActorCoverageMm": list(r5_protocol["safeActorCoverageMm"]),
        "movementStepMm": 1.5,
        "maximumFreshFrames": MAXIMUM_FRESH_FRAMES,
        "estimatedFreshFrames": budget,
        "minimumInitialCrossPlanePixelMae": HISTORICAL_GLOBAL_MAE,
        "outputRoot": output_root,
        "acquisition": {
            "lateralOffsetMm": -6.0,
            "baselineYawDeg": BASELINE_YAW_DEG,
            "alternativeYawDeg": ALTERNATIVE_YAW_DEG,
            "matchedDepthMm": MATCHED_DEPTH_MM,
            "alternativeJustification": (
                "Yaw 65 is inside the previously contact-valid yaw 60 to 90 interval, "
                "is not the failed yaw-60 acquisition, and remains 25 degrees from the "
                "longitudinal baseline to provide a materially different scan plane."
            ),
        },
        "viewQuality": {
            "globalPixelMaeDiagnosticThreshold": HISTORICAL_GLOBAL_MAE,
            "minimumCentralPixelMae": MINIMUM_CENTRAL_MAE,
            "minimumLocalGradientMae": MINIMUM_LOCAL_GRADIENT_MAE,
            "decisionRule": (
                "persist first; interpretable when global MAE OR central ROI MAE OR local "
                "gradient MAE passes, followed by offline interface measurability"
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
            "warning": "Image topology only; anatomical identity remains evaluator-only.",
        },
        "regions": regions,
    }


def projected_coordinate_from_observation(
    observation: dict[str, Any], image_width: int, pixel_x: float
) -> float:
    center = 0.5 * (float(image_width) - 1.0)
    image_offset = (float(pixel_x) - center) / max(center * 2.0, 1.0)
    signed_axis = observation.get("signedScanWidthAgainstTravelAxis")
    if signed_axis is None:
        signed_axis = float(observation["scanWidthAgainstTravelAxis"])
    physical_offset = (
        image_offset * float(observation["renderedFootprintWidthMm"]) * float(signed_axis)
    )
    return float(observation["signedActorRelativeMm"] - physical_offset)


def _neighbor_count(
    tracks: list[dict[str, Any]], chosen: dict[str, Any], pixel_x: float
) -> int:
    x = np.asarray(chosen["x"], dtype=np.float64)
    index = int(np.argmin(np.abs(x - pixel_x)))
    chosen_y = float(chosen["pathY"][index])
    count = 0
    for track in tracks:
        tx = np.asarray(track["x"], dtype=np.float64)
        other = int(np.argmin(np.abs(tx - pixel_x)))
        if track is chosen or abs(float(track["pathY"][other]) - chosen_y) >= 8.0:
            count += 1
    return count


def extract_measurements(
    session_root: Path, actor: dict[str, Any], protocol: dict[str, Any]
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    observations = _validate_capture_trace(session_root, actor)
    centers = {item["regionId"]: float(item["actorRelativeCenterMm"])
               for item in protocol["regions"]}
    all_rows: list[dict[str, Any]] = []
    associated: list[dict[str, Any]] = []
    for observation in observations:
        image = load_gray(session_root / observation["image"])
        tracks = track_multiple_interfaces(image)
        frame_rows = []
        for track in tracks:
            for valley in detect_interface_valleys(track):
                projected = projected_coordinate_from_observation(
                    observation, image.shape[1], float(valley["pixelX"])
                )
                distance = abs(projected - centers[observation["regionId"]])
                row = {
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
                    "targetDistanceMm": distance,
                    "withinTargetRadius": distance <= TARGET_RADIUS_MM,
                    "neighboringInterfaceCount": _neighbor_count(
                        tracks, track, float(valley["pixelX"])
                    ),
                    **measure_interface_termination(image, track, float(valley["pixelX"])),
                }
                all_rows.append(row)
                frame_rows.append(row)
        retained = [row for row in frame_rows if row["withinTargetRadius"]]
        if retained:
            associated.append(max(retained, key=lambda row: row["prominencePixels"]))
    return all_rows, associated


def _view_summary(rows: list[dict[str, Any]]) -> dict[str, Any]:
    best: dict[tuple[str, int], dict[str, Any]] = {}
    for row in rows:
        key = (row["direction"], round(float(row["probeCoordinateMm"]) / POSITION_BUCKET_MM))
        if key not in best or row["prominencePixels"] > best[key]["prominencePixels"]:
            best[key] = row
    independent = list(best.values())
    positions = {key[1] for key in best}
    directions = {row["direction"] for row in independent}

    def median(name: str) -> float | None:
        values = [float(row[name]) for row in independent]
        return float(np.median(values)) if values else None

    bridge = median("bridgeBrightnessRatio")
    low = median("lowBridgeFraction")
    termination = None if bridge is None or low is None else float(
        0.5 * np.clip(1.0 - bridge, 0.0, 1.0) + 0.5 * np.clip(low, 0.0, 1.0)
    )
    return {
        "independentObservationCount": len(independent),
        "physicalPositionCount": len(positions),
        "directions": sorted(directions),
        "adequate": (
            len(independent) >= MINIMUM_VIEW_OBSERVATIONS
            and len(positions) >= MINIMUM_VIEW_POSITIONS
            and {"FORWARD", "REVERSE"}.issubset(directions)
        ),
        "bridgeBrightnessRatioMedian": bridge,
        "lowBridgeFractionMedian": low,
        "shoulderAsymmetryMedian": median("shoulderAsymmetry"),
        "neighboringInterfaceCountMedian": median("neighboringInterfaceCount"),
        "terminationStrength": termination,
        "supportingObservationIndices": sorted(
            int(row["observationIndex"]) for row in independent
        ),
    }


def summarize(associated: list[dict[str, Any]], protocol: dict[str, Any]) -> list[dict[str, Any]]:
    result = []
    for region in protocol["regions"]:
        rows = [row for row in associated if row["regionId"] == region["regionId"]]
        baseline = _view_summary([row for row in rows if row["variantId"].startswith("LONGITUDINAL")])
        alternative = _view_summary([row for row in rows if row["variantId"].startswith("OBLIQUE")])
        signature = None
        if baseline["adequate"] and alternative["adequate"]:
            signature = [
                alternative["terminationStrength"] - baseline["terminationStrength"],
                alternative["shoulderAsymmetryMedian"] - baseline["shoulderAsymmetryMedian"],
                0.5 * (alternative["neighboringInterfaceCountMedian"]
                       - baseline["neighboringInterfaceCountMedian"]),
            ]
        result.append({
            "regionId": region["regionId"],
            "role": region["role"],
            "actorRelativeCenterMm": region["actorRelativeCenterMm"],
            "baseline": baseline,
            "oblique": alternative,
            "crossPlaneTopologySignature": signature,
        })
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    select_parser = subparsers.add_parser("select")
    select_parser.add_argument("--r5-protocol", required=True)
    select_parser.add_argument("--r5-decisions", required=True)
    select_parser.add_argument("--session-output", required=True)
    select_parser.add_argument("--output", required=True)
    analyze_parser = subparsers.add_parser("analyze")
    analyze_parser.add_argument("--session-root", required=True)
    analyze_parser.add_argument("--protocol", required=True)
    analyze_parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    if args.command == "select":
        protocol = build_protocol(
            read_json(Path(args.r5_protocol)), read_json(Path(args.r5_decisions)),
            args.session_output,
        )
        write_json(Path(args.output), protocol)
        print(json.dumps({"regions": len(protocol["regions"]),
                          "estimatedFreshFrames": protocol["estimatedFreshFrames"]}, indent=2))
        return
    session = Path(args.session_root)
    protocol = read_json(Path(args.protocol))
    all_rows, associated = extract_measurements(
        session, read_json(session / "trajectory_actor.json"), protocol
    )
    summaries = summarize(associated, protocol)
    decisions = classify(summaries)
    output = Path(args.output_root)
    write_measurements_csv(output / "cross_view_feature_measurements.csv", all_rows)
    write_json(output / "cross_view_tracking_diagnostics.json", {
        "contract": "thoracic_g2_r6_1_cross_view_diagnostics_v1",
        "evaluatorInputsUsed": False,
        "allImageMeasurements": len(all_rows),
        "targetAssociatedMeasurements": len(associated),
        "regions": summaries,
    })
    write_json(output / "actor_classification_decisions.json", decisions)
    print(json.dumps({"allMeasurements": len(all_rows),
                      "associatedMeasurements": len(associated),
                      "scientificVerdict": decisions["scientificVerdict"]}, indent=2))


if __name__ == "__main__":
    main()
