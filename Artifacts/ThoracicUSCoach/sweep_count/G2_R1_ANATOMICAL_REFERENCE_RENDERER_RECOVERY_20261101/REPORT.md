# G2-R1 Anatomical Reference & Renderer Recovery

## Verdict

`ANATOMY_AND_RENDERER_ADEQUATE — DETECTOR_REDESIGN_NEEDED`

This campaign resolves the immediate observability diagnosis without changing the scene, anatomy, renderer, skin collider, Active2D, or P1-R3.

## A. Anatomical geometry findings

The production ultrasound bone source is one `Skeleton` mesh from `RA Thoracic Anatomy V3.fbx`: one submesh, 24,376 vertices and 42,966 triangles. A single connected component contains 97.61% of vertices and 99.22% of triangles, so connected components cannot identify T2-T8 individually.

However, the combined mesh is not geometrically featureless. A blind multi-offset posterior-surface analysis found eight robust recess candidates. After freezing those candidates, seven aligned one-to-one with the separately modeled LF T2-T8 meshes within 0.13-1.96 mm. The unmatched eighth candidate is retained as a negative control.

The old T-level label centers are not reliable image-visible recess positions. Their longitudinal displacement from the corresponding LF centers reaches 18.27 mm. This explains a major error in the prior center-versus-arithmetic-midpoint diagnostic.

Individual vertebral body, lamina, rib, spinous-process, and triangle-level identities remain unresolved inside the combined Skeleton. The validated reference is an LF-associated posterior bone recess region, not a segmented vertebral bone center.

## B. Ultrasound observability findings

One diagnostic Unity session acquired 42 fresh original-scene images at seven anatomy-validated regions under three safe longitudinal geometries. All contact, workspace, freshness, pose/frame/serial, hash, and provenance checks passed.

The best configuration is `PARAMEDIAN_LONGITUDINAL_M6`, surface yaw 90 degrees:

- exact target LF reached before or at the first Skeleton shadow: 7/7;
- Skeleton shadow on both longitudinal sides of target LF: 6/7;
- independently rendered repeat-B matched repeat-A region: 7/7;
- minimum same-pose SSIM: 0.99049;
- maximum same-pose descriptor distance: 0.000452;
- minimum adjacent-region descriptor distance: 0.058604;
- adjacent-minus-repeat separation margin: +0.058152.

Visible evidence includes recurring curved bright interfaces, shadow entry/exit, shadow-width changes, and level-dependent local morphology. The full ordered-interface trace confirms what appears before the first Shadow termination and identifies later occluded intersections. No claim relies on `firstVisibleObject` alone.

## C. Scientific diagnosis

### H1 — Geometry adequate, detector inadequate: SUPPORTED

The original anatomy and renderer expose repeatable LF-associated bone/recess information at all seven regions when the acquisition is placed at actual anatomy-derived references. Previous algorithms used the wrong reference semantics and did not implement a continuous detector for this event family.

### H2 — Renderer or acquisition correction needed: previous acquisition reference error supported; renderer defect refuted

Corrected anatomy-based placement is necessary. A permanent renderer change is not supported because the unchanged renderer already produces strong, repeatable evidence at M6.

### H3 — Anatomical representation insufficient: refuted as the primary blocker

The combined mesh limits semantic provenance but retains the recurring geometry required for an event detector. Per-level bone segmentation would improve provenance, but current evidence does not justify replacing anatomy to make G2 easier.

### H4 — Evidence insufficient: refuted for diagnostic observability, retained for autonomous detection

R1 establishes the signal source and best geometry. It does not establish that an actor can find seven events during a continuous sweep.

## D. Impact on autonomous T7 discovery

The future chain `image -> event detection -> relative T2-T8 map -> V6/T7 -> return` is now scientifically plausible in the fixed scene, but only its first prerequisite is resolved.

The smallest next stage is a bounded detector redesign on a physical continuous M6/yaw-90 sweep. It should combine image-only shadow/interface transition morphology, spatial persistence, executed relative displacement, and reversal agreement. The seven-level prior may rank a seven-event interpretation but must not insert missing events. The unassociated 33.5 mm recess must be a locked false-positive control. No evaluator coordinates, LF names, provenance, or T labels may enter the actor.

## E. Verification and execution summary

- Python focused tests: 9/9 PASS.
- Unity compile: PASS; four pre-existing warnings, zero errors.
- G2-R1 EditMode tests: 5/5 PASS.
- Active2D safety regressions: 12/12 PASS.
- Direct fresh-capture regressions: 8/8 PASS.
- P1-R3 focused regressions: 7/7 PASS.
- Diagnostic ultrasound capture sessions: 1.
- Fresh diagnostic PNGs: 42, all unique hashes.
- Full longitudinal sweeps: 0.
- Autonomous return/P1-R3 confirmations: 0.
- Training: 0.
- Original scene, active Skeleton FBX, and UltrasoundManager hashes: unchanged.

## Scientific gates

- G0: PASS with combined-Skeleton identity limitation.
- G1: PASS.
- G2: FAIL. R1 is diagnostic evidence, not a continuous actor event-detection gate.
- G3: NOT_RUN.
- G4: NOT_RUN.

## Evidence index

- R1-A: `R1_A_ANATOMICAL_AUDIT.md`, `skeleton_mesh_topology.json`, `bone_structure_reference_map.json`, `reference_vs_actual_anatomy.csv`, `anatomical_geometry_visualization.png`, `unresolved_structures.json`.
- R1-B: `R1_B_ULTRASOUND_OBSERVABILITY.md`, `anatomical_acquisition_manifest.json`, `bone_gap_signal_analysis.csv`, `bone_gap_contact_sheet.png`, `bone_gap_waveform.png`, `renderer_contribution_audit.json`, `repeatability_and_negative_controls.json`, and `evaluator_only/r1b_observability/`.
- R1-C: `scientific_diagnosis.json`, `source_asset_integrity.json`, `experiment_registry.json`, and `test_results/`.

No autonomous T7 PASS is claimed.
