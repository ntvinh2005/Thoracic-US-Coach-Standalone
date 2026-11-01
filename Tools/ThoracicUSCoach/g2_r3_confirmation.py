"""Blind G2-R3 topology confirmation; this module never reads evaluator artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt

from g2_r3_topology_discriminator import (
    MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
    apply_shadow_topology_gate,
    candidate_topology_measurements,
)
from relative_spine_e2_analysis import load_gray


FORBIDDEN_KEYS = (
    "level", "t7", "world", "anatom", "mesh", "provenance", "targetdistance",
    "referencecoordinate", "probeposition", "contactpoint",
)
ALLOWED_INFERRED_KEYS = ("levels", "inferredLevel", "predictedT7RelativeMm")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def assert_actor_isolation(value: Any, path: str = "") -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            normalized = key.lower().replace("_", "")
            if (key not in ALLOWED_INFERRED_KEYS
                    and any(token in normalized for token in FORBIDDEN_KEYS)):
                raise ValueError(f"Forbidden actor key at {path}/{key}")
            assert_actor_isolation(child, f"{path}/{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            assert_actor_isolation(child, f"{path}/{index}")


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def draw_waveform(topology: dict[str, Any], gate: dict[str, Any], output: Path) -> None:
    candidates = topology["candidates"]
    accepted = {item["candidateId"] for item in gate["accepted"]}
    figure, axis = plt.subplots(figsize=(12, 5))
    for candidate in candidates:
        color = "tab:blue" if candidate["candidateId"] in accepted else "tab:orange"
        axis.scatter(candidate["actorRelativeCoordinateMm"],
                     candidate["minimumDistalObservationPixels"], color=color, s=65)
        axis.text(candidate["actorRelativeCoordinateMm"],
                  candidate["minimumDistalObservationPixels"] + 2,
                  candidate["candidateId"], ha="center", fontsize=8)
    axis.axhline(MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
                 color="tab:red", linestyle="--", label="frozen distal-field minimum")
    axis.set_xlabel("Actor-relative displacement from Home (mm)")
    axis.set_ylabel("Distal field available after tracked interface (pixels)")
    axis.set_title("Frozen G2-R3 shadow-topology verification")
    axis.grid(alpha=0.25)
    axis.legend()
    figure.tight_layout()
    figure.savefig(output, dpi=180)
    plt.close(figure)


def draw_contact_sheet(source_root: Path, gate: dict[str, Any], output: Path) -> None:
    candidates = gate["accepted"] + gate["ambiguous"]
    figure, axes = plt.subplots(2, max(1, (len(candidates) + 1) // 2),
                                figsize=(3 * max(1, (len(candidates) + 1) // 2), 6))
    axes = list(getattr(axes, "flat", [axes]))
    for axis, candidate in zip(axes, candidates):
        image = load_gray(source_root / candidate["supportingImages"][0])
        axis.imshow(image, cmap="gray", vmin=0, vmax=1)
        axis.set_title(f"{candidate['candidateId']}\n{candidate['decision']}", fontsize=8)
        axis.axis("off")
    for axis in axes[len(candidates):]:
        axis.axis("off")
    figure.tight_layout()
    figure.savefig(output, dpi=180)
    plt.close(figure)


def freeze_blind_outputs(output_root: Path, relative_paths: list[str]) -> dict[str, Any]:
    manifest = {
        "contract": "thoracic_g2_r3_blind_freeze_v1",
        "files": {name: sha256(output_root / name) for name in relative_paths},
    }
    write_json(output_root / "blind_results.sha256", manifest)
    return manifest


def verify_blind_outputs(output_root: Path) -> bool:
    manifest = json.loads((output_root / "blind_results.sha256").read_text())
    return all(sha256(output_root / name) == expected
               for name, expected in manifest["files"].items())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--candidate-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    source_root = args.source_root.resolve()
    output_root = args.output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=False)
    topology = candidate_topology_measurements(
        source_root, args.candidate_root.resolve()
    )
    gate = apply_shadow_topology_gate(topology)
    actor_candidates = {
        "contract": "thoracic_g2_r3_blind_event_candidates_v1",
        "inputIsolation": gate["inputIsolation"],
        "accepted": gate["accepted"],
        "ambiguous": gate["ambiguous"],
        "rejected": gate["rejected"],
    }
    assert_actor_isolation(actor_candidates)
    assert_actor_isolation(gate["relativeSpineMap"])
    write_json(output_root / "event_candidates.json", actor_candidates)
    write_json(output_root / "event_morphology_diagnostics.json", topology)
    write_json(output_root / "relative_spine_map.json", gate["relativeSpineMap"])
    configuration = {
        "contract": "thoracic_g2_r3_frozen_confirmation_configuration_v1",
        "featureFamily": gate["featureFamily"],
        "minimumDistalShadowObservationPixels":
            MINIMUM_DISTAL_SHADOW_OBSERVATION_PIXELS,
        "acquisition": {
            "lateralOffsetMm": -6.0,
            "surfaceYawDeg": 91.0,
            "stepMm": 1.5,
            "variationFromDevelopment": "surface yaw +1 degree",
        },
        "eventScoringToleranceMm": 5.0,
        "coordinateProjection": "renderer linear texture-x to bottom-footprint scan-plane-x",
    }
    write_json(output_root / "frozen_configuration.json", configuration)
    draw_waveform(topology, gate, output_root / "anatomical_waveform.png")
    draw_contact_sheet(source_root, gate, output_root / "event_support_contact_sheet.png")
    freeze_blind_outputs(output_root, [
        "event_candidates.json", "event_morphology_diagnostics.json",
        "relative_spine_map.json", "frozen_configuration.json",
        "anatomical_waveform.png", "event_support_contact_sheet.png",
    ])
    print(json.dumps({
        "accepted": len(gate["accepted"]),
        "ambiguous": len(gate["ambiguous"]),
        "predictedT7RelativeMm": gate["relativeSpineMap"]["predictedT7RelativeMm"],
        "blindFreezeValid": verify_blind_outputs(output_root),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
