using UnityEngine;

// Tool profile data (spec: TOOL-ABSTRACTION-SPEC.md §T2, §T5, §T6).
//
// A tool's vocabulary is DATA, not code. A verb selects an AXIS; a qualifier
// selects the SIGN along it (T5.2). That single rule is what lets a probe, a
// needle, a camera and a ghost probe differ only by what is typed into these
// fields, while every one of them drives the same two motion primitives.
//
// These types live in ContextCommander's array for now — deliberately the
// simplest thing that works. If sharing one vocabulary across many tools ever
// becomes painful enough to matter, they can move to a ScriptableObject without
// changing the resolution rules.

/// <summary>A signed local axis of a tool. Distinct from WorldAxis, which maps the patient's body axes onto world axes.</summary>
public enum SignedAxis { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }

/// <summary>Which of the two motion primitives (spec T4) a proposed motion is.</summary>
public enum MotionKind { Translate, Rotate }

/// <summary>A motion about to be applied, or just applied, to a tool (spec T7.3 / T7.6).</summary>
public struct ToolMotion
{
    public MotionKind kind;
    public Transform tool;
    /// <summary>Rotations only: the world-space pivot.</summary>
    public Vector3 pivot;
    /// <summary>Signed unit vector — the direction of travel, or the axis a positive turn is about.</summary>
    public Vector3 axis;
    /// <summary>Millimetres for a translation, degrees for a rotation. Always positive (T0.4).</summary>
    public float magnitude;
    public float seconds;
}

// The host's hook into a tool's motion (spec T7). The package owns this
// interface and never references any simulator type; a host implements it and
// the dependency points one way only (T7.2).
//
// This is what lets simulator-specific physics — a probe's tissue-pressure
// limit, a needle permitting less lateral travel the deeper it sits — attach to
// a tool without the LLM layer knowing any of it exists.
public interface IToolMotionSubscriber
{
    /// <summary>
    /// Called before a motion is applied (T7.3). Return the magnitude to apply.
    /// It may be REDUCED — including to zero, which is a refusal (T7.4) — but is
    /// never allowed to increase or redirect the motion. Set <paramref name="reason"/>
    /// whenever you reduce: a reduction reported as though it were achieved is a
    /// defect (T7.5), and the reason is spoken back to the operator and the model.
    /// </summary>
    float BeforeMotion(ToolMotion motion, out string reason);

    /// <summary>Called after the motion completes (T7.6). Return a short note, or "" for nothing to report.</summary>
    string AfterMotion(ToolMotion motion);
}

/// <summary>Which end of the tool a ROTATION qualifier describes (spec T5.7 / T5.8).</summary>
public enum QualifierReference
{
    /// <summary>"toward the head" means the BUSINESS END (beam, tip, view) aims that way. The spec default.</summary>
    BusinessEnd,
    /// <summary>"toward the head" means the FAR END (handle) leans that way, so the business end goes the opposite way.</summary>
    FarEnd,
}

// The vocabulary for ONE local axis: the verbs that select it, and the
// qualifiers that pick each direction along it.
//
// Several verbs on one axis are synonyms and must behave identically (T6.2) —
// this is how "caudad"/"cephalic"/"superior" style synonyms are supported
// without touching code.
//
// Qualifiers listed here are TOOL-LOCAL words ("toward the notch"). Anatomical
// qualifiers ("cephalad", "left") always work and need not be listed: they are
// resolved in the patient frame and then projected onto this axis to pick the
// nearer sign (T5.4).
[System.Serializable]
public class AxisVerbs
{
    // [Tooltip("Words that select THIS axis. Several are synonyms and behave identically (T6.2). Leave empty to disable this axis for this tool.")]
    public string[] verbs;

    // [Tooltip("Tool-local words selecting the POSITIVE direction along this axis, e.g. 'toward notch'. Anatomical words always work and need not be listed.")]
    public string[] plusQualifiers;

    // [Tooltip("Tool-local words selecting the NEGATIVE direction along this axis.")]
    public string[] minusQualifiers;
}

// One tool: the Transform, its frame roles, its pivot, and its vocabulary.
//
// Anything a tool needs that is NOT shared with other tools lives here rather
// than on the commander. That is deliberate: a single global contactPoint and a
// single global beam-vs-handle setting are exactly what stops a probe and a
// needle from coexisting today.
[System.Serializable]
public class ToolBinding
{
    // [Tooltip("The tool this profile describes.")]
    public Transform tool;

    [Header("Frame")]
    // [Tooltip("The signed local axis the tool WORKS ALONG (T2.4): the probe's beam (-Z), a camera's view (+Z), a needle's tip. " +
             // "Rotation qualifiers are described relative to this end, and it fixes the clock plane.")]
    public SignedAxis businessEnd = SignedAxis.MinusZ;

    // [Tooltip("Rotation pivot (T3.1). Rotations only — translations never pivot. Empty = the tool's own origin. " +
             // "Read live, so external code may move it (this is how a needle can pivot about a skin entry point that did not exist a moment ago).")]
    public Transform pivot;

