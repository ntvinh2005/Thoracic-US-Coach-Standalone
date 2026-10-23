from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from cached_env import CachedEnvironment, cached_features, load_cached_trajectories, start_indices
from coach_data import INDEX_TO_ACTION, model_probabilities, project_root, save_json
from ppo_math import generalized_advantage_estimate, value_predict


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"


def file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def actor_difference(left: dict[str, object], right: dict[str, object]) -> dict[str, float]:
    left_weights = np.asarray(left["weights"], dtype=np.float64)
    right_weights = np.asarray(right["weights"], dtype=np.float64)
    left_bias = np.asarray(left["bias"], dtype=np.float64)
    right_bias = np.asarray(right["bias"], dtype=np.float64)
    return {
        "weightL2": float(np.linalg.norm(right_weights - left_weights)),
        "weightMaxAbs": float(np.max(np.abs(right_weights - left_weights))),
        "biasL2": float(np.linalg.norm(right_bias - left_bias)),
        "biasMaxAbs": float(np.max(np.abs(right_bias - left_bias))),
    }


def numeric_summary(values) -> dict[str, object]:
    array = np.asarray(list(values), dtype=np.float64)
    if not len(array):
        return {"count": 0}
    return {
        "count": len(array),
        "mean": float(np.mean(array)),
        "min": float(np.min(array)),
        "max": float(np.max(array)),
    }


def epoch_summary(updates) -> dict[str, object]:
    epochs = [epoch for update in updates for epoch in update.get("epochs", [])]
    return {
        key: numeric_summary(float(epoch[key]) for epoch in epochs if key in epoch)
        for key in ("entropyMean", "approxKlMean", "clipFraction", "ratioMean", "ratioMin", "ratioMax", "actorWeightGradientNorm", "valueLossBefore", "valueLossAfter")
    }


