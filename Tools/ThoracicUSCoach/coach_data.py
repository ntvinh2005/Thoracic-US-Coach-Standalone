from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image


ACTION_TO_INDEX = {-1: 0, 0: 1, 1: 2}
INDEX_TO_ACTION = np.asarray([-1, 0, 1], dtype=np.int64)
DEFAULT_EXPERIMENT_ID = "thoracic_diverse_v2_20261020"
LEGACY_EXPERIMENT_ID = "legacy_fixed_path_smoke"


@dataclass(frozen=True)
class Frame:
    image: Path
    image_relative: str
    manifest: Path
    experiment_id: str
    sweep_id: str
    split: str
    configuration_family: str
    evaluation_group: str
    random_seed: int
    start_side: str
    path_direction: int
    step_index: int
    path_progress: float
    teacher_action: int
    teacher_action_defined: bool
    target_acceptable: bool
    contact_valid: bool
    score: float
    pleura_visibility: float
    shadow_penalty: float
    target_samples: int
    path_mm: float
    signed_depth_mm: float
    position: tuple[float, float, float]
    rotation: tuple[float, float, float, float]


def project_root() -> Path:
    return Path(__file__).resolve().parents[2]


def _xyz(value: dict[str, object]) -> tuple[float, float, float]:
    return float(value["x"]), float(value["y"]), float(value["z"])


def _xyzw(value: dict[str, object]) -> tuple[float, float, float, float]:
    return float(value["x"]), float(value["y"]), float(value["z"]), float(value["w"])


def load_frames(
    root: Path | None = None,
    experiment_id: str | None = DEFAULT_EXPERIMENT_ID,
    require_complete: bool = True,
) -> list[Frame]:
    root = (root or project_root()).resolve()
    dataset = root / "Artifacts" / "ThoracicUSCoach" / "dataset"
    frames: list[Frame] = []
    for manifest_path in sorted(dataset.glob("*/*/sweep_manifest.json")):
        if any(part.endswith(".partial") for part in manifest_path.parts):
            continue
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest_experiment = str(payload.get("experimentId") or LEGACY_EXPERIMENT_ID)
        if experiment_id is not None and manifest_experiment != experiment_id:
            continue
        if require_complete and payload.get("captureStatus", "complete") != "complete":
            continue
        trajectory = payload.get("trajectory") or {}
        split = str(payload.get("split") or "")
        path_length = float(trajectory.get("pathLengthMm", 0.0))
        for item in payload.get("frames", []):
            image_relative = str(item["image"])
            image = (root / image_relative).resolve()
            overlay = (root / str(item["anatomyOverlay"])).resolve()
            if require_complete and (not image.is_file() or not overlay.is_file()):
                raise FileNotFoundError(f"Incomplete manifest {manifest_path}: {image_relative}")
            pose = item["pose"]
            score = item["score"]
            progress = float(item.get("pathProgress", 0.0))
            frames.append(
                Frame(
                    image=image,
                    image_relative=image_relative.replace("\\", "/"),
                    manifest=manifest_path.resolve(),
                    experiment_id=manifest_experiment,
                    sweep_id=str(item.get("sweepId") or payload.get("sweepId")),
                    split=str(item.get("split") or split),
                    configuration_family=str(trajectory.get("configurationFamily") or "legacy-fixed-path"),
                    evaluation_group=str(trajectory.get("evaluationGroup") or "legacy-smoke"),
                    random_seed=int(trajectory.get("randomSeed", 0)),
                    start_side=str(trajectory.get("requestedStartSide") or "legacy"),
                    path_direction=int(trajectory.get("pathDirection", 1)),
                    step_index=int(item["stepIndex"]),
                    path_progress=progress,
                    teacher_action=int(item["teacherAction"]),
                    teacher_action_defined=bool(item.get("teacherActionDefined", True)),
                    target_acceptable=bool(item["targetAcceptable"]),
                    contact_valid=bool(item.get("contactValid", True)),
                    score=float(score["total"]),
                    pleura_visibility=float(score.get("pleuraVisibility", 0.0)),
                    shadow_penalty=float(score.get("shadowPenalty", 0.0)),
                    target_samples=int(score.get("targetSamples", 0)),
                    path_mm=progress * path_length if path_length > 0 else float(pose["caudalOffsetMm"]),
                    signed_depth_mm=float(pose["signedDepthMm"]),
                    position=_xyz(pose["position"]),
                    rotation=_xyzw(pose["rotation"]),
                )
            )
    return frames


