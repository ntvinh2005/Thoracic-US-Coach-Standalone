from __future__ import annotations

import argparse
import hashlib
import json
import time
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

import numpy as np

from active2d_contract import CONTRACT, LEVEL_ORDER, project_root
from active2d_data import dataset_fingerprint, feature_matrix, load_active2d_frames
from visual_landmark_topology import VisualTopologicalMap, project_features


def emit(phase: str, current: int, total: int, message: str) -> None:
    print("::localjobs_progress " + json.dumps({"phase": phase, "current": current, "total": total, "message": message}), flush=True)


def atomic_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    temporary.replace(path)


def atomic_text(path: Path, payload: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(payload, encoding="utf-8")
    temporary.replace(path)


def render_map_svg(maps: list[dict]) -> str:
    representative = max(maps, key=lambda item: (len(item["nodes"]), len(item["edges"])))
    nodes = representative["nodes"]
    positions = {node["nodeId"]: node["relativePositionAtCreation"] for node in nodes}
    lateral = [position[1] for position in positions.values()] or [0]
    longitudinal = [position[0] for position in positions.values()] or [0]
    def point(node_id: str) -> tuple[float, float]:
        y, x = positions[node_id]
        px = 80 + (x - min(lateral)) / max(1, max(lateral) - min(lateral)) * 740
        py = 70 + (y - min(longitudinal)) / max(1, max(longitudinal) - min(longitudinal)) * 430
        return px, py
    palette = ("#2563eb", "#16a34a", "#ea580c", "#9333ea", "#0891b2", "#be123c", "#4f46e5", "#65a30d")
    parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="900" height="580" viewBox="0 0 900 580">',
             '<rect width="900" height="580" fill="#f8fafc"/>',
             f'<text x="30" y="32" font-family="sans-serif" font-size="18" font-weight="bold">U05 visual topology — {representative["episodeId"]}</text>',
             '<text x="30" y="555" font-family="sans-serif" font-size="12">Coordinates are policy-owned issued-action odometry; edges and loop closures are image-derived.</text>']
    for edge in representative["edges"]:
        if edge["source"] not in positions or edge["destination"] not in positions:
            continue
        x1, y1 = point(edge["source"]); x2, y2 = point(edge["destination"])
        parts.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="#64748b" stroke-width="{1 + min(4, edge["count"]):.1f}"/>')
    for node in nodes:
        x, y = point(node["nodeId"]); color = palette[int(node["prototype"]) % len(palette)]
        parts.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{10 + min(10, node["visits"] * 2)}" fill="{color}" stroke="#0f172a" stroke-width="2"/>')
        parts.append(f'<text x="{x + 14:.1f}" y="{y + 4:.1f}" font-family="monospace" font-size="11">{node["nodeId"]} v={node["visits"]}</text>')
    parts.append('</svg>')
    return "\n".join(parts)


def fit_projection(features: np.ndarray, width: int) -> dict[str, np.ndarray]:
    mean = features.mean(0).astype(np.float32)
    _, singular, right = np.linalg.svd(features - mean, full_matrices=False)
    components = right[:width].astype(np.float32)
    scale = (singular[:width] / np.sqrt(max(1, len(features) - 1))).astype(np.float32)
    scale[scale < 1e-5] = 1.0
    return {"mean": mean, "components": components, "scale": scale}


def kmeans(values: np.ndarray, count: int, iterations: int, seed: int) -> tuple[np.ndarray, np.ndarray]:
    rng = np.random.default_rng(seed)
    centers = [values[int(rng.integers(len(values)))]]
    for _ in range(1, count):
        distance = np.min(1.0 - values @ np.stack(centers).T, axis=1)
        probability = np.maximum(distance, 1e-6) ** 2
        probability /= probability.sum()
        centers.append(values[int(rng.choice(len(values), p=probability))])
    centers = np.stack(centers)
    labels = np.zeros(len(values), np.int64)
    for _ in range(iterations):
        labels = np.argmin(1.0 - values @ centers.T, axis=1)
        updated = []
        for index in range(count):
            selected = values[labels == index]
            center = selected.mean(0) if len(selected) else values[int(rng.integers(len(values)))]
            updated.append(center / max(float(np.linalg.norm(center)), 1e-8))
        updated = np.stack(updated)
        if np.max(np.abs(updated - centers)) < 1e-5:
            centers = updated
            break
        centers = updated
    return centers.astype(np.float32), labels


def privileged_positions(frames, root: Path) -> dict[tuple[str, int], np.ndarray]:
    needed = {frame.episode_id for frame in frames}
    result = {}
    dataset = root / "Artifacts/ThoracicUSCoach/active2d_dataset"
    for path in dataset.glob("*/*/privileged_annotations.json"):
        payload = json.loads(path.read_text(encoding="utf-8"))
        episode = str(payload.get("episodeId"))
        if episode not in needed:
            continue
        for item in payload.get("frames", []):
            position = item["contactPosition"]
            result[(episode, int(item["index"]))] = np.asarray([position["x"], position["y"], position["z"]], np.float32)
    return result


