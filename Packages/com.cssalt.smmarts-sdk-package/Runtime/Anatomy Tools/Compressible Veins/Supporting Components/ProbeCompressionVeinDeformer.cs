using System.Collections.Generic;
using System;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using System.Linq;
using System.Security.Cryptography;

// ============================================================
// ProbeCompressionVeinDeformer.cs
// ============================================================
// Real-time vessel mesh deformation system for ultrasound probe compression simulation.
// Uses Unity Job System with Burst compilation for high-performance vertex displacement.
//
// Key Features:
// - Shape-aware compression using actual probe contact points (not circular)
// - Backwall collision constraints to prevent over-compression
// - Multiple vessel support with individual mesh instance management
// - Real-time performance via Burst-compiled parallel job execution
//
// Units: ALL distances in millimeters (1 Unity unit = 1mm)
// ============================================================

public class ProbeCompressionVeinDeformer : MonoBehaviour
{
    // ============================================================
    // INSPECTOR CONFIGURATION
    // ============================================================

    [Header("Probe / Geometry")]
    [Tooltip("Component that calculates probe-skin intersection data (compression depth, center, normal, contact points).")]
    public SkinIntersectionCompression SkinIntersectionCompressor;

    [Tooltip("Manual list of vessel GameObjects to apply compression deformation to.")]
    public List<GameObject> Vessels;

    [Header("Parameters (mm)")]
    [Tooltip("Maximum distance from probe contact for compression effects (mm).")]
    public float InfluenceRadius_mm = 50f;

    [Tooltip("Maximum allowed compression depth - higher values are clamped (mm).")]
    public float MaxCompression_mm = 10f;

    [Tooltip("Minimum compression depth before deformation starts (mm).")]
    public float MinDisplacementThreshold_mm = 0.05f;

    [Range(0f, 5f)]
    [Tooltip("Increasing sensitivity makes the veins compress with less pressure.")]
    public float sensitivity = 1f; // Qualitative adjustment for easily compressible veins, added by Lizdas 

    [Tooltip("Falloff curve for compression influence (0=full effect at contact, 1=no effect at radius edge).")]
    public AnimationCurve FalloffCurve = AnimationCurve.EaseInOut(0, 1, 1, 0);

    [Header("Backwall / Column Settings (mm)")]
    [Tooltip("Lateral bin size when grouping vertices into compression columns (mm).")]
    public float ColumnSize_mm = 1.0f;

    [Tooltip("Safety offset from backwall to avoid perfectly flat compression artifacts (mm).")]
    public float BackwallRelax_mm = 0.02f;

    [Header("Backplane Raycast (collision constraint)")]
    [Tooltip("MeshCollider representing the backplane surface that limits compression depth.")]
    public MeshCollider BackplaneCollider;

    [Tooltip("Distance to start raycasts outside the backplane surface (mm).")]
    public float BackplaneRayStartOffset_mm = 0f;

    [Tooltip("Maximum raycast distance to find backplane collision (mm).")]
    public float BackplaneRayLength_mm = 1000f;

    [Tooltip("Minimum gap to maintain between vessel and backplane (prevents paper-thin compression) (mm).")]
    public float MinGapToBackplane_mm = 0.2f;

    [Header("Performance")]
    [Tooltip("Batch size for Burst job parallelization (32-128 recommended).")]
    public int JobBatchSize = 32;

    // ============================================================
    // INTERNAL DATA STRUCTURES
    // ============================================================

    /// <summary>
    /// Contains all mesh and deformation data for a single vessel.
    /// Each vessel gets its own mesh instance to avoid modifying shared assets.
    /// </summary>
    private class VeinData
    {
        public MeshFilter meshFilter;       // Vessel's MeshFilter component
        public MeshCollider meshCollider;   // Vessel's MeshCollider component (optional)
        public Mesh mesh;                   // Unique mesh instance for runtime modification
        public Vector3[] originalVerts;     // Original vertex positions (local space)
        public Vector3[] workingVerts;      // Current vertex positions (local space)
        public int startIndex;              // Starting index in flattened vertex arrays
        public int count;                   // Number of vertices in this vessel
        public bool currentlyDeformed;      // Whether deformation is currently applied
    }

