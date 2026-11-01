"""Evaluator-only E4 center/interlevel separability analysis."""

from __future__ import annotations

import argparse
import itertools
import json
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np

from position_aware_relocalization import block_ssim
from relative_spine_e2_analysis import (
    cosine_distance,
    load_gray,
    morphology_descriptor,
    provenance_features,
    sha256,
)


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def validate_capture(experiment_root: Path, capture: dict) -> None:
    if not all(capture.get(key) for key in
               ("contactValid", "workspaceValid", "frameIntegrityValid", "provenanceMatched")):
        raise ValueError(f"Invalid E4 capture: {capture['anchorId']} {capture['repeat']}")
    path = experiment_root / capture["image"]
    if sha256(path) != capture["imageSha256"]:
        raise ValueError(f"E4 image hash mismatch: {path}")


def normalized_centroid(vectors: list[np.ndarray]) -> np.ndarray:
    centroid = np.mean(np.asarray(vectors), axis=0)
    return centroid / max(float(np.linalg.norm(centroid)), 1e-9)


def leave_anchor_out_classification(
        anchor_ids: list[str], labels: dict[str, str], reference: dict[str, np.ndarray],
        query: dict[str, np.ndarray]) -> list[dict]:
    rows = []
    classes = sorted(set(labels.values()))
    for anchor in anchor_ids:
        centroids = {}
        for class_name in classes:
            candidates = [reference[item] for item in anchor_ids
                          if item != anchor and labels[item] == class_name]
            if not candidates:
                raise ValueError("Each E4 class requires another independent anchor")
            centroids[class_name] = normalized_centroid(candidates)
        distances = {class_name: cosine_distance(query[anchor], centroid)
                     for class_name, centroid in centroids.items()}
        predicted = min(distances, key=distances.get)
        rows.append({
            "anchorId": anchor,
            "actual": labels[anchor],
            "predicted": predicted,
            "correct": predicted == labels[anchor],
            "centerDistance": distances["LEVEL_CENTER"],
            "interlevelDistance": distances["INTERLEVEL_MIDPOINT"],
        })
    return rows


def permutation_p_value(anchor_ids: list[str], labels: dict[str, str],
                        reference: dict[str, np.ndarray], query: dict[str, np.ndarray],
                        observed_correct: int) -> dict:
    center_count = sum(labels[item] == "LEVEL_CENTER" for item in anchor_ids)
    exceed = 0
    total = 0
    for centers in itertools.combinations(anchor_ids, center_count):
        center_set = set(centers)
        candidate = {item: ("LEVEL_CENTER" if item in center_set else "INTERLEVEL_MIDPOINT")
                     for item in anchor_ids}
        rows = leave_anchor_out_classification(anchor_ids, candidate, reference, query)
        score = sum(item["correct"] for item in rows)
        exceed += score >= observed_correct
        total += 1
    return {
        "method": "Exact enumeration of all label assignments with seven centers and six interlevels",
        "assignments": total,
        "atLeastObserved": exceed,
        "pValue": exceed / total,
    }


