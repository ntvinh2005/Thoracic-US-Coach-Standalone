---
name: thoracic-sweep-count
description: "Use for planning, implementing, reviewing, debugging, and evaluating the Thoracic US Coach Unity Phase 1 image-only longitudinal T2–T8 sweep-and-count experiment, including ultrasound feature signals, vertebral event detection, T7 enumeration, physical return, and evidence-based evaluation. Not for autonomous Phase 2, new neural-network training, or generalization experiments without explicit user approval."
---

# Thoracic US Coach — Phase 1 longitudinal sweep-and-count

## 1. Mission and scope

The project is a **simulator-only, fixed-anatomy proof of concept**. The immediate goal is to show that a probe can **physically sweep along the thoracic anatomy, perceive recurring ultrasound structures, count vertebral-level events, identify T7, and physically return to and reacquire that T7 level**. A successful image-only Phase 1 is not a clinically validated result and is not Phase 2 final needle-target placement.

**Intended episode:** known, experiment-controlled cranial starting region T2 -> safe skin-following caudal sweep through T8 -> clean ultrasound sequence -> interpretable bone/space-related signal -> detect unique ordered vertebral events -> enumerate levels using the **known T2 starting assumption** -> select event 6 (T7) -> reverse motion using exploration history -> capture a fresh ultrasound observation at T7 -> independent Unity-ground-truth assessment.

The core scientific hypothesis is **not yet proven**: one repeatable event or event group in the ultrasound signal may correspond to one vertebral level. Never bake this hypothesis into a guaranteed seven-event detector. A rib echo, lamina echo, acoustic shadow, and interspace are not automatically equivalent to a vertebra. Empirically identify what the measured event represents.

Maintain a separation between:
- **Physical sweep coverage**: did the probe traverse and image the relevant regions?
- **Anatomical event detection**: can imaging evidence detect distinct real structures?
- **Enumeration**: does the ordered event sequence correspond to T2..T8 given an externally known T2 start?
- **T7 localization and physical return**: was T7 selected from the detected sequence, physically revisited, and freshly confirmed?

## 2. Read the repository before proposing edits

Confirm the actual branch, commit, working-tree status, scene/prefab and runtime path. Do not assume the following historical files or settings still have the same contents:

- `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity`
- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs`
- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs`
- `Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Guided2D.cs`
- `Tools/ThoracicUSCoach/README.md`
- `Tools/ThoracicUSCoach/` (capture, policy server, evaluation, tests)
- `Artifacts/ThoracicUSCoach/` (historical results; do not confuse historical results with a new verification)

Find and reuse the existing **clean ultrasound capture** (`CaptureCleanImage` if still present), discrete **Cranial/Caudal skin-tangent motion**, raycast/contact checks and bridge infrastructure. Inspect the actual active mode before integrating; guided/oracle-assisted demos and image-only policies must not be conflated. Historical T6–T8 workspace limits do not establish a safe T2–T8 travel corridor.

Do not assume the scene contains an independently identifiable T2–T8 vertebral mesh merely because it has seven label GameObjects. Confirm visible image-generating anatomy, scan accessibility, and independently checkable reference regions before accepting labels as ground truth.

## 3. Mandatory working agreement

1. **Plan -> user approval -> implementation.** First provide a concise, concrete plan with affected files, risks, evidence to collect, test commands, and stage gates. Wait for explicit approval before editing source files, modifying scenes, training, running expensive captures, or changing evaluation criteria. If the user specifically asks only for audit or plan, do not implement.
2. **No subagents.** Work directly; do not spawn workers/delegations.
3. **Small, reviewable patches.** Preserve original source-code comments verbatim. Do not rewrite broad controllers, alter unrelated modes, reorganize the repository, or silently introduce dependencies. Preserve the existing guided and image-only demos.
4. **Do not overengineer.** Start with classical image processing, a 1D signal and a simple temporal event detector. Do not add PPO, RL, CNN training, full 3D reconstruction, atlas matching, 2.5D mapping or complex belief filters unless a measured failure motivates a new plan and the user explicitly approves it.
5. **No inference-time privileged leakage.** The counting/localization policy may use clean ultrasound pixels, the *actually executed* previous actions, action success/failure and its own accumulated history. It must not read Unity world pose, T labels, target transforms, T7 seed, mesh object names, target distance, path index, score, ground-truth masks or evaluator artifacts. A safe movement controller may internally use skin normals, collider geometry and contact sensors to enforce motion; that privileged safety channel must not expose absolute anatomical location to the localization policy.
6. **Ground truth is permitted only for** experiment setup (for this fixed-start pilot, placing the initial probe in a known T2 region), safety, offline annotation, diagnostics and independent scoring. Be explicit that *known T2 initialization* is an experiment assumption, not autonomous identification of T2.
7. **Evidence over claims.** Never call unexecuted code "verified". Label unavailable Unity Play Mode/MCP tests `NOT_RUN`, unsupported results `INCONCLUSIVE`, and actual failures `FAIL`. Do not treat synthetic signal visualizations, unit tests, or a geometry-guided oracle run as image-only success.
8. **Stop on a blocked gate.** Do not hide failed coverage, ambiguous signal, failed matching, wrong T7 or failed return by adding new training or by weakening thresholds. Report the blocker, evidence, and the smallest next experiment.
9. **Keep human-readable experiment tracking.** If a project master tracker is accessible and editing is approved, update implementation, execution and verification independently, using only the existing status enum and grounded evidence links. Never claim a tracker was updated if it was not.

