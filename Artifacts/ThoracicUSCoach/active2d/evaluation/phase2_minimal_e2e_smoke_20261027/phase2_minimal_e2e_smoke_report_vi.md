# Phase 2 Minimal End-to-End Smoke Test

Kết luận: `BLOCKED_BY_RECOGNITION`.

Đây là engineering diagnostic trên evidence development đã consumed, không phải scientific/clinical validation.

| Episode | Target encountered | Recognized | Returned | Fresh confirm | STOP | False STOP | Abstain | Failure reason |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| R4_RANDOM_01_POSITIVE_F0010 | Có | Không | Không | Không | Không | Không | Có | policy abstained safely: safe_abstain:exploration-complete-without-stop-eligible-observation |
| R4_RANDOM_05_POSITIVE_F0016 | Có | Không | Không | Không | Không | Không | Có | policy abstained safely: safe_abstain:exploration-complete-without-stop-eligible-observation |
| R4_RANDOM_06_HARDNEG_F0005 | Không | Không | Không | Không | Không | Không | Có | policy abstained safely: safe_abstain:exploration-complete-without-stop-eligible-observation |

## Runtime

- Unity: `6000.3.9f1`; scene: `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity`.
- Execution commit: `359c4c6027deccdcf64bae4c40250e6cb038f899`.
- CSSALT Local Jobs: Unity `1aac5303fb44` (episodes complete; exit 1 in post-processing); compile-only failed attempt `58242e0da0f5`.
- Frozen STOP: `phase2_stop_live_spatial_logistic_v1`, threshold `0.9300000071525574`.
- Action model: frozen U08 spatial-v2 seed 47.
- Operating rule: first 32 actions of the existing frozen raster, then existing return/confirm/STOP-or-abstain state machine; maximum 80 actor steps.
- Checkpoints remain engineering-diagnostic only; no deployment/scientific promotion is claimed.
- Phase 1 handoff: 3/3 starts restored at valid T7 handoff poses. `ScoreCandidate()` and target floor were unchanged.

## Per-episode mechanics

- `R4_RANDOM_01_POSITIVE_F0010`: 34 physical moves; 34 accepted; 34 completed; 0 rejected; max STOP score 0.318631; freshness=PASS; producer serial 3..228; contact/workspace violations=0/0.
- `R4_RANDOM_05_POSITIVE_F0016`: 34 physical moves; 34 accepted; 34 completed; 0 rejected; max STOP score 0.390745; freshness=PASS; producer serial 233..459; contact/workspace violations=0/0.
- `R4_RANDOM_06_HARDNEG_F0005`: 34 physical moves; 34 accepted; 34 completed; 0 rejected; max STOP score 0.758517; freshness=PASS; producer serial 465..686; contact/workspace violations=0/0.

Raw Unity traces, privileged offline measurements, logs and a compact set of genuine ultrasound frames are in this directory.

First actual blocker: two evaluator-positive views were physically encountered, but STOP scores never reached 0.93; therefore no best view was eligible for return or fresh confirmation.

Failed jobs: `58242e0da0f5` stopped at compile preflight before any episode (freshness fields initially placed on the wrong trace type). `1aac5303fb44` completed all three Unity episodes with Unity exit 0, then exited 1 only because the first representative-frame filename exceeded the Windows path limit; summarization was rerun on the unchanged closed traces.
