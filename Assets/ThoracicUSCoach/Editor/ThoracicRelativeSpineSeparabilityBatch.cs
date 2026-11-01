using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicRelativeSpineSeparabilityBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.RelativeSpineSeparability.Active";
        private const string StartedKey = "ThoracicUSCoach.RelativeSpineSeparability.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.RelativeSpineSeparability.StartedUtcTicks";
        private static bool exiting;

        static ThoracicRelativeSpineSeparabilityBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void RunE4Cli()
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
                Exit(3, "Relative-spine E4 exceeded the 20-minute timeout");
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
                Exit(4, "Relative-spine controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartRelativeSpineE4Separability())
                    Exit(5, "Relative-spine controller refused to start E4");
                return;
            }
            if (!controller.RelativeSpineAuditComplete) return;
            if (!string.IsNullOrEmpty(controller.RelativeSpineAuditFailure))
            {
                Exit(6, controller.RelativeSpineAuditFailure);
                return;
            }
            Exit(0, "Relative-spine E4 completed with status "
                + controller.RelativeSpineAuditStatus + "; output="
                + controller.RelativeSpineAuditOutputRoot);
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks))
                return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromMinutes(20);
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][RelativeSpineSeparability] " + message);
            else Debug.LogError("[ThoracicUSCoach][RelativeSpineSeparability] " + message);
            EditorApplication.Exit(code);
        }
    }
}
