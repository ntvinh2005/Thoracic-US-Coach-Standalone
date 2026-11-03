# G2-R6.1 repaired cross-plane anatomical discrimination

## Scientific verdict

`INCONCLUSIVE_ACQUISITION`

Stage A passed and the single authorized Stage B session completed safely. The yaw-65 plane produced fresh, visibly different, anatomically relevant images, but the frozen `CROSS_VIEW_INTERFACE_ENTRY_EXIT_TOPOLOGY_V1` feature could not be measured uniformly for the weak cranial event, stable secondary interface, and positive control. The actor therefore abstained; no seven-level map and no T7 target were emitted.

## Stage A

The prior 17 baseline frames did not fail tracking. All 17 had a valid interface and valley. Their projected locations clustered near -75 to -76 mm, 6.43–8.20 mm from the weak-event hypothesis at -83.039 mm, so the locked 4 mm association filter removed them before topology output. The earliest failure was target-window matching. R6.1 now preserves image-local topology before association without promoting those measurements to the target event.

The capture path now verifies integrity/contact/workspace/provenance, persists PNG and metadata, then records quality measurements. A negative informativeness result becomes an explicit status. Direct in-episode `SetPositionAndRotation` cleanup was removed; normal and recoverable abort returns use logged accepted physical actions. Final status serialization occurs after recovery and settings restoration.

Stage A verification:

- Python G2 regressions: 148/148 PASS.
- Unity compilation: PASS under licensed Unity Personal graphics Editor.
- Focused EditMode R6.1/R6/R5/R2/Active2D/SweepCount/freshness: 50/50 PASS.
- Full EditMode diagnostic: 159/161; one R6.1 string expectation was corrected, and one unrelated pre-existing prefab-component test failed. The focused in-scope suite then passed completely.

## Frozen Stage B protocol

The actor selected the same three image-derived regions used by R6: unresolved weak event, stable secondary-interface hypothesis, and supported-event control. No T label, LF identity, Unity coordinate, evaluator score, or calibrated T7 entered selection or acquisition.

The alternative plane was frozen at M6 yaw 65 degrees and depth 60 mm. It lies within the previously contact-valid yaw 60–90 interval, does not repeat failed yaw 60, and is 25 degrees from longitudinal yaw 90. The historical global MAE threshold stayed 0.020 as a diagnostic. The predeclared repaired quality rule also considered central-ROI MAE and local-gradient MAE, then required offline interface measurability.

## Physical execution

Exactly one diagnostic session ran:

- 228/228 accepted physical movements; zero rejected.
- Zero contact, workspace, or safety failures.
- 110/110 fresh, integrity-valid, unique frames.
- 55 yaw-90 frames and 55 yaw-65 frames.
- 55 matched cross-view position pairs; maximum actor-coordinate mismatch 0.00136 mm.
- Physical return to Home: PASS; final actor-relative coordinate +0.00396 mm.
- Final status: `COMPLETED`; no direct pose recovery was used.

The first yaw-65 image was persisted before quality evaluation. Its global MAE was 0.016379, below the historical 0.020 threshold, while central-ROI MAE was 0.033559 and local-gradient MAE was 0.002096. It therefore passed the predeclared region-aware evidence-retention rule. Unlike R6, the complete alternative-view stream was retained.

## Actor-only topology result

The actor extracted 159 image-local topology measurements, of which 44 associated within the unchanged 4 mm target radius.

| Region | Baseline evidence | Yaw-65 evidence | Frozen conclusion |
|---|---:|---:|---|
| Weak cranial event | 0 associated observations | 1 observation, one direction/position | Inadequate |
| Stable secondary interface | 21 observations, 11 positions, both directions | 18 observations, 10 positions, both directions | Complete signature |
| Positive recess control | 0 associated observations | 4 observations, 2 positions, both directions | Inadequate |

