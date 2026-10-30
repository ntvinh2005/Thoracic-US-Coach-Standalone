using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Globalization;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicSweepCountBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.SweepCount.Active";
        private const string StartedKey = "ThoracicUSCoach.SweepCount.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.SweepCount.StartedUtcTicks";
        private static bool exiting;

        static ThoracicSweepCountBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Sweep-count runner requires Unity -batchmode");
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
                Exit(3, "Sweep-count exceeded the 30-minute infrastructure timeout");
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
                Exit(4, "Sweep-count controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                float yaw = CommandLineFloat("-sweepYawDeg", 0f);
                if (!controller.StartSweepCountMilestone1(yaw))
                    Exit(5, "Sweep-count controller refused to start");
                return;
            }
            if (!controller.SweepCountMilestoneComplete) return;
            if (!string.IsNullOrEmpty(controller.SweepCountInfrastructureFailure))
            {
                Exit(6, controller.SweepCountInfrastructureFailure);
                return;
            }
            Exit(0, "Sweep-count runtime completed with scientific status "
                + controller.SweepCountScientificStatus + "; output=" + controller.SweepCountOutputRoot);
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks)) return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromMinutes(30);
        }

        private static float CommandLineFloat(string name, float fallback)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(arguments[index + 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float value))
                    return value;
            }
            return fallback;
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][SweepCount] " + message);
            else Debug.LogError("[ThoracicUSCoach][SweepCount] " + message);
            EditorApplication.Exit(code);
        }
    }
}
