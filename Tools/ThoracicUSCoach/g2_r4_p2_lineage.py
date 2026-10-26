from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import defaultdict
from pathlib import Path
from typing import Any

from PIL import Image


RAW_ROOTS = (
    "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_raw_20261025",
    "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_20261025",
    "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_v2_20261025",
)
MANIFESTS = (
    "Tools/ThoracicUSCoach/experiments/g2_r4_p1_capture_v1.json",
    "Tools/ThoracicUSCoach/experiments/g2_r4_p1_hard_supplement_v1.json",
    "Tools/ThoracicUSCoach/experiments/g2_r4_p1_hard_supplement_v2.json",
)
PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_r4_p2_protocol_v1.json"
OUTPUT = "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2_20261025"


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def distance(left: dict[str, float], right: dict[str, float]) -> float:
    return math.sqrt(sum((float(left[key]) - float(right[key])) ** 2 for key in ("x", "y", "z")))


def quaternion_angle(left: dict[str, float], right: dict[str, float]) -> float:
    dot = abs(sum(float(left[key]) * float(right[key]) for key in ("x", "y", "z", "w")))
    return math.degrees(2.0 * math.acos(min(1.0, max(-1.0, dot))))


def normalized_action(frame: dict[str, Any]) -> str:
    if bool(frame.get("episodeReset", False)):
        return "RESET"
    if not bool(frame.get("actionAccepted", False)) or not bool(frame.get("actionCompleted", False)):
        return "RESET"
    return str(frame.get("issuedAction", "RESET")).replace("_", "").upper()


def coverage_overlap(
    left: list[dict[str, Any]], right: list[dict[str, Any]], *, position_mm: float, angle_deg: float
) -> tuple[float, float]:
    if not left or not right:
        return 0.0, 0.0

    def fraction(source: list[dict[str, Any]], target: list[dict[str, Any]]) -> float:
        matched = 0
        for item in source:
            if any(
                distance(item["probePosition"], other["probePosition"]) <= position_mm
                and quaternion_angle(item["probeRotation"], other["probeRotation"]) <= angle_deg
                for other in target
            ):
                matched += 1
        return matched / len(source)

    return fraction(left, right), fraction(right, left)


