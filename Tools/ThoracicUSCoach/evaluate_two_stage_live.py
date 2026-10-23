from __future__ import annotations

import argparse
import json
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from coach_data import model_probabilities, request_features, save_json
from contract_v3 import oracle_action
from protocol_v3 import verify_frozen_scenario
from stop_policy import request_stop_features, two_stage_action, validate_stop_model
from unity_env import UnityFileEnvironment


def policy_action(mode, navigation, stop, current, previous, previous_action):
    started = time.perf_counter()
    navigation_features = request_features(
        current,
        previous,
        previous_action,
        int(navigation["featureSize"]),
        str(navigation["observationMode"]),
    )
    navigation_probability = np.asarray(model_probabilities(navigation, navigation_features))
    if mode == "three-action-argmax":
        action = int((-1, 0, 1)[int(np.argmax(navigation_probability))])
        stop_score = float(navigation_probability[1])
    elif mode == "two-stage":
        stop_features = request_stop_features(stop, current, previous, previous_action)
        action, stop_score, _ = two_stage_action(
            navigation, stop, navigation_features, stop_features
        )
    else:
        raise ValueError(mode)
    return action, stop_score, (time.perf_counter() - started) * 1000.0


def run_episode(environment, scenario_record, start, mode, navigation, stop, max_steps):
    response = environment.reset(
        int(start["index"]), max_steps=max_steps, frozen_scenario=dict(scenario_record["scenario"])
    )
    actual = dict(environment.describe()["scenario"])
    verify_frozen_scenario(scenario_record, actual)
    acceptable_set = set(int(value) for value in actual["acceptableIndices"])
    acceptable = tuple(index in acceptable_set for index in range(len(actual["pathMm"])))
    previous_image = None
    previous_action = 0
    previous_movement = 0
    region_entered = bool(response.get("regionEntered", False))
    wrong_before = wrong_after = wrong_direction = reversals = 0
    boundary = invalid = constraints = 0
    contact = workspace = 0
    first_entry_stop = False
    inference_ms = []
    trace = []
    for step in range(max_steps):
        current = Path(str(response["imagePath"]))
        was_inside = int(response["pathIndex"]) in acceptable_set
        first_target_decision = was_inside and not region_entered
        if step == 0 and was_inside:
            first_target_decision = True
        action, stop_score, latency = policy_action(
            mode, navigation, stop, current, previous_image, previous_action
        )
        inference_ms.append(latency)
        if action == 0 and first_target_decision:
            first_entry_stop = True
        intended = oracle_action(acceptable, int(response["pathIndex"]), int(actual["actionStepCount"]))
        if action != 0 and intended != 0 and action != intended:
            wrong_direction += 1
        if action != 0 and previous_movement and action != previous_movement:
            reversals += 1
        if action != 0:
            previous_movement = action
        next_response = environment.step(action)
        if action == 0 and not bool(next_response.get("explicitStopSuccess", False)):
            if region_entered:
                wrong_after += 1
            else:
                wrong_before += 1
        is_boundary = bool(next_response.get("patchBoundaryViolation", False))
        boundary += int(is_boundary)
        invalid += int(bool(next_response.get("invalidMotion", False)))
        constraints += int(bool(next_response.get("constraintViolation", False)))
        contact += int(not bool(next_response.get("contactValid", False)))
        workspace += int(not bool(next_response.get("workspaceValid", False)))
        trace.append(
            {
                "step": step,
                "index": int(response["pathIndex"]),
                "acceptable": was_inside,
                "action": action,
                "stopScore": stop_score,
                "nextIndex": int(next_response["pathIndex"]),
                "success": bool(next_response.get("explicitStopSuccess", False)),
            }
        )
        region_entered |= bool(next_response.get("regionEntered", False))
        previous_image = current
        previous_action = action
        response = next_response
        if bool(response["done"]):
            break
    success = bool(response.get("explicitStopSuccess", False))
    initially = bool(start["initiallyAcceptable"])
    return {
        "scenarioIdentitySha256": scenario_record["scenarioIdentitySha256"],
        "trajectorySeed": scenario_record["trajectorySeed"],
        "family": scenario_record["configurationFamily"],
        "start": start["name"],
        "startIndex": start["index"],
        "initiallyAcceptable": initially,
        "success": success,
        "recognitionSuccess": initially and success,
        "immediateRecognitionSuccess": initially and success and len(trace) == 1,
        "acquisitionSuccess": (not initially) and success,
        "wrongStopBeforeEntry": wrong_before,
        "wrongStopAfterEntry": wrong_after,
        "firstDecisionWrongStop": (not initially) and len(trace) == 1 and trace[0]["action"] == 0 and not success,
        "firstTargetEntryStop": first_entry_stop,
        "regionEnteredWithoutCorrectStop": region_entered and not success,
        "wrongMovementDirection": wrong_direction,
        "reversals": reversals,
        "boundaryActions": boundary,
        "invalidActions": invalid,
        "constraintViolations": constraints,
        "contactViolations": contact,
        "workspaceViolations": workspace,
        "timeout": bool(response.get("truncated", False)),
        "steps": len(trace),
        "inferenceLatencyMsMean": float(np.mean(inference_ms)),
        "trace": trace,
    }


