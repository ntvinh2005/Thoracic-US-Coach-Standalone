from __future__ import annotations

import argparse
import csv
import hashlib
import json
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image, ImageDraw

from g2_r2_features import normalized_grayscale


OFFSETS_MM = (-6.0, -3.0, 0.0, 3.0, 6.0)
FORBIDDEN_FEATURE_FIELDS = {
    "observationIndex",
    "profileToken",
    "cumulativeLongitudinalTravelMm",
    "diagnosticReferenceLevel",
    "corridorCoordinateMm",
    "image",
    "imageSha256",
}


@dataclass(frozen=True)
class ImageFeature:
    values: tuple[float, ...]


FEATURE_NAMES = (
    "left_interface",
    "center_interface",
    "right_interface",
    "left_shadow",
    "center_shadow",
    "right_shadow",
    "left_depth",
    "center_depth",
    "right_depth",
    "left_bright_coverage",
    "center_bright_coverage",
    "right_bright_coverage",
    "interface_lr_asymmetry",
    "shadow_lr_asymmetry",
    "shadow_column_fraction",
    "shadow_column_centroid",
)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def extract_image_features(image_path: Path) -> ImageFeature:
    values = normalized_grayscale(image_path, 256).astype(np.float64)
    height, width = values.shape
    roi = values[int(0.15 * height) : int(0.90 * height), int(0.08 * width) : int(0.92 * width)]
    p99 = max(float(np.quantile(roi, 0.99)), 1e-4)
    normalized = np.clip(roi / p99, 0.0, 1.0)
    thirds = np.array_split(normalized, 3, axis=1)
    interfaces: list[float] = []
    shadows: list[float] = []
    depths: list[float] = []
    bright_coverages: list[float] = []
    column_shadow_scores: list[float] = []
    for third in thirds:
        positive_gradient = np.maximum(np.diff(third, axis=0), 0.0)
        start = int(0.10 * positive_gradient.shape[0])
        end = int(0.82 * positive_gradient.shape[0])
        searchable = positive_gradient[start:end]
        peak_rows = np.argmax(searchable, axis=0) + start
        peak_values = np.max(searchable, axis=0)
        threshold = max(0.06, float(np.quantile(peak_values, 0.55)))
        valid = peak_values >= threshold
        interfaces.append(float(np.mean(peak_values[valid])) if np.any(valid) else 0.0)
        depths.append(
            float(np.mean(peak_rows[valid]) / max(1, third.shape[0] - 1))
            if np.any(valid)
            else 1.0
        )
        per_column: list[float] = []
        for column, row in enumerate(peak_rows):
            if not valid[column]:
                per_column.append(0.0)
                continue
            below = third[min(row + 8, third.shape[0] - 1) :, column]
            score = float(np.mean(below < 0.12)) if below.size else 0.0
            per_column.append(score)
        shadows.append(float(np.mean([value for value in per_column if value > 0.0]))
                       if any(value > 0.0 for value in per_column) else 0.0)
        column_shadow_scores.extend(per_column)
        bright_coverages.append(float(np.mean(third >= 0.72)))
    shadow_mask = np.asarray(column_shadow_scores, dtype=np.float64) >= 0.50
    shadow_column_fraction = float(np.mean(shadow_mask))
    shadow_column_centroid = (
        float(np.mean(np.flatnonzero(shadow_mask)) / max(1, len(shadow_mask) - 1))
        if np.any(shadow_mask)
        else 0.5
    )
    feature_values = (
        *interfaces,
        *shadows,
        *depths,
        *bright_coverages,
        abs(interfaces[0] - interfaces[2]),
        abs(shadows[0] - shadows[2]),
        shadow_column_fraction,
        shadow_column_centroid,
    )
    return ImageFeature(tuple(float(value) for value in feature_values))


