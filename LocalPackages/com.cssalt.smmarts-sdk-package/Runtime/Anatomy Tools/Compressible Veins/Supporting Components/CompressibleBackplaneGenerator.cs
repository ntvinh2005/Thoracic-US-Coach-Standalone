using SMMARTS;
using System.Collections.Generic;
using UnityEngine;

// ============================================================
// CompressibleBackplaneGenerator.cs
// ============================================================
// Generates collision backplane geometry from DTM vessel centerlines.
// The backplane provides collision constraints for vessel compression,
// preventing over-compression through the vessel walls during ultrasound simulation.
//
// Key Features:
// - Combines multiple DTM vessel centerlines into unified backplane mesh
// - Auto-finds probe markers from UltrasoundManager for dynamic probe direction
// - Generates mesh strips along vessel paths offset by probe direction
// - Updates every frame to track probe movement in real-time
// ============================================================

public class CompressibleBackplaneGenerator : MonoBehaviour
{
    // ============================================================
    // INSPECTOR CONFIGURATION
    // ============================================================

    [Header("Veins created with Dynamic Tubular Mesh")]
    [Tooltip("Manual list of DTM components to generate backplane from.")]
    public List<DynamicTubularMesh> TargetDTMs = new List<DynamicTubularMesh>();

    [Header("Backplane Target")]
    [Tooltip("GameObject that will receive the generated backplane mesh (must have MeshFilter and MeshCollider).")]
    public GameObject BackplaneGameObject;

    [Header("Probe Direction")]
    [Tooltip("Direction from vein toward the probe (will be normalized). Backplane is placed behind the vein, opposite this direction.")]
    public Vector3 probeDirection = new Vector3(0f, -1f, 0f);

    [Tooltip("This Transform will be used for the compression center.")]
    public Transform ProbeTipMarker;

    [Tooltip("The vector from this Transform to ProbeTipMarker will be used for probe direction.")]
    public Transform ProbeBackMarker;

    [Header("Backplane Settings")]
    [Tooltip("Additional offset distance for backplane positioning (mm).")]
    public float epsilon = 1f;

    [Tooltip("Half-length of each thin line segment (world units) in the backplane plane.")]
    public float lineHalfLength = 0.4f;

    // ============================================================
    // PRIVATE DATA STRUCTURES
    // ============================================================

    /// <summary>
    /// Edge connectivity structure for linear vessel paths.
    /// Each edge connects two consecutive waypoints (v1 → v2).
    /// </summary>
    private struct Edge
    {
        public int v1; // First vertex index
        public int v2; // Second vertex index

        public Edge(int v1, int v2) { this.v1 = v1; this.v2 = v2; }
    }

    // Combined centerline data from all DTMs
    private Vector3[] combinedPositions;  // World-space centerline waypoints
    private float[] combinedRadii;        // Radius at each waypoint (mm)
    private Vector3[] backplanePoints;    // Offset points forming collision backplane
    private Edge[] combinedEdges;         // Linear connectivity (0→1, 1→2, etc.)

    // ============================================================
    // UNITY LIFECYCLE
    // ============================================================

    /// <summary>
    /// Auto-discovers probe markers from UltrasoundManager if not manually assigned.
    /// </summary>
    void Start()
    {
        // Auto-find probe markers from UltrasoundManager hierarchy
        if (ProbeTipMarker == null || ProbeBackMarker == null)
        {
            var ultrasoundManager = FindAnyObjectByType<UltrasoundManager>();
            if (ultrasoundManager != null)
            {
                // Search all children for the specific marker GameObjects
                Transform[] children = ultrasoundManager.GetComponentsInChildren<Transform>();

                foreach (Transform child in children)
                {
                    if (child.name == "Face Center Marker" && ProbeTipMarker == null)
                    {
                        ProbeTipMarker = child;
                        Debug.Log($"Auto-assigned ProbeTipMarker: {child.name}");
                    }
                    else if (child.name == "Rear Center Marker" && ProbeBackMarker == null)
                    {
                        ProbeBackMarker = child;
                        Debug.Log($"Auto-assigned ProbeBackMarker: {child.name}");
                    }
                }

                // Warn if markers couldn't be found
                if (ProbeTipMarker == null || ProbeBackMarker == null)
                {
                    Debug.LogWarning($"CompressibleBackplaneGenerator: Could not find all probe markers. " +
                                   $"Found Tip: {ProbeTipMarker != null}, Found Back: {ProbeBackMarker != null}");
                }
            }
            else
            {
                Debug.LogWarning("CompressibleBackplaneGenerator: Could not find UltrasoundManager in scene");
            }
        }
    }

    /// <summary>
    /// Updates probe direction from markers and regenerates backplane mesh every frame.
    /// Real-time updates are necessary because ultrasound probe moves continuously.
    /// </summary>
    void Update()
    {
        // Update probe direction from marker positions
        if (ProbeTipMarker != null && ProbeBackMarker != null)
        {
            Vector3 dir = ProbeTipMarker.position - ProbeBackMarker.position;
            probeDirection = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.up;
        }

        // Collect and combine DTM centerline data
        CollectDTMData();

        // Generate backplane if we have valid data
        if (combinedPositions != null && combinedPositions.Length >= 2)
        {
            ComputeBackplane();
            GenerateBackplaneMesh();
        }
    }

