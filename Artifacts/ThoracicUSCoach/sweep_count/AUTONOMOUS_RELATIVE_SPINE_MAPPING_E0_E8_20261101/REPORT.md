# Autonomous Relative Spine Mapping — E0–E4 final report

## Outcome

The campaign stopped at the first decisive scientific blocker after exhausting the safe authorized centerline, paramedian longitudinal, and oblique alternatives. The renderer produces causal combined-Skeleton signal and highly repeatable position-dependent images, but the images do not provide a recurrent vertebral-center/interlevel signature sufficient to construct seven anatomical events without evaluator labels.

No full fixed-Home development sweep, V1–V7 map, T7 handoff, physical return, or confirmation episode was executed.

## Implementation

- Added opt-in, read-only full-interface provenance to `UltrasoundManager`; it records the exact published scan's ordered interfaces and occlusion state without changing raycast or render ordering.
- Added isolated evaluator-only E0/E1, E2 and E4 controller partials and CLI batch entry points. Existing Active2D, SweepCount, guided/oracle and P1-R3 behavior was not modified.
- Added deterministic offline causal-attribution, geometry-screening and center/interlevel separability analyzers plus focused tests.
- Kept all anatomical labels, reference coordinates, mesh identities and renderer provenance under `evaluator_only/`. No autonomous actor or target-return adapter was created because G2 failed.
- The published review branch is `codex/autonomous-relative-spine-e0-e4`; its exact commit is recorded in the researcher handoff after publication.

## Verification

- Python focused tests: 8/8 PASS.
- Unity focused `RelativeSpineAuditTests`: 6/6 PASS on the final E4/oblique implementation.
- Unity regressions: Active2D safety 12/12 PASS; fresh capture 8/8 PASS; SweepCount milestone 3/3 PASS; acquisition-geometry recovery 7/7 PASS.
- Unity compilation completed through the graphics-capable CLI test path.
- 176 JSON evidence files parsed successfully; the source/configuration/evidence freeze hash is recorded in `final_evidence.sha256`.
- The Unity Personal editor license was Assigned/Unlimited. Only the separate headless `-batchmode` entitlement was unavailable; reported tests and captures used the licensed graphics-capable CLI path.

## E0 — anatomy and renderer

- Seven ordered evaluator-only T2–T8 label/LF reference pairs exist and project safely to skin.
- The level-specific `LF Tn` objects are ligamentum flavum, not vertebral bone renderers.
- Bone shadow pixels originate from one combined `Skeleton` mesh with no per-level bone identity.
- G0: `PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION`.

## E1 — causal observability

Same-pose counterfactual renders retained the original scene state after every capture.

- Combined Skeleton causal contribution: 7/7 reference regions.
- Level-specific LF causal contribution: 0/7 under locked criteria.
- Superficial muscle strongly contributes speckle at all levels.
- Full ray-interface provenance was read-only and had exact image parity when enabled versus disabled.

The result proves bone contributes pixels. It does not prove a pixel belongs to a named vertebra, rib, or lamina.

## E2 — geometry screening

Historical failures were retained for transverse center, transverse +6 mm, and midline longitudinal. New original-scene targeted acquisitions tested paramedian longitudinal +6 mm and −6 mm at yaw 90°.

- 28/28 frames valid, fresh, contact-valid, workspace-valid, and provenance-matched.
- Maximum yaw error: approximately `0.000023°`.
- Normal-alignment error: `0°`.
- Both geometries matched independent same-pose repeats to the correct evaluator-guided region 7/7.
- Minimum adjacent-minus-worst-repeat descriptor margin: `0.1197` (+6 mm) and `0.1586` (−6 mm).

This is an upper-bound pose-separability result. It does not localize unlabeled vertebrae.

## E4 — anatomical separability

At each promising geometry, 7 level-center and 6 interlevel-midpoint locations were captured twice. A final safe oblique candidate at −6 mm/yaw 60° was also tested.

All 78 E4 images passed safety, freshness, integrity, pose, and provenance checks. Each geometry reidentified 13/13 exact positions from the independent repeat. However, leave-anchor-out recurring center/interlevel classification failed:

| Geometry | Correct | Exact label-permutation p | Conclusion |
|---|---:|---:|---|
| Paramedian longitudinal −6 mm | 8/13 | 0.2920 | Not supported |
| Paramedian longitudinal +6 mm | 5/13 | 0.7080 | Not supported |
| Paramedian oblique −6 mm/yaw 60° | 8/13 | 0.2325 | Not supported |

The ultrasound contains stable spatial fingerprints, but those fingerprints do not form a common bone/interspace morphology across T2–T8. Therefore the seven-level prior cannot be applied without manufacturing or evaluator-selecting events.

## Stopping decision

E3 was not run because no defensible event existed whose physical width could be measured. E5–E8 were not run because a full sweep could only produce position-dependent image change, not independently supported vertebral events. No full-sweep or confirmation budget was consumed.

## Direct researcher answers

1. The renderer generates anatomically caused combined-Skeleton signal, but not independently attributable per-level vertebral signal.
2. Paramedian longitudinal −6 mm gave the strongest exact-reference separation; none gave recurrent anatomical semantics.
3. Seven distinct vertebral structures/transitions were not detected.
4. Stable intensity/gradient morphology and Skeleton shadow geometry explain position repeatability, not vertebral identity.
5. The recurring structural-event test failed exact permutation controls.
6. No accepted relative T2–T8 map was constructed.
7. T2–T8 predicted coordinates are all null.
8. Coordinate uncertainties are all null/unbounded.
9. No coordinates were copied from Unity labels into actor output.
10. V6/T7 was not assigned.
11. No relative coordinate was supplied to navigation.
12. The probe did not execute a T7 return.
13. No final fresh T7 ultrasound was acquired.
14. No intended T7-right transverse view was acquired.
15. Independent evaluation concludes G2 FAIL; G3/G4 NOT_RUN.
16. Complete end-to-end PASS episodes: 0/0.
17. G0 PASS with identity limitation; G1 PASS from reused full-coverage evidence plus current safety; G2 FAIL; G3/G4 NOT_RUN.
18. Exact blocker: the unchanged renderer/acquisition candidates provide reproducible spatial appearance but no cross-level recurrent morphology that identifies seven vertebral centers or transitions without privileged reference positions.

## Run accounting

- Diagnostic Unity runs: 5 total (E1, E2 D/E, E4 −6, E4 +6, E4 oblique).
- New geometry configurations: 3 (+6 longitudinal, −6 longitudinal, −6/yaw60 oblique).
- New full physical development sweeps: 0/6.
- Frozen end-to-end confirmations: 0/3.
- Safety or freshness violations: 0.

## Scientific gates

- G0: PASS with combined-Skeleton identity limitation.
- G1: PASS.
- G2: FAIL.
- G3: NOT_RUN.
- G4: NOT_RUN.

The smallest evidence-backed next step would require separate approval for improved per-level anatomical rendering/provenance or a scene/anatomy representation change. More threshold tuning or finer sampling of the same ambiguous signal is not justified.
