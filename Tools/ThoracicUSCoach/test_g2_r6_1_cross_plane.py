import unittest

from g2_r6_1_cross_plane import (
    ALTERNATIVE_YAW_DEG,
    build_protocol,
    projected_coordinate_from_observation,
)
from test_g2_r6_cross_plane import fixtures


class G2R61CrossPlaneTests(unittest.TestCase):
    def test_protocol_freezes_one_new_plane_and_preserves_actor_selection(self):
        source, decisions = fixtures()
        protocol = build_protocol(source, decisions, "session")
        self.assertEqual(protocol["mode"],
                         "ACTOR_GUIDED_REPAIRED_CROSS_PLANE_DISCRIMINATION")
        self.assertEqual(ALTERNATIVE_YAW_DEG, 65.0)
        for region in protocol["regions"]:
            self.assertEqual([item["surfaceYawDeg"] for item in region["variants"]],
                             [90.0, 65.0])
        self.assertNotIn("T2", str(protocol))

    def test_projection_uses_signed_scan_axis_and_rendered_footprint(self):
        observation = {
            "signedActorRelativeMm": 10.0,
            "renderedFootprintWidthMm": 40.0,
            "signedScanWidthAgainstTravelAxis": 0.5,
            "scanWidthAgainstTravelAxis": 0.5,
        }
        self.assertAlmostEqual(
            projected_coordinate_from_observation(observation, 201, 200), 0.0
        )
        observation["signedScanWidthAgainstTravelAxis"] = -0.5
        self.assertAlmostEqual(
            projected_coordinate_from_observation(observation, 201, 200), 20.0
        )

    def test_view_quality_is_region_aware_and_persist_first(self):
        source, decisions = fixtures()
        protocol = build_protocol(source, decisions, "session")
        quality = protocol["viewQuality"]
        self.assertIn("persist first", quality["decisionRule"])
        self.assertIn("central ROI MAE", quality["decisionRule"])
        self.assertIn("local gradient MAE", quality["decisionRule"])


if __name__ == "__main__":
    unittest.main()
