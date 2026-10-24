from __future__ import annotations

import json
import copy
import re
import tempfile
import unittest
from pathlib import Path

from evaluate_gate2_phase1 import (
    recompute,
    recompute_start_geometry,
    unity_float_matches,
    validate_frozen_protocol,
)


ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = ROOT / "Tools/ThoracicUSCoach/experiments/gate2_phase1_fresh_40_v1.json"


def synthetic_start(expected: str) -> dict:
    return {
        "expectedLevel": expected, "actualLevel": expected, "expectedLabelMatches": True,
        "contactValid": True, "workspaceValid": True, "t7Reachable": True,
        "t7DistanceMm": 3.0, "t7LongitudinalOffsetMm": 3.0,
        "t7LateralOffsetMm": 0.0, "t7PreciseRadiusMm": 4.0,
        "closestAttainableT7DistanceMm": 0.0,
        "longitudinalTravelToPreciseRegionMm": 0.0,
        "minimumLongitudinalSteps": 0, "t7Direction": "AT_T7",
        "contactPosition": {"x": 0, "y": 0, "z": 0},
        "t7TargetPosition": {"x": 3, "y": 0, "z": 0},
        "caudalAxis": {"x": 1, "y": 0, "z": 0},
        "lateralAxis": {"x": 0, "y": 1, "z": 0},
        "probeRotation": {"x": 0, "y": 0, "z": 0, "w": 1},
        "longitudinalHalfExtentMm": 48.0, "stepMm": 3.0, "preflightMaxSteps": 32,
    }


def synthetic_bundle(root: Path, flip_raw_verdicts: bool = False) -> tuple[dict, dict]:
    levels = ["T6", "T7", "T8", "OUTSIDE_T6_T8"]
    cases = []
    outcomes = []
    preflight = []
    nonce = "abcdef123456"
    for order in range(40):
        expected = levels[order % 4]
        case_id = f"CASE_{order:02d}_{expected}"
        family = f"private_family_{order:02d}_{expected}"
        seed = 700000000 + order
        planned = {
            "caseId": case_id, "order": order, "family": family, "seed": seed,
            "startStratum": order % 4, "expectedStartLevel": expected,
            "plannedInterpolation": 0.5, "plannedLateralMm": 0.0,
        }
        cases.append(planned)
        start = synthetic_start(expected)
        physical_trace = [] if expected == "T7" else [{
            "step": 0, "action": "CAUDAL", "accepted": True,
            "contactValid": True, "workspaceValid": True, "safetyFailure": False,
            "t7DistanceMm": 1.0, "t7LevelAcceptable": True,
        }]
        preflight.append({
            "caseId": case_id, "order": order, "placementSucceeded": True, "valid": True,
            "start": copy.deepcopy(start), "physicalReachabilityTested": True,
            "physicalReachedPreciseRegion": True, "physicalSteps": len(physical_trace),
            "physicalRejectedMoves": 0, "physicalUnsafe": False,
            "closestObservedT7DistanceMm": 3.0 if not physical_trace else 1.0,
            "physicalTrace": physical_trace,
        })
        episode = f"G2E_{nonce}_{order:04d}"
        raw_name = f"raw_{order}.json"
        privileged_name = f"privileged_{order}.json"
        raw = {
            "episodeId": episode, "finalPhase": "FinalSuccess",
            "overallPass": not flip_raw_verdicts,
            "phaseOneHandoff": not flip_raw_verdicts,
            "correctLevelAtHandoff": not flip_raw_verdicts,
            "finalContactValid": True, "finalWorkspaceValid": True,
            "rejectedMovements": 0, "contactViolations": 0, "workspaceViolations": 0,
            "failureReason": "", "trace": [{
                "step": 0, "action": "PHASEHANDOFF", "accepted": True,
                "confidence": 0.9, "contactValidAfterExecution": True,
                "workspaceValidAfterExecution": True, "safetyFailure": False,
                "image": f"Artifacts/ThoracicUSCoach/bridge/active2d/observation_{episode}_0000.png",
            }],
        }
        privileged = {
            "workspaceOrigin": {"x": 0, "y": 0, "z": 0},
            "actuatorCranialAxis": {"x": -1, "y": 0, "z": 0},
            "actuatorLateralAxis": {"x": 0, "y": 1, "z": 0},
            "t7TargetPosition": {"x": 3, "y": 0, "z": 0},
            "t7PreciseRadiusMm": 4.0, "stepMm": 3.0,
            "longitudinalHalfExtentMm": 48.0, "lateralHalfExtentMm": 36.0,
            "frames": [
                {"index": -1, "contactPosition": {"x": 0, "y": 0, "z": 0},
                 "nearestLevel": expected, "t7DistanceMm": 3.0,
                 "contactValid": True, "workspaceValid": True, "t7LevelAcceptable": expected == "T7"},
                {"index": 0, "contactPosition": {"x": 0, "y": 0, "z": 0},
                 "nearestLevel": "T7", "t7DistanceMm": 3.0,
                 "contactValid": True, "workspaceValid": True, "t7LevelAcceptable": True},
            ],
        }
        (root / raw_name).write_text(json.dumps(raw), encoding="utf-8")
        (root / privileged_name).write_text(json.dumps(privileged), encoding="utf-8")
        outcomes.append({
            "caseId": case_id, "order": order, "rawResult": raw_name, "privilegedTrace": privileged_name,
            "start": start,
        })
    manifest = {
        "protocolId": "test", "requiredCaseCount": 40, "requiredPerStratum": 10,
        "handoffThreshold": 0.85, "preflightMaxSteps": 32, "stepBudget": 300,
        "minimumCorrectPreciseHandoffs": 33, "maximumWrongLevelHandoffs": 1,
        "maximumConfidentWrongLevelHandoffs": 0, "minimumRecognitionAfterPreciseEntry": 0.9,
        "cases": cases,
    }
    report = {"complete": True, "runNonce": nonce, "preflight": preflight, "cases": outcomes}
    return manifest, report


