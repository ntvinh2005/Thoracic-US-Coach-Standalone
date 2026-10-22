from __future__ import annotations

import argparse
import json
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from coach_data import DEFAULT_EXPERIMENT_ID, INDEX_TO_ACTION, project_root, request_features, save_json, stable_softmax
from contract_v3 import CONTRACT_VERSION
from ppo_math import clipped_surrogate_loss_and_gradient, generalized_advantage_estimate, normalize_batch
from unity_env import UnityFileEnvironment


@dataclass
class Rollout:
    features: list[np.ndarray]
    next_features: list[np.ndarray]
    actions: list[int]
    old_probabilities: list[float]
    rewards: list[float]
    terminated: list[bool]
    truncated: list[bool]
    report: dict[str, object]


def policy_features(response, previous_image: Path | None, previous_action: int, model: dict[str, object]) -> np.ndarray:
    return request_features(
        Path(str(response["imagePath"])), previous_image, previous_action,
        int(model.get("featureSize", 16)), str(model.get("observationMode", "current")),
    )


def starts_from_scenario(scenario: dict[str, object]) -> dict[str, int]:
    path_count = len(scenario["pathMm"])
    acceptable = [int(value) for value in scenario["acceptableIndices"]]
    starts = {"endpoint-low": 0, "endpoint-high": path_count - 1}
    if acceptable:
        exact = set(acceptable)
        neighbors = [candidate for target in acceptable for candidate in (target - 1, target + 1) if 0 <= candidate < path_count and candidate not in exact]
        starts["inside"] = acceptable[len(acceptable) // 2]
        if neighbors:
            starts["immediately-outside"] = neighbors[0]
        starts["far"] = max(range(path_count), key=lambda index: min(abs(index - target) for target in acceptable))
    return starts


def regenerate(environment: UnityFileEnvironment, scenario_index: int, seed: int, max_steps: int) -> tuple[dict[str, object], dict[str, object]]:
    first = environment.reset(0, scenario_index=scenario_index, trajectory_seed=seed, regenerate_trajectory=True, max_steps=max_steps)
    scenario = dict(environment.describe()["scenario"])
    if scenario.get("environmentContractVersion") != CONTRACT_VERSION:
        raise RuntimeError("Unity/Python 1D contract mismatch")
    return first, scenario


def collect_episode(environment, model, weights, bias, rng, scenario_index, trajectory_seed, start_condition, max_steps) -> Rollout:
    _, scenario = regenerate(environment, scenario_index, trajectory_seed, max_steps)
    starts = starts_from_scenario(scenario)
    if start_condition not in starts:
        start_condition = sorted(starts)[int(rng.integers(0, len(starts)))]
    start_index = starts[start_condition]
    response = environment.reset(start_index, max_steps=max_steps)
    features_batch, next_features_batch, actions, old_probabilities, rewards, terminated, truncated = [], [], [], [], [], [], []
    previous_image = None
    previous_action = 0
    boundary_streak = longest_boundary_streak = 0
    false_stop = False
    for _ in range(max_steps):
        current_image = Path(str(response["imagePath"]))
        features = policy_features(response, previous_image, previous_action, model)
        probabilities = stable_softmax(features @ weights + bias)
        action_index = int(rng.choice(3, p=probabilities))
        action = int(INDEX_TO_ACTION[action_index])
        next_response = environment.step(action)
        next_features = policy_features(next_response, current_image, action, model)
        features_batch.append(features)
        next_features_batch.append(next_features)
        actions.append(action_index)
        old_probabilities.append(float(probabilities[action_index]))
        rewards.append(float(next_response["reward"]))
        terminated.append(bool(next_response.get("terminated", False)))
        truncated.append(bool(next_response.get("truncated", False)))
        if bool(next_response.get("patchBoundaryViolation", False)):
            boundary_streak += 1
            longest_boundary_streak = max(longest_boundary_streak, boundary_streak)
        else:
            boundary_streak = 0
        false_stop |= action == 0 and not bool(next_response.get("explicitStopSuccess", False))
        previous_image, previous_action, response = current_image, action, next_response
        if bool(response["done"]):
            break
    return Rollout(features_batch, next_features_batch, actions, old_probabilities, rewards, terminated, truncated, {
        "scenarioIndex": scenario_index,
        "trajectorySeed": trajectory_seed,
        "configurationSha256": scenario["configurationSha256"],
        "labelsSha256": scenario["labelsSha256"],
        "startCondition": start_condition,
        "startIndex": start_index,
        "steps": len(rewards),
        "return": float(sum(rewards)),
        "terminated": bool(response.get("terminated", False)),
        "truncated": bool(response.get("truncated", False)),
        "explicitStopSuccess": bool(response.get("explicitStopSuccess", False)),
        "falseStop": false_stop,
        "regionEntered": bool(response.get("regionEntered", False)),
        "constraintViolation": bool(response.get("constraintViolation", False)),
        "longestBoundaryStuckStreak": longest_boundary_streak,
    })


def run_frozen_evaluation(environment, frozen, model, weights, bias, max_steps) -> dict[str, object]:
    episodes = []
    for frozen_scenario in frozen["scenarios"]:
        scenario_index = int(frozen_scenario["scenarioIndex"])
        trajectory_seed = int(frozen_scenario["trajectorySeed"])
        _, actual = regenerate(environment, scenario_index, trajectory_seed, max_steps)
        if actual["configurationSha256"] != frozen_scenario["configurationSha256"] or actual["labelsSha256"] != frozen_scenario["labelsSha256"]:
            raise RuntimeError(f"Frozen scenario identity mismatch for scenario {scenario_index}")
        target_reachable = bool(frozen_scenario.get("targetReachable", actual["acceptableIndices"]))
        for start in frozen_scenario["starts"]:
            response = environment.reset(int(start["index"]), max_steps=max_steps)
            previous_image = None
            previous_action = 0
            false_stop = False
            boundary_failures = 0
            for step in range(max_steps):
                current = Path(str(response["imagePath"]))
                features = policy_features(response, previous_image, previous_action, model)
                action = int(INDEX_TO_ACTION[int(np.argmax(stable_softmax(features @ weights + bias)))])
                previous_image, previous_action = current, action
                response = environment.step(action)
                boundary_failures += int(bool(response.get("patchBoundaryViolation", False)))
                false_stop |= action == 0 and not bool(response.get("explicitStopSuccess", False))
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
                "wrongStop": false_stop,
                "boundaryFailures": boundary_failures,
                "timeout": bool(response.get("truncated", False)),
                "constraintViolation": bool(response.get("constraintViolation", False)),
                "steps": step + 1,
            })
    def rate(items, key):
        return float(np.mean([bool(item[key]) for item in items])) if items else None
    recognition = [item for item in episodes if item["targetReachable"] and item["initiallyAcceptable"]]
    acquisition = [item for item in episodes if item["targetReachable"] and not item["initiallyAcceptable"]]
    target_absent = [item for item in episodes if item["targetAbsent"]]
    return {
        "episodes": episodes,
        "rawCounts": {
            "episodes": len(episodes),
            "explicitStopSuccess": sum(item["explicitStopSuccess"] for item in episodes),
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
        "explicitStopSuccessRate": rate(episodes, "explicitStopSuccess"),
        "recognitionRate": rate(recognition, "recognitionSuccess"),
        "acquisitionRate": rate(acquisition, "acquisitionSuccess"),
        "byFamily": {family: {"episodes": sum(item["family"] == family for item in episodes), "successes": sum(item["family"] == family and item["explicitStopSuccess"] for item in episodes)} for family in sorted({item["family"] for item in episodes})},
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Corrected live PPO for the 1D contract v3")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--bc", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/bc_pixel_history_contract_v3.json")
    parser.add_argument("--dev-manifest", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/manifests/one_d_development_contract_v3.json")
    parser.add_argument("--updates", type=int, default=10)
    parser.add_argument("--episodes-per-update", type=int, default=12)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--eval-interval", type=int, default=2)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--gamma", type=float, default=0.95)
    parser.add_argument("--gae-lambda", type=float, default=0.95)
    parser.add_argument("--clip", type=float, default=0.15)
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--value-lr", type=float, default=0.01)
    parser.add_argument("--update-epochs", type=int, default=4)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/ppo_live_final_contract_v3.json")
    parser.add_argument("--best-output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/policy_best_dev_contract_v3.json")
    parser.add_argument("--metrics-output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/metrics/ppo_training_contract_v3.json")
    args = parser.parse_args()
    rng = np.random.default_rng(args.seed)
    model = json.loads(args.bc.read_text(encoding="utf-8"))
    frozen = json.loads(args.dev_manifest.read_text(encoding="utf-8"))
    if model.get("experimentId") != args.experiment_id:
        raise SystemExit("BC checkpoint experiment does not match --experiment-id")
    if frozen.get("contractVersion") != CONTRACT_VERSION:
        raise SystemExit("Frozen development manifest contract mismatch")
    weights = np.asarray(model["weights"], dtype=np.float64)
    bias = np.asarray(model["bias"], dtype=np.float64)
    value_weights = np.zeros(weights.shape[0], dtype=np.float64)
    value_bias = 0.0
    environment = UnityFileEnvironment(timeout=20.0)
    started_training = time.perf_counter()
    training_episodes, update_reports, dev_reports = [], [], []
    bc_dev = run_frozen_evaluation(environment, frozen, model, weights, bias, args.max_steps)
    best_score = float(bc_dev["explicitStopSuccessRate"] or 0.0)
    best_weights, best_bias, selected_source = weights.copy(), bias.copy(), "initial-bc"
    dev_reports.append({"afterUpdate": 0, "candidate": "initial-bc", **bc_dev})
    start_names = ("endpoint-low", "endpoint-high", "inside", "immediately-outside", "far")
    for update in range(args.updates):
        rollouts = []
        for _ in range(args.episodes_per_update):
            # Scenario, seed, and start are independent random variables. A
            # rejected constrained geometry resamples only the seed; it is not
            # silently projected or repaired into an oracle-centred path.
            scenario_index = int(rng.integers(0, 12))
            start_condition = start_names[int(rng.integers(0, len(start_names)))]
            rollout = None
            for _generation_attempt in range(64):
                trajectory_seed = int(rng.integers(1, 2_000_000_000))
                try:
                    rollout = collect_episode(environment, model, weights, bias, rng, scenario_index, trajectory_seed, start_condition, args.max_steps)
                    break
                except RuntimeError as error:
                    if "Could not generate requested constrained trajectory" not in str(error):
                        raise
            if rollout is None:
                raise RuntimeError(f"Could not sample a valid training trajectory for scenario {scenario_index}")
            rollouts.append(rollout)
            training_episodes.append({"update": update + 1, **rollout.report})
        x_parts, action_parts, old_parts, advantage_parts, return_parts = [], [], [], [], []
        for rollout in rollouts:
            x, next_x = np.stack(rollout.features), np.stack(rollout.next_features)
            values, next_values = x @ value_weights + value_bias, next_x @ value_weights + value_bias
            advantages, returns = generalized_advantage_estimate(np.asarray(rollout.rewards), values, next_values, np.asarray(rollout.terminated), np.asarray(rollout.truncated), args.gamma, args.gae_lambda)
            x_parts.append(x); action_parts.append(np.asarray(rollout.actions)); old_parts.append(np.asarray(rollout.old_probabilities)); advantage_parts.append(advantages); return_parts.append(returns)
        x_batch = np.concatenate(x_parts)
        actions, old = np.concatenate(action_parts), np.concatenate(old_parts)
        advantages, returns = normalize_batch(np.concatenate(advantage_parts)), np.concatenate(return_parts)
        epoch_stats = []
        for _ in range(args.update_epochs):
            _, gradient_logits, stats = clipped_surrogate_loss_and_gradient(x_batch @ weights + bias, actions, old, advantages, args.clip)
            weights -= args.lr * (x_batch.T @ gradient_logits + 1e-4 * weights)
            bias -= args.lr * gradient_logits.sum(axis=0)
            value_error = x_batch @ value_weights + value_bias - returns
            value_weights -= args.value_lr * (x_batch.T @ value_error / len(x_batch))
            value_bias -= args.value_lr * float(value_error.mean())
            stats["valueLoss"] = float(0.5 * np.mean(value_error**2))
            epoch_stats.append(stats)
        coverage = {"scenarioIndices": sorted({item.report["scenarioIndex"] for item in rollouts}), "trajectorySeeds": len({item.report["trajectorySeed"] for item in rollouts}), "startConditions": sorted({item.report["startCondition"] for item in rollouts})}
        update_reports.append({"update": update + 1, "samples": len(x_batch), "coverage": coverage, "epochs": epoch_stats})
        if (update + 1) % args.eval_interval == 0 or update + 1 == args.updates:
            dev = run_frozen_evaluation(environment, frozen, model, weights, bias, args.max_steps)
            dev_reports.append({"afterUpdate": update + 1, "candidate": "ppo", **dev})
            score = float(dev["explicitStopSuccessRate"] or 0.0)
            if score > best_score:
                best_score, best_weights, best_bias, selected_source = score, weights.copy(), bias.copy(), f"ppo-update-{update + 1}"
    common = {
        "schemaVersion": 3,
        "algorithm": "on-policy clipped PPO with linear value baseline and GAE",
        "environmentContractVersion": CONTRACT_VERSION,
        "experimentId": args.experiment_id,
        "sourceBcCheckpoint": str(args.bc),
        "developmentManifest": str(args.dev_manifest),
        "trainingSeed": args.seed,
        "trainingEpisodes": training_episodes,
        "updates": update_reports,
        "developmentEvaluations": dev_reports,
        "initialBcEligibleForSelection": True,
        "selectedSource": selected_source,
        "hyperparameters": {"gamma": args.gamma, "gaeLambda": args.gae_lambda, "clip": args.clip, "learningRate": args.lr, "valueLearningRate": args.value_lr, "updateEpochs": args.update_epochs, "episodesPerUpdate": args.episodes_per_update},
        "elapsedSeconds": time.perf_counter() - started_training,
        "testSetInspected": False,
    }
    final_model, best_model = dict(model), dict(model)
    final_model.update(common | {"model": "pixel16-history-ppo-final-contract-v3", "weights": weights.tolist(), "bias": bias.tolist(), "checkpointRole": "final"})
    best_model.update(common | {"model": "pixel16-history-policy-best-dev-contract-v3", "weights": best_weights.tolist(), "bias": best_bias.tolist(), "checkpointRole": "best-development", "bestDevExplicitStopSuccessRate": best_score})
    save_json(args.output, final_model); save_json(args.best_output, best_model); save_json(args.metrics_output, common | {"bestDevExplicitStopSuccessRate": best_score})
    print(json.dumps({"finalCheckpoint": str(args.output), "bestCheckpoint": str(args.best_output), "selectedSource": selected_source, "bestDevExplicitStopSuccessRate": best_score}, indent=2))


if __name__ == "__main__":
    main()
