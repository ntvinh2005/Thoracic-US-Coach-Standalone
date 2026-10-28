using UnityEngine;
using System.Collections;
using System.Linq;

// This drives a clockface cognitive aid for US probe alignment with skin. 

namespace SMMARTS
{
    public class ProbePerpendicularityIndicator: MonoBehaviour
    {
        [Header("----------- Object References -----------------------------------------------------------")]
        public GameObject USProbeFaceCenterMarker;
        public GameObject USProbeRearCenterMarker;
        public GameObject IndicatorStick, IndicatorCenterDot;
        public GameObject SkinMesh;
        public LayerMask SkinLayer;
        public GameObject ProbeRidgeRotationPointer;
        public GameObject TiltAxisDot, RockAxisDot;

        // When discussing US probe manipulation the “PARRT” mnemonic is useful.
        // • Pressure (how hard you are pressing into skin - not handled here)
        // • Alignment(sliding - not handled here, we are looking at angles here)
        // • Rocking, which rolls the probe along the short axis of the probe face, pushing one end of the long face into the skin while lifting the other side off the skin
        // • Rotation, defined as the axis of probe, like spinning it around the cord. 0 degrees points at the left, rotating CW. 
        // • Tilt, which rolls the probe along the skin about the long axis of the probe face

        [Header("----------- Probe Rotation (degrees and clockface hours) --------------------------------")]
        public float RotationAngle;
        public float RotationHours;
        public int RotationOClock;

        [Header("----------- Probe Tilt (degrees) --------------------------------------------------------")]
        public float TiltAngle;

        [Header("----------- Probe Rock (degrees) --------------------------------------------------------")]
        public float RockAngle;

        [Header("----------- Off-Perpendicular (degrees) -------------------------------------------------")]
        public float DegreesOffPerpendicular;

        private Vector3[] SkinMeshNormals;
        private int[] SkinMeshTriangles;
        private Vector3 IndicatorStickVector, TiltAxis, RockAxis;

        void Start()
        {
            InitializeMesh();

            if (SkinLayer == 0) SkinLayer = 1 << LayerMask.NameToLayer("Skin");
            if (USProbeFaceCenterMarker == null) USProbeFaceCenterMarker = GameObject.Find("Tracked Ultrasound Probe/3D Model of Probe/Face Center Marker");
            if (USProbeRearCenterMarker == null) USProbeRearCenterMarker = GameObject.Find("Tracked Ultrasound Probe/3D Model of Probe/Rear Center Marker");
        }

        public void InitializeMesh()
        {
            // this assumes the skin does not move. if your skin moves or changes, you will have to rebuild this.
            MeshCollider SkinCollider = this.SkinMesh.GetComponent<MeshCollider>();
            Mesh SkinMesh = SkinCollider.sharedMesh;
            SkinMeshNormals = SkinMesh.normals;
            SkinMeshTriangles = SkinMesh.triangles;
        }

        public void Update()
        {
            if (SkinMesh == null) return;   

            RaycastHit hit;

            // forward direction
            Vector3 forward = USProbeFaceCenterMarker.transform.position - USProbeRearCenterMarker.transform.position;
            Ray lookingForward = new Ray(USProbeRearCenterMarker.transform.position, forward);

            float d = forward.magnitude * 1.4F;

            if (Physics.Raycast(lookingForward, out hit, d, SkinLayer))
            {

                // Extract local space normals of the triangle we hit.   
                Vector3 n0 = SkinMeshNormals[SkinMeshTriangles[hit.triangleIndex * 3 + 0]];
                Vector3 n1 = SkinMeshNormals[SkinMeshTriangles[hit.triangleIndex * 3 + 1]];
                Vector3 n2 = SkinMeshNormals[SkinMeshTriangles[hit.triangleIndex * 3 + 2]];

                // Extract the barycentric coordinate of the hitpoint 
                Vector3 baryCenter = hit.barycentricCoordinate;

                // Use barycentric coordinate to interpolate between the three normals
                Vector3 interpolatedNormal = n0 * baryCenter.x + n1 * baryCenter.y + n2 * baryCenter.z;

                // normalize the interpolated normal 
                interpolatedNormal = interpolatedNormal.normalized;

                // Transform local space normals to world space 
                Transform hitTransform = hit.collider.transform;
                interpolatedNormal = hitTransform.TransformDirection(interpolatedNormal);
                interpolatedNormal.Normalize();

                // First we move the entire indicator - including the camera, background, everything - to align with the skin perpendicular.
                transform.rotation = Quaternion.FromToRotation(Vector3.back, interpolatedNormal);

                // Second, we move the Indicator Stick to align with the ultrasound probe.
                IndicatorStick.transform.rotation = USProbeFaceCenterMarker.transform.rotation;

                // Now that we have the cognitive aid oriented properly, lets define specific probe rotations.

                // PARRT Rotation.  On the clockface, 270 = 12 o'clock. 0 = 3 o'clock. 90 = 6 o'clock. 180 = 9 o'clock. 
                // rotationAngle is 0 to 360 degrees, starting at 9 o'clock, CW direction.
                RotationAngle = IndicatorStick.transform.localEulerAngles.z;
                RotationHours = ((RotationAngle + 270f) % 360f) / 30f;
                RotationOClock = Mathf.RoundToInt(RotationHours); if (RotationOClock == 0) RotationOClock = 12;

                // this is the red clock "hand", which is directly related to the rotation angle
                ProbeRidgeRotationPointer.transform.localEulerAngles = new Vector3(ProbeRidgeRotationPointer.transform.localEulerAngles.x, RotationAngle, ProbeRidgeRotationPointer.transform.localEulerAngles.z);

                // note the direction of the indicator stick, which is the probe in relation to the skin normal.  
                IndicatorStickVector = IndicatorStick.transform.position - IndicatorCenterDot.transform.position;

                // PARRT Rock angle.
                TiltAxis = IndicatorStick.transform.position - TiltAxisDot.transform.position;
                RockAngle = Vector3.Angle(IndicatorStickVector, Vector3.ProjectOnPlane(IndicatorStickVector, TiltAxis));

                // PARRT Tilt angle.  
                RockAxis = IndicatorStick.transform.position - RockAxisDot.transform.position;
                TiltAngle = Vector3.Angle(IndicatorStickVector, Vector3.ProjectOnPlane(IndicatorStickVector, RockAxis));

                // Entire difference from perpendicular
                DegreesOffPerpendicular = 180F - Vector3.Angle(forward.normalized, interpolatedNormal);


            }

        }



    }
}