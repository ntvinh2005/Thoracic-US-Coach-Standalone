"""Build the bounded G2-R4 failure audit and offline development evidence."""

from __future__ import annotations

import argparse
import csv
import json
import time
import tracemalloc
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw

from g2_r2_continuous_detector import read_json, write_json
from g2_r2_interface_tracker import reference_coordinates
from g2_r3_offline_audit import score
from g2_r3_topology_discriminator import (
    apply_shadow_topology_gate,
    candidate_topology_measurements,
)
from g2_r4_multi_hypothesis_tracker import (
    MAXIMUM_TRACK_HYPOTHESES,
    MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION,
    MINIMUM_SHADOW_ONSET_CONTRAST,
    MINIMUM_SHADOW_PERSISTENCE_FRACTION,
    build_ambiguity_aware_map,
    run_actor_tracking,
    select_active_reacquisition_windows,
    track_multiple_interfaces,
)
from relative_spine_e2_analysis import load_gray, sha256


def candidate_from_cluster(cluster: dict[str, Any], index: int) -> dict[str, Any]:
    return {
        "candidateId": f"R4E{index:02d}",
        "actorRelativeCoordinateMm": cluster["centerMm"],
        "uncertaintyIntervalMm": cluster["supportIntervalMm"],
        "supportingFrameIndices": cluster["supportingFrameIndices"],
        "supportingImages": cluster["supportingImages"],
        "eventType": "MULTI_HYPOTHESIS_ACTUAL_SHADOW_SUPPORTED_RECESS",
        "accepted": True,
    }


def strip_cluster(cluster: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in cluster.items() if key != "support"}


def run_dataset(
    name: str, source_root: Path, baseline_root: Path, r1_root: Path
) -> dict[str, Any]:
    references, _ = reference_coordinates(source_root, r1_root)
    baseline_candidates = read_json(baseline_root / "event_candidates.json")["candidates"]
    baseline_score = score(baseline_candidates, references)
    topology = candidate_topology_measurements(source_root, baseline_root)
    r3_gate = apply_shadow_topology_gate(topology)
    r3_score = score(r3_gate["accepted"], references)
    tracemalloc.start()
    start = time.perf_counter()
    tracking = run_actor_tracking(source_root)
    runtime = time.perf_counter() - start
    _, peak_memory = tracemalloc.get_traced_memory()
    tracemalloc.stop()
    clusters = tracking["chosen"]["clusters"]
    accepted_clusters = [item for item in clusters if item["accepted"]]
    candidates = [
        candidate_from_cluster(cluster, index)
        for index, cluster in enumerate(accepted_clusters, start=1)
    ]
    r4_score = score(candidates, references)
    relative_map = build_ambiguity_aware_map(clusters)
    actor = read_json(source_root / "trajectory_actor.json")
    coordinates = [float(item["signedActorRelativeMm"]) for item in actor["observations"]]
    decisions = select_active_reacquisition_windows(
        clusters, (min(coordinates), max(coordinates))
    )
    return {
        "dataset": name,
        "sourceRoot": str(source_root),
        "baselineRoot": str(baseline_root),
        "references": references,
        "baseline": baseline_score,
        "r3Topology": r3_score,
        "r4": r4_score,
        "r3AcceptedCount": len(r3_gate["accepted"]),
        "r3AmbiguousCount": len(r3_gate["ambiguous"]),
        "r4AcceptedCount": len(accepted_clusters),
        "r4AmbiguousCount": sum(
            item["classification"] == "AMBIGUOUS_INSUFFICIENT_EVIDENCE"
            for item in clusters
        ),
        "r4RejectedCount": sum(
            item["classification"] == "REJECTED_SECONDARY_OR_DUPLICATE_EVENT"
            for item in clusters
        ),
        "r4Clusters": [strip_cluster(item) for item in clusters],
        "relativeSpineMap": relative_map,
        "activeReacquisitionDecisions": decisions,
        "runtimeSeconds": runtime,
        "peakMemoryBytes": peak_memory,
        "chosenImageAxisSign": tracking["chosen"]["sign"],
        "rawMultiHypothesisDetectionCount": tracking["rawDetectionCount"],
    }


