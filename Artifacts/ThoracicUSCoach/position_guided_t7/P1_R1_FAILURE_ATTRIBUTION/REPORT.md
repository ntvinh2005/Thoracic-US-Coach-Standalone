# P1-R1 Position-Guided T7 Reacquisition Failure Attribution

## 1. IMPLEMENTATION

Read-only offline audit only. Existing source and P1 artifacts were not modified. No Unity capture, training, threshold change, controller change, commit, or push was performed. This directory contains derived diagnostic evidence and the isolated audit script.

## 2. VERIFICATION

- Essential P1 files present: **True**.
- P1 offline freeze hashes: **PASS** (7/7 valid).
- Calibration PNG hash: **PASS**.
- Return PNG hash and freshness association: **PASS**.
- Both images: `1024x1024` `RGBA`.
- Limitation: the exact calibration image pose epoch/producer frame/scan serial was not serialized. Its hash and runtime `fresh=true` are present; same-pose instrumentation evidence is separate.

## 3. NAVIGATION STOP ROOT CAUSE

The controller stopped because the remaining longitudinal error was **2.628346 mm**, below the locked 3 mm tolerance, with lateral error **0.001602 mm**. There was no rejection, safety failure, workspace failure, or alternate STOP trigger.

Calibrated contact point: `(-2.912864, 138.711807, -48.825516) mm`; calibrated quaternion: `(-0.760374740, 0.025261140, 0.028832028, 0.648352407)`. Initial and every executed pose are preserved in `navigation_pose_error.csv` and `stop_condition_audit.json`.

Final target-minus-contact vector: `(-0.077098, -0.419952, -2.625568) mm`; 3D contact distance **2.660059 mm**. Quaternion, surface-yaw, and normal-alignment errors were all 0 degrees. The actor's two-axis stop model leaves an additional **0.409523 mm** orthogonal component caused by the curved contact geometry.

A linear extrapolation of the last real 3 mm motion predicts another full step would cross the target longitudinally by approximately **0.336 mm** and be closer in 3D. This step was not executed, so safety acceptance is unknown. The current P1 loop has no smaller step, although the unchanged Active2D primitive declares a 0.5–5 mm range and retains all safety checks.

## 4. CALIBRATION VS RETURN IMAGE

- Locked SSIM recomputation: **0.643609167534**, exactly reproducing the reported value within `0.000e+00`.
- Locked threshold: **0.971000105354**; verdict remains **FAIL**.
- Same calibration pose, separate natural render: **0.989491084555**, above threshold.
- Left/right descriptor SSIM: **0.624722 / 0.662496**.

The side-by-side/difference image shows displacement and shape changes in the central bright arch, central shadow boundary, and lower bilateral bright interfaces. The evaluator-only beam audit also changes visible-interface identities/depths. This is evidence of a changed scan-plane intersection, not a stale or missing frame. Exact anatomy identity remains unresolved.

## 5. POSE ERROR VS SSIM SENSITIVITY

The threshold was calibrated from **39** reverse-pass near-same-position pairs. Their median actual full-pose separation was **0.002723 mm**, not 2–3 mm. The 2.660 mm P1 return is therefore outside the threshold's primary repeatability population.

Existing M0-B data contain approximately one-step pairs, and **98/98** fall below the locked threshold. However, there are no direct P1 frames between the calibration pose and the 2.660 mm return pose. Exact verdict: **INSUFFICIENT_POSE_SENSITIVITY_DATA** for the sub-step curve at T7.

The smallest observed one-step full-pose separation below threshold was **2.999972 mm** with SSIM **0.514357**. This is a corridor-wide observation, not a T7-specific operating point.

## 6. RELOCALIZATION AUDIT

- Reference/query captures are disjoint: **0 overlapping observation IDs**.
- IMAGE_ONLY selection uses descriptor SSIM only; query ground-truth position enters after selection for scoring.
- Fusion applies the declared bias to measured query pose and uses no second unperturbed position in selection.
- Clean IMAGE_ONLY errors: median **0.002693 mm**, worst **2.999985 mm**.
- Near-exact matches: **39/40**; adjacent-grid matches: **1/40**.

The ~0.003 mm median occurs because independent traversals revisit nearly identical 3 mm grid locations. The ~3 mm worst case is one adjacent grid sample. Wrong-location is defined as >6 mm, so a one-step error is not counted wrong. This supports discrete map-bias correction on the same path, not continuous fine visual correction.

## 7. ROOT-CAUSE CLASSIFICATION

| Hypothesis | Classification |
| --- | --- |
| H1 — Position tolerance / discrete step | **CONFIRMED** |
| H2 — Calibration or pose-frame mismatch | **REFUTED** (calibration publication metadata persistence is incomplete) |
| H3 — Acquisition or SSIM mismatch | **REFUTED** |
| H4 — Genuine ultrasound sensitivity to residual pose | **SUPPORTED** |
| H5 — Relocalization leakage/metric caused the physical failure | **REFUTED** |

Primary attribution: premature coarse-tolerance STOP combined with real ultrasound sensitivity to residual scan-plane translation. The exact pose-to-SSIM operating curve remains unmeasured.

## 8. ONE RECOMMENDED MINIMAL FIX

For a separately approved P1-R2 only: preserve coarse 3 mm navigation, then use the same safe Active2D primitive at fixed 0.5 mm steps until both longitudinal and lateral residuals are <=0.5 mm. Keep SSIM as locked confirmation, not steering. Do not change Active2D, freshness, renderer, threshold, or evaluator separation.

## 9. EXACT FILES AND RISKS

Future modification would be limited to:

- `ThoracicUSCoachController.PositionGuidedT7.cs`
- `PositionGuidedT7Tests.cs`
- `ThoracicPositionGuidedT7Batch.cs` only for isolated P1-R2 run IDs

Risks: fine-step progress may fall near the 0.25 mm meaningful-progress floor, curvature may create small residual normal displacement, and closer pose may still fail SSIM. Three locked yaw-0 smoke pilots are required; stop on the first valid fine-position run that still fails SSIM.

## 10. DECISION REQUIRED

Approve or reject the bounded P1-R2 two-stage position-control experiment. No correction or new pilot has been executed in P1-R1.

Scientific status remains unchanged: POSITION_NAVIGATION **PASS** under 3 mm tolerance; FRESH_ULTRASOUND_CONFIRMATION **FAIL**; AUTONOMOUS_T7_IDENTIFICATION **NOT_TESTED**; G2 **FAIL**; G3/G4 **NOT_RUN**.
