using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class SubmeshSplitter : EditorWindow
{
    [MenuItem("SMMARTS/Anatomy Import 2: Split into Submeshes (optional)")]
    static void Init()
    {
        SubmeshSplitter window = (SubmeshSplitter)EditorWindow.GetWindow(typeof(SubmeshSplitter));
        window.titleContent = new GUIContent("Submesh Splitter");
        window.minSize = new Vector2(400, 200);
        window.Show();
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Submesh Splitter", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        GameObject selected = Selection.activeGameObject;

        if (selected == null)
        {
            EditorGUILayout.HelpBox("Select a GameObject with a MeshFilter to split submeshes.", MessageType.Info);
            return;
        }

        MeshFilter meshFilter = selected.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            EditorGUILayout.HelpBox("Selected object must have a MeshFilter with a valid mesh.", MessageType.Warning);
            return;
        }

        Mesh mesh = meshFilter.sharedMesh;
        int submeshCount = mesh.subMeshCount;

        EditorGUILayout.LabelField($"Selected: {selected.name}");
        EditorGUILayout.LabelField($"Submeshes found: {submeshCount}");

        if (submeshCount <= 1)
        {
            EditorGUILayout.HelpBox("This mesh only has one submesh. Nothing to split.", MessageType.Info);
            return;
        }

        // Show submesh info
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Submesh Details:", EditorStyles.boldLabel);

        MeshRenderer renderer = selected.GetComponent<MeshRenderer>();
        Material[] materials = renderer?.sharedMaterials ?? new Material[0];

        for (int i = 0; i < submeshCount; i++)
        {
            string materialName = i < materials.Length && materials[i] != null ?
                materials[i].name : "No Material";
            int triangleCount = mesh.GetTriangles(i).Length / 3;
            EditorGUILayout.LabelField($"  Submesh {i}: {materialName} ({triangleCount} triangles)");
        }

        EditorGUILayout.Space();

        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Split Into Sibling Objects", GUILayout.Height(30)))
        {
            SplitSubmeshes(selected, mesh, materials);
        }
        GUI.backgroundColor = Color.white;
    }

    void SplitSubmeshes(GameObject originalObject, Mesh originalMesh, Material[] materials)
    {
        Vector3[] vertices = originalMesh.vertices;
        Vector3[] normals = originalMesh.normals;
        Vector2[] uv = originalMesh.uv;

        List<GameObject> createdObjects = new List<GameObject>();

        for (int submeshIndex = 0; submeshIndex < originalMesh.subMeshCount; submeshIndex++)
        {
            int[] submeshTriangles = originalMesh.GetTriangles(submeshIndex);

            if (submeshTriangles.Length == 0) continue;

            // Find unique vertices used by this submesh
            HashSet<int> usedVertices = new HashSet<int>();
            foreach (int vertexIndex in submeshTriangles)
            {
                usedVertices.Add(vertexIndex);
            }

            // Create vertex mapping
            List<int> vertexList = new List<int>(usedVertices);
            Dictionary<int, int> vertexMap = new Dictionary<int, int>();
            for (int i = 0; i < vertexList.Count; i++)
            {
                vertexMap[vertexList[i]] = i;
            }

            // Extract vertices, normals, UVs for this submesh
            Vector3[] newVertices = new Vector3[vertexList.Count];
            Vector3[] newNormals = normals.Length > 0 ? new Vector3[vertexList.Count] : null;
            Vector2[] newUV = uv.Length > 0 ? new Vector2[vertexList.Count] : null;

            for (int i = 0; i < vertexList.Count; i++)
            {
                int originalIndex = vertexList[i];
                newVertices[i] = vertices[originalIndex];
                if (newNormals != null) newNormals[i] = normals[originalIndex];
                if (newUV != null) newUV[i] = uv[originalIndex];
            }

            // Remap triangle indices
            int[] newTriangles = new int[submeshTriangles.Length];
            for (int i = 0; i < submeshTriangles.Length; i++)
            {
                newTriangles[i] = vertexMap[submeshTriangles[i]];
            }

            // Create new mesh
            Mesh newMesh = new Mesh();
            newMesh.name = $"{originalMesh.name}_Submesh_{submeshIndex}";
            newMesh.vertices = newVertices;
            if (newNormals != null) newMesh.normals = newNormals;
            if (newUV != null) newMesh.uv = newUV;
            newMesh.triangles = newTriangles;

            if (newNormals == null) newMesh.RecalculateNormals();
            newMesh.RecalculateBounds();

            // Create GameObject as sibling of original (same parent)
            string materialName = submeshIndex < materials.Length && materials[submeshIndex] != null ?
                materials[submeshIndex].name : $"Submesh_{submeshIndex}";

            GameObject newObject = new GameObject(materialName);
            newObject.transform.SetParent(originalObject.transform.parent, false);
            newObject.transform.position = originalObject.transform.position;
            newObject.transform.rotation = originalObject.transform.rotation;
            newObject.transform.localScale = originalObject.transform.localScale;

            // Add components
            MeshFilter newMeshFilter = newObject.AddComponent<MeshFilter>();
            newMeshFilter.sharedMesh = newMesh;

            MeshRenderer newRenderer = newObject.AddComponent<MeshRenderer>();
            if (submeshIndex < materials.Length && materials[submeshIndex] != null)
            {
                newRenderer.sharedMaterial = materials[submeshIndex];
            }

            // Save mesh as asset in Split Meshes folder
            string folderPath = "Assets/Split Meshes";
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                AssetDatabase.CreateFolder("Assets", "Split Meshes");
            }

            string meshPath = $"{folderPath}/{newMesh.name}.asset";
            AssetDatabase.CreateAsset(newMesh, meshPath);

            createdObjects.Add(newObject);
        }

        AssetDatabase.SaveAssets();

        // Disable the original mesh renderer so you only see the split parts
        MeshRenderer originalRenderer = originalObject.GetComponent<MeshRenderer>();
        if (originalRenderer != null)
        {
            originalRenderer.enabled = false;
        }

        EditorUtility.DisplayDialog("Split Complete",
            $"Successfully split {originalMesh.subMeshCount} submeshes into sibling objects.\n\nOriginal mesh renderer disabled.\nMesh assets saved to 'Split Meshes' folder.",
            "OK");
    }
}