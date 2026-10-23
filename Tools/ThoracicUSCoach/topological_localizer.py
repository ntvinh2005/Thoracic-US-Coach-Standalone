from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass, field
from typing import Any, Sequence

import numpy as np

from active2d_contract import ACTION_ORDER, LEVEL_ORDER
from visual_landmark_topology import VisualTopologicalMap, assign_visual_landmark, project_features


LEVEL_TO_INDEX = {level: index for index, level in enumerate(LEVEL_ORDER)}
ACTION_KEYS = ("RESET", *ACTION_ORDER)


def normalized_entropy(values: np.ndarray) -> float:
    values = np.maximum(np.asarray(values, np.float64), 1e-12)
    values /= values.sum()
    return float(-np.sum(values * np.log(values)) / np.log(len(values)))


def fit_probabilistic_localizer(frames: Sequence, raw_features: np.ndarray, topology_model: dict[str, Any],
                               smoothing: float) -> dict[str, Any]:
    """Fit supervised associations; the online state never receives these labels."""
    embeddings = project_features(topology_model, raw_features)
    prototype_count = len(topology_model["prototypes"])
    emission_counts = np.full((len(LEVEL_ORDER), prototype_count), smoothing, np.float64)
    transition_counts = {action: np.full((len(LEVEL_ORDER), len(LEVEL_ORDER)), smoothing, np.float64)
                         for action in ACTION_KEYS}
    initial_counts = np.full(len(LEVEL_ORDER), smoothing, np.float64)
    previous_episode = None
    previous_level = None
    assigned = 0
    for frame, embedding in zip(frames, embeddings):
        level = LEVEL_TO_INDEX[frame.level]
        assignment = assign_visual_landmark(topology_model, embedding)
        if assignment["status"] == "MATCHED":
            emission_counts[level, int(assignment["prototype"])] += 1.0
            assigned += 1
        reset = frame.episode_reset or frame.episode_id != previous_episode
        if reset or previous_level is None:
            initial_counts[level] += 1.0
        else:
            action = frame.issued_action.upper()
            action = action if action in transition_counts else "RESET"
            transition_counts[action][previous_level, level] += 1.0
        previous_episode = frame.episode_id
        previous_level = level
    emissions = emission_counts / emission_counts.sum(axis=1, keepdims=True)
    transitions = {action: values / values.sum(axis=1, keepdims=True)
                   for action, values in transition_counts.items()}
    initial = initial_counts / initial_counts.sum()
    return {"levelOrder": list(LEVEL_ORDER), "prototypeEmission": emissions.tolist(),
            "actionTransitions": {key: value.tolist() for key, value in transitions.items()},
            "initialBelief": initial.tolist(), "smoothing": smoothing,
            "trainAssignmentCoverage": assigned / max(1, len(frames))}


