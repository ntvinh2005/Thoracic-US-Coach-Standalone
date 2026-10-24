from __future__ import annotations

import argparse
import hashlib
import json
import math
import subprocess
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


UNITY_FLOAT_ABS_TOL = 1e-6


def unity_float_matches(actual: float, expected: float) -> bool:
    """Compare values serialized from Unity's 32-bit float fields."""
    return math.isclose(float(actual), float(expected), rel_tol=0.0,
                        abs_tol=UNITY_FLOAT_ABS_TOL)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def project_root() -> Path:
    return Path(__file__).resolve().parents[2]


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _vec(value: dict[str, Any]) -> tuple[float, float, float]:
    return float(value["x"]), float(value["y"]), float(value["z"])


def _sub(a: tuple[float, float, float], b: tuple[float, float, float]) -> tuple[float, float, float]:
    return a[0] - b[0], a[1] - b[1], a[2] - b[2]


def _dot(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    return sum(x * y for x, y in zip(a, b))


def _norm(a: tuple[float, float, float]) -> float:
    return math.sqrt(_dot(a, a))


def _assert_close(actual: float, expected: float, name: str, tolerance: float = 1e-4) -> None:
    if not math.isclose(float(actual), float(expected), abs_tol=tolerance):
        raise ValueError(f"{name} mismatch: {actual} != {expected}")


def recompute_start_geometry(start: dict[str, Any], step_budget: int) -> dict[str, Any]:
    contact = _vec(start["contactPosition"])
    target = _vec(start["t7TargetPosition"])
    axis = _vec(start["caudalAxis"])
    axis_norm = _norm(axis)
    if not math.isclose(axis_norm, 1.0, abs_tol=1e-4):
        raise ValueError("caudal axis is not normalized")
    delta = _sub(target, contact)
    longitudinal = _dot(delta, axis)
    residual = tuple(delta[index] - axis[index] * longitudinal for index in range(3))
    radius = float(start["t7PreciseRadiusMm"])
    step_mm = float(start["stepMm"])
    available = min(
        int(start["preflightMaxSteps"]), int(step_budget),
        math.floor(float(start["longitudinalHalfExtentMm"]) / step_mm),
    )
    closest = math.inf
    minimum_steps: int | None = None
    minimum_signed_step = 0
    for signed_step in range(-available, available + 1):
        remaining = longitudinal - signed_step * step_mm
        distance = math.sqrt(_dot(residual, residual) + remaining * remaining)
        closest = min(closest, distance)
        if distance <= radius and (minimum_steps is None or abs(signed_step) < minimum_steps):
            minimum_steps = abs(signed_step)
            minimum_signed_step = signed_step
    reachable = minimum_steps is not None
    direction = "UNREACHABLE" if not reachable else (
        "CAUDAL" if minimum_signed_step > 0 else "CRANIAL" if minimum_signed_step < 0 else "AT_T7")
    derived = {
        "longitudinal": longitudinal,
        "lateral": _dot(delta, _vec(start["lateralAxis"])),
        "t7Distance": _norm(delta),
        "closest": closest,
        "reachable": reachable,
        "minimumSteps": minimum_steps if minimum_steps is not None else -1,
        "travel": minimum_steps * step_mm if minimum_steps is not None else -1.0,
        "direction": direction,
    }
    _assert_close(start["t7LongitudinalOffsetMm"], derived["longitudinal"], "start longitudinal")
    _assert_close(start["t7LateralOffsetMm"], derived["lateral"], "start lateral")
    _assert_close(start["t7DistanceMm"], derived["t7Distance"], "start T7 distance")
    _assert_close(start["closestAttainableT7DistanceMm"], derived["closest"], "closest attainable T7 distance")
    _assert_close(start["longitudinalTravelToPreciseRegionMm"], derived["travel"], "T7 travel")
    if bool(start["t7Reachable"]) != reachable or int(start["minimumLongitudinalSteps"]) != derived["minimumSteps"]:
        raise ValueError("start reachability mismatch")
    if start["t7Direction"] != direction:
        raise ValueError("start direction mismatch")
    return derived


def recompute(manifest: dict[str, Any], report: dict[str, Any], root: Path) -> dict[str, Any]:
    cases_by_id = {case["caseId"]: case for case in manifest["cases"]}
    if len(cases_by_id) != 40 or len(report.get("cases", [])) != 40:
        raise ValueError("Gate2 requires exactly 40 unique manifest cases and 40 result cases")
    expected_ids = [case["caseId"] for case in sorted(manifest["cases"], key=lambda item: item["order"])]
    if [case.get("caseId") for case in report["cases"]] != expected_ids:
        raise ValueError("Gate2 result case IDs/order do not exactly match the manifest")
    if len(report.get("preflight", [])) != 40:
        raise ValueError("Gate2 requires a complete 40-case preflight")
    if [case.get("caseId") for case in report["preflight"]] != expected_ids:
        raise ValueError("Gate2 preflight case IDs/order do not exactly match the manifest")
    for preflight in report["preflight"]:
        planned = cases_by_id[preflight["caseId"]]
        derived = recompute_start_geometry(preflight["start"], int(manifest["stepBudget"]))
        physical_trace = preflight.get("physicalTrace", [])
        initially_precise = (preflight["start"]["actualLevel"] == "T7"
                             and float(preflight["start"]["t7DistanceMm"])
                             <= float(preflight["start"]["t7PreciseRadiusMm"]))
        physically_reached = initially_precise or any(
            bool(item["t7LevelAcceptable"]) for item in physical_trace)
        physically_unsafe = any(
            not item["contactValid"] or not item["workspaceValid"] or item["safetyFailure"]
            for item in physical_trace)
        closest_observed = min(
            [float(preflight["start"]["t7DistanceMm"])]
            + [float(item["t7DistanceMm"]) for item in physical_trace])
        if not preflight.get("physicalReachabilityTested"):
            raise ValueError(f"{planned['caseId']} missing physical reachability test")
        if bool(preflight["physicalReachedPreciseRegion"]) != physically_reached:
            raise ValueError(f"{planned['caseId']} physical reachability mismatch")
        if bool(preflight["physicalUnsafe"]) != physically_unsafe:
            raise ValueError(f"{planned['caseId']} physical safety mismatch")
        if int(preflight["physicalSteps"]) != len(physical_trace):
            raise ValueError(f"{planned['caseId']} physical step count mismatch")
        if int(preflight["physicalRejectedMoves"]) != sum(not item["accepted"] for item in physical_trace):
            raise ValueError(f"{planned['caseId']} physical rejection count mismatch")
        _assert_close(preflight["closestObservedT7DistanceMm"], closest_observed,
                      f"{planned['caseId']} closest observed distance")
        valid = (bool(preflight["placementSucceeded"])
                 and preflight["start"]["actualLevel"] == planned["expectedStartLevel"]
                 and bool(preflight["start"]["contactValid"])
                 and bool(preflight["start"]["workspaceValid"])
                 and derived["reachable"] and physically_reached and not physically_unsafe)
        if bool(preflight["valid"]) != valid:
            raise ValueError(f"{planned['caseId']} preflight validity mismatch")

    recomputed_cases: list[dict[str, Any]] = []
    for outcome in report["cases"]:
        planned = cases_by_id[outcome["caseId"]]
        raw_path = root / outcome["rawResult"]
        privileged_path = root / outcome["privilegedTrace"]
        raw = load_json(raw_path)
        privileged = load_json(privileged_path)
        frames = privileged.get("frames", [])
        start = next((frame for frame in frames if int(frame["index"]) == -1), None)
        if start is None:
            raise ValueError(f"{planned['caseId']} has no privileged start frame")
        start_audit = outcome["start"]
        derived_start = recompute_start_geometry(start_audit, int(manifest["stepBudget"]))
        for name, evidence in (
            ("contactPosition", start["contactPosition"]),
            ("t7TargetPosition", privileged["t7TargetPosition"]),
            ("lateralAxis", privileged["actuatorLateralAxis"]),
        ):
            for actual, expected in zip(_vec(start_audit[name]), _vec(evidence)):
                _assert_close(actual, expected, f"{planned['caseId']} {name}")
        caudal_evidence = tuple(-value for value in _vec(privileged["actuatorCranialAxis"]))
        for actual, expected in zip(_vec(start_audit["caudalAxis"]), caudal_evidence):
            _assert_close(actual, expected, f"{planned['caseId']} caudalAxis")
        _assert_close(start_audit["stepMm"], privileged["stepMm"], "privileged stepMm")
        _assert_close(start_audit["t7PreciseRadiusMm"], privileged["t7PreciseRadiusMm"], "privileged radius")
        trace = raw.get("trace", [])
        handoff = next((item for item in trace if item.get("action") == "PHASEHANDOFF"), None)
        precise_frames = sorted(
            (frame for frame in frames if int(frame["index"]) >= 0 and frame.get("t7LevelAcceptable")),
            key=lambda frame: int(frame["index"]),
        )
        first_precise = int(precise_frames[0]["index"]) if precise_frames else -1
        handoff_step = int(handoff["step"]) if handoff else -1
        handoff_confidence = float(handoff["confidence"]) if handoff else 0.0
        handoff_frame = next((frame for frame in frames if handoff and int(frame["index"]) == handoff_step), None)
        accepted_handoff = bool(handoff and handoff.get("accepted"))
        terminal_evidence = handoff or (trace[-1] if trace else None)
        terminal_contact = (bool(terminal_evidence["contactValidAfterExecution"])
                            if terminal_evidence else bool(frames[-1]["contactValid"]))
        terminal_workspace = (bool(terminal_evidence["workspaceValidAfterExecution"])
                              if terminal_evidence else bool(frames[-1]["workspaceValid"]))
        precise_handoff = bool(handoff_frame and handoff_frame.get("t7LevelAcceptable"))
        level_correct = bool(handoff_frame and handoff_frame.get("nearestLevel") == "T7")
        correct = accepted_handoff and precise_handoff and terminal_contact and terminal_workspace
        wrong = accepted_handoff and not level_correct
        actual_level = start["nearestLevel"]
        valid_start = (
            actual_level == planned["expectedStartLevel"]
            and bool(start["contactValid"])
            and bool(start["workspaceValid"])
            and derived_start["reachable"]
        )
        terminal_actions = {"PHASEHANDOFF", "STOP"}
        unsafe_trace = sum(
            (bool(item.get("accepted")) or item.get("action") in terminal_actions)
            and (not item.get("contactValidAfterExecution") or not item.get("workspaceValidAfterExecution"))
            for item in trace
        )
        action_counts = Counter(item.get("action", "") for item in trace)
        expected_episode = f"G2E_{report['runNonce']}_{int(planned['order']):04d}"
        forbidden = (planned["caseId"], planned["family"], planned["expectedStartLevel"], str(planned["seed"]))
        actor_values = [str(raw.get("episodeId", ""))] + [str(item.get("image", "")) for item in trace]
        leakage = int(raw.get("episodeId") != expected_episode)
        leakage += sum(
            expected_episode not in value
            or any(secret.lower() in value.lower() for secret in forbidden)
            for value in actor_values[1:]
        )
        recomputed_cases.append({
            "caseId": planned["caseId"],
            "startStratum": int(planned["startStratum"]),
            "actualStartLevel": actual_level,
            "validStart": valid_start,
            "completed": raw.get("finalPhase") in {"FinalSuccess", "FinalFailure"},
            "correctPreciseHandoff": correct,
            "handoffNearestLevel": handoff_frame.get("nearestLevel", "") if handoff_frame else "",
            "handoffT7DistanceMm": float(handoff_frame["t7DistanceMm"]) if handoff_frame else -1.0,
            "handoffNearestLevelCorrect": level_correct,
            "handoffT7LevelAcceptable": precise_handoff,
            "wrongLevelHandoff": wrong,
            "confidentWrongLevelHandoff": wrong
            and handoff_confidence >= float(manifest["handoffThreshold"]),
            "preciseRegionEntered": bool(precise_frames),
            "recognitionAfterPreciseEntry": correct and first_precise >= 0 and handoff_step >= first_precise,
            "finalContactValid": terminal_contact,
            "finalWorkspaceValid": terminal_workspace,
            "unsafeAcceptedOrTerminalPoses": unsafe_trace,
            "privilegedUnsafeFrames": sum(
                not frame.get("contactValid") or not frame.get("workspaceValid") for frame in frames),
            "safetyFailures": sum(bool(item.get("safetyFailure")) for item in trace),
            "rejectedMovements": int(raw.get("rejectedMovements", 0)),
            "contactViolations": int(raw.get("contactViolations", 0)),
            "workspaceViolations": int(raw.get("workspaceViolations", 0)),
            "timeout": "timeout" in str(raw.get("failureReason", "")).lower(),
            "abstention": not accepted_handoff,
            "failure": raw.get("finalPhase") == "FinalFailure",
            "actionCounts": dict(sorted(action_counts.items())),
            "actorPrivilegedLeakage": leakage,
            "rawVerdictMismatch": bool(raw.get("overallPass")) != correct
            or bool(raw.get("phaseOneHandoff")) != accepted_handoff
            or bool(raw.get("correctLevelAtHandoff")) != precise_handoff,
        })

    entered = sum(case["preciseRegionEntered"] for case in recomputed_cases)
    recognized = sum(case["recognitionAfterPreciseEntry"] for case in recomputed_cases)
    summary = {
        "requiredCases": int(manifest["requiredCaseCount"]),
        "completedCases": sum(case["completed"] for case in recomputed_cases),
        "t6Starts": sum(case["actualStartLevel"] == "T6" for case in recomputed_cases),
        "t7Starts": sum(case["actualStartLevel"] == "T7" for case in recomputed_cases),
        "t8Starts": sum(case["actualStartLevel"] == "T8" for case in recomputed_cases),
        "outsideStarts": sum(case["actualStartLevel"] == "OUTSIDE_T6_T8" for case in recomputed_cases),
        "validStarts": sum(case["validStart"] for case in recomputed_cases),
        "correctPreciseHandoffs": sum(case["correctPreciseHandoff"] for case in recomputed_cases),
        "wrongLevelHandoffs": sum(case["wrongLevelHandoff"] for case in recomputed_cases),
        "confidentWrongLevelHandoffs": sum(case["confidentWrongLevelHandoff"] for case in recomputed_cases),
        "preciseRegionEntries": entered,
        "recognitionsAfterPreciseEntry": recognized,
        "recognitionAfterPreciseEntryRate": recognized / entered if entered else 0.0,
        "finalContactValidCount": sum(case["finalContactValid"] for case in recomputed_cases),
        "finalWorkspaceValidCount": sum(case["finalWorkspaceValid"] for case in recomputed_cases),
        "unsafeAcceptedOrTerminalPoses": sum(case["unsafeAcceptedOrTerminalPoses"] for case in recomputed_cases),
        "privilegedUnsafeFrames": sum(case["privilegedUnsafeFrames"] for case in recomputed_cases),
        "safetyFailures": sum(case["safetyFailures"] for case in recomputed_cases),
        "rejectedMovements": sum(case["rejectedMovements"] for case in recomputed_cases),
        "contactViolations": sum(case["contactViolations"] for case in recomputed_cases),
        "workspaceViolations": sum(case["workspaceViolations"] for case in recomputed_cases),
        "timeouts": sum(case["timeout"] for case in recomputed_cases),
        "abstentions": sum(case["abstention"] for case in recomputed_cases),
        "failures": sum(case["failure"] for case in recomputed_cases),
        "actorPrivilegedLeakage": sum(case["actorPrivilegedLeakage"] for case in recomputed_cases),
        "rawVerdictMismatches": sum(case["rawVerdictMismatch"] for case in recomputed_cases),
    }
    actions: Counter[str] = Counter()
    strata: dict[int, list[dict[str, Any]]] = defaultdict(list)
    for case in recomputed_cases:
        actions.update(case["actionCounts"])
        strata[case["startStratum"]].append(case)
    summary["actionCounts"] = [{"name": name, "count": count} for name, count in sorted(actions.items())]
    summary["perStratum"] = [{
        "startStratum": stratum,
        "expectedLevel": next(item["expectedStartLevel"] for item in manifest["cases"]
                              if item["startStratum"] == stratum),
        "cases": len(items),
        "validStarts": sum(item["validStart"] for item in items),
        "correctPreciseHandoffs": sum(item["correctPreciseHandoff"] for item in items),
        "wrongLevelHandoffs": sum(item["wrongLevelHandoff"] for item in items),
        "abstentions": sum(item["abstention"] for item in items),
    } for stratum, items in sorted(strata.items())]
    summary["protocolComplete"] = (
        bool(report.get("complete"))
        and summary["completedCases"] == manifest["requiredCaseCount"]
        and summary["validStarts"] == manifest["requiredCaseCount"]
        and len(report["preflight"]) == manifest["requiredCaseCount"]
        and all(item["valid"] for item in report["preflight"])
        and all(summary[name] == manifest["requiredPerStratum"] for name in
                ("t6Starts", "t7Starts", "t8Starts", "outsideStarts"))
        and summary["actorPrivilegedLeakage"] == 0
        and summary["rawVerdictMismatches"] == 0
    )
    summary["thresholdsPass"] = (
        summary["protocolComplete"]
        and summary["correctPreciseHandoffs"] >= manifest["minimumCorrectPreciseHandoffs"]
        and summary["wrongLevelHandoffs"] <= manifest["maximumWrongLevelHandoffs"]
        and summary["confidentWrongLevelHandoffs"] <= manifest["maximumConfidentWrongLevelHandoffs"]
        and summary["recognitionAfterPreciseEntryRate"]
        >= manifest["minimumRecognitionAfterPreciseEntry"]
        and summary["finalContactValidCount"] == manifest["requiredCaseCount"]
        and summary["finalWorkspaceValidCount"] == manifest["requiredCaseCount"]
        and summary["unsafeAcceptedOrTerminalPoses"] == 0
        and summary["privilegedUnsafeFrames"] == 0
        and summary["safetyFailures"] == 0
        and summary["actorPrivilegedLeakage"] == 0
        and summary["rawVerdictMismatches"] == 0
    )
    return {"schemaVersion": 1, "protocolId": manifest["protocolId"],
            "cases": recomputed_cases, "summary": summary}


def validate_frozen_protocol(manifest: dict[str, Any], root: Path) -> None:
    if manifest["requiredCaseCount"] != 40 or manifest["requiredPerStratum"] != 10:
        raise ValueError("invalid Gate2 case allocation")
    if not manifest["phaseOneOnly"] or manifest["stepBudget"] != 300:
        raise ValueError("Gate2 must be Phase1-only with a 300-step budget")
    if manifest["hybridEnabled"] or manifest["stopCheckpoint"] or manifest["landmarkCheckpoint"]:
        raise ValueError("Gate2 cannot use hybrid, stop, or landmark checkpoints")
    if not math.isclose(float(manifest["handoffThreshold"]), 0.85, abs_tol=1e-9):
        raise ValueError("Gate2 threshold must be exactly 0.85")
    if sha256(root / manifest["checkpoint"]) != manifest["checkpointSha256"]:
        raise ValueError("checkpoint hash mismatch")
    if sha256(root / manifest["policyServer"]) != manifest["policyServerSha256"]:
        raise ValueError("policy server hash mismatch")


def validate_report_binding(
        manifest: dict[str, Any], report: dict[str, Any], root: Path, manifest_path: Path) -> None:
    exact_fields = {
        "protocolId": manifest["protocolId"],
        "manifestSha256": sha256(manifest_path),
        "manifestSourceCommit": manifest["sourceCommit"],
        "checkpoint": manifest["checkpoint"],
        "checkpointSha256": manifest["checkpointSha256"],
        "policyServer": manifest["policyServer"],
        "policyServerSha256": manifest["policyServerSha256"],
        "phaseOneOnly": manifest["phaseOneOnly"],
        "stepBudget": manifest["stepBudget"],
        "unityVersion": manifest["unityVersion"],
        "scene": manifest["scene"],
    }
    for name, expected in exact_fields.items():
        if report.get(name) != expected:
            raise ValueError(f"report {name} is not bound to the frozen manifest")
    if not unity_float_matches(report.get("handoffThreshold", -1),
                               manifest["handoffThreshold"]):
        raise ValueError("report handoffThreshold is not bound to the frozen manifest")
    execution_commit = subprocess.check_output(
        ["git", "-c", f"safe.directory={root.as_posix()}", "-C", str(root), "rev-parse", "HEAD"],
        text=True).strip()
    if report.get("executionCommit") != execution_commit:
        raise ValueError("report executionCommit does not equal current HEAD")
    if report.get("runNonce") != execution_commit[:12]:
        raise ValueError("report runNonce does not match the wrapper rule")
    if not report.get("complete") or report.get("infrastructureFailure"):
        raise ValueError("Gate2 result is incomplete or has an infrastructure failure")
    expected_cases = sorted(manifest["cases"], key=lambda item: item["order"])
    if [(item.get("caseId"), item.get("order")) for item in report.get("cases", [])] != [
            (item["caseId"], item["order"]) for item in expected_cases]:
        raise ValueError("result case IDs/order do not exactly match the manifest")
    if [(item.get("caseId"), item.get("order")) for item in report.get("preflight", [])] != [
            (item["caseId"], item["order"]) for item in expected_cases]:
        raise ValueError("preflight case IDs/order do not exactly match the manifest")
    for planned, outcome in zip(expected_cases, report["cases"]):
        episode = f"G2E_{report['runNonce']}_{int(planned['order']):04d}"
        expected_raw = f"Artifacts/ThoracicUSCoach/active2d/runs/{episode}.json"
        expected_privileged = f"Artifacts/ThoracicUSCoach/active2d/runs_privileged/{episode}.json"
        if outcome.get("rawResult") != expected_raw or outcome.get("privilegedTrace") != expected_privileged:
            raise ValueError(f"{planned['caseId']} trace path escapes or differs from its exact artifact path")
        for relative in (expected_raw, expected_privileged):
            resolved = (root / relative).resolve()
            if root.resolve() not in resolved.parents:
                raise ValueError(f"trace path escapes project root: {relative}")


def compare_serialized(actual: Any, expected: Any, path: str = "summary") -> None:
    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            raise ValueError(f"{path} type mismatch")
        for name, value in expected.items():
            if name not in actual:
                raise ValueError(f"{path}.{name} is missing")
            compare_serialized(actual[name], value, f"{path}.{name}")
        return
    if isinstance(expected, list):
        if not isinstance(actual, list) or len(actual) != len(expected):
            raise ValueError(f"{path} list mismatch")
        for index, value in enumerate(expected):
            compare_serialized(actual[index], value, f"{path}[{index}]")
        return
    if isinstance(expected, float):
        if not math.isclose(float(actual), expected, abs_tol=1e-6):
            raise ValueError(f"{path} float mismatch: {actual} != {expected}")
        return
    if actual != expected:
        raise ValueError(f"{path} mismatch: {actual} != {expected}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Recompute frozen Gate2 Phase-1 metrics from raw Unity traces")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--result", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = project_root()
    manifest = load_json(args.manifest)
    report = load_json(args.result)
    validate_frozen_protocol(manifest, root)
    validate_report_binding(manifest, report, root, args.manifest)
    evaluated = recompute(manifest, report, root)
    compare_serialized(report["summary"], evaluated["summary"])
    output = args.output or args.result.with_name(args.result.stem + "_recomputed.json")
    output.write_text(json.dumps(evaluated, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(evaluated["summary"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
