from __future__ import annotations

import json
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

import numpy as np

from coach_data import ACTION_TO_INDEX, DEFAULT_EXPERIMENT_ID, pixel_features, project_root
from contract_v3 import CONTRACT_VERSION, StepResult, transition


@dataclass(frozen=True)
class CachedFrame:
    index: int
    image: Path
    path_mm: float
    acceptable: bool
    contact_valid: bool
    workspace_valid: bool


@dataclass(frozen=True)
class CachedTrajectory:
    sweep_id: str
    split: str
    configuration_family: str
    evaluation_group: str
    random_seed: int
    path_direction: int
    default_start_index: int
    step_count: int
    frames: tuple[CachedFrame, ...]

    @property
    def acceptable(self) -> tuple[bool, ...]:
        return tuple(frame.acceptable for frame in self.frames)

    @property
    def path_mm(self) -> tuple[float, ...]:
        return tuple(frame.path_mm for frame in self.frames)


def _infer_step_count(payload: dict[str, object], action_step_mm: float) -> int:
    observed = []
    for item in payload.get("transitions", []):
        action = int(item.get("executedAction", 0))
        previous = int(item.get("previousIndex", 0))
        current = int(item.get("currentIndex", previous))
        if action and current != previous:
            observed.append(abs(current - previous))
    if observed:
        return max(observed)
    trajectory = payload.get("trajectory") or {}
    sample_count = int(trajectory.get("sampleCount", 0))
    path_length = abs(float(trajectory.get("pathLengthMm", 0.0)))
    spacing = path_length / (sample_count - 1) if sample_count > 1 else action_step_mm
    # Mathf.RoundToInt and Python round both use nearest-even at exact halves.
    return max(1, int(round(action_step_mm / max(0.01, spacing))))


def load_cached_trajectories(
    *,
    root: Path | None = None,
    experiment_id: str = DEFAULT_EXPERIMENT_ID,
    dataset_run_id: str,
    split: str = "train",
    action_step_mm: float = 1.5,
) -> list[CachedTrajectory]:
    root = (root or project_root()).resolve()
    dataset = root / "Artifacts" / "ThoracicUSCoach" / "dataset"
    trajectories: list[CachedTrajectory] = []
    for manifest_path in sorted(dataset.glob("*/*/sweep_manifest.json")):
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
        if payload.get("captureStatus", "complete") != "complete":
            continue
        if payload.get("experimentId") != experiment_id or payload.get("datasetRunId") != dataset_run_id:
            continue
        if payload.get("environmentContractVersion") != CONTRACT_VERSION:
            raise ValueError(f"Contract mismatch in {manifest_path}")
        trajectory = payload.get("trajectory") or {}
        manifest_split = str(payload.get("split") or trajectory.get("split") or "")
        if manifest_split != split:
            continue
        frames = []
        for item in sorted(payload.get("frames", []), key=lambda value: int(value["stepIndex"])):
            frames.append(
                CachedFrame(
                    index=int(item["stepIndex"]),
                    image=(root / str(item["image"])).resolve(),
                    path_mm=float(item["pathMm"]),
                    acceptable=bool(item["targetAcceptable"]),
                    contact_valid=bool(item.get("contactValid", True)),
                    workspace_valid=bool(item.get("workspaceValid", True)),
                )
            )
        if not frames or [item.index for item in frames] != list(range(len(frames))):
            raise ValueError(f"Cached trajectory indices are not contiguous: {manifest_path}")
        if any(not item.image.is_file() for item in frames):
            raise FileNotFoundError(f"Cached trajectory image is missing: {manifest_path}")
        trajectories.append(
            CachedTrajectory(
                sweep_id=str(payload["sweepId"]),
                split=manifest_split,
                configuration_family=str(trajectory.get("configurationFamily")),
                evaluation_group=str(trajectory.get("evaluationGroup")),
                random_seed=int(trajectory.get("randomSeed", 0)),
                path_direction=int(trajectory.get("pathDirection", 1)),
                default_start_index=int(trajectory.get("defaultStartIndex", len(frames) - 1)),
                step_count=_infer_step_count(payload, action_step_mm),
                frames=tuple(frames),
            )
        )
    if not trajectories:
        raise ValueError(f"No {split} cached trajectories for dataset run {dataset_run_id}")
    families = [item.configuration_family for item in trajectories]
    if len(families) != len(set(families)):
        raise ValueError("Cached training trajectories must have unique configuration families")
    return trajectories


def start_indices(trajectory: CachedTrajectory) -> dict[str, int]:
    count = len(trajectory.frames)
    acceptable = [index for index, value in enumerate(trajectory.acceptable) if value]
    starts = {"endpoint-low": 0, "endpoint-high": count - 1}
    if acceptable:
        exact = set(acceptable)
        neighbors = [candidate for target in acceptable for candidate in (target - 1, target + 1) if 0 <= candidate < count and candidate not in exact]
        starts["inside"] = acceptable[len(acceptable) // 2]
        if neighbors:
            starts["immediately-outside"] = neighbors[0]
        starts["far"] = max(range(count), key=lambda index: min(abs(index - target) for target in acceptable))
    return starts


class CachedEnvironment:
    """Replay captured clean images while applying the exact v3 1-D transition contract."""

    def __init__(self, trajectory: CachedTrajectory, start_index: int, max_steps: int):
        self.trajectory = trajectory
        self.index = min(len(trajectory.frames) - 1, max(0, int(start_index)))
        self.max_steps = max(1, int(max_steps))
        self.steps_taken = 0

    @property
    def frame(self) -> CachedFrame:
        return self.trajectory.frames[self.index]

    def step(self, action: int) -> StepResult:
        next_index = min(
            len(self.trajectory.frames) - 1,
            max(0, self.index + int(action) * self.trajectory.step_count),
        )
        destination = self.trajectory.frames[next_index]
        result = transition(
            index=self.index,
            action=int(action),
            step_count=self.trajectory.step_count,
            acceptable=self.trajectory.acceptable,
            path_mm=self.trajectory.path_mm,
            contact_valid=destination.contact_valid,
            workspace_valid=destination.workspace_valid,
            steps_taken=self.steps_taken,
            max_steps=self.max_steps,
        )
        self.index = result.index
        self.steps_taken += 1
        return result


@lru_cache(maxsize=16_384)
def cached_pixels(path: str, size: int) -> np.ndarray:
    values = pixel_features(Path(path), size)
    values.setflags(write=False)
    return values


def cached_features(
    current: Path,
    previous: Path | None,
    previous_action: int,
    *,
    size: int,
    observation: str,
) -> np.ndarray:
    current_values = cached_pixels(str(current), size)
    if observation == "current":
        return current_values.copy()
    if observation != "history":
        raise ValueError(f"Unknown observation mode: {observation}")
    previous_values = cached_pixels(str(previous or current), size)
    one_hot = np.zeros(3, dtype=np.float32)
    one_hot[ACTION_TO_INDEX.get(int(previous_action), ACTION_TO_INDEX[0])] = 1.0
    return np.concatenate((previous_values, current_values, one_hot))
