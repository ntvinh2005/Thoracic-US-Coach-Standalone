using UnityEngine;

namespace SMMARTS
{

    public class HydrodissectionManager : MonoBehaviour
    {

        /*  
        Overview: 
        •	HydrodissectionManager is a Monobehavior script manually pre-placed on one suitable gameobject in the hierarchy, for example on the Anatomy gameobject
        •	singleton for program-wide access to hydrodissection variables and constants
        •	during initialization, creates a list of all the meshes that we can hydrodissect
        •	watches needle tip and tests to see if its inside a mesh or not
        •	listens for space bar and foot switch
        •	controls hydrodissection injectate volume
        •	sets a flag that the Mesh Deformers read so they know when to check for movement
        •	updates other variables that the Mesh Deformers read, like the location of the needle tip
        •	can be told to reset all the meshes to their original shapes
        */

        public static HydrodissectionManager ME;

        // Variables
        [Header("Set the Needle Hub and Tip - found automatically if blank")]

        public Transform NeedleHub;
        // reference to the NeedleHub on the SMMARTS modular needle

        public Transform NeedleTip;
        // reference to the NeedleTip on the SMMARTS modular needle

        [Header("Set the Needle Bevel Heel - created automatically if blank")]
        public Transform NeedleBevelHeel;
        // reference to the other side of the orifice on the SMMARTS modular needle. Its 2.8mm up the shaft and 0.7mm laterally from the tip.
        // we need this because the hydrodissection space is really small so we'll test if any part of the needle orifice is in the space, not just the tip.
        // we'll make this procedurally so we can use any SMMARTS modular needle in any scene without having to remember to place it.

        [Header("Set the Hydrodissectable Layers")]
        public LayerMask MeshDetectionMask;
        // Used for raycast detection of meshes to see if the needle tip is or is not inside specific kinds of meshes, in this case muscles
        // Means for raycasts to hit certain types of meshes only, for example muscles but not skin or any other type of tissue

        [Header("List the Hydrodissectable Objects - created automatically if blank")]
        public HydrodissectionMeshDeformer[] HydrodissectableMeshList;
        // List of all the HydrodissectionMeshDeformers that have been pre-placed on each mesh that can move from hydrodissection
        // used for a Reset() function so we can tell the muscles to reset to their original shape

        [Header("Set the Syringe Size and flow rate")]
        public float SyringeSize_ml = 5;
        // Size of the syringe used in hydrodissection, set once for the entire project.
        // Initially set to 5 ml
        // Hydrodissection is commonly done with either 5 ml or 10 ml syringes, we are assuming a 5 ml.

        public float InjectateRate_ml_per_s = 0.5f;
        // Range 0 to 2 ml/s, initially set to 0.5 ml/s

        [Header("Set the Sphere Of Influence Radius mm")]
        [Range(0, 100)]
        public float SphereOfInfluenceRadius_mm = 20;
        // Range 0-100 mm, initially set to 20 mm
        // Set qualitatively, adjustable during development

        [Header("Set the Vertex Displacement Limit mm")]
        [Range(0, 5)]
        public float VertexDisplacementLimit_mm = 4;
        // Range 0 to 4 mm, initially set to 4 mm.

        [Header("Set the Hydrodissection Constant")]
        [Range(0, 100)]
        public float TissueHydrodissectionConstant = 5;
        // Qualitative tuning factor
        // Used by all vertex drivers in any mesh to convert their distance from needle tip and the injectate volume into their individual displacement magnitude
        // Units of mm2/mL
        // Range 0 to 100 mm2/mL, initially set to 10 mm2/mL
        // adjustable only during development

        [Header("Use keys?")]
        public bool SpaceKeyIsPlunger = true;
        // If true, this script will poll for a space key press and use it for hydrodissection.
        // if false, you have to set PressingOnThePlunger to true externally from another script.

        public bool ShiftRIsReset = true;
        // if true, this script will poll for a Shift-R keypress and use it to refill the syringe and reset the meshes.
        // if false, you will have to call Reset() from another script.

        [Header("Reference Variables")]
        public float VolumeLeftInSyringe_ml;
        // volume of fluid left in the syringe.
        // Initially set to SyringeSize_ml ml

        public float InjectateVolume_ml;
        // variable range from 0 to SyringeSize_ml, initially set to 0 ml
        // Volume of fluid injected for a hydrodissection bolus.
        // Changes only when fluid is being injected in that frame, otherwise its reset to zero.



        public bool PressingOnThePlunger = false;
        // public flag to enable an outside script (voice recognition) to trigger hydrodisssection

        public bool Injecting = false;
        // True or false flag for when the meshes are expected to change
        // True for each frame when the footswitch or spacebar is held down while part of the needle bevel is outside a muscle and the syringe isn’t empty
        // When true, InjectateVolume_ml will increase at InjectateRate_ml up to SyringeSize_ml
        // and VolumeLeftInSyringe_ml will drop down to 0 ml.

