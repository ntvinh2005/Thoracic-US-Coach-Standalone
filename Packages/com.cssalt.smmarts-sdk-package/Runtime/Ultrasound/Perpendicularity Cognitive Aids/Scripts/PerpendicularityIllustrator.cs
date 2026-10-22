using UnityEngine;
using System.Collections;

// detector for: If the needle is pointing at a layer, what is the degrees offset to normal?
// and uses the needle hub and tip analogy.

namespace SMMARTS {
	public class PerpendicularityIllustrator : MonoBehaviour
	{
		[Header("----------- Object References -----------------------------------------------------------")]
		public GameObject Hub;
		public GameObject Tip;
		public GameObject IndicatorStick, IndicatorCenterDot;
		public GameObject SkinMesh;
        public LayerMask SkinMask;


		[Header("----------- Off-Perpendicular (degrees) -------------------------------------------------")]
		public float DegreesOffPerpendicular;


		private Vector3[] SkinMeshNormals;
		private int[] SkinMeshTriangles;
		private Vector3 IndicatorStickVector;

		void Start()
		{
            InitializeMesh();

            if (SkinMask == 0) SkinMask = 1 << LayerMask.NameToLayer("SkinMesh");
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

			RaycastHit hit;

			// forward direction
			Vector3 forward = Tip.transform.position - Hub.transform.position;
			Ray lookingForward = new Ray(Hub.transform.position, forward);
			//float tipDistance = Vector3.Distance (Hub.transform.position, Tip.transform.position);


			float d = forward.magnitude * 1.4F;

			if (Physics.Raycast(lookingForward, out hit, d, SkinMask))
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

				// First we move the entire indicator - including the camera, background, everything - to align with the skin perpendicular.
				transform.rotation = Quaternion.FromToRotation(Vector3.back, interpolatedNormal);

				// Second, we move the Indicator Stick to align with the ultrasound probe.
				IndicatorStick.transform.rotation = Tip.transform.rotation;

				float rotationAngle = IndicatorStick.transform.localEulerAngles.z;

				//if (RotationNeedle != null)
				//	RotationNeedle.transform.localEulerAngles = new Vector3(RotationNeedle.transform.localEulerAngles.x, rotationAngle, RotationNeedle.transform.localEulerAngles.z);

				// note the direction of the indicator stick, which is the probe in relation to the skin normal.  
				IndicatorStickVector = IndicatorStick.transform.position - IndicatorCenterDot.transform.position;

				// Entire difference from perpendicular
				DegreesOffPerpendicular = 180F - Vector3.Angle(forward.normalized, interpolatedNormal);


			}

		}

	}
	
}
