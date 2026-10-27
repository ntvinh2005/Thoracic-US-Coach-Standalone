from __future__ import annotations

import math
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image


SCHEMA = "active2d-diagnostic-template-stop-v1"


def normalized_template_features(path: Path, size: int = 32) -> np.ndarray:
    with Image.open(path) as image:
        values = np.asarray(
            image.convert("L").resize((size, size), Image.Resampling.BILINEAR),
            dtype=np.float32,
        ).reshape(-1)
    values -= float(values.mean())
    norm = float(np.linalg.norm(values))
    if norm <= 1e-6:
        raise ValueError(f"Template image has no structural contrast: {path}")
    return values / norm


def validate_template_checkpoint(model: dict[str, Any]) -> None:
    if model.get("schema") != SCHEMA:
        raise ValueError("Diagnostic template checkpoint schema mismatch")
    if model.get("policyInputs") != ["cleanUltrasoundImage"]:
        raise ValueError("Diagnostic template recognizer must be image-only")
    if model.get("deployment", {}).get("status") != "DIAGNOSTIC_ONLY":
        raise ValueError("Diagnostic template checkpoint cannot use the production loader")
    architecture = model.get("architecture") or {}
    if architecture.get("type") != "normalized-grayscale-template-nn-v1":
        raise ValueError("Unsupported diagnostic template architecture")
    width = int(architecture.get("imageSize", 0)) ** 2
    positive = architecture.get("positiveTemplates") or []
    negative = architecture.get("negativeTemplates") or []
    if width <= 0 or not positive or not negative:
        raise ValueError("Diagnostic template references are missing")
    if any(len(item) != width for item in [*positive, *negative]):
        raise ValueError("Diagnostic template width mismatch")
    rule = model.get("operatingRule") or {}
    if not -1.0 <= float(rule.get("minimumPositiveSimilarity", -2.0)) <= 1.0:
        raise ValueError("Diagnostic template similarity is invalid")
    if float(rule.get("temperature", 0.0)) <= 0.0:
        raise ValueError("Diagnostic template temperature must be positive")
    if float(model.get("stopRule", {}).get("threshold", -1.0)) != 0.5:
        raise ValueError("Diagnostic template decision threshold must remain 0.5")


def template_components(model: dict[str, Any], image_path: Path) -> tuple[float, float, float]:
    validate_template_checkpoint(model)
    architecture = model["architecture"]
    feature = normalized_template_features(image_path, int(architecture["imageSize"]))
    positive = np.asarray(architecture["positiveTemplates"], dtype=np.float32)
    negative = np.asarray(architecture["negativeTemplates"], dtype=np.float32)
    positive_similarity = float(np.max(positive @ feature))
    negative_similarity = float(np.max(negative @ feature))
    return positive_similarity, negative_similarity, positive_similarity - negative_similarity


def template_predictions(model: dict[str, Any], image_path: Path) -> tuple[float, float]:
    positive_similarity, _, _ = template_components(model, image_path)
    rule = model["operatingRule"]
    evidence = positive_similarity - float(rule["minimumPositiveSimilarity"])
    temperature = float(rule["temperature"])
    confidence = 1.0 / (1.0 + math.exp(-max(min(evidence / temperature, 30.0), -30.0)))
    quality = float(np.clip((positive_similarity + 1.0) * 0.5, 0.0, 1.0))
    return confidence, quality