def analyze(experiment_root: Path, subdir: str = "e4_separability") -> dict:
    root = experiment_root / "evaluator_only" / subdir
    manifest = load_json(root / "capture_manifest.json")
    captures = manifest["captures"]
    lookup = {(item["anchorId"], item["repeat"]): item for item in captures}
    anchor_ids = [item["anchorId"] for item in sorted(
        (capture for capture in captures if capture["repeat"] == "repeat_a"),
        key=lambda item: item["evaluatorCorridorCoordinateMm"])]
    if len(anchor_ids) != 13 or len(lookup) != 26:
        raise ValueError("E4 requires thirteen ordered anchors with two repeats each")
    images = {}
    descriptors = {}
    for key, capture in lookup.items():
        validate_capture(experiment_root, capture)
        images[key] = load_gray(experiment_root / capture["image"])
        descriptors[key] = morphology_descriptor(images[key])
    labels = {anchor: lookup[(anchor, "repeat_a")]["anchorType"] for anchor in anchor_ids}
    coordinates = {anchor: lookup[(anchor, "repeat_a")]["evaluatorCorridorCoordinateMm"]
                   for anchor in anchor_ids}
    same_pose = [{
        "anchorId": anchor,
        "anchorType": labels[anchor],
        "coordinateMm": coordinates[anchor],
        "ssim": float(block_ssim(images[(anchor, "repeat_a")], images[(anchor, "repeat_b")])),
        "descriptorDistance": cosine_distance(
            descriptors[(anchor, "repeat_a")], descriptors[(anchor, "repeat_b")]),
    } for anchor in anchor_ids]
    adjacent = [{
        "before": before,
        "after": after,
        "separationMm": coordinates[after] - coordinates[before],
        "descriptorDistance": cosine_distance(
            descriptors[(before, "repeat_a")], descriptors[(after, "repeat_a")]),
    } for before, after in zip(anchor_ids[:-1], anchor_ids[1:])]
    reference = {anchor: descriptors[(anchor, "repeat_a")] for anchor in anchor_ids}
    query = {anchor: descriptors[(anchor, "repeat_b")] for anchor in anchor_ids}
    position_matches = []
    for anchor in anchor_ids:
        ordered = sorted(((candidate, cosine_distance(query[anchor], reference[candidate]))
                          for candidate in anchor_ids), key=lambda item: item[1])
        position_matches.append({
            "anchorId": anchor,
            "predictedAnchor": ordered[0][0],
            "correct": ordered[0][0] == anchor,
            "bestDistance": ordered[0][1],
            "secondBestDistance": ordered[1][1],
            "margin": ordered[1][1] - ordered[0][1],
        })
    class_rows = leave_anchor_out_classification(anchor_ids, labels, reference, query)
    class_correct = sum(item["correct"] for item in class_rows)
    null = permutation_p_value(anchor_ids, labels, reference, query, class_correct)
    within_max = max(item["descriptorDistance"] for item in same_pose)
    adjacent_min = min(item["descriptorDistance"] for item in adjacent)
    topology_supported = (
        class_correct >= 12 and null["pValue"] <= 0.05 and adjacent_min > within_max
    )
    per_anchor = []
    for anchor in anchor_ids:
        capture = lookup[(anchor, "repeat_a")]
        per_anchor.append({
            "anchorId": anchor,
            "anchorType": labels[anchor],
            "coordinateMm": coordinates[anchor],
            "image": capture["image"],
            "repeatImage": lookup[(anchor, "repeat_b")]["image"],
            **provenance_features(experiment_root / capture["provenance"]),
        })
    result = {
        "contract": "thoracic_relative_spine_e4_separability_analysis_v1",
        "geometry": manifest["geometry"],
        "analysisIsolation": "Evaluator-only diagnostic labels; no labels or reference coordinates are actor inputs.",
        "representation": (
            "The frozen E2 normalized pooled intensity and gradient morphology descriptor; repeat_a is the "
            "reference and independently rendered repeat_b is the query."
        ),
        "anchorCount": 13,
        "validCaptureCount": 26,
        "samePose": same_pose,
        "adjacentTransitions": adjacent,
        "positionSpecificRepeatMatches": position_matches,
        "positionSpecificCorrect": sum(item["correct"] for item in position_matches),
        "centerInterlevelLeaveAnchorOut": class_rows,
        "centerInterlevelCorrect": class_correct,
        "centerInterlevelAccuracy": class_correct / 13.0,
        "permutationControl": null,
        "maximumSamePoseDescriptorDistance": within_max,
        "minimumAdjacentAnchorDescriptorDistance": adjacent_min,
        "perAnchor": per_anchor,
        "anatomicalTopologyConclusion": (
            "SUPPORTED_RECURRENT_CENTER_INTERLEVEL_MORPHOLOGY"
            if topology_supported else "AMBIGUOUS_POSITION_SPECIFIC_BUT_NO_GENERAL_CENTER_INTERLEVEL_SIGNATURE"
        ),
        "e4Decision": (
            "PROCEED_TO_E3_TARGETED_TRANSITION_WIDTH_SAMPLING"
            if topology_supported else "STOP_BEFORE_E3_RECURRENT_ANATOMICAL_SEMANTICS_UNSUPPORTED"
        ),
        "scientificGate": "G2_REMAINS_FAIL_PENDING_CONTINUOUS_SPATIAL_EVENT_VALIDATION",
    }
    (root / "image_separability_report.json").write_text(
        json.dumps(result, indent=2) + "\n", encoding="utf-8")
    draw_contact_sheet(experiment_root, lookup, anchor_ids, manifest["geometry"],
                       root / "center_interlevel_contact_sheet.png")
    draw_waveforms(result, root / "center_interlevel_feature_plot.png")
    return result


