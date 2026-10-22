using UnityEngine;
using SMMARTS;

public class VeinCompressionMeshDeformer : MonoBehaviour
{
    /*
    OVERVIEW:
    • VeinCompressionMeshDeformer uses pre-generated collapsed mesh for realistic compression
    • Simply lerps between original and collapsed mesh based on pressure and proximity
    • Much simpler and more predictable than real-time vertex calculations
    • Works with VeinWallCollapser to get the collapsed mesh state
    
    SETUP WORKFLOW:
    1. Add VeinWallCollapser component to vein gameobject
    2. Set up wall colliders and push points in VeinWallCollapser
    3. Generate collapsed mesh using VeinWallCollapser
    4. Add this VeinCompressionMeshDeformer component to the same vein gameobject
    5. Tune CollapseMultiplier per vein as needed
    
    DEPENDENCIES:
    • VeinCompressionManager (singleton) - provides probe position and pressure
    • VeinWallCollapser (same gameobject) - provides original and collapsed meshes
    • MeshFilter (same gameobject) - for visual mesh rendering
    • MeshCollider (same gameobject) - for ultrasound physics simulation
    • SMMARTS.UltrasoundVisibility (same gameobject) - controls how the vein is visible in ultrasound, so you can make the vein vanish completely past a threshold.
    
    HOW IT WORKS:
    • Vertices within probe influence radius get compressed based on:
      - Distance to probe (closer = more compression)
      - Current probe pressure from VeinCompressionManager
      - Per-vein CollapseMultiplier tuning factor
    • Lerps between original vertex positions and pre-generated collapsed positions
    • Updates both visual mesh and physics collider in real-time
    
    ATTACH THIS SCRIPT TO THE VEIN GAMEOBJECT
    */

    [Header("Tuning")]
    [Range(0.1f, 5f)]
    public float CollapseMultiplier = 1f;      // Per-vein compression scaling (0.1 = very stiff, 5 = very compressible)

    [Header("Testing")]
    [Range(0f, 1f)]
    public float TestCompression = 0f;      // Manual compression override for testing (set to 0 for production)

    [Header("Compression State")]
    [Range(0f, 1f)]
    public float CurrentCompressionAmount = 0f; // Current compression (0 = original, 1 = fully collapsed)

    [Header("Highest Compression Detected")] // this is the maximum compression seen by any vertex in the mesh.
    [Range(0f, 1f)]
    public float maxCompressionFraction = 0f; // we can use this to turn off the vein in ultrasound.

    [Header("Threshold to make Vein Invisible in US")]
    [Range(0f, 1f)]
    public float VanishThreshold = 1f; // if maxCompressionFraction is more than VanishThreshold, we turn off the vein in US through UltrasoundVisibility. 



    // COMPONENT REFERENCES - Auto-found on same gameobject
    private MeshFilter MyMeshFilter;         // The mesh filter component
    private MeshCollider MyMeshCollider;     // Physics collider for ultrasound simulation
    private VeinWallCollapser WallCollapser; // Reference to the wall collapser component
    private SMMARTS.UltrasoundVisibility MyUltrasoundVisibility; // reference to the script component that makes this vein visible in US

    // MESH DATA - Working copies of vertex positions
    private Mesh workingMesh;               // The mesh we actually modify each frame
    private Vector3[] originalVertices;     // Original mesh vertices (local mesh space)
    private Vector3[] collapsedVertices;    // Collapsed mesh vertices (local mesh space)
    private Vector3[] currentVertices;      // Current working vertices (local mesh space)

    // STATE TRACKING - Manage compression lifecycle
    private bool isReady = false;           // Is initialization complete?
    private bool wasCompressingLastFrame = false; // Compression state tracking for start/stop detection

    void Start()
    {
        InitializeMeshes();
    }

