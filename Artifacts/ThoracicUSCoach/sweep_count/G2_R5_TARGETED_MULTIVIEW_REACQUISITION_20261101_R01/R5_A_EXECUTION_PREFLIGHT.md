# G2-R5 execution preflight

## Result

`PASS_TO_FREEZE`

No G2-R5 physical diagnostic session had been consumed at this checkpoint.

## Unity license

Unity 6000.3.9f1 started in batch mode through the installed Unity Hub/Editor. The Editor reported an assigned Unity Personal entitlement and completed compilation and EditMode testing. No license file, client, executable, or entitlement was modified or bypassed.

## Scene identity

The committed G2-R4/R5 branch scene has SHA-256 `60fef5d7f7ba3e1091c15b89a960ce64cb369f410fd67f436911277cbab1cac9`. The successful G2-R2/R3 physical acquisitions used the user's preserved staged scene with SHA-256 `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`.

The exact diff is limited to serialized controller defaults/references: Active2D dataset/default fields, one null `ultrasoundImageRenderer` reference, `datasetRunId`, Guided2D fields, and serialization placement of Phase-2 diagnostic fields. It contains no probe transform, ultrasound camera, anatomy, Skeleton, material, collider, skin-contact, or workspace-object change. G2-R5 will use an isolated execution worktree containing the exact `ce9527...` scene for physical comparability. The user's staged scene is neither edited nor committed.

## Actor-selected diagnostic regions

The protocol was generated before evaluator anatomy was loaded. It selected two weak image-event hypotheses and one stable unresolved interface using only G2-R4 actor outputs, executed Home-relative coverage, support counts, traversal direction, image morphology, and acquisition geometry. No T label, LF identity, world coordinate, or evaluator reference participates in selection.

The frozen budget is one session, three regions, yaw 90/91 degrees, 50 mm depth for all regions, one additional 60 mm view only for the stable unresolved interface, 1.5 mm physical steps, at most 240 fresh frames, and no full sweep or G4 return.

## Verification

- Python: 66 relevant G2-R2/R3/R4/R5 tests passed; the focused R4+R5 rerun passed 23/23.
- Unity compilation: passed with no C# errors.
- Focused EditMode suite: 41/41 passed, including R5, R3/R2, Active2D safety, fresh-capture, and SweepCount regressions.
- The first test invocation used `-quit` and only compiled/imported; it is not counted as a test run. A subsequent actual test run initially produced 40/41 because one new static test misspelled the runtime message (`G2R5` versus `G2-R5`). After correcting only that assertion, the full suite passed 41/41.

## Entry decision

The physical session is permitted only after committing/fixing the source identity, reproducing compilation/tests under the exact `ce9527...` execution scene, and recording source/configuration hashes.
