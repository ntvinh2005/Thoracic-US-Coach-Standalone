import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import numpy as np

from g2_r6_1_lineage import write_csv


class G2R61LineageTests(unittest.TestCase):
    def test_csv_preserves_lineage_fields_from_different_failure_stages(self):
        rows = [
            {"observationIndex": 0, "valleyFound": False,
             "discardReason": "NO_INTERFACE_VALLEY"},
            {"observationIndex": 1, "valleyFound": True,
             "discardReason": "OUTSIDE_TARGET_RADIUS", "targetDistanceMm": 4.1},
        ]
        with TemporaryDirectory() as directory:
            output = Path(directory) / "lineage.csv"
            write_csv(output, rows)
            text = output.read_text(encoding="utf-8")
        self.assertIn("targetDistanceMm", text)
        self.assertIn("NO_INTERFACE_VALLEY", text)
        self.assertIn("OUTSIDE_TARGET_RADIUS", text)

    def test_lineage_module_has_no_evaluator_dependency(self):
        source = Path(__file__).with_name("g2_r6_1_lineage.py").read_text(encoding="utf-8")
        self.assertNotIn("trajectory_privileged", source)
        self.assertNotIn("anatomical_correspondence", source)
        self.assertNotIn("LF T", source)
        self.assertFalse(np.isnan(4.0))


if __name__ == "__main__":
    unittest.main()
