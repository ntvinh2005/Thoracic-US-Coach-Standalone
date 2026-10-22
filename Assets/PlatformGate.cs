using System.Collections.Generic;
using UnityEngine;

/*
PlatformGate
Enables one list of GameObjects on WebGL builds and a different list on every other
platform. Lets a single scene contain features that only make sense in one platform
(e.g. the phone-gyro pairing rig is WebGL-only; a keyboard/mouse rig is desktop-only)
without juggling separate scenes per build.

Behavior:
- At runtime: Awake() branches on the UNITY_WEBGL compile define and sets each managed
  GameObject's active state once.
- In the Editor: a companion script (Editor/PlatformGateEditor.cs) re-applies on load,
  when the Build target changes, when a scene is opened, and when entering Play mode,
  so the Game view always reflects what the build will look like.

Why UNITY_WEBGL and not Application.platform:
  Application.platform reports WindowsEditor/OSXEditor in the Editor no matter what the
  active Build Target is — it only ever returns WebGLPlayer inside a real browser build.
  Gating on it meant pressing Play with the Web target selected silently applied the
  NON-WebGL layout, overruling whatever the Editor companion had just set up. Unity sets
  the UNITY_WEBGL define from the active Build Target for Editor and Player assemblies
  alike, so it is correct in Play mode and in the build.

Usage:
  1) Drop this on a persistent GameObject in the scene (it doesn't matter which —
     it just needs to exist).
  2) Drag any WebGL-only objects into WebGLOnly.
  3) Drag any non-WebGL objects (Windows, Mac, Linux, mobile) into NonWebGLOnly.
  4) Switch the Build Target in Build Settings; the lists toggle automatically.

Notes:
- An empty list means "no objects" — the other list still gets applied as usual.
- Null entries (deleted GameObjects) are silently skipped, not errors.
- A GameObject in both lists is allowed; it'll end up matching whichever list applies
  to the active platform.
*/
public class PlatformGate : MonoBehaviour
{
    [Tooltip("Active on WebGL builds and in the Editor when the Build Target is WebGL.")]
    public List<GameObject> WebGLOnly = new List<GameObject>();

    [Tooltip("Active on every platform EXCEPT WebGL (Windows, Mac, Linux, iOS, Android, etc.).")]
    public List<GameObject> NonWebGLOnly = new List<GameObject>();

    void Awake()
    {
#if UNITY_WEBGL
        Apply(true);
#else
        Apply(false);
#endif
    }

    /// <summary>
    /// Sets each managed GameObject's active state for the given platform.
    /// Called by Awake at runtime and by the Editor companion.
    /// Returns true if any GameObject's active state actually changed, so the Editor
    /// companion can avoid marking scenes dirty when there was nothing to do.
    /// </summary>
    public bool Apply(bool isWebGL)
    {
        bool changed = false;

        foreach (var go in WebGLOnly)
        {
            if (go == null || go.activeSelf == isWebGL) continue;
            go.SetActive(isWebGL);
            changed = true;
        }

        foreach (var go in NonWebGLOnly)
        {
            // Desired state is !isWebGL, so a change is needed exactly when
            // the current state still equals isWebGL.
            if (go == null || go.activeSelf != isWebGL) continue;
            go.SetActive(!isWebGL);
            changed = true;
        }

        return changed;
    }
}
