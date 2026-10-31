using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicPositionGuidedT7R3Batch
    {
        private const string ActiveKey = "ThoracicUSCoach.P1R3.Active";
        private const string ConfirmationKey = "ThoracicUSCoach.P1R3.Confirmation";
        private const string StartedKey = "ThoracicUSCoach.P1R3.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.P1R3.StartedUtcTicks";
        private static bool exiting;

        static ThoracicPositionGuidedT7R3Batch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void RunNextDevelopment()
        {
            Start(false);
        }

        public static void RunNextConfirmation()
        {
            Start(true);
        }

        private static void Start(bool confirmation)
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("P1-R3 runner requires Unity -batchmode");
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(ConfirmationKey, confirmation);
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
                Exit(3, "P1-R3 run exceeded the 20-minute timeout");
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
                Exit(4, "P1-R3 controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                bool confirmation = SessionState.GetBool(ConfirmationKey, false);
                bool started = confirmation
                    ? controller.StartP1R3ConfirmationRun()
                    : controller.StartP1R3DevelopmentRun();
                if (!started) Exit(5, "P1-R3 controller refused to start");
                return;
            }
            if (!controller.P1R3Complete) return;
            if (!string.IsNullOrEmpty(controller.P1R3Failure))
            {
                Exit(6, controller.P1R3Failure);
                return;
            }
            Exit(0, "P1-R3 completed with status " + controller.P1R3Status
                + "; output=" + controller.P1R3OutputRoot);
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
            SessionState.EraseBool(ConfirmationKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][P1R3] " + message);
            else Debug.LogError("[ThoracicUSCoach][P1R3] " + message);
            EditorApplication.Exit(code);
        }
    }
}