    /// <summary>
    /// Helper struct for spatial column grouping (used for backwall calculations).
    /// </summary>
    private struct ColumnKey : System.IEquatable<ColumnKey>
    {
        public int u, v; // 2D grid coordinates

        public ColumnKey(int u, int v) { this.u = u; this.v = v; }
        public bool Equals(ColumnKey other) => u == other.u && v == other.v;
        public override bool Equals(object obj) => obj is ColumnKey other && Equals(other);
        public override int GetHashCode() => unchecked((u * 397) ^ v);
    }

    /// <summary>
    /// Statistics for vertex groupings in spatial columns.
    /// </summary>
    private struct ColumnStats
    {
        public float minDepth; // Shallowest vertex along compression direction
        public float maxDepth; // Deepest vertex (defines local backwall)
    }

    // ============================================================
    // PRIVATE STATE
    // ============================================================

    // Vessel data management
    private List<VeinData> veins = new List<VeinData>();
    private bool initialized = false;

    // Flattened arrays for Burst job (all vessels combined)
    private NativeArray<float3> originalPosWorld;    // World-space original positions (mm)
    private NativeArray<float3> displacements;       // Job output: vertex displacements (mm)
    private NativeArray<float> columnScale;          // Per-vertex scaling (0-1) based on backwall
    private NativeArray<float> backwallDistance;     // Per-vertex distance to backwall (mm)

    // Normal vectors (cached for performance)
    private Vector3[] originalNormalsLocal;          // Per-vertex normals in local space
    private Vector3[] originalNormalsWorld;          // Per-vertex normals in world space

    // Compression provider caching
    private ICompressionProvider compressionProvider = null;
    private bool missingProviderWarned = false;

    // Spatial column processing (CPU-side scratch data)
    private float[] vertexDepths;
    private ColumnKey[] vertexColumns;
    private readonly Dictionary<ColumnKey, ColumnStats> columnStats = new Dictionary<ColumnKey, ColumnStats>();

    // ============================================================
    // UNITY LIFECYCLE
    // ============================================================

    void Start()
    {
        Initialize();
    }

    /// <summary>
    /// Validates inspector parameters to prevent invalid configurations.
    /// </summary>
    void OnValidate()
    {
        InfluenceRadius_mm = Mathf.Max(0.01f, InfluenceRadius_mm);
        MinDisplacementThreshold_mm = Mathf.Max(0f, MinDisplacementThreshold_mm);
        MaxCompression_mm = Mathf.Max(0f, MaxCompression_mm);
        ColumnSize_mm = Mathf.Max(0.01f, ColumnSize_mm);
        BackwallRelax_mm = Mathf.Max(0f, BackwallRelax_mm);
        JobBatchSize = Mathf.Max(1, JobBatchSize);
    }

