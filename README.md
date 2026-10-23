# Thoracic US Coach — standalone debug project

This repository is an isolated Unity 6000.3.9f1 project containing the Thoracic US Coach proof of concept and its required Unity asset dependencies. It is simulator-only, provisional, and not clinically validated.

## Interactive guided 2D demo

This is a **geometry-guided, simulator-only 2D engineering demo**. It is not autonomous ultrasound-only vertebral identification, a learned 2D policy, or a clinically validated system. The UI intentionally labels privileged simulator geometry as `GUIDED / ORACLE-ASSISTED`.

### Open and run the demo

1. Open this project with Unity `6000.3.9f1`.
2. Use **Tools > Thoracic US Coach > Open 2D Demo**, or open `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity` directly.
3. Enter Play Mode. The `THORACIC US COACH — 2D DEMO` panel appears without running the old discovery shortcut.
4. Select `Near T6`, `Near T8`, or `Random T6/T8`; keep or edit the reproducible seed.
5. Click **Start 2D Demo**. Watch the probe physically follow the curved skin through longitudinal search, T7 acquisition, lateral search, fine placement, and explicit final STOP.
6. Read the `2D GUIDED DEMO RESULT` panel. Green/success is only produced after geometry, two-axis motion, contact, workspace, image score, and explicit STOP validation all pass.

The demo uses the existing validated candidate manifest in `Artifacts/ThoracicUSCoach/candidates/candidate_manifest.json`. If it is missing, the demo refuses to claim a result and asks for one candidate-discovery run.

### Controls

| Control | Actual behavior |
|---|---|
| Start 2D Demo / `F2` | Initializes one deterministic run at the selected non-target start. |
| Pause / `F4` | Pauses before the next physical movement step. |
| Resume / `F4` | Continues a paused run. |
| Single Step | While paused, permits exactly one physical movement step. |
| Reset / `F3` | Stops the owning coroutine and clears trace, phase, STOP, and result state. |
| Run Again | Performs Reset and starts the same selection/seed again. |
| Cranial / Caudal | Performs one validated longitudinal skin-tangent move while no automatic run owns the probe. |
| Medial / Lateral | Performs one validated transverse skin-tangent move through the same projection/contact checks. |
| Show Guidance / Ground Truth | Shows/hides privileged trajectory and target gizmos; it does not alter navigation or success. |
| Speed | Changes only playback delay, not geometry, tolerances, or movement length. |

Existing 1D shortcuts (`D`, `C`, `P`, arrow keys, `Space`, `R`, `G`) remain unchanged.

### What the phases mean

- `LONGITUDINAL SEARCH`: incremental cranial/caudal movement until the physical contact point is inside the T7-level tangent-coordinate tolerance.
- `T7 ACQUIRED`: the T7 level was geometrically validated; this is not final placement success.
- `LATERAL SEARCH`: incremental medial/lateral movement begins at the actual Phase A endpoint.
- `FINE PLACEMENT`: bounded incremental motion reaches the previously discovered valid candidate contact.
- `FINAL SUCCESS` / `FINAL FAILURE`: an explicit STOP was issued and the complete validation result is shown with a failure reason when applicable.

The debug values (target distance, tangent coordinates, target markers, score floor, and guidance trail) are privileged simulator diagnostics. They are not ultrasound-only policy inputs.

Runtime traces are written to `Artifacts/ThoracicUSCoach/guided2d/runs`; screenshots are written to `Artifacts/ThoracicUSCoach/guided2d/screenshots` when the active Unity graphics environment supports capture.

### Troubleshooting

- **UI not visible:** open the exact PoC scene, enter Play Mode, and maximize the Game view. The panel is runtime IMGUI, not an Inspector field.
- **Play Mode not started:** all movement buttons intentionally require Play Mode.
- **Missing T6/T7/T8 marker or skin:** run **Tools > Thoracic US Coach > Validate PoC Scene** and repair the named scene reference.
- **Missing candidate / invalid final target:** press `D` once in Play Mode to rebuild the existing candidate manifest, then Reset and replay the same seed.
- **Probe loses contact or leaves workspace:** the step is rejected, the previous pose is restored, and the result reports the exact constraint failure. Use Reset rather than forcing a success.
- **Ultrasound not updating:** confirm the Primary ultrasound camera/display are enabled and inspect the Console for `UltrasoundManager` errors.
- **Demo pauses unexpectedly:** click Resume or press `F4`. Single Step deliberately leaves the run paused.
- **Compilation errors:** open Console, fix the first C# error, then wait for domain reload before entering Play Mode.
- **Failed/incomplete run:** read the failure reason and JSON trace, Reset, restore the same start/seed, and Run Again.

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

