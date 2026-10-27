from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from g2_p2_basin_audit import candidate_passes, connected_positive_components
from g2_p2_freshness_audit import canonical_sha256, read_json, write_json
from g2_p3_stage_b_final_audit import (
    PROTOCOL,
    _adapt_basin_protocol,
    audit_physical_records,
    validate_p3_grid,
)
from g2_r6_audit import sha256


def _verify_index(root: Path, index: dict[str, Any], expected_count: int) -> list[str]:
    errors: list[str] = []
    entries = index.get("files", [])
    if not isinstance(entries, list) or len(entries) != expected_count:
        return ["failure snapshot index count mismatch"]
    for entry in entries:
        path = (root / str(entry.get("path", ""))).resolve()
        try:
            path.relative_to(root.resolve())
        except ValueError:
            errors.append(f"indexed path escapes failure snapshot: {entry.get('path')}")
            continue
        if not path.is_file():
            errors.append(f"indexed failure evidence missing: {entry.get('path')}")
        elif path.stat().st_size != int(entry.get("bytes", -1)) or sha256(path) != entry.get("sha256"):
            errors.append(f"indexed failure evidence mismatch: {entry.get('path')}")
    return errors


def summarize_base(
    manifest: dict[str, Any],
    normalized: list[dict[str, Any]],
    adapted: dict[str, Any],
    controls: list[str],
) -> dict[str, Any]:
    by_result = {str(item.get("candidateId")): item for item in normalized}
    candidates = manifest.get("mapCandidates", [])
    passing = {
        str(item.get("candidateId")) for item in candidates
        if str(item.get("candidateId")) in by_result
        and candidate_passes(by_result[str(item.get("candidateId"))], adapted)
    }
    components = connected_positive_components(passing, manifest.get("neighborPairs", []), candidates)
    per_anchor: dict[str, Any] = {}
    missing_refinement_centers: list[str] = []
    for control in controls:
        declared = [item for item in candidates if item.get("anchorControlId") == control]
        eligible_center_ids = {
            str(item.get("candidateId")) for item in declared
            if item.get("stage") in {"coarse", "wider"}
        }
        passing_centers = sorted(passing & eligible_center_ids)
        accepted = sum(
            int(by_result.get(str(item.get("candidateId")), {}).get("acceptedFreshCaptures", 0)) > 0
            for item in declared
        )
        if not passing_centers:
            missing_refinement_centers.append(control)
        per_anchor[control] = {
            "declaredBaseCandidates": len(declared),
            "acceptedBaseCandidates": accepted,
            "strictPassingBaseCandidates": len(passing & {str(item.get('candidateId')) for item in declared}),
            "passingCoarseWiderCandidates": len(passing_centers),
            "passingCoarseWiderCandidateIds": passing_centers,
        }
    minimum = int(adapted["frozenThresholds"]["minimumConnectedAcceptableCells"])
    return {
        "strictPassingBaseCandidateIds": sorted(passing),
        "strictPassingBaseCandidateCount": len(passing),
        "positiveComponents": components,
        "eligibleBaseComponents": [component for component in components if len(component) >= minimum],
        "perAnchor": per_anchor,
        "missingDeterministicRefinementCenterAnchors": missing_refinement_centers,
    }


