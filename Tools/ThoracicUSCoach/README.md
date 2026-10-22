# Thoracic US Coach proof of concept

This is a simulator-only, provisional T7-right axial/transverse probe-coaching PoC. It is not clinician approved and makes no claim about real-ultrasound or patient performance.

## Active 1D environment contract (v3)

The active experiment is `thoracic_1d_contract_v3_20261022`. Historical diversity-v2 captures and reports are preserved, but their observation/transition and target semantics are not mixed with v3 training.

- Observation: the actor receives the current clean ultrasound PNG, the previous clean PNG, and the action actually executed between them. At reset, previous=current and STOP is the declared memory-initialization sentinel; schema-v3 records mark that sentinel with `actionWasExecuted=false`. Pose, score, path index, target distance, target mask, and anatomy level remain privileged and are never policy inputs.
- Actions: Backward=`-1`, Stop=`0`, Forward=`+1`. Forward always increases the canonical skin-corridor coordinate in millimetres; Backward decreases it. A move is clamped at an endpoint and reported as invalid/boundary motion when it produces no displacement.
- Target: `fixed-pose-exact-set`. The score floor is frozen at 97% of the discovery-best simulator score when a constrained path is built. Teacher action, distance, progress reward, region entry, and STOP success all use that exact set; gaps between acceptable islands remain unacceptable. Rerendered scores are diagnostics only.
- Success: only an explicit STOP at an exact acceptable pose with valid contact and workspace succeeds. STOP always terminates. A time limit truncates without being treated as terminal for value bootstrapping.
- Reward: `-0.01` per step, `+0.05 * reduction_in_target_distance_mm`, `-0.1` for clamped motion, `+1` for correct STOP or `-0.5` for wrong STOP, and `-1` for invalid contact/workspace.

Schema-v3 manifests hash configuration, frozen labels, and every clean transition image. History training uses genuine executed transition pairs, including active probe, reversal, and boundary-clamp coverage; it no longer combines adjacent frames with an action that was never executed.

## Historical diversity v2

The diversity-v2 dataset and its reports are retained as historical evidence. They are not fresh measurements of the contract-v3 implementation.

Experiment `thoracic_diverse_v2_20261020` generates a new constrained trajectory for every sweep. Seeded configuration varies center translation, length, path angle/direction, sample count, yaw/tilt endpoints, and start side. Each point is projected back to the real skin collider and rejected if it leaves the bounded patch, loses rigid contact, jumps discontinuously, or cannot render. A fixed discovery score floor defines the target for the whole experiment; a resumed session reloads that floor instead of changing labels with render jitter.

Configuration families are assigned to train/dev/test before capture and never cross splits. Stress trajectories may intentionally contain no acceptable target; those frames use `teacherActionDefined=false` instead of a fabricated STOP label. Capture is transactional: files are written under `.partial`, the completed directory is committed, and only then is the index rebuilt. Loaders ignore `.partial` directories.

The deployed request contains only clean ultrasound pixels, the previous clean frame, and previous action. Pose, path index, masks, labels, score, target geometry, and surface normals are not policy features. Unity geometry remains available only for constraints, labels, rewards, and evaluation.

## Run in Unity

1. Open `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity` in Unity 6000.3.9f1 and enter Play Mode.
2. Press `D`, or select `Tools > Thoracic US Coach > Run Candidate Discovery`.
3. Press `C` to capture one new independent contract-v3 sweep, or select `Capture Diverse Pilot Dataset` to resume transactionally toward 84 sweeps.
4. Up/Down/Space are manual Forward/Backward/Stop, `R` resets, and `G` toggles the separate ground-truth explanation.

To run the selected image-policy candidate:

```powershell
cd Tools\ThoracicUSCoach
.\.venv\Scripts\python.exe run_policy_server.py
```

Keep that terminal open, then press `P` or select `Tools > Thoracic US Coach > Run Image Policy Demo`. The loop is clean PNG history -> prediction -> constrained probe step -> new clean PNG. The selected PPO candidate is an engineering checkpoint, not a clinically deployable model.

## Contract-v3 diagnostics and reproduction

The lightweight environment needs only NumPy and Pillow. The already-installed Anaconda PyTorch is used for USFM, so no CUDA build, torchvision, or full timm package is added.

