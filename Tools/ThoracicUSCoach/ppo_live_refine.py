from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import DEFAULT_EXPERIMENT_ID, INDEX_TO_ACTION, project_root, request_features, save_json, stable_softmax
from unity_env import UnityFileEnvironment


def discounted_returns(rewards: list[float], gamma: float) -> np.ndarray:
    result = np.zeros(len(rewards), dtype=np.float32)
    running = 0.0
    for index in range(len(rewards) - 1, -1, -1):
        running = rewards[index] + gamma * running
        result[index] = running
    return result


def policy_features(response, previous_image: Path | None, previous_action: int, model: dict[str, object]) -> np.ndarray:
    return request_features(
        Path(str(response["imagePath"])),
        previous_image,
        previous_action,
        int(model.get("featureSize", 16)),
        str(model.get("observationMode", "current")),
    )


def live_dev_evaluation(environment: UnityFileEnvironment, weights: np.ndarray, bias: np.ndarray, model, max_steps: int) -> dict[str, object]:
    episodes = []
    for scenario in (12, 13, 14):
        for start_index in (0, 999):
            response = environment.reset(start_index, scenario_index=scenario, regenerate_trajectory=True)
            previous_image = None
            previous_action = 0
            reversals = 0
            last_nonstop = 0
            started = time.perf_counter()
            for step in range(max_steps):
                current_image = Path(str(response["imagePath"]))
                features = policy_features(response, previous_image, previous_action, model)
                probabilities = stable_softmax(features @ weights + bias)
                action = int(INDEX_TO_ACTION[int(np.argmax(probabilities))])
                if action and last_nonstop and action != last_nonstop:
                    reversals += 1
                if action:
                    last_nonstop = action
                previous_image = current_image
                previous_action = action
                response = environment.step(action)
                if bool(response["done"]):
                    break
            episodes.append({
                "scenarioIndex": scenario,
                "startIndex": start_index,
                "steps": step + 1,
                "explicitStopSuccess": bool(response.get("explicitStopSuccess", False)),
                "regionEntered": bool(response.get("regionEntered", False)),
                "constraintViolation": bool(response.get("constraintViolation", False)),
                "reversals": reversals,
                "loopLatencyMs": (time.perf_counter() - started) * 1000.0 / (step + 1),
            })
    return {
        "episodes": episodes,
        "explicitStopSuccessRate": float(np.mean([episode["explicitStopSuccess"] for episode in episodes])),
        "regionEnteredRate": float(np.mean([episode["regionEntered"] for episode in episodes])),
        "constraintViolations": int(sum(episode["constraintViolation"] for episode in episodes)),
        "meanSteps": float(np.mean([episode["steps"] for episode in episodes])),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="On-policy clipped PPO update through live Unity image observations")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--bc", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/bc_pixel_current_diverse_v2.json")
    parser.add_argument("--episodes", type=int, default=30)
    parser.add_argument("--max-steps", type=int, default=30)
    parser.add_argument("--eval-interval", type=int, default=10)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--gamma", type=float, default=0.95)
    parser.add_argument("--clip", type=float, default=0.15)
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/ppo_live_final_diverse_v2.json")
    parser.add_argument("--best-output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/ppo_live_best_dev_diverse_v2.json")
    args = parser.parse_args()
    rng = np.random.default_rng(args.seed)
    model = json.loads(args.bc.read_text(encoding="utf-8"))
    if model.get("experimentId") != args.experiment_id:
        raise SystemExit("BC checkpoint experiment does not match --experiment-id")
    weights = np.asarray(model["weights"], dtype=np.float32)
    bias = np.asarray(model["bias"], dtype=np.float32)
    environment = UnityFileEnvironment(timeout=20.0)
    episode_reports: list[dict[str, object]] = []
    dev_reports: list[dict[str, object]] = []
    best_score = -1.0
    best_weights = weights.copy()
    best_bias = bias.copy()
    started_training = time.perf_counter()

    for episode in range(args.episodes):
        scenario = episode % 12
        start_index = 0 if episode % 2 == 0 else 999
        response = environment.reset(start_index, scenario_index=scenario, regenerate_trajectory=True)
        features_batch: list[np.ndarray] = []
        actions: list[int] = []
        old_action_probabilities: list[float] = []
        rewards: list[float] = []
        previous_image = None
        previous_action = 0
        violation = False
        for _ in range(args.max_steps):
            current_image = Path(str(response["imagePath"]))
            features = policy_features(response, previous_image, previous_action, model)
            probabilities = stable_softmax(features @ weights + bias)
            action_index = int(rng.choice(3, p=probabilities))
            action = int(INDEX_TO_ACTION[action_index])
            previous_image = current_image
            previous_action = action
            response = environment.step(action)
            features_batch.append(features)
            actions.append(action_index)
            old_action_probabilities.append(float(probabilities[action_index]))
            rewards.append(float(response["reward"]))
            violation = violation or bool(response["constraintViolation"])
            if bool(response["done"]):
                break

        x = np.stack(features_batch)
        action_array = np.asarray(actions, dtype=np.int64)
        old = np.asarray(old_action_probabilities, dtype=np.float32)
        advantages = discounted_returns(rewards, args.gamma)
        advantages = (advantages - advantages.mean()) / (advantages.std() + 1e-6)
        for _ in range(4):
            probabilities = stable_softmax(x @ weights + bias)
            selected = probabilities[np.arange(len(x)), action_array]
            ratio = selected / np.clip(old, 1e-6, 1.0)
            clipped_out = ((advantages > 0) & (ratio > 1.0 + args.clip)) | ((advantages < 0) & (ratio < 1.0 - args.clip))
            signal = np.where(clipped_out, 0.0, advantages / np.clip(old, 1e-6, 1.0))
            one_hot = np.eye(3, dtype=np.float32)[action_array]
            gradient_logits = -(one_hot - probabilities) * signal[:, None] / len(x)
            weights -= args.lr * (x.T @ gradient_logits + 1e-4 * weights)
            bias -= args.lr * gradient_logits.sum(axis=0)
        episode_reports.append({
            "episode": episode,
            "scenarioIndex": scenario,
            "startIndex": start_index,
            "configurationFamily": response.get("configurationFamily"),
            "steps": len(rewards),
            "return": float(sum(rewards)),
            "explicitStopSuccess": bool(response.get("explicitStopSuccess", False)),
            "regionEntered": bool(response.get("regionEntered", False)),
            "constraintViolation": violation,
        })

        if (episode + 1) % args.eval_interval == 0 or episode + 1 == args.episodes:
            dev = live_dev_evaluation(environment, weights, bias, model, args.max_steps)
            dev["afterEpisode"] = episode + 1
            dev_reports.append(dev)
            score = float(dev["explicitStopSuccessRate"])
            if score > best_score:
                best_score = score
                best_weights = weights.copy()
                best_bias = bias.copy()

    common = {
        "schemaVersion": 2,
        "algorithm": "on-policy clipped PPO objective with Monte-Carlo returns; no learned value function",
        "experimentId": args.experiment_id,
        "sourceBcCheckpoint": str(args.bc),
        "trainingSeed": args.seed,
        "trainingEpisodes": episode_reports,
        "developmentEvaluations": dev_reports,
        "hyperparameters": {"gamma": args.gamma, "clip": args.clip, "learningRate": args.lr, "updateEpochs": 4},
        "elapsedSeconds": time.perf_counter() - started_training,
        "testSetInspected": False,
    }
    final_model = dict(model)
    final_model.update(common | {"model": "pixel16-bc-live-ppo-final-diverse-v2", "weights": weights.tolist(), "bias": bias.tolist(), "checkpointRole": "final"})
    best_model = dict(model)
    best_model.update(common | {"model": "pixel16-bc-live-ppo-best-dev-diverse-v2", "weights": best_weights.tolist(), "bias": best_bias.tolist(), "checkpointRole": "best-development", "bestDevExplicitStopSuccessRate": best_score})
    save_json(args.output, final_model)
    save_json(args.best_output, best_model)
    print(json.dumps({"finalCheckpoint": str(args.output), "bestCheckpoint": str(args.best_output), "bestDevExplicitStopSuccessRate": best_score, "developmentEvaluations": dev_reports}, indent=2))


if __name__ == "__main__":
    main()
