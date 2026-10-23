from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Sequence

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from active2d_contract import LEVEL_ORDER, project_root
from active2d_data import feature_matrix, load_active2d_frames
from visual_landmark_topology import assign_visual_landmark, project_features


RELEVANT = (0, 3, 5, 6, 7, 14)
INTERPRETATIONS = {
    0: "UNCERTAIN: repeated bright lower reflector with variable double-contour/notch; compatible with a generic osseous boundary/acoustic window, not enough to distinguish rib, transverse process, or lamina.",
    3: "UNCERTAIN: small concavity/notch along a bright curved lower contour appears repeatable, but T6/T7 overlap prevents an anatomical identity claim.",
    5: "UNCERTAIN: no development representative; train/test examples are needed before judging whether the mostly-T6 association is anatomy or family-specific appearance.",
    6: "UNCERTAIN: multiple/branching bright lower contours recur, but mixed T6/T7 support is consistent with generic bone-shadow/window geometry.",
    7: "UNCERTAIN: sparse support and a broad superficial speckle band over a curved reflector; insufficient evidence for a named landmark.",
    14: "UNCERTAIN: sloped superficial speckle wedge and sparse lower reflector/dark field; likely acoustic-window/texture variation rather than a stable vertebral anchor.",
}


def entropy(counts: Counter[str]) -> float | None:
    total = sum(counts.values())
    if not total:
        return None
    values = np.asarray([counts[level] / total for level in LEVEL_ORDER if counts[level]], np.float64)
    return float(-np.sum(values * np.log(values)) / math.log(len(LEVEL_ORDER)))


def blinded_sample(sample: dict[str, Any]) -> dict[str, Any]:
    return {key: sample[key] for key in ("sampleCode", "prototypeId", "selectionRole")}


def select_examples(prototype: int, frames: Sequence, assignments: Sequence[dict[str, Any]],
                    distances: np.ndarray, radius: float) -> list[dict[str, Any]]:
    nearest = [i for i, item in enumerate(assignments) if int(item["prototype"]) == prototype]
    matched = sorted((i for i in nearest if assignments[i]["status"] == "MATCHED"),
                     key=lambda i: float(distances[i, prototype]))
    chosen, families = [], set()
    for i in matched:
        if frames[i].family in families:
            continue
        chosen.append((i, "closest_diverse_matched"))
        families.add(frames[i].family)
        if len(chosen) == 3:
            break
    for i in matched:
        if len(chosen) >= 5:
            break
        if i not in {x[0] for x in chosen}:
            chosen.append((i, "additional_matched"))
    if matched:
        boundary = max(matched, key=lambda i: float(distances[i, prototype]) / max(radius, 1e-9))
        if boundary not in {x[0] for x in chosen}:
            if len(chosen) >= 5:
                chosen[-1] = (boundary, "near_radius_matched")
            else:
                chosen.append((boundary, "near_radius_matched"))
        else:
            chosen = [(i, "near_radius_matched" if i == boundary else role) for i, role in chosen]
    for status, role in (("AMBIGUOUS", "ambiguous_nearest"), ("UNMATCHED", "unmatched_nearest")):
        candidates = sorted((i for i in nearest if assignments[i]["status"] == status),
                            key=lambda i: float(distances[i, prototype]))
        if candidates:
            chosen.append((candidates[0], role))
    result = []
    for number, (i, role) in enumerate(chosen, 1):
        frame, item = frames[i], assignments[i]
        result.append({"sampleCode": f"P{prototype}-S{number}", "prototypeId": prototype,
                       "selectionRole": role, "assignmentStatus": item["status"],
                       "distance": float(item["distance"]), "radius": radius,
                       "radiusFraction": float(item["distance"]) / max(radius, 1e-9),
                       "margin": float(item["margin"]), "split": frame.split,
                       "actualLevelEvaluatorOnly": frame.level, "t7AcceptableEvaluatorOnly": frame.t7_acceptable,
                       "episodeId": frame.episode_id, "family": frame.family,
                       "frameIndex": frame.index, "image": frame.image_relative,
                       "_imagePath": str(frame.image)})
    return result


