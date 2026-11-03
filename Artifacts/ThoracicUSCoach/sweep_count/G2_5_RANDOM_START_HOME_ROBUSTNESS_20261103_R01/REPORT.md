# G2.5 Random-Start Return-to-Home and Home-Position Robustness

## Outcome

PASS for the bounded fixed-anatomy simulator milestone. The robot was initialized at a seeded nonzero longitudinal position, used only positioning-relative displacement and safe Active2D actions to return physically to the episode Home, and began landmark exploration only after Home arrival was verified. A new frozen confirmation with an unseen small Home perturbation produced seven image-supported events, zero accepted false events, an unambiguous ordered map, and a non-null T7 target.

This does not establish arbitrary-start or patient-level robustness. The demonstrated envelope is the same safe longitudinal corridor, random longitudinal initialization up to 30 mm from the episode Home, and small Home perturbations represented in development by lateral offsets from -52.0 to -55.0 mm. The passing confirmation used an unseen -54.0 mm corridor. T2/T8 names remain conditional on the controlled-region prior that the seven consecutive landmarks are T2-T8.

## What changed

- Added opt-in seeded random initialization on the mechanically traced skin path.
- Added physical positioning-driven return to the episode Home before exploration.
- Excluded all pre-Home positioning frames from landmark inference while retaining them as motion, safety, freshness, and integrity evidence.
- Added seeded longitudinal and lateral episode-Home perturbations without LF/T-level lookup.
- Expanded the pixel-only ExtraTrees development distribution across three whole physical sessions with per-session evaluator labels isolated to training.
- Added symmetric image-only handling of edge-clipped likelihood peaks: classifier score, bidirectional support, inward prominence, and duplicate exclusion are required.
- Added an option to stop Play Mode but leave Unity Editor open after a run.

No anatomy, Skeleton, scene, renderer, material, collider, Active2D baseline, or P1-R3 controller was modified.

## Physical executions

| Episode | Role | Initial relative position | Episode Home perturbation | Moves | Frames | Blind result |
|---|---|---:|---|---:|---:|---|
| development_session_01 | development | +21.000 mm | longitudinal -9.003 mm; lateral -1.5 mm | 257 accepted / 0 rejected | 259 fresh unique | old model 4/7; expanded model 7/7 |
| frozen_confirmation_random_home | frozen v1 | +24.002 mm | longitudinal +9.000 mm; lateral +0.5 mm | 259 accepted / 0 rejected | 261 fresh unique | FAIL: 6/7, T2 missed, no T7 target |
| frozen_confirmation_v2_random_home | frozen v2 | -30.000 mm | longitudinal -6.003 mm; lateral -0.5 mm | 263 accepted / 0 rejected | 265 fresh unique | PASS: 7/7, 0 FP, non-null T7 |

The v1 failure was retained. Its T2 image likelihood exceeded the classifier threshold but was clipped by the coverage edge, so the original two-sided peak-prominence calculation did not produce a candidate. The subsequent development rule treats both edges identically and requires an inward image-likelihood decline; it does not insert an event based on count or coordinate.

## Frozen v2 motion and acquisition

- Random start: -30.000244 mm relative to episode Home.
- Initial physical Home return: 20 accepted actions, 0 rejected.
- Home arrival residual before coordinate reset: 0.000011 mm.
- Exploration acquisition: yaw 89.5 degrees, width 45 mm, 1.5 mm nominal step.
- Exploration movements: included in 263 total accepted actions; 0 rejected.
- Freshness: 265/265 unique hashes and 265/265 unique scan serials.
- Final physical Home residual: 1.505342 mm, within the locked 3 mm episode-return tolerance.
- Unity remained open after Play Mode completed for researcher inspection.

## Independent per-level evaluation

