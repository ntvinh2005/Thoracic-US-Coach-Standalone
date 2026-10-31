#!/usr/bin/env python3
"""Recovery audit and locked offline evaluation for P1-R3."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

from PIL import Image, ImageDraw

from position_aware_relocalization import block_ssim, load_image_descriptor


ORIGINAL_SSIM_THRESHOLD = 0.9710001053543619
POSE_TOLERANCE_MM = 0.25
ORIENTATION_TOLERANCE_DEG = 0.5
CONFIRMATION_STARTS_MM = [-24.0, -33.0, -39.0]


def _load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _save(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _utc() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def capture_fresh(step: dict, privileged: dict) -> bool:
    return bool(
        step.get("frameIntegrityValid")
        and step.get("poseEpoch") == step.get("producerPoseEpoch")
        and int(step.get("producerUnityFrame", -1)) > int(step.get("poseAppliedUnityFrame", -1))
        and step.get("captureUnityFrame") == step.get("producerUnityFrame")
        and int(step.get("producerScanSerial", -1)) > 0
        and privileged.get("provenanceMatched")
    )


def pose_pass(step: dict) -> bool:
    return bool(
        abs(float(step["longitudinalErrorMm"])) <= POSE_TOLERANCE_MM
        and abs(float(step["lateralErrorMm"])) <= POSE_TOLERANCE_MM
        and float(step["fullContactPointErrorMm"]) <= POSE_TOLERANCE_MM
        and float(step["orientationErrorDeg"]) <= ORIENTATION_TOLERANCE_DEG
        and step.get("contactValid")
        and step.get("workspaceValid")
        and not step.get("safetyFailure")
    )


def classify_confirmation(position: bool, fresh: bool, ssim_pass: bool,
                          evaluator_t7: bool) -> str:
    return "PASS" if position and fresh and ssim_pass and evaluator_t7 else "FAIL"


def freeze_eligible(summary: dict) -> bool:
    runs = summary.get("runs", [])
    navigation = [item for item in runs if item.get("kind") == "NAVIGATION"]
    sensitivity = [item for item in runs if item.get("kind") == "SENSITIVITY"]
    return bool(
        len(runs) == 3
        and len(navigation) == 2
        and len(sensitivity) == 1
        and all(item.get("positionPrecision") == "PASS" for item in navigation)
        and all(item.get("imageIntegrity") == "PASS" for item in runs)
        and sum(int(item.get("acceptedFineMovements", 0)) for item in runs) > 0
        and sensitivity[0].get("centerReturn") == "PASS"
    )


def audit(root: Path, repo: Path) -> dict:
    registry_path = root / "development_run_registry.json"
    if registry_path.exists():
        registry = _load(registry_path)
        development = len(registry.get("developmentRuns", []))
        confirmation = len(registry.get("confirmationRuns", []))
        state = registry.get("state", "IMPLEMENTATION_IN_PROGRESS")
    else:
        development = 0
        confirmation = 0
        state = "NOT_STARTED"
    value = {
        "contract": "thoracic_p1_r3_recovery_state_audit_v1",
        "generatedUtc": _utc(),
        "resumedState": state,
        "localBranch": _git(repo, ["branch", "--show-current"]),
        "localHead": _git(repo, ["rev-parse", "HEAD"]),
        "remoteReviewedBaseline": "5094d560e14518e0e5b0f9d6bd1dd577bd23ca40",
        "developmentRunsConsumed": development,
        "confirmationRunsConsumed": confirmation,
        "confirmationContamination": confirmation > 0,
        "implementedBeforeRecovery": [],
        "testsFoundBeforeRecovery": [],
        "exactRemainingBlocker": "No P1-R3 implementation, registry, run, freeze, or confirmation artifact existed locally.",
    }
    _save(root / "state_audit.json", value)
    return value


def _git(repo: Path, args: list[str]) -> str:
    import subprocess
    completed = subprocess.run(["git", *args], cwd=repo, check=True,
                               capture_output=True, text=True)
    return completed.stdout.strip()


def analyze_development(root: Path) -> dict:
    registry = _load(root / "development_run_registry.json")
    rows: list[dict] = []
    sensitivity_rows: list[dict] = []
    for entry in registry.get("developmentRuns", []):
        run_root = root / entry["directory"]
        actor = _load(run_root / "trajectory_actor.json")
        evaluator = _load(run_root / "evaluator_only" / "trajectory_privileged.json")
        calibration = _load(run_root / "calibration_manifest.json")
        images = [item for item in actor["steps"] if item.get("image")]
        fresh = all(capture_fresh(step, evaluator["steps"][actor["steps"].index(step)])
                    for step in images)
        hashes = all(_sha256(root / step["image"]) == step["imageSha256"] for step in images)
        if entry["scenario"] == "FINE_MOTION_SENSITIVITY":
            reference = root / calibration["referenceImage"]
            for step in images:
                sensitivity_rows.append({
                    "runId": entry["runId"],
                    "step": step["step"],
                    "longitudinalErrorMm": float(step["longitudinalErrorMm"]),
                    "fullContactPointErrorMm": float(step["fullContactPointErrorMm"]),
                    "ssimToCalibration": block_ssim(
                        load_image_descriptor(reference), load_image_descriptor(root / step["image"])),
                    "fresh": capture_fresh(step, evaluator["steps"][actor["steps"].index(step)]),
                })
            final = actor["steps"][-1]
            rows.append({
                "runId": entry["runId"], "kind": "SENSITIVITY",
                "status": entry["status"],
                "acceptedFineMovements": actor["acceptedFineMovements"],
                "centerReturn": "PASS" if pose_pass(final) else "FAIL",
                "finalFullContactPointErrorMm": float(final["fullContactPointErrorMm"]),
                "imageIntegrity": "PASS" if fresh and hashes else "FAIL",
            })
        else:
            final = actor["steps"][-1]
            reference = root / calibration["referenceImage"]
            returned = root / final["image"]
            similarity = block_ssim(load_image_descriptor(reference), load_image_descriptor(returned))
            rows.append({
                "runId": entry["runId"], "kind": "NAVIGATION",
                "scenario": entry["scenario"], "status": entry["status"],
                "acceptedCoarseMovements": actor["acceptedCoarseMovements"],
                "acceptedFineMovements": actor["acceptedFineMovements"],
                "rejectedMovements": actor["rejectedMovements"],
                "positionPrecision": "PASS" if pose_pass(final) else "FAIL",
                "finalLongitudinalErrorMm": float(final["longitudinalErrorMm"]),
                "finalLateralErrorMm": float(final["lateralErrorMm"]),
                "finalFullContactPointErrorMm": float(final["fullContactPointErrorMm"]),
                "finalOrientationErrorDeg": float(final["orientationErrorDeg"]),
                "originalExactImageSsim": similarity,
                "originalExactImageSsimPass": similarity >= ORIGINAL_SSIM_THRESHOLD,
                "imageIntegrity": "PASS" if fresh and hashes else "FAIL",
            })
    summary = {
        "contract": "thoracic_p1_r3_development_summary_v1",
        "generatedUtc": _utc(),
        "developmentRunsConsumed": len(registry.get("developmentRuns", [])),
        "confirmationRunsConsumed": len(registry.get("confirmationRuns", [])),
        "originalSsimThreshold": ORIGINAL_SSIM_THRESHOLD,
        "newTaskMetricDeveloped": False,
        "runs": rows,
    }
    summary["freezeEligible"] = freeze_eligible(summary)
    _save(root / "development_summary.json", summary)
    sensitivity = {
        "contract": "thoracic_p1_r3_pose_sensitivity_v1",
        "generatedUtc": _utc(),
        "method": "Actual fresh ultrasound after executed 0.5 mm moves over a bounded +/-1 mm route.",
        "observations": sensitivity_rows,
    }
    _save(root / "pose_sensitivity_results.json", sensitivity)
    _write_sensitivity_csv(root / "pose_sensitivity.csv", sensitivity_rows)
    _draw_sensitivity(root / "pose_sensitivity_plot.png", sensitivity_rows)
    _write_report(root, summary, None)
    return summary


def _write_sensitivity_csv(path: Path, rows: list[dict]) -> None:
    if not rows:
        return
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def _draw_sensitivity(path: Path, rows: list[dict]) -> None:
    width, height = 1000, 600
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    draw.text((70, 25), "P1-R3 actual fine-motion pose sensitivity", fill="black")
    left, top, right, bottom = 80, 70, 940, 510
    draw.rectangle((left, top, right, bottom), outline="black", width=2)
    if rows:
        xs = [float(item["longitudinalErrorMm"]) for item in rows]
        ys = [float(item["ssimToCalibration"]) for item in rows]
        x_min, x_max = min(-1.1, min(xs)), max(1.1, max(xs))
        y_min, y_max = min(0.9, min(ys) - 0.01), 1.0
        points = []
        for x, y in zip(xs, ys):
            px = left + int((x - x_min) / (x_max - x_min) * (right - left))
            py = bottom - int((y - y_min) / (y_max - y_min) * (bottom - top))
            points.append((px, py))
        draw.line(points, fill=(35, 95, 210), width=3)
        for point in points:
            draw.ellipse((point[0] - 5, point[1] - 5, point[0] + 5, point[1] + 5),
                         fill=(35, 95, 210))
        threshold_y = bottom - int((ORIGINAL_SSIM_THRESHOLD - y_min)
                                   / (y_max - y_min) * (bottom - top))
        draw.line((left, threshold_y, right, threshold_y), fill=(190, 45, 45), width=2)
        draw.text((left + 8, threshold_y - 20), "original SSIM threshold", fill=(170, 35, 35))
    draw.text((left, bottom + 25), "Signed measured longitudinal residual (mm)", fill="black")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def freeze(root: Path, repo: Path) -> dict:
    summary = _load(root / "development_summary.json")
    if not freeze_eligible(summary):
        raise ValueError("P1-R3 development evidence is not eligible for freezing")
    registry = _load(root / "development_run_registry.json")
    source_entry = next(item for item in registry["developmentRuns"]
                        if item["scenario"] == "FINE_MOTION_SENSITIVITY")
    calibration = _load(root / source_entry["directory"] / "calibration_manifest.json")
    sources = [
        repo / "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7R3.cs",
        repo / "Assets/ThoracicUSCoach/Editor/ThoracicPositionGuidedT7R3Batch.cs",
        repo / "Tools/ThoracicUSCoach/p1_r3_recovery_analysis.py",
    ]
    value = {
        "contract": "thoracic_p1_r3_frozen_configuration_v1",
        "generatedUtc": _utc(), "frozen": True,
        "sourceRunId": source_entry["runId"],
        "targetPose": calibration["targetPose"],
        "corridorAxis": calibration["corridorAxis"],
        "lateralAxis": calibration["lateralAxis"],
        "targetSurfaceNormal": calibration["targetSurfaceNormal"],
        "referenceImage": calibration["referenceImage"],
        "referenceImageSha256": calibration["referenceImageSha256"],
        "originalSsimThreshold": ORIGINAL_SSIM_THRESHOLD,
        "poseToleranceMm": POSE_TOLERANCE_MM,
        "orientationToleranceDeg": ORIENTATION_TOLERANCE_DEG,
        "coarseStepMm": 3.0, "fineStepMm": 0.5,
        "minimumImprovementMm": 0.05, "coarseBudget": 16, "fineBudget": 8,
        "confirmationStartOffsetsMm": CONFIRMATION_STARTS_MM,
        "imageMetric": "original fixed 8x8-block SSIM; confirmation-only",
        "newTaskMetric": None,
        "sourceIdentity": {str(path.relative_to(repo)).replace("\\", "/"): _sha256(path)
                           for path in sources},
        "selectionRule": "Position-only candidate selected because all development pose/integrity gates passed; no image steering or new metric introduced.",
    }
    _save(root / "frozen_configuration.json", value)
    manifest_paths = [root / "frozen_configuration.json", root / "development_summary.json",
                      root / "pose_sensitivity_results.json", *sources]
    lines = [f"{_sha256(path)}  {path.relative_to(repo).as_posix()}" for path in manifest_paths]
    (root / "freeze.sha256").write_text("\n".join(lines) + "\n", encoding="utf-8")
    registry["state"] = "CONFIG_FROZEN"
    _save(root / "development_run_registry.json", registry)
    return value


def analyze_confirmation(root: Path) -> dict:
    registry = _load(root / "development_run_registry.json")
    frozen = _load(root / "frozen_configuration.json")
    reference = root / frozen["referenceImage"]
    episodes = []
    for entry in registry.get("confirmationRuns", []):
        run_root = root / entry["directory"]
        actor = _load(run_root / "trajectory_actor.json")
        evaluator = _load(run_root / "evaluator_only" / "trajectory_privileged.json")
        final = actor["steps"][-1]
        privileged = evaluator["steps"][-1]
        returned = root / final["image"]
        similarity = block_ssim(load_image_descriptor(reference), load_image_descriptor(returned))
        fresh = capture_fresh(final, privileged) and _sha256(returned) == final["imageSha256"]
        position = pose_pass(final)
        evaluator_t7 = float(privileged["distanceToT7ReferenceMm"]) <= 3.0
        verdict = classify_confirmation(position, fresh,
                                        similarity >= ORIGINAL_SSIM_THRESHOLD, evaluator_t7)
        episodes.append({
            "runId": entry["runId"], "status": entry["status"],
            "startOffsetMm": actor["startOffsetMm"],
            "coarseMovements": actor["acceptedCoarseMovements"],
            "fineMovements": actor["acceptedFineMovements"],
            "rejectedMovements": actor["rejectedMovements"],
            "finalLongitudinalErrorMm": float(final["longitudinalErrorMm"]),
            "finalLateralErrorMm": float(final["lateralErrorMm"]),
            "finalFullContactPointErrorMm": float(final["fullContactPointErrorMm"]),
            "finalOrientationErrorDeg": float(final["orientationErrorDeg"]),
            "contactValid": final["contactValid"], "workspaceValid": final["workspaceValid"],
            "freshAndProvenanceMatched": fresh,
            "originalExactImageSsim": similarity,
            "originalExactImageSsimPass": similarity >= ORIGINAL_SSIM_THRESHOLD,
            "independentEvaluatorT7": evaluator_t7,
            "verdict": verdict,
        })
    summary = {
        "contract": "thoracic_p1_r3_confirmation_summary_v1",
        "generatedUtc": _utc(),
        "configurationFrozen": True,
        "episodesConsumed": len(registry.get("confirmationRuns", [])),
        "episodes": episodes,
        "scientificFailure": any(item["verdict"] == "FAIL" for item in episodes),
        "allExecutedPass": bool(episodes) and all(item["verdict"] == "PASS" for item in episodes),
        "originalSsimThreshold": ORIGINAL_SSIM_THRESHOLD,
        "ultrasoundFeedbackUsedForNavigation": False,
        "newTaskMetricDeveloped": False,
    }
    _save(root / "confirmation_summary.json", summary)
    development = _load(root / "development_summary.json")
    _write_report(root, development, summary)
    return summary


def _write_report(root: Path, development: dict, confirmation: dict | None) -> None:
    nav = [item for item in development.get("runs", []) if item.get("kind") == "NAVIGATION"]
    sens = [item for item in development.get("runs", []) if item.get("kind") == "SENSITIVITY"]
    confirmations = [] if confirmation is None else confirmation.get("episodes", [])
    state = "DEVELOPMENT_COMPLETE" if confirmation is None else (
        "COMPLETE" if len(confirmations) == 3 and confirmation.get("allExecutedPass")
        else "COMPLETE_AFTER_CONFIRMATION_FAILURE" if confirmation.get("scientificFailure")
        else "CONFIRMATION_EXECUTED")
    report = f"""# P1-R3 Recovery, Completion and Critical Review

