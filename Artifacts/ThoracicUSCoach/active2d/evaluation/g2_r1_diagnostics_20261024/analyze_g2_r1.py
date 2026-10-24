from __future__ import annotations

import hashlib
import json
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np


ROOT = Path(__file__).resolve().parents[5]
TOOLS = ROOT / "Tools" / "ThoracicUSCoach"
sys.path.insert(0, str(TOOLS))

from active2d_data import dataset_fingerprint, load_active2d_frames  # noqa: E402
from active2d_stop import (  # noqa: E402
    frame_stop_feature_matrix_v2,
    frame_temporal_feature_matrix_v3,
    quality_predictions,
    request_stop_features,
    stop_probabilities,
    validate_stop_checkpoint,
)
from train_phase2_stop import classification_metrics, replay_stop  # noqa: E402


DATASET_RELATIVE = Path("Artifacts/ThoracicUSCoach/active2d/g2_temporal_capture")
DATASET_RUN_ID = "g2_temporal_stop_v1_20261024"
CHECKPOINT = Path("Artifacts/ThoracicUSCoach/active2d/checkpoints/g2_temporal_quality_stop_v1.json")
BASELINE = Path("Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_stop_live_spatial_logistic_v1.json")
FLOOR = 0.5912307500839233


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def quantiles(values: np.ndarray) -> dict[str, float | int | None]:
    if len(values) == 0:
        return {"count": 0, "min": None, "q25": None, "median": None, "q75": None, "max": None}
    return {
        "count": int(len(values)), "min": float(values.min()),
        "q25": float(np.quantile(values, 0.25)), "median": float(np.median(values)),
        "q75": float(np.quantile(values, 0.75)), "max": float(values.max()),
    }


def episode_metrics(frames, scores: np.ndarray, threshold: float) -> dict:
    replay = replay_stop(frames, scores, threshold, 1)
    groups: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        groups[frame.episode_id].append(index)
    rows = []
    for episode_id, indices in groups.items():
        positives = [index for index in indices if frames[index].target_acceptable]
        above = [index for index in indices if scores[index] >= threshold]
        first = above[0] if above else None
        rows.append({
            "episodeId": episode_id,
            "family": frames[indices[0]].family,
            "targetObserved": bool(positives),
            "positiveFrames": len(positives),
            "maximumStopScore": float(max(scores[index] for index in indices)),
            "firstThresholdCrossingFrame": None if first is None else frames[first].index,
            "firstThresholdCrossingCorrect": None if first is None else bool(frames[first].target_acceptable),
        })
    return {"replay": replay, "episodes": rows}


def threshold_sweep(frames, scores: np.ndarray) -> list[dict]:
    labels = np.asarray([frame.target_acceptable for frame in frames], dtype=bool)
    candidates = sorted(set([0.0, 0.05, 0.10, 0.20, 0.30, 0.50, 0.75, 0.90, 0.93, 0.95, 0.99, 1.0,
                             *[float(value) for value in scores]]))
    rows = []
    for threshold in candidates:
        cls = classification_metrics(labels, scores, threshold)
        replay = replay_stop(frames, scores, threshold, 1)
        rows.append({"threshold": threshold, "classification": cls, "episodeReplay": replay})
    zero_fp = [row for row in rows if row["classification"]["falsePositive"] == 0]
    best_zero_fp = max(zero_fp, key=lambda row: (row["classification"]["recall"], row["threshold"]))
    maximum_recall_zero_wrong_episode = max(
        (row for row in rows if row["episodeReplay"]["wrongStops"] == 0),
        key=lambda row: (row["classification"]["recall"], row["threshold"]),
    )
    display_thresholds = {0.0, 0.05, 0.10, 0.20, 0.30, 0.50, 0.75, 0.90, 0.93, 0.95, 0.99, 1.0}
    return {
        "operatingPoints": [row for row in rows if row["threshold"] in display_thresholds],
        "bestZeroFrameFalsePositive": best_zero_fp,
        "bestZeroWrongStopEpisode": maximum_recall_zero_wrong_episode,
        "eligibleRecall80ZeroFrameFalsePositiveCount": sum(
            row["classification"]["recall"] >= 0.8 and row["classification"]["falsePositive"] == 0
            for row in rows
        ),
    }