def collect(repo: Path, protocol: dict[str, Any]) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[str]]:
    manifest_cases: dict[str, dict[str, Any]] = {}
    for relative in MANIFESTS:
        manifest = read_json(repo / relative)
        for case in manifest["cases"]:
            case = dict(case)
            case["sourceManifest"] = relative
            manifest_cases[case["caseId"]] = case

    cases: list[dict[str, Any]] = []
    records: list[dict[str, Any]] = []
    errors: list[str] = []
    hashes: set[str] = set()
    locked = set(protocol["historicalLockedFamilies"])
    floor = float(protocol["acceptableScoreFloor"])
    for root_relative in RAW_ROOTS:
        root = repo / root_relative
        report_path = root / "g2_r4_p1_unity_report.json"
        if not report_path.is_file():
            errors.append(f"missing raw report: {root_relative}")
            continue
        report = read_json(report_path)
        for audit in report.get("cases", []):
            case_id = str(audit["caseId"])
            declared = manifest_cases.get(case_id)
            if declared is None:
                errors.append(f"undeclared case: {case_id}")
                continue
            source = str(declared["baseStart"]["sourceFamily"])
            if source in locked or str(audit["familyId"]) in locked:
                errors.append(f"locked-family collision: {case_id}")
            case_dir = root / str(audit["arm"]) / case_id
            if audit.get("captureStatus") != "complete":
                cases.append({
                    "caseId": case_id, "captureStatus": audit.get("captureStatus"),
                    "failureReason": audit.get("failureReason", ""), "sourceBaseGroup": source,
                    "countsAsIndependentP1": bool(declared["countsAsNewIndependentFamily"]),
                })
                continue
            stream = read_json(case_dir / "stream_manifest.json")
            privileged = read_json(case_dir / "privileged_annotations.json")
            observations = list(audit.get("observations", []))
            frames = list(stream.get("frames", []))
            labels = list(privileged.get("frames", []))
            if not (len(observations) == len(frames) == len(labels)):
                errors.append(f"row count mismatch: {case_id}")
                continue
            case_records: list[dict[str, Any]] = []
            for index, (observation, frame, label) in enumerate(zip(observations, frames, labels)):
                image = repo / str(frame["image"]).replace("\\", "/")
                actual_hash = sha256(image) if image.is_file() else "MISSING"
                if actual_hash != frame.get("imageSha256") or actual_hash != observation.get("imageSha256"):
                    errors.append(f"image hash mismatch: {case_id}/{index}")
                if actual_hash in hashes:
                    errors.append(f"duplicate image hash: {case_id}/{index}")
                hashes.add(actual_hash)
                if image.is_file():
                    with Image.open(image) as opened:
                        if opened.size != (1024, 1024):
                            errors.append(f"native size mismatch: {case_id}/{index}")
                score = float(observation["score"]["total"])
                target = bool(observation["finalTargetAcceptable"])
                if target != (score >= floor) or target != bool(label["finalTargetAcceptable"]):
                    errors.append(f"label/score mismatch: {case_id}/{index}")
                if not bool(observation["contactValid"]) or not bool(observation["workspaceValid"]):
                    errors.append(f"safety invalid: {case_id}/{index}")
                actor_action = normalized_action(frame)
                if index == 0 and (actor_action != "RESET" or not bool(frame.get("episodeReset"))):
                    errors.append(f"reset boundary mismatch: {case_id}")
                if str(observation.get("incomingAction", "")).replace("_", "").upper() != str(frame.get("issuedAction", "")).replace("_", "").upper():
                    errors.append(f"action alignment mismatch: {case_id}/{index}")
                row = {
                    "caseId": case_id,
                    "trajectoryFamily": audit["familyId"],
                    "sourceBaseGroup": source,
                    "arm": audit["arm"],
                    "countsAsIndependentP1": bool(declared["countsAsNewIndependentFamily"]),
                    "index": index,
                    "image": str(frame["image"]).replace("\\", "/"),
                    "imageSha256": actual_hash,
                    "target": target,
                    "score": score,
                    "margin": score - floor,
                    "issuedAction": str(frame.get("issuedAction", "RESET")).replace("_", "").upper(),
                    "executedAction": actor_action,
                    "actionAccepted": bool(frame.get("actionAccepted")),
                    "actionCompleted": bool(frame.get("actionCompleted")),
                    "episodeReset": bool(frame.get("episodeReset")),
                    "probePosition": observation["probePosition"],
                    "probeRotation": observation["probeRotation"],
                    "contactValid": bool(observation["contactValid"]),
                    "workspaceValid": bool(observation["workspaceValid"]),
                }
                records.append(row)
                case_records.append(row)
            cases.append({
                "caseId": case_id,
                "trajectoryFamily": audit["familyId"],
                "sourceBaseGroup": source,
                "sourceEpisode": declared["baseStart"].get("sourceEpisode"),
                "sourceStartId": declared["baseStart"].get("startId"),
                "captureSeed": int(declared["captureSeed"]),
                "arm": audit["arm"],
                "countsAsIndependentP1": bool(declared["countsAsNewIndependentFamily"]),
                "inheritedStartingGeometry": True,
                "sourcePose": {
                    "position": declared["baseStart"]["probePosition"],
                    "rotation": declared["baseStart"]["probeRotation"],
                },
                "prepositionActions": declared.get("prepositionActions", []),
                "trajectoryActions": declared.get("trajectoryActions", []),
                "observationCount": len(case_records),
                "positiveCount": sum(item["target"] for item in case_records),
                "scoreRange": [min((item["score"] for item in case_records), default=None), max((item["score"] for item in case_records), default=None)],
                "previousUsage": "consumed P1 development/diagnostic evidence; never confirmation",
                "sourceManifest": declared["sourceManifest"],
                "rawRoot": root_relative,
                "captureStatus": "complete",
            })
    return cases, records, errors


