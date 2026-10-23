from __future__ import annotations

import argparse
import json
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from coach_data import DEFAULT_EXPERIMENT_ID, INDEX_TO_ACTION, model_probabilities, project_root, request_features, save_json, stable_softmax
from contract_v3 import CONTRACT_VERSION
from protocol_v3 import validate_manifest, validate_model, verify_frozen_scenario
from ppo_math import (
    adam_step,
    clipped_surrogate_loss_and_gradient,
    generalized_advantage_estimate,
    normalize_batch,
    require_finite,
    value_loss_and_gradient,
    value_predict,
)
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
    wrong_stop_count = 0
    contact_violations = workspace_violations = constraint_violations = 0
    invalid_actions = boundary_actions = 0
    region_entered = bool(response.get("regionEntered", False))
    travelled_distance_mm = 0.0
    for _ in range(max_steps):
        current_image = Path(str(response["imagePath"]))
        features = policy_features(response, previous_image, previous_action, model)
        probabilities = stable_softmax(features @ weights + bias)
        action_index = int(rng.choice(3, p=probabilities))
        action = int(INDEX_TO_ACTION[action_index])
        next_response = environment.step(action)
        travelled_distance_mm += abs(int(next_response.get("pathIndex", 0)) - int(response.get("pathIndex", 0))) * float(next_response.get("pathSpacingMm", 0.0))
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
        wrong_stop = action == 0 and not bool(next_response.get("explicitStopSuccess", False))
        false_stop |= wrong_stop
        wrong_stop_count += int(wrong_stop)
        contact_violations += int(not bool(next_response.get("contactValid", False)))
        workspace_violations += int(not bool(next_response.get("workspaceValid", False)))
        constraint_violations += int(bool(next_response.get("constraintViolation", False)))
        invalid_actions += int(bool(next_response.get("invalidMotion", False)))
        boundary_actions += int(bool(next_response.get("patchBoundaryViolation", False)))
        region_entered |= bool(next_response.get("regionEntered", False))
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
        "wrongStopCount": wrong_stop_count,
        "regionEntered": region_entered,
        "contactViolations": contact_violations,
        "workspaceViolations": workspace_violations,
        "constraintViolations": constraint_violations,
        "invalidActions": invalid_actions,
        "boundaryActions": boundary_actions,
        "travelledDistanceMm": travelled_distance_mm,
        "longestBoundaryStuckStreak": longest_boundary_streak,
    })


