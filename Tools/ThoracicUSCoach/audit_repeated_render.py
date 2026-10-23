from __future__ import annotations

import argparse
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image

from coach_data import model_probabilities, pixel_features, request_features, save_json
from stop_policy import request_stop_features, stop_probabilities, two_stage_action, validate_stop_model
from unity_env import UnityFileEnvironment


def raw_image(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("L"), dtype=np.float32) / 255.0


def summarize(values) -> dict[str, object]:
    array = np.asarray(list(values), dtype=np.float64)
    return {
        "count": int(len(array)),
        "mean": float(array.mean()) if len(array) else None,
        "std": float(array.std()) if len(array) else None,
        "min": float(array.min()) if len(array) else None,
        "max": float(array.max()) if len(array) else None,
    }


def selected_indices(scenario: dict[str, object]) -> dict[str, int]:
    exact = sorted(int(value) for value in scenario["acceptableIndices"])
    if not exact:
        raise ValueError("Repeated-render audit requires reachable development targets")
    inside = exact[len(exact) // 2]
    exact_set = set(exact)
    outside = [
        candidate
        for target in exact
        for candidate in (target - 1, target + 1)
        if 0 <= candidate < len(scenario["pathMm"]) and candidate not in exact_set
    ]
    if not outside:
        raise ValueError("No immediately unacceptable neighbor")
    far = max(
        (index for index in range(len(scenario["pathMm"])) if index not in exact_set),
        key=lambda index: min(abs(index - target) for target in exact),
    )
    return {"inside": inside, "immediately-outside": outside[0], "far": far}


def main() -> None:
    parser = argparse.ArgumentParser(description="Repeated live renderer audit on calibration-only frozen poses")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--navigation-checkpoint", type=Path, required=True)
    parser.add_argument("--stop-checkpoint", type=Path, required=True)
    parser.add_argument("--scenario-count", type=int, default=3)
    parser.add_argument("--repeats", type=int, default=5)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    if manifest.get("selectionUse") != "development-calibration":
        raise SystemExit("Repeated-render tuning audit is restricted to the calibration manifest")
    navigation = json.loads(args.navigation_checkpoint.read_text(encoding="utf-8"))
    stop = json.loads(args.stop_checkpoint.read_text(encoding="utf-8"))
    validate_stop_model(stop)
    output_dir = args.output_dir.resolve()
    image_dir = output_dir / "representative_live_renders"
    image_dir.mkdir(parents=True, exist_ok=True)
    environment = UnityFileEnvironment(timeout=20.0)
    groups = []
    total_resets = 0
    for scenario_record in manifest["scenarios"][: args.scenario_count]:
        scenario = dict(scenario_record["scenario"])
        poses = selected_indices(scenario)
        for pose_name, index in poses.items():
            renders = []
            for repeat in range(args.repeats):
                response = environment.reset(
                    index,
                    max_steps=int(scenario["episodeBudget"]),
                    frozen_scenario=scenario,
                )
                total_resets += 1
                image_path = Path(response["imagePath"])
                raw = raw_image(image_path)
                zscore = pixel_features(image_path, 16)
                nav_features = request_features(
                    image_path,
                    None,
                    0,
                    int(navigation["featureSize"]),
                    str(navigation["observationMode"]),
                )
                stop_features = request_stop_features(stop, image_path, None, 0)
                action, score, nav_probability = two_stage_action(
                    navigation, stop, nav_features, stop_features
                )
                if repeat == 0:
                    destination = image_dir / (
                        f"{scenario_record['trajectorySeed']}_{pose_name}_{index:03d}.png"
                    )
                    shutil.copy2(image_path, destination)
                renders.append(
                    {
                        "raw": raw,
                        "zscore": zscore,
                        "stopScore": score,
                        "twoStageAction": action,
                        "navigationProbability": nav_probability.tolist(),
                        "observedScore": float(response["observedScore"]),
                        "referenceScore": float(response["referenceScore"]),
                        "targetAcceptable": bool(response["targetAcceptable"]),
                    }
                )
            reference = renders[0]
            raw_rms = [
                float(np.sqrt(np.mean((item["raw"] - reference["raw"]) ** 2)))
                for item in renders[1:]
            ]
            feature_rms = [
                float(np.sqrt(np.mean((item["zscore"] - reference["zscore"]) ** 2)))
                for item in renders[1:]
            ]
            groups.append(
                {
                    "trajectorySeed": scenario_record["trajectorySeed"],
                    "scenarioIdentitySha256": scenario_record["scenarioIdentitySha256"],
                    "pose": pose_name,
                    "index": index,
                    "acceptable": bool(reference["targetAcceptable"]),
                    "rawRmsVsFirst": raw_rms,
                    "zscore16RmsVsFirst": feature_rms,
                    "stopScores": [item["stopScore"] for item in renders],
                    "twoStageActions": [item["twoStageAction"] for item in renders],
                    "observedScores": [item["observedScore"] for item in renders],
                    "rawMeanImage": np.mean([item["raw"] for item in renders], axis=0),
                }
            )
    within_raw = [value for group in groups for value in group["rawRmsVsFirst"]]
    within_feature = [value for group in groups for value in group["zscore16RmsVsFirst"]]
    score_ranges = [max(group["stopScores"]) - min(group["stopScores"]) for group in groups]
    action_flip_groups = sum(len(set(group["twoStageActions"])) > 1 for group in groups)
    between = []
    by_seed = {}
    for group in groups:
        by_seed.setdefault(group["trajectorySeed"], {})[group["pose"]] = group
    for seed, poses in by_seed.items():
        if "inside" not in poses or "immediately-outside" not in poses:
            continue
        difference = poses["inside"]["rawMeanImage"] - poses["immediately-outside"]["rawMeanImage"]
        between.append(float(np.sqrt(np.mean(difference * difference))))
    serializable_groups = []
    for group in groups:
        item = {key: value for key, value in group.items() if key != "rawMeanImage"}
        item["stopScoreRange"] = max(group["stopScores"]) - min(group["stopScores"])
        item["actionStable"] = len(set(group["twoStageActions"])) == 1
        serializable_groups.append(item)
    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "phase": "Stage A repeated-render calibration audit",
        "manifest": str(args.manifest.resolve()),
        "manifestSha256": manifest["manifestSha256"],
        "selectionUse": manifest["selectionUse"],
        "heldoutOrStressAccessed": False,
        "navigationCheckpoint": str(args.navigation_checkpoint.resolve()),
        "stopCheckpoint": str(args.stop_checkpoint.resolve()),
        "unityResets": total_resets,
        "groups": serializable_groups,
        "summary": {
            "samePoseRawRms": summarize(within_raw),
            "samePoseZscore16Rms": summarize(within_feature),
            "adjacentInsideOutsideMeanRenderRawRms": summarize(between),
            "stopScoreRange": summarize(score_ranges),
            "actionFlipGroups": action_flip_groups,
            "poseGroups": len(groups),
            "betweenToWithinRawMeanRatio": (
                float(np.mean(between) / np.mean(within_raw)) if between and np.mean(within_raw) else None
            ),
        },
    }
    save_json(output_dir / "repeated_render_audit.json", report)
    print(json.dumps(report["summary"], indent=2))


if __name__ == "__main__":
    main()
