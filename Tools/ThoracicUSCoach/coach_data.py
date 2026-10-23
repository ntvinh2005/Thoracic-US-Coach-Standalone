from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image

from contract_v3 import CONTRACT_VERSION, EXPERIMENT_ID, OBSERVATION_SCHEMA, TARGET_SEMANTICS, canonical_json_sha256


ACTION_TO_INDEX = {-1: 0, 0: 1, 1: 2}
INDEX_TO_ACTION = np.asarray([-1, 0, 1], dtype=np.int64)
DEFAULT_EXPERIMENT_ID = EXPERIMENT_ID
DEFAULT_DATASET_RUN_ID = "main_cpu_v3_20261022"
LEGACY_DATASET_RUN_ID = "legacy_unversioned_run"
HISTORICAL_V2_EXPERIMENT_ID = "thoracic_diverse_v2_20261020"
LEGACY_EXPERIMENT_ID = "legacy_fixed_path_smoke"


@dataclass(frozen=True)
class Frame:
    image: Path
    image_relative: str
    manifest: Path
    experiment_id: str
    dataset_run_id: str
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
    workspace_valid: bool
    score: float
    pleura_visibility: float
    shadow_penalty: float
    target_samples: int
    path_mm: float
    signed_depth_mm: float
    position: tuple[float, float, float]
    rotation: tuple[float, float, float, float]


@dataclass(frozen=True)
class TransitionSample:
    manifest: Path
    experiment_id: str
    dataset_run_id: str
    sweep_id: str
    split: str
    configuration_family: str
    evaluation_group: str
    transition_id: str
    transition_kind: str
    previous_image: Path
    previous_image_relative: str
    previous_image_sha256: str
    previous_index: int
    executed_action: int
    action_was_executed: bool
    current_image: Path
    current_image_relative: str
    current_image_sha256: str
    current_index: int
    teacher_action: int
    teacher_action_defined: bool
    target_acceptable: bool
    contact_valid: bool
    workspace_valid: bool
    boundary_clamped: bool
    terminated: bool
    truncated: bool
    configuration_sha256: str
    labels_sha256: str


def project_root() -> Path:
    return Path(__file__).resolve().parents[2]


def _xyz(value: dict[str, object]) -> tuple[float, float, float]:
    return float(value["x"]), float(value["y"]), float(value["z"])


def _xyzw(value: dict[str, object]) -> tuple[float, float, float, float]:
    return float(value["x"]), float(value["y"]), float(value["z"]), float(value["w"])


def load_frames(
    root: Path | None = None,
    experiment_id: str | None = DEFAULT_EXPERIMENT_ID,
    dataset_run_id: str | None = DEFAULT_DATASET_RUN_ID,
    require_complete: bool = True,
    splits: set[str] | None = None,
) -> list[Frame]:
    root = (root or project_root()).resolve()
    dataset = root / "Artifacts" / "ThoracicUSCoach" / "dataset"
    frames: list[Frame] = []
    for manifest_path in sorted(dataset.glob("*/*/sweep_manifest.json")):
        if any(part.endswith(".partial") for part in manifest_path.parts):
            continue
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest_experiment = str(payload.get("experimentId") or LEGACY_EXPERIMENT_ID)
        manifest_run = str(payload.get("datasetRunId") or LEGACY_DATASET_RUN_ID)
        if experiment_id is not None and manifest_experiment != experiment_id:
            continue
        if dataset_run_id is not None and manifest_run != dataset_run_id:
            continue
        if require_complete and payload.get("captureStatus", "complete") != "complete":
            continue
        trajectory = payload.get("trajectory") or {}
        split = str(payload.get("split") or "")
        if splits is not None and split not in splits:
            continue
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
                    dataset_run_id=manifest_run,
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
                    workspace_valid=bool(item.get("workspaceValid", True)),
                    score=float(score["total"]),
                    pleura_visibility=float(score.get("pleuraVisibility", 0.0)),
                    shadow_penalty=float(score.get("shadowPenalty", 0.0)),
                    target_samples=int(score.get("targetSamples", 0)),
                    path_mm=float(item.get("pathMm", progress * path_length if path_length > 0 else pose["caudalOffsetMm"])),
                    signed_depth_mm=float(pose["signedDepthMm"]),
                    position=_xyz(pose["position"]),
                    rotation=_xyzw(pose["rotation"]),
                )
            )
    return frames


