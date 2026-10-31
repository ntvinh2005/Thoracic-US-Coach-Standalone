from __future__ import annotations

import argparse
import csv
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image, ImageDraw


CONTENT_FEATURES = (
    "gray_std",
    "gradient_energy",
    "edge_coverage",
    "interface_coverage",
    "bright_coverage",
    "shadow_response",
)
LOW_SCORE_THRESHOLD = -0.75
LOW_COMPONENT_THRESHOLD = -0.50
LOW_COMPONENT_COUNT = 3
PERSISTENCE_FRAMES = 5
CENTRAL_HALF_WIDTH_MM = 18.0
REVERSAL_TOLERANCE_MM = 6.0
FORBIDDEN_BLIND_FIELDS = {
    "probePosition",
    "probeRotation",
    "contactPoint",
    "surfaceNormal",
    "evaluatorLongitudinalCoordinateMm",
    "diagnosticReferenceLevel",
    "anatomy",
    "mesh",
    "provenance",
}


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


def _gray(path: Path, size: int = 256) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR),
            dtype=np.float64,
        ) / 255.0


def extract_image_features(path: Path, previous: Path | None = None) -> dict[str, float]:
    values = _gray(path)
    height, width = values.shape
    roi = values[int(0.15 * height):int(0.90 * height), int(0.08 * width):int(0.92 * width)]
    gy, gx = np.gradient(roi)
    magnitude = np.sqrt(gx * gx + gy * gy)
    positive_vertical = np.maximum(gy, 0.0)
    interface_mask = positive_vertical >= 0.08
    shadow_scores: list[float] = []
    for column in range(roi.shape[1]):
        candidates = np.flatnonzero(interface_mask[:, column])
        if not candidates.size:
            shadow_scores.append(0.0)
            continue
        row = int(candidates[np.argmax(positive_vertical[candidates, column])])
        below = roi[min(row + 8, roi.shape[0]):, column]
        shadow_scores.append(float(np.mean(below < 0.12)) if below.size else 0.0)
    previous_difference = 0.0
    previous_correlation = 1.0
    if previous is not None:
        prior = _gray(previous)[int(0.15 * height):int(0.90 * height), int(0.08 * width):int(0.92 * width)]
        previous_difference = float(np.mean(np.abs(roi - prior)))
        left = roi.reshape(-1) - float(np.mean(roi))
        right = prior.reshape(-1) - float(np.mean(prior))
        denominator = float(np.linalg.norm(left) * np.linalg.norm(right))
        previous_correlation = float(np.dot(left, right) / denominator) if denominator > 1e-9 else 1.0
    return {
        "gray_mean": float(np.mean(roi)),
        "gray_std": float(np.std(roi)),
        "gray_p10": float(np.quantile(roi, 0.10)),
        "gray_p50": float(np.quantile(roi, 0.50)),
        "gray_p90": float(np.quantile(roi, 0.90)),
        "gradient_energy": float(np.mean(magnitude)),
        "edge_coverage": float(np.mean(magnitude >= 0.08)),
        "interface_coverage": float(np.mean(interface_mask)),
        "bright_coverage": float(np.mean(roi >= 0.72)),
        "shadow_response": float(np.mean(shadow_scores)),
        "neighbor_mean_absolute_difference": previous_difference,
        "neighbor_correlation": previous_correlation,
    }


def _robust_content_scores(rows: list[dict]) -> None:
    matrix = np.asarray(
        [[float(row[name]) for name in CONTENT_FEATURES] for row in rows], dtype=np.float64
    )
    median = np.median(matrix, axis=0)
    mad = np.median(np.abs(matrix - median), axis=0)
    scale = np.maximum(1.4826 * mad, 0.01)
    standardized = (matrix - median) / scale
    for row, vector in zip(rows, standardized):
        row["content_score"] = float(np.mean(vector))
        row["low_component_count"] = int(np.sum(vector <= LOW_COMPONENT_THRESHOLD))
        low = (
            row["content_score"] <= LOW_SCORE_THRESHOLD
            and row["low_component_count"] >= LOW_COMPONENT_COUNT
        )
        row["classification"] = "LOW_CONTENT" if low else "STRUCTURED"


