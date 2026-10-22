using System.Collections.Generic;
using UnityEngine;

// Formerly "RaycastCompressionBridge".
//
// ICompressionProvider now ships alongside this file, so the package is the ONE
// definition of it. A host simulator that used to declare its own copy (SMMARTS did)
// must DELETE that copy and use this one: the name is in the global namespace, and
// Assembly-CSharp automatically references package assemblies, so two definitions
// would be visible at once and every unqualified use in the host would fail to
// compile as ambiguous (CS0433). One definition, one type, no adapter.
//
// Note that ProbeSurfaceClamp holds the CONCRETE type rather than the interface,
// because it also needs Measure(), SignedDepth and AnyHit, which are not on it.
public class SkinIntersectionCompression : MonoBehaviour, ICompressionProvider
{
    public enum Axis { X, Y, Z, NegX, NegY, NegZ }

    [Header("Center & Normal")]
    public Transform CenterTransform;      // probe tip
    public Transform RearCenterTransform; // probe tail
    public Transform NormalFromTransform;
    public Axis normalAxis = Axis.NegY;

    [Header("Target")]
    // [Tooltip("Optional: assign the exact skin/gel collider to raycast against (recommended). If null, uses Physics.Raycast with SkinLayer.")]
    public Collider SkinCollider;

    [Header("Grid Spread (local tangent plane)")]
    // [Tooltip("Number of samples across grid width (>=1)")]
    public int GridCols = 5;
    // [Tooltip("Number of samples across grid height (>=1)")]
    public int GridRows = 5;
    // [Tooltip("Total grid width in mm")]
    public float GridWidth_mm = 8f;
    // [Tooltip("Total grid height in mm")]
    public float GridHeight_mm = 8f;

    [Header("Raycast Settings")]
    public LayerMask SkinLayer;
    // [Tooltip("Distance from tip to ray start position OUTSIDE the skin (mm).")]
    public float RayStartBackoff_mm = 50f;
    // [Tooltip("Extra ray length beyond the tip (mm). Mostly just safety margin.")]
    public float ExtraRayLength_mm = 100f;

    [Header("Stability")]
    public float MinDepthThreshold_mm = 0.01f;

    public Vector3 CompressionCenter { get; private set; }
    public Vector3 CompressionNormal { get; private set; }
    public float CompressionDepth { get; private set; }
    public float ContactPercent { get; private set; } // this is a fraction. goes to 1 as soon as the probe touches the flat skin

    // NEW: Expose actual contact points
    public Vector3[] ContactPoints { get; private set; }

    // --- signed penetration (added for the two-sided surface constraint) -------
    // SignedDepth: the largest penetration across the HIT footprint samples.
    //   > 0  buried  (mm into tissue, the deepest point)
    //   < 0  floating (mm gap from the skin to the NEAREST footprint point)
    // Only meaningful when AnyHit is true. CompressionDepth stays max(0, signed)
    // for existing consumers; this exposes the sign the floor needs to pull the
    // probe back down onto the skin.
    public float SignedDepth { get; private set; }
    public bool AnyHit { get; private set; }

    // Store contact points as we calculate them
    private List<Vector3> frameContactPoints = new List<Vector3>();


    [Header("Debug Values - Runtime Only")]
    [SerializeField] private Vector3 debugCenter;
    [SerializeField] private Vector3 debugNormal;
    [SerializeField] private float debugDepth;
    [SerializeField] private float debugContact;



    [Header("Debug")]
    public bool DrawDebugRays = false;

    void OnValidate()
    {
        GridCols = Mathf.Max(1, GridCols);
        GridRows = Mathf.Max(1, GridRows);
        GridWidth_mm = Mathf.Max(0f, GridWidth_mm);
        GridHeight_mm = Mathf.Max(0f, GridHeight_mm);

        RayStartBackoff_mm = Mathf.Max(0.001f, RayStartBackoff_mm);
        ExtraRayLength_mm = Mathf.Max(0f, ExtraRayLength_mm);
        MinDepthThreshold_mm = Mathf.Max(0f, MinDepthThreshold_mm);
    }

    void Update()
    {
        Measure();
    }

