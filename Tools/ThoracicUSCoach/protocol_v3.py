from __future__ import annotations

from copy import deepcopy
from typing import Any

from contract_v3 import (
    CONTRACT_VERSION,
    EXPERIMENT_ID,
    OBSERVATION_SCHEMA,
    TARGET_SEMANTICS,
    canonical_json_sha256,
)


ACTION_ORDER = [-1, 0, 1]
PREPROCESSING_ID = "grayscale-bilinear-per-image-zscore-v1"


def scenario_identity_sha256(scenario: dict[str, Any]) -> str:
    """Hash the complete frozen physical/dynamics/render description."""
    payload = deepcopy(scenario)
    payload.pop("scenarioIdentitySha256", None)
    return canonical_json_sha256(payload)


def protocol_identity(max_steps: int) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "schemaVersion": 1,
        "experimentId": EXPERIMENT_ID,
        "environmentContractVersion": CONTRACT_VERSION,
        "observationSchema": OBSERVATION_SCHEMA,
        "targetSemantics": TARGET_SEMANTICS,
        "actionOrder": ACTION_ORDER,
        "preprocessing": PREPROCESSING_ID,
        "episodeBudget": int(max_steps),
        "actorInputs": ["previousCleanUltrasound", "currentCleanUltrasound", "previousExecutedAction"],
        "success": "explicit STOP AND exact acceptable pose AND valid contact AND valid workspace",
    }
    payload["protocolIdentitySha256"] = canonical_json_sha256(payload)
    return payload


def validate_manifest(
    manifest: dict[str, Any],
    *,
    expected_group: str | None = None,
    selection_allowed: bool | None = None,
) -> None:
    if manifest.get("experimentId") != EXPERIMENT_ID:
        raise ValueError("Scenario manifest experiment mismatch")
    if manifest.get("contractVersion") != CONTRACT_VERSION:
        raise ValueError("Scenario manifest contract mismatch")
    if manifest.get("observationSchema") != OBSERVATION_SCHEMA:
        raise ValueError("Scenario manifest observation schema mismatch")
    if manifest.get("targetSemantics") != TARGET_SEMANTICS:
        raise ValueError("Scenario manifest target semantics mismatch")
    if manifest.get("actionOrder") != ACTION_ORDER:
        raise ValueError("Scenario manifest action schema mismatch")
    if expected_group is not None and manifest.get("group") != expected_group:
        raise ValueError(f"Expected {expected_group} manifest, got {manifest.get('group')}")
    if selection_allowed is not None and bool(manifest.get("selectionAllowed")) != selection_allowed:
        raise ValueError("Scenario manifest selection-use policy mismatch")
    protocol = manifest.get("protocol") or {}
    expected_protocol_hash = protocol_identity(int(protocol.get("episodeBudget", 0))).get("protocolIdentitySha256")
    if protocol.get("protocolIdentitySha256") != expected_protocol_hash:
        raise ValueError("Scenario manifest protocol identity mismatch")


def verify_frozen_scenario(frozen: dict[str, Any], actual: dict[str, Any]) -> str:
    expected = str(frozen.get("scenarioIdentitySha256") or "")
    actual_hash = scenario_identity_sha256(actual)
    if not expected or actual_hash != expected:
        raise RuntimeError(
            "Frozen scenario identity mismatch: projected pose/dynamics/render protocol changed"
        )
    return actual_hash


def validate_model(model: dict[str, Any], feature_width: int | None = None) -> None:
    if model.get("experimentId") != EXPERIMENT_ID:
        raise ValueError("Checkpoint experiment mismatch")
    if model.get("environmentContractVersion") != CONTRACT_VERSION:
        raise ValueError("Checkpoint environment contract mismatch")
    if model.get("observationSchema") != OBSERVATION_SCHEMA:
        raise ValueError("Checkpoint observation schema mismatch")
    if model.get("preprocessing") != PREPROCESSING_ID:
        raise ValueError("Checkpoint preprocessing mismatch")
    if model.get("actionOrder") != ACTION_ORDER:
        raise ValueError("Checkpoint action order mismatch")
    architecture = str(model.get("architecture") or "linear-softmax-v1")
    if architecture == "linear-softmax-v1":
        weights = model.get("weights") or []
        bias = model.get("bias") or []
        if feature_width is not None and len(weights) != feature_width:
            raise ValueError("Checkpoint feature dimension mismatch")
        if len(bias) != 3 or any(len(row) != 3 for row in weights):
            raise ValueError("Linear checkpoint action dimension mismatch")
    elif architecture == "mlp-relu-v1":
        input_weights = model.get("inputWeights") or []
        input_bias = model.get("inputBias") or []
        output_weights = model.get("outputWeights") or []
        output_bias = model.get("outputBias") or []
        if feature_width is not None and len(input_weights) != feature_width:
            raise ValueError("Checkpoint feature dimension mismatch")
        hidden = len(input_bias)
        if hidden <= 0 or any(len(row) != hidden for row in input_weights):
            raise ValueError("MLP hidden dimension mismatch")
        if len(output_weights) != hidden or any(len(row) != 3 for row in output_weights) or len(output_bias) != 3:
            raise ValueError("MLP action dimension mismatch")
    else:
        raise ValueError(f"Unsupported checkpoint architecture: {architecture}")
