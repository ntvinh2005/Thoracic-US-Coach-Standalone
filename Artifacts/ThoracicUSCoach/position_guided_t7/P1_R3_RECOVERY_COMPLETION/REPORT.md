# P1-R3 Recovery, Completion and Critical Review

## RECOVERY STATE

The resumed local state was **NOT_STARTED**: no P1-R3 source, registry, physical run, frozen configuration, or confirmation evidence existed. P1/P1-R1/P1-R2 were preserved as historical baselines.

Current experiment state: **COMPLETE**.

## IMPLEMENTATION

P1-R3 is isolated from P1-R2. It adds measured-pose coarse/fine control with a 0.25 mm full 3D tolerance, actual 0.5 mm fine-motion diagnostics, cumulative run accounting, frozen confirmation starts, and offline original-SSIM evaluation. It does not alter Active2D, DirectFreshCapture, the scene, renderer, anatomy, or the historical SSIM threshold.

## VERIFICATION

- Frozen source SHA-256 identities remained unchanged through all confirmation episodes.
- Python focused/regression tests: **13/13 PASS**.
- Unity 6000.3.9f1 batch compilation after license recovery: **PASS**, exit code 0.
- Unity EditMode preflight: **53/53 PASS** across Active2D safety, freshness, mini-zigzag regression, P1/P1-R2 and P1-R3 fixtures.
- Confirmation physical movements: **32 accepted, 0 rejected, 0 safety failures**.
- Six confirmation captures (start and return for each episode): **6/6 fresh, frame-integrity valid and exact-provenance matched**.
- No controller, metric, threshold or frozen parameter was adapted between C01, C02 and C03.

## DEVELOPMENT

- Physical development runs consumed: **3/20**.
- Navigation runs: **2**.
- Fine-motion sensitivity runs: **1**.
- Freeze eligible: **True**.

Navigation development evidence:
[
  {
    "acceptedCoarseMovements": 10,
    "acceptedFineMovements": 0,
    "finalFullContactPointErrorMm": 0.01900058053433895,
    "finalLateralErrorMm": -0.000877415353897959,
    "finalLongitudinalErrorMm": 0.01898031122982502,
    "finalOrientationErrorDeg": 0.0,
    "imageIntegrity": "PASS",
    "kind": "NAVIGATION",
    "originalExactImageSsim": 0.9933786866235481,
    "originalExactImageSsimPass": true,
    "positionPrecision": "PASS",
    "rejectedMovements": 0,
    "runId": "D01",
    "scenario": "POSITION_NAV_MINUS30",
    "status": "EXECUTED"
  },
  {
    "acceptedCoarseMovements": 9,
    "acceptedFineMovements": 1,
    "finalFullContactPointErrorMm": 0.08481069654226303,
    "finalLateralErrorMm": -0.0008371648727916181,
    "finalLongitudinalErrorMm": -0.08480656147003174,
    "finalOrientationErrorDeg": 0.0,
    "imageIntegrity": "PASS",
    "kind": "NAVIGATION",
    "originalExactImageSsim": 0.9898599342656607,
    "originalExactImageSsimPass": true,
    "positionPrecision": "PASS",
    "rejectedMovements": 0,
    "runId": "D02",
    "scenario": "POSITION_NAV_MINUS27_4",
    "status": "EXECUTED"
  }
]

Sensitivity evidence:
[
  {
    "acceptedFineMovements": 8,
    "centerReturn": "PASS",
    "finalFullContactPointErrorMm": 9.945941565092653e-05,
    "imageIntegrity": "PASS",
    "kind": "SENSITIVITY",
    "runId": "D03",
    "status": "EXECUTED"
  }
]

## IMAGE VALIDITY

- IMAGE_INTEGRITY is reported independently per run.
- SIMULATOR_ANATOMICAL_VALIDITY remains limited to the fixed Unity anatomy and independent T7 evaluator correspondence.
- ORIGINAL_EXACT_IMAGE_SSIM remains locked at **0.971000105354**.
- NEW_TASK_IMAGE_CONSISTENCY: **NOT_DEVELOPED**. No replacement threshold was selected from P1-R2.
- Incorrect-T-level rejection by a new metric: **NOT_APPLICABLE**.

