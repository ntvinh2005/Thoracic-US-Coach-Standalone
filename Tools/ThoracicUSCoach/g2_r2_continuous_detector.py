"""Blind-first continuous M6 recess detector and evaluator-only scorer."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
from typing import Any, Iterable

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw

from anatomical_waveform_analysis import measure_bilateral_structure, measure_central_shadow
from relative_spine_e2_analysis import cosine_distance, load_gray, morphology_descriptor, sha256


LEVELS = [f"T{number}" for number in range(2, 9)]
BLIND_OUTPUTS = (
    "anatomical_waveform.csv",
    "event_candidates.json",
    "event_tracking_diagnostics.json",
    "event_rejection_reasons.json",
    "relative_spine_map.json",
    "relative_spine_map.csv",
    "mapping_uncertainty.json",
)
FORBIDDEN_ACTOR_KEYS = {
    "probePosition", "probeRotation", "contactPoint", "surfaceNormal",
    "evaluatorLongitudinalCoordinateMm", "provenance", "level", "meshIdentity",
    "targetDistance", "targetPose", "t7", "lfBoundsCenter",
}
GRID_STEP_MM = 1.5
MOTIF_RADIUS_MM = 6.0
REVERSE_MATCH_TOLERANCE_MM = 3.0
MINIMUM_EVENT_SEPARATION_MM = 7.5
MINIMUM_SCORE_ABSOLUTE = 0.012
THRESHOLD_MAD_MULTIPLIER = 1.5


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def robust_z(values: np.ndarray) -> np.ndarray:
    values = np.asarray(values, dtype=np.float64)
    median = np.nanmedian(values, axis=0)
    mad = np.nanmedian(np.abs(values - median), axis=0)
    scale = np.where(mad > 1e-8, 1.4826 * mad, np.nanstd(values, axis=0))
    scale = np.where(scale > 1e-8, scale, 1.0)
    return np.nan_to_num((values - median) / scale)


def _check_forbidden_keys(value: Any, location: str = "root") -> None:
    if isinstance(value, dict):
        for key, item in value.items():
            if key in FORBIDDEN_ACTOR_KEYS:
                raise ValueError(f"Actor input contains forbidden key {key} at {location}")
            _check_forbidden_keys(item, f"{location}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            _check_forbidden_keys(item, f"{location}[{index}]")


def validate_actor_trace(root: Path, actor: dict[str, Any]) -> list[dict[str, Any]]:
    _check_forbidden_keys(actor)
    observations = actor.get("observations", [])
    if not observations:
        raise ValueError("Actor trace has no observations")
    reconstructed = float(actor.get("initialActorRelativeMm", 0.0))
    reconstructed_lateral = float(actor.get("initialActorLateralMm", 0.0))
    serials: set[int] = set()
    hashes: set[str] = set()
    for index, observation in enumerate(observations):
        if observation["observationIndex"] != index:
            raise ValueError("Actor observation indices are not contiguous")
        action = observation["requestedAction"].upper()
        if observation["executedSuccessfully"] and action == "CAUDAL":
            reconstructed += float(observation["achievedDisplacementMm"])
        elif observation["executedSuccessfully"] and action == "CRANIAL":
            reconstructed -= float(observation["achievedDisplacementMm"])
        elif observation["executedSuccessfully"] and action == "LATERAL":
            reconstructed_lateral += float(observation["achievedDisplacementMm"])
        elif observation["executedSuccessfully"] and action == "MEDIAL":
            reconstructed_lateral -= float(observation["achievedDisplacementMm"])
        if observation.get("phase") == "home_arrival_baseline":
            if abs(reconstructed) > 0.05 or abs(reconstructed_lateral) > 0.05:
                raise ValueError(
                    f"Home-origin reset exceeds return tolerance at observation {index}"
                )
            reconstructed = 0.0
            reconstructed_lateral = 0.0
        if abs(reconstructed - float(observation["signedActorRelativeMm"])) > 1e-3:
            raise ValueError(f"Relative-coordinate integration mismatch at observation {index}")
        if abs(reconstructed_lateral
               - float(observation.get("signedActorLateralMm", 0.0))) > 1e-3:
            raise ValueError(f"Lateral-coordinate integration mismatch at observation {index}")
        if not all(observation[key] for key in (
            "frameIntegrityValid", "contactValidAfterExecution", "workspaceValidAfterExecution"
        )):
            raise ValueError(f"Invalid actor observation {index}")
        if not (
            observation["poseEpoch"] == observation["producerPoseEpoch"]
            and observation["producerUnityFrame"] > observation["poseAppliedUnityFrame"]
            and observation["captureUnityFrame"] == observation["producerUnityFrame"]
            and observation["producerScanSerial"] > 0
            and observation["activeAggregatorCount"] == 0
        ):
            raise ValueError(f"Stale or mismatched actor observation {index}")
        image_path = root / observation["image"]
        if not image_path.is_file() or sha256(image_path) != observation["imageSha256"]:
            raise ValueError(f"Image identity mismatch at observation {index}")
        serial = int(observation["producerScanSerial"])
        if serial in serials:
            raise ValueError(f"Repeated scan serial {serial}")
        serials.add(serial)
        hashes.add(observation["imageSha256"])
    if len(hashes) != len(observations):
        raise ValueError("Continuous sweep contains repeated image hashes")
    return observations


def image_features(path: Path) -> tuple[np.ndarray, np.ndarray, dict[str, float]]:
    image = load_gray(path)
    shadow = measure_central_shadow(image)
    bilateral = measure_bilateral_structure(image)
    gy, gx = np.gradient(image)
    gradient = np.hypot(gx, gy)
    scalars = {
        "mean_intensity": float(np.mean(image)),
        "pixel_variance": float(np.var(image)),
        "gradient_energy": float(np.mean(gradient)),
        "edge_coverage": float(np.mean(gradient > max(float(np.median(gradient) * 2.5), 0.025))),
        "shadow_width": float(shadow["widthMean"] if shadow["valid"] else np.nan),
        "shadow_centroid_offset": float(shadow["centroidOffset"] if shadow["valid"] else np.nan),
        "shadow_taper": float(shadow["taper"] if shadow["valid"] else np.nan),
        "bilateral_symmetry": float(bilateral["symmetry"] if bilateral["valid"] else np.nan),
        "bilateral_coverage_asymmetry": float(
            bilateral["coverageAsymmetry"] if bilateral["valid"] else np.nan
        ),
    }
    return image, morphology_descriptor(image), scalars


def extract_observations(root: Path, observations: list[dict[str, Any]]) -> list[dict[str, Any]]:
    extracted = []
    for observation in observations:
        _, descriptor, scalars = image_features(root / observation["image"])
        extracted.append({
            "observationIndex": observation["observationIndex"],
            "phase": observation["phase"],
            "coordinateMm": float(observation["signedActorRelativeMm"]),
            "image": observation["image"],
            "imageSha256": observation["imageSha256"],
            "descriptor": descriptor,
            **scalars,
        })
    return extracted


def build_spatial_pass(rows: list[dict[str, Any]], phases: Iterable[str]) -> list[dict[str, Any]]:
    selected = [row for row in rows if row["phase"] in set(phases)]
    selected.sort(key=lambda row: row["coordinateMm"])
    merged: list[dict[str, Any]] = []
    for row in selected:
        if merged and abs(row["coordinateMm"] - merged[-1]["coordinateMm"]) < 0.25:
            continue
        merged.append(row)
    return merged


def motif_scores(rows: list[dict[str, Any]], radius_mm: float = MOTIF_RADIUS_MM) -> np.ndarray:
    coordinates = np.asarray([row["coordinateMm"] for row in rows])
    descriptors = [row["descriptor"] for row in rows]
    scalar_names = (
        "shadow_width", "shadow_centroid_offset", "shadow_taper",
        "bilateral_symmetry", "bilateral_coverage_asymmetry",
        "mean_intensity", "pixel_variance", "gradient_energy", "edge_coverage",
    )
    scalar_values = np.asarray([[row[name] for name in scalar_names] for row in rows])
    scalar_values = robust_z(scalar_values)
    scores = np.zeros(len(rows), dtype=np.float64)
    for index, coordinate in enumerate(coordinates):
        left_candidates = np.where(coordinates <= coordinate - radius_mm)[0]
        right_candidates = np.where(coordinates >= coordinate + radius_mm)[0]
        if not len(left_candidates) or not len(right_candidates):
            continue
        left = left_candidates[-1]
        right = right_candidates[0]
        descriptor_novelty = 0.5 * (
            cosine_distance(descriptors[index], descriptors[left])
            + cosine_distance(descriptors[index], descriptors[right])
        ) - 0.25 * cosine_distance(descriptors[left], descriptors[right])
        scalar_novelty = 0.5 * (
            np.linalg.norm(scalar_values[index] - scalar_values[left])
            + np.linalg.norm(scalar_values[index] - scalar_values[right])
        ) - 0.25 * np.linalg.norm(scalar_values[left] - scalar_values[right])
        scores[index] = max(0.0, float(descriptor_novelty + 0.025 * scalar_novelty))
    return scores


def detect_local_events(
    rows: list[dict[str, Any]], scores: np.ndarray,
    minimum_separation_mm: float = MINIMUM_EVENT_SEPARATION_MM,
) -> tuple[list[dict[str, Any]], float]:
    coordinates = np.asarray([row["coordinateMm"] for row in rows])
    positive = scores[scores > 0]
    median = float(np.median(positive)) if len(positive) else 0.0
    mad = float(np.median(np.abs(positive - median))) if len(positive) else 0.0
    threshold = median + max(MINIMUM_SCORE_ABSOLUTE, THRESHOLD_MAD_MULTIPLIER * 1.4826 * mad)
    raw = []
    for index in range(2, len(scores) - 2):
        if scores[index] < threshold or scores[index] != np.max(scores[index - 2:index + 3]):
            continue
        half_height = 0.5 * scores[index]
        left = index
        right = index
        while left > 0 and scores[left - 1] >= half_height:
            left -= 1
        while right + 1 < len(scores) and scores[right + 1] >= half_height:
            right += 1
        raw.append({
            "coordinateMm": float(coordinates[index]),
            "score": float(scores[index]),
            "supportIntervalMm": [float(coordinates[left]), float(coordinates[right])],
            "supportingFrame": rows[index]["observationIndex"],
            "supportingImage": rows[index]["image"],
        })
    accepted = []
    for event in sorted(raw, key=lambda item: item["score"], reverse=True):
        if all(abs(event["coordinateMm"] - kept["coordinateMm"]) >= minimum_separation_mm
               for kept in accepted):
            accepted.append(event)
    accepted.sort(key=lambda item: item["coordinateMm"])
    return accepted, threshold


def consolidate_reverse_consensus(
    outbound: list[dict[str, Any]], repeat: list[dict[str, Any]],
    tolerance_mm: float = REVERSE_MATCH_TOLERANCE_MM,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    candidates: list[dict[str, Any]] = []
    rejected: list[dict[str, Any]] = []
    remaining = set(range(len(repeat)))
    for forward in outbound:
        matches = sorted(
            ((index, abs(forward["coordinateMm"] - repeat[index]["coordinateMm"]))
             for index in remaining), key=lambda item: item[1]
        )
        if not matches or matches[0][1] > tolerance_mm:
            rejected.append({**forward, "source": "OUTBOUND", "reason": "NO_REVERSE_SUPPORT"})
            continue
        index, disagreement = matches[0]
        remaining.remove(index)
        reverse = repeat[index]
        score = min(forward["score"], reverse["score"])
        coordinate = (
            forward["coordinateMm"] * forward["score"]
            + reverse["coordinateMm"] * reverse["score"]
        ) / max(forward["score"] + reverse["score"], 1e-12)
        candidates.append({
            "candidateId": f"E{len(candidates) + 1:02d}",
            "actorRelativeCoordinateMm": float(coordinate),
            "uncertaintyIntervalMm": [
                float(min(forward["supportIntervalMm"][0], reverse["supportIntervalMm"][0])),
                float(max(forward["supportIntervalMm"][1], reverse["supportIntervalMm"][1])),
            ],
            "score": float(score),
            "forwardReverseDisagreementMm": float(disagreement),
            "supportingFrameIndices": [
                forward["supportingFrame"], reverse["supportingFrame"]
            ],
            "supportingImages": [forward["supportingImage"], reverse["supportingImage"]],
            "eventType": "LOCAL_A_TO_RECESS_MORPHOLOGY_TO_A_CANDIDATE",
            "accepted": True,
            "acceptReason": "LOCAL_MORPHOLOGY_NOVELTY_WITH_INDEPENDENT_REVERSE_SUPPORT",
        })
    for index in sorted(remaining):
        rejected.append({**repeat[index], "source": "REVERSE", "reason": "NO_OUTBOUND_SUPPORT"})
    candidates.sort(key=lambda item: item["actorRelativeCoordinateMm"])
    for index, candidate in enumerate(candidates):
        candidate["candidateId"] = f"E{index + 1:02d}"
    return candidates, rejected


def build_relative_map(candidates: list[dict[str, Any]]) -> dict[str, Any]:
    exactly_seven = len(candidates) == 7
    levels = []
    for ordinal, level in enumerate(LEVELS, start=1):
        candidate = candidates[ordinal - 1] if exactly_seven else None
        levels.append({
            "ordinal": ordinal,
            "inferredLevel": level,
            "centerRelativeMm": candidate["actorRelativeCoordinateMm"] if candidate else None,
            "uncertaintyIntervalMm": candidate["uncertaintyIntervalMm"] if candidate else None,
            "supportingFrames": candidate["supportingFrameIndices"] if candidate else [],
            "eventType": candidate["eventType"] if candidate else "UNRESOLVED",
            "accepted": bool(candidate),
            "assignmentBasis": (
                "BLIND_IMAGE_EVENT_ORDER_PLUS_APPROVED_SEVEN_LEVEL_PRIOR"
                if candidate else "INSUFFICIENT_OR_AMBIGUOUS_EVENT_COUNT"
            ),
        })
    return {
        "contract": "thoracic_g2_r2_relative_spine_map_v1",
        "homeRelativeOriginMm": 0.0,
        "coordinateSource": "accepted_actual_achieved_relative_motion",
        "coveragePrior": "exactly_seven_consecutive_levels_T2_to_T8",
        "directionConvention": "cranial_negative_caudad_positive",
        "eventCount": len(candidates),
        "levels": levels,
        "predictedT7RelativeMm": levels[5]["centerRelativeMm"] if exactly_seven else None,
        "overallMapStatus": "ACCEPTED_BLIND_HYPOTHESIS" if exactly_seven else "INCONCLUSIVE",
        "abstentionReason": None if exactly_seven else (
            f"Expected seven independently supported events; observed {len(candidates)}"
        ),
    }


def write_waveform_csv(path: Path, outbound: list[dict[str, Any]], repeat: list[dict[str, Any]],
                       outbound_scores: np.ndarray, repeat_scores: np.ndarray) -> None:
    names = (
        "mean_intensity", "pixel_variance", "gradient_energy", "edge_coverage",
        "shadow_width", "shadow_centroid_offset", "shadow_taper",
        "bilateral_symmetry", "bilateral_coverage_asymmetry",
    )
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=(
            "pass", "observation_index", "actor_relative_mm", "motif_score", *names
        ))
        writer.writeheader()
        for pass_name, rows, scores in (
            ("OUTBOUND", outbound, outbound_scores), ("REPEAT", repeat, repeat_scores)
        ):
            for row, score in zip(rows, scores):
                writer.writerow({
                    "pass": pass_name,
                    "observation_index": row["observationIndex"],
                    "actor_relative_mm": row["coordinateMm"],
                    "motif_score": score,
                    **{name: row[name] for name in names},
                })


def draw_waveform(path: Path, outbound: list[dict[str, Any]], repeat: list[dict[str, Any]],
                  outbound_scores: np.ndarray, repeat_scores: np.ndarray,
                  candidates: list[dict[str, Any]], thresholds: tuple[float, float]) -> None:
    figure, axes = plt.subplots(3, 1, figsize=(15, 11), sharex=True, constrained_layout=True)
    for rows, scores, name in (
        (outbound, outbound_scores, "outbound"), (repeat, repeat_scores, "reverse/revisit")
    ):
        x = [row["coordinateMm"] for row in rows]
        axes[0].plot(x, scores, label=name)
        axes[1].plot(x, [row["shadow_width"] for row in rows], label=name)
        axes[2].plot(x, [row["gradient_energy"] for row in rows], label=name)
    axes[0].axhline(thresholds[0], color="tab:blue", linestyle="--", alpha=0.5)
    axes[0].axhline(thresholds[1], color="tab:orange", linestyle="--", alpha=0.5)
    for candidate in candidates:
        for axis in axes:
            axis.axvline(candidate["actorRelativeCoordinateMm"], color="tab:red", alpha=0.25)
    axes[0].set_ylabel("Local A-recess-A motif score")
    axes[1].set_ylabel("Central shadow width")
    axes[2].set_ylabel("Gradient energy")
    axes[2].set_xlabel("Actor-relative longitudinal displacement from fixed Home (mm)")
    for axis in axes:
        axis.grid(alpha=0.25)
        axis.legend()
    figure.suptitle("G2-R2 blind image-derived continuous M6 waveform")
    figure.savefig(path, dpi=170)
    plt.close(figure)


def draw_contact_sheet(root: Path, candidates: list[dict[str, Any]], output: Path) -> None:
    if not candidates:
        image = Image.new("RGB", (1000, 180), "white")
        ImageDraw.Draw(image).text((20, 70), "NO REVERSE-SUPPORTED BLIND EVENT CANDIDATES", fill="black")
        image.save(output)
        return
    tiles = []
    for candidate in candidates:
        pair = []
        for image_path in candidate["supportingImages"]:
            with Image.open(root / image_path) as source:
                pair.append(source.convert("RGB").resize((256, 256)))
        tile = Image.new("RGB", (512, 300), "white")
        tile.paste(pair[0], (0, 0))
        tile.paste(pair[1], (256, 0))
        ImageDraw.Draw(tile).text(
            (8, 270),
            f"{candidate['candidateId']} {candidate['actorRelativeCoordinateMm']:.2f} mm",
            fill="black",
        )
        tiles.append(tile)
    sheet = Image.new("RGB", (512, 300 * len(tiles)), "white")
    for index, tile in enumerate(tiles):
        sheet.paste(tile, (0, 300 * index))
    sheet.save(output)


def write_map_csv(path: Path, relative_map: dict[str, Any]) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=(
            "ordinal", "inferred_level", "center_relative_mm", "uncertainty_low_mm",
            "uncertainty_high_mm", "accepted", "event_type"
        ))
        writer.writeheader()
        for level in relative_map["levels"]:
            interval = level["uncertaintyIntervalMm"] or (None, None)
            writer.writerow({
                "ordinal": level["ordinal"], "inferred_level": level["inferredLevel"],
                "center_relative_mm": level["centerRelativeMm"],
                "uncertainty_low_mm": interval[0], "uncertainty_high_mm": interval[1],
                "accepted": level["accepted"], "event_type": level["eventType"],
            })


def freeze_outputs(root: Path, source_path: Path) -> str:
    entries = [(name, sha256(root / name)) for name in BLIND_OUTPUTS]
    entries.append((source_path.as_posix(), sha256(source_path)))
    text = "".join(f"{digest}  {name}\n" for name, digest in entries)
    (root / "blind_results.sha256").write_text(text, encoding="utf-8")
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def verify_freeze(root: Path, source_path: Path) -> None:
    lines = (root / "blind_results.sha256").read_text(encoding="utf-8").splitlines()
    expected = {name: digest for digest, name in (line.split("  ", 1) for line in lines)}
    for name in BLIND_OUTPUTS:
        if expected.get(name) != sha256(root / name):
            raise ValueError(f"Blind output changed after freeze: {name}")
    if expected.get(source_path.as_posix()) != sha256(source_path):
        raise ValueError("Detector source changed after blind freeze")


def run_blind(root: Path, source_path: Path) -> dict[str, Any]:
    actor = read_json(root / "trajectory_actor.json")
    observations = validate_actor_trace(root, actor)
    rows = extract_observations(root, observations)
    outbound = build_spatial_pass(rows, ("cranial_outbound", "forward_baseline", "caudal_outbound"))
    repeat = build_spatial_pass(rows, ("cranial_return", "caudal_return"))
    if min(len(outbound), len(repeat)) < 60:
        raise ValueError("Insufficient two-direction spatial coverage for continuous event detection")
    outbound_scores = motif_scores(outbound)
    repeat_scores = motif_scores(repeat)
    outbound_events, outbound_threshold = detect_local_events(outbound, outbound_scores)
    repeat_events, repeat_threshold = detect_local_events(repeat, repeat_scores)
    candidates, rejected = consolidate_reverse_consensus(outbound_events, repeat_events)
    relative_map = build_relative_map(candidates)
    write_waveform_csv(root / "anatomical_waveform.csv", outbound, repeat,
                       outbound_scores, repeat_scores)
    draw_waveform(root / "anatomical_waveform.png", outbound, repeat,
                  outbound_scores, repeat_scores, candidates,
                  (outbound_threshold, repeat_threshold))
    draw_contact_sheet(root, candidates, root / "event_support_contact_sheet.png")
    write_json(root / "event_candidates.json", {
        "contract": "thoracic_g2_r2_blind_event_candidates_v1",
        "inputIsolation": "Pixels and actor-relative executed motion only; evaluator data not read.",
        "candidateCount": len(candidates),
        "candidates": candidates,
    })
    write_json(root / "event_tracking_diagnostics.json", {
        "contract": "thoracic_g2_r2_event_tracking_diagnostics_v1",
        "physicalInterpretation": (
            "A candidate is a localized descriptor-and-morphology departure whose flanking images are more "
            "similar to each other than to its center, consistent with an A-to-recess-to-A transition."
        ),
        "parameters": {
            "gridStepMm": GRID_STEP_MM, "motifRadiusMm": MOTIF_RADIUS_MM,
            "reverseMatchToleranceMm": REVERSE_MATCH_TOLERANCE_MM,
            "minimumEventSeparationMm": MINIMUM_EVENT_SEPARATION_MM,
            "thresholdMadMultiplier": THRESHOLD_MAD_MULTIPLIER,
            "minimumAbsoluteScore": MINIMUM_SCORE_ABSOLUTE,
        },
        "outboundObservationCount": len(outbound),
        "repeatObservationCount": len(repeat),
        "outboundRawEventCount": len(outbound_events),
        "repeatRawEventCount": len(repeat_events),
        "outboundThreshold": outbound_threshold,
        "repeatThreshold": repeat_threshold,
        "outboundEvents": outbound_events,
        "repeatEvents": repeat_events,
    })
    write_json(root / "event_rejection_reasons.json", {
        "contract": "thoracic_g2_r2_event_rejections_v1", "rejected": rejected,
    })
    write_json(root / "relative_spine_map.json", relative_map)
    write_map_csv(root / "relative_spine_map.csv", relative_map)
    write_json(root / "mapping_uncertainty.json", {
        "contract": "thoracic_g2_r2_mapping_uncertainty_v1",
        "candidateCount": len(candidates),
        "coordinateIntervals": {
            candidate["candidateId"]: candidate["uncertaintyIntervalMm"] for candidate in candidates
        },
        "mapAccepted": relative_map["overallMapStatus"] == "ACCEPTED_BLIND_HYPOTHESIS",
        "criticalAmbiguityRule": (
            "Any count other than seven, including an eighth reverse-supported recess, causes abstention; "
            "no candidate may be removed using evaluator coordinates."
        ),
    })
    blind_hash = freeze_outputs(root, source_path)
    return {
        "candidateCount": len(candidates),
        "mapStatus": relative_map["overallMapStatus"],
        "predictedT7RelativeMm": relative_map["predictedT7RelativeMm"],
        "blindManifestSha256": blind_hash,
    }


def _interpolate_relative_from_world_z(
    z_value: float, actor: dict[str, Any], evaluator: dict[str, Any]
) -> float:
    pairs = sorted(
        (float(item["contactPoint"]["z"]), float(actor["observations"][item["observationIndex"]]["signedActorRelativeMm"]))
        for item in evaluator["observations"] if item["phase"] in (
            "cranial_outbound", "forward_baseline", "caudal_outbound"
        )
    )
    z = np.asarray([item[0] for item in pairs])
    s = np.asarray([item[1] for item in pairs])
    order = np.argsort(z)
    return float(np.interp(z_value, z[order], s[order]))


def ordered_match(predicted: list[float], references: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows = []
    for index, reference in enumerate(references):
        prediction = predicted[index] if index < len(predicted) else None
        error = abs(prediction - reference["coordinateMm"]) if prediction is not None else None
        rows.append({
            "level": reference["level"], "referenceCoordinateMm": reference["coordinateMm"],
            "toleranceMm": reference["toleranceMm"], "predictedCoordinateMm": prediction,
            "absoluteErrorMm": error,
            "matched": bool(error is not None and error <= reference["toleranceMm"]),
        })
    return rows


def run_evaluator(root: Path, r1_root: Path, source_path: Path) -> dict[str, Any]:
    verify_freeze(root, source_path)
    actor = read_json(root / "trajectory_actor.json")
    evaluator = read_json(root / "evaluator_only" / "trajectory_privileged.json")
    relative_map = read_json(root / "relative_spine_map.json")
    reference_map = read_json(r1_root / "bone_structure_reference_map.json")
    landmarks = reference_map["validatedAnatomyDerivedLandmarks"]
    references = []
    for landmark in landmarks:
        references.append({
            "level": landmark["level"],
            "coordinateMm": _interpolate_relative_from_world_z(
                float(landmark["ligamentBoundsCenter"]["z"]), actor, evaluator
            ),
        })
    references.sort(key=lambda item: item["coordinateMm"])
    for index, reference in enumerate(references):
        neighbors = []
        if index > 0:
            neighbors.append(reference["coordinateMm"] - references[index - 1]["coordinateMm"])
        if index + 1 < len(references):
            neighbors.append(references[index + 1]["coordinateMm"] - reference["coordinateMm"])
        reference["toleranceMm"] = min(5.0, 0.25 * min(abs(value) for value in neighbors))
    predicted = [
        level["centerRelativeMm"] for level in relative_map["levels"]
        if level["accepted"] and level["centerRelativeMm"] is not None
    ]
    matches = ordered_match(predicted, references) if len(predicted) == 7 else ordered_match([], references)
    true_positive = sum(item["matched"] for item in matches)
    false_negative = 7 - true_positive
    false_positive = max(0, len(predicted) - true_positive)
    precision = true_positive / max(true_positive + false_positive, 1)
    recall = true_positive / 7.0
    f1 = 2 * precision * recall / max(precision + recall, 1e-12)
    geometry = read_json(r1_root / "geometry_gap_candidates.json")
    extra = next(item for item in geometry["multiOffsetCandidates"]
                 if abs(float(item["centerZMm"]) - 33.5) < 1e-6)
    extra_coordinate = _interpolate_relative_from_world_z(float(extra["centerZMm"]), actor, evaluator)
    blind_candidates = read_json(root / "event_candidates.json")["candidates"]
    near_extra = [item for item in blind_candidates
                  if abs(item["actorRelativeCoordinateMm"] - extra_coordinate) <= 5.0]
    exact_sequence = len(predicted) == 7 and true_positive == 7
    t7_error = matches[5]["absoluteErrorMm"] if exact_sequence else None
    result = {
        "contract": "thoracic_g2_r2_evaluator_anatomical_correspondence_v1",
        "blindFreezeVerified": True,
        "referenceSemantics": "Validated LF-associated posterior bone recess regions; not vertebral-body centers.",
        "references": references,
        "orderedMatches": matches,
        "metrics": {
            "truePositive": true_positive, "falsePositive": false_positive,
            "falseNegative": false_negative, "precision": precision,
            "recall": recall, "f1": f1, "exactSevenEventSequence": exact_sequence,
            "t7RelativeCoordinateErrorMm": t7_error,
        },
        "g2R2": "PASS" if exact_sequence and f1 >= 0.95 else "FAIL",
        "g3R2": "PASS" if exact_sequence else "NOT_RUN",
    }
    write_json(root / "evaluator_only" / "anatomical_correspondence.json", result)
    negative = {
        "contract": "thoracic_g2_r2_locked_extra_recess_control_v1",
        "revealedOnlyAfterBlindFreeze": True,
        "extraGeometryCandidateWorldZMm": extra["centerZMm"],
        "extraCandidateActorRelativeMm": extra_coordinate,
        "blindCandidatesWithinFiveMm": [item["candidateId"] for item in near_extra],
        "incorrectlyAcceptedAsVertebralLevel": bool(
            near_extra and relative_map["overallMapStatus"] == "ACCEPTED_BLIND_HYPOTHESIS"
        ),
        "actorWasToldExtraCandidateCoordinate": False,
    }
    write_json(root / "evaluator_only" / "negative_control_results.json", negative)
    return {"metrics": result["metrics"], "g2R2": result["g2R2"], "g3R2": result["g3R2"],
            "negativeControl": negative}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", choices=("blind", "evaluate"), required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--r1-root", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    source = Path(__file__).resolve()
    if args.stage == "blind":
        result = run_blind(root, source)
    else:
        if args.r1_root is None:
            parser.error("--r1-root is required for evaluator stage")
        result = run_evaluator(root, args.r1_root.resolve(), source)
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


