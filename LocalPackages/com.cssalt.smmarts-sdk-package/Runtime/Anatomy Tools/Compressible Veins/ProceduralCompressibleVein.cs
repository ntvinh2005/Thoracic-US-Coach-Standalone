using UnityEngine;
using SMMARTS;

// ============================================================
// ProceduralCompressibleVein.cs  
// ============================================================
// Automatically configures vessel compression by:
// 1. Finding or creating shared compression controller (PCVD + CBG)
// 2. Adding this vein to controller's vessel lists
// 3. Tracking vessel movement and regenerating when anatomy relocates
//
// Usage: Simply drag this component onto any DTM vessel GameObject.
// No manual setup required - everything happens automatically in editor.
// ============================================================

[ExecuteAlways] // Runs in editor for immediate setup when component is added
public class ProceduralCompressibleVein : MonoBehaviour
{
    // ============================================================
    // INSPECTOR CONFIGURATION
    // ============================================================

    [Header("Position Tracking")]
    [Tooltip("Last known world position for movement detection.")]
    [SerializeField] private Vector3 lastWorldPosition;

    [Tooltip("Enable automatic regeneration when vessel moves (for tracking system support).")]
    [SerializeField] private bool trackMovement = true;

    // ============================================================
    // CONSTANTS & CACHED REFERENCES
    // ============================================================

    // Hard-coded prefab name - must exist in Resources folder
    private const string PREFAB_NAME = "Compressible Vein Controller";

    // Movement detection threshold (world units)
    private const float MOVEMENT_EPSILON = 0.001f;

    // Cached component references
    private GameObject controllerGameObject;
    private ProbeCompressionVeinDeformer cachedPCVD;
    private CompressibleBackplaneGenerator cachedCBG;
    private DynamicTubularMesh cachedDTM;

    // ============================================================
    // UNITY LIFECYCLE
    // ============================================================

    /// <summary>
    /// Performs automatic setup in editor when component is first added.
    /// Also initializes position tracking for runtime movement detection.
    /// </summary>
    void Awake()
    {

        // Only run auto-setup in editor, and only once
        if (!Application.isPlaying)
        {
            SetupCompression();
        }

        // Initialize position tracking for anatomy movement detection
        lastWorldPosition = transform.position;
        CacheComponentReferences();
    }

    /// <summary>
    /// Monitors vessel position for movement and triggers regeneration when needed.
    /// This handles cases where tracking systems relocate anatomy after initialization.
    /// </summary>
    void Update()
    {
        if (!trackMovement) return;

        // Check if world position changed beyond epsilon threshold
        Vector3 currentWorldPosition = transform.position;
        if (Vector3.Distance(currentWorldPosition, lastWorldPosition) > MOVEMENT_EPSILON)
        {
            Debug.Log($"{gameObject.name} moved from {lastWorldPosition} to {currentWorldPosition} - regenerating");

            RegenerateAfterMovement();
            lastWorldPosition = currentWorldPosition;
        }
    }

    // ============================================================
    // AUTOMATIC COMPRESSION SETUP
    // ============================================================

    /// <summary>
    /// Main auto-configuration method that handles:
    /// 1. Finding or creating the shared compression controller
    /// 2. Adding this vessel to PCVD and CBG component lists
    /// 3. Validating all required components exist
    /// </summary>
    void SetupCompression()
    {
        // Find or create the shared Compressible Vein Controller
        GameObject controllerGO = FindOrCreateController();
        if (controllerGO == null)
        {
            Debug.LogError($"Failed to find or create Compressible Vein Controller for {gameObject.name}");
            return;
        }

        // Get required compression components from controller
        var controllerPCVD = controllerGO.GetComponent<ProbeCompressionVeinDeformer>();
        var controllerCBG = controllerGO.GetComponent<CompressibleBackplaneGenerator>();

        if (controllerPCVD == null || controllerCBG == null)
        {
            Debug.LogError($"Compressible Vein Controller missing PCVD or CBG components!");
            return;
        }

        // Add this vessel to PCVD vessels list (avoid duplicates)
        if (!controllerPCVD.Vessels.Contains(gameObject))
        {
            controllerPCVD.Vessels.Add(gameObject);
            Debug.Log($"Added {gameObject.name} to PCVD vessels list");
        }

        // Add DTM component to CBG target list (if DTM exists)
        var vesselDTM = GetComponent<DynamicTubularMesh>();
        if (vesselDTM != null && !controllerCBG.TargetDTMs.Contains(vesselDTM))
        {
            controllerCBG.TargetDTMs.Add(vesselDTM);
            Debug.Log($"Added {gameObject.name} DTM to CBG target list");
        }
        else if (vesselDTM == null)
        {
            Debug.LogWarning($"{gameObject.name} has no DynamicTubularMesh component - compression may not work properly");
        }

        Debug.Log($"Compression auto-setup complete for {gameObject.name}");
    }

