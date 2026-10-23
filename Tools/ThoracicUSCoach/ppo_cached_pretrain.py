from __future__ import annotations

import argparse
import json
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from cached_env import CachedEnvironment, cached_features, load_cached_trajectories, start_indices
from coach_data import DEFAULT_DATASET_RUN_ID, DEFAULT_EXPERIMENT_ID, INDEX_TO_ACTION, project_root, save_json, stable_softmax
from contract_v3 import CONTRACT_VERSION
from ppo_math import (
    adam_step,
    clipped_surrogate_loss_and_gradient,
    generalized_advantage_estimate,
    normalize_batch,
    require_finite,
    value_loss_and_gradient,
    value_predict,
)
from protocol_v3 import validate_model


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


def collect_episode(trajectory, model, weights, bias, rng, start_condition: str, max_steps: int) -> Rollout:
    starts = start_indices(trajectory)
    if start_condition not in starts:
        start_condition = sorted(starts)[int(rng.integers(0, len(starts)))]
    environment = CachedEnvironment(trajectory, starts[start_condition], max_steps)
    feature_size = int(model.get("featureSize", 16))
    observation = str(model.get("observationMode", "current"))
    previous_image = None
    previous_action = 0
    features_batch, next_features_batch = [], []
    actions, old_probabilities, rewards, terminated, truncated = [], [], [], [], []
    boundary_actions = wrong_stops = 0
    region_entered = environment.frame.acceptable
    initial_index = environment.index
    success = False
    for _ in range(max_steps):
        current_image = environment.frame.image
        features = cached_features(
            current_image, previous_image, previous_action,
            size=feature_size, observation=observation,
        )
        probabilities = stable_softmax(features @ weights + bias)
        action_index = int(rng.choice(3, p=probabilities))
        action = int(INDEX_TO_ACTION[action_index])
        result = environment.step(action)
        next_image = environment.frame.image
        next_features = cached_features(
            next_image, current_image, action,
            size=feature_size, observation=observation,
        )
        features_batch.append(features)
        next_features_batch.append(next_features)
        actions.append(action_index)
        old_probabilities.append(float(probabilities[action_index]))
        rewards.append(float(result.reward))
        terminated.append(result.terminated)
        truncated.append(result.truncated)
        boundary_actions += int(result.boundary_violation)
        wrong_stops += int(action == 0 and not result.explicit_stop_success)
        region_entered |= result.target_acceptable
        success = result.explicit_stop_success
        previous_image, previous_action = current_image, action
        if result.done:
            break
    return Rollout(
        features_batch,
        next_features_batch,
        actions,
        old_probabilities,
        rewards,
        terminated,
        truncated,
        {
            "sweepId": trajectory.sweep_id,
            "configurationFamily": trajectory.configuration_family,
            "trajectorySeed": trajectory.random_seed,
            "pathDirection": trajectory.path_direction,
            "stepCount": trajectory.step_count,
            "startCondition": start_condition,
            "startIndex": initial_index,
            "steps": len(rewards),
            "return": float(sum(rewards)),
            "explicitStopSuccess": success,
            "wrongStopCount": wrong_stops,
            "boundaryActions": boundary_actions,
            "regionEntered": region_entered,
            "terminated": bool(terminated[-1]),
            "truncated": bool(truncated[-1]),
        },
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="On-policy PPO pretraining in the captured-image v3 cache")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--bc", type=Path, required=True)
    parser.add_argument("--transition-budget", type=int, default=50_000)
    parser.add_argument("--transitions-per-update", type=int, default=500)
    parser.add_argument("--episodes-per-update", type=int, default=24)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--gamma", type=float, default=0.95)
    parser.add_argument("--gae-lambda", type=float, default=0.95)
    parser.add_argument("--clip", type=float, default=0.15)
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--value-lr", type=float, default=0.01)
    parser.add_argument("--update-epochs", type=int, default=4)
    parser.add_argument("--action-step-mm", type=float, default=1.5)
    parser.add_argument("--run-id", default="main_cpu_v3_cached")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--metrics-output", type=Path)
    parser.add_argument("--state-output", type=Path)
    parser.add_argument("--resume", type=Path)
    args = parser.parse_args()
    if min(args.transition_budget, args.transitions_per_update, args.episodes_per_update, args.max_steps) <= 0:
        raise SystemExit("Budgets, episode counts, and max steps must be positive")
    stem = f"{args.run_id}_seed{args.seed}"
    args.output = args.output or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{stem}_pretrained.json"
    args.metrics_output = args.metrics_output or project_root() / f"Artifacts/ThoracicUSCoach/metrics/{stem}_training.json"
    args.state_output = args.state_output or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{stem}_state.json"

    model = json.loads(args.bc.read_text(encoding="utf-8"))
    if model.get("experimentId") != args.experiment_id or model.get("datasetRunId") != args.dataset_run_id:
        raise SystemExit("BC checkpoint experiment/dataset does not match cached pretraining request")
    expected_width = int(model.get("featureSize", 16)) ** 2
    if str(model.get("observationMode")) == "history":
        expected_width = expected_width * 2 + 3
    validate_model(model, expected_width)
    if str(model.get("architecture") or "linear-softmax-v1") != "linear-softmax-v1":
        raise SystemExit("Cached PPO supports only linear-softmax-v1 actors")
    trajectories = load_cached_trajectories(
        experiment_id=args.experiment_id,
        dataset_run_id=args.dataset_run_id,
        split="train",
        action_step_mm=args.action_step_mm,
    )
    if any(item.evaluation_group != "training" for item in trajectories):
        raise SystemExit("Cached PPO refuses non-training trajectory groups")

    rng = np.random.default_rng(args.seed)
    weights = np.asarray(model["weights"], dtype=np.float64)
    bias = np.asarray(model["bias"], dtype=np.float64)
    critic = np.zeros(weights.shape[0] + 1, dtype=np.float64)
    critic_first = np.zeros_like(critic)
    critic_second = np.zeros_like(critic)
    critic_step = completed_updates = total_transitions = 0
    update_reports: list[dict[str, object]] = []
    episode_reports: list[dict[str, object]] = []
    if args.resume:
        state = json.loads(args.resume.read_text(encoding="utf-8"))
        if state.get("experimentId") != args.experiment_id or state.get("datasetRunId") != args.dataset_run_id or int(state.get("trainingSeed", -1)) != args.seed:
            raise SystemExit("Resume state identity mismatch")
        if state.get("sourceBcDatasetFingerprint") != model.get("datasetFingerprint"):
            raise SystemExit("Resume state source dataset mismatch")
        weights = np.asarray(state["actorWeights"], dtype=np.float64)
        bias = np.asarray(state["actorBias"], dtype=np.float64)
        critic = np.asarray(state["criticParameters"], dtype=np.float64)
        critic_first = np.asarray(state["criticFirstMoment"], dtype=np.float64)
        critic_second = np.asarray(state["criticSecondMoment"], dtype=np.float64)
        critic_step = int(state["criticOptimizerStep"])
        completed_updates = int(state["completedUpdates"])
        total_transitions = int(state["totalTransitions"])
        update_reports = list(state.get("updates", []))
        episode_reports = list(state.get("trainingEpisodes", []))
        rng.bit_generator.state = state["rngState"]

    started = time.perf_counter()
    start_names = ("endpoint-low", "endpoint-high", "inside", "immediately-outside", "far")
    while total_transitions < args.transition_budget:
        update_number = completed_updates + 1
        rollouts = []
        batch_count = 0
        while len(rollouts) < args.episodes_per_update or batch_count < args.transitions_per_update:
            trajectory = trajectories[int(rng.integers(0, len(trajectories)))]
            start = start_names[int(rng.integers(0, len(start_names)))]
            rollout = collect_episode(trajectory, model, weights, bias, rng, start, args.max_steps)
            rollouts.append(rollout)
            batch_count += len(rollout.rewards)
            episode_reports.append({"update": update_number, **rollout.report})

        x_parts, action_parts, old_parts, advantage_parts, return_parts = [], [], [], [], []
        for rollout in rollouts:
            x = np.stack(rollout.features)
            next_x = np.stack(rollout.next_features)
            values = value_predict(x, critic)
            next_values = value_predict(next_x, critic)
            advantages, returns = generalized_advantage_estimate(
                np.asarray(rollout.rewards), values, next_values,
                np.asarray(rollout.terminated), np.asarray(rollout.truncated),
                args.gamma, args.gae_lambda,
            )
            x_parts.append(x)
            action_parts.append(np.asarray(rollout.actions))
            old_parts.append(np.asarray(rollout.old_probabilities))
            advantage_parts.append(advantages)
            return_parts.append(returns)
        x_batch = np.concatenate(x_parts)
        actions = np.concatenate(action_parts)
        old = np.concatenate(old_parts)
        raw_advantages = np.concatenate(advantage_parts)
        returns = np.concatenate(return_parts)
        advantages = normalize_batch(raw_advantages)
        require_finite("cached PPO advantages", advantages)
        require_finite("cached PPO returns", returns)
        epochs = []
        for _ in range(args.update_epochs):
            _, gradient_logits, stats = clipped_surrogate_loss_and_gradient(
                x_batch @ weights + bias, actions, old, advantages, args.clip,
            )
            weight_gradient = x_batch.T @ gradient_logits + 1e-4 * weights
            bias_gradient = gradient_logits.sum(axis=0)
            require_finite("cached actor weight gradient", weight_gradient)
            require_finite("cached actor bias gradient", bias_gradient)
            weights -= args.lr * weight_gradient
            bias -= args.lr * bias_gradient
            value_loss, value_gradient, before_predictions = value_loss_and_gradient(x_batch, returns, critic)
            critic_step += 1
            critic, critic_first, critic_second, value_stats = adam_step(
                critic, value_gradient, critic_first, critic_second, critic_step, args.value_lr,
            )
            next_value_loss, _, after_predictions = value_loss_and_gradient(x_batch, returns, critic)
            stats.update({
                "actorWeightGradientNorm": float(np.linalg.norm(weight_gradient)),
                "actorBiasGradientNorm": float(np.linalg.norm(bias_gradient)),
                "valueLossBefore": value_loss,
                "valueLossAfter": next_value_loss,
                "valuePredictionMeanBefore": float(np.mean(before_predictions)),
                "valuePredictionMeanAfter": float(np.mean(after_predictions)),
                **value_stats,
            })
            epochs.append(stats)
        require_finite("cached actor weights", weights)
        require_finite("cached actor bias", bias)
        total_transitions += len(x_batch)
        completed_updates = update_number
        update_reports.append({
            "update": update_number,
            "samples": len(x_batch),
            "cumulativeTransitions": total_transitions,
            "coverage": {
                "sweepIds": len({item.report["sweepId"] for item in rollouts}),
                "configurationFamilies": len({item.report["configurationFamily"] for item in rollouts}),
                "pathDirections": sorted({item.report["pathDirection"] for item in rollouts}),
                "startConditions": sorted({item.report["startCondition"] for item in rollouts}),
            },
            "batchStatistics": {
                "rawAdvantageMean": float(np.mean(raw_advantages)),
                "rawAdvantageStd": float(np.std(raw_advantages)),
                "returnMean": float(np.mean(returns)),
                "returnStd": float(np.std(returns)),
            },
            "epochs": epochs,
        })
        state = {
            "schemaVersion": 1,
            "experimentId": args.experiment_id,
            "datasetRunId": args.dataset_run_id,
            "environmentContractVersion": CONTRACT_VERSION,
            "trainingSeed": args.seed,
            "runId": args.run_id,
            "sourceBcCheckpoint": str(args.bc),
            "sourceBcDatasetFingerprint": model.get("datasetFingerprint"),
            "actorWeights": weights.tolist(),
            "actorBias": bias.tolist(),
            "criticParameters": critic.tolist(),
            "criticFirstMoment": critic_first.tolist(),
            "criticSecondMoment": critic_second.tolist(),
            "criticOptimizerStep": critic_step,
            "rngState": rng.bit_generator.state,
            "completedUpdates": completed_updates,
            "totalTransitions": total_transitions,
            "trainingEpisodes": episode_reports,
            "updates": update_reports,
        }
        save_json(args.state_output, state)
        print(json.dumps({"update": update_number, "transitions": total_transitions, "target": args.transition_budget}), flush=True)

    common = {
        "schemaVersion": 3,
        "algorithm": "on-policy clipped PPO in captured-image cache with linear value baseline and GAE",
        "environmentContractVersion": CONTRACT_VERSION,
        "experimentId": args.experiment_id,
        "datasetRunId": args.dataset_run_id,
        "sourceBcCheckpoint": str(args.bc),
        "trainingSeed": args.seed,
        "runId": args.run_id,
        "actualTrainingTransitions": total_transitions,
        "cachedSimulatorTrainingTransitions": total_transitions,
        "liveUnityTrainingTransitions": 0,
        "transitionBudget": args.transition_budget,
        "budgetComplete": total_transitions >= args.transition_budget,
        "completedUpdates": completed_updates,
        "cachedTrainingTrajectories": len(trajectories),
        "trainingEpisodes": episode_reports,
        "updates": update_reports,
        "hyperparameters": {
            "gamma": args.gamma,
            "gaeLambda": args.gae_lambda,
            "clip": args.clip,
            "learningRate": args.lr,
            "valueLearningRate": args.value_lr,
            "updateEpochs": args.update_epochs,
            "minimumEpisodesPerUpdate": args.episodes_per_update,
            "targetTransitionsPerUpdate": args.transitions_per_update,
            "maxSteps": args.max_steps,
            "actionStepMm": args.action_step_mm,
        },
        "elapsedSeconds": time.perf_counter() - started,
        "selectionDataGroups": ["training"],
        "heldoutOrStressUsedForSelection": False,
        "developmentUsedForSelection": False,
        "actorInputAudit": "clean cached ultrasound pixels plus previous clean image/executed action for history mode; labels used only by environment reward/termination",
        "methodLabel": "cached-simulator pretraining",
    }
    checkpoint = dict(model)
    checkpoint.update(common | {
        "model": f"pixel{model.get('featureSize', 16)}-history-ppo-cached-pretrained-contract-v3",
        "weights": weights.tolist(),
        "bias": bias.tolist(),
        "checkpointRole": "cached-pretraining-final",
    })
    save_json(args.output, checkpoint)
    save_json(args.metrics_output, common)
    print(json.dumps({
        "checkpoint": str(args.output),
        "state": str(args.state_output),
        "metrics": str(args.metrics_output),
        "actualTrainingTransitions": total_transitions,
        "budgetComplete": True,
    }, indent=2))


if __name__ == "__main__":
    main()