    // INITIALIZATION - Set up mesh data and validate dependencies
    void InitializeMeshes()
    {
        // AUTO-FIND COMPONENTS: Get references from same gameobject
        MyMeshFilter = GetComponent<MeshFilter>();
        MyMeshCollider = GetComponent<MeshCollider>();
        WallCollapser = GetComponent<VeinWallCollapser>();
        MyUltrasoundVisibility = GetComponent<UltrasoundVisibility>();

        // VALIDATION: Ensure all required components and data are present
        if (MyMeshFilter == null)
        {
            Debug.LogError($"VeinCompressionMeshDeformer on {gameObject.name} requires MeshFilter component on same gameobject!");
            return;
        }

        if (MyMeshCollider == null)
        {
            Debug.LogError($"VeinCompressionMeshDeformer on {gameObject.name} requires MeshCollider component on same gameobject!");
            return;
        }

        if (WallCollapser == null || WallCollapser.OriginalMesh == null || WallCollapser.CollapsedMesh == null)
        {
            Debug.LogError($"VeinCompressionMeshDeformer on {gameObject.name} requires VeinWallCollapser with generated meshes on same gameobject!");
            return;
        }

        // MESH DATA SETUP: Get vertex arrays from both original and collapsed meshes
        // These are in local mesh space coordinates
        originalVertices = WallCollapser.OriginalMesh.vertices;
        collapsedVertices = WallCollapser.CollapsedMesh.vertices;

        // WORKING MESH CREATION: Create our own copy to modify without affecting originals
        workingMesh = Instantiate(WallCollapser.OriginalMesh);
        MyMeshFilter.mesh = workingMesh;

        // VERTEX ARRAY INITIALIZATION: Set up working array for frame-by-frame updates
        currentVertices = new Vector3[originalVertices.Length];

        // READY STATE: Mark as ready for operation
        isReady = true;
        Debug.Log($"VeinCompressionMeshDeformer initialized on {gameObject.name} with {originalVertices.Length} vertices");
    }

    // MAIN UPDATE LOOP - Handle compression state and mesh updates
    void Update()
    {
        // SAFETY CHECK: Don't run until initialization is complete
        if (!isReady) return;

        // TEST COMPRESSION OVERRIDE: Skip normal logic if testing
        if (TestCompression > 0f)
        {
            CurrentCompressionAmount = TestCompression;
            UpdateMeshGeometry();
            return; // Skip normal compression logic when testing
        }

        // COMPRESSION STATE DETECTION: Check if compression is active
        bool isCompressingNow = VeinCompressionManager.ME != null && VeinCompressionManager.ME.IsCompressing;

        // COMPRESSION END DETECTION: Reset mesh when compression stops
        if (wasCompressingLastFrame && !isCompressingNow)
        {
            CurrentCompressionAmount = 0f;
            UpdateMeshGeometry();
        }

        // ACTIVE COMPRESSION: Update mesh geometry during compression
        if (isCompressingNow && VeinCompressionManager.ME != null)
        {
            // Store current compression amount for debugging/monitoring
            CurrentCompressionAmount = VeinCompressionManager.ME.CurrentPressure;
            UpdateMeshGeometry();
        }

        // STATE TRACKING: Remember compression state for next frame
        wasCompressingLastFrame = isCompressingNow;
    }

