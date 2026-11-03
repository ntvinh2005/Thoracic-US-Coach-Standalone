using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class RandomStartLandmarkDemoTests
    {
        [Test]
        public void AnalysisArguments_ContainOnlyActorSessionAndFrozenModelInputs()
        {
            string[] arguments = ThoracicUSCoachController
                .BuildRandomStartLandmarkDemoAnalysisArguments(
                    "C:/project", "C:/project/session", "C:/project/session/trajectory_actor.json",
                    "C:/project/model.joblib", "C:/project/session/actor_analysis");
            string joined = string.Join(" ", arguments);

            CollectionAssert.Contains(arguments, "--session-root");
            CollectionAssert.Contains(arguments, "--actor");
            CollectionAssert.Contains(arguments, "--model");
            CollectionAssert.Contains(arguments, "--output");
            StringAssert.DoesNotContain("evaluator_only", joined);
            StringAssert.DoesNotContain("trajectory_privileged", joined);
            StringAssert.DoesNotContain("target-position", joined);
            StringAssert.DoesNotContain("reference", joined);
        }

        [Test]
        public void AcceptedMap_RequiresSevenOrderedSupportedLevels()
        {
            var map = new ThoracicUSCoachController.RandomStartLandmarkDemoMap
            {
                overallMapStatus = "ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP",
                predictedT7RelativeMm = 42f
            };
            for (int index = 0; index < 7; index++)
            {
                map.levels.Add(new ThoracicUSCoachController.RandomStartLandmarkDemoLevel
                {
                    ordinal = index + 1,
                    inferredLevel = "T" + (index + 2),
                    accepted = true,
                    centerRelativeMm = index * 20f
                });
            }

            Assert.IsTrue(ThoracicUSCoachController.ValidateRandomStartLandmarkDemoMap(
                map, out string reason), reason);
            map.levels[1].accepted = false;
            Assert.IsFalse(ThoracicUSCoachController.ValidateRandomStartLandmarkDemoMap(
                map, out reason));
            StringAssert.Contains("V2", reason);
        }

        [Test]
        public void AmbiguousOrIncompleteMap_DoesNotAuthorizeMarkers()
        {
            var map = new ThoracicUSCoachController.RandomStartLandmarkDemoMap
            {
                overallMapStatus = "INCONCLUSIVE",
                abstentionReason = "ambiguous extra event"
            };

            Assert.IsFalse(ThoracicUSCoachController.ValidateRandomStartLandmarkDemoMap(
                map, out string reason));
            StringAssert.Contains("ambiguous extra event", reason);
        }

        [Test]
        public void MarkerProjection_UsesExecutedPathAtActorRelativeCoordinate()
        {
            var coordinates = new List<float> { -10f, 0f, 10f, 20f };
            var contacts = new List<Vector3>
            {
                new Vector3(-0.01f, 0f, 0f), Vector3.zero,
                new Vector3(0.01f, 0f, 0f), new Vector3(0.02f, 0f, 0f)
            };
            var normals = new List<Vector3>
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up
            };

            Assert.IsTrue(ThoracicUSCoachController.TryInterpolateRandomStartDemoContact(
                10f, coordinates, contacts, normals, out Vector3 contact, out Vector3 normal));
            Assert.That(contact.x, Is.EqualTo(0.01f).Within(0.0001f));
            Assert.That(normal, Is.EqualTo(Vector3.up));
        }

        [Test]
        public void RuntimeOnGUI_DoesNotInvokeHistoricalPhase2DiagnosticPanel()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string source = File.ReadAllText(Path.Combine(projectRoot,
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"));

            StringAssert.Contains("DrawRandomStartLandmarkDemoGUI();", source);
            StringAssert.DoesNotContain("DrawPhase2PocDiagnosticGUI();", source);
        }
    }
}
