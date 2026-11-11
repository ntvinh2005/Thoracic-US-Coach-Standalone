import unittest

from run_phase2_best_view_server import validate_actor_model
from phase2_best_view_recognition import ACTOR_INPUTS, FORBIDDEN_INPUTS


class Phase2BestViewServerTests(unittest.TestCase):
    def test_actor_contract_accepts_pixels_only(self):
        validate_actor_model({
            "actorInputs": ACTOR_INPUTS,
            "forbiddenInputs": FORBIDDEN_INPUTS,
            "featureMode": "global",
            "threshold": 0.7,
        })

    def test_privileged_actor_input_contract_is_rejected(self):
        with self.assertRaises(ValueError):
            validate_actor_model({
                "actorInputs": ("cleanUltrasoundImage", "probeWorldPose"),
                "forbiddenInputs": FORBIDDEN_INPUTS,
                "featureMode": "global",
                "threshold": 0.7,
            })


if __name__ == "__main__":
    unittest.main()
