from __future__ import annotations

import copy
import tempfile
import unittest
from pathlib import Path

import numpy as np

from coach_data import model_probabilities
from contract_v3 import CONTRACT_VERSION, EXPERIMENT_ID, OBSERVATION_SCHEMA, TARGET_SEMANTICS
from freeze_1d_scenarios import GROUP_SCENARIOS
from protocol_v3 import ACTION_ORDER, PREPROCESSING_ID, protocol_identity, scenario_identity_sha256, validate_manifest, validate_model, verify_frozen_scenario
from unity_env import UnityFileEnvironment


def scenario() -> dict[str, object]:
    return {
        "environmentContractVersion": CONTRACT_VERSION,
        "observationSchema": OBSERVATION_SCHEMA,
        "targetSemantics": TARGET_SEMANTICS,
        "actionOrder": ACTION_ORDER,
        "poses": [{"position": {"x": 1.0, "y": 2.0, "z": 3.0}, "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}}],
        "pathMm": [0.0],
        "acceptableIndices": [0],
        "actionStepCount": 1,
        "episodeBudget": 35,
        "maxPenetrationMm": 0.5,
        "rendererFullySeeded": False,
    }


class ProtocolV3Tests(unittest.TestCase):
    def test_compact_mlp_checkpoint_validates_and_predicts(self) -> None:
        model = {
            "experimentId": EXPERIMENT_ID,
            "environmentContractVersion": CONTRACT_VERSION,
            "observationSchema": OBSERVATION_SCHEMA,
            "preprocessing": PREPROCESSING_ID,
            "actionOrder": ACTION_ORDER,
            "architecture": "mlp-relu-v1",
            "inputWeights": [[1.0, -1.0], [0.5, 0.25]],
            "inputBias": [0.0, 0.0],
            "outputWeights": [[1.0, 0.0, -1.0], [-1.0, 0.0, 1.0]],
            "outputBias": [0.0, 0.0, 0.0],
        }
        validate_model(model, 2)
        probabilities = model_probabilities(model, np.asarray([1.0, 0.0], dtype=np.float32))
        self.assertEqual((3,), probabilities.shape)
        self.assertAlmostEqual(1.0, float(probabilities.sum()), places=6)

    def test_environment_bridge_retains_only_recent_observations(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            environment = object.__new__(UnityFileEnvironment)
            environment.bridge = Path(temporary)
            environment.retained_observation_limit = 2
            environment._retained_observations = []
            images = [environment.bridge / f"env_observation_{index}.png" for index in range(3)]
            for image in images:
                image.write_bytes(b"png")
                environment._retain_observation(image)
            self.assertFalse(images[0].exists())
            self.assertTrue(images[1].exists())
            self.assertTrue(images[2].exists())

    def test_frozen_groups_match_balanced_500_schedule(self) -> None:
        def group_for_slot(slot: int) -> str:
            if slot < 15:
                return "training"
            if slot < 18:
                return "development"
            return "stress" if slot >= 23 else "heldout"

        for expected, slots in GROUP_SCENARIOS.items():
            self.assertTrue(slots)
            self.assertEqual(len(slots), len(set(slots)))
            self.assertTrue(all(group_for_slot(slot % 25) == expected for slot in slots))

    def test_exact_frozen_scenario_is_sent_on_reset(self) -> None:
        environment = object.__new__(UnityFileEnvironment)
        environment._exchange = lambda payload: payload
        frozen = scenario()
        request = environment.reset(3, max_steps=35, frozen_scenario=frozen)
        self.assertTrue(request["replayFrozenScenario"])
        self.assertEqual(frozen, request["frozenScenario"])
        self.assertFalse(request["regenerateTrajectory"])
        self.assertEqual(3, request["startIndex"])

    def test_changed_pose_action_step_or_horizon_is_rejected(self) -> None:
        original = scenario()
        frozen = {"scenarioIdentitySha256": scenario_identity_sha256(original)}
        for mutate in (
            lambda value: value["poses"][0]["position"].update({"x": 1.001}),
            lambda value: value.update({"actionStepCount": 2}),
            lambda value: value.update({"episodeBudget": 36}),
        ):
            changed = copy.deepcopy(original)
            mutate(changed)
            with self.assertRaises(RuntimeError):
                verify_frozen_scenario(frozen, changed)

    def test_development_selection_policy_is_enforced(self) -> None:
        protocol = protocol_identity(35)
        manifest = {
            "experimentId": EXPERIMENT_ID,
            "contractVersion": CONTRACT_VERSION,
            "observationSchema": OBSERVATION_SCHEMA,
            "targetSemantics": TARGET_SEMANTICS,
            "actionOrder": ACTION_ORDER,
            "group": "development",
            "selectionAllowed": True,
            "protocol": protocol,
        }
        validate_manifest(manifest, expected_group="development", selection_allowed=True)
        manifest["group"] = "heldout"
        manifest["selectionAllowed"] = False
        with self.assertRaises(ValueError):
            validate_manifest(manifest, expected_group="development", selection_allowed=True)


if __name__ == "__main__":
    unittest.main()
