using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicPositionGuidedT7Batch
    {
        private const string ActiveKey = "ThoracicUSCoach.P1R2PositionGuided.Active";
        private const string StartedKey = "ThoracicUSCoach.P1R2PositionGuided.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.P1R2PositionGuided.StartedUtcTicks";
        private static bool exiting;

        static ThoracicPositionGuidedT7Batch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void RunPilot()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("P1-R2 position-guided runner requires Unity -batchmode");
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(StartedKey, false);
            SessionState.SetString(StartedUtcKey, DateTime.UtcNow.Ticks.ToString());
            EditorSceneManager.OpenScene(
                ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Update()
        {
            if (!SessionState.GetBool(ActiveKey, false) || exiting) return;
            if (TimedOut())
            {
                Exit(3, "P1-R2 position-guided pilot exceeded the 20-minute timeout");
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
                Exit(4, "P1-R2 controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartP1PositionGuidedPilot())
                    Exit(5, "P1-R2 controller refused to start or pilot output already exists");
                return;
            }
            if (!controller.P1PositionGuidedComplete) return;
            if (!string.IsNullOrEmpty(controller.P1PositionGuidedFailure))
            {
                Exit(6, controller.P1PositionGuidedFailure);
                return;
            }
            Exit(0, "P1-R2 pilot completed with status " + controller.P1PositionGuidedStatus
                + "; output=" + controller.P1PositionGuidedOutputRoot);
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks))
                return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)
                > TimeSpan.FromMinutes(20);
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][P1PositionGuided] " + message);
            else Debug.LogError("[ThoracicUSCoach][P1PositionGuided] " + message);
            EditorApplication.Exit(code);
        }
    }
}