    // [Tooltip("What a ROTATION qualifier describes (T5.8). Far End (default): 'tilt toward the head' leans the HANDLE that way, " +
             // "so the business end (beam/tip) goes the OPPOSITE way — the pivot effect. Business End: it aims the beam/tip that " +
             // "way instead. Both are taught clinically; set it per tool.")]
    public QualifierReference rotationQualifiersDescribe = QualifierReference.FarEnd;

    [Header("Local translation verbs (T5, T6)")]
    public AxisVerbs translateX = new AxisVerbs();
    public AxisVerbs translateY = new AxisVerbs();
    public AxisVerbs translateZ = new AxisVerbs();

    [Header("Local rotation verbs (T5, T6)")]
    public AxisVerbs rotateX = new AxisVerbs();
    public AxisVerbs rotateY = new AxisVerbs();
    public AxisVerbs rotateZ = new AxisVerbs();

    [Header("Clockface (T8) — optional")]
    // [Tooltip("Does this tool carry an orientation marker addressable as a clock face? A camera does not.")]
    public bool clockfaceEnabled = false;

    // [Tooltip("Which signed local axis is the orientation marker (the probe's notch). Must be perpendicular to the business end (T8.3).")]
    public SignedAxis markerAxis = SignedAxis.PlusX;

    // [Tooltip("Which anatomical direction is 12 o'clock (T8.4). Usually 'cephalad'.")]
    public string noonDirection = "cephalad";

    [Header("Imaging planes (§2) — optional")]
    // [Tooltip("Does this tool have an IMAGING PLANE that can be aligned to a named anatomical plane (sagittal/coronal/transverse/" +
             // "midsagittal)? This is the probe's View command. A camera does NOT — it has viewpoints (below), not imaging planes. " +
             // "Gating this keeps an overloaded word like 'sagittal' from landing on a tool that has no imaging plane.")]
    public bool imagingPlanesEnabled = false;

    [Header("Anatomical viewpoints (§3) — optional")]
    // [Tooltip("Does this tool take anatomical VIEWPOINTS (AP/PA/lateral/superior/inferior)? This is an OBSERVER capability — " +
             // "a camera does it, a probe does not (a probe has imaging PLANES, which is the separate View command). A viewpoint " +
             // "positions the tool relative to the patient AND aims its business end at the subject.")]
    public bool viewpointsEnabled = false;

    // [Tooltip("How far from the subject a viewpoint places the tool, in mm. 0 = use 300 mm. The subject itself is the " +
             // "commander's `origin` (patient centre) by default, or a target named in the command.")]
    public float viewpointDistanceMm = 300f;

    [Header("Host subscriptions (T7) — optional")]
    // [Tooltip("MonoBehaviours implementing IToolMotionSubscriber, which may limit this tool's motion before it is applied " +
             // "and react after — a probe's tissue-pressure limit, a needle's depth-vs-lateral limit, a travel range. " +
             // "Each may only REDUCE, so several compose as the MINIMUM and the order you list them in does not matter. " +
             // "Empty = no constraint; the tool moves freely. Per tool, so a live probe and a ghost probe differ only here.")]
    public MonoBehaviour[] subscribers;

    /// <summary>The translation vocabulary for local axis index 0=X, 1=Y, 2=Z.</summary>
    public AxisVerbs Translate(int axis) => axis == 0 ? translateX : axis == 1 ? translateY : translateZ;

    /// <summary>The rotation vocabulary for local axis index 0=X, 1=Y, 2=Z.</summary>
    public AxisVerbs Rotate(int axis) => axis == 0 ? rotateX : axis == 1 ? rotateY : rotateZ;

    /// <summary>World-space unit vector for a signed local axis of this tool. Read live from the transform (T2.1).</summary>
    public static Vector3 AxisVector(Transform t, SignedAxis a)
    {
        switch (a)
        {
            case SignedAxis.PlusX:  return  t.right;
            case SignedAxis.MinusX: return -t.right;
            case SignedAxis.PlusY:  return  t.up;
            case SignedAxis.MinusY: return -t.up;
            case SignedAxis.PlusZ:  return  t.forward;
            default:                return -t.forward;
        }
    }

    /// <summary>World-space unit vector for unsigned local axis index 0=X, 1=Y, 2=Z.</summary>
    public static Vector3 AxisVector(Transform t, int axis)
        => axis == 0 ? t.right : axis == 1 ? t.up : t.forward;

    /// <summary>The tool's business-end direction in world space (T2.4).</summary>
    public Vector3 BusinessEndVector => tool != null ? AxisVector(tool, businessEnd) : Vector3.forward;

    // The axis a twist turns about (T8.2 — the roll axis and the business-end
    // axis coincide), oriented so that looking along its NEGATIVE looks along the
    // business end. That is the operator's viewpoint for "clockwise" (T8.7):
    // standing at the far end, looking toward the working end. For a probe this
    // is +Z (handle-ward, so -Z is the beam); for a camera it is -Z, so that the
    // operator still looks along the view direction.
    public Vector3 TwistAxis => -BusinessEndVector;
}
