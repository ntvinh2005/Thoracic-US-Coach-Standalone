using System;
using System.IO;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class ThoracicR6FinalSprintTests
    {
        [Test]
        public void BasinManifest_AcceptsFrozenThreeHundredTwelveCandidateGrid()
        {
            R6FinalSprintManifest manifest = ValidManifest(ThoracicR6FinalSprintProtocol.BasinMapMode);
            AddFrozenControls(manifest);
            for (int index = 0; index < 312; index++)
                manifest.mapCandidates.Add(Candidate(
                    index, "r6_control_hard_00_f0008", index < 240 ? "coarse" : "fine"));

            Assert.DoesNotThrow(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
            manifest.mapCandidates.Add(Candidate(312, "r6_control_hard_00_f0008", "fine"));
            Assert.Throws<InvalidOperationException>(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
        }

        [Test]
        public void BasinManifest_RejectsUnprovenOrUnknownControls()
        {
            R6FinalSprintManifest manifest = ValidManifest(ThoracicR6FinalSprintProtocol.BasinMapMode);
            AddFrozenControls(manifest);
            R6PositiveControl control = manifest.positiveControls[0];
            control.sourceScore = ThoracicR6FinalSprintProtocol.FrozenAcceptableFloor - 0.01f;
            for (int index = 0; index < 312; index++)
                manifest.mapCandidates.Add(Candidate(
                    index, index == 0 ? "missing" : "r6_control_hard_00_f0008",
                    index < 240 ? "coarse" : "fine"));

            Assert.Throws<InvalidOperationException>(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
        }

        [Test]
        public void WideScout_AcceptsExactFiveHundredTwelveObservationBudget()
        {
            R6FinalSprintManifest manifest = ValidManifest(ThoracicR6FinalSprintProtocol.WideScoutMode);
            for (int routeIndex = 0; routeIndex < 8; routeIndex++)
            {
                R6CaptureRoute route = Route(routeIndex, "wide_scout");
                for (int action = 0; action < 60; action++) route.actions.Add("Lateral");
                route.repeatRenderAtObservationIndices.Add(0);
                route.repeatRenderAtObservationIndices.Add(60);
                route.repeatRenderAtObservationIndices.Add(30);
                manifest.routes.Add(route);
            }

            Assert.DoesNotThrow(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
            manifest.routes[0].repeatRenderAtObservationIndices.Add(1);
            Assert.Throws<InvalidOperationException>(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
        }

        [Test]
        public void Development_EnforcesSixteenTrajectoriesAndArmCaps()
        {
            R6FinalSprintManifest manifest = ValidManifest(ThoracicR6FinalSprintProtocol.DevelopmentMode);
            for (int routeIndex = 0; routeIndex < 16; routeIndex++)
            {
                R6CaptureRoute route = Route(
                    routeIndex, routeIndex < 8 ? "randomized_start" : "target_enriched");
                for (int action = 0; action < 120; action++) route.actions.Add("Caudal");
                manifest.routes.Add(route);
            }

            Assert.DoesNotThrow(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
            manifest.routes[15].arm = "randomized_start";
            Assert.Throws<InvalidOperationException>(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
        }

        [Test]
        public void RouteRepeatIndicesMustBeUniqueAndInRange()
        {
            R6FinalSprintManifest manifest = ValidManifest(ThoracicR6FinalSprintProtocol.WideScoutMode);
            R6CaptureRoute route = Route(0, "wide_scout");
            route.actions.Add("Lateral");
            route.repeatRenderAtObservationIndices.Add(2);
            manifest.routes.Add(route);

            Assert.Throws<InvalidOperationException>(() => ThoracicR6FinalSprintProtocol.Validate(manifest));
        }

        [Test]
        public void WideScoutExecutor_DoesNotReadTargetOrEvaluatorSignals()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.R6FinalSprint.cs");
            int begin = source.IndexOf("private R6ActionRecord ExecuteR6TargetIndependentAction", StringComparison.Ordinal);
            int end = source.IndexOf("private IEnumerator CaptureR6Observation", begin, StringComparison.Ordinal);
            Assert.That(begin, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(begin));
            string executor = source.Substring(begin, end - begin);

            StringAssert.DoesNotContain("ScoreCandidate", executor);
            StringAssert.DoesNotContain("BuildActive2DPrivilegedFrame", executor);
            StringAssert.DoesNotContain("finalTarget", executor);
            StringAssert.DoesNotContain("t7", executor.ToLowerInvariant());
        }

        private static R6FinalSprintManifest ValidManifest(string mode)
        {
            return new R6FinalSprintManifest
            {
                protocolId = ThoracicR6FinalSprintProtocol.ProtocolId,
                mode = mode,
                scene = "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity",
                unityVersion = "6000.3.9f1",
                sourceCommit = new string('a', 40),
                baseCommit = ThoracicR6FinalSprintProtocol.FrozenBaseCommit,
                captureBranch = ThoracicR6FinalSprintProtocol.CaptureBranch,
                datasetRunId = "r6_test",
                outputDirectory = "Artifacts/ThoracicUSCoach/active2d/r6_test",
                acceptableScoreFloor = ThoracicR6FinalSprintProtocol.FrozenAcceptableFloor,
                coordinatorProtocolPath = ThoracicR6FinalSprintProtocol.CoordinatorProtocolPath,
                coordinatorProtocolSha256 = ThoracicR6FinalSprintProtocol.CoordinatorProtocolSha256,
                auditProtocolPath = ThoracicR6FinalSprintProtocol.AuditProtocolPath,
                auditProtocolSha256 = ThoracicR6FinalSprintProtocol.AuditProtocolSha256,
                prerequisiteEvidencePath = "prerequisite.json",
                prerequisiteEvidenceSha256 = new string('e', 64),
                prerequisiteRequiredVerdict = "GO"
            };
        }

        private static void AddFrozenControls(R6FinalSprintManifest manifest)
        {
            manifest.positiveControls.Add(Control(
                "r6_control_hard_00_f0008", "46fe40c4c9af5848f6cad450840f7bae96d13c4d259307847a2cdcfc922a9c90"));
            manifest.positiveControls.Add(Control(
                "r6_control_random_01_f0010", "eeb9360661573aa64d1f3cbb000ff8ecc3dd9ccb4d3ba05277b58aa26db688a8"));
            manifest.positiveControls.Add(Control(
                "r6_control_random_05_f0016", "ddad5db3ec4ee58a758179eb8044ba80852871f9ba095e668b1abaea802bc6b7"));
            manifest.positiveControls.Add(Control(
                "r6_control_hard_01_f0009", "126b14863d3b78a6a829e64b5185d081c09facec0824547ae9fbff1d26a7df0a"));
        }

        private static R6PositiveControl Control(string id, string imageSha256)
        {
            return new R6PositiveControl
            {
                controlId = id,
                sourceCaseId = "historical_case",
                sourceFrameIndex = 8,
                sourceArm = "hard_causal",
                sourceImageSha256 = imageSha256,
                sourcePrivilegedAnnotationsSha256 = new string('a', 64),
                sourceScore = 0.61f,
                sourceMargin = 0.018f,
                repeatRenders = 3
            };
        }

        private static R6MapCandidate Candidate(int index, string controlId, string stage)
        {
            return new R6MapCandidate
            {
                candidateId = "candidate_" + index,
                anchorControlId = controlId,
                stage = stage,
                repeatRenders = 1
            };
        }

        private static R6CaptureRoute Route(int index, string arm)
        {
            return new R6CaptureRoute
            {
                routeId = "route_" + index,
                familyId = "family_" + index,
                arm = arm,
                proposalSeed = 20261026 + index
            };
        }
    }
}
