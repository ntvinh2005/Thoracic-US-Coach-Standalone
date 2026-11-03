import unittest

from ground_truth_free_evaluator import reference_coordinates_from_executed_path


class GroundTruthFreeEvaluatorTests(unittest.TestCase):
    def test_reference_projection_does_not_extrapolate_uncovered_anatomy(self):
        trajectory = {"observations": [
            {"contactPoint": {"z": -10.0}, "evaluatorLongitudinalCoordinateMm": 10.0},
            {"contactPoint": {"z": 0.0}, "evaluatorLongitudinalCoordinateMm": 0.0},
            {"contactPoint": {"z": 10.0}, "evaluatorLongitudinalCoordinateMm": -10.0},
        ]}
        anatomy = {"validatedAnatomyDerivedLandmarks": [
            {"level": "T2", "validatedPosteriorRecessCoordinateZMm": 12.0},
            {"level": "T3", "validatedPosteriorRecessCoordinateZMm": 5.0},
        ]}
        references = reference_coordinates_from_executed_path(trajectory, anatomy)
        self.assertFalse(references[0]["insideExecutedCoverage"])
        self.assertIsNone(references[0]["referenceHomeRelativeMm"])
        self.assertTrue(references[1]["insideExecutedCoverage"])
        self.assertAlmostEqual(references[1]["referenceHomeRelativeMm"], -5.0)


if __name__ == "__main__":
    unittest.main()
