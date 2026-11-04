"""Causal, anatomy-blind exploration decisions for thoracic landmark discovery.

This module deliberately keeps movement policy inputs separate from evaluator
anatomy.  It reuses the frozen image classifier, executed Home-relative motion,
and mechanically bounded exploration state only.
"""

from __future__ import annotations

import argparse
import copy
import json
import warnings
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import joblib

warnings.filterwarnings(
    "ignore",
    message="`sklearn.utils.parallel.delayed` should be used with",
    category=UserWarning,
)

from autonomous_t2t8_patch_likelihood import (
    FROZEN_ACCEPTANCE_SCORE,
    Session,
    apply_candidate_verifier,
    build_relative_map,
    candidate_events,
    infer_spatial_signal,
)
from g2_r2_continuous_detector import read_json, validate_actor_trace, write_json


EDGE_EVIDENCE_MARGIN_MM = 8.0
HOME_RETURN_TOLERANCE_MM = 0.25
MINIMUM_MEANINGFUL_EXCURSION_MM = 10.0
FORBIDDEN_POLICY_TERMS = (
    "lf t2",
    "lf t8",
    "referencecoordinate",
    "anatomicalcoordinate",
    "targettransform",
    "calibratedt7",
)


@dataclass(frozen=True)
class CausalCheckpoint:
    observation_count: int
    completed_excursions: int
    minimum_coordinate_mm: float
    maximum_coordinate_mm: float


