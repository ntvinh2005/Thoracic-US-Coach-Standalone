from pathlib import Path
import tempfile
import unittest

import numpy as np
from PIL import Image

from sweep_count_analysis import (
    detect_candidate_events,
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

    def test_ordered_match_does_not_reorder_or_duplicate_events(self) -> None:
        matches = ordered_match([1.0, 21.0, 39.0], [0.0, 20.0, 40.0], tolerance_mm=5.0)

        self.assertEqual([item["eventIndex"] for item in matches], [0, 1, 2])
        self.assertEqual([item["referenceIndex"] for item in matches], [0, 1, 2])
        self.assertEqual([item["absoluteErrorMm"] for item in matches], [1.0, 1.0, 1.0])


if __name__ == "__main__":
    unittest.main()
