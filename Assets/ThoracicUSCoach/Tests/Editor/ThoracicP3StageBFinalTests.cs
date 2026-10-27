using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class ThoracicP3StageBFinalTests
    {
        private static string Root => Directory.GetParent(Application.dataPath).FullName;
        private static string ManifestPath => Path.Combine(Root,
            "Artifacts/ThoracicUSCoach/active2d/g2_p3_stage_b_final_manifest_v1.json");

        [Test]
        public void PrefrozenManifestContainsOnlyBaseGridAndDeclaredTemplate()
        {
            Assert.IsTrue(File.Exists(ManifestPath));
            P3StageBFinalManifest manifest = JsonUtility.FromJson<P3StageBFinalManifest>(File.ReadAllText(ManifestPath));
            ThoracicP3StageBFinalProtocol.Validate(manifest);
            Assert.AreEqual(212, manifest.mapCandidates.Count);
            Assert.AreEqual(4, manifest.mapCandidates.Count(item => item.stage == "anchor"));
            Assert.AreEqual(104, manifest.mapCandidates.Count(item => item.stage == "coarse"));
            Assert.AreEqual(104, manifest.mapCandidates.Count(item => item.stage == "wider"));
            Assert.IsFalse(manifest.mapCandidates.Any(item => item.stage == "refinement"));
            Assert.AreEqual(27, manifest.refinement.offsetTemplate.Count);
            Assert.IsTrue(manifest.refinement.declaredBeforeCoarseCapture);
            Assert.IsFalse(manifest.refinement.postOutcomeExtensionAllowed);
            Assert.IsFalse(manifest.outcomeAdaptiveCandidateAddition);
        }

        [Test]
        public void AdaptiveOrPrefrozenRefinementCandidatesAreRejected()
        {
            P3StageBFinalManifest manifest = JsonUtility.FromJson<P3StageBFinalManifest>(File.ReadAllText(ManifestPath));
            manifest.outcomeAdaptiveCandidateAddition = true;
            Assert.Throws<InvalidOperationException>(() => ThoracicP3StageBFinalProtocol.Validate(manifest));
            manifest.outcomeAdaptiveCandidateAddition = false;
            manifest.mapCandidates.Add(new P3StageBCandidate { candidateId = "illegal", stage = "refinement" });
            Assert.Throws<InvalidOperationException>(() => ThoracicP3StageBFinalProtocol.Validate(manifest));
        }

        [Test]
        public void ExactHistoricalAnchorsAreConsumedControls()
        {
            P3StageBFinalManifest manifest = JsonUtility.FromJson<P3StageBFinalManifest>(File.ReadAllText(ManifestPath));
            foreach (P3StageBCandidate anchor in manifest.mapCandidates.Where(item => item.stage == "anchor"))
            {
                Assert.IsTrue(anchor.consumedHistoricalControl);
                Assert.AreEqual(0f, anchor.longitudinalOffsetMm, 1e-6f);
                Assert.AreEqual(0f, anchor.lateralOffsetMm, 1e-6f);
                Assert.AreEqual(0f, anchor.yawDeg, 1e-6f);
            }
        }

        [Test]
        public void RuntimeUsesFreshProducerAndStagedMaterialization()
        {
            string source = File.ReadAllText(Path.Combine(Root,
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P3StageBFinal.cs"));
            StringAssert.Contains("CaptureDirectProducerImmediate", source);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", source);
            StringAssert.Contains("select-refinement", source);
            StringAssert.Contains("stage_b_coarse_snapshot.json", File.ReadAllText(Path.Combine(Root,
                "Tools/ThoracicUSCoach/experiments/g2_p3_stage_b_final_protocol_v1.json")));
            Assert.Less(source.IndexOf("RunP3StageBMaterializer(\"select-refinement\")", StringComparison.Ordinal),
                source.IndexOf("foreach (P3StageBCandidate candidate in selection.mapCandidates)", StringComparison.Ordinal));
            StringAssert.Contains("record.producerUnityFrame <= record.poseAppliedUnityFrame", source);
            StringAssert.Contains("record.unityFrameCount < record.producerUnityFrame", source);
            StringAssert.Contains("RequireP3StageBFrozenEvaluatorFloor", source);
            StringAssert.Contains("live evaluator floor differs", source);
        }

        [Test]
        public void IndependentEvaluatorLabelIsNotOverwrittenByFloor()
        {
            string source = File.ReadAllText(Path.Combine(Root,
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P3StageBFinal.cs"));
            StringAssert.DoesNotContain("label.finalTargetAcceptable =", source);
            StringAssert.Contains("BuildActive2DPrivilegedFrame(p3StageBRecordIndex, accepted ? capture.image : null)", source);
            StringAssert.Contains("evaluatorFloorAgreement", source);
        }

        [Test]
        public void BatchCallsOnlyStageBEntry()
        {
            string batch = File.ReadAllText(Path.Combine(Root,
                "Assets/ThoracicUSCoach/Editor/ThoracicP3StageBFinalBatch.cs"));
            StringAssert.Contains("StartP3StageBFinal", batch);
            StringAssert.DoesNotContain("StartP3StageFFinal", batch);
        }

        [Test]
        public void LauncherPinsEveryScopedExecutionFileToManifestSourceCommit()
        {
            string launcher = File.ReadAllText(Path.Combine(Root,
                "Tools/ThoracicUSCoach/run_p3_stage_b_final.ps1"));
            StringAssert.Contains("cat-file -e", launcher);
            StringAssert.Contains("diff --quiet", launcher);
            StringAssert.Contains("status --porcelain=v1", launcher);
            StringAssert.Contains("ThoracicUSCoachController.DirectFreshCapture.cs", launcher);
            StringAssert.Contains("materialize_p3_stage_b_final.py", launcher);
            StringAssert.Contains("g2_p3_stage_b_final_audit.py", launcher);
        }
    }
}
