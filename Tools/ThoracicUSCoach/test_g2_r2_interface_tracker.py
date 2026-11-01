from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import numpy as np

from g2_r2_continuous_detector import build_relative_map
from g2_r2_interface_tracker import (
    FREEZE_FILES,
    cluster_for_sign,
    configured_footprint_width,
    detect_interface_valleys,
    freeze,
    projected_coordinate,
    select_axis_sign,
    track_bright_interface,
    verify_freeze,
)


def synthetic_v_interface() -> np.ndarray:
    height = 256
    width = 256
    image = np.full((height, width), 0.04, dtype=np.float64)
    x = np.arange(width)
    path = 145.0 + 24.0 * np.exp(-0.5 * ((x - 128.0) / 18.0) ** 2)
    for column, row in enumerate(path.astype(int)):
        image[max(0, row - 2):min(height, row + 3), column] = 1.0
    return image


def projected_support(structure_mm: float, start_index: int) -> list[dict]:
    detections = []
    probes = (-10.0, -7.5, -5.0, -2.5, 2.5, 5.0, 7.5, 10.0)
    for offset, probe_offset in enumerate(probes):
        probe = structure_mm + probe_offset
        pixel_x = 127.5 + (structure_mm - probe) / 40.0 * 255.0
        detections.append({
            "observationIndex": start_index + offset,
            "phase": "caudal_outbound" if offset < 4 else "caudal_return",
            "probeCoordinateMm": probe,
            "image": f"frames/frame_{start_index + offset:04d}.png",
            "imageSha256": "0" * 64,
            "imageWidth": 256,
            "pixelX": pixel_x,
            "depthPixelY": 170.0,
            "prominencePixels": 12.0,
            "trackMeanRidgeScore": 0.8,
        })
    return detections


class InterfaceTrackerTests(unittest.TestCase):
    def test_actor_acquisition_configuration_controls_footprint(self) -> None:
        self.assertAlmostEqual(configured_footprint_width({
            "renderedFootprintWidthMm": 50.2335968,
        }), 50.2335968)
        with self.assertRaises(ValueError):
            configured_footprint_width({"renderedFootprintWidthMm": 75.0})

    def test_continuity_tracker_recovers_coherent_v_interface(self) -> None:
        track = track_bright_interface(synthetic_v_interface())
        valleys = detect_interface_valleys(track)
        self.assertTrue(track["valid"])
        self.assertLessEqual(track["maximumPathJumpPixels"], 4.0)
        self.assertTrue(any(abs(item["pixelX"] - 128.0) <= 5.0 for item in valleys))

    def test_footprint_projection_recovers_structure_coordinate(self) -> None:
        detection = projected_support(22.0, 0)[0]
        self.assertAlmostEqual(projected_coordinate(detection, 1, 40.0), 22.0, places=6)

    def test_blind_axis_sign_prefers_compact_repeat_supported_clusters(self) -> None:
        detections = []
        for index, structure in enumerate((-36.0, -12.0, 18.0)):
            detections.extend(projected_support(structure, index * 20))
        chosen, alternatives = select_axis_sign(detections, 40.0)
        self.assertEqual(chosen["sign"], 1)
        accepted = [item for item in chosen["clusters"] if item["accepted"]]
        self.assertEqual(len(accepted), 3)
        self.assertGreater(chosen["qualityScore"], alternatives[1]["qualityScore"])

    def test_duplicate_observations_consolidate_to_one_spatial_event(self) -> None:
        result = cluster_for_sign(projected_support(11.0, 0), 1, 40.0)
        accepted = [item for item in result["clusters"] if item["accepted"]]
        self.assertEqual(len(accepted), 1)
        self.assertAlmostEqual(accepted[0]["centerMm"], 11.0, places=6)
        self.assertLessEqual(accepted[0]["forwardReverseDisagreementMm"], 3.0)

    def test_mixed_interface_depth_populations_are_rejected(self) -> None:
        detections = projected_support(11.0, 0)
        for index, detection in enumerate(detections):
            detection["depthPixelY"] = 140.0 if index < 4 else 216.0
        result = cluster_for_sign(detections, 1, 40.0)
        self.assertEqual(result["acceptedCount"], 0)
        self.assertTrue(any(
            "MIXED_INTERFACE_DEPTH_POPULATIONS" in item["rejectionReasons"]
            for item in result["clusters"]
        ))

    def test_eighth_supported_event_forces_map_abstention(self) -> None:
        candidates = []
        for index in range(8):
            coordinate = float(index * 20)
            candidates.append({
                "actorRelativeCoordinateMm": coordinate,
                "uncertaintyIntervalMm": [coordinate - 1.0, coordinate + 1.0],
                "supportingFrameIndices": [index, index + 100],
                "eventType": "CONTINUITY_TRACKED_BRIGHT_INTERFACE_RECESS_VALLEY",
            })
        result = build_relative_map(candidates)
        self.assertEqual(result["overallMapStatus"], "INCONCLUSIVE")
        self.assertIsNone(result["predictedT7RelativeMm"])

    def test_freeze_detects_source_or_output_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source_root = root / "input"
            output_root = root / "output"
            source_root.mkdir()
            output_root.mkdir()
            source = root / "tracker.py"
            source.write_text("# frozen\n", encoding="utf-8")
            (source_root / "trajectory_actor.json").write_text("{}\n", encoding="utf-8")
            for name in FREEZE_FILES:
                (output_root / name).write_text(f"{name}\n", encoding="utf-8")
            freeze(output_root, source_root, source)
            verify_freeze(output_root, source_root, source)
            (output_root / FREEZE_FILES[0]).write_text("changed\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                verify_freeze(output_root, source_root, source)


if __name__ == "__main__":
    unittest.main()
