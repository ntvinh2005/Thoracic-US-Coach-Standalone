from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from coach_data import DEFAULT_DATASET_RUN_ID, project_root


def main() -> None:
    parser = argparse.ArgumentParser(description="Run/resume the predeclared cached-PPO seed matrix sequentially")
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--seeds", default="13,37,101,211,307")
    parser.add_argument("--transition-budget", type=int, default=50_000)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    seeds = [int(value) for value in args.seeds.split(",")]
    output_dir = args.output_dir or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{args.dataset_run_id}"
    metrics_dir = project_root() / f"Artifacts/ThoracicUSCoach/metrics/{args.dataset_run_id}"
    output_dir.mkdir(parents=True, exist_ok=True)
    metrics_dir.mkdir(parents=True, exist_ok=True)
    completed = []
    for seed in seeds:
        source = output_dir / f"bc_history_linear16_seed{seed}.json"
        output = output_dir / f"ppo_cached_history_seed{seed}.json"
        state = output_dir / f"ppo_cached_history_seed{seed}_state.json"
        metrics = metrics_dir / f"ppo_cached_history_seed{seed}.json"
        command = [
            sys.executable,
            str(Path(__file__).with_name("ppo_cached_pretrain.py")),
            "--dataset-run-id", args.dataset_run_id,
            "--bc", str(source),
            "--transition-budget", str(args.transition_budget),
            "--transitions-per-update", "500",
            "--episodes-per-update", "24",
            "--max-steps", "35",
            "--seed", str(seed),
            "--gamma", "0.95",
            "--gae-lambda", "0.95",
            "--clip", "0.15",
            "--lr", "0.001",
            "--value-lr", "0.01",
            "--update-epochs", "4",
            "--run-id", "main_cpu_v3_cached_history",
            "--output", str(output),
            "--metrics-output", str(metrics),
            "--state-output", str(state),
        ]
        if state.is_file():
            saved = json.loads(state.read_text(encoding="utf-8"))
            if int(saved.get("totalTransitions", 0)) < args.transition_budget:
                command.extend(("--resume", str(state)))
            elif output.is_file():
                print(json.dumps({"phase": "cached-ppo", "seed": seed, "status": "already-complete"}), flush=True)
                completed.append({"seed": seed, "checkpoint": str(output), "resumed": False})
                continue
        print(json.dumps({"phase": "cached-ppo", "seed": seed, "source": str(source), "output": str(output)}), flush=True)
        subprocess.run(command, check=True)
        completed.append({"seed": seed, "checkpoint": str(output), "resumed": "--resume" in command})
    print(json.dumps({"completed": completed}, indent=2))


if __name__ == "__main__":
    main()
