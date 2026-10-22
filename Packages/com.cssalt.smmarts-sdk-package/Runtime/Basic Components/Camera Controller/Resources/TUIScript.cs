using UnityEngine;
using System.Collections;

// SINGLETON

namespace SMMARTS
{
    /// <summary>
    /// Tangible UI controller. Acts as a "viewfinder" for the main camera:
    ///
    ///   - Hold the TUI button: camera snaps to this GameObject's pose every frame (viewfinder mode).
    ///   - Release the button while the TUI is within StickyDistance of FollowParent:
    ///       camera latches onto FollowParent at its current pose and rides along.
    ///   - Release the button while out of range: camera stays frozen where it last was.
    ///   - Press the button while latched: camera un-latches and re-enters viewfinder mode.
    ///   - Double-click the button: restore the camera's startup parent and pose.
    /// </summary>
    public class TUIScript : MonoBehaviour, ITUIProbe
    {
        // ----- Singleton -----
        public static TUIScript ME;

        [Header("----------- Initializations --------------------------------------------------------------")]
        // Parent the camera gets attached to when free (not latched, not in viewfinder).
        public Transform DefaultParent;

        [Header("----------- External Follow Object  ------------------------------------------------------")]
        // Master toggle for the sticky/follow behavior.
        public bool EnableFollowParent;
        // The object the camera should ride along with once latched.
        public GameObject FollowParent;
        // How close (in world units) the TUI must be to FollowParent for a release to commit a latch.
        public float StickyDistance;

        [Header("----------- Double Click Reset -------------------------------------------------------")]
        // Max seconds between two presses to count as a double-click.
        public float doubleClickWindow = 0.5f;

        [Header("----------- Debug (read-only) -------------------------------------------------------")]
        // True when the TUI is currently within StickyDistance of FollowParent. Updated every frame.
        // Only meaningful while EnableFollowParent is on and FollowParent is assigned.
        [SerializeField] private bool _debugInRange;
        // True once the camera is latched onto FollowParent. Cleared on un-latch (next press) or double-click reset.
        [SerializeField] private bool _debugFollowParentLatch;

        // When true, this script is being used as a stylus and should NOT drive the camera.
        public bool actingAsStylusPointer_notCamera { get; set; }

        // ----- Internal latch state -----
        // Wraps the serialized debug field so the Inspector readout always matches the real state.
        private bool FollowParentLatch
        {
            get => _debugFollowParentLatch;
            set => _debugFollowParentLatch = value;
        }

        // ----- Startup camera state (captured once, restored on double-click) -----
        private Transform _startParent;
        private Vector3 _startLocalPosition;
        private Quaternion _startLocalRotation;

        // ----- Edge detection / timing state -----
        private bool _lastShutter;             // TUI button state from previous frame; used for edge detection.
        private float _lastPressTime = -999f;  // Time.time of the most recent button-press edge.
        private bool _ignoreShutterUntilRelease; // After a double-click reset, swallow the rest of the hold.

        // Cached main camera so we don't pay the FindGameObjectWithTag cost every frame.
        private Camera _cam;
        private Transform _camT;

        void Start()
        {
            actingAsStylusPointer_notCamera = false;

            // Singleton: keep the first instance, destroy any later duplicates.
            if (ME != null) GameObject.Destroy(ME);
            else ME = this;
            DontDestroyOnLoad(this);

            // Cache the camera references once.
            _cam = Camera.main;
            _camT = _cam.transform;

            // Start in the "free" state — parented to DefaultParent, perspective on, no latch.
            ResetFree();

            // Snapshot the camera's startup pose so a double-click can restore it later.
            _startParent = _camT.parent;
            _startLocalPosition = _camT.localPosition;
            _startLocalRotation = _camT.localRotation;

            // Sanity check: if follow is enabled but no target is assigned, warn once.
            // The runtime null checks below will silently skip the follow behavior either way,
            // so this is purely to save you from a confusing "why isn't it sticking" debugging session.
            if (EnableFollowParent && FollowParent == null)
            {
                Debug.LogWarning("[TUIScript] EnableFollowParent is true but FollowParent is unassigned — follow/latch behavior is disabled.", this);
            }
        }