@dataclass
class OnlineTopologicalLocalizer:
    model: dict[str, Any]
    topology: VisualTopologicalMap = field(init=False)
    belief: np.ndarray = field(init=False)
    node_beliefs: dict[str, np.ndarray] = field(default_factory=dict)
    observed_nodes: set[str] = field(default_factory=set)
    observed_anchors: set[int] = field(default_factory=set)
    step: int = 0

    def __post_init__(self) -> None:
        self.topology = VisualTopologicalMap(self.model["visualTopology"])
        self.belief = np.asarray(self.model["probabilisticLocalizer"]["initialBelief"], np.float64)

    def observe(self, image_features: np.ndarray, issued_action: str, frame_index: int) -> dict[str, Any]:
        """Inference contract: image representation plus policy-owned action history only."""
        topology_event = self.topology.observe(image_features, issued_action, frame_index)
        action = issued_action.upper()
        transitions = self.model["probabilisticLocalizer"]["actionTransitions"]
        matrix = np.asarray(transitions.get(action, transitions["RESET"]), np.float64)
        transition_mix = float(self.model["inference"]["transitionMix"])
        moved = self.belief @ matrix
        prior = transition_mix * moved + (1.0 - transition_mix) * self.belief
        status = str(topology_event["status"])
        prototype = int(topology_event["prototype"])
        if status == "MATCHED":
            emissions = np.asarray(self.model["probabilisticLocalizer"]["prototypeEmission"], np.float64)
            likelihood = np.maximum(emissions[:, prototype], 1e-8)
            power = float(self.model["inference"]["emissionPower"])
            if bool(self.model["visualTopology"]["prototypes"][prototype]["candidateAnchor"]):
                power *= float(self.model["inference"]["anchorPowerMultiplier"])
                self.observed_anchors.add(prototype)
            posterior = prior * np.power(likelihood, power)
        else:
            posterior = prior
        posterior /= posterior.sum()
        loop_before = None
        loop_after = None
        node_id = topology_event.get("nodeId")
        if node_id:
            self.observed_nodes.add(str(node_id))
            stored = self.node_beliefs.get(str(node_id))
            if topology_event["type"] == "LOOP_CLOSURE" and stored is not None:
                loop_before = float(1.0 - 0.5 * np.abs(posterior - stored).sum())
                fusion = float(self.model["inference"]["loopFusion"])
                posterior = (1.0 - fusion) * posterior + fusion * stored
                posterior /= posterior.sum()
                loop_after = float(1.0 - 0.5 * np.abs(posterior - stored).sum())
            self.node_beliefs[str(node_id)] = posterior.copy() if stored is None else (.7 * stored + .3 * posterior)
            self.node_beliefs[str(node_id)] /= self.node_beliefs[str(node_id)].sum()
        self.belief = posterior
        prediction = int(np.argmax(posterior))
        result = {"frameIndex": frame_index, "issuedAction": issued_action,
                  "predictedLevel": LEVEL_ORDER[prediction], "confidence": float(posterior[prediction]),
                  "uncertainty": normalized_entropy(posterior),
                  "belief": {level: float(posterior[index]) for index, level in enumerate(LEVEL_ORDER)},
                  "topologyEvent": topology_event["type"], "prototype": prototype,
                  "assignmentStatus": status, "nodeId": node_id,
                  "observedNodeCount": len(self.observed_nodes), "observedAnchorCount": len(self.observed_anchors),
                  "loopAgreementBefore": loop_before, "loopAgreementAfter": loop_after}
        self.step += 1
        return result


def handoff_candidate(record: dict[str, Any], gate: dict[str, Any]) -> bool:
    return (record["predictedLevel"] == "T7"
            and record["confidence"] >= float(gate["confidenceThreshold"])
            and record["uncertainty"] <= float(gate["maxUncertainty"])
            and record["observedNodeCount"] >= int(gate["minObservedNodes"])
            and record["observedAnchorCount"] >= int(gate["minObservedAnchors"]))


def replay_episode(model: dict[str, Any], frames: Sequence, raw_features: np.ndarray) -> list[dict[str, Any]]:
    localizer = OnlineTopologicalLocalizer(model)
    return [localizer.observe(features, frame.issued_action, frame.index)
            for frame, features in zip(frames, raw_features)]


def confusion_metrics(actual: np.ndarray, predicted: np.ndarray) -> dict[str, Any]:
    matrix = np.zeros((len(LEVEL_ORDER), len(LEVEL_ORDER)), np.int64)
    for left, right in zip(actual, predicted):
        matrix[int(left), int(right)] += 1
    per_level = {}
    f1s = []
    for index, level in enumerate(LEVEL_ORDER):
        tp = int(matrix[index, index]); support = int(matrix[index].sum()); selected = int(matrix[:, index].sum())
        precision = tp / max(1, selected); recall = tp / max(1, support)
        f1 = 2 * precision * recall / max(1e-12, precision + recall)
        per_level[level] = {"support": support, "precision": precision, "recall": recall, "f1": f1}
        if support:
            f1s.append(f1)
    return {"count": int(len(actual)), "accuracy": float(np.mean(actual == predicted)),
            "macroF1": float(np.mean(f1s)), "confusion": matrix.tolist(), "perLevel": per_level}


def group_indices(frames: Sequence) -> dict[str, list[int]]:
    grouped: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        grouped[frame.episode_id].append(index)
    return grouped
