from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from sweep_count_analysis import robust_z


DEFAULT_RUNS = (
    "SWEEP_COUNT_M1_20261030_161058_921",
    "SWEEP_COUNT_M1_20261030_161137_153",
    "SWEEP_COUNT_M1_20261030_161212_707",
)
YAW_NAMES = ("yaw_0", "yaw_minus_3", "yaw_plus_3")
FEATURE_NAMES = ("bone_interface", "shadow_fraction", "interface_depth", "morphology", "transition", "combined")


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def read_signal(path: Path) -> dict[str, np.ndarray]:
    values = np.genfromtxt(path, delimiter=",", names=True)
    return {name: np.asarray(values[name], dtype=np.float64) for name in values.dtype.names or ()}


def event_indices(document: dict, key: str) -> list[int]:
    return [int(item["observationIndex"]) for item in document[key]]


def cluster_cross_yaw_events(event_lists: list[list[int]], tolerance_frames: int = 3) -> list[dict]:
    clusters: list[dict] = []
    used: list[set[int]] = [set() for _ in event_lists]
    for run_index, events in enumerate(event_lists):
        for event in events:
            if event in used[run_index]:
                continue
            occurrences = {run_index: event}
            used[run_index].add(event)
            for other_index, other_events in enumerate(event_lists):
                if other_index == run_index:
                    continue
                available = [value for value in other_events if value not in used[other_index]]
                if not available:
                    continue
                nearest = min(available, key=lambda value: abs(value - event))
                if abs(nearest - event) <= tolerance_frames:
                    occurrences[other_index] = nearest
                    used[other_index].add(nearest)
            clusters.append(
                {
                    "centerObservationIndex": int(round(float(np.mean(list(occurrences.values()))))),
                    "occurrences": {YAW_NAMES[index]: value for index, value in sorted(occurrences.items())},
                    "yawSupport": len(occurrences),
                    "repeatability": "SUPPORTED" if len(occurrences) == len(event_lists) else "AMBIGUOUS",
                }
            )
    return sorted(clusters, key=lambda item: item["centerObservationIndex"])


def pairwise_correlations(signals: list[dict[str, np.ndarray]]) -> dict:
    result: dict[str, dict] = {}
    for feature in FEATURE_NAMES:
        pairs = {}
        values = []
        for first, second in ((0, 1), (0, 2), (1, 2)):
            correlation = float(np.corrcoef(signals[first][feature], signals[second][feature])[0, 1])
            pairs[f"{YAW_NAMES[first]}_vs_{YAW_NAMES[second]}"] = correlation
            values.append(correlation)
        result[feature] = {"pairs": pairs, "minimum": min(values), "mean": float(np.mean(values))}
    return result


def image_descriptor(path: Path) -> tuple[dict, list[tuple[int, int]], tuple[int, int, int, int]]:
    with Image.open(path) as source:
        gray = source.convert("L").resize((256, 256), Image.Resampling.BILINEAR)
    values = np.asarray(gray, dtype=np.float64) / 255.0
    height, width = values.shape
    x0, x1 = int(0.08 * width), int(0.92 * width)
    y0, y1 = int(0.15 * height), int(0.90 * height)
    roi = values[y0:y1, x0:x1]
    p99 = max(float(np.quantile(roi, 0.99)), 1e-4)
    normalized = np.clip(roi / p99, 0.0, 1.0)
    vertical_gradient = np.maximum(np.diff(normalized, axis=0), 0.0)
    search_start = int(0.10 * vertical_gradient.shape[0])
    search_end = int(0.82 * vertical_gradient.shape[0])
    searchable = vertical_gradient[search_start:search_end]
    peak_rows = np.argmax(searchable, axis=0) + search_start
    peak_values = np.max(searchable, axis=0)
    threshold = max(0.06, float(np.quantile(peak_values, 0.55)))
    valid = peak_values >= threshold
    points = [(x0 + column, y0 + int(peak_rows[column])) for column in range(len(peak_rows)) if valid[column]]
    deep_dark = []
    for column, row in enumerate(peak_rows):
        if not valid[column]:
            continue
        below = normalized[min(int(row) + 8, normalized.shape[0] - 1):, column]
        if below.size:
            deep_dark.append(float(np.mean(below < 0.12)))
    descriptor = {
        "validInterfaceColumnFraction": float(np.mean(valid)),
        "meanInterfaceGradient": float(np.mean(peak_values[valid])) if np.any(valid) else 0.0,
        "meanInterfaceDepth": float(np.mean(peak_rows[valid]) / max(1, normalized.shape[0] - 1)) if np.any(valid) else 1.0,
        "interfaceDepthStd": float(np.std(peak_rows[valid]) / max(1, normalized.shape[0] - 1)) if np.any(valid) else 0.0,
        "distalDarkFraction": float(np.mean(deep_dark)) if deep_dark else 0.0,
    }
    return descriptor, points, (x0, y0, x1, y1)


