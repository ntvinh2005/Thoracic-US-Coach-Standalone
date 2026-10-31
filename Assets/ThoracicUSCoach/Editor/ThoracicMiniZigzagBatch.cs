using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicMiniZigzagBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.MiniZigzag.Active";
        private const string StartedKey = "ThoracicUSCoach.MiniZigzag.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.MiniZigzag.StartedUtcTicks";
        private static bool exiting;

        static ThoracicMiniZigzagBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Mini-zigzag runner requires Unity -batchmode");
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
                Exit(3, "Mini-zigzag exceeded the 30-minute infrastructure timeout");
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
                Exit(4, "Mini-zigzag controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                float yaw = CommandLineFloat("-miniZigzagYawDeg", 0f);
                if (!controller.StartMiniZigzagPilot(yaw))
                    Exit(5, "Mini-zigzag controller refused to start");
                return;
            }
            if (!controller.MiniZigzagComplete) return;
            if (!string.IsNullOrEmpty(controller.MiniZigzagInfrastructureFailure))
            {
                Exit(6, controller.MiniZigzagInfrastructureFailure);
                return;
            }
            Exit(0, "Mini-zigzag pilot completed with status "
                + controller.MiniZigzagStatus + "; output=" + controller.MiniZigzagOutputRoot);
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

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks)) return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromMinutes(30);
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][MiniZigzag] " + message);
            else Debug.LogError("[ThoracicUSCoach][MiniZigzag] " + message);
            EditorApplication.Exit(code);
        }
    }
}
