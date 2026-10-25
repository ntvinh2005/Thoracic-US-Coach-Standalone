from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image
import torch
from torch import nn


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def binary_metrics(labels: np.ndarray, scores: np.ndarray, threshold: float) -> dict[str, Any]:
    labels = labels.astype(bool)
    predicted = scores >= threshold
    tp = int(np.sum(labels & predicted))
    fp = int(np.sum(~labels & predicted))
    fn = int(np.sum(labels & ~predicted))
    tn = int(np.sum(~labels & ~predicted))
    recall = tp / (tp + fn) if tp + fn else None
    fpr = fp / (fp + tn) if fp + tn else None
    specificity = tn / (tn + fp) if tn + fp else None
    return {
        "count": int(len(labels)), "positiveSupport": int(labels.sum()),
        "truePositive": tp, "falsePositive": fp, "falseNegative": fn, "trueNegative": tn,
        "recall": recall, "falsePositiveRate": fpr,
        "balancedAccuracy": 0.5 * (recall + specificity) if recall is not None and specificity is not None else None,
        "accuracy": float(np.mean(predicted == labels)) if len(labels) else None,
    }


def rank_metrics(labels: np.ndarray, scores: np.ndarray) -> dict[str, float | None]:
    labels = labels.astype(bool)
    positives, negatives = scores[labels], scores[~labels]
    if not len(positives) or not len(negatives):
        return {"rocAuc": None, "averagePrecision": None}
    comparisons = (positives[:, None] > negatives[None, :]).mean()
    ties = (positives[:, None] == negatives[None, :]).mean()
    auc = float(comparisons + 0.5 * ties)
    order = np.argsort(-scores, kind="mergesort")
    ordered = labels[order]
    precision = np.cumsum(ordered) / np.arange(1, len(ordered) + 1)
    ap = float(np.sum(precision * ordered) / max(int(labels.sum()), 1))
    return {"rocAuc": auc, "averagePrecision": ap}


def set_seed(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)


