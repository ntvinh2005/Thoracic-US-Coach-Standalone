from __future__ import annotations

import hashlib
import json
from collections import defaultdict
from pathlib import Path
from typing import Any, Callable

import numpy as np


FORBIDDEN_ACTOR_FIELDS = {
    "probePosition", "probeRotation", "contactPosition", "targetCoordinates",
    "targetDirection", "finalTargetScore", "score", "t7DistanceMm", "nearestLevel",
    "anatomyLabels", "UnityAnatomyLabels",
}
ALLOWED_MODEL_INPUTS = ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"]


def sha256_json(value: Any) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def training_scope(readiness: dict[str, Any]) -> str:
    if readiness.get("scientificTrainingDataReady") is True:
        return "SCIENTIFIC_DEVELOPMENT"
    if readiness.get("engineeringDemoDataReady") is True:
        return "ENGINEERING_DEMO_ONLY"
    return "INSUFFICIENT_INDEPENDENT_DATA"


def classify_readiness(evidence: dict[str, Any]) -> dict[str, Any]:
    engineering_checks = {
        "integrity": evidence.get("integrityStatus") == "PASS",
        "freshness": evidence.get("imagePoseFreshness") == "PASS",
        "basin": evidence.get("positiveBasin") == "PASS",
        "reachability": evidence.get("safeTargetReachability") == "PASS",
        "positiveObservations": int(evidence.get("positiveUniquePoseObservations", 0)) >= 24,
        "negativeObservations": int(evidence.get("negativeUniquePoseObservations", 0)) >= 48,
        "positiveGroups": int(evidence.get("primaryIndependentPositiveGroups", 0)) >= 2,
        "negativeGroups": int(evidence.get("primaryIndependentNegativeGroups", 0)) >= 3,
        "hardNegativeGroups": int(evidence.get("primaryIndependentHardNegativeGroups", 0)) >= 1,
        "allDecisionSupportEligible": evidence.get("allCountedObservationsDecisionSupportEligible") is True,
        "missingRawNotCounted": evidence.get("readinessDependsOnMissingRaw") is False,
    }
    engineering = all(engineering_checks.values())
    scientific_checks = {
        "engineering": engineering,
        "primaryPositiveGroups": int(evidence.get("primaryIndependentPositiveGroups", 0)) >= 6,
        "primaryNegativeGroups": int(evidence.get("primaryIndependentNegativeGroups", 0)) >= 6,
        "conservativePositiveGroups": int(evidence.get("conservativeIndependentPositiveGroups", 0)) >= 6,
        "conservativeNegativeGroups": int(evidence.get("conservativeIndependentNegativeGroups", 0)) >= 6,
        "hardNegativeGroups": int(evidence.get("primaryIndependentHardNegativeGroups", 0)) >= 2,
        "positiveStructuralClusters": int(evidence.get("positiveStructuralClusters", 0)) >= 3,
        "negativeStructuralClusters": int(evidence.get("negativeStructuralClusters", 0)) >= 3,
        "splitIntegrity": evidence.get("splitIntegrity") == "PASS",
        "minimumRolesPerLabel": evidence.get("minimumRolesPerLabel") == {"train": 3, "calibration": 1, "heldout_development": 2},
    }
    scientific = all(scientific_checks.values())
    return {
        "engineeringDemoDataReady": engineering,
        "scientificTrainingDataReady": scientific,
        "engineeringChecks": engineering_checks,
        "scientificChecks": scientific_checks,
        "blockingReasons": [
            *[f"engineering:{key}" for key, passed in engineering_checks.items() if not passed],
            *[f"scientific:{key}" for key, passed in scientific_checks.items() if not passed],
        ],
    }


