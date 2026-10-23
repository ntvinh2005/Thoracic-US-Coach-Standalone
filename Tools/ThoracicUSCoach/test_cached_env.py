from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from PIL import Image

from cached_env import CachedEnvironment, CachedFrame, CachedTrajectory, cached_features, start_indices


class CachedEnvironmentTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        images = []
        for index, value in enumerate((20, 80, 160, 220)):
            path = Path(self.temporary.name) / f"{index}.png"
            Image.new("L", (8, 8), value).save(path)
            images.append(path)
        frames = tuple(
            CachedFrame(index, image, float(index * 2), index == 2, True, True)
            for index, image in enumerate(images)
        )
        self.trajectory = CachedTrajectory("s", "train", "f", "training", 1, 1, 0, 1, frames)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_cached_transition_matches_contract_and_revisits(self) -> None:
        environment = CachedEnvironment(self.trajectory, 0, max_steps=6)
        first = environment.step(1)
        second = environment.step(-1)
        third = environment.step(1)
        self.assertEqual((1, 0, 1), (first.index, second.index, third.index))
        self.assertAlmostEqual(first.reward, third.reward)

    def test_stop_success_only_inside_exact_set(self) -> None:
        bad = CachedEnvironment(self.trajectory, 1, max_steps=3).step(0)
        good = CachedEnvironment(self.trajectory, 2, max_steps=3).step(0)
        self.assertFalse(bad.explicit_stop_success)
        self.assertTrue(good.explicit_stop_success)
        self.assertTrue(bad.terminated)
        self.assertTrue(good.terminated)

    def test_history_features_contain_only_images_and_executed_action(self) -> None:
        values = cached_features(
            self.trajectory.frames[1].image,
            self.trajectory.frames[0].image,
            -1,
            size=8,
            observation="history",
        )
        self.assertEqual(8 * 8 * 2 + 3, len(values))
        self.assertEqual([1.0, 0.0, 0.0], values[-3:].tolist())

    def test_start_set_includes_inside_and_both_endpoints(self) -> None:
        starts = start_indices(self.trajectory)
        self.assertEqual(0, starts["endpoint-low"])
        self.assertEqual(3, starts["endpoint-high"])
        self.assertEqual(2, starts["inside"])


if __name__ == "__main__":
    unittest.main()
