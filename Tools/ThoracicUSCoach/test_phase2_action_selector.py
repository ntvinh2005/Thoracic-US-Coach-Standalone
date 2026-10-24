from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image

from active2d_action import (
    ALLOWED_ACTIONS,
    ActionTransition,
    build_action_transitions,
    factual_predictions,
    fit_random_relu_ridge,
    transition_features,
    validate_action_model,
    validate_u08_manifest,
)
from active2d_data import Active2DFrame, load_active2d_frames


def frame(
    root: Path,
    episode: str,
    family: str,
    split: str,
    index: int,
    action: str,
    score: float,
    *,
    accepted: bool = True,
    contact: bool = True,
    workspace: bool = True,
) -> Active2DFrame:
    image = root / f"{episode}_{index}.png"
    image.write_bytes(b"synthetic")
    return Active2DFrame(
        image=image,
        image_relative=image.name,
        image_sha256=f"sha-{episode}-{index}",
        episode_id=episode,
        dataset_run_id="u08-test",
        split=split,
        family=family,
        seed=1,
        index=index,
        issued_action=action,
        action_accepted=accepted,
        episode_reset=index == 0,
        level="T7",
        t7_distance_mm=0.0,
        t7_acceptable=True,
        target_acceptable=score > 0.8,
        target_score=score,
        contact_valid=contact,
        workspace_valid=workspace,
    )


