from pathlib import Path
import tempfile
import unittest

import numpy as np
from PIL import Image

from sweep_count_analysis import (
    detect_candidate_events,
    detect_candidate_events_with_boundaries,
    detect_candidate_valleys_with_boundaries,
    evaluator_coordinates_for_event_indices,
    extract_frame_features,
    ordered_match,
)


class SweepCountAnalysisTests(unittest.TestCase):
    def test_bright_interface_with_dark_deep_region_has_shadow_signal(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            values = np.full((256, 256), 80, dtype=np.uint8)
            values[95:100, 24:232] = 245
            values[100:, 24:232] = 2
            path = Path(directory) / "bone_shadow.png"
            Image.fromarray(values, mode="L").save(path)

            feature = extract_frame_features(path, 12.0)

        self.assertGreater(feature.bone_interface, 0.4)
        self.assertGreater(feature.shadow_fraction, 0.8)
        self.assertEqual(feature.travel_mm, 12.0)

    def test_event_detector_does_not_force_seven_events(self) -> None:
        travel = np.arange(0.0, 33.0, 3.0)
        signal = np.asarray([0.0, 0.2, 2.5, 0.1, 0.0, 0.2, 0.1, 2.8, 0.1, 0.0, 0.0])

        events = detect_candidate_events(travel, signal)

        self.assertEqual(events, [2, 7])

    def test_boundary_peaks_and_valleys_are_detected_without_forcing_count(self) -> None:
        travel = np.arange(0.0, 15.0, 3.0)

        peaks = detect_candidate_events_with_boundaries(
            travel, np.asarray([3.0, 0.0, 0.0, 0.0, 4.0])
        )
        valleys = detect_candidate_valleys_with_boundaries(
            travel, np.asarray([-3.0, 0.0, 0.0, 0.0, -4.0])
        )

        self.assertEqual(peaks, [0, 4])
        self.assertEqual(valleys, [0, 4])

    def test_boundary_candidate_below_unchanged_threshold_is_rejected(self) -> None:
        travel = np.arange(0.0, 15.0, 3.0)
        signal = np.asarray([1.0, 0.0, 10.0, 0.0, 1.0])

        events = detect_candidate_events_with_boundaries(travel, signal)

        self.assertEqual(events, [2])

    def test_ordered_match_does_not_reorder_or_duplicate_events(self) -> None:
        matches = ordered_match([1.0, 21.0, 39.0], [0.0, 20.0, 40.0], tolerance_mm=5.0)

        self.assertEqual([item["eventIndex"] for item in matches], [0, 1, 2])
        self.assertEqual([item["referenceIndex"] for item in matches], [0, 1, 2])
        self.assertEqual([item["absoluteErrorMm"] for item in matches], [1.0, 1.0, 1.0])

    def test_event_indices_map_to_corresponding_actual_evaluator_frames(self) -> None:
        observations = [
            {"observationIndex": 0, "corridorCoordinateMm": 0.0},
            {"observationIndex": 1, "corridorCoordinateMm": 2.8},
            {"observationIndex": 2, "corridorCoordinateMm": 5.7},
        ]

        coordinates = evaluator_coordinates_for_event_indices([0, 2], observations)

        self.assertEqual(coordinates, [0.0, 5.7])

    def test_real_artifact_preserves_baseline_and_rejects_endpoints_when_available(self) -> None:
        root = Path(__file__).resolve().parents[2]
        signal_path = root / "Artifacts" / "ThoracicUSCoach" / "sweep_count" \
            / "SWEEP_COUNT_M1_20261030_152908_164" / "signal.csv"
        if not signal_path.exists():
            self.skipTest("Real Milestone 1 artifact is not available")
        values = np.genfromtxt(signal_path, delimiter=",", names=True)
        travel = np.asarray(values["travel_mm"], dtype=np.float64)
        combined = np.asarray(values["combined"], dtype=np.float64)

        baseline = detect_candidate_events(travel, combined)
        revised = detect_candidate_events_with_boundaries(travel, combined)

        self.assertEqual(baseline, [29, 38, 49])
        self.assertEqual(revised, baseline)


if __name__ == "__main__":
    unittest.main()
