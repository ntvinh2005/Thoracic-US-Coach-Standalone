using System;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;

/*
================================================================================
 PhoneGyroController
================================================================================

 Drives a single Unity Transform from an iPhone or Android's built-in
 orientation sensors. The phone provides rotation; on-screen joystick and lift
 buttons provide translation. Everything flows through a small Node.js
 WebSocket relay; the phone side is a plain web page — no app install required.

 ONE COMPONENT PER CONTROLLED OBJECT
 -----------------------------------
 Each instance of this component manages exactly one controllable object (the
 probe, a needle, a surgical instrument, etc.) and filters incoming messages
 by the `target` tag that the phone attaches to every payload. Put multiple
 components on a single host GameObject (e.g. "PhoneControllers") to control
 several objects from the same room — one phone per target, each driving its
 own component.

 If two phones in the same room tag their messages with the same target, both
 phones drive the same component (last-write-wins on every field). There is
 no ownership tracking; assigning distinct targets is a convention, not a
 protocol guarantee.

 COORDINATE FRAMES
 -----------------
   1. World frame          — Unity global. 1 unit = 1 mm.
   2. Phone frame          — W3C DeviceOrientationEvent (alpha, beta, gamma).
   3. Object frame         — local axes of the TargetTransform.
   4. Operator frame       — implicit "forward/right/up" of the human watching
                             the screen. Set by camera placement.

 See the wiki pages "PhoneGyroController" and "CoordinateSystems" for
 the conceptual treatment; the calibration math in particular is documented
 in CoordinateSystems.md.
================================================================================
*/

