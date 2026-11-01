"""Freeze the reviewed G2-R3 source/configuration before the single confirmation sweep."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


EXPECTED_ASSET_HASHES = {
    "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity":
        "ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131",
    "Assets/Thoracic RA Anatomy/RA Thoracic Anatomy V3.fbx":
        "df34d622b8af3a719cbc0cb42e33e643e4435bd2cbf2d6c4a2e4b49357d19d4e",
    "LocalPackages/com.cssalt.smmarts-sdk-package/Runtime/Ultrasound/UltrasoundManager.cs":
        "2f170ee99402279977f906a8288cf4af6ce66016d5b0bda00b30e1cbdd9220e0",
}

SOURCE_PATHS = (
    "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R2ContinuousSweep.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicG2R2ContinuousBatch.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicG2R3ConfirmationBatch.cs",
    "Assets/ThoracicUSCoach/Tests/Editor/G2R3TopologyConfirmationTests.cs",
    "Tools/ThoracicUSCoach/g2_r3_topology_discriminator.py",
    "Tools/ThoracicUSCoach/g2_r3_offline_audit.py",
    "Tools/ThoracicUSCoach/g2_r3_confirmation.py",
    "Tools/ThoracicUSCoach/g2_r3_evaluator.py",
    "Tools/ThoracicUSCoach/test_g2_r3_topology_discriminator.py",
)


def sha256(path: Path, normalize_text: bool = False) -> str:
    data = path.read_bytes()
    if normalize_text:
        data = data.replace(b"\r\n", b"\n")
    return hashlib.sha256(data).hexdigest()


def test_summary(path: Path) -> dict[str, object]:
    root = ET.parse(path).getroot()
    return {
        "path": str(path),
        "total": int(root.attrib["total"]),
        "passed": int(root.attrib["passed"]),
        "failed": int(root.attrib["failed"]),
        "skipped": int(root.attrib["skipped"]),
        "result": root.attrib["result"],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", type=Path, required=True)
    parser.add_argument("--execution-root", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    args = parser.parse_args()
    repository_root = args.repository_root.resolve()
    execution_root = args.execution_root.resolve()
    evidence_root = args.evidence_root.resolve()
    source_hashes = {
        name: sha256(repository_root / name, normalize_text=name.endswith(".cs"))
        for name in SOURCE_PATHS
    }
    execution_source_parity = {}
    for name in SOURCE_PATHS[:3]:
        execution_source_parity[name] = {
            "reviewedSha256Normalized": source_hashes[name],
            "executionSha256Normalized": sha256(
                execution_root / name, normalize_text=name.endswith(".cs")
            ),
        }
    asset_hashes = {name: sha256(execution_root / name)
                    for name in EXPECTED_ASSET_HASHES}
    assets_unchanged = asset_hashes == EXPECTED_ASSET_HASHES
    tests = [
        test_summary(evidence_root / "test_results" / "unity_editmode.xml"),
        test_summary(evidence_root / "test_results" / "unity_regressions.xml"),
    ]
    tests_pass = all(item["failed"] == 0 and item["result"] == "Passed" for item in tests)
    source_parity = all(
        item["reviewedSha256Normalized"] == item["executionSha256Normalized"]
        for item in execution_source_parity.values()
    )
    freeze = {
        "contract": "thoracic_g2_r3_preconfirmation_freeze_v1",
        "detectorSourceFrozen": True,
        "configurationFrozen": True,
        "acquisition": {
            "lateralOffsetMm": -6.0,
            "surfaceYawDeg": 91.0,
            "stepMm": 1.5,
            "singleVariation": "surface yaw +1 degree from G2-R2 development",
        },
        "detector": {
            "featureFamily": "ACOUSTIC_SHADOW_ONSET_TERMINATION_CONTINUITY",
            "minimumDistalShadowObservationPixels": 45.0,
            "acceptance": "distal field at/above threshold",
            "belowThreshold": "AMBIGUOUS_INSUFFICIENT_DISTAL_FIELD",
        },
        "evaluation": {
            "oneToOneOrderPreserving": True,
            "eventToleranceMm": 5.0,
            "confirmationSweepBudget": 1,
        },
        "sourceHashes": source_hashes,
        "executionSourceParity": execution_source_parity,
        "executionSourceParityPass": source_parity,
        "assetHashes": asset_hashes,
        "expectedAssetHashes": EXPECTED_ASSET_HASHES,
        "assetsUnchangedFromG2R2": assets_unchanged,
        "unityTests": tests,
        "unityTestsPass": tests_pass,
        "actorEvaluatorIsolationRequired": True,
        "actorEvaluatorIsolationTestsPass": tests_pass,
        "readyForSingleConfirmation": bool(
            assets_unchanged and source_parity and tests_pass
        ),
    }
    (evidence_root / "preconfirmation_freeze.json").write_text(
        json.dumps(freeze, indent=2) + "\n", encoding="utf-8"
    )
    readiness_path = evidence_root / "development_readiness_gate.json"
    readiness = json.loads(readiness_path.read_text())
    readiness["actorEvaluatorIsolationTestsRequired"] = True
    readiness["actorEvaluatorIsolationTestsPassed"] = tests_pass
    readiness["configurationFrozen"] = True
    readiness["confirmationConfigurationPredeclared"] = True
    readiness["executionSourceParityPass"] = source_parity
    readiness["assetsUnchangedFromG2R2"] = assets_unchanged
    readiness["readyForSingleConfirmation"] = freeze["readyForSingleConfirmation"]
    readiness_path.write_text(json.dumps(readiness, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "readyForSingleConfirmation": freeze["readyForSingleConfirmation"],
        "executionSourceParityPass": source_parity,
        "assetsUnchangedFromG2R2": assets_unchanged,
        "unityTestsPass": tests_pass,
    }, indent=2))
    return 0 if freeze["readyForSingleConfirmation"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
