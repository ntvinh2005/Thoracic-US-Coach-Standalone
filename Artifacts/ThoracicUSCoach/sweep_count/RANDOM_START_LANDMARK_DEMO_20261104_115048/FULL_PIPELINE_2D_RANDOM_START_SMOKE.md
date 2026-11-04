# Full Pipeline 2D Random-Start Smoke Test

- Status: PASS for the bounded simulator smoke test.
- Random seed: `-690300323`.
- Initial actor-relative position: longitudinal `+12.000022 mm`, lateral `-6.000000 mm`.
- Physical return: four accepted lateral actions followed by eight accepted cranial actions.
- Home residual: longitudinal `+0.000092 mm`, lateral `-0.000057 mm`.
- Phase 1 motion: 255 accepted movements, 0 rejected movements, 257 fresh frames.
- Phase 1 map: `ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP`, seven accepted events, predicted T7 `+39.496826 mm` from Home.
- Phase 2 actor: `ACTOR_SUCCESS_FROZEN`, fresh confirmation PASS.
- Phase 2 independent evaluator: `overallPass=true`, `t7LevelAcceptable=true`, `finalTargetAcceptable=true`.
- Final fresh ultrasound: `phase2_best_view/frames/frame_0236.png`.

The initial 2D coordinates and return actions are actor-positioning data. No LF/T-level coordinate was used to select the random start, return actions, Phase 1 map, or Phase 2 navigation target. The pink visualization path is positioning-only; the cyan path begins at Home and represents Phase 1 longitudinal exploration.

This single run establishes demo readiness on the fixed simulator anatomy. It does not establish multi-episode reliability or clinical generalization.