def build_model(train_frames, train_embedding: np.ndarray, centers: np.ndarray, labels: np.ndarray,
                projection: dict[str, np.ndarray], config: dict[str, Any]) -> dict[str, Any]:
    distance_matrix = 1.0 - train_embedding @ centers.T
    ordered = np.sort(distance_matrix, axis=1)
    ambiguity = float(np.quantile(ordered[:, 1] - ordered[:, 0], config["assignment"]["ambiguityQuantile"]))
    prototypes = []
    for index, center in enumerate(centers):
        selected = np.where(labels == index)[0]
        distances = distance_matrix[selected, index]
        counts = Counter(train_frames[item].level for item in selected)
        distribution = np.asarray([counts[level] + 1 for level in LEVEL_ORDER], np.float64)
        distribution /= distribution.sum()
        entropy = float(-np.sum(distribution * np.log(distribution)) / np.log(len(LEVEL_ORDER)))
        families = sorted({train_frames[item].family for item in selected})
        radius = max(float(np.quantile(distances, .95)) * 1.15, .015)
        prototypes.append({"id": index, "center": center.tolist(), "matchRadius": radius,
                           "trainObservations": int(len(selected)), "trainFamilySupport": len(families),
                           "levelDistribution": distribution.tolist(), "levelEntropy": entropy,
                           "candidateAnchor": bool(len(selected) >= 20 and len(families) >= 4 and entropy <= .55),
                           "anchorMeaning": "supervised-posthoc-candidate-not-proven-absolute-anchor"})
    return {"schemaVersion": 1, "taskId": "U05-R1", "contract": CONTRACT,
            "modelType": "unsupervised-visual-prototypes+online-topological-map-v1",
            "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
            "forbiddenInputs": ["pose", "markerIdentity", "targetCoordinates", "score", "groundTruthLevel", "oracleDirection"],
            "projection": {key: value.tolist() for key, value in projection.items()}, "prototypes": prototypes,
            "assignment": {"ambiguityMargin": ambiguity, "confirmFrames": config["assignment"]["confirmFrames"],
                           "loopGapFrames": config["assignment"]["loopGapFrames"],
                           "loopClosureThreshold": config["assignment"]["loopClosureThreshold"]}}


def evaluate_maps(model: dict, frames, raw_features: np.ndarray, positions: dict, loop_mm: float) -> tuple[dict, list[dict]]:
    grouped: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        grouped[frame.episode_id].append(index)
    maps = []
    statuses = Counter()
    true_closures = 0
    false_closures = 0
    opportunities = 0
    recurrent_edges = Counter()
    level_correct = 0
    level_total = 0
    for episode, indices in sorted(grouped.items()):
        topology = VisualTopologicalMap(model)
        seen_positions: list[tuple[int, np.ndarray]] = []
        for index in indices:
            frame = frames[index]
            event = topology.observe(raw_features[index], frame.issued_action, frame.index)
            statuses[event["type"]] += 1
            position = positions.get((episode, frame.index))
            if position is not None:
                prior_close = [old_index for old_index, old_position in seen_positions
                               if frame.index - old_index >= model["assignment"]["loopGapFrames"]
                               and float(np.linalg.norm(position - old_position)) <= loop_mm]
                opportunities += int(bool(prior_close))
                if event["type"] == "LOOP_CLOSURE" and event["matchedFrameIndex"] is not None:
                    matched = positions.get((episode, int(event["matchedFrameIndex"])))
                    correct = matched is not None and float(np.linalg.norm(position - matched)) <= loop_mm
                    true_closures += int(correct)
                    false_closures += int(not correct)
                seen_positions.append((frame.index, position))
            if "levelBelief" in event:
                predicted = max(event["levelBelief"], key=event["levelBelief"].get)
                level_correct += int(predicted == frame.level)
                level_total += 1
        exported = topology.export(episode)
        maps.append(exported)
        for edge in exported["edges"]:
            recurrent_edges[(edge["source"].split("-N")[0], edge["destination"].split("-N")[0], edge["issuedAction"])] += 1
    assigned = sum(statuses[name] for name in ("CANDIDATE", "NEW_NODE", "OBSERVED", "TRANSITION", "LOOP_CLOSURE"))
    total = sum(statuses.values())
    precision = true_closures / max(1, true_closures + false_closures)
    recall = true_closures / max(1, opportunities)
    return {"frames": total, "assignmentCoverage": assigned / max(1, total), "eventCounts": dict(statuses),
            "mapNodes": sum(len(item["nodes"]) for item in maps), "mapEdges": sum(len(item["edges"]) for item in maps),
            "visualLoopClosure": {"verifiedTrue": true_closures, "verifiedFalse": false_closures,
                                  "privilegedPhysicalRevisitOpportunities": opportunities, "precision": precision,
                                  "opportunityRecall": recall, "f1": 2 * precision * recall / max(1e-12, precision + recall),
                                  "verificationOnly": "contact position used only after mapper output"},
            "recurrentTopologicalEdgesAcrossEpisodes": sum(count >= 2 for count in recurrent_edges.values()),
            "posthocLevelAccuracy": level_correct / max(1, level_total)}, maps


