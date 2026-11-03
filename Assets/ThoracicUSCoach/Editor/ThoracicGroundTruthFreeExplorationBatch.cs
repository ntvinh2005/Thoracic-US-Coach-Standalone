using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicGroundTruthFreeExplorationBatch
    {
        private const string ActiveKey = "ThoracicUSCoach.GroundTruthFree.Active";
        private const string StartedKey = "ThoracicUSCoach.GroundTruthFree.Started";
        private const string StartedUtcKey = "ThoracicUSCoach.GroundTruthFree.StartedUtcTicks";
        private const string DefaultOutputRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/G2_5_GROUND_TRUTH_FREE_EXPLORATION_20261103_R01/development_session_01";
        private static bool exiting;

        static ThoracicGroundTruthFreeExplorationBatch()
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
                Exit(3, "Ground-truth-free exploration exceeded the 45-minute timeout");
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
                Exit(4, "Ground-truth-free exploration controller is missing");
                return;
            }
            if (!SessionState.GetBool(StartedKey, false))
            {
                SessionState.SetBool(StartedKey, true);
                string outputRoot = Environment.GetEnvironmentVariable("THORACIC_G25_OUTPUT_ROOT");
                if (string.IsNullOrWhiteSpace(outputRoot)) outputRoot = DefaultOutputRoot;
                bool shiftOneFootprintMedial = string.Equals(
                    Environment.GetEnvironmentVariable("THORACIC_G25_SHIFT_ONE_FOOTPRINT_MEDIAL"),
                    "1", StringComparison.Ordinal);
                string offsetText = Environment.GetEnvironmentVariable(
                    "THORACIC_G25_MECHANICAL_HOME_OFFSET_MM");
                float mechanicalHomeOffsetMm = shiftOneFootprintMedial ? -56f : -6f;
                if (float.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float parsedMechanicalHomeOffsetMm))
                    mechanicalHomeOffsetMm = parsedMechanicalHomeOffsetMm;
                string randomStartSeedText = Environment.GetEnvironmentVariable(
                    "THORACIC_G25_RANDOM_START_SEED");
                string randomStartMaximumText = Environment.GetEnvironmentVariable(
                    "THORACIC_G25_RANDOM_START_MAX_MM");
                if (int.TryParse(randomStartSeedText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int randomStartSeed))
                {
                    float maximumRandomStartMm = 30f;
                    if (float.TryParse(randomStartMaximumText, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float parsedRandomStartMaximumMm))
                        maximumRandomStartMm = parsedRandomStartMaximumMm;
                    ThoracicUSCoachController.ConfigureGroundTruthFreeRandomStartThoracicExploration(
                        outputRoot, 89.5f, 45, mechanicalHomeOffsetMm,
                        randomStartSeed, maximumRandomStartMm);
                }
                else
                    ThoracicUSCoachController.ConfigureGroundTruthFreeThoracicExploration(
                        outputRoot, 89.5f, 45, mechanicalHomeOffsetMm);
                string homeJitterSeedText = Environment.GetEnvironmentVariable(
                    "THORACIC_G25_HOME_JITTER_SEED");
                string homeJitterMaximumText = Environment.GetEnvironmentVariable(
                    "THORACIC_G25_HOME_JITTER_MAX_MM");
                if (int.TryParse(homeJitterSeedText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int homeJitterSeed))
                {
                    float maximumHomeJitterMm = 12f;
                    if (float.TryParse(homeJitterMaximumText, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float parsedHomeJitterMaximumMm))
                        maximumHomeJitterMm = parsedHomeJitterMaximumMm;
                    ThoracicUSCoachController.ConfigureGroundTruthFreeEpisodeHomeJitter(
                        homeJitterSeed, maximumHomeJitterMm);
                }
                string lateralJitterSeedText = Environment.GetEnvironmentVariable(
                    "THORACIC_G25_HOME_LATERAL_JITTER_SEED");
                string lateralJitterMaximumText = Environment.GetEnvironmentVariable(
                    "THORACIC_G25_HOME_LATERAL_JITTER_MAX_MM");
                if (int.TryParse(lateralJitterSeedText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int lateralJitterSeed))
                {
                    float maximumLateralJitterMm = 1.5f;
                    if (float.TryParse(lateralJitterMaximumText, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float parsedLateralJitterMaximumMm))
                        maximumLateralJitterMm = parsedLateralJitterMaximumMm;
                    ThoracicUSCoachController.ConfigureGroundTruthFreeEpisodeHomeLateralJitter(
                        lateralJitterSeed, maximumLateralJitterMm);
                }
                if (!controller.StartG2R2ContinuousSweep())
                    Exit(5, "Controller refused ground-truth-free exploration");
                return;
            }
            if (!controller.G2R2ContinuousComplete) return;
            if (!string.IsNullOrEmpty(controller.G2R2ContinuousFailure))
            {
                Exit(6, controller.G2R2ContinuousFailure);
                return;
            }
            Exit(0, "Ground-truth-free exploration completed with status "
                + controller.G2R2ContinuousStatus);
        }

        private static bool TimedOut()
        {
            if (!long.TryParse(SessionState.GetString(StartedUtcKey, string.Empty), out long ticks))
                return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)
                > TimeSpan.FromMinutes(45);
        }

        private static void Exit(int code, string message)
        {
            exiting = true;
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(StartedKey);
            SessionState.EraseString(StartedUtcKey);
            ThoracicUSCoachController.ResetG2R2ContinuousExperimentConfiguration();
            if (code == 0) Debug.Log("[ThoracicUSCoach][G2.5] " + message);
            else Debug.LogError("[ThoracicUSCoach][G2.5] " + message);
            if (string.Equals(
                    Environment.GetEnvironmentVariable("THORACIC_G25_KEEP_UNITY_OPEN"),
                    "1", StringComparison.Ordinal))
            {
                EditorApplication.isPlaying = false;
                return;
            }
            EditorApplication.Exit(code);
        }
    }
}
