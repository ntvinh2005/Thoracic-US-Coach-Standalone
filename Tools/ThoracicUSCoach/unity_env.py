from __future__ import annotations

import json
import os
import time
import uuid
from pathlib import Path

from coach_data import project_root


class UnityFileEnvironment:
    """Small dependency-free Unity/Python interface; observations are image paths, never poses."""

    def __init__(self, bridge: Path | None = None, timeout: float = 10.0):
        self.bridge = bridge or project_root() / "Artifacts/ThoracicUSCoach/bridge"
        self.bridge.mkdir(parents=True, exist_ok=True)
        self.request_path = self.bridge / "env_request.json"
        self.response_path = self.bridge / "env_response.json"
        self.timeout = timeout
        self.retained_observation_limit = 8
        self._retained_observations: list[Path] = []
        for existing in sorted(
            self.bridge.glob("env_observation_*.png"), key=lambda path: path.stat().st_mtime
        ):
            self._retain_observation(existing)

    def reset(
        self,
        start_index: int,
        *,
        scenario_index: int = 0,
        trajectory_seed: int = 0,
        regenerate_trajectory: bool = False,
        max_steps: int = 0,
        frozen_scenario: dict[str, object] | None = None,
    ) -> dict[str, object]:
        payload: dict[str, object] = {
            "command": "reset",
            "action": 0,
            "startIndex": start_index,
            "scenarioIndex": scenario_index,
            "trajectorySeed": trajectory_seed,
            "regenerateTrajectory": regenerate_trajectory,
            "maxSteps": int(max_steps),
        }
        if frozen_scenario is not None:
            payload["replayFrozenScenario"] = True
            payload["frozenScenario"] = frozen_scenario
        return self._exchange(payload)

    def step(self, action: int) -> dict[str, object]:
        return self._exchange({"command": "step", "action": int(action), "startIndex": 0, "regenerateTrajectory": False, "maxSteps": 0})

    def describe(self) -> dict[str, object]:
        return self._exchange({"command": "describe", "action": 0, "startIndex": 0, "regenerateTrajectory": False, "maxSteps": 0})

    def _exchange(self, payload: dict[str, object]) -> dict[str, object]:
        request_id = uuid.uuid4().hex
        payload["requestId"] = request_id
        temporary = self.bridge / f"env_request_{request_id}.tmp"
        temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        replace_error: OSError | None = None
        for attempt in range(10):
            try:
                os.replace(temporary, self.request_path)
                replace_error = None
                break
            except (PermissionError, OSError) as error:
                replace_error = error
                time.sleep(0.02 * (attempt + 1))
        if replace_error is not None:
            raise replace_error
        started = time.monotonic()
        while time.monotonic() - started < self.timeout:
            if self.response_path.exists():
                try:
                    response = json.loads(self.response_path.read_text(encoding="utf-8"))
                except (OSError, json.JSONDecodeError):
                    time.sleep(0.02)
                    continue
                if response.get("requestId") == request_id:
                    if response.get("error"):
                        raise RuntimeError(str(response["error"]))
                    image_path = response.get("imagePath")
                    if image_path:
                        self._retain_observation(Path(str(image_path)))
                    return response
            time.sleep(0.02)
        raise TimeoutError("Unity environment did not answer; keep the PoC scene in Play Mode after discovery")

    def _retain_observation(self, image_path: Path) -> None:
        try:
            resolved = image_path.resolve()
            if resolved.parent != self.bridge.resolve() or not resolved.name.startswith("env_observation_"):
                return
            if resolved not in self._retained_observations:
                self._retained_observations.append(resolved)
            while len(self._retained_observations) > self.retained_observation_limit:
                stale = self._retained_observations.pop(0)
                stale.unlink(missing_ok=True)
        except OSError:
            # Retention is hygiene only; a transient antivirus/file lock must
            # never corrupt the environment transition itself.
            return