def main() -> None:
    parser = argparse.ArgumentParser(description="U05-R1 appearance landmark discovery and visual topological mapping")
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    root = project_root()
    config_path = args.config.resolve()
    config_bytes = config_path.read_bytes()
    config = json.loads(config_bytes)
    frames = [frame for frame in load_active2d_frames(config["datasetRunId"], root)
              if frame.contact_valid and frame.workspace_valid]
    train = [frame for frame in frames if frame.split == "train"]
    dev = [frame for frame in frames if frame.split == "dev"]
    if {frame.family for frame in train} & {frame.family for frame in dev}:
        raise SystemExit("Trajectory-family leakage between train and dev")
    if args.smoke:
        train = train[:min(240, len(train))]
        dev = dev[:min(120, len(dev))]
    emit("features", 0, 4, f"Extracting images: train={len(train)} dev={len(dev)}")
    train_features = feature_matrix(train)
    dev_features = feature_matrix(dev)
    projection = fit_projection(train_features, min(config["projectionWidth"], len(train_features) - 1))
    projection_json = {key: value.tolist() for key, value in projection.items()}
    stub = {"projection": projection_json}
    train_embedding = project_features(stub, train_features)
    emit("discovery", 1, 4, "Discovering appearance prototypes without anatomical labels")
    candidate_counts = config["prototypeCounts"][:1] if args.smoke else config["prototypeCounts"]
    candidates = []
    dev_positions = privileged_positions(dev, root)
    for number, count in enumerate(candidate_counts, 1):
        centers, labels = kmeans(train_embedding, count, 5 if args.smoke else config["kmeansIterations"], config["seed"] + count)
        model = build_model(train, train_embedding, centers, labels, projection, config)
        metrics, maps = evaluate_maps(model, dev, dev_features, dev_positions, config["loopClosurePhysicalMm"])
        family_ratio = float(np.mean([item["trainFamilySupport"] for item in model["prototypes"]])) / max(1, len({f.family for f in train}))
        score = metrics["assignmentCoverage"] + .35 * metrics["visualLoopClosure"]["f1"] + .15 * family_ratio
        candidates.append({"prototypeCount": count, "selectionScore": score, "metrics": metrics,
                           "model": model, "maps": maps})
        emit("selection", 1 + number, 4, f"Evaluated {count} prototypes on development trajectories")
    selected = max(candidates, key=lambda item: (item["selectionScore"], -item["prototypeCount"]))
    model = selected.pop("model")
    maps = selected.pop("maps")
    common = {"experimentId": config["experimentId"], "datasetRunId": config["datasetRunId"],
              "datasetFingerprint": dataset_fingerprint([*train, *dev]),
              "configSha256": hashlib.sha256(config_bytes).hexdigest()}
    model.update(common)
    report = {"schemaVersion": 1, "taskId": "U05-R1", **common,
              "claim": "image-derived appearance topology; anatomical semantics are posthoc and not guaranteed",
              "split": {"trainFrames": len(train), "devFrames": len(dev),
                        "trainFamilies": sorted({f.family for f in train}), "devFamilies": sorted({f.family for f in dev})},
              "selection": {"uses": "development trajectories only", "selectedPrototypeCount": selected["prototypeCount"],
                            "candidates": [{key: value for key, value in item.items() if key not in {"model", "maps"}}
                                           for item in candidates]},
              "selectedDevelopmentMetrics": selected["metrics"],
              "candidateAnchors": [{"prototype": item["id"], "levelDistribution": item["levelDistribution"],
                                     "entropy": item["levelEntropy"], "meaning": item["anchorMeaning"]}
                                    for item in model["prototypes"] if item["candidateAnchor"]],
              "limitations": ["Appearance prototypes are not named anatomical structures.",
                              "Absolute vertebral identity uses supervised posthoc prototype associations.",
                              "Loop closure is proposed by image embedding; contact position is evaluator-only verification.",
                              "Single simulated anatomy cannot establish patient generalization.",
                              "Development output is not a frozen heldout claim."]}
    outputs = dict(config["outputs"])
    if args.smoke:
        outputs = {key: str(Path(value).with_name(Path(value).stem + ".smoke" + Path(value).suffix))
                   for key, value in outputs.items()}
    atomic_json(root / outputs["checkpoint"], model)
    atomic_json(root / outputs["report"], report)
    atomic_json(root / outputs["maps"], {"schemaVersion": 1, "taskId": "U05-R1", **common,
                                         "split": "dev", "maps": maps})
    atomic_text(root / outputs["visualization"], render_map_svg(maps))
    emit("complete", 4, 4, "Visual topology artifacts written; no deployment promotion")
    print(json.dumps({"outputs": outputs, "selectedPrototypeCount": selected["prototypeCount"],
                      "metrics": selected["metrics"], "candidateAnchors": len(report["candidateAnchors"])}, indent=2))


if __name__ == "__main__":
    main()