def validate_actor_features(row: dict[str, Any]) -> None:
    leaked = sorted(FORBIDDEN_ACTOR_FIELDS.intersection(row))
    if leaked:
        raise ValueError("privileged actor features present: " + ",".join(leaked))
    unexpected = sorted(set(row).difference(ALLOWED_MODEL_INPUTS))
    if unexpected:
        raise ValueError("undeclared actor features present: " + ",".join(unexpected))
    if not isinstance(row.get("cleanUltrasoundImage"), str) or not row["cleanUltrasoundImage"]:
        raise ValueError("clean ultrasound image missing")
    memory = row.get("policyOwnedMemory")
    if not isinstance(memory, (dict, list)):
        raise ValueError("policy-owned memory missing")
    history = row.get("policyIssuedActionHistory")
    if not isinstance(history, list):
        raise ValueError("policy action history missing")
    for action in history:
        if not all(key in action for key in ("issued", "executed", "accepted", "completed")):
            raise ValueError("action feedback incomplete")
        if (not action["accepted"] or not action["completed"]) and action["executed"] not in {"NONE", "RESET"}:
            raise ValueError("rejected/incomplete action encoded as executed motion")


def _role_order(component_id: str, protocol_id: str) -> str:
    return hashlib.sha256(f"{protocol_id}:{component_id}".encode()).hexdigest()


def validate_components(components: list[dict[str, Any]]) -> None:
    ids = [str(item.get("componentId")) for item in components]
    if len(ids) != len(set(ids)) or any(value in {"", "None"} for value in ids):
        raise ValueError("component IDs missing or duplicated")
    for item in components:
        if item.get("label") not in {"positive", "negative"}:
            raise ValueError("component label invalid")
        if item.get("independenceVerified") is not True:
            raise ValueError("component independence not verified")
        if not item.get("conservativeComponentId") or not item.get("ancestryKeys"):
            raise ValueError("component conservative identity or ancestry missing")


def materialize_component_split(
    components: list[dict[str, Any]],
    *,
    split_protocol_id: str,
    scope: str,
) -> dict[str, Any]:
    if scope not in {"SCIENTIFIC_DEVELOPMENT", "ENGINEERING_DEMO_ONLY"}:
        raise ValueError("training is not authorized by readiness")
    validate_components(components)
    by_label: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in components:
        by_label[item["label"]].append(item)
    if set(by_label) != {"positive", "negative"}:
        raise ValueError("both labels require independent components")
    assignments = []
    for label, values in sorted(by_label.items()):
        ordered = sorted(values, key=lambda item: (_role_order(item["componentId"], split_protocol_id), item["componentId"]))
        if scope == "SCIENTIFIC_DEVELOPMENT" and len(ordered) < 6:
            raise ValueError(f"scientific split lacks six {label} components")
        if scope == "SCIENTIFIC_DEVELOPMENT":
            roles = ["heldout_development", "heldout_development", "calibration"] + ["train"] * (len(ordered) - 3)
        else:
            if len(ordered) < 3:
                raise ValueError(f"engineering split lacks three {label} components")
            roles = ["development_regression", "development_calibration"] + ["development_train"] * (len(ordered) - 2)
        for item, role in zip(ordered, roles):
            assignments.append({
                "componentId": item["componentId"],
                "label": label,
                "role": role,
                "conservativeComponentId": item["conservativeComponentId"],
                "ancestryKeys": sorted(set(item["ancestryKeys"])),
                "recordIds": sorted(set(item.get("recordIds", []))),
            })
    validate_split_no_leakage(assignments)
    result = {
        "schemaVersion": 1,
        "splitProtocolId": split_protocol_id,
        "scope": scope,
        "assignments": sorted(assignments, key=lambda item: item["componentId"]),
    }
    result["splitSha256"] = sha256_json(result)
    return result


