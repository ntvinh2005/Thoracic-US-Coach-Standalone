# G2.5 Ground-Truth-Free Thoracic Exploration — Final Confirmation

## Outcome

The frozen fixed-anatomy confirmation passed the ground-truth-free acquisition and landmark-localization milestone. The controller started from a fixed mechanically defined, anatomically unlabeled Home, used a robot/world-back surface tangent rather than an LF-derived axis, explored symmetric generic skin-continuity limits, captured 244 fresh ultrasound frames, and physically returned to Home. Runtime acquisition did not load LF transforms, T-level coordinates, evaluator correspondence, or the calibrated T7 pose.

The frozen actor accepted exactly seven image-supported events, rejected all competing events, constructed one ordered map, and emitted T7 at `+39.496803 mm` relative to the new Home. Independent scoring after blind-output hashing found 7 TP, 0 FP, 0 FN and maximum per-level error `0.299056 mm`.

This is a fixed-anatomy, fixed-mechanical-setup simulator feasibility result. The T2–T8 names are a **CONDITIONAL_T2_T8_ENUMERATION** under the approved prior that the intended controlled region contains exactly T2–T8. The images establish seven ordered LF-associated recess landmarks; they do not independently provide an absolute T2 endpoint anchor. This is not patient-level generalization, lateral-perturbation robustness, or G4 physical T7 return.

## Frozen confirmation

- Source freeze commit: `a37a148`
- Protocol commit: `16d7668`
- Acquisition: scene-authored generic contact shifted by a frozen robot-local `-53.5 mm` lateral offset; world-back surface tangent; yaw `89.5°`; width `45 mm`; step `1.5 mm`
- Planning: symmetric `120 mm` mechanical ceiling with `6 mm` generic skin-boundary reserve; no LF-derived endpoint or movement count
- Model: interface-centered 16×16 intensity/gradient ExtraTrees classifier; dense 2 mm development negative sampling; frozen acceptance threshold `0.70`
- Episode count after freeze: exactly one; no retry and no post-evaluation tuning
- Physical movements: 243 accepted, 0 rejected
- Fresh frames: 244/244; 244 unique hashes and 244 unique scan serials
- Contact/workspace/safety failures: 0
- Final Home residual: `1.501155 mm` within the locked `3 mm` physical-return tolerance
- Actor map: `ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP`
- Predicted T7: `+39.496803 mm`
- Blind map SHA-256: `24da9fa2e0c41118dff5b1c59a0e8f2fb326af4158b6a62dcbf0f983cc2aeb7c`
- All blind hashes rechecked unchanged after evaluator execution

## Per-level independent evaluation

| Level | Actor prediction (mm) | Evaluator reference (mm) | Absolute error (mm) | Margin to 5 mm (mm) | PASS |
|---|---:|---:|---:|---:|---|
| T2 | -88.503197 | -88.480239 | 0.022957 | 4.977043 | YES |
| T3 | -66.503197 | -66.204141 | 0.299056 | 4.700944 | YES |
| T4 | -42.003197 | -41.878424 | 0.124772 | 4.875228 | YES |
| T5 | -15.503197 | -15.726729 | 0.223532 | 4.776468 | YES |
| T6 | +11.496803 | +11.509710 | 0.012907 | 4.987093 | YES |
| T7 | +39.496803 | +39.584278 | 0.087475 | 4.912525 | YES |
| T8 | +69.996803 | +70.035651 | 0.038848 | 4.961152 | YES |

Independent metrics: precision `1.0`, recall `1.0`, F1 `1.0`, exact sequence `true`, accepted false events `0`.

## What resolved the blocker

The previous ground-truth-free axis projected `world-down` into the skin plane. Because the local skin normal has lateral and longitudinal components, this produced a diagonal path (`x≈-0.21, z≈-0.98`) that crossed different bone surfaces and made event locations and morphology acquisition-sensitive. Replacing it with the independent robot/world-back axis projected into the skin tangent produced a nearly longitudinal path (`x≈0.00036, z≈-0.99916`) without reading anatomy.

The historical classifier still missed the caudal events on the new fixed corridor. Development-only evaluator labels were then used to train a corridor-domain model. Dense 2 mm negative sampling included the stable secondary interface between the first two landmarks; the classifier sees no coordinate feature, so it learned the image morphology rather than a hardcoded position. On development it produced 7 TP/0 FP. The frozen fresh confirmation reproduced 7 TP/0 FP.

