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
        private const string OutputRootOverrideKey =
            "ThoracicUSCoach.G2R2Continuous.OutputRootOverride";
        private const string SurfaceYawOverrideKey =
            "ThoracicUSCoach.G2R2Continuous.SurfaceYawOverride";
        private const string ScanTopWidthOverrideKey =
            "ThoracicUSCoach.G2R2Continuous.ScanTopWidthOverride";
        private const string PhysicalFinalizationKey =
            "ThoracicUSCoach.G2R2Continuous.PhysicalFinalization";
        private static bool exiting;

        static ThoracicG2R2ContinuousBatch()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void RunCli()
        {
            SessionState.EraseString(OutputRootOverrideKey);
            SessionState.EraseString(SurfaceYawOverrideKey);
            SessionState.EraseInt(ScanTopWidthOverrideKey);
            SessionState.EraseBool(PhysicalFinalizationKey);
            StartRun();
        }

        public static void RunCliConfigured(string outputRoot, float surfaceYawDeg)
        {
            SessionState.SetString(OutputRootOverrideKey, outputRoot);
            SessionState.SetString(SurfaceYawOverrideKey,
                surfaceYawDeg.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SessionState.EraseInt(ScanTopWidthOverrideKey);
            SessionState.EraseBool(PhysicalFinalizationKey);
            StartRun();
        }

        public static void RunCliConfigured(
            string outputRoot, float surfaceYawDeg, int scanTopWidthMm,
            bool requirePhysicalFinalization)
        {
            SessionState.SetString(OutputRootOverrideKey, outputRoot);
            SessionState.SetString(SurfaceYawOverrideKey,
                surfaceYawDeg.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SessionState.SetInt(ScanTopWidthOverrideKey, scanTopWidthMm);
            SessionState.SetBool(PhysicalFinalizationKey, requirePhysicalFinalization);
            StartRun();
        }

        private static void StartRun()
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
                ConfigureExperiment();
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

        private static void ConfigureExperiment()
        {
            string outputRoot = SessionState.GetString(OutputRootOverrideKey, string.Empty);
            string yawText = SessionState.GetString(SurfaceYawOverrideKey, string.Empty);
            if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(yawText))
            {
                ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
                return;
            }
            if (!float.TryParse(yawText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float surfaceYawDeg))
                throw new InvalidOperationException("Configured G2-R2 surface yaw is invalid");
            int scanTopWidthMm = SessionState.GetInt(ScanTopWidthOverrideKey, 0);
            if (scanTopWidthMm > 0)
                ThoracicUSCoachController.ConfigureG2R2ContinuousExperiment(
                    outputRoot, surfaceYawDeg, scanTopWidthMm,
                    SessionState.GetBool(PhysicalFinalizationKey, false));
            else ThoracicUSCoachController.ConfigureG2R2ContinuousExperiment(
                outputRoot, surfaceYawDeg);
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
            SessionState.EraseString(OutputRootOverrideKey);
            SessionState.EraseString(SurfaceYawOverrideKey);
            SessionState.EraseInt(ScanTopWidthOverrideKey);
            SessionState.EraseBool(PhysicalFinalizationKey);
            ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2R2] " + message);
            else Debug.LogError("[ThoracicUSCoach][G2R2] " + message);
            EditorApplication.Exit(code);
        }
    }
}


