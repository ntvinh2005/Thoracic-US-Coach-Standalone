"""Trace R6 image-to-topology lineage without evaluator anatomy inputs."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
from typing import Any

from g2_r2_interface_tracker import projected_coordinate
from g2_r3_topology_discriminator import measure_interface_termination
from g2_r4_multi_hypothesis_tracker import (
    detect_interface_valleys,
    track_multiple_interfaces,
)
from g2_r5_targeted_multiview import _validate_capture_trace
from relative_spine_e2_analysis import load_gray


DEFAULT_TARGET_RADIUS_MM = 4.0


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def trace_baseline_lineage(
    session_root: Path,
    actor: dict[str, Any],
    protocol: dict[str, Any],
    target_radius_mm: float = DEFAULT_TARGET_RADIUS_MM,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    observations = _validate_capture_trace(session_root, actor)
    centers = {
        item["regionId"]: float(item["actorRelativeCenterMm"])
        for item in protocol["regions"]
    }
    rows: list[dict[str, Any]] = []
    frame_summaries: list[dict[str, Any]] = []
    for observation in observations:
        image = load_gray(session_root / observation["image"])
        tracks = track_multiple_interfaces(image)
        footprint = float(observation.get(
            "longitudinalFootprintWidthMm", observation["renderedFootprintWidthMm"]
        ))
        frame_rows: list[dict[str, Any]] = []
        for track in tracks:
            valleys = detect_interface_valleys(track)
            if not valleys:
                rows.append({
                    "observationIndex": int(observation["observationIndex"]),
                    "image": observation["image"],
                    "direction": observation["direction"],
                    "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
                    "trackId": track["trackId"],
                    "trackValid": bool(track["valid"]),
                    "trackMeanRidgeScore": float(track["meanRidgeScore"]),
                    "valleyFound": False,
                    "pixelX": None,
                    "depthPixelY": None,
                    "prominencePixels": None,
                    "projectedCoordinateMm": None,
                    "targetCenterMm": centers[observation["regionId"]],
                    "targetDistanceMm": None,
                    "withinTargetRadius": False,
                    "topologyMeasured": False,
                    "discardReason": "NO_INTERFACE_VALLEY",
                })
                continue
            for valley in valleys:
                projected = projected_coordinate({
                    "imageWidth": image.shape[1],
                    "pixelX": valley["pixelX"],
                    "probeCoordinateMm": observation["signedActorRelativeMm"],
                }, -1, footprint)
                distance = abs(projected - centers[observation["regionId"]])
                topology = measure_interface_termination(
                    image, track, float(valley["pixelX"])
                )
                within = distance <= target_radius_mm
                row = {
                    "observationIndex": int(observation["observationIndex"]),
                    "image": observation["image"],
                    "direction": observation["direction"],
                    "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
                    "trackId": track["trackId"],
                    "trackValid": bool(track["valid"]),
                    "trackMeanRidgeScore": float(track["meanRidgeScore"]),
                    "valleyFound": True,
                    "pixelX": float(valley["pixelX"]),
                    "depthPixelY": float(valley["depthPixelY"]),
                    "prominencePixels": float(valley["prominencePixels"]),
                    "projectedCoordinateMm": projected,
                    "targetCenterMm": centers[observation["regionId"]],
                    "targetDistanceMm": distance,
                    "withinTargetRadius": within,
                    "topologyMeasured": True,
                    "discardReason": "" if within else "OUTSIDE_TARGET_RADIUS",
                    **topology,
                }
                rows.append(row)
                frame_rows.append(row)
        valley_rows = [item for item in frame_rows if item["valleyFound"]]
        retained_rows = [item for item in valley_rows if item["withinTargetRadius"]]
        frame_summaries.append({
            "observationIndex": int(observation["observationIndex"]),
            "image": observation["image"],
            "direction": observation["direction"],
            "probeCoordinateMm": float(observation["signedActorRelativeMm"]),
            "trackedInterfaceCount": len(tracks),
            "validTrackedInterfaceCount": sum(bool(item["valid"]) for item in tracks),
            "valleyCount": len(valley_rows),
            "retainedValleyCount": len(retained_rows),
            "closestTargetDistanceMm": min(
                (float(item["targetDistanceMm"]) for item in valley_rows), default=None
            ),
            "earliestFailureStage": (
                "TRACKING" if not tracks or not any(item["valid"] for item in tracks)
                else "VALLEY_EXTRACTION" if not valley_rows
                else "TARGET_WINDOW_MATCH" if not retained_rows
                else "NONE"
            ),
        })
    summary = {
        "contract": "thoracic_g2_r6_1_baseline_lineage_v1",
        "evaluatorInputsUsed": False,
        "targetRadiusMm": target_radius_mm,
        "frameCount": len(frame_summaries),
        "framesWithValidTrack": sum(
            item["validTrackedInterfaceCount"] > 0 for item in frame_summaries
        ),
        "framesWithValley": sum(item["valleyCount"] > 0 for item in frame_summaries),
        "framesWithRetainedValley": sum(
            item["retainedValleyCount"] > 0 for item in frame_summaries
        ),
        "topologyMeasurementsBeforeTargetMatch": sum(
            bool(item.get("topologyMeasured")) for item in rows
        ),
        "topologyMeasurementsAfterTargetMatch": sum(
            bool(item.get("topologyMeasured")) and bool(item["withinTargetRadius"])
            for item in rows
        ),
        "frameLineage": frame_summaries,
    }
    return rows, summary


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fields: list[str] = []
    for row in rows:
        for name in row:
            if name not in fields:
                fields.append(name)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--session-root", required=True)
    parser.add_argument("--protocol", required=True)
    parser.add_argument("--csv", required=True)
    parser.add_argument("--json", required=True)
    args = parser.parse_args()
    session = Path(args.session_root)
    rows, summary = trace_baseline_lineage(
        session, read_json(session / "trajectory_actor.json"), read_json(Path(args.protocol))
    )
    write_csv(Path(args.csv), rows)
    output = Path(args.json)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in summary.items()
                      if key != "frameLineage"}, indent=2))


if __name__ == "__main__":
    main()
