using System.Collections.Generic;
using System;
using UnityEngine;
using TMPro;
using System.Runtime.CompilerServices;
using System.Linq;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using System.Security.Permissions;
using static UnityEngine.Rendering.DebugUI;


/// <summary>
/// The UltrasoundManager is contained within the SMMARTS namespace.
/// </summary>
/// 
namespace SMMARTS
{
    /// <summary>
    /// 
    /// Class Overview:
    /// The UltrasoundManager (USM) is in charge of generating the Ultrasound (US) image. It uses the ultrasound probe's position
    /// in space, relative to specified anatomies to generate realistic ultrasound images. The ultrasound plane is rendered upon
    /// rebuild, and numerous variables can be used to manipulate the size and shape of the ultrasound's scanning plane. As it 
    /// sits, the values used to manipulate the scanning plane are only visible in the inspector, and the only functionality of 
    /// the USM is the generation of ultrasound images. The USM operates as a singleton. There can only be one instance of the
    /// class operating at any one time. All other instances are destroyed at Awake(). The references to the singleton instance
    /// of the USM can be made through the static UltrasoundManager ME. It can be referenced by any script using the namespace
    /// SMARTS_SDK.Ultrasound. The UltrasoundManager.cs class uses world units as the default measurement, and world units are
    /// generally assumed to be mm by CSSALT.
    /// 
    /// Future Functionality:
    ///		Dual-Axis US Image Reflection:
    ///		In later updates, the ultrasound probe will have the ability to be reflected vertically and horizontally across its
    ///		central axis.
    ///	
    /// Dependencies:
    /// This class runs as a standalone class and has no dependencies.
    /// 
    /// Developers:
    ///	2016 implementation used on original CVA and RA simulators: 
    ///	Dave Lizdas.
    ///	
    /// 2018 SMARTS-SDK: complete rebuild, ultrasound plane generation, and increased performance:
    ///	Andre Kazimierz Bigos
    ///
    /// 2025 SMMARTS SDK update, refactoring, and feature additions:
    /// Dave Lizdas, Simon Mesber
    ///	
    /// TERMINOLOGY:
    /// "Scanning Plane" refers to a plane of points that represent starting end ending points for the raycasts.
    /// Scanning Plane has its own local coordinate system with the origin at the middle of the top edge. 
    /// The staring and ending points in Scanning Plane space are used for the raycasts after a world space transform.
    /// "Scanning Plane" is used instead of "Insonation Plane" to avoid confusion because we use "Insonation Plane" for other things like the red sheet of light in the 3D visualization.
    /// 
    /// BIPLANE example reference image:
    /// Li, YY., Liu, YH., Yan, L. et al. Single-plane versus real-time biplane approaches for ultrasound-guided central venous catheterization in critical care patients: a randomized controlled trial. 
    /// Crit Care 27, 366 (2023). https://doi.org/10.1186/s13054-023-04635-y
    /// 
    /// </summary>
    public class UltrasoundManager : MonoBehaviour
    {
        /// <summary>
        /// Monotonic identity of the physical pose for which the most recent
        /// explicitly synchronized ultrasound texture was produced. Normal
        /// Update-driven scans intentionally leave this value unchanged: only
        /// a caller that owns a pose transition may assign the epoch.
        /// </summary>
        public long PublishedPoseEpoch { get; private set; } = -1;

        /// <summary>Unity frame in which the latest scan texture was published.</summary>
        public int PublishedUnityFrame { get; private set; } = -1;

        /// <summary>Monotonic counter incremented for every completed scan publication.</summary>
        public long PublishedScanSerial { get; private set; }

        [Serializable]
        public sealed class PublishedScanProvenanceBeam
        {
            public int beamIndex;
            public bool airGapPresent;
            public bool hasVisibleInterface;
            public string firstVisibleObject = string.Empty;
            public int firstVisibleObjectInstanceId;
            public string firstVisibleRenderType = string.Empty;
            public float firstVisibleDepthFraction = -1f;
            public bool hasShadowInterface;
            public string firstShadowObject = string.Empty;
            public int firstShadowObjectInstanceId;
            public float firstShadowDepthFraction = -1f;
        }

        [Serializable]
        public sealed class PublishedScanProvenanceSnapshot
        {
            public long poseEpoch = -1;
            public int producerUnityFrame = -1;
            public long producerScanSerial = -1;
            public int beamCount;
            public List<PublishedScanProvenanceBeam> beams = new();
        }

        public PublishedScanProvenanceSnapshot LastPublishedScanProvenance { get; private set; }

        private bool captureNextPublishedScanProvenance;
        private PublishedScanProvenanceSnapshot pendingPublishedScanProvenance;

        public void ArmNextPublishedScanProvenance()
        {
            captureNextPublishedScanProvenance = true;
        }

        public bool TryGetPublishedScanProvenance(
            long poseEpoch,
            long scanSerial,
            out PublishedScanProvenanceSnapshot snapshot)
        {
            snapshot = LastPublishedScanProvenance;
            return snapshot != null
                && snapshot.poseEpoch == poseEpoch
                && snapshot.producerScanSerial == scanSerial;
        }

        #region Declarations

        /// This static UltrasoundManager is used to ensure a single instance of the ultrasound manager exists in the scene and 
        /// that the single instance is easily accessible to outside scripts. In awake we destroy all other possible instances.
        /// When we need to create a second ultrasound image rendering simultaneously, like you may find in a biplane probe, then duplicate this entire script and name it UltrasoundManager_2. 
        // public static UltrasoundManager ME;

        public static UltrasoundManager ME => Get(Plane.Primary);
        public static UltrasoundManager ME2 => Get(Plane.Secondary);

        public enum Plane { Primary, Secondary }

        private static Dictionary<Plane, UltrasoundManager> _instances = new();

        [SerializeField] private Plane plane = Plane.Primary;

        //[Header("-----Primary Plane or Secondary Biplane?-----", order = 0)]
        public static UltrasoundManager Get(Plane plane) => _instances[plane];

        // Safe version of Get() for callers that may run before Awake(), or for simulators that only have a primary plane.
        public static bool TryGet(Plane plane, out UltrasoundManager manager) => _instances.TryGetValue(plane, out manager);

        // UltrasoundManager.Plane and UltrasoundDisplayController.Plane are separate enums, so map between them explicitly.
        private UltrasoundDisplayController.Plane DisplayPlane =>
            plane == Plane.Secondary ? UltrasoundDisplayController.Plane.Secondary : UltrasoundDisplayController.Plane.Primary;






        [Header("-----Active?-----", order = 0)]

        /// Rendering Ultrasound or not:
        /// This is like the On/Off switch on a US machine. Is usually just left on.
        [SerializeField]
        bool isUltrasoundActive = true;
        public bool Rendering
        {
            get
            {
                return isUltrasoundActive;
            }
            set
            {
                isUltrasoundActive = value;
                if (!isUltrasoundActive)
                    RenderEmptyImage();

            }
        }


        [Header("-----Insonation Plane Shape and Thickness-----", order = 3)]

        /// The width field defines the width of the top of the ultrasound probe's scanning plane area. This segment is centered
        /// at the probeCeneterPoint object and extends width/2 units to the positive and negative directions along the local x
        /// axis.
        [SerializeField]
        int insonationPlaneTopWidth_mm = 40;///world units (mm)
        public int Width { get { return insonationPlaneTopWidth_mm; } set { if (value <= 0) throw new System.ArgumentOutOfRangeException("Width must be greater than 0."); insonationPlaneTopWidth_mm = value; Rebuild = true; } }



        /// The depth field is used to define the depth of the ultrasoundï¿½s scanning plane. This is the maximum depth the 
        /// transducer can measure at. 
        /// Depth settings in ultrasound machines are measured in centimeters, not millimeters. 
        [SerializeField]
        int scanDepth_mm = 50;///world units (mm)
        public int Depth_cm { get { return scanDepth_mm / 10; } set { if (value <= 0) throw new System.ArgumentOutOfRangeException("Depth must be greater than 0."); scanDepth_mm = value * 10; Rebuild = true; } }

        /// The angle field defines the angle at which the scanning plane deviates from the direction perpendicular to the probe's
        /// contacting surface. An angle of 1 creates a scanning plane with nearly right angles, and angles nearer to 90 create almost 
        /// fanned out ultrasound scanning planes.
        [SerializeField]
        int angle = 5;///degrees
        public int Angle { get { return angle; } set { if (value < 1 || value > 120) throw new System.ArgumentOutOfRangeException("Angle must be in the range [1,120]."); angle = value; Rebuild = true; } }

        /// The resolution field defines the pixel resolution of the ultrasound image texture. At the moment only even parity, 
        /// square resolutions are allowed. This resolution may be expanded to allow for more easily scalable solutions.
        [SerializeField]
        private int textureResolutionPixels = 256;///pixels x pixels
        public int Resolution { get { return textureResolutionPixels; } }

        /// The beamThickness field defines the tolerance within which the ultrasound beam can travel in the direction perpendicular
        /// to the scanning plane. This gives a more realistic "flicker" to the ultrasound image as the independent RayCasts vary
        /// randomly from frame to frame.
        [SerializeField]
        float beamThickness_mm = 1;///world units (mm)
        public float BeamThickness_mm { get { return beamThickness_mm; } set { beamThickness_mm = value; Rebuild = true; } }
        public bool UseSpherecast = false;

        // "Raycast Up The Probe" is what we called the probeInternalOffsetMM.
        // This means that we start the ray casts a bit inside the probe body.
        // This is so we can insonate virtual objects under the real skin, which can be squishy.
        // this can happen when you press the ultrasound probe into the ballistic gel and shallow virtual anatomy overlaps the virtual ultrasound probe.
        // this is not an edge case - it happens frequently.
        // we set it here without serialization; 3 cm seems very adequate - you should never (I hope!) push the probe that far into the skin.
        private float raycastUptheProbe_mm = 30;



        /// Depth Attenuation is accomplished via an alpha channel overlay. When enabled, the ultrasound image gradually degrades and approaches black.
        /// The attenuation fades from 0 at the top (perfectly clear, no attenuation) to [attenuationFraction] at the bottom.
        /// An attenuationFraction of 1 means you can't see anything at the very bottom of the US image - its all black.
        /// A more realistic attenuationFraction is 0.3 to 0.5, which gives the effect of less brightness at the bottom of the image.
        [SerializeField]
        float depthAttenuationFraction;
        public float DepthAttenuationFraction { get { return depthAttenuationFraction; } set { depthAttenuationFraction = value; } }

        /// This refers to the "red sheet of light" in the 3D visualization.
        /// It gives users the option to render a dense and detailed US scanning plane, or a simpler version with 1/10th the vertices.
        /// The default is simple. If you need a very curved insonation plane (like for a curvelinear probe with a large angle) 
        /// then it might make sense to go with the detailed option to reduce tesselation of the insonation plane in the 3D visualization.
        bool renderSimpleScanningPlane = false;

        // This renders the US image on the insonation plane in the 3D visualization.
        // so the US image is rendered on top of the "red sheet of light".
        // Its not a toggle. It is an initialization. To toggle this feature, change the boolean and then rebuild.
        // This is a really cool feature that Andre Bigos added in 2018.
        [SerializeField]
        bool renderUltrasoundOntoScanningPlane = false;
        public bool RenderUltrasoundOntoScanningPlane { get { return renderUltrasoundOntoScanningPlane; } set { renderUltrasoundOntoScanningPlane = value; } }


        // finally: specular reflections. most implementations should benefit from specular reflections (from the light that illuminates the US image.)
        // sometimes, it just looks better for whatever reason. other implementations may not benefit. lets give you the option here:
        // Enable specular highlights on the ultrasound image material
        [SerializeField]
        bool specularHilights = true;
        public bool SpecularHilights { get { return specularHilights; } set { specularHilights = value; } }



        /// The rebuild field is a toggle that can be used during runtime to rebuild the ultrasound probe's scanning plane. It is
        /// useful in allowing the dynamic editing of the ultrasound probe's scanning plane width, depth, and angle.
        [SerializeField]
        bool rebuild = false;
        public bool Rebuild { set { rebuild = value; } }


        [Header("-----Layers you want to see in Ultrasound-----", order = 3)]
        // user setting for what anatomical layers you can see in the ultrasound
        public LayerMask ImageableLayers;

        // internal flag for air gaps, etc. Externally readable for other scripts. 
        bool probeIsFarFromSkin;
        public bool ProbeIsFarFromSkin { get { return probeIsFarFromSkin; } }

        float FarFromSkinSpherecastRadius_mm = 25;

        int MinimumFingerPressProbeFaceFSRValue = 400; // 0-1023. scaled output from microcontroller A/D. 


        [Header("-----Air Gap with Skin-----", order = 3)]
        public bool DrawAirGaps = true;
        public float MinimumAirGap_mm = 2;

        // for simulating air gap
        LayerMask SkinLayer;

        // an internal flag for rendering air gaps
        private bool airGapPresent = false;

        [Header("-----Realism Settings-----", order = 4)]

