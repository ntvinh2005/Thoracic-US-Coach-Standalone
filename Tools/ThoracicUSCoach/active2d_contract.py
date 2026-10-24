from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any


CONTRACT = "active2d_image_only_v1"
EXPERIMENT_ID = "thoracic_active2d_image_only_v1_20261023"
LEVEL_ORDER = ("T6", "T7", "T8", "OUTSIDE_T6_T8")
ACTION_ORDER = (
    "CRANIAL",
    "CAUDAL",
    "MEDIAL",
    "LATERAL",
    "ROTATECLOCKWISE",
    "ROTATECOUNTERCLOCKWISE",
    "PHASEHANDOFF",
    "STOP",
)
ACTOR_REQUEST_REQUIRED_FIELDS = frozenset(
    {"schemaVersion", "contract", "requestId", "episodeId", "reset", "imagePath"}
)
ACTOR_REQUEST_FEEDBACK_FIELDS = frozenset(
    {
        "hasPreviousActionFeedback",
        "previousAction",
        "previousActionAccepted",
        "previousActionCompleted",
    }
)
ACTOR_REQUEST_FIELDS = ACTOR_REQUEST_REQUIRED_FIELDS | ACTOR_REQUEST_FEEDBACK_FIELDS


def project_root() -> Path:
    return Path(__file__).resolve().parents[2]


def canonical_sha256(payload: Any) -> str:
    encoded = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def validate_actor_request(payload: dict[str, Any]) -> None:
    unexpected = set(payload) - ACTOR_REQUEST_FIELDS
    missing = ACTOR_REQUEST_REQUIRED_FIELDS - set(payload)
    if unexpected or missing:
        raise ValueError(f"Restricted actor request mismatch: missing={sorted(missing)} unexpected={sorted(unexpected)}")
    if payload.get("schemaVersion") != 1 or payload.get("contract") != CONTRACT:
        raise ValueError("Active 2D actor contract mismatch")
    if not isinstance(payload.get("reset"), bool):
        raise ValueError("reset must be boolean")
    for key in ("requestId", "episodeId", "imagePath"):
        if not isinstance(payload.get(key), str) or not payload[key]:
            raise ValueError(f"{key} must be a non-empty string")
    if "hasPreviousActionFeedback" in payload and not isinstance(payload["hasPreviousActionFeedback"], bool):
        raise ValueError("hasPreviousActionFeedback must be boolean")
    if payload.get("hasPreviousActionFeedback", False):
        action = payload.get("previousAction")
        if not isinstance(action, str) or action.replace("_", "").upper() not in ACTION_ORDER:
            raise ValueError("previousAction must be a supported action when feedback is present")
        for key in ("previousActionAccepted", "previousActionCompleted"):
            if not isinstance(payload.get(key), bool):
                raise ValueError(f"{key} must be boolean when feedback is present")
        if payload["previousActionCompleted"] and not payload["previousActionAccepted"]:
            raise ValueError("A completed previous action must also be accepted")


def validate_checkpoint(model: dict[str, Any]) -> None:
    if model.get("contract") != CONTRACT or model.get("experimentId") != EXPERIMENT_ID:
        raise ValueError("Active 2D checkpoint identity mismatch")
    if model.get("policyInputs") != ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"]:
        raise ValueError("Checkpoint does not declare the restricted image-only policy interface")
    if model.get("levelOrder") != list(LEVEL_ORDER):
        raise ValueError("Checkpoint level order mismatch")
    architecture = model.get("architecture") or {}
    if architecture.get("type") != "fixed-convolutional-feature-bank+mlp+bayes-v1":
        raise ValueError("Unsupported Phase 1 architecture")
    input_weights = architecture.get("inputWeights") or []
    input_bias = architecture.get("inputBias") or []
    output_weights = architecture.get("outputWeights") or []
    output_bias = architecture.get("outputBias") or []
    hidden = len(input_bias)
    if not input_weights or hidden <= 0 or len(output_bias) != len(LEVEL_ORDER):
        raise ValueError("Malformed Phase 1 checkpoint")
    if any(len(row) != hidden for row in input_weights):
        raise ValueError("Phase 1 input weight shape mismatch")
    if len(output_weights) != hidden or any(len(row) != len(LEVEL_ORDER) for row in output_weights):
        raise ValueError("Phase 1 output weight shape mismatch")
    handoff = model.get("handoffHead") or {}
    if handoff.get("type") != "fixed-convolutional-binary-handoff-v1":
        raise ValueError("Missing exact-region Phase 1 handoff head")
    handoff_weights = handoff.get("inputWeights") or []
    handoff_bias = handoff.get("inputBias") or []
    handoff_output = handoff.get("outputWeights") or []
    if len(handoff_weights) != int(handoff.get("featureWidth", 0)):
        raise ValueError("Phase 1 handoff input width mismatch")
    if not handoff_bias or any(len(row) != len(handoff_bias) for row in handoff_weights):
        raise ValueError("Malformed Phase 1 handoff input layer")
    if len(handoff_output) != len(handoff_bias):
        raise ValueError("Malformed Phase 1 handoff output layer")
