from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from active2d_data import Active2DFrame
from active2d_contract import ACTION_ORDER
from active2d_stop import checkpoint_deployability, request_stop_features, validate_stop_checkpoint
from g2_r2_features import feature_width, frame_feature_matrix, request_features


def frame(path: Path, index: int, action: str, *, accepted: bool = True, completed: bool = True) -> Active2DFrame:
    return Active2DFrame(
        image=path, image_relative=path.name, image_sha256=str(index), episode_id="episode",
        dataset_run_id="test", split="train", family="family", seed=1, index=index,
        issued_action=action, action_accepted=accepted, action_completed=completed,
        episode_reset=index == 0, level="T7", t7_distance_mm=0.0, t7_acceptable=True,
        target_acceptable=False, target_score=0.5, contact_valid=True, workspace_valid=True,
    )


class G2R2FeatureTests(unittest.TestCase):
    def test_feature_widths_and_no_action_ablation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image.png"
            Image.fromarray(np.arange(64 * 64, dtype=np.uint8).reshape(64, 64)).save(path)
            structural = request_features(path, "structural-v4")
            self.assertEqual(feature_width("structural-v4"), len(structural))
            left = request_stop_features(path, None, "LATERAL", "structural-v4", True, True)
            right = request_stop_features(path, None, "MEDIAL", "structural-v4", False, False)
            np.testing.assert_array_equal(left, right)

    def test_causal_history_offline_live_parity_and_rejected_action(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = []
            for index, value in enumerate((10, 80, 220)):
                path = root / f"{index}.png"
                Image.new("L", (64, 64), value).save(path); paths.append(path)
            frames = [frame(paths[0], 0, "RESET"), frame(paths[1], 1, "LATERAL"), frame(paths[2], 2, "MEDIAL", accepted=False, completed=False)]
            offline = frame_feature_matrix(frames, "temporal-structural-v5-h4")[-1]
            live = request_stop_features(
                paths[2], paths[1], "MEDIAL", "temporal-structural-v5-h4", False, False,
                paths[:2], ["RESET", "LATERAL"], [True, True], [True, True],
            )
            np.testing.assert_allclose(offline, live)
            action_tail = live[-4 * (512 + len(ACTION_ORDER) + 3):]
            most_recent_action = action_tail[512:512 + len(ACTION_ORDER)]
            self.assertEqual(0.0, float(most_recent_action.sum()))
            self.assertGreater(float(np.abs(live).sum()), 0.0)

    def test_blocked_structural_checkpoint_validates_but_cannot_deploy(self) -> None:
        width = feature_width("structural-v4")
        checkpoint = {
            "schema": "active2d-image-only-stop-v1", "version": 3,
            "contract": "active2d_image_only_v1", "experimentId": "thoracic_active2d_image_only_v1_20261023",
            "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
            "architecture": {
                "type": "standardized-temporal-quality-stop-v1", "featureExtractor": "structural-v4",
                "featureWidth": width, "featureMean": [0.0] * width, "featureScale": [1.0] * width,
                "qualityWeights": [0.0] * width, "qualityBias": 0.0,
                "stopWeights": [0.0] * width, "stopBias": 0.0,
            },
            "stopRule": {"threshold": None},
            "deployment": {"gate": "g2-stop-safety-v1", "status": "BLOCKED", "safeOperatingPoint": False, "reason": "test-blocked"},
        }
        validate_stop_checkpoint(checkpoint)
        self.assertEqual((False, "test-blocked"), checkpoint_deployability(checkpoint))
        with self.assertRaisesRegex(ValueError, "non-deployable"):
            validate_stop_checkpoint(checkpoint, require_deployable=True)


if __name__ == "__main__":
    unittest.main()
