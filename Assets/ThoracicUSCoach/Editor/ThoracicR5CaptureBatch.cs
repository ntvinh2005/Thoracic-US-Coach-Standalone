using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicR5CaptureBatch
    {
        private const string Active = "ThoracicUSCoach.R5Capture.Active";
        private const string Started = "ThoracicUSCoach.R5Capture.Started";
        private const string Manifest = "ThoracicUSCoach.R5Capture.Manifest";
        private static bool exiting;

        static ThoracicR5CaptureBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("G2-R5 capture requires batch mode");
            string manifest = Value("-r5Manifest");
            if (string.IsNullOrEmpty(manifest)) throw new ArgumentException("Missing -r5Manifest");
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
                if (!controller.StartR5Capture(SessionState.GetString(Manifest, string.Empty)))
                    Exit(5, controller.R5CaptureFailure);
                return;
            }
            if (!controller.R5CaptureComplete) return;
            Exit(string.IsNullOrEmpty(controller.R5CaptureFailure) ? 0 : 6, controller.R5CaptureFailure);
        }

        private static string Value(string name)
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2-R5] capture complete");
            else Debug.LogError("[ThoracicUSCoach][G2-R5] " + message);
            EditorApplication.Exit(code);
        }
    }
}