def build(repo: Path) -> dict[str, Any]:
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    cases, records, errors = collect(repo, protocol)
    criteria = protocol["lineage"]
    complete = [case for case in cases if case.get("captureStatus") == "complete"]
    by_case = {case["caseId"]: [row for row in records if row["caseId"] == case["caseId"]] for case in complete}
    near_start: list[dict[str, Any]] = []
    overlap: list[dict[str, Any]] = []
    for left_index, left in enumerate(complete):
        for right in complete[left_index + 1:]:
            position = distance(left["sourcePose"]["position"], right["sourcePose"]["position"])
            angle = quaternion_angle(left["sourcePose"]["rotation"], right["sourcePose"]["rotation"])
            same_source = left["sourceBaseGroup"] == right["sourceBaseGroup"]
            near = position <= float(criteria["nearDuplicateStart"]["maximumPositionDistanceMm"]) and angle <= float(criteria["nearDuplicateStart"]["maximumQuaternionAngleDeg"])
            fractions = coverage_overlap(
                by_case[left["caseId"]], by_case[right["caseId"]],
                position_mm=float(criteria["trajectoryOverlap"]["poseMatchPositionMm"]),
                angle_deg=float(criteria["trajectoryOverlap"]["poseMatchAngleDeg"]),
            )
            spatial_overlap = min(fractions) >= float(criteria["trajectoryOverlap"]["minimumBidirectionalCoverageFraction"])
            if same_source or near:
                near_start.append({
                    "left": left["caseId"], "right": right["caseId"],
                    "sameSourceBase": same_source, "positionDistanceMm": position,
                    "rotationDistanceDeg": angle, "nearDuplicate": near,
                })
            if spatial_overlap:
                overlap.append({
                    "left": left["caseId"], "right": right["caseId"],
                    "leftCoverageFraction": fractions[0], "rightCoverageFraction": fractions[1],
                    "sameSourceBase": same_source,
                })

    primary_cases = [case for case in complete if case["countsAsIndependentP1"]]
    source_groups: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for case in primary_cases:
        source_groups[case["sourceBaseGroup"]].append(case)
    source_positive = sorted(group for group, members in source_groups.items() if any(item["positiveCount"] > 0 for item in members))
    source_negative = sorted(set(source_groups) - set(source_positive))
    if len(source_positive) != 6 or len(source_negative) != 6:
        errors.append(f"expected six positive and six negative independent source groups, got {len(source_positive)}/{len(source_negative)}")

    # The scientific split unit is the connected source-base/spatial component,
    # not the human-readable trajectory ID. Near starts are disclosed above but
    # only captured-trajectory overlap is strong enough to merge distinct bases.
    parent = {group: group for group in source_groups}

    def find(group: str) -> str:
        while parent[group] != group:
            parent[group] = parent[parent[group]]
            group = parent[group]
        return group

    def union(left: str, right: str) -> None:
        left_root, right_root = find(left), find(right)
        if left_root != right_root:
            parent[right_root] = left_root

    case_lookup = {case["caseId"]: case for case in primary_cases}
    for item in overlap:
        if item["left"] in case_lookup and item["right"] in case_lookup:
            union(case_lookup[item["left"]]["sourceBaseGroup"], case_lookup[item["right"]]["sourceBaseGroup"])
    component_sources: dict[str, list[str]] = defaultdict(list)
    for source in sorted(source_groups):
        component_sources[find(source)].append(source)
    sorted_components = sorted(component_sources.values(), key=lambda values: tuple(values))
    source_to_spatial: dict[str, str] = {}
    spatial_groups: dict[str, list[dict[str, Any]]] = {}
    for index, sources in enumerate(sorted_components):
        group_id = f"spatial_group_{index:02d}"
        members = [case for source in sources for case in source_groups[source]]
        spatial_groups[group_id] = members
        for source in sources:
            source_to_spatial[source] = group_id
    for case in complete:
        case["spatialGroup"] = source_to_spatial.get(case["sourceBaseGroup"], "auxiliary_only_" + case["sourceBaseGroup"])
    positive_groups = sorted(group for group, members in spatial_groups.items() if any(item["positiveCount"] > 0 for item in members))
    negative_groups = sorted(set(spatial_groups) - set(positive_groups))
    folds = []
    for fold in range(3):
        evaluation = positive_groups[fold::3] + negative_groups[fold::3]
        training = sorted(set(spatial_groups) - set(evaluation))
        folds.append({
            "fold": fold,
            "evaluationSpatialGroups": evaluation,
            "trainingSpatialGroups": training,
            "evaluationPositiveGroups": [item for item in evaluation if item in positive_groups],
            "evaluationNegativeGroups": [item for item in evaluation if item in negative_groups],
            "auxiliaryHardGroupsAllowedOnlyWhenNotEvaluation": sorted({case["sourceBaseGroup"] for case in complete if not case["countsAsIndependentP1"]}),
        })

    trajectory_count = len(primary_cases)
    frame_count = sum(case["observationCount"] for case in primary_cases)
    graph = {
        "schemaVersion": 1,
        "taskId": "G2-R4-P2-A",
        "protocol": PROTOCOL,
        "protocolSha256": sha256(protocol_path),
        "lineageCriteria": criteria,
        "integrity": {
            "status": "PASS" if not errors else "FAIL",
            "errors": errors,
            "allP1FramesValidated": len(records),
            "uniqueImageHashes": len({row["imageSha256"] for row in records}),
            "rejectedOrIncompleteActorActionsNormalizedToReset": sum(row["executedAction"] == "RESET" and not row["episodeReset"] for row in records),
            "unsafeFrames": sum(not row["contactValid"] or not row["workspaceValid"] for row in records),
        },
        "independence": {
            "frameLevel": frame_count,
            "trajectoryLevel": trajectory_count,
            "sourceBaseLevel": len(source_groups),
            "sourceBasePositiveGroups": len(source_positive),
            "sourceBaseNegativeGroups": len(source_negative),
            "sourceBaseSpatialGroupLevel": len(spatial_groups),
            "positiveSourceBaseSpatialGroups": len(positive_groups),
            "negativeSourceBaseSpatialGroups": len(negative_groups),
            "allP1IncludingAuxiliaryFrames": len(records),
            "allCompleteTrajectoriesIncludingAuxiliary": len(complete),
            "warning": "Frames, trajectories and source-base groups are not interchangeable. The fixed Unity anatomy does not support patient/anatomy generalization claims.",
        },
        "positiveSourceBaseGroups": source_positive,
        "negativeSourceBaseGroups": source_negative,
        "positiveSpatialGroups": positive_groups,
        "negativeSpatialGroups": negative_groups,
        "spatialGroupSources": {group: sorted({item["sourceBaseGroup"] for item in members}) for group, members in spatial_groups.items()},
        "cases": complete,
        "nearDuplicateStarts": near_start,
        "trajectorySpatialOverlaps": overlap,
        "developmentFolds": folds,
        "historicalLockedFamiliesOpened": [],
    }
    output = repo / OUTPUT
    write_json(output / "p2_a_lineage_graph.json", graph)
    split = {
        "schemaVersion": 1,
        "taskId": "G2-R4-P2-A3",
        "protocolSha256": graph["protocolSha256"],
        "primaryCases": [case["caseId"] for case in primary_cases],
        "auxiliaryCases": [case["caseId"] for case in complete if not case["countsAsIndependentP1"]],
        "positiveSourceBaseGroups": source_positive,
        "negativeSourceBaseGroups": source_negative,
        "positiveSpatialGroups": positive_groups,
        "negativeSpatialGroups": negative_groups,
        "spatialGroupSources": graph["spatialGroupSources"],
        "folds": folds,
        "calibration": protocol["development"]["calibration"],
        "scientificLimitation": "Six positive source-base groups collapse to five source-base/spatial groups because 0052 and 0058 trajectories overlap. Repeated frames do not increase independent support.",
    }
    write_json(output / "p2_a_development_split.json", split)
    manifest = {
        "schemaVersion": 1,
        "taskId": "G2-R4-P2-A2",
        "rawRoots": [
            {"path": relative, "reportSha256": sha256(repo / relative / "g2_r4_p1_unity_report.json")}
            for relative in RAW_ROOTS
        ],
        "validatedFrameCount": len(records),
        "imageHashMerkleEquivalent": hashlib.sha256("".join(sorted(row["imageSha256"] for row in records)).encode()).hexdigest(),
        "errors": errors,
        "status": "PASS" if not errors else "FAIL",
    }
    write_json(output / "p2_a_integrity_manifest.json", manifest)
    return graph


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    result = build(args.repo.resolve())
    print(json.dumps({"integrity": result["integrity"], "independence": result["independence"], "folds": result["developmentFolds"]}, indent=2))
    if result["integrity"]["status"] != "PASS":
        raise SystemExit(2)


if __name__ == "__main__":
    main()
