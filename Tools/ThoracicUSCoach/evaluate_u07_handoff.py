from __future__ import annotations

import argparse
import hashlib
import json
import time
from pathlib import Path

import numpy as np

from active2d_contract import project_root
from active2d_data import convolutional_features, load_active2d_frames
from active2d_handoff import ImageDerivedHandoffState, handoff_ready, phase_two_prefix, update_handoff_state
from active2d_stop import stop_probabilities


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def atomic_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    temporary.replace(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="U07 dev comparison of scalar and explicit image-derived handoff")
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    root = project_root()
    config_path = args.config.resolve()
    config = json.loads(config_path.read_text(encoding="utf-8"))
    old_path = root / config["oldPhase1Checkpoint"]
    landmark_path = root / config["landmarkCheckpoint"]
    old = json.loads(old_path.read_text(encoding="utf-8"))
    landmark = json.loads(landmark_path.read_text(encoding="utf-8"))
    frames = [frame for frame in load_active2d_frames(config["datasetRunId"], root)
              if frame.split == "dev" and frame.contact_valid and frame.workspace_valid]
    episodes = sorted({frame.episode_id for frame in frames})
    if args.smoke:
        episodes = episodes[:2]
    comparisons = []
    for number, episode in enumerate(episodes, 1):
        selected = [frame for frame in frames if frame.episode_id == episode]
        state = ImageDerivedHandoffState()
        previous = None
        old_choice = None
        u07_choice = None
        last_direction = "Caudal"
        for local_step, frame in enumerate(selected):
            features = convolutional_features(frame.image)
            old_score = float(stop_probabilities({"architecture": old["handoffHead"]}, features)[0])
            if old_choice is None and old_score >= float(old["temporalFilter"]["handoffThreshold"]):
                old_choice = (local_step, frame, old_score)
            update_handoff_state(landmark, features, previous, frame.issued_action, local_step, state)
            if frame.issued_action.title() in {"Cranial", "Caudal"}:
                last_direction = frame.issued_action.title()
            if u07_choice is None and handoff_ready(state, min_confidence=config["gate"]["minConfidence"],
                                                    max_uncertainty=config["gate"]["maxUncertainty"],
                                                    min_landmark_events=config["gate"]["minLandmarkEvents"]):
                u07_choice = (local_step, frame, state.public_summary(), list(phase_two_prefix(state, last_direction)))
            previous = features
        comparisons.append({
            "episodeId": episode,
            "oldScalar": None if old_choice is None else {"step": old_choice[0], "score": old_choice[2],
                                                            "t7Acceptable": old_choice[1].t7_acceptable},
            "u07Explicit": None if u07_choice is None else {"step": u07_choice[0], "t7Acceptable": u07_choice[1].t7_acceptable,
                                                             "state": u07_choice[2], "phaseTwoPrefix": u07_choice[3]},
            "finalStateWhenAbstained": state.public_summary() if u07_choice is None else None,
        })
        print("::localjobs_progress " + json.dumps({"phase": "dev-replay", "current": number,
                                                     "total": len(episodes), "message": episode}), flush=True)
    def aggregate(key: str) -> dict:
        choices = [item[key] for item in comparisons if item[key] is not None]
        correct = sum(bool(item["t7Acceptable"]) for item in choices)
        return {"episodes": len(comparisons), "handoffs": len(choices), "abstentions": len(comparisons) - len(choices),
                "correct": correct, "premature": len(choices) - correct,
                "precision": correct / max(1, len(choices)), "acquisitionRate": correct / max(1, len(comparisons)),
                "meanStep": float(np.mean([item["step"] for item in choices])) if choices else None}
    report = {"schemaVersion": 1, "taskId": "U07", "experimentId": config["experimentId"],
              "datasetRunId": config["datasetRunId"], "split": "dev", "selectionUse": "development-only",
              "configSha256": sha256(config_path), "checkpointSha256": {"old": sha256(old_path), "landmark": sha256(landmark_path)},
              "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
              "forbiddenInputs": ["pose", "targetCoordinates", "score", "markerIdentity", "oracleDirection"],
              "gate": config["gate"], "oldScalar": aggregate("oldScalar"), "u07Explicit": aggregate("u07Explicit"),
              "comparisons": comparisons, "limitations": ["Development trajectories only; no deployment claim.",
              "Issued-action odometry is policy-owned and does not prove achieved physical displacement."]}
    output = root / config["output"]
    if args.smoke:
        output = output.with_suffix(".smoke.json")
    atomic_json(output, report)
    print(json.dumps({"output": str(output), "oldScalar": report["oldScalar"], "u07Explicit": report["u07Explicit"]}, indent=2))


if __name__ == "__main__":
    main()
