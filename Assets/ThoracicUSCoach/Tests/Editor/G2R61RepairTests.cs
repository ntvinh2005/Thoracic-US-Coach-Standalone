using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class G2R61RepairTests
    {
        private const string RuntimePath =
            "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R5TargetedReacquisition.cs";

        [Test]
        public void AlternativeImageIsPersistedBeforeQualityEvaluation()
        {
            string source = File.ReadAllText(RuntimePath);
            int persist = source.IndexOf(
                "File.WriteAllBytes(Path.Combine(frameRoot, imageFile), capture.pngBytes)",
                StringComparison.Ordinal);
            int evaluate = source.IndexOf(
                "EvaluateG2R5InitialCrossPlaneImageChange(", StringComparison.Ordinal);
            Assert.That(persist, Is.GreaterThan(0));
            Assert.That(evaluate, Is.GreaterThan(persist));
            StringAssert.Contains("PERSISTED_INCONCLUSIVE_ACQUISITION", source);
        }

        [Test]
        public void DiagnosticEpisodeContainsNoDirectPoseReset()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.DoesNotContain("SetPositionAndRotation", source);
            StringAssert.Contains("PHYSICAL_FAILURE_RECOVERY_HOME", source);
            StringAssert.Contains("NavigateG2R5(actor, 0f, 90f", source);
        }

        [TestCase("fresh capture exception", "ABORTED_INFRASTRUCTURE")]
        [TestCase("capture workspace invalid", "ABORTED_SAFETY")]
        [TestCase("unsafe movement rejected", "ABORTED_SAFETY")]
        public void FaultClassificationIsExplicit(string message, string expected)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "ClassifyG2R5Failure", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            string actual = (string)method.Invoke(null, new object[] { new InvalidOperationException(message) });
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void QualityFailureAndRecoveryFailureHaveDistinctFinalStates()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("INCONCLUSIVE_ACQUISITION", source);
            StringAssert.Contains("RECOVERY_FAILED", source);
            StringAssert.Contains("recoveryFailureReason", source);
            StringAssert.Contains("stopReason", source);
        }

        [Test]
        public void RecoveryActionsAreRecordedThroughNormalMovementInfrastructure()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("RecoverG2R5PhysicallyToHome", source);
            StringAssert.Contains("ExecuteG2R5Movement(actor", source);
            StringAssert.Contains("actor.movements.Add", source);
            StringAssert.Contains("recoveryAttempted", source);
            StringAssert.Contains("recoverySucceeded", source);
        }

        [Test]
        public void RepairedProtocolUsesOneFrozenAlternativePlane()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("ACTOR_GUIDED_REPAIRED_CROSS_PLANE_DISCRIMINATION", source);
            StringAssert.Contains("yaw 90/65 views at matched 60 mm depth", source);
            StringAssert.Contains("Mathf.Approximately(region.variants[1].surfaceYawDeg, 65f)", source);
        }
    }
}
