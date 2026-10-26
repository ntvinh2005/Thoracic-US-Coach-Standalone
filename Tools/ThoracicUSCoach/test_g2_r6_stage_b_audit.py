from __future__ import annotations

import unittest

from g2_r6_stage_b_audit import candidate_grid_components, connected_components


class R6StageBAuditTests(unittest.TestCase):
    def test_components_are_transitive(self) -> None:
        self.assertEqual(
            connected_components(["a", "b", "c", "d"], [("a", "b"), ("b", "c")]),
            [["a", "b", "c"], ["d"]],
        )

    def test_candidate_components_exclude_off_t7_positive(self) -> None:
        manifest = {"mapCandidates": [
            {"candidateId": "a", "anchorControlId": "control", "stage": "fine",
             "longitudinalOffsetMm": -1, "lateralOffsetMm": 0, "yawDeg": 0},
            {"candidateId": "b", "anchorControlId": "control", "stage": "fine",
             "longitudinalOffsetMm": 1, "lateralOffsetMm": 0, "yawDeg": 0},
            {"candidateId": "c", "anchorControlId": "control", "stage": "fine",
             "longitudinalOffsetMm": 1, "lateralOffsetMm": 2, "yawDeg": 0},
        ]}
        base = {
            "sourceKind": "basin_candidate", "acceptable": True,
            "contactValid": True, "workspaceValid": True,
        }
        records = [
            {**base, "sourceId": "a", "t7Valid": True},
            {**base, "sourceId": "b", "t7Valid": True},
            {**base, "sourceId": "c", "t7Valid": False},
        ]
        self.assertEqual(candidate_grid_components(records, manifest), [["a", "b"]])


if __name__ == "__main__":
    unittest.main()
