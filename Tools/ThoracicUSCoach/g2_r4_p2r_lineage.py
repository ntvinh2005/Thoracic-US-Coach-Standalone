from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path
from typing import Any

from g2_r4_p2_lineage import (
    PROTOCOL as HISTORICAL_PROTOCOL,
    collect,
    coverage_overlap,
    distance,
    quaternion_angle,
    read_json,
    sha256,
    write_json,
)


PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_r4_p2r_protocol_v1.json"
OUTPUT = "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2r_20261025"


class UnionFind:
    def __init__(self, values: list[str]) -> None:
        self.parent = {value: value for value in values}

    def find(self, value: str) -> str:
        while self.parent[value] != value:
            self.parent[value] = self.parent[self.parent[value]]
            value = self.parent[value]
        return value

    def union(self, left: str, right: str) -> None:
        left_root, right_root = self.find(left), self.find(right)
        if left_root != right_root:
            self.parent[right_root] = left_root

    def components(self) -> list[list[str]]:
        grouped: dict[str, list[str]] = defaultdict(list)
        for value in sorted(self.parent):
            grouped[self.find(value)].append(value)
        return sorted((sorted(values) for values in grouped.values()), key=lambda values: tuple(values))


def connected_components(case_ids: list[str], edges: list[dict[str, Any]], include_near: bool) -> list[list[str]]:
    union = UnionFind(case_ids)
    for edge in edges:
        if edge["sharedSourceBase"] or edge["strongTrajectoryOverlap"] or (include_near and edge["nearDuplicateStart"]):
            union.union(edge["left"], edge["right"])
    return union.components()


def component_rows(
    components: list[list[str]], case_lookup: dict[str, dict[str, Any]], records_by_case: dict[str, list[dict[str, Any]]], prefix: str
) -> tuple[list[dict[str, Any]], dict[str, str]]:
    rows: list[dict[str, Any]] = []
    mapping: dict[str, str] = {}
    for index, members in enumerate(components):
        identifier = f"{prefix}_{index:02d}"
        for member in members:
            mapping[member] = identifier
        primary = [case_lookup[member] for member in members if case_lookup[member]["countsAsIndependentP1"]]
        auxiliary = [case_lookup[member] for member in members if not case_lookup[member]["countsAsIndependentP1"]]
        primary_records = [row for member in members if case_lookup[member]["countsAsIndependentP1"] for row in records_by_case[member]]
        rows.append({
            "groupId": identifier,
            "trajectoryIds": members,
            "sourceBaseAncestors": sorted({case_lookup[member]["sourceBaseGroup"] for member in members}),
            "sourceEpisodes": sorted({str(case_lookup[member]["sourceEpisode"]) for member in members}),
            "primaryTrajectoryIds": [case["caseId"] for case in primary],
            "auxiliaryTrajectoryIds": [case["caseId"] for case in auxiliary],
            "primaryFrames": len(primary_records),
            "primaryPositiveFrames": sum(bool(row["target"]) for row in primary_records),
            "containsPositivePrimary": any(bool(row["target"]) for row in primary_records),
            "countsAsEvaluationGroup": bool(primary),
        })
    return rows, mapping


