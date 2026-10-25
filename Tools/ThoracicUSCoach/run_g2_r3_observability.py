from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

import numpy as np
from PIL import Image, ImageDraw


FLOOR = 0.5912307500839233
DATASET_RUN_ID = "main_camera1024_v4_20261023"
EXPERIMENT_ID = "thoracic_1d_contract_v3_20261022"
COMPONENTS = (
    "anatomyRelationship",
    "pleuraVisibility",
    "targetRegion",
    "acousticWindow",
    "needleGeometry",
    "shadowPenalty",
    "contactPenalty",
)
WEIGHTS = {
    "anatomyRelationship": 0.25,
    "pleuraVisibility": 0.23,
    "targetRegion": 0.20,
    "acousticWindow": 0.17,
    "needleGeometry": 0.15,
    "shadowPenalty": -0.18,
    "contactPenalty": -0.50,
}
OBSERVABILITY = {
    "anatomyRelationship": "PRIVILEGED_ANATOMY_RAYCAST",
    "pleuraVisibility": "MIXED_PIXEL_AND_PRIVILEGED_ANATOMY",
    "targetRegion": "PRIVILEGED_TARGET_SCAN_PLANE_GEOMETRY",
    "acousticWindow": "CLEAN_ULTRASOUND_PIXELS",
    "needleGeometry": "PRIVILEGED_NEEDLE_SCAN_PLANE_GEOMETRY",
    "shadowPenalty": "CLEAN_ULTRASOUND_PIXELS",
    "contactPenalty": "PRIVILEGED_CONTACT_STATE",
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def summary(values: Iterable[float]) -> dict[str, float | int | None]:
    data = np.asarray(list(values), dtype=np.float64)
    if not len(data):
        return {"count": 0, "min": None, "median": None, "mean": None, "q95": None, "max": None}
    return {
        "count": int(len(data)),
        "min": float(np.min(data)),
        "median": float(np.median(data)),
        "mean": float(np.mean(data)),
        "q95": float(np.quantile(data, 0.95)),
        "max": float(np.max(data)),
    }


def grayscale(path: Path, size: int) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("L").resize((size, size), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0


def rms(left: np.ndarray, right: np.ndarray) -> float:
    return float(np.sqrt(np.mean((left - right) ** 2)))


def resolve_image(source_root: Path, relative: str) -> Path:
    normalized = str(relative).replace("\\", "/")
    marker = "Artifacts/ThoracicUSCoach/"
    if marker not in normalized:
        raise RuntimeError(f"Unexpected evidence path: {relative}")
    return source_root / "Artifacts" / "ThoracicUSCoach" / normalized.split(marker, 1)[1]


def load_component_records(source_root: Path) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[Path]]:
    records: list[dict[str, Any]] = []
    transitions: list[dict[str, Any]] = []
    manifests: list[Path] = []
    dataset_root = source_root / "Artifacts" / "ThoracicUSCoach" / "dataset"
    for split in ("train", "dev"):
        for manifest_path in sorted((dataset_root / split).glob("*/sweep_manifest.json")):
            payload = load_json(manifest_path)
            if payload.get("datasetRunId") != DATASET_RUN_ID or payload.get("experimentId") != EXPERIMENT_ID:
                continue
            manifests.append(manifest_path)
            trajectory = payload["trajectory"]
            family = str(trajectory["configurationFamily"])
            sweep_id = str(payload["sweepId"])
            floor = float(trajectory["referenceScoreFloor"])
            if not math.isclose(floor, FLOOR, abs_tol=1e-8):
                raise RuntimeError(f"Frozen floor mismatch in {manifest_path}: {floor}")
            by_index: dict[int, dict[str, Any]] = {}
            for item in payload["frames"]:
                image = resolve_image(source_root, item["image"])
                if sha256(image) != item["imageSha256"]:
                    raise RuntimeError(f"Image hash mismatch: {image}")
                score = {key: float(item["score"][key]) for key in (*COMPONENTS, "total")}
                observed = {key: float(item["observedScore"][key]) for key in (*COMPONENTS, "total")}
                record = {
                    "split": split,
                    "family": family,
                    "sweepId": sweep_id,
                    "stepIndex": int(item["stepIndex"]),
                    "image": image,
                    "imageRelative": str(image.relative_to(source_root)).replace("\\", "/"),
                    "score": score,
                    "observedScore": observed,
                    "acceptable": bool(item["targetAcceptable"]),
                    "contactValid": bool(item["contactValid"]),
                    "workspaceValid": bool(item["workspaceValid"]),
                    "teacherAction": int(item["teacherAction"]),
                    "pose": item["pose"],
                }
                records.append(record)
                by_index[record["stepIndex"]] = record
            for item in payload.get("transitions", []):
                previous = resolve_image(source_root, item["previousImage"])
                current = resolve_image(source_root, item["currentImage"])
                if sha256(previous) != item["previousImageSha256"] or sha256(current) != item["currentImageSha256"]:
                    raise RuntimeError(f"Transition hash mismatch: {manifest_path} {item['transitionId']}")
                current_record = by_index.get(int(item["currentIndex"]))
                previous_record = by_index.get(int(item["previousIndex"]))
                transitions.append({
                    "split": split,
                    "family": family,
                    "sweepId": sweep_id,
                    "transitionId": str(item["transitionId"]),
                    "kind": str(item["transitionKind"]),
                    "executedAction": int(item["executedAction"]),
                    "actionWasExecuted": bool(item["actionWasExecuted"]),
                    "boundaryClamped": bool(item["boundaryClamped"]),
                    "previousImage": previous,
                    "currentImage": current,
                    "previousRecord": previous_record,
                    "currentRecord": current_record,
                })
    return records, transitions, manifests


def component_audit(records: list[dict[str, Any]]) -> dict[str, Any]:
    reconstruction = []
    observed_reconstruction = []
    label_mismatches = []
    component_repeat: dict[str, list[float]] = defaultdict(list)
    total_repeat = []
    flips = []
    grouped_absolute_sum: dict[str, list[float]] = defaultdict(list)
    for record in records:
        score = record["score"]
        observed = record["observedScore"]
        exact = sum(WEIGHTS[key] * score[key] for key in COMPONENTS)
        observed_exact = sum(WEIGHTS[key] * observed[key] for key in COMPONENTS)
        reconstruction.append(abs(exact - score["total"]))
        observed_reconstruction.append(abs(observed_exact - observed["total"]))
        expected = score["total"] >= FLOOR
        if expected != record["acceptable"]:
            label_mismatches.append({"family": record["family"], "stepIndex": record["stepIndex"]})
        per_frame_groups = {"privileged": 0.0, "pixel": 0.0, "mixed": 0.0}
        for key in COMPONENTS:
            component_repeat[key].append(abs(score[key] - observed[key]))
            contribution = abs(WEIGHTS[key] * score[key])
            source = OBSERVABILITY[key]
            if source.startswith("PRIVILEGED"):
                per_frame_groups["privileged"] += contribution
            elif source.startswith("CLEAN"):
                per_frame_groups["pixel"] += contribution
            else:
                per_frame_groups["mixed"] += contribution
        for group, value in per_frame_groups.items():
            grouped_absolute_sum[group].append(value)
        total_repeat.append(abs(score["total"] - observed["total"]))
        if (score["total"] >= FLOOR) != (observed["total"] >= FLOOR):
            flips.append({
                "family": record["family"], "stepIndex": record["stepIndex"],
                "score": score["total"], "observedScore": observed["total"],
            })
    return {
        "formula": "0.25*anatomyRelationship + 0.23*pleuraVisibility + 0.20*targetRegion + 0.17*acousticWindow + 0.15*needleGeometry - 0.18*shadowPenalty - 0.50*contactPenalty",
        "weights": WEIGHTS,
        "inputClassification": OBSERVABILITY,
        "frames": len(records),
        "families": len({record["family"] for record in records}),
        "labelScoreMismatch": len(label_mismatches),
        "reconstructionAbsoluteError": summary(reconstruction),
        "observedReconstructionAbsoluteError": summary(observed_reconstruction),
        "repeatAtCapture": {
            "description": "Manifest candidate score versus fresh observedScore at the same commanded pose; not a new R3 Unity run.",
            "totalAbsoluteDelta": summary(total_repeat),
            "componentAbsoluteDelta": {key: summary(values) for key, values in component_repeat.items()},
            "binaryLabelFlips": len(flips),
            "flipExamples": flips[:20],
        },
        "meanAbsoluteWeightedContributionPerComponent": {
            key: float(np.mean([abs(WEIGHTS[key] * record["score"][key]) for record in records])) for key in COMPONENTS
        },
        "absoluteWeightedContributionSumByInputClass": {
            group: summary(values) for group, values in grouped_absolute_sum.items()
        },
        "validity": {
            "contactInvalidFrames": sum(not record["contactValid"] for record in records),
            "workspaceInvalidFrames": sum(not record["workspaceValid"] for record in records),
        },
    }


def representative_records(records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    dev = [record for record in records if record["split"] == "dev"]
    candidates = {
        "clear_positive": max((record for record in dev if record["acceptable"]), key=lambda record: record["score"]["total"]),
        "boundary_positive": min((record for record in dev if record["acceptable"]), key=lambda record: record["score"]["total"] - FLOOR),
        "boundary_negative": min((record for record in dev if not record["acceptable"]), key=lambda record: FLOOR - record["score"]["total"]),
        "clear_negative": min((record for record in dev if not record["acceptable"]), key=lambda record: record["score"]["total"]),
    }
    result = []
    for role, record in candidates.items():
        result.append({
            "role": role,
            "family": record["family"],
            "stepIndex": record["stepIndex"],
            "image": record["imageRelative"],
            "total": record["score"]["total"],
            "acceptable": record["acceptable"],
            "margin": record["score"]["total"] - FLOOR,
            "components": {
                key: {
                    "raw": record["score"][key],
                    "weight": WEIGHTS[key],
                    "weightedContribution": WEIGHTS[key] * record["score"][key],
                    "inputClass": OBSERVABILITY[key],
                }
                for key in COMPONENTS
            },
        })
    return result


def pair_payload(left: dict[str, Any], right: dict[str, Any], resolutions: tuple[int, ...]) -> dict[str, Any]:
    return {
        "positive": {
            "family": left["family"], "stepIndex": left["stepIndex"], "image": left["imageRelative"],
            "score": left["score"]["total"], "margin": left["score"]["total"] - FLOOR,
            "components": left["score"],
        },
        "negative": {
            "family": right["family"], "stepIndex": right["stepIndex"], "image": right["imageRelative"],
            "score": right["score"]["total"], "margin": right["score"]["total"] - FLOOR,
            "components": right["score"],
        },
        "sameFamily": left["family"] == right["family"],
        "imageRms": {str(size): rms(grayscale(left["image"], size), grayscale(right["image"], size)) for size in resolutions},
        "weightedContributionDelta": {
            key: WEIGHTS[key] * (left["score"][key] - right["score"][key]) for key in COMPONENTS
        },
    }


def matched_pairs(records: list[dict[str, Any]], limit: int = 12) -> tuple[dict[str, Any], list[tuple[dict[str, Any], dict[str, Any]]]]:
    dev = [record for record in records if record["split"] == "dev"]
    positives = [record for record in dev if record["acceptable"]]
    negatives = [record for record in dev if not record["acceptable"]]
    positive_features = np.stack([grayscale(record["image"], 32).reshape(-1) for record in positives])
    negative_features = np.stack([grayscale(record["image"], 32).reshape(-1) for record in negatives])
    distance_squared = (
        np.sum(positive_features * positive_features, axis=1)[:, None]
        + np.sum(negative_features * negative_features, axis=1)[None, :]
        - 2.0 * positive_features @ negative_features.T
    )
    distances = np.sqrt(np.maximum(distance_squared, 0.0) / positive_features.shape[1])
    order = np.argsort(distances, axis=None)
    selected: list[tuple[dict[str, Any], dict[str, Any]]] = []
    used: set[tuple[str, int, str, int]] = set()
    for flat in order:
        p_index, n_index = np.unravel_index(flat, distances.shape)
        pair = positives[int(p_index)], negatives[int(n_index)]
        key = (pair[0]["family"], pair[0]["stepIndex"], pair[1]["family"], pair[1]["stepIndex"])
        if key in used:
            continue
        used.add(key)
        selected.append(pair)
        if len(selected) == limit:
            break
    within = []
    by_family: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in dev:
        by_family[record["family"]].append(record)
    for family_records in by_family.values():
        ordered = sorted(family_records, key=lambda record: record["stepIndex"])
        for left, right in zip(ordered, ordered[1:]):
            if left["acceptable"] == right["acceptable"]:
                continue
            positive, negative = (left, right) if left["acceptable"] else (right, left)
            within.append((positive, negative))
    within.sort(key=lambda pair: rms(grayscale(pair[0]["image"], 32), grayscale(pair[1]["image"], 32)))
    payload = {
        "selection": "Nearest opposite-label clean-image pairs on family-disjoint development data at 32x32; labels inspected only after distance selection.",
        "crossFamilyNearest": [pair_payload(left, right, (16, 32, 64, 128)) for left, right in selected],
        "withinFamilyBoundaryNearest": [pair_payload(left, right, (16, 32, 64, 128)) for left, right in within[:limit]],
        "counts": {"devPositiveFrames": len(positives), "devNegativeFrames": len(negatives), "withinFamilyBoundaryPairs": len(within)},
    }
    return payload, selected[:6]


def transition_audit(transitions: list[dict[str, Any]]) -> dict[str, Any]:
    valid = [item for item in transitions if item["actionWasExecuted"]]
    pixel_delta = []
    score_delta = []
    by_kind = Counter(item["kind"] for item in valid)
    by_action = Counter(str(item["executedAction"]) for item in valid)
    rejected = 0
    for item in valid:
        pixel_delta.append(rms(grayscale(item["previousImage"], 32), grayscale(item["currentImage"], 32)))
        previous, current = item["previousRecord"], item["currentRecord"]
        if previous is not None and current is not None:
            score_delta.append(current["score"]["total"] - previous["score"]["total"])
        rejected += int(item["boundaryClamped"])
    return {
        "source": "Recorded transitions from the component-rich v4 dataset; uses executedAction/actionWasExecuted, never teacherAction as feedback.",
        "transitions": len(transitions),
        "executedTransitions": len(valid),
        "boundaryClamped": rejected,
        "kindCounts": dict(by_kind),
        "executedActionCounts": dict(by_action),
        "imageRms32": summary(pixel_delta),
        "signedScoreDelta": summary(score_delta),
        "limitation": "These short transition probes are supplementary causal evidence; their small fixed action set is not an independent recognition benchmark.",
    }


def load_family_0090(source_root: Path) -> dict[str, Any]:
    directory = source_root / "Artifacts" / "ThoracicUSCoach" / "active2d_dataset" / "train" / "A2D_active2d_phase2_raster_v2_20261023_0090_S20973733"
    stream = load_json(directory / "stream_manifest.json")
    annotation = load_json(directory / "privileged_annotations.json")
    labels = {int(item["index"]): item for item in annotation["frames"]}
    frames = []
    for item in stream["frames"]:
        index = int(item["index"])
        label = labels[index]
        image = resolve_image(source_root, item["image"])
        if sha256(image) != item["imageSha256"]:
            raise RuntimeError(f"Image hash mismatch: {image}")
        frames.append({
            "index": index, "image": image, "imageRelative": str(image.relative_to(source_root)).replace("\\", "/"),
            "score": float(label["finalTargetScore"]), "acceptable": bool(label["finalTargetAcceptable"]),
            "issuedAction": str(item.get("issuedAction", "RESET")), "actionAccepted": bool(item.get("actionAccepted", True)),
            "contactValid": bool(label["contactValid"]), "workspaceValid": bool(label["workspaceValid"]),
        })
    positives = [frame for frame in frames if frame["acceptable"]]
    negatives = [frame for frame in frames if not frame["acceptable"]]
    if not positives:
        raise RuntimeError("train_family_0090 unexpectedly contains no positive frame")
    positive = min(positives, key=lambda frame: frame["score"] - FLOOR)
    distances = [(rms(grayscale(positive["image"], 128), grayscale(negative["image"], 128)), negative) for negative in negatives]
    distances.sort(key=lambda item: item[0])
    nearest = distances[0][1]
    return {
        "family": str(stream["family"]),
        "frames": len(frames),
        "positiveFrames": len(positives),
        "positive": {key: positive[key] for key in ("index", "imageRelative", "score", "acceptable", "issuedAction", "actionAccepted", "contactValid", "workspaceValid")},
        "nearestOpposite": {key: nearest[key] for key in ("index", "imageRelative", "score", "acceptable", "issuedAction", "actionAccepted", "contactValid", "workspaceValid")},
        "imageRms": {str(size): rms(grayscale(positive["image"], size), grayscale(nearest["image"], size)) for size in (16, 32, 64, 128, 1024)},
        "scoreMargin": {"positive": positive["score"] - FLOOR, "negative": nearest["score"] - FLOOR},
        "componentAvailability": "MISSING_IN_ACTIVE2D_SCHEMA",
        "componentLimitation": "This family stores finalTargetScore only. Exact constituent attribution requires an exclusive Unity pose-restoration diagnostic; none was fabricated.",
        "_images": (positive["image"], nearest["image"]),
    }


def montage(pairs: list[tuple[dict[str, Any], dict[str, Any]]], family_0090: dict[str, Any], output: Path, annotated: bool) -> None:
    rows = len(pairs) + 1
    width, cell_height = 1024, 250
    canvas = Image.new("RGB", (width, rows * cell_height), "white")
    draw = ImageDraw.Draw(canvas)
    image_pairs = [(left["image"], right["image"], left, right) for left, right in pairs]
    p_image, n_image = family_0090["_images"]
    image_pairs.append((p_image, n_image, family_0090["positive"], family_0090["nearestOpposite"]))
    for row, (left_path, right_path, left, right) in enumerate(image_pairs):
        for column, (path, metadata) in enumerate(((left_path, left), (right_path, right))):
            with Image.open(path) as image:
                image = image.convert("RGB")
                image.thumbnail((490, 210), Image.Resampling.LANCZOS)
                x = column * 512 + (512 - image.width) // 2
                y = row * cell_height + 30
                canvas.paste(image, (x, y))
            label = f"Pair {row + 1}{'A' if column == 0 else 'B'}"
            if annotated:
                family = metadata.get("family", family_0090["family"])
                index = metadata.get("stepIndex", metadata.get("index"))
                score = metadata.get("score", {}).get("total") if isinstance(metadata.get("score"), dict) else metadata.get("score")
                acceptable = metadata.get("acceptable", column == 0)
                label += f" | {family} i={index} label={acceptable} score={score:.6f}"
            draw.text((column * 512 + 8, row * cell_height + 8), label, fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)


def main() -> None:
    parser = argparse.ArgumentParser(description="G2-R3 development-only ground-truth observability audit")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--source-repo", type=Path, default=Path(r"D:\CSSALT\Thoracic-US-Coach-Standalone"))
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    source_root = args.source_repo.resolve()
    output = args.output_dir or repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r3_observability_20261025"
    output.mkdir(parents=True, exist_ok=True)

    records, transitions, manifests = load_component_records(source_root)
    components = component_audit(records)
    representatives = representative_records(records)
    pairs, montage_pairs = matched_pairs(records)
    transition = transition_audit(transitions)
    family_0090 = load_family_0090(source_root)
    montage(montage_pairs, family_0090, output / "g2_r3_matched_pairs_blinded.png", annotated=False)
    montage(montage_pairs, family_0090, output / "g2_r3_matched_pairs_annotated.png", annotated=True)
    family_0090.pop("_images")

    controller = repo / "Assets" / "ThoracicUSCoach" / "Runtime" / "ThoracicUSCoachController.cs"
    scene = repo / "Assets" / "ThoracicUSCoach" / "Scenes" / "ThoracicUSCoach_POC.unity"
    g0_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g0_ground_truth_observability" / "g0_observability_report.json"
    r2_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r2_20261024" / "g2_r2_results.json"
    repeat_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r2_20261024" / "g2_r2_repeat_render_audit.json"
    g0 = load_json(g0_path)
    r2 = load_json(r2_path)
    r2_repeat = load_json(repeat_path)

    classification = "PARTIALLY_OBSERVABLE"
    decision = "NO_GO_DEPENDENT_RETRAINING"
    report = {
        "schemaVersion": 1,
        "taskId": "G2-R3-A",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "scope": "Development diagnostics only; no locked test, new capture, retraining, threshold change, deployment, or evaluator change.",
        "classification": classification,
        "scientificDecision": decision,
        "frozenAcceptableScoreFloor": FLOOR,
        "sourceIdentity": {
            "developmentRepo": str(repo),
            "componentEvidenceRepo": str(source_root),
            "datasetRunId": DATASET_RUN_ID,
            "experimentId": EXPERIMENT_ID,
            "manifestCount": len(manifests),
            "manifestSetSha256": hashlib.sha256("\n".join(f"{path.relative_to(source_root)}:{sha256(path)}" for path in manifests).encode()).hexdigest(),
            "evaluatorSource": str(controller.relative_to(repo)).replace("\\", "/"),
            "evaluatorSha256": sha256(controller),
            "scene": str(scene.relative_to(repo)).replace("\\", "/"),
            "sceneSha256": sha256(scene),
            "sceneWorktreeWasModified": True,
        },
        "scoreDecomposition": components,
        "representativeObservations": representatives,
        "matchedPositiveNegative": pairs,
        "hardFamily0090": family_0090,
        "repeatRenderEvidence": {
            "componentRichHistoricalSameCommandedPose": components["repeatAtCapture"],
            "g2TemporalSamePoseConsecutive": r2_repeat,
            "newR3UnityRepeatRun": {
                "status": "BLOCKED_NO_EXCLUSIVE_UNITY_ACCESS",
                "reason": "Existing Unity editor processes were active; protocol forbids terminating or opening the same project concurrently.",
                "claimedPass": False,
                "positionRestorationError": None,
                "rotationRestorationError": None,
                "contactWorkspaceRevalidation": "NOT_RUN",
            },
        },
        "causalMultiView": {
            "recordedExecutedActionTransitions": transition,
            "priorG0": {
                "verdict": g0.get("verdict"),
                "singleFrame": g0.get("existingActive2DDevelopment", {}).get("singleImageDiagnostic"),
                "temporal": g0.get("existingActive2DDevelopment", {}).get("pastImageDeltaDiagnostic"),
                "nearestOppositeLabelRaw16Rms": g0.get("existingActive2DDevelopment", {}).get("nearestOppositeLabelRaw16Rms"),
                "note": "Historical development diagnostic, not fresh heldout evidence.",
            },
            "g2R2Outcome": {
                "status": r2["status"],
                "baselineRecall": r2["baseline"]["calibration"]["classification"]["recall"],
                "robustAcrossSeeds": r2["robustAcrossSeeds"],
                "calibrationGatePass": r2["calibrationGatePass"],
                "lockedTest": r2["lockedTest"],
            },
        },
        "unityRegression": {
            "status": "NOT_RUN",
            "reason": "Exclusive Unity access unavailable; no other Unity process was terminated.",
            "phase1Claim": "UNCHANGED_NOT_REVALIDATED_IN_R3",
            "safetyClaim": "UNCHANGED_NOT_REVALIDATED_IN_R3",
        },
        "gateMatrix": {
            "A1ScoreDecomposition": "PASS",
            "A2MatchedPairs": "PASS_WITH_0090_COMPONENT_LIMITATION",
            "A3FreshRepeatRender": "BLOCKED",
            "A4CausalObservability": classification,
            "A5UnityRegression": "NOT_RUN",
            "A6Decision": decision,
            "R3B": "BLOCKED_BY_A6",
            "R3C": "BLOCKED_BY_A6",
            "R3D": "BLOCKED_BY_A6",
        },
        "rationale": [
            "The exact evaluator total is reproducible from its seven stored components, but several high-weight terms require anatomy raycasts or target/needle/contact geometry unavailable to the image-only actor.",
            "Opposite-label clean images remain close across resolutions, including the known train_family_0090 miss; its active2d schema omits constituent scores, so no anatomical attribution is invented.",
            "Historical temporal evidence improves ranking in one diagnostic, but G2-R2 temporal candidates do not robustly recover the required positives across families.",
            "Finite pairs do not prove mathematical unobservability; the conservative conclusion is partial observability under the current permitted observations and frozen evaluator.",
        ],
        "proposedAlternativesRequiringUserReview": [
            "Keep the frozen target and explicitly allow simulator-privileged geometry for Phase 2 STOP (changes the image-only product contract).",
            "Define and independently validate an image-observable acquisition target (changes the scientific target/evaluator and requires separate review).",
            "Retain image-only inference but make multi-view uncertainty/abstention a first-class product outcome; this does not guarantee the current 80% recall requirement.",
            "When exclusive Unity access is available, run a small exact-pose component diagnostic for family_0090 and near-boundary controls before selecting an alternative.",
        ],
    }
    save_json(output / "g2_r3_a_observability_report.json", report)
    print(json.dumps({
        "output": str(output),
        "classification": classification,
        "decision": decision,
        "records": len(records),
        "families": components["families"],
        "labelMismatches": components["labelScoreMismatch"],
        "repeatFlips": components["repeatAtCapture"]["binaryLabelFlips"],
        "transitionCount": transition["transitions"],
    }, indent=2))


if __name__ == "__main__":
    main()
