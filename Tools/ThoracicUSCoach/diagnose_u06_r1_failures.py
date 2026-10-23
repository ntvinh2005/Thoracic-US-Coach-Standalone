from __future__ import annotations

import argparse
import copy
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image, ImageDraw

from active2d_contract import LEVEL_ORDER, project_root
from active2d_data import feature_matrix, load_active2d_frames
from topological_localizer import LEVEL_TO_INDEX, OnlineTopologicalLocalizer, group_indices
from train_topological_localizer_u06 import evaluate_records
from visual_landmark_topology import assign_visual_landmark, project_features


def classify_t7_failure(assignment_status: str, visual_prediction: str, full_prediction: str) -> str:
    if assignment_status != "MATCHED":
        return "assignment_unavailable"
    if visual_prediction != "T7":
        return "prototype_not_T7"
    if full_prediction != "T7":
        return "temporal_suppression"
    return "full_correct"


def gate_blockers(record: dict[str, Any], gate: dict[str, Any]) -> list[str]:
    blockers = []
    if record["predictedLevel"] != "T7": blockers.append("predicted_not_T7")
    if record["confidence"] < float(gate["confidenceThreshold"]): blockers.append("confidence_below_threshold")
    if record["uncertainty"] > float(gate["maxUncertainty"]): blockers.append("uncertainty_above_limit")
    if record["observedNodeCount"] < int(gate["minObservedNodes"]): blockers.append("insufficient_nodes")
    if record["observedAnchorCount"] < int(gate["minObservedAnchors"]): blockers.append("no_candidate_anchor_observed")
    return blockers


def prediction_delta(actual: np.ndarray, full: np.ndarray, ablated: np.ndarray) -> dict[str, int]:
    full_correct = full == actual; ablated_correct = ablated == actual
    return {"fullCorrectAblatedWrong": int(np.sum(full_correct & ~ablated_correct)),
            "fullWrongAblatedCorrect": int(np.sum(~full_correct & ablated_correct)),
            "bothCorrect": int(np.sum(full_correct & ablated_correct)),
            "bothWrong": int(np.sum(~full_correct & ~ablated_correct))}


def replay(model: dict[str, Any], frames, features: np.ndarray) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = [{} for _ in frames]
    for indices in group_indices(frames).values():
        localizer = OnlineTopologicalLocalizer(model)
        for index in indices:
            records[index] = localizer.observe(features[index], frames[index].issued_action, frames[index].index)
    return records


def variant(model: dict[str, Any], **changes: float) -> dict[str, Any]:
    result = copy.deepcopy(model)
    result["inference"].update(changes)
    return result


def prototype_predictions(model: dict[str, Any], features: np.ndarray) -> tuple[list[dict], np.ndarray]:
    topology = model["visualTopology"]
    emissions = np.asarray(model["probabilisticLocalizer"]["prototypeEmission"], np.float64)
    assignments = [assign_visual_landmark(topology, embedding) for embedding in project_features(topology, features)]
    predicted = np.asarray([np.argmax(emissions[:, int(item["prototype"])]) if item["status"] == "MATCHED" else -1
                            for item in assignments], np.int64)
    return assignments, predicted


