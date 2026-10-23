from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any

import numpy as np

from active2d_contract import CONTRACT, LEVEL_ORDER, project_root
from active2d_data import dataset_fingerprint, feature_matrix, load_active2d_frames, softmax
from topological_localizer import (LEVEL_TO_INDEX, OnlineTopologicalLocalizer, confusion_metrics,
                                   fit_probabilistic_localizer, group_indices, handoff_candidate)
from visual_landmark_topology import assign_visual_landmark, project_features


def emit(phase: str, current: int, total: int, message: str) -> None:
    print("::localjobs_progress " + json.dumps({"phase": phase, "current": current, "total": total, "message": message}), flush=True)


def atomic_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    temporary.replace(path)


def predict_head(head: dict[str, Any], values: np.ndarray) -> np.ndarray:
    hidden = np.maximum(values @ np.asarray(head["inputWeights"], np.float32)
                        + np.asarray(head["inputBias"], np.float32), 0.0)
    return softmax(hidden @ np.asarray(head["outputWeights"], np.float32)
                   + np.asarray(head["outputBias"], np.float32))


def replay_all(model: dict[str, Any], frames, features: np.ndarray) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = [{} for _ in frames]
    for indices in group_indices(frames).values():
        localizer = OnlineTopologicalLocalizer(model)
        for index in indices:
            records[index] = localizer.observe(features[index], frames[index].issued_action, frames[index].index)
    return records


def prototype_baseline(probabilistic: dict, topology: dict, features: np.ndarray) -> np.ndarray:
    emissions = np.asarray(probabilistic["prototypeEmission"], np.float64)
    initial = np.asarray(probabilistic["initialBelief"], np.float64)
    result = []
    for embedding in project_features(topology, features):
        assignment = assign_visual_landmark(topology, embedding)
        result.append(np.argmax(emissions[:, int(assignment["prototype"])] if assignment["status"] == "MATCHED" else initial))
    return np.asarray(result, np.int64)


def evaluate_records(frames, records: list[dict[str, Any]], gate: dict[str, Any]) -> dict[str, Any]:
    actual = np.asarray([LEVEL_TO_INDEX[frame.level] for frame in frames], np.int64)
    predicted = np.asarray([LEVEL_TO_INDEX[record["predictedLevel"]] for record in records], np.int64)
    handoffs = []
    for episode, indices in group_indices(frames).items():
        streak = 0; chosen = None
        for index in indices:
            streak = streak + 1 if handoff_candidate(records[index], gate) else 0
            if streak >= int(gate["confirmations"]):
                chosen = index
                break
        if chosen is not None:
            handoffs.append({"episodeId": episode, "frameIndex": frames[chosen].index,
                             "correctLevel": frames[chosen].level == "T7",
                             "preciseRegion": frames[chosen].t7_acceptable,
                             "confidence": records[chosen]["confidence"], "uncertainty": records[chosen]["uncertainty"]})
    correct_level = sum(item["correctLevel"] for item in handoffs)
    precise = sum(item["preciseRegion"] for item in handoffs)
    episodes = len(group_indices(frames))
    loop = [record for record in records if record["loopAgreementBefore"] is not None]
    topology_events = Counter(record["topologyEvent"] for record in records)
    return {"level": confusion_metrics(actual, predicted),
            "handoff": {"episodes": episodes, "handoffs": len(handoffs), "abstentions": episodes - len(handoffs),
                        "correctLevel": correct_level, "preciseRegion": precise,
                        "levelPrecision": correct_level / max(1, len(handoffs)),
                        "preciseAcquisitionRate": precise / max(1, episodes), "decisions": handoffs},
            "loopClosureBelief": {"events": len(loop),
                                  "meanAgreementBefore": float(np.mean([r["loopAgreementBefore"] for r in loop])) if loop else None,
                                  "meanAgreementAfter": float(np.mean([r["loopAgreementAfter"] for r in loop])) if loop else None,
                                  "improvedFraction": float(np.mean([r["loopAgreementAfter"] + 1e-12 >= r["loopAgreementBefore"] for r in loop])) if loop else None},
            "topologyEvidence": {"eventCounts": dict(topology_events),
                                 "observedTransitions": int(topology_events["TRANSITION"]),
                                 "visualLoopClosures": int(topology_events["LOOP_CLOSURE"])}}


