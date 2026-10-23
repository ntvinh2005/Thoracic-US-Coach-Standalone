"""Drive the Unity 2D demo bridge using only Python's standard library."""

from __future__ import annotations

import argparse
import json
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path


def request(bridge: Path, command: str, timeout: float = 10.0, **kwargs):
    request_id = uuid.uuid4().hex
    payload = {"requestId": request_id, "command": command, **kwargs}
    bridge.mkdir(parents=True, exist_ok=True)
    (bridge / "guided2d_request.json").write_text(json.dumps(payload), encoding="utf-8")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        response_path = bridge / "guided2d_response.json"
        if response_path.exists():
            try:
                response = json.loads(response_path.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError):
                time.sleep(0.05)
                continue
            if response.get("requestId") == request_id:
                return response
        time.sleep(0.05)
    raise TimeoutError(f"Unity did not answer guided 2D command {command!r}")


def wait_for_done(bridge: Path, timeout: float):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        last = request(bridge, "status")
        if last.get("done"):
            return last
        time.sleep(0.15)
    raise TimeoutError(f"2D run did not finish; last response={last}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=90.0)
    args = parser.parse_args()
    artifact = args.project / "Artifacts" / "ThoracicUSCoach"
    bridge = artifact / "bridge"

    checks = []
    request(bridge, "reset")
    for direction in ("cranial", "caudal", "medial", "lateral"):
        checks.append({"manual": direction, "response": request(
            bridge, "manual", manualDirection=direction)})
        time.sleep(0.15)

    runs = []
    matrix = [
        ("NearT6", 20261022),
        ("NearT8", 20261022),
        ("NearT6", 17),
        ("NearT8", 31),
        ("RandomT6T8", 101),
        ("RandomT6T8", 202),
    ]
    for index, (start, seed) in enumerate(matrix):
        request(bridge, "reset")
        request(bridge, "start", start=start, seed=seed, speed=4.0)
        if index == 0:
            time.sleep(0.2)
            paused = request(bridge, "pause")
            checks.append({"pause": paused})
            time.sleep(0.15)
            checks.append({"resume": request(bridge, "resume")})
        done = wait_for_done(bridge, args.timeout)
        result_path = Path(done.get("resultPath", ""))
        result = json.loads(result_path.read_text(encoding="utf-8-sig")) if result_path.exists() else {}
        runs.append({"start": start, "seed": seed, "bridge": done, "result": result})

    request(bridge, "reset")
    request(bridge, "start", start="NearT6", seed=20261022, speed=4.0)
    replay = wait_for_done(bridge, args.timeout)
    runs.append({"start": "NearT6", "seed": 20261022, "resetReplay": True, "bridge": replay})

    output = {
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "kind": "guided-2d-runtime-matrix",
        "manualAndControlChecks": checks,
        "runs": runs,
        "automaticPasses": sum(bool(item.get("result", {}).get("overallPass", item["bridge"].get("success"))) for item in runs),
        "automaticRuns": len(runs),
    }
    destination = artifact / "guided2d" / "runtime_test_matrix.json"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(output, indent=2), encoding="utf-8")
    print(destination)
    print(f"passes={output['automaticPasses']}/{output['automaticRuns']}")
    return 0 if output["automaticPasses"] == output["automaticRuns"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
