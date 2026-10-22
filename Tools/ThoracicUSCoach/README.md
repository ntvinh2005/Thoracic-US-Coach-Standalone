# Thoracic US Coach proof of concept

This is a simulator-only, provisional T7-right axial/transverse probe-coaching PoC. It is not clinician approved and makes no claim about real-ultrasound or patient performance.

## What changed in diversity v2

The obsolete `legacy_fixed_path_smoke` captures have been removed. Audit, training, and evaluation use only the diversity-v2 dataset.

Experiment `thoracic_diverse_v2_20261020` generates a new constrained trajectory for every sweep. Seeded configuration varies center translation, length, path angle/direction, sample count, yaw/tilt endpoints, and start side. Each point is projected back to the real skin collider and rejected if it leaves the bounded patch, loses rigid contact, jumps discontinuously, or cannot render. A fixed discovery score floor defines the target for the whole experiment; a resumed session reloads that floor instead of changing labels with render jitter.

Configuration families are assigned to train/dev/test before capture and never cross splits. Stress trajectories may intentionally contain no acceptable target; those frames use `teacherActionDefined=false` instead of a fabricated STOP label. Capture is transactional: files are written under `.partial`, the completed directory is committed, and only then is the index rebuilt. Loaders ignore `.partial` directories.

The deployed request contains only clean ultrasound pixels, the previous clean frame, and previous action. Pose, path index, masks, labels, score, target geometry, and surface normals are not policy features. Unity geometry remains available only for constraints, labels, rewards, and evaluation.

## Run in Unity

1. Open `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity` in Unity 6000.3.9f1 and enter Play Mode.
2. Press `D`, or select `Tools > Thoracic US Coach > Run Candidate Discovery`.
3. Press `C` to capture one new independent sweep, or select `Capture Diverse Pilot Dataset` to resume transactionally toward 84 sweeps.
4. Up/Down/Space are manual Forward/Backward/Stop, `R` resets, and `G` toggles the separate ground-truth explanation.

To run the selected image-policy candidate:

```powershell
cd Tools\ThoracicUSCoach
.\.venv\Scripts\python.exe run_policy_server.py
```

Keep that terminal open, then press `P` or select `Tools > Thoracic US Coach > Run Image Policy Demo`. The loop is clean PNG history -> prediction -> constrained probe step -> new clean PNG. The selected PPO candidate is an engineering checkpoint, not a clinically deployable model.

## Audit and reproduce

The lightweight environment needs only NumPy and Pillow. The already-installed Anaconda PyTorch is used for USFM, so no CUDA build, torchvision, or full timm package is added.

```powershell
cd Tools\ThoracicUSCoach

# Diversity/integrity gate; exits 2 on failure.
.\.venv\Scripts\python.exe audit_dataset.py

# Verify final-test identity before final evaluation.
.\.venv\Scripts\python.exe lock_final_test.py --verify

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

Actual PPO requires the Unity scene in Play Mode after discovery. It samples actions from the current policy, sends them to Unity, receives new live observations/rewards, and optimizes a clipped PPO objective with Monte-Carlo returns. `ppo_refine.py` is retained but explicitly labels itself a clipped replay objective, not PPO.

```powershell
.\.venv\Scripts\python.exe ppo_live_refine.py --bc ..\..\Artifacts\ThoracicUSCoach\checkpoints\bc_pixel_history_diverse_v2.json --episodes 20 --eval-interval 10 --seed 13
.\.venv\Scripts\python.exe evaluate_live.py --checkpoint ..\..\Artifacts\ThoracicUSCoach\checkpoints\ppo_live_best_dev_seed13_diverse_v2.json --group heldout --include-teacher --output ..\..\Artifacts\ThoracicUSCoach\metrics\live_ppo_heldout.json
```

## Measured diversity-v2 results

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
