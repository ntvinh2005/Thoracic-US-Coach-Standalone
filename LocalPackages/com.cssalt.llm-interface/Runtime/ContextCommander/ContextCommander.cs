using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// The Unity side of the LLM bridge: report scene state out, apply named
// commands in. The model picks a direction + amount; the sign is resolved from
// the Body Axes config below (set per project in the Inspector).
public class ContextCommander : MonoBehaviour
{
    public enum WorldAxis { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }
    public enum AxisMode { WorldConstants, FromTransform }

    public float unitsPerMm = 1f;

    [Header("Objects")]
    // [Tooltip("Everything the LLM may MOVE or ROTATE — one entry per tool. A tool with no verb tables still moves anatomically " +
             // "(Move/MoveToward/RotateAbout, T6.5); authoring verbs adds its LOCAL motions (Slide/Rock/…). The FIRST entry is the " +
             // "primary tool (T1.3), the one 'it'/'the tool' resolves to.")]
    public ToolBinding[] tools;

    // [Tooltip("Reference-only points: valid as MoveToward targets and Rotate pivots, but never moved themselves (e.g. anatomy landmarks).")]
    public Transform[] landmarks;

    // [Tooltip("A point on the patient midline; its patient-left coordinate defines the midsagittal plane. Empty = world origin.")]
    public Transform midline;

    // [Tooltip("Origin of the coordinates reported to the LLM. Spec §7: each simulator defines its own — a landmark, " +
             // "the centre of the scan volume, or any reference point. Empty = world origin. This only affects what " +
             // "BuildContext() reports; it never affects how commands are executed.")]
    public Transform origin;

    // [Tooltip("Within this many mm of the midline, medial/lateral is ambiguous and the move is refused (the assistant asks which side).")]
    public float midlineDeadzoneMm = 10f;

    // [Tooltip("Scene-view wireframe size drawn at each object (movable = green cube, landmark = blue sphere). 0 = off.")]
    public float gizmoRadius = 0f;

    [Header("Body axes — how a direction word becomes a world vector")]
    // [Tooltip("World Constants: pick a fixed world axis per body direction (static, axis-aligned scene). " +
             // "From Transform: read the axes live from a Transform that another script orients (re-posing rig, animation, physics).")]
    public AxisMode axisMode = AxisMode.WorldConstants;

    // [Tooltip("World Constants mode — toward the head.")]
    public WorldAxis cephaladAxis = WorldAxis.PlusZ;
    // [Tooltip("World Constants mode — toward the front.")]
    public WorldAxis anteriorAxis = WorldAxis.PlusY;
    // [Tooltip("World Constants mode — the patient's left.")]
    public WorldAxis patientLeftAxis = WorldAxis.PlusX;

    // [Tooltip("From Transform mode — the source is read as the PATIENT's own frame, in the standard character " +
             // "convention: its up is toward the head (cephalad), its forward is toward the face (anterior), and " +
             // "its right is the patient's RIGHT — so patient-left is -right. Orient it from any other script " +
             // "(rig, animation, physics) to drive the axes live.")]
    public Transform axisSource;

    // The three body axes in world space, from whichever mode is active.
    // FromTransform reads the source as the patient's own frame, so patient-left
    // is -right: for any Transform, .right is the subject's RIGHT. Using +right
    // here would both invert laterality and make the L/P/S triple mirrored
    // (L x P would land on +S; DICOM LPS requires -S in Unity's left-handed
    // space) — no orientation of the source can compensate for that.
    Vector3 SuperiorAxis => Custom ? axisSource.up      : Vec(cephaladAxis);
    Vector3 AnteriorAxis => Custom ? axisSource.forward : Vec(anteriorAxis);
    Vector3 LeftAxis     => Custom ? -axisSource.right  : Vec(patientLeftAxis);
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

    // Cardinal direction word -> world vector. medial/lateral are handled in
    // ResolveDir (they depend on the point's side of the midline).
    bool TryDirection(string name, out Vector3 world)
    {
        switch ((name ?? "").ToLowerInvariant())
        {
            case "cephalad":  world =  SuperiorAxis; return true;
            case "caudal":    world = -SuperiorAxis; return true;
            case "anterior":  world =  AnteriorAxis; return true;
            case "posterior": world = -AnteriorAxis; return true;
            case "left":      world =  LeftAxis;     return true;
            case "right":     world = -LeftAxis;     return true;
            default:          world = Vector3.zero;  return false;
        }
    }

    // 1. CONTEXT for the LLM: movable objects and landmarks, each name + position.
    // Names are printed with spaces turned into underscores so each is a single
    // token; lookup normalizes them back, so "Left Shoulder" ⇄ "left_shoulder".
    //
    // Positions are reported in the PATIENT's frame (DICOM LPS, spec §7), never
    // in world space. This matters because the model reasons from these numbers
    // and its conclusions feed back as direction words. Sign ownership protects
    // the command path — the model names a direction, ResolveDir owns the sign —
    // but it cannot protect a direction the model *inferred* from a frame that
    // was described wrongly. Reporting in the anatomical frame also means the
    // numbers mean the same thing in every simulator regardless of how its
    // anatomy is mounted, and keeps working under FromTransform, where there is
    // no fixed world axis to name.
    public string BuildContext()
    {
        var sb = new StringBuilder();
        sb.Append("[MOVABLE OBJECTS] you can Move/Rotate these. mm, patient frame: x=left y=posterior z=superior\n");
        AppendMovables(sb);
        AppendToolVerbs(sb);
        sb.Append("[LANDMARKS] reference points only (MoveToward targets / Rotate pivots), not movable\n");
        AppendList(sb, landmarks);
        AppendLocalDirections(sb);
        return sb.ToString();
    }

    void AppendMovables(StringBuilder sb)
    {
        if (tools == null) return;
        bool first = true;   // the first listed tool is the primary (T1.3) — the default when none is named
        foreach (var b in tools)
        {
            if (b == null || b.tool == null) continue;
            Vector3 p = ToLps(b.tool.position);
            sb.AppendLine($"{Token(b.tool.name)}{(first ? " (primary)" : "")}: ({p.x:0}, {p.y:0}, {p.z:0})");
            first = false;
        }
    }

    // Advertise, per tool, the COMMAND verbs it actually supports. This is what
    // lets the model route by which tool OWNS a verb rather than by guessing a
    // tool from an overloaded word: "sagittal" belongs to whichever tool lists
    // View (the probe), "AP" to whichever lists Viewpoint (the camera). When the
    // user names no tool, the model falls back to the primary (first) — so the
    // tool is chosen first, then it reads the rest of the phrase in its own
    // vocabulary. Verbs are derived from the tool's own profile, so a new tool
    // advertises itself with no code change.
    void AppendToolVerbs(StringBuilder sb)
    {
        if (tools == null) return;
        var body = new StringBuilder();
        foreach (var b in tools)
        {
            if (b == null || b.tool == null) continue;
            var verbs = new List<string>();
            void Add(string v) { if (!verbs.Contains(v)) verbs.Add(v); }

            Add("Move"); Add("MoveToward"); Add("PointAt");   // generic — every tool

            // Every authored verb IS a command (T6.1) — the dispatcher falls through to
            // the tool's own tables — so advertise the tool's OWN words, on every axis.
            // The business-end axis is NOT special-cased to Compress/Lift any more: that
            // was ultrasound vocabulary printed onto every tool, so a camera was told it
            // could "Compress" while its authored "Zoom" was never mentioned at all.
            for (int a = 0; a < 3; a++)
            {
                var v = b.Translate(a);
                if (v != null && v.verbs != null && v.verbs.Length > 0) Add(Cap(v.verbs[0]));
            }
            for (int a = 0; a < 3; a++)
            {
                var v = b.Rotate(a);
                if (v != null && v.verbs != null && v.verbs.Length > 0) Add(Cap(v.verbs[0]));
            }
            if (b.clockfaceEnabled)     { Add("RotateToClock"); Add("Aim"); }
            if (b.imagingPlanesEnabled) Add("View");
            if (b.viewpointsEnabled)    Add("Viewpoint");

            body.AppendLine($"{Token(b.tool.name)}: {string.Join(", ", verbs)}");
        }
        if (body.Length > 0)
            sb.Append("[TOOL VERBS] each tool supports ONLY these commands — pick the tool whose verbs fit the request; if none is named, use the primary\n")
              .Append(body);
    }

    // First letter upper-cased, for display parity with the bracket command names.
    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    // Spec T9.5: tell the model each tool's OWN direction words, so it can use
    // "toward the notch" instead of only anatomical words. Without this the model
    // has no way to know a tool-local qualifier exists and refuses/asks. Only
    // verbs that actually declare tool-local qualifiers are listed; anatomical
    // words always work and are covered by the static prompt.
    void AppendLocalDirections(StringBuilder sb)
    {
        if (tools == null) return;
        var body = new StringBuilder();
        foreach (var b in tools)
        {
            if (b == null || b.tool == null) continue;
            var parts = new List<string>();
            for (int a = 0; a < 3; a++) AddVerbDirs(parts, b.Translate(a));
            for (int a = 0; a < 3; a++) AddVerbDirs(parts, b.Rotate(a));
            if (parts.Count > 0)
                body.AppendLine($"{Token(b.tool.name)}: {string.Join("; ", parts)}");
        }
        if (body.Length > 0)
            sb.Append("[LOCAL DIRECTIONS] some tools take their OWN direction words — use the exact token shown (anatomical words also work)\n")
              .Append(body);
    }

    // "verb=plus_token|minus_token" for one axis, or nothing if it declares no
    // tool-local qualifiers. The token is the qualifier as a single word so it
    // parses as one bracket argument (Token collapses spaces to underscores).
    static void AddVerbDirs(List<string> parts, AxisVerbs v)
    {
        if (v == null || v.verbs == null || v.verbs.Length == 0) return;
        bool hasP = v.plusQualifiers != null && v.plusQualifiers.Length > 0;
        bool hasM = v.minusQualifiers != null && v.minusQualifiers.Length > 0;
        if (!hasP && !hasM) return;
        var dirs = new List<string>();
        if (hasP) dirs.Add(Token(v.plusQualifiers[0]));
        if (hasM) dirs.Add(Token(v.minusQualifiers[0]));
        parts.Add($"{v.verbs[0]}={string.Join("|", dirs)}");
    }

    // World position -> the patient's LPS frame in mm, relative to `origin`.
    // x = patient-left, y = posterior, z = superior (cephalad) — DICOM LPS.
    Vector3 ToLps(Vector3 world)
    {
        Vector3 d = (world - (origin != null ? origin.position : Vector3.zero)) / unitsPerMm;
        return new Vector3(Vector3.Dot(d, LeftAxis),
                           Vector3.Dot(d, -AnteriorAxis),   // +y is POSTERIOR in LPS
                           Vector3.Dot(d, SuperiorAxis));
    }

    void AppendList(StringBuilder sb, Transform[] set)
    {
        if (set != null)
            foreach (var o in set)
            {
                if (o == null) continue;
                Vector3 p = ToLps(o.position);
                sb.AppendLine($"{Token(o.name)}: ({p.x:0}, {p.y:0}, {p.z:0})");
            }
    }

    // 2. MOVE. Direction word + positive mm; sign resolved in ResolveDir.
    // seconds > 0 animates; omit (0) for instant.
    public string Move(string objectName, string direction, float mm, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        if (!ResolveDir(direction, t.position, out var dir, out var msg)) return msg;
        ApplyTranslation(t, dir, mm, seconds);   // resolved in the anatomical frame
        return $"moved {objectName} {mm:0} mm {direction}" + DurSuffix(seconds);
    }

    // 3. ROTATE ABOUT AN ANATOMICAL AXIS. The direction word names the AXIS to
    // spin about, not a destination — reverse the spin by naming the opposite
    // direction (cephalad <-> caudal), never a minus sign. Degrees are positive.
    // Pivot is any movable/landmark, or the object's own centre.
    // seconds > 0 animates; omit (0) for instant.
    //
    // Named RotateAbout, not Rotate, for two reasons. The spec (§8.2) reserves
    // "Rotate" for a TOOL-LOCAL motion — spinning a probe about its own
    // longitudinal axis — which is a different operation this does not perform.
    // And "[Rotate probe cephalad]" reads as "rotate toward the head", which is
    // meaningless; the spec asks that anatomy words not be used as rotation axes
    // for exactly that reason. "RotateAbout" makes the axis role explicit.
    // Intended for imaging planes and similar non-tool objects; probe motions
    // (rock / tilt / rotate / slide / sweep / compress) are tool-local.
    public string RotateAbout(string objectName, string axis, float degrees, string pivotName = null, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        if (!TryDirection(axis, out var ax)) return $"unknown axis '{axis}'";
        Vector3 pivot = string.IsNullOrEmpty(pivotName) ? t.position : (FindAny(pivotName)?.position ?? t.position);
        ApplyRotation(t, pivot, ax, degrees, seconds);   // about a named anatomical axis
        return $"rotated {objectName} {degrees:0} degrees about the {axis} axis"
             + (string.IsNullOrEmpty(pivotName) ? "" : $" around {pivotName}")
             + DurSuffix(seconds);
    }

    // 4. MOVE TOWARD a named target (movable or landmark) by mm along the line to
    // it. mm omitted (<=0) goes all the way (lands on it). Clamped, no overshoot.
    public string MoveToward(string objectName, string targetName, float mm = 0f, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        var target = FindAny(targetName);
        if (target == null) return $"no object named '{targetName}'";
        Vector3 to = target.position - t.position;
        float dist = to.magnitude;
        if (dist < 1e-6f) return $"{objectName} is already at {targetName}";
        float distMm = dist / unitsPerMm;
        float moveMm = (mm <= 0f) ? distMm : Mathf.Min(mm, distMm);   // clamp in mm: no overshoot
        ApplyTranslation(t, to / dist, moveMm, seconds);             // direction = toward the target
        return $"moved {objectName} {moveMm:0} mm toward {targetName}";
    }

    // ---- §7.2 / §8.1: the tool's own frame -------------------------------
    // Read live from the object's transform, per Appendix A.2 (verified against
    // the probe asset): +X along the footprint's long axis toward the notch,
    // +Y along the short axis, +Z longitudinal toward the handle — so -Z points
    // into tissue. Any movable can be driven this way; there is no "is a tool"
    // flag, because the object's own axes are the only thing these read.
    static Vector3 LongAxis(Transform t)         => t.right;     // A.2 +X, toward the notch
    static Vector3 ShortAxis(Transform t)        => t.up;        // A.2 +Y
    static Vector3 LongitudinalAxis(Transform t) => t.forward;   // A.2 +Z, toward the HANDLE; -Z is the BEAM (into tissue)

