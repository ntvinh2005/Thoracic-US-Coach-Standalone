from __future__ import annotations

import argparse
import hashlib
import json
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, INDEX_TO_ACTION, model_probabilities, pixel_features, project_root, request_features, save_json
from contract_v3 import canonical_json_sha256, oracle_action
from protocol_v3 import validate_manifest, validate_model, verify_frozen_scenario
from unity_env import UnityFileEnvironment


DEFAULT_DIAGNOSTIC_RUN_ID = "wrong_stop_diagnostic_v1_20261022"


def file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def sigmoid(value: float) -> float:
    return float(1.0 / (1.0 + np.exp(-np.clip(value, -40.0, 40.0))))


def model_logits(model: dict[str, object], features: np.ndarray) -> np.ndarray:
    architecture = str(model.get("architecture") or "linear-softmax-v1")
    if architecture == "linear-softmax-v1":
        return features @ np.asarray(model["weights"], dtype=np.float32) + np.asarray(model["bias"], dtype=np.float32)
    hidden = np.maximum(
        features @ np.asarray(model["inputWeights"], dtype=np.float32)
        + np.asarray(model["inputBias"], dtype=np.float32),
        0.0,
    )
    return hidden @ np.asarray(model["outputWeights"], dtype=np.float32) + np.asarray(model["outputBias"], dtype=np.float32)


def detector_score(detector: dict[str, object], image: Path) -> float:
    features = pixel_features(image, int(detector["featureSize"]))
    logit = float(features @ np.asarray(detector["weights"], dtype=np.float32) + float(detector["bias"]))
    return sigmoid(logit)


def make_diagnostic_manifest(source: dict[str, object], offset: int, count: int, run_id: str) -> dict[str, object]:
    manifest = deepcopy(source)
    manifest["createdUtc"] = datetime.now(timezone.utc).isoformat()
    manifest["diagnosticRunId"] = run_id
    manifest["sourceManifestSha256"] = source.get("manifestSha256")
    manifest["selectionUse"] = "DIAGNOSTIC development check only; no final heldout/stress use"
    manifest["scenarios"] = list(source["scenarios"])[offset : offset + count]
    manifest["manifestSha256"] = canonical_json_sha256(
        {key: value for key, value in manifest.items() if key not in ("createdUtc", "manifestSha256")}
    )
    return manifest


def outcome_name(*, success: bool, wrong_stop: bool, region_entered: bool, timeout: bool) -> str:
    if success:
        return "correct-explicit-stop"
    if wrong_stop:
        return "wrong-stop-after-target-entry" if region_entered else "wrong-stop-before-target-entry"
    if timeout:
        return "timeout-after-target-entry" if region_entered else "timeout-before-target-entry"
    return "other-invalid-or-aborted"


