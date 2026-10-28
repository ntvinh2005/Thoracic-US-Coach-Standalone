using SMMARTS;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural Pulsing Artery System
/// 
/// Creates a realistic arterial pulse by generating multiple offset versions of the original artery mesh
/// and cycling through them to simulate cardiac rhythm. The system duplicates the original GameObject
/// to preserve all components (UltrasoundVisibility, materials, etc.) while only modifying the mesh geometry.
/// 
/// The pulse pattern mimics real cardiac cycles:
/// - Quick systolic expansion (20% of cycle) - jumps to maximum size
/// - Gradual diastolic relaxation (80% of cycle) - slowly returns to baseline
/// 
/// Perfect for medical simulations where realistic vascular pulsation is needed for training.
/// </summary>
public class ProceduralPulsingArtery : MonoBehaviour
{
    [Header("Pulse Settings")]
    [Tooltip("Heart rate in beats per minute - adjustable in real-time")]
    public float heartRate = 60f;

    [Tooltip("Maximum radial expansion in millimeters")]
    public float maxOffset = 0.6f; // Maximum radial expansion (mm)

    [Tooltip("Number of intermediate mesh steps for smooth animation")]
    public int meshCount = 5; // Number of intermediate meshes

    // Array of generated GameObject copies with different mesh sizes
    private GameObject[] generatedMeshes;

    // Original mesh from this GameObject's MeshFilter - used as base for all offsets
    private Mesh originalMesh;

    // Tracks which mesh is currently visible to avoid unnecessary switching
    private int currentMeshIndex = 0;

    // These fields make ProceduralPulsingArtery work its own IdentifiableObject magic, because we're special:
    [Header("Highlight Management")]
    private List<IdentifiablePulsingArtery> childArteries = new List<IdentifiablePulsingArtery>();
    private Coroutine coordinatedHighlightCoroutine;
    private bool isCoordinatedHighlightActive = false;

    /// <summary>
    /// Initialize the pulsing system on startup
    /// Captures the original mesh and generates all offset versions
    /// </summary>
    void Start()
    {
        // Null check for empty mesh.
        // This can be the case for a mesh generated at runtime, like from Dynamic Tubular Mesh.
        Mesh mesh = GetComponent<MeshFilter>().mesh;
        if (mesh == null || mesh.vertexCount == 0)
        {
            Invoke("Start", 0.5f); // wait half a second and try again.
            return;
        }

        // Store reference to the original mesh before we start duplicating
        originalMesh = GetComponent<MeshFilter>().mesh;

        // Generate all the offset mesh variants
        GenerateOffsetMeshes();

        // Start with the smallest (baseline) mesh visible
        ShowOnlyMesh(0);
    }

    /// <summary>
    /// Main animation loop - calculates which mesh should be visible based on cardiac timing
    /// Uses realistic heart rhythm: quick expansion followed by gradual relaxation
    /// </summary>
    void Update()
    {
        // null check in case initialization has not finished
        if (generatedMeshes == null) return;

        // Convert heart rate (BPM) to frequency and get current position in cardiac cycle
        float time = Time.time * (heartRate / 60f);
        float cyclePosition = time % 1f; // 0 to 1 representing one complete heartbeat

        int targetMesh;

        // Systolic phase: Quick expansion (20% of cardiac cycle)
        if (cyclePosition < 0.2f)
        {
            targetMesh = meshCount - 1; // Jump immediately to maximum expansion
        }
        // Diastolic phase: Gradual relaxation (80% of cardiac cycle)
        else
        {
            // Calculate how far through the relaxation phase we are (0 to 1)
            float relaxationProgress = (cyclePosition - 0.2f) / 0.8f;

            // Gradually decrease from max size back to baseline
            targetMesh = Mathf.FloorToInt((1f - relaxationProgress) * (meshCount - 1));
        }

        // Safety clamp to prevent array index errors
        targetMesh = Mathf.Clamp(targetMesh, 0, meshCount - 1);

        // Only switch meshes when necessary to avoid redundant SetActive calls
        if (targetMesh != currentMeshIndex)
        {
            ShowOnlyMesh(targetMesh);
            currentMeshIndex = targetMesh;
        }
    }

