from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image

from coach_data import load_frames, save_json


def pixels(path: Path, size: int) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR),
            dtype=np.float32,
        ).reshape(-1) / 255.0


def summary(values) -> dict[str, object]:
    values = np.asarray(list(values), dtype=np.float64)
    return {
        "count": len(values),
        "mean": float(values.mean()),
        "min": float(values.min()),
        "q25": float(np.quantile(values, 0.25)),
        "median": float(np.median(values)),
        "q75": float(np.quantile(values, 0.75)),
        "max": float(values.max()),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="16x16 versus 32x32 development label-separability audit")
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    frames = load_frames(
        root=args.root,
        dataset_run_id=args.dataset_run_id,
        splits={"dev"},
    )
    report = {"sourceSplit": "dev", "heldoutOrStressAccessed": False, "frameCount": len(frames), "resolutions": {}}
    for size in (16, 32):
        matrix = np.stack([pixels(frame.image, size) for frame in frames])
        positive_indices = np.asarray([index for index, frame in enumerate(frames) if frame.target_acceptable])
        negative_indices = np.asarray([index for index, frame in enumerate(frames) if not frame.target_acceptable])
        positive = matrix[positive_indices]
        negative = matrix[negative_indices]
        squared = (
            np.sum(positive * positive, axis=1)[:, None]
            + np.sum(negative * negative, axis=1)[None, :]
            - 2.0 * positive @ negative.T
        )
        distance = np.sqrt(np.maximum(0.0, squared) / matrix.shape[1])
        by_sweep = defaultdict(list)
        for index, frame in enumerate(frames):
            by_sweep[frame.sweep_id].append((frame.step_index, index, frame.target_acceptable))
        boundary = []
        for items in by_sweep.values():
            items.sort()
            for left, right in zip(items, items[1:]):
                if left[2] != right[2]:
                    boundary.append(float(np.sqrt(np.mean((matrix[left[1]] - matrix[right[1]]) ** 2))))
        flattened = np.argsort(distance, axis=None)[:20]
        pairs = []
        for flat in flattened:
            p, n = np.unravel_index(flat, distance.shape)
            pairs.append(
                {
                    "rms": float(distance[p, n]),
                    "acceptableImage": frames[int(positive_indices[p])].image_relative,
                    "unacceptableImage": frames[int(negative_indices[n])].image_relative,
                }
            )
        report["resolutions"][str(size)] = {
            "adjacentBoundaryRms": summary(boundary),
            "nearestNegativePerPositiveRms": summary(np.min(distance, axis=1)),
            "nearestPositivePerNegativeRms": summary(np.min(distance, axis=0)),
            "twentyClosestOppositePairs": pairs,
        }
    sixteen = report["resolutions"]["16"]["nearestNegativePerPositiveRms"]["median"]
    thirty_two = report["resolutions"]["32"]["nearestNegativePerPositiveRms"]["median"]
    report["interpretation"] = {
        "medianNearestOppositeRatio32To16": thirty_two / sixteen,
        "note": "A larger distance at 32x32 means extra pixels preserve some signal; it does not establish a reliable decision boundary or closed-loop benefit.",
    }
    save_json(args.output, report)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
