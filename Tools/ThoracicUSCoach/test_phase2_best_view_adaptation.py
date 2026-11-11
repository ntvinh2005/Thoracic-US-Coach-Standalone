import tempfile
import unittest
from pathlib import Path

from phase2_best_view_adaptation import _role_from_case, challenge_frames


class Phase2BestViewAdaptationTests(unittest.TestCase):
    def test_role_is_derived_from_explicit_case_split(self):
        self.assertEqual(_role_from_case("case_06_dev_family_0073_f0105"), "dev")
        self.assertEqual(_role_from_case("case_14_test_family_0079_f0098"), "test")

    def test_unknown_case_split_is_rejected(self):
        with self.assertRaises(ValueError):
            _role_from_case("case_99_unknown_family")

    def test_consumed_challenge_contains_both_classes(self):
        frames = challenge_frames()
        self.assertTrue(any(frame["label"] for frame in frames))
        self.assertTrue(any(not frame["label"] for frame in frames))
        self.assertTrue(any("fresh_positive" in frame["name"] for frame in frames))


if __name__ == "__main__":
    unittest.main()
