from __future__ import annotations

import argparse
import copy
import itertools
import json
import math
from collections import defaultdict
from pathlib import Path
from typing import Any

from g2_p2_basin_audit import basin_decision, candidate_passes, validate_grid
from g2_p2_freshness_audit import (
    canonical_sha256,
    handshake_errors,
    position_distance,
    quaternion,
    read_json,
    record_capture_path_errors,
    record_image_errors,
    write_json,
)
from g2_r6_audit import image_features, q_angle, sha256, visual_distances


PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_p3_stage_b_final_protocol_v1.json"
REFERENCE_PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_p2_corrected_basin_audit_protocol_v1.json"
FRESHNESS_PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_p2_freshness_audit_protocol_v1.json"


def _adapt_basin_protocol(repo: Path, protocol: dict[str, Any]) -> tuple[dict[str, Any], list[str]]:
    errors: list[str] = []
    path = repo / REFERENCE_PROTOCOL
    if not path.is_file() or canonical_sha256(path) != protocol["frozenIdentities"]["referenceBasinProtocolCanonicalSha256"]:
        return {}, ["reference basin protocol identity mismatch"]
    adapted = copy.deepcopy(read_json(path))
    adapted["frozenThresholds"].update({
        "acceptableScoreFloor": protocol["positiveBasinGate"]["acceptableScoreFloor"],
        "maximumCandidatePoses": protocol["fixedGridContract"]["maximumCandidatePoses"],
        "minimumConnectedAcceptableCells": protocol["positiveBasinGate"]["minimumConnectedAcceptableCells"],
        "minimumInteriorScoreMargin": protocol["positiveBasinGate"]["minimumInteriorScoreMargin"],
        "minimumFreshCapturesPerCandidate": protocol["freshCaptureContract"]["minimumFreshCapturesPerCandidate"],
        "minimumFreshRepeatsPerRepresentative": protocol["freshCaptureContract"]["minimumFreshRepeatsPerRepresentative"],
        "maximumWithinPoseScoreRange": protocol["positiveBasinGate"]["maximumWithinPoseScoreRange"],
        "maximumWithinPoseRms128": protocol["positiveBasinGate"]["maximumWithinPoseRms128"],
        "maximumWithinPoseStructuralDistance": protocol["positiveBasinGate"]["maximumWithinPoseStructuralDistance"],
        "maximumPoseRestorePositionErrorMm": protocol["positiveBasinGate"]["maximumPoseRestorePositionErrorMm"],
        "maximumPoseRestoreAngleErrorDeg": protocol["positiveBasinGate"]["maximumPoseRestoreAngleErrorDeg"],
    })
    adapted["gridContract"].update({
        "historicalControlIds": protocol["fixedGridContract"]["historicalControlIds"],
        "requiredStages": protocol["fixedGridContract"]["requiredStages"],
        "optionalStage": protocol["fixedGridContract"]["optionalStage"],
    })
    adapted["verdicts"] = dict(protocol["verdicts"])
    return adapted, errors


