from __future__ import annotations

import unittest

from contract_v3 import run_diagnostic_suite, transition


class ContractV3Tests(unittest.TestCase):
    def test_oracle_solves_every_reachable_case(self) -> None:
        report = run_diagnostic_suite()
        self.assertEqual([], report["summary"]["oracleFailures"])
        self.assertEqual(1.0, report["summary"]["oracleSuccessRate"])

    def test_zero_invalid_success_flags(self) -> None:
        report = run_diagnostic_suite()
        self.assertEqual(0, report["summary"]["invalidSuccessFlags"])

    def test_disjoint_gap_is_not_accepted(self) -> None:
        result = transition(
            index=3,
            action=0,
            step_count=1,
            acceptable=(False, False, True, False, False, True),
            path_mm=(0, 1, 2, 3, 4, 5),
            contact_valid=True,
            workspace_valid=True,
            steps_taken=0,
            max_steps=4,
        )
        self.assertFalse(result.target_acceptable)
        self.assertFalse(result.explicit_stop_success)


if __name__ == "__main__":
    unittest.main()
