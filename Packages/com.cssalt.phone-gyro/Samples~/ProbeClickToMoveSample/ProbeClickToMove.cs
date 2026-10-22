using UnityEngine;
using UnityEngine.EventSystems;

/*
Overview: click anywhere on the skin mesh and the Tracked Ultrasound Probe jumps so its
Face Center Marker (a child marking the center of the probe's contact face) lands on the click point,
with the probe rotated so its face is flush with (perpendicular to) the skin surface there.
*/

public class ProbeClickToMove : MonoBehaviour
{
    [Header("Probe Refs")]
    public Transform TrackedUltrasoundProbe;
    public Transform FaceCenterMarker;

    [Header("Click Detection")]
    public Camera ClickCamera;          // leave empty to use Camera.main
    public LayerMask SkinLayer;         // assign the same Skin layer used in Simulation Manager
    public float MaxRayDistance = 10000f;
    public int MouseButton = 0;         // 0 = left

    [Header("Orientation")]
    // Which local axis of the FaceCenterMarker points OUT of the patient (away from skin).
    // Flip if the probe ends up upside down or sideways.
    public FaceAxis MarkerOutwardAxis = FaceAxis.Forward;
    public enum FaceAxis { Up, Down, Forward, Back, Right, Left }

    void Update()
    {
        if (!Input.GetMouseButtonDown(MouseButton)) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (TrackedUltrasoundProbe == null || FaceCenterMarker == null) return;

        Camera cam = ClickCamera != null ? ClickCamera : Camera.main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, MaxRayDistance, SkinLayer)) return;

        // Rotate first, then translate. Rotating the parent moves the child marker, so the offset
        // must be measured AFTER rotation or the marker won't land on the click point.
        Vector3 currentOutward = MarkerAxisWorld(FaceCenterMarker, MarkerOutwardAxis);
        Quaternion alignToNormal = Quaternion.FromToRotation(currentOutward, hit.normal);
        TrackedUltrasoundProbe.rotation = alignToNormal * TrackedUltrasoundProbe.rotation;

        TrackedUltrasoundProbe.position += hit.point - FaceCenterMarker.position;
    }

    static Vector3 MarkerAxisWorld(Transform t, FaceAxis axis)
    {
        switch (axis)
        {
            case FaceAxis.Up:      return t.up;
            case FaceAxis.Down:    return -t.up;
            case FaceAxis.Forward: return t.forward;
            case FaceAxis.Back:    return -t.forward;
            case FaceAxis.Right:   return t.right;
            case FaceAxis.Left:    return -t.right;
            default:               return t.up;
        }
    }
}
