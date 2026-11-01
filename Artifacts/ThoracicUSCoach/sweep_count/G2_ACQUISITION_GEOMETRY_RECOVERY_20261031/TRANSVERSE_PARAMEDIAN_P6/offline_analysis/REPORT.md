# Autonomous T7 Discovery from Fixed Home — Offline Observability Gate

## IMPLEMENTATION

Added one isolated blind-first analyzer. It derives actor-relative position only from accepted achieved motion, extracts four deterministic image-morphology families, compares exact sequence segmentations K=1…9, and audits a diagnostic K=7 partition with smooth-trend nulls, feature ablation, forward/reverse captures, adjacent-state effects, and capture-noise jitter. It does not modify Unity, Active2D, P1-R3, the renderer, the scene, or historical evidence.

The old known-T2-start assumption is explicitly disabled. Home is a repeatable but anatomically unlabeled position. The approved seven-level coverage prior is used only as a candidate K=7 hypothesis; it is not allowed to create accepted events when the image-evidence gates fail.

## VERIFICATION

- M0-B actor trace and 103 frame hashes: **PASS**.
- Actual primary observations: **63**; separately captured reverse-query observations: **40**.
- Physical movements in source data: **102 accepted, 0 rejected**.
- New physical sweeps: **0/20**. Confirmation episodes: **0/3**.
- Blind output hash freeze before evaluator access: **PASS**.
- Focused tests: see `focused_test_results.txt`.

## OFFLINE ANATOMICAL WAVEFORM

The descriptor waveform is spatially repeatable, but the seven-state interpretation is **`NO_DEFENSIBLE_SEVEN_LEVEL_SEQUENCE`**. BIC selected K=4, not a presupposed seven. The K=7 SSE reduction was 0.460; against a cubic smooth-trend plus correlated-noise null, p=0.8602 (null 95%=0.641).

The forced diagnostic K=7 partition is retained only for review. Accepted vertebral events: **0**. It is not converted into V1–V7, and no T7 coordinate is emitted.

## CONTROLS

- Forward/reverse segment-label accuracy: **0.750** over 40 genuine recaptures.
- Maximum boundary shift after leaving out one feature family: **3.002 mm**.
- Minimum boundary stability under recapture-derived jitter: **1.000**.
- Failed predeclared gates: **allAdjacentEffectsSupported, k7ExceedsSmoothTrendNull, k7SelectedByBic, reverseTraversalSupported**.

High image repeatability at the same coordinate is useful, but it cannot by itself establish seven vertebral identities. The combined Skeleton provenance remains unable to distinguish ribs, laminae, interspaces, or vertebral bodies.

## PHYSICAL EXECUTION DECISION

The offline gate did not authorize a Unity exploration-to-return pilot. Running a new sweep would reproduce safe images without resolving whether seven observed structures are vertebrae. P1-R3 was therefore not invoked, no target coordinate was supplied, and no calibrated pose was reused.

## SCIENTIFIC VERDICTS

| Gate | Verdict |
| --- | --- |
| G0 anatomical reference validity | **PASS** |
| G1 physical coverage | **PASS** |
| G2 anatomical event observability | **FAIL** |
| G3 enumeration accuracy | **NOT_RUN** |
| G4 T7 inference and physical reacquisition | **NOT_RUN** |

Overall end-to-end result: **NOT_RUN — OFFLINE G2 BLOCKER**.

## HIGHEST-PRIORITY BLOCKER

Existing clean ultrasound frames contain reproducible position-specific morphology, but they do not provide a defensible, independently attributable set of seven vertebral-level structures. K=7 can always partition a smooth changing sequence; the predeclared controls are what prevent that mathematical partition from being mislabeled as anatomy.

## NEXT APPROVAL REQUIRED

No ordinary parameter tuning is justified on this same dataset. A next experiment would need a separately approved source of independent anatomical observability—such as a diagnostic acquisition geometry whose image signature can be validated per structure—without exposing T-level coordinates to the actor. No alternative architecture is implemented here.