        /// The blur field lets users toggle on/off the blurring of the ultrasound screen. The ultrasound image is blurred using
        /// a custom shader attached to the camera looking at the ultrasound texture.
        [SerializeField]
        bool blur = false;
        public bool Blur { get { return blur; } set { blur = value; } }

        /// This toggles on/off the depth attenuation field. The field is an alpha transparency texture overlay placed in front of the ultrasound
        /// texture. When toggled off, the overlay is disabled.
        [SerializeField]
        bool depthAttenuation;
        public bool DepthAttenuation { get { return depthAttenuation; } set { depthAttenuation = value; } }

        /// This toggles on/off the "needle anisotropy" (applies to other objects too sometimes). 
        /// This happens when a large (unfavorable) incident angle makes the insonation waves bounce off the object sideways and away from the probe,
        /// which makes the object dissappear. this is common on needles and some anatomical structures, like nerves.
        [SerializeField]
        bool anisotropy;
        public bool Anisotropy { get { return anisotropy; } set { anisotropy = value; } }


        [Header("-----Noise Texture 1-----", order = 3)]
        // frequency is the overall granularity of the simplex gradient. This will effect all the Divisors equally.
        [SerializeField]
        [Range(0f, 1f)]
        float frequency1 = 0.4f;

        /// This gradient is used to rendered simplex noise within the ultrasound image. This gradient takes the result of the
        /// simplex class (a value [0,1]) and returns a color based on that calculated value.
        //[SerializeField]
        //Gradient texture1ColorGradient;

        /// These fields are used to distort the simplex noise (elongate it to lessen the effect of distance traveled in a specific
        /// axis by increasing the divisors) and dampen the extremes of the gradient (modify dampener).
        [SerializeField]
        Vector3 Divisor1 = new Vector3(0.2f, 0.2f, 0.2f);
        [SerializeField]
        [Range(-1f, 1f)]
        float dampener1 = .8f;

        // these are the colors of the black and white pixels that make up the ultrasound texture. 
        [SerializeField]
        [Range(0f, 1f)]
        float texture1MinBrightness = 0f;
        [SerializeField]
        [Range(0f, 1f)]
        float texture1MaxBrightness = 1f;

        [Header("-----Noise Texture 2-----", order = 3)]
        // frequency is the overall granularity of the simplex gradient. This will effect all the Divisors equally.
        [SerializeField]
        [Range(0f, 1f)]
        float frequency2 = 0.4f;

        /// This gradient is used to rendered simplex noise within the ultrasound image. This gradient takes the result of the
        /// simplex class (a value [0,1]) and returns a color based on that calculated value.
        //[SerializeField]
        //Gradient texture2ColorGradient;


        /// These fields are used to distort the simplex noise (elongate it to lessen the effect of distance traveled in a specific
        /// axis by increasing the divisors) and dampen the extremes of the gradient (modify dampener).
        [SerializeField]
        Vector3 Divisor2 = new Vector3(0.4f, 0.35f, 0.35f);
        [SerializeField]
        [Range(-1f, 1f)]
        float dampener2 = .35f;

        // these are the colors of the black and white pixels that make up the ultrasound texture. 
        [SerializeField]
        [Range(0f, 1f)]
        float texture2MinBrightness = 0f;
        [SerializeField]
        [Range(0f, 1f)]
        float texture2MaxBrightness = 1f;

        [Header("-----Background Noise Texture, or a Color-----", order = 3)]
        /// The background color is the color of all non-rendered elements. This color is typically black - but, when air gaps are enabled, 
        /// its useful to have it a bit lighter, like 0F0F0F - that way the background is differentiated from the dark void of an air gap.
        [SerializeField]
        Color backgroundColor;

        /// If you have the background texture enabled, this doesn't matter.
        [SerializeField]
        bool useBackgroundTexture = false;
        public bool UseBackgroundTexture { get { return useBackgroundTexture; } set { useBackgroundTexture = value; } }

        /// This gradient is used to rendered simplex noise within the ultrasound image. This gradient takes the result of the
        /// simplex class (a value [0,1]) and returns a color based on that calculated value.
        //[SerializeField]
        //Gradient BackgroundtextureColorGradient;

        // frequency is the overall granularity of the simplex gradient. This will effect all the Divisors equally.
        [SerializeField]
        [Range(0f, 1f)]
        float frequency0 = 0.4f;

        /// These fields are used to distort the simplex noise (elongate it to lessen the effect of distance traveled in a specific
        /// axis by increasing the divisors) and dampen the extremes of the gradient (modify dampener).
        [SerializeField]
        Vector3 Divisor0 = new Vector3(0.15f, 0.45f, 0.4f);
        [SerializeField]
        [Range(-1f, 1f)]
        float dampener0 = -0.1f;

        // these are the colors of the black and white pixels that make up the ultrasound texture. 
        [SerializeField]
        [Range(0f, 1f)]
        float backgroundMinBrightness = 0f;
        [SerializeField]
        [Range(0f, 1f)]
        float backgroundMaxBrightness = 1f;

        /// This multidimensional array stores data on the points used to generate the ultrasound images. It is populated when the
        /// scanning plane is built and is used in conjunction with the ever-moving center point to create the theoretical scanned
        /// image.
        Vector4[,] beamTrajectoryPoints;

        /// The depths are used primarily to define the attenuation overlay texture. However, this depth information can become
        /// rather useful in the future as more functionality is added.
        float[,] pixelDepthMap;

        /// 2D Array of boolean values listing whether or not a texture pixel is being rendered or not.
        bool[,] validUltrasoundRegion;


        /// <summary>
        /// stores the mapping between raycasts and their corresponding pixel positions on the ultrasound texture.
        /// Lists of Vector3s where:
        /// x component: represents the distance along the raycast(0 to 1)
        /// y component: x-coordinate in texture space
        /// z component: y-coordinate in texture space
        /// </summary>        
        List<Vector3>[] raycastPixelMappings;


        /// These transformation matrices are used to transform points between the ultrasound rendered texture and the corresponding
        /// point on the ultrasound scanning plane (in relation to the probe center point).
        Matrix4x4
            textureToScanningPlaneTransformPrimary,
            textureToScanningPlaneTransformSecondary,
            scanningPlaneToTextureTransformPrimary,
            scanningPlaneToTextureTransformSecondary;

        /// The currentUltrasoundMaterial field is used internally. The UltrasoundMaterial scripts attached to 
        /// objects the ultrasound needs to render are used and the current rendered ultrasound segment uses this field to minimize
        /// the amount of times the class is passed around. Eventually, the UltrasoundMaterial script will contain either a string
        /// parameter, a custom struct, or some other convenient way of passing off critical data to the UltrasoundManager.
        UltrasoundVisibility currentUltrasoundMaterial;

        /// This is the main ultrasound texture. It is what the ideal, non-blurred,
        /// non-attenuated, ultrasound image should be.
        Texture2D liveUltrasoundImage;

        /// This is the attenuation overlay. It is created when the scanning plane is
        /// rebuilt and is used to attenuate the ultrasound image.
        Texture2D attenuationDepthFalloffMask;

        /// This overlay obscures the sides of the ultrasound image that are not rendered. Eventually, this overlay will become a 
        /// background to the ultrasound texture to ensure full 100% accurate coverage of the sides of the ultrasound image. This will
        /// involve changing the main ultrasound rendered texture's GameObject's material to one that allows transparency.
        Texture2D beamBoundaryMask;

        /// This is the scanning plane that is created whenever the scanning plane is built. Its mesh is created vertex by vertex at 
        /// the time of its build. Although currently only visible in the inspector, this GameObject will eventually be accessible by
        /// users. Because it is created at runtime, it is much easier to provide users with a reference to the GameObject than 
        /// forcing them find it each time it is recreated during a rebuild.
        GameObject scanningPlane;


        Material insonationPlaneMaterial;
        Material renderedScanningPlaneMaterial;

        // The simualated ultrasound image is rendered on a square texture, taking up as much room as it can.
        // this helps us make a tall or wide ultrasound image on a square texture.
        // width_depth_ratio > 1 means a tall ultrasound image (like the linear RA probe). when fit inside a square, there will be excess black on either side of the simulated US image.
        // width_depth_ratio < 1 means a wide ultrasound image (like a curvilinear probe). when fit inside a square, there will be excess black on the bottom of the simulated US image.
        private float width_depth_ratio;

        [Header("-----Gameobject Assignments----", order = 3)]
        /// This is the ultrasoundProbe GameObject. Its primary function is to assign the scanning plane as a child of this object's
        /// transform to ensure simplicity in the scene hierarchy. This GameObject is currently only visible in the inspector, but
        /// will most like be made more accessible in later updates.
        [SerializeField] GameObject ultrasoundProbe;

        /// This GameObject is absolutely crucial. It is used to align the probe's scanning plane. The scanning plane is centered at
        /// this important GameObject, and aligned with its orientation.
        [SerializeField] GameObject probeFaceCenterPoint;
        public GameObject CenterOfProbeFace { get { return probeFaceCenterPoint; } }

        /// This GameObject is used to detect skin proximity. Its aligned at the back of the probe, where the cord comes out.
        [SerializeField] GameObject probeRearCenterPoint;
        public GameObject CenterOfProbeRear { get { return probeRearCenterPoint; } }

        /// The rendered ultrasound texture is visible in the inspector because of the ease of its assignment. This is the GameObject
        /// to which the ultrasound texture is added. The ultrasoundTextureCamera GameObject's camera component is pointed towards
        /// this GameObject. This is the basis for all ultrasound rendering.
        [SerializeField] GameObject ultrasoundImage;

        /// The attenuationOverlay GameObject is toggled on/off as directed by the depthAttenuation boolean. This overlay obscures the
        /// renderedUltrasoundTexture GameObject as it is between it and the ultrasoundTextureCamera. The non-unity alpha values of the
        /// texture's pixels ensure it that the rendered texture behind it is visible in the ultrasound screen, and is only partially
        /// obscured by the overlay.
        [SerializeField] GameObject attenuationOverlay;

        /// The edge overlay is used as another overlay. However, unlike the attenuation overlay, this overlay has either zero or unity
        /// alpha values. The edges of the rendered ultrasound screen are rendered the color edgeColor and the section of the texture
        /// that would normally obscure the rendered ultrasound texture's ultrasound area are assigned alpha values of zero.
        /// In the future, this overlay will become a background image, and the rendered ultrasound image will use alpha values of
        /// zero in the non-rendered area to have the edge colors come better.
        [SerializeField] GameObject edgeOverlay;

        /// This is the GameObject that contains the camera that captures all the rendered ultrasound textures, overlays, and applies
        /// all blur effects. It is visible in the inspector, however, this object should not be moved, modified, or changed in any
        /// way by  the user.
        [SerializeField] GameObject ultrasoundTextureCamera; // this is needed here to access a blur component, and adjust unfinished rotate and zoom features that we wont use.


        // these are references to two gameobjects that contain line renderers. 
        // They draw a midline and a sidedness line on the notch side of the insonation plane in the 3D visualization.
        [SerializeField] GameObject InsonationPlane_EdgeLine_NotchSide;
        [SerializeField] GameObject InsonationPlane_MidLine;
        [SerializeField] GameObject InsonationPlane_EdgeLine_FlatSide;

        // these two gameobjects are for the dynamic cm depth scale, which are composed of sprite rectangles and TMP text arranged under Ultrasound Display Components.
        // The large graduation has a dash with a TMP text for a number, the small graduation is just a tic mark.
        // The DepthScale is the gameobject that holds them.
        // The FirstTic marks the position of the first graduation in the top right corner of the US image display (always 0 cm); 
        // the LastTic marks the position of the last graduation in the bottom rigth corner of the US image display (like 5 cm).
        [SerializeField] GameObject LargeGraduation, SmallGraduation;
        [SerializeField] Transform DepthScaleGraduations;
        [SerializeField] Transform LocationOfFirstDepthGraduation, LocationOfLastDepthGraduation;

        [SerializeField] bool FingerPressEnabled = true; // variable becauase not all probes have FSRs. 
        [SerializeField] GameObject FlatSideFingerArtifact;
        [SerializeField] GameObject RidgeSideFingerArtifact;

        // These two LineRenderers draw the outer edges of the ultrasound image on the display.
        // They are children of Ultrasound Display Components, which is the overlay layer - and the overlay is NEVER
        // mirrored by Reverse Image (only the image layer is). So each of these lines is permanently pinned to one
        // side of the viewer's screen and never moves when the user flips the image.
        // That is why they are named for the display side and not for the probe side: their COLOR is the only thing
        // that carries probe-side meaning, and the color is what swaps on Reverse Image. See ApplyEdgeLineSidedness().
        [SerializeField, FormerlySerializedAs("ridgeSideEdgeLine")] private LineRenderer displayLeftEdgeLine;
        [SerializeField, FormerlySerializedAs("flatSideEdgeLine")] private LineRenderer displayRightEdgeLine;

        // The two edge line colors. We read them off the 3D insonation plane edge cubes rather than duplicating them
        // in the inspector, so that each plane has exactly one place its color convention is defined.
        private Material dotSideEdgeLineMaterial;  // the ridge/notch side, which is the side the sidedness dot is on
        private Material flatSideEdgeLineMaterial; // the side without the dot