def render_sheet(selections: dict[int, list[dict[str, Any]]], output: Path, blinded: bool) -> None:
    tile_w, tile_h, header_h = 240, 205, 42
    columns = max(len(x) for x in selections.values())
    canvas = Image.new("RGB", (columns * tile_w, len(selections) * (tile_h + header_h)), "#f8fafc")
    draw, font = ImageDraw.Draw(canvas), ImageFont.load_default()
    for row, (prototype, samples) in enumerate(selections.items()):
        y = row * (tile_h + header_h)
        draw.rectangle((0, y, canvas.width - 1, y + header_h - 1), fill="#dbeafe")
        title = f"P{prototype} - BLINDED APPEARANCE" if blinded else f"P{prototype} - EVALUATION METADATA (POST-HOC)"
        draw.text((8, y + 8), title, fill="#0f172a", font=font)
        for col, sample in enumerate(samples):
            x = col * tile_w
            with Image.open(sample["_imagePath"]) as source:
                image = source.convert("RGB")
                image.thumbnail((tile_w - 8, 142), Image.Resampling.LANCZOS)
                canvas.paste(image, (x + 4, y + header_h))
            if blinded:
                lines = [sample["sampleCode"]]
            else:
                short_episode = sample["episodeId"].split("_")[-2]
                lines = [f"{sample['sampleCode']} {sample['assignmentStatus']} {sample['actualLevelEvaluatorOnly']}",
                         f"d={sample['distance']:.3f} r={sample['radiusFraction']:.2f} margin={sample['margin']:.3f}",
                         f"{sample['split']} {short_episode} frame={sample['frameIndex']}"]
            caption_y = y + header_h + 146
            for line, value in enumerate(lines):
                draw.text((x + 4, caption_y + line * 15), value, fill="#111827", font=font)
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)


def quality_report(model: dict[str, Any], frames: Sequence, assignments: Sequence[dict[str, Any]],
                   distances: np.ndarray) -> list[dict[str, Any]]:
    result = []
    for prototype, metadata in enumerate(model["prototypes"]):
        nearest = [i for i, x in enumerate(assignments) if int(x["prototype"]) == prototype]
        matched = [i for i in nearest if assignments[i]["status"] == "MATCHED"]
        by_split = {}
        for split in ("train", "dev", "test"):
            ids = [i for i in nearest if frames[i].split == split]
            selected = [i for i in ids if assignments[i]["status"] == "MATCHED"]
            levels = Counter(frames[i].level for i in selected)
            by_split[split] = {"nearest": len(ids), "matched": len(selected),
                               "ambiguous": sum(assignments[i]["status"] == "AMBIGUOUS" for i in ids),
                               "unmatched": sum(assignments[i]["status"] == "UNMATCHED" for i in ids),
                               "matchedFamilies": len({frames[i].family for i in selected}),
                               "matchedLevelCounts": dict(levels), "matchedLevelEntropy": entropy(levels)}
        values = [float(distances[i, prototype]) for i in matched]
        levels = Counter(frames[i].level for i in matched)
        result.append({"prototypeId": prototype, "candidateAnchor": metadata["candidateAnchor"],
                       "matchRadius": metadata["matchRadius"], "trainingClusterObservations": metadata["trainObservations"],
                       "trainingClusterFamilySupport": metadata["trainFamilySupport"],
                       "trainingPosthocLevelDistribution": metadata["levelDistribution"],
                       "trainingLevelEntropy": metadata["levelEntropy"], "allSplitNearest": len(nearest),
                       "allSplitMatched": len(matched), "allSplitMatchedLevelCounts": dict(levels),
                       "crossLevelUse": sum(count > 0 for count in levels.values()) > 1,
                       "matchedDistance": {"mean": float(np.mean(values)) if values else None,
                                           "std": float(np.std(values)) if values else None,
                                           "p95": float(np.quantile(values, .95)) if values else None,
                                           "max": max(values) if values else None},
                       "bySplit": by_split,
                       "automatedInterpretation": INTERPRETATIONS.get(prototype, "UNRESOLVED: not prioritized in F02."),
                       "anatomyReviewState": "HUMAN_REVIEW_PENDING"})
    return result


