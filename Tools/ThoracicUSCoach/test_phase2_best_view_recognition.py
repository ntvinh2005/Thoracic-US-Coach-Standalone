from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from phase2_best_view_recognition import (
    FEATURE_MODES,
    extract_all_features,
    evaluate,
    extract_feature,
    safe_threshold,
)


class Phase2BestViewRecognitionTests(unittest.TestCase):
    def test_feature_modes_are_finite_and_deterministic(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image.png"
            values = np.tile(np.arange(64, dtype=np.uint8), (64, 1)) * 4
            Image.fromarray(values).save(path)
            for mode in FEATURE_MODES:
                first = extract_feature(path, mode)
                second = extract_feature(path, mode)
                self.assertGreater(first.size, 100)
                self.assertTrue(np.all(np.isfinite(first)))
                np.testing.assert_array_equal(first, second)
            all_features = extract_all_features(path)
            self.assertEqual(set(all_features), set(FEATURE_MODES))
            for mode in FEATURE_MODES:
                np.testing.assert_array_equal(all_features[mode], extract_feature(path, mode))

    def test_safe_threshold_is_above_every_development_negative(self) -> None:
        labels = np.asarray([0, 0, 1, 1])
        probabilities = np.asarray([0.2, 0.8, 0.7, 0.9])
        threshold = safe_threshold(labels, probabilities)
        self.assertGreater(threshold, 0.8)
        self.assertLessEqual(threshold, 1.0)

    def test_metrics_keep_validity_and_quality_separate(self) -> None:
        labels = np.asarray([0, 1, 1])
        probabilities = np.asarray([0.1, 0.8, 0.9])
        target_scores = np.asarray([0.2, 0.6, 0.7])
        quality = np.asarray([0.2, 0.5, 0.6])
        result = evaluate(labels, probabilities, target_scores, quality, 0.75)
        self.assertEqual(result.false_positive, 0)
        self.assertEqual(result.true_positive, 2)
        self.assertAlmostEqual(result.quality_mae, 0.0666666667)


if __name__ == "__main__":
    unittest.main()
