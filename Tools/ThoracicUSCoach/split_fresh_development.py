from __future__ import annotations

import argparse
import json
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path

from coach_data import save_json
from contract_v3 import canonical_json_sha256


def subset(source: dict[str, object], scenarios: list[dict[str, object]], role: str) -> dict[str, object]:
    payload = deepcopy(source)
    payload["createdUtc"] = datetime.now(timezone.utc).isoformat()
    payload["scenarios"] = scenarios
    payload["selectionUse"] = role
    payload["selectionPolicy"] = (
        "Calibration may choose STOP operating points."
        if role == "development-calibration"
        else "Disjoint development check; evaluate frozen candidates only and do not retune on outcomes."
    )
    starts = [start for scenario in scenarios for start in scenario["starts"]]
    payload["coverageAudit"] = {
        "geometries": len(scenarios),
        "uniqueScenarioStarts": len(starts),
        "recognitionStarts": sum(bool(start["initiallyAcceptable"]) for start in starts),
        "acquisitionStarts": sum(not bool(start["initiallyAcceptable"]) for start in starts),
    }
    payload.pop("manifestSha256", None)
    payload["sourceManifestSha256"] = source["manifestSha256"]
    payload["manifestSha256"] = canonical_json_sha256(
        {key: value for key, value in payload.items() if key != "createdUtc"}
    )
    return payload


def main() -> None:
    parser = argparse.ArgumentParser(description="Predeclare disjoint halves of a fresh development manifest")
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--calibration-output", type=Path, required=True)
    parser.add_argument("--check-output", type=Path, required=True)
    args = parser.parse_args()
    source = json.loads(args.source.read_text(encoding="utf-8"))
    scenarios = list(source["scenarios"])
    if len(scenarios) != 12:
        raise SystemExit("Expected exactly 12 predeclared fresh development geometries")
    calibration = subset(source, scenarios[:6], "development-calibration")
    check = subset(source, scenarios[6:], "development-check")
    save_json(args.calibration_output, calibration)
    save_json(args.check_output, check)
    print(
        json.dumps(
            {
                "source": source["manifestSha256"],
                "calibration": calibration["manifestSha256"],
                "check": check["manifestSha256"],
                "calibrationGeometries": 6,
                "checkGeometries": 6,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
