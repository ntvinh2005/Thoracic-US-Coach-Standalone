"""Evaluator-only G2-R1 anatomical mesh and reference analysis.

The geometry-first candidate detector intentionally receives no T-level labels.
Labels and LF objects are opened only after geometry candidates are fixed and are
used for diagnostic comparison, never as localization-actor inputs.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
from typing import Any

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from scipy.ndimage import median_filter
from scipy.signal import find_peaks


DEFAULT_ROOT = Path(
    "Artifacts/ThoracicUSCoach/sweep_count/"
    "G2_R1_ANATOMICAL_REFERENCE_RENDERER_RECOVERY_20261101"
)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_csv_array(path: Path, columns: tuple[str, ...]) -> np.ndarray:
    values: list[list[float]] = []
    with path.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            values.append([float(row[column]) for column in columns])
    return np.asarray(values, dtype=np.float64)


def sample_triangle_surfaces(vertices: np.ndarray, triangles: np.ndarray) -> np.ndarray:
    """Return deterministic surface samples without inferring anatomy identity."""
    a = vertices[triangles[:, 0]]
    b = vertices[triangles[:, 1]]
    c = vertices[triangles[:, 2]]
    return np.concatenate(
        (a, b, c, (a + b) / 2.0, (b + c) / 2.0, (c + a) / 2.0, (a + b + c) / 3.0),
        axis=0,
    )


def posterior_profile(
    samples: np.ndarray,
    x_center_mm: float,
    z_grid_mm: np.ndarray,
    lateral_half_width_mm: float = 2.0,
    longitudinal_half_width_mm: float = 0.75,
) -> dict[str, np.ndarray]:
    x_mask = np.abs(samples[:, 0] - x_center_mm) <= lateral_half_width_mm
    band = samples[x_mask]
    surface = np.full(z_grid_mm.shape, np.nan, dtype=np.float64)
    counts = np.zeros(z_grid_mm.shape, dtype=np.int64)
    for index, coordinate in enumerate(z_grid_mm):
        mask = np.abs(band[:, 2] - coordinate) <= longitudinal_half_width_mm
        counts[index] = int(np.count_nonzero(mask))
        if counts[index]:
            surface[index] = float(np.max(band[mask, 1]))
    finite = np.isfinite(surface)
    if np.count_nonzero(finite) >= 2:
        interpolated = np.interp(z_grid_mm, z_grid_mm[finite], surface[finite])
        smoothed = median_filter(interpolated, size=5, mode="nearest")
    else:
        smoothed = surface.copy()
    return {"z": z_grid_mm, "surface_y": surface, "smoothed_y": smoothed, "counts": counts}


def detect_geometry_gaps(
    profile: dict[str, np.ndarray],
    minimum_prominence_mm: float = 1.5,
    minimum_separation_mm: float = 10.0,
) -> list[dict[str, float]]:
    """Detect posterior-surface recessions; no labels or expected K are inputs."""
    z = profile["z"]
    signal = profile["smoothed_y"]
    if len(z) < 3 or not np.all(np.isfinite(signal)):
        return []
    step = float(np.median(np.diff(z)))
    distance = max(1, int(round(minimum_separation_mm / step)))
    peaks, properties = find_peaks(-signal, prominence=minimum_prominence_mm, distance=distance)
    candidates: list[dict[str, float]] = []
    for output_index, peak_index in enumerate(peaks):
        candidates.append(
            {
                "zMm": float(z[peak_index]),
                "posteriorSurfaceYMm": float(signal[peak_index]),
                "prominenceMm": float(properties["prominences"][output_index]),
                "leftBaseZMm": float(z[int(properties["left_bases"][output_index])]),
                "rightBaseZMm": float(z[int(properties["right_bases"][output_index])]),
                "identity": "GEOMETRY_RECESS_OR_GAP_UNRESOLVED",
            }
        )
    return candidates


def match_bilateral_candidates(
    negative: list[dict[str, float]], positive: list[dict[str, float]], tolerance_mm: float = 4.0
) -> list[dict[str, Any]]:
    matches: list[dict[str, Any]] = []
    used: set[int] = set()
    for left in negative:
        available = [
            (abs(left["zMm"] - right["zMm"]), index, right)
            for index, right in enumerate(positive)
            if index not in used
        ]
        if not available:
            continue
        distance, index, right = min(available, key=lambda item: item[0])
        if distance > tolerance_mm:
            continue
        used.add(index)
        matches.append(
            {
                "centerZMm": float((left["zMm"] + right["zMm"]) / 2.0),
                "sideDisagreementMm": float(distance),
                "negativeSide": left,
                "positiveSide": right,
                "identity": "BILATERAL_GEOMETRY_RECESS_UNRESOLVED",
                "evidenceCategory": "GEOMETRY_ONLY_UNRESOLVED",
            }
        )
    return matches


def cluster_offset_candidates(
    candidates_by_offset: dict[str, list[dict[str, float]]],
    tolerance_mm: float = 4.0,
    minimum_offset_support: int = 4,
) -> list[dict[str, Any]]:
    observations = sorted(
        (candidate["zMm"], float(offset), candidate)
        for offset, candidates in candidates_by_offset.items()
        for candidate in candidates
    )
    clusters: list[list[tuple[float, float, dict[str, float]]]] = []
    for observation in observations:
        eligible = [
            cluster
            for cluster in clusters
            if abs(observation[0] - float(np.mean([item[0] for item in cluster]))) <= tolerance_mm
        ]
        if eligible:
            min(eligible, key=lambda cluster: abs(observation[0] - np.mean([item[0] for item in cluster]))).append(
                observation
            )
        else:
            clusters.append([observation])
    results: list[dict[str, Any]] = []
    for cluster in clusters:
        offsets = sorted({item[1] for item in cluster})
        if len(offsets) < minimum_offset_support:
            continue
        coordinates = [item[0] for item in cluster]
        results.append(
            {
                "centerZMm": float(np.median(coordinates)),
                "rangeZMm": [float(min(coordinates)), float(max(coordinates))],
                "offsetSupportCount": len(offsets),
                "supportingOffsetsMm": offsets,
                "maximumProminenceMm": float(max(item[2]["prominenceMm"] for item in cluster)),
                "identity": "MULTI_OFFSET_POSTERIOR_RECESS_UNRESOLVED",
                "evidenceCategory": "GEOMETRY_ONLY_UNRESOLVED",
            }
        )
    return results


def nearest_candidate(z_mm: float, candidates: list[dict[str, Any]]) -> tuple[float | None, float | None]:
    if not candidates:
        return None, None
    candidate = min(candidates, key=lambda item: abs(float(item["centerZMm"]) - z_mm))
    coordinate = float(candidate["centerZMm"])
    return coordinate, abs(coordinate - z_mm)


def build_reference_map(topology: dict[str, Any], candidates: list[dict[str, Any]]) -> dict[str, Any]:
    levels: list[dict[str, Any]] = []
    validated: list[dict[str, Any]] = []
    used_candidates: set[float] = set()
    for reference in topology["references"]:
        label_z = float(reference["labelPosition"]["z"])
        ligament_z = float(reference["ligamentBoundsCenter"]["z"])
        candidate_z, candidate_distance = nearest_candidate(ligament_z, candidates)
        accepted = (
            candidate_z is not None
            and candidate_distance is not None
            and candidate_distance <= 3.0
            and candidate_z not in used_candidates
        )
        if accepted:
            used_candidates.add(candidate_z)
        item = {
            "level": reference["level"],
            "referenceCategory": (
                "VALIDATED_ANATOMY_DERIVED_LF_POSTERIOR_RECESS_REGION"
                if accepted
                else "LABEL_DERIVED_APPROXIMATE_REFERENCE"
            ),
            "validatedBoneSurfaceCoordinate": None,
            "validatedPosteriorRecessCoordinateZMm": candidate_z if accepted else None,
            "validatedAnatomicalGapCoordinate": None,
            "labelPosition": reference["labelPosition"],
            "ligamentObject": reference["ligamentObject"],
            "ligamentBoundsCenter": reference["ligamentBoundsCenter"],
            "nearestSkeletonComponentId": reference["nearestSkeletonComponentId"],
            "labelToNearestSkeletonVertexMm": reference["labelToNearestSkeletonVertexMm"],
            "nearestBlindGeometryCandidateZMm": candidate_z,
            "ligamentToBlindGeometryCandidateMm": candidate_distance,
            "anatomicalIdentity": "LF_ASSOCIATED_POSTERIOR_BONE_RECESS" if accepted else "UNRESOLVED",
            "reason": (
                "A blind multi-offset combined-Skeleton posterior recession is co-located within 3 mm of "
                "the independently modeled, level-named ligamentum-flavum mesh. This validates an LF-associated "
                "posterior recess region, not a segmented vertebral bone center or an open empty gap."
                if accepted
                else "No one-to-one geometry-first posterior recession is independently supported at this LF region."
            ),
        }
        levels.append(item)
        if accepted:
            validated.append(item)
    return {
        "contract": "g2_r1_evaluator_only_anatomical_reference_map_v1",
        "actorUse": "PROHIBITED_EVALUATOR_ONLY",
        "validatedAnatomyDerivedLandmarks": validated,
        "labelDerivedApproximateReferences": levels,
        "geometryOnlyUnresolvedCandidates": candidates,
        "conclusion": (
            "The active single-submesh Skeleton does not independently segment vertebral bones. Geometry-first "
            "posterior recessions can nevertheless be cross-validated against the separately modeled LF objects. "
            "These are diagnostic LF-associated recess regions, not automatically vertebral centers."
        ),
    }


def write_reference_csv(path: Path, reference_map: dict[str, Any]) -> None:
    fields = [
        "level",
        "reference_category",
        "label_z_mm",
        "lf_center_z_mm",
        "nearest_component_id",
        "label_to_skeleton_mm",
        "nearest_blind_candidate_z_mm",
        "lf_to_candidate_mm",
        "validated_posterior_recess",
        "validated_bone_surface",
        "validated_anatomical_gap",
        "identity",
    ]
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for item in reference_map["labelDerivedApproximateReferences"]:
            writer.writerow(
                {
                    "level": item["level"],
                    "reference_category": item["referenceCategory"],
                    "label_z_mm": item["labelPosition"]["z"],
                    "lf_center_z_mm": item["ligamentBoundsCenter"]["z"],
                    "nearest_component_id": item["nearestSkeletonComponentId"],
                    "label_to_skeleton_mm": item["labelToNearestSkeletonVertexMm"],
                    "nearest_blind_candidate_z_mm": item["nearestBlindGeometryCandidateZMm"],
                    "lf_to_candidate_mm": item["ligamentToBlindGeometryCandidateMm"],
                    "validated_posterior_recess": (
                        "YES" if item["validatedPosteriorRecessCoordinateZMm"] is not None else "NO"
                    ),
                    "validated_bone_surface": "NO",
                    "validated_anatomical_gap": "NO",
                    "identity": item["anatomicalIdentity"],
                }
            )


def render_visualization(
    path: Path,
    samples: np.ndarray,
    topology: dict[str, Any],
    profiles: dict[str, dict[str, np.ndarray]],
    side_candidates: dict[str, list[dict[str, float]]],
    robust_candidates: list[dict[str, Any]],
) -> None:
    figure, axes = plt.subplots(3, 1, figsize=(15, 15), constrained_layout=True)
    posterior = samples[(np.abs(samples[:, 0]) <= 20.0) & (samples[:, 2] >= -95.0) & (samples[:, 2] <= 105.0)]
    if len(posterior) > 50000:
        posterior = posterior[:: int(np.ceil(len(posterior) / 50000))]
    scatter = axes[0].scatter(posterior[:, 2], posterior[:, 0], c=posterior[:, 1], s=2, cmap="viridis")
    axes[0].set_title("Combined Skeleton posterior-region surface samples (geometry only)")
    axes[0].set_xlabel("world z (mm)")
    axes[0].set_ylabel("world x (mm)")
    figure.colorbar(scatter, ax=axes[0], label="world y (mm)")
    selected = ["-9.0", "-3.0", "0.0", "6.0", "12.0"]
    for name in selected:
        profile = profiles[name]
        axes[1].plot(profile["z"], profile["smoothed_y"], label="x=" + name + " mm", alpha=0.8)
    for candidate in robust_candidates:
        axes[1].axvline(candidate["centerZMm"], color="black", alpha=0.35)
    axes[1].set_title("Blind geometry-first multi-offset posterior profiles and robust recessions")
    axes[1].set_xlabel("world z (mm)")
    axes[1].set_ylabel("posterior surface world y (mm)")
    axes[1].legend()
    axes[2].scatter(
        [candidate["centerZMm"] for candidate in robust_candidates],
        [0.0 for _ in robust_candidates], marker="x", color="black", label="blind geometry recession"
    )
    for reference in topology["references"]:
        label_z = float(reference["labelPosition"]["z"])
        ligament_z = float(reference["ligamentBoundsCenter"]["z"])
        axes[2].axvline(label_z, color="black", linestyle=":", alpha=0.5)
        axes[2].text(label_z, 0.95, reference["level"], rotation=90, va="top", ha="right",
                     transform=axes[2].get_xaxis_transform())
        axes[2].scatter(label_z, reference["labelToNearestSkeletonVertexMm"], color="tab:red")
        axes[2].scatter(ligament_z, 1.0, color="tab:green")
    axes[2].set_title("Post-freeze evaluator comparison: LF regions versus label markers")
    axes[2].set_xlabel("world z (mm)")
    axes[2].set_ylabel("reference track (0=geometry, 1=LF; red=label distance)")
    axes[2].legend(loc="upper left")
    figure.savefig(path, dpi=180)
    plt.close(figure)


def run(root: Path) -> dict[str, Any]:
    topology_path = root / "skeleton_mesh_topology.json"
    vertices_path = root / "skeleton_vertices.csv"
    triangles_path = root / "skeleton_triangles.csv"
    topology = json.loads(topology_path.read_text(encoding="utf-8"))
    vertex_table = load_csv_array(
        vertices_path, ("vertex_index", "component_id", "world_x", "world_y", "world_z")
    )
    vertices = vertex_table[:, 2:5]
    component_ids = vertex_table[:, 1].astype(np.int64)
    triangles = load_csv_array(triangles_path, ("vertex_a", "vertex_b", "vertex_c")).astype(np.int64)
    main_component = int(max(topology["components"], key=lambda item: item["vertexCount"])["componentId"])
    main_triangles = triangles[np.all(component_ids[triangles] == main_component, axis=1)]
    samples = sample_triangle_surfaces(vertices, main_triangles)
    z_grid = np.arange(-92.0, 102.0001, 0.5)
    offsets = [-15.0, -12.0, -9.0, -6.0, -3.0, 0.0, 3.0, 6.0, 9.0, 12.0, 15.0]
    profiles = {str(offset): posterior_profile(samples, offset, z_grid) for offset in offsets}
    side_candidates = {name: detect_geometry_gaps(profile) for name, profile in profiles.items()}
    robust = cluster_offset_candidates(side_candidates)
    blind = {
        "contract": "g2_r1_blind_geometry_candidates_v1",
        "labelInputsUsed": False,
        "parameters": {
            "xCentersMm": offsets,
            "zStepMm": 0.5,
            "lateralHalfWidthMm": 2.0,
            "longitudinalHalfWidthMm": 0.75,
            "minimumProminenceMm": 1.5,
            "minimumSeparationMm": 10.0,
            "bilateralToleranceMm": 4.0,
            "minimumOffsetSupport": 4,
        },
        "mainComponentId": main_component,
        "sideCandidates": side_candidates,
        "multiOffsetCandidates": robust,
        "interpretation": (
            "Candidates are combined-Skeleton posterior surface recessions. They are not called interlaminar "
            "spaces or vertebral landmarks without independent anatomical evidence."
        ),
    }
    (root / "geometry_gap_candidates.json").write_text(json.dumps(blind, indent=2), encoding="utf-8")
    reference_map = build_reference_map(topology, robust)
    (root / "bone_structure_reference_map.json").write_text(
        json.dumps(reference_map, indent=2), encoding="utf-8"
    )
    write_reference_csv(root / "reference_vs_actual_anatomy.csv", reference_map)
    unresolved = {
        "activeSkeleton": {
            "mesh": topology["skeletonMeshName"],
            "asset": topology["skeletonMeshAssetPath"],
            "subMeshCount": topology["subMeshCount"],
            "connectedComponentCount": topology["connectedComponentCount"],
            "dominantComponentVertexFraction": max(
                item["vertexCount"] for item in topology["components"]
            ) / topology["vertexCount"],
        },
        "unresolved": [
            "No named or segmented per-level vertebral bone meshes in the active production Skeleton.",
            "All T2-T8 labels resolve to the same dominant connected component.",
            "The LF T2-T8 names identify ligament meshes, not individual vertebral bone surfaces.",
            "Geometry recessions cannot be assigned a vertebral or interlaminar identity from topology alone.",
        ],
        "validatedPerLevelBoneReferences": 0,
        "validatedLfAssociatedPosteriorRecessReferences": len(
            reference_map["validatedAnatomyDerivedLandmarks"]
        ),
        "validatedTrueOpenGapReferences": 0,
    }
    (root / "unresolved_structures.json").write_text(json.dumps(unresolved, indent=2), encoding="utf-8")
    render_visualization(
        root / "anatomical_geometry_visualization.png", samples, topology, profiles, side_candidates, robust
    )
    integrity = {
        "inputs": {
            path.name: sha256_file(path) for path in (topology_path, vertices_path, triangles_path)
        },
        "outputs": {
            name: sha256_file(root / name)
            for name in (
                "geometry_gap_candidates.json",
                "bone_structure_reference_map.json",
                "reference_vs_actual_anatomy.csv",
                "unresolved_structures.json",
                "anatomical_geometry_visualization.png",
            )
        },
    }
    (root / "r1a_integrity.json").write_text(json.dumps(integrity, indent=2), encoding="utf-8")
    return {
        "mainComponentId": main_component,
        "mainTriangleCount": int(len(main_triangles)),
        "profileCount": len(profiles),
        "robustCandidateCount": len(robust),
        "validatedPerLevelBoneReferences": 0,
        "validatedLfAssociatedPosteriorRecessReferences": len(
            reference_map["validatedAnatomyDerivedLandmarks"]
        ),
        "validatedTrueOpenGapReferences": 0,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    args = parser.parse_args()
    print(json.dumps(run(args.root), indent=2))


if __name__ == "__main__":
    main()
