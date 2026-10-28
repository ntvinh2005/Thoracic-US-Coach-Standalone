using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace SMMARTS
{
    public class UltrasoundDisplayController : MonoBehaviour
    {

        /// This static UltrasoundManager is used to ensure a single instance of the ultrasound manager exists in the scene and 
        /// that the single instance is easily accessible to outside scripts. In awake we destroy all other possible instances.
        /// When we need to create a second ultrasound image rendering simultaneously, like you may find in a biplane probe, then duplicate this entire script and name it UltrasoundManager_2. 
        // public static UltrasoundDisplayController ME;

        public static UltrasoundDisplayController ME => Get(Plane.Primary);
        public static UltrasoundDisplayController ME2 => Get(Plane.Secondary);

        public enum Plane { Primary, Secondary }

        private static Dictionary<Plane, UltrasoundDisplayController> _instances = new();

        // [Header("-----Primary Plane or Secondary Biplane?-----", order = 0)]
        [SerializeField] private Plane plane = Plane.Primary;

        public static UltrasoundDisplayController Get(Plane plane) => _instances[plane];

        // Safe version of Get() for callers that may run before Awake(), or for simulators that only have a primary plane.
        public static bool TryGet(Plane plane, out UltrasoundDisplayController controller) => _instances.TryGetValue(plane, out controller);

        // UltrasoundDisplayController.Plane and UltrasoundManager.Plane are separate enums, so map between them explicitly.
        private UltrasoundManager.Plane ManagerPlane =>
            plane == Plane.Secondary ? UltrasoundManager.Plane.Secondary : UltrasoundManager.Plane.Primary;


        [Header("-----Appearance at Startup-----", order = 1)]

        /// If true, begins using small ultrasound UI, if false, begins using large ultrasound UI dimensions.
        /// This is the only authored setting for screen size. The live state is the UltrasoundScreenSize property,
        /// which is not serialized because Start() applies this value over it every time.
        [SerializeField]
        bool beginInSmallScreen = true;

        /// If true, begins using blur, attenuation, and anisotropy settings.
        [SerializeField]
        bool beginWithRealism = true;

        /// If true, begins with the orientation dot on the right side of the display (cardiology, thyroid bx, etc.).
        /// If false, begins with the dot on the left, which is the default (anesthesiology).
        /// This is the only authored setting for sidedness. The live state is the DotOnRight property, which is not
        /// serialized, because Start() applies this value over it every time anyway.
        [SerializeField, FormerlySerializedAs("beginWithReverseImage")]
        bool beginWithDotOnRight = false;

        // Per-simulator overrides for cog aid sidedness, exposed on the ultrasound panel.
        // Which orientation reads correctly depends on the user's perspective, including how they physically stand
        // while holding the probe and needle, and that is specific to each use case. Flip these to suit your simulator.
        public bool InvertAxisViewCogAid, InvertInPlaneCogAid, InvertOutOfPlaneCogAid;


        /// Enum for the screen dimensions. Allows further expansion and default dimensions to be added at a different time.
        /// Used to quickly and simply toggle screen between two editable sizes.
        public enum SCREEN_DIMENSIONS
        {
            LARGE = 1,
            SMALL = 2
        }

        [Header("-----User Interface Managment-----", order = 1)]

        /// The live screen size. Deliberately not serialized, so it cannot drift away from beginInSmallScreen above:
        /// one authored knob, one runtime value. The custom inspector shows this as a live readout during play.
        SCREEN_DIMENSIONS ultrasoundScreenSize = SCREEN_DIMENSIONS.SMALL;
        public SCREEN_DIMENSIONS UltrasoundScreenSize
        {
            get
            {
                return ultrasoundScreenSize;
            }
            set
            {
                if (value != ultrasoundScreenSize)
                {
                    ultrasoundScreenSize = value;
                    UpdateDisplayPositionOnScreen();
                }
            }
        }

        [Header("----- Gameobject Assignments -----", order = 1)]
        /// The GameObject which holds the canvas upon which the ultrasound is rendered.
        [SerializeField] RectTransform Ultrasound_Display_Screen;

        /// The GameObject whose canvas holds the large dimensions used for the increasing size of the ultrasound.
        [SerializeField] RectTransform Ultrasound_Screen_Large_Dimensions;

        /// The GameObject whose canvas holds the small dimensions use for the decreasing size of the ultrasound.
        [SerializeField] RectTransform Ultrasound_Screen_Small_Dimensions;

        RectTransform small_or_large_rect;


        /// <summary>
        /// In Awake() we create the static singleton reference UltrasoundUIManager.ME. It also destroys every unnecessary instance of 
        /// the ultrasound ensuring the single instance.
        /// </summary>
        private void Awake()
        {
            _instances[plane] = this;
            //if (ME != null)
            //    Destroy(ME);
            //ME = this;   
        }

        // Start is called before the first frame update
        void Start()
        {
            // These are common SDK US parts so we can find them once at start().
            // Older implementations my use these names, but they should be included in the inspector.
            if (Ultrasound_Display_Screen == null) Ultrasound_Display_Screen = GameObject.Find("Ultrasound Display Screen").GetComponent<RectTransform>();
            if (Ultrasound_Screen_Large_Dimensions == null) Ultrasound_Screen_Large_Dimensions = GameObject.Find("Ultrasound Screen Large Dimensions").GetComponent<RectTransform>();
            if (Ultrasound_Screen_Small_Dimensions == null) Ultrasound_Screen_Small_Dimensions = GameObject.Find("Ultrasound Screen Small Dimensions").GetComponent<RectTransform>();

            // The scene's authored layout of the ultrasound display is what defines the SMALL preset,
            // so record it as SMALL first. See the comment inside InitializeDisplayPositionOnScreen().
            ultrasoundScreenSize = SCREEN_DIMENSIONS.SMALL;
            InitializeDisplayPositionOnScreen();

            // Now honor the authored startup size. We assign through the property rather than the field so that
            // beginning in large actually moves the display to the large dimensions. If we are beginning small
            // the property's own equality check makes this a no-op.
            UltrasoundScreenSize = beginInSmallScreen ? SCREEN_DIMENSIONS.SMALL : SCREEN_DIMENSIONS.LARGE;

            if (beginWithRealism) RealismOn(); else RealismOff();

            // Reverse Image On and Off not only sets the dot on the right or left side of the ultrasound display,
            // it also sets the orientation of the cog aids for axis view and in- and out-of-plane needling.
            if (beginWithDotOnRight) ReverseImage_On(); else ReverseImage_Off();

        }

        // Update is called once per frame
        void Update()
        {
            // UpdateDisplayPositionOnScreen();
        }


        /// <summary>
        /// Sets ultrasound display to one of the two preset sizes. 
        /// Currently there are two default sizes, Large and Small, but we expect those to move around as necessary to meet the needs of a specific simulator.
        /// The transform rectangle adjusts anchor positions, width, and height - not scale or rotation.
        /// It tests first to see if they need to be moved to avoid updating canvas items needlessly.
        /// Intended to be called from Update() because we expect the small and large dimensions to change dynamically.
        /// </summary>
        /// 
        void UpdateDisplayPositionOnScreen()  
        {

            if (ultrasoundScreenSize == SCREEN_DIMENSIONS.SMALL)
                small_or_large_rect = Ultrasound_Screen_Small_Dimensions; 
            else
                small_or_large_rect = Ultrasound_Screen_Large_Dimensions;

            // Changing a RectTransform's properties (like position, size, anchored position, etc.)
            // triggers Unity's layout system to recalculate the positions and sizes of all affected UI elements. 
            // So, we'll test first and update only if necessary.
            if (Ultrasound_Display_Screen.anchoredPosition != small_or_large_rect.anchoredPosition)
            {
                Ultrasound_Display_Screen.anchoredPosition = small_or_large_rect.anchoredPosition;
                Ultrasound_Display_Screen.anchoredPosition3D = small_or_large_rect.anchoredPosition3D;
                Ultrasound_Display_Screen.anchorMax = small_or_large_rect.anchorMax;
                Ultrasound_Display_Screen.anchorMin = small_or_large_rect.anchorMin;
                Ultrasound_Display_Screen.sizeDelta = small_or_large_rect.sizeDelta;
                Ultrasound_Display_Screen.offsetMin = small_or_large_rect.offsetMin;
                Ultrasound_Display_Screen.offsetMax = small_or_large_rect.offsetMax;
            }

        }



        void InitializeDisplayPositionOnScreen()
        {

            if (ultrasoundScreenSize == SCREEN_DIMENSIONS.SMALL)
                small_or_large_rect = Ultrasound_Screen_Small_Dimensions;
            else
                small_or_large_rect = Ultrasound_Screen_Large_Dimensions;

            // we'll use the inital layout of our system as the small dimensions.
            if (Ultrasound_Display_Screen.anchoredPosition != small_or_large_rect.anchoredPosition)
            {
                small_or_large_rect.anchoredPosition = Ultrasound_Display_Screen.anchoredPosition;
                small_or_large_rect.anchoredPosition3D = Ultrasound_Display_Screen.anchoredPosition3D;
                small_or_large_rect.anchorMax = Ultrasound_Display_Screen.anchorMax;
                small_or_large_rect.anchorMin = Ultrasound_Display_Screen.anchorMin;
                small_or_large_rect.sizeDelta = Ultrasound_Display_Screen.sizeDelta;
                small_or_large_rect.offsetMin = Ultrasound_Display_Screen.offsetMin;
                small_or_large_rect.offsetMax = Ultrasound_Display_Screen.offsetMax;
            }

        }



        // Fullscreen is a toggle that copies the properties of two prototype objects (a small and a large) to the ultrasound display image.
        public void FullscreenToggle()
        {
            if (ultrasoundScreenSize == SCREEN_DIMENSIONS.SMALL)
            {
                FullscreenOn();
            }
            else
            {
                FullscreenOff();
            }
        }

        public void FullscreenOn()
        {
            ultrasoundScreenSize = SCREEN_DIMENSIONS.LARGE;
            UpdateDisplayPositionOnScreen();
        }

        public void FullscreenOff()
        {
            ultrasoundScreenSize = SCREEN_DIMENSIONS.SMALL;
            UpdateDisplayPositionOnScreen();
        }


        // REALISM in SMMARTS simulators is a bundle of Blur, Depth Attenuation, and Anisotropy. 
        // Blur, Depth Attenuation, and Anisotropy can be adjusted separately but they have been bundled together in practice since creation.
        // the default is to have Realism off (no Blur, Depth Attenuation, or Anisotropy).
        public void RealismToggle()
        {
            UltrasoundManager.ME.Blur = !UltrasoundManager.ME.Blur;
            UltrasoundManager.ME.DepthAttenuation = !UltrasoundManager.ME.DepthAttenuation;
            UltrasoundManager.ME.Anisotropy = !UltrasoundManager.ME.Anisotropy;
        }

        public void RealismOn()
        {
            UltrasoundManager.ME.Blur = true;
            UltrasoundManager.ME.DepthAttenuation = true;
            UltrasoundManager.ME.Anisotropy = true;
        }

        public void RealismOff()
        {
            UltrasoundManager.ME.Blur = false;
            UltrasoundManager.ME.DepthAttenuation = false;
            UltrasoundManager.ME.Anisotropy = false;
        }


        // REVERSE IMAGE flips the US image view and moves the dot - without flipping the scale.
        // ultrasound machines label this function Reverse Image, Reverse, R, Rt/Lt depending on the manufacturer.
        // The orientation dot on the US image is on the left by default; cardioloy puts the dot on the right.
        // this is done by changing the sign of the X value of the tiling of the texture
        // it also enables a different sidedness dot. We're using a different sidedness dot becuase the flipped dot may interfere with the scale, 
        // so its safer to just have two for you to move around as you see fit, based on the shape of your insonating plane and the scale.
        public MeshRenderer RenderTexture;
        public GameObject SidednessDot_Normal, SidednessDot_Flipped;

        // The live sidedness state. Deliberately not serialized: the authored value lives in beginWithDotOnRight,
        // which Start() applies over this every time. Two serialized copies of the same setting is what used to make
        // the old inspector checkbox look live when it was actually inert.
        private bool dotOnRight = false;

        /// <summary>
        /// True when the orientation dot is on the right side of the display (cardiology, thyroid bx, etc.).
        /// False is the default, with the dot on the left (anesthesiology).
        /// Assigning to this applies the change; it is the same as calling ReverseImage_On() or ReverseImage_Off().
        /// </summary>
        public bool DotOnRight
        {
            get => dotOnRight;
            set { if (value) ReverseImage_On(); else ReverseImage_Off(); }
        }

        [System.Obsolete("Renamed to DotOnRight, which says which side the orientation dot is on. This alias forwards to DotOnRight and will be removed in a future SDK version.")]
        public bool ImagedReversed
        {
            get => DotOnRight;
            set => DotOnRight = value;
        }

        public void ReverseImage()
        {
            if (dotOnRight == false)
            {
                ReverseImage_On();  // orientation dot on right side (used by cardiology, thyroid bx, etc.)
            }
            else
            {
                ReverseImage_Off(); // orientation dot on left side (default)
            }
        }

        public void ReverseImage_On() // orientation dot on right side (used by cardiology, thyroid bx, etc.)
        {
            dotOnRight = true; // set the backing field, not the property, so the property setter cannot recurse into here.
            RenderTexture.material.mainTextureScale = new Vector2(-1, 1); // in the inspector, this is listed under Main Maps and labeled "Tiling"
            SidednessDot_Normal.SetActive(false);
            SidednessDot_Flipped.SetActive(true);
            SetCogAidSidedness(); // handles the sidedness of all three cog aids related to ultrasound probe and needling.
        }

        public void ReverseImage_Off() // orientation dot on left side (default - anesthesiology)
        {
            dotOnRight = false; // set the backing field, not the property, so the property setter cannot recurse into here.
            RenderTexture.material.mainTextureScale = new Vector2(1, 1); // in the inspector, this is listed under Main Maps and labeled "Tiling"
            SidednessDot_Normal.SetActive(true);
            SidednessDot_Flipped.SetActive(false);
            SetCogAidSidedness(); // handles the sidedness of all three cog aids related to ultrasound probe and needling.
        }

        public void SetCogAidSidedness()
        {
            // The colored edge lines on the ultrasound display have to swap colors with the sidedness dot.
            // Both planes have their own pair, so this has to happen before we bail out on the biplane below.
            // The lines belong to the UltrasoundManager for this plane, so we hand it off there.
            if (UltrasoundManager.TryGet(ManagerPlane, out UltrasoundManager ultrasoundManager))
                ultrasoundManager.ApplyEdgeLineSidedness();

            // we don't have these cog aids for the biplane (they are duplicates).
            if (plane == Plane.Secondary) return;

            // this is for the Axis View cog aid, looking down the axis of the probe.
            // this US Axis Viewpoint CogAid on/off is controlled elsewhere; this sets which child version the user sees when its parent is on.
            bool axisViewRight = dotOnRight; if (InvertAxisViewCogAid) axisViewRight = !axisViewRight;
            if (axisViewRight)  {
                AxisView_Notch_on_RightSide.SetActive(true);// Axis Viewpoint Cog Aid, as seen with notch on the right
                AxisView_Notch_on_LeftSide.SetActive(false);// Axis Viewpoint Cog Aid, as seen with notch on the left
            } else {

                AxisView_Notch_on_RightSide.SetActive(false);// Axis Viewpoint Cog Aid, as seen with notch on the right
                AxisView_Notch_on_LeftSide.SetActive(true);  // Axis Viewpoint Cog Aid, as seen with notch on the left
            }

            // this is for the in-plane cog aid, looking at the wide side of the probe for in-plane needling.
            // The cog aid toggle is controlled elsewhere; this sets which side the user sees when the cog aid is on.
            bool inPlaneViewRight = dotOnRight; if (InvertInPlaneCogAid) inPlaneViewRight = !inPlaneViewRight;
            if (inPlaneViewRight)
            {
                CameraForInPlaneNeedling.transform.position = TransformForInPlaneNeedling_DotOnRight.position;
                CameraForInPlaneNeedling.transform.rotation = TransformForInPlaneNeedling_DotOnRight.rotation;
            }
            else
            {
                CameraForInPlaneNeedling.transform.position = TransformForInPlaneNeedling_DotOnLeft.position;
                CameraForInPlaneNeedling.transform.rotation = TransformForInPlaneNeedling_DotOnLeft.rotation;
            }

            // this is for the out-of-plane cog aid, looking at the narrow side of the probe for out-of-plane needling.
            // The cog aid toggle is controlled elsewhere; this sets which side the user sees when the cog aid is on.
            // Unlike the other two cog aids, this one is deliberately NOT tied to the right/left sidedness of the image.
            // Which side reads correctly depends on the user's perspective, including how they physically stand while
            // holding the probe and needle, and that is specific to each use case. So it is set per simulator with
            // InvertOutOfPlaneCogAid rather than derived from the image sidedness.
            // This is a design decision and not an unfinished feature: do not wire it to DotOnRight.
            bool outOfPlaneViewA = true; if (InvertOutOfPlaneCogAid) outOfPlaneViewA = !outOfPlaneViewA;
            if (outOfPlaneViewA)
            {
                CameraForOutOfPlaneNeedling.transform.position = TransformForOutOfPlaneNeedling_SideA.position;
                CameraForOutOfPlaneNeedling.transform.rotation = TransformForOutOfPlaneNeedling_SideA.rotation;
            }
            else
            {
                CameraForOutOfPlaneNeedling.transform.position = TransformForOutOfPlaneNeedling_SideB.position;
                CameraForOutOfPlaneNeedling.transform.rotation = TransformForOutOfPlaneNeedling_SideB.rotation;
            }


        }


        // FREEZING the US image (without turning the device on or off) is done by simply turning the render texture cameras on and off.
        public Camera Ultrasound_Image_Camera;   // looks at the simulated ultrasound image (lower resolution) and has effects applied to it like blur
        public Camera Ultrasound_Display_Camera; // looks at the overlayed elements of the ultrasound display, like cm scale, centerlines, sidedness dot, etc with no image effects like blur

        public void FreezeToggle()
        {
            Ultrasound_Image_Camera.enabled = !Ultrasound_Image_Camera.enabled;
            Ultrasound_Display_Camera.enabled = !Ultrasound_Display_Camera.enabled;
        }

        public void Freeze()
        {
            Ultrasound_Image_Camera.enabled = false;
            Ultrasound_Display_Camera.enabled = false;
        }

        public void UnFreeze()
        {
            Ultrasound_Image_Camera.enabled = true;
            Ultrasound_Display_Camera.enabled = true;
        }

        [Header("----- Cognitive Aids (Primary Plane Only) -----", order = 1)]

        // Controlling the visiblity of the Axis Viewpoint Cog Aid.
        // this is the view looking down the axis of the probe
        // The old name is NeedlePlaneCogAid because its useful for both in- and out-of-plane needline.
        public GameObject AxisViewpointCogAid;

        // there are two child versions: one with the notch (and red dot) on the right and one on the left.
        // they are both children of the AxisViewpointCogAid.
        // this code turns on and off the parent, usually called from a general toggle for all cog aids.
        // the code for determining which child is active is in ReverseImageOn() and ReverseImageOff() methods.
        public GameObject AxisView_Notch_on_RightSide, AxisView_Notch_on_LeftSide;

        public void AxisViewpointCogAidToggle() // formerly NeedlePlaneCogAidToggle
        {
            AxisViewpointCogAid.SetActive(!AxisViewpointCogAid.activeInHierarchy);
        }

        public void AxisViewpointCogAid_On()    // formerly NeedlePlaneCogAid_On
        {
            AxisViewpointCogAid.SetActive(true);
        }

        public void AxisViewpointCogAid_Off()   // formerly NeedlePlaneCogAid_Off
        {
            AxisViewpointCogAid.SetActive(false);
        }

        // Controlling the visiblity of the insonation angle Cognitive Aid  (in- and out-of-plane needling)
        // this is the view looking at one of the sides of the probe. there are two cog aids for this:
        // one for in-plane needling and another for out-of-plane needling, and they are siblings.
        // the controller for these two children determines which one to render depending on where the needle is in relation to the probe. 
        // This code turns on and off the PARENT; if off, neither are shown. if on, either the in-plane needling or out-of-plane needling is shown.
        // In addition, both in- and out-of-plane cog aids have two modes, one for the dot on the right and one for the dot on the left.
        // the code for determining which mode is active is in ReverseImageOn() and ReverseImageOff() methods.
        public GameObject CogAid_NeedleInsonationAngleIndicator; // this game object holds both in-plane and out-of-plane cognitive aids and their shared controller.

        // this is for just the In-Plane Needling. we copy one of the transforms to the camera depending on if the dot is on the right or left side. 
        public GameObject CameraForInPlaneNeedling;
        public Transform TransformForInPlaneNeedling_DotOnRight;
        public Transform TransformForInPlaneNeedling_DotOnLeft;

        // this is for just the Out-of-Plane Needling. we copy one of the transforms to the camera depending on if we select side A or B
        public GameObject CameraForOutOfPlaneNeedling;
        public Transform TransformForOutOfPlaneNeedling_SideA;
        public Transform TransformForOutOfPlaneNeedling_SideB;

        public void NeedleInsonationAngleCogAidToggle()
        {
            CogAid_NeedleInsonationAngleIndicator.SetActive(!CogAid_NeedleInsonationAngleIndicator.activeInHierarchy);
        }

        public void NeedleInsonationAngleCogAid_On()
        {
            CogAid_NeedleInsonationAngleIndicator.SetActive(true);
        }

        public void NeedleInsonationAngleCogAid_Off()
        {
            CogAid_NeedleInsonationAngleIndicator.SetActive(false);
        }


    }
}
