using UnityEngine;
using TMPro;
using System.Drawing;
namespace SMMARTS
{
    /// <summary>
    /// Master control script for C-Arm positioning system.
    /// Controls 7 degrees of freedom: Base InOut/Slide, UpDown/InOut translation, WigWag/Tilt/Orbit rotations.
    /// Includes brake system for manual control axes (all except motorized UpDown).
    /// Only one brake can be unlocked at a time to reduce UI clutter.
    /// </summary>
    public class C_ArmPositioner : MonoBehaviour
    {
        public static C_ArmPositioner ME;
        [Header("C-Arm GameObjects")]
        [SerializeField] private GameObject baseGO;
        [SerializeField] private GameObject upDownGO;
        [SerializeField] private GameObject inOutGO;
        [SerializeField] private GameObject wigWagGO;
        [SerializeField] private GameObject tiltGO;
        [SerializeField] private GameObject orbitGO;

        [Header("Brake Handle GameObjects")]
        [SerializeField] private GameObject baseBrakeGO; // for both baseIn/Out and baseSlide
        [SerializeField] private GameObject inOutBrakeGO;
        [SerializeField] private GameObject wigWagBrakeGO;
        [SerializeField] private GameObject tiltBrakeGO;
        [SerializeField] private GameObject orbitBrakeGO;

        [Header("Arrow GameObjects")]
        [SerializeField] private GameObject baseInOutArrowGO;
        [SerializeField] private GameObject baseSlideArrowGO;
        [SerializeField] private GameObject upDownArrowGO;
        [SerializeField] private GameObject inOutArrowGO;
        [SerializeField] private GameObject wigWagArrowGO;
        [SerializeField] private GameObject tiltArrowGO;
        [SerializeField] private GameObject orbitArrowGO;

        [Header("Position Controls")]
        [Range(-300f, 300f)]
        public float baseInOutPosition = 0f;
        [Range(-500f, 500f)]
        public float baseSlidePosition = 0f;
        [Range(0f, 450f)]
        public float upDownPosition = 0f;
        [Range(0f, -200f)]
        public float inOutPosition = 0f;
        [Range(-10f, 10f)]
        public float wigWagRotation = 0f;
        [Range(-220f, 220f)]
        public float tiltRotation = 0f;
        [Range(-90f, 45f)]
        public float orbitRotation = 0f;

        [Header("Position Controls")]
        [SerializeField] private TMP_Text OrbitButtonText;

        [Header("Brake Controls")]
        [SerializeField] private bool baseUnlocked = false;
        [SerializeField] public bool upDownUnlocked = false;
        [SerializeField] private bool inOutBrakeUnlocked = false;
        [SerializeField] private bool wigWagBrakeUnlocked = false;
        [SerializeField] public bool tiltBrakeUnlocked = false;
        [SerializeField] private bool orbitBrakeUnlocked = false;

        [Header("Wheel Controls")]
        [SerializeField] private GameObject WheelDirectionHandleR;
        [SerializeField] private GameObject WheelDirectionHandleL;
        [SerializeField] private GameObject Wheel1, Wheel2, Wheel3, Wheel4;

        [Header("Brake Settings")]
        [SerializeField] private float brakeAnimationTime = 1f;

        [Header("Fluoro Tech's Perspective View")]
        // We don't move the cameara during LLM or Keyword control.
        public bool OK_toMoveCamera = false; // buttons will set this to true, handles will set this to true if you click on them, but the LLM sets this to false. 
        public FluoroTechCameraMover FluoroTechCameraMover;
        public bool MoveCameraToTechsViewpoint = true; // internal flag

 
        // Brake animation tracking
        private float inOutBrakeStartRotation, wigWagBrakeStartRotation, tiltBrakeStartRotation, orbitBrakeStartRotation, baseBrakeStartRotation;
        private float inOutBrakeTargetRotation, wigWagBrakeTargetRotation, tiltBrakeTargetRotation, orbitBrakeTargetRotation, baseBrakeTargetRotation;
        private float inOutBrakeTimer, wigWagBrakeTimer, tiltBrakeTimer, orbitBrakeTimer, baseBrakeTimer;
        private bool inOutBrakeAnimating, wigWagBrakeAnimating, tiltBrakeAnimating, orbitBrakeAnimating, baseBrakeAnimating;
        
