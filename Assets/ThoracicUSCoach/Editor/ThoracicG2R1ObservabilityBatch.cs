using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicG2R1ObservabilityBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.G2R1Observability.Active";
        private const string StartedKey = "ThoracicUSCoach.G2R1Observability.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.G2R1Observability.StartedUtcTicks";
        private static bool exiting;

        static ThoracicG2R1ObservabilityBatch()
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
                Exit(3, "G2-R1 observability exceeded the 20-minute timeout");
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
                Exit(4, "G2-R1 controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartG2R1ObservabilityDiagnostic())
                    Exit(5, "G2-R1 controller refused to start observability diagnostic");
                return;
            }
            if (!controller.RelativeSpineAuditComplete) return;
            if (!string.IsNullOrEmpty(controller.RelativeSpineAuditFailure))
            {
                Exit(6, controller.RelativeSpineAuditFailure);
                return;
            }
            Exit(0, "G2-R1 observability completed with status "
                + controller.RelativeSpineAuditStatus);
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2R1] " + message);
            else Debug.LogError("[ThoracicUSCoach][G2R1] " + message);
            EditorApplication.Exit(code);
        }
    }
}
