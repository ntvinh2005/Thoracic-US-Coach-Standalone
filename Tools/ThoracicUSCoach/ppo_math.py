from __future__ import annotations

import numpy as np


def require_finite(name: str, values: np.ndarray | float) -> None:
    if not np.all(np.isfinite(values)):
        raise FloatingPointError(f"{name} contains NaN or Inf")


def softmax(logits: np.ndarray) -> np.ndarray:
    require_finite("logits", logits)
    shifted = logits - logits.max(axis=-1, keepdims=True)
    exp = np.exp(shifted)
    return exp / exp.sum(axis=-1, keepdims=True)


def value_design_matrix(features: np.ndarray) -> np.ndarray:
    """Dimension-scale critic inputs and append an explicit bias column."""
    features = np.asarray(features, dtype=np.float64)
    if features.ndim != 2 or features.shape[1] == 0:
        raise ValueError("value features must be a non-empty 2D matrix")
    require_finite("value features", features)
    scaled = features / np.sqrt(float(features.shape[1]))
    return np.concatenate((scaled, np.ones((len(features), 1), dtype=np.float64)), axis=1)


def value_loss_and_gradient(
    features: np.ndarray,
    targets: np.ndarray,
    parameters: np.ndarray,
) -> tuple[float, np.ndarray, np.ndarray]:
    design = value_design_matrix(features)
    targets = np.asarray(targets, dtype=np.float64)
    parameters = np.asarray(parameters, dtype=np.float64)
    if targets.shape != (len(design),) or parameters.shape != (design.shape[1],):
        raise ValueError("critic targets/parameters have incompatible shapes")
    require_finite("critic targets", targets)
    require_finite("critic parameters", parameters)
    predictions = design @ parameters
    errors = predictions - targets
    loss = float(0.5 * np.mean(errors**2))
    gradient = design.T @ errors / max(1, len(design))
    require_finite("critic predictions", predictions)
    require_finite("critic loss", loss)
    require_finite("critic gradient", gradient)
    return loss, gradient, predictions


def value_predict(features: np.ndarray, parameters: np.ndarray) -> np.ndarray:
    design = value_design_matrix(features)
    parameters = np.asarray(parameters, dtype=np.float64)
    if parameters.shape != (design.shape[1],):
        raise ValueError("critic parameters have incompatible shape")
    require_finite("critic parameters", parameters)
    predictions = design @ parameters
    require_finite("critic predictions", predictions)
    return predictions


def adam_step(
    parameters: np.ndarray,
    gradient: np.ndarray,
    first_moment: np.ndarray,
    second_moment: np.ndarray,
    step: int,
    learning_rate: float,
    *,
    max_gradient_norm: float = 1.0,
    beta1: float = 0.9,
    beta2: float = 0.999,
    epsilon: float = 1e-8,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, dict[str, float]]:
    parameters = np.asarray(parameters, dtype=np.float64)
    gradient = np.asarray(gradient, dtype=np.float64)
    first_moment = np.asarray(first_moment, dtype=np.float64)
    second_moment = np.asarray(second_moment, dtype=np.float64)
    if not (parameters.shape == gradient.shape == first_moment.shape == second_moment.shape):
        raise ValueError("Adam state shapes do not match")
    if step < 1 or learning_rate <= 0.0:
        raise ValueError("Adam step and learning rate must be positive")
    require_finite("Adam parameters", parameters)
    require_finite("Adam gradient", gradient)
    raw_norm = float(np.linalg.norm(gradient))
    scale = min(1.0, max_gradient_norm / max(raw_norm, 1e-12))
    clipped = gradient * scale
    first_moment = beta1 * first_moment + (1.0 - beta1) * clipped
    second_moment = beta2 * second_moment + (1.0 - beta2) * clipped**2
    first_hat = first_moment / (1.0 - beta1**step)
    second_hat = second_moment / (1.0 - beta2**step)
    parameters = parameters - learning_rate * first_hat / (np.sqrt(second_hat) + epsilon)
    require_finite("Adam updated parameters", parameters)
    return parameters, first_moment, second_moment, {
        "rawGradientNorm": raw_norm,
        "clippedGradientNorm": float(np.linalg.norm(clipped)),
        "gradientClipScale": scale,
        "parameterNorm": float(np.linalg.norm(parameters)),
    }


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
    require_finite("old action probabilities", old)
    require_finite("advantages", advantages)
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
    require_finite("rewards", rewards)
    require_finite("values", values)
    require_finite("next values", next_values)
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
    require_finite("normalization input", values)
    if values.size <= 1:
        return values.copy()
    standard_deviation = float(values.std())
    if standard_deviation < 1e-8:
        return values - values.mean()
    return (values - values.mean()) / (standard_deviation + 1e-8)
