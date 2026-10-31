from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from mini_zigzag_analysis import (
    FEATURE_NAMES,
    FORBIDDEN_FEATURE_FIELDS,
    OFFSETS_MM,
    _classify_held_position,
    _feature_record,
    _feature_vector,
    classify_provenance,
    extract_image_features,
    select_unique_profile_observations,
)
from mini_zigzag_combined_analysis import (
    cross_yaw_profile_correlations,
    leave_one_yaw_out,
    within_vs_adjacent_separation,
)


class MiniZigzagAnalysisTests(unittest.TestCase):
    def test_feature_vector_rejects_privileged_or_schedule_fields(self) -> None:
        valid = {name: float(index) for index, name in enumerate(FEATURE_NAMES)}
        vector = _feature_vector(valid)
        self.assertEqual(vector.shape, (len(FEATURE_NAMES),))
        for forbidden in FORBIDDEN_FEATURE_FIELDS:
            invalid = dict(valid)
            invalid[forbidden] = 1
            with self.assertRaises(ValueError):
                _feature_vector(invalid)

    def test_profile_selection_uses_exact_five_unique_offsets_and_first_occurrence(self) -> None:
        observations = []
        for index, offset in enumerate((0.0, -3.0, -6.0, -3.0, 0.0, 3.0, 6.0, 3.0, 0.0)):
            observations.append(
                {
                    "observationIndex": index,
                    "profileToken": "P00",
                    "relativeWithinProfileOffsetMm": offset,
                }
            )
        selected = select_unique_profile_observations(observations)
        self.assertEqual(set(selected["P00"]), set(OFFSETS_MM))
        self.assertEqual(selected["P00"][-3.0]["observationIndex"], 1)
        self.assertEqual(selected["P00"][0.0]["observationIndex"], 0)
        self.assertEqual(selected["P00"][3.0]["observationIndex"], 5)

    def test_equal_budget_classifier_reports_all_seven_by_five_trials(self) -> None:
        signatures = np.zeros((7, 5, 3), dtype=np.float64)
        for class_index in range(7):
            signatures[class_index, :, 0] = class_index * 10.0
            signatures[class_index, :, 1] = np.arange(5)
        result = _classify_held_position(signatures)
        self.assertEqual(result["total"], 35)
        self.assertEqual(result["correct"], 35)
        self.assertEqual(len(result["predictions"]), 35)

    def test_combined_skeleton_is_explicitly_unresolved(self) -> None:
        result = classify_provenance(
            {
                "beams": [
                    {
                        "hasVisibleInterface": True,
                        "firstVisibleObject": "Skin",
                        "hasShadowInterface": True,
                        "firstShadowObject": "Skeleton",
                    },
                    {
                        "hasVisibleInterface": True,
                        "firstVisibleObject": "LF T5",
                        "hasShadowInterface": False,
                    },
                ]
            }
        )
        self.assertEqual(result["combinedSkeletonUnresolvedBeamCount"], 1)
        self.assertEqual(result["anatomicalIdentity"], "COMBINED_SKELETON_UNRESOLVED")

    def test_image_features_are_pixel_only_finite_and_frozen_schema(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "synthetic.png"
            pixels = np.zeros((256, 256), dtype=np.uint8)
            pixels[70:74, 30:220] = 255
            pixels[75:210, 90:165] = 8
            Image.fromarray(pixels, mode="L").convert("RGB").save(path)
            features = extract_image_features(path)
        record = _feature_record(features)
        self.assertEqual(tuple(record), FEATURE_NAMES)
        self.assertTrue(np.all(np.isfinite(np.asarray(features.values))))
        self.assertTrue(FORBIDDEN_FEATURE_FIELDS.isdisjoint(record))

    def test_leave_one_yaw_out_uses_twenty_one_class_predictions(self) -> None:
        signatures = np.zeros((3, 7, 5, 2), dtype=np.float64)
        for yaw in range(3):
            for class_index in range(7):
                signatures[yaw, class_index, :, 0] = class_index * 10.0
                signatures[yaw, class_index, :, 1] = np.arange(5) + yaw * 0.01
        result = leave_one_yaw_out(signatures)
        self.assertEqual(result["total"], 21)
        self.assertEqual(result["correct"], 21)

    def test_cross_yaw_correlation_applies_predeclared_point_eight_threshold(self) -> None:
        base = np.arange(70, dtype=np.float64).reshape(7, 5, 2)
        signatures = np.stack([base, base + 0.1, base + 0.2])
        rows = cross_yaw_profile_correlations(signatures)
        self.assertEqual(len(rows), 7)
        self.assertTrue(all(row["passesThreshold"] for row in rows))
        self.assertTrue(all(row["minimumPairwiseCorrelation"] >= 0.80 for row in rows))

    def test_within_level_distance_must_be_strictly_below_adjacent_distance(self) -> None:
        signatures = np.zeros((3, 7, 5, 2), dtype=np.float64)
        for yaw in range(3):
            for class_index in range(7):
                signatures[yaw, class_index, :, :] = class_index * 100.0 + yaw * 0.01
        rows = within_vs_adjacent_separation(signatures)
        self.assertEqual(len(rows), 7)
        self.assertTrue(all(row["passes"] for row in rows))


if __name__ == "__main__":
    unittest.main()
