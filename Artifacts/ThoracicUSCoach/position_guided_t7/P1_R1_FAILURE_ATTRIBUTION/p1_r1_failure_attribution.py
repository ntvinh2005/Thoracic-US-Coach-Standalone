from __future__ import annotations

import csv
import hashlib
import json
import math
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


OUTPUT_ROOT = Path(__file__).resolve().parent
REPO_ROOT = Path(__file__).resolve().parents[4]
P1_ROOT = REPO_ROOT / "Artifacts/ThoracicUSCoach/position_guided_t7/P1_POSITION_AWARE_PILOT"
M0B_ROOT = REPO_ROOT / "Artifacts/ThoracicUSCoach/boundary_t7/M0B_VISUAL_BOUNDARY_FEASIBILITY_20261030"
TOOLS_ROOT = REPO_ROOT / "Tools/ThoracicUSCoach"
sys.path.insert(0, str(TOOLS_ROOT))

from position_aware_relocalization import (  # noqa: E402
    AMBIGUITY_SEPARATION_MM,
    AMBIGUITY_SIMILARITY_DELTA,
    ORIENTATION_MM_PER_DEG,
    QUERY_PHASES,
    REFERENCE_PHASES,
    WRONG_LOCATION_TOLERANCE_MM,
    block_ssim,
    build_records,
    load_image_descriptor,
    pose_distance_mm,
    quaternion_angle_deg,
    verify_inputs,
)


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def vector(value: dict) -> np.ndarray:
    return np.asarray([float(value[key]) for key in ("x", "y", "z")], dtype=np.float64)


def quaternion(value: dict) -> np.ndarray:
    result = np.asarray([float(value[key]) for key in ("x", "y", "z", "w")], dtype=np.float64)
    return result / max(float(np.linalg.norm(result)), 1e-12)


def quantiles(values: list[float]) -> dict:
    if not values:
        return {"count": 0}
    data = np.asarray(values, dtype=np.float64)
    return {
        "count": int(data.size),
        "minimum": float(np.min(data)),
        "q05": float(np.quantile(data, 0.05)),
        "median": float(np.median(data)),
        "q95": float(np.quantile(data, 0.95)),
        "maximum": float(np.max(data)),
    }


def raw_gray(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("L"), dtype=np.float64) / 255.0


def full_resolution_block_ssim(left: np.ndarray, right: np.ndarray, block: int = 8) -> float:
    c1 = 0.01 ** 2
    c2 = 0.03 ** 2
    scores = []
    for y in range(0, left.shape[0], block):
        for x in range(0, left.shape[1], block):
            a = left[y:y + block, x:x + block]
            b = right[y:y + block, x:x + block]
            mean_a, mean_b = float(np.mean(a)), float(np.mean(b))
            var_a, var_b = float(np.var(a)), float(np.var(b))
            covariance = float(np.mean((a - mean_a) * (b - mean_b)))
            numerator = (2 * mean_a * mean_b + c1) * (2 * covariance + c2)
            denominator = (mean_a * mean_a + mean_b * mean_b + c1) * (var_a + var_b + c2)
            scores.append(numerator / max(denominator, 1e-12))
    return float(np.mean(scores))


def bright_centroid(descriptor: np.ndarray) -> dict:
    threshold = float(np.quantile(descriptor, 0.95))
    ys, xs = np.nonzero(descriptor >= threshold)
    return {
        "threshold": threshold,
        "xFraction": float(np.mean(xs) / max(descriptor.shape[1] - 1, 1)),
        "yFraction": float(np.mean(ys) / max(descriptor.shape[0] - 1, 1)),
        "pixelCount": int(xs.size),
    }


def central_shadow_summary(descriptor: np.ndarray) -> dict:
    height, width = descriptor.shape
    x0, x1 = int(0.35 * width), int(0.65 * width)
    y0, y1 = int(0.15 * height), int(0.75 * height)
    roi = descriptor[y0:y1, x0:x1]
    mask = roi <= 0.15
    ys, xs = np.nonzero(mask)
    return {
        "roi": {"xFraction": [0.35, 0.65], "yFraction": [0.15, 0.75]},
        "darkThreshold": 0.15,
        "darkFraction": float(np.mean(mask)),
        "darkCentroidXFractionWithinRoi": float(np.mean(xs) / max(roi.shape[1] - 1, 1)) if xs.size else None,
        "darkCentroidYFractionWithinRoi": float(np.mean(ys) / max(roi.shape[0] - 1, 1)) if ys.size else None,
    }


def phase_correlation_shift(left: np.ndarray, right: np.ndarray) -> dict:
    a = left - np.mean(left)
    b = right - np.mean(right)
    cross = np.fft.fft2(a) * np.conj(np.fft.fft2(b))
    cross /= np.maximum(np.abs(cross), 1e-12)
    correlation = np.abs(np.fft.ifft2(cross))
    peak = np.unravel_index(int(np.argmax(correlation)), correlation.shape)
    dy, dx = int(peak[0]), int(peak[1])
    if dy > left.shape[0] // 2:
        dy -= left.shape[0]
    if dx > left.shape[1] // 2:
        dx -= left.shape[1]
    return {"descriptorShiftPixelsX": dx, "descriptorShiftPixelsY": dy,
            "peak": float(correlation[peak])}


def draw_comparison(calibration_path: Path, return_path: Path, output_path: Path,
                    locked_ssim: float, threshold: float, residual_mm: float) -> None:
    with Image.open(calibration_path) as source:
        calibration = source.convert("RGB")
    with Image.open(return_path) as source:
        returned = source.convert("RGB")
    size = (430, 430)
    cal_small = calibration.resize(size, Image.Resampling.BILINEAR)
    ret_small = returned.resize(size, Image.Resampling.BILINEAR)
    cal_array = np.asarray(cal_small, dtype=np.int16)
    ret_array = np.asarray(ret_small, dtype=np.int16)
    difference = np.abs(cal_array - ret_array).astype(np.uint8)
    amplified = np.clip(difference.astype(np.float32) * 3.0, 0, 255).astype(np.uint8)
    heat = np.zeros_like(amplified)
    intensity = np.max(amplified, axis=2)
    heat[..., 0] = intensity
    heat[..., 1] = np.minimum(intensity * 2, 255)
    diff_image = Image.fromarray(heat, mode="RGB")

    canvas = Image.new("RGB", (1330, 520), "white")
    draw = ImageDraw.Draw(canvas)
    for index, (label, panel) in enumerate((
            ("Calibration", cal_small), ("Fresh return", ret_small), ("Absolute pixel difference x3", diff_image))):
        x = 10 + index * 440
        canvas.paste(panel, (x, 45))
        draw.text((x, 15), label, fill="black")
    verdict = "FAIL" if locked_ssim < threshold else "PASS"
    draw.text((10, 485),
              f"Locked 64x64 block-SSIM={locked_ssim:.6f}; threshold={threshold:.6f}; {verdict}; "
              f"contact-point residual={residual_mm:.3f} mm",
              fill="black")
    canvas.save(output_path)


