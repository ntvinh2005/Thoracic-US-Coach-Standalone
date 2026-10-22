from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

from coach_data import project_root, save_json


def file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_lock(manifest: Path, lock: Path) -> dict[str, object]:
    payload = json.loads(lock.read_text(encoding="utf-8"))
    actual = file_sha256(manifest)
    if payload.get("manifestFileSha256") != actual:
        raise RuntimeError("Frozen scenario lock verification FAILED: manifest bytes changed")
    frozen = json.loads(manifest.read_text(encoding="utf-8"))
    if payload.get("manifestIdentitySha256") != frozen.get("manifestSha256"):
        raise RuntimeError("Frozen scenario lock verification FAILED: identity changed")
    return {"verified": True, "manifestFileSha256": actual, "manifestIdentitySha256": frozen.get("manifestSha256")}


def main() -> None:
    parser = argparse.ArgumentParser(description="Create or verify a frozen scenario-manifest lock")
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/final_scenario_lock_contract_v3.json")
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    if args.verify:
        print(json.dumps(verify_lock(args.manifest, args.output), indent=2))
        return
    if args.output.exists():
        raise SystemExit(f"Refusing to overwrite existing lock: {args.output}")
    frozen = json.loads(args.manifest.read_text(encoding="utf-8"))
    payload = {
        "schemaVersion": 1,
        "lockedUtc": datetime.now(timezone.utc).isoformat(),
        "manifest": str(args.manifest),
        "manifestFileSha256": file_sha256(args.manifest),
        "manifestIdentitySha256": frozen.get("manifestSha256"),
        "group": frozen.get("group"),
        "geometries": len(frozen.get("scenarios", [])),
    }
    save_json(args.output, payload)
    print(json.dumps(payload, indent=2))


if __name__ == "__main__":
    main()