        // Wheel animation tracking
        private float wheelStartRotation, wheelTargetRotation, wheelTimer;
        private bool wheelAnimating;
        
        // Previous brake states for change detection
        private bool prevUpDownUnlocked, prevInOutBrake, prevWigWagBrake, prevTiltBrake, prevOrbitBrake, prevBaseBrake;

        // flag for outside.
        public bool IsMoving => baseUnlocked || upDownUnlocked || inOutBrakeUnlocked ||
                        wigWagBrakeUnlocked || tiltBrakeUnlocked || orbitBrakeUnlocked;

        public bool AllowTeleport = false; // flag for external scripts to send all the movement at once
        public bool CommandedThisFrame { get; private set; }


        public bool MovementBlocked = false;

        // holder for previous position
        // make PositionSnapshot public so GhostCArmManager can use it
        public struct PositionSnapshot
        {
            public float baseInOut, baseSlide, upDown, inOut, wigWag, tilt, orbit;
        }

        private PositionSnapshot snapshot;


        // expose snapshot getter
        public PositionSnapshot GetSnapshot() => snapshot;


        void Start()
        {
            ME = this;
            ReadCurrentPositions();
            InitializeBrakeStates();
        }
        void LateUpdate()
        {
            CheckBrakeStateChanges();
            UpdateBrakeAnimations();
            UpdateWheelAnimation();
            ApplyPositions();
            CommandedThisFrame = false;
        }
        /// <summary>
        /// Reads current positions/rotations from GameObjects and sets slider values to match
        /// </summary>
        void ReadCurrentPositions()
        {
            if (baseGO != null)
            {
                baseInOutPosition = baseGO.transform.localPosition.x;
                baseSlidePosition = baseGO.transform.localPosition.z;
            }
            if (upDownGO != null)
                upDownPosition = upDownGO.transform.localPosition.z;
            if (inOutGO != null)
                inOutPosition = inOutGO.transform.localPosition.x;
            if (wigWagGO != null)
                wigWagRotation = wigWagGO.transform.localEulerAngles.y;
            if (tiltGO != null)
                tiltRotation = tiltGO.transform.localEulerAngles.z;
            if (orbitGO != null)
                orbitRotation = orbitGO.transform.localEulerAngles.z;
        }
        /// <summary>
        /// Initialize brake handles to locked position (0 degrees) and hide all arrows
        /// </summary>
        void InitializeBrakeStates()
        {
            SetBrakeHandleRotation(baseBrakeGO, 0f);
            SetBrakeHandleRotation(inOutBrakeGO, 0f);
            SetBrakeHandleRotation(wigWagBrakeGO, 0f);
            SetBrakeHandleRotation(tiltBrakeGO, 0f);
            SetBrakeHandleRotation(orbitBrakeGO, 0f);
            // Hide all arrows initially
            SetArrowVisibility("BaseInOut", false);
            SetArrowVisibility("BaseSlide", false);
            SetArrowVisibility("UpDown", false);
            SetArrowVisibility("InOut", false);
            SetArrowVisibility("WigWag", false);
            SetArrowVisibility("Tilt", false);
            SetArrowVisibility("Orbit", false);
            // Store initial states
            prevBaseBrake = baseUnlocked;
            prevUpDownUnlocked = upDownUnlocked;
            prevInOutBrake = inOutBrakeUnlocked;
            prevWigWagBrake = wigWagBrakeUnlocked;
            prevTiltBrake = tiltBrakeUnlocked;
            prevOrbitBrake = orbitBrakeUnlocked;
        }
        /// <summary>
        /// Check for brake state changes and start animations
        /// </summary>
        void CheckBrakeStateChanges()
        {
            if (baseUnlocked != prevBaseBrake)
            {
                StartBrakeAnimation(ref baseBrakeStartRotation, ref baseBrakeTargetRotation, ref baseBrakeTimer, ref baseBrakeAnimating, baseBrakeGO, baseUnlocked, 40f);
                SetArrowVisibility("BaseInOut", baseUnlocked);
                SetArrowVisibility("BaseSlide", baseUnlocked);
                prevBaseBrake = baseUnlocked;
            }
            if (upDownUnlocked != prevUpDownUnlocked)
            {
                SetArrowVisibility("UpDown", upDownUnlocked);
                prevUpDownUnlocked = upDownUnlocked;
            }
            if (inOutBrakeUnlocked != prevInOutBrake)
            {
                StartBrakeAnimation(ref inOutBrakeStartRotation, ref inOutBrakeTargetRotation, ref inOutBrakeTimer, ref inOutBrakeAnimating, inOutBrakeGO, inOutBrakeUnlocked, 90f);
                SetArrowVisibility("InOut", inOutBrakeUnlocked);
                prevInOutBrake = inOutBrakeUnlocked;
            }
            if (wigWagBrakeUnlocked != prevWigWagBrake)
            {
                StartBrakeAnimation(ref wigWagBrakeStartRotation, ref wigWagBrakeTargetRotation, ref wigWagBrakeTimer, ref wigWagBrakeAnimating, wigWagBrakeGO, wigWagBrakeUnlocked, 90f);
                SetArrowVisibility("WigWag", wigWagBrakeUnlocked);
                prevWigWagBrake = wigWagBrakeUnlocked;
            }
            if (tiltBrakeUnlocked != prevTiltBrake)
            {
                StartBrakeAnimation(ref tiltBrakeStartRotation, ref tiltBrakeTargetRotation, ref tiltBrakeTimer, ref tiltBrakeAnimating, tiltBrakeGO, tiltBrakeUnlocked, 45f);
                SetArrowVisibility("Tilt", tiltBrakeUnlocked);
                prevTiltBrake = tiltBrakeUnlocked;
            }
            if (orbitBrakeUnlocked != prevOrbitBrake)
            {
                StartBrakeAnimation(ref orbitBrakeStartRotation, ref orbitBrakeTargetRotation, ref orbitBrakeTimer, ref orbitBrakeAnimating, orbitBrakeGO, orbitBrakeUnlocked, 45f);
                SetArrowVisibility("Orbit", orbitBrakeUnlocked);
                prevOrbitBrake = orbitBrakeUnlocked;
            }
            // Camera viewpoint logic
            if (MoveCameraToTechsViewpoint & OK_toMoveCamera)
            {
                if (baseUnlocked) FluoroTechCameraMover.BaseViewpoint();
                else if (upDownUnlocked) FluoroTechCameraMover.UpDownViewpoint();
                else if (inOutBrakeUnlocked) FluoroTechCameraMover.InOutViewpoint();
                else if (wigWagBrakeUnlocked) FluoroTechCameraMover.WigWagViewpoint();
                else if (tiltBrakeUnlocked) FluoroTechCameraMover.TiltViewpoint();
                else if (orbitBrakeUnlocked) FluoroTechCameraMover.OrbitViewpoint();
                else FluoroTechCameraMover.DefaultViewpoint();
            }
            else
            {
                FluoroTechCameraMover.DefaultViewpoint();
            }
        }
        /// <summary>
        /// Show or hide arrow based on control state
        /// </summary>
        void SetArrowVisibility(string controlType, bool visible)
        {
            GameObject arrowGO = null;
            switch (controlType)
            {
                case "BaseInOut":
                    arrowGO = baseInOutArrowGO;
                    break;
                case "BaseSlide":
                    arrowGO = baseSlideArrowGO;
                    break;
                case "UpDown":
                    arrowGO = upDownArrowGO;
                    break;
                case "InOut":
                    arrowGO = inOutArrowGO;
                    break;
                case "WigWag":
                    arrowGO = wigWagArrowGO;
                    break;
                case "Tilt":
                    arrowGO = tiltArrowGO;
                    break;
                case "Orbit":
                    arrowGO = orbitArrowGO;
                    break;
            }
            if (arrowGO != null)
            {
                arrowGO.SetActive(visible);
            }
        }
        /// <summary>
        /// Start brake handle animation
        /// </summary>
        void StartBrakeAnimation(ref float startRotation, ref float targetRotation, ref float timer, ref bool animating, GameObject brakeGO, bool unlocked, float unlockedAngle)
        {
            if (brakeGO != null)
            {
                startRotation = brakeGO.transform.localEulerAngles.z;
                targetRotation = unlocked ? unlockedAngle : 0f;
                timer = 0f;
                animating = true;
            }
        }
        /// <summary>
        /// Start wheel rotation animation (Y-axis)
        /// Only starts if target is different from current target
        /// </summary>
        void StartWheelAnimation(float targetRotation)
        {
            // Only start animation if we're not already animating toward this target
            if (!wheelAnimating || !Mathf.Approximately(wheelTargetRotation, targetRotation))
            {
                // Get current rotation from first wheel (they should all match)
                if (WheelDirectionHandleR != null)
                {
                    wheelStartRotation = WheelDirectionHandleR.transform.localEulerAngles.y;
                    wheelTargetRotation = targetRotation;
                    wheelTimer = 0f;
                    wheelAnimating = true;
                }
            }
        }
        /// <summary>
        /// Update all brake animations
        /// </summary>
        void UpdateBrakeAnimations()
        {
            UpdateSingleBrakeAnimation(ref baseBrakeTimer, ref baseBrakeAnimating, baseBrakeGO, baseBrakeStartRotation, baseBrakeTargetRotation);
            UpdateSingleBrakeAnimation(ref inOutBrakeTimer, ref inOutBrakeAnimating, inOutBrakeGO, inOutBrakeStartRotation, inOutBrakeTargetRotation);
            UpdateSingleBrakeAnimation(ref wigWagBrakeTimer, ref wigWagBrakeAnimating, wigWagBrakeGO, wigWagBrakeStartRotation, wigWagBrakeTargetRotation);
            UpdateSingleBrakeAnimation(ref tiltBrakeTimer, ref tiltBrakeAnimating, tiltBrakeGO, tiltBrakeStartRotation, tiltBrakeTargetRotation);
            UpdateSingleBrakeAnimation(ref orbitBrakeTimer, ref orbitBrakeAnimating, orbitBrakeGO, orbitBrakeStartRotation, orbitBrakeTargetRotation);
        }
        /// <summary>
        /// Update individual brake animation
        /// </summary>
        void UpdateSingleBrakeAnimation(ref float timer, ref bool animating, GameObject brakeGO, float startRotation, float targetRotation)
        {
            if (animating && brakeGO != null)
            {
                timer += Time.deltaTime;
                float progress = Mathf.Clamp01(timer / brakeAnimationTime);
                float currentRotation = Mathf.Lerp(startRotation, targetRotation, progress);
                SetBrakeHandleRotation(brakeGO, currentRotation);
                if (progress >= 1f)
                {
                    animating = false;
                }
            }
        }
        /// <summary>
        /// Update wheel rotation animation (all 6 wheel GameObjects)
        /// </summary>
        void UpdateWheelAnimation()
        {
            if (wheelAnimating)
            {
                wheelTimer += Time.deltaTime;
                float progress = Mathf.Clamp01(wheelTimer / brakeAnimationTime);
                float currentRotation = Mathf.LerpAngle(wheelStartRotation, wheelTargetRotation, progress);

                // Apply rotation to all 6 wheel objects
                SetWheelRotation(WheelDirectionHandleR, currentRotation);
                SetWheelRotation(WheelDirectionHandleL, currentRotation);
                SetWheelRotation(Wheel1, currentRotation);
                SetWheelRotation(Wheel2, currentRotation);
                SetWheelRotation(Wheel3, currentRotation);
                SetWheelRotation(Wheel4, currentRotation);

                if (progress >= 1f)
                {
                    wheelAnimating = false;
                }
            }
        }
        /// <summary>
        /// Set wheel rotation (Y axis only)
        /// </summary>
        void SetWheelRotation(GameObject wheelGO, float yRotation)
        {
            if (wheelGO != null)
            {
                Vector3 rot = wheelGO.transform.localEulerAngles;
                rot.y = yRotation;
                wheelGO.transform.localEulerAngles = rot;
            }
        }
        /// <summary>
        /// Set brake handle rotation (Z axis only)
        /// </summary>
        void SetBrakeHandleRotation(GameObject brakeGO, float zRotation)
        {
            if (brakeGO != null)
            {
                Vector3 rot = brakeGO.transform.localEulerAngles;
                rot.z = zRotation;
                brakeGO.transform.localEulerAngles = rot;
            }
        }
        /// <summary>
        /// Applies slider values to GameObjects transforms (only if brakes allow movement)
        /// </summary>
        void ApplyPositions()
        {
            // Base movements work when unlocked
            if (baseGO != null && baseUnlocked)
            {
                Vector3 pos = baseGO.transform.localPosition;
                pos.x = baseInOutPosition;
                pos.z = baseSlidePosition;
                baseGO.transform.localPosition = pos;
            }
            // UpDown works when unlocked
            if (upDownGO != null && upDownUnlocked)
            {
                Vector3 pos = upDownGO.transform.localPosition;
                pos.z = upDownPosition;
                upDownGO.transform.localPosition = pos;
            }
            // InOut only works if brake is unlocked
            if (inOutGO != null && inOutBrakeUnlocked)
            {
                Vector3 pos = inOutGO.transform.localPosition;
                pos.x = inOutPosition;
                inOutGO.transform.localPosition = pos;
            }
            // WigWag only works if brake is unlocked (uses Y axis)
            if (wigWagGO != null && wigWagBrakeUnlocked)
            {
                Vector3 rot = wigWagGO.transform.localEulerAngles;
                rot.y = wigWagRotation;
                wigWagGO.transform.localEulerAngles = rot;
            }
            // Tilt only works if brake is unlocked
            if (tiltGO != null && tiltBrakeUnlocked)
            {
                Vector3 rot = tiltGO.transform.localEulerAngles;
                rot.z = tiltRotation;
                tiltGO.transform.localEulerAngles = rot;
            }
            // Orbit only works if brake is unlocked
            if (orbitGO != null && orbitBrakeUnlocked)
            {
                Vector3 rot = orbitGO.transform.localEulerAngles;
                rot.z = orbitRotation;
                orbitGO.transform.localEulerAngles = rot;
                OrbitButtonText.text = "Oblique<size=65%>" + "\n" + orbitRotation.ToString("0") + "°";
            }
        }
        /// <summary>
        /// Toggle brake state - only one brake can be unlocked at a time
        /// Called by UI buttons and BrakeHandle scripts
        /// </summary>
        public void ToggleBrake(string brakeType)
        {
            // Lock all brakes first (only one can be unlocked at a time)
            // Then unlock the requested brake
            TakeSnapshot();
            MoveCameraToTechsViewpoint = true;
            switch (brakeType)
            {
                case "Base":
                    baseUnlocked = !baseUnlocked;
                    upDownUnlocked = false;
                    inOutBrakeUnlocked = false;
                    wigWagBrakeUnlocked = false;
                    tiltBrakeUnlocked = false;
                    orbitBrakeUnlocked = false;
                    break;
                case "UpDown":
                    upDownUnlocked = !upDownUnlocked;
                    baseUnlocked = false;
                    inOutBrakeUnlocked = false;
                    wigWagBrakeUnlocked = false;
                    tiltBrakeUnlocked = false;
                    orbitBrakeUnlocked = false;
                    break;
                case "InOut":
                    inOutBrakeUnlocked = !inOutBrakeUnlocked;
                    baseUnlocked = false;
                    upDownUnlocked = false;
                    wigWagBrakeUnlocked = false;
                    tiltBrakeUnlocked = false;
                    orbitBrakeUnlocked = false;
                    break;
                case "WigWag":
                    wigWagBrakeUnlocked = !wigWagBrakeUnlocked;
                    baseUnlocked = false;
                    upDownUnlocked = false;
                    inOutBrakeUnlocked = false;
                    tiltBrakeUnlocked = false;
                    orbitBrakeUnlocked = false;
                    break;
                case "Tilt":
                    tiltBrakeUnlocked = !tiltBrakeUnlocked;
                    baseUnlocked = false;
                    upDownUnlocked = false;
                    inOutBrakeUnlocked = false;
                    wigWagBrakeUnlocked = false;
                    orbitBrakeUnlocked = false;
                    break;
                case "Orbit":
                    orbitBrakeUnlocked = !orbitBrakeUnlocked;
                    baseUnlocked = false;
                    upDownUnlocked = false;
                    inOutBrakeUnlocked = false;
                    wigWagBrakeUnlocked = false;
                    tiltBrakeUnlocked = false;
                    break;
                default:
                    Debug.LogWarning($"Unknown brake type: {brakeType}");
                    break;
            }
        }
        /// <summary>
        /// Updates C-Arm position/rotation based on input from arrows
        /// Called by C_ArmDragControl scripts
        /// </summary>
        /// 

