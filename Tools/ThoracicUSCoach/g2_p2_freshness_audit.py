from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import defaultdict
from pathlib import Path
from typing import Any

from g2_r6_audit import image_features, q_angle, sha256, visual_distances


PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_p2_freshness_audit_protocol_v1.json"


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=False) + "\n", encoding="utf-8")


def canonical_sha256(path: Path) -> str:
    text = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def position_distance(left: dict[str, Any], right: dict[str, Any]) -> float:
    return math.sqrt(sum((float(left[key]) - float(right[key])) ** 2 for key in ("x", "y", "z")))


def quaternion(value: dict[str, Any]) -> list[float]:
    return [float(value[key]) for key in ("x", "y", "z", "w")]


def record_image_errors(root: Path, records: list[dict[str, Any]]) -> list[str]:
    errors: list[str] = []
    accepted_hashes: set[str] = set()
    for record in records:
        image = root / str(record.get("image", ""))
        if not image.is_file() or sha256(image) != record.get("imageSha256"):
            errors.append(f"record image/hash mismatch: {record.get('recordId')}")
            continue
        if record.get("captureDisposition") == "accepted":
            if record["imageSha256"] in accepted_hashes:
                errors.append(f"duplicate accepted image hash: {record.get('recordId')}")
            accepted_hashes.add(record["imageSha256"])
    return errors


def verify_snapshot(
    root: Path,
    protocol_path: Path,
    *,
    expected_manifest_sha256: str,
    expected_source_commit: str,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], list[str]]:
    errors: list[str] = []
    names = {
        "report": "p2_freshness_report.json",
        "manifest": "capture_input_manifest.json",
        "index": "evidence_index.json",
        "seal": "snapshot_seal.json",
    }
    paths = {key: root / name for key, name in names.items()}
    for key, path in paths.items():
        if not path.is_file():
            errors.append(f"required snapshot file missing: {names[key]}")
    if errors:
        return {}, {}, {}, errors
    report, manifest, index, seal = (read_json(paths[key]) for key in ("report", "manifest", "index", "seal"))
    if report.get("mode") != "P2_IMAGE_POSE_FRESHNESS" or manifest.get("mode") != "P2_IMAGE_POSE_FRESHNESS":
        errors.append("freshness mode mismatch")
    if report.get("protocolId") != "G2_PHASE2_FINAL_SPRINT_P2_V1" or manifest.get("protocolId") != "G2_PHASE2_FINAL_SPRINT_P2_V1":
        errors.append("Prompt 2 protocol identity mismatch")
    if report.get("infrastructureFailure") or not report.get("complete"):
        errors.append("freshness report incomplete or failed")
    if not seal.get("complete") or not seal.get("immutable"):
        errors.append("snapshot seal is not complete and immutable")
    if manifest.get("sourceCommit") != expected_source_commit or seal.get("sourceCommit") != expected_source_commit:
        errors.append("source commit mismatch")
    if sha256(paths["manifest"]) != expected_manifest_sha256 or seal.get("manifestSha256") != expected_manifest_sha256:
        errors.append("capture manifest differs from the pre-outcome frozen hash")
    protocol_hash = canonical_sha256(protocol_path)
    if manifest.get("auditProtocolSha256") != protocol_hash or seal.get("auditProtocolCanonicalSha256") != protocol_hash:
        errors.append("freshness audit protocol hash mismatch")
    if seal.get("sceneCanonicalSha256") != read_json(protocol_path)["frozenIdentities"]["sceneCanonicalSha256"]:
        errors.append("frozen scene identity mismatch")
    if seal.get("evaluatorCanonicalSha256") != read_json(protocol_path)["frozenIdentities"]["evaluatorCanonicalSha256"]:
        errors.append("frozen evaluator identity mismatch")
    entries = index.get("files", [])
    if len(entries) != int(seal.get("indexedFiles", -1)):
        errors.append("seal/index file count mismatch")
    for entry in entries:
        path = (root / entry["path"]).resolve()
        try:
            path.relative_to(root.resolve())
        except ValueError:
            errors.append(f"indexed path escapes snapshot: {entry['path']}")
            continue
        if not path.is_file():
            errors.append(f"indexed file missing: {entry['path']}")
        elif path.stat().st_size != int(entry["bytes"]) or sha256(path) != entry["sha256"]:
            errors.append(f"indexed file mismatch: {entry['path']}")
    for path, field in (
        (paths["index"], "evidenceIndexSha256"),
        (paths["report"], "reportSha256"),
        (paths["manifest"], "manifestSha256"),
    ):
        if sha256(path) != seal.get(field):
            errors.append(f"seal hash mismatch: {field}")
    records = report.get("records", [])
    if len(records) != int(seal.get("recordCount", -1)):
        errors.append("record count mismatch")
    required_record_fields = read_json(protocol_path)["evidenceInterface"]["requiredRecordFields"]
    for record in records:
        missing = [field for field in required_record_fields if field not in record]
        if missing:
            errors.append(
                f"record missing required fields: {record.get('recordId', 'UNKNOWN')}:"
                + ",".join(missing)
            )
    errors.extend(record_image_errors(root, records))
    return report, manifest, seal, errors


