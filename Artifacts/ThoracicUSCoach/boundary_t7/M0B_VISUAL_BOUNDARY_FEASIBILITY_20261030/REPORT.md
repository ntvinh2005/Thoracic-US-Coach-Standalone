# Thoracic US Coach — M0-B Visual Boundary Feasibility

## Implementation

An isolated partial controller and batch runner executed one yaw-0 physical route using existing Active2D movement, contact rollback, fresh capture, and opt-in evaluator provenance. The diagnostic workspace was derived from the connected skin corridor and restored in `finally`. No scene, collider, renderer, standard Active2D behavior, T7 logic, or G0-G4 gate was changed.

Changed implementation files:

- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.VisualBoundaryFeasibility.cs`
- `Assets/ThoracicUSCoach/Editor/ThoracicBoundaryFeasibilityBatch.cs`
- `Assets/ThoracicUSCoach/Tests/Editor/BoundaryFeasibilityTests.cs`
- `Tools/ThoracicUSCoach/boundary_feasibility_analysis.py`
- `Tools/ThoracicUSCoach/test_boundary_feasibility_analysis.py`

The previous M0 artifact and verdict were retained unchanged. This report belongs only to the corrected M0-B experiment identity.

## Verification

- Physical sampling: **PASS**
- Image integrity: **PASS**
- Valid frames: **103/103**
- Accepted/rejected movements: **102/0**
- Max yaw error: **0.000000 deg**
- Max normal-alignment error: **0.000000 deg**
- Python focused/regression tests: **25/25 passed**.
- Unity EditMode compilation and focused regressions: **40/40 passed**, including Active2D safety, fresh capture, sweep-count, mini-zigzag, and M0-B tests.
- Pilot count: **exactly one** yaw-0 physical episode; no retry was performed.
- Provenance instrumentation parity: **exact pixel parity**, 0 differing pixels; pose epoch, producer frame, and scan serial matched.
- Static isolation: the actor trace contains pixels, executed action/result, relative displacement, and freshness metadata, but no world pose, evaluator coordinate, mesh identity, renderer provenance, or T-level label.

The Unity pilot exited successfully and produced complete evidence. Its shutdown log contains a pre-existing optional `ATC64` native connection warning; this occurred outside the ultrasound capture path and did not invalidate any captured frame.

## Safe physical coverage

- Actor-relative evaluator coverage: **-88.933 to 94.710 mm**.
- Planned endpoint coordinates: **-88.934 to 94.710 mm**.
- Original workspace restored: **True**.
- Validated connected skin corridor: **-91.839 to +97.602 mm** evaluator-relative.
- Diagnostic workspace: asymmetric center offset **+2.881 mm**, half extent **94.720 mm**, derived from that corridor.
- Actually sampled evaluator coordinates: **-88.933 to +94.710 mm**.
- All **102/102** attempted 3 mm physical moves were accepted; contact and workspace checks remained valid for every observation.
- Skeleton AABB was recorded only as historical evaluator context and was not a movement stopping rule or analysis input.

## Cranial ultrasound observations

- Verdict: **FAIL** — adequate endpoint sampling found no terminal persistent low-content run.
- Persistent observations outbound/reverse: **0/0**.
- Transition difference: **None mm**.
- All endpoint observations from approximately **-75 to -90 mm** remained `STRUCTURED` under the frozen image-only criteria.
- The lowest outbound endpoint content score was approximately **-0.498**, above the predeclared low-content threshold of **-0.75**, and no frame satisfied the persistent five-frame requirement.
- Representative endpoint images retain broad speckled tissue regions and continuous bright structural interfaces; they are not visually blank.

## Caudal ultrasound observations

- Verdict: **FAIL** — adequate endpoint sampling found no terminal persistent low-content run.
- Persistent observations outbound/reverse: **0/0**.
- Transition difference: **None mm**.
- All endpoint observations from approximately **+81 to +96 mm** remained `STRUCTURED`.
- Endpoint content scores ranged approximately **-0.174 to +0.339**; none reached the low-content threshold and none formed a persistent low-content run.
- Representative endpoint images visibly retain bilateral speckled regions, curved bright interfaces, and central shadow/interface structure.

## Reversal

- REVERSAL_REPEATABILITY: **FAIL** using a predeclared 6 mm tolerance.
- This FAIL means there was no predicted boundary to reproduce; it is not a motion-repeatability failure.
- At matched endpoint positions, outbound/reverse image correlations were approximately **0.999** at +87/+90/+93 mm and **0.999** at -81/-84/-87 mm, with position differences below **0.0002 mm** for the matched pairs.
- The physical route and images were highly repeatable, but they repeatedly showed structured content rather than a blank transition.

## Failures and limitations

- Low-content observations inside the central +/-18 mm comparator: **0**.
- The route is evaluator-planned and the evidence comes from one fixed-anatomy episode.
- `Skeleton` remains `COMBINED_SKELETON_UNRESOLVED`; no substructure identity is claimed.
- The blind detector uses fixed robust-content criteria and does not use anatomy bounds or evaluator labels.
- One episode can falsify the proposed two-boundary phenomenon over this sampled safe corridor, but it does not establish general behavior for other anatomy, lateral offsets, yaw angles, renderer settings, or scenes.
- The endpoint sampling stayed about one 3 mm step inside each validated skin limit; forcing a further step was intentionally prohibited.

## Verdict

**M0-B: FAIL**

Subcriteria: `PHYSICAL_SAMPLING=PASS`, `IMAGE_INTEGRITY=PASS`, `CAUDAL_VISUAL_BOUNDARY=FAIL`, `CRANIAL_VISUAL_BOUNDARY=FAIL`, `REVERSAL_REPEATABILITY=FAIL`.

Both safely reachable endpoint regions were adequately sampled with real, fresh and provenance-matched ultrasound images. Every valid image was classified `STRUCTURED`; neither end produced a persistent low-content region. Therefore TWO visual boundaries are not available for the proposed localization method within the validated corridor.

The previous AABB stopping rule would have prevented this actual visual test; AABB containment was not used as evidence for or against a visual transition.

## Next decision

M1 remains unauthorized and is not recommended on the blank-boundary hypothesis supported by this scene/corridor. Review the real frames, blind analysis, reversal evidence, and evaluator provenance before selecting a different localization hypothesis. G2 remains **FAIL**; G3/G4 remain **NOT_RUN**.
