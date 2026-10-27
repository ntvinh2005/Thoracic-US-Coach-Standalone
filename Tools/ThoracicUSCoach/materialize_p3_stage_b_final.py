from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from g2_p2_basin_audit import candidate_passes, connected_positive_components
from g2_p2_freshness_audit import canonical_sha256, read_json, write_json
from g2_p3_stage_b_final_audit import PROTOCOL, _adapt_basin_protocol, audit_physical_records
from g2_r6_audit import sha256


def _candidate_results(runtime: dict[str, Any], normalized: list[dict[str, Any]]) -> list[dict[str, Any]]:
    raw = {str(item.get("candidateId")): item for item in runtime.get("candidateResults", [])}
    output: list[dict[str, Any]] = []
    for item in normalized:
        candidate_id = str(item["candidateId"])
        if int(item.get("acceptedFreshCaptures", 0)) > 0:
            output.append(item)
        else:
            source = raw.get(candidate_id, {})
            output.append({
                "candidateId": candidate_id,
                "acceptedFreshCaptures": 0,
                "freshnessPass": False,
                "repeatStable": False,
                "allAcceptable": False,
                "allFinalTargetAcceptable": False,
                "allT7Valid": False,
                "allContactValid": False,
                "allWorkspaceValid": False,
                "rejectionReason": source.get("rejectionReason"),
                "actualResultingPoseRecorded": source.get("actualResultingPoseRecorded") is True,
            })
    return output


def _derive(repo: Path, root: Path, runtime: dict[str, Any], protocol: dict[str, Any]) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    physical = audit_physical_records(repo, root, runtime, protocol)
    substantive = [
        value for value in physical["errors"]
        if not value.startswith("candidate reported metric differs from independent derivation:")
    ]
    if substantive:
        raise RuntimeError("physical evidence invalid before materialization: " + " | ".join(substantive))
    return _candidate_results(runtime, physical["normalizedCandidateResults"]), physical


def _pair_grid(candidates: list[dict[str, Any]], selected_center: str) -> list[list[str]]:
    by_offset = {
        (float(item["longitudinalOffsetMm"]), float(item["lateralOffsetMm"]), float(item["yawDeg"])): item
        for item in candidates
    }
    axes = [sorted({key[index] for key in by_offset}) for index in range(3)]
    pairs: list[list[str]] = []
    for key, item in by_offset.items():
        for axis in range(3):
            index = axes[axis].index(key[axis])
            if index + 1 >= len(axes[axis]):
                continue
            neighbor = list(key)
            neighbor[axis] = axes[axis][index + 1]
            other = by_offset[tuple(neighbor)]
            left = selected_center if item["role"] == "selected_center_repeat" else item["candidateId"]
            right = selected_center if other["role"] == "selected_center_repeat" else other["candidateId"]
            if left != right:
                pairs.append([left, right])
    return pairs


def select_refinement(repo: Path, root: Path, manifest: dict[str, Any], runtime: dict[str, Any]) -> None:
    protocol = read_json(repo / PROTOCOL)
    adapted, errors = _adapt_basin_protocol(repo, protocol)
    if errors:
        raise RuntimeError("reference protocol invalid: " + " | ".join(errors))
    results, _ = _derive(repo, root, runtime, protocol)
    base_ids = {str(item["candidateId"]) for item in manifest["mapCandidates"]}
    by_result = {str(item["candidateId"]): item for item in results}
    if set(by_result) != base_ids or len(results) != 212:
        raise RuntimeError("coarse snapshot must cover exactly 212 base candidates")
    parent_sha = sha256(root / "stage_b_final_manifest.json")
    coarse = {
        "protocolId": protocol["protocolId"],
        "mode": protocol["mode"],
        "sourceCommit": manifest["sourceCommit"],
        "parentManifestSha256": parent_sha,
        "auditProtocolCanonicalSha256": manifest["auditProtocolCanonicalSha256"],
        "complete": True,
        "sealedBeforeRefinementCapture": True,
        "candidateResults": results,
    }
    coarse_path = root / manifest["refinement"]["coarseSnapshotFile"]
    write_json(coarse_path, coarse)

    selections: list[dict[str, Any]] = []
    refined: list[dict[str, Any]] = []
    pairs: list[list[str]] = []
    controls = protocol["fixedGridContract"]["historicalControlIds"]
    for control_index, control in enumerate(controls):
        eligible = [
            item for item in manifest["mapCandidates"]
            if item["anchorControlId"] == control and item["stage"] in {"coarse", "wider"}
            and candidate_passes(by_result[item["candidateId"]], adapted)
        ]
        if not eligible:
            raise RuntimeError(f"no passing coarse/wider candidate for deterministic refinement: {control}")
        center = sorted(
            eligible,
            key=lambda item: (-float(by_result[item["candidateId"]]["minimumScore"]), item["candidateId"]),
        )[0]["candidateId"]
        selections.append({"anchorControlId": control, "selectedCenterCandidateId": center})
        group: list[dict[str, Any]] = []
        for offset_index, offset in enumerate(manifest["refinement"]["offsetTemplate"]):
            zero = all(float(offset[field]) == 0.0 for field in ("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg"))
            item = {
                "candidateId": f"p3_refine_{control_index:02d}_{offset_index:02d}",
                "anchorControlId": control,
                "stage": "refinement",
                "centerCandidateId": center,
                **offset,
                "role": "selected_center_repeat" if zero else "refinement_cell",
                "countsAsDistinctCell": not zero,
                "consumedHistoricalControl": False,
            }
            refined.append(item)
            group.append(item)
        pairs.extend(_pair_grid(group, center))
    selection = {
        "protocolId": protocol["protocolId"],
        "mode": protocol["mode"],
        "sourceCommit": manifest["sourceCommit"],
        "parentManifestSha256": parent_sha,
        "auditProtocolCanonicalSha256": manifest["auditProtocolCanonicalSha256"],
        "generatedBeforeFirstRefinementCapture": True,
        "coarseSnapshotSha256": sha256(coarse_path),
        "selectionRule": manifest["refinement"]["selectionRule"],
        "postOutcomeExtensionAllowed": False,
        "selections": selections,
        "mapCandidates": refined,
        "neighborPairs": pairs,
    }
    write_json(root / manifest["refinement"]["selectionManifestFile"], selection)


