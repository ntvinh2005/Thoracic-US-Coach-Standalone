# G2-R4 Multi-Hypothesis Tracking and Active Reacquisition

## Scientific outcome

Offline readiness **FAIL**. R4 preserves seven evaluator-matched events on each yaw-90 development sweep and does not accept the recurrent deep interface. It does not recover sufficient T2/T3 evidence on yaw 91 degrees: 5 TP, 0 FP, 2 FN, precision 1.000, recall 0.714, F1 0.833. The actor emits no T-level map and no T7 coordinate. No new physical sweep was permitted by the readiness gate.

## Failure-point audit

The primary tracker loses T2/T3 evidence because the globally selected lower bright path changes which visible interface supplies the local valley after the one-degree yaw change. The T2-like hypothesis has three supporting frames; the T3-like hypothesis has two outbound frames and no reverse support. Both survive coordinate projection and clustering. The earliest failure is therefore contour identity/local valley extraction, followed by the scientifically correct support-validation abstention—not projection.

The bounded H1 edge-dominant and H2 intensity-dominant paths recover weak evidence on DEV01 but do not create stable yaw-91 cranial evidence. They preserve the five caudal events and do not multiply same-frame evidence because density is capped per frame.

## Morphology and the secondary interface

R4 distinguishes distal image availability from verified shadow morphology. Actual shadow support requires post-interface darkness, onset contrast and persistence in neighboring columns. The deep E02-like interface has inadequate distal pixels, so its acoustic-shadow topology is **not verified**. It is not accepted as a recess, but it remains semantically ambiguous rather than being erased as a known false structure. No evaluator coordinate participates in this judgment.

## Development comparison

DEV01 improves to 7/0/0 from the R2 6/1/1 result; DEV02 remains 7/0/0; DEV03 improves from R2 7/1/0 to 7/0/0. YAW91 remains 5/0/2. Detailed baseline/R3/R4 metrics, runtimes and peak memory are in `offline_development_comparison.*`. The five-millimeter T7 diagnostic remains fragile: errors are 4.943–4.973 mm, leaving only 0.027–0.058 mm margin. No evaluator-derived offset was applied.

## Active reacquisition

The actor-only planner is implemented and selects at most two Home-relative windows from ambiguous image candidates using frame support, missing traversal direction and pixel shadow evidence. It does not load anatomy coordinates. For YAW91 it selects the two cranial unresolved hypotheses after regeneration. In-session Unity reacquisition is NOT_RUN because readiness failed; therefore no claim is made that the visits resolve uncertainty.

## Required answers

1. T2/T3 were lost through contour-identity/local-valley instability under yaw 91.
2. The earliest failure was tracking/valley evidence, not projection or clustering.
3. Multi-hypothesis tracking did not recover stable yaw-91 T2/T3 support.
4. The verifier uses local post-interface darkness, onset contrast and neighboring-column persistence.
5. Real shadow morphology is checked where distal pixels exist; E02 has only an availability limitation, not verified shadow.
6. No extra event is accepted, but the deep interface remains ambiguous.
7. All seven references survive DEV01/02/03; only five survive YAW91.
8. Actor-driven local rescan selection is implemented but not physically executed.
9. Revisit windows come only from actor-relative ambiguous image hypotheses.
10. Revisit resolution is NOT_RUN.
11. No frozen physical sweep was executed.
12. No new blind run exists; actor/evaluator isolation holds in offline processing.
13. The actor did not infer all seven positions on YAW91.
14. The final sequence is INCONCLUSIVE.
15. `predictedT7RelativeMm` is null.
16. Independent T7 error is not scoreable for an emitted target; diagnostic event error is 4.972 mm.
17. G2-R4 FAIL.
18. G3-R4 NOT_RUN because the actor abstained.
19. Before G4, a frozen actor-only episode must recover T2/T3 and resolve any enumeration-changing extra interface.
20. The smallest blocker is missing bidirectional local recess evidence at yaw 91; the next discriminating experiment is a separately approved localized actor-derived rescan, not a lower gate or another whole-sweep search.

## Execution and gates

Branch: `codex/g2-r4-multi-hypothesis-reacquisition`; baseline: `e15bab3`. Changed source files are the R4 tracker, offline audit and focused tests. Physical sweeps: 0; physical revisits: 0. Python tests passed 38/38. Two Unity batch attempts reached the licensing client but stopped before compilation with `No valid Unity Editor license found`; Unity compilation/EditMode verification is therefore `NOT_RUN_LICENSE_BLOCKED`, not PASS or FAIL. A minimal diagnostic excerpt is preserved under `test_results/` without publishing machine/network details from the full local logs.

The R4 branch scene, Skeleton and renderer files were not edited. Their hashes are recorded in `source_configuration_hash_manifest.json`. The branch scene hash (`60fef5…`) differs from the historical R2/R3 execution-worktree scene hash (`ce9527…`); this pre-existing baseline/provenance difference was detected before any R4 Unity execution. Skeleton and renderer hashes match the historical freeze. G0 historical PASS with identity limitation; G1 historical PASS; G2-R4 FAIL; G3 NOT_RUN; G4 NOT_RUN.
