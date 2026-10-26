using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class ThoracicP2FreshnessTests
    {
        [Test]
        public void ManifestRequiresFrozenControlTransitionSequence()
        {
            P2FreshnessManifest manifest = ValidManifest();
            Assert.DoesNotThrow(() => ThoracicP2FreshnessProtocol.Validate(manifest));
            manifest.freshnessControlSequence[2] = manifest.freshnessControlSequence[1];
            Assert.Throws<InvalidOperationException>(() => ThoracicP2FreshnessProtocol.Validate(manifest));
        }

        [Test]
        public void ManifestRequiresTwoAcceptedFreshCaptures()
        {
            P2FreshnessManifest manifest = ValidManifest();
            manifest.acceptedFreshCapturesPerTransition = 1;
            Assert.Throws<InvalidOperationException>(() => ThoracicP2FreshnessProtocol.Validate(manifest));
        }

        [Test]
        public void CaptureUsesProducerEpochAndFrameHandshake()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P2Freshness.cs");
            StringAssert.Contains("yield return null", source);
            StringAssert.Contains("item.PublishCurrentPose(poseEpoch)", source);
            StringAssert.Contains("producer.PublishedUnityFrame <= attempt.poseAppliedFrame", source);
            StringAssert.Contains("captureDisposition = \"discarded\"", source);
            StringAssert.Contains("captureDisposition = \"accepted\"", source);
            StringAssert.Contains("producerSamplesPublished = 1", source);
            StringAssert.Contains("aggregators.Length != 0", source);
            StringAssert.Contains("capturePath = \"direct_producer_texture\"", source);
            StringAssert.Contains("capturePathMode = \"DIRECT_PRODUCER_CAMERA\"", source);
            StringAssert.Contains("activeAggregatorCount = aggregators.Length", source);
            StringAssert.Contains("producerSamplesPublished = publication.producerSamplesPublished", source);
            StringAssert.Contains("Time.frameCount <= lastAcceptedCaptureFrame", source);
            StringAssert.DoesNotContain("aggregator.PublishCurrentPose", source);
        }

        [Test]
        public void MovementEvidenceUsesActualExecutionFacts()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P2Freshness.cs");
            StringAssert.Contains("observedRejected |= !execution.accepted", source);
            StringAssert.Contains("observedIncomplete |= execution.accepted && !execution.completed", source);
            StringAssert.Contains("observedRollback |= execution.rollbackAttempted", source);
            StringAssert.DoesNotContain("rejectionEvidenceCases", source);
        }

        [Test]
        public void RunnerEnforcesExternalPrefrozenManifestHash()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P2Freshness.cs");
            StringAssert.Contains("THORACIC_P2_MANIFEST_SHA256", source);
            StringAssert.Contains("P2 pre-frozen manifest hash mismatch", source);
        }

        [Test]
        public void NestedCoroutineFailuresAreFlattenedAndBatchIsBounded()
        {
            string controller = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P2Freshness.cs");
            string batch = File.ReadAllText(
                "Assets/ThoracicUSCoach/Editor/ThoracicP2FreshnessBatch.cs");
            StringAssert.Contains("Stack<IEnumerator>", controller);
            StringAssert.Contains("current is IEnumerator nested", controller);
            StringAssert.Contains("MaximumBatchSeconds = 1800f", batch);
            StringAssert.Contains("EditorApplication.Exit(code)", batch);
        }

        private static P2FreshnessManifest ValidManifest()
        {
            P2FreshnessManifest manifest = new P2FreshnessManifest
            {
                protocolId = ThoracicP2FreshnessProtocol.ProtocolId,
                mode = ThoracicP2FreshnessProtocol.Mode,
                scene = "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity",
                unityVersion = "6000.3.9f1",
                sourceCommit = new string('a', 40),
                baseCommit = ThoracicP2FreshnessProtocol.BaseCommit,
                captureBranch = ThoracicP2FreshnessProtocol.CaptureBranch,
                datasetRunId = "test",
                outputDirectory = "Artifacts/test",
                acceptableScoreFloor = ThoracicP2FreshnessProtocol.FrozenAcceptableFloor,
                captureProtocolPath = "capture.json",
                captureProtocolSha256 = new string('b', 64),
                auditProtocolPath = "audit.json",
                auditProtocolSha256 = new string('c', 64),
                acceptedFreshCapturesPerTransition = 2,
                maximumRejectionProbeActions = 16,
                movementFreshnessActions = new List<string>
                    { "Lateral", "Cranial", "RotateClockwise", "Observe" },
                freshnessControlSequence = new List<string>
                {
                    "A", "B", "A", "C", "D"
                }
            };
            foreach (string id in new[] { "A", "B", "C", "D" })
                manifest.positiveControls.Add(new R6PositiveControl { controlId = id });
            return manifest;
        }
    }
}
