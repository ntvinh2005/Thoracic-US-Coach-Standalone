from __future__ import annotations

from collections import defaultdict, deque
from pathlib import Path
from typing import Iterable, Sequence

import numpy as np
from PIL import Image

from active2d_contract import ACTION_ORDER
from active2d_data import Active2DFrame


STRUCTURAL_FEATURE_WIDTH = 1348


def _action_vector(action: str) -> np.ndarray:
    result = np.zeros(len(ACTION_ORDER), dtype=np.float32)
    normalized = str(action).replace("_", "").upper()
    if normalized in ACTION_ORDER:
        result[ACTION_ORDER.index(normalized)] = 1.0
    return result


def _pool(values: np.ndarray, cells: int) -> np.ndarray:
    rows = np.array_split(np.arange(values.shape[0]), cells)
    columns = np.array_split(np.arange(values.shape[1]), cells)
    return np.asarray(
        [values[np.ix_(row, column)].mean() for row in rows for column in columns],
        dtype=np.float32,
    )


def normalized_grayscale(path: Path, size: int) -> np.ndarray:
    with Image.open(path) as image:
        values = np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR),
            dtype=np.float32,
        ) / 255.0
    return values


def structural_features_v4(path: Path) -> np.ndarray:
    """Compact multi-scale intensity/edge representation from clean pixels only.

    The 32x32 intensity grid retains four times the spatial samples of the old
    16x16 representation.  Gradient pools are computed at 64x64 so thin bright
    contours are represented without introducing any annotation or pose input.
    """
    raw32 = normalized_grayscale(path, 32)
    raw64 = normalized_grayscale(path, 64)
    standardized = (raw64 - raw64.mean()) / max(float(raw64.std()), 1e-6)
    gy, gx = np.gradient(standardized)
    magnitude = np.sqrt(gx * gx + gy * gy)
    vector = np.concatenate(
        (
            raw32.reshape(-1),
            _pool(gx, 8),
            _pool(gy, 8),
            _pool(magnitude, 8),
            _pool(raw64, 8),
            _pool(raw64 * raw64, 8),
            np.asarray(
                [float(raw64.mean()), float(raw64.std()), float(magnitude.mean()), float(magnitude.std())],
                dtype=np.float32,
            ),
        )
    ).astype(np.float32)
    if len(vector) != STRUCTURAL_FEATURE_WIDTH:
        raise RuntimeError(f"structural-v4 width mismatch: {len(vector)}")
    return vector


def compact_delta(current: Path, previous: Path | None) -> np.ndarray:
    current16 = normalized_grayscale(current, 16)
    previous16 = normalized_grayscale(previous, 16) if previous is not None else current16
    delta = current16 - previous16
    return np.concatenate((delta.reshape(-1), np.abs(delta).reshape(-1))).astype(np.float32)


def temporal_structural_features_v5(
    image_path: Path,
    history_images: Sequence[Path],
    action_history: Sequence[str],
    accepted_history: Sequence[bool],
    completed_history: Sequence[bool],
    *,
    history_length: int,
) -> np.ndarray:
    if history_length not in {1, 4}:
        raise ValueError("temporal-structural-v5 supports history length 1 or 4")
    parts: list[np.ndarray] = [structural_features_v4(image_path)]
    available = min(len(history_images), history_length)
    for lag in range(1, history_length + 1):
        previous = history_images[-lag] if lag <= available else None
        parts.append(compact_delta(image_path, previous))
        if lag <= len(action_history):
            accepted = bool(accepted_history[-lag]) if lag <= len(accepted_history) else False
            completed = bool(completed_history[-lag]) if lag <= len(completed_history) else False
            action = action_history[-lag] if accepted and completed else "RESET"
        else:
            accepted = completed = False
            action = "RESET"
        parts.append(_action_vector(action))
        parts.append(np.asarray([float(previous is not None), float(accepted), float(completed)], dtype=np.float32))
    return np.concatenate(parts).astype(np.float32)


def feature_width(feature_extractor: str) -> int:
    if feature_extractor == "structural-v4":
        return STRUCTURAL_FEATURE_WIDTH
    if feature_extractor == "temporal-structural-v5-h1":
        return STRUCTURAL_FEATURE_WIDTH + 512 + len(ACTION_ORDER) + 3
    if feature_extractor == "temporal-structural-v5-h4":
        return STRUCTURAL_FEATURE_WIDTH + 4 * (512 + len(ACTION_ORDER) + 3)
    raise ValueError(f"Unsupported G2-R2 feature extractor: {feature_extractor}")


def request_features(
    image_path: Path,
    feature_extractor: str,
    *,
    history_images: Sequence[Path] = (),
    action_history: Sequence[str] = (),
    accepted_history: Sequence[bool] = (),
    completed_history: Sequence[bool] = (),
) -> np.ndarray:
    if feature_extractor == "structural-v4":
        return structural_features_v4(image_path)
    history_length = 1 if feature_extractor.endswith("-h1") else 4
    return temporal_structural_features_v5(
        image_path,
        history_images,
        action_history,
        accepted_history,
        completed_history,
        history_length=history_length,
    )


def frame_feature_matrix(frames: Iterable[Active2DFrame], feature_extractor: str) -> np.ndarray:
    selected = list(frames)
    rows: list[np.ndarray] = []
    images: dict[str, deque[Path]] = defaultdict(lambda: deque(maxlen=4))
    actions: dict[str, deque[str]] = defaultdict(lambda: deque(maxlen=4))
    accepted: dict[str, deque[bool]] = defaultdict(lambda: deque(maxlen=4))
    completed: dict[str, deque[bool]] = defaultdict(lambda: deque(maxlen=4))
    for frame in selected:
        if frame.episode_reset:
            images[frame.episode_id].clear()
            actions[frame.episode_id].clear()
            accepted[frame.episode_id].clear()
            completed[frame.episode_id].clear()
        current_actions = [*actions[frame.episode_id], frame.issued_action]
        current_accepted = [*accepted[frame.episode_id], frame.action_accepted]
        current_completed = [*completed[frame.episode_id], frame.action_completed]
        rows.append(
            request_features(
                frame.image,
                feature_extractor,
                history_images=list(images[frame.episode_id]),
                action_history=current_actions,
                accepted_history=current_accepted,
                completed_history=current_completed,
            )
        )
        images[frame.episode_id].append(frame.image)
        actions[frame.episode_id].append(frame.issued_action)
        accepted[frame.episode_id].append(frame.action_accepted)
        completed[frame.episode_id].append(frame.action_completed)
    width = feature_width(feature_extractor)
    return np.stack(rows) if rows else np.empty((0, width), dtype=np.float32)
