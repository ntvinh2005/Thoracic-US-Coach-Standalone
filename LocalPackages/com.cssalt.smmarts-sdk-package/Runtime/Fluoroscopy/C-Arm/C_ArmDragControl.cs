
using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Handles click and hold interaction for C-Arm control arrows.
    /// Provides continuous movement while mouse button is held down.
    /// </summary>
    public class C_ArmDragControl : MonoBehaviour
    {
        [Header("Arrow Configuration")]
        [SerializeField] private string controlType; // "UpDown", "InOut", "WigWag", "Tilt", "Orbit"
        [SerializeField] private bool isRotationControl = false; // true for rotations, false for translations

        [Header("Movement Settings")]
        [SerializeField] private float movementSpeed = 10f; // Units per second for translations, degrees per second for rotations
        [SerializeField] private bool reverseDirection = false; // Flip movement direction if needed

        private bool isHolding = false;

        void Update()
        {
            if (isHolding && C_ArmPositioner.ME != null)
            {
                // Calculate movement amount based on time and speed
                float deltaMovement = movementSpeed * Time.deltaTime;

                // Apply direction reversal if needed
                if (reverseDirection)
                {
                    deltaMovement = -deltaMovement;
                }

                // Send movement to C_ArmPositioner
                C_ArmPositioner.ME.UpdatePosition(controlType, deltaMovement);
            }
        }

        public void OnMouseDown()
        {
            if (C_ArmPositioner.ME != null)
            {
                isHolding = true;
            }
        }

        public void OnMouseUp()
        {
            isHolding = false;
        }

        // Also stop movement if mouse leaves the collider while holding
        // void OnMouseExit()
        // {
        //     isHolding = false;
        // }
    }
}


/*
using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Handles click and drag interaction for C-Arm control arrows.
    /// Automatically calculates drag direction based on arrow orientation markers.
    /// Converts mouse movement along arrow direction to C-Arm position/rotation adjustments.
    /// </summary>
    public class C_ArmDragControl : MonoBehaviour
    {
        [Header("Arrow Configuration")]
        [SerializeField] private string controlType; // "UpDown", "InOut", "WigWag", "Tilt", "Orbit"
        [SerializeField] private bool isRotationControl = false; // true for rotations, false for translations

        [Header("Arrow Direction Markers (Empty GameObjects)")]
        [SerializeField] private GameObject startMarker; // Base of arrow
        [SerializeField] private GameObject endMarker;   // Pointy end of arrow

        [Header("Sensitivity Settings")]
        [SerializeField] private float dragSensitivity = 1f;
        [SerializeField] private float scalingFactor = 1f;

        private bool isDragging = false;
        private Vector3 lastMousePosition;
        private Vector2 calculatedDragDirection;

        void Start()
        {
            CalculateDragDirection();
        }

        void CalculateDragDirection()
        {
            if (startMarker != null && endMarker != null && Camera.main != null)
            {
                // Convert world positions to screen positions
                Vector3 startScreen = Camera.main.WorldToScreenPoint(startMarker.transform.position);
                Vector3 endScreen = Camera.main.WorldToScreenPoint(endMarker.transform.position);

                // Calculate direction vector in screen space (from start to end)
                Vector2 screenDirection = (endScreen - startScreen).normalized;
                calculatedDragDirection = screenDirection;

                Debug.Log($"{controlType} arrow drag direction calculated: {calculatedDragDirection}");
            }
            else
            {
                Debug.LogWarning($"{controlType} arrow missing markers or camera - using default up direction");
                calculatedDragDirection = Vector2.up;
            }
        }

        void OnMouseDown()
        {
            if (C_ArmPositioner.ME != null)
            {
                isDragging = true;
                lastMousePosition = Input.mousePosition;

                // Recalculate drag direction in case camera moved
                CalculateDragDirection();
            }
        }

        void OnMouseDrag()
        {
            if (!isDragging || C_ArmPositioner.ME == null) return;

            Vector3 currentMousePosition = Input.mousePosition;
            Vector3 mouseDelta = currentMousePosition - lastMousePosition;

            // Calculate movement along the arrow's calculated drag direction
            Vector2 mouseDelta2D = new Vector2(mouseDelta.x, mouseDelta.y);
            float movementAmount = Vector2.Dot(mouseDelta2D, calculatedDragDirection) * dragSensitivity * scalingFactor;

            // Send movement to C_ArmPositioner
            C_ArmPositioner.ME.UpdatePosition(controlType, movementAmount);

            lastMousePosition = currentMousePosition;
        }

        void OnMouseUp()
        {
            isDragging = false;
        }
    }
}
*/
