from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from coach_data import project_root, save_json
from contract_v3 import CONTRACT_VERSION, TARGET_SEMANTICS
from unity_env import UnityFileEnvironment


def main() -> None:
    parser = argparse.ArgumentParser(description="Measure repeated-pose score noise and fixed-label/current-image agreement")
    parser.add_argument("--scenario-index", type=int, default=12)
    parser.add_argument("--trajectory-seed", type=int, default=202611420)
    parser.add_argument("--repeats", type=int, default=8)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/metrics/score_noise_contract_v3.json")
    args = parser.parse_args()
    environment = UnityFileEnvironment(timeout=20.0)
    environment.reset(0, scenario_index=args.scenario_index, trajectory_seed=args.trajectory_seed, regenerate_trajectory=True)
    scenario = environment.describe()["scenario"]
    rows = []
    for index in range(len(scenario["pathMm"])):
        samples = [environment.reset(index) for _ in range(args.repeats)]
        fixed = bool(samples[0]["targetAcceptable"])
        observed = np.asarray([float(item["observedScore"]) for item in samples])
        agreements = [bool(item["currentImageAcceptable"]) == fixed for item in samples]
        rows.append({
            "pathIndex": index,
            "fixedPoseAcceptable": fixed,
            "fixedPoseScore": float(samples[0]["referenceScore"]),
            "observedScoreMean": float(observed.mean()),
            "observedScoreStd": float(observed.std()),
            "observedScoreMin": float(observed.min()),
            "observedScoreMax": float(observed.max()),
            "labelAgreementCount": sum(agreements),
            "samples": args.repeats,
        })
    report = {
        "schemaVersion": 1,
        "contractVersion": CONTRACT_VERSION,
        "targetSemantics": TARGET_SEMANTICS,
        "configurationSha256": scenario["configurationSha256"],
        "labelsSha256": scenario["labelsSha256"],
        "rendererFullySeeded": bool(scenario["rendererFullySeeded"]),
        "repeatsPerPose": args.repeats,
        "poses": rows,
        "summary": {
            "maxRepeatedPoseScoreStd": max(row["observedScoreStd"] for row in rows),
            "fixedVsCurrentImageAgreementRate": sum(row["labelAgreementCount"] for row in rows) / (len(rows) * args.repeats),
        },
    }
    save_json(args.output, report)
    print(json.dumps(report["summary"], indent=2))


if __name__ == "__main__":
    main()
