from __future__ import annotations

import unittest

from g2_p2_recognition import (
    classify_readiness,
    experiment_plan,
    materialize_component_split,
    p2_checkpoint_envelope,
    run_p2_diagnostic_inference,
    training_scope,
    validate_actor_features,
    validate_p2_checkpoint,
    validate_split_no_leakage,
    verify_preprocessing_parity,
)


def components() -> list[dict]:
    rows = []
    for label in ("positive", "negative"):
        for index in range(6):
            rows.append({
                "componentId": f"{label}-{index}",
                "label": label,
                "independenceVerified": True,
                "conservativeComponentId": f"{label}-conservative-{index}",
                "ancestryKeys": [f"{label}-ancestry-{index}"],
                "recordIds": [f"{label}-record-{index}"],
            })
    return rows


class Prompt2RecognitionContractTests(unittest.TestCase):
    def test_readiness_uses_frozen_engineering_and_scientific_counts(self) -> None:
        evidence = {
            "integrityStatus": "PASS", "imagePoseFreshness": "PASS", "positiveBasin": "PASS",
            "safeTargetReachability": "PASS", "positiveUniquePoseObservations": 24,
            "negativeUniquePoseObservations": 48, "primaryIndependentPositiveGroups": 6,
            "primaryIndependentNegativeGroups": 6, "primaryIndependentHardNegativeGroups": 2,
            "conservativeIndependentPositiveGroups": 6, "conservativeIndependentNegativeGroups": 6,
            "positiveStructuralClusters": 3, "negativeStructuralClusters": 3,
            "allCountedObservationsDecisionSupportEligible": True, "readinessDependsOnMissingRaw": False,
            "splitIntegrity": "PASS", "minimumRolesPerLabel": {"train": 3, "calibration": 1, "heldout_development": 2},
        }
        result = classify_readiness(evidence)
        self.assertTrue(result["engineeringDemoDataReady"])
        self.assertTrue(result["scientificTrainingDataReady"])

    def test_missing_raw_dependency_fails_readiness(self) -> None:
        result = classify_readiness({"readinessDependsOnMissingRaw": True})
        self.assertFalse(result["engineeringDemoDataReady"])
        self.assertFalse(result["scientificTrainingDataReady"])

    def test_no_readiness_means_no_training(self) -> None:
        readiness = {"engineeringDemoDataReady": False, "scientificTrainingDataReady": False}
        self.assertEqual(training_scope(readiness), "INSUFFICIENT_INDEPENDENT_DATA")
        self.assertFalse(experiment_plan(readiness, split_sha256=None, causal_history_verified=False)["trainingAuthorized"])

    def test_scientific_split_is_component_disjoint(self) -> None:
        result = materialize_component_split(
            components(), split_protocol_id="G2_P2_COMPONENT_SPLIT_V1", scope="SCIENTIFIC_DEVELOPMENT"
        )
        self.assertEqual(len(result["splitSha256"]), 64)
        for label in ("positive", "negative"):
            roles = [item["role"] for item in result["assignments"] if item["label"] == label]
            self.assertEqual(roles.count("heldout_development"), 2)
            self.assertEqual(roles.count("calibration"), 1)
            self.assertEqual(roles.count("train"), 3)

    def test_shared_ancestry_across_roles_is_rejected(self) -> None:
        rows = [
            {"componentId": "a", "role": "train", "recordIds": ["1"], "ancestryKeys": ["shared"], "conservativeComponentId": "ca"},
            {"componentId": "b", "role": "calibration", "recordIds": ["2"], "ancestryKeys": ["shared"], "conservativeComponentId": "cb"},
        ]
        with self.assertRaisesRegex(ValueError, "ancestry crosses roles"):
            validate_split_no_leakage(rows)

    def test_privileged_actor_field_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "privileged actor features"):
            validate_actor_features({
                "cleanUltrasoundImage": "frame.png", "policyOwnedMemory": {},
                "policyIssuedActionHistory": [], "probePosition": [0, 0, 0]
            })

    def test_undeclared_actor_field_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "undeclared actor features"):
            validate_actor_features({
                "cleanUltrasoundImage": "frame.png", "policyOwnedMemory": {},
                "policyIssuedActionHistory": [], "futureHint": "forbidden",
            })

    def test_rejected_action_cannot_be_encoded_as_motion(self) -> None:
        with self.assertRaisesRegex(ValueError, "encoded as executed motion"):
            validate_actor_features({
                "cleanUltrasoundImage": "frame.png",
                "policyOwnedMemory": {},
                "policyIssuedActionHistory": [{"issued": "LEFT", "executed": "LEFT", "accepted": False, "completed": False}],
            })

    def test_diagnostic_interface_separates_quality_and_never_authorizes_stop(self) -> None:
        checkpoint = p2_checkpoint_envelope(
            candidate_id="compact_single_frame_cnn", scope="ENGINEERING_DEMO_ONLY",
            readiness_sha256="a" * 64, split_sha256="b" * 64,
            model_payload={"architecture": "test-backend"}, diagnostic_operating_point=0.8,
        )
        actor_input = {
            "cleanUltrasoundImage": "frame.png", "policyOwnedMemory": {},
            "policyIssuedActionHistory": [],
        }
        low_recognition = run_p2_diagnostic_inference(
            checkpoint, actor_input,
            lambda _actor, _model: {"recognitionProbability": 0.2, "qualityEstimate": 1.0},
        )
        self.assertFalse(low_recognition["diagnosticRecognitionCandidate"])
        self.assertFalse(low_recognition["qualityUsedForRecognition"])
        self.assertFalse(low_recognition["stopAuthorized"])
        recognized = run_p2_diagnostic_inference(
            checkpoint, actor_input,
            lambda _actor, _model: {"recognitionProbability": 0.9, "qualityEstimate": 0.0},
        )
        self.assertTrue(recognized["diagnosticRecognitionCandidate"])
        self.assertFalse(recognized["stopAuthorized"])

    def test_diagnostic_interface_rejects_invalid_backend_output(self) -> None:
        checkpoint = p2_checkpoint_envelope(
            candidate_id="structural_v4_equivalent", scope="SCIENTIFIC_DEVELOPMENT",
            readiness_sha256="a" * 64, split_sha256="b" * 64,
            model_payload={}, diagnostic_operating_point=0.8,
        )
        actor_input = {
            "cleanUltrasoundImage": "frame.png", "policyOwnedMemory": [],
            "policyIssuedActionHistory": [],
        }
        with self.assertRaisesRegex(ValueError, "outside"):
            run_p2_diagnostic_inference(
                checkpoint, actor_input,
                lambda _actor, _model: {"recognitionProbability": 1.1, "qualityEstimate": 0.5},
            )

    def test_blocked_checkpoint_rejected_by_normal_loader(self) -> None:
        checkpoint = p2_checkpoint_envelope(
            candidate_id="compact_single_frame_cnn",
            scope="ENGINEERING_DEMO_ONLY",
            readiness_sha256="a" * 64,
            split_sha256="b" * 64,
            model_payload={"weights": []},
            diagnostic_operating_point=0.8,
        )
        with self.assertRaisesRegex(ValueError, "non-deployable"):
            validate_p2_checkpoint(checkpoint)
        validate_p2_checkpoint(checkpoint, diagnostic_harness=True)

    def test_offline_live_preprocessing_must_match(self) -> None:
        result = verify_preprocessing_parity(
            [[0.1, 0.2], [0.3, 0.4]], [[0.1, 0.2], [0.3, 0.4]], feature_extractor="structural-v4"
        )
        self.assertTrue(result["pass"])
        with self.assertRaisesRegex(ValueError, "preprocessing mismatch"):
            verify_preprocessing_parity(
                [[0.1, 0.2]], [[0.1, 0.21]], feature_extractor="structural-v4"
            )


if __name__ == "__main__":
    unittest.main()
