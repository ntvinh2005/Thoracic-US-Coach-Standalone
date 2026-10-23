from __future__ import annotations

import ast
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from active2d_contract import ACTOR_REQUEST_FIELDS, CONTRACT, validate_actor_request
from run_active2d_policy_server import PolicyMemory, act


ROOT = Path(__file__).resolve().parents[2]


class Active2DNoCheatingTests(unittest.TestCase):
    def test_actor_request_is_restricted_to_image_and_policy_memory_identity(self) -> None:
        request = {
            "schemaVersion": 1,
            "contract": CONTRACT,
            "requestId": "request-1",
            "episodeId": "episode-1",
            "reset": True,
            "imagePath": "observation.png",
        }
        self.assertEqual(set(request), set(ACTOR_REQUEST_FIELDS))
        validate_actor_request(request)
        for forbidden in ("pose", "targetZone", "score", "pathIndex", "candidateManifest", "markerIdentity"):
            poisoned = dict(request)
            poisoned[forbidden] = 123
            with self.assertRaises(ValueError):
                validate_actor_request(poisoned)

    def test_python_actor_signature_has_no_privileged_input(self) -> None:
        source = (ROOT / "Tools/ThoracicUSCoach/run_active2d_policy_server.py").read_text(encoding="utf-8")
        tree = ast.parse(source)
        actor = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "act")
        arguments = {argument.arg for argument in actor.args.args}
        self.assertEqual(arguments, {"model", "stop_model", "image_path", "memory"})
        names = {node.id for node in ast.walk(actor) if isinstance(node, ast.Name)}
        forbidden = {
            "pose", "targetZone", "t7RightSeed", "t6RightMarker", "t8RightMarker",
            "pathIndex", "acceptableIndices", "referenceScore", "bestDiscoveredCandidate",
        }
        self.assertFalse(names & forbidden)

    def test_unity_live_loop_does_not_invoke_guided_oracle(self) -> None:
        source = (ROOT / "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs").read_text(encoding="utf-8")
        start = source.index("private IEnumerator RunActive2DImageOnly()")
        end = source.index("private void ResetActive2DRunCounters()", start)
        live = source[start:end]
        for forbidden in (
            "StartGuided2DDemo", "RunGuided2DDemo", "BuildPath(", "bestDiscoveredCandidate",
            "TargetProjection(", "candidate_manifest.json", "ApplyRecordedPose", "ApplyPathPose",
        ):
            self.assertNotIn(forbidden, live)

    def test_same_image_and_memory_are_independent_of_external_target_payload(self) -> None:
        rng = np.random.default_rng(7)
        feature_width = 256
        hidden = 4
        model = {
            "temporalFilter": {
                "transitionShiftPerLongitudinalStep": 0.15,
                "handoffThreshold": 0.8,
                "requiredConfirmations": 2,
            },
            "architecture": {
                "inputWeights": rng.normal(size=(feature_width, hidden)).tolist(),
                "inputBias": np.zeros(hidden).tolist(),
                "outputWeights": rng.normal(size=(hidden, 4)).tolist(),
                "outputBias": np.zeros(4).tolist(),
            },
            "handoffHead": {
                "inputWeights": rng.normal(size=(feature_width, hidden)).tolist(),
                "inputBias": np.zeros(hidden).tolist(),
                "outputWeights": rng.normal(size=hidden).tolist(),
                "outputBias": 0.0,
            },
        }
        with tempfile.TemporaryDirectory() as directory:
            image_path = Path(directory) / "neutral-name.png"
            Image.fromarray(np.arange(64 * 64, dtype=np.uint8).reshape(64, 64)).save(image_path)
            left = PolicyMemory("episode")
            right = PolicyMemory("episode")
            output_left = act(model, None, image_path, left)
            hidden_target_payload = {"targetZone": [999, -999, 42], "score": -1000, "markerIdentity": "T99"}
            self.assertTrue(hidden_target_payload)  # deliberately exists outside the actor call
            output_right = act(model, None, image_path, right)
            self.assertEqual(output_left, output_right)
            np.testing.assert_allclose(left.belief, right.belief)


if __name__ == "__main__":
    unittest.main()
