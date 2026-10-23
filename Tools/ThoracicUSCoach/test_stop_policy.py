from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from cached_env import CachedFrame, CachedTrajectory
from coach_data import (
    TransitionSample,
    decision_transition_feature_matrix,
    request_features,
    valid_policy_decisions,
)
from stop_policy import build_decision_contexts, feature_width


class DecisionSemanticsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.images = []
        for index in range(5):
            values = np.zeros((8, 8), dtype=np.uint8)
            values[index : index + 2, :] = 30 + index * 20
            path = self.root / f"frame_{index}.png"
            Image.fromarray(values).save(path)
            self.images.append(path)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def transition(self, *, kind="move", terminated=False, truncated=False, executed=True):
        return TransitionSample(
            manifest=self.root / "manifest.json",
            experiment_id="e",
            dataset_run_id="r",
            sweep_id="s",
            split="train",
            configuration_family="f",
            evaluation_group="training",
            transition_id=f"t-{kind}-{terminated}-{truncated}",
            transition_kind=kind,
            previous_image=self.images[0],
            previous_image_relative="frame_0.png",
            previous_image_sha256="a",
            previous_index=0,
            executed_action=0 if kind == "reset" else 1,
            action_was_executed=executed,
            current_image=self.images[1],
            current_image_relative="frame_1.png",
            current_image_sha256="b",
            current_index=1,
            teacher_action=1,
            teacher_action_defined=True,
            target_acceptable=False,
            contact_valid=True,
            workspace_valid=True,
            boundary_clamped=False,
            terminated=terminated,
            truncated=truncated,
            configuration_sha256="c",
            labels_sha256="l",
        )

    def test_post_terminal_rows_are_not_policy_decisions(self) -> None:
        valid = self.transition()
        terminal = self.transition(terminated=True)
        truncated = self.transition(truncated=True)
        self.assertEqual([valid], valid_policy_decisions([valid, terminal, truncated]))
        with self.assertRaisesRegex(ValueError, "not policy decisions"):
            decision_transition_feature_matrix([terminal], size=8)

    def test_reset_previous_equals_current_and_sentinel_is_not_executed(self) -> None:
        reset = self.transition(kind="reset", executed=False)
        features = decision_transition_feature_matrix([reset], size=8)[0]
        np.testing.assert_allclose(features[:64], features[64:128])
        self.assertEqual([0.0, 1.0, 0.0], features[-3:].tolist())
        bad = self.transition(kind="reset", executed=True)
        with self.assertRaisesRegex(ValueError, "claims an executed action"):
            decision_transition_feature_matrix([bad], size=8)

    def test_missing_nonreset_history_is_rejected(self) -> None:
        missing = self.root / "deleted.png"
        with self.assertRaises(FileNotFoundError):
            request_features(self.images[1], missing, 1, 8, "history")
        reset = request_features(self.images[1], None, 0, 8, "history")
        np.testing.assert_allclose(reset[:64], reset[64:128])

    def test_synthetic_contexts_cover_reset_and_both_directions(self) -> None:
        frames = tuple(
            CachedFrame(index, path, float(index), index in (2, 3), True, True)
            for index, path in enumerate(self.images)
        )
        trajectory = CachedTrajectory("s", "train", "f", "training", 1, 1, 0, 1, frames)
        contexts = build_decision_contexts([trajectory])
        center = [context for context in contexts if context.current_index == 2]
        self.assertEqual({-1, 0, 1}, {context.previous_action for context in center})
        reset = next(context for context in center if context.reset)
        self.assertEqual(reset.current_image, reset.previous_image)
        self.assertTrue(reset.acceptable)

    def test_declared_feature_widths(self) -> None:
        self.assertEqual(64, feature_width(8, "current", "basic"))
        self.assertEqual(131, feature_width(8, "history", "basic"))
        self.assertEqual(201, feature_width(8, "history", "diff-stats"))
        self.assertEqual(208, feature_width(8, "current", "structural-basic"))
        self.assertEqual(419, feature_width(8, "history", "structural-basic"))
        self.assertEqual(633, feature_width(8, "history", "structural-diff-stats"))


if __name__ == "__main__":
    unittest.main()
