from __future__ import annotations

import argparse
import hashlib
import json
from collections import defaultdict
from dataclasses import asdict
from pathlib import Path
from typing import Any

import numpy as np
from sklearn.linear_model import LogisticRegression, Ridge

from active2d_contract import ACTION_ORDER, CONTRACT, EXPERIMENT_ID, LEVEL_ORDER
from active2d_data import Active2DFrame
from active2d_stop import (
    DEPLOYMENT_GATE,
    convolutional_features_v2,
    raw_features_16,
    stop_probabilities,
    validate_stop_checkpoint,
)
from train_g2_temporal_stop import rankdata, select_threshold, spearman
from train_phase2_stop import classification_metrics


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def action_vector(action: str) -> np.ndarray:
    result = np.zeros(len(ACTION_ORDER), dtype=np.float32)
    normalized = action.replace("_", "").upper()
    if normalized in ACTION_ORDER:
        result[ACTION_ORDER.index(normalized)] = 1.0
    return result


def load_cases(
    manifest: dict[str, Any], source_root: Path, roles: set[str]
) -> dict[str, list[Active2DFrame]]:
    result: dict[str, list[Active2DFrame]] = defaultdict(list)
    for case in manifest["cases"]:
        role = str(case["role"])
        if role not in roles:
            continue
        directory = source_root / case["episodeDirectory"]
        stream_path = directory / "stream_manifest.json"
        labels_path = directory / "privileged_annotations.json"
        audit_path = directory / "start_audit.txt"
        expected = {
            stream_path: case["streamManifestSha256"],
            labels_path: case["privilegedAnnotationsSha256"],
            audit_path: case["startAuditSha256"],
        }
        for path, digest in expected.items():
            if not path.is_file() or sha256(path) != digest:
                raise ValueError(f"Frozen G2-R1 source mismatch: {path}")
        stream = json.loads(stream_path.read_text(encoding="utf-8"))
        labels_payload = json.loads(labels_path.read_text(encoding="utf-8"))
        if (
            stream.get("contract") != CONTRACT
            or stream.get("experimentId") != EXPERIMENT_ID
            or stream.get("datasetRunId") != manifest["datasetRunId"]
            or stream.get("family") != case["family"]
            or stream.get("captureStatus") != "complete"
        ):
            raise ValueError(f"G2-R1 stream identity mismatch: {stream_path}")
        labels = {int(item["index"]): item for item in labels_payload["frames"]}
        if len(labels) != len(stream["frames"]):
            raise ValueError(f"G2-R1 stream/label alignment mismatch: {stream_path}")
        for item in stream["frames"]:
            index = int(item["index"])
            label = labels[index]
            relative = str(item["image"]).replace("\\", "/")
            image = source_root / relative
            digest = sha256(image)
            if digest != item["imageSha256"]:
                raise ValueError(f"G2-R1 image hash mismatch: {image}")
            level = str(label["nearestLevel"])
            if level not in LEVEL_ORDER:
                raise ValueError(f"Unsupported level label: {level}")
            result[role].append(Active2DFrame(
                image=image,
                image_relative=relative,
                image_sha256=digest,
                episode_id=str(stream["episodeId"]),
                dataset_run_id=str(stream["datasetRunId"]),
                split=role,
                family=str(stream["family"]),
                seed=int(stream["seed"]),
                index=index,
                issued_action=str(item.get("issuedAction") or "RESET").upper(),
                action_accepted=bool(item.get("actionAccepted", True)),
                action_completed=bool(item.get("actionCompleted", item.get("actionAccepted", True))),
                episode_reset=bool(item.get("episodeReset", False)),
                level=level,
                t7_distance_mm=float(label.get("t7DistanceMm", float("inf"))),
                t7_acceptable=bool(label.get("t7LevelAcceptable", False)),
                target_acceptable=bool(label.get("finalTargetAcceptable", False)),
                target_score=float(label.get("finalTargetScore", 0.0)),
                contact_valid=bool(label.get("contactValid", False)),
                workspace_valid=bool(label.get("workspaceValid", False)),
            ))
    for frames in result.values():
        frames.sort(key=lambda item: (item.episode_id, item.index))
    return dict(result)


