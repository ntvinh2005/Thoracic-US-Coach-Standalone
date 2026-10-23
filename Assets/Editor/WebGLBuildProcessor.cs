using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public class WebGLBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    // Re-enable WhiteboxCommunicator when Unity reloads (safety net for failed/cancelled builds)
    static WebGLBuildProcessor()
    {
        EditorApplication.delayCall += () =>
        {
            if (!BuildPipeline.isBuildingPlayer)
            {
                SetWhiteboxCommunicatorActive(true, save: true);
            }
        };
    }

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.WebGL)
            return;

        SetWhiteboxCommunicatorActive(false, save: true);
        Debug.Log("[WebGLBuildProcessor] Disabled WhiteboxCommunicator before WebGL build.");
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.WebGL)
            return;

        // Delay until the build pipeline fully completes so the Editor picks up the change
        EditorApplication.delayCall += () =>
        {
            SetWhiteboxCommunicatorActive(true, save: true);
            Debug.Log("[WebGLBuildProcessor] Re-enabled WhiteboxCommunicator after WebGL build.");
        };
    }

    private static void SetWhiteboxCommunicatorActive(bool active, bool save)
    {
        bool changed = false;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (SearchAndSet(root.transform, active))
                {
                    changed = true;
                    EditorSceneManager.MarkSceneDirty(scene);
                }
            }

            if (changed && save)
            {
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[WebGLBuildProcessor] Saved scene: " + scene.name);
            }
        }
    }

    private static bool SearchAndSet(Transform parent, bool active)
    {
        bool found = false;

        if (parent.name == "WhiteboxCommunicator")
        {
            parent.gameObject.SetActive(active);
            EditorUtility.SetDirty(parent.gameObject);
            found = true;
            Debug.Log("[WebGLBuildProcessor] Set WhiteboxCommunicator active = " + active);
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            if (SearchAndSet(parent.GetChild(i), active))
                found = true;
        }

        return found;
    }
}
