from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def analyze_episode(root: Path, summary: dict[str, Any]) -> dict[str, Any]:
    raw_path = root / summary["rawResult"]
    privileged_path = root / summary["privilegedTrace"]
    raw = load(raw_path)
    privileged = load(privileged_path)
    frames = {int(frame["index"]): frame for frame in privileged.get("frames", [])}
    trace = raw.get("trace", [])
    comparisons: list[dict[str, Any]] = []
    for row in trace:
        if not row.get("candidateSelected"):
            continue
        step = int(row["step"])
        current = frames.get(step)
        following = frames.get(step + 1)
        if current is None or following is None or not row.get("accepted"):
            continue
        action = str(row.get("action", "")).upper()
        predicted = row.get("candidateMedialValue") if action == "MEDIAL" else row.get("candidateLateralValue")
        observed = float(following["finalTargetScore"]) - float(current["finalTargetScore"])
        comparisons.append({
            "step": step,
            "action": action,
            "predictedDelta": float(predicted),
            "observedDelta": observed,
            "absoluteError": abs(float(predicted) - observed),
            "signCorrect": (float(predicted) >= 0.0) == (observed >= 0.0),
        })
    result = dict(summary)
    result["rawResultSha256"] = sha256(raw_path)
    result["privilegedTraceSha256"] = sha256(privileged_path)
    result["predictionComparisons"] = comparisons
    result["predictionMae"] = (
        sum(item["absoluteError"] for item in comparisons) / len(comparisons)
        if comparisons else None
    )
    result["predictionSignAgreement"] = (
        sum(item["signCorrect"] for item in comparisons) / len(comparisons)
        if comparisons else None
    )
    result["traceDerived"] = {
        "candidateChoices": sum(bool(item.get("candidateSelected")) for item in trace),
        "fallbacks": sum(bool(item.get("candidateFallback")) for item in trace),
        "accepted": sum(bool(item.get("accepted")) for item in trace),
        "rejected": sum(not bool(item.get("accepted")) for item in trace if item.get("action") not in {"STOP", "PHASEHANDOFF"}),
        "safetyFailures": sum(bool(item.get("safetyFailure")) for item in trace),
    }
    return result


