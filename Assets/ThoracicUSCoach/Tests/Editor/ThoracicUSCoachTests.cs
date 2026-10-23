using NUnit.Framework;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class ThoracicUSCoachTests
    {
        [Test]
        public void SweepSplit_IsStable()
        {
            Assert.AreEqual(SweepSplit.ForSweep("sweep-A"), SweepSplit.ForSweep("sweep-A"));
        }

        [Test]
        public void SweepSplit_AssignsOnlyKnownPartitions()
        {
            for (int i = 0; i < 100; i++)
            {
                string split = SweepSplit.ForSweep("sweep-" + i);
                CollectionAssert.Contains(new[] { "train", "dev", "test" }, split);
            }
        }

        [Test]
        public void CoachAction_HasStableWireValues()
        {
            Assert.AreEqual(-1, (int)CoachAction.Backward);
            Assert.AreEqual(0, (int)CoachAction.Stop);
            Assert.AreEqual(1, (int)CoachAction.Forward);
        }

        [Test]
        public void TwoDNavigation_ProjectsDirectionOntoLocalSkinTangent()
        {
            UnityEngine.Vector3 tangent = Thoracic2DNavigation.TangentToward(
                UnityEngine.Vector3.zero,
                new UnityEngine.Vector3(3f, 5f, 4f),
                UnityEngine.Vector3.up);
            Assert.That(UnityEngine.Vector3.Dot(tangent, UnityEngine.Vector3.up), Is.EqualTo(0f).Within(0.0001f));
            Assert.That(tangent.magnitude, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(tangent.x, Is.GreaterThan(0f));
            Assert.That(tangent.z, Is.GreaterThan(0f));
        }

        [Test]
        public void TwoDNavigation_RequiresPhysicalMotionOnBothAxes()
        {
            Assert.IsTrue(Thoracic2DNavigation.HasTwoAxisMotion(12f, 8f));
            Assert.IsFalse(Thoracic2DNavigation.HasTwoAxisMotion(12f, 0f));
            Assert.IsFalse(Thoracic2DNavigation.HasTwoAxisMotion(0f, 8f));
        }

        [Test]
        public void TwoDNavigation_FlagsTeleportLikeDiscontinuity()
        {
            Assert.IsFalse(Thoracic2DNavigation.IsTeleportLike(2.2f, 2f, 0.5f));
            Assert.IsTrue(Thoracic2DNavigation.IsTeleportLike(8f, 2f, 2.5f));
        }

        [Test]
        public void DiverseSchedule_IsReproducibleAndSplitBeforeCapture()
        {
            Assert.AreEqual(DiverseTrajectorySchedule.SeedForSequence(7), DiverseTrajectorySchedule.SeedForSequence(7));
            Assert.AreNotEqual(DiverseTrajectorySchedule.SeedForSequence(7), DiverseTrajectorySchedule.SeedForSequence(8));
            Assert.AreEqual("train", DiverseTrajectorySchedule.SplitForSequence(11));
            Assert.AreEqual("dev", DiverseTrajectorySchedule.SplitForSequence(15));
            Assert.AreEqual("test", DiverseTrajectorySchedule.SplitForSequence(18));
            Assert.AreEqual("stress", DiverseTrajectorySchedule.EvaluationGroupForSequence(23));
        }

        [Test]
        public void DiverseSchedule_DoesNotShareConfigurationFamiliesAcrossSplits()
        {
            for (int left = 0; left < DiverseTrajectorySchedule.ScheduleLength; left++)
            for (int right = 0; right < DiverseTrajectorySchedule.ScheduleLength; right++)
            {
                if (DiverseTrajectorySchedule.SplitForSequence(left) == DiverseTrajectorySchedule.SplitForSequence(right)) continue;
                Assert.AreNotEqual(DiverseTrajectorySchedule.FamilyForSequence(left), DiverseTrajectorySchedule.FamilyForSequence(right));
            }
        }

        [Test]
        public void DiverseSchedule_ProducesDeclaredFullScaleAllocation()
        {
            Assert.AreEqual(300, Enumerable.Range(0, 500).Count(i => DiverseTrajectorySchedule.EvaluationGroupForSequence(i) == "training"));
            Assert.AreEqual(60, Enumerable.Range(0, 500).Count(i => DiverseTrajectorySchedule.EvaluationGroupForSequence(i) == "development"));
            Assert.AreEqual(100, Enumerable.Range(0, 500).Count(i => DiverseTrajectorySchedule.EvaluationGroupForSequence(i) == "heldout"));
            Assert.AreEqual(40, Enumerable.Range(0, 500).Count(i => DiverseTrajectorySchedule.EvaluationGroupForSequence(i) == "stress"));
        }

        [Test]
        public void PocScene_HasNoUnresolvedAssetGuids()
        {
            const string scenePath = "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity";
            string scene = File.ReadAllText(scenePath);
            string[] unresolved = Regex.Matches(scene, "guid: ([0-9a-f]{32})")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .Where(guid => string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                .ToArray();
            Assert.That(unresolved, Is.Empty,
                "Scene contains references copied from pre-package asset paths: " + string.Join(", ", unresolved));
        }

        [Test]
        public void UltrasoundPrefab_HasCoreRuntimeComponentsAndProbeReferences()
        {
            const string prefabPath = "Packages/com.cssalt.smmarts-sdk-package/Runtime/Ultrasound/Resources/Ultrasound and Probe.prefab";
            UnityEngine.GameObject prefab = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null);
            Assert.AreEqual(2, prefab.GetComponentsInChildren<SMMARTS.UltrasoundManager>(true).Length);
            Assert.AreEqual(2, prefab.GetComponentsInChildren<SMMARTS.UltrasoundDisplayController>(true).Length);
            SkinIntersectionCompression skin = prefab.GetComponentInChildren<SkinIntersectionCompression>(true);
            Assert.That(skin, Is.Not.Null);
            Assert.That(skin.CenterTransform, Is.Not.Null);
            Assert.That(skin.RearCenterTransform, Is.Not.Null);
        }

        [Test]
        public void OneDContract_UsesExactDisjointAcceptableSet()
        {
            bool[] acceptable = { false, false, true, false, false, true, false };
            Assert.IsTrue(OneDEnvironmentContract.IsAcceptable(acceptable, 2));
            Assert.IsFalse(OneDEnvironmentContract.IsAcceptable(acceptable, 3));
            Assert.IsFalse(OneDEnvironmentContract.IsAcceptable(acceptable, 4));
            Assert.IsTrue(OneDEnvironmentContract.IsAcceptable(acceptable, 5));
            Assert.AreEqual(2, OneDEnvironmentContract.NearestAcceptableIndex(acceptable, 3));
        }

        [Test]
        public void OneDContract_ActionDirectionAndBoundaryClampAreStable()
        {
            Assert.AreEqual(3, OneDEnvironmentContract.StepIndex(2, CoachAction.Forward, 1, 5));
            Assert.AreEqual(1, OneDEnvironmentContract.StepIndex(2, CoachAction.Backward, 1, 5));
            int probed = OneDEnvironmentContract.StepIndex(2, CoachAction.Forward, 1, 5);
            Assert.AreEqual(2, OneDEnvironmentContract.StepIndex(probed, CoachAction.Backward, 1, 5));
            Assert.AreEqual(0, OneDEnvironmentContract.StepIndex(0, CoachAction.Backward, 1, 5));
            OneDStepResult clamped = OneDEnvironmentContract.Step(
                0, CoachAction.Backward, 1,
                new[] { false, false, true }, new[] { -1f, 0f, 1f }, true, true, 0, 10);
            Assert.IsTrue(clamped.invalidMotion);
            Assert.IsTrue(clamped.patchBoundaryViolation);
            Assert.IsFalse(clamped.explicitStopSuccess);
        }

        [Test]
        public void OneDContract_StopSuccessRequiresExactTargetContactAndWorkspace()
        {
            bool[] acceptable = { false, true, false };
            float[] coordinates = { -1f, 0f, 1f };
            Assert.IsTrue(OneDEnvironmentContract.Step(1, CoachAction.Stop, 1, acceptable, coordinates, true, true, 0, 5).explicitStopSuccess);
            Assert.IsFalse(OneDEnvironmentContract.Step(0, CoachAction.Stop, 1, acceptable, coordinates, true, true, 0, 5).explicitStopSuccess);
            Assert.IsFalse(OneDEnvironmentContract.Step(1, CoachAction.Stop, 1, acceptable, coordinates, false, true, 0, 5).explicitStopSuccess);
            Assert.IsFalse(OneDEnvironmentContract.Step(1, CoachAction.Stop, 1, acceptable, coordinates, true, false, 0, 5).explicitStopSuccess);
        }

        [Test]
        public void OneDContract_TerminationAndTimeoutTruncationAreDistinct()
        {
            bool[] acceptable = { false, true, false };
            float[] coordinates = { -1f, 0f, 1f };
            OneDStepResult stopped = OneDEnvironmentContract.Step(0, CoachAction.Stop, 1, acceptable, coordinates, true, true, 0, 2);
            Assert.IsTrue(stopped.terminated);
            Assert.IsFalse(stopped.truncated);
            OneDStepResult timedOut = OneDEnvironmentContract.Step(0, CoachAction.Forward, 1, acceptable, coordinates, true, true, 1, 2);
            Assert.IsFalse(timedOut.terminated);
            Assert.IsTrue(timedOut.truncated);
        }

        [Test]
        public void OneDContract_OracleSolvesAllReachableDiagnosticStarts()
        {
            var suites = new List<bool[]>
            {
                new[] { true, false, false, false, false },
                new[] { false, false, true, false, false },
                new[] { false, false, false, false, true },
                new[] { false, true, false, true, false },
            };
            float[] coordinates = Enumerable.Range(0, 5).Select(index => (float)index).ToArray();
            foreach (bool[] acceptable in suites)
            for (int start = 0; start < acceptable.Length; start++)
            {
                int index = start;
                bool success = false;
                CoachAction previous = CoachAction.Stop;
                bool sawReversal = false;
                for (int step = 0; step < 8; step++)
                {
                    CoachAction action = OneDEnvironmentContract.OracleAction(acceptable, index);
                    if (previous != CoachAction.Stop && action != CoachAction.Stop && action != previous) sawReversal = true;
                    OneDStepResult result = OneDEnvironmentContract.Step(index, action, 1, acceptable, coordinates, true, true, step, 8);
                    index = result.pathIndex;
                    previous = action;
                    if (result.explicitStopSuccess) { success = true; break; }
                }
                Assert.IsTrue(success, "Oracle failed start " + start + " mask " + string.Join("", acceptable.Select(value => value ? "1" : "0")));
                Assert.IsFalse(sawReversal, "Shortest-path oracle should not oscillate");
            }
        }

        [Test]
        public void OneDContract_TargetAbsentNeverReportsSuccess()
        {
            bool[] absent = { false, false, false };
            float[] coordinates = { -1f, 0f, 1f };
            Assert.AreEqual(CoachAction.Stop, OneDEnvironmentContract.OracleAction(absent, 1));
            OneDStepResult result = OneDEnvironmentContract.Step(1, CoachAction.Stop, 1, absent, coordinates, true, true, 0, 5);
            Assert.IsTrue(result.terminated);
            Assert.IsFalse(result.explicitStopSuccess);
        }

        [Test]
        public void OneDContract_StrideAwareOracleReachesSkippedParityTargetViaBoundary()
        {
            bool[] acceptable = { false, false, true, false, false, false };
            float[] coordinates = Enumerable.Range(0, acceptable.Length).Select(index => (float)index).ToArray();
            int index = 1;
            bool success = false;
            for (int step = 0; step < 10; step++)
            {
                CoachAction action = OneDEnvironmentContract.OracleAction(acceptable, index, 2);
                OneDStepResult result = OneDEnvironmentContract.Step(
                    index, action, 2, acceptable, coordinates, true, true, step, 10);
                index = result.pathIndex;
                if (result.explicitStopSuccess) { success = true; break; }
            }
            Assert.IsTrue(success);
        }

        [Test]
        public void EnvironmentRequest_RoundTripsExactFrozenScenario()
        {
            FrozenScenarioDescription frozen = new FrozenScenarioDescription
            {
                trajectory = new TrajectoryConfigurationRecord { configurationFamily = "dev-family" },
                configurationSha256 = "configuration",
                labelsSha256 = "labels",
                episodeBudget = 35
            };
            frozen.poses.Add(new ProbePoseRecord
            {
                position = new UnityEngine.Vector3(1f, 2f, 3f),
                rotation = UnityEngine.Quaternion.identity
            });
            frozen.pathMm.Add(4.5f);
            frozen.referenceScores.Add(0.625f);
            frozen.acceptableIndices.Add(0);
            EnvironmentRequest request = new EnvironmentRequest
            {
                command = "reset",
                startIndex = 0,
                maxSteps = 35,
                replayFrozenScenario = true,
                frozenScenario = frozen
            };

            EnvironmentRequest restored = UnityEngine.JsonUtility.FromJson<EnvironmentRequest>(
                UnityEngine.JsonUtility.ToJson(request));
            Assert.IsTrue(restored.replayFrozenScenario);
            Assert.That(restored.frozenScenario, Is.Not.Null);
            Assert.AreEqual(1, restored.frozenScenario.poses.Count);
            Assert.AreEqual(4.5f, restored.frozenScenario.pathMm[0]);
            Assert.AreEqual(0.625f, restored.frozenScenario.referenceScores[0]);
            Assert.AreEqual(0, restored.frozenScenario.acceptableIndices[0]);
        }
    }
}