    // MESH GEOMETRY UPDATE - Core compression logic with proximity-based vertex processing
    void UpdateMeshGeometry()
    {
        // SAFETY CHECK: Ensure manager is available
        if (VeinCompressionManager.ME == null) return;

        // GET COMPRESSION PARAMETERS: Read current state from manager singleton
        Vector3 compressionPoint = VeinCompressionManager.ME.CompressionPoint;     // World position of probe tip
        float influenceRadius = VeinCompressionManager.ME.InfluenceRadius_mm;      // Radius of compression effect
        float currentPressure = VeinCompressionManager.ME.CurrentPressure;        // Current pressure from probe

        // NOTE MAX COMPRESSION AMOUNT
        maxCompressionFraction = 0f;

        // VERTEX PROCESSING LOOP: Check each vertex for compression
        for (int i = 0; i < currentVertices.Length; i++)
        {
            // COORDINATE CONVERSION: Convert vertex from local mesh space to world space for distance calculation
            Vector3 worldVertex = transform.TransformPoint(originalVertices[i]);

            // PROXIMITY CHECK: Only process vertices within probe influence radius
            float distanceToProbe = Vector3.Distance(compressionPoint, worldVertex);

            if (distanceToProbe < influenceRadius)
            {
                // PROXIMITY FACTOR: Calculate how close vertex is to probe center
                // Closer vertices get more compression (1.0 at center, 0.0 at edge of influence)
                float proximityFactor = 1f - (distanceToProbe / influenceRadius);

                // EFFECTIVE COMPRESSION CALCULATION: Combine pressure, proximity, and per-vein multiplier
                float effectiveCompression = currentPressure * proximityFactor * CollapseMultiplier;

                // TEST COMPRESSION OVERRIDE: Use test value if active
                if (TestCompression > 0f)
                    effectiveCompression = TestCompression * proximityFactor * CollapseMultiplier;

                // COMPRESSION CLAMPING: Keep within valid range for lerp
                effectiveCompression = Mathf.Clamp01(effectiveCompression);

                // NOTE MAX COMPRESSION
                if (effectiveCompression > maxCompressionFraction) maxCompressionFraction = effectiveCompression;

                // VERTEX INTERPOLATION: Lerp between original and collapsed positions
                currentVertices[i] = Vector3.Lerp(originalVertices[i], collapsedVertices[i], effectiveCompression);
            }
            else
            {
                // OUTSIDE INFLUENCE: Keep original position for vertices outside probe range
                currentVertices[i] = originalVertices[i];
            }
        }

        // MESH UPDATE: Apply new vertex positions to working mesh
        workingMesh.vertices = currentVertices;
        workingMesh.RecalculateNormals();    // Recalculate surface normals for proper lighting
        workingMesh.RecalculateBounds();     // Update bounding box for culling/optimization

        // VISUAL MESH UPDATE: Force refresh of mesh filter for 3D rendering
        MyMeshFilter.mesh = null;   // Clear reference to force Unity to refresh
        MyMeshFilter.mesh = workingMesh;

        // PHYSICS MESH UPDATE: Update collider for ultrasound simulation
        if (MyMeshCollider != null)
        {
            MyMeshCollider.sharedMesh = null; // Force refresh of physics mesh
            MyMeshCollider.sharedMesh = workingMesh;
        }

        // ULTRASOUND VISIBILITY: force the ultrasound visiblity script component off if the vein is compressed past a threshold amount
        if (VanishThreshold > 0f)
        {
            if (maxCompressionFraction >= VanishThreshold)
                MyUltrasoundVisibility.ShowOnUltrasound = false;
            else
                MyUltrasoundVisibility.ShowOnUltrasound = true;
        }

    }

    // DEBUG/TESTING FUNCTIONS: Manual compression control via context menu
    [ContextMenu("Test Full Compression")]
    void TestFullCompression()
    {
        if (!isReady) return;
        CurrentCompressionAmount = 1f;
        UpdateMeshGeometry();
    }

    [ContextMenu("Test No Compression")]
    void TestNoCompression()
    {
        if (!isReady) return;
        CurrentCompressionAmount = 0f;
        UpdateMeshGeometry();
    }

    /*
    NOTES FOR FUTURE DEVELOPMENT/DEBUGGING:
    
    PERFORMANCE CONSIDERATIONS:
    • This script processes every vertex every frame during compression
    • For high-poly meshes, consider vertex culling or LOD systems
    • Mesh updates are expensive - consider caching unchanged frames
    
    TUNING TIPS:
    • CollapseMultiplier: 1.0 = normal, 0.5 = half compression, 2.0 = double compression
    • TestCompression: Use for fine-tuning collapsed mesh appearance without probe
    • VeinCompressionManager.PressureMultiplier: Global pressure sensitivity
    
    COMMON ISSUES:
    • Visual mesh not updating: Check MeshFilter component and workingMesh creation
    • Physics not matching visual: Ensure MeshCollider is being updated
    • Compression not working: Verify VeinCompressionManager.ME singleton is active
    • Wrong compression shape: Check VeinWallCollapser setup and collapsed mesh generation
    
    DEPENDENCIES TO CHECK IF PROBLEMS:
    1. VeinCompressionManager exists in scene and ME singleton is set
    2. VeinWallCollapser has generated both OriginalMesh and CollapsedMesh
    3. Probe markers and IntersectionVolumeManager are working in VeinCompressionManager
    4. Wall colliders and push points are set up correctly in VeinWallCollapser
    5. All components (MeshFilter, MeshCollider, VeinWallCollapser) are on same gameobject
    */
}