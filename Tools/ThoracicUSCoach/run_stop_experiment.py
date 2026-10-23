from __future__ import annotations

import argparse
import hashlib
import json
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from cached_env import CachedEnvironment, CachedTrajectory
from coach_data import model_probabilities, request_features, save_json
from contract_v3 import EXPERIMENT_ID, oracle_action
from stop_policy import (
    STOP_EXPERIMENT_ID,
    STOP_MODEL_SCHEMA,
    binary_metrics,
    context_fingerprint,
    feature_matrix,
    fit_binary_linear,
    fit_binary_mlp,
    fit_global_normalization,
    load_contexts,
    request_stop_features,
    stop_probabilities,
    training_weights,
    two_stage_action,
)


CANDIDATES = (
    {
        "name": "current_linear_zscore",
        "observation": "current",
        "layout": "basic",
        "preprocessing": "per-image-zscore",
        "architecture": "linear",
        "epochs": 350,
        "lr": 0.03,
    },
    {
        "name": "history_linear_zscore_corrected",
        "observation": "history",
        "layout": "basic",
        "preprocessing": "per-image-zscore",
        "architecture": "linear",
        "epochs": 350,
        "lr": 0.03,
    },
    {
        "name": "history_mlp32_zscore_diff",
        "observation": "history",
        "layout": "diff-stats",
        "preprocessing": "per-image-zscore",
        "architecture": "mlp",
        "hidden": 32,
        "epochs": 18,
        "lr": 0.001,
    },
    {
        "name": "history_mlp32_global_diff",
        "observation": "history",
        "layout": "diff-stats",
        "preprocessing": "global-pixel",
        "architecture": "mlp",
        "hidden": 32,
        "epochs": 18,
        "lr": 0.001,
    },
    {
        "name": "history_mlp32_global_noise",
        "observation": "history",
        "layout": "diff-stats",
        "preprocessing": "global-pixel",
        "architecture": "mlp",
        "hidden": 32,
        "epochs": 18,
        "lr": 0.001,
        "noiseStd": 0.0019,
    },
    {
        "name": "current_mlp64_structural",
        "observation": "current",
        "layout": "structural-basic",
        "preprocessing": "structural-zscore-v1",
        "architecture": "mlp",
        "hidden": 64,
        "epochs": 24,
        "lr": 0.001,
    },
    {
        "name": "history_mlp32_structural_diff",
        "observation": "history",
        "layout": "structural-diff-stats",
        "preprocessing": "structural-zscore-v1",
        "architecture": "mlp",
        "hidden": 32,
        "epochs": 16,
        "lr": 0.001,
    },
)


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def balanced_starts(trajectory: CachedTrajectory) -> list[dict[str, object]]:
    targets = [index for index, value in enumerate(trajectory.acceptable) if value]
    if not targets:
        return []
    exact = set(targets)
    acquisition_candidates: list[tuple[str, int]] = [
        ("endpoint-low", 0),
        ("endpoint-high", len(trajectory.frames) - 1),
    ]
    neighbors = [
        candidate
        for target in targets
        for candidate in (target - 1, target + 1)
        if 0 <= candidate < len(trajectory.frames) and candidate not in exact
    ]
    if neighbors:
        acquisition_candidates.append(("immediately-outside", neighbors[0]))
    far = max(
        (index for index in range(len(trajectory.frames)) if index not in exact),
        key=lambda index: min(abs(index - target) for target in targets),
    )
    acquisition_candidates.append(("far", far))
    unique = []
    used = set()
    for name, index in acquisition_candidates:
        if index in exact or index in used:
            continue
        unique.append({"name": name, "index": index, "initiallyAcceptable": False})
        used.add(index)
        if len(unique) == 3:
            break
    if len(unique) < 3:
        for index in range(len(trajectory.frames)):
            if index not in exact and index not in used:
                unique.append({"name": f"fill-{index:03d}", "index": index, "initiallyAcceptable": False})
                used.add(index)
                if len(unique) == 3:
                    break
    unique.append(
        {
            "name": "inside",
            "index": targets[len(targets) // 2],
            "initiallyAcceptable": True,
        }
    )
    return unique


def split_development(
    trajectories: list[CachedTrajectory], calibration_count: int, check_count: int
) -> tuple[list[CachedTrajectory], list[CachedTrajectory], list[CachedTrajectory]]:
    eligible = [trajectory for trajectory in trajectories if len(balanced_starts(trajectory)) == 4]
    ranked = sorted(
        eligible,
        key=lambda item: hashlib.sha256(
            f"{STOP_EXPERIMENT_ID}:{item.configuration_family}".encode()
        ).hexdigest(),
    )
    if len(ranked) < calibration_count + check_count:
        raise ValueError("Not enough development trajectories for disjoint calibration/check")
    return (
        ranked[:calibration_count],
        ranked[calibration_count : calibration_count + check_count],
        ranked[calibration_count + check_count :],
    )


def contexts_for_trajectories(contexts, trajectories):
    sweeps = {trajectory.sweep_id for trajectory in trajectories}
    return [context for context in contexts if context.sweep_id in sweeps]


def run_cached(
    trajectories: list[CachedTrajectory],
    navigation_model: dict[str, object],
    *,
    stop_model: dict[str, object] | None = None,
    navigation_stop_threshold: float | None = None,
    max_steps: int = 35,
) -> dict[str, object]:
    episodes = []
    for trajectory in trajectories:
        for start in balanced_starts(trajectory):
            environment = CachedEnvironment(trajectory, int(start["index"]), max_steps=max_steps)
            previous_image = None
            previous_action = 0
            region_entered = bool(environment.frame.acceptable)
            wrong_before = wrong_after = wrong_direction = reversals = boundary = 0
            first_entry_stop = False
            previous_movement = 0
            stop_scores = []
            success = False
            terminal_action = None
            for step in range(max_steps):
                current = environment.frame.image
                nav_features = request_features(
                    current,
                    previous_image,
                    previous_action,
                    int(navigation_model.get("featureSize", 16)),
                    str(navigation_model.get("observationMode", "current")),
                )
                nav_probability = np.asarray(model_probabilities(navigation_model, nav_features))
                if stop_model is not None:
                    detector_features = request_stop_features(
                        stop_model, current, previous_image, previous_action
                    )
                    action, stop_score, _ = two_stage_action(
                        navigation_model, stop_model, nav_features, detector_features
                    )
                elif navigation_stop_threshold is not None:
                    stop_score = float(nav_probability[1])
                    action = 0 if stop_score >= navigation_stop_threshold else (-1 if nav_probability[0] >= nav_probability[2] else 1)
                else:
                    stop_score = float(nav_probability[1])
                    action = int((-1, 0, 1)[int(np.argmax(nav_probability))])
                stop_scores.append(stop_score)
                was_inside = bool(environment.frame.acceptable)
                first_target_decision = was_inside and not region_entered
                if was_inside and step == 0:
                    first_target_decision = True
                if action == 0 and first_target_decision:
                    first_entry_stop = True
                intended = oracle_action(trajectory.acceptable, environment.index, trajectory.step_count)
                if action != 0 and intended != 0 and action != intended:
                    wrong_direction += 1
                if action != 0 and previous_movement and action != previous_movement:
                    reversals += 1
                if action != 0:
                    previous_movement = action
                previous_index = environment.index
                result = environment.step(action)
                boundary += int(action != 0 and result.index == previous_index)
                if action == 0 and not result.explicit_stop_success:
                    if region_entered:
                        wrong_after += 1
                    else:
                        wrong_before += 1
                region_entered |= bool(trajectory.acceptable[result.index])
                previous_image = current
                previous_action = action
                if result.done:
                    success = bool(result.explicit_stop_success)
                    terminal_action = action
                    break
            initially = bool(start["initiallyAcceptable"])
            episodes.append(
                {
                    "sweepId": trajectory.sweep_id,
                    "family": trajectory.configuration_family,
                    "start": start["name"],
                    "startIndex": start["index"],
                    "initiallyAcceptable": initially,
                    "success": success,
                    "recognitionSuccess": initially and success,
                    "immediateRecognitionSuccess": initially and success and len(stop_scores) == 1,
                    "acquisitionSuccess": (not initially) and success,
                    "wrongStopBeforeEntry": wrong_before,
                    "wrongStopAfterEntry": wrong_after,
                    "firstDecisionWrongStop": (not initially) and terminal_action == 0 and len(stop_scores) == 1 and not success,
                    "firstTargetEntryStop": first_entry_stop,
                    "regionEnteredWithoutCorrectStop": region_entered and not success,
                    "wrongMovementDirection": wrong_direction,
                    "reversals": reversals,
                    "boundaryActions": boundary,
                    "timeout": terminal_action is None,
                    "steps": len(stop_scores),
                    "stopScoreMax": float(max(stop_scores)),
                }
            )
    recognition = [episode for episode in episodes if episode["initiallyAcceptable"]]
    acquisition = [episode for episode in episodes if not episode["initiallyAcceptable"]]
    return {
        "episodes": episodes,
        "counts": {
            "episodes": len(episodes),
            "recognitionSuccess": sum(item["recognitionSuccess"] for item in recognition),
            "recognitionTotal": len(recognition),
            "immediateRecognitionSuccess": sum(item["immediateRecognitionSuccess"] for item in recognition),
            "acquisitionSuccess": sum(item["acquisitionSuccess"] for item in acquisition),
            "acquisitionTotal": len(acquisition),
            "wrongStopBeforeEntry": sum(item["wrongStopBeforeEntry"] for item in episodes),
            "wrongStopAfterEntry": sum(item["wrongStopAfterEntry"] for item in episodes),
            "firstDecisionWrongStop": sum(item["firstDecisionWrongStop"] for item in episodes),
            "regionEnteredWithoutCorrectStop": sum(item["regionEnteredWithoutCorrectStop"] for item in episodes),
            "wrongMovementDirection": sum(item["wrongMovementDirection"] for item in episodes),
            "reversals": sum(item["reversals"] for item in episodes),
            "boundaryActions": sum(item["boundaryActions"] for item in episodes),
            "timeouts": sum(item["timeout"] for item in episodes),
        },
    }


def selection_key(result: dict[str, object]) -> tuple:
    counts = result["counts"]
    recognition_rate = counts["recognitionSuccess"] / max(1, counts["recognitionTotal"])
    eligible = recognition_rate >= 0.95
    return (
        int(eligible),
        counts["acquisitionSuccess"] if eligible else counts["recognitionSuccess"],
        -counts["wrongStopBeforeEntry"],
        -counts["wrongStopAfterEntry"],
        counts["immediateRecognitionSuccess"],
        -counts["timeouts"],
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Corrected two-stage STOP experiment on train/development only")
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--dataset-run-id", default="main_cpu_v3_20261022")
    parser.add_argument("--navigation-checkpoint", type=Path, required=True)
    parser.add_argument("--seed", type=int, default=13)
    parser.add_argument("--feature-size", type=int, default=16)
    parser.add_argument("--calibration-trajectories", type=int, default=18)
    parser.add_argument("--check-trajectories", type=int, default=18)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument(
        "--candidate",
        action="append",
        choices=tuple(item["name"] for item in CANDIDATES),
        help="Run only the named candidate(s); default runs the complete declared set.",
    )
    args = parser.parse_args()
    root = args.root.resolve()
    output_dir = args.output_dir.resolve()
    checkpoint_dir = output_dir / "checkpoints"
    checkpoint_dir.mkdir(parents=True, exist_ok=True)
    navigation_model = json.loads(args.navigation_checkpoint.read_text(encoding="utf-8"))

    train_trajectories, train_contexts_all = load_contexts(
        root=root, dataset_run_id=args.dataset_run_id, split="train"
    )
    dev_trajectories, dev_contexts = load_contexts(
        root=root, dataset_run_id=args.dataset_run_id, split="dev"
    )
    calibration, check, unused = split_development(
        dev_trajectories, args.calibration_trajectories, args.check_trajectories
    )
    split_manifest = {
        "schemaVersion": 1,
        "experimentRunId": STOP_EXPERIMENT_ID,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "sourceSplits": ["train", "dev"],
        "heldoutOrStressAccessed": False,
        "selectionPolicy": "Threshold and candidate ranking use calibration only; check is read once after each frozen candidate; no final split is accessed.",
        "calibrationSweepIds": [item.sweep_id for item in calibration],
        "checkSweepIds": [item.sweep_id for item in check],
        "unusedDevelopmentSweepIds": [item.sweep_id for item in unused],
    }
    save_json(output_dir / "development_split_manifest.json", split_manifest)
    calibration_contexts = contexts_for_trajectories(dev_contexts, calibration)
    check_contexts = contexts_for_trajectories(dev_contexts, check)
    global_normalization = fit_global_normalization(
        (context.current_image for context in train_contexts_all), args.feature_size
    )

    baseline = {
        "threeActionArgmax": run_cached(check, navigation_model),
        "threeActionThreshold0_65": run_cached(
            check, navigation_model, navigation_stop_threshold=0.65
        ),
    }
    reports = []
    specifications = [
        item for item in CANDIDATES if not args.candidate or item["name"] in set(args.candidate)
    ]
    for specification in specifications:
        started = time.perf_counter()
        observation = str(specification["observation"])
        train_contexts = (
            [context for context in train_contexts_all if context.reset]
            if observation == "current"
            else train_contexts_all
        )
        normalization = (
            global_normalization if str(specification["preprocessing"]).startswith("global") else None
        )
        matrix_kwargs = {
            "size": args.feature_size,
            "observation": observation,
            "preprocessing": str(specification["preprocessing"]),
            "layout": str(specification["layout"]),
            "normalization": normalization,
        }
        x_train = feature_matrix(train_contexts, **matrix_kwargs)
        y_train = np.asarray([context.acceptable for context in train_contexts], dtype=np.float32)
        weights = training_weights(train_contexts)
        noise_std = float(specification.get("noiseStd", 0.0))
        if noise_std > 0.0:
            x_noise = feature_matrix(
                train_contexts,
                **matrix_kwargs,
                seed=args.seed + 10_000,
                noise_std=noise_std,
            )
            x_train = np.concatenate((x_train, x_noise))
            y_train = np.concatenate((y_train, y_train))
            weights = np.concatenate((weights, weights))
        if specification["architecture"] == "linear":
            parameters, losses = fit_binary_linear(
                x_train,
                y_train,
                weights,
                epochs=int(specification["epochs"]),
                lr=float(specification["lr"]),
                seed=args.seed,
            )
        else:
            parameters, losses = fit_binary_mlp(
                x_train,
                y_train,
                weights,
                hidden_size=int(specification["hidden"]),
                epochs=int(specification["epochs"]),
                lr=float(specification["lr"]),
                seed=args.seed,
            )
        model = {
            "schemaVersion": 1,
            "schema": STOP_MODEL_SCHEMA,
            "model": specification["name"],
            "experimentRunId": STOP_EXPERIMENT_ID,
            "experimentId": EXPERIMENT_ID,
            "datasetRunId": args.dataset_run_id,
            "environmentContractVersion": navigation_model["environmentContractVersion"],
            "observationSchema": navigation_model["observationSchema"],
            "targetSemantics": navigation_model["targetSemantics"],
            "task": "binary STOP acceptance on valid pre-decision states",
            "observationMode": observation,
            "featureSize": args.feature_size,
            "featureLayout": specification["layout"],
            "preprocessing": specification["preprocessing"],
            "normalization": normalization,
            "stopThreshold": 0.5,
            "datasetFingerprint": context_fingerprint(train_contexts),
            "historySemantics": "reset previous=current with non-executed STOP sentinel; movement history is previous/current plus actually executed action; no post-terminal rows",
            "training": {
                "seed": args.seed,
                "epochs": specification["epochs"],
                "learningRate": specification["lr"],
                "hardNegativeWeight": 2.0,
                "rendererNoiseStdRaw01": noise_std,
                "rows": len(train_contexts),
                "effectiveRows": len(x_train),
                "finalLoss": losses[-1],
                "classBalancedScoresAreCalibratedProbabilities": False,
                "heldoutOrStressUsed": False,
            },
            **parameters,
        }
        calibration_x = feature_matrix(calibration_contexts, **matrix_kwargs)
        calibration_y = np.asarray([context.acceptable for context in calibration_contexts])
        calibration_scores = np.asarray(stop_probabilities(model, calibration_x))
        threshold_grid = sorted(
            set(
                [round(value, 3) for value in np.arange(0.05, 0.951, 0.025)]
                + [round(float(value), 3) for value in np.quantile(calibration_scores, np.linspace(0.05, 0.95, 19))]
            )
        )
        threshold_runs = []
        for threshold in threshold_grid:
            model["stopThreshold"] = threshold
            result = run_cached(calibration, navigation_model, stop_model=model)
            threshold_runs.append({"threshold": threshold, "cachedEpisodes": result, "selectionKey": selection_key(result)})
        selected = max(threshold_runs, key=lambda item: tuple(item["selectionKey"]))
        model["stopThreshold"] = float(selected["threshold"])
        check_x = feature_matrix(check_contexts, **matrix_kwargs)
        check_y = np.asarray([context.acceptable for context in check_contexts])
        check_scores = np.asarray(stop_probabilities(model, check_x))
        cached_check = run_cached(check, navigation_model, stop_model=model)
        elapsed = time.perf_counter() - started
        model["calibration"] = {
            "thresholdSelection": "cached closed-loop acquisition under recognition >=95%, then premature STOP minimization",
            "selectedThreshold": model["stopThreshold"],
            "classification": binary_metrics(calibration_y, calibration_scores, model["stopThreshold"]),
            "cachedEpisodes": selected["cachedEpisodes"],
        }
        model["developmentCheck"] = {
            "usedForSelection": False,
            "classification": binary_metrics(check_y, check_scores, model["stopThreshold"]),
            "cachedEpisodes": cached_check,
        }
        model["compute"] = {
            "elapsedSeconds": elapsed,
            "featureMatrixBytes": int(x_train.nbytes),
            "trainTrajectories": len(train_trajectories),
            "calibrationTrajectories": len(calibration),
            "checkTrajectories": len(check),
        }
        checkpoint = checkpoint_dir / f"{specification['name']}_seed{args.seed}.json"
        save_json(checkpoint, model)
        reports.append(
            {
                "name": specification["name"],
                "checkpoint": str(checkpoint),
                "checkpointSha256": sha256_file(checkpoint),
                "threshold": model["stopThreshold"],
                "calibration": model["calibration"],
                "check": model["developmentCheck"],
                "compute": model["compute"],
            }
        )
        print(
            json.dumps(
                {
                    "candidate": specification["name"],
                    "threshold": model["stopThreshold"],
                    "checkClassification": model["developmentCheck"]["classification"],
                    "checkCached": cached_check["counts"],
                    "elapsedSeconds": elapsed,
                },
                indent=2,
            ),
            flush=True,
        )
    report = {
        "schemaVersion": 1,
        "experimentRunId": STOP_EXPERIMENT_ID,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "Stage B supervised STOP; cached development check",
        "sourceDatasetRunId": args.dataset_run_id,
        "navigationCheckpoint": str(args.navigation_checkpoint.resolve()),
        "navigationCheckpointSha256": sha256_file(args.navigation_checkpoint),
        "seed": args.seed,
        "heldoutOrStressAccessed": False,
        "baselineCheck": baseline,
        "candidates": reports,
    }
    save_json(output_dir / f"offline_stop_experiment_seed{args.seed}.json", report)
    print(json.dumps({"output": str(output_dir), "baseline": {key: value["counts"] for key, value in baseline.items()}}, indent=2))


if __name__ == "__main__":
    main()
