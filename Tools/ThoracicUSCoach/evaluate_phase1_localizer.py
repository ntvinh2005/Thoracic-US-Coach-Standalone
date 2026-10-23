from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

import numpy as np

from active2d_contract import CONTRACT, LEVEL_ORDER, project_root, validate_checkpoint
from active2d_data import feature_matrix, load_active2d_frames, model_probabilities
from active2d_stop import stop_probabilities
from train_phase1_localizer import LEVEL_TO_INDEX, metrics, temporal_posteriors


def replay_handoff(frames, handoff_scores: np.ndarray, threshold: float, confirmations: int) -> dict[str, object]:
    episodes: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        episodes[frame.episode_id].append(index)
    successes = premature = timeouts = 0
    steps: list[int] = []
    for indices in episodes.values():
        streak = 0
        chosen = None
        for local_step, index in enumerate(indices):
            streak = streak + 1 if handoff_scores[index] >= threshold else 0
            if streak >= confirmations:
                chosen = index
                steps.append(local_step + 1)
                break
        if chosen is None:
            timeouts += 1
        elif frames[chosen].t7_acceptable:
            successes += 1
        else:
            premature += 1
    total = len(episodes)
    return {
        "episodes": total,
        "successes": successes,
        "prematureHandoffs": premature,
        "timeouts": timeouts,
        "correctAcquisitionRate": successes / max(1, total),
        "handoffPrecision": successes / max(1, successes + premature),
        "meanStepsToHandoff": float(np.mean(steps)) if steps else None,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Frozen held-out test evaluation for Phase-1 localizer")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    checkpoint = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    validate_checkpoint(checkpoint)
    dataset_run_id = str(checkpoint["datasetRunId"])
    frames = [
        frame for frame in load_active2d_frames(dataset_run_id)
        if frame.split == "test" and frame.contact_valid and frame.workspace_valid
    ]
    if not frames:
        raise SystemExit("No held-out test frames")
    missing = set(LEVEL_ORDER) - {frame.level for frame in frames}
    if missing:
        raise SystemExit(f"Absolute-level test gate failed: missing classes {sorted(missing)}")

    features = feature_matrix(frames)
    emissions = model_probabilities(checkpoint, features)
    shift = float(checkpoint["temporalFilter"]["transitionShiftPerLongitudinalStep"])
    posteriors = temporal_posteriors(frames, emissions, shift)
    handoff_scores = stop_probabilities({"architecture": checkpoint["handoffHead"]}, features)
    labels = np.asarray([LEVEL_TO_INDEX[frame.level] for frame in frames], dtype=np.int64)
    threshold = float(checkpoint["temporalFilter"]["handoffThreshold"])
    confirmations = int(checkpoint["temporalFilter"]["requiredConfirmations"])
    report = {
        "schemaVersion": 1,
        "contract": CONTRACT,
        "evaluation": "held-out-test-only-after-dev-selection",
        "checkpoint": str(args.checkpoint.resolve()),
        "datasetRunId": dataset_run_id,
        "singleFrame": metrics(labels, emissions),
        "temporal": metrics(labels, posteriors),
        "handoff": replay_handoff(frames, handoff_scores, threshold, confirmations),
    }
    output = args.output or (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/evaluation"
        / f"phase1_{args.checkpoint.stem}_heldout_test.json"
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(json.dumps(report, indent=2), encoding="utf-8")
    temporary.replace(output)
    print(json.dumps({"output": str(output), **report}, indent=2))


if __name__ == "__main__":
    main()