[RequireComponent(typeof(PhoneRelayClient))]
public class PhoneGyroController : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    //  RELAY CONNECTION
    // ─────────────────────────────────────────────────────────────────────────
    // WebSocket lifecycle and relay-host configuration live on
    // PhoneRelayClient. This controller just subscribes to its
    // MessageReceived event. Multiple PhoneGyroController components on the
    // same GameObject share one connection — set RelayHost / ServerUrl in
    // exactly one place.
    //
    // Leave Relay null to auto-find the PhoneRelayClient on this GameObject
    // (the [RequireComponent] above guarantees one exists). Drag an explicit
    // reference here only if the relay lives on a different GameObject.
    [Header("Relay")]
    [Tooltip("Override the shared relay. Leave null to auto-use GetComponent<PhoneRelayClient>() on this GameObject.")]
    public PhoneRelayClient Relay;

    // ─────────────────────────────────────────────────────────────────────────
    //  TARGET IDENTITY
    // ─────────────────────────────────────────────────────────────────────────
    // The tag this controller matches on. The phone sends every message with
    // a `target` field carrying its currently selected object name (e.g. "probe"
    // or "needle"); this controller only processes messages whose target equals
    // TargetName. An empty/missing target on the incoming message is treated as
    // "applies to everyone" — useful for legacy clients and as a fallback.
    [Header("Target Identity")]
    [Tooltip("Filter tag for incoming messages. Only messages with target equal to this value (or with no target at all) will be processed by this controller.")]
    public string TargetName = "probe";

    [Tooltip("Friendly label shown in the phone's object picker (e.g. \"Ultrasound Probe\"). Leave blank to use TargetName. Purely cosmetic — the wire tag is always TargetName.")]
    public string DisplayName = "";

    // ─────────────────────────────────────────────────────────────────────────
    //  CONTROLLED OBJECT REFERENCES
    // ─────────────────────────────────────────────────────────────────────────
    // These fields used to be split into Probe/Needle pairs on a single
    // controller. They're now per-instance: one controller manages one object.
    //
    // FormerlySerializedAs keeps existing scene wiring intact across the rename.
    // If you previously had a Probe-only controller, the references carry over
    // automatically. If you had a single controller with both probe AND needle
    // fields set, the probe fields carry over here and you need to add a second
    // controller component for the needle and wire its fields manually.

    [Header("Controlled Object")]
    [FormerlySerializedAs("TargetProbe")]
    [Tooltip("The Transform this controller rotates and translates.")]
    public Transform TargetTransform;

    [FormerlySerializedAs("FaceCenterMarker")]
    [Tooltip("Pivot for rotation and origin for the skin raycast / lift translation. Typically a child of the controlled object at its contact point.")]
    public Transform RotationPivot;

    [Tooltip("Which axis of the RotationPivot points OUT of the patient (away from skin). Used for skin raycast and for the lift buttons, which translate along this axis. Interpreted in the space chosen by MarkerOutwardAxisSpace.")]
    public ProbeAxis MarkerOutwardAxis = ProbeAxis.Forward;

    [Tooltip("Frame for MarkerOutwardAxis: Local = the RotationPivot's local axis (rotates with the object). World = the global axis (constant regardless of object orientation, e.g. World Up = vertical lift no matter how the probe is tilted).")]
    public AxisSpace MarkerOutwardAxisSpace = AxisSpace.Local;

    [FormerlySerializedAs("ProbeForwardAxis")]
    [Tooltip("The controlled object's working travel direction for joystick-Y (sweeping motion). For the standard probe this is Down; for a needle this is Up (shaft direction). Interpreted in the space chosen by ForwardAxisSpace.")]
    public ProbeAxis ForwardAxis = ProbeAxis.Down;

    [Tooltip("Frame for ForwardAxis: Local = the controlled object's local axis (rotates with it). World = the global axis. Use World when you want joystick-forward to mean a fixed direction in the scene regardless of how the object has been rotated.")]
    public AxisSpace ForwardAxisSpace = AxisSpace.Local;

    [Tooltip("The controlled object's lateral direction for joystick-X (strafe / sliding sideways). Default Right matches the probe; flip to Left to invert if the object is rigged with the opposite handedness (e.g. some needles). Interpreted in the space chosen by StrafeAxisSpace.")]
    public ProbeAxis StrafeAxis = ProbeAxis.Right;

    [Tooltip("Frame for StrafeAxis: Local = the controlled object's local axis (rotates with it). World = the global axis.")]
    public AxisSpace StrafeAxisSpace = AxisSpace.Local;

    [Tooltip("Flip joystick-forward sign if the object's working axis was authored the 'wrong' way. Different concern from StandOnOtherSide (which compensates for operator viewpoint).")]
    public bool InvertTranslation = false;

    // Per-axis rotation inversions. Use these when the object's local axes are
    // authored such that one of the phone-tilt directions feels reversed. Each
    // flag negates the matching quaternion component of the live phone reading
    // — applied both at calibration and per-frame, so the object stays put at
    // zero and only the deltas after that flip on the chosen axis. Procedure:
    //   1. Connect a phone with TargetName equal to this controller's TargetName.
    //   2. Toggle the flag for the axis that feels wrong.
    //   3. Press the phone's "zero" button to re-anchor the calibration.
    //   4. Re-test that single motion (pitch / yaw / roll) on the phone.
    [Header("Rotation Inversions")]
    [FormerlySerializedAs("InvertNeedlePitch")]
    [Tooltip("Invert phone-pitch direction (negates the X component of the phone quaternion).")]
    public bool InvertPitch = false;
    [FormerlySerializedAs("InvertNeedleYaw")]
    [Tooltip("Invert phone-yaw direction (negates the Y component of the phone quaternion).")]
    public bool InvertYaw = false;
    [FormerlySerializedAs("InvertNeedleRoll")]
    [Tooltip("Invert phone-roll direction (negates the Z component of the phone quaternion).")]
    public bool InvertRoll = false;

    // ─────────────────────────────────────────────────────────────────────────
    //  BEHAVIOR TOGGLES
    // ─────────────────────────────────────────────────────────────────────────
    [Header("Behavior")]
    public bool ApplyRotation = true;
    public bool ApplyTranslation = true;

    // Joystick travel speed. Scene units are mm, so 5 = 5 mm/s at full
    // deflection. The lift buttons use a separate speed (LiftSpeed).
    public float TranslationSpeed = 5f;

    // Deadman timer on the phone's input stream. The controller writes the
    // object's rotation EVERY frame from the last reading it received, so a
    // stream that stops without a clean handoff would otherwise pin the object
    // at a frozen pose forever — and fight any other system (an LLM commander,
    // a scripted animation) that tries to move the same object. Releasing on
    // silence means "no phone input" reliably degrades to "no phone control".
    //
    // Set generously relative to the phone's send rate (~60 Hz): this is a
    // failsafe for closed tabs, backgrounded browsers, and dropped sockets,
    // not a per-frame gate. Set to 0 or negative to disable (not recommended
    // when another system shares control of the same object).
    [Tooltip("Seconds without an orientation message before this controller releases the object so another system can drive it. Covers closed phone tabs and dropped connections. 0 or negative disables the timeout.")]
    public float InputTimeoutSeconds = 1f;

    // ─────────────────────────────────────────────────────────────────────────
    //  VIO TRANSLATION (alternate translation source)
    // ─────────────────────────────────────────────────────────────────────────
    // The phone can also drive translation directly from its physical position
    // in space. Under the hood the pose source is a visual-inertial SLAM
    // (VI-SLAM) engine — 8th Wall's WASM binary by default — but we consume
    // ONLY its per-frame 6-DoF position output. Its mapping, relocalization,
    // and recenter features are not invoked. Behaviorally, our integration
    // is a visual-inertial odometry (VIO) consumer of a VI-SLAM library, so
    // we call the mode "VIO" throughout the code.
    //
    // Rotation is unchanged — it always comes from DeviceOrientationEvent on
    // the phone, never from the engine's quaternion. VI-SLAM-derived rotation
    // has failure modes (sudden jumps on feature re-localization, drift when
    // the camera sees a featureless surface) that pure gyroscope doesn't have.
    public enum TranslationSource { Joystick, VIO }

    [Header("VIO Translation")]
    [Tooltip("Where translation comes from. Joystick = the 2D thumbstick + lift buttons on the phone (default, existing behavior). VIO = the phone's physical position in space, measured by the in-browser visual-inertial tracking engine. The phone can toggle this at runtime via the Source button.")]
    public TranslationSource Translation = TranslationSource.Joystick;

    [UnityEngine.Serialization.FormerlySerializedAs("VSLAMScale")]
    [Tooltip("Scale factor applied to VIO position deltas: scene-units per phone-meter. Probe scenes are typically mm-scale, so 1000 maps 1 m of phone motion to 1000 mm of object motion at 1:1 scale. Adjust per scene.")]
    public float VIOScale = 1000f;

    // VIO translation routes each phone-motion axis onto the SAME object axis
    // the joystick uses (ForwardAxis / StrafeAxis / MarkerOutwardAxis), so the
    // two sources agree on "forward", "strafe", and "lift". These flags fix the
    // SIGN of each phone axis if a motion feels reversed.
    [UnityEngine.Serialization.FormerlySerializedAs("InvertVSLAMX")]
    [Tooltip("Invert the phone's X axis (left/right) — drives the StrafeAxis in VIO mode. Toggle if moving the phone right moves the object left.")]
    public bool InvertVIOX = false;
    [UnityEngine.Serialization.FormerlySerializedAs("InvertVSLAMY")]
    [Tooltip("Invert the phone's Y axis (up/down, gravity-aligned) — drives the MarkerOutwardAxis (lift) in VIO mode. Toggle if moving the phone up presses the object down.")]
    public bool InvertVIOY = false;
    [UnityEngine.Serialization.FormerlySerializedAs("InvertVSLAMZ")]
    [Tooltip("Invert the phone's Z axis (forward/back) — drives the ForwardAxis in VIO mode. Toggle if moving the phone forward moves the object backward.")]
    public bool InvertVIOZ = false;

    [UnityEngine.Serialization.FormerlySerializedAs("MaxVSLAMRange")]
    [Tooltip("Max travel (in scene units) along each control axis — forward, strafe, and lift — from the VIO origin. The object can never move more than this far in any one direction from where Align Phone to Obj last anchored it. Set to 0 or negative to disable the clamp (unlimited range).")]
    public float MaxVIORange = 200f;

    [UnityEngine.Serialization.FormerlySerializedAs("VSLAMSmoothing")]
    [Tooltip("Low-pass smoothing on the raw VIO position to suppress at-rest jitter. 0 = no smoothing (raw signal, jittery). 0.5 = balanced (default). Closer to 1 = very smooth but laggy. Capped below 1 so the filter always advances.")]
    [Range(0f, 0.95f)]
    public float VIOSmoothing = 0.5f;

    [Tooltip("When the phone rotates faster than this (deg/sec), VIO translation is held so 'rocking' the phone to angle the object doesn't also move it (lever-arm / parallax suppression — the camera sensor swings on an arc when you rotate the phone). The object resumes following the phone cleanly once rotation slows. Set to 0 or negative to disable.")]
    public float VIORockSuppressionDegPerSec = 60f;

    [Tooltip("If tracking recovers from limited/lost, or the pose stream pauses longer than this (sec), the VIO origin re-anchors so the object resumes from where it was frozen instead of teleporting to wherever the engine's position drifted during the outage. 0.5 is a safe default.")]
    public float VIOReanchorGapSeconds = 0.5f;

    // ─────────────────────────────────────────────────────────────────────────
    //  OPERATOR VIEWPOINT COMPENSATION
    // ─────────────────────────────────────────────────────────────────────────
    // The scene's camera and initial object orientation are fixed by the
    // procedure and cannot be moved. If the developer is viewing the object
    // from the "opposite side" — where tilting and rocking feel reversed
    // even though they're mathematically correct — this flag flips them
    // (but preserves rotating/yaw) so the controls feel natural again.
    //
    // The mirror is composed in two places (see Calibrate() and Update()).
    // The math is documented in CoordinateSystems.md, step 3.
    //
    // IMPORTANT: re-zero the phone after toggling. The auto-OnValidate hook
    // below covers the Inspector case in Play mode; if you set this from
    // code, call Calibrate() yourself.
    [Header("Your Perspective (which side of the object you are standing)")]
    [Tooltip("Mirror tilt and rock (but not twist) so the controls feel correct when the operator is viewing the object from the opposite side. Twist/yaw is preserved.")]
    public bool StandOnOtherSide = false;

    // ─────────────────────────────────────────────────────────────────────────
    //  SKIN FOLLOWING
    // ─────────────────────────────────────────────────────────────────────────
    // When SlideAlongSkin is on, joystick translation projects onto the skin's
    // tangent plane and the object sticks to the surface as it moves. When off,
    // joystick translation is straight-line along the object's local axes.
    [Header("Skin Following")]
    public bool SlideAlongSkin = true;
    public LayerMask SkinLayer;

    // Used to size the raycast search range in FindSkin. The actual range
    // used is max(SurfaceLiftDistance * 20, 100) mm. Generous on purpose:
    // we want the raycast to find the skin even if the object is well above
    // or slightly below the surface between frames.
    public float SurfaceLiftDistance = 5f;

    [Tooltip("Speed (units/sec) the lift buttons translate the object along MarkerOutwardAxis.")]
    public float LiftSpeed = 0.05f;

    // Names for the six possible axis directions, used by the enum fields
    // above. Combined with an AxisSpace selector, ResolveAxis() converts them
    // into world-space vectors either through the transform's basis (Local)
    // or as fixed global vectors (World).
    public enum ProbeAxis { Up, Down, Forward, Back, Right, Left }

    // Whether an axis enum is interpreted in the controlled object's local
    // frame (rotates with it) or in world space (constant regardless of
    // object orientation). Per-axis: Forward, Strafe, and MarkerOutward each
    // have their own AxisSpace field so mixed configurations are possible
    // (e.g. forward in world, strafe in local).
    public enum AxisSpace { Local, World }

    [Header("Debug")]
    public bool VerboseLogs = false;

    // ─────────────────────────────────────────────────────────────────────────
    //  WIRE FORMAT
    // ─────────────────────────────────────────────────────────────────────────
    // Messages from the phone are JSON objects with a "type" discriminator.
    // Every message also carries a `target` field naming which controlled
    // object the phone is currently driving. Only the relevant fields per
    // type are populated; the others stay at their default 0/null/"".
    // JsonUtility tolerates missing fields silently.
    //
    // Types:
    //   "orientation"        uses alpha, beta, gamma         (+ target)
    //   "zero"               uses no extra fields            (+ target)
    //   "translate"          uses value and x                (+ target)
    //   "lift"               uses value                      (+ target)
    //   "target"             uses target (the phone announces its current selection)
    //   "pose-translate"     uses pX, pY, pZ, tracking       (+ target)  — VIO
    //   "pose-zero"          uses no extra fields            (+ target)  — VIO origin reset
    //   "translation-source" uses source ("joystick"|"vio")  (+ target)  — phone's source toggle
    [Serializable]
    class Msg
    {
        public string type;
        public float alpha;
        public float beta;
        public float gamma;
        public float value;    // "translate" forward axis (joystick Y) or "lift" magnitude
        public float x;        // "translate" lateral axis (joystick X)
        public string target;  // routing tag: phone's currently selected object
        // VIO-specific fields. Populated only by pose-* and translation-source
        // messages; default to 0 / null on other messages, which is fine because
        // those other branches don't read them.
        public float pX;       // "pose-translate" phone position X (meters)
        public float pY;       // "pose-translate" phone position Y (meters)
        public float pZ;       // "pose-translate" phone position Z (meters)
        public string tracking;// "pose-translate" tracking quality: "normal" | "limited" | "lost"
        public string source;  // "translation-source" body: "joystick" | "vio"
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  RUNTIME STATE
    // ─────────────────────────────────────────────────────────────────────────
    // Latest phone orientation, already converted from W3C euler angles
    // (alpha/beta/gamma) into a Unity quaternion via PhoneEulerToUnityQuat.
    // Written only in OnMessage on receipt of an "orientation" message.
    Quaternion latestPhoneRotation = Quaternion.identity;

    // The constant change-of-basis from phone frame to object frame, computed
    // once at calibration and reused every frame. With M = optional mirror:
    //     calibrationOffset = R₀ · M · P₀⁻¹
    // Every frame we then compute:
    //     newRot = calibrationOffset · latestPhoneRotation
    //            = R₀ · M · P₀⁻¹ · P
    // See CoordinateSystems.md step 2 for the derivation.
    Quaternion calibrationOffset = Quaternion.identity;

    // The object's world rotation at the moment of calibration ("R₀" in the
    // math). Re-captured whenever a matching target message arrives, the
    // click-to-place script jumps the object, or any Inspector value changes
    // in Play mode.
    Quaternion referenceRotation = Quaternion.identity;

    // The object's world position at the same moments referenceRotation is
    // captured. The phone's "Align Phone to Obj" / Zero button restores the
    // controlled object back to this position on pose-zero, in addition to
    // re-calibrating rotation. Default is whatever Awake() saw, which is the
    // scene-authored position. Updated by CaptureReferenceFromTarget() and
    // by SetReferenceToCurrent() so click-to-place flows update the home.
    Vector3 initialWorldPosition = Vector3.zero;

    // hasReading flips true once we've received the first orientation message;
    // hasReference flips true once we've snapshotted the target's pose. Both
    // must be true before we can produce meaningful output.
    //
    // hasReading doubles as the "this controller currently owns its object"
    // flag: Update() only writes the transform while it's true, and
    // ReleaseControl() clears it. Clearing rather than latching is what lets
    // another system take the object over — and it makes re-acquisition
    // self-correcting, because the next orientation message runs back through
    // the !hasReading branch below and recalibrates against wherever that
    // other system left the object.
    bool hasReading = false;
    bool hasReference = false;

    // Whether the phone's object picker currently points at this controller.
    // The picker's standing default is "Any / all objects", which the phone
    // sends as an empty target and every controller accepts — so the honest
    // initial value is true, matching that broadcast default.
    bool isSelected = true;

    // Latest joystick and lift values. All clamped to [-1, 1] on receipt so
    // downstream math doesn't have to defend against bad input.
    float latestSliderValue = 0f;   // joystick forward axis (sweeping motion along skin)
    float latestSliderStrafe = 0f;  // joystick lateral axis (sliding motion along skin)
    float latestLiftValue = 0f;     // lift buttons (+1 = away, -1 = toward) along MarkerOutwardAxis

    // VIO-translation state. Populated when Translation == VIO and the
    // phone is sending pose-translate messages.
    Vector3 vioCurrentPosition;   // latest phone position in the engine's world frame, meters
    Vector3 vioOriginPosition;    // phone position at the most recent pose-zero (or first reading)
    Vector3 vioOriginWorldPos;    // TargetTransform.position captured at the same moment
    bool hasVioReading = false;   // true after the first pose-translate message
    bool vioTrackingOk = true;    // false when tracking == "limited" or "lost"
    float lastPoseTime = -1f;     // Time.time of the last pose-translate (for gap detection)

    // Rock-suppression state. phoneAngularSpeedDegPerSec is an EMA of the
    // gyro's frame-to-frame rotation magnitude, computed in the "orientation"
    // branch. vioRockClutchEngaged holds the hysteresis state so the clutch
    // doesn't chatter when angular speed hovers near the threshold.
    Quaternion prevPhoneRotationForVel = Quaternion.identity;
    float lastOrientationTime = -1f;
    bool hasPrevPhoneRotation = false;
    float phoneAngularSpeedDegPerSec = 0f;
    bool vioRockClutchEngaged = false;

    // ─────────────────────────────────────────────────────────────────────────
    //  LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────
    // Awake runs once before any message arrives, regardless of whether the
    // controller is enabled. We capture the scene-authored position here so
    // pose-zero has somewhere sensible to return the object to if it fires
    // before the first orientation message has triggered a reference capture.
    void Awake()
    {
        if (TargetTransform != null) initialWorldPosition = TargetTransform.position;
    }

    // Subscribe to the shared relay on Enable; unsubscribe on Disable. Using
    // OnEnable/OnDisable rather than Start/OnDestroy means hot-reloading the
    // host GameObject's active state cleanly hooks/unhooks the controller
    // without a duplicate subscription.
    void OnEnable()
    {
        if (Relay == null) Relay = GetComponent<PhoneRelayClient>();
        if (Relay != null) Relay.MessageReceived += OnMessage;
        else Debug.LogError($"[PhoneGyro:{TargetName}] No PhoneRelayClient found on this GameObject. Either add one or assign Relay manually.");
    }

    void OnDisable()
    {
        if (Relay != null) Relay.MessageReceived -= OnMessage;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MESSAGE HANDLING
    // ─────────────────────────────────────────────────────────────────────────
    // Dispatches incoming WebSocket messages to the appropriate state update.
    // Messages are JSON with a "type" field; unknown types are silently
    // ignored, as are messages that fail to parse. This is intentional —
    // forward-compatibility with future message types is more important than
    // strict validation.
    //
    // Routing: any message whose `target` field is non-empty and does NOT
    // equal TargetName is silently dropped before any state mutation. This is
    // what lets multiple PhoneGyroController components live in the same
    // scene without interfering with each other.
    void OnMessage(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        if (VerboseLogs) Debug.Log($"[PhoneGyro:{TargetName}] msg: {text}");

        Msg m;
        try { m = JsonUtility.FromJson<Msg>(text); }
        catch { return; }
        if (m == null || string.IsNullOrEmpty(m.type)) return;

        // Selection changes are handled BEFORE the target filter below,
        // because a "target" message naming a DIFFERENT object is precisely
        // how this controller learns it has been deselected. Running it
        // through the filter would drop it — and the controller would go on
        // driving its object from a frozen reading forever, fighting whoever
        // the phone moved on to and any other system sharing the object.
        if (m.type == "target")
        {
            // Empty target is the picker's "Any / all objects" mode, which
            // selects every controller at once.
            bool nowSelected = string.IsNullOrEmpty(m.target) || m.target == TargetName;

            if (!nowSelected)
            {
                isSelected = false;
                ReleaseControl();
                return;
            }

            // Selected: re-capture the object's current pose as the new
            // reference and recalibrate, so the phone's current orientation
            // corresponds to the object's current rotation — no snap. If we
            // were released, hasReading stays false until the next
            // orientation message, which recalibrates again against a live
            // reading rather than the stale one we're holding here.
            isSelected = true;
            CaptureReferenceFromTarget();
            Calibrate();
            Debug.Log($"[PhoneGyro:{TargetName}] phone selected this controller; recalibrated.");
            return;
        }

        // Filter by target tag. An empty target is accepted by every
        // controller (legacy clients, broadcast messages).
        if (!string.IsNullOrEmpty(m.target) && m.target != TargetName) return;

        // Anything reaching this point is addressed to us — either by name, or
        // by the picker's empty-target broadcast ("Any / all objects"). Both
        // mean this controller is selected again. This is what revives a
        // controller that was released earlier when the phone page reloads:
        // it comes back in broadcast mode and never re-sends a "target"
        // message, so re-selection has to be inferred from being addressed.
        isSelected = true;

        if (m.type == "orientation")
        {
            // Convert the W3C euler triple into a Unity-frame quaternion.
            // See PhoneEulerToUnityQuat for the exact mapping and why it
            // isn't the textbook conversion (calibration absorbs the slop).
            latestPhoneRotation = PhoneEulerToUnityQuat(m.alpha, m.beta, m.gamma);

            // Estimate the phone's angular speed from the change in orientation
            // since the previous message. Used by the VIO rock-suppression
            // clutch (see ApplyVIOTranslation): when you rock the phone fast,
            // the camera sensor swings on an arc and VIO reports it as
            // translation; gating on angular speed lets us hold translation
            // during that. Quaternion.Angle works in quaternion space, so it's
            // immune to the alpha/beta/gamma euler wrap.
            if (hasPrevPhoneRotation)
            {
                float dt = Time.time - lastOrientationTime;
                if (dt > 1e-4f)
                {
                    float instSpeed = Quaternion.Angle(prevPhoneRotationForVel, latestPhoneRotation) / dt;
                    // Light EMA so one stuttered/batched message doesn't spike
                    // the clutch on or off.
                    phoneAngularSpeedDegPerSec = Mathf.Lerp(phoneAngularSpeedDegPerSec, instSpeed, 0.5f);
                }
            }
            prevPhoneRotationForVel = latestPhoneRotation;
            lastOrientationTime = Time.time;
            hasPrevPhoneRotation = true;

            // First reading after (re)connection: snapshot the object's
            // current pose and calibrate so the phone's current orientation
            // corresponds to wherever the object is right now. Without this,
            // the object would snap to a random orientation on connection.
            if (!hasReading)
            {
                hasReading = true;
                CaptureReferenceFromTarget();
                Calibrate();
            }
        }
        else if (m.type == "zero")
        {
            // User pressed the "zero" button on the phone. Re-calibrate so
            // the current phone pose maps to the current object pose. The
            // object doesn't move; only the calibration offset changes.
            Calibrate();
        }
        else if (m.type == "translate")
        {
            // Joystick state. value = Y axis (sweeping), x = X axis (sliding).
            // Older phone-side clients may omit "x" — JsonUtility defaults it
            // to 0, which correctly produces no lateral motion.
            latestSliderValue = Mathf.Clamp(m.value, -1f, 1f);
            latestSliderStrafe = Mathf.Clamp(m.x, -1f, 1f);
        }
        else if (m.type == "lift")
        {
            // Lift buttons — push object into / pull away from skin.
            latestLiftValue = Mathf.Clamp(m.value, -1f, 1f);
        }
        else if (m.type == "pose-translate")
        {
            // VIO position update. The phone sends these whenever the user is
            // in VIO-translation mode and the engine is producing a pose. We
            // always update vioCurrentPosition (so a Set Origin tap captures
            // the latest reading) but only let it MOVE the object when
            // tracking is healthy — that's the freeze-on-tracking-loss policy.
            // A missing/empty "tracking" field is treated as healthy for
            // forward-compatibility with any older phone client that doesn't
            // populate it.
            Vector3 raw = new Vector3(m.pX, m.pY, m.pZ);
            bool nowOk = string.IsNullOrEmpty(m.tracking) || m.tracking == "normal";

            // Re-anchor (assign verbatim + capture a fresh origin) instead of
            // smoothing the delta in, whenever stream continuity broke:
            //  - first reading ever, or after a Joystick→VIO source switch
            //    (hasVioReading is reset there) — anchor to the phone's CURRENT
            //    position, not a stale one;
            //  - tracking just recovered from limited/lost — the engine's
            //    reported position drifts/jumps during an outage, so without
            //    re-anchoring the accumulated delta would teleport the object
            //    on the first good frame;
            //  - a long gap in the pose stream (phone went silent during a
            //    'lost' spell, or the engine/network stalled).
            float now = Time.time;
            bool recovering = nowOk && !vioTrackingOk;
            bool gap = hasVioReading && (now - lastPoseTime) > VIOReanchorGapSeconds;

            if (!hasVioReading || recovering || gap)
            {
                vioCurrentPosition = raw;
                hasVioReading = true;
                CapturePoseOrigin();   // object holds where it is; new zero is here
            }
            else
            {
                // Exponential moving average: smoothed = lerp(prev, raw, alpha)
                // where alpha = 1 - smoothing. VIOSmoothing = 0 → alpha = 1
                // → straight passthrough; VIOSmoothing → 1 → alpha → 0 →
                // very slow convergence. Range is capped at 0.95 in the
                // Inspector so alpha is always > 0 and the filter advances.
                float alpha = 1f - VIOSmoothing;
                vioCurrentPosition = Vector3.Lerp(vioCurrentPosition, raw, alpha);
            }
            lastPoseTime = now;
            vioTrackingOk = nowOk;
        }
        else if (m.type == "pose-zero")
        {
            // User tapped Set Origin (or the unified Zero button, which sends
            // both "zero" and "pose-zero").
            // 1. Restore the controlled object to its initial position. This
            //    is what makes Align Phone to Obj a true "go home" button.
            //    Works in joystick mode too — the object snaps back to its
            //    starting position regardless of how it got moved.
            // 2. Then capture the current phone position as the VIO origin
            //    so subsequent VIO deltas anchor at this restored home,
            //    not at wherever the object happened to be before reset.
            if (TargetTransform != null) TargetTransform.position = initialWorldPosition;
            CapturePoseOrigin();
        }
        else if (m.type == "translation-source")
        {
            // The phone's UI toggled between joystick and VIO. Update our
            // runtime mode so Update() reads from the right source next frame.
            if (m.source == "joystick")
            {
                Translation = TranslationSource.Joystick;
                Debug.Log($"[PhoneGyro:{TargetName}] translation source: joystick.");
            }
            else if (m.source == "vio")
            {
                Translation = TranslationSource.VIO;
                // Force re-anchor on the next pose-translate. Setting
                // hasVioReading = false makes the next incoming pose go
                // through the "first reading" branch above, which assigns
                // vioCurrentPosition verbatim from the raw value AND calls
                // CapturePoseOrigin() — so the origin is anchored to the
                // phone's CURRENT physical position, not whatever stale
                // value vioCurrentPosition held from the end of the
                // previous VIO session.
                hasVioReading = false;
                Debug.Log($"[PhoneGyro:{TargetName}] translation source: vio.");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  RELEASING THE OBJECT
    // ─────────────────────────────────────────────────────────────────────────
    // Stop driving the controlled object and drop every piece of state that
    // could keep moving it on its own. Called when the phone selects a
    // different object, when the relay drops, and when the input stream goes
    // silent past InputTimeoutSeconds.
    //
    // Each field cleared here is a way a released controller could otherwise
    // keep writing the transform from stale input:
    //   hasReading            gates the per-frame rotation write, which
    //                         reapplies the last phone reading every frame.
    //   slider / lift values  a joystick held at the moment the connection
    //                         drops would translate the object forever.
    //   hasVioReading         same for VIO position deltas.
    //
    // The object is left exactly where it is — releasing hands over control,
    // it doesn't restore a pose. Re-acquisition recalibrates against the
    // object's pose at that time, so whatever moved it in the meantime is
    // respected rather than undone.
    void ReleaseControl()
    {
        if (!hasReading && !hasVioReading) return;

        hasReading = false;
        hasPrevPhoneRotation = false;
        phoneAngularSpeedDegPerSec = 0f;

        latestSliderValue = 0f;
        latestSliderStrafe = 0f;
        latestLiftValue = 0f;

        hasVioReading = false;
        vioRockClutchEngaged = false;

        Debug.Log($"[PhoneGyro:{TargetName}] released control; another system may now drive this object.");
    }

    // Whether the phone's input is live enough to keep driving the object.
    // Requires an open socket and, unless the timeout is disabled, an
    // orientation message within InputTimeoutSeconds.
    bool HasLiveInput()
    {
        if (Relay == null || !Relay.IsConnected) return false;
        if (InputTimeoutSeconds <= 0f) return true;
        if (lastOrientationTime < 0f) return false;
        return (Time.time - lastOrientationTime) <= InputTimeoutSeconds;
    }

    // Snapshot the phone's current VIO-space position AND the controlled
    // object's current Unity-world position. Every subsequent pose-translate
    // message produces a delta from vioOriginPosition that we apply on top
    // of vioOriginWorldPos — so the object stays exactly where it is the
    // moment the user taps Zero/Set Origin, and only deltas after that point
    // move it.
    void CapturePoseOrigin()
    {
        vioOriginPosition = vioCurrentPosition;
        if (TargetTransform != null) vioOriginWorldPos = TargetTransform.position;
    }

    // Snapshot the current world rotation AND position of TargetTransform as
    // the calibration reference. Rotation is the gyro calibration anchor;
    // position is the home that Align Phone to Obj returns the object to.
    // Called on the first orientation message, on every matching `target`
    // message, and from the external SetReferenceToCurrent() hook.
    void CaptureReferenceFromTarget()
    {
        if (TargetTransform != null)
        {
            referenceRotation = TargetTransform.rotation;
            initialWorldPosition = TargetTransform.position;
            hasReference = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PHONE → UNITY ORIENTATION CONVERSION
    // ─────────────────────────────────────────────────────────────────────────
    // Converts the W3C DeviceOrientationEvent angles (alpha, beta, gamma)
    // into a Unity quaternion. The W3C spec defines an intrinsic Z-X'-Y''
    // Tait-Bryan rotation in a right-handed frame; Unity is left-handed.
    //
    // The three negations flip handedness. The axis order (world Y, X, Z
    // applied left-to-right here) is NOT the textbook conversion — it's a
    // practical mapping that lands the phone's orientation in a usable place.
    // The calibration step downstream absorbs any residual frame offset, so
    // we don't need spec-perfect alignment here.
    //
    // If the controls ever feel symmetrically reversed across all three
    // axes (not asymmetric), this is the function to revisit. See the
    // debugging checklist in CoordinateSystems.md.
    static Quaternion PhoneEulerToUnityQuat(float alpha, float beta, float gamma)
    {
        return Quaternion.AngleAxis(-alpha, Vector3.up)
             * Quaternion.AngleAxis(-beta, Vector3.right)
             * Quaternion.AngleAxis(-gamma, Vector3.forward);
    }

    // Applies the per-controller axis-inversion flags to the live phone
    // quaternion. Each InvertX flag negates one component of the quaternion's
    // vector part, flipping rotation around that single axis while leaving
    // the other two axes unchanged.
    //
    // The flips must be applied at BOTH calibration time and per-frame time;
    // because Flip and Inverse commute on unit quaternions, the calibration
    // anchor is preserved (the object stays put at zero) and only the
    // post-zero deltas pick up the inverted axis.
    Quaternion AdjustPhoneRotation(Quaternion q)
    {
        if (InvertPitch) q = new Quaternion(-q.x, q.y, q.z, q.w);
        if (InvertYaw)   q = new Quaternion(q.x, -q.y, q.z, q.w);
        if (InvertRoll)  q = new Quaternion(q.x, q.y, -q.z, q.w);
        return q;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  CALIBRATION
    // ─────────────────────────────────────────────────────────────────────────
    // Computes calibrationOffset = R₀ · M · P₀⁻¹ where:
    //   R₀ = object's current world rotation (referenceRotation)
    //   P₀ = phone's current world rotation (latestPhoneRotation, adjusted)
    //   M  = optional 180° rotation about world-back, used when the operator
    //        is viewing from the opposite side. Identity when
    //        StandOnOtherSide is false.
    //
    // Every frame the controller computes newRot = calibrationOffset · P,
    // which expands to R₀ · M · P₀⁻¹ · P. At the moment of calibration
    // (P = P₀) this simplifies to R₀ · M, meaning the object sits at its
    // reference pose (modulo mirror). When the phone rotates by Δ in its
    // own frame (P = P₀ · Δ), the object rotates by Δ in its own frame.
    //
    // This is the right-multiplication form. An earlier left-multiplication
    // version (P · P₀⁻¹ · R₀) applied phone deltas in the WORLD frame
    // instead of the object's frame; it worked only when R₀ = identity and
    // produced axis swaps at 90° / axis blending at 45° otherwise.
    public void Calibrate()
    {
        if (!hasReference) return;

        // Mirror is the first half of the StandOnOtherSide compensation;
        // the second half is the local-Z flip applied in Update(). The two
        // flips compose to identity at our current reference pose, so the
        // object's sidedness ridge stays oriented correctly at zero — but
        // the phone delta still feels mirrored when the phone moves.
        Quaternion mirror = StandOnOtherSide
            ? Quaternion.AngleAxis(180f, Vector3.back)
            : Quaternion.identity;

        // R₀ · mirror · P₀⁻¹. The phone reading is run through AdjustPhoneRotation
        // so axis-flip flags are baked into the calibration anchor as well as
        // the per-frame math below.
        Quaternion adjustedP0 = AdjustPhoneRotation(latestPhoneRotation);
        calibrationOffset = referenceRotation * mirror * Quaternion.Inverse(adjustedP0);
        Debug.Log($"[PhoneGyro:{TargetName}] Calibrated to reference pose. StandOnOtherSide={StandOnOtherSide}");
    }

    // External hook: after a click-to-place script reorients the object to a
    // new skin normal, it should call this so the gyro's "home" pose matches
    // the new orientation instead of the old one. Without this call, the
    // next phone movement would yank the object back toward the old pose.
    public void SetReferenceToCurrent()
    {
        if (TargetTransform == null) return;
        referenceRotation = TargetTransform.rotation;
        initialWorldPosition = TargetTransform.position;
        hasReference = true;
        Calibrate();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PER-FRAME UPDATE
    // ─────────────────────────────────────────────────────────────────────────
    void Update()
    {
        // WebSocket message dispatch happens on PhoneRelayClient.Update,
        // which runs first via [DefaultExecutionOrder(-100)]. Subscribers'
        // OnMessage handlers fire synchronously during that dispatch, so by
        // the time we get here this frame's state is already up to date.

        Transform target = TargetTransform;
        Transform pivot = RotationPivot;
        if (target == null) return;

        // Deadman check. A clean deselect arrives as a "target" message, but
        // closed tabs, backgrounded browsers, and dropped sockets produce no
        // message at all — so silence has to release the object too. Without
        // this, "I closed the phone page" would leave the object pinned to
        // its last reading for the rest of the session.
        if (hasReading && !HasLiveInput()) ReleaseControl();

        // ── Rotation ────────────────────────────────────────────────────
        if (ApplyRotation && hasReading)
        {
            // OLD (incorrect) form, kept here as a breadcrumb so the next
            // person doesn't try to "simplify" the math back to wrong:
            //   Quaternion newRot = latestPhoneRotation * calibrationOffset;
            //
            // The current form right-multiplies the live phone reading onto
            // the stored offset, putting the phone's delta in the object's
            // local frame at any initial object rotation. See Calibrate().
            // The phone reading is first run through AdjustPhoneRotation so
            // axis-flip flags take effect.
            Quaternion adjustedPhone = AdjustPhoneRotation(latestPhoneRotation);
            Quaternion newRot = calibrationOffset * adjustedPhone;

            if (pivot != null)
            {
                // Rotate around the pivot's world position so the contact
                // point stays planted on the skin while the rest of the
                // object swings around it. Standard "rotate around arbitrary
                // pivot" formula: shift, rotate, shift back.
                Vector3 p = pivot.position;
                Quaternion delta = newRot * Quaternion.Inverse(target.rotation);
                target.position = p + delta * (target.position - p);
                target.rotation = newRot;

                // Second half of the StandOnOtherSide compensation: an
                // additional 180° about the object's local Z. At our current
                // reference pose this composes with the world-back mirror
                // in Calibrate() to identity, so the sidedness ridge stays
                // oriented correctly at zero — only the phone delta feels
                // mirrored.
                if (StandOnOtherSide) target.Rotate(0, 0, 180, Space.Self);
            }
            else
            {
                // No pivot assigned: rotate around the object's own origin.
                // Fine for objects whose origin already sits at the contact
                // point, but most authored probes have their origin in the
                // housing, so the pivot is normally non-null.
                target.rotation = newRot;
            }
        }

        // ── Translation ────────────────────────────────────────────────
        // Branch on the current translation source. The two paths are
        // intentionally mutually exclusive: in VIO mode we ignore the
        // joystick/lift inputs entirely so they don't double up with the
        // phone's physical motion. The joystick path is the existing
        // pre-VIO behavior, unchanged.
        if (ApplyTranslation)
        {
            if (Translation == TranslationSource.VIO)
            {
                ApplyVIOTranslation(target);
            }
            else
            {
                ApplyJoystickTranslation(target);
            }
        }
    }

    // VIO translation: set the object's world position to the VIO origin
    // anchor plus a scaled, optionally-per-axis-inverted phone delta. When
    // tracking is unhealthy (limited / lost), don't update position at all —
    // the object freezes at whatever it was last frame. The phone UI shows
    // a red/yellow status pill in that case so the user knows why.
    void ApplyVIOTranslation(Transform target)
    {
        if (!hasVioReading || !vioTrackingOk) return;

        // Rock-suppression clutch. When the phone is rotating quickly, the
        // VIO position change is dominated by the camera sensor swinging on
        // an arc (lever-arm/parallax), not by intentional sliding — so hold
        // translation. Hysteresis (engage at the threshold, release at half)
        // stops the clutch chattering when angular speed hovers near it.
        // While engaged we re-anchor the origin every frame via
        // CapturePoseOrigin(), which sets vioOriginPosition to the current
        // phone position and vioOriginWorldPos to the probe's current (held)
        // position — so the parasitic motion is absorbed into the origin and
        // translation resumes from wherever the phone ends up, with no jump.
        if (VIORockSuppressionDegPerSec > 0f)
        {
            float engage  = VIORockSuppressionDegPerSec;
            float release = VIORockSuppressionDegPerSec * 0.5f;
            if (!vioRockClutchEngaged && phoneAngularSpeedDegPerSec >= engage) vioRockClutchEngaged = true;
            else if (vioRockClutchEngaged && phoneAngularSpeedDegPerSec <= release) vioRockClutchEngaged = false;

            if (vioRockClutchEngaged)
            {
                CapturePoseOrigin();
                return;
            }
        }

        // Phone-frame position delta since the origin, in the engine's
        // gravity-aligned world frame (Y up; X/Z horizontal, fixed at the
        // moment of origin capture). The per-axis invert flags fix the sign
        // of each phone axis if a motion feels reversed.
        Vector3 delta = vioCurrentPosition - vioOriginPosition;
        if (InvertVIOX) delta.x = -delta.x;
        if (InvertVIOY) delta.y = -delta.y;
        if (InvertVIOZ) delta.z = -delta.z;

        // Decompose the phone motion into forward / strafe / lift amounts.
        // Convention: phone Z = forward/back, phone X = left/right, phone Y =
        // up/down. If forward and strafe feel swapped on a given device, the
        // engine assigned its horizontal axes the other way — that needs a
        // forward/strafe swap option (not yet exposed); for now the invert
        // flags handle pure sign reversals.
        float forwardAmt = delta.z * VIOScale;
        float strafeAmt  = delta.x * VIOScale;
        float liftAmt    = delta.y * VIOScale;

        // Per-axis cube clamp, now in CONTROL-axis space: how far the object
        // may travel forward, sideways, and in lift from the origin. This is
        // more meaningful than the old world-axis clamp and keeps the object
        // from wandering off. Non-positive MaxVIORange disables it.
        if (MaxVIORange > 0f)
        {
            forwardAmt = Mathf.Clamp(forwardAmt, -MaxVIORange, MaxVIORange);
            strafeAmt  = Mathf.Clamp(strafeAmt,  -MaxVIORange, MaxVIORange);
            liftAmt    = Mathf.Clamp(liftAmt,    -MaxVIORange, MaxVIORange);
        }

        // Resolve the SAME object axes the joystick/lift path uses, so VIO and
        // joystick agree on every direction. Sharing ForwardAxis / StrafeAxis /
        // MarkerOutwardAxis (+ their AxisSpace), InvertTranslation, and
        // StandOnOtherSide is the whole point of this method — it's why moving
        // the phone forward now matches pushing the joystick forward.
        Vector3 fwd    = ResolveAxis(target, ForwardAxis, ForwardAxisSpace);
        Vector3 strafe = ResolveAxis(target, StrafeAxis, StrafeAxisSpace);
        Transform liftRef = RotationPivot != null ? RotationPivot : target;
        Vector3 lift   = ResolveAxis(liftRef, MarkerOutwardAxis, MarkerOutwardAxisSpace);

        // Same forward-axis definition as the joystick path: InvertTranslation
        // flips the object's "forward", StandOnOtherSide mirrors forward+strafe
        // for an operator on the opposite side.
        if (InvertTranslation) fwd = -fwd;
        if (StandOnOtherSide) { fwd = -fwd; strafe = -strafe; }

        Vector3 worldDelta = fwd * forwardAmt + strafe * strafeAmt + lift * liftAmt;
        target.position = vioOriginWorldPos + worldDelta;
    }

    // Joystick + lift: the original translation path. Kept here as a method
    // so the Update() body stays a clean two-line dispatch.
    void ApplyJoystickTranslation(Transform target)
    {
        // ── Lift (pressing motion) ─────────────────────────────────────
        // Lift buttons translate the object along its MarkerOutwardAxis —
        // i.e., into or away from the skin. No skin raycast is involved;
        // this is a straight translation in the pivot's local frame,
        // applied every frame the button is held.
        if (Mathf.Abs(latestLiftValue) > 0.001f && RotationPivot != null)
        {
            Vector3 liftAxis = ResolveAxis(RotationPivot, MarkerOutwardAxis, MarkerOutwardAxisSpace);
            target.position += liftAxis * (latestLiftValue * LiftSpeed * Time.deltaTime);
        }

        // ── Joystick translation (sliding + sweeping) ──────────────────
        // Two branches: skin-following (uses raycast + tangent projection)
        // or straight-line (uses object-local axes directly).
        bool anyTranslate = Mathf.Abs(latestSliderValue) > 0.001f
                          || Mathf.Abs(latestSliderStrafe) > 0.001f;
        if (!anyTranslate) return;

        if (SlideAlongSkin && RotationPivot != null)
        {
            MoveTangentToSkin(Time.deltaTime);
        }
        else
        {
            // Straight-line translation along the object's local axes —
            // used when there's no skin to follow (testing, needle
            // insertion, etc.) or when SlideAlongSkin is intentionally off.
            float sign = InvertTranslation ? -1f : 1f;
            Vector3 fwd = ResolveAxis(target, ForwardAxis, ForwardAxisSpace);
            Vector3 strafe = ResolveAxis(target, StrafeAxis, StrafeAxisSpace);

            // Operator-side flip for translation: from the other side,
            // joystick "forward" should still push the object away from
            // the operator, and joystick "right" should still be to the
            // operator's right.
            if (StandOnOtherSide)
            {
                fwd = -fwd;
                strafe = -strafe;
            }

            target.position += fwd * (sign * latestSliderValue * TranslationSpeed * Time.deltaTime);
            target.position += strafe * (latestSliderStrafe * TranslationSpeed * Time.deltaTime);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SKIN-FOLLOWING TRANSLATION
    // ─────────────────────────────────────────────────────────────────────────
    // Slides the object along the skin's tangent plane, so it sticks to
    // curved anatomy as the joystick moves it around rather than tunneling
    // through or flying off.
    //
    // Algorithm:
    //   1. Raycast to find the skin point under the rotation pivot and the
    //      local surface normal n.
    //   2. Project the object's forward direction onto the skin's tangent
    //      plane to get a "forward along skin" direction.
    //   3. Compute the lateral tangent as n × forwardTangent.
    //   4. Step in the tangent plane by forward·joyY + lateral·joyX.
    //   5. Re-raycast at the proposed point and snap the object to the
    //      new skin point (via the pivot offset).
    //
    // TODO: this function does not currently honor StandOnOtherSide. The
    // straight-line branch in Update() does, but operator-side flipping
    // is missing here.
    void MoveTangentToSkin(float dt)
    {
        Vector3 outward = ResolveAxis(RotationPivot, MarkerOutwardAxis, MarkerOutwardAxisSpace);
        if (!FindSkin(RotationPivot.position, outward, out RaycastHit here)) return;

        Vector3 n = here.normal;

        Vector3 forward = ResolveAxis(TargetTransform, ForwardAxis, ForwardAxisSpace);
        if (InvertTranslation) forward = -forward;

        // Project the object's forward onto the skin's tangent plane.
        // If forward is parallel to the surface normal (object pointing
        // exactly along the normal), the projection is zero-length and
        // there's no meaningful "forward along skin" direction; bail out
        // rather than divide by near-zero.
        Vector3 forwardTangent = Vector3.ProjectOnPlane(forward, n);
        if (forwardTangent.sqrMagnitude < 1e-6f) return;
        forwardTangent.Normalize();

        // Sideways tangent: in the skin plane, perpendicular to the forward
        // tangent. The cross of the surface normal and the forward tangent
        // gives a unit lateral direction; +x on the joystick steps the
        // object to the operator's right when looking along forward.
        Vector3 lateralTangent = Vector3.Cross(n, forwardTangent).normalized;

        Vector3 step = forwardTangent * (latestSliderValue * TranslationSpeed * dt)
                     + lateralTangent * (latestSliderStrafe * TranslationSpeed * dt);
        if (step.sqrMagnitude < 1e-12f) return;

        // Re-raycast at the proposed point — using the same normal as the
        // starting point — to find where on the skin the pivot should
        // actually land. This is what makes the object hug curved surfaces
        // instead of tunneling through them at high curvature.
        Vector3 proposed = here.point + step;
        if (FindSkin(proposed, n, out RaycastHit next))
        {
            TargetTransform.position += next.point - RotationPivot.position;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SKIN RAYCAST UTILITY
    // ─────────────────────────────────────────────────────────────────────────
    // Finds the skin surface near a given origin by raycasting along
    // ±probeOut, picking the closer hit. The two-direction raycast is
    // defensive: between frames the object may end up slightly inside or
    // slightly outside the skin mesh; one of the two rays will still find
    // a hit even when the other misses.
    //
    // Range is sized generously (max(SurfaceLiftDistance * 20, 100) mm)
    // because the raycasts start far away to ensure the skin is between
    // the origin and the ray's endpoint.
    bool FindSkin(Vector3 origin, Vector3 probeOut, out RaycastHit hit)
    {
        float range = Mathf.Max(SurfaceLiftDistance * 20f, 100f);
        bool foundA = Physics.Raycast(origin + probeOut * range, -probeOut, out RaycastHit a, 2f * range, SkinLayer);
        bool foundB = Physics.Raycast(origin - probeOut * range, probeOut, out RaycastHit b, 2f * range, SkinLayer);

        if (!foundA && !foundB) { hit = default; return false; }
        if (foundA && !foundB) { hit = a; return true; }
        if (!foundA && foundB) { hit = b; return true; }

        // Both hit: prefer the closer one.
        hit = (Vector3.Distance(origin, a.point) <= Vector3.Distance(origin, b.point)) ? a : b;
        return true;
    }

    // Converts a ProbeAxis enum value into a world-space unit vector by
    // looking up the corresponding direction on the transform's basis.
    // This is the indirection that lets the inspector say "ForwardAxis
    // = Down" and have the math do the right thing in world space without
    // anyone editing code.
    static Vector3 ProbeAxisWorld(Transform t, ProbeAxis axis)
    {
        switch (axis)
        {
            case ProbeAxis.Up: return t.up;
            case ProbeAxis.Down: return -t.up;
            case ProbeAxis.Forward: return t.forward;
            case ProbeAxis.Back: return -t.forward;
            case ProbeAxis.Right: return t.right;
            case ProbeAxis.Left: return -t.right;
            default: return t.up;
        }
    }

    // Returns the fixed world-space unit vector for an axis enum. Used when
    // a controller's *AxisSpace field is set to World — translation in that
    // axis stays direction-locked to the scene rather than following the
    // controlled object's rotation.
    static Vector3 WorldAxis(ProbeAxis axis)
    {
        switch (axis)
        {
            case ProbeAxis.Up: return Vector3.up;
            case ProbeAxis.Down: return Vector3.down;
            case ProbeAxis.Forward: return Vector3.forward;
            case ProbeAxis.Back: return Vector3.back;
            case ProbeAxis.Right: return Vector3.right;
            case ProbeAxis.Left: return Vector3.left;
            default: return Vector3.up;
        }
    }

    // One-stop resolver: returns the world-space unit vector for an axis
    // given its space choice. Local means "look up t.up / t.right etc.";
    // World means "use the corresponding global vector".
    static Vector3 ResolveAxis(Transform t, ProbeAxis axis, AxisSpace space)
    {
        return space == AxisSpace.World ? WorldAxis(axis) : ProbeAxisWorld(t, axis);
    }

#if UNITY_EDITOR
    // Re-calibrate whenever any Inspector field changes during Play mode.
    // Mainly useful for toggling StandOnOtherSide or the inversion flags
    // live without having to press "zero" on the phone. The guards ensure
    // we don't try to calibrate in Edit mode (no phone reading available)
    // or before the first orientation message has arrived.
    void OnValidate()
    {
        if (Application.isPlaying && hasReading && hasReference) Calibrate();
    }
#endif
}
