using UnityEngine;

/// <summary>
/// CrossSectionManager - Global controller for cross-section rendering effects
/// 
/// This component controls cross-section clipping for CrossSection shaders.
/// The system automatically updates all CrossSection materials when this component moves.
/// CrossSection Shaders work with or without this manager - no manager means no cross-section effect.
/// 
/// Position and rotate the GameObject to define where the cross-section cuts - and we can do alot of that for you for common useage.
/// If you like, attach the script component to a Unity plane primitive for visual reference.
/// 
/// Supports three modes:
/// - FollowTransform: Uses transform orientation directly 
/// - FaceCamera: Always faces toward the target camera, clipping everything in front
/// - SmartFollow: Follows transform but flips direction when camera crosses to other side
/// 
/// Usage:
/// - Set mode and optional targetObject/targetCamera
/// - Enable/disable component or GameObject to turn cross-sections on/off
/// - Position updates automatically based on optional targetObject or transform hierarchy
/// 
/// Common Code Examples for "Hybrid Cross Section":
/// // Ultrasound Probe (most common application)
/// CrossSectionManager.ME.targetObject = (Face Center Marker - its under 3D Model of Probe);
/// CrossSectionManager.ME.mode = CrossSectionMode.SmartFollow;
/// 
/// // Needle tip
/// CrossSectionManager.ME.targetObject = NeedleTipMarker;
/// CrossSectionManager.ME.mode = CrossSectionMode.FaceCamera;
/// 
/// // Turn cross-section on/off
/// CrossSectionManager.ME.gameObject.SetActive(false);
/// 
/// Singleton access: CrossSectionManager.ME
/// </summary>


public enum CrossSectionMode
{
    FollowTransform,  // Basic - use transform.up as-is
    FaceCamera,       // Always point toward target camera
    SmartFollow       // Follow transform but flip when camera crosses over
}

public class CrossSectionManager : MonoBehaviour
{
    [Header("Cross Section Settings")]
    public CrossSectionMode mode = CrossSectionMode.FollowTransform;

    [Header("Targets")]
    public GameObject targetObject;  // If null, uses this transform
    public Camera targetCamera;      // If null, uses Camera.main

    private static CrossSectionManager m_instance;

    public static CrossSectionManager ME => m_instance;

    void Awake()
    {
        if (m_instance != null)
        {
            DestroyImmediate(this.gameObject);
            return;
        }
        m_instance = this;
    }

    void Start() => EnableCrossSection();
    void LateUpdate() => SetSectionPlane();
    void OnEnable() => EnableCrossSection();
    void OnDisable() => DisableCrossSection();
    void OnDestroy() => m_instance = null;

    void SetSectionPlane()
    {
        // Handle parenting based on targetObject
        if (targetObject != null)
        {
            if (transform.parent != targetObject.transform)
            {
                transform.SetParent(targetObject.transform);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
        }
        else
        {
            if (transform.parent != null)
            {
                transform.SetParent(null);
            }
        }

        Vector3 sectionNormal = CalculateSectionNormal();
        Shader.SetGlobalVector("_SectionPoint", transform.position);
        Shader.SetGlobalVector("_SectionPlane", sectionNormal);
    }

    Vector3 CalculateSectionNormal()
    {
        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null) return transform.up; // Fallback if no camera found

        switch (mode)
        {
            case CrossSectionMode.FaceCamera:
                return (cam.transform.position - transform.position).normalized;

            case CrossSectionMode.SmartFollow:
                Vector3 baseNormal = transform.up;
                Vector3 cameraDirection = (cam.transform.position - transform.position).normalized;

                // Flip if camera is on the "back" side of the plane
                if (Vector3.Dot(baseNormal, cameraDirection) < 0)
                {
                    baseNormal = -baseNormal;
                }
                return baseNormal;

            default: // FollowTransform
                return transform.up;
        }
    }

    void EnableCrossSection()
    {
        Shader.DisableKeyword("CLIP_NONE");
        Shader.EnableKeyword("CLIP_PLANE");
    }

    void DisableCrossSection()
    {
        Shader.DisableKeyword("CLIP_PLANE");
        Shader.EnableKeyword("CLIP_NONE");
    }
}