def audit_failure_snapshot(
    repo: Path,
    root: Path,
    *,
    expected_source_commit: str,
    expected_manifest_sha256: str,
    expected_seal_sha256: str,
) -> dict[str, Any]:
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    report_path = root / "stage_b_failure_report.json"
    index_path = root / "failure_evidence_index.json"
    seal_path = root / "snapshot_seal.json"
    errors: list[str] = []
    for path in (report_path, index_path, seal_path):
        if not path.is_file():
            errors.append(f"required failure snapshot file missing: {path.name}")
    if errors:
        return {"verdict": "POSITIVE_BASIN_FAIL", "integrityStatus": "FAIL", "errors": errors}
    report, index, seal = read_json(report_path), read_json(index_path), read_json(seal_path)
    if sha256(seal_path) != expected_seal_sha256:
        errors.append("failure snapshot seal hash mismatch")
    if sha256(report_path) != seal.get("reportSha256") or sha256(index_path) != seal.get("evidenceIndexSha256"):
        errors.append("failure report/index is not bound by seal")
    if seal.get("complete") is not True or seal.get("immutable") is not True or seal.get("failureSnapshot") is not True:
        errors.append("failure snapshot identity invalid")
    if seal.get("scientificComplete") is not False or seal.get("refinementComplete") is not False:
        errors.append("failed Stage B snapshot falsely claims scientific/refinement completion")
    if seal.get("sourceCommit") != expected_source_commit or report.get("sourceCommit") != expected_source_commit:
        errors.append("failure snapshot source mismatch")
    if seal.get("manifestSha256") != expected_manifest_sha256 or report.get("manifestSha256") != expected_manifest_sha256:
        errors.append("failure snapshot manifest identity mismatch")
    protocol_hash = canonical_sha256(protocol_path)
    if seal.get("auditProtocolCanonicalSha256") != protocol_hash or report.get("auditProtocolCanonicalSha256") != protocol_hash:
        errors.append("failure snapshot audit protocol mismatch")
    errors.extend(_verify_index(root, index, int(seal.get("indexedFiles", -1))))

    raw_root = root / "raw_partial"
    manifest_path = raw_root / "stage_b_final_manifest.json"
    runtime_path = raw_root / "stage_b_runtime_report.json"
    coarse_path = raw_root / "stage_b_coarse_snapshot.json"
    for path in (manifest_path, runtime_path, coarse_path):
        if not path.is_file():
            errors.append(f"required raw failure evidence missing: {path.name}")
    if errors:
        return {"verdict": "POSITIVE_BASIN_FAIL", "integrityStatus": "FAIL", "errors": sorted(set(errors))}
    if sha256(manifest_path) != expected_manifest_sha256:
        errors.append("raw prefrozen manifest hash mismatch")
    if sha256(runtime_path) != seal.get("rawRuntimeReportSha256"):
        errors.append("raw runtime report hash mismatch")
    if sha256(coarse_path) != seal.get("coarseSnapshotSha256"):
        errors.append("coarse snapshot hash mismatch")
    manifest, runtime, coarse = read_json(manifest_path), read_json(runtime_path), read_json(coarse_path)
    adapted, reference_errors = _adapt_basin_protocol(repo, protocol)
    errors.extend(reference_errors)
    grid_errors = validate_p3_grid(manifest, protocol, adapted) if adapted else ["base grid not evaluated"]
    physical = audit_physical_records(repo, raw_root, runtime, protocol)
    substantive_physical_errors = [
        value for value in physical.get("errors", [])
        if not value.startswith("candidate reported metric differs from independent derivation:")
    ]
    normalized = physical.get("normalizedCandidateResults", [])
    base = summarize_base(
        manifest, normalized, adapted, protocol["fixedGridContract"]["historicalControlIds"]
    ) if adapted else {}

    declared_ids = {str(item.get("candidateId")) for item in manifest.get("mapCandidates", [])}
    attempted_ids = {str(value) for value in runtime.get("attemptedCandidateIds", [])}
    factual_checks = {
        "baseCaptureComplete": report.get("baseCaptureComplete") is True,
        "all212BaseCandidatesAttempted": len(declared_ids) == 212 and attempted_ids == declared_ids,
        "recordAccounting": (
            int(report.get("physicalRecordCount", -1)) == len(runtime.get("records", [])) == 848
            and int(report.get("acceptedRecordCount", -1)) == 424
            and int(report.get("discardedRecordCount", -1)) == 424
        ),
        "noSubstantivePhysicalErrors": not substantive_physical_errors,
        "noRefinementOutcome": (
            report.get("refinementComplete") is False
            and int(report.get("attemptedRefinementCandidates", -1)) == 0
            and not (raw_root / "refinement_selection_manifest.json").exists()
        ),
        "failureReasonMatchesMissingCenter": bool(base.get("missingDeterministicRefinementCenterAnchors"))
            and "no passing coarse/wider candidate" in str(report.get("failureReason", "")),
    }
    scientific_fail = bool(base.get("missingDeterministicRefinementCenterAnchors"))
    integrity_pass = not errors and not grid_errors and all(factual_checks.values())
    return {
        "schemaVersion": 1,
        "taskId": "G2-P3-STAGE-B-FINAL-INDEPENDENT-FAILURE-AUDIT",
        "verdict": "POSITIVE_BASIN_FAIL",
        "authorizedNextStage": False,
        "finalClassification": "PHASE2_NO_GO",
        "integrityStatus": "PASS" if integrity_pass else "FAIL",
        "failureAttribution": "INSUFFICIENT_POSITIVE_BASIN_COVERAGE_ON_FROZEN_FIXED_ANATOMY_CONTROLS"
            if scientific_fail else "INCONCLUSIVE_FAILURE_ATTRIBUTION",
        "provenance": {
            "handoff": str(root),
            "jobId": report.get("jobId"),
            "sourceCommit": expected_source_commit,
            "manifestSha256": expected_manifest_sha256,
            "snapshotSealSha256": expected_seal_sha256,
            "protocolCanonicalSha256": protocol_hash,
        },
        "factualChecks": factual_checks,
        "baseEvidence": base,
        "physicalEvidence": {
            "recordCount": len(runtime.get("records", [])),
            "acceptedRecordCount": sum(item.get("captureDisposition") == "accepted" for item in runtime.get("records", [])),
            "discardedRecordCount": sum(item.get("captureDisposition") == "discarded" for item in runtime.get("records", [])),
            "substantiveErrors": substantive_physical_errors,
        },
        "gridErrors": grid_errors,
        "integrityErrors": sorted(set(errors)),
        "stageStatus": {
            "stageFEngineering": "PASS",
            "stageBPositiveBasin": "FAIL",
            "stageCSafeTargetIndependentReachability": "BLOCKED_NOT_RUN",
            "stageDReadinessData": "BLOCKED_NOT_RUN",
            "recognitionModel": "BLOCKED_NOT_TRAINED",
            "pairedEndToEndPoc": "BLOCKED_NOT_RUN",
        },
        "interpretation": (
            "Stage F established capture/action-feedback engineering integrity. Stage B then failed its frozen "
            "positive-basin prerequisite because two of four consumed control anchors had no passing "
            "coarse/wider refinement center. This is a bounded fixed-anatomy coverage failure, not evidence "
            "of fundamental ultrasound observability impossibility. No threshold, grid, retry, Stage C/D, "
            "training, or product claim is authorized."
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Audit immutable Prompt 3 Stage B failure evidence")
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--handoff", type=Path, required=True)
    parser.add_argument("--expected-source-commit", required=True)
    parser.add_argument("--expected-manifest-sha256", required=True)
    parser.add_argument("--expected-seal-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = audit_failure_snapshot(
        args.repo.resolve(), args.handoff.resolve(),
        expected_source_commit=args.expected_source_commit,
        expected_manifest_sha256=args.expected_manifest_sha256,
        expected_seal_sha256=args.expected_seal_sha256,
    )
    write_json(args.output.resolve(), result)
    print(json.dumps({
        "output": str(args.output.resolve()), "verdict": result["verdict"],
        "integrityStatus": result["integrityStatus"],
        "finalClassification": result.get("finalClassification"),
    }, indent=2))
    return 0 if result["integrityStatus"] == "PASS" and result["verdict"] == "POSITIVE_BASIN_FAIL" else 1


if __name__ == "__main__":
    raise SystemExit(main())
