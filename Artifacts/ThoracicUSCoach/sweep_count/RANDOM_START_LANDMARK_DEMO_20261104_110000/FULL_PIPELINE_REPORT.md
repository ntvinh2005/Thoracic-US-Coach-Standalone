# Full Pipeline Frozen Development Confirmation

## Outcome

`PASS` for this fixed-anatomy simulator episode.

- Random-start physical return: PASS.
- Phase 1 exploration: 249 fresh observations, 247 accepted movements, 0 rejected movements.
- Phase 1 map: `ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP`.
- Phase 1 correspondence: TP 7, FP 0, FN 0, F1 1.0.
- Predicted T7: +45.499214 mm relative to episode Home.
- Phase 2 actor: `ACTOR_SUCCESS_FROZEN`.
- Phase 2 observations: 237.
- Fresh image-only confirmation: PASS, 2/2 fresh frames accepted in this episode.
- Independent evaluator: PASS.
- Final T7 marker distance: 11.705278 mm under the Phase 2 evaluator's precise-region criterion.
- Final target score: 0.605084; frozen acceptable floor: 0.591231.

## Phase 1 map and independent errors

| Level | Predicted mm | Absolute error mm |
|---|---:|---:|
| T2 | -80.500786 | 1.984513 |
| T3 | -64.000786 | 3.791568 |
| T4 | -36.000786 | 0.117260 |
| T5 | -9.500786 | 0.231095 |
| T6 | +17.499214 | 0.005318 |
| T7 | +45.499214 | 0.079838 |
| T8 | +75.999214 | 0.031182 |

All seven are within the locked 5 mm Phase 1 localization tolerance.

## Final Phase 2 pose

- Best longitudinal offset from Phase 1 T7: -2.703005 mm.
- Best right-lateral offset: +26.252174 mm.
- Best yaw offset: +6.002028 degrees.
- Fixed transverse tilt: approximately +3 degrees.
- Final fresh ultrasound: `phase2_best_view/frames/frame_0236.png`.
- Final image SHA-256: `2C3516ECFCB00DF73984A959610804C9894814897B53C8536109A042122CC1B4`.

## Isolation

Phase 1 actor inputs were ultrasound pixels, executed Home-relative motion, direction/history, and the approved seven-level ordering prior. Phase 2 actor inputs were fresh ultrasound pixels, relative bounded search actions, and the Phase 1 T7 handoff. Evaluator coordinates and scores were loaded only after actor outputs were frozen.

This is one fixed-anatomy simulator confirmation, not a reliability or clinical-generalization claim.
