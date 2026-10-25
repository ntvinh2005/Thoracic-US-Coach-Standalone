from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Iterable

import numpy as np


RESET = "RESET"


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def normalized_action(action: str | None) -> str:
    return str(action or RESET).replace("_", "").upper()


def frozen_training_action(stream_frame: dict[str, Any]) -> str:
    """Return the action encoded by the historical spatial-v2 trainer.

    The source stream stores the incoming action on the same row as the image it
    produced.  frame_stop_feature_matrix_v2 used this field verbatim and did
    not apply accepted/completed feedback.
    """
    return normalized_action(stream_frame.get("issuedAction"))


def causal_live_action(
    *,
    episode_reset: bool,
    feedback_available: bool,
    action: str | None = None,
    accepted: bool = False,
    completed: bool = False,
) -> str | None:
    """Action feature genuinely available for the image currently observed.

    A reset image has no causally preceding actor action.  After a normal live
    request, the feedback describes the command issued for the preceding image.
    Rejected and incomplete actions are represented as RESET, matching the live
    policy server.  Missing non-reset feedback is unavailable, never guessed.
    """
    if episode_reset:
        return RESET
    if not feedback_available:
        return None
    if completed and not accepted:
        raise ValueError("completed action cannot be rejected")
    return normalized_action(action) if accepted and completed else RESET


def align_outgoing_trace_action(rows: list[dict[str, Any]], current_index: int) -> str | None:
    """Align an outgoing-action trace to the next captured image."""
    if current_index == 0 or bool(rows[current_index].get("reset", False)):
        return RESET
    previous = rows[current_index - 1]
    return causal_live_action(
        episode_reset=False,
        feedback_available=True,
        action=previous.get("action"),
        accepted=bool(previous.get("accepted", False)),
        completed=bool(previous.get("completed", previous.get("accepted", False))),
    )


def binary_metrics(labels: Iterable[bool], scores: Iterable[float], threshold: float) -> dict[str, Any]:
    y = np.asarray(list(labels), dtype=bool)
    values = np.asarray(list(scores), dtype=np.float64)
    predicted = values >= threshold
    tp = int(np.sum(y & predicted))
    fp = int(np.sum(~y & predicted))
    fn = int(np.sum(y & ~predicted))
    tn = int(np.sum(~y & ~predicted))
    recall = tp / (tp + fn) if tp + fn else None
    fpr = fp / (fp + tn) if fp + tn else None
    specificity = tn / (tn + fp) if tn + fp else None
    if y.any() and (~y).any():
        positive, negative = values[y], values[~y]
        auc = float((positive[:, None] > negative[None, :]).mean()
                    + 0.5 * (positive[:, None] == negative[None, :]).mean())
    else:
        auc = None
    return {
        "count": int(len(y)),
        "positiveSupport": int(y.sum()),
        "truePositive": tp,
        "falsePositive": fp,
        "falseNegative": fn,
        "trueNegative": tn,
        "recall": recall,
        "falsePositiveRate": fpr,
        "balancedAccuracy": 0.5 * (recall + specificity)
        if recall is not None and specificity is not None else None,
        "accuracy": float(np.mean(predicted == y)) if len(y) else None,
        "rocAuc": auc,
    }


def source_path(source_root: Path, relative: str) -> Path:
    normalized = str(relative).replace("\\", "/")
    marker = "Artifacts/ThoracicUSCoach/"
    return source_root / "Artifacts" / "ThoracicUSCoach" / normalized.split(marker, 1)[1]


