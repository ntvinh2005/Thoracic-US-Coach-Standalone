using UnityEngine;

public class VeinWallCollapser : MonoBehaviour
{
    /*
    OVERVIEW:
    • VeinWallCollapser generates a pre-collapsed version of a vein mesh at startup
    • Uses wall colliders and push points to define how the vein should collapse
    • Creates two meshes: original (normal vein) and collapsed (compressed vein)
    • Works with VeinCompressionMeshDeformer which lerps between these two states
    
    SETUP WORKFLOW:
    1. Attach this script to the vein gameobject (same object as the mesh)
    2. Create wall colliders (Unity cubes, capsules, etc.) and put them on WallLayerMask layer
    3. Create push point transforms and position them to define compression directions
    4. Set up push points in PushPointTransforms array
    5. Tune CompressionLimit to prevent over-compression
    6. Toggle ShowCollapsedMesh to preview the generated collapsed shape
    7. Add VeinCompressionMeshDeformer component for real-time compression
    
    DEPENDENCIES:
    • MeshFilter (same gameobject) - provides the original vein mesh to collapse
    • Wall colliders - define the surfaces to compress against
    • Push point transforms - define compression directions for different parts of vein
    
    HOW IT WORKS:
    • At startup, finds closest push point for each vertex
    • Shoots raycast from vertex toward wall using push point's forward direction
    • Moves vertex to wall surface (with compression limit to prevent over-flattening)
    • Generates collapsed mesh that looks realistic and avoids triangle overlaps
    • Provides both original and collapsed meshes to VeinCompressionMeshDeformer
    
    ATTACH THIS SCRIPT TO THE VEIN GAMEOBJECT
    */

    [Header("ARTISTIC push directions to PRE-SHAPE a compressed vein")]
    // if you select one of these push points with your Move tool in the unity scene (with local coordinates, not global)
    // then the Push is in the direction of the blue arrow. 
    public Transform[] PushPointTransforms; // Array of push points - position and aim these to define compression directions
    public LayerMask WallLayerMask = -1;   // Layer mask for wall colliders (put your wall objects on this layer)

    [Header("Compression Settings")]
    [Range(0.1f, 1f)]
    public float CompressionLimit = 0.8f;  // Limits compression to prevent triangle overlap (0.1 = barely compress, 1 = full compression)

    [Header("Mesh References - Auto-Found")]
    public MeshFilter TargetMeshFilter;    // The vein mesh to collapse (auto-found from same gameobject)

    [Header("Generated Meshes - Created at Startup")]
    public Mesh OriginalMesh;             // Original vein shape (copy of starting mesh)
    public Mesh CollapsedMesh;            // Generated collapsed shape (compressed against walls)

    [Header("Debug Visualization")]
    public bool ShowCollapsedMesh = false; // Toggle to preview collapsed mesh (set to false for production)

    void Start()
    {
        // AUTO-FIND COMPONENTS: Get mesh filter from same gameobject
        if (TargetMeshFilter == null)
            TargetMeshFilter = GetComponent<MeshFilter>();

        // INITIAL GENERATION: Create collapsed mesh at startup if vein is active
        GenerateCollapsedMeshIfNeeded();
    }

    void OnEnable()
    {
        // ACTIVATION GENERATION: Generate collapsed mesh when vein becomes active
        // This handles cases where veins are toggled on/off (easy/medium/difficult sets)
        // Only generates if not already created - prevents duplicate work
        GenerateCollapsedMeshIfNeeded();
    }

    void Update()
    {
        // DEBUG VISUALIZATION: Switch between original and collapsed mesh for preview
        if (ShowCollapsedMesh && CollapsedMesh != null)
            TargetMeshFilter.mesh = CollapsedMesh;
        else if (OriginalMesh != null)
            TargetMeshFilter.mesh = OriginalMesh;
    }

    // CONDITIONAL GENERATION - Only creates collapsed mesh if it doesn't already exist
    void GenerateCollapsedMeshIfNeeded()
    {
        // DUPLICATE PREVENTION: Skip if collapsed mesh already exists
        if (CollapsedMesh != null)
        {
            Debug.Log($"VeinWallCollapser on {gameObject.name} - collapsed mesh already exists, skipping generation");
            return;
        }

        // COMPONENT CHECK: Ensure TargetMeshFilter is found before generating
        if (TargetMeshFilter == null)
            TargetMeshFilter = GetComponent<MeshFilter>();

        // MESH GENERATION: Create the collapsed mesh
        GenerateCollapsedMesh();
    }

