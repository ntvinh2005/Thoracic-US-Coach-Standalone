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
            StringAssert.Contains("Vector3.back", method);
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
            StringAssert.Contains("THORACIC_G25_MECHANICAL_HOME_OFFSET_MM", source);
            StringAssert.Contains("outputRoot, 89.5f, 45, mechanicalHomeOffsetMm", source);
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

        [Test]
        public void MechanicalHomeOffsetIsBoundedAndContainsNoAnatomicalLookup()
        {
            string source = File.ReadAllText(RuntimePath);
            int start = source.IndexOf("float mechanicalHomeOffsetMm)");
            int end = source.IndexOf("public static void ResetG2R2ContinuousExperimentConfiguration", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string method = source.Substring(start, end - start);
            StringAssert.Contains("mechanicalHomeOffsetMm < -60f", method);
            StringAssert.DoesNotContain("LF T2", method);
            StringAssert.DoesNotContain("LF T8", method);
            StringAssert.DoesNotContain("BuildSweepCountReferences", method);
        }

        [Test]
        public void ScanPlaneIntegrityUsesSurfaceLocalCorridorTangent()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("Vector3 localCorridor = Vector3.ProjectOnPlane", source);
            StringAssert.Contains("Vector3.Dot(scanWidth, localCorridor)", source);
            StringAssert.DoesNotContain(
                "Vector3.Dot(scanWidth, evaluator.corridorAxis)", source);
        }

        [Test]
        public void RandomStartBatchIsSeededBoundedAndOptIn()
        {
            string source = File.ReadAllText(BatchPath);
            StringAssert.Contains("THORACIC_G25_RANDOM_START_SEED", source);
            StringAssert.Contains("THORACIC_G25_RANDOM_START_MAX_MM", source);
            StringAssert.Contains(
                "ConfigureGroundTruthFreeRandomStartThoracicExploration", source);
            StringAssert.Contains("maximumRandomStartMm = 30f", source);
        }

        [Test]
        public void RandomStartPhysicallyReturnsBeforeExploration()
        {
            string source = File.ReadAllText(RuntimePath);
            int returnCall = source.IndexOf("yield return RunG2R2InitialHomeReturn");
            int explorationCall = source.IndexOf(
                "\"caudal_outbound\", Active2DAction.Caudal", returnCall);
            Assert.That(returnCall, Is.GreaterThanOrEqualTo(0));
            Assert.That(explorationCall, Is.GreaterThan(returnCall));
            int methodStart = source.IndexOf(
                "private IEnumerator RunG2R2InitialHomeReturn");
            int methodEnd = source.IndexOf(
                "private bool TryG2R2PhysicalAction", methodStart);
            string method = source.Substring(methodStart, methodEnd - methodStart);
            StringAssert.Contains("TryG2R2PhysicalAction", method);
            StringAssert.Contains("achievedDisplacementMm", method);
            StringAssert.Contains("initial_home_return", method);
            StringAssert.DoesNotContain("SetPositionAndRotation", method);
        }

        [Test]
        public void EpisodeHomeJitterUsesOnlyMechanicalSurfaceTrace()
        {
            string source = File.ReadAllText(RuntimePath);
            int start = source.IndexOf("if (g2R2EpisodeHomeJitterEnabled)");
            int end = source.IndexOf(
                "int traceSteps = Mathf.CeilToInt", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string block = source.Substring(start, end - start);
            StringAssert.Contains("TraceBoundarySurface", block);
            StringAssert.Contains("System.Random", block);
            StringAssert.DoesNotContain("LF T", block);
            StringAssert.DoesNotContain("BuildSweepCountReferences", block);
        }

        [Test]
        public void RandomStartAndHomeJitterRemainActorRelative()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("initialActorRelativeMm", source);
            StringAssert.Contains("homeArrivalResidualMm", source);
            StringAssert.Contains("episodeHomeJitterMm", source);
            StringAssert.Contains("random_start_baseline", source);
            StringAssert.Contains("home_arrival_baseline", source);
        }

        [Test]
        public void LateralHomeJitterIsSmallSeededAndAnatomyBlind()
        {
            string runtime = File.ReadAllText(RuntimePath);
            string batch = File.ReadAllText(BatchPath);
            int start = runtime.IndexOf(
                "if (g2R2EpisodeHomeLateralJitterEnabled)");
            int end = runtime.IndexOf(
                "RaycastHit fixedHomeHit = FindG2R2OffsetSurface", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string block = runtime.Substring(start, end - start);
            StringAssert.Contains("System.Random", block);
            StringAssert.Contains("0.5f", block);
            StringAssert.DoesNotContain("LF T", block);
            StringAssert.DoesNotContain("BuildSweepCountReferences", block);
            StringAssert.Contains("THORACIC_G25_HOME_LATERAL_JITTER_SEED", batch);
            StringAssert.Contains("THORACIC_G25_HOME_LATERAL_JITTER_MAX_MM", batch);
        }

        [Test]
        public void BatchCanLeaveEditorOpenAfterFinalizingEpisode()
        {
            string source = File.ReadAllText(BatchPath);
            int start = source.IndexOf("THORACIC_G25_KEEP_UNITY_OPEN");
            int exit = source.IndexOf("EditorApplication.Exit(code)", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(exit, Is.GreaterThan(start));
            StringAssert.Contains("EditorApplication.isPlaying = false", source.Substring(start, exit - start));
        }
    }
}
