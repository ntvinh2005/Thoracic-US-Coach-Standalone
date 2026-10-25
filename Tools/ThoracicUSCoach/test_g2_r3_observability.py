from __future__ import annotations

import unittest

from run_g2_r3_observability import COMPONENTS, FLOOR, OBSERVABILITY, WEIGHTS, component_audit


class G2R3ObservabilityTests(unittest.TestCase):
    def test_exact_frozen_formula_and_label(self) -> None:
        component_values = {
            "anatomyRelationship": 0.8,
            "pleuraVisibility": 0.1,
            "targetRegion": 0.9,
            "acousticWindow": 0.4,
            "needleGeometry": 0.9,
            "shadowPenalty": 0.0,
            "contactPenalty": 0.0,
        }
        total = sum(WEIGHTS[key] * component_values[key] for key in COMPONENTS)
        record = {
            "family": "test-family",
            "stepIndex": 0,
            "score": {**component_values, "total": total},
            "observedScore": {**component_values, "total": total},
            "acceptable": total >= FLOOR,
            "contactValid": True,
            "workspaceValid": True,
        }
        audit = component_audit([record])
        self.assertEqual(audit["labelScoreMismatch"], 0)
        self.assertLess(audit["reconstructionAbsoluteError"]["max"], 1e-12)
        self.assertEqual(audit["repeatAtCapture"]["binaryLabelFlips"], 0)

    def test_privileged_components_are_not_marked_pixel_observable(self) -> None:
        self.assertEqual(OBSERVABILITY["acousticWindow"], "CLEAN_ULTRASOUND_PIXELS")
        self.assertTrue(OBSERVABILITY["targetRegion"].startswith("PRIVILEGED"))
        self.assertTrue(OBSERVABILITY["needleGeometry"].startswith("PRIVILEGED"))
        self.assertTrue(OBSERVABILITY["contactPenalty"].startswith("PRIVILEGED"))


if __name__ == "__main__":
    unittest.main()
