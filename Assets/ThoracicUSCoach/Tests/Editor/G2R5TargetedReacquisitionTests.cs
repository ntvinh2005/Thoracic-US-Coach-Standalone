using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class G2R5TargetedReacquisitionTests
    {
        private const string RuntimePath =
            "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R5TargetedReacquisition.cs";
        private const string BatchPath =
            "Assets/ThoracicUSCoach/Editor/ThoracicG2R5TargetedBatch.cs";

        [TearDown]
        public void ResetConfiguration()
        {
            ThoracicUSCoachController.ResetG2R5TargetedExperimentConfiguration();
        }

        [Test]
        public void ActorObservationContainsNoWorldOrAnatomyFields()
        {
            Type actorType = typeof(ThoracicUSCoachController).GetNestedType(
                "G2R5Observation", BindingFlags.NonPublic);
            Assert.That(actorType, Is.Not.Null);
            string[] names = actorType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(item => item.Name).ToArray();
            foreach (string forbidden in new[]
            {
                "probePosition", "probeRotation", "contactPoint", "surfaceNormal",
                "evaluatorLongitudinalCoordinateMm", "provenance", "level",
                "meshIdentity", "worldCoordinate", "targetDistance"
            })
                CollectionAssert.DoesNotContain(names, forbidden);
        }

        [Test]
        public void TargetedControllerUsesActualAchievedMovement()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("execution.achievedDisplacementMm", source);
            StringAssert.Contains("actorRelativeAfterMm", source);
            StringAssert.Contains("G2R5PositionToleranceMm = 0.76f", source);
        }

        [Test]
        public void CaptureUsesFreshProducerAndSafetyChecks()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", source);
            StringAssert.Contains("TryGetPublishedScanProvenance", source);
            StringAssert.Contains("MeasureSweepCountContact", source);
            StringAssert.Contains("IsActive2DWorkspaceValid", source);
            StringAssert.Contains("provenanceMatchedPublishedFrame", source);
        }

        [Test]
        public void ProtocolLocksThreeRegionsAndFrameBudget()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("protocol.regions.Count != 3", source);
            StringAssert.Contains("G2R5MaximumFreshFrames = 240", source);
            StringAssert.Contains("G2-R5 fresh-frame budget exceeds 240", source);
        }

        [Test]
        public void OnlyApprovedYawAndDepthVariantsAreAccepted()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("variant.surfaceYawDeg, 90f", source);
            StringAssert.Contains("variant.surfaceYawDeg, 91f", source);
            StringAssert.Contains("variant.scanDepthMm != 50", source);
            StringAssert.Contains("variant.scanDepthMm != 60", source);
            StringAssert.Contains("manager.Depth_cm = targetCm", source);
        }

        [Test]
        public void SettingsAreRestoredAfterPhysicalEpisodeFinalization()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("context.manager.Depth_cm = context.originalDepthCm", source);
            StringAssert.Contains("active2DStepMm = context.originalStep", source);
            StringAssert.Contains("active2DWorkspaceOrigin = context.originalWorkspaceOrigin", source);
            StringAssert.Contains("FinalizeG2R5Run", source);
            StringAssert.DoesNotContain("probeRoot.SetPositionAndRotation", source);
        }

        [Test]
        public void RuntimeDoesNotUseT7TargetOrEvaluatorScore()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.DoesNotContain("targetPose", source);
            StringAssert.DoesNotContain("distanceToT7", source);
            StringAssert.DoesNotContain("targetScore", source);
            StringAssert.DoesNotContain("ScoreCandidate", source);
            StringAssert.DoesNotContain("PositionGuidedT7", source);
        }

        [Test]
        public void BatchRunsOnlyTheTargetedDiagnosticController()
        {
            string source = File.ReadAllText(BatchPath);
            StringAssert.Contains("StartG2R5TargetedReacquisition", source);
            StringAssert.Contains("ACTOR", File.ReadAllText(RuntimePath));
            StringAssert.DoesNotContain("PositionGuidedT7", source);
            StringAssert.DoesNotContain("ScoreCandidate", source);
        }
    }
}