def _reject_privileged_policy_input(value: Any, location: str = "root") -> None:
    if isinstance(value, dict):
        for key, item in value.items():
            normalized = str(key).replace("_", "").replace("-", "").lower()
            if any(term.replace(" ", "") in normalized for term in FORBIDDEN_POLICY_TERMS):
                raise ValueError(f"Privileged policy field at {location}.{key}")
            _reject_privileged_policy_input(item, f"{location}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            _reject_privileged_policy_input(item, f"{location}[{index}]")


def causal_round_trip_checkpoints(
    observations: list[dict[str, Any]],
    home_tolerance_mm: float = HOME_RETURN_TOLERANCE_MM,
    minimum_excursion_mm: float = MINIMUM_MEANINGFUL_EXCURSION_MM,
) -> list[CausalCheckpoint]:
    """Return checkpoints visible to an online actor after completed excursions."""
    checkpoints: list[CausalCheckpoint] = []
    excursion_peak = 0.0
    away_from_home = False
    coordinates: list[float] = []
    for index, observation in enumerate(observations):
        coordinate = float(observation["signedActorRelativeMm"])
        coordinates.append(coordinate)
        excursion_peak = max(excursion_peak, abs(coordinate))
        if abs(coordinate) > home_tolerance_mm:
            away_from_home = True
        if (
            away_from_home
            and excursion_peak >= minimum_excursion_mm
            and abs(coordinate) <= home_tolerance_mm
        ):
            checkpoints.append(CausalCheckpoint(
                observation_count=index + 1,
                completed_excursions=len(checkpoints) + 1,
                minimum_coordinate_mm=min(coordinates),
                maximum_coordinate_mm=max(coordinates),
            ))
            away_from_home = False
            excursion_peak = 0.0
    return checkpoints


def build_prefix_actor(actor: dict[str, Any], observation_count: int) -> dict[str, Any]:
    if observation_count <= 0 or observation_count > len(actor.get("observations", [])):
        raise ValueError("Invalid causal prefix length")
    prefix = copy.deepcopy(actor)
    prefix["observations"] = prefix["observations"][:observation_count]
    prefix["status"] = "CAUSAL_REPLAY_CHECKPOINT"
    prefix["causalReplayObservationCount"] = observation_count
    return prefix


def exploration_actor_after_home(actor: dict[str, Any]) -> dict[str, Any]:
    """Exclude positioning-to-Home evidence from anatomical landmark inference."""
    if not actor.get("randomStartEnabled", False):
        return copy.deepcopy(actor)
    observations = actor.get("observations", [])
    start = next(
        (index for index, item in enumerate(observations)
         if item.get("phase") == "home_arrival_baseline"),
        None,
    )
    if start is None or not actor.get("initialHomeReturnCompleted", False):
        raise ValueError("Random-start actor has no verified physical Home arrival")
    exploration = copy.deepcopy(actor)
    exploration["observations"] = exploration["observations"][start:]
    for index, observation in enumerate(exploration["observations"]):
        observation["observationIndex"] = index
    exploration["randomStartEnabled"] = False
    exploration["initialActorRelativeMm"] = 0.0
    exploration["initialReturnExcludedFromLandmarkInference"] = True
    return exploration


def assess_coverage(
    actor: dict[str, Any],
    candidates: list[dict[str, Any]],
    edge_margin_mm: float = EDGE_EVIDENCE_MARGIN_MM,
) -> dict[str, Any]:
    """Assess map sufficiency using actor observations and image events only."""
    _reject_privileged_policy_input(actor)
    observations = actor["observations"]
    minimum = min(float(item["signedActorRelativeMm"]) for item in observations)
    maximum = max(float(item["signedActorRelativeMm"]) for item in observations)
    accepted = sorted(
        (item for item in candidates if item["accepted"]),
        key=lambda item: float(item["coordinateMm"]),
    )
    exactly_seven = len(accepted) == 7
    lower_clearance = None
    upper_clearance = None
    directional_support = False
    if accepted:
        lower_clearance = float(accepted[0]["supportIntervalMm"][0]) - minimum
        upper_clearance = maximum - float(accepted[-1]["supportIntervalMm"][1])
        directional_support = all(
            int(item["forwardCount"]) >= 2 and int(item["reverseCount"]) >= 2
            for item in accepted
        )
    edge_closed = bool(
        exactly_seven
        and lower_clearance is not None
        and upper_clearance is not None
        and lower_clearance >= edge_margin_mm
        and upper_clearance >= edge_margin_mm
    )
    if exactly_seven and directional_support and edge_closed:
        action = "STOP_COVERAGE_SUFFICIENT"
        reason = "Seven supported events have independently observed margins on both edges."
    elif exactly_seven:
        action = "EXPAND_EDGE_COVERAGE"
        reason = "Seven events exist, but at least one outer event is edge-clipped or weakly supported."
    else:
        seen_negative = minimum < -MINIMUM_MEANINGFUL_EXCURSION_MM
        seen_positive = maximum > MINIMUM_MEANINGFUL_EXCURSION_MM
        action = "EXPLORE_UNVISITED_DIRECTION" if not (seen_negative and seen_positive) else "CONTINUE_BOUNDED_EXPLORATION"
        reason = f"Expected seven supported image events; observed {len(accepted)}."
    return {
        "contract": "ground_truth_free_coverage_assessment_v1",
        "actorInputsOnly": True,
        "acceptedEventCount": len(accepted),
        "exactlySeven": exactly_seven,
        "directionalSupportComplete": directional_support,
        "observedCoordinateRangeMm": [minimum, maximum],
        "edgeEvidenceMarginRequiredMm": edge_margin_mm,
        "lowerEdgeClearanceMm": lower_clearance,
        "upperEdgeClearanceMm": upper_clearance,
        "edgeEvidenceClosed": edge_closed,
        "recommendedAction": action,
        "reason": reason,
    }


def run_causal_replay(
    session_root: Path,
    actor_path: Path,
    model_path: Path,
    output_root: Path,
) -> dict[str, Any]:
    actor = read_json(actor_path)
    validate_actor_trace(session_root, actor)
    _reject_privileged_policy_input(actor)
    inference_actor = exploration_actor_after_home(actor)
    checkpoints = causal_round_trip_checkpoints(inference_actor["observations"])
    if not checkpoints:
        raise ValueError("No completed physical round trip found")
    model_bundle = joblib.load(model_path)
    model = model_bundle["model"]
    verifier_model = model_bundle.get("verifierModel")
    if hasattr(model, "n_jobs"):
        model.n_jobs = 1
    if verifier_model is not None and hasattr(verifier_model, "n_jobs"):
        verifier_model.n_jobs = 1
    results = []
    for checkpoint in checkpoints:
        checkpoint_root = output_root / f"checkpoint_{checkpoint.completed_excursions:02d}"
        checkpoint_root.mkdir(parents=True, exist_ok=True)
        prefix_path = checkpoint_root / "trajectory_actor_prefix.json"
        prefix = build_prefix_actor(inference_actor, checkpoint.observation_count)
        write_json(prefix_path, prefix)
        session = Session(
            f"CAUSAL_{checkpoint.completed_excursions:02d}", session_root, prefix_path)
        signal = infer_spatial_signal(session, model)
        candidates = candidate_events(signal, FROZEN_ACCEPTANCE_SCORE)
        verifier_signal = None
        if verifier_model is not None:
            verifier_signal = infer_spatial_signal(session, verifier_model)
            candidates = apply_candidate_verifier(candidates, verifier_signal)
        relative_map = build_relative_map(candidates)
        coverage = assess_coverage(prefix, candidates)
        write_json(checkpoint_root / "spatial_patch_likelihood.json", signal)
        if verifier_signal is not None:
            write_json(checkpoint_root / "spatial_patch_verifier_likelihood.json",
                       verifier_signal)
        write_json(checkpoint_root / "patch_event_candidates.json", {
            "contract": "autonomous_t2t8_patch_candidates_v1",
            "threshold": FROZEN_ACCEPTANCE_SCORE,
            "candidates": candidates,
        })
        write_json(checkpoint_root / "relative_spine_map.json", relative_map)
        write_json(checkpoint_root / "coverage_assessment.json", coverage)
        results.append({
            "checkpoint": checkpoint.completed_excursions,
            "observationCount": checkpoint.observation_count,
            "coordinateRangeMm": [
                checkpoint.minimum_coordinate_mm, checkpoint.maximum_coordinate_mm],
            "acceptedEventCount": coverage["acceptedEventCount"],
            "mapStatus": relative_map["overallMapStatus"],
            "predictedT7RelativeMm": relative_map["predictedT7RelativeMm"],
            "coverage": coverage,
            "runtimeSeconds": signal["runtimeSeconds"],
        })
    summary = {
        "contract": "ground_truth_free_causal_replay_v1",
        "warning": "OFFLINE REPLAY; no new physical acquisition.",
        "actorInputsOnly": True,
        "modelPath": str(model_path),
        "checkpoints": results,
    }
    write_json(output_root / "causal_replay_summary.json", summary)
    return summary


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--session-root", type=Path, required=True)
    parser.add_argument("--actor", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser


def main() -> None:
    args = build_parser().parse_args()
    run_causal_replay(args.session_root, args.actor, args.model, args.output)


if __name__ == "__main__":
    main()