def load_transitions(
    root: Path | None = None,
    experiment_id: str = DEFAULT_EXPERIMENT_ID,
    dataset_run_id: str | None = DEFAULT_DATASET_RUN_ID,
    *,
    verify_hashes: bool = True,
    splits: set[str] | None = None,
) -> list[TransitionSample]:
    root = (root or project_root()).resolve()
    dataset = root / "Artifacts" / "ThoracicUSCoach" / "dataset"
    samples: list[TransitionSample] = []
    for manifest_path in sorted(dataset.glob("*/*/sweep_manifest.json")):
        if any(part.endswith(".partial") for part in manifest_path.parts):
            continue
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
        if str(payload.get("experimentId")) != experiment_id:
            continue
        manifest_run = str(payload.get("datasetRunId") or LEGACY_DATASET_RUN_ID)
        if dataset_run_id is not None and manifest_run != dataset_run_id:
            continue
        if int(payload.get("schemaVersion", 0)) < 3:
            raise ValueError(f"History training requires schema v3 transitions: {manifest_path}")
        if payload.get("environmentContractVersion") != CONTRACT_VERSION:
            raise ValueError(f"Contract mismatch in {manifest_path}")
        if payload.get("observationSchema") != OBSERVATION_SCHEMA or payload.get("targetSemantics") != TARGET_SEMANTICS:
            raise ValueError(f"Observation/target semantics mismatch in {manifest_path}")
        trajectory = payload.get("trajectory") or {}
        split = str(payload.get("split") or trajectory.get("split") or "")
        if splits is not None and split not in splits:
            continue
        for item in payload.get("transitions", []):
            previous_relative = str(item["previousImage"]).replace("\\", "/")
            current_relative = str(item["currentImage"]).replace("\\", "/")
            previous = (root / previous_relative).resolve()
            current = (root / current_relative).resolve()
            if not previous.is_file() or not current.is_file():
                raise FileNotFoundError(f"Incomplete transition {manifest_path}: {item.get('transitionId')}")
            previous_hash = str(item.get("previousImageSha256") or "")
            current_hash = str(item.get("currentImageSha256") or "")
            if verify_hashes and (image_sha256(previous) != previous_hash or image_sha256(current) != current_hash):
                raise ValueError(f"Transition image hash mismatch: {manifest_path} {item.get('transitionId')}")
            samples.append(
                TransitionSample(
                    manifest=manifest_path.resolve(),
                    experiment_id=experiment_id,
                    dataset_run_id=manifest_run,
                    sweep_id=str(payload.get("sweepId")),
                    split=split,
                    configuration_family=str(trajectory.get("configurationFamily")),
                    evaluation_group=str(trajectory.get("evaluationGroup")),
                    transition_id=str(item.get("transitionId")),
                    transition_kind=str(item.get("transitionKind")),
                    previous_image=previous,
                    previous_image_relative=previous_relative,
                    previous_image_sha256=previous_hash,
                    previous_index=int(item.get("previousIndex", 0)),
                    executed_action=int(item.get("executedAction", 0)),
                    action_was_executed=bool(item.get("actionWasExecuted", item.get("transitionKind") != "reset")),
                    current_image=current,
                    current_image_relative=current_relative,
                    current_image_sha256=current_hash,
                    current_index=int(item.get("currentIndex", 0)),
                    teacher_action=int(item.get("teacherAction", 0)),
                    teacher_action_defined=bool(item.get("teacherActionDefined", True)),
                    target_acceptable=bool(item.get("targetAcceptable", False)),
                    contact_valid=bool(item.get("contactValid", False)),
                    workspace_valid=bool(item.get("workspaceValid", False)),
                    boundary_clamped=bool(item.get("boundaryClamped", False)),
                    terminated=bool(item.get("terminated", False)),
                    truncated=bool(item.get("truncated", False)),
                    configuration_sha256=str(payload.get("configurationSha256")),
                    labels_sha256=str(payload.get("labelsSha256")),
                )
            )
    return samples


