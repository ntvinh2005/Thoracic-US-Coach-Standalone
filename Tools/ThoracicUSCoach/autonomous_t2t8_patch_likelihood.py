"""Session-isolated image-only landmark morphology research for fixed-anatomy T2-T8.

Evaluator coordinates are accepted only by the explicit development fitting and
scoring entry points.  The fitted actor model and inference path consume clean
ultrasound pixels, acquisition geometry, and actor-relative motion only.
"""

from __future__ import annotations

import argparse
import json
import math
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

import joblib
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image
from scipy.signal import find_peaks
from sklearn.ensemble import ExtraTreesClassifier
from sklearn.metrics import precision_recall_fscore_support, roc_auc_score

from g2_r2_continuous_detector import read_json, validate_actor_trace, write_json
from g2_r4_multi_hypothesis_tracker import track_multiple_interfaces
from relative_spine_e2_analysis import load_gray


IMAGE_AXIS_SIGN = -1
PATCH_WIDTH_PIXELS = 48
PATCH_HEIGHT_PIXELS = 80
PATCH_ABOVE_INTERFACE_PIXELS = 34
FEATURE_SIZE_PIXELS = 16
GRID_STEP_MM = 0.5
MINIMUM_EVENT_SEPARATION_MM = 10.0
MINIMUM_DIRECTION_SUPPORT = 2
RAW_PEAK_MINIMUM_SCORE = 0.55
RAW_PEAK_MINIMUM_PROMINENCE = 0.08
FROZEN_ACCEPTANCE_SCORE = 0.70
LEVELS = ("T2", "T3", "T4", "T5", "T6", "T7", "T8")


@dataclass(frozen=True)
class Session:
    session_id: str
    root: Path
    actor_path: Path


def _trajectory(session: Session) -> dict[str, Any]:
    actor = read_json(session.actor_path)
    validate_actor_trace(session.root, actor)
    return actor


def _footprint(actor: dict[str, Any]) -> float:
    value = float(actor["renderedFootprintWidthMm"])
    if not 8.0 <= value <= 70.0:
        raise ValueError(f"Invalid rendered footprint {value}")
    return value


def _pixel_x_for_coordinate(
    coordinate_mm: float, probe_mm: float, image_width: int, footprint_mm: float
) -> float:
    center = 0.5 * (image_width - 1.0)
    offset = (coordinate_mm - probe_mm) / (IMAGE_AXIS_SIGN * footprint_mm)
    return center + offset * max(2.0 * center, 1.0)


def _direction(phase: str) -> str:
    lowered = phase.lower()
    if "return" in lowered or "reverse" in lowered:
        return "REVERSE"
    return "FORWARD"


def _resize(array: np.ndarray, width: int, height: int) -> np.ndarray:
    image = Image.fromarray(np.asarray(np.clip(array * 255.0, 0, 255), dtype=np.uint8))
    return np.asarray(image.resize((width, height), Image.Resampling.BILINEAR), dtype=np.float64) / 255.0


def _patch_feature(image: np.ndarray, pixel_x: float, interface_y: float) -> np.ndarray | None:
    x0 = int(round(pixel_x - PATCH_WIDTH_PIXELS / 2))
    x1 = x0 + PATCH_WIDTH_PIXELS
    y0 = int(round(interface_y - PATCH_ABOVE_INTERFACE_PIXELS))
    y1 = y0 + PATCH_HEIGHT_PIXELS
    if x0 < 0 or y0 < 0 or x1 > image.shape[1] or y1 > image.shape[0]:
        return None
    patch = np.asarray(image[y0:y1, x0:x1], dtype=np.float64)
    low, high = np.quantile(patch, (0.05, 0.95))
    normalized = np.clip((patch - low) / max(high - low, 1e-6), 0.0, 1.0)
    small = _resize(normalized, FEATURE_SIZE_PIXELS, FEATURE_SIZE_PIXELS)
    gx = np.gradient(small, axis=1)
    gy = np.gradient(small, axis=0)
    magnitude = np.hypot(gx, gy)
    interface_row = min(FEATURE_SIZE_PIXELS - 1,
                        max(0, round(PATCH_ABOVE_INTERFACE_PIXELS
                                     * FEATURE_SIZE_PIXELS / PATCH_HEIGHT_PIXELS)))
    proximal = small[max(0, interface_row - 4):interface_row]
    distal = small[interface_row + 1:min(FEATURE_SIZE_PIXELS, interface_row + 6)]
    summaries = np.asarray([
        float(np.mean(small)), float(np.std(small)),
        float(np.mean(proximal)) if proximal.size else 0.0,
        float(np.mean(distal)) if distal.size else 0.0,
        float(np.mean(magnitude)), float(np.quantile(magnitude, 0.9)),
    ])
    return np.concatenate((small.ravel(), gx.ravel(), gy.ravel(), summaries))