    // Where a tool pivots against tissue. Rock/Tilt use this, not t.position:
    // a probe leans about its footprint, and on the CSSALT asset the mesh origin
    // sits ~39 mm above the face, which would swing the footprint through an arc.
    // T3.1: the tool's own pivot, read live (T3.3) so external code may move it —
    // a needle can pivot about an entry point created on puncture without any of
    // this knowing. Empty pivot = the tool's own origin (T3.2).
    Vector3 PivotFor(Transform t)
    {
        var b = FindBinding(t);
        return b != null && b.pivot != null ? b.pivot.position : t.position;
    }

    // The axis a twist turns about (T8.2), from the tool's business end.
    Vector3 TwistAxisFor(Transform t)
    {
        var b = FindBinding(t);
        return b != null ? b.TwistAxis : t.forward;
    }

    // Spoken-summary duration tail (e.g. " over 2s"). Commented out for now — a
    // sonographer doesn't say "over 1 second". To restore it everywhere, swap the
    // body for the commented line below (one edit; every command summary picks it up).
    static string DurSuffix(float seconds) => "";
    // static string DurSuffix(float seconds) => seconds > 0f ? $" over {seconds:0.#}s" : "";

    // ── Generic motion primitives (tool-agnostic) ───────────────────────────────
    // Every motion verb is the same two-step shape: RESOLVE a direction/axis in
    // some coordinate system, then CALL one of these. Move resolves its direction
    // in the anatomical frame; Slide/Sweep/Compress resolve theirs in the tool's
    // OWN frame; Rock/Tilt/Rotate/Clock/Aim resolve a rotation axis + pivot. The
    // primitives don't know or care which frame produced the vector — they are
    // pure geometry. This is the seam a non-probe tool reuses: same primitives,
    // a different resolver. `unit` is expected already-signed and unit-length, so
    // "never a minus sign" stays a property of the resolver, not the primitive.
    // ── Capture → replay (spec T10) ─────────────────────────────────────────────
    // A turn is BeginCapture() … commands … EndCaptureAndReplay(). While capturing,
    // every motion is applied INSTANTLY and recorded, so each command resolves
    // against the previous one's REAL result (fixing the "twist computed against
    // the un-tilted pose" bug) and the tissue cap / summary see the true
    // intermediate pose. After the turn, each moved object is rewound to where it
    // started and its steps are replayed as ONE smooth sequence — so a multi-
    // command turn can't clobber itself, and a single command glides as before.
    // Outside a captured turn (self tests calling a verb directly), motion applies
    // or glides immediately, exactly as it used to.
    struct MotionStep { public Transform t; public Vector3 pivot, axis, delta; public float degrees, seconds; }
    bool _capturing;
    readonly Dictionary<Transform, List<MotionStep>> _captured = new();
    readonly Dictionary<Transform, (Vector3 pos, Quaternion rot)> _startPose = new();

    /// <summary>Begin a turn: subsequent motions are applied instantly and recorded (T10).</summary>
    public void BeginCapture()
    {
        _capturing = true;
        _captured.Clear();
        _startPose.Clear();
    }

    /// <summary>End a turn: rewind each moved object and replay its steps as one smooth sequence (T10.2/T10.4).</summary>
    public void EndCaptureAndReplay()
    {
        _capturing = false;
        if (Application.isPlaying)
            foreach (var kv in _captured)
            {
                var t = kv.Key; var steps = kv.Value;
                bool glide = false; foreach (var s in steps) if (s.seconds > 0f) { glide = true; break; }
                if (!glide) continue;                          // all instant — already at the final pose
                var start = _startPose[t];
                t.position = start.pos; t.rotation = start.rot;   // rewind; the replay animates from here
                Animate(t, Replay(steps));
            }
        // Not playing (self test): the instant application already landed the final pose; leave it.
        _captured.Clear(); _startPose.Clear();
    }

    // The one primitive all motion goes through: rotate `degrees` about (pivot,
    // axis) AND translate by `delta`, over `seconds`. Either component may be zero.
    void ApplyMotion(Transform t, Vector3 pivot, Vector3 axis, float degrees, Vector3 delta, float seconds)
    {
        if (_capturing)
        {
            if (!_captured.TryGetValue(t, out var list))
            {
                list = new List<MotionStep>(); _captured[t] = list;
                _startPose[t] = (t.position, t.rotation);      // the turn-start pose for this object
            }
            list.Add(new MotionStep { t = t, pivot = pivot, axis = axis, degrees = degrees, delta = delta, seconds = seconds });
            StepInstant(t, pivot, axis, degrees, delta);       // apply for real so the next command sees it
            return;
        }
        if (seconds > 0f) Animate(t, Replay(new List<MotionStep> { new MotionStep { t = t, pivot = pivot, axis = axis, degrees = degrees, delta = delta, seconds = seconds } }));
        else StepInstant(t, pivot, axis, degrees, delta);
    }

    static void StepInstant(Transform t, Vector3 pivot, Vector3 axis, float degrees, Vector3 delta)
    {
        if (degrees != 0f) t.RotateAround(pivot, axis, degrees);
        if (delta != Vector3.zero) t.position += delta;
    }

    // Glide one step over `seconds`: rotation about its pivot plus its translation,
    // together. In a sequence the earlier steps' replay lands the tool at this step's
    // captured pivot first, so the captured pivot is the right starting pivot.
    //
    // The pivot TRAVELS with the translation. It has to: a step that both turns and
    // moves would otherwise keep rotating about a point the tool has already walked
    // away from, so each increment adds a little unwanted orbit and the tool drifts
    // off its intended endpoint — the more so the larger the turn and the travel.
    // (StepInstant turns about the pivot and then translates; carrying the pivot
    // along makes the glide agree with that, because a translation and a rotation
    // about a pivot rigidly attached to the body commute.) Rotation-only steps are
    // unaffected either way, which is why this only surfaced once one command both
    // turned and travelled.
    IEnumerator StepOver(MotionStep s)
    {
        float doneDeg = 0f; Vector3 doneMove = Vector3.zero;
        Vector3 pivot = s.pivot;
        for (float e = 0f; e < s.seconds; e += Time.deltaTime)
        {
            float f = Time.deltaTime / s.seconds;
            float dDeg = s.degrees * f; Vector3 dMove = s.delta * f;
            if (s.degrees != 0f) s.t.RotateAround(pivot, s.axis, dDeg);
            if (s.delta != Vector3.zero) { s.t.position += dMove; pivot += dMove; }
            doneDeg += dDeg; doneMove += dMove;
            yield return null;
        }
        if (s.degrees != 0f) s.t.RotateAround(pivot, s.axis, s.degrees - doneDeg);
        if (s.delta != Vector3.zero) s.t.position += s.delta - doneMove;
    }

    // Play captured steps in order — each glided (or snapped) over its own
    // duration. One coroutine per object, so a turn cannot clobber itself (T10.1).
    IEnumerator Replay(List<MotionStep> steps)
    {
        foreach (var s in steps)
        {
            if (s.seconds > 0f) { var inner = StepOver(s); while (inner.MoveNext()) yield return inner.Current; }
            else StepInstant(s.t, s.pivot, s.axis, s.degrees, s.delta);
        }
    }

    void ApplyTranslation(Transform t, Vector3 unit, float mm, float seconds)
        => ApplyMotion(t, Vector3.zero, Vector3.zero, 0f, unit * (mm * unitsPerMm), seconds);

    void ApplyRotation(Transform t, Vector3 pivot, Vector3 axis, float degrees, float seconds)
        => ApplyMotion(t, pivot, axis, degrees, Vector3.zero, seconds);

    // ── Tool profile lookup and verb resolution (spec T5, T6) ──────────────────

    /// <summary>The profile declared for a tool, or null if it has none (T6.5: still anatomically movable).</summary>
    public ToolBinding FindBinding(Transform t)
    {
        if (tools == null || t == null) return null;
        foreach (var b in tools) if (b != null && b.tool == t) return b;
        return null;
    }

    static bool MatchesAny(string[] set, string normalized)
    {
        if (set == null) return false;
        foreach (var s in set) if (Norm(s) == normalized) return true;
        return false;
    }

    // T5.2 — a VERB selects the axis; a QUALIFIER selects the sign. Returns the
    // signed world-space axis to travel along (translation) or turn about
    // (rotation), so the caller always applies a POSITIVE magnitude to it and
    // never writes a sign of its own (T0.4).
    //
    // Two kinds of qualifier are accepted (T5.4): a TOOL-LOCAL word declared in
    // the profile ("toward the notch") names the sign outright, and an
    // ANATOMICAL word is resolved in the patient frame and then compared against
    // this axis to pick the sign that best goes that way.
    public bool TryResolveToolVerb(ToolBinding b, string verb, string qualifier, bool rotation,
                                   out Vector3 unit, out int axisIndex, out string msg)
    {
        unit = Vector3.zero; axisIndex = -1; msg = null;
        if (b == null || b.tool == null) { msg = "that object has no tool profile"; return false; }

        // 1. Which axis does the verb name? A verb this tool does not declare is
        //    refused by name rather than silently ignored (T6.4, T0.3).
        string v = Norm(verb);
        for (int a = 0; a < 3 && axisIndex < 0; a++)
        {
            var table = rotation ? b.Rotate(a) : b.Translate(a);
            if (table != null && MatchesAny(table.verbs, v)) axisIndex = a;
        }
        if (axisIndex < 0)
        {
            msg = $"'{verb}' is not a {(rotation ? "rotation" : "movement")} {Token(b.tool.name)} can do";
            return false;
        }

        var declared = rotation ? b.Rotate(axisIndex) : b.Translate(axisIndex);
        Vector3 axis = ToolBinding.AxisVector(b.tool, axisIndex);

        // 2. Which way along it? A verb with no qualifier is ambiguous and is
        //    refused, never defaulted to a side (T5.3, T0.2).
        if (string.IsNullOrWhiteSpace(qualifier))
        {
            msg = $"'{verb}' needs a direction — say which way";
            return false;
        }
        string q = Norm(qualifier);

        // 2a. A tool-local qualifier names the sign outright.
        if (MatchesAny(declared.plusQualifiers, q))  { unit =  axis; return true; }
        if (MatchesAny(declared.minusQualifiers, q)) { unit = -axis; return true; }

        // 2b. Otherwise it must be an anatomical word.
        if (!ResolveDir(qualifier, b.tool.position, out var anat, out msg)) return false;

        // A rotation cannot aim anything along its own axis of rotation.
        if (rotation && Mathf.Abs(Vector3.Dot(anat.normalized, axis)) > 0.999f)
        {
            msg = $"can't {verb} {Token(b.tool.name)} {qualifier}: that direction runs along the axis it {verb}s about";
            return false;
        }

        // For a ROTATION the qualifier describes where the BUSINESS END travels
        // (T5.7), so the correct sense is the one whose turn carries that end
        // toward the named direction; a far-end tool inverts it (T5.8). For a
        // TRANSLATION the axis simply has to point that way.
        Vector3 reference = b.rotationQualifiersDescribe == QualifierReference.FarEnd
            ? -b.BusinessEndVector : b.BusinessEndVector;
        float sense = rotation ? Vector3.Dot(Vector3.Cross(axis, reference), anat)
                               : Vector3.Dot(axis, anat);

        if (Mathf.Abs(sense) < 1e-3f)
        {
            msg = rotation
                ? $"'{qualifier}' lies along the axis {verb} turns about, so it can't {verb} that way"
                : $"that axis is perpendicular to '{qualifier}' — the tool can't travel {qualifier} along it";
            return false;
        }
        unit = sense > 0f ? axis : -axis;
        return true;
    }

