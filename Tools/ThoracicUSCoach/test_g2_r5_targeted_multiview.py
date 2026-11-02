import tempfile
import unittest
from pathlib import Path

import g2_r5_targeted_multiview as r5


def ambiguous(center, frames, shadow, outbound=True, repeat=True, width=1.0):
    reasons = []
    if frames < 6:
        reasons.append("INSUFFICIENT_DISTINCT_FRAMES")
    if not outbound or not repeat:
        reasons.append("MISSING_DIRECTIONAL_SUPPORT")
    if shadow < 0.5:
        reasons.append("INSUFFICIENT_ACTUAL_SHADOW_SUPPORT")
    return {
        "centerMm": center,
        "supportIntervalMm": [center - width / 2, center + width / 2],
        "distinctFrameCount": frames,
        "supportingFrameIndices": list(range(frames)),
        "supportingImages": [f"frame_{index}.png" for index in range(frames)],
        "actualShadowSupportFraction": shadow,
        "outboundCenterMm": center if outbound else None,
        "repeatCenterMm": center if repeat else None,
        "classification": "AMBIGUOUS_INSUFFICIENT_EVIDENCE",
        "rejectionReasons": reasons,
    }


class G2R5TargetedMultiviewTests(unittest.TestCase):
    def test_selects_two_weak_and_one_stable_unresolved(self):
        clusters = [
            ambiguous(-82, 3, 1.0), ambiguous(-63, 2, 1.0, repeat=False),
            ambiguous(-72, 30, 0.0, width=5), ambiguous(20, 1, 1.0),
        ]
        selected = r5.select_revisit_regions(clusters, (-89, 90))
        self.assertEqual(len(selected), 3)
        self.assertEqual(sum(item["category"] == "WEAK_EVENT_HYPOTHESIS"
                             for item in selected), 2)
        self.assertEqual(sum(item["category"] == "STABLE_UNRESOLVED_INTERFACE"
                             for item in selected), 1)

    def test_selector_is_not_coordinate_specific(self):
        clusters = [
            ambiguous(12, 3, 1.0), ambiguous(35, 2, 1.0, repeat=False),
            ambiguous(24, 30, 0.0, width=5),
        ]
        centers = {item["actorRelativeCenterMm"] for item in
                   r5.select_revisit_regions(clusters, (0, 50))}
        self.assertEqual(centers, {12.0, 24.0, 35.0})

    def test_window_expands_and_clamps_to_actor_coverage(self):
        selected = r5.select_revisit_regions([
            ambiguous(-88, 3, 1.0), ambiguous(-63, 2, 1.0, repeat=False),
            ambiguous(-72, 30, 0.0, width=5),
        ], (-89, 90))
        edge = next(item for item in selected if item["actorRelativeCenterMm"] == -88)
        self.assertEqual(edge["windowMm"][0], -89)
        self.assertTrue(edge["windowClampedByWorkspace"])
        self.assertGreater(edge["windowMm"][1] - edge["windowMm"][0], 8)

    def test_depth_sixty_is_only_assigned_to_stable_ambiguity(self):
        selected = r5.select_revisit_regions([
            ambiguous(-82, 3, 1.0), ambiguous(-63, 2, 1.0, repeat=False),
            ambiguous(-72, 30, 0.0),
        ], (-89, 90))
        for region in selected:
            depths = {item["scanDepthMm"] for item in region["variants"]}
            if region["category"] == "STABLE_UNRESOLVED_INTERFACE":
                self.assertEqual(depths, {50, 60})
            else:
                self.assertEqual(depths, {50})

    def test_protocol_stays_within_frame_budget(self):
        protocol = r5.build_protocol([
            ambiguous(-82, 3, 1.0), ambiguous(-63, 2, 1.0, repeat=False),
            ambiguous(-72, 30, 0.0, width=5),
        ], (-89, 90), "session")
        self.assertLessEqual(protocol["estimatedFreshFrames"], 240)

    def test_duplicate_hypotheses_in_one_frame_are_capped(self):
        detections = [
            {"variantId": "A", "direction": "FORWARD", "probeCoordinateMm": 1.01,
             "prominencePixels": value, "actualShadowSupported": True,
             "observationIndex": index}
            for index, value in enumerate((3.0, 8.0, 5.0))
        ]
        support = r5._independent_support(detections)
        self.assertEqual(len(support), 1)
        self.assertEqual(support[0]["prominencePixels"], 8.0)

    def test_duplicate_image_cannot_add_independent_support(self):
        detections = [
            {"variantId": "A", "direction": "FORWARD", "probeCoordinateMm": 1.0,
             "prominencePixels": 6.0, "actualShadowSupported": True,
             "observationIndex": 1},
        ]
        support = r5._independent_support(detections + [dict(detections[0])])
        self.assertEqual(len(support), 1)

    def test_fusion_requires_distinct_positions_and_both_directions(self):
        original = ambiguous(-63, 2, 1.0, repeat=False)
        one_direction = [
            {"variantId": "A", "direction": "FORWARD", "probeCoordinateMm": value,
             "projectedCoordinateMm": -63 + value / 10, "prominencePixels": 5.0,
             "actualShadowSupported": True, "observationIndex": index}
            for index, value in enumerate((0.0, 1.0, 2.0))
        ]
        self.assertEqual(r5.fuse_candidate(original, one_direction)["classification"],
                         "AMBIGUOUS_UNRESOLVED")
        one_direction.append({
            "variantId": "A", "direction": "REVERSE", "probeCoordinateMm": 1.5,
            "projectedCoordinateMm": -62.9, "prominencePixels": 5.0,
            "actualShadowSupported": True, "observationIndex": 4,
        })
        self.assertEqual(r5.fuse_candidate(original, one_direction)["classification"],
                         "SUPPORTED_RECESS_EVENT")

    def test_stable_deep_interface_is_not_autoaccepted_by_shadow(self):
        original = ambiguous(-72, 30, 0.0)
        detections = [
            {"variantId": "D60", "direction": direction, "probeCoordinateMm": value,
             "projectedCoordinateMm": -72.0, "prominencePixels": 5.0,
             "actualShadowSupported": True, "observationIndex": index}
            for index, (direction, value) in enumerate((("FORWARD", 0), ("REVERSE", 1)))
        ]
        self.assertEqual(r5.fuse_candidate(original, detections)["classification"],
                         "AMBIGUOUS_UNRESOLVED")

    def test_depth_sixty_can_reject_stable_nonshadow_interface(self):
        original = ambiguous(-72, 30, 0.0)
        detections = []
        for index, (direction, value) in enumerate((
            ("FORWARD", 0.0), ("FORWARD", 1.0),
            ("REVERSE", 0.5), ("REVERSE", 1.5),
        )):
            detections.append({
                "variantId": "D60", "direction": direction,
                "probeCoordinateMm": value, "projectedCoordinateMm": -72.0,
                "prominencePixels": 5.0, "actualShadowSupported": False,
                "minimumDistalObservationPixels": 60.0, "scanDepthMm": 60,
                "observationIndex": index,
            })
        self.assertEqual(
            r5.fuse_candidate(original, detections)["classification"],
            "REJECTED_SECONDARY_OR_DUPLICATE_INTERFACE",
        )

    def test_fused_map_does_not_fill_missing_levels(self):
        uncertainty = {
            "accepted": [
                {**ambiguous(value, 8, 1.0),
                 "classification": "SUPPORTED_ANATOMICAL_EVENT", "accepted": True}
                for value in (-40, -20, 0, 20, 40)
            ],
            "ambiguous": [ambiguous(-80, 3, 1.0), ambiguous(-60, 2, 1.0),
                          ambiguous(-70, 30, 0.0)],
        }
        fused = [
            {"originalCenterMm": -80.0, "updatedCenterMm": -80.0,
             "updatedSupportIntervalMm": [-81.0, -79.0],
             "classification": "SUPPORTED_RECESS_EVENT"},
            {"originalCenterMm": -60.0, "updatedCenterMm": -60.0,
             "updatedSupportIntervalMm": [-61.0, -59.0],
             "classification": "SUPPORTED_RECESS_EVENT"},
            {"originalCenterMm": -70.0, "updatedCenterMm": -70.0,
             "updatedSupportIntervalMm": [-71.0, -69.0],
             "classification": "AMBIGUOUS_UNRESOLVED"},
        ]
        result, _ = r5.build_fused_relative_map(uncertainty, fused)
        self.assertEqual(result["acceptedEventCount"], 7)
        self.assertIsNone(result["predictedT7RelativeMm"])

    def test_map_abstains_with_consequential_ambiguity(self):
        accepted = [{
            **ambiguous(index * 20, 8, 1.0),
            "classification": "SUPPORTED_ANATOMICAL_EVENT", "accepted": True,
        } for index in range(7)]
        extra = ambiguous(10, 20, 0.0)
        result = r5.build_ambiguity_aware_map(accepted + [extra])
        self.assertIsNone(result["predictedT7RelativeMm"])

    def test_freeze_manifest_detects_content_change(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "a.json"
            output = Path(folder) / "freeze.sha256"
            path.write_text("{}", encoding="utf-8")
            r5.freeze_manifest([path], output)
            before = output.read_text(encoding="utf-8")
            path.write_text("{\"changed\":true}", encoding="utf-8")
            r5.freeze_manifest([path], output)
            self.assertNotEqual(before, output.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
