using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class G2R2ContinuousSweepTests
    {
        private const string RuntimePath =
            "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R2ContinuousSweep.cs";

        [Test]
        public void ActorObservationContainsNoEvaluatorOrAnatomicalFields()
        {
            Type actorType = typeof(ThoracicUSCoachController).GetNestedType(
                "G2R2ActorObservation", BindingFlags.NonPublic);
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
        public void PhysicalRouteStartsAtUnlabeledHomeAndSamplesBothDirections()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("fixed unlabeled Home", source);
            StringAssert.Contains("caudal_outbound", source);
            StringAssert.Contains("caudal_return", source);
            StringAssert.Contains("cranial_outbound", source);
            StringAssert.Contains("cranial_return", source);
            StringAssert.Contains("execution.achievedDisplacementMm", source);
        }

        [Test]
        public void RandomStartUsesTwoDimensionalPhysicalReturnBeforeExploration()
        {
            string source = File.ReadAllText(RuntimePath);
            int returnStart = source.IndexOf(
                "private IEnumerator RunG2R2InitialHomeReturn");
            int returnEnd = source.IndexOf(
                "private bool TryG2R2PhysicalAction", returnStart);
            Assert.That(returnStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(returnEnd, Is.GreaterThan(returnStart));
            string returnMethod = source.Substring(returnStart, returnEnd - returnStart);

            StringAssert.Contains("initialActorLateralMm", source);
            StringAssert.Contains("signedActorLateralMm", source);
            StringAssert.Contains("Active2DAction.Medial", returnMethod);
            StringAssert.Contains("Active2DAction.Lateral", returnMethod);
            StringAssert.Contains("getLateralRelative", returnMethod);
            StringAssert.Contains("setLateralRelative", returnMethod);
            StringAssert.DoesNotContain("SetPositionAndRotation", returnMethod);
        }

        [Test]
        public void ApprovedGeometryAndFineSamplingAreLocked()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("G2R2StepMm = 1.5f", source);
            StringAssert.Contains("G2R2LateralOffsetMm = -6f", source);
            StringAssert.Contains("G2R2SurfaceYawDeg = 90f", source);
            StringAssert.Contains("scanWidthAgainstCorridor < 0.95f", source);
        }

        [Test]
        public void CaptureEnforcesFreshnessContactWorkspaceAndProvenance()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("G2R2MaximumStartupFrames = 120", source);
            StringAssert.Contains("manager.isActiveAndEnabled", source);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", source);
            StringAssert.Contains("TryGetPublishedScanProvenance", source);
            StringAssert.Contains("M0BObservationFresh(observation)", source);
            StringAssert.Contains("contactValidAfterExecution", source);
            StringAssert.Contains("workspaceValidAfterExecution", source);
        }

        [Test]
        public void RuntimeDoesNotUseCalibratedTargetOrEvaluatorScore()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.DoesNotContain("targetPose", source);
            StringAssert.DoesNotContain("distanceToT7", source);
            StringAssert.DoesNotContain("targetScore", source);
            StringAssert.DoesNotContain("ScoreCandidate", source);
            StringAssert.DoesNotContain("PositionGuidedT7", source);
        }

        [Test]
        public void OriginalMovementSettingsAreRestoredInFinally()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("active2DStepMm = originalStep", source);
            StringAssert.Contains("active2DLongitudinalHalfExtentMm = originalLongitudinalExtent", source);
            StringAssert.Contains("active2DLateralHalfExtentMm = originalLateralExtent", source);
            StringAssert.Contains("active2DWorkspaceOrigin = originalWorkspaceOrigin", source);
            StringAssert.Contains("probeRoot.SetPositionAndRotation(originalPosition, originalRotation)", source);
        }
    }
}


