# P1-R3 Recovery, Completion and Critical Review

## 1. Recovered state

The local P1-R3 state at recovery was **NOT_STARTED**. There was no P1-R3 source, run registry, development run, frozen configuration, or confirmation evidence. Existing P1/P1-R1/P1-R2 data and unrelated working-tree changes were preserved.

Current state: **CONFIG_FROZEN / BLOCKED BEFORE CONFIRMATION**.

The blocker is Unity licensing infrastructure, not a controller, image, motion-safety, or test failure. After development and freeze, new Unity batch launches ended with exit code 198: `No valid Unity Editor license found`. The local entitlement file remains present, but the Unity Hub access token is absent from the license cache. No confirmation episode was started or consumed.

## 2. Implementation

P1-R3 is isolated from P1-R2. It adds:

- measured-pose coarse/fine position control;
- 3 mm coarse and 0.5 mm fine physical steps;
- a locked 0.25 mm longitudinal, lateral, and full 3D contact-point tolerance;
- a locked 0.5 degree quaternion-orientation tolerance;
- minimum-improvement, movement-budget, rejection, rollback, contact, and workspace stopping rules;
- actual fine-motion pose/SSIM sensitivity capture;
- a cumulative run registry and immutable frozen configuration;
- offline evaluation using the unchanged original block SSIM and threshold `0.9710001053543619`.

The implementation does not change Active2D, DirectFreshCapture, the Unity scene, anatomy, renderer, skin collider, or historical scientific gates. Runtime navigation uses only the stored calibration pose, measured robot pose, executed-action outcome, and safety feedback. T7 reference data are used once for development calibration and separately for evaluator-only records.

## 3. Verification

- Python focused/regression tests: **13/13 PASS** when invoked from `Tools/ThoracicUSCoach`.
- An earlier root-directory Python invocation produced 3 import-loader errors because the local module directory was not on `sys.path`; no test logic ran in that invocation.
- Unity compilation before development: **PASS**, exit code 0.
- Unity EditMode tests: **53/53 PASS** for P1-R3/P1-R2, Active2D safety, direct fresh capture, and ultrasound provenance.
- Focused Unity P1-R3 tests after the final development-start offset change: **7/7 PASS**.
- Development D01-D03 subsequently compiled and executed the frozen source successfully.
- Static actor/evaluator separation and `git diff --check`: **PASS**.
- Frozen source hashes remain unchanged after freeze: **PASS**.
- Post-freeze Unity batch recheck: **BLOCKED** by license exit 198. This prevented confirmation and is not reported as a passing preflight.

## 4. Development evidence

Physical development runs consumed: **3/20**. Confirmation episodes consumed: **0/3**.

| Run | Purpose | Coarse | Fine | Rejected | Final 3D error | Orientation | Original SSIM | Result |
|---|---|---:|---:|---:|---:|---:|---:|---|
| D01 | Navigation from -30 mm | 10 | 0 | 0 | 0.019001 mm | 0.0 deg | 0.993379 | PASS |
| D02 | Navigation from -27.4 mm | 9 | 1 | 0 | 0.084811 mm | 0.0 deg | 0.989860 | PASS |
| D03 | Bounded +/-1 mm fine-motion sensitivity | 0 | 8 | 0 | 0.000099 mm after return | 0.0 deg | 0.997820 at final return | PASS |

D02 is the direct executed evidence that the 0.5 mm fine stage works physically. It reduced longitudinal residual from approximately +0.415 mm to -0.0848 mm with an achieved displacement of 0.500014 mm. This is not inferred from a unit test.

D03 executed eight 0.5 mm moves with zero rejection or safety violation. SSIM dropped to approximately 0.944-0.951 at +/-0.5 mm and 0.843-0.851 at +/-1.0 mm. Physical returns to within 0.0001 mm produced SSIM 0.993312 and 0.997820. The data support genuine ultrasound sensitivity to sub-millimetre longitudinal pose in this fixed simulation.

## 5. Frozen candidate

The selected candidate is position-only. Ultrasound is confirmation-only; it does not steer the controller. The source, controller parameters, original image metric, threshold, calibration target/reference image, and three confirmation starts (-24, -33, -39 mm) are recorded in `frozen_configuration.json` and `freeze.sha256`.

No parameter or source was changed after viewing confirmation data because no confirmation data exist.

## 6. Image validity

- **IMAGE_INTEGRITY:** PASS for D01-D03; all captured observations used in the analysis were fresh and provenance-matched.
- **SIMULATOR_ANATOMICAL_VALIDITY:** limited to the fixed Unity anatomy and evaluator-only T7 correspondence; no patient or cross-anatomy claim.
- **ORIGINAL_EXACT_IMAGE_SSIM:** PASS in both navigation development runs and both D03 center returns.
- **NEW_TASK_IMAGE_CONSISTENCY:** NOT_DEVELOPED. No replacement metric or threshold was introduced.
- **Incorrect-T-level rejection by a new metric:** NOT_APPLICABLE. No new metric exists, and T6/T8 negative-control claims are therefore not made.

## 7. Confirmation and scientific verdicts

- **CALIBRATED_POSE_RETURN (development): SUPPORTED.**
- **STORED_T7_VIEW_REPRODUCTION (development): SUPPORTED.**
- **ULTRASOUND_FEEDBACK_IMPROVED_NAVIGATION: NOT_TESTED.** The frozen controller is position-only.
- **FROZEN_CONFIRMATION: NOT_RUN / BLOCKED.** Unity licensing failed before C01.
- **AUTONOMOUS_T7_IDENTIFICATION: NOT_TESTED.**
- **P1-R3 overall: BLOCKED**, not PASS.
- **G2: FAIL. G3/G4: NOT_RUN.**

## 8. Critical review

The implementation fixes the P1-R2 premature-stop limitation and demonstrates actual safe fine motion. The development evidence also shows that the original exact-image SSIM is physically meaningful but very sensitive to residual longitudinal pose. The strongest current result is a calibrated, fixed-anatomy position controller that reproduced the stored view in development.

This does not demonstrate ultrasound-assisted correction, uncalibrated T7 identification, or confirmation-start generalization. Because the confirmation set was frozen but never executed, development success must not be promoted to a final P1-R3 success claim.

## 9. Highest-priority remaining blocker and next action

The single highest-priority blocker is restoration of a valid Unity batch license/session. After that external prerequisite is restored, rerun the unchanged post-freeze compile/regression preflight and execute C01 from the already frozen -24 mm start. If C01 fails any pose, safety, integrity, evaluator, or original-SSIM criterion, stop without C02/C03. If C01 passes, continue the unchanged frozen sequence up to the cumulative maximum of three.

Do not regenerate the freeze, alter thresholds, or reset run counts.

## 10. GitHub review readiness

- Recovery workspace branch: `codex/u08-phase2`.
- Recovery workspace HEAD: `f0f7e87ee7522dbef8937e23139d7de21b3e7c23`.
- P1-R3 evidence size: 65 files, 6,258,534 bytes total; largest individual file approximately 256 KB.
- All P1-R3 source, JSON/CSV/XML evidence, plots, and raw development ultrasound images are small enough to publish.
- No P1-R3 raw file requires exclusion for GitHub size limits.
- Unrelated staged/untracked historical artifacts, scene changes, material changes, and `VersionData.asset` changes are not part of the P1-R3 publication set.