```powershell
cd Tools\ThoracicUSCoach

# Pure deterministic contract and exact PPO-gradient tests.
& "$env:USERPROFILE\anaconda3\python.exe" -m unittest test_contract_v3.py test_ppo_math.py -v
& "$env:USERPROFILE\anaconda3\python.exe" contract_v3.py --output ..\..\Artifacts\ThoracicUSCoach\diagnostic_contract_v3.json

# With the Unity scene in Play Mode after discovery, measure renderer noise.
& "$env:USERPROFILE\anaconda3\python.exe" diagnose_score_noise.py

# Freeze development geometries before selection.
& "$env:USERPROFILE\anaconda3\python.exe" freeze_1d_scenarios.py --group development
& "$env:USERPROFILE\anaconda3\python.exe" evaluate_live.py --teacher-only --manifest ..\..\Artifacts\ThoracicUSCoach\manifests\one_d_development_contract_v3.json --output ..\..\Artifacts\ThoracicUSCoach\metrics\live_oracle_development_contract_v3.json

# Train only from fresh schema-v3 data. History mode refuses v2 pseudo-history.
& "$env:USERPROFILE\anaconda3\python.exe" train_bc_policy.py --observation current --epochs 500 --output ..\..\Artifacts\ThoracicUSCoach\checkpoints\bc_pixel_current_contract_v3.json
& "$env:USERPROFILE\anaconda3\python.exe" train_bc_policy.py --observation history --epochs 500 --output ..\..\Artifacts\ThoracicUSCoach\checkpoints\bc_pixel_history_contract_v3.json

# Corrected PPO; repeat with --seed 13, 37, and 101 under the same budget.
& "$env:USERPROFILE\anaconda3\python.exe" ppo_live_refine.py --seed 13 --updates 10 --episodes-per-update 12

# Freeze and lock heldout only after model selection, then verify on every run.
& "$env:USERPROFILE\anaconda3\python.exe" freeze_1d_scenarios.py --group heldout
& "$env:USERPROFILE\anaconda3\python.exe" lock_scenario_manifest.py --manifest ..\..\Artifacts\ThoracicUSCoach\manifests\one_d_heldout_contract_v3.json
& "$env:USERPROFILE\anaconda3\python.exe" lock_scenario_manifest.py --verify --manifest ..\..\Artifacts\ThoracicUSCoach\manifests\one_d_heldout_contract_v3.json
& "$env:USERPROFILE\anaconda3\python.exe" evaluate_live.py --checkpoint ..\..\Artifacts\ThoracicUSCoach\checkpoints\policy_best_dev_contract_v3.json --manifest ..\..\Artifacts\ThoracicUSCoach\manifests\one_d_heldout_contract_v3.json --lock ..\..\Artifacts\ThoracicUSCoach\final_scenario_lock_contract_v3.json --include-teacher --output ..\..\Artifacts\ThoracicUSCoach\metrics\live_heldout_contract_v3.json

# Historical v2 audit remains reproducible only with its explicit identity.
& "$env:USERPROFILE\anaconda3\python.exe" audit_dataset.py --experiment-id thoracic_diverse_v2_20261020

# Historical v2 final-test identity.
& "$env:USERPROFILE\anaconda3\python.exe" lock_final_test.py --verify --experiment-id thoracic_diverse_v2_20261020

# Pixel baselines selected on train/dev only.
.\.venv\Scripts\python.exe train_bc_policy.py --observation current --epochs 500 --output ..\..\Artifacts\ThoracicUSCoach\checkpoints\bc_pixel_current_diverse_v2.json
.\.venv\Scripts\python.exe train_bc_policy.py --observation history --epochs 500 --output ..\..\Artifacts\ThoracicUSCoach\checkpoints\bc_pixel_history_diverse_v2.json
.\.venv\Scripts\python.exe train_reference_head.py --features pixels --epochs 500 --output ..\..\Artifacts\ThoracicUSCoach\checkpoints\reference_head_pixels_diverse_v2.json

# Official frozen USFM checkout belongs at Library/USFM-official, pinned by source_manifest.json.
& "$env:USERPROFILE\anaconda3\python.exe" usfm_adapter.py verify --checkpoint ..\..\Artifacts\ThoracicUSCoach\usfm\USFM_latest.pth --device cpu
& "$env:USERPROFILE\anaconda3\python.exe" usfm_adapter.py extract-dataset --checkpoint ..\..\Artifacts\ThoracicUSCoach\usfm\USFM_latest.pth --device cpu --batch-size 8 --output ..\..\Artifacts\ThoracicUSCoach\usfm_features_diverse_v2.npz
.\.venv\Scripts\python.exe train_reference_head.py --features usfm --usfm-cache ..\..\Artifacts\ThoracicUSCoach\usfm_features_diverse_v2.npz --epochs 1000 --output ..\..\Artifacts\ThoracicUSCoach\checkpoints\reference_head_usfm_diverse_v2.json
.\.venv\Scripts\python.exe train_usfm_action_head.py --usfm-cache ..\..\Artifacts\ThoracicUSCoach\usfm_features_diverse_v2.npz --epochs 1000
```