def main() -> None:
    checkpoint_path = ROOT / CHECKPOINT
    baseline_path = ROOT / BASELINE
    checkpoint = json.loads(checkpoint_path.read_text(encoding="utf-8"))
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    validate_stop_checkpoint(checkpoint)
    validate_stop_checkpoint(baseline)
    frames = load_active2d_frames(DATASET_RUN_ID, root=ROOT, dataset_relative=DATASET_RELATIVE)
    splits = {name: [frame for frame in frames if frame.split == name]
              for name in ("train", "calibration", "heldout_dev")}

    manifests = sorted((ROOT / DATASET_RELATIVE).glob("*/*/stream_manifest.json"))
    manifest_errors = []
    image_hashes = 0
    action_counts = Counter()
    accepted = completed = resets = 0
    label_floor_mismatches = 0
    index_alignment_errors = 0
    families_by_split: dict[str, set[str]] = defaultdict(set)
    yaw_by_split: dict[str, Counter] = defaultdict(Counter)
    capture_manifest = json.loads(
        (ROOT / "Tools/ThoracicUSCoach/experiments/g2_temporal_capture_v1.json").read_text(encoding="utf-8")
    )
    capture_cases = {item["caseId"]: item for item in capture_manifest["cases"]}
    for manifest_path in manifests:
        stream = json.loads(manifest_path.read_text(encoding="utf-8"))
        annotations_path = manifest_path.with_name("privileged_annotations.json")
        annotations = json.loads(annotations_path.read_text(encoding="utf-8"))
        labels = {int(item["index"]): item for item in annotations["frames"]}
        items = stream["frames"]
        if len(items) != len(labels):
            manifest_errors.append(f"frame count mismatch: {manifest_path}")
        expected_indices = list(range(len(items)))
        actual_indices = [int(item["index"]) for item in items]
        if actual_indices != expected_indices or sorted(labels) != expected_indices:
            index_alignment_errors += 1
        if not items or not items[0].get("episodeReset") or any(item.get("episodeReset") for item in items[1:]):
            manifest_errors.append(f"reset alignment invalid: {manifest_path}")
        families_by_split[stream["split"]].add(stream["family"])
        yaw_by_split[stream["split"]][str(capture_cases[stream["episodeId"]]["sourceYawDeg"])] += 1
        for item in items:
            index = int(item["index"])
            image = ROOT / str(item["image"])
            if not image.is_file() or sha256(image) != item["imageSha256"]:
                manifest_errors.append(f"image hash/path mismatch: {image}")
            image_hashes += 1
            action_counts[str(item["issuedAction"])] += 1
            accepted += int(bool(item.get("actionAccepted")))
            completed += int(bool(item.get("actionCompleted")))
            resets += int(bool(item.get("episodeReset")))
            label = labels[index]
            if bool(label["finalTargetAcceptable"]) != (float(label["finalTargetScore"]) >= FLOOR):
                label_floor_mismatches += 1

    overlap = {}
    for left in families_by_split:
        for right in families_by_split:
            if left < right:
                overlap[f"{left}__{right}"] = sorted(families_by_split[left] & families_by_split[right])

    split_results = {}
    parity_max = 0.0
    for split, selected in splits.items():
        temporal = frame_temporal_feature_matrix_v3(selected)
        manual_rows = []
        previous_episode = None
        previous_image = None
        for frame in selected:
            if frame.episode_id != previous_episode or frame.episode_reset:
                previous_image = None
            manual_rows.append(request_stop_features(
                frame.image, previous_image, frame.issued_action, "temporal-v3",
                frame.action_accepted, frame.action_completed,
            ))
            previous_image = frame.image
            previous_episode = frame.episode_id
        manual = np.stack(manual_rows)
        parity_max = max(parity_max, float(np.max(np.abs(temporal - manual))))
        stop_scores = stop_probabilities(checkpoint, temporal)
        quality_scores = quality_predictions(checkpoint, temporal)
        labels = np.asarray([frame.target_acceptable for frame in selected], dtype=bool)
        target_scores = np.asarray([frame.target_score for frame in selected], dtype=float)
        threshold = float(checkpoint["stopRule"]["threshold"])
        split_results[split] = {
            "frames": len(selected),
            "families": sorted({frame.family for frame in selected}),
            "positiveFrames": int(labels.sum()),
            "positiveScoreDistribution": quantiles(stop_scores[labels]),
            "negativeScoreDistribution": quantiles(stop_scores[~labels]),
            "qualityPredictionDistribution": quantiles(quality_scores),
            "evaluatorScoreDistribution": quantiles(target_scores),
            "nearNegativeFramesWithin0_05OfFloor": int(np.sum((~labels) & (target_scores >= FLOOR - 0.05))),
            "nearNegativeFramesWithin0_02OfFloor": int(np.sum((~labels) & (target_scores >= FLOOR - 0.02))),
            "classificationAtFrozenCandidateThreshold": classification_metrics(labels, stop_scores, threshold),
            "episodeAtFrozenCandidateThreshold": episode_metrics(selected, stop_scores, threshold),
        }
        if split == "calibration":
            split_results[split]["thresholdSweepDevelopmentOnly"] = threshold_sweep(selected, stop_scores)
        if split == "heldout_dev":
            baseline_features = frame_stop_feature_matrix_v2(selected)
            baseline_scores = stop_probabilities(baseline, baseline_features)
            split_results[split]["frozenSpatialV2At0_93"] = {
                "classification": classification_metrics(labels, baseline_scores, 0.93),
                "episode": episode_metrics(selected, baseline_scores, 0.93),
                "positiveScoreDistribution": quantiles(baseline_scores[labels]),
                "negativeScoreDistribution": quantiles(baseline_scores[~labels]),
            }

    policy_source = (ROOT / "Tools/ThoracicUSCoach/run_active2d_policy_server.py").read_text(encoding="utf-8")
    actor_source = (ROOT / "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs").read_text(encoding="utf-8")
    trainer_source = (ROOT / "Tools/ThoracicUSCoach/train_g2_temporal_stop.py").read_text(encoding="utf-8")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    branch = subprocess.check_output(["git", "branch", "--show-current"], cwd=ROOT, text=True).strip()
    origin_main = subprocess.check_output(["git", "rev-parse", "origin/main"], cwd=ROOT, text=True).strip()
    result = {
        "schemaVersion": 1,
        "taskId": "G2-R1",
        "diagnosticOnly": True,
        "repository": {"branch": branch, "head": head, "originMain": origin_main},
        "provenance": {
            "datasetRunId": DATASET_RUN_ID,
            "datasetFingerprint": dataset_fingerprint(frames),
            "checkpoint": CHECKPOINT.as_posix(), "checkpointSha256": sha256(checkpoint_path),
            "baseline": BASELINE.as_posix(), "baselineSha256": sha256(baseline_path),
            "captureManifestSha256": sha256(ROOT / "Tools/ThoracicUSCoach/experiments/g2_temporal_capture_v1.json"),
            "sceneSha256": sha256(ROOT / "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity"),
            "featureSourceSha256": sha256(ROOT / "Tools/ThoracicUSCoach/active2d_stop.py"),
            "trainerSourceSha256": sha256(ROOT / "Tools/ThoracicUSCoach/train_g2_temporal_stop.py"),
            "policyServerSha256": sha256(ROOT / "Tools/ThoracicUSCoach/run_active2d_policy_server.py"),
        },
        "datasetAudit": {
            "manifests": len(manifests), "frames": len(frames), "imagesHashChecked": image_hashes,
            "manifestErrors": manifest_errors, "indexAlignmentErrors": index_alignment_errors,
            "labelFloor": FLOOR, "labelFloorMismatches": label_floor_mismatches,
            "actionCounts": dict(action_counts), "actionAccepted": accepted,
            "actionCompleted": completed, "episodeResetFrames": resets,
            "familiesBySplit": {key: sorted(value) for key, value in families_by_split.items()},
            "familyOverlap": overlap,
            "yawEpisodeCountsBySplit": {key: dict(value) for key, value in yaw_by_split.items()},
            "trainOfflineFeatureParityMaxAbsError": parity_max,
        },
        "checkpoint": {
            "architecture": checkpoint["architecture"]["type"],
            "featureExtractor": checkpoint["architecture"]["featureExtractor"],
            "featureWidth": checkpoint["architecture"]["featureWidth"],
            "threshold": checkpoint["stopRule"]["threshold"],
            "thresholdFallbackIsAbstention": checkpoint["stopRule"]["threshold"] == 1.0,
        },
        "splits": split_results,
        "parityAudit": {
            "numericTrainVsOfflineFeatureParity": parity_max == 0.0,
            "liveCallsTemporalExtractor": "request_stop_features(image_path, memory.previous_image, memory.last_action, extractor)" in policy_source,
            "livePassesAcceptedCompletedFlagsToTemporalExtractor": False,
            "unitySuppliesExecutionFeedback": all(token in actor_source for token in (
                "previousActionAccepted", "previousActionCompleted", "IsActive2DActionExecutionComplete")),
            "liveConsumesExecutionFeedbackOnlyWhenActionModelEnabled": "if action_model is not None and not request[\"reset\"]" in policy_source,
            "liveUsesQualityHeadForBestFrame": "quality_predictions" in policy_source,
            "liveRevalidatesFreshStopScoreAfterReturn": False,
            "offlineReplayModelsRasterCompletionAndPhysicalReturn": False,
            "offlineReplayFinding": "replay_stop terminates at first threshold crossing; it does not execute the raster, remember/return physically, model rejected motion, or require a fresh confirming image.",
        },
        "fallbackAudit": {
            "trainerNoEligibleBranch": "selected = max(rows, key=lambda row: (-row[1][\"falsePositive\"], row[1][\"recall\"], row[0]))" in trainer_source,
            "finding": "When no safe candidate exists the trainer serializes the fallback threshold (1.0 here) into a normal checkpoint and reports OFFLINE_BLOCKED. Validation accepts it; no explicit non-deployable checkpoint flag is enforced by live inference.",
        },
        "safetyStaticAudit": {
            "terminalContactWorkspaceRevalidation": "ValidateActive2DTerminalSafety" in actor_source,
            "rollbackFailureTelemetry": all(token in actor_source for token in (
                "rollbackAttempted", "rollbackContactRestored", "safetyFailure")),
            "rotationWorkspaceValidation": "rotation candidate failed safety validation" in actor_source,
            "ineffectiveMotionRejected": "translation made less than minimum meaningful progress" in actor_source,
        },
        "decisions": {
            "retraining": "NO-GO until data/representation and calibration fallback defects are specified and independently reviewed",
            "boundedUnityClosedLoop": "NO-GO because STOP offline gate and live parity are blocked",
            "gate2FortyEpisode": "NO-GO; Phase 1 remains untouched and the existing Gate2 protocol parity tests currently fail",
        },
    }
    output = Path(__file__).with_name("g2_r1_diagnostics.json")
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output), "frames": len(frames), "manifestErrors": len(manifest_errors),
        "featureParityMaxAbsError": parity_max,
        "calibrationEligible": split_results["calibration"]["thresholdSweepDevelopmentOnly"]["eligibleRecall80ZeroFrameFalsePositiveCount"],
    }, indent=2))


if __name__ == "__main__":
    main()
