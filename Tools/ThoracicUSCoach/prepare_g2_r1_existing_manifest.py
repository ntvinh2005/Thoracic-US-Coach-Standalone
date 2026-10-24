from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


PLAN = {
    "train": {
        "sourceSplit": "train",
        "positive": [80, 86, 41, 62, 65, 17],
        "hard_negative": [45, 66, 87, 72, 75, 51],
    },
    "calibration": {
        "sourceSplit": "dev",
        "positive": [83, 38, 58],
        "hard_negative": [68, 48, 93],
    },
    "locked_test": {
        "sourceSplit": "test",
        "positive": [44, 59, 69],
        "hard_negative": [99, 89, 54],
    },
}

HYBRID_CONFIRMATION_PLAN = {
    "train": PLAN["train"],
    "calibration": {
        "sourceSplit": "train",
        "positive": [16, 37, 52, 97],
        "hard_negative": [92, 77, 56, 46],
    },
    "locked_test": PLAN["locked_test"],
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--plan", choices=("comparison", "hybrid_confirmation"), default="comparison")
    args = parser.parse_args()
    selected_plan = PLAN if args.plan == "comparison" else HYBRID_CONFIRMATION_PLAN
    dataset_root = args.source_root / "Artifacts/ThoracicUSCoach/active2d_dataset"
    cases: list[dict[str, object]] = []
    seen: set[str] = set()
    for role, specification in selected_plan.items():
        source_split = str(specification["sourceSplit"])
        for coverage in ("positive", "hard_negative"):
            for ordinal in specification[coverage]:
                matches = sorted((dataset_root / source_split).glob(
                    f"A2D_active2d_phase2_raster_v2_20261023_{ordinal:04d}_S*"
                ))
                if len(matches) != 1:
                    raise RuntimeError(f"Expected one source episode for {source_split}/{ordinal:04d}")
                episode = matches[0]
                stream_path = episode / "stream_manifest.json"
                labels_path = episode / "privileged_annotations.json"
                audit_path = episode / "start_audit.txt"
                stream = json.loads(stream_path.read_text(encoding="utf-8"))
                labels = json.loads(labels_path.read_text(encoding="utf-8"))
                family = str(stream["family"])
                if family in seen:
                    raise RuntimeError(f"Duplicate family: {family}")
                seen.add(family)
                positives = sum(bool(frame["finalTargetAcceptable"]) for frame in labels["frames"])
                expected_positive = coverage == "positive"
                if bool(positives) != expected_positive:
                    raise RuntimeError(f"Coverage stratum changed for {family}: positives={positives}")
                cases.append({
                    "order": len(cases),
                    "role": role,
                    "coverageStratum": coverage,
                    "sourceSplit": source_split,
                    "family": family,
                    "episodeId": stream["episodeId"],
                    "episodeDirectory": episode.relative_to(args.source_root).as_posix(),
                    "frames": len(stream["frames"]),
                    "positiveFrames": positives,
                    "maximumTargetScore": max(float(frame["finalTargetScore"]) for frame in labels["frames"]),
                    "streamManifestSha256": sha256(stream_path),
                    "privilegedAnnotationsSha256": sha256(labels_path),
                    "startAuditSha256": sha256(audit_path),
                })
    canonical = json.dumps(cases, sort_keys=True, separators=(",", ":")).encode("utf-8")
    role_families = {
        role: sum(len(specification[name]) for name in ("positive", "hard_negative"))
        for role, specification in selected_plan.items()
    }
    positive_families = {
        role: len(specification["positive"])
        for role, specification in selected_plan.items()
    }
    hybrid = args.plan == "hybrid_confirmation"
    payload = {
        "schemaVersion": 1,
        "protocolId": (
            "G2_R1_HYBRID_CONFIRMATION_V1" if hybrid
            else "G2_R1_EXISTING_FAMILY_COMPARISON_V1"
        ),
        "datasetRunId": "active2d_phase2_raster_v2_20261023",
        "sourceRepositoryHint": "sibling Thoracic-US-Coach-Standalone local evidence store",
        "selectionPolicy": {
            "predeclaredBeforeTraining": True,
            "familyDisjoint": True,
            "trainFamilies": role_families["train"],
            "calibrationFamilies": role_families["calibration"],
            "lockedTestFamilies": role_families["locked_test"],
            "positiveFamiliesPerRole": positive_families,
            "lockedTestUse": "evaluate the selected configuration once; never tune threshold or model on it",
            "previousG2HeldoutUse": "diagnostic only; not used for model selection or locked-test scoring",
        },
        "predeclaredGate": {
            "calibrationRecallMin": 0.80,
            "calibrationFalsePositiveMax": 0,
            "calibrationWrongStopEpisodesMax": 0,
            "qualitySpearmanMin": 0.60,
            "medianEpisodeBestViewRegretMax": 0.015,
            "lockedTestIsSelectionBlind": True,
        },
        "trainingBudget": {
            "models": (["frozen_spatial_v2_stop+temporal_v3_quality"] if hybrid else [
                "frozen_spatial_v2",
                "current_raw16_logistic",
                "temporal_images_no_action",
                "temporal_v3_full",
            ]),
            "candidateSeeds": ([] if hybrid else [13, 29, 47]),
            "classifier": (
                "frozen spatial-v2 logistic; threshold fixed at 0.93" if hybrid
                else "class-balanced L2 logistic regression"
            ),
            "qualityHead": "deterministic L2 ridge regression",
        },
        "cases": cases,
        "manifestDatasetFingerprint": hashlib.sha256(canonical).hexdigest(),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(args.output),
        "cases": len(cases),
        "fingerprint": payload["manifestDatasetFingerprint"],
    }, indent=2))


if __name__ == "__main__":
    main()