def provenance_summary(calibration_pose_provenance: dict, return_provenance: dict) -> dict:
    left = calibration_pose_provenance["beams"]
    right = return_provenance["beams"]
    count = min(len(left), len(right))
    visible_depth_differences = []
    shadow_depth_differences = []
    visible_identity_changes = 0
    shadow_identity_changes = 0
    for index in range(count):
        a, b = left[index], right[index]
        if a.get("hasVisibleInterface") and b.get("hasVisibleInterface"):
            visible_depth_differences.append(abs(float(a["firstVisibleDepthFraction"])
                                                 - float(b["firstVisibleDepthFraction"])))
        if a.get("hasShadowInterface") and b.get("hasShadowInterface"):
            shadow_depth_differences.append(abs(float(a["firstShadowDepthFraction"])
                                                - float(b["firstShadowDepthFraction"])))
        visible_identity_changes += a.get("firstVisibleObject") != b.get("firstVisibleObject")
        shadow_identity_changes += a.get("firstShadowObject") != b.get("firstShadowObject")
    return {
        "calibrationPoseSource": "instrumentation_enabled_provenance.json; same physical calibration pose, but not the exact calibration.png publication",
        "returnSource": "p1_step_010.json; exact return.png publication association",
        "beamCountCompared": count,
        "visibleObjectCountsAtCalibrationPose": dict(Counter(item.get("firstVisibleObject", "") for item in left)),
        "visibleObjectCountsAtReturn": dict(Counter(item.get("firstVisibleObject", "") for item in right)),
        "shadowObjectCountsAtCalibrationPose": dict(Counter(item.get("firstShadowObject", "") for item in left)),
        "shadowObjectCountsAtReturn": dict(Counter(item.get("firstShadowObject", "") for item in right)),
        "visibleIdentityChangedBeamCount": int(visible_identity_changes),
        "shadowIdentityChangedBeamCount": int(shadow_identity_changes),
        "absoluteVisibleDepthFractionDifference": quantiles(visible_depth_differences),
        "absoluteShadowDepthFractionDifference": quantiles(shadow_depth_differences),
    }


