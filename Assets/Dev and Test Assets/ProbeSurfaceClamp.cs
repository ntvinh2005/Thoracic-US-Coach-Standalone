using System.Collections.Generic;
using UnityEngine;
using CSSALT.LLMInterface;

// ProbeSurfaceClamp — continuous two-sided constraint that keeps the ultrasound
// probe ON the skin: neither buried past an allowed interference depth, nor
// floating off it. The probe rides the surface.
//
// It reads SignedDepth from a SkinIntersectionCompression provider, which grids
// the whole footprint and reports the largest signed penetration:
//   > MaxInterference  → buried too deep; push OUT along the probe axis.
//   < 0                → the whole footprint floated off; pull IN until the
//                        nearest point touches (SignedDepth = 0).
//   in [0, Max]        → riding the surface within the allowance; do nothing.
// Because the grid reports the DEEPEST point, a rocked/tilted edge is caught even
// when the face centre is on the surface.
//
// Why it works everywhere: it reacts to the probe's TRANSFORM, not to any
// command. However the probe got where it is — LLM (ContextCommander), the phone
// app, manual dragging, animation — LateUpdate runs after all of them, measures,
// and corrects. That's why this is a standalone component and NOT part of
// ContextCommander: a command-level constraint would only catch LLM-driven motion
// and let the phone sail through the skin (or float off it).
//
// Always-in-contact: there is no intentional "float" state. The only way off the
// skin is to move beyond the provider's ray reach (~ExtraRayLength_mm) or off the
// edge of the body — then no sample hits (AnyHit == false) and the probe free-
// floats. "Lift" therefore means "reduce compression toward just-touching", not
// "leave the surface".
//
// ─────────────────────────────────────────────────────────────────────────────
// TODO (noted, not built): optional "keep the probe perpendicular to the skin"
//
// A toggle to auto-orient the probe so its longitudinal axis follows the skin's
// surface normal. Deliberately NOT built yet — this constraint is depth-only, and
// orientation was left to the trainee/AI (Rock/Tilt). Things to settle first:
//
//  * It FIGHTS Rock/Tilt by definition. Those exist to angle the probe OFF the
//    normal; a perpendicular constraint would undo them every frame. So it needs
//    a policy: does it apply only on initial placement / re-seating, or does it
//    continuously win? Probably "snap perpendicular when placed, then let Rock/
//    Tilt angle away from there" — i.e. a one-shot, not a standing constraint.
//  * It needs the SKIN NORMAL, which the provider does not currently expose.
//    SkinIntersectionCompression stores ContactPoints but discards
//    hitInfo.normal — so this needs another small additive change there (same
//    shape as the SignedDepth/AnyHit addition), e.g. an averaged normal over the
//    contacting grid samples. Averaging matters: a single sample is noisy on a
//    detailed mesh.
//  * Interaction with the depth clamp: re-orienting changes which footprint point
//    is deepest, so orientation and depth would need to settle together (orient,
//    then re-clamp) rather than fight across frames.
//  * Only meaningful while in contact — undefined off-body.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(100)]   // run after the provider's Update and typical movers
public class ProbeSurfaceClamp : MonoBehaviour, IToolMotionSubscriber
{
    [Header("Wiring")]
    // [Tooltip("The probe root to move. The whole rig (markers, Skin Intersection child) rides with it.")]
    public Transform ProbeRoot;

    // [Tooltip("Footprint penetration provider. Supplies CompressionDepth (mm) and CompressionNormal (outward).")]
    public SkinIntersectionCompression Interference;

    [Header("Limit")]
    // [Tooltip("Deepest penetration allowed before the probe is pushed back out (mm). The 'tissue gives a little' allowance.")]
    public float MaxInterference_mm = 5f;

    // [Tooltip("Fraction of the needed correction applied per frame. 1 = a hard snap to the limit; lower = softer/springier. Start at 1.")]
    [Range(0.1f, 1f)] public float Responsiveness = 1f;

    [Header("LLM feedback (optional)")]
    // [Tooltip("If set, the probe's contact state is reported back to the AI after each motion (quiet unless at the limit or off the skin). Auto-found if left empty.")]
    public LLMInterfaceControl LLM;

