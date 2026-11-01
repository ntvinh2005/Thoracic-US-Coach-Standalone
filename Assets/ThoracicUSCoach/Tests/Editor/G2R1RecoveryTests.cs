using System.IO;
using NUnit.Framework;

namespace CSSALT.ThoracicUSCoach.Tests.Editor
{
    public sealed class G2R1RecoveryTests
    {
        private const string RuntimePath =
            "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R1Observability.cs";
        private const string AnatomyBatchPath =
            "Assets/ThoracicUSCoach/Editor/ThoracicG2R1AnatomyBatch.cs";

        [Test]
        public void DiagnosticIsExplicitlyEvaluatorOnly()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("PROHIBITED_EVALUATOR_GUIDED_DIAGNOSTIC_ONLY", source);
            StringAssert.Contains("LF_ASSOCIATED_POSTERIOR_RECESS_REGION", source);
            StringAssert.DoesNotContain("targetPose", source);
            StringAssert.DoesNotContain("distanceToT7", source);
            StringAssert.DoesNotContain("targetScore", source);
        }

        [Test]
        public void DiagnosticUsesLfGeometryInsteadOfLabelContactCenters()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("lfRenderer.bounds.center", source);
            StringAssert.DoesNotContain("reference.contactPoint +", source);
            StringAssert.Contains("FindSurfaceAt(lfRenderer.bounds.center", source);
        }

        [Test]
        public void DiagnosticRetainsContactWorkspaceAndFreshnessChecks()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.Contains("MeasureSweepCountContact()", source);
            StringAssert.Contains("IsActive2DWorkspaceValid(faceCenter.position)", source);
            StringAssert.Contains("CaptureDirectProducerFreshFrame", source);
            StringAssert.Contains("TryGetPublishedScanProvenance", source);
            StringAssert.Contains("provenanceMatched", source);
        }

        [Test]
        public void DiagnosticDoesNotMutateSceneOrRendererConfiguration()
        {
            string source = File.ReadAllText(RuntimePath);
            StringAssert.DoesNotContain("ShowOnUltrasound = false", source);
            StringAssert.DoesNotContain("Destroy(", source);
            StringAssert.DoesNotContain("SetActive(", source);
            StringAssert.DoesNotContain("AssetDatabase.SaveAssets", source);
        }

        [Test]
        public void TopologyExporterDoesNotAssignComponentsToTLevels()
        {
            string source = File.ReadAllText(AnatomyBatchPath);
            StringAssert.Contains("proximity alone does not establish vertebral identity", source);
            StringAssert.Contains("anatomicalIdentity = \"UNRESOLVED\"", source);
            StringAssert.Contains("Connected components are geometric facts only", source);
        }
    }
}