class Phase2ActionSelectorTests(unittest.TestCase):
    def test_offline_predictive_result_cannot_claim_navigation_pass(self) -> None:
        tool_root = Path(__file__).resolve().parent
        trainer = (tool_root / "train_phase2_action_selector.py").read_text(encoding="utf-8")
        evaluator = (tool_root / "evaluate_phase2_action_selector.py").read_text(encoding="utf-8")
        for source in (trainer, evaluator):
            self.assertIn("TRAINED_NO_IMPROVEMENT", source)
            self.assertNotIn('status = "TRAINED_DEV_PASS"', source)
            self.assertIn('"closedLoopAcquisitionEvaluated": False', source)

    def test_split_filter_happens_before_locked_image_or_annotation_reads(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dataset = root / "Artifacts/ThoracicUSCoach/active2d_dataset"
            train = dataset / "train/episode"
            test = dataset / "test/locked"
            train.mkdir(parents=True)
            test.mkdir(parents=True)
            image = train / "frame.png"
            Image.new("L", (4, 4), 127).save(image)
            image_hash = hashlib.sha256(image.read_bytes()).hexdigest()
            relative = image.relative_to(root).as_posix()
            base = {
                "schemaVersion": 1,
                "contract": "active2d_image_only_v1",
                "experimentId": "thoracic_active2d_image_only_v1_20261023",
                "datasetRunId": "run",
                "captureStatus": "complete",
            }
            (train / "stream_manifest.json").write_text(json.dumps({
                **base, "episodeId": "train", "split": "train", "family": "train-family", "seed": 1,
                "frames": [{"index": 0, "image": relative, "imageSha256": image_hash,
                            "issuedAction": "RESET", "actionAccepted": True, "episodeReset": True}],
            }), encoding="utf-8")
            (train / "privileged_annotations.json").write_text(json.dumps({
                "contract": base["contract"], "episodeId": "train", "frames": [{
                    "index": 0, "nearestLevel": "T7", "t7DistanceMm": 0,
                    "t7LevelAcceptable": True, "finalTargetAcceptable": False,
                    "finalTargetScore": 0, "contactValid": True, "workspaceValid": True,
                }],
            }), encoding="utf-8")
            # A locked manifest deliberately has no annotation/image. Loading
            # train must not touch it.
            (test / "stream_manifest.json").write_text(json.dumps({
                **base, "episodeId": "test", "split": "test", "family": "test-family", "seed": 2,
                "frames": [{"index": 0, "image": "missing.png", "imageSha256": "missing",
                            "issuedAction": "RESET", "actionAccepted": True, "episodeReset": True}],
            }), encoding="utf-8")
            frames = load_active2d_frames("run", root=root, splits={"train"})
            self.assertEqual(["train"], [item.episode_id for item in frames])

    def test_transition_uses_pre_action_state_and_privileged_delta_only_as_label(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frames = [
                frame(root, "E", "F", "train", 0, "RESET", 0.10),
                frame(root, "E", "F", "train", 1, "LATERAL", 0.35),
                frame(root, "E", "F", "train", 2, "MEDIAL", 0.20),
            ]
            rows = build_action_transitions(frames)
            self.assertEqual(2, len(rows))
            self.assertEqual(0, rows[0].state.index)
            self.assertEqual(1, rows[0].outcome.index)
            self.assertEqual("LATERAL", rows[0].action)
            self.assertAlmostEqual(0.25, rows[0].delta_score)
            self.assertEqual(1, rows[1].state.index)
            self.assertAlmostEqual(-0.15, rows[1].delta_score)

    def test_rejected_or_unsafe_outcomes_are_excluded(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frames = [
                frame(root, "E", "F", "train", 0, "RESET", 0.0),
                frame(root, "E", "F", "train", 1, "LATERAL", 0.1, accepted=False),
                frame(root, "E", "F", "train", 2, "MEDIAL", 0.2, workspace=False),
            ]
            self.assertEqual([], build_action_transitions(frames))

    def test_family_split_leakage_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frames = [
                frame(root, "E1", "shared", "train", 0, "RESET", 0.0),
                frame(root, "E1", "shared", "train", 1, "LATERAL", 0.1),
                frame(root, "E2", "shared", "dev", 0, "RESET", 0.0),
                frame(root, "E2", "shared", "dev", 1, "MEDIAL", 0.1),
            ]
            with self.assertRaisesRegex(ValueError, "leakage"):
                build_action_transitions(frames)

    def test_spatial_features_are_taken_from_pre_action_image(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state = frame(root, "E", "F", "train", 0, "RESET", 0.0)
            outcome = frame(root, "E", "F", "train", 1, "LATERAL", 0.1)
            row = ActionTransition("E", "train", "F", state, state, outcome, "LATERAL", 0.1)
            seen: list[str] = []

            def fake(path: str) -> np.ndarray:
                seen.append(path)
                return np.zeros(1024, dtype=np.float32)

            with patch("active2d_action._spatial", side_effect=fake):
                matrix = transition_features([row], "spatial-v2")
            self.assertEqual((1, 1024), matrix.shape)
            self.assertEqual([str(state.image.resolve())], seen)

    def test_model_schema_is_image_only_and_scores_both_actions(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            rows = []
            values = []
            for index in range(12):
                action = ALLOWED_ACTIONS[index % 2]
                state = frame(root, f"E{index}", f"F{index}", "train", 0, "RESET", 0.0)
                outcome = frame(root, f"E{index}", f"F{index}", "train", 1, action, 0.1)
                rows.append(ActionTransition(
                    state.episode_id, "train", state.family, state, state, outcome,
                    action, 0.1 if action == "LATERAL" else -0.1,
                ))
                values.append([index, index % 3, index % 5, 1.0])
            features = np.asarray(values, dtype=np.float32)
            model = fit_random_relu_ridge(
                features, rows, feature_mode="synthetic", hidden_size=2, ridge=0.1, seed=13
            )
            validate_action_model(model)
            predictions = factual_predictions(model, features, rows)
            self.assertEqual((12,), predictions.shape)
            serialized = str(model)
            for forbidden in ("targetCoordinates", "pathIndex", "oracleDirection"):
                self.assertIn(forbidden, serialized)  # declared forbidden, never a model input
            self.assertEqual(
                ["cleanUltrasoundImage", "policyOwnedMemory", "policyIssuedActionHistory"],
                model["policyInputs"],
            )

    def test_manifest_matrix_is_bounded(self) -> None:
        manifest = {
            "schemaVersion": 1,
            "taskId": "U08",
            "contract": "active2d_image_only_v1",
            "experimentId": "thoracic_active2d_image_only_v1_20261023",
            "allowedActions": ["MEDIAL", "LATERAL"],
            "selectionSplits": ["train", "dev"],
            "lockedTestSplit": "test",
            "configurations": [
                {"featureMode": "spatial-v2", "seeds": [13, 47]},
                {"featureMode": "history-v1", "seeds": [13, 47]},
            ],
        }
        validate_u08_manifest(manifest)
        manifest["configurations"][0]["seeds"] = [13, 29, 47]
        with self.assertRaisesRegex(ValueError, "seeds"):
            validate_u08_manifest(manifest)


if __name__ == "__main__":
    unittest.main()