def validate_split_no_leakage(assignments: list[dict[str, Any]]) -> None:
    role_by_component = {item["componentId"]: item["role"] for item in assignments}
    if len(role_by_component) != len(assignments):
        raise ValueError("component crosses roles")
    seen_records: dict[str, str] = {}
    ancestry_roles: dict[str, set[str]] = defaultdict(set)
    conservative_roles: dict[str, set[str]] = defaultdict(set)
    for item in assignments:
        role = item["role"]
        for record_id in item.get("recordIds", []):
            previous = seen_records.setdefault(record_id, role)
            if previous != role:
                raise ValueError("record crosses roles")
        for key in item["ancestryKeys"]:
            ancestry_roles[key].add(role)
        conservative_roles[item["conservativeComponentId"]].add(role)
    if any(len(roles) > 1 for roles in ancestry_roles.values()):
        raise ValueError("ancestry crosses roles")
    if any(len(roles) > 1 for roles in conservative_roles.values()):
        raise ValueError("conservative component crosses roles")


def experiment_plan(
    readiness: dict[str, Any],
    *,
    split_sha256: str | None,
    causal_history_verified: bool,
) -> dict[str, Any]:
    scope = training_scope(readiness)
    if scope == "INSUFFICIENT_INDEPENDENT_DATA":
        return {
            "status": "INSUFFICIENT_INDEPENDENT_DATA",
            "trainingAuthorized": False,
            "scope": scope,
            "candidates": [],
        }
    if not split_sha256 or len(split_sha256) != 64:
        raise ValueError("materialized component split hash required before training")
    candidates = ["frozen_spatial_v2", "structural_v4_equivalent", "compact_single_frame_cnn"]
    if causal_history_verified:
        candidates.append("causal_h4_corrected")
    return {
        "status": "AUTHORIZED_BOUNDED_STUDY",
        "trainingAuthorized": True,
        "scope": scope,
        "splitSha256": split_sha256,
        "candidates": candidates,
        "seeds": [13, 29, 47],
        "maximumEpochs": 120,
        "additionalSweepAuthorized": False,
        "lockedOrReservedConfirmationAccess": False,
    }


def p2_checkpoint_envelope(
    *,
    candidate_id: str,
    scope: str,
    readiness_sha256: str,
    split_sha256: str,
    model_payload: dict[str, Any],
    diagnostic_operating_point: float | None,
) -> dict[str, Any]:
    if scope not in {"SCIENTIFIC_DEVELOPMENT", "ENGINEERING_DEMO_ONLY"}:
        raise ValueError("invalid Prompt 2 checkpoint scope")
    return {
        "schemaVersion": 1,
        "prompt2ExperimentId": "G2_P2_BOUNDED_RECOGNITION_V1",
        "candidateId": candidate_id,
        "scope": scope,
        "policyInputs": ALLOWED_MODEL_INPUTS,
        "forbiddenInputs": sorted(FORBIDDEN_ACTOR_FIELDS),
        "readinessSha256": readiness_sha256,
        "splitSha256": split_sha256,
        "model": model_payload,
        "diagnosticOperatingPoint": diagnostic_operating_point,
        "deployment": {
            "status": "BLOCKED",
            "safeOperatingPoint": False,
            "reason": "Prompt 2 development candidate remains NON_DEPLOYABLE pending separate promotion gates",
        },
    }


def validate_p2_checkpoint(checkpoint: dict[str, Any], *, diagnostic_harness: bool = False) -> None:
    if checkpoint.get("prompt2ExperimentId") != "G2_P2_BOUNDED_RECOGNITION_V1":
        raise ValueError("Prompt 2 checkpoint identity mismatch")
    if checkpoint.get("policyInputs") != ALLOWED_MODEL_INPUTS:
        raise ValueError("Prompt 2 checkpoint actor input contract mismatch")
    if set(checkpoint.get("forbiddenInputs", [])) != FORBIDDEN_ACTOR_FIELDS:
        raise ValueError("Prompt 2 forbidden input contract mismatch")
    if any(len(str(checkpoint.get(field, ""))) != 64 for field in ("readinessSha256", "splitSha256")):
        raise ValueError("Prompt 2 checkpoint provenance hash missing")
    deployment = checkpoint.get("deployment") or {}
    if deployment.get("status") != "BLOCKED" or deployment.get("safeOperatingPoint") is not False:
        raise ValueError("Prompt 2 development checkpoint must remain blocked")
    if not diagnostic_harness:
        raise ValueError("Prompt 2 checkpoint is non-deployable; use isolated diagnostic harness explicitly")


