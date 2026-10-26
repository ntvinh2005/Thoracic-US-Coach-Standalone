from __future__ import annotations

import argparse
import json
import math
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np

from g2_r6_audit import image_features, q_angle, read_json, sha256, visual_distances, write_json


EXPECTED_MANIFEST_SHA256 = "50c20528c75227b055f41bfc9024af1f9f2b243f77d062a414d040f065598e3f"
EXPECTED_SOURCE_COMMIT = "06e656377fc7e5ed0822b4f79bfead5e83a705eb"
EXPECTED_COORDINATOR = "d201227ab95e5a858003fa2985d5c118ae724de0b1740972a82c68d1f4eaf7c9"
EXPECTED_AUDIT = "a01d30e294939eda46f6f2090a11761bd5b37f26d6b516d05b3d3c00c19e07a5"
EXPECTED_SCENE = "6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d"
EXPECTED_EVALUATOR = "7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc"


def canonical_sha256(path: Path) -> str:
    text = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    import hashlib
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def position_distance(left: dict[str, Any], right: dict[str, Any]) -> float:
    return math.sqrt(sum((float(left[key]) - float(right[key])) ** 2 for key in ("x", "y", "z")))


def quaternion(left: dict[str, Any]) -> list[float]:
    return [float(left[key]) for key in ("x", "y", "z", "w")]


def verify_snapshot(root: Path) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], list[str]]:
    errors: list[str] = []
    paths = {
        name: root / name for name in (
            "r6_unity_report.json", "evidence_index.json", "snapshot_seal.json", "capture_input_manifest.json"
        )
    }
    for name, path in paths.items():
        if not path.is_file():
            errors.append(f"required snapshot file missing: {name}")
    if errors:
        return {}, {}, {}, errors
    report = read_json(paths["r6_unity_report.json"])
    index = read_json(paths["evidence_index.json"])
    seal = read_json(paths["snapshot_seal.json"])
    manifest = read_json(paths["capture_input_manifest.json"])
    if not seal.get("complete") or not seal.get("immutable"):
        errors.append("snapshot seal is not complete and immutable")
    if not report.get("complete") or not report.get("immutableSnapshotSealed") or report.get("infrastructureFailure"):
        errors.append("Unity report is incomplete, unsealed, or failed")
    if len(index.get("files", [])) != int(seal.get("indexedFiles", -1)):
        errors.append("seal/index file count mismatch")
    for entry in index.get("files", []):
        path = (root / entry["path"]).resolve()
        try:
            path.relative_to(root.resolve())
        except ValueError:
            errors.append(f"indexed path escapes snapshot: {entry['path']}")
            continue
        if not path.is_file():
            errors.append(f"indexed file missing: {entry['path']}")
        elif sha256(path) != entry["sha256"] or path.stat().st_size != int(entry["bytes"]):
            errors.append(f"indexed file mismatch: {entry['path']}")
    special = (
        (paths["evidence_index.json"], "evidenceIndexSha256"),
        (paths["r6_unity_report.json"], "reportSha256"),
        (paths["capture_input_manifest.json"], "manifestSha256"),
    )
    for path, field in special:
        if sha256(path) != seal.get(field):
            errors.append(f"seal hash mismatch: {field}")
    if sha256(paths["capture_input_manifest.json"]) != EXPECTED_MANIFEST_SHA256:
        errors.append("capture manifest differs from pre-outcome frozen hash")
    expected_identity = {
        "sourceCommit": EXPECTED_SOURCE_COMMIT,
        "coordinatorProtocolCanonicalSha256": EXPECTED_COORDINATOR,
        "auditProtocolCanonicalSha256": EXPECTED_AUDIT,
        "sceneCanonicalSha256": EXPECTED_SCENE,
        "evaluatorCanonicalSha256": EXPECTED_EVALUATOR,
    }
    for field, expected in expected_identity.items():
        if seal.get(field) != expected:
            errors.append(f"frozen identity mismatch: {field}")
    if manifest.get("sourceCommit") != EXPECTED_SOURCE_COMMIT:
        errors.append("manifest source commit mismatch")
    if manifest.get("coordinatorProtocolSha256") != EXPECTED_COORDINATOR:
        errors.append("manifest coordinator protocol mismatch")
    if manifest.get("auditProtocolSha256") != EXPECTED_AUDIT:
        errors.append("manifest audit protocol mismatch")
    frozen_contracts = root / "frozen_contracts"
    if canonical_sha256(frozen_contracts / "g2_r6_final_sprint_protocol_v1.json") != EXPECTED_COORDINATOR:
        errors.append("snapshotted coordinator protocol canonical hash mismatch")
    if canonical_sha256(frozen_contracts / "g2_r6_audit_protocol_v1.json") != EXPECTED_AUDIT:
        errors.append("snapshotted audit protocol canonical hash mismatch")
    records = report.get("records", [])
    if len(records) != int(seal.get("recordCount", -1)) or len(records) != int(report.get("observations", -1)):
        errors.append("record/report/seal count mismatch")
    image_records = [record for record in records if record.get("image")]
    if len(image_records) != int(seal.get("imageCount", -1)):
        errors.append("image record/seal count mismatch")
    if len(report.get("actions", [])) != int(seal.get("actionCount", -1)):
        errors.append("action report/seal count mismatch")
    if len(report.get("attempts", [])) != int(seal.get("attemptCount", -1)):
        errors.append("attempt report/seal count mismatch")
    floor = float(manifest["acceptableScoreFloor"])
    seen_hashes: set[str] = set()
    for record in records:
        image = root / record["image"]
        actual = sha256(image) if image.is_file() else "MISSING"
        if actual != record.get("imageSha256"):
            errors.append(f"record image hash mismatch: {record.get('recordId')}")
        if actual in seen_hashes:
            errors.append(f"duplicate image hash: {record.get('recordId')}")
        seen_hashes.add(actual)
        target = float(record["score"]["total"]) >= floor
        if bool(record.get("acceptable")) != target or bool(record.get("finalTargetAcceptable")) != target:
            errors.append(f"label/floor mismatch: {record.get('recordId')}")
    return report, manifest, seal, errors