def cached_advantage_probe(trajectories, actor, critic, hyperparameters, episodes: int, seed: int) -> dict[str, object]:
    rng = np.random.default_rng(seed)
    categories = defaultdict(list)
    episode_reports = []
    gamma = float(hyperparameters["gamma"])
    gae_lambda = float(hyperparameters["gaeLambda"])
    max_steps = 35
    for episode_index in range(episodes):
        trajectory = trajectories[episode_index % len(trajectories)]
        starts = start_indices(trajectory)
        start_name = sorted(starts)[episode_index % len(starts)]
        environment = CachedEnvironment(trajectory, starts[start_name], max_steps)
        previous = None
        previous_action = 0
        features = []
        next_features = []
        rewards = []
        terminated = []
        truncated = []
        step_categories = []
        entropies = []
        region_entered = environment.frame.acceptable
        for _ in range(max_steps):
            current = environment.frame.image
            feature = cached_features(
                current, previous, previous_action,
                size=int(actor.get("featureSize", 16)), observation=str(actor.get("observationMode", "current")),
            )
            probabilities = model_probabilities(actor, feature)
            action_index = int(rng.choice(3, p=probabilities))
            action = int(INDEX_TO_ACTION[action_index])
            result = environment.step(action)
            destination = environment.frame.image
            next_feature = cached_features(
                destination, current, action,
                size=int(actor.get("featureSize", 16)), observation=str(actor.get("observationMode", "current")),
            )
            if action == 0:
                category = "correct-stop" if result.explicit_stop_success else "wrong-stop"
            elif result.reward > 0:
                category = "useful-movement-positive-reward"
            else:
                category = "nonpositive-movement"
            features.append(feature)
            next_features.append(next_feature)
            rewards.append(result.reward)
            terminated.append(result.terminated)
            truncated.append(result.truncated)
            step_categories.append(category)
            entropies.append(float(-np.sum(probabilities * np.log(np.clip(probabilities, 1e-8, 1.0)))))
            region_entered |= result.target_acceptable
            previous, previous_action = current, action
            if result.done:
                break
        x = np.stack(features)
        next_x = np.stack(next_features)
        advantages, _ = generalized_advantage_estimate(
            np.asarray(rewards), value_predict(x, critic), value_predict(next_x, critic),
            np.asarray(terminated), np.asarray(truncated), gamma, gae_lambda,
        )
        for category, advantage in zip(step_categories, advantages):
            categories[category].append(float(advantage))
        episode_reports.append({
            "sweepId": trajectory.sweep_id,
            "startCondition": start_name,
            "steps": len(rewards),
            "regionEntered": region_entered,
            "terminated": bool(terminated[-1]),
            "truncated": bool(truncated[-1]),
            "return": float(sum(rewards)),
            "entropyMean": float(np.mean(entropies)),
        })
    return {
        "label": "DIAGNOSTIC cached probe with final live critic; not reconstruction of saved training batches",
        "episodes": episodes,
        "categoryAdvantages": {
            category: {
                **numeric_summary(values),
                "positiveFraction": float(np.mean(np.asarray(values) > 0.0)),
                "negativeFraction": float(np.mean(np.asarray(values) < 0.0)),
            }
            for category, values in sorted(categories.items())
        },
        "regionEnteredEpisodes": sum(item["regionEntered"] for item in episode_reports),
        "terminatedEpisodes": sum(item["terminated"] for item in episode_reports),
        "truncatedEpisodes": sum(item["truncated"] for item in episode_reports),
        "episodeReports": episode_reports,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Saved PPO update/selection and bounded advantage diagnostic")
    parser.add_argument("--dataset-run-id", default="main_cpu_v3_20261022")
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--seeds", default="13,37,101,211,307")
    parser.add_argument("--advantage-episodes", type=int, default=24)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    checkpoint_dir = root / "Artifacts" / "ThoracicUSCoach" / "checkpoints" / args.dataset_run_id
    metric_dir = root / "Artifacts" / "ThoracicUSCoach" / "metrics" / args.dataset_run_id
    seed_reports = {}

    for seed in [int(value) for value in args.seeds.split(",")]:
        paths = {
            "bc": checkpoint_dir / f"bc_history_linear16_seed{seed}.json",
            "cached": checkpoint_dir / f"ppo_cached_history_seed{seed}.json",
            "cachedState": checkpoint_dir / f"ppo_cached_history_seed{seed}_state.json",
            "cachedMetrics": metric_dir / f"ppo_cached_history_seed{seed}.json",
            "liveFinal": checkpoint_dir / f"ppo_live_history_seed{seed}_final.json",
            "liveBest": checkpoint_dir / f"ppo_live_history_seed{seed}_best_dev.json",
            "liveState": checkpoint_dir / f"ppo_live_history_seed{seed}_state.json",
            "liveMetrics": metric_dir / f"ppo_live_history_seed{seed}.json",
        }
        missing = [str(path) for path in paths.values() if not path.is_file()]
        if missing:
            raise FileNotFoundError(f"Missing seed {seed} artifacts: {missing}")
        payloads = {name: json.loads(path.read_text(encoding="utf-8")) for name, path in paths.items()}
        live_metrics = payloads["liveMetrics"]
        cached_metrics = payloads["cachedMetrics"]
        live_state = payloads["liveState"]
        episodes = list(live_metrics["trainingEpisodes"])
        start_counts = Counter(str(item["startCondition"]) for item in episodes)
        development_tradeoff = [
            {
                "afterUpdate": int(item["afterUpdate"]),
                "candidate": str(item["candidate"]),
                "recognitionRate": float(item["recognitionRate"]),
                "acquisitionRate": float(item["acquisitionRate"]),
                "pooledRate": float(item["explicitStopSuccessRate"]),
                "wrongStop": int(item["rawCounts"]["wrongStop"]),
                "constraintViolations": int(item["rawCounts"]["constraintViolations"]),
                "selectionKey": list(item["selectionKey"]),
            }
            for item in live_metrics["developmentEvaluations"]
        ]
        seed_reports[str(seed)] = {
            "checkpointSha256": {name: file_sha256(path) for name, path in paths.items()},
            "actorDelta": {
                "bcToCached": actor_difference(payloads["bc"], payloads["cached"]),
                "cachedToLiveFinal": actor_difference(payloads["cached"], payloads["liveFinal"]),
                "cachedToSelectedBest": actor_difference(payloads["cached"], payloads["liveBest"]),
            },
            "cachedTraining": {
                "transitions": int(cached_metrics["actualTrainingTransitions"]),
                "updates": int(cached_metrics["completedUpdates"]),
                "actorGradientSteps": int(cached_metrics["completedUpdates"]) * int(cached_metrics["hyperparameters"]["updateEpochs"]),
                "criticOptimizerSteps": int(payloads["cachedState"]["criticOptimizerStep"]),
                "epochStatistics": epoch_summary(cached_metrics["updates"]),
            },
            "liveTraining": {
                "transitions": int(live_metrics["actualTrainingTransitions"]),
                "updates": int(live_metrics["completedUpdates"]),
                "actorGradientSteps": int(live_metrics["completedUpdates"]) * int(live_metrics["hyperparameters"]["updateEpochs"]),
                "criticOptimizerSteps": int(live_state["criticOptimizerStep"]),
                "criticFreshStartEvidence": {
                    "firstEpochValuePredictionMeanBefore": float(live_metrics["updates"][0]["epochs"][0]["valuePredictionMeanBefore"]),
                    "resumeWasRequiredToImportCritic": True,
                },
                "trainingEpisodes": len(episodes),
                "explicitStopSuccessEpisodes": sum(bool(item["explicitStopSuccess"]) for item in episodes),
                "falseStopEpisodes": sum(bool(item["falseStop"]) for item in episodes),
                "regionEnteredEpisodes": sum(bool(item["regionEntered"]) for item in episodes),
                "startConditionCounts": dict(sorted(start_counts.items())),
                "uniqueTrajectorySeeds": len({int(item["trajectorySeed"]) for item in episodes}),
                "epochStatistics": epoch_summary(live_metrics["updates"]),
            },
            "selection": {
                "selectedSource": str(live_metrics["selectedSource"]),
                "recognitionGate": 0.95,
                "developmentTradeoff": development_tradeoff,
            },
        }

    seed13_state = json.loads((checkpoint_dir / "ppo_live_history_seed13_state.json").read_text(encoding="utf-8"))
    seed13_actor = json.loads((checkpoint_dir / "ppo_live_history_seed13_final.json").read_text(encoding="utf-8"))
    seed13_metrics = json.loads((metric_dir / "ppo_live_history_seed13.json").read_text(encoding="utf-8"))
    trajectories = load_cached_trajectories(dataset_run_id=args.dataset_run_id, split="train")
    advantage_probe = cached_advantage_probe(
        trajectories,
        seed13_actor,
        np.asarray(seed13_state["criticParameters"], dtype=np.float64),
        seed13_metrics["hyperparameters"],
        args.advantage_episodes,
        13,
    )

    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "sourceDatasetRunId": args.dataset_run_id,
        "heldoutOrStressAccessed": False,
        "trainingPolicy": "stochastic sampling from actor probabilities",
        "evaluationPolicy": "deterministic argmax",
        "seedReports": seed_reports,
        "advantageProbeSeed13": advantage_probe,
        "startDistributionMismatch": {
            "liveTraining": "five named start-condition types sampled over generated trajectories",
            "fullDevelopmentEvaluation": "all 1,030 pose starts across 60 frozen geometries (170 initially acceptable, 860 acquisition)",
            "consequence": "training does not exhaust arbitrary interior pose resets used by full evaluation",
        },
        "interpretationLimits": [
            "Saved PPO summaries do not contain per-step advantages; the advantage probe is a new bounded cached diagnostic using the final critic.",
            "Actor-only transfer into a fresh live critic is documented behavior, not automatically classified as a bug.",
            "No heldout or stress artifact was loaded.",
        ],
    }
    output = output_dir / "ppo_training_audit.json"
    save_json(output, report)
    print(json.dumps({
        "phase": "DIAGNOSTIC",
        "output": str(output),
        "seedSummaries": {
            seed: {
                "cachedToLiveFinal": item["actorDelta"]["cachedToLiveFinal"],
                "cachedToSelectedBest": item["actorDelta"]["cachedToSelectedBest"],
                "liveTraining": item["liveTraining"],
                "selectedSource": item["selection"]["selectedSource"],
            }
            for seed, item in seed_reports.items()
        },
        "advantageProbe": advantage_probe,
    }, indent=2))


if __name__ == "__main__":
    main()