    // A tool translation: resolve the verb+direction in the tool's frame (T5), then
    // travel. The verb must be authored (T6.4) or it is refused — an unconfigured
    // tool has no local motions, only anatomical Move.
    string ToolTranslate(string objectName, string commandVerb, string verb, string direction, float mm, float seconds)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        if (!TryResolveToolVerb(FindBinding(t), commandVerb, direction, false, out var unit, out _, out var msg)) return msg;
        ApplyTranslation(t, unit, mm, seconds);
        return $"{verb} {objectName} {mm:0} mm {direction}" + DurSuffix(seconds);
    }

    // ── Generic tool verbs (T6.1) ───────────────────────────────────────────────
    // The fixed commands below (Slide, Sweep, Rock, Tilt …) each pass a HARDCODED
    // lookup word, so a tool could only reach them by authoring that exact word.
    // That made "a tool's vocabulary is DATA" only half true: a camera could author
    // "zoom" or "yaw" and nothing would ever ask for them. These two entry points
    // close that — any verb a tool declares is a command it can be given, with no
    // code per tool and no per-tool case in the dispatcher.

    /// <summary>
    /// Which motion kind a tool declares <paramref name="verb"/> on, if any (T6.1).
    /// Lets a caller tell an UNDECLARED verb ("unknown command") apart from a declared
    /// one that refuses, and decide whether the command's number is millimetres or degrees.
    /// </summary>
    public bool TryToolVerbKind(string objectName, string verb, out bool rotation)
    {
        rotation = false;
        var b = FindBinding(FindMovable(objectName));
        if (b == null) return false;
        string v = Norm(verb);
        for (int a = 0; a < 3; a++) { var x = b.Translate(a); if (x != null && MatchesAny(x.verbs, v)) return true; }
        for (int a = 0; a < 3; a++) { var x = b.Rotate(a);    if (x != null && MatchesAny(x.verbs, v)) { rotation = true; return true; } }
        return false;
    }

    /// <summary>
    /// GENERIC TOOL VERB (T6.1) — run any verb the tool declares in its own profile.
    /// The table the verb sits in decides everything: which axis it moves along or turns
    /// about, and whether it is a translation (mm) or a rotation (degrees). Rotations go
    /// through the same path as Rock/Tilt, so a host subscriber still caps them (T7.3).
    /// </summary>
    public string ToolCommand(string objectName, string verb, string qualifier, float amount, float seconds = 0f)
    {
        if (!TryToolVerbKind(objectName, verb, out bool rotation))
        {
            var t = FindMovable(objectName);
            return t == null ? NotMovable(objectName)
                             : $"'{verb}' is not a movement {Token(t.name)} can do";
        }
        // No authored past tense yet, so the verb is echoed as given.
        return rotation ? ToolAngle(objectName, verb, verb, qualifier, amount, seconds)
                        : ToolTranslate(objectName, verb, verb, qualifier, amount, seconds);
    }

    /// <summary>SLIDE — translate along whichever axis the tool authors "slide" on; the direction word picks the sign.</summary>
    public string Slide(string objectName, string direction, float mm, float seconds = 0f)
        => ToolTranslate(objectName, "slide", "slid", direction, mm, seconds);

    /// <summary>SWEEP — translate along whichever axis the tool authors "sweep" on.</summary>
    public string Sweep(string objectName, string direction, float mm, float seconds = 0f)
        => ToolTranslate(objectName, "sweep", "swept", direction, mm, seconds);

    // COMPRESS/LIFT — translate along the tool's BUSINESS END axis (T2.4): into
    // tissue (Compress) or off it (Lift). Gated like any tool verb — a tool that
    // has not authored compression (its business-end axis declares no verbs) does
    // nothing, because "compress" is a tool-specific motion, not anatomy.
    string Longitudinal(string objectName, float mm, float seconds, bool intoTissue)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        var b = FindBinding(t);
        var beTable = b != null ? b.Translate((int)b.businessEnd / 2) : null;
        if (beTable == null || beTable.verbs == null || beTable.verbs.Length == 0)
            return $"'{(intoTissue ? "compress" : "lift")}' is not a movement {Token(t.name)} can do";
        Vector3 into = b.BusinessEndVector;
        ApplyTranslation(t, intoTissue ? into : -into, mm, seconds);
        return $"{(intoTissue ? "compressed" : "lifted")} {objectName} {mm:0} mm {(intoTissue ? "into" : "off")} the tissue"
             + DurSuffix(seconds);
    }

    // ── Host subscriptions (spec T7) ───────────────────────────────────────────
    // A tool may declare SEVERAL subscribers — a probe wants a tissue-pressure cap
    // and may also want a travel range or a collision limit, and one slot forced a
    // choice between them.
    //
    // Composition is defined by T7.4: a subscriber may only ever REDUCE. So the
    // answer is simply the MINIMUM of what they all allow, which makes the outcome
    // independent of the order they happen to be listed in — there is no "first
    // one wins" to reason about, and adding one can never loosen a limit.

    /// <summary>How far this motion may go once every subscriber has had its say (T7.3/T7.4). Returns the requested magnitude when there are none.</summary>
    float AskSubscribers(ToolBinding b, ToolMotion motion, out string reason)
    {
        reason = null;
        float allowed = motion.magnitude;
        if (b == null || b.subscribers == null) return allowed;
        foreach (var m in b.subscribers)
        {
            var s = m as IToolMotionSubscriber;   // skips empty slots and wrong types (OnValidate warns)
            if (s == null) continue;
            float got = Mathf.Clamp(s.BeforeMotion(motion, out string why), 0f, motion.magnitude);
            // Keep the reason from whichever subscriber actually BINDS the motion, so
            // the operator hears the real cause rather than the first opinion offered.
            if (got < allowed) { allowed = got; reason = why; }
        }
        return allowed;
    }

    /// <summary>Notes from every subscriber on the settled motion, joined (T7.6). Null when there is nothing to say.</summary>
    string NotifySubscribers(ToolBinding b, ToolMotion motion)
    {
        if (b == null || b.subscribers == null) return null;
        string all = null;
        foreach (var m in b.subscribers)
        {
            var s = m as IToolMotionSubscriber;
            if (s == null) continue;
            string note = s.AfterMotion(motion);
            if (string.IsNullOrWhiteSpace(note)) continue;
            all = string.IsNullOrWhiteSpace(all) ? note.Trim() : all + " " + note.Trim();
        }
        return all;
    }

    // ROCK / TILT — angle the tool by rotating about whichever axis it authors the
    // verb on, pivoting at its pivot. The direction QUALIFIER picks the rotational
    // sense — whichever carries the tool's business end (or far end, per the tool's
    // `rotationQualifiersDescribe`) toward that direction — all resolved in
    // TryResolveToolVerb, which also refuses a direction that lies along the axis
    // of rotation (the rotation analogue of a translation's perpendicular refusal).
    //
    // CONVENTION (per tool): a rock/tilt qualifier points EITHER the business end
    // or the far end, and the two are OPPOSITE (pivoting at the contact point,
    // tipping the beam toward the head leans the handle toward the feet). Both are
    // taught clinically; `rotationQualifiersDescribe` selects, defaulting to the
    // FAR END (handle). Pinned by the self test.
    string ToolAngle(string objectName, string commandVerb, string verb, string direction, float degrees, float seconds)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);

        // `ax` comes back SIGNED: a positive turn about it carries the business end
        // toward `direction` (or the far end, per the tool's convention), so the
        // magnitude below is always positive (T0.4). The verb must be authored or
        // it is refused (T6.4).
        var b = FindBinding(t);
        if (!TryResolveToolVerb(b, commandVerb, direction, true, out var ax, out _, out var msg)) return msg;

        Vector3 pivot = PivotFor(t);

        // BEFORE (T7.3): the tool's subscribers may reduce the turn so the footprint
        // can't bury past the tissue limit, each supplying its own reason (T7.5). They
        // may only REDUCE (T7.4). No subscribers = no cap.
        var motion = new ToolMotion { kind = MotionKind.Rotate, tool = t, pivot = pivot, axis = ax.normalized, magnitude = degrees, seconds = seconds };
        float applied = AskSubscribers(b, motion, out string capReason);

        ApplyRotation(t, pivot, ax, applied, seconds);   // about a tool axis, pivoting at the contact point

        // AFTER (T7.6): the tool's subscribers may report on the settled motion.
        string afterNote = NotifySubscribers(b, new ToolMotion { kind = MotionKind.Rotate, tool = t, pivot = pivot, axis = ax.normalized, magnitude = applied, seconds = seconds });

        bool farEnd = b.rotationQualifiersDescribe == QualifierReference.FarEnd;
        string what = farEnd ? "handle" : "beam";
        string tail = (string.IsNullOrWhiteSpace(afterNote) ? "" : " " + afterNote.Trim()) + DurSuffix(seconds);
        float appliedMag = Mathf.Abs(applied);
        if (appliedMag < degrees - 0.5f)   // a host capped it short of the ask
        {
            // The subscriber's own reason if it gave one, else the legacy phrasing.
            string why = string.IsNullOrWhiteSpace(capReason) ? "the tissue limited how far it could go" : capReason.Trim();
            return $"{verb} {objectName} {appliedMag:0} degrees instead of {degrees:0} — {why}, {what} toward {direction}" + tail;
        }
        return $"{verb} {objectName} {degrees:0} degrees, {what} toward {direction}" + tail;
    }

    /// <summary>ROCK — angle the tool by rotating about whichever axis it authors "rock" on, pivoting at its pivot.</summary>
    public string Rock(string objectName, string direction, float degrees, float seconds = 0f)
        => ToolAngle(objectName, "rock", "rocked", direction, degrees, seconds);

    /// <summary>TILT — angle the tool by rotating about whichever axis it authors "tilt" on.</summary>
    public string Tilt(string objectName, string direction, float degrees, float seconds = 0f)
        => ToolAngle(objectName, "tilt", "tilted", direction, degrees, seconds);

    // §8.2 / §8.3 ROTATE — the twist about the longitudinal axis. "Clockwise" is
    // defined by the spec (A.4) as the operator's view from the handle end
    // looking toward tissue. Rather than derive that sense in Unity's
    // left-handed space by hand (the mistake behind the retracted D4 finding),
    // build the operator's viewpoint with Unity's own LookRotation and read
    // "3 o'clock" as the operator's right — then every clock angle is geometry,
    // and the spin uses SignedAngle + RotateAround, a self-consistent pair.
    //
    // Returns the signed angle (about L, for RotateAround) of a `cwDegrees`
    // CLOCKWISE turn as the handle-end operator sees it. Any up ⊥ L gives the
    // same sense; clockwise about L from the handle does not depend on cephalad.
    static float ClockwiseSigned(Vector3 L, float cwDegrees)
    {
        Vector3 up = Vector3.ProjectOnPlane(Vector3.up, L);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(Vector3.right, L);
        up.Normalize();
        Quaternion view = Quaternion.LookRotation(-L, up);   // at handle, looking toward tissue
        Vector3 vUp = view * Vector3.up, vRight = view * Vector3.right;   // 12 and 3 o'clock
        // Does a +1° turn about L carry 12 o'clock toward 3 o'clock (clockwise)?
        float unit = Vector3.Dot(Quaternion.AngleAxis(1f, L) * vUp, vRight) > 0f ? 1f : -1f;
        return unit * cwDegrees;
    }

    /// <summary>§8.2/§8.3 ROTATE — spin the tool about its longitudinal axis. sense = "clockwise"/"cw" or "counterclockwise"/"ccw", from the handle looking toward tissue.</summary>
    public string Rotate(string objectName, string sense, float degrees, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        string s = (sense ?? "").ToLowerInvariant();
        bool cw = s == "clockwise" || s == "cw";
        bool ccw = s == "counterclockwise" || s == "counter-clockwise" || s == "ccw" || s == "anticlockwise";
        if (!cw && !ccw) return $"rotate needs 'clockwise' or 'counterclockwise', got '{sense}'";

        Vector3 L = TwistAxisFor(t);   // T8.2: the roll axis, oriented for the operator's view (T8.7)
        float signed = ClockwiseSigned(L, cw ? degrees : -degrees);
        Vector3 pivot = PivotFor(t);
        ApplyRotation(t, pivot, L, signed, seconds);   // twist about the longitudinal axis
        return $"rotated {objectName} {degrees:0} degrees {(cw ? "clockwise" : "counter-clockwise")}"
             + DurSuffix(seconds);
    }

    /// <summary>§8.3 clockface — spin the tool about its longitudinal axis to put its marker at an hour position (noon = cephalad, each hour = 30°, clockwise from the handle).</summary>
    public string RotateToClock(string objectName, float hour, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        var bind = FindBinding(t);
        if (bind == null || !bind.clockfaceEnabled) return $"{Token(t.name)} has no orientation marker to set to a clock position";

        Vector3 L = TwistAxisFor(t);   // T8.2: the roll axis, oriented for the operator's view (T8.7)
        Vector3 noon = Vector3.ProjectOnPlane(SuperiorAxis, L);     // cephalad in the clock plane
        if (noon.sqrMagnitude < 1e-6f)
            return $"can't set a clock position for {objectName}: its beam runs along the head-foot axis, so noon (cephalad) is undefined";
        noon.Normalize();

        // Operator at the handle looking toward tissue; their right is 3 o'clock.
        Vector3 threeOClock = Quaternion.LookRotation(-L, noon) * Vector3.right;
        float theta = hour * 30f * Mathf.Deg2Rad;                   // clockwise from noon
        Vector3 targetMarker = Mathf.Cos(theta) * noon + Mathf.Sin(theta) * threeOClock;

        float delta = Vector3.SignedAngle(LongAxis(t), targetMarker, L);
        Vector3 pivot = PivotFor(t);
        ApplyRotation(t, pivot, L, delta, seconds);   // twist to put the marker at the hour

        int h = ((Mathf.RoundToInt(hour) % 12) + 12) % 12; if (h == 0) h = 12;
        return $"rotated {objectName} to {h} o'clock"
             + DurSuffix(seconds);
    }

    // §8.4 AIM THE MARKER — spin the tool about its longitudinal axis so the
    // marker (notch, +X long axis) points as near as it can toward an anatomical
    // direction ("aim the marker cephalad") or a named target ("...toward the
    // right shoulder"). The marker can only travel in the plane perpendicular to
    // the longitudinal axis, so the target is projected onto that plane; if the
    // target lies along the axis, the marker can't point there and it's refused.
    // Same longitudinal spin as Rotate/RotateToClock, pivoting at the contact
    // point (which sits on the axis, so the footprint stays put).
    public string Aim(string objectName, string target, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        var bind = FindBinding(t);
        if (bind == null || !bind.clockfaceEnabled) return $"{Token(t.name)} has no orientation marker to aim";
        Vector3 pivot = PivotFor(t);

        Vector3 want; string desc;
        if (ResolveDir(target, pivot, out want, out var msg)) desc = target;      // a direction word
        else if (msg != null && msg.Contains("ambiguous")) return msg;            // medial/lateral on the midline
        else
        {
            var tgt = FindAny(target);                                            // a landmark/movable target
            if (tgt == null) return $"no direction or object named '{target}'";
            want = tgt.position - pivot;
            desc = "the " + target;
        }

        Vector3 L = TwistAxisFor(t);   // T8.2: the roll axis, oriented for the operator's view (T8.7)
        Vector3 inPlane = Vector3.ProjectOnPlane(want, L);
        if (inPlane.sqrMagnitude < 1e-6f)
            return $"can't aim {objectName}'s marker toward {desc}: it lies along the probe's long axis, so the marker can't point there";
        inPlane.Normalize();

        float delta = Vector3.SignedAngle(LongAxis(t), inPlane, L);
        ApplyRotation(t, pivot, L, delta, seconds);   // twist to point the marker in-plane
        return $"aimed {objectName}'s marker toward {desc}"
             + DurSuffix(seconds);
    }

    // The minimal rotation carrying world unit `from` onto world unit `to`, as an
    // (angle, axis) pair for RotateAround. FromToRotation hands back an arbitrary
    // axis for the two degenerate cases — already aligned (0°/360°, no turn) and
    // an exact reversal (180°, any perpendicular axis will do) — so both are
    // guarded here rather than by each caller.
    static void RotateOnto(Vector3 from, Vector3 to, out float degrees, out Vector3 axis)
    {
        Quaternion q = Quaternion.FromToRotation(from.normalized, to.normalized);
        q.ToAngleAxis(out degrees, out axis);
        if (degrees < 1e-4f || degrees > 359.9999f) { degrees = 0f; axis = Vector3.up; return; }  // aligned: no-op
        if (axis.sqrMagnitude < 1e-8f || float.IsNaN(axis.x))                                      // ~180° reversal
        {
            axis = Vector3.ProjectOnPlane(Vector3.up, from);
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.ProjectOnPlane(Vector3.right, from);
            axis.Normalize();
            degrees = 180f;
        }
    }

    // POINT AT — a GENERIC look-at: rotate the tool so its BUSINESS END (a probe's
    // beam, a needle's tip, a camera's view) points at a target — an anatomical
    // direction ("point the camera posterior") or a named object ("point the
    // needle at the vessel"). Every tool has a business end (T2.4), so this needs
    // no authoring and works for any tool; there is nothing probe-specific here.
    //
    // Distinct from Aim, which twists the MARKER about the longitudinal axis
    // (in-plane, and only meaningful for a tool with a marker). PointAt reorients
    // the business-end axis itself. Pivots at the tool's pivot (T3): a camera
    // declares none and turns in place; a probe with a contact pivot turns about
    // the contact point.
    public string PointAt(string objectName, string target, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        var b = FindBinding(t);
        if (b == null) return $"{Token(t.name)} has no tool profile";
        Vector3 pivot = PivotFor(t);

        Vector3 want; string desc;
        if (ResolveDir(target, pivot, out want, out var msg)) desc = target;      // a direction word
        else if (msg != null && msg.Contains("ambiguous")) return msg;            // medial/lateral on the midline
        else
        {
            var tgt = FindAny(target);                                            // a landmark/movable target
            if (tgt == null) return $"no direction or object named '{target}'";
            want = tgt.position - pivot;
            desc = "the " + target;
        }
        if (want.sqrMagnitude < 1e-10f) return $"{objectName} is already at {desc}";

        RotateOnto(b.BusinessEndVector, want, out float degrees, out Vector3 axis);
        ApplyRotation(t, pivot, axis, degrees, seconds);   // swing the business end onto the target
        return $"pointed {objectName} at {desc}" + DurSuffix(seconds);
    }

    // §2 ANATOMICAL VIEWS — orient the probe so its imaging plane IS a named
    // anatomical plane. An ultrasound slice lies in the plane spanned by the beam
    // and the long axis; its normal is the SHORT axis (+Y). So a plane is fixed by
    // one fact: which anatomical axis the short axis lies along —
    //   sagittal   → patient left-right   (slice runs head-foot × front-back)
    //   coronal    → anterior-posterior   (slice runs head-foot × left-right)
    //   transverse → cephalad-caudal      (slice runs left-right × front-back)
    // We snap the short axis onto the NEAREST of ±that axis with the smallest
    // rotation, which (a) leaves the operator's rock and beam direction as-is —
    // rock is free within a plane — and (b) picks whichever marker end (clock 12
    // vs 6) is already closer, both being valid. Pivoting at the contact point
    // keeps the footprint on the skin; position (slide/sweep/compress) is left
    // alone. Defined entirely in the §7 anatomical frame — no skin normal, no
    // absolute-tilt reference needed.
    public string View(string objectName, string plane, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        var bind = FindBinding(t);
        if (bind == null || !bind.imagingPlanesEnabled)
            return $"{Token(t.name)} doesn't do imaging planes";   // a camera has viewpoints, not planes

        Vector3 anat; string canon; bool toMidline = false;
        switch ((plane ?? "").ToLowerInvariant())
        {
            case "sagittal":   anat = LeftAxis;     canon = "sagittal";   break;
            case "coronal":
            case "frontal":    anat = AnteriorAxis; canon = "coronal";    break;
            case "transverse":
            case "axial":      anat = SuperiorAxis; canon = "transverse"; break;
            // Mid-sagittal (median): a sagittal orientation AND positioned on the
            // midline. Unlike the orientation-only planes, this NAMES a specific
            // located plane, so it also slides the probe onto it (see below).
            case "midsagittal":
            case "mid-sagittal":
            case "median":     anat = LeftAxis;     canon = "mid-sagittal"; toMidline = true; break;
            default: return $"unknown plane '{plane}' — say sagittal, coronal, transverse, or mid-sagittal";
        }

        Vector3 shortAxis = ShortAxis(t);
        // Nearest of ±anat → the minimal snap. sign 0 (exactly orthogonal) picks +.
        Vector3 target = anat * (Vector3.Dot(shortAxis, anat) < 0f ? -1f : 1f);

        Quaternion q = Quaternion.FromToRotation(shortAxis, target);
        q.ToAngleAxis(out float degrees, out Vector3 axis);
        // FromToRotation returns an arbitrary axis for a 0° (already aligned) or a
        // near-180° flip; guard both so the tween gets a usable axis.
        if (degrees < 1e-4f || degrees > 359.9999f) { axis = LongitudinalAxis(t); if (degrees > 180f) degrees = 180f; }

        Vector3 pivot = PivotFor(t);

        // Mid-sagittal also SLIDES the footprint onto the median plane — the same
        // plane medial/lateral uses: the midline point, normal = LeftAxis. Only the
        // lateral offset is zeroed; cephalad position and depth are untouched. The
        // midline transform's rotation is irrelevant — only its position is read.
        Vector3 slide = Vector3.zero;
        if (toMidline)
        {
            if (midline == null)
                return $"can't set {objectName} to a mid-sagittal view: no midline is assigned to define the median plane";
            float off = Vector3.Dot(pivot - midline.position, LeftAxis);   // signed lateral distance to the plane
            slide = -off * LeftAxis;
        }

        // One motion: rotate to the plane, and (mid-sagittal) slide onto the midline.
        ApplyMotion(t, pivot, axis, degrees, slide, seconds);

        return $"set {objectName} to a {canon} view"
             + DurSuffix(seconds);
    }

    // §3 ANATOMICAL VIEWPOINT — an optional OBSERVER capability (the camera). Place
    // the tool at a named viewpoint RELATIVE TO THE PATIENT and aim its business
    // end at the subject: "give me an AP view" sits the camera on the anterior side
    // looking posterior. A viewpoint is POSITION + LOOK-AT together — unlike the
    // probe's imaging-plane View (§2), which only reorients. Built from generic
    // pieces: move to (subject + fromDir·distance), then point the business end
    // back at the subject. Gated on `viewpointsEnabled`: a probe does imaging
    // planes, not viewpoints, so "AP" must never land on it.
    //
    // The SUBJECT (what the view frames and is measured from) defaults to `origin`
    // — the patient centre already assigned for coordinate reporting — but a
    // command may name a target to reframe on it: "an AP view of the liver".
    public string Viewpoint(string objectName, string from, string targetName = null, float seconds = 0f)
    {
        var t = FindMovable(objectName);
        if (t == null) return NotMovable(objectName);
        var b = FindBinding(t);
        if (b == null || !b.viewpointsEnabled)
            return $"{Token(t.name)} doesn't do anatomical viewpoints";

        // The tool's CURRENT position decides the side for a plane word (below), so
        // this is resolved before anything moves.
        if (!TryViewpointDir(from, t.position, out Vector3 fromDir, out string canon, out string why))
            return why ?? $"unknown viewpoint '{from}' — say anterior/AP, posterior/PA, left, right, "
                        + "superior, inferior, or a plane (sagittal/coronal/transverse)";

        // Default subject = origin (patient centre). A named target overrides it;
        // an unfound name is refused, not quietly defaulted to the centre.
        Vector3 subject; string subjectDesc = "";
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            var tgt = FindAny(targetName);
            if (tgt == null) return $"no object named '{targetName}' to frame";
            subject = tgt.position;
            subjectDesc = " of the " + targetName;
        }
        else subject = origin != null ? origin.position : Vector3.zero;

        float dist = (b.viewpointDistanceMm > 0f ? b.viewpointDistanceMm : 300f) * unitsPerMm;
        Vector3 lookDir = -fromDir;   // from the viewpoint back toward the subject

        // ONE rigid motion, pivoting at the SUBJECT so the tool ORBITS the patient
        // instead of flying through them. The turn aims the business end at the
        // subject; taken about the subject, that same turn also carries the tool
        // around to the requested side — when the tool was already looking at the
        // subject (true after any previous viewpoint) the aim turn and the orbit turn
        // are the SAME turn, so the whole move is an arc at constant radius plus a
        // radial nudge to the framing distance. Pivoting at the tool instead would
        // send it straight across: left-lateral to right-lateral is a 180 degree
        // reversal, and the straight line between those two points runs through the
        // patient. Whatever the arc leaves over is closed by `delta`, so the endpoint
        // is exact even from a pose that was not looking at the subject at all.
        RotateOnto(b.BusinessEndVector, lookDir, out float degrees, out Vector3 axis);
        Vector3 afterTurn = subject + Quaternion.AngleAxis(degrees, axis) * (t.position - subject);
        Vector3 delta = (subject + fromDir * dist) - afterTurn;
        ApplyMotion(t, subject, axis, degrees, delta, seconds);

        return $"moved {objectName} to a {canon} view{subjectDesc}" + DurSuffix(seconds);
    }

    // Map a viewpoint word — anatomical or radiographic — to the direction the
    // OBSERVER sits on, relative to the patient. AP (anteroposterior) = the camera
    // is anterior, looking posterior; PA the reverse. Colloquial words
    // (front/back/top/bottom) map too, since operators say them, but they are
    // pose-dependent shorthand — the model is still steered toward anatomical terms.
    //
    // PLANE words (sagittal/coronal/transverse) are also accepted, because a lateral
    // projection IS the sagittal view. They resolve differently: see TryPlaneSide.
    bool TryViewpointDir(string word, Vector3 atPoint, out Vector3 fromDir, out string canon, out string msg)
    {
        msg = null;
        switch (Norm(word))
        {
            case "ap": case "anteroposterior": case "anterior": case "front":
                fromDir =  AnteriorAxis; canon = "anterior (AP)";  return true;
            case "pa": case "posteroanterior": case "posterior": case "back":
                fromDir = -AnteriorAxis; canon = "posterior (PA)"; return true;
            case "left":
                fromDir =  LeftAxis;     canon = "left lateral";   return true;
            case "right":
                fromDir = -LeftAxis;     canon = "right lateral";  return true;
            case "superior": case "cephalad": case "top": case "head":
                fromDir =  SuperiorAxis; canon = "superior";       return true;
            case "inferior": case "caudal": case "bottom": case "foot": case "feet":
                fromDir = -SuperiorAxis; canon = "inferior";       return true;

            // A plane names an AXIS; a viewpoint needs a SIDE of that axis. Each
            // plane therefore admits two opposed viewpoints, resolved below.
            case "sagittal":
                return TryPlaneSide(atPoint, LeftAxis, "left lateral", "right lateral",
                                    "sagittal", "left or right", out fromDir, out canon, out msg);
            case "coronal": case "frontal":
                return TryPlaneSide(atPoint, AnteriorAxis, "anterior (AP)", "posterior (PA)",
                                    "coronal", "AP or PA", out fromDir, out canon, out msg);
            case "transverse": case "axial":
                return TryPlaneSide(atPoint, SuperiorAxis, "superior", "inferior",
                                    "transverse", "superior or inferior", out fromDir, out canon, out msg);

            default:
                fromDir = Vector3.zero;  canon = null;             return false;
        }
    }

    // Which side of a plane's axis should the observer sit on? Take the side the
    // tool is ALREADY on, measured from the midline — what a person does without
    // being asked ("the camera is left of the patient, so a sagittal view means the
    // left lateral one"). This is the same rule medial/lateral uses in ResolveDir,
    // including its deadzone: sitting on the plane itself is genuinely ambiguous, so
    // it ASKS rather than picking a side (T0.2). The midline transform is the
    // reference for all three axes — it is a point on the patient, so "in front of
    // it" and "above it" are as meaningful as "left of it".
    bool TryPlaneSide(Vector3 atPoint, Vector3 axis, string plusCanon, string minusCanon,
                      string plane, string choices, out Vector3 fromDir, out string canon, out string msg)
    {
        fromDir = Vector3.zero; canon = null; msg = null;
        if (midline == null)
        {
            msg = $"can't place a {plane} view: no midline is assigned to measure which side to view from";
            return false;
        }
        float side = Vector3.Dot(atPoint - midline.position, axis);
        if (Mathf.Abs(side) < midlineDeadzoneMm * unitsPerMm)
        {
            msg = $"'{plane}' is ambiguous from here — say {choices}";
            return false;
        }
        fromDir = side >= 0f ? axis      : -axis;
        canon   = side >= 0f ? plusCanon : minusCanon;
        return true;
    }

    /// <summary>§8.2 COMPRESS — press the tool into tissue along -Z (A.3).</summary>
    public string Compress(string objectName, float mm, float seconds = 0f)
        => Longitudinal(objectName, mm, seconds, intoTissue: true);

    /// <summary>§8.2 COMPRESS (reverse) — lift the tool off tissue along +Z (A.3).</summary>
    public string Lift(string objectName, float mm, float seconds = 0f)
        => Longitudinal(objectName, mm, seconds, intoTissue: false);

    // Resolve a direction word to a world vector. Cardinal words come from the
    // body axes; medial/lateral depend on which side of the midline the point is
    // on. Returns false (with a message) for an unknown word or an ambiguous
    // midline position — the caller relays the message.
    bool ResolveDir(string direction, Vector3 atPoint, out Vector3 world, out string msg)
    {
        msg = null;
        string d = (direction ?? "").ToLowerInvariant();
        if (d == "medial" || d == "lateral")
        {
            Vector3 patientLeft = LeftAxis;                           // midline normal = patient-left
            Vector3 origin = midline != null ? midline.position : Vector3.zero;
            float side = Vector3.Dot(atPoint - origin, patientLeft);  // >0 patient-left, <0 right
            if (Mathf.Abs(side) < midlineDeadzoneMm * unitsPerMm)
            {
                world = Vector3.zero;
                msg = $"'{direction}' is ambiguous on the midline — say left or right";
                return false;
            }
            Vector3 away = side >= 0f ? patientLeft : -patientLeft;
            world = (d == "lateral") ? away : -away;
            return true;
        }
        if (TryDirection(d, out world)) return true;
        msg = $"unknown direction '{direction}'";
        return false;
    }

    // --- name lookup: any separator (space, _, -, .) is ignored, case-insensitive.
    // Keeping only letters+digits means "Right-Shoulder", "right_shoulder" and
    // "right shoulder" all match the same object.
    static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
    // Display token for the LLM: collapse separators to a single underscore form.
    static string Token(string s) => s == null ? "" : s.Replace(' ', '_').Replace('-', '_');

    static Transform FindIn(Transform[] set, string name)
    {
        if (set == null) return null;
        string n = Norm(name);
        foreach (var o in set)
            if (o != null && Norm(o.name) == n) return o;
        return null;
    }
    // The PRIMARY tool (spec T1.3): the FIRST entry in `tools`. An explicit,
    // deterministic designation of a declared tool — not a name match (T1.2) —
    // and the "Element 0 is the default" convention authors already expect.
    Transform PrimaryTool => (tools != null && tools.Length > 0 && tools[0] != null) ? tools[0].tool : null;

    // A generic, tool-agnostic PRONOUN — "it", "the tool" — that names the PRIMARY
    // tool rather than any specific object. Deliberately NOT "probe": "probe" is
    // ultrasound-specific vocabulary, not a pronoun, and treating it as generic
    // sent it to whatever sat first in the list (e.g. a camera). A tool-specific
    // word must match a tool by name, never resolve to the primary by default.
    static bool IsGenericToolReference(string n)
        => n == "it" || n == "tool" || n == "thetool";

    // Every movable is a tool (T1.1). An exact name wins; an unmatched GENERIC
    // reference ("it", "the tool") resolves to the primary tool — the first entry
    // (T1.3).
    Transform FindMovable(string name)
    {
        string n = Norm(name);
        if (tools != null)
            foreach (var b in tools)
                if (b != null && b.tool != null && Norm(b.tool.name) == n) return b.tool;
        return IsGenericToolReference(n) ? PrimaryTool : null;
    }

    Transform FindAny(string name) => FindMovable(name) ?? FindIn(landmarks, name);

    // Helpful message: distinguish "not a thing" from "a landmark, can't move it".
    string NotMovable(string name) =>
        FindIn(landmarks, name) != null ? $"'{name}' is a landmark, not movable" : $"no object named '{name}'";

    // --- timed motion: native coroutines, no dependency ----------------------
    // One replay coroutine per object; a new turn cancels the object's in-flight
    // one. The tweening itself lives in StepOver/Replay (above).
    readonly Dictionary<Transform, Coroutine> _running = new();

    void Animate(Transform t, IEnumerator step)
    {
        if (_running.TryGetValue(t, out var c) && c != null) StopCoroutine(c);
        _running[t] = StartCoroutine(step);
    }

    // Scene-view differentiation: movable = green cube, landmark = blue sphere.
    void OnDrawGizmos()
    {
        if (gizmoRadius <= 0f) return;
        if (tools != null)
        {
            Gizmos.color = new Color(0.3f, 1f, 0.45f);
            foreach (var b in tools) if (b != null && b.tool != null) Gizmos.DrawWireCube(b.tool.position, Vector3.one * gizmoRadius);
        }
        if (landmarks != null)
        {
            Gizmos.color = new Color(0.35f, 0.7f, 1f);
            foreach (var o in landmarks) if (o != null) Gizmos.DrawWireSphere(o.position, gizmoRadius * 0.5f);
        }
    }

