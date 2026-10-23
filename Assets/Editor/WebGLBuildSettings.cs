#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/*
WebGLBuildSettings
Applies the project's reference WebGL Player/Publishing settings in one place, so the
configuration is reviewable in git instead of living only as an opaque diff in
ProjectSettings.asset.

Source: the WebGL setup document (settings captured by Chris Samouce, written up by
Simon Mesber) recording the configuration that produces builds without a long startup
time.

Only settings that DIFFER from the reference are worth touching, but everything the
document specifies is asserted here anyway — that way this stays a complete, executable
statement of the intended configuration rather than a changelog of one moment.

Two documented settings are deliberately NOT applied:

  Color Space = Gamma
      The project is Linear and staying that way for now. Switching forces a full asset
      reimport and lighting rebake and visibly changes shading across the whole sim, so
      it is a deliberate, separately-reviewed decision rather than part of a settings pass.

  Graphics Jobs = "GPU (Batched)"
      No such option exists in Unity 6000.3 — GraphicsJobMode offers Native, Legacy and
      Split only. The document predates this Editor version. Graphics Jobs is left off;
      WebGL is single-threaded, so this is not expected to affect startup time.

Both are called out in ApplyReferenceSettings' log so the divergence stays visible.
*/
public static class WebGLBuildSettings
{
    // Vertex Compression: Normal | Tangent | TexCoord0 | TexCoord2 | TexCoord3.
    // The enum's flag values are Position=1, Normal=2, Color=4, UV0=8, UV1=16,
    // UV2=32, UV3=64, Tangent=128, so the documented set is 2+128+8+32+64 = 234.
    // There is no PlayerSettings API for this in 6000.3; it is written through
    // SerializedObject below.
    const int VertexCompressionMask = 234;

    // LightmapEncodingQuality / HDRCubemapEncodingQuality are internal to UnityEditor
    // in 6000.3 and their PlayerSettings accessors are not public, so both are written
    // through SerializedObject as raw ints. Both enums are ordered Low, Normal, High.
    const int LightmapEncodingHigh = 2;
    const int HDRCubemapEncodingNormal = 1;

    // Serialized name of the WebGL platform inside the per-platform setting arrays.
    const string WebGLTargetName = "WebGL";

    const string BuildFolder = "WebGLBuild";

