from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path

from PIL import Image

from active2d_contract import CONTRACT, EXPERIMENT_ID, project_root
from active2d_data import load_active2d_frames


def main() -> None:
    parser = argparse.ArgumentParser(description="Integrity and no-leakage audit for active2d_image_only_v1")
    parser.add_argument("--dataset-run-id", required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = args.output or project_root() / f"Artifacts/ThoracicUSCoach/active2d/audit_{args.dataset_run_id}.json"
    frames = load_active2d_frames(args.dataset_run_id)
    if not frames:
        raise SystemExit("No complete Active 2D frames found")

    failures: list[str] = []
    hashes = [frame.image_sha256 for frame in frames]
    if len(hashes) != len(set(hashes)):
        failures.append("exact duplicate clean ultrasound images found")
    dimensions = Counter()
    for frame in frames:
        with Image.open(frame.image) as image:
            dimensions[f"{image.width}x{image.height}"] += 1
    if set(dimensions) != {"1024x1024"}:
        failures.append("capture source is not uniformly 1024x1024")
    family_splits: dict[str, set[str]] = {}
    for frame in frames:
        family_splits.setdefault(frame.family, set()).add(frame.split)
    leaking = {family: sorted(splits) for family, splits in family_splits.items() if len(splits) > 1}
    if leaking:
        failures.append("episode/configuration families span splits")
    if any(not frame.contact_valid for frame in frames):
        failures.append("contact-invalid observations found")
    if any(not frame.workspace_valid for frame in frames):
        failures.append("workspace-invalid observations found")
    partials = list((project_root() / "Artifacts/ThoracicUSCoach/active2d_dataset").glob("*/*.partial"))
    if partials:
        failures.append("partial episode directories remain")

    episodes = {frame.episode_id for frame in frames}
    report = {
        "schemaVersion": 1,
        "contract": CONTRACT,
        "experimentId": EXPERIMENT_ID,
        "datasetRunId": args.dataset_run_id,
        "passed": not failures,
        "failures": failures,
        "summary": {
            "episodes": len(episodes),
            "frames": len(frames),
            "uniqueImages": len(set(hashes)),
            "splits": dict(Counter(frame.split for frame in frames)),
            "families": len(family_splits),
            "levels": dict(Counter(frame.level for frame in frames)),
            "actions": dict(Counter(frame.issued_action for frame in frames)),
            "dimensions": dict(dimensions),
            "contactViolations": sum(not frame.contact_valid for frame in frames),
            "workspaceViolations": sum(not frame.workspace_valid for frame in frames),
        },
        "familySplitLeakage": leaking,
        "streamActorFields": ["clean ultrasound image bytes", "episode reset", "policy-owned memory/action history"],
        "privilegedAnnotationFieldsAreTrainingEvaluationOnly": True,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    if failures:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