def p5_explanation(items: list[dict[str, Any]]) -> dict[str, Any]:
    p5 = next(x for x in items if x["prototypeId"] == 5)
    dev, test = p5["bySplit"]["dev"], p5["bySplit"]["test"]
    if dev["nearest"] == 0:
        conclusion = ("Development coverage gap: no dev observation selected P5 even as its nearest prototype. "
                      "Therefore absence is not caused by P5 radius rejection or ambiguity within dev; there were no P5-nearest dev candidates. "
                      f"Former test has {test['nearest']} P5-nearest observations ({test['matched']} matched, "
                      f"{test['ambiguous']} ambiguous), showing family/split-specific appearance coverage.")
        cause = "NO_DEV_NEAREST_CANDIDATES_FAMILY_COVERAGE_GAP"
    else:
        conclusion, cause = "Development has P5-nearest observations; inspect matched/ambiguous/unmatched counts.", "MIXED"
    return {"supportedCause": cause, "conclusion": conclusion, "counts": p5["bySplit"],
            "thresholdChanged": False}


def markdown(report: dict[str, Any]) -> str:
    p5 = report["p5Investigation"]
    return "\n".join([
        "# U05-F02 prototype anatomy and observability review",
        "", "Result: **EVIDENCE READY**; anatomical claims remain **HUMAN_REVIEW_PENDING**.", "",
        "## What the images support", "",
        "P0/P3/P6/P7/P14 show repeatable renderer appearance motifs, but mostly the same superficial speckle wedge and bright curved lower reflector. T6/T7 overlap and single-anatomy data do not support naming these as rib, transverse process, lamina, or a vertebral identity.", "",
        f"P5: {p5['conclusion']}", "",
        "## Review order", "",
        "1. Open View A and record similarity/visible-structure judgments without level labels.",
        "2. Open View B and compare evaluator levels, split/family, distance, and status.",
        "3. Mark candidate structures only as uncertain unless a qualified reviewer confirms them.",
        "4. Check whether near-radius and ambiguous examples differ anatomically or only by renderer texture/window.", "",
        "## F03 recommendation (not implemented)", "",
        "1. E — hybrid direct-image + optional landmark memory. Falsify if a new locked-family test fails to improve precise handoff or reduces direct-image level accuracy.",
        "2. B — repair prototype clustering/assignment. Falsify if cross-level entropy/coverage improves but T7 recall and handoff do not.",
        "3. C — add landmark-specific supervised signals. Falsify if clinician-reviewed landmark labels are not reproducible or add no locked-test value.",
        "4. D — repair temporal/action use after visual evidence improves. Falsify if no-action ablation remains equal/better on a new locked test.",
        "5. A — broad representation replacement is lower priority because the preserved direct head is already strong. Promote only if representation ablation improves independent-family calibration and T7 discrimination.", "",
        "No retraining, threshold change, controller edit, or deployment was performed.", "",
    ])


