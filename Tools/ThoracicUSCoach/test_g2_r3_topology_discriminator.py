from __future__ import annotations

import unittest
import tempfile
from pathlib import Path

import numpy as np

from g2_r3_topology_discriminator import (
    MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
    apply_shadow_topology_gate,
    depth_aware_projected_coordinate,
    measure_interface_termination,
    pixel_to_beam_index,
)
from g2_r3_confirmation import (
    assert_actor_isolation,
    freeze_blind_outputs,
    verify_blind_outputs,
)


class TopologyDiscriminatorTests(unittest.TestCase):
    def test_beam_mapping_accounts_for_depth_varying_fan_width(self) -> None:
        shallow = pixel_to_beam_index(190.0, 80.0, 256, 252, 45.0, 50.2336, 50.0)
        deep = pixel_to_beam_index(190.0, 220.0, 256, 252, 45.0, 50.2336, 50.0)
        self.assertGreater(shallow, deep)

    def test_local_width_projection_diagnostic_differs_from_renderer_mapping(self) -> None:
        actor = {
            "scanTopWidthMm": 45.0,
            "scanDepthMm": 50.0,
            "renderedFootprintWidthMm": 50.2336,
        }
        detection = {
            "imageWidth": 256,
            "pixelX": 200.0,
            "depthPixelY": 100.0,
            "probeCoordinateMm": 0.0,
        }
        coordinate = depth_aware_projected_coordinate(detection, 1, actor)
        bottom_width_coordinate = (200.0 - 127.5) / 255.0 * 50.2336
        self.assertGreater(coordinate, 0.0)
        self.assertLess(coordinate, bottom_width_coordinate)

    def test_shadow_metrics_are_image_only(self) -> None:
        image = np.full((256, 256), 0.25, dtype=np.float64)
        image[145:, 100:111] = 0.0
        track = {
            "x": np.arange(18, 238),
            "pathY": np.full(220, 140.0),
            "ridgeScores": np.full(220, 0.5),
        }
        result = measure_interface_termination(image, track, 105.0)
        self.assertGreater(result["shadowOnsetContrast"], 0.9)
        self.assertGreater(result["distalDarkFraction"], 0.9)

    def test_insufficient_distal_field_is_ambiguous_not_deleted(self) -> None:
        candidates = [self._candidate(index, 80.0) for index in range(7)]
        candidates.insert(1, self._candidate(99, MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS - 1.0))
        result = apply_shadow_topology_gate({"candidates": candidates})
        self.assertEqual(len(result["accepted"]), 7)
        self.assertEqual(len(result["ambiguous"]), 1)
        self.assertEqual(len(result["rejected"]), 0)
        self.assertIsNotNone(result["relativeSpineMap"]["predictedT7RelativeMm"])

    def test_weak_prominence_is_not_an_acceptance_input(self) -> None:
        candidate = self._candidate(0, 60.0)
        candidate["meanProminencePixels"] = 0.01
        result = apply_shadow_topology_gate({"candidates": [candidate]})
        self.assertEqual(len(result["accepted"]), 1)

    def test_gate_preserves_renderer_mapped_actor_coordinate(self) -> None:
        candidate = self._candidate(2, 60.0)
        result = apply_shadow_topology_gate({"candidates": [candidate]})
        self.assertEqual(
            result["accepted"][0]["actorRelativeCoordinateMm"],
            candidate["actorRelativeCoordinateMm"],
        )
        self.assertEqual(
            result["accepted"][0]["uncertaintyIntervalMm"],
            candidate["uncertaintyIntervalMm"],
        )

    def test_actor_isolation_allows_inferred_sequence_but_rejects_world_pose(self) -> None:
        assert_actor_isolation({
            "levels": [{"inferredLevel": "T2"}],
            "predictedT7RelativeMm": 42.0,
        })
        with self.assertRaises(ValueError):
            assert_actor_isolation({"worldPose": [1.0, 2.0, 3.0]})

    def test_blind_freeze_detects_tampering(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / "event_candidates.json"
            target.write_text("{}\n", encoding="utf-8")
            freeze_blind_outputs(root, [target.name])
            self.assertTrue(verify_blind_outputs(root))
            target.write_text("{\"changed\": true}\n", encoding="utf-8")
            self.assertFalse(verify_blind_outputs(root))

    @staticmethod
    def _candidate(index: int, available: float) -> dict:
        coordinate = float(index * 20)
        return {
            "candidateId": f"E{index + 1:02d}",
            "actorRelativeCoordinateMm": coordinate,
            "uncertaintyIntervalMm": [coordinate - 0.5, coordinate + 0.5],
            "depthAwareCenterMm": coordinate + 0.1,
            "depthAwareSupportIntervalMm": [coordinate - 1.0, coordinate + 1.0],
            "supportingFrameIndices": [index, index + 100],
            "supportingImages": [f"frames/frame_{index:04d}.png"],
            "minimumDistalObservationPixels": available,
            "medianInterfaceDepthPixelY": 255.0 - available,
            "shadowOnsetContrastMedian": 0.7,
            "shadowTopologyVerifiable": available
            >= MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
        }


if __name__ == "__main__":
    unittest.main()