        public Vector3 HydrodissectionPoint;
        // The position in space on the needle bevel where injectate emanates from. Can be anywhere on the needle tip or needle bevel heel.

        public GameObject SphereOfInfluence;
        // reference to a sphere placed on needle tip just for visualization during development
        // sphere has radius = SphereOfInfluenceRadius_mm










        // Functions

        // Initialize on Start(), called before the first frame renders.
        // Builds the HydrodissectableMeshList by searching every loaded scene for instances of the HydrodissectionMeshDeformer component and adding it to the list
        // search may include inactive gameobjects any loaded scenes, not just this one, or exclude them
        // Builds references to the SMMARTS modular needle, the needle tip and hub,
        // and creates a new reference point on the needle bevel heel we can use for detecting if any part of the lumen is in the muscle or not.
        void Start()
        {
            // find the SMMARTS modular needle hub and tip markers automatically by the name.
            // if you have assigned them already, we'll use yours.
            if (NeedleHub == null | NeedleTip == null)
            {
                NeedleHub = GameObject.Find("NeedleHubMarker").transform;
                NeedleTip = GameObject.Find("NeedleTipMarker").transform;
            }

            // we need to reference a point on the bevel heel is for detecting the back side of the opening at the tip of the needle.
            // since the SMMARTS modular needle does not have a bevel heel gameobject, we'll make on. its -2.8mm up the shaft and 0.7mm forward.
            // locate new gameobject "Needle Bevel Heel" during runtime and you can verify where it is.
            // If you have a bevel heel already prepared, we'll just use yours.
            if (NeedleBevelHeel == null)
            {
                GameObject bevelheel = GameObject.Instantiate(NeedleTip.gameObject, NeedleTip.parent, true);
                bevelheel.transform.localPosition = new Vector3(0, 0.7f, -2.8f);
                bevelheel.gameObject.name = "Needle Bevel Heel";
                NeedleBevelHeel = bevelheel.transform;
            }

            // make a list of all the hydrodissectable muscles so we can tell them to Reset()  
            // if you have a list already, we'll just use yours. If not, we'll look for any ACTIVE gameobjects that have the hydrodissection mesh deformer script.
            if (HydrodissectableMeshList == null)
            {
                HydrodissectableMeshList = FindObjectsByType<HydrodissectionMeshDeformer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            }

            // initialize the syringe state, fill it completely
            VolumeLeftInSyringe_ml = SyringeSize_ml;
            InjectateVolume_ml = 0;

            // if there isnt a sphere of influence for dubugging/visualization purposes, just make one.
            // if you don't want to show it, make your own sphere, assign it in the inspector, and turn the mesh renderer off.
            if (SphereOfInfluence == null)
            {
                SphereOfInfluence = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                SphereOfInfluence.gameObject.name = "Sphere of Influence";
                SphereOfInfluence.transform.parent = transform;
            }
        }

        // Update() runs before each frame is rendered to the screen
        // sets Injecting flag to false (which is the default case)
        // asks AreWePushingOnTheSyringePlunger()
        // If yes, asks IsNeedleTipPositionOKforHydrodissection()
        // If yes, asks AreWeInjecting()
        // Sets Injecting flag to true if the above three conditions are true.
        // in addition, Update sets the NeedleTipPosition to match the needle tip
        void Update()
        {
            Injecting = false; // default case
            if (AreWePushingOnTheSyringePlunger())
            {
                if (IsNeedleTipPositionOKforHydrodissection())
                {
                    Injecting = AreWeInjecting(); // AreWeInjecting() sets InjectateVolume_ml 
                }
                else
                {
                    InjectateVolume_ml = 0;
                }
            }
            else
            {
                InjectateVolume_ml = 0;
            }
            // HydrodissectionPoint = NeedleTip.transform.position; // transform position in world space of the needle tip

            // just for visualization during development, you don't need this object for hydrolocation:
            SphereOfInfluence.transform.position = HydrodissectionPoint;
            SphereOfInfluence.transform.localScale = Vector3.one * SphereOfInfluenceRadius_mm * 2; // transform.localScale sets diameter, NOT radius. Side note: So many forums say scale = radius that AI-generated code is wrong. Measure it for yourself. Unity documentation for Sphere primitive doesn't say (!?)

            // handy reset for development.
            if (ShiftRIsReset && Input.GetKeyDown("r"))
            { 
                Reset();
            }

        }

        // AreWePushingOnTheSyringePlunger() returns true or false. 
        // true = your finger is pushing on the plunger, false = your finger is not pushing on the plunger.
        // Checks to see if the hydrodissection keyboard key (the space key) is being held down
        // Assumes the hydrodissection foot switch is programmed to act like the space key on a keyboard (the foot switch is literally a keyboard with a single key)
        // For private use within this script
        // used to be named "AreWeTryingToHydrodissect" but this name is more accurate
        bool AreWePushingOnTheSyringePlunger()
        {

            // PressingOnThePlunger is to enable an outside script, like voice recognition, to control hydrodissection.
            // so, we are pushing on the syringe if either the spacebar or footswitch is held down or if the voice recognition says we are.
            return (PressingOnThePlunger | (SpaceKeyIsPlunger && Input.GetKey("space")));

        }