        // Three corners of the ultrasound TEXTURE in world space, used to build the texture-to-world basis below.
        // Careful, these are texture-space corners, not screen corners. Texture space is mirrored relative to what the
        // viewer sees, so the texture's left edge (beam 0, the probe's flat side) is the edge that appears on the
        // viewer's RIGHT. That is why the "Left" corners below are read from displayRightEdgeLine.
        private Vector3 worldBottomLeft;
        private Vector3 worldTopLeft;
        private Vector3 worldBottomRight;


        #endregion Declarations


        #region Initializations

        /// <summary>
        /// In Awake() we create the static singleton reference UltrasoundManager.ME. It also destroys every unnecessary instance of 
        /// the ultrasound ensuring the single instance.
        /// </summary>
        private void Awake()
        {
            _instances[plane] = this;
            //if (ME != null)
            //    Destroy(ME);
            //ME = this;   
        }




//public class UltrasoundManager : MonoBehaviour {

//}







        /// <summary>
        /// In start, we build the scanning plane as it is described in the inspector at the beginning of runtime.
        /// </summary>
        private void Start()
        {
            // Set the skin layermask 
            SkinLayer = LayerMask.GetMask("Skin");

            // Load the following from the /Resources folder
            insonationPlaneMaterial = (Material)Resources.Load("Ultrasound Red Sheet of Light Material", typeof(Material));
            LargeGraduation = (GameObject)Resources.Load("Large Graduation Mark with Numeral", typeof(GameObject));
            SmallGraduation = (GameObject)Resources.Load("Small Graduation Mark", typeof(GameObject));

            // Load the following from the Scene
            if (ultrasoundProbe == null) ultrasoundProbe = GameObject.Find("Steerable Scanning Plane");
            if (probeFaceCenterPoint == null) probeFaceCenterPoint = GameObject.Find("Probe Face Center");
            if (probeRearCenterPoint == null) probeRearCenterPoint = GameObject.Find("Probe Rear Center");
            if (ultrasoundImage == null) ultrasoundImage = GameObject.Find("Ultrasound Image");
            if (attenuationOverlay == null) attenuationOverlay = GameObject.Find("Ultrasound Image Overlay for Attenuation");
            if (edgeOverlay == null) edgeOverlay = GameObject.Find("Ultrasound Image Overlay for Edges");
            if (ultrasoundTextureCamera == null) ultrasoundTextureCamera = GameObject.Find("Ultrasound Image Camera");
            if (InsonationPlane_EdgeLine_NotchSide == null) InsonationPlane_EdgeLine_NotchSide = GameObject.Find("Line on Insonation Plane Edge - Ridge Side");
            if (InsonationPlane_EdgeLine_FlatSide == null) InsonationPlane_EdgeLine_FlatSide = GameObject.Find("Line on Insonation Plane Edge - Flat Side");
            if (InsonationPlane_MidLine == null) InsonationPlane_MidLine = GameObject.Find("Midline on Insonation Plane");
            if (DepthScaleGraduations == null) DepthScaleGraduations = GameObject.Find("US Depth Scale Graduations").transform;
            if (LocationOfFirstDepthGraduation == null) LocationOfFirstDepthGraduation = GameObject.Find("US Display Position of First Graduation Mark").transform;
            if (LocationOfLastDepthGraduation == null) LocationOfLastDepthGraduation = GameObject.Find("US Display Position of Last Graduation Mark").transform;
            if (FlatSideFingerArtifact == null) FlatSideFingerArtifact = GameObject.Find("Flat Side Finger Artifact");
            if (RidgeSideFingerArtifact == null) RidgeSideFingerArtifact = GameObject.Find("Ridge Side Finger Artifact");

            // We're using the initial positions of the line renderers for the color coded edge lines
            // to define their own coordinate system, lets save some initial positions.
            if (displayRightEdgeLine != null && displayLeftEdgeLine != null)
            {
                 // point 0 of each line is the shallow (probe face) end, point 1 is the deep end.
                 worldBottomLeft = displayRightEdgeLine.GetPosition(1);
                 worldTopLeft = displayRightEdgeLine.GetPosition(0);
                 worldBottomRight = displayLeftEdgeLine.GetPosition(1);
            }
            
            // now that we have everything loaded, lets have some fun.
            BuildUltrasoundScanningPlane();
            DrawMidline_and_Edges_of_3D_InsonationPlane();
        }

        #endregion Initializations

        #region Build Scanning Plane

        /// <summary>
        /// This is where all the creation occurs:
        /// - the ultrasound plane - i.e., the set of points we use for raycasts - is created 
        /// - textures are created and initialized
        /// - the insonation plane in the 3D visualization is created (via method call)
        /// - all overlays are built, like the overlay we use for depth attenuation (via method call)
        /// 
        /// Its worth noting that we are modeling the insonation plane with parallel rays, forming a thin rectangular cross section.
        /// real ultrasound does not have perfectly parallel beams, and the cross section of the beams is hourglass-shaped.
        ///
        /// It is called from within Start(), and can be called at any
        /// time during runtime setting the rebuild flag "should rebuild scan plane" to true. This method destroys or zeros all elements and allows the new scanning
        /// plane to be built sans errors.
        /// 
        /// 
        /// </summary>
        void BuildUltrasoundScanningPlane()
        {

            // first lets make sure the requested texture is something that works for this implementation.
            // note - in Unity, for best performance, texture2D resolution should be in 32, 64, 128, 256, 512, 1024, or 2048 pixels and square.
            // so a resolution of 256 will run a little faster than a resolution of 250, even though it has more pixels.
            if (textureResolutionPixels < 32)
            {
                Debug.Log("Ultrasound texture resolution cannot be less than 32x32. The resolution has been forced to 32x32.");
                textureResolutionPixels = 2;
            }
            if (textureResolutionPixels > 2048)
            {
                Debug.Log("Ultrasound texture resolution is capped at 2048x2048. The resolution has been forced to 2048x2048.");
                textureResolutionPixels = 2048;
            }
            if (textureResolutionPixels % 2 != 0)
            {
                Debug.Log("Only even valued resolutions are accepted. The resolution " + textureResolutionPixels + "x" + textureResolutionPixels + " is not an acceptable resolution. " +
                "The resolution has been forced to " + (textureResolutionPixels + 1) + "x" + (textureResolutionPixels + 1) + ".");
                textureResolutionPixels += 1;
            }
            if (scanningPlane != null)
                Destroy(scanningPlane);


            // this block defines the shape of the insonating plane.
            // this is in ultrasound insonation plane space.
            // the origin is at the middle of the insonating edge, corresponding to the middle of the US probe's face.
            // x is width, +x to the right
            // y is depth, +y down
            // depth is mm and is the max range of a raycast.
            // the angle is the spread of the edge raycast, from perpendicualr to the width line.
            // given the width (width of the US probe transducer), the depth, and the angle, the dimensions of the insonation plane is determined.

            // Ultrasound machines have depth settings in cm. scanDepth_mm should be set externally through the Depth_cm setter.
            // This is a doublecheck to make sure scanDepth_mm is in multiples of 10.  
            if (scanDepth_mm % 10 > 0)
            {
                scanDepth_mm = scanDepth_mm + 10 - (scanDepth_mm % 10);
                Debug.Log("Depth must be in cm increments. Changed scanDepth_mm to " + scanDepth_mm + " mm");
            }

            Mathf.Clamp(angle, 1, 120); // protect suitable range for angle.
            float spreadAngle_radians = Mathf.Deg2Rad * angle; // degrees

            float widthDBU_mm = scanDepth_mm * Mathf.Sin(spreadAngle_radians); // mm - this is that little extra bit on either side because it fans out 
            float depthDBU_mm = scanDepth_mm * Mathf.Cos(spreadAngle_radians); // mm - 

            float insonationPlaneBottomWidth_mm = widthDBU_mm * 2 + insonationPlaneTopWidth_mm; // widest at the bottom because it fans out

            // The focal point is where all the raycasts would join together 
            Vector4 focalPointU = new Vector4(0, (-insonationPlaneTopWidth_mm / 2) / Mathf.Tan(spreadAngle_radians), 1);

            // bU is a point on the bottom left corner of the insonation area - the termination of the leftmost raycast
            Vector4 bU = new Vector4(-insonationPlaneTopWidth_mm / 2 - widthDBU_mm, depthDBU_mm, 1);

            // dU is a point on the left edge of the insonation face - the beginning of the leftmost raycast 
            Vector4 dU = new Vector4(-insonationPlaneTopWidth_mm / 2, 0, 1);

            // focalPointU, bU, and dU are Vector4 with the fourth element (w) set to zero by default. This is setup for use in 4D transformation matrices.

            // next, define pixel space. 
            // this will be a square 2D array of RGB values that can be directly uploaded to a texture 2D.
            // we are using a square-shaped texture2D for performance reasons (see unity documentation for Texture2D)
            float resolutionT_pixels = textureResolutionPixels - 1; // address in pixel space start with 0. if resolution is set to 256, the array index goes from 0 to 255.

            // The simualated ultrasound image is rendered on a square texture, taking up as much room as it can.
            width_depth_ratio = scanDepth_mm / insonationPlaneBottomWidth_mm;
            // width_depth_ratio > 1 means a tall ultrasound image (like the linear RA probe). when fit inside a square, there will be excess black on either side of the simulated US image.
            // width_depth_ratio < 1 means a wide ultrasound image (like a curvilinear probe). when fit inside a square, there will be excess black on the bottom of the simulated US image.

            float textureDepth_pixels;  // number of pixels mapped to the depth of the simulated ultrasound image
            float textureWidth_pixels;  // number of pixels mapped to the widest part (the bottom of the fan) of the simulated ultrasound image

            if (width_depth_ratio > 1) // tall US image, like linear probe in RA and CVA with the default settings
            {
                textureDepth_pixels = resolutionT_pixels; // the image will take up the entire height
                textureWidth_pixels = resolutionT_pixels / width_depth_ratio; // width will contain some blank space on either side
            }
            else // wide US image, like you may see from a curvilinear probe
            {
                textureWidth_pixels = resolutionT_pixels; // the image will take up the entire width
                textureDepth_pixels = resolutionT_pixels * width_depth_ratio; // and the height contains some blank space at the bottom
            }
            float halfResolution_pixels = resolutionT_pixels / 2;

            // these transformation matrices are used to transform points between the ultrasound rendered texture (in pixel space)
            // and the correspoinding point on the US scanning plane (in insonation plane space, as defined above)
            // transformationMatrixTToU1 -> going from texture space to ultrasound scanning plane space
            textureToScanningPlaneTransformPrimary =
                new Matrix4x4(new Vector4(1, 0), new Vector4(0, 1), new Vector4(-halfResolution_pixels, 0, 1), Vector4.zero);

            textureToScanningPlaneTransformSecondary =
                new Matrix4x4(new Vector4(insonationPlaneBottomWidth_mm / textureWidth_pixels, 0), new Vector4(0, scanDepth_mm / textureDepth_pixels), Vector4.zero, Vector4.zero);

            scanningPlaneToTextureTransformPrimary =
                new Matrix4x4(new Vector4(textureWidth_pixels / insonationPlaneBottomWidth_mm, 0), new Vector4(0, textureDepth_pixels / scanDepth_mm), new Vector4(0, 0, 1), Vector4.zero);

            scanningPlaneToTextureTransformSecondary =
                new Matrix4x4(new Vector4(1, 0), new Vector4(0, 1), new Vector4(halfResolution_pixels, 0, 1), Vector4.zero);

            // now that we have the shape of the insonation plane established in insonation plane space and in texture pixel space,
            // we build up the structure to be used by the raycasts in scanning plane space 
            Vector4 bT = scanningPlaneToTextureTransformSecondary * (scanningPlaneToTextureTransformPrimary * bU);
            Vector4 dT = scanningPlaneToTextureTransformSecondary * (scanningPlaneToTextureTransformPrimary * dU);
            Vector4 focalPointT = scanningPlaneToTextureTransformSecondary * (scanningPlaneToTextureTransformPrimary * focalPointU);


            List<Vector4> startPointsU = new List<Vector4>();
            List<Vector4> endPointsU = new List<Vector4>();
            List<Vector4> startPointsT = new List<Vector4>();
            List<Vector4> endPointsT = new List<Vector4>();
            Vector4 p0S = dT; // position of the start of the ray - upper left corner
            Vector4 p0E = bT; // position of the end of the ray - lower left corner

            float theta0 = spreadAngle_radians; // initialization.
            startPointsT.Add(p0S);
            endPointsT.Add(p0E);

            // here we are building up the raycast start and endpoints we need in TEXTURE SPACE -
            // because there is no need to create raycasts smaller than one pixel of the texture.
            while (true)
            {
                Vector4 p1E = p0E + new Vector4(Mathf.Cos(theta0), Mathf.Sin(theta0));
                Vector4 p1S;
                //if (!curved)
                p1S = IntersectYEquals0(p1E, focalPointT);
                //else
                //    p1S = p1E + Vector4.Normalize(focalPointT - p1E) * distanceT;
                float distance = Vector4.Distance(p1E, p1S);
                float lerpValue = textureDepth_pixels / distance;
                p1E = Vector4.Lerp(p1S, p1E, lerpValue);
                endPointsT.Add(p1E);
                startPointsT.Add(p1S);
                p0S = p1S;
                p0E = p1E;
                Vector2 dir = p1E - p1S;
                theta0 = Vector2.Angle(dir, Vector2.up) * Mathf.Deg2Rad;
                if (halfResolution_pixels - p1E.x <= 1)
                {
                    if (halfResolution_pixels - p1E.x > .5)
                    {
                        p1E = p0E + new Vector4(Mathf.Cos(theta0) / 2, Mathf.Sin(theta0) / 2);
                        p1S = IntersectYEquals0(p1E, focalPointT);
                        distance = Vector4.Distance(p1E, p1S);
                        lerpValue = textureDepth_pixels / distance;
                        p1E = Vector4.Lerp(p1S, p1E, lerpValue);
                        endPointsT.Add(p1E);
                        startPointsT.Add(p1S);
                    }
                    break;
                }
            }
            for (int x = startPointsT.Count - 1; x >= 0; x--)
            {
                Vector4 sPX = startPointsT[x];
                sPX.x = halfResolution_pixels + (halfResolution_pixels - sPX.x);
                startPointsT.Add(sPX);
                Vector4 ePX = endPointsT[x];
                ePX.x = halfResolution_pixels + (halfResolution_pixels - ePX.x);
                endPointsT.Add(ePX);
            }


            // now that we have all the starting and ending points of each ray in texture space,
            // we build the corresponding starting and ending points in scanning plane space. 
            for (int x = 0; x < startPointsT.Count; x++)
            {
                startPointsU.Add(textureToScanningPlaneTransformSecondary * (textureToScanningPlaneTransformPrimary * startPointsT[x]));
                endPointsU.Add(textureToScanningPlaneTransformSecondary * (textureToScanningPlaneTransformPrimary * endPointsT[x]));
            }

            // we're using a single element in a Vector4 array to hold the starting and ending points of the raycast,
            // as well as their correspoinding locations on the texture.
            beamTrajectoryPoints = new Vector4[4, endPointsT.Count];
            for (int x = 0; x < startPointsT.Count; x++)
            {
                beamTrajectoryPoints[0, x] = startPointsT[x];
                beamTrajectoryPoints[1, x] = endPointsT[x];
                beamTrajectoryPoints[2, x] = startPointsU[x];
                beamTrajectoryPoints[3, x] = endPointsU[x];
            }

            raycastPixelMappings = new List<Vector3>[endPointsT.Count];
            for (int x = 0; x < raycastPixelMappings.Length / 2; x++)
            {
                List<Vector3> currentLineAnalysis = new List<Vector3>();
                float xPixS = startPointsT[x].x;
                float yPixS = startPointsT[x].y;
                float xPixE = endPointsT[x].x;
                float yPixE = endPointsT[x].y;
                float xPix; // = xPixS;
                float yPix = yPixS;
                currentLineAnalysis.Add(new Vector3(0, xPixS, yPixS));
                float dx = Mathf.Abs(xPixE - xPixS);
                float dy = yPixE - yPixS;
                float m = dy / dx;
                float dist; //  = 0;
                if ((int)xPixS != (int)xPixE)
                {

                    xPix = (int)xPixS;
                    dist = Mathf.Abs((xPix - xPixS) / dx);
                    yPix = dist * dy + yPix;
                    currentLineAnalysis.Add(new Vector3(dist, xPix, yPix));
                    while (true)
                    {
                        xPix--;
                        yPix += m;
                        dist += 1 / dx;
                        if ((int)xPixE >= (int)(xPix))
                        {
                            break;
                        }
                        currentLineAnalysis.Add(new Vector3(dist, xPix, yPix));
                    }
                }
                currentLineAnalysis.Add(new Vector3(1, xPixE, yPixE));
                raycastPixelMappings[x] = currentLineAnalysis;
            }


            //attenuationDepthFalloffMask = new Texture2D(textureResolutionPixels, textureResolutionPixels);
            attenuationDepthFalloffMask = new Texture2D(textureResolutionPixels, textureResolutionPixels, TextureFormat.RGBA32, false);
            attenuationDepthFalloffMask.filterMode = FilterMode.Point;
            ///beamBoundaryMask = new Texture2D(textureResolutionPixels, textureResolutionPixels);
            ///beamBoundaryMask.filterMode = FilterMode.Point;

            beamBoundaryMask = new Texture2D(textureResolutionPixels, textureResolutionPixels, TextureFormat.RGBA32, false);
            beamBoundaryMask.filterMode = FilterMode.Point;

            for (int x = raycastPixelMappings.Length / 2; x < raycastPixelMappings.Length; x++)
            {
                List<Vector3> oppositeAnalysis = new List<Vector3>();
                List<Vector3> leftAnalysis = raycastPixelMappings[raycastPixelMappings.Length - x - 1];
                for (int y = 0; y < leftAnalysis.Count; y++)
                {
                    oppositeAnalysis.Add(new Vector3(leftAnalysis[y].x, halfResolution_pixels + (halfResolution_pixels - leftAnalysis[y].y), leftAnalysis[y].z));
                }
                raycastPixelMappings[x] = oppositeAnalysis;
            }

            // create the 2D array of pixels that will be the simulated ultrasound image
            // Texture2Ds dimensions should be powers of two on both width and height for performance reasons.
            liveUltrasoundImage = new Texture2D(textureResolutionPixels, textureResolutionPixels);
            for (int x = 0; x < textureResolutionPixels; x++)
            {
                for (int y = 0; y < textureResolutionPixels; y++)
                {
                    liveUltrasoundImage.SetPixel(x, y, Color.clear);
                }
            }

            // finally: specular reflections. most implementations should benefit from specular reflections (from the light that illuminates the US image.)
            // sometimes, it just looks better for whatever reason. other implementations may not benefit. lets give you the option here:
            // Enable specular highlights on the ultrasound image material
            Material usMaterial = ultrasoundImage.GetComponent<MeshRenderer>().sharedMaterial;
            if (specularHilights) usMaterial.SetFloat("_SpecularHighlights", 0.0f); // 0 = enabled, 1 = disabled (Unity logic!)
            else usMaterial.SetFloat("_SpecularHighlights", 1.0f); // 0 = enabled, 1 = disabled (Unity logic!)
            usMaterial.shader = usMaterial.shader; // Force recompile because Unity = Unity.



            // Build texture-to-world transform from manually placed LineRenderer corner points.
            // point 0 of each line is the start of the beam (shallow, at the probe face), point 1 is the end of the beam (deep).
            // beams run from the flat side to the ridge side, so beam 0 is the flat side edge and the last beam is the ridge side edge.
            // the flat side of the probe appears on the viewer's right, hence displayRightEdgeLine gets beam 0.
            if (displayRightEdgeLine != null && displayLeftEdgeLine != null)
            {

                Vector3 xBasis = (worldBottomRight - worldBottomLeft) / textureResolutionPixels;
                Vector3 yBasis = (worldTopLeft - worldBottomLeft) / textureResolutionPixels;

                System.Func<Vector4, Vector3> texToWorld = (texPoint) =>
                    worldBottomLeft + xBasis * texPoint.x + yBasis * texPoint.y;

                displayRightEdgeLine.SetPosition(0, texToWorld(startPointsT[0]));
                displayRightEdgeLine.SetPosition(1, texToWorld(endPointsT[0]));

                int lastIdx = startPointsT.Count - 1;
                displayLeftEdgeLine.SetPosition(0, texToWorld(startPointsT[lastIdx]));
                displayLeftEdgeLine.SetPosition(1, texToWorld(endPointsT[lastIdx]));

                // we just regenerated the edge lines, so re-apply their sidedness colors.
                // without this, changing depth, width, or angle after a Reverse Image would quietly drop the swap.
                ApplyEdgeLineSidedness();
            }




            CreateInsonationPlaneFor3DVisualization();

            SetAttenuationDepths();

            Create_cm_DepthScale();
        }
        #endregion Build Scanning Plane

