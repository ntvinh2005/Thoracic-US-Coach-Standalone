from __future__ import annotations

import unittest

from g2_p3_stage_b_failure_audit import summarize_base


class StageBFailureAuditTests(unittest.TestCase):
    def test_missing_anchor_refinement_center_is_fail_closed(self) -> None:
        manifest = {
            "mapCandidates": [
                {"candidateId": "a", "anchorControlId": "c0", "stage": "coarse"},
                {"candidateId": "b", "anchorControlId": "c1", "stage": "coarse"},
            ],
            "neighborPairs": [],
        }
        passing = {
            "candidateId": "b", "acceptedFreshCaptures": 2, "freshnessPass": True,
            "repeatStable": True, "allAcceptable": True, "allFinalTargetAcceptable": True,
            "allT7Valid": True, "allContactValid": True, "allWorkspaceValid": True,
            "minimumScore": 0.7, "scoreRange": 0.0,
            "maximumRms128": 0.0, "maximumStructuralDistance": 0.0,
        }
        failing = dict(passing, candidateId="a", allAcceptable=False)
        adapted = {
            "frozenThresholds": {
                "minimumFreshCapturesPerCandidate": 2,
                "acceptableScoreFloor": 0.5912307500839233,
                "maximumWithinPoseScoreRange": 0.01,
                "maximumWithinPoseRms128": 0.02,
                "maximumWithinPoseStructuralDistance": 0.02,
                "minimumConnectedAcceptableCells": 3,
            }
        }
        result = summarize_base(manifest, [failing, passing], adapted, ["c0", "c1"])
        self.assertEqual(result["missingDeterministicRefinementCenterAnchors"], ["c0"])
        self.assertEqual(result["perAnchor"]["c0"]["passingCoarseWiderCandidates"], 0)
        self.assertEqual(result["perAnchor"]["c1"]["passingCoarseWiderCandidates"], 1)


if __name__ == "__main__":
    unittest.main()