        // string blockedControlType;
        // int positiveDeltaValue;
        public void UpdatePosition(string controlType, float deltaValue)
        {
            Debug.Log("UpdatePosition is running!");

            if (MovementBlocked) return;

            CommandedThisFrame = true;

            // Check if movement would cause collision
            if (CArmCollisionDetector.Instance.IsInterferenceDetected())
            {
                // TODO: correct collision
            }

            switch (controlType)
            {
                case "BaseInOut":
                    baseInOutPosition = Mathf.Clamp(baseInOutPosition + deltaValue, -300f, 300f);
                    StartWheelAnimation(0f); // Wheels point forward for in/out movement
                    break;
                case "BaseSlide":
                    baseSlidePosition = Mathf.Clamp(baseSlidePosition + deltaValue, -500f, 500f);
                    StartWheelAnimation(-90f); // Wheels point sideways for slide movement
                    break;
                case "UpDown":
                    upDownPosition = Mathf.Clamp(upDownPosition + deltaValue, 0f, 450f);
                    break;
                case "InOut":
                    inOutPosition = Mathf.Clamp(inOutPosition + deltaValue, -200f, 0f);
                    break;
                case "WigWag":
                    wigWagRotation = Mathf.Clamp(wigWagRotation + deltaValue, -10f, 10f);
                    break;
                case "Tilt":
                    tiltRotation = Mathf.Clamp(tiltRotation + deltaValue, -220f, 220f);
                    break;
                case "Orbit":
                    orbitRotation = Mathf.Clamp(orbitRotation + deltaValue, -90f, 45f);
                    break;
                default:
                    Debug.LogWarning($"Unknown control type: {controlType}");
                    break;
            }
        }

