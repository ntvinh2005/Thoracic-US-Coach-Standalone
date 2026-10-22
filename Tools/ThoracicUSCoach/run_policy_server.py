from __future__ import annotations

import argparse
import json
import os
import time
from pathlib import Path

import numpy as np

from coach_data import INDEX_TO_ACTION, project_root, request_features, stable_softmax


def atomic_write(path: Path, payload: dict[str, object]) -> None:
    temporary = path.with_name(f"{path.stem}_{time.time_ns()}.tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    error: OSError | None = None
    for attempt in range(10):
        try:
            os.replace(temporary, path)
            return
        except (PermissionError, OSError) as caught:
            error = caught
            time.sleep(0.02 * (attempt + 1))
    if error is not None:
        raise error


def main() -> None:
    parser = argparse.ArgumentParser(description="File bridge: ultrasound PNG history -> selected image-policy action; no pose/mask inputs")
    parser.add_argument("--checkpoint", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/checkpoints/policy_best_dev_contract_v3.json")
    parser.add_argument("--bridge", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/bridge")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    model = json.loads(args.checkpoint.read_text(encoding="utf-8"))
    weights = np.asarray(model["weights"], dtype=np.float32)
    bias = np.asarray(model["bias"], dtype=np.float32)
    args.bridge.mkdir(parents=True, exist_ok=True)
    request_path = args.bridge / "policy_request.json"
    response_path = args.bridge / "policy_response.json"
    handled: str | None = None
    print(f"Watching {request_path}", flush=True)
    while True:
        if request_path.exists():
            try:
                request = json.loads(request_path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                time.sleep(0.02)
                continue
            request_id = request.get("requestId")
            if request_id and request_id != handled:
                started = time.perf_counter()
                observation = str(model.get("observationMode", "current"))
                previous_path = Path(request["previousImagePath"]) if request.get("previousImagePath") else None
                features = request_features(
                    Path(request["imagePath"]),
                    previous_path,
                    int(request.get("previousAction", 0)),
                    int(model.get("featureSize", 16)),
                    observation,
                )
                probabilities = stable_softmax(features @ weights + bias)
                index = int(np.argmax(probabilities))
                response = {
                    "requestId": request_id,
                    "action": int(INDEX_TO_ACTION[index]),
                    "confidence": float(probabilities[index]),
                    "probabilities": probabilities.tolist(),
                    "recognitionProbability": 0.0,
                    "latencyMs": (time.perf_counter() - started) * 1000.0,
                    "model": model["model"],
                    "error": "",
                }
                atomic_write(response_path, response)
                handled = request_id
                if args.once:
                    return
        time.sleep(0.02)


if __name__ == "__main__":
    main()