## Fresh contract-v3 verification (2026-10-22)

- Pure deterministic suite: 9/9 reachable cases solved by the oracle, including both directions, endpoints, immediately-outside/far starts and disjoint acceptable islands; 0 invalid success flags. Reversal, boundary clamp, target absent, timeout, wrong STOP, bad contact and bad workspace probes also passed.
- Python unit tests: 7/7 passed, including finite-difference PPO gradients for positive/negative advantages in clipped/unclipped regions and singleton termination/truncation GAE signals.
- Unity EditMode tests: 11/11 passed.
- Repeated live pose rendering: maximum score standard deviation 0.0 over eight repeats per pose; fixed-target/current-image label agreement 1.0.
- Frozen development manifest: 6 independent geometries and 22 unique geometry/start pairs, split into 10 recognition starts and 12 acquisition starts. The live teacher oracle succeeded 22/22, with 0 wrong STOP, boundary failure, timeout, or constraint violation.

No schema-v3 dataset has been captured yet. Therefore corrected BC and three-seed PPO were not trained or compared, no learned policy has passed the proposed 95% easy-suite gate, and no v3 deployment checkpoint is selected. Heldout/final v3 scenarios remain deliberately unfrozen until model selection; historical v2 numbers are not used as before/after evidence.

Actual PPO requires the Unity scene in Play Mode after discovery. It samples actions from the current policy, sends them to Unity, receives new live observations/rewards, and optimizes a clipped PPO objective with Monte-Carlo returns. `ppo_refine.py` is retained but explicitly labels itself a clipped replay objective, not PPO.

```powershell
.\.venv\Scripts\python.exe ppo_live_refine.py --bc ..\..\Artifacts\ThoracicUSCoach\checkpoints\bc_pixel_history_diverse_v2.json --episodes 20 --eval-interval 10 --seed 13
.\.venv\Scripts\python.exe evaluate_live.py --checkpoint ..\..\Artifacts\ThoracicUSCoach\checkpoints\ppo_live_best_dev_seed13_diverse_v2.json --group heldout --include-teacher --output ..\..\Artifacts\ThoracicUSCoach\metrics\live_ppo_heldout.json
```

## Archived diversity-v2 results (not reproduced by contract v3)

The exact machine-readable record is `Artifacts/ThoracicUSCoach/validation_report_diverse_v2.json`.

- Dataset: 84 complete sweeps / 1,418 frames; 48 train, 12 dev, 12 held-out, and 12 stress. There are 84 configuration families and 84 unique pose sequences.
- Diversity audit: pass; 0 exact duplicate clean PNGs, 0 cross-split near-identical trajectories, and 0 contact violations. Dev/test train-nearest images at the legacy renderer-noise scale were 7.77% / 5.10% held-out / 1.03% stress, all below the 10% failure threshold.
- Pixel action BC: current-frame dev accuracy 0.641; one-step-history dev accuracy 0.864. History is necessary to disambiguate direction.
- Frozen USFM: official commit `960a1e1d30b9490e53e6e7cf1b4dd24425fffc67`; 195/209 backbone tensors matched (93.30%); 1,418x768 frozen embeddings. Reference-head dev accuracy/F1 were 0.864/0.851. Cached linear-head latency excludes encoder time.
- PPO best-dev explicit-STOP success across seeds 13/37/101 was 0.667/0.333/0.167. This high variance is retained and reported.
- Final locked held-out frame accuracy: BC-history 0.867, selected PPO 0.633, USFM reference 0.888, and USFM action 0.663.
- Final live held-out explicit-STOP success: BC-history 0.333; selected PPO 0.400; teacher oracle 1.000. Final live stress reachable success: BC-history 0.200; PPO 0.200; teacher 1.000. All live runs had 0 contact violations.

The PPO result is only marginally better than BC in live held-out acquisition and remains far below the oracle. It must not be described as clinically successful.

## Known limitations

- Candidate/target scores and acceptable ranges are provisional simulator hypotheses, not clinical labels.
- One synthetic simulator/anatomy does not provide patient, operator, probe-pressure, tracking-noise, or real-ultrasound diversity.
- The 3-action policy has no explicit abstain/failure action for target-absent stress cases.
- PPO seed variance and boundary-sticking remain unresolved engineering failures.
- PyTorch is CPU-only; the local 4 GB GPU was not used to avoid a large CUDA installation.
- The simulator still logs an unrelated missing `ATC64` DLL and pre-existing missing-prefab issues.
