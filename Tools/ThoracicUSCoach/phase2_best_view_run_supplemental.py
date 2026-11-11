"""Build a whole-session Phase-2 supplemental training report.

Unity evaluator labels are copied into this offline-only report.  The runtime
recognizer continues to consume ultrasound pixels alone.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from active2d_contract import project_root


def build_report(base_report: Path, run_roots: list[Path], output: Path) -> dict:
    root = project_root()
    report = json.loads(base_report.read_text(encoding="utf-8"))
    cases = list(report["cases"])
    for run_index, run_root in enumerate(run_roots):
        actor_path = run_root / "phase2_best_view" / "actor_result.json"
        evaluator_path = (run_root / "phase2_best_view" / "evaluator_only"
                          / "final_evaluation.json")
        actor = json.loads(actor_path.read_text(encoding="utf-8"))
        evaluator = json.loads(evaluator_path.read_text(encoding="utf-8"))
        actor_by_index = {item["index"]: item for item in actor["observations"]}
        observations = []
        for item in evaluator["observations"]:
            actor_item = actor_by_index[item["index"]]
            image = root / actor_item["image"]
            digest = hashlib.sha256(image.read_bytes()).hexdigest()
            if digest != actor_item["imageSha256"]:
                raise ValueError(f"Image integrity mismatch: {image}")
            observations.append({
                "repeatIndex": item["index"],
                "image": actor_item["image"],
                "imageSha256": digest,
                "contactValid": True,
                "workspaceValid": True,
                "score": {"total": item["targetScore"]},
                "targetAcceptable": bool(
                    item["t7LevelAcceptable"]
                    and item["targetScore"] >= evaluator["acceptableScoreFloor"]),
            })
        cases.append({
            "caseId": f"phase2_tilt_train_session_{run_index:02d}",
            "family": run_root.name,
            "sourceFrameIndex": -1,
            "selectionReason": "whole Unity development session; joint T7 and quality label",
            "observations": observations,
        })
    merged = {
        **{key: value for key, value in report.items() if key != "cases"},
        "protocolId": "PHASE2_BEST_VIEW_TILTED_TRANSVERSE_SUPPLEMENTAL_V1",
        "cases": cases,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(merged, indent=2) + "\n", encoding="utf-8")
    return merged


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-report", type=Path, required=True)
    parser.add_argument("--run-root", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = project_root()
    base = args.base_report if args.base_report.is_absolute() else root / args.base_report
    runs = [path if path.is_absolute() else root / path for path in args.run_root]
    output = args.output if args.output.is_absolute() else root / args.output
    report = build_report(base, runs, output)
    new_cases = report["cases"][-len(runs):]
    print(json.dumps({
        "newCases": len(new_cases),
        "newFrames": sum(len(case["observations"]) for case in new_cases),
        "newPositives": sum(
            observation["targetAcceptable"]
            for case in new_cases for observation in case["observations"]),
        "output": str(output),
    }, indent=2))


if __name__ == "__main__":
    main()
