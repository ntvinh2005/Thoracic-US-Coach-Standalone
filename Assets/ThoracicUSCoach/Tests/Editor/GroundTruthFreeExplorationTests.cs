using System.IO;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class GroundTruthFreeExplorationTests
    {
        private const string RuntimePath =
            "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R2ContinuousSweep.cs";
        private const string BatchPath =
            "Assets/ThoracicUSCoach/Editor/ThoracicGroundTruthFreeExplorationBatch.cs";

        [Test]
        public void GroundTruthFreeSetupUsesMechanicalHomeAxisAndSymmetricLimit()
        {
            string source = File.ReadAllText(RuntimePath);
            int start = source.IndexOf(
                "private G2R2AcquisitionSetup BuildG2R2GroundTruthFreeSetup");
            int end = source.IndexOf(
                "private G2R2AcquisitionSetup BuildG2R2EvaluatorPlannedSetup", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string method = source.Substring(start, end - start);
            StringAssert.Contains("faceCenter.position", method);
            StringAssert.Contains("Vector3.down", method);
            StringAssert.Contains("G2R2GroundTruthFreeMechanicalExtentMm", method);
            StringAssert.DoesNotContain("BuildSweepCountReferences", method);
            StringAssert.DoesNotContain("LF T2", method);
            StringAssert.DoesNotContain("LF T8", method);
            StringAssert.DoesNotContain("FindG2R2LfRenderer", method);
        }

        [Test]
        public void OptInBatchLocksFrozenAcquisitionAndPhysicalFinalization()
        {
            string source = File.ReadAllText(BatchPath);
            StringAssert.Contains("ConfigureGroundTruthFreeThoracicExploration", source);
            StringAssert.Contains("outputRoot, 89.5f, 45, shiftOneFootprintMedial", source);
            StringAssert.DoesNotContain("BuildSweepCountReferences", source);
            StringAssert.DoesNotContain("LF T2", source);
            StringAssert.DoesNotContain("LF T8", source);
        }

        [Test]
        public void GroundTruthFreeModeIsOptInAndResettable()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("g2R2GroundTruthFreeExploration = true", source);
            StringAssert.Contains("g2R2GroundTruthFreeExploration = false", source);
            StringAssert.Contains("g2R2GroundTruthFreeExploration", source);
            StringAssert.Contains("? BuildG2R2GroundTruthFreeSetup()", source);
        }

        [Test]
        public void ActorDisclosureExcludesAnatomyPlannedAcquisition()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("no anatomy references plan acquisition", source);
            StringAssert.Contains("no LF or T-level reference is queried", source);
        }

        [Test]
        public void GuardedFailureCannotAdvertiseCompletionBeforePhysicalRecovery()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("guardedFinalizationPending", source);
            StringAssert.Contains("if (!guardedFinalizationPending)", source);
            StringAssert.Contains("RecoverG2R2PhysicallyToHome", source);
            StringAssert.Contains("g2R2ContinuousStatus = \"RECOVERY_FAILED\"", source);
        }

        [Test]
        public void GuardedFailurePersistsResolvedStatusAfterPhysicalRecovery()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("FinalizeG2R2FailureArtifacts(", source);
            StringAssert.Contains("g2R2ActiveActor.status = status", source);
            StringAssert.Contains("g2R2ActiveSummary.returnedWithinTolerance", source);
            StringAssert.Contains("WriteG2R2Artifacts(g2R2ActiveActor", source);
        }

        [Test]
        public void SecondDevelopmentHomeShiftUsesOneImageFootprintNotAnatomy()
        {
            string runtime = File.ReadAllText(RuntimePath);
            string batch = File.ReadAllText(BatchPath);
            StringAssert.Contains("G2R2LateralOffsetMm - 50f", runtime);
            StringAssert.Contains("THORACIC_G25_SHIFT_ONE_FOOTPRINT_MEDIAL", batch);
            StringAssert.DoesNotContain("reference", batch.ToLowerInvariant());
        }

        [Test]
        public void SafeBoundaryRejectionReturnsByActuallyAchievedRelativeMotion()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("Mathf.Abs(relative) / G2R2StepMm", source);
            StringAssert.Contains("rollbackContactRestored", source);
            StringAssert.Contains("Ground-truth-free boundary rejection", source);
        }
    }
}
