using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Applies the "Hybrid Cross Section" effect to the current hierarchy Selection (and all
/// descendants) by swapping each renderer's material(s) onto the CrossSection/Standard shader.
///
/// For every UNIQUE source material found, one cross-section material asset is generated in
/// "Assets/Anatomy Materials", carrying over the source's base color / albedo / metallic /
/// smoothness, and taking its _SectionColor from the SDK's CrossSectionMaterial.mat.
///
/// Also ensures a CrossSectionManager exists in the scene (creates one if missing), since the
/// clipping effect is driven globally by that singleton.
///
/// This tool is intentionally Selection-driven (not tied to an "Anatomy" GameObject) so it can
/// be retrofitted onto existing projects, including ones whose meshes live outside "Anatomy"
/// (e.g. SkinnedMeshRenderer rigs).
/// </summary>
public class AnatomyCrossSectionApplier : EditorWindow
{
    const string ShaderName = "CrossSection/Standard";
    const string MaterialsFolder = "Assets/Anatomy Materials";
    const string GeneratedSuffix = "_CrossSection";

    // Properties carried over from the source material for a full visual match.
    static readonly string[] CopiedFloats = { "_Metallic", "_Smoothness" };

    [MenuItem("SMMARTS/Apply Hybrid Cross Section to Selection")]
    static void Init()
    {
        AnatomyCrossSectionApplier window = GetWindow<AnatomyCrossSectionApplier>();
        window.titleContent = new GUIContent("Cross Section Applier");
        window.minSize = new Vector2(420, 220);
        window.Show();
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Apply Hybrid Cross Section", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        GameObject[] selection = Selection.gameObjects;

        if (selection == null || selection.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "Select one or more GameObjects in the hierarchy. The cross-section shader will be " +
                "applied to every MeshRenderer / SkinnedMeshRenderer on the selection and its children.",
                MessageType.Info);
            return;
        }

        List<Renderer> renderers = GatherRenderers(selection);
        HashSet<Material> uniqueMaterials = GatherUniqueMaterials(renderers);

        EditorGUILayout.LabelField($"Selected roots: {selection.Length}");
        EditorGUILayout.LabelField($"Renderers found: {renderers.Count}");
        EditorGUILayout.LabelField($"Unique source materials: {uniqueMaterials.Count}");

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "One cross-section material is generated per unique source material in\n" +
            $"\"{MaterialsFolder}\". Existing generated materials are reused; materials already\n" +
            "using the cross-section shader are left untouched. A CrossSectionManager will be\n" +
            "created if the scene has none.",
            MessageType.None);

        if (Shader.Find(ShaderName) == null)
        {
            EditorGUILayout.HelpBox($"Shader \"{ShaderName}\" not found in project. Aborting.", MessageType.Error);
            return;
        }

        EditorGUILayout.Space();
        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Apply Cross Section to Selection", GUILayout.Height(32)))
        {
            Apply(renderers);
        }
        GUI.backgroundColor = Color.white;
    }

    // --- Gathering -----------------------------------------------------------------------

    static List<Renderer> GatherRenderers(GameObject[] roots)
    {
        // Use a set keyed by instance to avoid double-counting when selected objects are nested.
        HashSet<Renderer> set = new HashSet<Renderer>();
        foreach (GameObject root in roots)
        {
            if (root == null) continue;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true)) set.Add(mr);
            foreach (var sr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) set.Add(sr);
        }
        return new List<Renderer>(set);
    }

    static HashSet<Material> GatherUniqueMaterials(List<Renderer> renderers)
    {
        HashSet<Material> materials = new HashSet<Material>();
        foreach (Renderer r in renderers)
        {
            foreach (Material m in r.sharedMaterials)
            {
                if (m != null) materials.Add(m);
            }
        }
        return materials;
    }

    // --- Apply ---------------------------------------------------------------------------

    void Apply(List<Renderer> renderers)
    {
        Shader crossSectionShader = Shader.Find(ShaderName);
        Color sectionColor = GetReferenceSectionColor();

        EnsureFolder();

        // Map each source material to its generated cross-section material (created or reused).
        Dictionary<Material, Material> map = new Dictionary<Material, Material>();
        int generated = 0, reused = 0, skipped = 0;

        HashSet<Material> uniqueMaterials = GatherUniqueMaterials(renderers);
        foreach (Material source in uniqueMaterials)
        {
            // Already a cross-section material? Leave it as-is (map to itself, no reassignment change).
            if (source.shader == crossSectionShader)
            {
                map[source] = source;
                skipped++;
                continue;
            }

            string assetPath = $"{MaterialsFolder}/{SanitizeName(source.name)}{GeneratedSuffix}.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                map[source] = existing;
                reused++;
                continue;
            }

            Material generatedMat = BuildCrossSectionMaterial(source, crossSectionShader, sectionColor);
            AssetDatabase.CreateAsset(generatedMat, assetPath);
            map[source] = generatedMat;
            generated++;
        }

        AssetDatabase.SaveAssets();

        // Reassign renderer material slots, wrapped in Undo.
        int renderersUpdated = 0;
        foreach (Renderer r in renderers)
        {
            Material[] slots = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null && map.TryGetValue(slots[i], out Material replacement) && replacement != slots[i])
                {
                    slots[i] = replacement;
                    changed = true;
                }
            }
            if (changed)
            {
                Undo.RecordObject(r, "Apply Hybrid Cross Section");
                r.sharedMaterials = slots;
                EditorUtility.SetDirty(r);
                renderersUpdated++;
            }
        }

        bool managerCreated = EnsureManager();

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

        string message =
            $"Hybrid Cross Section applied.\n\n" +
            $"Materials generated: {generated}\n" +
            $"Materials reused (already existed): {reused}\n" +
            $"Materials skipped (already cross-section): {skipped}\n" +
            $"Renderers updated: {renderersUpdated}\n" +
            $"CrossSectionManager: {(managerCreated ? "created" : "already present")}";
        EditorUtility.DisplayDialog("Cross Section Applier", message, "OK");
    }

    // --- Material construction ------------------------------------------------------------

    static Material BuildCrossSectionMaterial(Material source, Shader crossSectionShader, Color sectionColor)
    {
        Material mat = new Material(crossSectionShader) { name = $"{SanitizeName(source.name)}{GeneratedSuffix}" };

        // Base color (falls back to _Color for older/standard sources).
        if (source.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", source.GetColor("_BaseColor"));
        else if (source.HasProperty("_Color")) mat.SetColor("_BaseColor", source.GetColor("_Color"));

        // Albedo map (falls back to _MainTex).
        Texture albedo = null;
        if (source.HasProperty("_BaseMap")) albedo = source.GetTexture("_BaseMap");
        if (albedo == null && source.HasProperty("_MainTex")) albedo = source.GetTexture("_MainTex");
        if (albedo != null)
        {
            mat.SetTexture("_BaseMap", albedo);
            if (source.HasProperty("_BaseMap"))
            {
                mat.SetTextureScale("_BaseMap", source.GetTextureScale("_BaseMap"));
                mat.SetTextureOffset("_BaseMap", source.GetTextureOffset("_BaseMap"));
            }
        }

        // Metallic / smoothness scalars.
        foreach (string prop in CopiedFloats)
        {
            if (source.HasProperty(prop)) mat.SetFloat(prop, source.GetFloat(prop));
        }

        // Carry over surface type + blend state so a transparent source stays transparent.
        CopySurfaceSettings(source, mat);

        // Cross-section face color from the SDK reference material.
        mat.SetColor("_SectionColor", sectionColor);

        // The section-color faces are the mesh BACK faces (see LitForwardPass.hlsl). They only
        // render with culling OFF. The shader's default _Cull is 2 (Back), and it's HideInInspector
        // so there's no UI to change it — hence a freshly-created material shows no section color.
        // Force it here (and make sure the section-color keyword is on).
        EnsureSectionColorVisible(mat);
        return mat;
    }

    /// <summary>
    /// Copies surface/transparency state from source to target so transparent materials survive
    /// the shader swap. Both CrossSection/Standard and URP/Lit share these property names.
    /// </summary>
    static void CopySurfaceSettings(Material source, Material target)
    {
        string[] surfaceFloats =
        {
            "_Surface", "_Blend", "_SrcBlend", "_DstBlend", "_SrcBlendAlpha", "_DstBlendAlpha",
            "_ZWrite", "_AlphaClip", "_Cutoff", "_BlendModePreserveSpecular", "_AlphaToMask"
        };
        foreach (string prop in surfaceFloats)
        {
            if (source.HasProperty(prop) && target.HasProperty(prop))
                target.SetFloat(prop, source.GetFloat(prop));
        }

        // Mirror the surface-related keywords Unity uses to branch blending / alpha test.
        string[] surfaceKeywords = { "_SURFACE_TYPE_TRANSPARENT", "_ALPHATEST_ON", "_ALPHAPREMULTIPLY_ON" };
        foreach (string kw in surfaceKeywords)
        {
            if (source.IsKeywordEnabled(kw)) target.EnableKeyword(kw);
            else target.DisableKeyword(kw);
        }

        // Keep the render queue (transparent materials sort in the Transparent range).
        target.renderQueue = source.renderQueue;
    }

    /// <summary>Forces culling off and enables the section-color keyword so the cut face renders.</summary>
    static void EnsureSectionColorVisible(Material mat)
    {
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f); // 0 = Off (double-sided)
        if (mat.HasProperty("_useSectionColor")) mat.SetFloat("_useSectionColor", 1f);
        mat.EnableKeyword("USE_SECTION_COLOR");
        mat.doubleSidedGI = true;
    }

    /// <summary>Reads _SectionColor from the SDK's CrossSectionMaterial.mat; falls back to red.</summary>
    static Color GetReferenceSectionColor()
    {
        string[] guids = AssetDatabase.FindAssets("CrossSectionMaterial t:Material");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material refMat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (refMat != null && refMat.HasProperty("_SectionColor"))
            {
                return refMat.GetColor("_SectionColor");
            }
        }
        return new Color(1f, 0f, 0f, 1f);
    }

    // --- Scene / asset helpers ------------------------------------------------------------

    static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder(MaterialsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Anatomy Materials");
        }
    }

    /// <summary>Creates a CrossSectionManager GameObject if none exists. Returns true if created.</summary>
    static bool EnsureManager()
    {
#if UNITY_2023_1_OR_NEWER
        CrossSectionManager existing = Object.FindFirstObjectByType<CrossSectionManager>(FindObjectsInactive.Include);
#else
        CrossSectionManager existing = Object.FindObjectOfType<CrossSectionManager>(true);
#endif
        if (existing != null) return false;

        GameObject go = new GameObject("CrossSectionManager");
        go.AddComponent<CrossSectionManager>();
        Undo.RegisterCreatedObjectUndo(go, "Create CrossSectionManager");
        return true;
    }

    static string SanitizeName(string name)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}
