from __future__ import annotations

import unittest

from generate_p3_production_fault_artifact import build_production_fault_artifact


class Prompt3ProductionFeedbackTests(unittest.TestCase):
    def test_p3_accepted_incomplete_live_feedback_fail_closed(self) -> None:
        artifact = build_production_fault_artifact("b" * 40)
        trial = artifact["trial"]
        self.assertEqual(artifact["result"], "PASS")
        self.assertEqual(trial["actualDelta"], {"lateralMm": 0.0, "longitudinalMm": 0.0, "yawDeg": 0.0})
        self.assertEqual(trial["odometryDeltaApplied"], trial["actualDelta"])
        self.assertEqual(trial["preVisitedCellIds"], trial["postVisitedCellIds"])
        self.assertNotIn(trial["plannedFullMoveTargetCellId"], trial["postVisitedCellIds"])
        self.assertEqual(trial["preBestMemory"], trial["postBestMemory"])
        self.assertEqual(trial["controllerDecision"], "ABSTAIN")
        self.assertFalse(trial["stopIssued"])
        self.assertTrue(trial["causalActionHistory"][-1]["accepted"])
        self.assertFalse(trial["causalActionHistory"][-1]["completed"])
        self.assertTrue(trial["causalActionHistory"][-1]["feedbackObservedBeforeAppend"])

    def test_full_source_commit_is_required(self) -> None:
        with self.assertRaisesRegex(ValueError, "40-character"):
            build_production_fault_artifact("short")


if __name__ == "__main__":
    unittest.main()
