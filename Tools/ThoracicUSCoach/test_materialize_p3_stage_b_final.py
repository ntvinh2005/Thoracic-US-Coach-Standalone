from __future__ import annotations

import itertools
import unittest

from materialize_p3_stage_b_final import _pair_grid


class StageBMaterializerTests(unittest.TestCase):
    def test_zero_refinement_slot_is_replaced_by_selected_base_center_in_topology(self) -> None:
        candidates = []
        for index, offset in enumerate(itertools.product((-1.0, 0.0, 1.0), repeat=3)):
            zero = offset == (0.0, 0.0, 0.0)
            candidates.append({
                "candidateId": f"ref_{index:02d}",
                "longitudinalOffsetMm": offset[0],
                "lateralOffsetMm": offset[1],
                "yawDeg": offset[2],
                "role": "selected_center_repeat" if zero else "refinement_cell",
            })
        pairs = _pair_grid(candidates, "base_center")
        zero_id = next(item["candidateId"] for item in candidates if item["role"] == "selected_center_repeat")
        self.assertEqual(len(pairs), 54)
        self.assertFalse(any(zero_id in pair for pair in pairs))
        self.assertEqual(sum("base_center" in pair for pair in pairs), 6)
        linked = {value for pair in pairs for value in pair if value.startswith("ref_")}
        self.assertEqual(len(linked), 26)


if __name__ == "__main__":
    unittest.main()
