# Autonomous T2–T8 Landmark Discovery

## Outcome

The frozen fixed-anatomy confirmation passed the authorized landmark-discovery objective. Starting at fixed anatomically unlabeled Home, the robot completed one physical bidirectional M6 sweep, captured 221 fresh ultrasound frames, and returned physically to Home. The frozen actor accepted seven image-supported events, rejected the recurrent secondary interface, built a unique ordered T2–T8 map, and emitted T7 at `+42.997581 mm` relative to Home. Independent scoring performed after blind-output hashing found 7 TP, 0 FP, 0 FN and per-landmark error below 0.255 mm.

This is preliminary simulator feasibility on one fixed anatomy. It is not clinical evidence, cross-patient generalization, or a G4 physical T7-return result.

## Frozen confirmation

- Preconfirmation source/model freeze: `dc931242529146f1326237376537f478df65b730`
- Acquisition: M6 paramedian longitudinal, lateral offset -6 mm, yaw 89.5 degrees, top width 45 mm, depth 50 mm, 1.5 mm commanded sampling
- Episode count: exactly one; no retry and no post-evaluation tuning
- Physical movements: 220 accepted, 0 rejected
- Fresh frames: 221/221; 221 unique image hashes and 221 unique increasing scan serials
- Contact/workspace/integrity failures: 0
- Final Home residual: 0.001177 mm
- Actor map status: `ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP`
- Accepted/rejected image events: 7/1
- Rejected event: -73.502419 mm, score 0.592663 below the frozen 0.70 acceptance threshold
- Predicted T7: +42.997581 mm

The actor outputs were hashed before the evaluator was invoked. The frozen map SHA-256 is `6fc99eafe013e29a8d5b491ad797dd68d64da7c7197cab267d39dd3889481fc1`. Rechecking after evaluation confirmed that all blind hashes still matched.

## Per-level independent evaluation

| Level | Actor prediction (mm) | Evaluator reference (mm) | Signed error (mm) | Absolute error (mm) | Margin to 5 mm (mm) | PASS |
|---|---:|---:|---:|---:|---:|---|
| T2 | -86.502419 | -86.622489 | +0.120070 | 0.120070 | 4.879930 | YES |
| T3 | -63.502419 | -63.558186 | +0.055768 | 0.055768 | 4.944232 | YES |
| T4 | -38.502419 | -38.756323 | +0.253904 | 0.253904 | 4.746096 | YES |
| T5 | -12.502419 | -12.420832 | -0.081586 | 0.081586 | 4.918414 | YES |
| T6 | +14.997581 | +14.852174 | +0.145408 | 0.145408 | 4.854592 | YES |
| T7 | +42.997581 | +42.977545 | +0.020037 | 0.020037 | 4.979963 | YES |
| T8 | +73.497581 | +73.752155 | -0.254574 | 0.254574 | 4.745426 | YES |

The maximum error was 0.254574 mm at T8. The previous near-threshold T7 error was not present in the frozen method; T7 error was 0.020037 mm.

## Method

The selected method replaces the old single lower-interface valley count with a spatial patch-likelihood map:

1. Extract bounded bright-interface hypotheses from each clean ultrasound image.
2. Normalize local 16x16 intensity and gradient patches around those interfaces.
3. Apply a small CPU ExtraTrees classifier trained only on explicitly separated development sessions with evaluator labels isolated to `fit`.
4. Project each observation into the actor's Home-relative coordinate system using actual executed movement and calibrated scan footprint.
5. Aggregate evidence on a 0.5 mm grid, consolidate repeated forward/reverse observations, and find spatial peaks separated by at least 10 mm.
6. Accept peaks only above the frozen 0.70 classifier threshold. Keep lower-scoring recurrent peaks in the audit as rejected evidence.
7. Emit a map only when exactly seven distinct accepted events form a unique sequence. Apply the approved order so V1–V7 map to T2–T8 and V6 is T7; otherwise abstain.

The actor-side `infer` command has no reference-coordinate argument and does not load evaluator artifacts. The evaluator is a separate post-freeze `score` command.

## Development campaign

Four new development sessions were used (884 fresh frames), followed by one frozen confirmation (221 frames). Total new physical data were 1,105 frames and 1,100 accepted movements, with zero rejected movements or safety failures.

- Wide acquisition reproduced all seven landmarks across yaw 90/91 development data.
- A 12 mm narrow-field experiment lost four landmarks and was rejected.
- Yaw 89 and yaw 90.5 experiments tested small acquisition perturbations.
- A 0.60 acceptance threshold admitted the stable secondary interface on two development comparisons. The frozen 0.70 threshold retained all seven true events on the wide development comparisons while rejecting that secondary event. It was frozen before confirmation.
- No active revisit was needed in the final episode because the first complete sweep produced a unique map. No anatomical coordinate was used to select an event or movement target.