def draw_contact_sheet(experiment_root: Path, lookup: dict, anchor_ids: list[str],
                       geometry: str, output: Path) -> None:
    fig, axes = plt.subplots(2, len(anchor_ids), figsize=(26, 5), squeeze=False)
    for column, anchor in enumerate(anchor_ids):
        for row, repeat in enumerate(("repeat_a", "repeat_b")):
            image = load_gray(experiment_root / lookup[(anchor, repeat)]["image"])
            axes[row, column].imshow(image, cmap="gray", vmin=0, vmax=1)
            axes[row, column].axis("off")
            if row == 0:
                axes[row, column].set_title(anchor.replace("INTERLEVEL_", "I_").replace("CENTER_", "C_"),
                                            fontsize=8, rotation=35)
    fig.suptitle(f"E4 {geometry}: level centers and interlevel midpoints (evaluator-only labels)")
    fig.tight_layout()
    fig.savefig(output, dpi=150)
    plt.close(fig)


def draw_waveforms(result: dict, output: Path) -> None:
    anchors = result["perAnchor"]
    coordinates = np.asarray([item["coordinateMm"] for item in anchors])
    shadow = np.asarray([item["skeletonShadowFraction"] for item in anchors])
    depth = np.asarray([item["medianSkeletonShadowDepth"] for item in anchors])
    types = [item["anchorType"] for item in anchors]
    fig, axes = plt.subplots(2, 1, figsize=(12, 8), sharex=True)
    axes[0].plot(coordinates, shadow, marker="o", label="Skeleton shadow beam fraction")
    axes[1].plot(coordinates, depth, marker="o", label="Median first Skeleton shadow depth")
    for axis in axes:
        for coordinate, kind in zip(coordinates, types):
            axis.axvline(coordinate, color="tab:blue" if kind == "LEVEL_CENTER" else "tab:orange",
                         alpha=0.12)
        axis.legend()
        axis.grid(alpha=0.25)
    axes[1].set_xlabel("Evaluator corridor coordinate (mm; diagnostic only)")
    fig.suptitle("Renderer provenance check; not an actor feature")
    fig.tight_layout()
    fig.savefig(output, dpi=160)
    plt.close(fig)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--experiment-root", type=Path, required=True)
    parser.add_argument("--subdir", default="e4_separability")
    args = parser.parse_args()
    result = analyze(args.experiment_root.resolve(), args.subdir)
    print(json.dumps({
        "positionSpecificCorrect": result["positionSpecificCorrect"],
        "centerInterlevelCorrect": result["centerInterlevelCorrect"],
        "centerInterlevelAccuracy": result["centerInterlevelAccuracy"],
        "permutationPValue": result["permutationControl"]["pValue"],
        "conclusion": result["anatomicalTopologyConclusion"],
        "decision": result["e4Decision"],
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