class Gate2Phase1Tests(unittest.TestCase):
    def test_unity_float_serialization_matches_frozen_threshold(self) -> None:
        self.assertTrue(unity_float_matches(0.8500000238418579, 0.85))
        self.assertFalse(unity_float_matches(0.851, 0.85))

    def test_manifest_is_frozen_balanced_and_direct_only(self) -> None:
        manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
        validate_frozen_protocol(manifest, ROOT)
        self.assertEqual(len(manifest["cases"]), 40)
        self.assertEqual(len({case["seed"] for case in manifest["cases"]}), 40)
        self.assertEqual(len({case["family"] for case in manifest["cases"]}), 40)
        self.assertEqual([case["order"] for case in manifest["cases"]], list(range(40)))
        for stratum in range(4):
            self.assertEqual(sum(case["startStratum"] == stratum for case in manifest["cases"]), 10)

    def test_unity_runner_does_not_expand_actor_request(self) -> None:
        source = (ROOT / "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs").read_text(
            encoding="utf-8")
        request = source[source.index("Active2DActorRequest request ="):source.index(
            "WriteActive2DRequest(request)")]
        for forbidden in ("seed", "startStratum", "expectedStartLevel", "plannedInterpolation",
                          "plannedLateralMm", "nearestLevel", "t7DistanceMm", "probePosition"):
            self.assertNotIn(forbidden, request)
        for required in ("requestId", "episodeId", "reset", "imagePath"):
            self.assertIn(required, request)
        types_source = (ROOT / "Assets/ThoracicUSCoach/Runtime/ThoracicActive2DTypes.cs").read_text(
            encoding="utf-8")
        actor_class = re.search(
            r"public sealed class Active2DActorRequest\s*\{(.*?)^    \}",
            types_source, re.MULTILINE | re.DOTALL).group(1)
        fields = set(re.findall(r"public\s+[\w<>]+\s+(\w+)\s*(?:=[^;]+)?;", actor_class))
        self.assertEqual(fields, {"schemaVersion", "contract", "requestId", "episodeId", "reset", "imagePath"})

    def test_gate2_actor_identifiers_are_opaque(self) -> None:
        source = (ROOT / "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Gate2.cs").read_text(
            encoding="utf-8")
        body = source[source.index("private string Gate2EpisodeId"):]
        body = body[:body.index("\n        }")]
        for forbidden in ("caseId", "family", "expectedStartLevel", "seed", "startStratum", "protocolId"):
            self.assertNotIn(forbidden, body)
        manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
        nonce = "0123456789ab"
        for case in manifest["cases"]:
            visible = f"G2E_{nonce}_{case['order']:04d}"
            for secret in (case["caseId"], case["family"], case["expectedStartLevel"], str(case["seed"])):
                self.assertNotIn(secret.lower(), visible.lower())

    def test_recompute_uses_raw_and_privileged_traces(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, report = synthetic_bundle(root, flip_raw_verdicts=True)
            result = recompute(manifest, report, root)
            self.assertEqual(result["summary"]["correctPreciseHandoffs"], 40)
            self.assertEqual(result["summary"]["rawVerdictMismatches"], 40)
            self.assertFalse(result["summary"]["protocolComplete"])
            self.assertTrue(result["cases"][0]["recognitionAfterPreciseEntry"])

    def test_tampered_start_derived_geometry_is_rejected(self) -> None:
        start = synthetic_start("T7")
        start["t7LongitudinalOffsetMm"] = 99.0
        with self.assertRaisesRegex(ValueError, "start longitudinal mismatch"):
            recompute_start_geometry(start, 300)

    def test_actor_leakage_is_recomputed_from_raw_episode(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, report = synthetic_bundle(root)
            raw_path = root / report["cases"][0]["rawResult"]
            raw = json.loads(raw_path.read_text(encoding="utf-8"))
            raw["episodeId"] = manifest["cases"][0]["caseId"]
            raw_path.write_text(json.dumps(raw), encoding="utf-8")
            result = recompute(manifest, report, root)
            self.assertGreater(result["summary"]["actorPrivilegedLeakage"], 0)
            self.assertFalse(result["summary"]["protocolComplete"])

    def test_abstention_terminal_safety_uses_last_trace_not_raw_boolean(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, report = synthetic_bundle(root)
            raw_path = root / report["cases"][0]["rawResult"]
            raw = json.loads(raw_path.read_text(encoding="utf-8"))
            raw["trace"][0]["action"] = "CAUDAL"
            raw["phaseOneHandoff"] = False
            raw["correctLevelAtHandoff"] = False
            raw["overallPass"] = False
            raw["finalContactValid"] = False
            raw["finalWorkspaceValid"] = False
            raw_path.write_text(json.dumps(raw), encoding="utf-8")
            result = recompute(manifest, report, root)
            case = result["cases"][0]
            self.assertTrue(case["abstention"])
            self.assertTrue(case["finalContactValid"])
            self.assertTrue(case["finalWorkspaceValid"])


if __name__ == "__main__":
    unittest.main()
