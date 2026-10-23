# M01 Worker D — Probe contact and motion safety audit

Status: **EVIDENCE_READY**  
Verdict: **MORE_SAFETY_DIAGNOSTICS**  
Commit inspected: `5184d92c308bb0791e3a299085f9c4066c7ff1e5`

No deployed controller, model, dataset, Phase 2 code, or frozen checkpoint was changed. The fresh 40-episode Gate-2 benchmark was not run.

## Exact configured contract

- `maxPenetrationMm = 0.5` in the C# default and in both `Thoracic-RA_Review.unity` and `ThoracicUSCoach_POC.unity`.
- Implemented predicate: `skinIntersection != null && skinIntersection.AnyHit && abs(skinIntersection.SignedDepth) <= maxPenetrationMm`.
- Active2D translation command: `3.0 mm`.
- Workspace: `±48 mm` longitudinal and `±36 mm` lateral from the placement/handoff origin.
- Translation discontinuity rejection threshold: commanded distance plus `2.5 mm` (therefore `>5.5 mm` for the configured step).
- Rotation contact-shift rejection threshold: `>2.5 mm`.

## What is implemented

Initial placement finds the skin, aligns the probe, calls `EnforceRigidContact`, and only then records the achieved face-center as the workspace origin. Cranial/caudal/medial/lateral actions project onto the current skin tangent, raycast a new surface point, align to it, enforce contact, reject excessive displacement, and check the 2D workspace. A failed surface raycast happens before any transform mutation, so the pose remains unchanged.

Rejected translations restore the old transform and call `EnforceRigidContact`. Rotation preserves the contact pivot, enforces contact, and rejects contact shift above 2.5 mm. The static implementation therefore validates accepted *discrete poses*; it does not prove swept/continuous skin contact along the path between two poses.

## Executed evidence

The bounded Unity 6000.3.9f1 EditMode suite passed **19/19** tests (0 failures). It includes the existing motion discontinuity, two-axis motion, scene reference, and environment STOP/contact tests. This was not a live Gate-2 episode run.

I also paired 127 historical Active2D run traces with their privileged pose audits, excluding one episode contaminated by concurrent Worker-B offline pose replay:

| Check | Result |
|---|---:|
| Frames | 23,733 |
| Initial poses with valid contact + workspace | 127/127 |
| Invalid contact frames | 0 |
| Invalid workspace frames | 0 |
| Maximum absolute signed depth | 0.00003052 mm |
| Accepted 3 mm translations | 22,995 |
| Achieved translation min / median / p95 / max | 2.999946 / 3.000001 / 3.000067 / 3.005128 mm |
| Rejected translations | 449 |
| Maximum rollback residual | 0.00003058 mm |
| Rotations | 54 |
| Maximum rotational contact shift | 0.00003076 mm |
| Handoff observations with valid contact + workspace | 121/121 |
| STOP observations with valid contact + workspace | 69/69 |

Accepted directional counts were cranial 2,995; caudal 2,483; medial 8,728; lateral 8,789. All 449 rejected moves had valid contact and workspace on the next recorded observation.

The excluded episode `LIVE_A2D_20261023_200247_105` contains apparent 0–32.787 mm deltas while actions are marked accepted. Its timestamp overlaps Worker B's Unity pose-replay work, which directly sets `probeRoot` to saved dataset poses. Those deltas are therefore external transform mutation, not evidence of actuator acceptance. They do demonstrate that the current trace schema cannot attribute or detect out-of-band transform changes.

## Safety gaps blocking Gate-2 readiness

1. **Phase handoff is not contact-gated.** It uses the already-captured privileged level flag, marks handoff accepted, and resets the workspace origin without re-measuring or requiring current contact/workspace validity.
2. **Final STOP is not contact-gated.** `overallPass` requires handoff and image target conditions, but not contact or workspace. `Active2DRunResult` does not store final contact/workspace flags.
3. **Rollback success is assumed.** Both translation and rotation rollback call `EnforceRigidContact` but ignore its return value.
4. **Rotation omits workspace validation.** Translation checks it; Active2D rotation does not.
5. **Commanded-versus-achieved progress is incompletely guarded.** Excessively large translations are rejected, but there is no minimum-progress rule and traces do not store achieved displacement or rejection reason.
6. **Failed raycast is silent.** It safely leaves the transform unchanged, but returns only `false`; there is no distinct contact violation/rejection reason for diagnosis.
7. **Only accepted-pose contact is evidenced.** No swept motion/contact fixture proves continuous physical contact between frames.

The historical observations are reassuring, but they do not exercise forced failure paths and cannot compensate for missing terminal safety predicates.

## Required bounded follow-up before Gate 2 approval

- Add or independently verify contact + workspace gates at both `PhaseHandoff` and `Stop`.
- Require and record successful contact restoration after rollback.
- Apply workspace validation to rotation.
- Record requested and achieved displacement plus explicit rejection reasons; define a minimum-progress rule.
- Run a small safety-only Unity fixture that deliberately forces raycast failure, contact failure, workspace rejection, rollback, rotation, handoff, and STOP. This is separate from, and should precede, the user-approved 40-episode Gate-2 benchmark.

## Evidence paths

- `Artifacts/ThoracicUSCoach/active2d/diagnostics/m01_worker_d/m01_worker_d_contact_motion_audit_v1.json`
- `Artifacts/ThoracicUSCoach/active2d/diagnostics/m01_worker_d/m01_worker_d_editmode_results.xml`
- Controller sources: `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs`, `ThoracicUSCoachController.cs`, and `Thoracic2DNavigation.cs`.

Unity batch Editor exited after the bounded test, and `unity close` confirms no editor for this project remained open. Exclusive Unity access is released.
