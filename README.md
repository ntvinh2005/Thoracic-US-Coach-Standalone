# Thoracic US Coach — standalone debug project

This repository is an isolated Unity 6000.3.9f1 project containing the Thoracic US Coach proof of concept and its required Unity asset dependencies. It is simulator-only, provisional, and not clinically validated.

The active 1D task is `thoracic-1d-contract-v3`. It uses an exact frozen pose-target set at 97% of the discovery-best simulator score, a canonical physical action direction, genuine executed history transitions, explicit STOP plus contact/workspace success, and distinct termination/time-limit truncation. Existing diversity-v2 data and metrics remain archived and are never silently reused as v3 evidence.

Codex Unity MCP is configured locally with the project pinned to this repository. The installed project package is `com.unity.pipeline` `0.9.0-exp.1`; a new Codex session can use the `unity` MCP server. The project still works through Unity Hub and the file bridge when MCP is unavailable.

## Open and run

1. In Unity Hub, choose **Add > Add project from disk** and select this repository.
2. Open `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity`.
3. Enter Play Mode.
4. Press `D` for candidate discovery. Press `C` only when you intentionally want to capture fresh contract-v3 data. Run a v3 policy server before pressing `P`.

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

The historical v2 dataset, USFM checkpoint/cache, Unity `Library`, and runtime bridge are locally available but ignored by Git. Fresh v3 data must be captured and validated before v3 BC/PPO claims are made.

For the full pipeline commands, limitations, and measured results, see `Tools/ThoracicUSCoach/README.md`.

## Important limitations

- Private CSSALT packages in `Packages/manifest.json` require your existing Git/SSH access.
- The PPO checkpoint is an engineering result with high seed variance, not evidence of clinical success.
- An unrelated missing `ATC64` DLL warning and pre-existing missing-prefab warnings may still appear.

