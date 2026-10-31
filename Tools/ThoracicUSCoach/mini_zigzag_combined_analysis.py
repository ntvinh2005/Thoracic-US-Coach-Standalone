from __future__ import annotations

import argparse
import csv
import json
from datetime import datetime, timezone
from itertools import combinations
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from mini_zigzag_analysis import FEATURE_NAMES, _feature_vector, _robust_standardizer


def _save_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def leave_one_yaw_out(signatures: np.ndarray) -> dict:
    if signatures.shape[0] != 3 or signatures.shape[1] != 7:
        raise ValueError("Expected three yaw episodes and seven opaque classes")
    flattened = signatures.reshape(signatures.shape[0], signatures.shape[1], -1)
    predictions: list[dict] = []
    correct = 0
    for held_yaw in range(3):
        training = np.delete(flattened, held_yaw, axis=0)
        centroids = np.mean(training, axis=0)
        for class_index in range(7):
            distances = np.linalg.norm(centroids - flattened[held_yaw, class_index], axis=1)
            predicted = int(np.argmin(distances))
            is_correct = predicted == class_index
            correct += int(is_correct)
            predictions.append(
                {
                    "heldYawIndex": held_yaw,
                    "trueOpaqueClass": f"C{class_index:02d}",
                    "predictedOpaqueClass": f"C{predicted:02d}",
                    "correct": is_correct,
                    "nearestDistance": float(distances[predicted]),
                }
            )
    return {"correct": correct, "total": 21, "predictions": predictions}


def cross_yaw_profile_correlations(signatures: np.ndarray) -> list[dict]:
    flattened = signatures.reshape(signatures.shape[0], signatures.shape[1], -1)
    results: list[dict] = []
    for class_index in range(flattened.shape[1]):
        values = []
        for first, second in combinations(range(flattened.shape[0]), 2):
            correlation = float(np.corrcoef(
                flattened[first, class_index], flattened[second, class_index]
            )[0, 1])
            values.append(correlation if np.isfinite(correlation) else 0.0)
        results.append(
            {
                "opaqueClass": f"C{class_index:02d}",
                "minimumPairwiseCorrelation": min(values),
                "meanPairwiseCorrelation": float(np.mean(values)),
                "passesThreshold": min(values) >= 0.80,
            }
        )
    return results


def within_vs_adjacent_separation(signatures: np.ndarray) -> list[dict]:
    flattened = signatures.reshape(signatures.shape[0], signatures.shape[1], -1)
    results: list[dict] = []
    for class_index in range(flattened.shape[1]):
        within = [
            float(np.linalg.norm(flattened[first, class_index] - flattened[second, class_index]))
            for first, second in combinations(range(flattened.shape[0]), 2)
        ]
        adjacent_classes = [candidate for candidate in (class_index - 1, class_index + 1)
                            if 0 <= candidate < flattened.shape[1]]
        adjacent = [
            float(np.linalg.norm(flattened[yaw, class_index] - flattened[other_yaw, adjacent_class]))
            for yaw in range(flattened.shape[0])
            for other_yaw in range(flattened.shape[0])
            for adjacent_class in adjacent_classes
        ]
        results.append(
            {
                "opaqueClass": f"C{class_index:02d}",
                "maximumWithinLevelDistance": max(within),
                "minimumAdjacentLevelDistance": min(adjacent),
                "passes": max(within) < min(adjacent),
            }
        )
    return results


def _load_run(run_root: Path) -> dict:
    analysis_root = run_root / "offline_analysis"
    blind_lateral = json.loads(
        (analysis_root / "blind_lateral_profiles.json").read_text(encoding="utf-8")
    )
    blind_longitudinal = json.loads(
        (analysis_root / "blind_longitudinal_features.json").read_text(encoding="utf-8")
    )
    selection = json.loads(
        (analysis_root / "equal_budget_longitudinal_selection.json").read_text(encoding="utf-8")
    )
    crosswalk = json.loads(
        (analysis_root / "evaluator_only_crosswalk.json").read_text(encoding="utf-8")
    )
    summary = json.loads((run_root / "run_summary.json").read_text(encoding="utf-8"))
    actor_trace = json.loads((run_root / "trace.json").read_text(encoding="utf-8"))
    commanded_yaw = float(summary.get("commandedYawDeg", actor_trace["commandedYawDeg"]))
    tokens = sorted(blind_lateral["profiles"])
    lateral = np.asarray(
        [
            [_feature_vector(sample["features"])
             for sample in blind_lateral["profiles"][token]["samples"]]
            for token in tokens
        ], dtype=np.float64
    )
    baseline_features = {
        item["opaqueSampleId"]: _feature_vector(item["features"])
        for item in blind_longitudinal["samples"]
    }
    longitudinal = np.asarray(
        [
            [baseline_features[sample["opaqueSampleId"]] for sample in region["samples"]]
            for region in selection
        ], dtype=np.float64
    )
    return {
        "root": run_root,
        "summary": summary,
        "commandedYawDeg": commanded_yaw,
        "lateral": lateral,
        "longitudinal": longitudinal,
        "crosswalk": crosswalk,
        "featureInputs": blind_lateral["featureInputs"],
        "excludedInputs": blind_lateral["explicitlyExcludedInputs"],
    }


