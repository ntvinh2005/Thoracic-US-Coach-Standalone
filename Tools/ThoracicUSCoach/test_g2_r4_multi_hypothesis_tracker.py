import inspect
import tempfile
import unittest
from pathlib import Path

import numpy as np

import g2_r4_multi_hypothesis_tracker as r4
from g2_r2_interface_tracker import track_bright_interface


def cluster(
    coordinate, classification="AMBIGUOUS_INSUFFICIENT_EVIDENCE", frames=3,
    shadow=1.0, outbound=0.0, repeat=0.2, reasons=None,
):
    return {
        "centerMm": coordinate,
        "supportIntervalMm": [coordinate - 0.5, coordinate + 0.5],
        "distinctFrameCount": frames,
        "supportingFrameIndices": list(range(frames)),
        "supportingImages": [f"frame_{index}.png" for index in range(frames)],
        "actualShadowSupportFraction": shadow,
        "distalFieldVerifiableFraction": shadow,
        "outboundCenterMm": outbound,
        "repeatCenterMm": repeat,
        "classification": classification,
        "accepted": classification == "SUPPORTED_ANATOMICAL_EVENT",
        "rejectionReasons": reasons or [],
    }


class G2R4MultiHypothesisTests(unittest.TestCase):
    def test_primary_path_is_immutable_baseline(self):
        image = np.zeros((256, 256), dtype=np.float64)
        for x in range(20, 236):
            y = int(180 + 15 * np.sin(x / 25.0))
            image[max(0, y - 1):y + 2, x] = 1.0
        baseline = track_bright_interface(image)
        tracks = r4.track_multiple_interfaces(image)
        self.assertEqual(tracks[0]["trackId"], "H0_BASELINE")
        np.testing.assert_allclose(tracks[0]["pathY"], baseline["pathY"])

    def test_near_identical_paths_are_not_duplicated(self):
        image = np.zeros((256, 256), dtype=np.float64)
        image[180:183, 20:236] = 1.0
        tracks = r4.track_multiple_interfaces(image)
        for left_index, left in enumerate(tracks):
            for right in tracks[left_index + 1:]:
                separation = np.abs(np.asarray(left["pathY"]) - np.asarray(right["pathY"]))
                self.assertGreaterEqual(float(np.median(separation)),
                                        r4.MINIMUM_MEDIAN_TRACK_SEPARATION_PIXELS)

    def test_actual_shadow_requires_pixels_not_only_available_field(self):
        image = np.full((256, 256), 0.2, dtype=np.float64)
        image[178:183, :] = 1.0
        image[188:225, 120:137] = 0.0
        x = np.arange(18, 238)
        track = {
            "x": x, "pathY": np.full(len(x), 180.0),
            "ridgeScores": np.ones(len(x)),
        }
        result = r4.measure_actual_shadow(image, track, 128.0)
        self.assertTrue(result["actualShadowSupported"])
        bright_below = image.copy()
        bright_below[188:225, 120:137] = 0.2
        result = r4.measure_actual_shadow(bright_below, track, 128.0)
        self.assertFalse(result["actualShadowSupported"])

    def test_distal_boundary_cannot_claim_shadow(self):
        image = np.full((256, 256), 0.2, dtype=np.float64)
        image[229:232, :] = 1.0
        x = np.arange(18, 238)
        track = {"x": x, "pathY": np.full(len(x), 230.0),
                 "ridgeScores": np.ones(len(x))}
        result = r4.measure_actual_shadow(image, track, 128.0)
        self.assertLess(result["minimumDistalObservationPixels"], 45.0)
        self.assertFalse(result["actualShadowSupported"])

    def test_frame_capped_density_prevents_hypothesis_multiplication(self):
        grid = np.arange(-5.0, 5.5, 0.5)
        base = {
            "observationIndex": 1, "projectedCoordinateMm": 0.0,
            "prominencePixels": 5.0, "trackMeanRidgeScore": 1.0,
        }
        single = r4._frame_capped_density([base], grid)
        triplicate = r4._frame_capped_density([
            {**base, "trackId": name} for name in ("H0", "H1", "H2")
        ], grid)
        np.testing.assert_allclose(single, triplicate)

    def test_reacquisition_uses_actor_evidence_and_limits_two_windows(self):
        clusters = [
            cluster(-80.0, frames=3, reasons=["INSUFFICIENT_DISTINCT_FRAMES"]),
            cluster(-62.0, frames=2, repeat=None,
                    reasons=["INSUFFICIENT_DISTINCT_FRAMES", "MISSING_DIRECTIONAL_SUPPORT"]),
            cluster(-72.0, frames=30, shadow=0.0,
                    reasons=["INSUFFICIENT_ACTUAL_SHADOW_SUPPORT"]),
            cluster(20.0, frames=1, reasons=["INSUFFICIENT_DISTINCT_FRAMES"]),
        ]
        windows = r4.select_active_reacquisition_windows(clusters, (-90.0, 80.0))
        self.assertEqual(len(windows), 2)
        self.assertEqual(
            {item["actorRelativeCenterMm"] for item in windows},
            {-80.0, -62.0},
        )
        self.assertNotIn(-72.0, [item["actorRelativeCenterMm"] for item in windows])
        self.assertTrue(all(item["coordinateSource"] ==
                            "ACTOR_RELATIVE_IMAGE_EVENT_HYPOTHESIS" for item in windows))

    def test_map_abstains_when_consequential_extra_is_ambiguous(self):
        accepted = [
            cluster(float(index * 20), "SUPPORTED_ANATOMICAL_EVENT", frames=8)
            for index in range(7)
        ]
        ambiguous = cluster(10.0, frames=4, reasons=["INSUFFICIENT_DISTINCT_FRAMES"])
        result = r4.build_ambiguity_aware_map(accepted + [ambiguous])
        self.assertEqual(result["overallMapStatus"], "INCONCLUSIVE")
        self.assertIsNone(result["predictedT7RelativeMm"])

    def test_map_accepts_unique_seven_only(self):
        accepted = [
            cluster(float(index * 20), "SUPPORTED_ANATOMICAL_EVENT", frames=8)
            for index in range(7)
        ]
        result = r4.build_ambiguity_aware_map(accepted)
        self.assertEqual(result["overallMapStatus"], "ACCEPTED_UNIQUE_SEQUENCE")
        self.assertEqual(result["predictedT7RelativeMm"], 100.0)

    def test_actor_module_does_not_import_evaluator_reference_mapping(self):
        source = inspect.getsource(r4)
        self.assertNotIn("reference_coordinates", source)
        self.assertNotIn("bone_structure_reference_map", source)
        self.assertNotIn("trajectory_privileged", source)

    def test_revisit_window_is_clamped_to_actor_coverage(self):
        clusters = [cluster(-89.0, frames=5, reasons=["INSUFFICIENT_DISTINCT_FRAMES"])]
        windows = r4.select_active_reacquisition_windows(clusters, (-90.0, 80.0))
        self.assertGreaterEqual(windows[0]["windowMm"][0], -90.0)
        self.assertLessEqual(windows[0]["windowMm"][1], 80.0)


if __name__ == "__main__":
    unittest.main()
