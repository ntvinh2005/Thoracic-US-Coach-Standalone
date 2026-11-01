using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class G2R3TopologyConfirmationTests
    {
        private const string RuntimePath =
            "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R2ContinuousSweep.cs";
        private const string BatchPath =
            "Assets/ThoracicUSCoach/Editor/ThoracicG2R3ConfirmationBatch.cs";

        [TearDown]
        public void ResetConfiguration()
        {
            ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
        }

        [Test]
        public void ExperimentConfigurationDefaultsRemainG2R2Compatible()
        {
            ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
            Assert.That(ReadStaticProperty<string>("G2R2ResolvedOutputRoot"),
                Does.Contain("G2_R2_CONTINUOUS_RECESS_MAPPING_20261101_DEV03"));
            Assert.That(ReadStaticProperty<float>("G2R2ResolvedSurfaceYawDeg"), Is.EqualTo(90f));
        }

        [Test]
        public void ConfirmationConfigurationIsIsolatedAndResettable()
        {
            const string output = "Artifacts/ThoracicUSCoach/sweep_count/test_g2_r3";
            ThoracicUSCoachController.ConfigureG2R2ContinuousExperiment(output, 91f);
            Assert.That(ReadStaticProperty<string>("G2R2ResolvedOutputRoot"), Is.EqualTo(output));
            Assert.That(ReadStaticProperty<float>("G2R2ResolvedSurfaceYawDeg"), Is.EqualTo(91f));
            ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
            Assert.That(ReadStaticProperty<float>("G2R2ResolvedSurfaceYawDeg"), Is.EqualTo(90f));
        }

        [Test]
        public void ConfirmationBatchDeclaresOnlyRoboticAcquisitionPerturbation()
        {
            string source = File.ReadAllText(BatchPath);
            StringAssert.Contains("SurfaceYawDeg = 91f", source);
            StringAssert.Contains("RunCliConfigured", source);
            StringAssert.DoesNotContain("T7", source);
            StringAssert.DoesNotContain("LF T", source);
            StringAssert.DoesNotContain("targetPose", source);
        }

        [Test]
        public void RuntimeOverrideDoesNotChangeMovementOrFreshnessContracts()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("G2R2StepMm = 1.5f", source);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", source);
            StringAssert.Contains("TryActive2DMove", source);
            StringAssert.Contains("ResetG2R2ContinuousExperimentConfiguration", source);
        }

        private static T ReadStaticProperty<T>(string name)
        {
            PropertyInfo property = typeof(ThoracicUSCoachController).GetProperty(
                name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return (T)property.GetValue(null);
        }
    }
}