    [Header("Rock/Tilt tissue cap (optional)")]
    // [Tooltip("The ContextCommander whose Rock/Tilt commands are capped so the footprint can't bury past MaxInterference. Auto-found if empty. Depth clamp above is unaffected; only COMMANDED rock/tilt is capped (manual/phone stay depth-only).")]
    public ContextCommander Commander;
    // [Tooltip("Cap AI Rock/Tilt at the interference limit (rotate only as far as the footprint edge can go before burying past MaxInterference).")]
    public bool CapRockTilt = true;

    // Contact state for feedback. Distinct from the correction logic: AtLimit means
    // the probe is at (or being held at) the interference limit, whether it was
    // commanded deeper or not.
    public enum Contact { OffBody, InContact, AtLimit }
    public Contact CurrentContact { get; private set; }

    [Header("Debug (runtime, read-only)")]
    [SerializeField] float signedDepth_mm;      // + buried, - floating gap
    [SerializeField] float correctionThisFrame_mm;   // + pushed out, - pulled in
    [SerializeField] string state;              // buried / floating / riding / off-body

    void Reset()
    {
        // Sensible defaults when the component is added to the probe root.
        ProbeRoot = transform;
        Interference = GetComponentInChildren<SkinIntersectionCompression>();
        LLM = FindFirstObjectByType<LLMInterfaceControl>();
        Commander = FindFirstObjectByType<ContextCommander>();
    }

    void OnEnable()
    {
        if (LLM == null) LLM = FindFirstObjectByType<LLMInterfaceControl>();
        if (LLM != null) LLM.contactStatusProvider = ContactNote;   // quiet per-turn after-note

        if (Commander == null) Commander = FindFirstObjectByType<ContextCommander>();
        WireSubscriber(true);
    }

    void OnDisable()
    {
        if (LLM != null && LLM.contactStatusProvider == ContactNote) LLM.contactStatusProvider = null;
        WireSubscriber(false);
    }

    // Attach (or detach) this clamp as one of the probe's per-tool motion subscribers
    // (spec T7). Replaces the old global rockTiltLimitProvider: each tool holds its
    // own limiters, so a needle and a probe can differ. ADDS to the list rather than
    // owning it, so whatever else the probe declares — a travel range, a collision
    // limit — survives this wiring itself up and down with the component.
    void WireSubscriber(bool on)
    {
        if (Commander == null || Commander.tools == null) return;
        Transform probe = ProbeRoot != null ? ProbeRoot : transform;
        foreach (var b in Commander.tools)
        {
            if (b == null || b.tool != probe) continue;

            var list = new List<MonoBehaviour>();
            if (b.subscribers != null)
                foreach (var m in b.subscribers)
                    if (m != null && !ReferenceEquals(m, this)) list.Add(m);   // drop nulls and any stale copy of us
            if (on) list.Add(this);
            b.subscribers = list.ToArray();
            return;
        }
        if (on)
            Debug.LogWarning($"[ProbeSurfaceClamp] '{probe.name}' has no ToolBinding in the ContextCommander, " +
                             "so the rock/tilt tissue cap won't apply. Add it to the commander's tool list.");
    }

    // T7.3 BEFORE: cap a rock/tilt so the footprint can't bury past MaxInterference.
    // How far can the probe turn about (pivot, axis) before the deepest footprint
    // sample buries too deep? Returns the allowed magnitude (<= requested). Because
    // the pivot is the face contact point, the rotation leaves the centre's depth
    // unchanged — so this caps the EDGE while the centre stays seated. Simulated by
    // rotating the probe, measuring, and restoring: a handful of raycasts, ONCE per
    // command, not a per-frame spring (that oscillated and threw the probe sideways).
    // Only rotations are capped; other motions pass through unchanged.
    public float BeforeMotion(ToolMotion motion, out string reason)
    {
        reason = null;
        if (!CapRockTilt || motion.kind != MotionKind.Rotate
            || ProbeRoot == null || Interference == null || motion.magnitude < 0.01f)
            return motion.magnitude;

        float allowed = LimitAngle(motion.pivot, motion.axis, motion.magnitude);
        if (allowed < motion.magnitude - 0.5f)
            reason = "the tissue won't let the probe angle that far";
        return allowed;
    }