    /// <summary>
    /// Main update loop: gets compression data, calculates displacements, applies to meshes.
    /// Runs every frame to handle real-time probe movement and pressure changes.
    /// </summary>
    /// 
    public float ContactPercent_Display;
    void Update()
    {
        if (!initialized) Initialize();
        if (!initialized) return;
        if (SkinIntersectionCompressor == null) return;

        // Get current compression data from skin intersection system
        if (!TryGetCompressionData(out Vector3 centerV, out Vector3 normalV, out float depth_mm, out float contact_percent))
        {
            RestoreOriginalsIfNeeded();
            return;
        }

        // Apply configured limits
        depth_mm = Mathf.Min(depth_mm, MaxCompression_mm);

        // this is just so I can read it - the inspector is handy.
        // contact_percent is always 1 for a flat probe barely touching the skin. that cant be right.
        ContactPercent_Display = contact_percent;

        // Dynamic influence radius based on contact percentage
        // InfluenceRadius_mm = 60f * contact_percent;
        // influence_mm needs to be renamed and scaled to volume of intersection, not what "contact percent" is. 
        float influence_mm = InfluenceRadius_mm;

        // Early exit if compression below threshold
        if (depth_mm < MinDisplacementThreshold_mm)
        {
            RestoreOriginalsIfNeeded();
            return;
        }

        // Prepare job data
        var falloff = SampleCurve(32);

        // Update world-space normals for backwall calculations
        UpdateWorldSpaceNormals();

        // Calculate backwall collision constraints
        BuildBackwallColumns(centerV, normalV);

        // Setup and execute Burst job for parallel vertex displacement
        var job = new DisplacementJobBurst_mm
        {
            vertexPos = originalPosWorld,
            displacements = displacements,
            columnScale = columnScale,
            backwallDistance = backwallDistance,
            contactPoints = CreateContactPointsArray(),
            normal = math.normalize(new float3(normalV.x, normalV.y, normalV.z)),
            depth_mm = depth_mm,
            sensitivity = sensitivity,
            influenceRadius_mm = influence_mm,
            backwallRelax_mm = BackwallRelax_mm,
            maxCompression_mm = Mathf.Max(0.001f, MaxCompression_mm),
            falloff = falloff
        };

        job.Schedule(originalPosWorld.Length, JobBatchSize).Complete();

        // Apply results to vessel meshes
        ApplyDisplacementsToMeshes();

        // Cleanup temporary arrays
        falloff.Dispose();
        job.contactPoints.Dispose();
    }

    void OnDestroy()
    {
        // Dispose all NativeArrays to prevent memory leaks
        if (originalPosWorld.IsCreated) originalPosWorld.Dispose();
        if (displacements.IsCreated) displacements.Dispose();
        if (columnScale.IsCreated) columnScale.Dispose();
        if (backwallDistance.IsCreated) backwallDistance.Dispose();
    }

    // ============================================================
    // INITIALIZATION
    // ============================================================

    /// <summary>
    /// Initializes vessel data structures and allocates native arrays for Burst job.
    /// Creates unique mesh instances for each vessel to avoid modifying shared assets.
    /// </summary>
    void Initialize()
    {
        // Auto-find skin intersection calculator if not assigned
        if (SkinIntersectionCompressor == null)
            SkinIntersectionCompressor = FindAnyObjectByType<SkinIntersectionCompression>();

        // Release existing arrays
        DisposeNativeArrays();
        veins.Clear();

        // Process vessel list and create VeinData instances
        int runningIndex = 0;
        foreach (var vessel in Vessels)
        {
            if (vessel == null) continue;

            var mf = vessel.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            // Create unique mesh instance to avoid modifying shared asset
            Mesh instance = UnityEngine.Object.Instantiate(mf.sharedMesh);
            mf.mesh = instance;

            var vd = new VeinData
            {
                meshFilter = mf,
                meshCollider = vessel.GetComponent<MeshCollider>(),
                mesh = instance,
                originalVerts = instance.vertices,
                workingVerts = new Vector3[instance.vertexCount],
                startIndex = runningIndex,
                count = instance.vertexCount,
                currentlyDeformed = false
            };

            System.Array.Copy(vd.originalVerts, vd.workingVerts, vd.count);
            veins.Add(vd);
            runningIndex += vd.count;
        }

        // Check if we found any valid vessels
        int totalVertices = 0;
        foreach (var v in veins) totalVertices += v.count;

        if (totalVertices == 0)
        {
            Debug.LogWarning("ProbeCompressionVeinDeformer: No vessel meshes found - retrying in 1 second.");
            Invoke("Initialize", 1f);
            return;
        }

        // Allocate native arrays for Burst job
        AllocateNativeArrays(totalVertices);

        // Fill world position data
        FillWorldPositionData();

        // Setup normal arrays
        SetupNormalArrays(totalVertices);

        initialized = true;
    }

