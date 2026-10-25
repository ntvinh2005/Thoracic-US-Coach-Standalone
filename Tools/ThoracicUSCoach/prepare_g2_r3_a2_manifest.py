from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image


FLOOR = 0.5912307500839233
MANDATORY_FAMILY = "train_family_0090"
MANDATORY_INDICES = (100, 101)
DECISION_PROCEDURE = (
    "Predeclared before fresh outcomes. INCONCLUSIVE if fewer than 12/16 poses are valid, either mandatory "
    "0090 pose is invalid, or component/label consistency fails. Otherwise compute repeat-render RMS128 q95 "
    "and matched-pair original/fresh RMS128. A pair is visually separated only when its between-pose RMS128 "
    "is greater than 3x repeat q95. GO_FOR_IMAGE_ONLY_RESEARCH requires >=75% selected opposite-label pairs "
    "visually separated and frozen image-only positive recall >=80% with zero observed false positives. "
    "NEEDS_MULTIVIEW requires the single-frame GO rule to fail but genuine causal history to add >=0.03 AP "
    "and resolve >=50% of ambiguous pairs without privileged inputs. PARTIALLY_OBSERVABLE_WITH_EVIDENCE "
    "requires at least two valid ambiguous opposite-label pairs whose score difference has >=0.02 absolute "
    "contribution explained by anatomy/target/needle/contact privileged terms and which remain missed or "
    "uncertain for frozen image-only diagnostics. NO_GO_CURRENT_TARGET requires >=75% pairs ambiguous, "
    ">=80% of their absolute score differences explained by privileged terms, and no qualifying causal-history "
    "gain. Ties or insufficient causal evidence yield INCONCLUSIVE rather than a stronger verdict."
)


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def grayscale(path: Path, size: int = 128) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("L").resize((size, size), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0


def rms(left: np.ndarray, right: np.ndarray) -> float:
    return float(np.sqrt(np.mean((left - right) ** 2)))


def resolve_source(source_root: Path, relative: str) -> Path:
    normalized = str(relative).replace("\\", "/")
    marker = "Artifacts/ThoracicUSCoach/"
    return source_root / "Artifacts" / "ThoracicUSCoach" / normalized.split(marker, 1)[1]


def vector(payload: dict[str, Any]) -> dict[str, float]:
    return {key: float(payload[key]) for key in ("x", "y", "z")}


def quaternion(payload: dict[str, Any]) -> dict[str, float]:
    return {key: float(payload[key]) for key in ("x", "y", "z", "w")}


def main() -> None:
    parser = argparse.ArgumentParser(description="Predeclare bounded G2-R3-A2 Phase 2 pose audit")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--source-repo", type=Path, default=Path(r"D:\CSSALT\Thoracic-US-Coach-Standalone"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    source_root = args.source_repo.resolve()
    ledger_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r2_20261024" / "family_usage_ledger.json"
    ledger = load_json(ledger_path)
    rows = [row for row in ledger["rows"] if row["g2R2Role"] in {"train", "calibration"}]

    frames: list[dict[str, Any]] = []
    source_hash_lines = [f"{ledger_path.relative_to(repo)}:{sha256(ledger_path)}"]
    for row in rows:
        directory = source_root / str(row["episodeDirectory"]).replace("/", "\\")
        stream_path = directory / "stream_manifest.json"
        annotation_path = directory / "privileged_annotations.json"
        stream = load_json(stream_path)
        annotation = load_json(annotation_path)
        source_hash_lines.extend((f"{stream_path.relative_to(source_root)}:{sha256(stream_path)}", f"{annotation_path.relative_to(source_root)}:{sha256(annotation_path)}"))
        labels = {int(item["index"]): item for item in annotation["frames"]}
        images = {int(item["index"]): item for item in stream["frames"]}
        for index, label in labels.items():
            image_item = images[index]
            image_path = resolve_source(source_root, image_item["image"])
            actual_hash = sha256(image_path)
            if actual_hash != image_item["imageSha256"]:
                raise RuntimeError(f"Image hash mismatch: {image_path}")
            frames.append({
                "family": row["family"],
                "sourceSplit": row["sourceSplit"],
                "role": row["g2R2Role"],
                "index": index,
                "image": image_path,
                "imageRelative": str(image_item["image"]).replace("\\", "/"),
                "imageSha256": actual_hash,
                "score": float(label["finalTargetScore"]),
                "acceptable": bool(label["finalTargetAcceptable"]),
                "probePosition": vector(label["probePosition"]),
                "probeRotation": quaternion(label["probeRotation"]),
                "contactPosition": vector(label["contactPosition"]),
                "signedDepthMm": float(label["signedDepthMm"]),
                "contactValid": bool(label["contactValid"]),
                "workspaceValid": bool(label["workspaceValid"]),
                "workspaceOrigin": vector(annotation["workspaceOrigin"]),
                "actuatorCranialAxis": vector(annotation["actuatorCranialAxis"]),
                "actuatorLateralAxis": vector(annotation["actuatorLateralAxis"]),
                "longitudinalHalfExtentMm": float(annotation["longitudinalHalfExtentMm"]),
                "lateralHalfExtentMm": float(annotation["lateralHalfExtentMm"]),
            })

    lookup = {(item["family"], item["index"]): item for item in frames}
    selected: list[tuple[dict[str, Any], str]] = []
    used: set[tuple[str, int]] = set()

    def add(item: dict[str, Any], reason: str) -> None:
        key = item["family"], item["index"]
        if key not in used:
            selected.append((item, reason))
            used.add(key)

    for index, reason in ((100, "mandatory known false-negative positive"), (101, "mandatory same-family matched negative"), (99, "nearby causal-history predecessor"), (102, "nearby causal-history successor")):
        add(lookup[(MANDATORY_FAMILY, index)], reason)

    positives = [item for item in frames if item["acceptable"] and item["family"] != MANDATORY_FAMILY]
    negatives = [item for item in frames if not item["acceptable"] and item["family"] != MANDATORY_FAMILY]
    seen_families: set[str] = set()
    for item in sorted(positives, key=lambda value: value["score"] - FLOOR):
        if item["family"] in seen_families:
            continue
        add(item, "independent near-boundary positive")
        seen_families.add(item["family"])
        if len(seen_families) == 3:
            break
    seen_families.clear()
    for item in sorted(negatives, key=lambda value: FLOOR - value["score"]):
        if item["family"] in seen_families:
            continue
        add(item, "independent near-boundary negative")
        seen_families.add(item["family"])
        if len(seen_families) == 3:
            break

    add(max(positives, key=lambda item: item["score"]), "clearly positive control")
    add(min(negatives, key=lambda item: item["score"]), "clearly negative control")

    mandatory_pixels = grayscale(lookup[(MANDATORY_FAMILY, 100)]["image"])
    hard = []
    for item in negatives:
        if (item["family"], item["index"]) in used:
            continue
        hard.append((rms(mandatory_pixels, grayscale(item["image"])), item))
    seen_families.clear()
    for distance, item in sorted(hard, key=lambda value: value[0]):
        if item["family"] in seen_families:
            continue
        add(item, f"cross-family hard negative nearest to family0090/frame100 at RMS128={distance:.9f}")
        seen_families.add(item["family"])
        if len(selected) == 16:
            break
    if len(selected) != 16:
        raise RuntimeError(f"Expected exactly 16 predeclared cases, got {len(selected)}")

    cases = []
    for ordinal, (item, reason) in enumerate(selected):
        cases.append({
            "caseId": f"case_{ordinal:02d}_{item['family']}_f{item['index']:04d}",
            "family": item["family"],
            "sourceFrameIndex": item["index"],
            "selectionReason": reason,
            "sourceImage": item["imageRelative"],
            "sourceImageSha256": item["imageSha256"],
            "originalScore": item["score"],
            "originalAcceptable": item["acceptable"],
            "probePosition": item["probePosition"],
            "probeRotation": item["probeRotation"],
            "contactPosition": item["contactPosition"],
            "signedDepthMm": item["signedDepthMm"],
            "originalContactValid": item["contactValid"],
            "originalWorkspaceValid": item["workspaceValid"],
            "workspaceOrigin": item["workspaceOrigin"],
            "actuatorCranialAxis": item["actuatorCranialAxis"],
            "actuatorLateralAxis": item["actuatorLateralAxis"],
            "longitudinalHalfExtentMm": item["longitudinalHalfExtentMm"],
            "lateralHalfExtentMm": item["lateralHalfExtentMm"],
        })

    scene = repo / "Assets" / "ThoracicUSCoach" / "Scenes" / "ThoracicUSCoach_POC.unity"
    evaluator = repo / "Assets" / "ThoracicUSCoach" / "Runtime" / "ThoracicUSCoachController.cs"
    active2d = repo / "Assets" / "ThoracicUSCoach" / "Runtime" / "ThoracicUSCoachController.Active2D.cs"
    payload = {
        "schemaVersion": 1,
        "protocolId": "g2_r3_a2_exact_pose_v1_20261025",
        "generatedUtc": "2026-10-25T00:00:00Z",
        "scene": "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity",
        "unityVersion": "6000.3.9f1",
        "outputDirectory": "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r3_a2_20261025/unity_capture",
        "acceptableScoreFloor": FLOOR,
        "positionToleranceMm": 0.05,
        "rotationToleranceDeg": 0.05,
        "contactPositionToleranceMm": 0.10,
        "signedDepthToleranceMm": 0.10,
        "repeatRenders": 6,
        "decisionProcedure": DECISION_PROCEDURE,
        "selectionSourceSha256": hashlib.sha256("\n".join(sorted(source_hash_lines)).encode()).hexdigest(),
        "sceneSha256": sha256(scene),
        "evaluatorSha256": sha256(evaluator),
        "active2DControllerSha256": sha256(active2d),
        "cases": cases,
    }
    output = args.output or repo / "Tools" / "ThoracicUSCoach" / "experiments" / "g2_r3_a2_exact_pose_v1.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "cases": len(cases), "manifestSha256": sha256(output), "selection": [{"caseId": case["caseId"], "reason": case["selectionReason"], "score": case["originalScore"], "acceptable": case["originalAcceptable"]} for case in cases]}, indent=2))


if __name__ == "__main__":
    main()
