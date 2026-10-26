from __future__ import annotations

import unittest

from g2_p2_basin_audit import basin_decision, connected_positive_components, validate_grid


def protocol() -> dict:
    return {
        "frozenThresholds": {
            "acceptableScoreFloor": 0.5912307500839233, "maximumCandidatePoses": 320,
            "minimumConnectedAcceptableCells": 3, "minimumInteriorScoreMargin": 0.005,
            "minimumFreshCapturesPerCandidate": 2,
            "minimumFreshRepeatsPerRepresentative": 3, "maximumWithinPoseScoreRange": 0.01,
            "maximumWithinPoseRms128": 0.013763083470985293,
            "maximumWithinPoseStructuralDistance": 0.01,
        },
        "gridContract": {
            "historicalControlIds": ["A"], "requiredStages": ["anchor", "coarse"],
            "optionalStage": "refinement",
        },
        "verdicts": {"pass": "POSITIVE_BASIN_PASS", "fail": "POSITIVE_BASIN_FAIL"},
    }


def candidate(candidate_id: str, stage: str, offset: tuple[float, float, float]) -> dict:
    return {
        "candidateId": candidate_id, "anchorControlId": "A", "stage": stage,
        "centerCandidateId": "anchor", "longitudinalOffsetMm": offset[0],
        "lateralOffsetMm": offset[1], "yawDeg": offset[2],
    }


def valid_manifest() -> dict:
    candidates = [candidate("a0", "anchor", (0, 0, 0))]
    for name, offset in (
        ("l-", (-1, 0, 0)), ("l+", (1, 0, 0)),
        ("x-", (0, -1, 0)), ("x+", (0, 1, 0)),
        ("y-", (0, 0, -1)), ("y+", (0, 0, 1)),
    ):
        candidates.append(candidate(name, "coarse", offset))
    return {
        "mapCandidates": candidates,
        "neighborPairs": [["a0", "l-"], ["a0", "l+"], ["a0", "x-"], ["a0", "x+"],
                          ["a0", "y-"], ["a0", "y+"]],
    }


class Prompt2BasinAuditTests(unittest.TestCase):
    def test_exact_anchor_and_symmetric_grid_pass(self) -> None:
        self.assertEqual(validate_grid(valid_manifest(), protocol()), [])

    def test_missing_exact_anchor_fails(self) -> None:
        manifest = valid_manifest()
        manifest["mapCandidates"] = [item for item in manifest["mapCandidates"] if item["candidateId"] != "a0"]
        self.assertTrue(any("exact anchor" in value for value in validate_grid(manifest, protocol())))

    def test_asymmetric_grid_fails(self) -> None:
        manifest = valid_manifest()
        manifest["mapCandidates"] = [item for item in manifest["mapCandidates"] if item["candidateId"] != "l+"]
        self.assertTrue(any("asymmetric" in value for value in validate_grid(manifest, protocol())))

    def test_connectivity_never_crosses_anchor(self) -> None:
        rows = [candidate("a", "coarse", (0, 0, 0)), candidate("b", "coarse", (1, 0, 0))]
        rows[1]["anchorControlId"] = "B"
        self.assertEqual(connected_positive_components({"a", "b"}, [["a", "b"]], rows), [["a"], ["b"]])

    def test_negative_cannot_be_promoted_as_representative(self) -> None:
        manifest = valid_manifest()
        positive_ids = {"a0", "l-", "l+"}
        candidate_results = []
        for item in manifest["mapCandidates"]:
            positive = item["candidateId"] in positive_ids
            candidate_results.append({
                "candidateId": item["candidateId"], "acceptedFreshCaptures": 2 if positive else 0,
                "freshnessPass": positive, "repeatStable": positive, "allAcceptable": positive,
                "allFinalTargetAcceptable": positive, "allT7Valid": positive,
                "allContactValid": positive, "allWorkspaceValid": positive,
                "minimumScore": 0.61 if positive else 0.0, "scoreRange": 0.0,
                "maximumRms128": 0.0, "maximumStructuralDistance": 0.0,
                "rejectionReason": "" if positive else "not acceptable",
            })
        evidence = {
            "integrityStatus": "PASS", "imagePoseFreshness": "PASS",
            "attemptedCandidateIds": [item["candidateId"] for item in manifest["mapCandidates"]],
            "candidateResults": candidate_results,
            "representatives": [{
                "representativeId": "bad", "sourceCandidateId": "x-", "maximumRms128": 0,
                "maximumStructuralDistance": 0,
                "repeats": [{
                    "freshnessPass": True, "acceptable": False, "finalTargetAcceptable": False,
                    "t7Valid": True, "contactValid": True, "workspaceValid": True,
                    "score": {"total": 0.7},
                }] * 3,
            }],
        }
        self.assertEqual(basin_decision(manifest, evidence, protocol())["verdict"], "POSITIVE_BASIN_FAIL")


if __name__ == "__main__":
    unittest.main()