def split_diagnostic(name: str, model: dict[str, Any], frames, features: np.ndarray) -> dict[str, Any]:
    variants = {
        "frozenFull": model,
        "noTransition": variant(model, transitionMix=0.0),
        "noLoopFusion": variant(model, loopFusion=0.0),
        "noAnchorBoost": variant(model, anchorPowerMultiplier=1.0),
    }
    records = {key: replay(value, frames, features) for key, value in variants.items()}
    metrics = {key: evaluate_records(frames, value, model["deploymentGate"]) for key, value in records.items()}
    assignments, visual_predictions = prototype_predictions(model, features)
    actual = np.asarray([LEVEL_TO_INDEX[frame.level] for frame in frames], np.int64)
    predictions = {key: np.asarray([LEVEL_TO_INDEX[item["predictedLevel"]] for item in value], np.int64)
                   for key, value in records.items()}
    t7_causes = Counter()
    prototype_counts = Counter()
    for index, frame in enumerate(frames):
        if frame.level != "T7":
            continue
        assignment = assignments[index]
        visual = "UNAVAILABLE" if visual_predictions[index] < 0 else LEVEL_ORDER[int(visual_predictions[index])]
        t7_causes[classify_t7_failure(assignment["status"], visual, records["frozenFull"][index]["predictedLevel"])] += 1
        prototype_counts[str(assignment["prototype"])] += 1
    loop_indices = [index for index, record in enumerate(records["frozenFull"])
                    if record["topologyEvent"] == "LOOP_CLOSURE"]
    loop_actual = actual[loop_indices]
    loop_full = predictions["frozenFull"][loop_indices]
    loop_no_fusion = predictions["noLoopFusion"][loop_indices]
    blockers = Counter()
    episode_failures = []
    gate = model["deploymentGate"]
    for episode, indices in group_indices(frames).items():
        precise = [index for index in indices if frames[index].t7_acceptable]
        for index in precise:
            blockers.update(gate_blockers(records["frozenFull"][index], gate))
        episode_failures.append({"episodeId": episode, "preciseT7Frames": len(precise),
                                 "maxT7BeliefAtPreciseFrames": max((records["frozenFull"][i]["belief"]["T7"] for i in precise), default=None),
                                 "blockersAtPreciseFrames": dict(Counter(reason for i in precise
                                                                         for reason in gate_blockers(records["frozenFull"][i], gate)))})
    return {"split": name, "selectionPolicy": "diagnostic-only; no parameter selection",
            "ablations": {key: {"level": value["level"], "handoff": value["handoff"],
                                 "loopClosureBelief": value["loopClosureBelief"]} for key, value in metrics.items()},
            "t7FailureAttribution": {"actualT7Frames": int(np.sum(actual == LEVEL_TO_INDEX["T7"])),
                                     "counts": dict(t7_causes), "prototypeFrequencyOnT7": dict(prototype_counts)},
            "gateFailure": {"blockersAcrossPreciseT7Frames": dict(blockers), "episodes": episode_failures},
            "loopFusionEffect": {"allFrames": prediction_delta(actual, predictions["frozenFull"], predictions["noLoopFusion"]),
                                 "loopClosureFrames": prediction_delta(loop_actual, loop_full, loop_no_fusion),
                                 "warning": "agreement increase is structural and is not evidence of correct localization"}}