def select_unique_profile_observations(observations: Iterable[dict]) -> dict[str, dict[float, dict]]:
    profiles: dict[str, dict[float, dict]] = {}
    for observation in observations:
        token = str(observation.get("profileToken", ""))
        if not token:
            continue
        offset = float(observation["relativeWithinProfileOffsetMm"])
        if offset not in OFFSETS_MM:
            continue
        profiles.setdefault(token, {}).setdefault(offset, observation)
    for token, profile in profiles.items():
        missing = [offset for offset in OFFSETS_MM if offset not in profile]
        if missing:
            raise ValueError(f"{token}: missing unique offsets {missing}")
    return profiles


def _feature_record(feature: ImageFeature) -> dict[str, float]:
    return dict(zip(FEATURE_NAMES, feature.values))


def _feature_vector(record: dict) -> np.ndarray:
    if FORBIDDEN_FEATURE_FIELDS.intersection(record):
        raise ValueError("Feature record contains privileged or schedule-derived fields")
    if set(record) != set(FEATURE_NAMES):
        raise ValueError("Feature record schema differs from the frozen image-only feature set")
    return np.asarray([float(record[name]) for name in FEATURE_NAMES], dtype=np.float64)


def _blind_extract(run_root: Path, baseline_root: Path, output_root: Path) -> tuple[dict, dict]:
    trace = json.loads((run_root / "trace.json").read_text(encoding="utf-8"))
    profiles = select_unique_profile_observations(trace["observations"])
    blind_profiles: dict[str, dict] = {}
    for token in sorted(profiles):
        rows: list[dict] = []
        for offset in OFFSETS_MM:
            observation = profiles[token][offset]
            image_path = run_root / observation["image"]
            if _sha256(image_path).lower() != observation["imageSha256"].lower():
                raise ValueError(f"Image hash mismatch: {image_path}")
            feature = extract_image_features(image_path)
            rows.append({"relativeOffsetMm": offset, "features": _feature_record(feature)})
        blind_profiles[token] = {"samples": rows}
    blind = {
        "phase": "A_BLIND_IMAGE_ONLY",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "sourceRun": run_root.name,
        "scheduleDisclosure": trace.get("scheduleDisclosure", ""),
        "profileTokensUsedOnlyForGrouping": True,
        "featureInputs": list(FEATURE_NAMES),
        "explicitlyExcludedInputs": sorted(FORBIDDEN_FEATURE_FIELDS),
        "profiles": blind_profiles,
    }
    _save_json(output_root / "blind_lateral_profiles.json", blind)

    baseline_trace = json.loads((baseline_root / "trace.json").read_text(encoding="utf-8"))
    baseline_rows: list[dict] = []
    for observation in baseline_trace["observations"]:
        image_path = baseline_root / observation["image"]
        if _sha256(image_path).lower() != observation["imageSha256"].lower():
            raise ValueError(f"Baseline image hash mismatch: {image_path}")
        feature = extract_image_features(image_path)
        baseline_rows.append(
            {
                "opaqueSampleId": f"S{len(baseline_rows):04d}",
                "features": _feature_record(feature),
            }
        )
    blind_baseline = {
        "phase": "A_BLIND_IMAGE_ONLY",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "sourceRun": baseline_root.name,
        "featureInputs": list(FEATURE_NAMES),
        "explicitlyExcludedInputs": sorted(FORBIDDEN_FEATURE_FIELDS),
        "samples": baseline_rows,
    }
    _save_json(output_root / "blind_longitudinal_features.json", blind_baseline)
    return blind, blind_baseline


