# Scene identity comparison

| Item | SHA-256 | Role |
|---|---|---|
| G2-R4/R5 committed scene | `60fef5d7f7ba3e1091c15b89a960ce64cb369f410fd67f436911277cbab1cac9` | Source/preflight scene |
| Preserved G2-R2/R3 execution scene | `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131` | Required isolated physical-execution scene |
| Skeleton FBX | `df34d622b8af3a719cbc0cb42e33e643e4435bd2cbf2d6c4a2e4b49357d19d4e` | Unchanged anatomy |
| UltrasoundManager.cs | `2f170ee99402279977f906a8288cf4af6ce66016d5b0bda00b30e1cbdd9220e0` | Unchanged renderer implementation |

The scene diff adds serialized defaults for Active2D and Guided2D modes, a null ultrasound image renderer field, and a dataset run ID, while moving existing Phase-2 diagnostic serialization. There are no changed transforms, anatomy references, camera references, Skeleton references, colliders, materials, or skin-contact objects.

The `ce9527...` file exists as a user-staged change in the original worktree. G2-R5 must copy it into a detached execution worktree without changing, unstaging, committing, or discarding the original. This makes the physical execution comparable to G2-R2/R3 while keeping the review branch free of unrelated scene changes.