    // ============================================================
    // CENTERLINE DATA PROCESSING
    // ============================================================

    /// <summary>
    /// Reads DTM waypoints from all target DTMs and combines them into unified centerline arrays.
    /// Creates linear edge connectivity between consecutive waypoints within each DTM.
    /// </summary>
    void CollectDTMData()
    {
        if (TargetDTMs == null || TargetDTMs.Count == 0)
        {
            // Clear arrays if no DTMs available
            combinedPositions = null;
            combinedRadii = null;
            combinedEdges = null;
            return;
        }

        // Count total valid waypoints across all DTMs
        int totalPoints = 0;
        int totalEdges = 0;

        foreach (var dtm in TargetDTMs)
        {
            if (dtm == null) continue;

            var positions = dtm.CenterlineWorldPositions;
            if (positions != null && positions.Length >= 2)
            {
                totalPoints += positions.Length;
                totalEdges += positions.Length - 1; // Linear connectivity: n points = n-1 edges
            }
        }

        if (totalPoints < 2)
        {
            // Not enough valid waypoints to create meaningful backplane
            combinedPositions = null;
            combinedRadii = null;
            combinedEdges = null;
            return;
        }

        // Allocate combined arrays
        combinedPositions = new Vector3[totalPoints];
        combinedRadii = new float[totalPoints];
        combinedEdges = new Edge[totalEdges];

        int posIndex = 0;
        int edgeIndex = 0;

        // Process each DTM and combine data
        foreach (var dtm in TargetDTMs)
        {
            if (dtm == null) continue;

            var positions = dtm.CenterlineWorldPositions;
            var radii = dtm.CenterlineRadii;

            if (positions == null || positions.Length < 2) continue;
            if (radii == null || radii.Length != positions.Length) continue;

            int startIndex = posIndex;

            // Copy positions and radii from this DTM
            for (int i = 0; i < positions.Length; i++)
            {
                combinedPositions[posIndex] = positions[i];
                combinedRadii[posIndex] = radii[i];
                posIndex++;
            }

            // Create linear edges for this DTM (0→1, 1→2, etc.)
            // Each DTM forms its own continuous path, no connections between DTMs
            for (int i = 0; i < positions.Length - 1; i++)
            {
                combinedEdges[edgeIndex] = new Edge(startIndex + i, startIndex + i + 1);
                edgeIndex++;
            }
        }
    }

    // ============================================================
    // BACKPLANE GEOMETRY GENERATION
    // ============================================================

    /// <summary>
    /// Computes backplane points by offsetting centerline positions along probe direction.
    /// Each backplane point = centerline point + (radius + epsilon) * probe direction.
    /// </summary>
    void ComputeBackplane()
    {
        if (combinedPositions == null) return;

        Vector3 dirWorld = probeDirection;
        if (dirWorld.sqrMagnitude < 1e-12f) dirWorld = Vector3.up; // Fallback direction
        dirWorld.Normalize();

        backplanePoints = new Vector3[combinedPositions.Length];
        for (int i = 0; i < combinedPositions.Length; i++)
        {
            // Offset each centerline point by its radius plus epsilon
            float d = combinedRadii[i] + epsilon;
            backplanePoints[i] = combinedPositions[i] + dirWorld * d;
        }
    }

