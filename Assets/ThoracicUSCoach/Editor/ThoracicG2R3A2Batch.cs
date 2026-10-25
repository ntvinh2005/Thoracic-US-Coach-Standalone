using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicG2R3A2Batch
    {
        private const string Active = "ThoracicUSCoach.G2R3A2.Active";
        private const string Started = "ThoracicUSCoach.G2R3A2.Started";
        private const string Manifest = "ThoracicUSCoach.G2R3A2.Manifest";
        private static bool exiting;

        static ThoracicG2R3A2Batch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("G2-R3-A2 requires batch mode");
            string manifest = Value("-g2R3A2Manifest");
            if (string.IsNullOrEmpty(manifest)) throw new ArgumentException("Missing -g2R3A2Manifest");
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
                if (!controller.StartG2R3A2(SessionState.GetString(Manifest, string.Empty)))
                    Exit(5, controller.G2R3A2Failure);
                return;
            }
            if (!controller.G2R3A2Complete) return;
            Exit(string.IsNullOrEmpty(controller.G2R3A2Failure) ? 0 : 6, controller.G2R3A2Failure);
        }

        private static string Value(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return string.Empty;
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(Active);
            SessionState.EraseBool(Started);
            SessionState.EraseString(Manifest);
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2-R3-A2] complete");
            else Debug.LogError("[ThoracicUSCoach][G2-R3-A2] " + message);
            EditorApplication.Exit(code);
        }
    }
}
