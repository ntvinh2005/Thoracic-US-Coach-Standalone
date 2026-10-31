# P1-R2 Precise Position-Guided T7 Reacquisition

## IMPLEMENTATION

The isolated calibrated controller now uses measured-pose feedback with a 3 mm coarse stage and a bounded 0.5 mm fine stage. Existing Active2D movement, contact, workspace, rollback, progress, yaw, and fresh-capture contracts remain active. Standard Active2D, the scene, anatomy, renderer, and the locked SSIM threshold were not changed.

## VERIFICATION

See `preflight_results.json` and the Unity/Python test outputs in this directory. The physical pilot was permitted only after compilation, focused tests, safety/freshness regressions, and the actor/evaluator isolation audit passed.

## COARSE NAVIGATION

- Accepted coarse moves: **10**.
- The previous 3 mm component-wise premature STOP rule was removed. A coarse action is taken only when its predicted measured-pose error reduction is at least 0.05 mm.

## FINE NAVIGATION

- Accepted fine moves: **0**.
- Fine command: **0.5 mm**, with the existing 0.25 mm meaningful-progress floor unchanged.
- Rejected moves: **0**.

## FINAL POSE

- Longitudinal residual: **0.335909 mm**.
- Lateral residual: **0.001782 mm**.
- Full 3D contact-point residual: **0.339965 mm**.
- Quaternion orientation error: **0.000000 deg**.
- Surface-yaw / normal-alignment error: **0.000000 / 0.000000 deg**.
- POSITION_PRECISION: **PASS**.

## FRESH ULTRASOUND CONFIRMATION

- Locked SSIM: **0.966989**.
- Locked threshold: **0.971000** (unchanged).
- Fresh frame and publication association: **True**.
- Independent evaluator T7 correspondence: **True**.
- FRESH_ULTRASOUND_CONFIRMATION: **FAIL**.

Calibration publication metadata were persisted at pose epoch 3, producer frame 2, scan serial 5, and capture frame 2.

## OUTCOME

P1_R2_OVERALL: **POSE_PRECISE_IMAGE_MISMATCH**.

Reducing contact-point residual from **2.660031 mm** to **0.339965 mm** increased SSIM from **0.643609** to **0.966989**. This is strong evidence that premature stopping was a major contributor to the original image mismatch. It was not sufficient to satisfy the locked criterion: the new value remains **0.004011** below threshold. The causal hypothesis is therefore supported, but exact locked-threshold reacquisition is not demonstrated.

This single pilot does not use SSIM for steering and does not establish autonomous T7 identification.

## LIMITATIONS

- Fixed-anatomy, simulator-only, calibrated engineering baseline.
- One yaw-0 episode; no retry or held-out starting condition.
- AUTONOMOUS_T7_IDENTIFICATION: **NOT_TESTED**.
- G2: **FAIL**; G3/G4: **NOT_RUN**.
