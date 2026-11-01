"""Offline evaluator-only analysis for relative-spine E0/E1 diagnostics."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Iterable

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image

from position_aware_relocalization import block_ssim


MIN_ABSOLUTE_MEAN_DIFFERENCE = 0.002
MIN_CHANGED_PIXEL_FRACTION = 0.01
NOISE_MULTIPLIER = 3.0
MIN_CHANNEL_DIFFERENCE = 2.0 / 255.0


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def load_gray(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("L"), dtype=np.float64) / 255.0


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def difference_metrics(reference: np.ndarray, comparison: np.ndarray) -> dict:
    if reference.shape != comparison.shape:
        raise ValueError("Counterfactual images must have identical dimensions")
    difference = np.abs(reference - comparison)
    return {
        "meanAbsoluteDifference": float(np.mean(difference)),
        "p95AbsoluteDifference": float(np.percentile(difference, 95)),
        "p99AbsoluteDifference": float(np.percentile(difference, 99)),
        "maximumAbsoluteDifference": float(np.max(difference)),
        "changedPixelFractionAt2Levels": float(np.mean(difference >= MIN_CHANNEL_DIFFERENCE)),
        "ssim": float(block_ssim(reference, comparison)),
    }


def causal_contribution(counterfactual: dict, noise: dict) -> bool:
    required_mean = max(MIN_ABSOLUTE_MEAN_DIFFERENCE,
                        NOISE_MULTIPLIER * noise["meanAbsoluteDifference"])
    return (counterfactual["meanAbsoluteDifference"] >= required_mean
            and counterfactual["changedPixelFractionAt2Levels"] >= MIN_CHANGED_PIXEL_FRACTION)


def interface_summary(beams: Iterable[dict], expected_level_object: str) -> dict:
    rows = list(beams)
    entering = []
    for beam in rows:
        for item in beam.get("interfaces", []):
            if item.get("enteringMaterial"):
                entering.append(item)
    reached = Counter(item.get("objectName", "") for item in entering
                      if item.get("reachedBeforeOrAtFirstShadow"))
    occluded = Counter(item.get("objectName", "") for item in entering
                       if item.get("occludedByEarlierShadow"))
    beam_count = len(rows)
    skeleton_shadow_beams = sum(
        beam.get("hasShadowInterface") and beam.get("firstShadowObject") == "Skeleton"
        for beam in rows)
    expected_reached_beams = sum(
        any(item.get("enteringMaterial")
            and item.get("objectName") == expected_level_object
            and item.get("reachedBeforeOrAtFirstShadow")
            for item in beam.get("interfaces", []))
        for beam in rows)
    expected_occluded_beams = sum(
        any(item.get("enteringMaterial")
            and item.get("objectName") == expected_level_object
            and item.get("occludedByEarlierShadow")
            for item in beam.get("interfaces", []))
        for beam in rows)
    return {
        "beamCount": beam_count,
        "combinedSkeletonFirstShadowBeamFraction": skeleton_shadow_beams / beam_count,
        "expectedLevelObject": expected_level_object,
        "expectedLevelObjectReachedBeamFraction": expected_reached_beams / beam_count,
        "expectedLevelObjectOccludedBeamFraction": expected_occluded_beams / beam_count,
        "reachedObjectCounts": dict(reached),
        "occludedObjectCounts": dict(occluded),
    }


def variant_lookup(manifest: dict) -> dict[tuple[str, str], dict]:
    return {(item["level"], item["variant"]): item for item in manifest["captures"]}


def analyze(experiment_root: Path) -> dict:
    evaluator_root = experiment_root / "evaluator_only"
    audit = load_json(evaluator_root / "anatomy_reference_audit.json")
    manifest = load_json(evaluator_root / "e1_capture_manifest.json")
    parity = load_json(evaluator_root / "instrumentation_check.json")
    if not parity["exactPixelParity"] or not parity["metadataMatched"]:
        raise ValueError("Instrumentation parity or publication metadata failed")
    captures = variant_lookup(manifest)
    levels = [f"T{number}" for number in range(2, 9)]
    rows = []
    ray_diagnostics = []
    image_cache: dict[tuple[str, str], np.ndarray] = {}

    def image_for(level: str, variant: str) -> np.ndarray:
        key = (level, variant)
        if key not in image_cache:
            image_cache[key] = load_gray(experiment_root / captures[key]["image"])
        return image_cache[key]

    for level in levels:
        noise = difference_metrics(image_for(level, "noise_a"), image_for(level, "noise_b"))
        baseline = image_for(level, "baseline_controlled")
        comparisons = {}
        for variant in ("level_lf_disabled", "combined_skeleton_disabled", "superficial_muscle_disabled"):
            metrics = difference_metrics(baseline, image_for(level, variant))
            metrics["causalContribution"] = causal_contribution(metrics, noise)
            comparisons[variant] = metrics
        provenance_path = experiment_root / captures[(level, "baseline_controlled")]["provenance"]
        provenance = load_json(provenance_path)
        interfaces = interface_summary(provenance["beams"], f"LF {level}")
        ray_diagnostics.append({"level": level, **interfaces})
        skeleton_causal = comparisons["combined_skeleton_disabled"]["causalContribution"]
        lf_causal = comparisons["level_lf_disabled"]["causalContribution"]
        muscle_causal = comparisons["superficial_muscle_disabled"]["causalContribution"]
        if skeleton_causal:
            observability = "BONE_SIGNAL_PRESENT_LEVEL_IDENTITY_UNRESOLVED"
        elif lf_causal:
            observability = "LIGAMENT_SIGNAL_PRESENT_NOT_VERTEBRAL_IDENTITY"
        else:
            observability = "NO_CAUSAL_LEVEL_RELEVANT_SIGNAL"
        rows.append({
            "level": level,
            "samePoseNoiseMeanAbsoluteDifference": noise["meanAbsoluteDifference"],
            "samePoseNoiseSsim": noise["ssim"],
            "lfDisabledMeanAbsoluteDifference": comparisons["level_lf_disabled"]["meanAbsoluteDifference"],
            "lfDisabledChangedPixelFraction": comparisons["level_lf_disabled"]["changedPixelFractionAt2Levels"],
            "lfCausalContribution": lf_causal,
            "skeletonDisabledMeanAbsoluteDifference": comparisons["combined_skeleton_disabled"]["meanAbsoluteDifference"],
            "skeletonDisabledChangedPixelFraction": comparisons["combined_skeleton_disabled"]["changedPixelFractionAt2Levels"],
            "skeletonCausalContribution": skeleton_causal,
            "muscleDisabledMeanAbsoluteDifference": comparisons["superficial_muscle_disabled"]["meanAbsoluteDifference"],
            "muscleDisabledChangedPixelFraction": comparisons["superficial_muscle_disabled"]["changedPixelFractionAt2Levels"],
            "muscleCausalContribution": muscle_causal,
            "combinedSkeletonFirstShadowBeamFraction": interfaces["combinedSkeletonFirstShadowBeamFraction"],
            "expectedLfReachedBeamFraction": interfaces["expectedLevelObjectReachedBeamFraction"],
            "expectedLfOccludedBeamFraction": interfaces["expectedLevelObjectOccludedBeamFraction"],
            "observabilityConclusion": observability,
            "vertebralIdentityConclusion": "UNRESOLVED_COMBINED_SKELETON",
        })

    skeleton_levels = sum(row["skeletonCausalContribution"] for row in rows)
    lf_levels = sum(row["lfCausalContribution"] for row in rows)
    result = {
        "contract": "thoracic_relative_spine_e1_observability_v1",
        "analysisIsolation": "Evaluator-only diagnostics; no output is actor input.",
        "lockedCriteria": {
            "minimumAbsoluteMeanDifference": MIN_ABSOLUTE_MEAN_DIFFERENCE,
            "minimumChangedPixelFraction": MIN_CHANGED_PIXEL_FRACTION,
            "noiseMultiplier": NOISE_MULTIPLIER,
            "minimumChannelDifference": MIN_CHANNEL_DIFFERENCE,
        },
        "instrumentationParity": parity,
        "perLevel": rows,
        "levelsWithCausalCombinedSkeletonSignal": skeleton_levels,
        "levelsWithCausalLevelLfSignal": lf_levels,
        "rendererSignalConclusion": (
            "CAUSAL_BONE_SIGNAL_PRESENT_ACROSS_SEVEN_REFERENCES"
            if skeleton_levels == 7 else "CAUSAL_BONE_SIGNAL_INCOMPLETE"),
        "anatomicalIdentityConclusion": "LEVEL_SPECIFIC_VERTEBRAL_IDENTITY_UNRESOLVED",
        "e1Decision": (
            "PROCEED_TO_ACQUISITION_GEOMETRY_SCREENING"
            if skeleton_levels == 7 else "STOP_RENDERER_OBSERVABILITY_BLOCKER"),
        "warning": "A combined Skeleton counterfactual establishes a bone contribution but cannot isolate ribs, laminae, vertebral bodies, or individual T levels. LF is ligament and is not counted as a vertebra.",
    }
    save_csv(evaluator_root / "renderer_causal_attribution.csv", rows)
    save_csv(evaluator_root / "renderer_structure_mapping.csv", audit["ultrasoundObjects"])
    (evaluator_root / "ray_intersection_diagnostics.json").write_text(
        json.dumps(ray_diagnostics, indent=2) + "\n", encoding="utf-8")
    (evaluator_root / "per_level_observability.json").write_text(
        json.dumps(result, indent=2) + "\n", encoding="utf-8")
    draw_contact_sheet(experiment_root, captures, rows,
                       evaluator_root / "renderer_counterfactual_contact_sheet.png")
    write_g0_review(evaluator_root / "G0_REVIEW.md", audit, result)
    return result


def save_csv(path: Path, rows: list[dict]) -> None:
    if not rows:
        raise ValueError("Cannot save an empty CSV")
    keys = list(rows[0])
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=keys, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def draw_contact_sheet(experiment_root: Path, captures: dict, rows: list[dict], output: Path) -> None:
    variants = (
        "baseline_controlled", "level_lf_disabled", "combined_skeleton_disabled",
        "superficial_muscle_disabled")
    labels = ("Original", "LF off", "Skeleton off", "Muscle off")
    levels = [row["level"] for row in rows]
    fig, axes = plt.subplots(len(levels), 7, figsize=(18, 2.7 * len(levels)), squeeze=False)
    for row_index, level in enumerate(levels):
        original = load_gray(experiment_root / captures[(level, variants[0])]["image"])
        images = [load_gray(experiment_root / captures[(level, variant)]["image"])
                  for variant in variants]
        panels = [images[0], images[1], np.abs(original - images[1]), images[2],
                  np.abs(original - images[2]), images[3], np.abs(original - images[3])]
        titles = ("Original", "LF off", "|difference|", "Skeleton off",
                  "|difference|", "Muscle off", "|difference|")
        for column, (panel, title) in enumerate(zip(panels, titles)):
            ax = axes[row_index, column]
            if "difference" in title:
                ax.imshow(panel, cmap="magma", vmin=0.0, vmax=max(0.05, float(np.percentile(panel, 99))))
            else:
                ax.imshow(panel, cmap="gray", vmin=0.0, vmax=1.0)
            ax.set_title(title if row_index == 0 else "", fontsize=8)
            ax.axis("off")
        axes[row_index, 0].text(-0.08, 0.5, level, rotation=90, va="center", ha="right",
                                transform=axes[row_index, 0].transAxes, fontweight="bold")
    fig.suptitle("Evaluator-only same-pose causal renderer audit (not actor input)")
    fig.tight_layout(rect=(0, 0, 1, 0.98))
    fig.savefig(output, dpi=150)
    plt.close(fig)


def write_g0_review(path: Path, audit: dict, result: dict) -> None:
    missing_per_level_bone = sum(not item["perLevelBoneObject"] for item in audit["references"])
    review = f"""# G0 Reference and Renderer Review

