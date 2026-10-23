from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, DEFAULT_DATASET_RUN_ID, DEFAULT_EXPERIMENT_ID, INDEX_TO_ACTION, classification_metrics, dataset_fingerprint, feature_matrix, load_frames, load_transitions, model_probabilities, project_root, request_features, save_json, transition_dataset_fingerprint, transition_feature_matrix


def replay_rollouts(frames, model) -> dict[str, object]:
    episodes: list[dict[str, object]] = []
    for sweep_id in sorted({frame.sweep_id for frame in frames}):
        sweep = sorted((frame for frame in frames if frame.sweep_id == sweep_id), key=lambda frame: frame.step_index)
        acceptable = [index for index, frame in enumerate(sweep) if frame.target_acceptable]
        if not acceptable:
            episodes.append({"sweep": sweep_id, "reachable": False, "excludedFromAcquisition": True})
            continue
        exact = set(acceptable)
        near = next((candidate for target in acceptable for candidate in (target - 1, target + 1) if 0 <= candidate < len(sweep) and candidate not in exact), None)
        far = max(range(len(sweep)), key=lambda index: min(abs(index - target) for target in acceptable))
        named = [("endpoint-low", 0), ("endpoint-high", len(sweep) - 1), ("inside", acceptable[len(acceptable) // 2]), ("far", far)]
        if near is not None:
            named.append(("immediately-outside", near))
        starts: dict[int, list[str]] = {}
        for name, index in named:
            starts.setdefault(index, []).append(name)
        for start, start_names in starts.items():
            index = start
            stopped = False
            visited: list[int] = []
            reversals = 0
            previous_nonstop = 0
            previous_image = None
            previous_action = 0
            for _ in range(len(sweep) * 2):
                visited.append(index)
                current_image = sweep[index].image
                features = request_features(current_image, previous_image, previous_action, int(model.get("featureSize", 16)), str(model.get("observationMode", "current")))
                action = int(INDEX_TO_ACTION[int(np.argmax(model_probabilities(model, features)))])
                if action == 0:
                    stopped = True
                    break
                if previous_nonstop and action != previous_nonstop:
                    reversals += 1
                previous_nonstop = action
                next_index = int(np.clip(index + action, 0, len(sweep) - 1))
                if next_index == index:
                    break
                previous_image = current_image
                previous_action = action
                index = next_index
            error_mm = min(abs(sweep[index].path_mm - sweep[target].path_mm) for target in acceptable)
            episodes.append({
                "sweep": sweep_id,
                "family": sweep[0].configuration_family,
                "startConditions": start_names,
                "reachable": True,
                "startIndex": start,
                "stopIndex": index,
                "explicitStop": stopped,
                "explicitStopInRegion": bool(
                    stopped and index in acceptable and sweep[index].contact_valid and sweep[index].workspace_valid
                ),
                "regionEntered": any(value in acceptable for value in visited),
                "stoppingErrorToAcceptableRangeMm": float(error_mm),
                "steps": len(visited),
                "reversals": reversals,
            })
    reachable = [episode for episode in episodes if episode.get("reachable")]
    by_family = {}
    for family in sorted({episode.get("family") for episode in reachable}):
        family_items = [episode for episode in reachable if episode.get("family") == family]
        by_family[str(family)] = {"episodes": len(family_items), "successes": sum(item["explicitStopInRegion"] for item in family_items)}
    return {
        "definition": "Recorded-image trajectory replay only; acquisition requires an explicit model STOP in an acceptable frame. This is not the live Unity result.",
        "episodes": episodes,
        "reachableEpisodes": len(reachable),
        "explicitStopInRegionRate": float(np.mean([item["explicitStopInRegion"] for item in reachable])) if reachable else None,
        "regionEnteredRate": float(np.mean([item["regionEntered"] for item in reachable])) if reachable else None,
        "meanStoppingErrorToAcceptableRangeMm": float(np.mean([item["stoppingErrorToAcceptableRangeMm"] for item in reachable])) if reachable else None,
        "meanSteps": float(np.mean([item["steps"] for item in reachable])) if reachable else None,
        "timeoutsOrBoundaryFailures": int(sum(not item["explicitStop"] for item in reachable)),
        "meanReversals": float(np.mean([item["reversals"] for item in reachable])) if reachable else None,
        "rawCounts": {
            "reachable": len(reachable),
            "explicitStopSuccess": sum(item["explicitStopInRegion"] for item in reachable),
            "regionEnteredWithoutCorrectStop": sum(item["regionEntered"] and not item["explicitStopInRegion"] for item in reachable),
            "wrongStop": sum(item["explicitStop"] and not item["explicitStopInRegion"] for item in reachable),
            "boundaryOrTimeout": sum(not item["explicitStop"] for item in reachable),
        },
        "byFamily": by_family,
    }


def bootstrap_sweep_accuracy(frames, probabilities, samples: int = 1000, seed: int = 20261020) -> list[float]:
    rng = np.random.default_rng(seed)
    sweep_ids = sorted({frame.sweep_id for frame in frames})
    by_sweep = {}
    predicted = probabilities.argmax(axis=1)
    labels = np.asarray([ACTION_TO_INDEX[frame.teacher_action] for frame in frames])
    for sweep_id in sweep_ids:
        indices = [index for index, frame in enumerate(frames) if frame.sweep_id == sweep_id]
        by_sweep[sweep_id] = float(np.mean(predicted[indices] == labels[indices]))
    draws = [np.mean([by_sweep[sweep_ids[index]] for index in rng.integers(0, len(sweep_ids), len(sweep_ids))]) for _ in range(samples)]
    return [float(np.percentile(draws, 2.5)), float(np.percentile(draws, 97.5))]


def main() -> None:
    parser = argparse.ArgumentParser(description="Locked offline evaluation for a selected pixel policy")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--split", choices=("train", "dev", "test"), default="test")
    parser.add_argument("--evaluation-group", choices=("all", "heldout", "stress"), default="heldout")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.output is None:
        args.output = args.checkpoint.with_name(args.checkpoint.stem + f"_{args.evaluation_group}_evaluation.json")
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    frames_all = load_frames(experiment_id=args.experiment_id, dataset_run_id=args.dataset_run_id)
    observation = str(model.get("observationMode", "current"))
    transitions_all = load_transitions(experiment_id=args.experiment_id, dataset_run_id=args.dataset_run_id) if observation == "history" else []
    # Training fingerprints only the train/dev selection corpus so the locked test
    # split remains unseen. Rebuild that exact identity here before evaluating any
    # requested split; including test rows would reject every valid checkpoint.
    selection_frames = [frame for frame in frames_all if frame.split in {"train", "dev"}]
    selection_transitions = [
        sample
        for sample in transitions_all
        if sample.split in {"train", "dev"}
        and sample.teacher_action_defined
        and not sample.terminated
        and not sample.truncated
        and sample.contact_valid
        and sample.workspace_valid
    ]
    fingerprint = (
        transition_dataset_fingerprint(selection_transitions)
        if observation == "history"
        else dataset_fingerprint(selection_frames)
    )
    if model.get("experimentId") != args.experiment_id or model.get("datasetRunId") != args.dataset_run_id or model.get("datasetFingerprint") != fingerprint:
        raise SystemExit("Checkpoint dataset identity does not match the selected experiment")
    selected = [frame for frame in frames_all if frame.split == args.split and frame.teacher_action_defined]
    if args.evaluation_group != "all":
        selected = [frame for frame in selected if frame.evaluation_group == args.evaluation_group]
    if not selected:
        raise SystemExit("No defined-action frames for requested evaluation condition")
    started = time.perf_counter()
    metric_rows = selected
    if observation == "history":
        metric_rows = [sample for sample in transitions_all if sample.split == args.split and sample.teacher_action_defined and (args.evaluation_group == "all" or sample.evaluation_group == args.evaluation_group)]
        if not metric_rows:
            raise SystemExit("No genuine history transitions for requested evaluation condition")
        x = transition_feature_matrix(metric_rows, size=int(model.get("featureSize", 16)))
    else:
        x = feature_matrix(metric_rows, size=int(model.get("featureSize", 16)), observation="current")
    probabilities = model_probabilities(model, x)
    latency_ms = (time.perf_counter() - started) * 1000.0 / len(metric_rows)
    labels = np.asarray([ACTION_TO_INDEX[item.teacher_action] for item in metric_rows], dtype=np.int64)
    metrics = classification_metrics(labels, probabilities)
    metrics["sweepBootstrap95CiAccuracy"] = bootstrap_sweep_accuracy(metric_rows, probabilities)
    metrics["latencyMsPerFrameCpu"] = latency_ms
    report: dict[str, object] = {
        "schemaVersion": 3,
        "checkpoint": str(args.checkpoint),
        "experimentId": args.experiment_id,
        "datasetFingerprint": fingerprint,
        "split": args.split,
        "evaluationGroup": args.evaluation_group,
        "finalTestWasNotUsedForModelSelection": args.split == "test",
        "inputAudit": f"{observation} clean ultrasound observation only; no pose, path index, mask, score, target, or surface normal",
        "frameMetrics" if observation == "current" else "transitionMetrics": metrics,
        "recordedTrajectoryReplay": replay_rollouts(selected, model),
        "geometry": {
            "constraintViolations": int(sum(
                not frame.contact_valid or not frame.workspace_valid or abs(frame.signed_depth_mm) > 0.5
                for frame in selected
            )),
            "maxAbsSignedDepthMm": float(max(abs(frame.signed_depth_mm) for frame in selected)),
        },
    }
    save_json(args.output, report)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