        #region Depth Scale
        /// <summary>
        /// This function builds up the large and small tic marks that make up the cm depth graduations on the right edge of the ultrasound image display.
        /// This works in cm increments. Each cm has a numbered hash mark on the right edge of the US display. The bottom hash mark is also labeled "cm".
        /// There are small tic marks in between the numbered hash marks that represent half-centimeters.
        /// </summary>
        private void Create_cm_DepthScale()
        {
            // first, delete the old scale.
            foreach (Transform child in DepthScaleGraduations) Destroy(child.gameObject);

            // so what is the depth?  That is scanDepth_mm
            float sizeOfGraduation_cm_Scale = Vector3.Distance(LocationOfFirstDepthGraduation.position, LocationOfLastDepthGraduation.position); // unity units

            // edge case: when the insonation plane is wider that it is tall, when fit inside a square texture it will not go all the way to the bottom.
            // in this edge case, move the LocationOfLastDepthGraduation up to the level of the bottom of the insonation plane.
            // width_depth_ratio > 1 means a tall ultrasound image (like the linear RA probe). when fit inside a square, there will be excess black on either side of the simulated US image.
            // nothing to do here, the entire scale is already mapped vertically.
            // width_depth_ratio < 1 means a wide ultrasound image (like a curvilinear probe). when fit inside a square, there will be excess black on the bottom of the simulated US image.
            // when width_depth_ratio < 1, we must account for that extra space at the bottom of the texture because it is not part of the scale.
            if (width_depth_ratio < 1)
            {
                sizeOfGraduation_cm_Scale = sizeOfGraduation_cm_Scale * width_depth_ratio;
            }

            // to get the number of cm graduations, take the scaleDistace and divide by scanDepth_mm / 10.
            float cm_graduation_separation = (sizeOfGraduation_cm_Scale / (scanDepth_mm / 10)); // unity units
            float numberOfLargeGraduations = 1 + (sizeOfGraduation_cm_Scale / cm_graduation_separation);
            for (int n = 0; n < numberOfLargeGraduations; n++)
            {
                float graduationPosition_y = -(n * cm_graduation_separation) + DepthScaleGraduations.position.y;
                Vector3 graduationPosition = LocationOfFirstDepthGraduation.position;
                graduationPosition.y = graduationPosition_y;

                // reference the gameobject to reach a text component to write a number
                GameObject newLargeGraduation = Instantiate(LargeGraduation, graduationPosition, Quaternion.identity, DepthScaleGraduations);
                newLargeGraduation.GetComponentInChildren<TMP_Text>().text = n.ToString("");

                // apply a units label to the last graduation numeral - add a "cm" to the end of the string
                if (n == numberOfLargeGraduations - 1)
                {
                    newLargeGraduation.GetComponentInChildren<TMP_Text>().text = n.ToString("") + "<size=80%> cm</size>"; //dropping the font size for the cm seems to be less obtrusive on the screen
                }


                //  if (n == 0) Debug.Log(newLargeGraduation.GetComponentInChildren<TMP_Text>().gameObject.GetComponent<RectTransform>().localPosition);
                if (n == 0) newLargeGraduation.GetComponentInChildren<TMP_Text>().gameObject.GetComponent<RectTransform>().localPosition = new Vector3(-0.7f, -0.2f, 0);
                if (n == numberOfLargeGraduations - 1) newLargeGraduation.GetComponentInChildren<TMP_Text>().gameObject.GetComponent<RectTransform>().localPosition = new Vector3(-0.7f, 0.2f, 0);
                // if (n == numberOfLargeGraduations - 1) newLargeGraduation.GetComponentInChildren<TMP_Text>().gameObject.rectTransform.y = 0.2f;

            }

            // now for the smaller graduations that represent 0.5 cm between the cm tic marks.
            float numberOfSmallGraduations = sizeOfGraduation_cm_Scale / cm_graduation_separation;
            for (int n = 0; n < numberOfSmallGraduations; n++)
            {
                float graduationPosition_y = -(n * cm_graduation_separation) + DepthScaleGraduations.position.y;
                Vector3 graduationPosition = LocationOfFirstDepthGraduation.position;
                graduationPosition.y = graduationPosition_y - (cm_graduation_separation / 2);
                Instantiate(SmallGraduation, graduationPosition, Quaternion.identity, DepthScaleGraduations);
            }


        }
        #endregion Depth Scale

