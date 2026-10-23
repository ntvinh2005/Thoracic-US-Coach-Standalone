from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image

from cached_env import CachedEnvironment, cached_features, load_cached_trajectories, start_indices
from coach_data import INDEX_TO_ACTION, model_probabilities, project_root, request_features, save_json
from protocol_v3 import validate_manifest, validate_model
from unity_env import UnityFileEnvironment


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pixels(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(
            image.convert("L").resize((32, 32), Image.Resampling.BILINEAR), dtype=np.float32
        ) / 255.0


def rms(left: Path, right: Path) -> float:
    return float(np.sqrt(np.mean((pixels(left) - pixels(right)) ** 2)))


def canonical_starts(acceptable: list[int], count: int) -> list[int]:
    starts = [0, count - 1]
    if acceptable:
        exact = set(acceptable)
        starts.append(acceptable[len(acceptable) // 2])
        neighbors = [candidate for target in acceptable for candidate in (target - 1, target + 1) if 0 <= candidate < count and candidate not in exact]
        if neighbors:
            starts.append(neighbors[0])
        starts.append(max(range(count), key=lambda index: min(abs(index - target) for target in acceptable)))
    return list(dict.fromkeys(starts))


def scenario_from_sweep(payload: dict[str, object], template: dict[str, object], max_steps: int) -> dict[str, object]:
    frames = sorted(payload["frames"], key=lambda item: int(item["stepIndex"]))
    acceptable = [int(item["stepIndex"]) for item in frames if item["targetAcceptable"]]
    poses = []
    for item in frames:
        pose = item["pose"]
        poses.append({
            "position": pose["position"],
            "rotation": pose["rotation"],
            "contactPoint": pose["contactPoint"],
            "surfaceNormal": pose["surfaceNormal"],
            "caudalOffsetMm": pose["caudalOffsetMm"],
            "lateralOffsetMm": pose["lateralOffsetMm"],
            "yawDeg": pose["yawDeg"],
            "tiltDeg": pose["tiltDeg"],
        })
    path_mm = [float(item["pathMm"]) for item in frames]
    reference_scores = [float(item["score"]["total"]) for item in frames]
    return {
        "schemaVersion": 1,
        "environmentContractVersion": payload["environmentContractVersion"],
        "observationSchema": payload["observationSchema"],
        "targetSemantics": payload["targetSemantics"],
        "actionOrder": [-1, 0, 1],
        "configurationSha256": payload["configurationSha256"],
        "labelsSha256": payload["labelsSha256"],
        "trajectorySeed": int(payload["trajectory"]["randomSeed"]),
        "trajectory": payload["trajectory"],
        "poses": poses,
        "pathMm": path_mm,
        "referenceScores": reference_scores,
        "acceptableIndices": acceptable,
        "canonicalStarts": canonical_starts(acceptable, len(frames)),
        "unityVersion": template["unityVersion"],
        "scene": template["scene"],
        "renderWidth": template["renderWidth"],
        "renderHeight": template["renderHeight"],
        "colorSpace": template["colorSpace"],
        "rendererFullySeeded": False,
        "actionStepCount": int(template["actionStepCount"]),
        "actionStepMm": float(template["actionStepMm"]),
        "pathSpacingMm": float(abs(path_mm[-1] - path_mm[0]) / max(1, len(path_mm) - 1)),
        "episodeBudget": max_steps,
        "maxPenetrationMm": float(template["maxPenetrationMm"]),
        "acceptableRelativeToBest": float(template["acceptableRelativeToBest"]),
        "acceptableStoppingErrorMm": float(template["acceptableStoppingErrorMm"]),
        "captureCaudalPatchHalfExtentMm": float(template["captureCaudalPatchHalfExtentMm"]),
        "captureLateralPatchHalfExtentMm": float(template["captureLateralPatchHalfExtentMm"]),
        "referenceScoreFloor": float(payload["trajectory"]["referenceScoreFloor"]),
    }


def model_decision(model: dict[str, object], features: np.ndarray) -> tuple[int, np.ndarray]:
    probabilities = model_probabilities(model, features)
    return int(INDEX_TO_ACTION[int(np.argmax(probabilities))]), probabilities


def cached_rollout(trajectory, start: int, model: dict[str, object], max_steps: int) -> dict[str, object]:
    environment = CachedEnvironment(trajectory, start, max_steps)
    previous = None
    previous_action = 0
    actions = []
    stop_scores = []
    last = None
    history = []
    for step in range(max_steps):
        current = environment.frame.image
        features = cached_features(
            current, previous, previous_action,
            size=int(model.get("featureSize", 16)), observation=str(model.get("observationMode", "current")),
        )
        action, probabilities = model_decision(model, features)
        before = environment.index
        last = environment.step(action)
        actions.append(action)
        stop_scores.append(float(probabilities[1]))
        history.append({"step": step, "before": before, "after": last.index, "action": action, "reward": last.reward})
        previous, previous_action = current, action
        if last.done:
            break
    return {
        "actions": actions,
        "stopScores": stop_scores,
        "history": history,
        "success": bool(last and last.explicit_stop_success),
        "terminated": bool(last and last.terminated),
        "truncated": bool(last and last.truncated),
        "finalIndex": int(last.index if last else start),
    }


def live_rollout(environment, scenario, start: int, model: dict[str, object], max_steps: int) -> dict[str, object]:
    response = environment.reset(start, max_steps=max_steps, frozen_scenario=scenario)
    previous = None
    previous_action = 0
    actions = []
    stop_scores = []
    history = []
    missing_history_images = 0
    for step in range(max_steps):
        current = Path(str(response["imagePath"]))
        if previous is not None and not previous.is_file():
            missing_history_images += 1
        features = request_features(
            current, previous, previous_action,
            int(model.get("featureSize", 16)), str(model.get("observationMode", "current")),
        )
        action, probabilities = model_decision(model, features)
        before = int(response["pathIndex"])
        next_response = environment.step(action)
        actions.append(action)
        stop_scores.append(float(probabilities[1]))
        history.append({
            "step": step,
            "before": before,
            "after": int(next_response["pathIndex"]),
            "action": action,
            "reward": float(next_response["reward"]),
        })
        previous, previous_action, response = current, action, next_response
        if bool(response["done"]):
            break
    return {
        "actions": actions,
        "stopScores": stop_scores,
        "history": history,
        "success": bool(response["explicitStopSuccess"]),
        "terminated": bool(response["terminated"]),
        "truncated": bool(response["truncated"]),
        "finalIndex": int(response["pathIndex"]),
        "missingHistoryImages": missing_history_images,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Actual captured development cache versus live Unity diagnostic")
    parser.add_argument("--dataset-run-id", default="main_cpu_v3_20261022")
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--renderer-template-manifest", type=Path, required=True)
    parser.add_argument("--bc", type=Path, required=True)
    parser.add_argument("--ppo", type=Path, required=True)
    parser.add_argument("--trajectories", type=int, default=3)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    template_manifest = json.loads(args.renderer_template_manifest.read_text(encoding="utf-8"))
    validate_manifest(template_manifest, expected_group="development", selection_allowed=True)
    template = dict(template_manifest["scenarios"][0]["scenario"])
    models = {
        "BC_history_seed13": json.loads(args.bc.read_text(encoding="utf-8")),
        "PPO_best_seed13": json.loads(args.ppo.read_text(encoding="utf-8")),
    }
    for model in models.values():
        validate_model(model)

    trajectories = load_cached_trajectories(dataset_run_id=args.dataset_run_id, split="dev")
    selected = sorted(trajectories, key=lambda value: value.sweep_id)[: args.trajectories]
    manifest_paths = {
        path.parent.name: path
        for path in (root / "Artifacts" / "ThoracicUSCoach" / "dataset" / "dev").glob("*/sweep_manifest.json")
    }
    environment = UnityFileEnvironment(timeout=20.0)
    pose_probes = []
    fixed_sequences = []
    policy_rollouts = []

    for trajectory in selected:
        payload = json.loads(manifest_paths[trajectory.sweep_id].read_text(encoding="utf-8"))
        if payload.get("datasetRunId") != args.dataset_run_id:
            raise RuntimeError("Selected sweep does not belong to diagnostic source run")
        scenario = scenario_from_sweep(payload, template, args.max_steps)
        frames = sorted(payload["frames"], key=lambda item: int(item["stepIndex"]))

        # Exact stored files must produce identical features through both public
        # feature helpers. Fresh live renders are then compared at the same pose.
        probe_indices = canonical_starts(
            [int(item["stepIndex"]) for item in frames if item["targetAcceptable"]], len(frames)
        )
        for index in probe_indices:
            stored = trajectory.frames[index].image
            live = environment.reset(index, max_steps=args.max_steps, frozen_scenario=scenario)
            live_image = Path(str(live["imagePath"]))
            model_records = {}
            for name, model in models.items():
                size = int(model.get("featureSize", 16))
                observation = str(model.get("observationMode", "current"))
                cache_helper = cached_features(stored, stored, 0, size=size, observation=observation)
                request_helper = request_features(stored, stored, 0, size, observation)
                live_features = request_features(live_image, live_image, 0, size, observation)
                cached_action, cached_probabilities = model_decision(model, cache_helper)
                live_action, live_probabilities = model_decision(model, live_features)
                model_records[name] = {
                    "storedFeatureHelperMaxAbs": float(np.max(np.abs(cache_helper - request_helper))),
                    "storedVsLiveFeatureRms": float(np.sqrt(np.mean((cache_helper - live_features) ** 2))),
                    "storedProbabilities": [float(value) for value in cached_probabilities],
                    "liveProbabilities": [float(value) for value in live_probabilities],
                    "probabilityMaxAbs": float(np.max(np.abs(cached_probabilities - live_probabilities))),
                    "storedAction": cached_action,
                    "liveAction": live_action,
                    "stopNonStopFlip": (cached_action == 0) != (live_action == 0),
                }
            pose_probes.append({
                "sweepId": trajectory.sweep_id,
                "index": index,
                "storedImageSha256": sha256(stored),
                "liveImageSha256": sha256(live_image),
                "rawImageRms32": rms(stored, live_image),
                "storedAcceptable": trajectory.frames[index].acceptable,
                "liveAcceptable": bool(live["targetAcceptable"]),
                "pathIndexSynchronized": int(live["pathIndex"]) == index,
                "contactValid": bool(live["contactValid"]),
                "workspaceValid": bool(live["workspaceValid"]),
                "models": model_records,
            })

        sequence_definitions = ((-1, 1, -1, 1, 0), (1, -1, 1, -1, 0))
        start_map = start_indices(trajectory)
        starts = list(dict.fromkeys(start_map.values()))[:2]
        for sequence_index, (start, actions) in enumerate(zip(starts, sequence_definitions)):
            cached_environment = CachedEnvironment(trajectory, start, args.max_steps)
            live = environment.reset(start, max_steps=args.max_steps, frozen_scenario=scenario)
            cached_previous = live_previous = None
            previous_action = 0
            steps = []
            for step, action in enumerate(actions):
                cached_current = cached_environment.frame.image
                live_current = Path(str(live["imagePath"]))
                model_records = {}
                for name, model in models.items():
                    size = int(model.get("featureSize", 16))
                    observation = str(model.get("observationMode", "current"))
                    cache_features = cached_features(
                        cached_current, cached_previous, previous_action, size=size, observation=observation
                    )
                    stored_request_features = request_features(
                        cached_current, cached_previous, previous_action, size, observation
                    )
                    live_features = request_features(live_current, live_previous, previous_action, size, observation)
                    cached_action, cached_probabilities = model_decision(model, cache_features)
                    live_action, live_probabilities = model_decision(model, live_features)
                    model_records[name] = {
                        "cachedRequestFeatureMaxAbs": float(np.max(np.abs(cache_features - stored_request_features))),
                        "cachedVsLiveFeatureRms": float(np.sqrt(np.mean((cache_features - live_features) ** 2))),
                        "probabilityMaxAbs": float(np.max(np.abs(cached_probabilities - live_probabilities))),
                        "cachedArgmax": cached_action,
                        "liveArgmax": live_action,
                        "stopNonStopFlip": (cached_action == 0) != (live_action == 0),
                    }
                cached_result = cached_environment.step(action)
                next_live = environment.step(action)
                steps.append({
                    "step": step,
                    "executedAction": action,
                    "cachedIndex": cached_result.index,
                    "liveIndex": int(next_live["pathIndex"]),
                    "cachedReward": cached_result.reward,
                    "liveReward": float(next_live["reward"]),
                    "contractMatch": {
                        "index": cached_result.index == int(next_live["pathIndex"]),
                        "target": cached_result.target_acceptable == bool(next_live["targetAcceptable"]),
                        "terminated": cached_result.terminated == bool(next_live["terminated"]),
                        "truncated": cached_result.truncated == bool(next_live["truncated"]),
                        "reward": abs(cached_result.reward - float(next_live["reward"])) <= 1e-5,
                    },
                    "rawImageRms32Before": rms(cached_current, live_current),
                    "models": model_records,
                })
                cached_previous, live_previous = cached_current, live_current
                previous_action, live = action, next_live
                if cached_result.done or bool(live["done"]):
                    break
            fixed_sequences.append({
                "sweepId": trajectory.sweep_id,
                "sequenceIndex": sequence_index,
                "startIndex": start,
                "actions": list(actions),
                "steps": steps,
            })

        rollout_starts = list(dict.fromkeys(start_map.values()))[:2]
        for start in rollout_starts:
            for name, model in models.items():
                cached = cached_rollout(trajectory, start, model, args.max_steps)
                live = live_rollout(environment, scenario, start, model, args.max_steps)
                policy_rollouts.append({
                    "sweepId": trajectory.sweep_id,
                    "startIndex": start,
                    "model": name,
                    "cached": cached,
                    "live": live,
                    "actionSequencesEqual": cached["actions"] == live["actions"],
                    "outcomesEqual": (
                        cached["success"] == live["success"]
                        and cached["terminated"] == live["terminated"]
                        and cached["truncated"] == live["truncated"]
                    ),
                })

        print(json.dumps({
            "phase": "DIAGNOSTIC",
            "matchedCacheLiveSweep": trajectory.sweep_id,
            "completedSweeps": len({item["sweepId"] for item in pose_probes}),
        }), flush=True)

    all_model_records = [
        record
        for probe in pose_probes
        for record in probe["models"].values()
    ] + [
        record
        for sequence in fixed_sequences
        for step in sequence["steps"]
        for record in step["models"].values()
    ]
    contract_records = [comparison for sequence in fixed_sequences for step in sequence["steps"] for comparison in step["contractMatch"].values()]
    raw_rms = [probe["rawImageRms32"] for probe in pose_probes] + [
        step["rawImageRms32Before"] for sequence in fixed_sequences for step in sequence["steps"]
    ]
    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "sourceDatasetRunId": args.dataset_run_id,
        "sourceSplit": "dev",
        "heldoutOrStressAccessed": False,
        "scope": {
            "trajectories": len(selected),
            "poseProbes": len(pose_probes),
            "fixedActionSequences": len(fixed_sequences),
            "policyDrivenRollouts": len(policy_rollouts),
        },
        "summary": {
            "rawImageRms32Mean": float(np.mean(raw_rms)),
            "rawImageRms32Max": float(np.max(raw_rms)),
            "storedFeatureHelperMaxAbs": max(
                float(record.get("storedFeatureHelperMaxAbs", record.get("cachedRequestFeatureMaxAbs", 0.0)))
                for record in all_model_records
            ),
            "cachedVsLiveFeatureRmsMean": float(np.mean([
                float(record.get("storedVsLiveFeatureRms", record.get("cachedVsLiveFeatureRms", 0.0)))
                for record in all_model_records
            ])),
            "probabilityMaxAbsMax": max(float(record["probabilityMaxAbs"]) for record in all_model_records),
            "stopNonStopFlips": sum(bool(record["stopNonStopFlip"]) for record in all_model_records),
            "contractComparisons": len(contract_records),
            "contractFailures": sum(not bool(value) for value in contract_records),
            "policyDrivenActionSequenceDivergences": sum(not item["actionSequencesEqual"] for item in policy_rollouts),
            "policyDrivenOutcomeDivergences": sum(not item["outcomesEqual"] for item in policy_rollouts),
            "missingHistoryImages": sum(int(item["live"]["missingHistoryImages"]) for item in policy_rollouts),
        },
        "poseProbes": pose_probes,
        "fixedActionSequences": fixed_sequences,
        "policyDrivenRollouts": policy_rollouts,
        "limitations": [
            "This directly replays actual captured development trajectories; it does not access final data.",
            "Fresh Unity renders are stochastic, so exact PNG equality is not expected.",
            "Fixed-action comparisons isolate rendering/preprocessing; policy-driven rollouts measure compounding divergence.",
        ],
    }
    output = output_dir / "actual_cache_live_audit.json"
    save_json(output, report)
    print(json.dumps({"phase": "DIAGNOSTIC", "output": str(output), "summary": report["summary"]}, indent=2))


if __name__ == "__main__":
    main()
