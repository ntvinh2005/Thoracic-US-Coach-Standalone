# G2-R6 decisive cross-plane anatomical discrimination

Status: `COMPLETE`

Scientific verdict: `INCONCLUSIVE_ACQUISITION`

## Outcome

Unity Personal licensing worked through the graphics/UI Editor entitlement. Compilation passed, focused EditMode tests passed 42/42, and exactly one authorized physical diagnostic session ran. The session completed the yaw-90 forward/reverse scan of actor-selected region R6R01 and then stopped at the frozen alternative-view informativeness gate.

The transient yaw-60 image differed from the matched yaw-90 baseline by pixel MAE `0.017132`, below the predeclared minimum `0.020000` by `0.002868` (14.34%). The runtime correctly refused to continue to topology classification. It persisted 17 unique fresh yaw-90 frames and 77 accepted movements, with zero rejections and no logged contact/workspace failures. It did not persist the transient yaw-60 frame, did not reach R6R02/R6R03, and did not generate a cross-plane topology signature.

No retry was executed and no threshold was changed. The one-session budget is consumed (1/1).

## Frozen experiment design

- Actor-selected regions: unresolved weak event, stable consequential secondary interface, and strongest supported control, selected solely from G2-R5 actor evidence.
- Baseline plane: M6 paramedian longitudinal, yaw 90 degrees, depth 60 mm.
- Alternative plane: predeclared yaw 60 degrees, depth 60 mm.
- Sampling: approximately 1.5 mm, local forward/reverse traversal, no teleportation.
- Feature: `CROSS_VIEW_INTERFACE_ENTRY_EXIT_TOPOLOGY_V1`, measuring interface bridge/termination behavior, shoulder asymmetry, and neighboring-interface count with identical extraction for all regions.
- Initial informativeness gate: matched-view pixel MAE at least 0.02.
- Maximum: three regions, 160 frames, one physical session, no full sweep and no G4 return.

These choices remain development protocol. This session does not support or refute anatomical discrimination because the selected alternative plane did not create a sufficiently different persisted image view.

## Verification

| Check | Result | Evidence |
|---|---|---|
| R6 focused Python tests | PASS, 6/6 | `test_results/python_tests.json` |
| G2 R1-R6 Python regression discovery | PASS, 143/143 | `test_results/python_tests.json` |
| Source/configuration freeze | PASS | `source_configuration_hash_manifest.json` |
| Actor/evaluator separation tests | PASS in Python scope | `test_results/python_tests.json` |
| Unity Editor launch | PASS | `preflight_and_scene_identity.json` |
| Legitimate Unity license | PASS | Unity Personal, assigned, unlimited |
| Unity compilation | PASS | graphics Editor, exit code 0; four pre-existing warnings |
| Unity EditMode tests | PASS, 42/42 | `test_results/unity_editmode_ui_20261103.xml` |
| Physical diagnostic session | EXECUTED, INCONCLUSIVE | 1 session, 17 persisted frames, 77 accepted movements |
| Protected scene/anatomy/renderer changes | NONE from R6 | hashes in source manifest |

The current Unity Personal license does not expose the `com.unity.editor.headless` entitlement, so `-batchmode` remained unavailable. The official Editor was run with graphics/UI entitlement, without `-nographics`; compilation, tests, and the diagnostic method all executed successfully. Raw Unity logs remain local because they include machine/licensing identifiers; their SHA-256 hashes are recorded.

## Execution evidence

- Physical session: 1/1.
- Regions selected actor-only: 3; regions started: 1; regions completed across both views: 0.
- Fresh images persisted: 17/160, all unique hashes and valid freshness metadata.
- Accepted/rejected movements: 77/0.
- Persisted acquisition: R6R01 yaw 90 degrees, depth 60 mm, forward and reverse.
- Transient alternative acquisition: yaw 60 degrees, depth 60 mm; MAE measured but frame not persisted before the gate exception.
- Cross-plane threshold: MAE >= 0.02; observed 0.017132.
- R6R02 stable secondary and R6R03 control: not acquired.
- Actor classification: all three regions `INCONCLUSIVE_ACQUISITION`.
- Relative map: unchanged/inconclusive; T7 target remains null.

Evaluator-only provenance for the baseline frames found a combined-Skeleton shadow behind right trapezius on all beams that reported shadow. This establishes that the baseline imaged bone-related structure, but it cannot establish cross-view anatomical identity because no oblique frame/provenance was saved.

Two execution-integrity limitations are preserved rather than repaired post hoc:

1. The raw actor/summary JSON retained status `RUNNING` because the inner `finally` serialized artifacts before the outer coroutine guard assigned the failure status. The frozen MAE fields and Unity log establish the actual stop reason.
2. Failure cleanup restored the original probe pose using `SetPositionAndRotation`. Although all logged actor movements were physical and valid, this direct-pose cleanup violates R6's no-teleportation return contract. R6 G1 integrity is therefore FAIL for this episode. Raw evidence was not edited.

