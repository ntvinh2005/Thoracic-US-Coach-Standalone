using System.IO;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class G2R6CrossPlaneTests
    {
        private static string ReadProjectFile(string relativePath)
        {
            return File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), relativePath));
        }

        [Test]
        public void RuntimeAcceptsOnlyFrozenCrossPlaneYawForR6()
        {
            string source = ReadProjectFile(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R5TargetedReacquisition.cs");
            StringAssert.Contains("ACTOR_GUIDED_CROSS_PLANE_DISCRIMINATION", source);
            StringAssert.Contains("Mathf.Approximately(variant.surfaceYawDeg, 60f)", source);
            StringAssert.Contains("G2-R6 requires frozen yaw 90/60 views at matched 60 mm depth", source);
        }

        [Test]
        public void RuntimeEnforcesR6FrameBudgetAndPixelChangeCheck()
        {
            string source = ReadProjectFile(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R5TargetedReacquisition.cs");
            StringAssert.Contains("G2-R6 fresh-frame budget exceeds 160", source);
            StringAssert.Contains("initialCrossPlanePixelMeanAbsoluteDifference", source);
            StringAssert.Contains("g2R5RunMaximumFreshFrames", source);
        }

        [Test]
        public void ActorTraceRecordsAcquisitionProjectionWithoutWorldPose()
        {
            string source = ReadProjectFile(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R5TargetedReacquisition.cs");
            StringAssert.Contains("scanWidthAgainstTravelAxis", source);
            StringAssert.Contains("longitudinalFootprintWidthMm", source);
            StringAssert.Contains("excludedInputs", source);
        }

        [Test]
        public void BatchUsesDedicatedR6ProtocolAndNoEvaluatorInput()
        {
            string source = ReadProjectFile(
                "Assets/ThoracicUSCoach/Editor/ThoracicG2R6CrossPlaneBatch.cs");
            StringAssert.Contains("G2_R6_CROSS_PLANE_DISCRIMINATION_20261101_R01", source);
            StringAssert.DoesNotContain("T7", source);
            StringAssert.DoesNotContain("ScoreCandidate", source);
        }

        [Test]
        public void Active2DBaselineWasNotModifiedByR6()
        {
            string source = ReadProjectFile(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs");
            StringAssert.DoesNotContain("G2R6", source);
            StringAssert.DoesNotContain("CROSS_PLANE_DISCRIMINATION", source);
        }
    }
}
