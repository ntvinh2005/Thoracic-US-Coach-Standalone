import inspect

import numpy as np

from topological_localizer import OnlineTopologicalLocalizer, handoff_candidate


def model() -> dict:
    topology = {
        "projection": {"mean": [0., 0.], "components": [[1., 0.], [0., 1.]], "scale": [1., 1.]},
        "prototypes": [
            {"center": [1., 0.], "matchRadius": .4, "levelDistribution": [.1, .7, .1, .1], "candidateAnchor": True},
            {"center": [0., 1.], "matchRadius": .4, "levelDistribution": [.1, .1, .7, .1], "candidateAnchor": False},
        ],
        "assignment": {"ambiguityMargin": .1, "confirmFrames": 1, "loopGapFrames": 2, "loopClosureThreshold": .1},
    }
    identity = np.eye(4).tolist()
    return {"visualTopology": topology,
            "probabilisticLocalizer": {"initialBelief": [.25] * 4,
                                       "prototypeEmission": [[.05, .05], [.85, .05], [.05, .85], [.05, .05]],
                                       "actionTransitions": {name: identity for name in
                                       ("RESET", "CRANIAL", "CAUDAL", "MEDIAL", "LATERAL", "ROTATECLOCKWISE",
                                        "ROTATECOUNTERCLOCKWISE", "PHASEHANDOFF", "STOP")}},
            "inference": {"transitionMix": .5, "emissionPower": 1., "loopFusion": .5, "anchorPowerMultiplier": 1.5}}


def test_online_api_has_only_sensor_valid_inputs() -> None:
    parameters = set(inspect.signature(OnlineTopologicalLocalizer.observe).parameters)
    assert parameters == {"self", "image_features", "issued_action", "frame_index"}


def test_anchor_and_visual_evidence_update_belief() -> None:
    localizer = OnlineTopologicalLocalizer(model())
    record = localizer.observe(np.asarray([1., 0.]), "RESET", 0)
    assert record["predictedLevel"] == "T7"
    assert record["observedAnchorCount"] == 1
    assert record["confidence"] > .5


def test_handoff_abstains_without_map_evidence() -> None:
    gate = {"confidenceThreshold": .7, "maxUncertainty": .5, "minObservedNodes": 2, "minObservedAnchors": 1}
    record = {"predictedLevel": "T7", "confidence": .95, "uncertainty": .1,
              "observedNodeCount": 1, "observedAnchorCount": 1}
    assert not handoff_candidate(record, gate)
    record["observedNodeCount"] = 2
    assert handoff_candidate(record, gate)


def test_loop_closure_fuses_stored_belief_without_ground_truth() -> None:
    localizer = OnlineTopologicalLocalizer(model())
    localizer.observe(np.asarray([1., 0.]), "RESET", 0)
    localizer.observe(np.asarray([0., 1.]), "CAUDAL", 1)
    record = localizer.observe(np.asarray([1., 0.]), "CRANIAL", 3)
    assert record["topologyEvent"] == "LOOP_CLOSURE"
    assert record["loopAgreementAfter"] >= record["loopAgreementBefore"]
