# G2 Acquisition Geometry Recovery

## Outcome

**G2 remains FAIL.** The historical centerline transverse acquisition, one new transverse paramedian acquisition at +6 mm, and one new centerline longitudinal/parasagittal acquisition all completed with valid physical coverage and fresh ultrasound. None produced a defensible blind sequence of seven vertebral-level image events. No T7 coordinate was emitted, P1-R3 was not invoked, and no confirmation episode was run.

The experiment stopped after two new physical development sweeps, as required when all safe authorized geometries fail. It did not use the remaining four-sweep development allowance to tune on evaluator labels.

## Implementation

Added an isolated acquisition-geometry experiment without changing standard Active2D, SweepCount, DirectFreshCapture, P1-R3, the scene, anatomy, skin collider, or renderer:

- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.AcquisitionGeometryRecovery.cs`
- `Assets/ThoracicUSCoach/Editor/ThoracicAcquisitionGeometryRecoveryBatch.cs`
- `Assets/ThoracicUSCoach/Tests/Editor/AcquisitionGeometryRecoveryTests.cs`
- `Tools/ThoracicUSCoach/acquisition_geometry_recovery_analysis.py`
- `Tools/ThoracicUSCoach/test_acquisition_geometry_recovery_analysis.py`

The controller reuses the validated M0-B physical route, 3 mm surface-following movement, rollback, contact/workspace checks, yaw preservation, and direct fresh capture. Actor traces contain only images, executed relative movement, action/freshness/integrity state, and the fixed unlabeled-Home history. Geometry commands, world poses, scan-plane axes, anatomy references, and beam provenance are evaluator-only.

## Geometry and physical execution

| Geometry | Source | Plane verification | Accepted / rejected | Fresh valid frames | Longitudinal coverage | Safety result |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Centerline transverse | Historical M0-B | yaw 0 degrees | 102 / 0 | 103 / 103 | -88.933 to +94.710 mm | PASS |
| Transverse paramedian +6 mm | New run 1 | minimum lateral-plane alignment 0.998821 | 102 / 0 | 103 / 103 | -89.167 to +94.402 mm | PASS |
| Centerline parasagittal | New run 2 | minimum corridor-plane alignment 0.956886 | 102 / 0 | 103 / 103 | -89.167 to +94.400 mm | PASS |

Maximum yaw error was 0 degrees for paramedian and 0.0000229 degrees for parasagittal. Maximum normal-alignment error was 0 degrees. Both diagnostic workspaces were restored. No movement was rejected, no frame was stale, and there was no unhandled contact/workspace failure.

One initial `-nographics` invocation crashed in URP before controller startup or any physical action. It generated no geometry run directory and is retained in `preflight/paramedian_run.log` as an infrastructure attempt, not counted as a physical development sweep. The approved GPU batch invocation then completed normally.

## Blind image-only gate

The same predeclared deterministic descriptor/sequence gate was applied independently to each 103-frame actor stream before evaluator access. Outputs were SHA-256 frozen before anatomical comparison.

| Geometry | BIC-selected K | K=7 smooth-trend-null p | Reverse accuracy | Max feature-ablation boundary shift | Accepted vertebral events |
| --- | ---: | ---: | ---: | ---: | ---: |
| Centerline transverse | 5 | 0.8203 | 0.725 | 24.003 mm | 0 |
| Transverse paramedian +6 mm | 4 | 0.8602 | 0.750 | 3.002 mm | 0 |
| Centerline parasagittal | 5 | 0.8386 | 0.775 | 99.008 mm | 0 |

All three results are `NO_DEFENSIBLE_SEVEN_LEVEL_SEQUENCE`. The diagnostic K=7 partitions remain visible for audit but were rejected and never promoted to V1-V7.

## Evaluator-only per-level anatomy audit

The following matrix was generated only after both new blind freezes. `LFmax` is the maximum fraction of beams whose first visible evaluator object was the corresponding `LF Tn` within +/-6 mm of the independent reference. It is diagnostic provenance, not an actor feature and not proof that the image contains a uniquely identifiable vertebra. `Skeleton shadow` remains a combined unresolved mesh response.

| Geometry | T2 | T3 | T4 | T5 | T6 | T7 | T8 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Center transverse | 6.7%, supported provenance only | 9.1%, supported provenance only | 0%, not observed | 5.6%, sparse (1 frame) | 0%, not observed | 1.6%, weak | 3.6%, weak |
| Paramedian +6 mm | 7.5%, supported provenance only | 9.1%, supported provenance only | 0%, not observed | 4.0%, sparse (1 frame) | 0%, not observed | 2.0%, weak | 4.0%, weak |
| Parasagittal | 0%, not observed | 15.5%, supported provenance only | 9.9%, supported provenance only | 7.1%, supported provenance only | 0%, not observed | 0%, not observed | 0%, not observed |

No geometry exposed level-specific LF provenance strongly and persistently at all seven levels. The transverse views remained dominated by trapezius plus a broad central combined-Skeleton shadow. The parasagittal view changed morphology substantially, but it collapsed into long continuous interfaces: evaluator provenance supported T3-T5 only, while T2/T6/T7/T8 were absent as corresponding first-visible LF structures. Every level remained `NOT_RECOGNIZED` by the blind actor.

See:

- `geometry_per_level_contact_sheet.png`
- `per_level_anatomical_correspondence.csv`
- `TRANSVERSE_PARAMEDIAN_P6/offline_analysis/waveform_vs_relative_displacement.png`
- `PARASAGITTAL_CENTER/offline_analysis/waveform_vs_relative_displacement.png`

## Verification

- Unity 6000.3.9f1 compilation and focused EditMode regressions: **31/31 PASS**, 0 failed, 0 skipped.
- Covered `AcquisitionGeometryRecoveryTests`, `Active2DSafetyTests`, `MiniZigzagFeasibilityTests`, and `SweepCountMilestoneTests`.
- Focused Python detector/audit regressions: **12/12 PASS**.
- Both blind hash freezes verified before evaluator access.
- Static actor/evaluator separation audit: PASS. No runtime T7 transform, score, T-level coordinate, anatomy identity, renderer provenance, or calibrated P1-R3 target enters actor output or navigation.
- Actual physical development sweeps: **2/6**. Confirmation episodes: **0/3**.

## Scientific gates

| Gate | Verdict | Evidence |
| --- | --- | --- |
| G0 anatomical reference validity | PASS | Independent T2-T8 references available evaluator-only; combined Skeleton substructure remains unresolved. |
| G1 physical coverage | PASS | All three geometries cover the intended corridor with valid contact, workspace, orientation, and fresh capture. |
| G2 anatomical event observability | **FAIL** | 0 accepted events for every geometry; no seven-level per-structure provenance support. |
| G3 enumeration accuracy | NOT_RUN | No accepted V1-V7 sequence exists. |
| G4 T7 inference and physical reacquisition | NOT_RUN | No defensible V6 coordinate exists; invoking P1-R3 would fabricate a target. |

## Exact observability blocker

The current renderer/acquisition combinations do generate repeatable position-dependent morphology, but they do not expose seven separately attributable vertebral-level signatures along the safe corridor. Transverse and +6 mm paramedian views are dominated by the same combined-Skeleton shadow; the parasagittal view reveals a different continuous interface but loses independent visibility of four required levels. The approved K=7 prior can partition these smooth changes mathematically, but those boundaries fail null, BIC, reversal, adjacent-effect, or ablation controls and do not correspond one-to-one with independently visible T2-T8 structures.

This is an acquisition observability failure, not a movement, capture, yaw, freshness, or P1-R3 return failure.

## Decision

No further detector tuning, return run, or confirmation episode is scientifically justified on these geometries. Any next step requires separate approval for a materially different source of anatomical observability (for example a different safe lateral/oblique acquisition family or renderer/anatomy instrumentation study). This report does not propose or implement that architecture.
