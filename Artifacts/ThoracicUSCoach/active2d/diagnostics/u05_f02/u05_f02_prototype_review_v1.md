# U05-F02 prototype anatomy and observability review

Result: **EVIDENCE READY**; anatomical claims remain **HUMAN_REVIEW_PENDING**.

## What the images support

P0/P3/P6/P7/P14 show repeatable renderer appearance motifs, but mostly the same superficial speckle wedge and bright curved lower reflector. T6/T7 overlap and single-anatomy data do not support naming these as rib, transverse process, lamina, or a vertebral identity.

P5: Development coverage gap: no dev observation selected P5 even as its nearest prototype. Therefore absence is not caused by P5 radius rejection or ambiguity within dev; there were no P5-nearest dev candidates. Former test has 14 P5-nearest observations (12 matched, 2 ambiguous), showing family/split-specific appearance coverage.

## Review order

1. Open View A and record similarity/visible-structure judgments without level labels.
2. Open View B and compare evaluator levels, split/family, distance, and status.
3. Mark candidate structures only as uncertain unless a qualified reviewer confirms them.
4. Check whether near-radius and ambiguous examples differ anatomically or only by renderer texture/window.

## F03 recommendation (not implemented)

1. E — hybrid direct-image + optional landmark memory. Falsify if a new locked-family test fails to improve precise handoff or reduces direct-image level accuracy.
2. B — repair prototype clustering/assignment. Falsify if cross-level entropy/coverage improves but T7 recall and handoff do not.
3. C — add landmark-specific supervised signals. Falsify if clinician-reviewed landmark labels are not reproducible or add no locked-test value.
4. D — repair temporal/action use after visual evidence improves. Falsify if no-action ablation remains equal/better on a new locked test.
5. A — broad representation replacement is lower priority because the preserved direct head is already strong. Promote only if representation ablation improves independent-family calibration and T7 discrimination.

No retraining, threshold change, controller edit, or deployment was performed.
