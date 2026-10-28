using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class Phase2PocInteractiveTests
    {
        private const string ManifestPath =
            "Tools/ThoracicUSCoach/experiments/phase2_fixed_anatomy_engineering_poc_v1.json";

        [Test]
        public void PolicyArguments_UseFrozenDiagnosticInputsWithoutOracleFields()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            U08P1Manifest manifest = JsonUtility.FromJson<U08P1Manifest>(
                File.ReadAllText(Path.Combine(projectRoot, ManifestPath)));

            string[] arguments = ThoracicUSCoachController.BuildPhase2PocPolicyArguments(
                projectRoot, manifest);
            string joined = string.Join(" ", arguments);

            CollectionAssert.Contains(arguments, "--diagnostic-template-checkpoint");
            CollectionAssert.Contains(arguments, "--phase2-action-checkpoint");
            CollectionAssert.Contains(arguments, "--phase-two-only-pilot");
            CollectionAssert.Contains(arguments, "--phase2-exploration-limit");
            StringAssert.Contains("phase2_fixed_anatomy_template_demo_v1.json", joined);
            StringAssert.DoesNotContain("target-score", joined);
            StringAssert.DoesNotContain("target-position", joined);
            StringAssert.DoesNotContain("anatomy-label", joined);
            Assert.AreEqual("8", arguments[System.Array.IndexOf(
                arguments, "--phase2-exploration-limit") + 1]);
        }

        [Test]
        public void Scene_EnablesDiagnosticPanelWithoutDeveloperOrGuidedOverlay()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string scene = File.ReadAllText(Path.Combine(projectRoot,
                "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity"));

            StringAssert.Contains("showPhase2PocDiagnostic: 1", scene);
            StringAssert.Contains("showDeveloperOverlay: 0", scene);
            StringAssert.Contains("showGuided2DOverlay: 0", scene);
        }

        [Test]
        public void InteractiveController_DoesNotInvokeBatchEntryPoint()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string source = File.ReadAllText(Path.Combine(projectRoot,
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Phase2PocDiagnostic.cs"));

            StringAssert.DoesNotContain("ThoracicU08P1Batch", source);
            StringAssert.Contains("RunActive2DImageOnlyCore", source);
            StringAssert.Contains("StartPhase2PocOwnedPolicy", source);
        }
    }
}
