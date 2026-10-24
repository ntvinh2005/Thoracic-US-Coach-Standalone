from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import numpy as np
from sklearn.linear_model import Ridge

from active2d_contract import ACTION_ORDER, CONTRACT, EXPERIMENT_ID
from active2d_stop import DEPLOYMENT_GATE, stop_probabilities, validate_stop_checkpoint
from run_g2_r1_comparison import (
    action_vector,
    build_features,
    data_diagnosis,
    evaluate,
    load_cases,
    sha256,
)


def spatial_features(frames) -> np.ndarray:
    from active2d_stop import convolutional_features_v2

    return np.stack([
        np.concatenate((
            convolutional_features_v2(frame.image),
            action_vector(
                frame.issued_action if frame.action_accepted and frame.action_completed else "RESET"
            ),
        ))
        for frame in frames
    ])


def build_hybrid_checkpoint(
    baseline: dict[str, Any], temporal_train: np.ndarray, quality_targets: np.ndarray,
    manifest: dict[str, Any], deployment_status: str, reason: str,
) -> dict[str, Any]:
    temporal_mean = temporal_train.mean(axis=0)
    temporal_scale = temporal_train.std(axis=0)
    temporal_scale[temporal_scale < 1e-5] = 1.0
    temporal_z = (temporal_train - temporal_mean) / temporal_scale
    quality = Ridge(alpha=5.0).fit(temporal_z, quality_targets)
    spatial = baseline["architecture"]
    spatial_width = int(spatial["featureWidth"])
    temporal_width = temporal_train.shape[1]
    safe = deployment_status == "DEPLOYABLE"
    return {
        "schema": "active2d-image-only-stop-v1",
        "version": 2,
        "contract": CONTRACT,
        "experimentId": EXPERIMENT_ID,
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": ["UnityAnatomyLabels", "probePose", "targetCoordinates", "finalTargetScore"],
        "architecture": {
            "type": "standardized-temporal-quality-stop-v1",
            "featureExtractor": "hybrid-spatial-v2+temporal-v3",
            "featureWidth": spatial_width + temporal_width,
            "featureMean": list(spatial["featureMean"]) + temporal_mean.tolist(),
            "featureScale": list(spatial["featureScale"]) + temporal_scale.tolist(),
            "stopWeights": list(spatial["weights"]) + [0.0] * temporal_width,
            "stopBias": float(spatial["bias"]),
            "qualityWeights": [0.0] * spatial_width + quality.coef_.astype(np.float32).tolist(),
            "qualityBias": float(quality.intercept_),
        },
        "stopRule": {
            "threshold": float(baseline["stopRule"]["threshold"]) if safe else None,
            "requiredConfirmations": 1,
        },
        "deployment": {
            "gate": DEPLOYMENT_GATE,
            "status": deployment_status,
            "safeOperatingPoint": safe,
            "reason": reason,
        },
        "training": {
            "taskId": "G2-R1-HYBRID-CONFIRMATION",
            "configuration": "frozen-spatial-v2-stop+temporal-v3-quality",
            "classifierTraining": "none; exact frozen spatial-v2 weights and threshold",
            "qualityTraining": "deterministic L2 ridge on train families only",
            "manifestDatasetFingerprint": manifest["manifestDatasetFingerprint"],
            "thresholdSelectionSplit": "none; frozen baseline threshold 0.93",
            "lockedTestUsedForSelection": False,
        },
    }


def gate_pass(metrics: dict[str, Any], gate: dict[str, Any]) -> bool:
    return (
        metrics["classification"]["recall"] >= gate["calibrationRecallMin"]
        and metrics["classification"]["falsePositive"] <= gate["calibrationFalsePositiveMax"]
        and metrics["wrongStopEpisodes"] <= gate["calibrationWrongStopEpisodesMax"]
        and metrics["qualitySpearman"] >= gate["qualitySpearmanMin"]
        and metrics["medianEpisodeBestViewRegret"] <= gate["medianEpisodeBestViewRegretMax"]
    )


