from autonomous_t2t8_patch_likelihood import (
    FROZEN_ACCEPTANCE_SCORE,
    apply_candidate_verifier,
    candidate_events,
)


def _signal(forward_count: int = 4, reverse_count: int = 4) -> dict:
    scores = [0.10, 0.20, 0.691, 0.20, 0.10]
    return {
        "gridStepMm": 1.0,
        "records": [
            {
                "coordinateMm": float(index - 2),
                "scoreQ75": score,
                "forwardCount": forward_count if index == 2 else 0,
                "reverseCount": reverse_count if index == 2 else 0,
                "topSupportingObservations": [],
            }
            for index, score in enumerate(scores)
        ],
    }


def test_frozen_threshold_rejects_bidirectionally_supported_below_threshold_event() -> None:
    events = candidate_events(_signal(), FROZEN_ACCEPTANCE_SCORE)

    assert FROZEN_ACCEPTANCE_SCORE == 0.70
    assert len(events) == 1
    assert events[0]["accepted"] is False
    assert "CLASSIFIER_SCORE_BELOW_FROZEN_MINIMUM" in events[0]["rejectionReasons"]


def test_frozen_threshold_does_not_override_directional_support() -> None:
    events = candidate_events(_signal(reverse_count=1), FROZEN_ACCEPTANCE_SCORE)

    assert len(events) == 1
    assert events[0]["accepted"] is False
    assert "INSUFFICIENT_DIRECTIONAL_SUPPORT" in events[0]["rejectionReasons"]


def _verifier_signal(score: float = 0.95, reverse_count: int = 4) -> dict:
    return {
        "records": [
            {
                "coordinateMm": 0.5,
                "scoreQ75": score,
                "forwardCount": 4,
                "reverseCount": reverse_count,
            }
        ]
    }


def test_verifier_accepts_supported_candidate_without_shifting_primary_coordinate() -> None:
    events = candidate_events(_signal(), FROZEN_ACCEPTANCE_SCORE)

    verified = apply_candidate_verifier(events, _verifier_signal())

    assert verified[0]["accepted"] is True
    assert verified[0]["verifierAccepted"] is True
    assert verified[0]["coordinateMm"] == 0.0
    assert verified[0]["verifierCoordinateMm"] == 0.5


def test_verifier_does_not_rescue_weak_or_one_direction_evidence() -> None:
    weak = apply_candidate_verifier(
        candidate_events(_signal(), FROZEN_ACCEPTANCE_SCORE),
        _verifier_signal(score=0.89),
    )
    one_direction = apply_candidate_verifier(
        candidate_events(_signal(), FROZEN_ACCEPTANCE_SCORE),
        _verifier_signal(reverse_count=1),
    )

    assert weak[0]["accepted"] is False
    assert one_direction[0]["accepted"] is False
