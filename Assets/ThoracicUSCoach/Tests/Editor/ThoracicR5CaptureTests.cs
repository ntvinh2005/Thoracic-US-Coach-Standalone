using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class ThoracicR5CaptureTests
    {
        [Test]
        public void ScoutManifest_ExactlyAtFrozenBudgets_IsAccepted()
        {
            R5CaptureManifest manifest = ValidManifest(ThoracicR5CaptureProtocol.ScoutMode);
            for (int routeIndex = 0; routeIndex < 8; routeIndex++)
            {
                R5CaptureRoute route = Route(routeIndex, "scout");
                for (int action = 0; action < 11; action++) route.actions.Add(action % 2 == 0 ? "Lateral" : "Medial");
                manifest.routes.Add(route);
            }

            Assert.DoesNotThrow(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));
        }

        [Test]
        public void ScoutManifest_RejectsEveryFrozenBudgetOverflow()
        {
            R5CaptureManifest manifest = ValidManifest(ThoracicR5CaptureProtocol.ScoutMode);
            manifest.maxStartProposals = 49;
            manifest.routes.Add(Route(0, "scout"));
            Assert.Throws<InvalidOperationException>(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));

            manifest = ValidManifest(ThoracicR5CaptureProtocol.ScoutMode);
            manifest.maxRoutes = 9;
            manifest.routes.Add(Route(0, "scout"));
            Assert.Throws<InvalidOperationException>(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));

            manifest = ValidManifest(ThoracicR5CaptureProtocol.ScoutMode);
            manifest.maxMovementsPerRoute = 13;
            manifest.routes.Add(Route(0, "scout"));
            Assert.Throws<InvalidOperationException>(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));

            manifest = ValidManifest(ThoracicR5CaptureProtocol.ScoutMode);
            manifest.maxImages = 97;
            manifest.routes.Add(Route(0, "scout"));
            Assert.Throws<InvalidOperationException>(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));
        }

        [Test]
        public void DevelopmentManifest_EnforcesArmsAndObservationBudget()
        {
            R5CaptureManifest manifest = ValidManifest(ThoracicR5CaptureProtocol.DevelopmentMode);
            manifest.maxStartProposals = 96;
            manifest.maxRoutes = 16;
            manifest.maxMovementsPerRoute = 80;
            manifest.maxImages = 1296;
            manifest.maxObservationsPerRoute = 81;
            for (int routeIndex = 0; routeIndex < 16; routeIndex++)
            {
                R5CaptureRoute route = Route(routeIndex, routeIndex < 8 ? "randomized_start" : "target_enriched");
                for (int action = 0; action < 80; action++) route.actions.Add("Lateral");
                manifest.routes.Add(route);
            }
            Assert.DoesNotThrow(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));

            manifest.routes[15].arm = "target_enriched_extra";
            Assert.Throws<InvalidOperationException>(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));
        }

        [Test]
        public void StartOffsets_AreDeterministicStratifiedAndWithinSafeBounds()
        {
            ThoracicR5CaptureProtocol.StartOffsets(
                "near_cranial", 20261026, 3, 20f,
                out float longitudinalA, out float lateralA, out float yawA);
            ThoracicR5CaptureProtocol.StartOffsets(
                "near_cranial", 20261026, 3, 20f,
                out float longitudinalB, out float lateralB, out float yawB);

            Assert.That(longitudinalA, Is.EqualTo(longitudinalB));
            Assert.That(lateralA, Is.EqualTo(lateralB));
            Assert.That(yawA, Is.EqualTo(yawB));
            Assert.That(longitudinalA, Is.LessThan(0f));
            Assert.That(Mathf.Abs(lateralA), Is.LessThanOrEqualTo(12f));
            Assert.That(Mathf.Abs(yawA), Is.LessThanOrEqualTo(9f));
        }

        [Test]
        public void CaptureManifest_HasNoPriorBaseTransformField()
        {
            string json = JsonUtility.ToJson(ValidManifest(ThoracicR5CaptureProtocol.ScoutMode));

            StringAssert.DoesNotContain("baseStart", json);
            StringAssert.DoesNotContain("probePosition", json);
            StringAssert.DoesNotContain("probeRotation", json);
        }

        [Test]
        public void StopHandoffAndObserve_AreForbiddenCaptureMovements()
        {
            foreach (string forbidden in new[] { "Stop", "PhaseHandoff", "Observe" })
            {
                R5CaptureManifest manifest = ValidManifest(ThoracicR5CaptureProtocol.ScoutMode);
                R5CaptureRoute route = Route(0, "scout");
                route.actions.Add(forbidden);
                manifest.routes.Add(route);
                Assert.Throws<InvalidOperationException>(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));
            }
        }

        [Test]
        public void ScoutRoutes_CannotDeclareMoreThanNinetySixImagesIncludingRepeats()
        {
            R5CaptureManifest manifest = ValidManifest(ThoracicR5CaptureProtocol.ScoutMode);
            for (int routeIndex = 0; routeIndex < 8; routeIndex++)
            {
                R5CaptureRoute route = Route(routeIndex, "scout");
                for (int action = 0; action < 11; action++) route.actions.Add("Lateral");
                manifest.routes.Add(route);
            }
            manifest.routes[0].repeatRenderAtObservationIndices.Add(0);

            Assert.Throws<InvalidOperationException>(() => ThoracicR5CaptureProtocol.ValidateStructure(manifest));
        }

        [Test]
        public void ActionExecutionPath_HasNoT7OrScoreDependentGate()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string source = File.ReadAllText(Path.Combine(
                projectRoot, "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.R5Capture.cs"));
            int start = source.IndexOf("private IEnumerator ExecuteR5Action", StringComparison.Ordinal);
            int end = source.IndexOf("private IEnumerator StoreR5Observation", start, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string executionPath = source.Substring(start, end - start);

            StringAssert.DoesNotContain("t7LevelAcceptable", executionPath);
            StringAssert.DoesNotContain("BuildActive2DPrivilegedFrame", executionPath);
            StringAssert.DoesNotContain("ScoreCandidate", executionPath);
            StringAssert.Contains("TryActive2DAction", executionPath);
        }

        private static R5CaptureManifest ValidManifest(string mode)
        {
            return new R5CaptureManifest
            {
                protocolId = "G2_R5_FIXED_ANATOMY_DIVERSITY_V1",
                mode = mode,
                scene = "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity",
                unityVersion = "6000.3.9f1",
                sourceCommit = new string('a', 40),
                baseCommit = new string('b', 40),
                captureBranch = "codex/r5-unity",
                datasetRunId = "r5-test",
                outputDirectory = "Artifacts/ThoracicUSCoach/active2d/r5-test",
                sharedContractPath = "schema.json",
                sharedContractSha256 = new string('1', 64),
                sharedContractMaterializedSha256 = new string('2', 64),
                scientificCriteriaPath = "protocol.json",
                scientificCriteriaSha256 = new string('3', 64),
                scientificCriteriaMaterializedSha256 = new string('4', 64),
                independentAuditPath = "audit.json",
                independentAuditSha256 = new string('5', 64),
                independentAuditMaterializedSha256 = new string('6', 64),
                acceptableScoreFloor = ThoracicR5CaptureProtocol.FrozenAcceptableScoreFloor,
                maxStartProposals = 48,
                maxRoutes = 8,
                maxMovementsPerRoute = 12,
                maxImages = 96,
                maxObservationsPerRoute = 81
            };
        }

        private static R5CaptureRoute Route(int index, string arm)
        {
            return new R5CaptureRoute
            {
                routeId = "route_" + index,
                familyId = "family_" + index,
                arm = arm,
                coverageIntent = "bounded-test",
                startStratum = index % 2 == 0 ? "near_cranial" : "near_caudal",
                proposalSeed = 20261026 + index,
                maxStartAttempts = 6,
                actions = new List<string>(),
                repeatRenderAtObservationIndices = new List<int>()
            };
        }
    }
}
