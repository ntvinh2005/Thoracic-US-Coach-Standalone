from __future__ import annotations

import unittest

import numpy as np

from ppo_math import clipped_surrogate_loss_and_gradient, generalized_advantage_estimate, normalize_batch, softmax


def finite_difference(logits, actions, old, advantages, clip, epsilon=1e-6):
    numerical = np.zeros_like(logits, dtype=np.float64)
    for row in range(logits.shape[0]):
        for column in range(logits.shape[1]):
            plus = logits.copy()
            minus = logits.copy()
            plus[row, column] += epsilon
            minus[row, column] -= epsilon
            loss_plus = clipped_surrogate_loss_and_gradient(plus, actions, old, advantages, clip)[0]
            loss_minus = clipped_surrogate_loss_and_gradient(minus, actions, old, advantages, clip)[0]
            numerical[row, column] = (loss_plus - loss_minus) / (2 * epsilon)
    return numerical


class PpoMathTests(unittest.TestCase):
    def assert_gradient(self, logits, actions, old, advantages, clip=0.2):
        _, analytic, _ = clipped_surrogate_loss_and_gradient(logits, actions, old, advantages, clip)
        numerical = finite_difference(logits, actions, old, advantages, clip)
        np.testing.assert_allclose(analytic, numerical, atol=2e-7, rtol=2e-6)

    def test_positive_and_negative_unclipped_gradients(self):
        logits = np.asarray([[0.2, -0.1, 0.0], [-0.3, 0.4, 0.1]], dtype=np.float64)
        actions = np.asarray([0, 2])
        current = softmax(logits)[np.arange(2), actions]
        self.assert_gradient(logits, actions, current / np.asarray([1.05, 0.95]), np.asarray([1.7, -0.8]))

    def test_positive_and_negative_clipped_gradients_are_zero(self):
        logits = np.asarray([[2.0, -1.0, -1.0], [-2.0, 1.0, 1.0]], dtype=np.float64)
        actions = np.asarray([0, 0])
        current = softmax(logits)[np.arange(2), actions]
        old = current / np.asarray([1.5, 0.5])
        advantages = np.asarray([1.0, -1.0])
        _, analytic, stats = clipped_surrogate_loss_and_gradient(logits, actions, old, advantages, 0.2)
        np.testing.assert_allclose(analytic, 0.0, atol=1e-12)
        self.assertEqual(1.0, stats["clipFraction"])
        self.assert_gradient(logits, actions, old, advantages)

    def test_singleton_advantage_is_not_erased(self):
        normalized = normalize_batch(np.asarray([2.5]))
        np.testing.assert_allclose(normalized, [2.5])

    def test_truncation_bootstraps_but_termination_does_not(self):
        rewards = np.asarray([0.0, 0.0])
        values = np.asarray([0.0, 0.0])
        next_values = np.asarray([3.0, 3.0])
        advantages, _ = generalized_advantage_estimate(
            rewards, values, next_values,
            terminated=np.asarray([True, False]),
            truncated=np.asarray([False, True]),
            gamma=0.9,
            gae_lambda=0.95,
        )
        self.assertEqual(0.0, advantages[0])
        self.assertAlmostEqual(2.7, advantages[1])


if __name__ == "__main__":
    unittest.main()
