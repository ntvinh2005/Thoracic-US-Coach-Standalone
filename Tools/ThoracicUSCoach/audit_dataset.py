from __future__ import annotations

import argparse
import itertools
import json
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
from PIL import Image

from coach_data import (
    DEFAULT_EXPERIMENT_ID,
    LEGACY_EXPERIMENT_ID,
    dataset_fingerprint,
    image_sha256,
    load_frames,
    project_root,
    save_json,
)


def small_image(path: Path, size: int = 32) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("L").resize((size, size), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0


def average_hash(image: np.ndarray) -> int:
    reduced = np.asarray(Image.fromarray((image * 255).astype(np.uint8)).resize((8, 8), Image.Resampling.BILINEAR))
    bits = reduced >= reduced.mean()
    value = 0
    for bit in bits.flat:
        value = (value << 1) | int(bit)
    return value


def hamming(left: int, right: int) -> int:
    return (left ^ right).bit_count()


def rms(left: np.ndarray, right: np.ndarray) -> float:
    return float(np.sqrt(np.mean((left - right) ** 2)))


def resample(values: np.ndarray, count: int = 21) -> np.ndarray:
    source = np.linspace(0.0, 1.0, len(values))
    target = np.linspace(0.0, 1.0, count)
    return np.stack([np.interp(target, source, values[:, column]) for column in range(values.shape[1])], axis=1)


def trajectory_distance(left, right) -> tuple[float, float]:
    lp = resample(np.asarray([frame.position for frame in left], dtype=np.float64))
    rp = resample(np.asarray([frame.position for frame in right], dtype=np.float64))
    lq = resample(np.asarray([frame.rotation for frame in left], dtype=np.float64))
    rq = resample(np.asarray([frame.rotation for frame in right], dtype=np.float64))
    lq /= np.linalg.norm(lq, axis=1, keepdims=True)
    rq /= np.linalg.norm(rq, axis=1, keepdims=True)
    position_rms = float(np.sqrt(np.mean(np.sum((lp - rp) ** 2, axis=1))))
    dots = np.clip(np.abs(np.sum(lq * rq, axis=1)), 0.0, 1.0)
    angle_mean = float(np.degrees(2.0 * np.arccos(dots)).mean())
    return position_rms, angle_mean


def legacy_calibration() -> dict[str, float | int]:
    legacy = load_frames(experiment_id=LEGACY_EXPERIMENT_ID)
    if not legacy:
        return {"frames": 0, "rendererNoiseP95Rms": 0.001, "adjacentPoseP05Rms": 0.004, "nearDuplicateRmsThreshold": 0.0025}
    images = {frame.image_relative: small_image(frame.image) for frame in legacy}
    by_step: dict[int, list] = defaultdict(list)
    by_sweep: dict[str, list] = defaultdict(list)
    for frame in legacy:
        by_step[frame.step_index].append(frame)
        by_sweep[frame.sweep_id].append(frame)
    noise: list[float] = []
    for frames in by_step.values():
        reference = images[frames[0].image_relative]
        noise.extend(rms(reference, images[frame.image_relative]) for frame in frames[1:])
    adjacent: list[float] = []
    for frames in by_sweep.values():
        ordered = sorted(frames, key=lambda frame: frame.step_index)
        adjacent.extend(rms(images[left.image_relative], images[right.image_relative]) for left, right in zip(ordered, ordered[1:]))
    noise_p95 = float(np.percentile(noise, 95)) if noise else 0.001
    adjacent_p05 = float(np.percentile(adjacent, 5)) if adjacent else 0.004
    threshold = float((noise_p95 + adjacent_p05) * 0.5) if adjacent_p05 > noise_p95 else noise_p95 * 1.5
    return {
        "frames": len(legacy),
        "sweeps": len(by_sweep),
        "rendererNoiseP95Rms": noise_p95,
        "adjacentPoseP05Rms": adjacent_p05,
        "nearDuplicateRmsThreshold": threshold,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Trajectory/image diversity and integrity audit")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/diversity_audit_v2.json")
    parser.add_argument("--allow-fail", action="store_true")
    args = parser.parse_args()

    frames = load_frames(experiment_id=args.experiment_id)
    if not frames:
        raise SystemExit(f"No complete frames for experiment {args.experiment_id}")
    sweeps: dict[str, list] = defaultdict(list)
    for frame in frames:
        sweeps[frame.sweep_id].append(frame)
    for sweep in sweeps.values():
        sweep.sort(key=lambda frame: frame.step_index)
    calibration = legacy_calibration()
    duplicate_threshold = float(calibration["nearDuplicateRmsThreshold"])

    failures: list[str] = []
    families: dict[str, set[str]] = defaultdict(set)
    for frame in frames:
        families[frame.configuration_family].add(frame.split)
    leaking_families = {family: sorted(splits) for family, splits in families.items() if len(splits) > 1}
    if leaking_families:
        failures.append("trajectory configuration families span multiple splits")

    trajectory_pairs: list[dict[str, object]] = []
    near_trajectory_pairs: list[dict[str, object]] = []
    for left_id, right_id in itertools.combinations(sorted(sweeps), 2):
        left, right = sweeps[left_id], sweeps[right_id]
        position_rms, angle_mean = trajectory_distance(left, right)
        item = {
            "left": left_id,
            "right": right_id,
            "leftSplit": left[0].split,
            "rightSplit": right[0].split,
            "positionRmsMm": position_rms,
            "orientationMeanDeg": angle_mean,
        }
        trajectory_pairs.append(item)
        if position_rms < 0.25 and angle_mean < 0.25:
            near_trajectory_pairs.append(item)
    cross_split_near = [item for item in near_trajectory_pairs if item["leftSplit"] != item["rightSplit"]]
    if cross_split_near:
        failures.append("exact or near-identical trajectories occur across splits")

    images = {frame.image_relative: small_image(frame.image) for frame in frames}
    hashes = {frame.image_relative: image_sha256(frame.image) for frame in frames}
    phashes = {name: average_hash(image) for name, image in images.items()}
    exact_duplicates = len(frames) - len(set(hashes.values()))
    if exact_duplicates:
        failures.append("exact duplicate clean ultrasound PNGs found")

    within_adjacent: list[float] = []
    for sweep in sweeps.values():
        within_adjacent.extend(rms(images[left.image_relative], images[right.image_relative]) for left, right in zip(sweep, sweep[1:]))

    train = [frame for frame in frames if frame.split == "train"]
    nearest_report: dict[str, object] = {}
    representative_pairs: list[dict[str, object]] = []
    for group_name, selected in (
        ("dev", [frame for frame in frames if frame.split == "dev"]),
        ("testHeldout", [frame for frame in frames if frame.split == "test" and frame.evaluation_group == "heldout"]),
        ("testStress", [frame for frame in frames if frame.split == "test" and frame.evaluation_group == "stress"]),
    ):
        distances: list[float] = []
        perceptual: list[int] = []
        pairs: list[tuple[float, object, object, int]] = []
        for held in selected:
            candidates = [(rms(images[held.image_relative], images[item.image_relative]), item) for item in train]
            distance, nearest = min(candidates, key=lambda value: value[0])
            phash_distance = hamming(phashes[held.image_relative], phashes[nearest.image_relative])
            distances.append(distance)
            perceptual.append(phash_distance)
            pairs.append((distance, held, nearest, phash_distance))
        below = sum(distance <= duplicate_threshold for distance in distances)
        nearest_report[group_name] = {
            "frames": len(selected),
            "rmsMean": float(np.mean(distances)) if distances else None,
            "rmsMedian": float(np.median(distances)) if distances else None,
            "rmsMin": float(np.min(distances)) if distances else None,
            "rmsMax": float(np.max(distances)) if distances else None,
            "nearRendererNoiseCount": below,
            "nearRendererNoiseFraction": below / max(1, len(distances)),
            "perceptualHashHammingMean": float(np.mean(perceptual)) if perceptual else None,
        }
        for distance, held, nearest, phash_distance in sorted(pairs, key=lambda value: value[0])[:3]:
            representative_pairs.append({
                "group": group_name,
                "heldoutImage": held.image_relative,
                "trainImage": nearest.image_relative,
                "rms": distance,
                "perceptualHashHamming": phash_distance,
                "heldoutFamily": held.configuration_family,
                "trainFamily": nearest.configuration_family,
            })
        if selected and below / len(selected) > 0.10:
            failures.append(f"{group_name} contains too many train-near-duplicates at the legacy-calibrated renderer-noise scale")

    split_summary: dict[str, object] = {}
    for split in ("train", "dev", "test"):
        selected = [frame for frame in frames if frame.split == split]
        defined = [frame for frame in selected if frame.teacher_action_defined]
        split_summary[split] = {
            "sweeps": len({frame.sweep_id for frame in selected}),
            "frames": len(selected),
            "families": len({frame.configuration_family for frame in selected}),
            "evaluationGroups": dict(Counter(frame.evaluation_group for frame in selected)),
            "actions": {str(action): sum(frame.teacher_action == action for frame in defined) for action in (-1, 0, 1)},
            "acceptable": sum(frame.target_acceptable for frame in selected),
            "unacceptable": sum(not frame.target_acceptable for frame in selected),
            "teacherUndefined": sum(not frame.teacher_action_defined for frame in selected),
            "contactViolations": sum(not frame.contact_valid or abs(frame.signed_depth_mm) > 0.5 for frame in selected),
        }
        if split in ("train", "dev") and any(split_summary[split]["actions"][str(action)] == 0 for action in (-1, 0, 1)):
            failures.append(f"{split} does not contain all three defined teacher actions")
    if any(summary["contactViolations"] for summary in split_summary.values()):
        failures.append("skin-contact violations found")
    if any(split_summary[split]["sweeps"] == 0 for split in ("train", "dev", "test")):
        failures.append("one or more required splits are empty")

    per_sweep = []
    for sweep_id, sweep in sorted(sweeps.items()):
        per_sweep.append({
            "sweepId": sweep_id,
            "split": sweep[0].split,
            "family": sweep[0].configuration_family,
            "evaluationGroup": sweep[0].evaluation_group,
            "seed": sweep[0].random_seed,
            "frames": len(sweep),
            "pathLengthMm": float(max(frame.path_mm for frame in sweep) - min(frame.path_mm for frame in sweep)),
            "meanStepMm": float(np.mean(np.linalg.norm(np.diff(np.asarray([frame.position for frame in sweep]), axis=0), axis=1))),
            "scoreRange": [min(frame.score for frame in sweep), max(frame.score for frame in sweep)],
            "acceptableFrames": sum(frame.target_acceptable for frame in sweep),
            "pleuraVisibilityRange": [min(frame.pleura_visibility for frame in sweep), max(frame.pleura_visibility for frame in sweep)],
            "shadowPenaltyRange": [min(frame.shadow_penalty for frame in sweep), max(frame.shadow_penalty for frame in sweep)],
            "targetVisibleSamplesMax": max(frame.target_samples for frame in sweep),
        })

    report: dict[str, object] = {
        "schemaVersion": 2,
        "experimentId": args.experiment_id,
        "datasetFingerprint": dataset_fingerprint(frames),
        "passed": not failures,
        "failures": failures,
        "definitions": {
            "nearDuplicateImage": "32x32 grayscale RMS at or below midpoint between legacy renderer-noise p95 and legacy adjacent-pose p05",
            "nearDuplicateTrajectory": "normalized-progress position RMS <0.25 mm and mean quaternion angle <0.25 degrees; threshold separates the legacy 0/0 baseline from configured multi-mm/multi-degree perturbations",
        },
        "legacyCalibration": calibration,
        "summary": {
            "sweeps": len(sweeps),
            "frames": len(frames),
            "configurationFamilies": len(families),
            "uniquePoseSequences": len(sweeps) - len(near_trajectory_pairs),
            "exactImageDuplicates": exact_duplicates,
            "withinSweepAdjacentRmsMean": float(np.mean(within_adjacent)),
            "pathLengthsMm": [min(item["pathLengthMm"] for item in per_sweep), max(item["pathLengthMm"] for item in per_sweep)],
            "startSides": dict(Counter(sweep[0].start_side for sweep in sweeps.values())),
            "pathDirections": dict(Counter(str(sweep[0].path_direction) for sweep in sweeps.values())),
        },
        "splits": split_summary,
        "trajectoryPairs": {
            "pairs": len(trajectory_pairs),
            "nearIdentical": near_trajectory_pairs,
            "crossSplitNearIdentical": cross_split_near,
            "minimumPositionRmsMm": min(item["positionRmsMm"] for item in trajectory_pairs) if trajectory_pairs else None,
            "minimumOrientationMeanDeg": min(item["orientationMeanDeg"] for item in trajectory_pairs) if trajectory_pairs else None,
        },
        "crossSplitNearestImages": nearest_report,
        "representativeMostSimilarPairs": representative_pairs,
        "configurationFamilySplitLeakage": leaking_families,
        "sweeps": per_sweep,
    }
    save_json(args.output, report)
    print(json.dumps({key: report[key] for key in ("experimentId", "passed", "failures", "summary", "splits", "crossSplitNearestImages")}, indent=2))
    if failures and not args.allow_fail:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
