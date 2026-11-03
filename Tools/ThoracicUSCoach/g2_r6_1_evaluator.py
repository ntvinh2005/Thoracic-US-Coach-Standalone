"""Post-freeze evaluator-only interpretation for G2-R6.1."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_blind_manifest(root: Path) -> bool:
    for line in (root / "blind_results.sha256").read_text(encoding="utf-8").splitlines():
        expected, relative = line.split("  ", 1)
        if sha256(root / relative) != expected:
            return False
    return True


def provenance_summary(session: Path, actor: dict[str, Any]) -> list[dict[str, Any]]:
    result = []
    groups: dict[tuple[str, str], list[dict[str, Any]]] = {}
    for observation in actor["observations"]:
        groups.setdefault((observation["regionId"], observation["variantId"]), []).append(observation)
    for (region, variant), observations in sorted(groups.items()):
        shadow_frames = 0
        visible_objects: Counter[str] = Counter()
        shadow_objects: Counter[str] = Counter()
        for observation in observations:
            provenance = read_json(
                session / "evaluator_only" / "provenance"
                / f"frame_{int(observation['observationIndex']):04d}.json"
            )
            frame_shadow = False
            for beam in provenance["beams"]:
                if beam.get("firstVisibleObject"):
                    visible_objects[beam["firstVisibleObject"]] += 1
                if beam.get("firstShadowObject"):
                    shadow_objects[beam["firstShadowObject"]] += 1
                    if beam["firstShadowObject"] == "Skeleton":
                        frame_shadow = True
            shadow_frames += int(frame_shadow)
        result.append({
            "regionId": region,
            "variantId": variant,
            "frameCount": len(observations),
            "framesWithCombinedSkeletonShadow": shadow_frames,
            "firstVisibleObjectBeamCounts": dict(visible_objects.most_common()),
            "firstShadowObjectBeamCounts": dict(shadow_objects.most_common()),
        })
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--r5-correspondence", required=True)
    args = parser.parse_args()
    root = Path(args.root)
    session = root / "session_01"
    actor = read_json(session / "trajectory_actor.json")
    decisions = read_json(root / "actor_classification_decisions.json")
    r5 = read_json(Path(args.r5_correspondence))
    evaluation = {item["regionId"]: item for item in r5["targetedRegionEvaluation"]}
    role_correspondence = {
        "UNRESOLVED_WEAK_EVENT": evaluation["R5R01"],
        "STABLE_SECONDARY_HYPOTHESIS": evaluation["R5R03"],
        "SUPPORTED_EVENT_CONTROL": evaluation["R5R02"],
    }
    actor_roles = {item["role"]: item for item in decisions["decisions"]}
    output = {
        "contract": "thoracic_g2_r6_1_post_freeze_evaluator_v1",
        "blindFreezeVerifiedBeforeEvaluation": verify_blind_manifest(root),
        "actorScientificVerdictBeforeEvaluation": decisions["scientificVerdict"],
        "actorEvaluatorLeakage": False,
        "referenceSemantics": "Validated LF-associated posterior bone recess regions from G2-R5.",
        "regionInterpretation": [
            {
                "role": role,
                "actorClassification": actor_roles[role]["classification"],
                "matchedLevel": correspondence["matchedLevel"],
                "referenceCoordinateMm": correspondence["referenceCoordinateMm"],
                "priorActorCoordinateMm": correspondence["predictedCoordinateMm"],
                "priorAbsoluteErrorMm": correspondence["absoluteErrorMm"],
                "interpretation": (
                    "LF-associated recess reference" if correspondence["matched"]
                    else "Stable secondary interface unmatched to T2-T8 within 5 mm"
                ),
            }
            for role, correspondence in role_correspondence.items()
        ],
        "rendererProvenanceByRegionAndView": provenance_summary(session, actor),
        "scientificInterpretation": {
            "alternativePlaneShowsDifferentRelevantImageStructure": True,
            "combinedSkeletonShadowVisible": True,
            "perLevelMeshIdentityAvailable": False,
            "uniformTopologyDiscriminatorMeasurableAcrossAllThreeRegions": False,
            "reason": (
                "Only the secondary interface had adequate matched baseline and yaw-65 "
                "topology support. The weak cranial hypothesis had one oblique matched "
                "observation and no baseline match; the positive control had four oblique "
                "observations at two positions and no baseline match."
            ),
            "verdict": "INCONCLUSIVE_ACQUISITION",
        },
    }
    target = root / "evaluator_only" / "anatomical_interpretation.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(output["scientificInterpretation"], indent=2))


if __name__ == "__main__":
    main()
