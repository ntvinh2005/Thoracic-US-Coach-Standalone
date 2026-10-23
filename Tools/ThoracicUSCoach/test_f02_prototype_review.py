from types import SimpleNamespace
import json
from pathlib import Path

import numpy as np

from review_u05_prototypes_f02 import blinded_sample, select_examples


def test_blinded_sample_contains_no_evaluator_metadata() -> None:
    sample = {"sampleCode": "P0-S1", "prototypeId": 0, "selectionRole": "closest_diverse_matched",
              "actualLevelEvaluatorOnly": "T7", "episodeId": "secret", "split": "test", "image": "secret.png"}
    result = blinded_sample(sample)
    assert result == {"sampleCode": "P0-S1", "prototypeId": 0,
                      "selectionRole": "closest_diverse_matched"}
    assert not ({"actualLevelEvaluatorOnly", "episodeId", "split", "image"} & set(result))


def test_selection_caps_matched_examples_at_five_and_adds_diagnostics() -> None:
    frames = [SimpleNamespace(family=f"f{i}", split="dev", level="T7", t7_acceptable=True,
                              episode_id=f"e{i}", index=i, image_relative=f"i{i}.png", image=f"i{i}.png")
              for i in range(8)]
    assignments = [{"prototype": 0, "status": "MATCHED", "distance": .01 * i, "margin": .2} for i in range(6)]
    assignments += [{"prototype": 0, "status": "AMBIGUOUS", "distance": .07, "margin": .001},
                    {"prototype": 0, "status": "UNMATCHED", "distance": .2, "margin": .2}]
    distances = np.ones((8, 1), np.float32)
    distances[:, 0] = np.asarray([x["distance"] for x in assignments])
    selected = select_examples(0, frames, assignments, distances, .1)
    assert sum(x["assignmentStatus"] == "MATCHED" for x in selected) == 5
    assert {x["assignmentStatus"] for x in selected} == {"MATCHED", "AMBIGUOUS", "UNMATCHED"}


def test_generated_evidence_package_is_blinded_and_reconciled() -> None:
    root = Path(__file__).resolve().parents[2]
    path = root / "Artifacts/ThoracicUSCoach/active2d/diagnostics/u05_f02/u05_f02_prototype_review_v1.json"
    report = json.loads(path.read_text(encoding="utf-8"))
    assert report["result"] == "EVIDENCE READY"
    assert report["anatomicalValidation"] == "HUMAN_REVIEW_PENDING"
    assert report["assignmentCoverage"]["frames"] == sum(report["assignmentCoverage"]["statusCounts"].values())
    forbidden = {"actualLevelEvaluatorOnly", "episodeId", "family", "split", "image"}
    assert all(not (forbidden & set(sample))
               for samples in report["blindedManifest"].values() for sample in samples)
    for prototype, samples in report["relevantPrototypeEvidence"].items():
        matched = sum(sample["assignmentStatus"] == "MATCHED" for sample in samples)
        assert 3 <= matched <= 5, prototype
    assert report["p5Investigation"]["counts"]["dev"]["nearest"] == 0
    assert report["p5Investigation"]["thresholdChanged"] is False