    /// <summary>
    /// Generates all offset mesh variants by duplicating the original GameObject
    /// This preserves all components (UltrasoundVisibility, materials, scripts, etc.)
    /// while creating different sized versions of the artery mesh
    /// </summary>
    void GenerateOffsetMeshes()
    {
        generatedMeshes = new GameObject[meshCount];

        // First loop: Create and configure all meshes WITHOUT parenting
        // (Parenting during instantiation would create exponential child growth)
        for (int i = 0; i < meshCount; i++)
        {
            // Calculate offset distance for this mesh (0 to maxOffset)
            float offsetDistance = (float)i / (meshCount - 1) * maxOffset;

            // Duplicate the entire GameObject with all its components intact
            GameObject meshObj = Instantiate(gameObject);
            meshObj.name = $"ArteryMesh_{i}_offset_{offsetDistance:F2}mm";

            // Remove the ProceduralPulsingArtery script from copies to prevent recursion
            ProceduralPulsingArtery pulsingScript = meshObj.GetComponent<ProceduralPulsingArtery>();
            if (pulsingScript != null)
                DestroyImmediate(pulsingScript);

            // Get references to the mesh components we need to modify
            MeshFilter meshFilter = meshObj.GetComponent<MeshFilter>();
            MeshCollider meshCollider = meshObj.GetComponent<MeshCollider>();

            // Generate the offset mesh and apply it to both visual and collision
            Mesh offsetMesh = OffsetMeshRadially(originalMesh, offsetDistance);
            meshFilter.mesh = offsetMesh;
            meshCollider.sharedMesh = offsetMesh;

            // Store reference for later activation/deactivation
            generatedMeshes[i] = meshObj;
        }

        // Second loop: Parent all the meshes after they're fully configured
        // and ensure their mesh renderer and collider components are on
        for (int i = 0; i < generatedMeshes.Length; i++)
        {
            generatedMeshes[i].transform.SetParent(transform);
            generatedMeshes[i].transform.localPosition = Vector3.zero;
            generatedMeshes[i].transform.localRotation = Quaternion.identity;
            generatedMeshes[i].transform.localScale = Vector3.one;
            generatedMeshes[i].GetComponent<MeshRenderer>().enabled = true;
            generatedMeshes[i].GetComponent<MeshCollider>().enabled = true;
        }

        // Disable original's mesh collider and ultrasound visibility since children handle rendering now
        GetComponent<MeshCollider>().enabled = false;
        GetComponent<UltrasoundVisibility>().enabled = false;
        GetComponent<MeshRenderer>().enabled = false;

        // Add this line at the very end of GenerateOffsetMeshes(), after the existing code:
        ConvertToIdentifiablePulsingArteries();
    }

    /// <summary>
    /// Controls which mesh variant is currently visible
    /// Only one mesh should be active at a time to create the pulsing effect
    /// </summary>
    /// <param name="index">Index of the mesh to show (0 = smallest, meshCount-1 = largest)</param>
    void ShowOnlyMesh(int index)
    {
        for (int i = 0; i < generatedMeshes.Length; i++)
        {
            if (generatedMeshes[i] != null)
            {
                generatedMeshes[i].SetActive(i == index);
            }
        }
    }

