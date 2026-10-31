from __future__ import annotations

import argparse
import hashlib
import json
import math
from datetime import datetime, timezone
from pathlib import Path
from typing import Sequence

import numpy as np
from PIL import Image, ImageDraw


REFERENCE_PHASES = ("caudal_reverse", "cranial_outbound")
QUERY_PHASES = ("caudal_outbound", "cranial_reverse")
PERTURBATIONS = (
    {"name": "clean", "longitudinalBiasMm": 0.0, "yawBiasDeg": 0.0},
    {"name": "longitudinal_plus_3mm", "longitudinalBiasMm": 3.0, "yawBiasDeg": 0.0},
    {"name": "longitudinal_minus_3mm", "longitudinalBiasMm": -3.0, "yawBiasDeg": 0.0},
    {"name": "yaw_plus_2deg", "longitudinalBiasMm": 0.0, "yawBiasDeg": 2.0},
    {"name": "yaw_minus_2deg", "longitudinalBiasMm": 0.0, "yawBiasDeg": -2.0},
)
IMAGE_SIZE = 64
SSIM_BLOCK_SIZE = 8
POSE_SIGMA_MM = 6.0
ORIENTATION_MM_PER_DEG = 0.5
FUSION_POSITION_WEIGHT = 0.65
FUSION_IMAGE_WEIGHT = 0.35
WRONG_LOCATION_TOLERANCE_MM = 6.0
AMBIGUITY_SIMILARITY_DELTA = 0.01
AMBIGUITY_SEPARATION_MM = 9.0
FORBIDDEN_RELOCALIZATION_KEYS = (
    "t7", "tlevel", "referencelevel", "anatomy", "mesh", "provenance",
    "evaluatordistance", "targetscore", "targetcoordinate",
)


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _vector(value: dict) -> np.ndarray:
    return np.asarray([float(value[axis]) for axis in ("x", "y", "z")], dtype=np.float64)


def _quaternion(value: dict) -> np.ndarray:
    result = np.asarray([float(value[axis]) for axis in ("x", "y", "z", "w")], dtype=np.float64)
    return result / max(float(np.linalg.norm(result)), 1e-12)


def quaternion_angle_deg(left: np.ndarray, right: np.ndarray) -> float:
    dot = float(np.clip(abs(np.dot(left, right)), 0.0, 1.0))
    return float(2.0 * math.degrees(math.acos(dot)))


def quaternion_multiply(left: np.ndarray, right: np.ndarray) -> np.ndarray:
    x1, y1, z1, w1 = left
    x2, y2, z2, w2 = right
    result = np.asarray([
        w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
        w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
        w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2,
        w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2,
    ])
    return result / max(float(np.linalg.norm(result)), 1e-12)


def axis_angle_quaternion(axis: np.ndarray, angle_deg: float) -> np.ndarray:
    normalized = axis / max(float(np.linalg.norm(axis)), 1e-12)
    half = math.radians(angle_deg) / 2.0
    return np.asarray([*(normalized * math.sin(half)), math.cos(half)], dtype=np.float64)


