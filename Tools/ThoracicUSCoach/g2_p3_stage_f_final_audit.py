from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
from pathlib import Path
from typing import Any

from g2_p2_freshness_audit import (
    audit_controls,
    audit_movements,
    canonical_sha256,
    read_json,
    record_capture_path_errors,
    record_image_errors,
    write_json,
)
from g2_r6_audit import sha256


PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_p3_stage_f_final_protocol_v1.json"
PRIOR_PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_p2_freshness_audit_protocol_v1.json"


def canonical_object_sha256(value: Any) -> str:
    payload = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _delta(value: Any, fields: list[str]) -> tuple[float, ...] | None:
    if not isinstance(value, dict):
        return None
    try:
        result = tuple(float(value[field]) for field in fields)
    except (KeyError, TypeError, ValueError):
        return None
    return result if all(math.isfinite(item) for item in result) else None


def _vector_close(left: tuple[float, ...], right: tuple[float, ...], tolerance: float) -> bool:
    return len(left) == len(right) and all(abs(a - b) <= tolerance for a, b in zip(left, right))


def derive_offline_transition(trial: dict[str, Any]) -> dict[str, Any]:
    """Independently replay the frozen fail-closed transition from input/pre-state.

    This implementation deliberately does not consume any reported offline or
    post-transition state. The only compared post-state is exported by the live
    C# feedback-state test.
    """
    history_tail = {
        "issuedAction": trial.get("issuedAction"),
        "executedAction": trial.get("actualExecutedAction"),
        "executedDelta": trial.get("actualDelta"),
        "accepted": True,
        "completed": False,
        "feedbackObservedBeforeAppend": True,
    }
    return {
        "odometryDeltaApplied": trial.get("actualDelta"),
        "postVisitedCellIds": trial.get("preVisitedCellIds"),
        "postBestMemory": trial.get("preBestMemory"),
        "controllerDecision": "ABSTAIN",
        "stopIssued": False,
        "causalHistoryTail": history_tail,
    }


