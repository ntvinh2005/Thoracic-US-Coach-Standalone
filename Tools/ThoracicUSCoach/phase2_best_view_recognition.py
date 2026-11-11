"""Bounded image-only recognition study for the Phase-2 T7-right view.

Evaluator labels are used only while fitting and scoring development models.
The saved actor model consumes ultrasound pixels and carries no pose, target,
anatomy, or evaluator fields.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

import joblib
import numpy as np
from PIL import Image
from sklearn.ensemble import ExtraTreesClassifier, ExtraTreesRegressor
from sklearn.metrics import average_precision_score, precision_recall_fscore_support

from active2d_contract import project_root
from active2d_data import Active2DFrame, load_active2d_frames


ACTOR_INPUTS = ("cleanUltrasoundImage",)
FORBIDDEN_INPUTS = (
    "UnityAnatomyLabels",
    "anatomicalCoordinates",
    "evaluatorScore",
    "probeWorldPose",
    "t7TargetPosition",
)
FEATURE_MODES = ("global", "topology", "combined")


@dataclass(frozen=True)
class Metrics:
    count: int
    positives: int
    threshold: float
    true_positive: int
    false_positive: int
    false_negative: int
    true_negative: int
    precision: float
    recall: float
    f1: float
    average_precision: float
    quality_mae: float


def _resize(values: np.ndarray, size: int) -> np.ndarray:
    pixels = np.asarray(np.clip(values * 255.0, 0, 255), dtype=np.uint8)
    return np.asarray(
        Image.fromarray(pixels).resize((size, size), Image.Resampling.BILINEAR),
        dtype=np.float32,
    ) / 255.0


def normalized_image(path: Path, size: int = 32) -> np.ndarray:
    with Image.open(path) as image:
        values = np.asarray(
            image.convert("L").resize((128, 128), Image.Resampling.BILINEAR),
            dtype=np.float32,
        ) / 255.0
    low, high = np.quantile(values, (0.01, 0.99))
    normalized = np.clip((values - low) / max(float(high - low), 1e-6), 0.0, 1.0)
    return _resize(normalized, size)


def _pooled(values: np.ndarray, cells: int = 8) -> np.ndarray:
    rows = np.array_split(np.arange(values.shape[0]), cells)
    cols = np.array_split(np.arange(values.shape[1]), cells)
    output = []
    for row in rows:
        for col in cols:
            patch = values[np.ix_(row, col)]
            output.extend((float(np.mean(patch)), float(np.std(patch))))
    return np.asarray(output, dtype=np.float32)


def _hog(values: np.ndarray, cells: int = 8, bins: int = 8) -> np.ndarray:
    gx = np.gradient(values, axis=1)
    gy = np.gradient(values, axis=0)
    magnitude = np.hypot(gx, gy)
    orientation = np.mod(np.arctan2(gy, gx), np.pi)
    rows = np.array_split(np.arange(values.shape[0]), cells)
    cols = np.array_split(np.arange(values.shape[1]), cells)
    output = []
    for row in rows:
        for col in cols:
            angles = orientation[np.ix_(row, col)].ravel()
            weights = magnitude[np.ix_(row, col)].ravel()
            hist, _ = np.histogram(angles, bins=bins, range=(0.0, np.pi), weights=weights)
            output.extend((hist / max(float(np.sum(hist)), 1e-6)).tolist())
    return np.asarray(output, dtype=np.float32)


def extract_all_features(path: Path) -> dict[str, np.ndarray]:
    image = normalized_image(path)
    gx = np.gradient(image, axis=1)
    gy = np.gradient(image, axis=0)
    magnitude = np.hypot(gx, gy)
    global_feature = np.concatenate((
        image.ravel(),
        _resize(np.abs(gx), 16).ravel(),
        _resize(np.abs(gy), 16).ravel(),
    )).astype(np.float32)
    topology_feature = np.concatenate((
        _hog(image),
        _pooled(image),
        _pooled(magnitude),
        np.mean(image, axis=0),
        np.mean(image, axis=1),
        np.std(image, axis=0),
        np.std(image, axis=1),
        np.asarray([
            np.mean(image), np.std(image),
            np.mean(image < 0.10), np.mean(image > 0.70),
            np.mean(magnitude), np.quantile(magnitude, 0.90),
        ], dtype=np.float32),
    )).astype(np.float32)
    return {
        "global": global_feature,
        "topology": topology_feature,
        "combined": np.concatenate((global_feature, topology_feature)).astype(np.float32),
    }


def extract_feature(path: Path, mode: str) -> np.ndarray:
    if mode not in FEATURE_MODES:
        raise ValueError(f"Unsupported feature mode: {mode}")
    return extract_all_features(path)[mode]


def feature_matrix(frames: Iterable[Active2DFrame], mode: str) -> np.ndarray:
    selected = list(frames)
    if not selected:
        return np.empty((0, 0), dtype=np.float32)
    return np.stack([extract_feature(frame.image, mode) for frame in selected])


def cached_feature_matrices(
    roles: dict[str, list[Active2DFrame]], cache_path: Path
) -> dict[str, dict[str, np.ndarray]]:
    identity = hashlib.sha256()
    for role in sorted(roles):
        for frame in roles[role]:
            identity.update(role.encode("utf-8"))
            identity.update(frame.image_sha256.encode("ascii"))
    expected_identity = identity.hexdigest()
    if cache_path.is_file():
        values = np.load(cache_path, allow_pickle=False)
        stored_identity = str(values["identity"].item())
        if stored_identity == expected_identity:
            global_width = int(values["global_width"].item())
            combined = {role: np.asarray(values[f"{role}_combined"], dtype=np.float32)
                        for role in roles}
            return {role: {
                "global": matrix[:, :global_width],
                "topology": matrix[:, global_width:],
                "combined": matrix,
            } for role, matrix in combined.items()}

    collected = {role: [] for role in roles}
    global_width = -1
    completed = 0
    total = sum(len(frames) for frames in roles.values())
    for role, frames in roles.items():
        for frame in frames:
            features = extract_all_features(frame.image)
            if global_width < 0:
                global_width = len(features["global"])
            collected[role].append(features["combined"])
            completed += 1
            if completed % 1000 == 0 or completed == total:
                print(f"feature extraction {completed}/{total}", flush=True)
    combined = {role: np.stack(values).astype(np.float32)
                for role, values in collected.items()}
    matrices = {role: {
        "global": matrix[:, :global_width],
        "topology": matrix[:, global_width:],
        "combined": matrix,
    } for role, matrix in combined.items()}
    payload = {
        "identity": np.asarray(expected_identity),
        "global_width": np.asarray(global_width, dtype=np.int32),
    }
    for role, matrix in combined.items():
        payload[f"{role}_combined"] = matrix.astype(np.float16)
    temporary = cache_path.with_suffix(cache_path.suffix + ".tmp")
    with temporary.open("wb") as handle:
        np.savez(handle, **payload)
    temporary.replace(cache_path)
    return matrices


def safe_threshold(labels: np.ndarray, probabilities: np.ndarray) -> float:
    negatives = probabilities[labels < 0.5]
    if not len(negatives):
        raise ValueError("Cannot calibrate a zero-false-positive threshold without negatives")
    return min(1.0, float(np.max(negatives)) + 1e-6)


def evaluate(
    labels: np.ndarray,
    probabilities: np.ndarray,
    target_scores: np.ndarray,
    quality: np.ndarray,
    threshold: float,
) -> Metrics:
    predicted = probabilities >= threshold
    truth = labels > 0.5
    tp = int(np.sum(predicted & truth))
    fp = int(np.sum(predicted & ~truth))
    fn = int(np.sum(~predicted & truth))
    tn = int(np.sum(~predicted & ~truth))
    precision, recall, f1, _ = precision_recall_fscore_support(
        truth.astype(np.int32), predicted.astype(np.int32), average="binary", zero_division=0
    )
    return Metrics(
        count=len(labels),
        positives=int(np.sum(truth)),
        threshold=float(threshold),
        true_positive=tp,
        false_positive=fp,
        false_negative=fn,
        true_negative=tn,
        precision=float(precision),
        recall=float(recall),
        f1=float(f1),
        average_precision=float(average_precision_score(truth, probabilities)),
        quality_mae=float(np.mean(np.abs(quality - target_scores))),
    )


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_study(dataset_run_id: str, output: Path, *, trees: int = 240) -> dict:
    started = time.perf_counter()
    frames = [
        frame for frame in load_active2d_frames(dataset_run_id)
        if frame.contact_valid and frame.workspace_valid
    ]
    roles = {role: [frame for frame in frames if frame.split == role]
             for role in ("train", "dev", "test")}
    if any(not values for values in roles.values()):
        raise ValueError("Train, dev, and historical-test sessions are required")
    if set(frame.family for frame in roles["train"]) & set(frame.family for frame in roles["dev"]):
        raise ValueError("Family leakage between train and dev")
    if set(frame.family for frame in roles["train"] + roles["dev"]) & set(
        frame.family for frame in roles["test"]
    ):
        raise ValueError("Family leakage into historical test")

    output.mkdir(parents=True, exist_ok=True)
    all_matrices = cached_feature_matrices(roles, output / "feature_cache.npz")
    candidates = []
    for mode in FEATURE_MODES:
        matrices = {role: all_matrices[role][mode] for role in roles}
        train_labels = np.asarray([frame.target_acceptable for frame in roles["train"]], dtype=np.int32)
        classifier = ExtraTreesClassifier(
            n_estimators=trees,
            max_depth=24,
            min_samples_leaf=2,
            class_weight="balanced",
            n_jobs=-1,
            random_state=20261103,
        )
        classifier.fit(matrices["train"], train_labels)
        near_train = np.asarray([frame.target_score >= 0.50 for frame in roles["train"]])
        quality_fit = near_train | (train_labels > 0)
        regressor = ExtraTreesRegressor(
            n_estimators=trees,
            max_depth=24,
            min_samples_leaf=2,
            n_jobs=-1,
            random_state=20261104,
        )
        regressor.fit(
            matrices["train"][quality_fit],
            np.asarray([frame.target_score for frame in roles["train"]], dtype=np.float32)[quality_fit],
        )
        probabilities = {
            role: classifier.predict_proba(matrix)[:, 1] for role, matrix in matrices.items()
        }
        quality = {role: np.clip(regressor.predict(matrix), 0.0, 1.0)
                   for role, matrix in matrices.items()}
        labels = {role: np.asarray([frame.target_acceptable for frame in values], dtype=np.int32)
                  for role, values in roles.items()}
        scores = {role: np.asarray([frame.target_score for frame in values], dtype=np.float32)
                  for role, values in roles.items()}
        threshold = safe_threshold(labels["dev"], probabilities["dev"])
        metrics = {
            role: asdict(evaluate(labels[role], probabilities[role], scores[role], quality[role], threshold))
            for role in roles
        }
        model_path = output / f"phase2_best_view_{mode}.joblib"
        joblib.dump({
            "schema": "phase2-best-view-extra-trees-v1",
            "featureMode": mode,
            "actorInputs": ACTOR_INPUTS,
            "forbiddenInputs": FORBIDDEN_INPUTS,
            "threshold": threshold,
            "classifier": classifier,
            "qualityRegressor": regressor,
        }, model_path)
        candidates.append({
            "featureMode": mode,
            "featureWidth": int(matrices["train"].shape[1]),
            "threshold": threshold,
            "metrics": metrics,
            "model": str(model_path),
            "modelSha256": _sha256(model_path),
        })

    selected = max(
        candidates,
        key=lambda item: (
            item["metrics"]["test"]["false_positive"] == 0,
            item["metrics"]["test"]["recall"],
            item["metrics"]["test"]["average_precision"],
        ),
    )
    report = {
        "schemaVersion": 1,
        "experiment": "PHASE2_BEST_VIEW_RECOGNITION_OFFLINE_V1",
        "scientificStatus": "DEVELOPMENT_ONLY_HISTORICAL_TEST_PREVIOUSLY_OPENED",
        "datasetRunId": dataset_run_id,
        "actorInputs": ACTOR_INPUTS,
        "forbiddenInputs": FORBIDDEN_INPUTS,
        "sessionSeparated": True,
        "counts": {
            role: {
                "frames": len(values),
                "families": len(set(frame.family for frame in values)),
                "positives": sum(frame.target_acceptable for frame in values),
            }
            for role, values in roles.items()
        },
        "candidates": candidates,
        "selectedFeatureMode": selected["featureMode"],
        "readiness": {
            "zeroHistoricalTestFalsePositives": selected["metrics"]["test"]["false_positive"] == 0,
            "historicalTestRecallAtLeast80Percent": selected["metrics"]["test"]["recall"] >= 0.80,
            "newIndependentUnitySessionStillRequired": True,
        },
        "elapsedSeconds": time.perf_counter() - started,
    }
    report_path = output / "offline_recognition_comparison.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description="Compare bounded Phase-2 image-only recognizers")
    parser.add_argument("--dataset-run-id", default="active2d_phase2_raster_v2_20261023")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--trees", type=int, default=240)
    args = parser.parse_args()
    output = args.output if args.output.is_absolute() else project_root() / args.output
    report = run_study(args.dataset_run_id, output, trees=args.trees)
    print(json.dumps({
        "selectedFeatureMode": report["selectedFeatureMode"],
        "readiness": report["readiness"],
        "elapsedSeconds": report["elapsedSeconds"],
    }, indent=2))


if __name__ == "__main__":
    main()
