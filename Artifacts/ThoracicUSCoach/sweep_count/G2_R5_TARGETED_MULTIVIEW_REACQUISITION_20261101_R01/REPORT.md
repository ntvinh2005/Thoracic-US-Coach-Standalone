# G2-R5 targeted multi-view reacquisition

## Status at source freeze

`READY_FOR_ONE_BOUNDED_DIAGNOSTIC_SESSION`

G2-R5 extends the G2-R4 actor-only uncertainty policy to select two weak image-event hypotheses and one stable unresolved interface. Selection uses only clean-image candidate evidence, executed Home-relative motion, traversal support, and morphology. It does not use evaluator anatomy coordinates or labels.

The diagnostic runtime physically navigates from the same fixed, anatomically unlabeled Home to the three frozen Home-relative windows, obtains forward/reverse yaw-90/yaw-91 observations, and adds one 60 mm-depth view only for the stable distal-field ambiguity. It restores depth and movement settings and physically returns Home. It does not execute a full sweep or G4 return.

## Preflight evidence

- Unity 6000.3.9f1 license: legitimate Unity Personal entitlement active.
- Compilation: PASS.
- Focused Unity EditMode tests: 41/41 PASS.
- Relevant Python regressions: 66/66 PASS.
- Actor region selection: three regions frozen; estimated 163 fresh frames, under the 240-frame limit.
- Scene provenance: resolved. Physical execution must use isolated scene SHA-256 `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`, matching G2-R2/R3 acquisitions.
- Anatomy/renderer assets: unchanged.
- Physical diagnostic sessions consumed at this checkpoint: 0/1.

## Scientific classification rule

- A weak hypothesis can become `SUPPORTED_RECESS_EVENT` only after new distinct-position, bidirectional observations satisfy the existing pixel-shadow morphology.
- A stable unresolved interface can become `REJECTED_SECONDARY_OR_DUPLICATE_INTERFACE` only when at least four independent 60 mm-depth observations, spanning at least two physical positions and both directions, have adequate distal pixels but consistently lack the locked shadow morphology.
- Added depth revealing some shadow does not automatically make the stable interface a vertebral event; it remains ambiguous.
- Exact duplicate images and multiple contour hypotheses from one frame cannot increase independent support.

## Scientific gates before capture

- G0: historical PASS with combined-Skeleton identity limitation.
- G1: pending targeted physical session.
- G2: remains FAIL from historical evidence; R5 is diagnostic and cannot by itself establish a held-out full-pipeline PASS.
- G3: NOT_RUN / no T7 target.
- G4: NOT_RUN and outside this protocol.

This report will be completed with actual movement, capture, fusion, evaluator, and readiness results after the single authorized session.
