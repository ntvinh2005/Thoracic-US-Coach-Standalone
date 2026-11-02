"""Actor-only G2-R5 revisit selection and targeted multi-view evidence fusion."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
from typing import Any

import numpy as np

from g2_r2_interface_tracker import detect_interface_valleys, projected_coordinate
from g2_r4_multi_hypothesis_tracker import (
    MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION,
    MINIMUM_DISTINCT_FRAMES,
    build_ambiguity_aware_map,
    measure_actual_shadow,
    track_multiple_interfaces,
)
from relative_spine_e2_analysis import load_gray, sha256


MAXIMUM_REVISIT_REGIONS = 3
MOVEMENT_STEP_MM = 1.5
MINIMUM_WINDOW_HALF_WIDTH_MM = 8.0
MAXIMUM_WINDOW_HALF_WIDTH_MM = 12.0
WINDOW_CONTEXT_MM = 6.0
MAXIMUM_FRESH_FRAMES = 240
LOCKED_IMAGE_AXIS_SIGN = -1
TARGETED_CLUSTER_RADIUS_MM = 3.0
MINIMUM_NEW_PHYSICAL_POSITIONS = 2
POSITION_BUCKET_MM = 0.5


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def _is_weak_promising(cluster: dict[str, Any]) -> bool:
    return (
        cluster.get("classification") == "AMBIGUOUS_INSUFFICIENT_EVIDENCE"
        and float(cluster.get("actualShadowSupportFraction", 0.0))
        >= MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION
        and int(cluster.get("distinctFrameCount", 0)) < MINIMUM_DISTINCT_FRAMES
    )


def _is_stable_unresolved(cluster: dict[str, Any]) -> bool:
    return (
        cluster.get("classification") == "AMBIGUOUS_INSUFFICIENT_EVIDENCE"
        and int(cluster.get("distinctFrameCount", 0)) >= MINIMUM_DISTINCT_FRAMES
        and cluster.get("outboundCenterMm") is not None
        and cluster.get("repeatCenterMm") is not None
        and float(cluster.get("actualShadowSupportFraction", 0.0))
        < MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION
    )


def _weak_score(cluster: dict[str, Any]) -> float:
    frames = int(cluster.get("distinctFrameCount", 0))
    missing_direction = int(
        cluster.get("outboundCenterMm") is None
        or cluster.get("repeatCenterMm") is None
    )
    missing_support = max(0, MINIMUM_DISTINCT_FRAMES - frames)
    return 2.0 * min(frames, MINIMUM_DISTINCT_FRAMES) + 2.0 * missing_direction \
        + 0.5 / (1.0 + missing_support)


def _stable_ambiguity_score(cluster: dict[str, Any]) -> float:
    frames = int(cluster.get("distinctFrameCount", 0))
    interval = cluster.get("supportIntervalMm") or [cluster["centerMm"], cluster["centerMm"]]
    width = max(0.0, float(interval[1]) - float(interval[0]))
    compactness = 1.0 / (1.0 + width)
    return 2.0 * min(frames, 30) / 30.0 + compactness


def _window_for_cluster(
    cluster: dict[str, Any], coverage: tuple[float, float]
) -> tuple[list[float], bool, float]:
    center = float(cluster["centerMm"])
    interval = cluster.get("supportIntervalMm") or [center, center]
    support_half_width = 0.5 * abs(float(interval[1]) - float(interval[0]))
    half_width = float(np.clip(
        support_half_width + WINDOW_CONTEXT_MM,
        MINIMUM_WINDOW_HALF_WIDTH_MM,
        MAXIMUM_WINDOW_HALF_WIDTH_MM,
    ))
    desired = [center - half_width, center + half_width]
    clamped = [max(float(coverage[0]), desired[0]), min(float(coverage[1]), desired[1])]
    return clamped, clamped != desired, half_width


def _variants(category: str) -> list[dict[str, Any]]:
    variants = [
        {"variantId": "YAW90_DEPTH50", "surfaceYawDeg": 90.0, "scanDepthMm": 50},
        {"variantId": "YAW91_DEPTH50", "surfaceYawDeg": 91.0, "scanDepthMm": 50},
    ]
    if category == "STABLE_UNRESOLVED_INTERFACE":
        variants.append({
            "variantId": "YAW90_DEPTH60",
            "surfaceYawDeg": 90.0,
            "scanDepthMm": 60,
        })
    return variants


def estimate_frame_budget(regions: list[dict[str, Any]]) -> int:
    total = 0
    for region in regions:
        width = float(region["windowMm"][1]) - float(region["windowMm"][0])
        movements = int(math.ceil(width / MOVEMENT_STEP_MM))
        total += (1 + 2 * movements) * len(region["variants"])
    return total


def select_revisit_regions(
    clusters: list[dict[str, Any]], coverage: tuple[float, float]
) -> list[dict[str, Any]]:
    """Choose two weak hypotheses plus one consequential stable ambiguity."""
    weak = sorted(
        (item for item in clusters if _is_weak_promising(item)),
        key=lambda item: (_weak_score(item), int(item["distinctFrameCount"])),
        reverse=True,
    )[:2]
    stable = sorted(
        (item for item in clusters if _is_stable_unresolved(item)),
        key=_stable_ambiguity_score,
        reverse=True,
    )[:1]
    selected: list[dict[str, Any]] = []
    for cluster in weak + stable:
        category = (
            "STABLE_UNRESOLVED_INTERFACE" if _is_stable_unresolved(cluster)
            else "WEAK_EVENT_HYPOTHESIS"
        )
        window, clamped, requested_half_width = _window_for_cluster(cluster, coverage)
        selected.append({
            "regionId": f"R5R{len(selected) + 1:02d}",
            "category": category,
            "actorRelativeCenterMm": float(cluster["centerMm"]),
            "supportIntervalMm": [float(value) for value in cluster["supportIntervalMm"]],
            "windowMm": window,
            "windowClampedByWorkspace": clamped,
            "requestedHalfWidthMm": requested_half_width,
            "supportingFrameIndices": list(cluster.get("supportingFrameIndices", [])),
            "supportingImages": list(cluster.get("supportingImages", [])),
            "distinctFrameCount": int(cluster.get("distinctFrameCount", 0)),
            "outboundSupport": cluster.get("outboundCenterMm") is not None,
            "repeatSupport": cluster.get("repeatCenterMm") is not None,
            "actualShadowSupportFraction": float(
                cluster.get("actualShadowSupportFraction", 0.0)
            ),
            "ambiguityReasons": list(cluster.get("rejectionReasons", [])),
            "expectedInformationGain": (
                "Test whether added distal field resolves a stable enumeration-changing "
                "interface." if category == "STABLE_UNRESOLVED_INTERFACE" else
                "Acquire distinct positions and both traversal directions under matched yaw."
            ),
            "variants": _variants(category),
            "coordinateSource": "ACTOR_RELATIVE_IMAGE_HYPOTHESIS",
        })
    if len(selected) != MAXIMUM_REVISIT_REGIONS:
        raise ValueError(
            f"Expected two weak and one stable unresolved region; selected {len(selected)}"
        )
    frame_budget = estimate_frame_budget(selected)
    if frame_budget > MAXIMUM_FRESH_FRAMES:
        raise ValueError(f"Targeted protocol exceeds frame budget: {frame_budget}")
    return selected


def build_protocol(
    clusters: list[dict[str, Any]], coverage: tuple[float, float], output_root: str
) -> dict[str, Any]:
    regions = select_revisit_regions(clusters, coverage)
    return {
        "contract": "thoracic_g2_r5_actor_selected_multiview_protocol_v1",
        "inputIsolation": (
            "Only G2-R4 actor image candidates, executed relative coverage, and robotic "
            "acquisition geometry are used; no evaluator anatomy coordinates."
        ),
        "mode": "ACTOR_GUIDED_DIAGNOSTIC_REACQUISITION",
        "homeRelativeOriginMm": 0.0,
        "directionConvention": "cranial_negative_caudad_positive",
        "safeActorCoverageMm": [float(coverage[0]), float(coverage[1])],
        "movementStepMm": MOVEMENT_STEP_MM,
        "maximumFreshFrames": MAXIMUM_FRESH_FRAMES,
        "estimatedFreshFrames": estimate_frame_budget(regions),
        "outputRoot": output_root,
        "regions": regions,
    }


def _validate_capture_trace(root: Path, actor: dict[str, Any]) -> list[dict[str, Any]]:
    observations = actor.get("observations", [])
    movements = actor.get("movements", [])
    if not observations or not movements:
        raise ValueError("Targeted trace requires observations and movements")
    coordinate = 0.0
    movement_coordinates: dict[int, float] = {-1: coordinate}
    for index, movement in enumerate(movements):
        if movement["movementIndex"] != index:
            raise ValueError("Movement indices are not contiguous")
        if movement["accepted"]:
            sign = 1.0 if movement["requestedAction"].upper() == "CAUDAL" else -1.0
            coordinate += sign * float(movement["achievedDisplacementMm"])
        if abs(coordinate - float(movement["actorRelativeAfterMm"])) > 1e-3:
            raise ValueError("Movement integration mismatch")
        movement_coordinates[index] = coordinate
    hashes: set[str] = set()
    serials: set[int] = set()
    for index, observation in enumerate(observations):
        if observation["observationIndex"] != index:
            raise ValueError("Observation indices are not contiguous")
        movement_index = int(observation["movementIndexAtCapture"])
        expected = movement_coordinates[movement_index]
        if abs(expected - float(observation["signedActorRelativeMm"])) > 1e-3:
            raise ValueError("Capture coordinate does not match executed motion")
        if not all(observation[key] for key in (
            "frameIntegrityValid", "contactValid", "workspaceValid"
        )):
            raise ValueError("Invalid capture integrity or safety")
        if not (
            observation["poseEpoch"] == observation["producerPoseEpoch"]
            and observation["producerUnityFrame"] > observation["poseAppliedUnityFrame"]
            and observation["captureUnityFrame"] == observation["producerUnityFrame"]
            and observation["producerScanSerial"] > 0
            and observation["activeAggregatorCount"] == 0
        ):
            raise ValueError("Stale targeted capture")
        image_path = root / observation["image"]
        if not image_path.is_file() or sha256(image_path) != observation["imageSha256"]:
            raise ValueError("Targeted image identity mismatch")
        duplicate_image = observation["imageSha256"] in hashes
        if bool(observation.get("duplicateImageHash", False)) != duplicate_image:
            raise ValueError("Duplicate-image provenance flag mismatch")
        if int(observation["producerScanSerial"]) in serials:
            raise ValueError("Duplicate scan serial")
        hashes.add(observation["imageSha256"])
        serials.add(int(observation["producerScanSerial"]))
    return observations


def extract_targeted_detections(root: Path, actor: dict[str, Any]) -> list[dict[str, Any]]:
    observations = _validate_capture_trace(root, actor)
    detections: list[dict[str, Any]] = []
    for observation in observations:
        if observation.get("duplicateImageHash", False):
            continue
        image = load_gray(root / observation["image"])
        footprint = float(observation["renderedFootprintWidthMm"])
        for track in track_multiple_interfaces(image):
            for valley in detect_interface_valleys(track):
                morphology = measure_actual_shadow(image, track, valley["pixelX"])
                projected = projected_coordinate({
                    "imageWidth": image.shape[1],
                    "pixelX": valley["pixelX"],
                    "probeCoordinateMm": observation["signedActorRelativeMm"],
                }, LOCKED_IMAGE_AXIS_SIGN, footprint)
                detections.append({
                    "observationIndex": observation["observationIndex"],
                    "regionId": observation["regionId"],
                    "variantId": observation["variantId"],
                    "direction": observation["direction"],
                    "probeCoordinateMm": observation["signedActorRelativeMm"],
                    "scanDepthMm": observation["scanDepthMm"],
                    "surfaceYawDeg": observation["surfaceYawDeg"],
                    "image": observation["image"],
                    "imageSha256": observation["imageSha256"],
                    "trackId": track["trackId"],
                    "pixelX": valley["pixelX"],
                    "depthPixelY": valley["depthPixelY"],
                    "prominencePixels": valley["prominencePixels"],
                    "projectedCoordinateMm": projected,
                    **morphology,
                })
    return detections


def _independent_support(detections: list[dict[str, Any]]) -> list[dict[str, Any]]:
    best: dict[tuple[Any, ...], dict[str, Any]] = {}
    for item in detections:
        key = (
            item["variantId"], item["direction"],
            round(float(item["probeCoordinateMm"]) / POSITION_BUCKET_MM),
        )
        score = float(item["prominencePixels"])
        if key not in best or score > float(best[key]["prominencePixels"]):
            best[key] = item
    return list(best.values())


def fuse_candidate(
    original: dict[str, Any], detections: list[dict[str, Any]]
) -> dict[str, Any]:
    center = float(original["centerMm"])
    nearby = [item for item in detections if abs(
        float(item["projectedCoordinateMm"]) - center
    ) <= TARGETED_CLUSTER_RADIUS_MM]
    independent = _independent_support(nearby)
    positions = {
        round(float(item["probeCoordinateMm"]) / POSITION_BUCKET_MM)
        for item in independent
    }
    directions = {item["direction"] for item in independent}
    variants = {item["variantId"] for item in independent}
    shadow = [item for item in independent if item["actualShadowSupported"]]
    combined_centers = [center] + [float(item["projectedCoordinateMm"]) for item in independent]
    updated_center = float(np.median(combined_centers))
    updated_interval = [float(np.min(combined_centers)), float(np.max(combined_centers))]
    enough_new = len(positions) >= MINIMUM_NEW_PHYSICAL_POSITIONS
    both_directions = {"FORWARD", "REVERSE"}.issubset(directions)
    shadow_fraction = len(shadow) / max(len(independent), 1)
    weak_candidate = _is_weak_promising(original)
    accepted_weak = (
        weak_candidate and enough_new and both_directions
        and shadow_fraction >= MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION
    )
    depth_sixty = [item for item in independent if int(item.get("scanDepthMm", 50)) == 60]
    depth_sixty_positions = {
        round(float(item["probeCoordinateMm"]) / POSITION_BUCKET_MM)
        for item in depth_sixty
    }
    depth_sixty_directions = {item["direction"] for item in depth_sixty}
    depth_sixty_verifiable = [
        item for item in depth_sixty
        if float(item.get("minimumDistalObservationPixels", 0.0)) >= 45.0
    ]
    depth_sixty_shadow = [item for item in depth_sixty_verifiable
                          if item["actualShadowSupported"]]
    stable_candidate = _is_stable_unresolved(original)
    rejects_secondary = (
        stable_candidate
        and len(depth_sixty_verifiable) >= 4
        and len(depth_sixty_positions) >= MINIMUM_NEW_PHYSICAL_POSITIONS
        and {"FORWARD", "REVERSE"}.issubset(depth_sixty_directions)
        and not depth_sixty_shadow
    )
    if accepted_weak:
        classification = "SUPPORTED_RECESS_EVENT"
        decision = "Weak event gained distinct-position, bidirectional, pixel-shadow support."
    elif rejects_secondary:
        classification = "REJECTED_SECONDARY_OR_DUPLICATE_INTERFACE"
        decision = (
            "The stable interface was observed bidirectionally at 60 mm with adequate distal "
            "pixels but repeatedly lacked the locked pixel-shadow morphology."
        )
    else:
        classification = "AMBIGUOUS_UNRESOLVED"
        decision = (
            "New evidence does not yet satisfy distinct-position, direction, and morphology "
            "gates, or added depth reveals morphology that remains semantically ambiguous."
        )
    return {
        "originalCenterMm": center,
        "updatedCenterMm": updated_center,
        "updatedSupportIntervalMm": updated_interval,
        "newRawDetectionCount": len(nearby),
        "newIndependentSupportCount": len(independent),
        "newPhysicalPositionCount": len(positions),
        "newDirections": sorted(directions),
        "newVariants": sorted(variants),
        "newActualShadowSupportFraction": shadow_fraction,
        "depthSixtyIndependentSupportCount": len(depth_sixty),
        "depthSixtyDistalVerifiableCount": len(depth_sixty_verifiable),
        "depthSixtyActualShadowCount": len(depth_sixty_shadow),
        "classification": classification,
        "imageOnlyDecision": decision,
        "supportingObservations": [item["observationIndex"] for item in independent],
    }


def build_fused_relative_map(
    uncertainty: dict[str, Any], fused_regions: list[dict[str, Any]]
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    clusters = [dict(item) for item in uncertainty["accepted"]]
    unresolved = [dict(item) for item in uncertainty["ambiguous"]]
    rejected = []
    for fused in fused_regions:
        original = min(
            unresolved,
            key=lambda item: abs(float(item["centerMm"]) - fused["originalCenterMm"]),
        )
        unresolved.remove(original)
        updated = {
            **original,
            "centerMm": fused["updatedCenterMm"],
            "supportIntervalMm": fused["updatedSupportIntervalMm"],
            "targetedFusion": fused,
        }
        if fused["classification"] == "SUPPORTED_RECESS_EVENT":
            updated["classification"] = "SUPPORTED_ANATOMICAL_EVENT"
            updated["accepted"] = True
            clusters.append(updated)
        elif fused["classification"] == "REJECTED_SECONDARY_OR_DUPLICATE_INTERFACE":
            updated["classification"] = fused["classification"]
            updated["accepted"] = False
            rejected.append(updated)
        else:
            updated["classification"] = "AMBIGUOUS_INSUFFICIENT_EVIDENCE"
            updated["accepted"] = False
            unresolved.append(updated)
    relative_map = build_ambiguity_aware_map(clusters + unresolved)
    relative_map["contract"] = "thoracic_g2_r5_targeted_fusion_relative_map_v1"
    relative_map["diagnosticOnly"] = True
    relative_map["heldOutConfirmationRequired"] = True
    return relative_map, rejected


def freeze_manifest(paths: list[Path], output: Path) -> None:
    rows = [f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.as_posix()}" for path in paths]
    output.write_text("\n".join(rows) + "\n", encoding="utf-8")


def _command_select(args: argparse.Namespace) -> None:
    uncertainty = read_json(Path(args.mapping_uncertainty))
    trajectory = read_json(Path(args.trajectory))
    coordinates = [float(item["signedActorRelativeMm"]) for item in trajectory["observations"]]
    coverage = (min(coordinates), max(coordinates))
    protocol = build_protocol(uncertainty["ambiguous"], coverage, args.session_output)
    write_json(Path(args.output), protocol)
    print(json.dumps({
        "selectedRegions": len(protocol["regions"]),
        "estimatedFreshFrames": protocol["estimatedFreshFrames"],
        "coverage": protocol["safeActorCoverageMm"],
    }, indent=2))


def _command_fuse(args: argparse.Namespace) -> None:
    source = Path(args.session_root)
    actor = read_json(source / "trajectory_actor.json")
    detections = extract_targeted_detections(source, actor)
    uncertainty = read_json(Path(args.mapping_uncertainty))
    selected = read_json(Path(args.protocol))["regions"]
    by_center = {round(float(item["centerMm"]), 3): item for item in uncertainty["ambiguous"]}
    fused = []
    for region in selected:
        original = min(
            by_center.values(),
            key=lambda item: abs(float(item["centerMm"]) - region["actorRelativeCenterMm"]),
        )
        region_detections = [item for item in detections if item["regionId"] == region["regionId"]]
        fused.append({"regionId": region["regionId"], **fuse_candidate(original, region_detections)})
    relative_map, rejected = build_fused_relative_map(uncertainty, fused)
    output = Path(args.output)
    write_json(output, {
        "contract": "thoracic_g2_r5_multiview_fusion_v1",
        "regions": fused,
        "rejectedEvents": rejected,
    })
    map_output = Path(args.map_output)
    write_json(map_output, relative_map)
    freeze_manifest([source / "trajectory_actor.json", Path(args.protocol), output, map_output],
                    output.with_name("blind_results.sha256"))
    print(json.dumps({"regions": fused, "relativeMap": relative_map}, indent=2))


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    select = sub.add_parser("select")
    select.add_argument("--mapping-uncertainty", required=True)
    select.add_argument("--trajectory", required=True)
    select.add_argument("--session-output", required=True)
    select.add_argument("--output", required=True)
    select.set_defaults(func=_command_select)
    fuse = sub.add_parser("fuse")
    fuse.add_argument("--mapping-uncertainty", required=True)
    fuse.add_argument("--protocol", required=True)
    fuse.add_argument("--session-root", required=True)
    fuse.add_argument("--output", required=True)
    fuse.add_argument("--map-output", required=True)
    fuse.set_defaults(func=_command_fuse)
    args = parser.parse_args()
    args.func(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
