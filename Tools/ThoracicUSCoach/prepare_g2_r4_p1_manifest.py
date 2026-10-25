from __future__ import annotations

import argparse
import copy
import hashlib
import json
import random
from pathlib import Path
from typing import Any


FLOOR = 0.5912307500839233
LOCKED = [
    "test_family_0044", "test_family_0054", "test_family_0059",
    "test_family_0069", "test_family_0089", "test_family_0099",
]
RANDOM_BASES = ["0086", "0041", "0062", "0065", "0017", "0045", "0055", "0093"]
ENRICHED_BASES = ["0066", "0080", "0052", "0058"]
HARD_CASES = [
    "case_00_train_family_0090_f0100",
    "case_12_train_family_0066_f0100",
    "case_13_train_family_0077_f0023",
    "case_07_dev_family_0058_f0105",
]


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def random_walk(seed: int, steps: int = 80) -> list[str]:
    rng = random.Random(seed)
    longitudinal = lateral = yaw = 0
    actions: list[str] = []
    for index in range(steps):
        candidates: list[tuple[str, tuple[int, int, int]]] = []
        for name, delta in (
            ("Cranial", (-1, 0, 0)), ("Caudal", (1, 0, 0)),
            ("Medial", (0, -1, 0)), ("Lateral", (0, 1, 0)),
            ("RotateCounterClockwise", (0, 0, -1)),
            ("RotateClockwise", (0, 0, 1)),
        ):
            next_state = (longitudinal + delta[0], lateral + delta[1], yaw + delta[2])
            if abs(next_state[0]) <= 5 and abs(next_state[1]) <= 5 and abs(next_state[2]) <= 2:
                candidates.append((name, next_state))
        # Deterministic observations expose renderer variability without moving.
        if index in {12, 39, 66}:
            actions.append("Observe")
            continue
        name, state = rng.choice(candidates)
        actions.append(name)
        longitudinal, lateral, yaw = state
    return actions


def enriched_path(source_yaw: float, variant: int) -> list[str]:
    yaw = "RotateCounterClockwise" if source_yaw < 0 else "RotateClockwise"
    approach = ["Caudal"] * (2 + variant) + ["Lateral"] * (3 + variant) + [yaw]
    actions = approach + ["Observe", "Observe"]
    actions += ["RotateCounterClockwise", "RotateClockwise", "RotateClockwise", "RotateCounterClockwise"]
    actions += ["Cranial"] * (2 + variant) + ["Medial"] * (2 + variant)
    for row in range(5):
        direction = "Lateral" if row % 2 == 0 else "Medial"
        actions.append("Observe")
        actions.extend([direction] * 4)
        if row < 4:
            actions.append("Caudal")
    actions += ["Observe", "RotateClockwise", "RotateCounterClockwise", "Observe"]
    return actions


