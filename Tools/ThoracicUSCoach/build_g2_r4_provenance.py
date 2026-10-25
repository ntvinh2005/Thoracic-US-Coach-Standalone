from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    repo = Path(__file__).resolve().parents[2]
    r2_path = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r2_20261024" / "family_usage_ledger.json"
    a2_manifest_path = repo / "Tools" / "ThoracicUSCoach" / "experiments" / "g2_r3_a2_exact_pose_v1.json"
    r4_protocol_path = repo / "Tools" / "ThoracicUSCoach" / "experiments" / "g2_r4_a_representation_v1.json"
    output = repo / "Artifacts" / "ThoracicUSCoach" / "active2d" / "evaluation" / "g2_r4_20261025" / "g2_r4_family_usage_ledger.json"
    r2, a2 = load_json(r2_path), load_json(a2_manifest_path)
    a2_cases: dict[str, list[int]] = {}
    for item in a2["cases"]:
        a2_cases.setdefault(item["family"], []).append(int(item["sourceFrameIndex"]))
    rows = []
    for source in r2["rows"]:
        row = dict(source)
        history = list(source.get("priorUsage", []))
        if source["family"] in a2_cases:
            history.append({
                "study": "G2-R3-A2 exact-pose confirmatory audit",
                "role": "diagnostic",
                "status": "consumed",
                "frameIndices": sorted(a2_cases[source["family"]]),
            })
            history.append({
                "study": "G2-R4-A learned representation diagnostic",
                "role": "training/model-selection diagnostic",
                "status": "consumed-not-independent",
                "frameIndices": sorted(a2_cases[source["family"]]),
            })
        row["usageHistory"] = history
        row["eligibleForFutureIndependentConfirmation"] = source["g2R2Role"] == "unused" and not history
        row["lockedTestStatus"] = "SEALED_UNTOUCHED" if source["g2R2Role"] == "locked_test" else "NOT_LOCKED"
        rows.append(row)
    payload = {
        "schemaVersion": 1,
        "taskId": "G2-R4",
        "datasetRunId": r2["datasetRunId"],
        "sourceFamilyCount": len(rows),
        "sourceLedger": str(r2_path.relative_to(repo)).replace("\\", "/"),
        "sourceLedgerSha256": sha256(r2_path),
        "r3A2SelectionManifestSha256": sha256(a2_manifest_path),
        "r4ProtocolSha256": sha256(r4_protocol_path),
        "newR4Capture": {"status": "NOT_RUN", "reason": "R4-A decision was not PASS"},
        "lockedFamiliesAccessedByR4": [],
        "rows": rows,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "families": len(rows), "r4ConsumedFamilies": len(a2_cases), "sealedLockedFamilies": sum(row["lockedTestStatus"] == "SEALED_UNTOUCHED" for row in rows)}, indent=2))


if __name__ == "__main__":
    main()
