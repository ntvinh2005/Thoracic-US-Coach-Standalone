import json
from pathlib import Path

import numpy as np

from active2d_handoff import ImageDerivedHandoffState, LandmarkObservation, handoff_ready, phase_two_prefix


def test_gate_abstains_without_sequence_evidence() -> None:
    state = ImageDerivedHandoffState(estimated_level="T7", localization_confidence=.95, uncertainty=.05)
    assert not handoff_ready(state, min_confidence=.7, max_uncertainty=.4, min_landmark_events=1)
    state.landmark_evidence.append(LandmarkObservation(4, "T6_TO_T7", .8, 4, 0))
    state.promising_search_region = "relative-T7-centered-symmetric-lateral"
    assert handoff_ready(state, min_confidence=.7, max_uncertainty=.4, min_landmark_events=1)


def test_phase_two_behavior_depends_on_legitimate_handoff_uncertainty() -> None:
    confident = ImageDerivedHandoffState(uncertainty=.05)
    confident.landmark_evidence = [LandmarkObservation(2, "T6_TO_T7", .9, 2, 0),
                                   LandmarkObservation(5, "T7_TO_T8", .9, 5, 0)]
    uncertain = ImageDerivedHandoffState(uncertainty=.3)
    uncertain.landmark_evidence = [LandmarkObservation(2, "T6_TO_T7", .9, 2, 0)]
    assert phase_two_prefix(confident, "Caudal") == ()
    assert phase_two_prefix(uncertain, "Caudal") == ("Caudal", "Cranial")


def test_public_state_contains_no_privileged_geometry() -> None:
    summary = ImageDerivedHandoffState().public_summary()
    serialized = json.dumps(summary)
    for forbidden in ("targetCoordinates", "pose", "score", "markerIdentity", "oracleDirection"):
        assert forbidden not in serialized