def features_at_coordinate(
    image: np.ndarray, tracks: list[dict[str, Any]], pixel_x: float
) -> list[np.ndarray]:
    features: list[np.ndarray] = []
    for track in tracks:
        if not track.get("valid", False):
            continue
        xs = np.asarray(track["x"], dtype=np.float64)
        if pixel_x < xs[0] or pixel_x > xs[-1]:
            continue
        interface_y = float(np.interp(pixel_x, xs, np.asarray(track["pathY"])))
        feature = _patch_feature(image, pixel_x, interface_y)
        if feature is not None:
            features.append(feature)
    return features


def _sample_coordinates(
    minimum: float, maximum: float, references: Iterable[float], stride_mm: float = 4.0
) -> tuple[list[float], list[float]]:
    if not math.isfinite(stride_mm) or stride_mm <= 0:
        raise ValueError("Negative sampling stride must be finite and positive")
    positives = list(references)
    negatives = []
    for coordinate in np.arange(minimum, maximum + 0.01, stride_mm):
        if min(abs(coordinate - reference) for reference in positives) >= 7.0:
            negatives.append(float(coordinate))
    return positives, negatives


def build_training_examples(
    session: Session, references_mm: list[float], negative_stride_mm: float = 4.0
) -> tuple[np.ndarray, np.ndarray, list[dict[str, Any]]]:
    actor = _trajectory(session)
    observations = validate_actor_trace(session.root, actor)
    footprint = _footprint(actor)
    positions = [float(item["signedActorRelativeMm"]) for item in observations]
    positives, negatives = _sample_coordinates(
        min(positions), max(positions), references_mm, negative_stride_mm)
    examples: list[np.ndarray] = []
    labels: list[int] = []
    metadata: list[dict[str, Any]] = []
    for observation in observations:
        image = load_gray(session.root / observation["image"])
        tracks = track_multiple_interfaces(image)
        for label, coordinates in ((1, positives), (0, negatives)):
            for coordinate in coordinates:
                pixel_x = _pixel_x_for_coordinate(
                    coordinate, float(observation["signedActorRelativeMm"]),
                    image.shape[1], footprint)
                for track_index, feature in enumerate(features_at_coordinate(image, tracks, pixel_x)):
                    examples.append(feature)
                    labels.append(label)
                    metadata.append({
                        "sessionId": session.session_id,
                        "observationIndex": observation["observationIndex"],
                        "direction": _direction(observation["phase"]),
                        "coordinateMm": coordinate,
                        "pixelX": pixel_x,
                        "trackIndex": track_index,
                        "label": label,
                    })
    if not examples:
        raise ValueError(f"No training examples from {session.session_id}")
    return np.stack(examples), np.asarray(labels, dtype=np.int32), metadata


def fit_model(features: np.ndarray, labels: np.ndarray, seed: int = 20261103) -> ExtraTreesClassifier:
    model = ExtraTreesClassifier(
        n_estimators=240,
        max_depth=18,
        min_samples_leaf=3,
        class_weight="balanced",
        n_jobs=-1,
        random_state=seed,
    )
    model.fit(features, labels)
    return model


