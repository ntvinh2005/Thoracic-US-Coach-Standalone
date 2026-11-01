# G2-R2 Continuous Recess Mapping — Final Report

## Outcome

G2-R2 did not pass. The frozen third sweep produced eight blind, reverse-supported interface-valley events. Independent evaluation matched seven of them one-to-one to the validated LF-associated posterior bone recess regions, but one additional recurrent image event at `-73.964 mm` was not a validated level. The actor correctly abstained, emitted no T7 coordinate, and no physical return was executed.

Final gates: G0 `PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION`; G1 `PASS`; G2 `FAIL`; G3/G4 `NOT_RUN`.

## Implementation

- Added an isolated M6 continuous sweep controller and CLI runner. It starts from fixed anatomically unlabeled Home, uses physical 1.5 mm surface-following motion, captures after accepted moves, integrates actual achieved displacement, reverses physically, and restores all movement settings.
- Added actor/evaluator trace separation and exact publication provenance checks.
- Added a blind deterministic detector: continuity-constrained lower bright-interface tracking, local recess-valley extraction, projection through the configured ultrasound footprint, spatial duplicate consolidation, reverse-pass confirmation, and conservative abstention unless exactly seven supported events remain.
- Added a quality gate rejecting clusters that merge incompatible interface-depth populations. This gate uses image coordinates only.
- Preserved Active2D, SweepCount, P1-R3, scene, anatomy, renderer, and all historical evidence.

## Verification

- Python G2-R2 tests: 20/20 passed after the final detector freeze.
- Unity compilation: passed; warnings were pre-existing SDK/type warnings.
- Unity EditMode preflight before sweeps 2 and 3: 29/29 passed, covering G2-R2, Active2D safety, DirectFreshCapture/freshness, and SweepCount regressions.
- Original scene SHA-256 remained `ce9527...131`; Skeleton FBX remained `df34d6...9d4e`; UltrasoundManager remained `2f170e...220e0`.
- Four early publication-worktree launches failed before any motion or capture because the primary renderer/manager was unavailable. They consumed no complete physical sweep but remain recorded.

## Physical sweeps

Three complete development sweeps were consumed (the full approved budget). Each had 220 accepted and 0 rejected moves, 221 fresh frames, actor-relative coverage `-88.502` to `+76.504 mm`, maximum yaw error `0.0000229°`, normal-alignment error `0°`, and final Home return residual `0.001029 mm`. No unhandled contact, workspace, freshness, or provenance failure occurred.

## Image feature and event semantics

The useful feature is a downward valley of a continuity-tracked lower bright interface followed by acoustic shadow. The same physical interface is visible in many frames because the rendered longitudinal footprint is `50.2336 mm`, so each pixel valley is projected into actor-relative space and duplicate observations are consolidated. A candidate is accepted only with multi-frame support, independent outbound/reverse support, compact spatial location, and coherent interface depth.

This is evidence for recurring LF-associated posterior bone-recess morphology, not segmented vertebral-body centers. The combined Skeleton still limits substructure identity.

## Frozen blind result

The detector source was frozen before sweep 3 (tracker SHA-256 `780f35...412`). The actor produced these blind candidates:

| Candidate | Relative mm | Evaluator result | Error mm |
| --- | ---: | --- | ---: |
| E01 | -88.677 | T2 | 2.054 |
| E02 | -73.964 | Unmatched recurrent structure | — |
| E03 | -61.350 | T3 | 2.208 |
| E04 | -42.773 | T4 | 4.017 |
| E05 | -14.099 | T5 | 1.678 |
| E06 | +17.526 | T6 | 2.674 |
| E07 | +47.920 | T7 | 4.942 |
| E08 | +77.528 | T8 | 3.776 |

Metrics: TP 7, FP 1, FN 0, precision 0.875, recall 1.0, F1 0.9333, exact-seven sequence false. The locked R1 extra recess challenge near actor coordinate `-23.695 mm` was not accepted; the observed false event is a different recurrent interface.

Because there were eight supported candidates, `relative_spine_map.json` intentionally leaves T2–T8 coordinates null and `predictedT7RelativeMm` null. Although E07 lies near evaluator T7, selecting it after evaluator review would violate the actor/evaluator boundary.

## Development history and instability

- Sweep 1: the physically corrected footprint produced seven candidates, but only six matched; T2 was missed and a non-reference event was counted (F1 0.857).
- Sweep 2: after the image-only depth-coherence gate, the development data produced seven correct matches. This was not treated as held-out success.
- Sweep 3 frozen: the same non-reference event remained depth-coherent and survived, producing eight events and mandatory abstention.

This demonstrates that the blocker is not physical coverage or freshness. The current valley-only event semantics cannot reliably distinguish a true LF-associated recess from another repeatable posterior interface.

## Required questions

1. A real continuous M6 sweep ran: yes, three complete sweeps.
2. T2–T8 corridor safely covered: yes, independently verified; G1 pass.
3. Images fresh and movement-matched: yes, 663/663 frames across complete sweeps.
4. Feature: continuity-tracked bright posterior interface valley plus shadow, footprint-projected and reverse-confirmed.
5. Frozen candidate count: eight.
6. Supported as distinct image events: eight; anatomically validated levels: seven.
7. All seven levels found without actor anatomy coordinates: evaluator found a one-to-one match for all seven, but actor could not know which one of eight to reject.
8. Locked `+33.50 mm` geometry challenge counted: no. A different non-reference event was counted.
9. Accepted T2–T8 coordinates: all null because the actor abstained. Diagnostic candidate coordinates are listed above.
10. V6 correctly inferred as T7: no accepted enumeration; G3 not run.
11. Valid T7 target emitted: no.
12. Physical return: not run.
13. Final fresh T7 capture: not run.
14. T7-right transverse/axial view: not run.
15. Independent evaluation: seven true recesses plus one false event; G2 fail.
16. Episodes: 3 development, 0 confirmation.
17. Gates: G0/G1 pass with stated limitation; G2 fail; G3/G4 not run.
18. Exact blocker: the image-only valley representation has insufficient semantics to reject a stable non-level posterior interface without privileged anatomy coordinates.

## Smallest next decision

Do not add thresholds to delete E02 by coordinate or force K=7. The smallest discriminating next experiment is evaluator-only causal attribution of E02's beam/pixel contribution, followed by one predeclared complementary image feature that distinguishes that structure from the seven validated recesses (for example component termination topology or shadow-onset continuity). This requires a new approved protocol because the three-sweep development budget is exhausted.

No confirmation or G4 handoff is justified from the present evidence.
