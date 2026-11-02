import unittest

from g2_r6_cross_plane import (
    MAXIMUM_FRESH_FRAMES,
    build_protocol,
    classify,
    estimate_frame_budget,
    select_regions,
)


def fixtures():
    protocol = {
        "safeActorCoverageMm": [-90.0, 80.0],
        "regions": [
            {"regionId": "a", "category": "WEAK_EVENT_HYPOTHESIS"},
            {"regionId": "b", "category": "STABLE_UNRESOLVED_INTERFACE"},
            {"regionId": "c", "category": "WEAK_EVENT_HYPOTHESIS"},
        ],
    }
    decisions = {"regions": [
        {"regionId": "a", "classification": "AMBIGUOUS_UNRESOLVED",
         "updatedCenterMm": -80.0, "updatedSupportIntervalMm": [-81.0, -79.0],
         "supportingObservations": [1, 2], "newIndependentSupportCount": 2,
         "newActualShadowSupportFraction": 0.2},
        {"regionId": "b", "classification": "AMBIGUOUS_UNRESOLVED",
         "updatedCenterMm": -70.0, "updatedSupportIntervalMm": [-73.0, -68.0],
         "supportingObservations": list(range(20)), "newIndependentSupportCount": 20,
         "newActualShadowSupportFraction": 0.4},
        {"regionId": "c", "classification": "SUPPORTED_RECESS_EVENT",
         "updatedCenterMm": -60.0, "updatedSupportIntervalMm": [-61.0, -59.0],
         "supportingObservations": list(range(10)), "newIndependentSupportCount": 10,
         "newActualShadowSupportFraction": 1.0},
    ]}
    return protocol, decisions


def summary(role, signature):
    adequate = {"adequate": signature is not None}
    return {"regionId": role, "role": role, "baseline": adequate,
            "oblique": adequate, "crossPlaneTopologySignature": signature}


class G2R6CrossPlaneTests(unittest.TestCase):
    def test_selects_roles_without_anatomical_labels(self):
        protocol, decisions = fixtures()
        regions = select_regions(protocol, decisions)
        self.assertEqual([item["role"] for item in regions], [
            "UNRESOLVED_WEAK_EVENT", "STABLE_SECONDARY_HYPOTHESIS",
            "SUPPORTED_EVENT_CONTROL",
        ])
        self.assertNotIn("T2", str(regions))

    def test_protocol_is_bounded_and_uses_one_oblique_plane(self):
        protocol, decisions = fixtures()
        result = build_protocol(protocol, decisions, "out")
        self.assertLessEqual(estimate_frame_budget(result["regions"]), MAXIMUM_FRESH_FRAMES)
        for region in result["regions"]:
            self.assertEqual([item["surfaceYawDeg"] for item in region["variants"]], [90.0, 60.0])
            self.assertEqual({item["scanDepthMm"] for item in region["variants"]}, {60})

    def test_coordinate_values_do_not_change_selection_roles(self):
        protocol, decisions = fixtures()
        original = [item["role"] for item in select_regions(protocol, decisions)]
        for item in decisions["regions"]:
            item["updatedCenterMm"] += 23.0
            item["updatedSupportIntervalMm"] = [value + 23.0 for value in item["updatedSupportIntervalMm"]]
        shifted = [item["role"] for item in select_regions(protocol, decisions)]
        self.assertEqual(original, shifted)

    def test_supported_discriminator_requires_control_similarity_and_secondary_separation(self):
        result = classify([
            summary("UNRESOLVED_WEAK_EVENT", [0.1, 0.1, 0.0]),
            summary("STABLE_SECONDARY_HYPOTHESIS", [0.9, 0.9, 0.9]),
            summary("SUPPORTED_EVENT_CONTROL", [0.0, 0.0, 0.0]),
        ])
        self.assertEqual(result["scientificVerdict"], "DISCRIMINATING_FEATURE_SUPPORTED")

    def test_close_secondary_is_not_forced_rejected(self):
        result = classify([
            summary("UNRESOLVED_WEAK_EVENT", [0.1, 0.1, 0.0]),
            summary("STABLE_SECONDARY_HYPOTHESIS", [0.2, 0.1, 0.1]),
            summary("SUPPORTED_EVENT_CONTROL", [0.0, 0.0, 0.0]),
        ])
        self.assertEqual(result["scientificVerdict"], "DISCRIMINATION_NOT_SUPPORTED")
        self.assertTrue(all(item["classification"] == "AMBIGUOUS_INSUFFICIENT_IMAGE_EVIDENCE"
                            for item in result["decisions"]))

    def test_missing_view_is_inconclusive_acquisition(self):
        result = classify([
            summary("UNRESOLVED_WEAK_EVENT", None),
            summary("STABLE_SECONDARY_HYPOTHESIS", [0.8, 0.8, 0.8]),
            summary("SUPPORTED_EVENT_CONTROL", [0.0, 0.0, 0.0]),
        ])
        self.assertEqual(result["scientificVerdict"], "INCONCLUSIVE_ACQUISITION")


if __name__ == "__main__":
    unittest.main()
