from __future__ import annotations

import hashlib
import json
from pathlib import Path


def main() -> None:
    repo = Path(__file__).resolve().parents[2]
    source_path = repo / "Tools/ThoracicUSCoach/experiments/g2_r3_a2_exact_pose_v1.json"
    source = json.loads(source_path.read_text(encoding="utf-8-sig"))
    case = next(item for item in source["cases"] if item["caseId"] == "case_08_train_family_0045_f0100")
    actions = [
        "Observe", "RotateClockwise", "Observe", "RotateCounterClockwise",
        "RotateCounterClockwise", "Observe", "RotateClockwise", "Lateral",
        "Observe", "Medial", "Medial", "Observe", "Lateral",
    ]
    start = {
        "startId": "r4p1_hard_supplement_base_0045",
        "order": 0,
        "sourceEpisode": "consumed-exact-pose-diagnostic",
        "sourceFamily": case["family"],
        "seed": 66221,
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
        "datasetRunId": "g2_r4_revised_p1_hard_supplement_v2_20261025",
        "outputDirectory": "Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_v2_20261025",
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
            "reason": "0058 and 0080 replacement poses failed T7 start validation before image capture. Source annotation audit identified 0045/100 as T7-valid; this is the final bounded replacement attempt.",
            "sourceExactPoseManifestSha256": hashlib.sha256(source_path.read_bytes()).hexdigest(),
            "countsTowardIndependentSupport": False,
            "maximumFurtherAttempts": 0,
        },
        "cases": [{
            "caseId": "r4p1_hard_supplement_0045",
            "arm": "hard_causal",
            "familyId": "r4p1_consumed_hard_train_family_0045_supplement",
            "captureSeed": 66221,
            "countsAsNewIndependentFamily": False,
            "planningReference": "consumed case_08_train_family_0045_f0100; final replacement; excluded from new-family support",
            "baseStart": start,
            "prepositionActions": [],
            "trajectoryActions": actions,
        }],
    }
    output = repo / "Tools/ThoracicUSCoach/experiments/g2_r4_p1_hard_supplement_v2.json"
    output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output),
        "sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
        "observations": 14,
        "remainingAttempts": 0,
    }, indent=2))


if __name__ == "__main__":
    main()