## CONFIRMATION

- Confirmation episodes consumed: **3/3**.
- Ultrasound feedback used for navigation: **False**.

[
  {
    "runId": "C01",
    "status": "EXECUTED",
    "startOffsetMm": -24.0,
    "coarseMovements": 8,
    "fineMovements": 0,
    "rejectedMovements": 0,
    "finalLongitudinalErrorMm": 0.008651085197925568,
    "finalLateralErrorMm": -0.000643619685433805,
    "finalFullContactPointErrorMm": 0.008675002492964268,
    "finalOrientationErrorDeg": 0.0,
    "contactValid": true,
    "workspaceValid": true,
    "freshAndProvenanceMatched": true,
    "originalExactImageSsim": 0.9961368678685748,
    "originalExactImageSsimPass": true,
    "independentEvaluatorT7": true,
    "verdict": "PASS"
  },
  {
    "runId": "C02",
    "status": "EXECUTED",
    "startOffsetMm": -33.0,
    "coarseMovements": 11,
    "fineMovements": 0,
    "rejectedMovements": 0,
    "finalLongitudinalErrorMm": 0.024136247113347054,
    "finalLateralErrorMm": -0.001052792533300817,
    "finalFullContactPointErrorMm": 0.024159198626875877,
    "finalOrientationErrorDeg": 0.0,
    "contactValid": true,
    "workspaceValid": true,
    "freshAndProvenanceMatched": true,
    "originalExactImageSsim": 0.994015557688491,
    "originalExactImageSsimPass": true,
    "independentEvaluatorT7": true,
    "verdict": "PASS"
  },
  {
    "runId": "C03",
    "status": "EXECUTED",
    "startOffsetMm": -39.0,
    "coarseMovements": 13,
    "fineMovements": 0,
    "rejectedMovements": 0,
    "finalLongitudinalErrorMm": 0.045466165989637375,
    "finalLateralErrorMm": -0.004394122399389744,
    "finalFullContactPointErrorMm": 0.04567800834774971,
    "finalOrientationErrorDeg": 0.0,
    "contactValid": true,
    "workspaceValid": true,
    "freshAndProvenanceMatched": true,
    "originalExactImageSsim": 0.9931253930912354,
    "originalExactImageSsimPass": true,
    "independentEvaluatorT7": true,
    "verdict": "PASS"
  }
]

## SCIENTIFIC DISTINCTIONS

- Calibrated pose return: evaluated by measured full-pose error.
- Stored-view reproduction: evaluated separately by the original locked SSIM.
- Ultrasound-assisted correction: **NOT TESTED**, because the selected controller is position-only.
- Autonomous T7 identification: **NOT_TESTED**.
- G2: **FAIL**; G3/G4: **NOT_RUN**.

## REMAINING BLOCKER

Frozen exact-view confirmation passed in all three predeclared episodes. The highest-priority remaining scientific blocker is **absolute T7 identification without a stored calibrated T7 target pose**. P1-R3 demonstrates calibrated, fixed-anatomy pose return and view reproduction; it does not demonstrate ultrasound-assisted steering or autonomous anatomical localization.

## FINAL VERDICTS

- CALIBRATED_POSE_RETURN: **SUPPORTED (3/3 frozen confirmation episodes PASS)**.
- STORED_T7_VIEW_REPRODUCTION: **SUPPORTED (3/3 original locked SSIM PASS)**.
- ULTRASOUND_FEEDBACK_IMPROVED_NAVIGATION: **NOT_TESTED**; navigation was position-only.
- P1_R3_OVERALL: **PASS — CALIBRATED FIXED-ANATOMY ENGINEERING BASELINE**.
- AUTONOMOUS_T7_IDENTIFICATION: **NOT_TESTED**.
- G2: **FAIL**; G3/G4: **NOT_RUN**.

The confirmation episodes used 32 accepted coarse moves, zero fine moves, zero rejected moves and zero unhandled safety violations in total. Final 3D errors ranged from 0.008675 to 0.045678 mm. Original exact-image SSIM ranged from 0.993125 to 0.996137 against the locked threshold 0.9710001053543619.
