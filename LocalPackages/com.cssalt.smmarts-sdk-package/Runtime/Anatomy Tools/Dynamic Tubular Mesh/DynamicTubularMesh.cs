using System;
using System.Collections.Generic;
using UnityEngine;
// ============================================================
// DynamicTubularMesh.cs
// ============================================================
// Built upon "TubeRenderer.cs" by NYC Unity Developer Ray Nothnagel (2008).
// Original script is free for use and available on the Unify Wiki.
//
// Modifications by Dave Lizdas, Sept 2018:
//   - Removed vertex colors.
//   - Removed LOD-style flat plane rendering for distant cameras.
//   - Added MeshCollider support.
//
// Further additions:
//   - TubeLength_mm: computed length of the tube in millimeters (1 Unity unit = 1mm).
//   - RegenerateInUpdate: optional per-frame regeneration flag.
//   - EndcapAtFirstWaypoint / EndcapAtLastWaypoint: optional flat polygon endcaps.
//   - Centerline data exposure: world positions and radii for compression system integration.
// ============================================================
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshCollider))]
public class DynamicTubularMesh : MonoBehaviour
{
    // ============================================================
    // INNER CLASS: VesselSegment
    // ============================================================
    // Represents a single cross-sectional ring of the tube.
    // Each segment knows its position and radius, and holds
    // references to its neighbors in a doubly-linked chain.
    // The chain is constructed in CreateChainOfSegments() but
    // mesh generation iterates the Segments[] array directly.
    // ============================================================
    public class VesselSegment
    {
        /// <summary>Radius of the tube at this segment (in mm).</summary>
        public float Radius = 1.0f;
        /// <summary>Position of this segment in local space.</summary>
        public Vector3 CenterPoint = Vector3.zero;
        // Linked-list neighbors (constructed but not used by mesh generation)
        VesselSegment NextSegment = null;
        VesselSegment LastSegment = null;
        /// <summary>Sets the next segment in the chain and returns this segment.</summary>
        public VesselSegment ChainNext(VesselSegment next)
        {
            NextSegment = next;
            return this;
        }
        /// <summary>Sets the previous segment in the chain.</summary>
        public void ChainLast(VesselSegment last)
        {
            LastSegment = last;
        }
    }
    // ============================================================
    // INSPECTOR VARIABLES
    // ============================================================
    /// <summary>
    /// Enable to dynamically inject extra waypoints for smoother curves.
    /// </summary>
    public bool EnableInterpolation = false;

