from __future__ import annotations

import math
from collections import defaultdict
from typing import Any


OFFSET_FIELDS = ("longitudinalOffsetMm", "lateralOffsetMm", "yawDeg")


def _offset(candidate: dict[str, Any]) -> tuple[float, float, float]:
    return tuple(float(candidate[field]) for field in OFFSET_FIELDS)


def _offset_key(value: tuple[float, float, float]) -> tuple[float, float, float]:
    return tuple(round(item, 6) for item in value)


def validate_grid(manifest: dict[str, Any], protocol: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    candidates = manifest.get("mapCandidates", [])
    maximum = int(protocol["frozenThresholds"]["maximumCandidatePoses"])
    if not candidates or len(candidates) > maximum:
        errors.append(f"candidate count must be 1..{maximum}")
    ids = [str(item.get("candidateId", "")) for item in candidates]
    if any(not value for value in ids) or len(ids) != len(set(ids)):
        errors.append("candidate IDs missing or duplicated")
    controls = set(protocol["gridContract"]["historicalControlIds"])
    declared_controls = {str(item.get("anchorControlId", "")) for item in candidates}
    if not declared_controls.issubset(controls) or not controls.issubset(declared_controls):
        errors.append("historical control coverage mismatch")
    allowed_stages = set(protocol["gridContract"]["requiredStages"])
    allowed_stages.add(protocol["gridContract"]["optionalStage"])
    if any(item.get("stage") not in allowed_stages for item in candidates):
        errors.append("candidate stage invalid")

    exact_anchor_ids: set[str] = set()
    for control_id in sorted(controls):
        anchors = [
            item for item in candidates
            if item.get("anchorControlId") == control_id
            and item.get("stage") == "anchor"
            and _offset_key(_offset(item)) == (0.0, 0.0, 0.0)
        ]
        if len(anchors) != 1:
            errors.append(f"exact anchor count is not one: {control_id}")
        else:
            exact_anchor_ids.add(anchors[0]["candidateId"])

    groups: dict[tuple[str, str, str], list[dict[str, Any]]] = defaultdict(list)
    for item in candidates:
        if item.get("stage") == "anchor":
            continue
        center = str(item.get("centerCandidateId", "ANCHOR"))
        groups[(str(item.get("anchorControlId")), str(item.get("stage")), center)].append(item)
    for group_id, group in sorted(groups.items()):
        offsets = {_offset_key(_offset(item)) for item in group}
        for offset in offsets:
            opposite = _offset_key(tuple(-value for value in offset))
            if opposite not in offsets:
                errors.append(f"asymmetric grid group {group_id}: {offset} lacks {opposite}")
        if group_id[1] == "coarse":
            for axis in range(3):
                if not any(value[axis] > 0 for value in offsets) or not any(value[axis] < 0 for value in offsets):
                    errors.append(f"coarse grid lacks signed axis {axis}: {group_id}")

    known = set(ids)
    pairs = manifest.get("neighborPairs", [])
    normalized_pairs: set[tuple[str, str]] = set()
    for pair in pairs:
        if not isinstance(pair, list) or len(pair) != 2 or pair[0] not in known or pair[1] not in known or pair[0] == pair[1]:
            errors.append("neighbor pair malformed or references unknown candidate")
            continue
        normalized_pairs.add(tuple(sorted((pair[0], pair[1]))))
    if not pairs:
        errors.append("neighbor pairs missing")
    elif len(normalized_pairs) != len(pairs):
        errors.append("neighbor pairs missing or duplicated")
    return errors


def connected_positive_components(
    positive_candidate_ids: set[str], neighbor_pairs: list[list[str]], candidates: list[dict[str, Any]],
) -> list[list[str]]:
    by_id = {item["candidateId"]: item for item in candidates}
    parent = {value: value for value in positive_candidate_ids}

    def find(value: str) -> str:
        while parent[value] != value:
            parent[value] = parent[parent[value]]
            value = parent[value]
        return value

    for pair in neighbor_pairs:
        if len(pair) != 2 or pair[0] not in parent or pair[1] not in parent:
            continue
        left, right = by_id[pair[0]], by_id[pair[1]]
        if left.get("anchorControlId") != right.get("anchorControlId"):
            continue
        left_root, right_root = find(pair[0]), find(pair[1])
        if left_root != right_root:
            parent[right_root] = left_root
    groups: dict[str, list[str]] = defaultdict(list)
    for value in sorted(parent):
        groups[find(value)].append(value)
    return sorted((sorted(group) for group in groups.values()), key=lambda group: (-len(group), group))


def representative_passes(record: dict[str, Any], protocol: dict[str, Any]) -> bool:
    limits = protocol["frozenThresholds"]
    repeats = record.get("repeats", [])
    if len(repeats) < int(limits["minimumFreshRepeatsPerRepresentative"]):
        return False
    scores = [float(item.get("score", {}).get("total", -math.inf)) for item in repeats]
    strict = all(
        item.get("freshnessPass") is True
        and item.get("acceptable") is True
        and item.get("finalTargetAcceptable") is True
        and item.get("t7Valid") is True
        and item.get("contactValid") is True
        and item.get("workspaceValid") is True
        for item in repeats
    )
    return (
        strict
        and min(scores) >= float(limits["acceptableScoreFloor"]) + float(limits["minimumInteriorScoreMargin"])
        and max(scores) - min(scores) <= float(limits["maximumWithinPoseScoreRange"])
        and float(record.get("maximumRms128", math.inf)) <= float(limits["maximumWithinPoseRms128"])
        and float(record.get("maximumStructuralDistance", math.inf))
        <= float(limits["maximumWithinPoseStructuralDistance"])
    )


def candidate_passes(record: dict[str, Any], protocol: dict[str, Any]) -> bool:
    limits = protocol["frozenThresholds"]
    return (
        int(record.get("acceptedFreshCaptures", 0)) >= int(limits["minimumFreshCapturesPerCandidate"])
        and record.get("freshnessPass") is True
        and record.get("repeatStable") is True
        and record.get("allAcceptable") is True
        and record.get("allFinalTargetAcceptable") is True
        and record.get("allT7Valid") is True
        and record.get("allContactValid") is True
        and record.get("allWorkspaceValid") is True
        and float(record.get("minimumScore", -math.inf)) >= float(limits["acceptableScoreFloor"])
        and float(record.get("scoreRange", math.inf)) <= float(limits["maximumWithinPoseScoreRange"])
        and float(record.get("maximumRms128", math.inf)) <= float(limits["maximumWithinPoseRms128"])
        and float(record.get("maximumStructuralDistance", math.inf))
        <= float(limits["maximumWithinPoseStructuralDistance"])
    )


def basin_decision(manifest: dict[str, Any], evidence: dict[str, Any], protocol: dict[str, Any]) -> dict[str, Any]:
    grid_errors = validate_grid(manifest, protocol)
    candidates = manifest.get("mapCandidates", [])
    declared_ids = {item["candidateId"] for item in candidates}
    attempted_ids = set(evidence.get("attemptedCandidateIds", []))
    exact_anchor_ids = {
        item["candidateId"] for item in candidates
        if item.get("stage") == "anchor" and _offset_key(_offset(item)) == (0.0, 0.0, 0.0)
    }
    if attempted_ids != declared_ids:
        grid_errors.append("attempted candidate identities differ from the pre-frozen grid")
    if not exact_anchor_ids.issubset(attempted_ids):
        grid_errors.append("every exact anchor must be attempted")
    result_by_id = {item.get("candidateId"): item for item in evidence.get("candidateResults", [])}
    if set(result_by_id) != declared_ids:
        grid_errors.append("every declared candidate requires a retained result or rejection reason")
    positive = {
        candidate_id for candidate_id, item in result_by_id.items()
        if candidate_id in declared_ids and candidate_passes(item, protocol)
    }
    components = connected_positive_components(positive, manifest.get("neighborPairs", []), candidates)
    minimum = int(protocol["frozenThresholds"]["minimumConnectedAcceptableCells"])
    eligible_components = [component for component in components if len(component) >= minimum]
    representative_results = []
    expected_representatives = {
        tuple(component): sorted(
            component,
            key=lambda candidate_id: (
                -float(result_by_id[candidate_id].get("minimumScore", -math.inf)), candidate_id
            ),
        )[0]
        for component in eligible_components
    }
    for item in evidence.get("representatives", []):
        source = item.get("sourceCandidateId")
        component = next((group for group in eligible_components if source in group), None)
        selected_by_frozen_rule = (
            component is not None and expected_representatives.get(tuple(component)) == source
        )
        passed = selected_by_frozen_rule and representative_passes(item, protocol)
        representative_results.append({
            "representativeId": item.get("representativeId"),
            "sourceCandidateId": source,
            "component": component,
            "selectedByFrozenRule": selected_by_frozen_rule,
            "pass": passed,
        })
    passed = (
        evidence.get("integrityStatus") == "PASS"
        and evidence.get("imagePoseFreshness") == "PASS"
        and not grid_errors
        and bool(eligible_components)
        and any(item["pass"] for item in representative_results)
        and attempted_ids == declared_ids
    )
    return {
        "verdict": protocol["verdicts"]["pass"] if passed else protocol["verdicts"]["fail"],
        "authorizedNextStage": passed,
        "gridErrors": grid_errors,
        "strictPositiveCandidateIds": sorted(positive),
        "positiveComponents": components,
        "eligiblePositiveComponents": eligible_components,
        "representatives": representative_results,
    }
