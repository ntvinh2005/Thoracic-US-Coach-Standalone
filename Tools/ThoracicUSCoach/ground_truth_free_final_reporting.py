"""Generate compact final evidence figures for a frozen G2.5 confirmation."""

from __future__ import annotations

import argparse
import csv
from pathlib import Path

import matplotlib.pyplot as plt
from PIL import Image

from g2_r2_continuous_detector import read_json


def generate(session: Path, output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    actor = read_json(session / "trajectory_actor.json")
    candidates = read_json(
        session / "actor_analysis" / "patch_event_candidates.json")["candidates"]
    evaluation = read_json(
        session / "evaluator_only" / "actor_score" / "anatomical_correspondence.json")

    figure, axes = plt.subplots(2, 1, figsize=(13, 7), height_ratios=[2, 1])
    coordinates = [
        float(item["signedActorRelativeMm"]) for item in actor["observations"]]
    axes[0].plot(range(len(coordinates)), coordinates, color="navy")
    axes[0].axhline(0.0, color="black", linestyle="--", linewidth=1)
    axes[0].set_ylabel("Home-relative mm")
    axes[0].set_xlabel("Fresh observation index")
    axes[0].set_title("Frozen ground-truth-free confirmation trajectory")
    axes[0].grid(alpha=0.2)

    for reference in evaluation["references"]:
        coordinate = reference["referenceHomeRelativeMm"]
        if coordinate is not None:
            axes[1].axvline(coordinate, color="black", alpha=0.4)
            axes[1].text(coordinate, 0.92, reference["level"], rotation=90,
                         ha="right", va="top")
    for candidate in candidates:
        axes[1].scatter(candidate["coordinateMm"], 0.35, color="green")
    axes[1].axvline(0.0, color="navy", linestyle="--", label="Home")
    axes[1].set_ylim(0, 1)
    axes[1].set_yticks([0.35], ["actor prediction"])
    axes[1].set_xlabel("Home-relative mm")
    axes[1].set_title("Actor predictions; evaluator references added only after blind freeze")
    axes[1].grid(axis="x", alpha=0.2)
    figure.tight_layout()
    figure.savefig(output / "final_trajectory_and_map.png", dpi=160)
    plt.close(figure)

    figure, axes = plt.subplots(2, 4, figsize=(16, 8))
    for axis, candidate in zip(axes.flat, candidates):
        support = candidate["supportingObservations"][0]
        image = Image.open(session / support["image"]).convert("L")
        axis.imshow(image, cmap="gray", vmin=0, vmax=255)
        axis.set_title(
            f"{candidate['coordinateMm']:+.2f} mm ACCEPTED\n"
            f"score={candidate['score']:.3f}; frame={support['observationIndex']}")
        axis.set_axis_off()
    for axis in axes.flat[len(candidates):]:
        axis.set_axis_off()
    figure.suptitle("Actual fresh ultrasound support frames from the frozen actor")
    figure.tight_layout()
    figure.savefig(output / "final_event_contact_sheet.png", dpi=160)
    plt.close(figure)

    rows = []
    for reference, match in zip(evaluation["references"], evaluation["matches"]):
        error = float(match["absoluteErrorMm"])
        rows.append({
            "level": reference["level"],
            "predictedHomeRelativeMm": match["coordinateMm"],
            "referenceHomeRelativeMm": reference["referenceHomeRelativeMm"],
            "absoluteErrorMm": error,
            "marginToFiveMm": 5.0 - error,
            "pass": error <= 5.0,
        })
    with (output / "per_level_error_analysis.csv").open(
        "w", newline="", encoding="utf-8"
    ) as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)

    figure, axis = plt.subplots(figsize=(9, 4))
    axis.bar([row["level"] for row in rows],
             [row["absoluteErrorMm"] for row in rows], color="teal")
    axis.axhline(5.0, color="red", linestyle="--", label="locked tolerance")
    axis.set_ylabel("Absolute localization error (mm)")
    axis.set_title("Independent per-level localization error")
    axis.set_ylim(0, 5.25)
    axis.grid(axis="y", alpha=0.2)
    axis.legend()
    figure.tight_layout()
    figure.savefig(output / "per_level_localization_error.png", dpi=160)
    plt.close(figure)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    generate(args.session, args.output)


if __name__ == "__main__":
    main()
