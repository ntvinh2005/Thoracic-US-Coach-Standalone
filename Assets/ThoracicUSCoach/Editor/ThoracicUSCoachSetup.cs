using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    public static class ThoracicUSCoachSetup
    {
        public const string DevelopmentScene = "Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity";

        [MenuItem("Tools/Thoracic US Coach/Open 2D Demo", priority = 1)]
        public static void Open2DDemo()
        {
            if (EditorApplication.isPlaying)
            {
                ThoracicUSCoachController playing = Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
                if (playing != null) Selection.activeGameObject = playing.gameObject;
                Debug.Log("[ThoracicUSCoach][2D] Demo is already in Play Mode. Use the visible 2D panel or press F2.");
                return;
            }
            if (!File.Exists(DevelopmentScene))
            {
                Debug.LogError("[ThoracicUSCoach][2D] Demo scene is missing: " + DevelopmentScene);
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Scene scene = EditorSceneManager.OpenScene(DevelopmentScene, OpenSceneMode.Single);
            ThoracicUSCoachController controller = Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null)
            {
                Debug.LogError("[ThoracicUSCoach][2D] Controller is missing from " + DevelopmentScene);
                return;
            }
            Selection.activeGameObject = controller.gameObject;
            Debug.Log("[ThoracicUSCoach][2D] Scene opened. Press Play; the interactive 2D panel appears automatically.", controller);
        }

        // Command-line/runtime verification entry point. It opens the exact
        // scene and enters the same Play Mode a user reaches from the menu.
        public static void Open2DDemoAndPlay()
        {
            Open2DDemo();
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorApplication.isPlaying = true;
            };
        }

        [MenuItem("Tools/Thoracic US Coach/Create or Refresh Development Scene")]
        public static void CreateDevelopmentScene()
        {
            Directory.CreateDirectory("Assets/ThoracicUSCoach/Scenes");
            Scene source = SceneManager.GetActiveScene();
            if (!source.IsValid())
            {
                Debug.LogError("[ThoracicUSCoach] No active source scene.");
                return;
            }

            // Save As changes the in-memory scene's path to the isolated PoC asset. The original source asset is untouched.
            if (!EditorSceneManager.SaveScene(source, DevelopmentScene, false))
            {
                Debug.LogError("[ThoracicUSCoach] Could not create development scene.");
                return;
            }

            GameObject root = GameObject.Find("Thoracic US Coach POC");
            if (root == null)
            {
                root = new GameObject("Thoracic US Coach POC");
                Undo.RegisterCreatedObjectUndo(root, "Create Thoracic US Coach POC");
            }
            ThoracicUSCoachController controller = root.GetComponent<ThoracicUSCoachController>();
            if (controller == null) controller = Undo.AddComponent<ThoracicUSCoachController>(root);
            controller.AutoWire();
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(root.scene);
            EditorSceneManager.SaveScene(root.scene);
            Selection.activeGameObject = root;
            Debug.Log("[ThoracicUSCoach] Development scene ready: " + DevelopmentScene);
        }

        [MenuItem("Tools/Thoracic US Coach/Validate PoC Scene")]
        public static void ValidateScene()
        {
            ThoracicUSCoachController controller = Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null)
            {
                Debug.LogError("[ThoracicUSCoach] Controller missing. Run Create or Refresh Development Scene.");
                return;
            }
            controller.AutoWire();
            SerializedObject serialized = new SerializedObject(controller);
            string[] required = { "probeRoot", "faceCenter", "rearCenter", "scanningPlane", "t7RightSeed", "skinCollider", "skinIntersection", "ultrasoundCamera" };
            foreach (string name in required)
            {
                SerializedProperty property = serialized.FindProperty(name);
                if (property == null || property.objectReferenceValue == null)
                {
                    Debug.LogError("[ThoracicUSCoach] Missing required reference: " + name, controller);
                    return;
                }
            }
            Debug.Log("[ThoracicUSCoach] Scene validation passed. Enter Play Mode, press D, then C.", controller);
        }

        [MenuItem("Tools/Thoracic US Coach/Run Candidate Discovery")]
        public static void RunDiscovery()
        {
            ThoracicUSCoachController controller = Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null || !Application.isPlaying)
            {
                Debug.LogError("[ThoracicUSCoach] Open the PoC scene and enter Play Mode before discovery.");
                return;
            }
            controller.StartDiscovery();
        }

        [MenuItem("Tools/Thoracic US Coach/Capture One Complete Sweep")]
        public static void CaptureSweep()
        {
            ThoracicUSCoachController controller = Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null || !Application.isPlaying)
            {
                Debug.LogError("[ThoracicUSCoach] Open the PoC scene and enter Play Mode before capture.");
                return;
            }
            controller.StartCaptureSweep();
        }

        [MenuItem("Tools/Thoracic US Coach/Capture Diverse Pilot Dataset")]
        public static void CapturePilot()
        {
            ThoracicUSCoachController controller = Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null || !Application.isPlaying)
            {
                Debug.LogError("[ThoracicUSCoach] Open the PoC scene, enter Play Mode, and run discovery before pilot capture.");
                return;
            }
            controller.StartCapturePilot();
        }

        [MenuItem("Tools/Thoracic US Coach/Run Image Policy Demo")]
        public static void RunPolicyDemo()
        {
            ThoracicUSCoachController controller = Object.FindFirstObjectByType<ThoracicUSCoachController>(FindObjectsInactive.Include);
            if (controller == null || !Application.isPlaying)
            {
                Debug.LogError("[ThoracicUSCoach] Open the PoC scene and enter Play Mode before the policy demo.");
                return;
            }
            controller.StartPolicyDemo();
        }
    }
}