        #region Update Loop 
        /// <summary>
        /// In update, if the ultrasound plane has been requested to be rebuilt, that is, the "shouldRebuildScanPlane" flag in the inspector has
        /// been set to true, the ultrasound scanning plane will be rebuilt. In update, we also perform the scanning of the
        /// ultrasound.
        /// </summary>
        private void Update()
        {
            // first, check for the rebuild flag. if its true, call the function that creates the scanning plane and set the flag back to false.
            if (rebuild)
            {
                rebuild = false;
                BuildUltrasoundScanningPlane();
                DrawMidline_and_Edges_of_3D_InsonationPlane();
            }

            // if the ultrasound is not even active then bail out of this script early before scanning or rendering occurs.
            if (!isUltrasoundActive)
                return;

            // if were here, ultrasound is active. First, call the function that detects the skin proximity and
            // controls the finger artifact.
            DetectSkin_and_FingerPress();

            // if the probe is close enough to the skin, then build the image with raycasts.
            // if the probe is far from the skin - don't raycast at all! you can't scan anything but finger artifacts and we do that separately.
            if (!probeIsFarFromSkin)
            {
                Scan();
                RecordPublishedScan(PublishedPoseEpoch);
            }
        }

        /// <summary>
        /// Deterministically rebuild the ultrasound texture from the current,
        /// already-synchronized physics pose. This is the producer half of the
        /// image/pose handshake used by bounded automated capture. It does not
        /// move the probe or infer a target.
        /// </summary>
        public bool PublishCurrentPose(long poseEpoch)
        {
            if (!isActiveAndEnabled || !isUltrasoundActive) return false;
            Physics.SyncTransforms();
            DetectSkin_and_FingerPress();
            if (probeIsFarFromSkin) return false;
            Scan();
            RecordPublishedScan(poseEpoch);
            return true;
        }

        private void RecordPublishedScan(long poseEpoch)
        {
            PublishedPoseEpoch = poseEpoch;
            PublishedUnityFrame = Time.frameCount;
            PublishedScanSerial++;
            if (pendingPublishedScanProvenance != null)
            {
                pendingPublishedScanProvenance.poseEpoch = PublishedPoseEpoch;
                pendingPublishedScanProvenance.producerUnityFrame = PublishedUnityFrame;
                pendingPublishedScanProvenance.producerScanSerial = PublishedScanSerial;
                LastPublishedScanProvenance = pendingPublishedScanProvenance;
                pendingPublishedScanProvenance = null;
            }
        }
        #endregion Update Loop 

        /// <summary>
        /// Returns the intersection point of the line connecting two points, and the y=0
        /// axis. Although the input parameters are Vector4s, the only components that are used to find the intersection point
        /// are the x and y components of the Vector4s. Essentially, the Vector4 is treated as a Vector2 and the intersection 
        /// point is calculated accordingly, and returned as a Vector4 (with the z component = 1).
        /// </summary>
        /// <param name="point1">The first of two points to calculate the intersection of.</param>
        /// <param name="point2">The second of two points to calculate the intersection of.</param>
        /// <returns>The intersection of the line connecting the two points, and the y = 0 lines.</returns>
        Vector4 IntersectYEquals0(Vector4 point1, Vector4 point2)
        {
            float y1 = point1.y;
            float x1 = point1.x;
            float y2 = point2.y;
            float x2 = point2.x;
            float x = -y1 * (x2 - x1) / (y2 - y1) + x1;
            float y = 0;
            return new Vector4(x, y, 1);
        }

        /// <summary>
        /// The DetectSkin_and_FingerPress method determines if the probe is far from the skin.
        /// If it is, it reads the probe face pressure sensors and turns on the corresponding finger press artifact.
        /// This is to enable users to hold the probe away from the patient and press on one edge with their finger to determine
        /// if they are holding the probe correctly. Its a common substitute for feeling the sidedness ridge.
        /// </summary>
        ///
        void DetectSkin_and_FingerPress()
        {
            // is ProbeIsFarFromSkin?
            // we shoot a large sphere (same diameter as the width of the probe) from the back to the front face of the probe looking for the skin.
            // probeIsFarFromSkin is used by Update() to Scan() or not.
            RaycastHit farfromskinhit;
            probeIsFarFromSkin = !(Physics.SphereCast(probeRearCenterPoint.transform.position, FarFromSkinSpherecastRadius_mm,
                CenterOfProbeFace.transform.position - probeRearCenterPoint.transform.position, out farfromskinhit,
                Vector3.Distance(CenterOfProbeFace.transform.position, probeRearCenterPoint.transform.position), SkinLayer));

            // Turn off the finger artifacts by default.
            RidgeSideFingerArtifact.SetActive(false);
            FlatSideFingerArtifact.SetActive(false);

            // The rest of the method pertains to only probes and planes that have FSRs, like the primary plane in the default probe.
            // We determine if the right or left finger artifact is visible based on the right and left force sensors.
            // We dont have FSRs for biplane so this boolean is set to false in secondary plane.
            // And if you are running a probe that does not have FSRs, set to false.
            if (!FingerPressEnabled) return;

            // But first, if we are close to the skin, we said we will assume no finger artifacts: leave them off and bail out now.
            if (!probeIsFarFromSkin) return;

            // if the probe is indeed far from the skin, we can test for a finger artifact.
            // we need to determine which one. which Finger artifact do we show, left or right? The default is neither.
            // now read the microcontroller's values for the force sensitive resistors (FSRs) that are behind the face of the simulated US probe.
            // FSR values are read by the microcontroller's onboard A/D converter.
            // First we have to test if Microcontroller exists; we can't assume all sims have a whitebox and microcontroller...
            if (Microcontroller_Manager.ME != null)
            {
                float probeFaceFlatSideFSR = Microcontroller_Manager.ME.ProbeFaceFSR_FlatSide; // dimensionless. 0-1023 output from arduino AD.
                float probeFaceRidgeSideFSR = Microcontroller_Manager.ME.ProbeFaceFSR_RidgeSide;// dimensionless. 0-1023 output from arduino AD.
                float bothProbeFaceFSRs = probeFaceFlatSideFSR + probeFaceRidgeSideFSR;// dimensionless. max value is 510.

                // test for a threshold. we want an obvious finger press without picking up noise 
                // a sensible threshold is 400
                if (bothProbeFaceFSRs > MinimumFingerPressProbeFaceFSRValue)
                {
                    // if all the force is on the flat side,  probeFSR_Flat_to_Ridge = 0.
                    // if all the force is on the ridge side, probeFSR_Flat_to_Ridge = 1.
                    // if the force is split evenly between flat and ridge side, probeFSR_Flat_to_Ridge = 0.5.
                    float probeFSR_Flat_to_Ridge = probeFaceRidgeSideFSR / bothProbeFaceFSRs;

                    // the finger press will be a strong bias towards one side or the other.
                    // if the force is a balance of both, just dont show a finger artifact at all.
                    // Also, if the display is flipped, then reverse the logic of the flat and ridge side.
                    if (UltrasoundDisplayController.ME.DotOnRight == false)
                    {
                        if (probeFSR_Flat_to_Ridge < 0.25f) FlatSideFingerArtifact.SetActive(true);
                        if (probeFSR_Flat_to_Ridge > 0.75f) RidgeSideFingerArtifact.SetActive(true);
                    }
                    else // its reversed, the flat side and ridge side swap places.
                    {
                        if (probeFSR_Flat_to_Ridge < 0.25f) RidgeSideFingerArtifact.SetActive(true);
                        if (probeFSR_Flat_to_Ridge > 0.75f) FlatSideFingerArtifact.SetActive(true);
                    }

                }
            }
        }

        /// <summary>
        /// These gameobjects are for the insonation plane in the 3D visualization (red sheet of light). 
        /// they are cognitive aids.
        /// There are three: one is a mideline, and the other is a sidedness line on the notch side of the insonation plane. 
        /// we added another one on the flat side. 
        /// The edges are color-coded, and the colors are not set here: they are set by material assignments in the inspector 
        /// </summary>
        ///
        void DrawMidline_and_Edges_of_3D_InsonationPlane()
        {
            // the insonation plane is made up of beams. beams are stored in a Vector4 array named beamTrajectoryPoints. 
            // the beamTrajectoryPoints are arranged from the flat side to the notch side. 
            // We want the notch side, so we reference the last index of beamTrajectoryPoints[].
            int beamIndex = beamTrajectoryPoints.GetLength(1) - 1;

            // first, we draw a (probably white) line on the ridge side of the insonation plane.
            Vector3 startPoint = beamTrajectoryPoints[2, beamIndex]; // in ultrasound plane space
            Vector3 endPoint = beamTrajectoryPoints[3, beamIndex];   // in ultrasound plane space
            startPoint = probeFaceCenterPoint.transform.TransformPoint(startPoint); // transformed into 3D world space
            endPoint = probeFaceCenterPoint.transform.TransformPoint(endPoint); // transformed into 3D world space

            // The InsonationPlane_EdgeLine_NotchSide gameobject has an unlit white cube. Its 1x1x(length)mm.
            // Calculate direction and length
            Vector3 direction = endPoint - startPoint;
            float length = direction.magnitude;

            // Position in the middle
            InsonationPlane_EdgeLine_NotchSide.transform.position = Vector3.Lerp(startPoint, endPoint, 0.5f);

            // Thickness of edge line and midline, what are 3D objects - this tweaks the size of the unlit white cube.
            float edge_and_midline_thickness = 0.5f;

            // Set scale - only change z-scale for length
            InsonationPlane_EdgeLine_NotchSide.transform.localScale = new Vector3(edge_and_midline_thickness, edge_and_midline_thickness, length);

            // Rotate to point along the line
            if (direction != Vector3.zero)
            {
                InsonationPlane_EdgeLine_NotchSide.transform.rotation = Quaternion.LookRotation(direction);
            }



            // Next, we draw a (probably white) line down the middle of the insonation plane.
            // since the beams are arranged from flat side to notch side, we'll reference the one in the middle. 
            beamIndex = (beamTrajectoryPoints.GetLength(1)) / 2;

            startPoint = beamTrajectoryPoints[2, beamIndex]; // in ultrasound plane space
            endPoint = beamTrajectoryPoints[3, beamIndex];   // in ultrasound plane space
            startPoint = probeFaceCenterPoint.transform.TransformPoint(startPoint); // transformed into 3D world space
            endPoint = probeFaceCenterPoint.transform.TransformPoint(endPoint); // transformed into 3D world space

            // the line renderer for the edge and midlines have only two points. we just update those points here.
            //InsonationPlane_MidLine.SetPosition(0, startPoint); // in 3D world space
            //InsonationPlane_MidLine.SetPosition(1, endPoint); // in 3D world space

            // The InsonationPlane_MidLine gameobject also has an unlit white cube. Its 1x1x(length)mm.
            // Calculate direction and length
            direction = endPoint - startPoint;
            length = direction.magnitude;

            // Position in the middle
            InsonationPlane_MidLine.transform.position = Vector3.Lerp(startPoint, endPoint, 0.5f);

            // Set scale - only change z-scale for length
            InsonationPlane_MidLine.transform.localScale = new Vector3(edge_and_midline_thickness, edge_and_midline_thickness, length);

            // Rotate to point along the line
            if (direction != Vector3.zero)
            {
                InsonationPlane_MidLine.transform.rotation = Quaternion.LookRotation(direction);
            }



            // We want the flat side, so we reference the first index of beamTrajectoryPoints[].
            beamIndex = 0;

            // first, we draw a (probably white) line on the ridge side of the insonation plane.
            startPoint = beamTrajectoryPoints[2, beamIndex]; // in ultrasound plane space
            endPoint = beamTrajectoryPoints[3, beamIndex];   // in ultrasound plane space
            startPoint = probeFaceCenterPoint.transform.TransformPoint(startPoint); // transformed into 3D world space
            endPoint = probeFaceCenterPoint.transform.TransformPoint(endPoint); // transformed into 3D world space

            // The InsonationPlane_EdgeLine_NotchSide gameobject has an unlit white cube. Its 1x1x(length)mm.
            // Calculate direction and length
            direction = endPoint - startPoint;
            length = direction.magnitude;

            // Position in the middle
            InsonationPlane_EdgeLine_FlatSide.transform.position = Vector3.Lerp(startPoint, endPoint, 0.5f);

            // Thickness of edge line and midline, what are 3D objects - this tweaks the size of the unlit white cube.
            edge_and_midline_thickness = 0.5f;

            // Set scale - only change z-scale for length
            InsonationPlane_EdgeLine_FlatSide.transform.localScale = new Vector3(edge_and_midline_thickness, edge_and_midline_thickness, length);

            // Rotate to point along the line
            if (direction != Vector3.zero)
            {
                InsonationPlane_EdgeLine_FlatSide.transform.rotation = Quaternion.LookRotation(direction);
            }


        }

