from dataclasses import dataclass

import numpy as np

from landmark_mapping import (EVENT_TO_INDEX, apply_event_threshold, context_features,
                              derive_landmark_event, event_labels, sequence_diagnostics)


@dataclass
class Frame:
    episode_id: str
    index: int
    issued_action: str
    episode_reset: bool
    level: str


def test_event_derivation_and_episode_boundary() -> None:
    assert derive_landmark_event("T6", "T7") == "T6_TO_T7"
    assert derive_landmark_event("T7", "T6") == "T7_TO_T6"
    assert derive_landmark_event("T6", "T8") == "UNMATCHED"
    frames = [Frame("a", 0, "RESET", True, "T6"), Frame("a", 1, "CAUDAL", False, "T7"),
              Frame("b", 0, "RESET", True, "T8")]
    assert event_labels(frames).tolist() == [0, EVENT_TO_INDEX["T6_TO_T7"], 0]


def test_context_never_crosses_episode_boundary() -> None:
    frames = [Frame("a", 0, "RESET", True, "T6"), Frame("a", 1, "CAUDAL", False, "T7"),
              Frame("b", 0, "RESET", True, "T8")]
    features = np.vstack((np.zeros(256), np.ones(256), np.full(256, 5))).astype(np.float32)
    context = context_features(frames, features)
    assert context.shape[1] > 256
    assert np.allclose(context[0, 256:320], 0)
    assert not np.allclose(context[1, 256:320], 0)
    assert np.allclose(context[2, 256:320], 0)


def test_threshold_and_sequence_diagnostics() -> None:
    probabilities = np.zeros((3, 10), np.float32)
    probabilities[:, 0] = .8
    probabilities[1, EVENT_TO_INDEX["T6_TO_T7"]] = .7
    predicted = apply_event_threshold(probabilities, .5)
    assert predicted.tolist() == [0, EVENT_TO_INDEX["T6_TO_T7"], 0]
    frames = [Frame("a", 0, "RESET", True, "T6"), Frame("a", 1, "CAUDAL", False, "T7"),
              Frame("a", 2, "CAUDAL", False, "T8")]
    events = np.asarray([0, EVENT_TO_INDEX["T6_TO_T7"], EVENT_TO_INDEX["T7_TO_T8"]])
    diagnostics = sequence_diagnostics(frames, events, np.asarray([0, 1, 2]))
    assert diagnostics["orderingConsistency"] == 1.0
