# G2-R6.1 Stage A repair

## Outcome

`STAGE_A_READY`

Stage A used no new physical movement. The repair preserves every valid fresh image before the view-informativeness decision, replaces in-episode direct pose restoration with bounded physical recovery through the existing movement controller, and persists a resolved final status only after recovery and settings restoration.

## Previous 17-frame failure lineage

All 17 R6 baseline images produced a valid primary interface track and at least one valley. The projected valley locations were stable near -75 to -76 mm, but all were 6.43–8.20 mm from the actor-selected weak-event hypothesis at -83.039 mm. The locked 4 mm association radius therefore rejected every valley before topology was recorded in the old output. The earliest failure was `TARGET_WINDOW_MATCH`, not tracking, valley extraction, or image integrity.

R6.1 records image-local topology before target association and preserves the association result separately. This does not promote the observed secondary structure to the weak-event hypothesis.

## Runtime repair

- Validity, contact, workspace and producer provenance are checked before persistence.
- The PNG and provenance are persisted before global/ROI/gradient informativeness measurements.
- A negative quality decision becomes `INCONCLUSIVE_ACQUISITION`; it does not throw away the image.
- Final states are distinguishable: `COMPLETED`, `INCONCLUSIVE_ACQUISITION`, `ABORTED_SAFETY`, `ABORTED_INFRASTRUCTURE`, and `RECOVERY_FAILED`.
- `SetPositionAndRotation` was removed from this diagnostic episode path.
- Normal and recoverable failure returns use accepted `NavigateG2R5` movements and are logged as physical actions.
- Original failure and recovery failure are serialized separately.

## Verification

- Python G2 regression discovery: 148/148 PASS.
- Unity 6000.3.9f1 compilation through licensed graphics Editor: PASS.
- Focused Unity EditMode R6.1/R6/R5/R2/Active2D/SweepCount/freshness: 50/50 PASS.
- An all-EditMode diagnostic run found 159/161 passing; one repaired test had an outdated string expectation and was corrected, while one unrelated pre-existing prefab-component test remained outside this focused scope. The focused safety/capture suite subsequently passed completely.
- Original execution scene SHA-256 remained `ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131`.

## Stage B protocol

The predeclared alternative is yaw 65 degrees at matched 60 mm depth. It lies within the previously contact-valid 60–90 degree interval, does not repeat yaw 60, and remains 25 degrees from the longitudinal yaw-90 baseline. The old global MAE threshold remains a diagnostic measurement; R6.1 additionally predeclares central-ROI MAE and local-gradient MAE, followed by offline interface measurability.

Actor region selection is unchanged and contains no Unity anatomy coordinate, label, LF identity, or evaluator score.