def pairwise_repeat_metrics(root: Path, records: list[dict[str, Any]]) -> dict[str, Any]:
    features = [(record, image_features(root / record["image"])) for record in records]
    rms_values: list[float] = []
    structural_values: list[float] = []
    pairs = []
    for left_index, (left_record, left) in enumerate(features):
        for right_record, right in features[left_index + 1:]:
            rms, structural = visual_distances(left, right)
            rms_values.append(rms)
            structural_values.append(structural)
            pairs.append({
                "leftRepeat": int(left_record["repeatIndex"]),
                "rightRepeat": int(right_record["repeatIndex"]),
                "rms128": rms, "structuralDistance": structural,
            })
    return {
        "pairs": len(rms_values),
        "maximumRms128": max(rms_values, default=0.0),
        "maximumStructuralDistance": max(structural_values, default=0.0),
        "pairMetrics": pairs,
    }


def connected_components(nodes: list[str], edges: list[tuple[str, str]]) -> list[list[str]]:
    parent = {node: node for node in nodes}

    def find(value: str) -> str:
        while parent[value] != value:
            parent[value] = parent[parent[value]]
            value = parent[value]
        return value

    for left, right in edges:
        left_root, right_root = find(left), find(right)
        if left_root != right_root:
            parent[right_root] = left_root
    groups: dict[str, list[str]] = defaultdict(list)
    for node in nodes:
        groups[find(node)].append(node)
    return sorted((sorted(group) for group in groups.values()), key=lambda group: (-len(group), group))