def select_representatives(repo: Path, root: Path, manifest: dict[str, Any], runtime: dict[str, Any]) -> None:
    protocol = read_json(repo / PROTOCOL)
    adapted, errors = _adapt_basin_protocol(repo, protocol)
    if errors:
        raise RuntimeError("reference protocol invalid: " + " | ".join(errors))
    results, _ = _derive(repo, root, runtime, protocol)
    selection = read_json(root / manifest["refinement"]["selectionManifestFile"])
    by_result = {str(item["candidateId"]): item for item in results}
    topology_candidates = [
        *manifest["mapCandidates"],
        *(item for item in selection["mapCandidates"] if item["countsAsDistinctCell"] is True),
    ]
    topology_ids = {str(item["candidateId"]) for item in topology_candidates}
    positive = {candidate_id for candidate_id in topology_ids if candidate_passes(by_result[candidate_id], adapted)}
    pairs = [*manifest["neighborPairs"], *selection["neighborPairs"]]
    components = connected_positive_components(positive, pairs, topology_candidates)
    eligible = [group for group in components if len(group) >= int(protocol["positiveBasinGate"]["minimumConnectedAcceptableCells"])]
    representatives = []
    for index, component in enumerate(eligible):
        source = sorted(component, key=lambda value: (-float(by_result[value]["minimumScore"]), value))[0]
        representatives.append({
            "representativeId": f"stage_b_representative_{index:03d}",
            "sourceCandidateId": source,
            "component": component,
        })
    write_json(root / "stage_b_representative_plan.json", {"representatives": representatives})


def finalize_report(repo: Path, root: Path, manifest: dict[str, Any], runtime: dict[str, Any]) -> None:
    protocol = read_json(repo / PROTOCOL)
    results, _ = _derive(repo, root, runtime, protocol)
    attempted = sorted(str(value) for value in runtime.get("attemptedCandidateIds", []))
    if len(attempted) != 320 or len(set(attempted)) != 320:
        raise RuntimeError("final report requires exactly 320 unique attempted candidate IDs")
    representatives = []
    for item in runtime.get("representativeRuntime", []):
        if item.get("recordIds"):
            representatives.append({
                "representativeId": item["representativeId"],
                "sourceCandidateId": item["sourceCandidateId"],
                "recordIds": item["recordIds"],
            })
    report = {
        "schemaVersion": 1,
        "taskId": runtime.get("taskId"),
        "protocolId": manifest["protocolId"],
        "mode": manifest["mode"],
        "sourceCommit": manifest["sourceCommit"],
        "manifestSha256": sha256(root / "stage_b_final_manifest.json"),
        "auditProtocolCanonicalSha256": canonical_sha256(repo / PROTOCOL),
        "upstreamStageFIndependentAuditSha256": manifest["upstreamStageFIndependentAuditSha256"],
        "complete": True,
        "infrastructureFailure": "",
        "attemptedCandidateIds": attempted,
        "candidateResults": results,
        "representatives": representatives,
        "records": runtime["records"],
    }
    write_json(root / "stage_b_final_report.json", report)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("select-refinement", "select-representatives", "finalize-report"))
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--runtime-report", type=Path, required=True)
    args = parser.parse_args()
    repo, root = args.repo.resolve(), args.root.resolve()
    manifest, runtime = read_json(args.manifest.resolve()), read_json(args.runtime_report.resolve())
    if args.mode == "select-refinement":
        select_refinement(repo, root, manifest, runtime)
    elif args.mode == "select-representatives":
        select_representatives(repo, root, manifest, runtime)
    else:
        finalize_report(repo, root, manifest, runtime)
    print(json.dumps({"mode": args.mode, "status": "PASS"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