def run_p2_diagnostic_inference(
    checkpoint: dict[str, Any],
    actor_input: dict[str, Any],
    backend: Callable[[dict[str, Any], dict[str, Any]], dict[str, Any]],
) -> dict[str, Any]:
    """Run a BLOCKED Prompt-2 model only through the isolated diagnostic interface.

    The backend receives only the validated image/policy-owned input contract and
    model payload. It must return separate recognition and quality values. This
    adapter never authorizes live STOP; quality is ranking-only and cannot affect
    even the diagnostic recognition decision.
    """
    validate_p2_checkpoint(checkpoint, diagnostic_harness=True)
    validate_actor_features(actor_input)
    raw = backend(dict(actor_input), dict(checkpoint.get("model") or {}))
    if not isinstance(raw, dict):
        raise ValueError("Prompt 2 inference backend output must be an object")
    required = {"recognitionProbability", "qualityEstimate"}
    if set(raw) != required:
        raise ValueError("Prompt 2 inference output contract mismatch")
    try:
        recognition = float(raw["recognitionProbability"])
        quality = float(raw["qualityEstimate"])
    except (TypeError, ValueError) as exc:
        raise ValueError("Prompt 2 inference outputs must be numeric") from exc
    if not np.isfinite(recognition) or not 0.0 <= recognition <= 1.0:
        raise ValueError("recognition probability outside [0,1]")
    if not np.isfinite(quality) or not 0.0 <= quality <= 1.0:
        raise ValueError("quality estimate outside [0,1]")
    threshold = checkpoint.get("diagnosticOperatingPoint")
    diagnostic_candidate = False
    if threshold is not None:
        threshold = float(threshold)
        if not np.isfinite(threshold) or not 0.0 <= threshold <= 1.0:
            raise ValueError("diagnostic operating point outside [0,1]")
        diagnostic_candidate = recognition >= threshold
    return {
        "recognitionProbability": recognition,
        "qualityEstimate": quality,
        "diagnosticRecognitionCandidate": diagnostic_candidate,
        "qualityUsedForRecognition": False,
        "stopAuthorized": False,
        "deploymentStatus": "BLOCKED",
    }


def preprocessing_fingerprint(feature_extractor: str, vectors: list[list[float]]) -> str:
    return sha256_json({"featureExtractor": feature_extractor, "vectors": vectors})


def verify_preprocessing_parity(
    offline_vectors: list[list[float]],
    live_vectors: list[list[float]],
    *,
    feature_extractor: str,
    absolute_tolerance: float = 1e-7,
) -> dict[str, Any]:
    offline = np.asarray(offline_vectors, dtype=np.float32)
    live = np.asarray(live_vectors, dtype=np.float32)
    if offline.shape != live.shape:
        raise ValueError("offline/live feature shape mismatch")
    maximum_error = float(np.max(np.abs(offline - live))) if offline.size else 0.0
    if not np.allclose(offline, live, rtol=0.0, atol=absolute_tolerance):
        raise ValueError(f"offline/live preprocessing mismatch: maximum error {maximum_error}")
    fingerprint = preprocessing_fingerprint(feature_extractor, offline.tolist())
    return {
        "featureExtractor": feature_extractor,
        "rows": int(offline.shape[0]) if offline.ndim else 0,
        "width": int(offline.shape[1]) if offline.ndim == 2 else 0,
        "maximumAbsoluteError": maximum_error,
        "offlineFingerprint": fingerprint,
        "liveFingerprint": preprocessing_fingerprint(feature_extractor, live.tolist()),
        "pass": True,
    }


def write_materialized_split(path: Path, result: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