    /// <summary>
    /// Releases all NativeArrays safely.
    /// </summary>
    void DisposeNativeArrays()
    {
        if (originalPosWorld.IsCreated) originalPosWorld.Dispose();
        if (displacements.IsCreated) displacements.Dispose();
        if (columnScale.IsCreated) columnScale.Dispose();
        if (backwallDistance.IsCreated) backwallDistance.Dispose();
    }

    /// <summary>
    /// Allocates NativeArrays with the specified total vertex count.
    /// </summary>
    void AllocateNativeArrays(int totalVertices)
    {
        originalPosWorld = new NativeArray<float3>(totalVertices, Allocator.Persistent);
        displacements = new NativeArray<float3>(totalVertices, Allocator.Persistent);
        columnScale = new NativeArray<float>(totalVertices, Allocator.Persistent);
        backwallDistance = new NativeArray<float>(totalVertices, Allocator.Persistent);
    }

    /// <summary>
    /// Fills the world position array by transforming local vertices to world space.
    /// </summary>
    void FillWorldPositionData()
    {
        int idx = 0;
        foreach (var v in veins)
        {
            for (int i = 0; i < v.count; i++)
            {
                Vector3 worldPos = v.meshFilter.transform.TransformPoint(v.originalVerts[i]);
                originalPosWorld[idx++] = new float3(worldPos.x, worldPos.y, worldPos.z);
            }
        }
    }

    /// <summary>
    /// Sets up normal arrays for backwall calculations.
    /// </summary>
    void SetupNormalArrays(int totalVertices)
    {
        originalNormalsLocal = new Vector3[totalVertices];
        originalNormalsWorld = new Vector3[totalVertices];

        int nIdx = 0;
        foreach (var v in veins)
        {
            var norms = v.mesh.normals;
            for (int i = 0; i < v.count; i++)
            {
                originalNormalsLocal[nIdx++] = norms[i];
            }
        }
    }

    // ============================================================
    // COMPRESSION DATA HANDLING
    // ============================================================

    /// <summary>
    /// Retrieves compression data from the SkinIntersectionCompressor.
    /// Simplified version that only uses ICompressionProvider interface.
    /// </summary>
    bool TryGetCompressionData(out Vector3 centerV, out Vector3 normalV, out float depth_mm, out float contact_percent)
    {
        centerV = Vector3.zero;
        normalV = Vector3.up;
        depth_mm = 0f;
        contact_percent = 0f;

        // Cache the compression provider reference
        if (compressionProvider == null)
        {
            compressionProvider = SkinIntersectionCompressor as ICompressionProvider;
        }

        // Read from provider if available
        if (compressionProvider != null)
        {
            centerV = compressionProvider.CompressionCenter;
            normalV = compressionProvider.CompressionNormal;
            depth_mm = compressionProvider.CompressionDepth;
            contact_percent = compressionProvider.ContactPercent;

            // Validate we got meaningful data
            if (normalV.sqrMagnitude > 1e-10f && depth_mm > 0f)
            {
                normalV.Normalize();
                return true;
            }
        }

        return false;
    }

    // ============================================================
    // MESH PROCESSING
    // ============================================================

    /// <summary>
    /// Updates world-space normal vectors for all vertices.
    /// Called before backwall calculations that need surface normals.
    /// </summary>
    void UpdateWorldSpaceNormals()
    {
        int wi = 0;
        foreach (var v in veins)
        {
            for (int i = 0; i < v.count; i++, wi++)
            {
                originalNormalsWorld[wi] = v.meshFilter.transform.TransformDirection(originalNormalsLocal[wi]).normalized;
            }
        }
    }