    // T7.6 AFTER: the per-turn contact note (contactStatusProvider) already reports
    // contact state to the AI, so nothing extra to say per motion.
    public string AfterMotion(ToolMotion motion) => "";

    // Largest turn (0..requestedDeg, about the SIGNED axis) that keeps the deepest
    // footprint sample within the tissue limit.
    float LimitAngle(Vector3 pivot, Vector3 axis, float requestedDeg)
    {
        Vector3 savedPos = ProbeRoot.position;
        Quaternion savedRot = ProbeRoot.rotation;

        bool Fits(float deg)
        {
            ProbeRoot.position = savedPos; ProbeRoot.rotation = savedRot;
            ProbeRoot.RotateAround(pivot, axis, deg);
            Interference.Measure();
            return !Interference.AnyHit || Interference.SignedDepth <= MaxInterference_mm;
        }

        float allowed;
        if (Fits(requestedDeg)) allowed = requestedDeg;            // the whole turn fits
        else
        {
            float lo = 0f, hi = requestedDeg;                     // lo fits, hi doesn't
            for (int i = 0; i < 14; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Fits(mid)) lo = mid; else hi = mid;
            }
            allowed = lo;                                          // largest that still fits
        }

        ProbeRoot.position = savedPos; ProbeRoot.rotation = savedRot;
        Interference.Measure();
        return allowed;
    }

    // Short note for the AI after a motion. "" unless reality diverged from intent.
    // Only OFF-BODY is reported. "At limit" was dropped: it fired on the frame the
    // note is sampled (one frame after dispatch, mid-glide — the T7.6 timing
    // defect), giving false positives, AND it was redundant — when tissue actually
    // caps a rock/tilt, ToolAngle already reports the real capped angle and reason.
    // Off-body has no such false positive: the depth clamp keeps the probe seated,
    // so "no footprint sample hit" genuinely means the probe left the skin.
    string ContactNote()
    {
        switch (CurrentContact)
        {
            case Contact.OffBody: return "— the probe isn't on the skin.";
            default:              return "";
        }
    }

    void LateUpdate()
    {
        if (ProbeRoot == null || Interference == null) return;

        // Re-measure at the probe's CURRENT position first. Motion animations move
        // the probe in a coroutine that resumes between Update and LateUpdate, so
        // the provider's own Update-time reading is one frame stale here — using it
        // makes the clamp lag fast motion (probe dips inside / floats outside mid-
        // glide). Measuring now, right before correcting, keeps it glued.
        Interference.Measure();

        // No footprint sample reached the skin: the probe is off the edge or
        // beyond the ray reach. There's nothing to hold it to — free float.
        if (!Interference.AnyHit)
        {
            signedDepth_mm = 0f; correctionThisFrame_mm = 0f;
            CurrentContact = Contact.OffBody; state = "off-body";
            return;
        }

        // Signed: > 0 buried (deepest point), < 0 floating (nearest-point gap).
        float d = Interference.SignedDepth;
        signedDepth_mm = d;

        // Correction along the outward axis: positive = out of tissue, negative = in.
        //   buried past the limit → move out by the overshoot
        //   floating              → move in by the gap (d is already negative)
        float move = 0f;
        if (d > MaxInterference_mm) move = d - MaxInterference_mm;
        else if (d < 0f)           move = d;

        if (move != 0f)
        {
            Vector3 outward = Interference.CompressionNormal;   // Rear - Center: away from tissue
            if (outward.sqrMagnitude >= 1e-8f)
            {
                outward.Normalize();
                // Rays are parallel to this axis, so a move changes depth ~1:1 — one
                // step seats it on a flat surface; a curved one settles over a frame
                // or two. (Provider convention: 1 world unit = 1 mm.)
                float applied = move * Responsiveness;
                ProbeRoot.position += outward * applied;
                correctionThisFrame_mm = applied;
            }
        }
        else correctionThisFrame_mm = 0f;

        // Reported contact state (for feedback), from depth vs the limit. Note the
        // "at limit" test is d >= Max - epsilon, not d > Max: once clamped, the
        // probe SITS at the limit, and that is exactly the case worth reporting.
        if (d >= MaxInterference_mm - 0.5f) { CurrentContact = Contact.AtLimit; state = "at limit"; }
        else                                { CurrentContact = Contact.InContact; state = "in contact"; }
    }
}
