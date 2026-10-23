from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from active2d_contract import CONTRACT, project_root
from active2d_data import load_active2d_frames
from active2d_stop import (
    frame_stop_feature_matrix,
    frame_stop_feature_matrix_v2,
    stop_probabilities,
    validate_stop_checkpoint,
)
from train_phase2_stop import classification_metrics, replay_stop


def main() -> None:
    parser = argparse.ArgumentParser(description="Frozen held-out test evaluation for Active 2D STOP")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    checkpoint = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    validate_stop_checkpoint(checkpoint)
    frames = [
        frame for frame in load_active2d_frames(str(checkpoint["datasetRunId"]))
        if frame.split == "test" and frame.contact_valid and frame.workspace_valid
    ]
    if not frames:
        raise SystemExit("No held-out Phase 2 test frames")
    positives = sum(frame.target_acceptable for frame in frames)
    if positives < 5:
        raise SystemExit(f"Phase 2 held-out test has only {positives} positives; evaluation is not credible")
    extractor = str(checkpoint.get("architecture", {}).get("featureExtractor", "history-v1"))
    builder = frame_stop_feature_matrix_v2 if extractor == "spatial-v2" else frame_stop_feature_matrix
    features = builder(frames)
    scores = stop_probabilities(checkpoint, features)
    labels = np.asarray([frame.target_acceptable for frame in frames], dtype=np.float32)
    threshold = float(checkpoint["stopRule"]["threshold"])
    confirmations = int(checkpoint["stopRule"]["requiredConfirmations"])
    report = {
        "schemaVersion": 1, "contract": CONTRACT,
        "evaluation": "held-out-test-only-after-dev-selection",
        "checkpoint": str(args.checkpoint.resolve()),
        "datasetRunId": checkpoint["datasetRunId"],
        "classification": classification_metrics(labels, scores, threshold),
        "episodeReplay": replay_stop(frames, scores, threshold, confirmations),
    }
    output = args.output or (
        project_root() / "Artifacts/ThoracicUSCoach/active2d/evaluation"
        / f"phase2_{args.checkpoint.stem}_heldout_test.json"
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(json.dumps(report, indent=2), encoding="utf-8")
    temporary.replace(output)
    print(json.dumps({"output": str(output), **report}, indent=2))


if __name__ == "__main__":
    main()