def representative_audit(model: dict[str, Any], frames, features: np.ndarray, output: Path, per_prototype: int) -> list[dict]:
    topology = model["visualTopology"]
    embeddings = project_features(topology, features)
    centers = np.asarray([item["center"] for item in topology["prototypes"]], np.float32)
    distances = 1.0 - embeddings @ centers.T
    selections = []
    for prototype in range(len(centers)):
        order = np.argsort(distances[:, prototype])
        chosen = []; episodes = set()
        for index in order:
            assignment = assign_visual_landmark(topology, embeddings[index])
            if int(assignment["prototype"]) != prototype or assignment["status"] != "MATCHED":
                continue
            if frames[index].episode_id in episodes and len(episodes) < per_prototype:
                continue
            chosen.append(int(index)); episodes.add(frames[index].episode_id)
            if len(chosen) >= per_prototype: break
        selections.append({"prototype": prototype, "candidateAnchor": topology["prototypes"][prototype]["candidateAnchor"],
                           "posthocLevelDistribution": topology["prototypes"][prototype]["levelDistribution"],
                           "representatives": [{"image": frames[index].image_relative, "episodeId": frames[index].episode_id,
                                                "frameIndex": frames[index].index, "evaluatorLevel": frames[index].level,
                                                "distance": float(distances[index, prototype])} for index in chosen],
                           "humanAnatomyReview": "PENDING"})
    thumb = 120; block_columns = 4
    block_width = per_prototype * thumb; block_height = thumb + 48
    block_rows = (len(selections) + block_columns - 1) // block_columns
    sheet = Image.new("RGB", (block_columns * block_width, block_rows * block_height), "white")
    draw = ImageDraw.Draw(sheet)
    for item_index, item in enumerate(selections):
        block_x = (item_index % block_columns) * block_width
        block_y = (item_index // block_columns) * block_height
        draw.rectangle((block_x, block_y, block_x + block_width - 2, block_y + block_height - 2), outline="#94a3b8")
        draw.text((block_x + 4, block_y + 3),
                  f"P{item['prototype']} anchor={item['candidateAnchor']} anatomy=PENDING", fill="black")
        for column, representative in enumerate(item["representatives"]):
            frame = next(frame for frame in frames if frame.episode_id == representative["episodeId"]
                         and frame.index == representative["frameIndex"])
            with Image.open(frame.image) as image:
                tile = image.convert("RGB"); tile.thumbnail((thumb, thumb - 24))
                sheet.paste(tile, (block_x + column * thumb, block_y + 22))
            draw.text((block_x + column * thumb + 3, block_y + thumb + 24),
                      f"{representative['evaluatorLevel']} d={representative['distance']:.3f}", fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)
    return selections


def markdown(report: dict[str, Any]) -> str:
    test = report["testDiagnosis"]
    counts = test["t7FailureAttribution"]["counts"]
    full = test["ablations"]["frozenFull"]["level"]
    return "\n".join([
        "# U06-R1 Failure Analysis",
        "", "Status: **FAILED SCIENTIFIC GATE — DO NOT DEPLOY**", "",
        f"Frozen test accuracy: {full['accuracy']:.2%}; T7 recall: {full['perLevel']['T7']['recall']:.2%}.",
        f"T7 failure attribution: `{json.dumps(counts, sort_keys=True)}`.", "",
        "The prior final test is consumed for diagnosis and must not be reused for tuning or a new heldout claim.",
        "Open the prototype contact sheet and have an anatomy expert label visible structures; automated cluster identity is not anatomy proof.",
    ])


def atomic_write(path: Path, payload: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp"); temporary.write_text(payload, encoding="utf-8"); temporary.replace(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="Diagnostic-only U06-R1 failure analysis; performs no training")
    parser.add_argument("--config", type=Path, required=True); parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args(); root = project_root(); config_path = args.config.resolve(); config_bytes = config_path.read_bytes()
    config = json.loads(config_bytes); model_path = root / config["checkpoint"]
    model = json.loads(model_path.read_text(encoding="utf-8"))
    frames = [frame for frame in load_active2d_frames(config["datasetRunId"], root)
              if frame.contact_valid and frame.workspace_valid]
    splits = {name: [frame for frame in frames if frame.split == name] for name in ("dev", "test")}
    if args.smoke: splits = {name: values[:min(100, len(values))] for name, values in splits.items()}
    print("::localjobs_progress " + json.dumps({"phase": "features", "current": 0, "total": 4,
                                                 "message": "Extracting dev/test features for frozen diagnostics"}), flush=True)
    features = {name: feature_matrix(values) for name, values in splits.items()}
    dev = split_diagnostic("development", model, splits["dev"], features["dev"])
    print("::localjobs_progress " + json.dumps({"phase": "dev-ablation", "current": 1, "total": 4,
                                                 "message": "Completed fixed development ablations"}), flush=True)
    test = split_diagnostic("former-heldout-consumed-for-diagnosis", model, splits["test"], features["test"])
    outputs = dict(config["outputs"])
    if args.smoke:
        outputs = {key: str(Path(value).with_name(Path(value).stem + ".smoke" + Path(value).suffix))
                   for key, value in outputs.items()}
    representatives = representative_audit(model, splits["dev"], features["dev"], root / outputs["contactSheet"],
                                           config["representativesPerPrototype"])
    print("::localjobs_progress " + json.dumps({"phase": "prototype-audit", "current": 3, "total": 4,
                                                 "message": "Rendered appearance prototypes for human anatomy review"}), flush=True)
    report = {"schemaVersion": 1, "taskId": "U06-R1-FAILURE-ANALYSIS",
              "experimentId": config["experimentId"], "datasetRunId": config["datasetRunId"],
              "configSha256": hashlib.sha256(config_bytes).hexdigest(),
              "frozenCheckpointSha256": hashlib.sha256(model_path.read_bytes()).hexdigest(),
              "operation": "diagnostic replay only; no training and no controller changes",
              "scientificStatus": "U06-R1_REJECTED_DO_NOT_DEPLOY",
              "testSetStatus": "CONSUMED_FOR_FAILURE_DIAGNOSIS_NOT_ELIGIBLE_FOR_FUTURE_SELECTION_OR_HELDOUT_CLAIM",
              "developmentAblations": dev, "testDiagnosis": test, "prototypeAudit": representatives,
              "requiredNextEvidence": ["Human anatomical review of prototype contact sheet.",
                                       "Explain development-to-test T7 collapse without tuning on the consumed test.",
                                       "Create a new independent locked test family before any future performance claim."]}
    atomic_write(root / outputs["report"], json.dumps(report, indent=2))
    atomic_write(root / outputs["summary"], markdown(report))
    print("::localjobs_progress " + json.dumps({"phase": "complete", "current": 4, "total": 4,
                                                 "message": "Diagnostic artifacts written; model remains rejected"}), flush=True)
    print(json.dumps({"outputs": outputs, "scientificStatus": report["scientificStatus"],
                      "testT7Causes": test["t7FailureAttribution"], "testLoopFusion": test["loopFusionEffect"]}, indent=2))


if __name__ == "__main__":
    main()
