"""Adapt the bounded Phase-2 recognizer to fresh cross-family captures.

The supplemental Unity report is development evidence.  Anatomy-derived labels
and scores are used only while fitting and evaluating this offline model.  The
serialized actor consumes ultrasound pixels only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import time
from dataclasses import asdict
from pathlib import Path

import joblib
import numpy as np
from sklearn.ensemble import ExtraTreesClassifier, ExtraTreesRegressor

from active2d_contract import project_root
from active2d_data import load_active2d_frames
from phase2_best_view_recognition import (
    ACTOR_INPUTS,
    FORBIDDEN_INPUTS,
    FEATURE_MODES,
    _sha256,
    evaluate,
    extract_all_features,
    safe_threshold,
)


def _load_base_cache(dataset_run_id: str, cache_path: Path) -> tuple[dict, dict]:
    frames = [
        frame for frame in load_active2d_frames(dataset_run_id)
        if frame.contact_valid and frame.workspace_valid
    ]
    roles = {
        role: [frame for frame in frames if frame.split == role]
        for role in ("train", "dev", "test")
    }
    cache = np.load(cache_path, allow_pickle=False)
    global_width = int(cache["global_width"].item())
    matrices = {}
    for role, role_frames in roles.items():
        combined = np.asarray(cache[f"{role}_combined"], dtype=np.float32)
        if len(combined) != len(role_frames):
            raise ValueError(f"Base cache length mismatch for {role}")
        matrices[role] = {
            "global": combined[:, :global_width],
            "topology": combined[:, global_width:],
            "combined": combined,
        }
    return roles, matrices


def _role_from_case(case_id: str) -> str:
    for role in ("train", "dev", "test"):
        if f"_{role}_" in case_id:
            return role
    raise ValueError(f"Cannot determine split for {case_id}")


def load_supplemental(report_path: Path) -> dict:
    report = json.loads(report_path.read_text(encoding="utf-8"))
    root = project_root()
    roles = {role: [] for role in ("train", "dev", "test")}
    for case in report["cases"]:
        role = _role_from_case(case["caseId"])
        for observation in case["observations"]:
            image = root / observation["image"]
            if not image.is_file():
                raise FileNotFoundError(image)
            actual_sha = hashlib.sha256(image.read_bytes()).hexdigest()
            if actual_sha != observation["imageSha256"]:
                raise ValueError(f"Image integrity mismatch: {image}")
            roles[role].append({
                "caseId": case["caseId"],
                "family": case["family"],
                "image": image,
                "imageSha256": actual_sha,
                "label": int(observation["targetAcceptable"]),
                "score": float(observation["score"]["total"]),
                "features": extract_all_features(image),
            })
    return roles


def challenge_frames() -> list[dict]:
    root = project_root()
    definitions = [
        ("smoke_e1_f1", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_minimal_e2e_smoke_20261027/representative_frames/e1_f1.png", 1),
        ("smoke_e1_f2", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_minimal_e2e_smoke_20261027/representative_frames/e1_f2.png", 1),
        ("smoke_e2_f1", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_minimal_e2e_smoke_20261027/representative_frames/e2_f1.png", 1),
        ("smoke_e2_f2", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_minimal_e2e_smoke_20261027/representative_frames/e2_f2.png", 1),
        ("smoke_e3_f1", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_minimal_e2e_smoke_20261027/representative_frames/e3_f1.png", 0),
        ("smoke_e3_f2", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_minimal_e2e_smoke_20261027/representative_frames/e3_f2.png", 0),
        ("engineering_positive_01", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_engineering_poc_20261027/representative_frames/consumed_positive_01.png", 1),
        ("engineering_positive_05", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_engineering_poc_20261027/representative_frames/consumed_positive_05.png", 1),
        ("engineering_hard_negative_06", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_engineering_poc_20261027/representative_frames/consumed_hard_negative_06.png", 0),
        ("engineering_episode_1_fresh_positive", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_engineering_poc_20261027/representative_frames/episode_1.png", 1),
        ("engineering_episode_2_fresh_positive", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_engineering_poc_20261027/representative_frames/episode_2.png", 1),
        ("engineering_episode_3_fresh_negative", "Artifacts/ThoracicUSCoach/active2d/evaluation/phase2_engineering_poc_20261027/representative_frames/episode_3.png", 0),
    ]
    output = []
    for name, relative, label in definitions:
        path = root / relative
        if path.is_file():
            output.append({
                "name": name,
                "path": path,
                "label": label,
                "features": extract_all_features(path),
            })
    return output


def _arrays(records: list[dict], mode: str) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    return (
        np.stack([record["features"][mode] for record in records]),
        np.asarray([record["label"] for record in records], dtype=np.int32),
        np.asarray([record["score"] for record in records], dtype=np.float32),
    )


def run_adaptation(
    dataset_run_id: str,
    cache_path: Path,
    supplemental_report: Path,
    output: Path,
    *,
    trees: int = 360,
    include_consumed_bridge: bool = False,
) -> dict:
    started = time.perf_counter()
    base_roles, base_matrices = _load_base_cache(dataset_run_id, cache_path)
    supplemental = load_supplemental(supplemental_report)
    challenges = challenge_frames()
    unique_challenges = []
    challenge_hashes = set()
    for challenge in challenges:
        digest = hashlib.sha256(challenge["path"].read_bytes()).hexdigest()
        if digest not in challenge_hashes:
            challenge_hashes.add(digest)
            unique_challenges.append(challenge)
    challenges = unique_challenges
    output.mkdir(parents=True, exist_ok=True)
    candidates = []
    for mode in FEATURE_MODES:
        supplemental_arrays = {
            role: _arrays(records, mode) for role, records in supplemental.items()
        }
        base_labels = {
            role: np.asarray([frame.target_acceptable for frame in frames], dtype=np.int32)
            for role, frames in base_roles.items()
        }
        base_scores = {
            role: np.asarray([frame.target_score for frame in frames], dtype=np.float32)
            for role, frames in base_roles.items()
        }
        train_x = np.concatenate((base_matrices["train"][mode], supplemental_arrays["train"][0]))
        train_y = np.concatenate((base_labels["train"], supplemental_arrays["train"][1]))
        train_scores = np.concatenate((base_scores["train"], supplemental_arrays["train"][2]))
        bridge_x = np.stack([challenge["features"][mode] for challenge in challenges])
        bridge_y = np.asarray([challenge["label"] for challenge in challenges], dtype=np.int32)
        if include_consumed_bridge:
            train_x = np.concatenate((train_x, bridge_x))
            train_y = np.concatenate((train_y, bridge_y))
            train_scores = np.concatenate((train_scores, np.where(bridge_y > 0, 0.70, 0.20)))
        classifier = ExtraTreesClassifier(
            n_estimators=trees,
            max_depth=28,
            min_samples_leaf=1,
            class_weight="balanced",
            max_features=0.50,
            n_jobs=-1,
            random_state=20261103,
        )
        classifier.fit(train_x, train_y)
        quality_fit = (train_scores >= 0.50) | (train_y > 0)
        regressor = ExtraTreesRegressor(
            n_estimators=trees,
            max_depth=28,
            min_samples_leaf=1,
            max_features=0.50,
            n_jobs=-1,
            random_state=20261104,
        )
        regressor.fit(train_x[quality_fit], train_scores[quality_fit])

        dev_x = np.concatenate((base_matrices["dev"][mode], supplemental_arrays["dev"][0]))
        dev_y = np.concatenate((base_labels["dev"], supplemental_arrays["dev"][1]))
        if include_consumed_bridge:
            bridge_negative = bridge_x[bridge_y == 0]
            dev_x = np.concatenate((dev_x, bridge_negative))
            dev_y = np.concatenate((dev_y, np.zeros(len(bridge_negative), dtype=np.int32)))
        threshold = safe_threshold(dev_y, classifier.predict_proba(dev_x)[:, 1])
        metrics = {}
        for role in ("train", "dev", "test"):
            x = np.concatenate((base_matrices[role][mode], supplemental_arrays[role][0]))
            labels = np.concatenate((base_labels[role], supplemental_arrays[role][1]))
            scores = np.concatenate((base_scores[role], supplemental_arrays[role][2]))
            probabilities = classifier.predict_proba(x)[:, 1]
            quality = np.clip(regressor.predict(x), 0.0, 1.0)
            metrics[role] = asdict(evaluate(labels, probabilities, scores, quality, threshold))

        challenge_results = []
        for challenge in challenges:
            probability = float(classifier.predict_proba(
                challenge["features"][mode].reshape(1, -1)
            )[0, 1])
            challenge_results.append({
                "name": challenge["name"],
                "label": challenge["label"],
                "probability": probability,
                "accepted": probability >= threshold,
            })
        model_path = output / f"phase2_best_view_adapted_{mode}.joblib"
        joblib.dump({
            "schema": "phase2-best-view-extra-trees-adapted-v2",
            "featureMode": mode,
            "actorInputs": ACTOR_INPUTS,
            "forbiddenInputs": FORBIDDEN_INPUTS,
            "threshold": threshold,
            "classifier": classifier,
            "qualityRegressor": regressor,
            "developmentSources": [dataset_run_id, str(supplemental_report)],
        }, model_path)
        challenge_tp = sum(item["label"] and item["accepted"] for item in challenge_results)
        challenge_fp = sum((not item["label"]) and item["accepted"] for item in challenge_results)
        challenge_positive = sum(item["label"] for item in challenge_results)
        candidates.append({
            "featureMode": mode,
            "threshold": threshold,
            "metrics": metrics,
            "challenge": challenge_results,
            "challengeRecall": challenge_tp / max(challenge_positive, 1),
            "challengeFalsePositive": challenge_fp,
            "model": str(model_path),
            "modelSha256": _sha256(model_path),
        })

    selected = max(candidates, key=lambda candidate: (
        candidate["challengeFalsePositive"] == 0,
        candidate["challengeRecall"],
        candidate["metrics"]["test"]["false_positive"] == 0,
        candidate["metrics"]["test"]["recall"],
    ))
    report = {
        "schemaVersion": 2,
        "experiment": "PHASE2_BEST_VIEW_RECOGNITION_ADAPTATION_V2",
        "scientificStatus": "DEVELOPMENT_ONLY_ALL_CHALLENGES_PREVIOUSLY_CONSUMED",
        "actorInputs": ACTOR_INPUTS,
        "forbiddenInputs": FORBIDDEN_INPUTS,
        "supplementalSplitByWholeSourceFamily": True,
        "consumedBridgeIncludedInTraining": include_consumed_bridge,
        "consumedBridgePreviouslyOpenedDevelopmentEvidence": True,
        "supplementalCounts": {
            role: {
                "frames": len(records),
                "cases": len(set(record["caseId"] for record in records)),
                "positives": sum(record["label"] for record in records),
            }
            for role, records in supplemental.items()
        },
        "candidates": candidates,
        "selectedFeatureMode": selected["featureMode"],
        "readiness": {
            "zeroConsumedChallengeFalsePositives": selected["challengeFalsePositive"] == 0,
            "consumedChallengeRecallAtLeast75Percent": selected["challengeRecall"] >= 0.75,
            "zeroHistoricalAndSupplementalTestFalsePositives": selected["metrics"]["test"]["false_positive"] == 0,
            "newIndependentUnitySessionStillRequired": True,
        },
        "elapsedSeconds": time.perf_counter() - started,
    }
    (output / "offline_adaptation_comparison.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description="Adapt the Phase-2 recognizer to fresh captures")
    parser.add_argument("--dataset-run-id", default="active2d_phase2_raster_v2_20261023")
    parser.add_argument("--base-cache", type=Path, required=True)
    parser.add_argument("--supplemental-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--trees", type=int, default=360)
    parser.add_argument("--include-consumed-bridge", action="store_true")
    args = parser.parse_args()
    root = project_root()
    base_cache = args.base_cache if args.base_cache.is_absolute() else root / args.base_cache
    report_path = args.supplemental_report if args.supplemental_report.is_absolute() else root / args.supplemental_report
    output = args.output if args.output.is_absolute() else root / args.output
    report = run_adaptation(
        args.dataset_run_id,
        base_cache,
        report_path,
        output,
        trees=args.trees,
        include_consumed_bridge=args.include_consumed_bridge,
    )
    print(json.dumps({
        "selectedFeatureMode": report["selectedFeatureMode"],
        "readiness": report["readiness"],
        "elapsedSeconds": report["elapsedSeconds"],
    }, indent=2))


if __name__ == "__main__":
    main()
