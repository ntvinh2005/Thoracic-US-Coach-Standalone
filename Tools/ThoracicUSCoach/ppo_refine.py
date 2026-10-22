from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, DEFAULT_EXPERIMENT_ID, feature_matrix, load_frames, project_root, save_json, stable_softmax


def main() -> None:
    parser = argparse.ArgumentParser(description="Legacy clipped replay-policy objective; this is explicitly not on-policy PPO")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--bc", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/bc_pixel_policy.json")
    parser.add_argument("--epochs", type=int, default=40)
    parser.add_argument("--clip", type=float, default=0.15)
    parser.add_argument("--lr", type=float, default=0.003)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/ppo_pixel_policy.json")
    args = parser.parse_args()

    frames = [frame for frame in load_frames(experiment_id=args.experiment_id) if frame.split == "train" and frame.teacher_action_defined]
    if not frames:
        raise SystemExit("No training sweeps")
    bc = json.loads(args.bc.read_text(encoding="utf-8"))
    weights = np.asarray(bc["weights"], dtype=np.float32)
    bias = np.asarray(bc["bias"], dtype=np.float32)
    x = feature_matrix(frames)
    teacher = np.asarray([ACTION_TO_INDEX[frame.teacher_action] for frame in frames], dtype=np.int64)
    acceptable = np.asarray([frame.target_acceptable for frame in frames], dtype=np.float32)
    old_probabilities = stable_softmax(x @ weights + bias)

    # A bounded surrogate: reward stop in the acceptable set, otherwise reward the teacher direction.
    rewards = np.full((len(frames), 3), -0.05, dtype=np.float32)
    rewards[np.arange(len(frames)), teacher] = 0.2
    rewards[:, ACTION_TO_INDEX[0]] += np.where(acceptable > 0, 1.0, -0.4)
    baseline = (old_probabilities * rewards).sum(axis=1, keepdims=True)
    advantages = rewards - baseline
    for _ in range(args.epochs):
        probabilities = stable_softmax(x @ weights + bias)
        ratio = probabilities / np.clip(old_probabilities, 1e-6, 1.0)
        unclipped = ratio * advantages
        clipped = np.clip(ratio, 1.0 - args.clip, 1.0 + args.clip) * advantages
        active = unclipped <= clipped
        policy_signal = np.where(active, advantages / np.clip(old_probabilities, 1e-6, 1.0), 0.0)
        expected = (probabilities * policy_signal).sum(axis=1, keepdims=True)
        gradient_logits = -probabilities * (policy_signal - expected) / len(frames)
        weights -= args.lr * (x.T @ gradient_logits + 1e-4 * weights)
        bias -= args.lr * gradient_logits.sum(axis=0)

    final_probabilities = stable_softmax(x @ weights + bias)
    report = dict(bc)
    report.update(
        {
            "model": "pixel16-softmax-bc-clipped-replay-objective-not-ppo",
            "weights": weights.tolist(),
            "bias": bias.tolist(),
            "replayObjective": {
                "epochs": args.epochs,
                "clip": args.clip,
                "learningRate": args.lr,
                "algorithmClassification": "not PPO: static supervised replay states, no on-policy environment rollout",
                "meanExpectedRewardBefore": float(np.mean((old_probabilities * rewards).sum(axis=1))),
                "meanExpectedRewardAfter": float(np.mean((final_probabilities * rewards).sum(axis=1))),
            },
        }
    )
    save_json(args.output, report)
    print(json.dumps({"checkpoint": str(args.output), "replayObjective": report["replayObjective"]}, indent=2))


if __name__ == "__main__":
    main()
