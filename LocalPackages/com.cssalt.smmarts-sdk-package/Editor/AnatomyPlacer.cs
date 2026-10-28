using UnityEngine;
using UnityEditor;

public class AnatomyPlacer : EditorWindow
{
    private GameObject placedAnatomy;
    private Transform boundingBoxTransform;

    [MenuItem("SMMARTS/Anatomy Import 1: Place New Anatomy on Index Plate")]
    static void Init()
    {
        AnatomyPlacer window = (AnatomyPlacer)EditorWindow.GetWindow(typeof(AnatomyPlacer));
        window.titleContent = new GUIContent("Anatomy Placer");
        window.minSize = new Vector2(400, 350);
        window.Show();
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Anatomy Placer", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        GameObject selected = Selection.activeGameObject;

        if (selected == null)
        {
            EditorGUILayout.HelpBox("Select a GameObject (FBX) to place under Anatomy.", MessageType.Info);
            placedAnatomy = null;
            return;
        }

        // Find required GameObjects
        GameObject anatomyGO = GameObject.Find("Anatomy");
        if (anatomyGO == null)
        {
            EditorGUILayout.HelpBox("'Anatomy' GameObject not found in scene.", MessageType.Error);
            return;
        }

        boundingBoxTransform = anatomyGO.transform.Find("Bounding Box");
        if (boundingBoxTransform == null)
        {
            EditorGUILayout.HelpBox("'Bounding Box' GameObject not found under Anatomy.", MessageType.Error);
            return;
        }

        MeshFilter boundingBoxMesh = boundingBoxTransform.GetComponent<MeshFilter>();
        if (boundingBoxMesh == null || boundingBoxMesh.sharedMesh == null)
        {
            EditorGUILayout.HelpBox("Bounding Box must have a MeshFilter with a valid mesh.", MessageType.Error);
            return;
        }

        // Calculate current bounds of selected object
        Bounds currentBounds = CalculateBounds(selected);
        if (currentBounds.size == Vector3.zero)
        {
            EditorGUILayout.HelpBox("Selected object has no renderers or bounds.", MessageType.Warning);
            return;
        }

        // Calculate target bounds (cubic)
        Bounds targetBounds = boundingBoxMesh.sharedMesh.bounds;
        Vector3 targetSize = Vector3.Scale(targetBounds.size, boundingBoxTransform.localScale);
        float cubicSize = Mathf.Min(targetSize.x, targetSize.y, targetSize.z);

        // Display info
        EditorGUILayout.LabelField($"Selected: {selected.name}");
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Current Bounds:", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"  Size: {currentBounds.size.x:F2} x {currentBounds.size.y:F2} x {currentBounds.size.z:F2}");
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Target Cubic Box:", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"  Size: {cubicSize:F2} (cube)");
        EditorGUILayout.Space();

        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Place Anatomy", GUILayout.Height(30)))
        {
            PlaceAnatomy(selected, anatomyGO, boundingBoxTransform, cubicSize);
        }
        GUI.backgroundColor = Color.white;

        // Rotation controls (only show if anatomy has been placed)
        if (placedAnatomy != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rotate 90° around axis:", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("X-", GUILayout.Height(25)))
            {
                RotateAnatomy(Vector3.right, -90f);
            }
            if (GUILayout.Button("X+", GUILayout.Height(25)))
            {
                RotateAnatomy(Vector3.right, 90f);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Y-", GUILayout.Height(25)))
            {
                RotateAnatomy(Vector3.up, -90f);
            }
            if (GUILayout.Button("Y+", GUILayout.Height(25)))
            {
                RotateAnatomy(Vector3.up, 90f);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Z-", GUILayout.Height(25)))
            {
                RotateAnatomy(Vector3.forward, -90f);
            }
            if (GUILayout.Button("Z+", GUILayout.Height(25)))
            {
                RotateAnatomy(Vector3.forward, 90f);
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    Bounds CalculateBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }
        return bounds;
    }

    void PlaceAnatomy(GameObject fbx, GameObject anatomyGO, Transform boundingBoxTransform, float cubicSize)
    {
        Undo.RegisterFullObjectHierarchyUndo(fbx, "Place Anatomy");
        Undo.SetTransformParent(fbx.transform, anatomyGO.transform, "Place Anatomy");

        // Step 1: Parent under Anatomy
        fbx.transform.SetParent(anatomyGO.transform, true);

        // Step 2: Calculate bounds and find longest axis
        Bounds bounds = CalculateBounds(fbx);
        Vector3 size = bounds.size;
        Vector3 boundsCenter = bounds.center;

        // Find longest axis
        int longestAxis = 0;
        if (size.y > size.x && size.y > size.z) longestAxis = 1;
        else if (size.z > size.x && size.z > size.y) longestAxis = 2;

        // Step 3: Scale to fit in cubic bounding box
        float maxDimension = Mathf.Max(size.x, size.y, size.z);
        float scaleFactor = cubicSize / maxDimension;
        fbx.transform.localScale = Vector3.one * scaleFactor;

        // Step 4: Position at BoundingBox center (do this before rotation)
        fbx.transform.position = boundingBoxTransform.position;

        // Step 5: Rotate longest axis to Z
        Vector3 longestDir = Vector3.zero;
        longestDir[longestAxis] = 1f;

        // Calculate rotation to align longest axis with Z
        Quaternion alignRotation = Quaternion.FromToRotation(longestDir, Vector3.forward);

        // Apply rotation around the bounds center
        Vector3 pivotOffset = boundsCenter - fbx.transform.position;
        fbx.transform.position = boundsCenter;
        fbx.transform.rotation = alignRotation * fbx.transform.rotation;
        fbx.transform.position = boundsCenter - fbx.transform.rotation * Quaternion.Inverse(alignRotation) * pivotOffset;

        // Recenter at bounding box
        fbx.transform.position = boundingBoxTransform.position;

        placedAnatomy = fbx;
        this.boundingBoxTransform = boundingBoxTransform;

        // Align bottom surfaces
        AlignToBottom();

        Debug.Log($"Placed {fbx.name} under Anatomy. Scale: {scaleFactor:F3}, Longest axis: {longestAxis} aligned to Z");
    }

    void RotateAnatomy(Vector3 axis, float degrees)
    {
        if (placedAnatomy == null) return;

        Undo.RegisterFullObjectHierarchyUndo(placedAnatomy, "Rotate Anatomy");

        // Recalculate bounds center after previous rotations
        Bounds bounds = CalculateBounds(placedAnatomy);
        Vector3 boundsCenter = bounds.center;

        // Move to bounds center, rotate, move back
        Vector3 originalPos = placedAnatomy.transform.position;
        placedAnatomy.transform.position = boundsCenter;
        placedAnatomy.transform.Rotate(axis, degrees, Space.World);
        placedAnatomy.transform.position = originalPos;

        // Align bottom surfaces after rotation
        AlignToBottom();

        Debug.Log($"Rotated {placedAnatomy.name} {degrees}° around {axis}");
    }

    void AlignToBottom()
    {
        if (placedAnatomy == null || boundingBoxTransform == null) return;

        // Get current bounds of placed anatomy
        Bounds modelBounds = CalculateBounds(placedAnatomy);

        // Get bounding box bounds
        MeshFilter boundingBoxMesh = boundingBoxTransform.GetComponent<MeshFilter>();
        Bounds bbMeshBounds = boundingBoxMesh.sharedMesh.bounds;

        // Calculate world space bounds of bounding box
        Vector3 bbCenter = boundingBoxTransform.TransformPoint(bbMeshBounds.center);
        Vector3 bbSize = Vector3.Scale(bbMeshBounds.size, boundingBoxTransform.lossyScale);
        Bounds anatomyBBoxBounds = new Bounds(bbCenter, bbSize);

        // Calculate Y offset to align bottom surfaces
        float yOffset = anatomyBBoxBounds.min.y - modelBounds.min.y;

        // Apply offset
        placedAnatomy.transform.position += new Vector3(0, yOffset, 0);

        Debug.Log($"Aligned to bottom. Y offset: {yOffset:F3}");
    }
}