def validate_p3_grid(manifest: dict[str, Any], protocol: dict[str, Any], adapted: dict[str, Any]) -> list[str]:
    errors = validate_grid(manifest, adapted)
    candidates = manifest.get("mapCandidates", [])
    controls = protocol["fixedGridContract"]["historicalControlIds"]
    frozen = protocol["fixedGridContract"]["refinement"]
    if len(candidates) != int(frozen["baseCandidateCount"]):
        errors.append("initial manifest must contain exactly the frozen base candidate count")

    def cartesian_errors(items: list[dict[str, Any]], expected_count: int, label: str, include_zero: bool) -> None:
        fields = ("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg")
        offsets: set[tuple[float, float, float]] = set()
        axes: list[list[float]] = []
        try:
            offsets = {
                tuple(round(float(item[field]), 8) for field in fields)
                for item in items
            }
            axes = [sorted({value[axis] for value in offsets}) for axis in range(3)]
        except (KeyError, TypeError, ValueError):
            errors.append(f"{label} has malformed offsets")
            return
        if len(items) != expected_count or len(offsets) != expected_count:
            errors.append(f"{label} must contain {expected_count} unique offsets")
            return
        if any(len(axis) != 3 or axis[1] != 0.0 or not math.isclose(axis[0], -axis[2], abs_tol=1e-8) for axis in axes):
            errors.append(f"{label} axes are not frozen signed Cartesian levels")
            return
        expected = set(itertools.product(*axes))
        if not include_zero:
            expected.discard((0.0, 0.0, 0.0))
        if offsets != expected:
            errors.append(f"{label} is not the complete frozen Cartesian product")

    for control in controls:
        control_items = [item for item in candidates if item.get("anchorControlId") == control]
        if len(control_items) != int(frozen["baseCandidatesPerAnchor"]):
            errors.append(f"base candidate count mismatch: {control}")
        for stage in protocol["fixedGridContract"]["requiredStages"]:
            group = [item for item in candidates if item.get("anchorControlId") == control and item.get("stage") == stage]
            if not group:
                errors.append(f"required stage missing for {control}: {stage}")
                continue
            if stage in {"coarse", "wider"}:
                expected_count = int(frozen[
                    "smallCartesianNeighborsPerAnchor" if stage == "coarse" else "widerCartesianNeighborsPerAnchor"
                ])
                cartesian_errors(group, expected_count, f"{control}/{stage}", False)
                for field in ("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg"):
                    values = [float(item.get(field, 0.0)) for item in group]
                    if not any(value > 0 for value in values) or not any(value < 0 for value in values):
                        errors.append(f"{stage} grid lacks signed {field}: {control}")
    refinement = manifest.get("refinement")
    if not isinstance(refinement, dict):
        errors.append("refinement contract missing")
    else:
        if refinement.get("declaredBeforeCoarseCapture") is not True:
            errors.append("refinement was not declared before coarse capture")
        if refinement.get("selectionRule") != frozen["selectionRule"]:
            errors.append("refinement selection rule mismatch")
        if not isinstance(refinement.get("offsetTemplate"), list):
            errors.append("refinement offset template missing")
        else:
            cartesian_errors(
                refinement["offsetTemplate"], int(frozen["refinementTemplateSlotsPerAnchor"]),
                "refinement offset template", True,
            )
        if refinement.get("coarseSnapshotFile") != frozen["coarseSnapshotFile"]:
            errors.append("coarse snapshot filename mismatch")
        if refinement.get("selectionManifestFile") != frozen["selectionManifestFile"]:
            errors.append("refinement selection filename mismatch")
        if int(refinement.get("maximumSlotsPerAnchor", -1)) != int(frozen["refinementTemplateSlotsPerAnchor"]):
            errors.append("refinement slot count mismatch")
        if refinement.get("postOutcomeExtensionAllowed") is not False:
            errors.append("post-outcome refinement extension is not forbidden")
    if manifest.get("outcomeAdaptiveCandidateAddition") is not False:
        errors.append("outcome-adaptive candidate addition is not forbidden")
    return sorted(set(errors))


def _verify_snapshot(
    repo: Path,
    root: Path,
    protocol: dict[str, Any],
    *,
    expected_manifest_sha256: str,
    expected_source_commit: str,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], list[str]]:
    errors: list[str] = []
    names = {
        "manifest": "stage_b_final_manifest.json",
        "coarse": "stage_b_coarse_snapshot.json",
        "selection": "refinement_selection_manifest.json",
        "report": "stage_b_final_report.json",
        "index": "evidence_index.json",
        "seal": "snapshot_seal.json",
    }
    paths = {key: root / name for key, name in names.items()}
    for key, path in paths.items():
        if not path.is_file():
            errors.append(f"required snapshot file missing: {names[key]}")
    if errors:
        return {}, {}, {}, errors
    manifest, report, index, seal = (read_json(paths[key]) for key in ("manifest", "report", "index", "seal"))
    if any(item.get("protocolId") != protocol["protocolId"] for item in (manifest, report)):
        errors.append("Stage B protocol identity mismatch")
    if any(item.get("mode") != protocol["mode"] for item in (manifest, report)):
        errors.append("Stage B mode mismatch")
    if report.get("complete") is not True or report.get("infrastructureFailure"):
        errors.append("Stage B report incomplete or failed")
    if seal.get("complete") is not True or seal.get("immutable") is not True or seal.get("failureSnapshot") is not False:
        errors.append("Stage B snapshot is not complete and immutable")
    if manifest.get("sourceCommit") != expected_source_commit or seal.get("sourceCommit") != expected_source_commit:
        errors.append("source commit mismatch")
    if sha256(paths["manifest"]) != expected_manifest_sha256 or seal.get("manifestSha256") != expected_manifest_sha256:
        errors.append("manifest differs from prefrozen hash")
    protocol_hash = canonical_sha256(repo / PROTOCOL)
    if manifest.get("auditProtocolCanonicalSha256") != protocol_hash or seal.get("auditProtocolCanonicalSha256") != protocol_hash:
        errors.append("Stage B audit protocol hash mismatch")
    if manifest.get("upstreamStageFIndependentAuditSha256") != protocol["upstreamGate"]["independentAuditSha256"]:
        errors.append("upstream Stage F audit lineage mismatch")
    identities = protocol["frozenIdentities"]
    if seal.get("sceneCanonicalSha256") != identities["sceneCanonicalSha256"]:
        errors.append("scene identity mismatch")
    if seal.get("evaluatorCanonicalSha256") != identities["evaluatorCanonicalSha256"]:
        errors.append("evaluator identity mismatch")
    entries = index.get("files", [])
    if not isinstance(entries, list) or len(entries) != int(seal.get("indexedFiles", -1)):
        errors.append("seal/index file count mismatch")
    for entry in entries if isinstance(entries, list) else []:
        path = (root / str(entry.get("path", ""))).resolve()
        try:
            path.relative_to(root.resolve())
        except ValueError:
            errors.append(f"indexed path escapes snapshot: {entry.get('path')}")
            continue
        if not path.is_file():
            errors.append(f"indexed file missing: {entry.get('path')}")
        elif path.stat().st_size != int(entry.get("bytes", -1)) or sha256(path) != entry.get("sha256"):
            errors.append(f"indexed file mismatch: {entry.get('path')}")
    for path, field in (
        (paths["manifest"], "manifestSha256"),
        (paths["coarse"], "coarseSnapshotSha256"),
        (paths["selection"], "refinementSelectionManifestSha256"),
        (paths["report"], "reportSha256"),
        (paths["index"], "evidenceIndexSha256"),
    ):
        if sha256(path) != seal.get(field):
            errors.append(f"seal hash mismatch: {field}")
    records = report.get("records", [])
    if not isinstance(records, list) or len(records) != int(seal.get("recordCount", -1)):
        errors.append("record count mismatch")
    return report, manifest, seal, errors