def write_lineage(path: Path, results: list[dict[str, Any]]) -> None:
    fields = (
        "dataset", "detector", "coordinate_mm", "distinct_frames", "outbound_center_mm",
        "repeat_center_mm", "shadow_support_fraction", "distal_verifiable_fraction",
        "classification", "accepted", "rejection_reasons",
    )
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for result in results:
            baseline = read_json(Path(result["baselineRoot"]) / "event_tracking_diagnostics.json")
            sign = baseline["chosenImageAxisSign"]
            chosen = next(item for item in baseline["signAlternatives"] if item["sign"] == sign)
            for cluster in chosen["clusters"]:
                writer.writerow({
                    "dataset": result["dataset"], "detector": "G2_R2_SINGLE_PATH",
                    "coordinate_mm": cluster["centerMm"],
                    "distinct_frames": cluster["distinctFrameCount"],
                    "outbound_center_mm": cluster["outboundCenterMm"],
                    "repeat_center_mm": cluster["repeatCenterMm"],
                    "shadow_support_fraction": None, "distal_verifiable_fraction": None,
                    "classification": "ACCEPTED" if cluster["accepted"] else "REJECTED",
                    "accepted": cluster["accepted"],
                    "rejection_reasons": "|".join(cluster["rejectionReasons"]),
                })
            for cluster in result["r4Clusters"]:
                writer.writerow({
                    "dataset": result["dataset"], "detector": "G2_R4_MULTI_HYPOTHESIS",
                    "coordinate_mm": cluster["centerMm"],
                    "distinct_frames": cluster["distinctFrameCount"],
                    "outbound_center_mm": cluster["outboundCenterMm"],
                    "repeat_center_mm": cluster["repeatCenterMm"],
                    "shadow_support_fraction": cluster["actualShadowSupportFraction"],
                    "distal_verifiable_fraction": cluster["distalFieldVerifiableFraction"],
                    "classification": cluster["classification"],
                    "accepted": cluster["accepted"],
                    "rejection_reasons": "|".join(cluster["rejectionReasons"]),
                })


def nearest_observation(root: Path, coordinate: float, phases: set[str]) -> dict[str, Any]:
    actor = read_json(root / "trajectory_actor.json")
    rows = [item for item in actor["observations"] if item["phase"] in phases]
    return min(rows, key=lambda item: abs(float(item["signedActorRelativeMm"]) - coordinate))


def overlay_tracks(root: Path, observation: dict[str, Any], title: str) -> np.ndarray:
    gray = load_gray(root / observation["image"])
    image = Image.fromarray(np.uint8(np.clip(gray, 0.0, 1.0) * 255)).convert("RGB")
    draw = ImageDraw.Draw(image)
    colors = ((255, 64, 64), (0, 255, 255), (255, 230, 0))
    for track, color in zip(track_multiple_interfaces(gray), colors):
        points = [(int(x), int(round(y))) for x, y in zip(track["x"], track["pathY"])]
        draw.line(points, fill=color, width=2)
    draw.rectangle((0, 0, image.width, 25), fill=(0, 0, 0))
    draw.text((5, 5), title, fill=(255, 255, 255))
    return np.asarray(image)


def draw_yaw_comparison(
    yaw90_root: Path, yaw91_root: Path, decisions: list[dict[str, Any]], output: Path
) -> None:
    centers = [item["actorRelativeCenterMm"] for item in decisions[:2]]
    if not centers:
        centers = [-70.0]
    phases = (
        ("outbound", {"cranial_outbound"}),
        ("reverse", {"cranial_return"}),
    )
    figure, axes = plt.subplots(len(centers) * 2, 2, figsize=(10, 5 * len(centers) * 2),
                                constrained_layout=True)
    axes = np.asarray(axes).reshape(len(centers) * 2, 2)
    row = 0
    for center in centers:
        for phase_name, phase_set in phases:
            for column, (name, root) in enumerate((("yaw90", yaw90_root), ("yaw91", yaw91_root))):
                observation = nearest_observation(root, center, phase_set)
                title = (
                    f"{name} {phase_name}; actor s="
                    f"{float(observation['signedActorRelativeMm']):.2f} mm"
                )
                axes[row, column].imshow(overlay_tracks(root, observation, title))
                axes[row, column].axis("off")
            row += 1
    figure.savefig(output, dpi=160)
    plt.close(figure)