def direct_baseline(checkpoint: dict, features: np.ndarray, labels: np.ndarray) -> dict[str, Any]:
    scaler = checkpoint["imageScaler"]
    normalized = (features - np.asarray(scaler["mean"], np.float32)) / np.asarray(scaler["scale"], np.float32)
    probabilities = predict_head(checkpoint["directBaselineHead"], normalized)
    return confusion_metrics(labels, np.argmax(probabilities, axis=1))


def main() -> None:
    parser = argparse.ArgumentParser(description="U06-R1 topology-driven temporal vertebral localizer")
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    root = project_root(); config_path = args.config.resolve(); config_bytes = config_path.read_bytes()
    config = json.loads(config_bytes)
    topology_path = root / config["visualTopologyCheckpoint"]
    baseline_path = root / config["directBaselineCheckpoint"]
    topology = json.loads(topology_path.read_text(encoding="utf-8"))
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    frames = [frame for frame in load_active2d_frames(config["datasetRunId"], root)
              if frame.contact_valid and frame.workspace_valid]
    splits = {name: [frame for frame in frames if frame.split == name] for name in ("train", "dev", "test")}
    families = {name: {frame.family for frame in values} for name, values in splits.items()}
    if any(families[a] & families[b] for a, b in (("train", "dev"), ("train", "test"), ("dev", "test"))):
        raise SystemExit("Trajectory-family leakage")
    if args.smoke:
        splits = {name: values[:min(len(values), 160)] for name, values in splits.items()}
    emit("features", 0, 5, "Extracting clean-image features for family-separated splits")
    features = {name: feature_matrix(values) for name, values in splits.items()}
    probabilistic = fit_probabilistic_localizer(splits["train"], features["train"], topology, config["smoothing"])
    emit("fit", 1, 5, "Fitted prototype emissions and action-conditioned transitions")
    candidates = []
    grid = config["search"]
    if args.smoke:
        grid = {key: values[:1] for key, values in grid.items()}
    for transition_mix in grid["transitionMix"]:
        for emission_power in grid["emissionPower"]:
            for loop_fusion in grid["loopFusion"]:
                model = {"visualTopology": topology, "probabilisticLocalizer": probabilistic,
                         "inference": {"transitionMix": transition_mix, "emissionPower": emission_power,
                                       "loopFusion": loop_fusion, "anchorPowerMultiplier": config["anchorPowerMultiplier"]}}
                records = replay_all(model, splits["dev"], features["dev"])
                for threshold in grid["confidenceThreshold"]:
                    gate = {"confidenceThreshold": threshold, "maxUncertainty": config["gate"]["maxUncertainty"],
                            "minObservedNodes": config["gate"]["minObservedNodes"],
                            "minObservedAnchors": config["gate"]["minObservedAnchors"],
                            "confirmations": config["gate"]["confirmations"]}
                    metrics = evaluate_records(splits["dev"], records, gate)
                    handoff = metrics["handoff"]
                    loop = metrics["loopClosureBelief"]
                    loop_gain = 0.0 if loop["meanAgreementBefore"] is None else max(
                        0.0, loop["meanAgreementAfter"] - loop["meanAgreementBefore"]
                    )
                    score = (metrics["level"]["macroF1"] + .35 * handoff["preciseAcquisitionRate"]
                             + .15 * handoff["levelPrecision"] + .1 * loop_gain)
                    candidates.append({"transitionMix": transition_mix, "emissionPower": emission_power,
                                       "loopFusion": loop_fusion, "gate": gate, "selectionScore": score,
                                       "metrics": metrics})
    selected = max(candidates, key=lambda item: (item["selectionScore"], item["gate"]["confidenceThreshold"]))
    emit("selection", 3, 5, f"Selected dev-only configuration from {len(candidates)} candidates")
    model = {"schemaVersion": 1, "taskId": "U06-R1", "contract": CONTRACT,
             "modelType": "visual-topology+action-conditioned-bayes+loop-closure-v1",
             "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
             "forbiddenInputs": ["pose", "markerIdentity", "targetCoordinates", "score", "groundTruthLevel", "oracleDirection"],
             "visualTopology": topology, "probabilisticLocalizer": probabilistic,
             "inference": {key: selected[key] for key in ("transitionMix", "emissionPower", "loopFusion")},
             "deploymentGate": selected["gate"]}
    model["inference"]["anchorPowerMultiplier"] = config["anchorPowerMultiplier"]
    # Heldout test is touched only after every parameter and threshold is frozen above.
    test_records = replay_all(model, splits["test"], features["test"])
    test_metrics = evaluate_records(splits["test"], test_records, selected["gate"])
    test_labels = np.asarray([LEVEL_TO_INDEX[frame.level] for frame in splits["test"]], np.int64)
    baselines = {"directSingleImageMLP": direct_baseline(baseline, features["test"], test_labels),
                 "singlePrototypeAssociation": confusion_metrics(test_labels, prototype_baseline(probabilistic, topology, features["test"]))}
    emit("heldout", 4, 5, "Completed one frozen heldout replay")
    common = {"experimentId": config["experimentId"], "datasetRunId": config["datasetRunId"],
              "datasetFingerprint": dataset_fingerprint(frames), "configSha256": hashlib.sha256(config_bytes).hexdigest(),
              "dependencySha256": {"visualTopology": hashlib.sha256(topology_path.read_bytes()).hexdigest(),
                                   "directBaseline": hashlib.sha256(baseline_path.read_bytes()).hexdigest()}}
    model.update(common)
    anchors = [item for item in topology["prototypes"] if item["candidateAnchor"]]
    t7_anchors = [item for item in anchors if int(np.argmax(item["levelDistribution"])) == LEVEL_TO_INDEX["T7"]]
    report = {"schemaVersion": 1, "taskId": "U06-R1", **common,
              "selection": {"rule": "all parameters and gate selected on development trajectories only",
                            "candidateCount": len(candidates), "selected": selected,
                            "heldoutTestUsedForSelection": False},
              "heldoutEvaluation": {"topologicalTemporal": test_metrics, "baselines": baselines},
              "absoluteLevelIdentifiability": {"candidateAnchors": len(anchors), "candidateT7Anchors": len(t7_anchors),
                                                "directT7AnchorObserved": bool(t7_anchors),
                                                "mechanism": "prototype emissions plus action-conditioned sequence counting from candidate anchors",
                                                "warning": "No universal anatomical anchor is established on a single simulated anatomy."},
              "scientificVerdict": {"beatsDirectLevelAccuracy": test_metrics["level"]["accuracy"] > baselines["directSingleImageMLP"]["accuracy"],
                                    "providesVisualLoopClosureMemory": test_metrics["loopClosureBelief"]["events"] > 0,
                                    "autoPromoted": False},
              "limitations": ["All trajectories use one simulated anatomy.",
                              "Candidate anchors are supervised posthoc associations, not proven universal anatomy anchors.",
                              "Issued actions do not prove achieved displacement.",
                              "No Unity live end-to-end improvement is claimed by this offline evaluation."]}
    traces = {"schemaVersion": 1, "taskId": "U06-R1", **common, "split": "test",
              "episodes": [{"episodeId": episode, "records": [test_records[index] for index in indices]}
                           for episode, indices in group_indices(splits["test"]).items()]}
    outputs = dict(config["outputs"])
    if args.smoke:
        outputs = {key: str(Path(value).with_name(Path(value).stem + ".smoke" + Path(value).suffix))
                   for key, value in outputs.items()}
    atomic_json(root / outputs["checkpoint"], model)
    atomic_json(root / outputs["report"], report)
    atomic_json(root / outputs["traces"], traces)
    emit("complete", 5, 5, "U06-R1 artifacts written; no automatic deployment")
    print(json.dumps({"outputs": outputs, "selected": {key: selected[key] for key in
                      ("transitionMix", "emissionPower", "loopFusion", "gate")},
                      "heldout": report["heldoutEvaluation"], "verdict": report["scientificVerdict"]}, indent=2))


if __name__ == "__main__":
    main()