class SmallUltrasoundCNN(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.encoder = nn.Sequential(
            nn.Conv2d(1, 8, 5, stride=2, padding=2), nn.GroupNorm(2, 8), nn.ReLU(),
            nn.Conv2d(8, 16, 3, stride=2, padding=1), nn.GroupNorm(4, 16), nn.ReLU(),
            nn.Conv2d(16, 24, 3, stride=2, padding=1), nn.GroupNorm(4, 24), nn.ReLU(),
            nn.Conv2d(24, 32, 3, stride=2, padding=1), nn.GroupNorm(8, 32), nn.ReLU(),
            nn.AdaptiveAvgPool2d((4, 4)), nn.Flatten(),
            nn.Linear(32 * 4 * 4, 32), nn.ReLU(), nn.Dropout(0.10),
        )
        self.head = nn.Linear(32, 1)

    def features(self, values: torch.Tensor) -> torch.Tensor:
        return self.encoder(values)

    def forward(self, values: torch.Tensor) -> torch.Tensor:
        return self.head(self.features(values)).reshape(-1)


class LinearStructural(nn.Module):
    def __init__(self, width: int) -> None:
        super().__init__()
        self.head = nn.Linear(width, 1)

    def forward(self, values: torch.Tensor) -> torch.Tensor:
        return self.head(values).reshape(-1)


def sample_weights(labels: np.ndarray, groups: list[str]) -> np.ndarray:
    counts: dict[str, int] = defaultdict(int)
    for group in groups:
        counts[group] += 1
    values = np.asarray([1.0 / counts[group] for group in groups], dtype=np.float32)
    for target in (False, True):
        mask = labels.astype(bool) == target
        total = float(values[mask].sum())
        if total > 0:
            values[mask] *= 0.5 / total
    values *= len(values) / max(float(values.sum()), 1e-8)
    return values


def fit_model(
    model: nn.Module,
    train_x: torch.Tensor,
    train_y: np.ndarray,
    groups: list[str],
    *,
    epochs: int,
    learning_rate: float,
    weight_decay: float,
    stable_mask: np.ndarray | None = None,
    early_stop_stable: bool = False,
) -> tuple[nn.Module, dict[str, Any]]:
    labels = torch.from_numpy(train_y.astype(np.float32))
    weights = torch.from_numpy(sample_weights(train_y, groups))
    optimizer = torch.optim.AdamW(model.parameters(), lr=learning_rate, weight_decay=weight_decay)
    stable = np.ones(len(train_y), dtype=bool) if stable_mask is None else stable_mask
    consecutive = 0
    losses = []
    completed = 0
    for epoch in range(epochs):
        model.train()
        optimizer.zero_grad(set_to_none=True)
        logits = model(train_x)
        loss = (nn.functional.binary_cross_entropy_with_logits(logits, labels, reduction="none") * weights).mean()
        loss.backward()
        optimizer.step()
        losses.append(float(loss.detach()))
        completed = epoch + 1
        if early_stop_stable:
            model.eval()
            with torch.no_grad():
                predicted = torch.sigmoid(model(train_x)).numpy() >= 0.5
            consecutive = consecutive + 1 if np.all(predicted[stable] == train_y[stable].astype(bool)) else 0
            if consecutive >= 20:
                break
    return model, {"epochsCompleted": completed, "initialLoss": losses[0], "finalLoss": losses[-1]}


def predict(model: nn.Module, values: torch.Tensor) -> np.ndarray:
    model.eval()
    with torch.no_grad():
        return torch.sigmoid(model(values)).numpy().astype(np.float64)


def median_metric(runs: list[dict[str, Any]], section: str, key: str) -> float:
    return float(np.median([run[section][key] for run in runs]))


def decide_stage_a(inputs: dict[str, Any]) -> tuple[str, str]:
    if inputs["integrityErrors"] or inputs["medianStableMemorizationAccuracy"] < 0.98:
        return "FAIL", "Integrity or stable-case memorization prerequisite failed."
    if (
        inputs["medianSamePoseStableRecall"] >= 0.80
        and inputs["medianSamePoseStableFalsePositiveRate"] <= 0.05
        and inputs["medianCnnCrossFamilyBalancedAccuracy"] >= inputs["requiredBalancedAccuracy"]
        and inputs["medianCnnCrossFamilyRecall"] >= 0.60
        and inputs["medianCnnCrossFamilyFalsePositiveRate"] <= 0.20
    ):
        return "PASS", "All predeclared engineering-basis criteria passed."
    return "INCONCLUSIVE", "CNN can fit the diagnostic data, but the full repeat/cross-family improvement criteria were not met."


def main() -> None:
    parser = argparse.ArgumentParser(description="G2-R4-A bounded learned-representation diagnostic")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    repo = args.repo.resolve()
    protocol_path = repo / "Tools" / "ThoracicUSCoach" / "experiments" / "g2_r4_a_representation_v1.json"
    source_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r3_a2_20261025" / "unity_capture" / "g2_r3_a2_unity_report.json"
    output = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r4_20261025"
    protocol, source = load_json(protocol_path), load_json(source_path)
    output.mkdir(parents=True, exist_ok=True)
    torch.set_num_threads(min(4, max(torch.get_num_threads(), 1)))

    records: list[dict[str, Any]] = []
    integrity_errors = []
    for case in source["cases"]:
        labels = [bool(item["targetAcceptable"]) for item in case["observations"]]
        stable = len(set(labels)) == 1
        for observation in case["observations"]:
            path = repo / str(observation["image"]).replace("\\", "/")
            actual = sha256(path)
            if actual != observation["imageSha256"]:
                integrity_errors.append(str(path))
            with Image.open(path) as image:
                original_size = image.size
                values = np.asarray(image.convert("L").resize((128, 128), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
            records.append({
                "caseId": case["caseId"], "family": case["family"], "repeat": int(observation["repeatIndex"]),
                "path": path, "imageSha256": actual, "originalSize": list(original_size),
                "label": bool(observation["targetAcceptable"]), "score": float(observation["score"]["total"]),
                "stable": stable, "pixels": values,
            })
    if len(records) != 96 or any(item["originalSize"] != [1024, 1024] for item in records):
        integrity_errors.append("unexpected image count or native resolution")

    images = np.stack([item["pixels"] for item in records])[:, None, :, :]
    labels = np.asarray([item["label"] for item in records], dtype=np.float32)
    groups = [item["caseId"] for item in records]
    stable_mask = np.asarray([item["stable"] for item in records], dtype=bool)
    seeds = [int(value) for value in protocol["cnn"]["seeds"]]

    sys.path.insert(0, str(repo / "Tools" / "ThoracicUSCoach"))
    from active2d_stop import request_stop_features, stop_probabilities
    from g2_r2_features import structural_features_v4

    frozen_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "checkpoints" / "phase2_stop_live_spatial_logistic_v1.json"
    frozen = load_json(frozen_path)
    frozen_scores = np.asarray([
        float(stop_probabilities(frozen, request_stop_features(item["path"], None, "RESET", "spatial-v2"))[0])
        for item in records
    ])
    frozen_metrics = binary_metrics(labels[stable_mask], frozen_scores[stable_mask], 0.93)
    frozen_metrics.update(rank_metrics(labels[stable_mask], frozen_scores[stable_mask]))

    structural = np.stack([structural_features_v4(item["path"]) for item in records]).astype(np.float32)
    memorization_runs = []
    memorization_seed47: SmallUltrasoundCNN | None = None
    for seed in seeds:
        set_seed(seed)
        mean, std = float(images.mean()), max(float(images.std()), 1e-6)
        x = torch.from_numpy(((images - mean) / std).astype(np.float32))
        model, budget = fit_model(
            SmallUltrasoundCNN(), x, labels, groups,
            epochs=int(protocol["budgets"]["memorizationEpochs"]), learning_rate=0.001, weight_decay=0.0001,
            stable_mask=stable_mask, early_stop_stable=True,
        )
        scores = predict(model, x)
        run = {"seed": seed, "budget": budget, "all": binary_metrics(labels, scores, 0.5), "stable": binary_metrics(labels[stable_mask], scores[stable_mask], 0.5), "unstable": binary_metrics(labels[~stable_mask], scores[~stable_mask], 0.5)}
        memorization_runs.append(run)
        if seed == 47:
            memorization_seed47 = model

    train_repeat = np.asarray([item["repeat"] <= 2 for item in records], dtype=bool)
    eval_repeat = ~train_repeat
    repeat_runs = []
    for seed in seeds:
        set_seed(seed)
        mean = float(images[train_repeat].mean())
        std = max(float(images[train_repeat].std()), 1e-6)
        train_x = torch.from_numpy(((images[train_repeat] - mean) / std).astype(np.float32))
        eval_x = torch.from_numpy(((images[eval_repeat] - mean) / std).astype(np.float32))
        train_indices = np.flatnonzero(train_repeat)
        model, budget = fit_model(
            SmallUltrasoundCNN(), train_x, labels[train_repeat], [groups[index] for index in train_indices],
            epochs=int(protocol["budgets"]["samePoseRepeatEpochs"]), learning_rate=0.001, weight_decay=0.0001,
        )
        scores = predict(model, eval_x)
        eval_stable = stable_mask[eval_repeat]
        repeat_runs.append({
            "seed": seed, "budget": budget,
            "all": {**binary_metrics(labels[eval_repeat], scores, 0.5), **rank_metrics(labels[eval_repeat], scores)},
            "stable": {**binary_metrics(labels[eval_repeat][eval_stable], scores[eval_stable], 0.5), **rank_metrics(labels[eval_repeat][eval_stable], scores[eval_stable])},
            "unstable": binary_metrics(labels[eval_repeat][~eval_stable], scores[~eval_stable], 0.5),
        })

    fold_definitions = protocol["crossFamilyFolds"]
    cnn_cross_seed_runs, structural_cross_seed_runs = [], []
    for seed in seeds:
        cnn_labels, cnn_scores, cnn_families = [], [], []
        structural_labels, structural_scores = [], []
        fold_rows = []
        for fold in fold_definitions:
            eval_families = set(fold["evaluationFamilies"])
            train_mask = np.asarray([item["stable"] and item["family"] not in eval_families for item in records], dtype=bool)
            eval_mask = np.asarray([item["stable"] and item["family"] in eval_families for item in records], dtype=bool)
            train_indices = np.flatnonzero(train_mask)
            set_seed(seed * 10 + int(fold["fold"]))
            image_mean = float(images[train_mask].mean())
            image_std = max(float(images[train_mask].std()), 1e-6)
            cnn, cnn_budget = fit_model(
                SmallUltrasoundCNN(), torch.from_numpy(((images[train_mask] - image_mean) / image_std).astype(np.float32)),
                labels[train_mask], [groups[index] for index in train_indices],
                epochs=int(protocol["budgets"]["crossFamilyEpochs"]), learning_rate=0.001, weight_decay=0.0001,
            )
            current_cnn = predict(cnn, torch.from_numpy(((images[eval_mask] - image_mean) / image_std).astype(np.float32)))

            feature_mean = structural[train_mask].mean(axis=0)
            feature_std = structural[train_mask].std(axis=0)
            feature_std[feature_std < 1e-5] = 1.0
            linear, linear_budget = fit_model(
                LinearStructural(structural.shape[1]),
                torch.from_numpy(((structural[train_mask] - feature_mean) / feature_std).astype(np.float32)),
                labels[train_mask], [groups[index] for index in train_indices],
                epochs=int(protocol["budgets"]["crossFamilyEpochs"]), learning_rate=0.01, weight_decay=0.001,
            )
            current_structural = predict(linear, torch.from_numpy(((structural[eval_mask] - feature_mean) / feature_std).astype(np.float32)))
            current_labels = labels[eval_mask]
            cnn_labels.extend(current_labels.tolist())
            cnn_scores.extend(current_cnn.tolist())
            structural_labels.extend(current_labels.tolist())
            structural_scores.extend(current_structural.tolist())
            cnn_families.extend([item["family"] for index, item in enumerate(records) if eval_mask[index]])
            fold_rows.append({
                "fold": fold["fold"], "evaluationFamilies": sorted(eval_families),
                "trainFrames": int(train_mask.sum()), "evaluationFrames": int(eval_mask.sum()),
                "cnnBudget": cnn_budget, "structuralBudget": linear_budget,
                "cnn": {**binary_metrics(current_labels, current_cnn, 0.5), **rank_metrics(current_labels, current_cnn)},
                "structuralV4": {**binary_metrics(current_labels, current_structural, 0.5), **rank_metrics(current_labels, current_structural)},
            })
        y_cnn, s_cnn = np.asarray(cnn_labels), np.asarray(cnn_scores)
        y_structural, s_structural = np.asarray(structural_labels), np.asarray(structural_scores)
        cnn_cross_seed_runs.append({"seed": seed, "pooled": {**binary_metrics(y_cnn, s_cnn, 0.5), **rank_metrics(y_cnn, s_cnn)}, "folds": fold_rows})
        structural_cross_seed_runs.append({"seed": seed, "pooled": {**binary_metrics(y_structural, s_structural, 0.5), **rank_metrics(y_structural, s_structural)}, "folds": [{"fold": row["fold"], "metrics": row["structuralV4"]} for row in fold_rows]})

    embedding_diagnostics = []
    if memorization_seed47 is not None:
        mean, std = float(images.mean()), max(float(images.std()), 1e-6)
        normalized = torch.from_numpy(((images - mean) / std).astype(np.float32))
        memorization_seed47.eval()
        with torch.no_grad():
            embeddings = memorization_seed47.features(normalized).numpy()
        by_case: dict[str, list[int]] = defaultdict(list)
        for index, record in enumerate(records):
            by_case[record["caseId"]].append(index)
        pairs = [
            ("case_00_train_family_0090_f0100", "case_01_train_family_0090_f0101", "0090 matched"),
            ("case_00_train_family_0090_f0100", "case_12_train_family_0066_f0100", "cross-family ambiguous"),
        ]
        within = []
        for indices in by_case.values():
            for left in range(len(indices)):
                for right in range(left + 1, len(indices)):
                    within.append(float(np.linalg.norm(embeddings[indices[left]] - embeddings[indices[right]])))
        for left, right, name in pairs:
            left_mean = embeddings[by_case[left]].mean(axis=0)
            right_mean = embeddings[by_case[right]].mean(axis=0)
            distance = float(np.linalg.norm(left_mean - right_mean))
            embedding_diagnostics.append({"name": name, "left": left, "right": right, "embeddingDistance": distance, "ratioToWithinPoseMedian": distance / max(float(np.median(within)), 1e-8)})

    memo_stable = median_metric(memorization_runs, "stable", "accuracy")
    repeat_recall = median_metric(repeat_runs, "stable", "recall")
    repeat_fpr = median_metric(repeat_runs, "stable", "falsePositiveRate")
    cnn_balanced = float(np.median([run["pooled"]["balancedAccuracy"] for run in cnn_cross_seed_runs]))
    cnn_recall = float(np.median([run["pooled"]["recall"] for run in cnn_cross_seed_runs]))
    cnn_fpr = float(np.median([run["pooled"]["falsePositiveRate"] for run in cnn_cross_seed_runs]))
    structural_balanced = float(np.median([run["pooled"]["balancedAccuracy"] for run in structural_cross_seed_runs]))
    frozen_balanced = float(frozen_metrics["balancedAccuracy"])
    decision_inputs = {
        "integrityErrors": integrity_errors,
        "medianStableMemorizationAccuracy": memo_stable,
        "medianSamePoseStableRecall": repeat_recall,
        "medianSamePoseStableFalsePositiveRate": repeat_fpr,
        "medianCnnCrossFamilyBalancedAccuracy": cnn_balanced,
        "medianCnnCrossFamilyRecall": cnn_recall,
        "medianCnnCrossFamilyFalsePositiveRate": cnn_fpr,
        "medianStructuralCrossFamilyBalancedAccuracy": structural_balanced,
        "frozenSpatialBalancedAccuracy": frozen_balanced,
        "requiredBalancedAccuracy": max(frozen_balanced, structural_balanced) + 0.05,
    }
    decision, reason = decide_stage_a(decision_inputs)

    report = {
        "schemaVersion": 1,
        "taskId": "G2-R4-A",
        "decision": decision,
        "decisionReason": reason,
        "dependentStages": {"R4B": "ALLOWED" if decision == "PASS" else "BLOCKED", "R4C": "BLOCKED", "R4D": "BLOCKED"},
        "protocol": str(protocol_path.relative_to(repo)).replace("\\", "/"),
        "protocolSha256": sha256(protocol_path),
        "sourceEvidenceSha256": sha256(source_path),
        "frozenCheckpointSha256": sha256(frozen_path),
        "environment": {"torch": torch.__version__, "device": "cpu", "threads": torch.get_num_threads(), "externalPackagesInstalled": False},
        "integrity": {"records": len(records), "poses": len(set(groups)), "families": len(set(item["family"] for item in records)), "stablePoses": len(set(item["caseId"] for item in records if item["stable"])), "unstablePoses": len(set(item["caseId"] for item in records if not item["stable"])), "nativeResolution": [1024, 1024], "inputResolution": [128, 128], "hashErrors": integrity_errors},
        "baselines": {"frozenSpatialV2OnStableRepeats": frozen_metrics, "structuralV4CrossFamily": structural_cross_seed_runs},
        "cnn": {"parameterCount": sum(parameter.numel() for parameter in SmallUltrasoundCNN().parameters()), "memorization": memorization_runs, "samePoseRepeat": repeat_runs, "crossFamily": cnn_cross_seed_runs, "embeddingDiagnostics": embedding_diagnostics},
        "decisionInputs": decision_inputs,
        "limitations": ["All evidence is already-consumed G2-R3-A2 diagnostic data from one simulator anatomy.", "Repeat renders of one pose are correlated, not independent samples.", "R4-A uses no threshold calibration and makes no deployability claim.", "No privileged geometry, score or pose is supplied to either learned model."],
    }
    save_json(output / "g2_r4_a_representation_report.json", report)
    print(json.dumps({"decision": decision, "reason": reason, "parameters": report["cnn"]["parameterCount"], **report["decisionInputs"]}, indent=2))


if __name__ == "__main__":
    main()