        /// <summary>
        /// Colors the two edge lines on the ultrasound display to match the current image sidedness.
        ///
        /// The edge lines live in the display overlay, which never mirrors, so the two LineRenderers are permanently
        /// fixed to the viewer's left and right. Only their color carries probe-side meaning. By default the sidedness
        /// dot sits on the viewer's left, so the dot-side color goes on the left line. Reverse Image moves the dot to
        /// the viewer's right, so the two colors swap. Nothing moves, and no geometry changes - the insonation plane is
        /// symmetric about its midline, so the outline the lines trace is identical either way.
        ///
        /// We swap material REFERENCES here and never touch material properties like .color. These same material assets
        /// are shared with the 3D insonation plane edge cubes on the probe, and those must not change: they show the
        /// real probe, which does not move when the user presses Reverse Image.
        /// </summary>
        public void ApplyEdgeLineSidedness()
        {
            if (displayLeftEdgeLine == null || displayRightEdgeLine == null) return;

            CacheEdgeLineMaterials();
            if (dotSideEdgeLineMaterial == null || flatSideEdgeLineMaterial == null) return;

            // if there is no display controller for this plane we just leave the lines at their authored colors.
            bool dotOnRight = false;
            if (UltrasoundDisplayController.TryGet(DisplayPlane, out UltrasoundDisplayController displayController))
                dotOnRight = displayController.DotOnRight;

            displayLeftEdgeLine.sharedMaterial = dotOnRight ? flatSideEdgeLineMaterial : dotSideEdgeLineMaterial;
            displayRightEdgeLine.sharedMaterial = dotOnRight ? dotSideEdgeLineMaterial : flatSideEdgeLineMaterial;
        }

        /// <summary>
        /// Reads this plane's two edge line colors off the 3D insonation plane edge cubes on the probe.
        /// Those cubes are the single source of truth for the color convention, and it differs per plane:
        /// the primary is red on the ridge side and blue on the flat side, the biplane is yellow and magenta.
        /// Recolor a cube and the matching display edge line follows it.
        /// We use sharedMaterial so we read the material asset itself rather than instantiating a copy.
        /// </summary>
        private void CacheEdgeLineMaterials()
        {
            // the cubes are resolved in Start(), which may not have run yet if the display controller got there first, so retry until we have both.
            if (dotSideEdgeLineMaterial != null && flatSideEdgeLineMaterial != null) return;

            if (InsonationPlane_EdgeLine_NotchSide != null)
            {
                Renderer notchSideRenderer = InsonationPlane_EdgeLine_NotchSide.GetComponent<Renderer>();
                if (notchSideRenderer != null) dotSideEdgeLineMaterial = notchSideRenderer.sharedMaterial;
            }

            if (InsonationPlane_EdgeLine_FlatSide != null)
            {
                Renderer flatSideRenderer = InsonationPlane_EdgeLine_FlatSide.GetComponent<Renderer>();
                if (flatSideRenderer != null) flatSideEdgeLineMaterial = flatSideRenderer.sharedMaterial;
            }
        }


        #region Scan()
        /// <summary>
        /// The Scan method handles the actual ultrasound scan. Once per frame, this method is called, and it uses
        /// Physics.RaycastAll() between the pre-set points set within points, adjusted for the movement of the probeCenterPoint
        /// GameObject. This method takes the RaycastHit[] output and uses the data on hit distance in conjunction with the hit
        /// GameObject's UltrasoundMaterial to generate a representation of the ultrasound image.
        /// 
        /// </summary>
        ///
        void Scan()
        {
            PublishedScanProvenanceSnapshot provenance = captureNextPublishedScanProvenance
                ? new PublishedScanProvenanceSnapshot
                {
                    beamCount = beamTrajectoryPoints.GetLength(1),
                    beams = new List<PublishedScanProvenanceBeam>(beamTrajectoryPoints.GetLength(1))
                }
                : null;
            captureNextPublishedScanProvenance = false;

            // loop for each raycast start and endpoint pair:
            for (int beamIndex = 0; beamIndex < beamTrajectoryPoints.GetLength(1); beamIndex++)
            {
                PublishedScanProvenanceBeam provenanceBeam = provenance != null
                    ? new PublishedScanProvenanceBeam { beamIndex = beamIndex }
                    : null;

                Vector3 startPointU = beamTrajectoryPoints[2, beamIndex]; // in ultrasound plane space
                Vector3 endPointU = beamTrajectoryPoints[3, beamIndex];   // in ultrasound plane space

                // Spherecast is an option instead of raycasts, might be useful in some instances but makes all objects appear to grow by sphere radius. 
                if (UseSpherecast == false)
                {
                    // randomly move within the beam thickness. 
                    // real ultrasound beams are not parallel, they form an hourglass shape in cross section.
                    // this forms a narrow rectangle of parallel rays.
                    startPointU.z = UnityEngine.Random.Range(-beamThickness_mm / 2, beamThickness_mm / 2);
                    endPointU.z = startPointU.z;
                }

                Vector3 globalStartPointU = probeFaceCenterPoint.transform.TransformPoint(startPointU);
                Vector3 globalEndPointU = probeFaceCenterPoint.transform.TransformPoint(endPointU);

                // now its time for RAYCASTS!!!!
                // did we not detect the skin? if not, render a black line.
                // Raycast Up the probe because the tracked physical probe can easily be pushed into virtual anatomy because the physical skin it not tracked... 
                // the solution is to shoot the rays from farther up inside the probe, so if there is a little bit of overlap it doesnt create rendering artifacts.

                Vector3 d = (globalStartPointU - globalEndPointU).normalized * raycastUptheProbe_mm; // 20 mm up the probe
                Vector3 startUpTheProbe = globalStartPointU + d;

                // verification of the rays (only forwards)
                // Debug.DrawRay(startUpTheProbe, globalEndPointU - startUpTheProbe, Color.red, 0.1f);

                // by default, we'll assume there are no air gaps.
                airGapPresent = false;

                // DrawAirGaps flag means we must look for air gaps. Its usually the case.
                if (DrawAirGaps)
                    airGapPresent = !Physics.Raycast(startUpTheProbe, globalEndPointU - startUpTheProbe, raycastUptheProbe_mm + MinimumAirGap_mm, SkinLayer);
                if (provenanceBeam != null) provenanceBeam.airGapPresent = airGapPresent;

                // if an air gap is present, draw a simple null line.
                // The SetPixel() function will know to draw a black line because it reads the airGapPresent flag.
                // no further raycasts will be done.
                if (airGapPresent)
                    Render(beamIndex, 0, 1, null, 0);

                // if there is no air gap, RaycastAll into the world to get a list of everything you hit, forwards and backwards.
                if (!airGapPresent)
                {
                    // if the probe is far from the skin a, raycast for just the layers we allow in ImageableLayers set in the inspector.
                    // RaycastUpTheProbe compensates for pushing the probe into virtual anatomy becasue the real, untracked skin is squishy.

                    RaycastHit[] forwardHits;
                    RaycastHit[] backwardHits;

                    if (UseSpherecast == true)
                    {
                        float sphereRadius = beamThickness_mm / 2.0f;
                        forwardHits = Physics.SphereCastAll(startUpTheProbe, sphereRadius, globalEndPointU - startUpTheProbe, scanDepth_mm + raycastUptheProbe_mm, ImageableLayers);
                        backwardHits = Physics.SphereCastAll(globalEndPointU, sphereRadius, startUpTheProbe - globalEndPointU, scanDepth_mm + raycastUptheProbe_mm, ImageableLayers);
                    }
                    else
                    {
                        forwardHits = Physics.RaycastAll(startUpTheProbe, globalEndPointU - startUpTheProbe, scanDepth_mm + raycastUptheProbe_mm, ImageableLayers);
                        backwardHits = Physics.RaycastAll(globalEndPointU, startUpTheProbe - globalEndPointU, scanDepth_mm + raycastUptheProbe_mm, ImageableLayers);
                    }


                    // build an empty list for DetectedInterface.
                    // DetectedInterface is analagous to TransducerHit from the old 2014 ultrasound simualtion.
                    List<DetectedInterface> detectedInterfaces = new List<DetectedInterface>();

                    // ScannedElements from forward hits.
                    for (int hitIndex = 0; hitIndex < forwardHits.Length; hitIndex++)
                    {
                        RaycastHit hit = forwardHits[hitIndex];
                        UltrasoundVisibility uM = hit.transform.GetComponent<UltrasoundVisibility>();

                        if (uM != null && uM.ShowOnUltrasound)
                        {
                            float angle = Mathf.Abs(90f - Vector3.Angle(hit.normal, globalStartPointU - globalEndPointU));
                            if (!anisotropy | angle >= uM.AnisoIndex)
                            {
                                //float depth_fraction = hit.distance / depth;
                                float depth_fraction = (hit.distance - raycastUptheProbe_mm) / scanDepth_mm;
                                DetectedInterface sE = new DetectedInterface(depth_fraction, true, uM, hit.transform.GetInstanceID());

                                detectedInterfaces.Add(sE);
                            }
                        }
                    }


                    // ScannedElements from backwards hits.
                    for (int hitIndex = 0; hitIndex < backwardHits.Length; hitIndex++)
                    {
                        RaycastHit hit = backwardHits[hitIndex];
                        UltrasoundVisibility uM = hit.transform.GetComponent<UltrasoundVisibility>();
                        if (uM != null && uM.ShowOnUltrasound)
                        {
                            float angle = Mathf.Abs(90f - Vector3.Angle(hit.normal, globalEndPointU - globalStartPointU));
                            // unlike the forward hits, the backward hits must be tested without regards to anisotropic angle. 
                            // if not, when a backward hit is undetected but the forward hit is, a long streak of appears below the object.
                            // this condition may happen when the bottom edge of the object has a more obtuse insonation angle then the top edge, and the uM.AnisoIndex is between the two angle values.
                            {
                                float depth_fraction = 1 - ((hit.distance) / scanDepth_mm);
                                DetectedInterface sE = new DetectedInterface(depth_fraction, false, uM, hit.transform.GetInstanceID());

                                detectedInterfaces.Add(sE);
                            }
                        }
                    }

                    // this sorts the ScannedElements detected from the forward and backward raycasts in order of distance.
                    for (int y = 0; y < detectedInterfaces.Count - 1; y++)
                    {
                        DetectedInterface sEY = detectedInterfaces[y];
                        for (int z = y + 1; z < detectedInterfaces.Count; z++)
                        {
                            DetectedInterface sEZ = detectedInterfaces[z];
                            if (sEZ.depthFromProbe < sEY.depthFromProbe)
                            {
                                detectedInterfaces[y] = sEZ;
                                detectedInterfaces[z] = sEY;
                                sEY = sEZ;
                            }
                        }
                    }


                    // this loops through the sorted list of scanned elements and calls Render functions based on the UltrasoundMaterial type.
                    /*
                     * This loop is critical because it:
                        - Processes each detected element in order of distance
                        - Handles both entering and exiting material boundaries
                        - Maintains proper layering of materials
                        - Provides special handling for shadow (bone) visualization
                        - Manages the stack of currently active materials
                        - Ensures proper rendering order and material transitions
                    The loop essentially builds up the ultrasound image line by line, handling the complexity of multiple material layers and their interfaces.
                    It's particularly important for creating realistic ultrasound artifacts at material boundaries and handling special cases like bone visualization.
                     * */
                    List<DetectedInterface> activeInterfaces = new List<DetectedInterface>();
                    List<int> activeInterfaceIDs = new List<int>();
                    DetectedInterface current = new DetectedInterface(true);
                    float lastProcessedDepth = 0;
                    int lastRenderedIndex = 0;
                    for (int y = 0; y < detectedInterfaces.Count; y++)
                    {
                        current = detectedInterfaces[y];
                        if (provenanceBeam != null && current.isEnteringMaterial)
                        {
                            if (!provenanceBeam.hasVisibleInterface)
                            {
                                provenanceBeam.hasVisibleInterface = true;
                                provenanceBeam.firstVisibleObject = current.uMaterial.gameObject.name;
                                provenanceBeam.firstVisibleObjectInstanceId = current.objectInstanceID;
                                provenanceBeam.firstVisibleRenderType = current.uMaterial.Type.ToString();
                                provenanceBeam.firstVisibleDepthFraction = current.depthFromProbe;
                            }
                            if (!provenanceBeam.hasShadowInterface
                                && current.uMaterial.Type == UltrasoundVisibility.RenderType.Shadow)
                            {
                                provenanceBeam.hasShadowInterface = true;
                                provenanceBeam.firstShadowObject = current.uMaterial.gameObject.name;
                                provenanceBeam.firstShadowObjectInstanceId = current.objectInstanceID;
                                provenanceBeam.firstShadowDepthFraction = current.depthFromProbe;
                            }
                        }
                        if (current.isEnteringMaterial)
                        {
                            // If scanning in forward direction:
                            if (activeInterfaces.Count == 0)
                            { // If no objects are currently being rendered,
                              // render from last point to current with background material
                                lastRenderedIndex = Render(beamIndex, lastProcessedDepth, current.depthFromProbe, null, lastRenderedIndex);
                            }
                            else
                            {
                                // If there are objects being rendered,
                                // render from last point to current with the most recently opened material
                                lastRenderedIndex = Render(beamIndex, lastProcessedDepth, current.depthFromProbe, activeInterfaces[activeInterfaces.Count - 1].uMaterial, lastRenderedIndex);
                            }
                            // Add current element to list of open elements
                            activeInterfaces.Add(current);
                            // Track the object instance ID for proper closing later
                            activeInterfaceIDs.Add(current.objectInstanceID);
                            // Update the last rendered position
                            lastProcessedDepth = current.depthFromProbe;


                            // Special handling for materials that cast shadows, like bones.
                            if (current.uMaterial.Type == UltrasoundVisibility.RenderType.Shadow)
                            {
                                // Add bright edge highlight characteristic of bone in ultrasound

                                float f = current.uMaterial.EdgeThickness; //typically 0.02f, or about 2%.
                                lastRenderedIndex = RenderShadowEdge(beamIndex, current.depthFromProbe, current.depthFromProbe + f, current.uMaterial.EdgeColor, lastRenderedIndex);
                                // Render the rest of the shadow (typically black)
                                lastRenderedIndex = Render(beamIndex, current.depthFromProbe + f, 1, current.uMaterial, lastRenderedIndex);
                                break; // Stop processing after bone (bone blocks all signals behind it)
                            }


                        }
                        else
                        {
                            // If scanning in backward direction:
                            if (activeInterfaces.Count > 0)
                            {
                                // Render with current material up to this point
                                lastRenderedIndex = Render(beamIndex, lastProcessedDepth, current.depthFromProbe, activeInterfaces[activeInterfaces.Count - 1].uMaterial, lastRenderedIndex);

                                // If we've found the closing boundary of an object we previously opened
                                if (activeInterfaceIDs.Contains(current.objectInstanceID))
                                {
                                    // Remove the object from our tracking lists
                                    int index = activeInterfaceIDs.LastIndexOf(current.objectInstanceID);
                                    activeInterfaceIDs.RemoveAt(index);
                                    activeInterfaces.RemoveAt(index);
                                }
                            }
                            else
                            {
                                // If no objects are open, render with background material
                                lastRenderedIndex = Render(beamIndex, lastProcessedDepth, current.depthFromProbe, null, lastRenderedIndex);
                            }
                            // Update the last rendered position
                            lastProcessedDepth = current.depthFromProbe;
                        }
                    }
                    if (activeInterfaces.Count > 0)
                        Render(beamIndex, lastProcessedDepth, 1, activeInterfaces[activeInterfaces.Count - 1].uMaterial, lastRenderedIndex);
                    else
                        Render(beamIndex, lastProcessedDepth, 1, null, lastRenderedIndex);

                } // no airgap
                if (provenanceBeam != null) provenance.beams.Add(provenanceBeam);
            }


            ultrasoundTextureCamera.GetComponent<Volume>().enabled = blur;
            attenuationOverlay.SetActive(depthAttenuation);

            liveUltrasoundImage.Apply();

            ultrasoundImage.GetComponent<MeshRenderer>().material.mainTexture = liveUltrasoundImage;
            if (renderUltrasoundOntoScanningPlane)
                renderedScanningPlaneMaterial.mainTexture = liveUltrasoundImage;
            if (provenance != null) pendingPublishedScanProvenance = provenance;
        }

