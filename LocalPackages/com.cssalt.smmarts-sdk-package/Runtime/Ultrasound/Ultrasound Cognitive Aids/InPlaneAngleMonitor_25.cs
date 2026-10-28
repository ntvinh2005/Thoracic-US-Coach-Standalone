using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// TODO: clean and refactor

namespace SMMARTS
{
    public class InPlaneAngleMonitor25 : MonoBehaviour
    {

        [Header("-----Transform of this monitor's Camera-----")]
        public Transform CameraTransform; // 

        [Header("-----Needle Objects-----")]
        // these are found during initialization using Gameobject.find because in SMMARTS SDK, you may instantiate different needles.
        public GameObject NeedleHub;
        public GameObject NeedleTip;


        // these are needle approach detector boxes - we need to know if the needle is in-plane or out-of-plane  
        // because this cog aid will turn the angle text different colors based on specific threshold values for each
        [Header("-----Approach Detector Boxes around the Probe-----")]
        public GameObject Out_Of_Plane_Approach_Front_Side_Detector_Box;
        public GameObject Out_Of_Plane_Approach_Back_Side_Detector_Box;
        public GameObject In_Plane_Approach_Flat_Side_Detector_Box;
        public GameObject In_Plane_Approach_Ridge_Side_Detector_Box;
        public LayerMask ApproachDetectorLayermask; // raycast from hub to tip to detect one of the detector boxes - don't want to confuse it with raycast hit on some needle parts or the skin

        [Header("-----Cog Aid Objects-----")]
        public Text AngleText;

        Vector3 PlaneNormal;
        Vector3 NeedleInPlaneVector;

        [Header("-----Angle Thresholds for Red Angle Text-----")]
        public int RedAngleThresholdOutOfPlane;
        public int RedAngleThresholdInPlane;

        float needleLength; // technically the distance between the hub marker and the tip marker - so our raycasts don't extend past the tip of the needle

        //internal flag for detecting if there is even a needle at all... you can't assume all US simulators are interventional.
        private bool thereisaneedle;

        // Use this for initialization
        void Start()
        {
            // Needle Hub and Tip must reference one needle in the scene; one of several available in SDK may have been instantiated. this will work independent of which one we are using.
            if (NeedleHub == null) NeedleHub = GameObject.Find("NeedleHubMarker");
            if (NeedleTip == null) NeedleTip = GameObject.Find("NeedleTipMarker");

            // there might not even be a needle. Lets not spam with red errors like a noob.
            thereisaneedle = NeedleHub != null && NeedleTip != null;
            if (!thereisaneedle) return;

            needleLength = Vector3.Distance(NeedleHub.transform.position, NeedleTip.transform.position);
        }

        // Update is called once per frame

        void Update()
        {
            // first off, there is no need to continue if there isn't a needle. its rare but possible. 
            if (!thereisaneedle)
            {
                Debug.Log("InPlaneAngleMonitor needs a needle!");
                Start();
                return;
            }
            PlaneNormal = CameraTransform.forward;
            Vector3 needleVector = NeedleTip.transform.position - NeedleHub.transform.position;
            NeedleInPlaneVector = Vector3.ProjectOnPlane(needleVector, PlaneNormal);

            Vector3 InsonationVector = -CameraTransform.right;

            float InsonationAngle = Vector3.Angle(InsonationVector, NeedleInPlaneVector);

            if (InsonationAngle > 90) InsonationAngle = 180 - InsonationAngle;

            // Default angle text is blank. If the needle is in view, we'll change that below.
            AngleText.text = "--°";
            AngleText.color = Color.grey;

            Ray needleLookingForward = new Ray(NeedleHub.transform.position, needleVector);

            RaycastHit[] hits = Physics.RaycastAll(needleLookingForward, needleLength, ApproachDetectorLayermask);
            System.Array.Sort(hits, (hit1, hit2) => hit1.distance.CompareTo(hit2.distance));

            if (hits.Length > 0)
            {
                RaycastHit firstHit = hits[0]; // note, this works only if the first hit is the detector box! can it be the skin, or something else?

                // test both of the narrow sides of the probe for in-plane approach, and set the color
                if (firstHit.collider.gameObject == In_Plane_Approach_Flat_Side_Detector_Box | firstHit.collider.gameObject == In_Plane_Approach_Ridge_Side_Detector_Box)
                {
                    AngleText.text = InsonationAngle.ToString("0") + "°";
                    if (InsonationAngle < RedAngleThresholdInPlane) AngleText.color = Color.green;
                    if (InsonationAngle > RedAngleThresholdInPlane) AngleText.color = Color.red;
                }
                
                // test both of the wide sides of the probe for out-of-plane approach, and set the color
                else if (firstHit.collider.gameObject == Out_Of_Plane_Approach_Front_Side_Detector_Box | firstHit.collider.gameObject == Out_Of_Plane_Approach_Back_Side_Detector_Box)
                {
                    AngleText.text = InsonationAngle.ToString("0") + "°";
                    if (InsonationAngle > RedAngleThresholdOutOfPlane) AngleText.color = Color.green;
                    if (InsonationAngle < RedAngleThresholdOutOfPlane) AngleText.color = Color.red;
                }

            }

        }
    }
}