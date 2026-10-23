from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from cached_env import load_cached_trajectories
from coach_data import project_root, save_json
from contract_v3 import oracle_action, transition


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"
ACTIONS = (-1, 0, 1)


def solve(trajectory, horizon: int, gamma: float):
    count = len(trajectory.frames)
    values = np.zeros((horizon + 1, count), dtype=np.float64)
    action_sets = [[set() for _ in range(count)] for _ in range(horizon + 1)]
    q_full = np.zeros((count, len(ACTIONS)), dtype=np.float64)
    for remaining in range(1, horizon + 1):
        steps_taken = horizon - remaining
        for index in range(count):
            q_values = []
            for action in ACTIONS:
                next_index = min(count - 1, max(0, index + action * trajectory.step_count))
                destination = trajectory.frames[next_index]
                result = transition(
                    index=index,
                    action=action,
                    step_count=trajectory.step_count,
                    acceptable=trajectory.acceptable,
                    path_mm=trajectory.path_mm,
                    contact_valid=destination.contact_valid,
                    workspace_valid=destination.workspace_valid,
                    steps_taken=steps_taken,
                    max_steps=horizon,
                )
                continuation = 0.0 if result.done else gamma * values[remaining - 1, result.index]
                q_values.append(result.reward + continuation)
            best = max(q_values)
            values[remaining, index] = best
            action_sets[remaining][index] = {
                action for action, value in zip(ACTIONS, q_values) if abs(value - best) <= 1e-10
            }
            if remaining == horizon:
                q_full[index] = q_values
    return values, action_sets, q_full


def greedy_rollout(trajectory, actions, start: int, horizon: int):
    index = start
    steps = []
    for step in range(horizon):
        action = sorted(actions[horizon - step][index], key=lambda value: (value != 0, value))[0]
        destination_index = min(len(trajectory.frames) - 1, max(0, index + action * trajectory.step_count))
        destination = trajectory.frames[destination_index]
        result = transition(
            index=index,
            action=action,
            step_count=trajectory.step_count,
            acceptable=trajectory.acceptable,
            path_mm=trajectory.path_mm,
            contact_valid=destination.contact_valid,
            workspace_valid=destination.workspace_valid,
            steps_taken=step,
            max_steps=horizon,
        )
        steps.append({"index": index, "action": action, "reward": result.reward, "nextIndex": result.index})
        index = result.index
        if result.done:
            return result.explicit_stop_success, steps
    return False, steps


def main() -> None:
    parser = argparse.ArgumentParser(description="Exact finite-horizon DP audit of the v3 reward")
    parser.add_argument("--dataset-run-id", default="main_cpu_v3_20261022")
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--horizon", type=int, default=35)
    parser.add_argument("--gamma", type=float, default=0.99)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    trajectories = []
    for split in ("train", "dev"):
        trajectories.extend(load_cached_trajectories(dataset_run_id=args.dataset_run_id, split=split))

    states = oracle_agreement = rollout_successes = rollout_total = 0
    mismatch_examples = []
    optimal_stop_inside = optimal_stop_outside = 0
    intended_action_gap = []
    for trajectory in trajectories:
        _, action_sets, q_values = solve(trajectory, args.horizon, args.gamma)
        for index in range(len(trajectory.frames)):
            intended = oracle_action(trajectory.acceptable, index, trajectory.step_count)
            optimal = action_sets[args.horizon][index]
            states += 1
            oracle_agreement += int(intended in optimal)
            if 0 in optimal and trajectory.acceptable[index]:
                optimal_stop_inside += 1
            if 0 in optimal and not trajectory.acceptable[index]:
                optimal_stop_outside += 1
            intended_index = ACTIONS.index(intended)
            intended_action_gap.append(float(np.max(q_values[index]) - q_values[index, intended_index]))
            if intended not in optimal and len(mismatch_examples) < 20:
                mismatch_examples.append({
                    "sweepId": trajectory.sweep_id,
                    "index": index,
                    "acceptable": trajectory.acceptable[index],
                    "oracleAction": intended,
                    "optimalActions": sorted(optimal),
                    "qValues": {str(action): float(value) for action, value in zip(ACTIONS, q_values[index])},
                })
            success, _ = greedy_rollout(trajectory, action_sets, index, args.horizon)
            rollout_successes += int(success)
            rollout_total += 1

    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "sourceDatasetRunId": args.dataset_run_id,
        "sourceSplits": ["train", "dev"],
        "heldoutOrStressAccessed": False,
        "method": "exact backward dynamic programming over index, remaining horizon, exact v3 reward and discount",
        "horizon": args.horizon,
        "gamma": args.gamma,
        "trajectories": len(trajectories),
        "states": states,
        "oracleActionOptimalStates": oracle_agreement,
        "oracleActionAgreementRate": oracle_agreement / states,
        "optimalStopInsideStates": optimal_stop_inside,
        "optimalStopOutsideStates": optimal_stop_outside,
        "greedyOptimalRolloutSuccesses": rollout_successes,
        "greedyOptimalRolloutTotal": rollout_total,
        "intendedActionOptimalityGap": {
            "mean": float(np.mean(intended_action_gap)),
            "max": float(np.max(intended_action_gap)),
        },
        "mismatchExamples": mismatch_examples,
        "interpretation": "If agreement and rollout success are complete, the exact reward optimum supports moving toward an acceptable pose and explicit STOP; no reward change is justified by this audit.",
    }
    output = output_dir / "reward_dp_audit.json"
    save_json(output, report)
    print(json.dumps({"phase": "DIAGNOSTIC", "output": str(output), "summary": report}, indent=2))


if __name__ == "__main__":
    main()