    /// <summary>
    /// Creates NativeArray of contact points from compression provider for Burst job.
    /// </summary>
    NativeArray<float3> CreateContactPointsArray()
    {
        var contactPoints = compressionProvider?.ContactPoints;
        if (contactPoints == null || contactPoints.Length == 0)
        {
            return new NativeArray<float3>(0, Allocator.TempJob);
        }

        return new NativeArray<float3>(
            contactPoints.Select(p => new float3(p.x, p.y, p.z)).ToArray(),
            Allocator.TempJob
        );
    }

    /// <summary>
    /// Applies calculated displacements back to vessel meshes.
    /// Converts world-space displaced positions back to local vertex coordinates.
    /// </summary>
    void ApplyDisplacementsToMeshes()
    {
        int outIdx = 0;
        bool anyVertexMoved = false;

        for (int vi = 0; vi < veins.Count; vi++)
        {
            var v = veins[vi];

            // Apply displacements to this vessel's vertices
            for (int i = 0; i < v.count; i++, outIdx++)
            {
                // Calculate new world position (original + displacement)
                float3 worldPos = originalPosWorld[outIdx] + displacements[outIdx];

                // Convert back to local space for mesh vertices
                var localPos = v.meshFilter.transform.InverseTransformPoint(new Vector3(worldPos.x, worldPos.y, worldPos.z));
                v.workingVerts[i] = localPos;

                // Track if any significant movement occurred (for collider update optimization)
                if (!anyVertexMoved && (v.originalVerts[i] - localPos).sqrMagnitude > 1e-6f)
                    anyVertexMoved = true;
            }

            // Update mesh data
            v.mesh.vertices = v.workingVerts;
            v.mesh.RecalculateNormals();
            v.mesh.RecalculateBounds();
            v.meshFilter.mesh = v.mesh;
            v.currentlyDeformed = true;

            // Update mesh collider only if vertices actually moved (performance optimization)
            if (v.meshCollider != null && anyVertexMoved)
            {
                // v.meshCollider.sharedMesh = null; // Required by Unity before reassignment
                v.meshCollider.sharedMesh = v.mesh;
            }
        }
    }

    /// <summary>
    /// Restores all vessels to their original, undeformed state.
    /// Called when compression is below threshold or compression data is invalid.
    /// </summary>
    void RestoreOriginalsIfNeeded()
    {
        foreach (var v in veins)
        {
            if (!v.currentlyDeformed) continue;

            // Restore original mesh state
            System.Array.Copy(v.originalVerts, v.workingVerts, v.count);
            v.mesh.vertices = v.originalVerts;
            v.mesh.RecalculateNormals();
            v.mesh.RecalculateBounds();
            v.meshFilter.mesh = v.mesh;

            // Update collider to match original mesh
            if (v.meshCollider != null)
            {
                v.meshCollider.sharedMesh = null;
                v.meshCollider.sharedMesh = v.mesh;
            }

            v.currentlyDeformed = false;
        }
    }

    // ============================================================
    // BACKWALL COLLISION DETECTION
    // ============================================================