def exact_case_to_start(case: dict[str, Any], order: int) -> dict[str, Any]:
    return {
        "startId": f"r4p1_hard_base_{order:02d}",
        "order": order,
        "sourceEpisode": "consumed-exact-pose-diagnostic",
        "sourceFamily": case["family"],
        "seed": 31000 + order,
        "configSha256": case["sourceImageSha256"],
        "streamManifestSha256": "consumed-source-recorded-in-r3-a2",
        "privilegedAnnotationsSha256": "consumed-source-recorded-in-r3-a2",
        "startAuditSha256": "consumed-source-recorded-in-r3-a2",
        "probePosition": case["probePosition"],
        "probeRotation": case["probeRotation"],
        "workspaceOrigin": case["workspaceOrigin"],
        "actuatorCranialAxis": case["actuatorCranialAxis"],
        "actuatorLateralAxis": case["actuatorLateralAxis"],
        "longitudinalHalfExtentMm": case["longitudinalHalfExtentMm"],
        "lateralHalfExtentMm": case["lateralHalfExtentMm"],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Predeclare G2-R4 revised P1 Unity capture")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    output = args.output or repo / "Tools/ThoracicUSCoach/experiments/g2_r4_p1_capture_v1.json"
    base_path = repo / "Tools/ThoracicUSCoach/experiments/g2_temporal_capture_v1.json"
    exact_path = repo / "Tools/ThoracicUSCoach/experiments/g2_r3_a2_exact_pose_v1.json"
    base_manifest = load(base_path)
    exact_manifest = load(exact_path)
    bases = {item["start"]["sourceFamily"].split("_")[-1]: item for item in base_manifest["cases"]}
    exact = {item["caseId"]: item for item in exact_manifest["cases"]}
    cases: list[dict[str, Any]] = []

    prepositions = [
        ["Cranial", "Medial"], ["Caudal", "Lateral"],
        ["Cranial", "Lateral", "RotateClockwise"],
        ["Caudal", "Medial", "RotateCounterClockwise"],
        ["Medial", "Medial"], ["Lateral", "Lateral"],
        ["Cranial", "Cranial"], ["Caudal", "Caudal"],
    ]
    for index, ordinal in enumerate(RANDOM_BASES):
        source = bases[ordinal]
        seed = 42001 + index * 137
        cases.append({
            "caseId": f"r4p1_random_{index:02d}",
            "arm": "randomized_start",
            "familyId": f"r4p1_random_family_{index:02d}_s{seed}",
            "captureSeed": seed,
            "countsAsNewIndependentFamily": True,
            "planningReference": f"consumed safe handoff geometry {source['start']['sourceFamily']}; no source images reused",
            "baseStart": copy.deepcopy(source["start"]),
            "prepositionActions": prepositions[index],
            "trajectoryActions": random_walk(seed),
        })

    for base_index, ordinal in enumerate(ENRICHED_BASES):
        source = bases[ordinal]
        for variant in range(2):
            index = base_index * 2 + variant
            seed = 53003 + index * 149
            cases.append({
                "caseId": f"r4p1_enriched_{index:02d}",
                "arm": "target_enriched",
                "familyId": f"r4p1_enriched_family_{index:02d}_s{seed}",
                "captureSeed": seed,
                "countsAsNewIndependentFamily": True,
                "planningReference": f"privileged capture planning from consumed {source['start']['sourceFamily']}; not representative random success",
                "baseStart": copy.deepcopy(source["start"]),
                "prepositionActions": [] if variant == 0 else ["Medial", "Cranial"],
                "trajectoryActions": enriched_path(float(source["sourceYawDeg"]), variant),
            })

    hard_actions = [
        "Observe", "RotateClockwise", "Observe", "RotateCounterClockwise",
        "RotateCounterClockwise", "Observe", "RotateClockwise", "Lateral",
        "Observe", "Medial", "Medial", "Observe", "Lateral",
    ]
    for index, case_id in enumerate(HARD_CASES):
        source = exact[case_id]
        cases.append({
            "caseId": f"r4p1_hard_{index:02d}",
            "arm": "hard_causal",
            "familyId": f"r4p1_consumed_hard_{source['family']}_{index:02d}",
            "captureSeed": 64007 + index * 151,
            "countsAsNewIndependentFamily": False,
            "planningReference": f"consumed {case_id}; excluded from new-family support",
            "baseStart": exact_case_to_start(source, index),
            "prepositionActions": [],
            "trajectoryActions": hard_actions,
        })

    payload = {
        "schemaVersion": 1,
        "protocolId": "G2_R4_REVISED_P1_CAPTURE_V1",
        "scene": "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity",
        "unityVersion": "6000.3.9f1",
        "datasetRunId": "g2_r4_revised_p1_20261025",
        "outputDirectory": "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_raw_20261025",
        "acceptableScoreFloor": FLOOR,
        "maxIndependentFamilies": 16,
        "maxObservationsPerIndependentFamily": 121,
        "minHardCausalObservations": 48,
        "maxHardCausalObservations": 72,
        "historicallyLockedFamilies": LOCKED,
        "predeclaration": {
            "createdBeforeFreshCapture": True,
            "sourceBaseManifest": str(base_path.relative_to(repo)).replace("\\", "/"),
            "sourceBaseManifestSha256": sha256(base_path),
            "sourceExactPoseManifest": str(exact_path.relative_to(repo)).replace("\\", "/"),
            "sourceExactPoseManifestSha256": sha256(exact_path),
            "selectionPolicy": "All 16 new families are exploratory capture-development evidence. Arm A uses deterministic seeded target-independent walks. Arm B uses privileged target-enriched planning and is never interpreted as randomized-start success. Four consumed hard cases are excluded from independent support.",
            "rolePolicy": "No pilot family is assigned to future train/calibration/confirmation/locked roles in P1.",
        },
        "cases": cases,
    }
    if len(cases) != 20 or sum(item["countsAsNewIndependentFamily"] for item in cases) != 16:
        raise RuntimeError("P1 family budget construction failed")
    hard_observations = sum(len(item["trajectoryActions"]) + 1 for item in cases if item["arm"] == "hard_causal")
    if hard_observations != 56:
        raise RuntimeError(f"Expected 56 hard observations, got {hard_observations}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output), "sha256": sha256(output),
        "newFamilies": 16, "hardCausalObservations": hard_observations,
        "maximumNewFamilyObservations": max(len(item["trajectoryActions"]) + 1 for item in cases[:16]),
    }, indent=2))


if __name__ == "__main__":
    main()