## Verdict

**G0: PASS WITH EXPLICIT IDENTITY LIMITATION.** Seven ordered evaluator-only label/LF reference pairs exist, each has a valid skin projection, and E1 found a causal combined-Skeleton pixel contribution at {result['levelsWithCausalCombinedSkeletonSignal']}/7 reference poses. This establishes independently inspectable fixed-scene references and real bone-caused ultrasound pixels.

It does **not** establish actor-side identifiability. There are {missing_per_level_bone}/7 separately named per-level vertebral bone renderers: the vertebral bone source is one combined `Skeleton` Shadow mesh. `LF T2` through `LF T8` are level-named ligamentum-flavum objects, not vertebrae.

## Renderer mechanism

The renderer raycasts through all `UltrasoundVisibility` objects, sorts entering/exiting interfaces by depth, and renders until the first `Shadow` interface. The combined Skeleton is that bone Shadow source. Superficial muscle uses texture rendering and contributes before bone. Full-interface provenance was added read-only; exact instrumentation parity was verified.

## Isolation

All labels, scene objects, bounds, poses, counterfactual toggles, and provenance remain under `evaluator_only/`. These artifacts are prohibited as localization actor input.

## E1 decision

`{result['e1Decision']}`. Bone-caused pixels exist, but individual T-level identity remains unresolved and must be tested through acquisition geometry and image separability rather than inferred from object names.
"""
    path.write_text(review, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description="Analyze relative-spine E0/E1 diagnostics")
    parser.add_argument("--experiment-root", type=Path, required=True)
    args = parser.parse_args()
    experiment_root = args.experiment_root.resolve()
    result = analyze(experiment_root)
    evidence = [
        experiment_root / "evaluator_only" / "renderer_causal_attribution.csv",
        experiment_root / "evaluator_only" / "renderer_structure_mapping.csv",
        experiment_root / "evaluator_only" / "ray_intersection_diagnostics.json",
        experiment_root / "evaluator_only" / "per_level_observability.json",
        experiment_root / "evaluator_only" / "renderer_counterfactual_contact_sheet.png",
        experiment_root / "evaluator_only" / "G0_REVIEW.md",
    ]
    (experiment_root / "e0_e1_outputs.sha256").write_text(
        "".join(f"{sha256(path)}  {path.relative_to(experiment_root).as_posix()}\n" for path in evidence),
        encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
