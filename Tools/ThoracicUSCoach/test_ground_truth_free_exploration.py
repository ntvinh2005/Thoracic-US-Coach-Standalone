import inspect
import unittest

from ground_truth_free_exploration import (
    assess_coverage,
    build_prefix_actor,
    causal_round_trip_checkpoints,
    exploration_actor_after_home,
    run_causal_replay,
)


def observation(index, coordinate):
    return {
        "observationIndex": index,
        "signedActorRelativeMm": coordinate,
    }


def candidate(coordinate, accepted=True):
    return {
        "coordinateMm": coordinate,
        "accepted": accepted,
        "supportIntervalMm": [coordinate - 1.0, coordinate + 1.0],
        "forwardCount": 3,
        "reverseCount": 3,
    }


class GroundTruthFreeExplorationTests(unittest.TestCase):
    def test_round_trip_checkpoints_are_causal(self):
        coordinates = [0, 5, 11, 5, 0, -5, -11, -5, 0]
        checkpoints = causal_round_trip_checkpoints([
            observation(index, value) for index, value in enumerate(coordinates)])
        self.assertEqual([item.observation_count for item in checkpoints], [5, 9])

    def test_prefix_never_contains_future_observations(self):
        actor = {"observations": [observation(index, index) for index in range(5)]}
        prefix = build_prefix_actor(actor, 3)
        self.assertEqual(len(prefix["observations"]), 3)
        self.assertEqual(len(actor["observations"]), 5)

    def test_random_start_return_is_excluded_from_landmark_inference(self):
        actor = {
            "randomStartEnabled": True,
            "initialHomeReturnCompleted": True,
            "initialActorRelativeMm": 12.0,
            "observations": [
                {**observation(0, 12.0), "phase": "random_start_baseline"},
                {**observation(1, 6.0), "phase": "initial_home_return"},
                {**observation(2, 0.0), "phase": "home_arrival_baseline"},
                {**observation(3, 1.5), "phase": "caudal_outbound"},
            ],
        }
        exploration = exploration_actor_after_home(actor)
        self.assertEqual(len(exploration["observations"]), 2)
        self.assertEqual(exploration["observations"][0]["observationIndex"], 0)
        self.assertEqual(exploration["observations"][0]["phase"], "home_arrival_baseline")
        self.assertFalse(exploration["randomStartEnabled"])
        self.assertEqual(exploration["initialActorRelativeMm"], 0.0)

    def test_coverage_requires_edge_evidence_not_just_seven_events(self):
        actor = {"observations": [observation(0, -61), observation(1, 61)]}
        candidates = [candidate(value) for value in (-60, -40, -20, 0, 20, 40, 60)]
        result = assess_coverage(actor, candidates, edge_margin_mm=8.0)
        self.assertEqual(result["recommendedAction"], "EXPAND_EDGE_COVERAGE")
        self.assertFalse(result["edgeEvidenceClosed"])

    def test_coverage_stops_only_with_supported_clear_margins(self):
        actor = {"observations": [observation(0, -80), observation(1, 80)]}
        candidates = [candidate(value) for value in (-60, -40, -20, 0, 20, 40, 60)]
        result = assess_coverage(actor, candidates, edge_margin_mm=8.0)
        self.assertEqual(result["recommendedAction"], "STOP_COVERAGE_SUFFICIENT")

    def test_policy_rejects_privileged_fields(self):
        actor = {
            "anatomicalCoordinate": 3.0,
            "observations": [observation(0, -80), observation(1, 80)],
        }
        with self.assertRaises(ValueError):
            assess_coverage(actor, [])

    def test_replay_source_does_not_load_evaluator_references(self):
        source = inspect.getsource(run_causal_replay).lower()
        self.assertNotIn("reference", source)
        self.assertNotIn("evaluator", source)
        self.assertNotIn("lf t2", source)
        self.assertNotIn("lf t8", source)


if __name__ == "__main__":
    unittest.main()
