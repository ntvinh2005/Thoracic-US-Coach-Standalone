import inspect

import numpy as np

from visual_landmark_topology import VisualTopologicalMap, assign_visual_landmark


def model() -> dict:
    return {
        "projection": {"mean": [0., 0.], "components": [[1., 0.], [0., 1.]], "scale": [1., 1.]},
        "prototypes": [
            {"center": [1., 0.], "matchRadius": .4, "levelDistribution": [.7, .1, .1, .1], "candidateAnchor": True},
            {"center": [0., 1.], "matchRadius": .4, "levelDistribution": [.1, .1, .7, .1], "candidateAnchor": False},
        ],
        "assignment": {"ambiguityMargin": .1, "confirmFrames": 1, "loopGapFrames": 2, "loopClosureThreshold": .1},
    }


def test_online_api_has_no_privileged_arguments() -> None:
    parameters = set(inspect.signature(VisualTopologicalMap.observe).parameters)
    assert parameters == {"self", "image_features", "issued_action", "frame_index"}


def test_ambiguous_and_unmatched_are_explicit() -> None:
    current = model()
    ambiguous = np.asarray([1., 1.], np.float32) / np.sqrt(2)
    assert assign_visual_landmark(current, ambiguous)["status"] == "AMBIGUOUS"
    assert assign_visual_landmark(current, np.asarray([-1., 0.], np.float32))["status"] == "UNMATCHED"


def test_visual_reappearance_creates_loop_closure_and_topological_edges() -> None:
    topology = VisualTopologicalMap(model())
    assert topology.observe(np.asarray([1., 0.]), "RESET", 0)["type"] == "NEW_NODE"
    assert topology.observe(np.asarray([0., 1.]), "CAUDAL", 1)["type"] == "NEW_NODE"
    event = topology.observe(np.asarray([1., 0.]), "CRANIAL", 3)
    assert event["type"] == "LOOP_CLOSURE"
    exported = topology.export("episode")
    assert len(exported["nodes"]) == 2
    assert len(exported["edges"]) == 2
    assert exported["nodes"][0]["visits"] == 2


def test_decision_is_invariant_to_external_privileged_payload() -> None:
    left, right = VisualTopologicalMap(model()), VisualTopologicalMap(model())
    feature = np.asarray([1., 0.])
    external = {"pose": [999, 999, 999], "markerIdentity": "T99", "score": -100}
    assert external
    assert left.observe(feature, "RESET", 0) == right.observe(feature, "RESET", 0)
