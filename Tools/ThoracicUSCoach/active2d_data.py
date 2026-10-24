from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image

from active2d_contract import CONTRACT, EXPERIMENT_ID, LEVEL_ORDER, canonical_sha256, project_root


@dataclass(frozen=True)
class Active2DFrame:
    image: Path
    image_relative: str
    image_sha256: str
    episode_id: str
    dataset_run_id: str
    split: str
    family: str
    seed: int
    index: int
    issued_action: str
    action_accepted: bool
    action_completed: bool
    episode_reset: bool
    level: str
    t7_distance_mm: float
    t7_acceptable: bool
    target_acceptable: bool
    target_score: float
    contact_valid: bool
    workspace_valid: bool


def load_active2d_frames(
    dataset_run_id: str,
    root: Path | None = None,
    *,
    splits: set[str] | None = None,
    dataset_relative: Path | str = Path("Artifacts/ThoracicUSCoach/active2d_dataset"),
) -> list[Active2DFrame]:
    root = (root or project_root()).resolve()
    dataset = root / dataset_relative
    frames: list[Active2DFrame] = []
    for stream_path in sorted(dataset.glob("*/*/stream_manifest.json")):
        if any(part.endswith(".partial") for part in stream_path.parts):
            continue
        stream = json.loads(stream_path.read_text(encoding="utf-8"))
        if stream.get("contract") != CONTRACT or stream.get("experimentId") != EXPERIMENT_ID:
            continue
        if stream.get("datasetRunId") != dataset_run_id or stream.get("captureStatus") != "complete":
            continue
        if splits is not None and str(stream.get("split")) not in splits:
            # Filter at manifest level before privileged annotations or image
            # bytes are read. This keeps locked-test observations out of model
            # selection rather than merely excluding them after loading.
            continue
        privileged_path = stream_path.with_name("privileged_annotations.json")
        if not privileged_path.is_file():
            raise FileNotFoundError(f"Missing privileged annotations for {stream_path}")
        privileged = json.loads(privileged_path.read_text(encoding="utf-8"))
        if privileged.get("contract") != CONTRACT or privileged.get("episodeId") != stream.get("episodeId"):
            raise ValueError(f"Annotation identity mismatch: {privileged_path}")
        labels = {int(item["index"]): item for item in privileged.get("frames", [])}
        if len(labels) != len(stream.get("frames", [])):
            raise ValueError(f"Stream/annotation count mismatch: {stream_path}")
        for item in stream.get("frames", []):
            index = int(item["index"])
            annotation = labels[index]
            image_relative = str(item["image"]).replace("\\", "/")
            image = (root / image_relative).resolve()
            if not image.is_file():
                raise FileNotFoundError(image)
            actual_hash = hashlib.sha256(image.read_bytes()).hexdigest()
            if actual_hash != item.get("imageSha256"):
                raise ValueError(f"Image hash mismatch: {image}")
            level = str(annotation.get("nearestLevel"))
            if level not in LEVEL_ORDER:
                raise ValueError(f"Unsupported level label {level}: {privileged_path}")
            frames.append(
                Active2DFrame(
                    image=image,
                    image_relative=image_relative,
                    image_sha256=actual_hash,
                    episode_id=str(stream["episodeId"]),
                    dataset_run_id=dataset_run_id,
                    split=str(stream["split"]),
                    family=str(stream["family"]),
                    seed=int(stream["seed"]),
                    index=index,
                    issued_action=str(item.get("issuedAction") or "RESET").upper(),
                    action_accepted=bool(item.get("actionAccepted", True)),
                    action_completed=bool(item.get("actionCompleted", item.get("actionAccepted", True))),
                    episode_reset=bool(item.get("episodeReset", False)),
                    level=level,
                    t7_distance_mm=float(annotation.get("t7DistanceMm", float("inf"))),
                    t7_acceptable=bool(annotation.get("t7LevelAcceptable", False)),
                    target_acceptable=bool(annotation.get("finalTargetAcceptable", False)),
                    target_score=float(annotation.get("finalTargetScore", 0.0)),
                    contact_valid=bool(annotation.get("contactValid", False)),
                    workspace_valid=bool(annotation.get("workspaceValid", False)),
                )
            )
    return frames


FILTERS = np.asarray(
    [
        [[1, 0, -1], [2, 0, -2], [1, 0, -1]],
        [[1, 2, 1], [0, 0, 0], [-1, -2, -1]],
        [[0, 1, 0], [1, -4, 1], [0, 1, 0]],
        [[2, -1, -1], [-1, 2, -1], [-1, -1, 2]],
        [[-1, -1, 2], [-1, 2, -1], [2, -1, -1]],
        [[1, 1, 1], [1, -8, 1], [1, 1, 1]],
    ],
    dtype=np.float32,
)
FILTERS /= np.maximum(1.0, np.linalg.norm(FILTERS.reshape(len(FILTERS), -1), axis=1))[:, None, None]


def _pool4(values: np.ndarray) -> np.ndarray:
    height, width = values.shape
    rows = np.array_split(np.arange(height), 4)
    cols = np.array_split(np.arange(width), 4)
    return np.asarray([values[np.ix_(row, col)].mean() for row in rows for col in cols], dtype=np.float32)


def convolutional_features(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        pixels = np.asarray(image.convert("L").resize((32, 32), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
    pixels = (pixels - pixels.mean()) / max(float(pixels.std()), 1e-6)
    windows = np.lib.stride_tricks.sliding_window_view(pixels, (3, 3))
    parts: list[np.ndarray] = []
    for kernel in FILTERS:
        response = np.einsum("ijxy,xy->ij", windows, kernel, optimize=True)
        parts.extend((_pool4(response), _pool4(np.abs(response))))
    raw = np.asarray(Image.fromarray(pixels).resize((8, 8), Image.Resampling.BILINEAR), dtype=np.float32).reshape(-1)
    vector = np.concatenate([raw, *parts]).astype(np.float32)
    return (vector - vector.mean()) / max(float(vector.std()), 1e-6)


def feature_matrix(frames: Iterable[Active2DFrame]) -> np.ndarray:
    selected = list(frames)
    return np.stack([convolutional_features(frame.image) for frame in selected]) if selected else np.empty((0, 256), dtype=np.float32)


def dataset_fingerprint(frames: Iterable[Active2DFrame]) -> str:
    identity = [
        {
            "episodeId": frame.episode_id,
            "family": frame.family,
            "index": frame.index,
            "imageSha256": frame.image_sha256,
            "issuedAction": frame.issued_action,
            "level": frame.level,
        }
        for frame in sorted(frames, key=lambda value: (value.episode_id, value.index))
    ]
    return canonical_sha256(identity)


def softmax(logits: np.ndarray) -> np.ndarray:
    shifted = logits - np.max(logits, axis=-1, keepdims=True)
    values = np.exp(shifted)
    return values / np.sum(values, axis=-1, keepdims=True)


def model_probabilities(model: dict, features: np.ndarray) -> np.ndarray:
    architecture = model["architecture"]
    hidden = np.maximum(features @ np.asarray(architecture["inputWeights"], dtype=np.float32)
                        + np.asarray(architecture["inputBias"], dtype=np.float32), 0.0)
    return softmax(hidden @ np.asarray(architecture["outputWeights"], dtype=np.float32)
                   + np.asarray(architecture["outputBias"], dtype=np.float32))
