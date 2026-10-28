using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SMMARTS
{
    public class InsonationAngleMonitor_25 : MonoBehaviour
    {

        [Header("-----Transform of this monitor's Camera-----")]
        public Transform InPlaneCameraTransform; // 
        public Transform OutOfPlaneCameraTransform; // 

        [Header("-----Needle Objects-----")]
        // these are found during initialization using Gameobject.find because in SMMARTS SDK, you may instantiate different needles.
        public GameObject NeedleHub;
        public GameObject NeedleTip;

        [Header("-----Cog Aid Objects-----")]
        // these are found during initialization using Gameobject.find because in SMMARTS SDK, you may instantiate different needles.
        public GameObject InPlaneIndicator;
        public GameObject OutOfPlaneIndicator;
        public Text AngleText_InPlane;
        public Text AngleText_OutofPlane;

        // these are needle approach detector boxes - we need to know if the needle is in-plane or out-of-plane  
        // because this cog aid will turn the angle text different colors based on specific threshold values for each
        [Header("-----Approach Detector Boxes around the Probe-----")]
        public GameObject Out_Of_Plane_Approach_Front_Side_Detector_Box;
        public GameObject Out_Of_Plane_Approach_Back_Side_Detector_Box;
        public GameObject In_Plane_Approach_Flat_Side_Detector_Box;
        public GameObject In_Plane_Approach_Ridge_Side_Detector_Box;
        public LayerMask ApproachDetectorLayermask; // raycast from hub to tip to detect one of the detector boxes - don't want to confuse it with raycast hit on some needle parts or the skin

        [Header("-----Threshold for Unfavorable Angle of Incidence-----")]
        // if the angle is more than about 45 degrees (rule of thumb on most US machines) 
        // then most of the insonating energy is reflected away from the probe and the needle can disappear in US image.
        // if the angle is unfavorable the angle text is red, otherwise its green. if the nunber stale its grey (not visible to the user because the cog aid should be off)
        public int UnfavorableAngleOfIncidenceDetectionThreshold;


        private Vector3 PlaneNormal;
        private Vector3 NeedleInPlaneVector;
        private Vector3 InsonationVector;
        private float InsonationAngle;
        private float needleLength;

        //internal flags for detecting which plane we are needling
        private bool needleHitsInPlaneDetectionBox;
        private bool needleHitsOutOfPlaneDetectionBox;

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

        void Update()
        {
            // first off, there is no need to continue if there isn't a needle. its rare but possible. 
            if (!thereisaneedle)
            {
                Debug.Log("InsonationAngleMonitor needs a needle!");
                Start();
                return;
            }

            //Calculate the needle vector and length
            Vector3 needleVector = NeedleTip.transform.position - NeedleHub.transform.position;

            //Detect which plane (small or big) of the ultrasound probe that the needle is in
            DetectNeedlingPlane(needleVector);

            //Define the plane of which the needle is projected on to and insonation vector to which the angle measurement is made
            if (needleHitsInPlaneDetectionBox)
            {
                // Select the In-Plane insonation angle cognitive aid
                InPlaneIndicator.SetActive(true);
                OutOfPlaneIndicator.SetActive(false);

                // set up the geometry to measure the angle based on the camera, which is aligned with the probe in-plane
                PlaneNormal = InPlaneCameraTransform.forward;
                InsonationVector = InPlaneCameraTransform.up;

                // Project the needle onto the plane defined by the plane normal 
                NeedleInPlaneVector = Vector3.ProjectOnPlane(needleVector, PlaneNormal);
                InsonationAngle = Vector3.Angle(InsonationVector, NeedleInPlaneVector);

                // Ensure that angle remains within [0, 90] degrees
                if (InsonationAngle > 90) InsonationAngle = 180 - InsonationAngle;

                // write the angle string
                AngleText_InPlane.text = InsonationAngle.ToString("0") + "°";

                // set the color. if the angle is more than about 45 degrees (rule of thumb on most US machines) 
                // then most of the insonating energy is reflected away from the probe and the needle can disappear in US image.
                if (InsonationAngle > UnfavorableAngleOfIncidenceDetectionThreshold) AngleText_InPlane.color = Color.green;
                else AngleText_InPlane.color = Color.red;

            }
            else if (needleHitsOutOfPlaneDetectionBox)
            {
                // Select the Out-of-Plane insonation angle cognitive aid
                OutOfPlaneIndicator.SetActive(true);
                InPlaneIndicator.SetActive(false);

                // set up the geometry to measure the angle based on the camera, which is aligned with the probe out-of-plane
                PlaneNormal = OutOfPlaneCameraTransform.forward;
                InsonationVector = -OutOfPlaneCameraTransform.right;

                //Project the needle onto the plane defined by the plane normal 
                NeedleInPlaneVector = Vector3.ProjectOnPlane(needleVector, PlaneNormal);
                InsonationAngle = Vector3.Angle(InsonationVector, NeedleInPlaneVector);

                //Ensure that angle remains within [0, 90] degrees
                if (InsonationAngle > 90) InsonationAngle = 180 - InsonationAngle;

                // write the angle string
                AngleText_OutofPlane.text = InsonationAngle.ToString("0") + "°";

                // set the color. if the angle is more than about 45 degrees (rule of thumb on most US machines) 
                // then most of the insonating energy is reflected away from the probe and the needle can disappear in US image.
                if (InsonationAngle > UnfavorableAngleOfIncidenceDetectionThreshold) AngleText_OutofPlane.color = Color.green;
                else AngleText_OutofPlane.color = Color.red;

            }
            else // since the needle is not penetrating any of the zone detection boxes, assume no needling is occuring
            {
                // turn off both insonation angle cognitive aids 
                InPlaneIndicator.SetActive(false);
                OutOfPlaneIndicator.SetActive(false);

                // make sure the angle is not reporting stale data
                AngleText_InPlane.text = "--°";
                AngleText_OutofPlane.text = "--°";

                AngleText_InPlane.color = Color.grey;
                AngleText_OutofPlane.color = Color.grey;
            }

        }

        //Detects the position of the needle with respect to the plane of the ultrasound probe 
        private void DetectNeedlingPlane(Vector3 needleVector)
        {
            Ray needleLookingForward = new Ray(NeedleHub.transform.position, needleVector);

            // Puts all the objects hit by the needle within an array and sort the array by distance to prioritize the first layer that was hit
            // the ApproachDetectorLayermask is used so the needle does not interact with simulator-specific element
            RaycastHit[] hits = Physics.RaycastAll(needleLookingForward, needleLength, ApproachDetectorLayermask);
            System.Array.Sort(hits, (hit1, hit2) => hit1.distance.CompareTo(hit2.distance));

            // set both in- and out-of-plane detection flags false by default
            needleHitsInPlaneDetectionBox = false;
            needleHitsOutOfPlaneDetectionBox = false;


            // set the appropriate in- or out-of-plane detection flags true
            if (hits.Length > 0)
            {
                RaycastHit firstHit = hits[0]; // grab the first hit

                // test both of the narrow sides of the probe for in-plane approach
                if (firstHit.collider.gameObject == In_Plane_Approach_Flat_Side_Detector_Box | firstHit.collider.gameObject == In_Plane_Approach_Ridge_Side_Detector_Box)
                {
                    needleHitsInPlaneDetectionBox = true;
                }
                // test both of the wide sides of the probe for out-of-plane approach
                else if (firstHit.collider.gameObject == Out_Of_Plane_Approach_Front_Side_Detector_Box | firstHit.collider.gameObject == Out_Of_Plane_Approach_Back_Side_Detector_Box)
                {
                    needleHitsOutOfPlaneDetectionBox = true;
                }

            }

        }



    }
}


