import unittest

import numpy as np

from relative_spine_e4_analysis import leave_anchor_out_classification, normalized_centroid


class RelativeSpineE4AnalysisTests(unittest.TestCase):
    def test_normalized_centroid_has_unit_norm(self):
        result = normalized_centroid([np.array([1.0, 0.0]), np.array([1.0, 0.0])])
        self.assertAlmostEqual(float(np.linalg.norm(result)), 1.0)

    def test_leave_anchor_out_uses_other_anatomical_anchors(self):
        anchors = ["c1", "c2", "i1", "i2"]
        labels = {"c1": "LEVEL_CENTER", "c2": "LEVEL_CENTER",
                  "i1": "INTERLEVEL_MIDPOINT", "i2": "INTERLEVEL_MIDPOINT"}
        reference = {"c1": np.array([1.0, 0.0]), "c2": np.array([1.0, 0.0]),
                     "i1": np.array([0.0, 1.0]), "i2": np.array([0.0, 1.0])}
        rows = leave_anchor_out_classification(anchors, labels, reference, reference)
        self.assertEqual(sum(item["correct"] for item in rows), 4)


if __name__ == "__main__":
    unittest.main()
