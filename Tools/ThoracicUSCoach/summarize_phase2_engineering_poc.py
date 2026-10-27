from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path
from typing import Any


MOVEMENTS = {
    "CRANIAL", "CAUDAL", "MEDIAL", "LATERAL",
    "ROTATECLOCKWISE", "ROTATECOUNTERCLOCKWISE",
}


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


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
    args = parser.parse_args()
    root = args.root.resolve()
    output = args.output_directory.resolve()
    unity = load(output / "phase2_engineering_poc_unity.json")
    frames_dir = output / "representative_frames"
    frames_dir.mkdir(parents=True, exist_ok=True)
    episodes: list[dict[str, Any]] = []

    for number, outcome in enumerate(unity["episodes"], start=1):
        run = load(root / outcome["rawResult"])
        audit = load(root / outcome["privilegedTrace"])
        trace = run.get("trace", [])
        privileged = audit.get("frames", [])
        physical = [item for item in trace if str(item.get("action", "")).upper() in MOVEMENTS]
        max_item = max(trace, key=lambda item: float(item.get("confidence", 0)), default={})
        target = any(bool(item.get("finalTargetAcceptable")) for item in privileged)
        confirmation = next(
            (item for item in reversed(trace) if item.get("freshCapturePurpose") == "confirmation"),
            None,
        )
        source_item = confirmation or max_item
        representative = []
        relative = str(source_item.get("image", "")) if source_item else ""
        if relative:
            source = root / relative
            if source.exists():
                destination = frames_dir / f"episode_{number}.png"
                shutil.copy2(source, destination)
                representative.append({
                    "path": str(destination.relative_to(root)).replace("\\", "/"),
                    "sha256": sha256(destination),
                })
        episodes.append({
            "episode": outcome["startId"],
            "targetEncountered": target,
            "recognized": bool(outcome.get("recognized")),
            "maxRecognitionScore": float(max_item.get("confidence", 0.0)) if max_item else 0.0,
            "returned": bool(outcome.get("returnedToBest")),
            "freshConfirm": bool(outcome.get("freshConfirmation")),
            "stop": bool(outcome.get("explicitStop")),
            "correctStop": bool(outcome.get("explicitStop")) and bool(outcome.get("acquisitionSuccess")),
            "falseStop": bool(outcome.get("wrongStop")),
            "abstain": bool(outcome.get("abstained")),
            "failureReason": outcome.get("failureReason", ""),
            "steps": len(trace),
            "physicalMovements": len(physical),
            "acceptedMovements": sum(bool(item.get("accepted")) for item in physical),
            "completedMovements": sum(
                bool(item.get("accepted"))
                and (abs(float(item.get("achievedDisplacementMm", 0))) > 0
                     or abs(float(item.get("achievedRotationDeg", 0))) > 0)
                for item in physical
            ),
            "rejectedMovements": int(outcome.get("rejectedMovements", 0)),
            "contactViolations": int(outcome.get("contactViolations", 0)),
            "workspaceViolations": int(outcome.get("workspaceViolations", 0)),
            "freshnessPass": freshness_ok(trace),
            "representativeFrames": representative,
            "rawResult": outcome["rawResult"],
            "privilegedTrace": outcome["privilegedTrace"],
        })

    integration_failure = (
        bool(unity.get("infrastructureFailure"))
        or len(episodes) != 3
        or any(not item["freshnessPass"] for item in episodes)
    )
    positive = [item for item in episodes if "POSITIVE" in item["episode"]]
    negative = [item for item in episodes if "HARDNEG" in item["episode"]]
    unsafe = any(item["contactViolations"] or item["workspaceViolations"] for item in episodes)
    positive_success = any(item["correctStop"] and item["returned"] and item["freshConfirm"] for item in positive)
    negative_safe = bool(negative) and all(not item["falseStop"] and not item["stop"] for item in negative)
    if integration_failure:
        classification = "INTEGRATION_FAIL"
    elif positive_success and negative_safe and not unsafe:
        classification = "ENGINEERING_POC_WORKS"
    elif any(item["recognized"] and not item["correctStop"] for item in positive):
        classification = "NAVIGATION_OR_CONFIRMATION_FAIL"
    else:
        classification = "RECOGNITION_NOT_SEPARABLE"

    summary = {
        "schemaVersion": 1,
        "classification": classification,
        "warning": "Fixed-anatomy consumed-development engineering demonstration only; no scientific or clinical validation.",
        "job": {"id": args.job_id, "exitStatus": "SUCCEEDED"},
        "executionCommit": unity.get("executionCommit"),
        "model": {
            "id": "phase2_fixed_anatomy_template_demo_v1",
            "threshold": unity.get("recognitionThreshold"),
            "checkpointSha256": unity.get("diagnosticStopCheckpointSha256"),
            "eligibility": "DIAGNOSTIC_ONLY_NON_DEPLOYABLE",
            "frozenSpatialV2Preserved": True,
            "frozenSpatialV2Threshold": unity.get("stopThreshold"),
        },
        "unity": {
            "version": unity.get("unityVersion"),
            "scene": unity.get("scene"),
            "infrastructureFailure": unity.get("infrastructureFailure", ""),
        },
        "episodes": episodes,
    }
    (output / "phase2_engineering_poc_summary.json").write_text(
        json.dumps(summary, indent=2), encoding="utf-8"
    )

    yn = lambda value: "Có" if value else "Không"
    rows = [
        "| Episode | Target | Recognized | Max score | Returned | Fresh confirm | Correct STOP | False STOP | Abstain | Violation | Failure |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|",
    ]
    for item in episodes:
        violation = item["contactViolations"] + item["workspaceViolations"]
        rows.append(
            f"| {item['episode']} | {yn(item['targetEncountered'])} | {yn(item['recognized'])} | "
            f"{item['maxRecognitionScore']:.6f} | {yn(item['returned'])} | {yn(item['freshConfirm'])} | "
            f"{yn(item['correctStop'])} | {yn(item['falseStop'])} | {yn(item['abstain'])} | "
            f"{violation} | {item['failureReason'] or '-'} |"
        )
    report = "\n".join([
        "# Phase 2 Fixed-Anatomy Engineering POC",
        "",
        f"Kết luận: `{classification}`.",
        "",
        *rows,
        "",
        f"Unity job `{args.job_id}`; execution commit `{unity.get('executionCommit')}`; "
        f"scene `{unity.get('scene')}`.",
        "",
        "Recognizer dùng duy nhất pixel ultrasound sạch và checkpoint diagnostic-only. Spatial-v2 cùng threshold 0.93 được giữ nguyên. Ground truth chỉ dùng để đo kết quả sau chạy.",
        "",
        "Đây là fixed-anatomy engineering demo trên các start development đã consumed; không phải scientific, clinical hay unseen-anatomy validation.",
        "",
    ])
    (output / "phase2_engineering_poc_report_vi.md").write_text(report, encoding="utf-8")


if __name__ == "__main__":
    main()
