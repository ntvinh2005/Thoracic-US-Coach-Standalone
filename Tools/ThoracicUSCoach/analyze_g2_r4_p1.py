from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def quaternion_angle(left: dict[str, float], right: dict[str, float]) -> float:
    dot = abs(sum(float(left[key]) * float(right[key]) for key in ("x", "y", "z", "w")))
    return math.degrees(2.0 * math.acos(min(1.0, max(-1.0, dot))))


def distance(left: dict[str, float], right: dict[str, float]) -> float:
    return math.sqrt(sum((float(left[key]) - float(right[key])) ** 2 for key in ("x", "y", "z")))


def quantiles(values: list[float]) -> dict[str, float | int | None]:
    if not values:
        return {"count": 0, "min": None, "q10": None, "median": None, "q90": None, "max": None}
    array = np.asarray(values, dtype=np.float64)
    return {
        "count": len(values), "min": float(array.min()), "q10": float(np.quantile(array, 0.10)),
        "median": float(np.median(array)), "q90": float(np.quantile(array, 0.90)), "max": float(array.max()),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Validate and summarize G2-R4 revised P1 capture")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    repo = args.repo.resolve()
    output = repo / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_revised_20261025"
    roots = [
        repo / "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_raw_20261025",
        repo / "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_20261025",
        repo / "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_v2_20261025",
    ]
    reports = [load(root / "g2_r4_p1_unity_report.json") for root in roots]
    all_cases = [case for report in reports for case in report["cases"]]
    integrity_errors: list[str] = []
    image_hashes: set[str] = set()
    family_ids: set[str] = set()
    case_summaries: list[dict[str, Any]] = []
    identical_pose_groups = mixed_label_pose_groups = 0
    mixed_label_groups: list[dict[str, Any]] = []
    action_counts: Counter[str] = Counter()
    achieved_translation: list[float] = []
    achieved_rotation: list[float] = []

    for root, report in zip(roots, reports):
        if report.get("sceneSha256") != reports[0].get("sceneSha256"):
            integrity_errors.append(f"scene hash mismatch: {root}")
        if report.get("evaluatorSha256") != reports[0].get("evaluatorSha256"):
            integrity_errors.append(f"evaluator hash mismatch: {root}")
        if not report.get("complete") or report.get("infrastructureFailure"):
            integrity_errors.append(f"incomplete Unity report: {root}")
        for case in report["cases"]:
            if case["familyId"] in family_ids:
                integrity_errors.append(f"duplicate family identity: {case['familyId']}")
            family_ids.add(case["familyId"])
            observations = list(case.get("observations", []))
            trajectory_actions = [item for item in case.get("actions", []) if item.get("stage") == "trajectory"]
            if case["captureStatus"] == "complete":
                if len(observations) != len(trajectory_actions) + 1:
                    integrity_errors.append(f"action/image alignment count: {case['caseId']}")
                case_directory = root / case["arm"] / case["caseId"]
                stream_path = case_directory / "stream_manifest.json"
                privileged_path = case_directory / "privileged_annotations.json"
                audit_path = case_directory / "case_audit.json"
                if not all(path.is_file() for path in (stream_path, privileged_path, audit_path)):
                    integrity_errors.append(f"case files missing: {case['caseId']}")
                    continue
                stream = load(stream_path)
                privileged = load(privileged_path)
                if len(stream.get("frames", [])) != len(observations) or len(privileged.get("frames", [])) != len(observations):
                    integrity_errors.append(f"stream/privileged count mismatch: {case['caseId']}")
                if stream.get("family") != case["familyId"] or stream.get("episodeId") != case["caseId"]:
                    integrity_errors.append(f"stream identity mismatch: {case['caseId']}")
            for index, observation in enumerate(observations):
                image = repo / str(observation["image"]).replace("\\", "/")
                if not image.is_file() or sha256(image) != observation["imageSha256"]:
                    integrity_errors.append(f"image/hash mismatch: {image}")
                    continue
                with Image.open(image) as opened:
                    if opened.size != (1024, 1024) or opened.mode not in {"RGB", "RGBA"}:
                        integrity_errors.append(f"clean image format mismatch: {image}")
                if observation["imageSha256"] in image_hashes:
                    integrity_errors.append(f"duplicate new image hash: {observation['imageSha256']}")
                image_hashes.add(observation["imageSha256"])
                expected = float(observation["score"]["total"]) >= float(report["acceptableScoreFloor"])
                if bool(observation["finalTargetAcceptable"]) != expected or not observation["evaluatorFloorAgreement"]:
                    integrity_errors.append(f"floor/label mismatch: {case['caseId']}/{index}")
                if not observation["contactValid"] or not observation["workspaceValid"]:
                    integrity_errors.append(f"unsafe observation: {case['caseId']}/{index}")
                if index == 0:
                    if observation["incomingAction"] != "RESET" or not observation["episodeReset"]:
                        integrity_errors.append(f"reset alignment mismatch: {case['caseId']}")
                elif index - 1 < len(trajectory_actions):
                    action = trajectory_actions[index - 1]
                    if observation["incomingAction"] != action["action"]:
                        integrity_errors.append(f"incoming action mismatch: {case['caseId']}/{index}")
                    if (
                        bool(observation["actionAccepted"]) != bool(action["accepted"])
                        or bool(observation["actionCompleted"]) != bool(action["completed"])
                    ):
                        integrity_errors.append(f"feedback mismatch: {case['caseId']}/{index}")
            for action in case.get("actions", []):
                action_counts[action["action"]] += 1
                if abs(float(action.get("requestedDisplacementMm", 0.0))) > 0:
                    achieved_translation.append(abs(float(action.get("achievedDisplacementMm", 0.0))))
                if abs(float(action.get("requestedRotationDeg", 0.0))) > 0:
                    achieved_rotation.append(abs(float(action.get("achievedRotationDeg", 0.0))))

            # Repeated renders at effectively identical poses expose label instability.
            remaining = set(range(len(observations)))
            while remaining:
                anchor_index = remaining.pop()
                anchor = observations[anchor_index]
                group = [anchor_index]
                for other_index in list(remaining):
                    other = observations[other_index]
                    if (distance(anchor["probePosition"], other["probePosition"]) <= 0.05
                            and quaternion_angle(anchor["probeRotation"], other["probeRotation"]) <= 0.05):
                        group.append(other_index)
                        remaining.remove(other_index)
                if len(group) >= 2:
                    identical_pose_groups += 1
                    labels = {bool(observations[item]["finalTargetAcceptable"]) for item in group}
                    mixed_label_pose_groups += int(len(labels) > 1)
                    if len(labels) > 1:
                        mixed_label_groups.append({
                            "caseId": case["caseId"],
                            "indices": group,
                            "scores": [float(observations[item]["score"]["total"]) for item in group],
                            "labels": [bool(observations[item]["finalTargetAcceptable"]) for item in group],
                        })

            margins = [float(item["marginToAcceptableFloor"]) for item in observations]
            positions = [item["probePosition"] for item in observations]
            rotations = [item["probeRotation"] for item in observations]
            case_summaries.append({
                "caseId": case["caseId"], "arm": case["arm"], "familyId": case["familyId"],
                "countsAsNewIndependentFamily": bool(case["countsAsNewIndependentFamily"]),
                "captureStatus": case["captureStatus"], "failureReason": case.get("failureReason", ""),
                "targetAcceptableAtStart": bool(case.get("targetAcceptableAtStart", False)),
                "observationCount": len(observations), "positiveCount": int(case["positiveObservations"]),
                "nearBoundaryPositiveCount": int(case["nearBoundaryPositiveObservations"]),
                "nearBoundaryNegativeCount": int(case["nearBoundaryNegativeObservations"]),
                "hardNegativeCount": int(case["hardNegativeObservations"]),
                "scoreMargin": quantiles(margins),
                "positionSpanMm": max((distance(left, right) for left in positions for right in positions), default=0.0),
                "rotationSpanDeg": max((quaternion_angle(left, right) for left in rotations for right in rotations), default=0.0),
                "caseAuditSha256": sha256(root / case["arm"] / case["caseId"] / "case_audit.json")
                if case["captureStatus"] == "complete" else None,
            })

    independent = [item for item in case_summaries if item["countsAsNewIndependentFamily"]]
    by_arm: dict[str, Any] = {}
    for arm in ("randomized_start", "target_enriched"):
        selected = [item for item in independent if item["arm"] == arm]
        by_arm[arm] = {
            "families": len(selected),
            "completedFamilies": sum(item["captureStatus"] == "complete" for item in selected),
            "familiesWithPositive": sum(item["positiveCount"] > 0 for item in selected),
            "targetObservationRate": sum(item["positiveCount"] > 0 for item in selected) / max(len(selected), 1),
            "observations": sum(item["observationCount"] for item in selected),
            "positiveObservations": sum(item["positiveCount"] for item in selected),
            "nearBoundaryPositiveObservations": sum(item["nearBoundaryPositiveCount"] for item in selected),
            "nearBoundaryNegativeObservations": sum(item["nearBoundaryNegativeCount"] for item in selected),
            "hardNegativeObservations": sum(item["hardNegativeCount"] for item in selected),
        }
    successful_hard = [item for item in case_summaries if item["arm"] == "hard_causal" and item["captureStatus"] == "complete"]
    failed = [item for item in case_summaries if item["captureStatus"] != "complete"]
    all_observations = [item for case in all_cases for item in case.get("observations", [])]
    all_margins = [float(item["marginToAcceptableFloor"]) for item in all_observations]
    # Locked bytes are deliberately not opened. Identity protection is checked
    # against the predeclared names embedded in every manifest.
    declared_locked = set(load(repo / "Tools/ThoracicUSCoach/experiments/g2_r4_p1_capture_v1.json")["historicallyLockedFamilies"])
    if family_ids & declared_locked:
        integrity_errors.append("historically locked family identity collision")

    engineering_pass = (
        not integrity_errors
        and len(independent) == 16
        and all(item["captureStatus"] == "complete" for item in independent)
        and sum(int(report["safetyFailures"]) for report in reports) == 0
        and sum(int(report["contactViolations"]) for report in reports) == 0
        and sum(int(report["workspaceViolations"]) for report in reports) == 0
    )
    positive_families = sum(item["positiveCount"] > 0 for item in independent)
    hard_observations = sum(item["observationCount"] for item in successful_hard)
    development_ready = engineering_pass and positive_families >= 10 and hard_observations >= 48
    summary = {
        "schemaVersion": 1,
        "taskId": "G2-R4-REVISED-P1",
        "decision": {
            "engineeringAndSafety": "PASS" if engineering_pass else "FAIL",
            "boundedP2DevelopmentStudy": "GO" if development_ready else "NO_GO",
            "independentConfirmationOrPromotion": "BLOCKED",
            "reason": "Pilot has enough exploratory positive-family and hard-negative support for bounded development only; every inspected family is consumed and no independent confirmation role exists."
            if development_ready else "Capture integrity, positive-family coverage, or causal hard-case support is insufficient.",
        },
        "rawEvidence": [
            {
                "root": str(root.relative_to(repo)).replace("\\", "/"),
                "unityReportSha256": sha256(root / "g2_r4_p1_unity_report.json"),
                "protocolId": report["protocolId"],
            }
            for root, report in zip(roots, reports)
        ],
        "identity": {
            "sceneSha256": reports[0]["sceneSha256"],
            "evaluatorSha256": reports[0]["evaluatorSha256"],
            "active2DControllerSha256": reports[0]["active2DControllerSha256"],
            "mainCaptureControllerSha256": reports[0]["captureControllerSha256"],
            "supplementCaptureControllerSha256": reports[-1]["captureControllerSha256"],
            "acceptableScoreFloor": reports[0]["acceptableScoreFloor"],
            "lockedFamiliesOpened": [],
        },
        "integrity": {
            "errors": integrity_errors,
            "validatedImageCount": len(image_hashes),
            "uniqueImageHashCount": len(image_hashes),
            "cleanImageSize": [1024, 1024],
            "newFamilyIdentityCount": len(independent),
            "familyCollisionWithLocked": sorted(family_ids & declared_locked),
            "acceptedActions": sum(int(report["acceptedActions"]) for report in reports),
            "rejectedActions": sum(int(report["rejectedActions"]) for report in reports),
            "incompleteActions": sum(int(report["incompleteActions"]) for report in reports),
            "safetyFailures": sum(int(report["safetyFailures"]) for report in reports),
            "contactViolations": sum(int(report["contactViolations"]) for report in reports),
            "workspaceViolations": sum(int(report["workspaceViolations"]) for report in reports),
        },
        "coverage": {
            "newIndependentFamilies": len(independent),
            "newFamiliesWithPositive": positive_families,
            "arms": by_arm,
            "allCapturedMarginToFloor": quantiles(all_margins),
            "identicalPoseRepeatGroups": identical_pose_groups,
            "mixedLabelIdenticalPoseGroups": mixed_label_pose_groups,
            "mixedLabelGroups": mixed_label_groups,
            "actionCounts": dict(sorted(action_counts.items())),
            "achievedTranslationMm": quantiles(achieved_translation),
            "achievedRotationDeg": quantiles(achieved_rotation),
        },
        "hardCausal": {
            "successfulFamilies": len(successful_hard),
            "successfulObservations": hard_observations,
            "successfulFamilyIds": [item["familyId"] for item in successful_hard],
            "positiveObservations": sum(item["positiveCount"] for item in successful_hard),
            "nearBoundaryPositiveObservations": sum(item["nearBoundaryPositiveCount"] for item in successful_hard),
            "nearBoundaryNegativeObservations": sum(item["nearBoundaryNegativeCount"] for item in successful_hard),
            "hardNegativeObservations": sum(item["hardNegativeCount"] for item in successful_hard),
        },
        "failedStarts": failed,
        "selectionBias": {
            "targetEnrichedIsRepresentativeRandomSuccess": False,
            "allPilotFamiliesConsumedAfterInspection": True,
            "notes": "Arm B intentionally uses privileged target-enriched planning. Hard cases are previously consumed diagnostics. Neither may inflate Arm A randomized observation success or future independent confirmation support.",
        },
        "families": case_summaries,
    }
    path = output / "g2_r4_p1_capture_summary.json"
    path.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(path), "decision": summary["decision"], "integrity": summary["integrity"],
        "coverage": summary["coverage"], "hardCausal": summary["hardCausal"],
        "failedStarts": summary["failedStarts"],
    }, indent=2))


if __name__ == "__main__":
    main()
