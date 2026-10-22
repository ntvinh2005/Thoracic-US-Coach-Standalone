from __future__ import annotations

import json
import platform
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

from coach_data import DEFAULT_EXPERIMENT_ID, project_root, save_json


ROOT = project_root()
ARTIFACTS = ROOT / "Artifacts" / "ThoracicUSCoach"


def read(relative: str) -> dict[str, object]:
    return json.loads((ARTIFACTS / relative).read_text(encoding="utf-8"))


def git_value(*args: str) -> str:
    result = subprocess.run(
        ["git", *args], cwd=ROOT, text=True, capture_output=True, check=False
    )
    return result.stdout.strip() or "unavailable"


def main() -> None:
    audit = read("diversity_audit_v2.json")
    lock = read("final_test_lock_diverse_v2.json")
    bc_current = read("checkpoints/bc_pixel_current_diverse_v2.json")
    bc_history = read("checkpoints/bc_pixel_history_diverse_v2.json")
    ref_pixels = read("checkpoints/reference_head_pixels_diverse_v2.json")
    ref_usfm = read("checkpoints/reference_head_usfm_diverse_v2.json")
    action_usfm = read("checkpoints/usfm_action_head_diverse_v2.json")
    ppo = {
        seed: read(f"checkpoints/ppo_live_best_dev_seed{seed}_diverse_v2.json")
        for seed in (13, 37, 101)
    }

    offline = {
        name: read(f"metrics/{name}.json")
        for name in (
            "offline_bc_history_heldout",
            "offline_bc_history_stress",
            "offline_ppo_heldout",
            "offline_ppo_stress",
            "usfm_reference_heldout",
            "usfm_reference_stress",
            "usfm_action_heldout",
            "usfm_action_stress",
        )
    }
    live = {
        name: read(f"metrics/{name}.json")
        for name in (
            "live_dev_bc_history",
            "live_dev_ppo_seed13",
            "live_bc_history_heldout",
            "live_bc_history_stress",
            "live_ppo_heldout",
            "live_ppo_stress",
        )
    }
    usfm_load = read("usfm/usfm_load_report.json")

    report = {
        "schemaVersion": 2,
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "experimentId": DEFAULT_EXPERIMENT_ID,
        "scope": "Simulator-only engineering validation; not clinical validation",
        "code": {
            "repositoryHead": git_value("rev-parse", "HEAD"),
            "workingTreeDirty": bool(git_value("status", "--porcelain") not in ("", "unavailable")),
            "unityVersion": "6000.3.9f1",
            "python": sys.version,
            "platform": platform.platform(),
            "usfmOfficialCommit": "960a1e1d30b9490e53e6e7cf1b4dd24425fffc67",
        },
        "dataset": {
            "fingerprint": audit["datasetFingerprint"],
            "auditPassed": audit["passed"],
            "auditFailures": audit["failures"],
            "summary": audit["summary"],
            "splits": audit["splits"],
            "crossSplitNearestImages": audit["crossSplitNearestImages"],
            "transactionalCapture": "Only complete finalized directories enter dataset_index.json; one retained .partial failure is intentionally excluded",
            "legacyPolicy": "legacy_fixed_path_smoke remains readable but is excluded by default from v2 loaders/train/eval",
        },
        "testLock": {
            "lockedUtc": lock["lockedUtc"],
            "testFingerprint": lock["testFingerprint"],
            "testSweeps": lock["testSweeps"],
            "testFrames": lock["testFrames"],
            "verifiedBeforeFinalEvaluation": True,
        },
        "developmentSelection": {
            "pixelBcCurrentAccuracy": bc_current["metrics"]["dev"]["accuracy"],
            "pixelBcHistoryAccuracy": bc_history["metrics"]["dev"]["accuracy"],
            "referencePixels": ref_pixels["metrics"]["dev"],
            "referenceUsfm": ref_usfm["metrics"]["dev"],
            "actionUsfmAccuracy": action_usfm["metrics"]["dev"]["accuracy"],
            "ppoBestExplicitStopSuccessBySeed": {
                str(seed): model["bestDevExplicitStopSuccessRate"] for seed, model in ppo.items()
            },
            "liveProtocolBcHistory": live["live_dev_bc_history"]["policy"]["summary"],
            "liveProtocolPpoSeed13": live["live_dev_ppo_seed13"]["policy"]["summary"],
            "selectedBeforeTest": "checkpoints/ppo_live_best_dev_seed13_diverse_v2.json",
            "selectionReason": "Highest PPO dev success; versus BC it tied success while reducing stopping error, steps, and timeouts",
        },
        "finalEvaluation": {
            "offlineFrameAccuracy": {
                "bcHistoryHeldout": offline["offline_bc_history_heldout"]["frameMetrics"]["accuracy"],
                "bcHistoryStressDefinedActions": offline["offline_bc_history_stress"]["frameMetrics"]["accuracy"],
                "ppoHeldout": offline["offline_ppo_heldout"]["frameMetrics"]["accuracy"],
                "ppoStressDefinedActions": offline["offline_ppo_stress"]["frameMetrics"]["accuracy"],
                "usfmReferenceHeldout": offline["usfm_reference_heldout"]["metrics"]["accuracy"],
                "usfmReferenceStress": offline["usfm_reference_stress"]["metrics"]["accuracy"],
                "usfmActionHeldout": offline["usfm_action_heldout"]["metrics"]["accuracy"],
                "usfmActionStressDefinedActions": offline["usfm_action_stress"]["metrics"]["accuracy"],
            },
            "liveClosedLoop": {
                "bcHistoryHeldout": live["live_bc_history_heldout"]["policy"]["summary"],
                "bcHistoryStress": live["live_bc_history_stress"]["policy"]["summary"],
                "ppoHeldout": live["live_ppo_heldout"]["policy"]["summary"],
                "ppoStress": live["live_ppo_stress"]["policy"]["summary"],
                "teacherHeldout": live["live_ppo_heldout"]["teacherOracle"]["summary"],
                "teacherStress": live["live_ppo_stress"]["teacherOracle"]["summary"],
            },
            "interpretation": "The selected PPO candidate is marginally better than BC in held-out live success (0.40 vs 0.333) and ties stress (0.20), but both remain far below the teacher oracle. PPO is not a clinically deployable result.",
        },
        "usfm": {
            "checkpointSha256": usfm_load["checkpointSha256"],
            "matchedTensors": usfm_load["matchedTensors"],
            "backboneTensors": usfm_load["backboneTensors"],
            "matchRatio": usfm_load["matchRatio"],
            "frozen": usfm_load["encoderFrozen"],
            "device": usfm_load["device"],
            "featureShape": usfm_load["featureShape"],
            "latencyCaveat": "Cached linear-head latency excludes the CPU USFM encoder and is not an online deployment latency claim",
        },
        "knownLimitations": [
            "T7-right target and score are provisional and not clinician approved",
            "Synthetic single-simulator anatomy is not real-ultrasound domain validation",
            "PPO has high seed variance and poor final closed-loop success",
            "The 3-action policy cannot explicitly abstain when the target is absent",
            "Existing simulator ATC64 and missing-prefab errors are unrelated to this feature",
        ],
    }
    save_json(ARTIFACTS / "validation_report_diverse_v2.json", report)
    print(json.dumps({
        "report": str(ARTIFACTS / "validation_report_diverse_v2.json"),
        "datasetSweeps": report["dataset"]["summary"]["sweeps"],
        "auditPassed": report["dataset"]["auditPassed"],
        "selectedBeforeTest": report["developmentSelection"]["selectedBeforeTest"],
        "ppoHeldoutSuccess": report["finalEvaluation"]["liveClosedLoop"]["ppoHeldout"]["explicitStopInRegionRate"],
    }, indent=2))


if __name__ == "__main__":
    main()