def atomic_write(path: Path, payload: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(payload, encoding="utf-8")
    temporary.replace(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="U05-F02 evidence preparation; no training")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--checked-out-commit", default="UNKNOWN")
    parser.add_argument("--verification-command", default="NOT_RECORDED")
    parser.add_argument("--verification-status", default="NOT_RUN")
    args = parser.parse_args()
    root = project_root()
    checkpoint_path = (root / args.checkpoint).resolve() if not args.checkpoint.is_absolute() else args.checkpoint
    model_bytes = checkpoint_path.read_bytes()
    model = json.loads(model_bytes)
    frames = [x for x in load_active2d_frames(args.dataset_run_id, root) if x.contact_valid and x.workspace_valid]
    features = feature_matrix(frames)
    embeddings = project_features(model, features)
    centers = np.asarray([x["center"] for x in model["prototypes"]], np.float32)
    distances = 1.0 - embeddings @ centers.T
    assignments = [assign_visual_landmark(model, x) for x in embeddings]
    selections = {p: select_examples(p, frames, assignments, distances, float(model["prototypes"][p]["matchRadius"]))
                  for p in RELEVANT}
    quality = quality_report(model, frames, assignments, distances)
    output_dir = root / args.output_dir
    blind_path = output_dir / "u05_f02_prototypes_view_a_blinded.png"
    metadata_path = output_dir / "u05_f02_prototypes_view_b_metadata.png"
    render_sheet(selections, blind_path, True)
    render_sheet(selections, metadata_path, False)
    status = Counter(x["status"] for x in assignments)
    u05_report = json.loads((root / "Artifacts/ThoracicUSCoach/active2d/evaluation/u05_visual_topology_v1_report.json").read_text(encoding="utf-8"))
    report = {"schemaVersion": 1, "taskId": "U05-F02", "result": "EVIDENCE READY",
              "anatomicalValidation": "HUMAN_REVIEW_PENDING", "checkedOutCommit": args.checked_out_commit,
              "datasetRunId": args.dataset_run_id, "operation": "review/evidence only; no training/tuning/deployment",
              "frozenCheckpointSha256BeforeAndAfter": hashlib.sha256(model_bytes).hexdigest(),
              "assignmentCoverage": {"frames": len(frames), "statusCounts": dict(status),
                                     "matchedFraction": status["MATCHED"] / len(frames)},
              "prototypeQuality": quality, "p5Investigation": p5_explanation(quality),
              "relevantPrototypeEvidence": {str(p): [{k: v for k, v in sample.items() if k != "_imagePath"}
                                                       for sample in samples] for p, samples in selections.items()},
              "blindedManifest": {str(p): [blinded_sample(x) for x in samples] for p, samples in selections.items()},
              "visualAssessment": {str(p): {"interpretation": INTERPRETATIONS[p],
                                             "state": "HUMAN_REVIEW_PENDING"} for p in RELEVANT},
              "physicalRevisitEvaluation": u05_report["selectedDevelopmentMetrics"]["visualLoopClosure"],
              "t7DiscriminationFinding": "Relevant prototypes span multiple levels and appearance motifs are dominated by shared renderer geometry; prototype-only former-test T7 recall is 31.91% and full U06 recall is 11.70%, while direct-image T7 recall is 100%.",
              "cannotConclude": ["No prototype is a verified anatomical landmark or vertebral identity.",
                                  "Low training entropy alone does not establish a stable anchor.",
                                  "Single simulated anatomy does not establish patient generalization.",
                                  "Visual loop closure does not establish correct absolute level."],
              "humanReviewChecklist": ["Review View A first without opening metadata.",
                "For each sample mark bone shadow, rib-like arc, transverse-process-like contour, lamina/spinous-process-like contour, pleural line, acoustic window, or generic texture.",
                "Rate within-prototype similarity and whether near-boundary samples preserve the same structure.",
                "Then open View B and note level mixing and family/split concentration.",
                "Leave identity UNCERTAIN unless a qualified reviewer can reproduce the label."],
              "f03Recommendation": [
                {"rank": 1, "option": "E", "focus": "hybrid direct-image and landmark memory",
                 "falsification": "new locked-family test does not improve precise handoff or harms direct-image level accuracy"},
                {"rank": 2, "option": "B", "focus": "prototype clustering/assignment",
                 "falsification": "better coverage/entropy does not improve locked-test T7 recall and handoff"},
                {"rank": 3, "option": "C", "focus": "landmark-specific training signals",
                 "falsification": "clinician landmark labels are not reproducible or add no locked-test value"},
                {"rank": 4, "option": "D", "focus": "temporal/action model repair",
                 "falsification": "no-action ablation remains equal or better on a new locked test"},
                {"rank": 5, "option": "A", "focus": "new image representation",
                 "falsification": "representation ablation does not beat preserved direct features on independent-family calibration/T7 discrimination"}],
              "evidencePaths": {"viewA": str(args.output_dir / blind_path.name).replace("\\", "/"),
                                "viewB": str(args.output_dir / metadata_path.name).replace("\\", "/"),
                                "json": str(args.output_dir / "u05_f02_prototype_review_v1.json").replace("\\", "/"),
                                "summary": str(args.output_dir / "u05_f02_prototype_review_v1.md").replace("\\", "/")},
              "verification": [{"command": args.verification_command, "result": args.verification_status}]}
    atomic_write(output_dir / "u05_f02_prototype_review_v1.json", json.dumps(report, indent=2))
    atomic_write(output_dir / "u05_f02_prototype_review_v1.md", markdown(report))
    if hashlib.sha256(checkpoint_path.read_bytes()).hexdigest() != hashlib.sha256(model_bytes).hexdigest():
        raise AssertionError("Frozen U05 checkpoint changed")
    print(json.dumps({"result": report["result"], "assignmentCoverage": report["assignmentCoverage"],
                      "p5": report["p5Investigation"], "evidencePaths": report["evidencePaths"]}, indent=2))


if __name__ == "__main__":
    main()
