using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicG2R5TargetedBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.G2R5Targeted.Active";
        private const string StartedKey = "ThoracicUSCoach.G2R5Targeted.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.G2R5Targeted.StartedUtcTicks";
        private const string ProtocolPath =
            "Artifacts/ThoracicUSCoach/sweep_count/"
            + "G2_R5_TARGETED_MULTIVIEW_REACQUISITION_20261101_R01/"
            + "actor_selected_revisit_windows.json";
        private static bool exiting;

        static ThoracicG2R5TargetedBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void RunCli()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(StartedKey, false);
            SessionState.SetString(StartedUtcKey, DateTime.UtcNow.Ticks.ToString());
            EditorSceneManager.OpenScene(ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Update()
        {
            if (!SessionState.GetBool(ActiveKey, false) || exiting) return;
            if (TimedOut())
            {
                Exit(3, "G2-R5 targeted session exceeded the 30-minute timeout");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = true;
                return;
            }
            ThoracicUSCoachController controller = UnityEngine.Object
                .FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null)
            {
                Exit(4, "G2-R5 controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                ThoracicUSCoachController.ConfigureG2R5TargetedExperiment(ProtocolPath);
                if (!controller.StartG2R5TargetedReacquisition())
                    Exit(5, "G2-R5 controller refused to start");
                return;
            }
            if (!controller.G2R5Complete) return;
            if (!string.IsNullOrEmpty(controller.G2R5Failure))
            {
                Exit(6, controller.G2R5Failure);
                return;
            }
            Exit(0, "G2-R5 targeted diagnostic session completed with status "
                + controller.G2R5Status);
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks))
                return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromMinutes(30);
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            ThoracicUSCoachController.ResetG2R5TargetedExperimentConfiguration();
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2R5] " + message);
            else Debug.LogError("[ThoracicUSCoach][G2R5] " + message);
            EditorApplication.Exit(code);
        }
    }
}
