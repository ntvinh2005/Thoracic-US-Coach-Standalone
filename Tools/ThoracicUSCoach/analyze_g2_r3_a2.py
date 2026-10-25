from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

import numpy as np
from PIL import Image, ImageDraw


FLOOR = 0.5912307500839233
WEIGHTS = {
    "anatomyRelationship": 0.25,
    "pleuraVisibility": 0.23,
    "targetRegion": 0.20,
    "acousticWindow": 0.17,
    "needleGeometry": 0.15,
    "shadowPenalty": -0.18,
    "contactPenalty": -0.50,
}
PRIVILEGED = {"anatomyRelationship", "targetRegion", "needleGeometry", "contactPenalty"}
PAIR_COMPONENTS = tuple(WEIGHTS)


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save_json(path: Path, payload: Any) -> None:
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def resolve_project(repo: Path, relative: str) -> Path:
    return repo / str(relative).replace("\\", "/")


def resolve_source(source_repo: Path, relative: str) -> Path:
    normalized = str(relative).replace("\\", "/")
    marker = "Artifacts/ThoracicUSCoach/"
    return source_repo / "Artifacts" / "ThoracicUSCoach" / normalized.split(marker, 1)[1]


def pixels(path: Path, size: int | None) -> np.ndarray:
    with Image.open(path) as image:
        gray = image.convert("L")
        if size is not None:
            gray = gray.resize((size, size), Image.Resampling.BILINEAR)
        return np.asarray(gray, dtype=np.float32) / 255.0


def rms(left: np.ndarray, right: np.ndarray) -> float:
    return float(np.sqrt(np.mean((left - right) ** 2)))


def summary(values: Iterable[float]) -> dict[str, float | int | None]:
    data = np.asarray(list(values), dtype=np.float64)
    if not len(data):
        return {"count": 0, "min": None, "median": None, "mean": None, "q95": None, "max": None}
    return {"count": int(len(data)), "min": float(data.min()), "median": float(np.median(data)), "mean": float(data.mean()), "q95": float(np.quantile(data, 0.95)), "max": float(data.max())}


def metrics(labels: list[bool], predictions: list[bool]) -> dict[str, Any]:
    y = np.asarray(labels, dtype=bool)
    p = np.asarray(predictions, dtype=bool)
    tp, fp, fn, tn = int(np.sum(y & p)), int(np.sum(~y & p)), int(np.sum(y & ~p)), int(np.sum(~y & ~p))
    return {"count": len(y), "positiveSupport": int(y.sum()), "truePositive": tp, "falsePositive": fp, "falseNegative": fn, "trueNegative": tn, "recall": tp / (tp + fn) if tp + fn else None, "precision": tp / (tp + fp) if tp + fp else None}


def derive_verdict(inputs: dict[str, Any]) -> tuple[str, str]:
    """Apply the predeclared manifest rules; measurements are supplied by the caller."""
    if inputs["validCases"] < 12 or not inputs["mandatoryValid"] or inputs["measurementConsistencyFailed"]:
        return "INCONCLUSIVE", "Pose coverage or measurement consistency prerequisite failed."
    if inputs["separatedFraction"] >= 0.75 and inputs["frozenFreshRecall"] >= 0.80 and inputs["frozenFreshFalsePositive"] == 0:
        return "GO_FOR_IMAGE_ONLY_RESEARCH", "Declared visual-separation and frozen-baseline criteria both passed."
    if inputs["partialEvidencePairs"] >= 2:
        return "PARTIALLY_OBSERVABLE_WITH_EVIDENCE", "At least two declared ambiguous opposite-label pairs have meaningful privileged-component differences and missed positives."
    if inputs["separatedFraction"] < 0.75 and inputs["causalHistoryAPGain"] >= 0.03 and inputs["causalResolvedFraction"] >= 0.50:
        return "NEEDS_MULTIVIEW", "Single-frame criterion failed while genuine action history showed the declared additional evidence."
    if inputs["ambiguousFraction"] >= 0.75 and inputs["privilegedDifferenceFractionMedian"] >= 0.80 and inputs["causalHistoryAPGain"] < 0.03:
        return "NO_GO_CURRENT_TARGET", "Declared ambiguity, privileged-explanation and absent-history-gain criteria passed."
    return "INCONCLUSIVE", "Measurements are valid, but no predeclared stronger verdict rule is fully satisfied."


