from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable, Sequence


CONTRACT_VERSION = "thoracic-1d-contract-v3"
TARGET_SEMANTICS = "fixed-pose-exact-set"
OBSERVATION_SCHEMA = "clean-current-png+previous-clean-png+executed-previous-action-v3"
EXPERIMENT_ID = "thoracic_1d_contract_v3_20261022"


@dataclass(frozen=True)
class StepResult:
    previous_index: int
    index: int
    action: int
    target_acceptable: bool
    contact_valid: bool
    workspace_valid: bool
    invalid_motion: bool
    boundary_violation: bool
    explicit_stop_success: bool
    terminated: bool
    truncated: bool
    reward: float
    distance_to_acceptable_mm: float

    @property
    def done(self) -> bool:
        return self.terminated or self.truncated


def canonical_json_sha256(payload: object) -> str:
    encoded = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def step_index(index: int, action: int, step_count: int, path_count: int) -> int:
    if action not in (-1, 0, 1):
        raise ValueError(f"Unknown action {action}")
    if path_count <= 0:
        raise ValueError("path_count must be positive")
    delta = action * max(1, int(step_count))
    return min(path_count - 1, max(0, index + delta))


def nearest_acceptable(acceptable: Sequence[bool], index: int) -> int | None:
    candidates = [candidate for candidate, value in enumerate(acceptable) if value]
    return min(candidates, key=lambda candidate: (abs(candidate - index), candidate)) if candidates else None


def distance_to_acceptable_mm(acceptable: Sequence[bool], path_mm: Sequence[float], index: int) -> float:
    nearest = nearest_acceptable(acceptable, index)
    if nearest is None:
        span = abs(path_mm[-1] - path_mm[0]) if len(path_mm) > 1 else 0.0
        spacing = abs(path_mm[1] - path_mm[0]) if len(path_mm) > 1 else 1.0
        return span + spacing
    return abs(float(path_mm[index]) - float(path_mm[nearest]))


def _shortest_steps_to_acceptable(acceptable: Sequence[bool], start: int, step_count: int) -> int | None:
    if acceptable[start]:
        return 0
    visited = {start}
    frontier = [(start, 0)]
    while frontier:
        current, distance = frontier.pop(0)
        for action in (-1, 1):
            candidate = step_index(current, action, step_count, len(acceptable))
            if candidate in visited:
                continue
            if acceptable[candidate]:
                return distance + 1
            visited.add(candidate)
            frontier.append((candidate, distance + 1))
    return None


def oracle_action(acceptable: Sequence[bool], index: int, step_count: int = 1) -> int:
    if acceptable[index]:
        return 0
    if nearest_acceptable(acceptable, index) is None:
        return 0
    options = []
    for action in (-1, 1):
        candidate = step_index(index, action, step_count, len(acceptable))
        if candidate == index:
            continue
        distance = _shortest_steps_to_acceptable(acceptable, candidate, step_count)
        if distance is not None:
            options.append((distance, -action, action))
    return min(options)[2] if options else 0


def transition(
    *,
    index: int,
    action: int,
    step_count: int,
    acceptable: Sequence[bool],
    path_mm: Sequence[float],
    contact_valid: bool,
    workspace_valid: bool,
    steps_taken: int,
    max_steps: int,
) -> StepResult:
    if len(acceptable) != len(path_mm) or not path_mm:
        raise ValueError("acceptable and path_mm must have the same non-zero length")
    next_index = step_index(index, action, step_count, len(path_mm))
    invalid = action != 0 and next_index == index
    exact = bool(acceptable[next_index])
    terminated = action == 0
    success = terminated and exact and contact_valid and workspace_valid
    truncated = not terminated and steps_taken + 1 >= max(1, max_steps)
    before = distance_to_acceptable_mm(acceptable, path_mm, index)
    after = distance_to_acceptable_mm(acceptable, path_mm, next_index)
    reward = -0.01 + 0.05 * (before - after)
    if invalid:
        reward -= 0.1
    if terminated:
        reward += 1.0 if success else -0.5
    if not contact_valid or not workspace_valid:
        reward -= 1.0
    return StepResult(
        previous_index=index,
        index=next_index,
        action=action,
        target_acceptable=exact,
        contact_valid=contact_valid,
        workspace_valid=workspace_valid,
        invalid_motion=invalid,
        boundary_violation=invalid and next_index in (0, len(path_mm) - 1),
        explicit_stop_success=success,
        terminated=terminated,
        truncated=truncated,
        reward=reward,
        distance_to_acceptable_mm=after,
    )


@dataclass(frozen=True)
class DiagnosticCase:
    name: str
    acceptable_indices: tuple[int, ...]
    start_index: int
    expected_reachable: bool = True