def mark_persistence(rows: list[dict], minimum: int = PERSISTENCE_FRAMES) -> None:
    by_phase: dict[str, list[dict]] = {}
    for row in rows:
        by_phase.setdefault(str(row["phase"]), []).append(row)
    for phase_rows in by_phase.values():
        start = 0
        while start < len(phase_rows):
            if phase_rows[start]["classification"] != "LOW_CONTENT":
                start += 1
                continue
            end = start + 1
            while end < len(phase_rows) and phase_rows[end]["classification"] == "LOW_CONTENT":
                end += 1
            if end - start >= minimum:
                for index in range(start, end):
                    phase_rows[index]["classification"] = "PERSISTENT_LOW_CONTENT"
            start = end


def _terminal_run(rows: list[dict], terminal: bool) -> list[dict]:
    ordered = list(reversed(rows) if terminal else rows)
    result: list[dict] = []
    for row in ordered:
        if row["classification"] != "PERSISTENT_LOW_CONTENT":
            break
        result.append(row)
    return list(reversed(result)) if terminal else result


def assess_end(
    rows: list[dict], outbound_phase: str, reverse_phase: str, central_structured: bool
) -> dict:
    outbound = [row for row in rows if row["phase"] == outbound_phase]
    reverse = [row for row in rows if row["phase"] == reverse_phase]
    outbound_run = _terminal_run(outbound, terminal=True)
    reverse_run = _terminal_run(reverse, terminal=False)
    adequate = len(outbound) >= PERSISTENCE_FRAMES and len(reverse) >= PERSISTENCE_FRAMES
    outbound_transition = (
        float(outbound_run[0]["signed_actor_relative_mm"]) if outbound_run else None
    )
    reverse_transition = (
        float(reverse_run[-1]["signed_actor_relative_mm"]) if reverse_run else None
    )
    difference = (
        abs(outbound_transition - reverse_transition)
        if outbound_transition is not None and reverse_transition is not None
        else None
    )
    if not adequate:
        status = "INCONCLUSIVE"
        reason = "endpoint or reverse phase has fewer than five valid observations"
    elif not central_structured:
        status = "INCONCLUSIVE"
        reason = "central comparator was not predominantly structured"
    elif len(outbound_run) >= PERSISTENCE_FRAMES and len(reverse_run) >= PERSISTENCE_FRAMES:
        if difference is not None and difference <= REVERSAL_TOLERANCE_MM:
            status = "PASS"
            reason = "persistent endpoint low-content state recurred within tolerance"
        else:
            status = "INCONCLUSIVE"
            reason = "persistent state recurred but transition positions exceeded tolerance"
    elif not outbound_run and not reverse_run:
        status = "FAIL"
        reason = "adequate endpoint sampling found no terminal persistent low-content run"
    else:
        status = "INCONCLUSIVE"
        reason = "persistent low-content evidence occurred in only one traversal direction"
    return {
        "status": status,
        "reason": reason,
        "outboundPhase": outbound_phase,
        "reversePhase": reverse_phase,
        "outboundObservationCount": len(outbound),
        "reverseObservationCount": len(reverse),
        "outboundPersistentCount": len(outbound_run),
        "reversePersistentCount": len(reverse_run),
        "outboundTransitionActorRelativeMm": outbound_transition,
        "reverseTransitionActorRelativeMm": reverse_transition,
        "transitionDifferenceMm": difference,
        "toleranceMm": REVERSAL_TOLERANCE_MM,
    }