def image_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def dataset_fingerprint(frames: Iterable[Frame]) -> str:
    digest = hashlib.sha256()
    for frame in sorted(frames, key=lambda item: (item.sweep_id, item.step_index)):
        digest.update(frame.experiment_id.encode())
        digest.update(frame.sweep_id.encode())
        digest.update(frame.configuration_family.encode())
        digest.update(str(frame.step_index).encode())
        digest.update(image_sha256(frame.image).encode())
    return digest.hexdigest()


def pixel_features(path: Path, size: int = 16) -> np.ndarray:
    """Only clean ultrasound pixels enter this feature extractor."""
    with Image.open(path) as image:
        gray = image.convert("L").resize((size, size), Image.Resampling.BILINEAR)
        values = np.asarray(gray, dtype=np.float32).reshape(-1) / 255.0
    mean = float(values.mean())
    std = float(values.std()) + 1e-6
    return ((values - mean) / std).astype(np.float32)


def feature_matrix(frames: Iterable[Frame], size: int = 16, observation: str = "current") -> np.ndarray:
    selected = list(frames)
    if observation == "current":
        rows = [pixel_features(frame.image, size=size) for frame in selected]
    elif observation == "history":
        by_key = {(frame.sweep_id, frame.step_index): frame for frame in selected}
        rows = []
        for frame in selected:
            previous = by_key.get((frame.sweep_id, frame.step_index - 1), frame)
            previous_action = previous.teacher_action if previous is not frame else 0
            action_one_hot = np.zeros(3, dtype=np.float32)
            action_one_hot[ACTION_TO_INDEX[previous_action]] = 1.0
            rows.append(np.concatenate((pixel_features(previous.image, size), pixel_features(frame.image, size), action_one_hot)))
    else:
        raise ValueError(f"Unknown observation mode: {observation}")
    width = size * size if observation == "current" else size * size * 2 + 3
    return np.stack(rows) if rows else np.empty((0, width), dtype=np.float32)


def request_features(image: Path, previous_image: Path | None, previous_action: int, size: int, observation: str) -> np.ndarray:
    current = pixel_features(image, size)
    if observation == "current":
        return current
    previous = pixel_features(previous_image if previous_image and previous_image.is_file() else image, size)
    one_hot = np.zeros(3, dtype=np.float32)
    one_hot[ACTION_TO_INDEX.get(int(previous_action), ACTION_TO_INDEX[0])] = 1.0
    return np.concatenate((previous, current, one_hot))


def stable_softmax(logits: np.ndarray) -> np.ndarray:
    shifted = logits - logits.max(axis=-1, keepdims=True)
    exp = np.exp(shifted)
    return exp / exp.sum(axis=-1, keepdims=True)


def classification_metrics(y_true: np.ndarray, probabilities: np.ndarray) -> dict[str, object]:
    predicted = probabilities.argmax(axis=1)
    accuracy = float((predicted == y_true).mean()) if len(y_true) else float("nan")
    confusion = np.zeros((probabilities.shape[1], probabilities.shape[1]), dtype=np.int64)
    for truth, guess in zip(y_true, predicted):
        confusion[int(truth), int(guess)] += 1
    per_action: dict[str, dict[str, float | int]] = {}
    for index, action in enumerate(INDEX_TO_ACTION):
        tp = int(confusion[index, index])
        fp = int(confusion[:, index].sum() - tp)
        fn = int(confusion[index, :].sum() - tp)
        precision = tp / max(1, tp + fp)
        recall = tp / max(1, tp + fn)
        per_action[str(int(action))] = {
            "support": int(confusion[index, :].sum()),
            "precision": precision,
            "recall": recall,
            "f1": 2 * precision * recall / max(1e-12, precision + recall),
        }
    return {"accuracy": accuracy, "count": int(len(y_true)), "confusion": confusion.tolist(), "perAction": per_action}


def save_json(path: Path, payload: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    temporary.replace(path)
