using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicAcquisitionGeometryRecoveryBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.G2Geometry.Active";
        private const string StartedKey = "ThoracicUSCoach.G2Geometry.Started";
        private const string ModeKey = "ThoracicUSCoach.G2Geometry.Mode";
        private const string StartedUtcKey = "ThoracicUSCoach.G2Geometry.StartedUtcTicks";
        private static bool exiting;

        static ThoracicAcquisitionGeometryRecoveryBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("G2 acquisition geometry runner requires Unity -batchmode");
            string mode = CommandLineValue("-g2GeometryMode");
            if (string.IsNullOrEmpty(mode))
                throw new ArgumentException("-g2GeometryMode is required");
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(StartedKey, false);
            SessionState.SetString(ModeKey, mode);
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
                Exit(3, "G2 acquisition geometry run exceeded the 30-minute timeout");
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
                Exit(4, "G2 acquisition geometry controller is missing from the development scene");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                if (!controller.StartG2AcquisitionGeometryRecovery(
                        SessionState.GetString(ModeKey, string.Empty)))
                    Exit(5, "G2 acquisition geometry controller refused to start");
                return;
            }
            if (!controller.G2GeometryComplete) return;
            if (!string.IsNullOrEmpty(controller.G2GeometryFailure))
            {
                Exit(6, controller.G2GeometryFailure);
                return;
            }
            Exit(0, "G2 acquisition geometry completed with status "
                + controller.G2GeometryStatus + "; output=" + controller.G2GeometryOutputRoot);
        }

        private static string CommandLineValue(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return string.Empty;
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
            SessionState.EraseString(ModeKey);
            SessionState.EraseString(StartedUtcKey);
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2Geometry] " + message);
            else Debug.LogError("[ThoracicUSCoach][G2Geometry] " + message);
            EditorApplication.Exit(code);
        }
    }
}
