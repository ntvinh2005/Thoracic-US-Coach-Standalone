from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

from coach_data import DEFAULT_EXPERIMENT_ID, dataset_fingerprint, load_frames, project_root, save_json


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description="Freeze final-test identities before model selection")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument(
        "--output",
        type=Path,
        default=project_root() / "Artifacts/ThoracicUSCoach/final_test_lock_diverse_v2.json",
    )
    parser.add_argument("--verify", action="store_true", help="Verify an existing lock without rewriting it")
    args = parser.parse_args()

    root = project_root().resolve()
    test = [frame for frame in load_frames(experiment_id=args.experiment_id) if frame.split == "test"]
    if not test:
        raise SystemExit("No complete test frames to lock")
    manifests = sorted({frame.manifest for frame in test})
    identity = [
        {
            "path": manifest.relative_to(root).as_posix(),
            "sha256": file_sha256(manifest),
            "sweepId": next(frame.sweep_id for frame in test if frame.manifest == manifest),
            "evaluationGroup": next(frame.evaluation_group for frame in test if frame.manifest == manifest),
            "configurationFamily": next(frame.configuration_family for frame in test if frame.manifest == manifest),
            "randomSeed": next(frame.random_seed for frame in test if frame.manifest == manifest),
        }
        for manifest in manifests
    ]
    current = {
        "schemaVersion": 1,
        "experimentId": args.experiment_id,
        "purpose": "Immutable final held-out/stress identities; created before model selection or final evaluation",
        "datasetFingerprintAllSplits": dataset_fingerprint(load_frames(experiment_id=args.experiment_id)),
        "testFingerprint": dataset_fingerprint(test),
        "testSweeps": len(manifests),
        "testFrames": len(test),
        "evaluationGroups": dict(Counter(frame.evaluation_group for frame in test)),
        "manifests": identity,
    }

    if args.verify:
        locked = json.loads(args.output.read_text(encoding="utf-8"))
        comparable = {key: value for key, value in locked.items() if key != "lockedUtc"}
        if comparable != current:
            raise SystemExit("Final-test lock verification FAILED: identities or bytes changed")
        print(json.dumps({"verified": True, "testSweeps": len(manifests), "testFrames": len(test)}, indent=2))
        return

    if args.output.exists():
        raise SystemExit(f"Refusing to overwrite existing final-test lock: {args.output}")
    current["lockedUtc"] = datetime.now(timezone.utc).isoformat()
    save_json(args.output, current)
    print(json.dumps({"locked": True, "path": str(args.output), "testSweeps": len(manifests), "testFrames": len(test)}, indent=2))


if __name__ == "__main__":
    main()