        /// <summary>
        /// Put the camera into the "free" state: parented to DefaultParent, perspective on, latch cleared.
        /// </summary>
        public void ResetFree()
        {
            _camT.SetParent(DefaultParent);
            _cam.orthographic = false;
            FollowParentLatch = false;
        }

        void Update()
        {
            // ----- Debug: keep in-range readout fresh every frame, regardless of button state -----
            if (EnableFollowParent && FollowParent != null)
            {
                float dDebug = Vector3.Distance(transform.position, FollowParent.transform.position);
                _debugInRange = dDebug < StickyDistance;
            }
            else
            {
                _debugInRange = false;
            }

            // When this script is repurposed as a stylus, leave the camera alone entirely.
            if (actingAsStylusPointer_notCamera) return;

            // Need the microcontroller singleton to read the button.
            if (Microcontroller_Manager.ME == null) return;

            bool cameraShutter = Microcontroller_Manager.ME.TUIPressed;

            // After a double-click reset we wait for the user to let go before resuming normal behavior,
            // so the still-held button doesn't immediately re-enter viewfinder mode.
            if (_ignoreShutterUntilRelease)
            {
                if (!cameraShutter) _ignoreShutterUntilRelease = false;
                _lastShutter = cameraShutter; // keep edge detection consistent
                return;
            }

            // ----- Edge detection -----
            bool shutterJustPressed = cameraShutter && !_lastShutter;
            bool shutterJustReleased = !cameraShutter && _lastShutter;
            _lastShutter = cameraShutter;

            // ----- Press edge: handle double-click and un-latch -----
            if (shutterJustPressed)
            {
                // Double-click check first — it overrides everything else.
                if (Time.time - _lastPressTime <= doubleClickWindow)
                {
                    // Restore camera to its startup parent and pose.
                    ResetFree();
                    _camT.SetParent(_startParent);
                    _camT.localPosition = _startLocalPosition;
                    _camT.localRotation = _startLocalRotation;

                    _lastPressTime = -999f;            // Reset so a third quick press isn't another "double".
                    _ignoreShutterUntilRelease = true; // Swallow the rest of this hold.
                    return;
                }

                // Single press: remember when it happened (for the next double-click window check).
                _lastPressTime = Time.time;

                // If we were latched, this press un-latches and hands control back to the viewfinder.
                // We re-parent to DefaultParent here so the camera detaches from FollowParent cleanly;
                // the viewfinder block below will then snap it to the TUI for this frame onwards.
                if (FollowParentLatch)
                {
                    FollowParentLatch = false;
                    _camT.SetParent(DefaultParent);
                }
            }

            // ----- Release edge: commit a latch if in range, otherwise leave camera frozen -----
            if (shutterJustReleased)
            {
                // Only consider latching if we're NOT already latched, follow is enabled, and we're in range.
                if (!FollowParentLatch && EnableFollowParent && FollowParent != null && _debugInRange)
                {
                    // worldPositionStays:true preserves the camera's current world pose (the TUI pose the
                    // user was happy enough to release at) and converts it into a local offset under
                    // FollowParent — so it rides along from that exact framing.
                    _camT.SetParent(FollowParent.transform, worldPositionStays: true);
                    FollowParentLatch = true;
                }
                // If out of range or follow is disabled, do nothing: the camera stays frozen
                // at the last pose the viewfinder block wrote.
            }

            // ----- Per-frame viewfinder behavior (only while the button is held AND we're not latched) -----
            // If the latch is set, we shouldn't be in this state — the press handler above clears it on press —
            // but the guard keeps things safe regardless.
            if (cameraShutter && !FollowParentLatch)
            {
                _cam.orthographic = false;
                _camT.SetParent(DefaultParent);
                _camT.position = transform.position;
                _camT.rotation = transform.rotation;
            }
            // If !cameraShutter and !FollowParentLatch: camera is idle, sitting wherever it was last placed.
            // If FollowParentLatch is true: camera is parented to FollowParent and inherits its motion. Do nothing.
        }
    }
}