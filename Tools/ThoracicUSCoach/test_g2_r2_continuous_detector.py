from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from g2_r2_continuous_detector import (
    BLIND_OUTPUTS,
    _check_forbidden_keys,
    build_relative_map,
    consolidate_reverse_consensus,
    detect_local_events,
    freeze_outputs,
    motif_scores,
    ordered_match,
    sha256,
    validate_actor_trace,
    verify_freeze,
)


SCALARS = (
    "shadow_width", "shadow_centroid_offset", "shadow_taper",
    "bilateral_symmetry", "bilateral_coverage_asymmetry",
    "mean_intensity", "pixel_variance", "gradient_energy", "edge_coverage",
)


def synthetic_rows(event_coordinates: tuple[float, ...]) -> list[dict]:
    rows = []
    for index, coordinate in enumerate(np.arange(-45.0, 46.0, 1.5)):
        descriptor = np.zeros(8)
        descriptor[0] = 1.0
        if any(abs(coordinate - event) < 0.6 for event in event_coordinates):
            descriptor[:] = 0.0
            descriptor[1] = 1.0
        row = {
            "observationIndex": index,
            "image": f"frames/frame_{index:04d}.png",
            "coordinateMm": float(coordinate),
            "descriptor": descriptor,
        }
        row.update({name: 0.0 for name in SCALARS})
        rows.append(row)
    return rows


def event(coordinate: float, score: float = 1.0) -> dict:
    return {
        "coordinateMm": coordinate,
        "score": score,
        "supportIntervalMm": [coordinate - 1.5, coordinate + 1.5],
        "supportingFrame": int(coordinate + 100),
        "supportingImage": f"frame_{int(coordinate + 100):04d}.png",
    }