def run_frozen_evaluation(environment, frozen, model, weights, bias, max_steps) -> dict[str, object]:
    episodes = []
    for frozen_scenario in frozen["scenarios"]:
        scenario_index = int(frozen_scenario["scenarioIndex"])
        trajectory_seed = int(frozen_scenario["trajectorySeed"])
        exact_scenario = dict(frozen_scenario["scenario"])
        environment.reset(0, max_steps=max_steps, frozen_scenario=exact_scenario)
        actual = dict(environment.describe()["scenario"])
        verify_frozen_scenario(frozen_scenario, actual)
        target_reachable = bool(frozen_scenario.get("targetReachable", actual["acceptableIndices"]))
        for start in frozen_scenario["starts"]:
            response = environment.reset(int(start["index"]), max_steps=max_steps)
            previous_image = None
            previous_action = 0
            false_stop = False
            wrong_stop_count = 0
            boundary_failures = invalid_actions = 0
            contact_violations = workspace_violations = constraint_violations = 0
            boundary_streak = longest_boundary_streak = 0
            region_entered = bool(response.get("regionEntered", False))
            travelled_distance_mm = 0.0
            inference_ms = []
            step_ms = []
            for step in range(max_steps):
                current = Path(str(response["imagePath"]))
                inference_started = time.perf_counter()
                features = policy_features(response, previous_image, previous_action, model)
                action = int(INDEX_TO_ACTION[int(np.argmax(model_probabilities(model, features, weights, bias)))])
                inference_ms.append((time.perf_counter() - inference_started) * 1000.0)
                previous_image, previous_action = current, action
                previous_index = int(response.get("pathIndex", 0))
                step_started = time.perf_counter()
                response = environment.step(action)
                step_ms.append((time.perf_counter() - step_started) * 1000.0)
                travelled_distance_mm += abs(int(response.get("pathIndex", 0)) - previous_index) * float(response.get("pathSpacingMm", 0.0))
                boundary = bool(response.get("patchBoundaryViolation", False))
                boundary_failures += int(boundary)
                invalid_actions += int(bool(response.get("invalidMotion", False)))
                if boundary:
                    boundary_streak += 1
                    longest_boundary_streak = max(longest_boundary_streak, boundary_streak)
                else:
                    boundary_streak = 0
                wrong_stop = action == 0 and not bool(response.get("explicitStopSuccess", False))
                false_stop |= wrong_stop
                wrong_stop_count += int(wrong_stop)
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
                "wrongStop": false_stop,
                "wrongStopCount": wrong_stop_count,
                "boundaryFailures": boundary_failures,
                "invalidActions": invalid_actions,
                "longestBoundaryStuckStreak": longest_boundary_streak,
                "contactViolations": contact_violations,
                "workspaceViolations": workspace_violations,
                "constraintViolations": constraint_violations,
                "travelledDistanceMm": travelled_distance_mm,
                "policyLatencyMsMean": float(np.mean(inference_ms)),
                "unityStepLatencyMsMean": float(np.mean(step_ms)),
                "timeout": bool(response.get("truncated", False)),
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
            "invalidActions": sum(item["invalidActions"] for item in episodes),
            "contactViolations": sum(item["contactViolations"] for item in episodes),
            "workspaceViolations": sum(item["workspaceViolations"] for item in episodes),
            "constraintViolations": sum(item["constraintViolations"] for item in episodes),
            "targetAbsentTotal": len(target_absent),
            "targetAbsentWrongStop": sum(item["wrongStop"] for item in target_absent),
            "targetAbsentTimeouts": sum(item["timeout"] for item in target_absent),
        },
        "explicitStopSuccessRate": rate(episodes, "explicitStopSuccess"),
        "recognitionRate": rate(recognition, "recognitionSuccess"),
        "acquisitionRate": rate(acquisition, "acquisitionSuccess"),
        "byFamily": {family: {"episodes": sum(item["family"] == family for item in episodes), "successes": sum(item["family"] == family and item["explicitStopSuccess"] for item in episodes)} for family in sorted({item["family"] for item in episodes})},
    }


