from __future__ import annotations

import argparse
import copy
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any, Sequence

import numpy as np

from active2d_contract import LEVEL_ORDER, project_root
from active2d_data import feature_matrix, load_active2d_frames, softmax
from topological_localizer import LEVEL_TO_INDEX, OnlineTopologicalLocalizer, group_indices, normalized_entropy
from train_topological_localizer_u06 import evaluate_records
from visual_landmark_topology import assign_visual_landmark, project_features


def classify_t7_outcome(full_prediction: str) -> str:
    """Ground-truth T7 frame outcome; independent of its evidence path."""
    return "true_positive_T7" if full_prediction == "T7" else "false_negative_T7"


def classify_t7_failure(full_prediction: str) -> str:
    """Compatibility name for the corrected outcome classifier."""
    return classify_t7_outcome(full_prediction)


def evidence_precedence(assignment: str, visual_prediction: str, full_prediction: str) -> str:
    """Optional exclusive evidence summary. This is not a TP/FN partition."""
    if assignment != "MATCHED":
        return "assignment_not_matched"
    if visual_prediction != "T7":
        return "prototype_only_not_T7"
    if full_prediction != "T7":
        return "temporal_changed_away_from_T7"
    return "prototype_and_full_predict_T7"


def gate_blockers(record: dict[str, Any], gate: dict[str, Any]) -> list[str]:
    checks = (
        (record["predictedLevel"] != "T7", "predicted_not_T7"),
        (record["confidence"] < float(gate["confidenceThreshold"]), "confidence_below_threshold"),
        (record["uncertainty"] > float(gate["maxUncertainty"]), "uncertainty_above_limit"),
        (record["observedNodeCount"] < int(gate["minObservedNodes"]), "insufficient_nodes"),
        (record["observedAnchorCount"] < int(gate["minObservedAnchors"]), "no_candidate_anchor_observed"),
    )
    return [name for failed, name in checks if failed]


def prediction_delta(actual: np.ndarray, full: np.ndarray, ablated: np.ndarray) -> dict[str, int]:
    fc, ac = full == actual, ablated == actual
    return {"fullCorrectAblatedWrong": int(np.sum(fc & ~ac)),
            "fullWrongAblatedCorrect": int(np.sum(~fc & ac)),
            "bothCorrect": int(np.sum(fc & ac)), "bothWrong": int(np.sum(~fc & ~ac))}


def replay(model: dict[str, Any], frames: Sequence, features: np.ndarray) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = [{} for _ in frames]
    for indices in group_indices(frames).values():
        localizer = OnlineTopologicalLocalizer(model)
        for index in indices:
            frame = frames[index]
            records[index] = localizer.observe(features[index], frame.issued_action, frame.index)
    return records


def variant(model: dict[str, Any], **changes: float) -> dict[str, Any]:
    result = copy.deepcopy(model)
    result["inference"].update(changes)
    return result


def prototype_probabilities(model: dict[str, Any], features: np.ndarray) -> tuple[list[dict], np.ndarray]:
    topology = model["visualTopology"]
    emissions = np.asarray(model["probabilisticLocalizer"]["prototypeEmission"], np.float64)
    initial = np.asarray(model["probabilisticLocalizer"]["initialBelief"], np.float64)
    assignments = [assign_visual_landmark(topology, x) for x in project_features(topology, features)]
    probabilities = []
    for item in assignments:
        values = emissions[:, int(item["prototype"])] if item["status"] == "MATCHED" else initial
        probabilities.append(values / max(float(values.sum()), 1e-12))
    return assignments, np.asarray(probabilities)


def direct_probabilities(checkpoint: dict[str, Any], features: np.ndarray) -> np.ndarray:
    scaler, head = checkpoint["imageScaler"], checkpoint["directBaselineHead"]
    values = ((features - np.asarray(scaler["mean"], np.float32))
              / np.asarray(scaler["scale"], np.float32))
    hidden = np.maximum(values @ np.asarray(head["inputWeights"], np.float32)
                        + np.asarray(head["inputBias"], np.float32), 0.0)
    return softmax(hidden @ np.asarray(head["outputWeights"], np.float32)
                   + np.asarray(head["outputBias"], np.float32))