    // Recompute all outputs (center, normal, depth, signed depth, contact) at the
    // probe's CURRENT position. Runs every frame via Update for general consumers,
    // but is public so the surface clamp can call it on demand in LateUpdate —
    // AFTER an animation coroutine has moved the probe this frame. Reading the
    // Update-time value there is stale by one frame (the coroutine moves the probe
    // between Update and LateUpdate), which makes a constraint lag fast motion.
    public void Measure()
    {
        // 1) Center (tip)
        CompressionCenter = CenterTransform != null ? CenterTransform.position : transform.position;

        // 2) Normal (outward)
        CompressionNormal = GetNormalWorld();
        if (CompressionNormal.sqrMagnitude < 1e-10f) CompressionNormal = Vector3.up;

        // 3) Depth via raycasts
        CompressionDepth = ComputePenetrationDepth(CompressionCenter, CompressionNormal);

        if (CompressionDepth < MinDepthThreshold_mm)
            CompressionDepth = 0f;

        debugCenter = CompressionCenter;
        debugNormal = CompressionNormal;
        debugDepth = CompressionDepth;
        debugContact = ContactPercent;
    }

    Vector3 GetNormalWorld()
    {
        return RearCenterTransform.position - CenterTransform.position;
    }

    float ComputePenetrationDepth(Vector3 tip, Vector3 outwardNormal)
    {
        outwardNormal.Normalize();

        float signedMax = float.NegativeInfinity;   // max over HIT samples, may be negative (floating)
        bool anyHit = false;

        Transform frame = CenterTransform != null ? CenterTransform : transform;

        //we use probe-local axes
        //build up tangent vectors for horizontal and
        Vector3 localRight = frame.right;
        Vector3 localUp = frame.up;

        Vector3 t1 = Vector3.ProjectOnPlane(localRight, outwardNormal).normalized;
        Vector3 t2 = Vector3.ProjectOnPlane(localUp, outwardNormal).normalized;

        if (t1.sqrMagnitude < 1e-8f)
            t1 = Vector3.ProjectOnPlane(frame.forward, outwardNormal).normalized;

        if (t2.sqrMagnitude < 1e-8f)
            t2 = Vector3.Cross(outwardNormal, t1).normalized;


        Vector3 rayDir = -outwardNormal;

        float rayLength = RayStartBackoff_mm + ExtraRayLength_mm;

        float halfW = GridWidth_mm * 0.5f;
        float halfH = GridHeight_mm * 0.5f;

        int ContactCount = 0;
        int totalCount = GridRows * GridCols;
        ContactPercent = 0f;


        // Clear previous frame's contact points
        frameContactPoints.Clear();


        for (int row = 0; row < GridRows; row++)
        {
            float v = (GridRows == 1) ? 0f : Mathf.Lerp(-halfH, halfH, row / (float)(GridRows - 1));

            for (int col = 0; col < GridCols; col++)
            {
                float u = (GridCols == 1) ? 0f : Mathf.Lerp(-halfW, halfW, col / (float)(GridCols - 1));

                Vector3 offset = t1 * u + t2 * v;

                Vector3 rayOrigin = tip + outwardNormal * RayStartBackoff_mm + offset;

                bool hit;
                RaycastHit hitInfo;
                Ray ray = new Ray(rayOrigin, rayDir);

                if (DrawDebugRays) Debug.DrawRay(rayOrigin, rayDir * rayLength, Color.green);

                if (SkinCollider != null)
                {
                    hit = SkinCollider.Raycast(ray, out hitInfo, rayLength);
                }
                else
                {
                    hit = Physics.Raycast(rayOrigin, rayDir, out hitInfo, rayLength, SkinLayer, QueryTriggerInteraction.Collide);
                }


                if (hit)
                {
                    anyHit = true;
                    float depth = RayStartBackoff_mm - hitInfo.distance;
                    if (depth > signedMax) signedMax = depth;   // keep the sign (negative = floating)
                    if (depth > 0f)
                    {
                        ContactCount++;
                        // NEW: Store the actual contact point
                        frameContactPoints.Add(hitInfo.point);
                    }
                }

            }
        }

        // Update the contact points array
        ContactPoints = frameContactPoints.ToArray();
        ContactPercent = (float)ContactCount / totalCount; // not a percent. this is a fraction. also, it goes to 1 for a flat probe barely touching a flat skin.

        // Signed reading for the two-sided constraint; 0 / false when nothing was hit.
        AnyHit = anyHit;
        SignedDepth = anyHit ? signedMax : 0f;

        return anyHit ? Mathf.Max(0f, signedMax) : 0f;

    }
}
