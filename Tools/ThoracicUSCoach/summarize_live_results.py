from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

import numpy as np

from coach_data import project_root, save_json
from contract_v3 import oracle_action


def resolve_artifact(value: str, report_path: Path) -> Path:
    path = Path(value)
    for candidate in (path, report_path.parent / path, project_root() / path):
        if candidate.is_file():
            return candidate.resolve()
    raise FileNotFoundError(value)


def metric_summary(rows: list[dict[str, object]], success_key: str, rng, bootstrap_samples: int) -> dict[str, object]:
    successes = int(sum(bool(row[success_key]) for row in rows))
    total = len(rows)
    by_geometry: dict[str, list[dict[str, object]]] = defaultdict(list)
    for row in rows:
        by_geometry[str(row["geometryKey"])].append(row)
    geometry_rates = [float(np.mean([bool(row[success_key]) for row in group])) for group in by_geometry.values()]
    draws = []
    keys = sorted(by_geometry)
    if keys:
        for _ in range(bootstrap_samples):
            sampled = [keys[index] for index in rng.integers(0, len(keys), len(keys))]
            chosen = [row for key in sampled for row in by_geometry[key]]
            draws.append(float(np.mean([bool(row[success_key]) for row in chosen])))
    return {
        "successes": successes,
        "total": total,
        "episodeWeightedRate": successes / total if total else None,
        "geometryWeightedRate": float(np.mean(geometry_rates)) if geometry_rates else None,
        "clusterBootstrap95Ci": [float(np.percentile(draws, 2.5)), float(np.percentile(draws, 97.5))] if draws else None,
        "geometryCount": len(keys),
    }


def annotate(report: dict[str, object], report_path: Path) -> tuple[list[dict[str, object]], dict[str, object]]:
    policy = report.get("policy") or {}
    episodes = [dict(item) for item in policy.get("episodes", [])]
    manifest_path = resolve_artifact(str(report["scenarioManifest"]), report_path)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    scenarios = {
        (int(item["scenarioIndex"]), int(item["trajectorySeed"])): item
        for item in manifest["scenarios"]
    }
    for episode in episodes:
        key = (int(episode["scenarioIndex"]), int(episode["trajectorySeed"]))
        scenario_item = scenarios[key]
        scenario = scenario_item["scenario"]
        path_count = len(scenario["pathMm"])
        exact = set(int(value) for value in scenario["acceptableIndices"])
        acceptable = tuple(index in exact for index in range(path_count))
        start = int(episode["startIndex"])
        distance = min((abs(start - target) for target in exact), default=None)
        action = oracle_action(acceptable, start, int(scenario.get("actionStepCount", 1))) if exact else 0
        episode["geometryKey"] = f"{key[0]}:{key[1]}"
        episode["caseKey"] = f"{key[0]}:{key[1]}:{start}"
        episode["pathDirection"] = int(scenario["trajectory"].get("pathDirection", 1))
        episode["requiredAction"] = action
        episode["requiredPhysicalDirection"] = "forward" if action == 1 else "backward" if action == -1 else "stop-or-absent"
        episode["distanceIndices"] = distance
        episode["nearBoundaryStart"] = bool(distance is not None and distance <= int(scenario.get("actionStepCount", 1)))
        episode["internalHoleStart"] = bool(exact and min(exact) < start < max(exact) and start not in exact)
        episode["farStart"] = bool(distance is not None and distance >= max(2, int(np.ceil(path_count / 3))))
        episode["endpointStart"] = start in (0, path_count - 1)
    return episodes, manifest