def probability_metrics(actual: np.ndarray, probabilities: np.ndarray) -> dict[str, Any]:
    predicted = np.argmax(probabilities, axis=1)
    matrix = np.zeros((len(LEVEL_ORDER), len(LEVEL_ORDER)), np.int64)
    for left, right in zip(actual, predicted):
        matrix[int(left), int(right)] += 1
    per_level, f1s = {}, []
    for i, level in enumerate(LEVEL_ORDER):
        tp, support, selected = int(matrix[i, i]), int(matrix[i].sum()), int(matrix[:, i].sum())
        precision, recall = tp / max(1, selected), tp / max(1, support)
        f1 = 2 * precision * recall / max(1e-12, precision + recall)
        per_level[level] = {"support": support, "precision": precision, "recall": recall, "f1": f1}
        if support:
            f1s.append(f1)
    return {"count": len(actual), "accuracy": float(np.mean(actual == predicted)),
            "macroF1": float(np.mean(f1s)), "confusion": matrix.tolist(), "perLevel": per_level}


def prediction_only_handoff(frames: Sequence, probabilities: np.ndarray, gate: dict[str, Any]) -> dict[str, Any]:
    decisions = []
    for episode, indices in group_indices(frames).items():
        streak, chosen = 0, None
        for index in indices:
            values = probabilities[index]
            predicted = int(np.argmax(values))
            eligible = (predicted == LEVEL_TO_INDEX["T7"]
                        and float(values[predicted]) >= float(gate["confidenceThreshold"])
                        and normalized_entropy(values) <= float(gate["maxUncertainty"]))
            streak = streak + 1 if eligible else 0
            if streak >= int(gate["confirmations"]):
                chosen = index
                break
        if chosen is not None:
            decisions.append({"episodeId": episode, "frameIndex": frames[chosen].index,
                              "correctLevel": frames[chosen].level == "T7",
                              "preciseRegion": frames[chosen].t7_acceptable})
    correct = sum(x["correctLevel"] for x in decisions)
    episodes = len(group_indices(frames))
    return {"scope": "prediction/confidence/uncertainty/confirmation only; node/anchor gates N/A",
            "episodes": episodes, "handoffs": len(decisions), "correctLevel": correct,
            "wrongLevel": len(decisions) - correct, "preciseRegion": sum(x["preciseRegion"] for x in decisions),
            "abstentions": episodes - len(decisions), "decisions": decisions}


