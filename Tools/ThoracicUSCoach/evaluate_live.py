from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, DEFAULT_EXPERIMENT_ID, model_probabilities, request_features, project_root, save_json
from contract_v3 import CONTRACT_VERSION, oracle_action
from lock_scenario_manifest import verify_lock
from ppo_live_refine import run_frozen_evaluation
from protocol_v3 import validate_manifest, validate_model, verify_frozen_scenario
from unity_env import UnityFileEnvironment


def summarize_episodes(episodes: list[dict[str, object]]) -> dict[str, object]:
    reachable = [item for item in episodes if item["targetReachable"]]
    recognition = [item for item in reachable if item["initiallyAcceptable"]]
    acquisition = [item for item in reachable if not item["initiallyAcceptable"]]
    target_absent = [item for item in episodes if item["targetAbsent"]]

    def rate(items, key):
        return float(np.mean([bool(item[key]) for item in items])) if items else None

    return {
        "episodes": episodes,
        "rawCounts": {
            "episodes": len(episodes),
            "reachable": len(reachable),
            "successes": sum(item["explicitStopSuccess"] for item in reachable),
            "recognitionSuccess": sum(item["recognitionSuccess"] for item in recognition),
            "recognitionTotal": len(recognition),
            "acquisitionSuccess": sum(item["acquisitionSuccess"] for item in acquisition),
            "acquisitionTotal": len(acquisition),
            "wrongStop": sum(item["wrongStop"] for item in episodes),
            "regionEnteredWithoutCorrectStop": sum(item["regionEnteredWithoutCorrectStop"] for item in episodes),
            "boundaryFailures": sum(item["boundaryFailures"] for item in episodes),
            "invalidActions": sum(item["invalidActions"] for item in episodes),
            "timeouts": sum(item["timeout"] for item in episodes),
            "contactViolations": sum(item["contactViolations"] for item in episodes),
            "workspaceViolations": sum(item["workspaceViolations"] for item in episodes),
            "constraintViolations": sum(item["constraintViolations"] for item in episodes),
            "targetAbsentTotal": len(target_absent),
            "targetAbsentWrongStop": sum(item["wrongStop"] for item in target_absent),
            "targetAbsentTimeouts": sum(item["timeout"] for item in target_absent),
        },
        "explicitStopSuccessRateReachable": rate(reachable, "explicitStopSuccess"),
        "recognitionRate": rate(recognition, "recognitionSuccess"),
        "acquisitionRate": rate(acquisition, "acquisitionSuccess"),
        "byFamily": {
            family: {
                "episodes": sum(item["family"] == family for item in episodes),
                "successes": sum(item["family"] == family and item["explicitStopSuccess"] for item in episodes),
            }
            for family in sorted({item["family"] for item in episodes})
        },
    }


def run_search_baseline(environment, frozen, max_steps: int, mode: str, stop_model=None) -> dict[str, object]:
    """Always-STOP or bounded scan with an image-only STOP detector."""
    episodes = []
    for frozen_scenario in frozen["scenarios"]:
        exact_scenario = dict(frozen_scenario["scenario"])
        environment.reset(0, max_steps=max_steps, frozen_scenario=exact_scenario)
        actual = dict(environment.describe()["scenario"])
        verify_frozen_scenario(frozen_scenario, actual)
        target_reachable = bool(actual["acceptableIndices"])
        for start in frozen_scenario["starts"]:
            response = environment.reset(int(start["index"]), max_steps=max_steps)
            direction = 1
            wrong_stop_count = boundary_failures = invalid_actions = 0
            contact_violations = workspace_violations = constraint_violations = 0
            boundary_streak = longest_boundary_streak = 0
            region_entered = bool(response.get("regionEntered", False))
            travelled_distance_mm = 0.0
            inference_ms = []
            for step in range(max_steps):
                if mode == "always-stop":
                    action = 0
                else:
                    started = time.perf_counter()
                    features = request_features(
                        Path(str(response["imagePath"])), None, 0,
                        int(stop_model.get("featureSize", 16)), "current",
                    )
                    probabilities = model_probabilities(stop_model, features)
                    action = 0 if int(np.argmax(probabilities)) == ACTION_TO_INDEX[0] else direction
                    inference_ms.append((time.perf_counter() - started) * 1000.0)
                previous_index = int(response.get("pathIndex", 0))
                response = environment.step(action)
                travelled_distance_mm += abs(int(response.get("pathIndex", 0)) - previous_index) * float(response.get("pathSpacingMm", 0.0))
                boundary = bool(response.get("patchBoundaryViolation", False))
                boundary_failures += int(boundary)
                invalid_actions += int(bool(response.get("invalidMotion", False)))
                if boundary:
                    boundary_streak += 1
                    longest_boundary_streak = max(longest_boundary_streak, boundary_streak)
                    if mode == "scan-stop":
                        direction *= -1
                else:
                    boundary_streak = 0
                wrong_stop_count += int(action == 0 and not bool(response.get("explicitStopSuccess", False)))
                contact_violations += int(not bool(response.get("contactValid", False)))
                workspace_violations += int(not bool(response.get("workspaceValid", False)))
                constraint_violations += int(bool(response.get("constraintViolation", False)))
                region_entered |= bool(response.get("regionEntered", False))
                if bool(response["done"]):
                    break
            initially_acceptable = bool(start["initiallyAcceptable"])
            success = bool(response.get("explicitStopSuccess", False))
            episodes.append({
                "scenarioIndex": int(frozen_scenario["scenarioIndex"]),
                "trajectorySeed": int(frozen_scenario["trajectorySeed"]),
                "family": frozen_scenario["configurationFamily"],
                "startCondition": start["name"],
                "startIndex": int(start["index"]),
                "initiallyAcceptable": initially_acceptable,
                "targetReachable": target_reachable,
                "targetAbsent": not target_reachable,
                "recognitionSuccess": target_reachable and initially_acceptable and success,
                "acquisitionSuccess": target_reachable and not initially_acceptable and success,
                "explicitStopSuccess": success,
                "regionEnteredWithoutCorrectStop": region_entered and not success,
                "wrongStop": wrong_stop_count > 0,
                "wrongStopCount": wrong_stop_count,
                "boundaryFailures": boundary_failures,
                "invalidActions": invalid_actions,
                "longestBoundaryStuckStreak": longest_boundary_streak,
                "contactViolations": contact_violations,
                "workspaceViolations": workspace_violations,
                "constraintViolations": constraint_violations,
                "travelledDistanceMm": travelled_distance_mm,
                "policyLatencyMsMean": float(np.mean(inference_ms)) if inference_ms else 0.0,
                "timeout": bool(response.get("truncated", False)),
                "terminated": bool(response.get("terminated", False)),
                "truncated": bool(response.get("truncated", False)),
                "steps": step + 1,
            })
    return summarize_episodes(episodes)


