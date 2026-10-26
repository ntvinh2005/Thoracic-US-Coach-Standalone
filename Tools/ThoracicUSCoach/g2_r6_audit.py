from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

import numpy as np
from PIL import Image


PROTOCOL = "Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json"
R1_MANIFEST = "Tools/ThoracicUSCoach/experiments/g2_r1_existing_family_v1.json"
R4_MANIFESTS = (
    "Tools/ThoracicUSCoach/experiments/g2_r4_p1_capture_v1.json",
    "Tools/ThoracicUSCoach/experiments/g2_r4_p1_hard_supplement_v1.json",
    "Tools/ThoracicUSCoach/experiments/g2_r4_p1_hard_supplement_v2.json",
)
R3_REPORT = "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r3_a2_20261025/unity_capture/g2_r3_a2_unity_report.json"
OUTPUT = "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r6_final_sprint_20261026"


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def write_jsonl(path: Path, values: Iterable[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        for value in values:
            stream.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def q_angle(left: Iterable[float], right: Iterable[float]) -> float:
    dot = abs(sum(float(a) * float(b) for a, b in zip(left, right)))
    return math.degrees(2.0 * math.acos(min(1.0, max(-1.0, dot))))


def xyz(value: dict[str, Any] | None) -> list[float] | None:
    return None if value is None else [float(value[key]) for key in ("x", "y", "z")]


def xyzw(value: dict[str, Any] | None) -> list[float] | None:
    return None if value is None else [float(value[key]) for key in ("x", "y", "z", "w")]


def resolve_image(root: Path, recorded: str) -> Path:
    normalized = recorded.replace("\\", "/")
    return root / normalized


def image_features(path: Path) -> tuple[np.ndarray, np.ndarray]:
    with Image.open(path) as opened:
        array = np.asarray(
            opened.convert("L").resize((128, 128), Image.Resampling.BILINEAR), dtype=np.float32
        ) / 255.0
    gy, gx = np.gradient(array)
    magnitude = np.hypot(gx, gy)
    orientation = np.arctan2(gy, gx) % np.pi
    descriptor: list[float] = []
    for row in range(4):
        for column in range(4):
            region = (slice(row * 32, (row + 1) * 32), slice(column * 32, (column + 1) * 32))
            histogram = np.histogram(
                orientation[region], bins=8, range=(0.0, np.pi), weights=magnitude[region]
            )[0].astype(np.float64)
            histogram /= np.linalg.norm(histogram) + 1e-12
            descriptor.extend(histogram.tolist())
    structural = np.asarray(descriptor, dtype=np.float64)
    structural /= np.linalg.norm(structural) + 1e-12
    return array, structural


def visual_distances(left: tuple[np.ndarray, np.ndarray], right: tuple[np.ndarray, np.ndarray]) -> tuple[float, float]:
    rms = float(np.sqrt(np.mean(np.square(left[0] - right[0]))))
    structural = 1.0 - float(np.dot(left[1], right[1]))
    return rms, structural


def percentile(values: list[float], value: float) -> float | None:
    return None if not values else float(np.quantile(np.asarray(values, dtype=np.float64), value))


def eligibility(
    *, score: float, target: bool, t7: bool | None, contact: bool, workspace: bool,
    hash_valid: bool, floor: float,
) -> tuple[bool, bool]:
    agreement = bool(target) == (float(score) >= float(floor))
    eligible = t7 is True and contact and workspace and hash_valid and agreement
    return eligible, agreement


def add_record(
    records: list[dict[str, Any]], errors: list[str], *, root_id: str, root: Path,
    stage: str, trajectory: str, family: str, role: str, ancestors: list[str], lineage_keys: list[str],
    index: int, image_recorded: str, recorded_hash: str, position: dict[str, Any] | None,
    rotation: dict[str, Any] | None, score: float, target: bool, t7: bool | None,
    contact: bool, workspace: bool, action: str | None, floor: float,
) -> None:
    image = resolve_image(root, image_recorded)
    actual_hash = sha256(image) if image.is_file() else "MISSING"
    hash_valid = actual_hash == str(recorded_hash).lower()
    if not hash_valid:
        errors.append(f"image hash mismatch or missing: {stage}/{trajectory}/{index}")
    ready, agreement = eligibility(
        score=score, target=target, t7=t7, contact=contact, workspace=workspace,
        hash_valid=hash_valid, floor=floor,
    )
    if not agreement:
        errors.append(f"label/floor mismatch: {stage}/{trajectory}/{index}")
    records.append({
        "id": f"{stage}:{trajectory}:{index:04d}",
        "stage": stage,
        "trajectory": trajectory,
        "family": family,
        "priorRole": role,
        "ancestors": sorted(set(ancestors)),
        "lineageKeys": sorted(set(value for value in lineage_keys if value)),
        "frame": int(index),
        "imageRoot": root_id,
        "image": image_recorded.replace("\\", "/"),
        "imageSha256": actual_hash,
        "imageHashVerified": hash_valid,
        "probePosition": xyz(position),
        "probeRotation": xyzw(rotation),
        "score": float(score),
        "evaluatorPositive": bool(target),
        "t7Valid": t7,
        "contactValid": bool(contact),
        "workspaceValid": bool(workspace),
        "action": action,
        "labelFloorAgreement": agreement,
        "decisionSupportEligible": ready,
        "decisionLabel": ("positive" if target else "negative") if ready else None,
    })


def collect_r1_manifest(repo: Path, locked: set[str]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    manifest = read_json(repo / R1_MANIFEST)
    visible, excluded = [], []
    for case in manifest["cases"]:
        compact = {
            "family": case["family"], "episodeId": case["episodeId"], "role": case["role"],
            "frames": case["frames"], "positiveFrames": case["positiveFrames"],
            "maximumTargetScore": case["maximumTargetScore"],
            "streamManifestSha256": case["streamManifestSha256"],
            "privilegedAnnotationsSha256": case["privilegedAnnotationsSha256"],
        }
        (excluded if case["family"] in locked or case["role"] == "locked_test" else visible).append(compact)
    return visible, excluded


def collect_temporal(
    evidence_repo: Path, floor: float, locked: set[str], records: list[dict[str, Any]], errors: list[str]
) -> dict[str, Any]:
    relative = Path("Artifacts/ThoracicUSCoach/active2d/g2_temporal_capture")
    root = evidence_repo / relative
    trajectories = 0
    source_artifacts: list[dict[str, Any]] = []
    for split in ("train", "calibration", "heldout_dev"):
        for stream_path in sorted((root / split).glob("*/stream_manifest.json")):
            stream = read_json(stream_path)
            family = str(stream["family"])
            if family in locked:
                errors.append(f"blocked locked family unexpectedly present in temporal source: {family}")
                continue
            privileged_path = stream_path.parent / "privileged_annotations.json"
            privileged = read_json(privileged_path)
            labels = {int(item["index"]): item for item in privileged["frames"]}
            trajectory = str(stream["episodeId"])
            trajectories += 1
            source_artifacts.append({
                "trajectory": trajectory, "family": family, "priorRole": split,
                "streamManifestSha256": sha256(stream_path),
                "privilegedAnnotationsSha256": sha256(privileged_path),
            })
            for frame in stream["frames"]:
                index = int(frame["index"])
                label = labels[index]
                add_record(
                    records, errors, root_id="evidenceRepo", root=evidence_repo,
                    stage="R1_R2_TEMPORAL", trajectory=trajectory, family=family,
                    role=f"consumed_{split}_temporal_stop_development", ancestors=[family], lineage_keys=[family], index=index,
                    image_recorded=frame["image"], recorded_hash=frame["imageSha256"],
                    position=label.get("probePosition"), rotation=label.get("probeRotation"),
                    score=float(label["finalTargetScore"]), target=bool(label["finalTargetAcceptable"]),
                    t7=bool(label["t7LevelAcceptable"]), contact=bool(label["contactValid"]),
                    workspace=bool(label["workspaceValid"]), action=str(frame.get("issuedAction", "")), floor=floor,
                )
    report_path = root / "g2_capture_report.json"
    return {
        "root": str(root), "available": root.is_dir(), "trajectories": trajectories,
        "captureReportSha256": sha256(report_path) if report_path.is_file() else None,
        "sourceArtifacts": source_artifacts,
    }


def collect_r3(
    repo: Path, floor: float, locked: set[str], records: list[dict[str, Any]], errors: list[str]
) -> dict[str, Any]:
    report_path = repo / R3_REPORT
    report = read_json(report_path)
    trajectories = 0
    for case in report["cases"]:
        family = str(case["family"])
        if family in locked:
            errors.append(f"blocked locked family unexpectedly present in R3-A2 report: {family}")
            continue
        trajectory = str(case["caseId"])
        trajectories += 1
        for observation in case["observations"]:
            score = float(observation["score"]["total"])
            add_record(
                records, errors, root_id="auditRepo", root=repo, stage="R3_A2_REPEAT",
                trajectory=trajectory, family=family, role="consumed_exact_pose_observability_diagnostic",
                ancestors=[family, f"source_frame_{case['sourceFrameIndex']}"],
                lineage_keys=[family],
                index=int(observation["repeatIndex"]), image_recorded=observation["image"],
                recorded_hash=observation["imageSha256"], position=observation.get("actualProbePosition"),
                rotation=observation.get("actualProbeRotation"), score=score,
                target=bool(observation["targetAcceptable"]), t7=None,
                contact=bool(observation["contactValid"]), workspace=bool(observation["workspaceValid"]),
                action="REPEAT_RENDER", floor=floor,
            )
    return {
        "report": R3_REPORT, "available": report_path.is_file(), "reportSha256": sha256(report_path),
        "manifest": "Tools/ThoracicUSCoach/experiments/g2_r3_a2_exact_pose_v1.json",
        "manifestSha256": report.get("manifestSha256"), "trajectories": trajectories,
    }


def r4_declarations(repo: Path) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for relative in R4_MANIFESTS:
        manifest = read_json(repo / relative)
        for case in manifest.get("cases", []):
            item = dict(case)
            item["sourceManifest"] = relative
            result[str(item["caseId"])] = item
    return result


def collect_r4(
    repo: Path, evidence_repo: Path, floor: float, locked: set[str],
    records: list[dict[str, Any]], errors: list[str],
) -> list[dict[str, Any]]:
    declarations = r4_declarations(repo)
    root_relatives = (
        "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_raw_20261025",
        "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_20261025",
        "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_v2_20261025",
    )
    summaries: list[dict[str, Any]] = []
    for root_relative in root_relatives:
        raw_root = evidence_repo / root_relative
        report_path = raw_root / "g2_r4_p1_unity_report.json"
        if not report_path.is_file():
            summaries.append({"root": str(raw_root), "available": False, "reason": "raw report absent"})
            continue
        report = read_json(report_path)
        completed = 0
        for audit in report.get("cases", []):
            case_id = str(audit["caseId"])
            declared = declarations.get(case_id)
            if declared is None:
                errors.append(f"undeclared R4 case: {case_id}")
                continue
            base = declared.get("baseStart", {})
            source_family = str(base.get("sourceFamily", "unknown"))
            family = str(audit["familyId"])
            if source_family in locked or family in locked:
                errors.append(f"blocked locked ancestry in R4: {case_id}/{source_family}")
                continue
            if audit.get("captureStatus") != "complete":
                continue
            completed += 1
            role = "consumed_R4_primary_development" if bool(
                declared.get("countsAsNewIndependentFamily", audit.get("countsAsNewIndependentFamily", False))
            ) else "consumed_R4_auxiliary_diagnostic"
            for observation in audit.get("observations", []):
                add_record(
                    records, errors, root_id="evidenceRepo", root=evidence_repo, stage="R4_P1",
                    trajectory=case_id, family=family, role=role,
                    ancestors=[source_family, str(base.get("sourceEpisode", "")), str(base.get("startId", ""))],
                    lineage_keys=[source_family, str(base.get("sourceEpisode", ""))],
                    index=int(observation["index"]), image_recorded=observation["image"],
                    recorded_hash=observation["imageSha256"], position=observation.get("probePosition"),
                    rotation=observation.get("probeRotation"), score=float(observation["score"]["total"]),
                    target=bool(observation["finalTargetAcceptable"]),
                    t7=bool(observation["t7LevelAcceptable"]), contact=bool(observation["contactValid"]),
                    workspace=bool(observation["workspaceValid"]), action=str(observation.get("incomingAction", "")),
                    floor=floor,
                )
        summaries.append({
            "root": str(raw_root), "available": True, "completedTrajectories": completed,
            "unityReportSha256": sha256(report_path),
        })
    return summaries


def collect_r5(
    handoff_root: Path, floor: float, records: list[dict[str, Any]], errors: list[str]
) -> dict[str, Any]:
    evidence_path = handoff_root / "r5_evidence.json"
    if not evidence_path.is_file():
        return {"root": str(handoff_root), "available": False, "reason": "immutable evidence absent"}
    evidence = read_json(evidence_path)
    route_ids: set[str] = set()
    for item in evidence.get("records", []):
        route = str(item["routeId"])
        route_ids.add(route)
        annotation = item["offlineAnnotation"]
        score = float(annotation["targetScore"])
        image = item["image"]
        pose = item["poseTrace"]
        ancestry = item.get("sourceAncestry", {})
        add_record(
            records, errors, root_id="r5ImmutableHandoff", root=handoff_root, stage="R5_SCOUT",
            trajectory=route, family=route, role="consumed_R5_exploratory_scout",
            ancestors=[str(ancestry.get("origin", "unknown"))] + list(ancestry.get("landmarkObjects", [])),
            lineage_keys=[route],
            index=int(item["observationIndex"]), image_recorded=image["relativePath"], recorded_hash=image["sha256"],
            position=pose.get("afterPosition"), rotation=pose.get("afterRotation"), score=score,
            target=bool(annotation["acceptable"]), t7=bool(item["t7Valid"]),
            contact=bool(item["contactValid"]), workspace=bool(item["workspaceValid"]),
            action=str(item.get("executedAction", "")), floor=floor,
        )
    result = {
        "root": str(handoff_root), "available": True, "snapshotId": evidence.get("snapshotId"),
        "routes": len(route_ids), "records": len(evidence.get("records", [])), "snapshotRole": evidence.get("snapshotRole"),
    }
    for name in ("r5_evidence.json", "evidence_index.json", "snapshot_seal.json"):
        path = handoff_root / name
        result[name.replace(".", "_") + "Sha256"] = sha256(path) if path.is_file() else None
    return result


def trajectory_groups(records: list[dict[str, Any]]) -> dict[str, list[dict[str, Any]]]:
    result: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in records:
        result[f"{item['stage']}:{item['trajectory']}"].append(item)
    return dict(result)


def spatial_audit(records: list[dict[str, Any]], protocol: dict[str, Any]) -> dict[str, Any]:
    trajectories = trajectory_groups([item for item in records if item["probePosition"] and item["probeRotation"]])
    keys = sorted(trajectories)
    primary = protocol["spatialIndependence"]["primary"]
    conservative = protocol["spatialIndependence"]["conservativeSensitivity"]
    overlap_edges, ancestry_edges, near_start_edges = [], [], []
    for left_index, left_key in enumerate(keys):
        left = trajectories[left_key]
        left_ancestors = set(value for item in left for value in item["lineageKeys"] if value)
        for right_key in keys[left_index + 1:]:
            right = trajectories[right_key]
            right_ancestors = set(value for item in right for value in item["lineageKeys"] if value)
            shared = sorted(left_ancestors & right_ancestors)
            if shared:
                ancestry_edges.append({"left": left_key, "right": right_key, "shared": shared})
            lp = np.asarray([item["probePosition"] for item in left], dtype=np.float64)
            rp = np.asarray([item["probePosition"] for item in right], dtype=np.float64)
            lq = np.asarray([item["probeRotation"] for item in left], dtype=np.float64)
            rq = np.asarray([item["probeRotation"] for item in right], dtype=np.float64)
            position = np.linalg.norm(lp[:, None, :] - rp[None, :, :], axis=2)
            dots = np.abs(np.clip(lq @ rq.T, -1.0, 1.0))
            angle = np.degrees(2.0 * np.arccos(dots))
            matched = (position <= float(primary["poseMatchPositionMm"])) & (angle <= float(primary["poseMatchAngleDeg"]))
            left_fraction = float(np.any(matched, axis=1).mean())
            right_fraction = float(np.any(matched, axis=0).mean())
            if min(left_fraction, right_fraction) >= float(primary["minimumBidirectionalTrajectoryCoverageFraction"]):
                overlap_edges.append({
                    "left": left_key, "right": right_key,
                    "leftCoverage": left_fraction, "rightCoverage": right_fraction,
                })
            start_position = float(np.linalg.norm(lp[0] - rp[0]))
            start_angle = q_angle(lq[0], rq[0])
            if start_position <= float(conservative["nearStartPositionMm"]) and start_angle <= float(conservative["nearStartAngleDeg"]):
                near_start_edges.append({
                    "left": left_key, "right": right_key,
                    "positionMm": start_position, "angleDeg": start_angle,
                })
    return {
        "trajectoryCount": len(trajectories), "ancestryEdges": ancestry_edges,
        "primaryOverlapEdges": overlap_edges, "conservativeNearStartEdges": near_start_edges,
    }


def renderer_noise(repo: Path, protocol: dict[str, Any]) -> dict[str, Any]:
    root = repo / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r3_a2_20261025/unity_capture"
    rms_values: list[float] = []
    structural_values: list[float] = []
    for directory in sorted(root.glob("case_*")):
        features = [image_features(path) for path in sorted(directory.glob("repeat_*_us.png"))]
        for left_index, left in enumerate(features):
            for right in features[left_index + 1:]:
                rms, structural = visual_distances(left, right)
                rms_values.append(rms)
                structural_values.append(structural)
    structural_threshold = float(protocol["visualStructuralIndependence"]["minimumStructuralDistance"])
    structural_q95 = percentile(structural_values, 0.95)
    return {
        "source": "already-consumed nonlocked G2-R3-A2 same-pose repeats",
        "pairs": len(rms_values),
        "rms128": {"q95": percentile(rms_values, 0.95), "max": max(rms_values, default=None)},
        "gradientOrientationCosineDistance": {
            "q95": structural_q95, "q99": percentile(structural_values, 0.99),
            "max": max(structural_values, default=None), "frozenCutoff": structural_threshold,
            "threeTimesQ95": None if structural_q95 is None else 3.0 * structural_q95,
            "cutoffIsAboveThreeTimesQ95": structural_q95 is not None and structural_threshold > 3.0 * structural_q95,
        },
    }


def representative_visual_audit(
    records: list[dict[str, Any]], roots: dict[str, Path], protocol: dict[str, Any]
) -> dict[str, Any]:
    candidates: dict[tuple[str, str], dict[str, Any]] = {}
    for item in records:
        if not item["decisionSupportEligible"]:
            continue
        key = (f"{item['stage']}:{item['trajectory']}", str(item["decisionLabel"]))
        current = candidates.get(key)
        if current is None or (
            item["decisionLabel"] == "positive" and item["score"] > current["score"]
        ) or (
            item["decisionLabel"] == "negative" and item["score"] > current["score"]
        ):
            candidates[key] = item
    representatives = sorted(candidates.values(), key=lambda item: item["id"])
    features: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for item in representatives:
        features[item["id"]] = image_features(resolve_image(roots[item["imageRoot"]], item["image"]))
    rms_cutoff = float(protocol["visualStructuralIndependence"]["minimumIntensityRms128"])
    structural_cutoff = float(protocol["visualStructuralIndependence"]["minimumStructuralDistance"])
    merge_edges = []
    for left_index, left in enumerate(representatives):
        for right in representatives[left_index + 1:]:
            rms, structural = visual_distances(features[left["id"]], features[right["id"]])
            if rms <= rms_cutoff or structural <= structural_cutoff:
                merge_edges.append({
                    "left": left["id"], "right": right["id"], "rms128": rms,
                    "structuralDistance": structural,
                    "reason": "RMS_AT_OR_BELOW_CUTOFF" if rms <= rms_cutoff else "STRUCTURE_AT_OR_BELOW_CUTOFF",
                })
    parent = {item["id"]: item["id"] for item in representatives}

    def find(value: str) -> str:
        while parent[value] != value:
            parent[value] = parent[parent[value]]
            value = parent[value]
        return value

    def union(left: str, right: str) -> None:
        left_root, right_root = find(left), find(right)
        if left_root != right_root:
            parent[right_root] = left_root

    for edge in merge_edges:
        union(edge["left"], edge["right"])
    components: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in representatives:
        components[find(item["id"])].append(item)
    compact_components = []
    for index, members in enumerate(sorted(components.values(), key=lambda group: group[0]["id"])):
        compact_components.append({
            "componentId": f"visual_structural_{index:02d}",
            "representativeIds": [item["id"] for item in members],
            "labels": sorted(set(item["decisionLabel"] for item in members)),
            "trajectories": sorted(set(item["trajectory"] for item in members)),
        })
    return {
        "representativeSelection": "highest-score readiness-eligible frame per trajectory and label",
        "representatives": [{
            "id": item["id"], "label": item["decisionLabel"], "imageSha256": item["imageSha256"],
            "trajectory": item["trajectory"], "family": item["family"], "score": item["score"],
        } for item in representatives],
        "dualDistanceMergeEdges": merge_edges,
        "dualDistanceComponents": compact_components,
        "rule": "An edge exists if either intensity RMS or structural distance fails its threshold; RMS alone never establishes novelty.",
    }


def classify_stage_bc(stage_bc: dict[str, Any]) -> tuple[str, list[str]]:
    requirements = {
        "immutableCompleteHandoff": bool(stage_bc.get("immutableCompleteHandoff")),
        "integrityPass": stage_bc.get("integrityStatus") == "PASS",
        "positiveControlReproducibility": stage_bc.get("positiveControlReproducibility") == "PASS",
        "basinStability": stage_bc.get("basinStability") == "PASS",
        "targetIndependentReachability": stage_bc.get("targetIndependentReachability") == "PASS",
    }
    blockers = [name for name, passed in requirements.items() if not passed]
    return ("GO_R6_BOUNDED_DEVELOPMENT_CAPTURE" if not blockers else "NO_GO"), blockers


def summarize(records: list[dict[str, Any]], floor: float) -> dict[str, Any]:
    stages: dict[str, dict[str, Any]] = {}
    for stage in sorted(set(item["stage"] for item in records)):
        subset = [item for item in records if item["stage"] == stage]
        eligible = [item for item in subset if item["decisionSupportEligible"]]
        stages[stage] = {
            "records": len(subset), "uniqueImageHashes": len(set(item["imageSha256"] for item in subset)),
            "trajectories": len(set(item["trajectory"] for item in subset)),
            "evaluatorPositive": sum(item["evaluatorPositive"] for item in subset),
            "strictT7Positive": sum(item["decisionLabel"] == "positive" for item in eligible),
            "strictT7Negative": sum(item["decisionLabel"] == "negative" for item in eligible),
            "offT7OrUnknownRetainedOnly": sum(not item["decisionSupportEligible"] for item in subset),
        }
    strict_positive = [item for item in records if item["decisionLabel"] == "positive"]
    controls = sorted(strict_positive, key=lambda item: (-item["score"], item["id"]))
    return {
        "acceptableScoreFloor": floor, "records": len(records),
        "uniqueImageHashes": len(set(item["imageSha256"] for item in records)),
        "duplicateHashInstances": len(records) - len(set(item["imageSha256"] for item in records)),
        "stages": stages,
        "strictPositiveControlCandidates": [{
            "id": item["id"], "family": item["family"], "ancestors": item["ancestors"],
            "stage": item["stage"], "trajectory": item["trajectory"], "priorRole": item["priorRole"],
            "imageSha256": item["imageSha256"], "probePosition": item["probePosition"],
            "probeRotation": item["probeRotation"], "score": item["score"],
            "t7Valid": item["t7Valid"], "contactValid": item["contactValid"],
            "workspaceValid": item["workspaceValid"],
        } for item in controls],
    }


def build(repo: Path, evidence_repo: Path, r5_handoff: Path) -> dict[str, Any]:
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    floor = float(protocol["acceptableScoreFloor"])
    locked = set(protocol["historicalLockedFamilies"])
    errors: list[str] = []
    records: list[dict[str, Any]] = []
    visible_r1, excluded_r1 = collect_r1_manifest(repo, locked)
    sources = {
        "R1OriginalDeclared": {
            "manifest": R1_MANIFEST, "manifestSha256": sha256(repo / R1_MANIFEST),
            "nonlockedDeclaredCases": len(visible_r1), "lockedCasesExcludedWithoutRawAccess": len(excluded_r1),
            "rawRoot": str(evidence_repo / "Artifacts/ThoracicUSCoach/active2d_dataset"),
            "rawAvailable": (evidence_repo / "Artifacts/ThoracicUSCoach/active2d_dataset").is_dir(),
            "manifestOnlyNonlockedCases": visible_r1,
        },
        "R1R2Temporal": collect_temporal(evidence_repo, floor, locked, records, errors),
        "R3A2Repeat": collect_r3(repo, floor, locked, records, errors),
        "R4P1": {
            "manifests": [{"path": relative, "sha256": sha256(repo / relative)} for relative in R4_MANIFESTS],
            "rawRoots": collect_r4(repo, evidence_repo, floor, locked, records, errors),
        },
        "R5Scout": collect_r5(r5_handoff, floor, records, errors),
    }
    missing = []
    if not sources["R1OriginalDeclared"]["rawAvailable"]:
        missing.append({
            "source": "R1 original active2d_phase2_raster_v2_20261023",
            "path": sources["R1OriginalDeclared"]["rawRoot"],
            "impact": "18 declared nonlocked train/calibration episodes remain manifest-only; their original pixels and poses cannot support R6 coverage or readiness counts.",
        })
    for item in sources["R4P1"]["rawRoots"]:
        if not item["available"]:
            missing.append({"source": "R4 raw root", "path": item["root"], "impact": item["reason"]})
    roots = {"auditRepo": repo, "evidenceRepo": evidence_repo, "r5ImmutableHandoff": r5_handoff}
    duplicate_hashes = defaultdict(list)
    for item in records:
        duplicate_hashes[item["imageSha256"]].append(item["id"])
    duplicates = {key: value for key, value in duplicate_hashes.items() if key != "MISSING" and len(value) > 1}
    report = {
        "schemaVersion": 1,
        "taskId": "G2-R6-B-HISTORICAL-COVERAGE-AUDIT",
        "sourceCommit": protocol["sourceCommit"],
        "protocol": PROTOCOL,
        "protocolSha256": sha256(protocol_path),
        "scope": "All accessible already-consumed nonlocked R1-R5 sources; no R6 outcome, locked test, or confirmation data.",
        "lockedFamiliesNeverOpened": sorted(locked),
        "sources": sources,
        "coverageCompleteness": {
            "complete": not missing and not errors,
            "missingRawSources": missing,
            "integrityErrors": errors,
        },
        "summary": summarize(records, floor),
        "duplicateImageHashes": duplicates,
        "rendererNoiseStructuralControl": renderer_noise(repo, protocol),
        "spatialOverlap": spatial_audit(records, protocol),
        "visualStructuralCoverage": representative_visual_audit(records, roots, protocol),
        "verdictInterface": {
            "intermediateStageBCVerdict": "PENDING_IMMUTABLE_STAGE_B_C_HANDOFF",
            "intermediateStageBCAllowedValues": ["GO_R6_BOUNDED_DEVELOPMENT_CAPTURE", "NO_GO"],
            "intermediateStageBCRule": "GO only when a complete immutable handoff passes integrity, positive-control reproducibility, basin stability, and target-independent reachability; otherwise NO_GO with blockers.",
            "integrityStatus": "PASS_WITH_MISSING_RAW" if not errors else "FAIL",
            "historicalCoverageComplete": not missing and not errors,
            "missingRawSources": missing,
            "positiveControlReproducibility": "PENDING_R6_COMPLETE_IMMUTABLE_HANDOFF",
            "basinStability": "PENDING_R6_COMPLETE_IMMUTABLE_HANDOFF",
            "targetIndependentReachability": "PENDING_R6_COMPLETE_IMMUTABLE_HANDOFF",
            "engineeringDemoDataReady": False,
            "scientificTrainingDataReady": False,
            "blockingReasons": [
                "R6 positive-control, basin, and target-independent reachability outcomes have not been read.",
                *(item["impact"] for item in missing),
                *errors,
            ],
        },
        "records": records,
    }
    return report


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build frozen R6 historical provenance/coverage audit.")
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--evidence-repo", type=Path, required=True)
    parser.add_argument("--r5-handoff", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--record-output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo = args.repo.resolve()
    report = build(repo, args.evidence_repo.resolve(), args.r5_handoff.resolve())
    output = args.output or repo / OUTPUT / "g2_r6_historical_coverage_index.json"
    record_output = args.record_output or repo / OUTPUT / "g2_r6_historical_frame_index.jsonl"
    records = report.pop("records")
    write_jsonl(record_output, records)
    report["recordIndex"] = {
        "path": str(record_output.relative_to(repo)).replace("\\", "/"),
        "format": "newline-delimited JSON; one compact record per line",
        "records": len(records),
        "sha256": sha256(record_output),
    }
    write_json(output, report)
    print(json.dumps({
        "output": str(output), "records": report["summary"]["records"],
        "uniqueImageHashes": report["summary"]["uniqueImageHashes"],
        "historicalCoverageComplete": report["coverageCompleteness"]["complete"],
        "missingRawSources": len(report["coverageCompleteness"]["missingRawSources"]),
        "integrityErrors": len(report["coverageCompleteness"]["integrityErrors"]),
    }, indent=2))
    return 0 if not report["coverageCompleteness"]["integrityErrors"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