def summarize(episodes: list[dict[str, object]]) -> dict[str, object]:
    acquisition = [item for item in episodes if not item["initiallyAcceptable"]]
    recognition = [item for item in episodes if item["initiallyAcceptable"]]
    outcomes = sorted({str(item["primaryOutcome"]) for item in episodes})
    return {
        "episodes": len(episodes),
        "recognitionSuccess": sum(item["success"] for item in recognition),
        "recognitionTotal": len(recognition),
        "acquisitionSuccess": sum(item["success"] for item in acquisition),
        "acquisitionTotal": len(acquisition),
        "constraintViolations": sum(int(item["constraintViolations"]) for item in episodes),
        "primaryOutcomesAcquisition": {
            outcome: sum(item["primaryOutcome"] == outcome for item in acquisition) for outcome in outcomes
        },
        "wrongStopFirstDecisionAcquisition": sum(
            item["primaryOutcome"].startswith("wrong-stop") and item["steps"] == 1 for item in acquisition
        ),
        "wrongStopLaterDecisionAcquisition": sum(
            item["primaryOutcome"].startswith("wrong-stop") and item["steps"] > 1 for item in acquisition
        ),
        "firstTargetEncounteredAcquisition": sum(item["firstTargetEncounterStep"] is not None for item in acquisition),
        "failedToStopAtFirstTargetEncounterAcquisition": sum(item["missedFirstTargetStop"] for item in acquisition),
        "wrongFirstMovementDirectionAcquisition": sum(item["wrongFirstMovementDirection"] for item in acquisition),
        "oscillationEpisodesAcquisition": sum(item["oscillationCount"] > 0 for item in acquisition),
        "boundaryActionsAcquisition": sum(int(item["boundaryActions"]) for item in acquisition),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Bounded live STOP decomposition and intervention diagnostic")
    parser.add_argument("--diagnostic-run-id", default=DEFAULT_DIAGNOSTIC_RUN_ID)
    parser.add_argument("--source-manifest", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--stop-audit", type=Path, required=True)
    parser.add_argument("--detector", type=Path, required=True)
    parser.add_argument("--scenario-offset", type=int, default=6)
    parser.add_argument("--scenario-count", type=int, default=6)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = project_root()
    output_dir = args.output_dir or root / "Artifacts" / "ThoracicUSCoach" / "diagnostics" / args.diagnostic_run_id
    output_dir.mkdir(parents=True, exist_ok=True)
    selected_images_dir = output_dir / "selected_live_images"
    selected_images_dir.mkdir(parents=True, exist_ok=True)

    source_manifest = json.loads(args.source_manifest.read_text(encoding="utf-8"))
    validate_manifest(source_manifest, expected_group="development", selection_allowed=True)
    diagnostic_manifest = make_diagnostic_manifest(
        source_manifest, args.scenario_offset, args.scenario_count, args.diagnostic_run_id
    )
    if not diagnostic_manifest["scenarios"]:
        raise SystemExit("Diagnostic scenario slice is empty")
    manifest_path = output_dir / "development_live_check_manifest.json"
    save_json(manifest_path, diagnostic_manifest)

    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    validate_model(model)
    stop_audit = json.loads(args.stop_audit.read_text(encoding="utf-8"))
    detector = json.loads(args.detector.read_text(encoding="utf-8"))
    b1_threshold = float(stop_audit["B1CalibratedSameLogits"]["calibration"]["threshold"])
    detector_threshold = float(detector["stopThreshold"])
    max_steps = int(diagnostic_manifest["protocol"]["episodeBudget"])

    policies = [
        ("B0_three_action_argmax", "DEPLOYABLE-CURRENT"),
        ("B1_calibrated_stop_same_logits", "DEPLOYABLE-DIAGNOSTIC"),
        ("B2_binary_stop_same_navigation", "DEPLOYABLE-DIAGNOSTIC"),
        ("B3_binary_stop_bounded_scan", "DEPLOYABLE-WITH-MOTION-FEEDBACK"),
        ("I1_oracle_movement_learned_stop", "NON-DEPLOYABLE"),
        ("I2_learned_movement_oracle_stop", "NON-DEPLOYABLE"),
        ("I3_oracle_movement_oracle_stop", "NON-DEPLOYABLE"),
    ]
    environment = UnityFileEnvironment(timeout=20.0)
    policy_results = {}
    retained_failure_image_policy: set[str] = set()

    for policy_name, access_label in policies:
        episodes = []
        for frozen in diagnostic_manifest["scenarios"]:
            exact = dict(frozen["scenario"])
            environment.reset(0, max_steps=max_steps, frozen_scenario=exact)
            actual = dict(environment.describe()["scenario"])
            verify_frozen_scenario(frozen, actual)
            path_mm = [float(value) for value in actual["pathMm"]]
            acceptable_set = set(int(value) for value in actual["acceptableIndices"])
            acceptable = tuple(index in acceptable_set for index in range(len(path_mm)))
            step_count = int(actual["actionStepCount"])
            for start in frozen["starts"]:
                response = environment.reset(int(start["index"]), max_steps=max_steps)
                previous_image = None
                previous_image_sha = None
                previous_action = 0
                direction = 1
                region_entered = bool(response.get("regionEntered", False))
                first_target_encounter_step = 0 if bool(response.get("targetAcceptable", False)) else None
                missed_first_target_stop = False
                first_movement = None
                movement_actions = []
                boundary_actions = oscillations = constraints = 0
                traces = []
                image_bytes = []
                for step in range(max_steps):
                    image = Path(str(response["imagePath"]))
                    image_sha = file_sha256(image)
                    features = request_features(
                        image,
                        previous_image,
                        previous_action,
                        int(model.get("featureSize", 16)),
                        str(model.get("observationMode", "current")),
                    )
                    logits = model_logits(model, features)
                    probabilities = model_probabilities(model, features)
                    stop_score = float(probabilities[ACTION_TO_INDEX[0]])
                    binary_score = detector_score(detector, image)
                    movement_action = -1 if probabilities[ACTION_TO_INDEX[-1]] >= probabilities[ACTION_TO_INDEX[1]] else 1
                    privileged_oracle = oracle_action(acceptable, int(response["pathIndex"]), step_count)

                    if policy_name == "B0_three_action_argmax":
                        action = int(INDEX_TO_ACTION[int(np.argmax(probabilities))])
                        rationale = "three-action argmax"
                    elif policy_name == "B1_calibrated_stop_same_logits":
                        action = 0 if stop_score >= b1_threshold else movement_action
                        rationale = "calibrated STOP score; highest-probability non-STOP when rejected"
                    elif policy_name == "B2_binary_stop_same_navigation":
                        action = 0 if binary_score >= detector_threshold else movement_action
                        rationale = "binary detector; original actor movement with STOP suppressed when rejected"
                    elif policy_name == "B3_binary_stop_bounded_scan":
                        action = 0 if binary_score >= detector_threshold else direction
                        rationale = "binary detector plus scan direction and boundary-feedback reversal"
                    elif policy_name == "I1_oracle_movement_learned_stop":
                        learned_stop = int(np.argmax(probabilities)) == ACTION_TO_INDEX[0]
                        action = 0 if learned_stop else (
                            int(privileged_oracle) if int(privileged_oracle) != 0 else movement_action
                        )
                        rationale = "NON-DEPLOYABLE oracle movement outside target; original argmax exclusively decides STOP"
                    elif policy_name == "I2_learned_movement_oracle_stop":
                        action = 0 if bool(response["targetAcceptable"]) else movement_action
                        rationale = "NON-DEPLOYABLE oracle STOP; highest-probability non-STOP outside target"
                    else:
                        action = int(privileged_oracle)
                        rationale = "NON-DEPLOYABLE oracle movement and oracle STOP"

                    if first_target_encounter_step is not None and step == first_target_encounter_step and action != 0:
                        missed_first_target_stop = True
                    if action != 0:
                        if first_movement is None:
                            first_movement = action
                        if movement_actions and movement_actions[-1] != action:
                            oscillations += 1
                        movement_actions.append(action)

                    before_index = int(response["pathIndex"])
                    before_target = bool(response["targetAcceptable"])
                    before_distance = float(response["distanceToAcceptableMm"])
                    image_bytes.append(image.read_bytes())
                    next_response = environment.step(action)
                    boundary = bool(next_response.get("patchBoundaryViolation", False))
                    boundary_actions += int(boundary)
                    constraints += int(bool(next_response.get("constraintViolation", False)))
                    if policy_name == "B3_binary_stop_bounded_scan" and boundary:
                        direction *= -1
                    if first_target_encounter_step is None and bool(next_response.get("targetAcceptable", False)) and action != 0:
                        first_target_encounter_step = step + 1
                    region_entered |= bool(next_response.get("regionEntered", False))
                    traces.append({
                        "step": step,
                        "observationImageSha256": image_sha,
                        "historyImageSha256": previous_image_sha or image_sha,
                        "executedPreviousAction": previous_action,
                        "resetInitialization": step == 0,
                        "actorLogits": [float(value) for value in logits],
                        "actorProbabilities": [float(value) for value in probabilities],
                        "binaryTargetScore": binary_score,
                        "chosenAction": action,
                        "decisionRationale": rationale,
                        "targetBefore": before_target,
                        "targetAfter": bool(next_response["targetAcceptable"]),
                        "pathIndexBefore": before_index,
                        "pathIndexAfter": int(next_response["pathIndex"]),
                        "distanceToTargetMmBefore": before_distance,
                        "distanceToTargetMmAfter": float(next_response["distanceToAcceptableMm"]),
                        "contactValidAfter": bool(next_response["contactValid"]),
                        "workspaceValidAfter": bool(next_response["workspaceValid"]),
                        "boundaryAfter": boundary,
                        "reward": float(next_response["reward"]),
                        "terminated": bool(next_response["terminated"]),
                        "truncated": bool(next_response["truncated"]),
                        "diagnosticPrivilege": {
                            "oracleAction": int(privileged_oracle),
                            "targetMembershipUsedByActor": policy_name in (
                                "I2_learned_movement_oracle_stop", "I3_oracle_movement_oracle_stop"
                            ),
                            "oracleMovementUsed": policy_name in (
                                "I1_oracle_movement_learned_stop", "I3_oracle_movement_oracle_stop"
                            ),
                        },
                    })
                    previous_image, previous_image_sha = image, image_sha
                    previous_action, response = action, next_response
                    if bool(response["done"]):
                        break

                success = bool(response.get("explicitStopSuccess", False))
                wrong_stop = bool(response.get("terminated", False)) and not success
                timeout = bool(response.get("truncated", False))
                outcome = outcome_name(
                    success=success, wrong_stop=wrong_stop, region_entered=region_entered, timeout=timeout
                )
                initial_oracle = oracle_action(acceptable, int(start["index"]), step_count)
                wrong_first_direction = (
                    first_movement is not None and initial_oracle != 0 and first_movement != int(initial_oracle)
                )
                episode_id = f"s{int(frozen['scenarioIndex']):02d}_seed{int(frozen['trajectorySeed'])}_{start['name']}"
                saved_images = []
                if (
                    policy_name in {"B0_three_action_argmax", "B1_calibrated_stop_same_logits", "B2_binary_stop_same_navigation"}
                    and outcome.startswith("wrong-stop")
                    and policy_name not in retained_failure_image_policy
                ):
                    retained_failure_image_policy.add(policy_name)
                    safe_policy = policy_name.replace("_", "-")
                    for trace, payload in zip(traces, image_bytes):
                        destination = selected_images_dir / f"{safe_policy}_{episode_id}_step{trace['step']:02d}.png"
                        destination.write_bytes(payload)
                        saved_images.append(str(destination.relative_to(root)).replace("\\", "/"))
                episodes.append({
                    "episodeId": episode_id,
                    "policy": policy_name,
                    "accessLabel": access_label,
                    "scenarioIndex": int(frozen["scenarioIndex"]),
                    "trajectorySeed": int(frozen["trajectorySeed"]),
                    "startCondition": str(start["name"]),
                    "startIndex": int(start["index"]),
                    "initiallyAcceptable": bool(start["initiallyAcceptable"]),
                    "initialDistanceToTargetMm": float(traces[0]["distanceToTargetMmBefore"]),
                    "targetWidthPoses": len(acceptable_set),
                    "success": success,
                    "primaryOutcome": outcome,
                    "steps": len(traces),
                    "firstTargetEncounterStep": first_target_encounter_step,
                    "missedFirstTargetStop": missed_first_target_stop,
                    "wrongFirstMovementDirection": wrong_first_direction,
                    "boundaryActions": boundary_actions,
                    "oscillationCount": oscillations,
                    "constraintViolations": constraints,
                    "savedImages": saved_images,
                    "trace": traces,
                })
        policy_results[policy_name] = {
            "accessLabel": access_label,
            "summary": summarize(episodes),
            "episodes": episodes,
        }
        print(json.dumps({
            "phase": "DIAGNOSTIC",
            "policy": policy_name,
            "completedEpisodes": len(episodes),
            "summary": policy_results[policy_name]["summary"],
        }), flush=True)

    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "DIAGNOSTIC",
        "diagnosticRunId": args.diagnostic_run_id,
        "heldoutOrStressAccessed": False,
        "sourceDevelopmentManifest": str(args.source_manifest.resolve()),
        "diagnosticManifest": str(manifest_path.resolve()),
        "checkpoint": str(args.checkpoint.resolve()),
        "stopThreshold": b1_threshold,
        "detector": str(args.detector.resolve()),
        "detectorThreshold": detector_threshold,
        "policies": policy_results,
        "sensorAccessDisclosure": {
            "B0": "strict previous/current image plus previous executed action actor",
            "B1": "same actor inputs and logits as B0; calibrated STOP threshold",
            "B2": "binary current-image STOP detector plus B0 movement logits",
            "B3": "binary current-image STOP detector plus explicit patchBoundaryViolation motion feedback",
            "interventions": "I1-I3 use privileged oracle actions and are NON-DEPLOYABLE diagnostics",
        },
    }
    output = output_dir / "live_stop_diagnostic.json"
    save_json(output, report)
    print(json.dumps({"phase": "DIAGNOSTIC", "output": str(output)}), flush=True)


if __name__ == "__main__":
    main()
