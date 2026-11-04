"""Persistent image-only scorer for the bounded Phase-2 search controller."""

from __future__ import annotations

import argparse
import json
import os
import time
from pathlib import Path

import joblib
import numpy as np

from phase2_best_view_recognition import ACTOR_INPUTS, FORBIDDEN_INPUTS, extract_feature


CONTRACT = "thoracic_phase2_best_view_score_v1"


def atomic_write(path: Path, payload: dict) -> None:
    temporary = path.with_name(f"{path.stem}_{time.time_ns()}.tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def validate_actor_model(model: dict) -> None:
    if tuple(model.get("actorInputs", ())) != ACTOR_INPUTS:
        raise ValueError("Phase-2 actor model does not declare the pixel-only input contract")
    if tuple(model.get("forbiddenInputs", ())) != FORBIDDEN_INPUTS:
        raise ValueError("Phase-2 actor model does not preserve the forbidden-input contract")
    if model.get("featureMode") not in {"global", "topology", "combined"}:
        raise ValueError("Phase-2 actor model has an unsupported feature mode")
    if not 0.0 <= float(model.get("threshold", -1.0)) <= 1.0:
        raise ValueError("Phase-2 actor threshold is invalid")


def score_image(model: dict, image_path: Path) -> dict:
    features = extract_feature(image_path, model["featureMode"]).reshape(1, -1)
    probability = float(model["classifier"].predict_proba(features)[0, 1])
    quality = float(np.clip(model["qualityRegressor"].predict(features)[0], 0.0, 1.0))
    return {
        "validityProbability": probability,
        "qualityScore": quality,
        "accepted": probability >= float(model["threshold"]),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Watch Phase-2 image-only scoring requests")
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--bridge", type=Path, required=True)
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    model = joblib.load(args.model)
    validate_actor_model(model)
    args.bridge.mkdir(parents=True, exist_ok=True)
    request_path = args.bridge / "phase2_best_view_request.json"
    response_path = args.bridge / "phase2_best_view_response.json"
    handled = ""
    print(f"Watching Phase-2 pixel-only requests at {request_path}", flush=True)
    while True:
        if request_path.is_file():
            try:
                request = json.loads(request_path.read_text(encoding="utf-8"))
                if request.get("contract") != CONTRACT:
                    raise ValueError("request contract mismatch")
                request_id = str(request["requestId"])
                image_path = Path(request["imagePath"])
                if request_id and request_id != handled:
                    started = time.perf_counter()
                    result = score_image(model, image_path)
                    atomic_write(response_path, {
                        "contract": CONTRACT,
                        "requestId": request_id,
                        "modelSchema": model["schema"],
                        "featureMode": model["featureMode"],
                        "threshold": float(model["threshold"]),
                        "latencyMs": (time.perf_counter() - started) * 1000.0,
                        "error": "",
                        **result,
                    })
                    handled = request_id
                    if args.once:
                        return
            except (OSError, KeyError, ValueError, json.JSONDecodeError) as error:
                request_id = locals().get("request_id", "")
                if request_id and request_id != handled:
                    atomic_write(response_path, {
                        "contract": CONTRACT,
                        "requestId": request_id,
                        "error": f"{type(error).__name__}: {error}",
                    })
                    handled = request_id
                    if args.once:
                        return
        time.sleep(0.02)


if __name__ == "__main__":
    main()
