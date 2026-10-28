using UnityEngine;

public class VeinCompressionManager : MonoBehaviour
{
    /*
    OVERVIEW:
    • VeinCompressionManager is a singleton that manages the global state of vein compression
    • Monitors ultrasound probe position, orientation, and pressure against skin
    • Provides compression parameters to all VeinCompressionMeshDeformer components
    • Uses IntersectionVolumeManager to detect probe-skin contact and calculate pressure
    • Should be placed on a central gameobject like "Managers" or "Anatomy"
    
    HOW IT WORKS:
    1. Tracks probe markers (tip and back) to determine probe position and orientation
    2. Reads pressure data from IntersectionVolumeManager (probe-skin intersection volume)
    3. Calculates compression parameters (point, direction, pressure) each frame
    4. Provides this data to all mesh deformers via singleton pattern
    5. Handles compression on/off state based on pressure threshold
    */

    // SINGLETON PATTERN: Global access point for all mesh deformers
    public static VeinCompressionManager ME;

    // PROBE HARDWARE REFERENCES: Physical markers on the ultrasound probe
    [Header("Probe References")]
    public Transform ProbeTipMarker;        // Marker at center of probe face (compression point)
    public Transform ProbeBackMarker;       // Marker at back of probe handle (defines probe axis)
    public IntersectionVolumeManager IntersectionManager;  // Calculates probe-skin intersection volume

    // COMPRESSION PARAMETERS: Tunable settings that affect compression behavior
    [Header("Compression Parameters")]
    [Range(0, 100)]
    public float InfluenceRadius_mm = 30f;  // Radius around probe tip that affects vertices

    [Range(0, 10)]
    public float MaxCompression_mm = 5f;    // Maximum distance vertices can be compressed

    [Range(0, 10)]
    public float PressureMultiplier = 1f;   // Converts normalized intersection volume to pressure

    // CURRENT COMPRESSION STATE: Updated each frame, read by mesh deformers
    // These properties are calculated in UpdateCompressionState() and used by deformers
    public Vector3 CompressionPoint { get; private set; }     // World position where compression is applied (probe tip)
    public Vector3 CompressionDirection { get; private set; }  // Direction from probe tip toward probe back
    public float CurrentPressure { get; private set; }        // Current pressure value (0-10+ range)
    public bool IsCompressing { get; private set; }           // Is compression currently active?

    // SINGLETON INITIALIZATION: Ensure only one instance exists
    void Awake()
    {
        // SINGLETON PATTERN: Destroy duplicate instances
        if (ME != null)
            GameObject.Destroy(ME);
        else
            ME = this;

        // NOTE: DontDestroyOnLoad commented out - uncomment if manager should persist between scenes
        // DontDestroyOnLoad(this);
    }

    // MAIN UPDATE LOOP: Calculate compression state every frame
    void Update()
    {
        UpdateCompressionState();
    }

    // COMPRESSION STATE CALCULATION: Core logic that determines compression parameters
    void UpdateCompressionState()
    {
        // SAFETY CHECK: Ensure all required components are assigned
        if (ProbeTipMarker == null || ProbeBackMarker == null || IntersectionManager == null)
        {
            // If any component is missing, disable compression
            IsCompressing = false;
            return;
        }

        // PROBE POSITION: Get current world position of probe tip
        // This becomes the center point of compression influence
        CompressionPoint = ProbeTipMarker.position;

        // PROBE ORIENTATION: Calculate direction vector from tip toward back
        // This defines the axis along which compression occurs
        // Vector points FROM tip TO back (important for mesh deformer calculations)
        CompressionDirection = (ProbeBackMarker.position - ProbeTipMarker.position).normalized;

        // PRESSURE CALCULATION: Convert intersection volume to compression pressure
        // IntersectionManager.NormalizedIntersectionVolume ranges from 0 (no contact) to 1 (max intersection)
        // PressureMultiplier allows tuning the sensitivity of pressure response
        CurrentPressure = IntersectionManager.NormalizedIntersectionVolume * PressureMultiplier;

        // COMPRESSION STATE: Determine if compression should be active
        // Small threshold (0.01f) prevents flickering when pressure is very low
        // Compression is active when there's meaningful probe-skin contact
        IsCompressing = CurrentPressure > 0.01f;
    }

    // DEBUG VISUALIZATION: Draw compression geometry in scene view
    void OnDrawGizmos()
    {
        // Only draw if probe tip marker is assigned
        if (ProbeTipMarker != null)
        {
            // INFLUENCE SPHERE: Red wireframe sphere showing compression influence area
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(CompressionPoint, InfluenceRadius_mm);

            // COMPRESSION DIRECTION: Blue ray showing probe orientation
            // Ray points from tip toward back, showing compression axis
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(CompressionPoint, CompressionDirection * 20f);
        }
    }

    /*
    USAGE NOTES:
    
    1. SETUP:
       - Place this script on a central gameobject in your scene
       - Assign ProbeTipMarker and ProbeBackMarker transforms in inspector
       - Assign IntersectionVolumeManager reference in inspector
       - Tune compression parameters as needed
    
    2. INTEGRATION:
       - VeinCompressionMeshDeformer scripts automatically find this manager via ME singleton
       - No additional setup needed once this manager is configured
    
    3. DEBUGGING:
       - Enable Gizmos in scene view to see influence sphere and compression direction
       - Monitor CurrentPressure and IsCompressing in inspector during play
       - Adjust PressureMultiplier if pressure sensitivity needs tuning
    
    4. PERFORMANCE:
       - This script runs lightweight calculations each frame
       - Heavy mesh deformation work is done in individual deformer scripts
       - Singleton pattern ensures efficient communication between components
    */
}