    /// <summary>
    /// Creates a radially expanded version of a mesh by moving vertices along their normals
    /// Works well for tubular geometries like arteries where normals point outward
    /// 
    /// Note: This assumes mesh normals are properly oriented outward from the vessel surface
    /// May cause self-intersection if offset distance is too large for sharp internal angles
    /// </summary>
    /// <param name="originalMesh">Source mesh to offset</param>
    /// <param name="offsetDistance">Distance to move vertices along normals (in mm)</param>
    /// <returns>New mesh with vertices moved outward</returns>
    Mesh OffsetMeshRadially(Mesh originalMesh, float offsetDistance)
    {
        // Create a copy to avoid modifying the original
        Mesh offsetMesh = Instantiate(originalMesh);

        // Get vertex and normal arrays (these are copies, safe to modify)
        Vector3[] vertices = offsetMesh.vertices;
        Vector3[] normals = offsetMesh.normals;

        // Move each vertex outward along its normal vector
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] += normals[i] * offsetDistance;
        }

        // Apply the modified vertices back to the mesh
        offsetMesh.vertices = vertices;

        // Recalculate bounding box for proper rendering and culling
        offsetMesh.RecalculateBounds();

        return offsetMesh;
    }


    /// <summary>
    /// Register a child artery with the coordinator
    /// Called by IdentifiablePulsingArtery.Start()
    /// </summary>
    public void RegisterChildArtery(IdentifiablePulsingArtery childArtery)
    {
        if (!childArteries.Contains(childArtery))
        {
            childArteries.Add(childArtery);
        }
    }

    /// <summary>
    /// Start coordinated highlight on all child variants
    /// Called by any IdentifiablePulsingArtery when touched
    /// </summary>
    public void StartCoordinatedHighlight(Material glowMaterial, float duration)
    {
        // Stop existing highlight if running
        if (coordinatedHighlightCoroutine != null)
        {
            StopCoroutine(coordinatedHighlightCoroutine);
        }

        // Apply highlight to all child variants
        foreach (var child in childArteries)
        {
            if (child != null)
            {
                child.ApplyDirectHighlight(glowMaterial);
            }
        }

        isCoordinatedHighlightActive = true;

        // Start unified timeout
        coordinatedHighlightCoroutine = StartCoroutine(CoordinatedHighlightTimeout(duration));
    }

    /// <summary>
    /// Coordinated highlight timeout coroutine
    /// Removes highlight from all child variants simultaneously
    /// </summary>
    private IEnumerator CoordinatedHighlightTimeout(float duration)
    {
        yield return new WaitForSeconds(duration);

        // Remove highlight from all child variants
        foreach (var child in childArteries)
        {
            if (child != null)
            {
                child.RemoveDirectHighlight();
            }
        }

        isCoordinatedHighlightActive = false;
        coordinatedHighlightCoroutine = null;

    }

    /// <summary>
    /// Cleanup coordinated highlight if object is destroyed
    /// </summary>
    void OnDestroy()
    {
        if (coordinatedHighlightCoroutine != null)
        {
            StopCoroutine(coordinatedHighlightCoroutine);
        }
    }

    // Add this method to ProceduralPulsingArtery:

    /// <summary>
    /// Convert IdentifiableObject components to IdentifiablePulsingArtery on child meshes
    /// Copies all properties and removes original to prevent conflicts
    /// </summary>
    void ConvertToIdentifiablePulsingArteries()
    {
        // Handle the parent's IdentifiableObject (if it exists)
        IdentifiableObject parentIdObj = GetComponent<IdentifiableObject>();
        if (parentIdObj != null)
        {

            // Transfer properties to all child meshes
            foreach (GameObject meshObj in generatedMeshes)
            {
                IdentifiableObject childIdObj = meshObj.GetComponent<IdentifiableObject>();
                if (childIdObj != null)
                {
                    // Replace IdentifiableObject with IdentifiablePulsingArtery
                    IdentifiablePulsingArtery pulsingArtery = meshObj.AddComponent<IdentifiablePulsingArtery>();

                    // Copy all the key properties
                    CopyIdentifiableProperties(childIdObj, pulsingArtery);

                    // Remove the old component
                    DestroyImmediate(childIdObj);
                }
            }

            // Disable the parent's IdentifiableObject (properties have been transferred)
            parentIdObj.enabled = false;
        }
    }

    /// <summary>
    /// Copy properties from IdentifiableObject to IdentifiablePulsingArtery
    /// </summary>
    void CopyIdentifiableProperties(IdentifiableObject source, IdentifiablePulsingArtery destination)
    {
        // Copy the key properties that developers configure
        destination.TouchDisplayMessage = source.TouchDisplayMessage;
        destination.EnableTouchDisplayMessage = source.EnableTouchDisplayMessage;
        destination.Color = source.Color;
        destination.Type = source.Type;
        // Add any other properties that need copying
    }

}