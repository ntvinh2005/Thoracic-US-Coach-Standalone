from __future__ import annotations

import unittest

from sweep_count_event_semantics import cluster_cross_yaw_events


class SweepCountEventSemanticsTests(unittest.TestCase):
    def test_cross_yaw_cluster_requires_observed_occurrences_not_expected_count(self) -> None:
        clusters = cluster_cross_yaw_events([[10, 30], [11, 30], [9]], tolerance_frames=2)

        self.assertEqual(clusters[0]["yawSupport"], 3)
        self.assertEqual(clusters[0]["repeatability"], "SUPPORTED")
        self.assertEqual(clusters[1]["yawSupport"], 2)
        self.assertEqual(clusters[1]["repeatability"], "AMBIGUOUS")

    def test_cross_yaw_cluster_does_not_merge_distant_events(self) -> None:
        clusters = cluster_cross_yaw_events([[5], [12], [20]], tolerance_frames=3)

        self.assertEqual(len(clusters), 3)
        self.assertTrue(all(item["yawSupport"] == 1 for item in clusters))


if __name__ == "__main__":
    unittest.main()
