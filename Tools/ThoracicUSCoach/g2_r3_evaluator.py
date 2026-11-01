"""Evaluator-only anatomical scoring for already frozen G2-R3 predictions."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from g2_r3_confirmation import verify_blind_outputs
from g2_r3_offline_audit import reference_coordinates, score, write_json


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--blind-root", type=Path, required=True)
    parser.add_argument("--r1-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    source_root = args.source_root.resolve()
    blind_root = args.blind_root.resolve()
    if not verify_blind_outputs(blind_root):
        raise ValueError("Blind output hash verification failed")
    candidates = json.loads((blind_root / "event_candidates.json").read_text())["accepted"]
    references, _ = reference_coordinates(source_root, args.r1_root.resolve())
    result = score(candidates, references)
    result["contract"] = "thoracic_g2_r3_frozen_anatomical_correspondence_v1"
    result["blindFreezeVerifiedBeforeEvaluation"] = True
    metrics = result["metrics"]
    result["g2"] = "PASS" if (
        metrics["truePositive"] == 7
        and metrics["falsePositive"] == 0
        and metrics["falseNegative"] == 0
    ) else "FAIL"
    result["g3"] = "PASS" if (
        result["g2"] == "PASS" and len(candidates) == 7
    ) else "NOT_RUN_OR_FAIL"
    output_root = args.output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=False)
    write_json(output_root / "anatomical_correspondence.json", result)
    write_json(output_root / "negative_control_results.json", {
        "contract": "thoracic_g2_r3_locked_extra_event_control_v1",
        "acceptedEventCount": len(candidates),
        "unmatchedAcceptedEvents": metrics["falsePositive"],
        "wrongT7Acceptance": bool(result["g3"] == "PASS" and metrics["falsePositive"] > 0),
        "interpretation": (
            "The actor was never given the evaluator-only extra-recess coordinate or identity."
        ),
    })
    print(json.dumps({"metrics": metrics, "g2": result["g2"], "g3": result["g3"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
