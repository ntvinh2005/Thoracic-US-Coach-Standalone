# Thoracic US Coach M0 — Ultrasound Boundary Feasibility

## Implementation

Two isolated files were added for the Phase A evaluator-only audit:

- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.BoundaryFeasibility.cs`
- `Assets/ThoracicUSCoach/Editor/ThoracicBoundaryFeasibilityBatch.cs`

The partial controller inspects the exact live skin collider, ultrasound-visible renderer bounds, the evaluator-only T2-T8 corridor axis, and connected 3 mm surface projections. It does not change Active2D or SweepCount behavior and does not expose geometry to an actor.

No boundary detector, autonomous navigation, T7 logic, scene change, collider change, renderer change, or M1-M3 implementation was added. The Phase B-D implementation was intentionally not started after the Phase A stopping rule fired.

## Verification

- Repository branch: `codex/u08-phase2`.
- Starting HEAD: `e7be8ef15e9680f2cbf4f11eea69ea3501bcea1c`.
- Unity version: `6000.3.9f1`.
- Scene: `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity`.
- Unity batch compilation and evaluator-only geometry audit: **PASS**.
- Focused Python tests: **NOT_RUN**; no analyzer was implemented because Phase A blocked the experiment.
- Active2D/sweep-count regressions: **NOT_RUN in this M0 attempt**; the stopping rule prohibited progressing to the implementation/test/pilot stages.
- Physical pilot: **NOT_RUN**.
- Freshness, physical reversal, and renderer integrity checks: **NOT_RUN** because no frame was captured.

## Geometry

The corridor origin is the evaluator-only midpoint of the projected T2-T8 skin contacts. Its longitudinal axis runs from the T2 projection toward the T8 projection. This information is written only to `preflight_geometry.json`.

| Quantity | Cranial | Caudal |
|---|---:|---:|
| Skin collider projected AABB | -97.421 mm | +103.646 mm |
| Connected 3 mm surface projection reached | -91.839 mm | +97.602 mm |
| Ultrasound-visible bone-like AABB extent | -100.613 mm | +106.763 mm |
| Reachable margin outside bone-like bounds | -8.774 mm | -9.161 mm |
| Required predeclared outside margin | 15 mm | 15 mm |

The connected surface audit produced 32 cranial samples including the origin and 34 caudal samples including the origin. It used the existing surface raycast, 3 mm step, meaningful-progress, teleport-like discontinuity, and surface-normal continuity constraints. It did not move the probe.

The serialized Active2D longitudinal workspace is only +/-48 mm and would restrict travel well before either physical skin end. An isolated larger diagnostic workspace could remove that software restriction, but it cannot create missing skin beyond the anatomy: even the reachable skin extrema remain inside the projected combined Skeleton bounds.

The ultrasound-visible scene also contains lung pleura, dura, ligamentum flavum, supraclavicular tissue structures, vasculature, nerves, and other normal/shadow sources. Therefore a zero-intensity image would not be expected automatically outside T2-T8. The combined Skeleton remains a single unresolved shadow source.

## Results

No upper or lower blank-image transition was measured. Phase A found that neither direction offers the predeclared five-step (15 mm) physically valid corridor beyond the projected ultrasound-visible bone anatomy.

The absence of captured blank frames is not evidence that blank frames do not exist. It means the approved M0 hypothesis cannot be tested under the required definition of a safely reachable region beyond the imaged anatomy without changing the scene or physical contract.

## Reversal

**NOT_RUN.** No exploration or reversal was authorized after physical reachability failed.

## Failures

- Cranial outside reachability: **BLOCKED**.
- Caudal outside reachability: **BLOCKED**.
- Both-end physical reachability: **BLOCKED**.
- The existing +/-48 mm workspace would also stop the route prematurely, but workspace expansion alone is insufficient.
- Renderable AABB projection is conservative and does not prove that every point in the bounds produces visible ultrasound content. Nevertheless, the required safe skin corridor does not extend beyond the full combined Skeleton extent at either end, so the M0 acceptance condition cannot be established as written.
- No invalid, stale, contact-lost, or renderer-failed frame was classified as blank because no frames were captured.

## Verdict

**M0: BLOCKED — `M0_PHYSICAL_REACHABILITY_BLOCKED`.**

| Dimension | Verdict | Evidence |
|---|---|---|
| Physical reachability | BLOCKED | Neither connected skin end reaches the required outside-anatomy margin. |
| Visual boundary evidence | NOT_RUN | Phase A stopping rule fired before capture. |
| Reversal consistency | NOT_RUN | No physical route was executed. |
| Integrity and isolation | NOT_RUN | No actor inference or captured frame exists to grade. Geometry remained evaluator-only. |

Established G0/G1/G2/G3/G4 scientific verdicts were not modified.

## Next decision

Do not approve M1 autonomous boundary discovery from this evidence. Continuing the exact hypothesis would require a major approved change to the scene/collider/anatomy relationship or a revised definition that tests low-content transitions *within* the current anatomy rather than a physically reachable outside region. Either option is outside M0 authorization.