def build_features(frames: list[Active2DFrame]) -> dict[str, np.ndarray]:
    raw = np.stack([raw_features_16(frame.image) for frame in frames])
    previous_rows: list[np.ndarray] = []
    previous_episode = ""
    previous = None
    for frame, current in zip(frames, raw):
        if frame.episode_id != previous_episode or frame.episode_reset or previous is None:
            previous = current
        previous_rows.append(previous)
        previous = current
        previous_episode = frame.episode_id
    old = np.stack(previous_rows)
    image_history = np.concatenate((raw, old, raw - old, np.abs(raw - old)), axis=1)
    actions = np.stack([
        action_vector(frame.issued_action if frame.action_accepted and frame.action_completed else "RESET")
        for frame in frames
    ])
    feedback = np.asarray([
        [
            float(not frame.episode_reset),
            float(frame.action_accepted),
            float(frame.action_completed),
        ]
        for frame in frames
    ], dtype=np.float32)
    return {
        "current_raw16": raw,
        "temporal_images_no_action": image_history,
        "temporal_v3_full": np.concatenate((image_history, actions, feedback), axis=1),
    }


def distribution(values: np.ndarray) -> dict[str, Any]:
    if len(values) == 0:
        return {"count": 0}
    return {
        "count": int(len(values)),
        "min": float(values.min()),
        "q25": float(np.quantile(values, 0.25)),
        "median": float(np.median(values)),
        "q75": float(np.quantile(values, 0.75)),
        "max": float(values.max()),
    }


def wilson(successes: int, total: int) -> list[float] | None:
    if total == 0:
        return None
    z = 1.959963984540054
    p = successes / total
    denominator = 1.0 + z * z / total
    center = (p + z * z / (2 * total)) / denominator
    radius = z * np.sqrt((p * (1 - p) + z * z / (4 * total)) / total) / denominator
    return [float(max(0.0, center - radius)), float(min(1.0, center + radius))]


def evaluate(
    frames: list[Active2DFrame], stop: np.ndarray, quality: np.ndarray, threshold: float
) -> dict[str, Any]:
    labels = np.asarray([frame.target_acceptable for frame in frames], dtype=bool)
    classification = classification_metrics(labels, stop, threshold)
    groups: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        groups[frame.episode_id].append(index)
    observed = recognized = wrong = misses = 0
    regrets: list[float] = []
    family_results: dict[str, Any] = {}
    for indices in groups.values():
        episode_frames = [frames[index] for index in indices]
        actual = np.asarray([frame.target_score for frame in episode_frames])
        predicted = quality[indices]
        crossings = [index for index in indices if stop[index] >= threshold]
        target_observed = any(frame.target_acceptable for frame in episode_frames)
        correct_crossing = any(frames[index].target_acceptable for index in crossings)
        wrong_crossing = any(not frames[index].target_acceptable for index in crossings)
        observed += int(target_observed)
        recognized += int(target_observed and correct_crossing)
        wrong += int(wrong_crossing)
        misses += int(not crossings)
        regret = float(actual.max() - actual[int(np.argmax(predicted))])
        regrets.append(regret)
        family = episode_frames[0].family
        family_results[family] = {
            "frames": len(indices),
            "positiveFrames": sum(frame.target_acceptable for frame in episode_frames),
            "targetObserved": target_observed,
            "targetRecognized": correct_crossing,
            "wrongStop": wrong_crossing,
            "miss": not crossings,
            "bestViewRegret": regret,
            "maximumStopScore": float(stop[indices].max()),
        }
    recall_success = int(classification["truePositive"])
    recall_total = int(classification["positiveSupport"])
    return {
        "classification": classification,
        "recallWilson95": wilson(recall_success, recall_total),
        "qualitySpearman": spearman(
            np.asarray([frame.target_score for frame in frames]), quality
        ),
        "medianEpisodeBestViewRegret": float(np.median(regrets)),
        "episodes": len(groups),
        "targetObservedEpisodes": observed,
        "targetRecognizedEpisodes": recognized,
        "wrongStopEpisodes": wrong,
        "missEpisodes": misses,
        "conditionalRecognitionRate": recognized / observed if observed else 0.0,
        "familyResults": family_results,
    }


