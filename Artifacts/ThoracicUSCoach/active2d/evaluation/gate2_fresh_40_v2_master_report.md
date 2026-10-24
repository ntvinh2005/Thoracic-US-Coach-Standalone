# Gate 2 Master Report

Verdict: `GATE2_PASS`

The frozen direct-image-only Phase 1 controller is **simulator-validated for T7 localization under the frozen Gate 2 protocol**. This result does not establish real-patient, unseen-anatomy, or clinical validity.

## Frozen protocol

- Protocol: `gate2_phase1_fresh_40_v2_20261023`
- Local Jobs ID: `caf889f7fece`
- Unity: `6000.3.9f1`
- Scene: `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity`
- Source commit: `b611564939333218ea818810f31033cb685465c0`
- Execution commit: `f3f3a28f708d64442ed1a2d037c6502475ca9b13`
- Direct-image checkpoint SHA-256: `a9a2bad301da76ceb61818843fca4f4b82333f45c6127f2412d71217ae7594c6`
- Policy server SHA-256: `124cf49ba35386a8c05af799eee325addf73e65a51f5f0dfc1c584a2f8585c54`
- Threshold: `0.85`; Phase 1 only; 300-action budget
- Precise region: nearest marker T7 and contact-to-T7 distance <= `13.8861628 mm`

## Results

| Starting group | Valid / run | Precise T7 handoffs | Wrong-level | Abstentions |
| --- | ---: | ---: | ---: | ---: |
| T6 | 10 / 10 | 10 / 10 | 0 | 0 |
| T7 | 10 / 10 | 9 / 10 | 0 | 0 |
| T8 | 10 / 10 | 10 / 10 | 0 | 0 |
| Outside T6-T8 | 10 / 10 | 7 / 10 | 0 | 2 |
| **Total** | **40 / 40** | **36 / 40** | **0** | **2** |

- Precise T7 handoffs: `36/40` (required >= `33/40`).
- Recognition after precise-region entry: `36/40 = 90%` (required >= `90%`). All 40 cases physically entered the precise region.
- Phase handoffs: 38. Confidence range `0.8506129-0.9911460`, median `0.9752968`, at the frozen `0.85` threshold.
- Wrong-level handoffs: `0/40`; confident wrong-level handoffs: `0/40`.
- Final contact valid: `40/40`; final workspace valid: `40/40`.
- Unsafe accepted or terminal poses: `0`; privileged unsafe frames: `0`; safety failures: `0`; contact violations: `0`.
- Rejected movements: `239`, all workspace rejections; no rejected movement became an accepted unsafe or terminal pose.
- Actor privileged-input leakage: `0`; raw verdict mismatches: `0`.

## Four non-successes

- `G2_04_OUTSIDE_T6_T8`: handed off at nearest T7 with confidence `0.856717`, but distance `14.3668404 mm` was outside the frozen precise radius.
- `G2_05_T7`: handed off at nearest T7 with confidence `0.867226`, but distance `13.9252253 mm` was `0.0390625 mm` outside the frozen precise radius.
- `G2_23_OUTSIDE_T6_T8`: entered the precise region, then abstained after the 300-action budget; 26 rejected workspace movements.
- `G2_26_OUTSIDE_T6_T8`: entered the precise region, then abstained after the 300-action budget; 27 rejected workspace movements.

These are two spatial-precision misses and two search/recognition abstentions, not wrong-level handoffs.

## Independent evidence audit

The independent evaluator accounted for 40 locked episodes plus the separate smoke episode in both raw and privileged trees, and 1,454 unique referenced observations, with no missing, duplicate, or extra evidence. It independently recomputed all metrics and returned `GATE2_PASS`.

- Manifest SHA-256: `90c1ae8f7cc8586ebbc051838da4c5ddd54c52f7fe976336de20c4ad0820af01`
- Unity result SHA-256: `d087bf5ec6a926ce15e05a0dddb59dbcfaea77993683ba54e7e674eecc0554f7`
- Raw trace tree SHA-256: `08e6a277628909e79e173c045a074f7d7a79e6ea16d0aeabfd3e65f6b05adff7`
- Privileged trace tree SHA-256: `68292365d04e2bb09ab816478997cecb68b4e670249d15273822011b79d23980`
- Unity log SHA-256: `fe6a791e5ca85c5b31e8c474b82de9766b7b88ac39b7da08e2bfee8cd3dc3843`
- Canonical independent recomputation SHA-256: `1e8a0bee3d24f06b79ff9e25ac5fc5e91cc403d9746775713c25ed3477062239`

The scheduler recorded exit code 1 only because the original post-run validator compared Unity's serialized float32 `0.8500000238418579` against manifest `0.85` with an inappropriate `1e-9` tolerance. The difference was `2.384e-8`. Commit `f6211a48f80eb1155c26f552cb8d868eac0bf1c0` changes only that binding check to a float32-safe absolute tolerance and adds a regression test; 12 Python tests pass. The Unity run, model, threshold, manifest, controller, and collected evidence were not changed or rerun.

## Recommendation

Close Phase 1 as simulator-validated under this frozen protocol. Keep Phase 2 acquisition/STOP, broader anatomy, real ultrasound, and clinical validation as separate gates. Do not replace the passing direct-image controller with the weaker topology or hybrid research models.