## 4. Non-negotiable episode invariants

- Physically execute small incremental Cranial/Caudal movements with valid skin contact and validated transitions. No teleporting or unannounced `SetPositionAndRotation` during the autonomous sweep or return.
- Follow the curved skin; do not assume a world-space vertical line equals the correct anatomical longitudinal path. Keep the acoustic plane/orientation sufficiently consistent to interpret temporal image changes. Select and document the scan orientation before interpreting peaks.
- Capture **clean** ultrasound frames after *accepted* physical actions; pair each image with its true preceding executed action. Record rejected, clamped, repeated and reversal actions separately.
- Use cumulative **relative executed motion** or an auditable action-history-derived coordinate for the model-side 1D signal. Do not pass simulator absolute probe location to the model. Separately log exact Unity geometry only to privileged evaluation artifacts.
- Maintain workspace, physical-contact, image-validity and continuity checks. If the current movement constraints prevent T2–T8 coverage, report that and propose a scoped change; do not simply disable the constraints.
- Positive acceptance requires a fresh T7 return observation. A remembered good frame or a high image-quality score is not a successful reacquisition.
- Do **not** use the existing privileged `ScoreCandidate` or marker-derived T7 distance as the ultrasound-only event signal or target-selection input.

## 5. Stage gates and execution recipe

### G0 — Anatomical reference and observability feasibility

**Before modifying a navigator**, inspect the live scene/prefab, active ultrasound renderer, physical bone/rib/lamina structures, T-level metadata, and relevant skin-contact corridor. Distinguish an anatomical marker from a signal-generating structure. Establish independently evaluable T2..T8 reference regions/centers in a **privileged-only** artifact. Verify a safe known-T2 start can be established and that a T2-to-T8 corridor exists.

If annotation is incomplete (especially T2–T5), report missing evidence and request approval for minimal diagnostic-only reference annotation. Do not fabricate centers by evenly dividing T6–T8 spacing. If a level has no visible distinct ultrasound event, record that as an observability finding, not a code defect.

**Pass G0 only with:** documented actual references, visible scan examples, independently checkable correspondence and a demonstrated feasible physical corridor. Otherwise mark `BLOCKED` or `INCONCLUSIVE`.

### G1 — Real longitudinal sweep and data integrity

First produce **one** complete, physically executed sweep before implementing complex counting. Start with a controlled near-T2 pose, move consistently caudally with constrained steps to the validated T8 region, keep contact and orientation, and log clean ultrasound images. Expand the old workspace only as justified by scene geometry and motion tests.

Log: action command, accepted/rejected, relative displacement estimate, frame identifier/hash, contact validity, workspace validity, orientation information for audit, sample time and seed/configuration. Store privileged world poses separately from actor-visible observations.

Visualize: trajectory against independent T2..T8 reference regions, a chronological ultrasound contact sheet, and any frame/dropout or contact violations.

**Pass G1 only if:** actual traversed corridor provides T2..T8 coverage with sufficient valid sampling to inspect each region, no unauthorized teleport, and no unhandled contact/workspace violations. If coverage fails, do not grade signal detection yet.

### G2 — An ultrasound signal that captures anatomical transitions

Implement the simplest interpretable image-derived signal(s) first. Candidates: bright superficial bone-interface response, dark distal acoustic-shadow fraction, visible bone-to-interspace transitions, or a combined feature that accounts for imaging depth. Use ROI selection, normalisation, smoothing and artefact rejection only where justified by images.

Generate a **signal versus cumulative longitudinal displacement** plot, ideally showing multiple independent feature candidates, plus representative frames at predicted peaks, valleys and transitions. Raw inter-frame image difference alone is not a vertebral detector: ordinary movement, frame registration, probe tilt and rendering artefacts can produce changes.

Check whether candidate changes are (a) repeatable on resweeps, (b) consistent under reasonable orientation/step changes, and (c) associated with a meaningful anatomical structure in privileged ground-truth review.

