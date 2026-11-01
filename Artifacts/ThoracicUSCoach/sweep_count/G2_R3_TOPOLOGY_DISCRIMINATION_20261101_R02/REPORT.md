# G2-R3 Anatomical Topology Discrimination & Blind T7 Mapping

## Outcome

G2-R3 **did not pass frozen physical confirmation**. The complementary image-only acoustic-shadow topology rule removed the recurrent G2-R2 false event on development data without adding a false negative, but the single frozen yaw-91-degree confirmation produced only six baseline interface candidates. The topology rule accepted five and marked the deep extra event ambiguous. Independent scoring found `5 TP / 0 FP / 2 FN` (precision 1.0, recall 0.7143, F1 0.8333). The actor abstained, emitted no T7 coordinate, and G4 remained excluded by protocol.

Final gates: G0 `PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION`; G1 `PASS`; G2 `FAIL`; G3 `NOT_RUN`; G4 `NOT_RUN`.

## E02 attribution

E02 is not ordinary random noise. It is a stable, reverse-supported deep contour whose tracked interface identity is inconsistent across its support frames. Evaluator-only ordered-ray evidence at the mapped contour beam showed Skeleton as the first shadow in 11/27 observations and no shadow in 16/27. LF T2/T3 appeared before the first shadow in only 3/27 support observations. The combined Skeleton prevents a more specific bone substructure identity.

The defensible attribution is `TRACKING_IDENTITY_SWITCH_ACROSS_REAL_INTERFACES`: a real recurrent secondary/mixed deep interface, not a uniquely verified LF-associated posterior recess and not a proven inter-level structure. This evaluator conclusion never entered actor inference.

The image-only distinction is that E02 lies too close to the distal image boundary to verify the post-interface acoustic shadow. Its development median interface depth was about 216 px, leaving about 38 px after the 75th-percentile interface depth. The frozen rule requires 45 px of distal field. Below that value the event is `AMBIGUOUS_INSUFFICIENT_DISTAL_FIELD`; it is not deleted or called noise.

See `e02_attribution_report.md`, `evaluator_only/e02_attribution_evaluator.json`, `e01_e02_e03_comparison.png`, and `all_event_morphology_contact_sheet.png`.

## Implementation

- Added `g2_r3_topology_discriminator.py`: image-only interface/shadow topology measurements, uniform distal-field quality gate, and evaluator-only provenance attribution kept in a separate call path.
- Added `g2_r3_offline_audit.py`: three-sweep baseline-versus-topology comparison, contact sheets, waveform, E02 attribution, coordinate audit, and readiness evidence.
- Added `g2_r3_confirmation.py`: blind actor-only confirmation, leakage checks, map construction, figures, and SHA-256 freeze.
- Added `g2_r3_evaluator.py`: evaluator-only one-to-one order-preserving scoring after blind hash verification.
- Added `g2_r3_freeze.py`: source/configuration/test/asset freeze and execution-source parity check.
- Added focused Python and Unity tests.
- Added a minimal opt-in G2-R2 acquisition override and `ThoracicG2R3ConfirmationBatch`; default G2-R2 behavior remains 90 degrees and its original output root. G2-R3 alone used yaw 91 degrees and a separate output directory.
- No scene, Skeleton, renderer, material, collider, Active2D, SweepCount, or P1-R3 behavior was changed.

## Offline development evaluation

The 45-pixel criterion was selected on all three G2-R2 development sweeps after evaluator attribution; these are development-tuned results, not held-out validation.

| Dataset | Baseline | Topology-aware | Effect |
| --- | --- | --- | --- |
| DEV01 | 6 TP, 1 FP, 1 FN | 6 TP, 0 FP, 1 FN | Removed the deep extra; pre-existing T2 miss remained |
| DEV02 | 7 TP, 0 FP, 0 FN | 7 TP, 0 FP, 0 FN | Preserved all seven |
| DEV03 | 7 TP, 1 FP, 0 FN | 7 TP, 0 FP, 0 FN | E02 became ambiguous; all seven retained |

The three streams were separately rendered physical executions but followed nearly identical fixed paths. They provide repeated-path development evidence, not broad trajectory generalization.

## Coordinate projection

The renderer maps texture x to scanning-plane x using one linear transform scaled by `insonationPlaneBottomWidth_mm`. Therefore the original G2-R2 bottom-footprint projection is correct for pixel-to-world scan-plane position. A local-width renormalization by image depth would not reproduce the renderer and was not used. No evaluator-derived coordinate offset was applied.

The development T7 event remained fragile: 4.942 mm error with only 0.058 mm margin to the locked 5 mm tolerance. Confirmation's T7-corresponding accepted event had 4.972 mm diagnostic error, only 0.028 mm margin. Because no seven-event enumeration existed, this is not an emitted T7 prediction. The near-threshold error is dominated by tracked event-center semantics/acquisition variability rather than a missing fan-width correction. The support interval is not presented as a calibrated confidence interval.

## Verification before confirmation

- Python: 28/28 passed.
- Unity focused G2-R2/G2-R3: 10/10 passed.
- Unity Active2D safety, DirectFreshCapture/freshness, and SweepCount regressions: 23/23 passed.
- Unity compilation passed under 6000.3.9f1.
- Execution source matched reviewed source after newline normalization.
- Original execution assets matched the G2-R2 hashes: scene `ce9527...131`, Skeleton FBX `df34d6...9d4e`, UltrasoundManager `2f170e...220e0`.
- Actor/evaluator isolation and freeze-tamper tests passed.

