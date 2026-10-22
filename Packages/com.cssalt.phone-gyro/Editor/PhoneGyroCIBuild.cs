using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/*
================================================================================
 PhoneGyroCIBuild
================================================================================

 Headless WebGL build entry point for the consuming simulators' CI (see
 ci/webgl-deploy.yml in this repo):

   unity-editor -batchmode -nographics -quit -buildTarget WebGL
                -executeMethod PhoneGyroCIBuild.BuildWebGL

 Ships inside the package so every consumer gets it through the pinned tag.
 Global namespace on purpose, like RoomQRTemplateInstaller.

 Always builds with the PROJECT:RoomQR template (a renamed custom template is
 not used by CI) and assumes a git/registry-sourced package: with a local or
 embedded package the installer's content check could re-copy the template
 over the patched knobs, which the build then refuses loudly.

 Env vars (all optional):
   RELAY_ORIGIN          e.g. https://vam.anest.ufl.edu/websims/gyro-relay/  (trailing slash required)
   SHOW_CAMERA_TRACKING  true | false
   BUILD_DIR             output directory, default build/WebGL
================================================================================
*/
public static class PhoneGyroCIBuild
{
    const string Template = "Assets/WebGLTemplates/RoomQR/index.html";
    static readonly Regex OriginRx = new Regex(@"^(\s*var RELAY_ORIGIN = ')([^'\r\n]*)(';)", RegexOptions.Multiline);
    static readonly Regex CameraRx = new Regex(@"^(\s*var SHOW_CAMERA_TRACKING = )(true|false)(;)", RegexOptions.Multiline);

    public static void BuildWebGL()
    {
        try { Run(); }
        catch (Exception e) { Debug.LogError("[PhoneGyroCIBuild] FAILED: " + e); EditorApplication.Exit(1); }
    }

    static void Run()
    {
        // 1. Template, synchronously. The installer's [InitializeOnLoad] + delayCall is not guaranteed to
        //    run before -executeMethod, and consumers commit a stale .phone-gyro-version stamp, so
        //    without this the OLD template would ship while the build reports success.
        if (!RoomQRTemplateInstaller.Install(true))
            throw new Exception("RoomQR template install did not run (package not resolvable or template source missing); refusing to build with the committed copy");
        if (!File.Exists(Template)) throw new Exception("template not installed: " + Template);

        // 2. Patch ONLY the two knob lines of the installed copy (the installer overwrote any hand edit).
        //    Env unset = keep the package default. Only a quoted string / a boolean is replaced, so this
        //    cannot introduce a line starting with #if or a triple brace, which Unity's template
        //    preprocessor would misread.
        string html = File.ReadAllText(Template);
        string origin = Env("RELAY_ORIGIN");
        if (origin != null)
        {
            if (!origin.EndsWith("/") || origin.Contains("'"))
                throw new Exception("RELAY_ORIGIN must end with '/' and contain no quote: " + origin);
            html = ReplaceOnce(OriginRx, html, origin);
        }
        string cam = Env("SHOW_CAMERA_TRACKING");
        if (cam != null)
        {
            cam = cam.ToLowerInvariant();
            if (cam != "true" && cam != "false") throw new Exception("SHOW_CAMERA_TRACKING must be true or false: " + cam);
            html = ReplaceOnce(CameraRx, html, cam);
        }
        File.WriteAllText(Template, html);
        AssetDatabase.Refresh();
        var om = OriginRx.Match(html);
        if (!om.Success)
            throw new Exception("template has no `var RELAY_ORIGIN = '...';` line; this script needs the template that ships with it");
        origin = om.Groups[2].Value;   // the file is the single source of truth for step 4
        if (!origin.EndsWith("/")) throw new Exception("template RELAY_ORIGIN must end with '/': " + origin);
        Debug.Log($"[PhoneGyroCIBuild] RELAY_ORIGIN={origin} SHOW_CAMERA_TRACKING={CameraRx.Match(html).Groups[2].Value}");

        // 3. Player settings, explicit. Gzip + decompression fallback needs zero server configuration
        //    (works under any .htaccess), which is the safest default for a no-admin Apache host.
        PlayerSettings.WebGL.template = "PROJECT:RoomQR";
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;

        // 4. RelayHost/RelayPath are serialized Inspector values per scene; a stale one builds, uploads
        //    and never pairs, with no error anywhere. Fail loudly instead.
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) throw new Exception("no enabled scenes in Build Settings");
        var u = new Uri(origin);
        string wantHost = u.Host, wantPath = u.AbsolutePath + "ws";   // -> vam.anest.ufl.edu, /websims/gyro-relay/ws
        int found = 0;
        foreach (string s in scenes)
        {
            EditorSceneManager.OpenScene(s, OpenSceneMode.Single);
#if UNITY_2022_2_OR_NEWER
            var clients = UnityEngine.Object.FindObjectsByType<PhoneRelayClient>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var clients = UnityEngine.Object.FindObjectsOfType<PhoneRelayClient>(true);
#endif
            foreach (var c in clients)
            {
                found++;
                if (!string.Equals(c.RelayHost, wantHost, StringComparison.OrdinalIgnoreCase) || c.RelayPath != wantPath)
                    throw new Exception($"{s} / {c.name}: PhoneRelayClient = {c.RelayHost}{c.RelayPath}, template wants {wantHost}{wantPath}. Fix the Inspector value and commit.");
            }
        }
        if (found == 0) Debug.LogWarning("[PhoneGyroCIBuild] no PhoneRelayClient in any enabled scene");

        // 5. Build. The template must still be what step 2 wrote (a local/embedded package's installer could re-copy it).
        if (File.ReadAllText(Template) != html) throw new Exception("template changed after patching (installer re-copied it?); refusing to build");
        string outDir = Env("BUILD_DIR") ?? "build/WebGL";
        var sum = BuildPipeline.BuildPlayer(scenes, outDir, BuildTarget.WebGL, BuildOptions.None).summary;
        Debug.Log($"[PhoneGyroCIBuild] {sum.result} errors={sum.totalErrors} bytes={sum.totalSize} time={sum.totalTime}");
        if (sum.result != BuildResult.Succeeded) throw new Exception("WebGL build " + sum.result);
    }

    static string Env(string key)
    {
        string v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    // Exactly one match or fail: a silent no-op here would ship the wrong relay or knob.
    static string ReplaceOnce(Regex rx, string html, string value)
    {
        int n = rx.Matches(html).Count;
        if (n != 1) throw new Exception($"{rx} matched {n} lines in {Template}, expected exactly 1");
        return rx.Replace(html, m => m.Groups[1].Value + value + m.Groups[3].Value);
    }
}
