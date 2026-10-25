from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image, ImageDraw

from active2d_data import Active2DFrame
from active2d_stop import frame_stop_feature_matrix_v2, stop_probabilities
from g2_r2_features import feature_width, frame_feature_matrix, normalized_grayscale
from train_phase2_stop import classification_metrics


ACCEPTABLE_SCORE_FLOOR = 0.5912307500839233
EXTRACTORS = ("structural-v4", "temporal-structural-v5-h1", "temporal-structural-v5-h4")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_cases(source_root: Path, manifest: dict[str, Any], roles: set[str]) -> list[Active2DFrame]:
    frames: list[Active2DFrame] = []
    for case in manifest["cases"]:
        if case["role"] not in roles:
            continue
        directory = source_root / case["episodeDirectory"]
        stream_path = directory / "stream_manifest.json"
        annotation_path = directory / "privileged_annotations.json"
        audit_path = directory / "start_audit.txt"
        for path, key in ((stream_path, "streamManifestSha256"), (annotation_path, "privilegedAnnotationsSha256"), (audit_path, "startAuditSha256")):
            if sha256(path) != case[key]:
                raise RuntimeError(f"Frozen evidence hash mismatch: {path}")
        stream = json.loads(stream_path.read_text(encoding="utf-8"))
        annotations = json.loads(annotation_path.read_text(encoding="utf-8"))
        labels = {int(item["index"]): item for item in annotations["frames"]}
        for item in stream["frames"]:
            index = int(item["index"])
            label = labels[index]
            image_relative = str(item["image"]).replace("\\", "/")
            image = source_root / image_relative
            actual = sha256(image)
            if actual != item["imageSha256"]:
                raise RuntimeError(f"Image hash mismatch: {image}")
            frames.append(Active2DFrame(
                image=image,
                image_relative=image_relative,
                image_sha256=actual,
                episode_id=str(stream["episodeId"]),
                dataset_run_id=str(stream["datasetRunId"]),
                split=str(case["role"]),
                family=str(stream["family"]),
                seed=int(stream["seed"]),
                index=index,
                issued_action=str(item.get("issuedAction") or "RESET").upper(),
                action_accepted=bool(item.get("actionAccepted", True)),
                action_completed=bool(item.get("actionCompleted", item.get("actionAccepted", True))),
                episode_reset=bool(item.get("episodeReset", False)),
                level=str(label.get("nearestLevel")),
                t7_distance_mm=float(label.get("t7DistanceMm", math.inf)),
                t7_acceptable=bool(label.get("t7LevelAcceptable", False)),
                target_acceptable=bool(label.get("finalTargetAcceptable", False)),
                target_score=float(label.get("finalTargetScore", 0.0)),
                contact_valid=bool(label.get("contactValid", False)),
                workspace_valid=bool(label.get("workspaceValid", False)),
            ))
    return sorted(frames, key=lambda frame: (frame.episode_id, frame.index))


def standardize(train: np.ndarray, values: dict[str, np.ndarray]) -> tuple[np.ndarray, np.ndarray, dict[str, np.ndarray]]:
    mean = train.mean(axis=0).astype(np.float32)
    scale = train.std(axis=0).astype(np.float32)
    scale[scale < 1e-5] = 1.0
    return mean, scale, {name: ((matrix - mean) / scale).astype(np.float32) for name, matrix in values.items()}


def fit_logistic_sgd(x: np.ndarray, y: np.ndarray, seed: int, *, epochs: int = 80, batch_size: int = 256, alpha: float = 1e-4) -> tuple[np.ndarray, float]:
    rng = np.random.default_rng(seed)
    weights = np.zeros(x.shape[1], dtype=np.float32)
    bias = 0.0
    positive = max(int(y.sum()), 1)
    negative = max(len(y) - positive, 1)
    class_weight = np.where(y > 0.5, len(y) / (2.0 * positive), len(y) / (2.0 * negative)).astype(np.float32)
    for epoch in range(epochs):
        rate = 0.025 / math.sqrt(1.0 + epoch * 0.08)
        permutation = rng.permutation(len(y))
        for start in range(0, len(y), batch_size):
            indices = permutation[start:start + batch_size]
            xb, yb, wb = x[indices], y[indices], class_weight[indices]
            logits = np.clip(xb @ weights + bias, -25.0, 25.0)
            probability = 1.0 / (1.0 + np.exp(-logits))
            error = wb * (probability - yb)
            gradient = xb.T @ error / len(indices) + alpha * weights
            gradient_bias = float(error.mean())
            norm = max(float(np.linalg.norm(gradient)), 1.0)
            weights -= rate * gradient / norm
            bias -= rate * max(-1.0, min(1.0, gradient_bias))
    return weights, float(bias)