def validate_synthetic_trial(trial: dict[str, Any], protocol: dict[str, Any]) -> list[str]:
    gate = protocol["syntheticFaultSemanticsGate"]
    separation = protocol["evidenceSeparation"]
    errors: list[str] = []
    if trial.get("faultId") != gate["faultId"]:
        errors.append("fault identity mismatch")
    if trial.get("evidenceDomain") != separation["deterministicFaultDomain"]:
        errors.append("synthetic evidence domain mismatch")
    if trial.get("synthetic") is not True or trial.get("faultInjected") is not True:
        errors.append("deterministic fault flags missing")
    if trial.get("decisionSupportEligible") is not False or trial.get("trainingEligible") is not False:
        errors.append("synthetic evidence is not quarantined")
    if "image" in trial or "imageSha256" in trial:
        errors.append("synthetic trial must not contain image evidence")
    if trial.get("actionAccepted") is not True or trial.get("actionCompleted") is not False:
        errors.append("trial is not a deterministic synthetic accepted-but-incomplete input")

    fields = gate["vectorFields"]
    tolerance = float(gate["maximumVectorParityError"])
    issued = _delta(trial.get("issuedDelta"), fields)
    actual = _delta(trial.get("actualDelta"), fields)
    odometry = _delta(trial.get("odometryDeltaApplied"), fields)
    if issued is None or not any(abs(item) > tolerance for item in issued):
        errors.append("issued delta missing or zero")
    if actual is None:
        errors.append("actual delta missing")
    if odometry is None:
        errors.append("odometry delta missing")
    if issued is not None and actual is not None and _vector_close(issued, actual, tolerance):
        errors.append("fault did not prevent the full issued move")
    zero = tuple(0.0 for _ in fields)
    if trial.get("factualResultingDeltaProvided") is not False:
        errors.append("bridge must declare that no factual partial displacement was supplied")
    if actual is not None and not _vector_close(actual, zero, tolerance):
        errors.append("bridge invented a partial displacement absent factual feedback")
    if actual is not None and odometry is not None and not _vector_close(actual, odometry, tolerance):
        errors.append("odometry does not equal actual executed delta")
    if issued is not None and odometry is not None and _vector_close(issued, odometry, tolerance):
        errors.append("odometry incorrectly applied the full issued delta")

    before_visited = trial.get("preVisitedCellIds")
    after_visited = trial.get("postVisitedCellIds")
    if not isinstance(before_visited, list) or not isinstance(after_visited, list):
        errors.append("visited-cell snapshots missing")
    elif before_visited != after_visited:
        errors.append("incomplete action changed visited cells")
    if trial.get("plannedFullMoveTargetCellId") in set(after_visited or []):
        errors.append("planned full-move target cell was falsely visited")

    before_best = trial.get("preBestMemory")
    after_best = trial.get("postBestMemory")
    for value in (before_best, after_best):
        if not isinstance(value, dict) or not value.get("recordId") or len(str(value.get("canonicalSha256", ""))) != 64:
            errors.append("best-memory identity/checksum missing")
            break
    if isinstance(before_best, dict) and isinstance(after_best, dict) and before_best != after_best:
        errors.append("incomplete action corrupted best memory")

    safe_return = trial.get("safeReturn")
    if not isinstance(safe_return, dict):
        errors.append("safe-return evidence missing")
    else:
        if safe_return.get("returnAnchorSource") != "last_confirmed_actual_pose":
            errors.append("safe return is not anchored to confirmed actual pose")
        if safe_return.get("assumesFullIssuedMove") is not False:
            errors.append("safe return assumes the full issued move")
        if safe_return.get("contactValid") is not True or safe_return.get("workspaceValid") is not True:
            errors.append("safe-return assumptions violate contact/workspace")

    if trial.get("controllerDecision") != "ABSTAIN" or trial.get("stopIssued") is not False:
        errors.append("incomplete feedback did not force abstain/no-STOP")

    history = trial.get("causalActionHistory")
    if not isinstance(history, list) or not history:
        errors.append("causal action history missing")
    else:
        tail = history[-1]
        expected = {
            "issuedAction": trial.get("issuedAction"),
            "executedAction": trial.get("actualExecutedAction"),
            "accepted": True,
            "completed": False,
            "feedbackObservedBeforeAppend": True,
        }
        for key, value in expected.items():
            if tail.get(key) != value:
                errors.append(f"causal history mismatch: {key}")
        history_delta = _delta(tail.get("executedDelta"), fields)
        if actual is None or history_delta is None or not _vector_close(actual, history_delta, tolerance):
            errors.append("causal history does not contain actual executed delta")

    if "offlineTransitionSemantics" in trial:
        errors.append("hand-authored offline transition semantics are forbidden")
    offline = derive_offline_transition(trial)
    live = trial.get("liveTransitionSemantics")
    if not isinstance(live, dict):
        errors.append("live C# transition semantics missing")
    elif canonical_object_sha256(offline) != canonical_object_sha256(live):
        errors.append("independent offline replay differs from live C# transition")
    return errors