        /// <summary>
        /// Captures all 7 axis values before a brake is unlocked
        /// </summary>
        public void TakeSnapshot()
        {
            snapshot.baseInOut = baseInOutPosition;
            snapshot.baseSlide = baseSlidePosition;
            snapshot.upDown = upDownPosition;
            snapshot.inOut = inOutPosition;
            snapshot.wigWag = wigWagRotation;
            snapshot.tilt = tiltRotation;
            snapshot.orbit = orbitRotation;
        }

        /// <summary>
        /// Captures all 7 axis values at any time
        /// </summary>
        public PositionSnapshot GetCurrentPositionAsSnapshot()
        {
            return new PositionSnapshot
            {
                baseInOut = baseInOutPosition,
                baseSlide = baseSlidePosition,
                upDown = upDownPosition,
                inOut = inOutPosition,
                wigWag = wigWagRotation,
                tilt = tiltRotation,
                orbit = orbitRotation
            };
           
        }

        /// <summary>
        /// Restores all 7 axis values to pre-command snapshot
        /// Called by UI "go back" button or collision recovery
        /// </summary>
        public void RestoreSnapshot()
        {
            baseInOutPosition = snapshot.baseInOut;
            baseSlidePosition = snapshot.baseSlide;
            upDownPosition = snapshot.upDown;
            inOutPosition = snapshot.inOut;
            wigWagRotation = snapshot.wigWag;
            tiltRotation = snapshot.tilt;
            orbitRotation = snapshot.orbit;
            MovementBlocked = false;
        }

        /// <summary>
        /// Explicitly sets a brake state without toggling.
        /// Safe to call even if the brake is already in the requested state.
        /// </summary>
        public void SetBrake(string brakeType, bool unlocked)
        {
            switch (brakeType)
            {
                case "Base": baseUnlocked = unlocked; break;
                case "UpDown": upDownUnlocked = unlocked; break;
                case "InOut": inOutBrakeUnlocked = unlocked; break;
                case "WigWag": wigWagBrakeUnlocked = unlocked; break;
                case "Tilt": tiltBrakeUnlocked = unlocked; break;
                case "Orbit": orbitBrakeUnlocked = unlocked; break;
                default: Debug.LogWarning($"SetBrake: unknown brake type {brakeType}"); break;
            }
        }

        // expose axis reader for AnimateAxis
        public float ReadAxis(string axis)
        {
            switch (axis)
            {
                case "BaseInOut": return baseInOutPosition;
                case "BaseSlide": return baseSlidePosition;
                case "UpDown": return upDownPosition;
                case "InOut": return inOutPosition;
                case "WigWag": return wigWagRotation;
                case "Tilt": return tiltRotation;
                case "Orbit": return orbitRotation;
                default: return 0f;
            }
        }
    }
}