def audit_physical_records(repo: Path, root: Path, report: dict[str, Any], protocol: dict[str, Any]) -> dict[str, Any]:
    records = report.get("records", [])
    errors: list[str] = []
    freshness = read_json(repo / FRESHNESS_PROTOCOL)
    path_rule = freshness["producerPoseHandshake"]
    expected_domain = protocol["freshCaptureContract"]["evidenceDomain"]
    for record in records:
        if record.get("evidenceDomain") != expected_domain or record.get("synthetic") is not False or record.get("faultInjected") is not False:
            errors.append(f"evidence-domain contamination: {record.get('recordId')}")
        errors.extend(f"{record.get('recordId')}:{value}" for value in record_capture_path_errors(record, path_rule))
        if record.get("captureDisposition") == "accepted":
            errors.extend(f"{record.get('recordId')}:{value}" for value in handshake_errors(record, path_rule))
    errors.extend(record_image_errors(root, records))
    records_by_candidate: dict[str, list[dict[str, Any]]] = defaultdict(list)
    accepted_by_candidate: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in records:
        records_by_candidate[str(record.get("candidateId"))].append(record)
        if record.get("captureDisposition") == "accepted":
            accepted_by_candidate[str(record.get("candidateId"))].append(record)
    result_by_id = {str(item.get("candidateId")): item for item in report.get("candidateResults", [])}
    if len(result_by_id) != len(report.get("candidateResults", [])):
        errors.append("candidate result IDs missing or duplicated")

    limits = protocol["positiveBasinGate"]
    normalized: list[dict[str, Any]] = []
    comparable = (
        "acceptedFreshCaptures", "freshnessPass", "repeatStable", "allAcceptable",
        "allFinalTargetAcceptable", "allT7Valid", "allContactValid", "allWorkspaceValid",
        "minimumScore", "scoreRange", "maximumRms128", "maximumStructuralDistance",
        "poseRestorePass",
    )

    def warmup_kind(record: dict[str, Any]) -> str:
        reason = str(record.get("discardReason", record.get("actionRejectionReason", "")))
        return next((kind for kind in protocol["freshCaptureContract"]["discardedWarmups"] if reason.startswith(kind)), "")

    def same_value(left: Any, right: Any) -> bool:
        if isinstance(left, (int, float)) and not isinstance(left, bool):
            try:
                return math.isclose(float(left), float(right), rel_tol=0.0, abs_tol=1e-9)
            except (TypeError, ValueError):
                return False
        return left == right

    all_candidate_ids = sorted(set(records_by_candidate) | set(result_by_id))
    for candidate_id in all_candidate_ids:
        result = result_by_id.get(candidate_id, {})
        group = records_by_candidate.get(candidate_id, [])
        accepted = sorted(
            accepted_by_candidate.get(candidate_id, []),
            key=lambda item: (int(item.get("acquisitionOrder", -1)), str(item.get("recordId", ""))),
        )
        discarded = [item for item in group if item.get("captureDisposition") == "discarded"]
        rejected = not accepted
        if rejected:
            derived = {
                "candidateId": candidate_id, "acceptedFreshCaptures": 0,
                "freshnessPass": False, "repeatStable": False,
                "allAcceptable": False, "allFinalTargetAcceptable": False,
                "allT7Valid": False, "allContactValid": False, "allWorkspaceValid": False,
                "minimumScore": -math.inf, "scoreRange": math.inf,
                "maximumRms128": math.inf, "maximumStructuralDistance": math.inf,
                "poseRestorePass": False,
                "rejectionReason": result.get("rejectionReason"),
                "actualResultingPoseRecorded": result.get("actualResultingPoseRecorded"),
            }
            if not result.get("rejectionReason"):
                errors.append(f"candidate lacks captures and rejection reason: {candidate_id}")
            if result.get("actualResultingPoseRecorded") is not True:
                errors.append(f"rejected candidate actual pose missing: {candidate_id}")
        else:
            kinds = {warmup_kind(item) for item in discarded}
            required_kinds = set(protocol["freshCaptureContract"]["discardedWarmups"])
            if not required_kinds.issubset(kinds):
                errors.append(f"candidate discarded warmups missing: {candidate_id}")
            try:
                monotonic = all(
                    int(right["acquisitionOrder"]) > int(left["acquisitionOrder"])
                    and int(right["unityFrameCount"]) > int(left["unityFrameCount"])
                    and int(right["producerScanSerial"]) > int(left["producerScanSerial"])
                    for left, right in zip(accepted, accepted[1:])
                )
            except (KeyError, TypeError, ValueError):
                monotonic = False
            if not monotonic:
                errors.append(f"candidate accepted captures are not causally monotonic: {candidate_id}")
            pairs: list[tuple[float, float]] = []
            for index, left in enumerate(accepted):
                for right in accepted[index + 1:]:
                    pairs.append(visual_distances(image_features(root / left["image"]), image_features(root / right["image"])))
            scores = [float(item.get("score", {}).get("total", -math.inf)) for item in accepted]
            maximum_rms = max((item[0] for item in pairs), default=math.inf)
            maximum_structural = max((item[1] for item in pairs), default=math.inf)
            pose_positions: list[float] = []
            pose_angles: list[float] = []
            try:
                pose_positions = [position_distance(item["requestedProbePosition"], item["probePosition"]) for item in accepted]
                pose_angles = [q_angle(quaternion(item["requestedProbeRotation"]), quaternion(item["probeRotation"])) for item in accepted]
            except (KeyError, TypeError, ValueError):
                pass
            pose_pass = (
                len(pose_positions) == len(accepted) and len(pose_angles) == len(accepted)
                and max(pose_positions) <= float(limits["maximumPoseRestorePositionErrorMm"])
                and max(pose_angles) <= float(limits["maximumPoseRestoreAngleErrorDeg"])
            )
            handshake_pass = all(not handshake_errors(item, path_rule) for item in accepted)
            stable = (
                len(accepted) >= int(protocol["freshCaptureContract"]["minimumFreshCapturesPerCandidate"])
                and bool(pairs)
                and maximum_rms <= float(limits["maximumWithinPoseRms128"])
                and maximum_structural <= float(limits["maximumWithinPoseStructuralDistance"])
            )
            derived = {
                "candidateId": candidate_id, "acceptedFreshCaptures": len(accepted),
                "freshnessPass": handshake_pass and monotonic and pose_pass,
                "repeatStable": stable,
                "allAcceptable": all(item.get("acceptable") is True for item in accepted),
                "allFinalTargetAcceptable": all(item.get("finalTargetAcceptable") is True for item in accepted),
                "allT7Valid": all(item.get("t7Valid") is True for item in accepted),
                "allContactValid": all(item.get("contactValid") is True for item in accepted),
                "allWorkspaceValid": all(item.get("workspaceValid") is True for item in accepted),
                "minimumScore": min(scores), "scoreRange": max(scores) - min(scores),
                "maximumRms128": maximum_rms,
                "maximumStructuralDistance": maximum_structural,
                "poseRestorePass": pose_pass,
            }
        fields_to_compare = comparable if accepted else (
            "acceptedFreshCaptures", "freshnessPass", "repeatStable", "allAcceptable",
            "allFinalTargetAcceptable", "allT7Valid", "allContactValid", "allWorkspaceValid",
        )
        for field in fields_to_compare:
            if field not in result or not same_value(derived[field], result.get(field)):
                errors.append(f"candidate reported metric differs from independent derivation: {candidate_id}/{field}")
        normalized.append(derived)
    return {
        "verdict": "PASS" if not errors else "FAIL",
        "acceptedRecordCount": sum(len(values) for values in accepted_by_candidate.values()),
        "candidateCountWithAcceptedRecords": len(accepted_by_candidate),
        "errors": sorted(set(errors)),
        "acceptedByCandidate": accepted_by_candidate,
        "normalizedCandidateResults": normalized,
    }


