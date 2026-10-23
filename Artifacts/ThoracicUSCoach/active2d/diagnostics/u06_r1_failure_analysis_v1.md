# U06-F01 corrected failure attribution and contract audit

Result: **PASS**

Base commit: `5fcfb24776a440950c6af0adb3ad305ef4e8c621`. Frozen former-test accuracy 61.73%; T7 TP=11, FN=83, total=94; invariant PASS.

The old `full_correct=7` was an evidence-path leaf, not the final TP count. Its precedence first removed non-MATCHED and prototype-only-non-T7 frames, including some final T7 predictions recovered by temporal inference. Outcome and overlapping evidence are now independent.

All 40 manifests are family-disjoint and valid. All 1,920 non-reset transitions align physically with the recorded action into the current image; rejected moves are not stored. Offline replay has no evaluator-input leak.

The frozen model remains rejected; the former test is consumed and cannot support a new heldout claim.