def _thumbnail(path: Path, size: tuple[int, int] = (220, 220)) -> Image.Image:
    with Image.open(path) as image:
        result = image.convert("RGB")
        result.thumbnail(size, Image.Resampling.LANCZOS)
        canvas = Image.new("RGB", size, "black")
        canvas.paste(result, ((size[0] - result.width) // 2, (size[1] - result.height) // 2))
        return canvas


def _select_even(rows: list[dict], count: int = 3) -> list[dict]:
    if not rows:
        return []
    indices = np.linspace(0, len(rows) - 1, min(count, len(rows))).round().astype(int)
    return [rows[int(index)] for index in indices]


def make_contact_sheet(run_root: Path, rows: list[dict], output: Path) -> None:
    groups = [
        ("central", [row for row in rows if abs(row["signed_actor_relative_mm"]) <= 9]),
        ("caudal approach", [row for row in rows if row["phase"] == "caudal_outbound"][-10:-5]),
        ("caudal endpoint", [row for row in rows if row["phase"] == "caudal_outbound"][-5:]),
        ("caudal reverse", [row for row in rows if row["phase"] == "caudal_reverse"][:5]),
        ("cranial approach", [row for row in rows if row["phase"] == "cranial_outbound"][-10:-5]),
        ("cranial endpoint", [row for row in rows if row["phase"] == "cranial_outbound"][-5:]),
        ("cranial reverse", [row for row in rows if row["phase"] == "cranial_reverse"][:5]),
    ]
    tile_w, tile_h = 220, 250
    canvas = Image.new("RGB", (tile_w * 3, tile_h * len(groups)), "white")
    draw = ImageDraw.Draw(canvas)
    for row_index, (label, candidates) in enumerate(groups):
        selected = _select_even(candidates, 3)
        for column, item in enumerate(selected):
            image = _thumbnail(run_root / item["image"])
            y = row_index * tile_h
            canvas.paste(image, (column * tile_w, y))
            draw.text(
                (column * tile_w + 4, y + 222),
                f"{label}: {item['signed_actor_relative_mm']:.1f} mm {item['classification']}",
                fill="black",
            )
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)


def make_signal_plot(rows: list[dict], output: Path) -> None:
    width, height, margin = 1500, 760, 80
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    scores = np.asarray([float(row["content_score"]) for row in rows])
    minimum = min(float(np.min(scores)), LOW_SCORE_THRESHOLD - 0.5)
    maximum = max(float(np.max(scores)), LOW_SCORE_THRESHOLD + 0.5)
    span = max(maximum - minimum, 1e-6)
    def point(index: int, value: float) -> tuple[float, float]:
        x = margin + index * (width - 2 * margin) / max(1, len(rows) - 1)
        y = height - margin - (value - minimum) * (height - 2 * margin) / span
        return x, y
    threshold_y = point(0, LOW_SCORE_THRESHOLD)[1]
    draw.line((margin, threshold_y, width - margin, threshold_y), fill=(190, 0, 0), width=2)
    draw.text((margin + 5, threshold_y - 20), "predeclared low-content score -0.75", fill=(190, 0, 0))
    for index in range(1, len(rows)):
        draw.line((*point(index - 1, scores[index - 1]), *point(index, scores[index])), fill=(30, 90, 180), width=3)
    for index, row in enumerate(rows):
        color = (180, 0, 0) if "LOW" in row["classification"] else (0, 120, 50)
        x, y = point(index, scores[index])
        draw.ellipse((x - 3, y - 3, x + 3, y + 3), fill=color)
    draw.rectangle((margin, margin, width - margin, height - margin), outline="black")
    draw.text((margin, 30), "M0-B blind image-only content score over executed observation chronology", fill="black")
    draw.text((margin, height - 45), "chronological observations; reversals retained", fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)


def _image_similarity(left: Path, right: Path) -> float:
    a = _gray(left, 64).reshape(-1)
    b = _gray(right, 64).reshape(-1)
    a -= np.mean(a)
    b -= np.mean(b)
    denominator = float(np.linalg.norm(a) * np.linalg.norm(b))
    return float(np.dot(a, b) / denominator) if denominator > 1e-9 else 1.0


def make_reversal_comparison(run_root: Path, rows: list[dict], output: Path) -> list[dict]:
    comparisons: list[dict] = []
    pairs = (("caudal_outbound", "caudal_reverse"), ("cranial_outbound", "cranial_reverse"))
    selected_pairs: list[tuple[str, dict, dict]] = []
    for outbound_phase, reverse_phase in pairs:
        outbound = [row for row in rows if row["phase"] == outbound_phase][-4:]
        reverse = [row for row in rows if row["phase"] == reverse_phase]
        for left in outbound:
            right = min(reverse, key=lambda row: abs(
                row["signed_actor_relative_mm"] - left["signed_actor_relative_mm"]
            ))
            similarity = _image_similarity(run_root / left["image"], run_root / right["image"])
            comparisons.append({
                "outboundObservationIndex": left["observation_index"],
                "reverseObservationIndex": right["observation_index"],
                "outboundActorRelativeMm": left["signed_actor_relative_mm"],
                "reverseActorRelativeMm": right["signed_actor_relative_mm"],
                "positionDifferenceMm": abs(left["signed_actor_relative_mm"] - right["signed_actor_relative_mm"]),
                "pixelCorrelation": similarity,
            })
            selected_pairs.append((outbound_phase.split("_")[0], left, right))
    tile = 200
    canvas = Image.new("RGB", (tile * 2, (tile + 35) * len(selected_pairs)), "white")
    draw = ImageDraw.Draw(canvas)
    for index, (label, left, right) in enumerate(selected_pairs):
        y = index * (tile + 35)
        canvas.paste(_thumbnail(run_root / left["image"], (tile, tile)), (0, y))
        canvas.paste(_thumbnail(run_root / right["image"], (tile, tile)), (tile, y))
        draw.text((4, y + tile + 4), f"{label} out {left['signed_actor_relative_mm']:.1f}", fill="black")
        draw.text((tile + 4, y + tile + 4), f"reverse {right['signed_actor_relative_mm']:.1f}", fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)
    return comparisons


def _write_features_csv(path: Path, rows: list[dict]) -> None:
    fields = [
        "observation_index", "phase", "action", "signed_actor_relative_mm",
        "cumulative_executed_travel_mm", "classification", "content_score",
        "low_component_count", "gray_mean", "gray_std", "gray_p10", "gray_p50",
        "gray_p90", "gradient_energy", "edge_coverage", "interface_coverage",
        "bright_coverage", "shadow_response", "neighbor_mean_absolute_difference",
        "neighbor_correlation", "image", "image_sha256",
    ]
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows({field: row[field] for field in fields} for row in rows)


def _freeze_blind_outputs(run_root: Path, names: Iterable[str]) -> dict:
    hashes = {name: _sha256(run_root / name) for name in names}
    payload = {
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "BLIND_IMAGE_ONLY_FROZEN_BEFORE_EVALUATOR",
        "sha256": hashes,
    }
    _save_json(run_root / "blind_analysis.sha256", payload)
    return payload


def verify_blind_freeze(run_root: Path) -> dict:
    payload = _load_json(run_root / "blind_analysis.sha256")
    for name, expected in payload["sha256"].items():
        if _sha256(run_root / name) != expected:
            raise ValueError(f"Blind output changed after freeze: {name}")
    return payload


def validate_actor_isolation(actor: dict) -> None:
    forbidden = {field.lower() for field in FORBIDDEN_BLIND_FIELDS}

    def visit(value: object, path: str) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                normalized = str(key).lower()
                if any(field in normalized for field in forbidden):
                    raise ValueError(f"Actor trace contains forbidden evaluator field: {path}{key}")
                visit(child, f"{path}{key}.")
        elif isinstance(value, list):
            for index, child in enumerate(value):
                visit(child, f"{path}[{index}].")

    visit(actor, "")


def blind_analyze(run_root: Path) -> dict:
    actor = _load_json(run_root / "trajectory_actor.json")
    validate_actor_isolation(actor)
    rows: list[dict] = []
    previous: Path | None = None
    for observation in actor["observations"]:
        image = run_root / observation["image"]
        if _sha256(image).lower() != observation["imageSha256"].lower():
            raise ValueError(f"Image hash mismatch: {image}")
        if not observation["frameIntegrityValid"]:
            rows.append({
                "observation_index": int(observation["observationIndex"]),
                "phase": observation["phase"],
                "classification": "INVALID",
            })
            previous = None
            continue
        features = extract_image_features(image, previous)
        row = {
            "observation_index": int(observation["observationIndex"]),
            "phase": observation["phase"],
            "action": observation["requestedAction"],
            "signed_actor_relative_mm": float(observation["signedActorRelativeMm"]),
            "cumulative_executed_travel_mm": float(observation["cumulativeExecutedTravelMm"]),
            "image": observation["image"],
            "image_sha256": observation["imageSha256"],
            **features,
        }
        rows.append(row)
        previous = image
    valid_rows = [row for row in rows if row.get("classification") != "INVALID"]
    _robust_content_scores(valid_rows)
    mark_persistence(valid_rows)
    central = [row for row in valid_rows if abs(row["signed_actor_relative_mm"]) <= CENTRAL_HALF_WIDTH_MM]
    central_structured_fraction = float(np.mean([
        row["classification"] == "STRUCTURED" for row in central
    ])) if central else 0.0
    central_median_score = float(np.median([row["content_score"] for row in central])) if central else -999.0
    central_structured = central_structured_fraction >= 0.60 and central_median_score > -0.25
    caudal = assess_end(valid_rows, "caudal_outbound", "caudal_reverse", central_structured)
    cranial = assess_end(valid_rows, "cranial_outbound", "cranial_reverse", central_structured)
    comparisons = make_reversal_comparison(
        run_root, valid_rows, run_root / "reversal_comparison.png"
    )
    make_contact_sheet(run_root, valid_rows, run_root / "longitudinal_contact_sheet.png")
    make_signal_plot(valid_rows, run_root / "boundary_signal_plot.png")
    _write_features_csv(run_root / "image_features.csv", valid_rows)
    result = {
        "contract": "thoracic_visual_boundary_m0b_blind_v1",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "BLIND_IMAGE_ONLY",
        "featureInputs": list(CONTENT_FEATURES),
        "explicitlyExcludedInputs": sorted(FORBIDDEN_BLIND_FIELDS),
        "predeclaredCriteria": {
            "lowContentScoreThreshold": LOW_SCORE_THRESHOLD,
            "lowComponentThreshold": LOW_COMPONENT_THRESHOLD,
            "requiredLowComponents": LOW_COMPONENT_COUNT,
            "persistenceFrames": PERSISTENCE_FRAMES,
            "centralHalfWidthMm": CENTRAL_HALF_WIDTH_MM,
            "reversalToleranceMm": REVERSAL_TOLERANCE_MM,
        },
        "validObservationCount": len(valid_rows),
        "invalidObservationCount": len(rows) - len(valid_rows),
        "centralStructured": central_structured,
        "centralStructuredFraction": central_structured_fraction,
        "centralMedianContentScore": central_median_score,
        "caudalVisualBoundary": caudal,
        "cranialVisualBoundary": cranial,
        "reversalImageComparisons": comparisons,
        "warning": "No evaluator geometry, anatomy bounds, labels, mesh identity, or provenance was read.",
    }
    _save_json(run_root / "blind_boundary_analysis.json", result)
    _freeze_blind_outputs(run_root, (
        "blind_boundary_analysis.json",
        "image_features.csv",
        "boundary_signal_plot.png",
        "longitudinal_contact_sheet.png",
        "reversal_comparison.png",
    ))
    return result


def evaluator_crosscheck(run_root: Path) -> dict:
    verify_blind_freeze(run_root)
    blind = _load_json(run_root / "blind_boundary_analysis.json")
    actor = _load_json(run_root / "trajectory_actor.json")
    privileged = _load_json(run_root / "evaluator_only" / "trajectory_privileged.json")
    summary = _load_json(run_root / "run_summary.json")
    actor_by_index = {int(item["observationIndex"]): item for item in actor["observations"]}
    provenance_rows: list[dict] = []
    all_provenance_match = True
    for item in privileged["observations"]:
        index = int(item["observationIndex"])
        actor_item = actor_by_index[index]
        snapshot = _load_json(run_root / item["provenance"])
        beams = snapshot.get("beams", [])
        visible = sum(bool(beam.get("hasVisibleInterface")) for beam in beams)
        shadow = sum(bool(beam.get("hasShadowInterface")) for beam in beams)
        match = (
            int(snapshot["poseEpoch"]) == int(actor_item["producerPoseEpoch"])
            and int(snapshot["producerUnityFrame"]) == int(actor_item["producerUnityFrame"])
            and int(snapshot["producerScanSerial"]) == int(actor_item["producerScanSerial"])
            and bool(item["provenanceMatchedPublishedFrame"])
        )
        all_provenance_match &= match
        provenance_rows.append({
            "observation_index": index,
            "phase": actor_item["phase"],
            "actor_relative_mm": float(actor_item["signedActorRelativeMm"]),
            "evaluator_longitudinal_mm": float(item["evaluatorLongitudinalCoordinateMm"]),
            "beam_count": len(beams),
            "visible_interface_fraction": visible / max(1, len(beams)),
            "shadow_interface_fraction": shadow / max(1, len(beams)),
            "provenance_match": match,
        })
    crosswalk_path = run_root / "evaluator_only" / "provenance_crosscheck.csv"
    with crosswalk_path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(provenance_rows[0]))
        writer.writeheader()
        writer.writerows(provenance_rows)
    all_fresh = all(
        int(item["poseEpoch"]) == int(item["producerPoseEpoch"])
        and int(item["producerUnityFrame"]) > int(item["poseAppliedUnityFrame"])
        and int(item["captureUnityFrame"]) == int(item["producerUnityFrame"])
        and int(item["producerScanSerial"]) > 0
        and int(item["activeAggregatorCount"]) == 0
        for item in actor["observations"]
    )
    all_safe = all(
        bool(item["contactValidAfterExecution"])
        and bool(item["workspaceValidAfterExecution"])
        and not bool(item["safetyFailure"])
        for item in actor["observations"]
    )
    all_integrity = all(bool(item["frameIntegrityValid"]) for item in actor["observations"])
    image_integrity = "PASS" if all_fresh and all_safe and all_integrity and all_provenance_match else "FAIL"
    physical_sampling = str(summary["physicalSampling"])
    caudal_status = str(blind["caudalVisualBoundary"]["status"])
    cranial_status = str(blind["cranialVisualBoundary"]["status"])
    transition_differences = [
        blind[name].get("transitionDifferenceMm")
        for name in ("caudalVisualBoundary", "cranialVisualBoundary")
    ]
    reversal = (
        "PASS" if all(value is not None and float(value) <= REVERSAL_TOLERANCE_MM
                      for value in transition_differences)
        else "FAIL"
    )
    if physical_sampling != "PASS" or image_integrity != "PASS":
        overall = "BLOCKED"
    elif caudal_status == "PASS" and cranial_status == "PASS" and reversal == "PASS":
        overall = "PASS"
    elif sorted((caudal_status, cranial_status)) == ["FAIL", "PASS"]:
        overall = "PARTIAL"
    elif caudal_status == "FAIL" and cranial_status == "FAIL":
        overall = "FAIL"
    else:
        overall = "INCONCLUSIVE"
    central_low = sum(
        row["classification"] != "STRUCTURED"
        and abs(float(row["signed_actor_relative_mm"])) <= CENTRAL_HALF_WIDTH_MM
        for row in csv.DictReader((run_root / "image_features.csv").open(encoding="utf-8"))
    )
    evaluation = {
        "contract": "thoracic_visual_boundary_m0b_evaluation_v1",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "physicalSampling": physical_sampling,
        "caudalVisualBoundary": caudal_status,
        "cranialVisualBoundary": cranial_status,
        "reversalRepeatability": reversal,
        "imageIntegrity": image_integrity,
        "overallM0B": overall,
        "allFresh": all_fresh,
        "allSafe": all_safe,
        "allFrameIntegrityValid": all_integrity,
        "allProvenanceMatched": all_provenance_match,
        "centralLowContentObservationCount": central_low,
        "previousAabbRuleWouldHavePreventedThisVisualTest": True,
        "combinedSkeletonIdentity": "UNRESOLVED",
        "g2Status": "FAIL",
        "g3Status": "NOT_RUN",
        "g4Status": "NOT_RUN",
        "warnings": [
            "This is one evaluator-planned fixed-anatomy diagnostic episode, not autonomous boundary discovery.",
            "Combined Skeleton provenance does not establish rib, lamina, interspace, or vertebral identity.",
            "AABB containment was not used by the blind detector or acceptance logic.",
        ],
    }
    _save_json(run_root / "evaluation.json", evaluation)
    _write_report(run_root, summary, blind, evaluation)
    return evaluation


