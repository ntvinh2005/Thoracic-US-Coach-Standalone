using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicU08P1Batch
    {
        private const string ActiveKey = "ThoracicUSCoach.U08P1.Active";
        private const string StartedKey = "ThoracicUSCoach.U08P1.Started";
        private const string ManifestKey = "ThoracicUSCoach.U08P1.Manifest";
        private const string PolicyKey = "ThoracicUSCoach.U08P1.Policy";
        private const string StartedUtcKey = "ThoracicUSCoach.U08P1.StartedUtcTicks";
        private static bool exiting;

        static ThoracicU08P1Batch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("U08-P1 runner is evaluator-only and requires Unity -batchmode");
            string manifest = CommandLineValue("-u08P1Manifest");
            string policy = CommandLineValue("-u08P1Policy").ToLowerInvariant();
            if (string.IsNullOrEmpty(manifest)) throw new ArgumentException("Missing -u08P1Manifest");
            if (policy != "raster" && policy != "candidate")
                throw new ArgumentException("-u08P1Policy must be raster or candidate");
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(StartedKey, false);
            SessionState.SetString(ManifestKey, manifest);
            SessionState.SetString(PolicyKey, policy);
            SessionState.SetString(StartedUtcKey, DateTime.UtcNow.Ticks.ToString());
            EditorSceneManager.OpenScene(ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Update()
        {
            if (!SessionState.GetBool(ActiveKey, false) || exiting) return;
            if (TimedOut()) { Exit(3, "U08-P1 exceeded the six-hour infrastructure timeout"); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
            if (!EditorApplication.isPlaying) { EditorApplication.isPlaying = true; return; }
            ThoracicUSCoachController controller = UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null) { Exit(4, "U08-P1 controller is missing from the development scene"); return; }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartU08P1Evaluation(
                    SessionState.GetString(ManifestKey, string.Empty),
                    SessionState.GetString(PolicyKey, string.Empty)))
                    Exit(5, "U08-P1 controller rejected the manifest: " + controller.U08P1InfrastructureFailure);
                return;
            }
            if (!controller.U08P1EvaluationComplete) return;
            if (!string.IsNullOrEmpty(controller.U08P1InfrastructureFailure))
            { Exit(6, controller.U08P1InfrastructureFailure); return; }
            Exit(0, "U08-P1 completed exactly two starts for " + SessionState.GetString(PolicyKey, string.Empty));
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks)) return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(6);
        }

        private static string CommandLineValue(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < arguments.Length; i++)
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase)) return arguments[i + 1];
            return string.Empty;
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(ManifestKey);
            SessionState.EraseString(PolicyKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][U08-P1] " + message);
            else Debug.LogError("[ThoracicUSCoach][U08-P1] " + message);
            EditorApplication.Exit(code);
        }
    }
}