def evaluate(root: Path, output_directory: Path) -> dict[str, Any]:
    manifest_path = root / "Tools/ThoracicUSCoach/experiments/u08_p1_paired_v1.json"
    manifest = load(manifest_path)
    reports = {
        policy: load(output_directory / f"u08_p1_{policy}_unity.json")
        for policy in ("raster", "candidate")
    }
    for policy, report in reports.items():
        if not report.get("complete") or report.get("infrastructureFailure"):
            raise RuntimeError(f"{policy} Unity report is incomplete: {report.get('infrastructureFailure')}")
        if report.get("policy") != policy or len(report.get("episodes", [])) != 2:
            raise RuntimeError(f"{policy} report does not contain exactly two matching episodes")
        if report.get("manifestSha256") != sha256(manifest_path):
            raise RuntimeError(f"{policy} report manifest hash mismatch")
    episodes = [
        analyze_episode(root, episode)
        for policy in ("raster", "candidate")
        for episode in reports[policy]["episodes"]
    ]
    if len(episodes) != 4:
        raise RuntimeError("U08-P1 must contain exactly four total episodes")
    pairs: list[dict[str, Any]] = []
    for frozen in manifest["starts"]:
        start_id = frozen["startId"]
        baseline = next(item for item in episodes if item["startId"] == start_id and item["policy"] == "raster")
        candidate = next(item for item in episodes if item["startId"] == start_id and item["policy"] == "candidate")
        for item in (baseline, candidate):
            audit = item["start"]
            if audit.get("expectedConfigSha256") != frozen["configSha256"] or not audit.get("valid"):
                raise RuntimeError(f"start equality/provenance failed for {start_id}/{item['policy']}")
        pairs.append({
            "startId": start_id,
            "configSha256": frozen["configSha256"],
            "rasterSteps": baseline["phaseTwoSteps"],
            "candidateSteps": candidate["phaseTwoSteps"],
            "stepDelta": candidate["phaseTwoSteps"] - baseline["phaseTwoSteps"],
            "relativeReduction": (
                (baseline["phaseTwoSteps"] - candidate["phaseTwoSteps"]) / baseline["phaseTwoSteps"]
                if baseline["phaseTwoSteps"] else 0.0
            ),
            "rasterSuccess": baseline["acquisitionSuccess"],
            "candidateSuccess": candidate["acquisitionSuccess"],
            "rasterWrongStop": baseline["wrongStop"],
            "candidateWrongStop": candidate["wrongStop"],
            "rasterFirstTargetStep": baseline["firstTargetObservationStep"],
            "candidateFirstTargetStep": candidate["firstTargetObservationStep"],
        })
    unsafe = any(
        item["safetyFailures"] or item["contactViolations"] or item["workspaceViolations"]
        or not item["finalContactValid"] or not item["finalWorkspaceValid"]
        for item in episodes
    )
    complete = all(item["completed"] for item in episodes)
    candidate_episodes = [item for item in episodes if item["policy"] == "candidate"]
    actual_candidate_use = sum(item["candidateChoices"] for item in candidate_episodes) > 0
    baseline_success = sum(item["acquisitionSuccess"] for item in episodes if item["policy"] == "raster")
    candidate_success = sum(item["acquisitionSuccess"] for item in candidate_episodes)
    baseline_steps = sum(item["phaseTwoSteps"] for item in episodes if item["policy"] == "raster")
    candidate_steps = sum(item["phaseTwoSteps"] for item in candidate_episodes)
    reduction = (baseline_steps - candidate_steps) / baseline_steps if baseline_steps else 0.0
    wrong_stop_regression = any(
        pair["candidateWrongStop"] and not pair["rasterWrongStop"] for pair in pairs
    )
    if unsafe:
        decision = "PILOT_UNSAFE"
    elif not complete or not actual_candidate_use:
        decision = "PILOT_INCONCLUSIVE"
    elif candidate_success >= baseline_success and reduction >= 0.15 and not wrong_stop_regression:
        decision = "PILOT_PROMISING"
    else:
        decision = "PILOT_NO_IMPROVEMENT"
    return {
        "schemaVersion": 1,
        "protocolId": manifest["protocolId"],
        "decision": decision,
        "scope": "two-pair development smoke test; no statistical significance claim",
        "manifest": str(manifest_path.relative_to(root)).replace("\\", "/"),
        "manifestSha256": sha256(manifest_path),
        "frozenArtifacts": {
            "phaseOne": manifest["phaseOneCheckpointSha256"],
            "stop": manifest["stopCheckpointSha256"],
            "u08Action": manifest["actionCheckpointSha256"],
            "policyServer": manifest["policyServerSha256"],
        },
        "episodes": episodes,
        "pairs": pairs,
        "aggregate": {
            "rasterSuccesses": baseline_success,
            "candidateSuccesses": candidate_success,
            "rasterSteps": baseline_steps,
            "candidateSteps": candidate_steps,
            "relativeStepReduction": reduction,
            "candidateChoices": sum(item["candidateChoices"] for item in candidate_episodes),
            "fallbacks": sum(item["fallbacks"] for item in candidate_episodes),
            "unsafe": unsafe,
            "wrongStopRegression": wrong_stop_regression,
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output-directory", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    output = args.output_directory or root / "Artifacts/ThoracicUSCoach/active2d/evaluation/u08_p1"
    report = evaluate(root, output)
    output.mkdir(parents=True, exist_ok=True)
    destination = output / "u08_p1_master_report.json"
    destination.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"decision": report["decision"], "report": str(destination)}, indent=2))


if __name__ == "__main__":
    main()