def _live_test_provenance_errors(
    trial: dict[str, Any],
    protocol: dict[str, Any],
    *,
    root: Path,
    indexed_paths: set[str],
    expected_source_commit: str,
) -> list[str]:
    contracts = protocol["syntheticFaultSemanticsGate"]["liveTestContracts"]
    errors: list[str] = []
    artifacts: dict[str, dict[str, Any]] = {}
    paths: list[str] = []
    for contract_name in ("bridge", "productionReducer"):
        contract = contracts[contract_name]
        evidence = trial.get(contract["evidenceField"])
        if not isinstance(evidence, dict):
            errors.append(f"{contract_name} test provenance missing")
            continue
        required_identity = ["exportedBy", "testName", "result"]
        required_identity.append("assembly" if contract_name == "bridge" else "module")
        for field in required_identity:
            if evidence.get(field) != contract[field]:
                errors.append(f"{contract_name} test provenance mismatch: {field}")
        if evidence.get("sourceCommit") != expected_source_commit:
            errors.append(f"{contract_name} test source commit mismatch")
        relative = str(evidence.get("artifact", "")).replace("\\", "/")
        paths.append(relative)
        if relative not in indexed_paths:
            errors.append(f"{contract_name} test artifact is not indexed")
        artifact_path = (root / relative).resolve()
        try:
            artifact_path.relative_to(root.resolve())
        except ValueError:
            errors.append(f"{contract_name} test artifact escapes snapshot")
            continue
        if not artifact_path.is_file() or sha256(artifact_path) != evidence.get("artifactSha256"):
            errors.append(f"{contract_name} test artifact/hash mismatch")
            continue
        try:
            artifact = read_json(artifact_path)
        except (OSError, ValueError, json.JSONDecodeError):
            errors.append(f"{contract_name} test artifact is not valid JSON")
            continue
        artifacts[contract_name] = artifact
        for field in required_identity:
            if artifact.get(field) != contract[field]:
                errors.append(f"{contract_name} artifact identity mismatch: {field}")
        if artifact.get("sourceCommit") != expected_source_commit:
            errors.append(f"{contract_name} artifact source commit mismatch")
    if len(paths) == 2 and paths[0] == paths[1]:
        errors.append("bridge and production-reducer artifacts must be distinct")

    bridge = artifacts.get("bridge", {}).get("bridgeFeedback")
    if not isinstance(bridge, dict):
        errors.append("bridge artifact feedback payload missing")
    else:
        bridge_delta = _delta(bridge.get("actualDelta"), protocol["syntheticFaultSemanticsGate"]["vectorFields"])
        zero = tuple(0.0 for _ in protocol["syntheticFaultSemanticsGate"]["vectorFields"])
        tolerance = float(protocol["syntheticFaultSemanticsGate"]["maximumVectorParityError"])
        if bridge.get("accepted") is not True or bridge.get("completed") is not False:
            errors.append("bridge artifact does not serialize accepted-incomplete feedback")
        if bridge.get("factualResultingDeltaProvided") is not False:
            errors.append("bridge artifact claims nonexistent partial-displacement feedback")
        if bridge_delta is None or not _vector_close(bridge_delta, zero, tolerance):
            errors.append("bridge artifact invented partial displacement")

    production_trial = artifacts.get("productionReducer", {}).get("trial")
    if not isinstance(production_trial, dict):
        errors.append("production-reducer artifact trial payload missing")
    else:
        compared_fields = (
            "faultId", "issuedAction", "actualExecutedAction", "actionAccepted",
            "actionCompleted", "issuedDelta", "actualDelta", "factualResultingDeltaProvided",
            "preVisitedCellIds", "plannedFullMoveTargetCellId", "preBestMemory",
            "liveTransitionSemantics",
        )
        for field in compared_fields:
            if canonical_object_sha256(production_trial.get(field)) != canonical_object_sha256(trial.get(field)):
                errors.append(f"production-reducer artifact/trial mismatch: {field}")
    return errors


