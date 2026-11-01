using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicG2R2ContinuousBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.G2R2Continuous.Active";
        private const string StartedKey = "ThoracicUSCoach.G2R2Continuous.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.G2R2Continuous.StartedUtcTicks";
        private static bool exiting;

        static ThoracicG2R2ContinuousBatch()
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
                Exit(3, "G2-R2 continuous sweep exceeded the 30-minute timeout");
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
                Exit(4, "G2-R2 controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartG2R2ContinuousSweep())
                    Exit(5, "G2-R2 controller refused to start the continuous sweep");
                return;
            }
            if (!controller.G2R2ContinuousComplete) return;
            if (!string.IsNullOrEmpty(controller.G2R2ContinuousFailure))
            {
                Exit(6, controller.G2R2ContinuousFailure);
                return;
            }
            Exit(0, "G2-R2 continuous sweep completed with status "
                + controller.G2R2ContinuousStatus);
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2R2] " + message);
            else Debug.LogError("[ThoracicUSCoach][G2R2] " + message);
            EditorApplication.Exit(code);
        }
    }
}


