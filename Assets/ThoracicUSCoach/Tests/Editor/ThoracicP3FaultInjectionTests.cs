using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class ThoracicP3FaultInjectionTests
    {
        public const string ExactTestName =
            "ThoracicP3FaultInjectionTests.AcceptedIncompleteBridgeSerializesZeroDisplacement";

        [Test]
        public void AcceptedIncompleteBridgeSerializesZeroDisplacement()
        {
            P3SyntheticFaultPlan plan = DeterministicPlan();
            Active2DActorRequestV2 bridge = new Active2DActorRequestV2
            {
                requestId = "p3_fault_bridge_000",
                episodeId = "p3_fault_episode",
                imagePath = "clean_us_not_used_by_synthetic_fault.png",
                hasPreviousActionFeedback = true,
                previousAction = plan.issuedAction,
                previousActionAccepted = true,
                previousActionCompleted = false
            };
            string bridgeJson = JsonUtility.ToJson(bridge);
            Active2DActorRequestV2 bridgeRoundTrip = JsonUtility.FromJson<Active2DActorRequestV2>(bridgeJson);
            Assert.IsTrue(bridgeRoundTrip.hasPreviousActionFeedback);
            Assert.AreEqual(plan.issuedAction, bridgeRoundTrip.previousAction);
            Assert.IsTrue(bridgeRoundTrip.previousActionAccepted);
            Assert.IsFalse(bridgeRoundTrip.previousActionCompleted);
            P3SyntheticTrial trial = ThoracicUSCoachController.ApplyP3AcceptedIncompleteFeedback(plan);

            Assert.IsTrue(trial.actionAccepted);
            Assert.IsFalse(trial.actionCompleted);
            Assert.AreEqual("DETERMINISTIC_FAULT_INJECTION", trial.evidenceDomain);
            Assert.IsTrue(trial.synthetic);
            Assert.IsTrue(trial.faultInjected);
            Assert.IsFalse(trial.decisionSupportEligible);
            Assert.IsFalse(trial.trainingEligible);
            Assert.AreEqual(plan.actualDelta.lateralMm, trial.odometryDeltaApplied.lateralMm, 1e-6f);
            Assert.AreNotEqual(plan.issuedDelta.lateralMm, trial.odometryDeltaApplied.lateralMm);
            CollectionAssert.AreEqual(plan.preVisitedCellIds, trial.postVisitedCellIds);
            CollectionAssert.DoesNotContain(trial.postVisitedCellIds, plan.plannedFullMoveTargetCellId);
            Assert.AreEqual(plan.preBestMemory.recordId, trial.postBestMemory.recordId);
            Assert.AreEqual(plan.preBestMemory.canonicalSha256, trial.postBestMemory.canonicalSha256);
            Assert.AreEqual("last_confirmed_actual_pose", trial.safeReturn.returnAnchorSource);
            Assert.IsFalse(trial.safeReturn.assumesFullIssuedMove);
            Assert.AreEqual("ABSTAIN", trial.controllerDecision);
            Assert.IsFalse(trial.stopIssued);
            Assert.AreEqual(plan.actualDelta.lateralMm,
                trial.causalActionHistory[0].executedDelta.lateralMm, 1e-6f);
            Assert.IsTrue(trial.causalActionHistory[0].feedbackObservedBeforeAppend);

            string artifactPath = Environment.GetEnvironmentVariable("THORACIC_P3_FAULT_TEST_ARTIFACT");
            if (string.IsNullOrWhiteSpace(artifactPath)) return;
            string sourceCommit = Environment.GetEnvironmentVariable("THORACIC_P3_EXECUTION_COMMIT");
            if (string.IsNullOrWhiteSpace(sourceCommit))
                Assert.Fail("THORACIC_P3_EXECUTION_COMMIT is required when exporting fault evidence");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath));
            P3LiveTestArtifact artifact = new P3LiveTestArtifact
            {
                sourceCommit = sourceCommit,
                exportedBy = "CSharp",
                assembly = "ThoracicUSCoach.Tests.Editor",
                testName = ExactTestName,
                result = "PASS",
                bridgeFeedback = new P3BridgeFeedback
                {
                    accepted = bridgeRoundTrip.previousActionAccepted,
                    completed = bridgeRoundTrip.previousActionCompleted,
                    factualResultingDeltaProvided = false,
                    actualDelta = new P3ActionDelta()
                }
            };
            File.WriteAllText(artifactPath, JsonUtility.ToJson(artifact, true));
        }

        internal static P3SyntheticFaultPlan DeterministicPlan()
        {
            return new P3SyntheticFaultPlan
            {
                faultId = P3ActionFeedbackTransition.FaultId,
                trialId = "p3_fault_accepted_incomplete_000",
                issuedAction = "LATERAL",
                actualExecutedAction = "LATERAL",
                issuedDelta = new P3ActionDelta { lateralMm = 3f },
                actualDelta = new P3ActionDelta(),
                preVisitedCellIds = new List<string> { "cell_origin", "cell_confirmed_00" },
                plannedFullMoveTargetCellId = "cell_lateral_03",
                preBestMemory = new P3BestMemory
                {
                    recordId = "confirmed_best_000",
                    canonicalSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
            };
        }
    }
}
