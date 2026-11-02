# G2-R5 targeted multi-view reacquisition

## Outcome

`DIAGNOSTIC_EXECUTED — ANATOMICAL_AMBIGUITY_NOT_RESOLVED`

The one bounded physical episode completed safely and produced genuinely new multi-view evidence. It rescued the second weak cranial event, but it did not validate the first cranial event and did not reject the recurrent deep interface. The actor therefore retained an inconclusive map and correctly emitted no T7 target.

This is a negative but decisive diagnostic result: yaw 90/91 plus added 60 mm depth does not provide enough image-only semantic information to uniquely separate all seven intended LF-associated recesses from the stable secondary interface.

## Implementation

- Added an isolated G2-R5 partial controller and batch entry point.
- Added a coordinate-independent selector for two weak hypotheses plus one stable ambiguity.
- Added bounded Home-relative window expansion, 1.5 mm physical motion, yaw 90/91 views, optional 60 mm depth, freshness/provenance validation, setting restoration, and physical return Home.
- Added targeted multi-view fusion with correlated-evidence capping. Multiple tracks from one image and exact duplicate images cannot add independent support.
- Preserved the existing G2-R2/R3/R4 detector behavior as baselines. No anatomy, renderer, scene, material, collider, Active2D baseline, or P1-R3 source was modified.
- The fixed Home remains anatomically unlabeled. The newer approved three-prior contract supersedes the old skill's known-T2-start assumption for this experiment.

## Execution identity and infrastructure

Unity 6000.3.9f1 used a legitimate active Unity Personal entitlement. The physical episode used scene SHA-256 `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`, the exact R2/R3 execution scene. Skeleton and UltrasoundManager hashes remained `df34d622...` and `2f170ee9...`.

Two isolated-worktree startup checks produced zero movement and zero frames. The first used `-nographics`, which cannot create the ultrasound RenderTexture. The second revealed that a clean Library import cannot resolve the scene's historical serialized package GUIDs (178 missing-script warnings versus 14 in the historical environment). Neither is counted as a physical episode. The frozen R5 files were then copied byte-for-byte as new untracked execution files into the preserved R2/R3 worktree, compiled/tested, and run against its historical Library. No existing file was overwritten.

## Verification

- Python G2-R2/R3/R4/R5 regressions: 69/69 PASS.
- Review-branch Unity focused suite: 41/41 PASS.
- Exact historical execution environment Unity suite: 37/37 PASS (8 R5, 12 Active2D safety, 8 freshness, 6 R2, 3 SweepCount).
- Physical diagnostic episode: 278/278 accepted movements, 0 rejected.
- Fresh captures: 159/159; 0 duplicate hashes.
- Contact, workspace, frame integrity, pose epoch, scan serial, and producer publication: PASS for all observations.
- Physical return Home residual: 0.0094 mm; original settings restored.
- Scene, Skeleton, renderer, material, and collider assets were not modified by R5.

## Actor-selected regions

Selection was frozen before evaluator anatomy was loaded.

| Region | Actor evidence before | New evidence | Actor decision |
|---|---:|---:|---|
| R5R01 weak event | 3 frames, both directions | 5 independent observations, 3 positions, both directions, yaw 90/91; shadow fraction 0.20 | `AMBIGUOUS_UNRESOLVED` |
| R5R02 weak event | 2 frames, no reverse | 11 independent observations, 6 positions, both directions, yaw 90/91; shadow fraction 1.00 | `SUPPORTED_RECESS_EVENT` |
| R5R03 stable deep interface | 39 frames, no verifiable distal shadow | 70 independent observations, 13 positions, both directions, three views; 22/22 60 mm observations had adequate distal pixels and shadow support | `AMBIGUOUS_UNRESOLVED` |

The additional evidence is not deterministic replay: all 159 image hashes are unique, each selected hypothesis is sampled at distinct physical positions and both traversal directions, and yaw/depth variants are recorded separately. It remains development-selected diagnostic evidence, not a held-out end-to-end confirmation.

