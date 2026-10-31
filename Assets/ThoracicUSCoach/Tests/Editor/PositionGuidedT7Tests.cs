using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class PositionGuidedT7Tests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [TestCase(9f, 0f, 3f, Active2DAction.Caudal)]
        [TestCase(-9f, 0f, 3f, Active2DAction.Cranial)]
        [TestCase(1f, 6f, 3f, Active2DAction.Lateral)]
        [TestCase(1f, -6f, 3f, Active2DAction.Medial)]
        [TestCase(2.628f, 0f, 3f, Active2DAction.Caudal)]
        [TestCase(-0.336f, 0f, 3f, Active2DAction.Stop)]
        [TestCase(-0.336f, 0f, 0.5f, Active2DAction.Cranial)]
        public void StoredPoseController_SelectsOnlyPredictedImprovingAction(
            float longitudinalError, float lateralError,
            float requestedStep, Active2DAction expected)
        {
            object result = Method("ChooseP1ImprovingAction").Invoke(
                null, new object[] { longitudinalError, lateralError, requestedStep, 0.05f });
            Assert.AreEqual(expected, (Active2DAction)result);
        }

        [Test]
        public void CoarsePrediction_ImprovesOriginalP1Residual()
        {
            float improvement = (float)Method("P1PredictedAxisImprovement").Invoke(
                null, new object[] { 2.628f, 3f });
            Assert.That(improvement, Is.EqualTo(2.256f).Within(0.001f));
            Assert.That(improvement, Is.GreaterThan(0.05f));
        }

        [Test]
        public void FineStep_RespectsExistingMeaningfulProgressFloor()
        {
            Assert.That(Thoracic2DNavigation.MinimumMeaningfulProgressMm(0.5f),
                Is.EqualTo(0.25f).Within(0.0001f));
            Assert.IsTrue(Thoracic2DNavigation.HasMeaningfulProgress(0.5f, 0.25f));
            Assert.IsFalse(Thoracic2DNavigation.HasMeaningfulProgress(0.5f, 0.249f));
        }

        [Test]
        public void PoseAcceptance_RequiresFull3DContactTolerance()
        {
            Assert.IsFalse((bool)Method("P1PoseMeetsAcceptance").Invoke(
                null, new object[] { 0.1f, 0.1f, 0.6f, 0f }));
            Assert.IsTrue((bool)Method("P1PoseMeetsAcceptance").Invoke(
                null, new object[] { 0.1f, 0.1f, 0.49f, 0f }));
        }

        [Test]
        public void PoseAcceptance_RequiresOrientationTolerance()
        {
            Assert.IsFalse((bool)Method("P1PoseMeetsAcceptance").Invoke(
                null, new object[] { 0.1f, 0.1f, 0.2f, 0.51f }));
            Assert.IsTrue((bool)Method("P1PoseMeetsAcceptance").Invoke(
                null, new object[] { 0.1f, 0.1f, 0.2f, 0.5f }));
        }

        [Test]
        public void NavigationSource_UsesBoundedFineStageAndTerminatesOnFailure()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs");
            StringAssert.Contains("private const float P1FineStepMm = 0.5f", source);
            StringAssert.Contains("fine movement budget exhausted", source);
            StringAssert.Contains("accepted fine movement did not produce", source);
            StringAssert.Contains("P1 stopped on rejected or unsafe physical action", source);
            StringAssert.Contains("active2DStepMm = requestedStepMm", source);
        }

        [Test]
        public void CalibrationManifest_PersistsPublicationAssociation()
        {
            Type calibration = typeof(ThoracicUSCoachController).GetNestedType(
                "P1CalibrationState", BindingFlags.NonPublic);
            Assert.That(calibration, Is.Not.Null);
            foreach (string field in new[]
            {
                "poseEpoch", "poseAppliedUnityFrame", "producerPoseEpoch",
                "producerUnityFrame", "producerScanSerial", "captureUnityFrame",
                "referenceImageSha256", "provenanceMatchedPublishedFrame"
            })
                Assert.That(calibration.GetField(field), Is.Not.Null, "Missing calibration field " + field);
        }

        [Test]
        public void NavigationLoop_DoesNotQueryT7OrEvaluatorAtRuntime()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs");
            int start = source.IndexOf("private IEnumerator RunP1StoredPoseNavigation", StringComparison.Ordinal);
            int end = source.IndexOf("private bool TryP1PhysicalAction", start, StringComparison.Ordinal);
            string navigation = source.Substring(start, end - start).ToLowerInvariant();
            foreach (string forbidden in new[]
            {
                "t7rightseed", "buildsweepcountreferences", "scorecandidate",
                "targetreference", "nearestreference", "evaluatordistance"
            })
                StringAssert.DoesNotContain(forbidden, navigation);
            StringAssert.Contains("calibration.storedtargetpose", navigation);
            StringAssert.Contains("tryp1physicalaction", navigation);
        }

        [Test]
        public void ActorSchemas_ExposeRobotPoseButExcludeEvaluatorAndAnatomyFields()
        {
            Type controller = typeof(ThoracicUSCoachController);
            foreach (string nestedName in new[] { "P1ActorStep", "P1ActorTrajectory" })
            {
                Type nested = controller.GetNestedType(nestedName, BindingFlags.NonPublic);
                Assert.That(nested, Is.Not.Null);
                string[] fields = nested.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(item => item.Name.ToLowerInvariant()).ToArray();
                foreach (string forbidden in new[]
                {
                    "evaluatorreference", "anatomy", "mesh", "provenance",
                    "targetscore", "distanceTot7".ToLowerInvariant()
                })
                    Assert.IsFalse(fields.Any(item => item.Contains(forbidden)),
                        nestedName + " leaked " + forbidden);
            }
        }

        [Test]
        public void Source_UsesSetupAndCleanupPlacementOutsideNavigationLoop()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs");
            int navigationStart = source.IndexOf(
                "private IEnumerator RunP1StoredPoseNavigation", StringComparison.Ordinal);
            int navigationEnd = source.IndexOf(
                "private bool TryP1PhysicalAction", navigationStart, StringComparison.Ordinal);
            string navigation = source.Substring(navigationStart, navigationEnd - navigationStart);
            StringAssert.DoesNotContain("SetPositionAndRotation", navigation);
            StringAssert.Contains("TryActive2DMove", source);
            StringAssert.Contains("finally", source);
            StringAssert.Contains("active2DWorkspaceOrigin = originalWorkspaceOrigin", source);
        }

        [Test]
        public void PhysicalPilot_IsSingleRunAndRequiresFrozenOfflineEvidence()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.PositionGuidedT7.cs");
            StringAssert.Contains("offline_results.sha256", source);
            StringAssert.Contains("pilot_evaluation.json", source);
            StringAssert.Contains("File.Exists(Path.Combine(P1OutputDirectory, \"pilot_evaluation.json\"))",
                source);
            StringAssert.Contains("CALIBRATED_POSITION_GUIDED_BASELINE", source);
        }

        private static MethodInfo Method(string name)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(name, PrivateStatic);
            Assert.That(method, Is.Not.Null, "Missing production method " + name);
            return method;
        }
    }
}
