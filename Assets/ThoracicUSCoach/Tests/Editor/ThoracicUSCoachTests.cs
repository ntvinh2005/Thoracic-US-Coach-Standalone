using NUnit.Framework;

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
    }
}