    /// <summary>
    /// Calculates backwall collision constraints for each vertex to prevent over-compression.
    /// Uses raycast against BackplaneCollider to determine maximum allowed displacement.
    /// </summary>
    void BuildBackwallColumns(Vector3 centerV, Vector3 normalV)
    {
        if (!originalPosWorld.IsCreated) return;

        int count = originalPosWorld.Length;

        // Ensure arrays exist and match vertex count
        EnsureBackwallArraysExist(count);

        // Early exit if no backplane collider configured
        if (BackplaneCollider == null || BackplaneCollider.sharedMesh == null)
        {
            // Disable all constraints if no backplane available
            for (int i = 0; i < count; i++)
            {
                columnScale[i] = 0f;
                backwallDistance[i] = 0f;
            }
            return;
        }

        // Validate and normalize compression direction
        Vector3 compressionDir = normalV.normalized;
        if (compressionDir.sqrMagnitude < 1e-6f)
        {
            // Invalid compression direction - disable all constraints
            for (int i = 0; i < count; i++)
            {
                columnScale[i] = 0f;
                backwallDistance[i] = 0f;
            }
            return;
        }

        // Raycast direction is opposite to compression (toward backplane)
        Vector3 rayDir = -compressionDir;
        RaycastHit hit;

        // Process each vertex individually
        for (int i = 0; i < count; i++)
        {
            float3 pos3 = originalPosWorld[i];
            Vector3 vertexWorldPos = new Vector3(pos3.x, pos3.y, pos3.z);
            Vector3 rayStart = vertexWorldPos - rayDir * BackplaneRayStartOffset_mm;

            // Raycast from vertex toward backplane
            if (BackplaneCollider.Raycast(new Ray(rayStart, rayDir), out hit, BackplaneRayLength_mm))
            {
                // Calculate distances along compression direction
                float vertexDepth = Vector3.Dot(vertexWorldPos - centerV, rayDir);
                float hitDepth = Vector3.Dot(hit.point - centerV, rayDir);
                float availableDistance = hitDepth - vertexDepth;

                if (availableDistance <= 0f)
                {
                    // Vertex is already at or past backplane
                    backwallDistance[i] = 0f;
                    columnScale[i] = 0f;
                    continue;
                }

                // Calculate adaptive gap based on surface orientation
                // Vertices facing toward compression get smaller gap (more compression allowed)
                float surfaceDotCompression = Mathf.Clamp01(Vector3.Dot(originalNormalsWorld[i], rayDir));
                float adaptiveGap = MinGapToBackplane_mm * (1f - surfaceDotCompression);
                float allowedCompression = availableDistance - adaptiveGap;

                if (allowedCompression <= 0f)
                {
                    backwallDistance[i] = 0f;
                    columnScale[i] = 0f;
                }
                else
                {
                    backwallDistance[i] = allowedCompression;
                    columnScale[i] = 1f; // Full compression allowed up to backwall limit
                }
            }
            else
            {
                // No backplane hit - no constraint
                backwallDistance[i] = 0f;
                columnScale[i] = 0f;
            }
        }
    }

    /// <summary>
    /// Ensures backwall constraint arrays exist and match the required vertex count.
    /// </summary>
    void EnsureBackwallArraysExist(int count)
    {
        if (!columnScale.IsCreated || columnScale.Length != count)
        {
            if (columnScale.IsCreated) columnScale.Dispose();
            columnScale = new NativeArray<float>(count, Allocator.Persistent);
        }

        if (!backwallDistance.IsCreated || backwallDistance.Length != count)
        {
            if (backwallDistance.IsCreated) backwallDistance.Dispose();
            backwallDistance = new NativeArray<float>(count, Allocator.Persistent);
        }
    }

    // ============================================================
    // UTILITY METHODS
    // ============================================================

    /// <summary>
    /// Samples the falloff animation curve into a NativeArray for Burst job consumption.
    /// </summary>
    NativeArray<float> SampleCurve(int sampleCount)
    {
        var arr = new NativeArray<float>(sampleCount, Allocator.TempJob);
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1);
            arr[i] = FalloffCurve.Evaluate(t);
        }
        return arr;
    }
}

// ============================================================
// BURST COMPILED DISPLACEMENT JOB
// ============================================================

