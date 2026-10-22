from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from coach_data import DEFAULT_EXPERIMENT_ID, project_root, save_json
from contract_v3 import CONTRACT_VERSION, oracle_action
from lock_scenario_manifest import verify_lock
from ppo_live_refine import regenerate, run_frozen_evaluation
from unity_env import UnityFileEnvironment


def run_teacher(environment, frozen, max_steps: int) -> dict[str, object]:
    episodes = []
    for frozen_scenario in frozen["scenarios"]:
        scenario_index = int(frozen_scenario["scenarioIndex"])
        trajectory_seed = int(frozen_scenario["trajectorySeed"])
        _, actual = regenerate(environment, scenario_index, trajectory_seed, max_steps)
        if actual["configurationSha256"] != frozen_scenario["configurationSha256"] or actual["labelsSha256"] != frozen_scenario["labelsSha256"]:
            raise RuntimeError(f"Frozen scenario identity mismatch for teacher scenario {scenario_index}")
        acceptable = tuple(index in set(int(value) for value in actual["acceptableIndices"]) for index in range(len(actual["pathMm"])))
        target_reachable = any(acceptable)
        for start in frozen_scenario["starts"]:
            response = environment.reset(int(start["index"]), max_steps=max_steps)
            boundary_failures = 0
            for step in range(max_steps):
                # Target-absent scenarios are not oracle-solvable and must never
                # manufacture a successful STOP. Exercise the time-limit path.
                action = oracle_action(acceptable, int(response["pathIndex"])) if target_reachable else 1
                response = environment.step(action)
                boundary_failures += int(bool(response.get("patchBoundaryViolation", False)))
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
                "regionEnteredWithoutCorrectStop": bool(response.get("regionEntered", False)) and not success,
                "wrongStop": False,
                "boundaryFailures": boundary_failures,
                "timeout": bool(response.get("truncated", False)),
                "terminated": bool(response.get("terminated", False)),
                "truncated": bool(response.get("truncated", False)),
                "constraintViolation": bool(response.get("constraintViolation", False)),
                "steps": step + 1,
            })
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
            "timeouts": sum(item["timeout"] for item in episodes),
            "constraintViolations": sum(item["constraintViolation"] for item in episodes),
            "targetAbsentTotal": len(target_absent),
            "targetAbsentWrongStop": sum(item["wrongStop"] for item in target_absent),
            "targetAbsentTimeouts": sum(item["timeout"] for item in target_absent),
        },
        "explicitStopSuccessRateReachable": rate(reachable, "explicitStopSuccess"),
        "recognitionRate": rate(recognition, "recognitionSuccess"),
        "acquisitionRate": rate(acquisition, "acquisitionSuccess"),
        "byFamily": {family: {"episodes": sum(item["family"] == family for item in episodes), "successes": sum(item["family"] == family and item["explicitStopSuccess"] for item in episodes)} for family in sorted({item["family"] for item in episodes})},
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Paired frozen-manifest Unity evaluation for contract v3")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--checkpoint", type=Path)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--lock", type=Path)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--include-teacher", action="store_true")
    parser.add_argument("--teacher-only", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.checkpoint is None and not args.teacher_only:
        parser.error("--checkpoint is required unless --teacher-only is used")
    if args.teacher_only and args.checkpoint is not None:
        parser.error("--teacher-only cannot be combined with --checkpoint")
    model = json.loads(args.checkpoint.read_text(encoding="utf-8")) if args.checkpoint else None
    frozen = json.loads(args.manifest.read_text(encoding="utf-8"))
    if model is not None and model.get("experimentId") != args.experiment_id:
        raise SystemExit("Checkpoint experiment does not match requested experiment")
    if frozen.get("contractVersion") != CONTRACT_VERSION:
        raise SystemExit("Frozen scenario manifest contract mismatch")
    group = str(frozen.get("group"))
    lock_result = None
    if group in ("heldout", "stress"):
        if args.lock is None:
            raise SystemExit("Heldout/stress evaluation requires --lock and executes byte verification")
        lock_result = verify_lock(args.manifest, args.lock)
    environment = UnityFileEnvironment(timeout=20.0)
    policy = None
    if model is not None:
        weights = np.asarray(model["weights"], dtype=np.float64)
        bias = np.asarray(model["bias"], dtype=np.float64)
        policy = run_frozen_evaluation(environment, frozen, model, weights, bias, args.max_steps)
    teacher = run_teacher(environment, frozen, args.max_steps) if args.include_teacher or args.teacher_only else None
    report = {
        "schemaVersion": 3,
        "experimentId": args.experiment_id,
        "contractVersion": CONTRACT_VERSION,
        "checkpoint": str(args.checkpoint) if args.checkpoint else None,
        "model": model.get("model") if model else None,
        "scenarioManifest": str(args.manifest),
        "scenarioManifestIdentity": frozen.get("manifestSha256"),
        "evaluationGroup": group,
        "lockVerification": lock_result,
        "definition": "Success requires model-selected explicit STOP at an exact fixed-pose target with valid contact and workspace; termination and time-limit truncation are distinct.",
        "policyInputAudit": f"{model.get('observationMode', 'current')} clean ultrasound only; previous action means actually executed action" if model else None,
        "policy": policy,
        "teacherOracle": teacher,
        "finalTestWasNotUsedForModelSelection": bool(lock_result) if group in ("heldout", "stress") else None,
    }
    save_json(args.output, report)
    print(json.dumps({"output": str(args.output), "group": group, "policy": policy["rawCounts"] if policy else None, "teacher": teacher["rawCounts"] if teacher else None, "lockVerification": lock_result}, indent=2))


if __name__ == "__main__":
    main()
