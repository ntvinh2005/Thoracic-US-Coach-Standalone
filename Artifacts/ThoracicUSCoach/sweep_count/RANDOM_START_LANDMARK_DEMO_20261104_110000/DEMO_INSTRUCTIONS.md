# Full Phase 1 + Phase 2 demo

Unity Editor may remain open throughout the demo.

## Start

1. Open `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity` if it is not already open.
2. Enter Play Mode.
3. In the top-left **Random Start Landmark Demo** panel, optionally change the random seed and Home jitter.
4. Click **RUN FULL PIPELINE: PHASE 1 -> PHASE 2 BEST T7 VIEW**.

The same action is available before Play Mode from:

`Tools > Thoracic US Coach > Open Full Phase 1 + Phase 2 Demo`

## What the demo shows

1. A seeded random probe start.
2. Physical return to the fixed, anatomically unlabeled Home.
3. Ground-truth-free longitudinal exploration with fresh ultrasound capture.
4. Blind Phase 1 analysis and a Home-relative T2-T8 map.
5. Unity markers at the actor-predicted landmarks.
6. Physical movement to the predicted T7 level.
7. Bounded right/transverse Phase 2 search.
8. Return to the image-only selected best pose and fresh ultrasound confirmation.
9. Independent Unity evaluation after actor output freeze.

The `ANALYZING` state is CPU image inference. With the primary-plus-verifier bundle it normally takes a few minutes and is not a Unity hang.

## Viewing controls

After Phase 1 markers appear, use **Focus all landmarks**, **Left**, **Right**, **Up**, **Down**, **Zoom +**, and **Zoom -** in the same panel. **Restore camera** returns to the original camera pose.

The successful terminal UI state is:

- Phase 1: seven accepted T2-T8 landmarks and a non-null T7 coordinate.
- Phase 2: `COMPLETE`.
- A `Final fresh ultrasound` path is displayed.

The historical guided/oracle Phase 2 panel is not invoked by this demo.
