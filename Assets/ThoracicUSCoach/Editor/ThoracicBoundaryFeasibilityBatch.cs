using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicBoundaryFeasibilityBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.M0B.Active";
        private const string StartedKey = "ThoracicUSCoach.M0B.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.M0B.StartedUtcTicks";
        private static bool exiting;

        static ThoracicBoundaryFeasibilityBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void AuditGeometry()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Boundary geometry audit requires Unity -batchmode");
            try
            {
                EditorSceneManager.OpenScene(
                    ThoracicUSCoachSetup.DevelopmentScene, OpenSceneMode.Single);
                ThoracicUSCoachController controller = UnityEngine.Object
                    .FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
                if (controller == null)
                    throw new InvalidOperationException("Boundary controller is missing from the development scene");
                Physics.SyncTransforms();
                string output = controller.AuditBoundaryFeasibilityGeometry();
                Debug.Log("[ThoracicUSCoach][BoundaryM0] Geometry audit complete: " + output);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(2);
            }
        }

        public static void RunM0B()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("M0-B runner requires Unity -batchmode");
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
                Exit(3, "M0-B exceeded the 30-minute infrastructure timeout");
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
                Exit(4, "M0-B controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartM0BVisualBoundaryPilot())
                    Exit(5, "M0-B controller refused to start");
                return;
            }
            if (!controller.M0BVisualBoundaryComplete) return;
            if (!string.IsNullOrEmpty(controller.M0BVisualBoundaryInfrastructureFailure))
            {
                Exit(6, controller.M0BVisualBoundaryInfrastructureFailure);
                return;
            }
            Exit(0, "M0-B pilot completed with status "
                + controller.M0BVisualBoundaryStatus + "; output="
                + controller.M0BVisualBoundaryOutputRoot);
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks))
                return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)
                > TimeSpan.FromMinutes(30);
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][M0B] " + message);
            else Debug.LogError("[ThoracicUSCoach][M0B] " + message);
            EditorApplication.Exit(code);
        }
    }
}