**Pass G2 only if:** at least one observable feature provides independently reviewable anatomical event evidence. A visually periodic chart with no anatomical correspondence is not a pass. If weak or ambiguous, report `INCONCLUSIVE`, preserve the figure, and stop adding downstream logic.

### G3 — Detect, consolidate and enumerate events

Implement an interpretable event detector: e.g. smoothing -> peak/valley or state-transition candidates -> hysteresis -> spatial non-max suppression/merge -> ordered event list. Determine whether the event represents a vertebral structure, a rib, an interspace or a repeating pair. Do **not** equate one peak to one vertebral level without validation.

Consolidate multiple consecutive frames of the **same physical structure** into one event. Flag missing, fused, duplicate or low-confidence events. Do not force exactly seven events, insert synthetic events, or hardcode periodic spacings simply to produce T7.

Given the **known T2 start in this controlled pilot**, enumerate only when seven *independently detected and anatomically validated* vertebral-level events are aligned in order:

| Detected event | Interpreted level |
| --- | --- |
| V1 | T2 |
| V2 | T3 |
| V3 | T4 |
| V4 | T5 |
| V5 | T6 |
| V6 | **T7** |
| V7 | T8 |

Select **V6** only when supported by the event sequence. Without a known start anchor, relative event count alone does **not** establish an absolute T label. Return `INCONCLUSIVE` if event count, mapping or direction is ambiguous.

### G4 — Physically revisit T7 and confirm

After reaching the verified end of the sweep, retrieve the exploration-relative position and supporting frames for V6. Navigate backwards with real, contact-valid, bounded actions and action-history tracking. No resetting probe pose directly to a T7 transform, and no ground-truth distance-to-T7 steering.

Reacquire a **new** clean ultrasound image at the returned site. Compare the new observation with the remembered V6 appearance and neighbouring event structure using only allowed image/history information. Keep the local **recognition/confirmation** decision separate from independent Unity scoring.

The output is a Phase 1 handoff at the T7 *vertebral level*; do not perform lateral target acquisition, fine placement or the Phase 2 final STOP inside this skill.

**Pass G4 only when:** the selected event is correct, physical return succeeded, fresh image confirmation is valid, and independent ground-truth evaluation places the final probe within the predeclared tolerance.

## 6. Evaluation protocol — require both component and end-to-end results

### Dataset split and execution

- **Smoke:** at least 3 independently executed full sweeps to show basic feasibility; not enough to claim reliability.
- **Development:** initially 20 full sweeps or justified equivalent to select ROI, signal, event logic, thresholds and return parameters; vary step spacing, scan direction/replay checks, minor yaw/tilt and lateral offsets while preserving fixed anatomy and known-T2 assumption.
- **Locked evaluation:** freeze code and parameters, then run 30–50 independently executed held-out sweeps (or report the actual smaller number and no verified claim). Do not tune on locked outcomes. Preserve seeds, config, code revision, renderer/Unity version and data provenance. Clearly distinguish multiple recordings of the same path from independent trajectories.
- **Negative cases:** partial coverage, missing/ambiguous event, duplicate peak, wrong-known-start, blocked motion/contact loss, wrong-T6/T8 return and stale image. Reject or abstain rather than fabricate certainty.

### Metric definitions

**G0 reference:** every alleged T2..T8 level has a documented privileged reference matching actual ultrasound-accessible anatomy. A marker name alone is not ground truth validation.

**G1:** per-level coverage, valid ultrasound frame count, fraction of accepted actions, rejected/clamped actions, continuous physical travel and contact/workspace violations.

**G2/G3:** detect a list of event centers or intervals in *actor-relative scan coordinates*, then independently match to Unity reference events in the same sweep using a predeclared **one-to-one, order-preserving assignment** with spatial tolerance. Start with tolerance `<= 0.25 x nearest adjacent reference-level spacing` as a proposal, confirm a physically sensible threshold on development data, freeze it before test. Report true positives, false positives, false negatives, precision, recall, F1, misses, doubles and whether the event type represents vertebrae or ribs/interspaces. Do not match one detected event to multiple levels.

**G3:** report exact ordered seven-level accuracy **separately** from event-count accuracy; T7 event ID/position accuracy; false and uncertain absolute-level assignments.

**G4:** report exact returned T7 identity, fresh confirmation success, longitudinal and lateral error against privileged reference, return time/number of actions, physical contact/workspace validity and false acceptances. A **provisional** return tolerance is `min(5 mm, 0.25 x nearest adjacent reference-level spacing)` in longitudinal distance; establish lateral tolerance from the intended scan corridor before final testing. Being near T7 is not enough if image-based enumeration did not lead to the choice.

### Proposed acceptance targets (not measured results)

Use these only **after user approval and after G0 establishes feasible references**:

| Measurement | Proposed target |
| --- | --- |
| Full T2..T8 coverage in eligible episodes | >= 95% |
| Matched anatomical event precision/recall/F1 | F1 >= 0.95, report both P and R |
| Exact seven-level sequence | >= 90% |
| Correct V6 = T7 inference | >= 90% |
| Real physical T7 return + fresh reacquisition | >= 90% |
| Wrong-T7 acceptance on locked negative suite | 0 observed (report denominator; not proof risk is zero) |
| Unhandled contact/workspace violations | 0 |

**Overall Phase 1 PASS** requires G0, G1, G2, G3, G4 all passed for the same episodes **and** the locked evaluation meeting user-approved targets. Report per-gate performance and end-to-end success; never multiply, substitute or mix episode denominators. A failed or absent reference makes performance **not evaluable**, not 100% accurate.

## 7. Required experiment artifacts and report

Keep evidence in a dedicated run-specific folder, for example `Artifacts/ThoracicUSCoach/sweep_count/<run_id>/` (adapt to existing project conventions without overwriting historical runs):

- `trace.json` — commanded/accepted actions, relative motion, frame IDs, constraints and return steps.
- `signal.csv` — actor-side relative coordinate and image-only extracted features.
- `signal_plot.png` — features and detector events versus displacement; optional privileged reference overlay **clearly labelled evaluator-only**.
- `contact_sheet.png` — ultrasound observations near predicted events and ambiguities.
- `events.json` — raw candidate events, grouped events, confidence and V1..V7 assignments or explicit uncertainty.
- `evaluation.json` — independently computed matches, physical errors, gate outcomes and failures.
- `REPORT.md` — human-readable methods, code/scene revision, Unity execution evidence, success denominators and links to artifacts.

A useful `REPORT.md` includes:

1. **Scope and actual environment** (branch, commit, Unity version, scene, mode, fixed-anatomy/known-T2 assumptions).
2. **What was changed** (file-by-file) and what was intentionally preserved.
3. **What really ran**: unit tests, Unity EditMode tests, Unity Play Mode sweeps, counts, seeds, held-out protection and missing tests.
4. **G0..G4 results** with denominators and individual PASS/FAIL/INCONCLUSIVE/NOT_RUN.
5. **Signal evidence** and anatomical explanation for peaks/valleys; counterexamples.
6. **Detected vs Unity reference events**; exact counting and T7 inference evidence.
7. **Return-to-T7 evidence**: trace, fresh image, independent error and rejected actions.
8. **Failures, limitations and recommended smallest next step.**

Never describe a plot from artificial data as a measured Unity outcome. Never report clinical validity or patient-level generalisation.

## 8. Testing and reporting discipline

Use appropriate existing Python and C#/Unity test suites; add focused unit tests for event clustering, hysteresis, duplicate/missing events, order-preserving matching, no-GT-leakage, reversed action handling, aborted sweep and return safety. Where the Unity Editor is unavailable, run only accessible static/unit tests, state `Unity Play Mode NOT_RUN`, and provide concrete commands/steps for the user to verify locally. Do not label a Python-only simulation as an executed Unity sweep.

For long jobs, provide concise progress with exactly what has finished, current gate and any blocker. Do not keep training or repeatedly widen the task to hide failure. Stop at the first failed scientific gate unless the user approves a specific follow-up experiment.

**Final response format:**

- `Implementation:` precise file changes, or `No changes`.
- `Verification:` commands/tests actually run and PASS/FAIL/NOT_RUN.
- `Experiment:` G0..G4 outcomes and end-to-end success with denominators.
- `Evidence:` paths to plots, images, traces and evaluation files.
- `Limitations:` what is still unproven, including fixed anatomy and known-start dependency.
- `Decision needed:` one concrete request for user approval when a new stage, architecture or evaluation change is needed.

## 9. Anti-patterns that invalidate a result

- Derive T7 from `t7RightSeed`, target geometry, world pose or a hardcoded absolute path, while claiming ultrasound-only localisation.
- Construct seven peaks by filtering/spacing to fit the expected answer; count ribs or shadows as vertebrae without validation.
- Use prerecorded best-view scores, oracle guidance or `ScoreCandidate()` to accept T7.
- Stop automatically on the sixth time-series fluctuation without grouping repeated frames of one anatomical structure.
- Teleport from the scan end to T7 or call a remembered high-score image a fresh confirmation.
- Change a threshold after looking at locked-test results and still call the test held-out.
- Confuse guided/oracle success, training success, unit-test success or passing contact checks with **image-only Phase 1 success**.
- Claim success based on only one memorable good example while omitting failed or incomplete episodes.

**Operating principle:** observe real ultrasound changes, demonstrate their anatomical meaning, count only validated distinct events, return using only scan history, verify independently, and abstain when evidence is insufficient.