    // COLLAPSED MESH GENERATION - Core algorithm that creates the compressed vein shape
    void GenerateCollapsedMesh()
    {
        // VALIDATION: Ensure we have required components
        if (TargetMeshFilter == null || TargetMeshFilter.mesh == null)
        {
            Debug.LogError($"VeinWallCollapser on {gameObject.name} requires MeshFilter with mesh on same gameobject!");
            return;
        }

        if (PushPointTransforms == null || PushPointTransforms.Length == 0)
        {
            Debug.LogError($"VeinWallCollapser on {gameObject.name} requires at least one push point transform!");
            return;
        }

        // MESH DATA SETUP: Get original mesh data and create working copies
        OriginalMesh = TargetMeshFilter.mesh;
        Vector3[] originalVertices = OriginalMesh.vertices;

        // CREATE COLLAPSED MESH: Make a copy to modify
        CollapsedMesh = Instantiate(OriginalMesh);
        Vector3[] collapsedVertices = CollapsedMesh.vertices;

        // VERTEX PROCESSING LOOP: Compress each vertex against nearest wall
        for (int i = 0; i < originalVertices.Length; i++)
        {
            // COORDINATE CONVERSION: Convert vertex from local mesh space to world space
            Vector3 worldVertex = TargetMeshFilter.transform.TransformPoint(originalVertices[i]);

            // FIND CLOSEST PUSH POINT: Determine which push point controls this vertex
            Transform closestPushPoint = FindClosestPushPoint(worldVertex);

            if (closestPushPoint != null)
            {
                // GET PUSH DIRECTION: Use push point's forward direction for compression
                Vector3 pushDirection = closestPushPoint.forward;

                // WALL RAYCAST: Shoot ray from vertex toward wall
                Ray pushRay = new Ray(worldVertex, pushDirection);

                // WALL COLLISION CHECK: See if ray hits any wall colliders
                if (Physics.Raycast(pushRay, out RaycastHit hit, Mathf.Infinity, WallLayerMask))
                {
                    // COMPRESSION CALCULATION: Find point between original vertex and wall hit
                    Vector3 fullCompressionPoint = hit.point;

                    // COMPRESSION LIMITING: Lerp between original and full compression to prevent over-flattening
                    Vector3 limitedCompressionPoint = Vector3.Lerp(worldVertex, fullCompressionPoint, CompressionLimit);

                    // COORDINATE CONVERSION: Convert back to local mesh space and store
                    collapsedVertices[i] = TargetMeshFilter.transform.InverseTransformPoint(limitedCompressionPoint);
                }
                // If no wall hit, vertex keeps original position
            }
            // If no push points, vertex keeps original position
        }

        // MESH FINALIZATION: Update collapsed mesh with new vertex positions
        CollapsedMesh.vertices = collapsedVertices;
        CollapsedMesh.RecalculateNormals();  // Recalculate surface normals for proper lighting
        CollapsedMesh.RecalculateBounds();   // Update bounding box for optimization

        Debug.Log($"VeinWallCollapser generated collapsed mesh on {gameObject.name} with {collapsedVertices.Length} vertices using {PushPointTransforms.Length} push points (compression limit: {CompressionLimit})");
    }

    // PUSH POINT SELECTION - Find which push point is closest to given vertex
    Transform FindClosestPushPoint(Vector3 worldVertex)
    {
        // VALIDATION: Ensure we have push points
        if (PushPointTransforms == null || PushPointTransforms.Length == 0)
            return null;

        // DISTANCE COMPARISON: Find closest push point to this vertex
        Transform closest = PushPointTransforms[0];
        float closestDistance = Vector3.Distance(worldVertex, closest.position);

        // SEARCH LOOP: Check all push points for closest one
        for (int i = 1; i < PushPointTransforms.Length; i++)
        {
            float distance = Vector3.Distance(worldVertex, PushPointTransforms[i].position);
            if (distance < closestDistance)
            {
                closest = PushPointTransforms[i];
                closestDistance = distance;
            }
        }

        return closest;
    }

    /*
    NOTES FOR FUTURE DEVELOPMENT/DEBUGGING:
    
    SETUP TIPS:
    • Wall Colliders: Use Unity primitives (cubes, capsules) positioned where vein should compress
    • Push Points: Position these to define compression direction - their forward vector points toward wall
    • Layer Mask: Put all wall colliders on same layer and set WallLayerMask to that layer
    • Compression Limit: 0.8 usually works well - prevents triangle overlap while allowing good compression
    
    ARTISTIC WORKFLOW:
    • Create wall colliders visually positioned around vein
    • Add push points as children of walls, aimed toward wall surface
    • Use ShowCollapsedMesh toggle to preview compression shape
    • Adjust push point positions/rotations and CompressionLimit until shape looks good
    • Multiple push points allow different compression directions for different parts of vein
    
    PERFORMANCE CONSIDERATIONS:
    • Mesh generation happens once at startup - no runtime performance impact
    • High-poly meshes will take longer to generate but don't affect runtime
    • Consider using LOD meshes for very complex vein geometry
    
    COMMON ISSUES:
    • No compression: Check wall colliders are on correct layer and push points are aimed properly
    • Weird compression shape: Adjust push point positions and CompressionLimit value
    • Missing components: Ensure MeshFilter is on same gameobject as this script
    • Raycast misses: Check that push point forward direction actually points toward wall
    
    DEPENDENCIES TO CHECK IF PROBLEMS:
    1. MeshFilter component on same gameobject with valid mesh
    2. Wall colliders exist and are on layer specified by WallLayerMask
    3. Push point transforms are assigned and positioned correctly
    4. Push points are rotated so their forward direction points toward walls
    5. CompressionLimit is set to reasonable value (0.5-0.9 usually works)
    */
}