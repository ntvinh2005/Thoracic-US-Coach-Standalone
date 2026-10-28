using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Smoothly moves a camera to a target transform position and rotation.
    /// Useful for camera transitions and positioning in medical simulations.
    /// </summary>
    public class FluoroTechCameraMover : MonoBehaviour
    {

        [Header("Camera GameObjects and Transforms")]

        [Tooltip("Camera to move (not the main camera)")]
        public Camera FluoroTechViewpointCamera;

        [SerializeField] private Transform defaultCameraLocation;
        [SerializeField] private Transform baseCameraLocation;
        [SerializeField] private Transform upDownCameraLocation;
        [SerializeField] private Transform inOutCameraLocation;
        [SerializeField] private Transform wigWagCameraLocation;
        [SerializeField] private Transform tiltCameraLocation;
        [SerializeField] private Transform orbitCameraLocation;



        [Tooltip("Transform to move the camera to")]
        public Transform targetTransform;

        [Header("Movement Settings")]
        [Tooltip("Speed of position movement (units per second)")]
        public float moveSpeed = 5f;

        [Tooltip("Speed of rotation movement (degrees per second)")]
        public float rotateSpeed = 90f;

        [Tooltip("Distance threshold to consider movement complete")]
        public float positionThreshold = 0.1f;

        [Tooltip("Angle threshold to consider rotation complete")]
        public float rotationThreshold = 1f;

        [Header("Debug Info")]
        [SerializeField] private bool isMoving = false;
        [SerializeField] private float distanceToTarget = 0f;
        [SerializeField] private float angleToTarget = 0f;

        void Update()
        {
            if (isMoving && FluoroTechViewpointCamera != null && targetTransform != null)
            {
                MoveCameraToTarget();
            }
        }

        public void DefaultViewpoint()  { targetTransform = defaultCameraLocation; }
        public void BaseViewpoint()     { targetTransform = baseCameraLocation; }
        public void UpDownViewpoint()   { targetTransform = upDownCameraLocation; }
        public void InOutViewpoint()    { targetTransform = inOutCameraLocation; }
        public void WigWagViewpoint()   { targetTransform = wigWagCameraLocation; }
        public void TiltViewpoint()     { targetTransform = tiltCameraLocation; }
        public void OrbitViewpoint()    { targetTransform =  orbitCameraLocation; }


        /// <summary>
        /// Start moving the camera to the target transform
        /// </summary>
        /// 
        /*
        public void StartMoving()
        {
            if (FluoroTechViewpointCamera == null || targetTransform == null)
            {
                Debug.LogWarning("CameraMover: Camera or target transform not assigned!");
                return;
            }

            isMoving = true;
            Debug.Log("Camera movement started");
        }*/


        /// <summary>
        /// Move camera towards target smoothly
        /// </summary>
        private void MoveCameraToTarget()
        {
            Transform cameraTransform = FluoroTechViewpointCamera.transform;

            // Calculate distances for debug info
            distanceToTarget = Vector3.Distance(cameraTransform.position, targetTransform.position);
            angleToTarget = Quaternion.Angle(cameraTransform.rotation, targetTransform.rotation);

            // Check if movement is necessary
            if (distanceToTarget > positionThreshold && angleToTarget > rotationThreshold)
            {
               // isMoving = true;
            } else
            {
               // isMoving = false;
               // return;
            }


            // Move position
            if (distanceToTarget > positionThreshold)
            {
                cameraTransform.position = Vector3.MoveTowards(
                    cameraTransform.position,
                    targetTransform.position,
                    moveSpeed * Time.deltaTime
                );
            }

            // Rotate
            if (angleToTarget > rotationThreshold)
            {
                cameraTransform.rotation = Quaternion.RotateTowards(
                    cameraTransform.rotation,
                    targetTransform.rotation,
                    rotateSpeed * Time.deltaTime
                );
            }

            // Check if movement is complete
            /*
            if (distanceToTarget <= positionThreshold && angleToTarget <= rotationThreshold)
            {
                // Snap to exact target position/rotation
                cameraTransform.position = targetTransform.position;
                cameraTransform.rotation = targetTransform.rotation;

                isMoving = false;
                Debug.Log("Camera movement completed");
            }
            */

        }

        /// <summary>
        /// Instantly snap camera to target (no smooth movement)
        /// </summary>
        public void SnapToTarget()
        {
            if (FluoroTechViewpointCamera == null || targetTransform == null)
            {
                Debug.LogWarning("CameraMover: Camera or target transform not assigned!");
                return;
            }

            FluoroTechViewpointCamera.transform.position = targetTransform.position;
            FluoroTechViewpointCamera.transform.rotation = targetTransform.rotation;
            isMoving = false;

            Debug.Log("Camera snapped to target");
        }

        /// <summary>
        /// Check if camera is currently moving
        /// </summary>
        public bool IsMoving => isMoving;
    }
}
