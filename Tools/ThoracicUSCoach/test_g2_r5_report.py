import unittest

from g2_r5_report import ordered_match


class G2R5ReportTests(unittest.TestCase):
    def test_ordered_match_maximizes_matches(self):
        references = [
            {"level": "T2", "coordinateMm": -10.0},
            {"level": "T3", "coordinateMm": 0.0},
            {"level": "T4", "coordinateMm": 10.0},
        ]
        rows = ordered_match([-9.0, 0.5, 11.0], references, 2.0)
        self.assertEqual([row["matchedLevel"] for row in rows], ["T2", "T3", "T4"])

    def test_ordered_match_preserves_unmatched_extra(self):
        references = [{"level": "T2", "coordinateMm": 0.0}]
        rows = ordered_match([-20.0, 0.2], references, 1.0)
        self.assertIsNone(rows[0]["matchedLevel"])
        self.assertEqual(rows[1]["matchedLevel"], "T2")

    def test_ordered_match_never_weakens_tolerance(self):
        references = [{"level": "T7", "coordinateMm": 0.0}]
        rows = ordered_match([5.0001], references, 5.0)
        self.assertFalse(rows[0]["matched"])


if __name__ == "__main__":
    unittest.main()