def paired_delta(left, right, success_key: str, predicate, rng, bootstrap_samples: int) -> dict[str, object]:
    left_cases = {str(row["caseKey"]): row for row in left if predicate(row)}
    right_cases = {str(row["caseKey"]): row for row in right if predicate(row)}
    common = sorted(set(left_cases) & set(right_cases))
    differences = [float(bool(right_cases[key][success_key])) - float(bool(left_cases[key][success_key])) for key in common]
    by_geometry: dict[str, list[float]] = defaultdict(list)
    for key, difference in zip(common, differences):
        by_geometry[str(left_cases[key]["geometryKey"])].append(difference)
    geometries = sorted(by_geometry)
    draws = []
    if geometries:
        for _ in range(bootstrap_samples):
            sample = [geometries[index] for index in rng.integers(0, len(geometries), len(geometries))]
            draws.append(float(np.mean([value for geometry in sample for value in by_geometry[geometry]])))
    return {
        "pairedCases": len(common),
        "geometryCount": len(geometries),
        "rightMinusLeftRate": float(np.mean(differences)) if differences else None,
        "clusterBootstrap95Ci": [float(np.percentile(draws, 2.5)), float(np.percentile(draws, 97.5))] if draws else None,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Geometry-clustered summary of paired live Unity evaluations")
    parser.add_argument("--evaluation", action="append", required=True, help="LABEL=path/to/live_report.json")
    parser.add_argument("--bootstrap-samples", type=int, default=2_000)
    parser.add_argument("--seed", type=int, default=20261022)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rng = np.random.default_rng(args.seed)
    candidates: dict[str, dict[str, object]] = {}
    annotated: dict[str, list[dict[str, object]]] = {}
    for specification in args.evaluation:
        if "=" not in specification:
            raise SystemExit("Each --evaluation must be LABEL=PATH")
        label, raw_path = specification.split("=", 1)
        path = Path(raw_path).resolve()
        report = json.loads(path.read_text(encoding="utf-8"))
        rows, manifest = annotate(report, path)
        annotated[label] = rows
        reachable = [row for row in rows if row["targetReachable"]]
        recognition = [row for row in reachable if row["initiallyAcceptable"]]
        acquisition = [row for row in reachable if not row["initiallyAcceptable"]]
        absent = [row for row in rows if row["targetAbsent"]]
        acquisition_categories = {
            "forward": [row for row in acquisition if row["requiredAction"] == 1],
            "backward": [row for row in acquisition if row["requiredAction"] == -1],
            "nearBoundary": [row for row in acquisition if row["nearBoundaryStart"]],
            "internalHole": [row for row in acquisition if row["internalHoleStart"]],
            "far": [row for row in acquisition if row["farStart"]],
            "endpoint": [row for row in acquisition if row["endpointStart"]],
        }
        training_seed = None
        checkpoint = report.get("checkpoint")
        if checkpoint:
            try:
                checkpoint_payload = json.loads(resolve_artifact(str(checkpoint), path).read_text(encoding="utf-8"))
                training_seed = checkpoint_payload.get("trainingSeed") or (checkpoint_payload.get("training") or {}).get("seed")
            except FileNotFoundError:
                pass
        candidates[label] = {
            "report": str(path),
            "checkpoint": checkpoint,
            "model": report.get("model"),
            "baseline": report.get("baseline"),
            "evaluationGroup": report.get("evaluationGroup"),
            "trainingSeed": training_seed,
            "recognition": metric_summary(recognition, "recognitionSuccess", rng, args.bootstrap_samples),
            "acquisition": metric_summary(acquisition, "acquisitionSuccess", rng, args.bootstrap_samples),
            "reachableOverall": metric_summary(reachable, "explicitStopSuccess", rng, args.bootstrap_samples),
            "acquisitionByCategory": {
                name: metric_summary(selected, "acquisitionSuccess", rng, args.bootstrap_samples)
                for name, selected in acquisition_categories.items()
            },
            "failureCounts": {
                "wrongStopEpisodes": sum(bool(row["wrongStop"]) for row in rows),
                "regionEnteredWithoutCorrectStop": sum(bool(row["regionEnteredWithoutCorrectStop"]) for row in rows),
                "timeouts": sum(bool(row["timeout"]) for row in rows),
                "boundaryActions": sum(int(row["boundaryFailures"]) for row in rows),
                "maxBoundaryStuckStreak": max((int(row["longestBoundaryStuckStreak"]) for row in rows), default=0),
                "contactViolations": sum(int(row["contactViolations"]) for row in rows),
                "workspaceViolations": sum(int(row["workspaceViolations"]) for row in rows),
                "constraintViolations": sum(int(row["constraintViolations"]) for row in rows),
            },
            "targetAbsent": {
                "episodes": len(absent),
                "wrongStopEpisodes": sum(bool(row["wrongStop"]) for row in absent),
                "timeouts": sum(bool(row["timeout"]) for row in absent),
            },
            "manifestSha256": manifest.get("manifestSha256"),
        }
    comparisons: dict[str, object] = {}
    labels = list(candidates)
    for left_index, left in enumerate(labels):
        for right in labels[left_index + 1 :]:
            comparisons[f"{left}__vs__{right}"] = {
                "interpretation": f"positive delta favors {right}",
                "recognition": paired_delta(
                    annotated[left], annotated[right], "recognitionSuccess",
                    lambda row: bool(row["targetReachable"] and row["initiallyAcceptable"]), rng, args.bootstrap_samples,
                ),
                "acquisition": paired_delta(
                    annotated[left], annotated[right], "acquisitionSuccess",
                    lambda row: bool(row["targetReachable"] and not row["initiallyAcceptable"]), rng, args.bootstrap_samples,
                ),
            }
    seed_groups: dict[str, list[tuple[int, dict[str, object]]]] = defaultdict(list)
    for label, item in candidates.items():
        if item["trainingSeed"] is not None:
            method = label.rsplit("-seed", 1)[0] if "-seed" in label else str(item["model"])
            seed_groups[method].append((int(item["trainingSeed"]), item))
    across_seed = {}
    for method, values in seed_groups.items():
        across_seed[method] = {
            "seeds": [seed for seed, _ in values],
            "acquisitionRates": [item["acquisition"]["episodeWeightedRate"] for _, item in values],
            "recognitionRates": [item["recognition"]["episodeWeightedRate"] for _, item in values],
            "acquisitionMean": float(np.mean([item["acquisition"]["episodeWeightedRate"] for _, item in values])),
            "acquisitionStdAcrossSeeds": float(np.std([item["acquisition"]["episodeWeightedRate"] for _, item in values], ddof=1)) if len(values) > 1 else None,
            "recognitionMean": float(np.mean([item["recognition"]["episodeWeightedRate"] for _, item in values])),
            "recognitionStdAcrossSeeds": float(np.std([item["recognition"]["episodeWeightedRate"] for _, item in values], ddof=1)) if len(values) > 1 else None,
        }
    payload = {
        "schemaVersion": 1,
        "bootstrapUnit": "geometry with all associated starts",
        "bootstrapSamples": args.bootstrap_samples,
        "confidenceLevel": 0.95,
        "candidates": candidates,
        "pairedComparisons": comparisons,
        "acrossSeedVariability": across_seed,
    }
    save_json(args.output, payload)
    print(json.dumps({
        "output": str(args.output),
        "candidates": {
            label: {"acquisition": item["acquisition"], "recognition": item["recognition"]}
            for label, item in candidates.items()
        },
    }, indent=2))


if __name__ == "__main__":
    main()
