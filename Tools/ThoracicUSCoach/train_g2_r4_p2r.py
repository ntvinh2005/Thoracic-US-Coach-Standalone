from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import sys
import time
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image, ImageDraw
import psutil
import torch

from g2_r4_p2_lineage import PROTOCOL as HISTORICAL_PROTOCOL, collect, read_json, sha256, write_json
from g2_r4_p2r_lineage import OUTPUT, PROTOCOL, build as build_lineage
from train_g2_r4_p2 import (
    ImageStore,
    StructuralTwoHead,
    UltrasoundTwoHead,
    binary_metrics,
    evaluate,
    predict_image,
    predict_structural,
    quality_metrics,
    set_seed,
    structural_matrix,
    threshold_zero_fp,
    train_image_model,
    train_structural,
)


def split_indices(
    records: list[dict[str, Any]], evaluation: set[str], validation: set[str]
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    evaluation_indices = np.asarray([
        index for index, row in enumerate(records)
        if row["countsAsIndependentP1"] and row["spatialGroup"] in evaluation
    ], dtype=np.int64)
    validation_indices = np.asarray([
        index for index, row in enumerate(records)
        if row["countsAsIndependentP1"] and row["spatialGroup"] in validation
    ], dtype=np.int64)
    training_indices = np.asarray([
        index for index, row in enumerate(records)
        if row["spatialGroup"] not in evaluation and row["spatialGroup"] not in validation
    ], dtype=np.int64)
    return training_indices, validation_indices, evaluation_indices


def false_positive_sensitivity(labels: np.ndarray, scores: np.ndarray) -> list[dict[str, Any]]:
    rows = []
    for budget in (0, 1, 5, 10, 25, 50):
        best = {"falsePositiveBudget": budget, "recall": 0.0, "falsePositive": 0, "threshold": 1.0}
        for threshold in sorted(set(float(value) for value in scores), reverse=True):
            metrics = binary_metrics(labels, scores, threshold)
            if int(metrics["falsePositive"]) <= budget and float(metrics["recall"] or 0.0) >= best["recall"]:
                best = {
                    "falsePositiveBudget": budget,
                    "recall": float(metrics["recall"] or 0.0),
                    "falsePositive": int(metrics["falsePositive"]),
                    "threshold": threshold,
                }
        rows.append(best)
    return rows


def selected_error_analysis(
    records: list[dict[str, Any]], stop_scores: np.ndarray, quality_scores: np.ndarray, threshold: float
) -> dict[str, Any]:
    labels = np.asarray([row["target"] for row in records], dtype=bool)
    negative_indices = np.flatnonzero(~labels)
    positive_indices = np.flatnonzero(labels)
    highest_negative = sorted(negative_indices, key=lambda index: float(stop_scores[index]), reverse=True)[:20]
    lowest_positive = sorted(positive_indices, key=lambda index: float(stop_scores[index]))[:20]
    maximum_positive = float(np.max(stop_scores[positive_indices]))
    negatives_above_every_positive = [index for index in negative_indices if float(stop_scores[index]) > maximum_positive]

    def diagnostic(index: int) -> dict[str, Any]:
        row = records[index]
        return {
            "caseId": row["caseId"], "spatialGroup": row["spatialGroup"], "sourceBaseGroup": row["sourceBaseGroup"],
            "frameIndex": row["index"], "image": row["image"], "imageSha256": row["imageSha256"],
            "target": row["target"], "unityScore": row["score"], "margin": row["margin"],
            "stopProbability": float(stop_scores[index]), "qualityPrediction": float(quality_scores[index]),
            "nearBoundary": abs(float(row["margin"])) <= 0.015,
        }

    by_case: dict[str, list[int]] = defaultdict(list)
    for index, row in enumerate(records):
        by_case[row["caseId"]].append(index)
    positive_case_records = [row for row in records if any(records[index]["target"] for index in by_case[row["caseId"]])]
    positive_case_quality = quality_metrics(positive_case_records, np.asarray([
        quality_scores[records.index(row)] for row in positive_case_records
    ]))
    far = np.asarray([abs(float(row["margin"])) > 0.015 for row in records], dtype=bool)
    near = ~far
    return {
        "highestProbabilityNegatives": [diagnostic(index) for index in highest_negative],
        "lowestProbabilityPositives": [diagnostic(index) for index in lowest_positive],
        "negativeFramesAboveEveryPositive": len(negatives_above_every_positive),
        "negativeFramesAboveEveryPositiveExamples": [diagnostic(index) for index in negatives_above_every_positive[:20]],
        "falsePositiveSensitivityAnalysisOnly": false_positive_sensitivity(labels, stop_scores),
        "nearBoundaryAtCalibratedThreshold": binary_metrics(labels[near], stop_scores[near], threshold),
        "farFromBoundaryAtCalibratedThreshold": binary_metrics(labels[far], stop_scores[far], threshold),
        "qualityAllTrajectories": quality_metrics(records, quality_scores),
        "qualityPositiveTrajectories": positive_case_quality,
    }


def write_contact_sheet(repo: Path, rows: list[dict[str, Any]], output: Path) -> None:
    selected = rows[:8]
    width, height = 256, 286
    sheet = Image.new("RGB", (4 * width, 2 * height), "black")
    draw = ImageDraw.Draw(sheet)
    for slot, row in enumerate(selected):
        with Image.open(repo / row["image"]) as image:
            tile = image.convert("RGB").resize((width, 256), Image.Resampling.BILINEAR)
        x, y = (slot % 4) * width, (slot // 4) * height
        sheet.paste(tile, (x, y))
        draw.text((x + 4, y + 258), f"{row['caseId']} f{row['frameIndex']} p={row['stopProbability']:.3f} m={row['margin']:+.3f}", fill="white")
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    repo = args.repo.resolve()
    torch.set_num_threads(min(4, max(1, torch.get_num_threads())))
    started = time.perf_counter()
    process = psutil.Process()
    peak_rss = process.memory_info().rss
    output = repo / OUTPUT
    protocol_path = repo / PROTOCOL
    protocol = read_json(protocol_path)
    lineage = build_lineage(repo)
    if lineage["integrity"]["status"] != "PASS" or lineage["primaryPolicy"]["dataSufficiency"] != "PASS":
        write_json(output / "r4_entry_gate.json", {"status": "BLOCKED", "lineage": lineage["integrity"], "dataSufficiency": lineage["primaryPolicy"]["dataSufficiency"]})
        raise SystemExit("R4 entry gate blocked")
    expected_masks = protocol["causalHistory"]["availabilityMasksFirstFour"]
    synthetic = [{"caseId": "preflight", "index": index, "episodeReset": index == 0} for index in range(4)]
    _, observed_masks = ImageStore._history_layout(synthetic, 4)
    if observed_masks.astype(int).tolist() != expected_masks:
        raise SystemExit("R2 causal history preflight failed")
    write_json(output / "r4_entry_gate.json", {
        "status": "PASS", "r1Integrity": "PASS", "r1DataSufficiency": "PASS",
        "r2CausalMasks": observed_masks.astype(int).tolist(), "evaluatorFrozen": True,
        "actorContract": "image-only causal history plus policy-owned issued/executed feedback",
    })

    historical_protocol = read_json(repo / HISTORICAL_PROTOCOL)
    _, records, errors = collect(repo, historical_protocol)
    if errors:
        raise SystemExit("; ".join(errors))
    case_to_component = lineage["primaryPolicy"]["caseToComponent"]
    for row in records:
        row["spatialGroup"] = case_to_component[row["caseId"]]
    training_config = protocol["correctiveRerun"]
    compatible_protocol = {"training": {
        "batchSize": training_config["batchSize"], "learningRate": training_config["learningRate"],
        "weightDecay": training_config["weightDecay"], "maximumEpochs": training_config["maximumEpochs"],
    }}
    store = ImageStore(repo, records, int(training_config["imageSize"][0]))
    structural = structural_matrix(repo, records)
    peak_rss = max(peak_rss, process.memory_info().rss)
    primary_indices = np.asarray([index for index, row in enumerate(records) if row["countsAsIndependentP1"]], dtype=np.int64)
    primary_records = [records[index] for index in primary_indices]
    labels_primary = np.asarray([row["target"] for row in primary_records], dtype=bool)
    primary_position = {record_index: position for position, record_index in enumerate(primary_indices)}

    sys.path.insert(0, str(repo / "Tools/ThoracicUSCoach"))
    from active2d_stop import request_stop_features, stop_probabilities
    frozen_path = repo / "Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_stop_live_spatial_logistic_v1.json"
    frozen = read_json(frozen_path)
    frozen_all = np.asarray([
        float(stop_probabilities(frozen, request_stop_features(repo / row["image"], None, row["executedAction"], "spatial-v2"))[0])
        for row in records
    ])
    frozen_primary = frozen_all[primary_indices]
    frozen_metrics = evaluate(primary_records, frozen_primary, frozen_primary, 0.93)

    seeds = [int(seed) for seed in training_config["seeds"]]
    model_types = {"cnn_single_frame": False, "cnn_causal_multiview_h4_corrected": True}
    run_rows: dict[str, list[dict[str, Any]]] = {name: [] for name in (*model_types, "structural_v4_equivalent")}
    oof = {
        name: {seed: {"stop": np.full(len(records), np.nan), "quality": np.full(len(records), np.nan), "fold": np.full(len(records), -1)} for seed in seeds}
        for name in run_rows
    }
    best_epochs = {name: {seed: [] for seed in seeds} for name in run_rows}
    ledger_rows: list[dict[str, Any]] = []

    mixed = read_json(repo / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_revised_20261025/g2_r4_p1_capture_summary.json")["coverage"]["mixedLabelGroups"]
    unstable = {(item["caseId"], int(index)) for item in mixed for index in item["indices"]}

    for seed in seeds:
        for fold_info in lineage["primaryPolicy"]["folds"]:
            fold = int(fold_info["fold"])
            train_indices, validation_indices, evaluation_indices = split_indices(
                records, set(fold_info["evaluationGroups"]), set(fold_info["innerValidationGroups"])
            )
            evaluation_records = [records[index] for index in evaluation_indices]
            structural_model, budget, stats = train_structural(structural, records, train_indices, validation_indices, seed, compatible_protocol)
            stop, quality = predict_structural(structural_model, structural, evaluation_indices, stats)
            oof["structural_v4_equivalent"][seed]["stop"][evaluation_indices] = stop
            oof["structural_v4_equivalent"][seed]["quality"][evaluation_indices] = quality
            oof["structural_v4_equivalent"][seed]["fold"][evaluation_indices] = fold
            best_epochs["structural_v4_equivalent"][seed].append(int(budget["bestEpoch"]))
            run_rows["structural_v4_equivalent"].append({
                "seed": seed, "fold": fold, "fitGroups": fold_info["fitGroups"], "innerValidationGroups": fold_info["innerValidationGroups"],
                "evaluationGroups": fold_info["evaluationGroups"], "connectedAuxiliaryExcluded": fold_info["connectedAuxiliaryExcludedFromTraining"],
                "trainFrames": len(train_indices), "validationFrames": len(validation_indices), "evaluationFrames": len(evaluation_indices),
                "budget": budget, "metricsAtPointFive": evaluate(evaluation_records, stop, quality, 0.5),
            })
            for name, multiview in model_types.items():
                model, budget, stats = train_image_model(
                    store, records, train_indices, validation_indices,
                    seed=seed, multiview=multiview, protocol=compatible_protocol,
                )
                stop, quality = predict_image(model, store, records, evaluation_indices, stats, multiview)
                oof[name][seed]["stop"][evaluation_indices] = stop
                oof[name][seed]["quality"][evaluation_indices] = quality
                oof[name][seed]["fold"][evaluation_indices] = fold
                best_epochs[name][seed].append(int(budget["bestEpoch"]))
                run_rows[name].append({
                    "seed": seed, "fold": fold, "fitGroups": fold_info["fitGroups"], "innerValidationGroups": fold_info["innerValidationGroups"],
                    "evaluationGroups": fold_info["evaluationGroups"], "connectedAuxiliaryExcluded": fold_info["connectedAuxiliaryExcludedFromTraining"],
                    "trainFrames": len(train_indices), "validationFrames": len(validation_indices), "evaluationFrames": len(evaluation_indices),
                    "budget": budget, "metricsAtPointFive": evaluate(evaluation_records, stop, quality, 0.5),
                })
                peak_rss = max(peak_rss, process.memory_info().rss)

    summaries: dict[str, Any] = {}
    ensemble: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for name in run_rows:
        per_seed, stop_rows, quality_rows = [], [], []
        for seed in seeds:
            stop = oof[name][seed]["stop"][primary_indices]
            quality = oof[name][seed]["quality"][primary_indices]
            if np.isnan(stop).any() or np.isnan(quality).any():
                raise RuntimeError(f"OOF coverage missing for {name}/{seed}")
            stop_rows.append(stop); quality_rows.append(quality)
            per_seed.append({"seed": seed, "bestEpochs": best_epochs[name][seed], "metricsAtPointFive": evaluate(primary_records, stop, quality, 0.5)})
            for original_index in primary_indices:
                position = primary_position[int(original_index)]
                row = records[int(original_index)]
                ledger_rows.append({
                    "model": name, "seed": seed, "outerFold": int(oof[name][seed]["fold"][original_index]),
                    "trajectory": row["caseId"], "spatialGroup": row["spatialGroup"], "sourceBase": row["sourceBaseGroup"],
                    "frameIndex": row["index"], "image": row["image"], "imageSha256": row["imageSha256"],
                    "target": row["target"], "unityScore": row["score"], "margin": row["margin"],
                    "stopProbability": float(stop[position]), "qualityPrediction": float(quality[position]),
                    "diagnosticDecisionAtPointFive": bool(stop[position] >= 0.5), "calibrationDecision": None,
                    "historyAvailability": store.history_available[original_index].astype(int).tolist() if name.endswith("corrected") else [0, 0, 0, 1],
                    "issuedAction": row["issuedAction"], "executedAction": row["executedAction"],
                    "actionAccepted": row["actionAccepted"], "actionCompleted": row["actionCompleted"], "episodeReset": row["episodeReset"],
                    "nearBoundary": abs(float(row["margin"])) <= 0.015, "labelUnstableAtRepeatedPose": (row["caseId"], int(row["index"])) in unstable,
                    "privilegedColumnsUsedAsInferenceInput": False,
                })
        mean_stop, mean_quality = np.mean(stop_rows, axis=0), np.mean(quality_rows, axis=0)
        ensemble[name] = (mean_stop, mean_quality)
        summaries[name] = {
            "perSeed": per_seed,
            "ensembleAtPointFive": evaluate(primary_records, mean_stop, mean_quality, 0.5),
            "medianSeedGroupMacroAveragePrecision": float(np.median([item["metricsAtPointFive"]["group"]["sourceGroupMacroAveragePrecision"] for item in per_seed])),
            "minimumSeedAveragePrecision": float(min(item["metricsAtPointFive"]["frame"]["averagePrecision"] for item in per_seed)),
        }

    single_ap = summaries["cnn_single_frame"]["medianSeedGroupMacroAveragePrecision"]
    temporal_ap = summaries["cnn_causal_multiview_h4_corrected"]["medianSeedGroupMacroAveragePrecision"]
    selected = "cnn_causal_multiview_h4_corrected" if temporal_ap >= single_ap + 0.02 else "cnn_single_frame"
    selected_stop, selected_quality = ensemble[selected]
    threshold = threshold_zero_fp(labels_primary, selected_stop)
    calibrated = evaluate(primary_records, selected_stop, selected_quality, threshold)
    positive_groups = int(calibrated["group"]["positiveGroups"])
    eligible = bool(
        summaries[selected]["minimumSeedAveragePrecision"] >= 0.50
        and int(calibrated["frame"]["falsePositive"]) == 0
        and float(calibrated["frame"]["recall"] or 0.0) >= 0.70
        and int(calibrated["group"]["recognizedPositiveGroups"]) >= max(1, positive_groups - 1)
        and float(calibrated["quality"]["spearman"] or -1.0) >= 0.60
        and float(calibrated["quality"]["medianEpisodeBestViewRegret"] or math.inf) <= 0.015
    )
    for position, original_index in enumerate(primary_indices):
        row = records[int(original_index)]
        fold = next(int(item["fold"]) for item in lineage["primaryPolicy"]["folds"] if row["spatialGroup"] in item["evaluationGroups"])
        ledger_rows.append({
            "model": selected + "_ensemble", "seed": "ensemble", "outerFold": fold,
            "trajectory": row["caseId"], "spatialGroup": row["spatialGroup"], "sourceBase": row["sourceBaseGroup"],
            "frameIndex": row["index"], "image": row["image"], "imageSha256": row["imageSha256"],
            "target": row["target"], "unityScore": row["score"], "margin": row["margin"],
            "stopProbability": float(selected_stop[position]), "qualityPrediction": float(selected_quality[position]),
            "diagnosticDecisionAtPointFive": bool(selected_stop[position] >= 0.5),
            "calibrationDecision": bool(selected_stop[position] >= threshold),
            "historyAvailability": store.history_available[original_index].astype(int).tolist() if selected.endswith("corrected") else [0, 0, 0, 1],
            "issuedAction": row["issuedAction"], "executedAction": row["executedAction"],
            "actionAccepted": row["actionAccepted"], "actionCompleted": row["actionCompleted"], "episodeReset": row["episodeReset"],
            "nearBoundary": abs(float(row["margin"])) <= 0.015, "labelUnstableAtRepeatedPose": (row["caseId"], int(row["index"])) in unstable,
            "privilegedColumnsUsedAsInferenceInput": False,
        })
    ledger_path = output / "r3_corrected_oof_prediction_ledger.jsonl"
    with ledger_path.open("w", encoding="utf-8") as handle:
        for row in ledger_rows:
            handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")

    diagnostics = selected_error_analysis(primary_records, selected_stop, selected_quality, threshold)
    write_json(output / "r3_failure_analysis.json", diagnostics)
    write_contact_sheet(repo, diagnostics["highestProbabilityNegatives"], output / "r3_high_risk_negatives.png")
    write_contact_sheet(repo, diagnostics["lowestProbabilityPositives"], output / "r3_missed_positives.png")

    all_indices = np.arange(len(records), dtype=np.int64)
    final_models = []
    for seed in seeds:
        epochs = max(1, int(round(float(np.median(best_epochs[selected][seed])))))
        model, budget, stats = train_image_model(
            store, records, all_indices, all_indices[:1], seed=seed,
            multiview=model_types[selected], protocol=compatible_protocol, fixed_epochs=epochs,
        )
        final_models.append({"seed": seed, "epochs": epochs, "stats": stats, "stateDict": {key: value.detach().cpu() for key, value in model.state_dict().items()}})
    checkpoint_path = output / "g2_r4_p2r_candidate.pt"
    history_contract = "causal-h4-padding-mask-v2" if selected.endswith("corrected") else "single-frame-v1"
    torch.save({
        "schemaVersion": 1, "taskId": "G2-R4-P2-R", "architecture": selected,
        "historyContract": history_contract, "threshold": threshold,
        "policyInputs": ["cleanUltrasoundImageHistory", "policyOwnedMemory", "issuedAndActuallyExecutedActionFeedback"],
        "deployment": {"status": "NON_DEPLOYABLE", "safeOperatingPoint": False, "reason": "corrective development study only; confirmation and deployment prohibited"},
        "models": final_models,
    }, checkpoint_path)
    checkpoint = {
        "schemaVersion": 1, "taskId": "G2-R4-P2-R", "architecture": selected,
        "historyContract": history_contract, "threshold": threshold, "seeds": seeds,
        "checkpoint": str(checkpoint_path.relative_to(repo)).replace("\\", "/"), "checkpointSha256": sha256(checkpoint_path),
        "deployment": {"status": "NON_DEPLOYABLE", "safeOperatingPoint": False},
        "developmentEligibleForFutureConfirmationProposal": eligible,
    }
    write_json(output / "g2_r4_p2r_candidate.json", checkpoint)
    historical = read_json(repo / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2_20261025/p2_cd_development_results.json")
    result = {
        "schemaVersion": 1, "taskId": "G2-R4-P2-R4", "verdict": "DEVELOPMENT_PASS" if eligible else "DEVELOPMENT_FAIL",
        "protocol": PROTOCOL, "protocolSha256": sha256(protocol_path),
        "lineageSha256": sha256(output / "r1_complete_lineage_graph.json"),
        "historicalReference": {
            "sourceCommit": protocol["sourceCommit"], "protocolSha256": protocol["historicalP2ProtocolSha256"],
            "resultsSha256": protocol["historicalP2ResultsSha256"],
            "selected": historical["selection"],
            "perFramePredictionsAvailable": False,
            "reproductionLimitation": "Historical P2 did not persist per-frame OOF predictions. Final-refit checkpoint predictions are not substituted for historical OOF evidence.",
        },
        "frozenBaseline": {"checkpoint": str(frozen_path.relative_to(repo)).replace("\\", "/"), "checkpointSha256": sha256(frozen_path), "threshold": 0.93, "metrics": frozen_metrics},
        "foldRuns": run_rows, "summaries": summaries,
        "selection": {
            "selected": selected, "singleFrameMedianGroupMacroAp": single_ap,
            "causalMultiviewMedianGroupMacroAp": temporal_ap, "causalImprovement": temporal_ap - single_ap,
            "calibratedThreshold": threshold, "calibratedDevelopmentMetrics": calibrated,
            "eligibleForFutureConfirmationProposal": eligible,
        },
        "predictionLedger": {"path": str(ledger_path.relative_to(repo)).replace("\\", "/"), "sha256": sha256(ledger_path), "rows": len(ledger_rows)},
        "failureAnalysis": "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2r_20261025/r3_failure_analysis.json",
        "checkpoint": checkpoint,
        "compute": {"elapsedSeconds": time.perf_counter() - started, "peakRssBytes": peak_rss, "torchVersion": torch.__version__, "device": "cpu", "threads": torch.get_num_threads()},
        "confirmationOpened": False, "lockedFamiliesOpened": [], "closedLoopRun": False,
    }
    write_json(output / "r4_corrected_development_results.json", result)
    print(json.dumps({"verdict": result["verdict"], "selection": result["selection"], "compute": result["compute"]}, indent=2))


if __name__ == "__main__":
    main()