def _robust_standardizer(vectors: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    median = np.median(vectors, axis=0)
    mad = np.median(np.abs(vectors - median), axis=0)
    scale = np.maximum(1.4826 * mad, 1e-6)
    return median, scale


def _classify_held_position(signatures: np.ndarray) -> dict:
    classes, positions, _ = signatures.shape
    predictions: list[dict] = []
    correct = 0
    for position in range(positions):
        for class_index in range(classes):
            test = signatures[class_index, position]
            centroids = np.mean(
                np.delete(signatures, position, axis=1), axis=1
            )
            distances = np.linalg.norm(centroids - test, axis=1)
            predicted = int(np.argmin(distances))
            correct += int(predicted == class_index)
            predictions.append(
                {
                    "heldPositionIndex": position,
                    "trueOpaqueClass": f"C{class_index:02d}",
                    "predictedOpaqueClass": f"C{predicted:02d}",
                    "correct": predicted == class_index,
                    "nearestDistance": float(distances[predicted]),
                }
            )
    return {
        "correct": correct,
        "total": classes * positions,
        "accuracy": correct / max(1, classes * positions),
        "predictions": predictions,
        "warning": "Single-yaw diagnostic separability only; this is not cross-yaw validation or autonomous inference.",
    }


def _profile_variation(
    signatures: np.ndarray, center_indices: Iterable[int] | None = None
) -> list[float]:
    indices = list(center_indices) if center_indices is not None \
        else [OFFSETS_MM.index(0.0)] * signatures.shape[0]
    if len(indices) != signatures.shape[0]:
        raise ValueError("One center index is required per region")
    return [
        float(np.mean(np.linalg.norm(
            signatures[index] - signatures[index, indices[index]], axis=1
        )))
        for index in range(signatures.shape[0])
    ]


def classify_provenance(snapshot: dict) -> dict:
    visible: dict[str, int] = {}
    shadow: dict[str, int] = {}
    for beam in snapshot.get("beams", []):
        if beam.get("hasVisibleInterface"):
            name = str(beam.get("firstVisibleObject", ""))
            visible[name] = visible.get(name, 0) + 1
        if beam.get("hasShadowInterface"):
            name = str(beam.get("firstShadowObject", ""))
            shadow[name] = shadow.get(name, 0) + 1
    combined_skeleton = sum(count for name, count in shadow.items() if name == "Skeleton")
    return {
        "firstVisibleObjectBeamCounts": visible,
        "firstShadowObjectBeamCounts": shadow,
        "combinedSkeletonUnresolvedBeamCount": combined_skeleton,
        "anatomicalIdentity": "COMBINED_SKELETON_UNRESOLVED" if combined_skeleton else "NO_VERIFIED_BONE_IDENTITY",
    }


def _make_contact_sheet(run_root: Path, blind: dict, output: Path) -> None:
    trace = json.loads((run_root / "trace.json").read_text(encoding="utf-8"))
    profiles = select_unique_profile_observations(trace["observations"])
    tile = 190
    caption = 28
    sheet = Image.new("RGB", (len(OFFSETS_MM) * tile, len(profiles) * (tile + caption)), "white")
    draw = ImageDraw.Draw(sheet)
    for row, token in enumerate(sorted(blind["profiles"])):
        for column, offset in enumerate(OFFSETS_MM):
            observation = profiles[token][offset]
            with Image.open(run_root / observation["image"]) as source:
                image = source.convert("RGB").resize((tile, tile), Image.Resampling.BILINEAR)
            x = column * tile
            y = row * (tile + caption)
            sheet.paste(image, (x, y))
            draw.text((x + 4, y + tile + 4), f"{token}  lateral {offset:+.0f} mm", fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def _draw_comparison_plot(lateral: np.ndarray, longitudinal: np.ndarray, output: Path) -> None:
    width, height = 1300, 720
    margin = 90
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    maximum = max(float(np.max(lateral)), float(np.max(longitudinal)), 1e-6)
    plot_height = height - 2 * margin
    plot_width = width - 2 * margin
    draw.rectangle((margin, margin, margin + plot_width, margin + plot_height), outline="black")
    group_width = plot_width / 7
    for index in range(7):
        base_x = margin + group_width * index
        lateral_height = plot_height * float(lateral[index]) / maximum
        longitudinal_height = plot_height * float(longitudinal[index]) / maximum
        draw.rectangle(
            (base_x + 14, margin + plot_height - lateral_height,
             base_x + group_width / 2 - 4, margin + plot_height), fill=(36, 125, 190)
        )
        draw.rectangle(
            (base_x + group_width / 2 + 4, margin + plot_height - longitudinal_height,
             base_x + group_width - 14, margin + plot_height), fill=(220, 120, 45)
        )
        draw.text((base_x + group_width / 2 - 12, margin + plot_height + 10), f"C{index:02d}", fill="black")
    draw.text((margin, 30), "Equal-budget image-feature variation: lateral profiles vs longitudinal windows", fill="black")
    draw.rectangle((margin, 56, margin + 20, 72), fill=(36, 125, 190))
    draw.text((margin + 28, 54), "lateral", fill="black")
    draw.rectangle((margin + 120, 56, margin + 140, 72), fill=(220, 120, 45))
    draw.text((margin + 148, 54), "longitudinal", fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)


def analyze(run_root: Path, baseline_root: Path, output_root: Path) -> dict:
    output_root.mkdir(parents=True, exist_ok=True)
    blind, blind_baseline = _blind_extract(run_root, baseline_root, output_root)

    # Phase B begins only after the blind feature documents above have been materialized.
    privileged = json.loads(
        (run_root / "evaluator_only" / "trace_privileged.json").read_text(encoding="utf-8")
    )
    references = json.loads(
        (run_root / "evaluator_only" / "references.json").read_text(encoding="utf-8")
    )["references"]
    baseline_privileged = json.loads(
        (baseline_root / "evaluator_only" / "trace_privileged.json").read_text(encoding="utf-8")
    )["observations"]
    summary = json.loads((run_root / "run_summary.json").read_text(encoding="utf-8"))

    tokens = sorted(blind["profiles"])
    lateral_raw = np.asarray(
        [
            [_feature_vector(sample["features"]) for sample in blind["profiles"][token]["samples"]]
            for token in tokens
        ],
        dtype=np.float64,
    )
    baseline_feature_by_id = {
        item["opaqueSampleId"]: _feature_vector(item["features"])
        for item in blind_baseline["samples"]
    }
    baseline_windows: list[list[np.ndarray]] = []
    baseline_selection: list[dict] = []
    baseline_center_indices: list[int] = []
    for reference in references:
        coordinate = float(reference["corridorCoordinateMm"])
        nearest = sorted(
            baseline_privileged,
            key=lambda item: abs(float(item["corridorCoordinateMm"]) - coordinate),
        )[:5]
        nearest = sorted(nearest, key=lambda item: float(item["corridorCoordinateMm"]))
        vectors: list[np.ndarray] = []
        selected: list[dict] = []
        for item in nearest:
            sample_id = f"S{int(item['observationIndex']):04d}"
            vectors.append(baseline_feature_by_id[sample_id])
            selected.append(
                {
                    "opaqueSampleId": sample_id,
                    "relativeEvaluatorCoordinateMm": float(item["corridorCoordinateMm"]) - coordinate,
                }
            )
        if len(vectors) != 5:
            raise ValueError("Equal-budget longitudinal comparison requires five observations per region")
        baseline_windows.append(vectors)
        baseline_center_indices.append(int(np.argmin([
            abs(float(item["relativeEvaluatorCoordinateMm"])) for item in selected
        ])))
        baseline_selection.append({"diagnosticReferenceLevel": reference["level"], "samples": selected})
    longitudinal_raw = np.asarray(baseline_windows, dtype=np.float64)
    all_vectors = np.concatenate(
        [lateral_raw.reshape(-1, lateral_raw.shape[-1]), longitudinal_raw.reshape(-1, longitudinal_raw.shape[-1])],
        axis=0,
    )
    median, scale = _robust_standardizer(all_vectors)
    lateral = (lateral_raw - median) / scale
    longitudinal = (longitudinal_raw - median) / scale
    lateral_score = _classify_held_position(lateral)
    longitudinal_score = _classify_held_position(longitudinal)
    lateral_variation = _profile_variation(lateral)
    longitudinal_variation = _profile_variation(longitudinal, baseline_center_indices)

    provenance_by_token: dict[str, dict] = {}
    privileged_by_index = {int(item["observationIndex"]): item for item in privileged["observations"]}
    actor_trace = json.loads((run_root / "trace.json").read_text(encoding="utf-8"))
    actor_profiles = select_unique_profile_observations(actor_trace["observations"])
    for token in tokens:
        aggregate = {"beams": []}
        for offset in OFFSETS_MM:
            observation_index = int(actor_profiles[token][offset]["observationIndex"])
            provenance_path = run_root / privileged_by_index[observation_index]["provenance"]
            snapshot = json.loads(provenance_path.read_text(encoding="utf-8"))
            if snapshot["poseEpoch"] != actor_profiles[token][offset]["producerPoseEpoch"] \
                    or snapshot["producerScanSerial"] != actor_profiles[token][offset]["producerScanSerial"]:
                raise ValueError(f"Provenance mismatch for {token} offset {offset}")
            aggregate["beams"].extend(snapshot.get("beams", []))
        provenance_by_token[token] = classify_provenance(aggregate)

    _make_contact_sheet(run_root, blind, output_root / "lateral_contact_sheet_blind.png")
    _draw_comparison_plot(
        np.asarray(lateral_variation),
        np.asarray(longitudinal_variation),
        output_root / "lateral_vs_longitudinal_variation.png",
    )
    evaluator_crosswalk = []
    summary_profiles = {item["profileToken"]: item for item in summary["profiles"]}
    for index, token in enumerate(tokens):
        level = summary_profiles[token]["diagnosticReferenceLevel"]
        evaluator_crosswalk.append(
            {
                "opaqueClass": f"C{index:02d}",
                "profileToken": token,
                "diagnosticReferenceLevel": level,
                "provenance": provenance_by_token[token],
            }
        )
    _save_json(output_root / "evaluator_only_crosswalk.json", evaluator_crosswalk)
    _save_json(output_root / "equal_budget_longitudinal_selection.json", baseline_selection)

    lateral_correct_by_class = {
        f"C{index:02d}": sum(
            item["correct"] and item["trueOpaqueClass"] == f"C{index:02d}"
            for item in lateral_score["predictions"]
        )
        for index in range(7)
    }
    longitudinal_correct_by_class = {
        f"C{index:02d}": sum(
            item["correct"] and item["trueOpaqueClass"] == f"C{index:02d}"
            for item in longitudinal_score["predictions"]
        )
        for index in range(7)
    }
    with (output_root / "per_level_comparison.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "opaque_class",
                "diagnostic_reference_level_evaluator_only",
                "lateral_variation",
                "longitudinal_variation",
                "variation_delta",
                "lateral_correct_out_of_5",
                "longitudinal_correct_out_of_5",
                "anatomical_identity",
            ]
        )
        for index, item in enumerate(evaluator_crosswalk):
            opaque_class = item["opaqueClass"]
            writer.writerow(
                [
                    opaque_class,
                    item["diagnosticReferenceLevel"],
                    lateral_variation[index],
                    longitudinal_variation[index],
                    lateral_variation[index] - longitudinal_variation[index],
                    lateral_correct_by_class[opaque_class],
                    longitudinal_correct_by_class[opaque_class],
                    item["provenance"]["anatomicalIdentity"],
                ]
            )
    actor_by_index = {int(item["observationIndex"]): item for item in actor_trace["observations"]}
    with (output_root / "evaluator_only_trajectory.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "observation_index",
                "observation_kind",
                "action",
                "profile_token",
                "diagnostic_reference_level",
                "expected_lateral_offset_mm",
                "actual_lateral_offset_mm",
                "corridor_coordinate_mm",
                "surface_yaw_error_deg",
                "normal_alignment_error_deg",
                "signed_contact_depth_mm",
                "pose_epoch",
                "producer_frame",
                "scan_serial",
                "fresh",
                "provenance_matched",
            ]
        )
        for evaluator in privileged["observations"]:
            actor = actor_by_index[int(evaluator["observationIndex"])]
            fresh = (
                actor["producerPoseEpoch"] == actor["poseEpoch"]
                and actor["producerUnityFrame"] > actor["poseAppliedUnityFrame"]
                and actor["captureUnityFrame"] == actor["producerUnityFrame"]
                and actor["activeAggregatorCount"] == 0
            )
            writer.writerow(
                [
                    actor["observationIndex"],
                    actor["observationKind"],
                    actor["requestedAction"],
                    evaluator["profileToken"],
                    evaluator["diagnosticReferenceLevel"],
                    evaluator["expectedLateralOffsetMm"],
                    evaluator["actualLateralOffsetMm"],
                    evaluator["corridorCoordinateMm"],
                    evaluator["surfaceYawErrorDeg"],
                    evaluator["normalAlignmentErrorDeg"],
                    evaluator["signedContactDepthMm"],
                    actor["poseEpoch"],
                    actor["producerUnityFrame"],
                    actor["producerScanSerial"],
                    int(fresh),
                    int(evaluator["provenanceMatchedPublishedFrame"]),
                ]
            )

    score_gain = int(lateral_score["correct"]) - int(longitudinal_score["correct"])
    variation_gain_regions = sum(
        lateral_variation[index] > longitudinal_variation[index] for index in range(7)
    )
    lateral_information_gain = "INCONCLUSIVE_ONE_YAW_ONLY"
    anatomical_identity = (
        "UNRESOLVED"
        if any(
            item["anatomicalIdentity"] == "COMBINED_SKELETON_UNRESOLVED"
            for item in provenance_by_token.values()
        )
        else "INCONCLUSIVE"
    )
    result = {
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "pilotEligibility": summary["g1Status"],
        "lateralInformationGain": lateral_information_gain,
        "anatomicalIdentity": anatomical_identity,
        "equalBudget": {"lateralImagesPerRegion": 5, "longitudinalImagesPerRegion": 5, "regions": 7},
        "lateralHeldPositionSeparability": lateral_score,
        "longitudinalHeldPositionSeparability": longitudinal_score,
        "correctPredictionGain": score_gain,
        "lateralVariationByOpaqueClass": lateral_variation,
        "longitudinalVariationByOpaqueClass": longitudinal_variation,
        "regionsWithGreaterLateralVariation": variation_gain_regions,
        "observedDiagnosticDifference": (
            f"Lateral held-position score changed by {score_gain:+d}/35 versus equal-budget longitudinal; "
            f"lateral variation was greater in {variation_gain_regions}/7 regions."
        ),
        "repeatability": "INCONCLUSIVE_ONE_YAW_ONLY",
        "g2Status": "FAIL",
        "g3Status": "NOT_RUN",
        "g4Status": "NOT_RUN",
        "warnings": [
            "The movement schedule is evaluator-guided and cannot demonstrate autonomous localization or counting.",
            "Held-position separability uses image features only but is a single-run diagnostic, not held-out cross-yaw validation.",
            "COMBINED_SKELETON_UNRESOLVED is not a verified rib, lamina, vertebra, or interspace identity.",
            "More frames alone are not treated as evidence; the primary comparison uses five images per region in both arms.",
        ],
    }
    _save_json(output_root / "analysis.json", result)
    lines = [
        "# Mini-Zigzag G2 Feasibility — yaw 0° pilot",
        "",
        "This is an evaluator-guided diagnostic trajectory, not autonomous vertebral localization or counting.",
        "",
        f"- Pilot trajectory eligibility: **{summary['g1Status']}**",
        f"- LATERAL_INFORMATION_GAIN: **{lateral_information_gain}**",
        f"- ANATOMICAL_IDENTITY: **{anatomical_identity}**",
        f"- Equal-budget held-position score: lateral {lateral_score['correct']}/{lateral_score['total']}; longitudinal {longitudinal_score['correct']}/{longitudinal_score['total']}",
        f"- Regions with greater lateral image-feature variation: {variation_gain_regions}/7",
        "- The observed difference is descriptive only; the predeclared cross-yaw repeatability criteria cannot be evaluated with Z0 alone.",
        "- Repeatability: **INCONCLUSIVE — one yaw only**",
        "- G2: **FAIL** (unchanged)",
        "- G3/G4: **NOT_RUN**",
        "",
        "The combined `Skeleton` Shadow provenance is retained as `COMBINED_SKELETON_UNRESOLVED`; it does not establish rib, lamina, vertebra, or interspace identity.",
    ]
    (output_root / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description="Blind-first mini-zigzag feasibility analysis")
    parser.add_argument("--run-root", type=Path, required=True)
    parser.add_argument("--baseline-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    result = analyze(args.run_root.resolve(), args.baseline_root.resolve(), args.output_root.resolve())
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
