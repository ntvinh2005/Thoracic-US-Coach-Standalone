from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description="Predeclare one bounded P1 hard-causal replacement")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    repo = args.repo.resolve()
    source_path = repo / "Tools/ThoracicUSCoach/experiments/g2_r3_a2_exact_pose_v1.json"
    source = load(source_path)
    case = next(item for item in source["cases"] if item["caseId"] == "case_09_train_family_0080_f0052")
    actions = [
        "Observe", "RotateClockwise", "Observe", "RotateCounterClockwise",
        "RotateCounterClockwise", "Observe", "RotateClockwise", "Lateral",
        "Observe", "Medial", "Medial", "Observe", "Lateral",
    ]
    start = {
        "startId": "r4p1_hard_supplement_base_0080",
        "order": 0,
        "sourceEpisode": "consumed-exact-pose-diagnostic",
        "sourceFamily": case["family"],
        "seed": 65111,
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
    payload = {
        "schemaVersion": 1,
        "protocolId": "G2_R4_REVISED_P1_HARD_SUPPLEMENT_V1",
        "scene": "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity",
        "unityVersion": "6000.3.9f1",
        "datasetRunId": "g2_r4_revised_p1_hard_supplement_20261025",
        "outputDirectory": "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_20261025",
        "acceptableScoreFloor": 0.5912307500839233,
        "maxIndependentFamilies": 0,
        "maxObservationsPerIndependentFamily": 121,
        "minHardCausalObservations": 14,
        "maxHardCausalObservations": 14,
        "historicallyLockedFamilies": [
            "test_family_0044", "test_family_0054", "test_family_0059",
            "test_family_0069", "test_family_0089", "test_family_0099",
        ],
        "predeclaration": {
            "createdBeforeSupplementCapture": True,
            "reason": "The main predeclared dev_family_0058 hard start failed T7 validation before capture; add one distinct consumed diagnostic family without inspecting new target labels.",
            "sourceExactPoseManifestSha256": sha256(source_path),
            "countsTowardIndependentSupport": False,
        },
        "cases": [{
            "caseId": "r4p1_hard_supplement_0080",
            "arm": "hard_causal",
            "familyId": "r4p1_consumed_hard_train_family_0080_supplement",
            "captureSeed": 65111,
            "countsAsNewIndependentFamily": False,
            "planningReference": "consumed case_09_train_family_0080_f0052; replacement for rejected 0058 start; excluded from new-family support",
            "baseStart": start,
            "prepositionActions": [],
            "trajectoryActions": actions,
        }],
    }
    output = repo / "Tools/ThoracicUSCoach/experiments/g2_r4_p1_hard_supplement_v1.json"
    output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "sha256": sha256(output), "observations": 14}, indent=2))


if __name__ == "__main__":
    main()
