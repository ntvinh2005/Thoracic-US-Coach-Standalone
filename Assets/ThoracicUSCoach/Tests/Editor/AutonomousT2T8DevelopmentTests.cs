using NUnit.Framework;
using System.IO;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class AutonomousT2T8DevelopmentTests
    {
        [TearDown]
        public void TearDown()
        {
            ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
        }

        [Test]
        public void NarrowFootprintConfigurationIsIsolatedAndResettable()
        {
            ThoracicUSCoachController.ConfigureG2R2ContinuousExperiment(
                "temporary-output", 90f, 12, true);
            Assert.That(ThoracicUSCoachController.G2R2ResolvedScanTopWidthMm,
                Is.EqualTo(12));
            Assert.That(ThoracicUSCoachController.G2R2PhysicalFinalizationRequired,
                Is.True);
            ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
            Assert.That(ThoracicUSCoachController.G2R2ResolvedScanTopWidthMm, Is.Null);
            Assert.That(ThoracicUSCoachController.G2R2PhysicalFinalizationRequired,
                Is.False);
        }

        [Test]
        public void ConfirmationRunnerContainsNoAnatomicalTargetCoordinate()
        {
            string source = File.ReadAllText(
                "Assets/ThoracicUSCoach/Editor/ThoracicAutonomousT2T8ConfirmationBatch.cs");
            StringAssert.DoesNotContain("reference", source.ToLowerInvariant());
            StringAssert.DoesNotContain("42.977", source);
            StringAssert.DoesNotContain("BuildSweepCountReferences", source);
            StringAssert.Contains("89.5f", source);
            StringAssert.Contains("45, true", source);
        }
    }
}
