import tempfile
import unittest
from pathlib import Path

import numpy as np

from relative_spine_e2_analysis import (
    LEVELS,
    cosine_distance,
    morphology_descriptor,
    nearest_level_predictions,
)


class RelativeSpineE2AnalysisTests(unittest.TestCase):
    def test_descriptor_is_deterministic_and_normalized(self):
        image = np.arange(256 * 256, dtype=np.float64).reshape(256, 256) / (256 * 256)
        first = morphology_descriptor(image)
        second = morphology_descriptor(image.copy())
        np.testing.assert_array_equal(first, second)
        self.assertAlmostEqual(float(np.linalg.norm(first)), 1.0, places=10)

    def test_repeat_split_nearest_level_recovers_distinct_repeats(self):
        reference = {}
        query = {}
        for index, level in enumerate(LEVELS):
            vector = np.zeros(14)
            vector[index] = 1.0
            reference[level] = vector
            query[level] = vector.copy()
        rows = nearest_level_predictions(reference, query)
        self.assertEqual(sum(item["correct"] for item in rows), 7)

    def test_cosine_distance_rejects_opposite_structure(self):
        self.assertAlmostEqual(cosine_distance(np.array([1.0, 0.0]), np.array([1.0, 0.0])), 0.0)
        self.assertAlmostEqual(cosine_distance(np.array([1.0, 0.0]), np.array([-1.0, 0.0])), 2.0)


if __name__ == "__main__":
    unittest.main()