class ContinuousDetectorTests(unittest.TestCase):
    def test_actor_evaluator_isolation_rejects_world_pose(self) -> None:
        with self.assertRaises(ValueError):
            _check_forbidden_keys({"observations": [{"probePosition": [0, 0, 0]}]})

    def test_actual_relative_displacement_is_reconstructed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            frames = root / "frames"
            frames.mkdir()
            observations = []
            coordinate = 0.0
            for index, (action, achieved) in enumerate((
                ("Observe", 0.0), ("Caudal", 1.4), ("Cranial", 1.3)
            )):
                image = frames / f"frame_{index:04d}.png"
                Image.fromarray(np.full((32, 32), index * 20, dtype=np.uint8)).save(image)
                if action == "Caudal":
                    coordinate += achieved
                elif action == "Cranial":
                    coordinate -= achieved
                observations.append({
                    "observationIndex": index, "requestedAction": action,
                    "executedSuccessfully": True, "achievedDisplacementMm": achieved,
                    "signedActorRelativeMm": coordinate,
                    "frameIntegrityValid": True, "contactValidAfterExecution": True,
                    "workspaceValidAfterExecution": True, "image": image.relative_to(root).as_posix(),
                    "imageSha256": sha256(image), "poseEpoch": index + 1,
                    "producerPoseEpoch": index + 1, "poseAppliedUnityFrame": index * 3 + 1,
                    "producerUnityFrame": index * 3 + 2, "captureUnityFrame": index * 3 + 2,
                    "producerScanSerial": index + 1, "activeAggregatorCount": 0,
                })
            validated = validate_actor_trace(root, {"observations": observations})
            self.assertEqual(len(validated), 3)
            self.assertAlmostEqual(validated[-1]["signedActorRelativeMm"], 0.1)

    def test_random_start_relative_displacement_is_reconstructed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            frames = root / "frames"
            frames.mkdir()
            observations = []
            coordinate = 3.0
            for index, (action, achieved) in enumerate((
                ("Observe", 0.0), ("Cranial", 1.5), ("Cranial", 1.5)
            )):
                image = frames / f"frame_{index:04d}.png"
                Image.fromarray(np.full((32, 32), index * 20, dtype=np.uint8)).save(image)
                if action == "Cranial":
                    coordinate -= achieved
                observations.append({
                    "observationIndex": index, "requestedAction": action,
                    "executedSuccessfully": True, "achievedDisplacementMm": achieved,
                    "signedActorRelativeMm": coordinate,
                    "frameIntegrityValid": True, "contactValidAfterExecution": True,
                    "workspaceValidAfterExecution": True,
                    "image": image.relative_to(root).as_posix(),
                    "imageSha256": sha256(image), "poseEpoch": index + 1,
                    "producerPoseEpoch": index + 1,
                    "poseAppliedUnityFrame": index * 3 + 1,
                    "producerUnityFrame": index * 3 + 2,
                    "captureUnityFrame": index * 3 + 2,
                    "producerScanSerial": index + 1, "activeAggregatorCount": 0,
                })
            validated = validate_actor_trace(root, {
                "initialActorRelativeMm": 3.0,
                "observations": observations,
            })
            self.assertAlmostEqual(validated[-1]["signedActorRelativeMm"], 0.0)

    def test_local_a_recess_a_motifs_are_detected(self) -> None:
        rows = synthetic_rows((-30.0, -15.0, 0.0, 15.0, 30.0))
        scores = motif_scores(rows)
        events, _ = detect_local_events(rows, scores)
        coordinates = [item["coordinateMm"] for item in events]
        for expected in (-30.0, -15.0, 0.0, 15.0, 30.0):
            self.assertTrue(any(abs(actual - expected) <= 1.5 for actual in coordinates))

    def test_duplicate_forward_reverse_events_are_consolidated(self) -> None:
        accepted, rejected = consolidate_reverse_consensus(
            [event(10.0), event(30.0)], [event(11.0), event(29.5)]
        )
        self.assertEqual(len(accepted), 2)
        self.assertEqual(len(rejected), 0)
        self.assertLess(accepted[0]["forwardReverseDisagreementMm"], 3.0)

    def test_missing_reverse_event_is_rejected(self) -> None:
        accepted, rejected = consolidate_reverse_consensus(
            [event(10.0), event(30.0)], [event(10.5)]
        )
        self.assertEqual(len(accepted), 1)
        self.assertTrue(any(item["reason"] == "NO_REVERSE_SUPPORT" for item in rejected))

    def test_exactly_seven_events_are_required_for_map(self) -> None:
        seven, _ = consolidate_reverse_consensus(
            [event(float(value)) for value in range(0, 70, 10)],
            [event(float(value) + 0.5) for value in range(0, 70, 10)],
        )
        accepted = build_relative_map(seven)
        self.assertEqual(accepted["overallMapStatus"], "ACCEPTED_BLIND_HYPOTHESIS")
        self.assertIsNotNone(accepted["predictedT7RelativeMm"])

    def test_eighth_false_positive_forces_abstention(self) -> None:
        eight, _ = consolidate_reverse_consensus(
            [event(float(value)) for value in range(0, 80, 10)],
            [event(float(value) + 0.5) for value in range(0, 80, 10)],
        )
        result = build_relative_map(eight)
        self.assertEqual(result["overallMapStatus"], "INCONCLUSIVE")
        self.assertIsNone(result["predictedT7RelativeMm"])
        self.assertTrue(all(not item["accepted"] for item in result["levels"]))

    def test_ordered_matching_does_not_reuse_reference(self) -> None:
        references = [
            {"level": "T2", "coordinateMm": 0.0, "toleranceMm": 3.0},
            {"level": "T3", "coordinateMm": 20.0, "toleranceMm": 3.0},
        ]
        rows = ordered_match([1.0, 18.0], references)
        self.assertTrue(all(item["matched"] for item in rows))

    def test_freeze_detects_modified_blind_output(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.py"
            source.write_text("print('frozen')\n", encoding="utf-8")
            for name in BLIND_OUTPUTS:
                (root / name).write_text(json.dumps({"name": name}), encoding="utf-8")
            freeze_outputs(root, source)
            verify_freeze(root, source)
            (root / BLIND_OUTPUTS[0]).write_text("changed", encoding="utf-8")
            with self.assertRaises(ValueError):
                verify_freeze(root, source)


if __name__ == "__main__":
    unittest.main()