The clean publication worktree did not contain the user's staged scene used by G2-R2. The physical run therefore executed from the preserved original worktree with the identical reviewed in-scope runtime/batch source. The staged scene and all unrelated changes remained uncommitted and unmodified.

## Frozen physical confirmation

Configuration was frozen before launch: lateral offset -6 mm, surface yaw 91 degrees (the only perturbation from development), step 1.5 mm, 45-pixel distal field threshold, unchanged 5 mm evaluator tolerance, and one sweep maximum.

- Fixed anatomically unlabeled Home: yes.
- Accepted/rejected movements: 220/0.
- Fresh frames: 221/221, all unique hashes and scan serials.
- Coverage: actor-relative approximately -88 to +76 mm; evaluator confirmed the intended corridor.
- Safety failures: 0.
- Maximum yaw error: 0.0000153 degrees.
- Maximum normal-alignment error: 0 degrees.
- Final Home residual: 0.001153 mm.
- Original settings restored: yes.

The blind baseline found six events. The topology rule accepted five: approximately -43.052, -14.022, +17.674, +47.950, and +77.611 mm. It marked the recurrent deep event at -72.320 mm ambiguous. Because the event count was five, every T2-T8 map coordinate remained null and `predictedT7RelativeMm` remained null.

Only after `blind_results.sha256` was written and verified did evaluator scoring run. It matched the five accepted events to T4-T8. T2 and T3 were missed. The +1-degree yaw perturbation therefore exposed acquisition sensitivity in the primary interface candidate generator: the topology verifier successfully avoided the false event, but it cannot recover true events that were never proposed.

The new observations were genuinely fresh and not a deterministic image replay, and yaw differed by one degree. Independence remains limited because the fixed anatomy, Home, corridor, and movement schedule were otherwise closely related to development.

## Required answers

1. **What produced E02?** A repeatable mixed deep contour with interface-identity switching; Skeleton was first shadow in 11/27 supports and no shadow existed in 16/27. A single named anatomical structure cannot be resolved.
2. **Secondary, inter-level, or artifact?** A real recurrent secondary image structure/tracking-identity ambiguity, not random noise; inter-level anatomical identity is unresolved.
3. **Distinguishing feature?** Insufficient distal image field to verify post-interface acoustic-shadow continuity: about 38 px versus the frozen 45 px minimum.
4. **Image-only distinction?** Yes for conservative ambiguity/acceptance; no for a definitive semantic tissue identity.
5. **Reject E02 without deleting valid recesses?** Yes on DEV02/DEV03; DEV01 retained its pre-existing T2 miss. In confirmation the same rule rejected the analogous extra, but the baseline had already missed T2/T3.
6. **Consistent development improvement?** FP count fell to zero in all three development streams with no additional FN relative to each baseline.
7. **Projection correct?** Yes; retained renderer-consistent bottom-footprint linear mapping, with no evaluator offset.
8. **Near-threshold T7 error?** Event-center/tracker and acquisition variability; development error 4.942 mm and confirmation diagnostic error 4.972 mm.
9. **Frozen before new evaluation?** Yes; source/configuration hashes preceded the physical run, and blind outputs were hashed before evaluator access.
10. **New confirmation executed?** Exactly one.
11. **Sufficiently independent?** Fresh separate execution with yaw +1 degree, but still limited to the same fixed-anatomy path.
12. **Accepted events?** Five accepted, one ambiguous.
13. **Seven levels correctly inferred?** No; evaluator matched T4-T8 and found T2/T3 missing.
14. **Valid T7 coordinate emitted?** No; actor abstained.
15. **Independent T7 error?** Not applicable to an emitted target. The T7-corresponding diagnostic event error was 4.972 mm.
16. **G2 pass?** No.
17. **G3 pass?** No; NOT_RUN after actor abstention.
18. **Exact blocker?** The lower-interface candidate generator is not robust to a safe one-degree acquisition yaw change and failed to propose the two cranial true events. The topology rule resolves the stable false-event problem but cannot recover missing candidates.

## Commands

Key commands were:

- `python -m unittest test_g2_r2_continuous_detector.py test_g2_r2_features.py test_g2_r2_interface_tracker.py test_g2_r3_topology_discriminator.py`
- Unity EditMode filters for G2-R2/G2-R3, Active2D safety, freshness, and SweepCount.
- `Unity.exe -projectPath D:\CSSALT\Thoracic-US-Coach-Standalone -executeMethod CSSALT.ThoracicUSCoach.Editor.ThoracicG2R3ConfirmationBatch.RunCli -logFile ...\confirmation_sweep_unity.log`
- `python g2_r2_interface_tracker.py --stage blind ...`
- `python g2_r3_confirmation.py ...` followed by `python g2_r3_evaluator.py ...` only after blind freeze.

## Evidence

- Offline comparison: `offline_development_comparison.json/.csv`
- E02 images/attribution: `e01_e02_e03_comparison.png`, `all_event_morphology_contact_sheet.png`, `evaluator_only/e02_attribution_evaluator.json`
- Confirmation acquisition: `confirmation_01/acquisition/`
- Blind actor output: `confirmation_01/actor_analysis/`
- Independent scoring: `confirmation_01/evaluator_only/anatomical_correspondence.json`
- Tests: `test_results/`
- Freeze: `preconfirmation_freeze.json`, `confirmation_01/actor_analysis/blind_results.sha256`
- Final gates: `final_gates.json`

## Next approval required

G2-R3 is complete and its single confirmation budget is consumed. The next smallest experiment would need separate approval to improve the primary candidate generator's yaw robustness while retaining this topology gate; no G4 physical return is justified until a frozen run yields seven image-supported events.
