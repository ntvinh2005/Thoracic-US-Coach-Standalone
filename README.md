# Thoracic US Coach — standalone debug project

This repository is an isolated Unity 6000.3.9f1 project containing the Thoracic US Coach proof of concept and its required Unity asset dependencies. It is simulator-only, provisional, and not clinically validated.

## Open and run

1. In Unity Hub, choose **Add > Add project from disk** and select this repository.
2. Open `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity`.
3. Enter Play Mode.
4. Press `D` for candidate discovery, then `P` for the image-policy demo. Press `C` only when you intentionally want to capture another independent sweep.

Run the Python policy server in another PowerShell window:

```powershell
cd Tools\ThoracicUSCoach
uv sync
.\.venv\Scripts\python.exe run_policy_server.py
```

If `uv` is unavailable, use an existing Python environment and install only the dependencies listed in `pyproject.toml`. The lightweight policy path needs NumPy and Pillow; USFM workflows additionally require an existing PyTorch installation.

## Included

- `Assets/ThoracicUSCoach` plus every Unity asset dependency exported by Unity.
- `Tools/ThoracicUSCoach`: capture audit, training, evaluation, PPO, USFM adapter, and policy-server code.
- Curated diversity-v2 artifacts: checkpoints, metrics, audit, final-test lock, validation report, candidates, 84-sweep dataset, and the local USFM checkpoint/cache.
- `Setup/ThoracicUSCoachStandalone.unitypackage`: a recovery snapshot of the Unity asset export.

The dataset, USFM checkpoint/cache, Unity `Library`, and runtime bridge are locally available but ignored by Git. This keeps normal commits small while preserving a ready-to-debug working copy on this machine.

For the full pipeline commands, limitations, and measured results, see `Tools/ThoracicUSCoach/README.md`.

## Important limitations

- Private CSSALT packages in `Packages/manifest.json` require your existing Git/SSH access.
- The PPO checkpoint is an engineering result with high seed variance, not evidence of clinical success.
- An unrelated missing `ATC64` DLL warning and pre-existing missing-prefab warnings may still appear.