        #endregion Scan()



        #region Render Functions
        void RenderEmptyImage()
        {
            for (int x = 0; x < liveUltrasoundImage.width; x++)
            {
                for (int y = 0; y < liveUltrasoundImage.height; y++)
                {
                    liveUltrasoundImage.SetPixel(x, y, validUltrasoundRegion[x, y] ? backgroundColor : Color.clear);
                }
            }
            liveUltrasoundImage.Apply();
        }



        /// <summary>
        /// Called from within the Scan() to determin parameters of the rendering process.
        /// It is an intermediary between the Scan() and RenderLine() methods. This class analyses the
        /// information about what to render and passes the task of setting the pixels to the RenderLine() method.
        /// </summary>
        /// <param name="line">The line corresponding to the current Raycast</param>
        /// <param name="dist1">The fist distance of the given segment being rendered.</param>
        /// <param name="dist2">The second distance of the given segment being rendered.</param>
        /// <param name="USM">The UltrasoundMaterial of the segment being rendered.</param>
        /// <param name="lastRenderedIndex">The index corresponding to the last rendered pixelAnalysis for the current line.</param>
        /// <returns></returns>
        int Render(int line, float dist1, float dist2, UltrasoundVisibility USM, int lastRenderedIndex)
        {
            currentUltrasoundMaterial = USM;
            List<Vector3> analysis = raycastPixelMappings[line];
            for (int x = lastRenderedIndex; x < analysis.Count - 1; x++)
            {
                Vector3 current = analysis[x];
                Vector3 next = analysis[x + 1];
                if (current.x < dist1)
                {
                    if (next.x > dist2)
                    {
                        RenderLine(current.y, Mathf.Lerp(current.z, next.z, ((dist1 - current.x) / (next.x - current.x))), Mathf.Lerp(current.z, next.z, ((dist2 - current.x) / (next.x - current.x))));
                        return x;
                    }
                    else if (next.x > dist1)
                    {
                        RenderLine(current.y, Mathf.Lerp(current.z, next.z, ((dist1 - current.x) / (next.x - current.x))), next.z);
                    }
                }
                else if (next.x > dist2)
                {
                    RenderLine(current.y, current.z, Mathf.Lerp(current.z, next.z, ((dist2 - current.x) / (next.x - current.x))));
                    return x;
                }
                else
                {
                    RenderLine(current.y, current.z, next.z);
                }
            }
            return analysis.Count;
        }

        /// <summary>
        /// This method sets the pixels on the x line of the texture between [y1,y2].
        /// The actual setting of the pixels is performed in the SetPixel() method. However, this method dictates exactly
        /// which pixels to set.
        /// </summary>
        /// <param name="x">The x texture coordinate of the segment being rendered.</param>
        /// <param name="y1">The first y coordinate of the segment being rendered.</param>
        /// <param name="y2">The second y coordinate of the segment being rendered.</param>
        void RenderLine(float x, float y1, float y2)
        {
            float y = y1;
            while (true)
            {
                SetPixel(Mathf.RoundToInt(x), Mathf.RoundToInt(y));
                y++;
                if (y > y2)
                {
                    SetPixel(Mathf.RoundToInt(x), Mathf.RoundToInt(y2));
                    break;
                }
            }
        }

        /// <summary>
        /// Here, the actual pixels of the ultrasoundTexture are set individually. The color of the rendered pixel is determined
        /// using the currentUltrasoundMaterial set within the Render() method. Depending on the UltrasoundMaterial.RenderType 
        /// of the specified UltrasoundMaterial, certain render criteria are met differently.
        /// This method is a candidate for optimization in a future update.
        /// 
        /// </summary>
        /// <param name="x">X texture coordinate to set the color of.</param>
        /// <param name="y">Y texture coordinate to set the color of.</param>
        void SetPixel(int x, int y)
        {
            if (currentUltrasoundMaterial == null) // this happens when the ultrasound is picking up blank space (the background).
            {
                if (airGapPresent)
                {
                    liveUltrasoundImage.SetPixel(x, y, Color.black);
                }
                else if (useBackgroundTexture)
                {
                    Vector4 ultrasoundScreenPoint = new Vector4(x, y, 1);
                    Vector4 ultrasoundWorldPoint4 = (textureToScanningPlaneTransformSecondary * (textureToScanningPlaneTransformPrimary * ultrasoundScreenPoint));
                    Vector3 ultrasoundWorldPoint = ultrasoundWorldPoint4;
                    ultrasoundWorldPoint = probeFaceCenterPoint.transform.TransformPoint(ultrasoundWorldPoint);

                    // this feeds a distored ultrasoundWorldPoint into Simplex noise, which returns a value we can use to lookup a color from a gradient.
                    ultrasoundWorldPoint *= frequency0;
                    ultrasoundWorldPoint.x /= Divisor0.x; // this stretches the noise in the X direction
                    ultrasoundWorldPoint.y /= Divisor0.y; // this stretches the noise in the Y direction
                    ultrasoundWorldPoint.z /= Divisor0.z; // this stretches the noise in the Z direction
                    float noiseValue = UltrasoundSimplexNoise.Simplex3D(ultrasoundWorldPoint); // returns a noise value
                    noiseValue = noiseValue * (1 - dampener0) + dampener0; // this helps bias the color towards the middle of the gradient

                    float brightness = Mathf.Lerp(backgroundMinBrightness, backgroundMaxBrightness, noiseValue);
                    liveUltrasoundImage.SetPixel(x, y, new Color(brightness, brightness, brightness, 1f));
                }
                else
                {
                    liveUltrasoundImage.SetPixel(x, y, backgroundColor);
                }
            }
            else if (currentUltrasoundMaterial.Type == UltrasoundVisibility.RenderType.Normal)
            {
                liveUltrasoundImage.SetPixel(x, y, currentUltrasoundMaterial.Color);
            }
            else if (currentUltrasoundMaterial.Type == UltrasoundVisibility.RenderType.Shadow)
            {
                liveUltrasoundImage.SetPixel(x, y, currentUltrasoundMaterial.Color);
            }
            else if (currentUltrasoundMaterial.Type == UltrasoundVisibility.RenderType.Lung)
            {
                if (UnityEngine.Random.value >= .98)
                {
                    liveUltrasoundImage.SetPixel(x, y, Color.white);
                }
                else
                {
                    liveUltrasoundImage.SetPixel(x, y, currentUltrasoundMaterial.Color);
                }
            }
            else if (currentUltrasoundMaterial.Type == UltrasoundVisibility.RenderType.Texture1)
            {
                Vector4 ultrasoundScreenPoint = new Vector4(x, y, 1);
                Vector4 ultrasoundWorldPoint4 = (textureToScanningPlaneTransformSecondary * (textureToScanningPlaneTransformPrimary * ultrasoundScreenPoint));
                Vector3 ultrasoundWorldPoint = ultrasoundWorldPoint4;
                ultrasoundWorldPoint = probeFaceCenterPoint.transform.TransformPoint(ultrasoundWorldPoint);

                ultrasoundWorldPoint *= frequency1;
                ultrasoundWorldPoint.x /= Divisor1.x;
                ultrasoundWorldPoint.y /= Divisor1.y;
                ultrasoundWorldPoint.z /= Divisor1.z;
                float noiseValue = UltrasoundSimplexNoise.Simplex3D(ultrasoundWorldPoint);
                noiseValue = noiseValue * (1 - dampener1) + dampener1;

                float brightness = Mathf.Lerp(texture1MinBrightness, texture1MaxBrightness, noiseValue);
                liveUltrasoundImage.SetPixel(x, y, new Color(brightness, brightness, brightness, 1f));

            }
            else if (currentUltrasoundMaterial.Type == UltrasoundVisibility.RenderType.Texture2)
            {
                Vector4 ultrasoundScreenPoint = new Vector4(x, y, 1);
                Vector4 ultrasoundWorldPoint4 = (textureToScanningPlaneTransformSecondary * (textureToScanningPlaneTransformPrimary * ultrasoundScreenPoint));
                Vector3 ultrasoundWorldPoint = ultrasoundWorldPoint4;
                ultrasoundWorldPoint = probeFaceCenterPoint.transform.TransformPoint(ultrasoundWorldPoint);

                ultrasoundWorldPoint *= frequency2;
                ultrasoundWorldPoint.x /= Divisor2.x;
                ultrasoundWorldPoint.y /= Divisor2.y;
                ultrasoundWorldPoint.z /= Divisor2.z;

                float noiseValue = UltrasoundSimplexNoise.Simplex3D(ultrasoundWorldPoint);
                noiseValue = noiseValue * (1f - dampener2) + dampener2;

                float brightness = Mathf.Lerp(texture2MinBrightness, texture2MaxBrightness, noiseValue);
                liveUltrasoundImage.SetPixel(x, y, new Color(brightness, brightness, brightness, 1f));
            }
        }

        /// <summary>
        /// Used just as the Render() Method is. However, this is a special rendering method for creating the bright edge effect on bones.
        /// In the future, this method will be either expanded, or otherwise modified to better incorporate it into other existing methods.
        /// </summary>
        /// <param name="line">The line corresponding to the current Raycast</param>
        /// <param name="dist1">The fist distance of the given segment being rendered.</param>
        /// <param name="dist2">The second distance of the given segment being rendered.</param>
        /// <param name="edgeColor">The color to which the pixels will be set.</param>
        /// <param name="lastRenderedIndex">The index corresponding to the last rendered pixelAnalysis for the current line.</param>
        /// <returns></returns>
        int RenderShadowEdge(int line, float dist1, float dist2, Color edgeColor, int lastRenderedIndex)
        {
            List<Vector3> analysis = raycastPixelMappings[line];
            for (int x = lastRenderedIndex; x < analysis.Count - 1; x++)
            {
                Vector3 current = analysis[x];
                Vector3 next = analysis[x + 1];
                if (current.x < dist1)
                {
                    if (next.x > dist2)
                    {
                        RenderLine(current.y, Mathf.Lerp(current.z, next.z, ((dist1 - current.x) / (next.x - current.x))), Mathf.Lerp(current.z, next.z, ((dist2 - current.x) / (next.x - current.x))), edgeColor);
                        return x;
                    }
                    else if (next.x > dist1)
                    {
                        RenderLine(current.y, Mathf.Lerp(current.z, next.z, ((dist1 - current.x) / (next.x - current.x))), next.z, edgeColor);
                    }
                }
                else if (next.x > dist2)
                {
                    RenderLine(current.y, current.z, Mathf.Lerp(current.z, next.z, ((dist2 - current.x) / (next.x - current.x))), edgeColor);
                    return x;
                }
                else
                {
                    RenderLine(current.y, current.z, next.z, edgeColor);
                }
            }
            return analysis.Count;
        }

