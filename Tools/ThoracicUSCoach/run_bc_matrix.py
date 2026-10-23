from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from coach_data import DEFAULT_DATASET_RUN_ID, project_root


def main() -> None:
    parser = argparse.ArgumentParser(description="Run the predeclared CPU BC model/seed matrix sequentially")
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--seeds", default="13,37,101,211,307")
    parser.add_argument("--epochs", type=int, default=500)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    seeds = [int(value) for value in args.seeds.split(",")]
    output_dir = args.output_dir or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{args.dataset_run_id}"
    output_dir.mkdir(parents=True, exist_ok=True)
    configurations = (
        ("current_linear16", "current", "linear", []),
        ("history_linear16", "history", "linear", []),
        ("current_mlp64_16", "current", "mlp", ["--hidden-size", "64"]),
    )
    completed = []
    for seed in seeds:
        for name, observation, architecture, extra in configurations:
            output = output_dir / f"bc_{name}_seed{seed}.json"
            command = [
                sys.executable,
                str(Path(__file__).with_name("train_bc_policy.py")),
                "--dataset-run-id", args.dataset_run_id,
                "--observation", observation,
                "--architecture", architecture,
                "--feature-size", "16",
                "--epochs", str(args.epochs),
                "--seed", str(seed),
                "--output", str(output),
                *extra,
            ]
            print(json.dumps({"phase": "bc", "candidate": name, "seed": seed, "output": str(output)}), flush=True)
            subprocess.run(command, check=True)
            completed.append({"candidate": name, "seed": seed, "output": str(output)})
    print(json.dumps({"completed": completed}, indent=2))


if __name__ == "__main__":
    main()