        // IsNeedleTipPositionOKforHydrodissection() returns true or false
        // Sets NeedleTipPosition to the NeedleTip’s transform position
        // checks to see if the needle tip in the space between meshes of any layer listed in the MeshDetectionMask using two raycasts between the needle tip and the needle hub, tests in both directions. In this case, tests to see if the tip is inside the muscle or not. Hydrodissection is not possible if the tip is inside the muscle because the muscle tissue is too dense to push injectate into the muscle.
        // Returns true if needle tip is not inside any mesh of any layer in the MeshDetectionMask, false if the needle tip is inside any mesh of any layer in the MeshDetectionMask.
        // For private use within this script
        public bool IsNeedleTipPositionOKforHydrodissection()
        {
            // if you want to hydrodissect from inside the muscle too, just
            // return true;
            // Cam said on 1/7/25 that you can (the muscle swells from the inside) but we determined in October that we will not do this and wrote the spec accordingly.

            // if not, detect if needle tip is inside any muscle, independent of the number of muscles the needle shaft is penetrating.
            // transform position in world space of the needle hub

            // default case is both ends of the needle bevel orifice is not in the muscle.
            bool needleBevelTipInMuscle = false;
            bool needleBevelHeelInMuscle = false;

            // first, lets test if the needle tip is in the space. 

            Ray lookingDownTheNeedle = new Ray(NeedleHub.position, (NeedleTip.position - NeedleHub.position));
            Ray lookingBackUpTheNeedle = new Ray(NeedleTip.position, (NeedleHub.position - NeedleTip.position));
            float length = Vector3.Distance(NeedleHub.position, NeedleTip.position);

            RaycastHit[] forwardMuscleHits = Physics.RaycastAll(lookingDownTheNeedle, length, MeshDetectionMask);
            RaycastHit[] backMuscleHits = Physics.RaycastAll(lookingBackUpTheNeedle, length, MeshDetectionMask);

            // if you are poking through the same number of muscle walls from both sides, your not in a muscle.
            if (forwardMuscleHits.Length == backMuscleHits.Length) needleBevelTipInMuscle = true;


            // next, lets test if the needle bevel heel is in the space.
            lookingDownTheNeedle = new Ray(NeedleHub.position, (NeedleBevelHeel.position - NeedleHub.position));
            lookingBackUpTheNeedle = new Ray(NeedleTip.position, (NeedleHub.position - NeedleBevelHeel.position));
            length = Vector3.Distance(NeedleHub.position, NeedleBevelHeel.position);

            forwardMuscleHits = Physics.RaycastAll(lookingDownTheNeedle, length, MeshDetectionMask);
            backMuscleHits = Physics.RaycastAll(lookingBackUpTheNeedle, length, MeshDetectionMask);

            // if you are poking through the same number of muscle walls from both sides, your not in a muscle.
            if (forwardMuscleHits.Length == backMuscleHits.Length) needleBevelHeelInMuscle = true;


            if (needleBevelHeelInMuscle) HydrodissectionPoint = NeedleBevelHeel.position;
            if (needleBevelTipInMuscle) HydrodissectionPoint = NeedleTip.position;

            if (needleBevelHeelInMuscle | needleBevelTipInMuscle) return true;

            // we cannot hydrodissect.
            // return false.
            return false;
        }

        // AreWeInjecting() returns true or false
        // Set and read internally by this script
        // Increase InjectateVolume_ml by InjectateRate_ml_per_s until InjectateRate_ml_per_s = SyringeSize_ml, in which case the syringe is empty and no more volume can be injected
        // returns true if injectate volume changed, false if not
        // For private use within this script
        bool AreWeInjecting()
        {
            if (VolumeLeftInSyringe_ml <= 0)
            {
                return false;
            }
            else
            {
                float dV_ml = InjectateRate_ml_per_s * Time.deltaTime;

                InjectateVolume_ml += dV_ml;

                VolumeLeftInSyringe_ml -= dV_ml;

                if (VolumeLeftInSyringe_ml < 0) VolumeLeftInSyringe_ml = 0;

            }
            return true;
        }


        // Reset()
        // called by an outside script, for example a Reset UI button
        // resets InjectateVolume_ml to zero
        // calls ResetMesh() on each HydrodissectionMeshDeformer in the HydrodissectableMeshList
        public void Reset()
        {
            InjectateVolume_ml = 0;
            VolumeLeftInSyringe_ml = SyringeSize_ml;
            foreach (HydrodissectionMeshDeformer D in HydrodissectableMeshList) D.ResetMesh();

        }


        private void Awake()
        {
            // singleton reference:
            if (ME != null) GameObject.Destroy(ME);
            else ME = this;
            DontDestroyOnLoad(this);
        }



    }
}
