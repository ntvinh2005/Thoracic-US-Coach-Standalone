from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image

from coach_data import project_root, save_json
from contract_v3 import CONTRACT_VERSION, transition
from protocol_v3 import validate_manifest, verify_frozen_scenario
from unity_env import UnityFileEnvironment


def file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def observation_pixels(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("L").resize((32, 32), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0


def main() -> None:
    parser = argparse.ArgumentParser(description="Paired captured-cache versus live-Unity contract verification")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--scenarios", type=int, default=3)
    parser.add_argument("--max-steps", type=int, help="Must match the frozen protocol; defaults to its episode budget")
    parser.add_argument("--observation-rms-tolerance", type=float, default=0.005)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/metrics/cached_live_parity.json")
    args = parser.parse_args()
    frozen = json.loads(args.manifest.read_text(encoding="utf-8"))
    if frozen.get("contractVersion") != CONTRACT_VERSION:
        raise SystemExit("Frozen manifest contract mismatch")
    validate_manifest(frozen, expected_group=str(frozen["group"]), selection_allowed=bool(frozen["selectionAllowed"]))
    frozen_budget = int(frozen["protocol"]["episodeBudget"])
    if args.max_steps is not None and args.max_steps != frozen_budget:
        raise SystemExit(f"--max-steps must match frozen episode budget {frozen_budget}")
    max_steps = frozen_budget
    environment = UnityFileEnvironment(timeout=20.0)
    probes: list[dict[str, object]] = []
    failures: list[dict[str, object]] = []
    action_sequences = ((-1, 1, 0), (1, -1, 0), (1, 1, -1, 0), (-1, -1, 1, 0))

    for frozen_scenario in frozen["scenarios"][: args.scenarios]:
        exact = dict(frozen_scenario["scenario"])
        environment.reset(0, max_steps=max_steps, frozen_scenario=exact)
        actual = dict(environment.describe()["scenario"])
        verify_frozen_scenario(frozen_scenario, actual)
        path_mm = [float(value) for value in actual["pathMm"]]
        exact_indices = set(int(value) for value in actual["acceptableIndices"])
        acceptable = [index in exact_indices for index in range(len(path_mm))]
        step_count = int(actual["actionStepCount"])

        observation_hashes: dict[int, str] = {}
        observation_references: dict[int, np.ndarray] = {}
        observation_flags: dict[int, tuple[bool, bool]] = {}
        for index in range(len(path_mm)):
            response = environment.reset(index, max_steps=max_steps, frozen_scenario=exact)
            observation_hashes[index] = file_sha256(Path(str(response["imagePath"])))
            observation_references[index] = observation_pixels(Path(str(response["imagePath"])))
            observation_flags[index] = (bool(response["contactValid"]), bool(response["workspaceValid"]))

        starts = [int(item["index"]) for item in frozen_scenario["starts"]]
        starts = list(dict.fromkeys(starts))[:4]
        for probe_index, start in enumerate(starts):
            actions = action_sequences[probe_index % len(action_sequences)]
            response = environment.reset(start, max_steps=max_steps, frozen_scenario=exact)
            cached_index = start
            steps = []
            for steps_taken, action in enumerate(actions):
                live = environment.step(action)
                expected_index = min(len(path_mm) - 1, max(0, cached_index + action * step_count))
                contact_valid, workspace_valid = observation_flags[expected_index]
                cached = transition(
                    index=cached_index,
                    action=action,
                    step_count=step_count,
                    acceptable=acceptable,
                    path_mm=path_mm,
                    contact_valid=contact_valid,
                    workspace_valid=workspace_valid,
                    steps_taken=steps_taken,
                    max_steps=max_steps,
                )
                live_hash = file_sha256(Path(str(live["imagePath"])))
                observation_rms = float(np.sqrt(np.mean(
                    (observation_pixels(Path(str(live["imagePath"]))) - observation_references[cached.index]) ** 2
                )))
                comparisons = {
                    "previousIndex": int(live.get("previousIndex", cached.previous_index)) == cached.previous_index,
                    "pathIndex": int(live["pathIndex"]) == cached.index,
                    "targetAcceptable": bool(live["targetAcceptable"]) == cached.target_acceptable,
                    "contactValid": bool(live["contactValid"]) == cached.contact_valid,
                    "workspaceValid": bool(live["workspaceValid"]) == cached.workspace_valid,
                    "invalidMotion": bool(live["invalidMotion"]) == cached.invalid_motion,
                    "boundaryViolation": bool(live["patchBoundaryViolation"]) == cached.boundary_violation,
                    "explicitStopSuccess": bool(live["explicitStopSuccess"]) == cached.explicit_stop_success,
                    "terminated": bool(live["terminated"]) == cached.terminated,
                    "truncated": bool(live["truncated"]) == cached.truncated,
                    "reward": abs(float(live["reward"]) - cached.reward) <= 1e-5,
                    "observationWithinRmsTolerance": observation_rms <= args.observation_rms_tolerance,
                }
                record = {
                    "scenarioIndex": int(frozen_scenario["scenarioIndex"]),
                    "trajectorySeed": int(frozen_scenario["trajectorySeed"]),
                    "probeStart": start,
                    "step": steps_taken,
                    "action": action,
                    "cachedIndex": cached.index,
                    "liveIndex": int(live["pathIndex"]),
                    "cachedReward": cached.reward,
                    "liveReward": float(live["reward"]),
                    "observationHashExact": live_hash == observation_hashes[cached.index],
                    "observationRms": observation_rms,
                    "comparisons": comparisons,
                }
                steps.append(record)
                if not all(comparisons.values()):
                    failures.append(record)
                cached_index = cached.index
                response = live
                if cached.done or bool(response["done"]):
                    break
            probes.append({
                "scenarioIndex": int(frozen_scenario["scenarioIndex"]),
                "trajectorySeed": int(frozen_scenario["trajectorySeed"]),
                "configurationSha256": frozen_scenario["configurationSha256"],
                "labelsSha256": frozen_scenario["labelsSha256"],
                "startIndex": start,
                "steps": steps,
            })

    observation_rms_values = [step["observationRms"] for probe in probes for step in probe["steps"]]
    exact_hash_matches = sum(bool(step["observationHashExact"]) for probe in probes for step in probe["steps"])
    report = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "environmentContractVersion": CONTRACT_VERSION,
        "frozenManifest": str(args.manifest.resolve()),
        "frozenManifestSha256": frozen.get("manifestSha256"),
        "scenarioCount": min(args.scenarios, len(frozen["scenarios"])),
        "probeCount": len(probes),
        "stepComparisons": sum(len(item["steps"]) for item in probes),
        "failureCount": len(failures),
        "pass": not failures,
        "observationVariability": {
            "exactHashMatches": exact_hash_matches,
            "comparisons": len(observation_rms_values),
            "rmsMean": float(np.mean(observation_rms_values)) if observation_rms_values else None,
            "rmsMax": float(np.max(observation_rms_values)) if observation_rms_values else None,
            "rmsTolerance": args.observation_rms_tolerance,
        },
        "method": "Exact frozen Unity scenarios; paired action sequences checked against Python contract-v3 cache transitions. Renderer stochasticity is documented by exact hashes and gated by 32x32 grayscale RMS.",
        "probes": probes,
        "failures": failures,
    }
    save_json(args.output, report)
    print(json.dumps({key: report[key] for key in ("scenarioCount", "probeCount", "stepComparisons", "failureCount", "pass")}, indent=2))
    if failures:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