def audit_synthetic_trials(
    report: dict[str, Any],
    protocol: dict[str, Any],
    *,
    manifest: dict[str, Any] | None = None,
    root: Path | None = None,
    indexed_paths: set[str] | None = None,
    expected_source_commit: str | None = None,
) -> dict[str, Any]:
    trials = report.get(protocol["snapshotInterface"]["syntheticContainer"], [])
    if not isinstance(trials, list):
        trials = []
    results = []
    for item in trials:
        if not isinstance(item, dict):
            results.append({"trialId": None, "errors": ["synthetic trial is not an object"]})
            continue
        errors = validate_synthetic_trial(item, protocol)
        if manifest is not None:
            plan = manifest.get("syntheticFaultPlan")
            if not isinstance(plan, dict):
                errors.append("prefrozen synthetic fault plan missing")
            else:
                plan_fields = (
                    "faultId", "trialId", "issuedAction", "actualExecutedAction",
                    "issuedDelta", "actualDelta", "preVisitedCellIds",
                    "plannedFullMoveTargetCellId", "preBestMemory",
                )
                for field in plan_fields:
                    if canonical_object_sha256(item.get(field)) != canonical_object_sha256(plan.get(field)):
                        errors.append(f"synthetic trial differs from prefrozen plan: {field}")
        if root is not None and indexed_paths is not None and expected_source_commit is not None:
            errors.extend(_live_test_provenance_errors(
                item, protocol, root=root, indexed_paths=indexed_paths,
                expected_source_commit=expected_source_commit,
            ))
        results.append({"trialId": item.get("trialId"), "errors": errors})
    minimum = int(protocol["syntheticFaultSemanticsGate"]["minimumTrials"])
    passed = len(results) >= minimum and all(not item["errors"] for item in results)
    return {"verdict": "PASS" if passed else "FAIL", "trialCount": len(results), "trials": results}


def _prior_adapter(repo: Path, protocol: dict[str, Any]) -> tuple[dict[str, Any], list[str]]:
    path = repo / PRIOR_PROTOCOL
    errors: list[str] = []
    if not path.is_file():
        return {}, ["prior frozen freshness protocol missing"]
    expected = protocol["frozenIdentities"]["priorFreshnessProtocolCanonicalSha256"]
    if canonical_sha256(path) != expected:
        errors.append("prior frozen freshness protocol hash mismatch")
    adapted = copy.deepcopy(read_json(path))
    adapted["movementFreshnessGate"]["requiredCategories"] = list(
        protocol["realUnityPhysicalGate"]["requiredPhysicalMovementCategories"]
    )
    adapted["mandatoryTransitionSequence"] = list(
        protocol["realUnityPhysicalGate"]["requiredControlSequence"]
    )
    return adapted, errors


