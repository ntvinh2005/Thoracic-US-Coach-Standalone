#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Editor
{
    [InitializeOnLoad]
    public static class ThoracicFullPipelineDemo
    {
        private const string ScenePath =
            "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity";
        private const string PendingKey =
            "ThoracicUSCoach.FullPipelineDemo.Pending";
        private const string Phase2DevelopmentPendingKey =
            "ThoracicUSCoach.Phase2Development.Pending";
        private const string Phase2DevelopmentMap =
            "Artifacts/ThoracicUSCoach/sweep_count/RANDOM_START_LANDMARK_DEMO_20261104_022305/actor_analysis/checkpoint_02/relative_spine_map.json";
        private const string AutoStartRequest =
            "Artifacts/ThoracicUSCoach/active2d/phase2_best_view_20261103/full_pipeline_autostart.request";
        private const string Phase2DevelopmentAutoStartRequest =
            "Artifacts/ThoracicUSCoach/active2d/phase2_best_view_20261103/phase2_development_autostart.request";

        static ThoracicFullPipelineDemo()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update -= CheckForAutoStartRequest;
            EditorApplication.update += CheckForAutoStartRequest;
        }

        private static void CheckForAutoStartRequest()
        {
            if (Application.isBatchMode) return;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            string request = string.IsNullOrEmpty(projectRoot)
                ? string.Empty : Path.Combine(projectRoot, AutoStartRequest);
            string phase2Request = string.IsNullOrEmpty(projectRoot)
                ? string.Empty : Path.Combine(projectRoot, Phase2DevelopmentAutoStartRequest);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (EditorApplication.isPlaying
                    && ((!string.IsNullOrEmpty(request) && File.Exists(request))
                        || (!string.IsNullOrEmpty(phase2Request) && File.Exists(phase2Request))))
                    EditorApplication.isPlaying = false;
                return;
            }
            if (!string.IsNullOrEmpty(request) && File.Exists(request))
            {
                File.Delete(request);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                OpenFullPipelineDemo();
                return;
            }
            if (string.IsNullOrEmpty(phase2Request) || !File.Exists(phase2Request)) return;
            File.Delete(phase2Request);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            OpenPhase2DevelopmentFromFrozenPhaseOne();
        }

        [MenuItem("Tools/Thoracic US Coach/Open Full Phase 1 + Phase 2 Demo", priority = 1)]
        public static void OpenFullPipelineDemo()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorPrefs.SetBool(PendingKey, true);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("Tools/Thoracic US Coach/Run Phase 2 From Valid Phase 1 Handoff", priority = 2)]
        public static void OpenPhase2DevelopmentFromFrozenPhaseOne()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorPrefs.SetBool(Phase2DevelopmentPendingKey, true);
            EditorApplication.isPlaying = true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            if (EditorPrefs.GetBool(PendingKey, false))
            {
                EditorPrefs.SetBool(PendingKey, false);
                EditorApplication.delayCall += StartPipelineAfterAwake;
            }
            if (EditorPrefs.GetBool(Phase2DevelopmentPendingKey, false))
            {
                EditorPrefs.SetBool(Phase2DevelopmentPendingKey, false);
                EditorApplication.delayCall += StartPhase2DevelopmentAfterAwake;
            }
        }

        private static void StartPipelineAfterAwake()
        {
            ThoracicUSCoachController controller = UnityEngine.Object
                .FindObjectsByType<ThoracicUSCoachController>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault();
            if (controller == null)
            {
                Debug.LogError("[ThoracicUSCoach][FullPipeline] Controller not found.");
                return;
            }
            if (!controller.StartRandomStartLandmarkDemo(true))
                Debug.LogError("[ThoracicUSCoach][FullPipeline] Controller refused to start.");
        }

        private static void StartPhase2DevelopmentAfterAwake()
        {
            ThoracicUSCoachController controller = UnityEngine.Object
                .FindObjectsByType<ThoracicUSCoachController>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault();
            if (controller == null)
            {
                Debug.LogError("[ThoracicUSCoach][Phase2Development] Controller not found.");
                return;
            }
            string outputRoot = Path.Combine("Artifacts", "ThoracicUSCoach", "sweep_count",
                "PHASE2_BEST_VIEW_DEVELOPMENT_"
                    + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            if (!controller.StartPhase2BestViewFromPersistedPhaseOneMap(
                    Phase2DevelopmentMap, outputRoot))
                Debug.LogError("[ThoracicUSCoach][Phase2Development] Controller refused to start.");
        }
    }
}
#endif