def score_reconstruction(score: dict[str, Any]) -> float:
    return float(np.clip(sum(WEIGHTS[key] * float(score[key]) for key in PAIR_COMPONENTS), 0.0, 1.0))


def observation_paths(repo: Path, case: dict[str, Any]) -> list[Path]:
    return [resolve_project(repo, item["image"]) for item in case["observations"]]


def component_pair(left: dict[str, Any], right: dict[str, Any]) -> dict[str, Any]:
    left_mean = {key: float(np.mean([obs["score"][key] for obs in left["observations"]])) for key in PAIR_COMPONENTS}
    right_mean = {key: float(np.mean([obs["score"][key] for obs in right["observations"]])) for key in PAIR_COMPONENTS}
    weighted = {key: WEIGHTS[key] * (left_mean[key] - right_mean[key]) for key in PAIR_COMPONENTS}
    absolute_total = sum(abs(value) for value in weighted.values())
    privileged_absolute = sum(abs(weighted[key]) for key in PRIVILEGED)
    return {
        "positiveMeanComponents": left_mean,
        "negativeMeanComponents": right_mean,
        "weightedContributionDifference": weighted,
        "absolutePrivilegedDifference": privileged_absolute,
        "absoluteAllComponentDifference": absolute_total,
        "privilegedDifferenceFraction": privileged_absolute / absolute_total if absolute_total else 0.0,
        "meanTotalDifference": float(np.mean([obs["score"]["total"] for obs in left["observations"]]) - np.mean([obs["score"]["total"] for obs in right["observations"]])),
    }


