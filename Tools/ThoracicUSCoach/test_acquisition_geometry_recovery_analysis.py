import unittest

from acquisition_geometry_recovery_analysis import (
    diagnostic_provenance_conclusion,
    provenance_metrics,
    select_nearest_primary,
)


class AcquisitionGeometryRecoveryAnalysisTests(unittest.TestCase):
    def test_nearest_selection_excludes_reverse_capture(self):
        actor = [
            {"observationIndex": 0, "phase": "caudal_outbound"},
            {"observationIndex": 1, "phase": "caudal_reverse"},
        ]
        evaluator = [
            {"observationIndex": 0, "evaluatorLongitudinalCoordinateMm": 3.0},
            {"observationIndex": 1, "evaluatorLongitudinalCoordinateMm": 2.9},
        ]
        selected_actor, _ = select_nearest_primary(actor, evaluator, 2.9)
        self.assertEqual(0, selected_actor["observationIndex"])

    def test_provenance_metrics_keep_combined_skeleton_separate(self):
        beams = [
            {"firstVisibleObject": "LF T7", "firstShadowObject": "Skeleton"},
            {"firstVisibleObject": "Skeleton", "firstShadowObject": "Skeleton"},
            {"firstVisibleObject": "Right Trapezius", "firstShadowObject": "Skeleton"},
            {"firstVisibleObject": "LF T7", "firstShadowObject": "NONE"},
        ]
        result = provenance_metrics(beams, "LF T7")
        self.assertEqual(0.5, result["expectedFirstVisibleBeamFraction"])
        self.assertEqual(0.25, result["combinedSkeletonFirstVisibleFraction"])
        self.assertEqual(0.75, result["combinedSkeletonShadowFraction"])

    def test_diagnostic_conclusion_requires_strength_and_persistence(self):
        self.assertEqual("NOT_OBSERVED_IN_PROVENANCE", diagnostic_provenance_conclusion(0.0, 0))
        self.assertEqual("WEAK_OR_SPARSE_PROVENANCE_INTERSECTION", diagnostic_provenance_conclusion(0.06, 1))
        self.assertEqual("WEAK_OR_SPARSE_PROVENANCE_INTERSECTION", diagnostic_provenance_conclusion(0.04, 3))
        self.assertEqual("SUPPORTED_PROVENANCE_INTERSECTION_ONLY", diagnostic_provenance_conclusion(0.05, 2))


if __name__ == "__main__":
    unittest.main()