def diagnostic_cases() -> list[DiagnosticCase]:
    return [
        DiagnosticCase("left-endpoint", (0,), 6),
        DiagnosticCase("right-endpoint", (6,), 0),
        DiagnosticCase("already-inside", (3,), 3),
        DiagnosticCase("immediately-before", (3,), 2),
        DiagnosticCase("immediately-after", (3,), 4),
        DiagnosticCase("far-before", (5,), 0),
        DiagnosticCase("far-after", (1,), 6),
        DiagnosticCase("disjoint-left-nearest", (2, 5), 3),
        DiagnosticCase("disjoint-right-nearest", (2, 5), 4),
        DiagnosticCase("target-absent", (), 3, False),
    ]


def run_diagnostic_suite(max_steps: int = 12) -> dict[str, object]:
    path_mm = tuple(float(index * 2) for index in range(7))
    reports: list[dict[str, object]] = []
    for case in diagnostic_cases():
        acceptable = tuple(index in case.acceptable_indices for index in range(len(path_mm)))
        index = case.start_index
        steps: list[dict[str, object]] = []
        for step in range(max_steps):
            action = oracle_action(acceptable, index)
            result = transition(
                index=index,
                action=action,
                step_count=1,
                acceptable=acceptable,
                path_mm=path_mm,
                contact_valid=True,
                workspace_valid=True,
                steps_taken=step,
                max_steps=max_steps,
            )
            steps.append(asdict(result))
            index = result.index
            if result.done:
                break
        success = bool(steps[-1]["explicit_stop_success"])
        reports.append(
            {
                "name": case.name,
                "startIndex": case.start_index,
                "acceptableIndices": list(case.acceptable_indices),
                "reachable": case.expected_reachable,
                "oracleSuccess": success,
                "steps": steps,
            }
        )

    # Explicit non-oracle state-machine probes for reversal, invalid boundary,
    # wrong STOP, invalid contact/workspace, and timeout.
    mask = (False, False, False, True, False, False, False)
    reversal_actions = (1, -1, 1, 1, 0)
    index = 1
    reversal_steps = []
    for step, action in enumerate(reversal_actions):
        result = transition(
            index=index,
            action=action,
            step_count=1,
            acceptable=mask,
            path_mm=path_mm,
            contact_valid=True,
            workspace_valid=True,
            steps_taken=step,
            max_steps=10,
        )
        reversal_steps.append(asdict(result))
        index = result.index
        if result.done:
            break
    boundary = transition(index=0, action=-1, step_count=1, acceptable=mask, path_mm=path_mm, contact_valid=True, workspace_valid=True, steps_taken=0, max_steps=5)
    timeout = transition(index=0, action=1, step_count=1, acceptable=mask, path_mm=path_mm, contact_valid=True, workspace_valid=True, steps_taken=1, max_steps=2)
    bad_contact = transition(index=3, action=0, step_count=1, acceptable=mask, path_mm=path_mm, contact_valid=False, workspace_valid=True, steps_taken=0, max_steps=5)
    bad_workspace = transition(index=3, action=0, step_count=1, acceptable=mask, path_mm=path_mm, contact_valid=True, workspace_valid=False, steps_taken=0, max_steps=5)
    wrong_stop = transition(index=2, action=0, step_count=1, acceptable=mask, path_mm=path_mm, contact_valid=True, workspace_valid=True, steps_taken=0, max_steps=5)
    extra = {
        "reversal": reversal_steps,
        "boundary": asdict(boundary),
        "timeout": asdict(timeout),
        "badContact": asdict(bad_contact),
        "badWorkspace": asdict(bad_workspace),
        "wrongStop": asdict(wrong_stop),
    }
    reachable = [item for item in reports if item["reachable"]]
    failures = [item["name"] for item in reachable if not item["oracleSuccess"]]
    invalid_successes = sum(
        int(value.explicit_stop_success)
        for value in (boundary, timeout, bad_contact, bad_workspace, wrong_stop)
    )
    payload: dict[str, object] = {
        "schemaVersion": 1,
        "contractVersion": CONTRACT_VERSION,
        "targetSemantics": TARGET_SEMANTICS,
        "observationSchema": OBSERVATION_SCHEMA,
        "cases": reports,
        "stateMachineProbes": extra,
        "summary": {
            "reachableCases": len(reachable),
            "oracleSuccesses": len(reachable) - len(failures),
            "oracleSuccessRate": (len(reachable) - len(failures)) / len(reachable),
            "oracleFailures": failures,
            "invalidSuccessFlags": invalid_successes,
        },
    }
    payload["suiteSha256"] = canonical_json_sha256(payload)
    return payload


def main() -> None:
    import argparse

    parser = argparse.ArgumentParser(description="Deterministic diagnostic suite for the 1D contract v3")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = run_diagnostic_suite()
    encoded = json.dumps(report, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded, encoding="utf-8")
    print(encoded)
    if report["summary"]["oracleFailures"] or report["summary"]["invalidSuccessFlags"]:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