def _draw_correlation_plot(rows: list[dict], output: Path) -> None:
    width, height = 1200, 650
    margin_left, margin_right, margin_top, margin_bottom = 90, 35, 65, 85
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    plot_width = width - margin_left - margin_right
    plot_height = height - margin_top - margin_bottom
    draw.rectangle(
        (margin_left, margin_top, margin_left + plot_width, margin_top + plot_height), outline="black"
    )
    threshold_y = margin_top + int((1.0 - 0.80) / 2.0 * plot_height)
    draw.line((margin_left, threshold_y, margin_left + plot_width, threshold_y), fill=(190, 40, 40), width=2)
    draw.text((margin_left + 5, threshold_y - 20), "predeclared 0.80", fill=(190, 40, 40))
    group = plot_width / len(rows)
    for index, row in enumerate(rows):
        value = float(row["minimumPairwiseCorrelation"])
        y = margin_top + int((1.0 - value) / 2.0 * plot_height)
        x0 = margin_left + index * group + 22
        x1 = margin_left + (index + 1) * group - 22
        draw.rectangle((x0, y, x1, margin_top + plot_height), fill=(45, 125, 185))
        draw.text((x0 + 8, margin_top + plot_height + 12), row["diagnosticReferenceLevel"], fill="black")
        draw.text((x0 + 4, max(margin_top, y - 20)), f"{value:.3f}", fill="black")
    draw.text((margin_left, 25), "Minimum pairwise cross-yaw correlation of frozen lateral profiles", fill="black")
    draw.text((18, margin_top), "+1", fill="black")
    draw.text((18, margin_top + plot_height // 2), "0", fill="black")
    draw.text((18, margin_top + plot_height - 10), "-1", fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)


def analyze_combined(run_roots: list[Path], output_root: Path) -> dict:
    if len(run_roots) != 3:
        raise ValueError("Exactly three yaw runs are required")
    runs = [_load_run(path) for path in run_roots]
    yaws = [float(item["commandedYawDeg"]) for item in runs]
    if sorted(yaws) != [-3.0, 0.0, 3.0]:
        raise ValueError(f"Expected yaw set [-3, 0, +3], got {yaws}")
    ordered = [runs[yaws.index(value)] for value in (-3.0, 0.0, 3.0)]
    if any(item["featureInputs"] != list(FEATURE_NAMES) for item in ordered):
        raise ValueError("Frozen feature schema mismatch")
    if any(item["excludedInputs"] != ordered[0]["excludedInputs"] for item in ordered[1:]):
        raise ValueError("Excluded-input contract mismatch")

    lateral_raw = np.asarray([item["lateral"] for item in ordered], dtype=np.float64)
    longitudinal_raw = np.asarray([item["longitudinal"] for item in ordered], dtype=np.float64)
    all_vectors = np.concatenate(
        [lateral_raw.reshape(-1, lateral_raw.shape[-1]),
         longitudinal_raw.reshape(-1, longitudinal_raw.shape[-1])], axis=0
    )
    median, scale = _robust_standardizer(all_vectors)
    lateral = (lateral_raw - median) / scale
    longitudinal = (longitudinal_raw - median) / scale

    correlations = cross_yaw_profile_correlations(lateral)
    separation = within_vs_adjacent_separation(lateral)
    lateral_loo = leave_one_yaw_out(lateral)
    longitudinal_loo = leave_one_yaw_out(longitudinal)
    gain = lateral_loo["correct"] - longitudinal_loo["correct"]
    correlation_pass_count = sum(item["passesThreshold"] for item in correlations)
    separation_pass_count = sum(item["passes"] for item in separation)
    all_eligible = all(
        item["summary"]["status"] == "complete_eligible"
        and item["summary"]["profileCount"] == 7
        and item["summary"]["eligibleProfileCount"] == 7
        for item in ordered
    )
    lateral_information_gain = (
        "PASS"
        if all_eligible
        and correlation_pass_count == 7
        and separation_pass_count >= 6
        and lateral_loo["correct"] >= 18
        and gain >= 6
        else "FAIL"
    )

    levels = [item["diagnosticReferenceLevel"] for item in ordered[0]["crosswalk"]]
    anatomical_rows: list[dict] = []
    for class_index, level in enumerate(levels):
        identities = [
            item["crosswalk"][class_index]["provenance"]["anatomicalIdentity"]
            for item in ordered
        ]
        lf_presence = []
        for item in ordered:
            visible = item["crosswalk"][class_index]["provenance"]["firstVisibleObjectBeamCounts"]
            lf_presence.append(int(visible.get("LF " + level, 0) > 0))
        anatomical_rows.append(
            {
                "opaqueClass": f"C{class_index:02d}",
                "diagnosticReferenceLevel": level,
                "identities": identities,
                "levelSpecificLfVisibleYawCount": sum(lf_presence),
                "status": "UNRESOLVED" if "COMBINED_SKELETON_UNRESOLVED" in identities else "INCONCLUSIVE",
            }
        )
    anatomical_identity = "UNRESOLVED"

    for index, row in enumerate(correlations):
        row["diagnosticReferenceLevel"] = levels[index]
        row.update(separation[index])
    output_root.mkdir(parents=True, exist_ok=True)
    _save_json(output_root / "cross_yaw_metrics.json", {
        "correlations": correlations,
        "separation": separation,
        "lateralLeaveOneYawOut": lateral_loo,
        "longitudinalLeaveOneYawOut": longitudinal_loo,
        "anatomicalRows": anatomical_rows,
    })
    with (output_root / "per_level_cross_yaw.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow([
            "level", "minimum_pairwise_correlation", "correlation_pass",
            "maximum_within_distance", "minimum_adjacent_distance", "separation_pass",
            "level_specific_lf_visible_yaw_count", "anatomical_identity",
        ])
        for index, level in enumerate(levels):
            writer.writerow([
                level,
                correlations[index]["minimumPairwiseCorrelation"],
                int(correlations[index]["passesThreshold"]),
                separation[index]["maximumWithinLevelDistance"],
                separation[index]["minimumAdjacentLevelDistance"],
                int(separation[index]["passes"]),
                anatomical_rows[index]["levelSpecificLfVisibleYawCount"],
                anatomical_rows[index]["status"],
            ])
    _draw_correlation_plot(correlations, output_root / "cross_yaw_correlation.png")

    result = {
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "runRoots": [str(item["root"]) for item in ordered],
        "yawOrderDeg": [-3.0, 0.0, 3.0],
        "allThreeRunsEligible": all_eligible,
        "frozenFeatureSchema": True,
        "correlationThreshold": 0.80,
        "levelsPassingCorrelation": correlation_pass_count,
        "requiredLevelsPassingCorrelation": 7,
        "levelsPassingWithinVsAdjacentSeparation": separation_pass_count,
        "requiredLevelsPassingSeparation": 6,
        "lateralLeaveOneYawOutCorrect": lateral_loo["correct"],
        "longitudinalLeaveOneYawOutCorrect": longitudinal_loo["correct"],
        "requiredLateralCorrect": 18,
        "correctPredictionGain": gain,
        "requiredGain": 6,
        "lateralInformationGain": lateral_information_gain,
        "anatomicalIdentity": anatomical_identity,
        "g2Status": "FAIL",
        "g3Status": "NOT_RUN",
        "g4Status": "NOT_RUN",
        "warnings": [
            "All trajectories were evaluator-guided diagnostic collection, not autonomous localization or counting.",
            "Reference levels are used only in the evaluator crosswalk and are not feature inputs.",
            "COMBINED_SKELETON_UNRESOLVED is not a verified rib, lamina, vertebra, or interspace.",
        ],
    }
    _save_json(output_root / "combined_evaluation.json", result)
    lines = [
        "# Mini-Zigzag three-yaw feasibility",
        "",
        f"- LATERAL_INFORMATION_GAIN: **{lateral_information_gain}**",
        f"- ANATOMICAL_IDENTITY: **{anatomical_identity}**",
        f"- Eligible physical runs: **{sum(item['summary']['status'] == 'complete_eligible' for item in ordered)}/3**",
        f"- Cross-yaw correlation pass: **{correlation_pass_count}/7** levels (required 7/7 at >= 0.80)",
        f"- Within-vs-adjacent separation pass: **{separation_pass_count}/7** levels (required >= 6/7)",
        f"- Leave-one-yaw-out: lateral **{lateral_loo['correct']}/21**, longitudinal **{longitudinal_loo['correct']}/21**",
        f"- Equal-budget gain: **{gain:+d}/21** (required >= +6)",
        "- G2: **FAIL** (unchanged)",
        "- G3/G4: **NOT_RUN**",
        "",
        "The collection schedule was evaluator-guided. These results do not demonstrate autonomous localization, counting, or anatomical enumeration.",
        "",
        "Combined Skeleton provenance remains unresolved and is not treated as a verified rib, lamina, vertebra, or interspace identity.",
    ]
    (output_root / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description="Three-yaw mini-zigzag feasibility analysis")
    parser.add_argument("--run-root", type=Path, action="append", required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    result = analyze_combined(
        [path.resolve() for path in args.run_root], args.output_root.resolve()
    )
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
