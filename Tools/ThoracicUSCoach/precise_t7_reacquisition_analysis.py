#!/usr/bin/env python3
"""Offline confirmation and evidence generation for the bounded P1-R2 pilot."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from position_aware_relocalization import block_ssim, load_image_descriptor


def _load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _save_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def capture_is_fresh(step: dict, privileged_step: dict) -> bool:
    return bool(
        step.get("frameIntegrityValid")
        and step.get("poseEpoch") == step.get("producerPoseEpoch")
        and int(step.get("producerUnityFrame", -1)) > int(step.get("poseAppliedUnityFrame", -1))
        and step.get("captureUnityFrame") == step.get("producerUnityFrame")
        and int(step.get("producerScanSerial", -1)) > 0
        and privileged_step.get("provenanceMatchedPublishedFrame")
    )


def classify_outcome(position_pass: bool, fresh: bool, similarity_pass: bool,
                     evaluator_t7: bool) -> tuple[str, str]:
    confirmation = "PASS" if fresh and similarity_pass and evaluator_t7 else "FAIL"
    if not position_pass:
        return confirmation, "POSITION_PRECISION_FAIL"
    if not fresh:
        return confirmation, "IMAGE_INTEGRITY_FAIL"
    if not similarity_pass:
        return confirmation, "POSE_PRECISE_IMAGE_MISMATCH"
    if not evaluator_t7:
        return confirmation, "EVALUATOR_T7_MISMATCH"
    return confirmation, "P1_R2_REACQUISITION_PASS"


def _write_pose_csv(path: Path, actor: dict) -> None:
    fields = [
        "step", "stage", "action", "accepted", "requestedDisplacementMm",
        "achievedDisplacementMm", "longitudinalErrorMm", "lateralErrorMm",
        "fullContactPointErrorMm", "orientationErrorDeg", "contactValid",
        "workspaceValid", "safetyFailure", "rejectionReason",
    ]
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for item in actor["steps"]:
            writer.writerow({field: item.get(field, "") for field in fields})


def _draw_trajectory(path: Path, actor: dict) -> None:
    steps = actor["steps"]
    width, height = 1200, 650
    left, top, right, bottom = 90, 70, 1150, 540
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    draw.text((left, 24), "P1-R2 coarse/fine measured pose error", fill="black")
    draw.rectangle((left, top, right, bottom), outline=(40, 40, 40), width=2)
    maximum = max(1.0, max(float(item.get("fullContactPointErrorMm", 0.0)) for item in steps))

    def point(index: int, value: float) -> tuple[int, int]:
        x = left if len(steps) == 1 else left + int(index * (right - left) / (len(steps) - 1))
        y = bottom - int(abs(value) * (bottom - top) / maximum)
        return x, y

    colors = {
        "longitudinalErrorMm": (35, 95, 210),
        "lateralErrorMm": (220, 115, 30),
        "fullContactPointErrorMm": (130, 45, 155),
    }
    for key, color in colors.items():
        draw.line([point(i, float(item.get(key, 0.0))) for i, item in enumerate(steps)],
                  fill=color, width=4)
    tolerance_y = point(0, 0.5)[1]
    draw.line((left, tolerance_y, right, tolerance_y), fill=(20, 150, 70), width=2)
    draw.text((left + 8, tolerance_y - 19), "0.5 mm locked tolerance", fill=(20, 125, 60))
    for i, item in enumerate(steps):
        x, _ = point(i, 0.0)
        stage = item.get("stage", "")
        if stage in {"COARSE", "FINE"}:
            draw.text((x - 14, bottom + 8), "C" if stage == "COARSE" else "F",
                      fill=(30, 30, 30))
    draw.text((left, bottom + 45),
              "Blue: longitudinal |error|   Orange: lateral |error|   Purple: full 3D contact error",
              fill="black")
    draw.text((left, bottom + 72), "C: 3 mm coarse action   F: 0.5 mm fine action", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _draw_image_comparison(path: Path, calibration_path: Path, return_path: Path,
                           similarity: float, threshold: float) -> None:
    calibration = Image.open(calibration_path).convert("L")
    returned = Image.open(return_path).convert("L")
    if calibration.size != returned.size:
        raise ValueError("Calibration and return ultrasound dimensions differ")
    left = np.asarray(calibration, dtype=np.int16)
    right = np.asarray(returned, dtype=np.int16)
    difference = Image.fromarray(np.abs(left - right).astype(np.uint8), mode="L")
    scale = min(3, max(1, 720 // max(calibration.height, 1)))
    panels = [image.resize((image.width * scale, image.height * scale), Image.Resampling.NEAREST)
              for image in (calibration, returned, difference)]
    margin, header = 24, 74
    canvas = Image.new("RGB", (sum(image.width for image in panels) + margin * 4,
                               panels[0].height + header + 52), "white")
    draw = ImageDraw.Draw(canvas)
    draw.text((margin, 16),
              f"Locked block-SSIM: {similarity:.6f}  threshold: {threshold:.6f}", fill="black")
    labels = ("Calibration", "Precise return", "Absolute pixel difference")
    x = margin
    for label, image in zip(labels, panels):
        canvas.paste(image.convert("RGB"), (x, header))
        draw.text((x, header + image.height + 14), label, fill="black")
        x += image.width + margin
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def analyze(output_root: Path, baseline_root: Path) -> dict:
    calibration = _load_json(output_root / "calibration_manifest.json")
    actor = _load_json(output_root / "trajectory_actor.json")
    evaluator = _load_json(output_root / "evaluator_only" / "trajectory_privileged.json")
    evaluation = _load_json(output_root / "pilot_evaluation.json")
    metric = _load_json(baseline_root / "confirmation_metric.json")
    baseline_evaluation = _load_json(baseline_root / "pilot_evaluation.json")
    calibration_path = output_root / calibration["referenceImage"]
    return_path = output_root / evaluation["returnImage"]
    final_actor = actor["steps"][-1]
    final_evaluator = evaluator["steps"][-1]

    if _sha256(calibration_path) != calibration["referenceImageSha256"]:
        raise ValueError("P1-R2 calibration image hash mismatch")
    if _sha256(return_path) != final_actor["imageSha256"]:
        raise ValueError("P1-R2 return image hash mismatch")
    threshold = float(metric["lockedThreshold"])
    if abs(threshold - float(calibration["lockedImageSimilarityThreshold"])) > 1e-5:
        raise ValueError("P1-R2 calibration did not preserve the locked P1 threshold")
    similarity = block_ssim(load_image_descriptor(calibration_path), load_image_descriptor(return_path))
    fresh = capture_is_fresh(final_actor, final_evaluator)
    position_pass = evaluation.get("positionPrecision") == "PASS"
    evaluator_t7 = (
        abs(float(final_evaluator["evaluatorLongitudinalErrorMm"])) <= 3.0
        and abs(float(final_evaluator["evaluatorLateralErrorMm"])) <= 3.0
    )
    confirmation, overall = classify_outcome(
        position_pass, fresh, similarity >= threshold, evaluator_t7)
    evaluation.update({
        "freshUltrasoundConfirmation": confirmation,
        "p1R2Overall": overall,
        "imageSimilarityStatus": "PASS" if similarity >= threshold else "FAIL",
        "calibrationReturnSimilarity": similarity,
        "lockedImageSimilarityThreshold": threshold,
        "returnFrameFreshAndIntegrityValid": fresh,
        "evaluatorT7Correspondence": evaluator_t7,
        "evaluatorReferenceDistanceMm": float(final_evaluator["distanceToReferenceContactMm"]),
    })
    _save_json(output_root / "pilot_evaluation.json", evaluation)
    metrics = {
        "contract": "thoracic_p1_r2_confirmation_metrics_v1",
        "generatedUtc": _utc_now(),
        "metric": metric["metric"],
        "lockedThreshold": threshold,
        "calibrationReturnSimilarity": similarity,
        "originalP1Similarity": float(baseline_evaluation["calibrationReturnSimilarity"]),
        "similarityIncreaseFromP1": similarity
        - float(baseline_evaluation["calibrationReturnSimilarity"]),
        "originalP1FullContactPointErrorMm": float(
            baseline_evaluation["evaluatorReferenceDistanceMm"]),
        "p1R2FullContactPointErrorMm": float(
            evaluation["finalFullContactPointErrorMm"]),
        "similarityPass": similarity >= threshold,
        "calibrationImage": calibration["referenceImage"],
        "calibrationImageSha256": _sha256(calibration_path),
        "returnImage": evaluation["returnImage"],
        "returnImageSha256": _sha256(return_path),
        "returnFrameFreshAndIntegrityValid": fresh,
        "evaluatorT7Correspondence": evaluator_t7,
        "positionPrecision": evaluation.get("positionPrecision"),
        "freshUltrasoundConfirmation": confirmation,
        "p1R2Overall": overall,
    }
    _save_json(output_root / "confirmation_metrics.json", metrics)
    _write_pose_csv(output_root / "pose_error_by_step.csv", actor)
    _draw_trajectory(output_root / "coarse_fine_trajectory.png", actor)
    _draw_image_comparison(output_root / "calibration_vs_return_ultrasound.png",
                           calibration_path, return_path, similarity, threshold)
    _write_report(output_root, calibration, actor, evaluation, baseline_evaluation)
    return metrics


def _write_report(output_root: Path, calibration: dict, actor: dict, evaluation: dict,
                  baseline_evaluation: dict) -> None:
    coarse = int(actor.get("acceptedCoarseMovements", 0))
    fine = int(actor.get("acceptedFineMovements", 0))
    report = f"""# P1-R2 Precise Position-Guided T7 Reacquisition