def run_teacher(environment, frozen, max_steps: int) -> dict[str, object]:
    episodes = []
    for frozen_scenario in frozen["scenarios"]:
        scenario_index = int(frozen_scenario["scenarioIndex"])
        trajectory_seed = int(frozen_scenario["trajectorySeed"])
        exact_scenario = dict(frozen_scenario["scenario"])
        environment.reset(0, max_steps=max_steps, frozen_scenario=exact_scenario)
        actual = dict(environment.describe()["scenario"])
        verify_frozen_scenario(frozen_scenario, actual)
        acceptable = tuple(index in set(int(value) for value in actual["acceptableIndices"]) for index in range(len(actual["pathMm"])))
        target_reachable = any(acceptable)
        step_count = int(actual.get("actionStepCount", 1))
        for start in frozen_scenario["starts"]:
            response = environment.reset(int(start["index"]), max_steps=max_steps)
            boundary_failures = invalid_actions = 0
            contact_violations = workspace_violations = constraint_violations = 0
            wrong_stop_count = 0
            boundary_streak = longest_boundary_streak = 0
            region_entered = bool(response.get("regionEntered", False))
            travelled_distance_mm = 0.0
            for step in range(max_steps):
                # Target-absent scenarios are not oracle-solvable and must never
                # manufacture a successful STOP. Exercise the time-limit path.
                action = oracle_action(acceptable, int(response["pathIndex"]), step_count) if target_reachable else 1
                previous_index = int(response.get("pathIndex", 0))
                response = environment.step(action)
                travelled_distance_mm += abs(int(response.get("pathIndex", 0)) - previous_index) * float(response.get("pathSpacingMm", 0.0))
                boundary = bool(response.get("patchBoundaryViolation", False))
                boundary_failures += int(boundary)
                invalid_actions += int(bool(response.get("invalidMotion", False)))
                if boundary:
                    boundary_streak += 1
                    longest_boundary_streak = max(longest_boundary_streak, boundary_streak)
                else:
                    boundary_streak = 0
                wrong_stop_count += int(action == 0 and not bool(response.get("explicitStopSuccess", False)))
                contact_violations += int(not bool(response.get("contactValid", False)))
                workspace_violations += int(not bool(response.get("workspaceValid", False)))
                constraint_violations += int(bool(response.get("constraintViolation", False)))
                region_entered |= bool(response.get("regionEntered", False))
                if bool(response["done"]):
                    break
            initially_acceptable = bool(start["initiallyAcceptable"])
            success = bool(response.get("explicitStopSuccess", False))
            episodes.append({
                "scenarioIndex": scenario_index,
                "trajectorySeed": trajectory_seed,
                "family": frozen_scenario["configurationFamily"],
                "startCondition": start["name"],
                "startIndex": int(start["index"]),
                "initiallyAcceptable": initially_acceptable,
                "targetReachable": target_reachable,
                "targetAbsent": not target_reachable,
                "recognitionSuccess": target_reachable and initially_acceptable and success,
                "acquisitionSuccess": target_reachable and (not initially_acceptable) and success,
                "explicitStopSuccess": success,
                "regionEnteredWithoutCorrectStop": region_entered and not success,
                "wrongStop": wrong_stop_count > 0,
                "wrongStopCount": wrong_stop_count,
                "boundaryFailures": boundary_failures,
                "invalidActions": invalid_actions,
                "longestBoundaryStuckStreak": longest_boundary_streak,
                "contactViolations": contact_violations,
                "workspaceViolations": workspace_violations,
                "constraintViolations": constraint_violations,
                "travelledDistanceMm": travelled_distance_mm,
                "timeout": bool(response.get("truncated", False)),
                "terminated": bool(response.get("terminated", False)),
                "truncated": bool(response.get("truncated", False)),
                "steps": step + 1,
            })
    return summarize_episodes(episodes)