    [MenuItem("Tools/WebGL/Apply Reference Build Settings")]
    public static void ApplyReferenceSettings()
    {
        var nbt = NamedBuildTarget.WebGL;
        var bt = BuildTarget.WebGL;

        // ── Other Settings → Rendering ──────────────────────────────────
        // Color Space intentionally untouched (see file header).
        PlayerSettings.SetUseDefaultGraphicsAPIs(bt, true);
        SetLightmapEncoding(LightmapEncodingHigh);
        SetHDRCubemapEncoding(HDRCubemapEncodingNormal);
        PlayerSettings.enableFrameTimingStats = false;
        PlayerSettings.enable360StereoCapture = false;
        PlayerSettings.mipStripping = false;
        EditorUserBuildSettings.webGLBuildSubtarget = WebGLTextureSubtarget.DXT;

        // ── Other Settings → Configuration ──────────────────────────────
        PlayerSettings.SetApiCompatibilityLevel(nbt, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.SetIl2CppCodeGeneration(nbt, Il2CppCodeGeneration.OptimizeSpeed);
        PlayerSettings.SetIl2CppCompilerConfiguration(nbt, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetIl2CppStacktraceInformation(nbt, Il2CppStacktraceInformation.MethodOnly);
        PlayerSettings.gcIncremental = true;
        PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;

        // ── Other Settings → Shader Settings ────────────────────────────
        PlayerSettings.SetShaderPrecisionModel(ShaderPrecisionModel.PlatformDefault);
        PlayerSettings.strictShaderVariantMatching = false;
        PlayerSettings.SetDefaultShaderChunkSizeInMB(16);
        PlayerSettings.SetDefaultShaderChunkCount(0);

        // ── Other Settings → Script Compilation ─────────────────────────
        PlayerSettings.SetScriptingDefineSymbols(nbt, string.Empty);
        PlayerSettings.SetAdditionalCompilerArguments(nbt, new string[0]);
        PlayerSettings.suppressCommonWarnings = true;
        PlayerSettings.allowUnsafeCode = false;

        // ── Other Settings → Optimization ───────────────────────────────
        PlayerSettings.bakeCollisionMeshes = false;
        PlayerSettings.stripEngineCode = true;
        PlayerSettings.SetManagedStrippingLevel(nbt, ManagedStrippingLevel.Medium);
        PlayerSettings.stripUnusedMeshComponents = true;   // "Optimize Mesh Data"
        ApplyVertexCompressionMask(VertexCompressionMask);

        // ── Other Settings → Stack Trace ────────────────────────────────
        foreach (LogType lt in Enum.GetValues(typeof(LogType)))
            PlayerSettings.SetStackTraceLogType(lt, StackTraceLogType.ScriptOnly);

        // ── Publishing Settings ─────────────────────────────────────────
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.nameFilesAsHashes = true;
        PlayerSettings.WebGL.dataCaching = false;
        PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;
        PlayerSettings.WebGL.showDiagnostics = false;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.powerPreference = WebGLPowerPreference.HighPerformance;

        // ── WebAssembly Language Features ───────────────────────────────
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
        PlayerSettings.WebGL.threadsSupport = false;
        PlayerSettings.WebGL.wasm2023 = false;
        PlayerSettings.WebGL.initialMemorySize = 256;
        PlayerSettings.WebGL.memoryGrowthMode = WebGLMemoryGrowthMode.Geometric;
        PlayerSettings.WebGL.maximumMemorySize = 2048;
        PlayerSettings.WebGL.geometricMemoryGrowthStep = 0.2f;
        PlayerSettings.WebGL.memoryGeometricGrowthCap = 96;

        AssetDatabase.SaveAssets();

        Debug.Log(
            "[WebGLBuildSettings] Reference settings applied.\n" +
            $"  Color Space left at {PlayerSettings.colorSpace} (document says Gamma — deliberate divergence).\n" +
            "  Graphics Jobs left off (document's \"GPU (Batched)\" has no equivalent in this Editor version).");
    }

    // Several settings the reference document specifies have no public PlayerSettings
    // accessor in 6000.3 (or their enum type is internal), so they are written straight
    // to the ProjectSettings asset. LoadAllAssetsAtPath's first object is the
    // PlayerSettings instance the Inspector edits.
    static SerializedObject OpenPlayerSettings()
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
        if (assets == null || assets.Length == 0)
        {
            Debug.LogWarning("[WebGLBuildSettings] Could not load ProjectSettings.asset.");
            return null;
        }
        return new SerializedObject(assets[0]);
    }

    static void ApplyVertexCompressionMask(int mask)
    {
        var so = OpenPlayerSettings();
        if (so == null) return;

        var prop = so.FindProperty("VertexChannelCompressionMask");
        if (prop == null)
        {
            Debug.LogWarning("[WebGLBuildSettings] VertexChannelCompressionMask not found; vertex compression unchanged.");
            return;
        }

        prop.intValue = mask;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetLightmapEncoding(int quality)
    {
        SetPerPlatformInt("m_BuildTargetGroupLightmapEncodingQuality", "m_EncodingQuality", quality, serializedVersion: 2);
    }

    static void SetHDRCubemapEncoding(int quality)
    {
        SetPerPlatformInt("m_BuildTargetGroupHDRCubemapEncodingQuality", "m_EncodingQuality", quality, serializedVersion: 2);
    }

    // These settings are stored as arrays of { m_BuildTarget, <valueField> } keyed by
    // platform name, with absent platforms falling back to the Editor default. Updates
    // the WebGL entry in place, appending one if the platform has never been configured.
    static void SetPerPlatformInt(string arrayPropertyName, string valueField, int value, int serializedVersion)
    {
        var so = OpenPlayerSettings();
        if (so == null) return;

        var array = so.FindProperty(arrayPropertyName);
        if (array == null || !array.isArray)
        {
            Debug.LogWarning($"[WebGLBuildSettings] {arrayPropertyName} not found or not an array; left unchanged.");
            return;
        }

        for (int i = 0; i < array.arraySize; i++)
        {
            var element = array.GetArrayElementAtIndex(i);
            var targetProp = element.FindPropertyRelative("m_BuildTarget");
            if (targetProp == null || targetProp.stringValue != WebGLTargetName) continue;

            element.FindPropertyRelative(valueField).intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            return;
        }

        array.InsertArrayElementAtIndex(array.arraySize);
        var added = array.GetArrayElementAtIndex(array.arraySize - 1);
        added.FindPropertyRelative("m_BuildTarget").stringValue = WebGLTargetName;
        added.FindPropertyRelative(valueField).intValue = value;

        var versionProp = added.FindPropertyRelative("serializedVersion");
        if (versionProp != null) versionProp.intValue = serializedVersion;

        so.ApplyModifiedPropertiesWithoutUndo();
    }

    [MenuItem("Tools/WebGL/Build")]
    public static void Build()
    {
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[WebGLBuildSettings] No enabled scenes in Build Settings; nothing to build.");
            return;
        }

        Debug.Log($"[WebGLBuildSettings] Building {scenes.Length} scene(s) to {BuildFolder}/ …");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = BuildFolder,
            target = BuildTarget.WebGL,
            targetGroup = BuildTargetGroup.WebGL,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        Debug.Log($"[WebGLBuildSettings] Build {summary.result}: " +
                  $"{summary.totalSize / (1024 * 1024)} MB in {summary.totalTime}, " +
                  $"{summary.totalErrors} error(s), {summary.totalWarnings} warning(s).");
    }
}
#endif
