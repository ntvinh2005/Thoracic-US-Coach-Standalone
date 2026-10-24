from __future__ import annotations

import argparse
import json
import math
import time
from collections import Counter
from pathlib import Path

import numpy as np

from active2d_action import (
    ALLOWED_ACTIONS,
    baseline_predictions,
    build_action_transitions,
    factual_predictions,
    file_sha256,
    fit_random_relu_ridge,
    regression_metrics,
    relative_gain,
    save_json,
    transition_features,
    transition_fingerprint,
    validate_u08_manifest,
)
from active2d_contract import project_root
from active2d_data import load_active2d_frames


def main() -> None:
    parser = argparse.ArgumentParser(description="Train bounded image-only Phase-2 factual action-response heads")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    manifest_path = args.manifest.resolve()
    data_root = args.data_root.resolve()
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    validate_u08_manifest(manifest)
    dataset_run_id = str(manifest["datasetRunId"])
    dataset_path = data_root / "Artifacts/ThoracicUSCoach/active2d_dataset"
    if not dataset_path.is_dir():
        raise SystemExit(f"U08 data root has no Active2D dataset: {dataset_path}")
    stop = manifest["frozenStopBaseline"]
    stop_path = data_root / str(stop["path"])
    if not stop_path.is_file() or file_sha256(stop_path) != str(stop["sha256"]):
        raise SystemExit("Frozen STOP baseline path/hash mismatch; U08 will not retrain or replace it")

    output_dir = (args.output_dir or (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/u08_action_selector"
    )).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()

    # Heldout test manifests and images are not loaded by this process.
    development_frames = load_active2d_frames(
        dataset_run_id, root=data_root, splits={"train", "dev"}
    )
    transitions = build_action_transitions(development_frames)
    train_rows = [row for row in transitions if row.split == "train"]
    dev_rows = [row for row in transitions if row.split == "dev"]
    if not train_rows or not dev_rows:
        raise SystemExit("U08 requires non-empty train and dev factual transitions")
    if {row.family for row in train_rows} & {row.family for row in dev_rows}:
        raise SystemExit("U08 train/dev family leakage")
    for split_name, rows in (("train", train_rows), ("dev", dev_rows)):
        counts = Counter(row.action for row in rows)
        if set(counts) != set(ALLOWED_ACTIONS) or min(counts.values()) < 100:
            raise SystemExit(f"U08 {split_name} lacks action support: {dict(counts)}")

    selection_fingerprint = transition_fingerprint(transitions)
    expected_fingerprint = str(manifest.get("expectedDevelopmentTransitionFingerprint") or "")
    if expected_fingerprint and selection_fingerprint != expected_fingerprint:
        raise SystemExit("U08 development transition fingerprint mismatch")

    candidates: list[dict[str, object]] = []
    feature_cache: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for configuration in manifest["configurations"]:
        feature_mode = str(configuration["featureMode"])
        train_x = transition_features(train_rows, feature_mode)
        dev_x = transition_features(dev_rows, feature_mode)
        feature_cache[feature_mode] = (train_x, dev_x)
        baseline = baseline_predictions(
            train_rows, train_x, dev_rows, dev_x, ridge=float(configuration["ridge"])
        )
        baseline_metrics = {
            name: regression_metrics(dev_rows, predictions)
            for name, predictions in baseline.items()
        }
        for seed in configuration["seeds"]:
            model = fit_random_relu_ridge(
                train_x,
                train_rows,
                feature_mode=feature_mode,
                hidden_size=int(configuration["hiddenSize"]),
                ridge=float(configuration["ridge"]),
                seed=int(seed),
            )
            model.update({
                "taskId": "U08",
                "protocolId": manifest["protocolId"],
                "datasetRunId": dataset_run_id,
                "developmentTransitionFingerprint": selection_fingerprint,
                "manifestSha256": file_sha256(manifest_path),
                "frozenStopBaseline": stop,
                "training": {
                    "configurationId": configuration["id"],
                    "seed": int(seed),
                    "label": "privileged finalTargetScore delta, training/evaluation only",
                    "factualTransitionsOnly": True,
                    "actionBalancing": "separate per-action ridge heads",
                    "selectionSplits": ["train", "dev"],
                    "lockedTestLoaded": False,
                    "closedLoopClaim": False,
                },
            })
            dev_predictions = factual_predictions(model, dev_x, dev_rows)
            metrics = regression_metrics(dev_rows, dev_predictions)
            best_baseline_rmse = min(
                float(baseline_metrics["actionPrior"]["rmse"]),
                float(baseline_metrics["imageAgnostic"]["rmse"]),
            )
            gain = relative_gain(float(metrics["rmse"]), best_baseline_rmse)
            per_action_non_worse = all(
                float(metrics["byAction"][action]["rmse"])
                <= float(baseline_metrics["actionPrior"]["byAction"][action]["rmse"])
                for action in ALLOWED_ACTIONS
            )
            passed = (
                math.isfinite(float(metrics["rmse"]))
                and gain >= float(manifest["minimumDevRelativeRmseGain"])
                and per_action_non_worse
            )
            checkpoint_path = output_dir / f"{configuration['id']}_seed{seed}.json"
            save_json(checkpoint_path, model)
            candidates.append({
                "configurationId": configuration["id"],
                "featureMode": feature_mode,
                "seed": int(seed),
                "checkpoint": str(checkpoint_path),
                "checkpointSha256": file_sha256(checkpoint_path),
                "dev": metrics,
                "baselines": baseline_metrics,
                "relativeRmseGainOverBestBaseline": gain,
                "perActionNonWorseThanActionPrior": per_action_non_worse,
                "developmentGatePass": passed,
            })

    selected = min(
        candidates,
        key=lambda item: (
            float(item["dev"]["rmse"]),
            float(item["dev"]["mae"]),
            str(item["configurationId"]),
            int(item["seed"]),
        ),
    )
    selected_source = Path(str(selected["checkpoint"]))
    selected_checkpoint = json.loads(selected_source.read_text(encoding="utf-8"))
    selected_checkpoint["selection"] = {
        "selectedOn": "development factual-transition metrics only",
        "configurationId": selected["configurationId"],
        "seed": selected["seed"],
        "developmentGatePass": selected["developmentGatePass"],
        "lockedTestLoaded": False,
    }
    selected_path = output_dir / "u08_selected_action_response.json"
    save_json(selected_path, selected_checkpoint)
    report = {
        "schemaVersion": 1,
        "taskId": "U08",
        "protocolId": manifest["protocolId"],
        "status": "TRAINED_DEV_PASS" if selected["developmentGatePass"] else "NO_IMPROVEMENT",
        "interpretation": (
            "Offline factual transition-response screening only; this does not demonstrate closed-loop acquisition improvement."
        ),
        "manifest": str(manifest_path),
        "manifestSha256": file_sha256(manifest_path),
        "dataRoot": str(data_root),
        "datasetRunId": dataset_run_id,
        "developmentTransitionFingerprint": selection_fingerprint,
        "frozenStopBaseline": stop,
        "counts": {
            "train": len(train_rows),
            "dev": len(dev_rows),
            "trainFamilies": len({row.family for row in train_rows}),
            "devFamilies": len({row.family for row in dev_rows}),
            "trainActions": dict(Counter(row.action for row in train_rows)),
            "devActions": dict(Counter(row.action for row in dev_rows)),
        },
        "candidates": candidates,
        "selected": {
            **selected,
            "checkpoint": str(selected_path),
            "checkpointSha256": file_sha256(selected_path),
        },
        "lockedTestUsedForSelection": False,
        "elapsedSeconds": time.perf_counter() - started,
    }
    report_path = output_dir / "u08_development_report.json"
    save_json(report_path, report)
    print(json.dumps({
        "status": report["status"],
        "report": str(report_path),
        "selectedCheckpoint": str(selected_path),
        "selected": report["selected"],
        "elapsedSeconds": report["elapsedSeconds"],
    }, indent=2))


if __name__ == "__main__":
    main()
