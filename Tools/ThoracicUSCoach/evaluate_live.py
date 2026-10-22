from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import DEFAULT_EXPERIMENT_ID, INDEX_TO_ACTION, project_root, request_features, save_json, stable_softmax
from unity_env import UnityFileEnvironment


def proportion_ci(successes: int, total: int) -> list[float] | None:
    if total == 0:
        return None
    z = 1.96
    p = successes / total
    denominator = 1 + z * z / total
    center = (p + z * z / (2 * total)) / denominator
    margin = z * np.sqrt(p * (1 - p) / total + z * z / (4 * total * total)) / denominator
    return [float(max(0, center - margin)), float(min(1, center + margin))]


def inspect_scenario(environment: UnityFileEnvironment, scenario: int) -> tuple[dict[str, object], list[int]]:
    first = environment.reset(0, scenario_index=scenario, regenerate_trajectory=True)
    count = int(first["pathCount"])
    acceptable = []
    for index in range(count):
        response = first if index == 0 else environment.reset(index)
        if bool(response["targetAcceptable"]):
            acceptable.append(index)
    return first, acceptable


def initial_conditions(path_count: int, acceptable: list[int]) -> dict[str, int]:
    result = {"before": 0, "after": path_count - 1}
    if acceptable:
        first, last = acceptable[0], acceptable[-1]
        result["inside"] = acceptable[len(acceptable) // 2]
        result["near-boundary"] = first - 1 if first > 0 else min(path_count - 1, last + 1)
        result["far"] = 0 if first > path_count - 1 - last else path_count - 1
    return result


def run_episode(environment, start_index, acceptable, model, weights, bias, max_steps, teacher=False):
    response = environment.reset(start_index)
    previous_image = None
    previous_action = 0
    last_nonstop = 0
    reversals = 0
    policy_times = []
    loop_times = []
    violations = 0
    invalid_motions = 0
    for step in range(max_steps):
        loop_started = time.perf_counter()
        if teacher:
            index = int(response["pathIndex"])
            if index in acceptable:
                action = 0
            elif acceptable:
                nearest = min(acceptable, key=lambda target: abs(target - index))
                action = 1 if nearest > index else -1
            else:
                action = 0
            policy_times.append(0.0)
        else:
            current = Path(str(response["imagePath"]))
            inference_started = time.perf_counter()
            features = request_features(current, previous_image, previous_action, int(model.get("featureSize", 16)), str(model.get("observationMode", "current")))
            probabilities = stable_softmax(features @ weights + bias)
            action = int(INDEX_TO_ACTION[int(np.argmax(probabilities))])
            policy_times.append((time.perf_counter() - inference_started) * 1000.0)
            previous_image = current
            previous_action = action
        if action and last_nonstop and action != last_nonstop:
            reversals += 1
        if action:
            last_nonstop = action
        response = environment.step(action)
        loop_times.append((time.perf_counter() - loop_started) * 1000.0)
        violations += int(bool(response.get("constraintViolation", False)))
        invalid_motions += int(bool(response.get("invalidMotion", False)))
        if bool(response["done"]):
            break
    return {
        "steps": step + 1,
        "explicitStop": bool(response["done"]),
        "explicitStopInRegion": bool(response.get("explicitStopSuccess", False)),
        "regionEntered": bool(response.get("regionEntered", False)),
        "stoppingErrorToAcceptableRangeMm": float(response.get("distanceToAcceptableMm", 0.0)),
        "timeout": not bool(response["done"]),
        "reversals": reversals,
        "contactViolations": violations,
        "invalidMotionAttempts": invalid_motions,
        "policyLatencyMsMean": float(np.mean(policy_times)),
        "unityPythonLoopLatencyMsMean": float(np.mean(loop_times)),
    }


def summarize(episodes: list[dict[str, object]]) -> dict[str, object]:
    reachable = [episode for episode in episodes if episode["reachable"]]
    successes = sum(episode["explicitStopInRegion"] for episode in reachable)
    return {
        "episodes": len(episodes),
        "reachableEpisodes": len(reachable),
        "targetAbsentEpisodes": len(episodes) - len(reachable),
        "explicitStopInRegionRate": successes / max(1, len(reachable)),
        "explicitStopInRegionWilson95Ci": proportion_ci(successes, len(reachable)),
        "regionEnteredRate": sum(episode["regionEntered"] for episode in reachable) / max(1, len(reachable)),
        "meanStoppingErrorToAcceptableRangeMm": float(np.mean([episode["stoppingErrorToAcceptableRangeMm"] for episode in reachable])) if reachable else None,
        "meanSteps": float(np.mean([episode["steps"] for episode in episodes])),
        "timeouts": int(sum(episode["timeout"] for episode in episodes)),
        "meanReversals": float(np.mean([episode["reversals"] for episode in episodes])),
        "contactViolations": int(sum(episode["contactViolations"] for episode in episodes)),
        "invalidMotionAttempts": int(sum(episode["invalidMotionAttempts"] for episode in episodes)),
        "policyLatencyMsMean": float(np.mean([episode["policyLatencyMsMean"] for episode in episodes])),
        "unityPythonLoopLatencyMsMean": float(np.mean([episode["unityPythonLoopLatencyMsMean"] for episode in episodes])),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Actual Unity-Python-Unity closed-loop evaluation")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--group", choices=("development", "heldout", "stress"), default="heldout")
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--include-teacher", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    if model.get("experimentId") != args.experiment_id:
        raise SystemExit("Checkpoint experiment does not match requested experiment")
    weights = np.asarray(model["weights"], dtype=np.float32)
    bias = np.asarray(model["bias"], dtype=np.float32)
    scenarios = {
        "development": (12, 13, 14),
        "heldout": (15, 16, 17),
        "stress": (18, 19, 20),
    }[args.group]
    environment = UnityFileEnvironment(timeout=20.0)
    policy_episodes = []
    teacher_episodes = []
    scenario_report = []
    for scenario in scenarios:
        first, acceptable = inspect_scenario(environment, scenario)
        starts = initial_conditions(int(first["pathCount"]), acceptable)
        scenario_report.append({"scenarioIndex": scenario, "configurationFamily": first.get("configurationFamily"), "pathCount": int(first["pathCount"]), "acceptableIndices": acceptable, "reachable": bool(acceptable), "initialConditions": starts})
        for name, index in starts.items():
            policy = run_episode(environment, index, acceptable, model, weights, bias, args.max_steps)
            policy.update({"scenarioIndex": scenario, "startCondition": name, "startIndex": index, "reachable": bool(acceptable)})
            policy_episodes.append(policy)
            if args.include_teacher:
                oracle = run_episode(environment, index, acceptable, model, weights, bias, args.max_steps, teacher=True)
                oracle.update({"scenarioIndex": scenario, "startCondition": name, "startIndex": index, "reachable": bool(acceptable)})
                teacher_episodes.append(oracle)
    report = {
        "schemaVersion": 2,
        "experimentId": args.experiment_id,
        "checkpoint": str(args.checkpoint),
        "model": model.get("model"),
        "evaluationGroup": args.group,
        "definition": "Success requires an explicit model-selected STOP while Unity reports the current view inside the acceptable simulator-score range. Merely entering/passing through is reported separately.",
        "policyInputAudit": f"{model.get('observationMode', 'current')} clean ultrasound observation only",
        "scenarios": scenario_report,
        "policy": {"summary": summarize(policy_episodes), "episodes": policy_episodes},
        "teacherOracle": {"summary": summarize(teacher_episodes), "episodes": teacher_episodes} if teacher_episodes else None,
        "targetAbsentInterpretation": "The 3-action policy has no explicit abstain/failure action; STOP outside the region is retained as failure.",
        "finalTestWasNotUsedForModelSelection": True,
    }
    save_json(args.output, report)
    print(json.dumps({"checkpoint": report["checkpoint"], "evaluationGroup": args.group, "policy": report["policy"]["summary"], "teacherOracle": report["teacherOracle"]["summary"] if report["teacherOracle"] else None}, indent=2))


if __name__ == "__main__":
    main()