## Required answers

1. **Was the alternative acquisition physically safe?** The two yaw-alignment moves were accepted with valid contact/workspace state, but the session stopped before a local yaw-60 scan and cleanup used a direct pose reset. Full R6 safety/protocol integrity therefore did not pass.
2. **Did the oblique plane expose different relevant anatomical information?** Not sufficiently under the frozen gate: transient MAE was 0.017132 versus required 0.02. The frame was not persisted, so relevant anatomical difference is not evaluable.
3. **Which image feature was tested?** `CROSS_VIEW_INTERFACE_ENTRY_EXIT_TOPOLOGY_V1`: bridge brightness, low-bridge fraction, shoulder asymmetry, and neighboring-interface count.
4. **Does it measure structural identity or only appearance?** It measures pixel topology/appearance. Physical identity requires evaluator correspondence after actor freeze; that comparison was impossible for the missing oblique image.
5. **Could the ambiguous cranial event be characterized more reliably?** No. Only its yaw-90 baseline was persisted; the feature extractor produced no accepted topology measurement within the frozen target radius.
6. **Could the stable secondary interface be distinguished?** No. R6R02 was not reached.
7. **Was the difference visible from pixels alone?** The runtime measured a small difference, but it was below the predeclared informativeness threshold. No defensible structural distinction was measured.
8. **Did evaluator anatomy confirm the interpretation?** Evaluator provenance confirmed baseline right-trapezius/skeleton-shadow imaging only. It could not interpret a cross-plane distinction.
9. **Was evaluator knowledge leaked into actor inference?** No. Actor regions/protocol and blind outputs were frozen before evaluator provenance review.
10. **How many fresh frames and physical movements were collected?** 17 unique fresh persisted frames, 77 accepted movements, 0 rejected movements.
11. **Did all necessary Unity and Python tests pass?** Yes for executed preflight: Unity compilation PASS, focused EditMode 42/42 PASS, Python G2 regression discovery 143/143 PASS. The physical session itself ended INCONCLUSIVE at its frozen quality gate.
12. **Is a seven-level map uniquely supportable?** No new evidence; the map remains `INCONCLUSIVE`.
13. **Can T7 be inferred without privileged coordinates?** Not from the current evidence; `predictedT7RelativeMm` remains null.
14. **Does T7 localization accuracy remain a blocker?** Yes. The prior diagnostic error of approximately 4.972 mm remains an unresolved risk against the 5 mm tolerance.
15. **Scientific verdict?** `INCONCLUSIVE_ACQUISITION`.
16. **Continue ultrasound-only or switch to constrained PoC?** R6 does not justify either a GO or a general impossibility claim. Stop the current campaign. A further ultrasound-only experiment requires separate approval for one more genuinely distinct safe plane and must first fix evidence persistence and physical cleanup. If that bounded acquisition also lacks discriminating information, switch to an explicitly constrained/anchored PoC rather than continue detector tuning.

## Gates

- G0: historical PASS with combined-Skeleton identity limitation.
- G1: historical PASS; R6 targeted episode FAIL for incomplete acquisition plus direct-pose cleanup.
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
- `Tools/ThoracicUSCoach/g2_r6_incomplete_report.py` (post-run reporting only; not part of frozen actor inference)

Commands actually executed for verification:

```text
python -m unittest test_g2_r6_cross_plane.py
python -m unittest discover -s . -p test_g2_r*.py
Unity.exe -projectPath <historical-execution-worktree> -quit -logFile <local-compile-log>
Unity.exe -projectPath <historical-execution-worktree> -runTests -testPlatform EditMode -testFilter <R6/R5/R2/Active2D/Freshness/SweepCount suites> -testResults <R6>/test_results/unity_editmode_ui_20261103.xml -logFile <local-test-log>
Unity.exe -projectPath <historical-execution-worktree> -executeMethod CSSALT.ThoracicUSCoach.Editor.ThoracicG2R6CrossPlaneBatch.RunCli -logFile <local-session-log>
python g2_r6_cross_plane.py analyze --session-root <session_01> --protocol <actor_selected_regions.json> --r5-map <R5-map> --output-root <R6>
python g2_r6_incomplete_report.py --session-root <session_01> --output-root <R6>
```

All successful Unity commands used the licensed graphics Editor and deliberately omitted `-nographics`.

## Evidence limitation and next action

The cross-plane figure explicitly marks the unavailable yaw-60/R6R02/R6R03 panels; it does not synthesize missing images. The interface figure overlays actual multi-hypothesis contours only on acquired yaw-90 frames.

Smallest next action, requiring new researcher approval: make failure evidence complete (persist the initial alternative image before applying the gate, serialize final failure status, and remove direct-pose episode cleanup), then run at most one separately approved diagnostic using one more genuinely distinct but preflight-safe plane. Do not rerun yaw 60, lower the MAE threshold, or start G4.