def infer_spatial_signal(
    session: Session, model: ExtraTreesClassifier, grid_step_mm: float = GRID_STEP_MM
) -> dict[str, Any]:
    started = time.perf_counter()
    actor = _trajectory(session)
    observations = validate_actor_trace(session.root, actor)
    footprint = _footprint(actor)
    positions = np.asarray([float(item["signedActorRelativeMm"]) for item in observations])
    grid = np.arange(float(np.min(positions)), float(np.max(positions)) + 0.01, grid_step_mm)
    scores: list[list[float]] = [[] for _ in grid]
    forward: list[list[float]] = [[] for _ in grid]
    reverse: list[list[float]] = [[] for _ in grid]
    support_frames: list[set[int]] = [set() for _ in grid]
    support_records: list[list[dict[str, Any]]] = [[] for _ in grid]
    for observation in observations:
        image = load_gray(session.root / observation["image"])
        tracks = track_multiple_interfaces(image)
        probe = float(observation["signedActorRelativeMm"])
        direction_scores = forward if _direction(observation["phase"]) == "FORWARD" else reverse
        observation_features: list[np.ndarray] = []
        observation_targets: list[int] = []
        for index, coordinate in enumerate(grid):
            pixel_x = _pixel_x_for_coordinate(coordinate, probe, image.shape[1], footprint)
            features = features_at_coordinate(image, tracks, pixel_x)
            if not features:
                continue
            observation_features.extend(features)
            observation_targets.extend([index] * len(features))
        if not observation_features:
            continue
        probabilities = model.predict_proba(np.stack(observation_features))[:, 1]
        by_target: dict[int, list[float]] = {}
        for index, probability in zip(observation_targets, probabilities):
            by_target.setdefault(index, []).append(float(probability))
        for index, values in by_target.items():
            probability = max(values)
            scores[index].append(probability)
            direction_scores[index].append(probability)
            support_frames[index].add(int(observation["observationIndex"]))
            support_records[index].append({
                "observationIndex": int(observation["observationIndex"]),
                "image": observation["image"],
                "direction": _direction(observation["phase"]),
                "probability": probability,
            })
    records = []
    signal = np.zeros(len(grid), dtype=np.float64)
    for index, coordinate in enumerate(grid):
        values = scores[index]
        signal[index] = float(np.quantile(values, 0.75)) if values else 0.0
        records.append({
            "coordinateMm": float(coordinate),
            "scoreQ75": float(signal[index]),
            "scoreMedian": float(np.median(values)) if values else 0.0,
            "observationCount": len(values),
            "distinctFrameCount": len(support_frames[index]),
            "forwardCount": len(forward[index]),
            "reverseCount": len(reverse[index]),
            "forwardScoreQ75": float(np.quantile(forward[index], 0.75)) if forward[index] else 0.0,
            "reverseScoreQ75": float(np.quantile(reverse[index], 0.75)) if reverse[index] else 0.0,
            "topSupportingObservations": sorted(
                support_records[index], key=lambda item: item["probability"], reverse=True
            )[:12],
        })
    return {
        "contract": "autonomous_t2_t8_patch_likelihood_signal_v1",
        "sessionId": session.session_id,
        "actorInputsOnly": True,
        "coordinateSource": "executed_actor_relative_motion",
        "imageAxisSign": IMAGE_AXIS_SIGN,
        "footprintWidthMm": footprint,
        "gridStepMm": grid_step_mm,
        "runtimeSeconds": time.perf_counter() - started,
        "records": records,
    }


def candidate_events(signal_result: dict[str, Any], threshold: float) -> list[dict[str, Any]]:
    records = signal_result["records"]
    coordinates = np.asarray([item["coordinateMm"] for item in records])
    scores = np.asarray([item["scoreQ75"] for item in records])
    step = float(signal_result["gridStepMm"])
    distance = max(1, int(round(MINIMUM_EVENT_SEPARATION_MM / step)))
    peak_indices, properties = find_peaks(
        scores, height=RAW_PEAK_MINIMUM_SCORE, distance=distance,
        prominence=RAW_PEAK_MINIMUM_PROMINENCE)
    candidates = []
    for peak_index, prominence in zip(peak_indices, properties["prominences"]):
        record = records[int(peak_index)]
        direction_supported = (
            record["forwardCount"] >= MINIMUM_DIRECTION_SUPPORT
            and record["reverseCount"] >= MINIMUM_DIRECTION_SUPPORT)
        accepted = direction_supported and float(scores[peak_index]) >= threshold
        rejection_reasons = []
        if not direction_supported:
            rejection_reasons.append("INSUFFICIENT_DIRECTIONAL_SUPPORT")
        if float(scores[peak_index]) < threshold:
            rejection_reasons.append("CLASSIFIER_SCORE_BELOW_FROZEN_MINIMUM")
        support_level = max(RAW_PEAK_MINIMUM_SCORE, 0.8 * float(scores[peak_index]))
        left = int(peak_index)
        right = int(peak_index)
        while left > 0 and scores[left - 1] >= support_level:
            left -= 1
        while right + 1 < len(scores) and scores[right + 1] >= support_level:
            right += 1
        candidates.append({
            "coordinateMm": float(coordinates[peak_index]),
            "score": float(scores[peak_index]),
            "prominence": float(prominence),
            "forwardCount": record["forwardCount"],
            "reverseCount": record["reverseCount"],
            "accepted": bool(accepted),
            "rejectionReasons": rejection_reasons,
            "supportIntervalMm": [float(coordinates[left]), float(coordinates[right])],
            "supportingObservations": record.get("topSupportingObservations", []),
        })
    return candidates


