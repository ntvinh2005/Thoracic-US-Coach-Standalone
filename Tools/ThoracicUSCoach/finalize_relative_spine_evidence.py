"""Create deterministic source/configuration hashes for the relative-spine handoff."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


SOURCE_PATHS = (
    "LocalPackages/com.cssalt.smmarts-sdk-package/Runtime/Ultrasound/UltrasoundManager.cs",
    "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.RelativeSpineAudit.cs",
    "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.RelativeSpineGeometryScreen.cs",
    "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.RelativeSpineSeparability.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicRelativeSpineAuditBatch.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicRelativeSpineGeometryBatch.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicRelativeSpineSeparabilityBatch.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicRelativeSpineSeparabilityP6Batch.cs",
    "Assets/ThoracicUSCoach/Editor/ThoracicRelativeSpineObliqueBatch.cs",
    "Assets/ThoracicUSCoach/Tests/Editor/RelativeSpineAuditTests.cs",
    "Tools/ThoracicUSCoach/relative_spine_e0_e1_analysis.py",
    "Tools/ThoracicUSCoach/test_relative_spine_e0_e1_analysis.py",
    "Tools/ThoracicUSCoach/relative_spine_e2_analysis.py",
    "Tools/ThoracicUSCoach/test_relative_spine_e2_analysis.py",
    "Tools/ThoracicUSCoach/relative_spine_e4_analysis.py",
    "Tools/ThoracicUSCoach/test_relative_spine_e4_analysis.py",
    "Tools/ThoracicUSCoach/finalize_relative_spine_evidence.py",
)

EVIDENCE_PATHS = (
    "experiment_registry.json",
    "REPORT.md",
    "final_gates.json",
    "relative_spine_map.json",
    "evaluator_only/e1_capture_manifest.json",
    "evaluator_only/e2_geometry/capture_manifest.json",
    "evaluator_only/e2_geometry/geometry_analysis.json",
    "evaluator_only/e4_separability/capture_manifest.json",
    "evaluator_only/e4_separability/image_separability_report.json",
    "evaluator_only/e4_separability_p6/capture_manifest.json",
    "evaluator_only/e4_separability_p6/image_separability_report.json",
    "evaluator_only/e4_separability_oblique_m6_yaw60/capture_manifest.json",
    "evaluator_only/e4_separability_oblique_m6_yaw60/image_separability_report.json",
    "test_results/python_focused_tests.json",
    "test_results/unity_license_resolution.json",
)


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", type=Path, required=True)
    parser.add_argument("--experiment-root", type=Path, required=True)
    args = parser.parse_args()
    repository = args.repository_root.resolve()
    experiment = args.experiment_root.resolve()
    rows = []
    for relative in SOURCE_PATHS:
        path = repository / relative
        rows.append({"kind": "source", "path": relative, "sha256": digest(path)})
    for relative in EVIDENCE_PATHS:
        path = experiment / relative
        rows.append({"kind": "configuration_or_evidence", "path": relative, "sha256": digest(path)})
    manifest = {
        "contract": "thoracic_relative_spine_source_configuration_hashes_v1",
        "status": "FROZEN_AFTER_E4_STOPPING_DECISION",
        "files": rows,
    }
    output = experiment / "source_configuration_hash_manifest.json"
    output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    checksum = digest(output)
    (experiment / "final_evidence.sha256").write_text(
        f"{checksum}  source_configuration_hash_manifest.json\n", encoding="utf-8")
    print(json.dumps({"files": len(rows), "manifestSha256": checksum}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
