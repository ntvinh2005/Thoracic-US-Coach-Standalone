from __future__ import annotations

import unittest

from g2_p3_conditional_evaluation import (
    authorize_stage_b,
    final_classification,
    model_evaluation_gate,
    validate_stage_b_result,
    validate_stage_c_result,
    validate_stage_d_plan,
)


class Prompt3ConditionalEvaluationTests(unittest.TestCase):
    def test_stage_b_requires_complete_stage_f_final_pass(self) -> None:
        audit = {
            "verdict": "STAGE_F_FINAL_PASS",
            "authorizedNextStage": True,
            "integrityStatus": "PASS",
            "prospectiveSignals": {
                "FINAL_CAPTURE_ENGINEERING_PASS": True,
                "HISTORICAL_F3_VERDICT": "IMAGE_POSE_FRESHNESS_FAIL",
            },
        }
        self.assertTrue(authorize_stage_b(audit)["authorized"])
        audit["prospectiveSignals"]["HISTORICAL_F3_VERDICT"] = "PASS"
        self.assertFalse(authorize_stage_b(audit)["authorized"])

    def test_single_isolated_positive_cannot_pass_stage_b(self) -> None:
        result = {
            "verdict": "POSITIVE_BASIN_PASS",
            "candidateCount": 12,
            "exactAnchorsPass": True,
            "symmetricGridPass": True,
            "deterministicRefinementPass": True,
            "eligiblePositiveComponents": [["only"]],
            "representatives": [{
                "pass": True, "stableInterior": True,
                "freshRepeatsPass": True, "structuralVariationPass": True,
            }],
            "isolatedPositiveAcceptedAsBasin": False,
            "acceptableScoreFloor": 0.5912307500839233,
        }
        self.assertEqual(validate_stage_b_result(result)["verdict"], "FAIL")
        result["eligiblePositiveComponents"] = [["a", "b"]]
        self.assertEqual(validate_stage_b_result(result)["verdict"], "FAIL")
        result["eligiblePositiveComponents"] = [["a", "b", "c"]]
        self.assertEqual(validate_stage_b_result(result)["verdict"], "PASS")

    def test_stage_c_enforces_routes_moves_observations_and_new_start(self) -> None:
        result = {
            "stageBVerdict": "POSITIVE_BASIN_PASS",
            "routes": [{
                "moves": 60, "acceptedObservations": 64, "startSource": "marker",
                "startTargetIndependent": True, "imageOnlyActions": True,
                "newStart": True, "stableAcceptableT7BasinReached": True,
            }],
            "historicalLockedFamiliesUsed": False,
            "reservedConfirmationUsed": False,
        }
        self.assertEqual(validate_stage_c_result(result)["verdict"], "SAFE_TARGET_INDEPENDENT_REACHABILITY_PASS")
        result["routes"][0]["moves"] = 61
        self.assertEqual(validate_stage_c_result(result)["verdict"], "SAFE_TARGET_INDEPENDENT_REACHABILITY_FAIL")

    def test_stage_d_plan_is_bounded_and_causal(self) -> None:
        stage_c = {"verdict": "SAFE_TARGET_INDEPENDENT_REACHABILITY_PASS", "authorizedStageD": True}
        plan = {
            "trajectories": [
                {"kind": "randomized", "maximumAcceptedObservations": 121, "causalExecutedActionHistory": True},
                {"kind": "enriched", "maximumAcceptedObservations": 121, "causalExecutedActionHistory": True},
            ],
            "historicalLockedFamiliesUsed": False,
            "reservedConfirmationUsed": False,
        }
        self.assertTrue(validate_stage_d_plan(plan, stage_c)["captureAuthorized"])
        plan["trajectories"][0]["maximumAcceptedObservations"] = 122
        self.assertFalse(validate_stage_d_plan(plan, stage_c)["captureAuthorized"])

    def test_model_gate_preserves_scientific_thresholds_and_blocked_checkpoint(self) -> None:
        readiness = {"scientificTrainingDataReady": True, "immutable": True, "sha256": "a" * 64}
        result = {
            "groupSplitPass": True, "frameLevelSplitUsed": False, "seeds": [13, 29, 47],
            "maximumEpochs": 120, "causalActuallyExecutedActionHistory": True,
            "offlineLivePreprocessingParity": True,
            "metrics": {
                "recognitionRecall": 0.8, "observedFalseStopFrames": 0,
                "qualitySpearman": 0.6, "medianBestViewRegret": 0.015,
            },
            "checkpointDeploymentStatus": "BLOCKED", "normalLiveStopAuthorized": False,
            "qualityCanAuthorizeStop": False, "historicalLockedFamiliesUsed": False,
            "reservedConfirmationUsed": False,
        }
        self.assertEqual(model_evaluation_gate(result, readiness)["verdict"], "PASS")
        result["metrics"]["observedFalseStopFrames"] = 1
        self.assertEqual(model_evaluation_gate(result, readiness)["verdict"], "FAIL")

    def test_exact_final_classifications_and_claim_boundaries(self) -> None:
        gates = {"physical": True, "capture": True, "recognition": True, "return": True, "integrity": True}
        a = final_classification(
            mandatory_gates=gates, scientific_validation_pass=True,
            genuine_image_only_end_to_end_demo_pass=True,
        )
        self.assertEqual(a["classification"], "SCIENTIFICALLY_VALIDATED_FIXED_ANATOMY_SIMULATOR_POC")
        b = final_classification(
            mandatory_gates=gates, scientific_validation_pass=False,
            genuine_image_only_end_to_end_demo_pass=True,
        )
        self.assertEqual(b["classification"], "ENGINEERING_DEMO_ONLY")
        c = final_classification(
            mandatory_gates={**gates, "return": False}, scientific_validation_pass=False,
            genuine_image_only_end_to_end_demo_pass=False, failure_attribution="implementation failure",
        )
        self.assertEqual(c["classification"], "PHASE2_NO_GO")
        self.assertFalse(c["fundamentalImpossibilityClaimed"])


if __name__ == "__main__":
    unittest.main()