def handshake_errors(record: dict[str, Any], rule: dict[str, Any]) -> list[str]:
    missing = [field for field in rule["requiredFieldsPerAcceptedCapture"] if field not in record]
    if missing:
        return ["missing handshake fields: " + ",".join(missing)]
    errors: list[str] = []
    if int(record["producerPoseEpoch"]) != int(record["poseEpoch"]):
        errors.append("producer pose epoch differs from physical pose epoch")
    if int(record["producerUnityFrame"]) <= int(record["poseAppliedUnityFrame"]):
        errors.append("producer frame did not advance after pose application")
    if int(record["producerSamplesPublished"]) < 1:
        errors.append("no explicit producer sample published")
    if record["capturePathMode"] != rule["requiredCapturePathMode"]:
        errors.append("capture path mode mismatch")
    if int(record["unityFrameCount"]) < int(record["producerUnityFrame"]):
        errors.append("capture frame precedes producer frame")
    if int(record["activeAggregatorCount"]) != 0:
        errors.append("direct producer mode requires zero active aggregators")
    if record.get("captureDisposition") != "accepted":
        errors.append("handshake evaluated on non-accepted capture")
    return errors


def pair_metrics(root: Path, records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    features = [(record, image_features(root / record["image"])) for record in records]
    pairs = []
    for index, (left_record, left) in enumerate(features):
        for right_record, right in features[index + 1:]:
            rms, structural = visual_distances(left, right)
            pairs.append({
                "left": left_record["recordId"],
                "right": right_record["recordId"],
                "rms128": rms,
                "structuralDistance": structural,
            })
    return pairs


def _ordered(records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return sorted(records, key=lambda item: (int(item["acquisitionOrder"]), str(item["recordId"])))


def accepted_visit_audit(
    root: Path,
    records: list[dict[str, Any]],
    protocol: dict[str, Any],
) -> dict[str, Any]:
    accepted = _ordered([item for item in records if item.get("captureDisposition") == "accepted"])
    discarded = _ordered([item for item in records if item.get("captureDisposition") == "discarded"])
    discarded_kinds = {
        kind for kind in ("immediate_post_pose", "published_warmup")
        if any(str(item.get("actionRejectionReason", "")).startswith(kind) for item in discarded)
    }
    handshake = {
        item.get("recordId", "UNKNOWN"): handshake_errors(item, protocol["producerPoseHandshake"])
        for item in accepted
    }
    monotonic = bool(accepted)
    try:
        monotonic = monotonic and all(
            int(right["acquisitionOrder"]) > int(left["acquisitionOrder"])
            and int(right["unityFrameCount"]) > int(left["unityFrameCount"])
            and int(right["producerScanSerial"]) > int(left["producerScanSerial"])
            for left, right in zip(accepted, accepted[1:])
        )
    except (KeyError, TypeError, ValueError):
        monotonic = False
    pairs = pair_metrics(root, accepted)
    repeat = protocol["repeatNoise"]
    stable = bool(pairs) and all(
        pair["rms128"] <= float(repeat["maximumAcceptedPairRms128"])
        and pair["structuralDistance"] <= float(repeat["maximumAcceptedPairStructuralDistance"])
        for pair in pairs
    )
    return {
        "accepted": accepted,
        "discardedCount": len(discarded),
        "discardedKinds": sorted(discarded_kinds),
        "requiredWarmupsDiscarded": discarded_kinds == {"immediate_post_pose", "published_warmup"},
        "acceptedCount": len(accepted),
        "handshakeErrors": handshake,
        "handshakePass": bool(accepted) and not any(handshake.values()),
        "monotonicCaptureOrder": monotonic,
        "pairMetrics": pairs,
        "stableFreshPair": stable,
    }


def audit_controls(
    root: Path,
    report: dict[str, Any],
    manifest: dict[str, Any],
    protocol: dict[str, Any],
) -> dict[str, Any]:
    expected_sequence = protocol["mandatoryTransitionSequence"]
    declared_sequence = manifest.get("freshnessControlSequence", [])
    records = [item for item in report.get("records", []) if item.get("sourceKind") == protocol["evidenceInterface"]["controlSourceKind"]]
    visits: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in records:
        visits[str(item.get("visitId"))].append(item)
    visit_order = sorted(visits, key=lambda key: min(int(item["acquisitionOrder"]) for item in visits[key]))
    actual_sequence = [str(visits[key][0].get("controlId")) for key in visit_order]
    gate = protocol["positiveControlGate"]
    results = []
    prior_final: dict[str, Any] | None = None
    for visit_id in visit_order:
        group = visits[visit_id]
        visit = accepted_visit_audit(root, group, protocol)
        accepted = visit.pop("accepted")
        control_id = str(group[0].get("controlId"))
        positions = [position_distance(item["requestedProbePosition"], item["probePosition"]) for item in accepted]
        rotations = [q_angle(quaternion(item["requestedProbeRotation"]), quaternion(item["probeRotation"])) for item in accepted]
        scores = [float(item["score"]["total"]) for item in accepted]
        physical = all(
            item.get("t7Valid") is True and item.get("contactValid") is True
            and item.get("workspaceValid") is True for item in accepted
        )
        labels = all(
            item.get("acceptable") is True and item.get("finalTargetAcceptable") is True
            and float(item["score"]["total"]) >= float(protocol["acceptableScoreFloor"])
            for item in accepted
        )
        transition = None
        transition_pass = prior_final is None
        if prior_final is not None and accepted:
            rms, structural = visual_distances(
                image_features(root / prior_final["image"]), image_features(root / accepted[0]["image"])
            )
            transition = {"rms128": rms, "structuralDistance": structural}
            transition_pass = rms > float(protocol["repeatNoise"]["minimumTransitionRms128"])
        if accepted:
            prior_final = accepted[-1]
        checks = {
            "discardedWarmup": visit["discardedCount"] >= 2 and visit["requiredWarmupsDiscarded"],
            "acceptedFreshCount": visit["acceptedCount"] >= int(gate["minimumAcceptedFreshCapturesPerVisit"]),
            "handshake": visit["handshakePass"],
            "monotonic": visit["monotonicCaptureOrder"],
            "stablePair": visit["stableFreshPair"],
            "posePosition": bool(positions) and max(positions) <= float(gate["maximumPoseRestorePositionErrorMm"]),
            "poseRotation": bool(rotations) and max(rotations) <= float(gate["maximumPoseRestoreAngleErrorDeg"]),
            "physical": physical,
            "positive": labels,
            "scoreStable": bool(scores) and max(scores) - min(scores) <= float(gate["maximumWithinVisitScoreRange"]),
            "differentFromPreviousPose": transition_pass,
        }
        results.append({
            "visitId": visit_id,
            "controlId": control_id,
            "discardedCount": visit["discardedCount"],
            "acceptedCount": visit["acceptedCount"],
            "maximumPositionErrorMm": max(positions, default=None),
            "maximumRotationErrorDeg": max(rotations, default=None),
            "scoreRange": max(scores) - min(scores) if scores else None,
            "acceptedPairMetrics": visit["pairMetrics"],
            "transitionFromPrevious": transition,
            "checks": checks,
            "pass": all(checks.values()),
        })
    controls = {item["controlId"]: item for item in protocol["controls"]}
    attempted = {item["controlId"] for item in results}
    passing = {
        control_id for control_id in attempted
        if all(item["pass"] for item in results if item["controlId"] == control_id)
    }
    families = {controls[item]["independentFamily"] for item in passing if item in controls}
    sequence_pass = declared_sequence == expected_sequence and actual_sequence == expected_sequence
    verdict = (
        sequence_pass
        and attempted == set(controls)
        and len(passing) >= int(gate["minimumControlsPassing"])
        and len(families) >= int(gate["minimumIndependentFamiliesPassing"])
    )
    return {
        "verdict": "PASS" if verdict else "FAIL",
        "declaredSequence": declared_sequence,
        "actualSequence": actual_sequence,
        "sequencePass": sequence_pass,
        "controlsAttempted": sorted(attempted),
        "controlsPassing": sorted(passing),
        "independentFamiliesPassing": sorted(families),
        "visits": results,
    }


def derive_movement_categories(record: dict[str, Any]) -> list[str]:
    categories: list[str] = []
    planned = str(record.get("plannedAction", "")).replace("_", "").upper()
    executed = str(record.get("executedAction", "")).replace("_", "").upper()
    directional = planned if planned in {
        "LATERAL", "MEDIAL", "CRANIAL", "CAUDAL", "ROTATECLOCKWISE", "ROTATECOUNTERCLOCKWISE"
    } else executed
    if directional in {"LATERAL", "MEDIAL"}:
        categories.append("lateral")
    elif directional in {"CRANIAL", "CAUDAL"}:
        categories.append("longitudinal")
    elif directional in {"ROTATECLOCKWISE", "ROTATECOUNTERCLOCKWISE"}:
        categories.append("rotation")
    elif planned == "OBSERVE" or executed == "OBSERVE":
        categories.append("observe")
    if record.get("actionAccepted") is False:
        categories.append("rejected")
    if record.get("actionAccepted") is True and record.get("actionCompleted") is False:
        categories.append("incomplete")
    if record.get("rollbackAttempted") is True:
        categories.append("rollback")
    return sorted(set(categories))


def movement_semantics(category: str, item: dict[str, Any], gate: dict[str, Any]) -> bool:
    common = (
        item.get("actualResultingPoseRecorded") is True
        and isinstance(item.get("plannedAction"), str) and bool(item["plannedAction"])
        and isinstance(item.get("executedAction"), str) and bool(item["executedAction"])
        and isinstance(item.get("actionAccepted"), bool)
        and isinstance(item.get("actionCompleted"), bool)
    )
    if not common:
        return False
    if category == "rejected":
        return item.get("actionAccepted") is False
    if category == "incomplete":
        return item.get("actionAccepted") is True and item.get("actionCompleted") is False
    if category == "rollback":
        try:
            restored = (
                position_distance(item["beforeProbePosition"], item["probePosition"])
                <= float(gate["rollbackMaximumPoseRestorePositionErrorMm"])
                and q_angle(quaternion(item["beforeProbeRotation"]), quaternion(item["probeRotation"]))
                <= float(gate["rollbackMaximumPoseRestoreAngleErrorDeg"])
            )
        except (KeyError, TypeError, ValueError):
            return False
        return (
            item.get("rollbackAttempted") is True
            and item.get("rollbackContactRestored") is True
            and restored
        )
    if category == "observe":
        try:
            return (
                position_distance(item["beforeProbePosition"], item["probePosition"])
                <= float(gate["observeMaximumPoseChangeMm"])
                and q_angle(quaternion(item["beforeProbeRotation"]), quaternion(item["probeRotation"]))
                <= float(gate["observeMaximumPoseChangeDeg"])
            )
        except (KeyError, TypeError, ValueError):
            return False
    return True


def audit_movements(root: Path, report: dict[str, Any], protocol: dict[str, Any]) -> dict[str, Any]:
    records = [item for item in report.get("records", []) if item.get("sourceKind") == protocol["evidenceInterface"]["movementSourceKind"]]
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in records:
        for category in derive_movement_categories(item):
            grouped[category].append(item)
    results = {}
    gate = protocol["movementFreshnessGate"]
    for category in gate["requiredCategories"]:
        category_records = grouped.get(category, [])
        by_visit: dict[str, list[dict[str, Any]]] = defaultdict(list)
        for item in category_records:
            by_visit[str(item.get("visitId"))].append(item)
        visit_results = []
        for visit_id, group in sorted(by_visit.items()):
            visit = accepted_visit_audit(root, group, protocol)
            accepted = visit.pop("accepted")
            checks = {
                "discardedWarmup": visit["discardedCount"] >= 2 and visit["requiredWarmupsDiscarded"],
                "acceptedFreshCount": visit["acceptedCount"] >= int(gate["minimumAcceptedFreshCapturesPerCase"]),
                "handshake": visit["handshakePass"],
                "monotonic": visit["monotonicCaptureOrder"],
                "stablePair": visit["stableFreshPair"],
                "executedSemantics": bool(accepted) and all(
                    movement_semantics(category, item, gate) for item in accepted
                ),
            }
            visit_results.append({
                "visitId": visit_id,
                "checks": checks,
                "pass": all(checks.values()),
                **visit,
            })
        results[category] = {
            "present": bool(visit_results),
            "visitCount": len(visit_results),
            "visits": visit_results,
            "pass": bool(visit_results) and all(item["pass"] for item in visit_results),
        }
    return {
        "verdict": "PASS" if all(item["pass"] for item in results.values()) else "FAIL",
        "categories": results,
    }


def audit(
    repo: Path,
    root: Path,
    *,
    expected_manifest_sha256: str,
    expected_source_commit: str,
) -> dict[str, Any]:
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    report, manifest, seal, errors = verify_snapshot(
        root, protocol_path,
        expected_manifest_sha256=expected_manifest_sha256,
        expected_source_commit=expected_source_commit,
    )
    controls = audit_controls(root, report, manifest, protocol) if not errors else {"verdict": "NOT_EVALUATED"}
    movements = audit_movements(root, report, protocol) if not errors else {"verdict": "NOT_EVALUATED"}
    passed = not errors and controls["verdict"] == "PASS" and movements["verdict"] == "PASS"
    blockers = [*errors]
    if controls["verdict"] != "PASS":
        blockers.append("positive-control transition freshness did not pass")
    if movements["verdict"] != "PASS":
        blockers.append("movement/action freshness did not pass")
    return {
        "schemaVersion": 1,
        "taskId": "G2-P2-INDEPENDENT-IMAGE-POSE-FRESHNESS",
        "verdict": protocol["gate"]["pass"] if passed else protocol["gate"]["fail"],
        "authorizedNextStage": passed,
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
        "controls": controls,
        "movementFreshness": movements,
        "blockingReasons": blockers,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Fail-closed Prompt 2 Unity image-pose freshness audit")
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--handoff", type=Path, required=True)
    parser.add_argument("--expected-manifest-sha256", required=True)
    parser.add_argument("--expected-source-commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    result = audit(
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
    return 0 if result["authorizedNextStage"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