def fit_candidate(
    x_train: np.ndarray,
    y_train: np.ndarray,
    q_train: np.ndarray,
    *,
    seed: int,
) -> dict[str, Any]:
    mean = x_train.mean(axis=0)
    scale = x_train.std(axis=0)
    scale[scale < 1e-5] = 1.0
    z = (x_train - mean) / scale
    classifier = LogisticRegression(
        C=0.25,
        class_weight="balanced",
        max_iter=500,
        random_state=seed,
        solver="liblinear",
        tol=1e-6,
    ).fit(z, y_train)
    quality = Ridge(alpha=5.0).fit(z, q_train)
    return {
        "mean": mean.astype(np.float32),
        "scale": scale.astype(np.float32),
        "stopWeights": classifier.coef_[0].astype(np.float32),
        "stopBias": float(classifier.intercept_[0]),
        "qualityWeights": quality.coef_.astype(np.float32),
        "qualityBias": float(quality.intercept_),
    }


def predict(candidate: dict[str, Any], values: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    z = (values - candidate["mean"]) / candidate["scale"]
    logits = z @ candidate["stopWeights"] + candidate["stopBias"]
    stop = 1.0 / (1.0 + np.exp(-np.clip(logits, -30.0, 30.0)))
    quality = np.clip(z @ candidate["qualityWeights"] + candidate["qualityBias"], 0.0, 1.0)
    return stop, quality


def data_diagnosis(
    roles: dict[str, list[Active2DFrame]], features: dict[str, dict[str, np.ndarray]]
) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for role, frames in roles.items():
        labels = np.asarray([frame.target_acceptable for frame in frames], dtype=bool)
        families = {frame.family for frame in frames}
        positive_families = {frame.family for frame in frames if frame.target_acceptable}
        result[role] = {
            "frames": len(frames),
            "families": len(families),
            "positiveFrames": int(labels.sum()),
            "positiveFamilies": len(positive_families),
            "acceptedActions": sum(frame.action_accepted and not frame.episode_reset for frame in frames),
            "completedActions": sum(frame.action_completed and not frame.episode_reset for frame in frames),
            "rejectedActions": sum(not frame.action_accepted for frame in frames),
            "targetScore": distribution(np.asarray([frame.target_score for frame in frames])),
        }
    combined_frames = roles["train"] + roles["calibration"]
    combined = np.concatenate((
        features["train"]["current_raw16"],
        features["calibration"]["current_raw16"],
    ))
    labels = np.asarray([frame.target_acceptable for frame in combined_frames], dtype=bool)
    positive_indices = np.flatnonzero(labels)
    negative_indices = np.flatnonzero(~labels)
    closest: list[dict[str, Any]] = []
    distances: list[float] = []
    negatives = combined[negative_indices]
    for positive_index in positive_indices:
        rms = np.sqrt(np.mean((negatives - combined[positive_index]) ** 2, axis=1))
        nearest_offset = int(np.argmin(rms))
        negative_index = int(negative_indices[nearest_offset])
        distance = float(rms[nearest_offset])
        distances.append(distance)
        closest.append({
            "rms": distance,
            "positiveFamily": combined_frames[int(positive_index)].family,
            "positiveFrame": combined_frames[int(positive_index)].index,
            "negativeFamily": combined_frames[negative_index].family,
            "negativeFrame": combined_frames[negative_index].index,
        })
    closest.sort(key=lambda item: item["rms"])
    result["oppositeLabelNearestNeighbor"] = {
        "positiveFrames": len(positive_indices),
        "rmsDistribution": distribution(np.asarray(distances)),
        "pairsBelow0_02": sum(value < 0.02 for value in distances),
        "closestPairs": closest[:12],
        "interpretation": "raw16 image-space diagnostic only; labels never enter policy input",
    }
    return result


def checkpoint_payload(
    candidate: dict[str, Any], manifest: dict[str, Any], seed: int,
    threshold: float | None, gate_pass: bool, reason: str,
) -> dict[str, Any]:
    width = len(candidate["mean"])
    return {
        "schema": "active2d-image-only-stop-v1",
        "version": 2,
        "contract": CONTRACT,
        "experimentId": EXPERIMENT_ID,
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": ["UnityAnatomyLabels", "probePose", "targetCoordinates", "finalTargetScore"],
        "architecture": {
            "type": "standardized-temporal-quality-stop-v1",
            "featureExtractor": "temporal-v3",
            "featureWidth": width,
            "featureMean": candidate["mean"].tolist(),
            "featureScale": candidate["scale"].tolist(),
            "qualityWeights": candidate["qualityWeights"].tolist(),
            "qualityBias": candidate["qualityBias"],
            "stopWeights": candidate["stopWeights"].tolist(),
            "stopBias": candidate["stopBias"],
        },
        "stopRule": {"threshold": threshold, "requiredConfirmations": 1},
        "deployment": {
            "gate": DEPLOYMENT_GATE,
            "status": "DEPLOYABLE" if gate_pass else "BLOCKED",
            "safeOperatingPoint": gate_pass,
            "reason": reason,
        },
        "training": {
            "taskId": "G2-R1",
            "seed": seed,
            "configuration": "temporal_v3_full",
            "manifestDatasetFingerprint": manifest["manifestDatasetFingerprint"],
            "thresholdSelectionSplit": "calibration",
            "lockedTestUsedForSelection": False,
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    roles = load_cases(manifest, args.source_root, {"train", "calibration"})
    if set(roles) != {"train", "calibration"}:
        raise RuntimeError("Training/calibration roles are incomplete")
    features = {role: build_features(frames) for role, frames in roles.items()}
    labels = {
        role: np.asarray([frame.target_acceptable for frame in frames], dtype=bool)
        for role, frames in roles.items()
    }
    quality_labels = {
        role: np.asarray([frame.target_score for frame in frames], dtype=np.float32)
        for role, frames in roles.items()
    }
    comparisons: list[dict[str, Any]] = []
    trained: dict[tuple[str, int], dict[str, Any]] = {}
    for feature_name in ("current_raw16", "temporal_images_no_action", "temporal_v3_full"):
        for seed in manifest["trainingBudget"]["candidateSeeds"]:
            candidate = fit_candidate(
                features["train"][feature_name],
                labels["train"].astype(np.int32),
                quality_labels["train"],
                seed=int(seed),
            )
            trained[(feature_name, int(seed))] = candidate
            cal_stop, cal_quality = predict(candidate, features["calibration"][feature_name])
            threshold, selection = select_threshold(labels["calibration"], cal_stop)
            evaluation_threshold = threshold if threshold is not None else float(
                selection["diagnosticFallbackThreshold"]
            )
            metrics = evaluate(
                roles["calibration"], cal_stop, cal_quality, evaluation_threshold
            )
            gate = manifest["predeclaredGate"]
            pass_gate = (
                threshold is not None
                and metrics["classification"]["recall"] >= gate["calibrationRecallMin"]
                and metrics["classification"]["falsePositive"] <= gate["calibrationFalsePositiveMax"]
                and metrics["wrongStopEpisodes"] <= gate["calibrationWrongStopEpisodesMax"]
                and metrics["qualitySpearman"] >= gate["qualitySpearmanMin"]
                and metrics["medianEpisodeBestViewRegret"] <= gate["medianEpisodeBestViewRegretMax"]
            )
            comparisons.append({
                "model": feature_name,
                "seed": int(seed),
                "featureWidth": int(features["train"][feature_name].shape[1]),
                "selectedThreshold": threshold,
                "diagnosticEvaluationThreshold": evaluation_threshold,
                "thresholdSelection": selection,
                "calibration": metrics,
                "calibrationGatePass": pass_gate,
            })

    baseline = json.loads(args.baseline.read_text(encoding="utf-8"))
    validate_stop_checkpoint(baseline, require_deployable=True)
    baseline_features = np.stack([
        np.concatenate((convolutional_features_v2(frame.image), action_vector(frame.issued_action)))
        for frame in roles["calibration"]
    ])
    baseline_stop = stop_probabilities(baseline, baseline_features)
    baseline_metrics = evaluate(
        roles["calibration"], baseline_stop, baseline_stop, float(baseline["stopRule"]["threshold"])
    )

    full_results = [item for item in comparisons if item["model"] == "temporal_v3_full"]
    passing_full = [item for item in full_results if item["calibrationGatePass"]]
    if passing_full:
        selected = max(passing_full, key=lambda item: (
            item["calibration"]["classification"]["recall"],
            -item["calibration"]["medianEpisodeBestViewRegret"],
            item["calibration"]["qualitySpearman"],
            -item["seed"],
        ))
        deployable = True
        reason = "predeclared-calibration-gate-pass"
    else:
        selected = max(full_results, key=lambda item: (
            -item["calibration"]["classification"]["falsePositive"],
            item["calibration"]["classification"]["recall"],
            -item["calibration"]["medianEpisodeBestViewRegret"],
            -item["seed"],
        ))
        deployable = False
        reason = "predeclared-calibration-gate-failed"

    selected_candidate = trained[("temporal_v3_full", selected["seed"])]
    checkpoint = checkpoint_payload(
        selected_candidate,
        manifest,
        selected["seed"],
        selected["selectedThreshold"] if deployable else None,
        deployable,
        reason,
    )
    validate_stop_checkpoint(checkpoint)

    locked_test: dict[str, Any] = {
        "status": "SEALED",
        "reason": "calibration gate failed; locked-test images and labels were not loaded",
    }
    if deployable:
        locked_roles = load_cases(manifest, args.source_root, {"locked_test"})
        locked_features = build_features(locked_roles["locked_test"])
        locked_stop, locked_quality = predict(
            selected_candidate, locked_features["temporal_v3_full"]
        )
        locked_test = {
            "status": "OPENED_ONCE_AFTER_SELECTION",
            "metrics": evaluate(
                locked_roles["locked_test"],
                locked_stop,
                locked_quality,
                float(selected["selectedThreshold"]),
            ),
        }

    report = {
        "schemaVersion": 1,
        "taskId": "G2-R1-F2B",
        "status": "OFFLINE_PASS" if deployable else "OFFLINE_BLOCKED",
        "manifest": args.manifest.as_posix(),
        "manifestSha256": sha256(args.manifest),
        "manifestDatasetFingerprint": manifest["manifestDatasetFingerprint"],
        "sourceImagesCopied": False,
        "sourceRootRecordedInReport": False,
        "dataDiagnosis": data_diagnosis(roles, features),
        "comparisons": comparisons,
        "frozenSpatialV2Calibration": {
            "checkpointSha256": sha256(args.baseline),
            "threshold": float(baseline["stopRule"]["threshold"]),
            "metrics": baseline_metrics,
        },
        "selectedTemporalV3": selected,
        "checkpointDeployable": deployable,
        "lockedTest": locked_test,
        "unityClosedLoopEligible": deployable and locked_test.get("status") == "OPENED_ONCE_AFTER_SELECTION",
        "limitations": [
            "Single simulated anatomy; no clinical or unseen-patient claim.",
            "Frame/episode recognition is not physical return-to-best success.",
            "Existing captures contain no rejected actions, so feedback robustness is established by deterministic tests only.",
        ],
    }
    args.output_directory.mkdir(parents=True, exist_ok=True)
    checkpoint_path = args.output_directory / "g2_r1_temporal_v3_candidate_v2.json"
    report_path = args.output_directory / "g2_r1_controlled_comparison.json"
    checkpoint_path.write_text(json.dumps(checkpoint, indent=2) + "\n", encoding="utf-8")
    report["checkpoint"] = checkpoint_path.name
    report["checkpointSha256"] = sha256(checkpoint_path)
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": report["status"],
        "checkpointDeployable": deployable,
        "lockedTest": locked_test["status"],
        "report": str(report_path),
    }, indent=2))


if __name__ == "__main__":
    main()