def image_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def dataset_fingerprint(frames: Iterable[Frame]) -> str:
    digest = hashlib.sha256()
    for frame in sorted(frames, key=lambda item: (item.sweep_id, item.step_index)):
        digest.update(frame.experiment_id.encode())
        digest.update(frame.dataset_run_id.encode())
        digest.update(frame.sweep_id.encode())
        digest.update(frame.configuration_family.encode())
        digest.update(str(frame.step_index).encode())
        digest.update(image_sha256(frame.image).encode())
    return digest.hexdigest()


def transition_dataset_fingerprint(samples: Iterable[TransitionSample]) -> str:
    identity = [
        {
            "experimentId": sample.experiment_id,
            "datasetRunId": sample.dataset_run_id,
            "sweepId": sample.sweep_id,
            "transitionId": sample.transition_id,
            "configurationSha256": sample.configuration_sha256,
            "labelsSha256": sample.labels_sha256,
            "previousImageSha256": sample.previous_image_sha256,
            "executedAction": sample.executed_action,
            "actionWasExecuted": sample.action_was_executed,
            "currentImageSha256": sample.current_image_sha256,
            "teacherAction": sample.teacher_action,
            "terminated": sample.terminated,
            "truncated": sample.truncated,
        }
        for sample in sorted(samples, key=lambda item: (item.sweep_id, item.transition_id))
    ]
    return canonical_json_sha256(identity)


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
        raise ValueError("History features require genuine schema-v3 transitions; use transition_feature_matrix")
    else:
        raise ValueError(f"Unknown observation mode: {observation}")
    width = size * size if observation == "current" else size * size * 2 + 3
    return np.stack(rows) if rows else np.empty((0, width), dtype=np.float32)


def transition_feature_matrix(samples: Iterable[TransitionSample], size: int = 16) -> np.ndarray:
    rows = []
    for sample in samples:
        action_one_hot = np.zeros(3, dtype=np.float32)
        action_one_hot[ACTION_TO_INDEX[sample.executed_action]] = 1.0
        rows.append(
            np.concatenate(
                (pixel_features(sample.previous_image, size), pixel_features(sample.current_image, size), action_one_hot)
            )
        )
    return np.stack(rows) if rows else np.empty((0, size * size * 2 + 3), dtype=np.float32)


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


def model_probabilities(
    model: dict[str, object],
    features: np.ndarray,
    weights_override: np.ndarray | None = None,
    bias_override: np.ndarray | None = None,
) -> np.ndarray:
    """Checkpoint-compatible inference for the declared compact CPU models."""
    values = np.asarray(features, dtype=np.float32)
    architecture = str(model.get("architecture") or "linear-softmax-v1")
    if architecture == "linear-softmax-v1":
        weights = weights_override if weights_override is not None else np.asarray(model["weights"], dtype=np.float32)
        bias = bias_override if bias_override is not None else np.asarray(model["bias"], dtype=np.float32)
        return stable_softmax(values @ weights + bias)
    if architecture == "mlp-relu-v1":
        hidden = np.maximum(
            values @ np.asarray(model["inputWeights"], dtype=np.float32)
            + np.asarray(model["inputBias"], dtype=np.float32),
            0.0,
        )
        logits = hidden @ np.asarray(model["outputWeights"], dtype=np.float32) + np.asarray(
            model["outputBias"], dtype=np.float32
        )
        return stable_softmax(logits)
    raise ValueError(f"Unsupported checkpoint architecture: {architecture}")


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
