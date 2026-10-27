from __future__ import annotations

import itertools
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from g2_p2_freshness_audit import canonical_sha256, read_json, write_json
from g2_r6_audit import sha256
from g2_p3_stage_b_final_audit import (
    PROTOCOL,
    _adapt_basin_protocol,
    materialize_refinement_grid,
    structural_variation,
    validate_p3_grid,
)


ROOT = Path(__file__).resolve().parents[2]
PROTOCOL_VALUE = read_json(ROOT / PROTOCOL)


def manifest() -> dict:
    candidates = []
    pairs = []
    for control_index, control in enumerate(PROTOCOL_VALUE["fixedGridContract"]["historicalControlIds"]):
        anchor = f"c{control_index}_anchor"
        candidates.append({
            "candidateId": anchor, "anchorControlId": control, "stage": "anchor",
            "centerCandidateId": "ANCHOR", "longitudinalOffsetMm": 0.0,
            "lateralOffsetMm": 0.0, "yawDeg": 0.0,
        })
        for stage, magnitude in (("coarse", 1.0), ("wider", 2.0)):
            ids = {}
            for offset in itertools.product((-magnitude, 0.0, magnitude), repeat=3):
                if offset == (0.0, 0.0, 0.0):
                    continue
                token = "_".join("n" if value < 0 else "p" if value > 0 else "z" for value in offset)
                item = {
                    "candidateId": f"c{control_index}_{stage}_{token}",
                    "anchorControlId": control, "stage": stage, "centerCandidateId": anchor,
                    "longitudinalOffsetMm": offset[0], "lateralOffsetMm": offset[1], "yawDeg": offset[2],
                }
                candidates.append(item)
                ids[offset] = item["candidateId"]
                pairs.append([anchor, item["candidateId"]])
            for offset, candidate_id in ids.items():
                opposite = tuple(-value for value in offset)
                if offset < opposite:
                    pairs.append([candidate_id, ids[opposite]])
    template = [
        {"longitudinalOffsetMm": offset[0], "lateralOffsetMm": offset[1], "yawDeg": offset[2]}
        for offset in itertools.product((-0.5, 0.0, 0.5), repeat=3)
    ]
    return {
        "mapCandidates": candidates,
        "neighborPairs": pairs,
        "refinement": {
            "declaredBeforeCoarseCapture": True,
            "selectionRule": PROTOCOL_VALUE["fixedGridContract"]["refinement"]["selectionRule"],
            "offsetTemplate": template,
            "coarseSnapshotFile": "stage_b_coarse_snapshot.json",
            "selectionManifestFile": "refinement_selection_manifest.json",
            "maximumSlotsPerAnchor": 27,
            "postOutcomeExtensionAllowed": False,
        },
        "outcomeAdaptiveCandidateAddition": False,
    }


