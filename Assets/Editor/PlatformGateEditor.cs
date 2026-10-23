#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
PlatformGateEditor
Editor companion for PlatformGate. Re-applies any PlatformGate components in the open
scenes on four triggers:

  1) Editor load / domain reload (covers recompiles, and the common case of the target
     already being the desired one, where trigger 2 never fires).
  2) The active Build Target changes (user switches platform in Build Settings).
  3) A scene is opened (so freshly opened scenes reflect the current target).
  4) Entering Play mode, so the Game view can't start from a drifted edit-mode state.

Trigger 1 matters because lastTarget is re-seeded from the current target on every
domain reload: if you are already on the target you want, the change in trigger 2 is
never observed, and any hand-toggled object stays wrong indefinitely.

When a gate changes something, the affected scenes are marked dirty so the new active
states get saved on the next Save. Scenes are NOT dirtied when nothing changed, nor when
entering Play mode (those changes are discarded on exit anyway) — otherwise the added
triggers would produce constant spurious "unsaved changes" prompts. A "Tools → Platform
Gate → Apply Now" menu item is also available for manual runs.

Lives in an Editor/ folder so it's stripped from runtime builds.
*/
[InitializeOnLoad]
public static class PlatformGateEditor
{
    // We poll EditorUserBuildSettings.activeBuildTarget every editor update tick.
    // Polling is simpler and more reliable than EditorBuildSettings callbacks across
    // Unity versions, and the cost is negligible (one integer compare per tick).
    static BuildTarget lastTarget;

    static PlatformGateEditor()
    {
        lastTarget = EditorUserBuildSettings.activeBuildTarget;
        EditorApplication.update += OnEditorUpdate;
        EditorSceneManager.sceneOpened += OnSceneOpened;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

        // Deferred, because during a static constructor the scenes aren't
        // restored yet and FindObjectsByType comes back empty.
        //
        // Use afterAssemblyReload, NOT EditorApplication.delayCall: a delayCall
        // registered from an [InitializeOnLoad] static constructor is discarded
        // as the domain reload completes, so it silently never runs. This was
        // verified by re-introducing drift, forcing a reload, and observing the
        // object stay wrong. afterAssemblyReload fires after the reload has
        // finished and the scenes are back.
        AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
    }

    static void OnAfterAssemblyReload()
    {
        // One-shot per reload: the handler is re-subscribed by the static
        // constructor on the next reload, so unsubscribing here keeps the
        // invocation list from growing.
        AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
        ApplyToAllOpenScenes(quiet: true);
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // ExitingEditMode fires before the play-mode scene is spun up, so the
        // objects PlatformGate manages are in their correct state before any
        // Awake runs. PlatformGate.Awake also gates itself on UNITY_WEBGL, so
        // this is belt-and-braces for anything reading active state early.
        if (state == PlayModeStateChange.ExitingEditMode) ApplyToAllOpenScenes(quiet: true);
    }

    static void OnEditorUpdate()
    {
        var current = EditorUserBuildSettings.activeBuildTarget;
        if (current == lastTarget) return;
        lastTarget = current;
        ApplyToAllOpenScenes(quiet: false);
    }

    static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode)
    {
        ApplyToAllOpenScenes(quiet: true);
    }

    [MenuItem("Tools/Platform Gate/Apply Now")]
    public static void ApplyMenu() => ApplyToAllOpenScenes(quiet: false);

    static void ApplyToAllOpenScenes(bool quiet)
    {
        bool webgl = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
        var gates = Object.FindObjectsByType<PlatformGate>(FindObjectsSortMode.None);
        if (gates == null || gates.Length == 0) return;

        bool changed = false;
        foreach (var g in gates) changed |= g.Apply(webgl);

        // Only dirty scenes when something actually moved, and never while
        // entering Play mode — play-mode changes are thrown away on exit, so
        // dirtying there would just nag the user to save nothing.
        if (changed && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        // Log on explicit runs (target switch, menu item), and on any run that
        // actually changed something — a silent correction is worth surfacing.
        if (!quiet || changed)
        {
            Debug.Log($"[PlatformGate] Build Target = {EditorUserBuildSettings.activeBuildTarget} → " +
                      $"applied {gates.Length} gate(s) (WebGL list active: {webgl}; changed: {changed}).");
        }
    }
}
#endif