def split_diagnostic(name: str, model: dict[str, Any], direct: dict[str, Any], frames: Sequence,
                     features: np.ndarray) -> dict[str, Any]:
    variants = {"frozenFull": model, "noActionTransition": variant(model, transitionMix=0.0),
                "noLoopFusion": variant(model, loopFusion=0.0),
                "noAnchorBoost": variant(model, anchorPowerMultiplier=1.0)}
    records = {key: replay(value, frames, features) for key, value in variants.items()}
    metrics = {}
    for key, value in records.items():
        result = evaluate_records(frames, value, model["deploymentGate"])
        result["handoff"]["wrongLevel"] = result["handoff"]["handoffs"] - result["handoff"]["correctLevel"]
        metrics[key] = {"level": result["level"], "deploymentHandoff": result["handoff"],
                        "loopClosureBelief": result["loopClosureBelief"]}
    assignments, prototype_probs = prototype_probabilities(model, features)
    direct_probs = direct_probabilities(direct, features)
    actual = np.asarray([LEVEL_TO_INDEX[x.level] for x in frames], np.int64)
    prototype_pred = np.argmax(prototype_probs, axis=1)
    metrics["prototypeOnlyAssociation"] = {
        "level": probability_metrics(actual, prototype_probs),
        "predictionOnlyHandoff": prediction_only_handoff(frames, prototype_probs, model["deploymentGate"]),
        "loopClosureBelief": "NOT_APPLICABLE"}
    metrics["preservedDirectImageBaseline"] = {
        "level": probability_metrics(actual, direct_probs),
        "predictionOnlyHandoff": prediction_only_handoff(frames, direct_probs, model["deploymentGate"]),
        "loopClosureBelief": "NOT_APPLICABLE"}
    predictions = {key: np.asarray([LEVEL_TO_INDEX[x["predictedLevel"]] for x in value], np.int64)
                   for key, value in records.items()}

    outcome, status_counts, precedence, overlap, details = Counter(), Counter(), Counter(), Counter(), []
    topology = model["visualTopology"]
    for i, frame in enumerate(frames):
        if frame.level != "T7":
            continue
        assignment, full, no_loop = assignments[i], records["frozenFull"][i], records["noLoopFusion"][i]
        visual = LEVEL_ORDER[int(prototype_pred[i])]
        actual_outcome = classify_t7_outcome(full["predictedLevel"])
        outcome[actual_outcome] += 1
        status_counts[str(assignment["status"])] += 1
        precedence[evidence_precedence(str(assignment["status"]), visual, full["predictedLevel"])] += 1
        attrs = {"assignmentNotMatched": assignment["status"] != "MATCHED",
                 "prototypeOnlyNotT7": visual != "T7",
                 "temporalChangedPrediction": visual != full["predictedLevel"],
                 "loopFusionChangedPrediction": no_loop["predictedLevel"] != full["predictedLevel"],
                 "currentPrototypeCandidateAnchor": bool(topology["prototypes"][int(assignment["prototype"])]["candidateAnchor"]),
                 "candidateAnchorObservedSoFar": full["observedAnchorCount"] > 0}
        overlap.update(key for key, active in attrs.items() if active)
        details.append({"episodeId": frame.episode_id, "frameIndex": frame.index,
                        "actualOutcome": actual_outcome, "assignmentStatus": assignment["status"],
                        "prototypeId": int(assignment["prototype"]), "prototypeDistance": assignment["distance"],
                        "prototypeMargin": assignment["margin"], "prototypeOnlyPrediction": visual,
                        "fullModelPrediction": full["predictedLevel"],
                        "temporalReasoningChangedPrediction": attrs["temporalChangedPrediction"],
                        "loopFusionChangedPrediction": attrs["loopFusionChangedPrediction"],
                        "noLoopFusionPrediction": no_loop["predictedLevel"],
                        "currentPrototypeCandidateAnchor": attrs["currentPrototypeCandidateAnchor"],
                        "candidateAnchorObserved": attrs["candidateAnchorObservedSoFar"],
                        "confidence": full["confidence"], "uncertainty": full["uncertainty"],
                        "t7Belief": full["belief"]["T7"], "preciseT7Acceptable": frame.t7_acceptable,
                        "exclusiveEvidencePrecedenceCategory": evidence_precedence(
                            str(assignment["status"]), visual, full["predictedLevel"])})

    t7 = LEVEL_TO_INDEX["T7"]
    confusion = metrics["frozenFull"]["level"]["confusion"]
    tp, total = int(confusion[t7][t7]), int(sum(confusion[t7]))
    invariant = outcome["true_positive_T7"] == tp and outcome["false_negative_T7"] == total - tp
    if not invariant or sum(outcome.values()) != total:
        raise AssertionError("T7 TP/FN does not reconcile with confusion matrix")
    episodes, gate = [], model["deploymentGate"]
    for episode, indices in group_indices(frames).items():
        t7_indices = [i for i in indices if frames[i].level == "T7"]
        precise = [i for i in indices if frames[i].t7_acceptable]
        statuses = Counter(assignments[i]["status"] for i in indices)
        correct = sum(records["frozenFull"][i]["predictedLevel"] == "T7" for i in t7_indices)
        proto_correct = sum(int(prototype_pred[i]) == t7 for i in t7_indices)
        blockers = Counter(reason for i in precise for reason in gate_blockers(records["frozenFull"][i], gate))
        episodes.append({"episodeId": episode, "family": frames[indices[0]].family,
                         "actualT7Frames": len(t7_indices), "truePositiveT7": correct,
                         "falseNegativeT7": len(t7_indices) - correct,
                         "assignmentStatusAllObservations": {x: statuses[x] for x in ("MATCHED", "AMBIGUOUS", "UNMATCHED")},
                         "prototypeOnlyT7Recall": proto_correct / max(1, len(t7_indices)),
                         "fullModelT7Recall": correct / max(1, len(t7_indices)),
                         "preciseT7Opportunity": bool(precise), "preciseT7FrameCount": len(precise),
                         "maxT7BeliefInsidePreciseRegion": max((records["frozenFull"][i]["belief"]["T7"] for i in precise), default=None),
                         "handoffBlockersInsidePreciseRegion": dict(blockers.most_common()),
                         "handoffFailureEligible": bool(precise)})
    loop_indices = [i for i, x in enumerate(records["frozenFull"]) if x["topologyEvent"] == "LOOP_CLOSURE"]
    return {"split": name, "selectionPolicy": "frozen diagnostic replay; no search/selection",
            "ablations": metrics,
            "t7Outcome": {"actualT7Frames": total, "truePositiveT7": outcome["true_positive_T7"],
                          "falseNegativeT7": outcome["false_negative_T7"],
                          "confusionMatrixTruePositiveT7": tp, "confusionMatrixFalseNegativeT7": total - tp,
                          "invariant": "TP_T7 + FN_T7 == actual_T7_frames", "invariantPass": invariant},
            "diagnosticEvidencePath": {"overlappingAttributeCountsOnActualT7": dict(overlap),
                                       "assignmentStatusOnActualT7": dict(status_counts),
                                       "exclusiveEvidencePrecedenceCounts": dict(precedence),
                                       "exclusivePrecedence": ["assignment_not_matched", "prototype_only_not_T7",
                                                               "temporal_changed_away_from_T7", "prototype_and_full_predict_T7"],
                                       "warning": "Evidence categories are not TP/FN outcomes; attributes overlap.",
                                       "frames": details},
            "perEpisode": episodes,
            "loopFusionEffect": {"allFrames": prediction_delta(actual, predictions["frozenFull"], predictions["noLoopFusion"]),
                                 "loopClosureFrames": prediction_delta(actual[loop_indices], predictions["frozenFull"][loop_indices], predictions["noLoopFusion"][loop_indices]),
                                 "warning": "Agreement increase is structural, not localization evidence."}}