class Prompt3StageBFinalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.adapted, errors = _adapt_basin_protocol(ROOT, PROTOCOL_VALUE)
        if errors:
            raise AssertionError(errors)

    def test_complete_anchor_small_and_wider_symmetric_grid_passes(self) -> None:
        self.assertEqual(validate_p3_grid(manifest(), PROTOCOL_VALUE, self.adapted), [])

    def test_missing_wider_stage_fails(self) -> None:
        value = manifest()
        value["mapCandidates"] = [item for item in value["mapCandidates"] if item["stage"] != "wider"]
        value["neighborPairs"] = [
            pair for pair in value["neighborPairs"]
            if all(any(item["candidateId"] == endpoint for item in value["mapCandidates"]) for endpoint in pair)
        ]
        errors = validate_p3_grid(value, PROTOCOL_VALUE, self.adapted)
        self.assertTrue(any("required stage missing" in error and "wider" in error for error in errors))

    def test_exact_anchor_is_required_per_control(self) -> None:
        value = manifest()
        value["mapCandidates"] = [item for item in value["mapCandidates"] if item["candidateId"] != "c0_anchor"]
        errors = validate_p3_grid(value, PROTOCOL_VALUE, self.adapted)
        self.assertIn("exact anchor count is not one: r6_control_hard_00_f0008", errors)

    def test_outcome_adaptive_extension_fails(self) -> None:
        value = manifest()
        value["outcomeAdaptiveCandidateAddition"] = True
        self.assertIn(
            "outcome-adaptive candidate addition is not forbidden",
            validate_p3_grid(value, PROTOCOL_VALUE, self.adapted),
        )

    def test_refinement_rule_is_prefrozen(self) -> None:
        value = manifest()
        value["refinement"]["selectionRule"] = "choose after looking"
        self.assertIn("refinement selection rule mismatch", validate_p3_grid(value, PROTOCOL_VALUE, self.adapted))

    def test_refinement_template_must_be_complete_cartesian_product(self) -> None:
        value = manifest()
        value["refinement"]["offsetTemplate"].pop()
        self.assertTrue(any(
            "refinement offset template must contain 27 unique offsets" in error
            for error in validate_p3_grid(value, PROTOCOL_VALUE, self.adapted)
        ))

    def test_base_grid_is_exactly_212_candidates(self) -> None:
        value = manifest()
        self.assertEqual(len(value["mapCandidates"]), 212)
        value["mapCandidates"].pop()
        self.assertIn(
            "initial manifest must contain exactly the frozen base candidate count",
            validate_p3_grid(value, PROTOCOL_VALUE, self.adapted),
        )

    def test_structural_variation_requires_three_hashes_and_cross_cell_change(self) -> None:
        accepted = {
            "a": [{"image": "a.png", "imageSha256": "a"}],
            "b": [{"image": "b.png", "imageSha256": "b"}],
            "c": [{"image": "c.png", "imageSha256": "c"}],
        }
        with patch("g2_p3_stage_b_final_audit.image_features", return_value=[]), patch(
            "g2_p3_stage_b_final_audit.visual_distances", return_value=(0.02, 0.002)
        ):
            result = structural_variation(Path("."), [["a", "b", "c"]], accepted, PROTOCOL_VALUE)
        self.assertEqual(result["verdict"], "PASS")
        with patch("g2_p3_stage_b_final_audit.image_features", return_value=[]), patch(
            "g2_p3_stage_b_final_audit.visual_distances", return_value=(0.001, 0.0001)
        ):
            result = structural_variation(Path("."), [["a", "b", "c"]], accepted, PROTOCOL_VALUE)
        self.assertEqual(result["verdict"], "FAIL")

    def test_single_isolated_positive_is_insufficient_by_source_threshold(self) -> None:
        self.assertEqual(PROTOCOL_VALUE["positiveBasinGate"]["minimumConnectedAcceptableCells"], 3)
        self.assertEqual(self.adapted["frozenThresholds"]["minimumConnectedAcceptableCells"], 3)

    def test_refinement_zero_offset_is_repeat_not_distinct_cell(self) -> None:
        value = manifest()
        source = "a" * 40
        parent_hash = "b" * 64
        value.update({
            "protocolId": PROTOCOL_VALUE["protocolId"], "mode": PROTOCOL_VALUE["mode"],
            "sourceCommit": source, "auditProtocolCanonicalSha256": canonical_sha256(ROOT / PROTOCOL),
        })
        result_template = {
            "acceptedFreshCaptures": 2, "freshnessPass": True, "repeatStable": True,
            "allAcceptable": True, "allFinalTargetAcceptable": True, "allT7Valid": True,
            "allContactValid": True, "allWorkspaceValid": True, "minimumScore": 0.7,
            "scoreRange": 0.0, "maximumRms128": 0.0, "maximumStructuralDistance": 0.0,
            "poseRestorePass": True,
        }
        results = [dict(result_template, candidateId=item["candidateId"]) for item in value["mapCandidates"]]
        selected = {}
        for control in PROTOCOL_VALUE["fixedGridContract"]["historicalControlIds"]:
            selected[control] = sorted(
                item["candidateId"] for item in value["mapCandidates"]
                if item["anchorControlId"] == control and item["stage"] in {"coarse", "wider"}
            )[0]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            coarse = {
                "protocolId": PROTOCOL_VALUE["protocolId"], "mode": PROTOCOL_VALUE["mode"],
                "sourceCommit": source, "parentManifestSha256": parent_hash,
                "auditProtocolCanonicalSha256": value["auditProtocolCanonicalSha256"],
                "complete": True, "sealedBeforeRefinementCapture": True,
                "candidateResults": results,
            }
            write_json(root / "stage_b_coarse_snapshot.json", coarse)
            refined = []
            pairs = []
            for control_index, control in enumerate(PROTOCOL_VALUE["fixedGridContract"]["historicalControlIds"]):
                for offset_index, offset in enumerate(value["refinement"]["offsetTemplate"]):
                    candidate_id = f"r{control_index}_{offset_index:02d}"
                    zero = all(float(offset[field]) == 0.0 for field in (
                        "longitudinalOffsetMm", "lateralOffsetMm", "yawDeg"
                    ))
                    refined.append({
                        "candidateId": candidate_id, "anchorControlId": control, "stage": "refinement",
                        "centerCandidateId": selected[control], **offset,
                        "role": "selected_center_repeat" if zero else "refinement_cell",
                        "countsAsDistinctCell": not zero,
                    })
                    if not zero:
                        pairs.append([selected[control], candidate_id])
            selection = {
                "protocolId": PROTOCOL_VALUE["protocolId"], "mode": PROTOCOL_VALUE["mode"],
                "sourceCommit": source, "parentManifestSha256": parent_hash,
                "auditProtocolCanonicalSha256": value["auditProtocolCanonicalSha256"],
                "generatedBeforeFirstRefinementCapture": True,
                "coarseSnapshotSha256": sha256(root / "stage_b_coarse_snapshot.json"),
                "selectionRule": PROTOCOL_VALUE["fixedGridContract"]["refinement"]["selectionRule"],
                "postOutcomeExtensionAllowed": False,
                "selections": [
                    {"anchorControlId": control, "selectedCenterCandidateId": candidate_id}
                    for control, candidate_id in selected.items()
                ],
                "mapCandidates": refined, "neighborPairs": pairs,
            }
            write_json(root / "refinement_selection_manifest.json", selection)
            seal = {
                "coarseSnapshotSha256": sha256(root / "stage_b_coarse_snapshot.json"),
                "refinementSelectionManifestSha256": sha256(root / "refinement_selection_manifest.json"),
            }
            outcome = materialize_refinement_grid(
                root, value, seal, results, PROTOCOL_VALUE, self.adapted,
                expected_manifest_sha256=parent_hash, expected_source_commit=source,
            )
            self.assertEqual(outcome["errors"], [])
            self.assertEqual(len(outcome["attemptedCandidateIds"]), 320)
            self.assertEqual(len(outcome["topologyCandidateIds"]), 316)
            self.assertEqual(len(outcome["zeroOffsetRepeatCandidateIds"]), 4)

            selection["mapCandidates"][13]["countsAsDistinctCell"] = True
            write_json(root / "refinement_selection_manifest.json", selection)
            seal["refinementSelectionManifestSha256"] = sha256(root / "refinement_selection_manifest.json")
            outcome = materialize_refinement_grid(
                root, value, seal, results, PROTOCOL_VALUE, self.adapted,
                expected_manifest_sha256=parent_hash, expected_source_commit=source,
            )
            self.assertTrue(any("falsely count as a new cell" in error for error in outcome["errors"]))


if __name__ == "__main__":
    unittest.main()