#if UNITY_EDITOR
    // Edit-time guard for the one field that can look wired and do nothing. A tool's
    // subscribers are typed MonoBehaviour so the Inspector will accept ANY component,
    // but they are consumed with `as IToolMotionSubscriber` — list something that
    // doesn't implement it and that entry is silently skipped, leaving the tool less
    // constrained than the Inspector suggests. That is this project's least favourite
    // shape (assigned, plausible, inert), so say so at edit time rather than letting
    // it pass as working. Warns only when actually wrong.
    void OnValidate()
    {
        if (tools == null) return;
        foreach (var b in tools)
        {
            if (b == null || b.subscribers == null) continue;
            string who = b.tool != null ? Token(b.tool.name) : "a tool with no transform assigned";
            for (int i = 0; i < b.subscribers.Length; i++)
            {
                var m = b.subscribers[i];
                if (m == null || m is IToolMotionSubscriber) continue;   // empty slot, or fine
                Debug.LogWarning(
                    $"[ContextCommander] {who}: Subscribers[{i}] is a {m.GetType().Name}, which does not "
                  + "implement IToolMotionSubscriber, so it is IGNORED — it will not limit or report on this "
                  + "tool's motion. Use a component that implements the interface, or clear the slot. "
                  + "(A pose constraint held every frame is not a subscriber; run that from LateUpdate.)", this);
            }
        }
    }

    // ponytail: the one runnable check. Right-click the component > Self test.
    [ContextMenu("Self test")]
    void SelfTest()
    {
        // Debug.Assert logs but does not halt, so a bare Assert lets the run
        // reach the "passed" log with failures behind it. Count them instead.
        int failed = 0;
        void Check(bool ok, string what)
        {
            if (ok) return;
            failed++;
            Debug.LogError($"[ContextCommander] self test check FAILED: {what}");
        }

        // T1: tool identity. The PRIMARY tool is the FIRST entry in `tools`
        // (T1.3) — an explicit, name-independent designation. It resolves generic
        // references ("it", "the tool"); exact names always win (T1.1). Every
        // movable is a tool; there is no separate list.
        {
            var arbitrary = new GameObject("Widget_7");   // a name with no "probe" in it
            var ghost = new GameObject("Ghost Probe");
            var scalpel = new GameObject("Scalpel_3");
            tools = new[] { new ToolBinding { tool = arbitrary.transform },   // first = primary
                            new ToolBinding { tool = ghost.transform },
                            new ToolBinding { tool = scalpel.transform } };

            Check(FindMovable("it") == arbitrary.transform, "'it' must resolve to the first tool (T1.3)");
            Check(FindMovable("the tool") == arbitrary.transform, "'the tool' must resolve to the primary tool");
            Check(FindMovable("probe") == null, "'probe' is NOT a generic reference — it must match a tool by name or nothing (not default to primary)");
            Check(FindMovable("Ghost_Probe") == ghost.transform, "an exact name still wins over the primary reference (T1.1)");
            Check(FindMovable("Widget_7") == arbitrary.transform, "the primary tool still answers to its own exact name");
            Check(FindMovable("Scalpel_3") == scalpel.transform, "a non-primary tool still resolves by its own name");
            Check(FindMovable("nonexistent") == null, "a name matching no tool resolves to nothing");

            DestroyImmediate(ghost); DestroyImmediate(arbitrary); DestroyImmediate(scalpel);
            tools = null;
        }

        // The standard probe-shaped tool, written as DATA (the ONE path now — a
        // movable is a tool). Built once and reused by every section below.
        var go = new GameObject("probe"); go.transform.position = Vector3.zero;
        var pivot = new GameObject("origin_pivot"); pivot.transform.position = Vector3.zero;
        var binding = new ToolBinding
        {
            tool = go.transform,
            businessEnd = SignedAxis.MinusZ,
            rotationQualifiersDescribe = QualifierReference.BusinessEnd,   // beam sense, for the §8.2 asserts
            clockfaceEnabled = true, markerAxis = SignedAxis.PlusX, noonDirection = "cephalad",
            imagingPlanesEnabled = true,   // a probe has imaging planes (View); a camera would not
            translateX = new AxisVerbs { verbs = new[] { "slide" },
                                         plusQualifiers = new[] { "toward notch" },
                                         minusQualifiers = new[] { "away from notch" } },
            translateY = new AxisVerbs { verbs = new[] { "sweep", "fan" } },
            translateZ = new AxisVerbs { verbs = new[] { "compression" },
                                         plusQualifiers = new[] { "pull out" },
                                         minusQualifiers = new[] { "push in" } },
            rotateX    = new AxisVerbs { verbs = new[] { "tilt" } },
            rotateY    = new AxisVerbs { verbs = new[] { "rock" } },
            rotateZ    = new AxisVerbs { verbs = new[] { "rotate", "roll" } },
        };
        tools = new[] { binding };
        landmarks = new[] { pivot.transform };

        Move("probe", "cephalad", 10f);
        Check(go.transform.position == Vector3.forward * (10f * unitsPerMm), "cephalad must move +Z");
        Move("probe", "right", 10f);
        Check(go.transform.position.x < 0f, "right must be -X");

        // Pins the rotation sign: +90 deg about an axis takes +X to +Y.
        // The pivot is required — omit it and Rotate pivots about the object's
        // own position, spinning it in place so the position cannot change.
        go.transform.position = Vector3.right;
        RotateAbout("probe", "cephalad", 90f, "origin_pivot");
        Check(Vector3.Distance(go.transform.position, Vector3.up) < 1e-3f, "90 deg about cephalad about origin: +X -> +Y");

        // Landmark whose name uses a hyphen — must still match the underscore
        // token the model emits, via normalization (any separator is ignored).
        var tgt = new GameObject("right-shoulder"); tgt.transform.position = Vector3.forward * 100f;
        go.transform.position = Vector3.zero;
        landmarks = new[] { pivot.transform, tgt.transform };
        MoveToward("probe", "right_shoulder", 10f);
        Check(Mathf.Abs(go.transform.position.z - 10f * unitsPerMm) < 1e-3f, "moveToward: hyphen/underscore name matched, 10mm along +Z");
        Check(Move("right_shoulder", "cephalad", 10f).Contains("landmark"), "landmarks are not movable");
        Check(RotateAbout("right_shoulder", "cephalad", 10f).Contains("landmark"), "landmarks are not rotatable either");

        // Medial/lateral depend on the side of the midline (world x = 0 here).
        go.transform.position = new Vector3(50f, 0f, 0f);
        Move("probe", "lateral", 10f);
        Check(go.transform.position.x > 50f, "lateral on the +X side moves further +X");
        float xBefore = go.transform.position.x;
        Move("probe", "medial", 10f);
        Check(go.transform.position.x < xBefore, "medial moves back toward the midline");

        // The body axes must form a right-handed anatomical frame (DICOM LPS,
        // spec 7). Unity's basis is physically left-handed while Vector3.Cross
        // returns the algebraic right-handed result, so a conforming frame
        // satisfies L x P == -S. Testing for +S inverts the verdict.
        void CheckFrame(string mode)
        {
            Vector3 L = LeftAxis, P = -AnteriorAxis, S = SuperiorAxis;
            Check(Mathf.Abs(Vector3.Dot(L, P)) < 1e-4f
               && Mathf.Abs(Vector3.Dot(P, S)) < 1e-4f
               && Mathf.Abs(Vector3.Dot(L, S)) < 1e-4f, $"{mode}: body axes must be mutually orthogonal");
            Check(Vector3.Distance(Vector3.Cross(L, P), -S) < 1e-4f,
                  $"{mode}: body axes must be right-handed (LPS): expected L x P == -S, got L x P = {Vector3.Cross(L, P)}, -S = {-S}");
        }
        CheckFrame("WorldConstants");

        // Axis mapping is Inspector-driven: flipping patientLeftAxis flips "left".
        patientLeftAxis = WorldAxis.MinusX;
        go.transform.position = Vector3.zero;
        Move("probe", "left", 10f);
        Check(go.transform.position.x < 0f, "patientLeftAxis=-X: 'left' now moves -X");
        patientLeftAxis = WorldAxis.PlusX;

        // ---- §8.2 tool-local translation ---------------------------------
        // Sweep travels along the tool's SHORT axis; the direction word only
        // picks the sign. The distinguishing case is a canted tool: a free
        // anatomical Move would go straight cephalad, while a Sweep follows the
        // tool's own axis. Config is back at the defaults here (cephalad=+Z).
        // The CSSALT probe's real resting pose, canted 45 deg about its own
        // longitudinal axis: short axis (0.71, 0, -0.71), oblique to cephalad.
        // Rotating about Y alone would leave `up` at (0,1,0) — perpendicular to
        // cephalad — and Sweep would (correctly) refuse, testing nothing.
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);
        Vector3 shortAxis = go.transform.up, longAxis = go.transform.right;

        Sweep("probe", "cephalad", 100f);
        Vector3 swept = go.transform.position;
        Check(Vector3.Dot(swept.normalized, shortAxis) > 0.999f || Vector3.Dot(swept.normalized, -shortAxis) > 0.999f,
              $"sweep must travel along the tool's SHORT axis, not the body axis. went {swept.normalized}, short axis {shortAxis}");
        Check(Vector3.Dot(swept, SuperiorAxis) > 0f,
              $"sweep cephalad must end up further cephalad, went {swept}");
        Check(Mathf.Abs(swept.magnitude - 100f * unitsPerMm) < 1e-2f,
              $"sweep distance is measured along the TOOL axis: expected 100 mm of travel, got {swept.magnitude:0.#}");

        go.transform.position = Vector3.zero;
        string slideMsg = Slide("probe", "cephalad", 100f);
        Check(Vector3.Dot(go.transform.position.normalized, longAxis) > 0.999f
           || Vector3.Dot(go.transform.position.normalized, -longAxis) > 0.999f,
              "slide must travel along the tool's LONG axis");
        // Readback verb must match the motion — Slide reads "slid", not "swept".
        Check(slideMsg.StartsWith("slid"), $"slide readback must say 'slid', got \"{slideMsg}\"");
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);
        Check(Sweep("probe", "cephalad", 10f).StartsWith("swept"), "sweep readback must say 'swept'");

        // A direction perpendicular to the axis is undecidable, not a coin flip.
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;   // short axis = +Y = anterior
        Check(Sweep("probe", "cephalad", 100f).Contains("perpendicular"),
              "sweep must refuse a direction perpendicular to the tool axis rather than pick a side");
        Check(go.transform.position == Vector3.zero, "a refused sweep must not move anything");

        // ---- §8.2 rock / tilt: angle the beam toward a direction, pivoting at
        // the contact point ----
        // Canted pose so the beam is oblique to every body axis (an axis-aligned
        // probe puts the beam anti-parallel to cephalad, a degenerate case).
        go.transform.position = new Vector3(0f, 50f, 0f);
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);
        var contact = new GameObject("contact_pt").transform;
        contact.SetParent(go.transform);
        contact.position = go.transform.position - go.transform.forward * 20f;  // on the tissue side
        binding.pivot = contact;

        Vector3 contactBefore = contact.position;
        Vector3 beamBefore = -go.transform.forward, handleBefore = go.transform.forward;
        string rockMsg = Rock("probe", "cephalad", 15f);
        Vector3 beamAfterRock = -go.transform.forward;
        // Contact point is the pivot, so it must not move — the body swings, the
        // footprint stays on the skin.
        Check(Vector3.Distance(contact.position, contactBefore) < 1e-2f,
              $"rock must pivot about the contact point (it must not move); moved {Vector3.Distance(contact.position, contactBefore):0.###}");
        // Business-end convention (binding set to BusinessEnd above): rock cephalad
        // tips the BEAM toward the head, and the handle the opposite way. The
        // FarEnd check further down pins the inversion. Both are valid conventions.
        Check(Vector3.Dot(beamAfterRock, SuperiorAxis) > Vector3.Dot(beamBefore, SuperiorAxis) + 1e-4f,
              $"rock cephalad must tip the BEAM further toward the head: {Vector3.Dot(beamBefore, SuperiorAxis):0.###} -> {Vector3.Dot(beamAfterRock, SuperiorAxis):0.###}");
        Check(Vector3.Dot(go.transform.forward, SuperiorAxis) < Vector3.Dot(handleBefore, SuperiorAxis) - 1e-4f,
              "and the HANDLE must lean the opposite way — beam and handle are inverted (§8.5)");
        Check(rockMsg.StartsWith("rocked"), $"rock readback verb, got \"{rockMsg}\"");

        // Rock rotates about the short axis; a direction ALONG the short axis is
        // unreachable and must be refused, not guessed.
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);
        // Point the short axis at cephalad by construction: rotate so up = +Z.
        go.transform.rotation = Quaternion.FromToRotation(go.transform.up, SuperiorAxis) * go.transform.rotation;
        Vector3 posBeforeRefuse = go.transform.position;
        string refuse = Rock("probe", "cephalad", 15f);
        Check(refuse.Contains("runs along"), $"rock must refuse a direction along its rotation axis, got \"{refuse}\"");

        // Tilt: about the long axis, beam fans the other way. Just confirm it
        // pivots at contact and tips the beam toward the requested direction.
        go.transform.position = new Vector3(0f, 50f, 0f);
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);
        contactBefore = contact.position;
        beamBefore = -go.transform.forward;
        string tiltMsg = Tilt("probe", "left", 15f);
        Check(Vector3.Distance(contact.position, contactBefore) < 1e-2f, "tilt must pivot about the contact point");
        Check(Vector3.Dot(-go.transform.forward, LeftAxis) > Vector3.Dot(beamBefore, LeftAxis) + 1e-4f,
              "tilt left must tip the beam further toward the patient's left");
        Check(tiltMsg.StartsWith("tilted"), $"tilt readback verb, got \"{tiltMsg}\"");

        // §8.5 toggle: in Handle mode the SAME word points the handle, so the beam
        // goes the opposite way — the sign is inverted vs Beam mode.
        go.transform.position = new Vector3(0f, 50f, 0f);
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);
        Vector3 beamBeforeHandle = -go.transform.forward;
        binding.rotationQualifiersDescribe = QualifierReference.FarEnd;   // handle convention
        Rock("probe", "cephalad", 15f);
        Check(Vector3.Dot(go.transform.forward, SuperiorAxis) > Vector3.Dot(-beamBeforeHandle, SuperiorAxis) + 1e-4f,
              "Handle mode: rock cephalad must tip the HANDLE toward the head (beam the other way)");
        Check(Vector3.Dot(-go.transform.forward, SuperiorAxis) < Vector3.Dot(beamBeforeHandle, SuperiorAxis) - 1e-4f,
              "Handle mode: the beam must go the OPPOSITE way to Beam mode — the convention flips the sign");
        binding.rotationQualifiersDescribe = QualifierReference.BusinessEnd;   // restore beam for later asserts

        binding.pivot = null;
        DestroyImmediate(contact.gameObject);
        go.transform.rotation = Quaternion.identity;

        // ---- §8.3 Rotate + clockface: twist about the longitudinal axis ----
        // Pose so the clock face is unambiguous AND the longitudinal axis is not
        // parallel to cephalad (which would make noon undefined — cephalad is +Z
        // here). Longitudinal = +Y (handle toward +Y, beam toward -Y); cephalad
        // (+Z) then lies in the clock plane. The operator stands at the handle
        // looking toward tissue; build that viewpoint with Unity's own LookRotation
        // so "clockwise" and "3 o'clock" are measured, not derived.
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward); // fwd(longitudinal)=+Y, up=+Z
        var operatorView = new GameObject("operator").transform;
        operatorView.rotation = Quaternion.LookRotation(-LongitudinalAxis(go.transform), SuperiorAxis);
        Vector3 opRight = operatorView.right, opUp = operatorView.up;

        // Noon: the marker points cephalad (up in the operator's view).
        RotateToClock("probe", 12f);
        Check(Vector3.Dot(LongAxis(go.transform), SuperiorAxis) > 0.99f,
              $"12 o'clock must aim the marker at noon (cephalad); dot={Vector3.Dot(LongAxis(go.transform), SuperiorAxis):0.###}");

        // 3 o'clock: marker to the operator's RIGHT — the independent check.
        RotateToClock("probe", 3f);
        Check(Vector3.Dot(LongAxis(go.transform), opRight) > 0.99f,
              $"3 o'clock must aim the marker to the operator's right; dot={Vector3.Dot(LongAxis(go.transform), opRight):0.###}");

        // 6 o'clock: caudad (down in the view).
        RotateToClock("probe", 6f);
        Check(Vector3.Dot(LongAxis(go.transform), SuperiorAxis) < -0.99f, "6 o'clock must aim the marker caudad");

        // 9 o'clock: operator's left.
        RotateToClock("probe", 9f);
        Check(Vector3.Dot(LongAxis(go.transform), opRight) < -0.99f, "9 o'clock must aim the marker to the operator's left");

        // Relative spin must agree with the clock: from noon, a 90° CLOCKWISE
        // turn lands the marker where 3 o'clock is (operator's right).
        RotateToClock("probe", 12f);
        Rotate("probe", "clockwise", 90f);
        Check(Vector3.Dot(LongAxis(go.transform), opRight) > 0.99f,
              "a 90° clockwise spin from noon must reach the 3 o'clock (right) position");
        // And counter-clockwise is the opposite sense.
        RotateToClock("probe", 12f);
        Rotate("probe", "counterclockwise", 90f);
        Check(Vector3.Dot(LongAxis(go.transform), opRight) < -0.99f,
              "a 90° counter-clockwise spin from noon must reach the 9 o'clock (left) position");

        // Beam along the head-foot axis → noon undefined → refuse.
        go.transform.rotation = Quaternion.LookRotation(SuperiorAxis, Vector3.right); // longitudinal = cephalad
        Check(RotateToClock("probe", 3f).Contains("undefined"),
              "clock must refuse when the beam runs along the head-foot axis");

        // ---- §8.4 aim the marker toward a direction or a landmark ----
        // Same pose (longitudinal = +Y, cephalad = +Z in the clock plane).
        go.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
        Aim("probe", "cephalad");
        Check(Vector3.Dot(LongAxis(go.transform), SuperiorAxis) > 0.99f,
              $"aim cephalad must point the marker at the head; dot={Vector3.Dot(LongAxis(go.transform), SuperiorAxis):0.###}");

        // Aim at a landmark: marker should point toward the landmark's projection
        // into the clock plane. Put one straight caudad so the target is clear.
        var aimTgt = new GameObject("aim_target").transform;
        aimTgt.position = go.transform.position - SuperiorAxis * 100f;   // caudad of the probe
        landmarks = new[] { pivot.transform, aimTgt };
        Aim("probe", "aim_target");
        Check(Vector3.Dot(LongAxis(go.transform), SuperiorAxis) < -0.99f,
              "aim at a caudad landmark must point the marker caudad");
        DestroyImmediate(aimTgt.gameObject);
        landmarks = new[] { pivot.transform };

        // A target along the longitudinal axis is unreachable — refuse.
        go.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward); // longitudinal = +Y = anterior
        Check(Aim("probe", "anterior").Contains("along the probe's long axis"),
              "aim must refuse a target that lies along the longitudinal axis");

        DestroyImmediate(operatorView.gameObject);
        go.transform.rotation = Quaternion.identity;

        // ---- §2 anatomical views: align the imaging-plane normal (short axis) ----
        // Default config here: LeftAxis=+X, AnteriorAxis=+Y, SuperiorAxis=+Z.
        // A view is defined by which anatomical axis the SHORT axis lands on.
        binding.pivot = null;                      // pivot = own centre → position must not move
        go.transform.position = new Vector3(7f, 8f, 9f);
        go.transform.rotation = Quaternion.identity;    // short axis (up) = +Y, not on any plane axis
        Vector3 posBefore = go.transform.position;
        View("probe", "sagittal");
        Check(Mathf.Abs(Vector3.Dot(ShortAxis(go.transform), LeftAxis)) > 0.999f,
              $"sagittal must put the short axis on the left-right axis; dot={Vector3.Dot(ShortAxis(go.transform), LeftAxis):0.###}");
        Check((go.transform.position - posBefore).sqrMagnitude < 1e-6f,
              "a view must not move the probe — it only reorients");

        // Minimal snap: rock stays free. Rock the sagittal probe, re-issue sagittal;
        // it must STILL be sagittal (rock is in-plane) and barely move.
        Rock("probe", "cephalad", 20f);
        Quaternion rocked = go.transform.rotation;
        View("probe", "sagittal");
        Check(Mathf.Abs(Vector3.Dot(ShortAxis(go.transform), LeftAxis)) > 0.999f,
              "re-issuing sagittal after a rock must remain sagittal");
        Check(Quaternion.Angle(rocked, go.transform.rotation) < 0.01f,
              "sagittal on an already-sagittal (rocked) probe must be a no-op — rock is free within the plane");

        // Coronal → anterior-posterior; transverse → cephalad-caudal.
        go.transform.rotation = Quaternion.identity;
        View("probe", "coronal");
        Check(Mathf.Abs(Vector3.Dot(ShortAxis(go.transform), AnteriorAxis)) > 0.999f,
              "coronal must put the short axis on the anterior-posterior axis");
        go.transform.rotation = Quaternion.identity;
        View("probe", "transverse");
        Check(Mathf.Abs(Vector3.Dot(ShortAxis(go.transform), SuperiorAxis)) > 0.999f,
              "transverse must put the short axis on the cephalad-caudal axis");

        Check(View("probe", "oblique").Contains("unknown plane"),
              "an unrecognised plane name must be refused, not silently applied");

        // Mid-sagittal: sagittal orientation AND slid onto the midline plane
        // (default config: LeftAxis=+X, so the median plane is x = midline.x).
        // Only the lateral offset zeroes; cephalad position and depth stay.
        var midPt = new GameObject("midline_pt").transform;
        midPt.position = Vector3.zero;                 // median plane at x = 0
        midline = midPt;
        binding.pivot = null;                          // pivot = the probe's own position
        go.transform.position = new Vector3(37f, 5f, 9f);   // off midline (+X), some cephalad/depth
        go.transform.rotation = Quaternion.identity;
        View("probe", "midsagittal");
        Check(Mathf.Abs(Vector3.Dot(ShortAxis(go.transform), LeftAxis)) > 0.999f,
              "mid-sagittal must set sagittal orientation (short axis on left-right)");
        Check(Mathf.Abs(Vector3.Dot(go.transform.position - midPt.position, LeftAxis)) < 1e-3f,
              $"mid-sagittal must slide the footprint onto the midline (zero lateral offset); x={go.transform.position.x:0.###}");
        Check(Mathf.Abs(go.transform.position.y - 5f) < 1e-3f && Mathf.Abs(go.transform.position.z - 9f) < 1e-3f,
              "mid-sagittal must leave cephalad position and depth unchanged — only the lateral offset moves");
        midline = null;
        Check(View("probe", "midsagittal").Contains("no midline"),
              "mid-sagittal must refuse when no midline is assigned, not slide to the world origin");
        DestroyImmediate(midPt.gameObject);

        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        // ---- PointAt: a GENERIC look-at — swing the business end onto a target ----
        // The probe's business end is the beam (-Z). It's a generic verb: no
        // authoring, works off the business end alone. Pivot null → turns in place.
        binding.pivot = null;
        go.transform.position = new Vector3(3f, 4f, 5f);
        go.transform.rotation = Quaternion.identity;   // beam (-Z) starts pointing -Z
        Vector3 pointPosBefore = go.transform.position;
        string paMsg = PointAt("probe", "cephalad");
        Check(Vector3.Dot(binding.BusinessEndVector, SuperiorAxis) > 0.999f,
              $"PointAt cephalad must aim the business end at the head; dot={Vector3.Dot(binding.BusinessEndVector, SuperiorAxis):0.###}");
        Check((go.transform.position - pointPosBefore).sqrMagnitude < 1e-6f,
              "PointAt with no pivot turns in place — position must not move");
        Check(paMsg.StartsWith("pointed"), $"PointAt readback verb, got \"{paMsg}\"");

        // PointAt a named target: the business end points from the tool toward it.
        var ptTgt = new GameObject("point_target").transform;
        ptTgt.position = go.transform.position - SuperiorAxis * 100f;   // caudad of the tool
        landmarks = new[] { pivot.transform, ptTgt };
        PointAt("probe", "point_target");
        Check(Vector3.Dot(binding.BusinessEndVector, SuperiorAxis) < -0.999f,
              "PointAt a caudad target must aim the business end caudad");
        DestroyImmediate(ptTgt.gameObject);
        landmarks = new[] { pivot.transform };

        // ---- §3 Viewpoint: place an OBSERVER at an anatomical view, looking at it -
        // Gated on viewpointsEnabled (a probe wouldn't, but we borrow the binding).
        // Default config: LeftAxis=+X, AnteriorAxis=+Y, SuperiorAxis=+Z. The default
        // SUBJECT is `origin` (the patient centre already used for reporting).
        var subj = new GameObject("subject").transform;
        subj.position = new Vector3(10f, 20f, 30f);
        origin = subj;                                 // patient centre
        binding.viewpointsEnabled = true;
        binding.viewpointDistanceMm = 100f;
        go.transform.rotation = Quaternion.identity;

        string vpMsg = Viewpoint("probe", "AP");
        Check(Vector3.Distance(go.transform.position, subj.position + AnteriorAxis * 100f) < 1e-2f,
              $"AP viewpoint must sit the tool on the anterior side of `origin` at the framing distance; pos={go.transform.position}");
        Check(Vector3.Dot(binding.BusinessEndVector, -AnteriorAxis) > 0.999f,
              "AP viewpoint must aim the business end back at the subject (looking posterior)");
        Check(vpMsg.Contains("AP"), $"AP viewpoint readback should name the view, got \"{vpMsg}\"");

        Viewpoint("probe", "superior");   // "from the top"
        Check(Vector3.Distance(go.transform.position, subj.position + SuperiorAxis * 100f) < 1e-2f,
              "superior viewpoint (from the top) must sit the tool above the subject");
        Check(Vector3.Dot(binding.BusinessEndVector, -SuperiorAxis) > 0.999f,
              "superior viewpoint must look down at the subject");

        // A named target overrides the default subject ("an AP view OF THE liver").
        var liver = new GameObject("liver").transform;
        liver.position = new Vector3(-5f, -6f, -7f);
        landmarks = new[] { pivot.transform, liver };
        string ofLiver = Viewpoint("probe", "AP", "liver");
        Check(Vector3.Distance(go.transform.position, liver.position + AnteriorAxis * 100f) < 1e-2f,
              "a named target must reframe the viewpoint on it, overriding the default `origin` subject");
        Check(ofLiver.Contains("liver"), $"framing a named target should name it, got \"{ofLiver}\"");
        Check(Viewpoint("probe", "AP", "nonexistent_thing").Contains("no object named"),
              "an unfound target must be refused, not quietly framed on the centre");
        DestroyImmediate(liver.gameObject);
        landmarks = new[] { pivot.transform };

        // Plane words name an AXIS, not a side: the side is inferred from which side
        // of the MIDLINE the tool already sits on (the medial/lateral rule), and is
        // ASKED for inside the deadzone rather than guessed.
        Check(Viewpoint("probe", "sagittal").Contains("no midline"),
              "a plane viewpoint must refuse when no midline is assigned to measure the side from");

        var vpMid = new GameObject("vp_midline").transform;
        vpMid.position = Vector3.zero;                       // median plane at x = 0 (LeftAxis = +X)
        midline = vpMid;

        go.transform.position = -LeftAxis * 500f;            // tool on the patient's RIGHT
        string sagRight = Viewpoint("probe", "sagittal");
        Check(Vector3.Distance(go.transform.position, subj.position - LeftAxis * 100f) < 1e-2f,
              $"sagittal from the patient's right must place a RIGHT lateral view; pos={go.transform.position}");
        Check(sagRight.Contains("right lateral"), $"the readback must name the inferred side, got \"{sagRight}\"");

        go.transform.position = LeftAxis * 500f;             // tool on the patient's LEFT
        Viewpoint("probe", "sagittal");
        Check(Vector3.Distance(go.transform.position, subj.position + LeftAxis * 100f) < 1e-2f,
              "sagittal from the patient's left must place a LEFT lateral view — the side follows the tool");

        // On the midline the side is undecidable: ask, and move nothing (T0.2).
        go.transform.position = vpMid.position;
        Vector3 ambigBefore = go.transform.position;
        string ambig = Viewpoint("probe", "sagittal");
        Check(ambig.Contains("left or right"), $"sagittal on the midline must ask which side, got \"{ambig}\"");
        Check((go.transform.position - ambigBefore).sqrMagnitude < 1e-6f,
              "an ambiguous viewpoint must not move anything");

        // The same rule on the other two axes.
        go.transform.position = AnteriorAxis * 500f;         // in front of the patient
        Viewpoint("probe", "coronal");
        Check(Vector3.Distance(go.transform.position, subj.position + AnteriorAxis * 100f) < 1e-2f,
              "coronal from in front must place an AP view");
        go.transform.position = -SuperiorAxis * 500f;        // below the patient
        Viewpoint("probe", "transverse");
        Check(Vector3.Distance(go.transform.position, subj.position - SuperiorAxis * 100f) < 1e-2f,
              "transverse from below must place an INFERIOR view");

        midline = null;
        DestroyImmediate(vpMid.gameObject);

        // REGRESSION: consecutive viewpoints must not drift. Left lateral then right
        // lateral is a 180 degree reversal — the case that exposed both the fly-through
        // path and the stale glide pivot — and each must land EXACTLY on its point,
        // however many times they alternate. A tool that ends a viewpoint looking at
        // the subject must still be looking at it after the next one.
        for (int i = 0; i < 3; i++)
        {
            Viewpoint("probe", "left");
            Check(Vector3.Distance(go.transform.position, subj.position + LeftAxis * 100f) < 1e-2f,
                  $"repeat {i}: left lateral must land exactly on its point, at {go.transform.position}");
            Check(Vector3.Dot(binding.BusinessEndVector, (subj.position - go.transform.position).normalized) > 0.999f,
                  $"repeat {i}: and must be looking at the subject");
            Viewpoint("probe", "right");
            Check(Vector3.Distance(go.transform.position, subj.position - LeftAxis * 100f) < 1e-2f,
                  $"repeat {i}: right lateral after left must land exactly, at {go.transform.position} (no drift)");
            Check(Vector3.Dot(binding.BusinessEndVector, (subj.position - go.transform.position).normalized) > 0.999f,
                  $"repeat {i}: and must still be looking at the subject after the reversal");
        }
        // The radius is the framing distance, not whatever the tool drifted to.
        Check(Mathf.Abs(Vector3.Distance(go.transform.position, subj.position) - 100f) < 1e-2f,
              "a viewpoint must sit at the framing distance, not at an accumulated radius");

        // A viewpoint reached from a pose NOT looking at the subject must still land
        // exactly — the arc is chosen by the aim turn, and `delta` closes the rest.
        go.transform.position = subj.position + new Vector3(13f, -27f, 41f);
        go.transform.rotation = Quaternion.Euler(31f, 197f, 74f);
        Viewpoint("probe", "superior");
        Check(Vector3.Distance(go.transform.position, subj.position + SuperiorAxis * 100f) < 1e-2f,
              "a viewpoint from an arbitrary un-aimed pose must still land exactly on its point");
        Check(Vector3.Dot(binding.BusinessEndVector, -SuperiorAxis) > 0.999f,
              "...and end up looking at the subject");

        Check(Viewpoint("probe", "oblique").Contains("unknown viewpoint"),
              "an unknown viewpoint word must be refused, not applied");

        binding.viewpointsEnabled = false;
        Check(Viewpoint("probe", "AP").Contains("doesn't do anatomical viewpoints"),
              "viewpoints must be gated — a tool that doesn't declare them refuses (a probe has planes, not viewpoints)");
        origin = null;
        DestroyImmediate(subj.gameObject);

        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        // ---- spec T5/T6: verb selects the axis, qualifier selects the sign ----
        // Reuses the `binding` built at the top. Identity rotation, so the tool's
        // local axes are the world axes.
        Check(FindBinding(go.transform) == binding, "a tool's profile must be findable by its transform");

        // A tool-local qualifier names the sign outright (T5.4).
        Check(TryResolveToolVerb(binding, "slide", "toward notch", false, out var u, out _, out _)
              && Vector3.Dot(u, go.transform.right) > 0.999f, "'slide toward notch' must resolve to +X");
        // The single-token form the model emits (Token collapses spaces) must match too.
        Check(TryResolveToolVerb(binding, "slide", "toward_notch", false, out u, out _, out _)
              && Vector3.Dot(u, go.transform.right) > 0.999f, "the underscore token 'toward_notch' must resolve like 'toward notch'");
        // The context must ADVERTISE the tool-local qualifiers, or the model can't use them (T9.5).
        Check(BuildContext().Contains("[LOCAL DIRECTIONS]") && BuildContext().Contains("slide=toward_notch|away_from_notch"),
              "BuildContext must advertise a tool's local direction tokens");

        // ---- verb advertising + primary tag: the model routes by which tool owns
        // a verb, defaulting to the primary when none is named (the overloaded-word
        // fix). This probe binding declares View (imaging planes) but NOT Viewpoint.
        string tv = BuildContext();
        Check(tv.Contains("[TOOL VERBS]"), "BuildContext must advertise per-tool command verbs");
        Check(tv.Contains("probe (primary):"), "the first tool must be tagged (primary) in the movable list");
        // The probe's own profile verbs appear; the camera-only Viewpoint does not.
        // Note "Compression" — the tool's OWN business-end word, not a hardcoded
        // "Compress"/"Lift" (that printed ultrasound vocabulary onto every tool and
        // hid the tool's real verb, e.g. a camera's Zoom).
        foreach (var expect in new[] { "Slide", "Sweep", "Rock", "Tilt", "View", "Compression", "PointAt" })
            Check(tv.Contains(expect), $"[TOOL VERBS] must advertise the probe's '{expect}'");
        Check(!tv.Contains("Viewpoint"), "a tool that doesn't declare viewpoints must NOT advertise Viewpoint");

        // ---- T6.1 generic tool verbs: any AUTHORED verb is a usable command ----
        // The fixed commands each pass a hardcoded lookup word, so a tool that names
        // its motions differently (a camera's "zoom"/"yaw") could never be reached.
        // The kind query is what lets a caller tell "unknown command" from "refused",
        // and pick millimetres vs degrees.
        Check(TryToolVerbKind("probe", "sweep", out bool k1) && !k1, "an authored TRANSLATION verb reports kind = translate");
        Check(TryToolVerbKind("probe", "rock", out bool k2) && k2, "an authored ROTATION verb reports kind = rotate");
        Check(TryToolVerbKind("probe", "fan", out _), "a synonym is reachable too (T6.2), not just the first verb");
        Check(!TryToolVerbKind("probe", "frobnicate", out _), "an undeclared verb must report NOT declared, so the caller can say 'unknown command'");
        Check(!TryToolVerbKind("nonexistent_tool", "sweep", out _), "an unknown object declares nothing");

        // A generic verb travels the same axis its fixed twin would. "compression" is
        // the probe's own business-end word and was previously unreachable by name.
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;
        string genericSweep = ToolCommand("probe", "sweep", "anterior", 100f);
        Vector3 viaGeneric = go.transform.position;
        go.transform.position = Vector3.zero;
        Sweep("probe", "anterior", 100f);
        Check(Vector3.Distance(viaGeneric, go.transform.position) < 1e-3f,
              $"a generic verb must move identically to its fixed command; generic {viaGeneric} vs fixed {go.transform.position}");
        Check(genericSweep.StartsWith("sweep"), $"the generic readback echoes the verb as given, got \"{genericSweep}\"");

        go.transform.position = Vector3.zero;
        ToolCommand("probe", "compression", "push in", 50f);
        Check(Vector3.Dot(go.transform.position, -go.transform.forward) > 0f,
              "the tool's OWN business-end verb must be reachable generically and push toward the business end");

        // A rotation verb through the generic path turns about the same axis as its
        // fixed twin (degrees, not mm) — and an undeclared verb is refused by name.
        go.transform.position = new Vector3(0f, 50f, 0f);
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);
        Quaternion beforeGenericRot = go.transform.rotation;
        ToolCommand("probe", "rock", "cephalad", 15f);
        Quaternion viaGenericRot = go.transform.rotation;
        go.transform.rotation = beforeGenericRot;
        Rock("probe", "cephalad", 15f);
        Check(Quaternion.Angle(viaGenericRot, go.transform.rotation) < 0.01f,
              "a generic ROTATION verb must turn identically to its fixed command");
        Check(ToolCommand("probe", "frobnicate", "cephalad", 10f).Contains("frobnicate"),
              "an undeclared verb must be refused BY NAME, not silently ignored");
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        // View is gated on imaging planes: a tool without them refuses it, so an
        // overloaded 'sagittal' can't reorient a camera. (Probe has it; borrow a
        // bare tool to prove the refusal.)
        var noPlanes = new GameObject("no_planes");
        tools = new[] { binding, new ToolBinding { tool = noPlanes.transform } };
        Check(View("no_planes", "sagittal").Contains("doesn't do imaging planes"),
              "View must be refused on a tool that doesn't declare imaging planes");
        Check(BuildContext().Contains("[TOOL VERBS]"), "advertising must survive a second tool");
        DestroyImmediate(noPlanes);
        tools = new[] { binding };
        Check(TryResolveToolVerb(binding, "slide", "away from notch", false, out u, out _, out _)
              && Vector3.Dot(u, go.transform.right) < -0.999f, "'slide away from notch' must resolve to -X");

        // An anatomical qualifier is resolved in the patient frame, then picks
        // the sign of the tool axis that best goes that way (T5.4).
        Check(TryResolveToolVerb(binding, "slide", "left", false, out u, out _, out _)
              && Vector3.Dot(u, LeftAxis) > 0.999f, "'slide left' must pick the +X sign (patient-left)");
        Check(TryResolveToolVerb(binding, "slide", "right", false, out u, out _, out _)
              && Vector3.Dot(u, LeftAxis) < -0.999f, "'slide right' must pick the opposite sign");

        // Synonyms on one axis behave identically (T6.2) — the mechanism that
        // lets a tool accept caudad/cephalic/superior without a code change.
        Check(TryResolveToolVerb(binding, "sweep", "anterior", false, out var uSweep, out int axSweep, out _)
              && TryResolveToolVerb(binding, "fan", "anterior", false, out var uFan, out int axFan, out _)
              && axSweep == axFan && Vector3.Distance(uSweep, uFan) < 1e-4f,
              "'fan' is a synonym of 'sweep' and must resolve identically");

        // A verb this tool does not declare is refused BY NAME (T6.4, T0.3).
        Check(!TryResolveToolVerb(binding, "wibble", "left", false, out _, out _, out var mUnknown)
              && mUnknown.Contains("wibble"), "an undeclared verb must be refused, naming the verb");
        // Rock is a rotation, not a translation — the tables are separate.
        Check(!TryResolveToolVerb(binding, "rock", "left", false, out _, out _, out _),
              "a rotation verb must not resolve as a translation");

        // A verb with no qualifier is ambiguous and must be refused (T5.3, T0.2).
        Check(!TryResolveToolVerb(binding, "slide", "", false, out _, out _, out var mBare)
              && mBare.Contains("direction"), "a verb with no qualifier must be refused, not defaulted");

        // An anatomical direction perpendicular to the axis is unreachable (T5.5).
        Check(!TryResolveToolVerb(binding, "slide", "cephalad", false, out _, out _, out var mPerp)
              && mPerp.Contains("perpendicular"),
              "a direction perpendicular to the tool axis must be refused, not rounded to a side");

        // ROTATION: the qualifier describes where the BUSINESS END goes (T5.7).
        // Rock turns about the short axis (Y), and 'rock left' must be the sense
        // that carries the beam toward the patient's left.
        Check(TryResolveToolVerb(binding, "rock", "left", true, out var uRock, out int axRock, out _)
              && axRock == 1 && Mathf.Abs(Vector3.Dot(uRock, go.transform.up)) > 0.999f,
              "'rock' must turn about the tool's short axis (Y)");
        Check(Vector3.Dot(Vector3.Cross(uRock, binding.BusinessEndVector), LeftAxis) > 0f,
              "'rock left' must be the sense that carries the BUSINESS END toward the patient's left");

        // T5.8: a far-end tool describes the same word with the opposite sense.
        binding.rotationQualifiersDescribe = QualifierReference.FarEnd;
        Check(TryResolveToolVerb(binding, "rock", "left", true, out var uFar, out _, out _)
              && Vector3.Dot(uRock, uFar) < -0.999f,
              "declaring far-end qualifiers must invert the rotation sense");
        binding.rotationQualifiersDescribe = QualifierReference.BusinessEnd;

        // The DEFAULT convention is the handle (far end): "tilt cephalad" leans
        // the handle headward, the beam the other way (the pivot effect).
        Check(new ToolBinding().rotationQualifiersDescribe == QualifierReference.FarEnd,
              "the default rock/tilt convention must be the handle (far end)");

        // T6.4: a verb a tool does NOT declare is refused — the profile is the
        // only source of tool verbs; there is no hidden default axis to fall to.
        binding.translateY = new AxisVerbs();          // withdraw "sweep" from this tool
        Check(Sweep("probe", "cephalad", 10f).Contains("not a movement"),
              "a verb absent from a tool's profile must be refused, not silently handled");
        binding.translateY = new AxisVerbs { verbs = new[] { "sweep", "fan" } };

        // Compress is a tool verb too (T2.4): a tool that declares no business-end
        // translation does nothing, exactly like an unauthored tilt or sweep.
        var bareGo = new GameObject("bare_tool");
        tools = new[] { binding, new ToolBinding { tool = bareGo.transform } };
        Check(Compress("bare_tool", 5f).Contains("not a movement"),
              "compress must be refused on a tool that declares no compression");
        Check(Move("bare_tool", "cephalad", 5f).StartsWith("moved"),
              "a bare tool still MOVES anatomically (T6.5) — only its tool-local verbs are gated");
        DestroyImmediate(bareGo);
        tools = new[] { binding };

        // ---- spec T7: per-tool host subscriber ----
        var subGo = new GameObject("test_subscriber");
        var testSub = subGo.AddComponent<ToolSubscriberTestDouble>();
        binding.subscribers = new MonoBehaviour[] { testSub };
        var canted = Quaternion.Euler(270f, 315f, 0f);

        // Before may REDUCE, and the reduction is reported with the subscriber's
        // OWN reason (T7.3/T7.5) — not a fixed package string.
        testSub.cap = 10f; testSub.reason = "held by test tissue"; testSub.afterNote = "";
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        string r1 = Rock("probe", "left", 40f);
        Check(testSub.beforeCalls > 0, "a per-tool subscriber's BeforeMotion must be consulted (T7.3)");
        Check(r1.Contains("held by test tissue"), "a reduction must report the subscriber's own reason (T7.5)");
        Check(r1.Contains("10 degrees instead of 40"), "the reported angle must be the APPLIED one, not the ask");

        // Reduce to zero is a refusal, but still reports why (T7.4/T7.5).
        testSub.cap = 0f; testSub.reason = "no room to rock";
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        string r2 = Rock("probe", "left", 20f);
        Check(r2.Contains("0 degrees") && r2.Contains("no room to rock"),
              "a reduction to zero must still be reported with a reason");

        // After note surfaces (T7.6).
        testSub.cap = -1f; testSub.afterNote = "needle at 2 cm depth";
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        string r3 = Rock("probe", "left", 5f);
        Check(testSub.afterCalls > 0 && r3.Contains("needle at 2 cm depth"),
              "the subscriber's AfterMotion note must surface (T7.6)");

        // T7.7: a pass-through subscriber must be indistinguishable from none.
        testSub.cap = -1f; testSub.afterNote = "";
        binding.subscribers = null;
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        Rock("probe", "left", 15f); Quaternion noSub = go.transform.rotation;
        binding.subscribers = new MonoBehaviour[] { testSub };
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        Rock("probe", "left", 15f); Quaternion passThru = go.transform.rotation;
        Check(Quaternion.Angle(noSub, passThru) < 0.05f,
              "a pass-through subscriber must move the tool identically to no subscriber (T7.7)");

        // T7.1 — SEVERAL subscribers compose. Each may only reduce (T7.4), so the
        // result is the MINIMUM and the list order cannot change it. Two limiters:
        var subGo2 = new GameObject("test_subscriber_2");
        var testSub2 = subGo2.AddComponent<ToolSubscriberTestDouble>();
        testSub.cap = 25f; testSub.reason = "capped by the first";  testSub.afterNote = "";
        testSub2.cap = 10f; testSub2.reason = "capped by the second"; testSub2.afterNote = "";

        binding.subscribers = new MonoBehaviour[] { testSub, testSub2 };
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        string two = Rock("probe", "left", 40f);
        Check(two.Contains("10 degrees instead of 40"),
              $"several subscribers must compose as the MINIMUM allowance, got \"{two}\"");
        Check(two.Contains("capped by the second"),
              "the reason must come from whichever subscriber actually BOUND the motion, not the first consulted");

        // Reversing the list must not change the outcome — that is the property the
        // minimum rule buys, and the reason order is not a configuration decision.
        binding.subscribers = new MonoBehaviour[] { testSub2, testSub };
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        string twoRev = Rock("probe", "left", 40f);
        Check(twoRev.Contains("10 degrees instead of 40") && twoRev.Contains("capped by the second"),
              $"subscriber order must not affect the result, got \"{twoRev}\"");

        // Every subscriber's after-note surfaces, not just the first.
        testSub.cap = -1f; testSub.afterNote = "note one";
        testSub2.cap = -1f; testSub2.afterNote = "note two";
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        string notes = Rock("probe", "left", 5f);
        Check(notes.Contains("note one") && notes.Contains("note two"),
              $"every subscriber's AfterMotion note must surface (T7.6), got \"{notes}\"");
        testSub.afterNote = ""; testSub2.afterNote = "";

        // An entry that does NOT implement the interface is skipped, and must not
        // stop the real subscribers in the same list from being consulted. The list
        // is typed MonoBehaviour, so anything is assignable and would otherwise read
        // as wired while doing nothing — OnValidate warns about exactly this case.
        // (`this` is a MonoBehaviour and is not an IToolMotionSubscriber.)
        testSub2.cap = 10f;
        binding.subscribers = new MonoBehaviour[] { this, testSub2, null };
        go.transform.position = Vector3.zero; go.transform.rotation = canted;
        string mixed = Rock("probe", "left", 40f);
        Check(mixed.Contains("10 degrees instead of 40"),
              $"a non-implementing entry (and a null slot) must be skipped, not abort the list, got \"{mixed}\"");

        DestroyImmediate(subGo2);
        binding.subscribers = null;
        DestroyImmediate(subGo);
        go.transform.rotation = Quaternion.identity;

        // ---- spec T10: capture → replay sequences a multi-command turn ----
        // The key property: a captured turn applies its commands INSTANTLY IN
        // ORDER, so each resolves against the previous one's real result. Rock and
        // tilt are non-commuting rotations about different axes, so their order is
        // observable — the perfect ordering test.
        go.transform.position = new Vector3(0f, 50f, 0f);
        go.transform.rotation = Quaternion.Euler(270f, 315f, 0f);   // canted, oblique to every axis
        Quaternion seqStart = go.transform.rotation;

        Tilt("probe", "left", 15f); Rock("probe", "cephalad", 15f);   // tilt THEN rock, one at a time
        Quaternion expected = go.transform.rotation;

        go.transform.rotation = seqStart;
        Rock("probe", "cephalad", 15f); Tilt("probe", "left", 15f);   // reversed order
        Check(Quaternion.Angle(go.transform.rotation, expected) > 1f,
              "rock-then-tilt must differ from tilt-then-rock — so the ordering test below is real");

        go.transform.rotation = seqStart;
        BeginCapture();
        Tilt("probe", "left", 15f, 1f);         // durations set; capture applies instant + records
        Rock("probe", "cephalad", 15f, 1f);
        Check(Quaternion.Angle(go.transform.rotation, expected) < 0.05f,
              "a captured turn applies its commands IN ORDER, each against the previous result (T10.1/T10.3)");
        EndCaptureAndReplay();   // edit mode: no coroutine, leaves the final pose
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        // A GLIDED step that both turns and travels must converge on the same pose the
        // INSTANT one lands. Capture applies instantly but the visible pose comes from
        // the replay, so if these disagree the tool silently ends somewhere other than
        // where the command computed — which is what a fixed pivot did: each increment
        // orbited about a point the tool had already translated away from. Stepped in
        // chunks here, since coroutines don't run in an edit-mode test. 180 degrees and
        // a long travel, i.e. the left-lateral-to-right-lateral case, where it is worst.
        {
            Vector3 p0 = new Vector3(11f, 22f, 33f);
            Quaternion r0 = Quaternion.Euler(17f, 41f, 63f);
            Vector3 pv = new Vector3(-5f, 8f, 2f), ax = new Vector3(0.3f, -0.7f, 0.5f).normalized;
            float dg = 180f; Vector3 dl = new Vector3(60f, -20f, 15f);

            go.transform.position = p0; go.transform.rotation = r0;
            StepInstant(go.transform, pv, ax, dg, dl);
            Vector3 instantPos = go.transform.position; Quaternion instantRot = go.transform.rotation;

            go.transform.position = p0; go.transform.rotation = r0;
            Vector3 moving = pv; const int N = 16;
            for (int i = 0; i < N; i++)
            {
                go.transform.RotateAround(moving, ax, dg / N);
                go.transform.position += dl / N; moving += dl / N;
            }
            Check(Vector3.Distance(go.transform.position, instantPos) < 1e-2f,
                  $"a glided turn+travel must land where the instant one does; glide {go.transform.position} vs instant {instantPos}");
            Check(Quaternion.Angle(go.transform.rotation, instantRot) < 0.05f,
                  "...and with the same orientation");
        }
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        // Compress goes INTO tissue (-Z), Lift comes off it (+Z), per A.3.
        go.transform.position = Vector3.zero;
        Compress("probe", 10f);
        Check(Vector3.Dot(go.transform.position, -go.transform.forward) > 0f,
              $"compress must travel -Z into tissue, went {go.transform.position}");
        go.transform.position = Vector3.zero;
        Lift("probe", 10f);
        Check(Vector3.Dot(go.transform.position, go.transform.forward) > 0f,
              $"lift must travel +Z off tissue, went {go.transform.position}");

        // What the LLM is told must be the PATIENT's frame, not world space.
        // Pinned with a config where the two differ — with cephalad=+Z the
        // report is accidentally world-shaped and the check proves nothing.
        // Regression: this config once shipped a hardcoded "y=anterior
        // z=cephalad" claim, and a model asked to move the probe level with the
        // head computed 109 mm where the truth was 286 — reported as success.
        cephaladAxis = WorldAxis.PlusY;      // superior  = +Y
        anteriorAxis = WorldAxis.MinusZ;     // posterior = +Z
        patientLeftAxis = WorldAxis.PlusX;   // left      = +X
        origin = null;
        go.transform.position = new Vector3(10f, 20f, 30f) * unitsPerMm;
        string ctx = BuildContext();
        Check(ctx.Contains("x=left y=posterior z=superior"), "context must declare the patient (LPS) frame");
        Check(!ctx.Contains("y=anterior"), "context must not restate the old world-frame axis claim");
        // world (10,20,30) -> left=+X:10, posterior=+Z:30, superior=+Y:20
        Check(ctx.Contains("probe (primary): (10, 30, 20)"),
              $"context must report LPS coords (and tag the primary tool), not world coords. Got:\n{ctx}");

        pivot.transform.position = new Vector3(5f, 5f, 5f) * unitsPerMm;
        origin = pivot.transform;
        ctx = BuildContext();
        Check(ctx.Contains("probe (primary): (5, 25, 15)"),
              $"context must report coords relative to `origin`. Got:\n{ctx}");
        origin = null;
        pivot.transform.position = Vector3.zero;
        cephaladAxis = WorldAxis.PlusZ;
        anteriorAxis = WorldAxis.PlusY;
        patientLeftAxis = WorldAxis.PlusX;

        // FromTransform must (a) actually engage and track the source, and
        // (b) describe the SAME patient as the equivalent World Constants
        // config. The source is posed so all three axes DIFFER from the
        // dropdowns above — pose it to agree and these checks pass even when
        // the mode never engages and LeftAxis falls through to Vec(...).
        var src = new GameObject("axis_source").transform;
        src.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.right); // up=+X head, forward=+Z face
        var savedMode = axisMode;
        axisMode = AxisMode.FromTransform;
        axisSource = src;

        CheckFrame("FromTransform");
        Check(Vector3.Dot(MoveFromOrigin("cephalad"), src.up) > 0.999f,
              $"FromTransform: cephalad must track source.up ({src.up}), not the World Constants dropdown");
        Check(Vector3.Dot(MoveFromOrigin("anterior"), src.forward) > 0.999f,
              $"FromTransform: anterior must track source.forward ({src.forward}), not the World Constants dropdown");
        Check(Vector3.Dot(MoveFromOrigin("left"), -src.right) > 0.999f,
              $"FromTransform: patient-left must be -source.right ({-src.right}); +right inverts laterality and mirrors the frame");

        // The two modes must agree about the same patient. Posed as the World
        // Constants patient above (head +Z, face +Y), patient-left must land on
        // +X — not -X.
        src.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
        Check(Vector3.Dot(MoveFromOrigin("left"), Vector3.right) > 0.999f,
              "FromTransform vs World Constants: a patient posed head +Z / face +Y must put patient-left at +X in BOTH modes");

        axisMode = savedMode;
        axisSource = null;

        Vector3 MoveFromOrigin(string dir)
        {
            go.transform.position = Vector3.zero;
            Move("probe", dir, 10f);
            return go.transform.position.normalized;
        }

        DestroyImmediate(src.gameObject);
        DestroyImmediate(tgt);
        DestroyImmediate(pivot);
        DestroyImmediate(go);

        if (failed == 0) Debug.Log("ContextCommander self test passed");
        else Debug.LogError($"[ContextCommander] SELF TEST FAILED — {failed} check(s) failed; see errors above.");
    }
#endif
}

#if UNITY_EDITOR
// Test double for the T7 subscriber self-tests. Editor-only; never ships.
// `cap >= 0` reduces any motion to that magnitude (with a reason); otherwise the
// request passes through unmodified — which is exactly the T7.7 "no subscriber
// vs pass-through subscriber must be indistinguishable" case.
internal class ToolSubscriberTestDouble : MonoBehaviour, IToolMotionSubscriber
{
    public float cap = -1f;
    public string reason = "capped by test";
    public string afterNote = "";
    public int beforeCalls, afterCalls;

    public float BeforeMotion(ToolMotion motion, out string r)
    {
        beforeCalls++; r = null;
        if (cap >= 0f && cap < motion.magnitude) { r = reason; return cap; }
        return motion.magnitude;
    }

    public string AfterMotion(ToolMotion motion) { afterCalls++; return afterNote; }
}
#endif
