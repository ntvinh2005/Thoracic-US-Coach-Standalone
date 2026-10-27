using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicP3StageBFinalBatch
    {
        private const string Active = "ThoracicUSCoach.P3StageBFinal.Active";
        private const string Started = "ThoracicUSCoach.P3StageBFinal.Started";
        private const string Manifest = "ThoracicUSCoach.P3StageBFinal.Manifest";
        private const string StartedAt = "ThoracicUSCoach.P3StageBFinal.StartedAt";
        private const float MaximumBatchSeconds = 1800f;
        private static bool exiting;

        static ThoracicP3StageBFinalBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("P3 Stage B-Final requires batch mode");
            string manifest = Argument("-p3StageBFinalManifest");
            if (string.IsNullOrWhiteSpace(manifest))
                throw new ArgumentException("Missing -p3StageBFinalManifest");
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
            if (EditorApplication.timeSinceStartup - SessionState.GetFloat(StartedAt, 0f)
                > MaximumBatchSeconds)
            {
                Exit(8, "P3 Stage B-Final batch watchdog exceeded 1800 seconds");
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
                Exit(4, "controller missing");
                return;
            }
            if (!SessionState.GetBool(Started, false))
            {
                SessionState.SetBool(Started, true);
                if (!controller.StartP3StageBFinal(SessionState.GetString(Manifest, string.Empty)))
                    Exit(5, controller.P3StageBFailure);
                return;
            }
            if (!controller.P3StageBComplete) return;
            Exit(string.IsNullOrEmpty(controller.P3StageBFailure) ? 0 : 6, controller.P3StageBFailure);
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][P3StageB] capture complete");
            else Debug.LogError("[ThoracicUSCoach][P3StageB] " + message);
            EditorApplication.Exit(code);
        }
    }
}
