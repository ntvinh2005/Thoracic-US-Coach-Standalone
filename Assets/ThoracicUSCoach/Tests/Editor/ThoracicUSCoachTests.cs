using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

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
        public void DiverseSchedule_IsReproducibleAndSplitBeforeCapture()
        {
            Assert.AreEqual(DiverseTrajectorySchedule.SeedForSequence(7), DiverseTrajectorySchedule.SeedForSequence(7));
            Assert.AreNotEqual(DiverseTrajectorySchedule.SeedForSequence(7), DiverseTrajectorySchedule.SeedForSequence(8));
            Assert.AreEqual("train", DiverseTrajectorySchedule.SplitForSequence(11));
            Assert.AreEqual("dev", DiverseTrajectorySchedule.SplitForSequence(12));
            Assert.AreEqual("test", DiverseTrajectorySchedule.SplitForSequence(15));
            Assert.AreEqual("stress", DiverseTrajectorySchedule.EvaluationGroupForSequence(18));
        }

        [Test]
        public void DiverseSchedule_DoesNotShareConfigurationFamiliesAcrossSplits()
        {
            for (int left = 0; left < 21; left++)
            for (int right = 0; right < 21; right++)
            {
                if (DiverseTrajectorySchedule.SplitForSequence(left) == DiverseTrajectorySchedule.SplitForSequence(right)) continue;
                Assert.AreNotEqual(DiverseTrajectorySchedule.FamilyForSequence(left), DiverseTrajectorySchedule.FamilyForSequence(right));
            }
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
    }
}
