using System.Linq;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class Phase2BestViewPipelineTests
    {
        [Test]
        public void CoarseSearchStaysInsideLockedT7Tube()
        {
            var targets = ThoracicUSCoachController.BuildPhase2CoarseTargets();
            Assert.That(targets.Count, Is.EqualTo(70));
            Assert.That(targets.All(item => item.x >= -12f && item.x <= 6f), Is.True);
            Assert.That(targets.All(item => item.y >= 0f && item.y <= 27f), Is.True);
        }

        [Test]
        public void FineSearchNeverExpandsTheLockedTube()
        {
            var targets = ThoracicUSCoachController.BuildPhase2FineTargets(
                new Vector3(6f, 27f, 0f));
            Assert.That(targets.All(item => item.x >= -12f && item.x <= 6f), Is.True);
            Assert.That(targets.All(item => item.y >= 0f && item.y <= 27f), Is.True);
            Assert.That(targets.All(item => Mathf.Abs(item.z) <= 15f), Is.True);
            Assert.That(targets.Any(item => Mathf.Approximately(item.z, 15f)), Is.True);
            Assert.That(targets.Any(item => Mathf.Approximately(item.x, 4.5f)), Is.True);
            Assert.That(targets.Any(item => Mathf.Approximately(item.y, 25.5f)), Is.True);
            Assert.That(targets.Count, Is.EqualTo(99));
        }

        [Test]
        public void MicroSearchAddsResolutionWithoutExpandingTheLockedTube()
        {
            var targets = ThoracicUSCoachController.BuildPhase2MicroTargets(
                new Vector3(1.5f, 25.5f, 6f));
            Assert.That(targets.All(item => item.x >= -12f && item.x <= 6f), Is.True);
            Assert.That(targets.All(item => item.y >= 0f && item.y <= 27f), Is.True);
            Assert.That(targets.All(item => Mathf.Abs(item.z) <= 15f), Is.True);
            Assert.That(targets.Any(item => Mathf.Approximately(item.x, 1.875f)), Is.True);
            Assert.That(targets.Any(item => Mathf.Approximately(item.z, 9f)), Is.True);
            Assert.That(targets.Any(item => Mathf.Approximately(item.y, 27f)), Is.True);
            Assert.That(targets.Count, Is.EqualTo(75));
        }

        [Test]
        public void BoundaryRefinementCoversTheSmallImageOnlyHandoffTube()
        {
            var targets = ThoracicUSCoachController.BuildPhase2BoundaryRefinementTargets();
            Assert.That(targets.Count, Is.EqualTo(90));
            Assert.That(targets.All(item => item.x >= -3f && item.x <= 3f), Is.True);
            Assert.That(targets.All(item => item.y >= 24f && item.y <= 27f), Is.True);
            Assert.That(targets.All(item => item.z >= 0f && item.z <= 15f), Is.True);
            Assert.That(targets.Any(item => item == new Vector3(-1.5f, 25.5f, 6f)), Is.True);
        }

        [Test]
        public void FullPipelineEditorEntryStartsTheNonPrivilegedDemo()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Editor/ThoracicFullPipelineDemo.cs");
            StringAssert.Contains("StartRandomStartLandmarkDemo(true)", source);
            StringAssert.DoesNotContain("t7RightSeed", source);
            StringAssert.DoesNotContain("LF T7", source);
        }

        [Test]
        public void Phase2BridgeRetriesAtomicRequestReplacement()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Phase2BestView.cs");
            StringAssert.Contains("for (int attempt = 0; attempt < 20; attempt++)", source);
            StringAssert.Contains("System.Threading.Thread.Sleep(10)", source);
        }

        [Test]
        public void Phase2RightSearchUsesMechanicalRightProjectedOntoSkin()
        {
            Vector3 normal = new Vector3(-0.008f, 0.9998f, 0.018f).normalized;
            Vector3 right = ThoracicUSCoachController.BuildPhase2MechanicalRightAxis(normal);
            Assert.That(Vector3.Dot(right, Vector3.right), Is.GreaterThan(0.99f));
            Assert.That(Mathf.Abs(Vector3.Dot(right, normal)), Is.LessThan(1e-4f));
        }

        [Test]
        public void Phase2TranslationsPreserveTheFrozenAcquisitionPlane()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Phase2BestView.cs");
            StringAssert.Contains("TryPhase2Move(direction)", source);
            StringAssert.Contains(
                "hit, phase2CurrentYawDeg, phase2CurrentTiltDeg", source);
            StringAssert.Contains("Phase2CoordinateToleranceMm = 0.15f", source);
        }

        [Test]
        public void OneFreshRecognizedFrameConfirmsTheReturnedBestPose()
        {
            Assert.IsFalse(ThoracicUSCoachController.HasPhase2FreshConfirmation(0));
            Assert.IsTrue(ThoracicUSCoachController.HasPhase2FreshConfirmation(1));
            Assert.IsTrue(ThoracicUSCoachController.HasPhase2FreshConfirmation(2));
        }

        [Test]
        public void Phase2FineSearchMayRevisitTheBestUnacceptedCoarseProposal()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Phase2BestView.cs");
            StringAssert.Contains("BestPhase2SearchProposalCoordinate()", source);
            StringAssert.Contains("Phase2FineSearchSeeds()", source);
            StringAssert.Contains("OrderByDescending(item => item.qualityScore)", source);
            StringAssert.Contains(
                "Coarse and fine search found no image-only valid T7-right candidate", source);
            StringAssert.DoesNotContain(
                "Coarse search found no image-only valid T7-right candidate", source);
        }
    }
}