def build_relative_map(candidates: list[dict[str, Any]]) -> dict[str, Any]:
    accepted = sorted(
        [item for item in candidates if item["accepted"]],
        key=lambda item: item["coordinateMm"])
    unique = len(accepted) == len(LEVELS)
    levels = []
    for ordinal, level in enumerate(LEVELS, start=1):
        event = accepted[ordinal - 1] if unique else None
        levels.append({
            "ordinal": ordinal,
            "inferredLevel": level,
            "centerRelativeMm": event["coordinateMm"] if event else None,
            "uncertaintyIntervalMm": event["supportIntervalMm"] if event else None,
            "supportingFrames": [item["observationIndex"]
                                 for item in event["supportingObservations"]] if event else [],
            "eventType": "SUPERVISED_IMAGE_PATCH_RECESS_LIKELIHOOD_PEAK"
                         if event else "UNRESOLVED",
            "accepted": bool(event),
            "classifierScore": event["score"] if event else None,
        })
    return {
        "contract": "autonomous_t2_t8_relative_spine_map_v1",
        "homeRelativeOriginMm": 0.0,
        "coordinateSource": "executed_actor_relative_motion",
        "coveragePrior": "seven_consecutive_levels_T2_to_T8",
        "directionConvention": "cranial_negative_caudad_positive",
        "levels": levels,
        "rejectedImageEvents": [item for item in candidates if not item["accepted"]],
        "predictedT7RelativeMm": accepted[5]["coordinateMm"] if unique else None,
        "overallMapStatus": "ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP" if unique else "INCONCLUSIVE",
        "abstentionReason": None if unique else
            f"Expected seven accepted image events; observed {len(accepted)}",
    }


def score_candidates(candidates: list[dict[str, Any]], references: list[float], tolerance: float = 5.0) -> dict[str, Any]:
    accepted = [item for item in candidates if item["accepted"]]
    remaining = set(range(len(references)))
    matches = []
    for candidate in accepted:
        plausible = [(abs(candidate["coordinateMm"] - references[index]), index)
                     for index in remaining]
        error, index = min(plausible, default=(math.inf, -1))
        if error <= tolerance:
            remaining.remove(index)
            matches.append({"coordinateMm": candidate["coordinateMm"],
                            "referenceIndex": index, "absoluteErrorMm": error})
    tp = len(matches)
    fp = len(accepted) - tp
    fn = len(references) - tp
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {"truePositive": tp, "falsePositive": fp, "falseNegative": fn,
            "precision": precision, "recall": recall, "f1": f1,
            "matches": matches, "exactSeven": tp == 7 and fp == 0 and fn == 0}


def plot_signal(result: dict[str, Any], candidates: list[dict[str, Any]], output: Path) -> None:
    coordinates = [item["coordinateMm"] for item in result["records"]]
    scores = [item["scoreQ75"] for item in result["records"]]
    figure, axis = plt.subplots(figsize=(12, 4))
    axis.plot(coordinates, scores, color="navy", label="actor image likelihood (Q75)")
    for candidate in candidates:
        axis.axvline(candidate["coordinateMm"], color="green" if candidate["accepted"] else "orange",
                     alpha=0.7)
    axis.axvline(0.0, color="black", linestyle="--", linewidth=1, label="Home")
    axis.set_xlabel("Home-relative coordinate (mm)")
    axis.set_ylabel("LF-recess morphology likelihood")
    axis.set_ylim(0, 1)
    axis.grid(alpha=0.2)
    axis.legend(loc="best")
    figure.tight_layout()
    figure.savefig(output, dpi=160)
    plt.close(figure)


def _session(value: str) -> Session:
    session_id, root_text, actor_text = value.split("|", 2)
    root = Path(root_text)
    actor_path = Path(actor_text)
    if not actor_path.is_absolute():
        actor_path = root / actor_path
    return Session(session_id, root, actor_path)


def _labeled_session(value: str) -> tuple[Session, list[float]]:
    session_id, root_text, actor_text, references_text = value.split("|", 3)
    session = _session("|".join((session_id, root_text, actor_text)))
    references = [float(item) for item in references_text.split(",")]
    if len(references) != 7:
        raise ValueError("Each labeled development session requires seven references")
    return session, references


