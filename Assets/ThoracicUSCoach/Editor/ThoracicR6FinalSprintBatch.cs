using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicR6FinalSprintBatch
    {
        private const string Active = "ThoracicUSCoach.R6.Active";
        private const string Started = "ThoracicUSCoach.R6.Started";
        private const string Manifest = "ThoracicUSCoach.R6.Manifest";
        private static bool exiting;

        static ThoracicR6FinalSprintBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("R6 requires Unity batch mode");
            string manifest = Argument("-r6Manifest");
            if (string.IsNullOrWhiteSpace(manifest)) throw new ArgumentException("Missing -r6Manifest");
            SessionState.SetBool(Active, true);
            SessionState.SetBool(Started, false);
            SessionState.SetString(Manifest, manifest);
            EditorSceneManager.OpenScene(ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Update()
        {
            if (!SessionState.GetBool(Active, false) || exiting) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
            if (!EditorApplication.isPlaying) { EditorApplication.isPlaying = true; return; }
            ThoracicUSCoachController controller = UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null) { Exit(4, "controller missing"); return; }
            if (!SessionState.GetBool(Started, false))
            {
                SessionState.SetBool(Started, true);
                if (!controller.StartR6FinalSprint(SessionState.GetString(Manifest, string.Empty)))
                    Exit(5, controller.R6FinalSprintFailure);
                return;
            }
            if (!controller.R6FinalSprintComplete) return;
            Exit(string.IsNullOrEmpty(controller.R6FinalSprintFailure) ? 0 : 6, controller.R6FinalSprintFailure);
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][R6] capture complete");
            else Debug.LogError("[ThoracicUSCoach][R6] " + message);
            EditorApplication.Exit(code);
        }
    }
}