| Level | Actor Home-relative mm | Evaluator mm | Absolute error mm | Margin to 5 mm | Result |
|---|---:|---:|---:|---:|---|
| T2 | -80.500763 | -82.485299 | 1.984536 | 3.015464 | PASS, edge-sensitive |
| T3 | -64.500763 | -60.209218 | 4.291545 | 0.708455 | PASS, fragile |
| T4 | -36.000763 | -35.883525 | 0.117237 | 4.882763 | PASS |
| T5 | -9.500763 | -9.731881 | 0.231118 | 4.768882 | PASS |
| T6 | +17.499237 | +17.504532 | 0.005295 | 4.994705 | PASS |
| T7 | +45.499237 | +45.579052 | 0.079815 | 4.920185 | PASS |
| T8 | +75.999237 | +76.030396 | 0.031159 | 4.968841 | PASS |

Metrics: 7 TP, 0 FP, 0 FN, precision 1.0, recall 1.0, F1 1.0, maximum error 4.291545 mm. The actor emitted T7 at +45.499237 mm relative to this episode's perturbed Home.

## Information boundary

Runtime initialization, Home return, corridor tracing, movement budgets, capture, event proposals, and stopping use no LF object, T-level transform, evaluator coordinate, or calibrated T7 pose. The actor sees ultrasound pixels, actual achieved displacement, Home-relative positioning, acquisition configuration, and safety feedback. The supervised model used evaluator coordinates only to label three disclosed whole-session development datasets. Frozen confirmation anatomy was loaded only after candidate and map SHA-256 hashes were recorded.

## Verification

- Python focused: 29 PASS, 0 FAIL.
- Python full discovery: 386 tests, 385 PASS, 1 historical-data skip, 0 FAIL.
- Unity licensed EditMode final: 16/16 PASS.
- Unity compilation: PASS.
- Development physical sessions: 1.
- Frozen confirmation sessions: 2 (one retained FAIL, one PASS).
- Scene used for physical execution: SHA-256 `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`.
- Model SHA-256: `973723e45b6ac6f54b56b64be69fbd67c890537c665ceea2240860d02b6b8dfe`.
- Blind v2 candidates SHA-256: `de4925dbc9a689e7bfb1842ee63b4f64fd9bb68b35870db2db5af304882bddb4`.
- Blind v2 map SHA-256: `35fc36659d1f265e3c3cc50bed53b52c655db474489b6cf9fd0919af433984`.

## Gates and limitations

- G0: PASS with the historical combined-Skeleton identity limitation.
- G1: PASS for random-start physical Home return, full safe coverage, fresh capture, and final Home return.
- G2: PASS for this bounded fixed-anatomy Home-position envelope.
- G3: PASS as conditional T2-T8 enumeration; V6/T7 is non-null.
- G4: NOT_RUN; no physical T7 return was authorized.

The strongest remaining weakness is T3 at 4.292 mm, only 0.708 mm inside tolerance. T2 is also close to the cranial coverage edge. One passing confirmation establishes preliminary simulator feasibility, not reliability. A larger locked multi-seed evaluation is required before claiming robust probability of success.

## Evidence

- Protocols: `frozen_confirmation_protocol.json`, `frozen_confirmation_protocol_v2.json`.
- Model and disclosed fit: `actor_only_evidence/`.
- Final map: `relative_spine_map.json`.
- Event candidates: `landmark_detection_results.json`.
- Physical actor trace and frame hashes: `frozen_confirmation_v2_random_home/`.
- Independent evaluation: `frozen_confirmation_v2_random_home/evaluator_only/actor_score/`.
- Actual ultrasound contact sheet and plots: `final_evidence/`.
- Raw local PNGs remain at `D:/CSSALT/Thoracic-US-Coach-Standalone/Artifacts/ThoracicUSCoach/sweep_count/G2_5_RANDOM_START_HOME_ROBUSTNESS_20261103_R01/frozen_confirmation_v2_random_home/frames/` and are represented by the committed frame manifest rather than duplicated in Git.
