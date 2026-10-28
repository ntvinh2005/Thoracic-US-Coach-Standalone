using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    public static class ThoracicPhase2PocInteractive
    {
        private const string VerificationActiveKey = "ThoracicUSCoach.Phase2PocVerification.Active";
        private const string VerificationStartedKey = "ThoracicUSCoach.Phase2PocVerification.Started";
        private const string VerificationCaseKey = "ThoracicUSCoach.Phase2PocVerification.Case";
        private const string VerificationPythonKey = "ThoracicUSCoach.Phase2PocVerification.Python";
        private const string VerificationStartUtcKey = "ThoracicUSCoach.Phase2PocVerification.StartUtc";
        private const string VerificationResultsKey = "ThoracicUSCoach.Phase2PocVerification.Results";
        private const string VerificationOutputKey = "ThoracicUSCoach.Phase2PocVerification.Output";
        private const string DiagnosticCheckpoint =
            "Artifacts/ThoracicUSCoach/active2d/checkpoints/phase2_fixed_anatomy_template_demo_v1.json";
        private static bool exiting;

        [Serializable]
        private sealed class VerificationEpisode
        {
            public string caseName;
            public bool targetEncountered;
            public bool recognized;
            public float maximumRecognitionScore;
            public bool returnedToBest;
            public bool freshConfirmation;
            public bool explicitStop;
            public bool wrongStop;
            public bool abstained;
            public int steps;
            public int acceptedMovements;
            public int completedMovements;
            public int contactViolations;
            public int workspaceViolations;
            public string failureReason;
            public string outputDirectory;
            public bool policyStoppedAfterEpisode;
        }

        [Serializable]
        private sealed class VerificationReport
        {
            public int schemaVersion = 1;
            public string generatedUtc;
            public string unityVersion;
            public string scene;
            public string checkpoint = DiagnosticCheckpoint;
            public string warning = "FIXED-ANATOMY DIAGNOSTIC ONLY. NOT CLINICALLY VALIDATED.";
            public bool panelVisible;
            public bool resetCleanupPassed;
            public bool success;
            public string failureReason;
            public List<VerificationEpisode> episodes = new List<VerificationEpisode>();
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= UpdateVerification;
            EditorApplication.update += UpdateVerification;
        }

        [MenuItem("Tools/Thoracic US Coach/Open Phase 2 POC Demo", priority = 2)]
        public static void OpenDemo()
        {
            if (EditorApplication.isPlaying)
            {
                ThoracicUSCoachController playing = FindController();
                if (playing != null) Selection.activeGameObject = playing.gameObject;
                Debug.Log("[ThoracicUSCoach][Phase2POC] The diagnostic panel is visible in Game view.");
                return;
            }
            if (!File.Exists(ThoracicUSCoachSetup.DevelopmentScene))
            {
                Debug.LogError("[ThoracicUSCoach][Phase2POC] Scene is missing: "
                    + ThoracicUSCoachSetup.DevelopmentScene);
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            ThoracicUSCoachController controller = FindController();
            if (controller == null)
            {
                Debug.LogError("[ThoracicUSCoach][Phase2POC] Controller is missing from the POC scene.");
                return;
            }
            Selection.activeGameObject = controller.gameObject;
            Debug.Log("[ThoracicUSCoach][Phase2POC] Scene opened. Enter Play Mode and use the visible panel.", controller);
        }

        // Headless verification invokes the same public entry point as the panel's Start button.
        public static void VerifyInteractiveWorkflow()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Interactive workflow verification requires Unity -batchmode.");
            string python = CommandLineValue("-phase2PocPython");
            if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
                throw new ArgumentException("Missing or invalid -phase2PocPython executable.");
            string output = CommandLineValue("-phase2PocVerificationOutput");
            if (string.IsNullOrWhiteSpace(output))
                output = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                    "Artifacts", "ThoracicUSCoach", "active2d", "evaluation",
                    "phase2_interactive_poc_verification.json");
            VerificationReport report = new VerificationReport
            {
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                scene = ThoracicUSCoachSetup.DevelopmentScene
            };
            SessionState.SetBool(VerificationActiveKey, true);
            SessionState.SetBool(VerificationStartedKey, false);
            SessionState.SetInt(VerificationCaseKey, 0);
            SessionState.SetString(VerificationPythonKey, python);
            SessionState.SetString(VerificationStartUtcKey,
                DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            SessionState.SetString(VerificationResultsKey, JsonUtility.ToJson(report));
            SessionState.SetString(VerificationOutputKey, Path.GetFullPath(output));
            EditorSceneManager.OpenScene(ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void UpdateVerification()
        {
            if (!SessionState.GetBool(VerificationActiveKey, false) || exiting) return;
            if (VerificationTimedOut())
            {
                FinishVerification(3, "Interactive POC verification exceeded its 20-minute timeout.");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = true;
                return;
            }
            ThoracicUSCoachController controller = FindController();
            if (controller == null)
            {
                FinishVerification(4, "Controller is missing from the POC scene.");
                return;
            }
            VerificationReport report = ReadReport();
            report.panelVisible = controller.Phase2PocPanelVisible;
            if (!report.panelVisible)
            {
                SaveSessionReport(report);
                FinishVerification(5, "Phase 2 POC diagnostic panel is disabled.");
                return;
            }
            int caseIndex = SessionState.GetInt(VerificationCaseKey, 0);
            if (!SessionState.GetBool(VerificationStartedKey, false))
            {
                SessionState.SetBool(VerificationStartedKey, true);
                SaveSessionReport(report);
                if (!controller.StartPhase2PocInteractive(caseIndex,
                    SessionState.GetString(VerificationPythonKey, string.Empty)))
                {
                    FinishVerification(6, "Controller rejected case " + caseIndex
                        + ": " + controller.Phase2PocDisplayState);
                }
                return;
            }
            if (!controller.Phase2PocInteractiveComplete || controller.Phase2PocInteractiveRunning
                || controller.Phase2PocOwnedPolicyRunning) return;

            VerificationEpisode episode = BuildEpisode(caseIndex, controller);
            report.episodes.Add(episode);
            report.resetCleanupPassed &= !controller.Phase2PocOwnedPolicyRunning;
            if (report.episodes.Count == 1) report.resetCleanupPassed = true;
            SaveSessionReport(report);

            controller.ResetPhase2PocInteractive();
            if (controller.Phase2PocOwnedPolicyRunning)
            {
                report.resetCleanupPassed = false;
                SaveSessionReport(report);
                FinishVerification(7, "Reset did not stop the demo-owned Python process.");
                return;
            }
            caseIndex++;
            if (caseIndex < 3)
            {
                SessionState.SetInt(VerificationCaseKey, caseIndex);
                SessionState.SetBool(VerificationStartedKey, false);
                SaveSessionReport(report);
                return;
            }

            bool positivesPass = report.episodes.Take(2).All(item => item.targetEncountered
                && item.recognized && item.returnedToBest && item.freshConfirmation
                && item.explicitStop && !item.wrongStop && item.contactViolations == 0
                && item.workspaceViolations == 0);
            VerificationEpisode negative = report.episodes[2];
            bool negativePass = negative.abstained && !negative.explicitStop && !negative.wrongStop
                && negative.contactViolations == 0 && negative.workspaceViolations == 0;
            report.success = positivesPass && negativePass && report.panelVisible
                && report.resetCleanupPassed;
            report.failureReason = report.success ? string.Empty
                : "One or more bounded interactive POC expectations failed; inspect episode fields.";
            SaveSessionReport(report);
            FinishVerification(report.success ? 0 : 8,
                report.success ? "Interactive Phase 2 POC workflow passed all three consumed cases."
                    : report.failureReason);
        }

        private static VerificationEpisode BuildEpisode(
            int caseIndex, ThoracicUSCoachController controller)
        {
            U08P1EpisodeOutcome outcome = controller.Phase2PocLastOutcome;
            Active2DRunResult result = controller.Active2DResult;
            IEnumerable<Active2DRunTrace> traces = result != null
                ? result.trace : Enumerable.Empty<Active2DRunTrace>();
            return new VerificationEpisode
            {
                caseName = new[] { "Positive 01", "Positive 05", "Hard Negative 06" }[caseIndex],
                targetEncountered = outcome != null && outcome.targetObserved,
                recognized = outcome != null && outcome.recognized,
                maximumRecognitionScore = traces.Any() ? traces.Max(item => item.confidence) : 0f,
                returnedToBest = outcome != null && outcome.returnedToBest,
                freshConfirmation = outcome != null && outcome.freshConfirmation,
                explicitStop = outcome != null && outcome.explicitStop,
                wrongStop = outcome != null && outcome.wrongStop,
                abstained = outcome != null && outcome.abstained,
                steps = outcome != null ? outcome.phaseTwoSteps : 0,
                acceptedMovements = outcome != null ? outcome.acceptedMovements : 0,
                completedMovements = traces.Count(item => item.accepted
                    && item.executionFeedbackCompleted && item.action != "STOP"
                    && item.action != "PHASEHANDOFF"),
                contactViolations = outcome != null ? outcome.contactViolations : -1,
                workspaceViolations = outcome != null ? outcome.workspaceViolations : -1,
                failureReason = outcome != null ? outcome.failureReason : "Episode outcome missing.",
                outputDirectory = controller.Phase2PocLastOutputDirectory,
                policyStoppedAfterEpisode = !controller.Phase2PocOwnedPolicyRunning
            };
        }

        private static ThoracicUSCoachController FindController()
        {
            return UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>(
                FindObjectsInactive.Include);
        }

        private static VerificationReport ReadReport()
        {
            string json = SessionState.GetString(VerificationResultsKey, string.Empty);
            return string.IsNullOrEmpty(json)
                ? new VerificationReport() : JsonUtility.FromJson<VerificationReport>(json);
        }

        private static void SaveSessionReport(VerificationReport report)
        {
            string json = JsonUtility.ToJson(report, true);
            SessionState.SetString(VerificationResultsKey, json);
            string output = SessionState.GetString(VerificationOutputKey, string.Empty);
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, json);
        }

        private static bool VerificationTimedOut()
        {
            if (!long.TryParse(SessionState.GetString(VerificationStartUtcKey, string.Empty),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromMinutes(20);
        }

        private static string CommandLineValue(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return string.Empty;
        }

        private static void FinishVerification(int code, string message)
        {
            exiting = true;
            VerificationReport report = ReadReport();
            if (code != 0 && string.IsNullOrEmpty(report.failureReason)) report.failureReason = message;
            SaveSessionReport(report);
            SessionState.EraseBool(VerificationActiveKey);
            SessionState.EraseBool(VerificationStartedKey);
            SessionState.EraseInt(VerificationCaseKey);
            SessionState.EraseString(VerificationPythonKey);
            SessionState.EraseString(VerificationStartUtcKey);
            SessionState.EraseString(VerificationResultsKey);
            SessionState.EraseString(VerificationOutputKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][Phase2POC] " + message);
            else Debug.LogError("[ThoracicUSCoach][Phase2POC] " + message);
            EditorApplication.Exit(code);
        }
    }
}