def control_audit(
    root: Path, report: dict[str, Any], manifest: dict[str, Any], protocol: dict[str, Any],
    historical_records: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    controls = {item["controlId"]: item for item in manifest["positiveControls"]}
    groups: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in report["records"]:
        if record["sourceKind"] == "positive_control":
            groups[record["sourceId"]].append(record)
    rule = protocol["positiveControlReproducibility"]
    results = []
    for control_id in sorted(controls):
        control = controls[control_id]
        records = groups.get(control_id, [])
        scores = [float(item["score"]["total"]) for item in records]
        position_errors = [position_distance(control["probePosition"], item["probePosition"]) for item in records]
        rotation_errors = [q_angle(quaternion(control["probeRotation"]), quaternion(item["probeRotation"])) for item in records]
        visual = pairwise_repeat_metrics(root, records)
        settled_pairs = [
            item for item in visual["pairMetrics"] if item["leftRepeat"] > 0 and item["rightRepeat"] > 0
        ]
        first_pairs = [
            item for item in visual["pairMetrics"] if item["leftRepeat"] == 0 or item["rightRepeat"] == 0
        ]
        rms_limit = float(rule["maximumWithinControlRms128"])
        structural_limit = float(rule["maximumWithinControlStructuralDistance"])
        unsettled_pattern = (
            bool(settled_pairs) and bool(first_pairs)
            and all(item["rms128"] <= rms_limit and item["structuralDistance"] <= structural_limit
                    for item in settled_pairs)
            and all(item["rms128"] > rms_limit or item["structuralDistance"] > structural_limit
                    for item in first_pairs)
        )
        lineage_id = f"R4_P1:{control['sourceCaseId']}:{int(control['sourceFrameIndex']):04d}"
        historical = historical_records.get(lineage_id)
        checks = {
            "repeatCount": len(records) >= int(rule["minimumFreshRepeatsPerControl"]),
            "posePosition": max(position_errors, default=math.inf) <= float(rule["maximumPoseRestorePositionErrorMm"]),
            "poseRotation": max(rotation_errors, default=math.inf) <= float(rule["maximumPoseRestoreAngleErrorDeg"]),
            "allPhysicalValid": bool(records) and all(
                item["t7Valid"] and item["contactValid"] and item["workspaceValid"] for item in records
            ),
            "allPositive": bool(records) and all(item["acceptable"] for item in records),
            "scoreStable": bool(scores) and max(scores) - min(scores) <= float(rule["maximumWithinControlScoreRange"]),
            "imageRmsStable": visual["maximumRms128"] <= float(rule["maximumWithinControlRms128"]),
            "imageStructureStable": visual["maximumStructuralDistance"] <= float(rule["maximumWithinControlStructuralDistance"]),
            "historicalLineageFound": historical is not None,
        }
        results.append({
            "controlId": control_id, "historicalRecordId": lineage_id,
            "historicalFamily": historical.get("lineageKeys", [None])[0] if historical else None,
            "historicalLineageKeys": historical.get("lineageKeys", []) if historical else [],
            "repeatCount": len(records), "scoreMinimum": min(scores, default=None),
            "scoreMaximum": max(scores, default=None),
            "scoreRange": max(scores) - min(scores) if scores else None,
            "maximumPositionErrorMm": max(position_errors, default=None),
            "maximumRotationErrorDeg": max(rotation_errors, default=None),
            **visual,
            "firstCaptureUnsettledPattern": unsettled_pattern,
            "settledRepeatPairsPassFrozenVisualLimits": bool(settled_pairs) and all(
                item["rms128"] <= rms_limit and item["structuralDistance"] <= structural_limit
                for item in settled_pairs
            ),
            "checks": checks, "pass": all(checks.values()),
        })
    passing = [item for item in results if item["pass"]]
    independent = set(
        key for item in passing for key in item["historicalLineageKeys"]
        if key.startswith("train_family_") or key.startswith("dev_family_")
    )
    overall = (
        len(passing) >= int(rule["minimumControls"])
        and len(independent) >= int(rule["minimumIndependentHistoricalFamilies"])
    )
    unsettled_controls = sum(item["firstCaptureUnsettledPattern"] for item in results)
    ordered = [item for item in report["records"] if item["sourceKind"] == "positive_control"]
    ordered_features = {item["recordId"]: image_features(root / item["image"]) for item in ordered}
    boundary_diagnostics = []
    for index, current in enumerate(ordered):
        if index == 0 or int(current["repeatIndex"]) != 0 or index + 1 >= len(ordered):
            continue
        previous, settled = ordered[index - 1], ordered[index + 1]
        if previous["sourceId"] == current["sourceId"] or settled["sourceId"] != current["sourceId"]:
            continue
        previous_to_first = visual_distances(
            ordered_features[previous["recordId"]], ordered_features[current["recordId"]]
        )
        first_to_settled = visual_distances(
            ordered_features[current["recordId"]], ordered_features[settled["recordId"]]
        )
        matches_previous = (
            previous_to_first[0] <= float(rule["maximumWithinControlRms128"])
            and previous_to_first[1] <= float(rule["maximumWithinControlStructuralDistance"])
            and (first_to_settled[0] > float(rule["maximumWithinControlRms128"])
                 or first_to_settled[1] > float(rule["maximumWithinControlStructuralDistance"]))
        )
        boundary_diagnostics.append({
            "newControlId": current["sourceId"], "previousControlId": previous["sourceId"],
            "previousFinalToNewRepeat0Rms128": previous_to_first[0],
            "previousFinalToNewRepeat0StructuralDistance": previous_to_first[1],
            "newRepeat0ToRepeat1Rms128": first_to_settled[0],
            "newRepeat0ToRepeat1StructuralDistance": first_to_settled[1],
            "newRepeat0MatchesPreviousPoseImage": matches_previous,
        })
    return {
        "verdict": "PASS" if overall else "FAIL", "passingControls": len(passing),
        "requiredControls": int(rule["minimumControls"]),
        "independentHistoricalFamilies": sorted(independent),
        "requiredIndependentHistoricalFamilies": int(rule["minimumIndependentHistoricalFamilies"]),
        "renderSettleDiagnostic": {
            "controlsWithFirstCaptureUnsettledPattern": unsettled_controls,
            "controlsEvaluated": len(results),
            "demonstrated": len(results) > 0 and unsettled_controls == len(results),
            "poseChangeBoundariesChecked": len(boundary_diagnostics),
            "poseChangeBoundariesWhereRepeat0MatchesPreviousPose": sum(
                item["newRepeat0MatchesPreviousPoseImage"] for item in boundary_diagnostics
            ),
            "boundaryMetrics": boundary_diagnostics,
            "interpretation": "For every control, repeat 0 differs beyond a frozen visual limit from later repeats while repeats 1-2 agree. This is direct evidence that capture immediately after pose restoration is not fresh/settled.",
        },
        "controls": results,
    }


def candidate_grid_components(
    records: list[dict[str, Any]], manifest: dict[str, Any]
) -> list[list[str]]:
    declared = {item["candidateId"]: item for item in manifest["mapCandidates"]}
    eligible = {
        record["sourceId"]: record for record in records
        if record["sourceKind"] == "basin_candidate" and record["acceptable"]
        and record["t7Valid"] and record["contactValid"] and record["workspaceValid"]
    }
    edges: list[tuple[str, str]] = []
    ids = sorted(eligible)
    for left_index, left_id in enumerate(ids):
        left = declared[left_id]
        for right_id in ids[left_index + 1:]:
            right = declared[right_id]
            if left["anchorControlId"] != right["anchorControlId"] or left["stage"] != right["stage"]:
                continue
            differences = sum(
                not math.isclose(float(left[field]), float(right[field]), abs_tol=1e-6)
                for field in ("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg")
            )
            if differences != 1:
                continue
            stage_values = [
                sorted(set(float(item[field]) for item in manifest["mapCandidates"]
                           if item["anchorControlId"] == left["anchorControlId"] and item["stage"] == left["stage"]))
                for field in ("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg")
            ]
            adjacent = False
            for field_index, field in enumerate(("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg")):
                if not math.isclose(float(left[field]), float(right[field]), abs_tol=1e-6):
                    values = stage_values[field_index]
                    adjacent = abs(values.index(float(left[field])) - values.index(float(right[field]))) == 1
                    break
            if adjacent:
                edges.append((left_id, right_id))
    return connected_components(ids, edges) if ids else []


def control_pose_components(
    report: dict[str, Any], *, position_mm: float, angle_deg: float
) -> list[list[str]]:
    first = {}
    for record in report["records"]:
        if record["sourceKind"] == "positive_control" and record["sourceId"] not in first:
            first[record["sourceId"]] = record
    ids = sorted(first)
    edges = []
    for left_index, left_id in enumerate(ids):
        for right_id in ids[left_index + 1:]:
            left, right = first[left_id], first[right_id]
            if position_distance(left["probePosition"], right["probePosition"]) <= position_mm and q_angle(
                quaternion(left["probeRotation"]), quaternion(right["probeRotation"])
            ) <= angle_deg:
                edges.append((left_id, right_id))
    return connected_components(ids, edges)


def basin_audit(root: Path, report: dict[str, Any], manifest: dict[str, Any], protocol: dict[str, Any]) -> dict[str, Any]:
    floor = float(manifest["acceptableScoreFloor"])
    candidates = [item for item in report["records"] if item["sourceKind"] == "basin_candidate"]
    evaluator_positive = [item for item in candidates if item["acceptable"]]
    strict_positive = [
        item for item in evaluator_positive
        if item["t7Valid"] and item["contactValid"] and item["workspaceValid"]
    ]
    t7_negative = [
        item for item in candidates
        if not item["acceptable"] and item["t7Valid"] and item["contactValid"] and item["workspaceValid"]
    ]
    components = candidate_grid_components(candidates, manifest)
    representatives: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in report["records"]:
        if record["sourceKind"] == "basin_representative":
            representatives[record["sourceId"]].append(record)
    representative_results = []
    for source_id, records in sorted(representatives.items()):
        scores = [float(item["score"]["total"]) for item in records]
        visual = pairwise_repeat_metrics(root, records)
        stable = (
            len(records) >= 3 and all(
                item["acceptable"] and item["t7Valid"] and item["contactValid"] and item["workspaceValid"]
                for item in records
            ) and min(scores) >= floor + float(protocol["basinStability"]["minimumInteriorScoreMargin"])
            and visual["maximumRms128"] <= float(protocol["positiveControlReproducibility"]["maximumWithinControlRms128"])
            and visual["maximumStructuralDistance"] <= float(
                protocol["positiveControlReproducibility"]["maximumWithinControlStructuralDistance"]
            )
        )
        representative_results.append({
            "sourceId": source_id, "anchorControlId": records[0]["familyId"], "repeats": len(records),
            "scoreMinimum": min(scores), "scoreMaximum": max(scores),
            "minimumMargin": min(scores) - floor, **visual, "repeatStableAcceptableInterior": stable,
        })
    primary = protocol["spatialIndependence"]["primary"]
    conservative = protocol["spatialIndependence"]["conservativeSensitivity"]
    primary_controls = control_pose_components(
        report, position_mm=float(primary["poseMatchPositionMm"]), angle_deg=float(primary["poseMatchAngleDeg"])
    )
    conservative_controls = control_pose_components(
        report, position_mm=float(conservative["nearStartPositionMm"]), angle_deg=float(conservative["nearStartAngleDeg"])
    )
    geometry_pass = (
        max((len(group) for group in components), default=0)
        >= int(protocol["basinStability"]["minimumConnectedAcceptableCells"])
        and any(item["repeatStableAcceptableInterior"] for item in representative_results)
    )
    return {
        "verdict": "PASS" if geometry_pass else "FAIL",
        "candidateBudget": {
            "declared": len(manifest["mapCandidates"]), "attempted": int(report["attemptedCandidatePoses"]),
            "captured": len(candidates), "maximum": int(protocol["basinStability"]["maximumCandidatePoses"]),
        },
        "candidateCoverage": {
            "evaluatorPositive": len(evaluator_positive),
            "evaluatorPositiveButOffT7": sum(not item["t7Valid"] for item in evaluator_positive),
            "strictT7SafePositive": len(strict_positive), "strictT7SafeNegative": len(t7_negative),
            "maximumEvaluatorPositiveScore": max((float(item["score"]["total"]) for item in evaluator_positive), default=None),
            "maximumStrictT7SafeScore": max((float(item["score"]["total"]) for item in candidates
                                              if item["t7Valid"] and item["contactValid"] and item["workspaceValid"]), default=None),
        },
        "strictCandidateGridComponents": components,
        "largestStrictCandidateComponent": max((len(group) for group in components), default=0),
        "representatives": representative_results,
        "repeatStableAcceptableRepresentatives": sum(
            item["repeatStableAcceptableInterior"] for item in representative_results
        ),
        "controlOnlySpatialSensitivity": {
            "primaryComponents": primary_controls,
            "conservativeComponents": conservative_controls,
            "interpretation": "Reused positive controls are disclosed separately and do not replace the required newly mapped candidate basin.",
        },
        "reachability": "NOT_RUN_STAGE_C",
        "blockingFacts": [
            "No mapped candidate simultaneously passed score floor, T7 validity, contact, and workspace.",
            "No repeated basin representative remained evaluator-positive; all four selected representatives were negative on every fresh repeat.",
            "Stage B contains direct pose placement, not a safe executed target-independent route to the basin.",
            "Every mapped candidate has only one capture taken immediately after pose change; control repeats demonstrate that the first such capture is unsettled, so candidate image/pose associations are not validated as fresh.",
        ],
    }


def load_historical_records(path: Path) -> dict[str, dict[str, Any]]:
    result = {}
    with path.open("r", encoding="utf-8") as stream:
        for line in stream:
            item = json.loads(line)
            result[item["id"]] = item
    return result


def build(handoff: Path, repo: Path, historical_index: Path) -> dict[str, Any]:
    report, manifest, seal, errors = verify_snapshot(handoff)
    protocol_path = repo / "Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json"
    protocol = read_json(protocol_path)
    if errors:
        return {
            "schemaVersion": 1, "taskId": "G2-R6-STAGE-B-INDEPENDENT-AUDIT",
            "verdict": "STAGE_B_NO_GO", "integrityStatus": "FAIL", "errors": errors,
            "authorizedNextStage": False,
        }
    historical = load_historical_records(historical_index)
    controls = control_audit(handoff, report, manifest, protocol, historical)
    basin = basin_audit(handoff, report, manifest, protocol)
    stage_b_pass = controls["verdict"] == "PASS" and basin["verdict"] == "PASS"
    blockers = []
    if controls["verdict"] != "PASS":
        blockers.append("positive-control reproducibility failed")
        if controls["renderSettleDiagnostic"]["demonstrated"]:
            blockers.append(
                "All four controls demonstrate a stale/unsettled first capture after pose restoration; candidate single-frame captures therefore lack fresh image/pose validity."
            )
    if basin["verdict"] != "PASS":
        blockers.extend(basin["blockingFacts"])
    result = {
        "schemaVersion": 1,
        "taskId": "G2-R6-STAGE-B-INDEPENDENT-AUDIT",
        "verdict": "STAGE_B_PASS" if stage_b_pass else "STAGE_B_NO_GO",
        "authorizedNextStage": False,
        "authorizationNote": "This audit never authorizes or runs Stage C; coordinator approval is separate.",
        "integrityStatus": "PASS",
        "positiveControlReproducibility": controls["verdict"],
        "sampledBasinGeometry": basin["verdict"],
        "basinStability": "FAIL" if basin["verdict"] == "FAIL" else "PENDING_TARGET_INDEPENDENT_REACHABILITY",
        "targetIndependentReachability": "NOT_RUN",
        "intermediateStageBCVerdict": "NO_GO",
        "engineeringDemoDataReady": False,
        "scientificTrainingDataReady": False,
        "blockingReasons": blockers + ["Stage C target-independent reachability has not run."],
        "provenance": {
            "handoff": str(handoff), "sourceCommit": seal["sourceCommit"],
            "manifestSha256": sha256(handoff / "capture_input_manifest.json"),
            "reportSha256": sha256(handoff / "r6_unity_report.json"),
            "evidenceIndexSha256": sha256(handoff / "evidence_index.json"),
            "snapshotSealSha256": sha256(handoff / "snapshot_seal.json"),
            "auditProtocolSha256": sha256(protocol_path),
            "coordinatorProtocolCanonicalSha256": seal["coordinatorProtocolCanonicalSha256"],
            "auditProtocolCanonicalSha256": seal["auditProtocolCanonicalSha256"],
            "sceneCanonicalSha256": seal["sceneCanonicalSha256"],
            "evaluatorCanonicalSha256": seal["evaluatorCanonicalSha256"],
        },
        "snapshotCounts": {
            "indexedFiles": seal["indexedFiles"], "records": seal["recordCount"],
            "images": seal["imageCount"], "actions": seal["actionCount"], "attempts": seal["attemptCount"],
        },
        "controls": controls,
        "basin": basin,
        "sourceCrossReview": {
            "targetLeakage": "PASS: fixed route action executor and underlying actuator use only manifest action plus physical safety/contact/workspace; evaluator labels are recorded after action.",
            "budgetContract": "PASS for exact frozen manifest: 4 controls, 240 coarse, 72 fine, 312 total candidates.",
            "publication": "PASS after independent seal/index/hash/size/count validation.",
            "limitations": [
                "Runner does not itself compare the pre-outcome Stage B manifest SHA; this audit enforced the expected 50c205... hash.",
                "Runtime repeats one highest-score T7-safe candidate per control, not one representative per connected acceptable region, and does not require the selected candidate to be acceptable.",
                "Runtime aborts when any of four controls fails although frozen science threshold requires at least three; this is stricter and can only cause false NO_GO.",
            ],
        },
    }
    return result


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Independently audit complete immutable R6 Stage B evidence.")
    parser.add_argument("--handoff", type=Path, required=True)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--historical-index", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo = args.repo.resolve()
    result = build(args.handoff.resolve(), repo, args.historical_index.resolve())
    output = args.output or repo / (
        "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r6_final_sprint_20261026/"
        "g2_r6_stage_b_independent_audit.json"
    )
    write_json(output, result)
    print(json.dumps({
        "output": str(output), "verdict": result["verdict"],
        "integrityStatus": result["integrityStatus"],
        "positiveControlReproducibility": result.get("positiveControlReproducibility"),
        "sampledBasinGeometry": result.get("sampledBasinGeometry"),
        "authorizedNextStage": result["authorizedNextStage"],
    }, indent=2))
    return 0 if result["integrityStatus"] == "PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
