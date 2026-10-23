from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from coach_data import DEFAULT_DATASET_RUN_ID, project_root


def main() -> None:
    parser = argparse.ArgumentParser(description="Evaluate the predeclared candidate matrix on frozen development selection")
    parser.add_argument("--dataset-run-id", default=DEFAULT_DATASET_RUN_ID)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--seeds", default="13,37,101,211,307")
    parser.add_argument("--checkpoint-dir", type=Path)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    seeds = [int(value) for value in args.seeds.split(",")]
    checkpoint_dir = args.checkpoint_dir or project_root() / f"Artifacts/ThoracicUSCoach/checkpoints/{args.dataset_run_id}"
    output_dir = args.output_dir or project_root() / f"Artifacts/ThoracicUSCoach/metrics/{args.dataset_run_id}/development_selection"
    output_dir.mkdir(parents=True, exist_ok=True)
    evaluator = str(Path(__file__).with_name("evaluate_live.py"))
    jobs: list[tuple[str, list[str]]] = [
        ("always_stop", ["--baseline", "always-stop"]),
        ("teacher", ["--teacher-only"]),
        ("scan_stop_current_linear_seed13", [
            "--baseline", "scan-stop", "--checkpoint", str(checkpoint_dir / "bc_current_linear16_seed13.json")
        ]),
    ]
    for seed in seeds:
        for candidate in ("current_linear16", "history_linear16", "current_mlp64_16"):
            jobs.append((f"bc_{candidate}_seed{seed}", [
                "--checkpoint", str(checkpoint_dir / f"bc_{candidate}_seed{seed}.json")
            ]))
        jobs.append((f"ppo_best_seed{seed}", [
            "--checkpoint", str(checkpoint_dir / f"ppo_live_history_seed{seed}_best_dev.json")
        ]))
    completed = []
    for label, extra in jobs:
        output = output_dir / f"{label}.json"
        if output.is_file():
            print(json.dumps({"phase": "dev-selection", "candidate": label, "status": "already-complete"}), flush=True)
            completed.append(label)
            continue
        command = [
            sys.executable, evaluator,
            "--manifest", str(args.manifest),
            "--max-steps", "35",
            "--output", str(output),
            *extra,
        ]
        print(json.dumps({"phase": "dev-selection", "candidate": label, "output": str(output)}), flush=True)
        subprocess.run(command, check=True)
        completed.append(label)
    print(json.dumps({"completed": completed}, indent=2))


if __name__ == "__main__":
    main()