    /// <summary>
    ///  If interpolation is enabled, this value defines the maximum allowed distance (in mm) between consecutive waypoints.
    ///  If the distance between any two waypoints exceeds this threshold, additional waypoints will be automatically
    ///  inserted at runtime to maintain a smooth tube shape.
    /// </summary>
    [Range(0.1f, 20f)]
    [SerializeField] private float MaximumWaypointDistance_mm = 20f;
    /// <summary>
    /// The total number of waypoints currently defining the tube's centerline.
    /// </summary>
    [SerializeField] private int WaypointCount;
    public int TotalWaypointCount => WaypointCount;
    /// <summary>
    /// Number of sides on each cross-sectional polygon ring.
    /// Higher values produce a smoother tube at the cost of performance.
    /// </summary>
    public int PolygonSideCount = 10;
    /// <summary>
    /// The array of segments that define the tube's path and shape.
    /// Populated automatically from child transforms in the hierarchy.
    /// </summary>
    public VesselSegment[] Segments;
    /// <summary>
    /// If selected, this GameObject must be a direct child of ManditoryParent.
    /// If it is not (e.g. it is a procedural duplicate from an external process), the script
    /// will destroy itself on Start to avoid redundant copies.
    /// if ManditoryParent is null this check is skipped.
    /// </summary>
    [SerializeField] private Transform ManditoryParent;
    /// <summary>
    /// The total length of the tube path in millimeters.
    /// Computed as the sum of distances between consecutive segment centers.
    /// (1 Unity unit = 1mm in this project.)
    /// Read-only from outside this class.
    /// </summary>
    [SerializeField] private float tubeLength_mm;
    public float TubeLength_mm => tubeLength_mm;
    /// <summary>
    /// If true, Regenerate() is called every frame in Update().
    /// Useful for tubes whose waypoints move at runtime.
    /// Leave false for static tubes to avoid unnecessary overhead.
    /// </summary>
    public bool RegenerateInUpdate = false;
    /// <summary>
    /// If true, a flat polygon endcap is added at the first waypoint (tube start).
    /// </summary>
    public bool EndcapAtFirstWaypoint = false;
    /// <summary>
    /// If true, a flat polygon endcap is added at the last waypoint (tube end).
    /// </summary>
    public bool EndcapAtLastWaypoint = false;
    // ============================================================
    // CENTERLINE DATA EXPOSURE
    // ============================================================
    /// <summary>
    /// World-space positions of all centerline waypoints.
    /// Updated automatically when segments are rebuilt.
    /// </summary>
    private Vector3[] worldPositions;
    /// <summary>
    /// Gets the centerline positions in world space.
    /// Returns null if there are fewer than 2 waypoints.
    /// IMPORTANT: Returns direct reference to internal array - do not modify!
    /// Connectivity is linear: edges are (0→1, 1→2, 2→3, ..., n-2→n-1).
    /// </summary>
    public Vector3[] CenterlineWorldPositions
    {
        get
        {
            if (worldPositions == null || worldPositions.Length < 2) return null;
            return worldPositions;
        }
    }
    /// <summary>
    /// Gets the radius at each centerline waypoint (in mm).
    /// Returns null if there are fewer than 2 waypoints.
    /// IMPORTANT: Returns direct reference to internal array - do not modify!
    /// Array indices correspond to CenterlineWorldPositions indices.
    /// </summary>
    public float[] CenterlineRadii
    {
        get
        {
            if (Segments == null || Segments.Length < 2) return null;
            float[] radii = new float[Segments.Length];
            for (int i = 0; i < Segments.Length; i++)
            {
                radii[i] = Segments[i].Radius;
            }
            return radii;
        }
    }
    // ============================================================
    // PRIVATE VARIABLES
    // ============================================================
    // The transform whose children define the tube's waypoints.
    // Set to this.transform on Start.
    Transform WaypointSet;
    // A precomputed unit circle of points used to form each cross-sectional ring.
    // Recomputed only when PolygonSideCount changes.
    Vector3[] circularSetOfPoints;
    // Tracks the last value of PolygonSideCount used to build circularSetOfPoints,
    // so we only recompute it when necessary.
    int lastCrossSegments;
    // The vertex and triangle arrays used to construct the mesh each generation.
    Vector3[] MeshVertices;
    int[] Triangles;
    // A list to track procedural waypoint GameObjects created during interpolation,
    // so they can be cleaned up before the next generation.
    private List<GameObject> proceduralWaypoints = new List<GameObject>();
    // ============================================================
    // UNITY LIFECYCLE
    // ============================================================
    void Start()
    {
        // If this GameObject is not a direct child of ManditoryParent, it is
        // assumed to be a procedural duplicate and does not need a lumen mapper.
        if (ManditoryParent != null)
        {
            if (gameObject.transform.parent != ManditoryParent)
            {
                Destroy(this);
                return;
            }
        }
        WaypointSet = this.transform;
        CreateChainOfSegments();
        Regenerate();
    }
    void Update()
    {
        if (RegenerateInUpdate) Regenerate();
    }
    // ============================================================
    // PUBLIC METHODS
    // ============================================================
    /// <summary>
    /// Rebuilds the segment chain and regenerates the tube mesh and collider.
    /// Call this after modifying waypoints at runtime.
    /// </summary>
    public void Regenerate()
    {
        CreateChainOfSegments();
        CreateTubeShapedMeshOverSegmentChain();
    }
    /// <summary>
    /// Replaces the mesh and collider with empty meshes, effectively hiding the tube.
    /// </summary>
    public void ClearMesh()
    {
        Mesh emptyMesh = new Mesh();
        GetComponent<MeshFilter>().mesh = emptyMesh;
        GetComponent<MeshCollider>().sharedMesh = emptyMesh;
    }
    /// <summary>
    /// Destroys all child waypoint GameObjects from the WaypointSet.
    /// </summary>
    public void DeleteWaypoints()
    {
        foreach (GameObject child in WaypointSet)
            Destroy(child);
    }
    // ============================================================
    // PRIVATE METHODS
    // ============================================================
    /// <summary>
    /// Reads original child transforms of WaypointSet (ignoring previously generated procedural ones) 
    /// to define the base path. If interpolation is enabled, dynamically injects extra waypoints 
    /// to ensure no two points exceed the maximum allowed distance. 
    /// Builds the final Segments array, populates world-space centerline data for external scripts, 
    /// links segments into a doubly-linked chain, and computes the total tube length.
    /// </summary>
    void CreateChainOfSegments()
    {
        if (WaypointSet == null) return;
        if (WaypointSet.childCount == 0) return;

        // 1. CLEANUP
        foreach (GameObject obj in proceduralWaypoints) { if(obj != null) DestroyImmediate(obj); }
        proceduralWaypoints.Clear();

        List<VesselSegment> dynamicSegments = new List<VesselSegment>();
        
        // 2. HARVEST ORIGINALS
        List<Transform> originalWaypointsList = new List<Transform>();
        foreach (Transform child in WaypointSet)
        {
            if (!child.name.StartsWith("Procedural_Waypoint"))
            {
                originalWaypointsList.Add(child);
                
                MeshRenderer mr = child.gameObject.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;
            }
        }
        
        if (originalWaypointsList.Count == 0) return;
        Transform[] originalWaypoints = originalWaypointsList.ToArray();
        GameObject sphereTemplate = originalWaypoints[0].gameObject;

        for (int i = 0; i < originalWaypoints.Length; i++)
        {
            // Add original segment
            VesselSegment current = new VesselSegment();
            current.Radius = originalWaypoints[i].localScale.x / 2f;
            current.CenterPoint = WaypointSet.InverseTransformPoint(originalWaypoints[i].position);
            dynamicSegments.Add(current);

            // Add interpolated segments if enabled and distance exceeds threshold
            if (EnableInterpolation && i < originalWaypoints.Length - 1)
            {
                Vector3 nextPos = WaypointSet.InverseTransformPoint(originalWaypoints[i + 1].position);
                float nextRadius = originalWaypoints[i + 1].localScale.x / 2f;
                float distance = Vector3.Distance(current.CenterPoint, nextPos);

                if (distance > MaximumWaypointDistance_mm)
                {
                    int extraPoints = Mathf.FloorToInt(distance / MaximumWaypointDistance_mm);
                    for (int j = 1; j <= extraPoints; j++)
                    {
                        float t = (float)j / (extraPoints + 1);
                        VesselSegment interpolated = new VesselSegment();
                        interpolated.CenterPoint = Vector3.Lerp(current.CenterPoint, nextPos, t);
                        interpolated.Radius = Mathf.Lerp(current.Radius, nextRadius, t);
                        dynamicSegments.Add(interpolated);

                        // Procedural Visuals
                        GameObject newSphere = Instantiate(sphereTemplate, WaypointSet);
                        newSphere.name = $"Procedural_Waypoint_{i}_{j}";
                        newSphere.transform.localPosition = interpolated.CenterPoint;
                        newSphere.transform.localScale = Vector3.one * (interpolated.Radius * 2f);
                        
                        MeshRenderer procMr = newSphere.GetComponent<MeshRenderer>();
                        if (procMr != null) procMr.enabled = false;

                        proceduralWaypoints.Add(newSphere);
                    }
                }
            }
        }

        // 3. FINALIZE ARRAYS
        Segments = dynamicSegments.ToArray();

        // Populate the worldPositions array
        worldPositions = new Vector3[Segments.Length];
        for (int i = 0; i < Segments.Length; i++)
        {
            worldPositions[i] = transform.TransformPoint(Segments[i].CenterPoint);
        }

        // Link segments into a doubly-linked chain.
        VesselSegment lastSegment = null;
        foreach (VesselSegment aSegment in Segments)
        {
            if (lastSegment != null)
            {
                aSegment.ChainLast(lastSegment.ChainNext(aSegment));
            }
            lastSegment = aSegment;
        }

        // Compute total tube length
        tubeLength_mm = 0f;
        for (int k = 0; k < Segments.Length - 1; k++)
        {
            tubeLength_mm += Vector3.Distance(Segments[k].CenterPoint, Segments[k + 1].CenterPoint);
        }

        // Update final WaypointCount after interpolation
        WaypointCount = Segments.Length;
    }
    /// <summary>
    /// Builds the tube mesh and mesh collider from the current Segments[] array.
    /// Generates a ring of vertices at each segment, connects them with quads,
    /// and optionally adds flat polygon endcaps at either end.
    /// </summary>
    void CreateTubeShapedMeshOverSegmentChain()
    {
        if (WaypointSet == null) return;
        if (WaypointSet.childCount == 0) return;
        if (null == Segments || Segments.Length <= 1)
        {
            GetComponent<Renderer>().enabled = false;
            return;
        }
        GetComponent<Renderer>().enabled = true;
        // Recompute the unit circle template only if PolygonSideCount has changed.
        if (PolygonSideCount != lastCrossSegments)
        {
            circularSetOfPoints = new Vector3[PolygonSideCount];
            float theta = 2.0f * Mathf.PI / PolygonSideCount;
            for (int c = 0; c < PolygonSideCount; c++)
            {
                circularSetOfPoints[c] = new Vector3(Mathf.Cos(theta * c), Mathf.Sin(theta * c), 0);
            }
            lastCrossSegments = PolygonSideCount;
        }
        // Allocate vertex array based on total segments
        MeshVertices = new Vector3[Segments.Length * PolygonSideCount];
        Triangles = new int[(Segments.Length - 1) * PolygonSideCount * 6]; // Changed Segments.Length to Segments.Length - 1

        int[] lastVertices = new int[PolygonSideCount];
        int[] theseVertices = new int[PolygonSideCount];
        // Initialize "Up" rotation to identity; it will be updated for each segment to orient the rings correctly.
        Vector3 currentUp = Vector3.up;
        Quaternion rotation = Quaternion.identity;
        // For each segment, place a ring of vertices and connect it to the previous ring.
        for (int p = 0; p < Segments.Length; p++)
        {
            Vector3 forwardDir = Vector3.forward;
            // Calculate the Forward direction
            if (p < Segments.Length - 1)
                forwardDir = (Segments[p + 1].CenterPoint - Segments[p].CenterPoint).normalized;
            else if (p > 0) 
                forwardDir = (Segments[p].CenterPoint - Segments[p - 1].CenterPoint).normalized; // Final segment keeps previous direction

            // Prevent errors if waypoints are exactly on top of each other
            if (forwardDir != Vector3.zero)
            {
                // LookRotation aligns Forward, while forcing the Roll to match currentUp
                rotation = Quaternion.LookRotation(forwardDir, currentUp);
                currentUp = rotation * Vector3.up; 
            }
            // Place ring vertices.
            for (int c = 0; c < PolygonSideCount; c++)
            {
                int vertexIndex = p * PolygonSideCount + c;
                MeshVertices[vertexIndex] = Segments[p].CenterPoint + (rotation * circularSetOfPoints[c] * Segments[p].Radius);
                lastVertices[c] = theseVertices[c];
                theseVertices[c] = vertexIndex;
            }
            // Connect this ring to the previous ring
            if (p > 0)
            {
                for (int c = 0; c < PolygonSideCount; c++)
                {
                    int start = (p - 1) * PolygonSideCount * 6 + (c * 6); // Changed p to p-1
                    Triangles[start] = lastVertices[c];
                    Triangles[start + 1] = lastVertices[(c + 1) % PolygonSideCount];
                    Triangles[start + 2] = theseVertices[c];
                    Triangles[start + 3] = Triangles[start + 2];
                    Triangles[start + 4] = Triangles[start + 1];
                    Triangles[start + 5] = theseVertices[(c + 1) % PolygonSideCount];
                }
            }
        }

        // --------------------------------------------------------
        // ENDCAPS
        // Each active endcap adds a center vertex plus a copy of
        // the relevant ring's vertices, then fans triangles from
        // the center to each edge. Winding is mirrored between
        // start and end caps so normals face outward on both ends.
        // --------------------------------------------------------
        int capStartVertex = MeshVertices.Length;
        int extraVerts = 0;
        if (EndcapAtFirstWaypoint) extraVerts += 1 + PolygonSideCount;
        if (EndcapAtLastWaypoint) extraVerts += 1 + PolygonSideCount;
        System.Array.Resize(ref MeshVertices, MeshVertices.Length + extraVerts);
        int extraTris = 0;
        if (EndcapAtFirstWaypoint) extraTris += PolygonSideCount * 3;
        if (EndcapAtLastWaypoint) extraTris += PolygonSideCount * 3;
        System.Array.Resize(ref Triangles, Triangles.Length + extraTris);
        int capVertIndex = capStartVertex;
        int capTriIndex = Triangles.Length - extraTris;
        if (EndcapAtFirstWaypoint)
        {
            MeshVertices[capVertIndex] = Segments[0].CenterPoint;
            for (int c = 0; c < PolygonSideCount; c++)
                MeshVertices[capVertIndex + 1 + c] = MeshVertices[c];
            for (int c = 0; c < PolygonSideCount; c++)
            {
                Triangles[capTriIndex + c * 3] = capVertIndex;
                Triangles[capTriIndex + c * 3 + 1] = capVertIndex + 1 + (c + 1) % PolygonSideCount;
                Triangles[capTriIndex + c * 3 + 2] = capVertIndex + 1 + c;
            }
            capVertIndex += 1 + PolygonSideCount;
            capTriIndex += PolygonSideCount * 3;
        }

        if (EndcapAtLastWaypoint)
        {
            MeshVertices[capVertIndex] = Segments[Segments.Length - 1].CenterPoint;
            for (int c = 0; c < PolygonSideCount; c++)
                MeshVertices[capVertIndex + 1 + c] = MeshVertices[(Segments.Length - 1) * PolygonSideCount + c];

            for (int c = 0; c < PolygonSideCount; c++)
            {
                Triangles[capTriIndex + c * 3] = capVertIndex;
                Triangles[capTriIndex + c * 3 + 1] = capVertIndex + 1 + c;
                Triangles[capTriIndex + c * 3 + 2] = capVertIndex + 1 + (c + 1) % PolygonSideCount;
            }
        }

        // Apply vertices and triangles to the render mesh.
        Mesh mesh = GetComponent<MeshFilter>().mesh;
        if (!mesh) mesh = new Mesh();
        // Clear the old mesh data before applying the new resized arrays
        mesh.Clear();
        mesh.vertices = MeshVertices;
        mesh.triangles = Triangles;
        mesh.RecalculateNormals();

        // Apply to mesh collider
        MeshCollider myMC = GetComponent<MeshCollider>();
        if (myMC != null)
        {
            Mesh newMesh = new Mesh();
            newMesh.vertices = MeshVertices;
            newMesh.triangles = Triangles;
            newMesh.RecalculateBounds();
            myMC.sharedMesh = newMesh;
            Debug.Log("REBUILT " + gameObject.name);
        }
    }
}