The classifier is supervised on fixed-anatomy development sessions. Session-level separation and a new yaw-89.5 confirmation avoid adjacent-frame random splitting, but this does not establish patient-level or anatomy-level generalization.

## Scientific gates

| Gate | Verdict | Evidence |
|---|---|---|
| G0 anatomical reference | PASS with combined-Skeleton identity limitation | Historical R1 reference audit retained |
| G1 physical coverage | PASS | 220 accepted moves, 221 fresh frames, full corridor, physical Home return, no safety failures |
| G2 anatomical observability | PASS | 7/7 LF-associated recess events independently matched; 0 accepted false event |
| G3 enumeration and T7 inference | PASS | Unique T2–T8 sequence, non-null V6/T7, no evaluator leakage |
| G4 physical T7 return | NOT_RUN | Explicitly outside this campaign |

## Verification

- Python focused regressions after confirmation: 155 passed in 12.56 seconds.
- Unity confirmation preflight: 8/8 EditMode tests passed before the run.
- Unity confirmation process exit: 0.
- Frame hashes: 221/221 matched manifest.
- Protected scene, UltrasoundManager and recorded Skeleton hashes remained at their frozen values; no protected asset was intentionally modified.
- The execution worktree used the historical scene SHA `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`; the review branch retains committed scene SHA `60fef5d7f7ba3e1091c15b89a960ce64cb369f410fd67f436911277cbab1cac9`. The difference was already documented and the historical execution scene was deliberately preserved.

## Reproduction commands and resource use

The licensed Unity graphics-capable confirmation was launched once with:

```text
Unity.exe -projectPath D:\CSSALT\Thoracic-US-Coach-Standalone -executeMethod CSSALT.ThoracicUSCoach.Editor.ThoracicAutonomousT2T8ConfirmationBatch.RunCli -logFile <campaign>\test_results\final_confirmation_unity.log
```

The blind actor was then invoked without any evaluator argument:

```text
python autonomous_t2t8_patch_likelihood.py infer --session FINAL|<confirmation>|trajectory_actor.json --model <campaign>\actor_only_evidence\wide_three_session_patch_model.joblib --output <confirmation>\actor_analysis
```

After hashing those outputs, the independent evaluator was invoked separately:

```text
python autonomous_t2t8_patch_likelihood.py score --candidates <confirmation>\actor_analysis\patch_event_candidates.json --references=<evaluator-only-list> --output <confirmation>\evaluator_only\patch_likelihood_score.json
```

Focused Python regression command:

```text
python -m pytest -q test_g2_r*.py test_autonomous_t2t8_patch_likelihood.py --junitxml=<campaign>\test_results\python_final_regressions.xml
```

The final Unity process took approximately 33 seconds including Editor startup and 220 simulated physical actions. Frozen actor inference over 221 1024x1024 frames took approximately 110 seconds on CPU; the exact inference wall time was not instrumented, so this is an observed rather than benchmark-grade value. The serialized model is 6.64 MB. Python regressions took 12.56 seconds.

## Evidence index

- Actor-only final map: `final_confirmation_01_yaw89_5/actor_analysis/relative_spine_map.json`
- Blind hashes: `final_confirmation_01_yaw89_5/actor_analysis/blind_results.sha256`
- Independent score: `final_confirmation_01_yaw89_5/evaluator_only/patch_likelihood_score.json`
- Raw frames and manifests: `final_confirmation_01_yaw89_5/frames/`, `frame_manifest.json`, `trajectory_actor.json`
- Integrity audit: `final_confirmation_01_yaw89_5/final_integrity_checks.json`
- Actual ultrasound contact sheet: `landmark_and_secondary_contact_sheet.png`
- Actor/evaluator map overlay: `final_relative_spine_map_evaluator_overlay.png`
- Per-level errors: `per_level_error_analysis.csv`, `per_landmark_localization_error.png`
- Probe trajectory: `confirmation_probe_trajectory.png`
- Gate matrix: `final_gates.json`
- Development comparisons: `method_comparison.csv`, `research_experiment_log.md`

## Remaining work

Landmark discovery is complete for the authorized fixed-anatomy feasibility milestone. The next separately reviewed stage is a G4 adapter that hands the frozen actor-derived `+42.997581 mm` T7 target to the validated P1-R3 physical controller, physically returns to that relative coordinate, restores the required T7-right transverse acquisition geometry, captures a fresh image, and performs independent evaluation. No calibrated T7 pose may enter that handoff.
