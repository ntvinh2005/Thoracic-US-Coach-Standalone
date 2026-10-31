import unittest

from p1_r3_recovery_analysis import classify_confirmation, freeze_eligible


class P1R3RecoveryAnalysisTests(unittest.TestCase):
    def test_confirmation_requires_every_independent_gate(self):
        self.assertEqual("PASS", classify_confirmation(True, True, True, True))
        self.assertEqual("FAIL", classify_confirmation(False, True, True, True))
        self.assertEqual("FAIL", classify_confirmation(True, False, True, True))
        self.assertEqual("FAIL", classify_confirmation(True, True, False, True))
        self.assertEqual("FAIL", classify_confirmation(True, True, True, False))

    def test_freeze_requires_actual_fine_motion_and_three_runs(self):
        summary = {
            "runs": [
                {"kind": "NAVIGATION", "positionPrecision": "PASS",
                 "imageIntegrity": "PASS", "acceptedFineMovements": 1},
                {"kind": "NAVIGATION", "positionPrecision": "PASS",
                 "imageIntegrity": "PASS", "acceptedFineMovements": 0},
                {"kind": "SENSITIVITY", "centerReturn": "PASS",
                 "imageIntegrity": "PASS", "acceptedFineMovements": 8},
            ]
        }
        self.assertTrue(freeze_eligible(summary))
        summary["runs"][0]["acceptedFineMovements"] = 0
        summary["runs"][2]["acceptedFineMovements"] = 0
        self.assertFalse(freeze_eligible(summary))

    def test_freeze_rejects_failed_integrity_or_center_return(self):
        summary = {
            "runs": [
                {"kind": "NAVIGATION", "positionPrecision": "PASS",
                 "imageIntegrity": "FAIL", "acceptedFineMovements": 1},
                {"kind": "NAVIGATION", "positionPrecision": "PASS",
                 "imageIntegrity": "PASS", "acceptedFineMovements": 1},
                {"kind": "SENSITIVITY", "centerReturn": "PASS",
                 "imageIntegrity": "PASS", "acceptedFineMovements": 8},
            ]
        }
        self.assertFalse(freeze_eligible(summary))


if __name__ == "__main__":
    unittest.main()
