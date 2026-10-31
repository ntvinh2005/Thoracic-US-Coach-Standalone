# Thoracic US Coach - P1 Position-Aware T7 Localization Pilot

## IMPLEMENTATION

P1 adds one isolated partial controller, one batch runner, one EditMode test class, and one deterministic offline relocalization analyzer. Existing Active2D, renderer, scene, anatomy, image-only modes, M0-C/M0-D reports, and G2-G4 verdicts were not modified.

The mode is explicitly `CALIBRATED_POSITION_GUIDED_BASELINE`. Unity T7 ground truth is read once during calibration and frozen as a robot-frame target pose. The physical navigation loop uses only the stored target, measured robot pose, executed action history, and existing safe Active2D movement.

Explicit calibrated target pose data appears in `calibration_manifest.json` and `trajectory_actor.json`. Raw T7 reference/error data and renderer provenance appear only under `evaluator_only/`. The image-only relocalization output contains neither target coordinates nor T-level labels.

## VERIFICATION

- M0-B/M0-C/M0-D and P1 offline hash integrity: **PASS**.
- Python focused tests: see `test_results.json`.
- Unity compilation: **PASS**.
- Unity EditMode P1/Active2D/freshness/provenance tests: **38/38 PASS**.
- One and only one yaw-0 physical pilot: completed; no retry.
- SDK warning: background ATC hardware connection could not load `ATC64`; Unity imaging and the pilot completed. This was not used as blank/safety evidence.

## ROBOT POSITION CONTRACT

Robot coordinates are Unity world position in millimetres and Unity world quaternion `(x,y,z,w)`. This identity frame is independent of the target. The actor receives measured probe pose/orientation, stored calibrated target pose, actual movement result, action history, and ultrasound. T-level labels, runtime marker queries, evaluator score/distance, mesh identity, and renderer provenance are excluded from navigation.

## SPATIAL ULTRASOUND MAP

The offline map contains 62 reference observations from one physical traversal and 40 separately captured query observations. All 103 M0-B frame hashes and pose associations were verified. Each map entry binds actual robot pose, orientation, action/displacement, freshness metadata, and clean ultrasound.

## RELOCALIZATION COMPARISON

Values are median / worst absolute error in millimetres:

| Condition | Position only | Image only | Position + image | Fusion helped / hurt |
| --- | ---: | ---: | ---: | ---: |
| clean | 0.003 / 3.000 | 0.003 / 3.000 | 0.003 / 3.000 | 0 / 0 |
| longitudinal_plus_3mm | 3.003 / 3.007 | 0.003 / 3.000 | 0.005 / 3.006 | 20 / 0 |
| longitudinal_minus_3mm | 2.999 / 3.001 | 0.003 / 3.000 | 0.005 / 3.000 | 22 / 0 |
| yaw_plus_2deg | 0.003 / 3.000 | 0.003 / 3.000 | 0.003 / 3.000 | 0 / 0 |
| yaw_minus_2deg | 0.003 / 3.000 | 0.003 / 3.000 | 0.003 / 3.000 | 0 / 0 |

Exact pose has a ceiling effect. Under +/-3 mm longitudinal bias, ultrasound fusion corrected roughly half the queries and hurt none. The result is fixed-anatomy and reuses revisited paths; it is not independent cross-anatomy validation.

## CALIBRATED T7 NAVIGATION

- Initial longitudinal separation: **29.403 mm**.
- Accepted/rejected moves: **9/0**.
- Cumulative physical movement: **27.003 mm**.
- Final longitudinal error: **2.628 mm**.
- Final lateral error: **0.0016 mm**.
- Final quaternion/yaw/normal errors: **0.000 / 0.000 / 0.000 degrees**.
- Contact/workspace/freshness valid; zero rejected movement and zero navigation safety failure.

## ULTRASOUND CONFIRMATION

- Fresh calibration-versus-return block-SSIM: **0.643609**.
- Locked threshold: **0.971000**.
- Correct pose epoch, producer frame, scan serial, image hash, and provenance association: **PASS**.
- Independent evaluator reference distance: **2.660 mm**.

## SCIENTIFIC INTERPRETATION

This pilot proves one calibrated fixed-anatomy engineering baseline can physically navigate from a nontrivial start to the stored T7 robot pose and capture a valid fresh image. That image did not meet the locked calibration-image consistency threshold. The offline study also shows ultrasound can correct bounded longitudinal registration bias in this revisited-path dataset. It does not identify unknown T7, count vertebrae, validate patient generalization, or establish performance across starts/anatomies.

## VERDICTS

| Outcome | Verdict |
| --- | --- |
| POSITION_NAVIGATION | **PASS** |
| FRESH_ULTRASOUND_CONFIRMATION | **FAIL** |
| SPATIAL_RELOCALIZATION | **SUPPORTED_ON_EXISTING_REVISITED_PATHS; NOT HELD-OUT GENERALIZATION** |
| AUTONOMOUS_T7_IDENTIFICATION | **NOT_TESTED** |

Existing scientific gates remain unchanged: G2 **FAIL**, G3/G4 **NOT_RUN**.

## NEXT DECISION

The smallest next step toward localization without a known target coordinate is a separately approved, actor-scheduled held-out relocalization pilot from one new start with a deliberately perturbed pose prior, while keeping the map and SSIM/fusion parameters frozen. It would test whether image correction transfers beyond the exact recorded reverse paths; it would still not assign T7 without an anatomical anchor.
