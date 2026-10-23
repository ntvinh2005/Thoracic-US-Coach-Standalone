from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, model_probabilities, project_root, request_features, save_json
from protocol_v3 import validate_manifest, validate_model, verify_frozen_scenario
from unity_env import UnityFileEnvironment


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"


def summarize(episodes) -> dict[str, object]:
    recognition = [item for item in episodes if item["initiallyAcceptable"]]
    acquisition = [item for item in episodes if not item["initiallyAcceptable"]]
    return {
        "episodes": len(episodes),
        "recognitionSuccess": sum(item["success"] for item in recognition),
        "recognitionTotal": len(recognition),
        "recognitionRate": float(np.mean([item["success"] for item in recognition])),
        "acquisitionSuccess": sum(item["success"] for item in acquisition),
        "acquisitionTotal": len(acquisition),
        "acquisitionRate": float(np.mean([item["success"] for item in acquisition])),
        "pooledSuccess": sum(item["success"] for item in episodes),
        "wrongStopBeforeEntryAcquisition": sum(item["wrongStop"] and not item["regionEntered"] for item in acquisition),
        "wrongStopAfterEntryAcquisition": sum(item["wrongStop"] and item["regionEntered"] for item in acquisition),
        "wrongStopFirstDecisionAcquisition": sum(item["wrongStop"] and item["steps"] == 1 for item in acquisition),
        "timeoutsAcquisition": sum(item["timeout"] for item in acquisition),
        "constraintViolations": sum(item["constraintViolations"] for item in episodes),
    }


def run(environment, scenarios, model, threshold: float, max_steps: int):
    episodes = []
    for frozen in scenarios:
        exact = dict(frozen["scenario"])
        environment.reset(0, max_steps=max_steps, frozen_scenario=exact)
        verify_frozen_scenario(frozen, dict(environment.describe()["scenario"]))
        for start in frozen["starts"]:
            response = environment.reset(int(start["index"]), max_steps=max_steps)
            previous_image = None
            previous_action = 0
            region_entered = bool(response["regionEntered"])
            constraints = 0
            trace = []
            for step in range(max_steps):
                current = Path(str(response["imagePath"]))
                features = request_features(
                    current, previous_image, previous_action,
                    int(model.get("featureSize", 16)), str(model.get("observationMode", "current")),
                )
                probabilities = model_probabilities(model, features)
                stop_score = float(probabilities[ACTION_TO_INDEX[0]])
                movement = -1 if probabilities[ACTION_TO_INDEX[-1]] >= probabilities[ACTION_TO_INDEX[1]] else 1
                action = 0 if stop_score >= threshold else movement
                before_target = bool(response["targetAcceptable"])
                next_response = environment.step(action)
                trace.append({
                    "step": step,
                    "stopScore": stop_score,
                    "chosenAction": action,
                    "targetBefore": before_target,
                    "targetAfter": bool(next_response["targetAcceptable"]),
                    "distanceBeforeMm": float(response["distanceToAcceptableMm"]),
                    "distanceAfterMm": float(next_response["distanceToAcceptableMm"]),
                    "reward": float(next_response["reward"]),
                })
                constraints += int(bool(next_response["constraintViolation"]))
                region_entered |= bool(next_response["regionEntered"])
                previous_image, previous_action, response = current, action, next_response
                if bool(response["done"]):
                    break
            success = bool(response["explicitStopSuccess"])
            episodes.append({
                "scenarioIndex": int(frozen["scenarioIndex"]),
                "trajectorySeed": int(frozen["trajectorySeed"]),
                "startCondition": str(start["name"]),
                "startIndex": int(start["index"]),
                "initiallyAcceptable": bool(start["initiallyAcceptable"]),
                "success": success,
                "wrongStop": bool(response["terminated"]) and not success,
                "timeout": bool(response["truncated"]),
                "regionEntered": region_entered,
                "steps": len(trace),
                "constraintViolations": constraints,
                "trace": trace,
            })
    return {"summary": summarize(episodes), "episodes": episodes}


def selection_key(result) -> tuple[float, ...]:
    summary = result["summary"]
    eligible = summary["constraintViolations"] == 0 and summary["recognitionRate"] >= 0.95
    return (
        float(eligible),
        float(summary["acquisitionRate"]),
        float(summary["recognitionRate"]),
        float(summary["pooledSuccess"]),
        -float(summary["wrongStopBeforeEntryAcquisition"]),
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Separate live development calibration/check for STOP threshold")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--thresholds", default="0.35,0.45,0.55,0.65,0.75,0.85")
    parser.add_argument("--calibration-scenarios", type=int, default=6)
    parser.add_argument("--check-scenarios", type=int, default=6)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    validate_manifest(manifest, expected_group="development", selection_allowed=True)
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    validate_model(model)
    max_steps = int(manifest["protocol"]["episodeBudget"])
    calibration_scenarios = list(manifest["scenarios"])[: args.calibration_scenarios]
    check_scenarios = list(manifest["scenarios"])[args.calibration_scenarios : args.calibration_scenarios + args.check_scenarios]
    if not calibration_scenarios or not check_scenarios:
        raise SystemExit("Calibration/check scenario split is empty")
    thresholds = [float(value) for value in args.thresholds.split(",")]
    environment = UnityFileEnvironment(timeout=20.0)
    calibration = {}
    for threshold in thresholds:
        result = run(environment, calibration_scenarios, model, threshold, max_steps)
        calibration[str(threshold)] = result
        print(json.dumps({
            "phase": "DIAGNOSTIC",
            "split": "development-calibration",
            "threshold": threshold,
            "completedEpisodes": result["summary"]["episodes"],
            "summary": result["summary"],
        }), flush=True)
    selected_threshold = max(thresholds, key=lambda value: selection_key(calibration[str(value)]))
    selected_key = selection_key(calibration[str(selected_threshold)])
    check = run(environment, check_scenarios, model, selected_threshold, max_steps)
    print(json.dumps({
        "phase": "DIAGNOSTIC",
        "split": "development-check",
        "selectedThreshold": selected_threshold,
        "completedEpisodes": check["summary"]["episodes"],
        "summary": check["summary"],
    }), flush=True)
    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "heldoutOrStressAccessed": False,
        "sourceManifest": str(args.manifest.resolve()),
        "checkpoint": str(args.checkpoint.resolve()),
        "candidateThresholdsPredeclared": thresholds,
        "selectionRule": "constraint violations=0 AND recognition>=0.95; maximize acquisition, recognition, pooled success, then minimize premature STOP",
        "calibrationScenarioIdentities": [item["scenarioIdentitySha256"] for item in calibration_scenarios],
        "checkScenarioIdentities": [item["scenarioIdentitySha256"] for item in check_scenarios],
        "calibration": calibration,
        "selectedThreshold": selected_threshold,
        "selectedKey": list(selected_key),
        "check": check,
        "decisionRule": "STOP iff P(STOP)>=threshold; otherwise highest-probability non-STOP action",
        "weightedSoftmaxProbabilitiesTreatedAsScoresNotCalibratedProbabilities": True,
    }
    output = output_dir / "live_stop_threshold_calibration.json"
    save_json(output, report)
    print(json.dumps({"phase": "DIAGNOSTIC", "output": str(output)}), flush=True)


if __name__ == "__main__":
    main()