def fit_quality_sgd(x: np.ndarray, y: np.ndarray, seed: int, *, epochs: int = 100, batch_size: int = 256, alpha: float = 5e-4) -> tuple[np.ndarray, float]:
    rng = np.random.default_rng(seed + 1000)
    weights = np.zeros(x.shape[1], dtype=np.float32)
    bias = float(y.mean())
    for epoch in range(epochs):
        rate = 0.012 / math.sqrt(1.0 + epoch * 0.08)
        permutation = rng.permutation(len(y))
        for start in range(0, len(y), batch_size):
            indices = permutation[start:start + batch_size]
            error = np.clip(x[indices] @ weights + bias - y[indices], -0.25, 0.25)
            gradient = x[indices].T @ error / len(indices) + alpha * weights
            norm = max(float(np.linalg.norm(gradient)), 1.0)
            weights -= rate * gradient / norm
            bias -= rate * float(error.mean())
    return weights, float(bias)


def probability(x: np.ndarray, weights: np.ndarray, bias: float) -> np.ndarray:
    logits = np.clip(x @ weights + bias, -30.0, 30.0)
    return 1.0 / (1.0 + np.exp(-logits))


def rankdata(values: np.ndarray) -> np.ndarray:
    order = np.argsort(values, kind="mergesort")
    ranks = np.empty(len(values), dtype=np.float64)
    start = 0
    while start < len(values):
        end = start + 1
        while end < len(values) and values[order[end]] == values[order[start]]:
            end += 1
        ranks[order[start:end]] = 0.5 * (start + end - 1)
        start = end
    return ranks


def spearman(left: np.ndarray, right: np.ndarray) -> float:
    if len(left) < 2:
        return 0.0
    a, b = rankdata(left), rankdata(right)
    if np.std(a) < 1e-12 or np.std(b) < 1e-12:
        return 0.0
    return float(np.corrcoef(a, b)[0, 1])


def wilson(successes: int, total: int) -> list[float] | None:
    if total <= 0:
        return None
    z = 1.959963984540054
    p = successes / total
    denominator = 1.0 + z * z / total
    center = (p + z * z / (2.0 * total)) / denominator
    margin = z * math.sqrt(p * (1.0 - p) / total + z * z / (4.0 * total * total)) / denominator
    return [max(0.0, center - margin), min(1.0, center + margin)]


def select_threshold(labels: np.ndarray, scores: np.ndarray) -> tuple[float | None, dict[str, Any]]:
    negatives = scores[~labels]
    threshold = float(np.nextafter(np.float32(negatives.max()), np.float32(np.inf))) if len(negatives) else 0.0
    metrics = classification_metrics(labels, scores, threshold)
    safe = metrics["falsePositive"] == 0 and metrics["recall"] >= 0.80
    return (threshold if safe else None), {
        "status": "SAFE_OPERATING_POINT" if safe else "NO_SAFE_OPERATING_POINT",
        "diagnosticThreshold": threshold,
        "metrics": metrics,
    }