def verify_snapshot(
    repo: Path,
    root: Path,
    protocol: dict[str, Any],
    *,
    expected_manifest_sha256: str,
    expected_source_commit: str,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], list[str]]:
    errors: list[str] = []
    file_names = {
        "manifest": "stage_f_final_manifest.json",
        "report": "stage_f_final_report.json",
        "index": "evidence_index.json",
        "seal": "snapshot_seal.json",
    }
    paths = {key: root / name for key, name in file_names.items()}
    for key, path in paths.items():
        if not path.is_file():
            errors.append(f"required snapshot file missing: {file_names[key]}")
    if errors:
        return {}, {}, {}, errors
    manifest, report, index, seal = (read_json(paths[key]) for key in ("manifest", "report", "index", "seal"))
    identity = protocol["protocolId"]
    mode = protocol["mode"]
    if any(value.get("protocolId") != identity for value in (manifest, report)):
        errors.append("Stage F-Final protocol identity mismatch")
    if any(value.get("mode") != mode for value in (manifest, report)):
        errors.append("Stage F-Final mode mismatch")
    if report.get("complete") is not True or report.get("infrastructureFailure"):
        errors.append("Stage F-Final report incomplete or failed")
    if seal.get("complete") is not True or seal.get("immutable") is not True or seal.get("failureSnapshot") is not False:
        errors.append("snapshot seal is not a complete immutable outcome")
    if manifest.get("sourceCommit") != expected_source_commit or seal.get("sourceCommit") != expected_source_commit:
        errors.append("source commit mismatch")
    if sha256(paths["manifest"]) != expected_manifest_sha256 or seal.get("manifestSha256") != expected_manifest_sha256:
        errors.append("manifest differs from prefrozen hash")
    protocol_hash = canonical_sha256(repo / PROTOCOL)
    if manifest.get("auditProtocolCanonicalSha256") != protocol_hash or seal.get("auditProtocolCanonicalSha256") != protocol_hash:
        errors.append("audit protocol hash mismatch")
    if manifest.get("physicalControlSequence") != protocol["realUnityPhysicalGate"]["requiredControlSequence"]:
        errors.append("physical control sequence mismatch")
    plan = manifest.get("syntheticFaultPlan")
    if not isinstance(plan, dict) or plan.get("faultId") != protocol["syntheticFaultSemanticsGate"]["faultId"]:
        errors.append("synthetic fault plan mismatch")
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
        (paths["report"], "reportSha256"),
        (paths["index"], "evidenceIndexSha256"),
    ):
        if sha256(path) != seal.get(field):
            errors.append(f"seal hash mismatch: {field}")

    records = report.get("records", [])
    trials = report.get(protocol["snapshotInterface"]["syntheticContainer"], [])
    if not isinstance(records, list):
        errors.append("physical records container missing")
        records = []
    if not isinstance(trials, list):
        errors.append("synthetic trials container missing")
        trials = []
    if len(records) != int(seal.get("physicalRecordCount", -1)):
        errors.append("physical record count mismatch")
    if len(trials) != int(seal.get("syntheticTrialCount", -1)):
        errors.append("synthetic trial count mismatch")

    adapted, inherited_errors = _prior_adapter(repo, protocol)
    errors.extend(inherited_errors)
    if adapted:
        required = adapted["evidenceInterface"]["requiredRecordFields"]
        path_rule = adapted["producerPoseHandshake"]
        physical_domain = protocol["evidenceSeparation"]["realUnityPhysicalDomain"]
        for record in records:
            missing = [field for field in required if field not in record]
            if missing:
                errors.append(f"physical record fields missing: {record.get('recordId')}:{','.join(missing)}")
            if record.get("evidenceDomain") != physical_domain or record.get("synthetic") is not False or record.get("faultInjected") is not False:
                errors.append(f"physical evidence-domain contamination: {record.get('recordId')}")
            errors.extend(
                f"{record.get('recordId')}:{message}"
                for message in record_capture_path_errors(record, path_rule)
            )
        errors.extend(record_image_errors(root, records))
    return report, manifest, seal, errors


def _movement_signal(movements: dict[str, Any], key: str) -> bool:
    categories = movements.get("categories", {})
    if not categories:
        return False
    for category in categories.values():
        if not category.get("present"):
            return False
        for visit in category.get("visits", []):
            checks = visit.get("checks", {})
            if key == "feedback":
                if checks.get("executedSemantics") is not True:
                    return False
            elif any(checks.get(name) is not True for name in (
                "discardedWarmup", "acceptedFreshCount", "handshake", "monotonic", "stablePair"
            )):
                return False
    return True


