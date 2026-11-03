"""Generate compact human-readable G2.5 development figures."""

from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib.pyplot as plt
from PIL import Image

from g2_r2_continuous_detector import read_json


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--campaign", required=True, type=Path)
    args = parser.parse_args()
    campaign = args.campaign
    session = campaign / "development_session_02"
    actor = read_json(session / "trajectory_actor.json")
    candidates = read_json(
        session / "actor_analysis_terminal_prefix" / "patch_event_candidates.json"
    )["candidates"]
    evaluation = read_json(
        session / "evaluator_only" / "ground_truth_free_scoring"
        / "anatomical_correspondence.json")

    figure, axes = plt.subplots(2, 1, figsize=(13, 7), height_ratios=[2, 1])
    coordinates = [
        float(item["signedActorRelativeMm"]) for item in actor["observations"]]
    axes[0].plot(range(len(coordinates)), coordinates, color="navy")
    axes[0].axhline(0.0, color="black", linestyle="--", linewidth=1)
    axes[0].set_ylabel("Home-relative mm")
    axes[0].set_xlabel("Fresh observation index")
    axes[0].set_title("Executed G2.5 development trajectory (session 02)")
    axes[0].grid(alpha=0.2)

    for reference in evaluation["references"]:
        coordinate = reference["referenceHomeRelativeMm"]
        if coordinate is not None:
            axes[1].axvline(coordinate, color="black", alpha=0.4)
            axes[1].text(coordinate, 0.92, reference["level"], rotation=90,
                         ha="right", va="top")
    for candidate in candidates:
        axes[1].scatter(
            candidate["coordinateMm"], 0.35 if candidate["accepted"] else 0.15,
            color="green" if candidate["accepted"] else "orange")
    axes[1].axvline(0.0, color="navy", linestyle="--", label="Home")
    axes[1].set_ylim(0, 1)
    axes[1].set_yticks([0.15, 0.35], ["rejected", "accepted"])
    axes[1].set_xlabel("Home-relative mm")
    axes[1].set_title("Actor candidates; evaluator references overlaid only after freeze")
    axes[1].grid(axis="x", alpha=0.2)
    figure.tight_layout()
    figure.savefig(campaign / "development_trajectory_and_map.png", dpi=160)
    plt.close(figure)

    figure, axes = plt.subplots(2, 4, figsize=(16, 8))
    for axis, candidate in zip(axes.flat, candidates):
        support = candidate["supportingObservations"][0]
        image = Image.open(session / support["image"]).convert("L")
        axis.imshow(image, cmap="gray", vmin=0, vmax=255)
        state = "ACCEPTED" if candidate["accepted"] else "REJECTED"
        axis.set_title(
            f"{candidate['coordinateMm']:+.2f} mm {state}\n"
            f"score={candidate['score']:.3f}")
        axis.set_axis_off()
    for axis in axes.flat[len(candidates):]:
        axis.set_axis_off()
    figure.suptitle("Actual session-02 ultrasound support frames (actor-only decisions)")
    figure.tight_layout()
    figure.savefig(campaign / "actor_event_contact_sheet.png", dpi=160)
    plt.close(figure)


if __name__ == "__main__":
    main()
