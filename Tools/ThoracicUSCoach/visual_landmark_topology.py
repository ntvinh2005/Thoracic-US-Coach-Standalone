from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass, field
from typing import Any

import numpy as np

from active2d_contract import LEVEL_ORDER


def project_features(model: dict[str, Any], features: np.ndarray) -> np.ndarray:
    values = np.asarray(features, np.float32)
    mean = np.asarray(model["projection"]["mean"], np.float32)
    components = np.asarray(model["projection"]["components"], np.float32)
    scale = np.asarray(model["projection"]["scale"], np.float32)
    projected = ((values - mean) @ components.T) / scale
    norm = np.linalg.norm(projected, axis=-1, keepdims=True)
    return projected / np.maximum(norm, 1e-8)


def assign_visual_landmark(model: dict[str, Any], embedding: np.ndarray) -> dict[str, Any]:
    centers = np.asarray([item["center"] for item in model["prototypes"]], np.float32)
    distances = 1.0 - centers @ embedding
    order = np.argsort(distances)
    best, second = int(order[0]), int(order[1])
    radius = float(model["prototypes"][best]["matchRadius"])
    margin = float(distances[second] - distances[best])
    if float(distances[best]) > radius:
        status = "UNMATCHED"
    elif margin < float(model["assignment"]["ambiguityMargin"]):
        status = "AMBIGUOUS"
    else:
        status = "MATCHED"
    confidence = max(0.0, min(1.0, 1.0 - float(distances[best]) / max(radius, 1e-6)))
    return {"status": status, "prototype": best, "distance": float(distances[best]),
            "margin": margin, "confidence": confidence}


@dataclass
class VisualNode:
    node_id: str
    prototype: int
    centroid: np.ndarray
    first_frame: int
    last_frame: int
    visits: int = 1
    observation_count: int = 1
    confidence_sum: float = 0.0
    relative_longitudinal: int = 0
    relative_lateral: int = 0


@dataclass
class VisualTopologicalMap:
    model: dict[str, Any]
    nodes: list[VisualNode] = field(default_factory=list)
    edges: dict[tuple[str, str, str], int] = field(default_factory=lambda: defaultdict(int))
    events: list[dict[str, Any]] = field(default_factory=list)
    level_belief: np.ndarray = field(default_factory=lambda: np.full(len(LEVEL_ORDER), 1.0 / len(LEVEL_ORDER)))
    active_node: str | None = None
    candidate_prototype: int | None = None
    candidate_count: int = 0
    longitudinal_index: int = 0
    lateral_index: int = 0

    def _advance(self, action: str) -> None:
        action = action.upper()
        if action == "CRANIAL":
            self.longitudinal_index -= 1
        elif action == "CAUDAL":
            self.longitudinal_index += 1
        elif action == "MEDIAL":
            self.lateral_index -= 1
        elif action == "LATERAL":
            self.lateral_index += 1

    def _node(self, node_id: str | None) -> VisualNode | None:
        return next((node for node in self.nodes if node.node_id == node_id), None)

    def observe(self, image_features: np.ndarray, issued_action: str, frame_index: int) -> dict[str, Any]:
        """Online actor-side update: image features and owned action history only."""
        self._advance(issued_action)
        embedding = project_features(self.model, np.asarray(image_features, np.float32).reshape(1, -1))[0]
        assignment = assign_visual_landmark(self.model, embedding)
        base = {"frameIndex": frame_index, "issuedAction": issued_action,
                "relativePosition": [self.longitudinal_index, self.lateral_index], **assignment}
        if assignment["status"] != "MATCHED":
            self.candidate_prototype = None
            self.candidate_count = 0
            event = {**base, "type": assignment["status"]}
            self.events.append(event)
            return event
        prototype = int(assignment["prototype"])
        self.candidate_count = self.candidate_count + 1 if self.candidate_prototype == prototype else 1
        self.candidate_prototype = prototype
        if self.candidate_count < int(self.model["assignment"]["confirmFrames"]):
            event = {**base, "type": "CANDIDATE"}
            self.events.append(event)
            return event
        candidates = [node for node in self.nodes if node.prototype == prototype]
        node = None
        if candidates:
            node = min(candidates, key=lambda item: float(1.0 - item.centroid @ embedding))
            visual_distance = float(1.0 - node.centroid @ embedding)
            if visual_distance > float(self.model["assignment"]["loopClosureThreshold"]):
                node = None
        previous = self._node(self.active_node)
        if node is None:
            node = VisualNode(f"P{prototype}-N{len(candidates)}", prototype, embedding.copy(), frame_index, frame_index,
                              confidence_sum=float(assignment["confidence"]),
                              relative_longitudinal=self.longitudinal_index, relative_lateral=self.lateral_index)
            self.nodes.append(node)
            event_type = "NEW_NODE"
            matched_frame = None
        else:
            gap = frame_index - node.last_frame
            matched_frame = node.last_frame
            event_type = "LOOP_CLOSURE" if self.active_node != node.node_id and gap >= int(self.model["assignment"]["loopGapFrames"]) else "OBSERVED"
            node.visits += int(event_type == "LOOP_CLOSURE")
            node.observation_count += 1
            node.confidence_sum += float(assignment["confidence"])
            node.centroid = node.centroid * .9 + embedding * .1
            node.centroid /= max(float(np.linalg.norm(node.centroid)), 1e-8)
            node.last_frame = frame_index
        if previous is not None and previous.node_id != node.node_id:
            self.edges[(previous.node_id, node.node_id, issued_action.upper())] += 1
            if event_type == "OBSERVED":
                event_type = "TRANSITION"
        self.active_node = node.node_id
        level_distribution = np.asarray(self.model["prototypes"][prototype]["levelDistribution"], np.float64)
        self.level_belief = .55 * self.level_belief + .45 * level_distribution
        self.level_belief /= self.level_belief.sum()
        event = {**base, "type": event_type, "nodeId": node.node_id, "matchedFrameIndex": matched_frame,
                 "candidateAnchor": bool(self.model["prototypes"][prototype]["candidateAnchor"]),
                 "levelBelief": {level: float(self.level_belief[index]) for index, level in enumerate(LEVEL_ORDER)}}
        self.events.append(event)
        return event

    def export(self, episode_id: str) -> dict[str, Any]:
        return {
            "episodeId": episode_id,
            "nodes": [{"nodeId": node.node_id, "prototype": node.prototype, "firstFrame": node.first_frame,
                       "lastFrame": node.last_frame, "visits": node.visits,
                       "observationCount": node.observation_count,
                       "meanConfidence": node.confidence_sum / max(1, node.observation_count),
                       "relativePositionAtCreation": [node.relative_longitudinal, node.relative_lateral]}
                      for node in self.nodes],
            "edges": [{"source": source, "destination": destination, "issuedAction": action, "count": count}
                      for (source, destination, action), count in sorted(self.edges.items())],
            "events": self.events,
            "finalLevelBelief": {level: float(self.level_belief[index]) for index, level in enumerate(LEVEL_ORDER)},
        }
