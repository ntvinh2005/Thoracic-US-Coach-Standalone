from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from autonomous_t7_sequence_analysis import (
    _freeze,
    extract_descriptor_families,
    model_selection,
    optimal_segmentation,
    reversal_validation,
    sequence_gate,
    smooth_trend_null,
    validate_actor_isolation,
    verify_freeze,
)


class AutonomousT7SequenceAnalysisTests(unittest.TestCase):
    def test_exact_segmentation_recovers_persistent_state_boundaries(self) -> None:
        values = np.repeat(np.arange(7, dtype=np.float64), 5)[:, None]
        result = optimal_segmentation(values, 7, minimum_samples=4)
        self.assertEqual(result["boundaries"], [0, 5, 10, 15, 20, 25, 30, 35])
        self.assertAlmostEqual(result["sse"], 0.0)

    def test_model_selection_does_not_hardcode_seven(self) -> None:
        values = np.column_stack((np.linspace(0.0, 1.0, 63), np.linspace(0.0, 1.0, 63) ** 2))
        result = model_selection(values)
        self.assertIn(result["bestBicK"], range(1, 10))
        self.assertEqual(len(result["models"]), 9)

    def test_smooth_nonperiodic_change_is_not_strong_against_matching_null(self) -> None:
        x = np.linspace(-1.0, 1.0, 63)
        values = np.column_stack((x, x * x, np.tanh(2.0 * x)))
        one = optimal_segmentation(values, 1)["sse"]
        seven = optimal_segmentation(values, 7)["sse"]
        result = smooth_trend_null(values, x * 90.0, (one - seven) / one, iterations=90, seed=51)
        self.assertGreater(result["pValue"], 0.01)

    def test_failed_evidence_gate_cannot_promote_k7_partition(self) -> None:
        gate = sequence_gate(
            {"bestBicK": 7},
            {"pValue": 0.20},
            [1.0] * 6,
            {"maximumBoundaryDifferenceMm": 1.0},
            {"minimumWithinToleranceFraction": 1.0},
            {"labelAccuracy": 1.0},
        )
        self.assertEqual(gate["status"], "NO_DEFENSIBLE_SEVEN_LEVEL_SEQUENCE")
        self.assertFalse(gate["allChecksPass"])

    def test_complete_evidence_gate_accepts_supported_sequence(self) -> None:
        gate = sequence_gate(
            {"bestBicK": 7},
            {"pValue": 0.01},
            [1.0] * 6,
            {"maximumBoundaryDifferenceMm": 3.0},
            {"minimumWithinToleranceFraction": 0.90},
            {"labelAccuracy": 0.90},
        )
        self.assertEqual(gate["status"], "SUPPORTED_SEVEN_LEVEL_SEQUENCE")

    def test_reverse_validation_uses_separately_supplied_query_descriptors(self) -> None:
        primary = np.repeat(np.arange(7, dtype=np.float64), 4)[:, None]
        query = primary.copy()
        rows = [{"observation_index": index, "coordinate_mm": float(index)} for index in range(len(primary))]
        query_rows = [{"observation_index": 100 + index, "coordinate_mm": float(index)}
                      for index in range(len(query))]
        result = reversal_validation(primary, query, rows, query_rows, list(range(0, 29, 4)))
        self.assertEqual(result["matchedObservationCount"], 28)
        self.assertEqual(result["labelAccuracy"], 1.0)

    def test_descriptor_families_are_deterministic_and_finite(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "frame.png"
            yy, xx = np.mgrid[:128, :128]
            values = np.uint8(np.clip(40 + 120 * (xx / 127.0) + 60 * (yy > xx), 0, 255))
            Image.fromarray(values, mode="L").save(path)
            first = extract_descriptor_families(path)
            second = extract_descriptor_families(path)
            self.assertEqual(set(first), set(second))
            for name in first:
                self.assertTrue(np.all(np.isfinite(first[name])))
                np.testing.assert_allclose(first[name], second[name])

    def test_actor_evaluator_separation_rejects_privileged_coordinates(self) -> None:
        validate_actor_isolation({"observations": [{"image": "frame.png", "achievedDisplacementMm": 3.0}]})
        with self.assertRaises(ValueError):
            validate_actor_isolation({"observations": [{"diagnosticReferenceLevel": "T7"}]})

    def test_blind_freeze_detects_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            item = root / "blind.json"
            item.write_text("{}\n", encoding="utf-8")
            _freeze(root, [item.name])
            verify_freeze(root)
            item.write_text('{"changed": true}\n', encoding="utf-8")
            with self.assertRaises(ValueError):
                verify_freeze(root)


if __name__ == "__main__":
    unittest.main()
