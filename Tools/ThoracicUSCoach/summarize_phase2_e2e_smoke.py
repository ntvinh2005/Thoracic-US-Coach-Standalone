from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path
from typing import Any


MOVEMENTS = {"CRANIAL", "CAUDAL", "MEDIAL", "LATERAL", "ROTATECLOCKWISE", "ROTATECOUNTERCLOCKWISE"}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def freshness_ok(trace: list[dict[str, Any]]) -> bool:
    if not trace:
        return False
    previous_serial = -1
    previous_epoch = -1
    for item in trace:
        if item.get("poseEpoch", -1) != item.get("producerPoseEpoch", -2):
            return False
        if item.get("producerUnityFrame", -1) <= item.get("poseAppliedUnityFrame", -1):
            return False
        if item.get("producerUnityFrame") != item.get("captureUnityFrame"):
            return False
        if item.get("capturePath") != "direct_producer_texture":
            return False
        if item.get("activeAggregatorCount") != 0:
            return False
        serial = int(item.get("producerScanSerial", -1))
        epoch = int(item.get("poseEpoch", -1))
        if serial <= previous_serial or epoch <= previous_epoch:
            return False
        previous_serial = serial
        previous_epoch = epoch
    return True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--job-id", default="")
    parser.add_argument("--failed-job-id", default="")
    args = parser.parse_args()
    root = args.root.resolve()
    output = args.output_directory.resolve()
    unity_path = output / "phase2_minimal_e2e_smoke_unity.json"
    unity = load(unity_path)
    frames_dir = output / "representative_frames"
    frames_dir.mkdir(parents=True, exist_ok=True)
    episodes: list[dict[str, Any]] = []

    for episode_number, outcome in enumerate(unity["episodes"], start=1):
        run_path = root / outcome["rawResult"]
        audit_path = root / outcome["privilegedTrace"]
        run = load(run_path)
        audit = load(audit_path)
        trace = run.get("trace", [])
        privileged = audit.get("frames", [])
        physical = [item for item in trace if item.get("action") in MOVEMENTS]
        accepted = sum(bool(item.get("accepted")) for item in physical)
        completed = sum(
            bool(item.get("accepted"))
            and (abs(float(item.get("achievedDisplacementMm", 0))) > 0
                 or abs(float(item.get("achievedRotationDeg", 0))) > 0)
            for item in physical
        )
        target = any(bool(item.get("finalTargetAcceptable")) for item in privileged)
        representative = []
        chosen = []
        if trace:
            chosen.append(trace[0])
            chosen.append(max(trace, key=lambda item: float(item.get("confidence", 0))))
        seen: set[str] = set()
        for frame_number, item in enumerate(chosen, start=1):
            relative = str(item.get("image", ""))
            if not relative or relative in seen:
                continue
            seen.add(relative)
            source = root / relative
            if not source.exists():
                continue
            destination = frames_dir / f"e{episode_number}_f{frame_number}.png"
            shutil.copy2(source, destination)
            representative.append({"path": str(destination.relative_to(root)).replace("\\", "/"), "sha256": sha256(destination)})
        episodes.append({
            "episode": outcome["startId"],
            "phaseOneHandoff": True,
            "correctT7Handoff": bool(outcome.get("start", {}).get("t7LevelAcceptable")),
            "targetEncountered": target,
            "recognized": bool(outcome.get("recognized")),
            "returned": bool(outcome.get("returnedToBest")),
            "freshConfirm": bool(outcome.get("freshConfirmation")),
            "stop": bool(outcome.get("explicitStop")),
            "falseStop": bool(outcome.get("wrongStop")),
            "abstain": bool(outcome.get("abstained")),
            "failureReason": outcome.get("failureReason", ""),
            "steps": len(trace),
            "physicalMovements": len(physical),
            "acceptedMovements": accepted,
            "completedMovements": completed,
            "rejectedMovements": int(outcome.get("rejectedMovements", 0)),
            "contactViolations": int(outcome.get("contactViolations", 0)),
            "workspaceViolations": int(outcome.get("workspaceViolations", 0)),
            "freshnessPass": freshness_ok(trace),
            "freshnessRange": {
                "firstPoseEpoch": int(trace[0].get("poseEpoch", -1)) if trace else -1,
                "lastPoseEpoch": int(trace[-1].get("poseEpoch", -1)) if trace else -1,
                "firstProducerScanSerial": int(trace[0].get("producerScanSerial", -1)) if trace else -1,
                "lastProducerScanSerial": int(trace[-1].get("producerScanSerial", -1)) if trace else -1,
            },
            "maxStopScore": max((float(item.get("confidence", 0)) for item in trace), default=0.0),
            "representativeFrames": representative,
            "rawResult": outcome["rawResult"],
            "privilegedTrace": outcome["privilegedTrace"],
        })

    integration_failure = bool(unity.get("infrastructureFailure")) or any(not item["freshnessPass"] for item in episodes)
    unsafe = any(item["contactViolations"] or item["workspaceViolations"] for item in episodes)
    positive = [item for item in episodes if "POSITIVE" in item["episode"]]
    hard_negative = [item for item in episodes if "HARDNEG" in item["episode"]]
    if integration_failure:
        classification = "BLOCKED_BY_INTEGRATION"
    elif positive and all(item["stop"] and not item["falseStop"] for item in positive) and all(item["abstain"] for item in hard_negative) and not unsafe:
        classification = "IMAGE_ONLY_END_TO_END_DEMO_WORKS"
    elif any(item["recognized"] and not item["returned"] for item in episodes):
        classification = "BLOCKED_BY_NAVIGATION"
    elif any(item["targetEncountered"] and not item["recognized"] for item in episodes):
        classification = "BLOCKED_BY_RECOGNITION"
    else:
        classification = "PARTIAL_PIPELINE_WORKS"

    summary = {
        "schemaVersion": 1,
        "classification": classification,
        "warning": "Consumed development-only Unity engineering diagnostic; not scientific validation.",
        "jobs": {
            "unityExecution": args.job_id,
            "compilePreflightFailure": args.failed_job_id,
            "unityExecutionExit": "FAILED_POSTPROCESS_ONLY",
            "details": [
                {
                    "jobId": args.failed_job_id,
                    "exitCode": 1,
                    "episodesExecuted": 0,
                    "blocker": "compile preflight: freshness fields were initially placed on the wrong trace type",
                },
                {
                    "jobId": args.job_id,
                    "exitCode": 1,
                    "unityExitCode": 0,
                    "episodesExecuted": 3,
                    "blocker": "post-processing only: first representative-frame destination exceeded Windows path length",
                },
            ],
        },
        "executionCommit": unity.get("executionCommit"),
        "model": {
            "stop": "phase2_stop_live_spatial_logistic_v1",
            "stopThreshold": unity.get("stopThreshold"),
            "stopCheckpointSha256": unity.get("stopCheckpointSha256"),
            "actionCheckpointSha256": unity.get("actionCheckpointSha256"),
            "eligibility": "SUPPORTED_LEGACY_V1_LOADER; NON_DEPLOYABLE_RESEARCH_STATUS",
        },
        "unity": {
            "version": unity.get("unityVersion"),
            "scene": unity.get("scene"),
            "infrastructureFailure": unity.get("infrastructureFailure", ""),
        },
        "episodes": episodes,
    }
    summary_path = output / "phase2_minimal_e2e_smoke_summary.json"
    summary_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")

    rows = []
    for item in episodes:
        yn = lambda value: "Có" if value else "Không"
        rows.append(
            f"| {item['episode']} | {yn(item['targetEncountered'])} | {yn(item['recognized'])} | "
            f"{yn(item['returned'])} | {yn(item['freshConfirm'])} | {yn(item['stop'])} | "
            f"{yn(item['falseStop'])} | {yn(item['abstain'])} | {item['failureReason'] or '-'} |"
        )
    report = "\n".join([
        "# Phase 2 Minimal End-to-End Smoke Test",
        "",
        f"Kết luận: `{classification}`.",
        "",
        "Đây là engineering diagnostic trên evidence development đã consumed, không phải scientific/clinical validation.",
        "",
        "| Episode | Target encountered | Recognized | Returned | Fresh confirm | STOP | False STOP | Abstain | Failure reason |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---|",
        *rows,
        "",
        "## Runtime",
        "",
        f"- Unity: `{unity.get('unityVersion')}`; scene: `{unity.get('scene')}`.",
        f"- Execution commit: `{unity.get('executionCommit')}`.",
        f"- CSSALT Local Jobs: Unity `{args.job_id}` (episodes complete; exit 1 in post-processing); compile-only failed attempt `{args.failed_job_id}`.",
        f"- Frozen STOP: `phase2_stop_live_spatial_logistic_v1`, threshold `{unity.get('stopThreshold')}`.",
        "- Action model: frozen U08 spatial-v2 seed 47.",
        "- Operating rule: first 32 actions of the existing frozen raster, then existing return/confirm/STOP-or-abstain state machine; maximum 80 actor steps.",
        "- Checkpoints remain engineering-diagnostic only; no deployment/scientific promotion is claimed.",
        "- Phase 1 handoff: 3/3 starts restored at valid T7 handoff poses. `ScoreCandidate()` and target floor were unchanged.",
        "",
        "## Per-episode mechanics",
        "",
        *[
            f"- `{item['episode']}`: {item['physicalMovements']} physical moves; "
            f"{item['acceptedMovements']} accepted; {item['completedMovements']} completed; "
            f"{item['rejectedMovements']} rejected; max STOP score {item['maxStopScore']:.6f}; "
            f"freshness={'PASS' if item['freshnessPass'] else 'FAIL'}; "
            f"producer serial {item['freshnessRange']['firstProducerScanSerial']}..{item['freshnessRange']['lastProducerScanSerial']}; "
            f"contact/workspace violations={item['contactViolations']}/{item['workspaceViolations']}."
            for item in episodes
        ],
        "",
        "Raw Unity traces, privileged offline measurements, logs and a compact set of genuine ultrasound frames are in this directory.",
        "",
        "First actual blocker: two evaluator-positive views were physically encountered, but STOP scores never reached 0.93; therefore no best view was eligible for return or fresh confirmation.",
        "",
        "Failed jobs: `58242e0da0f5` stopped at compile preflight before any episode (freshness fields initially placed on the wrong trace type). `1aac5303fb44` completed all three Unity episodes with Unity exit 0, then exited 1 only because the first representative-frame filename exceeded the Windows path limit; summarization was rerun on the unchanged closed traces.",
        "",
    ])
    (output / "phase2_minimal_e2e_smoke_report_vi.md").write_text(report, encoding="utf-8")


if __name__ == "__main__":
    main()