/// <summary>
/// High-performance Burst-compiled job for parallel vertex displacement calculation.
/// Uses shape-aware compression based on nearest contact points rather than simple radial distance.
/// </summary>
[BurstCompile]
public struct DisplacementJobBurst_mm : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> vertexPos;
    [WriteOnly] public NativeArray<float3> displacements;
    [ReadOnly] public NativeArray<float> columnScale;
    [ReadOnly] public NativeArray<float> backwallDistance;
    [ReadOnly] public NativeArray<float3> contactPoints;    // Probe contact points for shape-aware compression
    [ReadOnly] public float3 normal;                        // Compression direction (normalized)
    [ReadOnly] public float depth_mm;                       // Compression depth (mm)
    [ReadOnly] public float sensitivity;                    // Qualitative adjusment for soft vessels, not material stiffness (N/mm) as the physics was removed
    [ReadOnly] public float influenceRadius_mm;             // Maximum influence distance (mm)
    [ReadOnly] public float backwallRelax_mm;               // Safety offset from backwall (mm)
    [ReadOnly] public NativeArray<float> falloff;           // Sampled falloff curve

    [ReadOnly] public float maxCompression_mm;

    /// <summary>
    /// Executes displacement calculation for a single vertex.
    /// Uses physics-based force model with shape-aware influence based on nearest contact point.
    /// </summary>
    public void Execute(int i)
    {
        float3 vertexPos = this.vertexPos[i];

        // Find distance to nearest probe contact point (shape-aware compression)
        float nearestContactDistance = FindNearestContactDistance(vertexPos);

        if (nearestContactDistance < 0f) // No contact points or vertex outside influence
        {
            displacements[i] = float3.zero;
            return;
        }

        // Check backwall scaling constraint
        float colScale = columnScale[i];
        if (colScale <= 1e-4f)
        {
            displacements[i] = float3.zero;
            return;
        }

        // Calculate falloff based on distance from nearest contact
        float normalizedDistance = math.saturate(nearestContactDistance / influenceRadius_mm);
        float falloffValue = SampleCurve(normalizedDistance, falloff);

        // Validate compression normal
        float normalLength = math.length(normal);
        if (normalLength <= 1e-6f)
        {
            displacements[i] = float3.zero;
            return;
        }
        float3 compressionDirection = normal / normalLength;


        // Each vertex moves a fraction of its own available room to the backwall.
        // Anterior vertices (far from the backwall) have lots of room so they move a lot.
        // Posterior vertices (already close to the backwall) have little room so they barely move.
        // This is what creates the oval gradient instead of a flat cap.
        // The alpha * alpha quadratic ramp keeps the response gentle at light contact and ramps harder as the probe pushes deeper,
        // preventing the lumen from rushing to the backwall immediately.
        float alpha = math.saturate(depth_mm / maxCompression_mm);
        alpha = alpha * alpha;
        float displacement_mm = alpha * falloffValue * colScale * backwallDistance[i];
        displacement_mm *= sensitivity; // this is a qualitative adjustment for very soft veins or low blood pressure veins

        // Apply backwall constraint (hard stop)
        float maxAllowedDisplacement = backwallDistance[i] - backwallRelax_mm;
        if (maxAllowedDisplacement <= 1e-4f)
        {
            displacements[i] = float3.zero;
            return;
        }

        if (displacement_mm > maxAllowedDisplacement)
            displacement_mm = maxAllowedDisplacement;

        // Apply displacement along compression direction (toward backwall)
        displacements[i] = -compressionDirection * displacement_mm;
    }

    /// <summary>
    /// Finds the distance to the nearest probe contact point.
    /// Returns -1 if no contact points exist or vertex is outside influence radius.
    /// </summary>
    float FindNearestContactDistance(float3 vertexPos)
    {
        if (contactPoints.Length == 0)
            return -1f;

        float minDistance = float.MaxValue;
        for (int c = 0; c < contactPoints.Length; c++)
        {
            float distance = math.distance(vertexPos, contactPoints[c]);
            if (distance < minDistance)
                minDistance = distance;
        }

        // Return -1 if outside influence radius
        return minDistance <= influenceRadius_mm ? minDistance : -1f;
    }

    /// <summary>
    /// Samples the falloff curve at the given normalized position (0-1).
    /// </summary>
    static float SampleCurve(float t, NativeArray<float> samples)
    {
        t = math.clamp(t, 0f, 1f);
        float indexFloat = t * (samples.Length - 1);
        int i0 = (int)math.floor(indexFloat);
        int i1 = math.min(i0 + 1, samples.Length - 1);
        float fraction = indexFloat - i0;
        return math.lerp(samples[i0], samples[i1], fraction);
    }
}