def evaluate(frames: list[Active2DFrame], stop: np.ndarray, quality: np.ndarray, threshold: float) -> dict[str, Any]:
    labels = np.asarray([frame.target_acceptable for frame in frames], dtype=bool)
    classification = classification_metrics(labels, stop, threshold)
    groups: dict[str, list[int]] = defaultdict(list)
    for index, frame in enumerate(frames):
        groups[frame.family].append(index)
    family_results: dict[str, Any] = {}
    regrets: list[float] = []
    observed = recognized = wrong = abstained = 0
    for family, indices in groups.items():
        positive = int(labels[indices].sum())
        predicted = [index for index in indices if stop[index] >= threshold]
        target_observed = positive > 0
        target_recognized = target_observed and any(labels[index] for index in predicted)
        wrong_stop = any(not labels[index] for index in predicted)
        actual_quality = np.asarray([frames[index].target_score for index in indices])
        predicted_quality = quality[indices]
        regret = float(actual_quality.max() - actual_quality[int(np.argmax(predicted_quality))])
        observed += int(target_observed)
        recognized += int(target_recognized)
        wrong += int(wrong_stop)
        abstained += int(not predicted)
        regrets.append(regret)
        family_results[family] = {
            "frames": len(indices), "positiveFrames": positive,
            "truePositive": int(sum(labels[index] and stop[index] >= threshold for index in indices)),
            "falsePositive": int(sum((not labels[index]) and stop[index] >= threshold for index in indices)),
            "targetObserved": target_observed, "targetRecognized": target_recognized,
            "wrongStop": wrong_stop, "abstained": not predicted,
            "maximumStopScore": float(stop[indices].max()), "bestViewRegret": regret,
        }
    return {
        "classification": classification,
        "recallWilson95": wilson(classification["truePositive"], classification["positiveSupport"]),
        "qualitySpearman": spearman(np.asarray([frame.target_score for frame in frames]), quality),
        "medianEpisodeBestViewRegret": float(np.median(regrets)),
        "families": len(groups), "positiveFamilies": observed,
        "targetObservedFamilies": observed, "targetRecognizedFamilies": recognized,
        "conditionalTargetRecognition": recognized / observed if observed else 0.0,
        "wrongStopFamilies": wrong, "abstentionRate": abstained / len(groups),
        "familyResults": family_results,
    }


def gate_pass(metrics: dict[str, Any], threshold: float | None) -> bool:
    classification = metrics["classification"]
    return bool(
        threshold is not None
        and metrics["positiveFamilies"] >= 5
        and classification["recall"] >= 0.80
        and classification["falsePositive"] == 0
        and metrics["wrongStopFamilies"] == 0
        and metrics["qualitySpearman"] >= 0.60
        and metrics["medianEpisodeBestViewRegret"] <= 0.015
    )


def label_audit(frames: list[Active2DFrame]) -> dict[str, Any]:
    mismatch = [frame for frame in frames if frame.target_acceptable != (frame.target_score >= ACCEPTABLE_SCORE_FLOOR)]
    groups: dict[str, list[Active2DFrame]] = defaultdict(list)
    for frame in frames:
        groups[frame.family].append(frame)
    transitions = 0
    boundary = []
    for family_frames in groups.values():
        ordered = sorted(family_frames, key=lambda frame: frame.index)
        transitions += sum(left.target_acceptable != right.target_acceptable for left, right in zip(ordered, ordered[1:]))
    for frame in frames:
        if abs(frame.target_score - ACCEPTABLE_SCORE_FLOOR) <= 0.005:
            boundary.append({"family": frame.family, "index": frame.index, "label": frame.target_acceptable, "score": frame.target_score})
    return {
        "acceptableScoreFloor": ACCEPTABLE_SCORE_FLOOR,
        "labelScoreMismatch": len(mismatch),
        "neighborLabelTransitions": transitions,
        "nearBoundaryFrames": len(boundary),
        "nearBoundaryExamples": boundary[:30],
        "finding": "labels exactly follow the frozen score floor" if not mismatch else "label/score inconsistency detected",
    }


