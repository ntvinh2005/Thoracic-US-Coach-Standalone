from __future__ import annotations

from typing import Any


STAGE_F_PASS = "STAGE_F_FINAL_PASS"
STAGE_B_PASS = "CORRECTED_POSITIVE_BASIN_PASS"
STAGE_C_PASS = "IMAGE_ONLY_REACHABILITY_PASS"


def authorize_stage_b(stage_f_audit: dict[str, Any]) -> dict[str, Any]:
    passed = (
        stage_f_audit.get("verdict") == STAGE_F_PASS
        and stage_f_audit.get("authorizedNextStage") is True
        and stage_f_audit.get("integrityStatus") == "PASS"
        and stage_f_audit.get("prospectiveSignals", {}).get("FINAL_CAPTURE_ENGINEERING_PASS") is True
        and stage_f_audit.get("prospectiveSignals", {}).get("HISTORICAL_F3_VERDICT")
        == "IMAGE_POSE_FRESHNESS_FAIL"
    )
    return {"authorized": passed, "nextStage": "B" if passed else None}


def validate_stage_b_result(result: dict[str, Any]) -> dict[str, Any]:
    candidate_count = int(result.get("candidateCount", -1))
    components = result.get("eligiblePositiveComponents", [])
    representatives = result.get("representatives", [])
    checks = {
        "verdict": result.get("verdict") == STAGE_B_PASS,
        "fixedGridBound": 0 < candidate_count <= 320,
        "exactAnchors": result.get("exactAnchorsPass") is True,
        "symmetricGrid": result.get("symmetricGridPass") is True,
        "deterministicRefinement": result.get("deterministicRefinementPass") is True,
        "connectedPositive": any(len(component) >= 2 for component in components if isinstance(component, list)),
        "stableInterior": any(
            item.get("pass") is True and item.get("stableInterior") is True
            and item.get("freshRepeatsPass") is True and item.get("structuralVariationPass") is True
            for item in representatives if isinstance(item, dict)
        ),
        "isolatedPositiveInsufficient": result.get("isolatedPositiveAcceptedAsBasin") is False,
        "evaluatorUnchanged": float(result.get("acceptableScoreFloor", -1.0)) == 0.5912307500839233,
    }
    passed = all(checks.values())
    return {"verdict": "PASS" if passed else "FAIL", "authorizedStageC": passed, "checks": checks}


def validate_stage_c_result(result: dict[str, Any]) -> dict[str, Any]:
    routes = result.get("routes", [])
    if not isinstance(routes, list):
        routes = []
    total_observations = sum(int(item.get("acceptedObservations", 0)) for item in routes if isinstance(item, dict))
    routes_valid = bool(routes) and len(routes) <= 8 and all(
        int(item.get("moves", -1)) <= 60
        and int(item.get("moves", -1)) >= 0
        and item.get("startSource") in {"marker", "skin"}
        and item.get("startTargetIndependent") is True
        and item.get("imageOnlyActions") is True
        for item in routes if isinstance(item, dict)
    )
    reached = any(
        item.get("newStart") is True and item.get("stableAcceptableT7BasinReached") is True
        for item in routes if isinstance(item, dict)
    )
    checks = {
        "stageB": result.get("stageBVerdict") == STAGE_B_PASS,
        "routeBudgetAndSemantics": routes_valid,
        "acceptedObservationBudget": total_observations <= 512,
        "newStartReachability": reached,
        "lockedFamiliesExcluded": result.get("historicalLockedFamiliesUsed") is False,
        "reservedConfirmationExcluded": result.get("reservedConfirmationUsed") is False,
    }
    passed = all(checks.values())
    return {
        "verdict": STAGE_C_PASS if passed else "IMAGE_ONLY_REACHABILITY_FAIL",
        "authorizedStageD": passed,
        "acceptedObservations": total_observations,
        "checks": checks,
    }


