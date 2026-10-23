import numpy as np

from diagnose_u06_r1_failures import classify_t7_failure, gate_blockers, prediction_delta


def test_t7_failure_attribution_is_mutually_exclusive() -> None:
    assert classify_t7_failure("AMBIGUOUS", "T7", "T7") == "assignment_unavailable"
    assert classify_t7_failure("MATCHED", "T6", "T6") == "prototype_not_T7"
    assert classify_t7_failure("MATCHED", "T7", "T6") == "temporal_suppression"
    assert classify_t7_failure("MATCHED", "T7", "T7") == "full_correct"


def test_gate_blockers_explain_abstention() -> None:
    record = {"predictedLevel": "T6", "confidence": .6, "uncertainty": .8,
              "observedNodeCount": 1, "observedAnchorCount": 0}
    gate = {"confidenceThreshold": .8, "maxUncertainty": .65, "minObservedNodes": 2, "minObservedAnchors": 1}
    assert set(gate_blockers(record, gate)) == {"predicted_not_T7", "confidence_below_threshold",
                                                "uncertainty_above_limit", "insufficient_nodes",
                                                "no_candidate_anchor_observed"}


def test_prediction_delta_reports_loop_fusion_harm_and_help() -> None:
    actual = np.asarray([0, 1, 2, 3]); full = np.asarray([0, 0, 2, 2]); ablated = np.asarray([1, 1, 2, 3])
    result = prediction_delta(actual, full, ablated)
    assert result == {"fullCorrectAblatedWrong": 1, "fullWrongAblatedCorrect": 2,
                      "bothCorrect": 1, "bothWrong": 0}