def evaluate_role(
    checkpoint: dict[str, Any], frames, temporal: np.ndarray, spatial: np.ndarray,
) -> dict[str, Any]:
    combined = np.concatenate((spatial, temporal), axis=1)
    threshold = float(checkpoint["stopRule"]["threshold"])
    stop = stop_probabilities(checkpoint, combined)
    architecture = checkpoint["architecture"]
    mean = np.asarray(architecture["featureMean"], dtype=np.float32)
    scale = np.asarray(architecture["featureScale"], dtype=np.float32)
    weights = np.asarray(architecture["qualityWeights"], dtype=np.float32)
    quality = np.clip(
        ((combined - mean) / scale) @ weights + float(architecture["qualityBias"]), 0.0, 1.0
    )
    return evaluate(frames, stop, quality, threshold)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    if manifest["protocolId"] != "G2_R1_HYBRID_CONFIRMATION_V1":
        raise ValueError("Hybrid confirmation manifest protocol mismatch")
    roles = load_cases(manifest, args.source_root, {"train", "calibration"})
    features = {role: build_features(frames) for role, frames in roles.items()}
    baseline = json.loads(args.baseline.read_text(encoding="utf-8"))
    validate_stop_checkpoint(baseline, require_deployable=True)
    provisional = build_hybrid_checkpoint(
        baseline,
        features["train"]["temporal_v3_full"],
        np.asarray([frame.target_score for frame in roles["train"]]),
        manifest,
        "DEPLOYABLE",
        "provisional-for-frozen-calibration-evaluation",
    )
    validate_stop_checkpoint(provisional)
    calibration = evaluate_role(
        provisional,
        roles["calibration"],
        features["calibration"]["temporal_v3_full"],
        spatial_features(roles["calibration"]),
    )
    calibration_pass = gate_pass(calibration, manifest["predeclaredGate"])
    locked: dict[str, Any] = {
        "status": "SEALED",
        "reason": "confirmation calibration gate failed",
    }
    locked_pass = False
    if calibration_pass:
        locked_roles = load_cases(manifest, args.source_root, {"locked_test"})
        locked_features = build_features(locked_roles["locked_test"])
        locked_metrics = evaluate_role(
            provisional,
            locked_roles["locked_test"],
            locked_features["temporal_v3_full"],
            spatial_features(locked_roles["locked_test"]),
        )
        locked_pass = gate_pass(locked_metrics, manifest["predeclaredGate"])
        locked = {
            "status": "OPENED_ONCE_AFTER_CALIBRATION_PASS",
            "gatePass": locked_pass,
            "metrics": locked_metrics,
        }

    deployable = calibration_pass and locked_pass
    reason = (
        "calibration-and-locked-test-gates-pass"
        if deployable else
        "locked-test-gate-failed" if calibration_pass else
        "confirmation-calibration-gate-failed"
    )
    checkpoint = build_hybrid_checkpoint(
        baseline,
        features["train"]["temporal_v3_full"],
        np.asarray([frame.target_score for frame in roles["train"]]),
        manifest,
        "DEPLOYABLE" if deployable else "BLOCKED",
        reason,
    )
    validate_stop_checkpoint(checkpoint)
    args.output_directory.mkdir(parents=True, exist_ok=True)
    checkpoint_path = args.output_directory / "g2_r1_hybrid_candidate_v2.json"
    checkpoint_path.write_text(json.dumps(checkpoint, indent=2) + "\n", encoding="utf-8")
    report = {
        "schemaVersion": 1,
        "taskId": "G2-R1-HYBRID-CONFIRMATION",
        "status": "OFFLINE_PASS" if deployable else "OFFLINE_BLOCKED",
        "manifest": args.manifest.as_posix(),
        "manifestSha256": sha256(args.manifest),
        "manifestDatasetFingerprint": manifest["manifestDatasetFingerprint"],
        "candidate": "frozen spatial-v2 STOP + temporal-v3 quality ranking",
        "stopThreshold": float(baseline["stopRule"]["threshold"]),
        "thresholdTuned": False,
        "calibration": {"gatePass": calibration_pass, "metrics": calibration},
        "lockedTest": locked,
        "checkpoint": checkpoint_path.name,
        "checkpointSha256": sha256(checkpoint_path),
        "checkpointDeployable": deployable,
        "unityClosedLoopEligible": deployable,
        "trainCalibrationDiagnosis": data_diagnosis(roles, features),
        "limitations": [
            "One simulated anatomy; no clinical or unseen-patient claim.",
            "Offline recognition does not establish physical return-to-best success.",
            "Unity closed-loop remains required even after an offline pass.",
        ],
    }
    report_path = args.output_directory / "g2_r1_hybrid_confirmation.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": report["status"],
        "calibrationPass": calibration_pass,
        "lockedTest": locked["status"],
        "lockedPass": locked_pass,
        "checkpointDeployable": deployable,
        "report": str(report_path),
    }, indent=2))


if __name__ == "__main__":
    main()
