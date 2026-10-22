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

    def reset(
        self,
        start_index: int,
        *,
        scenario_index: int = 0,
        trajectory_seed: int = 0,
        regenerate_trajectory: bool = False,
        max_steps: int = 0,
    ) -> dict[str, object]:
        return self._exchange(
            {
                "command": "reset",
                "action": 0,
                "startIndex": start_index,
                "scenarioIndex": scenario_index,
                "trajectorySeed": trajectory_seed,
                "regenerateTrajectory": regenerate_trajectory,
                "maxSteps": int(max_steps),
            }
        )

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
                    return response
            time.sleep(0.02)
        raise TimeoutError("Unity environment did not answer; keep the PoC scene in Play Mode after discovery")
