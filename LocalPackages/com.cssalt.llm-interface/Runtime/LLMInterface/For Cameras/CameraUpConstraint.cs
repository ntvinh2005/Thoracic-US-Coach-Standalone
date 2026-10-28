using UnityEngine;

// Keeps a camera level. After anything moves the camera, roll it about its OWN
// view axis so its +Y (up) points as near as it can to a fixed world axis.
//
// The view DIRECTION is never touched — only the roll about it — so this cannot
// fight an Aim, a PointAt or a Viewpoint. Those commands decide where the camera
// looks; this only removes the twist they leave behind. (Swinging the view onto a
// target fixes two of the three rotational degrees of freedom; the third, roll, is
// unconstrained, which is why a Viewpoint can land correctly aimed but cockeyed.)
//
// Runs in LateUpdate, AFTER the ContextCommander replay coroutine has moved the
// camera this frame, so the camera stays level THROUGHOUT a glide rather than
// snapping upright at the end. It composes safely with that replay: the replay
// turns about a FIXED world axis, and rolling about the view axis does not change
// the view axis, so each step still traces the intended arc.
//
// STABILITY NOTE (this project has been bitten before): this is a HARD constraint,
// not a spring, and it is safe every frame. The correction is a projection onto the
// level pose — it converges exactly in one step and re-applying it is a no-op. That
// is a different shape from the probe's rock/tilt experiment, where a corrective
// rotation fed back into the measurement that produced it and oscillated.
public class CameraUpConstraint : MonoBehaviour
{
    public enum WorldAxis { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }

    [Header("Target")]
    // The camera (or its root) to keep level. Empty = this GameObject.
    public Transform CameraRoot;

    [Header("Up")]
    // The world axis the camera's +Y should point toward, as near as the current
    // view direction allows.
    public WorldAxis UpAxis = WorldAxis.PlusY;

    [Header("Behaviour")]
    public bool Constrain = true;

    // Within this many degrees of looking straight along the up axis, "level" is
    // undefined — every roll is equally level — so the roll is left alone. Without
    // this the camera spins arbitrarily as it passes through the pole. Relevant in
    // practice: a superior/inferior Viewpoint looks along the head-foot axis.
    public float DegeneracyGuard_deg = 2f;

    [Header("Debug (runtime, read-only)")]
    [SerializeField] float rollRemoved_deg;
    [SerializeField] string state = "-";

    void Reset() { CameraRoot = transform; }

    static Vector3 Vec(WorldAxis a)
    {
        switch (a)
        {
            case WorldAxis.PlusX:  return Vector3.right;
            case WorldAxis.MinusX: return Vector3.left;
            case WorldAxis.PlusY:  return Vector3.up;
            case WorldAxis.MinusY: return Vector3.down;
            case WorldAxis.PlusZ:  return Vector3.forward;
            default:               return Vector3.back;
        }
    }

    void LateUpdate()
    {
        var cam = CameraRoot != null ? CameraRoot : transform;
        if (!Constrain) { state = "off"; return; }

        Vector3 fwd = cam.forward;
        Vector3 want = Vec(UpAxis);

        // Looking (nearly) along the up axis: no roll is more level than any other.
        if (Mathf.Abs(Vector3.Dot(fwd, want)) > Mathf.Cos(DegeneracyGuard_deg * Mathf.Deg2Rad))
        {
            rollRemoved_deg = 0f;
            state = "degenerate — looking along the up axis, roll left as-is";
            return;
        }

        // LookRotation keeps `fwd` EXACTLY and puts up in the fwd/want plane — i.e.
        // a pure roll about the view axis, with no change to where the camera aims.
        Quaternion level = Quaternion.LookRotation(fwd, want);
        rollRemoved_deg = Quaternion.Angle(cam.rotation, level);
        if (rollRemoved_deg > 1e-4f) cam.rotation = level;
        state = "level";
    }
}