        /// <summary>
        /// Exactly the same as the RenderLine() method above, however, a specific color is passed.
        /// This is used exclusively by the RenderShadowEdge() method to allow for special edges, like you see on bone
        /// flares to be added.
        /// </summary>
        /// <param name="x">The x texture coordinate of the segment being rendered.</param>
        /// <param name="y1">The first y coordinate of the segment being rendered.</param>
        /// <param name="y2">The second y coordinate of the segment being rendered.</param>
        /// <param name="c">The color to set the pixels to.</param>
        void RenderLine(float x, float y1, float y2, Color c)
        {
            float y = y1;
            while (true)
            {
                liveUltrasoundImage.SetPixel(Mathf.RoundToInt(x), Mathf.RoundToInt(y), c);
                y++;
                if (y > y2)
                {
                    liveUltrasoundImage.SetPixel(Mathf.RoundToInt(x), Mathf.RoundToInt(y2), c);
                    break;
                }
            }
        }

        /// <summary>
        /// DetectedInterface is a struct to store the distance, direction, UltrasoundMaterial,
        /// and instanceID associated with the object detected with a raycast in Scan().
        /// This is very similiar to Lizdas' original TransducerHit in the older implementation.
        /// </summary>
        public struct DetectedInterface
        {
            public float depthFromProbe;
            public bool isEnteringMaterial;
            public UltrasoundVisibility uMaterial;
            public int objectInstanceID;
            public DetectedInterface(float Distance, bool DirectionForward, UltrasoundVisibility UMaterial, int InstanceID)
            {
                depthFromProbe = Distance;
                isEnteringMaterial = DirectionForward;
                uMaterial = UMaterial;
                objectInstanceID = InstanceID;
            }
            public DetectedInterface(bool nullConstructor)
            {
                depthFromProbe = 0;
                isEnteringMaterial = true;
                uMaterial = null;
                objectInstanceID = 0;
            }
        }

        #endregion Render Functions

        /// <summary>
        /// This is the method that creates and shapes the "The red sheet of light" insonation plane you see below the US probe in the 3D visualization.
        /// Depending on the state of renderSimpleScanningPlane,
        /// (when false) this method creates either an ideal ultrasound scanning plane that perfectly matches the raycast lines,
        /// or a lower quality version with 1/10th the vertexes of the ideal plane (when true).
        /// Refactoring priority on this one is low, it works very well. It needs a white sideness dot 
        /// 
        /// </summary>
        void CreateInsonationPlaneFor3DVisualization()
        {


            renderedScanningPlaneMaterial = new Material(Shader.Find("Unlit/Texture"));
            renderedScanningPlaneMaterial.mainTexture = liveUltrasoundImage;
            scanningPlane = new GameObject("Scanning Plane");
            // MeshCollider meshCollider = scanningPlane.AddComponent<MeshCollider>();
            MeshRenderer meshRenderer = scanningPlane.AddComponent<MeshRenderer>();
            MeshFilter meshFilter = scanningPlane.AddComponent<MeshFilter>();
            Mesh mesh = new Mesh();
            Vector3[] vertices;
            if (renderSimpleScanningPlane && beamTrajectoryPoints.GetLength(1) > 10)
                vertices = new Vector3[(3 + 10) * 2];
            else
                vertices = new Vector3[(3 + beamTrajectoryPoints.GetLength(1)) * 2];
            vertices[0] = Vector3.zero;
            vertices[1] = beamTrajectoryPoints[2, 0];
            vertices[vertices.Length / 2 - 1] = beamTrajectoryPoints[2, beamTrajectoryPoints.GetLength(1) - 1];
            if (renderSimpleScanningPlane && beamTrajectoryPoints.GetLength(1) > 10)
            {
                for (int x = 0; x < 10; x++)
                {
                    float y = x;
                    y /= 9f;
                    vertices[x + 2] = beamTrajectoryPoints[3, (int)((beamTrajectoryPoints.GetLength(1) - 1) * y)];
                }
            }
            else
            {
                for (int x = 0; x < vertices.Length / 2 - 3; x++)
                {
                    vertices[x + 2] = beamTrajectoryPoints[3, x];
                }
            }
            for (int x = 0; x < vertices.Length / 2; x++)
            {
                Vector3 negativeVector = vertices[x];
                negativeVector.z = -beamThickness_mm / 2;
                Vector3 positiveZVector = negativeVector;
                positiveZVector.z = -positiveZVector.z;
                vertices[x] = positiveZVector;
                vertices[x + vertices.Length / 2] = negativeVector;
            }
            Vector2[] UV = new Vector2[vertices.Length];
            UV[0] = new Vector2(.5f, 0);
            UV[1] = new Vector2(beamTrajectoryPoints[0, 1].x / textureResolutionPixels, beamTrajectoryPoints[0, 1].y / textureResolutionPixels);
            UV[UV.Length / 2 - 1] = new Vector2(beamTrajectoryPoints[0, beamTrajectoryPoints.GetLength(1) - 1].x / textureResolutionPixels, beamTrajectoryPoints[0, beamTrajectoryPoints.GetLength(1) - 1].y / textureResolutionPixels);
            UV[UV.Length / 2] = UV[0];
            UV[UV.Length / 2 + 1] = UV[1];
            UV[UV.Length - 1] = UV[UV.Length / 2 - 1];
            if (!renderSimpleScanningPlane)
            {
                for (int x = 2; x < UV.Length / 2 - 1; x++)
                {
                    UV[x] = new Vector2(beamTrajectoryPoints[1, x - 2].x / textureResolutionPixels, beamTrajectoryPoints[1, x - 2].y / textureResolutionPixels);
                    UV[UV.Length / 2 + x] = UV[x];
                }
            }
            else
            {
                for (int x = 2; x < UV.Length / 2 - 1; x++)
                {
                    float y = x - 2;
                    y /= 9f;
                    UV[x] = new Vector2(beamTrajectoryPoints[1, (int)((beamTrajectoryPoints.GetLength(1) - 1) * y)].x / textureResolutionPixels, beamTrajectoryPoints[1, (int)((beamTrajectoryPoints.GetLength(1) - 1) * y)].y / textureResolutionPixels);
                    UV[UV.Length / 2 + x] = UV[x];
                }
            }
            List<int> triangleList = new List<int>();
            for (int x = 1; x < vertices.Length / 2 - 1; x++)
            {
                triangleList.Add(0);
                triangleList.Add((x + 1));
                triangleList.Add(x);
                triangleList.Add(vertices.Length / 2);
                triangleList.Add(x + vertices.Length / 2);
                triangleList.Add(vertices.Length / 2 + x + 1);
            }
            for (int x = 0; x < vertices.Length / 2 - 1; x++)
            {
                triangleList.Add(x);
                triangleList.Add(x + 1 + vertices.Length / 2);
                triangleList.Add(x + vertices.Length / 2);
                triangleList.Add(x);
                triangleList.Add(x + 1);
                triangleList.Add(x + vertices.Length / 2 + 1);
            }
            triangleList.Add(vertices.Length / 2 - 1);
            triangleList.Add(vertices.Length / 2);
            triangleList.Add(vertices.Length - 1);
            triangleList.Add(vertices.Length / 2 - 1);
            triangleList.Add(0);
            triangleList.Add(vertices.Length / 2);
            int[] triangles = new int[triangleList.Count];
            for (int x = 0; x < triangleList.Count; x++)
            {
                triangles[x] = triangleList[x];
            }
            mesh.vertices = vertices;
            mesh.uv = UV;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            meshFilter.mesh = mesh;
            if (!renderUltrasoundOntoScanningPlane)
                meshRenderer.material = insonationPlaneMaterial;
            else
                meshRenderer.material = renderedScanningPlaneMaterial;
            //meshCollider.sharedMesh = mesh;
            scanningPlane.transform.position = probeFaceCenterPoint.transform.position;
            scanningPlane.transform.rotation = probeFaceCenterPoint.transform.rotation;
            scanningPlane.transform.parent = ultrasoundProbe.transform;
        }

        /// <summary>
        /// The attenuation overlay is integral in creating a drop-off in the quality of the ultrasound image with increased
        /// depth. This method creates the field used for overlaying the attenuation depth.
        /// </summary>
        /// <summary>
        /// The attenuation overlay is integral in creating a drop-off in the quality of the ultrasound image with increased
        /// depth. This method creates the field used for overlaying the attenuation depth.
        /// </summary>
        void SetAttenuationDepths()
        {
            pixelDepthMap = new float[textureResolutionPixels, textureResolutionPixels];
            validUltrasoundRegion = new bool[textureResolutionPixels, textureResolutionPixels];
            for (int x = 0; x < pixelDepthMap.GetLength(0); x++)
            {
                for (int y = 0; y < pixelDepthMap.GetLength(1); y++)
                {
                    pixelDepthMap[x, y] = 1;
                    validUltrasoundRegion[x, y] = false;
                    attenuationDepthFalloffMask.SetPixel(x, y, new Color32(0, 0, 0, 0));
                    beamBoundaryMask.SetPixel(x, y, new Color32(0, 0, 0, 255)); // The edge color is black
                }
            }
            for (int line = 0; line < raycastPixelMappings.Length; line++)
            {
                List<Vector3> analysis = raycastPixelMappings[line];
                for (int x = 0; x < analysis.Count - 1; x++)
                {
                    Vector3 a = analysis[x];
                    Vector3 b = analysis[x + 1];
                    float dy = b.z - a.z;
                    float ddist = b.x - a.x;
                    Color32 c;
                    for (float y = a.z; y < b.z; y++)
                    {
                        float d = (a.x + ((y - a.z) / dy) * ddist);
                        pixelDepthMap[(int)a.y, Mathf.RoundToInt(y)] = d;
                        //byte alpha2 = (byte)(d * depthAttenuationFraction * 255);

                        float dither = ((x + y) % 2 == 0) ? -8f : 8f; // Simple checkerboard pattern
                        byte alpha2 = (byte)Mathf.Clamp((d * depthAttenuationFraction * 255) + dither, 0, 255);

                        c = new Color32(0, 0, 0, alpha2);
                        attenuationDepthFalloffMask.SetPixel((int)a.y, Mathf.RoundToInt(y), c);
                        beamBoundaryMask.SetPixel((int)a.y, Mathf.RoundToInt(y), new Color32(0, 0, 0, 0));
                        validUltrasoundRegion[(int)a.y, Mathf.RoundToInt(y)] = true;
                    }
                    pixelDepthMap[(int)a.y, Mathf.RoundToInt(b.z)] = b.x;
                    byte alpha1 = (byte)(b.x * depthAttenuationFraction * 255);
                    c = new Color32(0, 0, 0, alpha1);
                    attenuationDepthFalloffMask.SetPixel((int)a.y, Mathf.RoundToInt(b.z), c);
                    beamBoundaryMask.SetPixel((int)a.y, Mathf.RoundToInt(b.z), new Color32(0, 0, 0, 0));
                    validUltrasoundRegion[(int)a.y, Mathf.RoundToInt(b.z)] = true;
                }
            }
            attenuationDepthFalloffMask.Apply();
            attenuationOverlay.GetComponent<MeshRenderer>().material.mainTexture = attenuationDepthFalloffMask;
            beamBoundaryMask.Apply();
            edgeOverlay.GetComponent<MeshRenderer>().material.mainTexture = beamBoundaryMask;
        }


        /// <summary>
		/// This method takes in a screen position (anywhere on the US image screen), converts it to a pixel position on the US screen texture, and returns the
		/// corresponding position in world space of the screen location.
		/// 
		/// If the screen is frozen, the method will convert the location on the screen to the location in world units of where that position given is
		/// if the screen were not frozen. It does not know where the screen was at the time it was frozen.
		/// 
		/// TODO: If the location is not on the US Screen, returns Vector3.zero.
        /// TODO: If the location is on the screen, but not on the rendered US insonating plane, returns Vector3.zero
		/// </summary>
		/// <param name="screenPosition">Vector2 screen position. Must be in terms of entire screen</param>
		/// <returns>The Vector3 position of the point on the US Screen in world global units. Vector3.zero if screen not hit.</returns>
		public Vector3 ScreenPointToWorldCoordinate(Vector2 screenPosition)
        {

            Vector4 texturePoint = new Vector2(screenPosition.x, textureResolutionPixels - screenPosition.y - 1);

            texturePoint.z = 1;

            Vector4 ultrasoundWorldPoint4 = textureToScanningPlaneTransformSecondary * (textureToScanningPlaneTransformPrimary * texturePoint);

            Vector3 ultrasoundWorldPoint = ultrasoundWorldPoint4;
            ultrasoundWorldPoint = probeFaceCenterPoint.transform.TransformPoint(ultrasoundWorldPoint);
            return ultrasoundWorldPoint;
        }

    }
}
