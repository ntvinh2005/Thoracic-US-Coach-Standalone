# G2-R6 frozen cross-plane diagnostic protocol

## Question

Does a single safe oblique acquisition expose a pixel-derived interface topology that separates the unresolved weak event from the stable secondary interface, relative to an actor-selected supported-event control?

## Actor boundary

Region selection uses only G2-R5 actor classifications, support intervals, image-derived morphology, executed Home-relative coordinates, and robotic acquisition geometry. T labels, LF names, mesh identities, world anatomy coordinates, evaluator correspondence, renderer provenance, and calibrated T7 are excluded.

The fixed Home is anatomically unlabeled. This experiment follows the newer three-prior contract rather than the older skill's known-T2 initialization.

## Frozen acquisition

- One targeted session; no full sweep and no G4 return.
- M6 lateral offset: -6 mm.
- Baseline: surface yaw 90 degrees, depth 60 mm.
- Alternative: surface yaw 60 degrees, depth 60 mm.
- Movement step: 1.5 mm.
- Three actor-selected windows; maximum 160 fresh frames; estimated 114.
- The first matched baseline/oblique images must have normalized pixel MAE at least 0.02 or the session stops as uninformative.
- The yaw-60 choice is based on prior safety and scan-plane evidence. No angle search is permitted.

## Frozen image feature

`CROSS_VIEW_INTERFACE_ENTRY_EXIT_TOPOLOGY_V1` measures the same properties for all regions:

- bridge brightness ratio;
- low-bridge fraction;
- shoulder asymmetry;
- count of distinct neighboring bright-interface hypotheses.

For each view, evidence requires at least four independent observations, at least three physical positions, and both traversal directions. Cross-plane signatures are compared with the supported-event control using mean absolute normalized distance. The weak event must be within 0.25 of the control, the secondary interface at least 0.50 away, and their distance margin at least 0.25. These development thresholds are frozen before new capture.

The feature measures image appearance topology, not anatomical identity. Anatomy interpretation occurs only after blind actor outputs are hashed.

## Stopping decision

- `DISCRIMINATING_FEATURE_SUPPORTED`: all frozen criteria pass.
- `DISCRIMINATION_NOT_SUPPORTED`: both planes contain usable evidence but criteria fail.
- `INCONCLUSIVE_ACQUISITION`: cross-view evidence is insufficient or not comparable.
- `BLOCKED_INFRASTRUCTURE`: license, scene, compilation, safety, or freshness fails before/within capture.

Regardless of the diagnostic result, R6 emits no T7 target and does not run G4. A held-out fixed-Home G2/G3 episode remains separately gated.