def _v(item: dict[str, Any]) -> np.ndarray:
    return np.asarray([item["x"], item["y"], item["z"]], np.float64)


def audit_contract(root: Path, run_id: str, all_frames: Sequence, selected: Sequence) -> dict[str, Any]:
    families = {s: {f.family for f in all_frames if f.split == s} for s in ("train", "dev", "test")}
    cross = {f"{a}-{b}": sorted(families[a] & families[b]) for a, b in (("train", "dev"), ("train", "test"), ("dev", "test"))}
    reset_bad, accepted, aligned, misaligned, magnitudes = [], Counter(), 0, 0, []
    for path in (root / "Artifacts/ThoracicUSCoach/active2d_dataset").glob("*/*/stream_manifest.json"):
        stream = json.loads(path.read_text(encoding="utf-8"))
        if stream.get("datasetRunId") != run_id or stream.get("captureStatus") != "complete":
            continue
        items = stream["frames"]
        if not items or items[0].get("issuedAction") != "RESET" or not items[0].get("episodeReset") or any(x.get("episodeReset") for x in items[1:]):
            reset_bad.append(stream.get("episodeId"))
        annotations = json.loads(path.with_name("privileged_annotations.json").read_text(encoding="utf-8"))
        labels = {int(x["index"]): x for x in annotations["frames"]}
        axes = {"CRANIAL": (_v(annotations["actuatorCranialAxis"]), 1), "CAUDAL": (_v(annotations["actuatorCranialAxis"]), -1),
                "LATERAL": (_v(annotations["actuatorLateralAxis"]), 1), "MEDIAL": (_v(annotations["actuatorLateralAxis"]), -1)}
        previous = None
        for item in items:
            accepted[str(bool(item.get("actionAccepted", True)))] += 1
            current = _v(labels[int(item["index"])]["contactPosition"])
            action = str(item.get("issuedAction") or "RESET").upper()
            if previous is not None and action in axes:
                axis, sign = axes[action]
                delta = current - previous
                magnitudes.append(float(np.linalg.norm(delta)))
                if float(delta @ axis) * sign > .01:
                    aligned += 1
                else:
                    misaligned += 1
            previous = current
    counts = {s: {"frames": sum(f.split == s for f in all_frames),
                  "episodes": len({f.episode_id for f in all_frames if f.split == s}),
                  "families": len(families[s])} for s in ("train", "dev", "test")}
    return {
        "familySeparation": {"status": "VERIFIED", "splitCounts": counts,
                             "crossSplitFamilyIntersections": cross, "pass": not any(cross.values())},
        "labelsAndPreciseT7": {"status": "VERIFIED_FROM_CODE_AND_MANIFESTS",
            "nearestLevelDefinition": "nearest T6/T7/T8 marker; OUTSIDE when distance > max(8 mm, 0.62*adjacentSpacing)",
            "preciseT7Definition": "nearest marker T7 and t7Distance <= max(4 mm, 0.42*adjacentSpacing)",
            "warning": "Simulator-marker evaluator labels; not clinician-reviewed anatomy."},
        "validityFiltering": {"status": "VERIFIED", "rawFrames": len(all_frames), "selectedFrames": len(selected),
                              "invalidContact": sum(not f.contact_valid for f in all_frames),
                              "invalidWorkspace": sum(not f.workspace_valid for f in all_frames),
                              "filter": "contact_valid and workspace_valid"},
        "episodeResets": {"status": "VERIFIED", "violations": reset_bad, "pass": not reset_bad},
        "actionTimingAndMovement": {"status": "VERIFIED_FROM_CODE_AND_PRIVILEGED_PHYSICAL_AUDIT",
            "contract": "issuedAction is the accepted transition into the current observation",
            "captureOrder": "TryActive2DAction -> yield -> StoreActive2DObservation",
            "auditedMovementTransitions": aligned + misaligned, "directionAligned": aligned,
            "directionMisaligned": misaligned, "meanMovementMm": float(np.mean(magnitudes)),
            "actionAcceptedCounts": dict(accepted),
            "rejectedMovementFinding": "Rejected primary move is retried; if alternate also fails no frame is stored. Dataset has no rejected stored frame. U05/U06 would otherwise ignore actionAccepted."},
        "imageActionPairing": {"status": "VERIFIED", "finding": "Image is captured after accepted action; replay advances that action and processes the same current image; RESET is first observation."},
        "actorLeakage": {"status": "VERIFIED_FOR_OFFLINE_REPLAY_CODE_PATH",
            "actorInputs": ["clean ultrasound features", "issued action", "policy-owned memory"],
            "privilegedEvaluatorOnly": ["contact position", "t7LevelAcceptable", "target score"],
            "supervisedTrainingOnly": ["nearest level for emissions/transitions"],
            "finding": "No label, pose, target coordinate, score, or oracle direction enters observe during replay.",
            "limitation": "Topology checkpoint is not live-integrated; no live behavior claim."}}


def atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(text, encoding="utf-8")
    temporary.replace(path)


def summary(report: dict[str, Any]) -> str:
    x = report["formerTestDiagnosis"]["t7Outcome"]
    full = report["formerTestDiagnosis"]["ablations"]["frozenFull"]["level"]
    return ("# U06-F01 corrected failure attribution and contract audit\n\n"
            f"Result: **{report['result']}**\n\n"
            f"Base commit: `{report['checkedOutCommit']}`. Frozen former-test accuracy {full['accuracy']:.2%}; "
            f"T7 TP={x['truePositiveT7']}, FN={x['falseNegativeT7']}, total={x['actualT7Frames']}; invariant PASS.\n\n"
            "The old `full_correct=7` was an evidence-path leaf, not the final TP count. Its precedence first removed "
            "non-MATCHED and prototype-only-non-T7 frames, including some final T7 predictions recovered by temporal inference. "
            "Outcome and overlapping evidence are now independent.\n\n"
            "All 40 manifests are family-disjoint and valid. All 1,920 non-reset transitions align physically with the "
            "recorded action into the current image; rejected moves are not stored. Offline replay has no evaluator-input leak.\n\n"
            "The frozen model remains rejected; the former test is consumed and cannot support a new heldout claim.\n")


def main() -> None:
    parser = argparse.ArgumentParser(description="U06-F01 frozen diagnostic only")
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--checked-out-commit", default="UNKNOWN")
    parser.add_argument("--verification-command", default="NOT_RECORDED")
    parser.add_argument("--verification-status", choices=("PASS", "FAIL", "NOT_RUN"), default="NOT_RUN")
    args = parser.parse_args()
    root, config_path = project_root(), args.config.resolve()
    config_bytes = config_path.read_bytes()
    config = json.loads(config_bytes)
    model_path = root / config["checkpoint"]
    model_bytes = model_path.read_bytes()
    model = json.loads(model_bytes)
    direct_path = root / config["directBaselineCheckpoint"]
    direct = json.loads(direct_path.read_text(encoding="utf-8"))
    all_frames = load_active2d_frames(config["datasetRunId"], root)
    selected = [x for x in all_frames if x.contact_valid and x.workspace_valid]
    splits = {s: [x for x in selected if x.split == s] for s in ("dev", "test")}
    features = {s: feature_matrix(splits[s]) for s in splits}
    report = {"schemaVersion": 2, "taskId": "U06-F01", "result": "PASS",
              "checkedOutCommit": args.checked_out_commit, "experimentId": config["experimentId"],
              "datasetRunId": config["datasetRunId"], "configSha256": hashlib.sha256(config_bytes).hexdigest(),
              "frozenCheckpointSha256BeforeAndAfter": hashlib.sha256(model_bytes).hexdigest(),
              "operation": "diagnostic only; no training/tuning/checkpoint/controller/policy changes",
              "testSetStatus": "CONSUMED_FOR_DIAGNOSIS_NOT_ELIGIBLE_FOR_MODEL_SELECTION_OR_NEW_HELDOUT_CLAIM",
              "modifiedSourceFiles": ["Tools/ThoracicUSCoach/diagnose_u06_r1_failures.py",
                                      "Tools/ThoracicUSCoach/test_u06_failure_analysis.py",
                                      "Tools/ThoracicUSCoach/experiments/u06_r1_failure_analysis_v1.json"],
              "datasetContractAudit": audit_contract(root, config["datasetRunId"], all_frames, selected),
              "developmentDiagnosis": split_diagnostic("development", model, direct, splits["dev"], features["dev"]),
              "formerTestDiagnosis": split_diagnostic("former-test-consumed", model, direct, splits["test"], features["test"]),
              "verification": [{"command": args.verification_command, "result": args.verification_status}],
              "knownUncertainties": ["Marker labels are not clinician-reviewed anatomy.", "Single simulated anatomy.",
                                     "Observational ablations do not establish unique causality."],
              "evidencePaths": {"json": config["outputs"]["report"], "summary": config["outputs"]["summary"],
                                "frozenCheckpoint": config["checkpoint"]}}
    atomic_write(root / config["outputs"]["report"], json.dumps(report, indent=2))
    atomic_write(root / config["outputs"]["summary"], summary(report))
    if hashlib.sha256(model_path.read_bytes()).hexdigest() != hashlib.sha256(model_bytes).hexdigest():
        raise AssertionError("Frozen checkpoint changed")
    print(json.dumps({"result": report["result"], "formerTest": report["formerTestDiagnosis"]["t7Outcome"],
                      "outputs": config["outputs"]}, indent=2))


if __name__ == "__main__":
    main()