## Development results and limitations

- Earlier generic world-down corridors were uninformative or incomplete.
- World-back `-53.5 mm` with the historical model localized T2–T6 but missed T7/T8 at the frozen threshold.
- A development-trained model with 4 mm negative sampling accepted the stable secondary interface (8 events) and was rejected.
- Dense 2 mm negative sampling preserved all true events and rejected the secondary event.
- A held-out lateral perturbation at `-52.0 mm` found only 4/7 events. Therefore robustness to a 1.5 mm lateral Home shift is **FAIL**, and no claim beyond the frozen fixed setup is made.
- The final confirmation repeats the frozen mechanical Home but uses a new physical execution and fresh renderer outputs. This supports fixed-setup reproducibility, not acquisition-perturbation generalization.

Across the complete G2.5 work, six development sessions and one frozen confirmation were executed. The final corridor-search continuation contributed four development sessions plus the one confirmation. No G4 return was executed.

## Information isolation

- Actor inputs: clean PNG pixels, accepted actions, achieved displacement, Home-relative motion, direction, and acquisition geometry.
- Safety inputs: generic skin contact, surface normals, workspace validity, mechanical limits, and rollback state.
- Evaluator inputs: LF-associated R1 references and privileged trajectory, loaded only after actor JSON and SHA-256 freeze.
- Runtime actor code has no evaluator-reference argument. The final target is the sixth accepted actor event, not a calibrated T7 coordinate.
- Supervised development labels are disclosed. Whole physical sessions, not adjacent random frames, separate development from the frozen confirmation.

## Gates

| Gate | G2.5 verdict | Evidence |
|---|---|---|
| G0 | PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION | Historical R1 validated references preserved |
| G1 | PASS | 243 accepted moves, 244 fresh frames, complete two-sided mechanical sweep, physical Home return, zero safety failures |
| G2 | PASS_FIXED_SETUP | Seven independently matched LF-associated image events; zero accepted false events |
| G3 | PASS_CONDITIONAL_ENUMERATION | Unique ordered seven-event map and non-null V6/T7, with no runtime evaluator leakage |
| G4 | NOT_RUN_OUT_OF_SCOPE | No physical T7 return in this campaign |

## Verification

- Python full Tools/ThoracicUSCoach regression: 402 passed, 1 skipped because its historical Milestone-1 artifact is absent, 0 failed; focused changed-code tests were 14/14 PASS.
- Unity licensed EditMode: 10/10 PASS after the mechanical-axis and scan-plane integrity changes.
- Unity compilation: PASS as part of the licensed test and all three later physical sessions.
- Confirmation Unity process completed normally; 244/244 frame hashes and serials were unique.
- Scene, Skeleton, UltrasoundManager, materials, colliders, Active2D, and P1-R3 were not modified by this campaign.
- Historical execution scene SHA remained `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`.

## Evidence

- Frozen protocol: `frozen_protocol.json`
- Source/model hashes: `source_configuration_hash_manifest.json`
- Final map: `relative_spine_map.json`
- Uncertainty/support extents: `mapping_uncertainty.json`
- Per-level score: `per_level_error_analysis.csv`
- Fresh ultrasound contact sheet: `final_event_contact_sheet.png`
- Trajectory/map overlay: `final_trajectory_and_map.png`
- Error plot: `per_level_localization_error.png`
- Actor outputs and blind hashes: `final_confirmation_fixed_home_m53_5/actor_analysis/`
- Independent evaluator: `final_confirmation_fixed_home_m53_5/evaluator_only/actor_score/`
- Complete local raw frames: `D:/CSSALT/Thoracic-US-Coach-Standalone/Artifacts/ThoracicUSCoach/sweep_count/G2_5_CORRIDOR_SEARCH_20261103_R01/final_confirmation_fixed_home_m53_5/frames/`

## Next stage

Landmark discovery is complete for the frozen fixed-setup milestone. Before G4, review a minimal adapter that hands the actor-derived `+39.496803 mm` target to the P1-R3-compatible physical controller, without loading the calibrated T7 pose; then physically return, restore the validated T7-right transverse orientation, capture a fresh image, and score it independently. The failed `-52.0 mm` validation should remain a documented acquisition-robustness limitation.