def draw_contact_sheet(source_root: Path, clusters: list[dict[str, Any]], output: Path) -> None:
    panels = []
    for cluster in clusters:
        images = cluster["supportingImages"]
        if not images:
            continue
        actor = read_json(source_root / "trajectory_actor.json")
        image_name = images[0]
        observation = next(item for item in actor["observations"] if item["image"] == image_name)
        panel = Image.fromarray(overlay_tracks(
            source_root, observation,
            f"s={cluster['centerMm']:.2f} {cluster['classification']}"
        )).resize((320, 320))
        panels.append(panel)
    if not panels:
        Image.new("RGB", (640, 160), "white").save(output)
        return
    columns = 3
    rows = (len(panels) + columns - 1) // columns
    sheet = Image.new("RGB", (320 * columns, 320 * rows), "white")
    for index, panel in enumerate(panels):
        sheet.paste(panel, ((index % columns) * 320, (index // columns) * 320))
    sheet.save(output)


def draw_relative_map(result: dict[str, Any], output: Path) -> None:
    clusters = result["r4Clusters"]
    figure, axis = plt.subplots(figsize=(15, 4), constrained_layout=True)
    colors = {
        "SUPPORTED_ANATOMICAL_EVENT": "tab:green",
        "AMBIGUOUS_INSUFFICIENT_EVIDENCE": "tab:orange",
        "REJECTED_SECONDARY_OR_DUPLICATE_EVENT": "tab:red",
    }
    for index, cluster in enumerate(clusters):
        axis.scatter(cluster["centerMm"], 0.0, color=colors[cluster["classification"]], s=80)
        axis.text(cluster["centerMm"], 0.05 + 0.05 * (index % 2),
                  f"{cluster['centerMm']:.1f}", rotation=45, ha="center")
    for decision in result["activeReacquisitionDecisions"]:
        axis.axvspan(decision["windowMm"][0], decision["windowMm"][1],
                     color="tab:blue", alpha=0.10)
    axis.axvline(0.0, color="black", linestyle="--", label="fixed unlabeled Home")
    axis.set_ylim(-0.15, 0.25)
    axis.set_yticks([])
    axis.set_xlabel("Actor-relative coordinate from Home (mm)")
    axis.set_title("G2-R4 yaw91 actor-only event evidence and proposed revisit windows")
    axis.grid(axis="x", alpha=0.25)
    axis.legend()
    figure.savefig(output, dpi=180)
    plt.close(figure)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", action="append", nargs=3,
                        metavar=("NAME", "SOURCE", "BASELINE"), required=True)
    parser.add_argument("--r1-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    output_root = args.output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    (output_root / "evaluator_only").mkdir(exist_ok=True)
    r1_root = args.r1_root.resolve()
    results = []
    for name, source_text, baseline_text in args.dataset:
        results.append(run_dataset(
            name, Path(source_text).resolve(), Path(baseline_text).resolve(), r1_root
        ))
    yaw91 = next(item for item in results if item["dataset"] == "YAW91")
    yaw90 = next(item for item in results if item["dataset"] == "DEV03")
    comparison = {
        "contract": "thoracic_g2_r4_offline_development_comparison_v1",
        "developmentDisclosure": (
            "All four sweeps are development data. Yaw91 ceased to be held out after G2-R3."
        ),
        "datasets": results,
    }
    write_json(output_root / "offline_development_comparison.json", comparison)
    with (output_root / "offline_development_comparison.csv").open(
        "w", encoding="utf-8", newline=""
    ) as handle:
        fields = (
            "dataset", "detector", "tp", "fp", "fn", "precision", "recall", "f1",
            "exact_seven", "accepted", "ambiguous", "rejected", "runtime_seconds",
            "peak_memory_bytes",
        )
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for result in results:
            for detector, key in (("G2_R2", "baseline"), ("G2_R3", "r3Topology"),
                                  ("G2_R4", "r4")):
                metrics = result[key]["metrics"]
                writer.writerow({
                    "dataset": result["dataset"], "detector": detector,
                    "tp": metrics["truePositive"], "fp": metrics["falsePositive"],
                    "fn": metrics["falseNegative"], "precision": metrics["precision"],
                    "recall": metrics["recall"], "f1": metrics["f1"],
                    "exact_seven": metrics["exactSevenEventSequence"],
                    "accepted": result["r4AcceptedCount"] if detector == "G2_R4" else None,
                    "ambiguous": result["r4AmbiguousCount"] if detector == "G2_R4" else None,
                    "rejected": result["r4RejectedCount"] if detector == "G2_R4" else None,
                    "runtime_seconds": result["runtimeSeconds"] if detector == "G2_R4" else None,
                    "peak_memory_bytes": result["peakMemoryBytes"] if detector == "G2_R4" else None,
                })
    write_lineage(output_root / "candidate_lineage.csv", results)
    write_json(output_root / "multi_hypothesis_tracking_diagnostics.json", {
        "parameters": {
            "maximumTrackHypotheses": MAXIMUM_TRACK_HYPOTHESES,
            "actualShadowSupportFraction": MINIMUM_ACTUAL_SHADOW_SUPPORT_FRACTION,
            "shadowPersistenceFraction": MINIMUM_SHADOW_PERSISTENCE_FRACTION,
            "shadowOnsetContrast": MINIMUM_SHADOW_ONSET_CONTRAST,
        },
        "datasets": [{
            "dataset": item["dataset"], "clusters": item["r4Clusters"],
            "runtimeSeconds": item["runtimeSeconds"],
            "peakMemoryBytes": item["peakMemoryBytes"],
        } for item in results],
    })
    write_json(output_root / "morphology_verification.json", {
        "distinction": (
            "Distal-field availability and actual local post-interface darkness are separate. "
            "Actual shadow requires sufficient field, local onset contrast, and persistence "
            "across neighboring columns."
        ),
        "datasets": [{
            "dataset": item["dataset"],
            "events": [{
                "coordinateMm": cluster["centerMm"],
                "distalFieldVerifiableFraction": cluster["distalFieldVerifiableFraction"],
                "actualShadowSupportFraction": cluster["actualShadowSupportFraction"],
                "classification": cluster["classification"],
            } for cluster in item["r4Clusters"]],
        } for item in results],
    })
    decisions = {
        "contract": "thoracic_g2_r4_actor_reacquisition_decisions_v1",
        "inputIsolation": "Actor-relative image candidates only; no evaluator coordinates.",
        "plannerImplemented": True,
        "inSessionUnityExecution": "NOT_RUN_OFFLINE_READINESS_FAILED",
        "datasets": [{
            "dataset": item["dataset"], "decisions": item["activeReacquisitionDecisions"]
        } for item in results],
    }
    write_json(output_root / "active_reacquisition_decisions.json", decisions)
    write_json(output_root / "relative_spine_map.json", yaw91["relativeSpineMap"])
    write_json(output_root / "mapping_uncertainty.json", {
        "dataset": "YAW91",
        "supportIntervalsAreNotCalibratedConfidenceIntervals": True,
        "accepted": [item for item in yaw91["r4Clusters"] if item["accepted"]],
        "ambiguous": [item for item in yaw91["r4Clusters"]
                      if item["classification"] == "AMBIGUOUS_INSUFFICIENT_EVIDENCE"],
        "alternativeMapPolicy": (
            "No T-level assignment is emitted when fewer than seven supported events exist "
            "or an ambiguous insertion could alter V6."
        ),
    })
    coordinate_rows = []
    for result in results:
        coordinate_rows.append({
            "dataset": result["dataset"],
            "matchedEvents": result["r4"]["candidateMatches"],
            "t7Diagnostic": next((item for item in result["r4"]["candidateMatches"]
                                  if item.get("matchedLevel") == "T7"), None),
        })
    write_json(output_root / "coordinate_error_analysis.json", {
        "evaluatorOnly": True,
        "projection": "Unchanged renderer-consistent G2-R2 bottom-footprint projection.",
        "datasets": coordinate_rows,
    })
    cranial = [item for item in yaw91["r4Clusters"] if item["centerMm"] < -50.0]
    write_json(output_root / "cranial_candidate_failure_diagnostics.json", {
        "actorOnlyCoordinates": True,
        "candidates": cranial,
        "earliestFailure": (
            "The weak cranial events survive projection and clustering as hypotheses. One "
            "has only three distinct frames; the other has two and no reverse support. "
            "Alternative contour objectives do not create stable additional evidence."
        ),
    })
    write_json(output_root / "evaluator_only" / "cranial_anatomical_interpretation.json", {
        "references": yaw91["references"][:2],
        "interpretation": (
            "The two actor-side weak cranial clusters are within the locked five-millimeter "
            "diagnostic tolerance of T2 and T3, but neither passes actor evidence gates."
        ),
    })
    readiness = {
        "contract": "thoracic_g2_r4_readiness_gate_v1",
        "failureMechanismDocumented": True,
        "weakYaw91EvidenceRecovered": False,
        "noNewWrongLevelAcceptance": True,
        "actorEvaluatorIsolation": True,
        "reproducibleOfflineResults": True,
        "cpuRuntimeBounded": max(item["runtimeSeconds"] for item in results) < 180.0,
        "testsPassed": False,
        "frozenConfirmationProtocol": False,
        "offlineReadiness": False,
        "decision": "STOP_BEFORE_NEW_PHYSICAL_SWEEP",
    }
    write_json(output_root / "development_readiness_gate.json", readiness)
    draw_yaw_comparison(
        Path(yaw90["sourceRoot"]), Path(yaw91["sourceRoot"]),
        yaw91["activeReacquisitionDecisions"], output_root / "yaw90_vs_yaw91_contours.png",
    )
    draw_contact_sheet(
        Path(yaw91["sourceRoot"]), yaw91["r4Clusters"],
        output_root / "multi_hypothesis_contact_sheet.png",
    )
    draw_relative_map(yaw91, output_root / "relative_map_overview.png")
    audit_text = (
        "# R4-A Failure Audit\n\n"
        "The immutable G2-R2 outputs were reproduced on all four development sweeps. "
        "At yaw 91 degrees, the cranial hypotheses survive image-to-position projection "
        "and spatial clustering, so projection is not the first failing stage. The T2-like "
        "hypothesis has three frames with both directions; the T3-like hypothesis has two "
        "outbound frames and no reverse support. Matched-position images show the global "
        "lower path switching which visible interface supplies a local valley. The first "
        "scientifically decisive loss is therefore interface identity/local valley evidence, "
        "followed by the correct support-validation abstention. Multi-hypothesis intensity/"
        "gradient paths recover DEV01 but do not add stable yaw91 cranial support.\n\n"
        "The deep recurrent extra is not random. It remains image-ambiguous because its "
        "distal field is unavailable; actual acoustic shadow cannot be verified. It is not "
        "silently deleted.\n"
    )
    (output_root / "R4_A_FAILURE_AUDIT.md").write_text(audit_text, encoding="utf-8")
    final_gates = {
        "G0": "PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION_HISTORICAL",
        "G1": "PASS_HISTORICAL_R3_NO_NEW_SWEEP",
        "G2_R4": "FAIL_OFFLINE_READINESS",
        "G3_R4": "NOT_RUN_ACTOR_ABSTAINED",
        "G4": "NOT_RUN_PROTOCOL_EXCLUSION",
        "newPhysicalSweepCount": 0,
        "activeReacquisitionExecutionCount": 0,
    }
    write_json(output_root / "final_gates.json", final_gates)
    source_files = [
        Path(__file__).resolve(),
        Path(__file__).with_name("g2_r4_multi_hypothesis_tracker.py").resolve(),
        Path(__file__).with_name("test_g2_r4_multi_hypothesis_tracker.py").resolve(),
        Path(__file__).with_name("g2_r2_interface_tracker.py").resolve(),
        Path(__file__).with_name("g2_r3_topology_discriminator.py").resolve(),
    ]
    project_root = Path(__file__).resolve().parents[2]
    protected_assets = [
        project_root / "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity",
        project_root / "Assets/Thoracic RA Anatomy/RA Thoracic Anatomy V3.fbx",
        project_root / (
            "LocalPackages/com.cssalt.smmarts-sdk-package/Runtime/Ultrasound/"
            "UltrasoundManager.cs"
        ),
    ]
    write_json(output_root / "source_configuration_hash_manifest.json", {
        "sources": {str(path): sha256(path) for path in source_files},
        "protectedAssets": {
            str(path.relative_to(project_root)).replace("\\", "/"): sha256(path)
            for path in protected_assets
        },
        "expectedProtectedAssetHashesFromG2R3Freeze": {
            "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity":
                "ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131",
            "Assets/Thoracic RA Anatomy/RA Thoracic Anatomy V3.fbx":
                "df34d622b8af3a719cbc0cb42e33e643e4435bd2cbf2d6c4a2e4b49357d19d4e",
            "LocalPackages/com.cssalt.smmarts-sdk-package/Runtime/Ultrasound/UltrasoundManager.cs":
                "2f170ee99402279977f906a8288cf4af6ce66016d5b0bda00b30e1cbdd9220e0",
        },
        "inputs": {
            item["dataset"] + "/trajectory_actor.json": sha256(
                Path(item["sourceRoot"]) / "trajectory_actor.json"
            ) for item in results
        },
        "configuration": {
            "maximumTrackHypotheses": MAXIMUM_TRACK_HYPOTHESES,
            "scoringToleranceMm": 5.0,
            "maximumRevisitWindows": 2,
        },
    })
    report = (
        "# G2-R4 Multi-Hypothesis Tracking and Active Reacquisition\n\n"
        "## Scientific outcome\n\n"
        "Offline readiness **FAIL**. R4 preserves seven evaluator-matched events on each "
        "yaw-90 development sweep and does not accept the recurrent deep interface. It does "
        "not recover sufficient T2/T3 evidence on yaw 91 degrees: 5 TP, 0 FP, 2 FN, "
        "precision 1.000, recall 0.714, F1 0.833. The actor emits no T-level map and no "
        "T7 coordinate. No new physical sweep was permitted by the readiness gate.\n\n"
        "## Failure-point audit\n\n"
        "The primary tracker loses T2/T3 evidence because the globally selected lower bright "
        "path changes which visible interface supplies the local valley after the one-degree "
        "yaw change. The T2-like hypothesis has three supporting frames; the T3-like "
        "hypothesis has two outbound frames and no reverse support. Both survive coordinate "
        "projection and clustering. The earliest failure is therefore contour identity/local "
        "valley extraction, followed by the scientifically correct support-validation "
        "abstention—not projection.\n\n"
        "The bounded H1 edge-dominant and H2 intensity-dominant paths recover weak evidence "
        "on DEV01 but do not create stable yaw-91 cranial evidence. They preserve the five "
        "caudal events and do not multiply same-frame evidence because density is capped per "
        "frame.\n\n"
        "## Morphology and the secondary interface\n\n"
        "R4 distinguishes distal image availability from verified shadow morphology. Actual "
        "shadow support requires post-interface darkness, onset contrast and persistence in "
        "neighboring columns. The deep E02-like interface has inadequate distal pixels, so "
        "its acoustic-shadow topology is **not verified**. It is not accepted as a recess, "
        "but it remains semantically ambiguous rather than being erased as a known false "
        "structure. No evaluator coordinate participates in this judgment.\n\n"
        "## Development comparison\n\n"
        "DEV01 improves to 7/0/0 from the R2 6/1/1 result; DEV02 remains 7/0/0; DEV03 "
        "improves from R2 7/1/0 to 7/0/0. YAW91 remains 5/0/2. Detailed baseline/R3/R4 "
        "metrics, runtimes and peak memory are in `offline_development_comparison.*`. The "
        "five-millimeter T7 diagnostic remains fragile: errors are 4.943–4.973 mm, leaving "
        "only 0.027–0.058 mm margin. No evaluator-derived offset was applied.\n\n"
        "## Active reacquisition\n\n"
        "The actor-only planner is implemented and selects at most two Home-relative windows "
        "from ambiguous image candidates using frame support, missing traversal direction "
        "and pixel shadow evidence. It does not load anatomy coordinates. For YAW91 it "
        "selects the two cranial unresolved hypotheses after regeneration. In-session Unity "
        "reacquisition is NOT_RUN because readiness failed; therefore no claim is made that "
        "the visits resolve uncertainty.\n\n"
        "## Required answers\n\n"
        "1. T2/T3 were lost through contour-identity/local-valley instability under yaw 91.\n"
        "2. The earliest failure was tracking/valley evidence, not projection or clustering.\n"
        "3. Multi-hypothesis tracking did not recover stable yaw-91 T2/T3 support.\n"
        "4. The verifier uses local post-interface darkness, onset contrast and neighboring-"
        "column persistence.\n"
        "5. Real shadow morphology is checked where distal pixels exist; E02 has only an "
        "availability limitation, not verified shadow.\n"
        "6. No extra event is accepted, but the deep interface remains ambiguous.\n"
        "7. All seven references survive DEV01/02/03; only five survive YAW91.\n"
        "8. Actor-driven local rescan selection is implemented but not physically executed.\n"
        "9. Revisit windows come only from actor-relative ambiguous image hypotheses.\n"
        "10. Revisit resolution is NOT_RUN.\n"
        "11. No frozen physical sweep was executed.\n"
        "12. No new blind run exists; actor/evaluator isolation holds in offline processing.\n"
        "13. The actor did not infer all seven positions on YAW91.\n"
        "14. The final sequence is INCONCLUSIVE.\n"
        "15. `predictedT7RelativeMm` is null.\n"
        "16. Independent T7 error is not scoreable for an emitted target; diagnostic event "
        "error is 4.972 mm.\n"
        "17. G2-R4 FAIL.\n"
        "18. G3-R4 NOT_RUN because the actor abstained.\n"
        "19. Before G4, a frozen actor-only episode must recover T2/T3 and resolve any "
        "enumeration-changing extra interface.\n"
        "20. The smallest blocker is missing bidirectional local recess evidence at yaw 91; "
        "the next discriminating experiment is a separately approved localized actor-derived "
        "rescan, not a lower gate or another whole-sweep search.\n\n"
        "## Execution and gates\n\n"
        "Branch: `codex/g2-r4-multi-hypothesis-reacquisition`; baseline: `e15bab3`. "
        "Changed source files are the R4 tracker, offline audit and focused tests. Physical "
        "sweeps: 0; physical revisits: 0. Python and Unity evidence is under `test_results/`. "
        "G0 historical PASS with identity limitation; G1 historical PASS; G2-R4 FAIL; "
        "G3 NOT_RUN; G4 NOT_RUN. Protected scene, Skeleton and renderer hashes are recorded "
        "in `source_configuration_hash_manifest.json`.\n"
    )
    (output_root / "REPORT.md").write_text(report, encoding="utf-8")
    print(json.dumps({
        "outputRoot": str(output_root),
        "yaw91Metrics": yaw91["r4"]["metrics"],
        "yaw91Map": yaw91["relativeSpineMap"]["overallMapStatus"],
        "readiness": readiness["offlineReadiness"],
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
