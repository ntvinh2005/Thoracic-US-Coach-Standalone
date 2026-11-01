using System.IO;
using System.Reflection;
using NUnit.Framework;
using SMMARTS;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class RelativeSpineAuditTests
    {
        [Test]
        public void FullInterfaceProvenanceIsReadOnlyMetadata()
        {
            FieldInfo field = typeof(UltrasoundManager.PublishedScanProvenanceBeam)
                .GetField("interfaces", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            UltrasoundManager.PublishedScanProvenanceBeam beam =
                new UltrasoundManager.PublishedScanProvenanceBeam();
            Assert.That(beam.interfaces, Is.Empty);
            UltrasoundManager.PublishedScanProvenanceInterface item =
                new UltrasoundManager.PublishedScanProvenanceInterface();
            Assert.That(item.depthFraction, Is.EqualTo(-1f));
            Assert.That(item.occludedByEarlierShadow, Is.False);
        }

        [Test]
        public void DiagnosticControllerDoesNotReferenceRuntimeT7TargetOrScore()
        {
            string path = Path.Combine(Application.dataPath, "ThoracicUSCoach", "Runtime",
                "ThoracicUSCoachController.RelativeSpineAudit.cs");
            string source = File.ReadAllText(path);
            StringAssert.DoesNotContain("t7RightSeed", source);
            StringAssert.DoesNotContain("ScoreCandidate", source);
            StringAssert.DoesNotContain("targetPose", source);
            StringAssert.DoesNotContain("distanceToT7", source);
        }

        [Test]
        public void DiagnosticArtifactsAreExplicitlyEvaluatorOnly()
        {
            string path = Path.Combine(Application.dataPath, "ThoracicUSCoach", "Runtime",
                "ThoracicUSCoachController.RelativeSpineAudit.cs");
            string source = File.ReadAllText(path);
            StringAssert.Contains("evaluator_only", source);
            StringAssert.Contains("PROHIBITED_DIAGNOSTIC_ONLY", source);
            StringAssert.Contains("RestoreRelativeSpineVisibility", source);
        }

        [Test]
        public void E2GeometryScreenUsesOnlyTargetedEvaluatorDiagnostics()
        {
            string path = Path.Combine(Application.dataPath, "ThoracicUSCoach", "Runtime",
                "ThoracicUSCoachController.RelativeSpineGeometryScreen.cs");
            string source = File.ReadAllText(path);
            StringAssert.Contains("PARAMEDIAN_LONGITUDINAL_P6", source);
            StringAssert.Contains("PARAMEDIAN_LONGITUDINAL_M6", source);
            StringAssert.Contains("PROHIBITED_EVALUATOR_GUIDED_DIAGNOSTIC_ONLY", source);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", source);
            StringAssert.DoesNotContain("t7RightSeed", source);
            StringAssert.DoesNotContain("ScoreCandidate", source);
            StringAssert.DoesNotContain("targetPose", source);
            StringAssert.DoesNotContain("distanceToT7", source);
        }

        [Test]
        public void E2GeometryScreenDoesNotModifyActive2DMovementContract()
        {
            string path = Path.Combine(Application.dataPath, "ThoracicUSCoach", "Runtime",
                "ThoracicUSCoachController.RelativeSpineGeometryScreen.cs");
            string source = File.ReadAllText(path);
            StringAssert.DoesNotContain("MinimumMeaningfulProgressMm", source);
            StringAssert.DoesNotContain("active2DStepMm =", source);
            StringAssert.DoesNotContain("active2DLongitudinalHalfExtentMm =", source);
            StringAssert.DoesNotContain("active2DLateralHalfExtentMm =", source);
        }

        [Test]
        public void E4SeparabilityIsEvaluatorOnlyAndDoesNotLeakRuntimeTarget()
        {
            string path = Path.Combine(Application.dataPath, "ThoracicUSCoach", "Runtime",
                "ThoracicUSCoachController.RelativeSpineSeparability.cs");
            string source = File.ReadAllText(path);
            StringAssert.Contains("PROHIBITED_EVALUATOR_GUIDED_DIAGNOSTIC_ONLY", source);
            StringAssert.Contains("INTERLEVEL_MIDPOINT", source);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", source);
            StringAssert.DoesNotContain("t7RightSeed", source);
            StringAssert.DoesNotContain("ScoreCandidate", source);
            StringAssert.DoesNotContain("targetPose", source);
            StringAssert.DoesNotContain("distanceToT7", source);
        }
    }
}
