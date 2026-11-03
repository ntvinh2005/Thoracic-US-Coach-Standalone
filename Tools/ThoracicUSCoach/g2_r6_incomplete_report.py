"""Render truthful G2-R6 evidence when the frozen acquisition gate stops early."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import matplotlib.pyplot as plt

from g2_r4_multi_hypothesis_tracker import track_multiple_interfaces
from relative_spine_e2_analysis import load_gray


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def render(session_root: Path, output_root: Path) -> dict:
    actor = read_json(session_root / "trajectory_actor.json")
    observations = actor["observations"]
    movements = actor["movements"]
    selected = [observations[index] for index in (0, len(observations) // 2, len(observations) - 1)]

    figure, axes = plt.subplots(2, 3, figsize=(15, 9), constrained_layout=True)
    for axis, observation in zip(axes[0], selected):
        image = load_gray(session_root / observation["image"])
        axis.imshow(image, cmap="gray", vmin=0.0, vmax=1.0)
        axis.set_title(
            f"Actual yaw 90° | {observation['direction']}\n"
            f"s={observation['signedActorRelativeMm']:.3f} mm | frame {observation['observationIndex']}"
        )
        axis.axis("off")
    messages = [
        (
            "Yaw 60° initial image\nNOT PERSISTED BY FROZEN RUNTIME\n"
            f"Transient pixel MAE={actor['initialCrossPlanePixelMeanAbsoluteDifference']:.6f}\n"
            "Required MAE ≥ 0.020000"
        ),
        "R6R02 stable secondary interface\nNOT ACQUIRED: session stopped at R6R01 quality gate",
        "R6R03 supported-event control\nNOT ACQUIRED: session stopped at R6R01 quality gate",
    ]
    for axis, message in zip(axes[1], messages):
        axis.set_facecolor("#eeeeee")
        axis.text(0.5, 0.5, message, ha="center", va="center", wrap=True, fontsize=11)
        axis.set_xticks([])
        axis.set_yticks([])
    figure.suptitle("G2-R6 cross-plane acquisition: actual evidence and unavailable comparisons")
    figure.savefig(output_root / "cross_plane_comparison.png", dpi=170)
    plt.close(figure)

    figure, axes = plt.subplots(1, 3, figsize=(15, 5), constrained_layout=True)
    colors = ("#ff3b30", "#00c853", "#2979ff")
    for axis, observation in zip(axes, selected):
        image = load_gray(session_root / observation["image"])
        tracks = track_multiple_interfaces(image)
        axis.imshow(image, cmap="gray", vmin=0.0, vmax=1.0)
        for color, track in zip(colors, tracks[:3]):
            axis.plot(track["x"], track["pathY"], color=color, linewidth=1.0,
                      label=track["trackId"])
        axis.set_title(
            f"Yaw 90° contours\ns={observation['signedActorRelativeMm']:.3f} mm"
        )
        axis.axis("off")
        if tracks:
            axis.legend(loc="lower right", fontsize=7)
    figure.suptitle("Predeclared multi-hypothesis contours on acquired R6R01 baseline frames")
    figure.savefig(output_root / "interface_topology_comparison.png", dpi=170)
    plt.close(figure)

    accepted = [item for item in movements if item["accepted"]]
    rejected = [item for item in movements if not item["accepted"]]
    diagnostic = {
        "contract": "thoracic_g2_r6_incomplete_session_diagnostics_v1",
        "scientificVerdict": "INCONCLUSIVE_ACQUISITION",
        "stopGate": "MINIMUM_INITIAL_CROSS_PLANE_PIXEL_MAE",
        "pixelMae": actor["initialCrossPlanePixelMeanAbsoluteDifference"],
        "requiredPixelMae": 0.02,
        "absoluteShortfall": 0.02 - actor["initialCrossPlanePixelMeanAbsoluteDifference"],
        "fractionBelowThreshold": (
            0.02 - actor["initialCrossPlanePixelMeanAbsoluteDifference"]
        ) / 0.02,
        "freshFramesPersisted": len(observations),
        "uniqueImageHashes": len({item["imageSha256"] for item in observations}),
        "acceptedMovements": len(accepted),
        "rejectedMovements": len(rejected),
        "allPersistedFramesFreshAndValid": all(
            item["frameIntegrityValid"] and not item["duplicateImageHash"]
            for item in observations
        ),
        "allPersistedFramesContactAndWorkspaceValid": all(
            item["contactValid"] and item["workspaceValid"] for item in observations
        ),
        "regionsWithPersistedFrames": sorted({item["regionId"] for item in observations}),
        "variantsWithPersistedFrames": sorted({item["variantId"] for item in observations}),
        "directionsWithPersistedFrames": sorted({item["direction"] for item in observations}),
        "returnedHome": False,
        "settingsRestored": True,
        "obliqueImagePersisted": False,
        "limitation": (
            "The transient yaw-60 image was measured by the frozen runtime but was not saved "
            "before the quality-gate exception; no pixel-level cross-view topology comparison is possible."
        ),
    }
    write_json(output_root / "incomplete_session_diagnostics.json", diagnostic)
    return diagnostic


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--session-root", required=True)
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    print(json.dumps(render(Path(args.session_root), Path(args.output_root)), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
