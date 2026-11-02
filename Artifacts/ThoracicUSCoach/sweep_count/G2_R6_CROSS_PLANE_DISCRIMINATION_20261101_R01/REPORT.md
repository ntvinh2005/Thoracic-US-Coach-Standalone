# G2-R6 decisive cross-plane anatomical discrimination

Status: `BLOCKED_INFRASTRUCTURE`

Scientific experiment status: `NOT_RUN_LICENSE_BLOCKED`

## Outcome

The G2-R6 implementation, actor-only region selection, acquisition protocol, and image-only topology feature were frozen before Unity execution. The legitimate Unity 6000.3.9f1 Editor started and connected to the Unity licensing client, but the client returned zero matching entitlements and the Editor terminated with exit code 198 (`No valid Unity Editor license found`). The R6 stopping rule therefore prohibited compilation, EditMode execution, and physical acquisition.

No diagnostic session, physical movement, fresh frame, cross-plane measurement, actor classification, evaluator interpretation, map update, or T7 inference is claimed. The one-session budget remains unused (0/1).

## Frozen experiment design

- Actor-selected regions: unresolved weak event, stable consequential secondary interface, and strongest supported control, selected solely from G2-R5 actor evidence.
- Baseline plane: M6 paramedian longitudinal, yaw 90 degrees, depth 60 mm.
- Alternative plane: predeclared yaw 60 degrees, depth 60 mm.
- Sampling: approximately 1.5 mm, local forward/reverse traversal, no teleportation.
- Feature: `CROSS_VIEW_INTERFACE_ENTRY_EXIT_TOPOLOGY_V1`, measuring interface bridge/termination behavior, shoulder asymmetry, and neighboring-interface count with identical extraction for all regions.
- Initial informativeness gate: matched-view pixel MAE at least 0.02.
- Maximum: three regions, 160 frames, one physical session, no full sweep and no G4 return.

These choices remain development protocol, not evidence that cross-plane discrimination works.

## Verification

| Check | Result | Evidence |
|---|---|---|
| R6 focused Python tests | PASS, 6/6 | `test_results/python_tests.json` |
| G2 R1-R6 Python regression discovery | PASS, 143/143 | `test_results/python_tests.json` |
| Source/configuration freeze | PASS | `source_configuration_hash_manifest.json` |
| Actor/evaluator separation tests | PASS in Python scope | `test_results/python_tests.json` |
| Unity Editor launch | PASS | `preflight_and_scene_identity.json` |
| Legitimate Unity license | FAIL/BLOCKED | Editor exit code 198 |
| Unity compilation | NOT_RUN_LICENSE_BLOCKED | license stopped Editor before project compilation |
| Unity EditMode tests | NOT_RUN_LICENSE_BLOCKED | no XML test result was created |
| Physical diagnostic session | NOT_RUN_LICENSE_BLOCKED | 0 sessions, 0 frames, 0 movements |
| Protected scene/anatomy/renderer changes | NONE from R6 | hashes in source manifest |

The first attempted Python module invocation from the repository root failed because the test imports are intentionally local to `Tools/ThoracicUSCoach`; the same focused suite was then executed from its owning directory and passed 6/6. This invocation-path error did not affect source or scientific outputs.

## Required answers

