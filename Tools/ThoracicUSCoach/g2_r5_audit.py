from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import math
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

import numpy as np
from PIL import Image
from jsonschema import Draft202012Validator, FormatChecker

from g2_r4_p2_lineage import collect as collect_r4


PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_r5_audit_protocol_v1.json"
COORDINATOR_PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_r5_protocol_v1.json"
HISTORICAL_PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_r4_p2_protocol_v1.json"
HISTORICAL_LINEAGE = "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2r_20261025/r1_complete_lineage_graph.json"
OUTPUT = "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r5_20261026"
SCHEMA = "Tools/ThoracicUSCoach/experiments/g2_r5_evidence_schema_v1.json"
IDENTITY = "Tools/ThoracicUSCoach/experiments/g2_r5_contract_identity_v1.json"


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical_text_sha256(path: Path) -> str:
    text = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def merkle_equivalent(values: Iterable[str]) -> str:
    return hashlib.sha256("".join(sorted(values)).encode("utf-8")).hexdigest()


def position_distance(left: dict[str, float], right: dict[str, float]) -> float:
    return math.sqrt(sum((float(left[key]) - float(right[key])) ** 2 for key in ("x", "y", "z")))


def rotation_distance(left: dict[str, float], right: dict[str, float]) -> float:
    dot = abs(sum(float(left[key]) * float(right[key]) for key in ("x", "y", "z", "w")))
    return math.degrees(2.0 * math.acos(min(1.0, max(-1.0, dot))))


def coverage_overlap(
    left: list[dict[str, Any]], right: list[dict[str, Any]], *, position_mm: float, angle_deg: float
) -> tuple[float, float]:
    if not left or not right:
        return 0.0, 0.0

    def fraction(source: list[dict[str, Any]], target: list[dict[str, Any]]) -> float:
        return sum(
            any(
                position_distance(item["probePosition"], other["probePosition"]) <= position_mm
                and rotation_distance(item["probeRotation"], other["probeRotation"]) <= angle_deg
                for other in target
            )
            for item in source
        ) / len(source)

    return fraction(left, right), fraction(right, left)


class UnionFind:
    def __init__(self, values: Iterable[str]) -> None:
        self.parent = {value: value for value in values}

    def find(self, value: str) -> str:
        while self.parent[value] != value:
            self.parent[value] = self.parent[self.parent[value]]
            value = self.parent[value]
        return value

    def union(self, left: str, right: str) -> None:
        left, right = self.find(left), self.find(right)
        if left != right:
            self.parent[right] = left

    def components(self) -> list[list[str]]:
        groups: dict[str, list[str]] = defaultdict(list)
        for value in sorted(self.parent):
            groups[self.find(value)].append(value)
        return sorted((sorted(group) for group in groups.values()), key=tuple)


