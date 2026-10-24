from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from evaluate_u08_p1 import analyze_episode


class U08P1EvaluatorTests(unittest.TestCase):
    def test_accepted_candidate_prediction_is_compared_to_next_observation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            raw = root / "raw.json"
            privileged = root / "privileged.json"
            raw.write_text(
                '{"trace":[{"step":0,"action":"LATERAL","accepted":true,'
                '"candidateSelected":true,"candidateMedialValue":0.1,'
                '"candidateLateralValue":0.4,"candidateFallback":false,'
                '"safetyFailure":false}]}', encoding="utf-8")
            privileged.write_text(
                '{"frames":[{"index":0,"finalTargetScore":0.2},'
                '{"index":1,"finalTargetScore":0.5}]}', encoding="utf-8")
            result = analyze_episode(root, {
                "rawResult": "raw.json", "privilegedTrace": "privileged.json"
            })
            self.assertEqual(len(result["predictionComparisons"]), 1)
            self.assertAlmostEqual(result["predictionComparisons"][0]["observedDelta"], 0.3)
            self.assertAlmostEqual(result["predictionComparisons"][0]["absoluteError"], 0.1)


if __name__ == "__main__":
    unittest.main()
