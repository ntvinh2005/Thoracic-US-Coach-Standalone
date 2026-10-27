from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

from phase2_stop_state import Phase2DecisionState, decide_phase2, Phase2DecisionConfig
from run_active2d_policy_server import (
    PolicyMemory,
    apply_execution_feedback,
    register_phase_two_action,
)


FAULT_ID = "P3_ACCEPTED_INCOMPLETE_AFTER_PARTIAL_EXECUTION_V1"
TEST_NAME = "test_p3_accepted_incomplete_live_feedback_fail_closed"
MODULE = "Tools.ThoracicUSCoach.run_active2d_policy_server"
ZERO_DELTA = {"lateralMm": 0.0, "longitudinalMm": 0.0, "yawDeg": 0.0}
ISSUED_DELTA = {"lateralMm": 3.0, "longitudinalMm": 0.0, "yawDeg": 0.0}


def _canonical_sha256(value: Any) -> str:
    payload = json.dumps(value, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def build_production_fault_artifact(source_commit: str) -> dict[str, Any]:
    if len(source_commit) != 40:
        raise ValueError("full 40-character source commit required")

    best_state = {
        "lateralIndex": 0,
        "longitudinalIndex": 0,
        "yawIndex": 0,
        "bestLateralIndex": 0,
        "bestLongitudinalIndex": 0,
        "bestYawIndex": 0,
        "bestObservationIndex": 7,
        "bestQuality": 0.91,
        "bestStopScore": 0.96,
    }
    best_memory = {
        "recordId": "best_observation_0007",
        "canonicalSha256": _canonical_sha256(best_state),
    }
    state = Phase2DecisionState(
        stage="CONFIRM",
        lateral_index=best_state["lateralIndex"],
        longitudinal_index=best_state["longitudinalIndex"],
        yaw_index=best_state["yawIndex"],
        best_lateral_index=best_state["bestLateralIndex"],
        best_longitudinal_index=best_state["bestLongitudinalIndex"],
        best_yaw_index=best_state["bestYawIndex"],
        best_observation_index=best_state["bestObservationIndex"],
        best_quality=best_state["bestQuality"],
        best_stop_score=best_state["bestStopScore"],
    )
    memory = PolicyMemory(
        "p3_fault_accepted_incomplete_000",
        phase="LateralAcquisition",
        phase_two_prefix_actions=(),
        phase_two_state=state,
    )
    register_phase_two_action(memory, "Lateral", "confirm")
    request = {
        "contract": "active2d_image_only_v2",
        "hasPreviousActionFeedback": True,
        "previousAction": "LATERAL",
        "previousActionAccepted": True,
        "previousActionCompleted": False,
    }
    apply_execution_feedback(memory, request)

    post_best_state = {
        "lateralIndex": state.lateral_index,
        "longitudinalIndex": state.longitudinal_index,
        "yawIndex": state.yaw_index,
        "bestLateralIndex": state.best_lateral_index,
        "bestLongitudinalIndex": state.best_longitudinal_index,
        "bestYawIndex": state.best_yaw_index,
        "bestObservationIndex": state.best_observation_index,
        "bestQuality": state.best_quality,
        "bestStopScore": state.best_stop_score,
    }
    if post_best_state != best_state:
        raise RuntimeError("production feedback corrupted odometry or best memory")
    if memory.last_action != "RESET" or state.stage != "ABSTAIN":
        raise RuntimeError("production incomplete feedback did not fail closed")
    if memory.execution_feedback_action != "LATERAL":
        raise RuntimeError("production server did not retain actual executed-action feedback")

    decision = decide_phase2(
        state,
        Phase2DecisionConfig(
            prefix_actions=(), exploration_actions=("Lateral",), stop_threshold=0.93,
            required_confirmations=1,
        ),
        stop_score=1.0,
        quality_score=1.0,
    )
    if decision.action is not None or decision.stage != "ABSTAIN":
        raise RuntimeError("production state authorized an action after incomplete confirmation")

    history_tail = {
        "issuedAction": "LATERAL",
        "executedAction": memory.execution_feedback_action,
        "executedDelta": dict(ZERO_DELTA),
        "accepted": memory.execution_feedback_accepted,
        "completed": memory.execution_feedback_completed,
        "feedbackObservedBeforeAppend": memory.execution_feedback_applied,
    }
    transition = {
        "odometryDeltaApplied": dict(ZERO_DELTA),
        "postVisitedCellIds": ["cell_0_0_0"],
        "postBestMemory": dict(best_memory),
        "controllerDecision": "ABSTAIN",
        "stopIssued": False,
        "causalHistoryTail": history_tail,
    }
    trial = {
        "trialId": "p3_fault_accepted_incomplete_000",
        "faultId": FAULT_ID,
        "evidenceDomain": "DETERMINISTIC_FAULT_INJECTION",
        "synthetic": True,
        "faultInjected": True,
        "decisionSupportEligible": False,
        "trainingEligible": False,
        "issuedAction": "LATERAL",
        "actualExecutedAction": memory.execution_feedback_action,
        "actionAccepted": memory.execution_feedback_accepted,
        "actionCompleted": memory.execution_feedback_completed,
        "issuedDelta": dict(ISSUED_DELTA),
        "actualDelta": dict(ZERO_DELTA),
        "factualResultingDeltaProvided": False,
        "odometryDeltaApplied": dict(ZERO_DELTA),
        "preVisitedCellIds": ["cell_0_0_0"],
        "postVisitedCellIds": ["cell_0_0_0"],
        "plannedFullMoveTargetCellId": "cell_1_0_0",
        "preBestMemory": dict(best_memory),
        "postBestMemory": dict(best_memory),
        "safeReturn": {
            "returnAnchorSource": "last_confirmed_actual_pose",
            "assumesFullIssuedMove": False,
            "contactValid": True,
            "workspaceValid": True,
        },
        "controllerDecision": "ABSTAIN",
        "stopIssued": False,
        "causalActionHistory": [history_tail],
        "liveTransitionSemantics": transition,
    }
    return {
        "schemaVersion": 1,
        "sourceCommit": source_commit,
        "exportedBy": "Python",
        "module": MODULE,
        "testName": TEST_NAME,
        "result": "PASS",
        "productionFunctions": [
            "run_active2d_policy_server.register_phase_two_action",
            "run_active2d_policy_server.apply_execution_feedback",
            "phase2_stop_state.apply_execution_feedback",
            "phase2_stop_state.decide_phase2",
        ],
        "trial": trial,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Export Prompt 3 production accepted-incomplete evidence")
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    artifact = build_production_fault_artifact(args.source_commit)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(artifact, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "result": artifact["result"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
