using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicP2FreshnessBatch
    {
        private const string Active = "ThoracicUSCoach.P2Freshness.Active";
        private const string Started = "ThoracicUSCoach.P2Freshness.Started";
        private const string Manifest = "ThoracicUSCoach.P2Freshness.Manifest";
        private const string StartedAt = "ThoracicUSCoach.P2Freshness.StartedAt";
        private const float MaximumBatchSeconds = 1800f;
        private static bool exiting;

        static ThoracicP2FreshnessBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("P2 freshness requires batch mode");
            string manifest = Argument("-p2FreshnessManifest");
            if (string.IsNullOrWhiteSpace(manifest)) throw new ArgumentException("Missing -p2FreshnessManifest");
            SessionState.SetBool(Active, true);
            SessionState.SetBool(Started, false);
            SessionState.SetString(Manifest, manifest);
            SessionState.SetFloat(StartedAt, (float)EditorApplication.timeSinceStartup);
            EditorSceneManager.OpenScene(ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Update()
        {
            if (!SessionState.GetBool(Active, false) || exiting) return;
            if (EditorApplication.timeSinceStartup - SessionState.GetFloat(StartedAt, 0f) > MaximumBatchSeconds)
            {
                Exit(8, "P2 freshness batch watchdog exceeded 1800 seconds");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
            if (!EditorApplication.isPlaying) { EditorApplication.isPlaying = true; return; }
            ThoracicUSCoachController controller = UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>(
                FindObjectsInactive.Include);
            if (controller == null) { Exit(4, "controller missing"); return; }
            if (!SessionState.GetBool(Started, false))
            {
                SessionState.SetBool(Started, true);
                if (!controller.StartP2Freshness(SessionState.GetString(Manifest, string.Empty)))
                    Exit(5, controller.P2FreshnessFailure);
                return;
            }
            if (!controller.P2FreshnessComplete) return;
            Exit(string.IsNullOrEmpty(controller.P2FreshnessFailure) ? 0 : 6, controller.P2FreshnessFailure);
        }

        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < args.Length; index++)
                if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return string.Empty;
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(Active);
            SessionState.EraseBool(Started);
            SessionState.EraseString(Manifest);
            SessionState.EraseFloat(StartedAt);
            if (code == 0) Debug.Log("[ThoracicUSCoach][P2Freshness] capture complete");
            else Debug.LogError("[ThoracicUSCoach][P2Freshness] " + message);
            EditorApplication.Exit(code);
        }
    }
}