def nearest_opposite_diagnostics(frames: list[Active2DFrame]) -> tuple[dict[str, Any], list[tuple[Active2DFrame, Active2DFrame]]]:
    positives = [frame for frame in frames if frame.target_acceptable]
    negatives = [frame for frame in frames if not frame.target_acceptable]
    resolutions = (16, 32, 64, 128)
    caches = {size: {frame.image_sha256: normalized_grayscale(frame.image, size).reshape(-1) for frame in frames} for size in resolutions}
    per_resolution: dict[str, Any] = {}
    pairs: list[tuple[Active2DFrame, Active2DFrame]] = []
    for size in resolutions:
        distances = []
        local_pairs = []
        negative_matrix = np.stack([caches[size][frame.image_sha256] for frame in negatives])
        for positive in positives:
            delta = negative_matrix - caches[size][positive.image_sha256]
            rms = np.sqrt(np.mean(delta * delta, axis=1))
            nearest = int(np.argmin(rms))
            distances.append(float(rms[nearest]))
            local_pairs.append((positive, negatives[nearest]))
        per_resolution[str(size)] = {
            "positiveFrames": len(positives),
            "nearestOppositeRmsMin": float(np.min(distances)),
            "nearestOppositeRmsMedian": float(np.median(distances)),
            "nearestOppositeRmsMax": float(np.max(distances)),
        }
        if size == 128:
            pairs = local_pairs
    original_distances = []
    for positive, negative in pairs:
        with Image.open(positive.image) as image:
            positive_pixels = np.asarray(image.convert("L"), dtype=np.float32) / 255.0
        with Image.open(negative.image) as image:
            negative_pixels = np.asarray(image.convert("L"), dtype=np.float32) / 255.0
        original_distances.append(float(np.sqrt(np.mean((positive_pixels - negative_pixels) ** 2))))
    per_resolution["original1024_for_128_nearest"] = {
        "positiveFrames": len(original_distances),
        "nearestOppositeRmsMin": float(np.min(original_distances)),
        "nearestOppositeRmsMedian": float(np.median(original_distances)),
        "nearestOppositeRmsMax": float(np.max(original_distances)),
        "note": "RMS at original resolution for the negative selected as nearest at 128x128",
    }
    return {"resolutions": per_resolution}, pairs


def montage(pairs: list[tuple[Active2DFrame, Active2DFrame]], destination: Path) -> None:
    selected: list[tuple[Active2DFrame, Active2DFrame]] = []
    seen: set[str] = set()
    for positive, negative in pairs:
        if positive.family in seen:
            continue
        selected.append((positive, negative)); seen.add(positive.family)
    tile, label_height = 128, 36
    headings = ["POS original", "POS 128", "POS 64", "POS 32", "POS 16", "NEG original", "NEG 128", "NEG 64", "NEG 32", "NEG 16"]
    canvas = Image.new("RGB", (len(headings) * tile, label_height + len(selected) * (tile + label_height)), "white")
    draw = ImageDraw.Draw(canvas)
    for column, heading in enumerate(headings):
        draw.text((column * tile + 3, 10), heading, fill="black")
    for row, (positive, negative) in enumerate(selected):
        top = label_height + row * (tile + label_height)
        images = []
        for frame in (positive, negative):
            with Image.open(frame.image) as source:
                gray = source.convert("L")
                images.append(gray.resize((tile, tile), Image.Resampling.BILINEAR))
                for size in (128, 64, 32, 16):
                    images.append(gray.resize((size, size), Image.Resampling.BILINEAR).resize((tile, tile), Image.Resampling.NEAREST))
        for column, image in enumerate(images):
            canvas.paste(image.convert("RGB"), (column * tile, top))
        draw.text((3, top + tile + 2), f"{positive.family} #{positive.index} score={positive.target_score:.6f}", fill="black")
        draw.text((5 * tile + 3, top + tile + 2), f"{negative.family} #{negative.index} score={negative.target_score:.6f}", fill="black")
    destination.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(destination)