    /// <summary>
    /// Generates mesh geometry for the backplane by creating quad strips along each edge.
    /// Each edge becomes a thin rectangular strip perpendicular to the probe direction.
    /// </summary>
    void GenerateBackplaneMesh()
    {
        // Validate required components
        if (BackplaneGameObject == null)
        {
            Debug.LogWarning("CompressibleBackplaneGenerator: No BackplaneGameObject assigned!");
            return;
        }

        if (backplanePoints == null || backplanePoints.Length == 0 ||
            combinedEdges == null || combinedEdges.Length == 0)
        {
            Debug.LogWarning("CompressibleBackplaneGenerator: No valid backplane data to generate mesh.");
            return;
        }

        var meshFilter = BackplaneGameObject.GetComponent<MeshFilter>();
        var meshCollider = BackplaneGameObject.GetComponent<MeshCollider>();

        if (meshFilter == null)
        {
            Debug.LogError("CompressibleBackplaneGenerator: BackplaneGameObject missing MeshFilter component!");
            return;
        }

        if (meshCollider == null)
        {
            Debug.LogError("CompressibleBackplaneGenerator: BackplaneGameObject missing MeshCollider component!");
            return;
        }

        // Create new mesh with 32-bit indices for large vertex counts
        Mesh mesh = new Mesh
        {
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var normals = new List<Vector3>();

        // Setup coordinate system for mesh generation
        Vector3 nWorld = probeDirection;
        if (nWorld == Vector3.zero) nWorld = Vector3.up;
        nWorld.Normalize();

        float halfLen = lineHalfLength;
        Vector3 lastSideWorld = Vector3.zero;
        bool hasLastSide = false;

        // Generate mesh quads along each edge
        foreach (var edge in combinedEdges)
        {
            // Validate edge indices
            if (edge.v1 < 0 || edge.v1 >= backplanePoints.Length ||
                edge.v2 < 0 || edge.v2 >= backplanePoints.Length) continue;

            Vector3 p1World = backplanePoints[edge.v1];
            Vector3 p2World = backplanePoints[edge.v2];
            Vector3 tangentWorld = p2World - p1World;

            if (tangentWorld.sqrMagnitude < 1e-8f) continue; // Skip zero-length edges
            tangentWorld.Normalize();

            // Calculate perpendicular direction for strip width
            Vector3 sideWorld = Vector3.Cross(nWorld, tangentWorld).normalized;

            // Maintain consistent side direction to avoid mesh flipping
            if (hasLastSide && Vector3.Dot(sideWorld, lastSideWorld) < 0f)
            {
                sideWorld = -sideWorld;
            }
            lastSideWorld = sideWorld;
            hasLastSide = true;

            // Create quad vertices (thin strip perpendicular to edge)
            Vector3 p1LWorld = p1World - sideWorld * halfLen;
            Vector3 p1RWorld = p1World + sideWorld * halfLen;
            Vector3 p2LWorld = p2World - sideWorld * halfLen;
            Vector3 p2RWorld = p2World + sideWorld * halfLen;

            // Calculate face normal for proper lighting
            Vector3 faceNormalWorld = Vector3.Cross(tangentWorld, sideWorld);
            if (Vector3.Dot(faceNormalWorld, nWorld) < 0f)
                faceNormalWorld = -faceNormalWorld; // Ensure normal faces toward probe

            // Convert to local space relative to BackplaneGameObject
            Vector3 p1LLocal = BackplaneGameObject.transform.InverseTransformPoint(p1LWorld);
            Vector3 p1RLocal = BackplaneGameObject.transform.InverseTransformPoint(p1RWorld);
            Vector3 p2LLocal = BackplaneGameObject.transform.InverseTransformPoint(p2LWorld);
            Vector3 p2RLocal = BackplaneGameObject.transform.InverseTransformPoint(p2RWorld);
            Vector3 faceNormalLocal = BackplaneGameObject.transform.InverseTransformDirection(faceNormalWorld);

            // Add vertices to mesh
            int baseVert = vertices.Count;
            vertices.Add(p1LLocal);
            vertices.Add(p1RLocal);
            vertices.Add(p2LLocal);
            vertices.Add(p2RLocal);

            // Add normals for proper lighting
            normals.Add(faceNormalLocal);
            normals.Add(faceNormalLocal);
            normals.Add(faceNormalLocal);
            normals.Add(faceNormalLocal);

            // Create triangles (two triangles per quad, correct winding order)
            triangles.Add(baseVert + 0);
            triangles.Add(baseVert + 1);
            triangles.Add(baseVert + 2);
            triangles.Add(baseVert + 1);
            triangles.Add(baseVert + 3);
            triangles.Add(baseVert + 2);
        }

        if (vertices.Count == 0)
        {
            Debug.LogWarning("CompressibleBackplaneGenerator: Generated backplane mesh has zero vertices.");
            return;
        }

        // Apply mesh data
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        // Assign to components
        meshFilter.sharedMesh = mesh;
        // NOTE: Commented line below might indicate debugging - consider if null assignment is needed
        // meshCollider.sharedMesh = null;
        meshCollider.sharedMesh = mesh;
        meshCollider.convex = false; // Required for accurate raycast collision

        // Optional debug logging
        // Debug.Log($"Backplane mesh assigned: {mesh.vertexCount} vertices, {mesh.triangles.Length / 3} triangles");
    }

    // ============================================================
    // DEBUG VISUALIZATION (COMMENTED OUT)
    // ============================================================
    /*
    [Header("Debug")]
    [Tooltip("Draw debug gizmos for backplane visualization.")]
    public bool DebugDrawGizmos = false;
    
    void OnDrawGizmosSelected()
    {
        if (!DebugDrawGizmos) return;
        
        // Draw centerlines in green
        if (combinedPositions != null && combinedEdges != null)
        {
            Gizmos.color = Color.green;
            foreach (var edge in combinedEdges)
            {
                if (edge.v1 >= 0 && edge.v1 < combinedPositions.Length &&
                    edge.v2 >= 0 && edge.v2 < combinedPositions.Length)
                {
                    Gizmos.DrawLine(combinedPositions[edge.v1], combinedPositions[edge.v2]);
                }
            }
        }
        
        // Draw backplane in yellow
        if (backplanePoints != null && combinedEdges != null)
        {
            Gizmos.color = Color.yellow;
            foreach (var edge in combinedEdges)
            {
                if (edge.v1 >= 0 && edge.v1 < backplanePoints.Length &&
                    edge.v2 >= 0 && edge.v2 < backplanePoints.Length)
                {
                    Gizmos.DrawLine(backplanePoints[edge.v1], backplanePoints[edge.v2]);
                }
            }
        }
    }
    */
}