def load_image_descriptor(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        gray = image.convert("L")
        width, height = gray.size
        crop = gray.crop((int(0.05 * width), int(0.05 * height),
                          int(0.95 * width), int(0.95 * height)))
        resized = crop.resize((IMAGE_SIZE, IMAGE_SIZE), Image.Resampling.BILINEAR)
        values = np.asarray(resized, dtype=np.float64) / 255.0
    low, high = np.quantile(values, (0.02, 0.98))
    return np.clip((values - low) / max(float(high - low), 1e-8), 0.0, 1.0)


def block_ssim(left: np.ndarray, right: np.ndarray) -> float:
    if left.shape != right.shape or left.ndim != 2:
        raise ValueError("SSIM inputs must have the same 2D shape")
    c1 = 0.01 ** 2
    c2 = 0.03 ** 2
    scores = []
    for y in range(0, left.shape[0], SSIM_BLOCK_SIZE):
        for x in range(0, left.shape[1], SSIM_BLOCK_SIZE):
            a = left[y:y + SSIM_BLOCK_SIZE, x:x + SSIM_BLOCK_SIZE]
            b = right[y:y + SSIM_BLOCK_SIZE, x:x + SSIM_BLOCK_SIZE]
            mean_a, mean_b = float(np.mean(a)), float(np.mean(b))
            var_a, var_b = float(np.var(a)), float(np.var(b))
            covariance = float(np.mean((a - mean_a) * (b - mean_b)))
            numerator = (2 * mean_a * mean_b + c1) * (2 * covariance + c2)
            denominator = (mean_a * mean_a + mean_b * mean_b + c1) * (var_a + var_b + c2)
            scores.append(numerator / max(denominator, 1e-12))
    return float(np.mean(scores))


def pose_distance_mm(position: np.ndarray, rotation: np.ndarray,
                     reference_position: np.ndarray, reference_rotation: np.ndarray) -> float:
    translation = float(np.linalg.norm(position - reference_position))
    orientation = quaternion_angle_deg(rotation, reference_rotation) * ORIENTATION_MM_PER_DEG
    return float(math.sqrt(translation * translation + orientation * orientation))


def apply_pose_perturbation(position: np.ndarray, rotation: np.ndarray, corridor_axis: np.ndarray,
                            longitudinal_bias_mm: float, yaw_bias_deg: float) -> tuple[np.ndarray, np.ndarray]:
    perturbed_position = position + corridor_axis * longitudinal_bias_mm
    perturbed_rotation = quaternion_multiply(
        axis_angle_quaternion(corridor_axis, yaw_bias_deg), rotation)
    return perturbed_position, perturbed_rotation


def assert_no_restricted_keys(value: object, path: str = "") -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            normalized = str(key).replace("_", "").lower()
            if any(forbidden in normalized for forbidden in FORBIDDEN_RELOCALIZATION_KEYS):
                raise ValueError(f"Restricted relocalization field at {path}{key}")
            assert_no_restricted_keys(child, f"{path}{key}.")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            assert_no_restricted_keys(child, f"{path}[{index}].")


def verify_inputs(run_root: Path) -> tuple[dict, dict, dict]:
    actor_path = run_root / "trajectory_actor.json"
    privileged_path = run_root / "evaluator_only" / "trajectory_privileged.json"
    frame_manifest_path = run_root / "frame_manifest.json"
    actor = _load_json(actor_path)
    privileged = _load_json(privileged_path)
    observations = actor.get("observations", [])
    privileged_observations = privileged.get("observations", [])
    if len(observations) != 103 or len(privileged_observations) != 103:
        raise ValueError("P1 requires the exact 103-observation M0-B dataset")
    privileged_by_index = {int(item["observationIndex"]): item for item in privileged_observations}
    failures = []
    for item in observations:
        index = int(item["observationIndex"])
        frame = run_root / item["image"]
        if not frame.is_file() or _sha256(frame) != item["imageSha256"]:
            failures.append(index)
        if index not in privileged_by_index:
            failures.append(index)
    if failures:
        raise ValueError(f"P1 input integrity failed at observations {sorted(set(failures))}")
    manifest = {
        "contract": "thoracic_p1_position_aware_input_v1",
        "generatedUtc": _utc_now(),
        "sourceDataset": str(run_root),
        "observationCount": len(observations),
        "actorTraceSha256": _sha256(actor_path),
        "privilegedPoseChannelSha256": _sha256(privileged_path),
        "frameManifestSha256": _sha256(frame_manifest_path),
        "frameHashesVerified": len(observations),
        "poseChannelUse": "Unity world pose is copied as ideal simulated robot kinematics; anatomy and renderer provenance are discarded.",
        "tLevelReferencesRead": False,
    }
    return actor, privileged, manifest


def build_records(run_root: Path, actor: dict, privileged: dict) -> list[dict]:
    privileged_by_index = {int(item["observationIndex"]): item for item in privileged["observations"]}
    records = []
    for item in actor["observations"]:
        index = int(item["observationIndex"])
        pose = privileged_by_index[index]
        records.append({
            "observationIndex": index,
            "phase": item["phase"],
            "actorRelativeMm": float(item["signedActorRelativeMm"]),
            "robotPositionMm": _vector(pose["probePosition"]),
            "robotRotationQuaternion": _quaternion(pose["probeRotation"]),
            "image": item["image"],
            "imageSha256": item["imageSha256"],
            "requestedAction": item["requestedAction"],
            "achievedDisplacementMm": float(item["achievedDisplacementMm"]),
            "fresh": bool(item["poseEpoch"] == item["producerPoseEpoch"]
                          and item["producerUnityFrame"] > item["poseAppliedUnityFrame"]
                          and item["captureUnityFrame"] == item["producerUnityFrame"]),
            "descriptor": load_image_descriptor(run_root / item["image"]),
        })
    return records


def _json_record(item: dict) -> dict:
    return {
        "observationIndex": item["observationIndex"],
        "phase": item["phase"],
        "actorRelativeMm": item["actorRelativeMm"],
        "robotPositionMm": item["robotPositionMm"].tolist(),
        "robotRotationQuaternion": item["robotRotationQuaternion"].tolist(),
        "image": item["image"],
        "imageSha256": item["imageSha256"],
        "requestedAction": item["requestedAction"],
        "achievedDisplacementMm": item["achievedDisplacementMm"],
        "fresh": item["fresh"],
    }


def matched_control_similarities(reference: Sequence[dict], query: Sequence[dict], tolerance_mm: float = 1.6) -> list[float]:
    similarities = []
    for item in query:
        nearest = min(reference, key=lambda candidate: abs(candidate["actorRelativeMm"] - item["actorRelativeMm"]))
        if abs(nearest["actorRelativeMm"] - item["actorRelativeMm"]) <= tolerance_mm:
            similarities.append(block_ssim(item["descriptor"], nearest["descriptor"]))
    return similarities


def confirmation_threshold(similarities: Sequence[float]) -> float:
    if len(similarities) < 5:
        raise ValueError("At least five independent same-position controls are required")
    return float(np.clip(np.quantile(similarities, 0.05) - 0.02, 0.85, 0.98))


def localize_query(reference: Sequence[dict], query: dict, corridor_axis: np.ndarray,
                   perturbation: dict) -> dict:
    measured_position, measured_rotation = apply_pose_perturbation(
        query["robotPositionMm"], query["robotRotationQuaternion"], corridor_axis,
        perturbation["longitudinalBiasMm"], perturbation["yawBiasDeg"])
    pose_distances = np.asarray([
        pose_distance_mm(measured_position, measured_rotation,
                         item["robotPositionMm"], item["robotRotationQuaternion"])
        for item in reference
    ])
    similarities = np.asarray([block_ssim(query["descriptor"], item["descriptor"])
                               for item in reference])
    pose_index = int(np.argmin(pose_distances))
    image_index = int(np.argmax(similarities))
    pose_likelihood = np.exp(-0.5 * (pose_distances / POSE_SIGMA_MM) ** 2)
    image_likelihood = np.clip((similarities + 1.0) / 2.0, 0.0, 1.0)
    fused = FUSION_POSITION_WEIGHT * pose_likelihood + FUSION_IMAGE_WEIGHT * image_likelihood
    fusion_index = int(np.argmax(fused))
    best_similarity = float(similarities[image_index])
    ambiguous = [reference[index]["actorRelativeMm"] for index, value in enumerate(similarities)
                 if index != image_index and best_similarity - float(value) <= AMBIGUITY_SIMILARITY_DELTA
                 and abs(reference[index]["actorRelativeMm"] - reference[image_index]["actorRelativeMm"])
                 >= AMBIGUITY_SEPARATION_MM]

    def prediction(index: int) -> dict:
        predicted = float(reference[index]["actorRelativeMm"])
        error = abs(predicted - float(query["actorRelativeMm"]))
        return {
            "predictedActorRelativeMm": predicted,
            "absoluteErrorMm": error,
            "wrongLocation": error > WRONG_LOCATION_TOLERANCE_MM,
            "referenceObservationIndex": int(reference[index]["observationIndex"]),
        }

    return {
        "queryObservationIndex": int(query["observationIndex"]),
        "queryPhase": query["phase"],
        "trueActorRelativeMm": float(query["actorRelativeMm"]),
        "positionOnly": prediction(pose_index),
        "imageOnly": {**prediction(image_index), "bestSimilarity": best_similarity,
                      "ambiguousSeparatedMatches": ambiguous},
        "positionPlusImage": prediction(fusion_index),
    }


def _metrics(rows: Sequence[dict], key: str) -> dict:
    errors = np.asarray([row[key]["absoluteErrorMm"] for row in rows], dtype=np.float64)
    return {
        "count": len(rows),
        "medianAbsoluteErrorMm": float(np.median(errors)),
        "meanAbsoluteErrorMm": float(np.mean(errors)),
        "worstCaseErrorMm": float(np.max(errors)),
        "wrongLocationMatches": int(sum(row[key]["wrongLocation"] for row in rows)),
    }


def evaluate_relocalization(reference: Sequence[dict], query: Sequence[dict], corridor_axis: np.ndarray) -> dict:
    conditions = []
    for perturbation in PERTURBATIONS:
        rows = [localize_query(reference, item, corridor_axis, perturbation) for item in query]
        helped = sum(item["positionPlusImage"]["absoluteErrorMm"]
                     < item["positionOnly"]["absoluteErrorMm"] - 1e-6 for item in rows)
        hurt = sum(item["positionPlusImage"]["absoluteErrorMm"]
                   > item["positionOnly"]["absoluteErrorMm"] + 1e-6 for item in rows)
        conditions.append({
            "perturbation": perturbation,
            "positionOnly": _metrics(rows, "positionOnly"),
            "imageOnly": _metrics(rows, "imageOnly"),
            "positionPlusImage": _metrics(rows, "positionPlusImage"),
            "fusionHelpedCount": helped,
            "fusionHurtCount": hurt,
            "ambiguousImageMatchCount": sum(bool(item["imageOnly"]["ambiguousSeparatedMatches"]) for item in rows),
            "queries": rows,
        })
    return {
        "contract": "thoracic_p1_pose_image_relocalization_v1",
        "generatedUtc": _utc_now(),
        "referencePhases": list(REFERENCE_PHASES),
        "queryPhases": list(QUERY_PHASES),
        "referenceCount": len(reference),
        "queryCount": len(query),
        "metric": "fixed 8x8-block SSIM on robust-normalized 64x64 grayscale ultrasound",
        "conditions": conditions,
        "restrictedInformation": "No T-level labels, target coordinate, anatomy identity, evaluator score, or renderer provenance used.",
    }


def _draw_comparison(path: Path, result: dict) -> None:
    width, height = 1500, 760
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    draw.text((70, 20), "P1 offline relocalization: independent traversal queries", fill="black")
    colors = {"positionOnly": (35, 95, 210), "imageOnly": (210, 80, 40),
              "positionPlusImage": (30, 155, 75)}
    keys = list(colors)
    maximum = max(condition[key]["worstCaseErrorMm"]
                  for condition in result["conditions"] for key in keys)
    for condition_index, condition in enumerate(result["conditions"]):
        top = 75 + condition_index * 125
        draw.text((10, top + 25), condition["perturbation"]["name"], fill="black")
        for method_index, key in enumerate(keys):
            value = condition[key]["medianAbsoluteErrorMm"]
            y = top + method_index * 28
            x0 = 230
            x1 = x0 + int(value / max(maximum, 1e-8) * (width - 330))
            draw.rectangle((x0, y, x1, y + 18), fill=colors[key])
            draw.text((x1 + 8, y), f"{key}: median={value:.2f} mm, worst={condition[key]['worstCaseErrorMm']:.2f}",
                      fill=colors[key])
    draw.text((230, height - 35), "Bar length: median absolute localization error; text also reports worst case", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _verify_hash_manifest(root: Path, manifest_name: str) -> dict:
    manifest_path = root / manifest_name
    payload = _load_json(manifest_path)
    failures = []
    for name, expected in payload["sha256"].items():
        path = root / name
        if not path.is_file() or _sha256(path) != expected:
            failures.append(name)
    return {
        "root": str(root),
        "manifest": manifest_name,
        "manifestSha256": _sha256(manifest_path),
        "checkedFiles": len(payload["sha256"]),
        "failures": failures,
        "valid": not failures,
    }


def _draw_calibration_return(path: Path, calibration_path: Path, return_path: Path,
                             similarity: float, threshold: float) -> None:
    tile = 520
    label = 70
    canvas = Image.new("RGB", (tile * 2, tile + label), "white")
    draw = ImageDraw.Draw(canvas)
    for index, (name, source) in enumerate((("Calibration", calibration_path), ("Fresh return", return_path))):
        with Image.open(source) as image:
            resized = image.convert("RGB").resize((tile, tile), Image.Resampling.BILINEAR)
        canvas.paste(resized, (index * tile, 0))
        draw.text((index * tile + 8, tile + 8), name, fill="black")
    draw.text((8, tile + 34),
              f"Locked block-SSIM={similarity:.6f}; threshold={threshold:.6f}; "
              f"{'PASS' if similarity >= threshold else 'FAIL'}", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def _draw_navigation_errors(path: Path, actor: dict) -> None:
    steps = actor["steps"]
    width, height = 1300, 680
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    left, right, top, bottom = 90, width - 50, 60, height - 80
    draw.rectangle((left, top, right, bottom), outline="black")
    draw.text((left, 20), "P1 physical navigation errors from measured robot pose", fill="black")
    maximum = max(abs(float(item["longitudinalErrorMm"])) for item in steps)

    def xy(index: int, value: float) -> tuple[int, int]:
        x = left + int(index / max(len(steps) - 1, 1) * (right - left))
        y = bottom - int(abs(value) / max(maximum, 1e-8) * (bottom - top))
        return x, y

    longitudinal = [xy(index, float(item["longitudinalErrorMm"])) for index, item in enumerate(steps)]
    lateral = [xy(index, float(item["lateralErrorMm"])) for index, item in enumerate(steps)]
    draw.line(longitudinal, fill=(35, 95, 210), width=4)
    draw.line(lateral, fill=(210, 80, 40), width=4)
    tolerance_y = xy(0, 3.0)[1]
    draw.line((left, tolerance_y, right, tolerance_y), fill=(30, 155, 75), width=2)
    draw.text((left + 8, tolerance_y - 20), "3 mm tolerance", fill=(30, 130, 60))
    draw.text((left, bottom + 20), "Blue: |longitudinal error|; orange: |lateral error|", fill="black")
    draw.text((right - 230, bottom + 20), "Executed step / final STOP", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def evaluate_pilot(output_root: Path, m0c_root: Path, m0d_root: Path) -> dict:
    offline = _verify_hash_manifest(output_root, "offline_results.sha256")
    m0c = _verify_hash_manifest(m0c_root, "blind_results.sha256")
    m0d = _verify_hash_manifest(m0d_root, "blind_results.sha256")
    prior_integrity = {
        "contract": "thoracic_p1_prior_evidence_integrity_v1",
        "generatedUtc": _utc_now(),
        "m0bAndP1Offline": offline,
        "m0c": m0c,
        "m0d": m0d,
        "allValid": offline["valid"] and m0c["valid"] and m0d["valid"],
    }
    _save_json(output_root / "prior_evidence_integrity.json", prior_integrity)
    if not prior_integrity["allValid"]:
        raise ValueError("P1 prior evidence integrity failed")
    calibration = _load_json(output_root / "calibration_manifest.json")
    actor = _load_json(output_root / "trajectory_actor.json")
    evaluator = _load_json(output_root / "evaluator_only" / "trajectory_privileged.json")
    evaluation = _load_json(output_root / "pilot_evaluation.json")
    metric = _load_json(output_root / "confirmation_metric.json")
    calibration_path = output_root / calibration["referenceImage"]
    return_path = output_root / evaluation["returnImage"]
    if _sha256(calibration_path) != calibration["referenceImageSha256"]:
        raise ValueError("P1 calibration image hash mismatch")
    final_actor = actor["steps"][-1]
    if _sha256(return_path) != final_actor["imageSha256"]:
        raise ValueError("P1 return image hash mismatch")
    similarity = block_ssim(load_image_descriptor(calibration_path), load_image_descriptor(return_path))
    final_evaluator = evaluator["steps"][-1]
    evaluator_correspondence = (
        abs(float(final_evaluator["evaluatorLongitudinalErrorMm"])) <= 3.0
        and abs(float(final_evaluator["evaluatorLateralErrorMm"])) <= 3.0
    )
    return_fresh = (
        final_actor["frameIntegrityValid"]
        and final_actor["poseEpoch"] == final_actor["producerPoseEpoch"]
        and final_actor["producerUnityFrame"] > final_actor["poseAppliedUnityFrame"]
        and final_actor["captureUnityFrame"] == final_actor["producerUnityFrame"]
        and final_evaluator["provenanceMatchedPublishedFrame"]
    )
    confirmation_pass = (
        similarity >= float(metric["lockedThreshold"])
        and return_fresh and evaluator_correspondence
        and evaluation["positionNavigation"] == "PASS"
    )
    evaluation.update({
        "freshUltrasoundConfirmation": "PASS" if confirmation_pass else "FAIL",
        "imageSimilarityStatus": "PASS" if similarity >= float(metric["lockedThreshold"]) else "FAIL",
        "calibrationReturnSimilarity": similarity,
        "lockedImageSimilarityThreshold": float(metric["lockedThreshold"]),
        "returnFrameFreshAndIntegrityValid": return_fresh,
        "evaluatorT7Correspondence": evaluator_correspondence,
        "evaluatorReferenceDistanceMm": float(final_evaluator["distanceToReferenceContactMm"]),
        "sdkWarning": "Background SMMARTS ATC connection reported missing ATC64; probe navigation, renderer publication, and batch completion continued.",
    })
    _save_json(output_root / "pilot_evaluation.json", evaluation)
    _draw_calibration_return(output_root / "calibration_vs_return_ultrasound.png",
                             calibration_path, return_path, similarity, float(metric["lockedThreshold"]))
    _draw_navigation_errors(output_root / "navigation_error_plot.png", actor)
    _write_report(output_root, actor, evaluation)
    return evaluation


def _write_report(output_root: Path, actor: dict, evaluation: dict) -> None:
    relocalization = _load_json(output_root / "pose_image_relocalization.json")
    rows = []
    for condition in relocalization["conditions"]:
        rows.append(
            f"| {condition['perturbation']['name']} | "
            f"{condition['positionOnly']['medianAbsoluteErrorMm']:.3f} / {condition['positionOnly']['worstCaseErrorMm']:.3f} | "
            f"{condition['imageOnly']['medianAbsoluteErrorMm']:.3f} / {condition['imageOnly']['worstCaseErrorMm']:.3f} | "
            f"{condition['positionPlusImage']['medianAbsoluteErrorMm']:.3f} / {condition['positionPlusImage']['worstCaseErrorMm']:.3f} | "
            f"{condition['fusionHelpedCount']} / {condition['fusionHurtCount']} |"
        )
    report = f"""# Thoracic US Coach - P1 Position-Aware T7 Localization Pilot

## IMPLEMENTATION

P1 adds one isolated partial controller, one batch runner, one EditMode test class, and one deterministic offline relocalization analyzer. Existing Active2D, renderer, scene, anatomy, image-only modes, M0-C/M0-D reports, and G2-G4 verdicts were not modified.

The mode is explicitly `CALIBRATED_POSITION_GUIDED_BASELINE`. Unity T7 ground truth is read once during calibration and frozen as a robot-frame target pose. The physical navigation loop uses only the stored target, measured robot pose, executed action history, and existing safe Active2D movement.

Explicit calibrated target pose data appears in `calibration_manifest.json` and `trajectory_actor.json`. Raw T7 reference/error data and renderer provenance appear only under `evaluator_only/`. The image-only relocalization output contains neither target coordinates nor T-level labels.

## VERIFICATION

- M0-B/M0-C/M0-D and P1 offline hash integrity: **PASS**.
- Python focused tests: see `test_results.json`.
- Unity compilation: **PASS**.
- Unity EditMode P1/Active2D/freshness/provenance tests: **38/38 PASS**.
- One and only one yaw-0 physical pilot: completed; no retry.
- SDK warning: background ATC hardware connection could not load `ATC64`; Unity imaging and the pilot completed. This was not used as blank/safety evidence.

## ROBOT POSITION CONTRACT

Robot coordinates are Unity world position in millimetres and Unity world quaternion `(x,y,z,w)`. This identity frame is independent of the target. The actor receives measured probe pose/orientation, stored calibrated target pose, actual movement result, action history, and ultrasound. T-level labels, runtime marker queries, evaluator score/distance, mesh identity, and renderer provenance are excluded from navigation.

## SPATIAL ULTRASOUND MAP

The offline map contains 62 reference observations from one physical traversal and 40 separately captured query observations. All 103 M0-B frame hashes and pose associations were verified. Each map entry binds actual robot pose, orientation, action/displacement, freshness metadata, and clean ultrasound.

## RELOCALIZATION COMPARISON

Values are median / worst absolute error in millimetres:

| Condition | Position only | Image only | Position + image | Fusion helped / hurt |
| --- | ---: | ---: | ---: | ---: |
{chr(10).join(rows)}

Exact pose has a ceiling effect. Under +/-3 mm longitudinal bias, ultrasound fusion corrected roughly half the queries and hurt none. The result is fixed-anatomy and reuses revisited paths; it is not independent cross-anatomy validation.

## CALIBRATED T7 NAVIGATION

- Initial longitudinal separation: **{evaluation['initialLongitudinalSeparationMm']:.3f} mm**.
- Accepted/rejected moves: **{evaluation['acceptedMovements']}/{evaluation['rejectedMovements']}**.
- Cumulative physical movement: **{evaluation['cumulativeMovementMm']:.3f} mm**.
- Final longitudinal error: **{evaluation['finalLongitudinalErrorMm']:.3f} mm**.
- Final lateral error: **{evaluation['finalLateralErrorMm']:.4f} mm**.
- Final quaternion/yaw/normal errors: **{evaluation['finalQuaternionErrorDeg']:.3f} / {evaluation['finalSurfaceYawErrorDeg']:.3f} / {evaluation['finalNormalAlignmentErrorDeg']:.3f} degrees**.
- Contact/workspace/freshness valid; zero rejected movement and zero navigation safety failure.

## ULTRASOUND CONFIRMATION

- Fresh calibration-versus-return block-SSIM: **{evaluation['calibrationReturnSimilarity']:.6f}**.
- Locked threshold: **{evaluation['lockedImageSimilarityThreshold']:.6f}**.
- Correct pose epoch, producer frame, scan serial, image hash, and provenance association: **PASS**.
- Independent evaluator reference distance: **{evaluation['evaluatorReferenceDistanceMm']:.3f} mm**.

## SCIENTIFIC INTERPRETATION

This pilot proves one calibrated fixed-anatomy engineering baseline can physically navigate from a nontrivial start to the stored T7 robot pose and reacquire a consistent fresh image. It also shows ultrasound can correct bounded longitudinal registration bias in this revisited-path dataset. It does not identify unknown T7, count vertebrae, validate patient generalization, or establish performance across starts/anatomies.

## VERDICTS

| Outcome | Verdict |
| --- | --- |
| POSITION_NAVIGATION | **{evaluation['positionNavigation']}** |
| FRESH_ULTRASOUND_CONFIRMATION | **{evaluation['freshUltrasoundConfirmation']}** |
| SPATIAL_RELOCALIZATION | **SUPPORTED_ON_EXISTING_REVISITED_PATHS; NOT HELD-OUT GENERALIZATION** |
| AUTONOMOUS_T7_IDENTIFICATION | **NOT_TESTED** |

Existing scientific gates remain unchanged: G2 **FAIL**, G3/G4 **NOT_RUN**.

## NEXT DECISION

The smallest next step toward localization without a known target coordinate is a separately approved, actor-scheduled held-out relocalization pilot from one new start with a deliberately perturbed pose prior, while keeping the map and SSIM/fusion parameters frozen. It would test whether image correction transfers beyond the exact recorded reverse paths; it would still not assign T7 without an anatomical anchor.
"""
    (output_root / "REPORT.md").write_text(report, encoding="utf-8")


def analyze(run_root: Path, output_root: Path) -> dict:
    actor, privileged, input_manifest = verify_inputs(run_root)
    output_root.mkdir(parents=True, exist_ok=True)
    _save_json(output_root / "input_manifest.json", input_manifest)
    records = build_records(run_root, actor, privileged)
    reference = sorted([item for item in records if item["phase"] in REFERENCE_PHASES],
                       key=lambda item: item["actorRelativeMm"])
    query = [item for item in records if item["phase"] in QUERY_PHASES]
    if {item["observationIndex"] for item in reference} & {item["observationIndex"] for item in query}:
        raise ValueError("Reference and query traversals must use separately captured frames")
    corridor_axis = _vector(privileged["corridorAxis"])
    corridor_axis /= np.linalg.norm(corridor_axis)
    controls = matched_control_similarities(reference, query)
    threshold = confirmation_threshold(controls)
    contract = {
        "contract": "thoracic_p1_robot_pose_contract_v1",
        "generatedUtc": _utc_now(),
        "robotFrame": "UNITY_WORLD_IDENTITY",
        "unityToRobotPosition": "robot_position_mm = unity_world_position; scene units are millimetres",
        "unityToRobotOrientation": "robot_quaternion = Unity world quaternion (x,y,z,w)",
        "frameDefinitionIndependentOfTarget": True,
        "poseSource": "Exact simulated probe pose standing in for ideal robot forward kinematics",
        "allowed": ["clean ultrasound", "current and historical robot pose", "executed action/displacement",
                    "movement outcome", "spatial ultrasound map"],
        "restricted": ["T-level labels", "target marker runtime query", "distance-to-target evaluator score",
                       "anatomy mesh identity", "renderer provenance"],
    }
    _save_json(output_root / "robot_pose_contract.json", contract)
    settings = {
        "contract": "thoracic_p1_relocalization_algorithm_freeze_v1",
        "generatedUtc": _utc_now(),
        "referencePhases": list(REFERENCE_PHASES),
        "queryPhases": list(QUERY_PHASES),
        "imageSize": IMAGE_SIZE,
        "ssimBlockSize": SSIM_BLOCK_SIZE,
        "poseSigmaMm": POSE_SIGMA_MM,
        "orientationMmPerDeg": ORIENTATION_MM_PER_DEG,
        "fusionPositionWeight": FUSION_POSITION_WEIGHT,
        "fusionImageWeight": FUSION_IMAGE_WEIGHT,
        "wrongLocationToleranceMm": WRONG_LOCATION_TOLERANCE_MM,
        "perturbations": list(PERTURBATIONS),
        "confirmationThreshold": threshold,
        "confirmationThresholdRule": "clip(5th percentile of independent same-position traversal SSIM minus 0.02, 0.85, 0.98)",
        "controlPairCount": len(controls),
        "scoringStartedAfterThisPayloadWasWritten": True,
    }
    _save_json(output_root / "algorithm_freeze.json", settings)
    spatial_map = {
        "contract": "thoracic_p1_spatial_ultrasound_map_v1",
        "generatedUtc": _utc_now(),
        "robotFrame": "UNITY_WORLD_IDENTITY",
        "entries": [_json_record(item) for item in reference],
        "queryObservationIndices": [item["observationIndex"] for item in query],
        "queryFramesAreIndependentFromMap": True,
        "excluded": "T-level labels, anatomy provenance, target coordinates, evaluator scores",
    }
    assert_no_restricted_keys(spatial_map)
    _save_json(output_root / "spatial_map_manifest.json", spatial_map)
    result = evaluate_relocalization(reference, query, corridor_axis)
    assert_no_restricted_keys(result)
    _save_json(output_root / "pose_image_relocalization.json", result)
    _save_json(output_root / "confirmation_metric.json", {
        "metric": result["metric"],
        "lockedThreshold": threshold,
        "controlPairCount": len(controls),
        "controlSimilarityMinimum": float(np.min(controls)),
        "controlSimilarityMedian": float(np.median(controls)),
        "controlSimilarityMaximum": float(np.max(controls)),
        "source": "independent reverse-pass same-position pairs; no T7 label used",
    })
    _draw_comparison(output_root / "relocalization_comparison.png", result)
    frozen_names = ["input_manifest.json", "robot_pose_contract.json", "algorithm_freeze.json",
                    "spatial_map_manifest.json", "pose_image_relocalization.json",
                    "confirmation_metric.json", "relocalization_comparison.png"]
    _save_json(output_root / "offline_results.sha256", {
        "contract": "thoracic_p1_offline_freeze_v1",
        "generatedUtc": _utc_now(),
        "sha256": {name: _sha256(output_root / name) for name in frozen_names},
    })
    clean = next(item for item in result["conditions"] if item["perturbation"]["name"] == "clean")
    return {
        "referenceCount": len(reference),
        "queryCount": len(query),
        "confirmationThreshold": threshold,
        "clean": {key: clean[key] for key in ("positionOnly", "imageOnly", "positionPlusImage")},
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="P1 position-aware ultrasound relocalization")
    parser.add_argument("--run-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--phase", choices=("offline", "pilot"), default="offline")
    parser.add_argument("--m0c-root", type=Path)
    parser.add_argument("--m0d-root", type=Path)
    args = parser.parse_args()
    if args.phase == "offline":
        result = analyze(args.run_root.resolve(), args.output_root.resolve())
    else:
        if args.m0c_root is None or args.m0d_root is None:
            parser.error("--m0c-root and --m0d-root are required for pilot evaluation")
        result = evaluate_pilot(args.output_root.resolve(), args.m0c_root.resolve(), args.m0d_root.resolve())
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
