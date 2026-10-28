using UnityEngine;

// Defines the patient's anatomical (DICOM LPS) frame: three body axes plus the
// two reference points used to express positions in that frame — the coordinate
// origin and the midline point that defines the midsagittal plane.
public class LPS_AnatomicCoordinates : MonoBehaviour
{
    public enum WorldAxis { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }
    public enum AxisMode { WorldConstants, FromTransform }

    [Tooltip("A point on the patient midline; its patient-left coordinate defines the midsagittal plane. Empty = world origin.")]
    public Transform midline;

    [Tooltip("Origin of the coordinates reported to the LLM. Spec §7: each simulator defines its own — a landmark, " +
             "the centre of the scan volume, or any reference point. Empty = world origin. This only affects what " +
             "BuildContext() reports; it never affects how commands are executed.")]
    public Transform origin;

    [Header("Body axes — how a direction word becomes a world vector")]
    [Tooltip("World Constants: pick a fixed world axis per body direction (static, axis-aligned scene). " +
             "From Transform: read the axes live from a Transform that another script orients (re-posing rig, animation, physics).")]
    public AxisMode axisMode = AxisMode.WorldConstants;

    [Tooltip("World Constants mode — toward the head.")]
    public WorldAxis cephaladAxis = WorldAxis.PlusZ;
    [Tooltip("World Constants mode — toward the front.")]
    public WorldAxis anteriorAxis = WorldAxis.PlusY;
    [Tooltip("World Constants mode — the patient's left.")]
    public WorldAxis patientLeftAxis = WorldAxis.PlusX;

    [Tooltip("From Transform mode — the source is read as the PATIENT's own frame, in the standard character " +
             "convention: its up is toward the head (cephalad), its forward is toward the face (anterior), and " +
             "its right is the patient's RIGHT — so patient-left is -right. Orient it from any other script " +
             "(rig, animation, physics) to drive the axes live.")]
    public Transform axisSource;

    // The three body axes in world space, from whichever mode is active.
    // FromTransform reads the source as the patient's own frame, so patient-left
    // is -right: for any Transform, .right is the subject's RIGHT. Using +right
    // here would both invert laterality and make the L/P/S triple mirrored
    // (L x P would land on +S; DICOM LPS requires -S in Unity's left-handed
    // space) — no orientation of the source can compensate for that.
    public Vector3 SuperiorAxis => Custom ? axisSource.up : Vec(cephaladAxis);
    public Vector3 AnteriorAxis => Custom ? axisSource.forward : Vec(anteriorAxis);
    public Vector3 LeftAxis => Custom ? -axisSource.right : Vec(patientLeftAxis);
    bool Custom => axisMode == AxisMode.FromTransform && axisSource != null;

    static Vector3 Vec(WorldAxis a)
    {
        switch (a)
        {
            case WorldAxis.PlusX: return Vector3.right;
            case WorldAxis.MinusX: return Vector3.left;
            case WorldAxis.PlusY: return Vector3.up;
            case WorldAxis.MinusY: return Vector3.down;
            case WorldAxis.PlusZ: return Vector3.forward;
            case WorldAxis.MinusZ: return Vector3.back;
            default: return Vector3.forward;
        }
    }
}