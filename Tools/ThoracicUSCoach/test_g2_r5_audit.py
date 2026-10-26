from __future__ import annotations

import json
import math
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

import g2_r5_audit as audit


class G2R5AuditTests(unittest.TestCase):
    def test_position_and_quaternion_distance(self) -> None:
        self.assertAlmostEqual(audit.position_distance(
            {"x": 0, "y": 0, "z": 0}, {"x": 3, "y": 4, "z": 0}), 5.0)
        identity = {"x": 0, "y": 0, "z": 0, "w": 1}
        yaw_3 = {"x": 0, "y": math.sin(math.radians(1.5)), "z": 0, "w": math.cos(math.radians(1.5))}
        self.assertAlmostEqual(audit.rotation_distance(identity, yaw_3), 3.0, places=6)

    def test_bidirectional_overlap_is_not_one_way(self) -> None:
        def row(x: float) -> dict:
            return {"probePosition": {"x": x, "y": 0, "z": 0}, "probeRotation": {"x": 0, "y": 0, "z": 0, "w": 1}}
        self.assertEqual(audit.coverage_overlap([row(0), row(10)], [row(0)], position_mm=1, angle_deg=1.5), (0.5, 1.0))

    def test_union_find_transitively_groups_ancestry(self) -> None:
        union = audit.UnionFind(["a", "b", "c", "d"])
        union.union("a", "b")
        union.union("b", "c")
        self.assertEqual(union.components(), [["a", "b", "c"], ["d"]])

    def test_rms_uses_declared_grayscale_128_representation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "frame.png"
            Image.fromarray(np.full((32, 32), 255, dtype=np.uint8), mode="L").save(path)
            pixels = audit.grayscale_128(path)
            self.assertEqual(pixels.shape, (128 * 128,))
            self.assertAlmostEqual(audit.rms(pixels, np.zeros_like(pixels)), 1.0)

    def test_frozen_protocol_has_no_runtime_tunable_gate(self) -> None:
        repo = Path(__file__).resolve().parents[2]
        protocol = json.loads((repo / audit.PROTOCOL).read_text(encoding="utf-8"))
        self.assertTrue(protocol["frozenBeforeR5AInspection"])
        self.assertEqual(protocol["spatialNovelty"]["primary"]["poseMatchPositionMm"], 1.0)
        self.assertEqual(protocol["spatialNovelty"]["primary"]["poseMatchAngleDeg"], 1.5)
        self.assertEqual(protocol["spatialNovelty"]["primary"]["minimumBidirectionalCoverageFraction"], 0.2)
        self.assertAlmostEqual(protocol["visualNovelty"]["minimumNovelRms128"], 0.013763083470985293)
        self.assertFalse(protocol["restrictions"]["goCanBeRelaxedPostHoc"])

    def test_merkle_equivalent_is_order_independent(self) -> None:
        self.assertEqual(audit.merkle_equivalent(["b", "a"]), audit.merkle_equivalent(["a", "b"]))

    def test_canonical_text_hash_ignores_checkout_line_endings(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            left, right = Path(directory) / "left", Path(directory) / "right"
            left.write_bytes(b"one\ntwo\n")
            right.write_bytes(b"one\r\ntwo\r\n")
            self.assertEqual(audit.canonical_text_sha256(left), audit.canonical_text_sha256(right))

    def test_nearest_reference_rms(self) -> None:
        reference = np.asarray([[0.0, 0.0], [1.0, 1.0]], dtype=np.float32)
        query = np.asarray([[0.0, 0.0], [0.5, 0.5]], dtype=np.float32)
        np.testing.assert_allclose(audit.nearest_reference_rms(reference, query), [0.0, 0.5], atol=1e-7)

    def test_pairwise_subset_excludes_renderer_equivalent_component(self) -> None:
        representatives = {
            "a": np.asarray([0.0, 0.0], dtype=np.float32),
            "b": np.asarray([1.0, 1.0], dtype=np.float32),
            "c": np.asarray([1.001, 1.001], dtype=np.float32),
        }
        selected = audit.largest_pairwise_separated_subset(list(representatives), representatives, 0.01)
        self.assertEqual(len(selected), 2)
        self.assertIn("a", selected)

    def test_contract_identities_are_canonical_and_frozen(self) -> None:
        repo = Path(__file__).resolve().parents[2]
        identity = json.loads((repo / audit.IDENTITY).read_text(encoding="utf-8"))
        for row in identity["files"]:
            if row["role"] in {"coordinator_protocol", "evidence_schema", "independent_audit_protocol"}:
                self.assertEqual(audit.canonical_text_sha256(repo / row["path"]), row["canonicalLfSha256"])

    def test_decision_is_evidence_based_not_justification_based(self) -> None:
        inconclusive, _ = audit.classify_scout_decision(
            positive=3, negative=2, union=5, hard_negative=1, justified_route=False,
            exhausted=False, spatially_novel=5, visually_novel=5)
        self.assertEqual(inconclusive, "FEASIBILITY_INCONCLUSIVE")
        insufficient, collapse = audit.classify_scout_decision(
            positive=0, negative=0, union=0, hard_negative=0, justified_route=True,
            exhausted=True, spatially_novel=0, visually_novel=0)
        self.assertEqual(insufficient, "INSUFFICIENT_DIVERSITY_ON_CURRENT_ANATOMY")
        self.assertTrue(collapse)

    def test_shared_start_proposal_is_an_ancestry_edge(self) -> None:
        protocol = {
            "spatialNovelty": {
                "primary": {"poseMatchPositionMm": 1.0, "poseMatchAngleDeg": 1.5, "minimumBidirectionalCoverageFraction": 0.2},
                "conservativeSensitivity": {"nearStartPositionMm": 3.1, "nearStartAngleDeg": 3.1},
            }
        }
        identity = {"x": 0, "y": 0, "z": 0, "w": 1}
        records = []
        for route_id, x in (("left", 0.0), ("right", 50.0)):
            records.append({
                "routeId": route_id, "startProposalId": "shared",
                "poseTrace": {"afterPosition": {"x": x, "y": 0, "z": 0}, "afterRotation": identity},
            })
        result = audit.spatial_component_audit({"frames": [], "trajectories": []}, records, protocol, conservative=False)
        self.assertEqual(len(result["components"]), 1)
        self.assertEqual(len(result["ancestryEdges"]), 1)

    def test_development_route_requires_positive_negative_credible_components(self) -> None:
        records = [
            {"recordId": "p", "routeId": "rp", "offlineAnnotation": {"acceptable": True}},
            {"recordId": "n", "routeId": "rn", "offlineAnnotation": {"acceptable": False}},
        ]
        route = {
            "strategy": "bounded extension", "whyScoutSupportsIt": "two credible components",
            "supportingRecordIds": ["p", "n"],
            "expectedAdditionalIndependentPositiveComponents": 1,
            "expectedAdditionalIndependentNegativeComponents": 1,
        }
        result = audit.evaluate_development_route(route, records, {"rp": "cp", "rn": "cn"}, {"cp", "cn"})
        self.assertTrue(result["justified"])
        route["supportingRecordIds"] = ["p"]
        result = audit.evaluate_development_route(route, records, {"rp": "cp", "rn": "cn"}, {"cp", "cn"})
        self.assertFalse(result["justified"])


if __name__ == "__main__":
    unittest.main()