def build(repo: Path) -> dict[str, Any]:
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    historical_protocol = read_json(repo / HISTORICAL_PROTOCOL)
    cases, records, errors = collect(repo, historical_protocol)
    complete = [case for case in cases if case.get("captureStatus") == "complete"]
    case_lookup = {case["caseId"]: case for case in complete}
    records_by_case: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in records:
        records_by_case[row["caseId"]].append(row)
    primary_policy = protocol["lineagePolicies"]["primary"]
    conservative = protocol["lineagePolicies"]["conservativeSensitivity"]
    exact = protocol["lineagePolicies"]["exactPose"]

    edges: list[dict[str, Any]] = []
    for left_index, left in enumerate(complete):
        for right in complete[left_index + 1:]:
            start_distance = distance(left["sourcePose"]["position"], right["sourcePose"]["position"])
            start_angle = quaternion_angle(left["sourcePose"]["rotation"], right["sourcePose"]["rotation"])
            overlap_left, overlap_right = coverage_overlap(
                records_by_case[left["caseId"]], records_by_case[right["caseId"]],
                position_mm=float(primary_policy["poseMatchPositionMm"]),
                angle_deg=float(primary_policy["poseMatchAngleDeg"]),
            )
            exact_left, exact_right = coverage_overlap(
                records_by_case[left["caseId"]], records_by_case[right["caseId"]],
                position_mm=float(exact["positionMm"]), angle_deg=float(exact["angleDeg"]),
            )
            edge = {
                "left": left["caseId"], "right": right["caseId"],
                "leftSourceBase": left["sourceBaseGroup"], "rightSourceBase": right["sourceBaseGroup"],
                "sharedSourceBase": left["sourceBaseGroup"] == right["sourceBaseGroup"],
                "startPositionDistanceMm": start_distance, "startRotationDistanceDeg": start_angle,
                "nearDuplicateStart": start_distance <= float(conservative["nearStartPositionMm"]) and start_angle <= float(conservative["nearStartAngleDeg"]),
                "trajectoryOverlapLeft": overlap_left, "trajectoryOverlapRight": overlap_right,
                "strongTrajectoryOverlap": min(overlap_left, overlap_right) >= float(primary_policy["minimumBidirectionalCoverageFraction"]),
                "exactPoseOverlapLeft": exact_left, "exactPoseOverlapRight": exact_right,
                "hasCrossTrajectoryExactPose": exact_left > 0.0 and exact_right > 0.0,
            }
            if edge["sharedSourceBase"] or edge["nearDuplicateStart"] or edge["strongTrajectoryOverlap"] or edge["hasCrossTrajectoryExactPose"]:
                edges.append(edge)

    case_ids = sorted(case_lookup)
    primary_components = connected_components(case_ids, edges, include_near=False)
    conservative_components = connected_components(case_ids, edges, include_near=True)
    primary_rows, primary_mapping = component_rows(primary_components, case_lookup, records_by_case, "primary_spatial")
    conservative_rows, conservative_mapping = component_rows(conservative_components, case_lookup, records_by_case, "conservative_spatial")

    evaluation_primary = [row for row in primary_rows if row["countsAsEvaluationGroup"]]
    positive = sorted(row["groupId"] for row in evaluation_primary if row["containsPositivePrimary"])
    negative = sorted(row["groupId"] for row in evaluation_primary if not row["containsPositivePrimary"])
    evaluation_conservative = [row for row in conservative_rows if row["countsAsEvaluationGroup"]]
    conservative_positive = sorted(row["groupId"] for row in evaluation_conservative if row["containsPositivePrimary"])
    conservative_negative = sorted(row["groupId"] for row in evaluation_conservative if not row["containsPositivePrimary"])

    folds: list[dict[str, Any]] = []
    for fold in range(3):
        evaluation = positive[fold::3] + negative[fold::3]
        training = sorted(set(positive + negative) - set(evaluation))
        training_positive = [group for group in training if group in positive]
        training_negative = [group for group in training if group in negative]
        inner_validation = [training_positive[fold % len(training_positive)], training_negative[fold % len(training_negative)]]
        fit_groups = sorted(set(training) - set(inner_validation))
        connected_auxiliary_excluded = sorted({
            case_id for case_id, group in primary_mapping.items()
            if group in evaluation and not case_lookup[case_id]["countsAsIndependentP1"]
        })
        valid = bool(
            any(group in positive for group in evaluation)
            and any(group in negative for group in evaluation)
            and len(training_positive) >= 3
            and len(training_negative) >= 2
            and sum(group in positive for group in fit_groups) >= 2
            and any(group in negative for group in fit_groups)
        )
        folds.append({
            "fold": fold,
            "evaluationGroups": evaluation,
            "outerTrainingGroups": training,
            "innerValidationGroups": inner_validation,
            "fitGroups": fit_groups,
            "evaluationPositiveGroups": [group for group in evaluation if group in positive],
            "evaluationNegativeGroups": [group for group in evaluation if group in negative],
            "fitPositiveGroups": [group for group in fit_groups if group in positive],
            "fitNegativeGroups": [group for group in fit_groups if group in negative],
            "connectedAuxiliaryExcludedFromTraining": connected_auxiliary_excluded,
            "valid": valid,
        })

    hard_edge = next((edge for edge in edges if {edge["left"], edge["right"]} == {"r4p1_hard_00", "r4p1_hard_01"}), None)
    hard_0090_component = primary_mapping.get("r4p1_hard_00")
    primary_0066_components = {
        primary_mapping[case["caseId"]] for case in complete
        if case["sourceBaseGroup"] == "train_family_0066"
    }
    hard_connection_confirmed = bool(hard_edge and hard_edge["strongTrajectoryOverlap"] and hard_0090_component in primary_0066_components)
    if not hard_connection_confirmed:
        errors.append("hard_0090/hard_0066 transitive dependency was not connected")

    primary_sufficient = len(positive) >= 3 and len(negative) >= 3 and all(fold["valid"] for fold in folds)
    conservative_sufficient = len(conservative_positive) >= 3 and len(conservative_negative) >= 3
    result = {
        "schemaVersion": 1,
        "taskId": "G2-R4-P2-R1",
        "sourceCommit": protocol["sourceCommit"],
        "protocol": PROTOCOL,
        "protocolSha256": sha256(protocol_path),
        "integrity": {"status": "PASS" if not errors else "FAIL", "errors": errors},
        "policies": protocol["lineagePolicies"],
        "allTrajectoryCount": len(complete),
        "primaryTrajectoryCount": sum(case["countsAsIndependentP1"] for case in complete),
        "auxiliaryTrajectoryCount": sum(not case["countsAsIndependentP1"] for case in complete),
        "edges": edges,
        "primaryPolicy": {
            "components": primary_rows,
            "caseToComponent": primary_mapping,
            "evaluationGroups": len(evaluation_primary),
            "positiveEvaluationGroups": len(positive),
            "negativeEvaluationGroups": len(negative),
            "folds": folds,
            "dataSufficiency": "PASS" if primary_sufficient else "UNDERPOWERED_FOR_GROUP_GENERALIZATION",
        },
        "conservativeSensitivity": {
            "components": conservative_rows,
            "caseToComponent": conservative_mapping,
            "evaluationGroups": len(evaluation_conservative),
            "positiveEvaluationGroups": len(conservative_positive),
            "negativeEvaluationGroups": len(conservative_negative),
            "dataSufficiency": "PASS" if conservative_sufficient else "UNDERPOWERED_FOR_GROUP_GENERALIZATION",
        },
        "hard0090Hard0066": {
            "confirmed": hard_connection_confirmed,
            "edge": hard_edge,
            "correctedComponent": hard_0090_component,
            "primary0066Components": sorted(primary_0066_components),
            "effect": "hard_0090 must be excluded from fit whenever the connected primary 0066 component is held out",
        },
        "historicalLockedFamiliesOpened": [],
    }
    output = repo / OUTPUT
    write_json(output / "r1_complete_lineage_graph.json", result)
    write_json(output / "r1_grouping_sensitivity.json", {
        "schemaVersion": 1,
        "taskId": "G2-R4-P2-R1-SENSITIVITY",
        "primaryPolicy": {key: result["primaryPolicy"][key] for key in ("evaluationGroups", "positiveEvaluationGroups", "negativeEvaluationGroups", "dataSufficiency")},
        "conservativeSensitivity": {key: result["conservativeSensitivity"][key] for key in ("evaluationGroups", "positiveEvaluationGroups", "negativeEvaluationGroups", "dataSufficiency")},
        "interpretation": "Near-start proximity is a dependence warning, not proof of identical ultrasound information. Under the conservative union it removes negative-group independence and makes three-fold group-generalization evaluation impossible.",
    })
    write_json(output / "r1_corrected_split.json", {
        "schemaVersion": 1,
        "taskId": "G2-R4-P2-R1-SPLIT",
        "policy": "primary",
        "caseToComponent": primary_mapping,
        "positiveGroups": positive,
        "negativeGroups": negative,
        "folds": folds,
        "entryGate": "PASS" if primary_sufficient and not errors else "BLOCKED",
    })
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    result = build(args.repo.resolve())
    summary = {
        "integrity": result["integrity"],
        "primaryPolicy": {key: result["primaryPolicy"][key] for key in ("evaluationGroups", "positiveEvaluationGroups", "negativeEvaluationGroups", "dataSufficiency")},
        "conservativeSensitivity": {key: result["conservativeSensitivity"][key] for key in ("evaluationGroups", "positiveEvaluationGroups", "negativeEvaluationGroups", "dataSufficiency")},
        "hard0090Hard0066": result["hard0090Hard0066"],
    }
    import json
    print(json.dumps(summary, indent=2))
    if result["integrity"]["status"] != "PASS":
        raise SystemExit(2)


if __name__ == "__main__":
    main()