def summarize(episodes):
    recognition = [item for item in episodes if item["initiallyAcceptable"]]
    acquisition = [item for item in episodes if not item["initiallyAcceptable"]]
    keys = (
        "wrongStopBeforeEntry",
        "wrongStopAfterEntry",
        "firstDecisionWrongStop",
        "regionEnteredWithoutCorrectStop",
        "wrongMovementDirection",
        "reversals",
        "boundaryActions",
        "invalidActions",
        "constraintViolations",
        "contactViolations",
        "workspaceViolations",
        "timeout",
    )
    counts = {key: sum(item[key] for item in episodes) for key in keys}
    counts.update(
        {
            "episodes": len(episodes),
            "recognitionSuccess": sum(item["recognitionSuccess"] for item in recognition),
            "recognitionTotal": len(recognition),
            "immediateRecognitionSuccess": sum(item["immediateRecognitionSuccess"] for item in recognition),
            "acquisitionSuccess": sum(item["acquisitionSuccess"] for item in acquisition),
            "acquisitionTotal": len(acquisition),
        }
    )
    return {
        "counts": counts,
        "inferenceLatencyMsMean": float(np.mean([item["inferenceLatencyMsMean"] for item in episodes])),
        "episodes": episodes,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Interleaved paired live baseline/two-stage evaluation")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--navigation-checkpoint", type=Path, required=True)
    parser.add_argument("--stop-checkpoint", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    if manifest.get("group") != "development":
        raise SystemExit("This development evaluator refuses heldout/stress manifests")
    navigation = json.loads(args.navigation_checkpoint.read_text(encoding="utf-8"))
    stop = json.loads(args.stop_checkpoint.read_text(encoding="utf-8"))
    validate_stop_model(stop)
    environment = UnityFileEnvironment(timeout=20.0)
    results = {"three-action-argmax": [], "two-stage": []}
    for scenario_number, scenario in enumerate(manifest["scenarios"]):
        for start_number, start in enumerate(scenario["starts"]):
            for repeat in range(args.repeats):
                order = ["three-action-argmax", "two-stage"]
                if (scenario_number + start_number + repeat) % 2:
                    order.reverse()
                for mode in order:
                    episode = run_episode(
                        environment,
                        scenario,
                        start,
                        mode,
                        navigation,
                        stop,
                        args.max_steps,
                    )
                    episode["repeat"] = repeat
                    episode["interleavedOrder"] = order
                    results[mode].append(episode)
    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "paired live Unity development evaluation",
        "manifest": str(args.manifest.resolve()),
        "manifestSha256": manifest["manifestSha256"],
        "selectionUse": manifest.get("selectionUse"),
        "heldoutOrStressAccessed": False,
        "navigationCheckpoint": str(args.navigation_checkpoint.resolve()),
        "stopCheckpoint": str(args.stop_checkpoint.resolve()),
        "stopThreshold": stop["stopThreshold"],
        "rendererRepeatsPerCase": args.repeats,
        "interleaved": True,
        "policies": {key: summarize(value) for key, value in results.items()},
        "unityEpisodes": sum(len(value) for value in results.values()),
    }
    save_json(args.output, report)
    print(json.dumps({key: value["counts"] for key, value in report["policies"].items()}, indent=2))


if __name__ == "__main__":
    main()