    /// <summary>
    /// Searches for existing controller or creates new one from prefab.
    /// Search priority: Name-based lookup → Component-based lookup → Create new
    /// </summary>
    GameObject FindOrCreateController()
    {
        // Priority 1: Search by exact GameObject name
        GameObject existing = GameObject.Find("Compressible Vein Controller");
        if (existing != null)
        {
            Debug.Log($"Found existing Compressible Vein Controller");
            return existing;
        }

        // Priority 2: Search by PCVD component (handles renamed controllers)
        var existingPCVD = FindAnyObjectByType<ProbeCompressionVeinDeformer>();
        if (existingPCVD != null)
        {
            Debug.Log($"Found existing PCVD on {existingPCVD.gameObject.name}");
            return existingPCVD.gameObject;
        }

        // Priority 3: Create new controller from Resources prefab
        GameObject prefab = Resources.Load<GameObject>(PREFAB_NAME);
        if (prefab == null)
        {
            Debug.LogError($"Could not find prefab '{PREFAB_NAME}' in Resources folder! " +
                          $"Make sure the prefab exists at Resources/{PREFAB_NAME}.prefab");
            return null;
        }

        // Determine parent: use vessel's parent, or vessel itself if root-level
        Transform parentTransform = transform.parent != null ? transform.parent : transform;
        GameObject newController = Instantiate(prefab, parentTransform);
        newController.name = "Compressible Vein Controller"; // Remove (Clone) suffix

        Debug.Log($"Created new Compressible Vein Controller under {parentTransform.name}");
        return newController;
    }

    // ============================================================
    // MOVEMENT HANDLING
    // ============================================================

    /// <summary>
    /// Handles regeneration after vessel movement due to tracking system updates.
    /// This ensures mesh and compression data stay synchronized with new positions.
    /// </summary>
    void RegenerateAfterMovement()
    {
        // 1. Regenerate DTM mesh geometry at new position
        if (cachedDTM != null)
        {
            cachedDTM.Regenerate();
            Debug.Log($"Regenerated DTM for {gameObject.name}");
        }

        // 2. Reinitialize PCVD with delay to ensure DTM completes first
        if (cachedPCVD != null)
        {
            cachedPCVD.Invoke("Initialize", 0.1f); // 100ms delay for mesh generation
            Debug.Log($"Reinitializing PCVD after {gameObject.name} movement");
        }

        // Note: CBG updates automatically in its Update() loop by reading DTM data,
        // so no manual intervention needed for backplane regeneration
    }

    /// <summary>
    /// Caches references to frequently-used components for performance.
    /// Called during Awake() and can be called again if references become stale.
    /// </summary>
    void CacheComponentReferences()
    {
        // Cache our own DTM component
        cachedDTM = GetComponent<DynamicTubularMesh>();

        // Find and cache controller GameObject
        if (controllerGameObject == null)
        {
            controllerGameObject = GameObject.Find("Compressible Vein Controller");
            if (controllerGameObject == null)
            {
                var existingPCVD = FindAnyObjectByType<ProbeCompressionVeinDeformer>();
                if (existingPCVD != null)
                    controllerGameObject = existingPCVD.gameObject;
            }
        }

        // Cache controller's components if controller was found
        if (controllerGameObject != null)
        {
            cachedPCVD = controllerGameObject.GetComponent<ProbeCompressionVeinDeformer>();
            cachedCBG = controllerGameObject.GetComponent<CompressibleBackplaneGenerator>();
        }
    }
}