def annotate_frame(source: Path, destination: Path, caption: str) -> dict:
    descriptor, points, roi = image_descriptor(source)
    with Image.open(source) as opened:
        image = opened.convert("RGB")
    scale_x, scale_y = image.width / 256.0, image.height / 256.0
    draw = ImageDraw.Draw(image)
    box = tuple(int(value * (scale_x if index % 2 == 0 else scale_y)) for index, value in enumerate(roi))
    draw.rectangle(box, outline=(255, 215, 0), width=max(2, image.width // 256))
    sampled = points[::max(1, len(points) // 80)]
    for x, y in sampled:
        px, py = int(x * scale_x), int(y * scale_y)
        draw.ellipse((px - 2, py - 2, px + 2, py + 2), fill=(0, 255, 255))
    draw.rectangle((0, image.height - 34, image.width, image.height), fill=(0, 0, 0))
    draw.text((8, image.height - 27), caption, fill="white", font=ImageFont.load_default())
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination)
    return descriptor


def make_contact_sheet(paths: list[Path], destination: Path, title: str) -> None:
    tile = 360
    canvas = Image.new("RGB", (tile * len(paths), tile + 36), "white")
    draw = ImageDraw.Draw(canvas)
    for index, path in enumerate(paths):
        with Image.open(path) as source:
            image = source.convert("RGB").resize((tile, tile), Image.Resampling.BILINEAR)
        canvas.paste(image, (index * tile, 0))
    draw.text((8, tile + 10), title, fill="black")
    destination.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(destination)


def draw_blind_curves(path: Path, signals: list[dict[str, np.ndarray]]) -> None:
    width, height = 1500, 1200
    left, right, top, bottom = 90, 30, 45, 45
    panels = ("bone_interface", "shadow_fraction", "interface_depth", "combined")
    colors = ((30, 100, 200), (220, 70, 50), (40, 155, 80))
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    draw.text((left, 12), "Blind feature curves: labels and evaluator coordinates not used", fill="black")
    panel_height = (height - top - bottom) // len(panels)
    for panel_index, feature in enumerate(panels):
        y_top = top + panel_index * panel_height
        y_bottom = y_top + panel_height - 28
        all_values = np.concatenate([signal[feature] for signal in signals])
        y_min, y_max = float(np.min(all_values)), float(np.max(all_values))
        padding = max(1e-3, 0.08 * (y_max - y_min))
        y_min, y_max = y_min - padding, y_max + padding
        draw.rectangle((left, y_top, width - right, y_bottom), outline="black")
        draw.text((8, y_top + 4), feature, fill="black")
        for run_index, signal in enumerate(signals):
            values = signal[feature]
            points = []
            for index, value in enumerate(values):
                x = left + int(index / max(1, len(values) - 1) * (width - left - right))
                y = y_bottom - int((float(value) - y_min) / max(1e-9, y_max - y_min) * (y_bottom - y_top))
                points.append((x, y))
            draw.line(points, fill=colors[run_index], width=2)
        draw.text((left, y_bottom + 5), "observation index 0", fill="black")
        draw.text((width - right - 125, y_bottom + 5), "observation index 59", fill="black")
    x = left
    for name, color in zip(YAW_NAMES, colors):
        draw.line((x, height - 18, x + 30, height - 18), fill=color, width=4)
        draw.text((x + 36, height - 25), name, fill="black")
        x += 190
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def region_indices(coordinates: np.ndarray, references: list[float], level_index: int) -> list[int]:
    lower = -np.inf if level_index == 0 else 0.5 * (references[level_index - 1] + references[level_index])
    upper = np.inf if level_index == len(references) - 1 else 0.5 * (references[level_index] + references[level_index + 1])
    return [index for index, coordinate in enumerate(coordinates) if lower <= float(coordinate) < upper]


def main() -> int:
    repository = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description="Blind-first offline event-semantics audit")
    parser.add_argument("--run-root", type=Path, action="append")
    parser.add_argument(
        "--output", type=Path,
        default=repository / "Artifacts" / "ThoracicUSCoach" / "sweep_count"
        / "MILESTONE_1_5_EVENT_SEMANTICS_AUDIT_20261030",
    )
    args = parser.parse_args()
    base = repository / "Artifacts" / "ThoracicUSCoach" / "sweep_count"
    run_roots = [path.resolve() for path in args.run_root] if args.run_root else [base / name for name in DEFAULT_RUNS]
    if len(run_roots) != 3 or any(not path.is_dir() for path in run_roots):
        raise ValueError("Exactly three existing run roots are required in yaw 0, -3, +3 order")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    # Phase A is deliberately blind: only actor-side signals, frames, and observation indices are read.
    signals = [read_signal(path / "signal.csv") for path in run_roots]
    candidates = [read_json(path / "candidate_events.json") for path in run_roots]
    categories = {
        "positive_peak": [event_indices(document, "boundaryCorrectedCandidateEvents") for document in candidates],
        "negative_valley": [event_indices(document, "diagnosticNegativeValleys") for document in candidates],
        "transition": [event_indices(document, "diagnosticTransitionEvents") for document in candidates],
    }
    clusters = {name: cluster_cross_yaw_events(values) for name, values in categories.items()}
    correlations = pairwise_correlations(signals)
    blind = {
        "phase": "A_BLIND_PIXELS_AND_ACTION_INDEX_ONLY",
        "runOrder": list(YAW_NAMES),
        "featureCorrelationsAcrossYaw": correlations,
        "eventClusters": clusters,
        "importantConstraint": "No T label, Unity reference coordinate, mesh name, or world pose was loaded in Phase A.",
    }
    write_json(output / "blind_analysis.json", blind)
    draw_blind_curves(output / "blind_feature_curves.png", signals)

    blind_rows = []
    for category, items in clusters.items():
        for ordinal, item in enumerate(items, 1):
            row = {"blindEventId": f"{category[0].upper()}{ordinal:02d}", "category": category, **item}
            blind_rows.append(row)
            representative = item["centerObservationIndex"]
            annotate_frame(
                run_roots[0] / "frames" / f"frame_{representative:04d}.png",
                output / "blind_representative_frames" / f"{row['blindEventId']}_frame_{representative:04d}.png",
                f"{row['blindEventId']} frame {representative}; cyan=strongest rising interface; yellow=ROI",
            )
    write_json(output / "blind_event_catalog.json", blind_rows)

    # Phase B starts only after the blind catalog is fixed and written.
    references_document = read_json(run_roots[0] / "evaluator_only" / "references.json")
    references = references_document["references"]
    reference_coordinates = [float(item["corridorCoordinateMm"]) for item in references]
    privileged = [read_json(path / "evaluator_only" / "trace_privileged.json") for path in run_roots]
    coordinate_arrays = [
        np.asarray([item["corridorCoordinateMm"] for item in document["observations"]], dtype=np.float64)
        for document in privileged
    ]
    feature_z = []
    for signal in signals:
        feature_z.append(
            {
                "bone": robust_z(signal["bone_interface"]),
                "shadow": robust_z(signal["shadow_fraction"]),
                "depthTerm": -0.35 * robust_z(signal["interface_depth"]),
            }
        )

    level_rows = []
    for level_index, reference in enumerate(references):
        level = reference["level"]
        nearest = [int(np.argmin(np.abs(values - reference_coordinates[level_index]))) for values in coordinate_arrays]
        events_by_yaw = []
        for run_index in range(3):
            indices = region_indices(coordinate_arrays[run_index], reference_coordinates, level_index)
            events_by_yaw.append(
                {
                    "yaw": YAW_NAMES[run_index],
                    "positivePeaks": [value for value in categories["positive_peak"][run_index] if value in indices],
                    "negativeValleys": [value for value in categories["negative_valley"][run_index] if value in indices],
                    "transitions": [value for value in categories["transition"][run_index] if value in indices],
                }
            )
        positive_all = all(item["positivePeaks"] for item in events_by_yaw)
        valley_all = all(item["negativeValleys"] for item in events_by_yaw)
        transition_all = all(item["transitions"] for item in events_by_yaw)
        frame_descriptors = []
        annotated_paths = []
        for run_index, frame_index in enumerate(nearest):
            annotated_path = output / "per_level_frames" / f"{level}_{YAW_NAMES[run_index]}_frame_{frame_index:04d}.png"
            descriptor = annotate_frame(
                run_roots[run_index] / "frames" / f"frame_{frame_index:04d}.png",
                annotated_path,
                f"blind frame {frame_index}; yaw={YAW_NAMES[run_index]}; evaluator crosswalk added after extraction",
            )
            frame_descriptors.append(descriptor)
            annotated_paths.append(annotated_path)
        make_contact_sheet(
            annotated_paths,
            output / "per_level_contact_sheets" / f"{level}_three_yaw.png",
            f"{level} evaluator crosswalk after blind extraction; left-to-right yaw 0, -3, +3",
        )
        means = {
            feature: float(np.mean([signals[index][feature][nearest[index]] for index in range(3)]))
            for feature in ("bone_interface", "shadow_fraction", "interface_depth", "combined")
        }
        contributions = {
            name: float(np.mean([feature_z[index][name][nearest[index]] for index in range(3)]))
            for name in ("bone", "shadow", "depthTerm")
        }
        consistent_signature = (
            "positive_peak" if positive_all else
            "negative_valley" if valley_all else
            "transition" if transition_all else
            "mixed_or_incomplete"
        )
        level_rows.append(
            {
                "level": level,
                "status": "AMBIGUOUS",
                "representativeObservationIndices": dict(zip(YAW_NAMES, nearest)),
                "blindImageDescriptors": frame_descriptors,
                "meanExtractedFeatures": means,
                "meanMorphologyContributions": contributions,
                "eventsByYaw": events_by_yaw,
                "crossYawSignature": consistent_signature,
                "imageEvidence": "A bright rising interface and distal low-signal/shadow region are observable.",
                "evaluatorRendererEvidence": "The combined Skeleton is a first-hit Shadow source; the level-specific LF mesh is Normal.",
                "anatomicalIdentity": "Cannot distinguish rib, lamina, spinous process, or interspace from these pixels or evaluator artifacts.",
                "reason": "The column-wise strongest-gradient trace mixes upper, central, and bilateral lower interfaces, and no per-object render provenance or mask proves vertebral identity.",
            }
        )
    write_json(output / "per_level_event_semantics.json", level_rows)
    with (output / "per_level_event_semantics.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(("level", "status", "cross_yaw_signature", "bone", "shadow", "depth", "combined", "anatomical_identity"))
        for row in level_rows:
            feature = row["meanExtractedFeatures"]
            writer.writerow((row["level"], row["status"], row["crossYawSignature"], feature["bone_interface"], feature["shadow_fraction"], feature["interface_depth"], feature["combined"], row["anatomicalIdentity"]))

    summary = {
        "eventIdentity": {
            "status": "AMBIGUOUS",
            "supported": "The frames and renderer prove repeatable bone-shadow interface/occlusion changes.",
            "notSupported": "No evidence uniquely assigns a peak, valley, or transition to a rib, lamina, spinous process, interspace, or one vertebral level.",
            "featureExtractorLimitation": "The wide-ROI column-wise strongest-gradient trace mixes upper tissue edges, the central arch, and bilateral lower interfaces rather than tracking one coherent anatomical surface.",
        },
        "consistency": {
            "status": "AMBIGUOUS",
            "supported": "The overall feature curves and the three positive event regions are highly repeatable across yaw.",
            "notSupported": "No single event polarity occurs once at every one of the seven evaluator levels.",
        },
        "observability": {
            "status": "NOT_OBSERVABLE",
            "supported": "Bone-shadow geometry is observable along the centerline trajectory.",
            "notSupported": "Seven distinct vertebral identities are not observable from the present single-line images and available ground truth provenance.",
        },
        "scientificGate": {"G2": "FAIL", "G3": "NOT_RUN", "G4": "NOT_RUN"},
        "rendererProvenance": {
            "boneSource": references_document["boneObject"],
            "boneRenderType": references_document["boneRenderType"],
            "levelSpecificAnatomy": [item["anatomyObject"] for item in references],
            "levelSpecificRenderTypes": sorted(set(item["ultrasoundRenderType"] for item in references)),
            "limitation": "The evaluator contains no per-frame per-pixel object identity or segmentation mask, so renderer provenance cannot be assigned to each extracted interface point.",
        },
    }
    write_json(output / "audit_conclusions.json", summary)

    supported_clusters = {
        name: [item["centerObservationIndex"] for item in items if item["yawSupport"] == 3]
        for name, items in clusters.items()
    }
    report_lines = [
        "# Offline event-semantics audit", "", "G2 remains **FAIL**. G3/G4 remain **NOT_RUN**.", "",
        "## Method and leakage control", "",
        "Phase A loaded only actor-side `signal.csv`, ultrasound PNGs, and observation indices. It wrote `blind_analysis.json`, `blind_event_catalog.json`, the raw curves, and blind representative frames before Phase B opened evaluator traces or T-level references.",
        "No threshold, detector, training, scan trajectory, capture, or scientific gate was changed.", "",
        "## Blind-first result", "",
        f"Cross-yaw correlation was at least {correlations['combined']['minimum']:.4f} for the combined signal, {correlations['shadow_fraction']['minimum']:.4f} for shadow fraction, and {correlations['bone_interface']['minimum']:.4f} for bone-interface response. Transition correlation was lower at {correlations['transition']['minimum']:.4f}.",
        f"Three-yaw-supported blind clusters: positive peaks {supported_clusters['positive_peak']}; negative valleys {supported_clusters['negative_valley']}; transitions {supported_clusters['transition']}.",
        "No blind event family produced exactly one repeatable occurrence at seven positions.", "",
        "## Why polarity changes", "",
        "The combined signal is a robust z-score relative to the whole sweep: `z(bone_interface) + z(shadow_fraction) - 0.35*z(interface_depth)`, with a diagnostic transition term before smoothing. Its sign is therefore relative, not a categorical statement that bone is present or absent.",
        "T2–T3 are negative mainly because shadow fraction is far below the sweep median. T4 is negative mainly because interface-gradient strength is low. T5 becomes positive because shadow fraction is high. T6–T7 are positive because interface-gradient strength is very high with additional shadow support. T8 becomes negative because interface and shadow responses both fall.",
        "The same polarity pattern recurs across all three achieved yaw trajectories, so yaw noise does not explain the level-dependent sign.", "",
        "## Three questions", "",
        "1. **Event identity — AMBIGUOUS.** Bright edges and distal darkness support bone-shadow interface/occlusion. However, the cyan annotations show that the wide-ROI column-wise tracker mixes upper tissue edges, the central arch, and bilateral lower curves. The scene exposes bones as one combined `Skeleton` Shadow source, while `LF T2…T8` are Normal. With no per-pixel object mask, the evidence cannot distinguish rib, lamina, spinous process, ligamentum flavum, or interspace.",
        "2. **Consistency — AMBIGUOUS.** The waveform is highly repeatable, but a single anatomical event family is not: positive peaks repeat at only three positions; repeatable valleys and transitions occur at different counts and offsets. This is consistent with changing beam intersection geometry, not seven copies of one validated structure.",
        "3. **Observability — NOT_OBSERVABLE for vertebral counting.** Bone-shadow geometry is observable, but seven independently identifiable vertebral events are not verifiable along this single centerline trajectory with current provenance.", "",
        "## Per-level matrix", "",
        "| Level | Status | Cross-yaw signature | Mean z contributions: bone / shadow / depth | Identity |", "|---|---|---|---|---|",
    ]
    for row in level_rows:
        contribution = row["meanMorphologyContributions"]
        report_lines.append(
            f"| {row['level']} | {row['status']} | {row['crossYawSignature']} | "
            f"{contribution['bone']:.3f} / {contribution['shadow']:.3f} / {contribution['depthTerm']:.3f} | "
            "rib/lamina/spinous/LF/interspace unresolved |"
        )
    report_lines.extend(
        [
            "", "Every level is `AMBIGUOUS`: a visual response exists, but its anatomical identity is not independently resolved. The global vertebral-counting conclusion is `NOT_OBSERVABLE`, because no event family supplies seven validated identities.",
            "", "## Evidence", "",
            "- `blind_feature_curves.png`: raw feature overlays for all three yaw runs without evaluator labels.",
            "- `blind_event_catalog.json` and `blind_representative_frames/`: repeatability catalog fixed before evaluator crosswalk.",
            "- `per_level_event_semantics.json` and `per_level_contact_sheets/`: evaluator-only T2–T8 crosswalk and three-yaw annotated frames.",
            "- `audit_conclusions.json`: machine-readable answers and unchanged gate status.",
            "", "## Decision boundary", "",
            "A mini-zigzag is the better-motivated next observability experiment because lateral samples could test whether a feature persists at midline posterior elements or moves with rib/lateral geometry. It should be paired with evaluator-only per-object hit provenance or masks; otherwise more views may remain anatomically ambiguous. It still requires separate approval, a bounded trajectory, and a predeclared evaluator protocol. Continuing to tune a 1D positive threshold is not supported by this audit.",
        ]
    )
    (output / "REPORT.md").write_text("\n".join(report_lines) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "conclusions": summary, "levels": [{"level": row["level"], "status": row["status"], "signature": row["crossYawSignature"]} for row in level_rows]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
