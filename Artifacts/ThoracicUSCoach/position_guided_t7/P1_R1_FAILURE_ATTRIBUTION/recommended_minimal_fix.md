# Recommended minimal fix — not implemented

## Primary correction

Add a two-stage position controller to the isolated `CALIBRATED_POSITION_GUIDED_BASELINE` only:

1. Preserve the current 3 mm coarse actions until the remaining longitudinal error first enters the existing 3 mm tolerance.
2. Do not declare STOP there. Switch to the existing Active2D physical movement primitive with a fixed 0.5 mm diagnostic step (the current declared minimum) and continue pose feedback until longitudinal and lateral errors are each no greater than 0.5 mm.
3. Keep the locked SSIM threshold `0.971000` as confirmation only; do not use evaluator score or T7 marker queries during navigation.

The last observed residual was `2.628` mm. Five 0.5 mm movements would nominally leave about 0.13 mm longitudinal residual, but this is a planning estimate, not an executed result.

## Exact files for a separately approved implementation

- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs`: add the bounded fine stage, per-action step logging, a 0.5 mm final pose tolerance, and new isolated P1-R2 output roots.
- `Assets/ThoracicUSCoach/Tests/Editor/PositionGuidedT7Tests.cs`: test no premature STOP at 2.628 mm, coarse-to-fine transition, bounded convergence/oscillation handling, and unchanged safety rejection.
- `Assets/ThoracicUSCoach/Editor/ThoracicPositionGuidedT7Batch.cs`: only the minimal run-id/output selection needed to preserve P1 and record three predeclared P1-R2 pilots separately.

No change is recommended to Active2D, DirectFreshCapture, the scene, renderer, SSIM implementation, threshold, or offline descriptor.

## Expected impact

- Expected position effect: reduce the final contact-point residual from 2.660 mm toward no more than roughly 0.5–0.7 mm while retaining physical surface following.
- Image effect: unknown until captured. The audit supports pose sensitivity but has `INSUFFICIENT_POSE_SENSITIVITY_DATA` for the exact sub-step SSIM curve.

## Safety constraints that remain unchanged

Rigid contact, surface projection, workspace enforcement, rollback, teleport rejection, normal/yaw alignment, movement budget, pose epoch, producer frame/serial freshness, provenance isolation, and no runtime target-marker query.

## Required fresh evidence

Run **three** predeclared yaw-0 pilots after compilation and all existing safety/freshness tests pass. Three runs are a smoke check, not a reliability claim. A successful correction requires all three to reach the fine pose tolerance, produce fresh associated images, retain zero unhandled safety failures, and meet the unchanged SSIM threshold.

## Stopping conditions

- Stop immediately on any rejected/unsafe fine movement, contact/workspace failure, stale frame, pose/provenance mismatch, or budget exhaustion.
- If the first physically valid run reaches the fine pose tolerance but still fails locked SSIM, stop the campaign: H1 alone is insufficient, and repeated runs must not be used to search for a favorable image.
- Do not weaken the threshold or add visual servoing inside this correction.
