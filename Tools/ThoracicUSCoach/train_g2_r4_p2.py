from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import random
import sys
import time
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

import numpy as np
from PIL import Image
import psutil
from scipy.stats import spearmanr
from sklearn.metrics import average_precision_score, roc_auc_score
import torch
from torch import nn
from torch.utils.data import DataLoader, Dataset

from g2_r4_p2_lineage import OUTPUT, PROTOCOL, build as build_lineage, collect, read_json, sha256, write_json


ACTION_NAMES = ("RESET", "CRANIAL", "CAUDAL", "MEDIAL", "LATERAL", "ROTATECLOCKWISE", "ROTATECOUNTERCLOCKWISE", "OBSERVE")


def set_seed(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.use_deterministic_algorithms(True)


def binary_metrics(labels: np.ndarray, scores: np.ndarray, threshold: float) -> dict[str, Any]:
    y = labels.astype(bool)
    predicted = scores >= threshold
    tp, fp = int(np.sum(y & predicted)), int(np.sum(~y & predicted))
    fn, tn = int(np.sum(y & ~predicted)), int(np.sum(~y & ~predicted))
    recall = tp / (tp + fn) if tp + fn else None
    precision = tp / (tp + fp) if tp + fp else None
    specificity = tn / (tn + fp) if tn + fp else None
    return {
        "count": int(len(y)), "positiveSupport": int(y.sum()), "negativeSupport": int((~y).sum()),
        "truePositive": tp, "falsePositive": fp, "falseNegative": fn, "trueNegative": tn,
        "recall": recall, "precision": precision,
        "falsePositiveRate": fp / (fp + tn) if fp + tn else None,
        "balancedAccuracy": 0.5 * (recall + specificity) if recall is not None and specificity is not None else None,
        "averagePrecision": float(average_precision_score(y, scores)) if y.any() else None,
        "rocAuc": float(roc_auc_score(y, scores)) if y.any() and (~y).any() else None,
        "abstentionRate": float(np.mean(~predicted)) if len(y) else None,
    }


def bootstrap_interval(values: Iterable[float], seed: int = 20261025) -> list[float] | None:
    array = np.asarray(list(values), dtype=np.float64)
    if len(array) == 0:
        return None
    rng = np.random.default_rng(seed)
    samples = np.asarray([np.mean(rng.choice(array, len(array), replace=True)) for _ in range(2000)])
    return [float(np.quantile(samples, 0.025)), float(np.quantile(samples, 0.975))]


def quality_metrics(records: list[dict[str, Any]], predictions: np.ndarray) -> dict[str, Any]:
    truth = np.asarray([row["score"] for row in records], dtype=np.float64)
    rho = float(spearmanr(truth, predictions).statistic) if len(np.unique(truth)) > 1 else None
    regrets: list[float] = []
    family_rows: list[dict[str, Any]] = []
    by_case: dict[str, list[int]] = defaultdict(list)
    for index, row in enumerate(records):
        by_case[row["caseId"]].append(index)
    for case, indices in sorted(by_case.items()):
        selected = indices[int(np.argmax(predictions[indices]))]
        best = max(float(records[index]["score"]) for index in indices)
        selected_score = float(records[selected]["score"])
        regret = best - selected_score
        regrets.append(regret)
        family_rows.append({"caseId": case, "bestObservedScore": best, "selectedObservedScore": selected_score, "regret": regret})
    return {
        "spearman": rho,
        "medianEpisodeBestViewRegret": float(np.median(regrets)) if regrets else None,
        "meanEpisodeBestViewRegret": float(np.mean(regrets)) if regrets else None,
        "regretMean95Ci": bootstrap_interval(regrets),
        "perTrajectory": family_rows,
    }


def group_metrics(records: list[dict[str, Any]], scores: np.ndarray, threshold: float) -> dict[str, Any]:
    grouped: dict[str, list[int]] = defaultdict(list)
    for index, row in enumerate(records):
        grouped[row["spatialGroup"]].append(index)
    rows, aps = [], []
    recognized_positive = positive_groups = false_stop_groups = 0
    for group, indices in sorted(grouped.items()):
        labels = np.asarray([records[index]["target"] for index in indices], dtype=bool)
        current = scores[indices]
        positive = bool(labels.any())
        recognized = bool(np.any((current >= threshold) & labels))
        false_stop = bool(np.any((current >= threshold) & ~labels))
        if positive:
            positive_groups += 1
            recognized_positive += int(recognized)
        false_stop_groups += int(false_stop)
        ap = float(average_precision_score(labels, current)) if positive else None
        if ap is not None:
            aps.append(ap)
        rows.append({
            "spatialGroup": group, "frames": len(indices), "positiveFrames": int(labels.sum()),
            "recognizedPositive": recognized, "hasFalseStop": false_stop, "averagePrecision": ap,
            "maximumScore": float(np.max(current)),
        })
    return {
        "positiveGroups": positive_groups,
        "recognizedPositiveGroups": recognized_positive,
        "familyRecognitionRecall": recognized_positive / positive_groups if positive_groups else None,
        "falseStopGroups": false_stop_groups,
        "sourceGroupMacroAveragePrecision": float(np.mean(aps)) if aps else None,
        "perGroup": rows,
    }


def evaluate(records: list[dict[str, Any]], stop_scores: np.ndarray, quality_scores: np.ndarray, threshold: float) -> dict[str, Any]:
    labels = np.asarray([row["target"] for row in records], dtype=bool)
    near = np.asarray([abs(float(row["margin"])) <= 0.015 for row in records], dtype=bool)
    return {
        "threshold": threshold,
        "frame": binary_metrics(labels, stop_scores, threshold),
        "group": group_metrics(records, stop_scores, threshold),
        "nearBoundary": binary_metrics(labels[near], stop_scores[near], threshold) if near.any() else None,
        "quality": quality_metrics(records, quality_scores),
    }


def weights_for(records: list[dict[str, Any]]) -> np.ndarray:
    by_group: dict[str, list[int]] = defaultdict(list)
    for index, row in enumerate(records):
        by_group[row["spatialGroup"]].append(index)
    weights = np.zeros(len(records), dtype=np.float32)
    for indices in by_group.values():
        positive = [index for index in indices if records[index]["target"]]
        negative = [index for index in indices if not records[index]["target"]]
        if positive and negative:
            for index in positive:
                weights[index] = 0.5 / len(positive)
            for index in negative:
                weights[index] = 0.5 / len(negative)
        else:
            for index in indices:
                weights[index] = 1.0 / len(indices)
    weights *= len(records) / max(float(weights.sum()), 1e-9)
    return weights


class ImageStore:
    def __init__(self, repo: Path, records: list[dict[str, Any]], size: int) -> None:
        self.values = []
        for row in records:
            with Image.open(repo / row["image"]) as image:
                pixels = np.asarray(image.convert("L").resize((size, size), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
            self.values.append(pixels)
        self.values = np.stack(self.values).astype(np.float32)
        self.history = self._history_indices(records, 4)
        self.actions = np.stack([self._action(row["executedAction"]) for row in records]).astype(np.float32)
        self.feedback = np.asarray([[float(row["actionAccepted"]), float(row["actionCompleted"])] for row in records], dtype=np.float32)

    @staticmethod
    def _action(name: str) -> np.ndarray:
        value = np.zeros(len(ACTION_NAMES), dtype=np.float32)
        value[ACTION_NAMES.index(name if name in ACTION_NAMES else "RESET")] = 1.0
        return value

    @staticmethod
    def _history_indices(records: list[dict[str, Any]], length: int) -> np.ndarray:
        by_case: dict[str, list[int]] = defaultdict(list)
        result = np.zeros((len(records), length), dtype=np.int64)
        for current, row in enumerate(records):
            previous = by_case[row["caseId"]]
            chosen = (previous + [current])[-length:]
            result[current] = [chosen[0]] * (length - len(chosen)) + chosen
            previous.append(current)
        return result


class RecordDataset(Dataset):
    def __init__(
        self, store: ImageStore, records: list[dict[str, Any]], indices: np.ndarray, *,
        multiview: bool, mean: float, std: float, sample_weights: np.ndarray, quality_mean: float, quality_std: float,
    ) -> None:
        self.store, self.records, self.indices = store, records, np.asarray(indices, dtype=np.int64)
        self.multiview, self.mean, self.std = multiview, mean, std
        self.sample_weights = sample_weights
        self.quality_mean, self.quality_std = quality_mean, quality_std

    def __len__(self) -> int:
        return len(self.indices)

    def __getitem__(self, offset: int) -> tuple[torch.Tensor, ...]:
        index = int(self.indices[offset])
        if self.multiview:
            history = self.store.history[index]
            images = (self.store.values[history] - self.mean) / self.std
            available = np.asarray([float(self.records[item]["caseId"] == self.records[index]["caseId"] and self.records[item]["index"] <= self.records[index]["index"]) for item in history], dtype=np.float32)
            actions = np.concatenate([self.store.actions[history].reshape(-1), self.store.feedback[history].reshape(-1), available])
        else:
            images = ((self.store.values[index] - self.mean) / self.std)[None, :, :]
            actions = np.empty(0, dtype=np.float32)
        row = self.records[index]
        return (
            torch.from_numpy(images.astype(np.float32)), torch.from_numpy(actions.astype(np.float32)),
            torch.tensor(float(row["target"]), dtype=torch.float32),
            torch.tensor((float(row["score"]) - self.quality_mean) / self.quality_std, dtype=torch.float32),
            torch.tensor(float(self.sample_weights[offset]), dtype=torch.float32), torch.tensor(index, dtype=torch.long),
        )


class Encoder(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.layers = nn.Sequential(
            nn.Conv2d(1, 8, 5, stride=2, padding=2), nn.GroupNorm(2, 8), nn.ReLU(),
            nn.Conv2d(8, 16, 3, stride=2, padding=1), nn.GroupNorm(4, 16), nn.ReLU(),
            nn.Conv2d(16, 24, 3, stride=2, padding=1), nn.GroupNorm(4, 24), nn.ReLU(),
            nn.Conv2d(24, 32, 3, stride=2, padding=1), nn.GroupNorm(8, 32), nn.ReLU(),
            nn.AdaptiveAvgPool2d((4, 4)), nn.Flatten(), nn.Linear(32 * 4 * 4, 32), nn.ReLU(), nn.Dropout(0.10),
        )

    def forward(self, values: torch.Tensor) -> torch.Tensor:
        return self.layers(values)


class UltrasoundTwoHead(nn.Module):
    def __init__(self, multiview: bool) -> None:
        super().__init__()
        self.multiview = multiview
        self.encoder = Encoder()
        if multiview:
            width = 4 * 32 + 4 * len(ACTION_NAMES) + 4 * 2 + 4
            self.fusion = nn.Sequential(nn.Linear(width, 64), nn.ReLU(), nn.Dropout(0.10))
            head_width = 64
        else:
            self.fusion = nn.Identity()
            head_width = 32
        self.stop_head = nn.Linear(head_width, 1)
        self.quality_head = nn.Linear(head_width, 1)

    def forward(self, images: torch.Tensor, actions: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        if self.multiview:
            batch, history = images.shape[:2]
            encoded = self.encoder(images.reshape(batch * history, 1, images.shape[-2], images.shape[-1])).reshape(batch, -1)
            hidden = self.fusion(torch.cat((encoded, actions), dim=1))
        else:
            hidden = self.encoder(images)
        return self.stop_head(hidden).reshape(-1), self.quality_head(hidden).reshape(-1)


class StructuralTwoHead(nn.Module):
    def __init__(self, width: int) -> None:
        super().__init__()
        self.stop_head = nn.Linear(width, 1)
        self.quality_head = nn.Linear(width, 1)

    def forward(self, values: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        return self.stop_head(values).reshape(-1), self.quality_head(values).reshape(-1)


def train_image_model(
    store: ImageStore, records: list[dict[str, Any]], train_indices: np.ndarray, validation_indices: np.ndarray,
    *, seed: int, multiview: bool, protocol: dict[str, Any], fixed_epochs: int | None = None,
) -> tuple[UltrasoundTwoHead, dict[str, Any], dict[str, float]]:
    set_seed(seed)
    config = protocol["training"]
    pixels = store.values[train_indices]
    mean, std = float(pixels.mean()), max(float(pixels.std()), 1e-6)
    quality = np.asarray([records[index]["score"] for index in train_indices], dtype=np.float32)
    quality_mean, quality_std = float(quality.mean()), max(float(quality.std()), 1e-6)
    train_records = [records[index] for index in train_indices]
    validation_records = [records[index] for index in validation_indices]
    train_dataset = RecordDataset(store, records, train_indices, multiview=multiview, mean=mean, std=std, sample_weights=weights_for(train_records), quality_mean=quality_mean, quality_std=quality_std)
    validation_dataset = RecordDataset(store, records, validation_indices, multiview=multiview, mean=mean, std=std, sample_weights=weights_for(validation_records), quality_mean=quality_mean, quality_std=quality_std)
    generator = torch.Generator().manual_seed(seed)
    train_loader = DataLoader(train_dataset, batch_size=int(config["batchSize"]), shuffle=True, generator=generator, num_workers=0)
    validation_loader = DataLoader(validation_dataset, batch_size=128, shuffle=False, num_workers=0)
    model = UltrasoundTwoHead(multiview)
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(config["learningRate"]), weight_decay=float(config["weightDecay"]))
    maximum = fixed_epochs or int(config["maximumEpochs"])
    best_loss, best_epoch, best_state, stale = math.inf, 0, None, 0
    started = time.perf_counter()
    for epoch in range(maximum):
        model.train()
        for images, actions, target, score, weight, _ in train_loader:
            optimizer.zero_grad(set_to_none=True)
            stop, quality_prediction = model(images, actions)
            stop_loss = nn.functional.binary_cross_entropy_with_logits(stop, target, reduction="none")
            quality_loss = (quality_prediction - score) ** 2
            loss = ((stop_loss + 0.25 * quality_loss) * weight).mean()
            loss.backward()
            optimizer.step()
        if fixed_epochs is not None:
            best_epoch = epoch + 1
            continue
        model.eval()
        loss_sum = weight_sum = 0.0
        with torch.no_grad():
            for images, actions, target, score, weight, _ in validation_loader:
                stop, quality_prediction = model(images, actions)
                value = nn.functional.binary_cross_entropy_with_logits(stop, target, reduction="none") + 0.25 * (quality_prediction - score) ** 2
                loss_sum += float((value * weight).sum())
                weight_sum += float(weight.sum())
        current = loss_sum / max(weight_sum, 1e-9)
        if current < best_loss - 1e-5:
            best_loss, best_epoch, best_state, stale = current, epoch + 1, copy.deepcopy(model.state_dict()), 0
        else:
            stale += 1
        if epoch + 1 >= 24 and stale >= 16:
            break
    if best_state is not None:
        model.load_state_dict(best_state)
    budget = {"epochsCompleted": epoch + 1, "bestEpoch": best_epoch, "bestValidationLoss": None if fixed_epochs else best_loss, "seconds": time.perf_counter() - started}
    return model, budget, {"pixelMean": mean, "pixelStd": std, "qualityMean": quality_mean, "qualityStd": quality_std}


def predict_image(model: UltrasoundTwoHead, store: ImageStore, records: list[dict[str, Any]], indices: np.ndarray, stats: dict[str, float], multiview: bool) -> tuple[np.ndarray, np.ndarray]:
    selected = [records[index] for index in indices]
    dataset = RecordDataset(store, records, indices, multiview=multiview, mean=stats["pixelMean"], std=stats["pixelStd"], sample_weights=np.ones(len(indices), dtype=np.float32), quality_mean=stats["qualityMean"], quality_std=stats["qualityStd"])
    loader = DataLoader(dataset, batch_size=128, shuffle=False, num_workers=0)
    stop_values, quality_values = [], []
    model.eval()
    with torch.no_grad():
        for images, actions, _, _, _, _ in loader:
            stop, quality = model(images, actions)
            stop_values.extend(torch.sigmoid(stop).numpy().tolist())
            quality_values.extend((quality.numpy() * stats["qualityStd"] + stats["qualityMean"]).tolist())
    return np.asarray(stop_values), np.asarray(quality_values)


def structural_matrix(repo: Path, records: list[dict[str, Any]]) -> np.ndarray:
    from g2_r2_features import structural_features_v4
    return np.stack([structural_features_v4(repo / row["image"]) for row in records]).astype(np.float32)


def train_structural(features: np.ndarray, records: list[dict[str, Any]], train_indices: np.ndarray, validation_indices: np.ndarray, seed: int, protocol: dict[str, Any]) -> tuple[StructuralTwoHead, dict[str, Any], dict[str, Any]]:
    set_seed(seed)
    mean, scale = features[train_indices].mean(axis=0), features[train_indices].std(axis=0)
    scale[scale < 1e-5] = 1.0
    quality = np.asarray([records[index]["score"] for index in train_indices], dtype=np.float32)
    q_mean, q_std = float(quality.mean()), max(float(quality.std()), 1e-6)
    x_train = torch.from_numpy(((features[train_indices] - mean) / scale).astype(np.float32))
    x_val = torch.from_numpy(((features[validation_indices] - mean) / scale).astype(np.float32))
    y_train = torch.tensor([float(records[index]["target"]) for index in train_indices])
    q_train = torch.tensor([(float(records[index]["score"]) - q_mean) / q_std for index in train_indices])
    y_val = torch.tensor([float(records[index]["target"]) for index in validation_indices])
    q_val = torch.tensor([(float(records[index]["score"]) - q_mean) / q_std for index in validation_indices])
    weight = torch.from_numpy(weights_for([records[index] for index in train_indices]))
    val_weight = torch.from_numpy(weights_for([records[index] for index in validation_indices]))
    model = StructuralTwoHead(features.shape[1])
    optimizer = torch.optim.AdamW(model.parameters(), lr=0.001, weight_decay=0.0001)
    best_loss, best_state, best_epoch, stale = math.inf, None, 0, 0
    started = time.perf_counter()
    for epoch in range(int(protocol["training"]["maximumEpochs"])):
        model.train(); optimizer.zero_grad(set_to_none=True)
        stop, prediction = model(x_train)
        loss = ((nn.functional.binary_cross_entropy_with_logits(stop, y_train, reduction="none") + 0.25 * (prediction - q_train) ** 2) * weight).mean()
        loss.backward(); optimizer.step()
        model.eval()
        with torch.no_grad():
            stop, prediction = model(x_val)
            current = float(((nn.functional.binary_cross_entropy_with_logits(stop, y_val, reduction="none") + 0.25 * (prediction - q_val) ** 2) * val_weight).sum() / val_weight.sum())
        if current < best_loss - 1e-5:
            best_loss, best_state, best_epoch, stale = current, copy.deepcopy(model.state_dict()), epoch + 1, 0
        else:
            stale += 1
        if epoch + 1 >= 24 and stale >= 16:
            break
    model.load_state_dict(best_state)
    return model, {"epochsCompleted": epoch + 1, "bestEpoch": best_epoch, "bestValidationLoss": best_loss, "seconds": time.perf_counter() - started}, {"mean": mean, "scale": scale, "qualityMean": q_mean, "qualityStd": q_std}


def predict_structural(model: StructuralTwoHead, features: np.ndarray, indices: np.ndarray, stats: dict[str, Any]) -> tuple[np.ndarray, np.ndarray]:
    x = torch.from_numpy(((features[indices] - stats["mean"]) / stats["scale"]).astype(np.float32))
    model.eval()
    with torch.no_grad():
        stop, quality = model(x)
    return torch.sigmoid(stop).numpy(), quality.numpy() * stats["qualityStd"] + stats["qualityMean"]


def split_indices(records: list[dict[str, Any]], evaluation_groups: set[str], fold: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    primary_train_groups = sorted({row["spatialGroup"] for row in records if row["countsAsIndependentP1"] and row["spatialGroup"] not in evaluation_groups})
    positive = [group for group in primary_train_groups if any(row["target"] for row in records if row["spatialGroup"] == group and row["countsAsIndependentP1"])]
    negative = [group for group in primary_train_groups if group not in positive]
    validation_groups = {positive[fold % len(positive)], negative[fold % len(negative)]}
    evaluation = np.asarray([index for index, row in enumerate(records) if row["countsAsIndependentP1"] and row["spatialGroup"] in evaluation_groups], dtype=np.int64)
    validation = np.asarray([index for index, row in enumerate(records) if row["countsAsIndependentP1"] and row["spatialGroup"] in validation_groups], dtype=np.int64)
    training = np.asarray([
        index for index, row in enumerate(records)
        if row["spatialGroup"] not in evaluation_groups and row["spatialGroup"] not in validation_groups
    ], dtype=np.int64)
    return training, validation, evaluation


def threshold_zero_fp(labels: np.ndarray, scores: np.ndarray) -> float:
    negatives = scores[~labels.astype(bool)]
    if not len(negatives):
        return 1.0
    return min(1.0, float(np.nextafter(np.max(negatives), math.inf)))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    repo = args.repo.resolve()
    torch.set_num_threads(min(4, max(1, torch.get_num_threads())))
    started = time.perf_counter()
    process = psutil.Process()
    peak_rss = process.memory_info().rss

    graph = build_lineage(repo)
    if graph["integrity"]["status"] != "PASS":
        raise SystemExit("P2-A integrity failed")
    protocol = read_json(repo / PROTOCOL)
    cases, records, errors = collect(repo, protocol)
    if errors:
        raise SystemExit("; ".join(errors))
    spatial_by_case = {case["caseId"]: case["spatialGroup"] for case in graph["cases"]}
    source_to_spatial = {source: group for group, sources in graph["spatialGroupSources"].items() for source in sources}
    for row in records:
        row["spatialGroup"] = spatial_by_case.get(row["caseId"], source_to_spatial.get(row["sourceBaseGroup"], "auxiliary_only_" + row["sourceBaseGroup"]))
    store = ImageStore(repo, records, int(protocol["training"]["imageSize"][0]))
    structural = structural_matrix(repo, records)
    peak_rss = max(peak_rss, process.memory_info().rss)

    primary_indices = np.asarray([index for index, row in enumerate(records) if row["countsAsIndependentP1"]], dtype=np.int64)
    primary_records = [records[index] for index in primary_indices]
    labels_primary = np.asarray([row["target"] for row in primary_records], dtype=bool)

    sys.path.insert(0, str(repo / "Tools/ThoracicUSCoach"))
    from active2d_stop import request_stop_features, stop_probabilities
    frozen_path = repo / "Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_stop_live_spatial_logistic_v1.json"
    frozen = read_json(frozen_path)
    frozen_scores_all = []
    for row in records:
        feature = request_stop_features(repo / row["image"], None, row["executedAction"], "spatial-v2")
        frozen_scores_all.append(float(stop_probabilities(frozen, feature)[0]))
    frozen_scores_all = np.asarray(frozen_scores_all)
    frozen_primary = frozen_scores_all[primary_indices]
    frozen_result = evaluate(primary_records, frozen_primary, frozen_primary, 0.93)

    seeds = [int(seed) for seed in protocol["training"]["seeds"]]
    architectures = {"cnn_single_frame": False, "cnn_causal_multiview_h4": True}
    runs: dict[str, list[dict[str, Any]]] = {name: [] for name in (*architectures, "structural_v4_equivalent")}
    oof: dict[str, dict[int, dict[str, np.ndarray]]] = {
        name: {seed: {"stop": np.full(len(records), np.nan), "quality": np.full(len(records), np.nan)} for seed in seeds}
        for name in runs
    }
    best_epochs: dict[str, dict[int, list[int]]] = {name: {seed: [] for seed in seeds} for name in runs}

    for seed in seeds:
        for fold_info in graph["developmentFolds"]:
            fold = int(fold_info["fold"])
            train_indices, validation_indices, evaluation_indices = split_indices(records, set(fold_info["evaluationSpatialGroups"]), fold)
            fold_records = [records[index] for index in evaluation_indices]
            structural_model, budget, structural_stats = train_structural(structural, records, train_indices, validation_indices, seed * 100 + fold, protocol)
            stop, quality = predict_structural(structural_model, structural, evaluation_indices, structural_stats)
            oof["structural_v4_equivalent"][seed]["stop"][evaluation_indices] = stop
            oof["structural_v4_equivalent"][seed]["quality"][evaluation_indices] = quality
            best_epochs["structural_v4_equivalent"][seed].append(int(budget["bestEpoch"]))
            runs["structural_v4_equivalent"].append({"seed": seed, "fold": fold, "trainFrames": len(train_indices), "validationFrames": len(validation_indices), "evaluationFrames": len(evaluation_indices), "budget": budget, "metricsAtPointFive": evaluate(fold_records, stop, quality, 0.5)})
            for name, multiview in architectures.items():
                model, budget, stats = train_image_model(store, records, train_indices, validation_indices, seed=seed * 100 + fold, multiview=multiview, protocol=protocol)
                stop, quality = predict_image(model, store, records, evaluation_indices, stats, multiview)
                oof[name][seed]["stop"][evaluation_indices] = stop
                oof[name][seed]["quality"][evaluation_indices] = quality
                best_epochs[name][seed].append(int(budget["bestEpoch"]))
                runs[name].append({"seed": seed, "fold": fold, "trainFrames": len(train_indices), "validationFrames": len(validation_indices), "evaluationFrames": len(evaluation_indices), "budget": budget, "metricsAtPointFive": evaluate(fold_records, stop, quality, 0.5)})
                peak_rss = max(peak_rss, process.memory_info().rss)

    summaries: dict[str, Any] = {}
    ensemble_scores: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for name in runs:
        per_seed = []
        seed_stop, seed_quality = [], []
        for seed in seeds:
            stop = oof[name][seed]["stop"][primary_indices]
            quality = oof[name][seed]["quality"][primary_indices]
            if np.isnan(stop).any() or np.isnan(quality).any():
                raise RuntimeError(f"OOF coverage missing for {name}/{seed}")
            seed_stop.append(stop); seed_quality.append(quality)
            per_seed.append({"seed": seed, "metricsAtPointFive": evaluate(primary_records, stop, quality, 0.5), "bestEpochs": best_epochs[name][seed]})
        ensemble_stop, ensemble_quality = np.mean(seed_stop, axis=0), np.mean(seed_quality, axis=0)
        ensemble_scores[name] = (ensemble_stop, ensemble_quality)
        summaries[name] = {
            "perSeed": per_seed,
            "ensembleAtPointFive": evaluate(primary_records, ensemble_stop, ensemble_quality, 0.5),
            "medianSeedGroupMacroAveragePrecision": float(np.median([item["metricsAtPointFive"]["group"]["sourceGroupMacroAveragePrecision"] for item in per_seed])),
            "minimumSeedAveragePrecision": float(min(item["metricsAtPointFive"]["frame"]["averagePrecision"] for item in per_seed)),
        }

    single_ap = summaries["cnn_single_frame"]["medianSeedGroupMacroAveragePrecision"]
    temporal_ap = summaries["cnn_causal_multiview_h4"]["medianSeedGroupMacroAveragePrecision"]
    selected = "cnn_causal_multiview_h4" if temporal_ap >= single_ap + float(protocol["development"]["candidateSelection"]["historyComplexityMargin"]) else "cnn_single_frame"
    selected_stop, selected_quality = ensemble_scores[selected]
    threshold = threshold_zero_fp(labels_primary, selected_stop)
    calibrated = evaluate(primary_records, selected_stop, selected_quality, threshold)
    positive_groups = int(calibrated["group"]["positiveGroups"])
    eligible = (
        summaries[selected]["minimumSeedAveragePrecision"] >= 0.50
        and int(calibrated["frame"]["falsePositive"]) == 0
        and float(calibrated["frame"]["recall"] or 0.0) >= 0.70
        and int(calibrated["group"]["recognizedPositiveGroups"]) >= max(1, positive_groups - 1)
    )

    final_models: list[dict[str, Any]] = []
    all_indices = np.arange(len(records), dtype=np.int64)
    # Final refit uses the median OOF best epoch and all consumed development data.
    # A one-row validation placeholder is ignored because fixed_epochs disables validation selection.
    for seed in seeds:
        epochs = max(1, int(round(float(np.median(best_epochs[selected][seed])))))
        model, budget, stats = train_image_model(store, records, all_indices, all_indices[:1], seed=seed, multiview=architectures[selected], protocol=protocol, fixed_epochs=epochs)
        final_models.append({"seed": seed, "epochs": epochs, "stats": stats, "stateDict": {key: value.detach().cpu() for key, value in model.state_dict().items()}})
    output = repo / OUTPUT
    checkpoint_path = output / "g2_r4_p2_candidate.pt"
    torch.save({
        "schemaVersion": 1,
        "taskId": "G2-R4-P2",
        "architecture": selected,
        "historyLength": 4 if architectures[selected] else 1,
        "threshold": threshold,
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "actuallyExecutedActionFeedback"],
        "deployment": {"status": "BLOCKED", "safeOperatingPoint": False, "reason": "offline development only; independent confirmation not yet executed"},
        "models": final_models,
    }, checkpoint_path)
    checkpoint_meta = {
        "schemaVersion": 1, "taskId": "G2-R4-P2", "architecture": selected,
        "threshold": threshold, "seeds": seeds, "historyLength": 4 if architectures[selected] else 1,
        "checkpoint": str(checkpoint_path.relative_to(repo)).replace("\\", "/"), "checkpointSha256": sha256(checkpoint_path),
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "actuallyExecutedActionFeedback"],
        "deployment": {"status": "BLOCKED", "safeOperatingPoint": False, "reason": "independent confirmation pending or development gate failed"},
        "developmentEligibleForConfirmation": eligible,
    }
    write_json(output / "g2_r4_p2_candidate.json", checkpoint_meta)
    result = {
        "schemaVersion": 1,
        "taskId": "G2-R4-P2-C-D",
        "protocol": PROTOCOL, "protocolSha256": sha256(repo / PROTOCOL),
        "lineageSha256": sha256(output / "p2_a_lineage_graph.json"),
        "frozenBaseline": {"checkpoint": str(frozen_path.relative_to(repo)).replace("\\", "/"), "checkpointSha256": sha256(frozen_path), "threshold": 0.93, "metrics": frozen_result},
        "foldRuns": runs,
        "summaries": summaries,
        "selection": {
            "selected": selected, "singleFrameMedianGroupMacroAp": single_ap, "causalMultiviewMedianGroupMacroAp": temporal_ap,
            "causalImprovement": temporal_ap - single_ap, "calibratedThreshold": threshold,
            "calibratedDevelopmentMetrics": calibrated, "eligibleForIndependentConfirmation": eligible,
            "rule": protocol["development"]["candidateSelection"],
        },
        "checkpoint": checkpoint_meta,
        "compute": {"elapsedSeconds": time.perf_counter() - started, "peakRssBytes": peak_rss, "torchVersion": torch.__version__, "device": "cpu", "threads": torch.get_num_threads()},
        "lockedFamiliesOpened": [],
    }
    write_json(output / "p2_cd_development_results.json", result)
    print(json.dumps({"selection": result["selection"], "compute": result["compute"], "checkpointSha256": checkpoint_meta["checkpointSha256"]}, indent=2))


if __name__ == "__main__":
    main()
