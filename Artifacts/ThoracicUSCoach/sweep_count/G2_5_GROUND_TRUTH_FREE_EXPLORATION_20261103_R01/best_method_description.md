# Best method

The best retained recognition method is the frozen patch-likelihood pipeline from the prior autonomous discovery campaign:

1. Extract multiple bright-interface hypotheses from each clean ultrasound image.
2. Form normalized interface-centered 16x16 intensity/gradient patches.
3. Apply the frozen ExtraTrees development model.
4. Aggregate likelihood in achieved Home-relative space.
5. Require forward/reverse support and the frozen 0.70 score.
6. Preserve rejected and ambiguous events; enumerate only a unique seven-event map.

G2.5 adds an anatomy-blind acquisition wrapper:

- scene-authored operator Home projected onto generic skin;
- robot/world-down surface-tangent longitudinal axis;
- generic skin-continuity limits and a 120 mm mechanical resource ceiling;
- achieved-motion integration and physical recovery;
- causal stopping that requires exactly seven supported events plus observed margins beyond both outer events.

This wrapper is not complete because it does not yet search laterally using online image evidence. The fixed one-footprint medial variant is a development configuration, not a general autonomous corridor-selection solution.
