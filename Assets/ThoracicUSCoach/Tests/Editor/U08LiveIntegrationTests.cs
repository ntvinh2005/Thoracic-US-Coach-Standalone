using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class U08LiveIntegrationTests
    {
        [Test]
        public void LegacyActorRequest_RemainsBackwardCompatible()
        {
            const string json = "{\"schemaVersion\":1,\"contract\":\"active2d_image_only_v1\","
                + "\"requestId\":\"r0\",\"episodeId\":\"e0\",\"reset\":true,"
                + "\"imagePath\":\"clean.png\"}";

            Active2DActorRequest request = JsonUtility.FromJson<Active2DActorRequest>(json);

            Assert.IsNotNull(request);
            Assert.AreEqual(1, request.schemaVersion);
            Assert.AreEqual("active2d_image_only_v1", request.contract);
            StringAssert.DoesNotContain("previousAction", JsonUtility.ToJson(request));
        }

        [Test]
        public void ExecutionFeedback_RoundTripsWithoutPoseOrEvaluatorData()
        {
            Active2DActorRequestV2 request = new Active2DActorRequestV2
            {
                requestId = "r1",
                episodeId = "e1",
                reset = false,
                imagePath = "clean.png",
                hasPreviousActionFeedback = true,
                previousAction = "LATERAL",
                previousActionAccepted = true,
                previousActionCompleted = false
            };

            string json = JsonUtility.ToJson(request);
            Active2DActorRequestV2 restored = JsonUtility.FromJson<Active2DActorRequestV2>(json);

            Assert.AreEqual(2, restored.schemaVersion);
            Assert.AreEqual("active2d_image_only_v2", restored.contract);
            Assert.IsTrue(restored.hasPreviousActionFeedback);
            Assert.AreEqual("LATERAL", restored.previousAction);
            Assert.IsTrue(restored.previousActionAccepted);
            Assert.IsFalse(restored.previousActionCompleted);
            StringAssert.DoesNotContain("probePosition", json);
            StringAssert.DoesNotContain("target", json.ToLowerInvariant());
            StringAssert.DoesNotContain("nearestLevel", json);
        }

        [Test]
        public void CandidateAndFallbackTelemetry_PersistsInRawTrace()
        {
            Active2DRunTrace trace = new Active2DRunTrace
            {
                candidateEnabled = true,
                candidateSelected = false,
                candidateMedialValue = 0.125f,
                candidateLateralValue = 0.100f,
                candidateFallback = true,
                candidateFallbackReason = "unsupported_action:CRANIAL",
                executionFeedbackApplied = true,
                executionFeedbackAction = "MEDIAL",
                executionFeedbackAccepted = false,
                executionFeedbackCompleted = false,
                phaseTwoActionModel = "spatial_v2_random_relu32_ridge"
            };

            Active2DRunTrace restored = JsonUtility.FromJson<Active2DRunTrace>(
                JsonUtility.ToJson(trace));

            Assert.IsTrue(restored.candidateEnabled);
            Assert.IsFalse(restored.candidateSelected);
            Assert.IsTrue(restored.candidateFallback);
            Assert.AreEqual("unsupported_action:CRANIAL", restored.candidateFallbackReason);
            Assert.IsTrue(restored.executionFeedbackApplied);
            Assert.IsFalse(restored.executionFeedbackAccepted);
            Assert.IsFalse(restored.executionFeedbackCompleted);
            Assert.AreEqual("spatial_v2_random_relu32_ridge", restored.phaseTwoActionModel);
        }
    }
}