## Independent anatomical evaluation

Evaluator references were loaded only after `trajectory_actor.json`, the actor protocol, actor classifications, and relative map were hashed in `blind_results.sha256`.

- R5R01 corresponds to T2 within 3.584 mm, but the actor correctly kept it ambiguous because pixel-shadow support failed.
- R5R02 corresponds to T3 within 0.397 mm and became supported.
- R5R03 is unmatched to T2–T8 within 5 mm and remains a stable image-visible secondary structure; extra depth revealed convincing shadow rather than a distinguishing absence of shadow.
- Existing accepted T4–T8 events remain. Accepted-event scoring is TP=6, FP=0, FN=1, precision=1.000, recall=0.857, F1=0.923.
- All seven references have a nearby diagnostic event, but only six are actor-accepted; diagnostic correspondence is not equivalent to an accepted seven-level map.

## Relative map and T7

The final actor map contains six supported events and five ambiguous events. It is `INCONCLUSIVE`; all T2–T8 ordinal coordinates remain null and `predictedT7RelativeMm` is null. No evaluator coordinate was substituted.

The historical T7-corresponding event remains at +47.950 mm relative to Home. Independent error is 4.972 mm against the +42.978 mm reference, leaving only 0.028 mm margin to the locked 5 mm tolerance. R5 did not revisit T7, so localization accuracy is unchanged and remains fragile.

## Scientific gates

- G0: `PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION` (historical, unchanged).
- G1-R5 targeted safety/freshness: `PASS`.
- G2-R5: `FAIL` — six accepted reference events; T2 remains ambiguous and an unmatched shadow-producing interface remains unresolved.
- G3-R5: `INCONCLUSIVE_NO_T7_TARGET`.
- G4: `NOT_RUN_PROTOCOL_EXCLUDED`.

## Readiness and smallest next correction

A frozen full G2/G3 confirmation is not scientifically justified. The exact blocker is not low frame count: the stable secondary interface has abundant bidirectional multi-view support and real shadow morphology, while the real T2-corresponding hypothesis is less morphologically stable. Yaw 90/91 and 50/60 mm depth cannot distinguish their semantic identities.

The smallest next discriminating experiment is one bounded, actor-selected cross-plane/oblique local acquisition at the unresolved T2-like and secondary-interface hypotheses. It must test a predeclared pixel feature tied to interface entry/exit or neighboring laminar topology, not another support threshold. Only if that feature separates both hypotheses uniformly should the revisit policy be frozen for one held-out fixed-Home full episode. G4 remains gated on an unambiguous seven-level map and a non-null image-derived T7 target.

## Review identity and reproducibility

- Review branch: `codex/g2-r5-targeted-multiview-reacquisition`.
- Baseline: `ffa3f5a6f4b20b7d7c5f29c581b75bf15bd4de8b`.
- Actor/runtime source freeze: `f4545efe88981938813049079978e34401a683be`.
- Frozen configuration commit: `c9bfa8902eecb970ecf4ff9e360e517d0735e8e3`.
- Final evidence commit is reported in the researcher handoff because a commit cannot contain its own identity.

In-scope source additions are the isolated R5 runtime partial controller, batch entry point, EditMode tests, actor-only targeted multi-view analyzer, reporting/evaluator script, and their Python tests. Historical detector implementations were retained. The full source/configuration hashes are in `source_configuration_hash_manifest.json`.

Principal commands actually executed were the R5 protocol selector and freeze command, Unity batch compilation/EditMode tests, the single `ThoracicG2R5TargetedBatch.Run` physical session, actor-only `fuse`, post-freeze evaluator reporting, and the 69-test Python regression command recorded in `test_results/python_test_summary.json`. Two zero-motion Unity startup attempts are disclosed above and in `test_results/execution_summary.json`.