def audit_stage_f_final(
    repo: Path,
    root: Path,
    *,
    expected_manifest_sha256: str,
    expected_source_commit: str,
) -> dict[str, Any]:
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    report, manifest, seal, errors = verify_snapshot(
        repo, root, protocol,
        expected_manifest_sha256=expected_manifest_sha256,
        expected_source_commit=expected_source_commit,
    )
    adapted, inherited_errors = _prior_adapter(repo, protocol)
    if inherited_errors:
        errors.extend(message for message in inherited_errors if message not in errors)
    if errors:
        controls = {"verdict": "NOT_EVALUATED"}
        movements = {"verdict": "NOT_EVALUATED", "categories": {}}
    else:
        physical_manifest = dict(manifest)
        physical_manifest["freshnessControlSequence"] = manifest.get("physicalControlSequence")
        controls = audit_controls(root, report, physical_manifest, adapted)
        movements = audit_movements(root, report, adapted)
    indexed_paths = {
        str(item.get("path", "")).replace("\\", "/")
        for item in read_json(root / "evidence_index.json").get("files", [])
    } if (root / "evidence_index.json").is_file() else set()
    synthetic = audit_synthetic_trials(
        report, protocol, manifest=manifest, root=root, indexed_paths=indexed_paths,
        expected_source_commit=expected_source_commit,
    ) if report else {
        "verdict": "NOT_EVALUATED", "trialCount": 0, "trials": []
    }
    real_freshness = not errors and controls["verdict"] == "PASS" and _movement_signal(movements, "freshness")
    real_feedback = not errors and _movement_signal(movements, "feedback")
    capture_integration = not errors and real_freshness
    separation = not any("contamination" in item for item in errors) and bool(report)
    historical = report.get("historicalF3Verdict") if report else None
    historical_preserved = historical == protocol["historicalF3"]["verdict"]
    signals = {
        "REAL_IMAGE_POSE_FRESHNESS": "PASS" if real_freshness else "FAIL",
        "REAL_ACTION_FEEDBACK_INTEGRITY": "PASS" if real_feedback else "FAIL",
        "INCOMPLETE_FAULT_INJECTION": synthetic["verdict"],
        "CAPTURE_PATH_INTEGRATION": "PASS" if capture_integration else "FAIL",
        "HISTORICAL_F3_VERDICT": historical,
    }
    final_engineering = (
        not errors and separation and historical_preserved
        and all(signals[key] == "PASS" for key in (
            "REAL_IMAGE_POSE_FRESHNESS", "REAL_ACTION_FEEDBACK_INTEGRITY",
            "INCOMPLETE_FAULT_INJECTION", "CAPTURE_PATH_INTEGRATION",
        ))
    )
    signals["FINAL_CAPTURE_ENGINEERING_PASS"] = final_engineering
    blockers = list(errors)
    blockers.extend(
        key for key in (
            "REAL_IMAGE_POSE_FRESHNESS", "REAL_ACTION_FEEDBACK_INTEGRITY",
            "INCOMPLETE_FAULT_INJECTION", "CAPTURE_PATH_INTEGRATION",
        ) if signals[key] != "PASS"
    )
    if not historical_preserved:
        blockers.append("historical F3 verdict was altered or omitted")
    return {
        "schemaVersion": 1,
        "taskId": "G2-P3-STAGE-F-FINAL-INDEPENDENT-AUDIT",
        "verdict": protocol["verdicts"]["pass"] if final_engineering else protocol["verdicts"]["fail"],
        "authorizedNextStage": final_engineering,
        "integrityStatus": "PASS" if not errors else "FAIL",
        "protocol": PROTOCOL,
        "protocolCanonicalSha256": canonical_sha256(protocol_path),
        "provenance": {
            "handoff": str(root),
            "sourceCommit": expected_source_commit,
            "manifestSha256": expected_manifest_sha256,
            "reportSha256": seal.get("reportSha256") if seal else None,
            "evidenceIndexSha256": seal.get("evidenceIndexSha256") if seal else None,
        },
        "prospectiveSignals": signals,
        "evidenceDomainSeparation": "PASS" if separation else "FAIL",
        "realControls": controls,
        "realMovements": movements,
        "syntheticFaultSemantics": synthetic,
        "blockingReasons": sorted(set(blockers)),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Independent Prompt 3 Stage F-Final audit")
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--handoff", type=Path, required=True)
    parser.add_argument("--expected-manifest-sha256", required=True)
    parser.add_argument("--expected-source-commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    result = audit_stage_f_final(
        args.repo.resolve(), args.handoff.resolve(),
        expected_manifest_sha256=args.expected_manifest_sha256,
        expected_source_commit=args.expected_source_commit,
    )
    write_json(args.output.resolve(), result)
    print(json.dumps({
        "output": str(args.output.resolve()),
        "verdict": result["verdict"],
        "integrityStatus": result["integrityStatus"],
        "authorizedNextStage": result["authorizedNextStage"],
        "blockingReasons": result["blockingReasons"],
    }, indent=2))
    return 0 if result["authorizedNextStage"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
