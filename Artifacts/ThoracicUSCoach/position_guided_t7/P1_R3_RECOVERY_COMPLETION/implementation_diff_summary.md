# P1-R3 implementation diff summary

## New source

- `ThoracicUSCoachController.PositionGuidedT7R3.cs`: isolated coarse/fine controller, physical sensitivity route, cumulative registry, frozen calibration loading, freshness/provenance and evaluator-only traces.
- `ThoracicPositionGuidedT7R3Batch.cs`: CLI entry points for the next bounded development or confirmation episode.
- `PositionGuidedT7R3Tests.cs`: decision, tolerance, progress-floor, budget, restoration, and no-runtime-target-query tests.
- `p1_r3_recovery_analysis.py`: development SSIM/pose analysis, freeze hashing, confirmation classification, and report generation.
- `test_p1_r3_recovery_analysis.py`: focused offline decision/freeze tests.

## Explicitly unchanged

- Active2D physical movement behavior.
- DirectFreshCapture behavior.
- Scene, renderer, anatomy, skin collider, and standard workspace.
- Original block SSIM implementation and threshold.
- Historical P1/P1-R1/P1-R2 artifacts and G2-G4 verdicts.

## Local working-tree protection

No unrelated staged or untracked file was cleaned, reset, discarded, or incorporated into the P1-R3 implementation.
