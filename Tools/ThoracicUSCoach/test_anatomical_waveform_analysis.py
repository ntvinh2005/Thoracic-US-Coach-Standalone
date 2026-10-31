from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from anatomical_waveform_analysis import (
    match_phase_rows,
    measure_bright_interface,
    measure_central_shadow,
    periodicity_test,
    reconstruct_actor_coordinate,
    validate_actor_isolation,
    verify_freeze,
)


class AnatomicalWaveformAnalysisTests(unittest.TestCase):
    def test_shadow_width_recovers_synthetic_central_gap(self) -> None:
        values = np.full((256, 256), 0.7, dtype=np.float64)
        values[:, 103:153] = 0.04
        result = measure_central_shadow(values)
        self.assertTrue(result["valid"])
        self.assertAlmostEqual(result["widthMean"], 50 / 256, delta=0.025)

    def test_bright_interface_tracks_synthetic_bilateral_arch(self) -> None:
        values = np.full((256, 256), 0.05, dtype=np.float64)
        for x in range(20, 236):
            y = int(165 - 35 * np.exp(-((x - 128) / 65) ** 2))
            values[max(0, y - 2):min(256, y + 3), x] = 1.0
            values[:y - 2, x] = 0.35
        result = measure_bright_interface(values)
        self.assertTrue(result["valid"])
        self.assertGreater(result["archHeight"], 0.03)

    def test_actor_coordinate_uses_executed_achieved_displacements(self) -> None:
        observations = [
            {"requestedAction": "OBSERVE", "achievedDisplacementMm": 0.0, "signedActorRelativeMm": 0.0},
            {"requestedAction": "CAUDAL", "achievedDisplacementMm": 2.8, "signedActorRelativeMm": 2.8},
            {"requestedAction": "CRANIAL", "achievedDisplacementMm": 3.1, "signedActorRelativeMm": -0.3},
        ]
        result = reconstruct_actor_coordinate(observations)
        self.assertAlmostEqual(result["coordinates"][-1], -0.3)
        self.assertLess(result["maximumAbsoluteErrorMm"], 1e-9)

    def test_phase_matching_uses_coordinate_not_frame_ordinal(self) -> None:
        rows = [
            {"phase": "a", "observation_index": 1, "coordinate_mm": 3.0},
            {"phase": "a", "observation_index": 2, "coordinate_mm": 9.0},
            {"phase": "b", "observation_index": 99, "coordinate_mm": 8.9},
            {"phase": "b", "observation_index": 98, "coordinate_mm": 3.1},
        ]
        matches = match_phase_rows(rows, "a", "b")
        self.assertEqual([pair[1]["observation_index"] for pair in matches], [98, 99])

    def test_periodicity_supports_clean_multifeature_wave(self) -> None:
        x = np.arange(72, dtype=np.float64)
        matrix = np.column_stack((np.sin(2 * np.pi * x / 8), np.cos(2 * np.pi * x / 8),
                                  np.sin(2 * np.pi * x / 8 + 0.5)))
        result = periodicity_test(matrix, 3.0, iterations=120)
        self.assertEqual(result["status"], "SUPPORTED")
        self.assertAlmostEqual(result["peakLagMm"], 24.0, delta=3.1)

    def test_periodicity_rejects_monotonic_nonperiodic_trend(self) -> None:
        x = np.linspace(-1.0, 1.0, 72)
        matrix = np.column_stack((x, x * x, np.tanh(2 * x)))
        result = periodicity_test(matrix, 3.0, iterations=120)
        self.assertEqual(result["status"], "NOT_SUPPORTED")

    def test_actor_isolation_rejects_privileged_key_but_allows_disclosure(self) -> None:
        validate_actor_isolation({"excludedInputs": "world pose and provenance", "observations": []})
        with self.assertRaises(ValueError):
            validate_actor_isolation({"observations": [{"probePosition": [0, 0, 0]}]})

    def test_freeze_detects_changed_blind_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "blind_cycle_candidates.json"
            output.write_text("{}\n", encoding="utf-8")
            digest = hashlib.sha256(output.read_bytes()).hexdigest()
            (root / "blind_results.sha256").write_text(json.dumps({
                "sha256": {output.name: digest}
            }), encoding="utf-8")
            verify_freeze(root)
            output.write_text("{\"changed\": true}\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                verify_freeze(root)


if __name__ == "__main__":
    unittest.main()
