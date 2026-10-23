import numpy as np

from diagnose_u06_r1_failures import classify_t7_outcome, evidence_precedence, gate_blockers, prediction_delta


def test_t7_outcome_depends_only_on_final_prediction() -> None:
    assert classify_t7_outcome("T7") == "true_positive_T7"
    assert classify_t7_outcome("T6") == "false_negative_T7"


def test_evidence_precedence_is_not_the_outcome() -> None:
    assert evidence_precedence("AMBIGUOUS", "T7", "T7") == "assignment_not_matched"
    assert evidence_precedence("MATCHED", "T6", "T7") == "prototype_only_not_T7"
    assert evidence_precedence("MATCHED", "T7", "T6") == "temporal_changed_away_from_T7"
    assert classify_t7_outcome("T7") == "true_positive_T7"


def test_tp_fn_invariant_reconciles_independently() -> None:
    outcomes = [classify_t7_outcome(x) for x in ["T7"] * 11 + ["T6"] * 58 + ["T8"] * 25]
    assert outcomes.count("true_positive_T7") == 11
    assert outcomes.count("false_negative_T7") == 83
    assert len(outcomes) == 94


def test_gate_blockers_explain_abstention() -> None:
    record = {"predictedLevel": "T6", "confidence": .6, "uncertainty": .8,
              "observedNodeCount": 1, "observedAnchorCount": 0}
    gate = {"confidenceThreshold": .8, "maxUncertainty": .65, "minObservedNodes": 2, "minObservedAnchors": 1}
    assert set(gate_blockers(record, gate)) == {"predicted_not_T7", "confidence_below_threshold",
        "uncertainty_above_limit", "insufficient_nodes", "no_candidate_anchor_observed"}


def test_prediction_delta_reports_loop_fusion_harm_and_help() -> None:
    actual, full, ablated = np.asarray([0, 1, 2, 3]), np.asarray([0, 0, 2, 2]), np.asarray([1, 1, 2, 3])
    assert prediction_delta(actual, full, ablated) == {"fullCorrectAblatedWrong": 1,
        "fullWrongAblatedCorrect": 2, "bothCorrect": 1, "bothWrong": 0}
