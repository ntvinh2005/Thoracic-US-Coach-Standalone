from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


DATASET_RUN_ID = "active2d_phase2_raster_v2_20261023"
TRAIN_POSITIVE = [
    "train_family_0016", "train_family_0017", "train_family_0037", "train_family_0041",
    "train_family_0052", "train_family_0062", "train_family_0065", "train_family_0080",
    "train_family_0086", "train_family_0097", "dev_family_0038", "dev_family_0058",
    "dev_family_0083",
]
TRAIN_NEGATIVE = [
    "train_family_0045", "train_family_0066", "train_family_0087", "train_family_0072",
    "train_family_0075", "train_family_0051", "dev_family_0068", "dev_family_0048",
    "dev_family_0093", "train_family_0046", "train_family_0056", "train_family_0077",
    "train_family_0092",
]
CALIBRATION_POSITIVE = [
    "train_family_0055", "train_family_0076", "train_family_0090",
    "dev_family_0073", "test_family_0079", "test_family_0094",
]
CALIBRATION_NEGATIVE = [
    "dev_family_0063", "train_family_0057", "train_family_0010",
    "test_family_0034", "train_family_0085", "test_family_0039",
]
LOCKED_TEST = [
    "test_family_0044", "test_family_0059", "test_family_0069",
    "test_family_0099", "test_family_0089", "test_family_0054",
]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical_sha256(value: Any) -> str:
    return hashlib.sha256(
        json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()


def load_index(source_root: Path) -> dict[str, tuple[Path, dict[str, Any], dict[str, Any]]]:
    result: dict[str, tuple[Path, dict[str, Any], dict[str, Any]]] = {}
    dataset = source_root / "Artifacts/ThoracicUSCoach/active2d_dataset"
    for stream_path in sorted(dataset.glob("*/*/stream_manifest.json")):
        stream = json.loads(stream_path.read_text(encoding="utf-8"))
        if stream.get("datasetRunId") != DATASET_RUN_ID or stream.get("captureStatus") != "complete":
            continue
        annotation_path = stream_path.with_name("privileged_annotations.json")
        annotations = json.loads(annotation_path.read_text(encoding="utf-8"))
        family = str(stream["family"])
        if family in result:
            raise RuntimeError(f"Duplicate family: {family}")
        result[family] = (stream_path.parent, stream, annotations)
    if len(result) != 100:
        raise RuntimeError(f"Expected 100 complete source families, found {len(result)}")
    return result


def case_record(
    source_root: Path,
    index: dict[str, tuple[Path, dict[str, Any], dict[str, Any]]],
    family: str,
    role: str,
    stratum: str,
) -> dict[str, Any]:
    directory, stream, annotations = index[family]
    annotation_path = directory / "privileged_annotations.json"
    stream_path = directory / "stream_manifest.json"
    positive = sum(bool(item.get("finalTargetAcceptable")) for item in annotations["frames"])
    maximum = max(float(item["finalTargetScore"]) for item in annotations["frames"])
    expected_positive = stratum == "positive"
    if (positive > 0) != expected_positive:
        raise RuntimeError(f"Stratum mismatch for {family}: positiveFrames={positive}")
    audit = directory / "start_audit.txt"
    return {
        "role": role,
        "coverageStratum": stratum,
        "sourceSplit": stream["split"],
        "family": family,
        "episodeId": stream["episodeId"],
        "episodeDirectory": directory.relative_to(source_root).as_posix(),
        "frames": len(stream["frames"]),
        "positiveFrames": positive,
        "maximumTargetScore": maximum,
        "streamManifestSha256": sha256(stream_path),
        "privilegedAnnotationsSha256": sha256(annotation_path),
        "startAuditSha256": sha256(audit),
    }


def prior_usage(project_root: Path) -> dict[str, list[dict[str, str]]]:
    usages: dict[str, list[dict[str, str]]] = {}
    sources = [
        ("experiments/g2_r1_existing_family_v1.json", "G2-R1 controlled comparison"),
        ("experiments/g2_r1_hybrid_confirmation_v1.json", "G2-R1 hybrid confirmation"),
        ("experiments/g2_temporal_capture_v1.json", "G2 temporal capture"),
        ("experiments/g1_oracle_feasibility_v1.json", "G1 oracle diagnostic"),
    ]
    tool_root = project_root / "Tools/ThoracicUSCoach"
    for relative, study in sources:
        path = tool_root / relative
        if not path.is_file():
            continue
        payload = json.loads(path.read_text(encoding="utf-8"))
        for item in [*(payload.get("cases") or []), *(payload.get("starts") or [])]:
            family = item.get("family") or (item.get("start") or {}).get("sourceFamily") or item.get("sourceFamily")
            if not family:
                continue
            role = str(item.get("role") or "diagnostic")
            status = "reserved-sealed-not-opened" if role == "locked_test" else "consumed"
            usages.setdefault(str(family), []).append({"study": study, "role": role, "status": status})
    return usages


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    source_root = args.source_root.resolve()
    index = load_index(source_root)
    role_lists = {
        "train": [(family, "positive") for family in TRAIN_POSITIVE]
        + [(family, "hard_negative") for family in TRAIN_NEGATIVE],
        "calibration": [(family, "positive") for family in CALIBRATION_POSITIVE]
        + [(family, "hard_negative") for family in CALIBRATION_NEGATIVE],
        "locked_test": [
            (family, "positive" if family in LOCKED_TEST[:3] else "hard_negative")
            for family in LOCKED_TEST
        ],
    }
    all_selected = [family for values in role_lists.values() for family, _ in values]
    if len(all_selected) != len(set(all_selected)):
        raise RuntimeError("G2-R2 family roles overlap")
    cases = [
        case_record(source_root, index, family, role, stratum)
        for role, values in role_lists.items()
        for family, stratum in values
    ]
    identity = [
        {key: case[key] for key in ("role", "family", "episodeId", "streamManifestSha256", "privilegedAnnotationsSha256")}
        for case in cases
    ]
    manifest = {
        "schemaVersion": 1,
        "protocolId": "G2_R2_STRUCTURAL_TEMPORAL_V1",
        "baseCommit": "c11c9aac2761b7b62f1dfc08a3daff6b64cfa647",
        "datasetRunId": DATASET_RUN_ID,
        "sourceRepositoryHint": "local Thoracic-US-Coach-Standalone evidence store; image bytes are not copied",
        "datasetFingerprint": canonical_sha256(identity),
        "selectionPolicy": {
            "predeclaredBeforeFeatureFitting": True,
            "familyDisjoint": True,
            "calibrationPositiveFamilies": len(CALIBRATION_POSITIVE),
            "calibrationFamiliesPreviouslyConsumedByG2R1": 0,
            "lockedTestPolicy": "retain the six sealed G2-R1 families; load once only after every calibration gate passes",
        },
        "candidateBudget": {
            "screen": ["structural-v4", "temporal-structural-v5-h1", "temporal-structural-v5-h4"],
            "screenSeed": 13,
            "confirmationSeeds": [29, 47],
            "confirmationCandidates": 2,
            "classifier": "class-balanced mini-batch L2 logistic; alpha=1e-4; epochs=80; batch=256",
            "qualityHead": "bounded mini-batch L2 linear regression; alpha=5e-4; epochs=100",
            "thresholdSelection": "highest threshold among zero-FP calibration points with maximal recall; recall must be >=0.80",
        },
        "gate": {
            "targetRecallMin": 0.80,
            "falsePositiveMax": 0,
            "wrongStopEpisodesMax": 0,
            "qualitySpearmanMin": 0.60,
            "medianBestViewRegretMax": 0.015,
            "calibrationPositiveFamiliesMin": 5,
        },
        "cases": cases,
    }
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    usage = prior_usage(project_root)
    assigned = {case["family"]: case["role"] for case in cases}
    ledger_rows = []
    for family, (directory, stream, annotations) in sorted(index.items()):
        ledger_rows.append({
            "family": family,
            "sourceSplit": stream["split"],
            "positiveFrames": sum(bool(item.get("finalTargetAcceptable")) for item in annotations["frames"]),
            "maximumTargetScore": max(float(item["finalTargetScore"]) for item in annotations["frames"]),
            "priorUsage": usage.get(family, []),
            "g2R2Role": assigned.get(family, "unused"),
            "episodeDirectory": directory.relative_to(source_root).as_posix(),
        })
    ledger = {
        "schemaVersion": 1,
        "datasetRunId": DATASET_RUN_ID,
        "sourceFamilies": len(ledger_rows),
        "manifestSha256": sha256(args.manifest),
        "rows": ledger_rows,
    }
    args.ledger.parent.mkdir(parents=True, exist_ok=True)
    args.ledger.write_text(json.dumps(ledger, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "manifest": str(args.manifest),
        "manifestSha256": sha256(args.manifest),
        "datasetFingerprint": manifest["datasetFingerprint"],
        "roles": {role: len(values) for role, values in role_lists.items()},
    }, indent=2))


if __name__ == "__main__":
    main()