The alternative view visibly changed interface entry/exit topology, shoulders, and neighboring bright structures, but only the secondary interface had adequate measurements in both planes. Because the weak event and positive control lacked baseline signatures, the same rule could not compare all three structures. The actor verdict was frozen as `INCONCLUSIVE_ACQUISITION` before evaluator access.

## Independent evaluator review

Blind output hashes verified before evaluation. Evaluator provenance showed combined-Skeleton shadow in every frame for all three regions and both planes. The yaw-65 plane therefore did expose relevant bone-shadow anatomy, not a blank or invalid field.

Historical validated correspondence identifies the weak hypothesis with T2 (prior error 3.584 mm), the control with T3 (0.397 mm), and the stable interface as unmatched to T2–T8 within 5 mm. This identity was not available to the actor. The evaluator could not validate an image-only discriminator because the frozen feature was not uniformly measurable. Combined-Skeleton identity remains a provenance limitation.

## Answers to the required questions

1. **Why did the previous 17 frames yield zero measurements?** Every frame tracked an interface and valley; all valleys failed the 4 mm target association, so topology was discarded after projection.
2. **Failure type?** Target matching/association, not acquisition, tracking, valley extraction, or a coordinate-sign change.
3. **Persistence fixed?** Yes; valid PNG/provenance persistence now precedes quality evaluation.
4. **Direct-pose cleanup removed?** Yes from this diagnostic episode path.
5. **Failure/recovery tests?** PASS in the 50/50 focused Unity suite, including capture/safety classification, quality-inconclusive state, recovery-failed state, movement logging, and no direct pose reset.
6. **Alternative plane?** M6 yaw 65°, depth 60 mm; selected as a safe interpolated orientation with a 25° plane change and no repetition of yaw 60.
7. **Physical session safe?** Yes, 228 accepted movements, zero safety failures, physical Home return PASS.
8. **Images?** 55 baseline plus 55 alternative, all fresh and unique.
9. **New anatomy?** It revealed different combined-Skeleton interface/shadow structure, but not uniformly target-associated evidence.
10. **Weak versus secondary discrimination?** No. Only the secondary had a complete cross-plane signature.
11. **Evaluator support?** Evaluator confirmed relevant bone-shadow anatomy and the different physical identities, but not a usable uniform pixel discriminator.
12. **Uniform features?** Yes; the identical tracker, association radius, and topology features were applied to every region.
13. **New ambiguities?** No event was falsely accepted, but overlapping/competing interfaces remained and prevented target association in weak/control regions.
14. **Ready to freeze end-to-end G2/G3?** No.
15. **Remaining limitation?** The approximately 46–50 mm longitudinal footprint exposes multiple structures; the dominant valley is not consistently the actor-selected weak/control landmark. The current pixel tracker cannot establish cross-view identity for those landmarks.
16. **Recommended next action?** Stop incremental threshold tuning. Either run a separately approved acquisition study that materially reduces longitudinal footprint and demonstrates target-specific cross-view correspondence, or switch to a clearly labeled constrained/anchored PoC. Do not proceed to G4 from this map.

## Gates

- G0: historical PASS with combined-Skeleton identity limitation.
- G1: PASS for this session.
- G2: FAIL.
- G3: INCONCLUSIVE; `predictedT7RelativeMm = null`.
- G4: NOT_RUN.

Historical T7 diagnostic error remains 4.972 mm against a 5 mm tolerance and was not improved or retested here.

## Source and evidence

- Branch: `codex/g2-r6-1-repair-crossplane`
- Stage A frozen commit: `b897d8f562e120678357dc786d451e09444afd1d`
- Execution scene SHA-256: `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`
- Root: `Artifacts/ThoracicUSCoach/sweep_count/G2_R6_1_REPAIRED_CROSSPLANE_20261103_R01/`
- Actor trace and 110 raw PNGs: `session_01/`
- Actor freeze: `blind_results.sha256`
- Evaluator interpretation: `evaluator_only/anatomical_interpretation.json`
- Human-readable figures: `cross_plane_comparison.png`, `interface_topology_comparison.png`