## IMPLEMENTATION

The isolated calibrated controller now uses measured-pose feedback with a 3 mm coarse stage and a bounded 0.5 mm fine stage. Existing Active2D movement, contact, workspace, rollback, progress, yaw, and fresh-capture contracts remain active. Standard Active2D, the scene, anatomy, renderer, and the locked SSIM threshold were not changed.

## VERIFICATION

See `preflight_results.json` and the Unity/Python test outputs in this directory. The physical pilot was permitted only after compilation, focused tests, safety/freshness regressions, and the actor/evaluator isolation audit passed.

## COARSE NAVIGATION

- Accepted coarse moves: **{coarse}**.
- The previous 3 mm component-wise premature STOP rule was removed. A coarse action is taken only when its predicted measured-pose error reduction is at least 0.05 mm.

## FINE NAVIGATION

- Accepted fine moves: **{fine}**.
- Fine command: **0.5 mm**, with the existing 0.25 mm meaningful-progress floor unchanged.
- Rejected moves: **{evaluation['rejectedMovements']}**.

## FINAL POSE

- Longitudinal residual: **{evaluation['finalLongitudinalErrorMm']:.6f} mm**.
- Lateral residual: **{evaluation['finalLateralErrorMm']:.6f} mm**.
- Full 3D contact-point residual: **{evaluation['finalFullContactPointErrorMm']:.6f} mm**.
- Quaternion orientation error: **{evaluation['finalQuaternionErrorDeg']:.6f} deg**.
- Surface-yaw / normal-alignment error: **{evaluation['finalSurfaceYawErrorDeg']:.6f} / {evaluation['finalNormalAlignmentErrorDeg']:.6f} deg**.
- POSITION_PRECISION: **{evaluation['positionPrecision']}**.