def _write_report(run_root: Path, summary: dict, blind: dict, evaluation: dict) -> None:
    caudal = blind["caudalVisualBoundary"]
    cranial = blind["cranialVisualBoundary"]
    lines = [
        "# Thoracic US Coach — M0-B Visual Boundary Feasibility",
        "",
        "## Implementation",
        "",
        "An isolated partial controller and batch runner executed one yaw-0 physical route using existing Active2D movement, contact rollback, fresh capture, and opt-in evaluator provenance. The diagnostic workspace was derived from the connected skin corridor and restored in `finally`. No scene, collider, renderer, standard Active2D behavior, T7 logic, or G0-G4 gate was changed.",
        "",
        "## Verification",
        "",
        f"- Physical sampling: **{evaluation['physicalSampling']}**",
        f"- Image integrity: **{evaluation['imageIntegrity']}**",
        f"- Valid frames: **{summary['validFrames']}/{summary['totalFrames']}**",
        f"- Accepted/rejected movements: **{summary['acceptedMovements']}/{summary['rejectedMovements']}**",
        f"- Max yaw error: **{summary['maximumYawErrorDeg']:.6f} deg**",
        f"- Max normal-alignment error: **{summary['maximumNormalAlignmentErrorDeg']:.6f} deg**",
        "",
        "## Safe physical coverage",
        "",
        f"- Actor-relative evaluator coverage: **{summary['minimumEvaluatorCoordinateMm']:.3f} to {summary['maximumEvaluatorCoordinateMm']:.3f} mm**.",
        f"- Planned endpoint coordinates: **{summary['plannedCranialEndpointMm']:.3f} to {summary['plannedCaudalEndpointMm']:.3f} mm**.",
        f"- Original workspace restored: **{summary['originalWorkspaceRestored']}**.",
        "",
        "## Cranial ultrasound observations",
        "",
        f"- Verdict: **{cranial['status']}** — {cranial['reason']}.",
        f"- Persistent observations outbound/reverse: **{cranial['outboundPersistentCount']}/{cranial['reversePersistentCount']}**.",
        f"- Transition difference: **{cranial['transitionDifferenceMm']} mm**.",
        "",
        "## Caudal ultrasound observations",
        "",
        f"- Verdict: **{caudal['status']}** — {caudal['reason']}.",
        f"- Persistent observations outbound/reverse: **{caudal['outboundPersistentCount']}/{caudal['reversePersistentCount']}**.",
        f"- Transition difference: **{caudal['transitionDifferenceMm']} mm**.",
        "",
        "## Reversal",
        "",
        f"- REVERSAL_REPEATABILITY: **{evaluation['reversalRepeatability']}** using a predeclared 6 mm tolerance.",
        "",
        "## Failures and limitations",
        "",
        f"- Low-content observations inside the central +/-18 mm comparator: **{evaluation['centralLowContentObservationCount']}**.",
        "- The route is evaluator-planned and the evidence comes from one fixed-anatomy episode.",
        "- `Skeleton` remains `COMBINED_SKELETON_UNRESOLVED`; no substructure identity is claimed.",
        "- The blind detector uses fixed robust-content criteria and does not use anatomy bounds or evaluator labels.",
        "",
        "## Verdict",
        "",
        f"**M0-B: {evaluation['overallM0B']}**",
        "",
        "The previous AABB stopping rule would have prevented this actual visual test; AABB containment was not used as evidence for or against a visual transition.",
        "",
        "## Next decision",
        "",
        "M1 remains unauthorized. Review the real frames, blind analysis, reversal evidence, and evaluator provenance before deciding whether autonomous boundary discovery is scientifically justified.",
    ]
    (run_root / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description="M0-B blind visual-boundary analysis and evaluator audit")
    parser.add_argument("--run-root", type=Path, required=True)
    parser.add_argument("--phase", choices=("blind", "evaluate"), required=True)
    args = parser.parse_args()
    root = args.run_root.resolve()
    result = blind_analyze(root) if args.phase == "blind" else evaluator_crosscheck(root)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
