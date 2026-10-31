using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class PositionGuidedT7R3Tests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [Test]
        public void PoseAcceptance_IsStrictlyFull3DAndOrientationBounded()
        {
            MethodInfo method = Method("P1R3PoseAccepted");
            Assert.IsTrue((bool)method.Invoke(null, new object[] { 0.2f, 0.1f, 0.24f, 0.5f }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { 0.2f, 0.1f, 0.251f, 0f }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { 0.1f, 0.1f, 0.2f, 0.501f }));
        }

        [Test]
        public void FineStep_RemainsCompatibleWithSharedProgressFloor()
        {
            Assert.That(Thoracic2DNavigation.MinimumMeaningfulProgressMm(0.5f),
                Is.EqualTo(0.25f).Within(0.0001f));
            Assert.IsTrue(Thoracic2DNavigation.HasMeaningfulProgress(0.5f, 0.25f));
        }

        [Test]
        public void Source_EntersFineStageBelowCoarseResolution()
        {
            string source = Source();
            StringAssert.Contains("private const float P1R3PoseToleranceMm = 0.25f", source);
            StringAssert.Contains("private const float P1R3FineStepMm = 0.5f", source);
            StringAssert.Contains("fineStage = true", source);
            StringAssert.Contains("active2DStepMm = requestedStep", source);
            StringAssert.Contains("fine movement failed minimum progress", source);
        }

        [Test]
        public void RuntimeDecisionSection_HasNoTargetOrEvaluatorQuery()
        {
            string source = Source();
            int start = source.IndexOf("private IEnumerator RunP1R3Navigation", StringComparison.Ordinal);
            int end = source.IndexOf("private IEnumerator RunP1R3Sensitivity", start,
                StringComparison.Ordinal);
            string navigation = source.Substring(start, end - start).ToLowerInvariant();
            foreach (string forbidden in new[]
            {
                "buildsweepcountreferences", "t7rightseed", "scorecandidate",
                "findsurfaceat", "trygetpublishedscanprovenance", "targetscore"
            })
                StringAssert.DoesNotContain(forbidden, navigation);
            StringAssert.Contains("calibration.targetpose", navigation);
            StringAssert.Contains("measurep1robotpose", navigation);
        }

        [Test]
        public void Registry_PreservesCumulativeRunBudgetsAndConsumedFailures()
        {
            string source = Source();
            StringAssert.Contains("P1R3MaximumDevelopmentRuns = 20", source);
            StringAssert.Contains("P1R3MaximumConfirmationRuns = 3", source);
            StringAssert.Contains("physicalRunConsumed = true", source);
            StringAssert.Contains("entry.status = \"FAILED\"", source);
            StringAssert.DoesNotContain("File.Delete", source);
        }

        [Test]
        public void Confirmation_RequiresFrozenConfiguration()
        {
            string source = Source();
            StringAssert.Contains("P1R3FrozenFile", source);
            StringAssert.Contains("P1-R3 configuration is not frozen", source);
            StringAssert.Contains("LoadP1R3FrozenCalibration", source);
        }

        [Test]
        public void MovementSettings_AreRestoredOnAllPaths()
        {
            string source = Source();
            StringAssert.Contains("finally", source);
            StringAssert.Contains("active2DStepMm = originalStep", source);
            StringAssert.Contains("active2DWorkspaceOrigin = originalWorkspaceOrigin", source);
            StringAssert.Contains("active2DCaudalAxis = originalCaudalAxis", source);
        }

        private static MethodInfo Method(string name)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(name, PrivateStatic);
            Assert.That(method, Is.Not.Null);
            return method;
        }

        private static string Source()
        {
            return File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7R3.cs");
        }
    }
}