1. **Was the alternative acquisition physically safe?** NOT_RUN. No physical acquisition occurred, so safety was not empirically re-established for R6.
2. **Did the oblique plane expose different relevant anatomical information?** NOT_RUN.
3. **Which image feature was tested?** The feature was implemented and unit-tested but not tested on new physical images: cross-view interface entry/exit topology using bridge darkness, low-bridge fraction, shoulder asymmetry, and neighboring-interface count.
4. **Does it measure structural identity or only appearance?** It measures image appearance with a physically interpretable topology hypothesis; anatomical identity would require the post-freeze evaluator audit that did not run.
5. **Could the ambiguous cranial event be characterized more reliably?** NOT_RUN.
6. **Could the stable secondary interface be distinguished?** NOT_RUN.
7. **Was the difference visible from pixels alone?** NOT_RUN.
8. **Did evaluator anatomy confirm the interpretation?** NOT_RUN.
9. **Was evaluator knowledge leaked into actor inference?** No. Region roles and coordinates were frozen from actor-only R5 outputs, and no evaluator stage ran.
10. **How many fresh frames and physical movements were collected?** 0 frames and 0 movements.
11. **Did all Unity and Python tests pass?** Python passed 143/143 regressions including 6/6 R6 focused tests. Unity compilation/tests were NOT_RUN because licensing blocked the Editor.
12. **Is a seven-level map uniquely supportable?** No new evidence; the map remains `INCONCLUSIVE`.
13. **Can T7 be inferred without privileged coordinates?** Not from the current evidence; `predictedT7RelativeMm` remains null.
14. **Does T7 localization accuracy remain a blocker?** Yes. The prior diagnostic error of approximately 4.972 mm remains an unresolved risk against the 5 mm tolerance.
15. **Scientific verdict?** `BLOCKED_INFRASTRUCTURE`.
16. **Continue ultrasound-only or switch to constrained PoC?** No scientific GO/NO-GO can be made from R6 because acquisition did not occur. After legitimate activation, run exactly the already-frozen single diagnostic session; do not alter thresholds or add an extra angle first.

## Gates

- G0: historical PASS with combined-Skeleton identity limitation.
- G1: historical PASS; R6 physical coverage/safety NOT_RUN.
- G2: remains FAIL.
- G3: remains INCONCLUSIVE / NOT established.
- G4: NOT_RUN.

## Branch and changes

- Branch: `codex/g2-r6-cross-plane-discrimination`
- Baseline: `4a4b54a045999d16c2d34f10f4a3287e1a461c7f`
- Frozen implementation commit: `58c8a6021506ecd12334203c1ff507ab805bd6e1`
- Frozen protocol commit: `b162093`
- Final evidence commit: recorded in the researcher handoff after commit/push.

Source changes are limited to the opt-in G2-R6 reuse of the R5 acquisition engine, one Editor batch entry point, one Unity focused test class, one Python actor analyzer, and one Python test module. No production scene, anatomy, renderer, material, collider, Active2D, or P1-R3 controller file was changed by the R6 review branch.

Exact source files changed from the R5 baseline:

- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R5TargetedReacquisition.cs`
- `Assets/ThoracicUSCoach/Editor/ThoracicG2R6CrossPlaneBatch.cs` and its `.meta`
- `Assets/ThoracicUSCoach/Tests/Editor/G2R6CrossPlaneTests.cs` and its `.meta`
- `Tools/ThoracicUSCoach/g2_r6_cross_plane.py`
- `Tools/ThoracicUSCoach/test_g2_r6_cross_plane.py`

Commands actually executed for verification:

```text
python -m unittest test_g2_r6_cross_plane.py
python -m unittest discover -s . -p test_g2_r*.py
Unity.exe -batchmode -projectPath <historical-execution-worktree> -runTests -testPlatform EditMode -testFilter <R6/R5/R2/Active2D/Freshness/SweepCount suites> -testResults <R6>/test_results/unity_editmode.xml -logFile <R6>/test_results/unity_editmode.log
```

The Unity command started the Editor but terminated at the license gate before creating the XML test result. It deliberately omitted `-nographics`.

## Evidence limitation and next action

Required image figures, frame manifest, trajectory, cross-view diagnostics, and evaluator anatomy interpretation do not exist because producing them without a successful physical session would fabricate evidence. Their structured artifacts are explicitly marked `NOT_RUN_LICENSE_BLOCKED` where applicable.

Smallest next action: restore a legitimate Unity Editor entitlement for the signed-in account, then rerun compilation/EditMode preflight and the single frozen yaw-90/yaw-60 diagnostic session without changing the protocol.
