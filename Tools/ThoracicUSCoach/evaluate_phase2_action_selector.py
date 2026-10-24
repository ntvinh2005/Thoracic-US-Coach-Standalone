from __future__ import annotations

import argparse
import json
import math
from collections import Counter
from pathlib import Path

from active2d_action import (
    ALLOWED_ACTIONS,
    baseline_predictions,
    build_action_transitions,
    factual_predictions,
    file_sha256,
    relative_gain,
    regression_metrics,
    save_json,
    transition_features,
    transition_fingerprint,
    validate_action_model,
    validate_u08_manifest,
)
from active2d_contract import project_root
from active2d_data import load_active2d_frames


def main() -> None:
    parser = argparse.ArgumentParser(description="Independent locked-test evaluation for U08 action response")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--development-report", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    manifest_path = args.manifest.resolve()
    data_root = args.data_root.resolve()
    checkpoint_path = args.checkpoint.resolve()
    development_path = args.development_report.resolve()
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    validate_u08_manifest(manifest)
    checkpoint = json.loads(checkpoint_path.read_text(encoding="utf-8"))
    validate_action_model(checkpoint)
    development = json.loads(development_path.read_text(encoding="utf-8"))
    if checkpoint.get("manifestSha256") != file_sha256(manifest_path):
        raise SystemExit("Selected U08 checkpoint is not bound to the frozen manifest")
    selected = development.get("selected") or {}
    if selected.get("checkpointSha256") != file_sha256(checkpoint_path):
        raise SystemExit("Development report selected-checkpoint hash mismatch")
    if development.get("lockedTestUsedForSelection") is not False:
        raise SystemExit("U08 development report does not prove locked-test isolation")
    if checkpoint.get("datasetRunId") != manifest.get("datasetRunId"):
        raise SystemExit("U08 checkpoint dataset identity mismatch")

    dataset_run_id = str(manifest["datasetRunId"])
    selection_frames = load_active2d_frames(
        dataset_run_id, root=data_root, splits={"train", "dev"}
    )
    selection_rows = build_action_transitions(selection_frames)
    if transition_fingerprint(selection_rows) != checkpoint.get("developmentTransitionFingerprint"):
        raise SystemExit("U08 development transition fingerprint mismatch")
    train_rows = [row for row in selection_rows if row.split == "train"]

    # This is the first point at which locked test manifests/images are loaded.
    test_frames = load_active2d_frames(dataset_run_id, root=data_root, splits={"test"})
    test_rows = build_action_transitions(test_frames)
    if not test_rows or any(row.split != "test" for row in test_rows):
        raise SystemExit("U08 locked test transitions are missing or mixed")
    selection_families = {row.family for row in selection_rows}
    test_families = {row.family for row in test_rows}
    overlap = selection_families & test_families
    if overlap:
        raise SystemExit(f"U08 locked test family leakage: {sorted(overlap)[:5]}")

    feature_mode = str(checkpoint["architecture"]["featureMode"])
    train_x = transition_features(train_rows, feature_mode)
    test_x = transition_features(test_rows, feature_mode)
    predictions = factual_predictions(checkpoint, test_x, test_rows)
    metrics = regression_metrics(test_rows, predictions)
    ridge = float(checkpoint["architecture"]["ridge"])
    baselines = baseline_predictions(train_rows, train_x, test_rows, test_x, ridge=ridge)
    baseline_metrics = {
        name: regression_metrics(test_rows, values) for name, values in baselines.items()
    }
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
    heldout_pass = (
        math.isfinite(float(metrics["rmse"]))
        and gain >= float(manifest["minimumTestRelativeRmseGain"])
        and per_action_non_worse
    )
    development_pass = development.get("status") == "TRAINED_DEV_PASS"
    status = "TRAINED_DEV_PASS" if development_pass and heldout_pass else "NO_IMPROVEMENT"
    report = {
        "schemaVersion": 1,
        "taskId": "U08",
        "protocolId": manifest["protocolId"],
        "status": status,
        "interpretation": (
            "Locked factual-transition prediction only. Even a pass does not establish closed-loop acquisition improvement."
        ),
        "manifest": str(manifest_path),
        "manifestSha256": file_sha256(manifest_path),
        "checkpoint": str(checkpoint_path),
        "checkpointSha256": file_sha256(checkpoint_path),
        "developmentReport": str(development_path),
        "developmentReportSha256": file_sha256(development_path),
        "developmentStatus": development.get("status"),
        "datasetRunId": dataset_run_id,
        "lockedTestTransitionFingerprint": transition_fingerprint(test_rows),
        "lockedTestFamilies": len(test_families),
        "lockedTestRows": len(test_rows),
        "lockedTestActions": dict(Counter(row.action for row in test_rows)),
        "metrics": metrics,
        "baselines": baseline_metrics,
        "relativeRmseGainOverBestBaseline": gain,
        "perActionNonWorseThanActionPrior": per_action_non_worse,
        "heldoutPredictiveGatePass": heldout_pass,
        "closedLoopAcquisitionEvaluated": False,
        "livePolicyIntegrated": False,
        "frozenStopBaseline": manifest["frozenStopBaseline"],
    }
    output = (args.output or (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/u08_action_selector/u08_locked_test_report.json"
    )).resolve()
    save_json(output, report)
    print(json.dumps({"status": status, "output": str(output), **report}, indent=2))


if __name__ == "__main__":
    main()
