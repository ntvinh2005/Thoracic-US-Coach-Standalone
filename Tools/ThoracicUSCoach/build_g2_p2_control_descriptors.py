from __future__ import annotations

import argparse
import json
from itertools import combinations
from pathlib import Path
from typing import Any

from g2_r6_audit import image_features, sha256, visual_distances


EXPECTED_SEAL = "b405aae3e74a5dea1a4a38a1dff92a27d090b80b807b86b0a5815bb234ea7bcb"
EXPECTED_REPORT = "7744637a5db612021e2ff7f8fd6e855547d4a500635b2d5be2059ef2ba63c4ae"
CONTROL_FAMILIES = {
    "r6_control_hard_00_f0008": "train_family_0090",
    "r6_control_random_01_f0010": "train_family_0041",
    "r6_control_random_05_f0016": "train_family_0045",
    "r6_control_hard_01_f0009": "train_family_0066",
}
SEQUENCE = [
    "r6_control_hard_00_f0008",
    "r6_control_random_01_f0010",
    "r6_control_hard_00_f0008",
    "r6_control_random_05_f0016",
    "r6_control_hard_01_f0009",
]
RMS_CUTOFF = 0.013763083470985293
STRUCTURAL_CUTOFF = 0.01


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def build(root: Path) -> dict[str, Any]:
    seal_path = root / "snapshot_seal.json"
    report_path = root / "r6_unity_report.json"
    if sha256(seal_path) != EXPECTED_SEAL or sha256(report_path) != EXPECTED_REPORT:
        raise RuntimeError("old R6 immutable snapshot identity mismatch")
    report = read_json(report_path)
    groups: dict[str, list[dict[str, Any]]] = {key: [] for key in CONTROL_FAMILIES}
    for record in report["records"]:
        if record.get("sourceKind") == "positive_control" and record.get("sourceId") in groups:
            image = root / record["image"]
            if sha256(image) != record["imageSha256"]:
                raise RuntimeError(f"old control image mismatch: {record['recordId']}")
            groups[record["sourceId"]].append(record)
    controls = []
    references: dict[str, dict[str, Any]] = {}
    for control_id, records in groups.items():
        ordered = sorted(records, key=lambda item: int(item["repeatIndex"]))
        settled = [item for item in ordered if int(item["repeatIndex"]) >= 1]
        if len(ordered) != 3 or len(settled) != 2:
            raise RuntimeError(f"old control repeat support changed: {control_id}")
        reference = settled[-1]
        references[control_id] = reference
        rms, structural = visual_distances(
            image_features(root / settled[0]["image"]), image_features(root / settled[1]["image"])
        )
        controls.append({
            "controlId": control_id,
            "independentFamily": CONTROL_FAMILIES[control_id],
            "probePosition": reference["probePosition"],
            "probeRotation": reference["probeRotation"],
            "settledReferenceRecordId": reference["recordId"],
            "settledReferenceImageSha256": reference["imageSha256"],
            "settledRepeatImageSha256": [item["imageSha256"] for item in settled],
            "settledScoreRange": [
                min(float(item["score"]["total"]) for item in settled),
                max(float(item["score"]["total"]) for item in settled),
            ],
            "settledPairRms128": rms,
            "settledPairStructuralDistance": structural,
            "settledPairPass": rms <= RMS_CUTOFF and structural <= STRUCTURAL_CUTOFF,
        })
    pairwise = []
    distance_by_pair = {}
    for left, right in combinations(sorted(references), 2):
        rms, structural = visual_distances(
            image_features(root / references[left]["image"]),
            image_features(root / references[right]["image"]),
        )
        item = {
            "left": left, "right": right, "rms128": rms,
            "structuralDistance": structural,
            "freshnessDistinguishable": rms > RMS_CUTOFF,
            "dualNovelty": rms > RMS_CUTOFF and structural > STRUCTURAL_CUTOFF,
        }
        pairwise.append(item)
        distance_by_pair[tuple(sorted((left, right)))] = item
    transitions = []
    for left, right in zip(SEQUENCE, SEQUENCE[1:]):
        item = distance_by_pair[tuple(sorted((left, right)))]
        transitions.append({
            "from": left, "to": right, "rms128": item["rms128"],
            "structuralDistance": item["structuralDistance"],
            "freshnessDistinguishable": item["freshnessDistinguishable"],
            "dualNovelty": item["dualNovelty"],
        })
    return {
        "schemaVersion": 1,
        "taskId": "G2-P2-PREOUTCOME-CONTROL-DESCRIPTORS",
        "scope": "Already-consumed old R6 settled positive-control images only; old candidates excluded.",
        "oldSnapshot": str(root),
        "oldSnapshotSealSha256": EXPECTED_SEAL,
        "oldReportSha256": EXPECTED_REPORT,
        "rms128Cutoff": RMS_CUTOFF,
        "structuralCutoff": STRUCTURAL_CUTOFF,
        "mandatorySequence": SEQUENCE,
        "controls": controls,
        "settledReferencePairwise": pairwise,
        "mandatoryTransitions": transitions,
        "allControlSettledPairsPass": all(item["settledPairPass"] for item in controls),
        "allMandatoryTransitionsFreshnessDistinguishable": all(item["freshnessDistinguishable"] for item in transitions),
        "allMandatoryTransitionsDualNovel": all(item["dualNovelty"] for item in transitions),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--old-r6-snapshot", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = build(args.old_r6_snapshot.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(args.output),
        "controls": len(result["controls"]),
        "allControlSettledPairsPass": result["allControlSettledPairsPass"],
        "allMandatoryTransitionsFreshnessDistinguishable": result["allMandatoryTransitionsFreshnessDistinguishable"],
        "allMandatoryTransitionsDualNovel": result["allMandatoryTransitionsDualNovel"],
    }, indent=2))


if __name__ == "__main__":
    main()
