using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicRelativeSpineGeometryBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.RelativeSpineGeometry.Active";
        private const string StartedKey = "ThoracicUSCoach.RelativeSpineGeometry.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.RelativeSpineGeometry.StartedUtcTicks";
        private static bool exiting;

        static ThoracicRelativeSpineGeometryBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void RunE2()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Relative-spine E2 runner requires Unity -batchmode");
            BeginE2();
        }

        public static void RunE2Cli()
        {
            BeginE2();
        }

        private static void BeginE2()
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
                Exit(3, "Relative-spine E2 exceeded the 20-minute timeout");
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
                if (!controller.StartRelativeSpineE2GeometryScreen())
                    Exit(5, "Relative-spine controller refused to start E2");
                return;
            }
            if (!controller.RelativeSpineAuditComplete) return;
            if (!string.IsNullOrEmpty(controller.RelativeSpineAuditFailure))
            {
                Exit(6, controller.RelativeSpineAuditFailure);
                return;
            }
            Exit(0, "Relative-spine E2 completed with status "
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
            if (code == 0) Debug.Log("[ThoracicUSCoach][RelativeSpineGeometry] " + message);
            else Debug.LogError("[ThoracicUSCoach][RelativeSpineGeometry] " + message);
            EditorApplication.Exit(code);
        }
    }
}
