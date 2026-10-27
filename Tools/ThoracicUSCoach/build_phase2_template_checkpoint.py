from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

import numpy as np

from active2d_template_stop import SCHEMA, normalized_template_features, template_predictions


REFERENCE_CASES = ("r4p1_random_01", "r4p1_random_05", "r4p1_random_06")
NEGATIVE_REFERENCE_INDICES = tuple(range(0, 81, 10))
IMAGE_SIZE = 32


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source_root = args.source_root.resolve()
    samples: list[dict[str, Any]] = []

    for case in REFERENCE_CASES:
        directory = source_root / f"Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_raw_20261025/randomized_start/{case}"
        stream = load(directory / "stream_manifest.json")
        audit = load(directory / "privileged_annotations.json")
        frames = {int(item["index"]): item for item in stream["frames"]}
        for truth in audit["frames"]:
            index = int(truth["index"])
            image = source_root / frames[index]["image"]
            samples.append({
                "case": case,
                "index": index,
                "positive": bool(truth["finalTargetAcceptable"]),
                "image": image,
                "feature": normalized_template_features(image, IMAGE_SIZE),
                "sha256": sha256(image),
            })

    positive_references = [item for item in samples if item["positive"]]
    negative_references = [
        item for item in samples
        if not item["positive"] and (
            item["index"] in NEGATIVE_REFERENCE_INDICES
            or (item["case"] == "r4p1_random_06" and item["index"] == 5)
        )
    ]
    positive_matrix = np.stack([item["feature"] for item in positive_references])
    negative_matrix = np.stack([item["feature"] for item in negative_references])

    positive_calibration = []
    for item in positive_references:
        candidates = [other["feature"] for other in positive_references if other is not item]
        pos_similarity = float(np.max(np.stack(candidates) @ item["feature"]))
        neg_similarity = float(np.max(negative_matrix @ item["feature"]))
        positive_calibration.append((pos_similarity, pos_similarity - neg_similarity))

    negative_reference_ids = {(item["case"], item["index"]) for item in negative_references}
    negative_calibration = []
    for item in samples:
        if item["positive"] or (item["case"], item["index"]) in negative_reference_ids:
            continue
        pos_similarity = float(np.max(positive_matrix @ item["feature"]))
        neg_similarity = float(np.max(negative_matrix @ item["feature"]))
        negative_calibration.append((pos_similarity, pos_similarity - neg_similarity))

    minimum_positive_similarity = min(value[0] for value in positive_calibration)
    maximum_negative_similarity = max(value[0] for value in negative_calibration)
    minimum_similarity = (minimum_positive_similarity + maximum_negative_similarity) * 0.5
    maximum_negative_margin = max(value[1] for value in negative_calibration)
    minimum_positive_margin = min(value[1] for value in positive_calibration)
    separable = maximum_negative_similarity < minimum_positive_similarity
    checkpoint = {
        "schemaVersion": 1,
        "schema": SCHEMA,
        "id": "phase2_fixed_anatomy_template_demo_v1",
        "warning": "FIXED_ANATOMY DEVELOPMENT ENGINEERING DEMO ONLY. NON_DEPLOYABLE.",
        "policyInputs": ["cleanUltrasoundImage"],
        "forbiddenInputs": ["pose", "targetCoordinates", "anatomyLabels", "evaluatorScore", "targetDirection"],
        "deployment": {"status": "DIAGNOSTIC_ONLY", "scientificValidation": False},
        "architecture": {
            "type": "normalized-grayscale-template-nn-v1",
            "imageSize": IMAGE_SIZE,
            "positiveTemplates": [item["feature"].tolist() for item in positive_references],
            "negativeTemplates": [item["feature"].tolist() for item in negative_references],
        },
        "operatingRule": {
            "minimumPositiveSimilarity": minimum_similarity,
            "temperature": max((minimum_positive_similarity - maximum_negative_similarity) / 8.0, 1e-5),
            "calibrationRule": "midpoint between maximum calibration-negative and minimum leave-one-out positive similarity",
        },
        "stopRule": {"threshold": 0.5, "requiredConfirmations": 1},
        "lineage": {
            "role": "consumed development reference only",
            "cases": list(REFERENCE_CASES),
            "positiveReferenceCount": len(positive_references),
            "negativeReferenceCount": len(negative_references),
            "freshSmokeImagesUsedForCalibration": False,
            "positiveReferences": [
                {"case": item["case"], "index": item["index"], "imageSha256": item["sha256"]}
                for item in positive_references
            ],
            "negativeReferences": [
                {"case": item["case"], "index": item["index"], "imageSha256": item["sha256"]}
                for item in negative_references
            ],
        },
        "calibration": {
            "separable": separable,
            "positiveCount": len(positive_calibration),
            "negativeCount": len(negative_calibration),
            "minimumPositiveSimilarity": minimum_positive_similarity,
            "maximumNegativeSimilarity": maximum_negative_similarity,
            "minimumPositiveMargin": minimum_positive_margin,
            "maximumNegativeMargin": maximum_negative_margin,
            "observedFalsePositive": sum(value[0] >= minimum_similarity for value in negative_calibration),
            "observedTruePositive": sum(value[0] >= minimum_similarity for value in positive_calibration),
        },
    }
    if not separable:
        raise SystemExit(json.dumps(checkpoint["calibration"], indent=2))
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(checkpoint, indent=2), encoding="utf-8")
    print(json.dumps(checkpoint["calibration"], indent=2))


if __name__ == "__main__":
    main()
