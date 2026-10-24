from __future__ import annotations

import tempfile
import json
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from active2d_stop import (
    ACTION_ORDER,
    DEPLOYMENT_GATE,
    checkpoint_deployability,
    request_stop_features,
    validate_stop_checkpoint,
)
from train_g2_temporal_stop import select_threshold
from run_active2d_policy_server import load_stop_checkpoint


class G2TemporalStopTests(unittest.TestCase):
    def test_history_and_actual_execution_are_encoded(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            previous, current = root / "previous.png", root / "current.png"
            Image.new("L", (32, 32), 20).save(previous)
            Image.new("L", (32, 32), 220).save(current)
            executed = request_stop_features(current, previous, "LATERAL", "temporal-v3", True, True)
            rejected = request_stop_features(current, previous, "LATERAL", "temporal-v3", False, False)
            no_history = request_stop_features(current, None, "RESET", "temporal-v3", True, True)
            self.assertEqual(4 * 256 + len(ACTION_ORDER) + 3, len(executed))
            action = slice(4 * 256, 4 * 256 + len(ACTION_ORDER))
            self.assertEqual(1.0, float(executed[action].sum()))
            self.assertEqual(0.0, float(rejected[action].sum()))
            self.assertGreater(float(np.abs(executed[2 * 256:3 * 256]).mean()), 0.5)
            self.assertEqual(0.0, float(np.abs(no_history[2 * 256:3 * 256]).max()))

    def test_hybrid_features_keep_spatial_stop_and_temporal_quality_inputs_separate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            previous, current = root / "previous.png", root / "current.png"
            Image.new("L", (64, 64), 20).save(previous)
            Image.new("L", (64, 64), 220).save(current)
            features = request_stop_features(
                current, previous, "LATERAL", "hybrid-spatial-v2+temporal-v3", True, True
            )
            self.assertEqual(1024 + len(ACTION_ORDER) + 4 * 256 + len(ACTION_ORDER) + 3, len(features))

    def test_temporal_checkpoint_requires_both_heads(self) -> None:
        width = 4 * 256 + len(ACTION_ORDER) + 3
        model = {
            "schema": "active2d-image-only-stop-v1", "version": 2,
            "contract": "active2d_image_only_v1",
            "experimentId": "thoracic_active2d_image_only_v1_20261023",
            "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
            "architecture": {
                "type": "standardized-temporal-quality-stop-v1", "featureExtractor": "temporal-v3",
                "featureWidth": width, "featureMean": [0.0] * width, "featureScale": [1.0] * width,
                "qualityWeights": [0.0] * width, "qualityBias": 0.0,
                "stopWeights": [0.0] * width, "stopBias": 0.0,
            },
            "stopRule": {"threshold": 0.5},
            "deployment": {
                "gate": DEPLOYMENT_GATE,
                "status": "DEPLOYABLE",
                "safeOperatingPoint": True,
                "reason": "test",
            },
        }
        validate_stop_checkpoint(model)
        del model["architecture"]["qualityWeights"]
        with self.assertRaises(ValueError): validate_stop_checkpoint(model)

    def test_safe_threshold_selection_and_no_safe_candidate_are_explicit(self) -> None:
        threshold, result = select_threshold(
            np.asarray([False, False, True, True]),
            np.asarray([0.1, 0.2, 0.8, 0.9]),
        )
        self.assertEqual(0.8, threshold)
        self.assertEqual("SAFE_OPERATING_POINT", result["status"])
        blocked, result = select_threshold(
            np.asarray([False, False, True, True]),
            np.asarray([0.9, 0.95, 0.1, 0.2]),
        )
        self.assertIsNone(blocked)
        self.assertEqual("NO_SAFE_OPERATING_POINT", result["status"])
        self.assertIn("diagnosticFallbackThreshold", result)

    def test_blocked_checkpoint_is_valid_evidence_but_rejected_for_live_deployment(self) -> None:
        width = 4 * 256 + len(ACTION_ORDER) + 3
        model = {
            "schema": "active2d-image-only-stop-v1", "version": 2,
            "contract": "active2d_image_only_v1",
            "experimentId": "thoracic_active2d_image_only_v1_20261023",
            "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
            "architecture": {
                "type": "standardized-temporal-quality-stop-v1", "featureExtractor": "temporal-v3",
                "featureWidth": width, "featureMean": [0.0] * width, "featureScale": [1.0] * width,
                "qualityWeights": [0.0] * width, "qualityBias": 0.0,
                "stopWeights": [0.0] * width, "stopBias": 0.0,
            },
            "stopRule": {"threshold": None},
            "deployment": {
                "gate": DEPLOYMENT_GATE,
                "status": "BLOCKED",
                "safeOperatingPoint": False,
                "reason": "no-safe-calibration-operating-point",
            },
        }
        validate_stop_checkpoint(model)
        self.assertEqual((False, "no-safe-calibration-operating-point"), checkpoint_deployability(model))
        with self.assertRaisesRegex(ValueError, "non-deployable"):
            validate_stop_checkpoint(model, require_deployable=True)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "blocked.json"
            path.write_text(json.dumps(model), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "non-deployable"):
                load_stop_checkpoint(path)

    def test_malformed_versioned_checkpoint_is_rejected(self) -> None:
        width = 4 * 256 + len(ACTION_ORDER) + 3
        model = {
            "schema": "active2d-image-only-stop-v1", "version": 2,
            "contract": "active2d_image_only_v1",
            "experimentId": "thoracic_active2d_image_only_v1_20261023",
            "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
            "architecture": {
                "type": "standardized-temporal-quality-stop-v1", "featureExtractor": "temporal-v3",
                "featureWidth": width, "featureMean": [0.0] * width, "featureScale": [1.0] * width,
                "qualityWeights": [0.0] * width, "qualityBias": 0.0,
                "stopWeights": [0.0] * width, "stopBias": 0.0,
            },
            "stopRule": {"threshold": 0.5},
        }
        with self.assertRaisesRegex(ValueError, "deployment decision"):
            validate_stop_checkpoint(model)


if __name__ == "__main__":
    unittest.main()