def command_fit(args: argparse.Namespace) -> None:
    specifications: list[tuple[Session, list[float]]] = []
    if args.session:
        if not args.references:
            raise ValueError("--references is required with --session")
        references = [float(value) for value in args.references.split(",")]
        specifications.extend((_session(text), references) for text in args.session)
    if args.labeled_session:
        specifications.extend(_labeled_session(text) for text in args.labeled_session)
    if not specifications:
        raise ValueError("At least one development session is required")
    all_features = []
    all_labels = []
    metadata = []
    session_ids = []
    for session, references in specifications:
        features, labels, records = build_training_examples(
            session, references, args.negative_stride_mm)
        all_features.append(features)
        all_labels.append(labels)
        metadata.extend(records)
        session_ids.append(session.session_id)
    features = np.concatenate(all_features)
    labels = np.concatenate(all_labels)
    model = fit_model(features, labels)
    probabilities = model.predict_proba(features)[:, 1]
    summary = {
        "contract": "autonomous_t2_t8_supervised_development_fit_v1",
        "warning": "Evaluator coordinates were used only to label development images.",
        "sessions": session_ids,
        "exampleCount": int(len(labels)),
        "positiveCount": int(np.sum(labels == 1)),
        "negativeCount": int(np.sum(labels == 0)),
        "trainingRocAuc": float(roc_auc_score(labels, probabilities)),
        "featureDefinition": "normalized interface-centered 16x16 intensity and gradients",
        "negativeSamplingStrideMm": args.negative_stride_mm,
    }
    model_path = Path(args.model)
    output_path = Path(args.output)
    model_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    joblib.dump({"model": model, "summary": summary}, model_path)
    write_json(output_path, {**summary, "examples": metadata})


def command_infer(args: argparse.Namespace) -> None:
    bundle = joblib.load(args.model)
    result = infer_spatial_signal(_session(args.session), bundle["model"])
    candidates = candidate_events(result, args.threshold)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    write_json(output / "spatial_patch_likelihood.json", result)
    write_json(output / "patch_event_candidates.json", {
        "contract": "autonomous_t2_t8_patch_candidates_v1",
        "threshold": args.threshold,
        "candidates": candidates,
    })
    relative_map = build_relative_map(candidates)
    write_json(output / "relative_spine_map.json", relative_map)
    write_json(output / "mapping_uncertainty.json", {
        "contract": "autonomous_t2_t8_support_interval_audit_v1",
        "warning": "Support intervals are image-response extents, not calibrated confidence intervals.",
        "levels": relative_map["levels"],
    })
    plot_signal(result, candidates, output / "spatial_patch_likelihood.png")


def command_score(args: argparse.Namespace) -> None:
    candidate_document = read_json(Path(args.candidates))
    references = [float(value) for value in args.references.split(",")]
    write_json(Path(args.output), {
        "contract": "autonomous_t2_t8_independent_patch_evaluator_v1",
        "warning": "Evaluator-only coordinates loaded after actor candidate freeze.",
        **score_candidates(candidate_document["candidates"], references),
    })


def command_events(args: argparse.Namespace) -> None:
    result = read_json(Path(args.signal))
    candidates = candidate_events(result, args.threshold)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    write_json(output / "patch_event_candidates.json", {
        "contract": "autonomous_t2_t8_patch_candidates_v1",
        "threshold": args.threshold,
        "candidates": candidates,
    })
    write_json(output / "relative_spine_map.json", build_relative_map(candidates))
    plot_signal(result, candidates, output / "spatial_patch_likelihood.png")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    fit = subparsers.add_parser("fit")
    fit.add_argument("--session", action="append",
                     help="session_id|root|actor_path")
    fit.add_argument("--references")
    fit.add_argument("--labeled-session", action="append",
                     help="session_id|root|actor_path|seven comma-separated references")
    fit.add_argument("--negative-stride-mm", type=float, default=4.0)
    fit.add_argument("--model", required=True)
    fit.add_argument("--output", required=True)
    fit.set_defaults(function=command_fit)
    infer = subparsers.add_parser("infer")
    infer.add_argument("--session", required=True)
    infer.add_argument("--model", required=True)
    infer.add_argument("--threshold", type=float, default=FROZEN_ACCEPTANCE_SCORE)
    infer.add_argument("--output", required=True)
    infer.set_defaults(function=command_infer)
    score = subparsers.add_parser("score")
    score.add_argument("--candidates", required=True)
    score.add_argument("--references", required=True)
    score.add_argument("--output", required=True)
    score.set_defaults(function=command_score)
    events = subparsers.add_parser("events")
    events.add_argument("--signal", required=True)
    events.add_argument("--threshold", type=float, default=FROZEN_ACCEPTANCE_SCORE)
    events.add_argument("--output", required=True)
    events.set_defaults(function=command_events)
    return parser


def main() -> None:
    args = build_parser().parse_args()
    args.function(args)


if __name__ == "__main__":
    main()