def main() -> None:
    parser = argparse.ArgumentParser(description="Paired frozen-manifest Unity evaluation for contract v3")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--checkpoint", type=Path)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--lock", type=Path)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--include-teacher", action="store_true")
    parser.add_argument("--teacher-only", action="store_true")
    parser.add_argument("--baseline", choices=("always-stop", "scan-stop"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.checkpoint is None and not args.teacher_only and args.baseline != "always-stop":
        parser.error("--checkpoint is required unless --teacher-only is used")
    if args.teacher_only and args.checkpoint is not None:
        parser.error("--teacher-only cannot be combined with --checkpoint")
    model = json.loads(args.checkpoint.read_text(encoding="utf-8")) if args.checkpoint else None
    frozen = json.loads(args.manifest.read_text(encoding="utf-8"))
    if model is not None and model.get("experimentId") != args.experiment_id:
        raise SystemExit("Checkpoint experiment does not match requested experiment")
    if model is not None:
        expected_width = int(model.get("featureSize", 16)) ** 2
        if str(model.get("observationMode")) == "history":
            expected_width = expected_width * 2 + 3
        validate_model(model, expected_width)
    if args.baseline == "scan-stop" and str(model.get("observationMode")) != "current":
        raise SystemExit("scan-stop requires a current-frame checkpoint; it uses only the image STOP detector")
    if frozen.get("contractVersion") != CONTRACT_VERSION:
        raise SystemExit("Frozen scenario manifest contract mismatch")
    group = str(frozen.get("group"))
    validate_manifest(frozen, expected_group=group, selection_allowed=(group == "development"))
    lock_result = None
    if group in ("heldout", "stress"):
        if args.lock is None:
            raise SystemExit("Heldout/stress evaluation requires --lock and executes byte verification")
        lock_result = verify_lock(args.manifest, args.lock)
    environment = UnityFileEnvironment(timeout=20.0)
    policy = None
    if args.baseline:
        policy = run_search_baseline(environment, frozen, args.max_steps, args.baseline, model)
    elif model is not None:
        linear = str(model.get("architecture") or "linear-softmax-v1") == "linear-softmax-v1"
        weights = np.asarray(model["weights"], dtype=np.float64) if linear else None
        bias = np.asarray(model["bias"], dtype=np.float64) if linear else None
        policy = run_frozen_evaluation(environment, frozen, model, weights, bias, args.max_steps)
    teacher = run_teacher(environment, frozen, args.max_steps) if args.include_teacher or args.teacher_only else None
    report = {
        "schemaVersion": 3,
        "experimentId": args.experiment_id,
        "contractVersion": CONTRACT_VERSION,
        "checkpoint": str(args.checkpoint) if args.checkpoint else None,
        "model": model.get("model") if model else None,
        "baseline": args.baseline,
        "scenarioManifest": str(args.manifest),
        "scenarioManifestIdentity": frozen.get("manifestSha256"),
        "evaluationGroup": group,
        "lockVerification": lock_result,
        "definition": "Success requires model-selected explicit STOP at an exact fixed-pose target with valid contact and workspace; termination and time-limit truncation are distinct.",
        "policyInputAudit": (
            "constant explicit STOP; no observation" if args.baseline == "always-stop"
            else "bounded canonical scan, reverses only after observed clamp, and current-image model used only as a STOP detector" if args.baseline == "scan-stop"
            else f"{model.get('observationMode', 'current')} clean ultrasound only; previous action means actually executed action" if model else None
        ),
        "policy": policy,
        "teacherOracle": teacher,
        "selectionUse": "allowed" if group == "development" else "prohibited-final-only",
        "provenance": {
            "lockVerified": bool(lock_result),
            "claim": "Integrity verification only; non-use for selection is enforced by workflow and recorded candidate provenance, not inferred from the lock.",
        },
    }
    save_json(args.output, report)
    print(json.dumps({"output": str(args.output), "group": group, "policy": policy["rawCounts"] if policy else None, "teacher": teacher["rawCounts"] if teacher else None, "lockVerification": lock_result}, indent=2))


if __name__ == "__main__":
    main()
