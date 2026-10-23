from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from active2d_stop import frame_stop_feature_matrix, stop_probabilities, validate_stop_checkpoint
from train_phase2_live_adapted import load_live_frames
from train_phase2_stop import classification_metrics, replay_stop


def main() -> None:
    parser = argparse.ArgumentParser(description="Evaluate STOP checkpoints on post-cutoff live audited episodes")
    parser.add_argument("--after", required=True)
    parser.add_argument("--checkpoint", type=Path, action="append", required=True)
    parser.add_argument("--threshold", type=float, action="append")
    args = parser.parse_args()
    frames = [
        frame for frame in load_live_frames("LIVE_A2D_Z")
        if frame.episode_id >= args.after and frame.contact_valid and frame.workspace_valid
    ]
    if not frames:
        raise SystemExit("No post-cutoff Phase-2 live frames")
    features = frame_stop_feature_matrix(frames)
    labels = np.asarray([frame.target_acceptable for frame in frames], dtype=np.float32)
    results = []
    for path in args.checkpoint:
        checkpoint = json.loads(path.read_text(encoding="utf-8"))
        validate_stop_checkpoint(checkpoint)
        scores = stop_probabilities(checkpoint, features)
        thresholds = args.threshold or [float(checkpoint["stopRule"]["threshold"])]
        for threshold in thresholds:
            results.append({
                "checkpoint": str(path),
                "threshold": threshold,
                "qualityMae": float(np.mean(np.abs(scores - np.asarray([f.target_score for f in frames])))),
                "classification": classification_metrics(labels, scores, threshold),
                "episodeReplay": replay_stop(frames, scores, threshold, 1),
            })
    print(json.dumps({
        "exclusiveLowerBound": args.after,
        "episodes": len({frame.episode_id for frame in frames}),
        "frames": len(frames),
        "positiveFrames": int(labels.sum()),
        "results": results,
    }, indent=2))


if __name__ == "__main__":
    main()