def grayscale_128(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        pixels = image.convert("L").resize((128, 128), Image.Resampling.BILINEAR)
        return np.asarray(pixels, dtype=np.float32).reshape(-1) / 255.0


def rms(left: np.ndarray, right: np.ndarray) -> float:
    return float(np.sqrt(np.mean((left - right) ** 2)))


def record_as_pose(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "probePosition": row["poseTrace"]["afterPosition"],
        "probeRotation": row["poseTrace"]["afterRotation"],
    }


def spatial_component_audit(
    historical: dict[str, Any], records: list[dict[str, Any]], audit_protocol: dict[str, Any], *, conservative: bool
) -> dict[str, Any]:
    routes: dict[str, list[dict[str, Any]]] = defaultdict(list)
    route_start_proposals: dict[str, set[str]] = defaultdict(set)
    for row in records:
        routes[str(row["routeId"])].append(record_as_pose(row))
        route_start_proposals[str(row["routeId"])].add(str(row["startProposalId"]))
    historical_routes: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in historical["frames"]:
        historical_routes[str(row["caseId"])].append(row)
    historical_cases = {str(row["caseId"]): row for row in historical["trajectories"]}
    identifiers = [f"historical:{key}" for key in historical_routes] + [f"scout:{key}" for key in routes]
    union = UnionFind(identifiers)
    ancestry_edges = []
    scout_route_ids = sorted(routes)
    for left_index, left in enumerate(scout_route_ids):
        for right in scout_route_ids[left_index + 1:]:
            shared = sorted(route_start_proposals[left] & route_start_proposals[right])
            if shared:
                union.union(f"scout:{left}", f"scout:{right}")
                ancestry_edges.append({"left": left, "right": right, "sharedStartProposalIds": shared})

    component_field = "conservativeComponent" if conservative else "primaryComponent"
    by_historical_component: dict[str, list[str]] = defaultdict(list)
    for case_id, case in historical_cases.items():
        by_historical_component[str(case[component_field])].append(case_id)
    for members in by_historical_component.values():
        for member in members[1:]:
            union.union(f"historical:{members[0]}", f"historical:{member}")

    primary = audit_protocol["spatialNovelty"]["primary"]
    threshold = float(primary["minimumBidirectionalCoverageFraction"])
    combined = {**{f"historical:{key}": value for key, value in historical_routes.items()},
                **{f"scout:{key}": value for key, value in routes.items()}}
    identifiers = sorted(combined)
    overlap_edges = []
    for left_index, left in enumerate(identifiers):
        for right in identifiers[left_index + 1:]:
            if left.startswith("historical:") and right.startswith("historical:"):
                continue
            fractions = coverage_overlap(
                combined[left], combined[right],
                position_mm=float(primary["poseMatchPositionMm"]),
                angle_deg=float(primary["poseMatchAngleDeg"]),
            )
            if min(fractions) >= threshold:
                union.union(left, right)
                overlap_edges.append({"left": left, "right": right, "coverageLeft": fractions[0], "coverageRight": fractions[1]})

    near_start_edges = []
    if conservative:
        sensitivity = audit_protocol["spatialNovelty"]["conservativeSensitivity"]
        starts: dict[str, dict[str, Any]] = {}
        for case_id, case in historical_cases.items():
            starts[f"historical:{case_id}"] = {
                "probePosition": case["sourcePose"]["position"], "probeRotation": case["sourcePose"]["rotation"]}
        for route_id, rows in routes.items():
            starts[f"scout:{route_id}"] = rows[0]
        start_ids = sorted(starts)
        for left_index, left in enumerate(start_ids):
            for right in start_ids[left_index + 1:]:
                if left.startswith("historical:") and right.startswith("historical:"):
                    continue
                pos = position_distance(starts[left]["probePosition"], starts[right]["probePosition"])
                rot = rotation_distance(starts[left]["probeRotation"], starts[right]["probeRotation"])
                if pos <= float(sensitivity["nearStartPositionMm"]) and rot <= float(sensitivity["nearStartAngleDeg"]):
                    union.union(left, right)
                    near_start_edges.append({"left": left, "right": right, "positionMm": pos, "rotationDeg": rot})

    components = []
    route_to_component: dict[str, str] = {}
    for index, members in enumerate(union.components()):
        scout = [member.removeprefix("scout:") for member in members if member.startswith("scout:")]
        if not scout:
            continue
        identifier = ("conservative" if conservative else "primary") + f"_r5_component_{index:02d}"
        historical_members = [member.removeprefix("historical:") for member in members if member.startswith("historical:")]
        for route_id in scout:
            route_to_component[route_id] = identifier
        components.append({
            "componentId": identifier,
            "scoutRouteIds": sorted(scout),
            "historicalCaseIds": sorted(historical_members),
            "spatiallyNovel": not historical_members,
        })
    return {
        "components": components, "routeToComponent": route_to_component,
        "ancestryEdges": ancestry_edges, "trajectoryOverlapEdges": overlap_edges, "nearStartEdges": near_start_edges,
    }


def nearest_reference_rms(reference: np.ndarray, query: np.ndarray) -> np.ndarray:
    # Squared Euclidean distance divided by dimensionality equals RMS squared.
    reference_norm = np.sum(reference * reference, axis=1)[:, None]
    query_norm = np.sum(query * query, axis=1)[None, :]
    squared = np.maximum(reference_norm + query_norm - 2.0 * (reference @ query.T), 0.0)
    return np.sqrt(np.min(squared, axis=0) / reference.shape[1])


def largest_pairwise_separated_subset(
    component_ids: list[str], representatives: dict[str, np.ndarray], cutoff: float
) -> list[str]:
    for size in range(len(component_ids), 0, -1):
        for subset in itertools.combinations(sorted(component_ids), size):
            if all(rms(representatives[left], representatives[right]) > cutoff for left, right in itertools.combinations(subset, 2)):
                return list(subset)
    return []


def classify_scout_decision(
    *, positive: int, negative: int, union: int, hard_negative: int,
    justified_route: bool, exhausted: bool, spatially_novel: int, visually_novel: int,
) -> tuple[str, bool]:
    go = positive >= 3 and negative >= 3 and union >= 6 and hard_negative >= 1 and justified_route
    demonstrated_collapse = exhausted and (spatially_novel < 3 or visually_novel < 3)
    if go:
        return "GO_BOUNDED_DEVELOPMENT_CAPTURE", demonstrated_collapse
    if demonstrated_collapse:
        return "INSUFFICIENT_DIVERSITY_ON_CURRENT_ANATOMY", demonstrated_collapse
    return "FEASIBILITY_INCONCLUSIVE", demonstrated_collapse


def visual_component_audit(
    repo: Path, snapshot_root: Path, historical_evidence_root: Path, historical: dict[str, Any], records: list[dict[str, Any]],
    primary: dict[str, Any], audit_protocol: dict[str, Any]
) -> dict[str, Any]:
    evidence_root = historical_evidence_root.resolve()
    history_paths = [evidence_root / str(row["image"]).replace("\\", "/") for row in historical["frames"]]
    missing_or_changed = [
        str(path) for path, row in zip(history_paths, historical["frames"])
        if not path.is_file() or sha256(path) != row["imageSha256"]
    ]
    if missing_or_changed:
        raise ValueError(f"historical evidence missing/hash-changed ({len(missing_or_changed)} files), first={missing_or_changed[0]}")
    history_pixels = np.stack([grayscale_128(path) for path in history_paths])
    scout_paths = [snapshot_root / str(row["image"]["relativePath"]).replace("\\", "/") for row in records]
    scout_pixels = np.stack([grayscale_128(path) for path in scout_paths])
    nearest = nearest_reference_rms(history_pixels, scout_pixels)
    cutoff = float(audit_protocol["visualNovelty"]["minimumNovelRms128"])
    per_component: list[dict[str, Any]] = []
    representative_pixels: dict[str, np.ndarray] = {}
    for component in primary["components"]:
        component_id = component["componentId"]
        indices = [index for index, row in enumerate(records) if primary["routeToComponent"][str(row["routeId"])] == component_id]
        representative = max(indices, key=lambda index: float(nearest[index]))
        representative_pixels[component_id] = scout_pixels[representative]
        per_component.append({
            **component,
            "sampleCount": len(indices),
            "maximumMinimumHistoricalRms128": float(nearest[representative]),
            "representativeRecordId": records[representative]["recordId"],
            "visuallyNovelVsHistorical": bool(nearest[representative] > cutoff),
        })
    candidates = [row["componentId"] for row in per_component if row["spatiallyNovel"] and row["visuallyNovelVsHistorical"]]
    separated = largest_pairwise_separated_subset(candidates, representative_pixels, cutoff)
    pairwise = [{
        "left": left, "right": right, "rms128": rms(representative_pixels[left], representative_pixels[right]),
        "aboveCutoff": rms(representative_pixels[left], representative_pixels[right]) > cutoff,
    } for left, right in itertools.combinations(sorted(candidates), 2)]
    return {
        "rendererNoiseCutoffRms128": cutoff,
        "perComponent": per_component,
        "largestPairwiseSeparatedComponentSet": separated,
        "representativePairwiseDistances": pairwise,
    }


def flatten_strings(value: Any) -> list[str]:
    if isinstance(value, str):
        return [value]
    if isinstance(value, dict):
        return [item for child in value.values() for item in flatten_strings(child)]
    if isinstance(value, list):
        return [item for child in value for item in flatten_strings(child)]
    return []


def within(root: Path, candidate: Path) -> bool:
    try:
        candidate.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def validate_snapshot(repo: Path, snapshot_root: Path) -> tuple[dict[str, Any], list[str], dict[str, Any]]:
    errors: list[str] = []
    evidence_path = snapshot_root / "r5_evidence.json"
    index_path = snapshot_root / "evidence_index.json"
    seal_path = snapshot_root / "snapshot_seal.json"
    for path in (evidence_path, index_path, seal_path):
        if not path.is_file():
            errors.append(f"missing immutable snapshot file: {path.name}")
    if errors:
        return {}, errors, {}
    evidence, index, seal = read_json(evidence_path), read_json(index_path), read_json(seal_path)
    schema = read_json(repo / SCHEMA)
    for issue in sorted(Draft202012Validator(schema, format_checker=FormatChecker()).iter_errors(evidence), key=lambda item: list(item.path)):
        errors.append(f"schema:{'/'.join(map(str, issue.path))}:{issue.message}")

    identity = read_json(repo / IDENTITY)
    expected = {row["role"]: row for row in identity["files"]}
    for role in ("coordinator_protocol", "evidence_schema", "independent_audit_protocol"):
        row = expected[role]
        actual = canonical_text_sha256(repo / row["path"])
        if actual != row["canonicalLfSha256"]:
            errors.append(f"local frozen identity mismatch:{role}:{actual}")
    if evidence.get("protocolSha256") != expected["coordinator_protocol"]["canonicalLfSha256"]:
        errors.append("snapshot protocol canonical hash mismatch")
    source = evidence.get("source", {})
    if source.get("baseCommit") != identity["sourceBaseCommit"]:
        errors.append("source base commit mismatch")
    if source.get("captureBranch") != "codex/r5-unity":
        errors.append("unexpected capture branch")
    scene = evidence.get("scene", {})
    if scene.get("sha256CanonicalLf") != expected["unity_scene"]["canonicalLfSha256"]:
        errors.append("scene canonical hash mismatch")
    if scene.get("evaluatorSha256CanonicalLf") != expected["unity_evaluator"]["canonicalLfSha256"]:
        errors.append("evaluator canonical hash mismatch")
    if scene.get("sha256Materialized") not in {expected["unity_scene"]["canonicalLfSha256"], expected["unity_scene"]["windowsCrlfSha256"]}:
        errors.append("scene materialized hash is not an allowed transport hash")
    if scene.get("evaluatorSha256Materialized") not in {expected["unity_evaluator"]["canonicalLfSha256"], expected["unity_evaluator"]["windowsCrlfSha256"]}:
        errors.append("evaluator materialized hash is not an allowed transport hash")

    entries = index.get("files", index if isinstance(index, list) else [])
    if not isinstance(entries, list):
        errors.append("evidence index has no files array")
        entries = []
    indexed_paths: dict[str, str] = {}
    for entry in entries:
        relative = str(entry.get("path", "")).replace("\\", "/")
        path = snapshot_root / relative
        if not relative or not within(snapshot_root, path):
            errors.append(f"unsafe indexed path:{relative}")
            continue
        if not path.is_file():
            errors.append(f"indexed file missing:{relative}")
            continue
        actual_hash, actual_bytes = sha256(path), path.stat().st_size
        if actual_hash != entry.get("sha256"):
            errors.append(f"indexed hash mismatch:{relative}")
        if actual_bytes != int(entry.get("bytes", -1)):
            errors.append(f"indexed byte count mismatch:{relative}")
        indexed_paths[relative] = actual_hash
    if evidence.get("integrity", {}).get("filesSha256") != sha256(index_path):
        errors.append("integrity.filesSha256 does not hash evidence_index.json bytes")
    if evidence.get("integrity", {}).get("manifestSha256") not in set(indexed_paths.values()):
        errors.append("capture input manifest hash is not present in evidence index")
    for generator in source.get("generatorFiles", []):
        relative = str(generator.get("path", "")).replace("\\", "/")
        if indexed_paths.get(relative) != generator.get("sha256"):
            errors.append(f"generator source is not hash-linked by index:{relative}")
    sealed_strings = set(flatten_strings(seal))
    for required_hash, label in (
        (sha256(evidence_path), "r5_evidence"), (sha256(index_path), "evidence_index"),
        (expected["coordinator_protocol"]["canonicalLfSha256"], "protocol"),
        (expected["evidence_schema"]["canonicalLfSha256"], "schema"),
        (expected["independent_audit_protocol"]["canonicalLfSha256"], "audit"),
    ):
        if required_hash not in sealed_strings:
            errors.append(f"snapshot seal omits {label} hash")

    records = list(evidence.get("records", []))
    proposal = evidence.get("proposal", {})
    attempts = list(evidence.get("startAttempts", []))
    integrity = evidence.get("integrity", {})
    if int(proposal.get("attemptedStarts", -1)) != int(proposal.get("acceptedStarts", 0)) + int(proposal.get("rejectedStarts", 0)):
        errors.append("attempted starts do not equal accepted plus rejected")
    if proposal.get("seed") != 20261026:
        errors.append("proposal seed differs from frozen R5-A seed")
    if len(attempts) != int(proposal.get("attemptedStarts", -1)):
        errors.append("startAttempts length disagrees with attemptedStarts")
    attempt_ids = [str(row.get("proposalId", "")) for row in attempts]
    if len(attempt_ids) != len(set(attempt_ids)):
        errors.append("duplicate start proposal id")
    accepted_attempts = {str(row.get("proposalId")): row for row in attempts if bool(row.get("accepted"))}
    rejected_attempts = [row for row in attempts if not bool(row.get("accepted"))]
    if len(accepted_attempts) != int(proposal.get("acceptedStarts", -1)) or len(rejected_attempts) != int(proposal.get("rejectedStarts", -1)):
        errors.append("startAttempts accepted/rejected counts disagree with proposal summary")
    for attempt in attempts:
        proposal_id = str(attempt.get("proposalId", ""))
        physically_valid = all(bool(attempt.get(key)) for key in ("t7Valid", "contactValid", "workspaceValid"))
        if bool(attempt.get("accepted")) != physically_valid:
            errors.append(f"start acceptance does not match physical validity:{proposal_id}")
        if bool(attempt.get("accepted")) and attempt.get("rejectionReason") not in (None, ""):
            errors.append(f"accepted start has rejection reason:{proposal_id}")
        if not bool(attempt.get("accepted")) and not str(attempt.get("rejectionReason") or "").strip():
            errors.append(f"rejected start lacks reason:{proposal_id}")
    route_ids = {str(row.get("routeId")) for row in records}
    if len(route_ids) > 8 or len(records) > 96:
        errors.append("R5-A route/image budget exceeded")
    for route_id in route_ids:
        moved = sum(
            str(row.get("plannedAction", "")).replace("_", "").upper() not in {"", "OBSERVE", "NONE", "RESET"}
            for row in records if str(row.get("routeId")) == route_id
        )
        if moved > 12:
            errors.append(f"R5-A move budget exceeded:{route_id}:{moved}")
    if integrity.get("recordCount") != len(records) or integrity.get("imageCount") != len(records):
        errors.append("integrity record/image counts disagree with records")
    floor = float(read_json(repo / PROTOCOL)["acceptableScoreFloor"])
    record_ids = set()
    by_route: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in records:
        record_id = str(row.get("recordId", ""))
        if record_id in record_ids:
            errors.append(f"duplicate record id:{record_id}")
        record_ids.add(record_id)
        route_id = str(row.get("routeId", ""))
        by_route[route_id].append(row)
        if str(row.get("startProposalId", "")) not in accepted_attempts:
            errors.append(f"record references non-accepted start:{record_id}")
        if not all(bool(row.get(key)) for key in ("t7Valid", "contactValid", "workspaceValid")):
            errors.append(f"invalid claimed observation:{record_id}")
        annotation = row.get("offlineAnnotation", {})
        if bool(annotation.get("acceptable")) != (float(annotation.get("targetScore", -math.inf)) >= floor):
            errors.append(f"label/floor disagreement:{record_id}")
        relative = str(row.get("image", {}).get("relativePath", "")).replace("\\", "/")
        path = snapshot_root / relative
        if not within(snapshot_root, path) or not path.is_file():
            errors.append(f"record image missing or unsafe:{record_id}:{relative}")
        elif sha256(path) != row.get("image", {}).get("sha256"):
            errors.append(f"record image hash mismatch:{record_id}")
        elif indexed_paths.get(relative) != row.get("image", {}).get("sha256"):
            errors.append(f"record image not hash-linked by index:{record_id}")
        ancestry = row.get("sourceAncestry", {})
        if ancestry.get("copiedPriorBaseTransform") is not False:
            errors.append(f"copied prior base transform:{record_id}")
        ancestry_text = " ".join(flatten_strings(ancestry))
        if any(family in ancestry_text for family in read_json(repo / PROTOCOL)["historicalLockedFamilies"]):
            errors.append(f"locked ancestry collision:{record_id}")
        accepted_action = bool(row.get("actionAccepted"))
        has_reason = bool(str(row.get("rejectionReason") or "").strip())
        if accepted_action == has_reason:
            errors.append(f"action acceptance/rejection reason inconsistency:{record_id}")
        if not accepted_action:
            if str(row.get("executedAction", "")).replace("_", "").upper() != "NONE":
                errors.append(f"rejected action is not recorded as executed NONE:{record_id}")
            if position_distance(row["poseTrace"]["beforePosition"], row["poseTrace"]["afterPosition"]) > 0.05:
                errors.append(f"rejected action changed position:{record_id}")
            if rotation_distance(row["poseTrace"]["beforeRotation"], row["poseTrace"]["afterRotation"]) > 0.05:
                errors.append(f"rejected action changed rotation:{record_id}")
    for route_id, rows in by_route.items():
        rows.sort(key=lambda row: int(row["observationIndex"]))
        indices = [int(row["observationIndex"]) for row in rows]
        if indices != list(range(len(rows))):
            errors.append(f"non-contiguous route observation indices:{route_id}")
        if len({str(row["startProposalId"]) for row in rows}) != 1:
            errors.append(f"route mixes start proposals:{route_id}")
        for previous, current in zip(rows, rows[1:]):
            if position_distance(previous["poseTrace"]["afterPosition"], current["poseTrace"]["beforePosition"]) > 0.05:
                errors.append(f"route position trace discontinuity:{route_id}:{current['recordId']}")
            if rotation_distance(previous["poseTrace"]["afterRotation"], current["poseTrace"]["beforeRotation"]) > 0.05:
                errors.append(f"route rotation trace discontinuity:{route_id}:{current['recordId']}")
    route = evidence.get("developmentRoute", {})
    supporting = set(map(str, route.get("supportingRecordIds", [])))
    if not supporting or not supporting <= record_ids:
        errors.append("development route cites missing scout records")
    transport = {
        "evidenceSha256": sha256(evidence_path), "indexSha256": sha256(index_path),
        "sealSha256": sha256(seal_path), "indexedFiles": len(indexed_paths),
        "contractIdentity": {role: row["canonicalLfSha256"] for role, row in expected.items()},
    }
    return evidence, errors, transport


def audit_scout(
    repo: Path, snapshot_root: Path, historical: dict[str, Any], historical_evidence_root: Path | None = None
) -> dict[str, Any]:
    evidence, errors, transport = validate_snapshot(repo, snapshot_root)
    audit_protocol = read_json(repo / PROTOCOL)
    if errors:
        return {
            "schemaVersion": 1, "taskId": "G2-R5-A-INDEPENDENT-AUDIT",
            "verdict": "ENGINEERING_OR_INTEGRITY_BLOCKED", "integrity": {"status": "FAIL", "errors": errors},
            "transport": transport, "auditProtocolSha256": canonical_text_sha256(repo / PROTOCOL),
        }
    records = list(evidence["records"])
    primary = spatial_component_audit(historical, records, audit_protocol, conservative=False)
    conservative = spatial_component_audit(historical, records, audit_protocol, conservative=True)
    evidence_root = historical_evidence_root or Path(historical["evidenceRootHint"])
    try:
        visual = visual_component_audit(repo, snapshot_root, evidence_root, historical, records, primary, audit_protocol)
    except (OSError, ValueError) as issue:
        return {
            "schemaVersion": 1, "taskId": "G2-R5-A-INDEPENDENT-AUDIT",
            "verdict": "ENGINEERING_OR_INTEGRITY_BLOCKED",
            "integrity": {"status": "FAIL", "errors": [str(issue)]}, "transport": transport,
            "auditProtocolSha256": canonical_text_sha256(repo / PROTOCOL),
        }
    separated = set(visual["largestPairwiseSeparatedComponentSet"])
    positive, negative, hard_negative = set(), set(), set()
    floor = float(audit_protocol["acceptableScoreFloor"])
    for row in records:
        component = primary["routeToComponent"][str(row["routeId"])]
        if component not in separated:
            continue
        score = float(row["offlineAnnotation"]["targetScore"])
        if bool(row["offlineAnnotation"]["acceptable"]):
            positive.add(component)
        else:
            negative.add(component)
            if -0.05 <= score - floor < 0.0:
                hard_negative.add(component)
    union_components = positive | negative
    route = evidence["developmentRoute"]
    justified_route = bool(
        str(route["strategy"]).strip() and str(route["whyScoutSupportsIt"]).strip()
        and int(route["expectedAdditionalIndependentPositiveComponents"]) >= 1
        and int(route["expectedAdditionalIndependentNegativeComponents"]) >= 1
        and route["supportingRecordIds"]
    )
    exhausted = int(evidence["proposal"]["attemptedStarts"]) == 48 or len({str(row["routeId"]) for row in records}) == 8
    spatially_novel_count = sum(bool(row["spatiallyNovel"]) for row in primary["components"])
    visually_novel_count = len(separated)
    verdict, demonstrated_collapse = classify_scout_decision(
        positive=len(positive), negative=len(negative), union=len(union_components),
        hard_negative=len(hard_negative), justified_route=justified_route, exhausted=exhausted,
        spatially_novel=spatially_novel_count, visually_novel=visually_novel_count,
    )
    result = {
        "schemaVersion": 1, "taskId": "G2-R5-A-INDEPENDENT-AUDIT", "verdict": verdict,
        "integrity": {"status": "PASS", "errors": []}, "transport": transport,
        "sourceSnapshot": str(snapshot_root).replace("\\", "/"),
        "sourceSnapshotId": evidence["snapshotId"],
        "auditProtocolSha256": canonical_text_sha256(repo / PROTOCOL),
        "historicalIndexSha256": sha256(repo / OUTPUT / "g2_r5_historical_reference_index.json"),
        "primarySpatial": primary, "conservativeSensitivity": conservative, "visualNovelty": visual,
        "decisionCounts": {
            "positiveReachableComponents": sorted(positive), "negativeComponents": sorted(negative),
            "hardNegativeComponents": sorted(hard_negative), "unionComponents": sorted(union_components),
            "justifiedRouteToAdditionalIndependentSupport": justified_route,
            "explorationBudgetExhaustedForCollapseDecision": exhausted,
            "demonstratedSevereSpatialOrVisualCollapse": demonstrated_collapse,
        },
        "decisionRule": audit_protocol["decisionRule"],
        "historicalLockedFamiliesOpened": [], "confirmationOpened": False,
        "scientificLimitation": "A GO only authorizes bounded fixed-anatomy development capture; it is not recognition success, clinical validity, or unseen-anatomy generalization.",
    }
    write_json(repo / OUTPUT / "g2_r5_a_independent_verdict.json", result)
    return result


def build_historical_index(repo: Path, evidence_repo: Path | None = None) -> dict[str, Any]:
    evidence_repo = (evidence_repo or repo).resolve()
    audit_protocol_path = repo / PROTOCOL
    coordinator_protocol_path = repo / COORDINATOR_PROTOCOL
    historical_protocol_path = evidence_repo / HISTORICAL_PROTOCOL
    lineage_path = repo / HISTORICAL_LINEAGE
    audit_protocol = read_json(audit_protocol_path)
    historical_protocol = read_json(historical_protocol_path)
    lineage = read_json(lineage_path)
    cases, records, errors = collect_r4(evidence_repo, historical_protocol)
    locked = set(audit_protocol["historicalLockedFamilies"])
    complete = [case for case in cases if case.get("captureStatus") == "complete"]
    if any(case.get("sourceBaseGroup") in locked or case.get("trajectoryFamily") in locked for case in complete):
        errors.append("locked family entered historical index")
    complete = [
        case for case in complete
        if case.get("sourceBaseGroup") not in locked and case.get("trajectoryFamily") not in locked
    ]
    allowed_cases = {case["caseId"] for case in complete}
    records = [row for row in records if row["caseId"] in allowed_cases]
    primary_map = lineage["primaryPolicy"]["caseToComponent"]
    conservative_map = lineage["conservativeSensitivity"]["caseToComponent"]
    by_case: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in records:
        by_case[row["caseId"]].append(row)
    trajectories = []
    for case in complete:
        rows = by_case[case["caseId"]]
        trajectories.append({
            "caseId": case["caseId"],
            "trajectoryFamily": case["trajectoryFamily"],
            "sourceBaseGroup": case["sourceBaseGroup"],
            "sourceEpisode": case.get("sourceEpisode"),
            "sourceStartId": case.get("sourceStartId"),
            "arm": case["arm"],
            "countsAsIndependentP1": case["countsAsIndependentP1"],
            "primaryComponent": primary_map[case["caseId"]],
            "conservativeComponent": conservative_map[case["caseId"]],
            "sourcePose": case["sourcePose"],
            "observationCount": len(rows),
            "positiveCount": sum(bool(row["target"]) for row in rows),
            "previousUsage": case["previousUsage"],
            "sourceManifest": case["sourceManifest"],
            "rawRoot": case["rawRoot"],
        })
    frames = [{
        "caseId": row["caseId"], "index": row["index"], "image": row["image"],
        "imageSha256": row["imageSha256"], "target": row["target"], "score": row["score"],
        "margin": row["margin"], "probePosition": row["probePosition"],
        "probeRotation": row["probeRotation"], "issuedAction": row["issuedAction"],
        "executedAction": row["executedAction"], "actionAccepted": row["actionAccepted"],
        "actionCompleted": row["actionCompleted"], "episodeReset": row["episodeReset"],
        "contactValid": row["contactValid"], "workspaceValid": row["workspaceValid"],
    } for row in records]
    source_files = [audit_protocol_path, coordinator_protocol_path, historical_protocol_path, lineage_path]
    def source_entry(path: Path) -> dict[str, str]:
        try:
            display = str(path.relative_to(repo)).replace("\\", "/")
        except ValueError:
            display = str(path).replace("\\", "/")
        return {"path": display, "sha256": sha256(path)}

    result = {
        "schemaVersion": 1,
        "taskId": "G2-R5-B1-HISTORICAL-INDEX",
        "sourceCommit": audit_protocol["sourceCommit"],
        "auditProtocol": PROTOCOL,
        "auditProtocolSha256": canonical_text_sha256(audit_protocol_path),
        "coordinatorProtocol": COORDINATOR_PROTOCOL,
        "coordinatorProtocolSha256": canonical_text_sha256(coordinator_protocol_path),
        "evidenceRootHint": str(evidence_repo).replace("\\", "/"),
        "evidenceRootPolicy": "Caller must explicitly override when the recorded local hint is absent; every referenced image is re-hashed before visual audit.",
        "sources": [source_entry(path) for path in source_files],
        "integrity": {
            "status": "PASS" if not errors else "FAIL", "errors": errors,
            "trajectoryCount": len(trajectories), "frameCount": len(frames),
            "uniqueImageHashes": len({frame["imageSha256"] for frame in frames}),
            "frameHashMerkleEquivalent": merkle_equivalent(frame["imageSha256"] for frame in frames),
        },
        "spatialPolicies": audit_protocol["spatialNovelty"],
        "visualPolicy": audit_protocol["visualNovelty"],
        "primaryComponents": lineage["primaryPolicy"]["components"],
        "conservativeComponents": lineage["conservativeSensitivity"]["components"],
        "trajectories": trajectories,
        "frames": frames,
        "historicalLockedFamiliesOpened": [],
        "confirmationOpened": False,
        "scopeWarning": "Consumed development evidence from one fixed Unity anatomy; no patient/anatomy generalization claim.",
    }
    write_json(repo / OUTPUT / "g2_r5_historical_reference_index.json", result)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description="Independent G2-R5 historical coverage and scout audit")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--evidence-repo", type=Path)
    parser.add_argument("--build-index", action="store_true")
    parser.add_argument("--audit-scout", type=Path)
    parser.add_argument("--historical-evidence-root", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    if args.build_index == bool(args.audit_scout):
        parser.error("choose exactly one of --build-index or --audit-scout")
    if args.build_index:
        result = build_historical_index(repo, args.evidence_repo)
        print(json.dumps({"integrity": result["integrity"], "auditProtocolSha256": result["auditProtocolSha256"]}, indent=2))
    else:
        historical = read_json(repo / OUTPUT / "g2_r5_historical_reference_index.json")
        result = audit_scout(repo, args.audit_scout.resolve(), historical, args.historical_evidence_root)
        print(json.dumps({"integrity": result["integrity"], "verdict": result["verdict"]}, indent=2))
    if result["integrity"]["status"] != "PASS":
        raise SystemExit(2)


if __name__ == "__main__":
    main()
