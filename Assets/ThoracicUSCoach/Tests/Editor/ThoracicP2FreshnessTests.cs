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
            string controller = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P2Freshness.cs");
            string helper = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.DirectFreshCapture.cs");
            StringAssert.Contains("yield return null", helper);
            StringAssert.Contains("item.PublishCurrentPose(token.poseEpoch)", helper);
            StringAssert.Contains("producer.PublishedUnityFrame <= token.poseAppliedUnityFrame", helper);
            StringAssert.Contains("producer.PublishedScanSerial <= token.minimumProducerScanSerial", helper);
            StringAssert.Contains("captureDisposition = \"discarded\"", controller);
            StringAssert.Contains("captureDisposition = \"accepted\"", controller);
            StringAssert.Contains("producerSamplesPublished = 1", helper);
            StringAssert.Contains("RequireNoActiveUltrasoundAggregator()", helper);
            StringAssert.Contains("capturePath = \"direct_producer_texture\"", helper);
            StringAssert.Contains("capturePathMode = \"DIRECT_PRODUCER_CAMERA\"", helper);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", controller);
            StringAssert.DoesNotContain("aggregator.PublishCurrentPose", helper);
        }

        [Test]
        public void ReusableCaptureIsBoundedAndIntegratedAcrossPhysicalPaths()
        {
            string helper = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.DirectFreshCapture.cs");
            string r6 = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.R6FinalSprint.cs");
            string live = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs");
            StringAssert.Contains("DirectFreshMaximumFrameWait = 8", helper);
            StringAssert.Contains("DirectFreshMaximumPublicationAttempts = 3", helper);
            StringAssert.Contains("TimeoutException", helper);
            StringAssert.Contains("DirectFreshPurpose", helper);
            foreach (string purpose in new[]
                     { "direct_pose_restoration", "marker_skin_placement", "completed_movement",
                       "rejected_rollback", "observe", "confirmation", "live_diagnostic" })
                StringAssert.Contains("\"" + purpose + "\"", helper);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", r6);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", live);
            StringAssert.Contains("SaveActive2DBridgeImage(confirmation.image", live);
        }

        [Test]
        public void PhysicalEvidenceDeclaresRealNonSyntheticDomain()
        {
            string p2 = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P2Freshness.cs");
            string r6 = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.R6FinalSprint.cs");
            string live = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs");
            foreach (string source in new[] { p2, r6, live })
            {
                StringAssert.Contains("evidenceDomain = \"REAL_UNITY_PHYSICAL\"", source);
                StringAssert.Contains("synthetic = false", source);
                StringAssert.Contains("faultInjected = false", source);
            }
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