def materialize_refinement_grid(
    root: Path,
    manifest: dict[str, Any],
    seal: dict[str, Any],
    normalized_results: list[dict[str, Any]],
    protocol: dict[str, Any],
    adapted: dict[str, Any],
    *,
    expected_manifest_sha256: str,
    expected_source_commit: str,
) -> dict[str, Any]:
    errors: list[str] = []
    frozen = protocol["fixedGridContract"]["refinement"]
    coarse_path = root / frozen["coarseSnapshotFile"]
    selection_path = root / frozen["selectionManifestFile"]
    if not coarse_path.is_file() or not selection_path.is_file():
        return {"errors": ["refinement materialization artifacts missing"], "topologyManifest": {}, "attemptedCandidateIds": []}
    coarse = read_json(coarse_path)
    selection = read_json(selection_path)
    coarse_hash = sha256(coarse_path)
    selection_hash = sha256(selection_path)
    protocol_hash = manifest.get("auditProtocolCanonicalSha256")
    for name, value in (("coarse snapshot", coarse), ("refinement selection", selection)):
        if value.get("protocolId") != protocol["protocolId"] or value.get("mode") != protocol["mode"]:
            errors.append(f"{name} protocol/mode mismatch")
        if value.get("sourceCommit") != expected_source_commit:
            errors.append(f"{name} source mismatch")
        if value.get("parentManifestSha256") != expected_manifest_sha256:
            errors.append(f"{name} parent manifest mismatch")
        if value.get("auditProtocolCanonicalSha256") != protocol_hash:
            errors.append(f"{name} audit protocol mismatch")
    if coarse.get("complete") is not True or coarse.get("sealedBeforeRefinementCapture") is not True:
        errors.append("coarse snapshot was not sealed before refinement capture")
    if selection.get("generatedBeforeFirstRefinementCapture") is not True:
        errors.append("refinement selection was not sealed before first refinement capture")
    if selection.get("coarseSnapshotSha256") != coarse_hash:
        errors.append("refinement selection coarse snapshot hash mismatch")
    if seal.get("coarseSnapshotSha256") != coarse_hash or seal.get("refinementSelectionManifestSha256") != selection_hash:
        errors.append("final seal does not bind refinement artifacts")
    if selection.get("selectionRule") != frozen["selectionRule"]:
        errors.append("materialized refinement selection rule mismatch")
    if selection.get("postOutcomeExtensionAllowed") is not False:
        errors.append("materialized refinement permits post-outcome extension")

    base_candidates = manifest.get("mapCandidates", [])
    base_ids = {str(item.get("candidateId")) for item in base_candidates}
    result_by_id = {str(item.get("candidateId")): item for item in normalized_results}
    coarse_results = coarse.get("candidateResults", [])
    coarse_by_id = {str(item.get("candidateId")): item for item in coarse_results}
    if set(coarse_by_id) != base_ids:
        errors.append("coarse snapshot does not contain exactly all base candidate results")
    selection_fields = (
        "acceptedFreshCaptures", "freshnessPass", "repeatStable", "allAcceptable",
        "allFinalTargetAcceptable", "allT7Valid", "allContactValid", "allWorkspaceValid",
        "minimumScore", "scoreRange", "maximumRms128", "maximumStructuralDistance", "poseRestorePass",
    )
    for candidate_id in sorted(base_ids & set(coarse_by_id) & set(result_by_id)):
        fields = selection_fields if int(result_by_id[candidate_id].get("acceptedFreshCaptures", 0)) > 0 else (
            "acceptedFreshCaptures", "freshnessPass", "repeatStable", "allAcceptable",
            "allFinalTargetAcceptable", "allT7Valid", "allContactValid", "allWorkspaceValid",
        )
        for field in fields:
            left, right = coarse_by_id[candidate_id].get(field), result_by_id[candidate_id].get(field)
            if isinstance(right, (int, float)) and not isinstance(right, bool):
                try:
                    equal = math.isclose(float(left), float(right), rel_tol=0.0, abs_tol=1e-9)
                except (TypeError, ValueError):
                    equal = False
            else:
                equal = left == right
            if not equal:
                errors.append(f"coarse snapshot differs from independently derived result: {candidate_id}/{field}")

    candidates_by_id = {str(item.get("candidateId")): item for item in base_candidates}
    expected_centers: dict[str, str] = {}
    for control in protocol["fixedGridContract"]["historicalControlIds"]:
        eligible = [
            candidate_id for candidate_id, item in candidates_by_id.items()
            if item.get("anchorControlId") == control and item.get("stage") in {"coarse", "wider"}
            and candidate_id in result_by_id and candidate_passes(result_by_id[candidate_id], adapted)
        ]
        if not eligible:
            errors.append(f"no passing coarse/wider candidate for deterministic refinement: {control}")
            continue
        expected_centers[control] = sorted(
            eligible, key=lambda candidate_id: (-float(result_by_id[candidate_id]["minimumScore"]), candidate_id)
        )[0]
    selections = selection.get("selections", [])
    selected_by_control = {str(item.get("anchorControlId")): str(item.get("selectedCenterCandidateId")) for item in selections}
    if len(selected_by_control) != len(selections) or set(selected_by_control) != set(protocol["fixedGridContract"]["historicalControlIds"]):
        errors.append("refinement selections do not contain exactly one center per anchor")
    for control, expected in expected_centers.items():
        if selected_by_control.get(control) != expected:
            errors.append(f"refinement center violates frozen selection rule: {control}")

    template = manifest.get("refinement", {}).get("offsetTemplate", [])
    fields = ("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg")
    try:
        template_offsets = {tuple(round(float(item[field]), 8) for field in fields) for item in template}
    except (KeyError, TypeError, ValueError):
        template_offsets = set()
    refined = selection.get("mapCandidates", [])
    if len(refined) != len(protocol["fixedGridContract"]["historicalControlIds"]) * int(frozen["refinementTemplateSlotsPerAnchor"]):
        errors.append("materialized refinement attempt count mismatch")
    refined_ids = [str(item.get("candidateId", "")) for item in refined]
    if any(not value for value in refined_ids) or len(refined_ids) != len(set(refined_ids)) or base_ids.intersection(refined_ids):
        errors.append("materialized refinement candidate IDs missing, duplicated, or collide with base IDs")
    zero_ids: set[str] = set()
    topology_refined: list[dict[str, Any]] = []
    for control in protocol["fixedGridContract"]["historicalControlIds"]:
        group = [item for item in refined if item.get("anchorControlId") == control]
        try:
            offsets = {tuple(round(float(item[field]), 8) for field in fields) for item in group}
        except (KeyError, TypeError, ValueError):
            offsets = set()
        if len(group) != int(frozen["refinementTemplateSlotsPerAnchor"]) or offsets != template_offsets:
            errors.append(f"materialized refinement differs from frozen template: {control}")
        for item in group:
            if item.get("stage") != "refinement" or item.get("centerCandidateId") != selected_by_control.get(control):
                errors.append(f"refinement candidate center/stage mismatch: {item.get('candidateId')}")
            try:
                is_zero = all(math.isclose(float(item[field]), 0.0, abs_tol=1e-8) for field in fields)
            except (KeyError, TypeError, ValueError):
                is_zero = False
            if is_zero:
                zero_ids.add(str(item.get("candidateId")))
                if item.get("role") != frozen["zeroOffsetRole"] or item.get("countsAsDistinctCell") is not False:
                    errors.append(f"zero-offset refinement can falsely count as a new cell: {item.get('candidateId')}")
            else:
                if item.get("countsAsDistinctCell") is not True:
                    errors.append(f"nonzero refinement excluded from topology without declaration: {item.get('candidateId')}")
                topology_refined.append(item)
    if len(zero_ids) != len(protocol["fixedGridContract"]["historicalControlIds"]):
        errors.append("exactly one selected-center repeat is required per anchor")

    full_ids = base_ids | set(refined_ids)
    if len(full_ids) != int(frozen["totalAttemptSlots"]):
        errors.append("full materialized attempt budget is not exactly frozen total")
    selection_pairs = selection.get("neighborPairs", [])
    topology_ids = base_ids | {str(item.get("candidateId")) for item in topology_refined}
    topology_pairs: list[list[str]] = []
    linked_refined: set[str] = set()
    for pair in selection_pairs:
        if not isinstance(pair, list) or len(pair) != 2 or pair[0] not in full_ids or pair[1] not in full_ids or pair[0] == pair[1]:
            errors.append("refinement neighbor pair malformed or references unknown candidate")
            continue
        if pair[0] in zero_ids or pair[1] in zero_ids:
            continue
        topology_pairs.append(pair)
        linked_refined.update(endpoint for endpoint in pair if endpoint not in base_ids)
    expected_refined_topology = {str(item.get("candidateId")) for item in topology_refined}
    if linked_refined != expected_refined_topology:
        errors.append("every nonzero refinement cell must have a predeclared topology edge")
    topology_manifest = copy.deepcopy(manifest)
    topology_manifest["mapCandidates"] = [*base_candidates, *topology_refined]
    topology_manifest["neighborPairs"] = [*manifest.get("neighborPairs", []), *topology_pairs]
    return {
        "errors": sorted(set(errors)),
        "topologyManifest": topology_manifest,
        "attemptedCandidateIds": sorted(full_ids),
        "topologyCandidateIds": sorted(topology_ids),
        "zeroOffsetRepeatCandidateIds": sorted(zero_ids),
        "coarseSnapshotSha256": coarse_hash,
        "refinementSelectionManifestSha256": selection_hash,
        "selectedCenters": selected_by_control,
    }


