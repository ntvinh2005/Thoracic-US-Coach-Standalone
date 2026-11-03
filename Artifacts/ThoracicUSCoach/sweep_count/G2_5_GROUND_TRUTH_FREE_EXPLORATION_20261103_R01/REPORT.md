# G2.5 Ground-Truth-Free Thoracic Exploration

## Outcome

**PARTIAL IMPLEMENTATION; FROZEN CONFIRMATION NOT AUTHORIZED BY READINESS.**

The acquisition planner no longer queries LF T2/T8, BuildSweepCountReferences, or an anatomy-derived corridor in its opt-in ground-truth-free mode. It starts from the scene-authored operator pose projected to generic skin, derives the longitudinal axis from the robot/world-down surface tangent, traces generic skin continuity, integrates achieved motion, and physically recovers to Home after a guarded failure.

The landmark objective did not pass. Two authorized development sessions were consumed. The first nonprivileged corridor was laterally uninformative. A one-rendered-footprint medial mechanical shift improved the second corridor, but the episode stopped at frame 0167 when scan-width alignment was 0.948635 against the locked 0.95 minimum. Physical recovery then returned to Home at +0.001664 mm, but the original failure artifacts remained serialized as RUNNING; the persistence race is fixed and covered by tests.

Frozen actor inference on the 168 persisted session-02 frames accepted two events at +4.499 and +26.999 mm and emitted no T7 target. Post-freeze evaluation matched them to T6 (4.805 mm error, fragile) and T7 (1.419 mm error). T2 was outside the executed path and was not extrapolated. The covered-reference score was TP=2, FP=0, FN=4, F1=0.50. The actor correctly abstained.

## Information boundary

- Actor setup used the scene-authored operator pose, generic skin contact, robot/world-down tangent, a 120 mm mechanical ceiling, and the known rendered footprint.
- The one-footprint medial development change was motivated by actor-visible failure and a mechanical medial convention; no LF coordinate entered the controller.
- The frozen model used only ultrasound pixels and executed Home-relative motion.
- R1 reference anatomy was loaded only after blind_results.sha256 was written.
- Evaluator references outside actually executed world-z coverage were reported null rather than extrapolated.

## Experiments

| Experiment | Physical result | Actor result | Evaluator result |
|---|---|---|---|
| Offline causal replay of prior confirmation | No new motion | 7 events after two directions, but edge margins 0.0/2.5 mm required more coverage | Not rescored |
| Development 01, scene-authored Home | 177 fresh frames, 176 accepted moves, range -84.004..+90.003 mm; episode finalization invalid | 1 accepted event, no map | Accepted event unmatched; T2 outside path |
| Development 02, one-footprint medial | 168 fresh persisted frames, 167 accepted moves, range -94.501..+78.005 mm; guard failure; 63-step recovery to +0.001664 mm | 2 accepted events, 5 rejected, map inconclusive | T2 outside path; TP=2/6 covered, FP=0 |

Development budget: **2/2 sessions consumed**. Frozen confirmation: **NOT_RUN_READINESS_FAILED**. G4: **NOT_RUN_OUT_OF_SCOPE**.

## Gates

| Gate | G2.5 verdict | Basis |
|---|---|---|
| G0 | PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION | Historical validated references preserved |
| G1 | FAIL | Neither development episode completed a fully valid capture-and-return contract, although session 02 physically recovered |
| G2 | FAIL | Only 2/6 covered references accepted; T2 not covered |
| G3 | INCONCLUSIVE | Seven-level map absent; T7 target null |
| G4 | NOT_RUN_OUT_OF_SCOPE | No physical T7 return authorized |

## Scientific interpretation

The frozen landmark recognizer remains effective on the previously anatomy-planned corridor, but a generic longitudinal sweep is not sufficient to discover that imaging corridor. The decisive remaining capability is **causal lateral corridor acquisition**: the actor must compare ultrasound likelihood while making bounded mechanically defined lateral moves, choose a useful corridor without LF coordinates, then execute a complete two-direction scan with robust scan-plane alignment.

Absolute T2/T8 identity was not established. Even a future seven-event result under the approved region-contains-T2-T8 prior would be CONDITIONAL_T2_T8_ENUMERATION, not ground-truth-free absolute endpoint identification, unless an independent image anchor is demonstrated.

## Verification

- Python focused tests: 14/14 PASS.
- Unity licensed graphics/UI EditMode tests: 8/8 PASS.
- Review-worktree full EditMode regression: 170/171 PASS; the sole failure is the pre-existing unresolved scene GUID audit on committed scene SHA 60fef5...
- Historical execution scene before/after: ce9527f22e004772117c416465641b3cf3609979830607b636597eb374fb1131.
- Skeleton FBX and UltrasoundManager hashes are unchanged.
- One headless Unity attempt was license-blocked; the legitimate licensed graphics/UI path then passed.

## Evidence

- Actor-only frozen evidence: actor_only_evidence/
- Evaluator-only score: evaluator_only/anatomical_correspondence.json
- Session 01: development_session_01/
- Session 02: development_session_02/
- Tests: test_results/
- Research decisions: research_experiment_log.md

## Required next approval

No further physical development session is authorized. The smallest next campaign is one bounded development session for an actor-visible lateral corridor search (for example, three mechanically defined lateral offsets selected by image likelihood), followed only if successful by one separately frozen full confirmation. The controller must also retain the final-status persistence fix and reject/flag invalid terminal captures.