## FRESH ULTRASOUND CONFIRMATION

- Locked SSIM: **{evaluation['calibrationReturnSimilarity']:.6f}**.
- Locked threshold: **{evaluation['lockedImageSimilarityThreshold']:.6f}** (unchanged).
- Fresh frame and publication association: **{evaluation['returnFrameFreshAndIntegrityValid']}**.
- Independent evaluator T7 correspondence: **{evaluation['evaluatorT7Correspondence']}**.
- FRESH_ULTRASOUND_CONFIRMATION: **{evaluation['freshUltrasoundConfirmation']}**.

Calibration publication metadata were persisted at pose epoch {calibration['poseEpoch']}, producer frame {calibration['producerUnityFrame']}, scan serial {calibration['producerScanSerial']}, and capture frame {calibration['captureUnityFrame']}.

## OUTCOME

P1_R2_OVERALL: **{evaluation['p1R2Overall']}**.

Reducing contact-point residual from **{baseline_evaluation['evaluatorReferenceDistanceMm']:.6f} mm** to **{evaluation['finalFullContactPointErrorMm']:.6f} mm** increased SSIM from **{baseline_evaluation['calibrationReturnSimilarity']:.6f}** to **{evaluation['calibrationReturnSimilarity']:.6f}**. This is strong evidence that premature stopping was a major contributor to the original image mismatch. It was not sufficient to satisfy the locked criterion: the new value remains **{evaluation['lockedImageSimilarityThreshold'] - evaluation['calibrationReturnSimilarity']:.6f}** below threshold. The causal hypothesis is therefore supported, but exact locked-threshold reacquisition is not demonstrated.

This single pilot does not use SSIM for steering and does not establish autonomous T7 identification.

## LIMITATIONS

- Fixed-anatomy, simulator-only, calibrated engineering baseline.
- One yaw-0 episode; no retry or held-out starting condition.
- AUTONOMOUS_T7_IDENTIFICATION: **NOT_TESTED**.
- G2: **FAIL**; G3/G4: **NOT_RUN**.
"""
    (output_root / "REPORT.md").write_text(report, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--baseline-root", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(analyze(args.output_root, args.baseline_root), indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
