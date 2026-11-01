# G0 Reference and Renderer Review

## Verdict

**G0: PASS WITH EXPLICIT IDENTITY LIMITATION.** Seven ordered evaluator-only label/LF reference pairs exist, each has a valid skin projection, and E1 found a causal combined-Skeleton pixel contribution at 7/7 reference poses. This establishes independently inspectable fixed-scene references and real bone-caused ultrasound pixels.

It does **not** establish actor-side identifiability. There are 7/7 separately named per-level vertebral bone renderers: the vertebral bone source is one combined `Skeleton` Shadow mesh. `LF T2` through `LF T8` are level-named ligamentum-flavum objects, not vertebrae.

## Renderer mechanism

The renderer raycasts through all `UltrasoundVisibility` objects, sorts entering/exiting interfaces by depth, and renders until the first `Shadow` interface. The combined Skeleton is that bone Shadow source. Superficial muscle uses texture rendering and contributes before bone. Full-interface provenance was added read-only; exact instrumentation parity was verified.

## Isolation

All labels, scene objects, bounds, poses, counterfactual toggles, and provenance remain under `evaluator_only/`. These artifacts are prohibited as localization actor input.

## E1 decision

`PROCEED_TO_ACQUISITION_GEOMETRY_SCREENING`. Bone-caused pixels exist, but individual T-level identity remains unresolved and must be tested through acquisition geometry and image separability rather than inferred from object names.