def structural_variation(
    root: Path,
    eligible_components: list[list[str]],
    accepted_by_candidate: dict[str, list[dict[str, Any]]],
    protocol: dict[str, Any],
) -> dict[str, Any]:
    minimum_hashes = int(protocol["structuralVariationGate"]["minimumDistinctAcceptedImageHashesInEligibleComponent"])
    minimum_rms = float(protocol["structuralVariationGate"]["minimumCrossCellRms128"])
    results = []
    for component in eligible_components:
        selected = []
        all_hashes: set[str] = set()
        for candidate_id in component:
            records = accepted_by_candidate.get(candidate_id, [])
            all_hashes.update(str(item.get("imageSha256", "")) for item in records)
            if records:
                selected.append((candidate_id, records[0]))
        maximum_rms = 0.0
        maximum_structural = 0.0
        for left_index, (left_id, left) in enumerate(selected):
            for right_id, right in selected[left_index + 1:]:
                if left_id == right_id:
                    continue
                rms, structural = visual_distances(
                    image_features(root / left["image"]), image_features(root / right["image"])
                )
                maximum_rms = max(maximum_rms, float(rms))
                maximum_structural = max(maximum_structural, float(structural))
        passed = len(all_hashes) >= minimum_hashes and maximum_rms > minimum_rms
        results.append({
            "component": component,
            "distinctAcceptedImageHashes": len(all_hashes),
            "maximumCrossCellRms128": maximum_rms,
            "maximumCrossCellStructuralDistance": maximum_structural,
            "pass": passed,
        })
    return {"verdict": "PASS" if any(item["pass"] for item in results) else "FAIL", "components": results}


