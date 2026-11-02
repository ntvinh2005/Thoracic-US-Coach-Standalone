using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicG2R6CrossPlaneBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.G2R6CrossPlane.Active";
        private const string StartedKey = "ThoracicUSCoach.G2R6CrossPlane.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.G2R6CrossPlane.StartedUtcTicks";
        private const string ProtocolPath =
            "Artifacts/ThoracicUSCoach/sweep_count/"
            + "G2_R6_CROSS_PLANE_DISCRIMINATION_20261101_R01/"
            + "actor_selected_regions.json";
        private static bool exiting;

        static ThoracicG2R6CrossPlaneBatch()
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
                Exit(3, "G2-R6 cross-plane session exceeded the 30-minute timeout");
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
                Exit(4, "G2-R6 controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                ThoracicUSCoachController.ConfigureG2R5TargetedExperiment(ProtocolPath);
                if (!controller.StartG2R5TargetedReacquisition())
                    Exit(5, "G2-R6 controller refused to start");
                return;
            }
            if (!controller.G2R5Complete) return;
            if (!string.IsNullOrEmpty(controller.G2R5Failure))
            {
                Exit(6, controller.G2R5Failure);
                return;
            }
            Exit(0, "G2-R6 cross-plane diagnostic session completed with status "
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2R6] " + message);
            else Debug.LogError("[ThoracicUSCoach][G2R6] " + message);
            EditorApplication.Exit(code);
        }
    }
}