def checkpoint_payload(candidate: dict[str, Any], manifest: dict[str, Any], threshold: float | None, deployable: bool) -> dict[str, Any]:
    width = len(candidate["mean"])
    return {
        "schema": "active2d-image-only-stop-v1", "version": 3,
        "contract": "active2d_image_only_v1", "experimentId": "thoracic_active2d_image_only_v1_20261023",
        "policyInputs": ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
        "forbiddenInputs": ["UnityAnatomyLabels", "probePose", "targetCoordinates", "finalTargetScore"],
        "architecture": {
            "type": "standardized-temporal-quality-stop-v1",
            "featureExtractor": candidate["extractor"], "featureWidth": width,
            "featureMean": candidate["mean"].tolist(), "featureScale": candidate["scale"].tolist(),
            "stopWeights": candidate["stopWeights"].tolist(), "stopBias": candidate["stopBias"],
            "qualityWeights": candidate["qualityWeights"].tolist(), "qualityBias": candidate["qualityBias"],
        },
        "stopRule": {"threshold": threshold if deployable else None, "requiredConfirmations": 1},
        "deployment": {
            "gate": "g2-stop-safety-v1", "status": "DEPLOYABLE" if deployable else "BLOCKED",
            "safeOperatingPoint": deployable,
            "reason": "g2-r2-independent-family-gate-pass" if deployable else "g2-r2-independent-family-gate-failed",
        },
        "training": {
            "taskId": "G2-R2", "seed": candidate["seed"],
            "manifestDatasetFingerprint": manifest["datasetFingerprint"],
            "selectionProtocol": "screen seed13; require selected extractor to pass seeds 13/29/47; freeze seed29",
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    output = args.output_directory.resolve(); output.mkdir(parents=True, exist_ok=True)
    train = load_cases(args.source_root.resolve(), manifest, {"train"})
    calibration = load_cases(args.source_root.resolve(), manifest, {"calibration"})
    roles = {"train": train, "calibration": calibration}
    label_report = label_audit(train + calibration)
    ambiguity, pairs = nearest_opposite_diagnostics(calibration)
    montage(pairs, output / "g2_r2_ambiguity_multiresolution.png")
    (output / "g2_r2_label_observability_audit.json").write_text(
        json.dumps({"labelAudit": label_report, "oppositeLabelAmbiguity": ambiguity}, indent=2) + "\n", encoding="utf-8"
    )

    y = {role: np.asarray([frame.target_acceptable for frame in values], dtype=np.float32) for role, values in roles.items()}
    quality_target = {role: np.asarray([frame.target_score for frame in values], dtype=np.float32) for role, values in roles.items()}
    feature_cache: dict[str, dict[str, np.ndarray]] = {}
    candidate_rows: list[dict[str, Any]] = []
    fitted: dict[tuple[str, int], dict[str, Any]] = {}

    def fit(extractor: str, seed: int) -> dict[str, Any]:
        if extractor not in feature_cache:
            matrices = {role: frame_feature_matrix(values, extractor) for role, values in roles.items()}
            mean, scale, standardized = standardize(matrices["train"], matrices)
            feature_cache[extractor] = {**standardized, "mean": mean, "scale": scale}
        cache = feature_cache[extractor]
        stop_weights, stop_bias = fit_logistic_sgd(cache["train"], y["train"], seed)
        quality_weights, quality_bias = fit_quality_sgd(cache["train"], quality_target["train"], seed)
        calibration_stop = probability(cache["calibration"], stop_weights, stop_bias)
        calibration_quality = np.clip(cache["calibration"] @ quality_weights + quality_bias, 0.0, 1.0)
        threshold, selection = select_threshold(y["calibration"].astype(bool), calibration_stop)
        evaluation_threshold = threshold if threshold is not None else float(selection["diagnosticThreshold"])
        metrics = evaluate(calibration, calibration_stop, calibration_quality, evaluation_threshold)
        result = {
            "extractor": extractor, "seed": seed, "featureWidth": feature_width(extractor),
            "threshold": threshold, "thresholdSelection": selection,
            "calibration": metrics, "gatePass": gate_pass(metrics, threshold),
            "mean": cache["mean"], "scale": cache["scale"],
            "stopWeights": stop_weights, "stopBias": stop_bias,
            "qualityWeights": quality_weights, "qualityBias": quality_bias,
        }
        fitted[(extractor, seed)] = result
        candidate_rows.append({key: value for key, value in result.items() if key not in {"mean", "scale", "stopWeights", "qualityWeights"}})
        return result

    screen = [fit(extractor, 13) for extractor in EXTRACTORS]
    ranking = sorted(screen, key=lambda item: (
        item["calibration"]["classification"]["falsePositive"] == 0,
        item["calibration"]["classification"]["recall"],
        item["calibration"]["qualitySpearman"],
        -item["calibration"]["medianEpisodeBestViewRegret"],
    ), reverse=True)
    promising = [item["extractor"] for item in ranking[:2]]
    for extractor in promising:
        fit(extractor, 29); fit(extractor, 47)
    robust = [
        extractor for extractor in promising
        if all(fitted[(extractor, seed)]["gatePass"] for seed in (13, 29, 47))
    ]
    selected = fitted[(robust[0], 29)] if robust else ranking[0]
    calibration_pass = bool(robust)

    baseline = json.loads(args.baseline.read_text(encoding="utf-8"))
    baseline_scores = stop_probabilities(baseline, frame_stop_feature_matrix_v2(calibration))
    baseline_metrics = evaluate(calibration, baseline_scores, baseline_scores, 0.93)

    locked_status: dict[str, Any] = {"status": "SEALED", "reason": "calibration gate failed"}
    final_pass = False
    if calibration_pass:
        locked = load_cases(args.source_root.resolve(), manifest, {"locked_test"})
        locked_matrix = frame_feature_matrix(locked, selected["extractor"])
        locked_z = (locked_matrix - selected["mean"]) / selected["scale"]
        locked_stop = probability(locked_z, selected["stopWeights"], selected["stopBias"])
        locked_quality = np.clip(locked_z @ selected["qualityWeights"] + selected["qualityBias"], 0.0, 1.0)
        locked_metrics = evaluate(locked, locked_stop, locked_quality, float(selected["threshold"]))
        final_pass = gate_pass(locked_metrics, selected["threshold"])
        locked_status = {"status": "PASS" if final_pass else "FAIL", "openedOnce": True, "metrics": locked_metrics}

    checkpoint = checkpoint_payload(selected, manifest, selected["threshold"], final_pass)
    checkpoint_path = output / "g2_r2_structural_temporal_candidate_v3.json"
    checkpoint_path.write_text(json.dumps(checkpoint, indent=2) + "\n", encoding="utf-8")
    report = {
        "schemaVersion": 1, "taskId": "G2-R2",
        "status": "OFFLINE_PASS" if final_pass else "OFFLINE_BLOCKED",
        "baseCommit": manifest["baseCommit"],
        "manifest": args.manifest.as_posix(), "manifestSha256": sha256(args.manifest),
        "datasetFingerprint": manifest["datasetFingerprint"],
        "sourceImagesCopied": False,
        "support": {
            role: {"frames": len(values), "families": len({frame.family for frame in values}),
                   "positiveFrames": int(y[role].sum()),
                   "positiveFamilies": len({frame.family for frame in values if frame.target_acceptable})}
            for role, values in roles.items()
        },
        "predeclaredCandidateBudget": manifest["candidateBudget"],
        "baseline": {"name": "frozen-spatial-v2", "threshold": 0.93, "thresholdChanged": False,
                     "checkpointSha256": sha256(args.baseline), "calibration": baseline_metrics},
        "candidateResults": candidate_rows,
        "promisingExtractors": promising,
        "robustAcrossSeeds": robust,
        "selected": {key: value for key, value in selected.items() if key not in {"mean", "scale", "stopWeights", "qualityWeights"}},
        "calibrationGatePass": calibration_pass,
        "lockedTest": locked_status,
        "checkpoint": checkpoint_path.name, "checkpointSha256": sha256(checkpoint_path),
        "checkpointDeployable": final_pass,
        "unityClosedLoopEligible": final_pass,
        "labelAudit": label_report,
        "observability": ambiguity,
        "limitations": [
            "All images come from one simulated anatomy; no clinical or unseen-patient claim.",
            "Calibration includes source test-split families 0079/0094 reassigned before fitting; the six historical locked-test families remain separate.",
            "Offline image recognition does not establish physical return, fresh confirmation, or explicit STOP success.",
        ],
    }
    report_path = output / "g2_r2_results.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": report["status"], "calibrationPass": calibration_pass,
        "lockedTest": locked_status["status"], "selected": selected["extractor"],
        "checkpoint": str(checkpoint_path), "report": str(report_path),
    }, indent=2))


if __name__ == "__main__":
    main()