def audit_live_training_history(source_root: Path, exclusive_upper_bound: str) -> dict[str, Any]:
    run_root = source_root / "Artifacts/ThoracicUSCoach/active2d/runs"
    audit_root = source_root / "Artifacts/ThoracicUSCoach/active2d/runs_privileged"
    observations = rejected_predecessors = incomplete_predecessors = 0
    episodes = 0
    for run_path in sorted(run_root.glob("LIVE_A2D_*.json")):
        episode_id = run_path.stem
        if episode_id >= exclusive_upper_bound or not (audit_root / run_path.name).is_file():
            continue
        trace = list(load_json(run_path).get("trace", []))
        handoff = next((index for index, item in enumerate(trace)
                        if str(item.get("action", "")).upper() == "PHASEHANDOFF"), None)
        if handoff is None:
            continue
        episodes += 1
        previous: dict[str, Any] | None = None
        for item in trace[handoff + 1:]:
            if item.get("phase") not in {"LateralAcquisition", "FinalSuccess", "FinalFailure"}:
                continue
            observations += 1
            if previous is not None:
                accepted = bool(previous.get("accepted", True))
                requested = abs(float(previous.get("requestedDisplacementMm", 0.0)))
                achieved = abs(float(previous.get("achievedDisplacementMm", 0.0)))
                completed = accepted and (requested <= 1e-9 or abs(achieved - requested) <= 0.5)
                rejected_predecessors += int(not accepted)
                incomplete_predecessors += int(accepted and not completed)
            previous = item
    return {
        "episodes": episodes,
        "observations": observations,
        "observationsAfterRejectedAction": rejected_predecessors,
        "observationsAfterAcceptedIncompleteAction": incomplete_predecessors,
        "note": "Historical spatial-v2 live-adaptation encoded the prior action string even for these rows; live inference maps rejected/incomplete feedback to RESET.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="G2-R4 P0 frozen spatial-v2 action parity audit")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--source-root", type=Path, default=Path(r"D:\CSSALT\Thoracic-US-Coach-Standalone"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    source_root = args.source_root.resolve()
    output = args.output or (repo / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_revised_20261025")
    output.mkdir(parents=True, exist_ok=True)

    declaration_path = repo / "Tools/ThoracicUSCoach/experiments/g2_r3_a2_exact_pose_v1.json"
    unity_path = repo / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r3_a2_20261025/unity_capture/g2_r3_a2_unity_report.json"
    checkpoint_path = repo / "Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_stop_live_spatial_logistic_v1.json"
    r4_script = repo / "Tools/ThoracicUSCoach/run_g2_r4_a.py"
    declaration = load_json(declaration_path)
    unity = load_json(unity_path)
    checkpoint = load_json(checkpoint_path)
    threshold = float(checkpoint["stopRule"]["threshold"])
    if not np.isclose(threshold, 0.93):
        raise RuntimeError(f"Frozen threshold changed: {threshold}")

    sys.path.insert(0, str(repo / "Tools/ThoracicUSCoach"))
    from active2d_stop import request_stop_features, stop_probabilities

    declared = {item["caseId"]: item for item in declaration["cases"]}
    rows: list[dict[str, Any]] = []
    integrity_errors: list[str] = []
    for case in unity["cases"]:
        item = declared[case["caseId"]]
        original_image = source_path(source_root, item["sourceImage"])
        stream = load_json(original_image.parent / "stream_manifest.json")
        source_frame = next(frame for frame in stream["frames"]
                            if int(frame["index"]) == int(item["sourceFrameIndex"]))
        training_action = frozen_training_action(source_frame)
        labels = [bool(obs["targetAcceptable"]) for obs in case["observations"]]
        stable = len(set(labels)) == 1
        for observation in case["observations"]:
            image = repo / str(observation["image"]).replace("\\", "/")
            if sha256(image) != observation["imageSha256"]:
                integrity_errors.append(str(image))
            action_by_protocol = {
                "historicalResetDiagnostic": RESET,
                "frozenTrainingFeatureCounterfactual": training_action,
                "causalLiveExactRestore": causal_live_action(
                    episode_reset=True, feedback_available=False),
            }
            predictions = {}
            feature_hashes = {}
            for protocol, action in action_by_protocol.items():
                if action is None:
                    predictions[protocol] = None
                    feature_hashes[protocol] = None
                    continue
                features = request_stop_features(image, None, action, "spatial-v2")
                predictions[protocol] = float(stop_probabilities(checkpoint, features)[0])
                feature_hashes[protocol] = hashlib.sha256(features.tobytes()).hexdigest()
            rows.append({
                "caseId": case["caseId"],
                "family": case["family"],
                "sourceFrameIndex": int(item["sourceFrameIndex"]),
                "repeatIndex": int(observation["repeatIndex"]),
                "image": observation["image"],
                "imageSha256": observation["imageSha256"],
                "targetAcceptable": bool(observation["targetAcceptable"]),
                "stablePoseLabel": stable,
                "sourceIncomingAction": training_action,
                "sourceActionAccepted": bool(source_frame.get("actionAccepted", True)),
                "sourceActionCompleted": bool(source_frame.get("actionCompleted", source_frame.get("actionAccepted", True))),
                "protocolActions": action_by_protocol,
                "probabilities": predictions,
                "featureSha256": feature_hashes,
            })

    stable_rows = [row for row in rows if row["stablePoseLabel"]]
    protocols: dict[str, Any] = {}
    for name in ("historicalResetDiagnostic", "frozenTrainingFeatureCounterfactual", "causalLiveExactRestore"):
        available = [row for row in stable_rows if row["probabilities"][name] is not None]
        protocols[name] = {
            "metrics": binary_metrics(
                [row["targetAcceptable"] for row in available],
                [row["probabilities"][name] for row in available],
                threshold,
            ),
            "availableSamples": len(available),
        }

    historical = protocols["historicalResetDiagnostic"]["metrics"]
    causal = protocols["causalLiveExactRestore"]["metrics"]
    reproduced = (
        historical["count"] == 90
        and historical["positiveSupport"] == 30
        and historical["truePositive"] == 16
        and historical["falsePositive"] == 1
    )
    reset_equals_causal = all(
        row["featureSha256"]["historicalResetDiagnostic"]
        == row["featureSha256"]["causalLiveExactRestore"] for row in rows
    )
    training = checkpoint.get("training", {})
    live_history = audit_live_training_history(
        source_root,
        str(training.get("liveEpisodeExclusiveUpperBound", "LIVE_A2D_20261023_113700_000")),
    )
    report = {
        "schemaVersion": 1,
        "taskId": "G2-R4-REVISED-P0",
        "decision": "PASS" if not integrity_errors and reproduced and reset_equals_causal else "FAIL",
        "sourceCommit": "de50f596b396a842b3df7a52ebf6f94f25900585",
        "checkpoint": {
            "path": str(checkpoint_path.relative_to(repo)).replace("\\", "/"),
            "sha256": sha256(checkpoint_path),
            "threshold": threshold,
            "featureExtractor": checkpoint["architecture"]["featureExtractor"],
        },
        "sourceHashes": {
            "r4AScriptSha256": sha256(r4_script),
            "active2dStopSha256": sha256(repo / "Tools/ThoracicUSCoach/active2d_stop.py"),
            "active2dDataSha256": sha256(repo / "Tools/ThoracicUSCoach/active2d_data.py"),
            "liveServerSha256": sha256(repo / "Tools/ThoracicUSCoach/run_active2d_policy_server.py"),
            "unityActive2DControllerSha256": sha256(repo / "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs"),
            "declarationSha256": sha256(declaration_path),
            "freshUnityReportSha256": sha256(unity_path),
        },
        "semantics": {
            "sourceRasterStream": "issuedAction on image row t is the accepted command executed before I_t was captured; RESET marks the initial/no-movement observation.",
            "liveRequest": "I_t is captured first; request t carries feedback for the command issued after I_(t-1). Server uses that action only when accepted and completed, otherwise RESET.",
            "exactPoseAudit": "Each case is externally restored before repeat rendering. No actor action causes a fresh repeat, so causal live input is RESET.",
            "historicalTrainer": "frame_stop_feature_matrix_v2 encoded frame.issued_action verbatim and ignored accepted/completed flags.",
        },
        "historicalResultReproduced": reproduced,
        "historicalResetExactlyEqualsCausalRestore": reset_equals_causal,
        "integrityErrors": integrity_errors,
        "protocolsStableLabel90": protocols,
        "deltaCounterfactualTrainingMinusCausal": {
            key: protocols["frozenTrainingFeatureCounterfactual"]["metrics"][key] - causal[key]
            for key in ("recall", "falsePositiveRate", "balancedAccuracy", "rocAuc")
        },
        "liveAdaptationHistoryAudit": live_history,
        "interpretation": {
            "r4ABiased": False if reset_equals_causal else None,
            "direction": "none for exact-pose fresh renders; RESET was the causally correct action",
            "separateDefect": "Historical training encoded rejected prior actions instead of RESET for a bounded subset of live-adaptation observations.",
            "cnnMeaning": "The CNN comparison is not rescued or invalidated by action timing on the exact-pose fresh renders; the frozen baseline remains stronger cross-family evidence than the R4-A CNN.",
        },
        "predictions": rows,
    }
    output_path = output / "g2_r4_p0_baseline_parity.json"
    output_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output_path),
        "decision": report["decision"],
        "historical": historical,
        "trainingCounterfactual": protocols["frozenTrainingFeatureCounterfactual"]["metrics"],
        "causalLive": causal,
        "liveHistory": live_history,
    }, indent=2))


if __name__ == "__main__":
    main()