def audit_stage_b_final(
    repo: Path,
    root: Path,
    *,
    expected_manifest_sha256: str,
    expected_source_commit: str,
) -> dict[str, Any]:
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    report, manifest, seal, errors = _verify_snapshot(
        repo, root, protocol,
        expected_manifest_sha256=expected_manifest_sha256,
        expected_source_commit=expected_source_commit,
    )
    adapted, reference_errors = _adapt_basin_protocol(repo, protocol)
    errors.extend(reference_errors)
    base_grid_errors = validate_p3_grid(manifest, protocol, adapted) if manifest and adapted else ["grid not evaluated"]
    physical = audit_physical_records(repo, root, report, protocol) if report and not errors else {
        "verdict": "NOT_EVALUATED", "errors": [], "acceptedByCandidate": {}, "normalizedCandidateResults": []
    }
    refinement = materialize_refinement_grid(
        root, manifest, seal, physical.get("normalizedCandidateResults", []), protocol, adapted,
        expected_manifest_sha256=expected_manifest_sha256,
        expected_source_commit=expected_source_commit,
    ) if manifest and seal and adapted else {
        "errors": ["refinement not evaluated"], "topologyManifest": {},
        "attemptedCandidateIds": [], "topologyCandidateIds": [],
    }
    topology_manifest = refinement.get("topologyManifest", {})
    topology_grid_errors = validate_grid(topology_manifest, adapted) if topology_manifest and adapted else ["topology grid not evaluated"]
    attempted_full = {str(value) for value in report.get("attemptedCandidateIds", [])} if report else set()
    expected_full = set(refinement.get("attemptedCandidateIds", []))
    accounting_errors: list[str] = []
    if attempted_full != expected_full:
        accounting_errors.append("attempted candidate identities differ from the full 320-slot frozen materialization")
    normalized_results = physical.get("normalizedCandidateResults", [])
    if {str(item.get("candidateId")) for item in normalized_results} != expected_full:
        accounting_errors.append("independently derived candidate results do not cover the full frozen materialization")
    grid_errors = sorted(set([*base_grid_errors, *topology_grid_errors, *refinement.get("errors", []), *accounting_errors]))
    evidence = dict(report) if report else {}
    evidence["integrityStatus"] = "PASS" if not errors else "FAIL"
    evidence["imagePoseFreshness"] = "PASS" if physical["verdict"] == "PASS" else "FAIL"
    topology_ids = set(refinement.get("topologyCandidateIds", []))
    evidence["attemptedCandidateIds"] = sorted(topology_ids)
    evidence["candidateResults"] = [
        item for item in normalized_results if str(item.get("candidateId")) in topology_ids
    ]
    basin = basin_decision(topology_manifest, evidence, adapted) if topology_manifest and adapted else {
        "verdict": protocol["verdicts"]["fail"], "authorizedNextStage": False,
        "gridErrors": grid_errors, "eligiblePositiveComponents": [],
    }
    basin_grid_errors = sorted(set([*basin.get("gridErrors", []), *grid_errors]))
    basin["gridErrors"] = basin_grid_errors
    variation = structural_variation(
        root, basin.get("eligiblePositiveComponents", []),
        physical.get("acceptedByCandidate", {}), protocol,
    ) if physical["verdict"] == "PASS" else {"verdict": "NOT_EVALUATED", "components": []}
    passed = (
        not errors and not basin_grid_errors and physical["verdict"] == "PASS"
        and basin.get("verdict") == protocol["verdicts"]["pass"]
        and variation["verdict"] == "PASS"
    )
    blockers = [*errors, *basin_grid_errors, *physical.get("errors", [])]
    if basin.get("verdict") != protocol["verdicts"]["pass"]:
        blockers.append("connected positive basin/stable interior gate failed")
    if variation["verdict"] != "PASS":
        blockers.append("structural cross-cell variation gate failed")
    return {
        "schemaVersion": 1,
        "taskId": "G2-P3-STAGE-B-FINAL-INDEPENDENT-AUDIT",
        "verdict": protocol["verdicts"]["pass"] if passed else protocol["verdicts"]["fail"],
        "authorizedNextStage": passed,
        "integrityStatus": "PASS" if not errors else "FAIL",
        "protocol": PROTOCOL,
        "protocolCanonicalSha256": canonical_sha256(protocol_path),
        "provenance": {
            "handoff": str(root), "sourceCommit": expected_source_commit,
            "manifestSha256": expected_manifest_sha256,
            "reportSha256": seal.get("reportSha256") if seal else None,
            "evidenceIndexSha256": seal.get("evidenceIndexSha256") if seal else None,
            "upstreamStageFIndependentAuditSha256": protocol["upstreamGate"]["independentAuditSha256"],
        },
        "grid": {"verdict": "PASS" if not basin_grid_errors else "FAIL", "errors": basin_grid_errors},
        "physicalFreshness": {
            key: value for key, value in physical.items()
            if key not in {"acceptedByCandidate", "normalizedCandidateResults"}
        },
        "refinementMaterialization": {
            key: value for key, value in refinement.items()
            if key not in {"topologyManifest", "attemptedCandidateIds", "topologyCandidateIds"}
        },
        "positiveBasin": basin,
        "structuralVariation": variation,
        "blockingReasons": sorted(set(blockers)),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Independent Prompt 3 Stage B-Final audit")
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--handoff", type=Path, required=True)
    parser.add_argument("--expected-manifest-sha256", required=True)
    parser.add_argument("--expected-source-commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = audit_stage_b_final(
        args.repo.resolve(), args.handoff.resolve(),
        expected_manifest_sha256=args.expected_manifest_sha256,
        expected_source_commit=args.expected_source_commit,
    )
    write_json(args.output.resolve(), result)
    print(json.dumps({
        "output": str(args.output.resolve()), "verdict": result["verdict"],
        "integrityStatus": result["integrityStatus"],
        "authorizedNextStage": result["authorizedNextStage"],
        "blockingReasons": result["blockingReasons"],
    }, indent=2))
    return 0 if result["authorizedNextStage"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