## RECOVERY STATE

The resumed local state was **NOT_STARTED**: no P1-R3 source, registry, physical run, frozen configuration, or confirmation evidence existed. P1/P1-R1/P1-R2 were preserved as historical baselines.

Current experiment state: **{state}**.

## IMPLEMENTATION

P1-R3 is isolated from P1-R2. It adds measured-pose coarse/fine control with a 0.25 mm full 3D tolerance, actual 0.5 mm fine-motion diagnostics, cumulative run accounting, frozen confirmation starts, and offline original-SSIM evaluation. It does not alter Active2D, DirectFreshCapture, the scene, renderer, anatomy, or the historical SSIM threshold.

## DEVELOPMENT

- Physical development runs consumed: **{development.get('developmentRunsConsumed', 0)}/20**.
- Navigation runs: **{len(nav)}**.
- Fine-motion sensitivity runs: **{len(sens)}**.
- Freeze eligible: **{development.get('freezeEligible', False)}**.

Navigation development evidence:
{json.dumps(nav, indent=2)}

Sensitivity evidence:
{json.dumps(sens, indent=2)}

## IMAGE VALIDITY

- IMAGE_INTEGRITY is reported independently per run.
- SIMULATOR_ANATOMICAL_VALIDITY remains limited to the fixed Unity anatomy and independent T7 evaluator correspondence.
- ORIGINAL_EXACT_IMAGE_SSIM remains locked at **{ORIGINAL_SSIM_THRESHOLD:.12f}**.
- NEW_TASK_IMAGE_CONSISTENCY: **NOT_DEVELOPED**. No replacement threshold was selected from P1-R2.
- Incorrect-T-level rejection by a new metric: **NOT_APPLICABLE**.

## CONFIRMATION

- Confirmation episodes consumed: **{len(confirmations)}/3**.
- Ultrasound feedback used for navigation: **False**.

{json.dumps(confirmations, indent=2)}

## SCIENTIFIC DISTINCTIONS

- Calibrated pose return: evaluated by measured full-pose error.
- Stored-view reproduction: evaluated separately by the original locked SSIM.
- Ultrasound-assisted correction: **NOT TESTED**, because the selected controller is position-only.
- Autonomous T7 identification: **NOT_TESTED**.
- G2: **FAIL**; G3/G4: **NOT_RUN**.

## REMAINING BLOCKER

The highest-priority blocker is reproducible exact-view confirmation under the original SSIM threshold across frozen starts. A calibrated pose result does not by itself establish autonomous anatomical localization.
"""
    (root / "REPORT.md").write_text(report, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["audit", "development", "freeze", "confirmation"])
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--repo", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "audit":
        result = audit(args.root, args.repo)
    elif args.command == "development":
        result = analyze_development(args.root)
    elif args.command == "freeze":
        result = freeze(args.root, args.repo)
    else:
        result = analyze_confirmation(args.root)
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
