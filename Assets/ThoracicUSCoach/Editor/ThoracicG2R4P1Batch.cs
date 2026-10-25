using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicG2R4P1Batch
    {
        private const string Active = "ThoracicUSCoach.G2R4P1.Active";
        private const string Started = "ThoracicUSCoach.G2R4P1.Started";
        private const string Manifest = "ThoracicUSCoach.G2R4P1.Manifest";
        private static bool exiting;

        static ThoracicG2R4P1Batch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("G2-R4 P1 requires batch mode");
            string manifest = Value("-g2R4P1Manifest");
            if (string.IsNullOrEmpty(manifest)) throw new ArgumentException("Missing -g2R4P1Manifest");
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
                if (!controller.StartG2R4P1(SessionState.GetString(Manifest, string.Empty)))
                    Exit(5, controller.G2R4P1Failure);
                return;
            }
            if (!controller.G2R4P1Complete) return;
            Exit(string.IsNullOrEmpty(controller.G2R4P1Failure) ? 0 : 6, controller.G2R4P1Failure);
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2-R4-P1] capture complete");
            else Debug.LogError("[ThoracicUSCoach][G2-R4-P1] " + message);
            EditorApplication.Exit(code);
        }
    }
}
