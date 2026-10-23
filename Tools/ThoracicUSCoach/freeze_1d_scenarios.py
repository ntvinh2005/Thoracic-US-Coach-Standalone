from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from contract_v3 import CONTRACT_VERSION, EXPERIMENT_ID, OBSERVATION_SCHEMA, TARGET_SEMANTICS, canonical_json_sha256
from protocol_v3 import ACTION_ORDER, protocol_identity, scenario_identity_sha256
from ppo_live_refine import starts_from_scenario
from unity_env import UnityFileEnvironment
from coach_data import project_root, save_json


GROUP_SCENARIOS = {
    # Keep these slots synchronized with balanced_500_v1 in
    # DiverseTrajectorySchedule: 0-14 train, 15-17 development,
    # 18-22 heldout, and 23-24 stress in each 25-item cycle.
    "development": (15, 16, 17),
    "heldout": (18, 19, 20, 21, 22),
    "stress": (23, 24),
}


def main() -> None:
    parser = argparse.ArgumentParser(description="Freeze exact paired Unity scenarios for contract-v3 evaluation")
    parser.add_argument("--group", choices=tuple(GROUP_SCENARIOS), default="development")
    parser.add_argument("--seeds-per-scenario", type=int, default=2)
    parser.add_argument("--seed-base", type=int, default=202610220)
    parser.add_argument("--seed-attempts", type=int, default=64)
    parser.add_argument("--max-steps", type=int, default=35)
    parser.add_argument("--all-starts", action="store_true")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = args.output or project_root() / f"Artifacts/ThoracicUSCoach/manifests/one_d_{args.group}_contract_v3.json"
    if output.exists() and not args.force:
        raise SystemExit(f"Refusing to overwrite frozen manifest: {output}")
    environment = UnityFileEnvironment(timeout=20.0)
    scenarios = []
    for scenario_index in GROUP_SCENARIOS[args.group]:
        for replicate in range(args.seeds_per_scenario):
            seed_start = args.seed_base + scenario_index * 10_000 + replicate * 1_000
            trajectory_seed = None
            scenario = None
            last_error = None
            generation_attempts = []
            for offset in range(args.seed_attempts):
                candidate_seed = seed_start + offset
                try:
                    environment.reset(0, scenario_index=scenario_index, trajectory_seed=candidate_seed, regenerate_trajectory=True, max_steps=args.max_steps)
                except RuntimeError as error:
                    if "Could not generate requested constrained trajectory" not in str(error):
                        raise
                    last_error = error
                    generation_attempts.append({"seed": candidate_seed, "accepted": False, "reason": str(error)})
                    continue
                candidate_scenario = dict(environment.describe()["scenario"])
                exact = set(int(value) for value in candidate_scenario["acceptableIndices"])
                path_count = len(candidate_scenario["pathMm"])
                # Development and heldout suites must exercise acquisition,
                # not collapse into recognition-only STOP-at-reset episodes.
                if args.group != "stress" and (not exact or len(exact) == path_count):
                    last_error = RuntimeError(
                        f"seed {candidate_seed} has {len(exact)}/{path_count} acceptable poses"
                    )
                    generation_attempts.append({"seed": candidate_seed, "accepted": False, "reason": str(last_error)})
                    continue
                trajectory_seed = candidate_seed
                scenario = candidate_scenario
                generation_attempts.append({"seed": candidate_seed, "accepted": True, "reason": "predeclared geometry/coverage rules passed"})
                break
            if trajectory_seed is None:
                raise RuntimeError(
                    f"No valid constrained trajectory for scenario {scenario_index}, replicate {replicate}, "
                    f"seeds {seed_start}..{seed_start + args.seed_attempts - 1}"
                ) from last_error
            assert scenario is not None
            if scenario.get("environmentContractVersion") != CONTRACT_VERSION:
                raise RuntimeError("Unity/Python contract mismatch")
            starts = ({f"index-{index:03d}": index for index in range(len(scenario["pathMm"]))}
                      if args.all_starts or args.group in ("heldout", "stress")
                      else starts_from_scenario(scenario))
            exact = set(int(value) for value in scenario["acceptableIndices"])
            unique_starts: dict[int, list[str]] = {}
            for name, index in starts.items():
                unique_starts.setdefault(index, []).append(name)
            scenarios.append({
                "scenarioIndex": scenario_index,
                "trajectorySeed": trajectory_seed,
                "configurationFamily": scenario["trajectory"]["configurationFamily"],
                "configurationSha256": scenario["configurationSha256"],
                "labelsSha256": scenario["labelsSha256"],
                "scenarioIdentitySha256": scenario_identity_sha256(scenario),
                "targetReachable": bool(exact),
                "generationAttempts": generation_attempts,
                "starts": [
                    {"name": "+".join(names), "index": index, "initiallyAcceptable": index in exact}
                    for index, names in unique_starts.items()
                ],
                "scenario": scenario,
            })
    payload = {
        "schemaVersion": 1,
        "experimentId": EXPERIMENT_ID,
        "contractVersion": CONTRACT_VERSION,
        "observationSchema": OBSERVATION_SCHEMA,
        "targetSemantics": TARGET_SEMANTICS,
        "actionOrder": ACTION_ORDER,
        "group": args.group,
        "selectionAllowed": args.group == "development",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "selectionPolicy": "Only development may select models. Heldout/stress are final-only and their results must not alter candidates, hyperparameters, or stopping.",
        "protocol": protocol_identity(args.max_steps),
        "coverageRequirement": "Every development/heldout geometry must contain at least one acceptable and one unacceptable pose.",
        "scenarios": scenarios,
    }
    starts = [start for scenario in scenarios for start in scenario["starts"]]
    payload["coverageAudit"] = {
        "geometries": len(scenarios),
        "uniqueScenarioStarts": len(starts),
        "recognitionStarts": sum(bool(start["initiallyAcceptable"]) for start in starts),
        "acquisitionStarts": sum(not bool(start["initiallyAcceptable"]) for start in starts),
    }
    payload["manifestSha256"] = canonical_json_sha256({key: value for key, value in payload.items() if key != "createdUtc"})
    save_json(output, payload)
    print(json.dumps({"output": str(output), "group": args.group, "geometries": len(scenarios), "manifestSha256": payload["manifestSha256"]}, indent=2))


if __name__ == "__main__":
    main()
