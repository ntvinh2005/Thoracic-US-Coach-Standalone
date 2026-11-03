"""Post-freeze reporting for the R6.1 diagnostic session."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt
import numpy as np

from g2_r4_multi_hypothesis_tracker import (
    detect_interface_valleys,
    track_multiple_interfaces,
)
from relative_spine_e2_analysis import load_gray


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def representative(
    observations: list[dict[str, Any]], region: dict[str, Any], prefix: str
) -> dict[str, Any]:
    candidates = [item for item in observations
                  if item["regionId"] == region["regionId"]
                  and item["variantId"].startswith(prefix)]
    return min(candidates, key=lambda item: abs(
        float(item["signedActorRelativeMm"]) - float(region["actorRelativeCenterMm"])
    ))


def tracks_and_valleys(image: np.ndarray) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    tracks = track_multiple_interfaces(image)
    valleys = []
    for track in tracks:
        for valley in detect_interface_valleys(track):
            valleys.append({**valley, "trackId": track["trackId"]})
    return tracks, valleys


def build_figures(session: Path, protocol: dict[str, Any], actor: dict[str, Any], output: Path) -> None:
    rows = len(protocol["regions"])
    figure, axes = plt.subplots(rows, 2, figsize=(12, 4 * rows), squeeze=False)
    patch_figure, patch_axes = plt.subplots(rows, 2, figsize=(10, 3.5 * rows), squeeze=False)
    for row, region in enumerate(protocol["regions"]):
        for column, (prefix, label) in enumerate((
            ("LONGITUDINAL", "yaw 90"), ("OBLIQUE", "yaw 65")
        )):
            observation = representative(actor["observations"], region, prefix)
            image = load_gray(session / observation["image"])
            tracks, valleys = tracks_and_valleys(image)
            axis = axes[row, column]
            axis.imshow(image, cmap="gray", vmin=0.0, vmax=1.0)
            for track in tracks:
                axis.plot(track["x"], track["pathY"], linewidth=1.0,
                          label=track["trackId"])
            for valley in valleys:
                axis.scatter(valley["pixelX"], valley["depthPixelY"], s=28,
                             facecolors="none", edgecolors="yellow")
            axis.set_title(
                f"{region['regionId']} {region['role']} — {label}\n"
                f"s={observation['signedActorRelativeMm']:.3f} mm, frame "
                f"{observation['observationIndex']}"
            )
            axis.set_xlim(0, image.shape[1] - 1)
            axis.set_ylim(image.shape[0] - 1, 0)
            axis.legend(loc="lower right", fontsize=7)
            patch_axis = patch_axes[row, column]
            if valleys:
                chosen = max(valleys, key=lambda item: item["prominencePixels"])
                x = int(round(chosen["pixelX"]))
                y = int(round(chosen["depthPixelY"]))
                x0, x1 = max(0, x - 90), min(image.shape[1], x + 91)
                y0, y1 = max(0, y - 80), min(image.shape[0], y + 121)
                patch_axis.imshow(image[y0:y1, x0:x1], cmap="gray", vmin=0.0, vmax=1.0)
                patch_axis.axvline(x - x0, color="cyan", linewidth=0.8)
                patch_axis.axhline(y - y0, color="yellow", linewidth=0.8)
                patch_axis.set_title(
                    f"{region['regionId']} {label}: strongest observed valley"
                )
            else:
                patch_axis.text(0.5, 0.5, "No extracted valley", ha="center", va="center")
                patch_axis.set_title(f"{region['regionId']} {label}")
            patch_axis.set_axis_off()
    figure.tight_layout()
    figure.savefig(output / "cross_plane_comparison.png", dpi=160)
    plt.close(figure)
    patch_figure.tight_layout()
    patch_figure.savefig(output / "interface_topology_comparison.png", dpi=160)
    plt.close(patch_figure)


def quality_diagnostics(
    session: Path, protocol: dict[str, Any], actor: dict[str, Any]
) -> dict[str, Any]:
    observations = actor["observations"]
    pairs = []
    for region in protocol["regions"]:
        for direction in ("FORWARD", "REVERSE"):
            baseline = [item for item in observations
                        if item["regionId"] == region["regionId"]
                        and item["direction"] == direction
                        and item["variantId"].startswith("LONGITUDINAL")]
            alternative = [item for item in observations
                           if item["regionId"] == region["regionId"]
                           and item["direction"] == direction
                           and item["variantId"].startswith("OBLIQUE")]
            for item in baseline:
                if not alternative:
                    continue
                match = min(alternative, key=lambda candidate: abs(
                    float(candidate["signedActorRelativeMm"])
                    - float(item["signedActorRelativeMm"])
                ))
                delta = abs(float(match["signedActorRelativeMm"])
                            - float(item["signedActorRelativeMm"]))
                pairs.append({
                    "regionId": region["regionId"],
                    "direction": direction,
                    "baselineObservation": item["observationIndex"],
                    "alternativeObservation": match["observationIndex"],
                    "actorCoordinateDeltaMm": delta,
                })
    evaluated = [item for item in observations if item["alternativeViewQualityEvaluated"]]
    return {
        "contract": "thoracic_g2_r6_1_view_quality_v1",
        "evaluatorInputsUsed": False,
        "episodeStatus": actor["status"],
        "freshFrames": len(observations),
        "baselineFrames": sum(item["variantId"].startswith("LONGITUDINAL")
                              for item in observations),
        "alternativeFrames": sum(item["variantId"].startswith("OBLIQUE")
                                 for item in observations),
        "duplicateImageHashes": actor["duplicateImageHashes"],
        "initialAlternativeQuality": evaluated[0] if evaluated else None,
        "matchedViewPairCount": len(pairs),
        "maximumMatchedActorCoordinateDeltaMm": max(
            (item["actorCoordinateDeltaMm"] for item in pairs), default=None
        ),
        "matchedViewPairs": pairs,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--session-root", required=True)
    parser.add_argument("--protocol", required=True)
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    session = Path(args.session_root)
    output = Path(args.output_root)
    actor = read_json(session / "trajectory_actor.json")
    protocol = read_json(Path(args.protocol))
    build_figures(session, protocol, actor, output)
    write_json(output / "view_quality_diagnostics.json",
               quality_diagnostics(session, protocol, actor))


if __name__ == "__main__":
    main()