def candidate_selection_key(report: dict[str, object]) -> tuple[float, ...]:
    """Predeclared development-only ordering: constraints, acquisition, recognition."""
    raw = report["rawCounts"]
    acquisition = float(report.get("acquisitionRate") or 0.0)
    recognition = float(report.get("recognitionRate") or 0.0)
    constraints_ok = int(raw.get("constraintViolations", 0)) == 0
    recognition_gate = recognition >= 0.95
    eligible = constraints_ok and recognition_gate
    return (
        float(eligible),
        acquisition,
        recognition,
        float(report.get("explicitStopSuccessRate") or 0.0),
        -float(raw.get("wrongStop", 0)),
        -float(raw.get("boundaryFailures", 0)),
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Corrected live PPO for the 1D contract v3")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--bc", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/bc_pixel_history_contract_v3.json")
    parser.add_argument("--dev-manifest", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/manifests/one_d_development_contract_v3.json")
    parser.add_argument("--updates", type=int, help="Optional update cap for this invocation; transition budget remains authoritative")
    parser.add_argument("--episodes-per-update", type=int, default=12)
    parser.add_argument("--transitions-per-update", type=int, default=512)
    parser.add_argument("--transition-budget", type=int, default=50_000)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--eval-interval", type=int, default=2)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--gamma", type=float, default=0.95)
    parser.add_argument("--gae-lambda", type=float, default=0.95)
    parser.add_argument("--clip", type=float, default=0.15)
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--value-lr", type=float, default=0.01)
    parser.add_argument("--update-epochs", type=int, default=4)
    parser.add_argument("--run-id", default="fullscale_cpu_v3")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--best-output", type=Path)
    parser.add_argument("--metrics-output", type=Path)
    parser.add_argument("--state-output", type=Path)
    parser.add_argument("--resume", type=Path)
    args = parser.parse_args()
    artifact_stem = f"{args.run_id}_seed{args.seed}"
    args.output = args.output or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{artifact_stem}_ppo_final.json"
    args.best_output = args.best_output or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{artifact_stem}_policy_best_dev.json"
    args.metrics_output = args.metrics_output or project_root() / f"Artifacts/ThoracicUSCoach/metrics/{artifact_stem}_ppo_training.json"
    args.state_output = args.state_output or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{artifact_stem}_training_state.json"
    if args.transition_budget <= 0 or args.transitions_per_update <= 0 or args.episodes_per_update <= 0:
        raise SystemExit("Transition budget, transitions/update, and episodes/update must be positive")
    rng = np.random.default_rng(args.seed)
    model = json.loads(args.bc.read_text(encoding="utf-8"))
    frozen = json.loads(args.dev_manifest.read_text(encoding="utf-8"))
    if model.get("experimentId") != args.experiment_id:
        raise SystemExit("BC checkpoint experiment does not match --experiment-id")
    expected_width = int(model.get("featureSize", 16)) ** 2
    if str(model.get("observationMode")) == "history":
        expected_width = expected_width * 2 + 3
    validate_model(model, expected_width)
    if str(model.get("architecture") or "linear-softmax-v1") != "linear-softmax-v1":
        raise SystemExit("Live PPO currently supports only the declared linear-softmax-v1 actor")
    if frozen.get("contractVersion") != CONTRACT_VERSION:
        raise SystemExit("Frozen development manifest contract mismatch")
    validate_manifest(frozen, expected_group="development", selection_allowed=True)
    weights = np.asarray(model["weights"], dtype=np.float64)
    bias = np.asarray(model["bias"], dtype=np.float64)
    value_parameters = np.zeros(weights.shape[0] + 1, dtype=np.float64)
    value_first_moment = np.zeros_like(value_parameters)
    value_second_moment = np.zeros_like(value_parameters)
    value_optimizer_step = 0
    environment = UnityFileEnvironment(timeout=20.0)
    started_training = time.perf_counter()
    training_episodes, update_reports, dev_reports = [], [], []
    completed_updates = total_transitions = 0
    if args.resume is not None:
        state = json.loads(args.resume.read_text(encoding="utf-8"))
        if state.get("experimentId") != args.experiment_id or int(state.get("trainingSeed", -1)) != args.seed:
            raise SystemExit("Resume state experiment/seed mismatch")
        if state.get("sourceBcDatasetFingerprint") != model.get("datasetFingerprint"):
            raise SystemExit("Resume state BC dataset mismatch")
        weights = np.asarray(state["actorWeights"], dtype=np.float64)
        bias = np.asarray(state["actorBias"], dtype=np.float64)
        value_parameters = np.asarray(state["criticParameters"], dtype=np.float64)
        value_first_moment = np.asarray(state["criticFirstMoment"], dtype=np.float64)
        value_second_moment = np.asarray(state["criticSecondMoment"], dtype=np.float64)
        value_optimizer_step = int(state["criticOptimizerStep"])
        best_weights = np.asarray(state["bestActorWeights"], dtype=np.float64)
        best_bias = np.asarray(state["bestActorBias"], dtype=np.float64)
        selected_source = str(state["selectedSource"])
        best_key = tuple(float(value) for value in state["bestSelectionKey"])
        completed_updates = int(state["completedUpdates"])
        total_transitions = int(state["totalTransitions"])
        training_episodes = list(state.get("trainingEpisodes", []))
        update_reports = list(state.get("updates", []))
        dev_reports = list(state.get("developmentEvaluations", []))
        rng.bit_generator.state = state["rngState"]
    else:
        bc_dev = run_frozen_evaluation(environment, frozen, model, weights, bias, args.max_steps)
        best_key = candidate_selection_key(bc_dev)
        best_weights, best_bias, selected_source = weights.copy(), bias.copy(), "initial-bc"
        dev_reports.append({"afterUpdate": 0, "candidate": "initial-bc", "selectionKey": list(best_key), **bc_dev})
    start_names = ("endpoint-low", "endpoint-high", "inside", "immediately-outside", "far")
    updates_this_invocation = 0
    while total_transitions < args.transition_budget and (args.updates is None or updates_this_invocation < args.updates):
        update_number = completed_updates + 1
        rollouts = []
        batch_transitions = 0
        while len(rollouts) < args.episodes_per_update or batch_transitions < args.transitions_per_update:
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
            batch_transitions += len(rollout.rewards)
            training_episodes.append({"update": update_number, **rollout.report})
        x_parts, action_parts, old_parts, advantage_parts, return_parts = [], [], [], [], []
        for rollout in rollouts:
            x, next_x = np.stack(rollout.features), np.stack(rollout.next_features)
            values = value_predict(x, value_parameters)
            next_values = value_predict(next_x, value_parameters)
            advantages, returns = generalized_advantage_estimate(np.asarray(rollout.rewards), values, next_values, np.asarray(rollout.terminated), np.asarray(rollout.truncated), args.gamma, args.gae_lambda)
            x_parts.append(x); action_parts.append(np.asarray(rollout.actions)); old_parts.append(np.asarray(rollout.old_probabilities)); advantage_parts.append(advantages); return_parts.append(returns)
        x_batch = np.concatenate(x_parts)
        actions, old = np.concatenate(action_parts), np.concatenate(old_parts)
        raw_advantages = np.concatenate(advantage_parts)
        returns = np.concatenate(return_parts)
        batch_statistics = {
            "rawAdvantageMean": float(np.mean(raw_advantages)),
            "rawAdvantageStd": float(np.std(raw_advantages)),
            "rawAdvantageMin": float(np.min(raw_advantages)),
            "rawAdvantageMax": float(np.max(raw_advantages)),
            "returnMean": float(np.mean(returns)),
            "returnStd": float(np.std(returns)),
            "returnMin": float(np.min(returns)),
            "returnMax": float(np.max(returns)),
        }
        advantages = normalize_batch(raw_advantages)
        require_finite("PPO batch advantages", advantages)
        require_finite("PPO batch returns", returns)
        epoch_stats = []
        for _ in range(args.update_epochs):
            _, gradient_logits, stats = clipped_surrogate_loss_and_gradient(x_batch @ weights + bias, actions, old, advantages, args.clip)
            actor_weight_gradient = x_batch.T @ gradient_logits + 1e-4 * weights
            actor_bias_gradient = gradient_logits.sum(axis=0)
            require_finite("actor weight gradient", actor_weight_gradient)
            require_finite("actor bias gradient", actor_bias_gradient)
            weights -= args.lr * actor_weight_gradient
            bias -= args.lr * actor_bias_gradient
            require_finite("actor weights", weights)
            require_finite("actor bias", bias)
            value_loss, value_gradient, value_predictions = value_loss_and_gradient(x_batch, returns, value_parameters)
            value_optimizer_step += 1
            value_parameters, value_first_moment, value_second_moment, value_stats = adam_step(
                value_parameters, value_gradient, value_first_moment, value_second_moment,
                value_optimizer_step, args.value_lr,
            )
            next_value_loss, _, next_value_predictions = value_loss_and_gradient(x_batch, returns, value_parameters)
            stats.update({
                "actorWeightGradientNorm": float(np.linalg.norm(actor_weight_gradient)),
                "actorBiasGradientNorm": float(np.linalg.norm(actor_bias_gradient)),
                "valueLossBefore": value_loss,
                "valueLossAfter": next_value_loss,
                "valuePredictionMeanBefore": float(np.mean(value_predictions)),
                "valuePredictionMinBefore": float(np.min(value_predictions)),
                "valuePredictionMaxBefore": float(np.max(value_predictions)),
                "valuePredictionMeanAfter": float(np.mean(next_value_predictions)),
                "valueTargetMean": float(np.mean(returns)),
                "valueTargetStd": float(np.std(returns)),
                "normalizedAdvantageMean": float(np.mean(advantages)),
                "normalizedAdvantageStd": float(np.std(advantages)),
                **value_stats,
            })
            epoch_stats.append(stats)
        total_transitions += len(x_batch)
        completed_updates = update_number
        updates_this_invocation += 1
        coverage = {"scenarioIndices": sorted({item.report["scenarioIndex"] for item in rollouts}), "trajectorySeeds": len({item.report["trajectorySeed"] for item in rollouts}), "startConditions": sorted({item.report["startCondition"] for item in rollouts})}
        update_reports.append({"update": update_number, "samples": len(x_batch), "cumulativeTransitions": total_transitions, "coverage": coverage, "batchStatistics": batch_statistics, "epochs": epoch_stats})
        if update_number % args.eval_interval == 0 or total_transitions >= args.transition_budget:
            dev = run_frozen_evaluation(environment, frozen, model, weights, bias, args.max_steps)
            selection_key = candidate_selection_key(dev)
            dev_reports.append({"afterUpdate": update_number, "candidate": "ppo", "selectionKey": list(selection_key), **dev})
            if selection_key > best_key:
                best_key, best_weights, best_bias, selected_source = selection_key, weights.copy(), bias.copy(), f"ppo-update-{update_number}"
        state = {
            "schemaVersion": 1,
            "experimentId": args.experiment_id,
            "environmentContractVersion": CONTRACT_VERSION,
            "trainingSeed": args.seed,
            "runId": args.run_id,
            "sourceBcCheckpoint": str(args.bc),
            "sourceBcDatasetFingerprint": model.get("datasetFingerprint"),
            "developmentManifestIdentity": frozen.get("manifestSha256"),
            "actorWeights": weights.tolist(),
            "actorBias": bias.tolist(),
            "criticParameters": value_parameters.tolist(),
            "criticFirstMoment": value_first_moment.tolist(),
            "criticSecondMoment": value_second_moment.tolist(),
            "criticOptimizerStep": value_optimizer_step,
            "rngState": rng.bit_generator.state,
            "completedUpdates": completed_updates,
            "totalTransitions": total_transitions,
            "bestActorWeights": best_weights.tolist(),
            "bestActorBias": best_bias.tolist(),
            "bestSelectionKey": list(best_key),
            "selectedSource": selected_source,
            "trainingEpisodes": training_episodes,
            "updates": update_reports,
            "developmentEvaluations": dev_reports,
        }
        save_json(args.state_output, state)
        print(json.dumps({
            "phase": "live-unity-refinement",
            "update": update_number,
            "samples": len(x_batch),
            "cumulativeTransitions": total_transitions,
            "transitionBudget": args.transition_budget,
            "latestValueLossBefore": epoch_stats[-1]["valueLossBefore"],
            "latestValueLossAfter": epoch_stats[-1]["valueLossAfter"],
            "selectedSource": selected_source,
        }), flush=True)
    common = {
        "schemaVersion": 3,
        "algorithm": "on-policy clipped PPO with linear value baseline and GAE",
        "environmentContractVersion": CONTRACT_VERSION,
        "experimentId": args.experiment_id,
        "sourceBcCheckpoint": str(args.bc),
        "developmentManifest": str(args.dev_manifest),
        "trainingSeed": args.seed,
        "runId": args.run_id,
        "actualTrainingTransitions": total_transitions,
        "cachedSimulatorTrainingTransitions": int(model.get(
            "cachedSimulatorTrainingTransitions",
            model.get("actualTrainingTransitions", 0) if model.get("checkpointRole") == "cached-pretraining-final" else 0,
        )),
        "liveUnityTrainingTransitions": total_transitions,
        "completedUpdates": completed_updates,
        "transitionBudget": args.transition_budget,
        "budgetComplete": total_transitions >= args.transition_budget,
        "trainingEpisodes": training_episodes,
        "updates": update_reports,
        "developmentEvaluations": dev_reports,
        "initialBcEligibleForSelection": True,
        "selectedSource": selected_source,
        "selectionRule": "Development-only lexicographic: zero constraints + recognition>=0.95 eligibility, then acquisition, recognition, pooled success, fewer wrong STOP and boundary failures.",
        "bestSelectionKey": list(best_key),
        "hyperparameters": {"gamma": args.gamma, "gaeLambda": args.gae_lambda, "clip": args.clip, "learningRate": args.lr, "valueLearningRate": args.value_lr, "valueOptimizer": "Adam with dimension-scaled inputs and global-norm clip 1.0", "updateEpochs": args.update_epochs, "minimumEpisodesPerUpdate": args.episodes_per_update, "targetTransitionsPerUpdate": args.transitions_per_update},
        "elapsedSeconds": time.perf_counter() - started_training,
        "selectionDataGroups": ["training", "development"],
        "heldoutOrStressUsedForSelection": False,
        "methodLabel": (
            "cached-simulator pretraining + live refinement"
            if model.get("checkpointRole") == "cached-pretraining-final"
            else "live Unity PPO refinement"
        ),
    }
    final_model, best_model = dict(model), dict(model)
    final_model.update(common | {"model": "pixel16-history-ppo-final-contract-v3", "weights": weights.tolist(), "bias": bias.tolist(), "checkpointRole": "final"})
    best_model.update(common | {"model": "pixel16-history-policy-best-dev-contract-v3", "weights": best_weights.tolist(), "bias": best_bias.tolist(), "checkpointRole": "best-development"})
    save_json(args.output, final_model); save_json(args.best_output, best_model); save_json(args.metrics_output, common)
    print(json.dumps({"finalCheckpoint": str(args.output), "bestCheckpoint": str(args.best_output), "state": str(args.state_output), "selectedSource": selected_source, "bestSelectionKey": list(best_key), "actualTrainingTransitions": total_transitions, "budgetComplete": total_transitions >= args.transition_budget}, indent=2))


if __name__ == "__main__":
    main()
