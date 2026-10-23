using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicGate2Batch
    {
        private const string ActiveKey = "ThoracicUSCoach.Gate2.Active";
        private const string StartedKey = "ThoracicUSCoach.Gate2.Started";
        private const string ManifestKey = "ThoracicUSCoach.Gate2.Manifest";
        private const string StartedUtcKey = "ThoracicUSCoach.Gate2.StartedUtcTicks";
        private static bool exiting;

        static ThoracicGate2Batch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        // Usage: Unity -batchmode -projectPath <project> -executeMethod
        // CSSALT.ThoracicUSCoach.Editor.ThoracicGate2Batch.Run
        // -gate2Manifest Tools/ThoracicUSCoach/experiments/gate2_phase1_fresh_40_v1.json
        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Gate2 runner is evaluator-only and requires Unity -batchmode");
            string manifest = CommandLineValue("-gate2Manifest");
            if (string.IsNullOrEmpty(manifest))
                throw new ArgumentException("Missing -gate2Manifest argument");
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(StartedKey, false);
            SessionState.SetString(ManifestKey, manifest);
            SessionState.SetString(StartedUtcKey, DateTime.UtcNow.Ticks.ToString());
            EditorSceneManager.OpenScene(ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Update()
        {
            if (!SessionState.GetBool(ActiveKey, false) || exiting) return;
            if (TimedOut())
            {
                Exit(3, "Gate2 exceeded the six-hour infrastructure timeout");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = true;
                return;
            }
            ThoracicUSCoachController controller = UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>(
                FindObjectsInactive.Include);
            if (controller == null)
            {
                Exit(4, "Gate2 controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartGate2Evaluation(SessionState.GetString(ManifestKey, string.Empty)))
                {
                    Exit(5, "Gate2 controller rejected the manifest: " + controller.Gate2InfrastructureFailure);
                }
                return;
            }
            if (!controller.Gate2EvaluationComplete) return;
            if (!string.IsNullOrEmpty(controller.Gate2InfrastructureFailure))
            {
                Exit(6, controller.Gate2InfrastructureFailure);
                return;
            }
            Exit(0, "Gate2 completed all scheduled cases; scientific pass/fail is recorded in the output report");
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks)) return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(6);
        }

        private static string CommandLineValue(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return string.Empty;
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(ManifestKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][Gate2] " + message);
            else Debug.LogError("[ThoracicUSCoach][Gate2] " + message);
            EditorApplication.Exit(code);
        }
    }
}