def create_montage(repo: Path, source_repo: Path, pairs: list[dict[str, Any]], cases: dict[str, dict[str, Any]], output: Path, annotated: bool) -> None:
    cell_width, row_height = 520, 250
    canvas = Image.new("RGB", (cell_width * 2, row_height * len(pairs)), "white")
    draw = ImageDraw.Draw(canvas)
    for row, pair in enumerate(pairs):
        for column, case_id in enumerate((pair["positiveCase"], pair["negativeCase"])):
            case = cases[case_id]
            path = resolve_project(repo, case["observations"][0]["image"])
            with Image.open(path) as image:
                image = image.convert("RGB")
                image.thumbnail((480, 205), Image.Resampling.LANCZOS)
                canvas.paste(image, (column * cell_width + (cell_width - image.width) // 2, row * row_height + 36))
            text = f"Pair {row + 1}{'A' if column == 0 else 'B'}"
            if annotated:
                obs = case["observations"][0]
                text += f" | {case['family']} f={case['sourceFrameIndex']} label={obs['targetAcceptable']} score={obs['score']['total']:.6f}"
            draw.text((column * cell_width + 8, row * row_height + 8), text, fill="black")
    canvas.save(output)


def main() -> None:
    parser = argparse.ArgumentParser(description="Analyze completed G2-R3-A2 exact-pose audit")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--source-repo", type=Path, default=Path(r"D:\CSSALT\Thoracic-US-Coach-Standalone"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    source_repo = args.source_repo.resolve()
    output = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r3_a2_20261025"
    manifest_path = repo / "Tools" / "ThoracicUSCoach" / "experiments" / "g2_r3_a2_exact_pose_v1.json"
    unity_path = output / "unity_capture" / "g2_r3_a2_unity_report.json"
    manifest = load_json(manifest_path)
    unity = load_json(unity_path)
    cases = {case["caseId"]: case for case in unity["cases"]}
    declared = {case["caseId"]: case for case in manifest["cases"]}

    image_hash_errors = []
    reconstruction_errors = []
    label_floor_errors = []
    repeat_rms128: list[float] = []
    repeat_rms_native: list[float] = []
    repeat_score_delta: list[float] = []
    per_case_repeat = []
    pose_errors, rotation_errors, contact_errors, depth_errors = [], [], [], []
    original_fresh_rms128, original_fresh_rms_native = [], []
    original_fresh_score_delta = []
    cases_with_repeat_label_flip = 0
    original_to_fresh_label_changes = 0
    for case_id, case in cases.items():
        source_image = resolve_source(source_repo, case["sourceImage"])
        if sha256(source_image) != case["sourceImageSha256"]:
            image_hash_errors.append(f"source:{case_id}")
        paths = observation_paths(repo, case)
        for path, observation in zip(paths, case["observations"]):
            if sha256(path) != observation["imageSha256"]:
                image_hash_errors.append(f"fresh:{case_id}:{observation['repeatIndex']}")
            reconstructed = score_reconstruction(observation["score"])
            reconstruction_errors.append(abs(reconstructed - float(observation["score"]["total"])))
            if (float(observation["score"]["total"]) >= FLOOR) != bool(observation["targetAcceptable"]):
                label_floor_errors.append(f"{case_id}:{observation['repeatIndex']}")
            pose_errors.append(float(observation["positionErrorMm"]))
            rotation_errors.append(float(observation["rotationErrorDeg"]))
            contact_errors.append(float(observation["contactPositionErrorMm"]))
            depth_errors.append(float(observation["signedDepthErrorMm"]))
            original_fresh_score_delta.append(float(observation["scoreDeltaFromOriginal"]))
            original_fresh_rms128.append(rms(pixels(source_image, 128), pixels(path, 128)))
            original_fresh_rms_native.append(rms(pixels(source_image, None), pixels(path, None)))
        labels = {bool(item["targetAcceptable"]) for item in case["observations"]}
        cases_with_repeat_label_flip += int(len(labels) > 1)
        original_to_fresh_label_changes += int(bool(case["originalAcceptable"]) != bool(case["observations"][0]["targetAcceptable"]))
        local_rms128, local_rms_native, local_score = [], [], []
        for left in range(len(paths)):
            for right in range(left + 1, len(paths)):
                local_rms128.append(rms(pixels(paths[left], 128), pixels(paths[right], 128)))
                local_rms_native.append(rms(pixels(paths[left], None), pixels(paths[right], None)))
                local_score.append(abs(float(case["observations"][left]["score"]["total"]) - float(case["observations"][right]["score"]["total"])))
        repeat_rms128.extend(local_rms128)
        repeat_rms_native.extend(local_rms_native)
        repeat_score_delta.extend(local_score)
        per_case_repeat.append({"caseId": case_id, "valid": case["restorationValid"], "observations": len(paths), "imageRms128": summary(local_rms128), "imageRmsNative": summary(local_rms_native), "absoluteScoreDelta": summary(local_score), "repeatLabelFlip": len(labels) > 1})

    repeat_q95 = float(np.quantile(repeat_rms128, 0.95))
    pair_ids: list[tuple[str, str, str]] = [
        ("case_00_train_family_0090_f0100", "case_01_train_family_0090_f0101", "mandatory same-family matched pair"),
        ("case_00_train_family_0090_f0100", "case_02_train_family_0090_f0099", "causal predecessor comparison"),
        ("case_00_train_family_0090_f0100", "case_03_train_family_0090_f0102", "causal successor comparison"),
        ("case_04_train_family_0080_f0069", "case_07_dev_family_0058_f0105", "independent near-boundary control"),
        ("case_05_train_family_0062_f0102", "case_08_train_family_0045_f0100", "independent near-boundary control"),
        ("case_06_dev_family_0073_f0105", "case_09_train_family_0080_f0052", "independent near-boundary control"),
        ("case_10_train_family_0086_f0104", "case_11_train_family_0010_f0039", "clear controls"),
        ("case_00_train_family_0090_f0100", "case_12_train_family_0066_f0100", "cross-family hard negative"),
        ("case_00_train_family_0090_f0100", "case_13_train_family_0077_f0023", "cross-family hard negative"),
        ("case_00_train_family_0090_f0100", "case_14_test_family_0079_f0098", "cross-family hard negative; previously consumed calibration family"),
        ("case_00_train_family_0090_f0100", "case_15_dev_family_0058_f0074", "cross-family hard negative"),
    ]
    pairs = []
    for positive_id, negative_id, reason in pair_ids:
        positive, negative = cases[positive_id], cases[negative_id]
        source_positive = resolve_source(source_repo, positive["sourceImage"])
        source_negative = resolve_source(source_repo, negative["sourceImage"])
        fresh_positive = resolve_project(repo, positive["observations"][0]["image"])
        fresh_negative = resolve_project(repo, negative["observations"][0]["image"])
        fresh_rms128 = rms(pixels(fresh_positive, 128), pixels(fresh_negative, 128))
        payload = {
            "positiveCase": positive_id, "negativeCase": negative_id, "reason": reason,
            "sourceImageRms128": rms(pixels(source_positive, 128), pixels(source_negative, 128)),
            "sourceImageRmsNative": rms(pixels(source_positive, None), pixels(source_negative, None)),
            "freshImageRms128": fresh_rms128,
            "freshImageRmsNative": rms(pixels(fresh_positive, None), pixels(fresh_negative, None)),
            "repeatNoiseQ95Rms128": repeat_q95,
            "freshLabelsOpposite": bool(positive["observations"][0]["targetAcceptable"]) != bool(negative["observations"][0]["targetAcceptable"]),
            "separationRatioToRepeatQ95": fresh_rms128 / repeat_q95 if repeat_q95 else math.inf,
            "visuallySeparatedByDeclaredRule": fresh_rms128 > 3.0 * repeat_q95,
            **component_pair(positive, negative),
        }
        pairs.append(payload)

    sys.path.insert(0, str(repo / "Tools" / "ThoracicUSCoach"))
    from active2d_stop import request_stop_features, stop_probabilities
    checkpoint_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "checkpoints" / "phase2_stop_live_spatial_logistic_v1.json"
    checkpoint = load_json(checkpoint_path)
    threshold = 0.93
    source_probs, fresh_probs, labels = [], [], []
    model_cases = []
    for case_id in declared:
        case, declaration = cases[case_id], declared[case_id]
        source_image = resolve_source(source_repo, declaration["sourceImage"])
        source_stream = load_json(source_image.parent / "stream_manifest.json")
        source_item = next(item for item in source_stream["frames"] if int(item["index"]) == int(declaration["sourceFrameIndex"]))
        action = str(source_item.get("issuedAction", "RESET"))
        fresh_image = resolve_project(repo, case["observations"][0]["image"])
        source_probability = float(stop_probabilities(checkpoint, request_stop_features(source_image, None, action, "spatial-v2"))[0])
        fresh_probability = float(stop_probabilities(checkpoint, request_stop_features(fresh_image, None, action, "spatial-v2"))[0])
        source_probs.append(source_probability)
        fresh_probs.append(fresh_probability)
        labels.append(bool(case["observations"][0]["targetAcceptable"]))
        model_cases.append({"caseId": case_id, "issuedAction": action, "freshLabel": labels[-1], "sourceProbability": source_probability, "freshProbability": fresh_probability, "sourcePrediction": source_probability >= threshold, "freshPrediction": fresh_probability >= threshold})

    family0090 = sorted([item for item in model_cases if "train_family_0090" in item["caseId"]], key=lambda item: int(item["caseId"].rsplit("f", 1)[1]))
    source_family_dir = resolve_source(source_repo, declared[family0090[0]["caseId"]]["sourceImage"]).parent
    source_stream = load_json(source_family_dir / "stream_manifest.json")
    stream_items = {int(item["index"]): item for item in source_stream["frames"]}
    causal = []
    ordered_ids = ["case_02_train_family_0090_f0099", "case_00_train_family_0090_f0100", "case_01_train_family_0090_f0101", "case_03_train_family_0090_f0102"]
    for previous_id, current_id in zip(ordered_ids, ordered_ids[1:]):
        previous, current = cases[previous_id], cases[current_id]
        current_item = stream_items[int(current["sourceFrameIndex"])]
        source_previous = resolve_source(source_repo, previous["sourceImage"])
        source_current = resolve_source(source_repo, current["sourceImage"])
        fresh_previous = resolve_project(repo, previous["observations"][0]["image"])
        fresh_current = resolve_project(repo, current["observations"][0]["image"])
        source_delta = rms(pixels(source_previous, 128), pixels(source_current, 128))
        fresh_delta = rms(pixels(fresh_previous, 128), pixels(fresh_current, 128))
        causal.append({"previousIndex": previous["sourceFrameIndex"], "currentIndex": current["sourceFrameIndex"], "issuedAction": current_item.get("issuedAction", "RESET"), "actionAccepted": bool(current_item.get("actionAccepted", True)), "actionCompleted": bool(current_item.get("actionCompleted", current_item.get("actionAccepted", True))), "previousLabel": previous["observations"][0]["targetAcceptable"], "currentLabel": current["observations"][0]["targetAcceptable"], "sourceImageRms128": source_delta, "freshRestoredImageRms128": fresh_delta, "above3xRepeatQ95": fresh_delta > 3.0 * repeat_q95})

    g0 = load_json(repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g0_ground_truth_observability" / "g0_observability_report.json")
    r2 = load_json(repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r2_20261024" / "g2_r2_results.json")
    ap_single = float(g0["existingActive2DDevelopment"]["singleImageDiagnostic"]["averagePrecision"])
    ap_temporal = float(g0["existingActive2DDevelopment"]["pastImageDeltaDiagnostic"]["averagePrecision"])
    ap_gain = ap_temporal - ap_single
    valid_cases = sum(bool(case["restorationValid"]) for case in cases.values())
    mandatory_valid = cases["case_00_train_family_0090_f0100"]["restorationValid"] and cases["case_01_train_family_0090_f0101"]["restorationValid"]
    fresh_predictions = [value >= threshold for value in fresh_probs]
    frozen_metrics = metrics(labels, fresh_predictions)
    eligible_pairs = [pair for pair in pairs if pair["freshLabelsOpposite"]]
    label_invalidated_pairs = [pair for pair in pairs if not pair["freshLabelsOpposite"]]
    separated_fraction = sum(pair["visuallySeparatedByDeclaredRule"] for pair in eligible_pairs) / len(eligible_pairs)
    ambiguous = [pair for pair in eligible_pairs if not pair["visuallySeparatedByDeclaredRule"]]
    missed_positive_ids = {item["caseId"] for item in model_cases if item["freshLabel"] and not item["freshPrediction"]}
    partial_pairs = [pair for pair in ambiguous if pair["absolutePrivilegedDifference"] >= 0.02 and pair["positiveCase"] in missed_positive_ids]
    privileged_fraction_median = float(np.median([pair["privilegedDifferenceFraction"] for pair in pairs]))
    causal_resolved = sum(item["above3xRepeatQ95"] for item in causal) / len(causal) if causal else 0.0

    consistency_failed = bool(image_hash_errors or label_floor_errors or max(reconstruction_errors, default=0.0) > 1e-5)
    decision_inputs = {
        "validCases": valid_cases,
        "mandatoryValid": mandatory_valid,
        "measurementConsistencyFailed": consistency_failed,
        "separatedFraction": separated_fraction,
        "frozenFreshRecall": frozen_metrics["recall"] or 0.0,
        "frozenFreshFalsePositive": frozen_metrics["falsePositive"],
        "ambiguousPairs": len(ambiguous),
        "ambiguousFraction": len(ambiguous) / len(eligible_pairs),
        "partialEvidencePairs": len(partial_pairs),
        "privilegedDifferenceFractionMedian": privileged_fraction_median,
        "causalHistoryAPGain": ap_gain,
        "causalResolvedFraction": causal_resolved,
    }
    verdict, reason = derive_verdict(decision_inputs)

    create_montage(repo, source_repo, pairs, cases, output / "g2_r3_a2_pairs_blinded.png", False)
    create_montage(repo, source_repo, pairs, cases, output / "g2_r3_a2_pairs_annotated.png", True)
    report = {
        "schemaVersion": 1,
        "taskId": "G2-R3-A2",
        "verdict": verdict,
        "decisionReason": reason,
        "decisionProcedure": manifest["decisionProcedure"],
        "scope": "Confirmatory Phase 2 development diagnostic; locked test not opened; no training, retuning, deployment, evaluator change or Phase 1 change.",
        "provenance": {"manifest": str(manifest_path.relative_to(repo)).replace("\\", "/"), "manifestSha256": sha256(manifest_path), "unityReportSha256": sha256(unity_path), "sceneSha256": unity["sceneSha256"], "evaluatorSha256": unity["evaluatorSha256"], "active2DControllerSha256": unity["active2DControllerSha256"], "stopCheckpointSha256": sha256(checkpoint_path)},
        "poseRestoration": {"requestedCases": len(cases), "validCases": valid_cases, "invalidCases": len(cases) - valid_cases, "mandatory0090Valid": mandatory_valid, "observations": sum(len(case["observations"]) for case in cases.values()), "positionErrorMm": summary(pose_errors), "rotationErrorDeg": summary(rotation_errors), "contactPositionErrorMm": summary(contact_errors), "signedDepthErrorMm": summary(depth_errors)},
        "integrity": {"imageHashErrors": image_hash_errors, "scoreReconstructionAbsoluteError": summary(reconstruction_errors), "labelFloorErrors": label_floor_errors, "allFreshContactValid": all(obs["contactValid"] for case in cases.values() for obs in case["observations"]), "allFreshWorkspaceValid": all(obs["workspaceValid"] for case in cases.values() for obs in case["observations"])},
        "repeatRender": {"pairs": len(repeat_rms128), "imageRms128": summary(repeat_rms128), "imageRmsNative": summary(repeat_rms_native), "absoluteScoreDelta": summary(repeat_score_delta), "casesWithWithinRepeatLabelFlip": cases_with_repeat_label_flip, "originalToFreshFirstRenderLabelChanges": original_to_fresh_label_changes, "originalToFreshImageRms128": summary(original_fresh_rms128), "originalToFreshImageRmsNative": summary(original_fresh_rms_native), "originalToFreshScoreDelta": summary(original_fresh_score_delta), "perCase": per_case_repeat},
        "matchedPairs": {"declaredSeparationThreshold": 3.0 * repeat_q95, "predeclaredPairs": len(pairs), "freshOppositeLabelPairs": len(eligible_pairs), "pairsInvalidatedByFreshLabelChange": len(label_invalidated_pairs), "separatedPairs": sum(pair["visuallySeparatedByDeclaredRule"] for pair in eligible_pairs), "ambiguousPairs": len(ambiguous), "separatedFraction": separated_fraction, "privilegedDifferenceFractionMedian": privileged_fraction_median, "pairs": pairs},
        "frozenImageOnlyDiagnostic": {"checkpoint": str(checkpoint_path.relative_to(repo)).replace("\\", "/"), "threshold": threshold, "sourceMetrics": metrics([bool(declared[case_id]["originalAcceptable"]) for case_id in declared], [value >= threshold for value in source_probs]), "freshMetrics": frozen_metrics, "cases": model_cases},
        "causalMultiView": {"family": "train_family_0090", "sequenceIndices": [99, 100, 101, 102], "transitions": causal, "transitionsAbove3xRepeatQ95Fraction": causal_resolved, "historicalG0SingleFrameAP": ap_single, "historicalG0PastImageDeltaAP": ap_temporal, "historicalAPGain": ap_gain, "g2R2TemporalRobustAcrossSeeds": r2["robustAcrossSeeds"], "limitation": "Fresh poses were independently restored; causal ordering/action completion comes from the genuine recorded Active2D trajectory, not a newly executed sequence."},
        "decisionInputs": decision_inputs,
        "scientificJudgment": ["A privileged component's provenance or weight alone is not treated as proof of unobservability.", "Visual separation is defined relative to measured same-pose renderer variation, not by raw RMS alone.", "The selected sample is one simulator anatomy and development evidence; it cannot establish clinical or unseen-patient validity."],
    }
    save_json(output / "g2_r3_a2_analysis.json", report)
    print(json.dumps({"verdict": verdict, "reason": reason, "validCases": valid_cases, "observations": report["poseRestoration"]["observations"], "repeatRms128Q95": repeat_q95, "freshOppositeLabelPairs": len(eligible_pairs), "pairsInvalidatedByFreshLabelChange": len(label_invalidated_pairs), "separatedPairs": report["matchedPairs"]["separatedPairs"], "ambiguousPairs": len(ambiguous), "frozenFreshMetrics": frozen_metrics, "partialEvidencePairs": len(partial_pairs), "causalAPGain": ap_gain}, indent=2))


if __name__ == "__main__":
    main()