def validate_stage_d_plan(plan: dict[str, Any], stage_c_result: dict[str, Any]) -> dict[str, Any]:
    trajectories = plan.get("trajectories", [])
    if not isinstance(trajectories, list):
        trajectories = []
    randomized = [item for item in trajectories if item.get("kind") == "randomized"]
    enriched = [item for item in trajectories if item.get("kind") == "enriched"]
    checks = {
        "stageC": stage_c_result.get("verdict") == STAGE_C_PASS and stage_c_result.get("authorizedStageD") is True,
        "trajectoryBudget": 0 < len(trajectories) <= 16,
        "randomizedBudget": len(randomized) <= 8,
        "enrichedBudget": len(enriched) <= 8,
        "knownKindsOnly": len(randomized) + len(enriched) == len(trajectories),
        "observationBudget": all(0 <= int(item.get("maximumAcceptedObservations", -1)) <= 121 for item in trajectories),
        "causalExecutedHistory": all(item.get("causalExecutedActionHistory") is True for item in trajectories),
        "lockedFamiliesExcluded": plan.get("historicalLockedFamiliesUsed") is False,
        "reservedConfirmationExcluded": plan.get("reservedConfirmationUsed") is False,
    }
    passed = all(checks.values())
    return {"verdict": "PASS" if passed else "FAIL", "captureAuthorized": passed, "checks": checks}


def model_evaluation_gate(result: dict[str, Any], readiness: dict[str, Any]) -> dict[str, Any]:
    metrics = result.get("metrics", {})
    checks = {
        "readinessGo": readiness.get("scientificTrainingDataReady") is True
        or readiness.get("engineeringDemoDataReady") is True,
        "immutableReadiness": readiness.get("immutable") is True and len(str(readiness.get("sha256", ""))) == 64,
        "groupSplit": result.get("groupSplitPass") is True and result.get("frameLevelSplitUsed") is False,
        "seeds": result.get("seeds") == [13, 29, 47],
        "epochBudget": 0 < int(result.get("maximumEpochs", -1)) <= 120,
        "causalHistory": result.get("causalActuallyExecutedActionHistory") is True,
        "preprocessingParity": result.get("offlineLivePreprocessingParity") is True,
        "recognitionRecall": float(metrics.get("recognitionRecall", -1.0)) >= 0.8,
        "zeroFalseStop": int(metrics.get("observedFalseStopFrames", -1)) == 0,
        "qualitySpearman": float(metrics.get("qualitySpearman", -1.0)) >= 0.6,
        "medianRegret": 0.0 <= float(metrics.get("medianBestViewRegret", -1.0)) <= 0.015,
        "checkpointBlocked": result.get("checkpointDeploymentStatus") == "BLOCKED"
        and result.get("normalLiveStopAuthorized") is False,
        "qualityRankingOnly": result.get("qualityCanAuthorizeStop") is False,
        "lockedFamiliesExcluded": result.get("historicalLockedFamiliesUsed") is False,
        "reservedConfirmationExcluded": result.get("reservedConfirmationUsed") is False,
    }
    passed = all(checks.values())
    scope = "SCIENTIFIC_DEVELOPMENT" if readiness.get("scientificTrainingDataReady") is True else "ENGINEERING_DEMO_ONLY"
    return {"verdict": "PASS" if passed else "FAIL", "scope": scope, "livePromotionAuthorized": False, "checks": checks}


def final_classification(
    *,
    mandatory_gates: dict[str, bool],
    scientific_validation_pass: bool,
    genuine_image_only_end_to_end_demo_pass: bool,
    failure_attribution: str | None = None,
) -> dict[str, Any]:
    failed = sorted(key for key, value in mandatory_gates.items() if value is not True)
    if failed:
        attribution = failure_attribution or "incomplete coverage"
        if attribution not in {
            "implementation failure", "insufficient data", "incomplete coverage", "observability hypothesis"
        }:
            raise ValueError("unsupported failure attribution")
        return {
            "classification": "PHASE2_NO_GO",
            "class": "C",
            "failedMandatoryGates": failed,
            "failureAttribution": attribution,
            "fundamentalImpossibilityClaimed": False,
        }
    if scientific_validation_pass:
        return {
            "classification": "SCIENTIFICALLY_VALIDATED_FIXED_ANATOMY_SIMULATOR_POC",
            "class": "A",
            "claimBoundary": "same Unity anatomy only; no clinical or unseen-patient claims",
        }
    if genuine_image_only_end_to_end_demo_pass:
        return {
            "classification": "ENGINEERING_DEMO_ONLY",
            "class": "B",
            "deploymentStatus": "NON_DEPLOYABLE",
        }
    return {
        "classification": "PHASE2_NO_GO",
        "class": "C",
        "failedMandatoryGates": [],
        "failureAttribution": failure_attribution or "incomplete coverage",
        "fundamentalImpossibilityClaimed": False,
    }
