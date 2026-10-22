from __future__ import annotations

import numpy as np


def softmax(logits: np.ndarray) -> np.ndarray:
    shifted = logits - logits.max(axis=-1, keepdims=True)
    exp = np.exp(shifted)
    return exp / exp.sum(axis=-1, keepdims=True)


def clipped_surrogate_loss_and_gradient(
    logits: np.ndarray,
    actions: np.ndarray,
    old_action_probabilities: np.ndarray,
    advantages: np.ndarray,
    clip: float,
) -> tuple[float, np.ndarray, dict[str, float]]:
    """Return PPO policy loss and its exact derivative with respect to logits."""
    probabilities = softmax(np.asarray(logits, dtype=np.float64))
    actions = np.asarray(actions, dtype=np.int64)
    old = np.clip(np.asarray(old_action_probabilities, dtype=np.float64), 1e-12, 1.0)
    advantages = np.asarray(advantages, dtype=np.float64)
    selected = probabilities[np.arange(len(actions)), actions]
    ratio = selected / old
    clipped_ratio = np.clip(ratio, 1.0 - clip, 1.0 + clip)
    unclipped = ratio * advantages
    clipped = clipped_ratio * advantages
    surrogate = np.minimum(unclipped, clipped)
    loss = -float(np.mean(surrogate))
    clipped_out = ((advantages > 0.0) & (ratio > 1.0 + clip)) | (
        (advantages < 0.0) & (ratio < 1.0 - clip)
    )
    one_hot = np.eye(probabilities.shape[1], dtype=np.float64)[actions]
    # d ratio / d logits = ratio * (one_hot - probabilities).
    signal = np.where(clipped_out, 0.0, ratio * advantages)
    gradient = -(one_hot - probabilities) * signal[:, None] / max(1, len(actions))
    entropy = -np.sum(probabilities * np.log(np.clip(probabilities, 1e-12, 1.0)), axis=1)
    approx_kl = np.log(old) - np.log(np.clip(selected, 1e-12, 1.0))
    stats = {
        "loss": loss,
        "ratioMean": float(np.mean(ratio)),
        "ratioMin": float(np.min(ratio)),
        "ratioMax": float(np.max(ratio)),
        "approxKlMean": float(np.mean(approx_kl)),
        "entropyMean": float(np.mean(entropy)),
        "clipFraction": float(np.mean(clipped_out)),
        "gradientNorm": float(np.linalg.norm(gradient)),
    }
    return loss, gradient, stats


def generalized_advantage_estimate(
    rewards: np.ndarray,
    values: np.ndarray,
    next_values: np.ndarray,
    terminated: np.ndarray,
    truncated: np.ndarray,
    gamma: float,
    gae_lambda: float,
) -> tuple[np.ndarray, np.ndarray]:
    """GAE with bootstrap on time-limit truncation but not true termination."""
    rewards = np.asarray(rewards, dtype=np.float64)
    values = np.asarray(values, dtype=np.float64)
    next_values = np.asarray(next_values, dtype=np.float64)
    terminated = np.asarray(terminated, dtype=bool)
    truncated = np.asarray(truncated, dtype=bool)
    bootstrap = np.where(terminated, 0.0, next_values)
    deltas = rewards + gamma * bootstrap - values
    advantages = np.zeros_like(deltas)
    running = 0.0
    for index in range(len(deltas) - 1, -1, -1):
        episode_boundary = bool(terminated[index] or truncated[index])
        running = deltas[index] + gamma * gae_lambda * (0.0 if episode_boundary else running)
        advantages[index] = running
    returns = advantages + values
    return advantages, returns


def normalize_batch(values: np.ndarray) -> np.ndarray:
    values = np.asarray(values, dtype=np.float64)
    if values.size <= 1:
        return values.copy()
    standard_deviation = float(values.std())
    if standard_deviation < 1e-8:
        return values - values.mean()
    return (values - values.mean()) / (standard_deviation + 1e-8)