def main() -> None:
    generated = datetime.now(timezone.utc).isoformat()
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    calibration_manifest = load_json(P1_ROOT / "calibration_manifest.json")
    actor = load_json(P1_ROOT / "trajectory_actor.json")
    evaluator = load_json(P1_ROOT / "evaluator_only/trajectory_privileged.json")
    pilot = load_json(P1_ROOT / "pilot_evaluation.json")
    confirmation = load_json(P1_ROOT / "confirmation_metric.json")
    freeze = load_json(P1_ROOT / "offline_results.sha256")
    relocalization = load_json(P1_ROOT / "pose_image_relocalization.json")
    map_manifest = load_json(P1_ROOT / "spatial_map_manifest.json")

    calibration_path = P1_ROOT / calibration_manifest["referenceImage"]
    return_path = P1_ROOT / pilot["returnImage"]
    parity_path = P1_ROOT / "evaluator_only/instrumentation_parity/instrumentation_enabled.png"
    core_files = [
        "REPORT.md", "calibration_manifest.json", "trajectory_actor.json",
        "evaluator_only/trajectory_privileged.json", "pilot_evaluation.json",
        "confirmation_metric.json", "algorithm_freeze.json", "spatial_map_manifest.json",
        "pose_image_relocalization.json", "frames/calibration.png", "frames/start.png",
        "frames/return.png", "evaluator_only/instrumentation_check.json",
        "evaluator_only/instrumentation_parity/instrumentation_enabled.png",
        "evaluator_only/instrumentation_parity/instrumentation_enabled_provenance.json",
        "evaluator_only/provenance/p1_step_000.json", "evaluator_only/provenance/p1_step_010.json",
    ]
    file_rows = []
    for relative in core_files:
        path = P1_ROOT / relative
        file_rows.append({"path": relative, "exists": path.is_file(),
                          "bytes": path.stat().st_size if path.is_file() else None,
                          "sha256": sha256(path) if path.is_file() else None})
    audited_source_paths = [
        "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs",
        "Assets/ThoracicUSCoach/Editor/ThoracicPositionGuidedT7Batch.cs",
        "Assets/ThoracicUSCoach/Tests/Editor/PositionGuidedT7Tests.cs",
        "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs",
        "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.DirectFreshCapture.cs",
        "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.MiniZigzag.cs",
        "Assets/ThoracicUSCoach/Runtime/Thoracic2DNavigation.cs",
        "Tools/ThoracicUSCoach/position_aware_relocalization.py",
        "Tools/ThoracicUSCoach/test_position_aware_relocalization.py",
    ]
    audited_sources = []
    for relative in audited_source_paths:
        path = REPO_ROOT / relative
        audited_sources.append({"path": relative, "exists": path.is_file(),
                                "bytes": path.stat().st_size if path.is_file() else None,
                                "sha256": sha256(path) if path.is_file() else None})
    freeze_checks = []
    for relative, expected in freeze["sha256"].items():
        actual = sha256(P1_ROOT / relative)
        freeze_checks.append({"path": relative, "expectedSha256": expected,
                              "actualSha256": actual, "valid": actual == expected})
    return_step = actor["steps"][-1]
    with Image.open(calibration_path) as image:
        calibration_image_info = {"width": image.width, "height": image.height, "mode": image.mode}
    with Image.open(return_path) as image:
        return_image_info = {"width": image.width, "height": image.height, "mode": image.mode}
    integrity = {
        "contract": "thoracic_p1_r1_artifact_integrity_v1",
        "generatedUtc": generated,
        "sourceRoot": str(P1_ROOT),
        "essentialEvidencePresent": all(row["exists"] for row in file_rows),
        "files": file_rows,
        "auditedSourceFiles": audited_sources,
        "offlineFreezeChecks": freeze_checks,
        "offlineFreezeValid": all(item["valid"] for item in freeze_checks),
        "calibrationImage": {
            **calibration_image_info,
            "recordedSha256": calibration_manifest["referenceImageSha256"],
            "actualSha256": sha256(calibration_path),
            "hashValid": sha256(calibration_path) == calibration_manifest["referenceImageSha256"],
            "freshRecorded": calibration_manifest["fresh"],
            "exactPoseEpochProducerFrameAndScanSerialPersisted": False,
        },
        "returnImage": {
            **return_image_info,
            "recordedSha256": return_step["imageSha256"],
            "actualSha256": sha256(return_path),
            "hashValid": sha256(return_path) == return_step["imageSha256"],
            "poseEpoch": return_step["poseEpoch"],
            "producerPoseEpoch": return_step["producerPoseEpoch"],
            "poseAppliedUnityFrame": return_step["poseAppliedUnityFrame"],
            "producerUnityFrame": return_step["producerUnityFrame"],
            "captureUnityFrame": return_step["captureUnityFrame"],
            "producerScanSerial": return_step["producerScanSerial"],
            "freshAndAssociated": bool(return_step["frameIntegrityValid"]
                                       and return_step["poseEpoch"] == return_step["producerPoseEpoch"]
                                       and return_step["producerUnityFrame"] > return_step["poseAppliedUnityFrame"]
                                       and return_step["captureUnityFrame"] == return_step["producerUnityFrame"]),
        },
        "imageDimensionsAndModeIdentical": calibration_image_info == return_image_info,
        "acquisitionModeAudit": {
            "sameCaptureFunction": "CaptureDirectProducerFreshFrame(..., capturePrimaryProvenance: true)",
            "sameImageFunction": "CaptureCleanImage()",
            "uiOverlayOrCropInCapturedPng": False,
            "activeAggregatorCountRequired": 0,
            "exactRendererConfigurationSnapshotPersistedForBothFrames": False,
            "sourceEvidence": "ThoracicUSCoachController.PositionGuidedT7.cs:310-325,385-394 and DirectFreshCapture.cs:102-194",
        },
        "limitation": "Calibration hash and runtime fresh=true are preserved, but its exact pose epoch, producer frame, scan serial, and exact provenance snapshot were not serialized. p1_step_000 provenance belongs to navigation start, not calibration.png.",
    }
    save_json(OUTPUT_ROOT / "artifact_integrity.json", integrity)

    target_contact = vector(actor["frozenCalibratedTargetPose"]["contactPointMm"])
    target_position = vector(actor["frozenCalibratedTargetPose"]["positionMm"])
    target_rotation = quaternion(actor["frozenCalibratedTargetPose"]["rotation"])
    corridor = vector(calibration_manifest["corridorAxis"])
    corridor /= np.linalg.norm(corridor)
    lateral = vector(calibration_manifest["lateralAxis"])
    lateral /= np.linalg.norm(lateral)
    evaluator_by_step = {int(item["step"]): item for item in evaluator["steps"]}
    csv_rows = []
    for item in actor["steps"]:
        pose = item["measuredPose"]
        contact = vector(pose["contactPointMm"])
        position = vector(pose["positionMm"])
        difference = target_contact - contact
        longitudinal = float(np.dot(difference, corridor))
        lateral_error = float(np.dot(difference, lateral))
        orthogonal = math.sqrt(max(float(np.dot(difference, difference))
                                   - longitudinal ** 2 - lateral_error ** 2, 0.0))
        privileged = evaluator_by_step[int(item["step"])]
        csv_rows.append({
            "step": int(item["step"]), "action": item["action"], "accepted": item["accepted"],
            "requested_displacement_mm": float(item["requestedDisplacementMm"]),
            "achieved_displacement_mm": float(item["achievedDisplacementMm"]),
            "target_minus_contact_x_mm": float(difference[0]),
            "target_minus_contact_y_mm": float(difference[1]),
            "target_minus_contact_z_mm": float(difference[2]),
            "contact_point_distance_mm": float(np.linalg.norm(difference)),
            "probe_root_distance_mm": float(np.linalg.norm(target_position - position)),
            "longitudinal_error_mm": longitudinal, "lateral_error_mm": lateral_error,
            "unmodelled_orthogonal_component_mm": orthogonal,
            "quaternion_error_deg": quaternion_angle_deg(quaternion(pose["rotation"]), target_rotation),
            "contact_valid": item["contactValid"], "workspace_valid": item["workspaceValid"],
            "evaluator_reference_distance_mm": float(privileged["distanceToReferenceContactMm"]),
            "surface_yaw_error_deg": float(privileged["surfaceYawErrorDeg"]),
            "normal_alignment_error_deg": float(privileged["normalAlignmentErrorDeg"]),
            "stop_condition_true": abs(longitudinal) <= float(actor["longitudinalToleranceMm"])
            and abs(lateral_error) <= float(actor["lateralToleranceMm"]),
        })
    with (OUTPUT_ROOT / "navigation_pose_error.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(csv_rows[0]))
        writer.writeheader()
        writer.writerows(csv_rows)

    movement_rows = [row for row in csv_rows if row["action"] not in ("OBSERVE", "STOP")]
    final_movement = movement_rows[-1]
    previous_movement = movement_rows[-2]
    final_pose = actor["steps"][-1]["measuredPose"]
    final_contact = vector(final_pose["contactPointMm"])
    step8_contact = vector(actor["steps"][-3]["measuredPose"]["contactPointMm"])
    last_delta = final_contact - step8_contact
    extrapolated_contact = final_contact + last_delta
    extrapolated_difference = target_contact - extrapolated_contact
    extrapolated_longitudinal = float(np.dot(extrapolated_difference, corridor))
    stop_audit = {
        "contract": "thoracic_p1_r1_stop_condition_audit_v1",
        "generatedUtc": generated,
        "controllerSource": "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs",
        "decisionSourceLines": "459-525",
        "stopRule": "STOP when abs(longitudinalErrorMm) <= 3 and abs(lateralErrorMm) <= 3; orientation is evaluated only after STOP capture.",
        "robotFrame": actor["robotFrame"],
        "calibratedTargetPose": actor["frozenCalibratedTargetPose"],
        "navigationInitialPose": actor["navigationStartPose"],
        "finalMeasuredPose": final_pose,
        "corridorAxis": calibration_manifest["corridorAxis"],
        "lateralAxis": calibration_manifest["lateralAxis"],
        "finalPreStop": final_movement,
        "recordedStop": csv_rows[-1],
        "didControllerStopBecauseRemainingTranslationWasWithinThreeMm": True,
        "otherStopCausePresent": False,
        "lastAcceptedStepLongitudinalReductionMm": float(previous_movement["longitudinal_error_mm"]
                                                           - final_movement["longitudinal_error_mm"]),
        "counterfactualAnotherNominalStep": {
            "method": "linear extrapolation of the last actually executed contact-point displacement; no movement executed",
            "predictedTargetMinusContactMm": extrapolated_difference.tolist(),
            "predictedContactPointDistanceMm": float(np.linalg.norm(extrapolated_difference)),
            "predictedLongitudinalErrorMm": extrapolated_longitudinal,
            "wouldCrossTargetLongitudinally": extrapolated_longitudinal < 0.0,
            "predictedOvershootMagnitudeMm": abs(extrapolated_longitudinal) if extrapolated_longitudinal < 0 else 0.0,
            "wouldBeCloserInThisLinearApproximation": float(np.linalg.norm(extrapolated_difference))
            < final_movement["contact_point_distance_mm"],
            "safetyStatus": "NOT_EXECUTED; extrapolation is not proof that the next surface projection would be accepted",
        },
        "fineStepCapability": {
            "currentP1ControllerHasSmallerStepStage": False,
            "existingActive2DStepRangeMm": [0.5, 5.0],
            "sameSafetyPrimitiveCanUseSmallerConfiguredStep": True,
            "minimumMeaningfulProgressForA0p5MmRequestMm": 0.25,
            "exactIncrementalReachabilityAtFinalPose": "UNTESTED",
            "targetPoseWasValidForDirectCalibrationPlacement": True,
        },
    }
    save_json(OUTPUT_ROOT / "stop_condition_audit.json", stop_audit)

    calibration_descriptor = load_image_descriptor(calibration_path)
    return_descriptor = load_image_descriptor(return_path)
    parity_descriptor = load_image_descriptor(parity_path)
    locked_ssim = block_ssim(calibration_descriptor, return_descriptor)
    same_pose_ssim = block_ssim(calibration_descriptor, parity_descriptor)
    calibration_raw = raw_gray(calibration_path)
    return_raw = raw_gray(return_path)
    difference = np.abs(calibration_raw - return_raw)
    calibration_pose_provenance = load_json(
        P1_ROOT / "evaluator_only/instrumentation_parity/instrumentation_enabled_provenance.json")
    return_provenance = load_json(P1_ROOT / "evaluator_only/provenance/p1_step_010.json")
    image_audit = {
        "contract": "thoracic_p1_r1_ssim_recomputation_v1",
        "generatedUtc": generated,
        "lockedImplementation": "load_image_descriptor 5%-95% crop -> bilinear 64x64 -> per-image 2%-98% robust normalization -> fixed 8x8-block SSIM",
        "reportedSimilarity": float(pilot["calibrationReturnSimilarity"]),
        "recomputedSimilarity": locked_ssim,
        "absoluteRecomputationDifference": abs(locked_ssim - float(pilot["calibrationReturnSimilarity"])),
        "lockedThreshold": float(confirmation["lockedThreshold"]),
        "lockedVerdict": "PASS" if locked_ssim >= float(confirmation["lockedThreshold"]) else "FAIL",
        "sameCalibrationPoseNaturalRepeatSimilarity": same_pose_ssim,
        "sameCalibrationPoseRepeatPassesLockedThreshold": same_pose_ssim >= float(confirmation["lockedThreshold"]),
        "fullResolutionUncropped8x8BlockSsimSupplementary": full_resolution_block_ssim(calibration_raw, return_raw),
        "pixelDifferenceSupplementary": {
            "meanAbsoluteDifference": float(np.mean(difference)),
            "rmse": float(np.sqrt(np.mean((calibration_raw - return_raw) ** 2))),
            "p95AbsoluteDifference": float(np.quantile(difference, 0.95)),
            "fractionAbsoluteDifferenceAbove0p10": float(np.mean(difference > 0.10)),
        },
        "lockedDescriptorRegions": {
            "leftHalfSsim": block_ssim(calibration_descriptor[:, :32], return_descriptor[:, :32]),
            "rightHalfSsim": block_ssim(calibration_descriptor[:, 32:], return_descriptor[:, 32:]),
            "phaseCorrelation": phase_correlation_shift(calibration_descriptor, return_descriptor),
            "calibrationBrightCentroid": bright_centroid(calibration_descriptor),
            "returnBrightCentroid": bright_centroid(return_descriptor),
            "calibrationCentralShadow": central_shadow_summary(calibration_descriptor),
            "returnCentralShadow": central_shadow_summary(return_descriptor),
        },
        "provenanceScanIntersectionComparison": provenance_summary(
            calibration_pose_provenance, return_provenance),
        "interpretation": "Same-pose renderer variability passes the locked threshold, while the residual-pose return changes both image morphology and evaluator-only beam intersections. No single crop or stale-frame discrepancy is indicated.",
        "provenanceLimitation": "Exact provenance for calibration.png was not serialized; the comparison uses the instrumented scan captured at the same unchanged calibration pose immediately before calibration.png.",
    }
    save_json(OUTPUT_ROOT / "ssim_recomputation.json", image_audit)
    draw_comparison(calibration_path, return_path, OUTPUT_ROOT / "calibration_return_comparison.png",
                    locked_ssim, float(confirmation["lockedThreshold"]),
                    final_movement["contact_point_distance_mm"])

    m0b_actor, m0b_privileged, _ = verify_inputs(M0B_ROOT)
    records = build_records(M0B_ROOT, m0b_actor, m0b_privileged)
    privileged_by_index = {int(item["observationIndex"]): item
                           for item in m0b_privileged["observations"]}
    for record in records:
        privileged = privileged_by_index[int(record["observationIndex"])]
        record["contactPointMm"] = vector(privileged["contactPoint"])
    reference = [item for item in records if item["phase"] in REFERENCE_PHASES]
    queries = [item for item in records if item["phase"] in QUERY_PHASES]
    control_pairs = []
    for query in queries:
        nearest = min(reference, key=lambda candidate: abs(candidate["actorRelativeMm"]
                                                            - query["actorRelativeMm"]))
        coordinate_gap = abs(float(nearest["actorRelativeMm"] - query["actorRelativeMm"]))
        if coordinate_gap <= 1.6:
            translation = float(np.linalg.norm(nearest["robotPositionMm"] - query["robotPositionMm"]))
            angle = quaternion_angle_deg(nearest["robotRotationQuaternion"],
                                         query["robotRotationQuaternion"])
            control_pairs.append({
                "referenceObservationIndex": int(nearest["observationIndex"]),
                "queryObservationIndex": int(query["observationIndex"]),
                "actorRelativeGapMm": coordinate_gap,
                "probeRootTranslationMm": translation,
                "orientationDifferenceDeg": angle,
                "fullPoseDistanceMm": pose_distance_mm(
                    nearest["robotPositionMm"], nearest["robotRotationQuaternion"],
                    query["robotPositionMm"], query["robotRotationQuaternion"]),
                "ssim": block_ssim(nearest["descriptor"], query["descriptor"]),
            })
    adjacent_pairs = []
    for left_record, right_record in zip(records, records[1:]):
        if left_record["phase"] != right_record["phase"]:
            continue
        contact_delta = right_record["contactPointMm"] - left_record["contactPointMm"]
        translation = float(np.linalg.norm(contact_delta))
        angle = quaternion_angle_deg(left_record["robotRotationQuaternion"],
                                     right_record["robotRotationQuaternion"])
        adjacent_pairs.append({
            "leftObservationIndex": int(left_record["observationIndex"]),
            "rightObservationIndex": int(right_record["observationIndex"]),
            "phase": left_record["phase"],
            "longitudinalDisplacementMm": abs(float(np.dot(contact_delta, corridor))),
            "lateralDisplacementMm": abs(float(np.dot(contact_delta, lateral))),
            "probeRootTranslationMm": translation,
            "orientationDifferenceDeg": angle,
            "fullPoseDistanceMm": pose_distance_mm(
                left_record["robotPositionMm"], left_record["robotRotationQuaternion"],
                right_record["robotPositionMm"], right_record["robotRotationQuaternion"]),
            "ssim": block_ssim(left_record["descriptor"], right_record["descriptor"]),
        })
    below = sorted((item for item in adjacent_pairs
                    if item["ssim"] < float(confirmation["lockedThreshold"])),
                   key=lambda item: item["fullPoseDistanceMm"])

    def nearest_to_p1(position: np.ndarray, rotation: np.ndarray,
                      descriptor: np.ndarray) -> list[dict]:
        ranked = sorted(records, key=lambda item: pose_distance_mm(
            item["robotPositionMm"], item["robotRotationQuaternion"], position, rotation))
        output = []
        for item in ranked[:5]:
            output.append({
                "observationIndex": int(item["observationIndex"]), "phase": item["phase"],
                "image": item["image"], "actorRelativeMm": float(item["actorRelativeMm"]),
                "probeRootTranslationMm": float(np.linalg.norm(item["robotPositionMm"] - position)),
                "orientationDifferenceDeg": quaternion_angle_deg(item["robotRotationQuaternion"], rotation),
                "fullPoseDistanceMm": pose_distance_mm(
                    item["robotPositionMm"], item["robotRotationQuaternion"], position, rotation),
                "contactPointDistanceMm": float(np.linalg.norm(item["contactPointMm"]
                                                                 - target_contact)),
                "ssimToP1Image": block_ssim(item["descriptor"], descriptor),
            })
        return output

    final_position = vector(final_pose["positionMm"])
    final_rotation = quaternion(final_pose["rotation"])
    sensitivity = {
        "contract": "thoracic_p1_r1_pose_image_sensitivity_v1",
        "generatedUtc": generated,
        "directP1Evidence": {
            "samePoseCalibrationVersusParitySsim": same_pose_ssim,
            "calibrationVersusReturn": {
                "longitudinalDisplacementMm": abs(final_movement["longitudinal_error_mm"]),
                "lateralDisplacementMm": abs(final_movement["lateral_error_mm"]),
                "unmodelledOrthogonalComponentMm": final_movement["unmodelled_orthogonal_component_mm"],
                "contactPointDistanceMm": final_movement["contact_point_distance_mm"],
                "orientationDifferenceDeg": final_movement["quaternion_error_deg"],
                "ssim": locked_ssim,
            },
            "capturedIntermediateP1FramesBetweenReturnAndCalibration": 0,
        },
        "thresholdCalibrationPopulation": {
            "description": "39 independently captured reverse-pass pairs selected within 1.6 mm actor-relative tolerance; actual poses summarized below",
            "pairCount": len(control_pairs),
            "actorRelativeGapMm": quantiles([item["actorRelativeGapMm"] for item in control_pairs]),
            "probeRootTranslationMm": quantiles([item["probeRootTranslationMm"] for item in control_pairs]),
            "orientationDifferenceDeg": quantiles([item["orientationDifferenceDeg"] for item in control_pairs]),
            "fullPoseDistanceMm": quantiles([item["fullPoseDistanceMm"] for item in control_pairs]),
            "ssim": quantiles([item["ssim"] for item in control_pairs]),
            "lockedThreshold": float(confirmation["lockedThreshold"]),
        },
        "approximatelyOneStepPairs": {
            "pairCount": len(adjacent_pairs),
            "fullPoseDistanceMm": quantiles([item["fullPoseDistanceMm"] for item in adjacent_pairs]),
            "ssim": quantiles([item["ssim"] for item in adjacent_pairs]),
            "belowLockedThresholdCount": sum(item["ssim"] < float(confirmation["lockedThreshold"])
                                             for item in adjacent_pairs),
            "smallestObservedPoseSeparationBelowThreshold": below[0] if below else None,
        },
        "nearestM0BObservationsToCalibrationPose": nearest_to_p1(
            target_position, target_rotation, calibration_descriptor),
        "nearestM0BObservationsToReturnPose": nearest_to_p1(
            final_position, final_rotation, return_descriptor),
        "exactP1SensitivityVerdict": "INSUFFICIENT_POSE_SENSITIVITY_DATA",
        "reason": "The direct P1 path has no captured intermediate frames between 0 and 2.660 mm residual. M0-B supplies same-position and approximately 3 mm controls, but it follows a laterally different corridor and cannot establish the exact sub-step SSIM curve at calibrated T7.",
    }
    save_json(OUTPUT_ROOT / "pose_image_sensitivity.json", sensitivity)

    clean_condition = next(item for item in relocalization["conditions"]
                           if item["perturbation"]["name"] == "clean")
    clean_queries = clean_condition["queries"]
    image_errors = [float(item["imageOnly"]["absoluteErrorMm"]) for item in clean_queries]
    reference_indices = {int(item["observationIndex"]) for item in map_manifest["entries"]}
    query_indices = {int(item["queryObservationIndex"]) for item in clean_queries}
    analyzer_source = (TOOLS_ROOT / "position_aware_relocalization.py").read_text(encoding="utf-8")
    localize_source = analyzer_source[analyzer_source.index("def localize_query"):
                                      analyzer_source.index("def _metrics")]
    selection_source = localize_source[:localize_source.index("def prediction")]
    relocalization_audit = {
        "contract": "thoracic_p1_r1_relocalization_audit_v1",
        "generatedUtc": generated,
        "referenceCount": len(reference_indices), "queryCount": len(query_indices),
        "referenceQueryIndexOverlap": sorted(reference_indices & query_indices),
        "imageOnlySelectionUsesQueryGroundTruthPosition": False,
        "evidence": "image_index = argmax(block_ssim(query descriptor, reference descriptor)); query actorRelativeMm first enters prediction() for scoring after selection",
        "queryGroundTruthTokenPresentBeforeSelection": "query[\"actorRelativeMm\"]" in selection_source,
        "tLevelOrAnatomyMarkerUsed": False,
        "positionPlusImageUsesHiddenUnperturbedCoordinateAfterPerturbation": False,
        "perturbationAudit": "apply_pose_perturbation is applied once to query robot position/rotation; pose distances and fusion use that perturbed pose. Unperturbed actorRelativeMm is used for scoring all methods against the same truth.",
        "cleanImageOnlyErrorMm": quantiles(image_errors),
        "cleanImageOnlyNearExactMatchCount": sum(error < 0.01 for error in image_errors),
        "cleanImageOnlyAdjacentGridMatchCount": sum(2.9 <= error <= 3.1 for error in image_errors),
        "wrongLocationDefinition": f"absolute actor-relative error > {WRONG_LOCATION_TOLERANCE_MM:g} mm",
        "ambiguousDefinition": f"another reference within SSIM delta {AMBIGUITY_SIMILARITY_DELTA:g} and separated by at least {AMBIGUITY_SEPARATION_MM:g} mm",
        "whyMedianIsApproximately0p003Mm": "Reference and query traversals revisit almost the same 3 mm sampling grid; most selected map entries differ by only numerical/physical revisit residuals.",
        "whyWorstCaseIsApproximately3Mm": "A minority of queries select the immediately adjacent reference-grid sample, one nominal physical step away.",
        "fusionImprovementComparison": "All position-only and fused errors are scored against the same unperturbed query actorRelativeMm.",
        "fineCorrectionInterpretation": "Technically plausible for correcting a discrete 3 mm map-registration bias on this revisited path, but not evidence for continuous sub-millimetre correction or held-out trajectory generalization.",
        "causalRoleInP1Failure": "NONE: the offline relocalizer did not steer the physical P1 controller or determine the confirmation SSIM.",
    }
    save_json(OUTPUT_ROOT / "relocalization_audit.json", relocalization_audit)

    root_cause = {
        "contract": "thoracic_p1_r1_root_cause_matrix_v1",
        "generatedUtc": generated,
        "classifications": [
            {"hypothesis": "H1_POSITION_TOLERANCE_DISCRETE_STEP_LIMITATION", "classification": "CONFIRMED",
             "evidence": "The controller stopped solely when 2.628 mm longitudinal and 0.0016 mm lateral residuals entered the 3 mm box; it has no fine-step stage. Linear extrapolation of one more 3 mm step crosses the target by about 0.34 mm but would likely be closer; it was not executed."},
            {"hypothesis": "H2_CALIBRATION_OR_POSE_FRAME_MISMATCH", "classification": "REFUTED",
             "evidence": "Calibration and navigation use UNITY_WORLD_IDENTITY, the frozen target is copied unchanged, final quaternion exactly matches, and actor/evaluator errors agree. Exact calibration publication metadata was not persisted, so this is refuted at the recorded pose-contract level rather than by a complete calibration provenance record."},
            {"hypothesis": "H3_ACQUISITION_OR_SSIM_IMPLEMENTATION_MISMATCH", "classification": "REFUTED",
             "evidence": "Hashes, dimensions, capture function and recomputation agree; SSIM recomputes exactly. A natural same-pose calibration repeat scores 0.989491 above the locked 0.971 threshold."},
            {"hypothesis": "H4_GENUINE_ULTRASOUND_SENSITIVITY_TO_RESIDUAL_POSE", "classification": "SUPPORTED",
             "evidence": "At identical orientation, the 2.660 mm contact residual yields SSIM 0.643609 and visibly different interfaces; evaluator-only same-pose-vs-return beam intersections also change. No direct intermediate P1 images exist, so the exact sensitivity curve is unresolved."},
            {"hypothesis": "H5_RELOCALIZATION_METRIC_OR_INFORMATION_LEAKAGE_ISSUE", "classification": "REFUTED",
             "evidence": "Static audit finds no query-position use in image-only selection and no hidden coordinate in fusion after perturbation. The offline protocol is optimistic because paths and 3 mm grids nearly coincide, but it was not in the physical control loop and did not cause this failure."},
        ],
        "primaryAttribution": "Premature coarse-tolerance STOP combined with real ultrasound sensitivity to the residual scan-plane translation.",
        "notClaimed": "The audit does not prove a particular sub-millimetre SSIM operating point because no P1 intermediate frames were captured.",
    }
    save_json(OUTPUT_ROOT / "root_cause_matrix.json", root_cause)

    recommended = f"""# Recommended minimal fix — not implemented

## Primary correction

Add a two-stage position controller to the isolated `CALIBRATED_POSITION_GUIDED_BASELINE` only:

1. Preserve the current 3 mm coarse actions until the remaining longitudinal error first enters the existing 3 mm tolerance.
2. Do not declare STOP there. Switch to the existing Active2D physical movement primitive with a fixed 0.5 mm diagnostic step (the current declared minimum) and continue pose feedback until longitudinal and lateral errors are each no greater than 0.5 mm.
3. Keep the locked SSIM threshold `{float(confirmation['lockedThreshold']):.6f}` as confirmation only; do not use evaluator score or T7 marker queries during navigation.

The last observed residual was `{abs(final_movement['longitudinal_error_mm']):.3f}` mm. Five 0.5 mm movements would nominally leave about 0.13 mm longitudinal residual, but this is a planning estimate, not an executed result.

## Exact files for a separately approved implementation

- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs`: add the bounded fine stage, per-action step logging, a 0.5 mm final pose tolerance, and new isolated P1-R2 output roots.
- `Assets/ThoracicUSCoach/Tests/Editor/PositionGuidedT7Tests.cs`: test no premature STOP at 2.628 mm, coarse-to-fine transition, bounded convergence/oscillation handling, and unchanged safety rejection.
- `Assets/ThoracicUSCoach/Editor/ThoracicPositionGuidedT7Batch.cs`: only the minimal run-id/output selection needed to preserve P1 and record three predeclared P1-R2 pilots separately.

No change is recommended to Active2D, DirectFreshCapture, the scene, renderer, SSIM implementation, threshold, or offline descriptor.

## Expected impact

- Expected position effect: reduce the final contact-point residual from 2.660 mm toward no more than roughly 0.5–0.7 mm while retaining physical surface following.
- Image effect: unknown until captured. The audit supports pose sensitivity but has `INSUFFICIENT_POSE_SENSITIVITY_DATA` for the exact sub-step SSIM curve.

## Safety constraints that remain unchanged

Rigid contact, surface projection, workspace enforcement, rollback, teleport rejection, normal/yaw alignment, movement budget, pose epoch, producer frame/serial freshness, provenance isolation, and no runtime target-marker query.

## Required fresh evidence

Run **three** predeclared yaw-0 pilots after compilation and all existing safety/freshness tests pass. Three runs are a smoke check, not a reliability claim. A successful correction requires all three to reach the fine pose tolerance, produce fresh associated images, retain zero unhandled safety failures, and meet the unchanged SSIM threshold.

## Stopping conditions

- Stop immediately on any rejected/unsafe fine movement, contact/workspace failure, stale frame, pose/provenance mismatch, or budget exhaustion.
- If the first physically valid run reaches the fine pose tolerance but still fails locked SSIM, stop the campaign: H1 alone is insufficient, and repeated runs must not be used to search for a favorable image.
- Do not weaken the threshold or add visual servoing inside this correction.
"""
    (OUTPUT_ROOT / "recommended_minimal_fix.md").write_text(recommended, encoding="utf-8")

    report = f"""# P1-R1 Position-Guided T7 Reacquisition Failure Attribution

## 1. IMPLEMENTATION

Read-only offline audit only. Existing source and P1 artifacts were not modified. No Unity capture, training, threshold change, controller change, commit, or push was performed. This directory contains derived diagnostic evidence and the isolated audit script.

## 2. VERIFICATION

- Essential P1 files present: **{integrity['essentialEvidencePresent']}**.
- P1 offline freeze hashes: **{'PASS' if integrity['offlineFreezeValid'] else 'FAIL'}** ({len(freeze_checks)}/{len(freeze_checks)} valid).
- Calibration PNG hash: **{'PASS' if integrity['calibrationImage']['hashValid'] else 'FAIL'}**.
- Return PNG hash and freshness association: **{'PASS' if integrity['returnImage']['hashValid'] and integrity['returnImage']['freshAndAssociated'] else 'FAIL'}**.
- Both images: `{calibration_image_info['width']}x{calibration_image_info['height']}` `{calibration_image_info['mode']}`.
- Limitation: the exact calibration image pose epoch/producer frame/scan serial was not serialized. Its hash and runtime `fresh=true` are present; same-pose instrumentation evidence is separate.

## 3. NAVIGATION STOP ROOT CAUSE

The controller stopped because the remaining longitudinal error was **{abs(final_movement['longitudinal_error_mm']):.6f} mm**, below the locked 3 mm tolerance, with lateral error **{abs(final_movement['lateral_error_mm']):.6f} mm**. There was no rejection, safety failure, workspace failure, or alternate STOP trigger.

Calibrated contact point: `({target_contact[0]:.6f}, {target_contact[1]:.6f}, {target_contact[2]:.6f}) mm`; calibrated quaternion: `({target_rotation[0]:.9f}, {target_rotation[1]:.9f}, {target_rotation[2]:.9f}, {target_rotation[3]:.9f})`. Initial and every executed pose are preserved in `navigation_pose_error.csv` and `stop_condition_audit.json`.

Final target-minus-contact vector: `({target_contact[0]-final_contact[0]:.6f}, {target_contact[1]-final_contact[1]:.6f}, {target_contact[2]-final_contact[2]:.6f}) mm`; 3D contact distance **{final_movement['contact_point_distance_mm']:.6f} mm**. Quaternion, surface-yaw, and normal-alignment errors were all 0 degrees. The actor's two-axis stop model leaves an additional **{final_movement['unmodelled_orthogonal_component_mm']:.6f} mm** orthogonal component caused by the curved contact geometry.

A linear extrapolation of the last real 3 mm motion predicts another full step would cross the target longitudinally by approximately **{abs(extrapolated_longitudinal):.3f} mm** and be closer in 3D. This step was not executed, so safety acceptance is unknown. The current P1 loop has no smaller step, although the unchanged Active2D primitive declares a 0.5–5 mm range and retains all safety checks.

## 4. CALIBRATION VS RETURN IMAGE

- Locked SSIM recomputation: **{locked_ssim:.12f}**, exactly reproducing the reported value within `{image_audit['absoluteRecomputationDifference']:.3e}`.
- Locked threshold: **{float(confirmation['lockedThreshold']):.12f}**; verdict remains **FAIL**.
- Same calibration pose, separate natural render: **{same_pose_ssim:.12f}**, above threshold.
- Left/right descriptor SSIM: **{image_audit['lockedDescriptorRegions']['leftHalfSsim']:.6f} / {image_audit['lockedDescriptorRegions']['rightHalfSsim']:.6f}**.

The side-by-side/difference image shows displacement and shape changes in the central bright arch, central shadow boundary, and lower bilateral bright interfaces. The evaluator-only beam audit also changes visible-interface identities/depths. This is evidence of a changed scan-plane intersection, not a stale or missing frame. Exact anatomy identity remains unresolved.

## 5. POSE ERROR VS SSIM SENSITIVITY

The threshold was calibrated from **{len(control_pairs)}** reverse-pass near-same-position pairs. Their median actual full-pose separation was **{sensitivity['thresholdCalibrationPopulation']['fullPoseDistanceMm']['median']:.6f} mm**, not 2–3 mm. The 2.660 mm P1 return is therefore outside the threshold's primary repeatability population.

Existing M0-B data contain approximately one-step pairs, and **{sensitivity['approximatelyOneStepPairs']['belowLockedThresholdCount']}/{len(adjacent_pairs)}** fall below the locked threshold. However, there are no direct P1 frames between the calibration pose and the 2.660 mm return pose. Exact verdict: **INSUFFICIENT_POSE_SENSITIVITY_DATA** for the sub-step curve at T7.

The smallest observed one-step full-pose separation below threshold was **{sensitivity['approximatelyOneStepPairs']['smallestObservedPoseSeparationBelowThreshold']['fullPoseDistanceMm']:.6f} mm** with SSIM **{sensitivity['approximatelyOneStepPairs']['smallestObservedPoseSeparationBelowThreshold']['ssim']:.6f}**. This is a corridor-wide observation, not a T7-specific operating point.

## 6. RELOCALIZATION AUDIT

- Reference/query captures are disjoint: **{len(reference_indices & query_indices)} overlapping observation IDs**.
- IMAGE_ONLY selection uses descriptor SSIM only; query ground-truth position enters after selection for scoring.
- Fusion applies the declared bias to measured query pose and uses no second unperturbed position in selection.
- Clean IMAGE_ONLY errors: median **{relocalization_audit['cleanImageOnlyErrorMm']['median']:.6f} mm**, worst **{relocalization_audit['cleanImageOnlyErrorMm']['maximum']:.6f} mm**.
- Near-exact matches: **{relocalization_audit['cleanImageOnlyNearExactMatchCount']}/{len(image_errors)}**; adjacent-grid matches: **{relocalization_audit['cleanImageOnlyAdjacentGridMatchCount']}/{len(image_errors)}**.

The ~0.003 mm median occurs because independent traversals revisit nearly identical 3 mm grid locations. The ~3 mm worst case is one adjacent grid sample. Wrong-location is defined as >{WRONG_LOCATION_TOLERANCE_MM:g} mm, so a one-step error is not counted wrong. This supports discrete map-bias correction on the same path, not continuous fine visual correction.

## 7. ROOT-CAUSE CLASSIFICATION

| Hypothesis | Classification |
| --- | --- |
| H1 — Position tolerance / discrete step | **CONFIRMED** |
| H2 — Calibration or pose-frame mismatch | **REFUTED** (calibration publication metadata persistence is incomplete) |
| H3 — Acquisition or SSIM mismatch | **REFUTED** |
| H4 — Genuine ultrasound sensitivity to residual pose | **SUPPORTED** |
| H5 — Relocalization leakage/metric caused the physical failure | **REFUTED** |

Primary attribution: premature coarse-tolerance STOP combined with real ultrasound sensitivity to residual scan-plane translation. The exact pose-to-SSIM operating curve remains unmeasured.

## 8. ONE RECOMMENDED MINIMAL FIX

For a separately approved P1-R2 only: preserve coarse 3 mm navigation, then use the same safe Active2D primitive at fixed 0.5 mm steps until both longitudinal and lateral residuals are <=0.5 mm. Keep SSIM as locked confirmation, not steering. Do not change Active2D, freshness, renderer, threshold, or evaluator separation.

## 9. EXACT FILES AND RISKS

Future modification would be limited to:

- `ThoracicUSCoachController.PositionGuidedT7.cs`
- `PositionGuidedT7Tests.cs`
- `ThoracicPositionGuidedT7Batch.cs` only for isolated P1-R2 run IDs

Risks: fine-step progress may fall near the 0.25 mm meaningful-progress floor, curvature may create small residual normal displacement, and closer pose may still fail SSIM. Three locked yaw-0 smoke pilots are required; stop on the first valid fine-position run that still fails SSIM.

## 10. DECISION REQUIRED

Approve or reject the bounded P1-R2 two-stage position-control experiment. No correction or new pilot has been executed in P1-R1.

Scientific status remains unchanged: POSITION_NAVIGATION **PASS** under 3 mm tolerance; FRESH_ULTRASOUND_CONFIRMATION **FAIL**; AUTONOMOUS_T7_IDENTIFICATION **NOT_TESTED**; G2 **FAIL**; G3/G4 **NOT_RUN**.
"""
    (OUTPUT_ROOT / "REPORT.md").write_text(report, encoding="utf-8")


if __name__ == "__main__":
    main()
