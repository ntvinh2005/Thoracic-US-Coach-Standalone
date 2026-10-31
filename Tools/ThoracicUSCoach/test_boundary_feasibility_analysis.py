from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from boundary_feasibility_analysis import (
    PERSISTENCE_FRAMES,
    REVERSAL_TOLERANCE_MM,
    assess_end,
    extract_image_features,
    mark_persistence,
    validate_actor_isolation,
    verify_blind_freeze,
)


class BoundaryFeasibilityAnalysisTests(unittest.TestCase):
    def test_actor_isolation_allows_explicit_exclusion_disclosure(self) -> None:
        validate_actor_isolation({
            "excludedInputs": "World pose, anatomy bounds, mesh identities, and provenance are excluded.",
            "observations": [{"signedActorRelativeMm": 3.0}],
        })

    def test_actor_isolation_rejects_privileged_field_key(self) -> None:
        with self.assertRaises(ValueError):
            validate_actor_isolation({"observations": [{"probePosition": [0.0, 0.0, 0.0]}]})

    def test_dark_image_is_valid_finite_feature_input(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "dark.png"
            Image.fromarray(np.zeros((256, 256), dtype=np.uint8), mode="L").save(path)
            features = extract_image_features(path)
        self.assertTrue(all(np.isfinite(value) for value in features.values()))
        self.assertEqual(features["gray_std"], 0.0)

    def test_single_low_frame_is_not_persistent(self) -> None:
        rows = [
            {"phase": "out", "classification": "STRUCTURED"} for _ in range(8)
        ]
        rows[4]["classification"] = "LOW_CONTENT"
        mark_persistence(rows)
        self.assertEqual(rows[4]["classification"], "LOW_CONTENT")

    def test_five_consecutive_low_frames_are_persistent(self) -> None:
        rows = [
            {"phase": "out", "classification": "LOW_CONTENT"}
            for _ in range(PERSISTENCE_FRAMES)
        ]
        mark_persistence(rows)
        self.assertTrue(all(row["classification"] == "PERSISTENT_LOW_CONTENT" for row in rows))

    def test_reproduced_terminal_transition_passes_within_tolerance(self) -> None:
        rows = []
        for index in range(8):
            rows.append({
                "phase": "out", "classification": "STRUCTURED" if index < 3 else "PERSISTENT_LOW_CONTENT",
                "signed_actor_relative_mm": index * 3.0,
            })
        for index in range(8):
            rows.append({
                "phase": "back", "classification": "PERSISTENT_LOW_CONTENT" if index < 5 else "STRUCTURED",
                "signed_actor_relative_mm": 21.0 - index * 3.0,
            })
        result = assess_end(rows, "out", "back", True)
        self.assertEqual(result["status"], "PASS")
        self.assertLessEqual(result["transitionDifferenceMm"], REVERSAL_TOLERANCE_MM)

    def test_adequately_sampled_absent_boundary_fails(self) -> None:
        rows = []
        for phase in ("out", "back"):
            for index in range(8):
                rows.append({
                    "phase": phase,
                    "classification": "STRUCTURED",
                    "signed_actor_relative_mm": index * 3.0,
                })
        result = assess_end(rows, "out", "back", True)
        self.assertEqual(result["status"], "FAIL")

    def test_blind_freeze_detects_modified_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "blind_boundary_analysis.json"
            output.write_text("{}\n", encoding="utf-8")
            import hashlib
            digest = hashlib.sha256(output.read_bytes()).hexdigest()
            (root / "blind_analysis.sha256").write_text(json.dumps({
                "sha256": {output.name: digest}
            }), encoding="utf-8")
            verify_blind_freeze(root)
            output.write_text("{\"changed\": true}\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                verify_blind_freeze(root)


if __name__ == "__main__":
    unittest.main()
